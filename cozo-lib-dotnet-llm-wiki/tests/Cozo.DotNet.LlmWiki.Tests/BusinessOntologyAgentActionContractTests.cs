using System.Text.Json;
using Cozo.DotNet.LlmWiki.Tools;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyAgentActionContractTests
{
    public static Task RunAsync(Action<bool, string> assert)
    {
        var context = new BusinessOntologyAgentActionValidationContext(
            "analysis-run:submit-record",
            new HashSet<string>(["claim:submit-state", "evidence:record"], StringComparer.Ordinal),
            new HashSet<string>(["sha256:query-state"], StringComparer.Ordinal));
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

        AssertRejects(validator, "{not json", context, assert, "invalid JSON must be rejected before any effect");
        AssertRejects(validator, QueryWith(""" ,"extra": true"""), context, assert, "unknown top-level fields must be rejected");
        AssertRejects(validator, QueryWith(""" ,"operation": "query_named" """, replaceOperation: true), context, assert, "unknown query operation must be rejected");
        AssertRejects(validator, QueryWithParameters("""{"term":"record","sql":"select * from ck_symbol"}"""), context, assert, "operation parameters must reject arbitrary SQL");
        AssertRejects(validator, QueryWithParameters("""{"term":"record","path":"/tmp/source.java"}"""), context, assert, "operation parameters must reject arbitrary source paths");
        AssertRejects(validator, QueryWithParameters("""{"term":"record","provider":"codex-cli"}"""), context, assert, "operation parameters must reject provider steering");
        AssertRejects(validator, RecordWith(""" "kind": "claim" """, replaceKind: true), context, assert, "unknown analysis record kind must be rejected");
        AssertRejects(validator, RecordWith(""" "status": "accepted" """, replaceStatus: true), context, assert, "unknown analysis record status must be rejected");
        AssertRejects(validator, QueryWith(""" ,"identity": {"runId": "analysis-run:other", "turnId": "turn:1"} """, replaceIdentity: true), context, assert, "run identity mismatch must be rejected");
        AssertRejects(validator, RecordWith(""" "evidenceIds": ["not-known"] """, replaceEvidence: true), context, assert, "unknown evidence identities must be rejected locally");
        AssertRejects(validator, RecordWith(""" "queryDigest": "sha256:unseen" """, replaceDigest: true), context, assert, "unknown query digest identities must be rejected locally");
        AssertRejects(
            validator,
            RecordWith(""" "queryDigest": "sha256:query-state" """, replaceDigest: true),
            context with { AvailableQueryDigests = new HashSet<string>(StringComparer.Ordinal) },
            assert,
            "empty query digest scope must not accept arbitrary provenance");

        var schema = BusinessOntologyAgentActionContract.SchemaJson();
        var variants = schema.RootElement.GetProperty("oneOf").EnumerateArray().ToArray();
        assert(variants.Length == 3
                && variants.All(variant => variant.GetProperty("additionalProperties").GetBoolean() == false)
                && variants[0].GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "query"
                && variants[1].GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "record"
                && variants[2].GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "finish",
            "published action schema should close and discriminate query/record/finish variants");
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
}
