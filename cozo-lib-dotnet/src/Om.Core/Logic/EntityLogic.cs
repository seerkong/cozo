using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;
using Cozo.DotNet.Om.Internals;
using Cozo.DotNet.Om.Runtime;
using Cozo.DotNet.Om.Support;

namespace Cozo.DotNet.Om.Logic;

public static class EntityLogic
{
    public static async Task CreateEntityAsync(CozoOmRuntime runtime, EntityInput input, CancellationToken cancellationToken = default)
    {
        var entity = await NormalizeEntityAsync(runtime, input, cancellationToken);
        await runtime.Store.RunAsync(
            CozoScriptBuilder.InputInsert("om_entity", ["id"], ["type_name", "label"]),
            LogicSupport.Params(("id", entity.Id), ("type_name", entity.TypeName), ("label", entity.Label)),
            cancellationToken: cancellationToken);
    }

    public static async Task UpsertEntityAsync(CozoOmRuntime runtime, EntityInput input, CancellationToken cancellationToken = default)
    {
        var entity = await NormalizeEntityAsync(runtime, input, cancellationToken);
        await runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_entity", ["id"], ["type_name", "label"]),
            LogicSupport.Params(("id", entity.Id), ("type_name", entity.TypeName), ("label", entity.Label)),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Physically deletes an entity in a single transaction, cascading to every om_property row
    /// (all temporal versions, physical delete semantics) and every om_edge row where the entity
    /// appears as source or target. Deleting a non-existent entity is a harmless no-op.
    /// </summary>
    public static async Task DeleteEntityAsync(CozoOmRuntime runtime, string entityId, CancellationToken cancellationToken = default)
    {
        var id = OmConvert.RequireName(entityId, nameof(entityId));
        await using var tx = await runtime.Store.BeginTransactionAsync(write: true, cancellationToken);

        await tx.RunAsync(
            """
            ?[entity_id, attr_name, valid_time] :=
              *om_property{ entity_id: $id, attr_name, valid_time, value: _value, tx_time: _tx },
              entity_id = $id
            :rm om_property {entity_id, attr_name, valid_time}
            """,
            LogicSupport.Params(("id", id)),
            cancellationToken: cancellationToken);
        await tx.RunAsync(
            """
            ?[from_id, rel_name, to_id, valid_time] :=
              from_id = $id,
              *om_edge{ from_id, rel_name, to_id, valid_time, props: _props, tx_time: _tx }
            ?[from_id, rel_name, to_id, valid_time] :=
              to_id = $id,
              *om_edge{ from_id, rel_name, to_id, valid_time, props: _props, tx_time: _tx }
            :rm om_edge {from_id, rel_name, to_id, valid_time}
            """,
            LogicSupport.Params(("id", id)),
            cancellationToken: cancellationToken);
        await tx.RunAsync(
            """
            ?[id] <- [[$id]]
            :rm om_entity {id}
            """,
            LogicSupport.Params(("id", id)),
            cancellationToken: cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public static async Task<string> GetEntityTypeAsync(CozoOmRuntime runtime, string entityId, CancellationToken cancellationToken = default)
    {
        var id = OmConvert.RequireName(entityId, nameof(entityId));
        var result = await runtime.Store.RunAsync(
            """
            ?[type_name] :=
              *om_entity{ id: $id, type_name, label: _label }
            :limit 1
            """,
            LogicSupport.Params(("id", id)),
            cancellationToken: cancellationToken);
        if (result.Rows.Count == 0)
        {
            throw new CozoException($"Entity '{id}' does not exist");
        }

        return await TypeLogic.ResolveTypeAsync(runtime, JsonRows.StringAt(result.Rows[0], 0) ?? "", cancellationToken);
    }

    public static async Task SetPropertyAsync(CozoOmRuntime runtime, SetPropertyInput input, CancellationToken cancellationToken = default)
    {
        var id = OmConvert.RequireName(input.EntityId, nameof(input.EntityId));
        var typeName = await GetEntityTypeAsync(runtime, id, cancellationToken);
        var attrName = await TypeLogic.ResolveAttrAsync(runtime, typeName, input.AttrName, cancellationToken);
        await ValidatePropertyTypeAsync(runtime, id, attrName, input.Value, cancellationToken);
        var definitions = await TypeLogic.GetAttributeDefinitionsAsync(runtime, typeName, cancellationToken);
        var definition = definitions[attrName];

        if (input.Options?.SkipConstraints == true)
        {
            await WritePropertyAsync(runtime, id, attrName, definition, input.Value, input.Options, cancellationToken);
            return;
        }

        await using var tx = await runtime.Store.BeginTransactionAsync(write: true, cancellationToken);
        var txRuntime = runtime with { Store = tx };
        await WritePropertyAsync(txRuntime, id, attrName, definition, input.Value, input.Options, cancellationToken);
        var validation = await ConstraintLogic.ValidateEntityAsync(txRuntime, id, cancellationToken);
        if (!validation.Valid)
        {
            throw new CozoException(string.Join("; ", validation.Errors));
        }

        await tx.CommitAsync(cancellationToken);
    }

    private static async Task WritePropertyAsync(
        CozoOmRuntime runtime,
        string id,
        string attrName,
        OmAttribute definition,
        object? value,
        WriteOptions? options,
        CancellationToken cancellationToken)
    {
        var validTime = string.IsNullOrWhiteSpace(options?.ValidTime) ? "ASSERT" : options!.ValidTime!;
        var txTime = runtime.Options.TimeProvider.GetUtcNow().UtcDateTime.ToString("O");

        if (definition.ValueType == OmValueType.Validity)
        {
            var validity = OmConvert.NormalizeValidityInput(value, runtime.Options.TimeProvider.GetUtcNow());
            if (validity is null)
            {
                throw new CozoException($"Invalid Validity value for '{attrName}'");
            }

            await runtime.Store.RunAsync(
                """
                input[entity_id, attr_name, valid_time, ts_us, is_assert, tx_time] <- [[$entity_id, $attr_name, $valid_time, $ts_us, $is_assert, $tx_time]]
                ?[entity_id, attr_name, valid_time, value, tx_time] :=
                  input[entity_id, attr_name, valid_time, ts_us, is_assert, tx_time],
                  value = validity(ts_us, is_assert)
                :put om_property {entity_id, attr_name, valid_time => value, tx_time}
                """,
                LogicSupport.Params(
                    ("entity_id", id),
                    ("attr_name", attrName),
                    ("valid_time", validTime),
                    ("ts_us", validity.TimestampMicroseconds),
                    ("is_assert", validity.IsAssert),
                    ("tx_time", txTime)),
                cancellationToken: cancellationToken);
        }
        else
        {
            await runtime.Store.RunAsync(
                """
                ?[entity_id, attr_name, valid_time, value, tx_time] <- [[$entity_id, $attr_name, $valid_time, $value, $tx_time]]
                :put om_property {entity_id, attr_name, valid_time => value, tx_time}
                """,
                LogicSupport.Params(
                    ("entity_id", id),
                    ("attr_name", attrName),
                    ("valid_time", validTime),
                    ("value", value),
                    ("tx_time", txTime)),
                cancellationToken: cancellationToken);
        }
    }

    public static async Task<JsonElement?> GetPropertyAsync(CozoOmRuntime runtime, string entityId, string attrName, CancellationToken cancellationToken = default)
    {
        return await GetPropertyAtAsync(runtime, entityId, attrName, "\"NOW\"", null, cancellationToken);
    }

    public static async Task<JsonElement?> GetPropertyAsOfAsync(
        CozoOmRuntime runtime,
        string entityId,
        string attrName,
        string asOf,
        CancellationToken cancellationToken = default)
    {
        var timestamp = OmConvert.NormalizeTimestamp(asOf, nameof(asOf));
        return await GetPropertyAtAsync(runtime, entityId, attrName, "$as_of", timestamp, cancellationToken);
    }

    internal static Task<JsonElement?> GetPropertyAtNormalizedAsOfAsync(
        CozoOmRuntime runtime,
        string entityId,
        string attrName,
        string normalizedAsOf,
        CancellationToken cancellationToken = default) =>
        GetPropertyAtAsync(runtime, entityId, attrName, "$as_of", normalizedAsOf, cancellationToken);

    public static async Task<IReadOnlyList<PropertyHistoryEntry>> GetPropertyHistoryAsync(
        CozoOmRuntime runtime,
        string entityId,
        string attrName,
        HistoryRangeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var id = OmConvert.RequireName(entityId, nameof(entityId));
        var typeName = await GetEntityTypeAsync(runtime, id, cancellationToken);
        var canonicalAttr = await TypeLogic.ResolveAttrAsync(runtime, typeName, attrName, cancellationToken);
        var result = await runtime.Store.RunAsync(
            """
            ?[value, valid_time, tx_time] :=
              *om_property{ entity_id: $entity_id, attr_name: $attr_name, valid_time, value, tx_time }
            :sort valid_time
            """,
            LogicSupport.Params(("entity_id", id), ("attr_name", canonicalAttr)),
            cancellationToken: cancellationToken);

        return result.Rows
            .Select(row => new PropertyHistoryEntry(
                JsonRows.ElementAt(row, 0),
                JsonRows.StringAt(row, 1) ?? row[1].ToString(),
                JsonRows.StringAt(row, 2) ?? ""))
            .ToArray();
    }

    public static async Task ValidatePropertyTypeAsync(CozoOmRuntime runtime, string entityId, string attrName, object? value, CancellationToken cancellationToken = default)
    {
        var typeName = await GetEntityTypeAsync(runtime, entityId, cancellationToken);
        var definitions = await TypeLogic.GetAttributeDefinitionsAsync(runtime, typeName, cancellationToken);
        var canonicalAttr = await TypeLogic.ResolveAttrAsync(runtime, typeName, attrName, cancellationToken);
        if (!definitions.TryGetValue(canonicalAttr, out var definition))
        {
            throw new CozoException($"Attribute '{canonicalAttr}' is not defined for type '{typeName}'");
        }

        if (definition.ValueType == OmValueType.Validity)
        {
            if (OmConvert.NormalizeValidityInput(value, runtime.Options.TimeProvider.GetUtcNow()) is null)
            {
                throw new CozoException($"Type mismatch: attribute '{canonicalAttr}' expects {definition.ValueType}, got {OmConvert.InferValueType(value)}");
            }

            return;
        }

        var actual = OmConvert.InferValueType(value);
        if (definition.ValueType != OmValueType.Json && definition.ValueType != actual)
        {
            throw new CozoException($"Type mismatch: attribute '{canonicalAttr}' expects {definition.ValueType}, got {actual}");
        }
    }

    public static async Task<IReadOnlyDictionary<string, JsonElement>> GetAllPropertiesAsync(
        CozoOmRuntime runtime,
        string entityId,
        CancellationToken cancellationToken = default)
    {
        var id = OmConvert.RequireName(entityId, nameof(entityId));
        var typeName = await GetEntityTypeAsync(runtime, id, cancellationToken);
        var result = await runtime.Store.RunAsync(
            """
            ?[attr_name, value] :=
              *om_property{ entity_id: $entity_id, attr_name, value @ "NOW" }
            :sort attr_name
            """,
            LogicSupport.Params(("entity_id", id)),
            cancellationToken: cancellationToken);
        var raw = result.Rows.ToDictionary(
            row => JsonRows.StringAt(row, 0) ?? "",
            row => JsonRows.ElementAt(row, 1),
            StringComparer.Ordinal);
        return await CanonicalizeStoredPropertiesForTypeAsync(runtime, typeName, raw, cancellationToken);
    }

    public static Task<EntityView?> GetEntityViewAsync(CozoOmRuntime runtime, string entityId, CancellationToken cancellationToken = default)
    {
        return GetEntityViewAtAsync(runtime, entityId, null, cancellationToken);
    }

    public static Task<EntityView?> GetEntityViewAsOfAsync(
        CozoOmRuntime runtime,
        string entityId,
        string asOf,
        CancellationToken cancellationToken = default)
    {
        return GetEntityViewAtAsync(runtime, entityId, OmConvert.NormalizeTimestamp(asOf, nameof(asOf)), cancellationToken);
    }

    private static async Task<EntityView?> GetEntityViewAtAsync(
        CozoOmRuntime runtime,
        string entityId,
        string? asOf,
        CancellationToken cancellationToken)
    {
        var id = OmConvert.RequireName(entityId, nameof(entityId));
        var result = await runtime.Store.RunAsync(
            """
            ?[type_name, label] :=
              *om_entity{ id: $id, type_name, label }
            :limit 1
            """,
            LogicSupport.Params(("id", id)),
            cancellationToken: cancellationToken);
        if (result.Rows.Count == 0) return null;

        var typeName = await TypeLogic.ResolveTypeAsync(runtime, JsonRows.StringAt(result.Rows[0], 0) ?? "", cancellationToken);
        var label = JsonRows.StringAt(result.Rows[0], 1) ?? "";
        var properties = asOf is null
            ? await GetAllPropertiesAsync(runtime, id, cancellationToken)
            : await GetAllPropertiesAsOfAsync(runtime, id, typeName, asOf, cancellationToken);

        var computedCallbacks = await runtime.BehaviorGate.ResolveAsync(
            runtime,
            async (resolution, token) =>
            {
                var resolved = new List<(string AttrName, Func<OmComputedContext, ValueTask<object?>> Compute)>();
                foreach (var (computed, ownerType) in await ListEffectiveComputedAttrsAsync(runtime, typeName, token))
                {
                    var compute = await ResolveComputedCallbackAsync(
                        runtime,
                        resolution,
                        ownerType,
                        computed,
                        token);
                    if (!properties.ContainsKey(computed) && compute is not null)
                    {
                        resolved.Add((computed, compute));
                    }
                }

                return resolved;
            },
            cancellationToken);

        foreach (var (computed, compute) in computedCallbacks)
        {
            var value = await compute(new OmComputedContext(runtime, id, typeName, asOf));
            properties = properties
                .Concat([new KeyValuePair<string, JsonElement>(computed, OmConvert.CloneToElement(value))])
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        }

        var neighbors = asOf is null
            ? await RelationLogic.GetNeighborsAsync(runtime, id, direction: OmDirection.Outgoing, cancellationToken: cancellationToken)
            : await RelationLogic.GetNeighborsAsOfAsync(runtime, id, null, asOf, OmDirection.Outgoing, cancellationToken);
        var outgoing = neighbors.Outgoing
            .Select(n => new EntityViewEdge(n.RelName, n.EntityId, n.TypeName, n.Label))
            .ToArray();
        return new EntityView(id, typeName, label, properties, outgoing);
    }

    public static async Task<IReadOnlyList<OmEntity>> FindByTypeAsync(
        CozoOmRuntime runtime,
        string typeName,
        FindByTypeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await TypeLogic.ResolveTypeAsync(runtime, typeName, cancellationToken);
        var types = options?.Exact == true
            ? [canonical]
            : new[] { canonical }.Concat(await TypeLogic.GetDescendantsAsync(runtime, canonical, cancellationToken)).ToArray();
        var result = await runtime.Store.RunAsync(
            """
            ?[id, type_name, label] :=
              *om_entity{ id, type_name, label },
              is_in(type_name, $type_names)
            :sort id
            """,
            LogicSupport.Params(("type_names", types)),
            cancellationToken: cancellationToken);
        return result.Rows
            .Select(row => new OmEntity(
                JsonRows.StringAt(row, 0) ?? "",
                JsonRows.StringAt(row, 1) ?? "",
                JsonRows.StringAt(row, 2) ?? ""))
            .ToArray();
    }

    public static async Task<IReadOnlyList<FindByTypeEntry>> FindByTypeWithPropertiesAsync(
        CozoOmRuntime runtime,
        string typeName,
        IReadOnlyDictionary<string, object?>? filter = null,
        FindByTypeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await TypeLogic.ResolveTypeAsync(runtime, typeName, cancellationToken);
        var types = options?.Exact == true
            ? [canonical]
            : new[] { canonical }.Concat(await TypeLogic.GetDescendantsAsync(runtime, canonical, cancellationToken)).ToArray();
        var result = await runtime.Store.RunAsync(
            """
            ?[id, type_name, label] :=
              *om_entity{ id, type_name, label },
              is_in(type_name, $type_names)
            :sort id
            """,
            LogicSupport.Params(("type_names", types)),
            cancellationToken: cancellationToken);

        var normalizedFilter = await NormalizeFindFilterAsync(runtime, canonical, filter, cancellationToken);
        var entries = new List<FindByTypeEntry>();
        foreach (var row in result.Rows)
        {
            var id = JsonRows.StringAt(row, 0) ?? "";
            var storedType = JsonRows.StringAt(row, 1) ?? "";
            var rowType = await TypeLogic.ResolveTypeAsync(runtime, storedType, cancellationToken);
            var label = JsonRows.StringAt(row, 2) ?? "";
            var properties = await GetAllPropertiesAsync(runtime, id, cancellationToken);
            if (!PropertiesMatch(properties, normalizedFilter)) continue;
            entries.Add(new FindByTypeEntry(id, rowType, label, properties));
        }

        return entries;
    }

    public static async Task<double> AggregateByTypeAsync(
        CozoOmRuntime runtime,
        string typeName,
        string attrName,
        string op,
        FindByTypeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedOp = (op ?? "").Trim().ToLowerInvariant();
        if (normalizedOp is not ("sum" or "avg" or "min" or "max" or "count"))
        {
            throw new ArgumentException($"Unsupported aggregate op '{op}'", nameof(op));
        }

        var canonical = await TypeLogic.ResolveTypeAsync(runtime, typeName, cancellationToken);
        var canonicalAttr = await TypeLogic.ResolveAttrAsync(runtime, canonical, attrName, cancellationToken);
        var types = options?.Exact == true
            ? [canonical]
            : new[] { canonical }.Concat(await TypeLogic.GetDescendantsAsync(runtime, canonical, cancellationToken)).ToArray();
        var result = await runtime.Store.RunAsync(
            """
            ?[value] :=
              *om_entity{ id, type_name, label: _label },
              is_in(type_name, $type_names),
              *om_property{ entity_id: id, attr_name: $attr_name, value @ "NOW" }
            """,
            LogicSupport.Params(("type_names", types), ("attr_name", canonicalAttr)),
            cancellationToken: cancellationToken);

        if (normalizedOp == "count")
        {
            return result.Rows.Count;
        }

        var values = result.Rows
            .Select(row => JsonRows.ElementAt(row, 0))
            .Where(element => element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out _))
            .Select(element => element.GetDouble())
            .ToArray();
        if (values.Length == 0) return 0;
        return normalizedOp switch
        {
            "sum" => values.Sum(),
            "avg" => values.Average(),
            "min" => values.Min(),
            _ => values.Max(),
        };
    }

    private static async Task<OmEntity> NormalizeEntityAsync(CozoOmRuntime runtime, EntityInput input, CancellationToken cancellationToken)
    {
        var id = OmConvert.RequireName(input.Id, nameof(input.Id));
        var typeName = await TypeLogic.ResolveTypeAsync(runtime, input.TypeName, cancellationToken);
        if (!await TypeLogic.TypeExistsAsync(runtime, typeName, cancellationToken))
        {
            throw new CozoException($"Type '{typeName}' does not exist");
        }

        return new OmEntity(id, typeName, input.Label ?? "");
    }

    private static async Task<JsonElement?> GetPropertyAtAsync(
        CozoOmRuntime runtime,
        string entityId,
        string attrName,
        string atExpression,
        string? asOf,
        CancellationToken cancellationToken)
    {
        var id = OmConvert.RequireName(entityId, nameof(entityId));
        var typeName = await GetEntityTypeAsync(runtime, id, cancellationToken);
        var canonicalAttr = await TypeLogic.ResolveAttrAsync(runtime, typeName, attrName, cancellationToken);

        var parameters = LogicSupport.Params(("entity_id", id), ("attr_name", canonicalAttr));
        if (asOf is not null) parameters["as_of"] = asOf;

        var result = await runtime.Store.RunAsync(
            "?[value] :=\n" +
            $"  *om_property{{ entity_id: $entity_id, attr_name: $attr_name, value @ {atExpression} }}\n" +
            ":limit 1",
            parameters,
            cancellationToken: cancellationToken);
        JsonElement? storedValue = result.Rows.Count > 0
            ? JsonRows.ElementAt(result.Rows[0], 0)
            : null;

        if (storedValue is null)
        {
            foreach (var alias in await TypeLogic.GetAttributeAliasesForCanonicalAsync(runtime, typeName, canonicalAttr, cancellationToken))
            {
                var aliasResult = await runtime.Store.RunAsync(
                    "?[value] :=\n" +
                    $"  *om_property{{ entity_id: $entity_id, attr_name: $attr_name, value @ {atExpression} }}\n" +
                    ":limit 1",
                    asOf is null
                        ? LogicSupport.Params(("entity_id", id), ("attr_name", alias))
                        : LogicSupport.Params(("entity_id", id), ("attr_name", alias), ("as_of", asOf)),
                    cancellationToken: cancellationToken);
                if (aliasResult.Rows.Count > 0)
                {
                    storedValue = JsonRows.ElementAt(aliasResult.Rows[0], 0);
                    break;
                }
            }
        }

        var compute = await runtime.BehaviorGate.ResolveAsync(
            runtime,
            async (resolution, token) =>
            {
                var computedOwner = await ResolveComputedOwnerAsync(runtime, typeName, canonicalAttr, token);
                return computedOwner is null
                    ? null
                    : await ResolveComputedCallbackAsync(runtime, resolution, computedOwner, canonicalAttr, token);
            },
            cancellationToken);
        if (storedValue is not null)
        {
            return storedValue;
        }

        if (compute is not null)
        {
            return OmConvert.CloneToElement(await compute(new OmComputedContext(runtime, id, typeName, asOf)));
        }

        return null;
    }

    private static async Task<IReadOnlyDictionary<string, JsonElement>> GetAllPropertiesAsOfAsync(
        CozoOmRuntime runtime,
        string entityId,
        string typeName,
        string asOf,
        CancellationToken cancellationToken)
    {
        var result = await runtime.Store.RunAsync(
            """
            ?[attr_name, value] :=
              *om_property{ entity_id: $entity_id, attr_name, value @ $as_of }
            :sort attr_name
            """,
            LogicSupport.Params(("entity_id", entityId), ("as_of", asOf)),
            cancellationToken: cancellationToken);
        var raw = result.Rows.ToDictionary(
            row => JsonRows.StringAt(row, 0) ?? "",
            row => JsonRows.ElementAt(row, 1),
            StringComparer.Ordinal);
        return await CanonicalizeStoredPropertiesForTypeAsync(runtime, typeName, raw, cancellationToken);
    }

    private static async Task<IReadOnlyList<(string AttrName, string OwnerType)>> ListEffectiveComputedAttrsAsync(
        CozoOmRuntime runtime,
        string typeName,
        CancellationToken cancellationToken)
    {
        var canonical = await TypeLogic.ResolveTypeAsync(runtime, typeName, cancellationToken);
        var chain = (await TypeLogic.GetAncestorsAsync(runtime, canonical, cancellationToken)).Reverse().Concat([canonical]);
        var computed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var currentType in chain)
        {
            foreach (var attrName in await ConstraintLogic.ListComputedAttrsAsync(runtime, currentType, cancellationToken))
            {
                computed[attrName] = currentType;
            }
        }

        return computed
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => (item.Key, item.Value))
            .ToArray();
    }

    private static async Task<string?> ResolveComputedOwnerAsync(
        CozoOmRuntime runtime,
        string typeName,
        string attrName,
        CancellationToken cancellationToken)
    {
        var canonical = await TypeLogic.ResolveTypeAsync(runtime, typeName, cancellationToken);
        var canonicalAttr = await TypeLogic.ResolveAttrAsync(runtime, canonical, attrName, cancellationToken);
        foreach (var (computedAttr, ownerType) in await ListEffectiveComputedAttrsAsync(runtime, canonical, cancellationToken))
        {
            var resolved = await TypeLogic.ResolveAttrAsync(runtime, canonical, computedAttr, cancellationToken);
            if (resolved == canonicalAttr)
            {
                return ownerType;
            }
        }

        return null;
    }

    private static async Task<Func<OmComputedContext, ValueTask<object?>>?> ResolveComputedCallbackAsync(
        CozoOmRuntime runtime,
        BehaviorResolutionScope resolution,
        string ownerType,
        string attrName,
        CancellationToken cancellationToken)
    {
        var key = new BehaviorBindingKey(
            BehaviorKind.Computed,
            ownerType,
            attrName,
            BehaviorCallbackSlot.Compute,
            BehaviorBindingLogic.NonInterceptorPhase,
            BehaviorBindingLogic.NonInterceptorSeq);
        await BehaviorReadinessLogic.EnsureReadyIfBoundAsync(runtime, resolution, key, cancellationToken);
        return resolution.RegistrySnapshot.Computed.TryGetValue((ownerType, attrName), out var registration)
            ? registration.Callback
            : null;
    }

    private static async Task<IReadOnlyDictionary<string, JsonElement>> NormalizeFindFilterAsync(
        CozoOmRuntime runtime,
        string typeName,
        IReadOnlyDictionary<string, object?>? filter,
        CancellationToken cancellationToken)
    {
        var normalized = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (attrName, expected) in filter ?? new Dictionary<string, object?>())
        {
            var canonicalAttr = await TypeLogic.ResolveAttrAsync(runtime, typeName, attrName, cancellationToken);
            normalized[canonicalAttr] = OmConvert.CloneToElement(expected);
        }

        return normalized;
    }

    private static bool PropertiesMatch(
        IReadOnlyDictionary<string, JsonElement> properties,
        IReadOnlyDictionary<string, JsonElement> filter)
    {
        foreach (var (attrName, expected) in filter)
        {
            if (!properties.TryGetValue(attrName, out var actual)) return false;
            if (!JsonElementEquals(actual, expected)) return false;
        }

        return true;
    }

    private static bool JsonElementEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
            {
                return left.TryGetDouble(out var leftDouble) &&
                       right.TryGetDouble(out var rightDouble) &&
                       leftDouble.Equals(rightDouble);
            }

            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.TryGetDouble(out var leftDouble) &&
                                    right.TryGetDouble(out var rightDouble) &&
                                    leftDouble.Equals(rightDouble),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null => true,
            _ => left.GetRawText() == right.GetRawText(),
        };
    }

    private static async Task<IReadOnlyDictionary<string, JsonElement>> CanonicalizeStoredPropertiesForTypeAsync(
        CozoOmRuntime runtime,
        string typeName,
        IReadOnlyDictionary<string, JsonElement> rawProperties,
        CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var sourceIsCanonical = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (storedAttr, value) in rawProperties)
        {
            if (string.IsNullOrWhiteSpace(storedAttr)) continue;
            var canonicalAttr = await TypeLogic.ResolveAttrAsync(runtime, typeName, storedAttr, cancellationToken);
            var isCanonicalSource = storedAttr == canonicalAttr;
            if (!properties.ContainsKey(canonicalAttr))
            {
                properties[canonicalAttr] = value;
                sourceIsCanonical[canonicalAttr] = isCanonicalSource;
                continue;
            }

            if (!sourceIsCanonical.GetValueOrDefault(canonicalAttr) && isCanonicalSource)
            {
                properties[canonicalAttr] = value;
                sourceIsCanonical[canonicalAttr] = true;
            }
        }

        return properties;
    }
}
