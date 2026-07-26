using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Cozo.DotNet.LlmWiki.Tools;

/// <summary>
/// Writes an isolated, pending-only v3 artifact bundle. It never opens a Cozo database and has no
/// accepted-ontology or promotion dependency.
/// </summary>
internal interface IBusinessOntologySemanticPublicationQualityEvaluator
{
    string? SourceFingerprint { get; }

    Task<BusinessOntologySemanticQualityReport> EvaluateAsync(
        BusinessOntologySemanticPendingPublication publication,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Evaluates a pending publication against a server-owned, already-read deterministic ontology
/// projection. The pending draft never chooses the baseline, source fingerprint, or critic run.
/// </summary>
internal sealed class BusinessOntologySemanticPublicationQualityEvaluator(
    BusinessOntologySnapshot baselineSnapshot)
    : IBusinessOntologySemanticPublicationQualityEvaluator
{
    private readonly IReadOnlyList<BusinessOntologySemanticProjectionCarrier> _carriers = Carriers(baselineSnapshot);

    public string? SourceFingerprint { get; } = SourceFingerprintFor(baselineSnapshot, Carriers(baselineSnapshot));

    public static async Task<BusinessOntologySemanticPublicationQualityEvaluator> CreateAsync(
        BusinessOntologyStore store,
        string ontologyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(ontologyId);
        return new BusinessOntologySemanticPublicationQualityEvaluator(
            await store.ReadExportableAsync(ontologyId, cancellationToken));
    }

    public Task<BusinessOntologySemanticQualityReport> EvaluateAsync(
        BusinessOntologySemanticPendingPublication publication,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(publication);
        var sourceFingerprint = SourceFingerprint!;
        var inputFingerprint = publication.Envelope.InputDigest;
        var routing = publication.Envelope.CriticRouting;
        var context = new BusinessOntologySemanticEvaluationContext(
            sourceFingerprint,
            inputFingerprint,
            "synthesis:" + inputFingerprint,
            routing.Provenance.SnapshotDigest);
        var carrierDigest = BusinessOntologySemanticProjectionBaselineDigest.Compute(_carriers);
        var baseline = new BusinessOntologySemanticVerifiedProjectionBaseline(
            sourceFingerprint,
            inputFingerprint,
            carrierDigest,
            BusinessOntologySemanticProjectionBaselineDigest.ComputeIntegrityDigest(
                sourceFingerprint, inputFingerprint, carrierDigest, _carriers.Count),
            _carriers.Count,
            _carriers);
        var clusters = publication.Drafts.SelectMany(draft => draft.Clusters).OrderBy(cluster => cluster.ConceptId, StringComparer.Ordinal).ToArray();
        var relations = publication.Drafts.SelectMany(draft => draft.Relations).OrderBy(relation => relation.Id, StringComparer.Ordinal).ToArray();
        var rules = publication.Drafts.SelectMany(draft => draft.Rules).OrderBy(rule => rule.Id, StringComparer.Ordinal).ToArray();
        var lifecycles = publication.Drafts.SelectMany(draft => draft.Lifecycles).OrderBy(lifecycle => lifecycle.Id, StringComparer.Ordinal).ToArray();
        // Attributes are reviewable draft details but intentionally outside the P1 candidate-bundle
        // contract. Their critic verdicts must not make the smaller quality-gate candidate set look
        // incomplete or forge a verdict for a nonexistent semantic edge.
        var qualityCandidateIds = clusters.Select(cluster => cluster.ConceptId)
            .Concat(relations.Select(relation => relation.Id))
            .Concat(rules.Select(rule => rule.Id))
            .Concat(lifecycles.Select(lifecycle => lifecycle.Id))
            .ToHashSet(StringComparer.Ordinal);
        var bundle = new BusinessOntologySemanticCandidateBundle(
            baselineSnapshot.OntologyId,
            context.SynthesisRunId,
            sourceFingerprint,
            inputFingerprint,
            routing.Provenance,
            publication.Evidence,
            publication.Drafts.SelectMany(draft => draft.DomainCharters).DistinctBy(charter => charter.Id).OrderBy(charter => charter.Id, StringComparer.Ordinal).ToArray(),
            clusters,
            relations,
            rules,
            lifecycles,
            routing.Verdicts.Where(verdict => qualityCandidateIds.Contains(verdict.CandidateId)).ToArray());
        var report = new BusinessOntologySemanticQualityGate().Evaluate(
            bundle,
            context,
            new FixedBaselineProvider(baseline),
            new ExpectedCriticRunVerifier(routing.Provenance));
        return Task.FromResult(report);
    }

    private static IReadOnlyList<BusinessOntologySemanticProjectionCarrier> Carriers(BusinessOntologySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var mappings = snapshot.Mappings
            .Where(mapping => mapping.SubjectKind == "concept" && !string.IsNullOrWhiteSpace(mapping.SubjectId)
                && !string.IsNullOrWhiteSpace(mapping.Symbol))
            .GroupBy(mapping => mapping.SubjectId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(mapping => mapping.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var carriers = snapshot.Concepts
            .Where(concept => mappings.ContainsKey(concept.Id))
            .OrderBy(concept => concept.Id, StringComparer.Ordinal)
            .Select(concept => new BusinessOntologySemanticProjectionCarrier(concept.Id, mappings[concept.Id]))
            .ToArray();
        if (carriers.Length == 0)
        {
            throw new InvalidOperationException("Configured semantic baseline has no deterministic concept mappings.");
        }
        return carriers;
    }

    private static string SourceFingerprintFor(
        BusinessOntologySnapshot snapshot,
        IReadOnlyList<BusinessOntologySemanticProjectionCarrier> carriers)
    {
        var carrierDigest = BusinessOntologySemanticProjectionBaselineDigest.Compute(carriers);
        var payload = $"semantic-v3-baseline\n{snapshot.OntologyId}\n{snapshot.GenerationId}\n{carrierDigest}\n{carriers.Count}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private sealed class FixedBaselineProvider(BusinessOntologySemanticVerifiedProjectionBaseline baseline)
        : IBusinessOntologySemanticVerifiedProjectionBaselineProvider
    {
        public BusinessOntologySemanticVerifiedProjectionBaseline GetVerifiedBaseline(BusinessOntologySemanticEvaluationContext context) => baseline;
    }

    private sealed class ExpectedCriticRunVerifier(BusinessOntologySemanticCriticRunProvenance expected)
        : IBusinessOntologySemanticTrustedCriticRunVerifier
    {
        public bool VerifyCriticRun(
            BusinessOntologySemanticEvaluationContext context,
            BusinessOntologySemanticCriticRunProvenance criticRun) =>
            criticRun == expected
            && criticRun.SourceFingerprint == context.SourceFingerprint
            && criticRun.InputFingerprint == context.InputFingerprint
            && criticRun.SnapshotDigest == context.ExpectedCriticSnapshotDigest;
    }
}

/// <summary>Missing or unreadable baseline configuration is an explicit failed quality result.</summary>
internal sealed class BusinessOntologySemanticUnavailablePublicationQualityEvaluator
    : IBusinessOntologySemanticPublicationQualityEvaluator
{
    public string? SourceFingerprint => null;

    public Task<BusinessOntologySemanticQualityReport> EvaluateAsync(
        BusinessOntologySemanticPendingPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return Task.FromResult(new BusinessOntologySemanticQualityReport(
            BusinessOntologySemanticQualityGate.Failed,
            publication.Drafts.SelectMany(draft => draft.DomainCharters).DistinctBy(charter => charter.Id).Count(),
            publication.Drafts.Sum(draft => draft.Clusters.Count),
            publication.Drafts.Sum(draft => draft.Relations.Count),
            publication.Drafts.Sum(draft => draft.Rules.Count),
            publication.Drafts.Sum(draft => draft.Lifecycles.Count),
            0,
            0,
            [new BusinessOntologySemanticQualityDiagnostic(
                "verified_baseline_provider_unavailable", "error",
                "未配置或无法读取 server-owned 确定性投影基线；pending 制品只能进入人工审核，不能声明语义质量通过。")]));
    }
}

internal sealed class BusinessOntologySemanticArtifactPublisher(
    string artifactRoot,
    IBusinessOntologySemanticPublicationQualityEvaluator? qualityEvaluator = null)
    : IBusinessOntologySemanticPendingPublicationObserver
{
    private readonly string artifactRoot = Path.GetFullPath(artifactRoot ?? throw new ArgumentNullException(nameof(artifactRoot)));
    private readonly IBusinessOntologySemanticPublicationQualityEvaluator qualityEvaluator = qualityEvaluator ?? new BusinessOntologySemanticUnavailablePublicationQualityEvaluator();

    public async Task PublishAsync(BusinessOntologySemanticPendingPublication publication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (publication.Envelope.AcceptedOntologyMutationCount != 0)
        {
            throw new InvalidOperationException("Pending semantic publication must not mutate accepted ontology.");
        }

        var quality = await qualityEvaluator.EvaluateAsync(publication, cancellationToken);

        Directory.CreateDirectory(artifactRoot);
        var runName = "semantic-v3-" + publication.Envelope.InputDigest[..16] + "-" + publication.Envelope.CriticDigest[..16];
        var destination = Path.Combine(artifactRoot, runName);
        if (Directory.Exists(destination))
        {
            throw new InvalidOperationException("Refusing to overwrite an existing semantic artifact run.");
        }
        var temporary = Path.Combine(artifactRoot, "." + runName + "." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            await WriteJsonAsync(Path.Combine(temporary, "domain-charters.json"), publication.Drafts
                .SelectMany(draft => draft.DomainCharters).DistinctBy(charter => charter.Id).OrderBy(charter => charter.Id, StringComparer.Ordinal).ToArray(), cancellationToken);
            await WriteCandidateXmlAsync(Path.Combine(temporary, "semantic-candidate.xml"), publication, cancellationToken);
            await WriteJsonAsync(Path.Combine(temporary, "review-packet.json"), new
            {
                schemaVersion = "business-ontology-semantic-review-packet-v1",
                status = "pending_human_review",
                verdicts = publication.Envelope.CriticRouting.Verdicts,
                pendingCandidateIds = publication.Envelope.CriticRouting.PendingCandidateIds,
                reviewCandidateIds = publication.Envelope.CriticRouting.ReviewCandidateIds,
                diagnosisCandidateIds = publication.Envelope.CriticRouting.DiagnosisCandidateIds,
            }, cancellationToken);
            await WriteJsonAsync(Path.Combine(temporary, "quality-report.json"), new
            {
                schemaVersion = "business-ontology-semantic-quality-report-v1",
                status = quality.Status,
                domains = publication.DomainStatuses,
                quality.DomainCount,
                quality.ConceptCount,
                quality.RelationCount,
                quality.RuleCount,
                quality.LifecycleCount,
                quality.SingleAnchorConceptRatio,
                quality.ProjectionLikeConceptRatio,
                diagnostics = quality.Diagnostics,
                pendingCandidateCount = publication.Envelope.PendingCandidateCount,
                acceptedOntologyMutationCount = publication.Envelope.AcceptedOntologyMutationCount,
            }, cancellationToken);
            await WriteJsonAsync(Path.Combine(temporary, "provenance.json"), new
            {
                schemaVersion = publication.Provenance.SchemaVersion,
                inputDigest = publication.Envelope.InputDigest,
                criticDigest = publication.Envelope.CriticDigest,
                criticSnapshotDigest = publication.Envelope.CriticRouting.Provenance.SnapshotDigest,
                completeDraftDigest = publication.Envelope.CriticRouting.CompleteDraftDigest,
                phaseTrace = publication.PhaseTrace,
                modelCalls = publication.Provenance.ModelCalls,
                queryDigests = publication.Provenance.QueryDigests,
                budget = publication.Provenance.Budget,
                evidence = publication.Evidence.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            }, cancellationToken);
            Directory.Move(temporary, destination);
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    private static Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

    private static async Task WriteCandidateXmlAsync(string path, BusinessOntologySemanticPendingPublication publication, CancellationToken cancellationToken)
    {
        var kept = publication.Envelope.CriticRouting.PendingCandidateIds.ToHashSet(StringComparer.Ordinal);
        await using var stream = File.Create(path);
        await using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Async = true, Indent = true });
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "semantic-candidate", null);
        await writer.WriteAttributeStringAsync(null, "status", null, "pending");
        await writer.WriteAttributeStringAsync(null, "inputDigest", null, publication.Envelope.InputDigest);
        foreach (var draft in publication.Drafts.OrderBy(item => item.DomainId, StringComparer.Ordinal))
        {
            await writer.WriteStartElementAsync(null, "domain", null);
            await writer.WriteAttributeStringAsync(null, "id", null, draft.DomainId);
            foreach (var cluster in draft.Clusters.Where(item => kept.Contains(item.ConceptId)).OrderBy(item => item.ConceptId, StringComparer.Ordinal))
            {
                await writer.WriteStartElementAsync(null, "type", null);
                await writer.WriteAttributeStringAsync(null, "id", null, cluster.ConceptId);
                await writer.WriteElementStringAsync(null, "nameZh", null, cluster.NameZh);
                await writer.WriteElementStringAsync(null, "descriptionZh", null, cluster.DescriptionZh);
                foreach (var anchor in cluster.ImplementationAnchors.OrderBy(item => item.SymbolId, StringComparer.Ordinal))
                {
                    await writer.WriteStartElementAsync(null, "mapping", null);
                    await writer.WriteAttributeStringAsync(null, "symbol", null, anchor.SymbolId);
                    await writer.WriteAttributeStringAsync(null, "role", null, anchor.Role);
                    await writer.WriteAttributeStringAsync(null, "path", null, anchor.RelativePath);
                    await writer.WriteAttributeStringAsync(null, "evidence", null, anchor.EvidenceId);
                    await writer.WriteEndElementAsync();
                }
                await writer.WriteEndElementAsync();
            }
            foreach (var attribute in draft.Attributes.Where(item => kept.Contains(item.Id)).OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                await writer.WriteStartElementAsync(null, "attribute", null);
                await writer.WriteAttributeStringAsync(null, "id", null, attribute.Id);
                await writer.WriteAttributeStringAsync(null, "subject", null, attribute.SubjectConceptId);
                await writer.WriteAttributeStringAsync(null, "name", null, attribute.Name);
                await writer.WriteAttributeStringAsync(null, "valueType", null, attribute.ValueType);
                await writer.WriteElementStringAsync(null, "descriptionZh", null, attribute.DescriptionZh);
                foreach (var evidenceId in attribute.EvidenceIds.Order(StringComparer.Ordinal))
                {
                    await writer.WriteStartElementAsync(null, "evidence", null);
                    await writer.WriteAttributeStringAsync(null, "id", null, evidenceId);
                    await writer.WriteEndElementAsync();
                }
                await writer.WriteEndElementAsync();
            }
            foreach (var relation in draft.Relations.Where(item => kept.Contains(item.Id)).OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                await writer.WriteStartElementAsync(null, "relation", null);
                await writer.WriteAttributeStringAsync(null, "id", null, relation.Id);
                await writer.WriteAttributeStringAsync(null, "from", null, relation.FromConceptId);
                await writer.WriteAttributeStringAsync(null, "to", null, relation.ToConceptId);
                await writer.WriteAttributeStringAsync(null, "name", null, relation.Name);
                await writer.WriteElementStringAsync(null, "descriptionZh", null, relation.DescriptionZh);
                await WriteEvidenceBindingsAsync(writer, relation.EvidenceBindings);
                await writer.WriteEndElementAsync();
            }
            foreach (var rule in draft.Rules.Where(item => kept.Contains(item.Id)).OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                await writer.WriteStartElementAsync(null, "rule", null);
                await writer.WriteAttributeStringAsync(null, "id", null, rule.Id);
                await writer.WriteAttributeStringAsync(null, "subject", null, rule.SubjectConceptId);
                await writer.WriteElementStringAsync(null, "descriptionZh", null, rule.DescriptionZh);
                await WriteEvidenceBindingsAsync(writer, rule.EvidenceBindings);
                await writer.WriteEndElementAsync();
            }
            foreach (var lifecycle in draft.Lifecycles.Where(item => kept.Contains(item.Id)).OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                await writer.WriteStartElementAsync(null, "lifecycle", null);
                await writer.WriteAttributeStringAsync(null, "id", null, lifecycle.Id);
                await writer.WriteAttributeStringAsync(null, "subject", null, lifecycle.SubjectConceptId);
                await writer.WriteAttributeStringAsync(null, "stateProperty", null, lifecycle.StateProperty);
                await writer.WriteElementStringAsync(null, "descriptionZh", null, lifecycle.DescriptionZh);
                await WriteEvidenceBindingsAsync(writer, lifecycle.EvidenceBindings);
                await writer.WriteEndElementAsync();
            }
            await writer.WriteEndElementAsync();
        }
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
    }

    private static async Task WriteEvidenceBindingsAsync(
        XmlWriter writer,
        IReadOnlyList<BusinessOntologySemanticClaimEvidenceBinding> bindings)
    {
        foreach (var binding in bindings.OrderBy(item => item.EvidenceId, StringComparer.Ordinal))
        {
            await writer.WriteStartElementAsync(null, "evidenceBinding", null);
            await writer.WriteAttributeStringAsync(null, "type", null, binding.BindingType);
            await writer.WriteAttributeStringAsync(null, "evidence", null, binding.EvidenceId);
            await writer.WriteStringAsync(binding.SummaryZh);
            await writer.WriteEndElementAsync();
        }
    }
}
