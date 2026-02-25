using System.Text.Json;
using Cozo.DotNet.Om.Contracts;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;
using Cozo.DotNet.Om.Logic;
using Cozo.DotNet.Om.Runtime;
using Cozo.DotNet.Om.Support;

namespace Cozo.DotNet.Om;

public sealed class CozoOm
{
    public CozoOm(CozoDb db, CozoOmOptions? options = null)
        : this(new CozoDbOmStore(db), options)
    {
    }

    public CozoOm(ICozoOmStore store, CozoOmOptions? options = null)
    {
        Runtime = new CozoOmRuntime(
            store ?? throw new ArgumentNullException(nameof(store)),
            options ?? new CozoOmOptions(),
            new CozoOmRegistry());
    }

    public CozoOmRuntime Runtime { get; }

    public Task InitSchemaAsync(CancellationToken cancellationToken = default) =>
        SchemaLogic.InitSchemaAsync(Runtime, cancellationToken);

    public Task DefineTypeAsync(string name, string description = "", string? parentType = null, IReadOnlyList<string>? mixins = null, CancellationToken cancellationToken = default) =>
        TypeLogic.DefineTypeAsync(Runtime, new DefineTypeInput(name, description, parentType, mixins), cancellationToken);

    public Task DefineMixinAsync(string name, string description = "", CancellationToken cancellationToken = default) =>
        TypeLogic.DefineMixinAsync(Runtime, new DefineMixinInput(name, description), cancellationToken);

    public Task DefineAttributeAsync(
        string typeName,
        string attrName,
        OmValueType valueType,
        bool required = false,
        string? description = null,
        CancellationToken cancellationToken = default) =>
        TypeLogic.DefineAttributeAsync(Runtime, new DefineAttributeInput(typeName, attrName, valueType, required, description), cancellationToken);

    public Task DefineRelationAsync(
        string relName,
        string fromType,
        string toType,
        bool directed = true,
        string? description = null,
        CancellationToken cancellationToken = default) =>
        TypeLogic.DefineRelationAsync(Runtime, new DefineRelationInput(relName, fromType, toType, directed, description), cancellationToken);

    public Task DefineTypeAliasAsync(string alias, string canonical, CancellationToken cancellationToken = default) =>
        TypeLogic.DefineTypeAliasAsync(Runtime, alias, canonical, cancellationToken);

    public Task DefineRelationAliasAsync(string alias, string canonical, CancellationToken cancellationToken = default) =>
        TypeLogic.DefineRelationAliasAsync(Runtime, alias, canonical, cancellationToken);

    public Task DefineAttributeAliasAsync(string typeName, string aliasAttr, string canonicalAttr, CancellationToken cancellationToken = default) =>
        TypeLogic.DefineAttributeAliasAsync(Runtime, typeName, aliasAttr, canonicalAttr, cancellationToken);

    public Task CreateEntityAsync(string id, string typeName, string label = "", CancellationToken cancellationToken = default) =>
        EntityLogic.CreateEntityAsync(Runtime, new EntityInput(id, typeName, label), cancellationToken);

    public Task UpsertEntityAsync(string id, string typeName, string label = "", CancellationToken cancellationToken = default) =>
        EntityLogic.UpsertEntityAsync(Runtime, new EntityInput(id, typeName, label), cancellationToken);

    public Task<string> GetEntityTypeAsync(string entityId, CancellationToken cancellationToken = default) =>
        EntityLogic.GetEntityTypeAsync(Runtime, entityId, cancellationToken);

    /// <summary>
    /// Physically deletes an entity in a single transaction, cascading to all of its property
    /// rows (every temporal version) and every relation edge touching it. Deleting a
    /// non-existent entity is a harmless no-op.
    /// </summary>
    public Task DeleteEntityAsync(string entityId, CancellationToken cancellationToken = default) =>
        EntityLogic.DeleteEntityAsync(Runtime, entityId, cancellationToken);

    public Task SetPropertyAsync(string entityId, string attrName, object? value, WriteOptions? options = null, CancellationToken cancellationToken = default) =>
        EntityLogic.SetPropertyAsync(Runtime, new SetPropertyInput(entityId, attrName, value, options), cancellationToken);

    public Task<JsonElement?> GetPropertyAsync(string entityId, string attrName, CancellationToken cancellationToken = default) =>
        EntityLogic.GetPropertyAsync(Runtime, entityId, attrName, cancellationToken);

    public Task<JsonElement?> GetPropertyAsOfAsync(string entityId, string attrName, string asOf, CancellationToken cancellationToken = default) =>
        EntityLogic.GetPropertyAsOfAsync(Runtime, entityId, attrName, asOf, cancellationToken);

    public Task<IReadOnlyList<PropertyHistoryEntry>> GetPropertyHistoryAsync(string entityId, string attrName, HistoryRangeOptions? options = null, CancellationToken cancellationToken = default) =>
        EntityLogic.GetPropertyHistoryAsync(Runtime, entityId, attrName, options, cancellationToken);

    public Task LinkEntitiesAsync(string fromId, string relName, string toId, object? props = null, WriteOptions? options = null, CancellationToken cancellationToken = default) =>
        RelationLogic.LinkEntitiesAsync(Runtime, new LinkEntitiesInput(fromId, relName, toId, props, options), cancellationToken);

    public Task UnlinkEntitiesAsync(string fromId, string relName, string toId, WriteOptions? options = null, CancellationToken cancellationToken = default) =>
        RelationLogic.UnlinkEntitiesAsync(Runtime, fromId, relName, toId, options, cancellationToken);

    public Task<NeighborResult> GetNeighborsAsync(string entityId, string? relName = null, OmDirection direction = OmDirection.Both, CancellationToken cancellationToken = default) =>
        RelationLogic.GetNeighborsAsync(Runtime, entityId, relName, direction, cancellationToken);

    public Task<NeighborResult> GetNeighborsAsOfAsync(string entityId, string? relName, string asOf, OmDirection direction = OmDirection.Both, CancellationToken cancellationToken = default) =>
        RelationLogic.GetNeighborsAsOfAsync(Runtime, entityId, relName, asOf, direction, cancellationToken);

    public Task<IReadOnlyList<EdgeHistoryEntry>> GetEdgeHistoryAsync(string fromId, string relName, string? toId = null, HistoryRangeOptions? options = null, CancellationToken cancellationToken = default) =>
        RelationLogic.GetEdgeHistoryAsync(Runtime, fromId, relName, toId, options, cancellationToken);

    public Task<EntityView?> GetEntityViewAsync(string entityId, CancellationToken cancellationToken = default) =>
        EntityLogic.GetEntityViewAsync(Runtime, entityId, cancellationToken);

    public Task<EntityView?> GetEntityViewAsOfAsync(string entityId, string asOf, CancellationToken cancellationToken = default) =>
        EntityLogic.GetEntityViewAsOfAsync(Runtime, entityId, asOf, cancellationToken);

    public Task<IReadOnlyList<OmEntity>> FindByTypeAsync(string typeName, FindByTypeOptions? options = null, CancellationToken cancellationToken = default) =>
        EntityLogic.FindByTypeAsync(Runtime, typeName, options, cancellationToken);

    public Task<IReadOnlyList<FindByTypeEntry>> FindByTypeWithPropertiesAsync(
        string typeName,
        IReadOnlyDictionary<string, object?>? filter = null,
        FindByTypeOptions? options = null,
        CancellationToken cancellationToken = default) =>
        EntityLogic.FindByTypeWithPropertiesAsync(Runtime, typeName, filter, options, cancellationToken);

    public Task<double> AggregateByTypeAsync(string typeName, string attrName, string op, FindByTypeOptions? options = null, CancellationToken cancellationToken = default) =>
        EntityLogic.AggregateByTypeAsync(Runtime, typeName, attrName, op, options, cancellationToken);

    public Task<IReadOnlyList<string>> GetAncestorsAsync(string typeName, CancellationToken cancellationToken = default) =>
        TypeLogic.GetAncestorsAsync(Runtime, typeName, cancellationToken);

    public Task<IReadOnlyList<string>> GetDescendantsAsync(string typeName, CancellationToken cancellationToken = default) =>
        TypeLogic.GetDescendantsAsync(Runtime, typeName, cancellationToken);

    public Task<bool> IsSubtypeOfAsync(string childType, string parentType, CancellationToken cancellationToken = default) =>
        TypeLogic.IsSubtypeOfAsync(Runtime, childType, parentType, cancellationToken);

    public Task<TypeHierarchy> GetTypeHierarchyAsync(CancellationToken cancellationToken = default) =>
        TypeLogic.GetTypeHierarchyAsync(Runtime, cancellationToken);

    public Task<string> ResolveTypeAsync(string typeName, CancellationToken cancellationToken = default) =>
        TypeLogic.ResolveTypeAsync(Runtime, typeName, cancellationToken);

    public Task<string> ResolveRelationAsync(string relName, CancellationToken cancellationToken = default) =>
        TypeLogic.ResolveRelAsync(Runtime, relName, cancellationToken);

    public Task<string> ResolveAttributeAsync(string typeName, string attrName, CancellationToken cancellationToken = default) =>
        TypeLogic.ResolveAttrAsync(Runtime, typeName, attrName, cancellationToken);

    public Task<IReadOnlyDictionary<string, OmAttribute>> GetAttributeDefinitionsAsync(string typeName, CancellationToken cancellationToken = default) =>
        TypeLogic.GetAttributeDefinitionsAsync(Runtime, typeName, cancellationToken);

    public Task<ValidationResult> ValidateEntityAsync(string entityId, CancellationToken cancellationToken = default) =>
        ConstraintLogic.ValidateEntityAsync(Runtime, entityId, cancellationToken);

    public Task ValidatePropertyTypeAsync(string entityId, string attrName, object? value, CancellationToken cancellationToken = default) =>
        EntityLogic.ValidatePropertyTypeAsync(Runtime, entityId, attrName, value, cancellationToken);

    public Task ValidateRelationAsync(string fromId, string relName, string toId, CancellationToken cancellationToken = default) =>
        RelationLogic.ValidateRelationAsync(Runtime, fromId, relName, toId, cancellationToken);

    public Task<IReadOnlyList<string>> ValidateRequiredPropertiesAsync(string entityId, CancellationToken cancellationToken = default) =>
        ConstraintLogic.ValidateRequiredPropertiesAsync(Runtime, entityId, cancellationToken);

    public Task FinalizeEntityAsync(string entityId, CancellationToken cancellationToken = default) =>
        ConstraintLogic.FinalizeEntityAsync(Runtime, entityId, cancellationToken);

    public Task DefineConstraintAsync(string typeName, string constraintName, string constraintType, string message = "", CancellationToken cancellationToken = default) =>
        ConstraintLogic.DefineConstraintAsync(Runtime, new DefineConstraintInput(typeName, constraintName, constraintType, message), cancellationToken);

    public async Task DefineConstraintAsync(
        string typeName,
        string constraintName,
        string constraintType,
        Func<OmValidationContext, ValueTask<bool>> when,
        Func<OmValidationContext, ValueTask<bool>> then,
        string message = "",
        CancellationToken cancellationToken = default)
    {
        await ConstraintLogic.DefineConstraintAsync(Runtime, new DefineConstraintInput(typeName, constraintName, constraintType, message), cancellationToken);
        Runtime.Registry.RegisterConstraint(typeName, constraintName, when, then);
    }

    public void RegisterValidator(string typeName, string constraintName, Func<OmValidationContext, ValueTask<string?>> validator) =>
        Runtime.Registry.RegisterValidator(typeName, constraintName, validator);

    public void RegisterConstraint(
        string typeName,
        string constraintName,
        Func<OmValidationContext, ValueTask<bool>> when,
        Func<OmValidationContext, ValueTask<bool>> then) =>
        Runtime.Registry.RegisterConstraint(typeName, constraintName, when, then);

    public Task DefineComputedAsync(string typeName, string attrName, string description = "", CancellationToken cancellationToken = default) =>
        ConstraintLogic.DefineComputedAsync(Runtime, new DefineComputedInput(typeName, attrName, description), cancellationToken);

    public void RegisterComputed(string typeName, string attrName, Func<OmComputedContext, ValueTask<object?>> compute) =>
        Runtime.Registry.RegisterComputed(typeName, attrName, compute);

    public Task DefineActionAsync(string typeName, string actionName, string description = "", CancellationToken cancellationToken = default) =>
        ConstraintLogic.DefineActionAsync(Runtime, new DefineActionInput(typeName, actionName, description), cancellationToken);

    public Task DefineMutationAsync(string typeName, string mutationName, string description = "", CancellationToken cancellationToken = default) =>
        ConstraintLogic.DefineMutationAsync(Runtime, new DefineMutationInput(typeName, mutationName, description), cancellationToken);

    public Task AddInterceptorAsync(string typeName, string actionName, string phase, int seq, string description = "", CancellationToken cancellationToken = default) =>
        ConstraintLogic.AddInterceptorAsync(Runtime, new AddInterceptorInput(typeName, actionName, phase, seq, description), cancellationToken);

    public async Task DefineActionAsync(
        string typeName,
        string actionName,
        Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>> handler,
        string description = "",
        CancellationToken cancellationToken = default)
    {
        await ConstraintLogic.DefineActionAsync(Runtime, new DefineActionInput(typeName, actionName, description), cancellationToken);
        Runtime.Registry.RegisterAction(typeName, actionName, handler);
    }

    public async Task DefineMutationAsync(
        string typeName,
        string mutationName,
        Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask> executor,
        string description = "",
        CancellationToken cancellationToken = default)
    {
        await ConstraintLogic.DefineMutationAsync(Runtime, new DefineMutationInput(typeName, mutationName, description), cancellationToken);
        Runtime.Registry.RegisterMutation(typeName, mutationName, executor);
    }

    public async Task AddInterceptorAsync(
        string typeName,
        string actionName,
        string phase,
        Func<OmActionContext, ValueTask> handler,
        string description = "",
        CancellationToken cancellationToken = default)
    {
        var seq = Runtime.Registry.RegisterInterceptor(typeName, actionName, phase, handler, description);
        await ConstraintLogic.AddInterceptorAsync(Runtime, new AddInterceptorInput(typeName, actionName, phase, seq, description), cancellationToken);
    }

    public Task ExecuteActionAsync(
        string entityId,
        string actionName,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default) =>
        ConstraintLogic.ExecuteActionAsync(Runtime, entityId, actionName, parameters, cancellationToken);

    public Task ExecuteMutationsAsync(
        string entityId,
        IReadOnlyList<MutationSpec>? mutations,
        CancellationToken cancellationToken = default) =>
        ConstraintLogic.ExecuteMutationsAsync(Runtime, entityId, mutations, cancellationToken);

    public Task<SchemaState> GetSchemaStateAsync(CancellationToken cancellationToken = default) =>
        SchemaLogic.GetSchemaStateAsync(Runtime, cancellationToken);

    public Task<IReadOnlyList<SchemaVersion>> ListSchemaVersionsAsync(CancellationToken cancellationToken = default) =>
        SchemaLogic.ListSchemaVersionsAsync(Runtime, cancellationToken);

    public Task<SchemaSnapshot> WriteSchemaSnapshotAsync(int version, string? label = null, string? description = null, CancellationToken cancellationToken = default) =>
        SchemaLogic.WriteSchemaSnapshotAsync(Runtime, version, label, description, cancellationToken);

    public Task<SchemaSnapshot?> ReadSchemaSnapshotAsync(int version, CancellationToken cancellationToken = default) =>
        SchemaLogic.ReadSchemaSnapshotAsync(Runtime, version, cancellationToken);

    public Task<SchemaDiff> DiffSchemaVersionsAsync(int fromVersion, int toVersion, CancellationToken cancellationToken = default) =>
        SchemaLogic.DiffSchemaVersionsAsync(Runtime, fromVersion, toVersion, cancellationToken);

    public Task<SchemaDiff> DiffCurrentAgainstSnapshotAsync(int fromVersion, CancellationToken cancellationToken = default) =>
        SchemaLogic.DiffCurrentAgainstSnapshotAsync(Runtime, fromVersion, cancellationToken);

    public Task RollbackSchemaAsync(int version, bool strict = false, CancellationToken cancellationToken = default) =>
        SchemaLogic.RollbackSchemaAsync(Runtime, version, strict, cancellationToken);

    public Task<SchemaMigrationResult> ApplySchemaMigrationAsync(SchemaMigrationSpec spec, CancellationToken cancellationToken = default) =>
        SchemaLogic.ApplySchemaMigrationAsync(Runtime, spec, cancellationToken);

    public Task<ExistentialRule> DefineExistentialRuleAsync(string ruleName, ExistentialRuleSpec spec, CancellationToken cancellationToken = default) =>
        ExistentialRuleLogic.DefineExistentialRuleAsync(Runtime, new DefineExistentialRuleInput(ruleName, spec), cancellationToken);

    public Task<IReadOnlyList<ExistentialRule>> ListExistentialRulesAsync(CancellationToken cancellationToken = default) =>
        ExistentialRuleLogic.ListExistentialRulesAsync(Runtime, cancellationToken);

    public Task<IReadOnlyList<ExistentialViolation>> CheckExistentialRulesAsync(CheckExistentialRulesInput? input = null, CancellationToken cancellationToken = default) =>
        ExistentialRuleLogic.CheckExistentialRulesAsync(Runtime, input, cancellationToken);

    public Task<ExistentialChaseResult> ApplyExistentialRulesAsync(ApplyExistentialRulesInput? input = null, CancellationToken cancellationToken = default) =>
        ExistentialRuleLogic.ApplyExistentialRulesAsync(Runtime, input, cancellationToken);

    public Task SeedPermissionMetadataAsync(CancellationToken cancellationToken = default) =>
        ConstraintLogic.SeedPermissionMetadataAsync(Runtime, cancellationToken);

    public Task SeedPermissionMetadataAsync(PermissionSeedInput input, CancellationToken cancellationToken = default) =>
        ConstraintLogic.SeedPermissionMetadataAsync(Runtime, input, cancellationToken);

    public Task DefinePermissionPolicyAsync(
        string policyId,
        string effect,
        string action,
        string resourceType,
        bool enabled = true,
        string description = "",
        CancellationToken cancellationToken = default) =>
        ConstraintLogic.DefinePermissionPolicyAsync(Runtime, new DefinePermissionPolicyInput(policyId, effect, action, resourceType, enabled, description), cancellationToken);

    public Task AddPermissionAbacRuleAsync(
        string policyId,
        string leftRef,
        string op,
        string rightRef,
        CancellationToken cancellationToken = default) =>
        ConstraintLogic.AddPermissionAbacRuleAsync(Runtime, new DefinePermissionAbacRuleInput(policyId, leftRef, op, rightRef), cancellationToken);

    public Task AddPermissionPathRuleAsync(string policyId, string path, CancellationToken cancellationToken = default) =>
        ConstraintLogic.AddPermissionPathRuleAsync(Runtime, new AddPermissionPathRuleInput(policyId, path), cancellationToken);

    public Task<CheckAccessResult> CheckAccessAsync(CheckAccessInput input, CancellationToken cancellationToken = default) =>
        ConstraintLogic.CheckAccessAsync(Runtime, input, cancellationToken);
}
