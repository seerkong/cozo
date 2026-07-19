using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cozo.DotNet.Om.Contracts.Models;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Cozo.DotNet.Om.Portability.Yaml;

public sealed record BehaviorYamlOptions(
    int MaxUtf8Bytes = 1_048_576,
    int MaxDepth = 64,
    int MaxNodes = 100_000)
{
    public const int MaximumUtf8Bytes = 16_777_216;
    public const int MaximumDepth = 128;
    public const int MaximumNodes = 1_000_000;
}

public static class BehaviorManifestYamlAdapter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static BehaviorManifestDecodeResult Decode(
        string yaml,
        BehaviorYamlOptions? options = null)
    {
        if (yaml is null)
        {
            return Failure("OMY1000", "$", "Manifest YAML is required.");
        }

        try
        {
            var utf8Bytes = StrictUtf8.GetByteCount(yaml);
            return DecodeCore(yaml, utf8Bytes, options ?? new BehaviorYamlOptions());
        }
        catch (EncoderFallbackException)
        {
            return Failure("OMY1000", "$", "Manifest YAML must contain valid UTF-8 text.");
        }
    }

    public static BehaviorManifestDecodeResult Decode(
        ReadOnlySpan<byte> utf8Yaml,
        BehaviorYamlOptions? options = null)
    {
        string yaml;
        try
        {
            yaml = StrictUtf8.GetString(utf8Yaml);
        }
        catch (DecoderFallbackException)
        {
            return Failure("OMY1000", "$", "Manifest YAML must be valid UTF-8.");
        }

        return DecodeCore(yaml, utf8Yaml.Length, options ?? new BehaviorYamlOptions());
    }

    public static async Task<BehaviorImportResult> ImportAsync(
        CozoOm om,
        string yaml,
        BehaviorCallbackBindingSet? callbacks = null,
        BehaviorImportOptions? importOptions = null,
        BehaviorYamlOptions? yamlOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        cancellationToken.ThrowIfCancellationRequested();
        var decoded = Decode(yaml, yamlOptions);
        if (!decoded.Success)
        {
            return new BehaviorImportResult(false, decoded.Diagnostics.Select(ToImportDiagnostic));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var canonicalJson = StrictUtf8.GetString(BehaviorManifestJsonCodec.Encode(decoded.Catalog!));
        return await om.ImportBehaviorManifestJsonAsync(
            canonicalJson,
            callbacks,
            importOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private static BehaviorManifestDecodeResult DecodeCore(
        string yaml,
        int utf8Bytes,
        BehaviorYamlOptions options)
    {
        if (!ValidOptions(options))
        {
            return Failure(
                "OMY1001",
                "$",
                $"YAML limits must be positive and no greater than {BehaviorYamlOptions.MaximumUtf8Bytes} bytes, depth {BehaviorYamlOptions.MaximumDepth}, and {BehaviorYamlOptions.MaximumNodes} nodes.");
        }

        if (utf8Bytes > options.MaxUtf8Bytes)
        {
            return Failure(
                "OMY1101",
                "$",
                $"Manifest YAML exceeds the configured UTF-8 byte limit of {options.MaxUtf8Bytes}.");
        }

        try
        {
            var parser = new Parser(new StringReader(yaml));
            parser.Consume<StreamStart>();
            parser.Consume<DocumentStart>();
            var state = new ParseState(options);
            var root = ReadNode(parser, state, "$", 1);
            parser.Consume<DocumentEnd>();
            if (parser.Accept<DocumentStart>(out _))
            {
                return Failure("OMY1201", "$", "Manifest YAML must contain exactly one document.");
            }

            parser.Consume<StreamEnd>();
            var json = WriteJson(root);
            return BehaviorManifestJsonCodec.Decode(json);
        }
        catch (YamlAdapterException exception)
        {
            return Failure(exception.Code, exception.Path, exception.Message);
        }
        catch (YamlException exception)
        {
            return Failure(
                "OMY1000",
                "$",
                $"Manifest YAML is malformed at line {exception.Start.Line}, column {exception.Start.Column}.");
        }
    }

    private static YamlValue ReadNode(IParser parser, ParseState state, string path, int depth)
    {
        if (depth > state.Options.MaxDepth)
        {
            throw new YamlAdapterException(
                "OMY1102",
                path,
                $"Manifest YAML exceeds the configured depth limit of {state.Options.MaxDepth}.");
        }

        state.Nodes++;
        if (state.Nodes > state.Options.MaxNodes)
        {
            throw new YamlAdapterException(
                "OMY1103",
                path,
                $"Manifest YAML exceeds the configured node limit of {state.Options.MaxNodes}.");
        }

        if (parser.Current is AnchorAlias)
        {
            throw new YamlAdapterException("OMY1203", path, "YAML aliases are not supported.");
        }

        if (parser.Current is not NodeEvent nodeEvent)
        {
            throw new YamlAdapterException("OMY1000", path, "Manifest YAML contains an unexpected parser event.");
        }

        if (!nodeEvent.Anchor.IsEmpty)
        {
            throw new YamlAdapterException("OMY1203", path, "YAML anchors are not supported.");
        }

        if (!nodeEvent.Tag.IsEmpty)
        {
            throw new YamlAdapterException("OMY1202", path, "Explicit YAML tags are not supported.");
        }

        return nodeEvent switch
        {
            Scalar => ReadScalar(parser),
            SequenceStart => ReadSequence(parser, state, path, depth),
            MappingStart => ReadMapping(parser, state, path, depth),
            _ => throw new YamlAdapterException("OMY1000", path, "Manifest YAML contains an unsupported node."),
        };
    }

    private static YamlValue ReadScalar(IParser parser)
    {
        var scalar = parser.Consume<Scalar>();
        if (scalar.Style != ScalarStyle.Plain)
        {
            return new YamlScalar(YamlScalarKind.String, scalar.Value);
        }

        if (scalar.Value is "~" or "null" or "Null" or "NULL" or "")
        {
            return new YamlScalar(YamlScalarKind.Null, null);
        }

        if (string.Equals(scalar.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return new YamlScalar(YamlScalarKind.Boolean, true);
        }

        if (string.Equals(scalar.Value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return new YamlScalar(YamlScalarKind.Boolean, false);
        }

        if (long.TryParse(scalar.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return new YamlScalar(YamlScalarKind.Integer, integer);
        }

        return new YamlScalar(YamlScalarKind.String, scalar.Value);
    }

    private static YamlSequence ReadSequence(
        IParser parser,
        ParseState state,
        string path,
        int depth)
    {
        parser.Consume<SequenceStart>();
        var values = new List<YamlValue>();
        while (!parser.Accept<SequenceEnd>(out _))
        {
            values.Add(ReadNode(parser, state, $"{path}[{values.Count}]", depth + 1));
        }

        parser.Consume<SequenceEnd>();
        return new YamlSequence(values.ToImmutableArray());
    }

    private static YamlMapping ReadMapping(
        IParser parser,
        ParseState state,
        string path,
        int depth)
    {
        parser.Consume<MappingStart>();
        var values = ImmutableArray.CreateBuilder<KeyValuePair<string, YamlValue>>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (!parser.Accept<MappingEnd>(out _))
        {
            var keyPath = $"{path}{{key}}";
            var keyNode = ReadNode(parser, state, keyPath, depth + 1);
            if (keyNode is not YamlScalar { Kind: YamlScalarKind.String, Value: string key })
            {
                throw new YamlAdapterException("OMY1204", keyPath, "YAML mapping keys must be strings.");
            }

            if (key == "<<")
            {
                throw new YamlAdapterException("OMY1205", ChildPath(path, key), "YAML merge keys are not supported.");
            }

            if (!keys.Add(key))
            {
                throw new YamlAdapterException("OMY1206", ChildPath(path, key), $"YAML mapping key '{key}' is duplicated.");
            }

            values.Add(new(key, ReadNode(parser, state, ChildPath(path, key), depth + 1)));
        }

        parser.Consume<MappingEnd>();
        return new YamlMapping(values.ToImmutable());
    }

    private static byte[] WriteJson(YamlValue root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteValue(writer, root);
        }

        return stream.ToArray();
    }

    private static void WriteValue(Utf8JsonWriter writer, YamlValue value)
    {
        switch (value)
        {
            case YamlScalar { Kind: YamlScalarKind.Null }:
                writer.WriteNullValue();
                break;
            case YamlScalar { Kind: YamlScalarKind.String, Value: string text }:
                writer.WriteStringValue(text);
                break;
            case YamlScalar { Kind: YamlScalarKind.Integer, Value: long integer }:
                writer.WriteNumberValue(integer);
                break;
            case YamlScalar { Kind: YamlScalarKind.Boolean, Value: bool boolean }:
                writer.WriteBooleanValue(boolean);
                break;
            case YamlSequence sequence:
                writer.WriteStartArray();
                foreach (var item in sequence.Values)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case YamlMapping mapping:
                writer.WriteStartObject();
                foreach (var pair in mapping.Values)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteValue(writer, pair.Value);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new InvalidOperationException("Unknown YAML AST node.");
        }
    }

    private static string ChildPath(string path, string key) =>
        key.All(character => char.IsLetterOrDigit(character) || character == '_')
            ? $"{path}.{key}"
            : $"{path}['{key.Replace("'", "\\'", StringComparison.Ordinal)}']";

    private static bool ValidOptions(BehaviorYamlOptions options) =>
        options.MaxUtf8Bytes is > 0 and <= BehaviorYamlOptions.MaximumUtf8Bytes
        && options.MaxDepth is > 0 and <= BehaviorYamlOptions.MaximumDepth
        && options.MaxNodes is > 0 and <= BehaviorYamlOptions.MaximumNodes;

    private static BehaviorManifestDecodeResult Failure(string code, string path, string message) =>
        new(null, [new BehaviorManifestDiagnostic(code, path, message)]);

    private static BehaviorImportDiagnostic ToImportDiagnostic(BehaviorManifestDiagnostic diagnostic) =>
        new(diagnostic.Code, diagnostic.Path, diagnostic.Message);

    private sealed class ParseState(BehaviorYamlOptions options)
    {
        internal BehaviorYamlOptions Options { get; } = options;
        internal int Nodes { get; set; }
    }

    private abstract record YamlValue;
    private sealed record YamlScalar(YamlScalarKind Kind, object? Value) : YamlValue;
    private sealed record YamlSequence(ImmutableArray<YamlValue> Values) : YamlValue;
    private sealed record YamlMapping(ImmutableArray<KeyValuePair<string, YamlValue>> Values) : YamlValue;

    private enum YamlScalarKind
    {
        Null,
        String,
        Integer,
        Boolean,
    }

    private sealed class YamlAdapterException(string code, string path, string message) : Exception(message)
    {
        internal string Code { get; } = code;
        internal string Path { get; } = path;
    }
}
