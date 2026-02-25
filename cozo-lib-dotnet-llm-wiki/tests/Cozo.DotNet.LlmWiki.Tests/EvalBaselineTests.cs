using System.Text.Json;
using System.Text.Json.Nodes;
using Cozo.DotNet.LlmWiki.Tools;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>One suggested tool invocation inside an eval task (name + real-argument object).</summary>
internal sealed record EvalToolCall(string Name, JsonObject Arguments);

/// <summary>One eval task: a real question over a work root plus scoring keywords.</summary>
internal sealed record EvalTask(
    string Id,
    string Question,
    string Difficulty,
    string WorkRoot,
    IReadOnlyList<EvalToolCall> Tools,
    IReadOnlyList<string> Must,
    IReadOnlyList<string> Should);

/// <summary>Per-task score: weighted must (0.8) / should (0.2) keyword hits over an answer text.</summary>
internal sealed record EvalScoreResult(int MustHit, int MustTotal, int ShouldHit, int ShouldTotal, double Score);

/// <summary>
/// Keyword scorer for the eval baseline (track add-llm-wiki-eval-baseline T1.1).
/// Case-insensitive substring matching on purpose: keywords are long real identifiers
/// (symbol/file names) chosen to avoid accidental hits, so loose word boundaries are safe.
/// </summary>
internal static class EvalScorer
{
    internal const double MustWeight = 0.8;
    internal const double ShouldWeight = 0.2;

    internal static EvalScoreResult Score(string answerText, EvalTask task)
    {
        var text = answerText ?? "";
        var mustHit = task.Must.Count(keyword => Hits(text, keyword));
        var shouldHit = task.Should.Count(keyword => Hits(text, keyword));
        var mustRatio = task.Must.Count == 0 ? 0.0 : (double)mustHit / task.Must.Count;
        // Tasks without should keywords are scored on must alone (the 0.2 slice follows must).
        var shouldRatio = task.Should.Count == 0 ? mustRatio : (double)shouldHit / task.Should.Count;
        return new EvalScoreResult(mustHit, task.Must.Count, shouldHit, task.Should.Count,
            MustWeight * mustRatio + ShouldWeight * shouldRatio);
    }

    private static bool Hits(string text, string keyword) =>
        !string.IsNullOrWhiteSpace(keyword) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Loads eval/tasks.json from the repository root (walks up from the test binary).</summary>
internal static class EvalTaskSet
{
    internal static string ResolveTasksPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cozo.DotNet.LlmWiki.slnx")))
            {
                return Path.Combine(directory.FullName, "eval", "tasks.json");
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("could not locate the llm-wiki repository root from " + AppContext.BaseDirectory);
    }

    internal static IReadOnlyList<EvalTask> Load(string path)
    {
        var rootNode = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException("eval/tasks.json should be a JSON object");
        var tasksNode = rootNode["tasks"] as JsonArray
            ?? throw new InvalidOperationException("eval/tasks.json should carry a tasks array");
        return tasksNode.Select(node =>
        {
            var task = node!.AsObject();
            return new EvalTask(
                Str(task, "id"),
                Str(task, "question"),
                Str(task, "difficulty"),
                Str(task, "workRoot"),
                (task["tools"] as JsonArray ?? []).Select(tool => new EvalToolCall(
                    Str(tool!.AsObject(), "name"),
                    tool["arguments"]?.AsObject() ?? [])).ToArray(),
                Strings(task, "must"),
                Strings(task, "should"));
        }).ToArray();
    }

    private static string Str(JsonObject node, string key) => node[key]?.GetValue<string>() ?? "";

    private static IReadOnlyList<string> Strings(JsonObject node, string key) =>
        (node[key] as JsonArray ?? []).Select(item => item?.GetValue<string>() ?? "").ToArray();
}

/// <summary>
/// Eval baseline T1.1 (track add-llm-wiki-eval-baseline) — delta llm-wiki-tools
/// requirements/eval-baseline cases task-set-shape and scorer-discriminates.
/// The oracle-gate case (tool-sequence direct answering, must hit rate >= 90%) is P2.
/// </summary>
internal static class EvalBaselineTests
{
    private static readonly string[] AllowedDifficulties = ["easy", "medium", "hard"];

    internal static Task RunAsync(Action<bool, string> assert)
    {
        // --- task-set-shape: eval/tasks.json loads with >= 20 well-formed tasks. ---
        var tasksPath = EvalTaskSet.ResolveTasksPath();
        assert(File.Exists(tasksPath), "eval/tasks.json should exist at the repository root eval/ directory");
        var tasks = EvalTaskSet.Load(tasksPath);
        assert(tasks.Count >= 20, $"eval task set should contain at least 20 tasks (got {tasks.Count})");
        assert(tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() == tasks.Count,
            "eval task ids should be unique");

        var knownToolNames = LlmWikiToolRunner.ToolsJson()
            .Select(tool => tool?["name"]?.GetValue<string>() ?? "")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            assert(!string.IsNullOrWhiteSpace(task.Id), "every eval task should carry a non-empty id");
            assert(!string.IsNullOrWhiteSpace(task.Question), $"task {task.Id} should carry a non-empty question");
            assert(AllowedDifficulties.Contains(task.Difficulty),
                $"task {task.Id} difficulty should be easy|medium|hard (got '{task.Difficulty}')");
            assert(!string.IsNullOrWhiteSpace(task.WorkRoot), $"task {task.Id} should name its workRoot");
            assert(task.Tools.Count > 0, $"task {task.Id} should suggest at least one tool call");
            assert(task.Tools.All(tool => knownToolNames.Contains(tool.Name)),
                $"task {task.Id} tool names should all belong to the shared tool matrix (got: "
                + string.Join(", ", task.Tools.Select(t => t.Name)) + ")");
            assert(task.Must.Count > 0 && task.Must.All(keyword => !string.IsNullOrWhiteSpace(keyword)),
                $"task {task.Id} should carry non-empty must keywords");
        }

        // The set should exercise the graph across question families, not one tool only.
        var usedTools = tasks.SelectMany(t => t.Tools.Select(tool => tool.Name)).Distinct().ToArray();
        assert(usedTools.Length >= 6, "the task set should span at least six distinct tools (got: "
            + string.Join(", ", usedTools) + ")");

        // --- scorer-discriminates: full answer scores 1.0, empty answer 0.0, partial in between. ---
        var probe = new EvalTask(
            "probe", "who calls RepositoryIndexer?", "easy", "cozo-lib-dotnet-llm-wiki",
            [new EvalToolCall("semantic_search", new JsonObject { ["query"] = "RepositoryIndexer" })],
            Must: ["LlmWikiToolRunner", "RepositoryIndexer"],
            Should: ["McpServer"]);

        var full = EvalScorer.Score(
            "LlmWikiToolRunner.CallAsync(index_repo) and the McpServer Program call RepositoryIndexer.IndexAsync.", probe);
        assert(full is { MustHit: 2, MustTotal: 2, ShouldHit: 1 } && Math.Abs(full.Score - 1.0) < 1e-9,
            $"a full answer should hit every must keyword and score 1.0 (got {full})");

        var empty = EvalScorer.Score("", probe);
        assert(empty is { MustHit: 0, ShouldHit: 0 } && empty.Score == 0.0,
            $"an empty answer should score exactly 0.0 (got {empty})");

        var partial = EvalScorer.Score("the indexing lives in RepositoryIndexer.cs somewhere", probe);
        assert(partial.MustHit == 1 && partial.Score > 0.0 && partial.Score < full.Score,
            $"a partial answer should land strictly between empty and full (got {partial})");

        // Case-insensitive matching: keyword casing must not matter.
        var lowered = EvalScorer.Score("llmwikitoolrunner and repositoryindexer via mcpserver", probe);
        assert(Math.Abs(lowered.Score - 1.0) < 1e-9, "keyword matching should be case-insensitive");

        // Tasks without should keywords score on must alone and still reach 1.0.
        var mustOnly = probe with { Should = [] };
        assert(Math.Abs(EvalScorer.Score("LlmWikiToolRunner uses RepositoryIndexer", mustOnly).Score - 1.0) < 1e-9,
            "a task without should keywords should score 1.0 on full must coverage");

        // Every real task must be discriminative under the scorer: empty answer 0, own keywords full.
        foreach (var task in tasks)
        {
            assert(EvalScorer.Score("", task).Score == 0.0, $"task {task.Id} should score 0.0 on an empty answer");
            var selfAnswer = string.Join(" ", task.Must.Concat(task.Should));
            assert(Math.Abs(EvalScorer.Score(selfAnswer, task).Score - 1.0) < 1e-9,
                $"task {task.Id} should score 1.0 on an answer containing all its keywords");
        }

        return Task.CompletedTask;
    }
}
