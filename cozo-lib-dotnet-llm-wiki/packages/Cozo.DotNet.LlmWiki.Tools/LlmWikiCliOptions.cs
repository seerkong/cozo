using System.Text.Json;
using System.Text.Json.Nodes;
using Cozo.DotNet.LlmWiki.Core;

namespace Cozo.DotNet.LlmWiki.Tools;

public static class LlmWikiCliOptions
{
    private const int MaxJsonBodyChars = 1_048_576;
    private static readonly string[] JsonOptionNames = ["--arguments-json", "--args", "--json"];

    public static Dictionary<string, string> Parse(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            dict[args[i]] = i + 1 < args.Length
                && (!args[i + 1].StartsWith("--", StringComparison.Ordinal) || args[i + 1] == "-")
                ? args[++i]
                : "true";
        }

        return dict;
    }

    public static JsonObject ToolArguments(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var raw = JsonArgument(options);
        if (raw == "-")
        {
            throw new ArgumentException("--json - requires the asynchronous CLI input path.", nameof(options));
        }
        return ToolArguments(options, raw);
    }

    public static async Task<JsonObject> ToolArgumentsAsync(
        IReadOnlyDictionary<string, string> options,
        TextReader standardInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(standardInput);
        var raw = JsonArgument(options);
        if (raw == "-")
        {
            raw = await ReadBoundedStandardInputAsync(standardInput, cancellationToken);
        }
        return ToolArguments(options, raw);
    }

    private static JsonObject ToolArguments(IReadOnlyDictionary<string, string> options, string? raw)
    {
        var result = new JsonObject();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var parsed = JsonNode.Parse(raw) as JsonObject
                ?? throw new JsonException("--arguments-json/--args/--json must be a JSON object.");
            foreach (var property in parsed)
            {
                result[property.Key] = property.Value?.DeepClone();
            }
        }

        foreach (var (key, value) in options)
        {
            if (StorageOptionNames.Contains(key) || JsonOptionNames.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            result[KebabOptionToCamelName(key)] = JsonValue.Create(value);
        }

        return result;
    }

    private static string? JsonArgument(IReadOnlyDictionary<string, string> options)
    {
        var supplied = JsonOptionNames
            .Where(options.ContainsKey)
            .Select(name => (Name: name, Value: options[name]))
            .ToArray();
        if (supplied.Length > 1)
        {
            throw new ArgumentException("Use only one of --arguments-json, --args, or --json.", nameof(options));
        }
        return supplied.Length == 1 ? supplied[0].Value : null;
    }

    private static async Task<string> ReadBoundedStandardInputAsync(TextReader standardInput, CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await standardInput.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                break;
            }
            if (builder.Length + count > MaxJsonBodyChars)
            {
                throw new ArgumentException($"JSON stdin body exceeds the {MaxJsonBodyChars}-character limit.", nameof(standardInput));
            }
            builder.Append(buffer, 0, count);
        }
        if (builder.Length == 0)
        {
            throw new ArgumentException("--json - requires a non-empty JSON object on standard input.", nameof(standardInput));
        }
        return builder.ToString();
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
