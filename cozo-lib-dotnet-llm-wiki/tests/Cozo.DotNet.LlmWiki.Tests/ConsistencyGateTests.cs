using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Core;
using Cozo.DotNet.LlmWiki.Indexing;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>
/// Full-table consistency gate + derived-layers freshness (add-llm-wiki-incremental-indexing
/// track T2.1). The DumpFacts helper canonically serializes every observed and derived ck_*
/// table (timestamp columns excluded; ck_meta reduced to schema_version) so that the state
/// after N incremental rounds can be asserted row-for-row equal to a fresh full index of the
/// same working tree. Two fixtures cover both diff environments: a git repository and a plain
/// directory (per the T1.1 ruling the diff source is always the ck_file hash baseline, so
/// "two paths" means git vs non-git surroundings). Each fixture runs three consecutive
/// incremental rounds — change, remove, add — and the gate runs after every round.
/// </summary>
internal static class ConsistencyGateTests
{
    /// <summary>
    /// Table dumps for the consistency gate: query text per table, first column(s) form the
    /// row identity. updated_at (ck_file/ck_doc_block) is a wall-clock scan timestamp and is
    /// excluded; every other column participates. ck_meta compares schema_version only —
    /// indexed_commit is expected to advance with the fixture's git history.
    /// </summary>
    private static readonly (string Table, string Query)[] DumpQueries =
    [
        ("ck_repo", "?[repo_id, root_path, name, commit] := *ck_repo{ repo_id, root_path, name, commit }"),
        ("ck_file", "?[file_id, repo_id, path, language, hash] := *ck_file{ file_id, repo_id, path, language, hash }"),
        ("ck_symbol", """
            ?[symbol_id, file_id, name, kind, start_line, end_line, signature, parent_id, lang, visibility, exported, sym_key, doc_id, resolver] :=
              *ck_symbol{ symbol_id, file_id, name, kind, start_line, end_line, signature, parent_id, lang, visibility, exported, sym_key, doc_id, resolver }
            """),
        ("ck_edge", """
            ?[from_id, to_id, kind, file_id, line, confidence, resolver, evidence] :=
              *ck_edge{ from_id, to_id, kind, file_id, line, confidence, resolver, evidence }
            """),
        ("ck_doc_block", "?[doc_id, file_id, anchor, text, hash] := *ck_doc_block{ doc_id, file_id, anchor, text, hash }"),
        ("ck_entry_point", "?[symbol_id, kind, metadata] := *ck_entry_point{ symbol_id, kind, metadata }"),
        ("ck_external_call", """
            ?[caller_id, target_key, count, category, first_file_id, first_line, resolver] :=
              *ck_external_call{ caller_id, target_key, count, category, first_file_id, first_line, resolver }
            """),
        ("ck_community", "?[community_id, label, cohesion, symbol_count, algo] := *ck_community{ community_id, label, cohesion, symbol_count, algo }"),
        ("ck_member", "?[symbol_id, community_id] := *ck_member{ symbol_id, community_id }"),
        ("ck_process", """
            ?[process_id, name, entry_symbol_id, entry_kind, process_type, step_count] :=
              *ck_process{ process_id, name, entry_symbol_id, entry_kind, process_type, step_count }
            """),
        ("ck_process_step", "?[process_id, step, symbol_id, via_kind] := *ck_process_step{ process_id, step, symbol_id, via_kind }"),
        ("ck_search_text", "?[source_id, source_kind, text] := *ck_search_text{ source_id, source_kind, text }"),
        ("ck_diagnostic", "?[diagnostic_id, target_id, kind, message, severity] := *ck_diagnostic{ diagnostic_id, target_id, kind, message, severity }"),
        ("ck_meta", "?[key, value] := *ck_meta{ key, value }, key = \"schema_version\""),
    ];

    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"llm-wiki-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await RunScenarioAsync(assert, Path.Combine(root, "gitrepo"), useGit: true, repoId: "repo:gate-git");
            await RunScenarioAsync(assert, Path.Combine(root, "plain"), useGit: false, repoId: "repo:gate-plain");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Three consecutive incremental rounds (change -> remove -> add) with the full-table gate
    /// after each round, then the derived-layers-fresh assertions on the final state.
    /// </summary>
    private static async Task RunScenarioAsync(Action<bool, string> assert, string repoRoot, bool useGit, string repoId)
    {
        var tag = useGit ? "git" : "plain";
        Directory.CreateDirectory(repoRoot);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "Alpha.cs"), """
        namespace Gate;

        public class AlphaService
        {
            public void Drive()
            {
                new BetaService().Run();
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "Beta.cs"), """
        namespace Gate;

        public class BetaService
        {
            public void Run()
            {
                System.Console.WriteLine("run");
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "README.md"), """
        # Gate

        AlphaService drives BetaService in this consistency fixture.
        """);
        if (useGit)
        {
            Git(repoRoot, "init", "-q");
            Commit(repoRoot, "initial");
        }

        var request = new RepositoryIndexRequest(repoRoot, RepositoryId: repoId, RepositoryName: $"gate-{tag}", UseGitIgnore: false);
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        var indexer = new RepositoryIndexer();

        var bootstrap = await indexer.IndexAsync(om, request);
        assert(!bootstrap.IncrementalUsed, $"[{tag}] bootstrap should take the full path");

        // Round 1 (change): rename Beta's method.
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "Beta.cs"), """
        namespace Gate;

        public class BetaService
        {
            public void RunFaster()
            {
                System.Console.WriteLine("run faster");
            }
        }
        """);
        if (useGit)
        {
            Commit(repoRoot, "round1 change");
        }

        var round1 = await indexer.IndexAsync(om, request);
        assert(round1.IncrementalUsed && round1.ChangedFiles == 1 && round1.RemovedFiles == 0,
            $"[{tag}] round1 should be incremental with 1 changed file (changed={round1.ChangedFiles}, removed={round1.RemovedFiles})");
        await AssertConsistentWithFreshFullAsync(assert, om, request, $"{tag}/round1-change");

        // Round 2 (remove): delete Alpha.cs.
        File.Delete(Path.Combine(repoRoot, "Alpha.cs"));
        if (useGit)
        {
            Commit(repoRoot, "round2 remove");
        }

        var round2 = await indexer.IndexAsync(om, request);
        assert(round2.IncrementalUsed && round2.RemovedFiles == 1 && round2.ChangedFiles == 0,
            $"[{tag}] round2 should be incremental with 1 removed file (changed={round2.ChangedFiles}, removed={round2.RemovedFiles})");
        await AssertConsistentWithFreshFullAsync(assert, om, request, $"{tag}/round2-remove");

        // Round 3 (add): new Gamma.cs calling into the surviving Beta.
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "Gamma.cs"), """
        namespace Gate;

        public class GammaService
        {
            public void LaunchSequence()
            {
                new BetaService().RunFaster();
            }
        }
        """);
        if (useGit)
        {
            Commit(repoRoot, "round3 add");
        }

        var round3 = await indexer.IndexAsync(om, request);
        assert(round3.IncrementalUsed && round3.ChangedFiles == 1 && round3.RemovedFiles == 0,
            $"[{tag}] round3 should be incremental with 1 added file (changed={round3.ChangedFiles}, removed={round3.RemovedFiles})");
        await AssertConsistentWithFreshFullAsync(assert, om, request, $"{tag}/round3-add");

        // Round 4 (rename = remove + add in the same round, the git-mv shape): Beta.cs moves to
        // Renamed.cs with identical content, while the unchanged Gamma.cs owns CALLS edges into
        // the moving symbols — the hardest cross-file case for the edge-endpoint expansion.
        File.Move(Path.Combine(repoRoot, "Beta.cs"), Path.Combine(repoRoot, "Renamed.cs"));
        if (useGit)
        {
            Commit(repoRoot, "round4 rename");
        }

        var round4 = await indexer.IndexAsync(om, request);
        assert(round4.IncrementalUsed && round4.ChangedFiles == 1 && round4.RemovedFiles == 1,
            $"[{tag}] round4 rename should count 1 added + 1 removed (changed={round4.ChangedFiles}, removed={round4.RemovedFiles})");
        await AssertConsistentWithFreshFullAsync(assert, om, request, $"{tag}/round4-rename");

        if (useGit)
        {
            // Incremental runs must keep maintaining ck_meta.indexed_commit (detect_changes /
            // staleness semantics): after the round it equals the fixture's current HEAD.
            var indexedCommit = await QueryColumnAsync(om,
                "?[value] := *ck_meta{ key, value }, key = \"indexed_commit\"");
            var head = GitOutput(repoRoot, "rev-parse", "HEAD");
            assert(indexedCommit.Count == 1 && indexedCommit.Contains(head),
                $"[{tag}] incremental runs should update indexed_commit to HEAD (got {string.Join(",", indexedCommit)} vs {head})");
        }

        // --- derived-layers-fresh: the tail recomputation must reflect the new graph ----------
        // code-kind (symbol) rows only: the README doc block legitimately keeps mentioning AlphaService.
        var searchTexts = await QueryColumnAsync(om,
            "?[text] := *ck_search_text{ source_kind, text }, source_kind = \"code\"");
        assert(searchTexts.Any(text => text.Contains("LaunchSequence", StringComparison.Ordinal)),
            $"[{tag}] the incremental round's new symbol should appear in ck_search_text");
        assert(!searchTexts.Any(text => text.Contains("AlphaService", StringComparison.Ordinal)),
            $"[{tag}] the removed file's symbols should be gone from ck_search_text");

        var liveSymbols = await QueryColumnAsync(om, "?[symbol_id] := *ck_symbol{ symbol_id }");
        var memberSymbols = await QueryColumnAsync(om, "?[symbol_id] := *ck_member{ symbol_id }");
        assert(memberSymbols.IsSubsetOf(liveSymbols),
            $"[{tag}] ck_member must only reference live symbols (stale: {string.Join(", ", memberSymbols.Except(liveSymbols).Take(3))})");
        var processSymbols = await QueryColumnAsync(om, "?[symbol_id] := *ck_process_step{ symbol_id }");
        assert(processSymbols.IsSubsetOf(liveSymbols),
            $"[{tag}] ck_process_step must only reference live symbols (stale: {string.Join(", ", processSymbols.Except(liveSymbols).Take(3))})");
        var entrySymbols = await QueryColumnAsync(om, "?[symbol_id] := *ck_entry_point{ symbol_id }");
        assert(entrySymbols.IsSubsetOf(liveSymbols),
            $"[{tag}] ck_entry_point must only reference live symbols (stale: {string.Join(", ", entrySymbols.Except(liveSymbols).Take(3))})");
        var gammaEntryOrProcess = entrySymbols.Concat(processSymbols)
            .Any(id => id.Contains(":Gamma.cs:", StringComparison.Ordinal));
        assert(gammaEntryOrProcess,
            $"[{tag}] the new public symbol should surface in the execution-flow layer (entry points/process steps)");
    }

    /// <summary>
    /// The gate itself: dump every ck_* table from the incrementally maintained database and
    /// from a brand-new full index of the same working tree, and assert per-table equality.
    /// </summary>
    private static async Task AssertConsistentWithFreshFullAsync(
        Action<bool, string> assert,
        CozoOm incrementalOm,
        RepositoryIndexRequest request,
        string label)
    {
        using var fullDb = new CozoDb("mem", "");
        var fullOm = new CozoOm(fullDb);
        var summary = await new RepositoryIndexer().IndexAsync(fullOm, request with { IncrementalMode = "full" });
        assert(!summary.IncrementalUsed, $"[{label}] the reference index must take the full path");

        var incremental = await DumpFactsAsync(incrementalOm);
        var full = await DumpFactsAsync(fullOm);
        foreach (var (table, _) in DumpQueries)
        {
            var left = incremental[table];
            var right = full[table];
            assert(left.SetEquals(right),
                $"[{label}] {table} must equal a fresh full index "
                + $"(incremental-only: {Sample(left, right)}; full-only: {Sample(right, left)})");
        }
    }

    /// <summary>DumpFacts helper (design.md §3): canonical row set per ck_* table.</summary>
    private static async Task<Dictionary<string, HashSet<string>>> DumpFactsAsync(CozoOm om)
    {
        var dump = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (table, query) in DumpQueries)
        {
            var result = await om.Runtime.Store.RunAsync(query);
            var rows = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in result.Rows)
            {
                var builder = new StringBuilder();
                foreach (var cell in row)
                {
                    builder.Append(CanonicalCell(cell)).Append('');
                }

                rows.Add(builder.ToString());
            }

            dump[table] = rows;
        }

        return dump;
    }

    private static string CanonicalCell(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.String => cell.GetString()!,
        // Doubles (confidence/cohesion) go through the round-trip format so 0.6 and 0.60 agree.
        JsonValueKind.Number => cell.TryGetInt64(out var integer)
            ? integer.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : cell.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => cell.GetRawText(),
    };

    private static string Sample(HashSet<string> left, HashSet<string> right)
    {
        var diff = left.Except(right).Take(2)
            .Select(row => row.Replace('', '|'))
            .Select(row => row.Length > 160 ? row[..160] + "…" : row);
        return string.Join(" ;; ", diff);
    }

    private static async Task<HashSet<string>> QueryColumnAsync(CozoOm om, string query)
    {
        var result = await om.Runtime.Store.RunAsync(query);
        return result.Rows
            .Select(row => row[0].ValueKind == JsonValueKind.String ? row[0].GetString()! : row[0].ToString())
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void Commit(string workDir, string message)
    {
        Git(workDir, "add", "-A");
        Git(workDir, "commit", "-q", "-m", message);
    }

    private static string GitOutput(string workDir, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("failed to start git");
        var stdOut = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);
        return stdOut.Trim();
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
