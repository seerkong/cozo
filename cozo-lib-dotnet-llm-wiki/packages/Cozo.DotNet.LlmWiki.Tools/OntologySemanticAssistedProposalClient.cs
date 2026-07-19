using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cozo.DotNet.LlmWiki.LlmClient;

namespace Cozo.DotNet.LlmWiki.Tools;

public static class BusinessOntologySemanticProjectionModes
{
    public const string Deterministic = "deterministic";
    public const string Assisted = "assisted";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Deterministic,
        Assisted,
    };
}

public sealed record OntologySemanticAssistedFailureDiagnostic(
    string Category,
    string ResponseSha256,
    string Message,
    int Attempts);

public sealed class OntologySemanticAssistedProposalException(
    OntologySemanticAssistedFailureDiagnostic diagnostic)
    : InvalidOperationException(
        $"Assisted ontology proposal failed ({diagnostic.Category}, attempts={diagnostic.Attempts}, "
        + $"responseSha256={diagnostic.ResponseSha256}): {diagnostic.Message}")
{
    public OntologySemanticAssistedFailureDiagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>
/// Calls an injected LLM for reviewable proposals and treats its response as untrusted JSON.
/// Candidate identity, status, confidence, and evidence records remain locally owned.
/// </summary>
public sealed class OntologySemanticAssistedProposalClient(
    ILlmClient llmClient,
    OntologySemanticCandidateValidator? validator = null)
{
    public const int MaxCandidates = 24;
    public const int MaxOutputTokens = 4096;
    public const int MaxUserPromptUtf8Bytes = 128 * 1024;
    public const int MaxResponseUtf8Bytes = 96 * 1024;
    public const int MaxDiagnosticMessageCharacters = 240;
    public const int MaxAttempts = 2;

    public const string SystemPrompt =
        """
        Return exactly one JSON object and nothing else. Do not use Markdown or code fences.
        The object must contain exactly:
        {"schemaVersion":"onto-semantic-v1","candidates":[...]}.
        Each candidate must contain exactly kind, semantic, evidenceIds, basis, and rationale.
        kind is an exact closed enum: "relation", "rule", or "lifecycle". Never emit "type",
        "property", "entity", "concept", or any other kind. This transport does not propose business
        types or properties; if the evidence does not support one of the three allowed kinds, return
        an empty candidates array.
        Prefer a relation only when the evidence directly shows one supplied concept referring to another:
        its semantic object must contain exactly id, fromConceptId, toConceptId, name, min, max, descriptionZh;
        relation semantic.id MUST be a dot-separated PascalCase ontology FQN such as
        "ItAssetManagement.Relation.AssetOwnedBy", never a bare name, a source symbol, a path, or a UUID.
        Rule semantic.id and lifecycle semantic.id MUST likewise be dot-separated PascalCase ontology FQNs
        (for example "ItAssetManagement.Rule.AssetMustHaveOwner" and
        "ItAssetManagement.Lifecycle.Asset").
        min is "0" or "1", max is "1" or "many", and name is lowerCamelCase. Propose rule or lifecycle
        only when every field required by the supplied schema can be directly grounded in the evidence pack.
        Set basis to "assisted". Use only existing concept IDs and evidence IDs supplied by the user payload.
        Do not invent concepts, source facts, evidence identities, confidence, candidate IDs, status, acceptance,
        review decisions, or fields outside the schema. Never return accepted/status/id/confidence fields at the
        candidate envelope level. Do not reveal hidden chain-of-thought; rationale must be a short bounded summary
        of directly cited evidence. Return at most 24 candidates.
        """;

    private readonly OntologySemanticCandidateValidator candidateValidator =
        validator ?? new OntologySemanticCandidateValidator();

    public async Task<IReadOnlyList<ValidatedOntologySemanticCandidate>> ProposeAsync(
        SemanticEvidencePack evidencePack,
        IEnumerable<string> existingConceptIds,
        IEnumerable<string> availableEvidenceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidencePack);
        ArgumentNullException.ThrowIfNull(existingConceptIds);
        ArgumentNullException.ThrowIfNull(availableEvidenceIds);
        if (!llmClient.IsAvailable)
        {
            throw new InvalidOperationException(
                "Assisted ontology projection requires an available injected ILlmClient: "
                + (llmClient.UnavailableReason ?? "client unavailable"));
        }

        ValidateEvidencePackBounds(evidencePack);
        var concepts = existingConceptIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var evidence = availableEvidenceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var userPrompt = OntologySemanticJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schemaVersion = OntologySemanticCandidateValidator.SchemaVersion,
            existingConceptIds = concepts,
            availableEvidenceIds = evidence,
            evidencePack = JsonSerializer.Deserialize<JsonElement>(evidencePack.ToCanonicalJson()),
        }));
        if (Encoding.UTF8.GetByteCount(userPrompt) > MaxUserPromptUtf8Bytes)
        {
            throw new ArgumentException(
                $"Assisted semantic user prompt exceeds the {MaxUserPromptUtf8Bytes}-byte UTF-8 limit.",
                nameof(evidencePack));
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
                if (Encoding.UTF8.GetByteCount(response) > MaxResponseUtf8Bytes)
                {
                    throw Envelope(
                        "response_bounds",
                        $"Response exceeds the {MaxResponseUtf8Bytes}-byte UTF-8 limit.");
                }
                return ParseResponse(response, concepts, evidence);
            }
            catch (AssistedResponseEnvelopeException) when (attempt < MaxAttempts)
            {
                continue;
            }
            catch (AssistedResponseEnvelopeException ex)
            {
                throw Failure(ex.Category, response, ex, attempt);
            }
            catch (ArgumentException ex)
            {
                throw Failure("semantic_validation", response, ex, attempt);
            }
        }

        throw new InvalidOperationException("Unreachable assisted proposal retry state.");
    }

    public IReadOnlyList<ValidatedOntologySemanticCandidate> ParseResponse(
        string responseJson,
        IEnumerable<string> existingConceptIds,
        IEnumerable<string> availableEvidenceIds)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            throw Envelope("malformed_json", "Response is empty or whitespace.");
        }
        if (Encoding.UTF8.GetByteCount(responseJson) > MaxResponseUtf8Bytes)
        {
            throw Envelope(
                "response_bounds",
                $"Response exceeds the {MaxResponseUtf8Bytes}-byte UTF-8 limit.");
        }
        ArgumentNullException.ThrowIfNull(existingConceptIds);
        ArgumentNullException.ThrowIfNull(availableEvidenceIds);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            throw Envelope("malformed_json", "Response is not strict JSON.", ex);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Envelope("invalid_envelope", "Assisted semantic response '$' must be a JSON object.");
            }
            try
            {
                OntologySemanticJson.RejectDuplicateProperties(document.RootElement, "$");
            }
            catch (ArgumentException ex)
            {
                throw Envelope("invalid_envelope", ex.Message, ex);
            }

            var properties = document.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            var unknown = properties.Except(["schemaVersion", "candidates"], StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
            if (unknown is not null)
            {
                throw Envelope(
                    "invalid_envelope",
                    $"Assisted semantic response field '$.{unknown}' is not allowed.");
            }
            var missing = new[] { "schemaVersion", "candidates" }
                .Except(properties, StringComparer.Ordinal)
                .FirstOrDefault();
            if (missing is not null)
            {
                throw Envelope(
                    "invalid_envelope",
                    $"Assisted semantic response '$' is missing required field '{missing}'.");
            }

            var version = document.RootElement.GetProperty("schemaVersion");
            if (version.ValueKind != JsonValueKind.String
                || version.GetString() != OntologySemanticCandidateValidator.SchemaVersion)
            {
                throw Envelope(
                    "invalid_envelope",
                    $"Assisted semantic response '$.schemaVersion' must be '{OntologySemanticCandidateValidator.SchemaVersion}'.");
            }
            var candidates = document.RootElement.GetProperty("candidates");
            if (candidates.ValueKind != JsonValueKind.Array)
            {
                throw Envelope(
                    "invalid_envelope",
                    "Assisted semantic response '$.candidates' must be an array.");
            }
            if (candidates.GetArrayLength() > MaxCandidates)
            {
                throw Envelope(
                    "response_bounds",
                    $"Assisted semantic response '$.candidates' must contain at most {MaxCandidates} items.");
            }

            var concepts = existingConceptIds.ToHashSet(StringComparer.Ordinal);
            var evidence = availableEvidenceIds.ToHashSet(StringComparer.Ordinal);
            var validated = new List<ValidatedOntologySemanticCandidate>();
            var index = 0;
            foreach (var item in candidates.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw Envelope(
                        "invalid_envelope",
                        $"Assisted semantic response '$.candidates[{index}]' must be an object.");
                }
                try
                {
                    OntologySemanticJson.RejectDuplicateProperties(item, $"$.candidates[{index}]");
                }
                catch (ArgumentException ex)
                {
                    throw Envelope("invalid_envelope", ex.Message, ex);
                }
                var itemProperties = item.EnumerateObject()
                    .Select(property => property.Name)
                    .ToHashSet(StringComparer.Ordinal);
                var allowed = new[] { "kind", "semantic", "evidenceIds", "basis", "rationale" };
                var itemUnknown = itemProperties.Except(allowed, StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault();
                if (itemUnknown is not null)
                {
                    throw Envelope(
                        "invalid_envelope",
                        $"Assisted semantic response field '$.candidates[{index}].{itemUnknown}' is not allowed.");
                }
                var itemMissing = allowed.Except(itemProperties, StringComparer.Ordinal).FirstOrDefault();
                if (itemMissing is not null)
                {
                    throw Envelope(
                        "invalid_envelope",
                        $"Assisted semantic response '$.candidates[{index}]' is missing required field '{itemMissing}'.");
                }
                if (item.GetProperty("basis").ValueKind != JsonValueKind.String
                    || item.GetProperty("basis").GetString() != BusinessOntologySemanticProjectionModes.Assisted)
                {
                    throw Envelope(
                        "invalid_envelope",
                        $"Assisted semantic response '$.candidates[{index}].basis' must be 'assisted'.");
                }

                var payload = JsonSerializer.Serialize(new
                {
                    schemaVersion = OntologySemanticCandidateValidator.SchemaVersion,
                    kind = item.GetProperty("kind").Clone(),
                    semantic = item.GetProperty("semantic").Clone(),
                    evidenceIds = item.GetProperty("evidenceIds").Clone(),
                    basis = item.GetProperty("basis").Clone(),
                    rationale = item.GetProperty("rationale").Clone(),
                });
                validated.Add(candidateValidator.Validate(payload, concepts, evidence));
                index++;
            }
            return validated
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group
                    .OrderBy(item => item.CanonicalPayloadJson, StringComparer.Ordinal)
                    .First())
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private static void ValidateEvidencePackBounds(SemanticEvidencePack evidencePack)
    {
        if (evidencePack.Anchors.Count > SemanticEvidencePackBuilder.MaxAnchors
            || evidencePack.SourceUtf8Bytes < 0
            || evidencePack.SourceUtf8Bytes > SemanticEvidencePackBuilder.MaxSourceUtf8Bytes)
        {
            throw new ArgumentException(
                "Assisted semantic evidence pack exceeds its anchor or source-text bounds.",
                nameof(evidencePack));
        }
    }

    private static AssistedResponseEnvelopeException Envelope(
        string category,
        string message,
        Exception? innerException = null) =>
        new(category, message, innerException);

    private static OntologySemanticAssistedProposalException Failure(
        string category,
        string response,
        Exception innerException,
        int attempts)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(response)))
            .ToLowerInvariant();
        var message = BoundedMessage(innerException.Message);
        return new OntologySemanticAssistedProposalException(
            new OntologySemanticAssistedFailureDiagnostic(category, digest, message, attempts));
    }

    private static string BoundedMessage(string value)
    {
        var normalized = string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaxDiagnosticMessageCharacters
            ? normalized
            : normalized[..MaxDiagnosticMessageCharacters];
    }

    private sealed class AssistedResponseEnvelopeException(
        string category,
        string message,
        Exception? innerException = null)
        : ArgumentException(message, innerException)
    {
        public string Category { get; } = category;
    }
}
