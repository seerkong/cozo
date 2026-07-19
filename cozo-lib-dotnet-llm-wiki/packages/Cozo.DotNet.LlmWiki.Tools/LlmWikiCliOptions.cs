using System.Text.Json;
using System.Text.Json.Nodes;
using Cozo.DotNet.LlmWiki.Core;

namespace Cozo.DotNet.LlmWiki.Tools;

public static class LlmWikiCliOptions
{
    public static Dictionary<string, string> Parse(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            dict[args[i]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        return dict;
    }

    public static JsonObject ToolArguments(IReadOnlyDictionary<string, string> options)
    {
        var result = new JsonObject();
        if ((options.TryGetValue("--arguments-json", out var raw) || options.TryGetValue("--args", out raw))
            && !string.IsNullOrWhiteSpace(raw))
        {
            var parsed = JsonNode.Parse(raw)?.AsObject() ?? throw new JsonException("--arguments-json/--args must be a JSON object.");
            foreach (var property in parsed)
            {
                result[property.Key] = property.Value?.DeepClone();
            }
        }

        foreach (var (key, value) in options)
        {
            if (StorageOptionNames.Contains(key) || key is "--arguments-json" or "--args")
            {
                continue;
            }

            result[KebabOptionToCamelName(key)] = JsonValue.Create(value);
        }

        return result;
    }

    public static RepositoryIndexRequest IndexRequestFromOptions(IReadOnlyDictionary<string, string> options, string repoPath) =>
        new(
            repoPath,
            DocSymbolLinkMode: ParseDocSymbolLinkMode(options.GetValueOrDefault("--doc-symbol-link-mode")),
            MaxDocLinksPerDoc: PositiveIntOrDefault(options.GetValueOrDefault("--max-doc-links-per-doc"), 20),
            MaxInferredRelations: PositiveIntOrDefault(options.GetValueOrDefault("--max-inferred-relations"), 200_000),
            Reindex: string.Equals(options.GetValueOrDefault("--reindex"), "true", StringComparison.OrdinalIgnoreCase));

    public static string KebabOptionToCamelName(string option)
    {
        var name = option.TrimStart('-');
        var builder = new System.Text.StringBuilder();
        var upperNext = false;
        foreach (var ch in name)
        {
            if (ch == '-')
            {
                upperNext = true;
                continue;
            }

            builder.Append(upperNext ? char.ToUpperInvariant(ch) : ch);
            upperNext = false;
        }

        return builder.ToString();
    }

    private static readonly HashSet<string> StorageOptionNames = new(StringComparer.Ordinal)
    {
        "--engine",
        "--db",
        "--global-dir",
        "--work-dir",
        "--data-folder-name",
    };

    private static RepositoryDocSymbolLinkMode ParseDocSymbolLinkMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" => RepositoryDocSymbolLinkMode.Local,
            "off" => RepositoryDocSymbolLinkMode.Off,
            "local" => RepositoryDocSymbolLinkMode.Local,
            "global" => RepositoryDocSymbolLinkMode.Global,
            _ => throw new ArgumentException($"Invalid doc-symbol link mode: {value}. Expected off, local, or global.")
        };

    private static int PositiveIntOrDefault(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}
