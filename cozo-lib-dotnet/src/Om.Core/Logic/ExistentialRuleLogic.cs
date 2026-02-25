using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;
using Cozo.DotNet.Om.Internals;
using Cozo.DotNet.Om.Runtime;
using Cozo.DotNet.Om.Support;

namespace Cozo.DotNet.Om.Logic;

public static class ExistentialRuleLogic
{
    private const string SkolemOriginAttr = "_skolem_rule";

    public static async Task<ExistentialRule> DefineExistentialRuleAsync(
        CozoOmRuntime runtime,
        DefineExistentialRuleInput input,
        CancellationToken cancellationToken = default)
    {
        var ruleName = OmConvert.RequireName(input.RuleName, nameof(input.RuleName));
        var normalized = await NormalizeSpecAsync(runtime, input.Spec, cancellationToken);
        var mode = normalized.Mode == ExistentialRuleMode.Materialize ? "materialize" : "check";
        var specJson = JsonSerializer.Serialize(new
        {
            forEach = new
            {
                type = normalized.ForEach.Type,
                where = normalized.ForEach.Where
            },
            exists = new
            {
                rel = normalized.Exists.Rel,
                direction = normalized.Exists.Direction == ExistentialDirection.In ? "in" : "out",
                toType = normalized.Exists.ToType
            },
            materialize = normalized.Materialize
        }, OmConvert.JsonOptions);

        if (normalized.Mode == ExistentialRuleMode.Materialize)
        {
            await TypeLogic.DefineAttributeAsync(
                runtime,
                new DefineAttributeInput(normalized.Exists.ToType, SkolemOriginAttr, OmValueType.String, false, "Skolem origin rule"),
                cancellationToken);
        }

        await runtime.Store.RunAsync(
            CozoScriptBuilder.InputPut("om_existential_rule_def", ["rule_name"], ["spec_json", "mode", "message", "enabled"]),
            LogicSupport.Params(
                ("rule_name", ruleName),
                ("spec_json", specJson),
                ("mode", mode),
                ("message", normalized.Message),
                ("enabled", normalized.Enabled)),
            cancellationToken: cancellationToken);

        return ToRule(ruleName, normalized);
    }

    public static async Task<IReadOnlyList<ExistentialRule>> ListExistentialRulesAsync(CozoOmRuntime runtime, CancellationToken cancellationToken = default)
    {
        var result = await runtime.Store.RunAsync(
            """
            ?[rule_name, spec_json, mode, message, enabled] :=
              *om_existential_rule_def{ rule_name, spec_json, mode, message, enabled }
            :sort rule_name
            """,
            cancellationToken: cancellationToken);
        return result.Rows.Select(ParseRuleRow).Where(r => r is not null).Cast<ExistentialRule>().ToArray();
    }

    public static async Task<IReadOnlyList<ExistentialViolation>> CheckExistentialRulesAsync(
        CozoOmRuntime runtime,
        CheckExistentialRulesInput? input = null,
        CancellationToken cancellationToken = default)
    {
        var wanted = input?.Rules is { Count: > 0 } ? new HashSet<string>(input.Rules, StringComparer.Ordinal) : null;
        var asOf = string.IsNullOrWhiteSpace(input?.AsOf) ? null : OmConvert.NormalizeTimestamp(input!.AsOf!, nameof(input.AsOf));
        var output = new List<ExistentialViolation>();
        foreach (var rule in await ListExistentialRulesAsync(runtime, cancellationToken))
        {
            if (!rule.Enabled) continue;
            if (wanted is not null && !wanted.Contains(rule.RuleName)) continue;
            output.AddRange(await FindViolationsAsync(runtime, rule, asOf, cancellationToken));
        }

        return output.OrderBy(v => v.Rule, StringComparer.Ordinal).ThenBy(v => v.EntityId, StringComparer.Ordinal).ToArray();
    }

    public static async Task<ExistentialChaseResult> ApplyExistentialRulesAsync(
        CozoOmRuntime runtime,
        ApplyExistentialRulesInput? input = null,
        CancellationToken cancellationToken = default)
    {
        var maxIterations = input?.MaxIterations ?? runtime.Options.DefaultMaxChaseIterations;
        if (maxIterations < 1) throw new ArgumentOutOfRangeException(nameof(input.MaxIterations), "maxIterations must be positive");
        var wanted = input?.Rules is { Count: > 0 } ? new HashSet<string>(input.Rules, StringComparer.Ordinal) : null;
        var rules = (await ListExistentialRulesAsync(runtime, cancellationToken))
            .Where(r => r.Enabled && r.Mode == ExistentialRuleMode.Materialize)
            .Where(r => wanted is null || wanted.Contains(r.RuleName))
            .ToArray();
        var created = new List<ExistentialCreated>();
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var iterations = 0;

        for (var iter = 1; iter <= maxIterations; iter++)
        {
            iterations = iter;
            var roundCreated = 0;
            foreach (var rule in rules)
            {
                var violations = await FindViolationsAsync(runtime, rule, asOf: null, cancellationToken);
                foreach (var violation in violations)
                {
                    var attemptKey = $"{rule.RuleName}\u0001{violation.EntityId}";
                    if (!attempted.Add(attemptKey)) continue;
                    var skolemId = OmConvert.SkolemId(rule.RuleName, violation.EntityId);
                    var label = SkolemLabel(rule, violation.EntityId);
                    await EntityLogic.UpsertEntityAsync(runtime, new EntityInput(skolemId, rule.Exists.ToType, label), cancellationToken);
                    await EntityLogic.SetPropertyAsync(
                        runtime,
                        new SetPropertyInput(skolemId, SkolemOriginAttr, rule.RuleName, new WriteOptions(SkipConstraints: true, ValidTime: input?.ValidTime)),
                        cancellationToken);

                    foreach (var prop in rule.Materialize?.Props ?? new Dictionary<string, JsonElement>())
                    {
                        await EntityLogic.SetPropertyAsync(
                            runtime,
                            new SetPropertyInput(skolemId, prop.Key, prop.Value, new WriteOptions(SkipConstraints: true, ValidTime: input?.ValidTime)),
                            cancellationToken);
                    }

                    var link = rule.Exists.Direction == ExistentialDirection.In
                        ? new LinkEntitiesInput(skolemId, rule.Exists.Rel, violation.EntityId, new Dictionary<string, object?>(), new WriteOptions(SkipConstraints: true, ValidTime: input?.ValidTime))
                        : new LinkEntitiesInput(violation.EntityId, rule.Exists.Rel, skolemId, new Dictionary<string, object?>(), new WriteOptions(SkipConstraints: true, ValidTime: input?.ValidTime));
                    await RelationLogic.LinkEntitiesAsync(runtime, link, cancellationToken);
                    created.Add(new ExistentialCreated(rule.RuleName, violation.EntityId, skolemId, rule.Exists.Rel, rule.Exists.ToType));
                    roundCreated++;
                }
            }

            if (roundCreated == 0) break;
        }

        var diagnostics = new List<ExistentialDiagnostic>();
        foreach (var rule in rules)
        {
            var remaining = await FindViolationsAsync(runtime, rule, asOf: null, cancellationToken);
            if (remaining.Count > 0) diagnostics.Add(new ExistentialDiagnostic(rule.RuleName, remaining.Count));
        }

        return new ExistentialChaseResult(created, iterations, diagnostics.Count == 0, diagnostics);
    }

    private static async Task<ExistentialRuleSpec> NormalizeSpecAsync(CozoOmRuntime runtime, ExistentialRuleSpec spec, CancellationToken cancellationToken)
    {
        var bodyType = await TypeLogic.ResolveTypeAsync(runtime, spec.ForEach.Type, cancellationToken);
        if (!await TypeLogic.TypeExistsAsync(runtime, bodyType, cancellationToken))
        {
            throw new CozoException($"Unknown type '{bodyType}' in forEach.type");
        }

        var rel = await TypeLogic.ResolveRelAsync(runtime, spec.Exists.Rel, cancellationToken);
        _ = await TypeLogic.GetRelationDefinitionAsync(runtime, rel, cancellationToken);
        var toType = await TypeLogic.ResolveTypeAsync(runtime, spec.Exists.ToType, cancellationToken);
        if (!await TypeLogic.TypeExistsAsync(runtime, toType, cancellationToken))
        {
            throw new CozoException($"Unknown type '{toType}' in exists.toType");
        }

        var where = new List<ExistentialWhereCondition>();
        foreach (var condition in spec.ForEach.Where ?? [])
        {
            var attr = await TypeLogic.ResolveAttrAsync(runtime, bodyType, condition.Attr, cancellationToken);
            where.Add(condition with { Attr = attr });
        }

        return spec with
        {
            ForEach = new ExistentialForEachSpec(bodyType, where),
            Exists = new ExistentialExistsSpec(rel, spec.Exists.Direction, toType),
            Message = spec.Message ?? "",
            Enabled = spec.Enabled
        };
    }

    private static async Task<IReadOnlyList<ExistentialViolation>> FindViolationsAsync(
        CozoOmRuntime runtime,
        ExistentialRule rule,
        string? asOf,
        CancellationToken cancellationToken)
    {
        var bodyTypes = new[] { rule.ForEach.Type }
            .Concat(await TypeLogic.GetDescendantsAsync(runtime, rule.ForEach.Type, cancellationToken))
            .ToArray();
        var toTypes = new[] { rule.Exists.ToType }
            .Concat(await TypeLogic.GetDescendantsAsync(runtime, rule.Exists.ToType, cancellationToken))
            .ToArray();
        var at = asOf is null ? "\"NOW\"" : "$as_of";
        var edgeAtom = rule.Exists.Direction == ExistentialDirection.In
            ? $"*om_edge{{ from_id: other_id, rel_name: rn, to_id: id, props: _p @ {at} }}"
            : $"*om_edge{{ from_id: id, rel_name: rn, to_id: other_id, props: _p @ {at} }}";
        var parameters = LogicSupport.Params(("rel_name", rule.Exists.Rel), ("to_types", toTypes));
        if (asOf is not null) parameters["as_of"] = asOf;

        var whereAtoms = new List<string>();
        var index = 0;
        foreach (var condition in rule.ForEach.Where ?? [])
        {
            var op = condition.Op switch
            {
                "=" => "==",
                "!=" => "!=",
                ">" => ">",
                ">=" => ">=",
                "<" => "<",
                "<=" => "<=",
                _ => throw new CozoException($"Unsupported existential where op '{condition.Op}'")
            };
            parameters[$"w_attr_{index}"] = condition.Attr;
            parameters[$"w_value_{index}"] = JsonSerializer.Deserialize<object?>(condition.Value.GetRawText(), OmConvert.JsonOptions);
            whereAtoms.Add($"*om_property{{ entity_id: id, attr_name: $w_attr_{index}, value: w_val_{index} @ {at} }}");
            whereAtoms.Add($"w_val_{index} {op} $w_value_{index}");
            index++;
        }

        var where = whereAtoms.Count == 0 ? "" : ",\n  " + string.Join(",\n  ", whereAtoms);
        var script =
            $"sat[id] := {edgeAtom}, rn = $rel_name,\n" +
            "  *om_entity{ id: other_id, type_name: other_type, label: _other_label },\n" +
            "  is_in(other_type, $to_types)\n" +
            "?[id] := *om_entity{ id, type_name: $type_name, label: _label }" + where + ",\n" +
            "  not sat[id]\n" +
            ":sort id";

        var violations = new List<ExistentialViolation>();
        foreach (var bodyType in bodyTypes)
        {
            parameters["type_name"] = bodyType;
            var rows = await runtime.Store.RunAsync(script, parameters, cancellationToken: cancellationToken);
            violations.AddRange(rows.Rows.Select(row => new ExistentialViolation(rule.RuleName, JsonRows.StringAt(row, 0) ?? "", rule.Message)));
        }

        return violations;
    }

    private static string SkolemLabel(ExistentialRule rule, string triggerEntityId)
    {
        var template = rule.Materialize?.LabelTemplate;
        return string.IsNullOrWhiteSpace(template)
            ? $"skolem:{rule.RuleName}:{triggerEntityId}"
            : template!.Replace("{fromId}", triggerEntityId, StringComparison.Ordinal).Replace("{rule}", rule.RuleName, StringComparison.Ordinal);
    }

    private static ExistentialRule ToRule(string ruleName, ExistentialRuleSpec spec)
    {
        return new ExistentialRule(ruleName, spec.ForEach, spec.Exists, spec.Materialize, spec.Mode, spec.Message, spec.Enabled);
    }

    private static ExistentialRule? ParseRuleRow(IReadOnlyList<JsonElement> row)
    {
        var ruleName = JsonRows.StringAt(row, 0) ?? "";
        var raw = JsonRows.StringAt(row, 1) ?? "{}";
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var forEachRoot = root.GetProperty("forEach");
        var existsRoot = root.GetProperty("exists");
        var where = forEachRoot.TryGetProperty("where", out var whereElement) && whereElement.ValueKind == JsonValueKind.Array
            ? whereElement.EnumerateArray()
                .Select(e => new ExistentialWhereCondition(
                    e.GetProperty("attr").GetString() ?? "",
                    e.TryGetProperty("op", out var op) ? op.GetString() ?? "=" : "=",
                    e.GetProperty("value").Clone()))
                .ToArray()
            : [];
        var direction = existsRoot.TryGetProperty("direction", out var directionElement) &&
                        string.Equals(directionElement.GetString(), "in", StringComparison.OrdinalIgnoreCase)
            ? ExistentialDirection.In
            : ExistentialDirection.Out;
        ExistentialMaterializeSpec? materialize = null;
        if (root.TryGetProperty("materialize", out var mat) && mat.ValueKind == JsonValueKind.Object)
        {
            IReadOnlyDictionary<string, JsonElement>? props = null;
            if (mat.TryGetProperty("props", out var propsElement) && propsElement.ValueKind == JsonValueKind.Object)
            {
                props = propsElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            }

            materialize = new ExistentialMaterializeSpec(
                mat.TryGetProperty("labelTemplate", out var label) ? label.GetString() : null,
                props);
        }

        var mode = string.Equals(JsonRows.StringAt(row, 2), "materialize", StringComparison.OrdinalIgnoreCase)
            ? ExistentialRuleMode.Materialize
            : ExistentialRuleMode.Check;
        return new ExistentialRule(
            ruleName,
            new ExistentialForEachSpec(forEachRoot.GetProperty("type").GetString() ?? "", where),
            new ExistentialExistsSpec(existsRoot.GetProperty("rel").GetString() ?? "", direction, existsRoot.GetProperty("toType").GetString() ?? ""),
            materialize,
            mode,
            JsonRows.StringAt(row, 3) ?? "",
            JsonRows.BoolAt(row, 4));
    }
}
