using System.Globalization;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Cozo.DotNet.LlmWiki.VectorSearch;

public sealed class OnnxMiniLmEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    public const string DefaultModelName = "sentence-transformers/all-MiniLM-L6-v2-onnx";
    public const int DefaultDimensions = 384;
    public const int DefaultMaxTokens = 256;

    private readonly InferenceSession _session;
    private readonly IReadOnlyDictionary<string, int> _vocabulary;
    private readonly int _maxTokens;
    private bool _disposed;

    public OnnxMiniLmEmbeddingProvider(string modelDirectory, int maxTokens = DefaultMaxTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        if (maxTokens < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens), "Max token count must allow [CLS], content and [SEP].");
        }

        var modelPath = Path.Combine(modelDirectory, "model.onnx");
        var vocabPath = Path.Combine(modelDirectory, "vocab.txt");
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("MiniLM ONNX model was not found.", modelPath);
        }

        if (!File.Exists(vocabPath))
        {
            throw new FileNotFoundException("MiniLM vocab.txt was not found.", vocabPath);
        }

        _session = new InferenceSession(modelPath);
        _vocabulary = LoadVocabulary(vocabPath);
        _maxTokens = maxTokens;
    }

    public string Model => DefaultModelName;

    public int Dimensions => DefaultDimensions;

    public static OnnxMiniLmEmbeddingProvider? TryCreateBundled(int maxTokens = DefaultMaxTokens)
    {
        foreach (var directory in CandidateModelDirectories())
        {
            if (HasModelAssets(directory))
            {
                return new OnnxMiniLmEmbeddingProvider(directory, maxTokens);
            }
        }

        return null;
    }

    public ValueTask<EmbeddingVector> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var encoded = Encode(text ?? string.Empty);
        using var results = _session.Run(CreateInputs(encoded));
        cancellationToken.ThrowIfCancellationRequested();

        var embedding = Pool(results, encoded.AttentionMask);
        Normalize(embedding);
        return ValueTask.FromResult(new EmbeddingVector(Model, Dimensions, embedding));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _session.Dispose();
        _disposed = true;
    }

    private static IEnumerable<string> CandidateModelDirectories()
    {
        const string relative = "Models/all-MiniLM-L6-v2";
        yield return Path.Combine(AppContext.BaseDirectory, relative);
        yield return Path.Combine(Directory.GetCurrentDirectory(), "cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.VectorSearch", relative);
        yield return Path.Combine(Directory.GetCurrentDirectory(), "packages/Cozo.DotNet.LlmWiki.VectorSearch", relative);
    }

    private static bool HasModelAssets(string directory) =>
        File.Exists(Path.Combine(directory, "model.onnx")) &&
        File.Exists(Path.Combine(directory, "vocab.txt"));

    private static IReadOnlyDictionary<string, int> LoadVocabulary(string vocabPath)
    {
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        foreach (var line in File.ReadLines(vocabPath))
        {
            var token = line.Trim();
            if (token.Length > 0 && !vocabulary.ContainsKey(token))
            {
                vocabulary[token] = index;
            }

            index++;
        }

        foreach (var required in new[] { "[PAD]", "[UNK]", "[CLS]", "[SEP]" })
        {
            if (!vocabulary.ContainsKey(required))
            {
                throw new InvalidDataException($"MiniLM vocabulary is missing required token '{required}'.");
            }
        }

        return vocabulary;
    }

    private IReadOnlyCollection<NamedOnnxValue> CreateInputs(EncodedInput input)
    {
        var inputs = new List<NamedOnnxValue>();
        foreach (var name in _session.InputMetadata.Keys)
        {
            var values = name switch
            {
                "input_ids" => input.InputIds,
                "attention_mask" => input.AttentionMask,
                "token_type_ids" => input.TokenTypeIds,
                _ => null
            };

            if (values is null)
            {
                continue;
            }

            inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(values, [1, values.Length])));
        }

        return inputs;
    }

    private EncodedInput Encode(string text)
    {
        var cls = _vocabulary["[CLS]"];
        var sep = _vocabulary["[SEP]"];
        var pad = _vocabulary["[PAD]"];

        var ids = new List<long>(_maxTokens) { cls };
        foreach (var token in BasicTokenize(text))
        {
            foreach (var piece in WordPiece(token))
            {
                if (ids.Count >= _maxTokens - 1)
                {
                    break;
                }

                ids.Add(piece);
            }

            if (ids.Count >= _maxTokens - 1)
            {
                break;
            }
        }

        ids.Add(sep);
        var attention = Enumerable.Repeat(1L, ids.Count).ToList();
        while (ids.Count < _maxTokens)
        {
            ids.Add(pad);
            attention.Add(0);
        }

        return new EncodedInput(
            ids.ToArray(),
            attention.ToArray(),
            new long[_maxTokens]);
    }

    private IEnumerable<long> WordPiece(string token)
    {
        if (token.Length == 0)
        {
            yield break;
        }

        const int maxInputCharsPerWord = 100;
        if (token.Length > maxInputCharsPerWord)
        {
            yield return _vocabulary["[UNK]"];
            yield break;
        }

        var start = 0;
        var pieces = new List<long>();
        while (start < token.Length)
        {
            var end = token.Length;
            string? current = null;
            while (start < end)
            {
                var piece = token[start..end];
                if (start > 0)
                {
                    piece = "##" + piece;
                }

                if (_vocabulary.ContainsKey(piece))
                {
                    current = piece;
                    break;
                }

                end--;
            }

            if (current is null)
            {
                yield return _vocabulary["[UNK]"];
                yield break;
            }

            pieces.Add(_vocabulary[current]);
            start = end;
        }

        foreach (var piece in pieces)
        {
            yield return piece;
        }
    }

    private static IEnumerable<string> BasicTokenize(string text)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        foreach (var rune in text.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (IsWhitespace(rune))
            {
                Flush();
                continue;
            }

            if (IsPunctuation(rune))
            {
                Flush();
                tokens.Add(rune.ToString().ToLowerInvariant());
                continue;
            }

            builder.Append(rune.ToString().ToLowerInvariant());
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (builder.Length == 0)
            {
                return;
            }

            var token = builder.ToString();
            builder.Clear();
            tokens.Add(token);
        }
    }

    private static bool IsWhitespace(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ||
        rune.Value is '\t' or '\n' or '\r';

    private static bool IsPunctuation(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation
            or UnicodeCategory.MathSymbol
            or UnicodeCategory.CurrencySymbol
            or UnicodeCategory.ModifierSymbol
            or UnicodeCategory.OtherSymbol;
    }

    private static float[] Pool(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, IReadOnlyList<long> attentionMask)
    {
        var output = results.First().AsTensor<float>();
        var dimensions = output.Dimensions.ToArray();
        if (dimensions.Length == 2)
        {
            var vector = new float[dimensions[1]];
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = output[0, i];
            }

            return vector;
        }

        if (dimensions.Length != 3)
        {
            throw new InvalidOperationException($"Unsupported MiniLM ONNX output rank: {dimensions.Length}.");
        }

        var sequenceLength = dimensions[1];
        var hiddenSize = dimensions[2];
        var pooled = new float[hiddenSize];
        var count = 0;
        for (var tokenIndex = 0; tokenIndex < sequenceLength && tokenIndex < attentionMask.Count; tokenIndex++)
        {
            if (attentionMask[tokenIndex] == 0)
            {
                continue;
            }

            for (var hiddenIndex = 0; hiddenIndex < hiddenSize; hiddenIndex++)
            {
                pooled[hiddenIndex] += output[0, tokenIndex, hiddenIndex];
            }

            count++;
        }

        if (count == 0)
        {
            return pooled;
        }

        for (var i = 0; i < pooled.Length; i++)
        {
            pooled[i] /= count;
        }

        return pooled;
    }

    private static void Normalize(float[] values)
    {
        var norm = Math.Sqrt(values.Sum(value => value * value));
        if (norm <= 0)
        {
            values[0] = 1;
            return;
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (float)(values[i] / norm);
        }
    }

    private sealed record EncodedInput(long[] InputIds, long[] AttentionMask, long[] TokenTypeIds);
}
