using System.Xml.Linq;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyXmlExporterTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "onto-xml-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new CozoDb("mem", "");
            var om = new CozoOm(db);
            var store = new BusinessOntologyStore(om);
            await store.ReplaceGenerationAsync(Sample());
            var output = Path.Combine(root, "bundle");
            var result = await new BusinessOntologyXmlExporter(store).ExportAsync(new BusinessOntologyXmlExportRequest("SampleDomain.Ontology", output));
            assert(result.Concepts == 2 && result.Relations == 1 && result.Rules == 1 && result.Lifecycles == 1, "export should preserve every exportable ontology declaration kind");
            var rootDocument = XDocument.Load(Path.Combine(output, "ontology.xml"));
            assert(rootDocument.Root?.Name.LocalName == "Ontology" && rootDocument.Root.Attribute("id")?.Value == "SampleDomain.Ontology", "export should write the DSL ontology root");
            var typeXml = await File.ReadAllTextAsync(Path.Combine(output, "types", "generated.xml"));
            assert(typeXml.Contains("业务概念", StringComparison.Ordinal) && typeXml.Contains("status=\"hypothesis\"", StringComparison.Ordinal), "exported semantic descriptions should be Chinese and retain hypothesis status");
            var evidenceXml = await File.ReadAllTextAsync(Path.Combine(output, "evidence", "generated.xml"));
            assert(evidenceXml.Contains("grade=\"inferred\"", StringComparison.Ordinal) && evidenceXml.Contains("src/main/java", StringComparison.Ordinal), "generated XML should retain evidence grade and repository-relative source locations");
            var audit = await File.ReadAllTextAsync(Path.Combine(output, "generation", "candidates.json"));
            assert(audit.Contains("candidate:unreviewed", StringComparison.Ordinal) && !typeXml.Contains("candidate:unreviewed", StringComparison.Ordinal), "pending candidates must be audit-only, not ontology declarations");
            var rejected = false;
            try { await new BusinessOntologyXmlExporter(store).ExportAsync(new BusinessOntologyXmlExportRequest("SampleDomain.Ontology", output)); }
            catch (IOException) { rejected = true; }
            assert(rejected, "export must reject a non-empty output directory");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static BusinessOntologyGenerationInput Sample() => new(
        "SampleDomain.Ontology", "export-fixture", "fixture", "fixture-exporter", "2026-07-17T10:20:00Z",
        [new BusinessOntologyConcept("SampleDomain.Ontology.Record", "businessObject", "记录", "记录台账中的业务概念。", "hypothesis", 0.8, ["e:record"]), new BusinessOntologyConcept("SampleDomain.Ontology.RecordRequest", "businessObject", "记录申请", "记录申请业务概念。", "hypothesis", 0.75, ["e:request"])],
        [new BusinessOntologyAttribute("SampleDomain.Ontology.Record", "recordCode", "String", true, "记录编码。", "hypothesis", 0.8, ["e:record"])],
        [new BusinessOntologyRelation("SampleDomain.Ontology.Relation.RequestsRecord", "requestsRecord", "SampleDomain.Ontology.RecordRequest", "SampleDomain.Ontology.Record", true, "0", "*", "记录申请关联记录。", "hypothesis", 0.75, ["e:request"])],
        [new BusinessOntologyRule("SampleDomain.Ontology.Rule.RecordCodeRequired", "SampleDomain.Ontology.Record", "Conditional", "记录编码必须存在。", "{\"property\":\"recordCode\"}", "", "hypothesis", 0.8, ["e:record"])],
        [new BusinessOntologyLifecycle("SampleDomain.Ontology.Lifecycle.RecordRequest", "SampleDomain.Ontology.RecordRequest", "workflowState", "draft", "记录申请生命周期。", "hypothesis", 0.75, ["e:request"])],
        [new BusinessOntologyState("SampleDomain.Ontology.Lifecycle.RecordRequest", "draft", false, "草稿"), new BusinessOntologyState("SampleDomain.Ontology.Lifecycle.RecordRequest", "submitted", true, "已提交")],
        [new BusinessOntologyTransition("SampleDomain.Ontology.Transition.SubmitRequest", "SampleDomain.Ontology.Lifecycle.RecordRequest", "submit", "draft", "submitted", "提交记录申请。", "", "", "hypothesis", 0.75, ["e:request"])],
        [new BusinessOntologyMapping("SampleDomain.Ontology.Mapping.Record", "concept", "SampleDomain.Ontology.Record", "representedBy", "is-record-new", "java", "dto", "java:RecordDto", "src/main/java/example/RecordDto.java", "tree-sitter-java", 0.8, "hypothesis", ["e:record"])],
        [new BusinessOntologyEvidence("e:record", "is-record-new", "src/main/java/example/RecordDto.java", "java:RecordDto", 3, 20, "inferred", "tree-sitter-java", 0.8, "code", "从记录 DTO 推导的证据。"), new BusinessOntologyEvidence("e:request", "is-record-new", "src/main/java/example/RecordRequestDto.java", "java:RecordRequestDto", 3, 20, "inferred", "tree-sitter-java", 0.75, "code", "从记录申请 DTO 推导的证据。")],
        [new BusinessOntologyCandidate("candidate:unreviewed", "concept", "SampleDomain.Ontology.Unknown", "{}", "待审候选", 0.4, "pending", ["e:record"])], [], []);
}
