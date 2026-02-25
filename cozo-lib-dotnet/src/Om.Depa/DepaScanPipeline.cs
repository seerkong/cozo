using System.Text.Json;

namespace Cozo.DotNet.Om.Depa;

/// <summary>One ck_symbol observation row joined with its file path (shared with the ③ detectors).</summary>
internal sealed record CkSymbol(string SymbolId, string Name, string Kind, int StartLine, string SymKey, string Path, string Signature);

/// <summary>
/// One ck_edge observation row with its call/declaration site (shared with the ③ detectors).
/// <see cref="Confidence"/> carries the resolver's own fidelity (e.g. 0.5 for name-only
/// "ambiguous" cross-file CALLS resolution) so detectors can propagate it instead of
/// claiming config-grade certainty over a guessed edge (track expand-depa-detection-rules
/// T3.1 rescan adjudication).
/// </summary>
internal sealed record CkEdge(string FromId, string ToId, string Kind, string Evidence, string FileId, int Line, double Confidence = 1.0);

/// <summary>
/// The depa_scan materialization pipeline (design §3 end): steps ① annotation sync,
/// ② structural-relation materialization, ③ red-light detection materializing depa_violation
/// (track add-llm-wiki-depa-conformance-tools, <see cref="DepaViolationDetectors"/>) and
/// ④ existential-rule check.
///
/// Discipline (design §5): zero annotations produce BLOCKED findings — never guesses. Heuristic
/// judgements carry assigned_by=heuristic and confidence &lt;= 0.7. Every write is a put-style
/// upsert and every link is deduplicated per (from, rel, to), so repeated scans over the same
/// observation data are idempotent.
/// </summary>
internal static class DepaScanPipeline
{
    private const double HeuristicConfidence = 0.7;

    private static readonly string[] AnnotationDependentRules =
    [
        "capsule_must_expose_entry",
        "contract_must_have_impl",
        "projection_must_have_upstream",
        "factsource_must_have_writer",
    ];

    private static readonly IReadOnlyDictionary<int, string> GradeIds = new Dictionary<int, string>
    {
        [1] = "authoritative_fact",
        [2] = "domain_canonical_event",
        [3] = "runtime_control_fact",
        [4] = "append_only_journal",
        [5] = "checkpoint_snapshot",
        [6] = "derived_projection_cache",
        [7] = "surface_view",
    };

    private sealed record DepaAnnotation(string EntityId, string TypeName, string AssignedBy, double Confidence);

    /// <summary>
    /// Core pipeline entry (track fix-om-depa-conformance-gaps REC-2): consumes resolved
    /// configuration objects only — path resolution and every file read happen at the public
    /// entry layer (<see cref="CozoOmDepaExtensions.ResolveScanInputs"/>). Zero File.* calls
    /// in this type, by contract.
    /// </summary>
    internal static async Task<DepaScanResult> ScanAsync(CozoOm om, DepaMapConfig map, IReadOnlyList<DepaEffectRule>? userEffects, CancellationToken ct)
    {
        await DepaOntologySchema.InitAsync(om, ct);
        await DepaOntologySchema.SyncEffectApisAsync(om, userEffects, ct);

        var symbols = await LoadSymbolsAsync(om, ct);

        var entityCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var relationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var linked = new HashSet<(string From, string Rel, string To)>();
        var materialized = new HashSet<string>(StringComparer.Ordinal);
        var bySymbolId = new Dictionary<string, DepaAnnotation>(StringComparer.Ordinal);
        var configCount = 0;
        var heuristicCount = 0;

        async Task<string> UpsertAsync(string typeName, string idShort, string stableKey, string label, string assignedBy, double confidence, CkSymbol? anchor, params (string Attr, object? Value)[] props)
        {
            var id = $"depa:{idShort}:{stableKey}";
            await om.UpsertEntityAsync(id, typeName, label, ct);
            if (anchor is not null)
            {
                await om.SetPropertyAsync(id, "symbol_id", anchor.SymbolId, cancellationToken: ct);
                await om.SetPropertyAsync(id, "sym_key", anchor.SymKey, cancellationToken: ct);
                await om.SetPropertyAsync(id, "path", anchor.Path, cancellationToken: ct);
                await om.SetPropertyAsync(id, "line", anchor.StartLine, cancellationToken: ct);
            }

            await om.SetPropertyAsync(id, "assigned_by", assignedBy, cancellationToken: ct);
            await om.SetPropertyAsync(id, "confidence", confidence, cancellationToken: ct);
            foreach (var (attr, value) in props)
            {
                await om.SetPropertyAsync(id, attr, value, cancellationToken: ct);
            }

            if (materialized.Add(id))
            {
                entityCounts[typeName] = entityCounts.GetValueOrDefault(typeName) + 1;
                if (assignedBy == "config")
                {
                    configCount++;
                }
                else if (assignedBy == "heuristic")
                {
                    heuristicCount++;
                }
            }

            return id;
        }

        async Task<DepaAnnotation> AnnotateAsync(string typeName, string idShort, CkSymbol sym, string assignedBy, double confidence, params (string Attr, object? Value)[] props)
        {
            if (bySymbolId.TryGetValue(sym.SymbolId, out var existing))
            {
                return existing;
            }

            var id = await UpsertAsync(typeName, idShort, StableKeyOf(sym), sym.Name, assignedBy, confidence, sym, props);
            var annotation = new DepaAnnotation(id, typeName, assignedBy, confidence);
            bySymbolId[sym.SymbolId] = annotation;
            return annotation;
        }

        IReadOnlyList<CkSymbol> MatchSymbols(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return [];
            }

            var byName = symbols.Where(s => s.Name == key).ToArray();
            if (byName.Length > 0)
            {
                return byName;
            }

            var byPath = symbols.Where(s => s.Path == key).ToArray();
            if (byPath.Length > 0)
            {
                return byPath;
            }

            return symbols.Where(s =>
            {
                var qualified = QualifiedOf(s);
                return qualified == key || qualified.EndsWith("." + key, StringComparison.Ordinal);
            }).ToArray();
        }

        // ---- ① annotation sync: config channel first (assigned_by=config), then heuristics ----
        var capsuleList = new List<DepaCapsuleInfo>();
        foreach (var capsule in map.Capsules)
        {
            var rootPath = capsule.RootPath.TrimEnd('/');
            var id = await UpsertAsync(
                "depa_capsule", "capsule", capsule.Name, capsule.Name, "config", 1.0, anchor: null,
                ("name", capsule.Name), ("root_path", rootPath), ("internals_path", capsule.InternalsGlob), ("path", rootPath));
            capsuleList.Add(new DepaCapsuleInfo(id, capsule.Name, rootPath, capsule.InternalsGlob));
        }

        foreach (var entry in map.ContractPackages)
        {
            var prefix = entry.TrimEnd('/');
            foreach (var sym in symbols.Where(s => s.Kind == "interface" && (s.Name == entry || IsUnderPath(s.Path, prefix))))
            {
                await AnnotateAsync("depa_contract", "contract", sym, "config", 1.0, ("contract_kind", "effect"));
            }
        }

        // Track add-llm-wiki-depa-conformance-tools: detection-rule inputs declared in
        // depa-map.json (cores → V-E1/V-E2 subjects, projections → V-S1, runtimeParams → V-F1).
        foreach (var key in map.Cores)
        {
            foreach (var sym in MatchSymbols(key))
            {
                await AnnotateAsync("depa_impl", "impl", sym, "config", 1.0, ("purity", "core"));
            }
        }

        foreach (var key in map.Projections)
        {
            foreach (var sym in MatchSymbols(key))
            {
                await AnnotateAsync("depa_projection", "projection", sym, "config", 1.0, ("rebuildable", true));
            }
        }

        foreach (var param in map.RuntimeParams)
        {
            var declared = MatchSymbols(param.DeclaredType).FirstOrDefault();
            var paramProps = new (string, object?)[]
            {
                ("role", param.Role),
                ("declared_type_id", declared?.SymbolId ?? ""),
                // Raw declared-type text from depa-map.json: V-R1 (runtime big-bag lexicon) and
                // V-S3 (input-typed snapshot fields) judge it even when no repo symbol resolves
                // (track expand-depa-detection-rules T2.1).
                ("declared_type", param.DeclaredType),
            };
            var anchor = MatchSymbols(param.SymbolOrPath).FirstOrDefault();
            if (anchor is not null)
            {
                await AnnotateAsync("depa_runtime_param", "param", anchor, "config", 1.0, paramProps);
            }
            else
            {
                await UpsertAsync("depa_runtime_param", "param", param.SymbolOrPath, param.SymbolOrPath, "config", 1.0, anchor: null, paramProps);
            }
        }

        foreach (var typeName in map.RuntimeCarrierTypes)
        {
            foreach (var sym in symbols.Where(s => s.Name == typeName && IsCarrierKind(s.Kind)))
            {
                await AnnotateAsync("depa_runtime_carrier", "carrier", sym, "config", 1.0);
            }
        }

        var factSources = new List<(string EntityId, CkSymbol? Anchor, DepaFactSourceConfig Config)>();
        foreach (var fact in map.FactSources)
        {
            var anchor = MatchSymbols(fact.SymbolOrPath).FirstOrDefault();
            var props = new (string, object?)[]
            {
                ("grade", fact.Grade),
                ("grade_id", GradeIds.GetValueOrDefault(fact.Grade, "")),
                ("expected_owner", fact.ExpectedOwner),
            };
            string id;
            if (anchor is not null && bySymbolId.ContainsKey(anchor.SymbolId))
            {
                // The anchor symbol is already judged as another depa type (e.g. declared both
                // projection AND fact source). Do NOT swallow the grading into that entity —
                // materialize a separate depa_fact_source so the entanglement stays observable
                // (V-D3, track expand-depa-detection-rules T2.1).
                id = await UpsertAsync("depa_fact_source", "factsource", StableKeyOf(anchor), anchor.Name, "config", 1.0, anchor, props);
            }
            else if (anchor is not null)
            {
                id = (await AnnotateAsync("depa_fact_source", "factsource", anchor, "config", 1.0, props)).EntityId;
            }
            else
            {
                // Config truth without an observable anchor: materialize anyway so the
                // factsource_must_have_writer rule reports BLOCKED instead of silently passing.
                id = await UpsertAsync("depa_fact_source", "factsource", fact.SymbolOrPath, fact.SymbolOrPath, "config", 1.0, anchor: null, props);
            }

            factSources.Add((id, anchor, fact));
        }

        // Heuristic fallback (design §4.3 channel 3): only for symbols the config left unjudged.
        foreach (var sym in symbols.Where(s => s.Kind == "interface" && HasSegment(s.Path, "Contracts") && !bySymbolId.ContainsKey(s.SymbolId)))
        {
            await AnnotateAsync("depa_contract", "contract", sym, "heuristic", HeuristicConfidence, ("contract_kind", "effect"));
        }

        // Carrier heuristic is closed-world once the config channel speaks (track
        // fix-om-depa-conformance-gaps REC-4, delta case carrier-false-positive-suppressed):
        // a non-empty runtimeCarrierTypes list enumerates the real carriers, so name-based
        // guessing ("Runtime"/"Context" records) is suppressed entirely — it would only
        // re-introduce the false positives the declaration exists to kill.
        if (map.RuntimeCarrierTypes.Count == 0)
        {
            foreach (var sym in symbols.Where(s => s.Kind == "record"
                         && (s.Name.Contains("Runtime", StringComparison.Ordinal) || s.Name.Contains("Context", StringComparison.Ordinal))
                         && !bySymbolId.ContainsKey(s.SymbolId)))
            {
                await AnnotateAsync("depa_runtime_carrier", "carrier", sym, "heuristic", HeuristicConfidence);
            }
        }

        // ---- zero annotations: BLOCKED, never guess (delta case blocked-not-guess) ----
        if (configCount + heuristicCount == 0)
        {
            var (detectorBlocked, detectorVerdicts) = DepaViolationDetectors.BlockedForMissingAnnotations();
            var blocked = AnnotationDependentRules
                .Select(rule => new DepaRuleFinding(
                    rule,
                    "BLOCKED",
                    "",
                    "BLOCKED: no depa annotations available (depa-map.json missing or empty, no heuristic hits) — "
                    + "cannot evaluate this rule without capsule/contract/fact-source judgements; not guessing."))
                .Concat(detectorBlocked)
                .ToArray();
            return new DepaScanResult(entityCounts, relationCounts, blocked, configCount, heuristicCount)
            {
                DetectionVerdicts = detectorVerdicts,
            };
        }

        // ---- ② structural relations (design §2.1) ----
        var edges = await LoadEdgesAsync(om, ct);
        var entryPoints = await LoadEntryPointsAsync(om, ct);
        var symbolById = symbols.ToDictionary(s => s.SymbolId, StringComparer.Ordinal);

        async Task LinkAsync(string from, string rel, string to)
        {
            if (linked.Add((from, rel, to)))
            {
                await om.LinkEntitiesAsync(from, rel, to, cancellationToken: ct);
                relationCounts[rel] = relationCounts.GetValueOrDefault(rel) + 1;
            }
        }

        string? CapsuleOf(string path)
        {
            (string EntityId, int Depth)? best = null;
            foreach (var capsule in capsuleList)
            {
                if (IsUnderPath(path, capsule.RootPath) && (best is null || capsule.RootPath.Length > best.Value.Depth))
                {
                    best = (capsule.EntityId, capsule.RootPath.Length);
                }
            }

            return best?.EntityId;
        }

        // contract_implemented_by: ck_edge{IMPLEMENTS | METHOD_IMPLEMENTS} onto the contract anchor.
        // Cross-file tree-sitter IMPLEMENTS targets degrade to typeref:<lang>:<name> nodes, so the
        // name-based fallback keeps real repositories covered (recorded as a known low-fidelity path).
        var contracts = bySymbolId.Where(p => p.Value.TypeName == "depa_contract").ToArray();
        foreach (var (contractSymbolId, contract) in contracts)
        {
            var contractName = symbolById[contractSymbolId].Name;
            foreach (var edge in edges.Where(e =>
                         (e.Kind == "IMPLEMENTS" || e.Kind == "METHOD_IMPLEMENTS")
                         && (e.ToId == contractSymbolId || (e.ToId.StartsWith("typeref:", StringComparison.Ordinal) && e.ToId.EndsWith($":{contractName}", StringComparison.Ordinal)))))
            {
                if (!symbolById.TryGetValue(edge.FromId, out var implSym))
                {
                    continue;
                }

                var impl = await AnnotateAsync("depa_impl", "impl", implSym, contract.AssignedBy, contract.Confidence);
                if (impl.TypeName == "depa_impl")
                {
                    await LinkAsync(contract.EntityId, "contract_implemented_by", impl.EntityId);
                }
            }
        }

        // capsule_exposes + depa_entry materialization: ck_entry_point ∩ capsule members.
        var entryEntities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in entryPoints.GroupBy(e => e.SymbolId, StringComparer.Ordinal))
        {
            if (!symbolById.TryGetValue(group.Key, out var sym))
            {
                continue;
            }

            var capsuleId = CapsuleOf(sym.Path);
            if (capsuleId is null)
            {
                continue;
            }

            var entryKind = group.Select(e => e.Kind).OrderBy(EntryKindPriority).First();
            var annotation = await AnnotateAsync("depa_entry", "entry", sym, "config", 1.0, ("entry_kind", entryKind));
            if (annotation.TypeName != "depa_entry")
            {
                continue; // symbol already judged as another depa type — do not double-model.
            }

            entryEntities[group.Key] = annotation.EntityId;
            await LinkAsync(capsuleId, "capsule_exposes", annotation.EntityId);
        }

        // entry_delegates_to: CALLS from an entry symbol into a materialized depa_impl.
        foreach (var (entrySymbolId, entryEntityId) in entryEntities)
        {
            foreach (var edge in edges.Where(e => e.Kind == "CALLS" && e.FromId == entrySymbolId))
            {
                if (bySymbolId.TryGetValue(edge.ToId, out var target) && target.TypeName == "depa_impl")
                {
                    await LinkAsync(entryEntityId, "entry_delegates_to", target.EntityId);
                }
            }
        }

        // fact_written_by: write-evidence ACCESSES edges up to the writer (design §2.1).
        foreach (var (factId, anchor, _) in factSources)
        {
            if (anchor is null)
            {
                continue;
            }

            foreach (var edge in edges.Where(e => e.Kind == "ACCESSES" && e.ToId == anchor.SymbolId
                         && e.Evidence.Contains("write", StringComparison.OrdinalIgnoreCase)))
            {
                if (!symbolById.TryGetValue(edge.FromId, out var writerSym))
                {
                    continue;
                }

                var writer = await AnnotateAsync("depa_impl", "impl", writerSym, "heuristic", HeuristicConfidence);
                if (writer.TypeName == "depa_impl")
                {
                    await LinkAsync(factId, "fact_written_by", writer.EntityId);
                }
            }
        }

        // projection_derived_from: read-path ACCESSES from the projection onto a graded fact
        // anchor (design §2.1 — the derivation chain V-S1 later checks the reverse of).
        foreach (var (projSymbolId, projection) in bySymbolId.Where(p => p.Value.TypeName == "depa_projection").ToArray())
        {
            foreach (var (factId, anchor, _) in factSources)
            {
                if (anchor is not null && edges.Any(e =>
                        e.Kind == "ACCESSES" && e.FromId == projSymbolId && e.ToId == anchor.SymbolId
                        && !e.Evidence.Contains("write", StringComparison.OrdinalIgnoreCase)))
                {
                    await LinkAsync(projection.EntityId, "projection_derived_from", factId);
                }
            }
        }

        // capsule_contains: every anchored depa entity whose path falls under a capsule root.
        foreach (var (symbolId, annotation) in bySymbolId)
        {
            if (symbolById.TryGetValue(symbolId, out var sym) && CapsuleOf(sym.Path) is { } capsuleId)
            {
                await LinkAsync(capsuleId, "capsule_contains", annotation.EntityId);
            }
        }

        // capsule_depends_on: aggregated cross-capsule CALLS/ACCESSES member edges. (IMPORTS
        // targets are hashed import:* nodes without a path — not resolvable at this layer.)
        foreach (var edge in edges.Where(e => e.Kind is "CALLS" or "ACCESSES"))
        {
            if (symbolById.TryGetValue(edge.FromId, out var fromSym) && symbolById.TryGetValue(edge.ToId, out var toSym)
                && CapsuleOf(fromSym.Path) is { } fromCapsule && CapsuleOf(toSym.Path) is { } toCapsule
                && fromCapsule != toCapsule)
            {
                await LinkAsync(fromCapsule, "capsule_depends_on", toCapsule);
            }
        }

        // ---- ③ red-light detection & depa_violation materialization (design §3, this track) ----
        var filePaths = await LoadFilePathsAsync(om, ct);
        var externalCalls = await LoadExternalCallsAsync(om, ct);
        var scanCommit = await LoadScanCommitAsync(om, ct);
        var mergedEffects = DepaEffectCatalog.Merge(userEffects);
        var detection = await DepaViolationDetectors.RunAsync(
            om, symbols, edges, filePaths, externalCalls, capsuleList, mergedEffects, map, scanCommit, ct);

        // ---- ④ existential-rule check (design §2.3): misses become GAP/BLOCKED findings ----
        var violations = await om.CheckExistentialRulesAsync(cancellationToken: ct);
        var findings = violations
            .Select(v => new DepaRuleFinding(
                v.Rule,
                v.Message.StartsWith("BLOCKED", StringComparison.Ordinal) ? "BLOCKED" : "GAP",
                v.EntityId,
                v.Message))
            .Concat(detection.BlockedFindings)
            .OrderBy(f => f.RuleId, StringComparer.Ordinal)
            .ThenBy(f => f.EntityId, StringComparer.Ordinal)
            .ToArray();

        return new DepaScanResult(entityCounts, relationCounts, findings, configCount, heuristicCount)
        {
            Violations = detection.Violations,
            DetectionVerdicts = detection.Verdicts,
        };
    }

    private static int EntryKindPriority(string kind) => kind switch
    {
        "main" => 0,
        "http_route" => 1,
        "mcp_tool" => 2,
        "public_api" => 3,
        _ => 4,
    };

    private static string StableKeyOf(CkSymbol sym) =>
        sym.SymKey.Length > 0 ? sym.SymKey : $"{sym.Name}@{sym.Path}";

    private static string QualifiedOf(CkSymbol sym)
    {
        // sym_key format: "<lang>:<Qualified>#<arity>" — fall back to the bare name.
        var key = sym.SymKey;
        var colon = key.IndexOf(':');
        var hash = key.LastIndexOf('#');
        return colon >= 0 && hash > colon ? key[(colon + 1)..hash] : sym.Name;
    }

    private static bool IsUnderPath(string path, string prefix) =>
        prefix.Length > 0 && (path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal));

    private static bool HasSegment(string path, string segment) =>
        path.Split('/').Contains(segment, StringComparer.Ordinal);

    private static bool IsCarrierKind(string kind) =>
        kind is "class" or "record" or "struct" or "record_struct";

    private static async Task<IReadOnlyList<CkSymbol>> LoadSymbolsAsync(CozoOm om, CancellationToken ct)
    {
        try
        {
            var result = await om.Runtime.Store.RunAsync(
                """
                ?[symbol_id, name, kind, start_line, sym_key, path, signature] :=
                  *ck_symbol{ symbol_id, file_id, name, kind, start_line, sym_key, signature },
                  *ck_file{ file_id, path }
                """,
                cancellationToken: ct);
            return result.Rows
                .Select(row => new CkSymbol(
                    Str(row[0]), Str(row[1]), Str(row[2]), Int(row[3]), Str(row[4]), Str(row[5]), Str(row[6])))
                .Where(s => s.SymbolId.Length > 0)
                .ToArray();
        }
        catch (CozoException)
        {
            return []; // no CodeKnowledge schema in this store — zero observations.
        }
    }

    private static async Task<IReadOnlyList<CkEdge>> LoadEdgesAsync(CozoOm om, CancellationToken ct)
    {
        try
        {
            var result = await om.Runtime.Store.RunAsync(
                """
                ?[from_id, to_id, kind, evidence, file_id, line, confidence] :=
                  *ck_edge{ from_id, to_id, kind, file_id, line, evidence, confidence }
                """,
                cancellationToken: ct);
            return result.Rows
                .Select(row => new CkEdge(Str(row[0]), Str(row[1]), Str(row[2]), Str(row[3]), Str(row[4]), Int(row[5]), Num(row[6])))
                .ToArray();
        }
        catch (CozoException)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadFilePathsAsync(CozoOm om, CancellationToken ct)
    {
        try
        {
            var result = await om.Runtime.Store.RunAsync(
                "?[file_id, path] := *ck_file{ file_id, path }",
                cancellationToken: ct);
            return result.Rows
                .Where(row => Str(row[0]).Length > 0)
                .ToDictionary(row => Str(row[0]), row => Str(row[1]), StringComparer.Ordinal);
        }
        catch (CozoException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Null = the ck_external_call relation is absent (missing observation → V-E1 BLOCKED, not PASS).</summary>
    private static async Task<IReadOnlyList<DepaExternalCall>?> LoadExternalCallsAsync(CozoOm om, CancellationToken ct)
    {
        try
        {
            var result = await om.Runtime.Store.RunAsync(
                """
                ?[caller_id, target_key, count, category, first_file_id, first_line] :=
                  *ck_external_call{ caller_id, target_key, count, category, first_file_id, first_line }
                """,
                cancellationToken: ct);
            return result.Rows
                .Select(row => new DepaExternalCall(Str(row[0]), Str(row[1]), Int(row[2]), Str(row[3]), Str(row[4]), Int(row[5])))
                .ToArray();
        }
        catch (CozoException)
        {
            return null;
        }
    }

    private static async Task<string> LoadScanCommitAsync(CozoOm om, CancellationToken ct)
    {
        try
        {
            var result = await om.Runtime.Store.RunAsync("?[commit] := *ck_repo{ commit }", cancellationToken: ct);
            return result.Rows.Select(row => Str(row[0])).FirstOrDefault(c => c.Length > 0) ?? "";
        }
        catch (CozoException)
        {
            return "";
        }
    }

    private static async Task<IReadOnlyList<(string SymbolId, string Kind)>> LoadEntryPointsAsync(CozoOm om, CancellationToken ct)
    {
        try
        {
            var result = await om.Runtime.Store.RunAsync(
                "?[symbol_id, kind] := *ck_entry_point{ symbol_id, kind }",
                cancellationToken: ct);
            return result.Rows.Select(row => (Str(row[0]), Str(row[1]))).ToArray();
        }
        catch (CozoException)
        {
            return [];
        }
    }

    private static string Str(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int Int(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static double Num(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 1.0;
}
