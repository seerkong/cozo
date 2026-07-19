using System.Text.Json;
using Cozo.DotNet.LlmWiki.Tools;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyCandidateDiagnosisContractTests
{
    private const string OntologyId = "SampleDomain.Ontology";
    private const string GenerationId = "fixture-1";
    private const string Record = OntologyId + ".Record";
    private const string Supplier = OntologyId + ".Supplier";

    public static void Run(Action<bool, string> assert)
    {
        var run = new BusinessOntologyAnalysisRun(
            "analysis-run:diagnosis",
            OntologyId,
            GenerationId,
            "codex-cli",
            "gpt-5.6-terra",
            "candidate export diagnosis",
            "completed",
            "2026-07-19T00:00:00Z",
            "2026-07-19T00:01:00Z",
            "sha256:input");
        var acceptedDraft = Draft(
            "draft:relation",
            "relation",
            Record + ".supplier",
            "relation",
            """{"id":"SampleDomain.Ontology.Relation.Supplier","name":"supplier","fromConceptId":"SampleDomain.Ontology.Record","toConceptId":"SampleDomain.Ontology.Supplier","directed":true,"min":"0","max":"1","descriptionZh":"记录关联供应商。"}""",
            ["evidence:relation"],
            "sha256:query-relation");
        var rejectedObservation = Draft(
            "observation:relation-name",
            "relation",
            Record + ".supplier",
            "relation",
            """{"id":"SampleDomain.Ontology.Relation.Supplier","name":"supplier","fromConceptId":"SampleDomain.Ontology.Record","toConceptId":"SampleDomain.Ontology.Supplier","directed":true,"min":"0","max":"1","descriptionZh":"看起来像草案但不是。"}""",
            ["evidence:relation"],
            "sha256:query-observation") with
        {
            Kind = "observation",
            Status = "observed",
            Title = "观察到供应商字段"
        };
        var missingEvidenceDraft = Draft(
            "draft:missing-evidence",
            "concept",
            Record,
            "type",
            """{"id":"SampleDomain.Ontology.MissingEvidence","descriptionZh":"缺少推断证据。"}""",
            ["evidence:contractual"],
            "sha256:query-missing-evidence");

        var validator = new OntologyCandidateDraftXmlMappingValidator();
        var evidence = new[]
        {
            new OntologyCandidateDraftEvidence("evidence:relation", "inferred"),
            new OntologyCandidateDraftEvidence("evidence:contractual", "contractual"),
        };
        var decisions = new[]
        {
            BusinessOntologyCandidateMappingRecord.From(
                acceptedDraft,
                "xml:relation:SampleDomain.Ontology.Relation.Supplier",
                validator.Evaluate(acceptedDraft, [Record, Supplier], evidence)),
            BusinessOntologyCandidateMappingRecord.From(
                rejectedObservation,
                null,
                validator.Evaluate(rejectedObservation, [Record, Supplier], evidence)),
            BusinessOntologyCandidateMappingRecord.From(
                missingEvidenceDraft,
                null,
                validator.Evaluate(missingEvidenceDraft, [Record, Supplier], evidence)),
        };

        var diagnosis = BusinessOntologyCandidateDiagnosisDocument.Create(
            OntologyId,
            GenerationId,
            "2026-07-19T00:02:00Z",
            [run],
            [acceptedDraft, rejectedObservation, missingEvidenceDraft],
            decisions);
        assert(diagnosis.SchemaVersion == BusinessOntologyCandidateDiagnosisDocument.SchemaVersionValue,
            "diagnosis document should expose a stable schema version");
        assert(diagnosis.Items.Count == 3
                && diagnosis.Items[0].RunId == run.RunId
                && diagnosis.Items[0].RecordId == acceptedDraft.RecordId
                && diagnosis.Items[0].CandidateXmlId == "xml:relation:SampleDomain.Ontology.Relation.Supplier"
                && diagnosis.Items[0].QueryDigest == "sha256:query-relation"
                && diagnosis.Items[0].EvidenceIds.SequenceEqual(["evidence:relation"], StringComparer.Ordinal)
                && diagnosis.Items[0].MappingDecision.Mappable
                && diagnosis.Items[1].MappingDecision.RejectReasons.SequenceEqual([OntologyCandidateDraftRejectReasons.NotCandidateDraftRecord], StringComparer.Ordinal)
                && diagnosis.Items[2].MappingDecision.RejectReasons.SequenceEqual([OntologyCandidateDraftRejectReasons.MissingInferredEvidence], StringComparer.Ordinal),
            "diagnosis items should preserve run/record, candidate XML id, query, evidence, mapping decisions, and stable reject reasons");

        var json = diagnosis.ToCanonicalJson();
        assert(json.Contains("\"schemaVersion\":\"business-ontology-candidate-diagnosis-v1\"", StringComparison.Ordinal)
                && json.Contains("\"candidateXmlId\":\"xml:relation:SampleDomain.Ontology.Relation.Supplier\"", StringComparison.Ordinal)
                && json.Contains("\"rejectReasons\":[\"not_candidate_draft_record\"]", StringComparison.Ordinal)
                && !json.Contains("onto_review", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("materialization", StringComparison.OrdinalIgnoreCase),
            "diagnosis canonical JSON should be stable and must not expose accepted review/materialization write concepts");

        var markdown = diagnosis.ToMarkdown();
        assert(markdown.Contains("| `draft:relation` | `candidate_draft` | `xml:relation:SampleDomain.Ontology.Relation.Supplier` | `mappable` | `sha256:query-relation` |", StringComparison.Ordinal)
                && markdown.Contains("| `observation:relation-name` | `observation` |  | `not_candidate_draft_record` | `sha256:query-observation` |", StringComparison.Ordinal),
            "diagnosis Markdown should provide a deterministic reviewable table schema");

        var packet = BusinessOntologyAdvisoryReviewPacket.Create(
            OntologyId,
            GenerationId,
            "2026-07-19T00:03:00Z",
            decisions,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xml:relation:SampleDomain.Ontology.Relation.Supplier"] = BusinessOntologyReviewSuggestions.Defer,
            });
        assert(packet.SchemaVersion == BusinessOntologyAdvisoryReviewPacket.SchemaVersionValue
                && packet.Items.Count == 1
                && packet.Items.Single().SuggestedDecision == BusinessOntologyReviewSuggestions.Defer
                && packet.Items.Single().RunId == run.RunId
                && packet.Items.Single().RecordId == acceptedDraft.RecordId
                && packet.Items.Single().QueryDigest == acceptedDraft.QueryDigest
                && packet.Items.Single().EvidenceIds.SequenceEqual(["evidence:relation"], StringComparer.Ordinal)
                && packet.Items.Single().MappingDecision.Mappable,
            "review packet should include only mappable candidates and keep provenance plus mapping decisions");
        assert(packet.ToCanonicalJson().Contains("\"suggestedDecision\":\"defer\"", StringComparison.Ordinal)
                && packet.ToMarkdown().Contains("| `xml:relation:SampleDomain.Ontology.Relation.Supplier` | `relation` | `SampleDomain.Ontology.Relation.Supplier` | `defer` |", StringComparison.Ordinal),
            "review packet JSON and Markdown should expose a stable advisory schema");

        foreach (var allowed in new[]
        {
            BusinessOntologyReviewSuggestions.Accept,
            BusinessOntologyReviewSuggestions.Reject,
            BusinessOntologyReviewSuggestions.Defer,
            BusinessOntologyReviewSuggestions.RequestEvidence,
        })
        {
            _ = BusinessOntologyAdvisoryReviewPacket.Create(
                OntologyId,
                GenerationId,
                "2026-07-19T00:03:00Z",
                [decisions[0]],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["xml:relation:SampleDomain.Ontology.Relation.Supplier"] = allowed,
                });
        }

        assert(Rejects(() => BusinessOntologyAdvisoryReviewPacket.Create(
                    OntologyId,
                    GenerationId,
                    "2026-07-19T00:03:00Z",
                    [decisions[0]],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["xml:relation:SampleDomain.Ontology.Relation.Supplier"] = "approved",
                    }),
                "accept, reject, defer, or request_evidence"),
            "review suggestions should be closed to accept/reject/defer/request_evidence");

        var zeroCandidateMappings = new[]
        {
            BusinessOntologyCandidateMappingRecord.From(
                acceptedDraft,
                null,
                validator.Evaluate(acceptedDraft, [Record, Supplier], [])),
            BusinessOntologyCandidateMappingRecord.From(
                rejectedObservation,
                null,
                validator.Evaluate(rejectedObservation, [Record, Supplier], [])),
            BusinessOntologyCandidateMappingRecord.From(
                missingEvidenceDraft,
                null,
                validator.Evaluate(missingEvidenceDraft, [Record, Supplier], [])),
        };
        var zeroCandidateDiagnosis = BusinessOntologyCandidateDiagnosisDocument.Create(
            OntologyId,
            GenerationId,
            "2026-07-19T00:04:00Z",
            [run],
            [acceptedDraft, rejectedObservation, missingEvidenceDraft],
            zeroCandidateMappings);
        var zeroCandidatePacket = BusinessOntologyAdvisoryReviewPacket.Create(
            OntologyId,
            GenerationId,
            "2026-07-19T00:04:00Z",
            zeroCandidateMappings,
            new Dictionary<string, string>(StringComparer.Ordinal));
        assert(zeroCandidateDiagnosis.Items.Count == 3
                && zeroCandidateDiagnosis.Items.All(item => item.CandidateXmlId is null)
                && zeroCandidateDiagnosis.Items.Any(item => item.MappingDecision.RejectReasons.Contains(OntologyCandidateDraftRejectReasons.UnavailableEvidence, StringComparer.Ordinal))
                && zeroCandidatePacket.Items.Count == 0,
            "a zero-candidate analysis must still diagnose every record while producing no advisory review items");
        assert(Rejects(() => BusinessOntologyCandidateDiagnosisDocument.Create(
                    OntologyId,
                    GenerationId,
                    "2026-07-19T00:04:00Z",
                    [run],
                    [acceptedDraft, rejectedObservation, missingEvidenceDraft],
                    zeroCandidateMappings.Take(2).ToArray()),
                "exactly one mapping for every analysis record"),
            "diagnosis construction must reject a partial mapping rather than silently omitting an analysis record");
    }

    private static BusinessOntologyAnalysisRecord Draft(
        string recordId,
        string subjectKind,
        string subjectId,
        string xmlKind,
        string semanticJson,
        IReadOnlyList<string> evidenceIds,
        string queryDigest) =>
        new(
            "analysis-run:diagnosis",
            recordId,
            "candidate_draft",
            subjectKind,
            subjectId,
            "candidate draft",
            $$"""{"schemaVersion":"candidate-draft-to-ontology-xml-v1","xmlKind":"{{xmlKind}}","semantic":{{semanticJson}}}""",
            "proposed",
            0.25,
            queryDigest,
            "2026-07-19T00:00:30Z",
            evidenceIds);

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
}
