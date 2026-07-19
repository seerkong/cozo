using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyReviewServiceTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);

        // Simulate a database created before review evidence snapshots were introduced.
        await om.Runtime.Store.RunAsync(
            ":create onto_review {ontology_id, review_id => candidate_id, decision, reviewer, rationale, reviewed_at}");
        var store = new BusinessOntologyStore(om);
        await store.InitializeAsync();
        var relations = await om.Runtime.Store.RunAsync("::relations");
        assert(
            relations.Rows.Any(row => row[0].GetString() == "onto_review_expectation"),
            "review initialization should add the expectation relation without replacing legacy onto_review");

        var generation = SampleGeneration();
        await store.ReplaceGenerationAsync(generation);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 18, 8, 0, 0, TimeSpan.Zero));
        var service = new BusinessOntologyReviewService(store, clock);
        var root = Path.Combine(Path.GetTempPath(), $"onto-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var acceptedFile = Path.Combine(root, "accepted.json");
            await File.WriteAllTextAsync(
                acceptedFile,
                DecisionJson(
                    "candidate:relation",
                    "accepted",
                    "确认供应商关系属于业务本体。",
                    ["evidence:supplier", "evidence:record"]));
            var accepted = await service.ApplyDecisionFileAsync(
                acceptedFile,
                "SampleDomain.Ontology");
            assert(
                accepted.Reviews.Count == 1 && accepted.Reviews[0].Appended,
                "the first explicit decision should append one review");

            var firstHistory = await store.ReadReviewHistoryAsync(
                "SampleDomain.Ontology",
                "candidate:relation");
            assert(
                firstHistory.Count == 1
                && firstHistory[0].ExpectedEvidenceIds.SequenceEqual(
                    ["evidence:record", "evidence:supplier"],
                    StringComparer.Ordinal),
                "review history should preserve a sorted exact evidence snapshot");
            var firstReviewedAt = firstHistory[0].Review.ReviewedAt;
            var snapshot = await store.ReadExportableAsync("SampleDomain.Ontology");
            assert(
                snapshot.Relations.Count == 0 && snapshot.Rules.Count == 0 && snapshot.Lifecycles.Count == 0,
                "T4.1 review application must not materialize accepted candidates");

            clock.Advance(TimeSpan.FromMinutes(10));
            var replay = await service.ApplyDecisionFileAsync(acceptedFile);
            var replayedHistory = await store.ReadReviewHistoryAsync(
                "SampleDomain.Ontology",
                "candidate:relation");
            assert(
                replay.Reviews.Count == 1
                && !replay.Reviews[0].Appended
                && replayedHistory.Count == 1
                && replayedHistory[0].Review.ReviewedAt == firstReviewedAt,
                "replaying the same review identity should be idempotent and preserve the original timestamp");

            clock.Advance(TimeSpan.FromMinutes(10));
            var rejected = await service.ApplyDecisionJsonAsync(
                DecisionJson(
                    "candidate:relation",
                    "rejected",
                    "该字段仅用于传输，不提升为业务关系。",
                    ["evidence:record", "evidence:supplier"]));
            assert(rejected.Reviews.Single().Appended, "a later distinct decision should append");
            var relationHistory = await store.ReadReviewHistoryAsync(
                "SampleDomain.Ontology",
                "candidate:relation");
            var effective = await service.ReadEffectiveDecisionsAsync("SampleDomain.Ontology");
            assert(
                relationHistory.Count == 2
                && relationHistory.Select(entry => entry.Review.Decision).SequenceEqual(
                    ["accepted", "rejected"],
                    StringComparer.Ordinal)
                && effective.Single(item => item.Candidate.Id == "candidate:relation")
                    .Review.Review.Decision == "rejected",
                "review history should remain append-only while latest reviewed_at plus review id wins");

            clock.Advance(TimeSpan.FromMinutes(10));
            var reviewedLegacyCandidate = await service.ApplyDecisionJsonAsync(
                DecisionJson(
                    "candidate:legacy",
                    "accepted",
                    "重新审核后确认该规则有效。",
                    ["evidence:record"]));
            assert(
                reviewedLegacyCandidate.Reviews.Single().Appended,
                "a non-pending candidate with preserved review history should remain reviewable");

            var rowCountBeforeInvalid = (await store.ReadReviewHistoryAsync(
                "SampleDomain.Ontology")).Count;
            await ExpectRejectedAsync(
                () => service.ApplyDecisionJsonAsync(
                    DecisionJson(
                        "candidate:relation",
                        "accepted",
                        "证据集合错误。",
                        ["evidence:record"])),
                "expected evidence",
                assert);
            await ExpectRejectedAsync(
                () => service.ApplyDecisionJsonAsync(
                    DecisionJson(
                        "candidate:unreviewed-rejected",
                        "accepted",
                        "不应允许直接翻转。",
                        ["evidence:record"])),
                "neither pending nor previously reviewed",
                assert);
            assert(
                (await store.ReadReviewHistoryAsync("SampleDomain.Ontology")).Count
                    == rowCountBeforeInvalid,
                "failed review validation must not append partial history");

            await AssertInvalidFilesAsync(service, assert);
            assert(
                (await store.ReadReviewHistoryAsync("SampleDomain.Ontology")).Count
                    == rowCountBeforeInvalid,
                "a strict decision-file failure must leave the entire append batch unchanged");

            await store.ReplaceGenerationAsync(generation with
            {
                GenerationId = "generation-2",
                Candidates =
                [
                    generation.Candidates.Single(item => item.Id == "candidate:relation") with
                    {
                        EvidenceIds = ["evidence:record"],
                    },
                    .. generation.Candidates.Where(item => item.Id != "candidate:relation"),
                ],
            });
            var staleEffective = await service.ReadEffectiveDecisionsAsync("SampleDomain.Ontology");
            assert(
                staleEffective.All(item => item.Candidate.Id != "candidate:relation")
                && (await store.ReadReviewHistoryAsync(
                    "SampleDomain.Ontology",
                    "candidate:relation")).Count == 2,
                "generation replacement should preserve history but exclude evidence-stale reviews from effective lookup");

            clock.Advance(TimeSpan.FromMinutes(10));
            await service.ApplyDecisionJsonAsync(
                DecisionJson(
                    "candidate:relation",
                    "superseded",
                    "新证据集合下由后续候选替代。",
                    ["evidence:record"]));
            var refreshedEffective = await service.ReadEffectiveDecisionsAsync("SampleDomain.Ontology");
            assert(
                refreshedEffective.Single(item => item.Candidate.Id == "candidate:relation")
                    .Review.Review.Decision == "superseded",
                "a fresh review with the current evidence snapshot should become effective");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertInvalidFilesAsync(
        BusinessOntologyReviewService service,
        Action<bool, string> assert)
    {
        var invalidCases = new (string Json, string Message)[]
        {
            (
                """
                {"ontologyId":"SampleDomain.Ontology","decisions":[],"unknown":true}
                """,
                "not an allowed field"),
            (
                """
                {"ontologyId":"SampleDomain.Ontology","ontologyId":"Other.Ontology","decisions":[]}
                """,
                "duplicated"),
            (
                """
                {"ontologyId":"SampleDomain.Ontology","decisions":[{"candidateId":"candidate:relation","decision":"accepted","reviewer":"user:test","expectedEvidenceIds":["evidence:record","evidence:supplier"]}]}
                """,
                "missing required field"),
            (
                DecisionJson(
                    "candidate:relation",
                    "approved",
                    "非法 decision。",
                    ["evidence:record", "evidence:supplier"]),
                "accepted, rejected, or superseded"),
            (
                """
                {"ontologyId":"SampleDomain.Ontology","decisions":[{"candidateId":"candidate:relation","decision":"accepted","reviewer":" ","rationale":"有效说明","expectedEvidenceIds":["evidence:record","evidence:supplier"]}]}
                """,
                "non-empty string"),
            (
                """
                {"ontologyId":"SampleDomain.Ontology","decisions":[{"candidateId":"candidate:relation","decision":"accepted","reviewer":"user:test","rationale":"有效说明","expectedEvidenceIds":["evidence:record","evidence:record"]}]}
                """,
                "duplicate identities"),
            (
                DecisionJson(
                    "candidate:missing",
                    "accepted",
                    "不存在。",
                    ["evidence:record"]),
                "outside the active generation"),
            (
                """
                {
                  "ontologyId": "SampleDomain.Ontology",
                  "decisions": [
                    {
                      "candidateId": "candidate:relation",
                      "decision": "accepted",
                      "reviewer": "user:fixture",
                      "rationale": "本项本身有效。",
                      "expectedEvidenceIds": ["evidence:record", "evidence:supplier"]
                    },
                    {
                      "candidateId": "candidate:missing",
                      "decision": "accepted",
                      "reviewer": "user:fixture",
                      "rationale": "第二项无效，整批必须回滚。",
                      "expectedEvidenceIds": ["evidence:record"]
                    }
                  ]
                }
                """,
                "outside the active generation"),
        };

        foreach (var (json, message) in invalidCases)
        {
            await ExpectRejectedAsync(
                () => service.ApplyDecisionJsonAsync(json),
                message,
                assert);
        }
    }

    private static async Task ExpectRejectedAsync(
        Func<Task> action,
        string expectedMessage,
        Action<bool, string> assert)
    {
        var rejected = false;
        try
        {
            await action();
        }
        catch (ArgumentException ex)
        {
            rejected = ex.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase);
        }
        assert(rejected, $"invalid review input should be rejected with '{expectedMessage}'");
    }

    private static string DecisionJson(
        string candidateId,
        string decision,
        string rationale,
        IReadOnlyList<string> evidenceIds) =>
        $$"""
        {
          "ontologyId": "SampleDomain.Ontology",
          "decisions": [
            {
              "candidateId": "{{candidateId}}",
              "decision": "{{decision}}",
              "reviewer": "user:fixture",
              "rationale": "{{rationale}}",
              "expectedEvidenceIds": [{{string.Join(",", evidenceIds.Select(id => $"\"{id}\""))}}]
            }
          ]
        }
        """;

    private static BusinessOntologyGenerationInput SampleGeneration() => new(
        "SampleDomain.Ontology",
        "generation-1",
        "fingerprint-review",
        "onto-semantic/1",
        "2026-07-18T07:50:00Z",
        [
            new BusinessOntologyConcept(
                "SampleDomain.Ontology.Record",
                "record",
                "记录",
                "可管理记录",
                "accepted",
                0.95,
                ["evidence:record"]),
            new BusinessOntologyConcept(
                "SampleDomain.Ontology.Supplier",
                "party",
                "供应商",
                "记录供应方",
                "accepted",
                0.95,
                ["evidence:supplier"]),
        ],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [
            new BusinessOntologyEvidence(
                "evidence:record",
                "is-record-new",
                "src/Record.java",
                "java:Record",
                10,
                30,
                "contractual",
                "treesitter",
                0.95,
                "code",
                "记录声明"),
            new BusinessOntologyEvidence(
                "evidence:supplier",
                "is-record-new",
                "src/Supplier.java",
                "java:Supplier",
                5,
                18,
                "contractual",
                "treesitter",
                0.95,
                "code",
                "供应商声明"),
        ],
        [
            new BusinessOntologyCandidate(
                "candidate:relation",
                "relation",
                "SampleDomain.Ontology.Relation.HasSupplier",
                "{}",
                "直接类型引用",
                0.9,
                "pending",
                ["evidence:record", "evidence:supplier"]),
            new BusinessOntologyCandidate(
                "candidate:legacy",
                "rule",
                "SampleDomain.Ontology.Rule.RecordRequired",
                "{}",
                "旧审核候选",
                0.9,
                "rejected",
                ["evidence:record"]),
            new BusinessOntologyCandidate(
                "candidate:unreviewed-rejected",
                "rule",
                "SampleDomain.Ontology.Rule.RecordUnique",
                "{}",
                "未审核的非 pending 候选",
                0.9,
                "rejected",
                ["evidence:record"]),
        ],
        [
            new BusinessOntologyReview(
                "review:legacy",
                "candidate:legacy",
                "rejected",
                "legacy:fixture",
                "历史审核",
                "2026-07-17T08:00:00Z"),
        ],
        []);

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset current = current;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }
}
