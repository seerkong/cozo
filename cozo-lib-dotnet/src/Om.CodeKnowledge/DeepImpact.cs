using System.Text.Json;

namespace Cozo.DotNet.Om.CodeKnowledge;

/// <summary>
/// Layered impact analysis over the CALLS graph (track deepen-llm-wiki-context-impact,
/// design §2). Complements — does not replace — the legacy flat <c>ImpactOfChangeAsync</c>.
/// </summary>
public static class CozoOmDeepImpactExtensions
{
    private const int MaxAffectedProcesses = 20;
    private const int CriticalProcessThreshold = 3;

    /// <summary>
    /// Layered BFS impact of a change to <paramref name="rootSymbolId"/> along CALLS edges with
    /// confidence &gt;= <see cref="DeepImpactOptions.MinConfidence"/>.
    ///
    /// Semantics:
    /// - Direction Up (default): edges are walked in reverse (to → from), so layer d holds the
    ///   symbols that (transitively, at distance d) call the root — its blast radius. Down walks
    ///   forward (from → to): the root's transitive callees / dependency surface.
    /// - layers are first-reach: each symbol appears in exactly one layer (its BFS distance),
    ///   decorated with name/file/line and the highest confidence among the edges that reached it.
    /// - bounding: each layer reports at most MaxPerLayer symbols (sorted by symbol id) but keeps
    ///   the real first-reach count in <see cref="ImpactLayer.Total"/>; deeper expansion still
    ///   proceeds from all first-reached symbols, so totals stay truthful. MaxDepth clamps to 1..16.
    /// - risk: from layer 1's real total — LOW &lt; 4, MEDIUM 4–9, HIGH &gt;= 10, CRITICAL when
    ///   HIGH and the root participates in &gt;= 3 execution flows.
    /// - AffectedProcesses: processes containing the root or any reported layer member, deduplicated,
    ///   canonical order, capped at 20 (cut counted). Process/community lookups never throw:
    ///   missing or empty tables yield empty fields.
    /// - deterministic: layer members and processes are sorted; two runs over the same graph
    ///   produce identical output.
    /// </summary>
    public static async Task<DeepImpactResult> DeepImpactAsync(
        this CozoOm om,
        string rootSymbolId,
        DeepImpactOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentException.ThrowIfNullOrEmpty(rootSymbolId);
        options ??= new DeepImpactOptions();
        var maxDepth = Math.Clamp(options.MaxDepth, 1, 16);
        var maxPerLayer = Math.Max(1, options.MaxPerLayer);

        // 1. CALLS edges at or above MinConfidence, pulled once into an in-memory adjacency map.
        //    Direction decides the orientation: Down follows from → to, Up reverses to → from.
        //    Value = best confidence per (node, neighbor) pair across call sites.
        var edgeRows = await om.Runtime.Store.RunAsync(
            """
            ?[from_id, to_id, confidence] :=
              *ck_edge{ from_id, to_id, kind: "CALLS", confidence },
              confidence >= $min_confidence
            """,
            new Dictionary<string, object?> { ["min_confidence"] = options.MinConfidence },
            cancellationToken: cancellationToken);
        var adjacency = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var row in edgeRows.Rows)
        {
            var fromId = AsString(row[0]);
            var toId = AsString(row[1]);
            if (fromId.Length == 0 || toId.Length == 0)
            {
                continue;
            }

            var confidence = row[2].ValueKind == JsonValueKind.Number ? row[2].GetDouble() : 0.0;
            var (source, target) = options.Direction == ImpactDirection.Down ? (fromId, toId) : (toId, fromId);
            if (!adjacency.TryGetValue(source, out var neighbors))
            {
                adjacency[source] = neighbors = new Dictionary<string, double>(StringComparer.Ordinal);
            }

            if (!neighbors.TryGetValue(target, out var best) || confidence > best)
            {
                neighbors[target] = confidence;
            }
        }

        // 2. Layered BFS first-reach from the root.
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootSymbolId };
        var frontier = new List<string> { rootSymbolId };
        var rawLayers = new List<(string[] Members, Dictionary<string, double> Confidence, int Total)>();
        var reportedMembers = new List<string>();
        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var reached = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var node in frontier)
            {
                if (!adjacency.TryGetValue(node, out var neighbors))
                {
                    continue;
                }

                foreach (var (neighbor, confidence) in neighbors)
                {
                    if (visited.Contains(neighbor))
                    {
                        continue;
                    }

                    if (!reached.TryGetValue(neighbor, out var best) || confidence > best)
                    {
                        reached[neighbor] = confidence;
                    }
                }
            }

            if (reached.Count == 0)
            {
                break;
            }

            var ordered = reached.Keys.Order(StringComparer.Ordinal).ToArray();
            visited.UnionWith(ordered);
            var kept = ordered.Length > maxPerLayer ? ordered[..maxPerLayer] : ordered;
            rawLayers.Add((kept, reached, ordered.Length));
            reportedMembers.AddRange(kept);
            // Expansion continues from all first-reached symbols (not just the reported ones),
            // so deeper layers keep truthful totals under per-layer truncation.
            frontier = [.. ordered];
        }

        // 3. Decorate reported members with name/file/line.
        var symbolInfo = await SymbolInfoAsync(om, reportedMembers, cancellationToken);
        var layers = new List<ImpactLayer>(rawLayers.Count);
        for (var i = 0; i < rawLayers.Count; i++)
        {
            var (members, confidence, total) = rawLayers[i];
            var symbols = members
                .Select(id =>
                {
                    var info = symbolInfo.GetValueOrDefault(id, ("", "", "", 0));
                    return new ImpactLayerSymbol(id, info.Item1, info.Item2, info.Item3, info.Item4, confidence[id]);
                })
                .ToArray();
            layers.Add(new ImpactLayer(i + 1, symbols, total, total - members.Length));
        }

        // 4. Risk rating from layer 1's real total, escalated by the root's process participation.
        var direct = layers.Count > 0 ? layers[0].Total : 0;
        var risk = direct switch
        {
            < 4 => RiskLevel.Low,
            < 10 => RiskLevel.Medium,
            _ => RiskLevel.High,
        };
        if (risk == RiskLevel.High && await RootProcessCountAsync(om, rootSymbolId, cancellationToken) >= CriticalProcessThreshold)
        {
            risk = RiskLevel.Critical;
        }

        // 5. Affected execution flows: root + reported layer members, deduplicated, bounded.
        var (affected, truncatedProcesses) = await AffectedProcessesAsync(
            om, [rootSymbolId, .. reportedMembers], cancellationToken);

        return new DeepImpactResult(rootSymbolId, options.Direction, layers, risk, affected, truncatedProcesses);
    }

    private static async Task<Dictionary<string, (string Name, string FileId, string Path, int Line)>> SymbolInfoAsync(
        CozoOm om,
        IReadOnlyList<string> symbolIds,
        CancellationToken cancellationToken)
    {
        var info = new Dictionary<string, (string, string, string, int)>(StringComparer.Ordinal);
        if (symbolIds.Count == 0)
        {
            return info;
        }

        var ids = symbolIds.Distinct(StringComparer.Ordinal).ToList();
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[symbol_id, name, file_id, start_line] :=
              *ck_symbol{ symbol_id, name, file_id, start_line },
              is_in(symbol_id, $ids)
            """,
            new Dictionary<string, object?> { ["ids"] = ids },
            cancellationToken: cancellationToken);
        var fileIds = new List<string>();
        foreach (var row in rows.Rows)
        {
            var fileId = AsString(row[2]);
            info[AsString(row[0])] = (
                AsString(row[1]),
                fileId,
                "",
                row[3].ValueKind == JsonValueKind.Number ? row[3].GetInt32() : 0);
            fileIds.Add(fileId);
        }

        if (fileIds.Count == 0)
        {
            return info;
        }

        var fileRows = await om.Runtime.Store.RunAsync(
            "?[file_id, path] := *ck_file{ file_id, path }, is_in(file_id, $ids)",
            new Dictionary<string, object?> { ["ids"] = fileIds.Distinct(StringComparer.Ordinal).ToList() },
            cancellationToken: cancellationToken);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in fileRows.Rows)
        {
            paths[AsString(row[0])] = AsString(row[1]);
        }

        foreach (var (symbolId, value) in info.ToArray())
        {
            info[symbolId] = value with { Item3 = paths.GetValueOrDefault(value.Item2, "") };
        }

        return info;
    }

    /// <summary>Number of execution flows the root participates in; 0 on missing tables or query failure.</summary>
    private static async Task<int> RootProcessCountAsync(CozoOm om, string rootSymbolId, CancellationToken cancellationToken)
    {
        try
        {
            return (await om.FindProcessesForSymbolAsync(rootSymbolId, cancellationToken)).Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    private static async Task<(IReadOnlyList<CodeProcessSummary> Processes, int Truncated)> AffectedProcessesAsync(
        CozoOm om,
        IReadOnlyList<string> symbolIds,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await om.Runtime.Store.RunAsync(
                """
                ?[process_id, name, entry_symbol_id, entry_kind, process_type, step_count] :=
                  *ck_process_step{ process_id, symbol_id },
                  is_in(symbol_id, $ids),
                  *ck_process{ process_id, name, entry_symbol_id, entry_kind, process_type, step_count }
                :sort process_id
                """,
                new Dictionary<string, object?> { ["ids"] = symbolIds.Distinct(StringComparer.Ordinal).ToList() },
                cancellationToken: cancellationToken);
            var all = rows.Rows
                .Select(row => new CodeProcessSummary(
                    AsString(row[0]),
                    AsString(row[1]),
                    AsString(row[2]),
                    AsString(row[3]),
                    AsString(row[4]),
                    row[5].ValueKind == JsonValueKind.Number ? row[5].GetInt32() : 0))
                .ToArray();
            return all.Length > MaxAffectedProcesses
                ? (all[..MaxAffectedProcesses], all.Length - MaxAffectedProcesses)
                : (all, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ([], 0);
        }
    }

    private static string AsString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
}
