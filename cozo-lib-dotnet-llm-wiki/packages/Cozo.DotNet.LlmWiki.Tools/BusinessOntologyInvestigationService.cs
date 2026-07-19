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
    private static readonly byte[] CursorKey = RandomNumberGenerator.GetBytes(32);

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
                "find_semantic_patterns", "get_semantic_evidence", "inspect_ontology_subject",
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
        var result = await om.Runtime.Store.RunAsync(
            """
            ?[claim_id, subject_id, kind, payload_json, path, start_line, end_line, confidence, resolver, evidence, repository, repository_root, symbol] :=
              *ck_semantic_claim{claim_id, subject_id, kind, payload_json, file_id, start_line, end_line, confidence, resolver, evidence},
              *ck_file{file_id, repo_id, path},
              *ck_repo{repo_id, root_path: repository_root, name: repository},
              *ck_symbol{symbol_id: subject_id, name: symbol}
            :limit 5000
            """, cancellationToken: cancellationToken);
        return result.Rows.Select(row => new ClaimRow(
            S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), I(row, 5), I(row, 6), D(row, 7), S(row, 8), S(row, 9), S(row, 10), S(row, 11), S(row, 12))).ToArray();
    }

    private async Task<IReadOnlyList<SymbolRow>> ReadSymbolsAsync(CancellationToken cancellationToken)
    {
        var relations = await RelationNamesAsync(cancellationToken);
        if (!relations.IsSupersetOf(["ck_symbol", "ck_file", "ck_repo"])) return [];
        var result = await om.Runtime.Store.RunAsync(
            """
            ?[symbol_id, name, kind, repository, path, start_line, end_line] :=
              *ck_symbol{symbol_id, file_id, name, kind, start_line, end_line},
              *ck_file{file_id, repo_id, path},
              *ck_repo{repo_id, name: repository}
            :limit 5000
            """, cancellationToken: cancellationToken);
        return result.Rows.Select(row => new SymbolRow(S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), I(row, 5), I(row, 6))).ToArray();
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
    private static string ValidateTerm(string term) => !string.IsNullOrWhiteSpace(term) && term.Trim().Length <= 160 ? term.Trim() : throw new ArgumentException("term must contain 1..160 characters.", nameof(term));
    private static void ValidateOntologyId(string ontologyId) { if (string.IsNullOrWhiteSpace(ontologyId) || !System.Text.RegularExpressions.Regex.IsMatch(ontologyId, "^[A-Z][A-Za-z0-9]*(?:\\.[A-Z][A-Za-z0-9]*)+$")) throw new ArgumentException("ontologyId must be a dotted FQN.", nameof(ontologyId)); }
    private static void ValidateOptionalOntologyId(string? ontologyId) { if (!string.IsNullOrWhiteSpace(ontologyId)) ValidateOntologyId(ontologyId); }
    private static void EnsureOntologyRelations(ISet<string> relations) { if (!BusinessOntologyStore.RequiredRelationNames.All(relations.Contains)) throw new InvalidOperationException("Business ontology relations are unavailable; use an indexed database with an active ontology generation."); }
    private static bool Contains(string value, string term) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
    private static BusinessOntologyInvestigationEvidenceRef ToEvidenceRef(ClaimRow value) => new(value.ClaimId, value.Repository, value.Path, value.Symbol, value.StartLine, value.EndLine);
    private static string S(IReadOnlyList<JsonElement> row, int index) => row[index].ValueKind == JsonValueKind.String ? row[index].GetString() ?? "" : row[index].ToString();
    private static int I(IReadOnlyList<JsonElement> row, int index) => row[index].ValueKind == JsonValueKind.Number ? row[index].GetInt32() : int.Parse(S(row, index), System.Globalization.CultureInfo.InvariantCulture);
    private static double D(IReadOnlyList<JsonElement> row, int index) => row[index].ValueKind == JsonValueKind.Number ? row[index].GetDouble() : double.Parse(S(row, index), System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Cursor(string Operation, string Filter, string After);
    private sealed record ClaimRow(string ClaimId, string SubjectId, string Kind, string PayloadJson, string Path, int StartLine, int EndLine, double Confidence, string Resolver, string Evidence, string Repository, string RepositoryRoot, string Symbol);
    private sealed record SymbolRow(string SymbolId, string Name, string Kind, string Repository, string Path, int StartLine, int EndLine);
}

public sealed record BusinessOntologyInvestigationOverview(string? OntologyId, string? GenerationId, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<string> Operations, string QueryDigest);
public sealed record BusinessOntologyInvestigationEvidenceRef(string EvidenceId, string Repository, string Path, string Symbol, int StartLine, int EndLine);
public sealed record BusinessOntologySemanticEvidenceMetadata(string EvidenceId, string Repository, string Path, string SubjectId, int StartLine, int EndLine, string Resolver, double Confidence, string ClaimKind);
public sealed record BusinessTermHit(string Kind, string Id, string Label, string Detail, string Repository, string Path, int StartLine, int EndLine, IReadOnlyList<BusinessOntologyInvestigationEvidenceRef> EvidenceRefs, double Confidence, string Status);
public sealed record BusinessOntologySemanticPattern(string ClaimId, string ClaimKind, string SubjectId, string Symbol, string Repository, string Path, int StartLine, int EndLine, string PayloadJson, double Confidence, IReadOnlyList<BusinessOntologyInvestigationEvidenceRef> EvidenceRefs);
public sealed record BusinessOntologyInvestigationPage<T>(IReadOnlyList<T> Items, string? NextCursor, bool Truncated, string QueryDigest);
public sealed record BusinessOntologySubjectInspection(string OntologyId, string GenerationId, string SubjectKind, string SubjectId, object Subject, IReadOnlyList<BusinessOntologyMapping> Mappings, IReadOnlyList<string> EvidenceIds, IReadOnlyList<BusinessOntologyReview> Reviews, string QueryDigest);
