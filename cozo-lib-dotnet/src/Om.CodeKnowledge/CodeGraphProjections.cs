using System.Text.Json;
using Cozo.DotNet.Om.Contracts;

namespace Cozo.DotNet.Om.CodeKnowledge;

/// <summary>
/// CozoScript Datalog projections over ck_edge for Cozo fixed-rule algorithms
/// (design.md §5 of track redesign-codeknowledge-schema-v2).
///
/// Fixed-rule input/output conventions (cozo-core/src/fixed_rule/algos):
/// - CommunityDetectionLouvain(rel[]) consumes [from, to] or weighted [from, to, weight]
///   rows and outputs rows of [hierarchy_labels_list, node].
/// - StronglyConnectedComponents(rel[]) consumes [from, to] rows and outputs [node, group_id].
/// Invocation shape: <c>result[] &lt;~ FixedRuleName(projection[])</c>.
/// </summary>
public static class CodeGraphProjections
{
    /// <summary>
    /// CALLS edges at or above the <c>$min_confidence</c> parameter. Callers must bind
    /// <c>min_confidence</c> (use 0.0 for "everything").
    /// </summary>
    public const string CallGraph =
        """
        call_graph[from, to] := *ck_edge{ from_id: from, to_id: to, kind: "CALLS", confidence }, confidence >= $min_confidence
        """;

    /// <summary>IMPORTS edges (file/module dependency graph). No parameters.</summary>
    public const string ImportGraph =
        """
        import_graph[from, to] := *ck_edge{ from_id: from, to_id: to, kind: "IMPORTS" }
        """;

    /// <summary>
    /// Weighted CALLS+IMPORTS union used as Louvain clustering input: CALLS edges weigh
    /// twice their confidence (structural cohesion signal), IMPORTS edges weigh their
    /// confidence. No parameters.
    /// </summary>
    public const string ClusterInput =
        """
        cluster_input[from, to, weight] := *ck_edge{ from_id: from, to_id: to, kind: "CALLS", confidence }, weight = confidence * 2.0
        cluster_input[from, to, weight] := *ck_edge{ from_id: from, to_id: to, kind: "IMPORTS", confidence }, weight = confidence
        """;

    /// <summary>
    /// <see cref="ClusterInput"/> filtered to edges with confidence at or above the
    /// <c>$min_confidence</c> parameter (used by ComputeCommunitiesAsync; bind 0.0 for "everything").
    /// Internal: the public seam is <see cref="CommunityDetectionOptions.MinConfidence"/> (design: minimal public surface).
    /// </summary>
    internal const string ClusterInputMinConfidence =
        """
        cluster_input[from, to, weight] := *ck_edge{ from_id: from, to_id: to, kind: "CALLS", confidence }, confidence >= $min_confidence, weight = confidence * 2.0
        cluster_input[from, to, weight] := *ck_edge{ from_id: from, to_id: to, kind: "IMPORTS", confidence }, confidence >= $min_confidence, weight = confidence
        """;
}

/// <summary>
/// Fixed-rule consumers over the projections: the public community-detection API
/// (track add-llm-wiki-community-detection, design §1–§3) and the trace/cycles API
/// (track add-llm-wiki-trace-and-check, design §1–§2).
/// </summary>
public static class CozoOmCodeGraphExtensions
{
    /// <summary>
    /// Runs community detection (default: Cozo's Louvain fixed rule) over the weighted
    /// CALLS+IMPORTS cluster projection and persists the result to ck_community / ck_member
    /// (replacing previous contents).
    ///
    /// Quality semantics:
    /// - cohesion = intra-community edge weight / (intra + boundary edge weight); isolated = 1.0.
    /// - label = longest common dot-separated qualified-name prefix of the members (from sym_key,
    ///   lang prefix and arity suffix stripped); fallback: highest weighted-degree member's name;
    ///   collisions across communities get a #n suffix.
    /// - deterministic output: communities are numbered community:001… by
    ///   (size desc, smallest member symbolId asc) and member lists are sorted.
    ///
    /// Budgets: communities smaller than <see cref="CommunityDetectionOptions.MinCommunitySize"/>
    /// are dropped as noise (counted, not persisted); communities beyond
    /// <see cref="CommunityDetectionOptions.MaxCommunities"/> are truncated by descending size
    /// (counted, not persisted). Only nodes present in ck_symbol become ck_member rows.
    /// </summary>
    public static async Task<CommunityDetectionResult> ComputeCommunitiesAsync(
        this CozoOm om,
        CommunityDetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        options ??= new CommunityDetectionOptions();
        if (!string.Equals(options.Algorithm, "louvain", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported community detection algorithm '{options.Algorithm}'. Supported: louvain.",
                nameof(options));
        }

        var parameters = new Dictionary<string, object?> { ["min_confidence"] = options.MinConfidence };

        // 1. Louvain partition: node -> raw community key (dot-joined hierarchy labels; "0" when
        //    the graph is too small for Louvain to produce any hierarchy level).
        var louvain = await om.Runtime.Store.RunAsync(
            CodeGraphProjections.ClusterInputMinConfidence +
            """

            communities[] <~ CommunityDetectionLouvain(cluster_input[])
            ?[node, labels] := communities[labels, node]
            """,
            parameters,
            cancellationToken: cancellationToken);
        var nodeCommunity = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in louvain.Rows)
        {
            var node = AsString(row[0]);
            if (node.Length == 0)
            {
                continue;
            }

            var labels = row[1].ValueKind == JsonValueKind.Array
                ? row[1].EnumerateArray().Select(l => l.ToString()).ToArray()
                : [];
            nodeCommunity[node] = labels.Length == 0 ? "0" : string.Join(".", labels);
        }

        // 2. Cluster edges: weighted degree per node plus intra/boundary weight per raw community.
        var edgeRows = await om.Runtime.Store.RunAsync(
            CodeGraphProjections.ClusterInputMinConfidence + "\n?[from, to, weight] := cluster_input[from, to, weight]",
            parameters,
            cancellationToken: cancellationToken);
        var degree = new Dictionary<string, double>(StringComparer.Ordinal);
        var intraWeight = new Dictionary<string, double>(StringComparer.Ordinal);
        var boundaryWeight = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in edgeRows.Rows)
        {
            var from = AsString(row[0]);
            var to = AsString(row[1]);
            var weight = row[2].ValueKind == JsonValueKind.Number ? row[2].GetDouble() : 0.0;
            degree[from] = degree.GetValueOrDefault(from) + weight;
            degree[to] = degree.GetValueOrDefault(to) + weight;
            var fromCommunity = nodeCommunity.GetValueOrDefault(from);
            var toCommunity = nodeCommunity.GetValueOrDefault(to);
            if (fromCommunity is not null && fromCommunity == toCommunity)
            {
                intraWeight[fromCommunity] = intraWeight.GetValueOrDefault(fromCommunity) + weight;
            }
            else
            {
                if (fromCommunity is not null)
                {
                    boundaryWeight[fromCommunity] = boundaryWeight.GetValueOrDefault(fromCommunity) + weight;
                }

                if (toCommunity is not null)
                {
                    boundaryWeight[toCommunity] = boundaryWeight.GetValueOrDefault(toCommunity) + weight;
                }
            }
        }

        // 3. Symbols: display name and qualified name (sym_key) per symbol; total count for budget.
        var symbolRows = await om.Runtime.Store.RunAsync(
            "?[symbol_id, name, sym_key] := *ck_symbol{ symbol_id, name, sym_key }",
            cancellationToken: cancellationToken);
        var symbolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var symbolQualifiedNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in symbolRows.Rows)
        {
            var symbolId = AsString(row[0]);
            symbolNames[symbolId] = AsString(row[1]);
            symbolQualifiedNames[symbolId] = NormalizeQualifiedName(AsString(row[2]));
        }

        // 4. Group symbol members by raw community; apply the min-size noise threshold.
        var noiseSymbols = 0;
        var rawCommunities = new List<(string RawKey, string[] Members)>();
        foreach (var group in nodeCommunity
                     .Where(kv => symbolNames.ContainsKey(kv.Key))
                     .GroupBy(kv => kv.Value, StringComparer.Ordinal))
        {
            var members = group.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray();
            if (members.Length < options.MinCommunitySize)
            {
                noiseSymbols += members.Length;
                continue;
            }

            rawCommunities.Add((group.Key, members));
        }

        // 5. Canonical ordering (size desc, smallest member symbolId asc) + budget truncation.
        rawCommunities.Sort((a, b) =>
        {
            var bySize = b.Members.Length.CompareTo(a.Members.Length);
            return bySize != 0 ? bySize : string.CompareOrdinal(a.Members[0], b.Members[0]);
        });
        var maxCommunities = options.MaxCommunities
            ?? Math.Max(10, Math.Min(200, symbolNames.Count / 20));
        var truncatedCommunities = Math.Max(0, rawCommunities.Count - maxCommunities);
        if (truncatedCommunities > 0)
        {
            rawCommunities.RemoveRange(maxCommunities, truncatedCommunities);
        }

        // 6. Labels + canonical ids.
        var labelCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var communities = new List<CodeCommunitySummary>(rawCommunities.Count);
        var memberCommunity = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < rawCommunities.Count; i++)
        {
            var (rawKey, members) = rawCommunities[i];
            var communityId = $"community:{i + 1:000}";
            var intra = intraWeight.GetValueOrDefault(rawKey);
            var boundary = boundaryWeight.GetValueOrDefault(rawKey);
            var cohesion = intra + boundary > 0.0 ? intra / (intra + boundary) : 1.0;
            var label = ComputeLabel(members, symbolQualifiedNames, symbolNames, degree, communityId);
            var seen = labelCounts.GetValueOrDefault(label) + 1;
            labelCounts[label] = seen;
            if (seen > 1)
            {
                label = $"{label}#{seen}";
            }

            communities.Add(new CodeCommunitySummary(communityId, label, cohesion, members.Length, options.Algorithm));
            foreach (var member in members)
            {
                memberCommunity[member] = communityId;
            }
        }

        // 7. Persist (replace previous contents).
        await using var tx = await om.Runtime.Store.BeginTransactionAsync(write: true, cancellationToken);
        await tx.RunAsync(
            """
            ?[community_id, label, cohesion, symbol_count, algo] <- $rows
            :replace ck_community {community_id => label, cohesion, symbol_count, algo}
            """,
            new Dictionary<string, object?>
            {
                ["rows"] = communities.Select(c => new object?[] { c.CommunityId, c.Label, c.Cohesion, c.SymbolCount, c.Algo }).ToList(),
            },
            cancellationToken: cancellationToken);
        await tx.RunAsync(
            """
            ?[symbol_id, community_id] <- $rows
            :replace ck_member {symbol_id => community_id}
            """,
            new Dictionary<string, object?>
            {
                ["rows"] = rawCommunities
                    .SelectMany(c => c.Members)
                    .Select(member => new object?[] { member, memberCommunity[member] })
                    .ToList(),
            },
            cancellationToken: cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new CommunityDetectionResult(communities, memberCommunity, noiseSymbols, truncatedCommunities);
    }

    /// <summary>Lists persisted communities (ck_community) in canonical community_id order.</summary>
    public static async Task<IReadOnlyList<CodeCommunitySummary>> ListCommunitiesAsync(
        this CozoOm om,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[community_id, label, cohesion, symbol_count, algo] :=
              *ck_community{ community_id, label, cohesion, symbol_count, algo }
            :sort community_id
            """,
            cancellationToken: cancellationToken);
        return rows.Rows.Select(ReadCommunitySummary).ToArray();
    }

    /// <summary>Returns the sorted member symbol ids of a persisted community (empty if unknown).</summary>
    public static async Task<IReadOnlyList<string>> GetCommunityMembersAsync(
        this CozoOm om,
        string communityId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentException.ThrowIfNullOrEmpty(communityId);
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[symbol_id] := *ck_member{ symbol_id, community_id: $community_id }
            :sort symbol_id
            """,
            new Dictionary<string, object?> { ["community_id"] = communityId },
            cancellationToken: cancellationToken);
        return rows.Rows.Select(row => AsString(row[0])).ToArray();
    }

    /// <summary>Returns the community a symbol belongs to, or null when it is not a member.</summary>
    public static async Task<CodeCommunitySummary?> FindSymbolCommunityAsync(
        this CozoOm om,
        string symbolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentException.ThrowIfNullOrEmpty(symbolId);
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[community_id, label, cohesion, symbol_count, algo] :=
              *ck_member{ symbol_id: $symbol_id, community_id },
              *ck_community{ community_id, label, cohesion, symbol_count, algo }
            """,
            new Dictionary<string, object?> { ["symbol_id"] = symbolId },
            cancellationToken: cancellationToken);
        return rows.Rows.Count == 0 ? null : ReadCommunitySummary(rows.Rows[0]);
    }

    /// <summary>
    /// Traces the shortest call path (hop count) from <paramref name="fromSymbolId"/> to
    /// <paramref name="toSymbolId"/> over the <see cref="CodeGraphProjections.CallGraph"/>
    /// projection using Cozo's ShortestPathDijkstra fixed rule (track add-llm-wiki-trace-and-check
    /// design §1). Edges below <see cref="TraceOptions.MinConfidence"/> are excluded; a shortest
    /// path longer than <see cref="TraceOptions.MaxDepth"/> hops is reported as not found
    /// (post-filter — the fixed rule has no depth parameter). Each hop carries the
    /// highest-confidence call site of its symbol pair plus the symbol names. Never throws for
    /// "no path": <see cref="TraceResult.Found"/> is false with a <see cref="TraceResult.Reason"/>.
    /// </summary>
    public static async Task<TraceResult> TraceCallPathAsync(
        this CozoOm om,
        string fromSymbolId,
        string toSymbolId,
        TraceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentException.ThrowIfNullOrEmpty(fromSymbolId);
        ArgumentException.ThrowIfNullOrEmpty(toSymbolId);
        options ??= new TraceOptions();

        // ShortestPathDijkstra input: [from, to] (edge weight defaults to 1.0 → hop-count
        // shortest path); output: [start, goal, cost, path]. A goal that is absent from the
        // projected graph yields no row; a present-but-unreachable goal yields an empty path.
        var rows = await om.Runtime.Store.RunAsync(
            CodeGraphProjections.CallGraph +
            """

            starting[node] := node = $from
            goals[node] := node = $to
            res[] <~ ShortestPathDijkstra(call_graph[], starting[], goals[])
            ?[path] := res[_src, _dst, _cost, path]
            """,
            new Dictionary<string, object?>
            {
                ["min_confidence"] = options.MinConfidence,
                ["from"] = fromSymbolId,
                ["to"] = toSymbolId,
            },
            cancellationToken: cancellationToken);

        var noPathReason = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"no CALLS path from '{fromSymbolId}' to '{toSymbolId}' at MinConfidence={options.MinConfidence}");
        var path = rows.Rows.Count == 0 || rows.Rows[0][0].ValueKind != JsonValueKind.Array
            ? []
            : rows.Rows[0][0].EnumerateArray().Select(AsString).ToArray();
        if (path.Length == 0)
        {
            return new TraceResult(false, [], noPathReason);
        }

        if (path.Length == 1)
        {
            return new TraceResult(true, [], $"'{fromSymbolId}' and '{toSymbolId}' are the same symbol; zero hops");
        }

        var hopCount = path.Length - 1;
        if (hopCount > options.MaxDepth)
        {
            return new TraceResult(
                false,
                [],
                $"shortest CALLS path has {hopCount} hops, exceeding MaxDepth={options.MaxDepth}");
        }

        // Hop assembly: per consecutive pair pick the highest-confidence call site
        // (ties: file_id, then line ascending — deterministic), then decorate with names.
        var pathIds = path.Distinct(StringComparer.Ordinal).ToList();
        var edgeRows = await om.Runtime.Store.RunAsync(
            """
            ?[from_id, to_id, file_id, line, confidence] :=
              *ck_edge{ from_id, to_id, kind: "CALLS", file_id, line, confidence },
              is_in(from_id, $ids), is_in(to_id, $ids), confidence >= $min_confidence
            """,
            new Dictionary<string, object?>
            {
                ["ids"] = pathIds,
                ["min_confidence"] = options.MinConfidence,
            },
            cancellationToken: cancellationToken);
        var bestSite = new Dictionary<(string From, string To), (string FileId, int Line, double Confidence)>();
        foreach (var row in edgeRows.Rows)
        {
            var key = (AsString(row[0]), AsString(row[1]));
            var site = (
                FileId: AsString(row[2]),
                Line: row[3].ValueKind == JsonValueKind.Number ? row[3].GetInt32() : 0,
                Confidence: row[4].ValueKind == JsonValueKind.Number ? row[4].GetDouble() : 0.0);
            if (!bestSite.TryGetValue(key, out var current)
                || site.Confidence > current.Confidence
                || (site.Confidence == current.Confidence
                    && (string.CompareOrdinal(site.FileId, current.FileId) < 0
                        || (site.FileId == current.FileId && site.Line < current.Line))))
            {
                bestSite[key] = site;
            }
        }

        var nameRows = await om.Runtime.Store.RunAsync(
            "?[symbol_id, name] := *ck_symbol{ symbol_id, name }, is_in(symbol_id, $ids)",
            new Dictionary<string, object?> { ["ids"] = pathIds },
            cancellationToken: cancellationToken);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in nameRows.Rows)
        {
            names[AsString(row[0])] = AsString(row[1]);
        }

        var hops = new List<TraceHop>(hopCount);
        for (var i = 0; i < hopCount; i++)
        {
            var from = path[i];
            var to = path[i + 1];
            var site = bestSite.GetValueOrDefault((from, to));
            hops.Add(new TraceHop(
                from,
                names.GetValueOrDefault(from, ""),
                to,
                names.GetValueOrDefault(to, ""),
                site.FileId ?? "",
                site.Line,
                CodeEdgeKinds.Calls,
                site.Confidence));
        }

        return new TraceResult(true, hops);
    }

    /// <summary>
    /// Runs StronglyConnectedComponents over the IMPORTS and/or CALLS projection and returns the
    /// components with more than one member as cycles (track add-llm-wiki-trace-and-check design
    /// §2; formalizes the P0 internal import-cycle preview). CALLS cycles use the full CALLS
    /// graph (min_confidence 0.0). Deterministic output: members are canonicalized by walking
    /// the component's edges from the smallest member id (smallest unvisited successor first),
    /// and cycles are ordered by (kind, first member).
    /// </summary>
    public static async Task<IReadOnlyList<CodeCycle>> DetectCyclesAsync(
        this CozoOm om,
        CycleKind kind = CycleKind.Import,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        var cycles = new List<CodeCycle>();
        if (kind is CycleKind.Import or CycleKind.Both)
        {
            cycles.AddRange(await DetectCyclesOverProjectionAsync(
                om, CodeGraphProjections.ImportGraph, "import_graph", CodeEdgeKinds.Imports, cancellationToken));
        }

        if (kind is CycleKind.Calls or CycleKind.Both)
        {
            cycles.AddRange(await DetectCyclesOverProjectionAsync(
                om, CodeGraphProjections.CallGraph, "call_graph", CodeEdgeKinds.Calls, cancellationToken));
        }

        cycles.Sort((a, b) =>
        {
            var byKind = string.CompareOrdinal(a.Kind, b.Kind);
            return byKind != 0 ? byKind : string.CompareOrdinal(a.Members[0], b.Members[0]);
        });
        return cycles;
    }

    private static async Task<IReadOnlyList<CodeCycle>> DetectCyclesOverProjectionAsync(
        CozoOm om,
        string projection,
        string relationName,
        string edgeKind,
        CancellationToken cancellationToken)
    {
        // $min_confidence is only referenced by the CALLS projection; the unused binding for
        // import_graph is harmless. 0.0 = full graph (cycle detection is structural).
        var parameters = new Dictionary<string, object?> { ["min_confidence"] = 0.0 };
        var sccRows = await om.Runtime.Store.RunAsync(
            projection +
            $"""

            scc[] <~ StronglyConnectedComponents({relationName}[])
            ?[node, grp] := scc[node, grp]
            """,
            parameters,
            cancellationToken: cancellationToken);
        var components = sccRows.Rows
            .GroupBy(row => row[1].ToString(), StringComparer.Ordinal)
            .Select(group => group.Select(row => AsString(row[0])).Order(StringComparer.Ordinal).ToArray())
            .Where(members => members.Length > 1)
            .ToArray();
        if (components.Length == 0)
        {
            return [];
        }

        var edgeRows = await om.Runtime.Store.RunAsync(
            projection + $"\n?[from, to] := {relationName}[from, to]",
            parameters,
            cancellationToken: cancellationToken);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in edgeRows.Rows)
        {
            var from = AsString(row[0]);
            if (!adjacency.TryGetValue(from, out var targets))
            {
                adjacency[from] = targets = [];
            }

            targets.Add(AsString(row[1]));
        }

        return components
            .Select(members => new CodeCycle(edgeKind, OrderCycleMembers(members, adjacency)))
            .ToArray();
    }

    /// <summary>
    /// Canonical cycle order: start at the smallest member id, greedily follow in-component
    /// edges (smallest unvisited successor first). For a simple cycle this yields the actual
    /// path rotated to the smallest id; for denser components any unreached members are
    /// appended in sorted order. Deterministic either way.
    /// </summary>
    private static string[] OrderCycleMembers(
        string[] sortedMembers,
        IReadOnlyDictionary<string, List<string>> adjacency)
    {
        var memberSet = new HashSet<string>(sortedMembers, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>(sortedMembers.Length);
        var current = sortedMembers[0];
        while (current is not null && visited.Add(current))
        {
            ordered.Add(current);
            current = adjacency.TryGetValue(current, out var targets)
                ? targets
                    .Where(t => memberSet.Contains(t) && !visited.Contains(t))
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault()
                : null;
        }

        ordered.AddRange(sortedMembers.Where(m => !visited.Contains(m)));
        return [.. ordered];
    }

    /// <summary>
    /// Longest common dot-separated qualified-name prefix (&gt;= 1 segment) of the members;
    /// fallback: name of the highest weighted-degree member (ties broken by smallest symbolId);
    /// last resort: the community id.
    /// </summary>
    private static string ComputeLabel(
        IReadOnlyList<string> members,
        IReadOnlyDictionary<string, string> qualifiedNames,
        IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, double> degree,
        string communityId)
    {
        var segmentLists = members
            .Select(m => qualifiedNames.GetValueOrDefault(m, ""))
            .Where(q => q.Length > 0)
            .Select(q => q.Split('.'))
            .ToArray();
        if (segmentLists.Length == members.Count && segmentLists.Length > 0)
        {
            var prefixLength = segmentLists.Min(s => s.Length);
            for (var i = 0; i < prefixLength; i++)
            {
                var segment = segmentLists[0][i];
                if (segmentLists.Any(s => !string.Equals(s[i], segment, StringComparison.Ordinal)))
                {
                    prefixLength = i;
                    break;
                }
            }

            if (prefixLength >= 1)
            {
                return string.Join(".", segmentLists[0].Take(prefixLength));
            }
        }

        var topMember = members
            .OrderByDescending(m => degree.GetValueOrDefault(m))
            .ThenBy(m => m, StringComparer.Ordinal)
            .First();
        var name = names.GetValueOrDefault(topMember, "");
        return name.Length > 0 ? name : communityId;
    }

    /// <summary>
    /// Strips the lang prefix ("csharp:Demo.Program.Caller/0" → "Demo.Program.Caller/0") and the
    /// arity suffix ("/0" or "#0") from a sym_key, yielding the dot-separated qualified name.
    /// </summary>
    private static string NormalizeQualifiedName(string symKey)
    {
        if (symKey.Length == 0)
        {
            return "";
        }

        var value = symKey;
        var colon = value.IndexOf(':');
        if (colon >= 0)
        {
            value = value[(colon + 1)..];
        }

        var aritySeparator = value.LastIndexOfAny(['/', '#']);
        if (aritySeparator > 0 && value[(aritySeparator + 1)..].All(char.IsAsciiDigit))
        {
            value = value[..aritySeparator];
        }

        return value;
    }

    private static CodeCommunitySummary ReadCommunitySummary(IReadOnlyList<JsonElement> row) =>
        new(
            AsString(row[0]),
            AsString(row[1]),
            row[2].ValueKind == JsonValueKind.Number ? row[2].GetDouble() : 0.0,
            row[3].ValueKind == JsonValueKind.Number ? row[3].GetInt32() : 0,
            AsString(row[4]));

    private static string AsString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
}
