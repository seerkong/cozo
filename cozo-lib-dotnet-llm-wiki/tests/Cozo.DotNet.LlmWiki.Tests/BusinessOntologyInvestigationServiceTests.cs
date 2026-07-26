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
            var stateFieldPayload = JsonSerializer.Serialize(new { property = "state", ownerSymbolId = "symbol:record:service" });
            var stateValuePayload = JsonSerializer.Serialize(new { property = "state", value = "DRAFT", ownerSymbolId = "symbol:record:service" });
            var transitionPayload = JsonSerializer.Serialize(new { property = "state", fromValue = "DRAFT", toValue = "SUBMITTED", ownerSymbolId = "symbol:record:service" });
            var savedStateValuePayload = JsonSerializer.Serialize(new { property = "state", value = "ARCHIVED", ownerSymbolId = "symbol:record:repository" });
            var savedTransitionPayload = JsonSerializer.Serialize(new { property = "state", fromValue = "ARCHIVED", toValue = "DELETED", ownerSymbolId = "symbol:record:repository" });
            var validationPayload = JsonSerializer.Serialize(new { property = "state", rule = "required", ownerSymbolId = "symbol:record:service" });
            var persistencePayload = JsonSerializer.Serialize(new { property = "state", operation = "write", ownerSymbolId = "symbol:record:service" });
            var savedGuardPayload = JsonSerializer.Serialize(new { property = "state", allowedValues = new[] { "ARCHIVED" }, ownerSymbolId = "symbol:record:repository" });
            var savedValidationPayload = JsonSerializer.Serialize(new { property = "state", rule = "immutable", ownerSymbolId = "symbol:record:repository" });
            var savedPersistencePayload = JsonSerializer.Serialize(new { property = "state", operation = "delete", ownerSymbolId = "symbol:record:repository" });
            var pagePayload = JsonSerializer.Serialize(new { route = "/records", ownerSymbolId = "symbol:record:page" });
            await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Repositories:
                [
                    new CodeRepositoryFact("repo:sample", root, "sample"),
                    new CodeRepositoryFact("repo:web", root, "web"),
                ],
                Files:
                [
                    new CodeFileFact("file:controller", "repo:sample", "src/RecordController.java", "java"),
                    new CodeFileFact("file:record", "repo:sample", "src/RecordService.java", "java"),
                    new CodeFileFact("file:repository", "repo:sample", "src/RecordRepository.java", "java"),
                    new CodeFileFact("file:page", "repo:web", "web/RecordPage.ts", "typescript"),
                ],
                Symbols:
                [
                    new CodeSymbolFact("symbol:record:controller", "file:controller", "RecordController", "class", 1, 80, Lang: "java"),
                    new CodeSymbolFact("symbol:record:create", "file:controller", "createRecord", "method", 2, 4, ParentId: "symbol:record:controller", Lang: "java"),
                    new CodeSymbolFact("symbol:record:review", "file:controller", "reviewRecord", "method", 8, 12, ParentId: "symbol:record:controller", Lang: "java"),
                    new CodeSymbolFact("symbol:record:service", "file:record", "RecordService", "class", 1, 80, Lang: "java"),
                    new CodeSymbolFact("symbol:record:submit", "file:record", "submitRecord", "method", 2, 4, ParentId: "symbol:record:service", Lang: "java"),
                    new CodeSymbolFact("symbol:record:repository", "file:repository", "RecordRepository", "interface", 1, 80, Lang: "java"),
                    new CodeSymbolFact("symbol:record:save", "file:repository", "save", "method", 2, 4, ParentId: "symbol:record:repository", Lang: "java"),
                    new CodeSymbolFact("symbol:record:page", "file:page", "RecordPage", "component", 1, 80, Lang: "typescript"),
                ],
                Edges:
                [
                    new CodeEdgeFact("symbol:record:controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "fixture", "controller"),
                    new CodeEdgeFact("symbol:record:service", "spring:role:service", "SPRING_ROLE", "file:record", 1, 0.98, "fixture", "service"),
                    new CodeEdgeFact("symbol:record:repository", "spring:role:repository", "SPRING_ROLE", "file:repository", 1, 0.98, "fixture", "repository"),
                    new CodeEdgeFact("symbol:record:page", "ui:role:page", "SPRING_ROLE", "file:page", 1, 0.98, "fixture", "ui"),
                    new CodeEdgeFact("symbol:record:create", "symbol:record:submit", CodeEdgeKinds.Calls, "file:controller", 3, 1.0, "fixture", "controller calls service"),
                    new CodeEdgeFact("symbol:record:review", "symbol:record:submit", CodeEdgeKinds.Calls, "file:controller", 9, 1.0, "fixture", "controller calls service"),
                    new CodeEdgeFact("symbol:record:submit", "symbol:record:save", CodeEdgeKinds.Calls, "file:record", 3, 1.0, "fixture", "service calls repository"),
                ],
                SemanticClaims:
                [
                    new CodeSemanticClaimFact(claimId, "symbol:record:submit", CodeSemanticClaimKinds.BusinessGuard, payload, "file:record", 3, 3, 0.95, "treesitter", "只有草稿记录可以提交"),
                    new CodeSemanticClaimFact(CodeSemanticClaimIdentity.Create("file:record", CodeSemanticClaimKinds.StateField, stateFieldPayload, 3, 3), "symbol:record:submit", CodeSemanticClaimKinds.StateField, stateFieldPayload, "file:record", 3, 3, 0.95, "treesitter", "state field"),
                    new CodeSemanticClaimFact(CodeSemanticClaimIdentity.Create("file:record", CodeSemanticClaimKinds.StateValue, stateValuePayload, 3, 3), "symbol:record:submit", CodeSemanticClaimKinds.StateValue, stateValuePayload, "file:record", 3, 3, 0.95, "treesitter", "state value"),
                    new CodeSemanticClaimFact(CodeSemanticClaimIdentity.Create("file:record", CodeSemanticClaimKinds.StateAssignment, transitionPayload, 3, 3), "symbol:record:submit", CodeSemanticClaimKinds.StateAssignment, transitionPayload, "file:record", 3, 3, 0.95, "treesitter", "state transition"),
                    new CodeSemanticClaimFact(CodeSemanticClaimIdentity.Create("file:repository", CodeSemanticClaimKinds.StateValue, savedStateValuePayload, 3, 3), "symbol:record:save", CodeSemanticClaimKinds.StateValue, savedStateValuePayload, "file:repository", 3, 3, 0.95, "treesitter", "saved state value"),
                    new CodeSemanticClaimFact(CodeSemanticClaimIdentity.Create("file:repository", CodeSemanticClaimKinds.StateAssignment, savedTransitionPayload, 3, 3), "symbol:record:save", CodeSemanticClaimKinds.StateAssignment, savedTransitionPayload, "file:repository", 3, 3, 0.95, "treesitter", "saved state transition"),
                    new CodeSemanticClaimFact(CodeSemanticClaimIdentity.Create("file:repository", CodeSemanticClaimKinds.BusinessGuard, savedGuardPayload, 3, 3), "symbol:record:save", CodeSemanticClaimKinds.BusinessGuard, savedGuardPayload, "file:repository", 3, 3, 0.95, "treesitter", "saved guard"),
                    new CodeSemanticClaimFact(CodeSemanticClaimIdentity.Create("file:repository", CodeSemanticClaimKinds.ValidationConstraint, savedValidationPayload, 3, 3), "symbol:record:save", CodeSemanticClaimKinds.ValidationConstraint, savedValidationPayload, "file:repository", 3, 3, 0.95, "treesitter", "saved validation"),
                    new CodeSemanticClaimFact(CodeSemanticClaimIdentity.Create("file:repository", CodeSemanticClaimKinds.PersistenceConstraint, savedPersistencePayload, 3, 3), "symbol:record:save", CodeSemanticClaimKinds.PersistenceConstraint, savedPersistencePayload, "file:repository", 3, 3, 0.95, "treesitter", "saved persistence"),
                    new CodeSemanticClaimFact(CodeSemanticClaimIdentity.Create("file:record", CodeSemanticClaimKinds.ValidationConstraint, validationPayload, 3, 3), "symbol:record:submit", CodeSemanticClaimKinds.ValidationConstraint, validationPayload, "file:record", 3, 3, 0.95, "treesitter", "validation"),
                    new CodeSemanticClaimFact(CodeSemanticClaimIdentity.Create("file:record", CodeSemanticClaimKinds.PersistenceConstraint, persistencePayload, 3, 3), "symbol:record:submit", CodeSemanticClaimKinds.PersistenceConstraint, persistencePayload, "file:record", 3, 3, 0.95, "treesitter", "persistence"),
                    new CodeSemanticClaimFact(CodeSemanticClaimIdentity.Create("file:page", CodeSemanticClaimKinds.RouteBinding, pagePayload, 2, 2), "symbol:record:page", CodeSemanticClaimKinds.RouteBinding, pagePayload, "file:page", 2, 2, 0.91, "treesitter", "page route"),
                ],
                EntryPoints:
                [
                    new CodeEntryPointFact("symbol:record:create", "http_route", "POST /records"),
                    new CodeEntryPointFact("symbol:record:review", "http_route", "POST /records/{id}/review"),
                ]));

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

            var overview = await service.GetOverviewAsync(OntologyId);
            assert(overview.GenerationId == "fixture-1" && overview.Counts["ck_semantic_claim"] == 12,
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
            assert(patterns.Items.Any(item => item.ClaimId == claimId && item.EvidenceRefs.Single().Path == "src/RecordService.java"),
                "semantic patterns should retain direct indexed evidence references");

            var charterPage = await service.DiscoverDomainChartersAsync("record", limit: 1);
            var charterNextPage = await service.DiscoverDomainChartersAsync("record", charterPage.NextCursor, 10);
            assert(charterPage.Truncated && charterPage.Items.Single().DomainSeed == "record"
                    && charterPage.Items.Single().Workflow.EvidenceRefs.Count > 0
                    && !charterPage.Items.Select(item => item.Id).Intersect(charterNextPage.Items.Select(item => item.Id), StringComparer.Ordinal).Any(),
                "domain charter discovery should be evidence-linked, deterministic, and paginated");
            var charterCursorRejected = false;
            try { await service.DiscoverDomainChartersAsync("record", "eA==.00"); }
            catch (ArgumentException) { charterCursorRejected = true; }
            assert(charterCursorRejected, "domain charter cursors must be signed and bound to the normalized operation filter");

            var crossLayer = await service.ListCrossLayerUseCasesAsync(entrySymbolId: "symbol:record:create");
            assert(crossLayer.Items.Single().CallPath.Select(item => item.SymbolId).SequenceEqual([
                    "symbol:record:create", "symbol:record:submit", "symbol:record:save"
                ])
                && crossLayer.Items.Single().Roles.Contains("spring:role:service", StringComparer.Ordinal)
                && crossLayer.Items.Single().Roles.Contains("spring:role:repository", StringComparer.Ordinal),
                "cross-layer use cases should retain route-to-call-path-to-role evidence without an ontology id");

            var stateClusters = await service.FindStateRuleClustersAsync("record");
            var submittedCluster = stateClusters.Items.Single(item => item.SubjectId == "symbol:record:submit"
                && item.CallPath.First().SymbolId == "symbol:record:create");
            var savedCluster = stateClusters.Items.Single(item => item.SubjectId == "symbol:record:save"
                && item.CallPath.First().SymbolId == "symbol:record:create");
            assert(submittedCluster.StateFields.Count == 1
                    && submittedCluster.StateValues.Count == 1
                    && submittedCluster.Transitions.Count == 1
                    && submittedCluster.StateValues.All(item => item.SubjectId == "symbol:record:submit")
                    && submittedCluster.Transitions.All(item => item.SubjectId == "symbol:record:submit")
                    && submittedCluster.Guards.Count == 1
                    && submittedCluster.Validations.Count == 1
                    && submittedCluster.Persistences.Count == 1
                    && submittedCluster.Guards.All(item => item.SubjectId == "symbol:record:submit")
                    && submittedCluster.Validations.All(item => item.SubjectId == "symbol:record:submit")
                    && submittedCluster.Persistences.All(item => item.SubjectId == "symbol:record:submit")
                    && submittedCluster.EvidenceRefs.Select(item => item.EvidenceId).Order(StringComparer.Ordinal).SequenceEqual(
                        submittedCluster.StateFields.Concat(submittedCluster.StateValues).Concat(submittedCluster.Transitions)
                            .Concat(submittedCluster.Guards).Concat(submittedCluster.Validations).Concat(submittedCluster.Persistences)
                            .Select(item => item.ClaimId).Order(StringComparer.Ordinal))
                    && savedCluster.Guards.Count == 1
                    && savedCluster.Validations.Count == 1
                    && savedCluster.Persistences.Count == 1
                    && savedCluster.Guards.All(item => item.SubjectId == "symbol:record:save")
                    && savedCluster.Validations.All(item => item.SubjectId == "symbol:record:save")
                    && savedCluster.Persistences.All(item => item.SubjectId == "symbol:record:save")
                    && savedCluster.EvidenceRefs.Select(item => item.EvidenceId).Order(StringComparer.Ordinal).SequenceEqual(
                        savedCluster.StateFields.Concat(savedCluster.StateValues).Concat(savedCluster.Transitions)
                            .Concat(savedCluster.Guards).Concat(savedCluster.Validations).Concat(savedCluster.Persistences)
                            .Select(item => item.ClaimId).Order(StringComparer.Ordinal))
                    && savedCluster.StateValues.Single().PayloadJson.Contains("ARCHIVED", StringComparison.Ordinal)
                    && savedCluster.Transitions.Single().PayloadJson.Contains("DELETED", StringComparison.Ordinal),
                "state-rule clusters should retain only same-subject patterns and aligned evidence within a shared call path");

            using (var unrootedDb = new CozoDb("mem", ""))
            {
                var unrootedOm = new CozoOm(unrootedDb);
                await unrootedOm.InitCodeKnowledgeAsync();
                var unrootedPayload = JsonSerializer.Serialize(new { property = "state", value = "ORPHANED" });
                await unrootedOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                    Repositories: [new CodeRepositoryFact("repo:unrooted", root, "unrooted")],
                    Files: [new CodeFileFact("file:unrooted", "repo:unrooted", "src/UnrootedRecordService.java", "java")],
                    Symbols: [new CodeSymbolFact("symbol:record:unrooted", "file:unrooted", "orphanRecord", "method", 1, 3, Lang: "java")],
                    SemanticClaims:
                    [
                        new CodeSemanticClaimFact(CodeSemanticClaimIdentity.Create("file:unrooted", CodeSemanticClaimKinds.StateValue, unrootedPayload, 2, 2), "symbol:record:unrooted", CodeSemanticClaimKinds.StateValue, unrootedPayload, "file:unrooted", 2, 2, 0.95, "treesitter", "unrooted state value"),
                    ]));

                var unrootedClusters = await new BusinessOntologyInvestigationService(unrootedOm, new BusinessOntologyStore(unrootedOm))
                    .FindStateRuleClustersAsync("record");
                assert(unrootedClusters.Items.Single().SubjectId == "symbol:record:unrooted"
                        && unrootedClusters.Items.Single().CallPath.Count == 0,
                    "state-rule clusters without an entry point must not fabricate an entry-rooted call path");
            }

            var implementationClusters = await service.FindImplementationClustersAsync(domainSeed: "record");
            var implementationCluster = implementationClusters.Items.Single();
            assert(implementationCluster.SemanticCluster.ImplementationAnchors.Select(item => item.Role).Contains("spring:role:service", StringComparer.Ordinal)
                    && implementationCluster.SemanticCluster.ImplementationAnchors.Select(item => item.Role).Contains("ui:role:page", StringComparer.Ordinal)
                    && implementationCluster.SemanticCluster.ImplementationAnchors.Select(item => item.RelativePath).Distinct(StringComparer.Ordinal).Count() > 1
                    && implementationCluster.SemanticCluster.ImplementationAnchors.All(anchor => implementationCluster.EvidenceRefs.Any(evidence =>
                        evidence.EvidenceId == anchor.EvidenceId
                        && evidence.SymbolId == anchor.SymbolId
                        && evidence.Path == anchor.RelativePath))
                    && implementationCluster.EvidenceIds.Count > 0,
                "implementation clusters should retain only anchors with direct same-symbol, same-path evidence across frontend and backend roles");
            var denseSymbols = Enumerable.Range(0, BusinessUseCaseSliceBuilder.MaxSymbols)
                .Select(index => new CodeSymbolFact($"symbol:record:dense:{index:D2}", "file:dense", $"DenseRecord{index:D2}", "method", 1, 1, Lang: "java"))
                .ToArray();
            await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Files: [new CodeFileFact("file:dense", "repo:sample", "src/DenseRecordService.java", "java")],
                Symbols: denseSymbols,
                Edges: denseSymbols.SelectMany(source => denseSymbols
                    .Where(target => target.SymbolId != source.SymbolId)
                    .Select(target => new CodeEdgeFact(source.SymbolId, target.SymbolId, CodeEdgeKinds.Calls, "file:dense", 1, 1.0, "fixture", "dense call graph")))
                    .ToArray(),
                EntryPoints: [new CodeEntryPointFact(denseSymbols[0].SymbolId, "http_route", "POST /records/dense")]));
            var chainSymbols = Enumerable.Range(0, BusinessUseCaseSliceBuilder.MaxSymbols + 1)
                .Select(index => new CodeSymbolFact($"symbol:chainlimit:{index:D2}", "file:chainlimit", $"ChainLimit{index:D2}", "method", 1, 1, Lang: "java"))
                .ToArray();
            await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Files: [new CodeFileFact("file:chainlimit", "repo:sample", "src/ChainLimitService.java", "java")],
                Symbols: chainSymbols,
                Edges: chainSymbols.Skip(1).Select((target, index) => new CodeEdgeFact(chainSymbols[index].SymbolId, target.SymbolId, CodeEdgeKinds.Calls, "file:chainlimit", 1, 1.0, "fixture", "chain"))
                    .ToArray(),
                EntryPoints: [new CodeEntryPointFact(chainSymbols[0].SymbolId, "http_route", "GET /chainlimit")]));
            var before = await CountsAsync(om, ["ck_repo", "ck_file", "ck_symbol", "ck_edge", "ck_entry_point", "ck_semantic_claim", "onto_concept", "onto_candidate", "onto_review"]);
            var topology = await service.GetDomainTopologyAsync("record");
            var domainNode = topology.Nodes.Single(item => item.Layer == "domain");
            assert(topology.Nodes.Select(item => item.Layer).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).SequenceEqual(
                    ["domain", "entry-point", "evidence", "semantic-claim", "subject"]),
                "domain topology should retain every evidence layer when truncating CALLS edges");
            assert(topology.Edges.Any(item => item.Kind == "ENTRY_POINT")
                    && topology.Edges.Any(item => item.Kind == CodeEdgeKinds.Calls)
                    && topology.Edges.Any(item => item.Kind == "SEMANTIC_CLAIM")
                    && topology.Edges.Any(item => item.Kind == "EVIDENCE"),
                "domain topology should retain factual root, call, semantic-claim, and evidence edges");
            assert(topology.Edges.Count <= BusinessOntologyInvestigationService.MaxTopologyEdges && topology.Truncated,
                "domain topology should cap a dense CALLS projection and mark the result truncated");
            assert(topology.Nodes.All(item => item.Weight == Math.Max(1, item.Degree + item.DirectObservationCount))
                    && domainNode.DirectObservationCount == topology.Nodes.Count(item => item.Layer == "semantic-claim"),
                "domain topology visualization weight should derive only from degree and direct observations");
            var chainTopology = await service.GetDomainTopologyAsync("chainlimit");
            var repeatedChainTopology = await service.GetDomainTopologyAsync("chainlimit");
            assert(chainTopology.Nodes.Count(item => item.Layer == "subject") == chainSymbols.Length && !chainTopology.Truncated,
                "domain topology must not inherit the narrower use-case-slice traversal cap or report an untruncated partial graph");
            assert(chainTopology.Nodes.SequenceEqual(repeatedChainTopology.Nodes)
                    && chainTopology.Edges.SequenceEqual(repeatedChainTopology.Edges)
                    && chainTopology.QueryDigest == repeatedChainTopology.QueryDigest,
                "domain topology should be deterministic across repeated reads of the same indexed facts");
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

            var firstEvidencePage = await service.ListSemanticEvidenceAsync(limit: 1);
            var secondEvidencePage = await new BusinessOntologyInvestigationService(om, store)
                .ListSemanticEvidenceAsync(cursor: firstEvidencePage.NextCursor, limit: 1);
            assert(firstEvidencePage.Items.Count == 1 && firstEvidencePage.Truncated
                    && secondEvidencePage.Items.Count == 1
                    && secondEvidencePage.Items[0].ClaimId != firstEvidencePage.Items[0].ClaimId,
                "evidence enumeration must use deterministic bounded pages whose cursor survives a new CLI service instance");

            var runner = new LlmWikiToolRunner(om);
            var toolNames = LlmWikiToolRunner.ToolsJson()
                .Select(tool => tool?["name"]?.GetValue<string>())
                .Where(name => name is not null)
                .ToHashSet(StringComparer.Ordinal);
            assert(new[] { "ontology_investigation_overview", "find_business_terms", "list_use_case_slices", "get_use_case_slice", "list_semantic_evidence", "find_semantic_patterns", "get_semantic_evidence", "inspect_ontology_subject", "discover_domain_charters", "list_cross_layer_use_cases", "find_state_rule_clusters", "find_implementation_clusters", "get_domain_topology" }.All(toolNames.Contains),
                "shared tool metadata should expose every fixed investigation operation");
            var runnerPatterns = (BusinessOntologyInvestigationPage<BusinessOntologySemanticPattern>)await runner.CallAsync("find_semantic_patterns", new JsonObject
            {
                ["kind"] = CodeSemanticClaimKinds.BusinessGuard,
                ["term"] = "record",
            });
            assert(runnerPatterns.Items.Any(item => item.ClaimId == claimId),
                "runner dispatch should reuse the read-only investigation service");
            var runnerClusters = (BusinessOntologyInvestigationPage<BusinessOntologyImplementationCluster>)await runner.CallAsync("find_implementation_clusters", new JsonObject
            {
                ["domainSeed"] = "record",
                ["limit"] = 1,
            });
            assert(runnerClusters.Items.Single().EvidenceIds.Count > 0,
                "runner dispatch should expose the new bounded implementation-cluster operation");
            var runnerTopology = (BusinessOntologyDomainTopology)await runner.CallAsync("get_domain_topology", new JsonObject
            {
                ["term"] = "record",
            });
            assert(runnerTopology.Nodes.Count == topology.Nodes.Count && runnerTopology.QueryDigest == topology.QueryDigest,
                "runner dispatch should expose the same deterministic domain topology projection");

            var after = await CountsAsync(om, ["ck_repo", "ck_file", "ck_symbol", "ck_edge", "ck_entry_point", "ck_semantic_claim", "onto_concept", "onto_candidate", "onto_review"]);
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
                "ck_repo" => "repo_id",
                "ck_file" => "file_id",
                "ck_symbol" => "symbol_id",
                "ck_edge" => "from_id",
                "ck_entry_point" => "symbol_id",
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
