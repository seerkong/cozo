using System.Collections.Immutable;
using System.Text.Json;
using Cozo.DotNet.Om.Contracts.Models;

namespace Cozo.DotNet.Om.Inputs;

public sealed record DefineTypeInput(string Name, string Description, string? ParentType = null, IReadOnlyList<string>? Mixins = null);

public sealed record DefineMixinInput(string Name, string Description);

public sealed record DefineAttributeInput(
    string TypeName,
    string AttrName,
    OmValueType ValueType,
    bool Required = false,
    string? Description = null);

public sealed record DefineRelationInput(
    string RelName,
    string FromType,
    string ToType,
    bool Directed = true,
    string? Description = null);

public sealed record EntityInput(string Id, string TypeName, string Label);

public sealed record SetPropertyInput(string EntityId, string AttrName, object? Value, WriteOptions? Options = null);

public sealed record LinkEntitiesInput(
    string FromId,
    string RelName,
    string ToId,
    object? Props = null,
    WriteOptions? Options = null);

public sealed record WriteOptions(bool SkipConstraints = false, string? ValidTime = null);

public sealed record HistoryRangeOptions(string? From = null, string? To = null);

public sealed record FindByTypeOptions(bool Exact = false);

public sealed record DefineExistentialRuleInput(string RuleName, ExistentialRuleSpec Spec);

public sealed record CheckExistentialRulesInput(IReadOnlyList<string>? Rules = null, string? AsOf = null);

public sealed record ApplyExistentialRulesInput(IReadOnlyList<string>? Rules = null, int? MaxIterations = null, string? ValidTime = null);

public sealed record DefineConstraintInput(string TypeName, string ConstraintName, string ConstraintType, string Message = "");

public sealed record DefineComputedInput(string TypeName, string AttrName, string Description = "");

public sealed record DefineActionInput(string TypeName, string ActionName, string Description = "");

public sealed record DefineMutationInput(string TypeName, string MutationName, string Description = "");

public sealed record AddInterceptorInput(string TypeName, string ActionName, string Phase, int Seq, string Description = "");

public sealed record SchemaMigrationSpec(
    string MigrationId,
    int FromVersion,
    int ToVersion,
    string? Label = null,
    string? Description = null,
    bool Strict = false,
    IReadOnlyList<JsonElement>? Steps = null);

public sealed record InitializeSchemaV2Input(SchemaInitializationOptions? Options = null)
{
    public SchemaInitializationOptions EffectiveOptions => Options ?? SchemaInitializationOptions.Default;
}

public sealed record SchemaMigrationV2Input
{
    public SchemaMigrationV2Input(
        string migrationId,
        int fromVersion,
        int toVersion,
        IEnumerable<JsonElement>? steps = null,
        string? label = null,
        string? description = null,
        SchemaMigrationV2Options? options = null)
    {
        MigrationId = migrationId;
        FromVersion = fromVersion;
        ToVersion = toVersion;
        Steps = steps?.Select(step => step.Clone()).ToImmutableArray() ?? ImmutableArray<JsonElement>.Empty;
        Label = label;
        Description = description;
        Options = options ?? SchemaMigrationV2Options.Default;
    }

    public string MigrationId { get; }
    public int FromVersion { get; }
    public int ToVersion { get; }
    public ImmutableArray<JsonElement> Steps { get; }
    public string? Label { get; }
    public string? Description { get; }
    public SchemaMigrationV2Options Options { get; }
}

public sealed record RollbackSchemaV2Input(int TargetVersion, SchemaRollbackV2Options? Options = null)
{
    public SchemaRollbackV2Options EffectiveOptions => Options ?? SchemaRollbackV2Options.Default;
}

public sealed record DefinePermissionPolicyInput(
    string PolicyId,
    string Effect,
    string Action,
    string ResourceType,
    bool Enabled = true,
    string Description = "");

public sealed record DefinePermissionAbacRuleInput(string PolicyId, string LeftRef, string Op, string RightRef);

public sealed record AddPermissionPathRuleInput(string PolicyId, string Path);
