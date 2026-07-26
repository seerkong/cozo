using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cozo.DotNet.LlmWiki.LlmClient;

namespace Cozo.DotNet.LlmWiki.Tools;

internal static class BusinessOntologySemanticSynthesisStatuses
{
    public const string Completed = "completed";
    public const string CompletedWithFailures = "completed_with_failures";
    public const string Blocked = "blocked";
    public const string Cancelled = "cancelled";
}

/// <summary>Bounded, prompt-free public request for the v3 semantic synthesis tool.</summary>
internal sealed record BusinessOntologySemanticSynthesisRequest(IReadOnlyList<string>? DomainTerms = null);

/// <summary>
/// A dependency identity is runtime-owned: the role and configuration fingerprint are derived
/// locally, while separate configuration/client instances prevent a critic aliasing modeler state.
/// </summary>
internal sealed class BusinessOntologySemanticLlmDependency
{
    public BusinessOntologySemanticLlmDependency(string role, ILlmClient client, LlmClientConfig configuration)
    {
        if (role is not ("modeler" or "critic"))
        {
            throw new ArgumentException("Semantic synthesis LLM role must be modeler or critic.", nameof(role));
        }

        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(configuration);
        Role = role;
        Client = client;
        Configuration = configuration;
        Identity = DependencyDigest("llm-dependency", new
        {
            role,
            configuration.Provider,
            configuration.BaseUrl,
            configuration.Model,
            timeoutSeconds = configuration.Timeout?.TotalSeconds,
            configuration.CodexCliPath,
            configuration.CodexCliModelProvider,
            configuration.CodexCliBaseUrl,
            configuration.CodexCliWireApi,
        });
    }

    public string Role { get; }
    public ILlmClient Client { get; }
    public LlmClientConfig Configuration { get; }
    public string Identity { get; }

    private static string DependencyDigest(string scope, object value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { scope, value })))).ToLowerInvariant();
}

internal sealed record BusinessOntologySemanticSynthesisDomainStatus(string DomainId, string Status, bool FromCache);

/// <summary>Critic-safe staging shape: no evidence identifiers, symbols, repositories, or paths.</summary>
internal sealed record BusinessOntologySemanticCriticStagedDraft(
    string DomainId,
    IReadOnlyList<BusinessOntologySemanticCriticStagedConcept> Concepts,
    IReadOnlyList<BusinessOntologySemanticCriticStagedAttribute> Attributes,
    IReadOnlyList<BusinessOntologySemanticCriticStagedRelation> Relations,
    IReadOnlyList<BusinessOntologySemanticCriticStagedRule> Rules,
    IReadOnlyList<BusinessOntologySemanticCriticStagedLifecycle> Lifecycles);

internal sealed record BusinessOntologySemanticCriticStagedConcept(
    string ConceptId, string NameZh, string DescriptionZh, int ImplementationAnchorCount, IReadOnlyList<string> Roles);
internal sealed record BusinessOntologySemanticCriticStagedAttribute(
    string Id, string SubjectConceptId, string Name, string ValueType, string DescriptionZh);
internal sealed record BusinessOntologySemanticCriticStagedRelation(
    string Id, string FromConceptId, string ToConceptId, string Name, string DescriptionZh, IReadOnlyList<string> BindingTypes);
internal sealed record BusinessOntologySemanticCriticStagedRule(
    string Id, string SubjectConceptId, string DescriptionZh, IReadOnlyList<string> BindingTypes);
internal sealed record BusinessOntologySemanticCriticStagedLifecycle(
    string Id, string SubjectConceptId, string StateProperty, string DescriptionZh, IReadOnlyList<string> BindingTypes);

/// <summary>Only critic-safe staging and aggregate/digest material crosses into the pending result.</summary>
internal sealed record BusinessOntologySemanticPendingEnvelope(
    int DomainCount,
    int CriticReviewedDomainCount,
    int AcceptedOntologyMutationCount,
    string InputDigest,
    string CriticDigest,
    IReadOnlyList<BusinessOntologySemanticCriticStagedDraft> StagedDrafts,
    BusinessOntologySemanticCriticRouting CriticRouting)
{
    public int PendingCandidateCount => CriticRouting.PendingCandidateIds.Count;
    public int ReviewCandidateCount => CriticRouting.ReviewCandidateIds.Count;
    public int DiagnosisCandidateCount => CriticRouting.DiagnosisCandidateIds.Count;
}

internal sealed record BusinessOntologySemanticSynthesisResult(
    string Status,
    IReadOnlyList<string> PhaseTrace,
    IReadOnlyList<BusinessOntologySemanticSynthesisDomainStatus> DomainStatuses,
    BusinessOntologySemanticPendingEnvelope? PendingEnvelope);

/// <summary>
/// Trusted in-process hand-off for pending artifact publication. It is deliberately unavailable
/// from the public summary path: raw evidence and mappings never cross the tool boundary.
/// </summary>
internal sealed record BusinessOntologySemanticPendingPublication(
    string Status,
    IReadOnlyList<string> PhaseTrace,
    IReadOnlyList<BusinessOntologySemanticSynthesisDomainStatus> DomainStatuses,
    BusinessOntologySemanticPendingEnvelope Envelope,
    IReadOnlyList<BusinessOntologySemanticPendingDraft> Drafts,
    IReadOnlyList<BusinessOntologySemanticEvidence> Evidence,
    BusinessOntologySemanticPublicationProvenance Provenance);

/// <summary>
/// Replay-oriented, non-secret publication metadata. It records only operation names, response
/// digests, query digests, and active hard caps; prompts, provider configuration, and raw model
/// responses remain outside the artifact contract.
/// </summary>
internal sealed record BusinessOntologySemanticPublicationModelCall(
    string Phase,
    string ResponseDigest,
    int MaxTokens);

internal sealed record BusinessOntologySemanticPublicationBudget(
    int MaxDomains,
    int MaxCompletionsPerDomain,
    int MaxInvestigationOperationsPerDomain);

internal sealed record BusinessOntologySemanticPublicationProvenance(
    string SchemaVersion,
    IReadOnlyList<BusinessOntologySemanticPublicationModelCall> ModelCalls,
    IReadOnlyList<string> QueryDigests,
    BusinessOntologySemanticPublicationBudget Budget);

internal interface IBusinessOntologySemanticPendingPublicationObserver
{
    Task PublishAsync(BusinessOntologySemanticPendingPublication publication, CancellationToken cancellationToken = default);
}

internal sealed record BusinessOntologySemanticSynthesisStage(
    string Phase,
    string Digest,
    IReadOnlyList<BusinessOntologySemanticEvidence>? RuntimeEvidence = null,
    BusinessOntologySemanticPendingDraft? PendingDraft = null,
    IReadOnlyList<string>? QueryDigests = null);

internal sealed record BusinessOntologySemanticDiscoveredDomain(
    BusinessOntologySemanticDomainCharter Charter,
    IReadOnlyList<BusinessOntologySemanticEvidence> RuntimeEvidence,
    IReadOnlyList<string> QueryDigests);

/// <summary>
/// Production v3 control path. It produces only an in-memory, critic-routed pending envelope.
/// </summary>
internal sealed class BusinessOntologySemanticSynthesisOrchestrator
{
    private const int MaxDomainTerms = BusinessOntologySemanticDomainWorkLimits.DefaultMaxDomains;
    private static readonly IReadOnlyList<string> DefaultAutoDiscoveryTerms = ["business"];
    // This is the closed claim-kind vocabulary exposed by the indexed investigation boundary.
    private static readonly IReadOnlySet<string> IndexedSemanticClaimKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "typed_reference", "validation_constraint", "persistence_constraint", "state_field", "state_value",
        "state_assignment", "transaction_scope", "route_binding", "business_guard",
    };

    private readonly IBusinessOntologyInvestigationOperations _investigation;
    private readonly string _sourceFingerprint;
    private readonly BusinessOntologySemanticDomainWorkCoordinator _coordinator = new();
    private readonly BusinessOntologySemanticDomainWorkCache<BusinessOntologySemanticSynthesisStage> _exploreCache = new();
    private readonly BusinessOntologySemanticDomainWorkCache<BusinessOntologySemanticSynthesisStage> _synthesizeCache = new();

    public BusinessOntologySemanticSynthesisOrchestrator(
        IBusinessOntologyInvestigationOperations investigation,
        BusinessOntologySemanticLlmDependency modeler,
        BusinessOntologySemanticLlmDependency critic,
        string? sourceFingerprint = null)
    {
        _investigation = investigation ?? throw new ArgumentNullException(nameof(investigation));
        Modeler = modeler ?? throw new ArgumentNullException(nameof(modeler));
        Critic = critic ?? throw new ArgumentNullException(nameof(critic));
        if (ReferenceEquals(Modeler.Client, Critic.Client)
            || ReferenceEquals(Modeler.Configuration, Critic.Configuration)
            || string.Equals(Modeler.Identity, Critic.Identity, StringComparison.Ordinal))
        {
            throw new ArgumentException("Modeler and critic must be independent runtime dependencies.");
        }
        _sourceFingerprint = string.IsNullOrWhiteSpace(sourceFingerprint) ? Modeler.Identity : sourceFingerprint;
    }

    internal BusinessOntologySemanticLlmDependency Modeler { get; }
    internal BusinessOntologySemanticLlmDependency Critic { get; }

    public async Task<BusinessOntologySemanticSynthesisResult> RunAsync(
        BusinessOntologySemanticSynthesisRequest request,
        CancellationToken cancellationToken = default,
        IBusinessOntologySemanticPendingPublicationObserver? publicationObserver = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var phaseTrace = new List<string>(5);
        if (cancellationToken.IsCancellationRequested)
        {
            return NotPublished(BusinessOntologySemanticSynthesisStatuses.Cancelled, phaseTrace, []);
        }
        // Validate caller-controlled bounds before any environment-dependent early exit.
        var terms = NormalizeTerms(request.DomainTerms);
        if (!Modeler.Client.IsAvailable || !Critic.Client.IsAvailable)
        {
            return NotPublished(BusinessOntologySemanticSynthesisStatuses.Blocked, phaseTrace, []);
        }

        IReadOnlyList<BusinessOntologySemanticDiscoveredDomain> discoveredDomains;
        try
        {
            discoveredDomains = await DiscoverAsync(terms, cancellationToken);
            phaseTrace.Add("discover:completed");
        }
        catch (OperationCanceledException)
        {
            phaseTrace.Add("discover:cancelled");
            return NotPublished(BusinessOntologySemanticSynthesisStatuses.Cancelled, phaseTrace, []);
        }
        catch
        {
            phaseTrace.Add("discover:failed");
            return NotPublished(BusinessOntologySemanticSynthesisStatuses.Blocked, phaseTrace, []);
        }

        if (discoveredDomains.Count == 0)
        {
            phaseTrace.Add("explore:skipped");
            phaseTrace.Add("synthesize:skipped");
            phaseTrace.Add("critic:skipped");
            phaseTrace.Add("publish:not_published");
            return new BusinessOntologySemanticSynthesisResult(
                BusinessOntologySemanticSynthesisStatuses.CompletedWithFailures,
                phaseTrace,
                [],
                null);
        }

        var charters = discoveredDomains.Select(item => item.Charter).ToArray();
        var discoveredByDomain = discoveredDomains.ToDictionary(item => item.Charter.Id, StringComparer.Ordinal);
        var limits = BusinessOntologySemanticDomainWorkLimits.Default;
        var plan = BusinessOntologySemanticDomainWorkPlanner.Plan(charters, limits);
        var exploreEffects = new ExploreEffects(_investigation, Modeler);
        var explore = await _coordinator.ExecuteAsync(
            plan,
            limits,
            Effects(plan, completionCount: 1, investigationCount: 2),
            _exploreCache,
            exploreEffects,
            cancellationToken);
        phaseTrace.Add("explore:" + BatchPhase(explore));
        if (explore.Any(item => item.Status == "cancelled") || cancellationToken.IsCancellationRequested)
        {
            return NotPublished(BusinessOntologySemanticSynthesisStatuses.Cancelled, phaseTrace, DomainStatuses(explore));
        }

        var explored = explore
            .Where(item => item.Value is not null && item.Status is "completed" or "cached")
            .ToDictionary(item => item.Item.DomainId, item => item.Value!, StringComparer.Ordinal);
        if (explored.Count == 0)
        {
            phaseTrace.Add("synthesize:skipped");
            phaseTrace.Add("critic:skipped");
            phaseTrace.Add("publish:not_published");
            return new BusinessOntologySemanticSynthesisResult(
                BusinessOntologySemanticSynthesisStatuses.CompletedWithFailures,
                phaseTrace,
                DomainStatuses(explore),
                null);
        }

        var synthesisPlan = BusinessOntologySemanticDomainWorkPlanner.Plan(
            charters.Where(charter => explored.ContainsKey(charter.Id)).ToArray(),
            limits);
        var synthesize = await _coordinator.ExecuteAsync(
            synthesisPlan,
            limits,
            Effects(synthesisPlan, completionCount: 1, investigationCount: 0),
            _synthesizeCache,
            new SynthesizeEffects(Modeler, explored, discoveredByDomain),
            cancellationToken);
        var hasExploreFailure = explore.Any(item => item.Status == "failed");
        phaseTrace.Add("synthesize:" + (hasExploreFailure ? "completed_with_failures" : BatchPhase(synthesize)));
        if (synthesize.Any(item => item.Status == "cancelled") || cancellationToken.IsCancellationRequested)
        {
            return NotPublished(BusinessOntologySemanticSynthesisStatuses.Cancelled, phaseTrace, MergeDomainStatuses(explore, synthesize));
        }

        var staged = synthesize
            .Where(item => item.Value is not null && item.Status is "completed" or "cached")
            .Select(item => new
            {
                item.Item.DomainId,
                Draft = item.Value!.CompletionOutputs.Single().PendingDraft,
                Evidence = item.Value.CompletionOutputs.Single().RuntimeEvidence ?? [],
            })
            .Where(item => item.Draft is not null)
            .Select(item => new
            {
                item.DomainId,
                Draft = item.Draft!,
                item.Evidence,
                CriticDraft = ToCriticStagedDraft(item.Draft!),
            })
            .OrderBy(item => item.DomainId, StringComparer.Ordinal)
            .ToArray();
        if (staged.Length == 0)
        {
            phaseTrace.Add("critic:skipped");
            phaseTrace.Add("publish:not_published");
            return new BusinessOntologySemanticSynthesisResult(
                BusinessOntologySemanticSynthesisStatuses.CompletedWithFailures,
                phaseTrace,
                MergeDomainStatuses(explore, synthesize),
                null);
        }

        string criticDigest;
        BusinessOntologySemanticCriticRouting routing;
        try
        {
            var snapshot = BusinessOntologySemanticCriticVerdictRouter.CreateSnapshot(
                staged.Select(item => item.Draft).ToArray());
            var criticResponse = await Critic.Client.CompleteAsync(
                "You are the independent critic for a pending business-ontology semantic synthesis. "
                + "Return only the required closed JSON verdict document. Do not promote or mutate ontology data. "
                + "The supportRefs are opaque handles: do not invent sources or infer paths, symbols, or evidence identifiers.",
                JsonSerializer.Serialize(new
                {
                    phase = "critic",
                    schemaVersion = BusinessOntologySemanticCriticVerdictRouter.SchemaVersion,
                    snapshotDigest = snapshot.Digest,
                    modelerDependency = Modeler.Identity,
                    stagedDrafts = staged.Select(item => item.CriticDraft).ToArray(),
                    candidates = snapshot.Candidates.Select(candidate => new
                    {
                        candidate.Id,
                        candidate.Kind,
                        candidate.DomainId,
                        supportRefs = candidate.Supports.Select(support => support.Reference).ToArray(),
                    }).ToArray(),
                    requiredVerdict = new
                    {
                        rootProperties = new[] { "schemaVersion", "snapshotDigest", "verdicts" },
                        verdictProperties = new[] { "candidateId", "decision", "rationaleZh", "supportRefs", "targetCandidateId", "requestZh" },
                        decisions = BusinessOntologySemanticCriticDecisions.All.Order(StringComparer.Ordinal).ToArray(),
                        constraints = new[]
                        {
                            "return exactly one verdict for every candidate",
                            "every rationaleZh must be Chinese",
                            "supportRefs must be the supplied handles for that candidate",
                            "merge may target only a same-kind candidate that is kept",
                            "requestZh is required only for request_evidence",
                        },
                    },
                }),
                new LlmOptions(MaxTokens: 1024, Temperature: 0),
                cancellationToken);
            criticDigest = Digest("critic-response", criticResponse.Text);
            routing = BusinessOntologySemanticCriticVerdictRouter.ParseAndRoute(
                criticResponse.Text,
                snapshot,
                "critic:" + criticDigest,
                _sourceFingerprint,
                Digest("semantic-synthesis-input", plan.Items.Select(item => item.InputFingerprint).ToArray()));
            phaseTrace.Add("critic:completed");
        }
        catch (OperationCanceledException)
        {
            phaseTrace.Add("critic:cancelled");
            return NotPublished(BusinessOntologySemanticSynthesisStatuses.Cancelled, phaseTrace, MergeDomainStatuses(explore, synthesize));
        }
        catch
        {
            phaseTrace.Add("critic:failed");
            return NotPublished(BusinessOntologySemanticSynthesisStatuses.CompletedWithFailures, phaseTrace, MergeDomainStatuses(explore, synthesize));
        }

        phaseTrace.Add("publish:pending");
        var allCompleted = explore.All(item => item.Status is "completed" or "cached")
            && synthesize.All(item => item.Status is "completed" or "cached");
        var envelope = new BusinessOntologySemanticPendingEnvelope(
            staged.Length,
            staged.Length,
            AcceptedOntologyMutationCount: 0,
            InputDigest: Digest("semantic-synthesis-input", plan.Items.Select(item => item.InputFingerprint).ToArray()),
            CriticDigest: criticDigest,
            StagedDrafts: staged.Select(item => FilterPendingDraft(item.CriticDraft, routing)).ToArray(),
            CriticRouting: routing);
        var status = allCompleted ? BusinessOntologySemanticSynthesisStatuses.Completed : BusinessOntologySemanticSynthesisStatuses.CompletedWithFailures;
        var domainStatuses = MergeDomainStatuses(explore, synthesize);
        if (publicationObserver is not null)
        {
            await publicationObserver.PublishAsync(new BusinessOntologySemanticPendingPublication(
                status,
                phaseTrace.ToArray(),
                domainStatuses,
                envelope,
                staged.Select(item => item.Draft).ToArray(),
                MergeRuntimeEvidence(staged.SelectMany(item => item.Evidence)),
                PublicationProvenance(discoveredDomains, explore, synthesize, criticDigest, limits)), cancellationToken);
        }
        return new BusinessOntologySemanticSynthesisResult(
            status,
            phaseTrace,
            domainStatuses,
            envelope);
    }

    private async Task<IReadOnlyList<BusinessOntologySemanticDiscoveredDomain>> DiscoverAsync(
        IReadOnlyList<string> terms,
        CancellationToken cancellationToken)
    {
        var discovered = new List<(BusinessOntologyDiscoveredDomainCharter Charter, string QueryDigest)>();
        foreach (var term in terms)
        {
            var remaining = MaxDomainTerms - discovered.Count;
            if (remaining == 0)
            {
                break;
            }
            var page = await _investigation.DiscoverDomainChartersAsync(term, limit: remaining, cancellationToken: cancellationToken);
            discovered.AddRange(page.Items.Select(item => (item, page.QueryDigest)));
        }

        return discovered
            .Select(source => new
            {
                Charter = ToSemanticCharter(source.Charter),
                Evidence = RuntimeEvidence(source.Charter.EvidenceRefs, "domain-charter"),
                source.QueryDigest,
            })
            .Where(item => item.Charter is not null && item.Evidence.Count > 0)
            .GroupBy(item => item.Charter!.Id, StringComparer.Ordinal)
            .Select(group => new BusinessOntologySemanticDiscoveredDomain(
                new BusinessOntologySemanticDomainCharter(
                    group.Key,
                    group.First().Charter!.NameZh,
                    group.First().Charter!.DescriptionZh,
                    group.SelectMany(item => item.Charter!.EvidenceIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    group.SelectMany(item => item.Charter!.WorkflowNames).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
                MergeRuntimeEvidence(group.SelectMany(item => item.Evidence)),
                group.Select(item => item.QueryDigest).Where(digest => !string.IsNullOrWhiteSpace(digest))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))
            .OrderBy(item => item.Charter.Id, StringComparer.Ordinal)
            .Take(MaxDomainTerms)
            .ToArray();
    }

    private static BusinessOntologySemanticDomainCharter? ToSemanticCharter(BusinessOntologyDiscoveredDomainCharter source)
    {
        var domainId = ToPascalIdentifier(source.DomainSeed);
        var evidenceIds = source.EvidenceIds
            .Where(id => source.EvidenceRefs.Any(reference => reference.EvidenceId == id && IsUsableEvidenceReference(reference)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!BusinessOntologySemanticIdentifierGrammar.IsValidDomainId(domainId) || evidenceIds.Length == 0)
        {
            return null;
        }

        return new BusinessOntologySemanticDomainCharter(
            domainId,
            $"待归纳的{domainId}业务域",
            $"基于已索引入口和证据发现的{domainId}业务域，等待受控语义归纳。",
            evidenceIds,
            [source.Workflow.Action]);
    }

    private static IReadOnlyList<BusinessOntologySemanticEvidence> RuntimeEvidence(
        IEnumerable<BusinessOntologyInvestigationEvidenceRef> references,
        string sourceKind) =>
        MergeRuntimeEvidence(references
            .Where(IsUsableEvidenceReference)
            .Select(reference => new BusinessOntologySemanticEvidence(
                reference.EvidenceId,
                sourceKind,
                reference.Repository,
                reference.Path,
                string.IsNullOrWhiteSpace(reference.SymbolId) ? reference.Symbol : reference.SymbolId)));

    private static IReadOnlyList<BusinessOntologySemanticEvidence> RuntimeEvidence(
        IEnumerable<BusinessOntologySemanticPattern> patterns) =>
        MergeRuntimeEvidence(patterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern.ClaimId)
                && IndexedSemanticClaimKinds.Contains(pattern.ClaimKind))
            .SelectMany(pattern => pattern.EvidenceRefs
                .Where(reference => reference.EvidenceId == pattern.ClaimId && IsUsableEvidenceReference(reference))
                .Select(reference => new BusinessOntologySemanticEvidence(
                    reference.EvidenceId,
                    pattern.ClaimKind,
                    reference.Repository,
                    reference.Path,
                    string.IsNullOrWhiteSpace(reference.SymbolId) ? reference.Symbol : reference.SymbolId))));

    private static IReadOnlyList<BusinessOntologySemanticEvidence> MergeRuntimeEvidence(
        IEnumerable<BusinessOntologySemanticEvidence> evidence) =>
        evidence
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.SourceKind)
                && !string.IsNullOrWhiteSpace(item.Repository) && !string.IsNullOrWhiteSpace(item.RelativePath)
                && !string.IsNullOrWhiteSpace(item.SymbolId))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => IndexedSemanticClaimKinds.Contains(item.SourceKind) ? 0 : 1)
            .ThenBy(item => item.SourceKind, StringComparer.Ordinal)
            .ThenBy(item => item.Repository, StringComparer.Ordinal)
            .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.SymbolId, StringComparer.Ordinal)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

    private static bool IsUsableEvidenceReference(BusinessOntologyInvestigationEvidenceRef reference) =>
        !string.IsNullOrWhiteSpace(reference.EvidenceId) && !string.IsNullOrWhiteSpace(reference.Repository)
        && !string.IsNullOrWhiteSpace(reference.Path)
        && !string.IsNullOrWhiteSpace(string.IsNullOrWhiteSpace(reference.SymbolId) ? reference.Symbol : reference.SymbolId);

    private static BusinessOntologySemanticCriticStagedDraft ToCriticStagedDraft(BusinessOntologySemanticPendingDraft draft) =>
        new(
            draft.DomainId,
            draft.Clusters.Select(cluster => new BusinessOntologySemanticCriticStagedConcept(
                cluster.ConceptId, cluster.NameZh, cluster.DescriptionZh, cluster.ImplementationAnchors.Count,
                cluster.ImplementationAnchors.Select(anchor => anchor.Role).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())).ToArray(),
            draft.Attributes.Select(attribute => new BusinessOntologySemanticCriticStagedAttribute(
                attribute.Id, attribute.SubjectConceptId, attribute.Name, attribute.ValueType, attribute.DescriptionZh)).ToArray(),
            draft.Relations.Select(relation => new BusinessOntologySemanticCriticStagedRelation(
                relation.Id, relation.FromConceptId, relation.ToConceptId, relation.Name, relation.DescriptionZh,
                relation.EvidenceBindings.Select(binding => binding.BindingType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())).ToArray(),
            draft.Rules.Select(rule => new BusinessOntologySemanticCriticStagedRule(
                rule.Id, rule.SubjectConceptId, rule.DescriptionZh,
                rule.EvidenceBindings.Select(binding => binding.BindingType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())).ToArray(),
            draft.Lifecycles.Select(lifecycle => new BusinessOntologySemanticCriticStagedLifecycle(
                lifecycle.Id, lifecycle.SubjectConceptId, lifecycle.StateProperty, lifecycle.DescriptionZh,
                lifecycle.EvidenceBindings.Select(binding => binding.BindingType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())).ToArray());

    private static BusinessOntologySemanticCriticStagedDraft FilterPendingDraft(
        BusinessOntologySemanticCriticStagedDraft draft,
        BusinessOntologySemanticCriticRouting routing)
    {
        var kept = routing.PendingCandidateIds.ToHashSet(StringComparer.Ordinal);
        return draft with
        {
            Concepts = draft.Concepts.Where(item => kept.Contains(item.ConceptId)).ToArray(),
            Attributes = draft.Attributes.Where(item => kept.Contains(item.Id)).ToArray(),
            Relations = draft.Relations.Where(item => kept.Contains(item.Id)).ToArray(),
            Rules = draft.Rules.Where(item => kept.Contains(item.Id)).ToArray(),
            Lifecycles = draft.Lifecycles.Where(item => kept.Contains(item.Id)).ToArray(),
        };
    }

    private static IReadOnlyList<string> NormalizeTerms(IReadOnlyList<string>? requested)
    {
        var terms = (requested is { Count: > 0 } ? requested : DefaultAutoDiscoveryTerms)
            .Select(term => term?.Trim() ?? "")
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (terms.Length == 0 || terms.Length > MaxDomainTerms || terms.Any(term => term.Length > 96))
        {
            throw new ArgumentException($"domainTerms must contain 1 to {MaxDomainTerms} bounded literal terms.", nameof(requested));
        }
        return terms;
    }

    private static IReadOnlyDictionary<string, BusinessOntologySemanticDomainWorkEffectsPlan> Effects(
        BusinessOntologySemanticDomainWorkPlanner.DomainWorkPlan plan,
        int completionCount,
        int investigationCount) =>
        plan.Items.ToDictionary(
            item => item.DomainId,
            _ => new BusinessOntologySemanticDomainWorkEffectsPlan(completionCount, investigationCount),
            StringComparer.Ordinal);

    private static string BatchPhase(IReadOnlyList<BusinessOntologySemanticDomainWorkResult<BusinessOntologySemanticSynthesisStage>> results) =>
        results.All(item => item.Status is "completed" or "cached") ? "completed"
        : results.Any(item => item.Status == "cancelled") ? "cancelled"
        : "completed_with_failures";

    private static IReadOnlyList<BusinessOntologySemanticSynthesisDomainStatus> DomainStatuses(
        IReadOnlyList<BusinessOntologySemanticDomainWorkResult<BusinessOntologySemanticSynthesisStage>> results) =>
        results.Select(item => new BusinessOntologySemanticSynthesisDomainStatus(item.Item.DomainId, item.Status, item.FromCache)).ToArray();

    private static IReadOnlyList<BusinessOntologySemanticSynthesisDomainStatus> MergeDomainStatuses(
        IReadOnlyList<BusinessOntologySemanticDomainWorkResult<BusinessOntologySemanticSynthesisStage>> explore,
        IReadOnlyList<BusinessOntologySemanticDomainWorkResult<BusinessOntologySemanticSynthesisStage>> synthesize)
    {
        var merged = DomainStatuses(explore).ToDictionary(item => item.DomainId, StringComparer.Ordinal);
        foreach (var item in DomainStatuses(synthesize))
        {
            merged[item.DomainId] = item;
        }
        return merged.Values.OrderBy(item => item.DomainId, StringComparer.Ordinal).ToArray();
    }

    private static BusinessOntologySemanticPublicationProvenance PublicationProvenance(
        IReadOnlyList<BusinessOntologySemanticDiscoveredDomain> discoveredDomains,
        IReadOnlyList<BusinessOntologySemanticDomainWorkResult<BusinessOntologySemanticSynthesisStage>> explore,
        IReadOnlyList<BusinessOntologySemanticDomainWorkResult<BusinessOntologySemanticSynthesisStage>> synthesize,
        string criticDigest,
        BusinessOntologySemanticDomainWorkLimits limits)
    {
        var modelCalls = explore.SelectMany(item => item.Value?.CompletionOutputs ?? [])
            .Concat(synthesize.SelectMany(item => item.Value?.CompletionOutputs ?? []))
            .Where(output => output.Phase is "explore-plan" or "synthesis-draft")
            .Select(output => new BusinessOntologySemanticPublicationModelCall(output.Phase, output.Digest, 1024))
            .Append(new BusinessOntologySemanticPublicationModelCall("critic", criticDigest, 1024))
            .OrderBy(call => call.Phase, StringComparer.Ordinal)
            .ThenBy(call => call.ResponseDigest, StringComparer.Ordinal)
            .ToArray();
        var queryDigests = discoveredDomains.SelectMany(domain => domain.QueryDigests)
            .Concat(explore.SelectMany(item => item.Value?.InvestigationOutputs ?? [])
                .SelectMany(output => output.QueryDigests ?? []))
            .Where(digest => !string.IsNullOrWhiteSpace(digest))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new BusinessOntologySemanticPublicationProvenance(
            "business-ontology-semantic-provenance-v1",
            modelCalls,
            queryDigests,
            new BusinessOntologySemanticPublicationBudget(
                limits.MaxDomains,
                limits.MaxCompletionsPerDomain,
                limits.MaxInvestigationOperationsPerDomain));
    }

    private static BusinessOntologySemanticSynthesisResult NotPublished(
        string status,
        List<string> trace,
        IReadOnlyList<BusinessOntologySemanticSynthesisDomainStatus> domains)
    {
        if (trace.Count == 0)
        {
            trace.Add("discover:skipped");
        }
        foreach (var phase in new[] { "explore", "synthesize", "critic" })
        {
            if (!trace.Any(item => item.StartsWith(phase + ":", StringComparison.Ordinal)))
            {
                trace.Add(phase + ":skipped");
            }
        }
        trace.Add("publish:not_published");
        return new BusinessOntologySemanticSynthesisResult(status, trace, domains, null);
    }

    private static string ToPascalIdentifier(string value)
    {
        var parts = value.Split([ '.', '-', '_', ' ', '/' ], StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            var letters = new string(part.Where(char.IsLetterOrDigit).ToArray());
            if (letters.Length == 0)
            {
                continue;
            }
            builder.Append(char.ToUpperInvariant(letters[0]));
            if (letters.Length > 1)
            {
                builder.Append(letters[1..]);
            }
        }
        return builder.ToString();
    }

    private static string Digest(string scope, object value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { scope, value })))).ToLowerInvariant();

    private sealed class ExploreEffects(
        IBusinessOntologyInvestigationOperations investigation,
        BusinessOntologySemanticLlmDependency modeler)
        : IBusinessOntologySemanticDomainWorkEffectPort<BusinessOntologySemanticSynthesisStage>
    {
        public async Task<BusinessOntologySemanticSynthesisStage> CompleteAsync(
            BusinessOntologySemanticDomainWorkItem item,
            int ordinal,
            CancellationToken cancellationToken = default)
        {
            var response = await modeler.Client.CompleteAsync(
                "You are a bounded business-domain exploration planner. Use only the declared domain evidence; do not emit ontology mutations.",
                $"phase=explore;domain={item.DomainId};evidence={string.Join(',', item.EvidenceIds)};ordinal={ordinal}",
                new LlmOptions(MaxTokens: 1024, Temperature: 0),
                cancellationToken);
            return new BusinessOntologySemanticSynthesisStage("explore-plan", Digest("explore-plan", response.Text));
        }

        public async Task<BusinessOntologySemanticSynthesisStage> InvestigateAsync(
            BusinessOntologySemanticDomainWorkItem item,
            int ordinal,
            CancellationToken cancellationToken = default)
        {
            object response = ordinal switch
            {
                0 => await investigation.ListCrossLayerUseCasesAsync(domainSeed: item.DomainId, limit: 10, cancellationToken: cancellationToken),
                1 => await investigation.FindStateRuleClustersAsync(item.DomainId, limit: 10, cancellationToken: cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(ordinal)),
            };
            return new BusinessOntologySemanticSynthesisStage(
                "investigation",
                Digest("investigation", response),
                response switch
                {
                    BusinessOntologyInvestigationPage<BusinessOntologyCrossLayerUseCase> page =>
                        MergeRuntimeEvidence(page.Items.SelectMany(item => RuntimeEvidence(item.EvidenceRefs, "cross-layer-use-case"))
                            .Concat(page.Items.SelectMany(item => RuntimeEvidence(item.Claims)))),
                    BusinessOntologyInvestigationPage<BusinessOntologyStateRuleCluster> page =>
                        MergeRuntimeEvidence(page.Items.SelectMany(item => RuntimeEvidence(item.EvidenceRefs, "state-rule-cluster"))
                            .Concat(page.Items.SelectMany(item => RuntimeEvidence(
                                item.StateFields.Concat(item.StateValues).Concat(item.Transitions).Concat(item.Guards)
                                    .Concat(item.Validations).Concat(item.Persistences))))),
                    _ => [],
                },
                QueryDigests: response switch
                {
                    BusinessOntologyInvestigationPage<BusinessOntologyCrossLayerUseCase> page => [page.QueryDigest],
                    BusinessOntologyInvestigationPage<BusinessOntologyStateRuleCluster> page => [page.QueryDigest],
                    _ => [],
                });
        }
    }

    private sealed class SynthesizeEffects(
        BusinessOntologySemanticLlmDependency modeler,
        IReadOnlyDictionary<string, BusinessOntologySemanticDomainWorkOutput<BusinessOntologySemanticSynthesisStage>> explored,
        IReadOnlyDictionary<string, BusinessOntologySemanticDiscoveredDomain> discovered)
        : IBusinessOntologySemanticDomainWorkEffectPort<BusinessOntologySemanticSynthesisStage>
    {
        public async Task<BusinessOntologySemanticSynthesisStage> CompleteAsync(
            BusinessOntologySemanticDomainWorkItem item,
            int ordinal,
            CancellationToken cancellationToken = default)
        {
            if (!explored.TryGetValue(item.DomainId, out var stage))
            {
                throw new InvalidOperationException("Synthesis requires a completed staged exploration result.");
            }
            if (!discovered.TryGetValue(item.DomainId, out var domain))
            {
                throw new InvalidOperationException("Synthesis requires runtime-discovered domain evidence.");
            }
            var runtimeEvidence = MergeRuntimeEvidence(domain.RuntimeEvidence
                .Concat(stage.InvestigationOutputs.SelectMany(output => output.RuntimeEvidence ?? [])));
            if (runtimeEvidence.Count == 0)
            {
                throw new InvalidOperationException("Synthesis requires closed runtime-owned evidence.");
            }
            var response = await modeler.Client.CompleteAsync(
                "You are a bounded pending business-ontology synthesizer. Return only the required closed v1 JSON object. "
                + "Use only the supplied runtime evidence, do not invent evidence, do not promote or mutate ontology data.",
                JsonSerializer.Serialize(new
                {
                    phase = "synthesize",
                    domain = item.DomainId,
                    domainCharters = new[] { domain.Charter },
                    runtimeEvidence = runtimeEvidence.Select(evidence => new
                    {
                        evidence.Id,
                        evidence.SourceKind,
                        evidence.Repository,
                        evidence.RelativePath,
                        evidence.SymbolId,
                    }).ToArray(),
                    stagedExplorationDigests = stage.CompletionOutputs.Concat(stage.InvestigationOutputs).Select(output => output.Digest).Order(StringComparer.Ordinal).ToArray(),
                    requiredSchema = new
                    {
                        schemaVersion = BusinessOntologySemanticPendingDraftValidator.SchemaVersion,
                        rootProperties = new[] { "schemaVersion", "domainId", "domainCharters", "clusters", "attributes", "relations", "rules", "lifecycles" },
                        requiredCollections = new[] { "clusters(types)", "attributes", "relations", "rules", "lifecycles" },
                        constraints = new[]
                        {
                            "domainCharters must exactly echo the supplied charter",
                            "each type must have at least two distinct cross-role anchors matching runtime evidence",
                            "all descriptions and relation names must be Chinese",
                            "all evidence references must use supplied evidence ids",
                        },
                    },
                    ordinal,
                }),
                new LlmOptions(MaxTokens: 1024, Temperature: 0),
                cancellationToken);
            var draft = BusinessOntologySemanticPendingDraftValidator.ParseAndValidateForStaging(
                response.Text,
                runtimeEvidence,
                [domain.Charter]);
            return new BusinessOntologySemanticSynthesisStage(
                "synthesis-draft",
                Digest("synthesis-draft", draft),
                runtimeEvidence,
                draft);
        }

        public Task<BusinessOntologySemanticSynthesisStage> InvestigateAsync(
            BusinessOntologySemanticDomainWorkItem item,
            int ordinal,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The synthesis stage has no investigation effects.");
    }
}
