using System.Text.Json;

namespace Cozo.DotNet.Om.Contracts.Models;

public enum OmValueType
{
    String,
    Number,
    Bool,
    Json,
    Validity,
    Unknown,
}

public enum OmDirection
{
    Outgoing,
    Incoming,
    Both,
}

public enum ExistentialDirection
{
    Out,
    In,
}

public enum ExistentialRuleMode
{
    Check,
    Materialize,
}

public sealed record OmType(string Name, string Description, string? ParentType = null);

public sealed record OmMixin(string Name, string Description);

public sealed record OmAttribute(
    string TypeName,
    string AttrName,
    OmValueType ValueType,
    bool Required,
    string? Description = null);

public sealed record OmRelation(
    string RelName,
    string FromType,
    string ToType,
    bool Directed,
    string? Description = null);

public sealed record OmEntity(string Id, string TypeName, string Label);

public sealed record OmProperty(string EntityId, string AttrName, JsonElement Value);

public sealed record OmEdge(string FromId, string RelName, string ToId, JsonElement Props);

public sealed record NeighborEntry(string RelName, string EntityId, string TypeName, string Label);

public sealed record NeighborResult(IReadOnlyList<NeighborEntry> Outgoing, IReadOnlyList<NeighborEntry> Incoming);

public sealed record EntityView(
    string Id,
    string TypeName,
    string Label,
    IReadOnlyDictionary<string, JsonElement> Properties,
    IReadOnlyList<EntityViewEdge> Outgoing);

public sealed record EntityViewEdge(string RelName, string ToId, string ToType, string ToLabel);

public sealed record FindByTypeEntry(
    string Id,
    string TypeName,
    string Label,
    IReadOnlyDictionary<string, JsonElement> Properties);

public sealed record ValidationResult(bool Valid, IReadOnlyList<string> Errors);

public sealed record PropertyHistoryEntry(JsonElement Value, string ValidTime, string TxTime);

public sealed record EdgeHistoryEntry(
    string FromId,
    string RelName,
    string ToId,
    JsonElement Props,
    string ValidTime,
    string TxTime,
    bool IsAssert);

public sealed record TypeHierarchyNode(
    string Name,
    string Description,
    string? ParentType,
    IReadOnlyList<string> Mixins,
    IReadOnlyList<string> Children);

public sealed record TypeHierarchy(
    IReadOnlyDictionary<string, TypeHierarchyNode> Types,
    IReadOnlyList<string> Roots);

public sealed record SchemaState(int CurrentVersion, string? Checksum);

public sealed record SchemaVersion(
    int Version,
    string CreatedAt,
    string? Label,
    string? Description,
    int? ParentVersion,
    string? Checksum);

public sealed record SchemaSnapshot(
    int Version,
    string CreatedAt,
    JsonElement Schema,
    string? Checksum);

public sealed record SchemaDiff(int FromVersion, int ToVersion, JsonElement Added, JsonElement Removed, JsonElement Changed);

public sealed record ExistentialWhereCondition(string Attr, string Op, JsonElement Value);

public sealed record ExistentialForEachSpec(string Type, IReadOnlyList<ExistentialWhereCondition>? Where = null);

public sealed record ExistentialExistsSpec(string Rel, ExistentialDirection Direction, string ToType);

public sealed record ExistentialMaterializeSpec(string? LabelTemplate = null, IReadOnlyDictionary<string, JsonElement>? Props = null);

public sealed record ExistentialRuleSpec(
    ExistentialForEachSpec ForEach,
    ExistentialExistsSpec Exists,
    ExistentialMaterializeSpec? Materialize = null,
    ExistentialRuleMode Mode = ExistentialRuleMode.Check,
    string Message = "",
    bool Enabled = true);

public sealed record ExistentialRule(
    string RuleName,
    ExistentialForEachSpec ForEach,
    ExistentialExistsSpec Exists,
    ExistentialMaterializeSpec? Materialize,
    ExistentialRuleMode Mode,
    string Message,
    bool Enabled);

public sealed record ExistentialViolation(string Rule, string EntityId, string Message);

public sealed record ExistentialCreated(
    string Rule,
    string TriggerEntityId,
    string SkolemId,
    string Rel,
    string ToType);

public sealed record ExistentialDiagnostic(string RuleName, int RemainingViolations);

public sealed record ExistentialChaseResult(
    IReadOnlyList<ExistentialCreated> Created,
    int Iterations,
    bool ReachedFixpoint,
    IReadOnlyList<ExistentialDiagnostic> Diagnostics);

public sealed record CheckAccessInput(string SubjectId, string Action, string ResourceId, string? AsOf = null, string? FieldName = null);

public sealed record CheckAccessResult(bool Allow, JsonElement Explanation);

public sealed record SchemaMigrationResult(
    string MigrationId,
    int FromVersion,
    int ToVersion,
    int StepsApplied,
    string Checksum);

public sealed record PermissionSeedInput(
    IReadOnlyList<PermissionActionSeed>? Actions = null,
    IReadOnlyList<PermissionPolicySeed>? Policies = null,
    IReadOnlyList<PermissionAbacRuleSeed>? AbacRules = null,
    IReadOnlyList<PermissionPathRuleSeed>? PathRules = null);

public sealed record PermissionActionSeed(string Action, string Description = "");

public sealed record PermissionPolicySeed(
    string PolicyId,
    string Effect,
    string Action,
    string ResourceType,
    bool Enabled = true,
    string Description = "");

public sealed record PermissionAbacRuleSeed(string PolicyId, string LeftRef, string Op, string RightRef);

public sealed record PermissionPathRuleSeed(string PolicyId, string Path);
