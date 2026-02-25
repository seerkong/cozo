using System.Text.RegularExpressions;
using Cozo.DotNet.LlmWiki.SemanticParsing;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Indexing;

/// <summary>
/// Registry-based call resolution over tree-sitter facts (call-resolution track, design.md §2–§5).
/// Turns ParsedCallSite lists into CALLS/ACCESSES edges and post-processes EXTENDS/IMPLEMENTS
/// into METHOD_OVERRIDES/METHOD_IMPLEMENTS. Confidence grading (mission D1/A1): 0.9 unique
/// precise, 0.7 repo/import name+arity unique, 0.5 ambiguous (evidence "ambiguous:&lt;n&gt;");
/// no candidate → counted, never edged. Internal by design (minimal public surface): consumed
/// only by RepositoryIndexer; results surface through CodeEdgeFact rows and summary counters.
/// </summary>
internal static class CallResolver
{
    /// <summary>One tree-sitter-parsed file plus the ids/context the resolver needs.</summary>
    internal sealed record FileInput(
        string FileId,
        string RelativePath,
        string Language,
        string SourceText,
        ParsedFileResult Parsed);

    /// <summary>Aggregate counters for the index summary (design.md §5, add-only).</summary>
    internal sealed record ResolutionStats(int CallSites, int ResolvedCalls, int UnresolvedCalls);

    private const string Resolver = "treesitter";

    private static readonly HashSet<string> TypeKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "struct", "record", "enum",
    };

    /// <summary>Kinds a call target may have (fallback tiers); keeps namespaces/types/docs out of the ambiguity pool.</summary>
    private static readonly HashSet<string> CallableKinds = new(StringComparer.Ordinal)
    {
        "method", "function", "constructor", "delegate", "constant", "variable",
    };

    // Local binding table (design.md §2): text-level, single file, best effort.
    // `x = new T(...)` — covers var/const/let/field initializers; the type group stops before `<`.
    private static readonly Regex NewBindingPattern = new(
        @"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\s+([A-Za-z_][A-Za-z0-9_.]*)",
        RegexOptions.Compiled);

    // C# declared-type bindings: `OrderRepo repo ...` / `private readonly Repo _repo;` — the
    // receiver must start lowercase/underscore so method declarations don't bind.
    private static readonly Regex CsTypedBindingPattern = new(
        @"\b([A-Z][A-Za-z0-9_]*)(?:<[^>\r\n]*>)?\s+([_a-z][A-Za-z0-9_]*)\s*(?=;|=[^=>]|\{)",
        RegexOptions.Compiled);

    // TS/JS type annotations: `value: Counter` (uppercase-initial types only — skips primitives).
    private static readonly Regex TsTypedBindingPattern = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\s*:\s*([A-Z][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex IdentifierPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Registry entry: one tree-sitter symbol fact, sym_key split into qualified name + arity.</summary>
    private sealed record Entry(
        string Id,
        string FileId,
        string Name,
        string Kind,
        string Qualified,
        int Arity,
        bool HasArity,
        string Visibility,
        string ParentId,
        int StartLine,
        int EndLine);

    private sealed record Resolution(Entry Target, double Confidence, string Evidence);

    /// <summary>
    /// Resolves every call site of <paramref name="files"/> against the symbol facts already
    /// generated for this batch, appending CALLS/ACCESSES/METHOD_OVERRIDES/METHOD_IMPLEMENTS
    /// edges and per-file diagnostics in place.
    /// </summary>
    internal static ResolutionStats Resolve(
        IReadOnlyList<FileInput> files,
        IReadOnlyList<CodeSymbolFact> symbols,
        List<CodeEdgeFact> edges,
        List<CodeDiagnosticFact> diagnostics,
        bool emitAccessEdges,
        int maxAccessEdgesPerFile)
    {
        if (files.Count == 0)
        {
            return new ResolutionStats(0, 0, 0);
        }

        // ---- registry (design.md §2): byQualified / byName(+arity via filtering) --------------
        var entries = new List<Entry>();
        foreach (var symbol in symbols)
        {
            if (symbol.Resolver != Resolver || string.IsNullOrEmpty(symbol.SymKey))
            {
                continue;
            }

            var colon = symbol.SymKey.IndexOf(':');
            var hash = symbol.SymKey.LastIndexOf('#');
            if (colon < 0 || hash <= colon)
            {
                continue;
            }

            var hasArity = int.TryParse(symbol.SymKey[(hash + 1)..], out var arity);
            entries.Add(new Entry(
                symbol.SymbolId,
                symbol.FileId,
                symbol.Name,
                symbol.Kind,
                symbol.SymKey[(colon + 1)..hash],
                arity,
                hasArity,
                symbol.Visibility,
                symbol.ParentId,
                symbol.StartLine,
                symbol.EndLine));
        }

        var byId = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var byQualified = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        var byName = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        var byFileQualified = new Dictionary<(string FileId, string Qualified), List<Entry>>();
        var childrenByParent = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            byId.TryAdd(entry.Id, entry);
            Append(byQualified, entry.Qualified, entry);
            Append(byName, entry.Name, entry);
            Append(byFileQualified, (entry.FileId, entry.Qualified), entry);
            if (entry.ParentId.Length > 0)
            {
                Append(childrenByParent, entry.ParentId, entry);
            }
        }

        List<Entry> TypesByName(string name) =>
            byName.TryGetValue(name, out var list)
                ? list.Where(e => TypeKinds.Contains(e.Kind)).ToList()
                : [];

        // EXTENDS targets are file-local symbol ids or `typeref:{lang}:{name}` placeholders
        // (RepositoryIndexer.AppendTreeSitterFacts); resolve the latter when repo-unique.
        Entry? ResolveTypeTarget(string toId)
        {
            if (byId.TryGetValue(toId, out var direct))
            {
                return TypeKinds.Contains(direct.Kind) ? direct : null;
            }

            if (!toId.StartsWith("typeref:", StringComparison.Ordinal))
            {
                return null;
            }

            var name = toId[(toId.LastIndexOf(':') + 1)..];
            var types = TypesByName(name);
            return types.Count == 1 ? types[0] : null;
        }

        // Snapshot before we append: the structural EXTENDS/IMPLEMENTS edges drive both the
        // this-tier chain walk and the OVERRIDES/IMPLEMENTS post-process (design.md §4).
        var structuralEdges = edges.ToArray();
        var baseTypeOf = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var edge in structuralEdges)
        {
            if (edge.Kind != CodeEdgeKinds.Extends || !byId.TryGetValue(edge.FromId, out var from) || !TypeKinds.Contains(from.Kind))
            {
                continue;
            }

            if (ResolveTypeTarget(edge.ToId) is { } target && target.Id != from.Id)
            {
                baseTypeOf.TryAdd(from.Id, target);
            }
        }

        IEnumerable<Entry> ExtendsChain(Entry type)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var current = type; current is not null && visited.Add(current.Id);)
            {
                yield return current;
                current = baseTypeOf.GetValueOrDefault(current.Id);
            }
        }

        Entry? FindMember(Entry type, string name, int arity)
        {
            foreach (var link in ExtendsChain(type))
            {
                var members = childrenByParent.TryGetValue(link.Id, out var kids)
                    ? kids.Where(m => m.Name == name).ToList()
                    : [];
                if (members.Count > 0)
                {
                    return members.FirstOrDefault(m => m.Arity == arity) ?? members[0];
                }
            }

            return null;
        }

        Entry? ContainerType(Entry? caller)
        {
            var guard = 0;
            for (var current = caller; current is not null && guard++ < 32;)
            {
                if (TypeKinds.Contains(current.Kind))
                {
                    return current;
                }

                current = current.ParentId.Length > 0 ? byId.GetValueOrDefault(current.ParentId) : null;
            }

            return null;
        }

        Entry? PickType(List<Entry> types, string fileId)
        {
            if (types.Count == 1)
            {
                return types[0];
            }

            var local = types.Where(t => t.FileId == fileId).ToList();
            return local.Count == 1 ? local[0] : null;
        }

        Entry ConstructorOrType(Entry type, int arity)
        {
            var constructors = childrenByParent.TryGetValue(type.Id, out var kids)
                ? kids.Where(m => m.Kind == "constructor").ToList()
                : [];
            return constructors.Count > 0
                ? constructors.FirstOrDefault(c => c.Arity == arity) ?? constructors[0]
                : type;
        }

        static Entry FirstDeterministic(List<Entry> candidates) =>
            candidates.OrderBy(e => e.Qualified, StringComparer.Ordinal).ThenBy(e => e.FileId, StringComparer.Ordinal).First();

        var fileIdByPath = files.ToDictionary(f => NormalizeSlashes(f.RelativePath), f => f.FileId, StringComparer.Ordinal);

        // Import-constrained fallback scope (design.md §3): C# `using` = namespace prefix;
        // TS/JS relative specifier = entries of the resolved file (index/extension tolerant).
        HashSet<string> BuildImportScope(FileInput file)
        {
            var scope = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in file.Parsed.Edges)
            {
                if (edge.Kind != CodeEdgeKinds.Imports)
                {
                    continue;
                }

                if (file.Language == "csharp")
                {
                    var prefix = edge.ToName + ".";
                    foreach (var entry in entries)
                    {
                        if (entry.Qualified.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            scope.Add(entry.Id);
                        }
                    }
                }
                else if (edge.ToName.StartsWith(".", StringComparison.Ordinal))
                {
                    var resolved = ResolveRelativePath(file.RelativePath, edge.ToName);
                    foreach (var candidate in new[] { resolved + ".ts", resolved + ".tsx", resolved + ".js", resolved + ".jsx", resolved + "/index.ts", resolved + "/index.js" })
                    {
                        if (!fileIdByPath.TryGetValue(candidate, out var importedFileId))
                        {
                            continue;
                        }

                        foreach (var entry in entries)
                        {
                            if (entry.FileId == importedFileId)
                            {
                                scope.Add(entry.Id);
                            }
                        }
                    }
                }
            }

            return scope;
        }

        // ---- resolution pipeline (design.md §3, tier order is the spec) -----------------------
        Resolution? ResolveNew(ParsedCallSite site)
        {
            var name = site.TargetName;
            List<Entry> types;
            if (name.Contains('.'))
            {
                types = byQualified.TryGetValue(name, out var qualifiedHits)
                    ? qualifiedHits.Where(e => TypeKinds.Contains(e.Kind)).ToList()
                    : [];
                if (types.Count == 0)
                {
                    types = TypesByName(name[(name.LastIndexOf('.') + 1)..]);
                }
            }
            else
            {
                types = TypesByName(name);
            }

            if (types.Count == 0)
            {
                return null;
            }

            if (types.Count > 1)
            {
                return new Resolution(ConstructorOrType(FirstDeterministic(types), site.Arity), 0.5, $"ambiguous:{types.Count}");
            }

            return new Resolution(ConstructorOrType(types[0], site.Arity), 0.9, "new");
        }

        Resolution? ResolveCall(FileInput file, ParsedCallSite site, Entry? caller, Dictionary<string, string> bindings, Lazy<HashSet<string>> importScope)
        {
            var receiver = site.ReceiverText;

            // this / receiver-less: container type members, walking the EXTENDS chain (0.9).
            if (receiver is null or "this" or "base")
            {
                if (ContainerType(caller) is { } container && FindMember(container, site.TargetName, site.Arity) is { } member)
                {
                    return new Resolution(member, 0.9, "member");
                }
            }
            else if (IdentifierPattern.IsMatch(receiver))
            {
                // receiver bound locally (`var x = new T()` / declared field type) → T's members (0.9 unique).
                if (bindings.TryGetValue(receiver, out var boundType)
                    && PickType(TypesByName(boundType), file.FileId) is { } bound
                    && FindMember(bound, site.TargetName, site.Arity) is { } boundMember)
                {
                    return new Resolution(boundMember, boundMember.Arity == site.Arity ? 0.9 : 0.7, $"binding:{boundType}");
                }

                // receiver is a repo-unique type name (static call) → its members (0.9 exact / 0.7 name-only).
                var receiverTypes = TypesByName(receiver);
                if (receiverTypes.Count == 1 && FindMember(receiverTypes[0], site.TargetName, site.Arity) is { } staticMember)
                {
                    return new Resolution(staticMember, staticMember.Arity == site.Arity ? 0.9 : 0.7, "static-type");
                }
            }

            // fallback tiers: import-constrained unique → repo-unique (0.7) → ambiguous (0.5) → none.
            // Candidate hygiene (refine-depa-detection-precision T1.1): cross-file private/protected
            // members are not legal bare-name targets, and a known call-site arity must match a
            // parseable candidate arity (entries without arity info are kept — TS etc.). An emptied
            // pool means unresolved: never degrade onto an illegal candidate.
            var nameCandidates = byName.TryGetValue(site.TargetName, out var named)
                ? named.Where(e => CallableKinds.Contains(e.Kind)
                    && (e.FileId == file.FileId || !IsCrossFileRestricted(e))
                    && (site.Arity < 0 || !e.HasArity || e.Arity == site.Arity)).ToList()
                : [];
            if (nameCandidates.Count == 0)
            {
                return null;
            }

            var scope = importScope.Value;
            if (scope.Count > 0)
            {
                var imported = nameCandidates.Where(e => scope.Contains(e.Id)).ToList();
                if (imported.Count == 1)
                {
                    return new Resolution(imported[0], 0.7, "import");
                }
            }

            if (nameCandidates.Count == 1)
            {
                return new Resolution(nameCandidates[0], 0.7, "repo-unique");
            }

            return new Resolution(FirstDeterministic(nameCandidates), 0.5, $"ambiguous:{nameCandidates.Count}");
        }

        // ACCESSES (design.md §3): only when the receiver resolves to an in-repo type.
        Entry? ResolveAccessTarget(FileInput file, ParsedCallSite site, Entry? caller, Dictionary<string, string> bindings)
        {
            Entry? type = null;
            var receiver = site.ReceiverText;
            if (receiver is null or "this" or "base")
            {
                type = ContainerType(caller);
            }
            else if (IdentifierPattern.IsMatch(receiver))
            {
                if (bindings.TryGetValue(receiver, out var boundType))
                {
                    type = PickType(TypesByName(boundType), file.FileId);
                }

                if (type is null)
                {
                    var receiverTypes = TypesByName(receiver);
                    type = receiverTypes.Count == 1 ? receiverTypes[0] : null;
                }
            }

            return type is null ? null : FindMember(type, site.TargetName, 0);
        }

        // caller attribution (deepen-llm-wiki-context-impact §1): when the qualified name matches
        // several same-file symbols (overloads), pick the one whose [StartLine, EndLine] interval
        // contains the call-site line; several hits → smallest interval; zero hits → legacy first
        // entry, flagged so the imprecision surfaces in the edge evidence as caller_fallback.
        (Entry? Caller, bool Fallback) ResolveCaller(string fileId, string qualified, int line)
        {
            if (!byFileQualified.TryGetValue((fileId, qualified), out var candidates) || candidates.Count == 0)
            {
                return (null, false);
            }

            if (candidates.Count == 1)
            {
                return (candidates[0], false);
            }

            Entry? best = null;
            foreach (var candidate in candidates)
            {
                if (line < candidate.StartLine || line > candidate.EndLine)
                {
                    continue;
                }

                if (best is null || candidate.EndLine - candidate.StartLine < best.EndLine - best.StartLine)
                {
                    best = candidate;
                }
            }

            return best is not null ? (best, false) : (candidates[0], true);
        }

        var callSites = 0;
        var resolvedCalls = 0;
        var unresolvedCalls = 0;
        var emitted = new HashSet<(string From, string To, string Kind, string FileId, int Line)>();

        foreach (var file in files)
        {
            var bindings = BuildBindings(file);
            var importScope = new Lazy<HashSet<string>>(() => BuildImportScope(file));
            var accessBudget = maxAccessEdgesPerFile;
            var accessTruncated = false;
            var fileUnresolved = 0;

            foreach (var site in file.Parsed.CallSites)
            {
                var (caller, callerFallback) = ResolveCaller(file.FileId, site.CallerQualified, site.Line);
                var fromId = caller?.Id ?? file.FileId;
                var fallbackSuffix = callerFallback ? "; caller_fallback" : "";

                if (site.Kind == "access")
                {
                    if (!emitAccessEdges)
                    {
                        continue;
                    }

                    if (ResolveAccessTarget(file, site, caller, bindings) is not { } accessTarget)
                    {
                        continue;
                    }

                    if (accessBudget <= 0)
                    {
                        accessTruncated = true;
                        continue;
                    }

                    if (emitted.Add((fromId, accessTarget.Id, CodeEdgeKinds.Accesses, file.FileId, site.Line)))
                    {
                        edges.Add(new CodeEdgeFact(
                            fromId,
                            accessTarget.Id,
                            CodeEdgeKinds.Accesses,
                            file.FileId,
                            site.Line,
                            0.9,
                            Resolver,
                            $"{file.RelativePath}: {site.AccessMode ?? "read"}{fallbackSuffix}"));
                        accessBudget--;
                    }

                    continue;
                }

                callSites++;
                var resolution = site.Kind == "new"
                    ? ResolveNew(site)
                    : ResolveCall(file, site, caller, bindings, importScope);
                if (resolution is null)
                {
                    unresolvedCalls++;
                    fileUnresolved++;
                    continue;
                }

                resolvedCalls++;
                if (emitted.Add((fromId, resolution.Target.Id, CodeEdgeKinds.Calls, file.FileId, site.Line)))
                {
                    edges.Add(new CodeEdgeFact(
                        fromId,
                        resolution.Target.Id,
                        CodeEdgeKinds.Calls,
                        file.FileId,
                        site.Line,
                        resolution.Confidence,
                        Resolver,
                        $"{file.RelativePath}: {resolution.Evidence}{fallbackSuffix}"));
                }
            }

            if (accessTruncated)
            {
                diagnostics.Add(new CodeDiagnosticFact(
                    $"diag:accesses-truncated:{file.FileId}",
                    file.FileId,
                    "accesses_truncated",
                    $"per-file ACCESSES budget ({maxAccessEdgesPerFile}) reached; remaining member accesses dropped",
                    "warning"));
            }

            if (fileUnresolved > 0)
            {
                diagnostics.Add(new CodeDiagnosticFact(
                    $"diag:call-unresolved:{file.FileId}",
                    file.FileId,
                    "call_unresolved",
                    $"unresolved call sites: {fileUnresolved}",
                    "info"));
            }
        }

        // ---- METHOD_OVERRIDES / METHOD_IMPLEMENTS post-process (design.md §4) -----------------
        foreach (var edge in structuralEdges)
        {
            var isExtends = edge.Kind == CodeEdgeKinds.Extends;
            if (!isExtends && edge.Kind != CodeEdgeKinds.Implements)
            {
                continue;
            }

            if (!byId.TryGetValue(edge.FromId, out var subType) || !TypeKinds.Contains(subType.Kind))
            {
                continue;
            }

            if (ResolveTypeTarget(edge.ToId) is not { } superType || superType.Id == subType.Id)
            {
                continue;
            }

            var superMembers = childrenByParent.GetValueOrDefault(superType.Id) ?? [];
            foreach (var member in childrenByParent.GetValueOrDefault(subType.Id) ?? [])
            {
                if (member.Kind != "method")
                {
                    continue;
                }

                var match = superMembers.FirstOrDefault(m => m.Kind == "method" && m.Name == member.Name && m.Arity == member.Arity);
                if (match is null)
                {
                    continue;
                }

                var kind = isExtends ? CodeEdgeKinds.MethodOverrides : CodeEdgeKinds.MethodImplements;
                if (emitted.Add((member.Id, match.Id, kind, member.FileId, member.StartLine)))
                {
                    // confidence rides on the inheritance edge (C# base_list is a 0.7 heuristic).
                    edges.Add(new CodeEdgeFact(
                        member.Id,
                        match.Id,
                        kind,
                        member.FileId,
                        member.StartLine,
                        edge.Confidence,
                        Resolver,
                        $"name+arity match via {edge.Kind}"));
                }
            }
        }

        return new ResolutionStats(callSites, resolvedCalls, unresolvedCalls);
    }

    /// <summary>
    /// True when the entry's declared visibility makes it illegal as a cross-file bare-name
    /// candidate: any `private` token, or `protected` without `internal` (`protected internal`
    /// stays repo-visible). Empty visibility is kept — TS module functions etc. carry none.
    /// Visibility is the space-joined modifier list the extractors emit.
    /// </summary>
    private static bool IsCrossFileRestricted(Entry entry)
    {
        if (entry.Visibility.Length == 0)
        {
            return false;
        }

        var tokens = entry.Visibility.Split(' ');
        return tokens.Contains("private", StringComparer.Ordinal)
            || (tokens.Contains("protected", StringComparer.Ordinal) && !tokens.Contains("internal", StringComparer.Ordinal));
    }

    private static Dictionary<string, string> BuildBindings(FileInput file)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in NewBindingPattern.Matches(file.SourceText))
        {
            var type = match.Groups[2].Value;
            bindings[match.Groups[1].Value] = type[(type.LastIndexOf('.') + 1)..];
        }

        var typedPattern = file.Language == "csharp" ? CsTypedBindingPattern : TsTypedBindingPattern;
        foreach (Match match in typedPattern.Matches(file.SourceText))
        {
            var (name, type) = file.Language == "csharp"
                ? (match.Groups[2].Value, match.Groups[1].Value)
                : (match.Groups[1].Value, match.Groups[2].Value);
            bindings.TryAdd(name, type);
        }

        return bindings;
    }

    private static void Append<TKey>(Dictionary<TKey, List<Entry>> map, TKey key, Entry entry)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(entry);
    }

    private static string NormalizeSlashes(string path) => path.Replace('\\', '/');

    /// <summary>Resolves a relative import specifier against the importing file's directory ("a/b.ts" + "./util" → "a/util").</summary>
    private static string ResolveRelativePath(string importerRelativePath, string specifier)
    {
        var normalized = NormalizeSlashes(importerRelativePath);
        var slash = normalized.LastIndexOf('/');
        var segments = new List<string>();
        if (slash > 0)
        {
            segments.AddRange(normalized[..slash].Split('/'));
        }

        foreach (var part in NormalizeSlashes(specifier).Split('/'))
        {
            switch (part)
            {
                case "" or ".":
                    break;
                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    break;
                default:
                    segments.Add(part);
                    break;
            }
        }

        return string.Join('/', segments);
    }
}
