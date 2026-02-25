namespace Cozo.DotNet.LlmWiki.VectorSearch;

public sealed record EmbeddingVector(
    string Model,
    int Dimensions,
    IReadOnlyList<float> Values);

public interface IEmbeddingProvider
{
    string Model { get; }

    int Dimensions { get; }

    ValueTask<EmbeddingVector> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public sealed record VectorIndexRequest(
    bool IncludeSymbols = true,
    bool IncludeDocs = true,
    int Limit = 1000);

public sealed record VectorIndexResult(
    int Indexed,
    int Dimensions,
    string Model);

public sealed record VectorSearchRequest(
    string Query,
    int Limit = 10,
    IReadOnlyList<string>? SourceKinds = null);

/// <summary>
/// One semantic-search hit. Hybrid-search track add-only fields: <paramref name="RrfScore"/>
/// (reciprocal-rank-fusion score, null on pure vector <c>SearchAsync</c> results) and
/// <paramref name="Channels"/> (which channels hit this source: "text" and/or "vector").
/// For a text-only hit ItemId is empty (no embedding row backs it) and Distance is 0.
/// </summary>
public sealed record VectorSearchHit(
    string ItemId,
    string SourceKind,
    string SourceId,
    string Text,
    double Distance,
    double? RrfScore = null,
    IReadOnlyList<string>? Channels = null);

/// <summary>
/// Semantic-search result. Hybrid-search track add-only fields: <paramref name="Mode"/>
/// (hybrid|vector|text, null on pure vector <c>SearchAsync</c> results) and
/// <paramref name="Diagnostics"/> (channel degradations, e.g. fts_unavailable /
/// vector_channel_empty — degradations never throw).
/// </summary>
public sealed record VectorSearchResult(
    string Query,
    IReadOnlyList<VectorSearchHit> Hits,
    string Model,
    int Dimensions,
    string? Mode = null,
    IReadOnlyList<string>? Diagnostics = null);

/// <summary>
/// One BM25 hit from the ck_search_text FTS channel (hybrid-search track, design.md §1–2).
/// SourceKind is "code" (ck_symbol projection) or "docs" (ck_doc_block projection).
/// </summary>
internal sealed record TextSearchHit(
    string SourceId,
    string SourceKind,
    string Text,
    double Score);
