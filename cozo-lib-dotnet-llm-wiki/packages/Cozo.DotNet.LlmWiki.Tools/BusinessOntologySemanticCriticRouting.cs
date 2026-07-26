using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cozo.DotNet.LlmWiki.Tools;

/// <summary>One opaque support handle is safe to show to the critic but resolves only in memory.</summary>
internal sealed record BusinessOntologySemanticCriticSupport(string Reference, string EvidenceId);

internal sealed record BusinessOntologySemanticCriticCandidate(
    string Id,
    string Kind,
    string DomainId,
    IReadOnlyList<BusinessOntologySemanticCriticSupport> Supports,
    IReadOnlyList<string> RequiredKeptConceptIds);

internal sealed record BusinessOntologySemanticCriticSnapshot(
    string Digest,
    string CompleteDraftDigest,
    IReadOnlyList<BusinessOntologySemanticCriticCandidate> Candidates);

internal sealed record BusinessOntologySemanticCriticRouting(
    BusinessOntologySemanticCriticRunProvenance Provenance,
    string CompleteDraftDigest,
    IReadOnlyList<BusinessOntologySemanticCriticVerdict> Verdicts,
    IReadOnlyList<string> PendingCandidateIds,
    IReadOnlyList<string> ReviewCandidateIds,
    IReadOnlyList<string> DiagnosisCandidateIds);

/// <summary>
/// Parses the critic's closed, redacted verdict document. Evidence handles are opaque in the
/// prompt and resolved only against the complete in-memory pending drafts after parsing.
/// </summary>
internal static class BusinessOntologySemanticCriticVerdictRouter
{
    internal const string SchemaVersion = "business-ontology-semantic-critic-verdict-v1";
    private const int MaxRationaleLength = 1_200;
    private const int MaxRequestLength = 1_200;
    private const int MaxSupportsPerVerdict = 8;

    public static BusinessOntologySemanticCriticSnapshot CreateSnapshot(
        IReadOnlyList<BusinessOntologySemanticPendingDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var candidates = new List<BusinessOntologySemanticCriticCandidate>();
        foreach (var draft in drafts.OrderBy(item => item.DomainId, StringComparer.Ordinal))
        {
            candidates.AddRange(draft.Clusters.Select(cluster => Candidate(
                cluster.ConceptId, "concept", draft.DomainId,
                cluster.ImplementationAnchors.Select(anchor => anchor.EvidenceId), [])));
            candidates.AddRange(draft.Attributes.Select(attribute => Candidate(
                attribute.Id, "attribute", draft.DomainId, attribute.EvidenceIds, [attribute.SubjectConceptId])));
            candidates.AddRange(draft.Relations.Select(relation => Candidate(
                relation.Id, "relation", draft.DomainId, relation.EvidenceBindings.Select(binding => binding.EvidenceId),
                [relation.FromConceptId, relation.ToConceptId])));
            candidates.AddRange(draft.Rules.Select(rule => Candidate(
                rule.Id, "rule", draft.DomainId, rule.EvidenceBindings.Select(binding => binding.EvidenceId), [rule.SubjectConceptId])));
            candidates.AddRange(draft.Lifecycles.Select(lifecycle => Candidate(
                lifecycle.Id, "lifecycle", draft.DomainId, lifecycle.EvidenceBindings.Select(binding => binding.EvidenceId), [lifecycle.SubjectConceptId])));
        }

        var ordered = candidates.OrderBy(candidate => candidate.Id, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.GroupBy(candidate => candidate.Id, StringComparer.Ordinal).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Critic routing requires a non-empty globally unique candidate set.", nameof(drafts));
        }

        var publicSnapshot = new
        {
            schemaVersion = SchemaVersion,
            candidates = ordered.Select(candidate => new
            {
                candidate.Id,
                candidate.Kind,
                candidate.DomainId,
                supportRefs = candidate.Supports.Select(support => support.Reference).ToArray(),
            }).ToArray(),
        };
        return new BusinessOntologySemanticCriticSnapshot(
            Digest("critic-snapshot", publicSnapshot),
            Digest("critic-complete-draft", drafts),
            ordered);
    }

    public static BusinessOntologySemanticCriticRouting ParseAndRoute(
        string responseJson,
        BusinessOntologySemanticCriticSnapshot snapshot,
        string criticRunId,
        string sourceFingerprint,
        string inputFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseJson);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(criticRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFingerprint);

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        RequireObject(root, "$", ["schemaVersion", "snapshotDigest", "verdicts"]);
        if (root.GetProperty("schemaVersion").GetString() != SchemaVersion
            || !string.Equals(root.GetProperty("snapshotDigest").GetString(), snapshot.Digest, StringComparison.Ordinal))
        {
            throw Invalid("$", "must use the expected closed critic schema and snapshot digest.");
        }

        var candidates = snapshot.Candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var verdictsElement = root.GetProperty("verdicts");
        if (verdictsElement.ValueKind != JsonValueKind.Array || verdictsElement.GetArrayLength() != candidates.Count)
        {
            throw Invalid("$.verdicts", "must decide exactly once for every staged candidate.");
        }

        var parsed = new List<BusinessOntologySemanticCriticVerdict>(candidates.Count);
        foreach (var element in verdictsElement.EnumerateArray())
        {
            RequireObject(element, "$.verdicts[]", ["candidateId", "decision", "rationaleZh", "supportRefs", "targetCandidateId", "requestZh"]);
            var candidateId = RequireString(element, "candidateId", "$.verdicts[]");
            if (!candidates.TryGetValue(candidateId, out var candidate))
            {
                throw Invalid("$.verdicts[]", "cannot reference an unstaged candidate.");
            }

            var decision = RequireString(element, "decision", "$.verdicts[]");
            var rationale = RequireChinese(element, "rationaleZh", MaxRationaleLength, "$.verdicts[]");
            var supportRefs = RequireStringArray(element, "supportRefs", MaxSupportsPerVerdict, "$.verdicts[]");
            if (supportRefs.Count == 0 || supportRefs.Any(reference => !candidate.Supports.Any(support => support.Reference == reference)))
            {
                throw Invalid("$.verdicts[].supportRefs", "must be non-empty and closed over this candidate's staged support references.");
            }
            if (!BusinessOntologySemanticCriticDecisions.All.Contains(decision))
            {
                throw Invalid("$.verdicts[].decision", "must be one of the closed critic decisions.");
            }

            var target = OptionalString(element, "targetCandidateId", "$.verdicts[]");
            var request = OptionalString(element, "requestZh", "$.verdicts[]");
            if (decision == BusinessOntologySemanticCriticDecisions.Merge)
            {
                if (target is null || target == candidateId || !candidates.TryGetValue(target, out var targetCandidate)
                    || targetCandidate.Kind != candidate.Kind)
                {
                    throw Invalid("$.verdicts[].targetCandidateId", "must identify a distinct staged candidate of the same kind.");
                }
            }
            else if (target is not null)
            {
                throw Invalid("$.verdicts[].targetCandidateId", "is only permitted for merge.");
            }

            if (decision == BusinessOntologySemanticCriticDecisions.RequestEvidence)
            {
                if (request is null || request.Length > MaxRequestLength || !ContainsCjk(request))
                {
                    throw Invalid("$.verdicts[].requestZh", "must be a bounded Chinese evidence request.");
                }
            }
            else if (request is not null)
            {
                throw Invalid("$.verdicts[].requestZh", "is only permitted for request_evidence.");
            }

            parsed.Add(new BusinessOntologySemanticCriticVerdict(
                candidateId,
                decision,
                rationale,
                supportRefs.Select(reference => candidate.Supports.Single(support => support.Reference == reference).EvidenceId).ToArray(),
                target,
                request));
        }

        if (parsed.GroupBy(verdict => verdict.CandidateId, StringComparer.Ordinal).Any(group => group.Count() != 1)
            || parsed.Count != candidates.Count)
        {
            throw Invalid("$.verdicts", "must contain one verdict for each staged candidate.");
        }

        var decisions = parsed.ToDictionary(verdict => verdict.CandidateId, verdict => verdict.Decision, StringComparer.Ordinal);
        foreach (var verdict in parsed.Where(verdict => verdict.Decision == BusinessOntologySemanticCriticDecisions.Merge))
        {
            if (decisions[verdict.TargetCandidateId!] != BusinessOntologySemanticCriticDecisions.Keep)
            {
                throw Invalid("$.verdicts[].targetCandidateId", "must point to a candidate with a keep verdict.");
            }
        }
        ValidateKeptDependencies(parsed, decisions, candidates);

        return new BusinessOntologySemanticCriticRouting(
            new BusinessOntologySemanticCriticRunProvenance(criticRunId, sourceFingerprint, inputFingerprint, snapshot.Digest),
            snapshot.CompleteDraftDigest,
            parsed.OrderBy(verdict => verdict.CandidateId, StringComparer.Ordinal).ToArray(),
            parsed.Where(verdict => verdict.Decision == BusinessOntologySemanticCriticDecisions.Keep)
                .Select(verdict => verdict.CandidateId).Order(StringComparer.Ordinal).ToArray(),
            parsed.Where(verdict => verdict.Decision is BusinessOntologySemanticCriticDecisions.Merge
                or BusinessOntologySemanticCriticDecisions.Defer or BusinessOntologySemanticCriticDecisions.RequestEvidence)
                .Select(verdict => verdict.CandidateId).Order(StringComparer.Ordinal).ToArray(),
            parsed.Where(verdict => verdict.Decision == BusinessOntologySemanticCriticDecisions.Drop)
                .Select(verdict => verdict.CandidateId).Order(StringComparer.Ordinal).ToArray());
    }

    private static BusinessOntologySemanticCriticCandidate Candidate(
        string id,
        string kind,
        string domainId,
        IEnumerable<string> evidenceIds,
        IReadOnlyList<string> requiredKeptConceptIds)
    {
        var supports = evidenceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select((evidenceId, index) => new BusinessOntologySemanticCriticSupport(
                $"support:{id}:{index + 1}", evidenceId)).ToArray();
        if (string.IsNullOrWhiteSpace(id) || supports.Length == 0)
        {
            throw new ArgumentException("Every critic candidate must have a closed in-memory evidence set.", nameof(id));
        }
        return new BusinessOntologySemanticCriticCandidate(id, kind, domainId, supports, requiredKeptConceptIds);
    }

    private static void ValidateKeptDependencies(
        IReadOnlyList<BusinessOntologySemanticCriticVerdict> verdicts,
        IReadOnlyDictionary<string, string> decisions,
        IReadOnlyDictionary<string, BusinessOntologySemanticCriticCandidate> candidates)
    {
        foreach (var verdict in verdicts.Where(verdict => verdict.Decision == BusinessOntologySemanticCriticDecisions.Keep))
        {
            var candidate = candidates[verdict.CandidateId];
            foreach (var conceptId in candidate.RequiredKeptConceptIds)
            {
                if (!decisions.TryGetValue(conceptId, out var decision)
                    || decision != BusinessOntologySemanticCriticDecisions.Keep)
                {
                    throw Invalid("$.verdicts", "kept semantic candidates may depend only on kept concepts.");
                }
            }
        }
    }

    private static void RequireObject(JsonElement element, string path, IReadOnlyList<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Select(property => property.Name)
            .Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal) == false)
        {
            throw Invalid(path, "must be an object with the exact closed schema.");
        }
    }

    private static string RequireString(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid(path + "." + name, "must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static string RequireChinese(JsonElement element, string name, int maximumLength, string path)
    {
        var value = RequireString(element, name, path);
        if (value.Length > maximumLength || !ContainsCjk(value))
        {
            throw Invalid(path + "." + name, "must be bounded Chinese rationale text.");
        }
        return value;
    }

    private static string? OptionalString(JsonElement element, string name, string path)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return RequireString(element, name, path);
    }

    private static IReadOnlyList<string> RequireStringArray(JsonElement element, string name, int maximumLength, string path)
    {
        var array = element.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximumLength)
        {
            throw Invalid(path + "." + name, "must be a bounded string array.");
        }
        var values = array.EnumerateArray().Select(value =>
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw Invalid(path + "." + name, "must contain only non-empty strings.");
            }
            return value.GetString()!;
        }).ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw Invalid(path + "." + name, "must not contain duplicates.");
        }
        return values;
    }

    private static bool ContainsCjk(string value) => value.Any(character => character is >= '\u4e00' and <= '\u9fff');

    private static ArgumentException Invalid(string path, string message) => new($"{path} {message}");

    private static string Digest(string scope, object value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { scope, value })))).ToLowerInvariant();
}
