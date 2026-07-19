using System.Text;
using System.Text.Json;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class OntologySemanticCandidateContractTests
{
    private const string Record = "SampleDomain.Ontology.Record";
    private const string Supplier = "SampleDomain.Ontology.Supplier";

    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "onto-evidence-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await EvidencePackBoundsAsync(root, assert);
            CandidateContract(assert);
            CandidateDraftXmlMappingContract(assert);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task EvidencePackBoundsAsync(string root, Action<bool, string> assert)
    {
        var anchors = new List<SemanticEvidenceAnchorSource>();
        for (var index = 0; index < 30; index++)
        {
            var relativePath = $"src/Record{index:D2}.java";
            var absolutePath = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllLinesAsync(
                absolutePath,
                Enumerable.Range(1, 50).Select(line => $"line {line:D2} record {index:D2}"));
            anchors.Add(new SemanticEvidenceAnchorSource(
                $"evidence:{index:D2}",
                "backend",
                root,
                relativePath,
                $"symbol:{index:D2}",
                CodeSemanticClaimKinds.TypedReference,
                """{"resolvedType":"SupplierEntity","member":"supplier"}""",
                20,
                20,
                [Supplier, Record]));
        }

        var builder = new SemanticEvidencePackBuilder();
        var pack = await builder.BuildEvidencePackAsync(anchors);
        assert(pack.Anchors.Count == SemanticEvidencePackBuilder.MaxAnchors && pack.OmittedAnchors == 6,
            "evidence packs should deterministically cap clusters at 24 anchors");
        assert(pack.Anchors.All(anchor =>
                !Path.IsPathRooted(anchor.Path) &&
                !anchor.Path.Contains('\\') &&
                anchor.ExcerptStartLine == 8 &&
                anchor.ExcerptEndLine == 32),
            "evidence anchors should expose repository-relative POSIX paths and exactly 12 surrounding lines");
        assert(pack.Anchors.All(anchor =>
                anchor.ExistingOntologyIds.SequenceEqual([Record, Supplier]) &&
                anchor.ClaimPayloadJson == """{"member":"supplier","resolvedType":"SupplierEntity"}"""),
            "evidence packs should sort ontology IDs and canonicalize semantic claim payloads");
        assert(pack.SourceUtf8Bytes <= SemanticEvidencePackBuilder.MaxSourceUtf8Bytes &&
               Encoding.UTF8.GetByteCount(string.Concat(pack.Anchors.Select(anchor => anchor.SourceExcerpt + anchor.CorroborationExcerpt))) ==
               pack.SourceUtf8Bytes,
            "evidence source text should be measured and bounded by UTF-8 bytes");
        assert(!pack.ToCanonicalJson().Contains(root, StringComparison.Ordinal),
            "serialized evidence packs must never expose absolute repository roots");

        var hugePath = Path.Combine(root, "src/Huge.java");
        await File.WriteAllTextAsync(hugePath, new string('界', 20_000));
        var huge = await builder.BuildEvidencePackAsync(
        [
            new SemanticEvidenceAnchorSource(
                "evidence:huge",
                "backend",
                root,
                "src/Huge.java",
                "symbol:huge",
                CodeSemanticClaimKinds.ValidationConstraint,
                """{"annotation":"NotNull","member":"supplier"}""",
                1,
                1,
                [],
                new string('证', 10_000)),
        ]);
        assert(huge.SourceUtf8Bytes <= SemanticEvidencePackBuilder.MaxSourceUtf8Bytes &&
               huge.SourceUtf8Bytes > SemanticEvidencePackBuilder.MaxSourceUtf8Bytes - 4 &&
               huge.Anchors.Single().TextTruncated &&
               Encoding.UTF8.GetByteCount(huge.Anchors.Single().SourceExcerpt) <= SemanticEvidencePackBuilder.MaxSourceUtf8Bytes,
            "UTF-8 truncation should never split the configured 20 KiB source budget");

        var absoluteRejected = Rejects(() => builder.BuildEvidencePackAsync(
        [
            anchors[0] with { Path = Path.Combine(root, "src/Record00.java") },
        ]).GetAwaiter().GetResult(), "repository-relative");
        assert(absoluteRejected, "absolute source paths should be rejected before evidence-pack construction");
    }

    private static void CandidateContract(Action<bool, string> assert)
    {
        var validator = new OntologySemanticCandidateValidator();
        var concepts = new[] { Record, Supplier };
        var evidence = new[] { "evidence:typed", "evidence:required", "evidence:states", "evidence:assignment" };

        var relation = """
        {
          "rationale": "字段类型与非空约束共同支持供应商关系。",
          "basis": "deterministic",
          "evidenceIds": ["evidence:required", "evidence:typed"],
          "semantic": {
            "toConceptId": "__SUPPLIER__",
            "name": "supplier",
            "max": "1",
            "min": "1",
            "id": "__RECORD__.supplier",
            "fromConceptId": "__RECORD__",
            "descriptionZh": "记录关联的供应商。"
          },
          "kind": "relation",
          "schemaVersion": "onto-semantic-v1"
        }
        """
        .Replace("__RECORD__", Record, StringComparison.Ordinal)
        .Replace("__SUPPLIER__", Supplier, StringComparison.Ordinal);
        var validatedRelation = validator.Validate(relation, concepts, evidence);
        var reorderedRelation = relation
            .Replace("""["evidence:required", "evidence:typed"]""", """["evidence:typed", "evidence:required"]""", StringComparison.Ordinal);
        var repeated = validator.Validate(reorderedRelation, concepts, evidence);
        var rerationalized = validator.Validate(
            relation.Replace("字段类型与非空约束共同支持供应商关系。", "同一直接证据的另一种简短说明。", StringComparison.Ordinal),
            concepts,
            evidence);
        assert(validatedRelation.Id == repeated.Id &&
               validatedRelation.Id == rerationalized.Id &&
               validatedRelation.Id.StartsWith("candidate:semantic:", StringComparison.Ordinal) &&
               validatedRelation.EvidenceIds.SequenceEqual(["evidence:required", "evidence:typed"]),
            "candidate identity should depend on canonical semantic JSON and sorted evidence identities, never rationale wording");
        using (var canonical = JsonDocument.Parse(validatedRelation.CanonicalPayloadJson))
        {
            assert(canonical.RootElement.GetProperty("schemaVersion").GetString() == OntologySemanticCandidateValidator.SchemaVersion &&
                   canonical.RootElement.GetProperty("semantic").GetProperty("fromConceptId").GetString() == Record,
                "validated candidates should expose a complete canonical onto-semantic-v1 payload");
        }

        var rule = """
        {
          "schemaVersion":"onto-semantic-v1",
          "kind":"rule",
          "semantic":{
            "id":"__RECORD__.recordCodeRequired",
            "subjectConceptId":"__RECORD__",
            "ruleKind":"required",
            "descriptionZh":"记录编码不能为空。",
            "predicate":{"property":"recordCode","operator":"present"},
            "effect":{"type":"reject","messageZh":"记录编码不能为空。"}
          },
          "evidenceIds":["evidence:required"],
          "basis":"deterministic",
          "rationale":"直接验证注解。"
        }
        """.Replace("__RECORD__", Record, StringComparison.Ordinal);
        assert(validator.Validate(rule, concepts, evidence).Kind == OntologySemanticCandidateKinds.Rule,
            "declarative rule predicate/effect objects should validate");

        var businessConditionRule = """
        {
          "schemaVersion":"onto-semantic-v1",
          "kind":"rule",
          "semantic":{
            "id":"__RECORD__.createRecordBusinessCondition",
            "subjectConceptId":"__RECORD__",
            "ruleKind":"businessCondition",
            "descriptionZh":"只有草稿记录可以提交。",
            "predicate":{"source":"request.status() != RecordStatus.DRAFT","method":"createRecord","methodSymbolId":"symbol:service:create"},
            "effect":{"type":"reject","mechanism":"throw","messageZh":"只有草稿记录可以提交","allowedWhen":{"not":"request.status() != RecordStatus.DRAFT"}}
          },
          "evidenceIds":["evidence:typed","evidence:required"],
          "basis":"deterministic",
          "rationale":"guard 与跨文件用例切片共同支持。"
        }
        """.Replace("__RECORD__", Record, StringComparison.Ordinal);
        assert(validator.Validate(businessConditionRule, concepts, evidence).Kind == OntologySemanticCandidateKinds.Rule,
            "conditional business rules should validate with businessCondition ruleKind, predicate, and allow/reject effect");

        var lifecycle = """
        {
          "schemaVersion":"onto-semantic-v1",
          "kind":"lifecycle",
          "semantic":{
            "id":"__RECORD__.recordLifecycle",
            "subjectConceptId":"__RECORD__",
            "stateProperty":"status",
            "initialState":"DRAFT",
            "descriptionZh":"记录审批生命周期。",
            "states":[
              {"id":"DRAFT","terminal":false,"descriptionZh":"草稿"},
              {"id":"APPROVED","terminal":true,"descriptionZh":"已批准"}
            ],
            "transitions":[
              {
                "id":"__RECORD__.approve",
                "action":"approve",
                "fromState":"DRAFT",
                "toState":"APPROVED",
                "descriptionZh":"批准记录。",
                "guard":{},
                "effect":{"set":{"property":"status","value":"APPROVED"}}
              }
            ]
          },
          "evidenceIds":["evidence:states","evidence:assignment"],
          "basis":"deterministic",
          "rationale":"枚举状态与直接赋值共同支持。"
        }
        """.Replace("__RECORD__", Record, StringComparison.Ordinal);
        assert(validator.Validate(lifecycle, concepts, evidence).Kind == OntologySemanticCandidateKinds.Lifecycle,
            "internally consistent lifecycle states and transitions should validate");

        assert(Rejects(
                () => validator.Validate(relation.Replace(""" "kind": "relation",""", """ "kind": "process",""", StringComparison.Ordinal), concepts, evidence),
                "$.kind"),
            "candidate kind should be a closed vocabulary");
        assert(Rejects(
                () => validator.Validate(relation.Replace(""" "basis": "deterministic",""", """ "unexpected": true, "basis": "deterministic",""", StringComparison.Ordinal), concepts, evidence),
                "not an allowed field"),
            "unknown candidate fields should be rejected");
        assert(Rejects(
                () => validator.Validate(relation.Replace(""" "kind": "relation",""", """ "kind": "relation", "kind": "relation",""", StringComparison.Ordinal), concepts, evidence),
                "duplicated"),
            "duplicate candidate fields should be rejected");
        assert(Rejects(
                () => validator.Validate(rule.Replace(
                    "\"predicate\":{\"property\":\"recordCode\",\"operator\":\"present\"}",
                    "\"predicate\":{\"property\":\"recordCode\",\"property\":\"other\",\"operator\":\"present\"}",
                    StringComparison.Ordinal), concepts, evidence),
                "duplicated"),
            "duplicate fields inside declarative rule objects should be rejected");
        assert(Rejects(
                () => validator.Validate(relation.Replace(Supplier, "SampleDomain.Ontology.Unknown", StringComparison.Ordinal), concepts, evidence),
                "unknown concept"),
            "semantic candidates should only reference concepts in the current ontology generation");
        assert(Rejects(
                () => validator.Validate(relation.Replace("evidence:typed", "evidence:missing", StringComparison.Ordinal), concepts, evidence),
                "unavailable evidence"),
            "semantic candidates should only reference evidence supplied by the current pack");
        assert(Rejects(
                () => validator.Validate(relation.Replace(@"""max"": ""1""", @"""max"": ""*""", StringComparison.Ordinal), concepts, evidence),
                "cardinality"),
            "candidate cardinality should use the semantic contract's 0|1 and 1|many vocabulary");
        assert(Rejects(
                () => validator.Validate(lifecycle.Replace(@"""toState"":""APPROVED""", @"""toState"":""MISSING""", StringComparison.Ordinal), concepts, evidence),
                "endpoints"),
            "lifecycle transitions should only reference states declared in the same candidate");
        assert(Rejects(
                () => validator.Validate(lifecycle.Replace(@"""initialState"":""DRAFT""", @"""initialState"":""MISSING""", StringComparison.Ordinal), concepts, evidence),
                "initialState"),
            "lifecycle initial state should exist in its finite state set");
    }

    private static void CandidateDraftXmlMappingContract(Action<bool, string> assert)
    {
        var validator = new OntologyCandidateDraftXmlMappingValidator();
        var concepts = new[] { Record, Supplier };
        var evidence = new[]
        {
            new OntologyCandidateDraftEvidence("evidence:type", "inferred"),
            new OntologyCandidateDraftEvidence("evidence:property", "inferred"),
            new OntologyCandidateDraftEvidence("evidence:relation", "inferred"),
            new OntologyCandidateDraftEvidence("evidence:rule", "inferred"),
            new OntologyCandidateDraftEvidence("evidence:lifecycle", "inferred"),
            new OntologyCandidateDraftEvidence("evidence:contractual", "contractual"),
        };

        var type = validator.Evaluate(
            Draft("draft:type", "concept", Record, "type", """{"id":"SampleDomain.Ontology.Invoice","descriptionZh":"发票业务概念。"}""", ["evidence:type"]),
            concepts,
            evidence);
        var property = validator.Evaluate(
            Draft("draft:property", "attribute", Record + ".recordCode", "property", """{"ownerConceptId":"SampleDomain.Ontology.Record","name":"recordCode","valueType":"String","required":true,"descriptionZh":"记录编码。"}""", ["evidence:property"]),
            concepts,
            evidence);
        var relation = validator.Evaluate(
            Draft("draft:relation", "relation", Record + ".supplier", "relation", """{"id":"SampleDomain.Ontology.Relation.Supplier","name":"supplier","fromConceptId":"SampleDomain.Ontology.Record","toConceptId":"SampleDomain.Ontology.Supplier","directed":true,"min":"0","max":"1","descriptionZh":"记录关联供应商。"}""", ["evidence:relation"]),
            concepts,
            evidence);
        var rule = validator.Evaluate(
            Draft("draft:rule", "rule", Record + ".Rule.Required", "rule", """{"id":"SampleDomain.Ontology.Rule.RecordCodeRequired","scope":"SampleDomain.Ontology.Record","kind":"Conditional","statementZh":"记录编码不能为空。","require":{"PropertyPresent":{"property":"recordCode"}},"violationCode":"RECORD_CODE_REQUIRED","violationMessageZh":"记录编码不能为空。"}""", ["evidence:rule"]),
            concepts,
            evidence);
        var lifecycle = validator.Evaluate(
            Draft("draft:lifecycle", "lifecycle", Record + ".Lifecycle", "lifecycle", """{"id":"SampleDomain.Ontology.Lifecycle.Record","subject":"SampleDomain.Ontology.Record","stateProperty":"workflowState","initial":"draft","descriptionZh":"记录生命周期。","states":[{"id":"draft","terminal":false,"descriptionZh":"草稿"},{"id":"submitted","terminal":true,"descriptionZh":"已提交"}],"transitions":[{"id":"SampleDomain.Ontology.Transition.SubmitRecord","action":"submit","from":"draft","to":"submitted","descriptionZh":"提交记录。"}]}""", ["evidence:lifecycle"]),
            concepts,
            evidence);

        assert(new[] { type, property, relation, rule, lifecycle }.All(decision => decision.Mappable && decision.XmlStatus == "hypothesis"),
            "candidate_draft records with closed inferred evidence and DSL-shaped semantics should map only to hypothesis XML kinds");
        assert(type.XmlKind == OntologyCandidateDraftXmlKinds.Type
                && property.XmlKind == OntologyCandidateDraftXmlKinds.Property
                && relation.XmlKind == OntologyCandidateDraftXmlKinds.Relation
                && rule.XmlKind == OntologyCandidateDraftXmlKinds.Rule
                && lifecycle.XmlKind == OntologyCandidateDraftXmlKinds.Lifecycle,
            "draft mapping should preserve the strict type/property/relation/rule/lifecycle XML kind contract");

        var acceptedRequested = validator.Evaluate(
            Draft("draft:accepted", "concept", Record, "type", """{"id":"SampleDomain.Ontology.AcceptedByMistake","status":"accepted","descriptionZh":"不应直接 accepted。"}""", ["evidence:type"]),
            concepts,
            evidence);
        assert(acceptedRequested.Mappable
                && acceptedRequested.XmlStatus == "hypothesis"
                && acceptedRequested.DowngradeReasons.SequenceEqual([OntologyCandidateDraftDowngradeReasons.RequestedAcceptedStatus]),
            "candidate drafts that request accepted XML should be downgraded to hypothesis with a stable reason");

        foreach (var (kind, status) in new[] { ("observation", "observed"), ("hypothesis", "open"), ("conflict", "open"), ("gap", "open") })
        {
            var nonDraft = validator.Evaluate(
                Draft(kind + ":looks-like-draft", "concept", Record, "type", """{"id":"SampleDomain.Ontology.Observation","descriptionZh":"非 draft 不能伪装成 XML。"}""", ["evidence:type"]) with { Kind = kind, Status = status },
                concepts,
                evidence);
            assert(!nonDraft.Mappable
                    && nonDraft.RejectReasons.SequenceEqual([OntologyCandidateDraftRejectReasons.NotCandidateDraftRecord]),
                "observation/hypothesis/conflict/gap records must not be converted to XML even when the body is shaped like a draft");
        }

        var missingInferred = validator.Evaluate(
            Draft("draft:no-inferred", "concept", Record, "type", """{"id":"SampleDomain.Ontology.NoInferred","descriptionZh":"缺少 inferred evidence。"}""", ["evidence:contractual"]),
            concepts,
            evidence);
        assert(!missingInferred.Mappable
                && missingInferred.RejectReasons.Contains(OntologyCandidateDraftRejectReasons.MissingInferredEvidence, StringComparer.Ordinal),
            "generated hypothesis candidates require at least one direct inferred evidence ref");

        var missingEvidence = validator.Evaluate(
            Draft("draft:missing-evidence", "concept", Record, "type", """{"id":"SampleDomain.Ontology.MissingEvidence","descriptionZh":"未知证据。"}""", ["evidence:missing"]),
            concepts,
            evidence);
        assert(!missingEvidence.Mappable
                && missingEvidence.RejectReasons.Contains(OntologyCandidateDraftRejectReasons.UnavailableEvidence, StringComparer.Ordinal),
            "candidate_draft mapping must be evidence-closed over the supplied evidence set");

        var unsupported = validator.Evaluate(
            Draft("draft:unsupported", "unknown", Record, "observation", """{"id":"SampleDomain.Ontology.Unknown","descriptionZh":"无 XML kind。"}""", ["evidence:type"]),
            concepts,
            evidence);
        assert(!unsupported.Mappable
                && unsupported.RejectReasons.Contains(OntologyCandidateDraftRejectReasons.UnsupportedXmlKind, StringComparer.Ordinal),
            "unmapped analysis shapes should become diagnosis reasons instead of XML facts");
    }

    private static bool Rejects(Action action, string messageFragment)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException ex)
        {
            return ex.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static BusinessOntologyAnalysisRecord Draft(
        string recordId,
        string subjectKind,
        string subjectId,
        string xmlKind,
        string semanticJson,
        IReadOnlyList<string> evidenceIds) =>
        new(
            "analysis-run:contract",
            recordId,
            "candidate_draft",
            subjectKind,
            subjectId,
            "candidate draft",
            $$"""{"schemaVersion":"candidate-draft-to-ontology-xml-v1","xmlKind":"{{xmlKind}}","semantic":{{semanticJson}}}""",
            "proposed",
            0.25,
            "sha256:query",
            "2026-07-19T00:00:00Z",
            evidenceIds);
}
