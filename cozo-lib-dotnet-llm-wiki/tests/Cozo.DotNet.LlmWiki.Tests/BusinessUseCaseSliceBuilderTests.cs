using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;
using System.Text.Json;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessUseCaseSliceBuilderTests
{
    private const string OntologyId = "SampleDomain.Ontology";
    private const string Record = OntologyId + ".Record";
    private const string Supplier = OntologyId + ".Supplier";

    public static async Task RunAsync(Action<bool, string> assert)
    {
        await ControllerServiceRepositoryEntityProducesSliceAsync(assert);
        await InterfaceDispatchViaMethodImplementsProducesSliceAsync(assert);
        await LargeRepairMethodKeepsOwnedBehaviorClaimsAsync(assert);
        await RouteOnlyWithoutBehaviorAnchorProducesNoSliceAsync(assert);
        await CyclesAndBudgetsProduceDeterministicTruncationAsync(assert);
        await FrontendCanOnlyCorroborateExistingSliceAsync(assert);
    }

    private static async Task ControllerServiceRepositoryEntityProducesSliceAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        var store = new BusinessOntologyStore(om);
        await SeedBaseGraphAsync(om, store);

        var builder = new BusinessUseCaseSliceBuilder(om, store);
        var result = await builder.BuildAsync(new BusinessUseCaseSliceBuildRequest(
            OntologyId,
            Corroborations:
            [
                new BusinessOntologyCorroborationObservation(
                    "frontend:record-create",
                    "repo:record-fe",
                    "src/pages/RecordCreate.tsx",
                    "RecordCreatePage",
                    4,
                    28,
                    BusinessOntologyCorroborationKinds.FrontendPage,
                    "record",
                    "route",
                    "/api/records",
                    "记录创建页面调用记录入库接口。"),
            ]));

        assert(result.Slices.Count == 1, "controller->service->repository/entity path should produce one bounded use-case slice");
        var slice = result.Slices.Single();
        var typedSupplierClaimId = ClaimId("file:record", CodeSemanticClaimKinds.TypedReference, TypedSupplierPayload, 10);
        var stateWriteClaimId = ClaimId("file:service", CodeSemanticClaimKinds.StateAssignment, StateWritePayload, 28);
        var guardClaimId = ClaimId("file:service", CodeSemanticClaimKinds.BusinessGuard, GuardPayload, 22);
        var expectedClaimIds = new[] { guardClaimId, stateWriteClaimId, typedSupplierClaimId }
            .Order(StringComparer.Ordinal)
            .ToArray();
        assert(slice.Id.StartsWith("usecase:slice:", StringComparison.Ordinal)
                && slice.Action == "POST /api/records"
                && slice.RouteKind == "http_route",
            "slice should retain a stable id and action boundary from ck_entry_point");
        assert(slice.SymbolIds.SequenceEqual(
                new[] { "symbol:route:create", "symbol:service:create", "symbol:repo:save", "symbol:record" },
                StringComparer.Ordinal),
            "slice symbols should be ordered by bounded BFS from the route");
        assert(slice.FileIds.SequenceEqual(
                new[] { "file:controller", "file:service", "file:repository", "file:record" },
                StringComparer.Ordinal),
            "slice should be cross-file and keep ordered files");
        assert(slice.Roles.SequenceEqual(
                new[] { "spring:role:controller", "spring:role:service", "spring:role:repository", "spring:role:entity" },
                StringComparer.Ordinal),
            "slice should preserve ordered Spring roles observed along the call path");
        assert(slice.ClaimIds.SequenceEqual(expectedClaimIds, StringComparer.Ordinal),
            "slice should include bounded, ordered semantic claims reachable from the path");
        assert(slice.ConceptIds.SequenceEqual(new[] { Record, Supplier }, StringComparer.Ordinal),
            "slice should reference canonical concept ids from current mappings, not route/service names");
        assert(slice.EvidenceIds.Contains("frontend:record-create", StringComparer.Ordinal)
                && slice.EvidenceIds.Contains(stateWriteClaimId, StringComparer.Ordinal)
                && slice.EvidenceIds.Contains(guardClaimId, StringComparer.Ordinal)
                && slice.EvidenceIds.Contains("base:record", StringComparer.Ordinal),
            "frontend evidence should corroborate an already behavior-anchored slice and guard claims should stay in the slice evidence");
        assert(!slice.ConceptIds.Any(id =>
                id.Contains("Route", StringComparison.OrdinalIgnoreCase)
                || id.Contains("Service", StringComparison.OrdinalIgnoreCase)),
            "route and service names must not become business concepts");
        assert(!slice.Diagnostics.TruncatedByDepth
                && !slice.Diagnostics.TruncatedBySymbols
                && !slice.Diagnostics.TruncatedByClaims,
            "unbounded fixture should not report truncation");
    }

    private static async Task InterfaceDispatchViaMethodImplementsProducesSliceAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        var store = new BusinessOntologyStore(om);
        await SeedBaseOntologyAsync(store);
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:controller", "repo:record", "src/RecordTransferController.java", "java"),
                new CodeFileFact("file:service-interface", "repo:record", "src/IRecordTransferService.java", "java"),
                new CodeFileFact("file:service-impl", "repo:record", "src/RecordTransferServiceImpl.java", "java"),
                new CodeFileFact("file:repository", "repo:record", "src/RecordRepository.java", "java"),
                new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java"),
                new CodeFileFact("file:supplier", "repo:record", "src/SupplierEntity.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:transfer-controller", "file:controller", "RecordTransferController", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:transfer-route", "file:controller", "saveRecordTransfer", "method", 62, 70, ParentId: "symbol:transfer-controller", Lang: "java"),
                new CodeSymbolFact("symbol:transfer-interface", "file:service-interface", "IRecordTransferService", "interface", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:transfer-interface:init", "file:service-interface", "initRecordTransfer", "method", 37, 37, ParentId: "symbol:transfer-interface", Lang: "java"),
                new CodeSymbolFact("symbol:transfer-impl", "file:service-impl", "RecordTransferServiceImpl", "class", 1, 160, Lang: "java"),
                new CodeSymbolFact("symbol:transfer-impl:init", "file:service-impl", "initRecordTransfer", "method", 72, 120, ParentId: "symbol:transfer-impl", Lang: "java"),
                new CodeSymbolFact("symbol:repo", "file:repository", "RecordRepository", "interface", 1, 40, Lang: "java"),
                new CodeSymbolFact("symbol:repo:save", "file:repository", "save", "method", 8, 8, ParentId: "symbol:repo", Lang: "java"),
                new CodeSymbolFact("symbol:record", "file:record", "RecordEntity", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:supplier", "file:supplier", "SupplierEntity", "class", 1, 20, Lang: "java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:transfer-controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "spring_annotation"),
                new CodeEdgeFact("symbol:transfer-impl", "spring:role:service", "SPRING_ROLE", "file:service-impl", 1, 0.98, "spring_annotation"),
                new CodeEdgeFact("symbol:repo", "spring:role:repository", "SPRING_ROLE", "file:repository", 1, 0.98, "spring_annotation"),
                new CodeEdgeFact("symbol:record", "spring:role:entity", "SPRING_ROLE", "file:record", 1, 0.98, "spring_annotation"),
                new CodeEdgeFact("symbol:transfer-route", "symbol:transfer-interface:init", CodeEdgeKinds.Calls, "file:controller", 62, 0.9, "treesitter", "controller calls service interface"),
                new CodeEdgeFact("symbol:transfer-impl:init", "symbol:transfer-interface:init", CodeEdgeKinds.MethodImplements, "file:service-impl", 72, 0.9, "treesitter", "implementation method dispatches interface member"),
                new CodeEdgeFact("symbol:transfer-impl:init", "symbol:repo:save", CodeEdgeKinds.Calls, "file:service-impl", 92, 0.9, "treesitter", "implementation calls repository"),
                new CodeEdgeFact("symbol:repo:save", "symbol:record", CodeEdgeKinds.Calls, "file:repository", 8, 0.9, "treesitter", "repository writes entity"),
            ],
            EntryPoints: [new CodeEntryPointFact("symbol:transfer-route", "http_route", "POST /record-transfer")],
            SemanticClaims:
            [
                Claim("symbol:record", CodeSemanticClaimKinds.TypedReference, TypedSupplierPayload, "file:record", 10),
                Claim("symbol:transfer-impl:init", CodeSemanticClaimKinds.BusinessGuard, GuardPayload.Replace("symbol:service:create", "symbol:transfer-impl:init", StringComparison.Ordinal), "file:service-impl", 84),
                Claim("symbol:transfer-impl:init", CodeSemanticClaimKinds.StateAssignment, StateWritePayload.Replace("createRecord", "initRecordTransfer", StringComparison.Ordinal), "file:service-impl", 96),
            ]));

        var result = await new BusinessUseCaseSliceBuilder(om, store)
            .BuildAsync(new BusinessUseCaseSliceBuildRequest(OntologyId));

        assert(result.Slices.Count == 1, "route -> service interface should dispatch through explicit reverse METHOD_IMPLEMENTS to reach implementation behavior");
        var slice = result.Slices.Single();
        assert(slice.SymbolIds.Contains("symbol:transfer-interface:init", StringComparer.Ordinal)
                && slice.SymbolIds.Contains("symbol:transfer-impl:init", StringComparer.Ordinal)
                && slice.SymbolIds.Contains("symbol:repo:save", StringComparer.Ordinal),
            "slice walk should retain the interface method, implementation method, and downstream repository call");
        var dispatchedGuardPayload = GuardPayload.Replace("symbol:service:create", "symbol:transfer-impl:init", StringComparison.Ordinal);
        var dispatchedGuardClaimId = ClaimId("file:service-impl", CodeSemanticClaimKinds.BusinessGuard, dispatchedGuardPayload, 84);
        assert(slice.Roles.Contains("spring:role:service", StringComparer.Ordinal)
                && slice.ClaimIds.Contains(dispatchedGuardClaimId, StringComparer.Ordinal),
            "dispatched implementation behavior should contribute service role and semantic guard claims");
    }

    private static async Task LargeRepairMethodKeepsOwnedBehaviorClaimsAsync(Action<bool, string> assert)
    {
        const string damageConcept = OntologyId + ".RecordDamageInfo";
        const string repairMethod = "symbol:repair-impl:save";
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(new BusinessOntologyGenerationInput(
            OntologyId,
            "repair-base",
            "repair-fingerprint",
            "test",
            "2026-07-18T16:00:00Z",
            Concepts:
            [
                new BusinessOntologyConcept(damageConcept, "businessObject", "记录损坏信息", "记录维修中的损坏信息。", "accepted", 0.95, ["base:damage"]),
            ],
            Attributes: [],
            Relations: [],
            Rules: [],
            Lifecycles: [],
            States: [],
            Transitions: [],
            Mappings:
            [
                new BusinessOntologyMapping(
                    OntologyId + ".Mapping.RecordDamageInfo",
                    "concept",
                    damageConcept,
                    "representedBy",
                    "repo:record",
                    "java",
                    "class",
                    "symbol:damage-info",
                    "src/RecordDamageInfo.java",
                    "treesitter",
                    0.95,
                    "accepted",
                    ["base:damage"]),
            ],
            Evidence:
            [
                new BusinessOntologyEvidence("base:damage", "repo:record", "src/RecordDamageInfo.java", "symbol:damage-info", 1, 80, "contractual", "treesitter", 0.95, "code", "记录损坏信息实体。"),
            ],
            Candidates: [],
            Reviews: [],
            Diagnostics: []));
        await om.InitCodeKnowledgeAsync();

        var guardPayload = JsonSerializer.Serialize(new
        {
            effectKind = "throw",
            effectMessage = "仅待维修损坏信息可以保存维修结果",
            effectSource = "throw new BusinessException()",
            method = "repairSave",
            methodSymbolId = repairMethod,
            ownerSymbolId = "symbol:repair-impl",
            predicateSource = "!CODE_1.equals(damageInfosModel.getState())",
            stateGuard = new
            {
                allowedValues = new[] { "CODE_1" },
                property = "state",
                receiver = "damageInfosModel",
                source = "!CODE_1.equals(damageInfosModel.getState())",
                valueEncoding = "string",
            },
        });
        var mutationCode2Payload = RepairMutationPayload(repairMethod, "CODE_2", 132);
        var mutationCode5Payload = RepairMutationPayload(repairMethod, "CODE_5", 138);
        var parameterPayload = JsonSerializer.Serialize(new
        {
            collection = false,
            declaringSymbolId = repairMethod,
            member = "damageInfosModel",
            ownerSymbolId = "symbol:repair-impl",
            rawType = "RecordDamageInfo",
            resolvedTypeName = "RecordDamageInfo",
            resolvedTypeSymbolId = "symbol:damage-info",
            usageKind = "parameter",
        });
        var guard = Claim(repairMethod, CodeSemanticClaimKinds.BusinessGuard, guardPayload, "file:repair-impl", 126);
        var mutationCode2 = Claim(repairMethod, CodeSemanticClaimKinds.StateAssignment, mutationCode2Payload, "file:repair-impl", 132);
        var mutationCode5 = Claim(repairMethod, CodeSemanticClaimKinds.StateAssignment, mutationCode5Payload, "file:repair-impl", 138);
        var parameter = Claim(repairMethod, CodeSemanticClaimKinds.TypedReference, parameterPayload, "file:repair-impl", 118);
        var pollution = Enumerable.Range(0, 80)
            .Select(index =>
            {
                var payload = JsonSerializer.Serialize(new
                {
                    collection = false,
                    declaringSymbolId = $"symbol:unrelated:{index:D2}",
                    member = $"result{index:D2}",
                    ownerSymbolId = index % 2 == 0 ? "symbol:damage-info" : "symbol:unrelated-service",
                    rawType = "Result",
                    resolvedTypeName = "Result",
                    resolvedTypeSymbolId = "symbol:result",
                    usageKind = "local",
                });
                return Claim(
                    $"symbol:unrelated:{index:D2}",
                    CodeSemanticClaimKinds.TypedReference,
                    payload,
                    "file:unrelated",
                    200 + index);
            })
            .ToArray();
        var ambiguousMutationPayload = RepairMutationPayload("symbol:ambiguous:repair", "CODE_9", 40);
        var ambiguousMutation = Claim(
            "symbol:ambiguous:repair",
            CodeSemanticClaimKinds.StateAssignment,
            ambiguousMutationPayload,
            "file:ambiguous",
            40);
        var highConfidenceMutation = Claim(
            "symbol:high-confidence:repair",
            CodeSemanticClaimKinds.StateAssignment,
            RepairMutationPayload("symbol:high-confidence:repair", "CODE_8", 46),
            "file:high-confidence",
            46);
        var lowConfidencePreciseMutation = Claim(
            "symbol:low-confidence-precise:repair",
            CodeSemanticClaimKinds.StateAssignment,
            RepairMutationPayload("symbol:low-confidence-precise:repair", "CODE_7", 56),
            "file:low-confidence-precise",
            56);

        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:controller", "repo:record", "src/RecordRepairController.java", "java"),
                new CodeFileFact("file:service-interface", "repo:record", "src/IRecordRepairService.java", "java"),
                new CodeFileFact("file:repair-impl", "repo:record", "src/RecordRepairServiceImpl.java", "java"),
                new CodeFileFact("file:mapper", "repo:record", "src/RecordDamageInfoMapper.java", "java"),
                new CodeFileFact("file:damage", "repo:record", "src/RecordDamageInfo.java", "java"),
                new CodeFileFact("file:common", "repo:record", "src/Result.java", "java"),
                new CodeFileFact("file:ambiguous", "repo:record", "src/AmbiguousRepairService.java", "java"),
                new CodeFileFact("file:high-confidence", "repo:record", "src/HighConfidenceRepairService.java", "java"),
                new CodeFileFact("file:low-confidence-precise", "repo:record", "src/LowConfidencePreciseRepairService.java", "java"),
                new CodeFileFact("file:unrelated", "repo:record", "src/UnrelatedService.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:repair-controller", "file:controller", "RecordRepairController", "class", 1, 100, Lang: "java"),
                new CodeSymbolFact("symbol:repair-route", "file:controller", "repairSave", "method", 60, 72, ParentId: "symbol:repair-controller", Lang: "java"),
                new CodeSymbolFact("symbol:repair-interface", "file:service-interface", "IRecordRepairService", "interface", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:repair-interface:save", "file:service-interface", "repairSave", "method", 30, 30, ParentId: "symbol:repair-interface", Lang: "java"),
                new CodeSymbolFact("symbol:repair-impl", "file:repair-impl", "RecordRepairServiceImpl", "class", 1, 220, Lang: "java"),
                new CodeSymbolFact(repairMethod, "file:repair-impl", "repairSave", "method", 110, 180, ParentId: "symbol:repair-impl", Lang: "java"),
                new CodeSymbolFact("symbol:damage-mapper", "file:mapper", "RecordDamageInfoMapper", "interface", 1, 60, Lang: "java"),
                new CodeSymbolFact("symbol:damage-mapper:update", "file:mapper", "updateById", "method", 20, 20, ParentId: "symbol:damage-mapper", Lang: "java"),
                new CodeSymbolFact("symbol:damage-info", "file:damage", "RecordDamageInfo", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:result", "file:common", "Result", "class", 1, 40, Lang: "java"),
                new CodeSymbolFact("symbol:ambiguous:repair", "file:ambiguous", "repairSave", "method", 30, 50, Lang: "java"),
                new CodeSymbolFact("symbol:high-confidence:repair", "file:high-confidence", "repairSave", "method", 40, 50, Lang: "java"),
                new CodeSymbolFact("symbol:low-confidence-precise:repair", "file:low-confidence-precise", "repairSave", "method", 50, 60, Lang: "java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:repair-controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "spring_annotation"),
                new CodeEdgeFact("symbol:repair-impl", "spring:role:service", "SPRING_ROLE", "file:repair-impl", 1, 0.98, "spring_annotation"),
                new CodeEdgeFact("symbol:damage-mapper", "spring:role:repository", "SPRING_ROLE", "file:mapper", 1, 0.98, "spring_annotation"),
                new CodeEdgeFact("symbol:damage-info", "spring:role:entity", "SPRING_ROLE", "file:damage", 1, 0.98, "spring_annotation"),
                new CodeEdgeFact("symbol:repair-route", "symbol:repair-interface:save", CodeEdgeKinds.Calls, "file:controller", 66, 0.9, "treesitter", "controller calls service interface"),
                new CodeEdgeFact(repairMethod, "symbol:repair-interface:save", CodeEdgeKinds.MethodImplements, "file:repair-impl", 110, 0.9, "treesitter", "implementation dispatch"),
                new CodeEdgeFact(repairMethod, "symbol:damage-mapper:update", CodeEdgeKinds.Calls, "file:repair-impl", 150, 0.9, "treesitter", "persist damage info"),
                new CodeEdgeFact("symbol:damage-mapper:update", "symbol:damage-info", CodeEdgeKinds.Calls, "file:mapper", 20, 0.9, "treesitter", "mapper writes entity"),
                new CodeEdgeFact("symbol:repair-route", "symbol:result", CodeEdgeKinds.Calls, "file:controller", 68, 0.9, "treesitter", "route returns common Result"),
                new CodeEdgeFact("symbol:repair-route", "symbol:ambiguous:repair", CodeEdgeKinds.Calls, "file:controller", 69, 0.5, "treesitter", "is-record-service/src/main/java/com/kuaishou/record/service/RecordRepairServiceImpl.java: ambiguous:12"),
                new CodeEdgeFact("symbol:repair-route", "symbol:high-confidence:repair", CodeEdgeKinds.Calls, "file:controller", 70, 0.9, "treesitter", "is-record-service/src/main/java/com/kuaishou/record/service/RecordRepairServiceImpl.java: ambiguous:1"),
                new CodeEdgeFact("symbol:repair-route", "symbol:low-confidence-precise:repair", CodeEdgeKinds.Calls, "file:controller", 71, 0.5, "treesitter", "resolved exact call target"),
            ],
            EntryPoints: [new CodeEntryPointFact("symbol:repair-route", "http_route", "POST /api/v1/repair/save")],
            SemanticClaims:
            [
                parameter,
                guard,
                mutationCode2,
                mutationCode5,
                ambiguousMutation,
                highConfidenceMutation,
                lowConfidencePreciseMutation,
                .. pollution,
            ]));

        var result = await new BusinessUseCaseSliceBuilder(om, store)
            .BuildAsync(new BusinessUseCaseSliceBuildRequest(OntologyId));

        var slice = result.Slices.Single();
        assert(slice.Action == "POST /api/v1/repair/save"
                && slice.SymbolIds.Contains(repairMethod, StringComparer.Ordinal)
                && !slice.SymbolIds.Contains("symbol:ambiguous:repair", StringComparer.Ordinal),
            "repair route should dispatch to the concrete implementation while excluding a 0.5 CALLS branch whose real evidence has a path-prefixed ambiguous marker");
        assert(slice.SymbolIds.Contains("symbol:high-confidence:repair", StringComparer.Ordinal)
                && slice.SymbolIds.Contains("symbol:low-confidence-precise:repair", StringComparer.Ordinal),
            "high-confidence ambiguous-looking CALLS evidence and low-confidence precise CALLS evidence should not be dropped");
        assert(slice.ClaimIds.Contains(guard.ClaimId, StringComparer.Ordinal)
                && slice.ClaimIds.Contains(mutationCode2.ClaimId, StringComparer.Ordinal)
                && slice.ClaimIds.Contains(mutationCode5.ClaimId, StringComparer.Ordinal)
                && !slice.ClaimIds.Contains(ambiguousMutation.ClaimId, StringComparer.Ordinal)
                && slice.ClaimIds.Contains(highConfidenceMutation.ClaimId, StringComparer.Ordinal)
                && slice.ClaimIds.Contains(lowConfidencePreciseMutation.ClaimId, StringComparer.Ordinal)
                && !slice.ClaimIds.Any(id => pollution.Any(item => item.ClaimId == id)),
            "slice claim ownership must retain the repair guard/mutations and reject unrelated claims that only reference walked common or entity types");
        assert(slice.ConceptIds.SequenceEqual([damageConcept], StringComparer.Ordinal)
                && !slice.Diagnostics.TruncatedByClaims,
            "owned repair behavior should identify RecordDamageInfo without exhausting the claim budget on common-type pollution");

        static string RepairMutationPayload(string methodSymbolId, string toValue, int line) =>
            JsonSerializer.Serialize(new
            {
                field = "state",
                fromValue = "CODE_1",
                method = "repairSave",
                methodSymbolId,
                mutationKind = "setter",
                ownerSymbolId = "symbol:damage-info",
                property = "state",
                rawValue = $"\"{toValue}\"",
                receiver = "damageInfosModel",
                sourceLine = line,
                toValue,
                valueEncoding = "string",
            });
    }

    private static async Task RouteOnlyWithoutBehaviorAnchorProducesNoSliceAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        var store = new BusinessOntologyStore(om);
        await SeedBaseOntologyAsync(store);
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files: [new CodeFileFact("file:controller", "repo:record", "src/RecordController.java", "java")],
            Symbols:
            [
                new CodeSymbolFact("symbol:route:list", "file:controller", "listRecords", "method", 10, 16, ParentId: "symbol:controller", Lang: "java"),
                new CodeSymbolFact("symbol:controller", "file:controller", "RecordController", "class", 1, 40, Lang: "java"),
            ],
            Edges: [new CodeEdgeFact("symbol:controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "spring_annotation")],
            EntryPoints: [new CodeEntryPointFact("symbol:route:list", "http_route", "GET /api/records")]));

        var result = await new BusinessUseCaseSliceBuilder(om, store)
            .BuildAsync(new BusinessUseCaseSliceBuildRequest(OntologyId));

        assert(result.Slices.Count == 0, "route-only entry points without read/write/state/type anchors should not produce slices");
        assert(result.Diagnostics.Any(item =>
                item.Kind == BusinessUseCaseSliceDiagnosticKinds.DroppedRouteWithoutBehaviorAnchor
                && item.SubjectId == "symbol:route:list"),
            "dropped route should be visible as a bounded diagnostic");
    }

    private static async Task CyclesAndBudgetsProduceDeterministicTruncationAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        var store = new BusinessOntologyStore(om);
        await SeedBaseGraphAsync(om, store, extraServiceChainLength: 40, extraClaimCount: 30);

        var builder = new BusinessUseCaseSliceBuilder(om, store);
        var first = await builder.BuildAsync(new BusinessUseCaseSliceBuildRequest(OntologyId));
        var second = await builder.BuildAsync(new BusinessUseCaseSliceBuildRequest(OntologyId));
        var slice = first.Slices.Single();

        assert(slice.SymbolIds.Count == BusinessUseCaseSliceBuilder.MaxSymbols
                && slice.ClaimIds.Count == BusinessUseCaseSliceBuilder.MaxClaims,
            "builder should enforce fixed symbol and claim budgets");
        assert(slice.Diagnostics.TruncatedBySymbols
                && slice.Diagnostics.TruncatedByClaims,
            "cycles and over-budget paths should report symbol and claim truncation diagnostics");
        assert(first.Slices.Select(item => item.Id).SequenceEqual(second.Slices.Select(item => item.Id), StringComparer.Ordinal)
                && first.Slices.Single().SymbolIds.SequenceEqual(second.Slices.Single().SymbolIds, StringComparer.Ordinal)
                && first.Slices.Single().ClaimIds.SequenceEqual(second.Slices.Single().ClaimIds, StringComparer.Ordinal),
            "bounded BFS output should be deterministic across repeated runs");
    }

    private static async Task FrontendCanOnlyCorroborateExistingSliceAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        var store = new BusinessOntologyStore(om);
        await SeedBaseOntologyAsync(store);
        await om.InitCodeKnowledgeAsync();

        var frontendOnly = await new BusinessUseCaseSliceBuilder(om, store)
            .BuildAsync(new BusinessUseCaseSliceBuildRequest(
                OntologyId,
                Corroborations:
                [
                    new BusinessOntologyCorroborationObservation(
                        "frontend:record-only",
                        "repo:record-fe",
                        "src/pages/RecordOnly.tsx",
                        "RecordOnlyPage",
                        1,
                        12,
                        BusinessOntologyCorroborationKinds.FrontendPage,
                        "record",
                        "route",
                        "/api/records",
                        "只有前端调用，没有后端行为锚点。"),
                ]));

        assert(frontendOnly.Slices.Count == 0, "frontend observations alone must not create use-case slices");
    }

    private static async Task SeedBaseGraphAsync(
        CozoOm om,
        BusinessOntologyStore store,
        int extraServiceChainLength = 0,
        int extraClaimCount = 0)
    {
        await SeedBaseOntologyAsync(store);
        await om.InitCodeKnowledgeAsync();

        var files = new List<CodeFileFact>
        {
            new("file:controller", "repo:record", "src/RecordController.java", "java"),
            new("file:service", "repo:record", "src/RecordService.java", "java"),
            new("file:repository", "repo:record", "src/RecordRepository.java", "java"),
            new("file:record", "repo:record", "src/RecordEntity.java", "java"),
            new("file:supplier", "repo:record", "src/SupplierEntity.java", "java"),
        };
        var symbols = new List<CodeSymbolFact>
        {
            new("symbol:controller", "file:controller", "RecordController", "class", 1, 80, Lang: "java"),
            new("symbol:route:create", "file:controller", "createRecord", "method", 12, 24, ParentId: "symbol:controller", Lang: "java"),
            new("symbol:service", "file:service", "RecordService", "class", 1, 80, Lang: "java"),
            new("symbol:service:create", "file:service", "createRecord", "method", 18, 44, ParentId: "symbol:service", Lang: "java"),
            new("symbol:repo", "file:repository", "RecordRepository", "interface", 1, 40, Lang: "java"),
            new("symbol:repo:save", "file:repository", "save", "method", 8, 8, ParentId: "symbol:repo", Lang: "java"),
            new("symbol:record", "file:record", "RecordEntity", "class", 1, 80, Lang: "java"),
            new("symbol:supplier", "file:supplier", "SupplierEntity", "class", 1, 20, Lang: "java"),
        };
        var edges = new List<CodeEdgeFact>
        {
            new("symbol:controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "spring_annotation"),
            new("symbol:service", "spring:role:service", "SPRING_ROLE", "file:service", 1, 0.98, "spring_annotation"),
            new("symbol:repo", "spring:role:repository", "SPRING_ROLE", "file:repository", 1, 0.98, "spring_annotation"),
            new("symbol:record", "spring:role:entity", "SPRING_ROLE", "file:record", 1, 0.98, "spring_annotation"),
            new("symbol:route:create", "symbol:service:create", CodeEdgeKinds.Calls, "file:controller", 16, 1.0, "treesitter", "controller calls service"),
            new("symbol:service:create", "symbol:repo:save", CodeEdgeKinds.Calls, "file:service", 24, 1.0, "treesitter", "service calls repository"),
            new("symbol:repo:save", "symbol:record", CodeEdgeKinds.Calls, "file:repository", 8, 1.0, "treesitter", "repository writes entity"),
            new("symbol:service:create", "symbol:route:create", CodeEdgeKinds.Calls, "file:service", 30, 1.0, "treesitter", "cycle guard"),
        };
        var claims = new List<CodeSemanticClaimFact>
        {
            Claim("symbol:record", CodeSemanticClaimKinds.TypedReference, TypedSupplierPayload, "file:record", 10),
            Claim("symbol:service:create", CodeSemanticClaimKinds.BusinessGuard, GuardPayload, "file:service", 22),
            Claim("symbol:service:create", CodeSemanticClaimKinds.StateAssignment, StateWritePayload, "file:service", 28),
        };

        for (var index = 0; index < extraServiceChainLength; index++)
        {
            var fileId = $"file:extra:{index:00}";
            var symbolId = $"symbol:extra:{index:00}";
            files.Add(new CodeFileFact(fileId, "repo:record", $"src/Extra{index:00}.java", "java"));
            symbols.Add(new CodeSymbolFact(symbolId, fileId, $"extra{index:00}", "method", 1, 4, Lang: "java"));
            edges.Add(new CodeEdgeFact(
                "symbol:service:create",
                symbolId,
                CodeEdgeKinds.Calls,
                fileId,
                2,
                1.0,
                "treesitter",
                "extra fanout"));
        }
        if (extraServiceChainLength > 0)
        {
            for (var index = 0; index < 5; index++)
            {
                var fileId = $"file:deep:{index:00}";
                var symbolId = $"symbol:deep:{index:00}";
                files.Add(new CodeFileFact(fileId, "repo:record", $"src/Deep{index:00}.java", "java"));
                symbols.Add(new CodeSymbolFact(symbolId, fileId, $"deep{index:00}", "method", 1, 4, Lang: "java"));
                edges.Add(new CodeEdgeFact(
                    index == 0 ? "symbol:service:create" : $"symbol:deep:{index - 1:00}",
                    symbolId,
                    CodeEdgeKinds.Calls,
                    fileId,
                    2,
                    1.0,
                    "treesitter",
                    "extra depth"));
            }
        }
        for (var index = 0; index < extraClaimCount; index++)
        {
            claims.Add(Claim(
                "symbol:service:create",
                CodeSemanticClaimKinds.StateAssignment,
                $$"""
                {"enumType":"RecordStatus","enumTypeSymbolId":"symbol:status","field":"status","fromValue":"DRAFT","method":"extra{{index}}","ownerSymbolId":"symbol:record","toValue":"ACTIVE"}
                """,
                "file:service",
                40 + index));
        }

        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files: files,
            Symbols: symbols,
            Edges: edges,
            EntryPoints: [new CodeEntryPointFact("symbol:route:create", "http_route", "POST /api/records")],
            SemanticClaims: claims));
    }

    private static async Task SeedBaseOntologyAsync(BusinessOntologyStore store)
    {
        await store.ReplaceGenerationAsync(new BusinessOntologyGenerationInput(
            OntologyId,
            "base",
            "fingerprint",
            "test",
            "2026-07-18T10:00:00Z",
            Concepts:
            [
                new BusinessOntologyConcept(Record, "businessObject", "记录", "记录台账中的业务概念。", "accepted", 0.95, ["base:record"]),
                new BusinessOntologyConcept(Supplier, "businessObject", "供应商", "供应商业务概念。", "accepted", 0.95, ["base:supplier"]),
            ],
            Attributes: [],
            Relations: [],
            Rules: [],
            Lifecycles: [],
            States: [],
            Transitions: [],
            Mappings:
            [
                new BusinessOntologyMapping(OntologyId + ".Mapping.Record", "concept", Record, "representedBy", "repo:record", "java", "class", "symbol:record", "src/RecordEntity.java", "treesitter", 0.95, "accepted", ["base:record"]),
                new BusinessOntologyMapping(OntologyId + ".Mapping.Supplier", "concept", Supplier, "representedBy", "repo:record", "java", "class", "symbol:supplier", "src/SupplierEntity.java", "treesitter", 0.95, "accepted", ["base:supplier"]),
            ],
            Evidence:
            [
                new BusinessOntologyEvidence("base:record", "repo:record", "src/RecordEntity.java", "symbol:record", 1, 80, "contractual", "treesitter", 0.95, "code", "记录实体。"),
                new BusinessOntologyEvidence("base:supplier", "repo:record", "src/SupplierEntity.java", "symbol:supplier", 1, 20, "contractual", "treesitter", 0.95, "code", "供应商实体。"),
            ],
            Candidates: [],
            Reviews: [],
            Diagnostics: []));
    }

    private const string TypedSupplierPayload = """
    {"collection":false,"member":"supplier","ownerSymbolId":"symbol:record","rawType":"SupplierEntity","resolvedTypeName":"SupplierEntity","resolvedTypeSymbolId":"symbol:supplier"}
    """;

    private const string StateWritePayload = """
    {"enumType":"RecordStatus","enumTypeSymbolId":"symbol:status","field":"status","fromValue":"DRAFT","method":"createRecord","ownerSymbolId":"symbol:record","toValue":"ACTIVE"}
    """;

    private const string GuardPayload = """
    {"effectKind":"throw","effectMessage":"库存不足，不能创建记录","effectSource":"throw new BusinessException(\"库存不足，不能创建记录\")","method":"createRecord","methodSymbolId":"symbol:service:create","ownerSymbolId":"symbol:service","predicateSource":"request.quantity() <= 0"}
    """;

    private static CodeSemanticClaimFact Claim(
        string subjectId,
        string kind,
        string payloadJson,
        string fileId,
        int line) =>
        new(ClaimId(fileId, kind, payloadJson, line), subjectId, kind, payloadJson, fileId, line, line, 0.95, "treesitter", kind);

    private static string ClaimId(string fileId, string kind, string payloadJson, int line) =>
        CodeSemanticClaimIdentity.Create(fileId, kind, payloadJson, line, line);
}
