using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyAnalysisStoreTests
{
    private const string OntologyId = "SampleDomain.Ontology";
    private const string GenerationId = "fixture-1";

    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "onto-analysis-" + Guid.NewGuid().ToString("N"));
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
            await om.InitCodeKnowledgeAsync();
            var claimPayload = JsonSerializer.Serialize(new { property = "state", from = "DRAFT", to = "SUBMITTED" });
            var claimId = CodeSemanticClaimIdentity.Create("file:record", CodeSemanticClaimKinds.StateAssignment, claimPayload, 3, 3);
            await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Repositories: [new CodeRepositoryFact("repo:sample", root, "sample")],
                Files: [new CodeFileFact("file:record", "repo:sample", "src/RecordService.java", "java")],
                Symbols: [new CodeSymbolFact("symbol:record:submit", "file:record", "submitRecord", "method", 2, 4, Lang: "java")],
                SemanticClaims:
                [
                    new CodeSemanticClaimFact(
                        claimId,
                        "symbol:record:submit",
                        CodeSemanticClaimKinds.StateAssignment,
                        claimPayload,
                        "file:record",
                        3,
                        3,
                        0.94,
                        "treesitter",
                        "提交记录时把状态从草稿改为已提交")
                ],
                EntryPoints: [new CodeEntryPointFact("symbol:record:submit", "http_route", "POST /records/{id}/submit")]));

            var ontologyStore = new BusinessOntologyStore(om);
            await ontologyStore.ReplaceGenerationAsync(BaseGeneration(claimId));
            var acceptedBefore = await ontologyStore.ReadExportableAsync(OntologyId);
            var acceptedCountsBefore = await CountsAsync(om, [
                "onto_concept",
                "onto_candidate",
                "onto_review",
                "onto_materialization",
            ]);

            var analysisStore = new BusinessOntologyAnalysisStore(om);
            var run = new BusinessOntologyAnalysisRunInput(
                "analysis-run:submit-record",
                OntologyId,
                GenerationId,
                "codex-cli",
                "gpt-5.6-terra",
                "reconstruct submit record lifecycle",
                "running",
                "2026-07-19T00:00:00Z",
                "",
                "sha256:input");
            var runAppend = await analysisStore.AppendRunAsync(run);
            var runReplay = await analysisStore.AppendRunAsync(run);
            assert(runAppend.Appended && !runReplay.Appended,
                "analysis run append should be idempotent for identical immutable content");

            var records = await analysisStore.AppendRecordsAsync([
                new BusinessOntologyAnalysisRecordInput(
                    run.RunId,
                    "observation:submit-state-change",
                    "observation",
                    "lifecycle",
                    OntologyId + ".RecordLifecycle",
                    "提交记录触发状态迁移",
                    JsonSerializer.Serialize(new { action = "submit", from = "DRAFT", to = "SUBMITTED" }),
                    "observed",
                    0.05,
                    "sha256:query-state-assignment",
                    "2026-07-19T00:00:10Z",
                    [claimId]),
                new BusinessOntologyAnalysisRecordInput(
                    run.RunId,
                    "draft:candidate-submit-transition",
                    "candidate_draft",
                    "transition",
                    OntologyId + ".Transition.SubmitRecord",
                    "候选迁移草案",
                    JsonSerializer.Serialize(new { relation = "transition", action = "submitRecord", guard = "record.state == DRAFT" }),
                    "proposed",
                    0.2,
                    "sha256:query-use-case-slice",
                    "2026-07-19T00:00:20Z",
                    [claimId, "evidence:record"])
            ]);
            assert(records.Count == 2 && records.All(item => item.Appended),
                "analysis records should append observations and candidate drafts with provenance");

            var replay = await analysisStore.AppendRecordsAsync([
                new BusinessOntologyAnalysisRecordInput(
                    run.RunId,
                    "draft:candidate-submit-transition",
                    "candidate_draft",
                    "transition",
                    OntologyId + ".Transition.SubmitRecord",
                    "候选迁移草案",
                    JsonSerializer.Serialize(new { relation = "transition", action = "submitRecord", guard = "record.state == DRAFT" }),
                    "proposed",
                    0.2,
                    "sha256:query-use-case-slice",
                    "2026-07-19T00:00:20Z",
                    ["evidence:record", claimId])
            ]);
            assert(!replay.Single().Appended,
                "analysis record append should be idempotent even when evidence ids arrive in a different order");

            var conflictingIdentityRejected = false;
            try
            {
                await analysisStore.AppendRecordsAsync([
                    new BusinessOntologyAnalysisRecordInput(
                        run.RunId,
                        "draft:candidate-submit-transition",
                        "candidate_draft",
                        "transition",
                        OntologyId + ".Transition.SubmitRecord",
                        "候选迁移草案被改写",
                        JsonSerializer.Serialize(new { relation = "transition", action = "forceSubmit" }),
                        "proposed",
                        0.2,
                        "sha256:query-use-case-slice",
                        "2026-07-19T00:00:20Z",
                        [claimId])
                ]);
            }
            catch (InvalidOperationException)
            {
                conflictingIdentityRejected = true;
            }
            assert(conflictingIdentityRejected,
                "analysis records must be append-only and reject different content for an existing identity");

            var conflictBatchRejected = false;
            try
            {
                await analysisStore.AppendRecordsAsync([
                    new BusinessOntologyAnalysisRecordInput(
                        run.RunId,
                        "observation:should-not-commit",
                        "observation",
                        "concept",
                        OntologyId + ".Record",
                        "不应部分提交的观察",
                        JsonSerializer.Serialize(new { note = "this row must roll back with the batch" }),
                        "observed",
                        0.1,
                        "sha256:query-atomic-batch",
                        "2026-07-19T00:00:25Z",
                        [claimId]),
                    new BusinessOntologyAnalysisRecordInput(
                        run.RunId,
                        "draft:candidate-submit-transition",
                        "candidate_draft",
                        "transition",
                        OntologyId + ".Transition.SubmitRecord",
                        "同批冲突草案",
                        JsonSerializer.Serialize(new { relation = "transition", action = "forceSubmit" }),
                        "proposed",
                        0.2,
                        "sha256:query-use-case-slice",
                        "2026-07-19T00:00:20Z",
                        [claimId])
                ]);
            }
            catch (InvalidOperationException)
            {
                conflictBatchRejected = true;
            }
            assert(conflictBatchRejected
                    && !(await analysisStore.ReadRecordsAsync(run.RunId)).Any(item => item.RecordId == "observation:should-not-commit"),
                "analysis record batch append must be atomic when any record conflicts with immutable append-only content");

            var unknownEvidenceRejected = false;
            try
            {
                await analysisStore.AppendRecordsAsync([
                    new BusinessOntologyAnalysisRecordInput(
                        run.RunId,
                        "gap:missing-evidence",
                        "gap",
                        "evidence",
                        "unknown",
                        "缺失证据",
                        "{}",
                        "open",
                        0.8,
                        "sha256:query-gap",
                        "2026-07-19T00:00:30Z",
                        ["not-a-known-evidence-id"])
                ]);
            }
            catch (ArgumentException)
            {
                unknownEvidenceRejected = true;
            }
            assert(unknownEvidenceRejected,
                "analysis records must close over indexed ck_semantic_claim or active onto_evidence ids");

            var completion = new BusinessOntologyAnalysisRunCompletionInput(
                run.RunId,
                "finished",
                "2026-07-19T00:02:00Z",
                3,
                2,
                0,
                "");
            var completionAppend = await analysisStore.AppendRunCompletionAsync(completion);
            var completionReplay = await analysisStore.AppendRunCompletionAsync(completion);
            assert(completionAppend.Appended && !completionReplay.Appended,
                "analysis run completion should append exactly once and replay idempotently");

            var conflictingCompletionRejected = false;
            try
            {
                await analysisStore.AppendRunCompletionAsync(completion with { Status = "blocked" });
            }
            catch (InvalidOperationException)
            {
                conflictingCompletionRejected = true;
            }
            assert(conflictingCompletionRejected,
                "analysis run completions must remain immutable and reject terminal upserts");

            var workspace = await analysisStore.ReadWorkspaceAsync(OntologyId, GenerationId);
            var draft = workspace.Records.Single(item => item.RecordId == "draft:candidate-submit-transition");
            assert(workspace.Runs is [{ RunId: var runId, Status: "completed", CompletedAt: "2026-07-19T00:02:00Z" }]
                    && runId == run.RunId
                    && workspace.Records.Count == 2
                    && workspace.Completions is [{ Status: "finished", CompletedAt: "2026-07-19T00:02:00Z", QueryCount: 3, RecordCount: 2, RejectedActionCount: 0, BudgetMetric: "" }]
                    && draft.Status == "proposed"
                    && Math.Abs(draft.Uncertainty - 0.2) < 0.000001
                    && draft.QueryDigest == "sha256:query-use-case-slice"
                    && draft.EvidenceIds.SequenceEqual(new[] { claimId, "evidence:record" }.OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal),
                "workspace read view should project an immutable terminal completion onto its run while retaining the completion audit record");

            var acceptedAfter = await ontologyStore.ReadExportableAsync(OntologyId);
            var acceptedCountsAfter = await CountsAsync(om, [
                "onto_concept",
                "onto_candidate",
                "onto_review",
                "onto_materialization",
            ]);
            assert(SnapshotBytes(acceptedBefore) == SnapshotBytes(acceptedAfter)
                    && acceptedCountsBefore.OrderBy(item => item.Key).SequenceEqual(acceptedCountsAfter.OrderBy(item => item.Key)),
                "onto_analysis_* writes must not mutate accepted ontology generations, candidates, reviews, or materialization rows");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static BusinessOntologyGenerationInput BaseGeneration(string claimId) => new(
        OntologyId,
        GenerationId,
        "fixture",
        "fixture",
        "2026-07-19T00:00:00Z",
        [new BusinessOntologyConcept(OntologyId + ".Record", "businessObject", "记录", "受管理的资产记录", "accepted", 0.9, ["evidence:record"])],
        [],
        [],
        [],
        [],
        [],
        [],
        [new BusinessOntologyMapping(OntologyId + ".Mapping.Record", "concept", OntologyId + ".Record", "representedBy", "sample", "java", "method", "symbol:record:submit", "src/RecordService.java", "treesitter", 0.9, "accepted", ["evidence:record"])],
        [new BusinessOntologyEvidence("evidence:record", "sample", "src/RecordService.java", "symbol:record:submit", 2, 4, "contractual", "treesitter", 0.9, "code", "记录提交服务")],
        [new BusinessOntologyCandidate("candidate:existing", "rule", OntologyId + ".Rule.Existing", "{}", "既有待审候选", 0.5, "pending", ["evidence:record"])],
        [new BusinessOntologyReview("review:existing", "candidate:existing", "rejected", "fixture", "维持原样", "2026-07-19T00:00:00Z")],
        []);

    private static string SnapshotBytes(BusinessOntologySnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false });

    private static async Task<IReadOnlyDictionary<string, int>> CountsAsync(CozoOm om, IReadOnlyList<string> relations)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var relation in relations)
        {
            var key = relation switch
            {
                "onto_concept" => "concept_id",
                "onto_candidate" => "candidate_id",
                "onto_review" => "review_id",
                "onto_materialization" => "candidate_id",
                _ => throw new ArgumentOutOfRangeException(nameof(relations)),
            };
            var rows = await om.Runtime.Store.RunAsync($"?[count(value)] := *{relation}{{{key}: value}}");
            values[relation] = rows.Rows[0][0].GetInt32();
        }
        return values;
    }
}
