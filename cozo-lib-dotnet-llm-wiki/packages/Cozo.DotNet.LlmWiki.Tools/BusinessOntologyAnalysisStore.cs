using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.Contracts;

namespace Cozo.DotNet.LlmWiki.Tools;

public sealed record BusinessOntologyAnalysisRunInput(
    string RunId,
    string OntologyId,
    string GenerationId,
    string AgentId,
    string Model,
    string Purpose,
    string Status,
    string StartedAt,
    string CompletedAt,
    string InputDigest);

public sealed record BusinessOntologyAnalysisRecordInput(
    string RunId,
    string RecordId,
    string Kind,
    string SubjectKind,
    string SubjectId,
    string Title,
    string BodyJson,
    string Status,
    double Uncertainty,
    string QueryDigest,
    string CreatedAt,
    IReadOnlyList<string> EvidenceIds);

public sealed record BusinessOntologyAnalysisRun(
    string RunId,
    string OntologyId,
    string GenerationId,
    string AgentId,
    string Model,
    string Purpose,
    string Status,
    string StartedAt,
    string CompletedAt,
    string InputDigest);

public sealed record BusinessOntologyAnalysisRecord(
    string RunId,
    string RecordId,
    string Kind,
    string SubjectKind,
    string SubjectId,
    string Title,
    string BodyJson,
    string Status,
    double Uncertainty,
    string QueryDigest,
    string CreatedAt,
    IReadOnlyList<string> EvidenceIds);

public sealed record BusinessOntologyAnalysisAppendResult<T>(T Entry, bool Appended);

public sealed record BusinessOntologyAnalysisWorkspaceSnapshot(
    string OntologyId,
    string GenerationId,
    IReadOnlyList<BusinessOntologyAnalysisRun> Runs,
    IReadOnlyList<BusinessOntologyAnalysisRecord> Records);

/// <summary>
/// Append-only workspace for agentic ontology analysis. It can reference accepted ontology and
/// CodeKnowledge evidence, but it never writes accepted ontology, review, or materialization rows.
/// </summary>
public sealed class BusinessOntologyAnalysisStore(CozoOm om)
{
    private static readonly Regex Fqn = new("^[A-Z][A-Za-z0-9]*(?:\\.[A-Z][A-Za-z0-9]*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex Identity = new("^[A-Za-z0-9][A-Za-z0-9:_./-]{0,191}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> RunStatuses = new(["running", "completed", "failed", "abandoned"], StringComparer.Ordinal);
    private static readonly HashSet<string> RecordKinds = new(["observation", "hypothesis", "conflict", "gap", "candidate_draft"], StringComparer.Ordinal);
    private static readonly HashSet<string> RecordStatuses = new(["observed", "open", "proposed", "confirmed", "rejected", "resolved", "superseded"], StringComparer.Ordinal);
    private static readonly HashSet<string> SubjectKinds = new(["ontology", "concept", "attribute", "relation", "rule", "lifecycle", "state", "transition", "use_case", "evidence", "unknown"], StringComparer.Ordinal);

    public static IReadOnlyList<string> RequiredRelationNames { get; } =
    [
        "onto_analysis_run",
        "onto_analysis_record",
        "onto_analysis_evidence_ref",
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
                // Idempotent schema bootstrap. Analysis relations are isolated from accepted onto_*.
            }
        }
    }

    public async Task<BusinessOntologyAnalysisAppendResult<BusinessOntologyAnalysisRun>> AppendRunAsync(
        BusinessOntologyAnalysisRunInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateRun(input);
        await InitializeAsync(cancellationToken);
        await ValidateActiveGenerationIfPresentAsync(input.OntologyId, input.GenerationId, cancellationToken);

        var existing = await ReadRunByIdAsync(input.RunId, cancellationToken);
        var normalized = ToRun(input);
        if (existing is not null)
        {
            if (SameRun(existing, normalized))
            {
                return new BusinessOntologyAnalysisAppendResult<BusinessOntologyAnalysisRun>(existing, false);
            }
            throw new InvalidOperationException($"Analysis run '{input.RunId}' already exists with different immutable content.");
        }

        await om.Runtime.Store.RunAsync(
            """
            ?[run_id, ontology_id, generation_id, agent_id, model, purpose, status, started_at, completed_at, input_digest] <-
                [[$run_id, $ontology_id, $generation_id, $agent_id, $model, $purpose, $status, $started_at, $completed_at, $input_digest]]
            :insert onto_analysis_run {
                run_id =>
                ontology_id,
                generation_id,
                agent_id,
                model,
                purpose,
                status,
                started_at,
                completed_at,
                input_digest
            }
            """,
            Params(
                ("run_id", normalized.RunId),
                ("ontology_id", normalized.OntologyId),
                ("generation_id", normalized.GenerationId),
                ("agent_id", normalized.AgentId),
                ("model", normalized.Model),
                ("purpose", normalized.Purpose),
                ("status", normalized.Status),
                ("started_at", normalized.StartedAt),
                ("completed_at", normalized.CompletedAt),
                ("input_digest", normalized.InputDigest)),
            cancellationToken: cancellationToken);
        return new BusinessOntologyAnalysisAppendResult<BusinessOntologyAnalysisRun>(normalized, true);
    }

    public async Task<IReadOnlyList<BusinessOntologyAnalysisAppendResult<BusinessOntologyAnalysisRecord>>> AppendRecordsAsync(
        IReadOnlyList<BusinessOntologyAnalysisRecordInput> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) return [];
        Unique(inputs.Select(input => input.RecordId), "analysis record");
        foreach (var input in inputs) ValidateRecord(input);

        await InitializeAsync(cancellationToken);
        var run = await ReadRunByIdAsync(inputs[0].RunId, cancellationToken)
            ?? throw new ArgumentException($"Unknown analysis run '{inputs[0].RunId}'.", nameof(inputs));
        if (inputs.Any(input => !StringComparer.Ordinal.Equals(input.RunId, run.RunId)))
        {
            throw new ArgumentException("All analysis records in one append batch must belong to the same run.", nameof(inputs));
        }

        var normalized = inputs.Select(ToRecord).ToArray();
        await ValidateEvidenceClosureAsync(run, normalized, cancellationToken);
        var existingById = new Dictionary<string, BusinessOntologyAnalysisRecord>(StringComparer.Ordinal);

        foreach (var record in normalized)
        {
            var existing = await ReadRecordByIdAsync(record.RunId, record.RecordId, cancellationToken);
            if (existing is not null)
            {
                if (SameRecord(existing, record))
                {
                    existingById[record.RecordId] = existing;
                    continue;
                }
                throw new InvalidOperationException($"Analysis record '{record.RunId}/{record.RecordId}' already exists with different immutable content.");
            }
        }

        var toAppend = normalized.Where(record => !existingById.ContainsKey(record.RecordId)).ToArray();
        if (toAppend.Length != 0)
        {
            await using var transaction = await om.Runtime.Store.BeginTransactionAsync(write: true, cancellationToken);
            try
            {
                foreach (var record in toAppend)
                {
                    await InsertRecordAsync(transaction, record, cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.AbortAsync(cancellationToken);
                throw;
            }
        }

        return normalized
            .Select(record => existingById.TryGetValue(record.RecordId, out var existing)
                ? new BusinessOntologyAnalysisAppendResult<BusinessOntologyAnalysisRecord>(existing, false)
                : new BusinessOntologyAnalysisAppendResult<BusinessOntologyAnalysisRecord>(record, true))
            .ToArray();
    }

    public async Task<BusinessOntologyAnalysisWorkspaceSnapshot> ReadWorkspaceAsync(
        string ontologyId,
        string generationId,
        CancellationToken cancellationToken = default)
    {
        ValidateFqn(ontologyId, nameof(ontologyId));
        Require(generationId, nameof(generationId));
        await InitializeAsync(cancellationToken);

        var runs = await om.Runtime.Store.RunAsync(
            """
            ?[run_id, ontology_id, generation_id, agent_id, model, purpose, status, started_at, completed_at, input_digest] :=
                *onto_analysis_run{
                    run_id,
                    ontology_id,
                    generation_id,
                    agent_id,
                    model,
                    purpose,
                    status,
                    started_at,
                    completed_at,
                    input_digest
                },
                ontology_id == $ontology_id,
                generation_id == $generation_id
            """,
            Params(("ontology_id", ontologyId), ("generation_id", generationId)),
            cancellationToken: cancellationToken);
        var runRows = runs.Rows
            .Select(row => new BusinessOntologyAnalysisRun(S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), S(row, 5), S(row, 6), S(row, 7), S(row, 8), S(row, 9)))
            .OrderBy(item => item.StartedAt, StringComparer.Ordinal)
            .ThenBy(item => item.RunId, StringComparer.Ordinal)
            .ToArray();
        if (runRows.Length == 0)
        {
            return new BusinessOntologyAnalysisWorkspaceSnapshot(ontologyId, generationId, [], []);
        }

        var records = new List<BusinessOntologyAnalysisRecord>();
        foreach (var run in runRows)
        {
            records.AddRange(await ReadRecordsAsync(run.RunId, cancellationToken: cancellationToken));
        }
        return new BusinessOntologyAnalysisWorkspaceSnapshot(
            ontologyId,
            generationId,
            runRows,
            records
                .OrderBy(item => item.CreatedAt, StringComparer.Ordinal)
                .ThenBy(item => item.RecordId, StringComparer.Ordinal)
                .ToArray());
    }

    public async Task<IReadOnlyList<BusinessOntologyAnalysisRecord>> ReadRecordsAsync(
        string runId,
        string? kind = null,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(runId, nameof(runId));
        if (kind is not null && !RecordKinds.Contains(kind))
        {
            throw new ArgumentException("kind must be observation, hypothesis, conflict, gap, or candidate_draft.", nameof(kind));
        }
        await InitializeAsync(cancellationToken);

        var records = await om.Runtime.Store.RunAsync(
            """
            ?[run_id, record_id, kind, subject_kind, subject_id, title, body_json, status, uncertainty, query_digest, created_at] :=
                *onto_analysis_record{
                    run_id,
                    record_id,
                    kind,
                    subject_kind,
                    subject_id,
                    title,
                    body_json,
                    status,
                    uncertainty,
                    query_digest,
                    created_at
                },
                run_id == $run_id
            """,
            Params(("run_id", runId)),
            cancellationToken: cancellationToken);
        var refs = await ReadEvidenceRefsForRunAsync(runId, cancellationToken);
        return records.Rows
            .Select(row => new BusinessOntologyAnalysisRecord(
                S(row, 0),
                S(row, 1),
                S(row, 2),
                S(row, 3),
                S(row, 4),
                S(row, 5),
                S(row, 6),
                S(row, 7),
                D(row, 8),
                S(row, 9),
                S(row, 10),
                refs.GetValueOrDefault(S(row, 1), [])))
            .Where(item => kind is null || StringComparer.Ordinal.Equals(item.Kind, kind))
            .OrderBy(item => item.CreatedAt, StringComparer.Ordinal)
            .ThenBy(item => item.RecordId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task InsertRecordAsync(
        ICozoOmStore store,
        BusinessOntologyAnalysisRecord record,
        CancellationToken cancellationToken)
    {
        await store.RunAsync(
            """
            ?[run_id, record_id, kind, subject_kind, subject_id, title, body_json, status, uncertainty, query_digest, created_at] <-
                [[$run_id, $record_id, $kind, $subject_kind, $subject_id, $title, $body_json, $status, $uncertainty, $query_digest, $created_at]]
            :insert onto_analysis_record {
                run_id,
                record_id =>
                kind,
                subject_kind,
                subject_id,
                title,
                body_json,
                status,
                uncertainty,
                query_digest,
                created_at
            }
            """,
            Params(
                ("run_id", record.RunId),
                ("record_id", record.RecordId),
                ("kind", record.Kind),
                ("subject_kind", record.SubjectKind),
                ("subject_id", record.SubjectId),
                ("title", record.Title),
                ("body_json", record.BodyJson),
                ("status", record.Status),
                ("uncertainty", record.Uncertainty),
                ("query_digest", record.QueryDigest),
                ("created_at", record.CreatedAt)),
            cancellationToken: cancellationToken);
        foreach (var evidenceId in record.EvidenceIds)
        {
            await store.RunAsync(
                """
                ?[run_id, record_id, evidence_id, linked] <- [[$run_id, $record_id, $evidence_id, true]]
                :insert onto_analysis_evidence_ref {run_id, record_id, evidence_id => linked}
                """,
                Params(
                    ("run_id", record.RunId),
                    ("record_id", record.RecordId),
                    ("evidence_id", evidenceId)),
                cancellationToken: cancellationToken);
        }
    }

    private async Task<BusinessOntologyAnalysisRun?> ReadRunByIdAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var relations = await RelationNamesAsync(cancellationToken);
        if (!relations.Contains("onto_analysis_run")) return null;
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[run_id, ontology_id, generation_id, agent_id, model, purpose, status, started_at, completed_at, input_digest] :=
                *onto_analysis_run{run_id, ontology_id, generation_id, agent_id, model, purpose, status, started_at, completed_at, input_digest},
                run_id == $run_id
            """,
            Params(("run_id", runId)),
            cancellationToken: cancellationToken);
        return rows.Rows.Count == 0
            ? null
            : new BusinessOntologyAnalysisRun(S(rows.Rows[0], 0), S(rows.Rows[0], 1), S(rows.Rows[0], 2), S(rows.Rows[0], 3), S(rows.Rows[0], 4), S(rows.Rows[0], 5), S(rows.Rows[0], 6), S(rows.Rows[0], 7), S(rows.Rows[0], 8), S(rows.Rows[0], 9));
    }

    private async Task<BusinessOntologyAnalysisRecord?> ReadRecordByIdAsync(
        string runId,
        string recordId,
        CancellationToken cancellationToken)
    {
        var relations = await RelationNamesAsync(cancellationToken);
        if (!relations.Contains("onto_analysis_record")) return null;
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[run_id, record_id, kind, subject_kind, subject_id, title, body_json, status, uncertainty, query_digest, created_at] :=
                *onto_analysis_record{run_id, record_id, kind, subject_kind, subject_id, title, body_json, status, uncertainty, query_digest, created_at},
                run_id == $run_id,
                record_id == $record_id
            """,
            Params(("run_id", runId), ("record_id", recordId)),
            cancellationToken: cancellationToken);
        if (rows.Rows.Count == 0) return null;
        var refs = await ReadEvidenceRefsForRunAsync(runId, cancellationToken);
        var row = rows.Rows[0];
        return new BusinessOntologyAnalysisRecord(
            S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), S(row, 5), S(row, 6),
            S(row, 7), D(row, 8), S(row, 9), S(row, 10), refs.GetValueOrDefault(recordId, []));
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadEvidenceRefsForRunAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var relations = await RelationNamesAsync(cancellationToken);
        if (!relations.Contains("onto_analysis_evidence_ref")) return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[record_id, evidence_id] :=
                *onto_analysis_evidence_ref{run_id: $run_id, record_id, evidence_id}
            """,
            Params(("run_id", runId)),
            cancellationToken: cancellationToken);
        return rows.Rows
            .GroupBy(row => S(row, 0), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(row => S(row, 1)).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
    }

    private async Task ValidateEvidenceClosureAsync(
        BusinessOntologyAnalysisRun run,
        IReadOnlyList<BusinessOntologyAnalysisRecord> records,
        CancellationToken cancellationToken)
    {
        var ids = records.SelectMany(record => record.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray();
        var relations = await RelationNamesAsync(cancellationToken);
        var known = new HashSet<string>(StringComparer.Ordinal);
        if (relations.Contains("ck_semantic_claim"))
        {
            var rows = await om.Runtime.Store.RunAsync(
                """
                ?[claim_id] := *ck_semantic_claim{claim_id}
                """,
                cancellationToken: cancellationToken);
            foreach (var row in rows.Rows) known.Add(S(row, 0));
        }
        if (relations.Contains("onto_evidence"))
        {
            var rows = await om.Runtime.Store.RunAsync(
                """
                ?[evidence_id] :=
                    *onto_evidence{
                        ontology_id: $ontology_id,
                        generation_id: $generation_id,
                        evidence_id
                    }
                """,
                Params(("ontology_id", run.OntologyId), ("generation_id", run.GenerationId)),
                cancellationToken: cancellationToken);
            foreach (var row in rows.Rows) known.Add(S(row, 0));
        }

        var missing = ids.Where(id => !known.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
        {
            throw new ArgumentException(
                "Analysis records reference evidence ids outside indexed CodeKnowledge or the active ontology generation: "
                + string.Join(", ", missing));
        }
    }

    private async Task ValidateActiveGenerationIfPresentAsync(
        string ontologyId,
        string generationId,
        CancellationToken cancellationToken)
    {
        var relations = await RelationNamesAsync(cancellationToken);
        if (!relations.Contains("onto_generation")) return;
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[generation_id] :=
                *onto_generation{ontology_id: $ontology_id, generation_id}
            """,
            Params(("ontology_id", ontologyId)),
            cancellationToken: cancellationToken);
        if (rows.Rows.Count != 0 && !StringComparer.Ordinal.Equals(S(rows.Rows[0], 0), generationId))
        {
            throw new ArgumentException(
                $"Analysis run generation '{generationId}' does not match active ontology generation '{S(rows.Rows[0], 0)}'.");
        }
    }

    private async Task<HashSet<string>> RelationNamesAsync(CancellationToken cancellationToken) =>
        (await om.Runtime.Store.RunAsync("::relations", cancellationToken: cancellationToken)).Rows
            .Select(row => S(row, 0))
            .Where(name => !name.Contains(':', StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

    private static BusinessOntologyAnalysisRun ToRun(BusinessOntologyAnalysisRunInput input) =>
        new(
            input.RunId.Trim(),
            input.OntologyId.Trim(),
            input.GenerationId.Trim(),
            input.AgentId.Trim(),
            input.Model.Trim(),
            input.Purpose.Trim(),
            input.Status.Trim(),
            input.StartedAt.Trim(),
            input.CompletedAt.Trim(),
            input.InputDigest.Trim());

    private static BusinessOntologyAnalysisRecord ToRecord(BusinessOntologyAnalysisRecordInput input) =>
        new(
            input.RunId.Trim(),
            input.RecordId.Trim(),
            input.Kind.Trim(),
            input.SubjectKind.Trim(),
            input.SubjectId.Trim(),
            input.Title.Trim(),
            CanonicalizeJson(input.BodyJson, nameof(input.BodyJson)),
            input.Status.Trim(),
            input.Uncertainty,
            input.QueryDigest.Trim(),
            input.CreatedAt.Trim(),
            input.EvidenceIds.Select(id => id.Trim()).OrderBy(id => id, StringComparer.Ordinal).ToArray());

    private static void ValidateRun(BusinessOntologyAnalysisRunInput input)
    {
        ValidateIdentity(input.RunId, nameof(input.RunId));
        ValidateFqn(input.OntologyId, nameof(input.OntologyId));
        Require(input.GenerationId, nameof(input.GenerationId));
        ValidateIdentity(input.AgentId, nameof(input.AgentId));
        Require(input.Model, nameof(input.Model));
        Require(input.Purpose, nameof(input.Purpose));
        if (!RunStatuses.Contains(input.Status.Trim()))
        {
            throw new ArgumentException("run status must be running, completed, failed, or abandoned.", nameof(input.Status));
        }
        RequireTimestamp(input.StartedAt, nameof(input.StartedAt));
        if (!string.IsNullOrWhiteSpace(input.CompletedAt)) RequireTimestamp(input.CompletedAt, nameof(input.CompletedAt));
        Require(input.InputDigest, nameof(input.InputDigest));
    }

    private static void ValidateRecord(BusinessOntologyAnalysisRecordInput input)
    {
        ValidateIdentity(input.RunId, nameof(input.RunId));
        ValidateIdentity(input.RecordId, nameof(input.RecordId));
        if (!RecordKinds.Contains(input.Kind.Trim()))
        {
            throw new ArgumentException("record kind must be observation, hypothesis, conflict, gap, or candidate_draft.", nameof(input.Kind));
        }
        if (!SubjectKinds.Contains(input.SubjectKind.Trim()))
        {
            throw new ArgumentException("subject kind is not supported for ontology analysis records.", nameof(input.SubjectKind));
        }
        Require(input.SubjectId, nameof(input.SubjectId));
        Require(input.Title, nameof(input.Title));
        _ = CanonicalizeJson(input.BodyJson, nameof(input.BodyJson));
        if (!RecordStatuses.Contains(input.Status.Trim()))
        {
            throw new ArgumentException("record status is not supported for ontology analysis records.", nameof(input.Status));
        }
        if (input.Uncertainty is < 0 or > 1 || double.IsNaN(input.Uncertainty))
        {
            throw new ArgumentOutOfRangeException(nameof(input.Uncertainty), "uncertainty must be 0..1.");
        }
        Require(input.QueryDigest, nameof(input.QueryDigest));
        RequireTimestamp(input.CreatedAt, nameof(input.CreatedAt));
        if (input.EvidenceIds.Count == 0)
        {
            throw new ArgumentException("analysis records require at least one evidence id.", nameof(input.EvidenceIds));
        }
        if (input.EvidenceIds.Any(string.IsNullOrWhiteSpace)
            || input.EvidenceIds.Distinct(StringComparer.Ordinal).Count() != input.EvidenceIds.Count)
        {
            throw new ArgumentException("analysis evidence ids must be non-empty and unique.", nameof(input.EvidenceIds));
        }
    }

    private static string CanonicalizeJson(string json, string name)
    {
        Require(json, name);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("analysis body must be strict JSON.", name, ex);
        }
    }

    private static bool SameRun(BusinessOntologyAnalysisRun left, BusinessOntologyAnalysisRun right) =>
        left == right;

    private static bool SameRecord(BusinessOntologyAnalysisRecord left, BusinessOntologyAnalysisRecord right) =>
        StringComparer.Ordinal.Equals(left.RunId, right.RunId)
        && StringComparer.Ordinal.Equals(left.RecordId, right.RecordId)
        && StringComparer.Ordinal.Equals(left.Kind, right.Kind)
        && StringComparer.Ordinal.Equals(left.SubjectKind, right.SubjectKind)
        && StringComparer.Ordinal.Equals(left.SubjectId, right.SubjectId)
        && StringComparer.Ordinal.Equals(left.Title, right.Title)
        && StringComparer.Ordinal.Equals(left.BodyJson, right.BodyJson)
        && StringComparer.Ordinal.Equals(left.Status, right.Status)
        && left.Uncertainty.Equals(right.Uncertainty)
        && StringComparer.Ordinal.Equals(left.QueryDigest, right.QueryDigest)
        && StringComparer.Ordinal.Equals(left.CreatedAt, right.CreatedAt)
        && left.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal)
            .SequenceEqual(right.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal);

    private static void ValidateFqn(string value, string name)
    {
        Require(value, name);
        if (!Fqn.IsMatch(value.Trim())) throw new ArgumentException($"{name} must be a dotted FQN.", name);
    }

    private static void ValidateIdentity(string value, string name)
    {
        Require(value, name);
        if (!Identity.IsMatch(value.Trim())) throw new ArgumentException($"{name} contains unsupported characters.", name);
    }

    private static void RequireTimestamp(string value, string name)
    {
        Require(value, name);
        if (!DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
        {
            throw new ArgumentException($"{name} must be an ISO-8601 timestamp.", name);
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
    }

    private static void Unique(IEnumerable<string> values, string subject)
    {
        var duplicate = values.GroupBy(value => value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Duplicate {subject} identity '{duplicate.Key}'.");
    }

    private static Dictionary<string, object?> Params(params (string Name, object? Value)[] values) =>
        values.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);

    private static string S(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.String ? row[index].GetString() ?? "" : row[index].ToString();

    private static double D(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.Number
            ? row[index].GetDouble()
            : double.Parse(S(row, index), CultureInfo.InvariantCulture);

    private static bool IsCreateConflict(Exception ex) =>
        ex.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("exists", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] Creates =
    [
        ":create onto_analysis_run {run_id => ontology_id, generation_id, agent_id, model, purpose, status, started_at, completed_at, input_digest}",
        ":create onto_analysis_record {run_id, record_id => kind, subject_kind, subject_id, title, body_json, status, uncertainty, query_digest, created_at}",
        ":create onto_analysis_evidence_ref {run_id, record_id, evidence_id => linked default true}",
    ];
}
