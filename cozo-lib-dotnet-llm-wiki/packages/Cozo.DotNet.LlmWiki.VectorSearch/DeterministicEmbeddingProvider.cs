using System.Security.Cryptography;
using System.Text;

namespace Cozo.DotNet.LlmWiki.VectorSearch;

public sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
{
    public DeterministicEmbeddingProvider(int dimensions = 32, string model = "deterministic-hash-v1")
    {
        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Dimensions must be positive.");
        }

        Dimensions = dimensions;
        Model = model;
    }

    public string Model { get; }

    public int Dimensions { get; }

    public ValueTask<EmbeddingVector> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = new float[Dimensions];
        foreach (var token in Tokenize(text))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            for (var i = 0; i < Dimensions; i++)
            {
                var byteValue = hash[i % hash.Length];
                values[i] += (byteValue / 255f) - 0.5f;
            }
        }

        Normalize(values);
        return ValueTask.FromResult(new EmbeddingVector(Model, Dimensions, values));
    }

    private static IEnumerable<string> Tokenize(string text) =>
        text.ToLowerInvariant().Split(
            [' ', '\t', '\r', '\n', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void Normalize(float[] values)
    {
        var norm = Math.Sqrt(values.Sum(v => v * v));
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
}
