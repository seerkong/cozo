using Cozo.DotNet.Om.Internals;
using Cozo.DotNet.Om.Runtime;
using Cozo.DotNet.Om.Support;

namespace Cozo.DotNet.Om.Logic;

internal enum BehaviorKind
{
    Constraint,
    Computed,
    Action,
    Mutation,
    Interceptor,
}

internal enum BehaviorCallbackSlot
{
    When,
    Then,
    Validator,
    Compute,
    Handler,
    Executor,
}

internal sealed record BehaviorBindingKey(
    BehaviorKind BehaviorKind,
    string OwnerType,
    string BehaviorName,
    BehaviorCallbackSlot CallbackSlot,
    string Phase,
    int Seq);

internal sealed record BehaviorBindingRow(BehaviorBindingKey Key, string BindingId);

internal static class BehaviorBindingLogic
{
    internal const string NonInterceptorPhase = "";
    internal const int NonInterceptorSeq = -1;

    internal static async Task PutAsync(
        CozoOmRuntime runtime,
        BehaviorBindingRow row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(row);
        var normalized = Normalize(row);

        await runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut(
                "om_behavior_binding",
                ["behavior_kind", "owner_type", "behavior_name", "callback_slot", "phase", "seq"],
                ["binding_id"]),
            LogicSupport.Params(
                ("behavior_kind", ToStored(normalized.Key.BehaviorKind)),
                ("owner_type", normalized.Key.OwnerType),
                ("behavior_name", normalized.Key.BehaviorName),
                ("callback_slot", ToStored(normalized.Key.CallbackSlot)),
                ("phase", normalized.Key.Phase),
                ("seq", normalized.Key.Seq),
                ("binding_id", normalized.BindingId)),
            cancellationToken: cancellationToken);
    }

    internal static async Task<IReadOnlyList<BehaviorBindingRow>> ListAsync(
        CozoOmRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var result = await runtime.Store.RunAsync(
            """
            ?[behavior_kind, owner_type, behavior_name, callback_slot, phase, seq, binding_id] :=
              *om_behavior_binding{behavior_kind, owner_type, behavior_name, callback_slot, phase, seq, binding_id}
            """,
            cancellationToken: cancellationToken);

        return result.Rows
            .Select(ReadRow)
            .OrderBy(row => row.Key.BehaviorKind)
            .ThenBy(row => row.Key.OwnerType, StringComparer.Ordinal)
            .ThenBy(row => row.Key.BehaviorName, StringComparer.Ordinal)
            .ThenBy(row => row.Key.CallbackSlot)
            .ThenBy(row => row.Key.Phase, StringComparer.Ordinal)
            .ThenBy(row => row.Key.Seq)
            .ToArray();
    }

    internal static async Task<BehaviorBindingRow?> QueryAsync(
        CozoOmRuntime runtime,
        BehaviorBindingKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var normalized = NormalizeKey(key);
        var result = await runtime.Store.RunAsync(
            """
            ?[binding_id] :=
              *om_behavior_binding{
                behavior_kind: $behavior_kind,
                owner_type: $owner_type,
                behavior_name: $behavior_name,
                callback_slot: $callback_slot,
                phase: $phase,
                seq: $seq,
                binding_id
              }
            :limit 1
            """,
            LogicSupport.Params(
                ("behavior_kind", ToStored(normalized.BehaviorKind)),
                ("owner_type", normalized.OwnerType),
                ("behavior_name", normalized.BehaviorName),
                ("callback_slot", ToStored(normalized.CallbackSlot)),
                ("phase", normalized.Phase),
                ("seq", normalized.Seq)),
            cancellationToken: cancellationToken);

        return result.Rows.Count == 0
            ? null
            : new BehaviorBindingRow(normalized, RequireStoredName(JsonRows.StringAt(result.Rows[0], 0), "binding_id"));
    }

    internal static Task RemoveAsync(
        CozoOmRuntime runtime,
        BehaviorBindingKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var normalized = NormalizeKey(key);
        return runtime.Store.RunAsync(
            """
            ?[behavior_kind, owner_type, behavior_name, callback_slot, phase, seq] <-
              [[$behavior_kind, $owner_type, $behavior_name, $callback_slot, $phase, $seq]]
            :rm om_behavior_binding {behavior_kind, owner_type, behavior_name, callback_slot, phase, seq}
            """,
            LogicSupport.Params(
                ("behavior_kind", ToStored(normalized.BehaviorKind)),
                ("owner_type", normalized.OwnerType),
                ("behavior_name", normalized.BehaviorName),
                ("callback_slot", ToStored(normalized.CallbackSlot)),
                ("phase", normalized.Phase),
                ("seq", normalized.Seq)),
            cancellationToken: cancellationToken);
    }

    private static BehaviorBindingRow Normalize(BehaviorBindingRow row) =>
        new(NormalizeKey(row.Key), OmConvert.RequireName(row.BindingId, nameof(row.BindingId)));

    private static BehaviorBindingKey NormalizeKey(BehaviorBindingKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var ownerType = OmConvert.RequireName(key.OwnerType, nameof(key.OwnerType));
        var behaviorName = OmConvert.RequireName(key.BehaviorName, nameof(key.BehaviorName));
        ValidateSlot(key.BehaviorKind, key.CallbackSlot);

        if (key.BehaviorKind == BehaviorKind.Interceptor)
        {
            if (key.Phase is not ("before" or "after"))
            {
                throw new ArgumentException("Interceptor phase must be exactly 'before' or 'after'", nameof(key.Phase));
            }

            if (key.Seq < 0)
            {
                throw new ArgumentException("Interceptor sequence must be non-negative", nameof(key.Seq));
            }
        }
        else if (key.Phase != NonInterceptorPhase || key.Seq != NonInterceptorSeq)
        {
            throw new ArgumentException("Non-interceptor bindings require empty phase and sequence -1", nameof(key));
        }

        return key with { OwnerType = ownerType, BehaviorName = behaviorName };
    }

    private static void ValidateSlot(BehaviorKind kind, BehaviorCallbackSlot slot)
    {
        var valid = kind switch
        {
            BehaviorKind.Constraint => slot is BehaviorCallbackSlot.When or BehaviorCallbackSlot.Then or BehaviorCallbackSlot.Validator,
            BehaviorKind.Computed => slot == BehaviorCallbackSlot.Compute,
            BehaviorKind.Action => slot == BehaviorCallbackSlot.Handler,
            BehaviorKind.Mutation => slot == BehaviorCallbackSlot.Executor,
            BehaviorKind.Interceptor => slot == BehaviorCallbackSlot.Handler,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException($"Callback slot '{slot}' is invalid for behavior kind '{kind}'", nameof(slot));
        }
    }

    private static BehaviorBindingRow ReadRow(IReadOnlyList<System.Text.Json.JsonElement> row)
    {
        var key = new BehaviorBindingKey(
            ParseKind(RequireStoredName(JsonRows.StringAt(row, 0), "behavior_kind")),
            RequireStoredName(JsonRows.StringAt(row, 1), "owner_type"),
            RequireStoredName(JsonRows.StringAt(row, 2), "behavior_name"),
            ParseSlot(RequireStoredName(JsonRows.StringAt(row, 3), "callback_slot")),
            JsonRows.StringAt(row, 4) ?? "",
            JsonRows.IntAt(row, 5));
        return Normalize(new BehaviorBindingRow(key, RequireStoredName(JsonRows.StringAt(row, 6), "binding_id")));
    }

    private static string RequireStoredName(string? value, string fieldName) =>
        OmConvert.RequireName(value ?? "", fieldName);

    private static string ToStored(BehaviorKind kind) => kind switch
    {
        BehaviorKind.Constraint => "constraint",
        BehaviorKind.Computed => "computed",
        BehaviorKind.Action => "action",
        BehaviorKind.Mutation => "mutation",
        BehaviorKind.Interceptor => "interceptor",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown behavior kind"),
    };

    private static BehaviorKind ParseKind(string value) => value switch
    {
        "constraint" => BehaviorKind.Constraint,
        "computed" => BehaviorKind.Computed,
        "action" => BehaviorKind.Action,
        "mutation" => BehaviorKind.Mutation,
        "interceptor" => BehaviorKind.Interceptor,
        _ => throw new InvalidOperationException($"Stored behavior kind '{value}' is invalid"),
    };

    private static string ToStored(BehaviorCallbackSlot slot) => slot switch
    {
        BehaviorCallbackSlot.When => "when",
        BehaviorCallbackSlot.Then => "then",
        BehaviorCallbackSlot.Validator => "validator",
        BehaviorCallbackSlot.Compute => "compute",
        BehaviorCallbackSlot.Handler => "handler",
        BehaviorCallbackSlot.Executor => "executor",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown callback slot"),
    };

    private static BehaviorCallbackSlot ParseSlot(string value) => value switch
    {
        "when" => BehaviorCallbackSlot.When,
        "then" => BehaviorCallbackSlot.Then,
        "validator" => BehaviorCallbackSlot.Validator,
        "compute" => BehaviorCallbackSlot.Compute,
        "handler" => BehaviorCallbackSlot.Handler,
        "executor" => BehaviorCallbackSlot.Executor,
        _ => throw new InvalidOperationException($"Stored callback slot '{value}' is invalid"),
    };
}
