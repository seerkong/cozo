using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyStoreTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.Runtime.Store.RunAsync(":create ck_sentinel {id => value}");
        await om.Runtime.Store.RunAsync(":create depa_sentinel {id => value}");
        await om.Runtime.Store.RunAsync("?[id, value] <- [[\"ck\", \"unchanged\"]] :put ck_sentinel {id => value}");
        await om.Runtime.Store.RunAsync("?[id, value] <- [[\"depa\", \"unchanged\"]] :put depa_sentinel {id => value}");

        var store = new BusinessOntologyStore(om);
        await store.InitializeAsync();
        var relations = await om.Runtime.Store.RunAsync("::relations");
        var names = relations.Rows.Select(row => row[0].GetString() ?? "").ToHashSet(StringComparer.Ordinal);
        foreach (var name in BusinessOntologyStore.RequiredRelationNames)
        {
            assert(names.Contains(name), $"onto storage should initialize {name}");
        }
        assert(names.Contains("ck_sentinel") && names.Contains("depa_sentinel"), "onto initialization must preserve existing observation and DEPA relations");

        var first = Sample("SampleDomain.Ontology", "generation-1");
        await store.ReplaceGenerationAsync(first);
        await store.AppendReviewsAsync(
            "SampleDomain.Ontology",
            "generation-1",
            [
                new BusinessOntologyReviewEntry(
                    new BusinessOntologyReview(
                        "review:generation-1",
                        "candidate:legacy",
                        "rejected",
                        "user:fixture",
                        "错误 generation 清理后仍需保留审核历史。",
                        "2026-07-18T00:00:00.0000000+00:00"),
                    ["inferred:application"]),
            ]);
        var snapshot = await store.ReadExportableAsync("SampleDomain.Ontology");
        assert(snapshot.GenerationId == "generation-1", "exportable view should select the active generation");
        assert(snapshot.Concepts.Count == 2 && snapshot.Concepts.All(item => item.Status is "accepted" or "hypothesis"), "accepted and hypothesis concepts should be exportable");
        assert(snapshot.Attributes.Count == 1 && snapshot.Relations.Count == 1 && snapshot.Rules.Count == 1, "exportable view should retain semantic objects");
        assert(snapshot.Lifecycles.Count == 1 && snapshot.States.Count == 2 && snapshot.Transitions.Count == 1, "exportable view should retain lifecycle objects");
        assert(snapshot.Mappings.Count == 1 && snapshot.Evidence.Count == 2, "exportable view should retain mappings and direct evidence");
        assert(snapshot.Candidates.Count == 2 && snapshot.Candidates.All(item => item.Status is "pending" or "rejected"), "candidates stay queryable but are not ontology declarations");

        await store.ReplaceGenerationAsync(first);
        var repeated = await store.ReadExportableAsync("SampleDomain.Ontology");
        assert(repeated.Concepts.Count == snapshot.Concepts.Count && repeated.Evidence.Count == snapshot.Evidence.Count, "identical generation writes must be idempotent");

        await store.ReplaceGenerationAsync(Sample("ItRecordInventory.Ontology", "inventory-1"));
        await store.ReplaceGenerationAsync(Sample("SampleDomain.Ontology", "generation-2") with
        {
            Concepts = [new BusinessOntologyConcept("SampleDomain.Ontology.Record", "record", "记录", "记录台账中的可管理记录", "accepted", 0.95, ["backend:record"])],
            Attributes = [], Relations = [], Rules = [], Lifecycles = [], States = [], Transitions = [], Mappings = [], Candidates = [], Reviews = []
        });
        var refreshed = await store.ReadExportableAsync("SampleDomain.Ontology");
        var otherOntology = await store.ReadExportableAsync("ItRecordInventory.Ontology");
        assert(refreshed.GenerationId == "generation-2" && refreshed.Concepts.Count == 1, "refresh should replace only the active ontology generation");
        assert(otherOntology.GenerationId == "inventory-1" && otherOntology.Concepts.Count == 2, "refresh must not replace another ontology generation");
        var sentinel = await om.Runtime.Store.RunAsync("?[value] := *ck_sentinel{id: \"ck\", value}");
        var depaSentinel = await om.Runtime.Store.RunAsync("?[value] := *depa_sentinel{id: \"depa\", value}");
        assert(sentinel.Rows.Single()[0].GetString() == "unchanged" && depaSentinel.Rows.Single()[0].GetString() == "unchanged", "generation refresh must preserve ck and depa relations");

        var stalePurgeRejected = false;
        try
        {
            await store.PurgeGenerationAsync(
                "SampleDomain.Ontology",
                "generation-1",
                "wrong active generation");
        }
        catch (InvalidOperationException ex)
        {
            stalePurgeRejected = ex.Message.Contains("active generation", StringComparison.Ordinal);
        }
        assert(stalePurgeRejected, "purge must reject a non-active generation id instead of fuzzy cleanup");

        var purge = await store.PurgeGenerationAsync(
            "SampleDomain.Ontology",
            "generation-2",
            "remove incorrect semantic dogfood generation");
        assert(
            purge.Purged
            && purge.TombstoneWritten
            && purge.OntologyId == "SampleDomain.Ontology"
            && purge.GenerationId == "generation-2",
            "purge should return a machine-readable summary for the exact generation");
        var generationRows = await om.Runtime.Store.RunAsync(
            "?[generation_id] := *onto_generation{ontology_id: \"SampleDomain.Ontology\", generation_id}");
        var conceptRows = await om.Runtime.Store.RunAsync(
            "?[concept_id] := *onto_concept{ontology_id: \"SampleDomain.Ontology\", generation_id: \"generation-2\", concept_id}");
        var reviewRows = await om.Runtime.Store.RunAsync(
            "?[review_id] := *onto_review{ontology_id: \"SampleDomain.Ontology\", review_id}");
        var expectationRows = await om.Runtime.Store.RunAsync(
            "?[evidence_id] := *onto_review_expectation{ontology_id: \"SampleDomain.Ontology\", review_id: \"review:generation-1\", evidence_id}");
        var otherAfterPurge = await store.ReadExportableAsync("ItRecordInventory.Ontology");
        sentinel = await om.Runtime.Store.RunAsync("?[value] := *ck_sentinel{id: \"ck\", value}");
        depaSentinel = await om.Runtime.Store.RunAsync("?[value] := *depa_sentinel{id: \"depa\", value}");
        assert(
            generationRows.Rows.Count == 0
            && conceptRows.Rows.Count == 0
            && reviewRows.Rows.Count == 2
            && expectationRows.Rows.Count == 1
            && otherAfterPurge.GenerationId == "inventory-1"
            && sentinel.Rows.Single()[0].GetString() == "unchanged"
            && depaSentinel.Rows.Single()[0].GetString() == "unchanged",
            "purge must delete only active generation-scoped onto rows while preserving review, ck/depa, and other ontology data");

        var tombstonedRejected = false;
        try
        {
            await store.ReplaceGenerationAsync(first with { GenerationId = "generation-2" });
        }
        catch (InvalidOperationException ex)
        {
            tombstonedRejected = ex.Message.Contains("tombstone", StringComparison.OrdinalIgnoreCase);
        }
        assert(tombstonedRejected, "tombstoned ontology-id and generation-id pairs must not be reusable");

        var rejected = false;
        try
        {
            await store.ReplaceGenerationAsync(first with
            {
                GenerationId = "invalid-path",
                Evidence = [new BusinessOntologyEvidence("backend:record", "is-record-new", "/absolute/Record.java", "java:Record", 1, 2, "contractual", "treesitter", 0.9, "code", "记录契约")]
            });
        }
        catch (ArgumentException ex)
        {
            rejected = ex.Message.Contains("repository-relative", StringComparison.Ordinal);
        }
        assert(rejected, "absolute evidence paths must be rejected before persistence");

        rejected = false;
        try
        {
            await store.ReplaceGenerationAsync(first with
            {
                GenerationId = "invalid-hypothesis",
                Concepts = [new BusinessOntologyConcept("SampleDomain.Ontology.RecordGuess", "record", "记录猜测", "命名推断", "hypothesis", 0.6, ["backend:record"])]
            });
        }
        catch (ArgumentException ex)
        {
            rejected = ex.Message.Contains("inferred evidence", StringComparison.Ordinal);
        }
        assert(rejected, "hypotheses without inferred direct evidence must be rejected");
    }

    private static BusinessOntologyGenerationInput Sample(string ontologyId, string generationId) => new(
        ontologyId,
        generationId,
        "fingerprint-1",
        "onto-projector/1",
        "2026-07-17T09:14:10Z",
        [
            new BusinessOntologyConcept($"{ontologyId}.Record", "record", "记录", "受管的记录台账对象", "accepted", 0.95, ["backend:record"]),
            new BusinessOntologyConcept($"{ontologyId}.RecordApplication", "document", "记录申请单", "记录申请流程单据", "hypothesis", 0.72, ["inferred:application"]),
        ],
        [new BusinessOntologyAttribute($"{ontologyId}.Record", "recordCode", "String", true, "记录编码", "accepted", 0.95, ["backend:record"])],
        [new BusinessOntologyRelation($"{ontologyId}.Relation.AppliedFor", "appliedFor", $"{ontologyId}.RecordApplication", $"{ontologyId}.Record", true, "0", "*", "申请单关联的记录", "accepted", 0.82, ["backend:record"])],
        [new BusinessOntologyRule($"{ontologyId}.Rule.RecordCodeRequired", $"{ontologyId}.Record", "required", "记录编码不能为空", "{\"property\":\"recordCode\"}", "", "accepted", 0.95, ["backend:record"])],
        [new BusinessOntologyLifecycle($"{ontologyId}.Lifecycle.RecordApplication", $"{ontologyId}.RecordApplication", "workflowState", "draft", "申请单生命周期", "hypothesis", 0.72, ["inferred:application"])],
        [
            new BusinessOntologyState($"{ontologyId}.Lifecycle.RecordApplication", "draft", false, "草稿"),
            new BusinessOntologyState($"{ontologyId}.Lifecycle.RecordApplication", "submitted", true, "已提交"),
        ],
        [new BusinessOntologyTransition($"{ontologyId}.Transition.Submit", $"{ontologyId}.Lifecycle.RecordApplication", "submit", "draft", "submitted", "提交申请", "", "{\"set\":\"workflowState\"}", "hypothesis", 0.72, ["inferred:application"])],
        [new BusinessOntologyMapping($"{ontologyId}.Implementation.Record", "concept", $"{ontologyId}.Record", "representedBy", "is-record-new", "java", "dto", "java:RecordDto", "src/main/java/example/RecordDto.java", "treesitter", 0.9, "accepted", ["backend:record"])],
        [
            new BusinessOntologyEvidence("backend:record", "is-record-new", "src/main/java/example/RecordDto.java", "java:RecordDto", 12, 36, "contractual", "treesitter", 0.95, "code", "记录 DTO 契约"),
            new BusinessOntologyEvidence("inferred:application", "is-record-fe", "src/pages/record/apply/index.tsx", "ts:RecordApplyPage", 8, 42, "inferred", "llm", 0.72, "code", "申请页面与命名推断"),
        ],
        [
            new BusinessOntologyCandidate("candidate:record-lifecycle", "lifecycle", $"{ontologyId}.Lifecycle.RecordApplication", "{\"source\":\"page\"}", "页面和命名一致", 0.72, "pending", ["inferred:application"]),
            new BusinessOntologyCandidate("candidate:legacy", "concept", $"{ontologyId}.Legacy", "{}", "已否决示例", 0.2, "rejected", ["inferred:application"]),
        ],
        [new BusinessOntologyReview("review:legacy", "candidate:legacy", "rejected", "fixture", "不属于业务概念", "2026-07-17T09:14:10Z")],
        []);
}
