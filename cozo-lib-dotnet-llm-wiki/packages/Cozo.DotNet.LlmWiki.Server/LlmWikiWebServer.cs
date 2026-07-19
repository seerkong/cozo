using System.Text.Json.Nodes;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Cozo.DotNet.LlmWiki.Server;

public static class LlmWikiWebServer
{
    public static WebApplication BuildApp(LlmWikiServerOptions options, string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.WebHost.UseUrls(options.Url);
        builder.Services.AddCors(policy =>
        {
            policy.AddDefaultPolicy(cors => cors
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowAnyOrigin());
        });

        var app = builder.Build();
        app.UseCors();

        var db = new CozoDb(options.Storage.Engine, options.Storage.DbPath);
        var om = new CozoOm(db);
        var runner = new LlmWikiToolRunner(om);
        app.Lifetime.ApplicationStopping.Register(db.Dispose);

        app.MapGet("/health", () => Results.Json(new { status = "ok" }, LlmWikiJson.Options));
        app.MapGet("/api/status", async (CancellationToken cancellationToken) =>
        {
            // add-llm-wiki-detect-changes track (design.md §2, add-only fields): stale hint from
            // ck_meta.indexed_commit vs live HEAD. Non-git work dirs yield nulls, never an error.
            var (indexedCommit, headCommit, stale) = await runner.GetStalenessAsync(options.WorkDirectory, cancellationToken);
            return Results.Json(new
            {
                status = "ok",
                workDirectory = options.WorkDirectory,
                engine = options.Storage.Engine,
                dbPath = options.Storage.DbPath,
                globalDataDirectory = options.Storage.GlobalDataDirectory,
                workDataDirectory = options.Storage.WorkDataDirectory,
                staticDirectory = options.StaticDirectory,
                apiOnly = options.ApiOnly || options.StaticDirectory is null || !Directory.Exists(options.StaticDirectory),
                indexedCommit,
                headCommit,
                stale,
                staleHint = stale == true
                    ? "the index was built at a different commit than the current HEAD; re-run index_repo to refresh"
                    : null
            }, LlmWikiJson.Options);
        });
        app.MapGet("/api/tools", () => Results.Json(new JsonObject { ["tools"] = LlmWikiToolRunner.ToolsJson() }, LlmWikiJson.Options));
        app.MapPost("/api/tools/call", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            var body = await ReadJsonObjectAsync(request, cancellationToken);
            var name = body["name"]?.GetValue<string>() ?? "";
            var argsNode = body["arguments"]?.AsObject() ?? new JsonObject();
            try
            {
                var result = await runner.CallAsync(name, argsNode, cancellationToken);
                return Results.Json(result, LlmWikiJson.Options);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new JsonObject
                {
                    ["error"] = ex.Message
                });
            }
        });

        MapTool(app, "/api/index", runner, "index_repo");
        MapTool(app, "/api/embeddings/index", runner, "index_embeddings");
        MapTool(app, "/api/search/semantic", runner, "semantic_search");
        MapTool(app, "/api/symbol/context", runner, "symbol_context");
        MapTool(app, "/api/symbol/impact", runner, "impact_of_change");
        MapTool(app, "/api/graph/overview", runner, "overview_graph");
        MapTool(app, "/api/wiki/build", runner, "build_wiki");
        MapTool(app, "/api/query/named", runner, "query_named");

        if (!options.ApiOnly && options.StaticDirectory is { Length: > 0 } staticDirectory && Directory.Exists(staticDirectory))
        {
            app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = new PhysicalFileProvider(staticDirectory)
            });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(staticDirectory)
            });
            app.MapFallbackToFile("index.html", new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(staticDirectory)
            });
        }

        return app;
    }

    public static async Task RunAsync(LlmWikiServerOptions options, CancellationToken cancellationToken = default)
    {
        var app = BuildApp(options);
        if (!options.ApiOnly && (options.StaticDirectory is null || !Directory.Exists(options.StaticDirectory)))
        {
            Console.Error.WriteLine("Frontend dist not found; starting API-only server. Use --static-dir <path> after building cozo-lib-dotnet-llm-wiki-viz.");
        }

        Console.Error.WriteLine($"depa-wiki server running at {options.Url}");
        await app.RunAsync(cancellationToken);
    }

    private static void MapTool(WebApplication app, string path, LlmWikiToolRunner runner, string toolName)
    {
        app.MapPost(path, async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            var body = await ReadJsonObjectAsync(request, cancellationToken);
            var result = await runner.CallAsync(toolName, body, cancellationToken);
            return Results.Json(result, LlmWikiJson.Options);
        });
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is 0 or null)
        {
            return new JsonObject();
        }

        return await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken) as JsonObject ?? new JsonObject();
    }
}
