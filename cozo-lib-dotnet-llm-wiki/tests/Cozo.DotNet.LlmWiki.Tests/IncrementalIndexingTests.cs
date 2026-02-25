using System.Diagnostics;
using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Core;
using Cozo.DotNet.LlmWiki.Indexing;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>
/// File-level incremental indexing (add-llm-wiki-incremental-indexing track T1.1).
/// Delta cases covered here: single-file-change, file-removed, fallback-full (fresh db and
/// non-git-with-baseline hash path), plus the IncrementalMode="full" forced override.
/// consistency-gate / derived-layers-fresh live in T2.1.
/// </summary>
internal static class IncrementalIndexingTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"llm-wiki-incr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // --- git fixture repository -------------------------------------------------------
            var gitRoot = Path.Combine(root, "gitrepo");
            Directory.CreateDirectory(gitRoot);
            await File.WriteAllTextAsync(Path.Combine(gitRoot, "Alpha.cs"), """
            namespace Incr;

            public class AlphaService
            {
                public void Drive()
                {
                    new BetaService().Run();
                }
            }
            """);
            await File.WriteAllTextAsync(Path.Combine(gitRoot, "Beta.cs"), """
            namespace Incr;

            public class BetaService
            {
                public void Run()
                {
                    System.Console.WriteLine("run");
                }
            }
            """);
            await File.WriteAllTextAsync(Path.Combine(gitRoot, "README.md"), """
            # Incr

            AlphaService drives BetaService in this fixture.
            """);
            Git(gitRoot, "init", "-q");
            Git(gitRoot, "add", "-A");
            Git(gitRoot, "commit", "-q", "-m", "initial");

            var request = new RepositoryIndexRequest(gitRoot, RepositoryId: "repo:incr", RepositoryName: "incr", UseGitIgnore: false);

            using var db = new CozoDb("mem", "");
            var om = new CozoOm(db);
            var indexer = new RepositoryIndexer();

            // fallback-full: auto mode with no prior index must take the full path.
            var first = await indexer.IndexAsync(om, request);
            assert(!first.IncrementalUsed, "the bootstrap index (no baseline) should take the full path");
            assert(first.ChangedFiles == 0 && first.RemovedFiles == 0 && first.ReusedFiles == 0,
                "full runs should report zero incremental counters");
            assert(first.Files == 3, $"fixture should index 3 files (got {first.Files})");
            assert(first.Symbols >= 4, $"fixture should index the classes and methods (got {first.Symbols})");
            assert(!string.IsNullOrEmpty(first.Commit), "the git fixture should record a commit");

            // --- single-file-change: modify Beta.cs only --------------------------------------
            await File.WriteAllTextAsync(Path.Combine(gitRoot, "Beta.cs"), """
            namespace Incr;

            public class BetaService
            {
                public void RunFaster()
                {
                    System.Console.WriteLine("run faster");
                }
            }
            """);
            Git(gitRoot, "commit", "-q", "-am", "rename Run to RunFaster");

            var second = await indexer.IndexAsync(om, request);
            assert(second.IncrementalUsed, "auto mode with a baseline should take the incremental path");
            assert(second.ChangedFiles == 1, $"exactly one file changed (got {second.ChangedFiles})");
            assert(second.RemovedFiles == 0, $"no file was removed (got {second.RemovedFiles})");
            assert(second.ReusedFiles == 2, $"the other two files should be reused (got {second.ReusedFiles})");
            assert(second.Files == 3 && second.Symbols == first.Symbols,
                $"incremental summary should keep whole-repo totals (files {second.Files}, symbols {second.Symbols} vs {first.Symbols})");

            var symbolNames = await QueryStringsAsync(om, "?[name] := *ck_symbol{ name }");
            assert(symbolNames.Contains("RunFaster"), "the changed file's new symbol should be indexed");
            assert(!symbolNames.Contains("Run"), "the changed file's old symbol should be gone after the incremental rebuild");

            // Cross-file correctness: the incremental state must equal a fresh full index of the
            // same tree (light per-table comparison; the full DumpFacts gate is T2.1).
            using (var fullDb = new CozoDb("mem", ""))
            {
                var fullOm = new CozoOm(fullDb);
                await new RepositoryIndexer().IndexAsync(fullOm, request with { IncrementalMode = "full" });
                foreach (var (label, query) in new (string, string)[]
                {
                    ("ck_file", "?[x] := *ck_file{ file_id: x }"),
                    ("ck_symbol", "?[x] := *ck_symbol{ symbol_id: x }"),
                    ("ck_edge", "?[x] := *ck_edge{ from_id: f, to_id: t, kind: k, file_id: fi, line: l }, x = concat(f, '|', t, '|', k, '|', fi, '|', to_string(l))"),
                    ("ck_doc_block", "?[x] := *ck_doc_block{ doc_id: x }"),
                    ("ck_external_call", "?[x] := *ck_external_call{ caller_id: c, target_key: t }, x = concat(c, '|', t)"),
                })
                {
                    var incrementalRows = await QueryStringsAsync(om, query);
                    var fullRows = await QueryStringsAsync(fullOm, query);
                    assert(incrementalRows.SetEquals(fullRows),
                        $"incremental {label} rows should equal a fresh full index "
                        + $"(incremental-only: {string.Join(", ", incrementalRows.Except(fullRows).Take(3))}; "
                        + $"full-only: {string.Join(", ", fullRows.Except(incrementalRows).Take(3))})");
                }
            }

            // --- file-removed: delete Beta.cs -------------------------------------------------
            var betaFileId = "file:repo:incr:Beta.cs";
            var callerRows = await QueryStringsAsync(om,
                $"?[x] := *ck_external_call{{ caller_id: x }}");
            assert(callerRows.Any(id => id.Contains(":Beta.cs:", StringComparison.Ordinal)),
                "precondition: Beta.cs should own an external-call row before the removal");

            File.Delete(Path.Combine(gitRoot, "Beta.cs"));
            Git(gitRoot, "commit", "-q", "-am", "remove Beta.cs");

            var third = await indexer.IndexAsync(om, request);
            assert(third.IncrementalUsed, "the removal re-index should take the incremental path");
            assert(third.RemovedFiles == 1, $"exactly one file was removed (got {third.RemovedFiles})");
            assert(third.ChangedFiles == 0, $"no file content changed (got {third.ChangedFiles})");
            assert(third.ReusedFiles == 2, $"the two surviving files should be reused (got {third.ReusedFiles})");
            assert(third.Files == 2, $"summary should report the surviving files (got {third.Files})");

            var fileIds = await QueryStringsAsync(om, "?[x] := *ck_file{ file_id: x }");
            assert(!fileIds.Contains(betaFileId), "the removed file's ck_file row should be gone");
            var betaSymbols = await QueryStringsAsync(om,
                "?[x] := *ck_symbol{ symbol_id: x, file_id }, file_id = $fid", ("fid", betaFileId));
            assert(betaSymbols.Count == 0, "the removed file's ck_symbol rows should be gone");
            var betaEdges = await QueryStringsAsync(om,
                """
                ?[x] := *ck_edge{ from_id: f, to_id: t, file_id }, file_id = $fid, x = concat(f, '|', t)
                ?[x] := *ck_edge{ from_id: f, to_id: t }, starts_with(f, $symprefix), x = concat(f, '|', t)
                ?[x] := *ck_edge{ from_id: f, to_id: t }, starts_with(t, $symprefix), x = concat(f, '|', t)
                """,
                ("fid", betaFileId), ("symprefix", $"symbol:{betaFileId}:"));
            assert(betaEdges.Count == 0,
                $"no ck_edge row may reference the removed file or its symbols (got {string.Join(", ", betaEdges.Take(3))})");
            var betaDocs = await QueryStringsAsync(om,
                "?[x] := *ck_doc_block{ doc_id: x, file_id }, file_id = $fid", ("fid", betaFileId));
            assert(betaDocs.Count == 0, "the removed file's ck_doc_block rows should be gone");
            var betaEntryPoints = await QueryStringsAsync(om,
                "?[x] := *ck_entry_point{ symbol_id: x }, starts_with(x, $symprefix)",
                ("symprefix", $"symbol:{betaFileId}:"));
            assert(betaEntryPoints.Count == 0, "the removed file's ck_entry_point rows should be gone");
            var betaExternalCalls = await QueryStringsAsync(om,
                "?[x] := *ck_external_call{ caller_id: x }, starts_with(x, $symprefix)",
                ("symprefix", $"symbol:{betaFileId}:"));
            assert(betaExternalCalls.Count == 0, "the removed file's ck_external_call rows should be gone");

            // --- IncrementalMode="full" forces the legacy full rewrite ------------------------
            await File.WriteAllTextAsync(Path.Combine(gitRoot, "Alpha.cs"), """
            namespace Incr;

            public class AlphaService
            {
                public void DriveSolo()
                {
                }
            }
            """);
            var forced = await indexer.IndexAsync(om, request with { IncrementalMode = "full" });
            assert(!forced.IncrementalUsed, "IncrementalMode=full should force the full path even with a baseline");
            assert(forced.ChangedFiles == 0 && forced.RemovedFiles == 0 && forced.ReusedFiles == 0,
                "forced full runs should report zero incremental counters");
            var forcedNames = await QueryStringsAsync(om, "?[name] := *ck_symbol{ name }");
            assert(forcedNames.Contains("DriveSolo"), "the forced full run should still pick up new facts");

            // --- fallback-full + hash baseline on a plain (non-git) directory -----------------
            var plainRoot = Path.Combine(root, "plain");
            Directory.CreateDirectory(plainRoot);
            await File.WriteAllTextAsync(Path.Combine(plainRoot, "One.cs"), """
            namespace Plain;
            public class OneService { public void Go() { } }
            """);
            await File.WriteAllTextAsync(Path.Combine(plainRoot, "Two.cs"), """
            namespace Plain;
            public class TwoService { public void Stop() { } }
            """);
            var plainRequest = new RepositoryIndexRequest(plainRoot, RepositoryId: "repo:plain-incr", UseGitIgnore: false);

            using var plainDb = new CozoDb("mem", "");
            var plainOm = new CozoOm(plainDb);
            var plainFirst = await indexer.IndexAsync(plainOm, plainRequest);
            assert(!plainFirst.IncrementalUsed && plainFirst.Files == 2,
                "a non-git directory without a baseline should take the full path (fallback-full)");
            assert(plainFirst.Commit == "", "a non-git directory should record no commit");

            await File.WriteAllTextAsync(Path.Combine(plainRoot, "Two.cs"), """
            namespace Plain;
            public class TwoService { public void StopNow() { } }
            """);
            var plainSecond = await indexer.IndexAsync(plainOm, plainRequest);
            assert(plainSecond.IncrementalUsed, "a non-git directory with a ck_file hash baseline should still go incremental");
            assert(plainSecond.ChangedFiles == 1 && plainSecond.ReusedFiles == 1 && plainSecond.RemovedFiles == 0,
                $"hash-baseline diff should count 1 changed / 1 reused (got changed={plainSecond.ChangedFiles}, reused={plainSecond.ReusedFiles}, removed={plainSecond.RemovedFiles})");
            var plainNames = await QueryStringsAsync(plainOm, "?[name] := *ck_symbol{ name }");
            assert(plainNames.Contains("StopNow") && !plainNames.Contains("Stop"),
                "the hash-baseline incremental rebuild should replace the changed file's symbols");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<HashSet<string>> QueryStringsAsync(CozoOm om, string query, params (string Key, object? Value)[] parameters)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            dict[key] = value;
        }

        var result = await om.Runtime.Store.RunAsync(query, dict);
        return result.Rows
            .Select(row => row[0].ValueKind == JsonValueKind.String ? row[0].GetString()! : row[0].ToString())
            .ToHashSet(StringComparer.Ordinal);
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
