using System.Text.RegularExpressions;
using System.Text.Json;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.Contracts;

namespace Cozo.DotNet.LlmWiki.Tools;

public sealed record BusinessOntologyGenerationInput(
    string OntologyId,
    string GenerationId,
    string SourceFingerprint,
    string GeneratorVersion,
    string CreatedAt,
    IReadOnlyList<BusinessOntologyConcept> Concepts,
    IReadOnlyList<BusinessOntologyAttribute> Attributes,
    IReadOnlyList<BusinessOntologyRelation> Relations,
    IReadOnlyList<BusinessOntologyRule> Rules,
    IReadOnlyList<BusinessOntologyLifecycle> Lifecycles,
    IReadOnlyList<BusinessOntologyState> States,
    IReadOnlyList<BusinessOntologyTransition> Transitions,
    IReadOnlyList<BusinessOntologyMapping> Mappings,
    IReadOnlyList<BusinessOntologyEvidence> Evidence,
    IReadOnlyList<BusinessOntologyCandidate> Candidates,
    IReadOnlyList<BusinessOntologyReview> Reviews,
    IReadOnlyList<BusinessOntologyDiagnostic> Diagnostics);

public sealed record BusinessOntologyConcept(string Id, string Kind, string Label, string Description, string Status, double Confidence, IReadOnlyList<string> EvidenceIds);
public sealed record BusinessOntologyAttribute(string ConceptId, string Name, string ValueType, bool Required, string Description, string Status, double Confidence, IReadOnlyList<string> EvidenceIds);
public sealed record BusinessOntologyRelation(string Id, string Name, string FromConceptId, string ToConceptId, bool Directed, string Min, string Max, string Description, string Status, double Confidence, IReadOnlyList<string> EvidenceIds);
public sealed record BusinessOntologyRule(string Id, string SubjectId, string Kind, string Description, string PredicateJson, string EffectJson, string Status, double Confidence, IReadOnlyList<string> EvidenceIds);
public sealed record BusinessOntologyLifecycle(string Id, string SubjectId, string StateProperty, string InitialState, string Description, string Status, double Confidence, IReadOnlyList<string> EvidenceIds);
public sealed record BusinessOntologyState(string LifecycleId, string Id, bool Terminal, string Description, IReadOnlyList<string>? EvidenceIds = null);
public sealed record BusinessOntologyTransition(string Id, string LifecycleId, string Action, string FromState, string ToState, string Description, string GuardJson, string EffectJson, string Status, double Confidence, IReadOnlyList<string> EvidenceIds);
public sealed record BusinessOntologyMapping(string Id, string SubjectKind, string SubjectId, string MappingRole, string Repository, string Language, string CodeKind, string Symbol, string Path, string Resolver, double Confidence, string Status, IReadOnlyList<string> EvidenceIds);
public sealed record BusinessOntologyEvidence(string Id, string Repository, string Path, string Symbol, int StartLine, int EndLine, string Grade, string Resolver, double Confidence, string SourceKind, string Summary);
public sealed record BusinessOntologyCandidate(string Id, string SubjectKind, string ProposedId, string PayloadJson, string Reason, double Confidence, string Status, IReadOnlyList<string> EvidenceIds);
public sealed record BusinessOntologyReview(string Id, string CandidateId, string Decision, string Reviewer, string Rationale, string ReviewedAt);
public sealed record BusinessOntologyReviewEntry(BusinessOntologyReview Review, IReadOnlyList<string> ExpectedEvidenceIds);
public sealed record BusinessOntologyReviewAppendResult(BusinessOntologyReviewEntry Entry, bool Appended);
public sealed record BusinessOntologyEffectiveDecision(BusinessOntologyCandidate Candidate, BusinessOntologyReviewEntry Review);
public sealed record BusinessOntologyDiagnostic(string Id, string Kind, string SubjectKind, string ConflictKey, string Message, string DetailsJson, string Severity);
public sealed record BusinessOntologyEvidenceReference(string SubjectKind, string SubjectId, string EvidenceId);
public sealed record BusinessOntologyMaterializationExpectation(
    string CandidateId,
    string? PayloadJson,
    IReadOnlyList<string> CurrentEvidenceIds,
    string ReviewId,
    string Decision,
    IReadOnlyList<string> ReviewExpectedEvidenceIds);
public sealed record BusinessOntologyMaterializationRecord(
    string CandidateId,
    string SubjectKind,
    string SubjectId,
    string ReviewId,
    string PayloadJson);
public sealed record BusinessOntologyMaterializationBatch(
    string OntologyId,
    string GenerationId,
    IReadOnlyList<BusinessOntologyMaterializationExpectation> Expectations,
    IReadOnlyList<BusinessOntologyMaterializationRecord> Materializations,
    IReadOnlyList<BusinessOntologyRelation> Relations,
    IReadOnlyList<BusinessOntologyRule> Rules,
    IReadOnlyList<BusinessOntologyLifecycle> Lifecycles,
    IReadOnlyList<BusinessOntologyState> States,
    IReadOnlyList<BusinessOntologyTransition> Transitions,
    IReadOnlyList<BusinessOntologyDiagnostic> Diagnostics,
    IReadOnlyDictionary<string, string> CandidateStatuses);
public sealed record BusinessOntologyPurgeSummary(
    string OntologyId,
    string GenerationId,
    bool Purged,
    bool TombstoneWritten,
    string Reason,
    string PurgedAt);

public sealed record BusinessOntologySnapshot(
    string OntologyId,
    string GenerationId,
    IReadOnlyList<BusinessOntologyConcept> Concepts,
    IReadOnlyList<BusinessOntologyAttribute> Attributes,
    IReadOnlyList<BusinessOntologyRelation> Relations,
    IReadOnlyList<BusinessOntologyRule> Rules,
    IReadOnlyList<BusinessOntologyLifecycle> Lifecycles,
    IReadOnlyList<BusinessOntologyState> States,
    IReadOnlyList<BusinessOntologyTransition> Transitions,
    IReadOnlyList<BusinessOntologyMapping> Mappings,
    IReadOnlyList<BusinessOntologyEvidence> Evidence,
    IReadOnlyList<BusinessOntologyCandidate> Candidates,
    IReadOnlyList<BusinessOntologyReview> Reviews,
    IReadOnlyList<BusinessOntologyDiagnostic> Diagnostics,
    IReadOnlyList<BusinessOntologyEvidenceReference> EvidenceReferences);

/// <summary>
/// Persistent, package-owned generation state for business ontology interpretations. CodeKnowledge
/// and DEPA remain independent evidence layers: this store never writes or reads their relations.
/// </summary>
public sealed class BusinessOntologyStore(CozoOm om)
{
    private static readonly Regex Fqn = new("^[A-Z][A-Za-z0-9]*(?:\\.[A-Z][A-Za-z0-9]*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex LowerCamel = new("^[a-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant);
    private static readonly Regex StateToken = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SemanticStatuses = new(["accepted", "hypothesis"], StringComparer.Ordinal);
    private static readonly HashSet<string> CandidateStatuses = new(["pending", "accepted", "rejected", "superseded"], StringComparer.Ordinal);
    private static readonly HashSet<string> Grades = new(["authoritative", "enforced", "contractual", "presentational", "inferred"], StringComparer.Ordinal);
    private static readonly HashSet<string> PrimitiveTypes = new(["String", "Number", "Bool", "Json", "Validity"], StringComparer.Ordinal);

    public static IReadOnlyList<string> RequiredRelationNames { get; } =
    [
        "onto_generation", "onto_concept", "onto_attribute", "onto_relation", "onto_rule",
        "onto_lifecycle", "onto_state", "onto_transition", "onto_mapping", "onto_evidence",
        "onto_evidence_ref", "onto_candidate", "onto_review", "onto_review_expectation",
        "onto_diagnostic", "onto_materialization", "onto_generation_tombstone",
    ];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var create in Creates)
        {
            try
            {
                await om.Runtime.Store.RunAsync(create, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (IsCreateConflict(ex))
            {
                // Schema creation is intentionally idempotent; never inspect or modify other families.
            }
        }
    }

    public async Task ReplaceGenerationAsync(BusinessOntologyGenerationInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);
        await InitializeAsync(cancellationToken);
        if (await GenerationTombstoneExistsAsync(input.OntologyId, input.GenerationId, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Ontology generation '{input.OntologyId}'/'{input.GenerationId}' has a tombstone and cannot be reused.");
        }

        var previousGeneration = await ActiveGenerationAsync(input.OntologyId, cancellationToken);
        await using var transaction = await om.Runtime.Store.BeginTransactionAsync(write: true, cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(previousGeneration))
            {
                await RemoveGenerationAsync(transaction, input.OntologyId, previousGeneration, cancellationToken);
            }

            await PutAsync(transaction,
                """
                ?[ontology_id, generation_id, source_fingerprint, generator_version, created_at] <- [[$ontology_id, $generation_id, $source_fingerprint, $generator_version, $created_at]]
                :put onto_generation {ontology_id => generation_id, source_fingerprint, generator_version, created_at}
                """,
                Params(("ontology_id", input.OntologyId), ("generation_id", input.GenerationId), ("source_fingerprint", input.SourceFingerprint), ("generator_version", input.GeneratorVersion), ("created_at", input.CreatedAt)), cancellationToken);

            foreach (var item in input.Evidence) await PutEvidenceAsync(transaction, input, item, cancellationToken);
            foreach (var item in input.Concepts) await PutConceptAsync(transaction, input, item, cancellationToken);
            foreach (var item in input.Attributes) await PutAttributeAsync(transaction, input, item, cancellationToken);
            foreach (var item in input.Relations) await PutRelationAsync(transaction, input, item, cancellationToken);
            foreach (var item in input.Rules) await PutRuleAsync(transaction, input, item, cancellationToken);
            foreach (var item in input.Lifecycles) await PutLifecycleAsync(transaction, input, item, cancellationToken);
            foreach (var item in input.States) await PutStateAsync(transaction, input, item, cancellationToken);
            foreach (var item in input.Transitions) await PutTransitionAsync(transaction, input, item, cancellationToken);
            foreach (var item in input.Mappings) await PutMappingAsync(transaction, input, item, cancellationToken);
            foreach (var item in input.Candidates) await PutCandidateAsync(transaction, input, item, cancellationToken);
            foreach (var item in input.Reviews) await PutLegacyReviewAsync(transaction, input.OntologyId, item, cancellationToken);
            foreach (var item in input.Diagnostics) await PutDiagnosticAsync(transaction, input, item, cancellationToken);
            await PutEvidenceReferencesAsync(transaction, input, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.AbortAsync(cancellationToken);
            throw;
        }
    }

    public async Task<BusinessOntologyPurgeSummary> PurgeGenerationAsync(
        string ontologyId,
        string generationId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ValidateFqn(ontologyId, nameof(ontologyId));
        Require(generationId, nameof(generationId));
        Require(reason, nameof(reason));
        await InitializeAsync(cancellationToken);
        var purgedAt = DateTimeOffset.UtcNow.ToString("O");

        await using var transaction = await om.Runtime.Store.BeginTransactionAsync(
            write: true,
            cancellationToken);
        try
        {
            var activeRows = await transaction.RunAsync(
                "?[generation_id] := *onto_generation{ontology_id: $ontology_id, generation_id}",
                Params(("ontology_id", ontologyId)),
                cancellationToken: cancellationToken);
            if (activeRows.Rows.Count == 0
                || !StringComparer.Ordinal.Equals(S(activeRows.Rows[0], 0), generationId))
            {
                throw new InvalidOperationException(
                    $"Purge requires ontology '{ontologyId}' active generation to exactly match '{generationId}'.");
            }

            await RemoveGenerationAsync(transaction, ontologyId, generationId, cancellationToken);
            await PutAsync(
                transaction,
                """
                ?[ontology_id] :=
                    *onto_generation{ontology_id, generation_id},
                    ontology_id == $ontology_id,
                    generation_id == $generation_id
                :rm onto_generation {ontology_id}
                """,
                Params(("ontology_id", ontologyId), ("generation_id", generationId)),
                cancellationToken);
            await PutAsync(
                transaction,
                """
                ?[ontology_id, generation_id, reason, purged_at] <-
                    [[$ontology_id, $generation_id, $reason, $purged_at]]
                :put onto_generation_tombstone {ontology_id, generation_id => reason, purged_at}
                """,
                Params(
                    ("ontology_id", ontologyId),
                    ("generation_id", generationId),
                    ("reason", reason.Trim()),
                    ("purged_at", purgedAt)),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.AbortAsync(cancellationToken);
            throw;
        }

        return new BusinessOntologyPurgeSummary(
            ontologyId,
            generationId,
            Purged: true,
            TombstoneWritten: true,
            reason.Trim(),
            purgedAt);
    }

    public async Task<BusinessOntologySnapshot> ReadExportableAsync(string ontologyId, CancellationToken cancellationToken = default)
    {
        ValidateFqn(ontologyId, nameof(ontologyId));
        await InitializeAsync(cancellationToken);
        var generationId = await ActiveGenerationAsync(ontologyId, cancellationToken)
            ?? throw new InvalidOperationException($"No active onto generation exists for ontology '{ontologyId}'.");
        var refs = await ReadEvidenceReferencesAsync(ontologyId, generationId, cancellationToken);
        var evidenceIds = refs.GroupBy(item => (item.SubjectKind, item.SubjectId), StringComparerTuple.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(item => item.EvidenceId).OrderBy(id => id, StringComparer.Ordinal).ToArray(), StringComparerTuple.Ordinal);
        IReadOnlyList<string> Refs(string kind, string id) => evidenceIds.GetValueOrDefault((kind, id), []);

        var concepts = (await RowsAsync("?[id, kind, label, description, status, confidence] := *onto_concept{ontology_id: $ontology_id, generation_id: $generation_id, concept_id: id, kind, label, description, status, confidence}", ontologyId, generationId, cancellationToken))
            .Select(row => new BusinessOntologyConcept(S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), D(row, 5), Refs("concept", S(row, 0))))
            .Where(item => SemanticStatuses.Contains(item.Status)).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var attributes = (await RowsAsync("?[concept_id, name, value_type, required, description, status, confidence] := *onto_attribute{ontology_id: $ontology_id, generation_id: $generation_id, concept_id, attr_name: name, value_type, required, description, status, confidence}", ontologyId, generationId, cancellationToken))
            .Select(row => new BusinessOntologyAttribute(S(row, 0), S(row, 1), S(row, 2), B(row, 3), S(row, 4), S(row, 5), D(row, 6), Refs("attribute", AttributeKey(S(row, 0), S(row, 1)))))
            .Where(item => SemanticStatuses.Contains(item.Status)).OrderBy(item => item.ConceptId, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
        var relations = (await RowsAsync("?[id, name, from_id, to_id, directed, min, max, description, status, confidence] := *onto_relation{ontology_id: $ontology_id, generation_id: $generation_id, relation_id: id, name, from_id, to_id, directed, min, max, description, status, confidence}", ontologyId, generationId, cancellationToken))
            .Select(row => new BusinessOntologyRelation(S(row, 0), S(row, 1), S(row, 2), S(row, 3), B(row, 4), S(row, 5), S(row, 6), S(row, 7), S(row, 8), D(row, 9), Refs("relation", S(row, 0))))
            .Where(item => SemanticStatuses.Contains(item.Status)).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var rules = (await RowsAsync("?[id, subject_id, kind, description, predicate_json, effect_json, status, confidence] := *onto_rule{ontology_id: $ontology_id, generation_id: $generation_id, rule_id: id, subject_id, kind, description, predicate_json, effect_json, status, confidence}", ontologyId, generationId, cancellationToken))
            .Select(row => new BusinessOntologyRule(S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), S(row, 5), S(row, 6), D(row, 7), Refs("rule", S(row, 0))))
            .Where(item => SemanticStatuses.Contains(item.Status)).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var lifecycles = (await RowsAsync("?[id, subject_id, state_property, initial_state, description, status, confidence] := *onto_lifecycle{ontology_id: $ontology_id, generation_id: $generation_id, lifecycle_id: id, subject_id, state_property, initial_state, description, status, confidence}", ontologyId, generationId, cancellationToken))
            .Select(row => new BusinessOntologyLifecycle(S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), S(row, 5), D(row, 6), Refs("lifecycle", S(row, 0))))
            .Where(item => SemanticStatuses.Contains(item.Status)).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var states = (await RowsAsync("?[lifecycle_id, id, terminal, description] := *onto_state{ontology_id: $ontology_id, generation_id: $generation_id, lifecycle_id, state_id: id, terminal, description}", ontologyId, generationId, cancellationToken))
            .Select(row => new BusinessOntologyState(S(row, 0), S(row, 1), B(row, 2), S(row, 3), Refs("state", StateKey(S(row, 0), S(row, 1))))).OrderBy(item => item.LifecycleId, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var transitions = (await RowsAsync("?[id, lifecycle_id, action, from_state, to_state, description, guard_json, effect_json, status, confidence] := *onto_transition{ontology_id: $ontology_id, generation_id: $generation_id, transition_id: id, lifecycle_id, action, from_state, to_state, description, guard_json, effect_json, status, confidence}", ontologyId, generationId, cancellationToken))
            .Select(row => new BusinessOntologyTransition(S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), S(row, 5), S(row, 6), S(row, 7), S(row, 8), D(row, 9), Refs("transition", S(row, 0))))
            .Where(item => SemanticStatuses.Contains(item.Status)).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var mappings = (await RowsAsync("?[id, subject_kind, subject_id, mapping_role, repository, language, code_kind, symbol, path, resolver, confidence, status] := *onto_mapping{ontology_id: $ontology_id, generation_id: $generation_id, mapping_id: id, subject_kind, subject_id, mapping_role, repository, language, code_kind, symbol, path, resolver, confidence, status}", ontologyId, generationId, cancellationToken))
            .Select(row => new BusinessOntologyMapping(S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), S(row, 5), S(row, 6), S(row, 7), S(row, 8), S(row, 9), D(row, 10), S(row, 11), Refs("mapping", S(row, 0))))
            .Where(item => SemanticStatuses.Contains(item.Status)).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var evidence = (await RowsAsync("?[id, repository, path, symbol, start_line, end_line, grade, resolver, confidence, source_kind, summary] := *onto_evidence{ontology_id: $ontology_id, generation_id: $generation_id, evidence_id: id, repository, path, symbol, start_line, end_line, grade, resolver, confidence, source_kind, summary}", ontologyId, generationId, cancellationToken))
            .Select(row => new BusinessOntologyEvidence(S(row, 0), S(row, 1), S(row, 2), S(row, 3), I(row, 4), I(row, 5), S(row, 6), S(row, 7), D(row, 8), S(row, 9), S(row, 10))).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var candidates = (await RowsAsync("?[id, subject_kind, proposed_id, payload_json, reason, confidence, status] := *onto_candidate{ontology_id: $ontology_id, generation_id: $generation_id, candidate_id: id, subject_kind, proposed_id, payload_json, reason, confidence, status}", ontologyId, generationId, cancellationToken))
            .Select(row => new BusinessOntologyCandidate(S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), D(row, 5), S(row, 6), Refs("candidate", S(row, 0)))).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var reviews = (await RowsAsync("?[id, candidate_id, decision, reviewer, rationale, reviewed_at] := *onto_review{ontology_id: $ontology_id, review_id: id, candidate_id, decision, reviewer, rationale, reviewed_at}", ontologyId, generationId, cancellationToken))
            .Select(row => new BusinessOntologyReview(S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), S(row, 5))).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var diagnostics = (await RowsAsync("?[id, kind, subject_kind, conflict_key, message, details_json, severity] := *onto_diagnostic{ontology_id: $ontology_id, generation_id: $generation_id, diagnostic_id: id, kind, subject_kind, conflict_key, message, details_json, severity}", ontologyId, generationId, cancellationToken))
            .Select(row => new BusinessOntologyDiagnostic(S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), S(row, 5), S(row, 6))).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();

        return new BusinessOntologySnapshot(ontologyId, generationId, concepts, attributes, relations, rules, lifecycles, states, transitions, mappings, evidence, candidates, reviews, diagnostics, refs);
    }

    public async Task<IReadOnlyList<BusinessOntologyReviewEntry>> ReadReviewHistoryAsync(
        string ontologyId,
        string? candidateId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateFqn(ontologyId, nameof(ontologyId));
        await InitializeAsync(cancellationToken);
        var reviews = await om.Runtime.Store.RunAsync(
            """
            ?[review_id, candidate_id, decision, reviewer, rationale, reviewed_at] :=
                *onto_review{ontology_id: $ontology_id, review_id, candidate_id, decision, reviewer, rationale, reviewed_at}
            """,
            Params(("ontology_id", ontologyId)),
            cancellationToken: cancellationToken);
        var expectations = await om.Runtime.Store.RunAsync(
            """
            ?[review_id, evidence_id] :=
                *onto_review_expectation{ontology_id: $ontology_id, review_id, evidence_id}
            """,
            Params(("ontology_id", ontologyId)),
            cancellationToken: cancellationToken);
        var expectedByReview = expectations.Rows
            .GroupBy(row => S(row, 0), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(row => S(row, 1))
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        return reviews.Rows
            .Select(row => new BusinessOntologyReview(
                S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), S(row, 5)))
            .Where(review => candidateId is null || StringComparer.Ordinal.Equals(review.CandidateId, candidateId))
            .Select(review => new BusinessOntologyReviewEntry(
                review,
                expectedByReview.GetValueOrDefault(review.Id, [])))
            .OrderBy(entry => entry.Review.ReviewedAt, StringComparer.Ordinal)
            .ThenBy(entry => entry.Review.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<BusinessOntologyEffectiveDecision>> ReadEffectiveDecisionsAsync(
        string ontologyId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadExportableAsync(ontologyId, cancellationToken);
        var history = await ReadReviewHistoryAsync(ontologyId, cancellationToken: cancellationToken);
        return snapshot.Candidates
            .Select(candidate =>
            {
                var currentEvidence = candidate.EvidenceIds
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();
                var effective = history
                    .Where(entry => StringComparer.Ordinal.Equals(entry.Review.CandidateId, candidate.Id)
                        && entry.ExpectedEvidenceIds.Count > 0
                        && entry.ExpectedEvidenceIds.SequenceEqual(currentEvidence, StringComparer.Ordinal))
                    .OrderBy(entry => entry.Review.ReviewedAt, StringComparer.Ordinal)
                    .ThenBy(entry => entry.Review.Id, StringComparer.Ordinal)
                    .LastOrDefault();
                return effective is null ? null : new BusinessOntologyEffectiveDecision(candidate, effective);
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => item.Candidate.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<BusinessOntologyReviewAppendResult>> AppendReviewsAsync(
        string ontologyId,
        string generationId,
        IReadOnlyList<BusinessOntologyReviewEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ValidateFqn(ontologyId, nameof(ontologyId));
        Require(generationId, nameof(generationId));
        ArgumentNullException.ThrowIfNull(entries);
        Unique(entries.Select(entry => entry.Review.Id), "review");
        foreach (var entry in entries)
        {
            ValidateReviewEntry(entry);
        }

        await InitializeAsync(cancellationToken);
        await using var transaction = await om.Runtime.Store.BeginTransactionAsync(write: true, cancellationToken);
        try
        {
            await ValidateReviewTargetsAsync(
                transaction,
                ontologyId,
                generationId,
                entries,
                cancellationToken);
            var results = new List<BusinessOntologyReviewAppendResult>(entries.Count);
            foreach (var entry in entries)
            {
                var existing = await ReadReviewEntryAsync(transaction, ontologyId, entry.Review.Id, cancellationToken);
                if (existing is not null)
                {
                    if (!SameReviewIdentity(existing, entry))
                    {
                        throw new InvalidOperationException(
                            $"Review identity '{entry.Review.Id}' already exists with different immutable content.");
                    }
                    results.Add(new BusinessOntologyReviewAppendResult(existing, false));
                    continue;
                }

                await InsertReviewEntryAsync(transaction, ontologyId, entry, cancellationToken);
                results.Add(new BusinessOntologyReviewAppendResult(entry, true));
            }
            await transaction.CommitAsync(cancellationToken);
            return results;
        }
        catch
        {
            await transaction.AbortAsync(cancellationToken);
            throw;
        }
    }

    public async Task ApplyMaterializationAsync(
        BusinessOntologyMaterializationBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateFqn(batch.OntologyId, nameof(batch.OntologyId));
        Require(batch.GenerationId, nameof(batch.GenerationId));
        Unique(batch.Expectations.Select(item => item.CandidateId), "materialization expectation");
        Unique(batch.Materializations.Select(item => item.CandidateId), "materialization");
        Unique(batch.Relations.Select(item => item.Id), "materialized relation");
        Unique(batch.Rules.Select(item => item.Id), "materialized rule");
        Unique(batch.Lifecycles.Select(item => item.Id), "materialized lifecycle");
        Unique(batch.Transitions.Select(item => item.Id), "materialized transition");
        Unique(batch.Diagnostics.Select(item => item.Id), "materialization diagnostic");

        await InitializeAsync(cancellationToken);
        await using var transaction = await om.Runtime.Store.BeginTransactionAsync(write: true, cancellationToken);
        try
        {
            await ValidateMaterializationExpectationsAsync(transaction, batch, cancellationToken);
            await RemovePriorMaterializationsAsync(
                transaction,
                batch.OntologyId,
                batch.GenerationId,
                cancellationToken);
            await RemoveStaleReviewDiagnosticsAsync(
                transaction,
                batch.OntologyId,
                batch.GenerationId,
                cancellationToken);

            foreach (var (candidateId, status) in batch.CandidateStatuses.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (!CandidateStatuses.Contains(status))
                {
                    throw new ArgumentException(
                        $"Candidate '{candidateId}' has invalid materialized status '{status}'.");
                }
                await PutAsync(
                    transaction,
                    """
                    ?[ontology_id, generation_id, candidate_id, subject_kind, proposed_id, payload_json, reason, confidence, status] :=
                        *onto_candidate{
                            ontology_id,
                            generation_id,
                            candidate_id,
                            subject_kind,
                            proposed_id,
                            payload_json,
                            reason,
                            confidence,
                            status: _
                        },
                        ontology_id == $ontology_id,
                        generation_id == $generation_id,
                        candidate_id == $candidate_id,
                        status = $status
                    :put onto_candidate {
                        ontology_id,
                        generation_id,
                        candidate_id =>
                        subject_kind,
                        proposed_id,
                        payload_json,
                        reason,
                        confidence,
                        status
                    }
                    """,
                    Params(
                        ("ontology_id", batch.OntologyId),
                        ("generation_id", batch.GenerationId),
                        ("candidate_id", candidateId),
                        ("status", status)),
                    cancellationToken);
            }

            foreach (var item in batch.Relations)
            {
                await PutMaterializedRelationAsync(transaction, batch, item, cancellationToken);
                await PutMaterializedEvidenceReferencesAsync(
                    transaction, batch, "relation", item.Id, item.EvidenceIds, cancellationToken);
            }
            foreach (var item in batch.Rules)
            {
                await PutMaterializedRuleAsync(transaction, batch, item, cancellationToken);
                await PutMaterializedEvidenceReferencesAsync(
                    transaction, batch, "rule", item.Id, item.EvidenceIds, cancellationToken);
            }
            foreach (var item in batch.Lifecycles)
            {
                await PutMaterializedLifecycleAsync(transaction, batch, item, cancellationToken);
                await PutMaterializedEvidenceReferencesAsync(
                    transaction, batch, "lifecycle", item.Id, item.EvidenceIds, cancellationToken);
            }
            foreach (var item in batch.States)
            {
                await PutMaterializedStateAsync(transaction, batch, item, cancellationToken);
                await PutMaterializedEvidenceReferencesAsync(
                    transaction,
                    batch,
                    "state",
                    StateKey(item.LifecycleId, item.Id),
                    item.EvidenceIds ?? [],
                    cancellationToken);
            }
            foreach (var item in batch.Transitions)
            {
                await PutMaterializedTransitionAsync(transaction, batch, item, cancellationToken);
                await PutMaterializedEvidenceReferencesAsync(
                    transaction, batch, "transition", item.Id, item.EvidenceIds, cancellationToken);
            }
            foreach (var item in batch.Materializations)
            {
                await PutAsync(
                    transaction,
                    """
                    ?[ontology_id, generation_id, candidate_id, subject_kind, subject_id, review_id, payload_json] <-
                        [[$ontology_id, $generation_id, $candidate_id, $subject_kind, $subject_id, $review_id, $payload_json]]
                    :put onto_materialization {
                        ontology_id,
                        generation_id,
                        candidate_id =>
                        subject_kind,
                        subject_id,
                        review_id,
                        payload_json
                    }
                    """,
                    Params(
                        ("ontology_id", batch.OntologyId),
                        ("generation_id", batch.GenerationId),
                        ("candidate_id", item.CandidateId),
                        ("subject_kind", item.SubjectKind),
                        ("subject_id", item.SubjectId),
                        ("review_id", item.ReviewId),
                        ("payload_json", item.PayloadJson)),
                    cancellationToken);
            }
            foreach (var item in batch.Diagnostics)
            {
                await PutMaterializedDiagnosticAsync(transaction, batch, item, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.AbortAsync(cancellationToken);
            throw;
        }
    }

    private static readonly string[] Creates =
    [
        ":create onto_generation {ontology_id => generation_id, source_fingerprint, generator_version, created_at}",
        ":create onto_concept {ontology_id, generation_id, concept_id => kind, label, description, status, confidence}",
        ":create onto_attribute {ontology_id, generation_id, concept_id, attr_name => value_type, required, description, status, confidence}",
        ":create onto_relation {ontology_id, generation_id, relation_id => name, from_id, to_id, directed, min, max, description, status, confidence}",
        ":create onto_rule {ontology_id, generation_id, rule_id => subject_id, kind, description, predicate_json, effect_json, status, confidence}",
        ":create onto_lifecycle {ontology_id, generation_id, lifecycle_id => subject_id, state_property, initial_state, description, status, confidence}",
        ":create onto_state {ontology_id, generation_id, lifecycle_id, state_id => terminal, description}",
        ":create onto_transition {ontology_id, generation_id, transition_id => lifecycle_id, action, from_state, to_state, description, guard_json, effect_json, status, confidence}",
        ":create onto_mapping {ontology_id, generation_id, mapping_id => subject_kind, subject_id, mapping_role, repository, language, code_kind, symbol, path, resolver, confidence, status}",
        ":create onto_evidence {ontology_id, generation_id, evidence_id => repository, path, symbol, start_line, end_line, grade, resolver, confidence, source_kind, summary}",
        ":create onto_evidence_ref {ontology_id, generation_id, subject_kind, subject_id, evidence_id => linked default true}",
        ":create onto_candidate {ontology_id, generation_id, candidate_id => subject_kind, proposed_id, payload_json, reason, confidence, status}",
        ":create onto_review {ontology_id, review_id => candidate_id, decision, reviewer, rationale, reviewed_at}",
        ":create onto_review_expectation {ontology_id, review_id, evidence_id => expected default true}",
        ":create onto_diagnostic {ontology_id, generation_id, diagnostic_id => kind, subject_kind, conflict_key, message, details_json, severity}",
        ":create onto_materialization {ontology_id, generation_id, candidate_id => subject_kind, subject_id, review_id, payload_json}",
        ":create onto_generation_tombstone {ontology_id, generation_id => reason, purged_at}",
    ];

    private async Task<string?> ActiveGenerationAsync(string ontologyId, CancellationToken cancellationToken)
    {
        var rows = await om.Runtime.Store.RunAsync("?[generation_id] := *onto_generation{ontology_id: $ontology_id, generation_id}", Params(("ontology_id", ontologyId)), cancellationToken: cancellationToken);
        return rows.Rows.Count == 0 ? null : S(rows.Rows[0], 0);
    }

    private static async Task RemoveGenerationAsync(ICozoOmTransaction tx, string ontologyId, string generationId, CancellationToken cancellationToken)
    {
        foreach (var relation in RequiredRelationNames.Where(
            name => name is not "onto_generation"
                and not "onto_review"
                and not "onto_review_expectation"
                and not "onto_generation_tombstone"))
        {
            var keys = relation switch
            {
                "onto_concept" => "ontology_id, generation_id, concept_id",
                "onto_attribute" => "ontology_id, generation_id, concept_id, attr_name",
                "onto_relation" => "ontology_id, generation_id, relation_id",
                "onto_rule" => "ontology_id, generation_id, rule_id",
                "onto_lifecycle" => "ontology_id, generation_id, lifecycle_id",
                "onto_state" => "ontology_id, generation_id, lifecycle_id, state_id",
                "onto_transition" => "ontology_id, generation_id, transition_id",
                "onto_mapping" => "ontology_id, generation_id, mapping_id",
                "onto_evidence" => "ontology_id, generation_id, evidence_id",
                "onto_evidence_ref" => "ontology_id, generation_id, subject_kind, subject_id, evidence_id",
                "onto_candidate" => "ontology_id, generation_id, candidate_id",
                "onto_diagnostic" => "ontology_id, generation_id, diagnostic_id",
                "onto_materialization" => "ontology_id, generation_id, candidate_id",
                _ => throw new InvalidOperationException($"Unknown onto relation: {relation}"),
            };
            await PutAsync(tx, $"?[{keys}] := *{relation}{{{keys}}}, ontology_id == $ontology_id, generation_id == $generation_id\n:rm {relation} {{{keys}}}", Params(("ontology_id", ontologyId), ("generation_id", generationId)), cancellationToken);
        }
    }

    private async Task<bool> GenerationTombstoneExistsAsync(
        string ontologyId,
        string generationId,
        CancellationToken cancellationToken)
    {
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[generation_id] :=
                *onto_generation_tombstone{
                    ontology_id: $ontology_id,
                    generation_id,
                    reason,
                    purged_at
                },
                generation_id == $generation_id
            """,
            Params(("ontology_id", ontologyId), ("generation_id", generationId)),
            cancellationToken: cancellationToken);
        return rows.Rows.Count != 0;
    }

    private static async Task ValidateMaterializationExpectationsAsync(
        ICozoOmStore store,
        BusinessOntologyMaterializationBatch batch,
        CancellationToken cancellationToken)
    {
        var activeRows = await store.RunAsync(
            "?[generation_id] := *onto_generation{ontology_id: $ontology_id, generation_id}",
            Params(("ontology_id", batch.OntologyId)),
            cancellationToken: cancellationToken);
        if (activeRows.Rows.Count == 0
            || !StringComparer.Ordinal.Equals(S(activeRows.Rows[0], 0), batch.GenerationId))
        {
            throw new InvalidOperationException(
                $"Ontology '{batch.OntologyId}' active generation changed before materialization.");
        }

        foreach (var expectation in batch.Expectations)
        {
            var reviewRows = await store.RunAsync(
                """
                ?[review_id, decision, reviewed_at] :=
                    *onto_review{
                        ontology_id: $ontology_id,
                        review_id,
                        candidate_id: $candidate_id,
                        decision,
                        reviewed_at
                    }
                """,
                Params(
                    ("ontology_id", batch.OntologyId),
                    ("candidate_id", expectation.CandidateId)),
                cancellationToken: cancellationToken);
            var latestReview = reviewRows.Rows
                .OrderBy(row => S(row, 2), StringComparer.Ordinal)
                .ThenBy(row => S(row, 0), StringComparer.Ordinal)
                .LastOrDefault()
                ?? throw new InvalidOperationException(
                    $"Candidate '{expectation.CandidateId}' review history changed before materialization.");
            if (!StringComparer.Ordinal.Equals(S(latestReview, 0), expectation.ReviewId)
                || !StringComparer.Ordinal.Equals(S(latestReview, 1), expectation.Decision))
            {
                throw new InvalidOperationException(
                    $"Candidate '{expectation.CandidateId}' effective review changed before materialization.");
            }

            var reviewEvidenceRows = await store.RunAsync(
                """
                ?[evidence_id] :=
                    *onto_review_expectation{
                        ontology_id: $ontology_id,
                        review_id: $review_id,
                        evidence_id
                    }
                """,
                Params(
                    ("ontology_id", batch.OntologyId),
                    ("review_id", expectation.ReviewId)),
                cancellationToken: cancellationToken);
            var reviewEvidence = reviewEvidenceRows.Rows
                .Select(row => S(row, 0))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            if (!reviewEvidence.SequenceEqual(
                    expectation.ReviewExpectedEvidenceIds.OrderBy(id => id, StringComparer.Ordinal),
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Candidate '{expectation.CandidateId}' review evidence changed before materialization.");
            }

            var candidateRows = await store.RunAsync(
                """
                ?[payload_json] :=
                    *onto_candidate{
                        ontology_id: $ontology_id,
                        generation_id: $generation_id,
                        candidate_id: $candidate_id,
                        payload_json
                    }
                """,
                Params(
                    ("ontology_id", batch.OntologyId),
                    ("generation_id", batch.GenerationId),
                    ("candidate_id", expectation.CandidateId)),
                cancellationToken: cancellationToken);
            if (expectation.PayloadJson is null)
            {
                if (candidateRows.Rows.Count != 0)
                {
                    throw new InvalidOperationException(
                        $"Candidate '{expectation.CandidateId}' reappeared before materialization.");
                }
                continue;
            }
            if (candidateRows.Rows.Count != 1
                || !StringComparer.Ordinal.Equals(S(candidateRows.Rows[0], 0), expectation.PayloadJson))
            {
                throw new InvalidOperationException(
                    $"Candidate '{expectation.CandidateId}' payload changed before materialization.");
            }

            var evidenceRows = await store.RunAsync(
                """
                ?[evidence_id] :=
                    *onto_evidence_ref{
                        ontology_id: $ontology_id,
                        generation_id: $generation_id,
                        subject_kind: "candidate",
                        subject_id: $candidate_id,
                        evidence_id
                    }
                """,
                Params(
                    ("ontology_id", batch.OntologyId),
                    ("generation_id", batch.GenerationId),
                    ("candidate_id", expectation.CandidateId)),
                cancellationToken: cancellationToken);
            var currentEvidence = evidenceRows.Rows
                .Select(row => S(row, 0))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            if (!currentEvidence.SequenceEqual(
                    expectation.CurrentEvidenceIds.OrderBy(id => id, StringComparer.Ordinal),
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Candidate '{expectation.CandidateId}' evidence changed before materialization.");
            }
        }
    }

    private static async Task RemovePriorMaterializationsAsync(
        ICozoOmStore store,
        string ontologyId,
        string generationId,
        CancellationToken cancellationToken)
    {
        var rows = await store.RunAsync(
            """
            ?[candidate_id, subject_kind, subject_id] :=
                *onto_materialization{
                    ontology_id: $ontology_id,
                    generation_id: $generation_id,
                    candidate_id,
                    subject_kind,
                    subject_id
                }
            """,
            Params(("ontology_id", ontologyId), ("generation_id", generationId)),
            cancellationToken: cancellationToken);
        foreach (var row in rows.Rows)
        {
            var subjectKind = S(row, 1);
            var subjectId = S(row, 2);
            if (subjectKind == "lifecycle")
            {
                var states = await store.RunAsync(
                    """
                    ?[state_id] :=
                        *onto_state{
                            ontology_id: $ontology_id,
                            generation_id: $generation_id,
                            lifecycle_id: $subject_id,
                            state_id
                        }
                    """,
                    Params(
                        ("ontology_id", ontologyId),
                        ("generation_id", generationId),
                        ("subject_id", subjectId)),
                    cancellationToken: cancellationToken);
                foreach (var state in states.Rows)
                {
                    await RemoveEvidenceReferencesAsync(
                        store,
                        ontologyId,
                        generationId,
                        "state",
                        StateKey(subjectId, S(state, 0)),
                        cancellationToken);
                }
                var transitions = await store.RunAsync(
                    """
                    ?[transition_id] :=
                        *onto_transition{
                            ontology_id: $ontology_id,
                            generation_id: $generation_id,
                            transition_id,
                            lifecycle_id: $subject_id
                        }
                    """,
                    Params(
                        ("ontology_id", ontologyId),
                        ("generation_id", generationId),
                        ("subject_id", subjectId)),
                    cancellationToken: cancellationToken);
                foreach (var transition in transitions.Rows)
                {
                    await RemoveEvidenceReferencesAsync(
                        store,
                        ontologyId,
                        generationId,
                        "transition",
                        S(transition, 0),
                        cancellationToken);
                }
                await PutAsync(
                    store,
                    """
                    ?[ontology_id, generation_id, lifecycle_id, state_id] :=
                        *onto_state{ontology_id, generation_id, lifecycle_id, state_id},
                        ontology_id == $ontology_id,
                        generation_id == $generation_id,
                        lifecycle_id == $subject_id
                    :rm onto_state {ontology_id, generation_id, lifecycle_id, state_id}
                    """,
                    Params(
                        ("ontology_id", ontologyId),
                        ("generation_id", generationId),
                        ("subject_id", subjectId)),
                    cancellationToken);
                await PutAsync(
                    store,
                    """
                    ?[ontology_id, generation_id, transition_id] :=
                        *onto_transition{ontology_id, generation_id, transition_id, lifecycle_id},
                        ontology_id == $ontology_id,
                        generation_id == $generation_id,
                        lifecycle_id == $subject_id
                    :rm onto_transition {ontology_id, generation_id, transition_id}
                    """,
                    Params(
                        ("ontology_id", ontologyId),
                        ("generation_id", generationId),
                        ("subject_id", subjectId)),
                    cancellationToken);
            }

            await RemoveEvidenceReferencesAsync(
                store,
                ontologyId,
                generationId,
                subjectKind,
                subjectId,
                cancellationToken);
            var relation = subjectKind switch
            {
                "relation" => ("onto_relation", "relation_id"),
                "rule" => ("onto_rule", "rule_id"),
                "lifecycle" => ("onto_lifecycle", "lifecycle_id"),
                _ => throw new InvalidOperationException(
                    $"Unsupported materialized subject kind '{subjectKind}'."),
            };
            await PutAsync(
                store,
                $"?[ontology_id, generation_id, {relation.Item2}] := *{relation.Item1}{{ontology_id, generation_id, {relation.Item2}}}, ontology_id == $ontology_id, generation_id == $generation_id, {relation.Item2} == $subject_id\n:rm {relation.Item1} {{ontology_id, generation_id, {relation.Item2}}}",
                Params(
                    ("ontology_id", ontologyId),
                    ("generation_id", generationId),
                    ("subject_id", subjectId)),
                cancellationToken);
        }

        await PutAsync(
            store,
            """
            ?[ontology_id, generation_id, candidate_id] :=
                *onto_materialization{ontology_id, generation_id, candidate_id},
                ontology_id == $ontology_id,
                generation_id == $generation_id
            :rm onto_materialization {ontology_id, generation_id, candidate_id}
            """,
            Params(("ontology_id", ontologyId), ("generation_id", generationId)),
            cancellationToken);
    }

    private static Task RemoveStaleReviewDiagnosticsAsync(
        ICozoOmStore store,
        string ontologyId,
        string generationId,
        CancellationToken cancellationToken) =>
        PutAsync(
            store,
            """
            ?[ontology_id, generation_id, diagnostic_id] :=
                *onto_diagnostic{ontology_id, generation_id, diagnostic_id, kind},
                ontology_id == $ontology_id,
                generation_id == $generation_id,
                kind == "stale_review"
            :rm onto_diagnostic {ontology_id, generation_id, diagnostic_id}
            """,
            Params(("ontology_id", ontologyId), ("generation_id", generationId)),
            cancellationToken);

    private static Task RemoveEvidenceReferencesAsync(
        ICozoOmStore store,
        string ontologyId,
        string generationId,
        string subjectKind,
        string subjectId,
        CancellationToken cancellationToken) =>
        PutAsync(
            store,
            """
            ?[ontology_id, generation_id, subject_kind, subject_id, evidence_id] :=
                *onto_evidence_ref{ontology_id, generation_id, subject_kind, subject_id, evidence_id},
                ontology_id == $ontology_id,
                generation_id == $generation_id,
                subject_kind == $subject_kind,
                subject_id == $subject_id
            :rm onto_evidence_ref {ontology_id, generation_id, subject_kind, subject_id, evidence_id}
            """,
            Params(
                ("ontology_id", ontologyId),
                ("generation_id", generationId),
                ("subject_kind", subjectKind),
                ("subject_id", subjectId)),
            cancellationToken);

    private static async Task PutConceptAsync(ICozoOmTransaction tx, BusinessOntologyGenerationInput input, BusinessOntologyConcept item, CancellationToken ct) => await PutAsync(tx, "?[ontology_id,generation_id,concept_id,kind,label,description,status,confidence] <- [[$ontology_id,$generation_id,$concept_id,$kind,$label,$description,$status,$confidence]] :put onto_concept {ontology_id,generation_id,concept_id => kind,label,description,status,confidence}", Params(("ontology_id", input.OntologyId), ("generation_id", input.GenerationId), ("concept_id", item.Id), ("kind", item.Kind), ("label", item.Label), ("description", item.Description), ("status", item.Status), ("confidence", item.Confidence)), ct);
    private static async Task PutAttributeAsync(ICozoOmTransaction tx, BusinessOntologyGenerationInput input, BusinessOntologyAttribute item, CancellationToken ct) => await PutAsync(tx, "?[ontology_id,generation_id,concept_id,attr_name,value_type,required,description,status,confidence] <- [[$ontology_id,$generation_id,$concept_id,$attr_name,$value_type,$required,$description,$status,$confidence]] :put onto_attribute {ontology_id,generation_id,concept_id,attr_name => value_type,required,description,status,confidence}", Params(("ontology_id", input.OntologyId), ("generation_id", input.GenerationId), ("concept_id", item.ConceptId), ("attr_name", item.Name), ("value_type", item.ValueType), ("required", item.Required), ("description", item.Description), ("status", item.Status), ("confidence", item.Confidence)), ct);
    private static async Task PutRelationAsync(ICozoOmTransaction tx, BusinessOntologyGenerationInput input, BusinessOntologyRelation item, CancellationToken ct) => await PutAsync(tx, "?[ontology_id,generation_id,relation_id,name,from_id,to_id,directed,min,max,description,status,confidence] <- [[$ontology_id,$generation_id,$relation_id,$name,$from_id,$to_id,$directed,$min,$max,$description,$status,$confidence]] :put onto_relation {ontology_id,generation_id,relation_id => name,from_id,to_id,directed,min,max,description,status,confidence}", Params(("ontology_id", input.OntologyId), ("generation_id", input.GenerationId), ("relation_id", item.Id), ("name", item.Name), ("from_id", item.FromConceptId), ("to_id", item.ToConceptId), ("directed", item.Directed), ("min", item.Min), ("max", item.Max), ("description", item.Description), ("status", item.Status), ("confidence", item.Confidence)), ct);
    private static async Task PutRuleAsync(ICozoOmTransaction tx, BusinessOntologyGenerationInput input, BusinessOntologyRule item, CancellationToken ct) => await PutAsync(tx, "?[ontology_id,generation_id,rule_id,subject_id,kind,description,predicate_json,effect_json,status,confidence] <- [[$ontology_id,$generation_id,$rule_id,$subject_id,$kind,$description,$predicate_json,$effect_json,$status,$confidence]] :put onto_rule {ontology_id,generation_id,rule_id => subject_id,kind,description,predicate_json,effect_json,status,confidence}", Params(("ontology_id", input.OntologyId), ("generation_id", input.GenerationId), ("rule_id", item.Id), ("subject_id", item.SubjectId), ("kind", item.Kind), ("description", item.Description), ("predicate_json", item.PredicateJson), ("effect_json", item.EffectJson), ("status", item.Status), ("confidence", item.Confidence)), ct);
    private static async Task PutLifecycleAsync(ICozoOmTransaction tx, BusinessOntologyGenerationInput input, BusinessOntologyLifecycle item, CancellationToken ct) => await PutAsync(tx, "?[ontology_id,generation_id,lifecycle_id,subject_id,state_property,initial_state,description,status,confidence] <- [[$ontology_id,$generation_id,$lifecycle_id,$subject_id,$state_property,$initial_state,$description,$status,$confidence]] :put onto_lifecycle {ontology_id,generation_id,lifecycle_id => subject_id,state_property,initial_state,description,status,confidence}", Params(("ontology_id", input.OntologyId), ("generation_id", input.GenerationId), ("lifecycle_id", item.Id), ("subject_id", item.SubjectId), ("state_property", item.StateProperty), ("initial_state", item.InitialState), ("description", item.Description), ("status", item.Status), ("confidence", item.Confidence)), ct);
    private static async Task PutStateAsync(ICozoOmTransaction tx, BusinessOntologyGenerationInput input, BusinessOntologyState item, CancellationToken ct) => await PutAsync(tx, "?[ontology_id,generation_id,lifecycle_id,state_id,terminal,description] <- [[$ontology_id,$generation_id,$lifecycle_id,$state_id,$terminal,$description]] :put onto_state {ontology_id,generation_id,lifecycle_id,state_id => terminal,description}", Params(("ontology_id", input.OntologyId), ("generation_id", input.GenerationId), ("lifecycle_id", item.LifecycleId), ("state_id", item.Id), ("terminal", item.Terminal), ("description", item.Description)), ct);
    private static async Task PutTransitionAsync(ICozoOmTransaction tx, BusinessOntologyGenerationInput input, BusinessOntologyTransition item, CancellationToken ct) => await PutAsync(tx, "?[ontology_id,generation_id,transition_id,lifecycle_id,action,from_state,to_state,description,guard_json,effect_json,status,confidence] <- [[$ontology_id,$generation_id,$transition_id,$lifecycle_id,$action,$from_state,$to_state,$description,$guard_json,$effect_json,$status,$confidence]] :put onto_transition {ontology_id,generation_id,transition_id => lifecycle_id,action,from_state,to_state,description,guard_json,effect_json,status,confidence}", Params(("ontology_id", input.OntologyId), ("generation_id", input.GenerationId), ("transition_id", item.Id), ("lifecycle_id", item.LifecycleId), ("action", item.Action), ("from_state", item.FromState), ("to_state", item.ToState), ("description", item.Description), ("guard_json", item.GuardJson), ("effect_json", item.EffectJson), ("status", item.Status), ("confidence", item.Confidence)), ct);
    private static async Task PutMappingAsync(ICozoOmTransaction tx, BusinessOntologyGenerationInput input, BusinessOntologyMapping item, CancellationToken ct) => await PutAsync(tx, "?[ontology_id,generation_id,mapping_id,subject_kind,subject_id,mapping_role,repository,language,code_kind,symbol,path,resolver,confidence,status] <- [[$ontology_id,$generation_id,$mapping_id,$subject_kind,$subject_id,$mapping_role,$repository,$language,$code_kind,$symbol,$path,$resolver,$confidence,$status]] :put onto_mapping {ontology_id,generation_id,mapping_id => subject_kind,subject_id,mapping_role,repository,language,code_kind,symbol,path,resolver,confidence,status}", Params(("ontology_id", input.OntologyId), ("generation_id", input.GenerationId), ("mapping_id", item.Id), ("subject_kind", item.SubjectKind), ("subject_id", item.SubjectId), ("mapping_role", item.MappingRole), ("repository", item.Repository), ("language", item.Language), ("code_kind", item.CodeKind), ("symbol", item.Symbol), ("path", item.Path), ("resolver", item.Resolver), ("confidence", item.Confidence), ("status", item.Status)), ct);
    private static async Task PutEvidenceAsync(ICozoOmTransaction tx, BusinessOntologyGenerationInput input, BusinessOntologyEvidence item, CancellationToken ct) => await PutAsync(tx, "?[ontology_id,generation_id,evidence_id,repository,path,symbol,start_line,end_line,grade,resolver,confidence,source_kind,summary] <- [[$ontology_id,$generation_id,$evidence_id,$repository,$path,$symbol,$start_line,$end_line,$grade,$resolver,$confidence,$source_kind,$summary]] :put onto_evidence {ontology_id,generation_id,evidence_id => repository,path,symbol,start_line,end_line,grade,resolver,confidence,source_kind,summary}", Params(("ontology_id", input.OntologyId), ("generation_id", input.GenerationId), ("evidence_id", item.Id), ("repository", item.Repository), ("path", item.Path), ("symbol", item.Symbol), ("start_line", item.StartLine), ("end_line", item.EndLine), ("grade", item.Grade), ("resolver", item.Resolver), ("confidence", item.Confidence), ("source_kind", item.SourceKind), ("summary", item.Summary)), ct);
    private static async Task PutCandidateAsync(ICozoOmTransaction tx, BusinessOntologyGenerationInput input, BusinessOntologyCandidate item, CancellationToken ct) => await PutAsync(tx, "?[ontology_id,generation_id,candidate_id,subject_kind,proposed_id,payload_json,reason,confidence,status] <- [[$ontology_id,$generation_id,$candidate_id,$subject_kind,$proposed_id,$payload_json,$reason,$confidence,$status]] :put onto_candidate {ontology_id,generation_id,candidate_id => subject_kind,proposed_id,payload_json,reason,confidence,status}", Params(("ontology_id", input.OntologyId), ("generation_id", input.GenerationId), ("candidate_id", item.Id), ("subject_kind", item.SubjectKind), ("proposed_id", item.ProposedId), ("payload_json", item.PayloadJson), ("reason", item.Reason), ("confidence", item.Confidence), ("status", item.Status)), ct);
    private static async Task PutDiagnosticAsync(ICozoOmTransaction tx, BusinessOntologyGenerationInput input, BusinessOntologyDiagnostic item, CancellationToken ct) => await PutAsync(tx, "?[ontology_id,generation_id,diagnostic_id,kind,subject_kind,conflict_key,message,details_json,severity] <- [[$ontology_id,$generation_id,$diagnostic_id,$kind,$subject_kind,$conflict_key,$message,$details_json,$severity]] :put onto_diagnostic {ontology_id,generation_id,diagnostic_id => kind,subject_kind,conflict_key,message,details_json,severity}", Params(("ontology_id", input.OntologyId), ("generation_id", input.GenerationId), ("diagnostic_id", item.Id), ("kind", item.Kind), ("subject_kind", item.SubjectKind), ("conflict_key", item.ConflictKey), ("message", item.Message), ("details_json", item.DetailsJson), ("severity", item.Severity)), ct);

    private static Task PutMaterializedRelationAsync(
        ICozoOmStore store,
        BusinessOntologyMaterializationBatch batch,
        BusinessOntologyRelation item,
        CancellationToken cancellationToken) =>
        PutAsync(
            store,
            "?[ontology_id,generation_id,relation_id,name,from_id,to_id,directed,min,max,description,status,confidence] <- [[$ontology_id,$generation_id,$relation_id,$name,$from_id,$to_id,$directed,$min,$max,$description,$status,$confidence]] :put onto_relation {ontology_id,generation_id,relation_id => name,from_id,to_id,directed,min,max,description,status,confidence}",
            Params(("ontology_id", batch.OntologyId), ("generation_id", batch.GenerationId), ("relation_id", item.Id), ("name", item.Name), ("from_id", item.FromConceptId), ("to_id", item.ToConceptId), ("directed", item.Directed), ("min", item.Min), ("max", item.Max), ("description", item.Description), ("status", item.Status), ("confidence", item.Confidence)),
            cancellationToken);

    private static Task PutMaterializedRuleAsync(
        ICozoOmStore store,
        BusinessOntologyMaterializationBatch batch,
        BusinessOntologyRule item,
        CancellationToken cancellationToken) =>
        PutAsync(
            store,
            "?[ontology_id,generation_id,rule_id,subject_id,kind,description,predicate_json,effect_json,status,confidence] <- [[$ontology_id,$generation_id,$rule_id,$subject_id,$kind,$description,$predicate_json,$effect_json,$status,$confidence]] :put onto_rule {ontology_id,generation_id,rule_id => subject_id,kind,description,predicate_json,effect_json,status,confidence}",
            Params(("ontology_id", batch.OntologyId), ("generation_id", batch.GenerationId), ("rule_id", item.Id), ("subject_id", item.SubjectId), ("kind", item.Kind), ("description", item.Description), ("predicate_json", item.PredicateJson), ("effect_json", item.EffectJson), ("status", item.Status), ("confidence", item.Confidence)),
            cancellationToken);

    private static Task PutMaterializedLifecycleAsync(
        ICozoOmStore store,
        BusinessOntologyMaterializationBatch batch,
        BusinessOntologyLifecycle item,
        CancellationToken cancellationToken) =>
        PutAsync(
            store,
            "?[ontology_id,generation_id,lifecycle_id,subject_id,state_property,initial_state,description,status,confidence] <- [[$ontology_id,$generation_id,$lifecycle_id,$subject_id,$state_property,$initial_state,$description,$status,$confidence]] :put onto_lifecycle {ontology_id,generation_id,lifecycle_id => subject_id,state_property,initial_state,description,status,confidence}",
            Params(("ontology_id", batch.OntologyId), ("generation_id", batch.GenerationId), ("lifecycle_id", item.Id), ("subject_id", item.SubjectId), ("state_property", item.StateProperty), ("initial_state", item.InitialState), ("description", item.Description), ("status", item.Status), ("confidence", item.Confidence)),
            cancellationToken);

    private static Task PutMaterializedStateAsync(
        ICozoOmStore store,
        BusinessOntologyMaterializationBatch batch,
        BusinessOntologyState item,
        CancellationToken cancellationToken) =>
        PutAsync(
            store,
            "?[ontology_id,generation_id,lifecycle_id,state_id,terminal,description] <- [[$ontology_id,$generation_id,$lifecycle_id,$state_id,$terminal,$description]] :put onto_state {ontology_id,generation_id,lifecycle_id,state_id => terminal,description}",
            Params(("ontology_id", batch.OntologyId), ("generation_id", batch.GenerationId), ("lifecycle_id", item.LifecycleId), ("state_id", item.Id), ("terminal", item.Terminal), ("description", item.Description)),
            cancellationToken);

    private static Task PutMaterializedTransitionAsync(
        ICozoOmStore store,
        BusinessOntologyMaterializationBatch batch,
        BusinessOntologyTransition item,
        CancellationToken cancellationToken) =>
        PutAsync(
            store,
            "?[ontology_id,generation_id,transition_id,lifecycle_id,action,from_state,to_state,description,guard_json,effect_json,status,confidence] <- [[$ontology_id,$generation_id,$transition_id,$lifecycle_id,$action,$from_state,$to_state,$description,$guard_json,$effect_json,$status,$confidence]] :put onto_transition {ontology_id,generation_id,transition_id => lifecycle_id,action,from_state,to_state,description,guard_json,effect_json,status,confidence}",
            Params(("ontology_id", batch.OntologyId), ("generation_id", batch.GenerationId), ("transition_id", item.Id), ("lifecycle_id", item.LifecycleId), ("action", item.Action), ("from_state", item.FromState), ("to_state", item.ToState), ("description", item.Description), ("guard_json", item.GuardJson), ("effect_json", item.EffectJson), ("status", item.Status), ("confidence", item.Confidence)),
            cancellationToken);

    private static Task PutMaterializedDiagnosticAsync(
        ICozoOmStore store,
        BusinessOntologyMaterializationBatch batch,
        BusinessOntologyDiagnostic item,
        CancellationToken cancellationToken) =>
        PutAsync(
            store,
            "?[ontology_id,generation_id,diagnostic_id,kind,subject_kind,conflict_key,message,details_json,severity] <- [[$ontology_id,$generation_id,$diagnostic_id,$kind,$subject_kind,$conflict_key,$message,$details_json,$severity]] :put onto_diagnostic {ontology_id,generation_id,diagnostic_id => kind,subject_kind,conflict_key,message,details_json,severity}",
            Params(("ontology_id", batch.OntologyId), ("generation_id", batch.GenerationId), ("diagnostic_id", item.Id), ("kind", item.Kind), ("subject_kind", item.SubjectKind), ("conflict_key", item.ConflictKey), ("message", item.Message), ("details_json", item.DetailsJson), ("severity", item.Severity)),
            cancellationToken);

    private static async Task PutMaterializedEvidenceReferencesAsync(
        ICozoOmStore store,
        BusinessOntologyMaterializationBatch batch,
        string subjectKind,
        string subjectId,
        IReadOnlyList<string> evidenceIds,
        CancellationToken cancellationToken)
    {
        foreach (var evidenceId in evidenceIds.Distinct(StringComparer.Ordinal))
        {
            await PutAsync(
                store,
                "?[ontology_id,generation_id,subject_kind,subject_id,evidence_id,linked] <- [[$ontology_id,$generation_id,$subject_kind,$subject_id,$evidence_id,true]] :put onto_evidence_ref {ontology_id,generation_id,subject_kind,subject_id,evidence_id => linked}",
                Params(("ontology_id", batch.OntologyId), ("generation_id", batch.GenerationId), ("subject_kind", subjectKind), ("subject_id", subjectId), ("evidence_id", evidenceId)),
                cancellationToken);
        }
    }

    private static async Task PutLegacyReviewAsync(
        ICozoOmTransaction tx,
        string ontologyId,
        BusinessOntologyReview review,
        CancellationToken cancellationToken)
    {
        var existing = await ReadReviewEntryAsync(tx, ontologyId, review.Id, cancellationToken);
        var entry = new BusinessOntologyReviewEntry(review, []);
        if (existing is null)
        {
            await InsertReviewEntryAsync(tx, ontologyId, entry, cancellationToken);
            return;
        }
        if (!SameReviewIdentity(existing, entry))
        {
            throw new InvalidOperationException(
                $"Review identity '{review.Id}' already exists with different immutable content.");
        }
    }

    private static async Task InsertReviewEntryAsync(
        ICozoOmStore store,
        string ontologyId,
        BusinessOntologyReviewEntry entry,
        CancellationToken cancellationToken)
    {
        var review = entry.Review;
        await PutAsync(
            store,
            """
            ?[ontology_id, review_id, candidate_id, decision, reviewer, rationale, reviewed_at] <-
                [[$ontology_id, $review_id, $candidate_id, $decision, $reviewer, $rationale, $reviewed_at]]
            :insert onto_review {ontology_id, review_id => candidate_id, decision, reviewer, rationale, reviewed_at}
            """,
            Params(
                ("ontology_id", ontologyId),
                ("review_id", review.Id),
                ("candidate_id", review.CandidateId),
                ("decision", review.Decision),
                ("reviewer", review.Reviewer),
                ("rationale", review.Rationale),
                ("reviewed_at", review.ReviewedAt)),
            cancellationToken);
        foreach (var evidenceId in entry.ExpectedEvidenceIds)
        {
            await PutAsync(
                store,
                """
                ?[ontology_id, review_id, evidence_id, expected] <-
                    [[$ontology_id, $review_id, $evidence_id, true]]
                :insert onto_review_expectation {ontology_id, review_id, evidence_id => expected}
                """,
                Params(
                    ("ontology_id", ontologyId),
                    ("review_id", review.Id),
                    ("evidence_id", evidenceId)),
                cancellationToken);
        }
    }

    private static async Task ValidateReviewTargetsAsync(
        ICozoOmStore store,
        string ontologyId,
        string generationId,
        IReadOnlyList<BusinessOntologyReviewEntry> entries,
        CancellationToken cancellationToken)
    {
        var activeRows = await store.RunAsync(
            "?[generation_id] := *onto_generation{ontology_id: $ontology_id, generation_id}",
            Params(("ontology_id", ontologyId)),
            cancellationToken: cancellationToken);
        if (activeRows.Rows.Count == 0
            || !StringComparer.Ordinal.Equals(S(activeRows.Rows[0], 0), generationId))
        {
            throw new InvalidOperationException(
                $"Ontology '{ontologyId}' active generation changed before review append.");
        }

        foreach (var entry in entries)
        {
            var candidateId = entry.Review.CandidateId;
            var candidateRows = await store.RunAsync(
                """
                ?[status] :=
                    *onto_candidate{
                        ontology_id: $ontology_id,
                        generation_id: $generation_id,
                        candidate_id: $candidate_id,
                        status
                    }
                """,
                Params(
                    ("ontology_id", ontologyId),
                    ("generation_id", generationId),
                    ("candidate_id", candidateId)),
                cancellationToken: cancellationToken);
            if (candidateRows.Rows.Count == 0)
            {
                throw new ArgumentException(
                    $"Review candidate '{candidateId}' is absent from the active generation.");
            }

            var priorReviewRows = await store.RunAsync(
                """
                ?[review_id] :=
                    *onto_review{
                        ontology_id: $ontology_id,
                        review_id,
                        candidate_id: $candidate_id
                    }
                """,
                Params(("ontology_id", ontologyId), ("candidate_id", candidateId)),
                cancellationToken: cancellationToken);
            if (!StringComparer.Ordinal.Equals(S(candidateRows.Rows[0], 0), "pending")
                && priorReviewRows.Rows.Count == 0)
            {
                throw new ArgumentException(
                    $"Review candidate '{candidateId}' is neither pending nor previously reviewed.");
            }

            var evidenceRows = await store.RunAsync(
                """
                ?[evidence_id] :=
                    *onto_evidence_ref{
                        ontology_id: $ontology_id,
                        generation_id: $generation_id,
                        subject_kind: "candidate",
                        subject_id: $candidate_id,
                        evidence_id
                    }
                """,
                Params(
                    ("ontology_id", ontologyId),
                    ("generation_id", generationId),
                    ("candidate_id", candidateId)),
                cancellationToken: cancellationToken);
            var currentEvidence = evidenceRows.Rows
                .Select(row => S(row, 0))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            if (!currentEvidence.SequenceEqual(
                    entry.ExpectedEvidenceIds.OrderBy(id => id, StringComparer.Ordinal),
                    StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Review candidate '{candidateId}' expected evidence changed before append.");
            }
        }
    }

    private static async Task<BusinessOntologyReviewEntry?> ReadReviewEntryAsync(
        ICozoOmStore store,
        string ontologyId,
        string reviewId,
        CancellationToken cancellationToken)
    {
        var reviewRows = await store.RunAsync(
            """
            ?[candidate_id, decision, reviewer, rationale, reviewed_at] :=
                *onto_review{
                    ontology_id: $ontology_id,
                    review_id: $review_id,
                    candidate_id,
                    decision,
                    reviewer,
                    rationale,
                    reviewed_at
                }
            """,
            Params(("ontology_id", ontologyId), ("review_id", reviewId)),
            cancellationToken: cancellationToken);
        if (reviewRows.Rows.Count == 0)
        {
            return null;
        }
        var row = reviewRows.Rows[0];
        var expectationRows = await store.RunAsync(
            """
            ?[evidence_id] :=
                *onto_review_expectation{
                    ontology_id: $ontology_id,
                    review_id: $review_id,
                    evidence_id
                }
            """,
            Params(("ontology_id", ontologyId), ("review_id", reviewId)),
            cancellationToken: cancellationToken);
        return new BusinessOntologyReviewEntry(
            new BusinessOntologyReview(
                reviewId, S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4)),
            expectationRows.Rows
                .Select(expectation => S(expectation, 0))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray());
    }

    private static bool SameReviewIdentity(
        BusinessOntologyReviewEntry left,
        BusinessOntologyReviewEntry right) =>
        StringComparer.Ordinal.Equals(left.Review.Id, right.Review.Id)
        && StringComparer.Ordinal.Equals(left.Review.CandidateId, right.Review.CandidateId)
        && StringComparer.Ordinal.Equals(left.Review.Decision, right.Review.Decision)
        && StringComparer.Ordinal.Equals(left.Review.Reviewer, right.Review.Reviewer)
        && StringComparer.Ordinal.Equals(left.Review.Rationale, right.Review.Rationale)
        && left.ExpectedEvidenceIds.SequenceEqual(
            right.ExpectedEvidenceIds.OrderBy(id => id, StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static void ValidateReviewEntry(BusinessOntologyReviewEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Require(entry.Review.Id, "review.id");
        Require(entry.Review.CandidateId, "review.candidateId");
        if (entry.Review.Decision is not ("accepted" or "rejected" or "superseded"))
        {
            throw new ArgumentException($"Review '{entry.Review.Id}' has invalid decision.");
        }
        Require(entry.Review.Reviewer, "review.reviewer");
        Require(entry.Review.Rationale, "review.rationale");
        Require(entry.Review.ReviewedAt, "review.reviewedAt");
        if (!DateTimeOffset.TryParseExact(
                entry.Review.ReviewedAt,
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var reviewedAt)
            || reviewedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"Review '{entry.Review.Id}' reviewedAt must be a UTC round-trip timestamp.");
        }
        if (entry.ExpectedEvidenceIds.Count == 0)
        {
            throw new ArgumentException($"Review '{entry.Review.Id}' requires an expected evidence snapshot.");
        }
        Unique(entry.ExpectedEvidenceIds, "review evidence");
        foreach (var evidenceId in entry.ExpectedEvidenceIds)
        {
            Require(evidenceId, "review.expectedEvidenceId");
        }
    }

    private static async Task PutEvidenceReferencesAsync(ICozoOmTransaction tx, BusinessOntologyGenerationInput input, CancellationToken ct)
    {
        IEnumerable<(string Kind, string Id, IReadOnlyList<string> EvidenceIds)> subjects =
            input.Concepts.Select(item => ("concept", item.Id, item.EvidenceIds))
            .Concat(input.Attributes.Select(item => ("attribute", AttributeKey(item.ConceptId, item.Name), item.EvidenceIds)))
            .Concat(input.Relations.Select(item => ("relation", item.Id, item.EvidenceIds)))
            .Concat(input.Rules.Select(item => ("rule", item.Id, item.EvidenceIds)))
            .Concat(input.Lifecycles.Select(item => ("lifecycle", item.Id, item.EvidenceIds)))
            .Concat(input.States.Select(item => ("state", StateKey(item.LifecycleId, item.Id), item.EvidenceIds ?? [])))
            .Concat(input.Transitions.Select(item => ("transition", item.Id, item.EvidenceIds)))
            .Concat(input.Mappings.Select(item => ("mapping", item.Id, item.EvidenceIds)))
            .Concat(input.Candidates.Select(item => ("candidate", item.Id, item.EvidenceIds)));
        foreach (var (kind, id, ids) in subjects)
        foreach (var evidenceId in ids.Distinct(StringComparer.Ordinal))
            await PutAsync(tx, "?[ontology_id,generation_id,subject_kind,subject_id,evidence_id,linked] <- [[$ontology_id,$generation_id,$subject_kind,$subject_id,$evidence_id,true]] :put onto_evidence_ref {ontology_id,generation_id,subject_kind,subject_id,evidence_id => linked}", Params(("ontology_id", input.OntologyId), ("generation_id", input.GenerationId), ("subject_kind", kind), ("subject_id", id), ("evidence_id", evidenceId)), ct);
    }

    private async Task<IReadOnlyList<BusinessOntologyEvidenceReference>> ReadEvidenceReferencesAsync(string ontologyId, string generationId, CancellationToken ct) =>
        (await RowsAsync("?[subject_kind, subject_id, evidence_id] := *onto_evidence_ref{ontology_id: $ontology_id, generation_id: $generation_id, subject_kind, subject_id, evidence_id}", ontologyId, generationId, ct))
        .Select(row => new BusinessOntologyEvidenceReference(S(row, 0), S(row, 1), S(row, 2))).ToArray();

    private async Task<IReadOnlyList<IReadOnlyList<JsonElement>>> RowsAsync(string script, string ontologyId, string generationId, CancellationToken ct) => (await om.Runtime.Store.RunAsync(script, Params(("ontology_id", ontologyId), ("generation_id", generationId)), cancellationToken: ct)).Rows;
    private static Task PutAsync(ICozoOmStore store, string script, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct) => store.RunAsync(script, parameters, cancellationToken: ct);
    private static Dictionary<string, object?> Params(params (string Key, object? Value)[] values) => values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    private static void Validate(BusinessOntologyGenerationInput input)
    {
        ValidateFqn(input.OntologyId, nameof(input.OntologyId));
        Require(input.GenerationId, nameof(input.GenerationId)); Require(input.SourceFingerprint, nameof(input.SourceFingerprint)); Require(input.GeneratorVersion, nameof(input.GeneratorVersion)); Require(input.CreatedAt, nameof(input.CreatedAt));
        Unique(input.Concepts.Select(item => item.Id), "concept"); Unique(input.Relations.Select(item => item.Id), "relation"); Unique(input.Rules.Select(item => item.Id), "rule"); Unique(input.Lifecycles.Select(item => item.Id), "lifecycle"); Unique(input.Transitions.Select(item => item.Id), "transition"); Unique(input.Mappings.Select(item => item.Id), "mapping"); Unique(input.Evidence.Select(item => item.Id), "evidence"); Unique(input.Candidates.Select(item => item.Id), "candidate"); Unique(input.Reviews.Select(item => item.Id), "review"); Unique(input.Diagnostics.Select(item => item.Id), "diagnostic");
        var evidence = input.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var item in input.Evidence) { Require(item.Repository, "evidence.repository"); ValidatePath(item.Path); if (item.StartLine <= 0 || item.EndLine < item.StartLine) throw new ArgumentException("Evidence line range must be positive and ordered."); ValidateGrade(item.Grade); ValidateConfidence(item.Confidence); Require(item.Resolver, "evidence.resolver"); Require(item.SourceKind, "evidence.sourceKind"); }
        var concepts = input.Concepts.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in input.Concepts) { ValidateFqn(item.Id, "concept.id"); ValidateStatus(item.Status); ValidateConfidence(item.Confidence); ValidateRefs(item.EvidenceIds, evidence, item.Status, item.Id); }
        foreach (var item in input.Attributes) { if (!concepts.Contains(item.ConceptId)) throw new ArgumentException($"Attribute '{item.Name}' references unknown concept '{item.ConceptId}'."); if (!LowerCamel.IsMatch(item.Name) || !PrimitiveTypes.Contains(item.ValueType)) throw new ArgumentException("Attribute name or value type is invalid."); ValidateStatus(item.Status); ValidateConfidence(item.Confidence); ValidateRefs(item.EvidenceIds, evidence, item.Status, AttributeKey(item.ConceptId, item.Name)); }
        foreach (var item in input.Relations) { ValidateFqn(item.Id, "relation.id"); if (!LowerCamel.IsMatch(item.Name) || !concepts.Contains(item.FromConceptId) || !concepts.Contains(item.ToConceptId)) throw new ArgumentException($"Relation '{item.Id}' has invalid name or endpoints."); ValidateCardinality(item.Min, item.Max); ValidateStatus(item.Status); ValidateConfidence(item.Confidence); ValidateRefs(item.EvidenceIds, evidence, item.Status, item.Id); }
        foreach (var item in input.Rules) { ValidateFqn(item.Id, "rule.id"); if (!concepts.Contains(item.SubjectId)) throw new ArgumentException($"Rule '{item.Id}' has unknown subject."); ValidateJson(item.PredicateJson, "predicate"); ValidateJson(item.EffectJson, "effect", allowEmpty: true); ValidateStatus(item.Status); ValidateConfidence(item.Confidence); ValidateRefs(item.EvidenceIds, evidence, item.Status, item.Id); }
        var lifecycles = input.Lifecycles.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in input.Lifecycles) { ValidateFqn(item.Id, "lifecycle.id"); if (!concepts.Contains(item.SubjectId) || !LowerCamel.IsMatch(item.StateProperty)) throw new ArgumentException($"Lifecycle '{item.Id}' has invalid subject or state property."); ValidateStatus(item.Status); ValidateConfidence(item.Confidence); ValidateRefs(item.EvidenceIds, evidence, item.Status, item.Id); }
        var stateKeys = input.States.Select(item => StateKey(item.LifecycleId, item.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var item in input.States) { if (!lifecycles.Contains(item.LifecycleId) || !StateToken.IsMatch(item.Id)) throw new ArgumentException("State has invalid lifecycle or ID."); ValidateRefs(item.EvidenceIds ?? [], evidence, "accepted", StateKey(item.LifecycleId, item.Id), allowEmpty: true); }
        foreach (var item in input.Lifecycles) if (!stateKeys.Contains(StateKey(item.Id, item.InitialState))) throw new ArgumentException($"Lifecycle '{item.Id}' initial state is missing.");
        foreach (var item in input.Transitions) { ValidateFqn(item.Id, "transition.id"); if (!lifecycles.Contains(item.LifecycleId) || !LowerCamel.IsMatch(item.Action) || !stateKeys.Contains(StateKey(item.LifecycleId, item.FromState)) || !stateKeys.Contains(StateKey(item.LifecycleId, item.ToState))) throw new ArgumentException($"Transition '{item.Id}' has invalid lifecycle or states."); ValidateJson(item.GuardJson, "guard", allowEmpty: true); ValidateJson(item.EffectJson, "effect", allowEmpty: true); ValidateStatus(item.Status); ValidateConfidence(item.Confidence); ValidateRefs(item.EvidenceIds, evidence, item.Status, item.Id); }
        foreach (var item in input.Mappings) { ValidateFqn(item.Id, "mapping.id"); if (!IsSemanticSubject(item.SubjectKind, item.SubjectId, concepts, lifecycles, input)) throw new ArgumentException($"Mapping '{item.Id}' has unknown semantic subject."); ValidatePath(item.Path); ValidateStatus(item.Status); ValidateConfidence(item.Confidence); ValidateRefs(item.EvidenceIds, evidence, item.Status, item.Id); }
        foreach (var item in input.Candidates) { Require(item.Id, "candidate.id"); Require(item.SubjectKind, "candidate.subjectKind"); Require(item.ProposedId, "candidate.proposedId"); ValidateJson(item.PayloadJson, "candidate payload"); if (!CandidateStatuses.Contains(item.Status)) throw new ArgumentException($"Candidate '{item.Id}' has invalid status '{item.Status}'."); ValidateConfidence(item.Confidence); ValidateRefs(item.EvidenceIds, evidence, "accepted", item.Id); }
        var candidates = input.Candidates.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in input.Reviews) { Require(item.Id, "review.id"); if (!candidates.Contains(item.CandidateId) || item.Decision is not ("accepted" or "rejected" or "superseded")) throw new ArgumentException($"Review '{item.Id}' has invalid candidate or decision."); Require(item.Reviewer, "review.reviewer"); Require(item.ReviewedAt, "review.reviewedAt"); }
        foreach (var item in input.Diagnostics) { Require(item.Id, "diagnostic.id"); Require(item.Kind, "diagnostic.kind"); Require(item.SubjectKind, "diagnostic.subjectKind"); Require(item.ConflictKey, "diagnostic.conflictKey"); Require(item.Message, "diagnostic.message"); ValidateJson(item.DetailsJson, "diagnostic details"); if (item.Severity is not ("info" or "warning" or "error")) throw new ArgumentException($"Diagnostic '{item.Id}' has invalid severity '{item.Severity}'."); }
    }

    private static bool IsSemanticSubject(string kind, string id, HashSet<string> concepts, HashSet<string> lifecycles, BusinessOntologyGenerationInput input) => kind switch { "concept" => concepts.Contains(id), "relation" => input.Relations.Any(item => item.Id == id), "rule" => input.Rules.Any(item => item.Id == id), "lifecycle" => lifecycles.Contains(id), "transition" => input.Transitions.Any(item => item.Id == id), _ => false };
    private static void ValidateRefs(IReadOnlyList<string> refs, IReadOnlyDictionary<string, BusinessOntologyEvidence> evidence, string status, string subject, bool allowEmpty = false) { if (!allowEmpty && refs.Count == 0) throw new ArgumentException($"Semantic object '{subject}' requires direct evidence."); foreach (var id in refs) if (!evidence.ContainsKey(id)) throw new ArgumentException($"Semantic object '{subject}' references unknown evidence '{id}'."); if (status == "hypothesis" && !refs.Any(id => evidence[id].Grade == "inferred")) throw new ArgumentException($"Hypothesis '{subject}' requires inferred evidence."); }
    private static void ValidateFqn(string value, string name) { if (!Fqn.IsMatch(value)) throw new ArgumentException($"{name} must be a PascalCase FQN."); }
    private static void ValidateStatus(string value) { if (!SemanticStatuses.Contains(value)) throw new ArgumentException($"Invalid semantic status '{value}'."); }
    private static void ValidateGrade(string value) { if (!Grades.Contains(value)) throw new ArgumentException($"Invalid evidence grade '{value}'."); }
    private static void ValidateConfidence(double value) { if (double.IsNaN(value) || value < 0 || value > 1) throw new ArgumentException("Confidence must be in [0,1]."); }
    private static void ValidatePath(string value) { Require(value, "path"); if (Path.IsPathRooted(value) || value.Contains("..", StringComparison.Ordinal) || value.Contains('\\')) throw new ArgumentException("Evidence and mapping paths must be repository-relative POSIX paths."); }
    private static void ValidateCardinality(string min, string max) { if (!int.TryParse(min, out var minValue) || minValue < 0 || (max != "*" && (!int.TryParse(max, out var maxValue) || maxValue < minValue))) throw new ArgumentException("Relation cardinality is invalid."); }
    private static void ValidateJson(string value, string name, bool allowEmpty = false) { if (allowEmpty && string.IsNullOrWhiteSpace(value)) return; try { using var _ = JsonDocument.Parse(value); } catch (JsonException) { throw new ArgumentException($"{name} must be JSON."); } }
    private static void Unique(IEnumerable<string> ids, string kind) { if (ids.GroupBy(id => id, StringComparer.Ordinal).Any(group => group.Count() > 1)) throw new ArgumentException($"Duplicate {kind} identity."); }
    private static void Require(string value, string name) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required."); }
    private static string AttributeKey(string conceptId, string name) => conceptId + "#" + name;
    private static string StateKey(string lifecycleId, string stateId) => lifecycleId + "#" + stateId;
    private static bool IsCreateConflict(Exception ex) => ex.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("exists", StringComparison.OrdinalIgnoreCase);
    private static string S(IReadOnlyList<JsonElement> row, int index) => row[index].ValueKind == JsonValueKind.String ? row[index].GetString() ?? "" : row[index].ToString();
    private static int I(IReadOnlyList<JsonElement> row, int index) => row[index].GetInt32();
    private static double D(IReadOnlyList<JsonElement> row, int index) => row[index].GetDouble();
    private static bool B(IReadOnlyList<JsonElement> row, int index) => row[index].GetBoolean();

    private sealed class StringComparerTuple : IEqualityComparer<(string SubjectKind, string SubjectId)>
    {
        public static readonly StringComparerTuple Ordinal = new();
        public bool Equals((string SubjectKind, string SubjectId) x, (string SubjectKind, string SubjectId) y) => StringComparer.Ordinal.Equals(x.SubjectKind, y.SubjectKind) && StringComparer.Ordinal.Equals(x.SubjectId, y.SubjectId);
        public int GetHashCode((string SubjectKind, string SubjectId) value) => HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.SubjectKind), StringComparer.Ordinal.GetHashCode(value.SubjectId));
    }
}
