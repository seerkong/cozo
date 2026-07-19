using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyQualityReportBuilderTests
{
    private const string OntologyId = "SampleDomain.Ontology";
    private const string Record = OntologyId + ".Record";
    private const string Supplier = OntologyId + ".Supplier";

    public static async Task RunAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/abs/root/that/must/not/leak", "record")],
            Files:
            [
                new CodeFileFact("file:record-entity", "repo:record", "src/main/java/demo/RecordEntity.java", "java"),
                new CodeFileFact("file:record-service", "repo:record", "src/main/java/demo/RecordService.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:record-entity", "file:record-entity", "RecordEntity", "class", 3, 80, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record-code", "file:record-entity", "recordCode", "field", 12, 12, ParentId: "symbol:record-entity", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record-service", "file:record-service", "RecordService", "class", 4, 120, Lang: "java", Resolver: "tree-sitter-java"),
            ],
            SemanticClaims:
            [
                Claim("symbol:record-code", CodeSemanticClaimKinds.ValidationConstraint, "file:record-entity", 12, """{"annotation":"NotBlank","member":"recordCode","operator":"present"}"""),
                Claim("symbol:record-code", CodeSemanticClaimKinds.PersistenceConstraint, "file:record-entity", 12, """{"annotation":"Column","member":"recordCode","constraint":"unique"}"""),
            ]));

        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(Generation());
        var builder = new BusinessOntologyQualityReportBuilder(om, store);
        var report = await builder.BuildAsync(new BusinessOntologyQualityReportRequest(OntologyId));

        assert(report.OntologyId == OntologyId && report.GenerationId == "quality-fixture", "report should identify the active ontology generation");
        assert(report.RawImplementationCarriers.Count == 3, "raw carriers should be counted from implementation candidates only");
        assert(report.CanonicalConcepts.Count == 2, "canonical concepts should come from snapshot concepts, not carrier candidates");
        assert(report.Consolidation.MappedCarrierCount == 2
                && report.Consolidation.UnconsolidatedCarrierCount == 1
                && report.Consolidation.CarriersPerConcept.Single(item => item.ConceptId == Record).CarrierCount == 2,
            "carrier-to-concept consolidation should use implementation candidate source symbols and concept mappings");
        assert(report.ConceptPollution.PollutedConceptCount == 1
                && report.ConceptPollution.TechnicalSuffixHits.Single().Suffix == "DTO"
                && Math.Abs(report.ConceptPollution.PollutionRatio - 0.5) < 0.0001,
            "concept pollution should inspect canonical concept names and not count raw carriers as concepts");
        assert(report.AttributePollution.PollutedAttributeCount == 2
                && report.AttributePollution.PollutedAttributes.SequenceEqual(["cacheLock", "syncDataEntityList"], StringComparer.Ordinal),
            "attribute pollution should catch cache/lock/list implementation fields on canonical concepts");
        assert(report.FieldConstraints.Count == 2
                && report.FieldConstraints.ByKind["validation_constraint"] == 1
                && report.FieldConstraints.ByKind["persistence_constraint"] == 1,
            "field constraints should be counted directly from ck_semantic_claim and remain separate from business rules");
        assert(report.BusinessRules.Count == 1
                && report.BusinessRules.PendingCandidateCount == 1
                && report.BusinessRules.DirectOnlyDiagnosticCount == 0
                && report.BusinessRules.BusinessConditionCount == 1
                && report.BusinessRules.RepresentativeAnchors.Any(item => item.Kind == "businessCondition")
                && report.BusinessRules.RepresentativeAnchors.Count <= BusinessOntologyQualityReportBuilder.MaxRepresentativeAnchors,
            "business rules should count semantic candidate rules, especially businessCondition, not field constraints");
        assert(report.Relations.Count == 1
                && report.Relations.PendingCandidateCount == 1
                && report.Relations.DirectOnlyDiagnosticCount == 0
                && report.Relations.CrossFileCoveredCount == 1,
            "actual relation candidate coverage should be counted separately from direct-only diagnostics");
        assert(report.Lifecycles.Count == 1
                && report.Lifecycles.TransitionCount == 1
                && report.Lifecycles.CrossFileCoveredCount == 0,
            "lifecycle and transition counts should be reported separately from relations and rules");
        assert(report.SemanticCoverage.CandidateCount == 3
                && report.SemanticCoverage.ByKind.Keys.Order(StringComparer.Ordinal).SequenceEqual(["lifecycle", "relation", "rule"], StringComparer.Ordinal)
                && report.SemanticCoverage.UseCaseEvidenceCoveredCount > 0,
            "semantic coverage should summarize observed candidate kinds and evidence without prescribing a domain workflow");
        assert(report.UseCaseEvidence.Count == 1 && report.CrossFileEvidence.UnmetItems.Single().Kind == "lifecycle", "use-case evidence and unmet cross-file candidate coverage should be explicit");
        assert(report.QualityVerdict.Status == "GAP"
                && report.QualityVerdict.Diagnostics.Any(item => item.Kind == "concept_pollution_high")
                && report.QualityVerdict.Diagnostics.Any(item => item.Kind == "attribute_pollution_present")
                && report.QualityVerdict.Diagnostics.Any(item => item.Kind == "cross_file_evidence_missing"),
            "quality verdict should honestly return GAP when pollution or evidence coverage fails");
        assert(report.FieldConstraints.RepresentativeAnchors.All(item => !Path.IsPathRooted(item.Path))
                && report.BusinessRules.RepresentativeAnchors.All(item => !Path.IsPathRooted(item.Path))
                && JsonSerializer.Serialize(report).Contains("root/that/must/not/leak", StringComparison.Ordinal) == false,
            "representative anchors must be repository-relative and must not leak absolute repository roots");

        var output = Path.Combine(Path.GetTempPath(), "onto-quality-export-" + Guid.NewGuid().ToString("N"));
        var exported = await new BusinessOntologyXmlExporter(store, builder)
            .ExportAsync(new BusinessOntologyXmlExportRequest(OntologyId, output));
        var reportPath = Path.Combine(output, "generation", "quality-report.json");
        assert(exported.Files.Contains(reportPath, StringComparer.Ordinal)
                && File.Exists(reportPath)
                && (await File.ReadAllTextAsync(reportPath)).Contains("\"qualityVerdict\"", StringComparison.OrdinalIgnoreCase),
            "business ontology XML export should write generation/quality-report.json when a quality report builder is supplied");

        await store.ReplaceGenerationAsync(GenerationWithManyUnmetCandidates());
        var boundedReport = await builder.BuildAsync(new BusinessOntologyQualityReportRequest(OntologyId));
        assert(boundedReport.CrossFileEvidence.UnmetCandidateCount == 10
                && boundedReport.CrossFileEvidence.UnmetItems.Count == BusinessOntologyQualityReportBuilder.MaxRepresentativeAnchors
                && boundedReport.RawImplementationCarriers.Count <= BusinessOntologyQualityReportBuilder.MaxRepresentativeAnchors
                && boundedReport.CanonicalConcepts.Count <= BusinessOntologyQualityReportBuilder.MaxRepresentativeAnchors
                && boundedReport.Consolidation.CarriersPerConcept.Count <= BusinessOntologyQualityReportBuilder.MaxRepresentativeAnchors
                && boundedReport.Consolidation.UnconsolidatedCarriers.Count <= BusinessOntologyQualityReportBuilder.MaxRepresentativeAnchors
                && boundedReport.CrossFileEvidence.CandidateCount
                    == boundedReport.CrossFileEvidence.CoveredCandidateCount
                        + boundedReport.CrossFileEvidence.UnmetCandidateCount,
            "quality report should report full counts while bounding representative samples");

        await store.ReplaceGenerationAsync(GenerationWithUploadRuleAndZeroTransitionLifecycle());
        var uploadReport = await builder.BuildAsync(new BusinessOntologyQualityReportRequest(OntologyId));
        assert(uploadReport.BusinessRules.Count == 1
                && uploadReport.Lifecycles.Count == 1
                && uploadReport.Lifecycles.TransitionCount == 0
                && uploadReport.QualityVerdict.Status == "GAP"
                && uploadReport.QualityVerdict.Diagnostics.Any(item => item.Kind == "lifecycle_transitions_absent"),
            "lifecycle candidates without transitions must remain GAP even when XML/candidate counts are nonzero");

        await store.ReplaceGenerationAsync(GenerationWithPollutedRelations());
        var pollutedRelationReport = await builder.BuildAsync(new BusinessOntologyQualityReportRequest(OntologyId));
        assert(pollutedRelationReport.QualityVerdict.Status == "GAP"
                && pollutedRelationReport.QualityVerdict.Diagnostics.Any(item => item.Kind == "relation_self_pollution")
                && pollutedRelationReport.QualityVerdict.Diagnostics.Any(item => item.Kind == "relation_operation_name_pollution"),
            "quality report should mark operation-like and self relation pollution as GAP without a project-specific signature list");

        await store.ReplaceGenerationAsync(GenerationWithDirectOnlyRelationAndMappingKeywordEvidence());
        var directOnlyReport = await builder.BuildAsync(new BusinessOntologyQualityReportRequest(OntologyId));
        assert(directOnlyReport.Relations.Count == 0
                && directOnlyReport.Relations.PendingCandidateCount == 1
                && directOnlyReport.Relations.DirectOnlyDiagnosticCount == 1
                && directOnlyReport.Relations.CrossFileCoveredCount == 0
                && directOnlyReport.CrossFileEvidence.CandidateCount == 1
                && directOnlyReport.CrossFileEvidence.CoveredCandidateCount == 1
                && directOnlyReport.SemanticCoverage.DirectOnlyRelationDiagnosticCount == 1,
            "quality report must separate one direct-only structural diagnostic from actual cross-layer relation candidates and semantic coverage"
            + $" (relations={directOnlyReport.Relations.Count}/{directOnlyReport.Relations.PendingCandidateCount}/{directOnlyReport.Relations.DirectOnlyDiagnosticCount},"
            + $" crossFile={directOnlyReport.CrossFileEvidence.CandidateCount}/{directOnlyReport.CrossFileEvidence.CoveredCandidateCount},"
            + $" directOnly={directOnlyReport.SemanticCoverage.DirectOnlyRelationDiagnosticCount})");
        assert(directOnlyReport.BusinessRules.RepresentativeAnchors.Count == 1
                && directOnlyReport.BusinessRules.RepresentativeAnchors.All(item =>
                    item.EvidenceId == "evidence:generic-guard")
                && !directOnlyReport.BusinessRules.RepresentativeAnchors.Any(item =>
                    item.EvidenceId is "evidence:repair-request-mapping" or "usecase:slice:generic-action"),
            "representative business anchors should show direct guard evidence instead of a keyword-bearing DTO mapping or use-case summary");

        await store.ReplaceGenerationAsync(GenerationWithMixedSemanticEvidence());
        var mixedAnchorReport = await builder.BuildAsync(new BusinessOntologyQualityReportRequest(OntologyId));
        assert(mixedAnchorReport.Relations.RepresentativeAnchors.Count == 1
                && mixedAnchorReport.Relations.RepresentativeAnchors.Single().EvidenceId == "evidence:relation-typed-field"
                && mixedAnchorReport.Relations.RepresentativeAnchors.Single().Kind == "relation",
            "relation representative anchors must prefer direct structural typed_reference evidence over guard, state, mapping, and use-case enrichment evidence");
        assert(mixedAnchorReport.BusinessRules.RepresentativeAnchors.Count == 1
                && mixedAnchorReport.BusinessRules.RepresentativeAnchors.Single().EvidenceId == "evidence:rule-business-guard"
                && mixedAnchorReport.BusinessRules.RepresentativeAnchors.Single().Kind == "businessCondition",
            "business-rule representative anchors must use direct business_guard evidence and must not be represented by mappings or use-case summaries");
        assert(mixedAnchorReport.Lifecycles.RepresentativeAnchors.Count == 3
                && mixedAnchorReport.Lifecycles.RepresentativeAnchors.Select(item => item.EvidenceId).SequenceEqual(
                    ["evidence:lifecycle-state-assignment", "evidence:lifecycle-state-field", "evidence:lifecycle-state-value"],
                    StringComparer.Ordinal)
                && mixedAnchorReport.Lifecycles.RepresentativeAnchors.All(item => item.Kind == "lifecycle"),
            "lifecycle representative anchors must use direct state evidence and keep a deterministic bounded order");
        assert(mixedAnchorReport.SemanticCoverage.RepresentativeAnchors.All(item =>
                    item.Kind is "relation" or "businessCondition" or "lifecycle")
                && !mixedAnchorReport.SemanticCoverage.RepresentativeAnchors.Any(item =>
                    item.EvidenceId == "usecase:slice:mixed" || item.EvidenceId == "evidence:record-mapping"),
            "semantic coverage anchors must inherit accurate semantic kind anchors instead of promoting use-case or mapping evidence");
        assert(mixedAnchorReport.UseCaseEvidence.RepresentativeAnchors.Any(item => item.EvidenceId == "usecase:slice:mixed")
                && mixedAnchorReport.UseCaseEvidence.RepresentativeAnchors.All(item => item.Kind == "use-case"),
            "use-case summaries should remain use-case anchors only");

        await store.ReplaceGenerationAsync(GenerationWithoutDirectSemanticAnchors());
        var noDirectAnchorReport = await builder.BuildAsync(new BusinessOntologyQualityReportRequest(OntologyId));
        assert(noDirectAnchorReport.Relations.RepresentativeAnchors.Count == 0
                && noDirectAnchorReport.BusinessRules.RepresentativeAnchors.Count == 0
                && noDirectAnchorReport.Lifecycles.RepresentativeAnchors.Count == 0
                && noDirectAnchorReport.SemanticCoverage.DirectEvidenceCoveredCount == 0
                && noDirectAnchorReport.SemanticCoverage.RepresentativeAnchors.Count == 0
                && noDirectAnchorReport.QualityVerdict.Status == "GAP"
                && noDirectAnchorReport.QualityVerdict.Diagnostics.Any(item => item.Kind == "semantic_direct_evidence_missing"),
            "quality report must expose an honest GAP instead of fabricating PASS anchors from unrelated mapping or use-case evidence");
    }

    private static CodeSemanticClaimFact Claim(
        string subjectId,
        string kind,
        string fileId,
        int line,
        string payload)
    {
        var canonical = CodeSemanticClaimIdentity.CanonicalizePayload(payload);
        return new(
            CodeSemanticClaimIdentity.Create(fileId, kind, canonical, line, line),
            subjectId,
            kind,
            canonical,
            fileId,
            line,
            line,
            0.96,
            "treesitter",
            kind + " fixture");
    }

    private static BusinessOntologyGenerationInput Generation() => new(
        OntologyId,
        "quality-fixture",
        "fingerprint-quality",
        "quality-fixture/1",
        "2026-07-18T12:00:00Z",
        [
            new BusinessOntologyConcept(Record, "businessObject", "Record", "记录业务对象。", "hypothesis", 0.88, ["evidence:record-entity", "evidence:record-dto"]),
            new BusinessOntologyConcept(OntologyId + ".RecordDTO", "businessObject", "RecordDTO", "被污染的技术形态概念，用于质量门禁夹具。", "accepted", 0.8, ["evidence:record-dto"]),
        ],
        [
            new BusinessOntologyAttribute(Record, "recordCode", "String", false, "记录编码。", "hypothesis", 0.9, ["evidence:record-entity"]),
            new BusinessOntologyAttribute(Record, "cacheLock", "String", false, "污染属性夹具。", "hypothesis", 0.9, ["evidence:record-entity"]),
            new BusinessOntologyAttribute(Record, "syncDataEntityList", "String", false, "污染属性夹具。", "hypothesis", 0.9, ["evidence:record-dto"]),
        ],
        [],
        [],
        [],
        [],
        [],
        [
            new BusinessOntologyMapping(OntologyId + ".Mapping.Record.Dto", "concept", Record, "representedBy", "repo:record", "java", "class", "symbol:record-dto", "src/main/java/demo/RecordDto.java", "tree-sitter-java", 0.9, "hypothesis", ["evidence:record-dto"]),
            new BusinessOntologyMapping(OntologyId + ".Mapping.Record.Entity", "concept", Record, "representedBy", "repo:record", "java", "class", "symbol:record-entity", "src/main/java/demo/RecordEntity.java", "tree-sitter-java", 0.9, "hypothesis", ["evidence:record-entity"]),
        ],
        [
            Evidence("evidence:record-dto", "src/main/java/demo/RecordDto.java", "symbol:record-dto", "记录 DTO 载体。", "code", "inferred"),
            Evidence("evidence:record-entity", "src/main/java/demo/RecordEntity.java", "symbol:record-entity", "记录实体载体。", "code", "inferred"),
            Evidence("evidence:record-response", "src/main/java/demo/RecordResponse.java", "symbol:record-response", "未归并响应载体。", "code", "inferred"),
            Evidence("evidence:supplier", "src/main/java/demo/SupplierEntity.java", "symbol:supplier-entity", "供应商实体载体。", "code", "inferred"),
            Evidence("evidence:record-service", "src/main/java/demo/RecordService.java", "symbol:record-service#create", "直接源码语义事实：business_guard。", "code-semantic-claim", "contractual"),
            Evidence("usecase:slice:create-record", "src/main/java/demo/RecordController.java", "symbol:record-controller#create", "创建记录跨文件用例切片。", "usecase_slice", "inferred"),
        ],
        [
            ImplementationCandidate("symbol:record-dto", "RecordDto", "carrier:dto", ["evidence:record-dto"]),
            ImplementationCandidate("symbol:record-entity", "RecordEntity", "carrier:entity", ["evidence:record-entity"]),
            ImplementationCandidate("symbol:record-response", "RecordResponse", "carrier:response", ["evidence:record-response"]),
            new BusinessOntologyCandidate("candidate:semantic:relation", "relation", OntologyId + ".Relation.RecordSupplier", RelationPayload(), "直接类型引用与创建记录用例切片支持记录供应商关系。", 0.82, "pending", ["evidence:record-entity", "evidence:supplier", "usecase:slice:create-record"]),
            new BusinessOntologyCandidate("candidate:semantic:rule", "rule", OntologyId + ".Rule.CreateRecordDraftOnly", BusinessRulePayload(), "guard 与跨文件用例切片共同支持。", 0.84, "pending", ["evidence:record-service", "usecase:slice:create-record"]),
            new BusinessOntologyCandidate("candidate:semantic:lifecycle", "lifecycle", OntologyId + ".Lifecycle.RecordStatus", LifecyclePayload(), "状态 guard 和 setter 共同支持。", 0.78, "pending", ["evidence:record-service"]),
        ],
        [],
        []);

    private static BusinessOntologyGenerationInput GenerationWithManyUnmetCandidates()
    {
        var generation = Generation();
        var extraCandidates = Enumerable.Range(0, 9)
            .Select(index => new BusinessOntologyCandidate(
                $"candidate:semantic:lifecycle:unmet:{index:D2}",
                "lifecycle",
                $"{OntologyId}.Lifecycle.Unmet{index:D2}",
                LifecyclePayload(),
                "单文件证据不足的生命周期候选夹具。",
                0.7,
                "pending",
                ["evidence:record-service"]))
            .ToArray();
        return generation with
        {
            GenerationId = "quality-bounded-sample-fixture",
            SourceFingerprint = "fingerprint-quality-bounded-sample",
            Candidates = generation.Candidates.Concat(extraCandidates).ToArray(),
        };
    }

    private static BusinessOntologyGenerationInput GenerationWithUploadRuleAndZeroTransitionLifecycle()
    {
        var generation = Generation();
        return generation with
        {
            GenerationId = "quality-upload-zero-transition-fixture",
            SourceFingerprint = "fingerprint-quality-upload-zero-transition",
            Attributes = generation.Attributes
                .Where(item => item.Name == "recordCode")
                .ToArray(),
            Evidence = generation.Evidence.Concat(
            [
                Evidence("evidence:file-upload", "src/main/java/demo/FileController.java", "symbol:file#picUpload", "picUpload 文件上传大小 guard。", "code", "enforced"),
                Evidence("usecase:slice:file-upload", "src/main/java/demo/FileController.java", "symbol:file#picUpload", "picUpload 文件上传用例切片。", "usecase_slice", "inferred"),
                Evidence("evidence:shelf-cargo", "src/main/java/demo/ShelfCargoEntity.java", "symbol:shelf-cargo", "货架 cargo 状态声明。", "code", "inferred"),
                Evidence("usecase:slice:shelf-cargo", "src/main/java/demo/ShelfCargoController.java", "symbol:shelf-cargo#save", "货架 cargo 用例切片。", "usecase_slice", "inferred"),
            ]).ToArray(),
            Candidates =
            [
                new BusinessOntologyCandidate("candidate:semantic:rule:upload", "rule", OntologyId + ".Rule.PicUpload", UploadRulePayload(), "picUpload guard 与上传用例切片共同支持。", 0.84, "pending", ["evidence:file-upload", "usecase:slice:file-upload"]),
                new BusinessOntologyCandidate("candidate:semantic:lifecycle:zero-transition", "lifecycle", OntologyId + ".Lifecycle.ShelfCargoState", ZeroTransitionLifecyclePayload(), "货架 cargo 状态集合，无状态迁移。", 0.78, "pending", ["evidence:shelf-cargo", "usecase:slice:shelf-cargo"]),
            ],
        };
    }

    private static BusinessOntologyGenerationInput GenerationWithPollutedRelations()
    {
        var generation = Generation();
        return generation with
        {
            GenerationId = "quality-polluted-relations-fixture",
            SourceFingerprint = "fingerprint-quality-polluted-relations",
            Attributes = [generation.Attributes[0]],
            Evidence = generation.Evidence.Concat(
            [
                Evidence("usecase:slice:query-record", "src/main/java/demo/RecordInfoController.java", "symbol:record-info#query", "RecordInfo queryByRecordNumbers 用例切片。", "usecase_slice", "inferred"),
            ]).ToArray(),
            Candidates =
            [
                new BusinessOntologyCandidate("candidate:semantic:relation:signature", "relation", OntologyId + ".Relation.RecordInfoQueryByRecordNumbers", RelationPayload("queryByRecordNumbers", Record, Supplier), "签名污染关系。", 0.7, "pending", ["evidence:record-service", "usecase:slice:query-record"]),
                new BusinessOntologyCandidate("candidate:semantic:relation:self", "relation", OntologyId + ".Relation.RecordFindNewlyByRecordNumber", RelationPayload("findNewlyByRecordNumber", Record, Record), "自关系污染。", 0.7, "pending", ["evidence:record-service", "usecase:slice:query-record"]),
                new BusinessOntologyCandidate("candidate:semantic:relation:operation", "relation", OntologyId + ".Relation.RecordCategoryGetCategoryPath", RelationPayload("getCategoryPath", Record, Supplier), "operation-like 污染关系。", 0.7, "pending", ["evidence:record-service", "usecase:slice:query-record"]),
            ],
        };
    }

    private static BusinessOntologyGenerationInput GenerationWithDirectOnlyRelationAndMappingKeywordEvidence()
    {
        var generation = Generation();
        return generation with
        {
            GenerationId = "quality-direct-only-mapping-fixture",
            SourceFingerprint = "fingerprint-quality-direct-only-mapping",
            Attributes = [generation.Attributes[0]],
            Evidence = generation.Evidence.Concat(
            [
                Evidence("evidence:repair-request-mapping", "src/main/java/demo/RecordRepairRequestDTO.java", "symbol:repair-request-dto", "维修请求 DTO 到记录概念的载体 mapping。", "code", "inferred"),
                Evidence("evidence:generic-guard", "src/main/java/demo/GenericActionService.java", "symbol:generic-action#execute", "直接源码语义事实：business_guard。", "code-semantic-claim", "contractual"),
                Evidence("usecase:slice:generic-action", "src/main/java/demo/GenericActionController.java", "symbol:generic-action#execute", "POST /api/actions/execute 跨层业务用例切片。", "use-case-slice", "contractual"),
            ]).ToArray(),
            Candidates =
            [
                new BusinessOntologyCandidate(
                    "candidate:semantic:relation:direct-only",
                    "relation",
                    OntologyId + ".Relation.RecordSupplierDirect",
                    RelationPayload(),
                    "direct-only diagnostic: typed_reference 直接事实存在，但没有匹配 use-case slice。",
                    0.6,
                    "pending",
                    ["evidence:record-entity", "evidence:supplier"]),
                new BusinessOntologyCandidate(
                    "candidate:semantic:rule:generic",
                    "rule",
                    OntologyId + ".Rule.GenericAction",
                    GenericBusinessRulePayload(),
                    "direct business_guard 与匹配的 POST /api/actions/execute use-case slice 共同支持。",
                    0.84,
                    "pending",
                    ["evidence:repair-request-mapping", "evidence:generic-guard", "usecase:slice:generic-action"]),
            ],
        };
    }

    private static BusinessOntologyGenerationInput GenerationWithMixedSemanticEvidence()
    {
        var generation = Generation();
        return generation with
        {
            GenerationId = "quality-mixed-semantic-evidence-fixture",
            SourceFingerprint = "fingerprint-quality-mixed-semantic-evidence",
            Attributes = [generation.Attributes[0]],
            Evidence = generation.Evidence.Concat(
            [
                Evidence("evidence:record-mapping", "src/main/java/demo/RecordDto.java", "symbol:record-dto", "维修记录 DTO mapping enrichment。", "mapping", "inferred"),
                Evidence("evidence:relation-typed-field", "src/main/java/demo/RecordEntity.java", "symbol:record:supplier", "直接源码语义事实：typed_reference memberKind=field。", "code-semantic-claim", "contractual"),
                Evidence("evidence:rule-business-guard", "src/main/java/demo/RecordRepairService.java", "symbol:repair#save", "直接源码语义事实：business_guard。", "code-semantic-claim", "contractual"),
                Evidence("evidence:lifecycle-state-assignment", "src/main/java/demo/RecordRepairService.java", "symbol:repair#save", "直接源码语义事实：state_assignment。", "code-semantic-claim", "contractual"),
                Evidence("evidence:lifecycle-state-field", "src/main/java/demo/RecordEntity.java", "symbol:record:status", "直接源码语义事实：state_field。", "code-semantic-claim", "contractual"),
                Evidence("evidence:lifecycle-state-value", "src/main/java/demo/RecordStatus.java", "symbol:record-status:approved", "直接源码语义事实：state_value。", "code-semantic-claim", "contractual"),
                Evidence("usecase:slice:mixed", "src/main/java/demo/RecordRepairController.java", "symbol:repair#save", "POST /api/repair/save 跨层业务用例切片。", "use-case-slice", "contractual"),
            ]).ToArray(),
            Candidates =
            [
                new BusinessOntologyCandidate(
                    "candidate:semantic:relation:mixed",
                    "relation",
                    OntologyId + ".Relation.RecordSupplierMixed",
                    RelationPayload(),
                    "typed_reference 与 repair use-case enrichment 共同支持。",
                    0.82,
                    "pending",
                    [
                        "evidence:record-mapping",
                        "evidence:relation-typed-field",
                        "evidence:rule-business-guard",
                        "evidence:lifecycle-state-assignment",
                        "usecase:slice:mixed",
                    ]),
                new BusinessOntologyCandidate(
                    "candidate:semantic:rule:mixed",
                    "rule",
                    OntologyId + ".Rule.RecordRepairMixed",
                    GenericBusinessRulePayload(),
                    "business_guard 与 repair use-case enrichment 共同支持。",
                    0.84,
                    "pending",
                    [
                        "evidence:record-mapping",
                        "evidence:relation-typed-field",
                        "evidence:rule-business-guard",
                        "evidence:lifecycle-state-assignment",
                        "usecase:slice:mixed",
                    ]),
                new BusinessOntologyCandidate(
                    "candidate:semantic:lifecycle:mixed",
                    "lifecycle",
                    OntologyId + ".Lifecycle.RecordStatusMixed",
                    LifecyclePayload(),
                    "state evidence 与 repair use-case enrichment 共同支持。",
                    0.78,
                    "pending",
                    [
                        "evidence:record-mapping",
                        "evidence:rule-business-guard",
                        "evidence:lifecycle-state-assignment",
                        "evidence:lifecycle-state-field",
                        "evidence:lifecycle-state-value",
                        "usecase:slice:mixed",
                    ]),
            ],
        };
    }

    private static BusinessOntologyGenerationInput GenerationWithoutDirectSemanticAnchors()
    {
        var generation = Generation();
        return generation with
        {
            GenerationId = "quality-no-direct-semantic-anchor-fixture",
            SourceFingerprint = "fingerprint-quality-no-direct-semantic-anchor",
            Attributes = [generation.Attributes[0]],
            Evidence = generation.Evidence.Concat(
            [
                Evidence("evidence:record-mapping", "src/main/java/demo/RecordDto.java", "symbol:record-dto", "维修记录 DTO mapping enrichment。", "mapping", "inferred"),
                Evidence("usecase:slice:mixed", "src/main/java/demo/RecordRepairController.java", "symbol:repair#save", "POST /api/repair/save 跨层业务用例切片。", "use-case-slice", "contractual"),
            ]).ToArray(),
            Candidates =
            [
                new BusinessOntologyCandidate(
                    "candidate:semantic:relation:no-direct",
                    "relation",
                    OntologyId + ".Relation.RecordSupplierNoDirect",
                    RelationPayload(),
                    "缺少直接 typed_reference 的 relation 候选夹具。",
                    0.82,
                    "pending",
                    ["evidence:record-mapping", "usecase:slice:mixed"]),
                new BusinessOntologyCandidate(
                    "candidate:semantic:rule:no-direct",
                    "rule",
                    OntologyId + ".Rule.RecordRepairNoDirect",
                    GenericBusinessRulePayload(),
                    "缺少直接 business_guard 的 rule 候选夹具。",
                    0.84,
                    "pending",
                    ["evidence:record-mapping", "usecase:slice:mixed"]),
                new BusinessOntologyCandidate(
                    "candidate:semantic:lifecycle:no-direct",
                    "lifecycle",
                    OntologyId + ".Lifecycle.RecordStatusNoDirect",
                    LifecyclePayload(),
                    "缺少直接 state evidence 的 lifecycle 候选夹具。",
                    0.78,
                    "pending",
                    ["evidence:record-mapping", "usecase:slice:mixed"]),
            ],
        };
    }

    private static BusinessOntologyEvidence Evidence(
        string id,
        string path,
        string symbol,
        string summary,
        string sourceKind,
        string grade) =>
        new(id, "repo:record", path, symbol, 10, 20, grade, "fixture", 0.9, sourceKind, summary);

    private static BusinessOntologyCandidate ImplementationCandidate(
        string symbol,
        string sourceName,
        string role,
        IReadOnlyList<string> evidenceIds) =>
        new(
            "candidate:implementation:" + symbol + ":" + role,
            "implementation",
            OntologyId + ".Implementation." + sourceName.Replace("Dto", "", StringComparison.Ordinal).Replace("Entity", "", StringComparison.Ordinal).Replace("Response", "", StringComparison.Ordinal),
            JsonSerializer.Serialize(new { sourceSymbol = symbol, sourceName, role, canonicalFamily = "Record" }),
            "实现载体夹具。",
            0.8,
            "pending",
            evidenceIds);

    private static string RelationPayload() => JsonSerializer.Serialize(new
    {
        schemaVersion = "onto-semantic-v1",
        kind = "relation",
        semantic = new
        {
            id = OntologyId + ".Relation.RecordSupplier",
            fromConceptId = Record,
            toConceptId = Supplier,
            name = "supplier",
            min = "0",
            max = "1",
            descriptionZh = "记录关联的供应商。",
        },
        evidenceIds = new[] { "evidence:record-entity", "evidence:supplier" },
        basis = "deterministic",
        rationale = "typed reference",
    });

    private static string RelationPayload(string name, string fromConceptId, string toConceptId) => JsonSerializer.Serialize(new
    {
        schemaVersion = "onto-semantic-v1",
        kind = "relation",
        semantic = new
        {
            id = fromConceptId + "." + name,
            fromConceptId,
            toConceptId,
            name,
            min = "0",
            max = "1",
            descriptionZh = "污染关系夹具。",
        },
        evidenceIds = new[] { "evidence:record-service", "usecase:slice:query-record" },
        basis = "deterministic",
        rationale = "typed reference",
    });

    private static string BusinessRulePayload() => JsonSerializer.Serialize(new
    {
        schemaVersion = "onto-semantic-v1",
        kind = "rule",
        semantic = new
        {
            id = OntologyId + ".Rule.CreateRecordDraftOnly",
            subjectConceptId = Record,
            ruleKind = "businessCondition",
            descriptionZh = "只有草稿记录可以提交。",
            predicate = new { source = "record.status != DRAFT" },
            effect = new { type = "reject", mechanism = "throw", messageZh = "只有草稿记录可以提交" },
        },
        evidenceIds = new[] { "evidence:record-service", "usecase:slice:create-record" },
        basis = "deterministic",
        rationale = "guard + slice",
    });

    private static string GenericBusinessRulePayload() => JsonSerializer.Serialize(new
    {
        schemaVersion = "onto-semantic-v1",
        kind = "rule",
        semantic = new
        {
            id = OntologyId + ".Rule.GenericAction",
            subjectConceptId = Record,
            ruleKind = "businessCondition",
            descriptionZh = "输入无效时拒绝执行通用动作。",
            predicate = new { source = "request.invalid()" },
            effect = new { type = "reject", mechanism = "throw", messageZh = "输入无效" },
        },
        evidenceIds = new[] { "evidence:repair-request-mapping", "evidence:generic-guard", "usecase:slice:generic-action" },
        basis = "deterministic",
        rationale = "guard + generic action slice",
    });

    private static string LifecyclePayload() => JsonSerializer.Serialize(new
    {
        schemaVersion = "onto-semantic-v1",
        kind = "lifecycle",
        semantic = new
        {
            id = OntologyId + ".Lifecycle.RecordStatus",
            subjectConceptId = Record,
            stateProperty = "status",
            initialState = "DRAFT",
            descriptionZh = "记录状态生命周期。",
            states = new[]
            {
                new { id = "DRAFT", terminal = false, descriptionZh = "草稿" },
                new { id = "SUBMITTED", terminal = false, descriptionZh = "已提交" },
            },
            transitions = new[]
            {
                new
                {
                    id = OntologyId + ".Transition.SubmitRecord",
                    action = "submit",
                    fromState = "DRAFT",
                    toState = "SUBMITTED",
                    descriptionZh = "提交记录。",
                    guard = new { source = "record.status == DRAFT" },
                    effect = new { set = new { property = "status", value = "SUBMITTED" } },
                },
            },
        },
        evidenceIds = new[] { "evidence:record-service" },
        basis = "deterministic",
        rationale = "guard + assignment",
    });

    private static string UploadRulePayload() => JsonSerializer.Serialize(new
    {
        schemaVersion = "onto-semantic-v1",
        kind = "rule",
        semantic = new
        {
            id = OntologyId + ".Rule.PicUpload",
            subjectConceptId = Record,
            ruleKind = "businessCondition",
            descriptionZh = "文件上传大小不合法时拒绝。",
            predicate = new { source = "file.size <= 0" },
            effect = new { type = "reject", mechanism = "throw", messageZh = "文件上传失败" },
        },
        evidenceIds = new[] { "evidence:file-upload", "usecase:slice:file-upload" },
        basis = "deterministic",
        rationale = "upload guard + slice",
    });

    private static string ZeroTransitionLifecyclePayload() => JsonSerializer.Serialize(new
    {
        schemaVersion = "onto-semantic-v1",
        kind = "lifecycle",
        semantic = new
        {
            id = OntologyId + ".Lifecycle.ShelfCargoState",
            subjectConceptId = Record,
            stateProperty = "cargoState",
            initialState = "EMPTY",
            descriptionZh = "货架 cargo 状态集合。",
            states = new[]
            {
                new { id = "EMPTY", terminal = false, descriptionZh = "空" },
                new { id = "FULL", terminal = false, descriptionZh = "满" },
            },
            transitions = Array.Empty<object>(),
        },
        evidenceIds = new[] { "evidence:shelf-cargo", "usecase:slice:shelf-cargo" },
        basis = "deterministic",
        rationale = "state values only",
    });
}
