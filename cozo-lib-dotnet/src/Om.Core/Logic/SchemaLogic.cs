using System.Text.Json;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;
using Cozo.DotNet.Om.Internals;
using Cozo.DotNet.Om.Runtime;
using Cozo.DotNet.Om.Support;
using Cozo.DotNet;

namespace Cozo.DotNet.Om.Logic;

public static class SchemaLogic
{
    public static async Task InitSchemaAsync(CozoOmRuntime runtime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var creates = new[]
        {
            ":create om_type {name => description, parent_type}",
            ":create om_mixin {name => description}",
            ":create om_type_mixin {type_name, mixin_name}",
            ":create om_action_def {type_name, action_name => description}",
            ":create om_mutation_def {type_name, mutation_name => description}",
            ":create om_interceptor_def {type_name, action_name, phase, seq => description}",
            ":create om_constraint_def {type_name, constraint_name => constraint_type, message}",
            ":create om_computed_def {type_name, attr_name => description}",
            ":create om_perm_action {action => description}",
            ":create om_perm_policy {policy_id => effect, action, resource_type, enabled, description}",
            ":create om_perm_abac_rule {policy_id, left_ref, op, right_ref}",
            ":create om_perm_path_rule {policy_id, path}",
            ":create om_attr_def {type_name, attr_name => value_type, required}",
            ":create om_rel_def {rel_name => from_type, to_type, directed}",
            ":create om_entity {id => type_name, label}",
            ":create om_property {entity_id: String, attr_name: String, valid_time: Validity => value, tx_time: String}",
            ":create om_edge {from_id: String, rel_name: String, to_id: String, valid_time: Validity => props, tx_time: String}",
            ":create om_attr_desc {type_name, attr_name => description}",
            ":create om_rel_desc {rel_name => description}",
            ":create om_schema_state {id => current_version, current_checksum}",
            ":create om_schema_version {version => created_at, label, description, parent_version, checksum}",
            ":create om_schema_migration {migration_id => from_version, to_version, applied_at, applied_by, status, error, summary_json}",
            ":create om_schema_snapshot {version => snapshot_json}",
            ":create om_alias_type {alias => canonical}",
            ":create om_alias_rel {alias => canonical}",
            ":create om_alias_attr {type_name, alias_attr => canonical_attr}",
            ":create om_existential_rule_def {rule_name => spec_json, mode, message, enabled}",
        };

        foreach (var create in creates)
        {
            await LogicSupport.CreateIgnoreConflictAsync(runtime, create, cancellationToken);
        }

        await SeedSchemaStateAsync(runtime, cancellationToken);
    }

    public static async Task<SchemaState> GetSchemaStateAsync(CozoOmRuntime runtime, CancellationToken cancellationToken = default)
    {
        var result = await runtime.Store.RunAsync(
            """
            ?[current_version, current_checksum] :=
              *om_schema_state{ id: "default", current_version, current_checksum }
            :limit 1
            """,
            cancellationToken: cancellationToken);
        if (result.Rows.Count == 0)
        {
            throw new CozoException("Schema state not initialized; call InitSchemaAsync first.");
        }

        var row = result.Rows[0];
        return new SchemaState(JsonRows.IntAt(row, 0), JsonRows.StringAt(row, 1));
    }

    public static async Task<IReadOnlyList<SchemaVersion>> ListSchemaVersionsAsync(CozoOmRuntime runtime, CancellationToken cancellationToken = default)
    {
        var result = await runtime.Store.RunAsync(
            """
            ?[version, created_at, label, description, parent_version, checksum] :=
              *om_schema_version{ version, created_at, label, description, parent_version, checksum }
            :sort version
            """,
            cancellationToken: cancellationToken);
        return result.Rows.Select(row => new SchemaVersion(
            JsonRows.IntAt(row, 0),
            JsonRows.StringAt(row, 1) ?? "",
            JsonRows.StringAt(row, 2),
            JsonRows.StringAt(row, 3),
            row[4].ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : row[4].GetInt32(),
            JsonRows.StringAt(row, 5))).ToArray();
    }

    public static async Task<SchemaSnapshot> WriteSchemaSnapshotAsync(
        CozoOmRuntime runtime,
        int version,
        string? label = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var createdAt = runtime.Options.TimeProvider.GetUtcNow().UtcDateTime.ToString("O");
        var schema = await ReadCurrentSchemaObjectAsync(runtime, cancellationToken);
        var checksum = ComputeChecksum(schema);
        var schemaJson = schema.GetRawText();

        await runtime.Store.RunAsync(
            """
            ?[version, snapshot_json] <- [[$version, $snapshot_json]]
            :put om_schema_snapshot {version => snapshot_json}
            """,
            LogicSupport.Params(("version", version), ("snapshot_json", schemaJson)),
            cancellationToken: cancellationToken);
        await runtime.Store.RunAsync(
            """
            ?[version, created_at, label, description, parent_version, checksum] <- [[$version, $created_at, $label, $description, $parent_version, $checksum]]
            :put om_schema_version {version => created_at, label, description, parent_version, checksum}
            """,
            LogicSupport.Params(
                ("version", version),
                ("created_at", createdAt),
                ("label", label),
                ("description", description),
                ("parent_version", null),
                ("checksum", checksum)),
            cancellationToken: cancellationToken);
        return new SchemaSnapshot(version, createdAt, schema, checksum);
    }

    public static async Task<SchemaSnapshot?> ReadSchemaSnapshotAsync(CozoOmRuntime runtime, int version, CancellationToken cancellationToken = default)
    {
        var result = await runtime.Store.RunAsync(
            """
            ?[snapshot_json] :=
              *om_schema_snapshot{ version: $version, snapshot_json }
            :limit 1
            """,
            LogicSupport.Params(("version", version)),
            cancellationToken: cancellationToken);
        if (result.Rows.Count == 0) return null;

        var raw = JsonRows.StringAt(result.Rows[0], 0) ?? "{}";
        using var doc = JsonDocument.Parse(raw);
        var schema = doc.RootElement.Clone();
        var versionRows = await ListSchemaVersionsAsync(runtime, cancellationToken);
        var meta = versionRows.FirstOrDefault(v => v.Version == version);
        return new SchemaSnapshot(version, meta?.CreatedAt ?? "", schema, meta?.Checksum);
    }

    public static async Task<SchemaDiff> DiffSchemaVersionsAsync(CozoOmRuntime runtime, int fromVersion, int toVersion, CancellationToken cancellationToken = default)
    {
        var from = await ReadSchemaSnapshotAsync(runtime, fromVersion, cancellationToken)
            ?? throw new CozoException($"Missing schema snapshot for version={fromVersion}");
        var to = await ReadSchemaSnapshotAsync(runtime, toVersion, cancellationToken)
            ?? throw new CozoException($"Missing schema snapshot for version={toVersion}");
        return DiffSnapshots(from, to);
    }

    public static async Task<SchemaDiff> DiffCurrentAgainstSnapshotAsync(CozoOmRuntime runtime, int fromVersion, CancellationToken cancellationToken = default)
    {
        var from = await ReadSchemaSnapshotAsync(runtime, fromVersion, cancellationToken)
            ?? throw new CozoException($"Missing schema snapshot for version={fromVersion}");
        var current = new SchemaSnapshot(0, "", await ReadCurrentSchemaObjectAsync(runtime, cancellationToken), null);
        return DiffSnapshots(from, current);
    }

    public static async Task RollbackSchemaAsync(CozoOmRuntime runtime, int version, bool strict = false, CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadSchemaSnapshotAsync(runtime, version, cancellationToken)
            ?? throw new CozoException($"Missing schema snapshot for version={version}");
        if (!snapshot.Schema.TryGetProperty("schema", out var schemaRoot))
        {
            throw new CozoException($"Schema snapshot version={version} has invalid shape");
        }

        foreach (var spec in RollbackRelations)
        {
            await ClearRelationAsync(runtime, spec, cancellationToken);
        }

        foreach (var spec in RollbackRelations)
        {
            if (!schemaRoot.TryGetProperty(spec.Relation, out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in rows.EnumerateArray())
            {
                var cells = row.EnumerateArray().ToArray();
                var entries = new List<(string Key, object? Value)>();
                for (var i = 0; i < spec.Columns.Length && i < cells.Length; i++)
                {
                    entries.Add((spec.Columns[i], JsonSerializer.Deserialize<object?>(cells[i].GetRawText(), OmConvert.JsonOptions)));
                }

                await runtime.Store.RunAsync(
                    CozoScriptBuilder.InputPut(spec.Relation, spec.KeyColumns, spec.Columns.Skip(spec.KeyColumns.Length).ToArray()),
                    LogicSupport.Params(entries.ToArray()),
                    cancellationToken: cancellationToken);
            }
        }

        await runtime.Store.RunAsync(
            """
            ?[id, current_version, current_checksum] <- [["default", $current_version, $current_checksum]]
            :put om_schema_state {id => current_version, current_checksum}
            """,
            LogicSupport.Params(("current_version", version), ("current_checksum", snapshot.Checksum ?? "")),
            cancellationToken: cancellationToken);
    }

    public static async Task<SchemaMigrationResult> ApplySchemaMigrationAsync(
        CozoOmRuntime runtime,
        SchemaMigrationSpec spec,
        CancellationToken cancellationToken = default)
    {
        var migrationId = OmConvert.RequireName(spec.MigrationId, nameof(spec.MigrationId));
        if (spec.FromVersion <= 0) throw new ArgumentException("FromVersion must be positive", nameof(spec));
        if (spec.ToVersion <= 0) throw new ArgumentException("ToVersion must be positive", nameof(spec));
        if (spec.ToVersion == spec.FromVersion) throw new ArgumentException("ToVersion must differ from FromVersion", nameof(spec));

        var state = await GetSchemaStateAsync(runtime, cancellationToken);
        if (state.CurrentVersion != spec.FromVersion)
        {
            throw new CozoException($"Schema currentVersion={state.CurrentVersion} does not match fromVersion={spec.FromVersion}");
        }

        if (await ReadSchemaSnapshotAsync(runtime, spec.FromVersion, cancellationToken) is null)
        {
            await WriteSchemaSnapshotAsync(runtime, spec.FromVersion, $"v{spec.FromVersion}", $"Snapshot before {migrationId}", cancellationToken);
        }

        var steps = spec.Steps ?? [];
        foreach (var step in steps)
        {
            await ApplyMigrationStepAsync(runtime, step, spec.Strict, cancellationToken);
        }

        var snapshot = await WriteSchemaSnapshotAsync(runtime, spec.ToVersion, spec.Label ?? $"v{spec.ToVersion}", spec.Description, cancellationToken);
        var now = runtime.Options.TimeProvider.GetUtcNow().UtcDateTime.ToString("O");
        await runtime.Store.RunAsync(
            """
            ?[version, created_at, label, description, parent_version, checksum] <- [[$version, $created_at, $label, $description, $parent_version, $checksum]]
            :put om_schema_version {version => created_at, label, description, parent_version, checksum}
            """,
            LogicSupport.Params(
                ("version", spec.ToVersion),
                ("created_at", string.IsNullOrWhiteSpace(snapshot.CreatedAt) ? now : snapshot.CreatedAt),
                ("label", spec.Label ?? $"v{spec.ToVersion}"),
                ("description", spec.Description ?? ""),
                ("parent_version", spec.FromVersion),
                ("checksum", snapshot.Checksum ?? "")),
            cancellationToken: cancellationToken);
        await runtime.Store.RunAsync(
            """
            ?[id, current_version, current_checksum] <- [["default", $current_version, $current_checksum]]
            :put om_schema_state {id => current_version, current_checksum}
            """,
            LogicSupport.Params(("current_version", spec.ToVersion), ("current_checksum", snapshot.Checksum ?? "")),
            cancellationToken: cancellationToken);
        await runtime.Store.RunAsync(
            """
            ?[migration_id, from_version, to_version, applied_at, applied_by, status, error, summary_json] <- [[$migration_id, $from_version, $to_version, $applied_at, "", "applied", "", $summary_json]]
            :put om_schema_migration {migration_id => from_version, to_version, applied_at, applied_by, status, error, summary_json}
            """,
            LogicSupport.Params(
                ("migration_id", migrationId),
                ("from_version", spec.FromVersion),
                ("to_version", spec.ToVersion),
                ("applied_at", now),
                ("summary_json", JsonSerializer.SerializeToElement(new
                {
                    migrationId,
                    fromVersion = spec.FromVersion,
                    toVersion = spec.ToVersion,
                    strict = spec.Strict,
                    stepsApplied = steps.Count
                }, OmConvert.JsonOptions))),
            cancellationToken: cancellationToken);

        return new SchemaMigrationResult(migrationId, spec.FromVersion, spec.ToVersion, steps.Count, snapshot.Checksum ?? "");
    }

    private static async Task ApplyMigrationStepAsync(
        CozoOmRuntime runtime,
        JsonElement step,
        bool strict,
        CancellationToken cancellationToken)
    {
        var kind = ReadStepString(step, "kind", "type");
        switch (kind)
        {
            case "addType":
                await TypeLogic.DefineTypeAsync(
                    runtime,
                    new DefineTypeInput(
                        ReadRequiredStepString(step, "typeName", "type_name"),
                        ReadStepString(step, "description") ?? ReadRequiredStepString(step, "typeName", "type_name"),
                        ReadStepString(step, "parentType", "parent_type")),
                    cancellationToken);
                return;
            case "addAttribute":
                await TypeLogic.DefineAttributeAsync(
                    runtime,
                    new DefineAttributeInput(
                        ReadRequiredStepString(step, "typeName", "type_name"),
                        ReadRequiredStepString(step, "attrName", "attr_name"),
                        OmConvert.StoredToValueType(ReadRequiredStepString(step, "valueType", "value_type")),
                        ReadStepBool(step, false, "required")),
                    cancellationToken);
                return;
            case "addRelation":
                await TypeLogic.DefineRelationAsync(
                    runtime,
                    new DefineRelationInput(
                        ReadRequiredStepString(step, "relName", "rel_name"),
                        ReadRequiredStepString(step, "fromType", "from_type"),
                        ReadRequiredStepString(step, "toType", "to_type"),
                        ReadStepBool(step, true, "directed")),
                    cancellationToken);
                return;
            case "renameAttribute":
                await RenameAttributeAsync(runtime, step, cancellationToken);
                return;
            case "changeAttribute":
                await ChangeAttributeAsync(runtime, step, strict, cancellationToken);
                return;
            default:
                throw new CozoException($"Unsupported migration step kind '{kind}'");
        }
    }

    private static async Task RenameAttributeAsync(CozoOmRuntime runtime, JsonElement step, CancellationToken cancellationToken)
    {
        var typeName = await TypeLogic.ResolveTypeAsync(runtime, ReadRequiredStepString(step, "typeName", "type_name"), cancellationToken);
        var fromAttr = await TypeLogic.ResolveAttrAsync(runtime, typeName, ReadRequiredStepString(step, "fromAttr", "from_attr"), cancellationToken);
        var toAttr = OmConvert.RequireName(ReadRequiredStepString(step, "toAttr", "to_attr"), "toAttr");
        var definitions = await TypeLogic.GetAttributeDefinitionsAsync(runtime, typeName, cancellationToken);
        if (!definitions.TryGetValue(fromAttr, out var definition))
        {
            throw new CozoException($"Cannot rename missing attribute '{typeName}.{fromAttr}'");
        }

        await TypeLogic.DefineAttributeAsync(runtime, new DefineAttributeInput(typeName, toAttr, definition.ValueType, definition.Required), cancellationToken);
        await TypeLogic.DefineAttributeAliasAsync(runtime, typeName, fromAttr, toAttr, cancellationToken);
        await runtime.Store.RunAsync(
            """
            ?[type_name, attr_name] <- [[$type_name, $attr_name]]
            :rm om_attr_def {type_name, attr_name}
            """,
            LogicSupport.Params(("type_name", typeName), ("attr_name", fromAttr)),
            cancellationToken: cancellationToken);
    }

    private static async Task ChangeAttributeAsync(
        CozoOmRuntime runtime,
        JsonElement step,
        bool strict,
        CancellationToken cancellationToken)
    {
        var typeName = await TypeLogic.ResolveTypeAsync(runtime, ReadRequiredStepString(step, "typeName", "type_name"), cancellationToken);
        var attrName = await TypeLogic.ResolveAttrAsync(runtime, typeName, ReadRequiredStepString(step, "attrName", "attr_name"), cancellationToken);
        var definitions = await TypeLogic.GetAttributeDefinitionsAsync(runtime, typeName, cancellationToken);
        if (!definitions.TryGetValue(attrName, out var current))
        {
            throw new CozoException($"Cannot change missing attribute '{typeName}.{attrName}'");
        }

        var valueType = OmConvert.StoredToValueType(ReadRequiredStepString(step, "valueType", "value_type"));
        var required = step.TryGetProperty("required", out _)
            ? ReadStepBool(step, false, "required")
            : current.Required;
        await TypeLogic.DefineAttributeAsync(runtime, new DefineAttributeInput(typeName, attrName, valueType, required), cancellationToken);
    }

    private static string ReadRequiredStepString(JsonElement step, params string[] names)
    {
        return OmConvert.RequireName(ReadStepString(step, names) ?? "", names[0]);
    }

    private static string? ReadStepString(JsonElement step, params string[] names)
    {
        foreach (var name in names)
        {
            if (step.TryGetProperty(name, out var value))
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            }
        }

        return null;
    }

    private static bool ReadStepBool(JsonElement step, bool defaultValue, params string[] names)
    {
        foreach (var name in names)
        {
            if (!step.TryGetProperty(name, out var value)) continue;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                _ => defaultValue
            };
        }

        return defaultValue;
    }

    private static async Task SeedSchemaStateAsync(CozoOmRuntime runtime, CancellationToken cancellationToken)
    {
        var stateRows = await runtime.Store.RunAsync(
            """
            ?[current_version] :=
              *om_schema_state{ id: "default", current_version, current_checksum: _c }
            :limit 1
            """,
            cancellationToken: cancellationToken);
        if (stateRows.Rows.Count == 0)
        {
            await runtime.Store.RunAsync(
                """
                ?[id, current_version, current_checksum] <- [["default", 1, ""]]
                :put om_schema_state {id => current_version, current_checksum}
                """,
                cancellationToken: cancellationToken);
        }

        var v1Rows = await runtime.Store.RunAsync(
            """
            ?[created_at] :=
              *om_schema_version{ version: 1, created_at, label: _l, description: _d, parent_version: _p, checksum: _c }
            :limit 1
            """,
            cancellationToken: cancellationToken);
        if (v1Rows.Rows.Count == 0)
        {
            var now = runtime.Options.TimeProvider.GetUtcNow().UtcDateTime.ToString("O");
            await runtime.Store.RunAsync(
                """
                ?[version, created_at, label, description, parent_version, checksum] <- [[1, $created_at, "Initial schema", "Initial OM schema", null, ""]]
                :put om_schema_version {version => created_at, label, description, parent_version, checksum}
                """,
                LogicSupport.Params(("created_at", now)),
                cancellationToken: cancellationToken);
        }
    }

    internal static async Task<JsonElement> ReadCurrentSchemaObjectAsync(CozoOmRuntime runtime, CancellationToken cancellationToken)
    {
        var parts = new Dictionary<string, object?>
        {
            ["om_type"] = await ReadRowsAsync(runtime, "om_type", "name, description, parent_type", cancellationToken),
            ["om_mixin"] = await ReadRowsAsync(runtime, "om_mixin", "name, description", cancellationToken),
            ["om_type_mixin"] = await ReadRowsAsync(runtime, "om_type_mixin", "type_name, mixin_name", cancellationToken),
            ["om_attr_def"] = await ReadRowsAsync(runtime, "om_attr_def", "type_name, attr_name, value_type, required", cancellationToken),
            ["om_rel_def"] = await ReadRowsAsync(runtime, "om_rel_def", "rel_name, from_type, to_type, directed", cancellationToken),
            ["om_attr_desc"] = await ReadRowsAsync(runtime, "om_attr_desc", "type_name, attr_name, description", cancellationToken),
            ["om_rel_desc"] = await ReadRowsAsync(runtime, "om_rel_desc", "rel_name, description", cancellationToken),
            ["om_constraint_def"] = await ReadRowsAsync(runtime, "om_constraint_def", "type_name, constraint_name, constraint_type, message", cancellationToken),
            ["om_computed_def"] = await ReadRowsAsync(runtime, "om_computed_def", "type_name, attr_name, description", cancellationToken),
            ["om_action_def"] = await ReadRowsAsync(runtime, "om_action_def", "type_name, action_name, description", cancellationToken),
            ["om_mutation_def"] = await ReadRowsAsync(runtime, "om_mutation_def", "type_name, mutation_name, description", cancellationToken),
            ["om_interceptor_def"] = await ReadRowsAsync(runtime, "om_interceptor_def", "type_name, action_name, phase, seq, description", cancellationToken),
            ["om_perm_action"] = await ReadRowsAsync(runtime, "om_perm_action", "action, description", cancellationToken),
            ["om_perm_policy"] = await ReadRowsAsync(runtime, "om_perm_policy", "policy_id, effect, action, resource_type, enabled, description", cancellationToken),
            ["om_perm_abac_rule"] = await ReadRowsAsync(runtime, "om_perm_abac_rule", "policy_id, left_ref, op, right_ref", cancellationToken),
            ["om_perm_path_rule"] = await ReadRowsAsync(runtime, "om_perm_path_rule", "policy_id, path", cancellationToken),
            ["om_alias_type"] = await ReadRowsAsync(runtime, "om_alias_type", "alias, canonical", cancellationToken),
            ["om_alias_rel"] = await ReadRowsAsync(runtime, "om_alias_rel", "alias, canonical", cancellationToken),
            ["om_alias_attr"] = await ReadRowsAsync(runtime, "om_alias_attr", "type_name, alias_attr, canonical_attr", cancellationToken),
            ["om_existential_rule_def"] = await ReadRowsAsync(runtime, "om_existential_rule_def", "rule_name, spec_json, mode, message, enabled", cancellationToken),
        };

        return JsonSerializer.SerializeToElement(new { schema = parts }, OmConvert.JsonOptions).Clone();
    }

    private static async Task<IReadOnlyList<IReadOnlyList<JsonElement>>> ReadRowsAsync(
        CozoOmRuntime runtime,
        string relation,
        string columns,
        CancellationToken cancellationToken)
    {
        var fieldAtoms = string.Join(", ", columns.Split(',').Select(c =>
        {
            var trimmed = c.Trim();
            return $"{trimmed}: {trimmed}";
        }));
        var result = await runtime.Store.RunAsync(
            $"?[{columns}] :=\n" +
            $"  *{relation}{{ {fieldAtoms} }}\n" +
            $":sort {columns.Split(',')[0].Trim()}",
            cancellationToken: cancellationToken);
        return result.Rows;
    }

    private static SchemaDiff DiffSnapshots(SchemaSnapshot from, SchemaSnapshot to)
    {
        var fromText = from.Schema.GetRawText();
        var toText = to.Schema.GetRawText();
        var added = fromText == toText ? LogicSupport.EmptyObject() : to.Schema.Clone();
        var removed = fromText == toText ? LogicSupport.EmptyObject() : from.Schema.Clone();
        var changed = fromText == toText ? LogicSupport.EmptyObject() : JsonSerializer.SerializeToElement(new { from = from.Version, to = to.Version }, OmConvert.JsonOptions);
        return new SchemaDiff(from.Version, to.Version, added, removed, changed);
    }

    private static string ComputeChecksum(JsonElement schema)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(schema.GetRawText()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task ClearRelationAsync(CozoOmRuntime runtime, RelationSnapshotSpec spec, CancellationToken cancellationToken)
    {
        var head = string.Join(", ", spec.KeyColumns);
        var atoms = string.Join(", ", spec.KeyColumns.Select(c => $"{c}: {c}"));
        await runtime.Store.RunAsync(
            $"?[{head}] :=\n" +
            $"  *{spec.Relation}{{ {atoms} }}\n" +
            $":rm {spec.Relation} {{{head}}}",
            cancellationToken: cancellationToken);
    }

    private sealed record RelationSnapshotSpec(string Relation, string[] Columns, string[] KeyColumns);

    private static readonly RelationSnapshotSpec[] RollbackRelations =
    [
        new("om_type", ["name", "description", "parent_type"], ["name"]),
        new("om_mixin", ["name", "description"], ["name"]),
        new("om_type_mixin", ["type_name", "mixin_name"], ["type_name", "mixin_name"]),
        new("om_attr_def", ["type_name", "attr_name", "value_type", "required"], ["type_name", "attr_name"]),
        new("om_rel_def", ["rel_name", "from_type", "to_type", "directed"], ["rel_name"]),
        new("om_attr_desc", ["type_name", "attr_name", "description"], ["type_name", "attr_name"]),
        new("om_rel_desc", ["rel_name", "description"], ["rel_name"]),
        new("om_constraint_def", ["type_name", "constraint_name", "constraint_type", "message"], ["type_name", "constraint_name"]),
        new("om_computed_def", ["type_name", "attr_name", "description"], ["type_name", "attr_name"]),
        new("om_action_def", ["type_name", "action_name", "description"], ["type_name", "action_name"]),
        new("om_mutation_def", ["type_name", "mutation_name", "description"], ["type_name", "mutation_name"]),
        new("om_interceptor_def", ["type_name", "action_name", "phase", "seq", "description"], ["type_name", "action_name", "phase", "seq"]),
        new("om_perm_action", ["action", "description"], ["action"]),
        new("om_perm_policy", ["policy_id", "effect", "action", "resource_type", "enabled", "description"], ["policy_id"]),
        new("om_perm_abac_rule", ["policy_id", "left_ref", "op", "right_ref"], ["policy_id", "left_ref", "op", "right_ref"]),
        new("om_perm_path_rule", ["policy_id", "path"], ["policy_id", "path"]),
        new("om_alias_type", ["alias", "canonical"], ["alias"]),
        new("om_alias_rel", ["alias", "canonical"], ["alias"]),
        new("om_alias_attr", ["type_name", "alias_attr", "canonical_attr"], ["type_name", "alias_attr"]),
        new("om_existential_rule_def", ["rule_name", "spec_json", "mode", "message", "enabled"], ["rule_name"]),
    ];
}
