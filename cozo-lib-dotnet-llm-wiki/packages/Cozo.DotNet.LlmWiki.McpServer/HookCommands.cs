using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.McpServer;

/// <summary>
/// Claude Code hook subcommands (add-llm-wiki-agent-hooks track, design §1).
/// Discipline shared by every path: any failure degrades silently — one diagnostic line to
/// stderr, empty stdout, exit 0 — so a broken hook never disturbs the agent. The CLI shell in
/// Program.cs is wiring only; tests call <see cref="RunAsync"/> directly with in-memory streams.
/// </summary>
internal static partial class HookCommands
{
    private const string TruncationMarker = "…(truncated)";
    private const int DefaultBudget = 2000;
    private const int MaxEnrichedFiles = 10;
    private const int MaxCallNames = 3;
    private const int MaxProcessNames = 2;
    private const double MinCallConfidence = 0.7;

    internal static async Task<int> RunAsync(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            if (args.Length == 0)
            {
                return 0;
            }

            var options = LlmWikiCliOptions.Parse(args.Skip(1).ToArray());
            var workDir = CozoWikiStorageOptions.FullPath(options.GetValueOrDefault("--work-dir", Directory.GetCurrentDirectory()));
            return args[0] switch
            {
                "augment" => await AugmentAsync(options, workDir, stdin, stdout),
                "staleness" => await StalenessAsync(options, workDir, stdout),
                _ => 0,
            };
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"depa-wiki hook: {ex.Message}");
            return 0;
        }
    }

    // --- hooks installer (hooks install|uninstall|status, design §2) ---------------------------
    // Unlike the hook handlers above (silent degrade, always exit 0), the installer is a
    // user-invoked command: failures are loud — non-zero exit, no partial writes.

    /// <summary>Any hook command containing this marker is ours; everything else is the user's.</summary>
    private const string OwnershipMarker = "depa-wiki hook";

    private static readonly JsonSerializerOptions SettingsWriteOptions = new() { WriteIndented = true };

    internal static async Task<int> RunInstallerAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is not ("install" or "uninstall" or "status"))
        {
            await stderr.WriteLineAsync("Usage: depa-wiki hooks <install|uninstall|status> [--work-dir <path>]");
            return 2;
        }

        var options = LlmWikiCliOptions.Parse(args.Skip(1).ToArray());
        var workDir = CozoWikiStorageOptions.FullPath(options.GetValueOrDefault("--work-dir", Directory.GetCurrentDirectory()));
        var settingsPath = Path.Combine(workDir, ".claude", "settings.json");

        JsonObject settings;
        try
        {
            // JsonNode (not typed deserialization) keeps every unknown user field intact on rewrite.
            settings = File.Exists(settingsPath)
                ? JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))?.AsObject()
                    ?? throw new JsonException("root is not a JSON object")
                : [];
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync(
                $"depa-wiki hooks: cannot parse {settingsPath} ({ex.Message}); aborting without writing.");
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "install" => await InstallAsync(settings, settingsPath, workDir, stdout),
                "uninstall" => await UninstallAsync(settings, settingsPath, stdout),
                _ => await StatusAsync(settings, settingsPath, stdout),
            };
        }
        catch (Exception ex)
        {
            // e.g. "hooks" or an event holds a non-object/non-array shape we refuse to guess about.
            await stderr.WriteLineAsync(
                $"depa-wiki hooks: unexpected settings shape in {settingsPath} ({ex.Message}); aborting without writing.");
            return 1;
        }
    }

    /// <summary>The two entries this installer owns: search enrichment + session staleness hint.</summary>
    private static (string Event, string Matcher, string Command)[] OwnEntries(string workDir) =>
    [
        ("PostToolUse", "Grep|Glob", $"{OwnershipMarker} augment --work-dir {workDir}"),
        // SessionStart matchers are source filters (startup|resume|clear); "startup" scopes the
        // staleness hint to fresh sessions — once per session, per design.
        ("SessionStart", "startup", $"{OwnershipMarker} staleness --work-dir {workDir}"),
    ];

    private static async Task<int> InstallAsync(JsonObject settings, string settingsPath, string workDir, TextWriter stdout)
    {
        if (settings["hooks"] is not JsonObject hooks)
        {
            settings["hooks"] = hooks = [];
        }

        var added = 0;
        foreach (var (eventName, matcher, command) in OwnEntries(workDir))
        {
            if (hooks[eventName] is not JsonArray eventArray)
            {
                hooks[eventName] = eventArray = [];
            }

            if (eventArray.Any(group => OwnCommands(group).Any()))
            {
                continue; // idempotent: our entry for this event is already installed
            }

            eventArray.Add(new JsonObject
            {
                ["matcher"] = matcher,
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command }),
            });
            added++;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        await File.WriteAllTextAsync(settingsPath, settings.ToJsonString(SettingsWriteOptions) + "\n");
        await stdout.WriteLineAsync(added > 0
            ? $"Installed {added} depa-wiki hook entr{(added == 1 ? "y" : "ies")} into {settingsPath}"
            : $"depa-wiki hooks already installed in {settingsPath}");
        return 0;
    }

    private static async Task<int> UninstallAsync(JsonObject settings, string settingsPath, TextWriter stdout)
    {
        var removed = 0;
        if (File.Exists(settingsPath) && settings["hooks"] is JsonObject hooks)
        {
            foreach (var eventName in hooks.Select(property => property.Key).ToList())
            {
                if (hooks[eventName] is not JsonArray eventArray)
                {
                    continue;
                }

                var removedInEvent = 0;
                foreach (var group in eventArray.ToList())
                {
                    var ours = OwnCommands(group).ToList();
                    if (ours.Count == 0)
                    {
                        continue; // user group: zero-touch
                    }

                    var groupHooks = group!["hooks"]!.AsArray();
                    foreach (var entry in ours)
                    {
                        groupHooks.Remove(entry);
                        removedInEvent++;
                    }

                    if (groupHooks.Count == 0)
                    {
                        eventArray.Remove(group); // the group only existed to carry our command
                    }
                }

                removed += removedInEvent;
                if (eventArray.Count == 0 && removedInEvent > 0)
                {
                    hooks.Remove(eventName); // clean up only the array we ourselves emptied
                }
            }

            if (hooks.Count == 0 && removed > 0)
            {
                settings.Remove("hooks"); // clean up the object we emptied
            }
        }

        if (removed > 0)
        {
            await File.WriteAllTextAsync(settingsPath, settings.ToJsonString(SettingsWriteOptions) + "\n");
        }

        await stdout.WriteLineAsync(removed > 0
            ? $"Removed {removed} depa-wiki hook entr{(removed == 1 ? "y" : "ies")} from {settingsPath}"
            : $"No depa-wiki hooks installed in {settingsPath}; nothing to do.");
        return 0;
    }

    private static async Task<int> StatusAsync(JsonObject settings, string settingsPath, TextWriter stdout)
    {
        await stdout.WriteLineAsync($"Settings file: {settingsPath}{(File.Exists(settingsPath) ? "" : " (missing)")}");
        var found = 0;
        if (settings["hooks"] is JsonObject hooks)
        {
            foreach (var (eventName, eventNode) in hooks)
            {
                if (eventNode is not JsonArray eventArray)
                {
                    continue;
                }

                foreach (var group in eventArray)
                {
                    foreach (var entry in OwnCommands(group))
                    {
                        var matcher = group!["matcher"]?.GetValue<string>() ?? "";
                        await stdout.WriteLineAsync($"  {eventName} [{matcher}]: {entry["command"]!.GetValue<string>()}");
                        found++;
                    }
                }
            }
        }

        if (found == 0)
        {
            await stdout.WriteLineAsync("  No depa-wiki hook entries installed.");
        }

        return 0;
    }

    /// <summary>The entries we own inside one matcher group: type=command containing the marker.</summary>
    private static IEnumerable<JsonObject> OwnCommands(JsonNode? group)
    {
        if (group?["hooks"] is not JsonArray groupHooks)
        {
            yield break;
        }

        foreach (var entry in groupHooks)
        {
            if (entry is JsonObject candidate
                && candidate["command"] is JsonValue value
                && value.TryGetValue<string>(out var command)
                && command.Contains(OwnershipMarker, StringComparison.Ordinal))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// Resolves the sqlite db path the storage options would use — without creating any
    /// directories (unlike CozoWikiStorageOptions.From). A missing db means "no .depa-wiki
    /// library here": the hook exits empty instead of materializing an empty index.
    /// </summary>
    private static string? ResolveExistingDbPath(IReadOnlyDictionary<string, string> options, string workDir)
    {
        if (string.Equals(options.GetValueOrDefault("--engine", "sqlite"), "mem", StringComparison.Ordinal))
        {
            return null; // a mem engine has no persisted index for a hook process to read
        }

        var folder = options.GetValueOrDefault("--data-folder-name", ".depa-wiki");
        var dbPath = CozoWikiStorageOptions.FullPath(
            options.GetValueOrDefault("--db", Path.Combine(workDir, folder, "depa-wiki.db")));
        return File.Exists(dbPath) ? dbPath : null;
    }

    // --- staleness (SessionStart) -------------------------------------------------------------

    private static async Task<int> StalenessAsync(
        IReadOnlyDictionary<string, string> options, string workDir, TextWriter stdout)
    {
        var dbPath = ResolveExistingDbPath(options, workDir);
        if (dbPath is null)
        {
            return 0;
        }

        using var db = new CozoDb("sqlite", dbPath);
        var (indexedCommit, headCommit, stale) = await new LlmWikiToolRunner(new CozoOm(db)).GetStalenessAsync(workDir);
        if (stale != true)
        {
            return 0; // in sync, no git, or never indexed — nothing to say
        }

        await WriteHookOutputAsync(stdout, "SessionStart",
            $"depa-wiki: index is behind HEAD (indexed {Short(indexedCommit!)}, head {Short(headCommit!)}). "
            + $"Run: depa-wiki index --work-dir {workDir}");
        return 0;
    }

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;

    // --- augment (PostToolUse, Grep|Glob) -------------------------------------------------------

    private static async Task<int> AugmentAsync(
        IReadOnlyDictionary<string, string> options, string workDir, TextReader stdin, TextWriter stdout)
    {
        var budget = int.TryParse(options.GetValueOrDefault("--budget", ""), out var parsedBudget) && parsedBudget > 0
            ? parsedBudget
            : DefaultBudget;
        var raw = await stdin.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        var input = JsonNode.Parse(raw)?.AsObject(); // invalid JSON throws → silent degrade upstream
        var toolName = input?["tool_name"]?.GetValue<string>() ?? "";
        if (toolName is not ("Grep" or "Glob"))
        {
            return 0;
        }

        var responseText = ExtractResponseText(input?["tool_response"]);
        var hits = ExtractPathHits(responseText, workDir);
        if (hits.Count == 0)
        {
            return 0;
        }

        var dbPath = ResolveExistingDbPath(options, workDir);
        if (dbPath is null)
        {
            return 0;
        }

        using var db = new CozoDb("sqlite", dbPath);
        var om = new CozoOm(db);
        var lines = await EnrichAsync(om, hits);
        if (lines.Count == 0)
        {
            return 0;
        }

        var context = "Graph context (depa-wiki):\n" + string.Join("\n", lines);
        if (context.Length > budget)
        {
            context = context[..Math.Max(0, budget - TruncationMarker.Length)] + TruncationMarker;
        }

        await WriteHookOutputAsync(stdout, "PostToolUse", context);
        return 0;
    }

    /// <summary>
    /// tool_response compatibility (behavior delta augment-enriches): Claude Code sends either a
    /// plain string or an object/array shape (e.g. Grep's {mode, content, filenames}); every
    /// nested string value is harvested — the path regex downstream discards non-path noise.
    /// </summary>
    private static string ExtractResponseText(JsonNode? node)
    {
        var builder = new StringBuilder();
        Collect(node, builder, depth: 0);
        return builder.ToString();

        static void Collect(JsonNode? node, StringBuilder builder, int depth)
        {
            if (node is null || depth > 4)
            {
                return;
            }

            switch (node)
            {
                case JsonValue value when value.TryGetValue<string>(out var text):
                    builder.AppendLine(text);
                    break;
                case JsonObject obj:
                    foreach (var property in obj)
                    {
                        Collect(property.Value, builder, depth + 1);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        Collect(item, builder, depth + 1);
                    }

                    break;
            }
        }
    }

    [GeneratedRegex(@"(?<path>[A-Za-z0-9_~][A-Za-z0-9_./\\~-]*\.[A-Za-z0-9_]+|/[A-Za-z0-9_./\\~-]*\.[A-Za-z0-9_]+)(?::(?<line>\d+))?")]
    private static partial Regex PathHitRegex();

    /// <summary>
    /// Extracts ordered, deduplicated path(:line) hits from search-tool output. Paths are
    /// normalized to the ck_file form (forward slashes, repo-root-relative when under workDir);
    /// the first line number seen per path wins.
    /// </summary>
    private static List<(string Path, int? Line)> ExtractPathHits(string text, string workDir)
    {
        var workPrefix = workDir.Replace('\\', '/').TrimEnd('/') + "/";
        var hits = new List<(string Path, int? Line)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in PathHitRegex().Matches(text))
        {
            var path = match.Groups["path"].Value.Replace('\\', '/');
            if (path.StartsWith("./", StringComparison.Ordinal))
            {
                path = path[2..];
            }

            if (path.StartsWith(workPrefix, StringComparison.Ordinal))
            {
                path = path[workPrefix.Length..];
            }

            if (path.Length == 0 || !seen.Add(path))
            {
                continue;
            }

            int? line = match.Groups["line"].Success && int.TryParse(match.Groups["line"].Value, out var parsed)
                ? parsed
                : null;
            hits.Add((path, line));
            if (hits.Count >= 64)
            {
                break; // regex harvest bound; the indexed-file bound below is MaxEnrichedFiles
            }
        }

        return hits;
    }

    /// <summary>
    /// Enrichment chain (design §1): hit paths → ck_file (first MaxEnrichedFiles indexed) → one
    /// symbol per file (line-in-range preferred, else the file's top-degree symbol) → per symbol
    /// one compact line with CALLS callers/callees (confidence ≥ 0.7, top 3) and execution flows
    /// (top 2). Consumes existing Tools/Om queries only — no new graph logic.
    /// </summary>
    private static async Task<List<string>> EnrichAsync(CozoOm om, List<(string Path, int? Line)> hits)
    {
        var fileRows = await om.Runtime.Store.RunAsync(
            "?[file_id, path] := *ck_file{ file_id, path }, is_in(path, $paths)",
            new Dictionary<string, object?> { ["paths"] = hits.Select(hit => hit.Path).ToList() });
        var fileIdByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in fileRows.Rows)
        {
            fileIdByPath[AsString(row[1])] = AsString(row[0]);
        }

        var indexedHits = hits
            .Where(hit => fileIdByPath.ContainsKey(hit.Path))
            .Take(MaxEnrichedFiles)
            .ToList();
        if (indexedHits.Count == 0)
        {
            return [];
        }

        // One batched symbol fetch for every indexed hit file.
        var symbolRows = await om.Runtime.Store.RunAsync(
            """
            ?[symbol_id, name, file_id, start_line, end_line] :=
              *ck_symbol{ symbol_id, name, file_id, start_line, end_line }, is_in(file_id, $ids)
            :sort symbol_id
            """,
            new Dictionary<string, object?> { ["ids"] = indexedHits.Select(hit => fileIdByPath[hit.Path]).ToList() });
        var symbolsByFile = new Dictionary<string, List<(string Id, string Name, int Start, int End)>>(StringComparer.Ordinal);
        foreach (var row in symbolRows.Rows)
        {
            var fileId = AsString(row[2]);
            if (!symbolsByFile.TryGetValue(fileId, out var list))
            {
                symbolsByFile[fileId] = list = [];
            }

            list.Add((
                AsString(row[0]),
                AsString(row[1]),
                row[3].ValueKind == JsonValueKind.Number ? row[3].GetInt32() : 0,
                row[4].ValueKind == JsonValueKind.Number ? row[4].GetInt32() : 0));
        }

        var degrees = await SymbolDegreesAsync(om, symbolsByFile.Values.SelectMany(list => list.Select(s => s.Id)));
        var lines = new List<string>();
        foreach (var (path, line) in indexedHits)
        {
            if (!symbolsByFile.TryGetValue(fileIdByPath[path], out var symbols) || symbols.Count == 0)
            {
                continue;
            }

            // Line-in-range wins (tightest span when nested); otherwise the file's top-degree symbol.
            var chosen = line is { } hitLine
                ? symbols
                    .Where(s => s.Start <= hitLine && hitLine <= s.End)
                    .OrderBy(s => s.End - s.Start)
                    .ThenBy(s => s.Id, StringComparer.Ordinal)
                    .Select(s => ((string Id, string Name, int Start, int End)?)s)
                    .FirstOrDefault()
                : null;
            chosen ??= symbols
                .OrderByDescending(s => degrees.GetValueOrDefault(s.Id))
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .First();

            lines.Add(await SymbolLineAsync(om, chosen.Value.Id, chosen.Value.Name, path, chosen.Value.Start));
        }

        return lines;
    }

    /// <summary>In+out ck_edge degree per symbol id (top-degree fallback when a hit has no line).</summary>
    private static async Task<Dictionary<string, int>> SymbolDegreesAsync(CozoOm om, IEnumerable<string> symbolIds)
    {
        var ids = symbolIds.Distinct(StringComparer.Ordinal).ToList();
        var degrees = new Dictionary<string, int>(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return degrees;
        }

        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[id, other] := *ck_edge{ from_id: id, to_id: other }, is_in(id, $ids)
            ?[id, other] := *ck_edge{ from_id: other, to_id: id }, is_in(id, $ids)
            """,
            new Dictionary<string, object?> { ["ids"] = ids });
        foreach (var row in rows.Rows)
        {
            var id = AsString(row[0]);
            degrees[id] = degrees.GetValueOrDefault(id) + 1;
        }

        return degrees;
    }

    private static async Task<string> SymbolLineAsync(CozoOm om, string symbolId, string name, string path, int startLine)
    {
        var context = await om.FindSymbolContextAsync(symbolId);
        var callerIds = context.Incoming
            .Where(rel => rel.Kind == CodeEdgeKinds.Calls && rel.Confidence >= MinCallConfidence)
            .Select(rel => rel.FromId)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxCallNames)
            .ToList();
        var calleeIds = context.Outgoing
            .Where(rel => rel.Kind == CodeEdgeKinds.Calls && rel.Confidence >= MinCallConfidence)
            .Select(rel => rel.ToId)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxCallNames)
            .ToList();
        var names = await SymbolNamesAsync(om, callerIds.Concat(calleeIds));

        var parts = new List<string>();
        if (callerIds.Count > 0)
        {
            parts.Add("callers: " + string.Join(", ", callerIds.Select(id => names.GetValueOrDefault(id, LastSegment(id)))));
        }

        if (calleeIds.Count > 0)
        {
            parts.Add("callees: " + string.Join(", ", calleeIds.Select(id => names.GetValueOrDefault(id, LastSegment(id)))));
        }

        var processNames = context.Processes
            .Select(process => process.Name)
            .Where(processName => processName.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxProcessNames)
            .ToList();
        if (processNames.Count > 0)
        {
            parts.Add("processes: " + string.Join(", ", processNames));
        }

        var summary = parts.Count > 0 ? " — " + string.Join("; ", parts) : "";
        return $"{name} ({path}:{startLine}){summary}";
    }

    private static async Task<Dictionary<string, string>> SymbolNamesAsync(CozoOm om, IEnumerable<string> symbolIds)
    {
        var ids = symbolIds.Distinct(StringComparer.Ordinal).ToList();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return names;
        }

        var rows = await om.Runtime.Store.RunAsync(
            "?[symbol_id, name] := *ck_symbol{ symbol_id, name }, is_in(symbol_id, $ids)",
            new Dictionary<string, object?> { ["ids"] = ids });
        foreach (var row in rows.Rows)
        {
            names[AsString(row[0])] = AsString(row[1]);
        }

        return names;
    }

    private static string LastSegment(string id)
    {
        var index = id.LastIndexOf(':');
        return index >= 0 && index + 1 < id.Length ? id[(index + 1)..] : id;
    }

    private static string AsString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();

    private static async Task WriteHookOutputAsync(TextWriter stdout, string eventName, string additionalContext)
    {
        var payload = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = eventName,
                ["additionalContext"] = additionalContext,
            },
        };
        await stdout.WriteLineAsync(payload.ToJsonString(LlmWikiJson.Options));
        await stdout.FlushAsync();
    }
}
