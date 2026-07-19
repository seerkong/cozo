using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cozo.DotNet.LlmWiki.LlmClient;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tools;

public static class BusinessOntologyCorroborationKinds
{
    public const string FrontendPage = "frontend-page";
    public const string FrontendForm = "frontend-form";
    public const string Documentation = "documentation";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        FrontendPage,
        FrontendForm,
        Documentation,
    };
}

public sealed record BusinessOntologyCorroborationObservation(
    string EvidenceId,
    string Repository,
    string Path,
    string Symbol,
    int StartLine,
    int EndLine,
    string SourceKind,
    string DomainToken,
    string AnchorKind,
    string AnchorValue,
    string Summary,
    string Resolver = "corroboration",
    double Confidence = 0.75);

public sealed record BusinessOntologySemanticProjectionRequest(
    string OntologyId,
    string GenerationId,
    string SourceFingerprint,
    string CreatedAt,
    string GeneratorVersion = "onto-semantic-projector/1",
    IReadOnlyList<BusinessOntologyCorroborationObservation>? Corroborations = null,
    string Mode = BusinessOntologySemanticProjectionModes.Deterministic,
    string? Experiment = null,
    int? MaxSlices = null);

public sealed record BusinessOntologySemanticProjectionResult(
    string OntologyId,
    string GenerationId,
    int RelationCandidates,
    int CrossLayerRelationCandidates,
    int DirectOnlyRelationDiagnostics,
    int RuleCandidates,
    int LifecycleCandidates,
    int Evidence,
    int ConflictDiagnostics,
    OntologySemanticExperimentRunSummary? Experiment = null);

/// <summary>
/// Projects direct CodeKnowledge source claims into reviewable business-ontology candidates.
/// It never initializes CodeKnowledge and never promotes a candidate into an ontology assertion.
/// </summary>
public sealed class BusinessOntologySemanticProjector(
    CozoOm om,
    BusinessOntologyStore store,
    OntologySemanticCandidateValidator? validator = null,
    ILlmClient? llmClient = null,
    SemanticEvidencePackBuilder? evidencePackBuilder = null)
{
    private const int CandidateRationaleMaxLength = 1_800;
    private const int SliceRationaleSampleLimit = 4;
    private const int WorkflowCarrierStructuralFanoutThreshold = 8;
    private static readonly IReadOnlySet<string> InfrastructureSpringRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        "adapter",
        "aspect",
        "component",
        "configuration",
        "consumer",
        "controller",
        "controlleradvice",
        "filter",
        "handler",
        "interceptor",
        "job",
        "listener",
        "mapper",
        "producer",
        "repository",
        "restcontroller",
        "scheduled",
        "scheduler",
        "service",
    };
    private static readonly string[] RelationMemberContainerSuffixes =
    [
        "Collection",
        "Array",
        "List",
        "Set",
    ];
    private static readonly string[] RelationMemberCarrierSuffixes =
    [
        "Entities",
        "Entity",
        "DTOs",
        "DTO",
        "VOs",
        "VO",
        "Models",
        "Model",
        "Records",
    ];
    private static readonly string[] ImplementationOwnerPathSegments =
    [
        "action",
        "controller",
        "handler",
        "job",
        "listener",
        "repository",
        "service",
    ];
    private static readonly string[] ImplementationOwnerFileSuffixes =
    [
        "Action",
        "ActionImpl",
        "Controller",
        "ControllerImpl",
        "Handler",
        "HandlerImpl",
        "Job",
        "JobImpl",
        "Listener",
        "ListenerImpl",
        "Repository",
        "RepositoryImpl",
        "Service",
        "ServiceImpl",
    ];

    private readonly OntologySemanticCandidateValidator candidateValidator =
        validator ?? new OntologySemanticCandidateValidator();
    private readonly SemanticEvidencePackBuilder semanticEvidencePackBuilder =
        evidencePackBuilder ?? new SemanticEvidencePackBuilder();

    public async Task<BusinessOntologySemanticProjectionResult> ProjectAsync(
        BusinessOntologySemanticProjectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!BusinessOntologySemanticProjectionModes.All.Contains(request.Mode))
        {
            throw new ArgumentException(
                $"Semantic projection mode must be '{BusinessOntologySemanticProjectionModes.Deterministic}' "
                + $"or '{BusinessOntologySemanticProjectionModes.Assisted}'.",
                nameof(request));
        }
        if (request.Mode == BusinessOntologySemanticProjectionModes.Assisted
            && (llmClient is null || !llmClient.IsAvailable))
        {
            throw new InvalidOperationException(
                "Assisted ontology projection requires an available explicitly injected ILlmClient: "
                + (llmClient?.UnavailableReason ?? "no client was injected"));
        }
        if (request.Experiment is not null
            && request.Mode != BusinessOntologySemanticProjectionModes.Assisted)
        {
            throw new ArgumentException("Semantic experiments require --mode assisted.", nameof(request));
        }
        if (request.Experiment is not null)
        {
            _ = OntologySemanticExperimentProfiles.Resolve(request.Experiment);
        }

        var preflight = await om.PreflightSemanticClaimsAsync(cancellationToken);
        if (!preflight.Ready)
        {
            throw new InvalidOperationException(preflight.Diagnostic);
        }

        var current = await store.ReadExportableAsync(request.OntologyId, cancellationToken);
        var claims = await ReadClaimsAsync(cancellationToken);
        var infrastructureRoleSymbols = await ReadInfrastructureRoleSymbolIdsAsync(cancellationToken);
        var symbolDescriptors = await ReadSymbolDescriptorsAsync(cancellationToken);
        var claimById = claims.ToDictionary(item => item.ClaimId, StringComparer.Ordinal);
        var useCaseSlices = await new BusinessUseCaseSliceBuilder(om, store)
            .BuildAsync(new BusinessUseCaseSliceBuildRequest(
                request.OntologyId,
                request.Corroborations),
                cancellationToken)
            .ConfigureAwait(false);
        var conceptIds = current.Concepts.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var conceptBySymbol = UniqueConceptMappings(current.Mappings, conceptIds);
        var directRequired = claims
            .Where(IsDirectRequired)
            .Select(claim => (Claim: claim, Payload: ParsePayload(claim.PayloadJson)))
            .Where(item => item.Payload is not null)
            .GroupBy(
                item => (
                    Owner: String(item.Payload!.Value, "ownerSymbolId"),
                    Member: NormalizeRelationMember(String(item.Payload.Value, "member"))),
                StringTupleComparer.Instance)
            .Where(group => group.Key.Member.Length > 0)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Claim).OrderBy(item => item.ClaimId, StringComparer.Ordinal).ToArray(),
                StringTupleComparer.Instance);

        var evidence = current.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var candidates = current.Candidates
            .Where(item => !item.Id.StartsWith("candidate:semantic:", StringComparison.Ordinal))
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var candidateClaims = new Dictionary<string, IReadOnlyCollection<SemanticClaimObservation>>(StringComparer.Ordinal);
        var ruleDiagnostics = new List<BusinessOntologyDiagnostic>();
        var relationDiagnostics = new List<BusinessOntologyDiagnostic>();
        var relationCount = 0;
        var ruleCount = 0;
        var lifecycleCount = 0;
        OntologySemanticExperimentRunSummary? experimentSummary = null;

        var suppressedWorkflowCarriers = FindSuppressedWorkflowCarriers(
            claims,
            symbolDescriptors,
            conceptBySymbol,
            infrastructureRoleSymbols,
            relationDiagnostics);
        var relationDrafts = new List<RelationCandidateDraft>();
        foreach (var claim in claims
            .Where(item => item.Kind == CodeSemanticClaimKinds.TypedReference)
            .OrderBy(item => item.ClaimId, StringComparer.Ordinal))
        {
            var payload = ParsePayload(claim.PayloadJson);
            if (payload is null)
            {
                continue;
            }
            if (!IsStructuralRelationTypedReference(payload.Value))
            {
                continue;
            }

            var ownerSymbolId = String(payload.Value, "ownerSymbolId");
            var targetSymbolId = String(payload.Value, "resolvedTypeSymbolId");
            var member = NormalizeRelationMember(String(payload.Value, "member"));
            if (member.Length == 0
                || suppressedWorkflowCarriers.Contains(ownerSymbolId)
                || infrastructureRoleSymbols.Contains(ownerSymbolId)
                || infrastructureRoleSymbols.Contains(targetSymbolId)
                || !conceptBySymbol.TryGetValue(ownerSymbolId, out var fromConceptId)
                || !conceptBySymbol.TryGetValue(targetSymbolId, out var toConceptId)
                || fromConceptId == toConceptId
                || IsOperationLikeRelationName(member))
            {
                continue;
            }

            var supportingClaims = new List<SemanticClaimObservation> { claim };
            if (directRequired.TryGetValue((ownerSymbolId, member), out var requiredClaims))
            {
                supportingClaims.AddRange(requiredClaims);
            }
            var slices = MatchingRelationSlices(
                fromConceptId,
                toConceptId,
                useCaseSlices.Slices);
            relationDrafts.Add(new RelationCandidateDraft(
                fromConceptId,
                toConceptId,
                member,
                Boolean(payload.Value, "collection"),
                supportingClaims,
                slices));
        }

        foreach (var group in relationDrafts
            .GroupBy(
                item => (item.FromConceptId, item.ToConceptId, item.Member),
                RelationTupleComparer.Instance)
            .OrderBy(item => item.Key.FromConceptId, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Member, StringComparer.Ordinal)
            .ThenBy(item => item.Key.ToConceptId, StringComparer.Ordinal))
        {
            var drafts = group.ToArray();
            var supportingClaims = drafts
                .SelectMany(item => item.SupportingClaims)
                .DistinctBy(item => item.ClaimId, StringComparer.Ordinal)
                .OrderBy(item => item.ClaimId, StringComparer.Ordinal)
                .ToArray();
            var matchingSlices = drafts
                .SelectMany(item => item.MatchingSlices)
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => item.First())
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            var evidenceIds = supportingClaims
                .Select(item => AddEvidence(item, evidence))
                .Concat(RelationMappingEvidenceIds(current, group.Key.FromConceptId, group.Key.ToConceptId))
                .Concat(matchingSlices.SelectMany(slice =>
                    AddUseCaseSliceEvidence(slice, claimById, request.Corroborations ?? [], evidence)))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var min = supportingClaims.Any(IsDirectRequired) ? "1" : "0";
            var max = drafts.Any(item => item.Collection) ? "many" : "1";
            var member = group.Key.Member;
            var fromConceptId = group.Key.FromConceptId;
            var toConceptId = group.Key.ToConceptId;
            var semantic = new
            {
                id = fromConceptId + "." + member,
                fromConceptId,
                toConceptId,
                name = member,
                min,
                max,
                descriptionZh = $"{Label(current, fromConceptId)}关联的{Label(current, toConceptId)}。",
            };
            var hasSlice = matchingSlices.Length > 0;
            var validated = ValidateCandidate(
                OntologySemanticCandidateKinds.Relation,
                semantic,
                evidenceIds,
                RelationRationale(min, hasSlice, matchingSlices),
                conceptIds,
                evidence.Keys);
            candidates[validated.Id] = ToStoredCandidate(
                validated,
                supportingClaims,
                hasSlice ? null : 0.6);
            candidateClaims[validated.Id] = supportingClaims;
            relationCount++;
        }

        foreach (var claim in claims
            .Where(item => item.Kind == CodeSemanticClaimKinds.BusinessGuard)
            .OrderBy(item => item.ClaimId, StringComparer.Ordinal))
        {
            var payload = ParsePayload(claim.PayloadJson);
            if (payload is null)
            {
                continue;
            }

            var matchingSlices = useCaseSlices.Slices
                .Where(slice => slice.ClaimIds.Contains(claim.ClaimId, StringComparer.Ordinal))
                .OrderBy(slice => slice.Id, StringComparer.Ordinal)
                .ToArray();
            if (matchingSlices.Length == 0)
            {
                continue;
            }

            var subject = ResolveBusinessRuleSubject(
                claim,
                payload.Value,
                matchingSlices,
                claims,
                conceptBySymbol);
            if (subject.Ambiguous)
            {
                ruleDiagnostics.Add(AmbiguousRuleSubjectDiagnostic(claim, payload.Value, subject.CandidateConceptIds));
                continue;
            }
            if (subject.ConceptId.Length == 0)
            {
                continue;
            }

            var sliceClaimIds = matchingSlices
                .SelectMany(slice => slice.ClaimIds)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            var supportingClaims = claims
                .Where(item => item.ClaimId == claim.ClaimId || sliceClaimIds.Contains(item.ClaimId))
                .DistinctBy(item => item.ClaimId, StringComparer.Ordinal)
                .OrderBy(item => item.ClaimId, StringComparer.Ordinal)
                .ToArray();
            var evidenceIds = new[] { AddEvidence(claim, evidence) }
                .Concat(matchingSlices.SelectMany(slice =>
                    AddUseCaseSliceEvidence(slice, claimById, request.Corroborations ?? [], evidence)))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var ruleName = BusinessRuleName(payload.Value);
            var predicateSource = String(payload.Value, "predicateSource");
            var effectMessage = String(payload.Value, "effectMessage");
            var effectKind = String(payload.Value, "effectKind");
            var effectSource = String(payload.Value, "effectSource");
            var method = String(payload.Value, "method");
            var methodSymbolId = String(payload.Value, "methodSymbolId");
            var semantic = new
            {
                id = subject.ConceptId + "." + ruleName,
                subjectConceptId = subject.ConceptId,
                ruleKind = "businessCondition",
                descriptionZh = effectMessage.Length > 0
                    ? $"{Label(current, subject.ConceptId)}在条件“{predicateSource}”成立时被拒绝：{effectMessage}。"
                    : $"{Label(current, subject.ConceptId)}在条件“{predicateSource}”成立时被拒绝。",
                predicate = new
                {
                    source = predicateSource,
                    method,
                    methodSymbolId,
                },
                effect = new
                {
                    type = "reject",
                    mechanism = effectKind,
                    messageZh = effectMessage,
                    source = effectSource,
                    allowedWhen = new
                    {
                        not = predicateSource,
                    },
                },
            };
            var validated = ValidateCandidate(
                OntologySemanticCandidateKinds.Rule,
                semantic,
                evidenceIds,
                RuleRationale(matchingSlices),
                conceptIds,
                evidence.Keys);
            candidates[validated.Id] = ToStoredCandidate(validated, supportingClaims);
            candidateClaims[validated.Id] = supportingClaims;
            ruleCount++;
        }

        var stateValuesByEnum = claims
            .Where(item => item.Kind == CodeSemanticClaimKinds.StateValue)
            .Select(claim => (Claim: claim, Payload: ParsePayload(claim.PayloadJson)))
            .Where(item => item.Payload is not null)
            .GroupBy(item => String(item.Payload!.Value, "enumTypeSymbolId"), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(item => new StateValueDraft(
                        item.Claim,
                        String(item.Payload!.Value, "value")))
                    .Where(item => item.Value.Length > 0)
                    .GroupBy(item => item.Value, StringComparer.Ordinal)
                    .Select(item => item
                        .OrderBy(value => value.Claim.StartLine)
                        .ThenBy(value => value.Claim.ClaimId, StringComparer.Ordinal)
                        .First())
                    .OrderBy(item => item.Claim.StartLine)
                    .ThenBy(item => item.Claim.ClaimId, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var assignments = claims
            .Where(item => item.Kind == CodeSemanticClaimKinds.StateAssignment)
            .Select(claim => (Claim: claim, Payload: ParsePayload(claim.PayloadJson)))
            .Where(item => item.Payload is not null)
            .ToArray();
        var stateGuards = claims
            .Where(item => item.Kind == CodeSemanticClaimKinds.BusinessGuard)
            .Select(claim => CreateStateGuardDraft(claim))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        void StoreLifecycleCandidate(
            LifecycleFieldDraft field,
            IReadOnlyList<string> orderedStates,
            IReadOnlyList<SemanticClaimObservation> fieldClaims,
            IReadOnlyList<SemanticClaimObservation> declaredStateClaims,
            IReadOnlyList<TransitionDraft> transitionDrafts,
            string rationale)
        {
            var supportingClaims = fieldClaims
                .Concat(declaredStateClaims)
                .Concat(transitionDrafts.SelectMany(item => new[] { item.MutationClaim, item.GuardClaim }))
                .DistinctBy(item => item.ClaimId, StringComparer.Ordinal)
                .OrderBy(item => item.ClaimId, StringComparer.Ordinal)
                .ToArray();
            var evidenceIds = supportingClaims
                .Select(item => AddEvidence(item, evidence))
                .Concat(transitionDrafts.SelectMany(item => item.MatchingSlices.SelectMany(slice =>
                    AddLifecycleUseCaseSliceEvidence(slice, item, claimById, evidence))))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var lifecycleId = field.ConceptId + "." + field.StateProperty + "Lifecycle";
            var semantic = new
            {
                id = lifecycleId,
                subjectConceptId = field.ConceptId,
                stateProperty = field.StateProperty,
                initialState = orderedStates[0],
                descriptionZh = $"{Label(current, field.ConceptId)}的{field.StateProperty}生命周期。",
                states = orderedStates.Select(state => new
                {
                    id = state,
                    terminal = false,
                    descriptionZh = $"{Label(current, field.ConceptId)}处于 {state} 状态。",
                }).ToArray(),
                transitions = transitionDrafts.Select(item => new
                {
                    id = lifecycleId + "." + item.Action
                        + PascalToken(item.FromState) + "To" + PascalToken(item.ToState),
                    action = item.Action,
                    fromState = item.FromState,
                    toState = item.ToState,
                    descriptionZh =
                        $"{Label(current, field.ConceptId)}执行“{item.Method}”时，"
                        + $"{field.StateProperty} 从 {item.FromState} 变为 {item.ToState}。"
                        + (item.EffectMessage.Length > 0 ? item.EffectMessage : ""),
                    guard = new
                    {
                        source = item.PredicateSource,
                        method = item.Method,
                        methodSymbolId = item.MethodSymbolId,
                        allowedValues = new[] { item.FromState },
                    },
                    effect = new
                    {
                        set = new
                        {
                            property = field.StateProperty,
                            value = item.ToState,
                            valueEncoding = item.ValueEncoding,
                            rawValue = item.RawValue,
                        },
                        mechanism = item.EffectKind,
                        messageZh = item.EffectMessage,
                    },
                }).ToArray(),
            };
            var validated = ValidateCandidate(
                OntologySemanticCandidateKinds.Lifecycle,
                semantic,
                evidenceIds,
                rationale,
                conceptIds,
                evidence.Keys);
            candidates[validated.Id] = ToStoredCandidate(validated, supportingClaims);
            candidateClaims[validated.Id] = supportingClaims;
            lifecycleCount++;
        }

        var lifecycleFields = claims
            .Where(item => item.Kind == CodeSemanticClaimKinds.StateField)
            .Select(claim => CreateLifecycleField(claim, conceptBySymbol))
            .Where(item => item is not null)
            .Select(item => item!)
            .GroupBy(
                item => (item.OwnerSymbolId, item.StateProperty, item.EnumTypeSymbolId),
                StateFieldTupleComparer.Instance)
            .OrderBy(group => group.Key.OwnerSymbolId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.StateProperty, StringComparer.Ordinal)
            .ThenBy(group => group.Key.EnumTypeSymbolId, StringComparer.Ordinal)
            .ToArray();
        foreach (var fieldGroup in lifecycleFields)
        {
            var field = fieldGroup.First();
            if (!stateValuesByEnum.TryGetValue(field.EnumTypeSymbolId, out var stateValueDrafts)
                || stateValueDrafts.Length < 2)
            {
                continue;
            }

            var stateNames = stateValueDrafts.Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
            var transitionDrafts = assignments
                .Select(item => CreateTransitionDraft(
                    item.Claim,
                    item.Payload!.Value,
                    field,
                    stateNames,
                    stateGuards,
                    useCaseSlices.Slices))
                .Where(item => item is not null)
                .Select(item => item!)
                .GroupBy(
                    item => (item.Action, item.FromState, item.ToState),
                    TransitionTupleComparer.Instance)
                .Select(group => group
                    .OrderBy(item => item.MutationClaim.StartLine)
                    .ThenBy(item => item.MutationClaim.ClaimId, StringComparer.Ordinal)
                    .First())
                .OrderBy(item => item.Action, StringComparer.Ordinal)
                .ThenBy(item => item.FromState, StringComparer.Ordinal)
                .ThenBy(item => item.ToState, StringComparer.Ordinal)
                .ToArray();
            var rationale = transitionDrafts.Length == 0
                ? "直接状态字段和枚举声明支持有限状态集合；未发现同一跨文件 use-case slice 中由 reject guard 约束的状态写入，因此不提出状态迁移。"
                : "直接状态字段和枚举声明支持有限状态集合；同一跨文件 use-case slice 中的 reject guard 与状态写入共同支持状态迁移。";
            StoreLifecycleCandidate(
                field,
                stateValueDrafts.Select(item => item.Value).ToArray(),
                fieldGroup.Select(item => item.Claim).ToArray(),
                stateValueDrafts.Select(item => item.Claim).ToArray(),
                transitionDrafts,
                rationale);
        }

        var declaredLifecycleFields = lifecycleFields
            .Select(group => (Owner: group.Key.OwnerSymbolId, Member: group.Key.StateProperty))
            .ToHashSet(StringTupleComparer.Instance);
        var scalarAssignmentGroups = assignments
            .Select(item => new
            {
                item.Claim,
                Payload = item.Payload!.Value,
                OwnerSymbolId = String(item.Payload.Value, "ownerSymbolId"),
                StateProperty = StateProperty(item.Payload.Value),
                ValueEncoding = String(item.Payload.Value, "valueEncoding"),
            })
            .Where(item => conceptBySymbol.ContainsKey(item.OwnerSymbolId)
                && !declaredLifecycleFields.Contains((item.OwnerSymbolId, item.StateProperty))
                && IsStateLikeProperty(item.StateProperty)
                && IsScalarStateEncoding(item.ValueEncoding))
            .GroupBy(
                item => (Owner: item.OwnerSymbolId, Member: item.StateProperty),
                StringTupleComparer.Instance)
            .OrderBy(group => group.Key.Owner, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Member, StringComparer.Ordinal);
        foreach (var assignmentGroup in scalarAssignmentGroups)
        {
            var scalarAssignments = assignmentGroup
                .OrderBy(item => item.Claim.StartLine)
                .ThenBy(item => item.Claim.ClaimId, StringComparer.Ordinal)
                .ToArray();

            var candidateStates = scalarAssignments
                .SelectMany(item => new[]
                {
                    String(item.Payload, "fromValue"),
                    String(item.Payload, "toValue"),
                })
                .Where(item => item.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            if (candidateStates.Count < 2)
            {
                continue;
            }

            var first = scalarAssignments[0];
            var field = new LifecycleFieldDraft(
                first.Claim,
                conceptBySymbol[assignmentGroup.Key.Owner],
                assignmentGroup.Key.Owner,
                assignmentGroup.Key.Member,
                "",
                "");
            var transitionDrafts = scalarAssignments
                .Select(item => CreateTransitionDraft(
                    item.Claim,
                    item.Payload,
                    field,
                    candidateStates,
                    stateGuards,
                    useCaseSlices.Slices))
                .Where(item => item is not null)
                .Select(item => item!)
                .GroupBy(
                    item => (item.Action, item.FromState, item.ToState),
                    TransitionTupleComparer.Instance)
                .Select(group => group
                    .OrderBy(item => item.MutationClaim.StartLine)
                    .ThenBy(item => item.MutationClaim.ClaimId, StringComparer.Ordinal)
                    .First())
                .OrderBy(item => item.MutationClaim.StartLine)
                .ThenBy(item => item.MutationClaim.ClaimId, StringComparer.Ordinal)
                .ToArray();
            if (transitionDrafts.Length == 0)
            {
                continue;
            }

            var observedStates = transitionDrafts
                .SelectMany(item => new[] { item.FromState, item.ToState })
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (observedStates.Length < 2)
            {
                continue;
            }

            StoreLifecycleCandidate(
                field,
                observedStates,
                [],
                [],
                transitionDrafts,
                "同一跨文件 use-case slice 中的 reject guard 与 string/numeric 状态写入，"
                + "共同支持由直接观察值构造的有限生命周期。");
        }

        if (request.Mode == BusinessOntologySemanticProjectionModes.Assisted
            && request.Experiment is null)
        {
            var pack = await semanticEvidencePackBuilder.BuildEvidencePackAsync(
                claims
                    .Where(IsAssistedProposalClaim)
                    .Select(claim => new SemanticEvidenceAnchorSource(
                        claim.ClaimId,
                        claim.Repository,
                        claim.RepositoryRoot,
                        claim.Path,
                        claim.SubjectId,
                        claim.Kind,
                        claim.PayloadJson,
                        claim.StartLine,
                        claim.EndLine,
                        conceptIds.Order(StringComparer.Ordinal).ToArray())),
                cancellationToken);
            var assistedEvidence = pack.Anchors
                .Select(anchor => anchor.EvidenceId)
                .ToArray();
            foreach (var claim in claims.Where(claim =>
                assistedEvidence.Contains(claim.ClaimId, StringComparer.Ordinal)))
            {
                AddEvidence(claim, evidence);
            }
            var assisted = await new OntologySemanticAssistedProposalClient(llmClient!, candidateValidator)
                .ProposeAsync(pack, conceptIds, assistedEvidence, cancellationToken);
            var preparedAssisted = assisted.Select(validated =>
            {
                var supportingClaims = claims
                    .Where(claim => validated.EvidenceIds.Contains(
                        claim.ClaimId,
                        StringComparer.Ordinal))
                    .ToArray();
                return new
                {
                    Validated = validated,
                    Stored = ToStoredCandidate(validated, supportingClaims),
                };
            }).ToArray();
            foreach (var prepared in preparedAssisted)
            {
                if (candidates.ContainsKey(prepared.Validated.Id))
                {
                    continue;
                }
                candidates.Add(prepared.Validated.Id, prepared.Stored);
                switch (prepared.Validated.Kind)
                {
                    case OntologySemanticCandidateKinds.Relation:
                        relationCount++;
                        break;
                    case OntologySemanticCandidateKinds.Rule:
                        ruleCount++;
                        break;
                    case OntologySemanticCandidateKinds.Lifecycle:
                        lifecycleCount++;
                        break;
                }
            }
        }
        else if (request.Experiment is not null)
        {
            var experiment = await RunExperimentAsync(
                request,
                useCaseSlices.Slices,
                claims,
                claimById,
                conceptIds,
                evidence,
                candidates,
                cancellationToken);
            relationCount += experiment.Relations;
            ruleCount += experiment.Rules;
            lifecycleCount += experiment.Lifecycles;
            experimentSummary = experiment.Summary;
        }

        ApplyCorroborations(
            request.Corroborations ?? [],
            current,
            claims,
            evidence,
            candidates,
            candidateClaims,
            conceptIds);
        var conflictDiagnostics = DetectConflicts(candidates.Values);
        var directOnlyDiagnostics = DetectDirectOnlyRelationDiagnostics(candidates.Values);
        var diagnostics = current.Diagnostics
            .Where(item => !item.Id.StartsWith("diagnostic:semantic-conflict:", StringComparison.Ordinal))
            .Where(item => !item.Id.StartsWith("diagnostic:semantic-direct-only:", StringComparison.Ordinal))
            .Where(item => !item.Id.StartsWith("diagnostic:semantic-rule-subject:", StringComparison.Ordinal))
            .Where(item => !item.Id.StartsWith("diagnostic:semantic-workflow-carrier:", StringComparison.Ordinal))
            .Concat(conflictDiagnostics)
            .Concat(directOnlyDiagnostics)
            .Concat(relationDiagnostics)
            .Concat(ruleDiagnostics)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        var next = new BusinessOntologyGenerationInput(
            request.OntologyId,
            request.GenerationId,
            request.SourceFingerprint,
            request.GeneratorVersion,
            request.CreatedAt,
            current.Concepts,
            current.Attributes,
            current.Relations,
            current.Rules,
            current.Lifecycles,
            current.States,
            current.Transitions,
            current.Mappings,
            evidence.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            candidates.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            current.Reviews,
            diagnostics);
        await store.ReplaceGenerationAsync(next, cancellationToken);

        var crossLayerRelationCount = candidates.Values.Count(candidate =>
            candidate.SubjectKind == OntologySemanticCandidateKinds.Relation
            && candidate.EvidenceIds.Any(id =>
                evidence.TryGetValue(id, out var item)
                && (item.SourceKind.Contains("use-case", StringComparison.OrdinalIgnoreCase)
                    || item.SourceKind.Contains("usecase", StringComparison.OrdinalIgnoreCase))));
        return new BusinessOntologySemanticProjectionResult(
            request.OntologyId,
            request.GenerationId,
            relationCount,
            crossLayerRelationCount,
            directOnlyDiagnostics.Count,
            ruleCount,
            lifecycleCount,
            evidence.Count - current.Evidence.Count,
            conflictDiagnostics.Count,
            experimentSummary);
    }

    private async Task<ExperimentProjectionResult> RunExperimentAsync(
        BusinessOntologySemanticProjectionRequest request,
        IReadOnlyList<BusinessUseCaseSlice> slices,
        IReadOnlyList<SemanticClaimObservation> claims,
        IReadOnlyDictionary<string, SemanticClaimObservation> claimById,
        IReadOnlySet<string> conceptIds,
        IDictionary<string, BusinessOntologyEvidence> evidence,
        IDictionary<string, BusinessOntologyCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var profile = OntologySemanticExperimentProfiles.Resolve(request.Experiment!);
        var budget = OntologySemanticExperimentBudget.Create(profile, request.MaxSlices);
        var eligible = slices
            .Where(slice => IsExperimentEligibleSlice(slice, claimById))
            .OrderByDescending(slice => SlicePriority(slice, claimById))
            .ThenBy(slice => slice.Id, StringComparer.Ordinal)
            .Take(budget.MaxSlices)
            .ToArray();
        if (eligible.Length == 0)
        {
            throw new InvalidOperationException("Semantic experiment found no eligible business use-case slices.");
        }

        var proposalClient = new OntologySemanticAssistedProposalClient(llmClient!, candidateValidator);
        var criticClient = profile.UsesCritic ? new OntologySemanticCriticClient(llmClient!) : null;
        var calls = 0;
        var proposed = 0;
        var retained = 0;
        var dropped = 0;
        var relationCount = 0;
        var ruleCount = 0;
        var lifecycleCount = 0;
        var packDigests = new List<string>();

        foreach (var slice in eligible)
        {
            if (calls >= budget.MaxCompletions)
            {
                throw new InvalidOperationException(
                    $"Semantic experiment '{profile.Id}' exhausted its completion budget before slice '{slice.Id}'.");
            }
            var sliceClaims = slice.ClaimIds
                .Where(claimById.ContainsKey)
                .Select(id => claimById[id])
                .Where(IsAssistedProposalClaim)
                .OrderBy(item => item.ClaimId, StringComparer.Ordinal)
                .ToArray();
            if (sliceClaims.Length == 0)
            {
                continue;
            }
            var sliceConceptIds = slice.ConceptIds
                .Where(conceptIds.Contains)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (sliceConceptIds.Length == 0)
            {
                continue;
            }
            var pack = await semanticEvidencePackBuilder.BuildEvidencePackAsync(
                sliceClaims.Select(claim => new SemanticEvidenceAnchorSource(
                    claim.ClaimId,
                    claim.Repository,
                    claim.RepositoryRoot,
                    claim.Path,
                    claim.SubjectId,
                    claim.Kind,
                    claim.PayloadJson,
                    claim.StartLine,
                    claim.EndLine,
                    sliceConceptIds)),
                cancellationToken);
            var availableEvidence = pack.Anchors.Select(anchor => anchor.EvidenceId).ToArray();
            foreach (var claim in sliceClaims.Where(claim => availableEvidence.Contains(claim.ClaimId, StringComparer.Ordinal)))
            {
                AddEvidence(claim, evidence);
            }
            packDigests.Add(Sha256(pack.ToCanonicalJson()));
            var proposals = await proposalClient.ProposeAsync(
                pack,
                sliceConceptIds,
                availableEvidence,
                cancellationToken);
            calls++;
            proposed += proposals.Count;

            var kept = proposals;
            if (criticClient is not null && proposals.Count > 0)
            {
                if (calls >= budget.MaxCompletions)
                {
                    throw new InvalidOperationException(
                        $"Semantic experiment '{profile.Id}' exhausted its completion budget before critic slice '{slice.Id}'.");
                }
                var decisions = await criticClient.ReviewAsync(pack, proposals, cancellationToken);
                calls++;
                var keptIds = decisions
                    .Where(decision => decision.Decision == "keep")
                    .Select(decision => decision.CandidateId)
                    .ToHashSet(StringComparer.Ordinal);
                dropped += decisions.Count - keptIds.Count;
                kept = proposals.Where(candidate => keptIds.Contains(candidate.Id)).ToArray();
            }

            foreach (var candidate in kept)
            {
                if (candidates.ContainsKey(candidate.Id))
                {
                    continue;
                }
                var supportingClaims = claims
                    .Where(claim => candidate.EvidenceIds.Contains(claim.ClaimId, StringComparer.Ordinal))
                    .ToArray();
                candidates.Add(candidate.Id, ToStoredCandidate(candidate, supportingClaims));
                retained++;
                switch (candidate.Kind)
                {
                    case OntologySemanticCandidateKinds.Relation:
                        relationCount++;
                        break;
                    case OntologySemanticCandidateKinds.Rule:
                        ruleCount++;
                        break;
                    case OntologySemanticCandidateKinds.Lifecycle:
                        lifecycleCount++;
                        break;
                }
            }
        }

        return new ExperimentProjectionResult(
            relationCount,
            ruleCount,
            lifecycleCount,
            new OntologySemanticExperimentRunSummary(
                profile.Id,
                eligible.Length,
                calls,
                CacheHits: 0,
                proposed,
                retained,
                dropped,
                eligible.Select(slice => slice.Id).ToArray(),
                packDigests.Order(StringComparer.Ordinal).ToArray()));
    }

    private static int SlicePriority(
        BusinessUseCaseSlice slice,
        IReadOnlyDictionary<string, SemanticClaimObservation> claims) =>
        slice.ClaimIds.Count(id => claims.TryGetValue(id, out var claim)
            && claim.Kind == CodeSemanticClaimKinds.TypedReference) * 100
        + slice.ClaimIds.Count(id => claims.TryGetValue(id, out var claim)
            && claim.Kind is CodeSemanticClaimKinds.BusinessGuard or CodeSemanticClaimKinds.StateAssignment) * 10
        + slice.ConceptIds.Count * 2
        + slice.Roles.Count;

    private static bool IsExperimentEligibleSlice(
        BusinessUseCaseSlice slice,
        IReadOnlyDictionary<string, SemanticClaimObservation> claims) =>
        slice.ConceptIds.Count >= 2
        && slice.ClaimIds.Any(id => claims.TryGetValue(id, out var claim)
            && claim.Kind == CodeSemanticClaimKinds.TypedReference);

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ExperimentProjectionResult(
        int Relations,
        int Rules,
        int Lifecycles,
        OntologySemanticExperimentRunSummary Summary);

    private void ApplyCorroborations(
        IReadOnlyList<BusinessOntologyCorroborationObservation> observations,
        BusinessOntologySnapshot snapshot,
        IReadOnlyList<SemanticClaimObservation> claims,
        IDictionary<string, BusinessOntologyEvidence> evidence,
        IDictionary<string, BusinessOntologyCandidate> candidates,
        IReadOnlyDictionary<string, IReadOnlyCollection<SemanticClaimObservation>> candidateClaims,
        IReadOnlySet<string> conceptIds)
    {
        if (observations.Count == 0 || candidateClaims.Count == 0)
        {
            return;
        }

        var routeAnchors = RouteAnchorTokens(claims);
        foreach (var pair in candidateClaims.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray())
        {
            var candidate = candidates[pair.Key];
            if (candidate.SubjectKind == OntologySemanticCandidateKinds.Relation
                && candidate.Reason.Contains("direct-only diagnostic", StringComparison.Ordinal))
            {
                continue;
            }
            var domainTokens = CandidateDomainTokens(candidate, snapshot);
            var typeAnchors = CandidateTypeAnchors(candidate, pair.Value, snapshot);
            var matched = observations
                .Where(item => IsMatchingCorroboration(item, domainTokens, typeAnchors, routeAnchors))
                .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
                .ToArray();
            if (matched.Length == 0)
            {
                continue;
            }

            var addedIds = new List<string>();
            foreach (var observation in matched)
            {
                var item = ToCorroborationEvidence(observation);
                if (evidence.TryGetValue(item.Id, out var existing) && existing != item)
                {
                    throw new ArgumentException(
                        $"Corroboration evidence identity '{item.Id}' has conflicting content.",
                        nameof(observations));
                }
                evidence[item.Id] = item;
                addedIds.Add(item.Id);
            }

            using var document = JsonDocument.Parse(candidate.PayloadJson);
            var root = document.RootElement;
            var evidenceIds = candidate.EvidenceIds.Concat(addedIds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var enrichedPayload = JsonSerializer.Serialize(new
            {
                schemaVersion = String(root, "schemaVersion"),
                kind = String(root, "kind"),
                semantic = root.GetProperty("semantic").Clone(),
                evidenceIds,
                basis = String(root, "basis"),
                rationale = String(root, "rationale"),
            });
            var validated = candidateValidator.Validate(enrichedPayload, conceptIds, evidence.Keys);
            candidates.Remove(pair.Key);
            candidates[validated.Id] = new BusinessOntologyCandidate(
                validated.Id,
                validated.Kind,
                validated.SemanticId,
                validated.CanonicalPayloadJson,
                validated.Rationale,
                candidate.Confidence,
                "pending",
                validated.EvidenceIds);
        }
    }

    private static IReadOnlyList<BusinessUseCaseSlice> MatchingRelationSlices(
        string fromConceptId,
        string toConceptId,
        IReadOnlyList<BusinessUseCaseSlice> slices) =>
        slices
            .Where(slice => slice.ConceptIds.Contains(fromConceptId, StringComparer.Ordinal)
                && slice.ConceptIds.Contains(toConceptId, StringComparer.Ordinal))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> AddUseCaseSliceEvidence(
        BusinessUseCaseSlice slice,
        IReadOnlyDictionary<string, SemanticClaimObservation> claimById,
        IReadOnlyList<BusinessOntologyCorroborationObservation> corroborations,
        IDictionary<string, BusinessOntologyEvidence> evidence)
    {
        var ids = new List<string>
        {
            AddUseCaseSliceAnchorEvidence(slice, evidence),
        };
        var corroborationById = corroborations.ToDictionary(item => item.EvidenceId, StringComparer.Ordinal);
        foreach (var id in slice.EvidenceIds.Order(StringComparer.Ordinal))
        {
            if (claimById.TryGetValue(id, out var claim))
            {
                ids.Add(AddEvidence(claim, evidence));
                continue;
            }
            if (corroborationById.TryGetValue(id, out var corroboration))
            {
                var item = ToCorroborationEvidence(corroboration);
                if (evidence.TryGetValue(item.Id, out var existing) && existing != item)
                {
                    throw new ArgumentException(
                        $"Corroboration evidence identity '{item.Id}' has conflicting content.",
                        nameof(corroborations));
                }
                evidence[item.Id] = item;
                ids.Add(item.Id);
                continue;
            }
            if (evidence.ContainsKey(id))
            {
                ids.Add(id);
            }
        }
        return ids
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> AddLifecycleUseCaseSliceEvidence(
        BusinessUseCaseSlice slice,
        TransitionDraft transition,
        IReadOnlyDictionary<string, SemanticClaimObservation> claimById,
        IDictionary<string, BusinessOntologyEvidence> evidence)
    {
        var ids = new List<string>
        {
            AddUseCaseSliceAnchorEvidence(slice, evidence),
        };
        foreach (var id in slice.ClaimIds.Order(StringComparer.Ordinal))
        {
            if (!claimById.TryGetValue(id, out var claim))
            {
                continue;
            }
            if (id == transition.MutationClaim.ClaimId
                || id == transition.GuardClaim.ClaimId
                || claim.Kind == CodeSemanticClaimKinds.TypedReference
                    && TypedReferenceBelongsToMethod(claim, transition.MethodSymbolId))
            {
                ids.Add(AddEvidence(claim, evidence));
            }
        }
        return ids
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> RelationMappingEvidenceIds(
        BusinessOntologySnapshot snapshot,
        string fromConceptId,
        string toConceptId)
    {
        foreach (var mapping in snapshot.Mappings
            .Where(item => item.SubjectKind == "concept"
                && (item.SubjectId == fromConceptId || item.SubjectId == toConceptId))
            .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            foreach (var evidenceId in mapping.EvidenceIds.Order(StringComparer.Ordinal))
            {
                yield return evidenceId;
            }
        }
    }

    private static string AddUseCaseSliceAnchorEvidence(
        BusinessUseCaseSlice slice,
        IDictionary<string, BusinessOntologyEvidence> evidence)
    {
        var id = "evidence:usecase-slice-" + Digest(slice.Id);
        var summary =
            "跨层业务用例切片："
            + $"action={slice.Action}; "
            + $"entry={slice.EntrySymbolId}; "
            + $"roles={string.Join(",", slice.Roles)}; "
            + $"files={string.Join(",", slice.FileIds)}.";
        var item = new BusinessOntologyEvidence(
            id,
            "codeknowledge",
            "use-case-slices",
            slice.EntrySymbolId,
            1,
            1,
            "contractual",
            "business-use-case-slice",
            0.82,
            "use-case-slice",
            summary);
        if (evidence.TryGetValue(item.Id, out var existing) && existing != item)
        {
            throw new InvalidOperationException($"Use-case slice evidence identity '{item.Id}' has conflicting content.");
        }
        evidence[item.Id] = item;
        return item.Id;
    }

    private static string RelationRationale(
        string min,
        bool hasSlice,
        IReadOnlyList<BusinessUseCaseSlice> slices)
    {
        if (!hasSlice)
        {
            return min == "1"
                ? "direct-only diagnostic: 仅有 typed_reference 与直接非空/持久化约束支持；未匹配跨层 use-case slice，因此只保留为较低置信 pending relation。"
                : "direct-only diagnostic: 仅有 typed_reference 直接类型事实支持；未匹配跨层 use-case slice，因此不能声称跨层业务关系。";
        }
        var sliceText = UseCaseSliceRationaleSummary(slices);
        return Bounded(min == "1"
            ? "direct typed_reference、直接非空/持久化约束与可匹配的跨层 use-case slice 共同支持该关系：" + sliceText
            : "direct typed_reference 与可匹配的跨层 use-case slice 共同支持该关系：" + sliceText,
            CandidateRationaleMaxLength);
    }

    private static string RuleRationale(IReadOnlyList<BusinessUseCaseSlice> slices) =>
        DeterministicRationale(
            "direct business_guard 与匹配的跨文件 use-case slice 共同支持条件业务规则。"
            + " representativeActions="
            + UseCaseSliceRationaleSummary(slices));

    private static string UseCaseSliceRationaleSummary(IReadOnlyList<BusinessUseCaseSlice> slices)
    {
        var ordered = slices
            .OrderBy(item => item.Action, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var sampled = ordered.Length <= SliceRationaleSampleLimit
            ? ordered
            : ordered
                .Take(SliceRationaleSampleLimit - 1)
                .Concat(ordered.Skip(ordered.Length - 1))
                .ToArray();
        var sliceText = string.Join("; ", sampled.Select(FormatUseCaseSliceRationale));
        if (ordered.Length <= sampled.Length)
        {
            return sliceText + $"; totalSlices={ordered.Length}";
        }
        return sliceText + $"; ... totalSlices={ordered.Length}; omittedSlices={ordered.Length - sampled.Length}";
    }

    private static string FormatUseCaseSliceRationale(BusinessUseCaseSlice slice) =>
        $"action={Bounded(slice.Action, 120)} "
        + $"roles={string.Join(",", slice.Roles.Take(6))} "
        + $"files={string.Join(",", slice.FileIds.Take(6))}";

    private static string DeterministicRationale(string value) =>
        Bounded(value, CandidateRationaleMaxLength);

    private static string Bounded(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 1)] + "…";

    private static IReadOnlyList<BusinessOntologyDiagnostic> DetectDirectOnlyRelationDiagnostics(
        IEnumerable<BusinessOntologyCandidate> candidates)
    {
        var diagnostics = new List<BusinessOntologyDiagnostic>();
        foreach (var candidate in candidates
            .Where(item => item.Status == "pending"
                && item.SubjectKind == OntologySemanticCandidateKinds.Relation
                && item.Reason.Contains("direct-only diagnostic", StringComparison.Ordinal))
            .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(candidate.PayloadJson);
            var semantic = document.RootElement.GetProperty("semantic");
            var key = String(semantic, "fromConceptId") + "." + String(semantic, "name");
            var details = OntologySemanticJson.Canonicalize(JsonSerializer.Serialize(new
            {
                candidateId = candidate.Id,
                reason = "no_matching_use_case_slice",
                evidenceIds = candidate.EvidenceIds,
            }));
            diagnostics.Add(new BusinessOntologyDiagnostic(
                "diagnostic:semantic-direct-only:" + Digest(details),
                "direct-only",
                OntologySemanticCandidateKinds.Relation,
                key,
                "关系候选仅由 direct typed_reference 支持，未匹配跨层 use-case slice；保留为较低置信 pending relation。",
                details,
                "info"));
        }
        return diagnostics;
    }

    private static bool IsMatchingCorroboration(
        BusinessOntologyCorroborationObservation observation,
        IReadOnlySet<string> domainTokens,
        IReadOnlySet<string> typeAnchors,
        IReadOnlyDictionary<string, IReadOnlySet<string>> routeAnchors)
    {
        ValidateCorroboration(observation);
        var token = NormalizeDomainToken(observation.DomainToken);
        if (token.Length == 0 || !domainTokens.Contains(token))
        {
            return false;
        }

        return observation.AnchorKind switch
        {
            "type" => typeAnchors.Contains(NormalizeTypeAnchor(observation.AnchorValue)),
            "route" => routeAnchors.TryGetValue(
                    NormalizeRouteAnchor(observation.AnchorValue),
                    out var routeTokens)
                && routeTokens.Contains(token),
            _ => false,
        };
    }

    private static void ValidateCorroboration(BusinessOntologyCorroborationObservation item)
    {
        if (string.IsNullOrWhiteSpace(item.EvidenceId)
            || string.IsNullOrWhiteSpace(item.Repository)
            || string.IsNullOrWhiteSpace(item.Path)
            || string.IsNullOrWhiteSpace(item.Symbol)
            || string.IsNullOrWhiteSpace(item.DomainToken)
            || string.IsNullOrWhiteSpace(item.AnchorValue)
            || string.IsNullOrWhiteSpace(item.Summary)
            || string.IsNullOrWhiteSpace(item.Resolver))
        {
            throw new ArgumentException("Corroboration identity, source, domain token, anchor, and summary are required.");
        }
        if (!BusinessOntologyCorroborationKinds.All.Contains(item.SourceKind)
            || item.AnchorKind is not ("route" or "type"))
        {
            throw new ArgumentException("Corroboration must be a frontend page/form or document with a route/type anchor.");
        }
        if (Path.IsPathRooted(item.Path)
            || item.Path.Contains('\\')
            || item.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal)
            || item.StartLine < 1
            || item.EndLine < item.StartLine
            || double.IsNaN(item.Confidence)
            || item.Confidence < 0
            || item.Confidence > 1)
        {
            throw new ArgumentException("Corroboration path, line range, or confidence is invalid.");
        }
    }

    private static BusinessOntologyEvidence ToCorroborationEvidence(
        BusinessOntologyCorroborationObservation item) =>
        new(
            item.EvidenceId,
            item.Repository,
            item.Path,
            item.Symbol,
            item.StartLine,
            item.EndLine,
            item.SourceKind == BusinessOntologyCorroborationKinds.Documentation
                ? "contractual"
                : "presentational",
            item.Resolver,
            Confidence(item.Confidence),
            item.SourceKind,
            item.Summary);

    private static IReadOnlySet<string> CandidateDomainTokens(
        BusinessOntologyCandidate candidate,
        BusinessOntologySnapshot snapshot)
    {
        using var document = JsonDocument.Parse(candidate.PayloadJson);
        var semantic = document.RootElement.GetProperty("semantic");
        var values = new List<string>();
        foreach (var property in new[] { "fromConceptId", "toConceptId", "subjectConceptId", "name", "stateProperty" })
        {
            if (semantic.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            {
                values.Add(value.GetString() ?? "");
            }
        }
        if (semantic.TryGetProperty("predicate", out var predicate)
            && predicate.ValueKind == JsonValueKind.Object
            && predicate.TryGetProperty("property", out var member)
            && member.ValueKind == JsonValueKind.String)
        {
            values.Add(member.GetString() ?? "");
        }
        foreach (var conceptId in values
            .Where(value => value.Contains('.', StringComparison.Ordinal))
            .ToArray())
        {
            var concept = snapshot.Concepts.FirstOrDefault(item => item.Id == conceptId);
            if (concept is not null)
            {
                values.Add(concept.Label);
            }
        }
        return values.Select(NormalizeDomainToken)
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> CandidateTypeAnchors(
        BusinessOntologyCandidate candidate,
        IReadOnlyCollection<SemanticClaimObservation> supportingClaims,
        BusinessOntologySnapshot snapshot)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in supportingClaims)
        {
            anchors.Add(NormalizeTypeAnchor(claim.SubjectId));
            var payload = ParsePayload(claim.PayloadJson);
            if (payload is null)
            {
                continue;
            }
            foreach (var property in new[]
            {
                "ownerSymbolId", "resolvedTypeSymbolId", "resolvedTypeName", "rawType",
                "enumTypeSymbolId", "enumType",
            })
            {
                var value = String(payload.Value, property);
                if (value.Length > 0)
                {
                    anchors.Add(NormalizeTypeAnchor(value));
                }
            }
        }

        using var document = JsonDocument.Parse(candidate.PayloadJson);
        var semantic = document.RootElement.GetProperty("semantic");
        var conceptIds = new[] { "fromConceptId", "toConceptId", "subjectConceptId" }
            .Where(name => semantic.TryGetProperty(name, out _))
            .Select(name => String(semantic, name))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var mapping in snapshot.Mappings.Where(item =>
            item.SubjectKind == "concept" && conceptIds.Contains(item.SubjectId)))
        {
            anchors.Add(NormalizeTypeAnchor(mapping.Symbol));
        }
        anchors.Remove("");
        return anchors;
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> RouteAnchorTokens(
        IReadOnlyList<SemanticClaimObservation> claims)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var claim in claims.Where(item => item.Kind == CodeSemanticClaimKinds.RouteBinding))
        {
            var payload = ParsePayload(claim.PayloadJson);
            if (payload is null)
            {
                continue;
            }
            var anchors = new List<string>();
            if (payload.Value.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array)
            {
                anchors.AddRange(paths.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? ""));
            }
            anchors.Add(String(payload.Value, "site"));
            foreach (var anchor in anchors.Where(item => item.Length > 0))
            {
                var normalized = NormalizeRouteAnchor(anchor);
                if (!result.TryGetValue(normalized, out var tokens))
                {
                    tokens = [];
                    result[normalized] = tokens;
                }
                foreach (var token in DomainTokensFromAnchor(anchor))
                {
                    tokens.Add(token);
                }
            }
        }
        return result.ToDictionary(
            item => item.Key,
            item => (IReadOnlySet<string>)item.Value,
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> DomainTokensFromAnchor(string value) =>
        value.Split(['/', '.', ':', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeDomainToken)
            .Where(item => item.Length > 0);

    private static string NormalizeDomainToken(string value)
    {
        var terminal = value.Split('.').LastOrDefault() ?? value;
        var normalized = new string(terminal.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        foreach (var suffix in new[]
        {
            "documentation", "document", "docs", "page", "form", "view",
            "controller", "service", "entity", "dto",
        })
        {
            if (normalized.Length > suffix.Length && normalized.EndsWith(suffix, StringComparison.Ordinal))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }
        return normalized.EndsWith('s') && normalized.Length > 3 ? normalized[..^1] : normalized;
    }

    private static string NormalizeTypeAnchor(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string NormalizeRouteAnchor(string value) =>
        value.Trim().TrimEnd('/').ToLowerInvariant();

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..24];

    private static IReadOnlyList<BusinessOntologyDiagnostic> DetectConflicts(
        IEnumerable<BusinessOntologyCandidate> candidates)
    {
        var shapes = candidates
            .Where(item => item.Status == "pending"
                && OntologySemanticCandidateKinds.All.Contains(item.SubjectKind))
            .Select(CandidateConflictShape)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        var conflicts = new List<BusinessOntologyDiagnostic>();
        AddConflicts(
            shapes.Where(item => item.Kind == OntologySemanticCandidateKinds.Relation)
                .GroupBy(item => item.ConflictKey, StringComparer.Ordinal)
                .Where(group => group.Select(item => item.Variant).Distinct(StringComparer.Ordinal).Count() > 1),
            conflicts);
        AddConflicts(
            shapes.Where(item => item.Kind == OntologySemanticCandidateKinds.Rule)
                .GroupBy(item => item.ConflictKey, StringComparer.Ordinal)
                .Where(group => group.Select(item => item.Variant).Distinct(StringComparer.Ordinal).Count() > 1),
            conflicts);
        AddConflicts(
            shapes.Where(item => item.Kind == OntologySemanticCandidateKinds.Lifecycle)
                .GroupBy(item => item.ConflictKey, StringComparer.Ordinal)
                .Where(group => group.Select(item => item.Variant).Distinct(StringComparer.Ordinal).Count() > 1),
            conflicts);
        return conflicts.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    private static void AddConflicts(
        IEnumerable<IGrouping<string, CandidateConflictDraft>> groups,
        ICollection<BusinessOntologyDiagnostic> diagnostics)
    {
        foreach (var group in groups.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(item => item.CandidateId, StringComparer.Ordinal).ToArray();
            var details = OntologySemanticJson.Canonicalize(JsonSerializer.Serialize(new
            {
                conflictKey = group.Key,
                candidateIds = ordered.Select(item => item.CandidateId).ToArray(),
                variants = ordered.Select(item => JsonSerializer.Deserialize<JsonElement>(item.Variant)).ToArray(),
            }));
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(details)))
                .ToLowerInvariant();
            diagnostics.Add(new BusinessOntologyDiagnostic(
                "diagnostic:semantic-conflict:" + digest,
                "conflict",
                ordered[0].Kind,
                group.Key,
                $"检测到 {ordered[0].Kind} 候选之间存在互斥语义，已保留为分离的 pending candidates。",
                details,
                "warning"));
        }
    }

    private static CandidateConflictDraft? CandidateConflictShape(BusinessOntologyCandidate candidate)
    {
        using var document = JsonDocument.Parse(candidate.PayloadJson);
        var semantic = document.RootElement.GetProperty("semantic");
        return candidate.SubjectKind switch
        {
            OntologySemanticCandidateKinds.Relation => new CandidateConflictDraft(
                candidate.Id,
                candidate.SubjectKind,
                "relation:" + String(semantic, "fromConceptId") + ":" + String(semantic, "name"),
                OntologySemanticJson.Canonicalize(JsonSerializer.Serialize(new
                {
                    toConceptId = String(semantic, "toConceptId"),
                    min = String(semantic, "min"),
                    max = String(semantic, "max"),
                }))),
            OntologySemanticCandidateKinds.Rule => new CandidateConflictDraft(
                candidate.Id,
                candidate.SubjectKind,
                "rule:" + String(semantic, "id"),
                OntologySemanticJson.Canonicalize(semantic.GetProperty("predicate").GetRawText())),
            OntologySemanticCandidateKinds.Lifecycle => new CandidateConflictDraft(
                candidate.Id,
                candidate.SubjectKind,
                "lifecycle:" + String(semantic, "subjectConceptId"),
                OntologySemanticJson.Canonicalize(JsonSerializer.Serialize(new
                {
                    stateProperty = String(semantic, "stateProperty"),
                }))),
            _ => null,
        };
    }

    private ValidatedOntologySemanticCandidate ValidateCandidate(
        string kind,
        object semantic,
        IReadOnlyList<string> evidenceIds,
        string rationale,
        IReadOnlySet<string> concepts,
        IEnumerable<string> availableEvidenceIds)
    {
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = OntologySemanticCandidateValidator.SchemaVersion,
            kind,
            semantic,
            evidenceIds,
            basis = "deterministic",
            rationale = DeterministicRationale(rationale),
        });
        return candidateValidator.Validate(payload, concepts, availableEvidenceIds);
    }

    private static BusinessOntologyCandidate ToStoredCandidate(
        ValidatedOntologySemanticCandidate candidate,
        IReadOnlyCollection<SemanticClaimObservation> claims,
        double? confidenceOverride = null) =>
        new(
            candidate.Id,
            candidate.Kind,
            candidate.SemanticId,
            candidate.CanonicalPayloadJson,
            candidate.Rationale,
            confidenceOverride ?? claims.Min(item => Confidence(item.Confidence)),
            "pending",
            candidate.EvidenceIds);

    private static RuleSubjectResolution ResolveBusinessRuleSubject(
        SemanticClaimObservation guard,
        JsonElement guardPayload,
        IReadOnlyList<BusinessUseCaseSlice> matchingSlices,
        IReadOnlyList<SemanticClaimObservation> claims,
        IReadOnlyDictionary<string, string> conceptBySymbol)
    {
        var methodSymbolId = String(guardPayload, "methodSymbolId");
        var sliceClaimIds = matchingSlices
            .SelectMany(slice => slice.ClaimIds)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var predicateTokens = IdentifierTokens(String(guardPayload, "predicateSource"));
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in claims.Where(item =>
            item.Kind == CodeSemanticClaimKinds.TypedReference
            && sliceClaimIds.Contains(item.ClaimId)
            && TypedReferenceBelongsToMethod(item, methodSymbolId)))
        {
            var payload = ParsePayload(claim.PayloadJson);
            if (payload is null)
            {
                continue;
            }
            var member = String(payload.Value, "member");
            if (!predicateTokens.Contains(member))
            {
                continue;
            }
            var resolvedTypeSymbolId = String(payload.Value, "resolvedTypeSymbolId");
            if (conceptBySymbol.TryGetValue(resolvedTypeSymbolId, out var conceptId))
            {
                candidates.Add(conceptId);
            }
        }
        if (candidates.Count == 1)
        {
            return new RuleSubjectResolution(candidates.Single(), false, candidates.Order(StringComparer.Ordinal).ToArray());
        }
        if (candidates.Count > 1)
        {
            return new RuleSubjectResolution("", true, candidates.Order(StringComparer.Ordinal).ToArray());
        }
        return new RuleSubjectResolution("", true, []);
    }

    private static bool TypedReferenceBelongsToMethod(SemanticClaimObservation claim, string methodSymbolId)
    {
        if (methodSymbolId.Length == 0)
        {
            return false;
        }
        if (claim.SubjectId == methodSymbolId)
        {
            return true;
        }
        var payload = ParsePayload(claim.PayloadJson);
        return payload is not null
            && (String(payload.Value, "ownerSymbolId") == methodSymbolId
                || String(payload.Value, "declaringSymbolId") == methodSymbolId
                || String(payload.Value, "methodSymbolId") == methodSymbolId);
    }

    private static IEnumerable<string> ClaimSymbolRefs(SemanticClaimObservation claim)
    {
        var payload = ParsePayload(claim.PayloadJson);
        if (payload is null)
        {
            yield break;
        }
        foreach (var property in new[]
        {
            "ownerSymbolId", "resolvedTypeSymbolId", "enumTypeSymbolId", "subjectSymbolId",
            "methodSymbolId",
        })
        {
            var value = String(payload.Value, property);
            if (value.Length > 0)
            {
                yield return value;
            }
        }
    }

    private static IReadOnlySet<string> IdentifierTokens(string source)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder();
        foreach (var ch in source)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '$')
            {
                current.Append(ch);
                continue;
            }
            Flush();
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }
            var token = current.ToString();
            current.Clear();
            if (char.IsLetter(token[0]) || token[0] is '_' or '$')
            {
                tokens.Add(token);
            }
        }
    }

    private static BusinessOntologyDiagnostic AmbiguousRuleSubjectDiagnostic(
        SemanticClaimObservation guard,
        JsonElement payload,
        IReadOnlyList<string> conceptIds)
    {
        var details = OntologySemanticJson.Canonicalize(JsonSerializer.Serialize(new
        {
            guardClaimId = guard.ClaimId,
            methodSymbolId = String(payload, "methodSymbolId"),
            predicateSource = String(payload, "predicateSource"),
            candidateConceptIds = conceptIds,
        }));
        return new BusinessOntologyDiagnostic(
            "diagnostic:semantic-rule-subject:" + Digest(details),
            "ambiguous-business-rule-subject",
            OntologySemanticCandidateKinds.Rule,
            guard.ClaimId,
            "业务 guard 同时触达多个 canonical concept，未能唯一确定约束对象；未生成 business rule。",
            details,
            "warning");
    }

    private static string BusinessRuleName(JsonElement guardPayload)
    {
        var method = LowerCamel(String(guardPayload, "method"));
        if (method.Length == 0)
        {
            method = "guard";
        }
        var predicateDigest = Digest(String(guardPayload, "predicateSource") + "\n" + String(guardPayload, "effectSource"))[..8];
        return method + "BusinessCondition" + predicateDigest;
    }

    private static RuleDraft? CreateRuleDraft(
        SemanticClaimObservation claim,
        IReadOnlyDictionary<string, string> conceptBySymbol)
    {
        var payload = ParsePayload(claim.PayloadJson);
        if (payload is null)
        {
            return null;
        }

        var ownerSymbolId = String(payload.Value, "ownerSymbolId");
        var member = LowerCamel(String(payload.Value, "member"));
        if (member.Length == 0 || !conceptBySymbol.TryGetValue(ownerSymbolId, out var conceptId))
        {
            return null;
        }

        var sourceOperator = claim.Kind == CodeSemanticClaimKinds.ValidationConstraint
            ? String(payload.Value, "operator")
            : String(payload.Value, "constraintKind");
        var value = payload.Value.TryGetProperty("value", out var valueElement)
            ? valueElement.Clone()
            : default;
        var ruleKind = sourceOperator switch
        {
            "required" when IsTrue(value) => "required",
            "nullable" or "optional" when IsFalse(value) => "required",
            "min" or "minLength" => "min",
            "max" or "maxLength" => "max",
            "pattern" => "pattern",
            "unique" when IsTrue(value) => "unique",
            _ => "",
        };
        if (ruleKind.Length == 0)
        {
            return null;
        }

        var description = ruleKind switch
        {
            "required" => $"{member} 必须提供。",
            "min" => $"{member} 必须满足最小值约束。",
            "max" => $"{member} 必须满足最大值约束。",
            "pattern" => $"{member} 必须符合指定格式。",
            "unique" => $"{member} 必须保持唯一。",
            _ => throw new InvalidOperationException(),
        };
        object predicate = ruleKind switch
        {
            "required" => new { property = member, @operator = "present" },
            "unique" => new { property = member, @operator = "unique" },
            "pattern" => new { property = member, @operator = "matches", value },
            "min" => new
            {
                property = member,
                @operator = sourceOperator == "minLength" ? "minLength" : "min",
                value,
            },
            "max" => new
            {
                property = member,
                @operator = sourceOperator == "maxLength" ? "maxLength" : "max",
                value,
            },
            _ => throw new InvalidOperationException(),
        };
        var semantic = new
        {
            id = conceptId + "." + member + char.ToUpperInvariant(ruleKind[0]) + ruleKind[1..],
            subjectConceptId = conceptId,
            ruleKind,
            descriptionZh = description,
            predicate,
            effect = new { type = "reject", messageZh = description },
        };
        var semanticKey = OntologySemanticJson.Canonicalize(JsonSerializer.Serialize(semantic));
        return new RuleDraft(claim, semantic, semanticKey);
    }

    private static LifecycleFieldDraft? CreateLifecycleField(
        SemanticClaimObservation claim,
        IReadOnlyDictionary<string, string> conceptBySymbol)
    {
        var payload = ParsePayload(claim.PayloadJson);
        if (payload is null)
        {
            return null;
        }

        var ownerSymbolId = String(payload.Value, "ownerSymbolId");
        var stateProperty = LowerCamel(String(payload.Value, "member"));
        var enumType = String(payload.Value, "enumType");
        var enumTypeSymbolId = String(payload.Value, "enumTypeSymbolId");
        if (stateProperty.Length == 0
            || enumType.Length == 0
            || enumTypeSymbolId.Length == 0
            || !conceptBySymbol.TryGetValue(ownerSymbolId, out var conceptId))
        {
            return null;
        }

        return new LifecycleFieldDraft(
            claim,
            conceptId,
            ownerSymbolId,
            stateProperty,
            enumType,
            enumTypeSymbolId);
    }

    private static TransitionDraft? CreateTransitionDraft(
        SemanticClaimObservation claim,
        JsonElement payload,
        LifecycleFieldDraft field,
        IReadOnlySet<string> states,
        IReadOnlyList<StateGuardDraft> guards,
        IReadOnlyList<BusinessUseCaseSlice> slices)
    {
        if (String(payload, "ownerSymbolId") != field.OwnerSymbolId
            || StateProperty(payload) != field.StateProperty)
        {
            return null;
        }
        if (field.EnumTypeSymbolId.Length > 0)
        {
            if (String(payload, "enumType") != field.EnumType
                || String(payload, "enumTypeSymbolId") is { Length: > 0 } assignmentEnum
                    && assignmentEnum != field.EnumTypeSymbolId)
            {
                return null;
            }
        }
        else if (!IsScalarStateEncoding(String(payload, "valueEncoding")))
        {
            return null;
        }

        var fromState = String(payload, "fromValue");
        var toState = String(payload, "toValue");
        var method = String(payload, "method");
        var methodSymbolId = String(payload, "methodSymbolId");
        var action = LowerCamel(method);
        if (action.Length == 0
            || !states.Contains(fromState)
            || !states.Contains(toState))
        {
            return null;
        }

        var matchingGuards = guards
            .Where(guard => guard.MethodSymbolId == methodSymbolId
                && guard.Claim.EndLine < claim.StartLine
                && guard.Property == field.StateProperty
                && ReceiverMatches(guard.Receiver, String(payload, "receiver"))
                && guard.AllowedValues.Contains(fromState, StringComparer.Ordinal))
            .OrderBy(guard => guard.Claim.ClaimId, StringComparer.Ordinal)
            .ToArray();
        foreach (var guard in matchingGuards)
        {
            var matchingSlices = slices
                .Where(slice => slice.ConceptIds.Contains(field.ConceptId, StringComparer.Ordinal)
                    && slice.ClaimIds.Contains(claim.ClaimId, StringComparer.Ordinal)
                    && slice.ClaimIds.Contains(guard.Claim.ClaimId, StringComparer.Ordinal))
                .OrderBy(slice => slice.Id, StringComparer.Ordinal)
                .ToArray();
            if (matchingSlices.Length == 0)
            {
                continue;
            }

            return new TransitionDraft(
                claim,
                guard.Claim,
                method,
                methodSymbolId,
                action,
                fromState,
                toState,
                guard.PredicateSource,
                String(payload, "valueEncoding"),
                String(payload, "rawValue"),
                guard.EffectKind,
                guard.EffectMessage,
                matchingSlices);
        }

        return null;
    }

    private static StateGuardDraft? CreateStateGuardDraft(SemanticClaimObservation claim)
    {
        var payload = ParsePayload(claim.PayloadJson);
        if (payload is null
            || !payload.Value.TryGetProperty("stateGuard", out var stateGuard)
            || stateGuard.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var property = LowerCamel(String(stateGuard, "property"));
        var values = stateGuard.TryGetProperty("allowedValues", out var allowed)
            && allowed.ValueKind == JsonValueKind.Array
            ? allowed.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
        var methodSymbolId = String(payload.Value, "methodSymbolId");
        if (property.Length == 0 || values.Length == 0 || methodSymbolId.Length == 0)
        {
            return null;
        }

        return new StateGuardDraft(
            claim,
            methodSymbolId,
            property,
            String(stateGuard, "receiver"),
            values,
            String(payload.Value, "predicateSource"),
            String(payload.Value, "effectKind"),
            String(payload.Value, "effectMessage"));
    }

    private static IReadOnlyDictionary<string, string> UniqueConceptMappings(
        IReadOnlyList<BusinessOntologyMapping> mappings,
        IReadOnlySet<string> conceptIds) =>
        mappings
            .Where(item => item.SubjectKind == "concept"
                && conceptIds.Contains(item.SubjectId)
                && !string.IsNullOrWhiteSpace(item.Symbol))
            .GroupBy(item => item.Symbol, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.SubjectId).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().SubjectId, StringComparer.Ordinal);

    private static string AddEvidence(
        SemanticClaimObservation claim,
        IDictionary<string, BusinessOntologyEvidence> evidence)
    {
        // ck semantic claim IDs already use the DSL-compatible `semantic:<hash>` namespace.
        // Prefixing them again would create a multi-colon ID rejected by ontology-xml.
        var id = claim.ClaimId;
        evidence.TryAdd(id, new BusinessOntologyEvidence(
            id,
            claim.Repository,
            claim.Path,
            claim.SubjectId,
            claim.StartLine,
            claim.EndLine,
            claim.Kind is CodeSemanticClaimKinds.ValidationConstraint or CodeSemanticClaimKinds.PersistenceConstraint
                ? "enforced"
                : "contractual",
            claim.Resolver,
            Confidence(claim.Confidence),
            "code-semantic-claim",
            $"直接源码语义事实：{claim.Kind}。"));
        return id;
    }

    private async Task<IReadOnlyList<SemanticClaimObservation>> ReadClaimsAsync(
        CancellationToken cancellationToken)
    {
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[claim_id, subject_id, kind, payload_json, file_id, start_line, end_line,
              confidence, resolver, evidence, repository, path, root_path] :=
              *ck_semantic_claim{
                claim_id, subject_id, kind, payload_json, file_id, start_line, end_line,
                confidence, resolver, evidence
              },
              *ck_file{file_id, repo_id: repository, path},
              *ck_repo{repo_id: repository, root_path}
            """,
            cancellationToken: cancellationToken);
        return rows.Rows.Select(row => new SemanticClaimObservation(
            String(row, 0),
            String(row, 1),
            String(row, 2),
            String(row, 3),
            String(row, 4),
            Integer(row, 5),
            Integer(row, 6),
            Number(row, 7),
            String(row, 8),
            String(row, 9),
            String(row, 10),
            String(row, 11),
            String(row, 12))).ToArray();
    }

    private async Task<IReadOnlySet<string>> ReadInfrastructureRoleSymbolIdsAsync(
        CancellationToken cancellationToken)
    {
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[symbol_id, role_id] :=
              *ck_edge{from_id: symbol_id, to_id: role_id, kind: "SPRING_ROLE"}
            """,
            cancellationToken: cancellationToken);
        return rows.Rows
            .Where(row => InfrastructureSpringRoles.Contains(SpringRoleName(String(row, 1))))
            .Select(row => String(row, 0))
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<IReadOnlyDictionary<string, SymbolDescriptor>> ReadSymbolDescriptorsAsync(
        CancellationToken cancellationToken)
    {
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[symbol_id, name, kind, path] :=
              *ck_symbol{symbol_id, file_id, name, kind},
              *ck_file{file_id, path}
            """,
            cancellationToken: cancellationToken);
        return rows.Rows
            .Select(row => new SymbolDescriptor(
                String(row, 0),
                String(row, 1),
                String(row, 2),
                String(row, 3)))
            .GroupBy(item => item.SymbolId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.Path, StringComparer.Ordinal)
                    .ThenBy(item => item.Name, StringComparer.Ordinal)
                    .First(),
                StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> FindSuppressedWorkflowCarriers(
        IReadOnlyList<SemanticClaimObservation> claims,
        IReadOnlyDictionary<string, SymbolDescriptor> symbols,
        IReadOnlyDictionary<string, string> conceptBySymbol,
        IReadOnlySet<string> infrastructureRoleSymbols,
        ICollection<BusinessOntologyDiagnostic> diagnostics)
    {
        var owners = claims
            .Where(item => item.Kind == CodeSemanticClaimKinds.TypedReference)
            .Select(item => (Claim: item, Payload: ParsePayload(item.PayloadJson)))
            .Where(item => item.Payload is not null && IsStructuralRelationTypedReference(item.Payload.Value))
            .Select(item => new
            {
                OwnerSymbolId = String(item.Payload!.Value, "ownerSymbolId"),
                TargetSymbolId = String(item.Payload.Value, "resolvedTypeSymbolId"),
                Member = NormalizeRelationMember(String(item.Payload.Value, "member")),
            })
            .Where(item => item.OwnerSymbolId.Length > 0
                && item.TargetSymbolId.Length > 0
                && item.Member.Length > 0
                && !infrastructureRoleSymbols.Contains(item.OwnerSymbolId)
                && !infrastructureRoleSymbols.Contains(item.TargetSymbolId)
                && conceptBySymbol.TryGetValue(item.OwnerSymbolId, out var fromConceptId)
                && conceptBySymbol.TryGetValue(item.TargetSymbolId, out var toConceptId)
                && fromConceptId != toConceptId
                && !IsOperationLikeRelationName(item.Member))
            .GroupBy(item => item.OwnerSymbolId, StringComparer.Ordinal)
            .Select(group => new
            {
                OwnerSymbolId = group.Key,
                Members = group.Select(item => item.Member + "\u001f" + item.TargetSymbolId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
            })
            .Where(item => symbols.TryGetValue(item.OwnerSymbolId, out var symbol)
                && IsWorkflowCarrierSymbol(symbol)
                && (item.Members.Length >= WorkflowCarrierStructuralFanoutThreshold
                    || IsImplementationLocalWorkflowCarrier(symbol)))
            .Select(item => new
            {
                item.OwnerSymbolId,
                item.Members,
                Reason = IsImplementationLocalWorkflowCarrier(symbols[item.OwnerSymbolId])
                    ? "implementation_local_workflow_carrier"
                    : "high_fanout_workflow_carrier",
            })
            .OrderBy(item => item.OwnerSymbolId, StringComparer.Ordinal)
            .ToArray();

        foreach (var owner in owners)
        {
            var symbol = symbols[owner.OwnerSymbolId];
            var implementationLocal = owner.Reason == "implementation_local_workflow_carrier";
            var details = OntologySemanticJson.Canonicalize(JsonSerializer.Serialize(new
            {
                ownerSymbolId = owner.OwnerSymbolId,
                ownerName = symbol.Name,
                ownerKind = symbol.Kind,
                ownerPath = symbol.Path,
                directStructuralFanout = owner.Members.Length,
                threshold = WorkflowCarrierStructuralFanoutThreshold,
                relations = owner.Members.Select(item => item.Replace("\u001f", " -> ", StringComparison.Ordinal)).ToArray(),
                reason = owner.Reason,
            }));
            diagnostics.Add(new BusinessOntologyDiagnostic(
                "diagnostic:semantic-workflow-carrier:" + Digest(details),
                implementationLocal
                    ? "implementation-local-workflow-carrier-relations-suppressed"
                    : "workflow-carrier-relations-suppressed",
                OntologySemanticCandidateKinds.Relation,
                owner.OwnerSymbolId,
                implementationLocal
                    ? $"实现源文件 {symbol.Path} 中的流程局部载体 {symbol.Name} 暂存了 "
                        + $"{owner.Members.Length} 个结构引用，未将其投影为稳定业务关系。"
                    : $"高扇出流程载体 {symbol.Name} 一次聚合 {owner.Members.Length} 个结构引用，"
                        + "未将其暂存的副作用记录投影为稳定业务关系。",
                details,
                "info"));
        }

        return owners
            .Select(item => item.OwnerSymbolId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsWorkflowCarrierSymbol(SymbolDescriptor symbol)
    {
        if (symbol.Kind is not ("class" or "record" or "struct"))
        {
            return false;
        }
        if (HasIdentifierSuffix(symbol.Name, "Entity"))
        {
            return false;
        }
        return HasIdentifierSuffix(symbol.Name, "DTO")
            || HasIdentifierSuffix(symbol.Name, "VO")
            || HasIdentifierSuffix(symbol.Name, "Model");
    }

    private static bool IsImplementationLocalWorkflowCarrier(SymbolDescriptor symbol)
    {
        if (!IsWorkflowCarrierSymbol(symbol))
        {
            return false;
        }

        var path = "/" + symbol.Path.Replace('\\', '/').Trim('/') + "/";
        var lowerPath = path.ToLowerInvariant();
        if (lowerPath.Contains("/service/impl/", StringComparison.Ordinal))
        {
            return true;
        }
        if (IsDedicatedTransportOrViewPath(lowerPath))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(symbol.Path);
        var implementationOwnerFile = ImplementationOwnerFileSuffixes.Any(
            suffix => HasIdentifierSuffix(fileName, suffix));
        return implementationOwnerFile
            || ImplementationOwnerPathSegments.Any(
                segment => lowerPath.Contains("/" + segment + "/", StringComparison.Ordinal));
    }

    private static bool IsDedicatedTransportOrViewPath(string lowerPath) =>
        lowerPath.Contains("/data/dto/", StringComparison.Ordinal)
        || lowerPath.Contains("/data/vo/", StringComparison.Ordinal)
        || lowerPath.Contains("/common/dto/", StringComparison.Ordinal)
        || lowerPath.Contains("/common/vo/", StringComparison.Ordinal)
        || lowerPath.Contains("/dto/", StringComparison.Ordinal)
        || lowerPath.Contains("/vo/", StringComparison.Ordinal);

    private static bool HasIdentifierSuffix(string value, string suffix)
    {
        if (value.Equals(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (value.Length <= suffix.Length)
        {
            return false;
        }

        var suffixStart = value.Length - suffix.Length;
        return char.IsUpper(value[suffixStart])
            && value.AsSpan(suffixStart).Equals(suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectRequired(SemanticClaimObservation claim)
    {
        if (claim.Kind is not (CodeSemanticClaimKinds.ValidationConstraint or CodeSemanticClaimKinds.PersistenceConstraint))
        {
            return false;
        }
        var payload = ParsePayload(claim.PayloadJson);
        if (payload is null || !payload.Value.TryGetProperty("value", out var value))
        {
            return false;
        }
        var kind = claim.Kind == CodeSemanticClaimKinds.ValidationConstraint
            ? String(payload.Value, "operator")
            : String(payload.Value, "constraintKind");
        return kind == "required" && IsTrue(value)
            || kind is "nullable" or "optional" && IsFalse(value);
    }

    private static bool IsAssistedProposalClaim(SemanticClaimObservation claim) =>
        claim.Kind is not (CodeSemanticClaimKinds.ValidationConstraint or CodeSemanticClaimKinds.PersistenceConstraint);

    private static bool IsStructuralRelationTypedReference(JsonElement payload)
    {
        var usageKind = String(payload, "memberKind");
        if (usageKind.Length == 0)
        {
            usageKind = String(payload, "usageKind");
        }
        if (usageKind.Length == 0)
        {
            usageKind = String(payload, "referenceKind");
        }
        return usageKind is "field" or "record_component";
    }

    private static string SpringRoleName(string roleId)
    {
        var terminal = roleId.Split([':', '/', '.'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? roleId;
        return new string(terminal.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static bool IsOperationLikeRelationName(string member)
    {
        if (member.StartsWith("get", StringComparison.Ordinal)
            || member.StartsWith("set", StringComparison.Ordinal)
            || member.StartsWith("query", StringComparison.Ordinal)
            || member.StartsWith("find", StringComparison.Ordinal)
            || member.StartsWith("load", StringComparison.Ordinal)
            || member.StartsWith("list", StringComparison.Ordinal)
            || member.StartsWith("select", StringComparison.Ordinal)
            || member.StartsWith("save", StringComparison.Ordinal)
            || member.StartsWith("update", StringComparison.Ordinal)
            || member.StartsWith("delete", StringComparison.Ordinal)
            || member.StartsWith("remove", StringComparison.Ordinal)
            || member.StartsWith("export", StringComparison.Ordinal)
            || member.StartsWith("import", StringComparison.Ordinal)
            || member.StartsWith("create", StringComparison.Ordinal))
        {
            return true;
        }
        return member.EndsWith("ById", StringComparison.Ordinal)
            || member.EndsWith("ByKey", StringComparison.Ordinal);
    }

    private static bool ReceiverMatches(string guardReceiver, string mutationReceiver)
    {
        var guard = NormalizeReceiver(guardReceiver);
        var mutation = NormalizeReceiver(mutationReceiver);
        return guard.Length == 0
            || mutation.Length == 0
            || mutation == "this"
            || guard == mutation;
    }

    private static string NormalizeReceiver(string receiver) =>
        receiver.Trim() switch
        {
            "" => "",
            "this" => "this",
            var value when value.StartsWith("this.", StringComparison.Ordinal) => value["this.".Length..],
            var value => value,
        };

    private static JsonElement? ParsePayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Label(BusinessOntologySnapshot snapshot, string conceptId) =>
        snapshot.Concepts.First(item => item.Id == conceptId).Label;

    private static string LowerCamel(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Length == 0 || !char.IsLetter(normalized[0])
            ? ""
            : char.ToLowerInvariant(normalized[0]) + normalized[1..];
    }

    private static string NormalizeRelationMember(string value)
    {
        var member = LowerCamel(value);
        if (member.Length == 0)
        {
            return "";
        }

        while (TryStripRelationSuffix(ref member))
        {
        }
        return member.Length == 0 || !char.IsLetter(member[0])
            ? ""
            : char.ToLowerInvariant(member[0]) + member[1..];
    }

    private static bool TryStripRelationSuffix(ref string value)
    {
        foreach (var suffix in RelationMemberContainerSuffixes.Concat(RelationMemberCarrierSuffixes))
        {
            if (!HasIdentifierSuffix(value, suffix))
            {
                continue;
            }

            value = value[..^suffix.Length];
            return true;
        }
        return false;
    }

    private static string StateProperty(JsonElement payload)
    {
        var property = LowerCamel(String(payload, "property"));
        return property.Length > 0 ? property : LowerCamel(String(payload, "field"));
    }

    private static bool IsStateLikeProperty(string property) =>
        property.Equals("state", StringComparison.OrdinalIgnoreCase)
        || property.Equals("status", StringComparison.OrdinalIgnoreCase)
        || property.EndsWith("State", StringComparison.Ordinal)
        || property.EndsWith("Status", StringComparison.Ordinal);

    private static bool IsScalarStateEncoding(string encoding) =>
        encoding is "string"
            or "numeric"
            or "enum.member"
            or "enum.getCode"
            or "enum.getDbCode"
            or "enum.name";

    private static string PascalToken(string value)
    {
        var parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(part =>
            char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static bool IsTrue(JsonElement value) =>
        value.ValueKind == JsonValueKind.True
        || value.ValueKind == JsonValueKind.String
        && bool.TryParse(value.GetString(), out var parsed)
        && parsed;

    private static bool IsFalse(JsonElement value) =>
        value.ValueKind == JsonValueKind.False
        || value.ValueKind == JsonValueKind.String
        && bool.TryParse(value.GetString(), out var parsed)
        && !parsed;

    private static bool Boolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && IsTrue(value);

    private static string String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string String(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.String ? row[index].GetString() ?? "" : row[index].ToString();

    private static int Integer(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.Number ? row[index].GetInt32() : 0;

    private static double Number(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.Number ? row[index].GetDouble() : 0;

    private static double Confidence(double value) =>
        double.IsNaN(value) || value <= 0 ? 0.6 : Math.Min(1, value);

    private sealed record SemanticClaimObservation(
        string ClaimId,
        string SubjectId,
        string Kind,
        string PayloadJson,
        string FileId,
        int StartLine,
        int EndLine,
        double Confidence,
        string Resolver,
        string SourceEvidence,
        string Repository,
        string Path,
        string RepositoryRoot);

    private sealed record SymbolDescriptor(
        string SymbolId,
        string Name,
        string Kind,
        string Path);

    private sealed record RuleDraft(
        SemanticClaimObservation Claim,
        object Semantic,
        string SemanticKey);

    private sealed record RuleSubjectResolution(
        string ConceptId,
        bool Ambiguous,
        IReadOnlyList<string> CandidateConceptIds);

    private sealed record RelationCandidateDraft(
        string FromConceptId,
        string ToConceptId,
        string Member,
        bool Collection,
        IReadOnlyList<SemanticClaimObservation> SupportingClaims,
        IReadOnlyList<BusinessUseCaseSlice> MatchingSlices);

    private sealed record StateValueDraft(
        SemanticClaimObservation Claim,
        string Value);

    private sealed record LifecycleFieldDraft(
        SemanticClaimObservation Claim,
        string ConceptId,
        string OwnerSymbolId,
        string StateProperty,
        string EnumType,
        string EnumTypeSymbolId);

    private sealed record TransitionDraft(
        SemanticClaimObservation MutationClaim,
        SemanticClaimObservation GuardClaim,
        string Method,
        string MethodSymbolId,
        string Action,
        string FromState,
        string ToState,
        string PredicateSource,
        string ValueEncoding,
        string RawValue,
        string EffectKind,
        string EffectMessage,
        IReadOnlyList<BusinessUseCaseSlice> MatchingSlices);

    private sealed record StateGuardDraft(
        SemanticClaimObservation Claim,
        string MethodSymbolId,
        string Property,
        string Receiver,
        IReadOnlyList<string> AllowedValues,
        string PredicateSource,
        string EffectKind,
        string EffectMessage);

    private sealed record CandidateConflictDraft(
        string CandidateId,
        string Kind,
        string ConflictKey,
        string Variant);

    private sealed class StringTupleComparer : IEqualityComparer<(string Owner, string Member)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string Owner, string Member) x, (string Owner, string Member) y) =>
            StringComparer.Ordinal.Equals(x.Owner, y.Owner)
            && StringComparer.Ordinal.Equals(x.Member, y.Member);

        public int GetHashCode((string Owner, string Member) value) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.Owner),
                StringComparer.Ordinal.GetHashCode(value.Member));
    }

    private sealed class RelationTupleComparer : IEqualityComparer<(string FromConceptId, string ToConceptId, string Member)>
    {
        public static readonly RelationTupleComparer Instance = new();

        public bool Equals(
            (string FromConceptId, string ToConceptId, string Member) x,
            (string FromConceptId, string ToConceptId, string Member) y) =>
            StringComparer.Ordinal.Equals(x.FromConceptId, y.FromConceptId)
            && StringComparer.Ordinal.Equals(x.ToConceptId, y.ToConceptId)
            && StringComparer.Ordinal.Equals(x.Member, y.Member);

        public int GetHashCode((string FromConceptId, string ToConceptId, string Member) value) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.FromConceptId),
                StringComparer.Ordinal.GetHashCode(value.ToConceptId),
                StringComparer.Ordinal.GetHashCode(value.Member));
    }

    private sealed class StateFieldTupleComparer :
        IEqualityComparer<(string OwnerSymbolId, string StateProperty, string EnumTypeSymbolId)>
    {
        public static readonly StateFieldTupleComparer Instance = new();

        public bool Equals(
            (string OwnerSymbolId, string StateProperty, string EnumTypeSymbolId) x,
            (string OwnerSymbolId, string StateProperty, string EnumTypeSymbolId) y) =>
            StringComparer.Ordinal.Equals(x.OwnerSymbolId, y.OwnerSymbolId)
            && StringComparer.Ordinal.Equals(x.StateProperty, y.StateProperty)
            && StringComparer.Ordinal.Equals(x.EnumTypeSymbolId, y.EnumTypeSymbolId);

        public int GetHashCode(
            (string OwnerSymbolId, string StateProperty, string EnumTypeSymbolId) value) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.OwnerSymbolId),
                StringComparer.Ordinal.GetHashCode(value.StateProperty),
                StringComparer.Ordinal.GetHashCode(value.EnumTypeSymbolId));
    }

    private sealed class TransitionTupleComparer :
        IEqualityComparer<(string Action, string FromState, string ToState)>
    {
        public static readonly TransitionTupleComparer Instance = new();

        public bool Equals(
            (string Action, string FromState, string ToState) x,
            (string Action, string FromState, string ToState) y) =>
            StringComparer.Ordinal.Equals(x.Action, y.Action)
            && StringComparer.Ordinal.Equals(x.FromState, y.FromState)
            && StringComparer.Ordinal.Equals(x.ToState, y.ToState);

        public int GetHashCode((string Action, string FromState, string ToState) value) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.Action),
                StringComparer.Ordinal.GetHashCode(value.FromState),
                StringComparer.Ordinal.GetHashCode(value.ToState));
    }
}
