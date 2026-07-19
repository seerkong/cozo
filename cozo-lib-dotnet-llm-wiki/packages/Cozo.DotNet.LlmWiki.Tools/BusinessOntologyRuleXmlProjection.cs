using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace Cozo.DotNet.LlmWiki.Tools;

internal sealed record BusinessOntologyRuleXmlProjection(
    string DslRuleKind,
    string PredicateName,
    string Property,
    string? CompareOperator = null,
    string? Value = null)
{
    public XElement ToXml()
    {
        var element = new XElement(
            PredicateName,
            new XAttribute("property", Property));
        if (CompareOperator is not null)
        {
            element.Add(new XAttribute("op", CompareOperator));
        }
        if (Value is not null)
        {
            element.Add(new XAttribute("value", Value));
        }
        return element;
    }
}

internal sealed record BusinessOntologyUnsupportedRuleProjection(
    string ReasonCode,
    string Message);

/// <summary>
/// The single semantic boundary between persisted ontology rules and the XML DSL predicate
/// vocabulary. Unsupported source semantics are rejected instead of being weakened.
/// </summary>
internal static class BusinessOntologyRuleXmlProjector
{
    private static readonly HashSet<string> DslRuleKinds = new(
        [
            "Conditional",
            "CrossEntity",
            "ComputedDependency",
            "Existential",
            "Uniqueness",
            "Cardinality",
            "Custom",
        ],
        StringComparer.Ordinal);

    public static bool TryProject(
        string ruleKind,
        string predicateJson,
        out BusinessOntologyRuleXmlProjection? projection,
        out BusinessOntologyUnsupportedRuleProjection? unsupported)
    {
        projection = null;
        unsupported = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(predicateJson);
        }
        catch (JsonException)
        {
            unsupported = Unsupported(
                "predicate_json_invalid",
                "规则 predicate 不是有效 JSON，无法投影到 XML DSL。");
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryString(root, "property", out var property))
            {
                unsupported = Unsupported(
                    "property_predicate_required",
                    "规则必须提供明确的 property predicate 才能投影到 XML DSL。");
                return false;
            }

            var semanticKind = NormalizeSemanticKind(ruleKind);
            var sourceOperator = TryString(root, "operator", out var value)
                ? value
                : "";

            switch (semanticKind)
            {
                case "required":
                    if (sourceOperator.Length > 0 && sourceOperator != "present")
                    {
                        unsupported = Unsupported(
                            "required_operator_unsupported",
                            $"required 规则不能使用 operator '{sourceOperator}'。");
                        return false;
                    }
                    projection = new(
                        "Conditional",
                        "PropertyPresent",
                        property);
                    return true;

                case "unique":
                    if (sourceOperator.Length > 0 && sourceOperator != "unique")
                    {
                        unsupported = Unsupported(
                            "unique_operator_unsupported",
                            $"unique 规则不能使用 operator '{sourceOperator}'。");
                        return false;
                    }
                    projection = new(
                        "Uniqueness",
                        "PropertyPresent",
                        property);
                    return true;

                case "min":
                case "max":
                    if (sourceOperator is "minLength" or "maxLength")
                    {
                        unsupported = Unsupported(
                            "property_length_predicate_unsupported",
                            "当前 XML DSL 没有字符串或集合长度 predicate，不能把 minLength/maxLength 降级为数值比较或属性存在。");
                        return false;
                    }
                    if (sourceOperator != semanticKind)
                    {
                        unsupported = Unsupported(
                            "numeric_operator_unsupported",
                            $"{semanticKind} 规则需要同名数值 operator。");
                        return false;
                    }
                    if (!TryNumeric(root, out var numeric))
                    {
                        unsupported = Unsupported(
                            "numeric_value_required",
                            $"{semanticKind} 规则需要可机械解释的数值 value。");
                        return false;
                    }
                    projection = new(
                        "Conditional",
                        "PropertyCompare",
                        property,
                        semanticKind == "min" ? "gte" : "lte",
                        numeric);
                    return true;

                case "pattern":
                    unsupported = Unsupported(
                        "pattern_predicate_unsupported",
                        "当前 XML DSL 没有正则或 pattern predicate，不能把格式约束降级为属性存在。");
                    return false;
            }

            if (!DslRuleKinds.Contains(ruleKind))
            {
                unsupported = Unsupported(
                    "rule_kind_unsupported",
                    $"规则 kind '{ruleKind}' 无法投影到 XML DSL。");
                return false;
            }

            if (TryString(root, "equals", out var equals))
            {
                projection = new(
                    ruleKind,
                    "PropertyEquals",
                    property,
                    Value: equals);
                return true;
            }
            if (sourceOperator.Length == 0 || sourceOperator == "present")
            {
                projection = new(
                    ruleKind,
                    "PropertyPresent",
                    property);
                return true;
            }

            unsupported = Unsupported(
                "legacy_predicate_unsupported",
                $"规则 operator '{sourceOperator}' 不属于当前 XML DSL 可机械表达的 predicate。");
            return false;
        }
    }

    public static BusinessOntologyRuleXmlProjection Project(
        string ruleKind,
        string predicateJson)
    {
        if (TryProject(ruleKind, predicateJson, out var projection, out var unsupported))
        {
            return projection!;
        }
        throw new InvalidOperationException(
            $"Business ontology rule cannot be exported without semantic loss ({unsupported!.ReasonCode}): {unsupported.Message}");
    }

    private static string NormalizeSemanticKind(string value) => value switch
    {
        "required" => "required",
        "min" => "min",
        "max" => "max",
        "pattern" => "pattern",
        "unique" => "unique",
        _ => "",
    };

    private static bool TryString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = "";
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString() ?? "").Length > 0;
    }

    private static bool TryNumeric(JsonElement element, out string value)
    {
        value = "";
        if (!element.TryGetProperty("value", out var candidate))
        {
            return false;
        }

        var text = candidate.ValueKind switch
        {
            JsonValueKind.Number => candidate.GetRawText(),
            JsonValueKind.String => candidate.GetString() ?? "",
            _ => "",
        };
        if (!decimal.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return false;
        }
        value = number.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static BusinessOntologyUnsupportedRuleProjection Unsupported(
        string reasonCode,
        string message) =>
        new(reasonCode, message);
}
