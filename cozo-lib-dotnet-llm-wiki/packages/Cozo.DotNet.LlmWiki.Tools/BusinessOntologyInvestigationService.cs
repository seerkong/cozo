using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tools;

/// <summary>
/// Read-only, bounded views over indexed code facts and the current ontology generation.
/// This is deliberately not a generic database-query facade.
/// </summary>
public sealed class BusinessOntologyInvestigationService(CozoOm om, BusinessOntologyStore store)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    public const int MaxEvidencePageSize = 500;
    public const int MaxTopologyEntries = 20;
    public const int MaxTopologySubjects = 160;
    public const int MaxTopologyClaims = 240;
    public const int MaxTopologyCallEdges = 160;
    public const int MaxTopologyEdges = (MaxTopologyEntries * 2) + MaxTopologyCallEdges + (MaxTopologyClaims * 2);
    private const int RelationReadPageSize = 512;
    // Cursors are continuation tokens, not authentication credentials. A stable protocol key makes
    // a cursor returned by one CLI process usable by the next process.
    private static readonly byte[] CursorKey = SHA256.HashData(Encoding.UTF8.GetBytes("cozo.llm-wiki.investigation.cursor.v1"));

    public async Task<BusinessOntologyInvestigationOverview> GetOverviewAsync(
        string? ontologyId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOptionalOntologyId(ontologyId);
        var relations = await RelationNamesAsync(cancellationToken);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var relation in new[]
                 {
                     "ck_repo", "ck_file", "ck_symbol", "ck_semantic_claim", "ck_entry_point", "ck_process",
                     "onto_concept", "onto_relation", "onto_rule", "onto_lifecycle", "onto_candidate",
                 })
        {
            counts[relation] = relations.Contains(relation)
                ? await CountAsync(relation, cancellationToken)
                : 0;
        }

        string? generation = null;
        if (!string.IsNullOrWhiteSpace(ontologyId) && relations.Contains("onto_generation"))
        {
            generation = await ActiveGenerationAsync(ontologyId!, cancellationToken);
        }

        return new BusinessOntologyInvestigationOverview(
            ontologyId,
            generation,
            counts,
            [
                "find_business_terms", "list_use_case_slices", "get_use_case_slice",
                "list_semantic_evidence", "find_semantic_patterns", "get_semantic_evidence", "inspect_ontology_subject",
                "discover_domain_charters", "list_cross_layer_use_cases",
                "find_state_rule_clusters", "find_implementation_clusters",
                "get_domain_topology",
            ],
            Digest("ontology_investigation_overview", new { ontologyId, generation, counts }));
    }

    public async Task<BusinessOntologyInvestigationPage<BusinessTermHit>> FindBusinessTermsAsync(
        string term,
        string? ontologyId = null,
        string? cursor = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedTerm = ValidateTerm(term);
        ValidateOptionalOntologyId(ontologyId);
        var pageSize = NormalizeLimit(limit);
        var filter = $"term={normalizedTerm}|ontology={ontologyId ?? ""}";
        var after = ReadCursor("find_business_terms", filter, cursor);
        var hits = new List<BusinessTermHit>();

        foreach (var claim in await ReadClaimsAsync(cancellationToken))
        {
            if (Contains(claim.SubjectId, normalizedTerm) || Contains(claim.Symbol, normalizedTerm)
                || Contains(claim.PayloadJson, normalizedTerm) || Contains(claim.Evidence, normalizedTerm))
            {
                hits.Add(new BusinessTermHit(
                    "semantic-claim", claim.ClaimId, claim.Symbol, claim.Kind,
                    claim.Repository, claim.Path, claim.StartLine, claim.EndLine,
                    [ToEvidenceRef(claim)], claim.Confidence, "source-fact"));
            }
        }

        foreach (var symbol in await ReadSymbolsAsync(cancellationToken))
        {
            if (Contains(symbol.Name, normalizedTerm))
            {
                hits.Add(new BusinessTermHit(
                    "symbol", symbol.SymbolId, symbol.Name, symbol.Kind,
                    symbol.Repository, symbol.Path, symbol.StartLine, symbol.EndLine,
                    [], 0, "source-fact"));
            }
        }

        if (!string.IsNullOrWhiteSpace(ontologyId))
        {
            var snapshot = await ReadSnapshotIfPresentAsync(ontologyId!, cancellationToken);
            if (snapshot is not null)
            {
                hits.AddRange(snapshot.Concepts
                    .Where(item => Contains(item.Id, normalizedTerm) || Contains(item.Label, normalizedTerm) || Contains(item.Description, normalizedTerm))
                    .Select(item => new BusinessTermHit("concept", item.Id, item.Label, item.Kind, "", "", 0, 0,
                        item.EvidenceIds.Select(id => new BusinessOntologyInvestigationEvidenceRef(id, "", "", "", 0, 0)).ToArray(), item.Confidence, item.Status)));
            }
        }

        var ordered = hits
            .GroupBy(item => item.Kind + "\u001f" + item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        return Page("find_business_terms", filter, after, pageSize, ordered, item => $"{item.Kind}\u001f{item.Id}");
    }

    /// <summary>
    /// Produces evidence-backed domain-charter seeds from CodeKnowledge only. The result is
    /// deliberately a navigation aid, not an ontology proposal or an analysis-workspace record.
    /// </summary>
    public async Task<BusinessOntologyInvestigationPage<BusinessOntologyDiscoveredDomainCharter>> DiscoverDomainChartersAsync(
        string term,
        string? cursor = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedTerm = ValidateTerm(term);
        var pageSize = NormalizeLimit(limit);
        var filter = $"term={normalizedTerm}";
        var after = ReadCursor("discover_domain_charters", filter, cursor);
        var graph = await ReadInvestigationGraphAsync(cancellationToken);
        var domainSeed = NormalizeDomainSeed(normalizedTerm);
        var charters = MatchingEntries(graph, normalizedTerm)
            .Select(entry => CharterForEntry(entry, domainSeed, graph))
            .Where(item => item.EvidenceIds.Count > 0)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        return Page("discover_domain_charters", filter, after, pageSize, charters, item => item.Id);
    }

    /// <summary>
    /// Lists route-to-implementation slices rooted either at one known entry symbol or at a
    /// literal domain seed. It intentionally has no ontology-id input.
    /// </summary>
    public async Task<BusinessOntologyInvestigationPage<BusinessOntologyCrossLayerUseCase>> ListCrossLayerUseCasesAsync(
        string? entrySymbolId = null,
        string? domainSeed = null,
        string? cursor = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var hasEntry = !string.IsNullOrWhiteSpace(entrySymbolId);
        var hasDomain = !string.IsNullOrWhiteSpace(domainSeed);
        if (hasEntry == hasDomain)
        {
            throw new ArgumentException("Specify exactly one of entrySymbolId or domainSeed.");
        }

        var normalizedEntry = hasEntry ? ValidateEntrySymbolId(entrySymbolId!) : null;
        var normalizedDomain = hasDomain ? ValidateTerm(domainSeed!) : null;
        var pageSize = NormalizeLimit(limit);
        var filter = $"entry={normalizedEntry ?? ""}|domain={normalizedDomain ?? ""}";
        var after = ReadCursor("list_cross_layer_use_cases", filter, cursor);
        var graph = await ReadInvestigationGraphAsync(cancellationToken);
        var entries = normalizedEntry is not null
            ? graph.EntryPoints.Where(item => item.SymbolId == normalizedEntry)
            : MatchingEntries(graph, normalizedDomain!);
        var useCases = entries
            .Select(entry => UseCaseForEntry(entry, NormalizeDomainSeed(normalizedDomain ?? entry.SymbolId), graph))
            .Where(item => item.EvidenceIds.Count > 0)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        return Page("list_cross_layer_use_cases", filter, after, pageSize, useCases, item => item.Id);
    }

    /// <summary>
    /// Groups indexed state, guard, validation, and persistence claims by their deterministic
    /// subject. Call paths are present only when a real entry point reaches that subject. No
    /// source files or ontology generations are read.
    /// </summary>
    public async Task<BusinessOntologyInvestigationPage<BusinessOntologyStateRuleCluster>> FindStateRuleClustersAsync(
        string term,
        string? cursor = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedTerm = ValidateTerm(term);
        var pageSize = NormalizeLimit(limit);
        var filter = $"term={normalizedTerm}";
        var after = ReadCursor("find_state_rule_clusters", filter, cursor);
        var graph = await ReadInvestigationGraphAsync(cancellationToken);
        var stateClaims = graph.Claims
            .Where(item => item.Kind is CodeSemanticClaimKinds.StateField
                or CodeSemanticClaimKinds.StateValue
                or CodeSemanticClaimKinds.StateAssignment)
            .Where(item => ClaimMatchesTerm(item, normalizedTerm))
            .OrderBy(item => item.ClaimId, StringComparer.Ordinal)
            .ToArray();
        var clusters = new List<BusinessOntologyStateRuleCluster>();
        foreach (var subjectId in stateClaims.Select(item => item.SubjectId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var subjectClaims = graph.Claims
                .Where(item => item.SubjectId == subjectId)
                .Where(item => item.Kind is CodeSemanticClaimKinds.StateField
                    or CodeSemanticClaimKinds.StateValue
                    or CodeSemanticClaimKinds.StateAssignment
                    or CodeSemanticClaimKinds.BusinessGuard
                    or CodeSemanticClaimKinds.ValidationConstraint
                    or CodeSemanticClaimKinds.PersistenceConstraint)
                .OrderBy(item => item.ClaimId, StringComparer.Ordinal)
                .ToArray();
            if (!subjectClaims.Any(item => item.Kind is CodeSemanticClaimKinds.StateField
                or CodeSemanticClaimKinds.StateValue
                or CodeSemanticClaimKinds.StateAssignment))
            {
                continue;
            }

            var matchingEntries = graph.EntryPoints
                .Select(entry => new { Entry = entry, Symbols = WalkSymbols(entry.SymbolId, graph) })
                .Where(item => item.Symbols.Contains(subjectId, StringComparer.Ordinal))
                .OrderBy(item => item.Entry.SymbolId, StringComparer.Ordinal)
                .ToArray();
            if (matchingEntries.Length == 0)
            {
                clusters.Add(StateRuleCluster(subjectId, normalizedTerm, "", [], subjectClaims));
                continue;
            }

            foreach (var scope in matchingEntries)
            {
                clusters.Add(StateRuleCluster(subjectId, normalizedTerm, scope.Entry.SymbolId, CallPath(scope.Symbols, graph), subjectClaims));
            }
        }

        var ordered = clusters
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        return Page("find_state_rule_clusters", filter, after, pageSize, ordered, item => item.Id);
    }

    /// <summary>
    /// Builds a pending semantic-cluster-shaped anchor group from directly indexed evidence. The
    /// cluster carries no accepted ontology identity and does not publish or persist anything.
    /// </summary>
    public async Task<BusinessOntologyInvestigationPage<BusinessOntologyImplementationCluster>> FindImplementationClustersAsync(
        string? domainSeed = null,
        IReadOnlyList<string>? evidenceIds = null,
        string? cursor = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var hasDomain = !string.IsNullOrWhiteSpace(domainSeed);
        var hasEvidence = evidenceIds is { Count: > 0 };
        if (hasDomain == hasEvidence)
        {
            throw new ArgumentException("Specify exactly one of domainSeed or evidenceIds.");
        }

        var normalizedDomain = hasDomain ? ValidateTerm(domainSeed!) : null;
        var normalizedEvidenceIds = hasEvidence ? NormalizeEvidenceIds(evidenceIds!) : [];
        var pageSize = NormalizeLimit(limit);
        var filter = $"domain={normalizedDomain ?? ""}|evidence={string.Join(',', normalizedEvidenceIds)}";
        var after = ReadCursor("find_implementation_clusters", filter, cursor);
        var graph = await ReadInvestigationGraphAsync(cancellationToken);
        var claims = normalizedDomain is not null
            ? graph.Claims.Where(item => ClaimMatchesTerm(item, normalizedDomain)).ToArray()
            : graph.Claims.Where(item => normalizedEvidenceIds.Contains(item.ClaimId, StringComparer.Ordinal)).ToArray();
        if (normalizedEvidenceIds.Length > 0 && claims.Length != normalizedEvidenceIds.Length)
        {
            throw new ArgumentException("evidenceIds must refer to existing indexed semantic claims.", nameof(evidenceIds));
        }
        if (claims.Length == 0)
        {
            return Page("find_implementation_clusters", filter, after, pageSize, Array.Empty<BusinessOntologyImplementationCluster>(), item => item.Id);
        }

        var seed = NormalizeDomainSeed(normalizedDomain ?? "evidence");
        var evidenceByAnchor = claims
            .Where(item => graph.Symbols.TryGetValue(item.SubjectId, out var symbol) && symbol.Path == item.Path)
            .GroupBy(item => (item.SubjectId, item.Path))
            .ToDictionary(group => group.Key, group => group.Select(item => item.ClaimId).Order(StringComparer.Ordinal).ToArray());
        var anchorSymbols = graph.Symbols.Values
            .Where(item => evidenceByAnchor.ContainsKey((item.SymbolId, item.Path)))
            .OrderBy(item => item.Repository, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.SymbolId, StringComparer.Ordinal)
            .ToArray();
        var anchors = anchorSymbols
            .Select(item => new BusinessOntologySemanticImplementationAnchor(
                item.SymbolId,
                RolesForSymbol(item.SymbolId, graph).FirstOrDefault() ?? "unclassified",
                item.Path,
                evidenceByAnchor[(item.SymbolId, item.Path)][0]))
            .ToArray();
        var clusterId = "implementation-cluster:" + Digest("implementation-cluster", new
        {
            seed,
            symbols = anchors.Select(item => item.SymbolId).ToArray(),
            evidenceIds = claims.Select(item => item.ClaimId).Order(StringComparer.Ordinal).ToArray(),
        })[..24];
        var cluster = new BusinessOntologyImplementationCluster(
            clusterId,
            seed,
            new BusinessOntologySemanticCluster(
                clusterId,
                "domain:" + seed,
                "pending:" + seed,
                seed,
                "Deterministic implementation anchors; semantic naming remains pending.",
                anchors),
            claims.Select(item => item.ClaimId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            EvidenceRefsFor(claims));
        return Page("find_implementation_clusters", filter, after, pageSize, [cluster], item => item.Id);
    }

    /// <summary>
    /// Returns a bounded graph for visually navigating indexed evidence from a literal domain
    /// term. It is a topology projection, not an ontology proposal or a semantic conclusion.
    /// </summary>
    public async Task<BusinessOntologyDomainTopology> GetDomainTopologyAsync(
        string term,
        CancellationToken cancellationToken = default)
    {
        var normalizedTerm = ValidateTerm(term);
        var graph = await ReadInvestigationGraphAsync(cancellationToken);
        var rankedEntries = MatchingEntries(graph, normalizedTerm)
            .Select(entry => new
            {
                Entry = entry,
                DirectObservationCount = ClaimsForSymbols(WalkSymbols(entry.SymbolId, graph), graph).Count,
            })
            .OrderByDescending(item => item.DirectObservationCount)
            .ThenBy(item => item.Entry.SymbolId, StringComparer.Ordinal)
            .ToArray();
        var entries = rankedEntries
            .Take(MaxTopologyEntries)
            .Select(item => item.Entry)
            .ToArray();
        var truncatedEntries = rankedEntries.Length > MaxTopologyEntries;
        var selectedSubjects = new List<string>();
        foreach (var entry in entries)
        {
            foreach (var symbolId in WalkTopologySymbols(entry.SymbolId, graph))
            {
                if (!selectedSubjects.Contains(symbolId, StringComparer.Ordinal)) selectedSubjects.Add(symbolId);
                if (selectedSubjects.Count >= MaxTopologySubjects) break;
            }
            if (selectedSubjects.Count >= MaxTopologySubjects) break;
        }

        var subjects = selectedSubjects.Order(StringComparer.Ordinal).ToArray();
        var subjectIds = subjects.ToHashSet(StringComparer.Ordinal);
        var claims = ClaimsForSymbols(subjects, graph).Take(MaxTopologyClaims).ToArray();
        var edges = new List<BusinessOntologyTopologyEdge>();
        var domainId = "domain:" + Digest("domain-topology", new { term = normalizedTerm })[..24];
        foreach (var entry in entries)
        {
            edges.Add(new BusinessOntologyTopologyEdge(domainId, "entry:" + entry.SymbolId, "ENTRY_POINT"));
            if (subjectIds.Contains(entry.SymbolId))
            {
                edges.Add(new BusinessOntologyTopologyEdge("entry:" + entry.SymbolId, "subject:" + entry.SymbolId, "ENTRY_ROOT"));
            }
        }
        var callEdges = subjects.SelectMany(subject => graph.CallsFrom.GetValueOrDefault(subject, [])
                .Where(subjectIds.Contains)
                .Select(target => new BusinessOntologyTopologyEdge("subject:" + subject, "subject:" + target, CodeEdgeKinds.Calls)))
            .Distinct()
            .OrderBy(item => item.FromId, StringComparer.Ordinal)
            .ThenBy(item => item.ToId, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ToArray();
        var truncatedCalls = callEdges.Length > MaxTopologyCallEdges;
        edges.AddRange(callEdges.Take(MaxTopologyCallEdges));
        foreach (var claim in claims)
        {
            edges.Add(new BusinessOntologyTopologyEdge("subject:" + claim.SubjectId, "claim:" + claim.ClaimId, "SEMANTIC_CLAIM"));
            edges.Add(new BusinessOntologyTopologyEdge("claim:" + claim.ClaimId, "evidence:" + claim.ClaimId, "EVIDENCE"));
        }

        var canonicalEdges = edges.Distinct().OrderBy(item => item.FromId, StringComparer.Ordinal)
            .ThenBy(item => item.ToId, StringComparer.Ordinal).ThenBy(item => item.Kind, StringComparer.Ordinal).ToArray();
        var degree = canonicalEdges.SelectMany(item => new[] { item.FromId, item.ToId })
            .GroupBy(item => item, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var inDegree = canonicalEdges.GroupBy(item => item.ToId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var outDegree = canonicalEdges.GroupBy(item => item.FromId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var claimsBySubject = claims.GroupBy(item => item.SubjectId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var nodes = new List<TopologyNodeSeed>
        {
            new(domainId, "domain", "domain", NormalizeDomainSeed(normalizedTerm), normalizedTerm, claims.Length),
        };
        nodes.AddRange(entries.Select(entry => new TopologyNodeSeed("entry:" + entry.SymbolId, "entry-point", entry.Kind, entry.SymbolId, entry.Metadata, 0)));
        nodes.AddRange(subjects.Select(symbol =>
        {
            var value = graph.Symbols[symbol];
            return new TopologyNodeSeed("subject:" + symbol, "subject", value.Kind, value.Name, value.Path, claimsBySubject.GetValueOrDefault(symbol));
        }));
        nodes.AddRange(claims.Select(claim => new TopologyNodeSeed("claim:" + claim.ClaimId, "semantic-claim", claim.Kind, claim.Symbol, claim.PayloadJson, 1)));
        nodes.AddRange(claims.Select(claim => new TopologyNodeSeed("evidence:" + claim.ClaimId, "evidence", "indexed-evidence", claim.Path, claim.Repository, 0)));
        var topologyNodes = nodes.OrderBy(item => item.Layer, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new BusinessOntologyTopologyNode(
                item.Id, item.Layer, item.Kind, item.Label, item.Detail,
                inDegree.GetValueOrDefault(item.Id), outDegree.GetValueOrDefault(item.Id), degree.GetValueOrDefault(item.Id),
                item.DirectObservationCount, Math.Max(1, degree.GetValueOrDefault(item.Id) + item.DirectObservationCount)))
            .ToArray();
        var truncated = truncatedEntries || selectedSubjects.Count >= MaxTopologySubjects || truncatedCalls
            || ClaimsForSymbols(subjects, graph).Count > claims.Length;
        return new BusinessOntologyDomainTopology(
            NormalizeDomainSeed(normalizedTerm), topologyNodes, canonicalEdges, truncated,
            Digest("get_domain_topology", new { term = normalizedTerm, nodes = topologyNodes.Select(item => item.Id), edges = canonicalEdges }));
    }

    public async Task<BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern>> FindSemanticPatternsAsync(
        string kind,
        string? term = null,
        string? cursor = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (!CodeSemanticClaimKinds.IsSupported(kind))
        {
            throw new ArgumentException($"Unsupported semantic claim kind '{kind}'.", nameof(kind));
        }
        var normalizedTerm = string.IsNullOrWhiteSpace(term) ? null : ValidateTerm(term);
        var pageSize = NormalizeLimit(limit);
        var filter = $"kind={kind}|term={normalizedTerm ?? ""}";
        var after = ReadCursor("find_semantic_patterns", filter, cursor);
        var patterns = (await ReadClaimsAsync(cancellationToken))
            .Where(item => item.Kind == kind)
            .Where(item => normalizedTerm is null || Contains(item.SubjectId, normalizedTerm) || Contains(item.Symbol, normalizedTerm) || Contains(item.PayloadJson, normalizedTerm))
            .OrderBy(item => item.ClaimId, StringComparer.Ordinal)
            .Select(item => new BusinessOntologySemanticPattern(
                item.ClaimId, item.Kind, item.SubjectId, item.Symbol, item.Repository, item.Path,
                item.StartLine, item.EndLine, item.PayloadJson, item.Confidence, [ToEvidenceRef(item)]))
            .ToArray();
        return Page("find_semantic_patterns", filter, after, pageSize, patterns, item => item.ClaimId);
    }

    /// <summary>
    /// Enumerates every indexed semantic claim in deterministic claim-id order. This is the
    /// external analyzer's bulk evidence route: it remains bounded to one page per call and does
    /// not accept SQL, paths, source text, or mutation parameters.
    /// </summary>
    public async Task<BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern>> ListSemanticEvidenceAsync(
        string? term = null,
        string? cursor = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedTerm = string.IsNullOrWhiteSpace(term) ? null : ValidateTerm(term);
        var pageSize = NormalizeEvidenceLimit(limit);
        var filter = $"term={normalizedTerm ?? ""}";
        var after = ReadCursor("list_semantic_evidence", filter, cursor);

        // The no-term path stays server-paginated all the way through to avoid loading a large
        // corpus for every CLI page. A term filter remains a bounded convenience search over the
        // complete, keyset-paged underlying relation.
        if (normalizedTerm is null)
        {
            var rows = await ReadClaimPageAsync(after ?? "", pageSize + 1, cancellationToken);
            var items = rows.Take(pageSize).Select(ToPattern).ToArray();
            var truncated = rows.Count > items.Length;
            return new BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern>(
                items,
                truncated ? WriteCursor("list_semantic_evidence", filter, items[^1].ClaimId) : null,
                truncated,
                Digest("list_semantic_evidence", new { filter, returned = items.Length, truncated }));
        }

        var patterns = (await ReadClaimsAsync(cancellationToken))
            .Where(item => ClaimMatchesTerm(item, normalizedTerm))
            .OrderBy(item => item.ClaimId, StringComparer.Ordinal)
            .Select(ToPattern)
            .ToArray();
        return Page("list_semantic_evidence", filter, after, pageSize, patterns, item => item.ClaimId);
    }

    public async Task<SemanticEvidencePack> GetSemanticEvidenceAsync(
        IReadOnlyList<string> evidenceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidenceIds);
        if (evidenceIds.Count is < 1 or > SemanticEvidencePackBuilder.MaxAnchors)
        {
            throw new ArgumentException($"evidenceIds must contain 1..{SemanticEvidencePackBuilder.MaxAnchors} ids.", nameof(evidenceIds));
        }
        if (evidenceIds.Any(string.IsNullOrWhiteSpace) || evidenceIds.Distinct(StringComparer.Ordinal).Count() != evidenceIds.Count)
        {
            throw new ArgumentException("evidenceIds must be non-empty and unique.", nameof(evidenceIds));
        }

        var byId = (await ReadClaimsAsync(cancellationToken)).ToDictionary(item => item.ClaimId, StringComparer.Ordinal);
        var sources = evidenceIds.Select(id =>
        {
            if (!byId.TryGetValue(id, out var claim))
            {
                throw new ArgumentException($"Unknown indexed semantic evidence id '{id}'.", nameof(evidenceIds));
            }
            return new SemanticEvidenceAnchorSource(
                claim.ClaimId, claim.Repository, claim.RepositoryRoot, claim.Path, claim.SubjectId,
                claim.Kind, claim.PayloadJson, claim.StartLine, claim.EndLine, [], claim.Evidence);
        });
        return await new SemanticEvidencePackBuilder().BuildEvidencePackAsync(sources, cancellationToken);
    }

    /// <summary>
    /// Resolves indexed evidence identities to stable coordinates without opening source files or
    /// returning excerpts. Candidate publication uses this source-free projection.
    /// </summary>
    public async Task<IReadOnlyList<BusinessOntologySemanticEvidenceMetadata>> GetSemanticEvidenceMetadataAsync(
        IReadOnlyList<string> evidenceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidenceIds);
        if (evidenceIds.Count is < 1 or > SemanticEvidencePackBuilder.MaxAnchors)
        {
            throw new ArgumentException($"evidenceIds must contain 1..{SemanticEvidencePackBuilder.MaxAnchors} ids.", nameof(evidenceIds));
        }
        if (evidenceIds.Any(string.IsNullOrWhiteSpace) || evidenceIds.Distinct(StringComparer.Ordinal).Count() != evidenceIds.Count)
        {
            throw new ArgumentException("evidenceIds must be non-empty and unique.", nameof(evidenceIds));
        }
        var byId = (await ReadClaimsAsync(cancellationToken)).ToDictionary(item => item.ClaimId, StringComparer.Ordinal);
        return evidenceIds.Select(id =>
        {
            if (!byId.TryGetValue(id, out var claim))
            {
                throw new ArgumentException($"Unknown indexed semantic evidence id '{id}'.", nameof(evidenceIds));
            }
            return new BusinessOntologySemanticEvidenceMetadata(
                claim.ClaimId, claim.Repository, claim.Path, claim.SubjectId, claim.StartLine, claim.EndLine,
                claim.Resolver, claim.Confidence, claim.Kind);
        }).ToArray();
    }

    public async Task<BusinessOntologyInvestigationPage<BusinessUseCaseSlice>> ListUseCaseSlicesAsync(
        string ontologyId,
        string? cursor = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOntologyId(ontologyId);
        var pageSize = NormalizeLimit(limit);
        var filter = $"ontology={ontologyId}";
        var after = ReadCursor("list_use_case_slices", filter, cursor);
        EnsureOntologyRelations(await RelationNamesAsync(cancellationToken));
        var slices = (await new BusinessUseCaseSliceBuilder(om, store)
            .BuildAsync(new BusinessUseCaseSliceBuildRequest(ontologyId), cancellationToken)).Slices;
        return Page("list_use_case_slices", filter, after, pageSize, slices, item => item.Id);
    }

    public async Task<BusinessUseCaseSlice> GetUseCaseSliceAsync(
        string ontologyId,
        string sliceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sliceId)) throw new ArgumentException("sliceId is required.", nameof(sliceId));
        EnsureOntologyRelations(await RelationNamesAsync(cancellationToken));
        var slice = (await new BusinessUseCaseSliceBuilder(om, store)
            .BuildAsync(new BusinessUseCaseSliceBuildRequest(ontologyId), cancellationToken)).Slices
            .SingleOrDefault(item => item.Id == sliceId);
        return slice ?? throw new KeyNotFoundException($"Unknown use-case slice '{sliceId}'.");
    }

    public async Task<BusinessOntologySubjectInspection> InspectOntologySubjectAsync(
        string ontologyId,
        string subjectKind,
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        ValidateOntologyId(ontologyId);
        if (subjectKind is not ("concept" or "relation" or "rule" or "lifecycle" or "candidate"))
        {
            throw new ArgumentException("subjectKind must be concept, relation, rule, lifecycle, or candidate.", nameof(subjectKind));
        }
        if (string.IsNullOrWhiteSpace(subjectId)) throw new ArgumentException("subjectId is required.", nameof(subjectId));
        var snapshot = await ReadSnapshotIfPresentAsync(ontologyId, cancellationToken)
            ?? throw new InvalidOperationException($"No active ontology generation exists for '{ontologyId}'.");
        object? subject = subjectKind switch
        {
            "concept" => (object?)snapshot.Concepts.SingleOrDefault(item => item.Id == subjectId),
            "relation" => (object?)snapshot.Relations.SingleOrDefault(item => item.Id == subjectId),
            "rule" => (object?)snapshot.Rules.SingleOrDefault(item => item.Id == subjectId),
            "lifecycle" => (object?)snapshot.Lifecycles.SingleOrDefault(item => item.Id == subjectId),
            _ => (object?)snapshot.Candidates.SingleOrDefault(item => item.Id == subjectId),
        } ?? throw new KeyNotFoundException($"Unknown {subjectKind} '{subjectId}'.");
        var evidenceIds = subject switch
        {
            BusinessOntologyConcept item => item.EvidenceIds,
            BusinessOntologyRelation item => item.EvidenceIds,
            BusinessOntologyRule item => item.EvidenceIds,
            BusinessOntologyLifecycle item => item.EvidenceIds,
            BusinessOntologyCandidate item => item.EvidenceIds,
            _ => [],
        };
        var mappings = snapshot.Mappings.Where(item => item.SubjectKind == subjectKind && item.SubjectId == subjectId).ToArray();
        var reviews = snapshot.Reviews.Where(item => subject is BusinessOntologyCandidate candidate && item.CandidateId == candidate.Id).ToArray();
        return new BusinessOntologySubjectInspection(ontologyId, snapshot.GenerationId, subjectKind, subjectId, subject, mappings, evidenceIds, reviews,
            Digest("inspect_ontology_subject", new { ontologyId, snapshot.GenerationId, subjectKind, subjectId }));
    }

    private async Task<BusinessOntologySnapshot?> ReadSnapshotIfPresentAsync(string ontologyId, CancellationToken cancellationToken)
    {
        var relations = await RelationNamesAsync(cancellationToken);
        if (!BusinessOntologyStore.RequiredRelationNames.All(relations.Contains) || await ActiveGenerationAsync(ontologyId, cancellationToken) is null)
        {
            return null;
        }
        return await store.ReadExportableAsync(ontologyId, cancellationToken);
    }

    private async Task<IReadOnlyList<ClaimRow>> ReadClaimsAsync(CancellationToken cancellationToken)
    {
        var relations = await RelationNamesAsync(cancellationToken);
        if (!relations.IsSupersetOf(["ck_semantic_claim", "ck_file", "ck_repo", "ck_symbol"])) return [];
        var claims = new List<ClaimRow>();
        var after = "";
        while (true)
        {
            var page = await ReadClaimPageAsync(after, RelationReadPageSize, cancellationToken);
            claims.AddRange(page);
            if (page.Count < RelationReadPageSize) return claims;
            after = page[^1].ClaimId;
        }
    }

    private async Task<IReadOnlyList<ClaimRow>> ReadClaimPageAsync(string after, int pageSize, CancellationToken cancellationToken)
    {
        var result = await om.Runtime.Store.RunAsync(
            """
            ?[claim_id, subject_id, kind, payload_json, path, start_line, end_line, confidence, resolver, evidence, repository, repository_root, symbol] :=
              *ck_semantic_claim{claim_id, subject_id, kind, payload_json, file_id, start_line, end_line, confidence, resolver, evidence},
              *ck_file{file_id, repo_id, path},
              *ck_repo{repo_id, root_path: repository_root, name: repository},
              *ck_symbol{symbol_id: subject_id, name: symbol},
              claim_id > $after
            :order claim_id
            :limit $page_size
            """, new Dictionary<string, object?> { ["after"] = after, ["page_size"] = pageSize }, cancellationToken: cancellationToken);
        return result.Rows.Select(row => new ClaimRow(
            S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), I(row, 5), I(row, 6), D(row, 7), S(row, 8), S(row, 9), S(row, 10), S(row, 11), S(row, 12))).ToArray();
    }

    private async Task<IReadOnlyList<SymbolRow>> ReadSymbolsAsync(CancellationToken cancellationToken)
    {
        var relations = await RelationNamesAsync(cancellationToken);
        if (!relations.IsSupersetOf(["ck_symbol", "ck_file", "ck_repo"])) return [];
        var symbols = new List<SymbolRow>();
        var after = "";
        while (true)
        {
            var result = await om.Runtime.Store.RunAsync(
                """
                ?[symbol_id, name, kind, repository, path, start_line, end_line, parent_id] :=
                  *ck_symbol{symbol_id, file_id, name, kind, start_line, end_line, parent_id},
                  *ck_file{file_id, repo_id, path},
                  *ck_repo{repo_id, name: repository},
                  symbol_id > $after
                :order symbol_id
                :limit $page_size
                """, new Dictionary<string, object?> { ["after"] = after, ["page_size"] = RelationReadPageSize }, cancellationToken: cancellationToken);
            var page = result.Rows.Select(row => new SymbolRow(S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), I(row, 5), I(row, 6), S(row, 7))).ToArray();
            symbols.AddRange(page);
            if (page.Length < RelationReadPageSize) return symbols;
            after = page[^1].SymbolId;
        }
    }

    private async Task<InvestigationGraph> ReadInvestigationGraphAsync(CancellationToken cancellationToken)
    {
        var relations = await RelationNamesAsync(cancellationToken);
        var claims = await ReadClaimsAsync(cancellationToken);
        var symbols = (await ReadSymbolsAsync(cancellationToken)).ToDictionary(item => item.SymbolId, StringComparer.Ordinal);
        var entries = relations.Contains("ck_entry_point")
            ? (await om.Runtime.Store.RunAsync(
                "?[symbol_id, kind, metadata] := *ck_entry_point{symbol_id, kind, metadata}",
                cancellationToken: cancellationToken)).Rows
                .Select(row => new InvestigationEntryPoint(S(row, 0), S(row, 1), S(row, 2)))
                .Where(item => symbols.ContainsKey(item.SymbolId))
                .OrderBy(item => item.SymbolId, StringComparer.Ordinal)
                .ToArray()
            : [];
        var edges = relations.Contains("ck_edge")
            ? (await om.Runtime.Store.RunAsync(
                "?[from_id, to_id, kind] := *ck_edge{from_id, to_id, kind}",
                cancellationToken: cancellationToken)).Rows
                .Select(row => new InvestigationEdge(S(row, 0), S(row, 1), S(row, 2)))
                .OrderBy(item => item.FromId, StringComparer.Ordinal)
                .ThenBy(item => item.ToId, StringComparer.Ordinal)
                .ThenBy(item => item.Kind, StringComparer.Ordinal)
                .ToArray()
            : [];
        var roles = edges
            .Where(item => item.Kind == "SPRING_ROLE")
            .GroupBy(item => item.FromId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(item => item.ToId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var calls = edges
            .Where(item => item.Kind == CodeEdgeKinds.Calls)
            .GroupBy(item => item.FromId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(item => item.ToId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        return new InvestigationGraph(claims, symbols, entries, roles, calls);
    }

    private async Task<HashSet<string>> RelationNamesAsync(CancellationToken cancellationToken) =>
        (await om.Runtime.Store.RunAsync("::relations", cancellationToken: cancellationToken)).Rows
            .Select(row => S(row, 0)).Where(name => !name.Contains(':', StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);

    private async Task<int> CountAsync(string relation, CancellationToken cancellationToken)
    {
        var key = relation switch
        {
            "ck_repo" => "repo_id",
            "ck_file" => "file_id",
            "ck_symbol" => "symbol_id",
            "ck_semantic_claim" => "claim_id",
            "ck_entry_point" => "symbol_id",
            "ck_process" => "process_id",
            "onto_concept" => "concept_id",
            "onto_relation" => "relation_id",
            "onto_rule" => "rule_id",
            "onto_lifecycle" => "lifecycle_id",
            "onto_candidate" => "candidate_id",
            _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, "Unknown investigation count relation."),
        };
        var rows = await om.Runtime.Store.RunAsync($"?[count(value)] := *{relation}{{{key}: value}}", cancellationToken: cancellationToken);
        return rows.Rows.Count > 0 ? I(rows.Rows[0], 0) : 0;
    }

    private async Task<string?> ActiveGenerationAsync(string ontologyId, CancellationToken cancellationToken)
    {
        var rows = await om.Runtime.Store.RunAsync("?[generation_id] := *onto_generation{ontology_id: $ontology_id, generation_id}",
            new Dictionary<string, object?> { ["ontology_id"] = ontologyId }, cancellationToken: cancellationToken);
        return rows.Rows.Count == 0 ? null : S(rows.Rows[0], 0);
    }

    private static BusinessOntologyInvestigationPage<T> Page<T>(string operation, string filter, string? after, int pageSize, IReadOnlyList<T> all, Func<T, string> key)
    {
        var filtered = all.Where(item => after is null || StringComparer.Ordinal.Compare(key(item), after) > 0).ToArray();
        var items = filtered.Take(pageSize).ToArray();
        var truncated = filtered.Length > items.Length;
        return new BusinessOntologyInvestigationPage<T>(items, truncated ? WriteCursor(operation, filter, key(items[^1])) : null, truncated,
            Digest(operation, new { filter, returned = items.Length, truncated }));
    }

    private static string? ReadCursor(string operation, string filter, string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        var parts = cursor.Split('.', 2);
        if (parts.Length != 2) throw new ArgumentException("Invalid investigation cursor.", nameof(cursor));
        var payload = Convert.FromBase64String(parts[0]);
        var signature = Convert.FromHexString(parts[1]);
        var expected = HMACSHA256.HashData(CursorKey, payload);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature)) throw new ArgumentException("Invalid investigation cursor signature.", nameof(cursor));
        var value = JsonSerializer.Deserialize<Cursor>(payload) ?? throw new ArgumentException("Invalid investigation cursor.", nameof(cursor));
        if (value.Operation != operation || value.Filter != filter) throw new ArgumentException("Investigation cursor does not match this query.", nameof(cursor));
        return value.After;
    }

    private static string WriteCursor(string operation, string filter, string after)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Cursor(operation, filter, after));
        return Convert.ToBase64String(payload) + "." + Convert.ToHexString(HMACSHA256.HashData(CursorKey, payload));
    }

    private static string Digest(string operation, object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { operation, value })))).ToLowerInvariant();
    private static int NormalizeLimit(int? limit) => limit is null ? DefaultPageSize : limit.Value is >= 1 and <= MaxPageSize ? limit.Value : throw new ArgumentOutOfRangeException(nameof(limit), $"limit must be 1..{MaxPageSize}.");
    private static int NormalizeEvidenceLimit(int? limit) => limit is null ? DefaultPageSize : limit.Value is >= 1 and <= MaxEvidencePageSize ? limit.Value : throw new ArgumentOutOfRangeException(nameof(limit), $"limit must be 1..{MaxEvidencePageSize}.");
    private static string ValidateTerm(string term) => !string.IsNullOrWhiteSpace(term) && term.Trim().Length <= 160 ? term.Trim() : throw new ArgumentException("term must contain 1..160 characters.", nameof(term));
    private static string ValidateEntrySymbolId(string value) => value.StartsWith("symbol:", StringComparison.Ordinal) && value.Length <= 192 && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '_' or '.' or '-') ? value : throw new ArgumentException("entrySymbolId must be a bounded symbol id.", nameof(value));
    private static string[] NormalizeEvidenceIds(IReadOnlyList<string> evidenceIds)
    {
        if (evidenceIds.Count is < 1 or > SemanticEvidencePackBuilder.MaxAnchors
            || evidenceIds.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 192 || value.Any(character => !char.IsLetterOrDigit(character) && character is not ':' and not '_' and not '.' and not '-')))
        {
            throw new ArgumentException($"evidenceIds must contain 1..{SemanticEvidencePackBuilder.MaxAnchors} bounded evidence ids.", nameof(evidenceIds));
        }
        var normalized = evidenceIds.Order(StringComparer.Ordinal).ToArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("evidenceIds must be unique.", nameof(evidenceIds));
        }
        return normalized;
    }
    private static void ValidateOntologyId(string ontologyId) { if (string.IsNullOrWhiteSpace(ontologyId) || !System.Text.RegularExpressions.Regex.IsMatch(ontologyId, "^[A-Z][A-Za-z0-9]*(?:\\.[A-Z][A-Za-z0-9]*)+$")) throw new ArgumentException("ontologyId must be a dotted FQN.", nameof(ontologyId)); }
    private static void ValidateOptionalOntologyId(string? ontologyId) { if (!string.IsNullOrWhiteSpace(ontologyId)) ValidateOntologyId(ontologyId); }
    private static void EnsureOntologyRelations(ISet<string> relations) { if (!BusinessOntologyStore.RequiredRelationNames.All(relations.Contains)) throw new InvalidOperationException("Business ontology relations are unavailable; use an indexed database with an active ontology generation."); }
    private static bool Contains(string value, string term) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
    private static BusinessOntologyInvestigationEvidenceRef ToEvidenceRef(ClaimRow value) =>
        new(value.ClaimId, value.Repository, value.Path, value.Symbol, value.StartLine, value.EndLine) { SymbolId = value.SubjectId };
    private static BusinessOntologySemanticPattern ToPattern(ClaimRow item) => new(
        item.ClaimId, item.Kind, item.SubjectId, item.Symbol, item.Repository, item.Path,
        item.StartLine, item.EndLine, item.PayloadJson, item.Confidence, [ToEvidenceRef(item)]);
    private static string S(IReadOnlyList<JsonElement> row, int index) => row[index].ValueKind == JsonValueKind.String ? row[index].GetString() ?? "" : row[index].ToString();
    private static int I(IReadOnlyList<JsonElement> row, int index) => row[index].ValueKind == JsonValueKind.Number ? row[index].GetInt32() : int.Parse(S(row, index), System.Globalization.CultureInfo.InvariantCulture);
    private static double D(IReadOnlyList<JsonElement> row, int index) => row[index].ValueKind == JsonValueKind.Number ? row[index].GetDouble() : double.Parse(S(row, index), System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<InvestigationEntryPoint> MatchingEntries(InvestigationGraph graph, string term) =>
        graph.EntryPoints.Where(entry =>
        {
            var symbols = WalkSymbols(entry.SymbolId, graph);
            return Contains(entry.SymbolId, term)
                || Contains(entry.Kind, term)
                || Contains(entry.Metadata, term)
                || symbols.Any(symbolId => graph.Symbols.TryGetValue(symbolId, out var symbol) && SymbolMatchesTerm(symbol, term))
                || graph.Claims.Any(claim => symbols.Contains(claim.SubjectId, StringComparer.Ordinal) && ClaimMatchesTerm(claim, term));
        });

    private static BusinessOntologyDiscoveredDomainCharter CharterForEntry(InvestigationEntryPoint entry, string domainSeed, InvestigationGraph graph)
    {
        var symbols = WalkSymbols(entry.SymbolId, graph);
        var claims = ClaimsForSymbols(symbols, graph);
        var evidenceRefs = EvidenceRefsFor(claims);
        return new BusinessOntologyDiscoveredDomainCharter(
            "domain-charter:" + Digest("domain-charter", new { domainSeed, entry = entry.SymbolId, evidence = claims.Select(item => item.ClaimId).ToArray() })[..24],
            domainSeed,
            ActorsForPath(symbols, claims, graph),
            new BusinessOntologyInvestigationWorkflow(entry.SymbolId, entry.Kind, ActionFor(entry), CallPath(symbols, graph), evidenceRefs),
            BoundariesForPath(symbols, graph),
            evidenceRefs.Select(item => item.EvidenceId).ToArray(),
            evidenceRefs);
    }

    private static BusinessOntologyCrossLayerUseCase UseCaseForEntry(InvestigationEntryPoint entry, string domainSeed, InvestigationGraph graph)
    {
        var symbols = WalkSymbols(entry.SymbolId, graph);
        var claims = ClaimsForSymbols(symbols, graph);
        var evidenceRefs = EvidenceRefsFor(claims);
        return new BusinessOntologyCrossLayerUseCase(
            "cross-layer-use-case:" + Digest("cross-layer-use-case", new { entry = entry.SymbolId, domainSeed, evidence = claims.Select(item => item.ClaimId).ToArray() })[..24],
            domainSeed,
            entry.SymbolId,
            entry.Kind,
            ActionFor(entry),
            CallPath(symbols, graph),
            RolesForPath(symbols, graph),
            Patterns(claims),
            evidenceRefs.Select(item => item.EvidenceId).ToArray(),
            evidenceRefs);
    }

    private static IReadOnlyList<string> WalkSymbols(string root, InvestigationGraph graph)
    {
        if (!graph.Symbols.ContainsKey(root)) return [];
        var visited = new HashSet<string>(StringComparer.Ordinal) { root };
        var ordered = new List<string> { root };
        var queue = new Queue<(string SymbolId, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && ordered.Count < BusinessUseCaseSliceBuilder.MaxSymbols)
        {
            var (symbolId, depth) = queue.Dequeue();
            if (depth >= BusinessUseCaseSliceBuilder.MaxDepth) continue;
            foreach (var target in graph.CallsFrom.GetValueOrDefault(symbolId, []))
            {
                if (visited.Add(target) && graph.Symbols.ContainsKey(target))
                {
                    ordered.Add(target);
                    queue.Enqueue((target, depth + 1));
                }
            }
        }
        return ordered;
    }

    private static IReadOnlyList<string> WalkTopologySymbols(string root, InvestigationGraph graph)
    {
        if (!graph.Symbols.ContainsKey(root)) return [];
        var visited = new HashSet<string>(StringComparer.Ordinal) { root };
        var ordered = new List<string> { root };
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0 && ordered.Count < MaxTopologySubjects)
        {
            var symbolId = queue.Dequeue();
            foreach (var target in graph.CallsFrom.GetValueOrDefault(symbolId, []))
            {
                if (visited.Add(target) && graph.Symbols.ContainsKey(target))
                {
                    ordered.Add(target);
                    queue.Enqueue(target);
                    if (ordered.Count >= MaxTopologySubjects) break;
                }
            }
        }
        return ordered;
    }

    private static IReadOnlyList<ClaimRow> ClaimsForSymbols(IReadOnlyList<string> symbols, InvestigationGraph graph) =>
        graph.Claims.Where(item => symbols.Contains(item.SubjectId, StringComparer.Ordinal)).OrderBy(item => item.ClaimId, StringComparer.Ordinal).ToArray();

    private static BusinessOntologyStateRuleCluster StateRuleCluster(
        string subjectId,
        string term,
        string entrySymbolId,
        IReadOnlyList<BusinessOntologyInvestigationCallPathStep> callPath,
        IReadOnlyList<ClaimRow> claims)
    {
        var evidenceRefs = EvidenceRefsFor(claims);
        return new BusinessOntologyStateRuleCluster(
            "state-rule-cluster:" + Digest("state-rule-cluster", new { subjectId, entry = entrySymbolId, claims = claims.Select(item => item.ClaimId).ToArray() })[..24],
            NormalizeDomainSeed(term),
            subjectId,
            callPath,
            Patterns(claims.Where(item => item.Kind == CodeSemanticClaimKinds.StateField)),
            Patterns(claims.Where(item => item.Kind == CodeSemanticClaimKinds.StateValue)),
            Patterns(claims.Where(item => item.Kind == CodeSemanticClaimKinds.StateAssignment)),
            Patterns(claims.Where(item => item.Kind == CodeSemanticClaimKinds.BusinessGuard)),
            Patterns(claims.Where(item => item.Kind == CodeSemanticClaimKinds.ValidationConstraint)),
            Patterns(claims.Where(item => item.Kind == CodeSemanticClaimKinds.PersistenceConstraint)),
            evidenceRefs);
    }

    private static IReadOnlyList<BusinessOntologySemanticPattern> Patterns(IEnumerable<ClaimRow> claims) =>
        claims.OrderBy(item => item.ClaimId, StringComparer.Ordinal)
            .Select(item => new BusinessOntologySemanticPattern(item.ClaimId, item.Kind, item.SubjectId, item.Symbol, item.Repository, item.Path,
                item.StartLine, item.EndLine, item.PayloadJson, item.Confidence, [ToEvidenceRef(item)]))
            .ToArray();

    private static IReadOnlyList<BusinessOntologyInvestigationEvidenceRef> EvidenceRefsFor(IEnumerable<ClaimRow> claims) =>
        claims.Select(ToEvidenceRef).GroupBy(item => item.EvidenceId, StringComparer.Ordinal).Select(group => group.First()).OrderBy(item => item.EvidenceId, StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<BusinessOntologyInvestigationCallPathStep> CallPath(IReadOnlyList<string> symbols, InvestigationGraph graph) =>
        symbols.Where(graph.Symbols.ContainsKey).Select(symbolId =>
        {
            var symbol = graph.Symbols[symbolId];
            return new BusinessOntologyInvestigationCallPathStep(symbol.SymbolId, symbol.Name,
                RolesForSymbol(symbol.SymbolId, graph).FirstOrDefault() ?? "unclassified", symbol.Repository, symbol.Path, symbol.StartLine);
        }).ToArray();

    private static IReadOnlyList<string> RolesForPath(IReadOnlyList<string> symbols, InvestigationGraph graph) =>
        symbols.SelectMany(symbolId => RolesForSymbol(symbolId, graph)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> RolesForSymbol(string symbolId, InvestigationGraph graph)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var current = symbolId;
        for (var depth = 0; depth < 16 && graph.Symbols.TryGetValue(current, out var symbol); depth++)
        {
            foreach (var role in graph.RolesBySymbol.GetValueOrDefault(current, [])) roles.Add(role);
            current = symbol.ParentId;
        }
        return roles.Order(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<BusinessOntologyInvestigationActor> ActorsForPath(IReadOnlyList<string> symbols, IReadOnlyList<ClaimRow> claims, InvestigationGraph graph) =>
        symbols.Where(graph.Symbols.ContainsKey)
            .SelectMany(symbolId => RolesForSymbol(symbolId, graph).Select(role => new { SymbolId = symbolId, Role = role }))
            .OrderBy(item => item.SymbolId, StringComparer.Ordinal).ThenBy(item => item.Role, StringComparer.Ordinal)
            .Select(item => new BusinessOntologyInvestigationActor(
                "actor:" + item.SymbolId + ":" + item.Role,
                graph.Symbols[item.SymbolId].Name,
                item.Role,
                claims.Where(claim => claim.SubjectId == item.SymbolId).Select(claim => claim.ClaimId).Order(StringComparer.Ordinal).ToArray()))
            .ToArray();

    private static IReadOnlyList<BusinessOntologyInvestigationBoundary> BoundariesForPath(IReadOnlyList<string> symbols, InvestigationGraph graph) =>
        symbols.Where(graph.Symbols.ContainsKey).Select(symbolId => graph.Symbols[symbolId])
            .GroupBy(symbol => new { symbol.Repository, symbol.Path })
            .OrderBy(group => group.Key.Repository, StringComparer.Ordinal).ThenBy(group => group.Key.Path, StringComparer.Ordinal)
            .Select(group => new BusinessOntologyInvestigationBoundary(group.Key.Repository, group.Key.Path,
                group.SelectMany(symbol => RolesForSymbol(symbol.SymbolId, graph)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                group.Select(symbol => symbol.SymbolId).Order(StringComparer.Ordinal).ToArray()))
            .ToArray();

    private static string ActionFor(InvestigationEntryPoint entry)
    {
        if (entry.Metadata.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(entry.Metadata);
                var method = document.RootElement.TryGetProperty("method", out var methodValue) ? methodValue.GetString() ?? "" : "";
                var path = document.RootElement.TryGetProperty("path", out var pathValue) ? pathValue.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(method) || !string.IsNullOrWhiteSpace(path)) return (method.ToUpperInvariant() + " " + path).Trim();
            }
            catch (JsonException)
            {
            }
        }
        return entry.Metadata;
    }

    private static string NormalizeDomainSeed(string value)
    {
        var normalized = new string(value.Trim().Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized.Length == 0 ? "unknown" : normalized;
    }

    private static bool SymbolMatchesTerm(SymbolRow value, string term) => Contains(value.SymbolId, term) || Contains(value.Name, term);
    private static bool ClaimMatchesTerm(ClaimRow value, string term) => Contains(value.ClaimId, term) || Contains(value.SubjectId, term) || Contains(value.Symbol, term) || Contains(value.PayloadJson, term) || Contains(value.Evidence, term);

    private sealed record Cursor(string Operation, string Filter, string After);
    private sealed record ClaimRow(string ClaimId, string SubjectId, string Kind, string PayloadJson, string Path, int StartLine, int EndLine, double Confidence, string Resolver, string Evidence, string Repository, string RepositoryRoot, string Symbol);
    private sealed record SymbolRow(string SymbolId, string Name, string Kind, string Repository, string Path, int StartLine, int EndLine, string ParentId);
    private sealed record InvestigationEntryPoint(string SymbolId, string Kind, string Metadata);
    private sealed record InvestigationEdge(string FromId, string ToId, string Kind);
    private sealed record InvestigationGraph(
        IReadOnlyList<ClaimRow> Claims,
        IReadOnlyDictionary<string, SymbolRow> Symbols,
        IReadOnlyList<InvestigationEntryPoint> EntryPoints,
        IReadOnlyDictionary<string, IReadOnlyList<string>> RolesBySymbol,
        IReadOnlyDictionary<string, IReadOnlyList<string>> CallsFrom);
    private sealed record TopologyNodeSeed(string Id, string Layer, string Kind, string Label, string Detail, int DirectObservationCount);
}

public sealed record BusinessOntologyInvestigationOverview(string? OntologyId, string? GenerationId, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<string> Operations, string QueryDigest);
public sealed record BusinessOntologyInvestigationEvidenceRef(string EvidenceId, string Repository, string Path, string Symbol, int StartLine, int EndLine)
{
    public string SymbolId { get; init; } = "";
}
public sealed record BusinessOntologySemanticEvidenceMetadata(string EvidenceId, string Repository, string Path, string SubjectId, int StartLine, int EndLine, string Resolver, double Confidence, string ClaimKind);
public sealed record BusinessTermHit(string Kind, string Id, string Label, string Detail, string Repository, string Path, int StartLine, int EndLine, IReadOnlyList<BusinessOntologyInvestigationEvidenceRef> EvidenceRefs, double Confidence, string Status);
public sealed record BusinessOntologySemanticPattern(string ClaimId, string ClaimKind, string SubjectId, string Symbol, string Repository, string Path, int StartLine, int EndLine, string PayloadJson, double Confidence, IReadOnlyList<BusinessOntologyInvestigationEvidenceRef> EvidenceRefs);
public sealed record BusinessOntologyInvestigationPage<T>(IReadOnlyList<T> Items, string? NextCursor, bool Truncated, string QueryDigest);
public sealed record BusinessOntologySubjectInspection(string OntologyId, string GenerationId, string SubjectKind, string SubjectId, object Subject, IReadOnlyList<BusinessOntologyMapping> Mappings, IReadOnlyList<string> EvidenceIds, IReadOnlyList<BusinessOntologyReview> Reviews, string QueryDigest);
public sealed record BusinessOntologyInvestigationActor(string Id, string Name, string Role, IReadOnlyList<string> EvidenceIds);
public sealed record BusinessOntologyInvestigationCallPathStep(string SymbolId, string Symbol, string Role, string Repository, string RelativePath, int StartLine);
public sealed record BusinessOntologyInvestigationWorkflow(string EntrySymbolId, string EntryKind, string Action, IReadOnlyList<BusinessOntologyInvestigationCallPathStep> CallPath, IReadOnlyList<BusinessOntologyInvestigationEvidenceRef> EvidenceRefs);
public sealed record BusinessOntologyInvestigationBoundary(string Repository, string RelativePath, IReadOnlyList<string> Roles, IReadOnlyList<string> SymbolIds);
public sealed record BusinessOntologyDiscoveredDomainCharter(string Id, string DomainSeed, IReadOnlyList<BusinessOntologyInvestigationActor> Actors, BusinessOntologyInvestigationWorkflow Workflow, IReadOnlyList<BusinessOntologyInvestigationBoundary> Boundaries, IReadOnlyList<string> EvidenceIds, IReadOnlyList<BusinessOntologyInvestigationEvidenceRef> EvidenceRefs);
public sealed record BusinessOntologyCrossLayerUseCase(string Id, string DomainSeed, string EntrySymbolId, string RouteKind, string Action, IReadOnlyList<BusinessOntologyInvestigationCallPathStep> CallPath, IReadOnlyList<string> Roles, IReadOnlyList<BusinessOntologySemanticPattern> Claims, IReadOnlyList<string> EvidenceIds, IReadOnlyList<BusinessOntologyInvestigationEvidenceRef> EvidenceRefs);
public sealed record BusinessOntologyStateRuleCluster(string Id, string DomainSeed, string SubjectId, IReadOnlyList<BusinessOntologyInvestigationCallPathStep> CallPath, IReadOnlyList<BusinessOntologySemanticPattern> StateFields, IReadOnlyList<BusinessOntologySemanticPattern> StateValues, IReadOnlyList<BusinessOntologySemanticPattern> Transitions, IReadOnlyList<BusinessOntologySemanticPattern> Guards, IReadOnlyList<BusinessOntologySemanticPattern> Validations, IReadOnlyList<BusinessOntologySemanticPattern> Persistences, IReadOnlyList<BusinessOntologyInvestigationEvidenceRef> EvidenceRefs);
public sealed record BusinessOntologyImplementationCluster(string Id, string DomainSeed, BusinessOntologySemanticCluster SemanticCluster, IReadOnlyList<string> EvidenceIds, IReadOnlyList<BusinessOntologyInvestigationEvidenceRef> EvidenceRefs);
public sealed record BusinessOntologyTopologyNode(string Id, string Layer, string Kind, string Label, string Detail, int InDegree, int OutDegree, int Degree, int DirectObservationCount, int Weight);
public sealed record BusinessOntologyTopologyEdge(string FromId, string ToId, string Kind);
public sealed record BusinessOntologyDomainTopology(string DomainSeed, IReadOnlyList<BusinessOntologyTopologyNode> Nodes, IReadOnlyList<BusinessOntologyTopologyEdge> Edges, bool Truncated, string QueryDigest);
