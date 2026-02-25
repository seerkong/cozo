using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Cozo.DotNet.LlmWiki.SemanticParsing;

public sealed class TreeSitterCliParser
{
    private static readonly Regex NodeLine = new(
        @"^\s*\(([A-Za-z_][A-Za-z0-9_]*)\s+\[(\d+),\s*(\d+)\]\s+-\s+\[(\d+),\s*(\d+)\]",
        RegexOptions.Compiled);

    private readonly string _executable;

    public TreeSitterCliParser(string executable = "tree-sitter")
    {
        _executable = string.IsNullOrWhiteSpace(executable) ? "tree-sitter" : executable;
    }

    public async Task<ParserStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync("--version", null, cancellationToken);
        if (result.ExitCode != 0)
        {
            return new ParserStatus(
                false,
                "tree-sitter",
                "",
                [new SemanticDiagnostic("TSCLI001", MissingMessage(result), "warning")]);
        }

        return new ParserStatus(true, "tree-sitter", result.Stdout.Trim(), []);
    }

    public async Task<SemanticParseResult> ParseAsync(
        SemanticParseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = Path.GetFullPath(request.FilePath);
        if (!File.Exists(path))
        {
            return Error(path, request.Language, "TSCLI404", $"File does not exist: {path}");
        }

        var status = await GetStatusAsync(cancellationToken);
        if (!status.Available)
        {
            return new SemanticParseResult(false, path, "tree-sitter", DetectLanguage(path, request.Language), [], status.Diagnostics);
        }

        var result = await RunAsync($"parse --quiet \"{path.Replace("\"", "\\\"", StringComparison.Ordinal)}\"", Path.GetDirectoryName(path), cancellationToken);
        if (result.ExitCode != 0)
        {
            return new SemanticParseResult(
                false,
                path,
                "tree-sitter",
                DetectLanguage(path, request.Language),
                [],
                [new SemanticDiagnostic("TSCLI002", TrimOutput(result), "warning")],
                result.Stdout);
        }

        var nodes = ParseNodes(result.Stdout, request.MaxNodes).ToArray();
        return new SemanticParseResult(
            true,
            path,
            "tree-sitter",
            DetectLanguage(path, request.Language),
            nodes,
            nodes.Length == 0 ? [new SemanticDiagnostic("TSCLI003", "Tree-sitter returned no node summaries.", "info")] : [],
            result.Stdout);
    }

    private static IEnumerable<SemanticNodeSummary> ParseNodes(string output, int maxNodes)
    {
        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = NodeLine.Match(line);
            if (!match.Success)
            {
                continue;
            }

            yield return new SemanticNodeSummary(
                match.Groups[1].Value,
                int.Parse(match.Groups[2].Value) + 1,
                int.Parse(match.Groups[3].Value),
                int.Parse(match.Groups[4].Value) + 1,
                int.Parse(match.Groups[5].Value));
            if (--maxNodes <= 0)
            {
                yield break;
            }
        }
    }

    private async Task<ProcessResult> RunAsync(string arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(_executable, arguments)
            {
                WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return new ProcessResult(-1, "", "Failed to start tree-sitter process.");
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    private static SemanticParseResult Error(string path, string? language, string code, string message) =>
        new(false, path, "tree-sitter", DetectLanguage(path, language), [], [new SemanticDiagnostic(code, message, "error")]);

    private static string DetectLanguage(string path, string? language) =>
        !string.IsNullOrWhiteSpace(language)
            ? language
            : Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".cs" => "csharp",
                ".ts" or ".tsx" => "typescript",
                ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
                ".json" => "json",
                ".md" or ".mdx" => "markdown",
                _ => "unknown",
            };

    private static string MissingMessage(ProcessResult result) =>
        $"tree-sitter CLI is unavailable. Install tree-sitter and grammars to enable semantic parsing. Detail: {TrimOutput(result)}";

    private static string TrimOutput(ProcessResult result)
    {
        var text = string.Join("\n", new[] { result.Stdout, result.Stderr }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        return text.Length == 0 ? $"exit code {result.ExitCode}" : text;
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
