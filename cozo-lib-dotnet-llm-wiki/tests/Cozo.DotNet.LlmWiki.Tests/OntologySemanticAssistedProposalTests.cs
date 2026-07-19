using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.LlmClient;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class OntologySemanticAssistedProposalTests
{
    private const string OntologyId = "SampleDomain.Ontology";
    private const string Record = OntologyId + ".Record";
    private const string Supplier = OntologyId + ".Supplier";

    public static async Task RunAsync(Action<bool, string> assert)
    {
        AssertsClosedCriticAndExperimentBudgets(assert);
        await ProjectsCodexCliResponsesThroughLocalSemanticGovernanceAsync(assert);

        var root = Path.Combine(Path.GetTempPath(), "onto-assisted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "src", "RecordEntity.java"),
                "class RecordEntity {\n  SupplierEntity supplier;\n}\n");
            var payload = JsonSerializer.Serialize(new
            {
                collection = false,
                member = "supplier",
                ownerSymbolId = "symbol:record",
                rawType = "SupplierEntity",
                resolvedTypeName = "SupplierEntity",
                resolvedTypeSymbolId = "symbol:supplier",
            });
            var claim = new CodeSemanticClaimFact(
                CodeSemanticClaimIdentity.Create(
                    "file:record",
                    CodeSemanticClaimKinds.TypedReference,
                    payload,
                    2,
                    2),
                "symbol:record:supplier",
                CodeSemanticClaimKinds.TypedReference,
                payload,
                "file:record",
                2,
                2,
                0.96,
                "treesitter",
                "direct typed reference");
            var evidenceId = claim.ClaimId;
            var response = JsonSerializer.Serialize(new
            {
                schemaVersion = OntologySemanticCandidateValidator.SchemaVersion,
                candidates = new[]
                {
                    new
                    {
                        kind = OntologySemanticCandidateKinds.Relation,
                        semantic = new
                        {
                            id = Record + ".vendor",
                            fromConceptId = Record,
                            toConceptId = Supplier,
                            name = "vendor",
                            min = "0",
                            max = "1",
                            descriptionZh = "记录关联的供应商。",
                        },
                        evidenceIds = new[] { evidenceId },
                        basis = BusinessOntologySemanticProjectionModes.Assisted,
                        rationale = "字段声明直接引用已存在的供应商概念。",
                    },
                },
            });
            var llm = new FakeLlmClient(response);

            using var db = new CozoDb("mem", "");
            var om = new CozoOm(db);
            await om.InitCodeKnowledgeAsync();
            await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Repositories: [new CodeRepositoryFact("repo:record", root, "record")],
                Files: [new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java")],
                SemanticClaims: [claim]));
            var store = new BusinessOntologyStore(om);
            await store.ReplaceGenerationAsync(BaseGeneration());

            var projector = new BusinessOntologySemanticProjector(
                om,
                store,
                llmClient: llm);
            await projector.ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "deterministic",
                "fingerprint",
                "2026-07-18T12:00:00Z"));
            assert(llm.CallCount == 0,
                "deterministic mode must not invoke an injected ILlmClient");

            await projector.ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "assisted",
                "fingerprint",
                "2026-07-18T12:01:00Z",
                Mode: BusinessOntologySemanticProjectionModes.Assisted));
            var snapshot = await store.ReadExportableAsync(OntologyId);
            var assisted = snapshot.Candidates.Single(candidate =>
                candidate.PayloadJson.Contains("\"basis\":\"assisted\"", StringComparison.Ordinal));
            assert(llm.CallCount == 1
                    && assisted.SubjectKind == OntologySemanticCandidateKinds.Relation
                    && assisted.Status == "pending"
                    && assisted.EvidenceIds.SequenceEqual([evidenceId], StringComparer.Ordinal),
                "assisted mode should call the injected client once and locally create one pending, evidence-backed candidate");
            assert(snapshot.Candidates.Count > 0
                    && snapshot.Candidates.All(candidate => candidate.Status == "pending")
                    && snapshot.Relations.Count == 0
                    && snapshot.Rules.Count == 0
                    && snapshot.Lifecycles.Count == 0,
                "assisted and deterministic proposals must remain pending and must not materialize accepted semantic records");
            assert(snapshot.Evidence.Select(item => item.Id).Order(StringComparer.Ordinal).SequenceEqual(
                        new[] { "base:record", "base:supplier", evidenceId }.Order(StringComparer.Ordinal),
                        StringComparer.Ordinal)
                    && snapshot.Evidence.Single(item => item.Id == evidenceId) is
                    {
                        Resolver: "treesitter",
                        SourceKind: "code-semantic-claim",
                        Summary: "直接源码语义事实：typed_reference。",
                    }
                    && snapshot.Evidence.All(item =>
                        !item.Summary.Contains("字段声明直接引用已存在的供应商概念", StringComparison.Ordinal)
                        && !item.Resolver.Contains("fake", StringComparison.OrdinalIgnoreCase)),
                "provider identity, response text, and rationale must never become BusinessOntologyEvidence");
            assert(llm.SystemPrompt == OntologySemanticAssistedProposalClient.SystemPrompt
                    && llm.SystemPrompt.Contains("exactly one JSON object", StringComparison.Ordinal)
                    && llm.SystemPrompt.Contains("Do not use Markdown or code fences", StringComparison.Ordinal)
                    && llm.SystemPrompt.Contains("Do not reveal hidden chain-of-thought", StringComparison.Ordinal)
                    && llm.SystemPrompt.Contains("Do not invent concepts", StringComparison.Ordinal)
                    && llm.SystemPrompt.Contains("exact closed enum", StringComparison.Ordinal)
                    && llm.SystemPrompt.Contains("Never emit \"type\"", StringComparison.Ordinal)
                    && llm.UserPrompt.Contains("\"schemaVersion\":\"onto-semantic-v1\"", StringComparison.Ordinal)
                    && llm.UserPrompt.Contains("\"path\":\"src/RecordEntity.java\"", StringComparison.Ordinal)
                    && !llm.UserPrompt.Contains(root, StringComparison.Ordinal),
                "the assisted request should use the strict prompt and a repository-relative evidence envelope");
            assert(llm.LastOptions?.MaxTokens == OntologySemanticAssistedProposalClient.MaxOutputTokens
                    && llm.LastOptions?.Temperature == 0,
                "assisted mode should enforce its output-token and deterministic-temperature bounds");

            var malformedThenValid = new FakeLlmClient("not json", response);
            await new BusinessOntologySemanticProjector(om, store, llmClient: malformedThenValid)
                .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                    OntologyId,
                    "assisted-retry",
                    "fingerprint-retry",
                    "2026-07-18T12:02:00Z",
                    Mode: BusinessOntologySemanticProjectionModes.Assisted));
            assert(malformedThenValid.CallCount == 2,
                "malformed JSON should be retried exactly once before a valid response is accepted");

            var beforeMalformedFailure = SnapshotBytes(await store.ReadExportableAsync(OntologyId));
            var malformedFailureClient = new FakeLlmClient("not json", "{still not json");
            var malformedFailure = await CaptureFailureAsync(() =>
                new BusinessOntologySemanticProjector(om, store, llmClient: malformedFailureClient)
                    .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                        OntologyId,
                        "must-not-exist-malformed",
                        "malformed-json",
                        "2026-07-18T12:02:30Z",
                        Mode: BusinessOntologySemanticProjectionModes.Assisted)));
            var afterMalformedFailure = SnapshotBytes(await store.ReadExportableAsync(OntologyId));
            assert(malformedFailureClient.CallCount == OntologySemanticAssistedProposalClient.MaxAttempts
                    && malformedFailure.Diagnostic.Category == "malformed_json"
                    && malformedFailure.Diagnostic.Attempts == OntologySemanticAssistedProposalClient.MaxAttempts
                    && beforeMalformedFailure == afterMalformedFailure,
                "two malformed JSON responses must fail after one retry without replacing the active generation");

            using var validDocument = JsonDocument.Parse(response);
            var validCandidate = validDocument.RootElement.GetProperty("candidates")[0].Clone();
            var allOrNothingResponse = JsonSerializer.Serialize(new
            {
                schemaVersion = OntologySemanticCandidateValidator.SchemaVersion,
                candidates = new object[]
                {
                    validCandidate,
                    new
                    {
                        kind = OntologySemanticCandidateKinds.Relation,
                        semantic = new
                        {
                            id = Record + ".unsupportedOwner",
                            fromConceptId = OntologyId + ".Hallucinated",
                            toConceptId = Supplier,
                            name = "unsupportedOwner",
                            min = "0",
                            max = "1",
                            descriptionZh = "不存在的概念。",
                        },
                        evidenceIds = new[] { evidenceId },
                        basis = BusinessOntologySemanticProjectionModes.Assisted,
                        rationale = "引用了模型虚构的概念。",
                    },
                },
            });
            var beforeSemanticFailure = SnapshotBytes(await store.ReadExportableAsync(OntologyId));
            var semanticFailureClient = new FakeLlmClient(allOrNothingResponse, response);
            var semanticFailure = await CaptureFailureAsync(() =>
                new BusinessOntologySemanticProjector(om, store, llmClient: semanticFailureClient)
                    .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                        OntologyId,
                        "must-not-exist",
                        "invalid-semantic",
                        "2026-07-18T12:03:00Z",
                        Mode: BusinessOntologySemanticProjectionModes.Assisted)));
            var afterSemanticFailure = SnapshotBytes(await store.ReadExportableAsync(OntologyId));
            assert(semanticFailureClient.CallCount == 1
                    && semanticFailure.Diagnostic.Category == "semantic_validation"
                    && beforeSemanticFailure == afterSemanticFailure,
                "a locally invalid candidate should not retry or partially replace the active generation");

            var hallucinatedEvidenceResponse = response.Replace(
                $"\"{evidenceId}\"",
                "\"evidence:model-invented\"",
                StringComparison.Ordinal);
            var beforeEvidenceFailure = SnapshotBytes(await store.ReadExportableAsync(OntologyId));
            var evidenceFailureClient = new FakeLlmClient(hallucinatedEvidenceResponse, response);
            var evidenceFailure = await CaptureFailureAsync(() =>
                new BusinessOntologySemanticProjector(om, store, llmClient: evidenceFailureClient)
                    .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                        OntologyId,
                        "must-not-exist-evidence",
                        "invented-evidence",
                        "2026-07-18T12:03:30Z",
                        Mode: BusinessOntologySemanticProjectionModes.Assisted)));
            var afterEvidenceFailure = SnapshotBytes(await store.ReadExportableAsync(OntologyId));
            assert(evidenceFailureClient.CallCount == 1
                    && evidenceFailure.Diagnostic.Category == "semantic_validation"
                    && beforeEvidenceFailure == afterEvidenceFailure,
                "a hallucinated evidence identity must fail locally without retry or partial generation writes");

            var longUnknownField = new string('x', 2_000);
            var malformedEnvelope = "{\"schemaVersion\":\"onto-semantic-v1\",\"candidates\":[],\""
                + longUnknownField + "\":true}";
            var beforeEnvelopeFailure = SnapshotBytes(await store.ReadExportableAsync(OntologyId));
            var envelopeFailureClient = new FakeLlmClient(malformedEnvelope, malformedEnvelope);
            var envelopeFailure = await CaptureFailureAsync(() =>
                new BusinessOntologySemanticProjector(om, store, llmClient: envelopeFailureClient)
                    .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                        OntologyId,
                        "must-not-exist-envelope",
                        "invalid-envelope",
                        "2026-07-18T12:04:00Z",
                        Mode: BusinessOntologySemanticProjectionModes.Assisted)));
            var afterEnvelopeFailure = SnapshotBytes(await store.ReadExportableAsync(OntologyId));
            assert(envelopeFailureClient.CallCount == OntologySemanticAssistedProposalClient.MaxAttempts
                    && envelopeFailure.Diagnostic.Category == "invalid_envelope"
                    && envelopeFailure.Diagnostic.Attempts == OntologySemanticAssistedProposalClient.MaxAttempts
                    && envelopeFailure.Diagnostic.ResponseSha256
                        == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(malformedEnvelope))).ToLowerInvariant()
                    && envelopeFailure.Diagnostic.Message.Length
                        <= OntologySemanticAssistedProposalClient.MaxDiagnosticMessageCharacters
                    && !envelopeFailure.Diagnostic.Message.Contains(longUnknownField, StringComparison.Ordinal)
                    && beforeEnvelopeFailure == afterEnvelopeFailure,
                "invalid envelopes should retry at most once and expose only bounded digest diagnostics without writes");

            var oversizedResponse = new string('x', OntologySemanticAssistedProposalClient.MaxResponseUtf8Bytes + 1);
            var responseBoundsClient = new FakeLlmClient(oversizedResponse, oversizedResponse);
            var responseBoundsFailure = await CaptureFailureAsync(() =>
                new BusinessOntologySemanticProjector(om, store, llmClient: responseBoundsClient)
                    .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                        OntologyId,
                        "must-not-exist-bounds",
                        "response-bounds",
                        "2026-07-18T12:05:00Z",
                        Mode: BusinessOntologySemanticProjectionModes.Assisted)));
            assert(responseBoundsClient.CallCount == OntologySemanticAssistedProposalClient.MaxAttempts
                    && responseBoundsFailure.Diagnostic.Category == "response_bounds",
                "oversized provider text should be rejected within the bounded envelope retry policy");

            var tooManyCandidatesResponse = JsonSerializer.Serialize(new
            {
                schemaVersion = OntologySemanticCandidateValidator.SchemaVersion,
                candidates = Enumerable.Repeat(validCandidate, OntologySemanticAssistedProposalClient.MaxCandidates + 1),
            });
            var candidateBoundsClient = new FakeLlmClient(tooManyCandidatesResponse, tooManyCandidatesResponse);
            var candidateBoundsFailure = await CaptureFailureAsync(() =>
                new BusinessOntologySemanticProjector(om, store, llmClient: candidateBoundsClient)
                    .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                        OntologyId,
                        "must-not-exist-candidate-bounds",
                        "candidate-bounds",
                        "2026-07-18T12:05:30Z",
                        Mode: BusinessOntologySemanticProjectionModes.Assisted)));
            assert(candidateBoundsClient.CallCount == OntologySemanticAssistedProposalClient.MaxAttempts
                    && candidateBoundsFailure.Diagnostic.Category == "response_bounds",
                "provider candidate count should be bounded independently of the response byte limit");

            var packAnchor = new SemanticEvidencePackAnchor(
                evidenceId,
                "repo:record",
                "src/RecordEntity.java",
                claim.SubjectId,
                claim.Kind,
                claim.PayloadJson,
                2,
                2,
                1,
                3,
                "class RecordEntity {}",
                [Record, Supplier],
                "",
                false);
            var invalidPack = new SemanticEvidencePack(
                Enumerable.Repeat(packAnchor, SemanticEvidencePackBuilder.MaxAnchors + 1).ToArray(),
                20,
                0,
                false);
            var packBoundsClient = new FakeLlmClient(response);
            var packRejected = await ThrowsArgumentAsync(() =>
                new OntologySemanticAssistedProposalClient(packBoundsClient)
                    .ProposeAsync(invalidPack, [Record, Supplier], [evidenceId]));
            assert(packRejected && packBoundsClient.CallCount == 0,
                "an oversized evidence pack should fail locally before invoking the provider");

            var parser = new OntologySemanticAssistedProposalClient(llm);
            var withModelOwnedStatus = response.Replace(
                "\"kind\":\"relation\"",
                "\"status\":\"accepted\",\"kind\":\"relation\"",
                StringComparison.Ordinal);
            assert(ThrowsArgument(() => parser.ParseResponse(
                    withModelOwnedStatus,
                    [Record, Supplier],
                    [evidenceId])),
                "strict response parsing must reject model-owned status or acceptance fields");
            var duplicateRoot = response.Replace(
                "\"schemaVersion\":\"onto-semantic-v1\"",
                "\"schemaVersion\":\"onto-semantic-v1\",\"schemaVersion\":\"onto-semantic-v1\"",
                StringComparison.Ordinal);
            assert(ThrowsArgument(() => parser.ParseResponse(
                    duplicateRoot,
                    [Record, Supplier],
                    [evidenceId])),
                "strict response parsing must reject duplicate fields");
            var duplicateCandidates = JsonSerializer.Serialize(new
            {
                schemaVersion = OntologySemanticCandidateValidator.SchemaVersion,
                candidates = new[] { validCandidate, validCandidate },
            });
            assert(parser.ParseResponse(
                    duplicateCandidates,
                    [Record, Supplier],
                    [evidenceId]).Count == 1,
                "fully valid candidates should be deterministically deduplicated before generation merge");
            var duplicateClient = new FakeLlmClient(duplicateCandidates);
            await new BusinessOntologySemanticProjector(om, store, llmClient: duplicateClient)
                .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                    OntologyId,
                    "assisted-duplicate",
                    "duplicate-semantic-payload",
                    "2026-07-18T12:05:45Z",
                    Mode: BusinessOntologySemanticProjectionModes.Assisted));
            var duplicateSnapshot = await store.ReadExportableAsync(OntologyId);
            assert(duplicateClient.CallCount == 1
                    && duplicateSnapshot.Candidates.Count(candidate =>
                        candidate.ProposedId == Record + ".vendor") == 1
                    && duplicateSnapshot.Candidates.All(candidate => candidate.Status == "pending")
                    && duplicateSnapshot.Relations.Count == 0
                    && duplicateSnapshot.Rules.Count == 0
                    && duplicateSnapshot.Lifecycles.Count == 0,
                "duplicate semantic payloads must merge into one pending candidate without accepted records");

            using var unavailableDb = new CozoDb("mem", "");
            var unavailableOm = new CozoOm(unavailableDb);
            var unavailableFailure = await ThrowsInvalidOperationAsync(() =>
                new BusinessOntologySemanticProjector(
                    unavailableOm,
                    new BusinessOntologyStore(unavailableOm),
                    llmClient: new UnavailableLlmClient())
                .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                    OntologyId,
                    "unavailable",
                    "unavailable",
                    "2026-07-18T12:06:00Z",
                    Mode: BusinessOntologySemanticProjectionModes.Assisted)));
            var relations = await unavailableOm.Runtime.Store.RunAsync("::relations");
            assert(unavailableFailure
                    && !relations.Rows.SelectMany(row => row)
                        .Any(value => value.ToString().Contains("onto_", StringComparison.Ordinal)),
                "an unavailable assisted client must fail before any onto schema or write transaction is started");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertsClosedCriticAndExperimentBudgets(Action<bool, string> assert)
    {
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = OntologySemanticCandidateValidator.SchemaVersion,
            kind = OntologySemanticCandidateKinds.Relation,
            semantic = new
            {
                id = Record + ".vendor",
                fromConceptId = Record,
                toConceptId = Supplier,
                name = "vendor",
                min = "0",
                max = "1",
                descriptionZh = "记录关联供应商。",
            },
            evidenceIds = new[] { "evidence:record-supplier" },
            basis = BusinessOntologySemanticProjectionModes.Assisted,
            rationale = "直接类型引用。",
        });
        var candidate = new OntologySemanticCandidateValidator().Validate(
            payload,
            [Record, Supplier],
            ["evidence:record-supplier"]);
        var validCritic = JsonSerializer.Serialize(new
        {
            schemaVersion = OntologySemanticCriticClient.SchemaVersion,
            decisions = new[]
            {
                new { candidateId = candidate.Id, decision = "keep", rationale = "证据足够。" },
            },
        });
        var decisions = OntologySemanticCriticClient.ParseResponse(validCritic, [candidate.Id]);
        assert(decisions is [{ CandidateId: var id, Decision: "keep" }] && id == candidate.Id,
            "v3 critic should retain only a supplied locally validated candidate ID");

        var inventedId = validCritic.Replace(candidate.Id, "candidate:semantic:invented", StringComparison.Ordinal);
        var rejectedInvented = false;
        try
        {
            _ = OntologySemanticCriticClient.ParseResponse(inventedId, [candidate.Id]);
        }
        catch (ArgumentException)
        {
            rejectedInvented = true;
        }
        assert(rejectedInvented,
            "v3 critic must reject an unknown candidate ID instead of creating or reviewing it");

        var v2 = OntologySemanticExperimentProfiles.Resolve(OntologySemanticExperimentProfiles.V2);
        var v3 = OntologySemanticExperimentProfiles.Resolve(OntologySemanticExperimentProfiles.V3);
        var v2Budget = OntologySemanticExperimentBudget.Create(v2, 2);
        var v3Budget = OntologySemanticExperimentBudget.Create(v3, 2);
        assert(v2Budget is { MaxSlices: 2, MaxCompletions: 2 }
                && v3Budget is { MaxSlices: 2, MaxCompletions: 4 },
            "v2/v3 budget accounting should reserve one proposal or proposal-plus-critic completion per slice");
    }

    private static string SnapshotBytes(BusinessOntologySnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot);

    private static async Task ProjectsCodexCliResponsesThroughLocalSemanticGovernanceAsync(Action<bool, string> assert)
    {
        const string model = "semantic-codex-fixture-model";
        const string rationale = "Codex CLI 基于已索引字段引用提出供应商关系。";
        var root = Path.Combine(Path.GetTempPath(), "onto-assisted-codex-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "src", "RecordEntity.java"),
                "class RecordEntity {\n  SupplierEntity supplier;\n}\n");
            var payload = JsonSerializer.Serialize(new
            {
                collection = false,
                member = "supplier",
                ownerSymbolId = "symbol:record",
                rawType = "SupplierEntity",
                resolvedTypeName = "SupplierEntity",
                resolvedTypeSymbolId = "symbol:supplier",
            });
            var claim = new CodeSemanticClaimFact(
                CodeSemanticClaimIdentity.Create(
                    "file:record",
                    CodeSemanticClaimKinds.TypedReference,
                    payload,
                    2,
                    2),
                "symbol:record:supplier",
                CodeSemanticClaimKinds.TypedReference,
                payload,
                "file:record",
                2,
                2,
                0.96,
                "treesitter",
                "direct typed reference");
            var evidenceId = claim.ClaimId;
            var validResponse = JsonSerializer.Serialize(new
            {
                schemaVersion = OntologySemanticCandidateValidator.SchemaVersion,
                candidates = new[]
                {
                    new
                    {
                        kind = OntologySemanticCandidateKinds.Relation,
                        semantic = new
                        {
                            id = Record + ".codexVendor",
                            fromConceptId = Record,
                            toConceptId = Supplier,
                            name = "codexVendor",
                            min = "0",
                            max = "1",
                            descriptionZh = "记录关联的供应商。",
                        },
                        evidenceIds = new[] { evidenceId },
                        basis = BusinessOntologySemanticProjectionModes.Assisted,
                        rationale,
                    },
                },
            });

            using var db = new CozoDb("mem", "");
            var om = new CozoOm(db);
            await om.InitCodeKnowledgeAsync();
            await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Repositories: [new CodeRepositoryFact("repo:record", root, "record")],
                Files: [new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java")],
                SemanticClaims: [claim]));
            var store = new BusinessOntologyStore(om);
            await store.ReplaceGenerationAsync(BaseGeneration());

            using (var validCodex = await SemanticCodexCliFixture.CreateAsync(validResponse))
            {
                var client = new CodexCliLlmClient(
                    validCodex.ExecutablePath,
                    new LlmClientConfig(
                        Provider: "codex-cli",
                        Model: model,
                        Timeout: TimeSpan.FromSeconds(5)));
                await new BusinessOntologySemanticProjector(om, store, llmClient: client)
                    .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                        OntologyId,
                        "codex-cli-valid",
                        "codex-cli-valid-fingerprint",
                        "2026-07-18T13:00:00Z",
                        Mode: BusinessOntologySemanticProjectionModes.Assisted));

                var snapshot = await store.ReadExportableAsync(OntologyId);
                var candidate = snapshot.Candidates.Single(item => item.ProposedId == Record + ".codexVendor");
                assert(candidate.Status == "pending"
                        && candidate.SubjectKind == OntologySemanticCandidateKinds.Relation
                        && candidate.EvidenceIds.SequenceEqual([evidenceId], StringComparer.Ordinal)
                        && candidate.PayloadJson.Contains("\"basis\":\"assisted\"", StringComparison.Ordinal),
                    "a valid Codex CLI response must create only a locally evidence-backed pending candidate");
                assert(snapshot.Evidence.All(item =>
                        !JsonSerializer.Serialize(item).Contains(model, StringComparison.Ordinal)
                        && !JsonSerializer.Serialize(item).Contains(rationale, StringComparison.Ordinal)
                        && !JsonSerializer.Serialize(item).Contains("codex-cli-valid", StringComparison.Ordinal)),
                    "Codex CLI identity, model, rationale, and response content must not become ontology evidence");
                assert(snapshot.Candidates.All(item => item.Status == "pending")
                        && snapshot.Reviews.Count == 0
                        && snapshot.Relations.Count == 0
                        && snapshot.Rules.Count == 0
                        && snapshot.Lifecycles.Count == 0
                        && snapshot.States.Count == 0
                        && snapshot.Transitions.Count == 0,
                    "a valid Codex CLI response must not accept or materialize semantic records without an explicit review");

                var materialized = await new BusinessOntologyMaterializationService(store)
                    .MaterializeAsync(OntologyId);
                var afterMaterialization = await store.ReadExportableAsync(OntologyId);
                assert(materialized.Relations == 0
                        && materialized.Rules == 0
                        && materialized.Lifecycles == 0
                        && materialized.States == 0
                        && materialized.Transitions == 0
                        && afterMaterialization.Candidates.All(item => item.Status == "pending")
                        && afterMaterialization.Relations.Count == 0,
                    "materialization must remain a no-op for Codex CLI candidates without accepted reviews");
            }

            var beforeHallucinatedEvidence = SnapshotBytes(await store.ReadExportableAsync(OntologyId));
            var hallucinatedEvidenceResponse = validResponse.Replace(
                $"\"{evidenceId}\"",
                "\"evidence:codex-invented\"",
                StringComparison.Ordinal);
            using (var hallucinatedCodex = await SemanticCodexCliFixture.CreateAsync(hallucinatedEvidenceResponse))
            {
                var hallucinatedFailure = await CaptureFailureAsync(() =>
                    new BusinessOntologySemanticProjector(
                        om,
                        store,
                        llmClient: new CodexCliLlmClient(
                            hallucinatedCodex.ExecutablePath,
                            new LlmClientConfig(Provider: "codex-cli", Timeout: TimeSpan.FromSeconds(5))))
                    .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                        OntologyId,
                        "codex-cli-hallucinated-evidence",
                        "codex-cli-hallucinated-evidence-fingerprint",
                        "2026-07-18T13:01:00Z",
                        Mode: BusinessOntologySemanticProjectionModes.Assisted)));
                var afterHallucinatedEvidence = SnapshotBytes(await store.ReadExportableAsync(OntologyId));
                assert(hallucinatedFailure.Diagnostic.Category == "semantic_validation"
                        && beforeHallucinatedEvidence == afterHallucinatedEvidence,
                    "hallucinated Codex CLI evidence must be rejected by local validation without partial generation writes");
            }

            var beforeMalformedResponse = SnapshotBytes(await store.ReadExportableAsync(OntologyId));
            using (var malformedCodex = await SemanticCodexCliFixture.CreateAsync("not-json"))
            {
                var malformedFailure = await CaptureFailureAsync(() =>
                    new BusinessOntologySemanticProjector(
                        om,
                        store,
                        llmClient: new CodexCliLlmClient(
                            malformedCodex.ExecutablePath,
                            new LlmClientConfig(Provider: "codex-cli", Timeout: TimeSpan.FromSeconds(5))))
                    .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                        OntologyId,
                        "codex-cli-malformed",
                        "codex-cli-malformed-fingerprint",
                        "2026-07-18T13:02:00Z",
                        Mode: BusinessOntologySemanticProjectionModes.Assisted)));
                var afterMalformedResponse = SnapshotBytes(await store.ReadExportableAsync(OntologyId));
                assert(malformedFailure.Diagnostic.Category == "malformed_json"
                        && malformedFailure.Diagnostic.Attempts == OntologySemanticAssistedProposalClient.MaxAttempts
                        && beforeMalformedResponse == afterMalformedResponse,
                    "malformed Codex CLI output must use the existing bounded retry and leave no partial generation write");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<OntologySemanticAssistedProposalException> CaptureFailureAsync(
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OntologySemanticAssistedProposalException ex)
        {
            return ex;
        }
        throw new InvalidOperationException("Expected an assisted proposal failure.");
    }

    private static async Task<bool> ThrowsInvalidOperationAsync(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task<bool> ThrowsArgumentAsync(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool ThrowsArgument(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static BusinessOntologyGenerationInput BaseGeneration() => new(
        OntologyId,
        "base",
        "base-fingerprint",
        "fixture/1",
        "2026-07-18T11:59:00Z",
        [
            new BusinessOntologyConcept(Record, "businessObject", "记录", "记录业务概念。", "accepted", 0.95, ["base:record"]),
            new BusinessOntologyConcept(Supplier, "businessObject", "供应商", "供应商业务概念。", "accepted", 0.95, ["base:supplier"]),
        ],
        [],
        [],
        [],
        [],
        [],
        [],
        [
            new BusinessOntologyMapping(
                OntologyId + ".Mapping.Record",
                "concept",
                Record,
                "representedBy",
                "repo:record",
                "java",
                "class",
                "symbol:record",
                "src/RecordEntity.java",
                "treesitter",
                0.95,
                "accepted",
                ["base:record"]),
            new BusinessOntologyMapping(
                OntologyId + ".Mapping.Supplier",
                "concept",
                Supplier,
                "representedBy",
                "repo:record",
                "java",
                "class",
                "symbol:supplier",
                "src/SupplierEntity.java",
                "treesitter",
                0.95,
                "accepted",
                ["base:supplier"]),
        ],
        [
            new BusinessOntologyEvidence("base:record", "repo:record", "src/RecordEntity.java", "symbol:record", 1, 3, "contractual", "fixture", 0.95, "code", "记录。"),
            new BusinessOntologyEvidence("base:supplier", "repo:record", "src/SupplierEntity.java", "symbol:supplier", 1, 1, "contractual", "fixture", 0.95, "code", "供应商。"),
        ],
        [],
        [],
        []);

    private sealed class FakeLlmClient(params string[] responses) : ILlmClient
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public int CallCount { get; private set; }
        public string SystemPrompt { get; private set; } = "";
        public string UserPrompt { get; private set; } = "";
        public LlmOptions? LastOptions { get; private set; }

        public Task<LlmCompletion> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            LlmOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            SystemPrompt = systemPrompt;
            UserPrompt = userPrompt;
            LastOptions = options;
            var index = Math.Min(CallCount - 1, responses.Length - 1);
            return Task.FromResult(new LlmCompletion(responses[index], "fake"));
        }
    }

    private sealed class UnavailableLlmClient : ILlmClient
    {
        public bool IsAvailable => false;
        public string? UnavailableReason => "fixture unavailable";

        public Task<LlmCompletion> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            LlmOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unavailable client must never be called.");
    }

    private sealed class SemanticCodexCliFixture : IDisposable
    {
        private SemanticCodexCliFixture(string root, string executablePath, string invocationArgumentsPath)
        {
            Root = root;
            ExecutablePath = executablePath;
            _invocationArgumentsPath = invocationArgumentsPath;
        }

        private string Root { get; }
        private string _invocationArgumentsPath { get; }
        internal string ExecutablePath { get; }

        internal static async Task<SemanticCodexCliFixture> CreateAsync(string response)
        {
            var root = Path.Combine(Path.GetTempPath(), "onto-semantic-codex-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var invocationArgumentsPath = Path.Combine(root, "args.txt");
            var executablePath = Path.Combine(root, OperatingSystem.IsWindows() ? "codex-fixture.cmd" : "codex-fixture");
            await File.WriteAllTextAsync(
                executablePath,
                OperatingSystem.IsWindows()
                    ? WindowsScript(response, invocationArgumentsPath)
                    : UnixScript(response, invocationArgumentsPath));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    executablePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return new SemanticCodexCliFixture(root, executablePath, invocationArgumentsPath);
        }

        internal async Task<string[]> ReadInvocationArgumentsAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (!File.Exists(_invocationArgumentsPath) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            if (!File.Exists(_invocationArgumentsPath))
            {
                throw new InvalidOperationException("semantic Codex CLI fixture was not invoked");
            }

            return await File.ReadAllLinesAsync(_invocationArgumentsPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string UnixScript(string response, string invocationArgumentsPath)
        {
            var delimiter = "COZO_SEMANTIC_RESPONSE_" + Guid.NewGuid().ToString("N");
            return $"""
                #!/usr/bin/env bash
                set -eu
                printf '%s\n' "$@" > {BashQuote(invocationArgumentsPath)}
                output=""
                while [[ "$#" -gt 0 ]]; do
                  if [[ "$1" == "--output-last-message" ]]; then
                    output="$2"
                    shift 2
                    continue
                  fi
                  shift
                done
                test -n "$output"
                cat > "$output" <<'{delimiter}'
                {response}
                {delimiter}
                """;
        }

        private static string WindowsScript(string response, string invocationArgumentsPath)
        {
            var escaped = response
                .Replace("^", "^^", StringComparison.Ordinal)
                .Replace("&", "^&", StringComparison.Ordinal)
                .Replace("|", "^|", StringComparison.Ordinal)
                .Replace("<", "^<", StringComparison.Ordinal)
                .Replace(">", "^>", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
            return $"""
                @echo off
                setlocal EnableExtensions DisableDelayedExpansion
                set "args={invocationArgumentsPath}"
                type nul > "%args%"
                set "output="
                :args
                if "%~1"=="" goto done
                echo(%~1>> "%args%"
                if "%~1"=="--output-last-message" set "output=%~2"
                shift
                goto args
                :done
                if "%output%"=="" exit /b 99
                > "%output%" <nul set /p "={escaped}"
                exit /b 0
                """;
        }

        private static string BashQuote(string value) =>
            "'" + value.Replace("'", "'\\\"'\\\"'", StringComparison.Ordinal) + "'";
    }
}
