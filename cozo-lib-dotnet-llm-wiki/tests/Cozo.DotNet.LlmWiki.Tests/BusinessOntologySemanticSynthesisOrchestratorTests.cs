using System.Text.Json;
using System.Text.Json.Nodes;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.LlmClient;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologySemanticSynthesisOrchestratorTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        await OrchestratesWithDistinctCriticAndPendingOnlyOutputAsync(assert);
        await PublishesPendingArtifactsAtomicallyAsync(assert);
        await PublishesTrustedQualityGateVerdictsAsync(assert);
        await RejectsMalformedDomainDraftAndContinuesOtherDomainsAsync(assert);
        await RejectsIncompatibleOrUnconnectedBindingsBeforeCriticAsync(assert);
        await RoutesEveryCriticVerdictInMemoryAsync(assert);
        await FailsClosedForInvalidCriticVerdictsAsync(assert);
        await EnforcesCapsAndReusesControlledCacheAsync(assert);
        await ReportsUnavailableProvidersWithoutPublishingAsync(assert);
        await DispatchesTheBoundedCliToolAsync(assert);
    }

    private static async Task OrchestratesWithDistinctCriticAndPendingOnlyOutputAsync(Action<bool, string> assert)
    {
        var investigation = new FakeInvestigationOperations("Assets", "Orders");
        var modeler = new FakeLlmClient("modeler");
        var critic = new FakeLlmClient("critic");
        var orchestrator = CreateOrchestrator(investigation, modeler, critic);

        var result = await orchestrator.RunAsync(new BusinessOntologySemanticSynthesisRequest(["asset", "order"]));

        assert(result.Status == BusinessOntologySemanticSynthesisStatuses.Completed
                && result.PendingEnvelope is not null
                && result.PendingEnvelope.DomainCount == 2
                && result.PendingEnvelope.AcceptedOntologyMutationCount == 0
                && result.PendingEnvelope.StagedDrafts.Count == 2
                && result.PendingEnvelope.StagedDrafts.All(draft => draft.Concepts.Count == 2
                    && draft.Concepts.All(concept => concept.ImplementationAnchorCount >= 2
                        && HasCjk(concept.NameZh) && HasCjk(concept.DescriptionZh))
                    && draft.Attributes.Count == 1 && draft.Relations.Count == 1
                    && draft.Rules.Count == 1 && draft.Lifecycles.Count == 1)
                && result.PhaseTrace.SequenceEqual([
                    "discover:completed",
                    "explore:completed",
                    "synthesize:completed",
                    "critic:completed",
                    "publish:pending",
                ], StringComparer.Ordinal)
                && modeler.CallCount == 4
                && critic.CallCount == 1
                && CriticReceivesStructuredChineseDraft(critic.LastUserPrompt)
                && !critic.LastUserPrompt.Contains("evidence:", StringComparison.Ordinal)
                && !critic.LastUserPrompt.Contains("src/", StringComparison.Ordinal)
                && !critic.LastUserPrompt.Contains("SymbolId", StringComparison.Ordinal)
                && RuntimeEvidenceSourceKind(SynthesisPromptFor(modeler, "evidence:assets:typed"), "evidence:assets:typed") == "typed_reference"
                && RuntimeEvidenceSourceKind(SynthesisPromptFor(modeler, "evidence:assets:guard"), "evidence:assets:guard") == "business_guard"
                && RuntimeEvidenceSourceKind(SynthesisPromptFor(modeler, "evidence:assets:state"), "evidence:assets:state") == "state_assignment"
                && !ReferenceEquals(orchestrator.Modeler.Client, orchestrator.Critic.Client)
                && !ReferenceEquals(orchestrator.Modeler.Configuration, orchestrator.Critic.Configuration)
                && orchestrator.Modeler.Identity != orchestrator.Critic.Identity,
            "v3 should carry Chinese, multi-anchor typed drafts only in memory, expose only critic-safe staging, and keep ontology mutation count at zero");
    }

    private static async Task PublishesPendingArtifactsAtomicallyAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "cozo-semantic-artifacts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var orchestrator = CreateOrchestrator(new FakeInvestigationOperations("Assets"), new FakeLlmClient("modeler"), new FakeLlmClient("critic"));
            var publisher = new BusinessOntologySemanticArtifactPublisher(root);
            var result = await orchestrator.RunAsync(new BusinessOntologySemanticSynthesisRequest(["asset"]), publicationObserver: publisher);
            var run = Directory.GetDirectories(root).Single();
            var files = Directory.GetFiles(run).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var pendingXml = await File.ReadAllTextAsync(Path.Combine(run, "semantic-candidate.xml"));
            var provenance = await ReadPublicationProvenanceAsync(run);
            var rejectedOverwrite = await ThrowsAsync<InvalidOperationException>(() =>
                orchestrator.RunAsync(new BusinessOntologySemanticSynthesisRequest(["asset"]), publicationObserver: publisher));

            assert(result.PendingEnvelope is not null
                    && result.PendingEnvelope.AcceptedOntologyMutationCount == 0
                    && files.SequenceEqual([
                        "domain-charters.json", "provenance.json", "quality-report.json", "review-packet.json", "semantic-candidate.xml",
                    ], StringComparer.Ordinal)
                    && pendingXml.Contains("status=\"pending\"", StringComparison.Ordinal)
                    && pendingXml.Contains("<relation", StringComparison.Ordinal)
                    && pendingXml.Contains("<rule", StringComparison.Ordinal)
                    && pendingXml.Contains("<lifecycle", StringComparison.Ordinal)
                    && provenance.ModelCallPhases.SequenceEqual(["critic", "explore-plan", "synthesis-draft"], StringComparer.Ordinal)
                    && provenance.QueryDigests.SequenceEqual(["discover:asset", "state:Assets", "use-case"], StringComparer.Ordinal)
                    && provenance.Budget == (6, 4, 10)
                    && rejectedOverwrite,
                "pending artifact publication must atomically emit replayable model/query/budget provenance, remain non-promoting, and refuse overwrite");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task PublishesTrustedQualityGateVerdictsAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "cozo-semantic-quality-" + Guid.NewGuid().ToString("N"));
        try
        {
            var passingEvaluator = new BusinessOntologySemanticPublicationQualityEvaluator(BaselineSnapshot([
                ("Projection.Unrelated", ["symbol:Unrelated"]),
            ]));
            var passingOrchestrator = CreateOrchestrator(
                new FakeInvestigationOperations("Assets"), new FakeLlmClient("modeler"), new FakeLlmClient("critic"),
                passingEvaluator.SourceFingerprint);
            _ = await passingOrchestrator.RunAsync(
                new BusinessOntologySemanticSynthesisRequest(["asset"]),
                publicationObserver: new BusinessOntologySemanticArtifactPublisher(Path.Combine(root, "pass"), passingEvaluator));
            var passedReport = await ReadQualityReportAsync(Directory.GetDirectories(Path.Combine(root, "pass")).Single());

            var projectionEvaluator = new BusinessOntologySemanticPublicationQualityEvaluator(BaselineSnapshot([
                ("Projection.AssetRecord", ["symbol:AssetsService", "symbol:AssetsState"]),
                ("Projection.Custodian", ["symbol:Assets", "symbol:AssetsService"]),
            ]));
            var projectionOrchestrator = CreateOrchestrator(
                new FakeInvestigationOperations("Assets"), new FakeLlmClient("modeler"), new FakeLlmClient("critic"),
                projectionEvaluator.SourceFingerprint);
            _ = await projectionOrchestrator.RunAsync(
                new BusinessOntologySemanticSynthesisRequest(["asset"]),
                publicationObserver: new BusinessOntologySemanticArtifactPublisher(Path.Combine(root, "projection"), projectionEvaluator));
            var failedReport = await ReadQualityReportAsync(Directory.GetDirectories(Path.Combine(root, "projection")).Single());

            var noPartialRoot = Path.Combine(root, "no-partial");
            var evaluationFailed = await ThrowsAsync<InvalidOperationException>(() =>
                CreateOrchestrator(new FakeInvestigationOperations("Assets"), new FakeLlmClient("modeler"), new FakeLlmClient("critic"))
                    .RunAsync(new BusinessOntologySemanticSynthesisRequest(["asset"]), publicationObserver:
                        new BusinessOntologySemanticArtifactPublisher(noPartialRoot, new ThrowingQualityEvaluator())));

            assert(passedReport.Status == BusinessOntologySemanticQualityGate.Passed
                    && failedReport.Status == BusinessOntologySemanticQualityGate.Failed
                    && failedReport.Diagnostics.Contains("candidate_set_matches_projection_baseline", StringComparer.Ordinal)
                    && evaluationFailed
                    && !Directory.Exists(noPartialRoot),
                "artifact publication must record a trusted quality-gate pass or projection failure and leave no partial directory when quality evaluation fails");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RejectsIncompatibleOrUnconnectedBindingsBeforeCriticAsync(Action<bool, string> assert)
    {
        var investigation = new FakeInvestigationOperations("Assets", "Orders", "Billing");
        var modeler = new FakeLlmClient("modeler")
        {
            ResponseFor = prompt => prompt.Contains("\"domain\":\"Assets\"", StringComparison.Ordinal)
                ? FakeLlmClient.WithBindingEvidence(prompt, "typed-reference", "evidence:assets:state")
                : prompt.Contains("\"domain\":\"Billing\"", StringComparison.Ordinal)
                    ? FakeLlmClient.WithBindingEvidence(prompt, "typed-reference", "evidence:billing:unrelated")
                    : FakeLlmClient.ValidSynthesisResponse(prompt),
        };
        var critic = new FakeLlmClient("critic");
        var result = await CreateOrchestrator(investigation, modeler, critic)
            .RunAsync(new BusinessOntologySemanticSynthesisRequest(["asset", "order", "billing"]));

        assert(result.Status == BusinessOntologySemanticSynthesisStatuses.CompletedWithFailures
                && result.PendingEnvelope is not null
                && result.PendingEnvelope.DomainCount == 1
                && result.DomainStatuses.Count(item => item.Status == "failed") == 2
                && CriticDraftDomains(critic.LastUserPrompt).SequenceEqual(["Orders"], StringComparer.Ordinal),
            "incompatible source kinds and bindings disconnected from candidate anchors must fail their domains before critic receives a staged draft");
    }

    private static async Task RejectsMalformedDomainDraftAndContinuesOtherDomainsAsync(Action<bool, string> assert)
    {
        var investigation = new FakeInvestigationOperations("Assets", "Orders");
        var modeler = new FakeLlmClient("modeler")
        {
            ResponseFor = prompt => prompt.Contains("\"domain\":\"Assets\"", StringComparison.Ordinal)
                ? "[]" : FakeLlmClient.ValidSynthesisResponse(prompt),
        };
        var critic = new FakeLlmClient("critic");
        var result = await CreateOrchestrator(investigation, modeler, critic)
            .RunAsync(new BusinessOntologySemanticSynthesisRequest(["asset", "order"]));

        assert(result.Status == BusinessOntologySemanticSynthesisStatuses.CompletedWithFailures
                && result.PendingEnvelope is not null
                && result.PendingEnvelope.DomainCount == 1
                && result.PhaseTrace.SequenceEqual([
                    "discover:completed",
                    "explore:completed",
                    "synthesize:completed_with_failures",
                    "critic:completed",
                    "publish:pending",
                ], StringComparer.Ordinal)
                && result.DomainStatuses.OrderBy(item => item.DomainId, StringComparer.Ordinal)
                    .Select(item => item.Status)
                    .SequenceEqual(["failed", "completed"], StringComparer.Ordinal)
                && CriticDraftDomains(critic.LastUserPrompt).SequenceEqual(["Orders"], StringComparer.Ordinal)
                && !critic.LastUserPrompt.Contains("evidence:", StringComparison.Ordinal),
            "a malformed structured draft must fail only its domain and not prevent an independent evidence-closed domain from reaching critic staging");
    }

    private static async Task RoutesEveryCriticVerdictInMemoryAsync(Action<bool, string> assert)
    {
        var critic = new FakeLlmClient("critic")
        {
            ResponseFor = prompt => prompt.Contains("\"phase\":\"critic\"", StringComparison.Ordinal)
                ? CriticResponse(prompt, candidates => candidates.Select((candidate, index) =>
                {
                    var id = candidate.GetProperty("Id").GetString()!;
                    var kind = candidate.GetProperty("Kind").GetString()!;
                    var concepts = candidates.Where(item => item.GetProperty("Kind").GetString() == "concept").ToArray();
                    var decision = kind switch
                    {
                        "concept" when index == 0 => "keep",
                        "concept" => "merge",
                        "attribute" => "drop",
                        "relation" => "defer",
                        "rule" => "request_evidence",
                        _ => "drop",
                    };
                    return new
                    {
                        candidateId = id,
                        decision,
                        rationaleZh = "该候选已经完成独立审查。",
                        supportRefs = new[] { candidate.GetProperty("supportRefs")[0].GetString()! },
                        targetCandidateId = decision == "merge" ? concepts[0].GetProperty("Id").GetString() : null,
                        requestZh = decision == "request_evidence" ? "请补充跨层业务证据。" : null,
                    };
                }))
                : FakeLlmClient.ValidSynthesisResponse(prompt),
        };
        var result = await CreateOrchestrator(new FakeInvestigationOperations("Assets"), new FakeLlmClient("modeler"), critic)
            .RunAsync(new BusinessOntologySemanticSynthesisRequest(["asset"]));

        var routing = result.PendingEnvelope?.CriticRouting;
        assert(result.Status == BusinessOntologySemanticSynthesisStatuses.Completed
                && routing is not null
                && routing.PendingCandidateIds.Count == 1
                && routing.ReviewCandidateIds.Count == 3
                && routing.DiagnosisCandidateIds.Count == 2
                && routing.Verdicts.Count == 6
                && result.PendingEnvelope!.StagedDrafts.Single().Concepts.Count == 1
                && result.PendingEnvelope.StagedDrafts.Single().Attributes.Count == 0
                && result.PendingEnvelope.AcceptedOntologyMutationCount == 0
                && !critic.LastUserPrompt.Contains("evidence:", StringComparison.Ordinal)
                && !critic.LastUserPrompt.Contains("src/", StringComparison.Ordinal)
                && routing.Provenance.SnapshotDigest.StartsWith("sha256:", StringComparison.Ordinal),
            "critic routing must preserve immutable redacted provenance, keep only keep candidates pending, and hold merge/defer/request/drop outcomes in memory without mutation");
    }

    private static async Task FailsClosedForInvalidCriticVerdictsAsync(Action<bool, string> assert)
    {
        var malformed = await RunWithCriticResponseAsync(_ => "[]");
        var unknown = await RunWithCriticResponseAsync(prompt => CriticResponse(prompt, candidates => candidates.Select(candidate => new
        {
            candidateId = candidate.GetProperty("Id").GetString() == candidates[0].GetProperty("Id").GetString() ? "invented.candidate" : candidate.GetProperty("Id").GetString(),
            decision = "keep",
            rationaleZh = "独立审查通过。",
            supportRefs = new[] { candidate.GetProperty("supportRefs")[0].GetString()! },
            targetCandidateId = (string?)null,
            requestZh = (string?)null,
        })));
        var invalidMerge = await RunWithCriticResponseAsync(prompt => CriticResponse(prompt, candidates => candidates.Select(candidate => new
        {
            candidateId = candidate.GetProperty("Id").GetString(),
            decision = candidate.GetProperty("Kind").GetString() == "concept" ? "merge" : "keep",
            rationaleZh = "独立审查建议合并。",
            supportRefs = new[] { candidate.GetProperty("supportRefs")[0].GetString()! },
            targetCandidateId = candidate.GetProperty("Kind").GetString() == "concept" ? candidate.GetProperty("Id").GetString() : null,
            requestZh = (string?)null,
        })));

        assert(new[] { malformed, unknown, invalidMerge }.All(result => result.Status == BusinessOntologySemanticSynthesisStatuses.CompletedWithFailures
                && result.PendingEnvelope is null
                && result.PhaseTrace.Contains("critic:failed", StringComparer.Ordinal)
                && result.PhaseTrace.Contains("publish:not_published", StringComparer.Ordinal)),
            "malformed verdicts, unsupported candidate ids, and invalid merge targets must fail closed before any pending publication");
    }

    private static async Task<BusinessOntologySemanticSynthesisResult> RunWithCriticResponseAsync(Func<string, string> response) =>
        await CreateOrchestrator(
                new FakeInvestigationOperations("Assets"),
                new FakeLlmClient("modeler"),
                new FakeLlmClient("critic")
                {
                    ResponseFor = prompt => prompt.Contains("\"phase\":\"critic\"", StringComparison.Ordinal)
                        ? response(prompt)
                        : FakeLlmClient.ValidSynthesisResponse(prompt),
                })
            .RunAsync(new BusinessOntologySemanticSynthesisRequest(["asset"]));

    private static async Task EnforcesCapsAndReusesControlledCacheAsync(Action<bool, string> assert)
    {
        var investigation = new FakeInvestigationOperations("Assets");
        var modeler = new FakeLlmClient("modeler");
        var critic = new FakeLlmClient("critic");
        var orchestrator = CreateOrchestrator(investigation, modeler, critic);

        _ = await orchestrator.RunAsync(new BusinessOntologySemanticSynthesisRequest(["asset"]));
        var callsAfterFirstRun = modeler.CallCount;
        var replay = await orchestrator.RunAsync(new BusinessOntologySemanticSynthesisRequest(["asset"]));
        var tooManyTerms = await ThrowsAsync<ArgumentException>(() => orchestrator.RunAsync(
            new BusinessOntologySemanticSynthesisRequest(["a", "b", "c", "d", "e", "f", "g"])));

        assert(replay.DomainStatuses.Single().FromCache
                && modeler.CallCount == callsAfterFirstRun
                && critic.CallCount == 2
                && tooManyTerms,
            "v3 must preserve T2.3 cache behavior and reject caller attempts to exceed the six-domain cap before effects");
    }

    private static async Task ReportsUnavailableProvidersWithoutPublishingAsync(Action<bool, string> assert)
    {
        var unavailable = new FakeLlmClient("unavailable") { IsAvailableOverride = false };
        var result = await CreateOrchestrator(
                new FakeInvestigationOperations("Assets"),
                unavailable,
                new FakeLlmClient("critic"))
            .RunAsync(new BusinessOntologySemanticSynthesisRequest(["asset"]));

        assert(result.Status == BusinessOntologySemanticSynthesisStatuses.Blocked
                && result.PendingEnvelope is null
                && result.PhaseTrace.SequenceEqual([
                    "discover:skipped",
                    "explore:skipped",
                    "synthesize:skipped",
                    "critic:skipped",
                    "publish:not_published",
                ], StringComparer.Ordinal),
            "unavailable global providers must produce a redacted blocked result with no domain effects or publication");
    }

    private static async Task DispatchesTheBoundedCliToolAsync(Action<bool, string> assert)
    {
        var tool = LlmWikiToolRunner.ToolsJson()
            .Single(item => item?["name"]?.GetValue<string>() == "run_business_semantic_synthesis")!;
        var publicationTool = LlmWikiToolRunner.ToolsJson()
            .Single(item => item?["name"]?.GetValue<string>() == "publish_business_semantic_synthesis")!;
        var schema = tool["inputSchema"]!["properties"]!.AsObject();
        var publicationSchema = publicationTool["inputSchema"]!["properties"]!.AsObject();
        var forbidden = new[] { "prompt", "sql", "path", "repoPath", "provider", "apiKey", "parametersJson" };
        using var db = new CozoDb("mem", "");
        var runner = new LlmWikiToolRunner(new CozoOm(db));
        JsonObject result;
        using (new LlmEnvironmentOverride())
        {
            result = JsonSerializer.SerializeToNode(await runner.CallAsync(
                "run_business_semantic_synthesis",
                new JsonObject { ["domainTerms"] = new JsonArray("asset") }), LlmWikiJson.Options)!.AsObject();
        }
        var rejectedNonLiteralTerms = await ThrowsAsync<ArgumentException>(() => runner.CallAsync(
            "run_business_semantic_synthesis",
            new JsonObject { ["domainTerms"] = new JsonArray(1) }));
        var rejectedTooManyTermsWithoutProvider = await ThrowsAsync<ArgumentException>(() => runner.CallAsync(
            "run_business_semantic_synthesis",
            new JsonObject { ["domainTerms"] = new JsonArray("a", "b", "c", "d", "e", "f", "g") }));
        var rejectedUnconfiguredPublication = await ThrowsAsync<InvalidOperationException>(() => runner.CallAsync(
            "publish_business_semantic_synthesis",
            new JsonObject { ["domainTerms"] = new JsonArray("asset") }));

        assert(schema.Count == 1
                && schema.ContainsKey("domainTerms")
                && publicationSchema.Count == 1
                && publicationSchema.ContainsKey("domainTerms")
                && forbidden.All(name => !schema.ContainsKey(name))
                && forbidden.All(name => !publicationSchema.ContainsKey(name))
                && rejectedNonLiteralTerms
                && rejectedTooManyTermsWithoutProvider
                && rejectedUnconfiguredPublication
                && result["schemaVersion"]!.GetValue<string>() == "business-ontology-semantic-synthesis-summary-v1"
                && result["operation"]!.GetValue<string>() == "run_business_semantic_synthesis"
                && result["status"]!.GetValue<string>() == BusinessOntologySemanticSynthesisStatuses.Blocked
                && result["publication"]!["pending"]!.GetValue<bool>() == false
                && !result.ToJsonString().Contains("asset", StringComparison.OrdinalIgnoreCase),
            "the new CLI tool should dispatch through the unavailable-safe path with literal-only bounded input and a redacted public result");
    }

    private static BusinessOntologySemanticSynthesisOrchestrator CreateOrchestrator(
        FakeInvestigationOperations investigation,
        FakeLlmClient modeler,
        FakeLlmClient critic,
        string? sourceFingerprint = null) =>
        new(
            investigation,
            new BusinessOntologySemanticLlmDependency(
                "modeler",
                modeler,
                new LlmClientConfig(Provider: "fixture", Model: "modeler")),
            new BusinessOntologySemanticLlmDependency(
                "critic",
                critic,
                new LlmClientConfig(Provider: "fixture", Model: "critic")),
            sourceFingerprint);

    private static BusinessOntologySnapshot BaselineSnapshot(
        IReadOnlyList<(string ConceptId, IReadOnlyList<string> Symbols)> carriers) => new(
            "Projection.Ontology",
            "v2-symbol-projection",
            carriers.Select(item => new BusinessOntologyConcept(item.ConceptId, "concept", item.ConceptId, "deterministic projection", "hypothesis", 1, [])).ToArray(),
            [], [], [], [], [], [],
            carriers.SelectMany(item => item.Symbols.Select((symbol, index) => new BusinessOntologyMapping(
                $"mapping:{item.ConceptId}:{index}", "concept", item.ConceptId, "representedBy", "fixture", "fixture", "symbol", symbol,
                $"src/{index}.fixture", "fixture", 1, "hypothesis", []))).ToArray(),
            [], [], [], [], []);

    private static async Task<(string Status, IReadOnlyList<string> Diagnostics)> ReadQualityReportAsync(string runDirectory)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDirectory, "quality-report.json")));
        return (
            document.RootElement.GetProperty("status").GetString()!,
            document.RootElement.GetProperty("diagnostics").EnumerateArray()
                .Select(item => item.GetProperty("Code").GetString()!).ToArray());
    }

    private static async Task<(IReadOnlyList<string> ModelCallPhases, IReadOnlyList<string> QueryDigests, (int, int, int) Budget)> ReadPublicationProvenanceAsync(string runDirectory)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDirectory, "provenance.json")));
        var root = document.RootElement;
        var budget = root.GetProperty("budget");
        return (
            root.GetProperty("modelCalls").EnumerateArray().Select(item => item.GetProperty("Phase").GetString()!).ToArray(),
            root.GetProperty("queryDigests").EnumerateArray().Select(item => item.GetString()!).ToArray(),
            (budget.GetProperty("MaxDomains").GetInt32(), budget.GetProperty("MaxCompletionsPerDomain").GetInt32(), budget.GetProperty("MaxInvestigationOperationsPerDomain").GetInt32()));
    }

    private sealed class ThrowingQualityEvaluator : IBusinessOntologySemanticPublicationQualityEvaluator
    {
        public string? SourceFingerprint => null;

        public Task<BusinessOntologySemanticQualityReport> EvaluateAsync(
            BusinessOntologySemanticPendingPublication publication,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("fixture quality evaluation failure");
    }

    private static async Task<bool> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static bool HasCjk(string value) => value.Any(character => character is >= '\u4e00' and <= '\u9fff');

    private static bool CriticReceivesStructuredChineseDraft(string prompt)
    {
        using var document = JsonDocument.Parse(prompt);
        return document.RootElement.GetProperty("stagedDrafts").EnumerateArray()
            .SelectMany(draft => draft.GetProperty("Concepts").EnumerateArray())
            .Any(concept => HasCjk(concept.GetProperty("NameZh").GetString() ?? ""));
    }

    private static IReadOnlyList<string> CriticDraftDomains(string prompt)
    {
        using var document = JsonDocument.Parse(prompt);
        return document.RootElement.GetProperty("stagedDrafts").EnumerateArray()
            .Select(draft => draft.GetProperty("DomainId").GetString()!)
            .ToArray();
    }

    private static string? RuntimeEvidenceSourceKind(string prompt, string evidenceId)
    {
        using var document = JsonDocument.Parse(prompt);
        return document.RootElement.GetProperty("runtimeEvidence").EnumerateArray()
            .Single(item => item.GetProperty("Id").GetString() == evidenceId)
            .GetProperty("SourceKind").GetString();
    }

    private static string SynthesisPromptFor(FakeLlmClient client, string evidenceId) =>
        client.UserPrompts.Single(prompt => prompt.Contains("\"phase\":\"synthesize\"", StringComparison.Ordinal)
            && prompt.Contains(evidenceId, StringComparison.Ordinal));

    private static string CriticResponse(string prompt, Func<IReadOnlyList<JsonElement>, IEnumerable<object>> verdicts)
    {
        using var document = JsonDocument.Parse(prompt);
        var root = document.RootElement;
        var candidates = root.GetProperty("candidates").EnumerateArray().Select(item => item.Clone()).ToArray();
        return JsonSerializer.Serialize(new
        {
            schemaVersion = BusinessOntologySemanticCriticVerdictRouter.SchemaVersion,
            snapshotDigest = root.GetProperty("snapshotDigest").GetString(),
            verdicts = verdicts(candidates).ToArray(),
        });
    }

    private sealed class FakeLlmClient(string name) : ILlmClient
    {
        public int CallCount { get; private set; }
        public string LastUserPrompt { get; private set; } = "";
        public List<string> UserPrompts { get; } = [];
        public Func<string, bool>? ThrowWhen { get; init; }
        public Func<string, string>? ResponseFor { get; init; }
        public bool? IsAvailableOverride { get; init; }
        public bool IsAvailable => IsAvailableOverride ?? true;
        public string? UnavailableReason => IsAvailable ? null : "fixture unavailable";

        public Task<LlmCompletion> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            LlmOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastUserPrompt = userPrompt;
            UserPrompts.Add(userPrompt);
            if (ThrowWhen?.Invoke(userPrompt) == true)
            {
                throw new LlmException(name + " failure");
            }
            return Task.FromResult(new LlmCompletion(ResponseFor?.Invoke(userPrompt) ?? ValidSynthesisResponse(userPrompt), name));
        }

        public static string ValidSynthesisResponse(string prompt)
        {
            if (prompt.Contains("\"phase\":\"critic\"", StringComparison.Ordinal))
            {
                return CriticResponse(prompt, candidates => candidates.Select(candidate => new
                {
                    candidateId = candidate.GetProperty("Id").GetString(),
                    decision = "keep",
                    rationaleZh = "独立审查确认该候选的业务语义和证据闭包一致。",
                    supportRefs = new[] { candidate.GetProperty("supportRefs")[0].GetString()! },
                    targetCandidateId = (string?)null,
                    requestZh = (string?)null,
                }));
            }
            if (!prompt.Contains("\"phase\":\"synthesize\"", StringComparison.Ordinal))
            {
                return "{\"status\":\"ok\"}";
            }
            using var document = JsonDocument.Parse(prompt);
            var root = document.RootElement;
            var domain = root.GetProperty("domain").GetString()!;
            var charter = JsonSerializer.Deserialize<BusinessOntologySemanticDomainCharter>(
                root.GetProperty("domainCharters")[0].GetRawText())!;
            var evidence = JsonSerializer.Deserialize<BusinessOntologySemanticEvidence[]>(
                root.GetProperty("runtimeEvidence").GetRawText())!;
            var context = evidence.Single(item => item.SourceKind == "domain-charter");
            var typed = evidence.Single(item => item.Id.EndsWith(":typed", StringComparison.Ordinal));
            var guard = evidence.Single(item => item.Id.EndsWith(":guard", StringComparison.Ordinal));
            var state = evidence.Single(item => item.Id.EndsWith(":state", StringComparison.Ordinal));
            return JsonSerializer.Serialize(new
            {
                schemaVersion = BusinessOntologySemanticPendingDraftValidator.SchemaVersion,
                domainId = domain,
                domainCharters = new[] { charter },
                clusters = new[]
                {
                    new BusinessOntologySemanticCluster("cluster:" + domain + ":record", domain, domain + ".AssetRecord", "资产记录", "用于登记和追踪业务资产的记录。", [
                        new BusinessOntologySemanticImplementationAnchor(typed.SymbolId, "service", typed.RelativePath, typed.Id),
                        new BusinessOntologySemanticImplementationAnchor(state.SymbolId, "state", state.RelativePath, state.Id),
                    ]),
                    new BusinessOntologySemanticCluster("cluster:" + domain + ":custodian", domain, domain + ".Custodian", "资产保管人", "负责接收和保管业务资产的责任人。", [
                        new BusinessOntologySemanticImplementationAnchor(typed.SymbolId, "service", typed.RelativePath, typed.Id),
                        new BusinessOntologySemanticImplementationAnchor(context.SymbolId, "controller", context.RelativePath, context.Id),
                    ]),
                },
                attributes = new[]
                {
                    new BusinessOntologySemanticAttribute(domain + ".AssetRecord.AssetCode", domain + ".AssetRecord", "资产编号", "string", "用于唯一识别资产记录的业务编号。", [typed.Id]),
                },
                relations = new[]
                {
                    new BusinessOntologySemanticRelation(domain + ".Relation.CustodianKeepsAsset", domain + ".Custodian", domain + ".AssetRecord", "保管", "资产保管人负责保管资产记录。", [
                        new BusinessOntologySemanticClaimEvidenceBinding("typed-reference", "类型引用体现保管关系。", typed.Id),
                    ]),
                },
                rules = new[]
                {
                    new BusinessOntologySemanticRule(domain + ".Rule.AssetCodeRequired", domain + ".AssetRecord", "登记资产时必须提供资产编号。", [
                        new BusinessOntologySemanticClaimEvidenceBinding("validation-branch", "校验分支要求资产编号不能为空。", guard.Id),
                    ]),
                },
                lifecycles = new[]
                {
                    new BusinessOntologySemanticLifecycle(domain + ".Lifecycle.AssetStatus", domain + ".AssetRecord", "资产状态", "资产记录会从待登记变更为在用状态。", [
                        new BusinessOntologySemanticClaimEvidenceBinding("state-update", "状态赋值记录资产进入在用状态。", state.Id),
                    ]),
                },
            });
        }

        public static string WithBindingEvidence(string prompt, string bindingType, string evidenceId)
        {
            var node = JsonNode.Parse(ValidSynthesisResponse(prompt))!.AsObject();
            var relation = node["relations"]!.AsArray().Single()!.AsObject();
            var binding = relation["EvidenceBindings"]!.AsArray()
                .Single(item => item! ["BindingType"]!.GetValue<string>() == bindingType)!.AsObject();
            binding["EvidenceId"] = evidenceId;
            return node.ToJsonString();
        }
    }

    private sealed class FakeInvestigationOperations(params string[] domains) : IBusinessOntologyInvestigationOperations
    {
        public Task<BusinessOntologyInvestigationPage<BusinessOntologyDiscoveredDomainCharter>> DiscoverDomainChartersAsync(string term, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default)
        {
            var matches = domains.Where(domain => term.StartsWith(domain[..1], StringComparison.OrdinalIgnoreCase))
                .Select((domain, index) => Charter(domain, index)).ToArray();
            return Task.FromResult(new BusinessOntologyInvestigationPage<BusinessOntologyDiscoveredDomainCharter>(matches, null, false, "discover:" + term));
        }

        public Task<BusinessOntologyInvestigationPage<BusinessOntologyCrossLayerUseCase>> ListCrossLayerUseCasesAsync(string? entrySymbolId = null, string? domainSeed = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default)
        {
            var domain = domainSeed!;
            var typed = Pattern(domain, "typed", "typed_reference", "Service");
            var guard = Pattern(domain, "guard", "business_guard", "Service");
            var unrelated = Pattern(domain, "unrelated", "typed_reference", "Unrelated");
            var claims = new[] { typed, guard, unrelated };
            var item = new BusinessOntologyCrossLayerUseCase("usecase:" + domain, domain, "symbol:" + domain, "route", "登记资产", [], ["service"], claims, claims.SelectMany(item => item.EvidenceRefs).Select(item => item.EvidenceId).ToArray(), claims.SelectMany(item => item.EvidenceRefs).ToArray());
            return Task.FromResult(new BusinessOntologyInvestigationPage<BusinessOntologyCrossLayerUseCase>([item], null, false, "use-case"));
        }
        public Task<BusinessOntologyInvestigationPage<BusinessOntologyStateRuleCluster>> FindStateRuleClustersAsync(string term, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default)
        {
            var state = Pattern(term, "state", "state_assignment", "State");
            var item = new BusinessOntologyStateRuleCluster("state:" + term, term, "symbol:" + term, [], [], [], [state], [], [], [], state.EvidenceRefs);
            return Task.FromResult(new BusinessOntologyInvestigationPage<BusinessOntologyStateRuleCluster>([item], null, false, "state:" + term));
        }
        public Task<BusinessOntologyInvestigationPage<BusinessOntologyImplementationCluster>> FindImplementationClustersAsync(string? domainSeed = null, IReadOnlyList<string>? evidenceIds = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BusinessOntologyInvestigationPage<BusinessOntologyImplementationCluster>([], null, false, "implementation:" + domainSeed));
        public Task<BusinessOntologyInvestigationOverview> GetOverviewAsync(string? ontologyId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BusinessOntologyInvestigationPage<BusinessTermHit>> FindBusinessTermsAsync(string term, string? ontologyId = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern>> FindSemanticPatternsAsync(string kind, string? term = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SemanticEvidencePack> GetSemanticEvidenceAsync(IReadOnlyList<string> evidenceIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BusinessOntologyInvestigationPage<BusinessUseCaseSlice>> ListUseCaseSlicesAsync(string ontologyId, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BusinessUseCaseSlice> GetUseCaseSliceAsync(string ontologyId, string sliceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BusinessOntologySubjectInspection> InspectOntologySubjectAsync(string ontologyId, string subjectKind, string subjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private static BusinessOntologyDiscoveredDomainCharter Charter(string domain, int index)
        {
            var evidence = "evidence:" + domain.ToLowerInvariant();
            var reference = new BusinessOntologyInvestigationEvidenceRef(evidence, "repo", "src/" + domain + ".cs", "symbol:" + domain, 1, 1);
            return new BusinessOntologyDiscoveredDomainCharter(
                "charter:" + domain,
                domain,
                [],
                new BusinessOntologyInvestigationWorkflow("symbol:" + domain, "route", "GET /" + domain.ToLowerInvariant(), [], [reference]),
                [],
                [evidence],
                [reference]);
        }

        private static BusinessOntologySemanticPattern Pattern(string domain, string suffix, string claimKind, string symbolSuffix)
        {
            var evidenceId = "evidence:" + domain.ToLowerInvariant() + ":" + suffix;
            var subjectId = "symbol:" + domain + symbolSuffix;
            var reference = new BusinessOntologyInvestigationEvidenceRef(evidenceId, "repo", "src/" + domain + symbolSuffix + ".cs", subjectId, 1, 1) { SymbolId = subjectId };
            return new BusinessOntologySemanticPattern(evidenceId, claimKind, subjectId, domain + symbolSuffix, "repo", reference.Path, 1, 1, "{}", 0.95, [reference]);
        }
    }
}
