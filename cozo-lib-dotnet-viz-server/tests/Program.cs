using System.Net.Http.Json;
using System.Text.Json;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

var baseUrl = Environment.GetEnvironmentVariable("VIZ_SERVER_BASE_URL") ?? "http://127.0.0.1:5099";
using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

using var health = await http.GetFromJsonAsync<JsonDocument>("/health");
Assert(health?.RootElement.GetProperty("status").GetString() == "ok", "health should return ok");

using var demos = await http.GetFromJsonAsync<JsonDocument>("/api/demos");
Assert(demos?.RootElement.GetProperty("demos").GetArrayLength() > 0, "demos should not be empty");
var expectedDemoIds = new[] { "procurement", "hr", "crm", "it-asset", "approval-flow", "org-timeline" };
var demoElements = demos!.RootElement.GetProperty("demos").EnumerateArray().ToArray();
var actualDemoIds = demoElements.Select(d => d.GetProperty("id").GetString()).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
foreach (var demoId in expectedDemoIds)
{
    Assert(actualDemoIds.Contains(demoId), $"/api/demos should include {demoId}");
    var demo = demoElements.First(d => d.GetProperty("id").GetString() == demoId);
    var firstQuery = demo.GetProperty("queries").EnumerateArray().First().GetProperty("id").GetString();
    using var response = await PostJsonAsync("/api/run", new { demoId, queryId = firstQuery, tables = Array.Empty<object>() });
    Assert(response.RootElement.GetProperty("status").GetString() == "ok", $"/api/run {demoId}/{firstQuery} should return ok");
}

var runCases = new[]
{
    ("employees", "table"),
    ("impactAnalysis", "graph"),
    ("ownershipTree", "tree"),
    ("riskHotspot", "table"),
};
foreach (var (queryId, expectedKey) in runCases)
{
    using var response = await PostJsonAsync("/api/run", new { demoId = "hr", queryId, tables = Array.Empty<object>() });
    Assert(response.RootElement.GetProperty("status").GetString() == "ok", $"/api/run {queryId} should return ok");
    Assert(response.RootElement.TryGetProperty(expectedKey, out _), $"/api/run {queryId} should include {expectedKey}");
}

using var permissionModels = await http.GetFromJsonAsync<JsonDocument>("/api/permission/models");
Assert(permissionModels?.RootElement.GetProperty("models").GetArrayLength() > 0, "permission models should not be empty");

using var permissionRun = await PostJsonAsync("/api/permission/run", new
{
    modelId = "rbac-basic",
    queryId = "userPermissions",
    @params = new Dictionary<string, string> { ["userId"] = "u001" }
});
Assert(permissionRun.RootElement.GetProperty("status").GetString() == "ok", "permission run should return ok");
Assert(permissionRun.RootElement.GetProperty("sections").GetArrayLength() > 0, "permission run should return sections");

using var state = await http.GetFromJsonAsync<JsonDocument>("/api/schema/state");
Assert(state?.RootElement.GetProperty("currentVersion").GetInt32() >= 1, "schema state should include currentVersion");

using var schemaApply = await PostJsonAsync("/api/schema/apply", new
{
    spec = new
    {
        migrationId = "contract:add-case",
        fromVersion = 1,
        toVersion = 2,
        label = "contract",
        steps = new object[] { new { kind = "addType", typeName = "ContractCase", description = "Contract case" } }
    }
});
Assert(schemaApply.RootElement.GetProperty("ok").GetBoolean(), "schema apply should return ok");

using var schemaRollback = await PostJsonAsync("/api/schema/rollback", new { targetVersion = 1, strict = true });
Assert(schemaRollback.RootElement.GetProperty("ok").GetBoolean(), "schema rollback should return ok");

using var seed = await PostJsonAsync("/api/governance/seed", new { });
Assert(seed.RootElement.GetProperty("ok").GetBoolean(), "governance seed should return ok");

using var access = await PostJsonAsync("/api/governance/checkAccess", new { subjectId = "admin", action = "read", resourceId = "proj1" });
Assert(access.RootElement.GetProperty("result").GetProperty("allow").GetBoolean(), "governance check should allow admin read");

using var integritySeed = await PostJsonAsync("/api/governance/integrity/seed-demo", new { });
Assert(integritySeed.RootElement.GetProperty("ok").GetBoolean(), "integrity seed should return ok");

using var integrityRules = await http.GetFromJsonAsync<JsonDocument>("/api/governance/integrity/rules");
Assert(integrityRules?.RootElement.GetProperty("rules").GetArrayLength() > 0, "integrity rules should not be empty");

using var integrityCheck = await PostJsonAsync("/api/governance/integrity/check", new { });
Assert(integrityCheck.RootElement.GetProperty("violations").GetArrayLength() > 0, "integrity check should return violations");

using var integrityApply = await PostJsonAsync("/api/governance/integrity/apply", new { });
Assert(integrityApply.RootElement.GetProperty("result").GetProperty("reachedFixpoint").GetBoolean(), "integrity apply should reach fixpoint");

Console.WriteLine("Cozo.DotNet VizServer contract tests passed.");

async Task<JsonDocument> PostJsonAsync(string path, object body)
{
    using var response = await http.PostAsJsonAsync(path, body);
    response.EnsureSuccessStatusCode();
    var text = await response.Content.ReadAsStringAsync();
    return JsonDocument.Parse(text);
}
