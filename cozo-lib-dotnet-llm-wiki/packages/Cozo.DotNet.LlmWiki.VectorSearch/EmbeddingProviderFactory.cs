namespace Cozo.DotNet.LlmWiki.VectorSearch;

public static class EmbeddingProviderFactory
{
    public static IEmbeddingProvider CreateDefault()
    {
        return OnnxMiniLmEmbeddingProvider.TryCreateBundled() is { } provider
            ? provider
            : new DeterministicEmbeddingProvider();
    }
}
