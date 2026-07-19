using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cozo.DotNet.Om.Contracts.Models;

namespace Cozo.DotNet.Om.Internals;

internal static class OmConvert
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static string RequireName(string value, string fieldName)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"{fieldName} is required", fieldName);
        }

        return trimmed;
    }

    internal static string ValueTypeToStored(OmValueType valueType) => valueType switch
    {
        OmValueType.String => "String",
        OmValueType.Number => "Number",
        OmValueType.Bool => "Bool",
        OmValueType.Json => "Json",
        OmValueType.Validity => "Validity",
        _ => "Unknown",
    };

    internal static OmValueType StoredToValueType(string valueType) => valueType switch
    {
        "String" => OmValueType.String,
        "Number" => OmValueType.Number,
        "Bool" => OmValueType.Bool,
        "Json" => OmValueType.Json,
        "Validity" => OmValueType.Validity,
        _ => OmValueType.Unknown,
    };

    internal static OmValueType InferValueType(object? value)
    {
        // Match Bun's public inference contract: JavaScript null is unknown,
        // while object-shaped values are Json.
        if (value is null) return OmValueType.Unknown;
        if (value is string) return OmValueType.String;
        if (value is bool) return OmValueType.Bool;
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal) return OmValueType.Number;
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => OmValueType.String,
                JsonValueKind.Number => OmValueType.Number,
                JsonValueKind.True or JsonValueKind.False => OmValueType.Bool,
                JsonValueKind.Null or JsonValueKind.Undefined => OmValueType.Unknown,
                _ => OmValueType.Json,
            };
        }

        return OmValueType.Json;
    }

    internal static OmValidityInput? NormalizeValidityInput(object? value, DateTimeOffset now)
    {
        if (value is JsonElement element)
        {
            value = JsonElementToValidityInput(element);
        }

        if (value is string rawString)
        {
            var raw = rawString.Trim();
            if (raw.Length == 0) return null;
            if (raw == "ASSERT" || raw == "RETRACT")
            {
                return new OmValidityInput(ToUnixMicroseconds(now), raw == "ASSERT");
            }

            var isAssert = !raw.StartsWith('~');
            var timestamp = isAssert ? raw : raw[1..];
            if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            {
                return null;
            }

            return new OmValidityInput(ToUnixMicroseconds(dto.ToUniversalTime()), isAssert);
        }

        if (value is object?[] array && array.Length == 2)
        {
            return NormalizeValidityPair(array[0], array[1]);
        }

        if (value is IReadOnlyList<object?> list && list.Count == 2)
        {
            return NormalizeValidityPair(list[0], list[1]);
        }

        return null;
    }

    internal static JsonElement CloneToElement(object? value)
    {
        if (value is JsonElement element) return element.Clone();
        return JsonSerializer.SerializeToElement(value, JsonOptions).Clone();
    }

    internal static object? NormalizeParamValue(object? value)
    {
        if (value is JsonElement element)
        {
            return JsonSerializer.Deserialize<object?>(element.GetRawText(), JsonOptions);
        }

        return value;
    }

    internal static string NormalizeTimestamp(string value, string fieldName)
    {
        var raw = RequireName(value, fieldName);
        if (raw.Length == 7 && raw[4] == '-')
        {
            raw += "-01T00:00:00Z";
        }
        else if (raw.Length == 10 && raw[4] == '-' && raw[7] == '-')
        {
            raw += "T00:00:00Z";
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            throw new ArgumentException($"{fieldName} must be a timestamp string (RFC3339 or YYYY-MM[/DD])", fieldName);
        }

        return dto.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    internal static string SkolemId(string ruleName, string entityId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{ruleName}|{entityId}"));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"skolem:{hex[..16]}";
    }

    internal static string StableJson(object value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static object? JsonElementToValidityInput(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 2)
        {
            return element.EnumerateArray().Select(item => item.ValueKind switch
            {
                JsonValueKind.Number when item.TryGetInt64(out var longValue) => (object?)longValue,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }).ToArray();
        }

        return null;
    }

    private static OmValidityInput? NormalizeValidityPair(object? timestamp, object? isAssert)
    {
        var tsUs = timestamp switch
        {
            byte value => value,
            sbyte value => value,
            short value => value,
            ushort value => value,
            int value => value,
            uint value => value,
            long value => value,
            ulong value when value <= long.MaxValue => (long)value,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var value) => value,
            _ => (long?)null,
        };
        var assert = isAssert switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => (bool?)null,
        };

        return tsUs is null || assert is null ? null : new OmValidityInput(tsUs.Value, assert.Value);
    }

    private static long ToUnixMicroseconds(DateTimeOffset value)
    {
        return value.ToUnixTimeMilliseconds() * 1000;
    }
}

internal sealed record OmValidityInput(long TimestampMicroseconds, bool IsAssert);
