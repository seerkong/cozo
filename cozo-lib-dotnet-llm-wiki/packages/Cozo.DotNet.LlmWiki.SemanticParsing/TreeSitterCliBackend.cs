namespace Cozo.DotNet.LlmWiki.SemanticParsing;

/// <summary>
/// Fallback backend adapting the existing TreeSitterCliParser to the ISemanticParserBackend
/// contract. Behavior of TreeSitterCliParser itself is unchanged; this adapter only maps
/// its results into ParsedFileResult (no symbols/edges — the CLI path never produced them).
/// </summary>
public sealed class TreeSitterCliBackend : ISemanticParserBackend
{
    private static readonly string[] SupportedLanguages = ["csharp", "typescript", "javascript", "java"];

    private readonly TreeSitterCliParser _parser;

    public TreeSitterCliBackend(TreeSitterCliParser? parser = null)
    {
        _parser = parser ?? new TreeSitterCliParser();
    }

    public string Name => "cli";

    public bool SupportsLanguage(string languageId) =>
        SupportedLanguages.Contains((languageId ?? "").Trim().ToLowerInvariant());

    public ParsedFileResult Parse(string path, string source, string languageId)
    {
        // The CLI parser reads from disk; materialize the source when the path is not readable.
        var tempFile = (string?)null;
        var filePath = path;
        try
        {
            if (!File.Exists(filePath))
            {
                tempFile = Path.Combine(Path.GetTempPath(), $"ts-cli-{Guid.NewGuid():N}{GuessExtension(languageId, path)}");
                File.WriteAllText(tempFile, source);
                filePath = tempFile;
            }

            var result = _parser
                .ParseAsync(new SemanticParseRequest(filePath, languageId))
                .GetAwaiter()
                .GetResult();
            return new ParsedFileResult(
                result.Success,
                path,
                result.Language,
                Name,
                HasErrors: false,
                [],
                [],
                result.Diagnostics);
        }
        finally
        {
            if (tempFile is not null && File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static string GuessExtension(string languageId, string path)
    {
        var existing = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        return (languageId ?? "").Trim().ToLowerInvariant() switch
        {
            "csharp" => ".cs",
            "typescript" => ".ts",
            "javascript" => ".js",
            "java" => ".java",
            _ => ".txt",
        };
    }
}
