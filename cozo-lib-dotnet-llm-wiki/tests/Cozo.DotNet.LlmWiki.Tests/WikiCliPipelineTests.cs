using System.Text.Json.Nodes;

namespace Cozo.DotNet.LlmWiki.Tests;

/// <summary>
/// wiki subcommand --pipeline dispatch (fix-wiki-fractal-entry-and-context-ranking track T1.1,
/// delta behavior://llm-wiki-pipeline/requirements/codument-fractal-entry, suite cli-entry):
/// cases codument-fractal-runs-fractal (first-class fractal entry, dual-fractal output, canonical
/// pipeline echo), legacy-default-unchanged (no flag / --pipeline legacy stay the exact
/// WikiCompiler behavior) and invalid-pipeline-rejected (unknown value errors listing the legal
/// values, never a silent downgrade). Drives the internal CLI handler in-process; the tool-layer
/// canonical-name/alias cases live in the main Program.cs build_wiki block.
/// </summary>
internal static class WikiCliPipelineTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"llm-wiki-cli-pipeline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // --- fixture: minimal indexable repo (same shape as the main Program.cs sample).
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);
            await File.WriteAllTextAsync(Path.Combine(repo, "Sample.cs"), """
            namespace Demo;

            public class SampleService
            {
                public string GetValue()
                {
                    return "ok";
                }
            }
            """);
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), """
            # Demo

            SampleService is documented here.
            """);

            // codument-fractal-runs-fractal: --pipeline codument-fractal routes to
            // FractalWikiPipeline, writes the dual fractal (modeling + impl) to --out and
            // echoes the canonical pipeline name.
            var fractalOut = Path.Combine(root, "fractal-out");
            var fractal = await RunCliAsync(
                "wiki", "--repo", repo, "--out", fractalOut, "--work-dir", repo,
                "--engine", "mem", "--pipeline", "codument-fractal", "--use-llm", "false");
            assert(fractal.Exit == 0, "wiki --pipeline codument-fractal should exit 0 (stderr: " + fractal.Err + ")");
            var fractalJson = JsonNode.Parse(fractal.Out)!.AsObject();
            assert(fractalJson["pipeline"]?.GetValue<string>() == "codument-fractal",
                "wiki --pipeline codument-fractal should echo pipeline=codument-fractal in the result JSON");
            assert(fractalJson["pages"]!.AsArray().Count > 0,
                "wiki --pipeline codument-fractal should report fractal pages");
            assert(File.Exists(Path.Combine(fractalOut, "impl", "global", "overview", "index.md")),
                "wiki --pipeline codument-fractal should write the engineering fractal to --out");
            assert(File.Exists(Path.Combine(fractalOut, "modeling", "index.md")),
                "wiki --pipeline codument-fractal should write the modeling fractal to --out");

            // legacy-default-unchanged: no --pipeline stays the exact WikiCompiler behavior —
            // a WikiBuildResult JSON (indexMarkdown/pages, no pipeline field) written to --out.
            var legacyOut = Path.Combine(root, "legacy-out");
            var legacyDefault = await RunCliAsync(
                "wiki", "--repo", repo, "--out", legacyOut, "--work-dir", repo, "--engine", "mem");
            assert(legacyDefault.Exit == 0, "wiki without --pipeline should exit 0 (stderr: " + legacyDefault.Err + ")");
            var legacyDefaultJson = JsonNode.Parse(legacyDefault.Out)!.AsObject();
            assert(legacyDefaultJson["indexMarkdown"] is not null
                    && legacyDefaultJson["pages"]!.AsArray().Count > 0
                    && !legacyDefaultJson.ContainsKey("pipeline"),
                "wiki without --pipeline should keep the untouched legacy WikiBuildResult shape");
            assert(File.Exists(Path.Combine(legacyOut, "index.md")),
                "wiki without --pipeline should keep writing the legacy wiki files to --out");

            // legacy-default-unchanged (explicit): --pipeline legacy is the same legacy path.
            var legacyExplicitOut = Path.Combine(root, "legacy-explicit-out");
            var legacyExplicit = await RunCliAsync(
                "wiki", "--repo", repo, "--out", legacyExplicitOut, "--work-dir", repo,
                "--engine", "mem", "--pipeline", "legacy");
            assert(legacyExplicit.Exit == 0, "wiki --pipeline legacy should exit 0 (stderr: " + legacyExplicit.Err + ")");
            var legacyExplicitJson = JsonNode.Parse(legacyExplicit.Out)!.AsObject();
            assert(legacyExplicitJson["indexMarkdown"] is not null && !legacyExplicitJson.ContainsKey("pipeline"),
                "wiki --pipeline legacy should behave exactly like the default legacy path");

            // invalid-pipeline-rejected: unknown value errors listing the legal values
            // (legacy, codument-fractal) and never silently downgrades to a build.
            var invalidOut = Path.Combine(root, "invalid-out");
            var invalid = await RunCliAsync(
                "wiki", "--repo", repo, "--out", invalidOut, "--work-dir", repo,
                "--engine", "mem", "--pipeline", "bogus");
            assert(invalid.Exit != 0, "wiki --pipeline bogus should exit non-zero");
            assert(invalid.Err.Contains("legacy", StringComparison.Ordinal)
                    && invalid.Err.Contains("codument-fractal", StringComparison.Ordinal),
                "wiki --pipeline bogus should list the legal values legacy and codument-fractal (stderr: " + invalid.Err + ")");
            assert(string.IsNullOrWhiteSpace(invalid.Out) && !Directory.Exists(invalidOut),
                "wiki --pipeline bogus should not silently run any pipeline");

            // usage text: the wiki line advertises the --pipeline option.
            var help = await RunCliAsync("--help");
            assert(help.Err.Contains("[--pipeline legacy|codument-fractal]", StringComparison.Ordinal),
                "depa-wiki --help should advertise [--pipeline legacy|codument-fractal] on the wiki line");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // temp cleanup is best-effort
            }
        }
    }

    /// <summary>Runs the internal CLI entry in-process, capturing console stdout/stderr.</summary>
    private static async Task<(int Exit, string Out, string Err)> RunCliAsync(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await new LlmWikiCli().RunAsync(args);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
