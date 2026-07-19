using System.Text.Json;
using System.Text.Json.Nodes;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyInvestigationServiceTests
{
    private const string OntologyId = "SampleDomain.Ontology";

    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "onto-investigation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "RecordService.java"), """
            class RecordService {
                void createRecord() {
                    if (record.state == DRAFT) record.state = SUBMITTED;
                }
            }
            """);
        try
        {
            using var db = new CozoDb("mem", "");
            var om = new CozoOm(db);
            await om.InitCodeKnowledgeAsync();
            var payload = JsonSerializer.Serialize(new { property = "state", allowedValues = new[] { "DRAFT" } });
            var claimId = CodeSemanticClaimIdentity.Create("file:record", CodeSemanticClaimKinds.BusinessGuard, payload, 3, 3);
            await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Repositories: [new CodeRepositoryFact("repo:sample", root, "sample")],
                Files: [new CodeFileFact("file:record", "repo:sample", "src/RecordService.java", "java")],
                Symbols: [new CodeSymbolFact("symbol:record:create", "file:record", "createRecord", "method", 2, 4, Lang: "java")],
                SemanticClaims: [new CodeSemanticClaimFact(claimId, "symbol:record:create", CodeSemanticClaimKinds.BusinessGuard, payload, "file:record", 3, 3, 0.95, "treesitter", "只有草稿记录可以提交")],
                EntryPoints: [new CodeEntryPointFact("symbol:record:create", "http_route", "POST /records")]));

            var store = new BusinessOntologyStore(om);
            await store.ReplaceGenerationAsync(new BusinessOntologyGenerationInput(
                OntologyId, "fixture-1", "fixture", "fixture", "2026-07-19T00:00:00Z",
                [new BusinessOntologyConcept(OntologyId + ".Record", "record", "记录", "受管理的资产记录", "accepted", 0.9, ["evidence:record"])],
                [], [], [], [], [], [],
                [new BusinessOntologyMapping(OntologyId + ".Mapping.Record", "concept", OntologyId + ".Record", "representedBy", "sample", "java", "entity", "RecordService", "src/RecordService.java", "fixture", 0.9, "accepted", ["evidence:record"])],
                [new BusinessOntologyEvidence("evidence:record", "sample", "src/RecordService.java", "createRecord", 2, 4, "contractual", "fixture", 0.9, "code", "记录创建服务")],
                [new BusinessOntologyCandidate("candidate:record", "concept", OntologyId + ".RecordDraft", "{}", "待审核候选", 0.5, "pending", ["evidence:record"])],
                [new BusinessOntologyReview("review:record", "candidate:record", "rejected", "fixture", "尚无足够证据", "2026-07-19T00:00:00Z")], []));

            var service = new BusinessOntologyInvestigationService(om, store);
            var before = await CountsAsync(om, ["ck_semantic_claim", "onto_concept", "onto_candidate", "onto_review"]);

            var overview = await service.GetOverviewAsync(OntologyId);
            assert(overview.GenerationId == "fixture-1" && overview.Counts["ck_semantic_claim"] == 1,
                "overview should expose bounded indexed and active-generation counts");
            assert(overview.Operations.Contains("get_semantic_evidence", StringComparer.Ordinal),
                "overview should advertise only fixed investigation operations");

            var firstPage = await service.FindBusinessTermsAsync("record", OntologyId, limit: 1);
            assert(firstPage.Items.Count == 1 && firstPage.Truncated && !string.IsNullOrEmpty(firstPage.NextCursor),
                "term search should use a stable bounded page and continuation cursor");
            var secondPage = await service.FindBusinessTermsAsync("record", OntologyId, firstPage.NextCursor, 10);
            assert(secondPage.Items.Count > 0 && !secondPage.Items.Select(item => item.Id).Intersect(firstPage.Items.Select(item => item.Id), StringComparer.Ordinal).Any(),
                "term search continuation should not repeat the prior item");
            var crossQueryRejected = false;
            try { await service.FindSemanticPatternsAsync(CodeSemanticClaimKinds.BusinessGuard, cursor: firstPage.NextCursor); }
            catch (ArgumentException) { crossQueryRejected = true; }
            assert(crossQueryRejected, "a cursor must be bound to its operation and normalized filters");

            var patterns = await service.FindSemanticPatternsAsync(CodeSemanticClaimKinds.BusinessGuard, "record");
            assert(patterns.Items.Single().ClaimId == claimId && patterns.Items.Single().EvidenceRefs.Single().Path == "src/RecordService.java",
                "semantic patterns should retain direct indexed evidence references");
            var evidence = await service.GetSemanticEvidenceAsync([claimId]);
            assert(evidence.Anchors.Single().SourceExcerpt.Contains("DRAFT", StringComparison.Ordinal) && evidence.SourceUtf8Bytes <= SemanticEvidencePackBuilder.MaxSourceUtf8Bytes,
                "evidence lookup should be id-based and remain within the source-byte budget");

            var inspection = await service.InspectOntologySubjectAsync(OntologyId, "candidate", "candidate:record");
            assert(inspection.Subject is BusinessOntologyCandidate { Status: "pending" } && inspection.Reviews.Single().Decision == "rejected",
                "candidate inspection must preserve pending status and review instead of treating it as a source fact");

            var unknownEvidenceRejected = false;
            try { await service.GetSemanticEvidenceAsync(["/tmp/not-an-evidence-id"]); }
            catch (ArgumentException) { unknownEvidenceRejected = true; }
            assert(unknownEvidenceRejected, "evidence lookup must reject arbitrary paths and unknown ids");

            var runner = new LlmWikiToolRunner(om);
            var toolNames = LlmWikiToolRunner.ToolsJson()
                .Select(tool => tool?["name"]?.GetValue<string>())
                .Where(name => name is not null)
                .ToHashSet(StringComparer.Ordinal);
            assert(new[] { "ontology_investigation_overview", "find_business_terms", "list_use_case_slices", "get_use_case_slice", "find_semantic_patterns", "get_semantic_evidence", "inspect_ontology_subject" }.All(toolNames.Contains),
                "shared tool metadata should expose every fixed investigation operation");
            var runnerPatterns = (BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern>)await runner.CallAsync("find_semantic_patterns", new JsonObject
            {
                ["kind"] = CodeSemanticClaimKinds.BusinessGuard,
                ["term"] = "record",
            });
            assert(runnerPatterns.Items.Single().ClaimId == claimId,
                "runner dispatch should reuse the read-only investigation service");

            var after = await CountsAsync(om, ["ck_semantic_claim", "onto_concept", "onto_candidate", "onto_review"]);
            assert(before.OrderBy(item => item.Key).SequenceEqual(after.OrderBy(item => item.Key)),
                "investigation operations must not mutate indexed or ontology facts");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<IReadOnlyDictionary<string, int>> CountsAsync(CozoOm om, IReadOnlyList<string> relations)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var relation in relations)
        {
            var key = relation switch
            {
                "ck_semantic_claim" => "claim_id",
                "onto_concept" => "concept_id",
                "onto_candidate" => "candidate_id",
                "onto_review" => "review_id",
                _ => throw new ArgumentOutOfRangeException(nameof(relations)),
            };
            var rows = await om.Runtime.Store.RunAsync($"?[count(value)] := *{relation}{{{key}: value}}");
            values[relation] = rows.Rows[0][0].GetInt32();
        }
        return values;
    }
}
