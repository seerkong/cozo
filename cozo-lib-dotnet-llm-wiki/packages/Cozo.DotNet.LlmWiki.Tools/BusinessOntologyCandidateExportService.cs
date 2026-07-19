using System.Text.Json;
using System.Text.RegularExpressions;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Tools;

/// <summary>
/// The only shared operation that composes candidate build, validation, publication, and
/// diagnosis. Its inputs are analysis identities, never caller supplied files, SQL, or evidence.
/// </summary>
public sealed record BusinessOntologyCandidateExportRequest(
    string OntologyId,
    string GenerationId,
    string AnalysisRunId,
    string? Version = null,
    string? BundleId = null);

public sealed record BusinessOntologyCandidateExportResult(
    string OntologyId,
    string GenerationId,
    string AnalysisRunId,
    string Version,
    string BundleId,
    string? PublishedRelativePath,
    int CandidateCount,
    int ExcludedCount,
    int DiagnosisItemCount,
    int ReviewItemCount,
    string InputDigest);

public sealed class BusinessOntologyCandidateExportService(CozoOm om, string outputRoot, string bunExecutable, string validatorScript)
{
    private static readonly Regex BundleIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[a-z0-9][a-z0-9.-]{0,63})?$", RegexOptions.CultureInvariant);
    private readonly BusinessOntologyAnalysisStore _analysisStore = new(om);
    private readonly BusinessOntologyStore _ontologyStore = new(om);
    private readonly BusinessOntologyInvestigationService _investigation = new(om, new BusinessOntologyStore(om));

    public async Task<BusinessOntologyCandidateExportResult> ExportAsync(
        BusinessOntologyCandidateExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ontologyId = Required(request.OntologyId, nameof(request.OntologyId));
        var generationId = Required(request.GenerationId, nameof(request.GenerationId));
        var analysisRunId = Required(request.AnalysisRunId, nameof(request.AnalysisRunId));
        var version = RequireVersion(request.Version ?? "0.0.0-hypothesis");
        var bundleId = RequireBundleId(request.BundleId ?? DefaultBundleId(analysisRunId));
        var workspace = await _analysisStore.ReadWorkspaceAsync(ontologyId, generationId, cancellationToken);
        var run = workspace.Runs.SingleOrDefault(item => StringComparer.Ordinal.Equals(item.RunId, analysisRunId))
            ?? throw new ArgumentException("analysisRunId must identify an existing isolated analysis run in the requested ontology generation.", nameof(request));
        var records = workspace.Records.Where(item => StringComparer.Ordinal.Equals(item.RunId, run.RunId)).ToArray();
        var snapshot = await ReadMatchingSnapshotAsync(ontologyId, generationId, cancellationToken);
        var evidence = await ResolveEvidenceAsync(records, snapshot, cancellationToken);
        var bundleRequest = new BusinessOntologyCandidateBundleRequest(
            ontologyId,
            generationId,
            version,
            CandidateConceptClosure(records, snapshot),
            evidence,
            analysisRunId);
        var builder = new BusinessOntologyCandidateBundleBuilder(_analysisStore);
        var publication = await new BusinessOntologyCandidateBundlePublisher(builder).PublishAsync(
            new BusinessOntologyCandidateBundlePublicationRequest(
                bundleRequest,
                outputRoot,
                bunExecutable,
                validatorScript,
                bundleId),
            cancellationToken);

        var diagnosisItems = 0;
        var reviewItems = 0;
        if (publication.PublishedDirectory is not null)
        {
            var artifacts = await new BusinessOntologyCandidateArtifactWriter(_analysisStore).WriteAsync(
                publication.Build,
                new BusinessOntologyCandidateArtifactWriteRequest(
                    bundleRequest,
                    publication.PublishedDirectory,
                    DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    new Dictionary<string, string>(StringComparer.Ordinal)),
                cancellationToken);
            diagnosisItems = artifacts.Diagnosis.Items.Count;
            reviewItems = artifacts.ReviewPacket.Items.Count;
        }

        return new BusinessOntologyCandidateExportResult(
            ontologyId,
            generationId,
            analysisRunId,
            version,
            bundleId,
            publication.PublishedDirectory is null ? null : Path.GetRelativePath(Path.GetFullPath(outputRoot), publication.PublishedDirectory),
            publication.Build.Mappings.Count,
            publication.Build.Exclusions.Count,
            diagnosisItems,
            reviewItems,
            Digest($"{ontologyId}|{generationId}|{analysisRunId}|{version}|{bundleId}|{run.InputDigest}"));
    }

    private async Task<BusinessOntologySnapshot?> ReadMatchingSnapshotAsync(string ontologyId, string generationId, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _ontologyStore.ReadExportableAsync(ontologyId, cancellationToken);
            return StringComparer.Ordinal.Equals(snapshot.GenerationId, generationId) ? snapshot : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<BusinessOntologyCandidateEvidenceFact>> ResolveEvidenceAsync(
        IReadOnlyList<BusinessOntologyAnalysisRecord> records,
        BusinessOntologySnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        var ids = records.SelectMany(record => record.EvidenceIds).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, BusinessOntologyCandidateEvidenceFact>(StringComparer.Ordinal);
        foreach (var evidence in snapshot?.Evidence ?? [])
        {
            if (!ids.Contains(evidence.Id, StringComparer.Ordinal)) continue;
            result[evidence.Id] = new BusinessOntologyCandidateEvidenceFact(
                evidence.Id, evidence.Repository, evidence.Path, evidence.Symbol, evidence.StartLine, evidence.EndLine,
                evidence.Grade, evidence.Resolver, evidence.Confidence, evidence.SourceKind, evidence.Summary);
        }
        foreach (var batch in ids.Where(id => !result.ContainsKey(id)).Chunk(SemanticEvidencePackBuilder.MaxAnchors))
        {
            var metadata = await _investigation.GetSemanticEvidenceMetadataAsync(batch, cancellationToken);
            foreach (var anchor in metadata)
            {
                result[anchor.EvidenceId] = new BusinessOntologyCandidateEvidenceFact(
                    anchor.EvidenceId, anchor.Repository, anchor.Path, anchor.SubjectId, anchor.StartLine, anchor.EndLine,
                    "inferred", anchor.Resolver, anchor.Confidence, "code", "已索引语义事实支持该候选。");
            }
        }
        return result.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    // A candidate bundle is self-contained: accepted ontology may enrich the closure, but a
    // candidate run must never become unexportable merely because no accepted snapshot exists.
    private static IReadOnlyList<string> CandidateConceptClosure(
        IReadOnlyList<BusinessOntologyAnalysisRecord> records,
        BusinessOntologySnapshot? snapshot)
    {
        var concepts = new HashSet<string>(
            snapshot?.Concepts.Select(item => item.Id) ?? [],
            StringComparer.Ordinal);
        foreach (var record in records.Where(item => StringComparer.Ordinal.Equals(item.Kind, "candidate_draft")))
        {
            try
            {
                using var document = JsonDocument.Parse(record.BodyJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("semantic", out var semantic) || semantic.ValueKind != JsonValueKind.Object) continue;
                CollectCandidateConcepts(semantic, concepts);
            }
            catch (JsonException)
            {
                // The strict mapping validator records malformed candidate drafts in diagnosis.
            }
        }
        return concepts.OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }

    private static void CollectCandidateConcepts(JsonElement element, ISet<string> concepts)
    {
        var conceptKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "ownerConceptId", "fromConceptId", "toConceptId", "parentConceptId", "scope", "subject", "targetType"
        };
        foreach (var property in element.EnumerateObject())
        {
            if (conceptKeys.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                if (value is not null && FqnPattern.IsMatch(value)) concepts.Add(value);
            }
            if (property.Value.ValueKind == JsonValueKind.Object) CollectCandidateConcepts(property.Value, concepts);
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in property.Value.EnumerateArray())
                {
                    if (child.ValueKind == JsonValueKind.Object) CollectCandidateConcepts(child, concepts);
                }
            }
        }
    }

    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException($"{name} is required.", name)
        : value.Trim();

    private static string RequireVersion(string value)
    {
        if (!VersionPattern.IsMatch(value)) throw new ArgumentException("version must be a bounded semantic version.", nameof(value));
        return value;
    }

    private static string RequireBundleId(string value)
    {
        if (!BundleIdPattern.IsMatch(value)) throw new ArgumentException("bundleId must be a single safe directory token.", nameof(value));
        return value;
    }

    private static string DefaultBundleId(string analysisRunId) => "candidate-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(analysisRunId))).ToLowerInvariant()[..16];

    private static string Digest(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static readonly Regex FqnPattern = new("^[A-Z][A-Za-z0-9]*(?:\\.[A-Z][A-Za-z0-9]*)+$", RegexOptions.CultureInvariant);
}
