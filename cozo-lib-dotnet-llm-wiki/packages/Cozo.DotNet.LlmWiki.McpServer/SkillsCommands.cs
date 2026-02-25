using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.LlmWiki.Wiki;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.McpServer;

/// <summary>
/// skills generate/clean/status subcommands (add-llm-wiki-skills-generation track T2.1,
/// design §4): pure wiring onto the Wiki package's internal <see cref="SkillsGenerator"/> —
/// every behavior (rendering, budget, sync semantics, own-prefix discipline on generate)
/// lives there. Ownership is the <see cref="SkillsGenerator.OwnPrefix"/> directory-name
/// prefix: clean removes exactly those directories, status lists exactly those, and user
/// skill directories are never touched. Like the hooks installer (and unlike the hook
/// handlers), these are user-invoked commands: failures are loud, non-zero exit.
/// </summary>
internal static class SkillsCommands
{
    internal static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is not ("generate" or "clean" or "status"))
        {
            await stderr.WriteLineAsync(
                "Usage: depa-wiki skills <generate|clean|status> [--work-dir <path>] "
                + "[--target-dir <path>] [--max-skills <n>] [--budget <chars>]");
            return 2;
        }

        var options = LlmWikiCliOptions.Parse(args.Skip(1).ToArray());
        var workDir = CozoWikiStorageOptions.FullPath(options.GetValueOrDefault("--work-dir", Directory.GetCurrentDirectory()));
        var target = CozoWikiStorageOptions.FullPath(
            options.GetValueOrDefault("--target-dir", Path.Combine(workDir, ".claude", "skills")));

        try
        {
            return args[0] switch
            {
                "generate" => await GenerateAsync(options, workDir, target, stdout),
                "clean" => await CleanAsync(target, stdout),
                _ => await StatusAsync(target, stdout),
            };
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"depa-wiki skills: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> GenerateAsync(
        IReadOnlyDictionary<string, string> options, string workDir, string target, TextWriter stdout)
    {
        var skillsOptions = new SkillsOptions(target);
        if (int.TryParse(options.GetValueOrDefault("--max-skills", ""), out var maxSkills) && maxSkills > 0)
        {
            skillsOptions = skillsOptions with { MaxSkills = maxSkills };
        }

        if (int.TryParse(options.GetValueOrDefault("--budget", ""), out var budget) && budget > 0)
        {
            skillsOptions = skillsOptions with { BudgetPerSkill = budget };
        }

        var storage = CozoWikiStorageOptions.From(options, workDir);
        SkillsResult result;
        using (var db = new CozoDb(storage.Engine, storage.DbPath))
        {
            result = await new SkillsGenerator().GenerateAsync(new CozoOm(db), skillsOptions);
        }

        await stdout.WriteLineAsync($"Generated {result.Generated.Count} skill(s) into {target}");
        foreach (var skill in result.Generated)
        {
            await stdout.WriteLineAsync($"  {skill}");
        }

        if (result.Cleaned.Count > 0)
        {
            await stdout.WriteLineAsync($"Cleaned {result.Cleaned.Count} stale skill(s) whose context disappeared:");
            foreach (var skill in result.Cleaned)
            {
                await stdout.WriteLineAsync($"  {skill}");
            }
        }

        if (result.Truncated > 0)
        {
            await stdout.WriteLineAsync($"Truncated {result.Truncated} skill(s) at {skillsOptions.BudgetPerSkill} chars.");
        }

        return 0;
    }

    /// <summary>Removes every own-prefix skill directory under the target; user directories are never touched.</summary>
    private static async Task<int> CleanAsync(string target, TextWriter stdout)
    {
        var removed = new List<string>();
        if (Directory.Exists(target))
        {
            foreach (var directory in Directory.GetDirectories(target).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith(SkillsGenerator.OwnPrefix, StringComparison.Ordinal))
                {
                    Directory.Delete(directory, recursive: true);
                    removed.Add(name);
                }
            }
        }

        if (removed.Count == 0)
        {
            await stdout.WriteLineAsync($"No {SkillsGenerator.OwnPrefix}* skills in {target}; nothing to do.");
            return 0;
        }

        await stdout.WriteLineAsync($"Removed {removed.Count} skill(s) from {target}");
        foreach (var skill in removed)
        {
            await stdout.WriteLineAsync($"  {skill}");
        }

        return 0;
    }

    /// <summary>Names the target directory and lists the own-prefix skill directories in it.</summary>
    private static async Task<int> StatusAsync(string target, TextWriter stdout)
    {
        await stdout.WriteLineAsync($"Skills directory: {target}{(Directory.Exists(target) ? "" : " (missing)")}");
        var own = Directory.Exists(target)
            ? Directory.GetDirectories(target)
                .Select(directory => Path.GetFileName(directory)!)
                .Where(name => name.StartsWith(SkillsGenerator.OwnPrefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
        if (own.Length == 0)
        {
            await stdout.WriteLineAsync($"  No {SkillsGenerator.OwnPrefix}* skills generated.");
            return 0;
        }

        foreach (var skill in own)
        {
            await stdout.WriteLineAsync($"  {skill}");
        }

        return 0;
    }
}
