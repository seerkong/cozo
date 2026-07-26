using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.LlmClient;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyAgenticReconstructionServiceTests
{
    private const string RunId = "analysis-run:agentic-fixture";
    private const string OntologyId = "SampleDomain.Ontology";
    private const string GenerationId = "fixture-1";
    private const string OtherEvidenceId = "evidence:other-query";
    private const string SecondEvidenceId = "evidence:record-persist";
    private static readonly DateTimeOffset StartedAt = new(2026, 7, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly string ClaimPayload = JsonSerializer.Serialize(new { property = "state", from = "DRAFT", to = "SUBMITTED" });
    private static readonly string EvidenceId = CodeSemanticClaimIdentity.Create(
        "file:record",
        CodeSemanticClaimKinds.StateAssignment,
        ClaimPayload,
        3,
        3);

    public static async Task RunAsync(Action<bool, string> assert)
    {
        await MultiRoundStateMachineAsync(assert);
        await CrossQueryEvidenceIsRejectedBeforePersistenceAsync(assert);
        await SynthesisStaysInMemoryAndDoesNotMutatePersistenceAsync(assert);
        await SynthesisRejectsLaterRecordsWithoutPersistenceAsync(assert);
        await SynthesisBudgetExhaustionStaysInMemoryAsync(assert);
        PendingSynthesisAppearsInPublicRunSummary(assert);
        await LaterEvidenceCoordinatesCannotOverwriteFirstBindingAsync(assert);
        await ZeroCandidateFinishDoesNotInventDraftAsync(assert);
        await DispatchesAllWhitelistedOperationsAsync(assert);
        await UnknownOperationIsRejectedBeforeDispatchAsync(assert);
        await BudgetRejectionStopsBeforeSecondQueryDispatchAsync(assert);
        await LlmActionSourcePersistsValidatedRecordsWithoutLeakingSourceAsync(assert);
        await PersistedFailureAppendsOneTerminalCompletionAsync(assert);
        await InvalidActionsAreFailureAtomicWithPersistentStoreAsync(assert);
        await BudgetExhaustionPersistsLocalGapAndStopsBeforeFurtherEffectsAsync(assert);
        await TimeoutCancellationAndQueryFailureAreFailureAtomicAsync(assert);
    }

    private static async Task MultiRoundStateMachineAsync(Action<bool, string> assert)
    {
        var operations = new FakeInvestigationOperations();
        var service = new BusinessOntologyAgenticReconstructionService(operations);
        var result = await service.RunAsync(new BusinessOntologyAgentRunRequest(
            RunId,
            new BusinessOntologyAgentSequenceActionSource(
            [
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                    """
                    {"kind":"business_guard","term":"record","limit":1}
                    """),
                ctx => RecordObservation(ctx, "observation:submit-state"),
                ctx => CandidateDraft(ctx, "candidate:record-lifecycle"),
                ctx => Finish(ctx, BusinessOntologyAgentFinishStatuses.Completed),
            ]),
            StartedAtUtc: new DateTimeOffset(2026, 7, 19, 8, 30, 0, TimeSpan.Zero)));

        assert(result.Status == BusinessOntologyAgentRunStatuses.Finished && result.Phase == BusinessOntologyAgentPhases.Model,
            "multi-round controller should finish in model phase after candidate_draft");
        assert(result.Steps.Select(step => $"{step.PhaseBefore}->{step.PhaseAfter}:{step.Action}").SequenceEqual([
                "explore->verify:query",
                "verify->reconcile:record",
                "reconcile->model:record",
                "model->model:finish",
            ]),
            "query, record, candidate_draft, and finish should advance the deterministic state machine");
        assert(result.Queries.Single().QueryDigest.Length == 64
                && result.Queries.Single().EvidenceRefs.Single().EvidenceId == EvidenceId,
            "query observations should retain digest and direct evidence refs for later validation");
        assert(result.Records.Count == 2
                && result.Records.All(record => record.QueryDigest == result.Queries.Single().QueryDigest)
                && result.Records.All(record => record.EvidenceIds.Single() == EvidenceId),
            "record actions should be validated against previously observed query digest and evidence ids");
        assert(operations.Calls.SequenceEqual([BusinessOntologyAgentQueryOperations.FindSemanticPatterns]),
            "controller should dispatch through the typed investigation operation surface only");
    }

    private static async Task CrossQueryEvidenceIsRejectedBeforePersistenceAsync(Action<bool, string> assert)
    {
        using var fixture = await PersistentFixture.CreateAsync("cross-query-provenance");
        var result = await new BusinessOntologyAgenticReconstructionService(new ProvenanceInvestigationOperations())
            .RunAsync(new BusinessOntologyAgentRunRequest(
                RunId + ":cross-query-provenance",
                new BusinessOntologyAgentSequenceActionSource(
                [
                    ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                        """
                        {"kind":"business_guard","term":"record","limit":1}
                        """),
                    ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindBusinessTerms,
                        """
                        {"term":"record","limit":1}
                        """),
                    ctx => Record(ctx, "observation:cross-query", "observation", "lifecycle", OntologyId + ".RecordLifecycle", "observed",
                        ctx.Queries[0].QueryDigest,
                        ctx.Queries[1].EvidenceRefs.Single().EvidenceId),
                ]),
                StartedAtUtc: StartedAt,
                Persistence: new BusinessOntologyAgentPersistenceContext(
                    fixture.AnalysisStore,
                    AgentRunInput(RunId + ":cross-query-provenance"))));

        var workspace = await fixture.AnalysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
        assert(result.Status == BusinessOntologyAgentRunStatuses.Rejected
                && result.Queries.Count == 2
                && result.Records.Count == 0
                && workspace.Records.Count == 0,
            "controller must reject known evidence paired with a different query digest before persistence");

        var directResult = await new BusinessOntologyAgenticReconstructionService(new ProvenanceInvestigationOperations())
            .RunAsync(new BusinessOntologyAgentRunRequest(
                RunId + ":direct-query-provenance",
                new BusinessOntologyAgentSequenceActionSource(
                [
                    ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                        """
                        {"kind":"business_guard","term":"record","limit":1}
                        """),
                    ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindBusinessTerms,
                        """
                        {"term":"record","limit":1}
                        """),
                    ctx => Record(ctx, "observation:direct-query", "observation", "lifecycle", OntologyId + ".RecordLifecycle", "observed",
                        ctx.Queries[1].QueryDigest,
                        ctx.Queries[1].EvidenceRefs.Single().EvidenceId),
                    ctx => Finish(ctx, BusinessOntologyAgentFinishStatuses.Completed),
                ]),
                StartedAtUtc: StartedAt));

        assert(directResult.Status == BusinessOntologyAgentRunStatuses.Finished
                && directResult.Records.Single().EvidenceIds.SequenceEqual([OtherEvidenceId], StringComparer.Ordinal),
            "controller should accept evidence returned by the record query digest");
    }

    private static async Task SynthesisStaysInMemoryAndDoesNotMutatePersistenceAsync(Action<bool, string> assert)
    {
        using var fixture = await PersistentFixture.CreateAsync("pending-synthesis");
        var acceptedBefore = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
        var result = await new BusinessOntologyAgenticReconstructionService(new SynthesisInvestigationOperations())
            .RunAsync(new BusinessOntologyAgentRunRequest(
                RunId + ":pending-synthesis",
                new BusinessOntologyAgentSequenceActionSource(
                [
                    ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                        """
                        {"kind":"business_guard","term":"record","limit":2}
                        """),
                    ctx => Query(ctx, BusinessOntologyAgentQueryOperations.GetSemanticEvidence,
                        JsonSerializer.Serialize(new { evidenceIds = ctx.KnownEvidenceIds.Order(StringComparer.Ordinal).ToArray() })),
                    Synthesize,
                    ctx => Finish(ctx, BusinessOntologyAgentFinishStatuses.Completed),
                ]),
                StartedAtUtc: StartedAt,
                Persistence: new BusinessOntologyAgentPersistenceContext(
                    fixture.AnalysisStore,
                    AgentRunInput(RunId + ":pending-synthesis"))));

        var workspace = await fixture.AnalysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
        var acceptedAfter = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
        assert(result.Status == BusinessOntologyAgentRunStatuses.Finished
                && result.PendingSyntheses is [{ DomainCharters.Count: 1, Clusters.Count: 1 }]
                && result.PendingSyntheses.Single().Clusters.Single().ConceptId == "Records.Record"
                && result.Steps.Any(step => step.Action == BusinessOntologyAgentActionKinds.Synthesize && step.Outcome == "accepted")
                && result.Records.Count == 0
                && workspace.Runs.Count == 0
                && workspace.Records.Count == 0
                && workspace.Completions.Count == 0
                && acceptedBefore == acceptedAfter,
            "synthesize should retain a strong pending semantic draft only in the run result without writing analysis or accepted ontology");
        assert(result.Queries.Single(query => query.Operation == BusinessOntologyAgentQueryOperations.GetSemanticEvidence)
                    .EvidenceRefs.Single(item => item.EvidenceId == EvidenceId).SymbolId == "symbol:record:create",
            "semantic evidence-pack refs should retain their runtime SubjectId as the synthesize anchor symbol id");
    }

    private static async Task SynthesisRejectsLaterRecordsWithoutPersistenceAsync(Action<bool, string> assert)
    {
        using var fixture = await PersistentFixture.CreateAsync("synthesis-record-rejected");
        var acceptedBefore = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
        var result = await new BusinessOntologyAgenticReconstructionService(new SynthesisInvestigationOperations())
            .RunAsync(new BusinessOntologyAgentRunRequest(
                RunId + ":synthesis-record-rejected",
                new BusinessOntologyAgentSequenceActionSource(
                [
                    ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                        """
                        {"kind":"business_guard","term":"record","limit":2}
                        """),
                    ctx => Query(ctx, BusinessOntologyAgentQueryOperations.GetSemanticEvidence,
                        JsonSerializer.Serialize(new { evidenceIds = ctx.KnownEvidenceIds.Order(StringComparer.Ordinal).ToArray() })),
                    Synthesize,
                    ctx => Record(
                        ctx,
                        "observation:after-synthesis",
                        "observation",
                        "lifecycle",
                        OntologyId + ".RecordLifecycle",
                        "observed",
                        ctx.Queries.First(query => query.Operation == BusinessOntologyAgentQueryOperations.FindSemanticPatterns).QueryDigest,
                        EvidenceId),
                    ctx => Finish(ctx, BusinessOntologyAgentFinishStatuses.Completed),
                ]),
                StartedAtUtc: StartedAt,
                Persistence: new BusinessOntologyAgentPersistenceContext(
                    fixture.AnalysisStore,
                    AgentRunInput(RunId + ":synthesis-record-rejected"))));

        var workspace = await fixture.AnalysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
        var acceptedAfter = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
        assert(result.Status == BusinessOntologyAgentRunStatuses.Rejected
                && result.PendingSyntheses.Count == 1
                && result.Records.Count == 0
                && result.Steps.Last() is { Action: BusinessOntologyAgentActionKinds.Record, Outcome: "rejected" }
                && workspace.Runs.Count == 0
                && workspace.Records.Count == 0
                && workspace.Completions.Count == 0
                && acceptedBefore == acceptedAfter,
            "a record after synthesize must be rejected before any analysis workspace or accepted-ontology write");
    }

    private static async Task SynthesisBudgetExhaustionStaysInMemoryAsync(Action<bool, string> assert)
    {
        using var fixture = await PersistentFixture.CreateAsync("synthesis-budget");
        var acceptedBefore = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
        var result = await new BusinessOntologyAgenticReconstructionService(new SynthesisInvestigationOperations())
            .RunAsync(new BusinessOntologyAgentRunRequest(
                RunId + ":synthesis-budget",
                new BusinessOntologyAgentSequenceActionSource(
                [
                    ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                        """
                        {"kind":"business_guard","term":"record","limit":2}
                        """),
                    ctx => Query(ctx, BusinessOntologyAgentQueryOperations.GetSemanticEvidence,
                        JsonSerializer.Serialize(new { evidenceIds = ctx.KnownEvidenceIds.Order(StringComparer.Ordinal).ToArray() })),
                    Synthesize,
                ]),
                new BusinessOntologyAgentBudgetLimits(maxTurns: 3),
                StartedAt,
                new BusinessOntologyAgentPersistenceContext(
                    fixture.AnalysisStore,
                    AgentRunInput(RunId + ":synthesis-budget"))));

        var workspace = await fixture.AnalysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
        var acceptedAfter = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
        assert(result.Status == BusinessOntologyAgentRunStatuses.BudgetExhausted
                && result.BudgetRejection is { Metric: BusinessOntologyAgentBudgetMetrics.Turns }
                && result.PendingSyntheses.Count == 1
                && workspace.Runs.Count == 0
                && workspace.Records.Count == 0
                && workspace.Completions.Count == 0
                && acceptedBefore == acceptedAfter,
            "budget exhaustion after synthesize must not append a gap, run, or terminal analysis envelope");
    }

    private static void PendingSynthesisAppearsInPublicRunSummary(Action<bool, string> assert)
    {
        const string poisonRunId = "poison-run-id:/private/run.json";
        const string poisonTurnId = "poison-turn-id:/private/turn.json";
        const string poisonQueryDigest = "poison-query-digest:/private/query.json";
        const string poisonOperation = "poison-operation:/private/operation.json";
        const string poisonEvidenceId = "poison-evidence-id:/private/evidence.json";
        const string poisonSecondEvidenceId = "poison-evidence-id-2:/private/evidence-2.json";
        const string poisonOntologyId = "poison-ontology-id:/private/ontology.json";
        const string poisonCharterId = "poison-domain-id:/private/domain.json";
        const string poisonClusterId = "poison-cluster-id:/private/cluster.json";
        const string poisonConceptId = "poison-concept-id:/private/concept.json";
        const string poisonAnchorId = "poison-anchor-id:/private/anchor.json";
        var summary = LlmWikiToolRunner.BusinessOntologyAgentRunSummary(
            poisonRunId,
            BusinessOntologyAgentRunStatuses.Finished,
            BusinessOntologyAgentPhases.Model,
            BusinessOntologyAgentBudgetLimits.Default,
            null,
            [new BusinessOntologyAgentQueryObservation(
                poisonTurnId,
                poisonOperation,
                "{\"parameters\":\"/private/query.json\"}",
                poisonQueryDigest,
                [
                    new BusinessOntologyInvestigationEvidenceRef(poisonEvidenceId, "sample", "src/RecordService.java", "createRecord", 3, 3),
                    new BusinessOntologyInvestigationEvidenceRef(poisonSecondEvidenceId, "sample", "src/RecordRepository.java", "persistRecord", 9, 9),
                ],
                2,
                128,
                new { })],
            [
            new BusinessOntologyAnalysisRecordInput(poisonRunId, "record:/private/RecordService.cs:{\"raw\":true}", "candidate_draft", "unknown", poisonOntologyId, "ignored", "{}", "proposed", 0.5, poisonQueryDigest, StartedAt.ToString("O"), [poisonEvidenceId]),
            new BusinessOntologyAnalysisRecordInput(poisonRunId, "gap:/private/RecordRepository.cs", "gap", "unknown", poisonOntologyId, "ignored", "{}", "open", 0.5, poisonQueryDigest, StartedAt.ToString("O"), [poisonEvidenceId]),
            new BusinessOntologyAnalysisRecordInput(poisonRunId, "conflict:{\"source\":\"/private/RecordController.cs\"}", "conflict", "unknown", poisonOntologyId, "ignored", "{}", "open", 0.5, poisonQueryDigest, StartedAt.ToString("O"), [poisonEvidenceId]),
            ],
            [],
            "rejectionReason:/private/RecordService.cs:{\"raw\":true}",
            "providerUnavailableReason:/private/RecordProvider.cs:{\"raw\":true}",
            "model:/private/ModelConfig.json:{\"raw\":true}",
            "sha256:summary-input",
            budgetRejection: new BusinessOntologyAgentBudgetRejection(
                BusinessOntologyAgentBudgetMetrics.OutputTokens,
                100,
                90,
                20,
                "budgetAudit:/private/RecordBudget.cs:{\"raw\":true}"),
            finish: new BusinessOntologyAgentFinish(
                poisonTurnId,
                BusinessOntologyAgentFinishStatuses.NoCandidate,
                "finishReason:/private/RecordService.cs:{\"raw\":true}",
                ["unresolved:/private/RecordService.cs", "{\"sourceSnippet\":\"public void leak()\"}" ]),
            pendingSyntheses:
            [
                new BusinessOntologyAgentPendingSynthesis(
                [
                    new BusinessOntologySemanticDomainCharter(
                        poisonCharterId, "中文 charter:/private/RecordService.cs", "中文 description:{\"raw\":\"source\"}", [poisonEvidenceId, poisonSecondEvidenceId],
                        ["workflow:/private/RecordService.cs:{\"raw\":true}"]),
                ],
                [
                    new BusinessOntologySemanticCluster(
                        poisonClusterId, poisonCharterId, poisonConceptId, "中文 cluster:/private/RecordService.cs", "中文 description:{\"raw\":\"source\"}",
                        [
                            new BusinessOntologySemanticImplementationAnchor(poisonAnchorId, "role:/private/RecordController.cs:{\"raw\":true}", "src/RecordController.java", poisonEvidenceId),
                            new BusinessOntologySemanticImplementationAnchor(poisonAnchorId + "-2", "role:/private/RecordService.cs:{\"raw\":true}", "src/RecordService.java", poisonSecondEvidenceId),
                        ]),
                ]),
            ]);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(summary));
        var pending = document.RootElement.GetProperty("pendingSynthesis");
        var json = document.RootElement.GetRawText();
        static bool HasOnlyProperties(JsonElement element, params string[] names) =>
            element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(
                names.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal);

        assert(HasOnlyProperties(document.RootElement,
                    ["analysis", "budget", "diagnostics", "finish", "operation", "pendingSynthesis", "phase", "provenance", "schemaVersion", "status"])
                && HasOnlyProperties(document.RootElement.GetProperty("analysis"),
                    ["candidateDraftCount", "conflictCount", "gapCount", "recordCount"])
                && HasOnlyProperties(pending,
                    ["anchorCount", "charterCount", "clusterCount", "workflowCount"])
                && HasOnlyProperties(document.RootElement.GetProperty("budget"), ["limits", "rejection", "used"])
                && HasOnlyProperties(document.RootElement.GetProperty("budget").GetProperty("limits"),
                    ["MaxConcurrency", "MaxInputTokens", "MaxOutputTokens", "MaxQueries", "MaxRows", "MaxSourceBytes", "MaxTurns", "maxWallClockSeconds"])
                && HasOnlyProperties(document.RootElement.GetProperty("budget").GetProperty("rejection"),
                    ["Limit", "Metric", "Requested", "Used"])
                && document.RootElement.GetProperty("budget").GetProperty("used").ValueKind == JsonValueKind.Null
                && HasOnlyProperties(document.RootElement.GetProperty("provenance"),
                    ["evidenceCount", "inputDigest", "queryCount", "stepCount"])
                && HasOnlyProperties(document.RootElement.GetProperty("finish"), ["Status", "unresolvedCount"])
                && HasOnlyProperties(document.RootElement.GetProperty("diagnostics"), ["providerUnavailable", "rejected"])
                && document.RootElement.GetProperty("schemaVersion").GetString() == "business-ontology-agent-run-summary-v1"
                && document.RootElement.GetProperty("operation").GetString() == "run_business_ontology_agent"
                && document.RootElement.GetProperty("status").GetString() == BusinessOntologyAgentRunStatuses.Finished
                && document.RootElement.GetProperty("phase").GetString() == BusinessOntologyAgentPhases.Model
                && document.RootElement.GetProperty("analysis").GetProperty("recordCount").GetInt32() == 3
                && document.RootElement.GetProperty("analysis").GetProperty("candidateDraftCount").GetInt32() == 1
                && document.RootElement.GetProperty("analysis").GetProperty("gapCount").GetInt32() == 1
                && document.RootElement.GetProperty("analysis").GetProperty("conflictCount").GetInt32() == 1
                && !document.RootElement.GetProperty("analysis").TryGetProperty("runId", out _)
                && pending.GetProperty("charterCount").GetInt32() == 1
                && pending.GetProperty("clusterCount").GetInt32() == 1
                && pending.GetProperty("workflowCount").GetInt32() == 1
                && pending.GetProperty("anchorCount").GetInt32() == 2
                && document.RootElement.GetProperty("provenance").GetProperty("inputDigest").GetString() == "sha256:summary-input"
                && document.RootElement.GetProperty("provenance").GetProperty("queryCount").GetInt32() == 1
                && document.RootElement.GetProperty("provenance").GetProperty("evidenceCount").GetInt32() == 2
                && document.RootElement.GetProperty("provenance").GetProperty("stepCount").GetInt32() == 0
                && document.RootElement.GetProperty("finish").GetProperty("Status").GetString() == BusinessOntologyAgentFinishStatuses.NoCandidate
                && document.RootElement.GetProperty("finish").GetProperty("unresolvedCount").GetInt32() == 2
                && document.RootElement.GetProperty("diagnostics").GetProperty("rejected").GetBoolean()
                && document.RootElement.GetProperty("diagnostics").GetProperty("providerUnavailable").GetBoolean()
                && !document.RootElement.GetProperty("analysis").TryGetProperty("recordIds", out _)
                && !document.RootElement.TryGetProperty("ontologyId", out _)
                && !document.RootElement.TryGetProperty("generationId", out _)
                && !document.RootElement.TryGetProperty("workItem", out _)
                && !document.RootElement.GetProperty("finish").TryGetProperty("Reason", out _)
                && !document.RootElement.GetProperty("diagnostics").TryGetProperty("rejectionReason", out _)
                && !document.RootElement.GetProperty("diagnostics").TryGetProperty("providerUnavailableReason", out _)
                && !document.RootElement.GetProperty("budget").GetProperty("rejection").TryGetProperty("Audit", out _)
                && !document.RootElement.GetProperty("provenance").TryGetProperty("queryDigests", out _)
                && !document.RootElement.GetProperty("provenance").TryGetProperty("evidenceIds", out _)
                && !document.RootElement.GetProperty("provenance").TryGetProperty("operations", out _)
                && !new[]
                {
                    poisonRunId,
                    poisonTurnId,
                    poisonQueryDigest,
                    poisonOperation,
                    poisonEvidenceId,
                    poisonSecondEvidenceId,
                    poisonOntologyId,
                    poisonCharterId,
                    poisonClusterId,
                    poisonConceptId,
                    poisonAnchorId,
                }.Any(poison => json.Contains(poison, StringComparison.Ordinal))
                && !json.Contains("/private/", StringComparison.Ordinal)
                && !json.Contains("sourceSnippet", StringComparison.Ordinal)
                && !json.Contains("raw", StringComparison.Ordinal),
            "public run summary should retain only enum-controlled status, diagnostics, and aggregate counts without raw IDs or model-controlled text");
    }

    private static async Task LaterEvidenceCoordinatesCannotOverwriteFirstBindingAsync(Action<bool, string> assert)
    {
        var result = await new BusinessOntologyAgenticReconstructionService(new SynthesisInvestigationOperations())
            .RunAsync(new BusinessOntologyAgentRunRequest(
                RunId + ":first-evidence-binding",
                new BusinessOntologyAgentSequenceActionSource(
                [
                    ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                        """
                        {"kind":"business_guard","term":"record","limit":2}
                        """),
                    ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindBusinessTerms,
                        """
                        {"term":"record","limit":1}
                        """),
                    Synthesize,
                    ctx => Finish(ctx, BusinessOntologyAgentFinishStatuses.Completed),
                ]),
                StartedAtUtc: StartedAt));

        assert(result.Status == BusinessOntologyAgentRunStatuses.Finished
                && result.PendingSyntheses.Count == 1
                && result.Queries.Count == 2,
            "a later query must not replace the first runtime-owned evidence coordinates used by synthesize validation");
    }

    private static async Task DispatchesAllWhitelistedOperationsAsync(Action<bool, string> assert)
    {
        var operations = new FakeInvestigationOperations();
        var service = new BusinessOntologyAgenticReconstructionService(operations);
        var result = await service.RunAsync(new BusinessOntologyAgentRunRequest(
            RunId,
            new BusinessOntologyAgentSequenceActionSource(
            [
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.OntologyInvestigationOverview, "{}"),
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindBusinessTerms,
                    """
                    {"term":"record","limit":1}
                    """),
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.ListUseCaseSlices,
                    """
                    {"ontologyId":"SampleDomain.Ontology","limit":1}
                    """),
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.GetUseCaseSlice,
                    """
                    {"ontologyId":"SampleDomain.Ontology","sliceId":"slice:submit"}
                    """),
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                    """
                    {"kind":"business_guard","term":"record","limit":1}
                    """),
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.GetSemanticEvidence,
                    $$"""
                    {"evidenceIds":["{{ctx.KnownEvidenceIds.First()}}"]}
                    """),
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.InspectOntologySubject,
                    """
                    {"ontologyId":"SampleDomain.Ontology","subjectKind":"candidate","subjectId":"candidate:record"}
                    """),
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.DiscoverDomainCharters,
                    """
                    {"term":"record","limit":1}
                    """),
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.ListCrossLayerUseCases,
                    """
                    {"entrySymbolId":"symbol:record:create","limit":1}
                    """),
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindStateRuleClusters,
                    """
                    {"term":"record","limit":1}
                    """),
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindImplementationClusters,
                    """
                    {"domainSeed":"record","limit":1}
                    """),
                ctx => Finish(ctx, BusinessOntologyAgentFinishStatuses.NoCandidate),
            ]),
            new BusinessOntologyAgentBudgetLimits(
                maxTurns: 16,
                maxQueries: 12,
                maxRows: 32,
                maxSourceBytes: 131072,
                maxInputTokens: 200000,
                maxOutputTokens: 50000),
            StartedAtUtc: new DateTimeOffset(2026, 7, 19, 8, 40, 0, TimeSpan.Zero)));

        assert(result.Status == BusinessOntologyAgentRunStatuses.Finished && result.Queries.Count == 11,
            $"controller should accept every whitelisted investigation operation including the v3 read-only views (status={result.Status}, queries={result.Queries.Count}, reason={result.RejectionReason ?? result.BudgetRejection?.Metric ?? "none"})");
        assert(result.Finish is { Status: BusinessOntologyAgentFinishStatuses.NoCandidate }
                && result.Records.Count == 0
                && !result.Records.Any(record => record.Kind == "candidate_draft"),
            "zero-candidate runs should finish honestly without fabricating candidate_draft analysis records");
        assert(operations.Calls.SequenceEqual([
                BusinessOntologyAgentQueryOperations.OntologyInvestigationOverview,
                BusinessOntologyAgentQueryOperations.FindBusinessTerms,
                BusinessOntologyAgentQueryOperations.ListUseCaseSlices,
                BusinessOntologyAgentQueryOperations.GetUseCaseSlice,
                BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                BusinessOntologyAgentQueryOperations.GetSemanticEvidence,
                BusinessOntologyAgentQueryOperations.InspectOntologySubject,
                BusinessOntologyAgentQueryOperations.DiscoverDomainCharters,
                BusinessOntologyAgentQueryOperations.ListCrossLayerUseCases,
                BusinessOntologyAgentQueryOperations.FindStateRuleClusters,
                BusinessOntologyAgentQueryOperations.FindImplementationClusters,
            ]),
            "each whitelisted operation should dispatch to its typed G1 service method exactly once");
        assert(result.Queries.Any(query => query.Operation == BusinessOntologyAgentQueryOperations.GetSemanticEvidence
                                          && query.EvidenceRefs.Single().Path == "src/RecordService.java"),
            "semantic evidence dispatch should preserve source evidence references");
    }

    private static async Task ZeroCandidateFinishDoesNotInventDraftAsync(Action<bool, string> assert)
    {
        using var fixture = await PersistentFixture.CreateAsync("zero-candidate");
        var acceptedBefore = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
        var result = await new BusinessOntologyAgenticReconstructionService(new FakeInvestigationOperations())
            .RunAsync(new BusinessOntologyAgentRunRequest(
                RunId + ":zero-candidate",
                new BusinessOntologyAgentSequenceActionSource(
                [
                    ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                        """
                        {"kind":"business_guard","term":"unsupported term","limit":1}
                        """),
                    ctx => $$"""
                        {
                          "schemaVersion": "business-ontology-agent-action-v1",
                          "action": "finish",
                          "identity": {"runId": "{{ctx.RunId}}", "turnId": "{{ctx.TurnId}}"},
                          "status": "no_candidate",
                          "reason": "Indexed evidence does not support a candidate draft.",
                          "unresolved": ["No corroborating lifecycle or rule evidence was found."]
                        }
                        """
                ]),
                StartedAtUtc: StartedAt,
                Persistence: new BusinessOntologyAgentPersistenceContext(
                    fixture.AnalysisStore,
                    AgentRunInput(RunId + ":zero-candidate"))));

        var workspace = await fixture.AnalysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
        var acceptedAfter = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
        assert(result.Status == BusinessOntologyAgentRunStatuses.Finished
                && result.Finish is { Status: BusinessOntologyAgentFinishStatuses.NoCandidate, Unresolved.Count: 1 }
                && result.Queries.Count == 1
                && result.Records.Count == 0
                && workspace.Runs.Count == 0
                && workspace.Records.Count == 0
                && workspace.Completions.Count == 0
                && acceptedBefore == acceptedAfter,
            "no_candidate finish should preserve query provenance without creating an analysis run, terminal envelope, or accepted-ontology mutation");
    }

    private static async Task UnknownOperationIsRejectedBeforeDispatchAsync(Action<bool, string> assert)
    {
        var operations = new FakeInvestigationOperations();
        var service = new BusinessOntologyAgenticReconstructionService(operations);
        var result = await service.RunAsync(new BusinessOntologyAgentRunRequest(
            RunId,
            new BusinessOntologyAgentSequenceActionSource(
            [
                ctx => Query(ctx, "query_named",
                    """
                    {"name":"unsafe","parametersJson":"{}"}
                    """),
            ]),
            StartedAtUtc: new DateTimeOffset(2026, 7, 19, 8, 50, 0, TimeSpan.Zero)));

        assert(result.Status == BusinessOntologyAgentRunStatuses.Rejected
                && result.Queries.Count == 0
                && operations.Calls.Count == 0,
            "unknown operations should fail local validation before any G1 effect");
        assert(result.RejectionReason?.Contains("allowed business ontology investigation operation", StringComparison.Ordinal) == true,
            "unknown-operation rejection should be auditable");
    }

    private static async Task BudgetRejectionStopsBeforeSecondQueryDispatchAsync(Action<bool, string> assert)
    {
        var operations = new FakeInvestigationOperations();
        var service = new BusinessOntologyAgenticReconstructionService(operations);
        var result = await service.RunAsync(new BusinessOntologyAgentRunRequest(
            RunId,
            new BusinessOntologyAgentSequenceActionSource(
            [
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.OntologyInvestigationOverview, "{}"),
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindBusinessTerms,
                    """
                    {"term":"record","limit":1}
                    """),
            ]),
            new BusinessOntologyAgentBudgetLimits(maxQueries: 1),
            new DateTimeOffset(2026, 7, 19, 9, 0, 0, TimeSpan.Zero)));

        assert(result.Status == BusinessOntologyAgentRunStatuses.BudgetExhausted
                && result.BudgetRejection is { Metric: BusinessOntologyAgentBudgetMetrics.Queries }
                && result.Queries.Count == 1,
            "query budget exhaustion should stop the run with the prior accepted observation only");
        assert(operations.Calls.SequenceEqual([BusinessOntologyAgentQueryOperations.OntologyInvestigationOverview]),
            "query reservation must reject before the second G1 dispatch effect");
    }

    private static async Task LlmActionSourcePersistsValidatedRecordsWithoutLeakingSourceAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "onto-agent-controller-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "RecordService.java"), """
            class RecordService {
                void submitRecord() {
                    if (record.state == DRAFT) record.state = SUBMITTED;
                }
            }
            """);

        try
        {
            using var db = new CozoDb("mem", "");
            var om = new CozoOm(db);
            await SeedEvidenceAsync(om, root);
            var ontologyStore = new BusinessOntologyStore(om);
            await ontologyStore.ReplaceGenerationAsync(BaseGeneration());
            var acceptedBefore = SnapshotBytes(await ontologyStore.ReadExportableAsync(OntologyId));

            var analysisStore = new BusinessOntologyAnalysisStore(om);
            var runInput = AgentRunInput(RunId);
            var firstSource = new BusinessOntologyAgentLlmActionSource(new FakeLlmClient());
            var first = await new BusinessOntologyAgenticReconstructionService(new FakeInvestigationOperations())
                .RunAsync(new BusinessOntologyAgentRunRequest(
                    RunId,
                    firstSource,
                    StartedAtUtc: StartedAt,
                    Persistence: new BusinessOntologyAgentPersistenceContext(analysisStore, runInput)));

            assert(first.Status == BusinessOntologyAgentRunStatuses.Finished && first.Records.Count == 1,
                "ILlmClient-backed source should drive a bounded multi-turn controller run");
            assert(firstSource.UserPrompts.Count == 4
                    && firstSource.UserPrompts.All(prompt => !prompt.Contains("if (record.state == DRAFT)", StringComparison.Ordinal))
                    && firstSource.UserPrompts.All(prompt => !prompt.Contains("corroborating source text", StringComparison.Ordinal))
                    && firstSource.UserPrompts.All(prompt => !prompt.Contains("sourceExcerpt", StringComparison.OrdinalIgnoreCase))
                    && firstSource.UserPrompts.All(prompt => !prompt.Contains("corroborationExcerpt", StringComparison.OrdinalIgnoreCase))
                    && firstSource.UserPrompts.Any(prompt => prompt.Contains("\"resultSummary\"", StringComparison.Ordinal))
                    && firstSource.UserPrompts.Any(prompt => prompt.Contains("\"claimPayloadJson\"", StringComparison.Ordinal)),
                "LLM prompts should include bounded query result summaries for follow-up actions, never raw source text");

            var workspace = await analysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
            var persisted = workspace.Records.Single();
            var completion = workspace.Completions.Single();
            assert(workspace.Runs.Single().Model == "gpt-5.6-terra"
                    && persisted.RecordId == "candidate:record-lifecycle"
                    && persisted.Kind == "candidate_draft"
                    && persisted.EvidenceIds.SequenceEqual([EvidenceId], StringComparer.Ordinal)
                    && completion.Status == BusinessOntologyAgentRunStatuses.Finished
                    && DateTimeOffset.TryParse(completion.CompletedAt, out var completedAt)
                    && completedAt > StartedAt
                    && completion.QueryCount == first.Queries.Count
                    && completion.RecordCount == first.Records.Count
                    && completion.RejectedActionCount == 0
                    && completion.BudgetMetric == ""
                    && !persisted.EvidenceIds.Any(id => id.Contains("codex", StringComparison.OrdinalIgnoreCase)
                                                        || id.Contains("gpt", StringComparison.OrdinalIgnoreCase)
                                                        || id.Contains(RunId, StringComparison.Ordinal)),
                "G2 persistence should append one safe terminal envelope and store only validated evidence ids, not model text, model identity, or run identity as evidence");

            var second = await new BusinessOntologyAgenticReconstructionService(new FakeInvestigationOperations())
                .RunAsync(new BusinessOntologyAgentRunRequest(
                    RunId,
                    new BusinessOntologyAgentLlmActionSource(new FakeLlmClient()),
                    StartedAtUtc: StartedAt,
                    Persistence: new BusinessOntologyAgentPersistenceContext(analysisStore, runInput)));
            var replayWorkspace = await analysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
            assert(second.Status == BusinessOntologyAgentRunStatuses.Finished
                    && replayWorkspace.Records.Count == 1
                    && replayWorkspace.Completions.Count == 1
                    && replayWorkspace.Completions.Single() == completion
                    && second.Records.Single().CreatedAt == first.Records.Single().CreatedAt
                    && replayWorkspace.Records.Single().CreatedAt == first.Records.Single().CreatedAt,
                "record and terminal completion timestamps should come from stable run/turn context so replay is idempotent");

            var acceptedAfter = SnapshotBytes(await ontologyStore.ReadExportableAsync(OntologyId));
            assert(acceptedBefore == acceptedAfter,
                "agentic analysis records must not mutate accepted ontology");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task PersistedFailureAppendsOneTerminalCompletionAsync(Action<bool, string> assert)
    {
        using var fixture = await PersistentFixture.CreateAsync("persisted-failure");
        var runId = RunId + ":persisted-failure";
        var actions = new Func<BusinessOntologyAgentActionSourceContext, string>[]
        {
            ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                """
                {"kind":"business_guard","term":"record","limit":1}
                """),
            ctx => RecordObservation(ctx, "observation:before-provider-failure"),
        };

        var first = await new BusinessOntologyAgenticReconstructionService(new FakeInvestigationOperations())
            .RunAsync(new BusinessOntologyAgentRunRequest(
                runId,
                new BusinessOntologyAgentSequenceActionSource(actions),
                StartedAtUtc: StartedAt,
                Persistence: new BusinessOntologyAgentPersistenceContext(fixture.AnalysisStore, AgentRunInput(runId))));
        var second = await new BusinessOntologyAgenticReconstructionService(new FakeInvestigationOperations())
            .RunAsync(new BusinessOntologyAgentRunRequest(
                runId,
                new BusinessOntologyAgentSequenceActionSource(actions),
                StartedAtUtc: StartedAt,
                Persistence: new BusinessOntologyAgentPersistenceContext(fixture.AnalysisStore, AgentRunInput(runId))));

        var workspace = await fixture.AnalysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
        var completion = workspace.Completions.Single();
        assert(first.Status == BusinessOntologyAgentRunStatuses.Blocked
                && second.Status == BusinessOntologyAgentRunStatuses.Blocked
                && workspace.Runs.Count == 1
                && workspace.Records.Count == 1
                && workspace.Completions.Count == 1
                && completion.Status == BusinessOntologyAgentRunStatuses.Blocked
                && DateTimeOffset.TryParse(completion.CompletedAt, out var completedAt)
                && completedAt > StartedAt
                && completion.QueryCount == 1
                && completion.RecordCount == 1
                && completion.RejectedActionCount == 0
                && completion.BudgetMetric == "",
            "a provider/action-source failure after record persistence should append one immutable blocked terminal envelope");
    }

    private static async Task InvalidActionsAreFailureAtomicWithPersistentStoreAsync(Action<bool, string> assert)
    {
        var cases = new (string Name, IReadOnlyList<Func<BusinessOntologyAgentActionSourceContext, string>> Actions)[]
        {
            ("invalid-json", [_ => "{not json"]),
            ("unknown-action", [ctx => $$"""
                {
                  "schemaVersion": "business-ontology-agent-action-v1",
                  "action": "mutate",
                  "identity": {"runId": "{{ctx.RunId}}", "turnId": "{{ctx.TurnId}}"}
                }
                """]),
            ("unknown-query-digest", [
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                    """
                    {"kind":"business_guard","term":"record","limit":1}
                    """),
                ctx => RecordObservation(ctx, "observation:bad-digest")
                    .Replace(ctx.KnownQueryDigests.Single(), "sha256:unseen", StringComparison.Ordinal)
            ]),
            ("unknown-evidence", [
                ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                    """
                    {"kind":"business_guard","term":"record","limit":1}
                    """),
                ctx => RecordObservation(ctx, "observation:bad-evidence")
                    .Replace(EvidenceId, "evidence:not-known", StringComparison.Ordinal)
            ]),
        };

        foreach (var testCase in cases)
        {
            using var fixture = await PersistentFixture.CreateAsync(testCase.Name);
            var runId = RunId + ":" + testCase.Name;
            var acceptedBefore = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
            var result = await new BusinessOntologyAgenticReconstructionService(new FakeInvestigationOperations())
                .RunAsync(new BusinessOntologyAgentRunRequest(
                    runId,
                    new BusinessOntologyAgentSequenceActionSource(testCase.Actions),
                    StartedAtUtc: StartedAt,
                    Persistence: new BusinessOntologyAgentPersistenceContext(fixture.AnalysisStore, AgentRunInput(runId))));

            var workspace = await fixture.AnalysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
            var acceptedAfter = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
            assert(result.Status == BusinessOntologyAgentRunStatuses.Rejected
                    && result.Records.Count == 0
                    && workspace.Runs.Count == 0
                    && workspace.Records.Count == 0
                    && workspace.Completions.Count == 0
                    && acceptedBefore == acceptedAfter,
                $"{testCase.Name} should reject before appending model records or changing accepted ontology");
        }
    }

    private static async Task BudgetExhaustionPersistsLocalGapAndStopsBeforeFurtherEffectsAsync(Action<bool, string> assert)
    {
        using var fixture = await PersistentFixture.CreateAsync("budget");
        var operations = new FakeInvestigationOperations();
        var source = new CountingActionSource([
            ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                """
                {"kind":"business_guard","term":"record","limit":1}
                """),
            _ => throw new InvalidOperationException("a second model turn would violate failure atomicity")
        ]);
        var acceptedBefore = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
        var result = await new BusinessOntologyAgenticReconstructionService(operations)
            .RunAsync(new BusinessOntologyAgentRunRequest(
                RunId + ":budget",
                source,
                new BusinessOntologyAgentBudgetLimits(maxQueries: 1),
                StartedAt,
                new BusinessOntologyAgentPersistenceContext(fixture.AnalysisStore, AgentRunInput(RunId + ":budget"))));

        var workspace = await fixture.AnalysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
        var acceptedAfter = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
        var gap = workspace.Records.Single();
        var completion = workspace.Completions.Single();
        assert(result.Status == BusinessOntologyAgentRunStatuses.BudgetExhausted
                && result.BudgetRejection is { Metric: BusinessOntologyAgentBudgetMetrics.Queries }
                && source.Calls == 1
                && operations.Calls.SequenceEqual([BusinessOntologyAgentQueryOperations.FindSemanticPatterns]),
            "query budget exhaustion should terminate before any further model or G1 query effect");
        assert(result.Records.Single().Kind == "gap"
                && gap.Kind == "gap"
                && gap.QueryDigest == result.Queries.Single().QueryDigest
                && gap.EvidenceIds.SequenceEqual([EvidenceId], StringComparer.Ordinal)
                && completion.Status == BusinessOntologyAgentRunStatuses.BudgetExhausted
                && DateTimeOffset.TryParse(completion.CompletedAt, out var completedAt)
                && completedAt > StartedAt
                && completion.QueryCount == 1
                && completion.RecordCount == 1
                && completion.RejectedActionCount == 0
                && completion.BudgetMetric == BusinessOntologyAgentBudgetMetrics.Queries
                && acceptedBefore == acceptedAfter,
            "budget exhaustion should persist one local evidence-bounded gap and one immutable terminal envelope without changing accepted ontology");
    }

    private static async Task TimeoutCancellationAndQueryFailureAreFailureAtomicAsync(Action<bool, string> assert)
    {
        using (var fixture = await PersistentFixture.CreateAsync("timeout"))
        {
            var source = new DelayedActionSource(TimeSpan.FromSeconds(5));
            var acceptedBefore = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
            var result = await new BusinessOntologyAgenticReconstructionService(new FakeInvestigationOperations())
                .RunAsync(new BusinessOntologyAgentRunRequest(
                    RunId + ":timeout",
                    source,
                    new BusinessOntologyAgentBudgetLimits(maxWallClock: TimeSpan.FromMilliseconds(1010)),
                    StartedAt,
                    new BusinessOntologyAgentPersistenceContext(fixture.AnalysisStore, AgentRunInput(RunId + ":timeout"))));
            var workspace = await fixture.AnalysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
            var acceptedAfter = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
            assert(result.Status == BusinessOntologyAgentRunStatuses.TimedOut
                    && source.Calls == 1
                    && workspace.Runs.Count == 0
                    && workspace.Records.Count == 0
                    && workspace.Completions.Count == 0
                    && acceptedBefore == acceptedAfter,
                "action-source timeout should not append model records or mutate accepted ontology");
        }

        using (var fixture = await PersistentFixture.CreateAsync("cancel"))
        {
            using var cancellation = new CancellationTokenSource();
            var source = new CancellingActionSource(cancellation);
            var acceptedBefore = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
            var result = await new BusinessOntologyAgenticReconstructionService(new FakeInvestigationOperations())
                .RunAsync(new BusinessOntologyAgentRunRequest(
                    RunId + ":cancel",
                    source,
                    StartedAtUtc: StartedAt,
                    Persistence: new BusinessOntologyAgentPersistenceContext(fixture.AnalysisStore, AgentRunInput(RunId + ":cancel"))),
                    cancellation.Token);
            var workspace = await fixture.AnalysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
            var acceptedAfter = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
            assert(result.Status == BusinessOntologyAgentRunStatuses.Cancelled
                    && source.Calls == 1
                    && workspace.Runs.Count == 0
                    && workspace.Records.Count == 0
                    && workspace.Completions.Count == 0
                    && acceptedBefore == acceptedAfter,
                "external cancellation should return a terminal local result without partial records or accepted ontology changes");
        }

        using (var fixture = await PersistentFixture.CreateAsync("query-failure"))
        {
            var operations = new FailingInvestigationOperations();
            var acceptedBefore = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
            var result = await new BusinessOntologyAgenticReconstructionService(operations)
                .RunAsync(new BusinessOntologyAgentRunRequest(
                    RunId + ":query-failure",
                    new BusinessOntologyAgentSequenceActionSource([
                        ctx => Query(ctx, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                            """
                            {"kind":"business_guard","term":"record","limit":1}
                            """)
                    ]),
                    StartedAtUtc: StartedAt,
                    Persistence: new BusinessOntologyAgentPersistenceContext(fixture.AnalysisStore, AgentRunInput(RunId + ":query-failure"))));
            var workspace = await fixture.AnalysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
            var acceptedAfter = SnapshotBytes(await fixture.OntologyStore.ReadExportableAsync(OntologyId));
            assert(result.Status == BusinessOntologyAgentRunStatuses.Blocked
                    && operations.Calls.SequenceEqual([BusinessOntologyAgentQueryOperations.FindSemanticPatterns])
                    && result.Queries.Count == 0
                    && workspace.Runs.Count == 0
                    && workspace.Records.Count == 0
                    && workspace.Completions.Count == 0
                    && acceptedBefore == acceptedAfter,
                "query failure should not accept a query observation, append analysis records, or mutate accepted ontology");
        }
    }

    private static string Query(BusinessOntologyAgentActionSourceContext context, string operation, string parametersJson) =>
        $$"""
        {
          "schemaVersion": "business-ontology-agent-action-v1",
          "action": "query",
          "identity": {"runId": "{{context.RunId}}", "turnId": "{{context.TurnId}}"},
          "operation": "{{operation}}",
          "parameters": {{parametersJson}},
          "reason": "fixture {{operation}}"
        }
        """;

    private static string RecordObservation(BusinessOntologyAgentActionSourceContext context, string recordId) =>
        Record(context, recordId, "observation", "lifecycle", OntologyId + ".RecordLifecycle", "observed");

    private static string CandidateDraft(BusinessOntologyAgentActionSourceContext context, string recordId) =>
        Record(context, recordId, "candidate_draft", "lifecycle", OntologyId + ".RecordLifecycle", "proposed");

    private static string Record(
        BusinessOntologyAgentActionSourceContext context,
        string recordId,
        string kind,
        string subjectKind,
        string subjectId,
        string status,
        string? queryDigest = null,
        string? evidenceId = null) =>
        $$"""
        {
          "schemaVersion": "business-ontology-agent-action-v1",
          "action": "record",
          "identity": {"runId": "{{context.RunId}}", "turnId": "{{context.TurnId}}"},
          "records": [
            {
              "recordId": "{{recordId}}",
              "kind": "{{kind}}",
              "subjectKind": "{{subjectKind}}",
              "subjectId": "{{subjectId}}",
              "title": "Submit record lifecycle",
              "body": {"from": "DRAFT", "to": "SUBMITTED"},
              "status": "{{status}}",
              "uncertainty": 0.1,
              "queryDigest": "{{queryDigest ?? context.KnownQueryDigests.Single()}}",
              "evidenceIds": ["{{evidenceId ?? context.KnownEvidenceIds.Single()}}"]
            }
          ]
        }
        """;

    private static string Finish(BusinessOntologyAgentActionSourceContext context, string status) =>
        $$"""
        {
          "schemaVersion": "business-ontology-agent-action-v1",
          "action": "finish",
          "identity": {"runId": "{{context.RunId}}", "turnId": "{{context.TurnId}}"},
          "status": "{{status}}",
          "reason": "fixture complete",
          "unresolved": []
        }
        """;

    private static string Synthesize(BusinessOntologyAgentActionSourceContext context) =>
        $$"""
        {
          "schemaVersion": "business-ontology-agent-action-v1",
          "action": "synthesize",
          "identity": {"runId": "{{context.RunId}}", "turnId": "{{context.TurnId}}"},
          "domainCharters": [
            {
              "id": "Records",
              "nameZh": "记录管理",
              "descriptionZh": "管理业务记录的创建、提交和持久化。",
              "evidenceIds": ["{{EvidenceId}}", "{{SecondEvidenceId}}"],
              "workflowNames": ["记录提交"]
            }
          ],
          "clusters": [
            {
              "id": "cluster:record",
              "domainId": "Records",
              "conceptId": "Records.Record",
              "nameZh": "业务记录",
              "descriptionZh": "跨控制器和持久化实现聚合的业务记录。",
              "implementationAnchors": [
                {"symbolId": "symbol:record:create", "role": "controller", "relativePath": "src/RecordController.java", "evidenceId": "{{EvidenceId}}"},
                {"symbolId": "symbol:record:persist", "role": "repository", "relativePath": "src/RecordRepository.java", "evidenceId": "{{SecondEvidenceId}}"}
              ]
            }
          ]
        }
        """;

    private static async Task SeedEvidenceAsync(CozoOm om, string root)
    {
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:sample", root, "sample")],
            Files: [new CodeFileFact("file:record", "repo:sample", "src/RecordService.java", "java")],
            Symbols: [new CodeSymbolFact("symbol:record:submit", "file:record", "submitRecord", "method", 2, 4, Lang: "java")],
            SemanticClaims:
            [
                new CodeSemanticClaimFact(
                    EvidenceId,
                    "symbol:record:submit",
                    CodeSemanticClaimKinds.StateAssignment,
                    ClaimPayload,
                    "file:record",
                    3,
                    3,
                    0.94,
                    "treesitter",
                    "Submit changes record state from draft to submitted.")
            ],
            EntryPoints: [new CodeEntryPointFact("symbol:record:submit", "http_route", "POST /records/{id}/submit")]));
    }

    private static BusinessOntologyAnalysisRunInput AgentRunInput(string runId) => new(
        runId,
        OntologyId,
        GenerationId,
        "codex-cli",
        "gpt-5.6-terra",
        "reconstruct submit record lifecycle",
        "completed",
        StartedAt.ToString("O"),
        (StartedAt + TimeSpan.FromSeconds(30)).ToString("O"),
        "sha256:bounded-fixture-input");

    private static BusinessOntologyGenerationInput BaseGeneration() => new(
        OntologyId,
        GenerationId,
        "fixture",
        "fixture",
        StartedAt.ToString("O"),
        [new BusinessOntologyConcept(OntologyId + ".Record", "businessObject", "Record", "Managed asset record", "accepted", 0.9, ["evidence:record"])],
        [],
        [],
        [],
        [],
        [],
        [],
        [new BusinessOntologyMapping(OntologyId + ".Mapping.Record", "concept", OntologyId + ".Record", "representedBy", "sample", "java", "method", "symbol:record:submit", "src/RecordService.java", "treesitter", 0.9, "accepted", ["evidence:record"])],
        [new BusinessOntologyEvidence("evidence:record", "sample", "src/RecordService.java", "symbol:record:submit", 2, 4, "contractual", "treesitter", 0.9, "code", "Record submit service")],
        [],
        [],
        []);

    private static string SnapshotBytes(BusinessOntologySnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false });

    private sealed class FakeLlmClient : ILlmClient
    {
        private int _calls;

        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public Task<LlmCompletion> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            LlmOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls++;
            return Task.FromResult(new LlmCompletion(_calls switch
            {
                1 => LlmQuery(userPrompt, BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                    """
                    {"kind":"state_assignment","term":"record","limit":1}
                    """),
                2 => LlmQuery(userPrompt, BusinessOntologyAgentQueryOperations.GetSemanticEvidence,
                    $$"""
                    {"evidenceIds":["{{EvidenceId}}"]}
                    """),
                3 => LlmRecord(userPrompt),
                4 => LlmFinish(userPrompt),
                _ => throw new InvalidOperationException("unexpected fake LLM turn"),
            }, "fake-codex", new LlmUsage(PromptTokens: 10 + _calls, CompletionTokens: 5 + _calls)));
        }

        private static string LlmQuery(string prompt, string operation, string parametersJson)
        {
            var (runId, turnId) = Identity(prompt);
            return $$"""
            {
              "schemaVersion": "business-ontology-agent-action-v1",
              "action": "query",
              "identity": {"runId": "{{runId}}", "turnId": "{{turnId}}"},
              "operation": "{{operation}}",
              "parameters": {{parametersJson}},
              "reason": "bounded fake LLM query"
            }
            """;
        }

        private static string LlmRecord(string prompt)
        {
            var (runId, turnId) = Identity(prompt);
            var queryDigest = LastQueryDigest(prompt);
            return $$"""
            {
              "schemaVersion": "business-ontology-agent-action-v1",
              "action": "record",
              "identity": {"runId": "{{runId}}", "turnId": "{{turnId}}"},
              "records": [
                {
                  "recordId": "candidate:record-lifecycle",
                  "kind": "candidate_draft",
                  "subjectKind": "lifecycle",
                  "subjectId": "{{OntologyId}}.RecordLifecycle",
                  "title": "Record submit lifecycle",
                  "body": {"from": "DRAFT", "to": "SUBMITTED", "modelText": "fake model proposed this draft", "model": "fake-codex"},
                  "status": "proposed",
                  "uncertainty": 0.2,
                  "queryDigest": "{{queryDigest}}",
                  "evidenceIds": ["{{EvidenceId}}"]
                }
              ]
            }
            """;
        }

        private static string LlmFinish(string prompt)
        {
            var (runId, turnId) = Identity(prompt);
            return $$"""
            {
              "schemaVersion": "business-ontology-agent-action-v1",
              "action": "finish",
              "identity": {"runId": "{{runId}}", "turnId": "{{turnId}}"},
              "status": "completed",
              "reason": "candidate draft persisted",
              "unresolved": []
            }
            """;
        }

        private static (string RunId, string TurnId) Identity(string prompt)
        {
            using var document = JsonDocument.Parse(prompt);
            var state = document.RootElement.GetProperty("state");
            return (state.GetProperty("runId").GetString()!, state.GetProperty("turnId").GetString()!);
        }

        private static string LastQueryDigest(string prompt)
        {
            using var document = JsonDocument.Parse(prompt);
            return document.RootElement
                .GetProperty("querySummaries")
                .EnumerateArray()
                .Last()
                .GetProperty("queryDigest")
                .GetString()!;
        }
    }

    private sealed class PersistentFixture : IDisposable
    {
        private PersistentFixture(string root, CozoDb db, CozoOm om)
        {
            Root = root;
            Db = db;
            Om = om;
            OntologyStore = new BusinessOntologyStore(om);
            AnalysisStore = new BusinessOntologyAnalysisStore(om);
        }

        private string Root { get; }
        private CozoDb Db { get; }
        private CozoOm Om { get; }
        public BusinessOntologyStore OntologyStore { get; }
        public BusinessOntologyAnalysisStore AnalysisStore { get; }

        public static async Task<PersistentFixture> CreateAsync(string label)
        {
            var root = Path.Combine(Path.GetTempPath(), "onto-agent-" + label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            await File.WriteAllTextAsync(Path.Combine(root, "src", "RecordService.java"), """
                class RecordService {
                    void submitRecord() {
                        if (record.state == DRAFT) record.state = SUBMITTED;
                    }
                }
                """);
            var db = new CozoDb("mem", "");
            var om = new CozoOm(db);
            await SeedEvidenceAsync(om, root);
            var fixture = new PersistentFixture(root, db, om);
            await fixture.OntologyStore.ReplaceGenerationAsync(BaseGeneration());
            return fixture;
        }

        public void Dispose()
        {
            Db.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class CountingActionSource(IReadOnlyList<Func<BusinessOntologyAgentActionSourceContext, string>> actions)
        : IBusinessOntologyAgentActionSource
    {
        private int _index;
        public int Calls { get; private set; }

        public Task<string> NextActionJsonAsync(BusinessOntologyAgentActionSourceContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (_index >= actions.Count)
            {
                throw new InvalidOperationException("The deterministic action sequence is exhausted.");
            }
            return Task.FromResult(actions[_index++](context));
        }
    }

    private sealed class DelayedActionSource(TimeSpan delay) : IBusinessOntologyAgentActionSource
    {
        public int Calls { get; private set; }

        public async Task<string> NextActionJsonAsync(BusinessOntologyAgentActionSourceContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Delay(delay, cancellationToken);
            return Finish(context, BusinessOntologyAgentFinishStatuses.Completed);
        }
    }

    private sealed class CancellingActionSource(CancellationTokenSource cancellation) : IBusinessOntologyAgentActionSource
    {
        public int Calls { get; private set; }

        public async Task<string> NextActionJsonAsync(BusinessOntologyAgentActionSourceContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            await cancellation.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return Finish(context, BusinessOntologyAgentFinishStatuses.Completed);
        }
    }

    private class FakeInvestigationOperations : IBusinessOntologyInvestigationOperations
    {
        public List<string> Calls { get; } = [];

        public virtual Task<BusinessOntologyInvestigationOverview> GetOverviewAsync(string? ontologyId = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.OntologyInvestigationOverview);
            return Task.FromResult(new BusinessOntologyInvestigationOverview(ontologyId, "generation:fixture", new Dictionary<string, int>
            {
                ["ck_semantic_claim"] = 1,
                ["onto_candidate"] = 1,
            }, BusinessOntologyAgentQueryOperations.All.ToArray(), Digest("overview")));
        }

        public virtual Task<BusinessOntologyInvestigationPage<BusinessTermHit>> FindBusinessTermsAsync(
            string term,
            string? ontologyId = null,
            string? cursor = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.FindBusinessTerms);
            return Task.FromResult(Page(
                BusinessOntologyAgentQueryOperations.FindBusinessTerms,
                new BusinessTermHit("semantic-claim", EvidenceId, "record", "business_guard", "sample", "src/RecordService.java", 3, 3,
                    [EvidenceRef()], 0.95, "source-fact")));
        }

        public virtual Task<BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern>> FindSemanticPatternsAsync(
            string kind,
            string? term = null,
            string? cursor = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.FindSemanticPatterns);
            return Task.FromResult(Page(
                BusinessOntologyAgentQueryOperations.FindSemanticPatterns,
                new BusinessOntologySemanticPattern(EvidenceId, kind, "symbol:record:create", "createRecord", "sample", "src/RecordService.java", 3, 3,
                    """{"guard":"draft"}""", 0.95, [EvidenceRef()])));
        }

        public virtual Task<SemanticEvidencePack> GetSemanticEvidenceAsync(IReadOnlyList<string> evidenceIds, CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.GetSemanticEvidence);
            return Task.FromResult(new SemanticEvidencePack([
                new SemanticEvidencePackAnchor(EvidenceId, "sample", "src/RecordService.java", "symbol:record:create", "business_guard",
                    """{"guard":"draft"}""", 3, 3, 1, 5, "if (record.state == DRAFT) record.state = SUBMITTED;", [], "corroborating source text", false),
            ], 54, 0, false));
        }

        public virtual Task<BusinessOntologyInvestigationPage<BusinessUseCaseSlice>> ListUseCaseSlicesAsync(
            string ontologyId,
            string? cursor = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.ListUseCaseSlices);
            return Task.FromResult(Page(BusinessOntologyAgentQueryOperations.ListUseCaseSlices, Slice()));
        }

        public virtual Task<BusinessUseCaseSlice> GetUseCaseSliceAsync(string ontologyId, string sliceId, CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.GetUseCaseSlice);
            return Task.FromResult(Slice());
        }

        public virtual Task<BusinessOntologySubjectInspection> InspectOntologySubjectAsync(
            string ontologyId,
            string subjectKind,
            string subjectId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.InspectOntologySubject);
            return Task.FromResult(new BusinessOntologySubjectInspection(
                ontologyId,
                "generation:fixture",
                subjectKind,
                subjectId,
                new BusinessOntologyCandidate(subjectId, "lifecycle", OntologyId + ".RecordLifecycle", "{}", "fixture", 0.5, "pending", [EvidenceId]),
                [],
                [EvidenceId],
                [],
                Digest("inspect")));
        }

        public virtual Task<BusinessOntologyInvestigationPage<BusinessOntologyDiscoveredDomainCharter>> DiscoverDomainChartersAsync(
            string term,
            string? cursor = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.DiscoverDomainCharters);
            return Task.FromResult(Page(
                BusinessOntologyAgentQueryOperations.DiscoverDomainCharters,
                new BusinessOntologyDiscoveredDomainCharter(
                    "domain-charter:record", "record",
                    [new BusinessOntologyInvestigationActor("actor:record", "Record user", "controller", [EvidenceId])],
                    new BusinessOntologyInvestigationWorkflow("symbol:record:create", "http_route", "POST /records", [], [EvidenceRef()]),
                    [], [EvidenceId], [EvidenceRef()])));
        }

        public virtual Task<BusinessOntologyInvestigationPage<BusinessOntologyCrossLayerUseCase>> ListCrossLayerUseCasesAsync(
            string? entrySymbolId = null,
            string? domainSeed = null,
            string? cursor = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.ListCrossLayerUseCases);
            return Task.FromResult(Page(
                BusinessOntologyAgentQueryOperations.ListCrossLayerUseCases,
                new BusinessOntologyCrossLayerUseCase(
                    "cross-layer:record", "record", "symbol:record:create", "http_route", "POST /records",
                    [new BusinessOntologyInvestigationCallPathStep("symbol:record:create", "createRecord", "controller", "sample", "src/RecordService.java", 3)],
                    ["controller", "service", "repository"], [], [EvidenceId], [EvidenceRef()])));
        }

        public virtual Task<BusinessOntologyInvestigationPage<BusinessOntologyStateRuleCluster>> FindStateRuleClustersAsync(
            string term,
            string? cursor = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.FindStateRuleClusters);
            var pattern = new BusinessOntologySemanticPattern(EvidenceId, "state_assignment", "symbol:record:create", "createRecord", "sample", "src/RecordService.java", 3, 3, "{}", 0.95, [EvidenceRef()]);
            return Task.FromResult(Page(
                BusinessOntologyAgentQueryOperations.FindStateRuleClusters,
                new BusinessOntologyStateRuleCluster("state-rule:record", "record", "symbol:record:create", [], [], [], [pattern], [], [], [], [EvidenceRef()])));
        }

        public virtual Task<BusinessOntologyInvestigationPage<BusinessOntologyImplementationCluster>> FindImplementationClustersAsync(
            string? domainSeed = null,
            IReadOnlyList<string>? evidenceIds = null,
            string? cursor = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.FindImplementationClusters);
            return Task.FromResult(Page(
                BusinessOntologyAgentQueryOperations.FindImplementationClusters,
                new BusinessOntologyImplementationCluster(
                    "implementation-cluster:record", "record",
                    new BusinessOntologySemanticCluster("implementation-cluster:record", "domain:record", "pending:record", "record", "fixture anchors",
                        [new BusinessOntologySemanticImplementationAnchor("symbol:record:create", "service", "src/RecordService.java", EvidenceId)]),
                    [EvidenceId], [EvidenceRef()])));
        }

        private static BusinessOntologyInvestigationPage<T> Page<T>(string operation, T item) =>
            new([item], null, false, Digest(operation));

        private static BusinessUseCaseSlice Slice() =>
            new("slice:submit", "symbol:record:create", "http_route", "POST /records",
                ["symbol:record:create", "symbol:record:repo"],
                ["file:service", "file:repo"],
                ["service", "repository"],
                [EvidenceId],
                [OntologyId + ".Record"],
                [EvidenceId],
                new BusinessUseCaseSliceTruncationDiagnostics(false, false, false, 4, 32, 24));

        private static BusinessOntologyInvestigationEvidenceRef EvidenceRef() =>
            new(EvidenceId, "sample", "src/RecordService.java", "createRecord", 3, 3);

        private static string Digest(string seed) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
    }

    private sealed class FailingInvestigationOperations : FakeInvestigationOperations
    {
        public override Task<BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern>> FindSemanticPatternsAsync(
            string kind,
            string? term = null,
            string? cursor = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.FindSemanticPatterns);
            throw new InvalidOperationException("fixture query failure");
        }
    }

    private sealed class SynthesisInvestigationOperations : FakeInvestigationOperations
    {
        public override Task<BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern>> FindSemanticPatternsAsync(
            string kind,
            string? term = null,
            string? cursor = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.FindSemanticPatterns);
            return Task.FromResult(new BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern>(
            [
                Pattern(EvidenceId, "symbol:record:create", "src/RecordController.java", "createRecord", "controller"),
                Pattern(SecondEvidenceId, "symbol:record:persist", "src/RecordRepository.java", "persistRecord", "repository"),
            ],
            null,
            false,
            "query:synthesis-patterns"));
        }

        public override Task<SemanticEvidencePack> GetSemanticEvidenceAsync(IReadOnlyList<string> evidenceIds, CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.GetSemanticEvidence);
            return Task.FromResult(new SemanticEvidencePack(
            [
                Anchor(EvidenceId, "symbol:record:create", "src/RecordController.java", "controller"),
                Anchor(SecondEvidenceId, "symbol:record:persist", "src/RecordRepository.java", "repository"),
            ],
            128,
            0,
            false));
        }

        public override Task<BusinessOntologyInvestigationPage<BusinessTermHit>> FindBusinessTermsAsync(
            string term,
            string? ontologyId = null,
            string? cursor = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.FindBusinessTerms);
            var altered = new BusinessOntologyInvestigationEvidenceRef(EvidenceId, "sample", "src/ForgedRecord.java", "forgedRecord", 99, 99)
            {
                SymbolId = "symbol:record:forged",
            };
            return Task.FromResult(new BusinessOntologyInvestigationPage<BusinessTermHit>(
                [new BusinessTermHit("semantic-claim", EvidenceId, "record", "altered", "sample", "src/ForgedRecord.java", 99, 99, [altered], 0.95, "source-fact")],
                null,
                false,
                "query:altered-evidence"));
        }

        private static BusinessOntologySemanticPattern Pattern(string evidenceId, string symbolId, string path, string symbol, string role) =>
            new(evidenceId, "business_guard", symbolId, symbol, "sample", path, 3, 3, "{}", 0.95,
                [new BusinessOntologyInvestigationEvidenceRef(evidenceId, "sample", path, symbol, 3, 3) { SymbolId = symbolId }]);

        private static SemanticEvidencePackAnchor Anchor(string evidenceId, string subjectId, string path, string role) =>
            new(evidenceId, "sample", path, subjectId, "business_guard", "{}", 3, 3, 1, 5, role, [], "", false);
    }

    private sealed class ProvenanceInvestigationOperations : FakeInvestigationOperations
    {
        public override Task<BusinessOntologyInvestigationPage<BusinessTermHit>> FindBusinessTermsAsync(
            string term,
            string? ontologyId = null,
            string? cursor = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(BusinessOntologyAgentQueryOperations.FindBusinessTerms);
            var evidence = new BusinessOntologyInvestigationEvidenceRef(
                OtherEvidenceId,
                "sample",
                "src/OtherRecordService.java",
                "findRecord",
                5,
                5);
            return Task.FromResult(new BusinessOntologyInvestigationPage<BusinessTermHit>(
                [new BusinessTermHit("semantic-claim", OtherEvidenceId, "record", "business_guard", "sample", "src/OtherRecordService.java", 5, 5,
                    [evidence], 0.95, "source-fact")],
                null,
                false,
                "query:other-record"));
        }
    }
}
