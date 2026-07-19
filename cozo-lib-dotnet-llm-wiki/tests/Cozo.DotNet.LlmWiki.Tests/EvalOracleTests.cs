using System.Text.Json;
using System.Text.Json.Nodes;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Core;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>One oracle run outcome: the task, its score, and per-tool diagnostics (tool errors, if any).</summary>
internal sealed record EvalOracleTaskResult(EvalTask Task, EvalScoreResult Score, IReadOnlyList<string> Diagnostics);

/// <summary>
/// Oracle executor for the eval baseline (track add-llm-wiki-eval-baseline T2.1, delta case
/// oracle-gate): per work root it builds one real in-memory Cozo db, indexes the real repository
/// through the shared tool runner (index_repo), then answers every task by executing its
/// suggested tool sequence and concatenating the JSON-serialized tool outputs as the "answer
/// text" scored by EvalScorer. Input-echo fields (query/rootId/targetId/reason) are stripped
/// from each output first, so a hit proves the task set is answerable from the graph alone.
/// </summary>
internal static class EvalOracle
{
    /// <summary>Tools whose optional workDirectory argument must point at the work root (the MCP server would run with cwd there).</summary>
    private static readonly string[] WorkDirectoryTools = ["depa_conformance", "health_score", "detect_changes"];

    /// <summary>Top-level result fields that merely echo the tool's own input arguments; removed before scoring.</summary>
    private static readonly string[] EchoFields = ["query", "rootId", "targetId", "reason"];

    /// <summary>The directory containing both work-root repositories (parent of the llm-wiki repo root).</summary>
    internal static string ResolveWorkRootContainer()
    {
        var repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(EvalTaskSet.ResolveTasksPath()))
            ?? throw new InvalidOperationException("could not resolve the llm-wiki repository root");
        return Path.GetDirectoryName(repoRoot)
            ?? throw new InvalidOperationException("could not resolve the work-root container directory");
    }

    internal static async Task<IReadOnlyList<EvalOracleTaskResult>> RunAsync(
        IReadOnlyList<EvalTask> tasks, CancellationToken cancellationToken = default)
    {
        var container = ResolveWorkRootContainer();
        var resultsById = new Dictionary<string, EvalOracleTaskResult>(StringComparer.Ordinal);
        foreach (var group in tasks.GroupBy(task => task.WorkRoot, StringComparer.Ordinal))
        {
            var repoPath = Path.GetFullPath(Path.Combine(container, group.Key));
            if (!Directory.Exists(repoPath))
            {
                throw new InvalidOperationException($"work root '{group.Key}' not found at {repoPath}");
            }

            using var db = new CozoDb("mem", "");
            var om = new CozoOm(db);
            var runner = new LlmWikiToolRunner(om);
            await runner.CallAsync("index_repo", new JsonObject { ["repoPath"] = repoPath }, cancellationToken);

            foreach (var task in group)
            {
                resultsById[task.Id] = await AnswerAsync(runner, repoPath, task, cancellationToken);
            }
        }

        // Report in the original task-set order regardless of work-root grouping.
        return tasks.Select(task => resultsById[task.Id]).ToArray();
    }

    private static async Task<EvalOracleTaskResult> AnswerAsync(
        LlmWikiToolRunner runner, string repoPath, EvalTask task, CancellationToken cancellationToken)
    {
        var answerParts = new List<string>();
        var diagnostics = new List<string>();
        foreach (var call in task.Tools)
        {
            // Clone: the parsed arguments node stays attached to the task-set document tree.
            var args = (JsonObject)call.Arguments.DeepClone();
            if (WorkDirectoryTools.Contains(call.Name) && !args.ContainsKey("workDirectory"))
            {
                args["workDirectory"] = repoPath;
            }

            try
            {
                var result = await runner.CallAsync(call.Name, args, cancellationToken);
                var node = JsonSerializer.SerializeToNode(result, LlmWikiJson.Options);
                if (node is JsonObject obj)
                {
                    // Strip top-level input-echo fields (semantic_search query, impact rootId,
                    // docs targetId, trace reason) before scoring: a must hit must come from
                    // returned graph evidence, never from the tool echoing its own arguments.
                    foreach (var echoField in EchoFields)
                    {
                        obj.Remove(echoField);
                    }
                }
                answerParts.Add(node?.ToJsonString(LlmWikiJson.Options) ?? "");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed tool call contributes no keywords; the oracle itself never crashes.
                diagnostics.Add($"{call.Name}: {ex.Message}");
            }
        }

        var answerText = string.Join("\n", answerParts);
        var score = EvalScorer.Score(answerText, task);
        if (score.MustHit < score.MustTotal && Environment.GetEnvironmentVariable("EVAL_ORACLE_DEBUG") == "1")
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), $"eval-oracle-{task.Id}.txt"), answerText);
        }

        return new EvalOracleTaskResult(task, score, diagnostics);
    }
}

/// <summary>
/// oracle-gate (delta llm-wiki-tools requirements/eval-baseline): with both work roots indexed,
/// executing the whole task set in oracle mode must hit at least 90% of all must keywords.
/// The per-task detail printed here is the dogfood record for the track findings.
/// </summary>
internal static class EvalOracleGateTests
{
    internal const double MustHitGate = 0.90;

    internal static async Task RunAsync(Action<bool, string> assert)
    {
        var tasks = EvalTaskSet.Load(EvalTaskSet.ResolveTasksPath());
        var results = await EvalOracle.RunAsync(tasks);
        assert(results.Count == tasks.Count, "the oracle should answer every task exactly once");

        foreach (var result in results)
        {
            Console.WriteLine(
                $"  [eval-oracle] {result.Task.Id} ({result.Task.Difficulty}, {result.Task.WorkRoot}): "
                + $"must {result.Score.MustHit}/{result.Score.MustTotal}, "
                + $"should {result.Score.ShouldHit}/{result.Score.ShouldTotal}, "
                + $"score {result.Score.Score:F2}"
                + (result.Diagnostics.Count > 0 ? " | " + string.Join("; ", result.Diagnostics) : ""));
        }

        // Every suggested tool call must execute cleanly — a throwing sequence is a task bug.
        var failures = results.Where(r => r.Diagnostics.Count > 0).Select(r => r.Task.Id).ToArray();
        assert(failures.Length == 0,
            "every oracle tool sequence should execute without errors (failing: " + string.Join(", ", failures) + ")");

        var mustTotal = results.Sum(r => r.Score.MustTotal);
        var mustHit = results.Sum(r => r.Score.MustHit);
        var rate = mustTotal == 0 ? 0.0 : (double)mustHit / mustTotal;
        Console.WriteLine($"  [eval-oracle] aggregate must hit rate: {mustHit}/{mustTotal} = {rate:P1}");
        assert(rate >= MustHitGate,
            $"oracle must-keyword hit rate should be >= {MustHitGate:P0} over the whole task set (got {mustHit}/{mustTotal} = {rate:P1}; "
            + "misses: " + string.Join(", ", results
                .Where(r => r.Score.MustHit < r.Score.MustTotal)
                .Select(r => $"{r.Task.Id} {r.Score.MustHit}/{r.Score.MustTotal}")) + ")");
    }
}
