using System.Text.Json;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tools;

public sealed record LlmWikiOverviewGraphRequest(
    int MaxNodes = 300,
    int MaxEdges = 600,
    IReadOnlyList<string>? Categories = null);

public sealed record LlmWikiOverviewGraphResult(
    IReadOnlyList<LlmWikiGraphNode> Nodes,
    IReadOnlyList<LlmWikiGraphEdge> Edges,
    int TotalNodes,
    int TotalEdges,
    bool Truncated,
    IReadOnlyList<string> Categories);

public sealed record LlmWikiGraphNode(
    string Id,
    string Label,
    string Group,
    string Title,
    string? Description = null,
    string? BlockText = null,
    string? FullId = null);

public sealed record LlmWikiGraphEdge(
    string From,
    string To,
    string Label,
    string? Title = null);

public sealed class LlmWikiOverviewGraphBuilder
{
    public async Task<LlmWikiOverviewGraphResult> BuildAsync(
        CozoOm om,
        LlmWikiOverviewGraphRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        request ??= new LlmWikiOverviewGraphRequest();
        await om.InitCodeKnowledgeAsync(cancellationToken);

        var categories = NormalizeCategories(request.Categories);
        var includeCode = categories.Contains("code");
        var includeDocs = categories.Contains("docs");
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 2_000);
        var maxEdges = Math.Clamp(request.MaxEdges, 1, 5_000);
        var nodes = new Dictionary<string, LlmWikiGraphNode>(StringComparer.Ordinal);
        var edges = new List<LlmWikiGraphEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var totalNodes = 0;
        var totalEdges = 0;
        var fileLimit = Math.Max(1, maxNodes / (includeCode && includeDocs ? 4 : 2));
        var symbolLimit = includeCode ? Math.Max(1, maxNodes / (includeDocs ? 3 : 2)) : 0;
        var docLimit = includeDocs ? Math.Max(1, maxNodes / (includeCode ? 3 : 2)) : 0;

        totalNodes += await AddReposAsync(om, nodes, maxNodes, cancellationToken);

        if (includeCode || includeDocs)
        {
            totalNodes += await AddFilesAsync(om, nodes, edges, edgeKeys, fileLimit, maxNodes, maxEdges, cancellationToken);
        }

        if (includeCode)
        {
            totalNodes += await AddSymbolsAsync(om, nodes, edges, edgeKeys, symbolLimit, maxNodes, maxEdges, cancellationToken);
        }

        if (includeDocs)
        {
            totalNodes += await AddDocsAsync(om, nodes, edges, edgeKeys, docLimit, maxNodes, maxEdges, cancellationToken);
        }

        totalEdges += edges.Count;
        totalEdges += await AddRelationsAsync(om, nodes, edges, edgeKeys, maxNodes, maxEdges, includeCode, includeDocs, cancellationToken);

        return new LlmWikiOverviewGraphResult(
            nodes.Values.ToArray(),
            edges.ToArray(),
            totalNodes,
            totalEdges,
            totalNodes > nodes.Count || totalEdges > edges.Count,
            categories);
    }

    private static async Task<int> AddReposAsync(
        CozoOm om,
        Dictionary<string, LlmWikiGraphNode> nodes,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[repo_id, name, root_path, commit] :=
              *ck_repo{repo_id, root_path, name, commit}
            :limit 20
            """,
            cancellationToken: cancellationToken);

        foreach (var row in rows.Rows)
        {
            if (nodes.Count >= maxNodes)
            {
                return rows.Rows.Count;
            }

            var id = JsonString(row[0]) ?? "";
            if (id.Length == 0)
            {
                continue;
            }

            var name = JsonString(row[1]) ?? id;
            var rootPath = JsonString(row[2]) ?? "";
            var commit = JsonString(row[3]) ?? "";
            AddNode(nodes, new LlmWikiGraphNode(id, name, "repo", Title("repo", id, rootPath, commit), rootPath, FullId: id), maxNodes);
        }

        return rows.Rows.Count;
    }

    private static async Task<int> AddFilesAsync(
        CozoOm om,
        Dictionary<string, LlmWikiGraphNode> nodes,
        List<LlmWikiGraphEdge> edges,
        HashSet<string> edgeKeys,
        int queryLimit,
        int maxNodes,
        int maxEdges,
        CancellationToken cancellationToken)
    {
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[file_id, repo_id, path, language] :=
              *ck_file{file_id, repo_id, path, language, hash: _hash, updated_at: _updated_at}
            :sort path
            :limit $limit
            """,
            Params(("limit", queryLimit)),
            cancellationToken: cancellationToken);

        var seen = 0;
        foreach (var row in rows.Rows)
        {
            seen++;
            var id = JsonString(row[0]) ?? "";
            if (id.Length == 0)
            {
                continue;
            }

            var repoId = JsonString(row[1]) ?? "";
            var path = JsonString(row[2]) ?? id;
            var language = JsonString(row[3]) ?? "";
            AddNode(nodes, new LlmWikiGraphNode(id, Path.GetFileName(path), "file", Title("file", id, path, language), path, FullId: id), maxNodes);
            if (repoId.Length > 0)
            {
                AddEdge(nodes, edges, new LlmWikiGraphEdge(repoId, id, CodeEdgeKinds.Contains, path), edgeKeys, maxEdges);
            }
        }

        return seen;
    }

    private static async Task<int> AddSymbolsAsync(
        CozoOm om,
        Dictionary<string, LlmWikiGraphNode> nodes,
        List<LlmWikiGraphEdge> edges,
        HashSet<string> edgeKeys,
        int queryLimit,
        int maxNodes,
        int maxEdges,
        CancellationToken cancellationToken)
    {
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[symbol_id, file_id, name, kind, signature] :=
              *ck_symbol{symbol_id, file_id, name, kind, start_line: _start, end_line: _end, signature}
            :sort name
            :limit $limit
            """,
            Params(("limit", queryLimit)),
            cancellationToken: cancellationToken);

        var seen = 0;
        foreach (var row in rows.Rows)
        {
            seen++;
            var id = JsonString(row[0]) ?? "";
            if (id.Length == 0)
            {
                continue;
            }

            var fileId = JsonString(row[1]) ?? "";
            var name = JsonString(row[2]) ?? id;
            var kind = JsonString(row[3]) ?? "symbol";
            var signature = JsonString(row[4]) ?? "";
            AddNode(nodes, new LlmWikiGraphNode(id, name, "symbol", Title(kind, id, signature), signature, FullId: id), maxNodes);
            if (fileId.Length > 0)
            {
                // v2: file→symbol containment is CONTAINS (was v1 "defines"); using the same
                // label as the real ck_edge rows lets the edge-key dedup collapse duplicates.
                AddEdge(nodes, edges, new LlmWikiGraphEdge(fileId, id, CodeEdgeKinds.Contains, signature), edgeKeys, maxEdges);
            }
        }

        return seen;
    }

    private static async Task<int> AddDocsAsync(
        CozoOm om,
        Dictionary<string, LlmWikiGraphNode> nodes,
        List<LlmWikiGraphEdge> edges,
        HashSet<string> edgeKeys,
        int queryLimit,
        int maxNodes,
        int maxEdges,
        CancellationToken cancellationToken)
    {
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[doc_id, file_id, anchor, text] :=
              *ck_doc_block{doc_id, file_id, anchor, text, hash: _hash, updated_at: _updated_at}
            :sort doc_id
            :limit $limit
            """,
            Params(("limit", queryLimit)),
            cancellationToken: cancellationToken);

        var seen = 0;
        foreach (var row in rows.Rows)
        {
            seen++;
            var id = JsonString(row[0]) ?? "";
            if (id.Length == 0)
            {
                continue;
            }

            var fileId = JsonString(row[1]) ?? "";
            var anchor = JsonString(row[2]) ?? "doc";
            var text = JsonString(row[3]) ?? "";
            AddNode(nodes, new LlmWikiGraphNode(id, anchor, "doc", Title("doc", id, Snippet(text)), Snippet(text), text, id), maxNodes);
            if (fileId.Length > 0)
            {
                AddEdge(nodes, edges, new LlmWikiGraphEdge(fileId, id, CodeEdgeKinds.Contains, anchor), edgeKeys, maxEdges);
            }
        }

        return seen;
    }

    private static async Task<int> AddRelationsAsync(
        CozoOm om,
        Dictionary<string, LlmWikiGraphNode> nodes,
        List<LlmWikiGraphEdge> edges,
        HashSet<string> edgeKeys,
        int maxNodes,
        int maxEdges,
        bool includeCode,
        bool includeDocs,
        CancellationToken cancellationToken)
    {
        // v2 kinds (CONTAINS/IMPORTS/DOC_LINKS/...) and the regex-tier lowercase "near"
        // proximity kind are both passed through as edge labels; category filtering is
        // node-id-prefix based (doc:/symbol:/file:/import:) and therefore kind-agnostic.
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[from_id, to_id, kind, evidence] :=
              *ck_edge{from_id, to_id, kind, file_id: _file_id, line: _line, evidence}
            :sort kind, from_id, to_id
            :limit $limit
            """,
            Params(("limit", maxEdges)),
            cancellationToken: cancellationToken);

        var seen = 0;
        foreach (var row in rows.Rows)
        {
            seen++;
            var fromId = JsonString(row[0]) ?? "";
            var toId = JsonString(row[1]) ?? "";
            var kind = JsonString(row[2]) ?? "relates";
            var evidence = JsonString(row[3]) ?? "";
            if (fromId.Length == 0 || toId.Length == 0 ||
                !CategoryAllowed(fromId, includeCode, includeDocs) ||
                !CategoryAllowed(toId, includeCode, includeDocs))
            {
                continue;
            }

            AddPlaceholderNode(nodes, fromId, maxNodes);
            AddPlaceholderNode(nodes, toId, maxNodes);
            AddEdge(nodes, edges, new LlmWikiGraphEdge(fromId, toId, kind, evidence), edgeKeys, maxEdges);
        }

        return seen;
    }

    private static void AddPlaceholderNode(Dictionary<string, LlmWikiGraphNode> nodes, string id, int maxNodes)
    {
        if (nodes.ContainsKey(id) || nodes.Count >= maxNodes)
        {
            return;
        }

        var group = id.StartsWith("doc:", StringComparison.Ordinal) ? "doc"
            : id.StartsWith("symbol:", StringComparison.Ordinal) ? "symbol"
            : id.StartsWith("file:", StringComparison.Ordinal) ? "file"
            : id.StartsWith("import:", StringComparison.Ordinal) ? "import"
            : "external";
        AddNode(nodes, new LlmWikiGraphNode(id, CompactId(id), group, Title(group, id), FullId: id), maxNodes);
    }

    private static bool AddNode(Dictionary<string, LlmWikiGraphNode> nodes, LlmWikiGraphNode node, int maxNodes)
    {
        if (nodes.ContainsKey(node.Id))
        {
            return true;
        }

        if (nodes.Count >= maxNodes)
        {
            return false;
        }

        nodes[node.Id] = node;
        return true;
    }

    private static void AddEdge(
        IReadOnlyDictionary<string, LlmWikiGraphNode> nodes,
        List<LlmWikiGraphEdge> edges,
        LlmWikiGraphEdge edge,
        HashSet<string> edgeKeys,
        int maxEdges)
    {
        if (edges.Count >= maxEdges || !nodes.ContainsKey(edge.From) || !nodes.ContainsKey(edge.To))
        {
            return;
        }

        var key = $"{edge.From}\u0001{edge.To}\u0001{edge.Label}";
        if (edgeKeys.Add(key))
        {
            edges.Add(edge);
        }
    }

    private static bool CategoryAllowed(string id, bool includeCode, bool includeDocs)
    {
        if (id.StartsWith("doc:", StringComparison.Ordinal))
        {
            return includeDocs;
        }

        if (id.StartsWith("symbol:", StringComparison.Ordinal) ||
            id.StartsWith("file:", StringComparison.Ordinal) ||
            id.StartsWith("import:", StringComparison.Ordinal))
        {
            return includeCode || includeDocs;
        }

        return includeCode;
    }

    private static IReadOnlyList<string> NormalizeCategories(IReadOnlyList<string>? categories)
    {
        if (categories is null || categories.Count == 0)
        {
            return ["code", "docs"];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            foreach (var item in category.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (item.Trim().ToLowerInvariant())
                {
                    case "code":
                    case "symbol":
                    case "symbols":
                    case "file":
                    case "files":
                        result.Add("code");
                        break;
                    case "doc":
                    case "docs":
                    case "document":
                    case "documents":
                        result.Add("docs");
                        break;
                }
            }
        }

        return result.Count == 0 ? ["code", "docs"] : result.OrderBy(x => x, StringComparer.Ordinal).ToArray();
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

    private static string Title(params string?[] parts) =>
        string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string CompactId(string id)
    {
        var value = id;
        var index = value.LastIndexOf(':');
        if (index >= 0 && index + 1 < value.Length)
        {
            value = value[(index + 1)..];
        }

        return value.Length <= 40 ? value : $"{value[..18]}...{value[^18..]}";
    }

    private static string Snippet(string value)
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 220 ? compact : $"{compact[..217]}...";
    }

    private static string? JsonString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
}
