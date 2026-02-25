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
    public static Task DefineConstraintAsync(CozoOmRuntime runtime, DefineConstraintInput input, CancellationToken cancellationToken = default)
    {
        return runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_constraint_def", ["type_name", "constraint_name"], ["constraint_type", "message"]),
            LogicSupport.Params(
                ("type_name", OmConvert.RequireName(input.TypeName, nameof(input.TypeName))),
                ("constraint_name", OmConvert.RequireName(input.ConstraintName, nameof(input.ConstraintName))),
                ("constraint_type", OmConvert.RequireName(input.ConstraintType, nameof(input.ConstraintType))),
                ("message", input.Message)),
            cancellationToken: cancellationToken);
    }

    public static Task DefineComputedAsync(CozoOmRuntime runtime, DefineComputedInput input, CancellationToken cancellationToken = default)
    {
        return runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_computed_def", ["type_name", "attr_name"], ["description"]),
            LogicSupport.Params(
                ("type_name", OmConvert.RequireName(input.TypeName, nameof(input.TypeName))),
                ("attr_name", OmConvert.RequireName(input.AttrName, nameof(input.AttrName))),
                ("description", input.Description)),
            cancellationToken: cancellationToken);
    }

    public static Task DefineActionAsync(CozoOmRuntime runtime, DefineActionInput input, CancellationToken cancellationToken = default)
    {
        return runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_action_def", ["type_name", "action_name"], ["description"]),
            LogicSupport.Params(
                ("type_name", OmConvert.RequireName(input.TypeName, nameof(input.TypeName))),
                ("action_name", OmConvert.RequireName(input.ActionName, nameof(input.ActionName))),
                ("description", input.Description)),
            cancellationToken: cancellationToken);
    }

    public static Task DefineMutationAsync(CozoOmRuntime runtime, DefineMutationInput input, CancellationToken cancellationToken = default)
    {
        return runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_mutation_def", ["type_name", "mutation_name"], ["description"]),
            LogicSupport.Params(
                ("type_name", OmConvert.RequireName(input.TypeName, nameof(input.TypeName))),
                ("mutation_name", OmConvert.RequireName(input.MutationName, nameof(input.MutationName))),
                ("description", input.Description)),
            cancellationToken: cancellationToken);
    }

    public static Task AddInterceptorAsync(CozoOmRuntime runtime, AddInterceptorInput input, CancellationToken cancellationToken = default)
    {
        var phase = NormalizeInterceptorPhase(input.Phase);
        return runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_interceptor_def", ["type_name", "action_name", "phase", "seq"], ["description"]),
            LogicSupport.Params(
                ("type_name", OmConvert.RequireName(input.TypeName, nameof(input.TypeName))),
                ("action_name", OmConvert.RequireName(input.ActionName, nameof(input.ActionName))),
                ("phase", phase),
                ("seq", input.Seq),
                ("description", input.Description)),
            cancellationToken: cancellationToken);
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
        var typeName = await EntityLogic.GetEntityTypeAsync(runtime, id, cancellationToken);
        var actionRegistration = await ResolveActionAsync(runtime, typeName, action, cancellationToken);
        if (actionRegistration is null)
        {
            throw new InvalidOperationException($"Action '{action}' not defined for type '{typeName}'");
        }

        var ctx = new OmActionContext(runtime, id, typeName, actionRegistration.OwnerType, parameters ?? new Dictionary<string, object?>());
        foreach (var interceptor in await CollectInterceptorsAsync(runtime, typeName, action, "before", cancellationToken))
        {
            await interceptor.Handler(ctx);
        }

        var mutations = await actionRegistration.Handler(ctx, ctx.Params);
        await ExecuteMutationsCoreAsync(runtime, id, mutations, cancellationToken);

        foreach (var interceptor in await CollectInterceptorsAsync(runtime, typeName, action, "after", cancellationToken))
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

        await ExecuteMutationsCoreAsync(txRuntime, entityId, mutations, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteMutationsCoreAsync(
        CozoOmRuntime runtime,
        string entityId,
        IReadOnlyList<MutationSpec>? mutations,
        CancellationToken cancellationToken = default)
    {
        var id = OmConvert.RequireName(entityId, nameof(entityId));
        var typeName = await EntityLogic.GetEntityTypeAsync(runtime, id, cancellationToken);
        var ctx = new OmMutationContext(runtime, id, typeName);
        foreach (var item in mutations ?? [])
        {
            var mutationName = OmConvert.RequireName(item.Mutation, nameof(item.Mutation));
            var executor = await ResolveMutationAsync(runtime, typeName, mutationName, cancellationToken);
            if (executor is null)
            {
                throw new InvalidOperationException($"Mutation '{mutationName}' not defined for type '{typeName}'");
            }

            await executor(ctx, item.Params ?? new Dictionary<string, object?>());
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

        foreach (var constraint in await ListEffectiveConstraintsAsync(runtime, typeName, cancellationToken))
        {
            await EvaluateConstraintDefinitionAsync(runtime, constraint, new OmValidationContext(runtime, entityId, typeName), errors);
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
        foreach (var constraint in await ListEffectiveConstraintsAsync(runtime, typeName, cancellationToken))
        {
            if (wanted is not null && !wanted.Contains(constraint.Type)) continue;
            await EvaluateConstraintDefinitionAsync(runtime, constraint, ctx, errors);
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
        var resourceType = await EntityLogic.GetEntityTypeAsync(runtime, input.ResourceId, cancellationToken);
        var resourceScopes = new HashSet<string>(StringComparer.Ordinal)
        {
            "*",
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
                if (scope == "*") continue;
                resourceScopes.Add($"{scope}.*");
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
            if (policy.Action != "*" && !string.Equals(policy.Action, input.Action, StringComparison.Ordinal)) continue;
            if (!resourceScopes.Contains(policy.ResourceType)) continue;
            if (!await PermissionAbacMatchesAsync(runtime, policy.PolicyId, input, resourceType, cancellationToken)) continue;

            var paths = await PermissionPathsAsync(runtime, policy.PolicyId, cancellationToken);
            matched.Add(new
            {
                policyId = policy.PolicyId,
                effect = policy.Effect,
                action = policy.Action,
                resourceType = policy.ResourceType,
                witnessPaths = paths
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
            resourceType,
            matchedPolicies = matched,
            decision = deny ? "deny" : finalAllow ? "allow" : "deny",
            reason = deny ? "deny policy matched" : finalAllow ? "allow policy matched" : "no allow policy matched"
        }, OmConvert.JsonOptions);
        return new CheckAccessResult(finalAllow, explanation.Clone());
    }

    private static async Task EvaluateConstraintDefinitionAsync(
        CozoOmRuntime runtime,
        ConstraintDefinition constraint,
        OmValidationContext ctx,
        List<string> errors)
    {
        if (string.Equals(constraint.Type, "custom", StringComparison.OrdinalIgnoreCase))
        {
            if (runtime.Registry.TryGetValidator(constraint.OwnerType, constraint.Name, out var validator))
            {
                var message = await validator(ctx);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    errors.Add(message!);
                }
            }

            if (!runtime.Registry.TryGetConstraint(constraint.OwnerType, constraint.Name, out var customConstraint))
            {
                return;
            }

            await EvaluateScopedConstraintAsync(customConstraint, constraint, ctx, errors);
            return;
        }

        if (!runtime.Registry.TryGetConstraint(constraint.OwnerType, constraint.Name, out var scopedConstraint)) return;
        await EvaluateScopedConstraintAsync(scopedConstraint, constraint, ctx, errors);
    }

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
        catch (Exception ex)
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
        catch (Exception ex)
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

    private static async Task<ActionRegistration?> ResolveActionAsync(
        CozoOmRuntime runtime,
        string typeName,
        string actionName,
        CancellationToken cancellationToken)
    {
        foreach (var candidateType in new[] { typeName }.Concat(await TypeLogic.GetAncestorsAsync(runtime, typeName, cancellationToken)))
        {
            if (runtime.Registry.TryGetAction(candidateType, actionName, out var handler))
            {
                return new ActionRegistration(candidateType, handler);
            }
        }

        return null;
    }

    private static async Task<Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask>?> ResolveMutationAsync(
        CozoOmRuntime runtime,
        string typeName,
        string mutationName,
        CancellationToken cancellationToken)
    {
        foreach (var candidateType in new[] { typeName }.Concat(await TypeLogic.GetAncestorsAsync(runtime, typeName, cancellationToken)))
        {
            if (runtime.Registry.TryGetMutation(candidateType, mutationName, out var executor))
            {
                return executor;
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<OmInterceptorRegistration>> CollectInterceptorsAsync(
        CozoOmRuntime runtime,
        string typeName,
        string actionName,
        string phase,
        CancellationToken cancellationToken)
    {
        var chain = new[] { typeName }.Concat(await TypeLogic.GetAncestorsAsync(runtime, typeName, cancellationToken)).Reverse();
        return chain
            .SelectMany(type => runtime.Registry.GetInterceptors(type, actionName, phase))
            .OrderBy(item => item.Seq)
            .ToArray();
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

    private static async Task<bool> PermissionAbacMatchesAsync(
        CozoOmRuntime runtime,
        string policyId,
        CheckAccessInput input,
        string resourceType,
        CancellationToken cancellationToken)
    {
        var rows = await runtime.Store.RunAsync(
            """
            ?[left_ref, op, right_ref] :=
              *om_perm_abac_rule{ policy_id: $policy_id, left_ref, op, right_ref }
            """,
            LogicSupport.Params(("policy_id", policyId)),
            cancellationToken: cancellationToken);
        foreach (var row in rows.Rows)
        {
            var left = ResolvePermissionRef(JsonRows.StringAt(row, 0) ?? "", input, resourceType);
            var right = ResolvePermissionRef(JsonRows.StringAt(row, 2) ?? "", input, resourceType);
            var op = JsonRows.StringAt(row, 1) ?? "=";
            var ok = op switch
            {
                "=" or "==" => string.Equals(left, right, StringComparison.Ordinal),
                "!=" => !string.Equals(left, right, StringComparison.Ordinal),
                _ => false
            };
            if (!ok) return false;
        }

        return true;
    }

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

    private static string ResolvePermissionRef(string reference, CheckAccessInput input, string resourceType)
    {
        return reference switch
        {
            "subject.id" => input.SubjectId,
            "action" => input.Action,
            "resource.id" => input.ResourceId,
            "resource.type" => resourceType,
            "resource.field" => input.FieldName ?? "",
            _ when reference.StartsWith("literal:", StringComparison.Ordinal) => reference["literal:".Length..],
            _ => reference
        };
    }

    private sealed record PermissionPolicyRow(string PolicyId, string Effect, string Action, string ResourceType, string Description);

    private sealed record ActionRegistration(
        string OwnerType,
        Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>> Handler);

    private sealed record ConstraintDefinition(
        string OwnerType,
        string Name,
        string Type,
        string Message);
}
