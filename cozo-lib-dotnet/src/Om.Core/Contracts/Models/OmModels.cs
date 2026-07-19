using System.Collections.Immutable;
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

/// <summary>
/// Expresses parent changes without overloading null. The legacy nullable
/// DefineTypeAsync overload continues to use null as "preserve".
/// </summary>
public enum TypeParentPatchKind
{
    Keep,
    Set,
    Clear,
}

public sealed record TypeParentPatch
{
    private TypeParentPatch(TypeParentPatchKind kind, string? parentType = null)
    {
        if (kind == TypeParentPatchKind.Set && string.IsNullOrWhiteSpace(parentType))
        {
            throw new ArgumentException("A Set parent patch requires a parent type", nameof(parentType));
        }

        if (kind != TypeParentPatchKind.Set && parentType is not null)
        {
            throw new ArgumentException("Only a Set parent patch may carry a parent type", nameof(parentType));
        }

        Kind = kind;
        ParentType = parentType;
    }

    public TypeParentPatchKind Kind { get; }
    public string? ParentType { get; }

    public static TypeParentPatch Keep { get; } = new(TypeParentPatchKind.Keep);
    public static TypeParentPatch Clear { get; } = new(TypeParentPatchKind.Clear);
    public static TypeParentPatch Set(string parentType) => new(TypeParentPatchKind.Set, parentType);
}

public sealed record DefineTypePatchInput(
    string Name,
    string Description,
    TypeParentPatch Parent,
    IReadOnlyList<string>? Mixins = null);

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

public enum SchemaDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public enum SchemaLegacyHandling
{
    DetectOnly,
    Upgrade,
}

public sealed record SchemaInitializationOptions(SchemaLegacyHandling LegacyHandling = SchemaLegacyHandling.DetectOnly)
{
    public static SchemaInitializationOptions Default { get; } = new();
}

public sealed record SchemaMigrationV2Options(bool Strict = true)
{
    public static SchemaMigrationV2Options Default { get; } = new();
}

public sealed record SchemaRollbackV2Options(bool Strict = true, bool Force = false)
{
    public static SchemaRollbackV2Options Default { get; } = new();
}

public sealed record SchemaDiagnostic(
    string Code,
    SchemaDiagnosticSeverity Severity,
    string Message,
    string? DefinitionKind = null,
    string? DefinitionKey = null,
    string? EntityId = null);

public sealed record SchemaDefinitionChange(JsonElement Before, JsonElement After);

public sealed record SchemaDefinitionDiff
{
    public SchemaDefinitionDiff(
        IEnumerable<KeyValuePair<string, JsonElement>>? added = null,
        IEnumerable<KeyValuePair<string, JsonElement>>? removed = null,
        IEnumerable<KeyValuePair<string, SchemaDefinitionChange>>? changed = null)
    {
        Added = CopyElements(added);
        Removed = CopyElements(removed);
        Changed = changed?.ToImmutableDictionary(
            pair => pair.Key,
            pair => new SchemaDefinitionChange(pair.Value.Before.Clone(), pair.Value.After.Clone()),
            StringComparer.Ordinal)
            ?? ImmutableDictionary<string, SchemaDefinitionChange>.Empty.WithComparers(StringComparer.Ordinal);
    }

    public ImmutableDictionary<string, JsonElement> Added { get; }
    public ImmutableDictionary<string, JsonElement> Removed { get; }
    public ImmutableDictionary<string, SchemaDefinitionChange> Changed { get; }

    private static ImmutableDictionary<string, JsonElement> CopyElements(
        IEnumerable<KeyValuePair<string, JsonElement>>? entries) =>
        entries?.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal)
        ?? ImmutableDictionary<string, JsonElement>.Empty.WithComparers(StringComparer.Ordinal);
}

public sealed record SchemaKeyedDiff
{
    public SchemaKeyedDiff(
        int fromVersion,
        int toVersion,
        IEnumerable<KeyValuePair<string, SchemaDefinitionDiff>>? definitions = null)
    {
        FromVersion = fromVersion;
        ToVersion = toVersion;
        Definitions = definitions?.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal)
            ?? ImmutableDictionary<string, SchemaDefinitionDiff>.Empty.WithComparers(StringComparer.Ordinal);
    }

    public int FromVersion { get; }
    public int ToVersion { get; }
    public ImmutableDictionary<string, SchemaDefinitionDiff> Definitions { get; }
}

public sealed record SchemaInitializationResult
{
    public SchemaInitializationResult(
        bool initialized,
        SchemaLegacyHandling legacyHandling,
        bool legacySchemaDetected,
        IEnumerable<SchemaDiagnostic>? diagnostics = null)
    {
        Initialized = initialized;
        LegacyHandling = legacyHandling;
        LegacySchemaDetected = legacySchemaDetected;
        Diagnostics = diagnostics?.ToImmutableArray() ?? ImmutableArray<SchemaDiagnostic>.Empty;
    }

    public bool Initialized { get; }
    public SchemaLegacyHandling LegacyHandling { get; }
    public bool LegacySchemaDetected { get; }
    public ImmutableArray<SchemaDiagnostic> Diagnostics { get; }
}

public sealed record SchemaMigrationV2Result
{
    public SchemaMigrationV2Result(
        string migrationId,
        int fromVersion,
        int toVersion,
        bool applied,
        bool strict,
        IEnumerable<SchemaDiagnostic>? diagnostics = null,
        SchemaKeyedDiff? diff = null)
    {
        MigrationId = migrationId;
        FromVersion = fromVersion;
        ToVersion = toVersion;
        Applied = applied;
        Strict = strict;
        Diagnostics = diagnostics?.ToImmutableArray() ?? ImmutableArray<SchemaDiagnostic>.Empty;
        Diff = diff;
    }

    public string MigrationId { get; }
    public int FromVersion { get; }
    public int ToVersion { get; }
    public bool Applied { get; }
    public bool Strict { get; }
    public ImmutableArray<SchemaDiagnostic> Diagnostics { get; }
    public SchemaKeyedDiff? Diff { get; }
}

public sealed record SchemaRollbackV2Result
{
    public SchemaRollbackV2Result(
        int fromVersion,
        int targetVersion,
        bool applied,
        bool strict,
        bool forced,
        IEnumerable<SchemaDiagnostic>? diagnostics = null)
    {
        FromVersion = fromVersion;
        TargetVersion = targetVersion;
        Applied = applied;
        Strict = strict;
        Forced = forced;
        Diagnostics = diagnostics?.ToImmutableArray() ?? ImmutableArray<SchemaDiagnostic>.Empty;
    }

    public int FromVersion { get; }
    public int TargetVersion { get; }
    public bool Applied { get; }
    public bool Strict { get; }
    public bool Forced { get; }
    public ImmutableArray<SchemaDiagnostic> Diagnostics { get; }
}

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

public enum PermissionFieldVisibility
{
    Visible,
    Hidden,
}

public enum PermissionEvaluationStatus
{
    Matched,
    Unmatched,
    Invalid,
    NotEvaluated,
}

public sealed record PermissionWitnessHop(string FromId, string RelName, string ToId);

public sealed record PermissionWitnessDiagnostic(
    PermissionEvaluationStatus Status,
    ImmutableArray<string> DeclaredPaths,
    ImmutableArray<PermissionWitnessHop> Hops,
    ImmutableArray<string> Diagnostics);

public sealed record PermissionAbacDiagnostic(
    string LeftRef,
    string Operator,
    string RightRef,
    PermissionEvaluationStatus Status,
    string? Detail = null);

public sealed record PermissionPolicyEvaluation(
    string PolicyId,
    string Effect,
    string Action,
    string ResourceType,
    PermissionEvaluationStatus Status,
    PermissionWitnessDiagnostic Witness,
    ImmutableArray<PermissionAbacDiagnostic> AbacDiagnostics,
    ImmutableArray<string> Diagnostics);

public sealed record CheckAccessResult(bool Allow, JsonElement Explanation)
{
    public string? AsOf { get; init; }

    public ImmutableDictionary<string, PermissionFieldVisibility> FieldVisibility { get; init; } =
        ImmutableDictionary<string, PermissionFieldVisibility>.Empty.WithComparers(StringComparer.Ordinal);

    public ImmutableArray<PermissionPolicyEvaluation> PolicyEvaluations { get; init; } = [];

    public ImmutableArray<string> Diagnostics { get; init; } = [];
}

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
