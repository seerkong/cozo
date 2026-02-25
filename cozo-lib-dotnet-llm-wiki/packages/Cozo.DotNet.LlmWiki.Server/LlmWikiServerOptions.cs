using Cozo.DotNet.LlmWiki.Tools;

namespace Cozo.DotNet.LlmWiki.Server;

public sealed record LlmWikiServerOptions(
    CozoWikiStorageOptions Storage,
    string WorkDirectory,
    string Host = "127.0.0.1",
    int Port = 4176,
    string? StaticDirectory = null,
    bool ApiOnly = false)
{
    public string Url => $"http://{Host}:{Port}";

    public static LlmWikiServerOptions From(IReadOnlyDictionary<string, string> options, string defaultWorkDirectory)
    {
        var workDirectory = CozoWikiStorageOptions.FullPath(options.GetValueOrDefault("--work-dir", defaultWorkDirectory));
        var storage = CozoWikiStorageOptions.From(options, workDirectory);
        var host = options.GetValueOrDefault("--host", "127.0.0.1");
        var port = int.TryParse(options.GetValueOrDefault("--port"), out var parsedPort) && parsedPort > 0 ? parsedPort : 4176;
        var staticDirectory = options.TryGetValue("--static-dir", out var rawStaticDir) && !string.IsNullOrWhiteSpace(rawStaticDir)
            ? CozoWikiStorageOptions.FullPath(rawStaticDir)
            : DefaultStaticDirectory();
        var apiOnly = options.TryGetValue("--api-only", out var rawApiOnly) && rawApiOnly.Equals("true", StringComparison.OrdinalIgnoreCase);
        return new LlmWikiServerOptions(storage, workDirectory, host, port, staticDirectory, apiOnly);
    }

    private static string? DefaultStaticDirectory()
    {
        var current = Directory.GetCurrentDirectory();
        var candidate = Path.Combine(current, "cozo-lib-dotnet-llm-wiki-viz", "dist");
        return Directory.Exists(candidate) ? candidate : null;
    }
}
