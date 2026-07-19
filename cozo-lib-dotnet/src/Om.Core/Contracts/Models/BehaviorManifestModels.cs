using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Cozo.DotNet.Om.Contracts.Models;

public sealed record BehaviorManifestDiagnostic(string Code, string Path, string Message);

public sealed record BehaviorManifestDecodeResult
{
    public BehaviorManifestDecodeResult(
        BehaviorCatalog? catalog,
        IEnumerable<BehaviorManifestDiagnostic> diagnostics)
    {
        Catalog = catalog;
        Diagnostics = diagnostics?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public BehaviorCatalog? Catalog { get; init; }
    public ImmutableArray<BehaviorManifestDiagnostic> Diagnostics { get; init; }
    public bool Success => Catalog is not null && Diagnostics.IsEmpty;
}

public static class BehaviorManifestJsonCodec
{
    public const int CurrentVersion = 1;

    public static byte[] Encode(BehaviorCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var normalized = Normalize(catalog);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentVersion);
            writer.WritePropertyName("behaviors");
            writer.WriteStartArray();
            foreach (var behavior in normalized.Behaviors)
            {
                WriteBehavior(writer, behavior);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static BehaviorManifestDecodeResult Decode(string json)
    {
        if (json is null)
        {
            return Failure(new BehaviorManifestDiagnostic("OMM1000", "$", "Manifest JSON is required."));
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return Decode(document.RootElement);
        }
        catch (JsonException exception)
        {
            return Failure(new BehaviorManifestDiagnostic(
                "OMM1000",
                "$",
                $"Manifest JSON is malformed at line {exception.LineNumber ?? 0}, byte {exception.BytePositionInLine ?? 0}."));
        }
    }

    public static BehaviorManifestDecodeResult Decode(ReadOnlySpan<byte> utf8Json) =>
        Decode(Encoding.UTF8.GetString(utf8Json));

    private static BehaviorManifestDecodeResult Decode(JsonElement root)
    {
        var diagnostics = new List<BehaviorManifestDiagnostic>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new("OMM1001", "$", "Manifest root must be an object."));
            return Failure(diagnostics);
        }

        if (!root.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var version))
        {
            diagnostics.Add(new("OMM1001", "$.version", "Manifest version must be an integer."));
        }
        else if (version != CurrentVersion)
        {
            diagnostics.Add(new("OMM1002", "$.version", $"Manifest version '{version}' is not supported."));
        }

        var entries = new List<BehaviorCatalogEntry>();
        if (!root.TryGetProperty("behaviors", out var behaviorsElement)
            || behaviorsElement.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new("OMM1001", "$.behaviors", "Manifest behaviors must be an array."));
            return Failure(diagnostics);
        }

        var behaviorKeys = new HashSet<string>(StringComparer.Ordinal);
        var bindingKeys = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var behaviorElement in behaviorsElement.EnumerateArray())
        {
            var path = $"$.behaviors[{index}]";
            var entry = ReadBehavior(behaviorElement, path, diagnostics, bindingKeys);
            if (entry is not null)
            {
                var behaviorKey = BehaviorKey(entry);
                if (!behaviorKeys.Add(behaviorKey))
                {
                    diagnostics.Add(new(
                        "OMM1302",
                        path,
                        $"Behavior key '{DisplayBehaviorKey(entry)}' is duplicated or conflicting."));
                }

                entries.Add(entry);
            }

            index++;
        }

        if (diagnostics.Count > 0)
        {
            return Failure(diagnostics);
        }

        return new BehaviorManifestDecodeResult(Normalize(new BehaviorCatalog(entries)), []);
    }

    private static BehaviorCatalogEntry? ReadBehavior(
        JsonElement element,
        string path,
        ICollection<BehaviorManifestDiagnostic> diagnostics,
        ISet<string> bindingKeys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new("OMM1001", path, "Behavior entry must be an object."));
            return null;
        }

        var kindValue = ReadRequiredString(element, "kind", path, diagnostics);
        var kind = ParseKind(kindValue, $"{path}.kind", diagnostics);
        var ownerType = ReadRequiredString(element, "ownerType", path, diagnostics);
        var name = ReadRequiredString(element, "name", path, diagnostics);
        if (string.IsNullOrWhiteSpace(ownerType) || string.IsNullOrWhiteSpace(name))
        {
            diagnostics.Add(new("OMM1201", path, "Behavior ownerType and name must be non-empty strings."));
        }

        var constraintType = ReadOptionalString(element, "constraintType", path, diagnostics);
        var message = ReadOptionalString(element, "message", path, diagnostics);
        var description = ReadOptionalString(element, "description", path, diagnostics);
        var phase = ReadOptionalString(element, "interceptorPhase", path, diagnostics);
        var seq = ReadOptionalInt(element, "interceptorSeq", path, diagnostics);

        if (kind is not null)
        {
            ValidateMetadata(
                kind.Value,
                constraintType,
                message,
                description,
                phase,
                seq,
                path,
                diagnostics);
        }

        var callbacks = new List<BehaviorCallbackBinding>();
        if (!element.TryGetProperty("callbacks", out var callbacksElement)
            || callbacksElement.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new("OMM1001", $"{path}.callbacks", "Behavior callbacks must be an array."));
        }
        else
        {
            var callbackIndex = 0;
            foreach (var callbackElement in callbacksElement.EnumerateArray())
            {
                var callbackPath = $"{path}.callbacks[{callbackIndex}]";
                var callback = ReadCallback(callbackElement, callbackPath, diagnostics);
                if (callback is not null && kind is not null)
                {
                    ValidateSlot(kind.Value, callback.Slot, callbackPath, diagnostics);
                    var bindingKey = $"{KindWire(kind.Value)}\u001f{ownerType}\u001f{name}\u001f{phase}\u001f{seq}\u001f{SlotWire(callback.Slot)}";
                    if (!bindingKeys.Add(bindingKey))
                    {
                        diagnostics.Add(new(
                            "OMM1301",
                            callbackPath,
                            $"Callback binding key '{DisplayBehaviorKey(kind.Value, ownerType, name, phase, seq)}/{SlotWire(callback.Slot)}' is duplicated or conflicting."));
                    }

                    callbacks.Add(callback);
                }

                callbackIndex++;
            }
        }

        if (kind is null)
        {
            return null;
        }

        return new BehaviorCatalogEntry(
            kind.Value,
            ownerType ?? "",
            name ?? "",
            constraintType,
            message,
            description,
            phase,
            seq,
            callbacks);
    }

    private static BehaviorCallbackBinding? ReadCallback(
        JsonElement element,
        string path,
        ICollection<BehaviorManifestDiagnostic> diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new("OMM1001", path, "Callback entry must be an object."));
            return null;
        }

        var slotValue = ReadRequiredString(element, "slot", path, diagnostics);
        var slot = ParseSlot(slotValue, $"{path}.slot", diagnostics);
        var bindingId = ReadOptionalString(element, "bindingId", path, diagnostics);
        var readinessValue = ReadRequiredString(element, "readiness", path, diagnostics);
        var readiness = ParseReadiness(readinessValue, $"{path}.readiness", diagnostics);
        if (slot is null || readiness is null)
        {
            return null;
        }

        var bindingValid = readiness == BehaviorReadiness.Unbound
            ? bindingId is null
            : !string.IsNullOrWhiteSpace(bindingId);
        if (!bindingValid)
        {
            diagnostics.Add(new(
                "OMM1202",
                path,
                "Unbound callbacks require a null bindingId; unresolved and ready callbacks require a non-empty bindingId."));
        }

        return new BehaviorCallbackBinding(slot.Value, bindingId, readiness.Value);
    }

    private static string? ReadRequiredString(
        JsonElement element,
        string propertyName,
        string path,
        ICollection<BehaviorManifestDiagnostic> diagnostics)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        diagnostics.Add(new("OMM1001", $"{path}.{propertyName}", $"Property '{propertyName}' must be a string."));
        return null;
    }

    private static string? ReadOptionalString(
        JsonElement element,
        string propertyName,
        string path,
        ICollection<BehaviorManifestDiagnostic> diagnostics)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        diagnostics.Add(new("OMM1001", $"{path}.{propertyName}", $"Property '{propertyName}' must be a string or null."));
        return null;
    }

    private static int? ReadOptionalInt(
        JsonElement element,
        string propertyName,
        string path,
        ICollection<BehaviorManifestDiagnostic> diagnostics)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result))
        {
            return result;
        }

        diagnostics.Add(new("OMM1001", $"{path}.{propertyName}", $"Property '{propertyName}' must be an integer or null."));
        return null;
    }

    private static BehaviorCatalogKind? ParseKind(
        string? value,
        string path,
        ICollection<BehaviorManifestDiagnostic> diagnostics)
    {
        var kind = value switch
        {
            "constraint" => BehaviorCatalogKind.Constraint,
            "computed" => BehaviorCatalogKind.Computed,
            "action" => BehaviorCatalogKind.Action,
            "mutation" => BehaviorCatalogKind.Mutation,
            "interceptor" => BehaviorCatalogKind.Interceptor,
            _ => (BehaviorCatalogKind?)null,
        };
        if (kind is null && value is not null)
        {
            diagnostics.Add(new("OMM1101", path, $"Behavior kind '{value}' is unknown."));
        }

        return kind;
    }

    private static BehaviorCatalogCallbackSlot? ParseSlot(
        string? value,
        string path,
        ICollection<BehaviorManifestDiagnostic> diagnostics)
    {
        var slot = value switch
        {
            "when" => BehaviorCatalogCallbackSlot.When,
            "then" => BehaviorCatalogCallbackSlot.Then,
            "validator" => BehaviorCatalogCallbackSlot.Validator,
            "compute" => BehaviorCatalogCallbackSlot.Compute,
            "handler" => BehaviorCatalogCallbackSlot.Handler,
            "executor" => BehaviorCatalogCallbackSlot.Executor,
            _ => (BehaviorCatalogCallbackSlot?)null,
        };
        if (slot is null && value is not null)
        {
            diagnostics.Add(new("OMM1102", path, $"Callback slot '{value}' is unknown."));
        }

        return slot;
    }

    private static BehaviorReadiness? ParseReadiness(
        string? value,
        string path,
        ICollection<BehaviorManifestDiagnostic> diagnostics)
    {
        var readiness = value switch
        {
            "unbound" => BehaviorReadiness.Unbound,
            "unresolved" => BehaviorReadiness.Unresolved,
            "ready" => BehaviorReadiness.Ready,
            _ => (BehaviorReadiness?)null,
        };
        if (readiness is null && value is not null)
        {
            diagnostics.Add(new("OMM1103", path, $"Callback readiness '{value}' is unknown."));
        }

        return readiness;
    }

    private static void ValidateMetadata(
        BehaviorCatalogKind kind,
        string? constraintType,
        string? message,
        string? description,
        string? phase,
        int? seq,
        string path,
        ICollection<BehaviorManifestDiagnostic> diagnostics)
    {
        switch (kind)
        {
            case BehaviorCatalogKind.Constraint:
                RequireString(kind, "constraintType", constraintType, path, diagnostics);
                RequireNull(kind, "description", description, path, diagnostics);
                RequireNull(kind, "interceptorPhase", phase, path, diagnostics);
                RequireNull(kind, "interceptorSeq", seq, path, diagnostics);
                break;
            case BehaviorCatalogKind.Computed:
            case BehaviorCatalogKind.Action:
            case BehaviorCatalogKind.Mutation:
                RequireNull(kind, "constraintType", constraintType, path, diagnostics);
                RequireNull(kind, "message", message, path, diagnostics);
                RequireNull(kind, "interceptorPhase", phase, path, diagnostics);
                RequireNull(kind, "interceptorSeq", seq, path, diagnostics);
                break;
            case BehaviorCatalogKind.Interceptor:
                RequireNull(kind, "constraintType", constraintType, path, diagnostics);
                RequireNull(kind, "message", message, path, diagnostics);
                if (phase is not ("before" or "after") || seq is null or < 0)
                {
                    diagnostics.Add(new(
                        "OMM1201",
                        path,
                        "Interceptor keys require phase before/after and a non-negative sequence."));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown behavior kind.");
        }
    }

    private static void RequireString(
        BehaviorCatalogKind kind,
        string propertyName,
        string? value,
        string path,
        ICollection<BehaviorManifestDiagnostic> diagnostics)
    {
        if (value is null)
        {
            diagnostics.Add(new(
                "OMM1203",
                $"{path}.{propertyName}",
                $"Property '{propertyName}' must be a string for behavior kind '{KindWire(kind)}'."));
        }
    }

    private static void RequireNull(
        BehaviorCatalogKind kind,
        string propertyName,
        object? value,
        string path,
        ICollection<BehaviorManifestDiagnostic> diagnostics)
    {
        if (value is not null)
        {
            diagnostics.Add(new(
                "OMM1203",
                $"{path}.{propertyName}",
                $"Property '{propertyName}' must be null for behavior kind '{KindWire(kind)}'."));
        }
    }

    private static void ValidateSlot(
        BehaviorCatalogKind kind,
        BehaviorCatalogCallbackSlot slot,
        string path,
        ICollection<BehaviorManifestDiagnostic> diagnostics)
    {
        var valid = kind switch
        {
            BehaviorCatalogKind.Constraint => slot is BehaviorCatalogCallbackSlot.When or BehaviorCatalogCallbackSlot.Then or BehaviorCatalogCallbackSlot.Validator,
            BehaviorCatalogKind.Computed => slot == BehaviorCatalogCallbackSlot.Compute,
            BehaviorCatalogKind.Action => slot == BehaviorCatalogCallbackSlot.Handler,
            BehaviorCatalogKind.Mutation => slot == BehaviorCatalogCallbackSlot.Executor,
            BehaviorCatalogKind.Interceptor => slot == BehaviorCatalogCallbackSlot.Handler,
            _ => false,
        };
        if (!valid)
        {
            diagnostics.Add(new("OMM1202", path, $"Callback slot '{SlotWire(slot)}' is invalid for behavior kind '{KindWire(kind)}'."));
        }
    }

    private static BehaviorCatalog Normalize(BehaviorCatalog catalog) =>
        new(catalog.Behaviors
            .Select(entry => entry with
            {
                Callbacks = entry.Callbacks
                    .OrderBy(callback => callback.Slot)
                    .ThenBy(callback => callback.BindingId ?? "", StringComparer.Ordinal)
                    .ThenBy(callback => callback.Readiness)
                    .ToImmutableArray(),
            })
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.OwnerType, StringComparer.Ordinal)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.InterceptorPhase ?? "", StringComparer.Ordinal)
            .ThenBy(entry => entry.InterceptorSeq ?? -1)
            .ToArray());

    private static void WriteBehavior(Utf8JsonWriter writer, BehaviorCatalogEntry behavior)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", KindWire(behavior.Kind));
        writer.WriteString("ownerType", behavior.OwnerType);
        writer.WriteString("name", behavior.Name);
        WriteNullableString(writer, "constraintType", behavior.ConstraintType);
        WriteNullableString(writer, "message", behavior.Message);
        WriteNullableString(writer, "description", behavior.Description);
        WriteNullableString(writer, "interceptorPhase", behavior.InterceptorPhase);
        if (behavior.InterceptorSeq is int seq)
        {
            writer.WriteNumber("interceptorSeq", seq);
        }
        else
        {
            writer.WriteNull("interceptorSeq");
        }

        writer.WritePropertyName("callbacks");
        writer.WriteStartArray();
        foreach (var callback in behavior.Callbacks)
        {
            writer.WriteStartObject();
            writer.WriteString("slot", SlotWire(callback.Slot));
            WriteNullableString(writer, "bindingId", callback.BindingId);
            writer.WriteString("readiness", ReadinessWire(callback.Readiness));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static string BehaviorKey(BehaviorCatalogEntry entry) =>
        $"{KindWire(entry.Kind)}\u001f{entry.OwnerType}\u001f{entry.Name}\u001f{entry.InterceptorPhase}\u001f{entry.InterceptorSeq}";

    private static string DisplayBehaviorKey(BehaviorCatalogEntry entry) =>
        DisplayBehaviorKey(entry.Kind, entry.OwnerType, entry.Name, entry.InterceptorPhase, entry.InterceptorSeq);

    private static string DisplayBehaviorKey(BehaviorCatalogKind kind, string? owner, string? name, string? phase, int? seq) =>
        kind == BehaviorCatalogKind.Interceptor
            ? $"{KindWire(kind)}:{owner}/{name}/{phase}/{seq}"
            : $"{KindWire(kind)}:{owner}/{name}";

    private static string KindWire(BehaviorCatalogKind kind) => kind switch
    {
        BehaviorCatalogKind.Constraint => "constraint",
        BehaviorCatalogKind.Computed => "computed",
        BehaviorCatalogKind.Action => "action",
        BehaviorCatalogKind.Mutation => "mutation",
        BehaviorCatalogKind.Interceptor => "interceptor",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown behavior kind."),
    };

    private static string SlotWire(BehaviorCatalogCallbackSlot slot) => slot switch
    {
        BehaviorCatalogCallbackSlot.When => "when",
        BehaviorCatalogCallbackSlot.Then => "then",
        BehaviorCatalogCallbackSlot.Validator => "validator",
        BehaviorCatalogCallbackSlot.Compute => "compute",
        BehaviorCatalogCallbackSlot.Handler => "handler",
        BehaviorCatalogCallbackSlot.Executor => "executor",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown callback slot."),
    };

    private static string ReadinessWire(BehaviorReadiness readiness) => readiness switch
    {
        BehaviorReadiness.Unbound => "unbound",
        BehaviorReadiness.Unresolved => "unresolved",
        BehaviorReadiness.Ready => "ready",
        _ => throw new ArgumentOutOfRangeException(nameof(readiness), readiness, "Unknown readiness."),
    };

    private static BehaviorManifestDecodeResult Failure(BehaviorManifestDiagnostic diagnostic) =>
        Failure([diagnostic]);

    private static BehaviorManifestDecodeResult Failure(IEnumerable<BehaviorManifestDiagnostic> diagnostics) =>
        new(null, diagnostics
            .OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Path, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToArray());
}
