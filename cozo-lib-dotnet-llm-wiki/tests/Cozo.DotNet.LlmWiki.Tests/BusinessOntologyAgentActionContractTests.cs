using System.Text.Json;
using System.Text.Json.Nodes;
using Cozo.DotNet.LlmWiki.Tools;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyAgentActionContractTests
{
    public static Task RunAsync(Action<bool, string> assert)
    {
        var context = new BusinessOntologyAgentActionValidationContext(
            "analysis-run:submit-record",
            new HashSet<string>(["claim:submit-state", "evidence:record"], StringComparer.Ordinal),
            new HashSet<string>(["sha256:query-state"], StringComparer.Ordinal),
            EvidenceIdsByQueryDigest: new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["sha256:query-state"] = new HashSet<string>(["claim:submit-state", "evidence:record"], StringComparer.Ordinal),
            },
            EvidenceRefsById: new Dictionary<string, BusinessOntologyInvestigationEvidenceRef>(StringComparer.Ordinal)
            {
                ["claim:submit-state"] = Evidence("claim:submit-state", "symbol:record:controller", "src/RecordController.java"),
                ["evidence:record"] = Evidence("evidence:record", "symbol:record:service", "src/RecordService.java"),
            });
        var validator = new BusinessOntologyAgentActionValidator();

        var query = validator.Validate(
            """
            {
              "schemaVersion": "business-ontology-agent-action-v1",
              "action": "query",
              "identity": {"runId": "analysis-run:submit-record", "turnId": "turn:1"},
              "operation": "find_semantic_patterns",
              "parameters": {"kind": "state_assignment", "term": "record", "limit": 5},
              "reason": "verify the record lifecycle transition"
            }
            """,
            context);
        assert(query is ValidatedBusinessOntologyQueryAction { Operation: "find_semantic_patterns" },
            "query action should validate one fixed investigation operation and canonical parameters");

        var domainDiscovery = validator.Validate(
            QueryFor("discover_domain_charters", "{\"term\":\"record\",\"limit\":1}"),
            context);
        assert(domainDiscovery is ValidatedBusinessOntologyQueryAction { Operation: "discover_domain_charters" },
            "domain charter discovery should join the fixed query-operation whitelist");

        var record = validator.Validate(
            """
            {
              "schemaVersion": "business-ontology-agent-action-v1",
              "action": "record",
              "identity": {"runId": "analysis-run:submit-record", "turnId": "turn:2"},
              "records": [
                {
                  "recordId": "observation:submit-state",
                  "kind": "observation",
                  "subjectKind": "lifecycle",
                  "subjectId": "SampleDomain.Ontology.RecordLifecycle",
                  "title": "Submit changes record state",
                  "body": {"from": "DRAFT", "to": "SUBMITTED"},
                  "status": "observed",
                  "uncertainty": 0.05,
                  "queryDigest": "sha256:query-state",
                  "evidenceIds": ["claim:submit-state", "evidence:record"]
                }
              ]
            }
            """,
            context);
        assert(record is ValidatedBusinessOntologyRecordAction { Records.Count: 1 } recordAction
                && recordAction.Records.Single().BodyJson == "{\"from\":\"DRAFT\",\"to\":\"SUBMITTED\"}",
            "record action should validate analysis record shape and canonicalize body JSON");

        var finish = validator.Validate(
            """
            {
              "schemaVersion": "business-ontology-agent-action-v1",
              "action": "finish",
              "identity": {"runId": "analysis-run:submit-record", "turnId": "turn:3"},
              "status": "no_candidate",
              "reason": "Evidence is insufficient for a candidate draft.",
              "unresolved": ["No direct rule evidence was found."]
            }
            """,
            context);
        assert(finish is ValidatedBusinessOntologyFinishAction { Status: "no_candidate", Unresolved.Count: 1 },
            "finish action should validate local terminal status and unresolved reasons");

        var synthesize = validator.Validate(Synthesize(), context);
        assert(synthesize is ValidatedBusinessOntologySynthesizeAction { DomainCharters.Count: 1, Clusters.Count: 1 } synthesis
                && synthesis.DomainCharters.Single().NameZh == "记录管理"
                && synthesis.Clusters.Single().ImplementationAnchors.Select(anchor => anchor.Role).Distinct(StringComparer.Ordinal).Count() == 2,
            "synthesize action should retain a pending Chinese business draft with cross-role evidence anchors");
        var inventorySynthesis = validator.Validate(SynthesizeItAssetInventory(), context);
        assert(inventorySynthesis is ValidatedBusinessOntologySynthesizeAction
            {
                DomainCharters: [{ Id: "ItAsset.Inventory" }],
                Clusters: [{ DomainId: "ItAsset.Inventory", ConceptId: "ItAsset.Asset" }],
            },
            "synthesize must accept a two-segment charter domain whose concepts use its business root");

        AssertRejects(validator, "{not json", context, assert, "invalid JSON must be rejected before any effect");
        AssertRejects(validator, QueryWith(""" ,"extra": true"""), context, assert, "unknown top-level fields must be rejected");
        AssertRejects(validator, QueryWith(""" ,"operation": "query_named" """, replaceOperation: true), context, assert, "unknown query operation must be rejected");
        AssertRejects(validator, QueryWithParameters("""{"term":"record","sql":"select * from ck_symbol"}"""), context, assert, "operation parameters must reject arbitrary SQL");
        AssertRejects(validator, QueryWithParameters("""{"term":"record","path":"/tmp/source.java"}"""), context, assert, "operation parameters must reject arbitrary source paths");
        AssertRejects(validator, QueryWithParameters("""{"term":"record","provider":"codex-cli"}"""), context, assert, "operation parameters must reject provider steering");
        AssertRejects(validator, QueryFor("discover_domain_charters", "{\"term\":\"record\",\"path\":\"/tmp/source.java\"}"), context, assert, "domain discovery must reject arbitrary source paths");
        AssertRejects(validator, QueryFor("list_cross_layer_use_cases", "{\"entrySymbolId\":\"symbol:record:create\",\"sql\":\"select * from ck_symbol\"}"), context, assert, "cross-layer discovery must reject arbitrary SQL parameters");
        AssertRejects(validator, QueryFor("find_implementation_clusters", "{\"evidenceIds\":[\"claim:submit-state\"],\"cursor\":\"/tmp/not-a-cursor\"}"), context, assert, "implementation-cluster discovery must reject unsafe cursors before dispatch");
        AssertRejects(validator, RecordWith(""" "kind": "claim" """, replaceKind: true), context, assert, "unknown analysis record kind must be rejected");
        AssertRejects(validator, RecordWith(""" "status": "accepted" """, replaceStatus: true), context, assert, "unknown analysis record status must be rejected");
        AssertRejects(validator, QueryWith(""" ,"identity": {"runId": "analysis-run:other", "turnId": "turn:1"} """, replaceIdentity: true), context, assert, "run identity mismatch must be rejected");
        AssertRejects(validator, RecordWith(""" "evidenceIds": ["not-known"] """, replaceEvidence: true), context, assert, "unknown evidence identities must be rejected locally");
        AssertRejects(validator, RecordWith(""" "queryDigest": "sha256:unseen" """, replaceDigest: true), context, assert, "unknown query digest identities must be rejected locally");
        var splitProvenanceContext = context with
        {
            AvailableQueryDigests = new HashSet<string>(["sha256:query-state", "sha256:query-other"], StringComparer.Ordinal),
            EvidenceIdsByQueryDigest = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["sha256:query-state"] = new HashSet<string>(["claim:submit-state"], StringComparer.Ordinal),
                ["sha256:query-other"] = new HashSet<string>(["evidence:record"], StringComparer.Ordinal),
            },
        };
        var directRecord = validator.Validate(RecordWithProvenance("sha256:query-other", "evidence:record"), splitProvenanceContext);
        assert(directRecord is ValidatedBusinessOntologyRecordAction { Records.Count: 1 },
            "records should accept evidence returned by their cited query digest");
        AssertRejects(validator, RecordWithProvenance("sha256:query-state", "evidence:record"), splitProvenanceContext, assert,
            "records must reject known evidence returned only by a different query digest");
        AssertRejects(
            validator,
            RecordWith(""" "queryDigest": "sha256:query-state" """, replaceDigest: true),
            context with { AvailableQueryDigests = new HashSet<string>(StringComparer.Ordinal) },
            assert,
            "empty query digest scope must not accept arbitrary provenance");
        AssertRejects(validator, Synthesize().Replace("Records.Record", "Records.RecordDto", StringComparison.Ordinal), context, assert,
            "synthesize must reject implementation-shaped concept identities");
        AssertRejects(validator, Synthesize().Replace("Records.Record", "Records.RecordHandler", StringComparison.Ordinal), context, assert,
            "synthesize must reject Handler-suffixed implementation concept identities");
        AssertRejects(validator, Synthesize().Replace("Records.Record", "Records.RecordManager", StringComparison.Ordinal), context, assert,
            "synthesize must reject Manager-suffixed implementation concept identities");
        AssertRejects(validator, Synthesize().Replace("\"id\": \"Records\"", "\"id\": \"RecordManager\"", StringComparison.Ordinal), context, assert,
            "synthesize must reject Manager-suffixed domain charter identities");
        var businessTermSynthesis = validator.Validate(
            Synthesize().Replace("Records.Record", "Records.RecordManagement", StringComparison.Ordinal),
            context);
        assert(businessTermSynthesis is ValidatedBusinessOntologySynthesizeAction,
            "technical suffix rejection must not reject longer business terms that merely contain Manager");
        AssertRejects(validator, SynthesizeWithSingleAnchor(), context, assert,
            "synthesize must reject single-anchor concepts");
        AssertRejects(validator, Synthesize().Replace("\"domainId\": \"Records\"", "\"domainId\": \"Orders\"", StringComparison.Ordinal), context, assert,
            "synthesize must reject clusters outside their declared business domain");
        AssertRejects(validator, SynthesizeWithDomainId("System.Xml.Linq"), context, assert,
            "synthesize must reject FQN-shaped charter and cluster domain identifiers even when they match");
        AssertRejects(validator, Synthesize().Replace("symbol:record:service", "symbol:record:forged", StringComparison.Ordinal), context, assert,
            "synthesize must reject anchor evidence whose symbol does not match runtime evidence");
        AssertRejects(validator, Synthesize().Replace("src/RecordService.java", "src/ForgedRecordService.java", StringComparison.Ordinal), context, assert,
            "synthesize must reject anchor evidence whose path does not match runtime evidence");
        AssertRejects(validator, Synthesize().Replace("\"clusters\": [", "\"unexpected\": true, \"clusters\": [", StringComparison.Ordinal), context, assert,
            "synthesize must reject unknown fields");
        AssertRejects(validator, Synthesize(), context with { EvidenceRefsById = null }, assert,
            "synthesize must require runtime-owned evidence references");

        var schema = BusinessOntologyAgentActionContract.SchemaJson();
        var variants = schema.RootElement.GetProperty("oneOf").EnumerateArray().ToArray();
        var operations = variants[0].GetProperty("properties").GetProperty("operation").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
        assert(variants.Length == 4
                && variants.All(variant => variant.GetProperty("additionalProperties").GetBoolean() == false)
                && variants[0].GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "query"
                && variants[1].GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "record"
                && variants[2].GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "finish"
                && variants[3].GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "synthesize"
                && new[] { "discover_domain_charters", "list_cross_layer_use_cases", "find_state_rule_clusters", "find_implementation_clusters" }.All(operations.Contains),
            "published action schema should close and discriminate query/record/finish/synthesize variants");
        return Task.CompletedTask;
    }

    private static void AssertRejects(
        BusinessOntologyAgentActionValidator validator,
        string json,
        BusinessOntologyAgentActionValidationContext context,
        Action<bool, string> assert,
        string message)
    {
        var rejected = false;
        try { validator.Validate(json, context); }
        catch (ArgumentException) { rejected = true; }
        assert(rejected, message);
    }

    private static string QueryWith(
        string replacement,
        bool replaceOperation = false,
        bool replaceIdentity = false)
    {
        var json = """
            {
              "schemaVersion": "business-ontology-agent-action-v1",
              "action": "query",
              "identity": {"runId": "analysis-run:submit-record", "turnId": "turn:1"},
              "operation": "find_business_terms",
              "parameters": {"term": "record"},
              "reason": "explore record"
            }
            """;
        if (replaceOperation) return json.Replace("\"operation\": \"find_business_terms\"", "\"operation\": \"query_named\"", StringComparison.Ordinal);
        if (replaceIdentity) return json.Replace("\"identity\": {\"runId\": \"analysis-run:submit-record\", \"turnId\": \"turn:1\"}", "\"identity\": {\"runId\": \"analysis-run:other\", \"turnId\": \"turn:1\"}", StringComparison.Ordinal);
        var end = json.LastIndexOf('}');
        return json[..end] + replacement + json[end..];
    }

    private static string QueryWithParameters(string parametersJson) =>
        """
            {
              "schemaVersion": "business-ontology-agent-action-v1",
              "action": "query",
              "identity": {"runId": "analysis-run:submit-record", "turnId": "turn:1"},
              "operation": "find_business_terms",
              "parameters": __PARAMETERS__,
              "reason": "explore record"
            }
            """.Replace("__PARAMETERS__", parametersJson, StringComparison.Ordinal);

    private static string QueryFor(string operation, string parametersJson) =>
        """
            {
              "schemaVersion": "business-ontology-agent-action-v1",
              "action": "query",
              "identity": {"runId": "analysis-run:submit-record", "turnId": "turn:1"},
              "operation": "__OPERATION__",
              "parameters": __PARAMETERS__,
              "reason": "bounded fixture query"
            }
            """
            .Replace("__OPERATION__", operation, StringComparison.Ordinal)
            .Replace("__PARAMETERS__", parametersJson, StringComparison.Ordinal);

    private static string RecordWith(
        string replacement,
        bool replaceKind = false,
        bool replaceStatus = false,
        bool replaceEvidence = false,
        bool replaceDigest = false)
    {
        var json = """
            {
              "schemaVersion": "business-ontology-agent-action-v1",
              "action": "record",
              "identity": {"runId": "analysis-run:submit-record", "turnId": "turn:2"},
              "records": [
                {
                  "recordId": "observation:submit-state",
                  "kind": "observation",
                  "subjectKind": "lifecycle",
                  "subjectId": "SampleDomain.Ontology.RecordLifecycle",
                  "title": "Submit changes record state",
                  "body": {"from": "DRAFT", "to": "SUBMITTED"},
                  "status": "observed",
                  "uncertainty": 0.05,
                  "queryDigest": "sha256:query-state",
                  "evidenceIds": ["claim:submit-state"]
                }
              ]
            }
            """;
        if (replaceKind) return json.Replace("\"kind\": \"observation\"", "\"kind\": \"claim\"", StringComparison.Ordinal);
        if (replaceStatus) return json.Replace("\"status\": \"observed\"", "\"status\": \"accepted\"", StringComparison.Ordinal);
        if (replaceEvidence) return json.Replace("\"evidenceIds\": [\"claim:submit-state\"]", "\"evidenceIds\": [\"not-known\"]", StringComparison.Ordinal);
        if (replaceDigest) return json.Replace("\"queryDigest\": \"sha256:query-state\"", "\"queryDigest\": \"sha256:unseen\"", StringComparison.Ordinal);
        return json;
    }

    private static string RecordWithProvenance(string queryDigest, string evidenceId) =>
        RecordWith("", false, false, false, false)
            .Replace("\"queryDigest\": \"sha256:query-state\"", $"\"queryDigest\": \"{queryDigest}\"", StringComparison.Ordinal)
            .Replace("\"evidenceIds\": [\"claim:submit-state\"]", $"\"evidenceIds\": [\"{evidenceId}\"]", StringComparison.Ordinal);

    private static BusinessOntologyInvestigationEvidenceRef Evidence(string id, string symbolId, string path) =>
        new(id, "fixture", path, symbolId, 1, 1) { SymbolId = symbolId };

    private static string Synthesize() =>
        """
        {
          "schemaVersion": "business-ontology-agent-action-v1",
          "action": "synthesize",
          "identity": {"runId": "analysis-run:submit-record", "turnId": "turn:4"},
          "domainCharters": [
            {
              "id": "Records",
              "nameZh": "记录管理",
              "descriptionZh": "管理业务记录的提交和状态变化。",
              "evidenceIds": ["claim:submit-state", "evidence:record"],
              "workflowNames": ["记录提交"]
            }
          ],
          "clusters": [
            {
              "id": "cluster:record",
              "domainId": "Records",
              "conceptId": "Records.Record",
              "nameZh": "业务记录",
              "descriptionZh": "可提交并跟踪状态的业务记录。",
              "implementationAnchors": [
                {"symbolId": "symbol:record:controller", "role": "controller", "relativePath": "src/RecordController.java", "evidenceId": "claim:submit-state"},
                {"symbolId": "symbol:record:service", "role": "service", "relativePath": "src/RecordService.java", "evidenceId": "evidence:record"}
              ]
            }
          ]
        }
        """;

    private static string SynthesizeWithSingleAnchor()
    {
        var payload = JsonNode.Parse(Synthesize())!;
        payload["clusters"]![0]!["implementationAnchors"]!.AsArray().RemoveAt(1);
        return payload.ToJsonString();
    }

    private static string SynthesizeItAssetInventory() =>
        SynthesizeWithDomainId("ItAsset.Inventory")
            .Replace("Records.Record", "ItAsset.Asset", StringComparison.Ordinal);

    private static string SynthesizeWithDomainId(string domainId) =>
        Synthesize()
            .Replace("\"id\": \"Records\"", $"\"id\": \"{domainId}\"", StringComparison.Ordinal)
            .Replace("\"domainId\": \"Records\"", $"\"domainId\": \"{domainId}\"", StringComparison.Ordinal);
}
