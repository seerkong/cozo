using System.Text.Json.Nodes;
using Cozo.DotNet.LlmWiki.McpServer;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>
/// hooks install/uninstall/status installer (add-llm-wiki-agent-hooks track T2.1, design §2).
/// Delta case covered here: install-merge-safe — user entries untouched, install idempotent,
/// uninstall removes own entries only, parse failure aborts without writing.
/// </summary>
internal static class HookInstallerTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"llm-wiki-hooks-installer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // --- install-merge-safe: pre-existing settings.json with user hooks + unknown fields
            var mergeRepo = Path.Combine(root, "merge");
            var settingsPath = Path.Combine(mergeRepo, ".claude", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var userSettings = """
            {
              "permissions": { "allow": ["Bash(ls:*)"] },
              "customTopLevel": { "keep": true, "nested": [1, 2, 3] },
              "hooks": {
                "PostToolUse": [
                  { "matcher": "Bash", "hooks": [{ "type": "command", "command": "echo user-post" }] }
                ],
                "Stop": [
                  { "matcher": "", "hooks": [{ "type": "command", "command": "echo user-stop" }] }
                ]
              }
            }
            """;
            await File.WriteAllTextAsync(settingsPath, userSettings);
            var original = JsonNode.Parse(userSettings)!.AsObject();

            var install = await RunInstallerAsync(["install", "--work-dir", mergeRepo]);
            assert(install.Exit == 0, "hooks install should exit 0 (stderr: " + install.Err + ")");
            var installed = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

            // User content zero-touch: unknown top-level fields and user hook entries survive byte-for-byte.
            assert(JsonNode.DeepEquals(installed["permissions"], original["permissions"]),
                "install should preserve unknown top-level fields (permissions)");
            assert(JsonNode.DeepEquals(installed["customTopLevel"], original["customTopLevel"]),
                "install should preserve unknown top-level fields (customTopLevel)");
            assert(JsonNode.DeepEquals(installed["hooks"]!["Stop"], original["hooks"]!["Stop"]),
                "install should not touch user hook events it does not own");
            var postToolUse = installed["hooks"]!["PostToolUse"]!.AsArray();
            assert(postToolUse.Count == 2, "install should append to PostToolUse without dropping the user entry");
            assert(JsonNode.DeepEquals(postToolUse[0], original["hooks"]!["PostToolUse"]![0]),
                "the user PostToolUse entry should stay first and unchanged");

            // Own entries: PostToolUse Grep|Glob augment + SessionStart staleness, absolute work dir.
            var ownPost = postToolUse[1]!.AsObject();
            assert(ownPost["matcher"]?.GetValue<string>() == "Grep|Glob",
                "own PostToolUse entry should match Grep|Glob");
            var ownPostCommand = ownPost["hooks"]!.AsArray().Single()!["command"]!.GetValue<string>();
            assert(ownPostCommand == $"depa-wiki hook augment --work-dir {mergeRepo}",
                "own PostToolUse command should run hook augment with the absolute work dir (got: " + ownPostCommand + ")");
            assert(ownPost["hooks"]!.AsArray().Single()!["type"]!.GetValue<string>() == "command",
                "own hook entries should be type=command");
            var sessionStart = installed["hooks"]!["SessionStart"]!.AsArray();
            var ownStaleCommand = sessionStart.Single()!["hooks"]!.AsArray().Single()!["command"]!.GetValue<string>();
            assert(ownStaleCommand == $"depa-wiki hook staleness --work-dir {mergeRepo}",
                "own SessionStart command should run hook staleness with the absolute work dir (got: " + ownStaleCommand + ")");

            // Idempotence: a second install changes nothing.
            var reinstall = await RunInstallerAsync(["install", "--work-dir", mergeRepo]);
            assert(reinstall.Exit == 0, "repeated hooks install should exit 0");
            assert(JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath)), installed),
                "repeated hooks install should be idempotent (no duplicate own entries)");

            // status: names the settings file and both own entries.
            var status = await RunInstallerAsync(["status", "--work-dir", mergeRepo]);
            assert(status.Exit == 0, "hooks status should exit 0");
            assert(status.Out.Contains(settingsPath, StringComparison.Ordinal),
                "hooks status should name the settings file (got: " + status.Out + ")");
            assert(status.Out.Contains("hook augment", StringComparison.Ordinal)
                && status.Out.Contains("hook staleness", StringComparison.Ordinal),
                "hooks status should list both own entries (got: " + status.Out + ")");

            // Uninstall: own entries removed, user content restored exactly (SessionStart cleaned up).
            var uninstall = await RunInstallerAsync(["uninstall", "--work-dir", mergeRepo]);
            assert(uninstall.Exit == 0, "hooks uninstall should exit 0");
            var uninstalled = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
            assert(JsonNode.DeepEquals(uninstalled, original),
                "uninstall should restore the user settings exactly (own entries and empty leftovers removed): "
                + uninstalled.ToJsonString());
            var reUninstall = await RunInstallerAsync(["uninstall", "--work-dir", mergeRepo]);
            assert(reUninstall.Exit == 0 && JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath)), original),
                "repeated hooks uninstall should be an idempotent no-op");

            // --- fresh repo: no settings.json — install creates it; uninstall cleans empty shells.
            var freshRepo = Path.Combine(root, "fresh");
            Directory.CreateDirectory(freshRepo);
            var freshSettingsPath = Path.Combine(freshRepo, ".claude", "settings.json");
            var freshInstall = await RunInstallerAsync(["install", "--work-dir", freshRepo]);
            assert(freshInstall.Exit == 0 && File.Exists(freshSettingsPath),
                "install without a settings.json should create the file");
            var freshInstalled = JsonNode.Parse(await File.ReadAllTextAsync(freshSettingsPath))!.AsObject();
            assert(freshInstalled["hooks"]!["PostToolUse"]!.AsArray().Count == 1
                && freshInstalled["hooks"]!["SessionStart"]!.AsArray().Count == 1,
                "fresh install should carry exactly the two own entries");
            var freshUninstall = await RunInstallerAsync(["uninstall", "--work-dir", freshRepo]);
            var freshUninstalled = JsonNode.Parse(await File.ReadAllTextAsync(freshSettingsPath))!.AsObject();
            assert(freshUninstall.Exit == 0 && !freshUninstalled.ContainsKey("hooks"),
                "uninstall should clean up emptied hook arrays and the empty hooks object (got: "
                + freshUninstalled.ToJsonString() + ")");

            // status on a repo without any own entries still exits 0.
            var bareStatus = await RunInstallerAsync(["status", "--work-dir", freshRepo]);
            assert(bareStatus.Exit == 0, "hooks status without own entries should exit 0");

            // --- parse failure: abort with a non-zero exit and never write the file.
            var brokenRepo = Path.Combine(root, "broken");
            var brokenSettingsPath = Path.Combine(brokenRepo, ".claude", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(brokenSettingsPath)!);
            await File.WriteAllTextAsync(brokenSettingsPath, "{ this is not json");
            var brokenInstall = await RunInstallerAsync(["install", "--work-dir", brokenRepo]);
            assert(brokenInstall.Exit != 0, "install over an unparseable settings.json should exit non-zero");
            assert(brokenInstall.Err.Length > 0, "install over an unparseable settings.json should explain on stderr");
            assert(await File.ReadAllTextAsync(brokenSettingsPath) == "{ this is not json",
                "install over an unparseable settings.json should never write the file");
            var brokenUninstall = await RunInstallerAsync(["uninstall", "--work-dir", brokenRepo]);
            assert(brokenUninstall.Exit != 0 && await File.ReadAllTextAsync(brokenSettingsPath) == "{ this is not json",
                "uninstall over an unparseable settings.json should abort without writing");

            // Unknown subcommand: usage error, exit non-zero.
            var unknown = await RunInstallerAsync(["frobnicate"]);
            assert(unknown.Exit != 0, "an unknown hooks subcommand should exit non-zero with usage help");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<(int Exit, string Out, string Err)> RunInstallerAsync(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await HookCommands.RunInstallerAsync(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
