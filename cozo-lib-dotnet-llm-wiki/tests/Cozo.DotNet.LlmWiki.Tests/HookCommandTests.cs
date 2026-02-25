using System.Diagnostics;
using System.Text.Json.Nodes;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Core;
using Cozo.DotNet.LlmWiki.Indexing;
using Cozo.DotNet.LlmWiki.McpServer;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>
/// Claude Code agent hook subcommands (add-llm-wiki-agent-hooks track T1.1).
/// Delta cases covered here: augment-enriches, augment-budget, silent-degrade, staleness-hint.
/// Handlers are called directly (CLI shell is wiring only); the subprocess smoke lives in P2.
/// </summary>
internal static class HookCommandTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"llm-wiki-hook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // --- augment fixture: sqlite CodeKnowledge db under <hookRoot>/.depa-wiki ----------
            // Synthetic facts pin the enrichment chain deterministically: CALLS callers/callees
            // at mixed confidences plus one execution flow (same shape the indexer produces).
            var hookRoot = Path.Combine(root, "hookrepo");
            Directory.CreateDirectory(Path.Combine(hookRoot, ".depa-wiki"));
            using (var db = new CozoDb("sqlite", Path.Combine(hookRoot, ".depa-wiki", "depa-wiki.db")))
            {
                var om = new CozoOm(db);
                await om.InitCodeKnowledgeAsync();
                await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                    Files: [new CodeFileFact("file:hk", "repo:hk", "src/Hook.cs")],
                    Symbols:
                    [
                        new CodeSymbolFact("symbol:hk:a", "file:hk", "HookAlpha", "method", 1, 10, "HookAlpha()"),
                        new CodeSymbolFact("symbol:hk:b", "file:hk", "HookBeta", "method", 11, 20, "HookBeta()"),
                        new CodeSymbolFact("symbol:hk:c", "file:hk", "HookCaller", "method", 21, 30, "HookCaller()"),
                        new CodeSymbolFact("symbol:hk:d", "file:hk", "HookCallee", "method", 31, 40, "HookCallee()"),
                        new CodeSymbolFact("symbol:hk:w", "file:hk", "HookWeak", "method", 41, 50, "HookWeak()")
                    ],
                    Edges:
                    [
                        new CodeEdgeFact("symbol:hk:c", "symbol:hk:a", CodeEdgeKinds.Calls, "file:hk", 25, 0.9, "roslyn", "caller"),
                        new CodeEdgeFact("symbol:hk:w", "symbol:hk:a", CodeEdgeKinds.Calls, "file:hk", 45, 0.5, "treesitter", "weak caller"),
                        new CodeEdgeFact("symbol:hk:a", "symbol:hk:d", CodeEdgeKinds.Calls, "file:hk", 5, 0.9, "roslyn", "callee")
                    ]));
                using (db.Run(
                    """
                    ?[process_id, name, entry_symbol_id, entry_kind, process_type, step_count] <- [
                      ["process:hk1", "HookFlow", "symbol:hk:a", "public_api", "public_api", 2]]
                    :put ck_process {process_id => name, entry_symbol_id, entry_kind, process_type, step_count}
                    """)) { }
                using (db.Run(
                    """
                    ?[process_id, step, symbol_id, via_kind] <- [
                      ["process:hk1", 0, "symbol:hk:a", ""], ["process:hk1", 1, "symbol:hk:d", "CALLS"]]
                    :put ck_process_step {process_id, step => symbol_id, via_kind}
                    """)) { }
            }

            // augment-enriches: Grep PostToolUse JSON (object-shaped tool_response) whose hit line
            // falls inside HookAlpha's [1,10] range — the enrichment names the containing symbol
            // with callers (confidence >= 0.7 only), callees and execution flows.
            var grepInput = new JsonObject
            {
                ["tool_name"] = "Grep",
                ["tool_input"] = new JsonObject { ["pattern"] = "HookAlpha" },
                ["tool_response"] = new JsonObject
                {
                    ["mode"] = "content",
                    ["content"] = "src/Hook.cs:5: public void HookAlpha()\n/somewhere/else/Unindexed.cs:3: noise",
                },
            }.ToJsonString();
            var enriched = await RunHookAsync(["augment", "--work-dir", hookRoot], grepInput);
            assert(enriched.Exit == 0, "hook augment should exit 0 on success");
            assert(enriched.Err.Length == 0, "hook augment success path should keep stderr empty");
            var enrichedContext = AdditionalContext(enriched.Out, "PostToolUse", assert);
            assert(enrichedContext.StartsWith("Graph context (depa-wiki):", StringComparison.Ordinal),
                "augment context should carry the Graph context prefix (got: " + enrichedContext + ")");
            assert(enrichedContext.Contains("HookAlpha (src/Hook.cs:1)", StringComparison.Ordinal),
                "a hit line inside a symbol range should enrich that symbol with its path:line (got: " + enrichedContext + ")");
            assert(enrichedContext.Contains("callers: HookCaller", StringComparison.Ordinal),
                "augment should list CALLS callers by name (got: " + enrichedContext + ")");
            assert(!enrichedContext.Contains("HookWeak", StringComparison.Ordinal),
                "callers below confidence 0.7 should be excluded (got: " + enrichedContext + ")");
            assert(enrichedContext.Contains("callees: HookCallee", StringComparison.Ordinal),
                "augment should list CALLS callees by name (got: " + enrichedContext + ")");
            assert(enrichedContext.Contains("processes: HookFlow", StringComparison.Ordinal),
                "augment should list the execution flows the symbol participates in (got: " + enrichedContext + ")");
            assert(!enrichedContext.Contains("Unindexed.cs", StringComparison.Ordinal),
                "hits outside the index should not be enriched");

            // Subprocess smoke (T2.1): the same PostToolUse JSON through the real depa-wiki
            // binary — stdin pipe in, JSON on stdout, exit 0 — proving the CLI wiring end-to-end.
            var smoke = await RunHookSubprocessAsync(["hook", "augment", "--work-dir", hookRoot], grepInput);
            assert(smoke.Exit == 0, "subprocess hook augment should exit 0 (stderr: " + smoke.Err + ")");
            var smokeContext = AdditionalContext(smoke.Out, "PostToolUse", assert);
            assert(smokeContext.Contains("HookAlpha", StringComparison.Ordinal),
                "subprocess hook augment should enrich through real stdin->stdout (got: " + smokeContext + ")");

            // tool_response shape compatibility: a plain-string response (Glob file list, no line)
            // still enriches — the file's top-degree symbol is chosen when no line is given.
            var globInput = new JsonObject
            {
                ["tool_name"] = "Glob",
                ["tool_response"] = "src/Hook.cs\n",
            }.ToJsonString();
            var globEnriched = await RunHookAsync(["augment", "--work-dir", hookRoot], globInput);
            assert(globEnriched.Exit == 0, "hook augment (Glob) should exit 0");
            var globContext = AdditionalContext(globEnriched.Out, "PostToolUse", assert);
            assert(globContext.Contains("HookAlpha", StringComparison.Ordinal),
                "a line-less hit should enrich the file's top-degree symbol (got: " + globContext + ")");

            // augment-budget: a tight --budget truncates within the budget and marks the cut.
            var budgeted = await RunHookAsync(["augment", "--work-dir", hookRoot, "--budget", "60"], grepInput);
            assert(budgeted.Exit == 0, "hook augment with budget should exit 0");
            var budgetedContext = AdditionalContext(budgeted.Out, "PostToolUse", assert);
            assert(budgetedContext.Length <= 60,
                $"augment context should stay within the character budget (got {budgetedContext.Length} chars)");
            assert(budgetedContext.EndsWith("…(truncated)", StringComparison.Ordinal),
                "a truncated augment context should carry the truncation marker (got: " + budgetedContext + ")");

            // silent-degrade #1: no .depa-wiki library — empty stdout, exit 0, for both subcommands.
            var bareDir = Path.Combine(root, "bare");
            Directory.CreateDirectory(bareDir);
            var noDbAugment = await RunHookAsync(["augment", "--work-dir", bareDir], grepInput);
            assert(noDbAugment is { Exit: 0, Out.Length: 0 },
                "augment without a .depa-wiki library should emit nothing and exit 0");
            var noDbStaleness = await RunHookAsync(["staleness", "--work-dir", bareDir], "");
            assert(noDbStaleness is { Exit: 0, Out.Length: 0 },
                "staleness without a .depa-wiki library should emit nothing and exit 0");

            // silent-degrade #2: broken stdin JSON — diagnostics to stderr only, empty stdout, exit 0.
            var badJson = await RunHookAsync(["augment", "--work-dir", hookRoot], "{not json");
            assert(badJson is { Exit: 0, Out.Length: 0 },
                "augment with broken JSON should emit nothing and exit 0");
            assert(badJson.Err.Length > 0, "augment with broken JSON should write a diagnostic to stderr");

            // silent-degrade #3: unsupported tool_name — empty stdout, exit 0.
            var unsupported = await RunHookAsync(
                ["augment", "--work-dir", hookRoot],
                new JsonObject { ["tool_name"] = "Read", ["tool_response"] = "src/Hook.cs" }.ToJsonString());
            assert(unsupported is { Exit: 0, Out.Length: 0 },
                "augment for a tool outside Grep|Glob should emit nothing and exit 0");

            // --- staleness fixture: real git repo indexed at HEAD, then a new commit ------------
            var gitRoot = Path.Combine(root, "gitrepo");
            Directory.CreateDirectory(gitRoot);
            await File.WriteAllTextAsync(Path.Combine(gitRoot, "Alpha.cs"), """
            namespace HookStale;

            public class AlphaService
            {
                public string Run() => "v1";
            }
            """);
            Git(gitRoot, "init", "-q");
            Git(gitRoot, "add", "-A");
            Git(gitRoot, "commit", "-q", "-m", "initial");
            Directory.CreateDirectory(Path.Combine(gitRoot, ".depa-wiki"));
            using (var gitDb = new CozoDb("sqlite", Path.Combine(gitRoot, ".depa-wiki", "depa-wiki.db")))
            {
                await new RepositoryIndexer().IndexAsync(new CozoOm(gitDb), new RepositoryIndexRequest(gitRoot, UseGitIgnore: false));
            }

            // staleness-hint (in-sync half): index == HEAD — empty stdout, exit 0.
            var inSync = await RunHookAsync(["staleness", "--work-dir", gitRoot], "");
            assert(inSync is { Exit: 0, Out.Length: 0 },
                "staleness with index at HEAD should emit nothing (got: " + inSync.Out + ")");

            // staleness-hint (behind half): a new commit after indexing yields the hint + reindex run line.
            await File.WriteAllTextAsync(Path.Combine(gitRoot, "Alpha.cs"), """
            namespace HookStale;

            public class AlphaService
            {
                public string Run() => "v2";
            }
            """);
            Git(gitRoot, "commit", "-q", "-am", "advance HEAD");
            var behind = await RunHookAsync(["staleness", "--work-dir", gitRoot], "");
            assert(behind.Exit == 0, "staleness behind HEAD should still exit 0");
            var staleContext = AdditionalContext(behind.Out, "SessionStart", assert);
            assert(staleContext.Contains("index is behind HEAD", StringComparison.Ordinal),
                "staleness behind HEAD should state the lag (got: " + staleContext + ")");
            assert(staleContext.Contains("Run: depa-wiki index --work-dir " + gitRoot, StringComparison.Ordinal),
                "staleness hint should carry the reindex command (got: " + staleContext + ")");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<(int Exit, string Out, string Err)> RunHookAsync(string[] args, string stdin)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await HookCommands.RunAsync(args, new StringReader(stdin), stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Runs the real depa-wiki apphost (copied next to the tests by the project reference) with
    /// piped stdin/stdout — the one true-subprocess smoke; handler details are covered in-process.
    /// </summary>
    private static async Task<(int Exit, string Out, string Err)> RunHookSubprocessAsync(string[] args, string stdin)
    {
        var cliPath = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "depa-wiki.exe" : "depa-wiki");
        if (!File.Exists(cliPath))
        {
            throw new InvalidOperationException($"depa-wiki apphost not found next to the tests: {cliPath}");
        }

        var info = new ProcessStartInfo(cliPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("failed to start depa-wiki");
        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>Parses a hook stdout line and returns hookSpecificOutput.additionalContext.</summary>
    private static string AdditionalContext(string stdout, string expectedEvent, Action<bool, string> assert)
    {
        assert(stdout.Length > 0, $"hook stdout should carry a {expectedEvent} JSON payload");
        var parsed = JsonNode.Parse(stdout)?.AsObject();
        var specific = parsed?["hookSpecificOutput"]?.AsObject();
        assert(specific?["hookEventName"]?.GetValue<string>() == expectedEvent,
            $"hookSpecificOutput.hookEventName should be {expectedEvent} (got: " + stdout + ")");
        return specific?["additionalContext"]?.GetValue<string>() ?? "";
    }

    private static void Git(string workDir, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("user.name=Test");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("user.email=test@example.com");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("commit.gpgsign=false");
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("failed to start git");
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdErr}");
        }
    }
}
