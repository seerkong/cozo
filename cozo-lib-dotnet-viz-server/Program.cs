using System.Text.Json;
using Cozo.DotNet.VizServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();
var omState = new OmServerState();
app.Lifetime.ApplicationStopping.Register(omState.Dispose);

app.MapGet("/health", () => Results.Json(new { status = "ok", runtime = ".NET" }));

app.MapGet("/api/demos", () => Results.Json(new { demos = DemoCatalog.All }));

app.MapPost("/api/run", DemoRunner.RunAsync);

app.MapGet("/api/permission/models", () => Results.Json(new { models = PermissionModels.All }));

app.MapPost("/api/permission/run", PermissionModels.RunAsync);

app.MapGet("/api/schema/state", () => OmServerEndpoints.SchemaStateAsync(omState));
app.MapGet("/api/schema/versions", () => OmServerEndpoints.SchemaVersionsAsync(omState));
app.MapPost("/api/schema/diff", (SchemaDiffRequest request) => OmServerEndpoints.SchemaDiffAsync(omState, request));
app.MapPost("/api/schema/apply", async (HttpRequest request) =>
{
    using var document = await JsonDocument.ParseAsync(request.Body);
    return await OmServerEndpoints.SchemaApplyAsync(omState, document.RootElement.Clone());
});
app.MapPost("/api/schema/rollback", (SchemaRollbackRequest request) => OmServerEndpoints.SchemaRollbackAsync(omState, request));

app.MapGet("/api/governance/seed-template", OmServerEndpoints.GovernanceSeedTemplateAsync);
app.MapPost("/api/governance/seed", () => OmServerEndpoints.GovernanceSeedAsync(omState));
app.MapPost("/api/governance/checkAccess", (GovernanceAccessRequest request) => OmServerEndpoints.GovernanceCheckAsync(omState, request));
app.MapPost("/api/governance/explain", (GovernanceAccessRequest request) => OmServerEndpoints.GovernanceExplainAsync(omState, request));
app.MapPost("/api/governance/integrity/seed-demo", () => OmServerEndpoints.IntegritySeedDemoAsync(omState));
app.MapGet("/api/governance/integrity/rules", () => OmServerEndpoints.IntegrityRulesAsync(omState));
app.MapPost("/api/governance/integrity/check", () => OmServerEndpoints.IntegrityCheckAsync(omState));
app.MapPost("/api/governance/integrity/apply", () => OmServerEndpoints.IntegrityApplyAsync(omState));

app.Run();

public partial class Program;
