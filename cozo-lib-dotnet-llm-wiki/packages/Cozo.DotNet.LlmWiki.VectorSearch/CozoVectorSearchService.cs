using System.Text.Json;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.VectorSearch;

public sealed class CozoVectorSearchService
{
    private readonly IEmbeddingProvider _embeddingProvider;

    public CozoVectorSearchService(IEmbeddingProvider? embeddingProvider = null)
    {
        _embeddingProvider = embeddingProvider ?? EmbeddingProviderFactory.CreateDefault();
    }

    public async Task EnsureSchemaAsync(CozoOm om, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        try
        {
            await om.Runtime.Store.RunAsync(
                $":create llm_wiki_embedding {{item_id: String => source_kind: String, source_id: String, text: String, vector: <F32; {_embeddingProvider.Dimensions}>, updated_at: String}}",
                cancellationToken: cancellationToken);
        }
        catch (CozoException ex) when (IsCreateConflict(ex))
        {
        }
    }

    public async Task EnsureHnswIndexAsync(CozoOm om, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        await EnsureSchemaAsync(om, cancellationToken);
        try
        {
            await om.Runtime.Store.RunAsync(
                """
                ::hnsw create llm_wiki_embedding:semantic {
                    fields: [vector],
                    dim:
                """ + _embeddingProvider.Dimensions + """
                    ,
                    dtype: F32,
                    distance: Cosine,
                    ef: 16,
                    m: 32
                }
                """,
                cancellationToken: cancellationToken);
        }
        catch (CozoException ex) when (IsCreateConflict(ex))
        {
        }
    }

    public async Task<VectorIndexResult> IndexAsync(
        CozoOm om,
        VectorIndexRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        request ??= new VectorIndexRequest();
        await EnsureSchemaAsync(om, cancellationToken);

        var sources = await ReadSourcesAsync(om, request, cancellationToken);
        var indexed = 0;
        foreach (var source in sources)
        {
            var embedding = await _embeddingProvider.EmbedAsync(source.Text, cancellationToken);
            await om.Runtime.Store.RunAsync(
                """
                ?[item_id, source_kind, source_id, text, vector, updated_at] <- [[$item_id, $source_kind, $source_id, $text, vec($vector), $updated_at]]
                :put llm_wiki_embedding {item_id => source_kind, source_id, text, vector, updated_at}
                """,
                Params(
                    ("item_id", source.ItemId),
                    ("source_kind", source.SourceKind),
                    ("source_id", source.SourceId),
                    ("text", source.Text),
                    ("vector", embedding.Values),
                    ("updated_at", DateTimeOffset.UtcNow.ToString("O"))),
                cancellationToken: cancellationToken);
            indexed++;
        }

        return new VectorIndexResult(indexed, _embeddingProvider.Dimensions, _embeddingProvider.Model);
    }

    /// <summary>
    /// Rebuilds the ck_search_text FTS projection (hybrid-search track, design.md §1): one row
    /// per ck_symbol (name + signature, source_kind=code) and per ck_doc_block (text,
    /// source_kind=docs), each truncated to <see cref="SearchTextMaxLength"/> characters. The
    /// relation is cleared with :rm + re-put (":replace" is rejected once the FTS index exists),
    /// so the rebuild is idempotent and keeps the index alive. Static: the FTS channel never
    /// needs an embedding provider.
    /// </summary>
    internal static async Task<int> EnsureSearchTextAsync(CozoOm om, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        await EnsureSearchTextSchemaAsync(om, cancellationToken);
        await om.Runtime.Store.RunAsync(
            "?[source_id] := *ck_search_text{source_id} :rm ck_search_text {source_id}",
            cancellationToken: cancellationToken);

        var rows = new List<object?[]>();
        var symbolRows = await om.Runtime.Store.RunAsync(
            """
            ?[symbol_id, name, signature] :=
              *ck_symbol{symbol_id, file_id: _file_id, name, kind: _kind, start_line: _start, end_line: _end, signature}
            """,
            cancellationToken: cancellationToken);
        foreach (var row in symbolRows.Rows)
        {
            var id = JsonString(row[0]) ?? "";
            var text = TruncateSearchText($"{JsonString(row[1])} {JsonString(row[2])}".Trim());
            if (id.Length > 0 && text.Length > 0)
            {
                rows.Add([id, "code", text]);
            }
        }

        var docRows = await om.Runtime.Store.RunAsync(
            """
            ?[doc_id, text] :=
              *ck_doc_block{doc_id, file_id: _file_id, anchor: _anchor, text, hash: _hash, updated_at: _updated_at}
            """,
            cancellationToken: cancellationToken);
        foreach (var row in docRows.Rows)
        {
            var id = JsonString(row[0]) ?? "";
            var text = TruncateSearchText(JsonString(row[1]) ?? "");
            if (id.Length > 0 && text.Length > 0)
            {
                rows.Add([id, "docs", text]);
            }
        }

        foreach (var chunk in rows.Chunk(SearchTextPutChunkSize))
        {
            await om.Runtime.Store.RunAsync(
                """
                ?[source_id, source_kind, text] <- $rows
                :put ck_search_text {source_id => source_kind, text}
                """,
                Params(("rows", chunk)),
                cancellationToken: cancellationToken);
        }

        return rows.Count;
    }

    /// <summary>
    /// Ensures the BM25 index on ck_search_text. Tokenizer choice (track-measured, see the
    /// hybrid-search track findings): Simple keeps a CJK run as a single token so Chinese
    /// sub-phrase queries miss; Cangjie (jieba, compiled unconditionally into cozo-core)
    /// segments CJK and keeps ASCII identifiers whole. A second ::fts create is swallowed as a
    /// create conflict, making the ensure idempotent.
    /// </summary>
    internal static async Task EnsureFtsIndexAsync(CozoOm om, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        await EnsureSearchTextSchemaAsync(om, cancellationToken);
        try
        {
            await om.Runtime.Store.RunAsync(
                """
                ::fts create ck_search_text:fts {
                    extractor: text,
                    tokenizer: Cangjie('search'),
                    filters: [Lowercase]
                }
                """,
                cancellationToken: cancellationToken);
        }
        catch (CozoException ex) when (IsCreateConflict(ex))
        {
        }
    }

    /// <summary>
    /// BM25 text search over ck_search_text (hybrid-search track, design.md §2). The user query
    /// is literalized — split on whitespace, each token escaped and double-quoted, joined with
    /// OR — so FTS expression syntax (AND/OR/NOT/NEAR/parentheses/quotes/boosters) in user input
    /// is matched literally instead of being parsed (behavior delta case literal-query).
    /// </summary>
    internal static async Task<IReadOnlyList<TextSearchHit>> TextSearchAsync(
        CozoOm om,
        string query,
        int limit = 10,
        IReadOnlyList<string>? sourceKinds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentNullException.ThrowIfNull(query);
        var literalQuery = BuildLiteralFtsQuery(query);
        if (literalQuery.Length == 0)
        {
            return [];
        }

        var kinds = NormalizeSearchTextKinds(sourceKinds);
        var effectiveLimit = Math.Max(1, limit);
        // With a kind filter the FTS top-k runs before the filter, so over-fetch (bounded).
        var k = kinds.Count == 0 ? effectiveLimit : Math.Min(effectiveLimit * 4, 200);
        var script = kinds.Count == 0
            ? """
              ?[source_id, source_kind, text, score] :=
                ~ck_search_text:fts{source_id, source_kind, text | query: $q, k: $k, bind_score: score}
              :order -score
              :limit $limit
              """
            : """
              ?[source_id, source_kind, text, score] :=
                ~ck_search_text:fts{source_id, source_kind, text | query: $q, k: $k, bind_score: score},
                is_in(source_kind, $source_kinds)
              :order -score
              :limit $limit
              """;
        var result = await om.Runtime.Store.RunAsync(
            script,
            kinds.Count == 0
                ? Params(("q", literalQuery), ("k", k), ("limit", effectiveLimit))
                : Params(("q", literalQuery), ("k", k), ("limit", effectiveLimit), ("source_kinds", kinds)),
            cancellationToken: cancellationToken);
        return result.Rows.Select(row => new TextSearchHit(
            JsonString(row[0]) ?? "",
            JsonString(row[1]) ?? "",
            JsonString(row[2]) ?? "",
            JsonDouble(row[3]))).ToArray();
    }

    /// <summary>
    /// Hybrid retrieval (hybrid-search track, design.md §3–4): the BM25 text channel
    /// (ck_search_text FTS, literalized query) and the vector channel each fetch
    /// top-(limit*4, capped 50); hits are merged by source id with reciprocal rank fusion,
    /// score = Σ_channels 1/(rrfK + rank), rank 1-based per channel. mode selects
    /// hybrid|vector|text (single-channel modes return the same result shape). Degradations
    /// never throw: an FTS failure or missing index yields pure vector results plus an
    /// "fts_unavailable" diagnostic; an empty vector channel (index_embeddings not run)
    /// yields text-only results plus a "vector_channel_empty" diagnostic.
    /// </summary>
    public async Task<VectorSearchResult> HybridSearchAsync(
        CozoOm om,
        string query,
        int limit = 10,
        IReadOnlyList<string>? sourceKinds = null,
        string mode = "hybrid",
        int rrfK = 60,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentNullException.ThrowIfNull(query);
        var normalizedMode = (mode ?? "").Trim().ToLowerInvariant();
        if (normalizedMode.Length == 0)
        {
            normalizedMode = "hybrid";
        }

        if (normalizedMode is not ("hybrid" or "vector" or "text"))
        {
            throw new ArgumentException($"Invalid search mode: {mode}. Expected hybrid, vector, or text.", nameof(mode));
        }

        var effectiveK = Math.Max(1, rrfK);
        var effectiveLimit = Math.Max(1, limit);
        var channelK = Math.Min(effectiveLimit * 4, 50);
        var diagnostics = new List<string>();
        var merged = new Dictionary<string, HybridAccumulator>(StringComparer.Ordinal);

        if (normalizedMode is "hybrid" or "vector")
        {
            var vectorResult = await SearchAsync(
                om,
                new VectorSearchRequest(query, channelK, sourceKinds),
                cancellationToken);
            var rank = 0;
            foreach (var hit in vectorResult.Hits)
            {
                rank++;
                if (!merged.TryGetValue(hit.SourceId, out var entry))
                {
                    entry = new HybridAccumulator(hit.SourceId);
                    merged[hit.SourceId] = entry;
                }

                entry.Score += 1.0 / (effectiveK + rank);
                entry.Vector = hit;
            }

            if (rank == 0)
            {
                diagnostics.Add("vector_channel_empty: no embeddings indexed for this query scope (run index_embeddings)");
            }
        }

        if (normalizedMode is "hybrid" or "text")
        {
            IReadOnlyList<TextSearchHit> textHits;
            try
            {
                textHits = await TextSearchAsync(om, query, channelK, sourceKinds, cancellationToken);
            }
            catch (CozoException ex)
            {
                textHits = [];
                diagnostics.Add($"fts_unavailable: text channel degraded ({FirstLine(ex.Message)})");
            }

            var rank = 0;
            foreach (var hit in textHits)
            {
                rank++;
                if (!merged.TryGetValue(hit.SourceId, out var entry))
                {
                    entry = new HybridAccumulator(hit.SourceId);
                    merged[hit.SourceId] = entry;
                }

                entry.Score += 1.0 / (effectiveK + rank);
                entry.Text = hit;
            }
        }

        var hits = merged.Values
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.SourceId, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .Select(entry => entry.ToHit())
            .ToArray();
        return new VectorSearchResult(
            query,
            hits,
            _embeddingProvider.Model,
            _embeddingProvider.Dimensions,
            Mode: normalizedMode,
            Diagnostics: diagnostics.Count == 0 ? null : diagnostics);
    }

    public async Task<VectorSearchResult> SearchAsync(
        CozoOm om,
        VectorSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSchemaAsync(om, cancellationToken);
        var embedding = await _embeddingProvider.EmbedAsync(request.Query, cancellationToken);
        var sourceKinds = NormalizeSourceKinds(request.SourceKinds);
        var script = sourceKinds.Count == 0
            ? """
              ?[item_id, source_kind, source_id, text, dist] :=
                *llm_wiki_embedding{item_id, source_kind, source_id, text, vector, updated_at: _updated_at},
                dist = cos_dist(vector, vec($query_vector))
              :order dist
              :limit $limit
              """
            : """
              ?[item_id, source_kind, source_id, text, dist] :=
                *llm_wiki_embedding{item_id, source_kind, source_id, text, vector, updated_at: _updated_at},
                is_in(source_kind, $source_kinds),
                dist = cos_dist(vector, vec($query_vector))
              :order dist
              :limit $limit
              """;
        var result = await om.Runtime.Store.RunAsync(
            script,
            sourceKinds.Count == 0
                ? Params(("query_vector", embedding.Values), ("limit", Math.Max(1, request.Limit)))
                : Params(("query_vector", embedding.Values), ("limit", Math.Max(1, request.Limit)), ("source_kinds", sourceKinds)),
            cancellationToken: cancellationToken);
        var hits = result.Rows.Select(row => new VectorSearchHit(
            JsonString(row[0]) ?? "",
            JsonString(row[1]) ?? "",
            JsonString(row[2]) ?? "",
            JsonString(row[3]) ?? "",
            JsonDouble(row[4]))).ToArray();
        return new VectorSearchResult(request.Query, hits, _embeddingProvider.Model, _embeddingProvider.Dimensions);
    }

    private static async Task<IReadOnlyList<VectorSource>> ReadSourcesAsync(
        CozoOm om,
        VectorIndexRequest request,
        CancellationToken cancellationToken)
    {
        var sources = new List<VectorSource>();
        if (request.IncludeSymbols)
        {
            var rows = await om.Runtime.Store.RunAsync(
                """
                ?[symbol_id, name, kind, signature] :=
                  *ck_symbol{symbol_id, file_id: _file_id, name, kind, start_line: _start, end_line: _end, signature}
                :limit $limit
                """,
                Params(("limit", Math.Max(1, request.Limit))),
                cancellationToken: cancellationToken);
            sources.AddRange(rows.Rows.Select(row =>
            {
                var id = JsonString(row[0]) ?? "";
                var text = $"{JsonString(row[1])} {JsonString(row[2])} {JsonString(row[3])}".Trim();
                return new VectorSource($"embedding:symbol:{id}", "symbol", id, text);
            }).Where(source => source.SourceId.Length > 0 && source.Text.Length > 0));
        }

        if (request.IncludeDocs)
        {
            var rows = await om.Runtime.Store.RunAsync(
                """
                ?[doc_id, text] :=
                  *ck_doc_block{doc_id, file_id: _file_id, anchor: _anchor, text, hash: _hash, updated_at: _updated_at}
                :limit $limit
                """,
                Params(("limit", Math.Max(1, request.Limit))),
                cancellationToken: cancellationToken);
            sources.AddRange(rows.Rows.Select(row =>
            {
                var id = JsonString(row[0]) ?? "";
                var text = JsonString(row[1]) ?? "";
                return new VectorSource($"embedding:doc:{id}", "doc", id, text);
            }).Where(source => source.SourceId.Length > 0 && source.Text.Length > 0));
        }

        return sources;
    }

    private static Dictionary<string, object?> Params(params (string Key, object? Value)[] entries)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            dict[key] = value;
        }

        return dict;
    }

    private static string? JsonString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();

    private static double JsonDouble(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result) ? result : 0;

    private static IReadOnlyList<string> NormalizeSourceKinds(IReadOnlyList<string>? sourceKinds)
    {
        if (sourceKinds is null || sourceKinds.Count == 0)
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceKind in sourceKinds)
        {
            foreach (var item in sourceKind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (item.Trim().ToLowerInvariant())
                {
                    case "code":
                    case "symbol":
                    case "symbols":
                        result.Add("symbol");
                        break;
                    case "doc":
                    case "docs":
                    case "document":
                    case "documents":
                        result.Add("doc");
                        break;
                    default:
                        if (!string.IsNullOrWhiteSpace(item))
                        {
                            result.Add(item.Trim());
                        }

                        break;
                }
            }
        }

        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private const int SearchTextMaxLength = 512;
    private const int SearchTextPutChunkSize = 500;

    private static async Task EnsureSearchTextSchemaAsync(CozoOm om, CancellationToken cancellationToken)
    {
        try
        {
            await om.Runtime.Store.RunAsync(
                ":create ck_search_text {source_id: String => source_kind: String, text: String}",
                cancellationToken: cancellationToken);
        }
        catch (CozoException ex) when (IsCreateConflict(ex))
        {
        }
    }

    private static string TruncateSearchText(string text) =>
        text.Length <= SearchTextMaxLength ? text : text[..SearchTextMaxLength];

    /// <summary>
    /// Literalizes a user query for the Cozo FTS expression grammar: whitespace-split tokens,
    /// each escaped (backslash, double quote) and double-quoted, joined with OR. Quoted phrases
    /// pass through the grammar as literals, so AND/OR/NOT/NEAR/(/)/^ in user input never parse
    /// as operators.
    /// </summary>
    private static string BuildLiteralFtsQuery(string query)
    {
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(
            " OR ",
            tokens.Select(token => "\"" + token.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));
    }

    private static IReadOnlyList<string> NormalizeSearchTextKinds(IReadOnlyList<string>? sourceKinds)
    {
        var normalized = NormalizeSourceKinds(sourceKinds);
        if (normalized.Count == 0)
        {
            return [];
        }

        // The vector channel canonicalizes to symbol/doc; ck_search_text stores code/docs.
        return normalized
            .Select(kind => kind switch
            {
                "symbol" => "code",
                "doc" => "docs",
                _ => kind,
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsCreateConflict(CozoException ex)
    {
        var text = $"{ex.Message}\n{ex.RawResponse}".ToLowerInvariant();
        return text.Contains("conflict", StringComparison.Ordinal) ||
               text.Contains("already", StringComparison.Ordinal) ||
               text.Contains("exists", StringComparison.Ordinal);
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? text : text[..index];
    }

    /// <summary>Per-source RRF accumulator; the vector-channel shape wins when both channels hit.</summary>
    private sealed class HybridAccumulator(string sourceId)
    {
        public string SourceId { get; } = sourceId;

        public double Score { get; set; }

        public VectorSearchHit? Vector { get; set; }

        public TextSearchHit? Text { get; set; }

        public VectorSearchHit ToHit()
        {
            var channels = new List<string>(2);
            if (Text is not null)
            {
                channels.Add("text");
            }

            if (Vector is not null)
            {
                channels.Add("vector");
            }

            return Vector is not null
                ? Vector with { RrfScore = Score, Channels = channels }
                : new VectorSearchHit(
                    ItemId: "",
                    // ck_search_text stores code/docs; align with the vector vocabulary.
                    SourceKind: Text!.SourceKind switch { "code" => "symbol", "docs" => "doc", var kind => kind },
                    SourceId: SourceId,
                    Text: Text.Text,
                    Distance: 0,
                    RrfScore: Score,
                    Channels: channels);
        }
    }

    private sealed record VectorSource(string ItemId, string SourceKind, string SourceId, string Text);
}
