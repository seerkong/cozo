using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tools;

public sealed record BusinessUseCaseSliceBuildRequest(
    string OntologyId,
    IReadOnlyList<BusinessOntologyCorroborationObservation>? Corroborations = null);

public sealed record BusinessUseCaseSliceBuildResult(
    IReadOnlyList<BusinessUseCaseSlice> Slices,
    IReadOnlyList<BusinessUseCaseSliceBuildDiagnostic> Diagnostics);

public sealed record BusinessUseCaseSlice(
    string Id,
    string EntrySymbolId,
    string RouteKind,
    string Action,
    IReadOnlyList<string> SymbolIds,
    IReadOnlyList<string> FileIds,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> ClaimIds,
    IReadOnlyList<string> ConceptIds,
    IReadOnlyList<string> EvidenceIds,
    BusinessUseCaseSliceTruncationDiagnostics Diagnostics);

public sealed record BusinessUseCaseSliceTruncationDiagnostics(
    bool TruncatedByDepth,
    bool TruncatedBySymbols,
    bool TruncatedByClaims,
    int MaxDepth,
    int MaxSymbols,
    int MaxClaims);

public sealed record BusinessUseCaseSliceBuildDiagnostic(
    string Id,
    string Kind,
    string SubjectId,
    string Message);

public static class BusinessUseCaseSliceDiagnosticKinds
{
    public const string DroppedRouteWithoutBehaviorAnchor = "dropped_route_without_behavior_anchor";
    public const string DroppedRouteWithoutCrossFilePath = "dropped_route_without_cross_file_path";
}

/// <summary>
/// Builds bounded business use-case source-evidence slices from entry-point rooted CodeKnowledge.
/// The slices are evidence packs only: this component never creates concepts, rules, or lifecycles.
/// </summary>
public sealed class BusinessUseCaseSliceBuilder(CozoOm om, BusinessOntologyStore store)
{
    public const int MaxDepth = 4;
    public const int MaxSymbols = 32;
    public const int MaxClaims = 24;

    public async Task<BusinessUseCaseSliceBuildResult> BuildAsync(
        BusinessUseCaseSliceBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await store.ReadExportableAsync(request.OntologyId, cancellationToken);
        var graph = await ReadGraphAsync(cancellationToken);
        var conceptBySymbol = UniqueConceptMappings(current.Mappings, current.Concepts.Select(item => item.Id));
        var evidenceByConcept = current.Concepts.ToDictionary(item => item.Id, item => item.EvidenceIds, StringComparer.Ordinal);
        var corroborations = (request.Corroborations ?? [])
            .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ToArray();
        var slices = new List<BusinessUseCaseSlice>();
        var diagnostics = new List<BusinessUseCaseSliceBuildDiagnostic>();

        foreach (var entry in graph.EntryPoints
            .Where(item => item.Kind == "http_route")
            .OrderBy(item => item.SymbolId, StringComparer.Ordinal))
        {
            if (!graph.Symbols.ContainsKey(entry.SymbolId))
            {
                continue;
            }

            var walk = Walk(entry.SymbolId, graph);
            var files = walk.SymbolIds
                .Select(id => graph.Symbols[id].FileId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var ownedClaims = ClaimsForSlice(walk.SymbolIds, graph).ToArray();
            var claims = ownedClaims.Take(MaxClaims).ToArray();
            var truncatedByClaims = ownedClaims.Length > MaxClaims;
            var roles = RolesForSlice(walk.SymbolIds, graph)
                .ToArray();
            var conceptIds = ConceptsForSlice(walk.SymbolIds, claims, graph, conceptBySymbol)
                .ToArray();
            var hasRepositoryReadWrite = HasServiceRepositoryReadWrite(walk.SymbolIds, graph, roles);
            var hasDirectSemanticAnchor = claims.Any(IsBehaviorAnchorClaim);
            if (!hasRepositoryReadWrite && !hasDirectSemanticAnchor)
            {
                diagnostics.Add(Diagnostic(
                    BusinessUseCaseSliceDiagnosticKinds.DroppedRouteWithoutBehaviorAnchor,
                    entry.SymbolId,
                    "入口缺少 service/repository 读写或直接状态/类型语义锚点，未产出 use-case slice。"));
                continue;
            }
            if (files.Length < 2)
            {
                diagnostics.Add(Diagnostic(
                    BusinessUseCaseSliceDiagnosticKinds.DroppedRouteWithoutCrossFilePath,
                    entry.SymbolId,
                    "入口未形成跨文件调用路径，未产出 use-case slice。"));
                continue;
            }
            if (conceptIds.Length == 0)
            {
                diagnostics.Add(Diagnostic(
                    BusinessUseCaseSliceDiagnosticKinds.DroppedRouteWithoutBehaviorAnchor,
                    entry.SymbolId,
                    "入口未触达当前 ontology canonical concept mapping，未产出 use-case slice。"));
                continue;
            }

            var evidenceIds = claims.Select(item => item.ClaimId)
                .Concat(conceptIds.SelectMany(id => evidenceByConcept.GetValueOrDefault(id, [])))
                .Concat(MatchingCorroborations(entry, conceptIds, current, corroborations).Select(item => item.EvidenceId))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var claimIds = claims.Select(item => item.ClaimId)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var orderedConceptIds = conceptIds.Order(StringComparer.Ordinal).ToArray();
            slices.Add(new BusinessUseCaseSlice(
                StableSliceId(entry, orderedConceptIds, claimIds),
                entry.SymbolId,
                entry.Kind,
                ActionFor(entry, graph),
                walk.SymbolIds,
                files,
                roles,
                claimIds,
                orderedConceptIds,
                evidenceIds,
                new BusinessUseCaseSliceTruncationDiagnostics(
                    walk.TruncatedByDepth,
                    walk.TruncatedBySymbols,
                    truncatedByClaims,
                    MaxDepth,
                    MaxSymbols,
                    MaxClaims)));
        }

        return new BusinessUseCaseSliceBuildResult(
            slices.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            diagnostics.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
    }

    private static WalkResult Walk(string root, GraphFacts graph)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { root };
        var ordered = new List<string> { root };
        var queue = new Queue<(string SymbolId, int Depth)>();
        queue.Enqueue((root, 0));
        var truncatedByDepth = false;
        var truncatedBySymbols = false;

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            var outgoing = WalkOutgoing(current, graph);
            if (depth >= MaxDepth)
            {
                truncatedByDepth |= outgoing.Count > 0;
                continue;
            }

            foreach (var edge in outgoing)
            {
                if (visited.Contains(edge.ToId))
                {
                    continue;
                }
                if (!graph.Symbols.ContainsKey(edge.ToId))
                {
                    continue;
                }
                if (ordered.Count >= MaxSymbols)
                {
                    truncatedBySymbols = true;
                    break;
                }

                visited.Add(edge.ToId);
                ordered.Add(edge.ToId);
                queue.Enqueue((edge.ToId, depth + 1));
            }
        }

        return new WalkResult(ordered, truncatedByDepth, truncatedBySymbols);
    }

    private static IReadOnlyList<EdgeFact> WalkOutgoing(string symbolId, GraphFacts graph) =>
        graph.CallsFrom.GetValueOrDefault(symbolId, [])
            .Concat(graph.ImplementationsByInterfaceMethod.GetValueOrDefault(symbolId, []))
            .OrderBy(item => item.ToId, StringComparer.Ordinal)
            .ThenBy(item => item.Line)
            .ThenBy(item => item.FromId, StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<string> RolesForSlice(IReadOnlyList<string> symbols, GraphFacts graph)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbolId in symbols)
        {
            foreach (var owner in SelfAndParents(symbolId, graph.Symbols))
            {
                foreach (var role in graph.RolesBySymbol.GetValueOrDefault(owner, []))
                {
                    if (seen.Add(role))
                    {
                        yield return role;
                    }
                }
            }
        }
    }

    private static bool HasServiceRepositoryReadWrite(
        IReadOnlyList<string> symbols,
        GraphFacts graph,
        IReadOnlyList<string> roles)
    {
        var hasService = roles.Any(role => role.Contains("service", StringComparison.OrdinalIgnoreCase));
        var hasRepository = roles.Any(role => role.Contains("repository", StringComparison.OrdinalIgnoreCase));
        var symbolSet = symbols.ToHashSet(StringComparer.Ordinal);
        var hasReadWriteAccess = graph.AllEdges.Any(edge =>
            symbolSet.Contains(edge.FromId)
            && symbolSet.Contains(edge.ToId)
            && edge.Kind == CodeEdgeKinds.Accesses
            && (edge.Evidence.Contains("read", StringComparison.OrdinalIgnoreCase)
                || edge.Evidence.Contains("write", StringComparison.OrdinalIgnoreCase)));
        return hasService && (hasRepository || hasReadWriteAccess);
    }

    private static IEnumerable<ClaimFact> ClaimsForSlice(
        IReadOnlyList<string> symbols,
        GraphFacts graph)
    {
        var symbolSet = symbols.ToHashSet(StringComparer.Ordinal);
        return graph.Claims
            .Where(claim => symbolSet.Contains(claim.SubjectId)
                || ExplicitOwningMethodIds(claim, graph.Symbols)
                    .Any(symbolSet.Contains))
            .Where(IsBehaviorAnchorClaim)
            .OrderBy(ClaimBudgetPriority)
            .ThenBy(item => item.FileId, StringComparer.Ordinal)
            .ThenBy(item => item.StartLine)
            .ThenBy(item => item.EndLine)
            .ThenBy(item => item.ClaimId, StringComparer.Ordinal);
    }

    private static int ClaimBudgetPriority(ClaimFact claim) =>
        claim.Kind switch
        {
            CodeSemanticClaimKinds.BusinessGuard => 0,
            CodeSemanticClaimKinds.StateAssignment => 0,
            CodeSemanticClaimKinds.TypedReference => 1,
            CodeSemanticClaimKinds.StateField => 2,
            CodeSemanticClaimKinds.StateValue => 3,
            _ => 4,
        };

    private static IEnumerable<string> ExplicitOwningMethodIds(
        ClaimFact claim,
        IReadOnlyDictionary<string, SymbolFact> symbols)
    {
        using var document = JsonDocument.Parse(claim.PayloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }
        foreach (var property in new[] { "methodSymbolId", "declaringSymbolId", "ownerSymbolId" })
        {
            if (!document.RootElement.TryGetProperty(property, out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            {
                continue;
            }
            var symbolId = value.GetString()!;
            if (symbols.TryGetValue(symbolId, out var symbol)
                && symbol.Kind == "method")
            {
                yield return symbolId;
            }
        }
    }

    private static IEnumerable<string> ConceptsForSlice(
        IReadOnlyList<string> symbols,
        IReadOnlyList<ClaimFact> claims,
        GraphFacts graph,
        IReadOnlyDictionary<string, string> conceptBySymbol)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            foreach (var owner in SelfAndParents(symbol, graph.Symbols))
            {
                if (conceptBySymbol.TryGetValue(owner, out var conceptId) && seen.Add(conceptId))
                {
                    yield return conceptId;
                }
            }
        }
        foreach (var claim in claims)
        {
            foreach (var symbol in ClaimSymbolRefs(claim))
            {
                if (conceptBySymbol.TryGetValue(symbol, out var conceptId) && seen.Add(conceptId))
                {
                    yield return conceptId;
                }
            }
        }
    }

    private static IReadOnlyList<BusinessOntologyCorroborationObservation> MatchingCorroborations(
        EntryPointFact entry,
        IReadOnlyCollection<string> conceptIds,
        BusinessOntologySnapshot snapshot,
        IReadOnlyList<BusinessOntologyCorroborationObservation> observations)
    {
        if (observations.Count == 0)
        {
            return [];
        }

        var actionRoute = NormalizeRouteAnchor(RouteOnly(ActionFor(entry, null)));
        var tokens = conceptIds
            .SelectMany(id =>
            {
                var concept = snapshot.Concepts.FirstOrDefault(item => item.Id == id);
                return new[] { id, concept?.Label ?? "" };
            })
            .Select(NormalizeDomainToken)
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var typeAnchors = snapshot.Mappings
            .Where(item => item.SubjectKind == "concept" && conceptIds.Contains(item.SubjectId))
            .Select(item => NormalizeTypeAnchor(item.Symbol))
            .ToHashSet(StringComparer.Ordinal);

        return observations
            .Where(item => IsValidCorroboration(item)
                && tokens.Contains(NormalizeDomainToken(item.DomainToken))
                && (item.AnchorKind == "route"
                    && NormalizeRouteAnchor(item.AnchorValue) == actionRoute
                    || item.AnchorKind == "type"
                    && typeAnchors.Contains(NormalizeTypeAnchor(item.AnchorValue))))
            .ToArray();
    }

    private static bool IsValidCorroboration(BusinessOntologyCorroborationObservation item) =>
        BusinessOntologyCorroborationKinds.All.Contains(item.SourceKind)
        && item.AnchorKind is "route" or "type"
        && !string.IsNullOrWhiteSpace(item.EvidenceId)
        && !string.IsNullOrWhiteSpace(item.DomainToken)
        && !string.IsNullOrWhiteSpace(item.AnchorValue);

    private static bool IsBehaviorAnchorClaim(ClaimFact claim) =>
        claim.Kind is CodeSemanticClaimKinds.TypedReference
            or CodeSemanticClaimKinds.StateField
            or CodeSemanticClaimKinds.StateValue
            or CodeSemanticClaimKinds.StateAssignment
            or CodeSemanticClaimKinds.BusinessGuard;

    private static IEnumerable<string> ClaimSymbolRefs(ClaimFact claim)
    {
        using var document = JsonDocument.Parse(claim.PayloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in new[]
        {
            "ownerSymbolId", "resolvedTypeSymbolId", "enumTypeSymbolId", "subjectSymbolId",
            "methodSymbolId",
        })
        {
            if (document.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                yield return value.GetString()!;
            }
        }
    }

    private static IEnumerable<string> SelfAndParents(
        string symbolId,
        IReadOnlyDictionary<string, SymbolFact> symbols)
    {
        var current = symbolId;
        var guard = 0;
        while (current.Length > 0 && guard++ < 16 && symbols.TryGetValue(current, out var symbol))
        {
            yield return current;
            current = symbol.ParentId;
        }
    }

    private static string ActionFor(EntryPointFact entry, GraphFacts? graph)
    {
        var metadata = entry.Metadata.Trim();
        if (metadata.Length == 0)
        {
            return graph?.Symbols.GetValueOrDefault(entry.SymbolId)?.Name ?? entry.SymbolId;
        }
        if (metadata.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(metadata);
                var root = document.RootElement;
                var method = String(root, "method").ToUpperInvariant();
                var path = String(root, "path");
                if (path.Length == 0
                    && root.TryGetProperty("paths", out var paths)
                    && paths.ValueKind == JsonValueKind.Array)
                {
                    path = paths.EnumerateArray()
                        .FirstOrDefault(item => item.ValueKind == JsonValueKind.String)
                        .GetString() ?? "";
                }
                return (method + " " + path).Trim();
            }
            catch (JsonException)
            {
            }
        }
        return metadata;
    }

    private static string RouteOnly(string action)
    {
        var parts = action.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^1] : action;
    }

    private static BusinessUseCaseSliceBuildDiagnostic Diagnostic(
        string kind,
        string subjectId,
        string message) =>
        new("diagnostic:usecase-slice:" + Digest(kind + "\n" + subjectId), kind, subjectId, message);

    private static string StableSliceId(
        EntryPointFact entry,
        IReadOnlyList<string> conceptIds,
        IReadOnlyList<string> claimIds) =>
        "usecase:slice:" + Digest(JsonSerializer.Serialize(new
        {
            entry.SymbolId,
            entry.Kind,
            action = ActionFor(entry, null),
            conceptIds,
            claimIds,
        }));

    private static IReadOnlyDictionary<string, string> UniqueConceptMappings(
        IReadOnlyList<BusinessOntologyMapping> mappings,
        IEnumerable<string> conceptIds)
    {
        var concepts = conceptIds.ToHashSet(StringComparer.Ordinal);
        return mappings
            .Where(item => item.SubjectKind == "concept"
                && concepts.Contains(item.SubjectId)
                && !string.IsNullOrWhiteSpace(item.Symbol))
            .GroupBy(item => item.Symbol, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.SubjectId).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().SubjectId, StringComparer.Ordinal);
    }

    private async Task<GraphFacts> ReadGraphAsync(CancellationToken cancellationToken)
    {
        var entries = (await om.Runtime.Store.RunAsync(
            "?[symbol_id, kind, metadata] := *ck_entry_point{symbol_id, kind, metadata}",
            cancellationToken: cancellationToken))
            .Rows.Select(row => new EntryPointFact(S(row, 0), S(row, 1), S(row, 2)))
            .ToArray();
        var symbols = (await om.Runtime.Store.RunAsync(
            "?[symbol_id, file_id, name, kind, start_line, end_line, parent_id] := *ck_symbol{symbol_id, file_id, name, kind, start_line, end_line, parent_id}",
            cancellationToken: cancellationToken))
            .Rows.Select(row => new SymbolFact(S(row, 0), S(row, 1), S(row, 2), S(row, 3), I(row, 4), I(row, 5), S(row, 6)))
            .ToDictionary(item => item.SymbolId, StringComparer.Ordinal);
        var files = (await om.Runtime.Store.RunAsync(
            "?[file_id, repo_id, path] := *ck_file{file_id, repo_id, path}",
            cancellationToken: cancellationToken))
            .Rows.Select(row => new FileFact(S(row, 0), S(row, 1), S(row, 2)))
            .ToDictionary(item => item.FileId, StringComparer.Ordinal);
        var edges = (await om.Runtime.Store.RunAsync(
            "?[from_id, to_id, kind, file_id, line, confidence, resolver, evidence] := *ck_edge{from_id, to_id, kind, file_id, line, confidence, resolver, evidence}",
            cancellationToken: cancellationToken))
            .Rows.Select(row => new EdgeFact(S(row, 0), S(row, 1), S(row, 2), S(row, 3), I(row, 4), D(row, 5), S(row, 6), S(row, 7)))
            .OrderBy(item => item.ToId, StringComparer.Ordinal)
            .ThenBy(item => item.Line)
            .ThenBy(item => item.FromId, StringComparer.Ordinal)
            .ToArray();
        var roles = edges
            .Where(item => item.Kind == "SPRING_ROLE")
            .GroupBy(item => item.FromId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(item => item.ToId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var calls = edges
            .Where(item => item.Kind == CodeEdgeKinds.Calls && !IsLowConfidenceAmbiguousCall(item))
            .GroupBy(item => item.FromId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<EdgeFact>)group.ToArray(),
                StringComparer.Ordinal);
        var implementationsByInterfaceMethod = edges
            .Where(item => item.Kind == CodeEdgeKinds.MethodImplements)
            .GroupBy(item => item.ToId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<EdgeFact>)group
                    .Select(item => item with { ToId = item.FromId, FromId = item.ToId })
                    .OrderBy(item => item.ToId, StringComparer.Ordinal)
                    .ThenBy(item => item.Line)
                    .ThenBy(item => item.FromId, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var claims = (await om.Runtime.Store.RunAsync(
            """
            ?[claim_id, subject_id, kind, payload_json, file_id, start_line, end_line, confidence, resolver, evidence] :=
                *ck_semantic_claim{claim_id, subject_id, kind, payload_json, file_id, start_line, end_line, confidence, resolver, evidence}
            """,
            cancellationToken: cancellationToken))
            .Rows.Select(row => new ClaimFact(S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), I(row, 5), I(row, 6), D(row, 7), S(row, 8), S(row, 9)))
            .OrderBy(item => item.ClaimId, StringComparer.Ordinal)
            .ToArray();
        return new GraphFacts(entries, symbols, files, edges, roles, calls, implementationsByInterfaceMethod, claims);
    }

    private static bool IsLowConfidenceAmbiguousCall(EdgeFact edge) =>
        edge.Confidence <= 0.5
        && edge.Evidence.Contains("ambiguous:", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDomainToken(string value)
    {
        var terminal = value.Split('.').LastOrDefault() ?? value;
        var normalized = new string(terminal.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        foreach (var suffix in new[] { "controller", "service", "repository", "entity", "dto", "page", "form" })
        {
            if (normalized.Length > suffix.Length && normalized.EndsWith(suffix, StringComparison.Ordinal))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }
        return normalized.EndsWith('s') && normalized.Length > 3 ? normalized[..^1] : normalized;
    }

    private static string NormalizeTypeAnchor(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string NormalizeRouteAnchor(string value) =>
        value.Trim().TrimEnd('/').ToLowerInvariant();

    private static string String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string S(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.String ? row[index].GetString() ?? "" : row[index].ToString();

    private static int I(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.Number ? row[index].GetInt32() : 0;

    private static double D(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.Number ? row[index].GetDouble() : 0;

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..24];

    private sealed record GraphFacts(
        IReadOnlyList<EntryPointFact> EntryPoints,
        IReadOnlyDictionary<string, SymbolFact> Symbols,
        IReadOnlyDictionary<string, FileFact> Files,
        IReadOnlyList<EdgeFact> AllEdges,
        IReadOnlyDictionary<string, IReadOnlyList<string>> RolesBySymbol,
        IReadOnlyDictionary<string, IReadOnlyList<EdgeFact>> CallsFrom,
        IReadOnlyDictionary<string, IReadOnlyList<EdgeFact>> ImplementationsByInterfaceMethod,
        IReadOnlyList<ClaimFact> Claims);

    private sealed record EntryPointFact(string SymbolId, string Kind, string Metadata);
    private sealed record SymbolFact(string SymbolId, string FileId, string Name, string Kind, int StartLine, int EndLine, string ParentId);
    private sealed record FileFact(string FileId, string RepoId, string Path);
    private sealed record EdgeFact(string FromId, string ToId, string Kind, string FileId, int Line, double Confidence, string Resolver, string Evidence);
    private sealed record ClaimFact(string ClaimId, string SubjectId, string Kind, string PayloadJson, string FileId, int StartLine, int EndLine, double Confidence, string Resolver, string Evidence);
    private sealed record WalkResult(IReadOnlyList<string> SymbolIds, bool TruncatedByDepth, bool TruncatedBySymbols);
}
