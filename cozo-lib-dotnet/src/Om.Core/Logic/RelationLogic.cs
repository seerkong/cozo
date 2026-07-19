using Cozo.DotNet;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;
using Cozo.DotNet.Om.Internals;
using Cozo.DotNet.Om.Runtime;

namespace Cozo.DotNet.Om.Logic;

public static class RelationLogic
{
    public static async Task ValidateRelationAsync(
        CozoOmRuntime runtime,
        string fromId,
        string relName,
        string toId,
        CancellationToken cancellationToken = default)
    {
        var relation = await TypeLogic.GetRelationDefinitionAsync(runtime, relName, cancellationToken);
        var fromType = await EntityLogic.GetEntityTypeAsync(runtime, fromId, cancellationToken);
        var toType = await EntityLogic.GetEntityTypeAsync(runtime, toId, cancellationToken);
        var forward = await TypeLogic.IsSubtypeOfAsync(runtime, fromType, relation.FromType, cancellationToken) &&
                      await TypeLogic.IsSubtypeOfAsync(runtime, toType, relation.ToType, cancellationToken);
        var reverse = !relation.Directed &&
                      await TypeLogic.IsSubtypeOfAsync(runtime, fromType, relation.ToType, cancellationToken) &&
                      await TypeLogic.IsSubtypeOfAsync(runtime, toType, relation.FromType, cancellationToken);
        if (!forward && !reverse)
        {
            throw new CozoException($"Relation '{relation.RelName}' cannot link '{fromType}' -> '{toType}'");
        }
    }

    public static async Task LinkEntitiesAsync(CozoOmRuntime runtime, LinkEntitiesInput input, CancellationToken cancellationToken = default)
    {
        var fromId = OmConvert.RequireName(input.FromId, nameof(input.FromId));
        var toId = OmConvert.RequireName(input.ToId, nameof(input.ToId));
        var relName = await TypeLogic.ResolveRelAsync(runtime, input.RelName, cancellationToken);
        await ValidateRelationAsync(runtime, fromId, relName, toId, cancellationToken);

        if (input.Options?.SkipConstraints == true)
        {
            await WriteEdgeAsync(runtime, fromId, relName, toId, input.Props, input.Options, cancellationToken);
            return;
        }

        await using var tx = await runtime.Store.BeginTransactionAsync(write: true, cancellationToken);
        var txRuntime = runtime with { Store = tx };
        await WriteEdgeAsync(txRuntime, fromId, relName, toId, input.Props, input.Options, cancellationToken);
        var validation = await ConstraintLogic.ValidateConstraintsAsync(txRuntime, fromId, ["cross-entity"], cancellationToken);
        if (!validation.Valid)
        {
            throw new CozoException(string.Join("; ", validation.Errors));
        }

        await tx.CommitAsync(cancellationToken);
    }

    private static async Task WriteEdgeAsync(
        CozoOmRuntime runtime,
        string fromId,
        string relName,
        string toId,
        object? props,
        WriteOptions? options,
        CancellationToken cancellationToken)
    {
        var validTime = string.IsNullOrWhiteSpace(options?.ValidTime) ? "ASSERT" : options!.ValidTime!;
        var txTime = runtime.Options.TimeProvider.GetUtcNow().UtcDateTime.ToString("O");

        await runtime.Store.RunAsync(
            """
            ?[from_id, rel_name, to_id, valid_time, props, tx_time] <- [[$from_id, $rel_name, $to_id, $valid_time, $props, $tx_time]]
            :put om_edge {from_id, rel_name, to_id, valid_time => props, tx_time}
            """,
            LogicSupport.Params(
                    ("from_id", fromId),
                    ("rel_name", relName),
                    ("to_id", toId),
                    ("valid_time", validTime),
                    ("props", props ?? new Dictionary<string, object?>()),
                    ("tx_time", txTime)),
            cancellationToken: cancellationToken);
    }

    public static async Task UnlinkEntitiesAsync(
        CozoOmRuntime runtime,
        string fromId,
        string relName,
        string toId,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var canonicalRel = await TypeLogic.ResolveRelAsync(runtime, relName, cancellationToken);
        await ValidateRelationAsync(runtime, fromId, canonicalRel, toId, cancellationToken);
        var requested = options?.ValidTime;
        var validTime = string.IsNullOrWhiteSpace(requested)
            ? "RETRACT"
            : requested!.StartsWith('~') ? requested : $"~{requested}";
        var txTime = runtime.Options.TimeProvider.GetUtcNow().UtcDateTime.ToString("O");

        await runtime.Store.RunAsync(
            """
            ?[from_id, rel_name, to_id, valid_time, props, tx_time] <- [[$from_id, $rel_name, $to_id, $valid_time, {}, $tx_time]]
            :put om_edge {from_id, rel_name, to_id, valid_time => props, tx_time}
            """,
            LogicSupport.Params(
                ("from_id", fromId),
                ("rel_name", canonicalRel),
                ("to_id", toId),
                ("valid_time", validTime),
                ("tx_time", txTime)),
            cancellationToken: cancellationToken);
    }

    public static async Task<NeighborResult> GetNeighborsAsync(
        CozoOmRuntime runtime,
        string entityId,
        string? relName = null,
        OmDirection direction = OmDirection.Both,
        CancellationToken cancellationToken = default)
    {
        return await GetNeighborsAtAsync(runtime, entityId, relName, direction, "\"NOW\"", null, cancellationToken);
    }

    public static async Task<NeighborResult> GetNeighborsAsOfAsync(
        CozoOmRuntime runtime,
        string entityId,
        string? relName,
        string asOf,
        OmDirection direction = OmDirection.Both,
        CancellationToken cancellationToken = default)
    {
        var timestamp = OmConvert.NormalizeTimestamp(asOf, nameof(asOf));
        return await GetNeighborsAtAsync(runtime, entityId, relName, direction, "$as_of", timestamp, cancellationToken);
    }

    public static async Task<IReadOnlyList<OmEntity>> TraverseAsync(
        CozoOmRuntime runtime,
        string startEntityId,
        IReadOnlyList<string>? relationPath,
        CancellationToken cancellationToken = default)
    {
        var start = OmConvert.RequireName(startEntityId, nameof(startEntityId));
        if (relationPath is null || relationPath.Count == 0)
        {
            var view = await EntityLogic.GetEntityViewAsync(runtime, start, cancellationToken);
            return view is null ? [] : [new OmEntity(view.Id, view.TypeName, view.Label)];
        }

        var frontier = new[] { start };
        IReadOnlyList<OmEntity> level = [];
        foreach (var relation in relationPath)
        {
            var relationName = OmConvert.RequireName(relation, nameof(relationPath));
            var next = new Dictionary<string, OmEntity>(StringComparer.Ordinal);
            foreach (var entityId in frontier)
            {
                var neighbors = await GetNeighborsAsync(
                    runtime,
                    entityId,
                    relationName,
                    OmDirection.Outgoing,
                    cancellationToken);
                foreach (var neighbor in neighbors.Outgoing)
                {
                    next.TryAdd(neighbor.EntityId, new OmEntity(neighbor.EntityId, neighbor.TypeName, neighbor.Label));
                }
            }

            level = next.Values.OrderBy(entity => entity.Id, StringComparer.Ordinal).ToArray();
            if (level.Count == 0) return level;
            frontier = level.Select(entity => entity.Id).ToArray();
        }

        return level;
    }

    internal static Task<NeighborResult> GetNeighborsAtNormalizedAsOfAsync(
        CozoOmRuntime runtime,
        string entityId,
        string? relName,
        string normalizedAsOf,
        OmDirection direction = OmDirection.Both,
        CancellationToken cancellationToken = default) =>
        GetNeighborsAtAsync(runtime, entityId, relName, direction, "$as_of", normalizedAsOf, cancellationToken);

    public static async Task<IReadOnlyList<EdgeHistoryEntry>> GetEdgeHistoryAsync(
        CozoOmRuntime runtime,
        string fromId,
        string relName,
        string? toId = null,
        HistoryRangeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var filters = string.IsNullOrWhiteSpace(toId) ? "" : ",\n  to_id = $to_id";
        var parameters = LogicSupport.Params(
            ("from_id", OmConvert.RequireName(fromId, nameof(fromId))),
            ("rel_name", await TypeLogic.ResolveRelAsync(runtime, relName, cancellationToken)));
        if (!string.IsNullOrWhiteSpace(toId)) parameters["to_id"] = toId;

        var result = await runtime.Store.RunAsync(
            "?[to_id, props, valid_time, tx_time, is_assert] :=\n" +
            "  *om_edge{ from_id: $from_id, rel_name: $rel_name, to_id, valid_time, props, tx_time },\n" +
            $"  is_assert = to_bool(valid_time){filters}\n" +
            ":sort valid_time, to_id",
            parameters,
            cancellationToken: cancellationToken);
        return result.Rows
            .Select(row => new EdgeHistoryEntry(
                fromId,
                relName,
                JsonRows.StringAt(row, 0) ?? "",
                JsonRows.ElementAt(row, 1),
                JsonRows.StringAt(row, 2) ?? row[2].ToString(),
                JsonRows.StringAt(row, 3) ?? "",
                JsonRows.BoolAt(row, 4)))
            .ToArray();
    }

    private static async Task<NeighborResult> GetNeighborsAtAsync(
        CozoOmRuntime runtime,
        string entityId,
        string? relName,
        OmDirection direction,
        string atExpression,
        string? asOf,
        CancellationToken cancellationToken)
    {
        var id = OmConvert.RequireName(entityId, nameof(entityId));
        var relFilter = "";
        var parameters = LogicSupport.Params(("id", id));
        if (asOf is not null) parameters["as_of"] = asOf;
        if (!string.IsNullOrWhiteSpace(relName))
        {
            parameters["rel_name"] = await TypeLogic.ResolveRelAsync(runtime, relName!, cancellationToken);
            relFilter = ",\n  rel_name = $rel_name";
        }

        var outgoing = direction is OmDirection.Outgoing or OmDirection.Both
            ? await NeighborRowsAsync(runtime, OutgoingScript(atExpression, relFilter), parameters, cancellationToken)
            : [];
        var incoming = direction is OmDirection.Incoming or OmDirection.Both
            ? await NeighborRowsAsync(runtime, IncomingScript(atExpression, relFilter), parameters, cancellationToken)
            : [];
        return new NeighborResult(outgoing, incoming);
    }

    private static string OutgoingScript(string atExpression, string relFilter)
    {
        return "?[rel_name, node_id, node_type, node_label] :=\n" +
               $"  *om_edge{{ from_id: $id, rel_name, to_id: node_id, props: _props @ {atExpression} }},\n" +
               $"  *om_entity{{ id: node_id, type_name: node_type, label: node_label }}{relFilter}\n" +
               ":sort rel_name, node_id";
    }

    private static string IncomingScript(string atExpression, string relFilter)
    {
        return "?[rel_name, node_id, node_type, node_label] :=\n" +
               $"  *om_edge{{ from_id: node_id, rel_name, to_id: $id, props: _props @ {atExpression} }},\n" +
               $"  *om_entity{{ id: node_id, type_name: node_type, label: node_label }}{relFilter}\n" +
               ":sort rel_name, node_id";
    }

    private static async Task<IReadOnlyList<NeighborEntry>> NeighborRowsAsync(
        CozoOmRuntime runtime,
        string script,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(script, parameters, cancellationToken: cancellationToken);
        var output = new List<NeighborEntry>();
        foreach (var row in result.Rows)
        {
            output.Add(new NeighborEntry(
                await TypeLogic.ResolveRelAsync(runtime, JsonRows.StringAt(row, 0) ?? "", cancellationToken),
                JsonRows.StringAt(row, 1) ?? "",
                await TypeLogic.ResolveTypeAsync(runtime, JsonRows.StringAt(row, 2) ?? "", cancellationToken),
                JsonRows.StringAt(row, 3) ?? ""));
        }

        return output;
    }
}
