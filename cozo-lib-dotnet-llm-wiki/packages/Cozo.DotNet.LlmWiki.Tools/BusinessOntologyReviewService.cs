using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cozo.DotNet.LlmWiki.Tools;

public sealed record BusinessOntologyReviewDecision(
    string CandidateId,
    string Decision,
    string Reviewer,
    string Rationale,
    IReadOnlyList<string> ExpectedEvidenceIds);

public sealed record BusinessOntologyReviewDecisionFile(
    string OntologyId,
    IReadOnlyList<BusinessOntologyReviewDecision> Decisions);

public sealed record BusinessOntologyReviewApplyResult(
    string OntologyId,
    IReadOnlyList<BusinessOntologyReviewAppendResult> Reviews);

/// <summary>
/// Applies explicit human review files to the append-only ontology decision log.
/// Review evidence snapshots are checked against the active generation before any write.
/// </summary>
public sealed class BusinessOntologyReviewService
{
    private static readonly HashSet<string> Decisions =
        new(["accepted", "rejected", "superseded"], StringComparer.Ordinal);

    private readonly BusinessOntologyStore store;
    private readonly TimeProvider timeProvider;

    public BusinessOntologyReviewService(
        BusinessOntologyStore store,
        TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BusinessOntologyReviewApplyResult> ApplyDecisionFileAsync(
        string filePath,
        string? expectedOntologyId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var json = await File.ReadAllTextAsync(filePath, cancellationToken);
        return await ApplyDecisionJsonAsync(json, expectedOntologyId, cancellationToken);
    }

    public async Task<BusinessOntologyReviewApplyResult> ApplyDecisionJsonAsync(
        string json,
        string? expectedOntologyId = null,
        CancellationToken cancellationToken = default)
    {
        var decisionFile = ParseDecisionFile(json);
        if (expectedOntologyId is not null
            && !StringComparer.Ordinal.Equals(expectedOntologyId, decisionFile.OntologyId))
        {
            throw new ArgumentException(
                $"Decision file ontology '{decisionFile.OntologyId}' does not match requested ontology '{expectedOntologyId}'.");
        }

        var snapshot = await store.ReadExportableAsync(decisionFile.OntologyId, cancellationToken);
        var history = await store.ReadReviewHistoryAsync(
            decisionFile.OntologyId,
            cancellationToken: cancellationToken);
        var candidates = snapshot.Candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var reviewedCandidateIds = history
            .Select(entry => entry.Review.CandidateId)
            .ToHashSet(StringComparer.Ordinal);
        var reviewedAt = timeProvider.GetUtcNow()
            .ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture);
        var entries = new List<BusinessOntologyReviewEntry>(decisionFile.Decisions.Count);

        foreach (var decision in decisionFile.Decisions)
        {
            if (!candidates.TryGetValue(decision.CandidateId, out var candidate))
            {
                throw new ArgumentException(
                    $"Decision references candidate '{decision.CandidateId}' outside the active generation.");
            }
            if (!StringComparer.Ordinal.Equals(candidate.Status, "pending")
                && !reviewedCandidateIds.Contains(candidate.Id))
            {
                throw new ArgumentException(
                    $"Candidate '{candidate.Id}' is neither pending nor previously reviewed.");
            }

            var currentEvidence = candidate.EvidenceIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            var expectedEvidence = decision.ExpectedEvidenceIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            if (!currentEvidence.SequenceEqual(expectedEvidence, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Candidate '{candidate.Id}' expected evidence does not match the active generation.");
            }

            var review = new BusinessOntologyReview(
                StableReviewId(decisionFile.OntologyId, decision),
                decision.CandidateId,
                decision.Decision,
                decision.Reviewer,
                decision.Rationale,
                reviewedAt);
            entries.Add(new BusinessOntologyReviewEntry(review, expectedEvidence));
        }

        var results = await store.AppendReviewsAsync(
            decisionFile.OntologyId,
            snapshot.GenerationId,
            entries,
            cancellationToken);
        return new BusinessOntologyReviewApplyResult(decisionFile.OntologyId, results);
    }

    public Task<IReadOnlyList<BusinessOntologyEffectiveDecision>> ReadEffectiveDecisionsAsync(
        string ontologyId,
        CancellationToken cancellationToken = default) =>
        store.ReadEffectiveDecisionsAsync(ontologyId, cancellationToken);

    public static BusinessOntologyReviewDecisionFile ParseDecisionFile(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Ontology review decision file must be strict JSON.", nameof(json), ex);
        }

        using (document)
        {
            var root = RequireObject(document.RootElement, "$");
            RequireExactProperties(
                root,
                "$",
                ["ontologyId", "decisions"]);
            var ontologyId = RequireString(root.GetProperty("ontologyId"), "$.ontologyId");
            var decisionsElement = root.GetProperty("decisions");
            if (decisionsElement.ValueKind != JsonValueKind.Array
                || decisionsElement.GetArrayLength() == 0)
            {
                throw Invalid("$.decisions", "must be a non-empty array.");
            }

            var parsed = new List<BusinessOntologyReviewDecision>();
            var index = 0;
            foreach (var item in decisionsElement.EnumerateArray())
            {
                var path = $"$.decisions[{index}]";
                var decisionObject = RequireObject(item, path);
                RequireExactProperties(
                    decisionObject,
                    path,
                    ["candidateId", "decision", "reviewer", "rationale", "expectedEvidenceIds"]);
                var candidateId = RequireString(
                    decisionObject.GetProperty("candidateId"),
                    path + ".candidateId");
                var decision = RequireString(
                    decisionObject.GetProperty("decision"),
                    path + ".decision");
                if (!Decisions.Contains(decision))
                {
                    throw Invalid(path + ".decision", "must be accepted, rejected, or superseded.");
                }
                var reviewer = RequireString(
                    decisionObject.GetProperty("reviewer"),
                    path + ".reviewer");
                var rationale = RequireString(
                    decisionObject.GetProperty("rationale"),
                    path + ".rationale");
                var expectedEvidenceIds = RequireStringArray(
                    decisionObject.GetProperty("expectedEvidenceIds"),
                    path + ".expectedEvidenceIds");
                if (expectedEvidenceIds.Count != expectedEvidenceIds.Distinct(StringComparer.Ordinal).Count())
                {
                    throw Invalid(path + ".expectedEvidenceIds", "must not contain duplicate identities.");
                }
                parsed.Add(new BusinessOntologyReviewDecision(
                    candidateId,
                    decision,
                    reviewer,
                    rationale,
                    expectedEvidenceIds));
                index++;
            }

            var duplicateCandidate = parsed
                .GroupBy(decision => decision.CandidateId, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateCandidate is not null)
            {
                throw Invalid(
                    "$.decisions",
                    $"contains duplicate candidate '{duplicateCandidate.Key}'.");
            }
            return new BusinessOntologyReviewDecisionFile(ontologyId, parsed);
        }
    }

    private static string StableReviewId(
        string ontologyId,
        BusinessOntologyReviewDecision decision)
    {
        var identity = JsonSerializer.Serialize(new
        {
            ontologyId,
            candidateId = decision.CandidateId,
            decision = decision.Decision,
            reviewer = decision.Reviewer,
            rationale = decision.Rationale,
            expectedEvidenceIds = decision.ExpectedEvidenceIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
        });
        var canonical = OntologySemanticJson.Canonicalize(identity);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return "review:semantic:" + digest;
    }

    private static JsonElement RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(path, "must be an object.");
        }
        OntologySemanticJson.RejectDuplicateProperties(element, path);
        return element;
    }

    private static void RequireExactProperties(
        JsonElement element,
        string path,
        IReadOnlyList<string> expected)
    {
        var properties = element.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = properties.Except(expected, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (unknown is not null)
        {
            throw Invalid(path + "." + unknown, "is not an allowed field.");
        }
        var missing = expected.Except(properties, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (missing is not null)
        {
            throw Invalid(path, $"is missing required field '{missing}'.");
        }
    }

    private static string RequireString(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw Invalid(path, "must be a non-empty string.");
        }
        return element.GetString()!;
    }

    private static IReadOnlyList<string> RequireStringArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
        {
            throw Invalid(path, "must be a non-empty array.");
        }
        var values = new List<string>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            values.Add(RequireString(item, $"{path}[{index++}]"));
        }
        return values;
    }

    private static ArgumentException Invalid(string path, string message) =>
        new($"Ontology review decision field '{path}' {message}");
}
