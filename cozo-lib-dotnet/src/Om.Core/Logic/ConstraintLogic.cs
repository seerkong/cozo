using System.Runtime.ExceptionServices;
using System.Collections.Immutable;
using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;
using Cozo.DotNet.Om.Internals;
using Cozo.DotNet.Om.Runtime;
using Cozo.DotNet.Om.Support;

namespace Cozo.DotNet.Om.Logic;

public static class ConstraintLogic
{
    public static async Task DefineConstraintAsync(CozoOmRuntime runtime, DefineConstraintInput input, CancellationToken cancellationToken = default)
    {
        var typeName = await ResolveExistingOwnerTypeAsync(runtime, input.TypeName, cancellationToken);
        var constraintType = ValidateConstraintType(input.ConstraintType);
        await runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_constraint_def", ["type_name", "constraint_name"], ["constraint_type", "message"]),
            LogicSupport.Params(
                ("type_name", typeName),
                ("constraint_name", OmConvert.RequireName(input.ConstraintName, nameof(input.ConstraintName))),
                ("constraint_type", constraintType),
                ("message", input.Message)),
            cancellationToken: cancellationToken);
    }

    public static async Task DefineComputedAsync(CozoOmRuntime runtime, DefineComputedInput input, CancellationToken cancellationToken = default)
    {
        var typeName = await ResolveExistingOwnerTypeAsync(runtime, input.TypeName, cancellationToken);
        await runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_computed_def", ["type_name", "attr_name"], ["description"]),
            LogicSupport.Params(
                ("type_name", typeName),
                ("attr_name", OmConvert.RequireName(input.AttrName, nameof(input.AttrName))),
                ("description", input.Description)),
            cancellationToken: cancellationToken);
    }

    public static async Task DefineActionAsync(CozoOmRuntime runtime, DefineActionInput input, CancellationToken cancellationToken = default)
    {
        var typeName = await ResolveExistingOwnerTypeAsync(runtime, input.TypeName, cancellationToken);
        await runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_action_def", ["type_name", "action_name"], ["description"]),
            LogicSupport.Params(
                ("type_name", typeName),
                ("action_name", OmConvert.RequireName(input.ActionName, nameof(input.ActionName))),
                ("description", input.Description)),
            cancellationToken: cancellationToken);
    }

    public static async Task DefineMutationAsync(CozoOmRuntime runtime, DefineMutationInput input, CancellationToken cancellationToken = default)
    {
        var typeName = await ResolveExistingOwnerTypeAsync(runtime, input.TypeName, cancellationToken);
        await runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_mutation_def", ["type_name", "mutation_name"], ["description"]),
            LogicSupport.Params(
                ("type_name", typeName),
                ("mutation_name", OmConvert.RequireName(input.MutationName, nameof(input.MutationName))),
                ("description", input.Description)),
            cancellationToken: cancellationToken);
    }

    public static async Task AddInterceptorAsync(CozoOmRuntime runtime, AddInterceptorInput input, CancellationToken cancellationToken = default)
    {
        var typeName = await ResolveExistingOwnerTypeAsync(runtime, input.TypeName, cancellationToken);
        var phase = NormalizeInterceptorPhase(input.Phase);
        await runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_interceptor_def", ["type_name", "action_name", "phase", "seq"], ["description"]),
            LogicSupport.Params(
                ("type_name", typeName),
                ("action_name", OmConvert.RequireName(input.ActionName, nameof(input.ActionName))),
                ("phase", phase),
                ("seq", input.Seq),
                ("description", input.Description)),
            cancellationToken: cancellationToken);
    }

    internal static async Task DefineConstraintCallbackAsync(
        CozoOmRuntime runtime,
        DefineConstraintInput input,
        Func<OmValidationContext, ValueTask<bool>>? when,
        Func<OmValidationContext, ValueTask<bool>>? then,
        Action? afterRegistration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(when);
        ArgumentNullException.ThrowIfNull(then);
        var typeName = await ResolveExistingOwnerTypeAsync(runtime, input.TypeName, cancellationToken);
        var constraintName = OmConvert.RequireName(input.ConstraintName, nameof(input.ConstraintName));
        var previousMetadata = await ReadConstraintMetadataAsync(runtime, typeName, constraintName, cancellationToken);
        var hadRegistration = runtime.Registry.TryGetConstraint(typeName, constraintName, out var previousRegistration);
        await DefineConstraintAsync(runtime, input with { TypeName = typeName, ConstraintName = constraintName }, cancellationToken);
        try
        {
            runtime.Registry.RegisterConstraint(typeName, constraintName, when, then);
            afterRegistration?.Invoke();
        }
        catch (Exception registrationFailure)
        {
            await RethrowAfterRegistrationRollbackAsync(
                registrationFailure,
                () =>
                {
                    runtime.Registry.UnregisterConstraint(typeName, constraintName);
                    if (hadRegistration)
                    {
                        runtime.Registry.RestoreConstraintRegistration(typeName, constraintName, previousRegistration);
                    }
                },
                () => RestoreConstraintMetadataAsync(runtime, typeName, constraintName, previousMetadata, cancellationToken));
        }
    }

    internal static async Task DefineActionCallbackAsync(
        CozoOmRuntime runtime,
        DefineActionInput input,
        Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>>? handler,
        Action? afterRegistration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var typeName = await ResolveExistingOwnerTypeAsync(runtime, input.TypeName, cancellationToken);
        var actionName = OmConvert.RequireName(input.ActionName, nameof(input.ActionName));
        var previousMetadata = await ReadActionMetadataAsync(runtime, typeName, actionName, cancellationToken);
        var hadRegistration = runtime.Registry.TryGetActionRegistration(typeName, actionName, out var previousRegistration);
        await DefineActionAsync(runtime, input with { TypeName = typeName, ActionName = actionName }, cancellationToken);
        try
        {
            runtime.Registry.RegisterAction(typeName, actionName, handler);
            afterRegistration?.Invoke();
        }
        catch (Exception registrationFailure)
        {
            await RethrowAfterRegistrationRollbackAsync(
                registrationFailure,
                () =>
                {
                    runtime.Registry.UnregisterAction(typeName, actionName);
                    if (hadRegistration)
                    {
                        runtime.Registry.RestoreActionRegistration(typeName, actionName, previousRegistration);
                    }
                },
                () => RestoreActionMetadataAsync(runtime, typeName, actionName, previousMetadata, cancellationToken));
        }
    }

    internal static async Task DefineMutationCallbackAsync(
        CozoOmRuntime runtime,
        DefineMutationInput input,
        Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask>? executor,
        Action? afterRegistration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        var typeName = await ResolveExistingOwnerTypeAsync(runtime, input.TypeName, cancellationToken);
        var mutationName = OmConvert.RequireName(input.MutationName, nameof(input.MutationName));
        var previousMetadata = await ReadMutationMetadataAsync(runtime, typeName, mutationName, cancellationToken);
        var hadRegistration = runtime.Registry.TryGetMutationRegistration(typeName, mutationName, out var previousRegistration);
        await DefineMutationAsync(runtime, input with { TypeName = typeName, MutationName = mutationName }, cancellationToken);
        try
        {
            runtime.Registry.RegisterMutation(typeName, mutationName, executor);
            afterRegistration?.Invoke();
        }
        catch (Exception registrationFailure)
        {
            await RethrowAfterRegistrationRollbackAsync(
                registrationFailure,
                () =>
                {
                    runtime.Registry.UnregisterMutation(typeName, mutationName);
                    if (hadRegistration)
                    {
                        runtime.Registry.RestoreMutationRegistration(typeName, mutationName, previousRegistration);
                    }
                },
                () => RestoreMutationMetadataAsync(runtime, typeName, mutationName, previousMetadata, cancellationToken));
        }
    }

    internal static async Task AddInterceptorCallbackAsync(
        CozoOmRuntime runtime,
        AddInterceptorInput input,
        Func<OmActionContext, ValueTask>? handler,
        Action? afterRegistration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var typeName = await ResolveExistingOwnerTypeAsync(runtime, input.TypeName, cancellationToken);
        var actionName = OmConvert.RequireName(input.ActionName, nameof(input.ActionName));
        var phase = NormalizeInterceptorPhase(input.Phase);
        var seq = await NextInterceptorSeqAsync(runtime, typeName, actionName, phase, cancellationToken);
        var previousMetadata = await ReadInterceptorMetadataAsync(runtime, typeName, actionName, phase, seq, cancellationToken);
        var hadRegistration = runtime.Registry.TryGetInterceptor(typeName, actionName, phase, seq, out var previousRegistration);
        await AddInterceptorAsync(runtime, input with { TypeName = typeName, ActionName = actionName, Phase = phase, Seq = seq }, cancellationToken);
        try
        {
            runtime.Registry.RegisterInterceptor(typeName, actionName, phase, seq, handler, input.Description);
            afterRegistration?.Invoke();
        }
        catch (Exception registrationFailure)
        {
            await RethrowAfterRegistrationRollbackAsync(
                registrationFailure,
                () =>
                {
                    runtime.Registry.UnregisterInterceptor(typeName, actionName, phase, seq);
                    if (hadRegistration)
                    {
                        runtime.Registry.RestoreInterceptorRegistration(typeName, actionName, phase, previousRegistration);
                    }
                },
                () => RestoreInterceptorMetadataAsync(
                    runtime,
                    typeName,
                    actionName,
                    phase,
                    seq,
                    previousMetadata,
                    cancellationToken));
        }
    }

    internal static async Task<IReadOnlyList<MutationSpec>> CallParentActionAsync(
        CozoOmRuntime runtime,
        string entityId,
        string typeName,
        string actionOwnerType,
        string actionName,
        IReadOnlyDictionary<string, object?>? parameters = null,
        BehaviorResolutionScope? outerResolution = null,
        CancellationToken cancellationToken = default)
    {
        var action = OmConvert.RequireName(actionName, nameof(actionName));
        var parentResolution = outerResolution is null
            ? await runtime.BehaviorGate.ResolveAsync(
                runtime,
                async (resolution, token) => new ParentActionResolution(
                    resolution,
                    await ResolveParentActionAsync(runtime, resolution, actionOwnerType, action, token)),
                cancellationToken)
            : await runtime.BehaviorGate.ResolveAsync(
                outerResolution,
                async (resolution, token) => new ParentActionResolution(
                    resolution,
                    await ResolveParentActionAsync(runtime, resolution, actionOwnerType, action, token)),
                cancellationToken);
        if (parentResolution.Action is null)
        {
            throw new InvalidOperationException(
                $"Parent action '{action}' not defined above current owner '{actionOwnerType}'");
        }

        var context = new OmActionContext(
            runtime,
            entityId,
            typeName,
            parentResolution.Action.OwnerType,
            parameters ?? new Dictionary<string, object?>())
        {
            BehaviorResolution = parentResolution.Resolution,
        };
        return await parentResolution.Action.Handler(context, context.Params) ?? [];
    }

    public static async Task ExecuteActionAsync(
        CozoOmRuntime runtime,
        string entityId,
        string actionName,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await runtime.Store.BeginTransactionAsync(write: true, cancellationToken);
        var txRuntime = runtime with { Store = tx };

        await ExecuteActionCoreAsync(txRuntime, entityId, actionName, parameters, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteActionCoreAsync(
        CozoOmRuntime runtime,
        string entityId,
        string actionName,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var id = OmConvert.RequireName(entityId, nameof(entityId));
        var action = OmConvert.RequireName(actionName, nameof(actionName));
        var pipeline = await runtime.BehaviorGate.ResolveAsync(
            runtime,
            async (resolution, token) =>
            {
                var typeName = await EntityLogic.GetEntityTypeAsync(runtime, id, token);
                var actionRegistration = await ResolveActionAsync(runtime, resolution, typeName, action, token);
                if (actionRegistration is null)
                {
                    return new ResolvedActionPipeline(resolution, typeName, null, [], []);
                }

                var beforeInterceptors = await CollectInterceptorsAsync(
                    runtime,
                    resolution,
                    typeName,
                    action,
                    "before",
                    token);
                var afterInterceptors = await CollectInterceptorsAsync(
                    runtime,
                    resolution,
                    typeName,
                    action,
                    "after",
                    token);
                return new ResolvedActionPipeline(
                    resolution,
                    typeName,
                    actionRegistration,
                    beforeInterceptors,
                    afterInterceptors);
            },
            cancellationToken);
        if (pipeline.Action is null)
        {
            throw new InvalidOperationException($"Action '{action}' not defined for type '{pipeline.TypeName}'");
        }

        var ctx = new OmActionContext(
            runtime,
            id,
            pipeline.TypeName,
            pipeline.Action.OwnerType,
            parameters ?? new Dictionary<string, object?>())
        {
            BehaviorResolution = pipeline.Resolution,
        };
        foreach (var interceptor in pipeline.BeforeInterceptors)
        {
            await interceptor.Handler(ctx);
        }

        var mutations = await pipeline.Action.Handler(ctx, ctx.Params);
        await ExecuteMutationsCoreAsync(runtime, id, mutations, pipeline.Resolution, cancellationToken);

        foreach (var interceptor in pipeline.AfterInterceptors)
        {
            await interceptor.Handler(ctx);
        }
    }

    public static async Task ExecuteMutationsAsync(
        CozoOmRuntime runtime,
        string entityId,
        IReadOnlyList<MutationSpec>? mutations,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await runtime.Store.BeginTransactionAsync(write: true, cancellationToken);
        var txRuntime = runtime with { Store = tx };

        await ExecuteMutationsCoreAsync(txRuntime, entityId, mutations, null, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteMutationsCoreAsync(
        CozoOmRuntime runtime,
        string entityId,
        IReadOnlyList<MutationSpec>? mutations,
        BehaviorResolutionScope? outerResolution,
        CancellationToken cancellationToken = default)
    {
        var id = OmConvert.RequireName(entityId, nameof(entityId));
        async Task<ResolvedMutationBatch> ResolveAsync(BehaviorResolutionScope resolution, CancellationToken token)
        {
            var typeName = await EntityLogic.GetEntityTypeAsync(runtime, id, token);
            var resolved = new List<ResolvedMutation>();
            foreach (var item in mutations ?? [])
            {
                var mutationName = OmConvert.RequireName(item.Mutation, nameof(item.Mutation));
                var registration = await ResolveMutationAsync(
                    runtime,
                    resolution,
                    typeName,
                    mutationName,
                    token);
                if (registration is null)
                {
                    throw new InvalidOperationException($"Mutation '{mutationName}' not defined for type '{typeName}'");
                }

                resolved.Add(new ResolvedMutation(
                    registration.Executor,
                    item.Params ?? new Dictionary<string, object?>()));
            }

            return new ResolvedMutationBatch(typeName, resolved);
        }

        var batch = outerResolution is null
            ? await runtime.BehaviorGate.ResolveAsync(runtime, ResolveAsync, cancellationToken)
            : await runtime.BehaviorGate.ResolveAsync(outerResolution, ResolveAsync, cancellationToken);
        var ctx = new OmMutationContext(runtime, id, batch.TypeName);
        foreach (var mutation in batch.Mutations)
        {
            await mutation.Executor(ctx, mutation.Parameters);
        }
    }

    public static async Task<ValidationResult> ValidateEntityAsync(CozoOmRuntime runtime, string entityId, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var typeName = await EntityLogic.GetEntityTypeAsync(runtime, entityId, cancellationToken);
        var definitions = await TypeLogic.GetAttributeDefinitionsAsync(runtime, typeName, cancellationToken);
        var properties = await EntityLogic.GetAllPropertiesAsync(runtime, entityId, cancellationToken);

        foreach (var attrName in await ValidateRequiredPropertiesAsync(runtime, entityId, cancellationToken))
        {
            errors.Add($"Missing required property '{attrName}'");
        }

        foreach (var (attrName, value) in properties)
        {
            if (!definitions.TryGetValue(attrName, out var definition))
            {
                errors.Add($"Undefined property '{attrName}' for type '{typeName}'");
                continue;
            }

            if (definition.ValueType == OmValueType.Validity)
            {
                continue;
            }

            var actual = OmConvert.InferValueType(value);
            if (definition.ValueType != OmValueType.Json && definition.ValueType != actual)
            {
                errors.Add($"Property '{attrName}' expects {definition.ValueType}, got {actual}");
            }
        }

        var resolvedConstraints = await runtime.BehaviorGate.ResolveAsync(
            runtime,
            async (resolution, token) =>
            {
                var resolved = new List<ResolvedConstraint>();
                foreach (var constraint in await ListEffectiveConstraintsAsync(runtime, typeName, token))
                {
                    resolved.Add(await ResolveConstraintDefinitionAsync(runtime, resolution, constraint, token));
                }

                return (IReadOnlyList<ResolvedConstraint>)resolved;
            },
            cancellationToken);
        foreach (var constraint in resolvedConstraints)
        {
            await EvaluateResolvedConstraintAsync(
                constraint,
                new OmValidationContext(runtime, entityId, typeName),
                errors);
        }

        return new ValidationResult(errors.Count == 0, errors);
    }

    public static async Task<ValidationResult> ValidateConstraintsAsync(
        CozoOmRuntime runtime,
        string entityId,
        IReadOnlyList<string>? types = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var typeName = await EntityLogic.GetEntityTypeAsync(runtime, entityId, cancellationToken);
        var wanted = types is null
            ? null
            : new HashSet<string>(types.Select(NormalizeConstraintType), StringComparer.Ordinal);
        var ctx = new OmValidationContext(runtime, entityId, typeName);
        var resolvedConstraints = await runtime.BehaviorGate.ResolveAsync(
            runtime,
            async (resolution, token) =>
            {
                var resolved = new List<ResolvedConstraint>();
                foreach (var constraint in await ListEffectiveConstraintsAsync(runtime, typeName, token))
                {
                    if (wanted is not null && !wanted.Contains(constraint.Type)) continue;
                    resolved.Add(await ResolveConstraintDefinitionAsync(runtime, resolution, constraint, token));
                }

                return (IReadOnlyList<ResolvedConstraint>)resolved;
            },
            cancellationToken);
        foreach (var constraint in resolvedConstraints)
        {
            await EvaluateResolvedConstraintAsync(constraint, ctx, errors);
        }

        return new ValidationResult(errors.Count == 0, errors);
    }

    public static async Task<IReadOnlyList<string>> ValidateRequiredPropertiesAsync(
        CozoOmRuntime runtime,
        string entityId,
        CancellationToken cancellationToken = default)
    {
        var typeName = await EntityLogic.GetEntityTypeAsync(runtime, entityId, cancellationToken);
        var definitions = await TypeLogic.GetAttributeDefinitionsAsync(runtime, typeName, cancellationToken);
        var properties = await EntityLogic.GetAllPropertiesAsync(runtime, entityId, cancellationToken);
        return definitions
            .Where(item => item.Value.Required && !properties.ContainsKey(item.Key))
            .Select(item => item.Key)
            .ToArray();
    }

    public static async Task FinalizeEntityAsync(CozoOmRuntime runtime, string entityId, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateEntityAsync(runtime, entityId, cancellationToken);
        if (!validation.Valid)
        {
            throw new CozoException(string.Join("; ", validation.Errors));
        }
    }

    public static async Task<IReadOnlyList<string>> ListComputedAttrsAsync(CozoOmRuntime runtime, string typeName, CancellationToken cancellationToken = default)
    {
        var result = await runtime.Store.RunAsync(
            """
            ?[attr_name] :=
              *om_computed_def{ type_name: $type_name, attr_name, description: _description }
            :sort attr_name
            """,
            LogicSupport.Params(("type_name", typeName)),
            cancellationToken: cancellationToken);
        return result.Rows.Select(row => JsonRows.StringAt(row, 0) ?? "").Where(x => x.Length > 0).ToArray();
    }

    public static async Task SeedPermissionMetadataAsync(CozoOmRuntime runtime, CancellationToken cancellationToken = default)
    {
        foreach (var (action, description) in new[] { ("read", "Read resource"), ("write", "Write resource"), ("admin", "Administer resource") })
        {
            await runtime.Store.RunAsync(
                CozoScriptBuilder.InputPut("om_perm_action", ["action"], ["description"]),
                LogicSupport.Params(("action", action), ("description", description)),
                cancellationToken: cancellationToken);
        }
    }

    public static async Task SeedPermissionMetadataAsync(
        CozoOmRuntime runtime,
        PermissionSeedInput input,
        CancellationToken cancellationToken = default)
    {
        foreach (var item in input.Actions ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Action)) continue;
            await runtime.Store.RunAsync(
                CozoScriptBuilder.InputPut("om_perm_action", ["action"], ["description"]),
                LogicSupport.Params(
                    ("action", item.Action.Trim()),
                    ("description", item.Description ?? "")),
                cancellationToken: cancellationToken);
        }

        foreach (var item in input.Policies ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.PolicyId)) continue;
            await DefinePermissionPolicyAsync(
                runtime,
                new DefinePermissionPolicyInput(
                    item.PolicyId,
                    item.Effect,
                    item.Action,
                    item.ResourceType,
                    item.Enabled,
                    item.Description),
                cancellationToken);
        }

        foreach (var item in input.AbacRules ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.PolicyId) ||
                string.IsNullOrWhiteSpace(item.LeftRef) ||
                string.IsNullOrWhiteSpace(item.Op) ||
                string.IsNullOrWhiteSpace(item.RightRef))
            {
                continue;
            }

            await AddPermissionAbacRuleAsync(
                runtime,
                new DefinePermissionAbacRuleInput(item.PolicyId, item.LeftRef, item.Op, item.RightRef),
                cancellationToken);
        }

        foreach (var item in input.PathRules ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.PolicyId) || string.IsNullOrWhiteSpace(item.Path))
            {
                continue;
            }

            await AddPermissionPathRuleAsync(
                runtime,
                new AddPermissionPathRuleInput(item.PolicyId, item.Path),
                cancellationToken);
        }
    }

    public static Task DefinePermissionPolicyAsync(CozoOmRuntime runtime, DefinePermissionPolicyInput input, CancellationToken cancellationToken = default)
    {
        return runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_perm_policy", ["policy_id"], ["effect", "action", "resource_type", "enabled", "description"]),
            LogicSupport.Params(
                ("policy_id", OmConvert.RequireName(input.PolicyId, nameof(input.PolicyId))),
                ("effect", OmConvert.RequireName(input.Effect, nameof(input.Effect)).ToLowerInvariant()),
                ("action", OmConvert.RequireName(input.Action, nameof(input.Action))),
                ("resource_type", OmConvert.RequireName(input.ResourceType, nameof(input.ResourceType))),
                ("enabled", input.Enabled),
                ("description", input.Description)),
            cancellationToken: cancellationToken);
    }

    public static Task AddPermissionAbacRuleAsync(CozoOmRuntime runtime, DefinePermissionAbacRuleInput input, CancellationToken cancellationToken = default)
    {
        return runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_perm_abac_rule", ["policy_id", "left_ref", "op", "right_ref"], []),
            LogicSupport.Params(
                ("policy_id", OmConvert.RequireName(input.PolicyId, nameof(input.PolicyId))),
                ("left_ref", OmConvert.RequireName(input.LeftRef, nameof(input.LeftRef))),
                ("op", OmConvert.RequireName(input.Op, nameof(input.Op))),
                ("right_ref", OmConvert.RequireName(input.RightRef, nameof(input.RightRef)))),
            cancellationToken: cancellationToken);
    }

    public static Task AddPermissionPathRuleAsync(CozoOmRuntime runtime, AddPermissionPathRuleInput input, CancellationToken cancellationToken = default)
    {
        return runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_perm_path_rule", ["policy_id", "path"], []),
            LogicSupport.Params(
                ("policy_id", OmConvert.RequireName(input.PolicyId, nameof(input.PolicyId))),
                ("path", OmConvert.RequireName(input.Path, nameof(input.Path)))),
            cancellationToken: cancellationToken);
    }

    public static async Task<CheckAccessResult> CheckAccessAsync(
        CozoOmRuntime runtime,
        CheckAccessInput input,
        CancellationToken cancellationToken = default)
    {
        var asOf = string.IsNullOrWhiteSpace(input.AsOf)
            ? null
            : OmConvert.NormalizeTimestamp(input.AsOf, nameof(input.AsOf));
        string subjectType;
        try
        {
            subjectType = await EntityLogic.GetEntityTypeAsync(runtime, input.SubjectId, cancellationToken);
        }
        catch (CozoException)
        {
            return CreateFailClosedAccessResult(input, asOf, "missing_subject");
        }

        string resourceType;
        try
        {
            resourceType = await EntityLogic.GetEntityTypeAsync(runtime, input.ResourceId, cancellationToken);
        }
        catch (CozoException)
        {
            return CreateFailClosedAccessResult(input, asOf, "missing_resource");
        }

        var resourceScopes = new HashSet<string>(StringComparer.Ordinal)
        {
            resourceType
        };
        foreach (var ancestor in await TypeLogic.GetAncestorsAsync(runtime, resourceType, cancellationToken))
        {
            resourceScopes.Add(ancestor);
        }

        if (!string.IsNullOrWhiteSpace(input.FieldName))
        {
            foreach (var scope in resourceScopes.ToArray())
            {
                resourceScopes.Add($"{scope}.{input.FieldName}");
            }
        }

        var policyRows = await runtime.Store.RunAsync(
            """
            ?[policy_id, effect, action, resource_type, enabled, description] :=
              *om_perm_policy{ policy_id, effect, action, resource_type, enabled, description },
              enabled = true
            :sort policy_id
            """,
            cancellationToken: cancellationToken);

        var matched = new List<object>();
        var evaluations = ImmutableArray.CreateBuilder<PermissionPolicyEvaluation>();
        var fieldVisibility = ImmutableDictionary<string, PermissionFieldVisibility>.Empty.WithComparers(StringComparer.Ordinal);
        var allow = false;
        var deny = false;
        foreach (var row in policyRows.Rows)
        {
            var policy = new PermissionPolicyRow(
                JsonRows.StringAt(row, 0) ?? "",
                JsonRows.StringAt(row, 1) ?? "",
                JsonRows.StringAt(row, 2) ?? "",
                JsonRows.StringAt(row, 3) ?? "",
                JsonRows.StringAt(row, 5) ?? "");
            if (policy.PolicyId.Length == 0) continue;
            if (!string.Equals(policy.Action, input.Action, StringComparison.Ordinal)) continue;
            if (!resourceScopes.Contains(policy.ResourceType)) continue;

            var paths = await PermissionPathsAsync(runtime, policy.PolicyId, cancellationToken);
            var witness = await DescribePermissionWitnessAsync(
                runtime,
                paths,
                input.SubjectId,
                input.ResourceId,
                asOf,
                cancellationToken);
            var abac = await EvaluatePermissionAbacAsync(
                runtime,
                policy.PolicyId,
                input,
                subjectType,
                resourceType,
                asOf,
                cancellationToken);
            var status = witness.Status == PermissionEvaluationStatus.Invalid || abac.HasInvalid
                ? PermissionEvaluationStatus.Invalid
                : witness.Status != PermissionEvaluationStatus.Matched || !abac.Matches
                    ? PermissionEvaluationStatus.Unmatched
                    : PermissionEvaluationStatus.Matched;
            var diagnostics = witness.Diagnostics
                .Concat(abac.Diagnostics.Where(diagnostic => diagnostic.Detail is not null).Select(diagnostic => diagnostic.Detail!))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
                .ToImmutableArray();
            evaluations.Add(new PermissionPolicyEvaluation(
                policy.PolicyId,
                policy.Effect,
                policy.Action,
                policy.ResourceType,
                status,
                witness,
                abac.Diagnostics,
                diagnostics));

            if (status != PermissionEvaluationStatus.Matched) continue;
            if (string.Equals(policy.Effect, "allow", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var field in abac.HiddenFields)
                {
                    fieldVisibility = fieldVisibility.SetItem(field, PermissionFieldVisibility.Hidden);
                }
            }
            matched.Add(new
            {
                policyId = policy.PolicyId,
                effect = policy.Effect,
                action = policy.Action,
                resourceType = policy.ResourceType,
                declaredPaths = witness.DeclaredPaths,
                witness = witness.Hops,
            });
            if (string.Equals(policy.Effect, "deny", StringComparison.OrdinalIgnoreCase)) deny = true;
            if (string.Equals(policy.Effect, "allow", StringComparison.OrdinalIgnoreCase)) allow = true;
        }

        var finalAllow = allow && !deny;
        var explanation = JsonSerializer.SerializeToElement(new
        {
            subjectId = input.SubjectId,
            action = input.Action,
            resourceId = input.ResourceId,
            fieldName = input.FieldName,
            asOf,
            resourceType,
            matchedPolicies = matched,
            evaluatedPolicies = evaluations.Select(ToExplanationPolicyEvaluation),
            fieldVisibility = fieldVisibility.ToDictionary(
                entry => entry.Key,
                entry => ToExplanationVisibility(entry.Value),
                StringComparer.Ordinal),
            diagnostics = Array.Empty<string>(),
            decision = deny ? "deny" : finalAllow ? "allow" : "deny",
            reason = deny ? "deny policy matched" : finalAllow ? "allow policy matched" : "no allow policy matched"
        }, OmConvert.JsonOptions);
        return new CheckAccessResult(finalAllow, explanation.Clone())
        {
            AsOf = asOf,
            FieldVisibility = fieldVisibility,
            PolicyEvaluations = evaluations.ToImmutable(),
            Diagnostics = [],
        };
    }

    private static CheckAccessResult CreateFailClosedAccessResult(CheckAccessInput input, string? asOf, string diagnostic)
    {
        var diagnostics = ImmutableArray.Create(diagnostic);
        var explanation = JsonSerializer.SerializeToElement(new
        {
            subjectId = input.SubjectId,
            action = input.Action,
            resourceId = input.ResourceId,
            fieldName = input.FieldName,
            asOf,
            resourceType = (string?)null,
            matchedPolicies = Array.Empty<object>(),
            evaluatedPolicies = Array.Empty<object>(),
            fieldVisibility = new Dictionary<string, string>(StringComparer.Ordinal),
            diagnostics,
            decision = "deny",
            reason = diagnostic,
        }, OmConvert.JsonOptions);
        return new CheckAccessResult(false, explanation.Clone())
        {
            AsOf = asOf,
            Diagnostics = diagnostics,
        };
    }

    private static object ToExplanationPolicyEvaluation(PermissionPolicyEvaluation evaluation) => new
    {
        policyId = evaluation.PolicyId,
        effect = evaluation.Effect,
        action = evaluation.Action,
        resourceType = evaluation.ResourceType,
        status = ToExplanationStatus(evaluation.Status),
        path = new
        {
            status = ToExplanationStatus(evaluation.Witness.Status),
            declaredPaths = evaluation.Witness.DeclaredPaths,
            witness = evaluation.Witness.Hops,
            diagnostics = evaluation.Witness.Diagnostics,
        },
        abacDiagnostics = evaluation.AbacDiagnostics.Select(diagnostic => new
        {
            leftRef = diagnostic.LeftRef,
            op = diagnostic.Operator,
            rightRef = diagnostic.RightRef,
            status = ToExplanationStatus(diagnostic.Status),
            diagnostic.Detail,
        }),
        diagnostics = evaluation.Diagnostics,
    };

    private static string ToExplanationStatus(PermissionEvaluationStatus status) => status switch
    {
        PermissionEvaluationStatus.Matched => "matched",
        PermissionEvaluationStatus.Unmatched => "unmatched",
        PermissionEvaluationStatus.Invalid => "invalid",
        _ => "not_evaluated",
    };

    private static string ToExplanationVisibility(PermissionFieldVisibility visibility) => visibility switch
    {
        PermissionFieldVisibility.Hidden => "hidden",
        _ => "visible",
    };

    private static async Task<ResolvedConstraint> ResolveConstraintDefinitionAsync(
        CozoOmRuntime runtime,
        BehaviorResolutionScope resolution,
        ConstraintDefinition constraint,
        CancellationToken cancellationToken)
    {
        var validatorKey = ConstraintBindingKey(constraint, BehaviorCallbackSlot.Validator);
        var whenKey = ConstraintBindingKey(constraint, BehaviorCallbackSlot.When);
        var thenKey = ConstraintBindingKey(constraint, BehaviorCallbackSlot.Then);

        if (string.Equals(constraint.Type, "custom", StringComparison.OrdinalIgnoreCase))
        {
            await BehaviorReadinessLogic.EnsureReadyIfBoundAsync(
                runtime,
                resolution,
                validatorKey,
                cancellationToken);
            await BehaviorReadinessLogic.EnsureReadyIfBoundAsync(runtime, resolution, whenKey, cancellationToken);
            await BehaviorReadinessLogic.EnsureReadyIfBoundAsync(runtime, resolution, thenKey, cancellationToken);
            resolution.RegistrySnapshot.Validators.TryGetValue(
                (constraint.OwnerType, constraint.Name),
                out var validatorRegistration);
            resolution.RegistrySnapshot.Constraints.TryGetValue(
                (constraint.OwnerType, constraint.Name),
                out var customConstraint);
            return new ResolvedConstraint(
                constraint,
                validatorRegistration?.Callback,
                customConstraint);
        }

        await BehaviorReadinessLogic.EnsureReadyIfBoundAsync(runtime, resolution, whenKey, cancellationToken);
        await BehaviorReadinessLogic.EnsureReadyIfBoundAsync(runtime, resolution, thenKey, cancellationToken);
        resolution.RegistrySnapshot.Constraints.TryGetValue(
            (constraint.OwnerType, constraint.Name),
            out var scopedConstraint);
        return new ResolvedConstraint(constraint, null, scopedConstraint);
    }

    private static async Task EvaluateResolvedConstraintAsync(
        ResolvedConstraint resolved,
        OmValidationContext ctx,
        List<string> errors)
    {
        if (resolved.Validator is not null)
        {
            var message = await resolved.Validator(ctx);
            if (!string.IsNullOrWhiteSpace(message))
            {
                errors.Add(message!);
            }
        }

        if (resolved.Constraint is not null)
        {
            await EvaluateScopedConstraintAsync(resolved.Constraint, resolved.Definition, ctx, errors);
        }
    }

    private static BehaviorBindingKey ConstraintBindingKey(
        ConstraintDefinition constraint,
        BehaviorCallbackSlot slot) =>
        new(
            BehaviorKind.Constraint,
            constraint.OwnerType,
            constraint.Name,
            slot,
            BehaviorBindingLogic.NonInterceptorPhase,
            BehaviorBindingLogic.NonInterceptorSeq);

    private static async Task EvaluateScopedConstraintAsync(
        OmConstraintRegistration registration,
        ConstraintDefinition constraint,
        OmValidationContext ctx,
        List<string> errors)
    {
        bool active;
        try
        {
            active = await registration.When(ctx);
        }
        catch (Exception ex) when (ex is not BehaviorUnresolvedException)
        {
            errors.Add($"Constraint '{constraint.Name}' evaluation failed (when): {ex.Message}");
            return;
        }

        if (!active) return;

        bool valid;
        try
        {
            valid = await registration.Then(ctx);
        }
        catch (Exception ex) when (ex is not BehaviorUnresolvedException)
        {
            errors.Add($"Constraint '{constraint.Name}' evaluation failed (then): {ex.Message}");
            return;
        }

        if (!valid)
        {
            var message = string.IsNullOrWhiteSpace(constraint.Message) ? "" : $": {constraint.Message}";
            errors.Add($"Constraint '{constraint.Name}' violated{message}");
        }
    }

    private static async Task<IReadOnlyList<ConstraintDefinition>> ListEffectiveConstraintsAsync(
        CozoOmRuntime runtime,
        string typeName,
        CancellationToken cancellationToken)
    {
        var canonical = await TypeLogic.ResolveTypeAsync(runtime, typeName, cancellationToken);
        var chain = (await TypeLogic.GetAncestorsAsync(runtime, canonical, cancellationToken)).Reverse().Concat([canonical]);
        var constraints = new Dictionary<string, ConstraintDefinition>(StringComparer.Ordinal);
        foreach (var currentType in chain)
        {
            foreach (var constraint in await ListConstraintsAsync(runtime, currentType, cancellationToken))
            {
                constraints[constraint.Name] = constraint;
            }
        }

        return constraints.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    private static async Task<IReadOnlyList<ConstraintDefinition>> ListConstraintsAsync(
        CozoOmRuntime runtime,
        string typeName,
        CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(
            """
            ?[constraint_name, constraint_type, message] :=
              *om_constraint_def{ type_name: $type_name, constraint_name, constraint_type, message }
            :sort constraint_name
            """,
            LogicSupport.Params(("type_name", typeName)),
            cancellationToken: cancellationToken);
        return result.Rows
            .Select(row => new ConstraintDefinition(
                typeName,
                JsonRows.StringAt(row, 0) ?? "",
                NormalizeConstraintType(JsonRows.StringAt(row, 1) ?? ""),
                JsonRows.StringAt(row, 2) ?? ""))
            .Where(row => row.Name.Length > 0)
            .ToArray();
    }

    private static string NormalizeConstraintType(string constraintType)
    {
        var normalized = (constraintType ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "" => "conditional",
            "conditional" => "conditional",
            "cross_entity" => "cross-entity",
            "cross-entity" => "cross-entity",
            "computed_dep" => "computed-dep",
            "computed-dep" => "computed-dep",
            "custom" => "custom",
            _ => normalized,
        };
    }

    private static string ValidateConstraintType(string constraintType)
    {
        var normalized = NormalizeConstraintType(constraintType);
        if (normalized is not ("conditional" or "cross-entity" or "computed-dep" or "custom"))
        {
            throw new ArgumentException(
                "Constraint type must be one of 'conditional', 'cross-entity', 'computed-dep', or 'custom'",
                nameof(constraintType));
        }

        return normalized;
    }

    private static async Task<string> ResolveExistingOwnerTypeAsync(
        CozoOmRuntime runtime,
        string typeName,
        CancellationToken cancellationToken)
    {
        var resolved = await TypeLogic.ResolveTypeAsync(runtime, typeName, cancellationToken);
        if (!await TypeLogic.TypeExistsAsync(runtime, resolved, cancellationToken))
        {
            throw new CozoException($"Owner type '{resolved}' does not exist");
        }

        return resolved;
    }

    private static async Task<ConstraintMetadataSnapshot?> ReadConstraintMetadataAsync(
        CozoOmRuntime runtime,
        string typeName,
        string constraintName,
        CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(
            """
            ?[constraint_type, message] :=
              *om_constraint_def{ type_name: $type_name, constraint_name: $constraint_name, constraint_type, message }
            :limit 1
            """,
            LogicSupport.Params(("type_name", typeName), ("constraint_name", constraintName)),
            cancellationToken: cancellationToken);
        return result.Rows.Count == 0
            ? null
            : new ConstraintMetadataSnapshot(
                JsonRows.StringAt(result.Rows[0], 0) ?? "",
                JsonRows.StringAt(result.Rows[0], 1) ?? "");
    }

    private static async Task<ActionMetadataSnapshot?> ReadActionMetadataAsync(
        CozoOmRuntime runtime,
        string typeName,
        string actionName,
        CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(
            """
            ?[description] :=
              *om_action_def{ type_name: $type_name, action_name: $action_name, description }
            :limit 1
            """,
            LogicSupport.Params(("type_name", typeName), ("action_name", actionName)),
            cancellationToken: cancellationToken);
        return result.Rows.Count == 0
            ? null
            : new ActionMetadataSnapshot(JsonRows.StringAt(result.Rows[0], 0) ?? "");
    }

    private static async Task<MutationMetadataSnapshot?> ReadMutationMetadataAsync(
        CozoOmRuntime runtime,
        string typeName,
        string mutationName,
        CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(
            """
            ?[description] :=
              *om_mutation_def{ type_name: $type_name, mutation_name: $mutation_name, description }
            :limit 1
            """,
            LogicSupport.Params(("type_name", typeName), ("mutation_name", mutationName)),
            cancellationToken: cancellationToken);
        return result.Rows.Count == 0
            ? null
            : new MutationMetadataSnapshot(JsonRows.StringAt(result.Rows[0], 0) ?? "");
    }

    private static async Task<InterceptorMetadataSnapshot?> ReadInterceptorMetadataAsync(
        CozoOmRuntime runtime,
        string typeName,
        string actionName,
        string phase,
        int seq,
        CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(
            """
            ?[description] :=
              *om_interceptor_def{
                type_name: $type_name,
                action_name: $action_name,
                phase: $phase,
                seq: $seq,
                description
              }
            :limit 1
            """,
            LogicSupport.Params(("type_name", typeName), ("action_name", actionName), ("phase", phase), ("seq", seq)),
            cancellationToken: cancellationToken);
        return result.Rows.Count == 0
            ? null
            : new InterceptorMetadataSnapshot(JsonRows.StringAt(result.Rows[0], 0) ?? "");
    }

    private static Task RestoreConstraintMetadataAsync(
        CozoOmRuntime runtime,
        string typeName,
        string constraintName,
        ConstraintMetadataSnapshot? snapshot,
        CancellationToken cancellationToken) =>
        snapshot is null
            ? RemoveConstraintMetadataAsync(runtime, typeName, constraintName, cancellationToken)
            : runtime.Store.RunAsync(
                CozoScriptBuilder.InputPut(
                    "om_constraint_def",
                    ["type_name", "constraint_name"],
                    ["constraint_type", "message"]),
                LogicSupport.Params(
                    ("type_name", typeName),
                    ("constraint_name", constraintName),
                    ("constraint_type", snapshot.ConstraintType),
                    ("message", snapshot.Message)),
                cancellationToken: cancellationToken);

    private static Task RestoreActionMetadataAsync(
        CozoOmRuntime runtime,
        string typeName,
        string actionName,
        ActionMetadataSnapshot? snapshot,
        CancellationToken cancellationToken) =>
        snapshot is null
            ? RemoveActionMetadataAsync(runtime, typeName, actionName, cancellationToken)
            : runtime.Store.RunAsync(
                CozoScriptBuilder.InputPut("om_action_def", ["type_name", "action_name"], ["description"]),
                LogicSupport.Params(
                    ("type_name", typeName),
                    ("action_name", actionName),
                    ("description", snapshot.Description)),
                cancellationToken: cancellationToken);

    private static Task RestoreMutationMetadataAsync(
        CozoOmRuntime runtime,
        string typeName,
        string mutationName,
        MutationMetadataSnapshot? snapshot,
        CancellationToken cancellationToken) =>
        snapshot is null
            ? RemoveMutationMetadataAsync(runtime, typeName, mutationName, cancellationToken)
            : runtime.Store.RunAsync(
                CozoScriptBuilder.InputPut("om_mutation_def", ["type_name", "mutation_name"], ["description"]),
                LogicSupport.Params(
                    ("type_name", typeName),
                    ("mutation_name", mutationName),
                    ("description", snapshot.Description)),
                cancellationToken: cancellationToken);

    private static Task RestoreInterceptorMetadataAsync(
        CozoOmRuntime runtime,
        string typeName,
        string actionName,
        string phase,
        int seq,
        InterceptorMetadataSnapshot? snapshot,
        CancellationToken cancellationToken) =>
        snapshot is null
            ? RemoveInterceptorMetadataAsync(runtime, typeName, actionName, phase, seq, cancellationToken)
            : runtime.Store.RunAsync(
                CozoScriptBuilder.InputPut(
                    "om_interceptor_def",
                    ["type_name", "action_name", "phase", "seq"],
                    ["description"]),
                LogicSupport.Params(
                    ("type_name", typeName),
                    ("action_name", actionName),
                    ("phase", phase),
                    ("seq", seq),
                    ("description", snapshot.Description)),
                cancellationToken: cancellationToken);

    private static async Task RethrowAfterRegistrationRollbackAsync(
        Exception registrationFailure,
        Action restoreRegistration,
        Func<Task> restoreMetadata)
    {
        var rollbackFailures = new List<Exception>();
        try
        {
            restoreRegistration();
        }
        catch (Exception rollbackFailure)
        {
            rollbackFailures.Add(rollbackFailure);
        }

        try
        {
            await restoreMetadata();
        }
        catch (Exception rollbackFailure)
        {
            rollbackFailures.Add(rollbackFailure);
        }

        if (rollbackFailures.Count > 0)
        {
            throw new AggregateException(
                "Callback registration failed and rollback could not fully restore the previous definition",
                [registrationFailure, .. rollbackFailures]);
        }

        ExceptionDispatchInfo.Capture(registrationFailure).Throw();
    }

    private static Task RemoveConstraintMetadataAsync(
        CozoOmRuntime runtime,
        string typeName,
        string constraintName,
        CancellationToken cancellationToken) =>
        runtime.Store.RunAsync(
            """
            ?[type_name, constraint_name] <- [[$type_name, $constraint_name]]
            :rm om_constraint_def {type_name, constraint_name}
            """,
            LogicSupport.Params(("type_name", typeName), ("constraint_name", constraintName)),
            cancellationToken: cancellationToken);

    private static Task RemoveActionMetadataAsync(
        CozoOmRuntime runtime,
        string typeName,
        string actionName,
        CancellationToken cancellationToken) =>
        runtime.Store.RunAsync(
            """
            ?[type_name, action_name] <- [[$type_name, $action_name]]
            :rm om_action_def {type_name, action_name}
            """,
            LogicSupport.Params(("type_name", typeName), ("action_name", actionName)),
            cancellationToken: cancellationToken);

    private static Task RemoveMutationMetadataAsync(
        CozoOmRuntime runtime,
        string typeName,
        string mutationName,
        CancellationToken cancellationToken) =>
        runtime.Store.RunAsync(
            """
            ?[type_name, mutation_name] <- [[$type_name, $mutation_name]]
            :rm om_mutation_def {type_name, mutation_name}
            """,
            LogicSupport.Params(("type_name", typeName), ("mutation_name", mutationName)),
            cancellationToken: cancellationToken);

    private static Task RemoveInterceptorMetadataAsync(
        CozoOmRuntime runtime,
        string typeName,
        string actionName,
        string phase,
        int seq,
        CancellationToken cancellationToken) =>
        runtime.Store.RunAsync(
            """
            ?[type_name, action_name, phase, seq] <- [[$type_name, $action_name, $phase, $seq]]
            :rm om_interceptor_def {type_name, action_name, phase, seq}
            """,
            LogicSupport.Params(("type_name", typeName), ("action_name", actionName), ("phase", phase), ("seq", seq)),
            cancellationToken: cancellationToken);

    private static async Task<ActionRegistration?> ResolveActionAsync(
        CozoOmRuntime runtime,
        BehaviorResolutionScope resolution,
        string typeName,
        string actionName,
        CancellationToken cancellationToken)
    {
        foreach (var candidateType in new[] { typeName }.Concat(await TypeLogic.GetAncestorsAsync(runtime, typeName, cancellationToken)))
        {
            var hasDefinition = await BehaviorDefinitionExistsAsync(
                runtime,
                "om_action_def",
                "type_name",
                candidateType,
                "action_name",
                actionName,
                cancellationToken);
            if (hasDefinition)
            {
                await BehaviorReadinessLogic.EnsureReadyIfBoundAsync(
                    runtime,
                    resolution,
                    new BehaviorBindingKey(
                        BehaviorKind.Action,
                        candidateType,
                        actionName,
                        BehaviorCallbackSlot.Handler,
                        BehaviorBindingLogic.NonInterceptorPhase,
                        BehaviorBindingLogic.NonInterceptorSeq),
                    cancellationToken);
            }

            if (resolution.RegistrySnapshot.Actions.TryGetValue((candidateType, actionName), out var registration))
            {
                return new ActionRegistration(candidateType, registration.Callback);
            }
        }

        return null;
    }

    private static async Task<ActionRegistration?> ResolveParentActionAsync(
        CozoOmRuntime runtime,
        BehaviorResolutionScope resolution,
        string actionOwnerType,
        string actionName,
        CancellationToken cancellationToken)
    {
        var parentType = await TypeLogic.GetParentTypeAsync(runtime, actionOwnerType, cancellationToken);
        return string.IsNullOrWhiteSpace(parentType)
            ? null
            : await ResolveActionAsync(runtime, resolution, parentType, actionName, cancellationToken);
    }

    private static async Task<MutationRegistration?> ResolveMutationAsync(
        CozoOmRuntime runtime,
        BehaviorResolutionScope resolution,
        string typeName,
        string mutationName,
        CancellationToken cancellationToken)
    {
        foreach (var candidateType in new[] { typeName }.Concat(await TypeLogic.GetAncestorsAsync(runtime, typeName, cancellationToken)))
        {
            var hasDefinition = await BehaviorDefinitionExistsAsync(
                runtime,
                "om_mutation_def",
                "type_name",
                candidateType,
                "mutation_name",
                mutationName,
                cancellationToken);
            if (hasDefinition)
            {
                await BehaviorReadinessLogic.EnsureReadyIfBoundAsync(
                    runtime,
                    resolution,
                    new BehaviorBindingKey(
                        BehaviorKind.Mutation,
                        candidateType,
                        mutationName,
                        BehaviorCallbackSlot.Executor,
                        BehaviorBindingLogic.NonInterceptorPhase,
                        BehaviorBindingLogic.NonInterceptorSeq),
                    cancellationToken);
            }

            if (resolution.RegistrySnapshot.Mutations.TryGetValue((candidateType, mutationName), out var registration))
            {
                return new MutationRegistration(candidateType, registration.Callback);
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<OmInterceptorRegistration>> CollectInterceptorsAsync(
        CozoOmRuntime runtime,
        BehaviorResolutionScope resolution,
        string typeName,
        string actionName,
        string phase,
        CancellationToken cancellationToken)
    {
        var chain = new[] { typeName }.Concat(await TypeLogic.GetAncestorsAsync(runtime, typeName, cancellationToken)).Reverse();
        var resolved = new List<OmInterceptorRegistration>();
        var source = phase == "before"
            ? resolution.RegistrySnapshot.BeforeInterceptors
            : resolution.RegistrySnapshot.AfterInterceptors;

        foreach (var ownerType in chain)
        {
            var metadataSequences = await ListInterceptorSequencesAsync(
                runtime,
                ownerType,
                actionName,
                phase,
                cancellationToken);
            var registrations = source.TryGetValue((ownerType, actionName), out var existing)
                ? existing.OrderBy(item => item.Seq).ToArray()
                : [];

            foreach (var seq in metadataSequences)
            {
                await BehaviorReadinessLogic.EnsureReadyIfBoundAsync(
                    runtime,
                    resolution,
                    new BehaviorBindingKey(
                        BehaviorKind.Interceptor,
                        ownerType,
                        actionName,
                        BehaviorCallbackSlot.Handler,
                        phase,
                        seq),
                    cancellationToken);
                var registration = registrations.FirstOrDefault(item => item.Seq == seq);
                if (registration is not null)
                {
                    resolved.Add(registration);
                }
            }

            var metadataSet = metadataSequences.ToHashSet();
            resolved.AddRange(registrations.Where(item => !metadataSet.Contains(item.Seq)));
        }

        return resolved;
    }

    private static async Task<bool> BehaviorDefinitionExistsAsync(
        CozoOmRuntime runtime,
        string relation,
        string ownerField,
        string ownerType,
        string nameField,
        string behaviorName,
        CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(
            $"?[name] := *{relation}{{{ownerField}: $owner, {nameField}: name}}, name = $name\n:limit 1",
            LogicSupport.Params(("owner", ownerType), ("name", behaviorName)),
            cancellationToken: cancellationToken);
        return result.Rows.Count > 0;
    }

    private static async Task<IReadOnlyList<int>> ListInterceptorSequencesAsync(
        CozoOmRuntime runtime,
        string ownerType,
        string actionName,
        string phase,
        CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(
            "?[seq] := *om_interceptor_def{type_name: $owner, action_name: $action, phase: $phase, seq}\n:sort seq",
            LogicSupport.Params(("owner", ownerType), ("action", actionName), ("phase", phase)),
            cancellationToken: cancellationToken);
        return result.Rows.Select(row => JsonRows.IntAt(row, 0)).ToArray();
    }

    private static async Task<int> NextInterceptorSeqAsync(
        CozoOmRuntime runtime,
        string ownerType,
        string actionName,
        string phase,
        CancellationToken cancellationToken)
    {
        var metadataSequences = await ListInterceptorSequencesAsync(
            runtime,
            ownerType,
            actionName,
            phase,
            cancellationToken);
        var metadataNext = metadataSequences.Count == 0 ? 0 : metadataSequences.Max() + 1;
        return Math.Max(metadataNext, runtime.Registry.NextInterceptorSeq(ownerType, actionName, phase));
    }

    private static string NormalizeInterceptorPhase(string phase)
    {
        var normalized = OmConvert.RequireName(phase, nameof(phase)).ToLowerInvariant();
        if (normalized is not ("before" or "after"))
        {
            throw new ArgumentException("Interceptor phase must be 'before' or 'after'", nameof(phase));
        }

        return normalized;
    }

    private static async Task<PermissionAbacEvaluation> EvaluatePermissionAbacAsync(
        CozoOmRuntime runtime,
        string policyId,
        CheckAccessInput input,
        string subjectType,
        string resourceType,
        string? asOf,
        CancellationToken cancellationToken)
    {
        var rows = await runtime.Store.RunAsync(
            """
            ?[left_ref, op, right_ref] :=
              *om_perm_abac_rule{ policy_id: $policy_id, left_ref, op, right_ref }
            :sort left_ref, op, right_ref
            """,
            LogicSupport.Params(("policy_id", policyId)),
            cancellationToken: cancellationToken);
        var diagnostics = ImmutableArray.CreateBuilder<PermissionAbacDiagnostic>();
        var hiddenFields = ImmutableArray.CreateBuilder<string>();
        foreach (var row in rows.Rows)
        {
            var leftRef = JsonRows.StringAt(row, 0) ?? "";
            var rightRef = JsonRows.StringAt(row, 2) ?? "";
            var op = (JsonRows.StringAt(row, 1) ?? "=").Trim();
            if (!TryDescribePermissionAbacShape(leftRef, op, rightRef, out var invalidDiagnostic))
            {
                diagnostics.Add(invalidDiagnostic!);
                continue;
            }

            var left = await ResolvePermissionValueAsync(
                runtime, leftRef, input, subjectType, resourceType, asOf, cancellationToken);
            var right = await ResolvePermissionValueAsync(
                runtime, rightRef, input, subjectType, resourceType, asOf, cancellationToken);
            if (left.Value is null || right.Value is null)
            {
                diagnostics.Add(new PermissionAbacDiagnostic(
                    leftRef,
                    op,
                    rightRef,
                    PermissionEvaluationStatus.Unmatched,
                    left.Value is null ? left.Detail : right.Detail));
                continue;
            }

            if (op == "hide")
            {
                var field = leftRef["field.".Length..].Trim();
                var hidden = IsPermissionTruthy(right.Value.Value);
                diagnostics.Add(new PermissionAbacDiagnostic(
                    leftRef,
                    op,
                    rightRef,
                    hidden ? PermissionEvaluationStatus.Matched : PermissionEvaluationStatus.Unmatched,
                    hidden ? null : "predicate_unmatched"));
                if (hidden) hiddenFields.Add(field);
                continue;
            }

            var ok = EvaluatePermissionComparator(op, left.Value.Value, right.Value.Value);
            diagnostics.Add(new PermissionAbacDiagnostic(
                leftRef,
                op,
                rightRef,
                ok ? PermissionEvaluationStatus.Matched : PermissionEvaluationStatus.Unmatched,
                ok ? null : "predicate_unmatched"));
        }

        var values = diagnostics.ToImmutable();
        return new PermissionAbacEvaluation(
            !values.Any(diagnostic => diagnostic.Operator != "hide"
                && diagnostic.Status is PermissionEvaluationStatus.Invalid or PermissionEvaluationStatus.Unmatched),
            values.Any(diagnostic => diagnostic.Status == PermissionEvaluationStatus.Invalid),
            values,
            hiddenFields.Distinct(StringComparer.Ordinal).OrderBy(field => field, StringComparer.Ordinal).ToImmutableArray());
    }

    private static async Task<PermissionWitnessDiagnostic> DescribePermissionWitnessAsync(
        CozoOmRuntime runtime,
        IReadOnlyList<string> paths,
        string subjectId,
        string resourceId,
        string? asOf,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
        {
            return new PermissionWitnessDiagnostic(
                PermissionEvaluationStatus.NotEvaluated,
                [],
                [],
                ["no_path_rules"]);
        }

        var diagnostics = ImmutableArray.CreateBuilder<string>();
        var parsedPaths = new List<ParsedPermissionPath>();
        var hasInvalidPath = false;
        foreach (var path in paths)
        {
            if (!TryParsePermissionPath(path, out var relations))
            {
                diagnostics.Add("malformed_path");
                hasInvalidPath = true;
                continue;
            }

            var canonicalRelations = ImmutableArray.CreateBuilder<string>();
            var isValidPath = true;
            foreach (var relation in relations)
            {
                try
                {
                    canonicalRelations.Add((await TypeLogic.GetRelationDefinitionAsync(runtime, relation, cancellationToken)).RelName);
                }
                catch (CozoException)
                {
                    diagnostics.Add("unknown_relation");
                    hasInvalidPath = true;
                    isValidPath = false;
                    break;
                }
            }

            if (isValidPath)
            {
                parsedPaths.Add(new ParsedPermissionPath(path, canonicalRelations.ToImmutable()));
            }
        }

        var resultDiagnostics = diagnostics
            .Distinct(StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToImmutableArray();
        if (hasInvalidPath)
        {
            return new PermissionWitnessDiagnostic(
                PermissionEvaluationStatus.Invalid,
                paths.ToImmutableArray(),
                [],
                resultDiagnostics);
        }

        foreach (var path in parsedPaths
                     .OrderBy(candidate => candidate.Relations.Length)
                     .ThenBy(candidate => string.Join("\u0001", candidate.Relations), StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Raw, StringComparer.Ordinal))
        {
            var hops = await FindPermissionWitnessAsync(
                runtime,
                subjectId,
                resourceId,
                path.Relations,
                asOf,
                cancellationToken);
            if (hops is not null)
            {
                return new PermissionWitnessDiagnostic(
                    PermissionEvaluationStatus.Matched,
                    paths.ToImmutableArray(),
                    hops.Value,
                    []);
            }
        }

        return new PermissionWitnessDiagnostic(
            PermissionEvaluationStatus.Unmatched,
            paths.ToImmutableArray(),
            [],
            ["witness_unavailable"]);
    }

    private static bool TryParsePermissionPath(string path, out ImmutableArray<string> relations)
    {
        relations = [];
        if (string.IsNullOrWhiteSpace(path)) return false;
        var trimmed = path.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
                var values = document.RootElement.EnumerateArray()
                    .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null)
                    .ToArray();
                if (values.Any(string.IsNullOrWhiteSpace)) return false;
                relations = values!.Cast<string>().ToImmutableArray();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        var slashPath = trimmed.StartsWith("/", StringComparison.Ordinal)
            ? trimmed.TrimStart('/')
            : trimmed;
        var normalized = slashPath.Replace("->", "/", StringComparison.Ordinal);
        if (normalized.Length == 0 || normalized.Contains('>') || normalized.Contains('<')) return false;
        var segments = normalized.Split('/', StringSplitOptions.None)
            .Select(segment => segment.Trim())
            .ToArray();
        if (segments.Any(string.IsNullOrWhiteSpace)) return false;
        relations = segments.ToImmutableArray();
        return true;
    }

    private static async Task<ImmutableArray<PermissionWitnessHop>?> FindPermissionWitnessAsync(
        CozoOmRuntime runtime,
        string subjectId,
        string resourceId,
        ImmutableArray<string> relations,
        string? asOf,
        CancellationToken cancellationToken)
    {
        if (relations.Length == 0)
        {
            return string.Equals(subjectId, resourceId, StringComparison.Ordinal)
                ? []
                : null;
        }

        var frontier = new Dictionary<string, ImmutableArray<PermissionWitnessHop>>(StringComparer.Ordinal)
        {
            [subjectId] = []
        };
        foreach (var relation in relations)
        {
            var next = new Dictionary<string, ImmutableArray<PermissionWitnessHop>>(StringComparer.Ordinal);
            foreach (var (fromId, hops) in frontier.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var neighbors = asOf is null
                    ? await RelationLogic.GetNeighborsAsync(runtime, fromId, relation, OmDirection.Outgoing, cancellationToken)
                    : await RelationLogic.GetNeighborsAtNormalizedAsOfAsync(runtime, fromId, relation, asOf, OmDirection.Outgoing, cancellationToken);
                foreach (var neighbor in neighbors.Outgoing
                             .OrderBy(candidate => candidate.EntityId, StringComparer.Ordinal)
                             .ThenBy(candidate => candidate.RelName, StringComparer.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(neighbor.EntityId) || next.ContainsKey(neighbor.EntityId)) continue;
                    next[neighbor.EntityId] = hops.Add(new PermissionWitnessHop(fromId, relation, neighbor.EntityId));
                }
            }

            if (next.Count == 0) return null;
            frontier = next;
        }

        return frontier.TryGetValue(resourceId, out var witness) ? witness : null;
    }

    private static bool TryDescribePermissionAbacShape(
        string leftRef,
        string op,
        string rightRef,
        out PermissionAbacDiagnostic? diagnostic)
    {
        if (op is not ("=" or "==" or "!=" or ">" or ">=" or "<" or "<=" or "hide"))
        {
            diagnostic = new PermissionAbacDiagnostic(leftRef, op, rightRef, PermissionEvaluationStatus.Invalid, "unsupported_operator");
            return false;
        }

        if (!IsPermissionReference(leftRef, allowLiteral: false)
            || !IsPermissionReference(rightRef, allowLiteral: true))
        {
            diagnostic = new PermissionAbacDiagnostic(leftRef, op, rightRef, PermissionEvaluationStatus.Invalid, "malformed_reference");
            return false;
        }

        if (rightRef.TrimStart().StartsWith("field.", StringComparison.Ordinal))
        {
            diagnostic = new PermissionAbacDiagnostic(leftRef, op, rightRef, PermissionEvaluationStatus.Invalid, "malformed_reference");
            return false;
        }

        if (op == "hide")
        {
            if (!leftRef.TrimStart().StartsWith("field.", StringComparison.Ordinal))
            {
                diagnostic = new PermissionAbacDiagnostic(leftRef, op, rightRef, PermissionEvaluationStatus.Invalid, "malformed_reference");
                return false;
            }
        }
        else if (leftRef.TrimStart().StartsWith("field.", StringComparison.Ordinal))
        {
            diagnostic = new PermissionAbacDiagnostic(leftRef, op, rightRef, PermissionEvaluationStatus.Invalid, "malformed_reference");
            return false;
        }

        diagnostic = null;
        return true;
    }

    private static bool IsPermissionReference(string reference, bool allowLiteral)
    {
        var value = reference.Trim();
        if (value.Length == 0) return false;
        if (value is "subject.type" or "resource.type") return true;
        if (value is "subject.id" or "action" or "resource.id" or "resource.field") return false;
        if (value.StartsWith("subject.", StringComparison.Ordinal)) return value.Length > "subject.".Length;
        if (value.StartsWith("resource.", StringComparison.Ordinal)) return value.Length > "resource.".Length;
        if (value.StartsWith("field.", StringComparison.Ordinal)) return value.Length > "field.".Length;
        return allowLiteral;
    }

    private static async Task<PermissionValueResolution> ResolvePermissionValueAsync(
        CozoOmRuntime runtime,
        string reference,
        CheckAccessInput input,
        string subjectType,
        string resourceType,
        string? asOf,
        CancellationToken cancellationToken)
    {
        var value = reference.Trim();
        switch (value)
        {
            case "subject.type": return PermissionValueResolution.From(subjectType);
            case "resource.type": return PermissionValueResolution.From(resourceType);
        }

        if (value.StartsWith("subject.", StringComparison.Ordinal) || value.StartsWith("resource.", StringComparison.Ordinal))
        {
            var isSubject = value.StartsWith("subject.", StringComparison.Ordinal);
            var attribute = value[(isSubject ? "subject." : "resource.").Length..].Trim();
            var entityId = isSubject ? input.SubjectId : input.ResourceId;
            try
            {
                var property = asOf is null
                    ? await EntityLogic.GetPropertyAsync(runtime, entityId, attribute, cancellationToken)
                    : await EntityLogic.GetPropertyAtNormalizedAsOfAsync(runtime, entityId, attribute, asOf, cancellationToken);
                return property is null
                    ? new PermissionValueResolution(null, "missing_value")
                    : new PermissionValueResolution(property.Value, null);
            }
            catch (CozoException)
            {
                return new PermissionValueResolution(null, "missing_value");
            }
        }

        return PermissionValueResolution.FromLiteral(value.StartsWith("literal:", StringComparison.Ordinal)
            ? value["literal:".Length..]
            : value);
    }

    private static bool EvaluatePermissionComparator(string op, JsonElement left, JsonElement right)
    {
        if (op is "=" or "==") return PermissionValuesEqual(left, right);
        if (op == "!=") return !PermissionValuesEqual(left, right);

        var comparison = ComparePermissionValues(left, right);
        return op switch
        {
            ">" => comparison > 0,
            ">=" => comparison >= 0,
            "<" => comparison < 0,
            "<=" => comparison <= 0,
            _ => false,
        };
    }

    private static bool PermissionValuesEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            || right.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            // Bun uses Object.is: independently resolved JSON containers are never the same value.
            return false;
        }

        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number
            && left.TryGetDecimal(out var leftNumber) && right.TryGetDecimal(out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        return left.ValueKind == right.ValueKind && left.GetRawText() == right.GetRawText();
    }

    private static int ComparePermissionValues(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number
            && left.TryGetDecimal(out var leftNumber) && right.TryGetDecimal(out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String)
        {
            return string.Compare(left.GetString(), right.GetString(), StringComparison.Ordinal);
        }

        return string.Compare(PermissionValueToString(left), PermissionValueToString(right), StringComparison.Ordinal);
    }

    private static string PermissionValueToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => string.Join(",", value.EnumerateArray().Select(PermissionValueToString)),
        JsonValueKind.Object => "[object Object]",
        _ => value.GetRawText(),
    };

    private static bool IsPermissionTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
        JsonValueKind.Number => !value.TryGetDecimal(out var number) || number != 0,
        _ => true,
    };

    private static async Task<IReadOnlyList<string>> PermissionPathsAsync(CozoOmRuntime runtime, string policyId, CancellationToken cancellationToken)
    {
        var rows = await runtime.Store.RunAsync(
            """
            ?[path] :=
              *om_perm_path_rule{ policy_id: $policy_id, path }
            :sort path
            """,
            LogicSupport.Params(("policy_id", policyId)),
            cancellationToken: cancellationToken);
        return rows.Rows.Select(row => JsonRows.StringAt(row, 0) ?? "").Where(x => x.Length > 0).ToArray();
    }

    private sealed record PermissionPolicyRow(string PolicyId, string Effect, string Action, string ResourceType, string Description);

    private sealed record ParsedPermissionPath(string Raw, ImmutableArray<string> Relations);

    private sealed record PermissionAbacEvaluation(
        bool Matches,
        bool HasInvalid,
        ImmutableArray<PermissionAbacDiagnostic> Diagnostics,
        ImmutableArray<string> HiddenFields);

    private sealed record PermissionValueResolution(JsonElement? Value, string? Detail)
    {
        public static PermissionValueResolution From(object? value) => new(OmConvert.CloneToElement(value), null);

        public static PermissionValueResolution FromLiteral(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length > 0 && (trimmed[0] is '{' or '[' or '"' || trimmed is "true" or "false" or "null" || char.IsDigit(trimmed[0]) || trimmed[0] == '-'))
            {
                try
                {
                    using var document = JsonDocument.Parse(trimmed);
                    return new PermissionValueResolution(document.RootElement.Clone(), null);
                }
                catch (JsonException)
                {
                    // Bare text remains a string literal, as it does in Bun.
                }
            }

            return From(value);
        }
    }

    private sealed record ConstraintMetadataSnapshot(string ConstraintType, string Message);

    private sealed record ActionMetadataSnapshot(string Description);

    private sealed record MutationMetadataSnapshot(string Description);

    private sealed record InterceptorMetadataSnapshot(string Description);

    private sealed record ActionRegistration(
        string OwnerType,
        Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>> Handler);

    private sealed record MutationRegistration(
        string OwnerType,
        Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask> Executor);

    private sealed record ParentActionResolution(
        BehaviorResolutionScope Resolution,
        ActionRegistration? Action);

    private sealed record ResolvedActionPipeline(
        BehaviorResolutionScope Resolution,
        string TypeName,
        ActionRegistration? Action,
        IReadOnlyList<OmInterceptorRegistration> BeforeInterceptors,
        IReadOnlyList<OmInterceptorRegistration> AfterInterceptors);

    private sealed record ResolvedMutation(
        Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask> Executor,
        IReadOnlyDictionary<string, object?> Parameters);

    private sealed record ResolvedMutationBatch(
        string TypeName,
        IReadOnlyList<ResolvedMutation> Mutations);

    private sealed record ConstraintDefinition(
        string OwnerType,
        string Name,
        string Type,
        string Message);

    private sealed record ResolvedConstraint(
        ConstraintDefinition Definition,
        Func<OmValidationContext, ValueTask<string?>>? Validator,
        OmConstraintRegistration? Constraint);
}
