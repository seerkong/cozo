using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Internals;
using Cozo.DotNet.Om.Runtime;

namespace Cozo.DotNet.Om.Logic;

internal static class BehaviorCatalogLogic
{
    internal static async Task<BehaviorCatalog> GetAsync(
        CozoOmRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return await runtime.BehaviorGate.ResolveAsync(
            runtime,
            (resolution, token) => GetResolvedAsync(runtime, resolution, token),
            cancellationToken);
    }

    internal static async Task<BehaviorCatalog> GetResolvedAsync(
        CozoOmRuntime runtime,
        BehaviorResolutionScope resolution,
        CancellationToken cancellationToken)
    {
        var registrySnapshot = resolution.RegistrySnapshot;

        var bindings = (await BehaviorBindingLogic.ListAsync(runtime, cancellationToken))
            .ToDictionary(row => row.Key);
        var entries = new List<BehaviorCatalogEntry>();

        await AddConstraintsAsync(runtime, registrySnapshot, bindings, entries, cancellationToken);
        await AddDefinitionsAsync(
            runtime,
            "om_computed_def",
            "type_name: owner, attr_name: name, description",
            BehaviorKind.Computed,
            BehaviorCallbackSlot.Compute,
            entries,
            bindings,
            registrySnapshot,
            cancellationToken);
        await AddDefinitionsAsync(
            runtime,
            "om_action_def",
            "type_name: owner, action_name: name, description",
            BehaviorKind.Action,
            BehaviorCallbackSlot.Handler,
            entries,
            bindings,
            registrySnapshot,
            cancellationToken);
        await AddDefinitionsAsync(
            runtime,
            "om_mutation_def",
            "type_name: owner, mutation_name: name, description",
            BehaviorKind.Mutation,
            BehaviorCallbackSlot.Executor,
            entries,
            bindings,
            registrySnapshot,
            cancellationToken);
        await AddInterceptorsAsync(runtime, registrySnapshot, bindings, entries, cancellationToken);

        return new BehaviorCatalog(entries
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.OwnerType, StringComparer.Ordinal)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.InterceptorPhase ?? "", StringComparer.Ordinal)
            .ThenBy(entry => entry.InterceptorSeq ?? BehaviorBindingLogic.NonInterceptorSeq)
            .ToArray());
    }

    private static async Task AddConstraintsAsync(
        CozoOmRuntime runtime,
        CozoOmRegistrySnapshot registrySnapshot,
        IReadOnlyDictionary<BehaviorBindingKey, BehaviorBindingRow> bindings,
        ICollection<BehaviorCatalogEntry> entries,
        CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(
            "?[type_name, constraint_name, constraint_type, message] := *om_constraint_def{type_name, constraint_name, constraint_type, message}",
            cancellationToken: cancellationToken);

        foreach (var row in result.Rows)
        {
            var owner = JsonRows.StringAt(row, 0) ?? "";
            var name = JsonRows.StringAt(row, 1) ?? "";
            var constraintType = JsonRows.StringAt(row, 2) ?? "";
            var slots = string.Equals(constraintType, "custom", StringComparison.OrdinalIgnoreCase)
                ? new HashSet<BehaviorCallbackSlot> { BehaviorCallbackSlot.Validator }
                : new HashSet<BehaviorCallbackSlot> { BehaviorCallbackSlot.When, BehaviorCallbackSlot.Then };
            foreach (var binding in bindings.Keys.Where(key =>
                         key.BehaviorKind == BehaviorKind.Constraint
                         && key.OwnerType == owner
                         && key.BehaviorName == name))
            {
                slots.Add(binding.CallbackSlot);
            }

            entries.Add(new BehaviorCatalogEntry(
                BehaviorCatalogKind.Constraint,
                owner,
                name,
                constraintType,
                JsonRows.StringAt(row, 3) ?? "",
                null,
                null,
                null,
                slots.OrderBy(slot => slot).Select(slot => ProjectBinding(
                    registrySnapshot,
                    bindings,
                    new BehaviorBindingKey(
                        BehaviorKind.Constraint,
                        owner,
                        name,
                        slot,
                        BehaviorBindingLogic.NonInterceptorPhase,
                        BehaviorBindingLogic.NonInterceptorSeq))).ToArray()));
        }
    }

    private static async Task AddDefinitionsAsync(
        CozoOmRuntime runtime,
        string relation,
        string fields,
        BehaviorKind kind,
        BehaviorCallbackSlot slot,
        ICollection<BehaviorCatalogEntry> entries,
        IReadOnlyDictionary<BehaviorBindingKey, BehaviorBindingRow> bindings,
        CozoOmRegistrySnapshot registrySnapshot,
        CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(
            $"?[owner, name, description] := *{relation}{{{fields}}}",
            cancellationToken: cancellationToken);
        foreach (var row in result.Rows)
        {
            var owner = JsonRows.StringAt(row, 0) ?? "";
            var name = JsonRows.StringAt(row, 1) ?? "";
            entries.Add(new BehaviorCatalogEntry(
                ToCatalogKind(kind),
                owner,
                name,
                null,
                null,
                JsonRows.StringAt(row, 2) ?? "",
                null,
                null,
                [ProjectBinding(registrySnapshot, bindings, new BehaviorBindingKey(
                    kind,
                    owner,
                    name,
                    slot,
                    BehaviorBindingLogic.NonInterceptorPhase,
                    BehaviorBindingLogic.NonInterceptorSeq))]));
        }
    }

    private static async Task AddInterceptorsAsync(
        CozoOmRuntime runtime,
        CozoOmRegistrySnapshot registrySnapshot,
        IReadOnlyDictionary<BehaviorBindingKey, BehaviorBindingRow> bindings,
        ICollection<BehaviorCatalogEntry> entries,
        CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(
            "?[type_name, action_name, phase, seq, description] := *om_interceptor_def{type_name, action_name, phase, seq, description}",
            cancellationToken: cancellationToken);
        foreach (var row in result.Rows)
        {
            var owner = JsonRows.StringAt(row, 0) ?? "";
            var name = JsonRows.StringAt(row, 1) ?? "";
            var phase = JsonRows.StringAt(row, 2) ?? "";
            var seq = JsonRows.IntAt(row, 3);
            entries.Add(new BehaviorCatalogEntry(
                BehaviorCatalogKind.Interceptor,
                owner,
                name,
                null,
                null,
                JsonRows.StringAt(row, 4) ?? "",
                phase,
                seq,
                [ProjectBinding(registrySnapshot, bindings, new BehaviorBindingKey(
                    BehaviorKind.Interceptor,
                    owner,
                    name,
                    BehaviorCallbackSlot.Handler,
                    phase,
                    seq))]));
        }
    }

    private static BehaviorCallbackBinding ProjectBinding(
        CozoOmRegistrySnapshot registrySnapshot,
        IReadOnlyDictionary<BehaviorBindingKey, BehaviorBindingRow> bindings,
        BehaviorBindingKey key)
    {
        if (!bindings.TryGetValue(key, out var binding))
        {
            return new BehaviorCallbackBinding(ToCatalogSlot(key.CallbackSlot), null, BehaviorReadiness.Unbound);
        }

        return new BehaviorCallbackBinding(
            ToCatalogSlot(key.CallbackSlot),
            binding.BindingId,
            registrySnapshot.TryGetBindingId(key, out var callbackBindingId)
            && string.Equals(binding.BindingId, callbackBindingId, StringComparison.Ordinal)
                ? BehaviorReadiness.Ready
                : BehaviorReadiness.Unresolved);
    }

    private static BehaviorCatalogKind ToCatalogKind(BehaviorKind kind) => kind switch
    {
        BehaviorKind.Constraint => BehaviorCatalogKind.Constraint,
        BehaviorKind.Computed => BehaviorCatalogKind.Computed,
        BehaviorKind.Action => BehaviorCatalogKind.Action,
        BehaviorKind.Mutation => BehaviorCatalogKind.Mutation,
        BehaviorKind.Interceptor => BehaviorCatalogKind.Interceptor,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown behavior kind"),
    };

    private static BehaviorCatalogCallbackSlot ToCatalogSlot(BehaviorCallbackSlot slot) => slot switch
    {
        BehaviorCallbackSlot.When => BehaviorCatalogCallbackSlot.When,
        BehaviorCallbackSlot.Then => BehaviorCatalogCallbackSlot.Then,
        BehaviorCallbackSlot.Validator => BehaviorCatalogCallbackSlot.Validator,
        BehaviorCallbackSlot.Compute => BehaviorCatalogCallbackSlot.Compute,
        BehaviorCallbackSlot.Handler => BehaviorCatalogCallbackSlot.Handler,
        BehaviorCallbackSlot.Executor => BehaviorCatalogCallbackSlot.Executor,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown callback slot"),
    };
}
