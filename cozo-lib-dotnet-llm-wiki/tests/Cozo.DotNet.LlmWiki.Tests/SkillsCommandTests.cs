using System.Diagnostics;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.McpServer;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>
/// skills generate/clean/status CLI wiring (add-llm-wiki-skills-generation track T2.1,
/// design §4). The generator semantics (rendering, budget, sync) are pinned in
/// SkillsGeneratorTests; this suite drives the CLI handlers directly at the own-prefix-only
/// case level — user directories zero-touch, clean removes own directories only, repeated
/// generate idempotent — plus one true-subprocess smoke (skills status) through the real
/// depa-wiki apphost.
/// </summary>
internal static class SkillsCommandTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"llm-wiki-skills-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // --- fixture: sqlite CodeKnowledge db under <repo>/.depa-wiki with two communities
            // (namespace labels -> contexts acme-billing / acme-shipping), same shape the
            // indexer + community detection produce.
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".depa-wiki"));
            using (var db = new CozoDb("sqlite", Path.Combine(repo, ".depa-wiki", "depa-wiki.db")))
            {
                var om = new CozoOm(db);
                await om.InitCodeKnowledgeAsync();
                await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                    Files:
                    [
                        new CodeFileFact("file:sc:bil", "repo:sc", "src/Billing.cs"),
                        new CodeFileFact("file:sc:shp", "repo:sc", "src/Shipping.cs")
                    ],
                    Symbols:
                    [
                        new CodeSymbolFact("symbol:sc:inv", "file:sc:bil", "Invoice", "class", 3, 20, "Invoice",
                            SymKey: "csharp:Acme.Billing.Invoice#0"),
                        new CodeSymbolFact("symbol:sc:svc", "file:sc:bil", "InvoiceService", "class", 22, 40, "InvoiceService",
                            SymKey: "csharp:Acme.Billing.InvoiceService#0"),
                        new CodeSymbolFact("symbol:sc:parcel", "file:sc:shp", "Parcel", "class", 3, 20, "Parcel",
                            SymKey: "csharp:Acme.Shipping.Parcel#0"),
                        new CodeSymbolFact("symbol:sc:router", "file:sc:shp", "Router", "class", 22, 40, "Router",
                            SymKey: "csharp:Acme.Shipping.Router#0")
                    ],
                    Edges:
                    [
                        new CodeEdgeFact("symbol:sc:svc", "symbol:sc:inv", CodeEdgeKinds.Calls, "file:sc:bil", 25, 0.9, "roslyn", ""),
                        new CodeEdgeFact("symbol:sc:router", "symbol:sc:parcel", CodeEdgeKinds.Calls, "file:sc:shp", 25, 0.9, "roslyn", "")
                    ]));
                using (db.Run(
                    """
                    ?[community_id, label, cohesion, symbol_count, algo] <- [
                      ["community:sc1", "Acme.Billing", 0.8, 2, "louvain"],
                      ["community:sc2", "Acme.Shipping", 0.7, 2, "louvain"]]
                    :put ck_community {community_id => label, cohesion, symbol_count, algo}
                    """)) { }
                using (db.Run(
                    """
                    ?[symbol_id, community_id] <- [
                      ["symbol:sc:inv", "community:sc1"], ["symbol:sc:svc", "community:sc1"],
                      ["symbol:sc:parcel", "community:sc2"], ["symbol:sc:router", "community:sc2"]]
                    :put ck_member {symbol_id => community_id}
                    """)) { }
            }

            string[] contextSkills = ["depa-wiki-acme-billing", "depa-wiki-acme-shipping"];
            string[] workflowSkills = ["depa-wiki-depa", "depa-wiki-exploring", "depa-wiki-impact"];
            var ownSkills = contextSkills.Concat(workflowSkills).Order(StringComparer.Ordinal).ToArray();

            // Default target <work-dir>/.claude/skills, pre-seeded with a user skill directory
            // (zero-touch forever) and a stale own-prefix directory (synced away on generate).
            var target = Path.Combine(repo, ".claude", "skills");
            Directory.CreateDirectory(Path.Combine(target, "my-notes"));
            const string userSkillContent = "---\nname: my-notes\n---\nhand-written, do not touch\n";
            await File.WriteAllTextAsync(Path.Combine(target, "my-notes", "SKILL.md"), userSkillContent);
            Directory.CreateDirectory(Path.Combine(target, "depa-wiki-stale"));
            await File.WriteAllTextAsync(Path.Combine(target, "depa-wiki-stale", "SKILL.md"), "stale\n");

            // --- generate: default target dir, own skills written, user dir untouched, stale synced.
            var generate = await RunSkillsAsync(["generate", "--work-dir", repo]);
            assert(generate.Exit == 0, "skills generate should exit 0 (stderr: " + generate.Err + ")");
            var dirs = SkillDirectories(target);
            assert(dirs.SequenceEqual(ownSkills.Concat(["my-notes"]).Order(StringComparer.Ordinal)),
                "generate should write the context + workflow skills into <work-dir>/.claude/skills and keep the "
                + "user directory (got: " + string.Join(", ", dirs) + ")");
            assert(await File.ReadAllTextAsync(Path.Combine(target, "my-notes", "SKILL.md")) == userSkillContent,
                "generate must keep a non-own-prefix user skill directory byte-identical");
            assert(!Directory.Exists(Path.Combine(target, "depa-wiki-stale")),
                "generate must sync away an own-prefix directory whose context no longer exists");
            assert(generate.Out.Contains(target, StringComparison.Ordinal),
                "generate should name the target directory (got: " + generate.Out + ")");
            assert(generate.Out.Contains("depa-wiki-acme-billing", StringComparison.Ordinal)
                    && generate.Out.Contains("depa-wiki-stale", StringComparison.Ordinal),
                "generate should report both written and cleaned skill directories (got: " + generate.Out + ")");

            // --- idempotence: a second generate rewrites own skills in place, cleans nothing new.
            var billingFirst = await File.ReadAllTextAsync(Path.Combine(target, "depa-wiki-acme-billing", "SKILL.md"));
            var regenerate = await RunSkillsAsync(["generate", "--work-dir", repo]);
            assert(regenerate.Exit == 0, "repeated skills generate should exit 0");
            assert(SkillDirectories(target).SequenceEqual(dirs),
                "repeated generate should leave the same directory set");
            assert(await File.ReadAllTextAsync(Path.Combine(target, "depa-wiki-acme-billing", "SKILL.md")) == billingFirst,
                "repeated generate should be byte-identical for own skills");
            assert(await File.ReadAllTextAsync(Path.Combine(target, "my-notes", "SKILL.md")) == userSkillContent,
                "the user skill directory must survive repeated generates untouched");

            // --- status: names the target and lists own directories only.
            var status = await RunSkillsAsync(["status", "--work-dir", repo]);
            assert(status.Exit == 0, "skills status should exit 0");
            assert(status.Out.Contains(target, StringComparison.Ordinal),
                "status should name the target directory (got: " + status.Out + ")");
            assert(ownSkills.All(skill => status.Out.Contains(skill, StringComparison.Ordinal)),
                "status should list every own skill directory (got: " + status.Out + ")");
            assert(!status.Out.Contains("my-notes", StringComparison.Ordinal),
                "status must not claim user skill directories (got: " + status.Out + ")");

            // --- --target-dir / --max-skills / --budget flow through to the generator.
            var customTarget = Path.Combine(root, "custom-skills");
            var custom = await RunSkillsAsync([
                "generate", "--work-dir", repo, "--target-dir", customTarget, "--max-skills", "1", "--budget", "400"]);
            assert(custom.Exit == 0, "skills generate with explicit options should exit 0 (stderr: " + custom.Err + ")");
            var customDirs = SkillDirectories(customTarget);
            assert(customDirs.SequenceEqual(new[] { "depa-wiki-acme-billing" }.Concat(workflowSkills).Order(StringComparer.Ordinal)),
                "--max-skills 1 should keep one context skill plus the three workflow skills (got: "
                + string.Join(", ", customDirs) + ")");
            foreach (var skill in customDirs)
            {
                var content = await File.ReadAllTextAsync(Path.Combine(customTarget, skill, "SKILL.md"));
                assert(content.Length <= 400, $"--budget 400 should bound {skill}/SKILL.md (got {content.Length} chars)");
            }

            // --- clean: removes own directories only, user directory stays; idempotent.
            var clean = await RunSkillsAsync(["clean", "--work-dir", repo]);
            assert(clean.Exit == 0, "skills clean should exit 0");
            assert(SkillDirectories(target).SequenceEqual(["my-notes"]),
                "clean should remove every depa-wiki-* directory and nothing else (got: "
                + string.Join(", ", SkillDirectories(target)) + ")");
            assert(await File.ReadAllTextAsync(Path.Combine(target, "my-notes", "SKILL.md")) == userSkillContent,
                "clean must keep the user skill directory byte-identical");
            assert(ownSkills.All(skill => clean.Out.Contains(skill, StringComparison.Ordinal)),
                "clean should report the removed own directories (got: " + clean.Out + ")");
            var recleaned = await RunSkillsAsync(["clean", "--work-dir", repo]);
            assert(recleaned.Exit == 0 && SkillDirectories(target).SequenceEqual(["my-notes"]),
                "repeated skills clean should be an idempotent no-op");

            // status on a missing target directory still exits 0 and says so.
            var bareStatus = await RunSkillsAsync(["status", "--work-dir", Path.Combine(root, "nowhere")]);
            assert(bareStatus.Exit == 0 && bareStatus.Out.Contains("missing", StringComparison.Ordinal),
                "status without a skills directory should exit 0 and flag it as missing (got: " + bareStatus.Out + ")");

            // Unknown subcommand: usage error, exit non-zero.
            var unknown = await RunSkillsAsync(["frobnicate"]);
            assert(unknown.Exit != 0 && unknown.Err.Contains("Usage", StringComparison.Ordinal),
                "an unknown skills subcommand should exit non-zero with usage help");

            // --- subprocess smoke: skills status through the real depa-wiki apphost.
            var smoke = await RunSkillsSubprocessAsync(["skills", "status", "--work-dir", repo]);
            assert(smoke.Exit == 0, "subprocess skills status should exit 0 (stderr: " + smoke.Err + ")");
            assert(smoke.Out.Contains(target, StringComparison.Ordinal),
                "subprocess skills status should name the target directory (got: " + smoke.Out + ")");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string[] SkillDirectories(string target) =>
        Directory.Exists(target)
            ? Directory.GetDirectories(target)
                .Select(dir => Path.GetFileName(dir)!)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    private static async Task<(int Exit, string Out, string Err)> RunSkillsAsync(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await SkillsCommands.RunAsync(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Runs the real depa-wiki apphost (copied next to the tests by the project reference).</summary>
    private static async Task<(int Exit, string Out, string Err)> RunSkillsSubprocessAsync(string[] args)
    {
        var cliPath = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "depa-wiki.exe" : "depa-wiki");
        if (!File.Exists(cliPath))
        {
            throw new InvalidOperationException($"depa-wiki apphost not found next to the tests: {cliPath}");
        }

        var info = new ProcessStartInfo(cliPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("failed to start depa-wiki");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        return (process.ExitCode, stdout, stderr);
    }
}
