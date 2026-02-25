using System.Text.Json;

namespace Cozo.DotNet.Om.CodeKnowledge;

/// <summary>
/// Process (execution-flow) extraction over the CodeKnowledge graph
/// (track add-llm-wiki-process-extraction, design §1–§2).
///
/// Entry points are detected in two layers: graph-level here (main / public_api over ck_symbol)
/// and syntax-level candidates (http_route / mcp_tool) written into ck_entry_point by the
/// indexing pipeline. Extraction merges both: pre-existing ck_entry_point rows whose kind is
/// neither main nor public_api are read out first and survive the :replace rebuild, while
/// stale main / public_api rows are superseded by fresh detection.
/// </summary>
public static class CozoOmProcessExtensions
{
    /// <summary>
    /// Detects entry points, runs a bounded cycle-safe BFS along CALLS edges with confidence
    /// &gt;= <see cref="ProcessExtractionOptions.MinConfidence"/> from each one, and persists the
    /// result to ck_entry_point / ck_process / ck_process_step (replacing previous contents).
    ///
    /// Semantics:
    /// - main: ck_symbol with kind=method and name=Main, or sym_key containing "&lt;Main&gt;$"
    ///   (top-level statements); public_api: exported public methods/constructors. A symbol with
    ///   several candidate kinds keeps the highest-priority one.
    /// - steps are numbered by BFS first-reach order, step 0 = the entry symbol itself.
    /// - process_type = entry kind, plus ",cross_community" when the steps span &gt;= 2
    ///   ck_member communities.
    /// - budgets: chains shorter than MinSteps are dropped (counted); BFS is cut at
    ///   MaxStepsPerProcess (counted per process); entry points beyond MaxProcesses
    ///   (default max(20, min(300, symbolCount / 10))) are not traversed (counted). The canonical
    ///   entry ordering (main &gt; http_route &gt; mcp_tool &gt; public_api, symbolId asc) makes the
    ///   budget cut from the public_api tail.
    /// - deterministic: all outputs are sorted and processes are numbered process:001….
    /// </summary>
    public static async Task<ProcessExtractionResult> ExtractProcessesAsync(
        this CozoOm om,
        ProcessExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        options ??= new ProcessExtractionOptions();

        // 1. Symbols (detection input, names) and file paths (process display names).
        var symbolRows = await om.Runtime.Store.RunAsync(
            "?[symbol_id, file_id, name, kind, visibility, exported, sym_key] := *ck_symbol{ symbol_id, file_id, name, kind, visibility, exported, sym_key }",
            cancellationToken: cancellationToken);
        var symbols = new Dictionary<string, SymbolInfo>(StringComparer.Ordinal);
        foreach (var row in symbolRows.Rows)
        {
            var symbolId = AsString(row[0]);
            if (symbolId.Length == 0)
            {
                continue;
            }

            symbols[symbolId] = new SymbolInfo(
                AsString(row[1]), AsString(row[2]), AsString(row[3]), AsString(row[4]),
                row[5].ValueKind == JsonValueKind.True, AsString(row[6]));
        }

        var fileRows = await om.Runtime.Store.RunAsync(
            "?[file_id, path] := *ck_file{ file_id, path }",
            cancellationToken: cancellationToken);
        var filePaths = fileRows.Rows.ToDictionary(row => AsString(row[0]), row => AsString(row[1]), StringComparer.Ordinal);

        // 2. Entry candidates: graph-level detection (main / public_api) plus pre-existing
        //    ck_entry_point rows of other kinds (syntax-level candidates from the indexing
        //    pipeline). Stale main / public_api rows are dropped and rebuilt from detection.
        var candidates = new List<CodeEntryPointSummary>();
        foreach (var (symbolId, symbol) in symbols)
        {
            if ((symbol.Kind == "method" && symbol.Name == "Main") || symbol.SymKey.Contains("<Main>$", StringComparison.Ordinal))
            {
                candidates.Add(new CodeEntryPointSummary(symbolId, CodeEntryPointKinds.Main, "detector=om"));
            }
            else if (symbol.Exported && symbol.Visibility == "public" && symbol.Kind is "method" or "constructor")
            {
                candidates.Add(new CodeEntryPointSummary(symbolId, CodeEntryPointKinds.PublicApi, "detector=om"));
            }
        }

        var existingRows = await om.Runtime.Store.RunAsync(
            "?[symbol_id, kind, metadata] := *ck_entry_point{ symbol_id, kind, metadata }",
            cancellationToken: cancellationToken);
        foreach (var row in existingRows.Rows)
        {
            var kind = AsString(row[1]);
            if (kind is not (CodeEntryPointKinds.Main or CodeEntryPointKinds.PublicApi))
            {
                candidates.Add(new CodeEntryPointSummary(AsString(row[0]), kind, AsString(row[2])));
            }
        }

        // One entry kind per symbol (highest priority wins) + canonical ordering.
        var entryPoints = candidates
            .GroupBy(c => c.SymbolId, StringComparer.Ordinal)
            .Select(g => g.OrderBy(c => KindPriority(c.Kind)).ThenBy(c => c.Kind, StringComparer.Ordinal).First())
            .OrderBy(c => KindPriority(c.Kind))
            .ThenBy(c => c.Kind, StringComparer.Ordinal)
            .ThenBy(c => c.SymbolId, StringComparer.Ordinal)
            .ToArray();

        // 3. CALLS adjacency (single Datalog pull, sorted neighbor lists for determinism).
        var edgeRows = await om.Runtime.Store.RunAsync(
            CodeGraphProjections.CallGraph + "\n?[from, to] := call_graph[from, to]",
            new Dictionary<string, object?> { ["min_confidence"] = options.MinConfidence },
            cancellationToken: cancellationToken);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in edgeRows.Rows)
        {
            var from = AsString(row[0]);
            if (!adjacency.TryGetValue(from, out var neighbors))
            {
                adjacency[from] = neighbors = [];
            }

            neighbors.Add(AsString(row[1]));
        }

        foreach (var neighbors in adjacency.Values)
        {
            neighbors.Sort(StringComparer.Ordinal);
        }

        // 4. Community membership (cross_community marker).
        var memberRows = await om.Runtime.Store.RunAsync(
            "?[symbol_id, community_id] := *ck_member{ symbol_id, community_id }",
            cancellationToken: cancellationToken);
        var memberCommunity = memberRows.Rows.ToDictionary(row => AsString(row[0]), row => AsString(row[1]), StringComparer.Ordinal);

        // 5. Bounded BFS per entry, in canonical order, under the process budget.
        var traversalKinds = options.EntryPointKinds is null
            ? null
            : new HashSet<string>(options.EntryPointKinds, StringComparer.Ordinal);
        var maxProcesses = options.MaxProcesses ?? Math.Max(20, Math.Min(300, symbols.Count / 10));
        var processes = new List<CodeProcessSummary>();
        var processSteps = new List<CodeProcessStepSummary>();
        var dropped = 0;
        var truncatedSteps = 0;
        var truncatedProcesses = 0;
        foreach (var entry in entryPoints)
        {
            if (traversalKinds is not null && !traversalKinds.Contains(entry.Kind))
            {
                continue;
            }

            if (processes.Count >= maxProcesses)
            {
                truncatedProcesses++;
                continue;
            }

            var (steps, truncated) = Traverse(entry.SymbolId, adjacency, options.MaxStepsPerProcess);
            if (truncated)
            {
                truncatedSteps++;
            }

            if (steps.Count < options.MinSteps)
            {
                dropped++;
                continue;
            }

            var processId = $"process:{processes.Count + 1:000}";
            var communityCount = steps
                .Select(s => memberCommunity.GetValueOrDefault(s))
                .Where(c => c is not null)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var processType = communityCount >= 2 ? $"{entry.Kind},cross_community" : entry.Kind;
            processes.Add(new CodeProcessSummary(
                processId, ProcessName(entry.SymbolId, symbols, filePaths), entry.SymbolId, entry.Kind, processType, steps.Count));
            for (var i = 0; i < steps.Count; i++)
            {
                processSteps.Add(new CodeProcessStepSummary(processId, i, steps[i], i == 0 ? "" : CodeEdgeKinds.Calls));
            }
        }

        // 6. Persist (replace previous contents; merged entry points keep external candidates).
        await using var tx = await om.Runtime.Store.BeginTransactionAsync(write: true, cancellationToken);
        await tx.RunAsync(
            """
            ?[symbol_id, kind, metadata] <- $rows
            :replace ck_entry_point {symbol_id, kind => metadata}
            """,
            new Dictionary<string, object?>
            {
                ["rows"] = entryPoints.Select(e => new object?[] { e.SymbolId, e.Kind, e.Metadata }).ToList(),
            },
            cancellationToken: cancellationToken);
        await tx.RunAsync(
            """
            ?[process_id, name, entry_symbol_id, entry_kind, process_type, step_count] <- $rows
            :replace ck_process {process_id => name, entry_symbol_id, entry_kind, process_type, step_count}
            """,
            new Dictionary<string, object?>
            {
                ["rows"] = processes.Select(p => new object?[] { p.ProcessId, p.Name, p.EntrySymbolId, p.EntryKind, p.ProcessType, p.StepCount }).ToList(),
            },
            cancellationToken: cancellationToken);
        await tx.RunAsync(
            """
            ?[process_id, step, symbol_id, via_kind] <- $rows
            :replace ck_process_step {process_id, step => symbol_id, via_kind}
            """,
            new Dictionary<string, object?>
            {
                ["rows"] = processSteps.Select(s => new object?[] { s.ProcessId, s.Step, s.SymbolId, s.ViaKind }).ToList(),
            },
            cancellationToken: cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new ProcessExtractionResult(entryPoints, processes, dropped, truncatedSteps, truncatedProcesses);
    }

    /// <summary>Lists persisted processes (ck_process) in canonical process_id order.</summary>
    public static async Task<IReadOnlyList<CodeProcessSummary>> ListProcessesAsync(
        this CozoOm om,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[process_id, name, entry_symbol_id, entry_kind, process_type, step_count] :=
              *ck_process{ process_id, name, entry_symbol_id, entry_kind, process_type, step_count }
            :sort process_id
            """,
            cancellationToken: cancellationToken);
        return rows.Rows.Select(ReadProcessSummary).ToArray();
    }

    /// <summary>Returns the steps of a persisted process in step order (empty if unknown).</summary>
    public static async Task<IReadOnlyList<CodeProcessStepSummary>> GetProcessStepsAsync(
        this CozoOm om,
        string processId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentException.ThrowIfNullOrEmpty(processId);
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[step, symbol_id, via_kind] := *ck_process_step{ process_id: $process_id, step, symbol_id, via_kind }
            :sort step
            """,
            new Dictionary<string, object?> { ["process_id"] = processId },
            cancellationToken: cancellationToken);
        return rows.Rows
            .Select(row => new CodeProcessStepSummary(
                processId,
                row[0].ValueKind == JsonValueKind.Number ? row[0].GetInt32() : 0,
                AsString(row[1]),
                AsString(row[2])))
            .ToArray();
    }

    /// <summary>Returns every persisted process containing the symbol as a step, in canonical order.</summary>
    public static async Task<IReadOnlyList<CodeProcessSummary>> FindProcessesForSymbolAsync(
        this CozoOm om,
        string symbolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentException.ThrowIfNullOrEmpty(symbolId);
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[process_id, name, entry_symbol_id, entry_kind, process_type, step_count] :=
              *ck_process_step{ process_id, symbol_id: $symbol_id },
              *ck_process{ process_id, name, entry_symbol_id, entry_kind, process_type, step_count }
            :sort process_id
            """,
            new Dictionary<string, object?> { ["symbol_id"] = symbolId },
            cancellationToken: cancellationToken);
        return rows.Rows.Select(ReadProcessSummary).ToArray();
    }

    /// <summary>
    /// Cycle-safe BFS from the entry (steps = first-reach order, entry included). Stops once
    /// <paramref name="maxSteps"/> steps are emitted; Truncated reports whether reachable
    /// symbols remained.
    /// </summary>
    private static (List<string> Steps, bool Truncated) Traverse(
        string entry,
        IReadOnlyDictionary<string, List<string>> adjacency,
        int maxSteps)
    {
        var steps = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { entry };
        var queue = new Queue<string>();
        queue.Enqueue(entry);
        while (queue.Count > 0)
        {
            if (steps.Count >= maxSteps)
            {
                return (steps, true);
            }

            var current = queue.Dequeue();
            steps.Add(current);
            if (!adjacency.TryGetValue(current, out var neighbors))
            {
                continue;
            }

            foreach (var neighbor in neighbors)
            {
                if (visited.Add(neighbor))
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        return (steps, false);
    }

    /// <summary>Entry-kind traversal priority: main first, public_api last (budget cuts its tail).</summary>
    private static int KindPriority(string kind) =>
        kind switch
        {
            CodeEntryPointKinds.Main => 0,
            CodeEntryPointKinds.CliCommand => 1,
            CodeEntryPointKinds.HttpRoute => 2,
            CodeEntryPointKinds.McpTool => 3,
            CodeEntryPointKinds.PublicApi => 5,
            _ => 4,
        };

    /// <summary>"EntryName (File.cs)"; falls back to the bare name, then the symbol id.</summary>
    private static string ProcessName(
        string symbolId,
        IReadOnlyDictionary<string, SymbolInfo> symbols,
        IReadOnlyDictionary<string, string> filePaths)
    {
        if (!symbols.TryGetValue(symbolId, out var symbol))
        {
            return symbolId;
        }

        var name = symbol.Name.Length > 0 ? symbol.Name : symbolId;
        var path = filePaths.GetValueOrDefault(symbol.FileId, "");
        var shortName = path.Length > 0 ? path[(path.LastIndexOfAny(['/', '\\']) + 1)..] : "";
        return shortName.Length > 0 ? $"{name} ({shortName})" : name;
    }

    private static CodeProcessSummary ReadProcessSummary(IReadOnlyList<JsonElement> row) =>
        new(
            AsString(row[0]),
            AsString(row[1]),
            AsString(row[2]),
            AsString(row[3]),
            AsString(row[4]),
            row[5].ValueKind == JsonValueKind.Number ? row[5].GetInt32() : 0);

    private static string AsString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();

    private sealed record SymbolInfo(string FileId, string Name, string Kind, string Visibility, bool Exported, string SymKey);
}
