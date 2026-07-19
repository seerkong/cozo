using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cozo.DotNet.LlmWiki.LlmClient;

namespace Cozo.DotNet.LlmWiki.Tools;

/// <summary>Named, bounded strategies for comparable assisted semantic experiments.</summary>
public static class OntologySemanticExperimentProfiles
{
    public const string V2 = "v2";
    public const string V3 = "v3";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        V2,
        V3,
    };

    public static OntologySemanticExperimentProfile Resolve(string experiment) => experiment switch
    {
        V2 => new OntologySemanticExperimentProfile(V2, MaxSlices: 8, MaxCompletions: 8, UsesCritic: false),
        V3 => new OntologySemanticExperimentProfile(V3, MaxSlices: 8, MaxCompletions: 16, UsesCritic: true),
        _ => throw new ArgumentException("Experiment must be 'v2' or 'v3'.", nameof(experiment)),
    };
}

public sealed record OntologySemanticExperimentProfile(
    string Id,
    int MaxSlices,
    int MaxCompletions,
    bool UsesCritic);

public sealed record OntologySemanticExperimentBudget(
    int MaxSlices,
    int MaxCompletions)
{
    public static OntologySemanticExperimentBudget Create(
        OntologySemanticExperimentProfile profile,
        int? requestedMaxSlices)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var maxSlices = requestedMaxSlices ?? profile.MaxSlices;
        if (maxSlices <= 0 || maxSlices > profile.MaxSlices)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedMaxSlices),
                $"Experiment '{profile.Id}' accepts --max-slices from 1 to {profile.MaxSlices}.");
        }

        var multiplier = profile.UsesCritic ? 2 : 1;
        return new OntologySemanticExperimentBudget(maxSlices, maxSlices * multiplier);
    }
}

public sealed record OntologySemanticExperimentRunSummary(
    string Profile,
    int SelectedSlices,
    int CompletionCalls,
    int CacheHits,
    int ProposedCandidates,
    int RetainedCandidates,
    int DroppedCandidates,
    IReadOnlyList<string> SliceIds,
    IReadOnlyList<string> PackDigests);

public sealed record OntologySemanticCriticDecision(
    string CandidateId,
    string Decision,
    string Rationale);

/// <summary>
/// A second, closed model envelope for v3. It may only prune already locally validated proposals.
/// </summary>
public sealed class OntologySemanticCriticClient(ILlmClient llmClient)
{
    public const string SchemaVersion = "onto-semantic-critic-v1";
    public const int MaxOutputTokens = 2048;
    public const int MaxResponseUtf8Bytes = 48 * 1024;
    public const int MaxAttempts = 2;
    public const string SystemPrompt =
        """
        Return exactly one JSON object and nothing else. Do not use Markdown or code fences.
        The object must contain exactly:
        {"schemaVersion":"onto-semantic-critic-v1","decisions":[...]}.
        For every input candidateId return exactly one decision object with exactly candidateId, decision, rationale.
        decision must be "keep" or "drop". You may only reference candidate IDs supplied by the user payload.
        Do not create candidates, concepts, evidence, acceptance decisions, confidence, or fields outside this schema.
        Rationale must be a short direct-evidence summary, never hidden chain-of-thought.
        """;

    public async Task<IReadOnlyList<OntologySemanticCriticDecision>> ReviewAsync(
        SemanticEvidencePack evidencePack,
        IReadOnlyList<ValidatedOntologySemanticCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidencePack);
        ArgumentNullException.ThrowIfNull(candidates);
        if (!llmClient.IsAvailable)
        {
            throw new InvalidOperationException("Semantic critic requires an available ILlmClient: "
                + (llmClient.UnavailableReason ?? "client unavailable"));
        }
        if (candidates.Count == 0)
        {
            return [];
        }

        var ids = candidates.Select(candidate => candidate.Id).Order(StringComparer.Ordinal).ToArray();
        var userPrompt = OntologySemanticJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            evidencePack = JsonSerializer.Deserialize<JsonElement>(evidencePack.ToCanonicalJson()),
            candidates = candidates.OrderBy(candidate => candidate.Id, StringComparer.Ordinal).Select(candidate => new
            {
                candidateId = candidate.Id,
                payload = JsonSerializer.Deserialize<JsonElement>(candidate.CanonicalPayloadJson),
            }),
        }));
        if (Encoding.UTF8.GetByteCount(userPrompt) > OntologySemanticAssistedProposalClient.MaxUserPromptUtf8Bytes)
        {
            throw new ArgumentException("Semantic critic user prompt exceeds the assisted prompt byte limit.", nameof(candidates));
        }

        string response = "";
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var completion = await llmClient.CompleteAsync(
                    SystemPrompt,
                    userPrompt,
                    new LlmOptions(MaxTokens: MaxOutputTokens, Temperature: 0),
                    cancellationToken);
                response = completion.Text ?? "";
                return ParseResponse(response, ids);
            }
            catch (ArgumentException) when (attempt < MaxAttempts)
            {
                continue;
            }
        }

        throw new InvalidOperationException(
            "Semantic critic returned no valid closed decision envelope; responseSha256="
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(response))).ToLowerInvariant());
    }

    public static IReadOnlyList<OntologySemanticCriticDecision> ParseResponse(
        string responseJson,
        IReadOnlyCollection<string> candidateIds)
    {
        if (string.IsNullOrWhiteSpace(responseJson)
            || Encoding.UTF8.GetByteCount(responseJson) > MaxResponseUtf8Bytes)
        {
            throw new ArgumentException("Semantic critic response is empty or exceeds its byte limit.", nameof(responseJson));
        }
        using var document = JsonDocument.Parse(responseJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Semantic critic response must be an object.", nameof(responseJson));
        }
        OntologySemanticJson.RejectDuplicateProperties(root, "$" );
        var fields = root.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (!fields.SetEquals(["schemaVersion", "decisions"])
            || root.GetProperty("schemaVersion").GetString() != SchemaVersion
            || root.GetProperty("decisions").ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Semantic critic response has an invalid closed envelope.", nameof(responseJson));
        }

        var expected = candidateIds.ToHashSet(StringComparer.Ordinal);
        var decisions = new List<OntologySemanticCriticDecision>();
        foreach (var item in root.GetProperty("decisions").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Every semantic critic decision must be an object.", nameof(responseJson));
            }
            OntologySemanticJson.RejectDuplicateProperties(item, "$.decisions[]");
            var properties = item.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            if (!properties.SetEquals(["candidateId", "decision", "rationale"])
                || item.GetProperty("candidateId").ValueKind != JsonValueKind.String
                || item.GetProperty("decision").ValueKind != JsonValueKind.String
                || item.GetProperty("rationale").ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("Semantic critic decision has an invalid closed shape.", nameof(responseJson));
            }
            var candidateId = item.GetProperty("candidateId").GetString()!;
            var decision = item.GetProperty("decision").GetString()!;
            var rationale = item.GetProperty("rationale").GetString()!;
            if (!expected.Contains(candidateId) || decision is not ("keep" or "drop") || rationale.Length > 2_000)
            {
                throw new ArgumentException("Semantic critic decision references an unknown candidate or invalid value.", nameof(responseJson));
            }
            decisions.Add(new OntologySemanticCriticDecision(candidateId, decision, rationale));
        }
        if (decisions.Count != expected.Count
            || decisions.Select(item => item.CandidateId).Distinct(StringComparer.Ordinal).Count() != expected.Count)
        {
            throw new ArgumentException("Semantic critic must decide exactly once for every input candidate.", nameof(responseJson));
        }
        return decisions.OrderBy(item => item.CandidateId, StringComparer.Ordinal).ToArray();
    }
}
