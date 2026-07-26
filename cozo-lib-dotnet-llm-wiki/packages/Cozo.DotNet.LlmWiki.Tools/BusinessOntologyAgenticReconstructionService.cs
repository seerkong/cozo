using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cozo.DotNet.LlmWiki.LlmClient;

namespace Cozo.DotNet.LlmWiki.Tools;

internal static class BusinessOntologyAgentPhases
{
    public const string Explore = "explore";
    public const string Verify = "verify";
    public const string Reconcile = "reconcile";
    public const string Model = "model";
}

internal static class BusinessOntologyAgentRunStatuses
{
    public const string Running = "running";
    public const string Finished = "finished";
    public const string Rejected = "rejected";
    public const string BudgetExhausted = "budget_exhausted";
    public const string Blocked = "blocked";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
}

internal sealed record BusinessOntologyAgentRunRequest(
    string RunId,
    IBusinessOntologyAgentActionSource ActionSource,
    BusinessOntologyAgentBudgetLimits? BudgetLimits = null,
    DateTimeOffset? StartedAtUtc = null,
    BusinessOntologyAgentPersistenceContext? Persistence = null,
    string? WorkItem = null);

internal sealed record BusinessOntologyAgentPersistenceContext(
    BusinessOntologyAnalysisStore Store,
    BusinessOntologyAnalysisRunInput Run);

internal sealed record BusinessOntologyAgentActionSourceContext(
    string RunId,
    string TurnId,
    string WorkItem,
    string Phase,
    BusinessOntologyAgentBudgetState BudgetState,
    IReadOnlyList<string> KnownQueryDigests,
    IReadOnlyList<string> KnownEvidenceIds,
    IReadOnlyList<BusinessOntologyAgentQueryObservation> Queries,
    IReadOnlyList<BusinessOntologyAnalysisRecordInput> Records);

internal interface IBusinessOntologyAgentActionSource
{
    Task<string> NextActionJsonAsync(BusinessOntologyAgentActionSourceContext context, CancellationToken cancellationToken = default);
}

internal interface IBusinessOntologyAgentCompletionMetadataSource
{
    LlmUsage? LastUsage { get; }
    string? LastModel { get; }
}

internal sealed class BusinessOntologyAgentSequenceActionSource(IReadOnlyList<Func<BusinessOntologyAgentActionSourceContext, string>> actions)
    : IBusinessOntologyAgentActionSource
{
    private int _index;

    public Task<string> NextActionJsonAsync(BusinessOntologyAgentActionSourceContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_index >= actions.Count)
        {
            throw new InvalidOperationException("The deterministic action sequence is exhausted.");
        }

        return Task.FromResult(actions[_index++](context));
    }
}

internal sealed class BusinessOntologyAgentLlmActionSource(
    ILlmClient client,
    LlmOptions? options = null)
    : IBusinessOntologyAgentActionSource, IBusinessOntologyAgentCompletionMetadataSource
{
    private static readonly JsonSerializerOptions PromptOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public LlmUsage? LastUsage { get; private set; }
    public string? LastModel { get; private set; }
    internal IReadOnlyList<string> UserPrompts => _userPrompts;

    private readonly List<string> _userPrompts = [];

    public async Task<string> NextActionJsonAsync(
        BusinessOntologyAgentActionSourceContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!client.IsAvailable)
        {
            throw new LlmException(client.UnavailableReason ?? "LLM client is unavailable.");
        }

        using var schema = BusinessOntologyAgentActionContract.SchemaJson();
        var userPrompt = JsonSerializer.Serialize(new
        {
            task = "Return exactly one strict JSON business ontology agent action.",
            actionSchema = schema.RootElement,
            state = new
            {
                context.RunId,
                context.TurnId,
                context.WorkItem,
                context.Phase,
                budget = new
                {
                    context.BudgetState.TurnsUsed,
                    context.BudgetState.QueriesUsed,
                    context.BudgetState.RowsRead,
                    context.BudgetState.SourceBytesRead,
                    context.BudgetState.InputTokensUsed,
                    context.BudgetState.OutputTokensUsed,
                    context.BudgetState.InFlightEffects,
                },
                context.KnownQueryDigests,
                context.KnownEvidenceIds,
            },
            querySummaries = context.Queries.Select(query => new
            {
                query.TurnId,
                query.Operation,
                query.ParametersJson,
                query.QueryDigest,
                query.Rows,
                query.SourceBytes,
                EvidenceIds = query.EvidenceRefs.Select(evidence => evidence.EvidenceId).ToArray(),
                ResultSummary = BusinessOntologyAgenticReconstructionService.QueryResultSummary(query.Result),
            }).ToArray(),
            recordSummaries = context.Records.Select(record => new
            {
                record.RecordId,
                record.Kind,
                record.SubjectKind,
                record.SubjectId,
                record.Status,
                record.Uncertainty,
                record.QueryDigest,
                record.EvidenceIds,
            }).ToArray(),
        }, PromptOptions);

        _userPrompts.Add(userPrompt);
        var completion = await client.CompleteAsync(
            "You are a bounded business ontology reconstruction action source. Use only the provided schema, state, and query summaries. Return JSON only.",
            userPrompt,
            options ?? new LlmOptions(MaxTokens: 2048, Temperature: 0),
            cancellationToken);
        LastUsage = completion.Usage;
        LastModel = completion.Model;
        return completion.Text;
    }
}

internal sealed record BusinessOntologyAgentQueryObservation(
    string TurnId,
    string Operation,
    string ParametersJson,
    string QueryDigest,
    IReadOnlyList<BusinessOntologyInvestigationEvidenceRef> EvidenceRefs,
    long Rows,
    long SourceBytes,
    object Result);

internal sealed record BusinessOntologyAgentFinish(
    string TurnId,
    string Status,
    string Reason,
    IReadOnlyList<string> Unresolved);

internal sealed record BusinessOntologyAgentStep(
    string TurnId,
    string PhaseBefore,
    string PhaseAfter,
    string Action,
    string Outcome);

internal sealed record BusinessOntologyAgentPendingSynthesis(
    IReadOnlyList<BusinessOntologySemanticDomainCharter> DomainCharters,
    IReadOnlyList<BusinessOntologySemanticCluster> Clusters);

internal sealed record BusinessOntologyAgentRunResult(
    string Status,
    string Phase,
    BusinessOntologyAgentBudgetState BudgetState,
    IReadOnlyList<BusinessOntologyAgentQueryObservation> Queries,
    IReadOnlyList<BusinessOntologyAnalysisRecordInput> Records,
    BusinessOntologyAgentFinish? Finish,
    BusinessOntologyAgentBudgetRejection? BudgetRejection,
    string? RejectionReason,
    IReadOnlyList<BusinessOntologyAgentStep> Steps,
    IReadOnlyList<BusinessOntologyAgentPendingSynthesis> PendingSyntheses);

internal interface IBusinessOntologyInvestigationOperations
{
    Task<BusinessOntologyInvestigationOverview> GetOverviewAsync(string? ontologyId = null, CancellationToken cancellationToken = default);
    Task<BusinessOntologyInvestigationPage<BusinessTermHit>> FindBusinessTermsAsync(string term, string? ontologyId = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern>> FindSemanticPatternsAsync(string kind, string? term = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<SemanticEvidencePack> GetSemanticEvidenceAsync(IReadOnlyList<string> evidenceIds, CancellationToken cancellationToken = default);
    Task<BusinessOntologyInvestigationPage<BusinessUseCaseSlice>> ListUseCaseSlicesAsync(string ontologyId, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<BusinessUseCaseSlice> GetUseCaseSliceAsync(string ontologyId, string sliceId, CancellationToken cancellationToken = default);
    Task<BusinessOntologySubjectInspection> InspectOntologySubjectAsync(string ontologyId, string subjectKind, string subjectId, CancellationToken cancellationToken = default);
    Task<BusinessOntologyInvestigationPage<BusinessOntologyDiscoveredDomainCharter>> DiscoverDomainChartersAsync(string term, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<BusinessOntologyInvestigationPage<BusinessOntologyCrossLayerUseCase>> ListCrossLayerUseCasesAsync(string? entrySymbolId = null, string? domainSeed = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<BusinessOntologyInvestigationPage<BusinessOntologyStateRuleCluster>> FindStateRuleClustersAsync(string term, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<BusinessOntologyInvestigationPage<BusinessOntologyImplementationCluster>> FindImplementationClustersAsync(string? domainSeed = null, IReadOnlyList<string>? evidenceIds = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default);
}

internal sealed class BusinessOntologyInvestigationServiceOperations(BusinessOntologyInvestigationService service)
    : IBusinessOntologyInvestigationOperations
{
    public Task<BusinessOntologyInvestigationOverview> GetOverviewAsync(string? ontologyId = null, CancellationToken cancellationToken = default) =>
        service.GetOverviewAsync(ontologyId, cancellationToken);

    public Task<BusinessOntologyInvestigationPage<BusinessTermHit>> FindBusinessTermsAsync(string term, string? ontologyId = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        service.FindBusinessTermsAsync(term, ontologyId, cursor, limit, cancellationToken);

    public Task<BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern>> FindSemanticPatternsAsync(string kind, string? term = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        service.FindSemanticPatternsAsync(kind, term, cursor, limit, cancellationToken);

    public Task<SemanticEvidencePack> GetSemanticEvidenceAsync(IReadOnlyList<string> evidenceIds, CancellationToken cancellationToken = default) =>
        service.GetSemanticEvidenceAsync(evidenceIds, cancellationToken);

    public Task<BusinessOntologyInvestigationPage<BusinessUseCaseSlice>> ListUseCaseSlicesAsync(string ontologyId, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        service.ListUseCaseSlicesAsync(ontologyId, cursor, limit, cancellationToken);

    public Task<BusinessUseCaseSlice> GetUseCaseSliceAsync(string ontologyId, string sliceId, CancellationToken cancellationToken = default) =>
        service.GetUseCaseSliceAsync(ontologyId, sliceId, cancellationToken);

    public Task<BusinessOntologySubjectInspection> InspectOntologySubjectAsync(string ontologyId, string subjectKind, string subjectId, CancellationToken cancellationToken = default) =>
        service.InspectOntologySubjectAsync(ontologyId, subjectKind, subjectId, cancellationToken);

    public Task<BusinessOntologyInvestigationPage<BusinessOntologyDiscoveredDomainCharter>> DiscoverDomainChartersAsync(string term, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        service.DiscoverDomainChartersAsync(term, cursor, limit, cancellationToken);

    public Task<BusinessOntologyInvestigationPage<BusinessOntologyCrossLayerUseCase>> ListCrossLayerUseCasesAsync(string? entrySymbolId = null, string? domainSeed = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        service.ListCrossLayerUseCasesAsync(entrySymbolId, domainSeed, cursor, limit, cancellationToken);

    public Task<BusinessOntologyInvestigationPage<BusinessOntologyStateRuleCluster>> FindStateRuleClustersAsync(string term, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        service.FindStateRuleClustersAsync(term, cursor, limit, cancellationToken);

    public Task<BusinessOntologyInvestigationPage<BusinessOntologyImplementationCluster>> FindImplementationClustersAsync(string? domainSeed = null, IReadOnlyList<string>? evidenceIds = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        service.FindImplementationClustersAsync(domainSeed, evidenceIds, cursor, limit, cancellationToken);
}

internal sealed class BusinessOntologyAgenticReconstructionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IBusinessOntologyInvestigationOperations _investigation;
    private readonly BusinessOntologyAgentActionValidator _validator = new();

    public BusinessOntologyAgenticReconstructionService(BusinessOntologyInvestigationService investigation)
        : this(new BusinessOntologyInvestigationServiceOperations(investigation))
    {
    }

    internal BusinessOntologyAgenticReconstructionService(IBusinessOntologyInvestigationOperations investigation)
    {
        _investigation = investigation;
    }

    public async Task<BusinessOntologyAgentRunResult> RunAsync(
        BusinessOntologyAgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentNullException.ThrowIfNull(request.ActionSource);
        if (request.Persistence is { } persistence)
        {
            ArgumentNullException.ThrowIfNull(persistence.Store);
            ArgumentNullException.ThrowIfNull(persistence.Run);
            if (!StringComparer.Ordinal.Equals(persistence.Run.RunId, request.RunId))
            {
                throw new ArgumentException("Persistence run identity must match the active agent run.", nameof(request));
            }
        }

        var limits = request.BudgetLimits ?? BusinessOntologyAgentBudgetLimits.Default;
        var startedAt = request.StartedAtUtc ?? DateTimeOffset.UtcNow;
        var state = BusinessOntologyAgentBudgetState.Start(startedAt);
        var phase = BusinessOntologyAgentPhases.Explore;
        var queries = new List<BusinessOntologyAgentQueryObservation>();
        var evidenceIdsByQueryDigest = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var evidenceRefsById = new Dictionary<string, BusinessOntologyInvestigationEvidenceRef>(StringComparer.Ordinal);
        var records = new List<BusinessOntologyAnalysisRecordInput>();
        var steps = new List<BusinessOntologyAgentStep>();
        var pendingSyntheses = new List<BusinessOntologyAgentPendingSynthesis>();
        BusinessOntologyAgentFinish? finish = null;
        var persistenceInitialized = false;

        async Task<BusinessOntologyAgentRunResult> CompleteTerminalAsync(BusinessOntologyAgentRunResult result)
        {
            if (!persistenceInitialized || request.Persistence is not { } persistence)
            {
                return result;
            }

            await persistence.Store.AppendRunCompletionAsync(
                new BusinessOntologyAnalysisRunCompletionInput(
                    request.RunId,
                    result.Status,
                    TerminalCompletedAt(startedAt, result).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    result.Queries.Count,
                    result.Records.Count,
                    result.Steps.Count(step => StringComparer.Ordinal.Equals(step.Outcome, "rejected")),
                    result.BudgetRejection?.Metric ?? ""),
                CancellationToken.None);
            return result;
        }

        async Task<BusinessOntologyAgentRunResult> CompleteWithBudgetAsync(
            string runId,
            string phase,
            BusinessOntologyAgentBudgetState state,
            IReadOnlyList<BusinessOntologyAgentQueryObservation> queries,
            List<BusinessOntologyAnalysisRecordInput> records,
            BusinessOntologyAgentFinish? finish,
            BusinessOntologyAgentBudgetRejection? rejection,
            IReadOnlyList<BusinessOntologyAgentStep> steps,
            IReadOnlyList<BusinessOntologyAgentPendingSynthesis> pendingSyntheses,
            BusinessOntologyAgentPersistenceContext? persistence,
            DateTimeOffset nowUtc,
            CancellationToken persistenceCancellationToken)
        {
            var budgetFinish = finish ?? new BusinessOntologyAgentFinish(
                "local:budget",
                BusinessOntologyAgentFinishStatuses.BudgetExhausted,
                $"Budget exhausted for run '{runId}' before accepting another effect.",
                rejection is null ? [] : [$"{rejection.Metric}: {rejection.Audit}"]);
            var rejectionReason = rejection?.Audit;

            if (persistence is not null
                && pendingSyntheses.Count == 0
                && TryCreateBudgetGapRecord(runId, phase, queries, rejection, nowUtc, out var gap)
                && !records.Any(record => StringComparer.Ordinal.Equals(record.RecordId, gap.RecordId)))
            {
                try
                {
                    await persistence.Store.AppendRunAsync(persistence.Run, persistenceCancellationToken);
                    persistenceInitialized = true;
                    await persistence.Store.AppendRecordsAsync([gap], persistenceCancellationToken);
                    records.Add(gap);
                }
                catch (OperationCanceledException) when (persistenceCancellationToken.IsCancellationRequested)
                {
                    return await CompleteTerminalAsync(CompleteWithFailure(
                        BusinessOntologyAgentRunStatuses.Cancelled,
                        phase,
                        state,
                        queries,
                        records,
                        budgetFinish,
                        rejection,
                        "external cancellation interrupted local budget-gap persistence",
                        steps,
                        pendingSyntheses));
                }
                catch (Exception ex)
                {
                    rejectionReason = rejectionReason is null
                        ? "budget gap persistence failed: " + ex.Message
                        : rejectionReason + "; budget gap persistence failed: " + ex.Message;
                }
            }

            return await CompleteTerminalAsync(new BusinessOntologyAgentRunResult(
                BusinessOntologyAgentRunStatuses.BudgetExhausted,
                phase,
                state,
                queries,
                records,
                budgetFinish,
                rejection,
                rejectionReason,
                steps,
                pendingSyntheses));
        }

        while (finish is null)
        {
            var turnId = "turn:" + (state.TurnsUsed + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var nowUtc = startedAt + TimeSpan.FromSeconds(state.TurnsUsed + queries.Count + records.Count + 1);
            if (TryFindExhaustedBudget(limits, state, nowUtc, out var exhausted))
            {
                return await CompleteWithBudgetAsync(
                    request.RunId,
                    phase,
                    state,
                    queries,
                    records,
                    finish,
                    exhausted,
                    steps,
                    pendingSyntheses,
                    request.Persistence,
                    nowUtc,
                    cancellationToken);
            }

            var sourceContext = Context(request.RunId, turnId, request.WorkItem ?? "", phase, state, queries, records);
            var prompt = JsonSerializer.Serialize(sourceContext, JsonOptions);
            var preflightUsage = BusinessOntologyAgentTokenUsage.FromProviderOrEstimate(
                null,
                prompt,
                "",
                "agentic ontology deterministic action-source preflight");

            if (!BusinessOntologyAgentBudget.TryReserveTurn(limits, state, nowUtc, preflightUsage, out var turnReservation, out var turnRejection))
            {
                return await CompleteWithBudgetAsync(
                    request.RunId,
                    phase,
                    state,
                    queries,
                    records,
                    finish,
                    turnRejection,
                    steps,
                    pendingSyntheses,
                    request.Persistence,
                    nowUtc,
                    cancellationToken);
            }

            string actionJson;
            try
            {
                using var effectCancellation = CreateEffectCancellation(limits, state, nowUtc, cancellationToken);
                actionJson = await request.ActionSource.NextActionJsonAsync(sourceContext, effectCancellation.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                steps.Add(new BusinessOntologyAgentStep(turnId, phase, phase, "source", "cancelled"));
                return await CompleteTerminalAsync(CompleteWithFailure(
                    BusinessOntologyAgentRunStatuses.Cancelled,
                    phase,
                    turnReservation.State.BeforeReservation,
                    queries,
                    records,
                    finish,
                    null,
                    "external cancellation interrupted the action source before accepting another effect",
                    steps,
                    pendingSyntheses));
            }
            catch (OperationCanceledException)
            {
                steps.Add(new BusinessOntologyAgentStep(turnId, phase, phase, "source", "timed_out"));
                return await CompleteTerminalAsync(CompleteWithFailure(
                    BusinessOntologyAgentRunStatuses.TimedOut,
                    phase,
                    turnReservation.State.BeforeReservation,
                    queries,
                    records,
                    finish,
                    WallClockRejection(limits, state, nowUtc),
                    "wall-clock timeout interrupted the action source before accepting another effect",
                    steps,
                    pendingSyntheses));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                steps.Add(new BusinessOntologyAgentStep(turnId, phase, phase, "source", "blocked"));
                return await CompleteTerminalAsync(CompleteWithFailure(
                    BusinessOntologyAgentRunStatuses.Blocked,
                    phase,
                    turnReservation.State.BeforeReservation,
                    queries,
                    records,
                    finish,
                    null,
                    ex.Message,
                    steps,
                    pendingSyntheses));
            }

            var turnCommit = BusinessOntologyAgentBudget.CommitTurnCompletion(
                turnReservation,
                BusinessOntologyAgentTokenUsage.FromProviderOrEstimate(
                    request.ActionSource is IBusinessOntologyAgentCompletionMetadataSource metadata ? metadata.LastUsage : null,
                    prompt,
                    actionJson,
                    "agentic ontology deterministic action-source output"));
            state = turnCommit.State;
            if (!turnCommit.Accepted)
            {
                return await CompleteWithBudgetAsync(
                    request.RunId,
                    phase,
                    state,
                    queries,
                    records,
                    finish,
                    turnCommit.Rejection,
                    steps,
                    pendingSyntheses,
                    request.Persistence,
                    nowUtc,
                    cancellationToken);
            }

            ValidatedBusinessOntologyAgentAction action;
            try
            {
                action = _validator.Validate(
                    actionJson,
                    new BusinessOntologyAgentActionValidationContext(
                        request.RunId,
                        queries.SelectMany(query => query.EvidenceRefs.Select(item => item.EvidenceId)).ToHashSet(StringComparer.Ordinal),
                        queries.Select(query => query.QueryDigest).ToHashSet(StringComparer.Ordinal),
                        nowUtc,
                        evidenceIdsByQueryDigest,
                        evidenceRefsById));
            }
            catch (ArgumentException ex)
            {
                steps.Add(new BusinessOntologyAgentStep(turnId, phase, phase, "validate", "rejected"));
                return await CompleteTerminalAsync(new BusinessOntologyAgentRunResult(
                    BusinessOntologyAgentRunStatuses.Rejected,
                    phase,
                    state,
                    queries,
                    records,
                    finish,
                    null,
                    ex.Message,
                    steps,
                    pendingSyntheses));
            }

            var phaseBefore = phase;
            switch (action)
            {
                case ValidatedBusinessOntologyQueryAction query:
                {
                    if (!BusinessOntologyAgentBudget.TryReserveQuery(
                            limits,
                            state,
                            nowUtc + TimeSpan.FromMilliseconds(1),
                            MaxRowsFor(query),
                            MaxSourceBytesFor(limits, state, query),
                            out var queryReservation,
                            out var queryRejection))
                    {
                        return await CompleteWithBudgetAsync(
                            request.RunId,
                            phase,
                            state,
                            queries,
                            records,
                            finish,
                            queryRejection,
                            steps,
                            pendingSyntheses,
                            request.Persistence,
                            nowUtc,
                            cancellationToken);
                    }

                    BusinessOntologyAgentQueryObservation observation;
                    try
                    {
                        using var effectCancellation = CreateEffectCancellation(limits, state, nowUtc, cancellationToken);
                        observation = await DispatchQueryAsync(query, effectCancellation.Token);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        steps.Add(new BusinessOntologyAgentStep(turnId, phase, phase, BusinessOntologyAgentActionKinds.Query, "cancelled"));
                        return await CompleteTerminalAsync(CompleteWithFailure(
                            BusinessOntologyAgentRunStatuses.Cancelled,
                            phase,
                            queryReservation.State.BeforeReservation,
                            queries,
                            records,
                            finish,
                            null,
                            "external cancellation interrupted the query before accepting its observation",
                            steps,
                            pendingSyntheses));
                    }
                    catch (OperationCanceledException)
                    {
                        steps.Add(new BusinessOntologyAgentStep(turnId, phase, phase, BusinessOntologyAgentActionKinds.Query, "timed_out"));
                        return await CompleteTerminalAsync(CompleteWithFailure(
                            BusinessOntologyAgentRunStatuses.TimedOut,
                            phase,
                            queryReservation.State.BeforeReservation,
                            queries,
                            records,
                            finish,
                            WallClockRejection(limits, state, nowUtc),
                            "wall-clock timeout interrupted the query before accepting its observation",
                            steps,
                            pendingSyntheses));
                    }
                    catch (Exception ex)
                    {
                        steps.Add(new BusinessOntologyAgentStep(turnId, phase, phase, BusinessOntologyAgentActionKinds.Query, "failed"));
                        return await CompleteTerminalAsync(CompleteWithFailure(
                            BusinessOntologyAgentRunStatuses.Blocked,
                            phase,
                            queryReservation.State.BeforeReservation,
                            queries,
                            records,
                            finish,
                            null,
                            ex.Message,
                            steps,
                            pendingSyntheses));
                    }

                    var queryCommit = BusinessOntologyAgentBudget.CommitQueryResult(
                        queryReservation,
                        observation.Rows,
                        observation.SourceBytes);
                    state = queryCommit.State;
                    if (!queryCommit.Accepted)
                    {
                        return await CompleteWithBudgetAsync(
                            request.RunId,
                            phase,
                            state,
                            queries,
                            records,
                            finish,
                            queryCommit.Rejection,
                            steps,
                            pendingSyntheses,
                            request.Persistence,
                            nowUtc,
                            cancellationToken);
                    }

                    queries.Add(observation);
                    foreach (var evidenceRef in observation.EvidenceRefs)
                    {
                        // Evidence identity binds to its first accepted coordinates for this run.
                        evidenceRefsById.TryAdd(evidenceRef.EvidenceId, evidenceRef);
                    }
                    evidenceIdsByQueryDigest.TryAdd(
                        observation.QueryDigest,
                        observation.EvidenceRefs.Select(item => item.EvidenceId).ToHashSet(StringComparer.Ordinal));
                    phase = NextPhaseAfterQuery(phase);
                    steps.Add(new BusinessOntologyAgentStep(turnId, phaseBefore, phase, BusinessOntologyAgentActionKinds.Query, "accepted"));
                    break;
                }

                case ValidatedBusinessOntologyRecordAction record:
                    if (pendingSyntheses.Count != 0)
                    {
                        steps.Add(new BusinessOntologyAgentStep(turnId, phase, phase, BusinessOntologyAgentActionKinds.Record, "rejected"));
                        return await CompleteTerminalAsync(CompleteWithFailure(
                            BusinessOntologyAgentRunStatuses.Rejected,
                            phase,
                            state,
                            queries,
                            records,
                            finish,
                            null,
                            "record actions are not allowed after a synthesize action is accepted",
                            steps,
                            pendingSyntheses));
                    }
                    if (request.Persistence is { } recordPersistence)
                    {
                        try
                        {
                            await recordPersistence.Store.AppendRunAsync(recordPersistence.Run, cancellationToken);
                            persistenceInitialized = true;
                            await recordPersistence.Store.AppendRecordsAsync(record.Records, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            steps.Add(new BusinessOntologyAgentStep(turnId, phase, phase, BusinessOntologyAgentActionKinds.Record, "cancelled"));
                            return await CompleteTerminalAsync(CompleteWithFailure(
                                BusinessOntologyAgentRunStatuses.Cancelled,
                                phase,
                                state,
                                queries,
                                records,
                                finish,
                                null,
                                "external cancellation interrupted analysis record append before accepting the batch",
                                steps,
                                pendingSyntheses));
                        }
                        catch (Exception ex)
                        {
                            steps.Add(new BusinessOntologyAgentStep(turnId, phase, phase, BusinessOntologyAgentActionKinds.Record, "failed"));
                            return await CompleteTerminalAsync(CompleteWithFailure(
                                BusinessOntologyAgentRunStatuses.Blocked,
                                phase,
                                state,
                                queries,
                                records,
                                finish,
                                null,
                                ex.Message,
                                steps,
                                pendingSyntheses));
                        }
                    }
                    records.AddRange(record.Records);
                    phase = NextPhaseAfterRecord(phase, record.Records);
                    steps.Add(new BusinessOntologyAgentStep(turnId, phaseBefore, phase, BusinessOntologyAgentActionKinds.Record, "accepted"));
                    break;

                case ValidatedBusinessOntologySynthesizeAction synthesize:
                    if (records.Count != 0)
                    {
                        steps.Add(new BusinessOntologyAgentStep(turnId, phase, phase, BusinessOntologyAgentActionKinds.Synthesize, "rejected"));
                        return await CompleteTerminalAsync(CompleteWithFailure(
                            BusinessOntologyAgentRunStatuses.Rejected,
                            phase,
                            state,
                            queries,
                            records,
                            finish,
                            null,
                            "synthesize actions require a run without accepted analysis records",
                            steps,
                            pendingSyntheses));
                    }
                    pendingSyntheses.Add(new BusinessOntologyAgentPendingSynthesis(
                        synthesize.DomainCharters,
                        synthesize.Clusters));
                    phase = BusinessOntologyAgentPhases.Model;
                    steps.Add(new BusinessOntologyAgentStep(turnId, phaseBefore, phase, BusinessOntologyAgentActionKinds.Synthesize, "accepted"));
                    break;

                case ValidatedBusinessOntologyFinishAction done:
                    finish = new BusinessOntologyAgentFinish(done.Identity.TurnId, done.Status, done.Reason, done.Unresolved);
                    steps.Add(new BusinessOntologyAgentStep(turnId, phaseBefore, phase, BusinessOntologyAgentActionKinds.Finish, "accepted"));
                    break;
            }
        }

        return await CompleteTerminalAsync(new BusinessOntologyAgentRunResult(
            BusinessOntologyAgentRunStatuses.Finished,
            phase,
            state,
            queries,
            records,
            finish,
            null,
            null,
            steps,
            pendingSyntheses));
    }

    private static BusinessOntologyAgentActionSourceContext Context(
        string runId,
        string turnId,
        string workItem,
        string phase,
        BusinessOntologyAgentBudgetState state,
        IReadOnlyList<BusinessOntologyAgentQueryObservation> queries,
        IReadOnlyList<BusinessOntologyAnalysisRecordInput> records) =>
        new(
            runId,
            turnId,
            workItem,
            phase,
            state,
            queries.Select(query => query.QueryDigest).Order(StringComparer.Ordinal).ToArray(),
            queries.SelectMany(query => query.EvidenceRefs.Select(item => item.EvidenceId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            queries.ToArray(),
            records.ToArray());

    private async Task<BusinessOntologyAgentQueryObservation> DispatchQueryAsync(
        ValidatedBusinessOntologyQueryAction query,
        CancellationToken cancellationToken)
    {
        using var parametersDocument = JsonDocument.Parse(query.ParametersJson);
        var parameters = parametersDocument.RootElement;
        object result = query.Operation switch
        {
            BusinessOntologyAgentQueryOperations.OntologyInvestigationOverview =>
                await _investigation.GetOverviewAsync(OptionalString(parameters, "ontologyId"), cancellationToken),
            BusinessOntologyAgentQueryOperations.FindBusinessTerms =>
                await _investigation.FindBusinessTermsAsync(
                    RequiredString(parameters, "term"),
                    OptionalString(parameters, "ontologyId"),
                    OptionalString(parameters, "cursor"),
                    OptionalInt(parameters, "limit"),
                    cancellationToken),
            BusinessOntologyAgentQueryOperations.ListUseCaseSlices =>
                await _investigation.ListUseCaseSlicesAsync(
                    RequiredString(parameters, "ontologyId"),
                    OptionalString(parameters, "cursor"),
                    OptionalInt(parameters, "limit"),
                    cancellationToken),
            BusinessOntologyAgentQueryOperations.GetUseCaseSlice =>
                await _investigation.GetUseCaseSliceAsync(
                    RequiredString(parameters, "ontologyId"),
                    RequiredString(parameters, "sliceId"),
                    cancellationToken),
            BusinessOntologyAgentQueryOperations.FindSemanticPatterns =>
                await _investigation.FindSemanticPatternsAsync(
                    RequiredString(parameters, "kind"),
                    OptionalString(parameters, "term"),
                    OptionalString(parameters, "cursor"),
                    OptionalInt(parameters, "limit"),
                    cancellationToken),
            BusinessOntologyAgentQueryOperations.GetSemanticEvidence =>
                await _investigation.GetSemanticEvidenceAsync(StringArray(parameters, "evidenceIds"), cancellationToken),
            BusinessOntologyAgentQueryOperations.InspectOntologySubject =>
                await _investigation.InspectOntologySubjectAsync(
                    RequiredString(parameters, "ontologyId"),
                    RequiredString(parameters, "subjectKind"),
                    RequiredString(parameters, "subjectId"),
                    cancellationToken),
            BusinessOntologyAgentQueryOperations.DiscoverDomainCharters =>
                await _investigation.DiscoverDomainChartersAsync(
                    RequiredString(parameters, "term"),
                    OptionalString(parameters, "cursor"),
                    OptionalInt(parameters, "limit"),
                    cancellationToken),
            BusinessOntologyAgentQueryOperations.ListCrossLayerUseCases =>
                await _investigation.ListCrossLayerUseCasesAsync(
                    OptionalString(parameters, "entrySymbolId"),
                    OptionalString(parameters, "domainSeed"),
                    OptionalString(parameters, "cursor"),
                    OptionalInt(parameters, "limit"),
                    cancellationToken),
            BusinessOntologyAgentQueryOperations.FindStateRuleClusters =>
                await _investigation.FindStateRuleClustersAsync(
                    RequiredString(parameters, "term"),
                    OptionalString(parameters, "cursor"),
                    OptionalInt(parameters, "limit"),
                    cancellationToken),
            BusinessOntologyAgentQueryOperations.FindImplementationClusters =>
                await _investigation.FindImplementationClustersAsync(
                    OptionalString(parameters, "domainSeed"),
                    OptionalStringArray(parameters, "evidenceIds"),
                    OptionalString(parameters, "cursor"),
                    OptionalInt(parameters, "limit"),
                    cancellationToken),
            _ => throw new ArgumentException($"Unknown business ontology investigation operation '{query.Operation}'.", nameof(query)),
        };

        return new BusinessOntologyAgentQueryObservation(
            query.Identity.TurnId,
            query.Operation,
            query.ParametersJson,
            QueryDigest(result, query.Operation, query.ParametersJson),
            EvidenceRefs(result),
            Rows(result),
            SourceBytes(result),
            result);
    }

    private static long MaxRowsFor(ValidatedBusinessOntologyQueryAction query)
    {
        using var parametersDocument = JsonDocument.Parse(query.ParametersJson);
        var parameters = parametersDocument.RootElement;
        return query.Operation switch
        {
            BusinessOntologyAgentQueryOperations.GetSemanticEvidence => StringArray(parameters, "evidenceIds").Count,
            BusinessOntologyAgentQueryOperations.GetUseCaseSlice or BusinessOntologyAgentQueryOperations.InspectOntologySubject or BusinessOntologyAgentQueryOperations.OntologyInvestigationOverview => 1,
            _ => OptionalInt(parameters, "limit") ?? BusinessOntologyInvestigationService.DefaultPageSize,
        };
    }

    private static long MaxSourceBytesFor(
        BusinessOntologyAgentBudgetLimits limits,
        BusinessOntologyAgentBudgetState state,
        ValidatedBusinessOntologyQueryAction query)
    {
        var remaining = Math.Max(0, limits.MaxSourceBytes - state.SourceBytesRead);
        return query.Operation == BusinessOntologyAgentQueryOperations.GetSemanticEvidence
            ? Math.Min(remaining, SemanticEvidencePackBuilder.MaxSourceUtf8Bytes)
            : remaining;
    }

    private static string NextPhaseAfterQuery(string phase) =>
        phase == BusinessOntologyAgentPhases.Explore ? BusinessOntologyAgentPhases.Verify : phase;

    private static string NextPhaseAfterRecord(string phase, IReadOnlyList<BusinessOntologyAnalysisRecordInput> records) =>
        records.Any(record => record.Kind == "candidate_draft")
            ? BusinessOntologyAgentPhases.Model
            : phase is BusinessOntologyAgentPhases.Explore or BusinessOntologyAgentPhases.Verify
                ? BusinessOntologyAgentPhases.Reconcile
                : phase;

    private static bool TryFindExhaustedBudget(
        BusinessOntologyAgentBudgetLimits limits,
        BusinessOntologyAgentBudgetState state,
        DateTimeOffset nowUtc,
        out BusinessOntologyAgentBudgetRejection? rejection)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(state);
        var elapsed = nowUtc < state.StartedAtUtc ? TimeSpan.Zero : nowUtc - state.StartedAtUtc;
        if (elapsed >= limits.MaxWallClock)
        {
            rejection = new BusinessOntologyAgentBudgetRejection(
                BusinessOntologyAgentBudgetMetrics.WallClock,
                (long)limits.MaxWallClock.TotalMilliseconds,
                (long)elapsed.TotalMilliseconds,
                0,
                "wall-clock budget already exhausted before the next model/query effect");
            return true;
        }

        foreach (var item in new[]
                 {
                     (BusinessOntologyAgentBudgetMetrics.Turns, limits.MaxTurns, state.TurnsUsed),
                     (BusinessOntologyAgentBudgetMetrics.Queries, limits.MaxQueries, state.QueriesUsed),
                     (BusinessOntologyAgentBudgetMetrics.Rows, limits.MaxRows, state.RowsRead),
                     (BusinessOntologyAgentBudgetMetrics.SourceBytes, limits.MaxSourceBytes, state.SourceBytesRead),
                     (BusinessOntologyAgentBudgetMetrics.InputTokens, limits.MaxInputTokens, state.InputTokensUsed),
                     (BusinessOntologyAgentBudgetMetrics.OutputTokens, limits.MaxOutputTokens, state.OutputTokensUsed),
                     (BusinessOntologyAgentBudgetMetrics.Concurrency, limits.MaxConcurrency, state.InFlightEffects),
                 })
        {
            if (item.Item3 >= item.Item2)
            {
                rejection = new BusinessOntologyAgentBudgetRejection(
                    item.Item1,
                    item.Item2,
                    item.Item3,
                    0,
                    $"{item.Item1} budget already exhausted before the next model/query effect");
                return true;
            }
        }

        rejection = null;
        return false;
    }

    private static CancellationTokenSource CreateEffectCancellation(
        BusinessOntologyAgentBudgetLimits limits,
        BusinessOntologyAgentBudgetState state,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var elapsed = nowUtc < state.StartedAtUtc ? TimeSpan.Zero : nowUtc - state.StartedAtUtc;
        var remaining = limits.MaxWallClock - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            linked.Cancel();
        }
        else
        {
            linked.CancelAfter(remaining);
        }
        return linked;
    }

    private static BusinessOntologyAgentBudgetRejection WallClockRejection(
        BusinessOntologyAgentBudgetLimits limits,
        BusinessOntologyAgentBudgetState state,
        DateTimeOffset nowUtc)
    {
        var elapsed = nowUtc < state.StartedAtUtc ? TimeSpan.Zero : nowUtc - state.StartedAtUtc;
        return new BusinessOntologyAgentBudgetRejection(
            BusinessOntologyAgentBudgetMetrics.WallClock,
            (long)limits.MaxWallClock.TotalMilliseconds,
            (long)elapsed.TotalMilliseconds,
            1,
            "wall-clock timeout interrupted an in-flight effect");
    }

    private static DateTimeOffset TerminalCompletedAt(
        DateTimeOffset startedAt,
        BusinessOntologyAgentRunResult result) =>
        startedAt + TimeSpan.FromSeconds(Math.Max(
            1L,
            result.BudgetState.TurnsUsed + result.Queries.Count + result.Records.Count + result.Steps.Count));

    private static bool TryCreateBudgetGapRecord(
        string runId,
        string phase,
        IReadOnlyList<BusinessOntologyAgentQueryObservation> queries,
        BusinessOntologyAgentBudgetRejection? rejection,
        DateTimeOffset nowUtc,
        out BusinessOntologyAnalysisRecordInput gap)
    {
        var boundaryQuery = queries.LastOrDefault(query => query.EvidenceRefs.Count != 0);
        if (boundaryQuery is null)
        {
            gap = null!;
            return false;
        }

        var evidenceIds = boundaryQuery.EvidenceRefs
            .Select(item => item.EvidenceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (evidenceIds.Length == 0)
        {
            gap = null!;
            return false;
        }

        var metric = rejection?.Metric ?? "unknown";
        gap = new BusinessOntologyAnalysisRecordInput(
            runId,
            "gap:budget:" + metric.Replace('_', '-'),
            "gap",
            "unknown",
            "unknown",
            "Budget exhausted before accepting another effect",
            JsonSerializer.Serialize(new
            {
                source = "local_controller",
                reason = "budget_exhausted",
                phase,
                metric,
                limit = rejection?.Limit,
                used = rejection?.Used,
                requested = rejection?.Requested,
                audit = rejection?.Audit,
                evidenceBoundary = new
                {
                    queryDigest = boundaryQuery.QueryDigest,
                    evidenceIds,
                    knownQueryDigests = queries.Select(query => query.QueryDigest).Order(StringComparer.Ordinal).ToArray(),
                },
            }, JsonOptions),
            "open",
            1,
            boundaryQuery.QueryDigest,
            nowUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            evidenceIds);
        return true;
    }

    private static BusinessOntologyAgentRunResult CompleteWithFailure(
        string status,
        string phase,
        BusinessOntologyAgentBudgetState state,
        IReadOnlyList<BusinessOntologyAgentQueryObservation> queries,
        IReadOnlyList<BusinessOntologyAnalysisRecordInput> records,
        BusinessOntologyAgentFinish? finish,
        BusinessOntologyAgentBudgetRejection? budgetRejection,
        string? rejectionReason,
        IReadOnlyList<BusinessOntologyAgentStep> steps,
        IReadOnlyList<BusinessOntologyAgentPendingSynthesis> pendingSyntheses) =>
        new(
            status,
            phase,
            state,
            queries,
            records,
            finish,
            budgetRejection,
            rejectionReason,
            steps,
            pendingSyntheses);

    private static string QueryDigest(object result, string operation, string parametersJson) =>
        result switch
        {
            BusinessOntologyInvestigationOverview value => value.QueryDigest,
            BusinessOntologyInvestigationPage<BusinessTermHit> value => value.QueryDigest,
            BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern> value => value.QueryDigest,
            BusinessOntologyInvestigationPage<BusinessUseCaseSlice> value => value.QueryDigest,
            BusinessOntologyInvestigationPage<BusinessOntologyDiscoveredDomainCharter> value => value.QueryDigest,
            BusinessOntologyInvestigationPage<BusinessOntologyCrossLayerUseCase> value => value.QueryDigest,
            BusinessOntologyInvestigationPage<BusinessOntologyStateRuleCluster> value => value.QueryDigest,
            BusinessOntologyInvestigationPage<BusinessOntologyImplementationCluster> value => value.QueryDigest,
            BusinessOntologySubjectInspection value => value.QueryDigest,
            _ => Digest(operation, new { parametersJson, result }),
        };

    private static long Rows(object result) =>
        result switch
        {
            BusinessOntologyInvestigationOverview => 1,
            BusinessOntologyInvestigationPage<BusinessTermHit> value => value.Items.Count,
            BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern> value => value.Items.Count,
            BusinessOntologyInvestigationPage<BusinessUseCaseSlice> value => value.Items.Count,
            BusinessOntologyInvestigationPage<BusinessOntologyDiscoveredDomainCharter> value => value.Items.Count,
            BusinessOntologyInvestigationPage<BusinessOntologyCrossLayerUseCase> value => value.Items.Count,
            BusinessOntologyInvestigationPage<BusinessOntologyStateRuleCluster> value => value.Items.Count,
            BusinessOntologyInvestigationPage<BusinessOntologyImplementationCluster> value => value.Items.Count,
            SemanticEvidencePack value => value.Anchors.Count,
            BusinessUseCaseSlice => 1,
            BusinessOntologySubjectInspection => 1,
            _ => 1,
        };

    private static long SourceBytes(object result) =>
        result is SemanticEvidencePack pack
            ? pack.SourceUtf8Bytes
            : Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, JsonOptions));

    internal static object QueryResultSummary(object result) =>
        result switch
        {
            BusinessOntologyInvestigationOverview value => new
            {
                value.OntologyId,
                value.GenerationId,
                value.Counts,
                value.Operations,
                value.QueryDigest,
            },
            BusinessOntologyInvestigationPage<BusinessTermHit> page => new
            {
                Items = page.Items.Take(5).Select(item => new
                {
                    item.Kind,
                    item.Id,
                    item.Label,
                    item.Detail,
                    item.Repository,
                    item.Path,
                    item.StartLine,
                    item.EndLine,
                    EvidenceIds = item.EvidenceRefs.Select(evidence => evidence.EvidenceId).ToArray(),
                    item.Confidence,
                    item.Status,
                }).ToArray(),
                page.NextCursor,
                page.Truncated,
                page.QueryDigest,
            },
            BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern> page => new
            {
                Items = page.Items.Take(5).Select(item => new
                {
                    item.ClaimId,
                    item.ClaimKind,
                    item.SubjectId,
                    item.Symbol,
                    item.Repository,
                    item.Path,
                    item.StartLine,
                    item.EndLine,
                    PayloadJson = Truncate(item.PayloadJson, 2048),
                    item.Confidence,
                    EvidenceIds = item.EvidenceRefs.Select(evidence => evidence.EvidenceId).ToArray(),
                }).ToArray(),
                page.NextCursor,
                page.Truncated,
                page.QueryDigest,
            },
            BusinessOntologyInvestigationPage<BusinessUseCaseSlice> page => new
            {
                Items = page.Items.Take(5).Select(SliceSummary).ToArray(),
                page.NextCursor,
                page.Truncated,
                page.QueryDigest,
            },
            BusinessOntologyInvestigationPage<BusinessOntologyDiscoveredDomainCharter> page => new
            {
                Items = page.Items.Take(5).Select(item => new { item.Id, item.DomainSeed, item.EvidenceIds, EntrySymbolId = item.Workflow.EntrySymbolId, item.Workflow.Action }).ToArray(),
                page.NextCursor,
                page.Truncated,
                page.QueryDigest,
            },
            BusinessOntologyInvestigationPage<BusinessOntologyCrossLayerUseCase> page => new
            {
                Items = page.Items.Take(5).Select(item => new { item.Id, item.DomainSeed, item.EntrySymbolId, item.Action, item.Roles, item.EvidenceIds }).ToArray(),
                page.NextCursor,
                page.Truncated,
                page.QueryDigest,
            },
            BusinessOntologyInvestigationPage<BusinessOntologyStateRuleCluster> page => new
            {
                Items = page.Items.Take(5).Select(item => new { item.Id, item.DomainSeed, item.SubjectId, item.EvidenceRefs, StateFieldCount = item.StateFields.Count, TransitionCount = item.Transitions.Count, GuardCount = item.Guards.Count }).ToArray(),
                page.NextCursor,
                page.Truncated,
                page.QueryDigest,
            },
            BusinessOntologyInvestigationPage<BusinessOntologyImplementationCluster> page => new
            {
                Items = page.Items.Take(5).Select(item => new { item.Id, item.DomainSeed, item.EvidenceIds, AnchorCount = item.SemanticCluster.ImplementationAnchors.Count }).ToArray(),
                page.NextCursor,
                page.Truncated,
                page.QueryDigest,
            },
            BusinessUseCaseSlice value => SliceSummary(value),
            SemanticEvidencePack pack => new
            {
                Anchors = pack.Anchors.Take(5).Select(anchor => new
                {
                    anchor.EvidenceId,
                    anchor.Repository,
                    anchor.Path,
                    anchor.SubjectId,
                    anchor.ClaimKind,
                    ClaimPayloadJson = Truncate(anchor.ClaimPayloadJson, 2048),
                    anchor.StartLine,
                    anchor.EndLine,
                    anchor.ExcerptStartLine,
                    anchor.ExcerptEndLine,
                    anchor.ExistingOntologyIds,
                    anchor.TextTruncated,
                }).ToArray(),
                pack.SourceUtf8Bytes,
                pack.OmittedAnchors,
                pack.TextTruncated,
            },
            BusinessOntologySubjectInspection value => new
            {
                value.OntologyId,
                value.GenerationId,
                value.SubjectKind,
                value.SubjectId,
                Subject = SubjectSummary(value.Subject),
                MappingCount = value.Mappings.Count,
                value.EvidenceIds,
                ReviewCount = value.Reviews.Count,
                value.QueryDigest,
            },
            _ => new
            {
                Digest = Digest("query_result_summary", result),
                Type = result.GetType().Name,
            },
        };

    private static object SliceSummary(BusinessUseCaseSlice slice) => new
    {
        slice.Id,
        slice.EntrySymbolId,
        slice.RouteKind,
        slice.Action,
        SymbolIds = slice.SymbolIds.Take(12).ToArray(),
        FileIds = slice.FileIds.Take(12).ToArray(),
        slice.Roles,
        ClaimIds = slice.ClaimIds.Take(12).ToArray(),
        ConceptIds = slice.ConceptIds.Take(12).ToArray(),
        EvidenceIds = slice.EvidenceIds.Take(12).ToArray(),
        slice.Diagnostics,
    };

    private static object SubjectSummary(object subject) =>
        subject switch
        {
            BusinessOntologyConcept item => new { SubjectType = "concept", item.Id, item.Kind, item.Label, item.Status, item.Confidence, item.EvidenceIds },
            BusinessOntologyRelation item => new { SubjectType = "relation", item.Id, item.Name, item.FromConceptId, item.ToConceptId, item.Status, item.Confidence, item.EvidenceIds },
            BusinessOntologyRule item => new { SubjectType = "rule", item.Id, item.SubjectId, item.Kind, item.Status, item.Confidence, item.EvidenceIds },
            BusinessOntologyLifecycle item => new { SubjectType = "lifecycle", item.Id, item.SubjectId, item.StateProperty, item.InitialState, item.Status, item.Confidence, item.EvidenceIds },
            BusinessOntologyCandidate item => new { SubjectType = "candidate", item.Id, item.SubjectKind, item.ProposedId, item.Status, item.Confidence, item.EvidenceIds },
            _ => new { SubjectType = subject.GetType().Name },
        };

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value ?? "";
        }
        return value[..maxChars] + "...";
    }

    private static IReadOnlyList<BusinessOntologyInvestigationEvidenceRef> EvidenceRefs(object result)
    {
        var refs = result switch
        {
            BusinessOntologyInvestigationPage<BusinessTermHit> page => page.Items.SelectMany(item => item.EvidenceRefs),
            BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern> page => page.Items.SelectMany(item => item.EvidenceRefs),
            BusinessOntologyInvestigationPage<BusinessUseCaseSlice> page => page.Items.SelectMany(item => item.EvidenceIds.Select(IdOnly)),
            BusinessOntologyInvestigationPage<BusinessOntologyDiscoveredDomainCharter> page => page.Items.SelectMany(item => item.EvidenceRefs),
            BusinessOntologyInvestigationPage<BusinessOntologyCrossLayerUseCase> page => page.Items.SelectMany(item => item.EvidenceRefs),
            BusinessOntologyInvestigationPage<BusinessOntologyStateRuleCluster> page => page.Items.SelectMany(item => item.EvidenceRefs),
            BusinessOntologyInvestigationPage<BusinessOntologyImplementationCluster> page => page.Items.SelectMany(item => item.EvidenceRefs),
            SemanticEvidencePack pack => pack.Anchors.Select(anchor => new BusinessOntologyInvestigationEvidenceRef(
                anchor.EvidenceId,
                anchor.Repository,
                anchor.Path,
                anchor.SubjectId,
                anchor.StartLine,
                anchor.EndLine)
            {
                SymbolId = anchor.SubjectId,
            }),
            BusinessUseCaseSlice slice => slice.EvidenceIds.Select(IdOnly),
            BusinessOntologySubjectInspection inspection => inspection.EvidenceIds.Select(IdOnly),
            _ => [],
        };

        return refs
            .Where(item => !string.IsNullOrWhiteSpace(item.EvidenceId))
            .GroupBy(item => item.EvidenceId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static BusinessOntologyInvestigationEvidenceRef IdOnly(string evidenceId) =>
        new(evidenceId, "", "", "", 0, 0);

    private static string Digest(string operation, object value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { operation, value }, JsonOptions)))).ToLowerInvariant();

    private static string? OptionalString(JsonElement parameters, string propertyName) =>
        parameters.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static string RequiredString(JsonElement parameters, string propertyName) =>
        parameters.GetProperty(propertyName).GetString() ?? throw new ArgumentException($"Missing required parameter: {propertyName}");

    private static int? OptionalInt(JsonElement parameters, string propertyName) =>
        parameters.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt32()
            : null;

    private static IReadOnlyList<string> StringArray(JsonElement parameters, string propertyName) =>
        parameters.GetProperty(propertyName).EnumerateArray()
            .Select(item => item.GetString() ?? throw new ArgumentException($"{propertyName} must contain strings."))
            .ToArray();

    private static IReadOnlyList<string>? OptionalStringArray(JsonElement parameters, string propertyName) =>
        parameters.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.EnumerateArray().Select(item => item.GetString() ?? throw new ArgumentException($"{propertyName} must contain strings.")).ToArray()
            : null;
}
