using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;

namespace Cozo.DotNet.VizServer;

public sealed class OmServerState : IDisposable
{
    private CozoDb _schemaDb = NewDb();
    private CozoDb _governanceDb = NewDb();
    private CozoDb _integrityDb = NewDb();

    public CozoOm SchemaOm => new(_schemaDb);
    public CozoOm GovernanceOm => new(_governanceDb);
    public CozoOm IntegrityOm => new(_integrityDb);

    public void ResetGovernance()
    {
        _governanceDb.Dispose();
        _governanceDb = NewDb();
    }

    public void ResetIntegrity()
    {
        _integrityDb.Dispose();
        _integrityDb = NewDb();
    }

    public void Dispose()
    {
        _schemaDb.Dispose();
        _governanceDb.Dispose();
        _integrityDb.Dispose();
    }

    private static CozoDb NewDb() => new(engine: "mem", path: "");
}

public sealed class SchemaDiffRequest
{
    public int FromVersion { get; set; }
    public int ToVersion { get; set; }
}

public sealed class SchemaRollbackRequest
{
    public int TargetVersion { get; set; }
    public bool Strict { get; set; } = true;
}

public sealed class GovernanceAccessRequest
{
    public string SubjectId { get; set; } = "";
    public string Action { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string? FieldName { get; set; }
}

public static class OmServerEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string IntegrityRuleName = "asset_must_have_owner";

    public static async Task<object> SchemaStateAsync(OmServerState state)
    {
        var om = state.SchemaOm;
        await om.InitSchemaAsync();
        return await om.GetSchemaStateAsync();
    }

    public static async Task<object> SchemaVersionsAsync(OmServerState state)
    {
        var om = state.SchemaOm;
        await om.InitSchemaAsync();
        var versions = await om.ListSchemaVersionsAsync();
        return new { versions = versions.Select(v => v.Version).ToArray(), details = versions };
    }

    public static async Task<object> SchemaDiffAsync(OmServerState state, SchemaDiffRequest request)
    {
        var om = state.SchemaOm;
        await om.InitSchemaAsync();
        return new { diff = await om.DiffSchemaVersionsAsync(request.FromVersion, request.ToVersion) };
    }

    public static async Task<object> SchemaApplyAsync(OmServerState state, JsonElement body)
    {
        var om = state.SchemaOm;
        await om.InitSchemaAsync();
        if (!body.TryGetProperty("spec", out var specElement))
        {
            throw new InvalidOperationException("schema apply request requires spec");
        }

        var spec = JsonSerializer.Deserialize<SchemaMigrationSpec>(specElement.GetRawText(), JsonOptions)
            ?? throw new InvalidOperationException("Invalid schema migration spec");
        var result = await om.ApplySchemaMigrationAsync(spec);
        return new { ok = true, result, state = await om.GetSchemaStateAsync() };
    }

    public static async Task<object> SchemaRollbackAsync(OmServerState state, SchemaRollbackRequest request)
    {
        var om = state.SchemaOm;
        await om.InitSchemaAsync();
        await om.RollbackSchemaAsync(request.TargetVersion, request.Strict);
        return new { ok = true, result = new { targetVersion = request.TargetVersion }, state = await om.GetSchemaStateAsync() };
    }

    public static async Task<object> GovernanceSeedTemplateAsync()
    {
        return new
        {
            tables = new object[]
            {
                new
                {
                    name = "类型定义",
                    columns = new[] { "name", "description" },
                    rows = new object[]
                    {
                        new { name = "User", description = "User" },
                        new { name = "Project", description = "Project" }
                    }
                },
                new
                {
                    name = "实体数据",
                    columns = new[] { "id", "typeName", "label" },
                    rows = new object[]
                    {
                        new { id = "admin", typeName = "User", label = "Admin" },
                        new { id = "u1", typeName = "User", label = "User 1" },
                        new { id = "proj1", typeName = "Project", label = "Project 1" }
                    }
                },
                new
                {
                    name = "权限动作",
                    columns = new[] { "action", "description" },
                    rows = new object[] { new { action = "read", description = "Read" } }
                },
                new
                {
                    name = "权限策略",
                    columns = new[] { "policy_id", "effect", "action", "resource_type", "enabled", "description" },
                    rows = new object[]
                    {
                        new { policy_id = "allow_admin_project_read", effect = "allow", action = "read", resource_type = "Project", enabled = true, description = "Admin can read projects" }
                    }
                },
                new
                {
                    name = "权限ABAC规则",
                    columns = new[] { "policy_id", "left_ref", "op", "right_ref" },
                    rows = new object[]
                    {
                        new { policy_id = "allow_admin_project_read", left_ref = "subject.id", op = "=", right_ref = "literal:admin" }
                    }
                },
                new
                {
                    name = "权限路径规则",
                    columns = new[] { "policy_id", "path" },
                    rows = new object[]
                    {
                        new { policy_id = "allow_admin_project_read", path = "subject->project" }
                    }
                }
            }
        };
    }

    public static async Task<object> GovernanceSeedAsync(OmServerState state)
    {
        state.ResetGovernance();
        var om = state.GovernanceOm;
        await SeedGovernanceAsync(om);
        return new { ok = true };
    }

    public static async Task<object> GovernanceCheckAsync(OmServerState state, GovernanceAccessRequest request)
    {
        var om = state.GovernanceOm;
        await EnsureGovernanceSeededAsync(om);
        var result = await om.CheckAccessAsync(new CheckAccessInput(request.SubjectId, request.Action, request.ResourceId, FieldName: request.FieldName));
        return new { result };
    }

    public static async Task<object> GovernanceExplainAsync(OmServerState state, GovernanceAccessRequest request)
    {
        return await GovernanceCheckAsync(state, request);
    }

    public static async Task<object> IntegritySeedDemoAsync(OmServerState state)
    {
        state.ResetIntegrity();
        var om = state.IntegrityOm;
        await SeedIntegrityAsync(om);
        return new { ok = true, rules = await om.ListExistentialRulesAsync() };
    }

    public static async Task<object> IntegrityRulesAsync(OmServerState state)
    {
        var om = state.IntegrityOm;
        await EnsureIntegritySeededAsync(om);
        return new { rules = await om.ListExistentialRulesAsync() };
    }

    public static async Task<object> IntegrityCheckAsync(OmServerState state)
    {
        var om = state.IntegrityOm;
        await EnsureIntegritySeededAsync(om);
        return new { violations = await om.CheckExistentialRulesAsync() };
    }

    public static async Task<object> IntegrityApplyAsync(OmServerState state)
    {
        var om = state.IntegrityOm;
        await EnsureIntegritySeededAsync(om);
        return new { result = await om.ApplyExistentialRulesAsync(new ApplyExistentialRulesInput(MaxIterations: 5)) };
    }

    private static async Task EnsureGovernanceSeededAsync(CozoOm om)
    {
        await om.InitSchemaAsync();
        try
        {
            await om.GetEntityTypeAsync("proj1");
        }
        catch
        {
            await SeedGovernanceAsync(om);
        }
    }

    private static async Task SeedGovernanceAsync(CozoOm om)
    {
        await om.InitSchemaAsync();
        await om.DefineTypeAsync("User", "User");
        await om.DefineTypeAsync("Project", "Project");
        await om.CreateEntityAsync("admin", "User", "Admin");
        await om.CreateEntityAsync("u1", "User", "User 1");
        await om.CreateEntityAsync("proj1", "Project", "Project 1");
        await om.SeedPermissionMetadataAsync(new PermissionSeedInput(
            Actions: [new PermissionActionSeed("read", "Read")],
            Policies: [new PermissionPolicySeed("allow_admin_project_read", "allow", "read", "Project")],
            AbacRules: [new PermissionAbacRuleSeed("allow_admin_project_read", "subject.id", "=", "literal:admin")],
            PathRules: [new PermissionPathRuleSeed("allow_admin_project_read", "subject->project")]));
    }

    private static async Task EnsureIntegritySeededAsync(CozoOm om)
    {
        await om.InitSchemaAsync();
        var rules = await om.ListExistentialRulesAsync();
        if (!rules.Any(r => r.RuleName == IntegrityRuleName))
        {
            await SeedIntegrityAsync(om);
        }
    }

    private static async Task SeedIntegrityAsync(CozoOm om)
    {
        await om.InitSchemaAsync();
        await om.DefineTypeAsync("User", "User");
        await om.DefineTypeAsync("Asset", "Asset");
        await om.DefineRelationAsync("owns", "User", "Asset");
        await om.DefineExistentialRuleAsync(
            IntegrityRuleName,
            new ExistentialRuleSpec(
                new ExistentialForEachSpec("Asset"),
                new ExistentialExistsSpec("owns", ExistentialDirection.In, "User"),
                new ExistentialMaterializeSpec("auto owner for {fromId}"),
                ExistentialRuleMode.Materialize,
                "Each asset must have an owner"));
        await om.UpsertEntityAsync("asset:orphan-1", "Asset", "Orphan Asset 1");
        await om.UpsertEntityAsync("asset:orphan-2", "Asset", "Orphan Asset 2");
    }
}
