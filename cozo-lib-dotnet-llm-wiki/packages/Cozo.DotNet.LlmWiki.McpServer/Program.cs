using System.Text.Json;
using System.Text.Json.Nodes;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Indexing;
using Cozo.DotNet.LlmWiki.Server;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.LlmWiki.Wiki;
using Cozo.DotNet.Om;

var app = new LlmWikiCli();
return await app.RunAsync(args);

internal sealed class LlmWikiCli
{
    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        if (args[0] == "--serve")
        {
            return await RunServeAsync(args);
        }

        return args[0] switch
        {
            "serve" => await RunServeAsync(args.Skip(1).ToArray()),
            "mcp" => await RunMcpAsync(args.Skip(1).ToArray()),
            "index" => await RunIndexAsync(args.Skip(1).ToArray()),
            "wiki" => await RunWikiAsync(args.Skip(1).ToArray()),
            "tools" => RunTools(),
            "call" => await RunCallAsync(args.Skip(1).ToArray()),
            // Claude Code agent hooks (add-llm-wiki-agent-hooks track): wiring only — every
            // behavior (stdin JSON, silent degrade, budget) lives in the testable HookCommands.
            "hook" => await Cozo.DotNet.LlmWiki.McpServer.HookCommands.RunAsync(
                args.Skip(1).ToArray(), Console.In, Console.Out, Console.Error),
            // hooks installer: merge-safe edits of <work-dir>/.claude/settings.json — user
            // entries zero-touch, idempotent, parse failure aborts loudly without writing.
            "hooks" => await Cozo.DotNet.LlmWiki.McpServer.HookCommands.RunInstallerAsync(
                args.Skip(1).ToArray(), Console.Out, Console.Error),
            // skills generator (add-llm-wiki-skills-generation track): wiring only — rendering,
            // budget and the own-prefix/sync discipline live in the Wiki package's SkillsGenerator.
            "skills" => await Cozo.DotNet.LlmWiki.McpServer.SkillsCommands.RunAsync(
                args.Skip(1).ToArray(), Console.Out, Console.Error),
            _ => Unknown(args[0]),
        };
    }

    private static async Task<int> RunMcpAsync(string[] args)
    {
        if (!args.Contains("--stdio", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("Only stdio MCP mode is supported. Use: mcp --stdio");
            return 2;
        }

        var options = LlmWikiCliOptions.Parse(args);
        var storage = CozoWikiStorageOptions.From(options, Directory.GetCurrentDirectory());
        using var db = new CozoDb(storage.Engine, storage.DbPath);
        var server = new LlmWikiMcpServer(new CozoOm(db));
        await server.RunAsync(Console.In, Console.Out);
        return 0;
    }

    private static async Task<int> RunIndexAsync(string[] args)
    {
        var options = LlmWikiCliOptions.Parse(args);
        var repo = CozoWikiStorageOptions.FullPath(options.GetValueOrDefault("--repo", Directory.GetCurrentDirectory()));
        var storage = CozoWikiStorageOptions.From(options, repo);
        using var db = new CozoDb(storage.Engine, storage.DbPath);
        var om = new CozoOm(db);
        var summary = await new RepositoryIndexer().IndexAsync(om, LlmWikiCliOptions.IndexRequestFromOptions(options, repo));
        Console.WriteLine(JsonSerializer.Serialize(summary, LlmWikiJson.Options));
        return 0;
    }

    /// <summary>
    /// wiki subcommand with the first-class fractal entry (fix-wiki-fractal-entry-and-context-
    /// ranking track T1.1): --pipeline legacy (default) stays the exact pre-existing
    /// WikiCompiler path; --pipeline codument-fractal reuses the build_wiki fractal branch of
    /// LlmWikiToolRunner (same useLlm/force semantics, work directory = --work-dir, defaulting
    /// to --repo). An unknown value errors listing the legal values — never a silent downgrade.
    /// </summary>
    private static async Task<int> RunWikiAsync(string[] args)
    {
        var options = LlmWikiCliOptions.Parse(args);
        var pipeline = options.GetValueOrDefault("--pipeline", "legacy").Trim().ToLowerInvariant();
        if (pipeline is not ("legacy" or "codument-fractal"))
        {
            Console.Error.WriteLine($"Invalid pipeline: {pipeline}. Expected legacy or codument-fractal.");
            return 2;
        }

        var repo = CozoWikiStorageOptions.FullPath(options.GetValueOrDefault("--repo", Directory.GetCurrentDirectory()));
        var storage = CozoWikiStorageOptions.From(options, repo);
        using var db = new CozoDb(storage.Engine, storage.DbPath);
        var om = new CozoOm(db);
        await new RepositoryIndexer().IndexAsync(om, LlmWikiCliOptions.IndexRequestFromOptions(options, repo));

        if (pipeline == "codument-fractal")
        {
            // Reuse the build_wiki fractal execution path — --out overrides the pipeline's
            // default preview root ({work-dir}/.depa-wiki/docs-preview) when present.
            var toolArgs = new JsonObject
            {
                ["pipeline"] = "codument-fractal",
                ["workDirectory"] = CozoWikiStorageOptions.FullPath(options.GetValueOrDefault("--work-dir", repo)),
                ["useLlm"] = options.GetValueOrDefault("--use-llm", "true"),
                ["force"] = options.GetValueOrDefault("--force", "false"),
            };
            if (options.TryGetValue("--out", out var fractalOut))
            {
                toolArgs["outputDirectory"] = fractalOut;
            }

            var fractalResult = await new LlmWikiToolRunner(om).CallAsync("build_wiki", toolArgs);
            Console.WriteLine(JsonSerializer.Serialize(fractalResult, LlmWikiJson.Options));
            return 0;
        }

        var output = options.GetValueOrDefault("--out", Path.Combine(repo, ".llm-wiki"));
        var result = await new WikiCompiler().BuildAsync(om, new(output, WriteFiles: true));
        Console.WriteLine(JsonSerializer.Serialize(result, LlmWikiJson.Options));
        return 0;
    }

    private static int RunTools()
    {
        Console.WriteLine(new JsonObject { ["tools"] = LlmWikiToolRunner.ToolsJson() }.ToJsonString(LlmWikiJson.Options));
        return 0;
    }

    private static async Task<int> RunCallAsync(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Usage: depa-wiki call <tool-name> [--arguments-json json] [--tool-arg value] [storage options]");
            return 2;
        }

        var toolName = args[0];
        var options = LlmWikiCliOptions.Parse(args.Skip(1).ToArray());
        var storage = CozoWikiStorageOptions.From(options, Directory.GetCurrentDirectory());
        using var db = new CozoDb(storage.Engine, storage.DbPath);
        var runner = new LlmWikiToolRunner(new CozoOm(db));
        var result = await runner.CallAsync(toolName, LlmWikiCliOptions.ToolArguments(options));
        Console.WriteLine(JsonSerializer.Serialize(result, LlmWikiJson.Options));
        return 0;
    }

    private static async Task<int> RunServeAsync(string[] args)
    {
        var options = LlmWikiCliOptions.Parse(args);
        var serverOptions = LlmWikiServerOptions.From(options, Directory.GetCurrentDirectory());
        await LlmWikiWebServer.RunAsync(serverOptions);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  depa-wiki --serve [options]");
        Console.Error.WriteLine("  depa-wiki serve [options]");
        Console.Error.WriteLine("  depa-wiki mcp --stdio [options]");
        Console.Error.WriteLine("  depa-wiki index --repo <path> [options]");
        Console.Error.WriteLine("  depa-wiki wiki --repo <path> --out <dir> [--pipeline legacy|codument-fractal] [options]");
        Console.Error.WriteLine("  depa-wiki tools");
        Console.Error.WriteLine("  depa-wiki call <tool-name> [--arguments-json json] [--tool-arg value] [options]");
        Console.Error.WriteLine("  depa-wiki hook augment [--budget <chars>] [options]   (Claude Code PostToolUse hook: stdin JSON -> graph context)");
        Console.Error.WriteLine("  depa-wiki hook staleness [options]                    (Claude Code SessionStart hook: index-behind-HEAD hint)");
        Console.Error.WriteLine("  depa-wiki hooks install|uninstall|status [--work-dir <path>]  (manage the hook entries in <work-dir>/.claude/settings.json, merge-safe)");
        Console.Error.WriteLine("  depa-wiki skills generate|clean|status [--work-dir <path>] [--target-dir <path>] [--max-skills <n>] [--budget <chars>]");
        Console.Error.WriteLine("                                                        (generate Claude Code skills from the code graph into <work-dir>/.claude/skills; only depa-wiki-* directories are ever written or removed)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Storage options:");
        Console.Error.WriteLine("  --engine sqlite|mem          CozoDB engine. Default: sqlite.");
        Console.Error.WriteLine("  --global-dir <path>          Base directory for global shared data/config. Default: ~/.");
        Console.Error.WriteLine("  --work-dir <path>            Project/workspace directory. Defaults to --repo for index/wiki, current directory for mcp.");
        Console.Error.WriteLine("  --data-folder-name <name>    Data folder name under global-dir and work-dir. Default: .depa-wiki.");
        Console.Error.WriteLine("  --db <path>                  Explicit sqlite database path. Overrides the default work db path.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Default sqlite layout:");
        Console.Error.WriteLine("  Global data/config: <global-dir>/<data-folder-name>");
        Console.Error.WriteLine("  Work data/config:   <work-dir>/<data-folder-name>");
        Console.Error.WriteLine("  SQLite DB:          <work-dir>/<data-folder-name>/depa-wiki.db");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Indexing options:");
        Console.Error.WriteLine("  --doc-symbol-link-mode off|local|global  Documentation link inference mode. Default: local.");
        Console.Error.WriteLine("  --max-doc-links-per-doc <n>              Max inferred documentation links per doc block. Default: 20.");
        Console.Error.WriteLine("  --max-inferred-relations <n>             Max inferred documentation relations per index run. Default: 200000.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Wiki options:");
        Console.Error.WriteLine("  --pipeline legacy|codument-fractal  Wiki pipeline: legacy template compiler (default) or the codument-fractal four-phase community-led dual fractal.");
        Console.Error.WriteLine("  --use-llm true|false                codument-fractal only: use the configured LLM backend. Default: true, degrading to the pure structure layer when unavailable.");
        Console.Error.WriteLine("  --force                             codument-fractal only: ignore the page-level incremental cache and rebuild every page.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Serve options:");
        Console.Error.WriteLine("  --serve                    Start local HTTP server mode.");
        Console.Error.WriteLine("  --host <host>              Server host. Default: 127.0.0.1.");
        Console.Error.WriteLine("  --port <port>              Server port. Default: 4176.");
        Console.Error.WriteLine("  --static-dir <path>        Serve built cozo-lib-dotnet-llm-wiki-viz dist assets.");
        Console.Error.WriteLine("  --api-only true            Start only API endpoints even if static assets exist.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Examples:");
        Console.Error.WriteLine("  depa-wiki --serve --work-dir ~/path/to/my/project");
        Console.Error.WriteLine("  depa-wiki --serve --work-dir ~/path/to/my/project --port 4176 --static-dir ./cozo-lib-dotnet-llm-wiki-viz/dist");
        Console.Error.WriteLine("  depa-wiki mcp --stdio");
        Console.Error.WriteLine("  depa-wiki index --repo ~/path/to/my/project");
        Console.Error.WriteLine("  depa-wiki wiki --repo ~/path/to/my/project --out ~/path/to/my/project/.llm-wiki");
        Console.Error.WriteLine("  depa-wiki mcp --stdio --work-dir ~/path/to/my/project");
        Console.Error.WriteLine("  depa-wiki mcp --stdio --engine mem");
        Console.Error.WriteLine("  depa-wiki mcp --stdio --db ~/path/to/my/project/.depa-wiki/custom.db");
        Console.Error.WriteLine("  depa-wiki tools");
        Console.Error.WriteLine("  depa-wiki call index_repo --work-dir ~/path/to/my/project --repo-path ~/path/to/my/project");
        Console.Error.WriteLine("  depa-wiki call index_repo --work-dir ~/path/to/my/project --repo-path ~/path/to/my/project --doc-symbol-link-mode off");
        Console.Error.WriteLine("  depa-wiki call semantic_search --work-dir ~/path/to/my/project --query SampleService --limit 5 --source-kinds code,docs");
        Console.Error.WriteLine("  depa-wiki call overview_graph --work-dir ~/path/to/my/project --categories code,docs --max-nodes 400 --max-edges 900");
        Console.Error.WriteLine("  depa-wiki call query_named --work-dir ~/path/to/my/project --name code_impact --parameters-json '{\"symbolId\":\"...\"}'");
    }
}

internal sealed class LlmWikiMcpServer(CozoOm om)
{
    private readonly LlmWikiToolRunner _runner = new(om);

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject response;
            try
            {
                response = await HandleAsync(JsonNode.Parse(line)?.AsObject() ?? throw new JsonException("Expected JSON object."), cancellationToken);
            }
            catch (Exception ex)
            {
                response = Error(null, -32603, ex.Message);
            }

            await output.WriteLineAsync(response.ToJsonString(LlmWikiJson.Options));
            await output.FlushAsync();
        }
    }

    private async Task<JsonObject> HandleAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>() ?? "";
        return method switch
        {
            "initialize" => Result(id, new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["serverInfo"] = new JsonObject { ["name"] = "depa-wiki", ["version"] = "0.1.0" },
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }
            }),
            "notifications/initialized" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject { ["tools"] = LlmWikiToolRunner.ToolsJson() }),
            "tools/call" => await CallToolAsync(id, request["params"]?.AsObject(), cancellationToken),
            _ => Error(id, -32601, $"Unknown method: {method}"),
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonNode? id, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? "";
        var args = parameters?["arguments"]?.AsObject() ?? new JsonObject();
        var result = await _runner.CallAsync(name, args, cancellationToken);
        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = JsonSerializer.Serialize(result, LlmWikiJson.Options)
            }),
            ["isError"] = false
        });
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
}
