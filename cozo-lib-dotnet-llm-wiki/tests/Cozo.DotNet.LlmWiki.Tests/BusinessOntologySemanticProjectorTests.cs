using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.LlmClient;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;
using Cozo.DotNet.Om.Support;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologySemanticProjectorTests
{
    private const string OntologyId = "SampleDomain.Ontology";
    private const string Record = OntologyId + ".Record";
    private const string Supplier = OntologyId + ".Supplier";

    public static async Task RunAsync(Action<bool, string> assert)
    {
        await ProjectsDirectClaimsAsync(assert);
        await ProjectsRelationsFromCanonicalEndpointsAndUseCaseSlicesAsync(assert);
        await RejectsOperationTypedReferencesAsRelationsAsync(assert);
        await NormalizesStructuralRelationMemberNamesAsync(assert);
        await RejectsHighFanoutWorkflowCarrierRelationsAsync(assert);
        await RejectsInfrastructureRoleTypedReferencesAsRelationsAsync(assert);
        await ProjectsBusinessRulesOnlyFromGuardedUseCaseSlicesAsync(assert);
        await BoundsBusinessRuleRationaleAcrossManyUseCaseSlicesAsync(assert);
        await BindsBusinessRuleSubjectFromPredicateSourceAsync(assert);
        await ProjectsLifecycleTransitionsFromGuardedSetterUseCaseSlicesAsync(assert);
        await ProjectsRepairLifecycleThroughInterfaceAndLargeMethodAsync(assert);
        await RequiresProvableLifecycleTransitionScopeAsync(assert);
        await ProjectsScalarLifecycleTransitionsFromObservedValuesAsync(assert);
        await ProjectsMixedEncodingScalarLifecycleTransitionsFromGuardedAssignmentsAsync(assert);
        await AssistedModeExcludesFieldConstraintsFromProposalPackAsync(assert);
        await CorroboratesAndDiagnosesConflictsAsync(assert);
        await RequiresExplicitReindexAsync(assert);
    }

    private static async Task ProjectsDirectClaimsAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var innerStore = new CozoDbOmStore(db);
        await innerStore.RunAsync(":create depa_projection_sentinel {id => value}");
        await innerStore.RunAsync(
            """?[id, value] <- [["sentinel", "unchanged"]] :put depa_projection_sentinel {id => value}""");
        var guardedStore = new ForbiddenRelationStore(innerStore, "depa_");
        var om = new CozoOm(guardedStore);
        await om.InitCodeKnowledgeAsync();

        var claims = Claims();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java"),
                new CodeFileFact("file:supplier", "repo:record", "src/SupplierEntity.java", "java"),
                new CodeFileFact("file:service", "repo:record", "src/RecordService.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:record", "file:record", "RecordEntity", "class", 1, 40, Lang: "java"),
                new CodeSymbolFact("symbol:supplier", "file:supplier", "SupplierEntity", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact("symbol:unmapped", "file:supplier", "WarehouseEntity", "class", 21, 30, Lang: "java"),
                new CodeSymbolFact("symbol:service", "file:service", "RecordService", "class", 1, 30, Lang: "java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:service", "spring:role:service", "SPRING_ROLE", "file:service", 1, 0.98, "spring_annotation"),
            ],
            SemanticClaims: claims));

        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(BaseGeneration());
        var projector = new BusinessOntologySemanticProjector(om, store);
        var request = new BusinessOntologySemanticProjectionRequest(
            OntologyId,
            "semantic-1",
            "semantic-fingerprint",
            "2026-07-18T10:00:00Z");
        var first = await projector.ProjectAsync(request);
        var snapshot = await store.ReadExportableAsync(OntologyId);

        var semanticCandidates = snapshot.Candidates
            .Where(item => item.Id.StartsWith("candidate:semantic:", StringComparison.Ordinal))
            .ToArray();
        assert(first.RelationCandidates == 2
                && first.CrossLayerRelationCandidates == 0
                && first.DirectOnlyRelationDiagnostics == 2
                && first.RuleCandidates == 0
                && first.LifecycleCandidates == 1,
            "direct mapped claims should yield two auditable direct-only relations, zero cross-layer relations, zero annotation-only rules, and one lifecycle");
        assert(semanticCandidates.Length == 3 && semanticCandidates.All(item => item.Status == "pending"),
            "every deterministic semantic projection must remain a pending candidate");
        assert(snapshot.Evidence
                .Where(item => item.SourceKind == "code-semantic-claim")
                .All(item => item.Id.StartsWith("semantic:", StringComparison.Ordinal)
                    && item.Id.Count(ch => ch == ':') == 1),
            "semantic source evidence identities must remain ontology-xml compatible namespaced IDs");
        assert(snapshot.Relations.Count == 0
                && snapshot.Rules.Count == 0
                && snapshot.Lifecycles.Count == 0
                && snapshot.States.Count == 0
                && snapshot.Transitions.Count == 0,
            "projection must not promote relation, rule, lifecycle, state, or transition assertions");

        var validator = new OntologySemanticCandidateValidator();
        foreach (var candidate in semanticCandidates)
        {
            var validated = validator.Validate(
                candidate.PayloadJson,
                snapshot.Concepts.Select(item => item.Id),
                snapshot.Evidence.Select(item => item.Id));
            assert(candidate.Id == validated.Id
                    && candidate.PayloadJson == validated.CanonicalPayloadJson
                    && candidate.EvidenceIds.SequenceEqual(validated.EvidenceIds),
                "stored candidates should retain canonical payloads, stable local ids, and direct evidence refs");
        }

        var relations = semanticCandidates
            .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Relation)
            .Select(item => JsonDocument.Parse(item.PayloadJson))
            .ToArray();
        try
        {
            var scalar = relations.Single(document =>
                document.RootElement.GetProperty("semantic").GetProperty("name").GetString() == "supplier");
            var collection = relations.Single(document =>
                document.RootElement.GetProperty("semantic").GetProperty("name").GetString() == "suppliers");
            assert(
                scalar.RootElement.GetProperty("semantic").GetProperty("min").GetString() == "1"
                && scalar.RootElement.GetProperty("semantic").GetProperty("max").GetString() == "1"
                && scalar.RootElement.GetProperty("evidenceIds").EnumerateArray()
                    .Select(item => item.GetString() ?? "")
                    .Count(id => claims.Any(claim => claim.ClaimId == id)) == 3,
                "a scalar typed reference with direct required/non-null claims should be 1..1 and retain all direct evidence");
            assert(
                collection.RootElement.GetProperty("semantic").GetProperty("min").GetString() == "0"
                && collection.RootElement.GetProperty("semantic").GetProperty("max").GetString() == "many",
                "a collection typed reference without a direct required claim should be 0..many");
        }
        finally
        {
            foreach (var relation in relations)
            {
                relation.Dispose();
            }
        }

        assert(!semanticCandidates.Any(item => item.SubjectKind == OntologySemanticCandidateKinds.Rule),
            "Bean Validation and persistence-column annotations must not become standalone business rule candidates");
        assert(snapshot.Diagnostics.Count(item =>
                item.Kind == "direct-only"
                && item.SubjectKind == OntologySemanticCandidateKinds.Relation) == 2,
            "relations without matching use-case slices should be marked with direct-only diagnostics");

        var lifecycleCandidate = semanticCandidates.Single(
            item => item.SubjectKind == OntologySemanticCandidateKinds.Lifecycle);
        using (var lifecycle = JsonDocument.Parse(lifecycleCandidate.PayloadJson))
        {
            var semantic = lifecycle.RootElement.GetProperty("semantic");
            var states = semantic.GetProperty("states")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetString() ?? "")
                .ToArray();
            var transitions = semantic.GetProperty("transitions").EnumerateArray().ToArray();
            assert(
                semantic.GetProperty("subjectConceptId").GetString() == Record
                && semantic.GetProperty("stateProperty").GetString() == "status"
                && semantic.GetProperty("initialState").GetString() == "DRAFT"
                && states.SequenceEqual(new[] { "DRAFT", "APPROVED", "REJECTED" }, StringComparer.Ordinal),
                "state field and ordered finite enum values should form one mapped lifecycle with multiple states");
            assert(transitions.Length == 0,
                "direct assignments without a matching guarded cross-file use-case slice should retain lifecycle states but not form complete transition tuples");
            assert(
                semantic.GetProperty("descriptionZh").GetString()!.Contains("生命周期", StringComparison.Ordinal)
                && lifecycle.RootElement.GetProperty("rationale").GetString()!.Contains("不提出状态迁移", StringComparison.Ordinal),
                "lifecycle descriptions and no-complete-transition rationale should be emitted in Chinese");
            assert(lifecycle.RootElement.GetProperty("evidenceIds").GetArrayLength() == 4,
                "lifecycle evidence should include its field and all finite values, excluding setter/assignment-only mutations");
        }
        assert(!lifecycleCandidate.PayloadJson.Contains("singleStatus", StringComparison.Ordinal)
                && !lifecycleCandidate.PayloadJson.Contains("stringStatus", StringComparison.Ordinal)
                && !lifecycleCandidate.PayloadJson.Contains("approveWithoutAssignment", StringComparison.Ordinal)
                && !lifecycleCandidate.PayloadJson.Contains("UNKNOWN", StringComparison.Ordinal)
                && !lifecycleCandidate.PayloadJson.Contains("approveWrongEnum", StringComparison.Ordinal)
                && !lifecycleCandidate.PayloadJson.Contains("rejectUnknownFrom", StringComparison.Ordinal),
            "String status, a one-value enum, method names without assignment, mismatched enum values/types, and unknown from-values must not add lifecycle semantics");
        assert(!semanticCandidates.Any(item =>
                item.PayloadJson.Contains("Warehouse", StringComparison.Ordinal)
                || item.PayloadJson.Contains("route", StringComparison.OrdinalIgnoreCase)
                || item.PayloadJson.Contains("transaction", StringComparison.OrdinalIgnoreCase)
                || item.PayloadJson.Contains("service", StringComparison.OrdinalIgnoreCase)),
            "unmapped targets and indirect service/route/transaction observations must not become semantic candidates");

        var stableIds = semanticCandidates.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray();
        await projector.ProjectAsync(request);
        var repeated = await store.ReadExportableAsync(OntologyId);
        assert(repeated.Candidates
                .Where(item => item.Id.StartsWith("candidate:semantic:", StringComparison.Ordinal))
                .Select(item => item.Id)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(stableIds, StringComparer.Ordinal),
            "repeated projection of identical direct claims should preserve candidate identities");
        var sentinel = await innerStore.RunAsync(
            """?[value] := *depa_projection_sentinel{id: "sentinel", value}""");
        assert(guardedStore.ForbiddenAccessCount == 0
               && sentinel.Rows.Single()[0].GetString() == "unchanged",
            "semantic projection must neither query nor mutate depa_* relations");
    }

    private static async Task ProjectsBusinessRulesOnlyFromGuardedUseCaseSlicesAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        var recordParameterPayload = JsonSerializer.Serialize(new
        {
            collection = false,
            member = "request",
            ownerSymbolId = "symbol:service:create",
            rawType = "RecordCreateRequest",
            usageKind = "parameter",
            resolvedTypeName = "RecordCreateRequest",
            resolvedTypeSymbolId = "symbol:record",
        });
        var guardPayload = JsonSerializer.Serialize(new
        {
            effectKind = "throw",
            effectMessage = "只有草稿记录可以提交",
            effectSource = "throw new BusinessException(\"只有草稿记录可以提交\")",
            method = "createRecord",
            methodSymbolId = "symbol:service:create",
            ownerSymbolId = "symbol:service",
            predicateSource = "request.status() != RecordStatus.DRAFT",
        });
        var annotationPayload = JsonSerializer.Serialize(new
        {
            annotation = "NotNull",
            member = "recordCode",
            @operator = "required",
            ownerSymbolId = "symbol:record",
            value = true,
        });
        var recordParameter = Claim("file:service", "symbol:service:create:param", CodeSemanticClaimKinds.TypedReference, recordParameterPayload, 20);
        var guard = Claim("file:service", "symbol:service:create", CodeSemanticClaimKinds.BusinessGuard, guardPayload, 24);
        var annotation = Claim("file:record", "symbol:record:code", CodeSemanticClaimKinds.ValidationConstraint, annotationPayload, 8);
        await SeedGuardRuleGraphAsync(om, [recordParameter, guard, annotation]);

        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(BaseGeneration());
        var projector = new BusinessOntologySemanticProjector(om, store);
        var request = new BusinessOntologySemanticProjectionRequest(
            OntologyId,
            "semantic-guard-rule",
            "semantic-guard-rule-fingerprint",
            "2026-07-18T13:00:00Z");
        var first = await projector.ProjectAsync(request);
        var snapshot = await store.ReadExportableAsync(OntologyId);

        var rules = snapshot.Candidates
            .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Rule)
            .ToArray();
        assert(first.RuleCandidates == 1 && rules.Length == 1,
            "only AST-anchored business_guard claims in a matching cross-file slice should produce business rule candidates");
        var rule = rules.Single();
        using (var document = JsonDocument.Parse(rule.PayloadJson))
        {
            var semantic = document.RootElement.GetProperty("semantic");
            assert(semantic.GetProperty("subjectConceptId").GetString() == Record
                    && semantic.GetProperty("ruleKind").GetString() == "businessCondition"
                    && semantic.GetProperty("predicate").GetProperty("source").GetString() == "request.status() != RecordStatus.DRAFT"
                    && semantic.GetProperty("effect").GetProperty("type").GetString() == "reject"
                    && semantic.GetProperty("effect").GetProperty("mechanism").GetString() == "throw"
                    && semantic.GetProperty("effect").GetProperty("messageZh").GetString() == "只有草稿记录可以提交",
                "business rule payload should retain governed concept, business precondition, reject/allow effect, and direct message");
            assert(semantic.GetProperty("effect").GetProperty("allowedWhen").GetProperty("not").GetString()
                    == "request.status() != RecordStatus.DRAFT",
                "business rule effect should expose the allowed condition as the negation of the reject guard");
        }
        assert(rule.EvidenceIds.Contains(guard.ClaimId, StringComparer.Ordinal)
                && rule.EvidenceIds.Contains(recordParameter.ClaimId, StringComparer.Ordinal)
                && rule.EvidenceIds.Any(id => id.StartsWith("evidence:usecase-slice-", StringComparison.Ordinal)
                    && id.Count(ch => ch == ':') == 1),
            "business rules should carry guard, typed subject, and cross-file use-case slice evidence");
        assert(!snapshot.Candidates.Any(item =>
                item.SubjectKind == OntologySemanticCandidateKinds.Rule
                && item.PayloadJson.Contains("recordCode", StringComparison.Ordinal)),
            "Bean Validation and persistence annotations must not be restored as business rules");
        var stableRuleId = rule.Id;
        await projector.ProjectAsync(request);
        var repeated = await store.ReadExportableAsync(OntologyId);
        assert(repeated.Candidates.Any(item => item.Id == stableRuleId),
            "business guard rule candidate ids should be stable across repeated projection");

        using var ambiguousDb = new CozoDb("mem", "");
        var ambiguousOm = new CozoOm(ambiguousDb);
        await ambiguousOm.InitCodeKnowledgeAsync();
        var supplierParameterPayload = JsonSerializer.Serialize(new
        {
            collection = false,
            member = "supplier",
            ownerSymbolId = "symbol:service:create",
            rawType = "SupplierEntity",
            usageKind = "parameter",
            resolvedTypeName = "SupplierEntity",
            resolvedTypeSymbolId = "symbol:supplier",
        });
        var supplierParameter = Claim("file:service", "symbol:service:create:supplier", CodeSemanticClaimKinds.TypedReference, supplierParameterPayload, 21);
        await SeedGuardRuleGraphAsync(ambiguousOm, [recordParameter, supplierParameter, guard]);
        var ambiguousStore = new BusinessOntologyStore(ambiguousOm);
        await ambiguousStore.ReplaceGenerationAsync(BaseGeneration());
        await new BusinessOntologySemanticProjector(ambiguousOm, ambiguousStore)
            .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "semantic-guard-rule-ambiguous",
                "semantic-guard-rule-ambiguous-fingerprint",
                "2026-07-18T13:05:00Z"));
        var ambiguousSnapshot = await ambiguousStore.ReadExportableAsync(OntologyId);
        var resolvedRule = ambiguousSnapshot.Candidates.Single(item => item.SubjectKind == OntologySemanticCandidateKinds.Rule);
        using var resolvedRuleDocument = JsonDocument.Parse(resolvedRule.PayloadJson);
        assert(resolvedRuleDocument.RootElement.GetProperty("semantic").GetProperty("subjectConceptId").GetString() == Record
                && !ambiguousSnapshot.Diagnostics.Any(item =>
                    item.Kind == "ambiguous-business-rule-subject"
                    && item.SubjectKind == OntologySemanticCandidateKinds.Rule),
            "guard rules should ignore method typed references absent from predicateSource and bind to the explicit predicate receiver");
    }

    private static async Task BindsBusinessRuleSubjectFromPredicateSourceAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        var recordBorrow = OntologyId + ".RecordBorrow";
        var recordTransfer = OntologyId + ".RecordTransfer";
        var borrowParameter = Claim("file:service", "symbol:service:borrow:param", CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = false,
                member = "borrow",
                ownerSymbolId = "symbol:service:create",
                rawType = "RecordBorrowEntity",
                usageKind = "parameter",
                resolvedTypeName = "RecordBorrowEntity",
                resolvedTypeSymbolId = "symbol:borrow",
            }), 20);
        var transferParameter = Claim("file:service", "symbol:service:transfer:param", CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = false,
                member = "transfer",
                ownerSymbolId = "symbol:service:create",
                rawType = "RecordTransferEntity",
                usageKind = "parameter",
                resolvedTypeName = "RecordTransferEntity",
                resolvedTypeSymbolId = "symbol:transfer",
            }), 21);
        var supplierParameter = Claim("file:service", "symbol:service:supplier:param", CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = false,
                member = "supplier",
                ownerSymbolId = "symbol:service:create",
                rawType = "SupplierEntity",
                usageKind = "parameter",
                resolvedTypeName = "SupplierEntity",
                resolvedTypeSymbolId = "symbol:supplier",
            }), 22);
        var borrowGuard = Claim("file:service", "symbol:service:create", CodeSemanticClaimKinds.BusinessGuard,
            JsonSerializer.Serialize(new
            {
                effectKind = "throw",
                effectMessage = "借用单状态不允许提交",
                effectSource = "throw new BusinessException(\"借用单状态不允许提交\")",
                method = "createRecord",
                methodSymbolId = "symbol:service:create",
                ownerSymbolId = "symbol:service",
                predicateSource = "borrow.getStatus() != BorrowStatus.DRAFT",
            }), 24);
        var transferGuard = Claim("file:service", "symbol:service:create", CodeSemanticClaimKinds.BusinessGuard,
            JsonSerializer.Serialize(new
            {
                effectKind = "throw",
                effectMessage = "调拨单状态不允许提交",
                effectSource = "throw new BusinessException(\"调拨单状态不允许提交\")",
                method = "createRecord",
                methodSymbolId = "symbol:service:create",
                ownerSymbolId = "symbol:service",
                predicateSource = "transfer.auditStatus() != TransferStatus.NEW",
            }), 26);
        await SeedGuardRuleGraphAsync(om, [borrowParameter, transferParameter, supplierParameter, borrowGuard, transferGuard]);

        var baseGeneration = BaseGeneration();
        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(baseGeneration with
        {
            Concepts =
            [
                .. baseGeneration.Concepts,
                new BusinessOntologyConcept(recordBorrow, "businessObject", "记录借用", "记录借用业务概念。", "accepted", 0.95, ["base:borrow"]),
                new BusinessOntologyConcept(recordTransfer, "businessObject", "记录调拨", "记录调拨业务概念。", "accepted", 0.95, ["base:transfer"]),
            ],
            Mappings =
            [
                .. baseGeneration.Mappings,
                new BusinessOntologyMapping(OntologyId + ".Mapping.RecordBorrow", "concept", recordBorrow, "representedBy", "repo:record", "java", "class", "symbol:borrow", "src/RecordBorrowEntity.java", "treesitter", 0.95, "accepted", ["base:borrow"]),
                new BusinessOntologyMapping(OntologyId + ".Mapping.RecordTransfer", "concept", recordTransfer, "representedBy", "repo:record", "java", "class", "symbol:transfer", "src/RecordTransferEntity.java", "treesitter", 0.95, "accepted", ["base:transfer"]),
            ],
            Evidence =
            [
                .. baseGeneration.Evidence,
                new BusinessOntologyEvidence("base:borrow", "repo:record", "src/RecordBorrowEntity.java", "symbol:borrow", 1, 40, "contractual", "treesitter", 0.95, "code", "记录借用类型。"),
                new BusinessOntologyEvidence("base:transfer", "repo:record", "src/RecordTransferEntity.java", "symbol:transfer", 1, 40, "contractual", "treesitter", 0.95, "code", "记录调拨类型。"),
            ],
        });

        await new BusinessOntologySemanticProjector(om, store)
            .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "semantic-predicate-subject",
                "semantic-predicate-subject-fingerprint",
                "2026-07-18T15:05:00Z"));
        var snapshot = await store.ReadExportableAsync(OntologyId);
        var ruleSubjects = snapshot.Candidates
            .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Rule)
            .Select(item =>
            {
                using var document = JsonDocument.Parse(item.PayloadJson);
                var semantic = document.RootElement.GetProperty("semantic");
                return (
                    Subject: semantic.GetProperty("subjectConceptId").GetString() ?? "",
                    Predicate: semantic.GetProperty("predicate").GetProperty("source").GetString() ?? "");
            })
            .OrderBy(item => item.Predicate, StringComparer.Ordinal)
            .ToArray();

        assert(ruleSubjects.Length == 2
                && ruleSubjects.Any(item => item.Predicate.Contains("borrow", StringComparison.Ordinal) && item.Subject == recordBorrow)
                && ruleSubjects.Any(item => item.Predicate.Contains("transfer", StringComparison.Ordinal) && item.Subject == recordTransfer)
                && !snapshot.Diagnostics.Any(item => item.Kind == "ambiguous-business-rule-subject"),
            "rule subject binding should prefer receiver/member variables explicitly present in predicateSource instead of treating every slice concept as a subject");
    }

    private static async Task BoundsBusinessRuleRationaleAcrossManyUseCaseSlicesAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        var recordParameterPayload = JsonSerializer.Serialize(new
        {
            collection = false,
            member = "request",
            ownerSymbolId = "symbol:service:create",
            rawType = "RecordCreateRequest",
            usageKind = "parameter",
            resolvedTypeName = "RecordCreateRequest",
            resolvedTypeSymbolId = "symbol:record",
        });
        var guardPayload = JsonSerializer.Serialize(new
        {
            effectKind = "throw",
            effectMessage = "只有草稿记录可以提交",
            effectSource = "throw new BusinessException(\"只有草稿记录可以提交\")",
            method = "createRecord",
            methodSymbolId = "symbol:service:create",
            ownerSymbolId = "symbol:service",
            predicateSource = "request.status() != RecordStatus.DRAFT",
        });
        var recordParameter = Claim(
            "file:service",
            "symbol:service:create:param",
            CodeSemanticClaimKinds.TypedReference,
            recordParameterPayload,
            20);
        var guard = Claim(
            "file:service",
            "symbol:service:create",
            CodeSemanticClaimKinds.BusinessGuard,
            guardPayload,
            24);

        var routeSymbols = Enumerable.Range(0, 36)
            .Select(index => $"symbol:route:create:{index:D2}")
            .ToArray();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:controller", "repo:record", "src/RecordController.java", "java"),
                new CodeFileFact("file:service", "repo:record", "src/RecordService.java", "java"),
                new CodeFileFact("file:repository", "repo:record", "src/RecordRepository.java", "java"),
                new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:controller", "file:controller", "RecordController", "class", 1, 300, Lang: "java"),
                .. routeSymbols.Select((symbol, index) =>
                    new CodeSymbolFact(symbol, "file:controller", $"createRecordVariant{index:D2}", "method", 12 + index, 12 + index, ParentId: "symbol:controller", Lang: "java")),
                new CodeSymbolFact("symbol:service", "file:service", "RecordService", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:service:create", "file:service", "createRecord", "method", 18, 44, ParentId: "symbol:service", Lang: "java"),
                new CodeSymbolFact("symbol:repo", "file:repository", "RecordRepository", "interface", 1, 40, Lang: "java"),
                new CodeSymbolFact("symbol:repo:save", "file:repository", "save", "method", 8, 8, ParentId: "symbol:repo", Lang: "java"),
                new CodeSymbolFact("symbol:record", "file:record", "RecordEntity", "class", 1, 80, Lang: "java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "spring_annotation", "controller"),
                new CodeEdgeFact("symbol:service", "spring:role:service", "SPRING_ROLE", "file:service", 1, 0.98, "spring_annotation", "service"),
                new CodeEdgeFact("symbol:repo", "spring:role:repository", "SPRING_ROLE", "file:repository", 1, 0.98, "spring_annotation", "repository"),
                new CodeEdgeFact("symbol:record", "spring:role:entity", "SPRING_ROLE", "file:record", 1, 0.98, "spring_annotation", "entity"),
                .. routeSymbols.Select((symbol, index) =>
                    new CodeEdgeFact(symbol, "symbol:service:create", CodeEdgeKinds.Calls, "file:controller", 16 + index, 1.0, "treesitter", "controller calls service")),
                new CodeEdgeFact("symbol:service:create", "symbol:repo:save", CodeEdgeKinds.Calls, "file:service", 24, 1.0, "treesitter", "service calls repository"),
                new CodeEdgeFact("symbol:repo:save", "symbol:record", CodeEdgeKinds.Calls, "file:repository", 8, 1.0, "treesitter", "repository writes entity"),
            ],
            EntryPoints: routeSymbols
                .Select((symbol, index) => new CodeEntryPointFact(
                    symbol,
                    "http_route",
                    $"POST /api/records/create/long-business-action-variant-{index:D2}/with-a-very-verbose-stable-route-name"))
                .ToArray(),
            SemanticClaims: [recordParameter, guard]));

        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(BaseGeneration());
        await new BusinessOntologySemanticProjector(om, store)
            .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "semantic-guard-many-slices",
                "semantic-guard-many-slices-fingerprint",
                "2026-07-18T16:00:00Z"));
        var snapshot = await store.ReadExportableAsync(OntologyId);
        var rule = snapshot.Candidates.Single(item => item.SubjectKind == OntologySemanticCandidateKinds.Rule);
        using var document = JsonDocument.Parse(rule.PayloadJson);
        var rationale = document.RootElement.GetProperty("rationale").GetString() ?? "";

        assert(rationale.Length <= 2_000
                && rationale.Contains("totalSlices=36", StringComparison.Ordinal)
                && rationale.Contains("omittedSlices=32", StringComparison.Ordinal)
                && rationale.Contains("long-business-action-variant-00", StringComparison.Ordinal)
                && rationale.Contains("long-business-action-variant-35", StringComparison.Ordinal),
            "business rule rationale should keep stable representative actions and total counts while staying within the validator contract");
        _ = new OntologySemanticCandidateValidator().Validate(
            rule.PayloadJson,
            snapshot.Concepts.Select(item => item.Id),
            snapshot.Evidence.Select(item => item.Id));
    }

    private static async Task ProjectsRelationsFromCanonicalEndpointsAndUseCaseSlicesAsync(Action<bool, string> assert)
    {
        const string RecordDto = "symbol:record:dto";
        const string SupplierDto = "symbol:supplier:dto";
        const string Warehouse = OntologyId + ".Warehouse";
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        var supplierEntityPayload = JsonSerializer.Serialize(new
        {
            collection = false,
            member = "supplier",
            ownerSymbolId = "symbol:record",
            rawType = "SupplierEntity",
            usageKind = "field",
            resolvedTypeName = "SupplierEntity",
            resolvedTypeSymbolId = "symbol:supplier",
        });
        var supplierDtoPayload = JsonSerializer.Serialize(new
        {
            collection = false,
            member = "supplier",
            ownerSymbolId = RecordDto,
            rawType = "SupplierDto",
            usageKind = "field",
            resolvedTypeName = "SupplierDto",
            resolvedTypeSymbolId = SupplierDto,
        });
        var directOnlyPayload = JsonSerializer.Serialize(new
        {
            collection = false,
            member = "billingSupplier",
            ownerSymbolId = RecordDto,
            rawType = "WarehouseEntity",
            usageKind = "field",
            resolvedTypeName = "WarehouseEntity",
            resolvedTypeSymbolId = "symbol:warehouse",
        });
        var parameterPayload = JsonSerializer.Serialize(new
        {
            collection = false,
            member = "request",
            ownerSymbolId = "symbol:service:create",
            methodSymbolId = "symbol:service:create",
            rawType = "RecordDto",
            referenceKind = "parameter",
            resolvedTypeName = "RecordDto",
            resolvedTypeSymbolId = RecordDto,
        });
        var supplierUsePayload = JsonSerializer.Serialize(new
        {
            collection = false,
            declaringSymbolId = "symbol:service:create",
            member = "supplier",
            ownerSymbolId = "symbol:service",
            methodSymbolId = "symbol:service:create",
            rawType = "SupplierDto",
            referenceKind = "local",
            resolvedTypeName = "SupplierDto",
            resolvedTypeSymbolId = SupplierDto,
        });
        var returnPayload = JsonSerializer.Serialize(new
        {
            collection = false,
            member = "createdRecord",
            ownerSymbolId = "symbol:service:create",
            methodSymbolId = "symbol:service:create",
            rawType = "RecordDto",
            referenceKind = "return",
            resolvedTypeName = "RecordDto",
            resolvedTypeSymbolId = RecordDto,
        });
        var unmappedEndpointPayload = JsonSerializer.Serialize(new
        {
            collection = false,
            member = "reviewer",
            ownerSymbolId = "symbol:record",
            rawType = "UserDto",
            usageKind = "field",
            resolvedTypeName = "UserDto",
            resolvedTypeSymbolId = "symbol:user:dto",
        });
        var routeOnlyPayload = JsonSerializer.Serialize(new
        {
            httpMethods = new[] { "GET" },
            paths = new[] { "/api/records" },
            site = "RecordController.list",
            symbolId = "symbol:route:list",
        });
        var supplierClaim = Claim("file:record", "symbol:record:supplier", CodeSemanticClaimKinds.TypedReference, supplierEntityPayload, 10);
        var supplierDtoClaim = Claim("file:dto", RecordDto + ":supplier", CodeSemanticClaimKinds.TypedReference, supplierDtoPayload, 6);
        var directOnlyClaim = Claim("file:dto", RecordDto + ":billingSupplier", CodeSemanticClaimKinds.TypedReference, directOnlyPayload, 8);
        var parameterClaim = Claim("file:service", "symbol:service:create", CodeSemanticClaimKinds.TypedReference, parameterPayload, 18);
        var supplierUseClaim = Claim("file:service", "symbol:service:create", CodeSemanticClaimKinds.TypedReference, supplierUsePayload, 19);
        var returnClaim = Claim("file:service", "symbol:service:create", CodeSemanticClaimKinds.TypedReference, returnPayload, 20);
        var unmappedClaim = Claim("file:record", "symbol:record:reviewer", CodeSemanticClaimKinds.TypedReference, unmappedEndpointPayload, 12);
        var routeOnlyClaim = Claim("file:controller", "symbol:route:list", CodeSemanticClaimKinds.RouteBinding, routeOnlyPayload, 40);

        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:controller", "repo:record", "src/RecordController.java", "java"),
                new CodeFileFact("file:service", "repo:record", "src/RecordService.java", "java"),
                new CodeFileFact("file:repository", "repo:record", "src/RecordRepository.java", "java"),
                new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java"),
                new CodeFileFact("file:supplier", "repo:record", "src/SupplierEntity.java", "java"),
                new CodeFileFact("file:dto", "repo:record", "src/RecordDto.java", "java"),
                new CodeFileFact("file:supplier-dto", "repo:record", "src/SupplierDto.java", "java"),
                new CodeFileFact("file:warehouse", "repo:record", "src/WarehouseEntity.java", "java"),
                new CodeFileFact("file:user-dto", "repo:record", "src/UserDto.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:controller", "file:controller", "RecordController", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:route:create", "file:controller", "createRecord", "method", 12, 24, ParentId: "symbol:controller", Lang: "java"),
                new CodeSymbolFact("symbol:route:list", "file:controller", "listRecords", "method", 32, 44, ParentId: "symbol:controller", Lang: "java"),
                new CodeSymbolFact("symbol:service", "file:service", "RecordService", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:service:create", "file:service", "createRecord", "method", 18, 44, ParentId: "symbol:service", Lang: "java"),
                new CodeSymbolFact("symbol:repo", "file:repository", "RecordRepository", "interface", 1, 40, Lang: "java"),
                new CodeSymbolFact("symbol:repo:save", "file:repository", "save", "method", 8, 8, ParentId: "symbol:repo", Lang: "java"),
                new CodeSymbolFact("symbol:record", "file:record", "RecordEntity", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:supplier", "file:supplier", "SupplierEntity", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact(RecordDto, "file:dto", "RecordDto", "class", 1, 24, Lang: "java"),
                new CodeSymbolFact(SupplierDto, "file:supplier-dto", "SupplierDto", "class", 1, 18, Lang: "java"),
                new CodeSymbolFact("symbol:warehouse", "file:warehouse", "WarehouseEntity", "class", 1, 18, Lang: "java"),
                new CodeSymbolFact("symbol:user:dto", "file:user-dto", "UserDto", "class", 1, 18, Lang: "java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "spring_annotation", "controller"),
                new CodeEdgeFact("symbol:service", "spring:role:service", "SPRING_ROLE", "file:service", 1, 0.98, "spring_annotation", "service"),
                new CodeEdgeFact("symbol:repo", "spring:role:repository", "SPRING_ROLE", "file:repository", 1, 0.98, "spring_annotation", "repository"),
                new CodeEdgeFact("symbol:record", "spring:role:entity", "SPRING_ROLE", "file:record", 1, 0.98, "spring_annotation", "entity"),
                new CodeEdgeFact("symbol:route:create", "symbol:service:create", CodeEdgeKinds.Calls, "file:controller", 16, 1.0, "treesitter", "controller calls service"),
                new CodeEdgeFact("symbol:service:create", "symbol:repo:save", CodeEdgeKinds.Calls, "file:service", 24, 1.0, "treesitter", "service calls repository"),
                new CodeEdgeFact("symbol:repo:save", "symbol:record", CodeEdgeKinds.Calls, "file:repository", 8, 1.0, "treesitter", "repository writes entity"),
            ],
            EntryPoints:
            [
                new CodeEntryPointFact("symbol:route:create", "http_route", "POST /api/records"),
                new CodeEntryPointFact("symbol:route:list", "http_route", "GET /api/records"),
            ],
            SemanticClaims:
            [
                supplierClaim,
                supplierDtoClaim,
                directOnlyClaim,
                parameterClaim,
                supplierUseClaim,
                returnClaim,
                unmappedClaim,
                routeOnlyClaim,
            ]));

        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(BaseGeneration() with
        {
            Mappings =
            [
                .. BaseGeneration().Mappings,
                new BusinessOntologyMapping(OntologyId + ".Mapping.RecordDto", "concept", Record, "representedBy", "repo:record", "java", "class", RecordDto, "src/RecordDto.java", "treesitter", 0.91, "accepted", ["base:record-dto"]),
                new BusinessOntologyMapping(OntologyId + ".Mapping.SupplierDto", "concept", Supplier, "representedBy", "repo:record", "java", "class", SupplierDto, "src/SupplierDto.java", "treesitter", 0.91, "accepted", ["base:supplier-dto"]),
                new BusinessOntologyMapping(OntologyId + ".Mapping.Warehouse", "concept", Warehouse, "representedBy", "repo:record", "java", "class", "symbol:warehouse", "src/WarehouseEntity.java", "treesitter", 0.91, "accepted", ["base:warehouse"]),
            ],
            Evidence =
            [
                .. BaseGeneration().Evidence,
                new BusinessOntologyEvidence("base:record-dto", "repo:record", "src/RecordDto.java", RecordDto, 1, 24, "contractual", "treesitter", 0.91, "code", "记录 DTO 映射。"),
                new BusinessOntologyEvidence("base:supplier-dto", "repo:record", "src/SupplierDto.java", SupplierDto, 1, 18, "contractual", "treesitter", 0.91, "code", "供应商 DTO 映射。"),
                new BusinessOntologyEvidence("base:warehouse", "repo:record", "src/WarehouseEntity.java", "symbol:warehouse", 1, 18, "contractual", "treesitter", 0.91, "code", "仓库实体映射。"),
            ],
            Concepts =
            [
                .. BaseGeneration().Concepts,
                new BusinessOntologyConcept(Warehouse, "businessObject", "仓库", "仓库业务概念。", "accepted", 0.91, ["base:warehouse"]),
            ],
        });

        var projector = new BusinessOntologySemanticProjector(om, store);
        var request = new BusinessOntologySemanticProjectionRequest(
            OntologyId,
            "semantic-slice-1",
            "semantic-slice-fingerprint",
            "2026-07-18T12:00:00Z",
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
            ]);
        await projector.ProjectAsync(request);
        var first = await store.ReadExportableAsync(OntologyId);
        var repeatedResult = await projector.ProjectAsync(request);
        var repeated = await store.ReadExportableAsync(OntologyId);

        var relations = first.Candidates
            .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Relation)
            .Select(item => (Candidate: item, Document: JsonDocument.Parse(item.PayloadJson)))
            .ToArray();
        try
        {
            assert(relations.Length == 2, "only typed_reference claims with canonical owner and target endpoints should become relation candidates, with multi-carrier evidence merged by canonical relation identity");
            assert(!relations.Any(item =>
                    item.Candidate.PayloadJson.Contains("reviewer", StringComparison.Ordinal)
                    || item.Candidate.PayloadJson.Contains("listRecords", StringComparison.Ordinal)),
                "unmapped endpoints and route-only/frontend-only observations must not create relation candidates");
            var supplier = relations.Single(item =>
                item.Document.RootElement.GetProperty("semantic").GetProperty("name").GetString() == "supplier"
                && item.Document.RootElement.GetProperty("semantic").GetProperty("fromConceptId").GetString() == Record
                && item.Document.RootElement.GetProperty("semantic").GetProperty("toConceptId").GetString() == Supplier);
            assert(supplier.Candidate.EvidenceIds.Contains(supplierClaim.ClaimId, StringComparer.Ordinal)
                    && supplier.Candidate.EvidenceIds.Contains(supplierDtoClaim.ClaimId, StringComparer.Ordinal)
                    && supplier.Candidate.EvidenceIds.Contains(parameterClaim.ClaimId, StringComparer.Ordinal)
                    && supplier.Candidate.EvidenceIds.Contains(supplierUseClaim.ClaimId, StringComparer.Ordinal)
                    && supplier.Candidate.EvidenceIds.Contains(returnClaim.ClaimId, StringComparer.Ordinal)
                    && supplier.Candidate.EvidenceIds.Contains("base:record-dto", StringComparer.Ordinal)
                    && supplier.Candidate.EvidenceIds.Contains("frontend:record-create", StringComparer.Ordinal)
                    && supplier.Candidate.EvidenceIds.Any(id => id.StartsWith("evidence:usecase-slice-", StringComparison.Ordinal)
                        && id.Count(ch => ch == ':') == 1),
                "endpoint co-occurrence in a real cross-file use-case slice should enrich the structural relation without requiring its field declaration claim to enter the call slice");
            assert(supplier.Candidate.Reason.Contains("POST /api/records", StringComparison.Ordinal)
                    && supplier.Candidate.Reason.Contains("spring:role:service", StringComparison.Ordinal)
                    && supplier.Candidate.Reason.Contains("spring:role:repository", StringComparison.Ordinal)
                    && !supplier.Candidate.Reason.Contains("direct-only", StringComparison.OrdinalIgnoreCase),
                "slice-enriched relation rationale should name action and service/repository roles without direct-only wording");
            assert(!first.Diagnostics.Any(item =>
                    item.Kind == "direct-only"
                    && item.ConflictKey == Record + ".supplier"),
                "slice-enriched relation should not be marked direct-only");
            assert(first.Diagnostics.Any(item =>
                    item.Kind == "direct-only"
                    && item.ConflictKey == Record + ".billingSupplier"),
                "canonical endpoint references without a matching business use-case slice should remain lower-confidence direct-only diagnostics");
            assert(repeatedResult.RelationCandidates == 2
                    && repeatedResult.CrossLayerRelationCandidates == 1
                    && repeatedResult.DirectOnlyRelationDiagnostics == 1,
                "projection result must distinguish one actual cross-layer relation from one direct-only diagnostic");
            assert(repeated.Candidates
                    .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Relation)
                    .Select(item => item.Id)
                    .Order(StringComparer.Ordinal)
                    .SequenceEqual(
                        first.Candidates
                            .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Relation)
                            .Select(item => item.Id)
                            .Order(StringComparer.Ordinal),
                        StringComparer.Ordinal),
                "slice-enriched relation ids should be stable across repeated projection");
            assert(JsonSerializer.Serialize(first.EvidenceReferences)
                    == JsonSerializer.Serialize(repeated.EvidenceReferences),
                "slice-enriched evidence references should be deterministic across repeated projection");
        }
        finally
        {
            foreach (var relation in relations)
            {
                relation.Document.Dispose();
            }
        }

        static CodeSemanticClaimFact Claim(string fileId, string subjectId, string kind, string payload, int line) =>
            new(
                CodeSemanticClaimIdentity.Create(fileId, kind, payload, line, line),
                subjectId,
                kind,
                payload,
                fileId,
                line,
                line,
                0.96,
                "treesitter",
                $"direct {kind}");
    }

    private static async Task RejectsOperationTypedReferencesAsRelationsAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        var field = Claim(
            "file:record",
            "symbol:record:supplier",
            CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = false,
                member = "supplier",
                ownerSymbolId = "symbol:record",
                rawType = "SupplierEntity",
                usageKind = "field",
                resolvedTypeName = "SupplierEntity",
                resolvedTypeSymbolId = "symbol:supplier",
            }),
            8);
        var recordComponent = Claim(
            "file:record-record",
            "symbol:record-record",
            CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = false,
                member = "supplier",
                ownerSymbolId = "symbol:record",
                rawType = "SupplierEntity",
                usageKind = "record_component",
                resolvedTypeName = "SupplierEntity",
                resolvedTypeSymbolId = "symbol:supplier",
            }),
            9);
        var operationRefs = new[]
        {
            ("symbol:record-service:param", "recordInfo", "parameter", "RecordInfo"),
            ("symbol:record-service:return", "queryByRecordNumbers", "return", "RecordInfo"),
            ("symbol:record-service:local", "recordInfo", "local", "RecordInfo"),
            ("symbol:record-service:method", "getCategoryPath", "method", "RecordCategory"),
            ("symbol:record-self:method", "findNewlyByRecordNumber", "method", "RecordInfo"),
        }.Select((item, index) => Claim(
            "file:service",
            item.Item1,
            CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = item.Item2 == "queryByRecordNumbers",
                member = item.Item2,
                ownerSymbolId = item.Item1.Contains("self", StringComparison.Ordinal)
                    ? "symbol:record"
                    : "symbol:record-service:query",
                rawType = item.Item4,
                usageKind = item.Item3,
                resolvedTypeName = item.Item4,
                resolvedTypeSymbolId = item.Item4 == "RecordCategory" ? "symbol:category" : "symbol:record",
            }),
            20 + index)).ToArray();

        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:record", "repo:record", "src/RecordInfo.java", "java"),
                new CodeFileFact("file:record-record", "repo:record", "src/RecordRecord.java", "java"),
                new CodeFileFact("file:supplier", "repo:record", "src/SupplierEntity.java", "java"),
                new CodeFileFact("file:category", "repo:record", "src/RecordCategory.java", "java"),
                new CodeFileFact("file:service", "repo:record", "src/RecordInfoService.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:record", "file:record", "RecordInfo", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:supplier", "file:supplier", "SupplierEntity", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact("symbol:category", "file:category", "RecordCategory", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact("symbol:record-service", "file:service", "RecordInfoService", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:record-service:query", "file:service", "queryByRecordNumbers", "method", 20, 40, ParentId: "symbol:record-service", Lang: "java"),
            ],
            SemanticClaims: [field, recordComponent, .. operationRefs]));

        var baseGeneration = BaseGeneration();
        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(baseGeneration with
        {
            Concepts =
            [
                .. baseGeneration.Concepts,
                new BusinessOntologyConcept(OntologyId + ".RecordCategory", "businessObject", "记录分类", "记录分类业务概念。", "accepted", 0.95, ["base:category"]),
            ],
            Mappings =
            [
                .. baseGeneration.Mappings,
                new BusinessOntologyMapping(OntologyId + ".Mapping.RecordCategory", "concept", OntologyId + ".RecordCategory", "representedBy", "repo:record", "java", "class", "symbol:category", "src/RecordCategory.java", "treesitter", 0.95, "accepted", ["base:category"]),
            ],
            Evidence =
            [
                .. baseGeneration.Evidence,
                new BusinessOntologyEvidence("base:category", "repo:record", "src/RecordCategory.java", "symbol:category", 1, 20, "contractual", "treesitter", 0.95, "code", "记录分类类型。"),
            ],
        });
        var result = await new BusinessOntologySemanticProjector(om, store)
            .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "semantic-operation-refs",
                "semantic-operation-refs-fingerprint",
                "2026-07-18T15:00:00Z"));
        var snapshot = await store.ReadExportableAsync(OntologyId);
        var relationNames = snapshot.Candidates
            .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Relation)
            .Select(item =>
            {
                using var document = JsonDocument.Parse(item.PayloadJson);
                return document.RootElement.GetProperty("semantic").GetProperty("name").GetString() ?? "";
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

        assert(result.RelationCandidates == 1
                && result.CrossLayerRelationCandidates == 0
                && result.DirectOnlyRelationDiagnostics == 1
                && relationNames.SequenceEqual(new[] { "supplier" }, StringComparer.Ordinal),
            "only field/record_component typed_reference claims may create business relations; parameter/return/local/method refs, self refs, and operation-like names must not");
        assert(!snapshot.Candidates.Any(item =>
                item.SubjectKind == OntologySemanticCandidateKinds.Relation
                && (item.PayloadJson.Contains("queryByRecordNumbers", StringComparison.Ordinal)
                    || item.PayloadJson.Contains("queryByRecordNumber", StringComparison.Ordinal)
                    || item.PayloadJson.Contains("getCategoryPath", StringComparison.Ordinal)
                    || item.PayloadJson.Contains("findNewlyByRecordNumber", StringComparison.Ordinal))),
            "real-project signature-like operation relations must be absent from semantic relation candidates");
    }

    private static async Task RejectsInfrastructureRoleTypedReferencesAsRelationsAsync(Action<bool, string> assert)
    {
        const string RecordTransfer = OntologyId + ".RecordTransfer";
        const string RecordUserSettings = OntologyId + ".RecordUserSettings";
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        var controllerInjection = Claim(
            "file:controller",
            "symbol:transfer-controller:user-settings",
            CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = false,
                member = "userSettingsService",
                ownerSymbolId = "symbol:transfer-controller",
                rawType = "RecordUserSettingsServiceImpl",
                usageKind = "field",
                resolvedTypeName = "RecordUserSettingsServiceImpl",
                resolvedTypeSymbolId = "symbol:user-settings-service",
            }),
            8);
        var mapperInjection = Claim(
            "file:record",
            "symbol:record:user-settings-mapper",
            CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = false,
                member = "userSettingsMapper",
                ownerSymbolId = "symbol:record",
                rawType = "RecordUserSettingsMapper",
                usageKind = "field",
                resolvedTypeName = "RecordUserSettingsMapper",
                resolvedTypeSymbolId = "symbol:user-settings-mapper",
            }),
            12);
        var dualRoleOwner = Claim(
            "file:dual",
            "symbol:dual:supplier",
            CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = false,
                member = "supplier",
                ownerSymbolId = "symbol:dual",
                rawType = "SupplierEntity",
                usageKind = "field",
                resolvedTypeName = "SupplierEntity",
                resolvedTypeSymbolId = "symbol:supplier",
            }),
            6);
        var entityField = Claim(
            "file:record",
            "symbol:record:supplier",
            CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = false,
                member = "supplier",
                ownerSymbolId = "symbol:record",
                rawType = "SupplierEntity",
                usageKind = "field",
                resolvedTypeName = "SupplierEntity",
                resolvedTypeSymbolId = "symbol:supplier",
            }),
            16);
        var ordinaryRecordComponent = Claim(
            "file:plain",
            "symbol:plain:audit-supplier",
            CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = false,
                member = "auditSupplier",
                ownerSymbolId = "symbol:plain-record",
                rawType = "SupplierEntity",
                usageKind = "record_component",
                resolvedTypeName = "SupplierEntity",
                resolvedTypeSymbolId = "symbol:supplier",
            }),
            4);

        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:controller", "repo:record", "src/RecordTransferController.java", "java"),
                new CodeFileFact("file:service", "repo:record", "src/RecordUserSettingsServiceImpl.java", "java"),
                new CodeFileFact("file:mapper", "repo:record", "src/RecordUserSettingsMapper.java", "java"),
                new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java"),
                new CodeFileFact("file:supplier", "repo:record", "src/SupplierEntity.java", "java"),
                new CodeFileFact("file:plain", "repo:record", "src/RecordAuditRecord.java", "java"),
                new CodeFileFact("file:dual", "repo:record", "src/ConfusedRecordServiceEntity.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:transfer-controller", "file:controller", "RecordTransferController", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:transfer-route", "file:controller", "transfer", "method", 20, 34, ParentId: "symbol:transfer-controller", Lang: "java"),
                new CodeSymbolFact("symbol:user-settings-service", "file:service", "RecordUserSettingsServiceImpl", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:user-settings-service:find", "file:service", "findSettings", "method", 18, 40, ParentId: "symbol:user-settings-service", Lang: "java"),
                new CodeSymbolFact("symbol:user-settings-mapper", "file:mapper", "RecordUserSettingsMapper", "interface", 1, 40, Lang: "java"),
                new CodeSymbolFact("symbol:user-settings-mapper:select", "file:mapper", "selectById", "method", 10, 10, ParentId: "symbol:user-settings-mapper", Lang: "java"),
                new CodeSymbolFact("symbol:record", "file:record", "RecordEntity", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:supplier", "file:supplier", "SupplierEntity", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact("symbol:plain-record", "file:plain", "RecordAuditRecord", "record", 1, 12, Lang: "java"),
                new CodeSymbolFact("symbol:dual", "file:dual", "ConfusedRecordServiceEntity", "class", 1, 40, Lang: "java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:transfer-controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "spring_annotation", "controller"),
                new CodeEdgeFact("symbol:user-settings-service", "spring:role:service", "SPRING_ROLE", "file:service", 1, 0.98, "spring_annotation", "service"),
                new CodeEdgeFact("symbol:user-settings-mapper", "spring:role:mapper", "SPRING_ROLE", "file:mapper", 1, 0.98, "spring_annotation", "mapper"),
                new CodeEdgeFact("symbol:user-settings-mapper", "spring:role:repository", "SPRING_ROLE", "file:mapper", 1, 0.98, "spring_annotation", "repository"),
                new CodeEdgeFact("symbol:record", "spring:role:entity", "SPRING_ROLE", "file:record", 1, 0.98, "spring_annotation", "entity"),
                new CodeEdgeFact("symbol:supplier", "spring:role:entity", "SPRING_ROLE", "file:supplier", 1, 0.98, "spring_annotation", "entity"),
                new CodeEdgeFact("symbol:dual", "spring:role:entity", "SPRING_ROLE", "file:dual", 1, 0.98, "spring_annotation", "entity"),
                new CodeEdgeFact("symbol:dual", "spring:role:service", "SPRING_ROLE", "file:dual", 1, 0.98, "spring_annotation", "service"),
                new CodeEdgeFact("symbol:transfer-route", "symbol:user-settings-service:find", CodeEdgeKinds.Calls, "file:controller", 25, 1.0, "treesitter", "controller calls service"),
                new CodeEdgeFact("symbol:user-settings-service:find", "symbol:user-settings-mapper:select", CodeEdgeKinds.Calls, "file:service", 30, 1.0, "treesitter", "service calls mapper"),
                new CodeEdgeFact("symbol:user-settings-mapper:select", "symbol:record", CodeEdgeKinds.Calls, "file:mapper", 10, 1.0, "treesitter", "mapper reads record"),
            ],
            EntryPoints: [new CodeEntryPointFact("symbol:transfer-route", "http_route", "POST /api/transfer")],
            SemanticClaims:
            [
                controllerInjection,
                mapperInjection,
                dualRoleOwner,
                entityField,
                ordinaryRecordComponent,
            ]));

        var baseGeneration = BaseGeneration();
        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(baseGeneration with
        {
            Concepts =
            [
                .. baseGeneration.Concepts,
                new BusinessOntologyConcept(RecordTransfer, "businessObject", "记录转移", "记录转移业务概念。", "accepted", 0.95, ["base:transfer"]),
                new BusinessOntologyConcept(RecordUserSettings, "businessObject", "记录用户设置", "记录用户设置业务概念。", "accepted", 0.95, ["base:user-settings"]),
            ],
            Mappings =
            [
                .. baseGeneration.Mappings,
                new BusinessOntologyMapping(OntologyId + ".Mapping.RecordTransferController", "concept", RecordTransfer, "representedBy", "repo:record", "java", "class", "symbol:transfer-controller", "src/RecordTransferController.java", "treesitter", 0.95, "accepted", ["base:transfer"]),
                new BusinessOntologyMapping(OntologyId + ".Mapping.RecordUserSettingsService", "concept", RecordUserSettings, "representedBy", "repo:record", "java", "class", "symbol:user-settings-service", "src/RecordUserSettingsServiceImpl.java", "treesitter", 0.95, "accepted", ["base:user-settings"]),
                new BusinessOntologyMapping(OntologyId + ".Mapping.RecordUserSettingsMapper", "concept", RecordUserSettings, "representedBy", "repo:record", "java", "class", "symbol:user-settings-mapper", "src/RecordUserSettingsMapper.java", "treesitter", 0.95, "accepted", ["base:user-settings"]),
                new BusinessOntologyMapping(OntologyId + ".Mapping.PlainRecordAuditRecord", "concept", Record, "representedBy", "repo:record", "java", "record", "symbol:plain-record", "src/RecordAuditRecord.java", "treesitter", 0.95, "accepted", ["base:plain"]),
                new BusinessOntologyMapping(OntologyId + ".Mapping.DualRecord", "concept", Record, "representedBy", "repo:record", "java", "class", "symbol:dual", "src/ConfusedRecordServiceEntity.java", "treesitter", 0.95, "accepted", ["base:dual"]),
            ],
            Evidence =
            [
                .. baseGeneration.Evidence,
                new BusinessOntologyEvidence("base:transfer", "repo:record", "src/RecordTransferController.java", "symbol:transfer-controller", 1, 80, "contractual", "treesitter", 0.95, "code", "记录转移控制器映射。"),
                new BusinessOntologyEvidence("base:user-settings", "repo:record", "src/RecordUserSettingsServiceImpl.java", "symbol:user-settings-service", 1, 80, "contractual", "treesitter", 0.95, "code", "记录用户设置服务映射。"),
                new BusinessOntologyEvidence("base:plain", "repo:record", "src/RecordAuditRecord.java", "symbol:plain-record", 1, 12, "contractual", "treesitter", 0.95, "code", "无 Spring 角色的数据记录映射。"),
                new BusinessOntologyEvidence("base:dual", "repo:record", "src/ConfusedRecordServiceEntity.java", "symbol:dual", 1, 40, "contractual", "treesitter", 0.95, "code", "同时带数据和基础设施角色的类型映射。"),
            ],
        });

        var slices = await new BusinessUseCaseSliceBuilder(om, store)
            .BuildAsync(new BusinessUseCaseSliceBuildRequest(OntologyId));
        assert(slices.Slices.Any(slice =>
                slice.ConceptIds.Contains(RecordTransfer, StringComparer.Ordinal)
                && slice.ConceptIds.Contains(RecordUserSettings, StringComparer.Ordinal)),
            "fixture should contain a matching controller-service use-case slice for the injected infrastructure endpoints");

        var result = await new BusinessOntologySemanticProjector(om, store)
            .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "semantic-infrastructure-refs",
                "semantic-infrastructure-refs-fingerprint",
                "2026-07-18T17:30:00Z"));
        var snapshot = await store.ReadExportableAsync(OntologyId);
        var relationNames = snapshot.Candidates
            .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Relation)
            .Select(item =>
            {
                using var document = JsonDocument.Parse(item.PayloadJson);
                return document.RootElement.GetProperty("semantic").GetProperty("name").GetString() ?? "";
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

        assert(result.RelationCandidates == 2
                && result.CrossLayerRelationCandidates == 0
                && result.DirectOnlyRelationDiagnostics == 2
                && relationNames.SequenceEqual(new[] { "auditSupplier", "supplier" }, StringComparer.Ordinal),
            "field/record_component relations should survive only for data carriers; infrastructure owner/target roles must be excluded even when canonical mappings and use-case slices exist");
        assert(!snapshot.Candidates.Any(item =>
                item.SubjectKind == OntologySemanticCandidateKinds.Relation
                && (item.PayloadJson.Contains("userSettingsService", StringComparison.Ordinal)
                    || item.PayloadJson.Contains("userSettingsMapper", StringComparison.Ordinal)
                    || item.PayloadJson.Contains("RecordTransfer", StringComparison.Ordinal)
                    || item.PayloadJson.Contains("RecordUserSettings", StringComparison.Ordinal))),
            "controller/service/repository/mapper/component-style field injections must not leak into business relation candidates");
    }

    private static async Task NormalizesStructuralRelationMemberNamesAsync(Action<bool, string> assert)
    {
        const string AcceptanceNotice = OntologyId + ".AcceptanceNotice";
        const string RecordInfos = OntologyId + ".RecordInfos";
        const string SyncData = OntologyId + ".SyncData";
        const string AcceptanceRecord = OntologyId + ".AcceptanceRecord";
        const string FileConcept = OntologyId + ".File";
        const string Transferline = OntologyId + ".Transferline";
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        var claims = new[]
        {
            RelationClaim("symbol:record:acceptance-notice-list", "acceptanceNoticeEntityList", "AcceptanceNoticeEntity", "symbol:acceptance-notice", true, 10),
            RelationClaim("symbol:record:acceptance-notice-array", "acceptanceNoticeDTOArray", "AcceptanceNoticeDTO", "symbol:acceptance-notice", true, 11),
            ConstraintClaim("symbol:record:acceptance-notice-required", CodeSemanticClaimKinds.ValidationConstraint, new { annotation = "NotNull", member = "acceptanceNoticeEntityList", @operator = "required", ownerSymbolId = "symbol:record", value = true }, 9),
            ConstraintClaim("symbol:record:acceptance-notice-column", CodeSemanticClaimKinds.PersistenceConstraint, new { annotation = "JoinColumn", constraintKind = "nullable", member = "acceptanceNoticeEntityList", ownerSymbolId = "symbol:record", value = false }, 8),
            RelationClaim("symbol:record:record-infos", "recordInfosEntityList", "RecordInfosEntity", "symbol:record-infos", true, 12),
            RelationClaim("symbol:record:acceptance-record", "acceptanceRecordEntityList", "AcceptanceRecordEntity", "symbol:acceptance-record", true, 12),
            RelationClaim("symbol:record:sync-data-list", "syncDataEntityList", "SyncDataEntity", "symbol:sync-data", true, 13),
            RelationClaim("symbol:record:sync-data-records", "syncDataRecordsCollection", "SyncDataRecord", "symbol:sync-data", true, 14),
            RelationClaim("symbol:record:files", "files", "FileEntity", "symbol:file", true, 15),
            RelationClaim("symbol:record:transferline", "transferline", "TransferlineEntity", "symbol:transferline", false, 16),
            RelationClaim("symbol:record:billing-supplier", "billingSupplier", "SupplierEntity", "symbol:supplier", false, 17),
            RelationClaim("symbol:record:supplier", "supplier", "SupplierEntity", "symbol:supplier", false, 18),
            RelationClaim("symbol:record:empty", "EntityList", "AcceptanceNoticeEntity", "symbol:acceptance-notice", true, 19),
        };
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java"),
                new CodeFileFact("file:acceptance", "repo:record", "src/AcceptanceNoticeEntity.java", "java"),
                new CodeFileFact("file:record-infos", "repo:record", "src/RecordInfosEntity.java", "java"),
                new CodeFileFact("file:acceptance-record", "repo:record", "src/AcceptanceRecordEntity.java", "java"),
                new CodeFileFact("file:sync", "repo:record", "src/SyncDataEntity.java", "java"),
                new CodeFileFact("file:file", "repo:record", "src/FileEntity.java", "java"),
                new CodeFileFact("file:transferline", "repo:record", "src/TransferlineEntity.java", "java"),
                new CodeFileFact("file:supplier", "repo:record", "src/SupplierEntity.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:record", "file:record", "RecordEntity", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:acceptance-notice", "file:acceptance", "AcceptanceNoticeEntity", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact("symbol:record-infos", "file:record-infos", "RecordInfosEntity", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact("symbol:acceptance-record", "file:acceptance-record", "AcceptanceRecordEntity", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact("symbol:sync-data", "file:sync", "SyncDataEntity", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact("symbol:file", "file:file", "FileEntity", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact("symbol:transferline", "file:transferline", "TransferlineEntity", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact("symbol:supplier", "file:supplier", "SupplierEntity", "class", 1, 20, Lang: "java"),
            ],
            SemanticClaims: claims));

        var baseGeneration = BaseGeneration();
        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(baseGeneration with
        {
            Concepts =
            [
                .. baseGeneration.Concepts,
                new BusinessOntologyConcept(AcceptanceNotice, "businessObject", "验收单", "验收单业务概念。", "accepted", 0.95, ["base:acceptance"]),
                new BusinessOntologyConcept(RecordInfos, "businessObject", "记录明细", "记录明细业务概念。", "accepted", 0.95, ["base:record-infos"]),
                new BusinessOntologyConcept(AcceptanceRecord, "businessObject", "验收记录", "验收记录业务概念。", "accepted", 0.95, ["base:acceptance-record"]),
                new BusinessOntologyConcept(SyncData, "businessObject", "同步数据", "同步数据业务概念。", "accepted", 0.95, ["base:sync"]),
                new BusinessOntologyConcept(FileConcept, "businessObject", "文件", "文件业务概念。", "accepted", 0.95, ["base:file"]),
                new BusinessOntologyConcept(Transferline, "businessObject", "调拨行", "调拨行业务概念。", "accepted", 0.95, ["base:transferline"]),
            ],
            Mappings =
            [
                .. baseGeneration.Mappings,
                new BusinessOntologyMapping(OntologyId + ".Mapping.AcceptanceNotice", "concept", AcceptanceNotice, "representedBy", "repo:record", "java", "class", "symbol:acceptance-notice", "src/AcceptanceNoticeEntity.java", "treesitter", 0.95, "accepted", ["base:acceptance"]),
                new BusinessOntologyMapping(OntologyId + ".Mapping.RecordInfos", "concept", RecordInfos, "representedBy", "repo:record", "java", "class", "symbol:record-infos", "src/RecordInfosEntity.java", "treesitter", 0.95, "accepted", ["base:record-infos"]),
                new BusinessOntologyMapping(OntologyId + ".Mapping.AcceptanceRecord", "concept", AcceptanceRecord, "representedBy", "repo:record", "java", "class", "symbol:acceptance-record", "src/AcceptanceRecordEntity.java", "treesitter", 0.95, "accepted", ["base:acceptance-record"]),
                new BusinessOntologyMapping(OntologyId + ".Mapping.SyncData", "concept", SyncData, "representedBy", "repo:record", "java", "class", "symbol:sync-data", "src/SyncDataEntity.java", "treesitter", 0.95, "accepted", ["base:sync"]),
                new BusinessOntologyMapping(OntologyId + ".Mapping.File", "concept", FileConcept, "representedBy", "repo:record", "java", "class", "symbol:file", "src/FileEntity.java", "treesitter", 0.95, "accepted", ["base:file"]),
                new BusinessOntologyMapping(OntologyId + ".Mapping.Transferline", "concept", Transferline, "representedBy", "repo:record", "java", "class", "symbol:transferline", "src/TransferlineEntity.java", "treesitter", 0.95, "accepted", ["base:transferline"]),
            ],
            Evidence =
            [
                .. baseGeneration.Evidence,
                new BusinessOntologyEvidence("base:acceptance", "repo:record", "src/AcceptanceNoticeEntity.java", "symbol:acceptance-notice", 1, 20, "contractual", "treesitter", 0.95, "code", "验收单类型。"),
                new BusinessOntologyEvidence("base:record-infos", "repo:record", "src/RecordInfosEntity.java", "symbol:record-infos", 1, 20, "contractual", "treesitter", 0.95, "code", "记录明细类型。"),
                new BusinessOntologyEvidence("base:acceptance-record", "repo:record", "src/AcceptanceRecordEntity.java", "symbol:acceptance-record", 1, 20, "contractual", "treesitter", 0.95, "code", "验收记录类型。"),
                new BusinessOntologyEvidence("base:sync", "repo:record", "src/SyncDataEntity.java", "symbol:sync-data", 1, 20, "contractual", "treesitter", 0.95, "code", "同步数据类型。"),
                new BusinessOntologyEvidence("base:file", "repo:record", "src/FileEntity.java", "symbol:file", 1, 20, "contractual", "treesitter", 0.95, "code", "文件类型。"),
                new BusinessOntologyEvidence("base:transferline", "repo:record", "src/TransferlineEntity.java", "symbol:transferline", 1, 20, "contractual", "treesitter", 0.95, "code", "调拨行类型。"),
            ],
        });

        var projector = new BusinessOntologySemanticProjector(om, store);
        var result = await projector.ProjectAsync(new BusinessOntologySemanticProjectionRequest(
            OntologyId,
            "semantic-relation-name-normalization",
            "semantic-relation-name-normalization-fingerprint",
            "2026-07-18T18:00:00Z"));
        var first = await store.ReadExportableAsync(OntologyId);
        await projector.ProjectAsync(new BusinessOntologySemanticProjectionRequest(
            OntologyId,
            "semantic-relation-name-normalization",
            "semantic-relation-name-normalization-fingerprint",
            "2026-07-18T18:00:00Z"));
        var repeated = await store.ReadExportableAsync(OntologyId);
        var relations = first.Candidates
            .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Relation)
            .Select(item => (Candidate: item, Document: JsonDocument.Parse(item.PayloadJson)))
            .ToArray();
        try
        {
            var relationNames = relations
                .Select(item => item.Document.RootElement.GetProperty("semantic").GetProperty("name").GetString() ?? "")
                .Order(StringComparer.Ordinal)
                .ToArray();
            assert(result.RelationCandidates == 8
                    && result.DirectOnlyRelationDiagnostics == 8
                    && relationNames.SequenceEqual(
                        new[] { "acceptanceNotice", "acceptanceRecord", "billingSupplier", "files", "recordInfos", "supplier", "syncData", "transferline" },
                        StringComparer.Ordinal),
                "relation projection should strip only identifier-boundary carrier/container suffixes while preserving the ordinary Record word and other business role/member names");
            assert(!relations.Any(item =>
                    item.Candidate.PayloadJson.Contains("acceptanceNoticeEntityList", StringComparison.Ordinal)
                    || item.Candidate.PayloadJson.Contains("recordInfosEntityList", StringComparison.Ordinal)
                    || item.Candidate.PayloadJson.Contains("syncDataEntityList", StringComparison.Ordinal)
                    || item.Candidate.PayloadJson.Contains("EntityList", StringComparison.Ordinal)),
                "candidate payloads must use normalized relation names and reject names that normalize to empty");

            var acceptanceNotice = relations.Single(item =>
                item.Document.RootElement.GetProperty("semantic").GetProperty("name").GetString() == "acceptanceNotice");
            assert(acceptanceNotice.Candidate.ProposedId == Record + ".acceptanceNotice"
                    && acceptanceNotice.Document.RootElement.GetProperty("semantic").GetProperty("id").GetString() == Record + ".acceptanceNotice"
                    && acceptanceNotice.Document.RootElement.GetProperty("semantic").GetProperty("min").GetString() == "1"
                    && acceptanceNotice.Document.RootElement.GetProperty("semantic").GetProperty("max").GetString() == "many"
                    && acceptanceNotice.Candidate.EvidenceIds.Count(id => claims.Any(claim => claim.ClaimId == id)) == 4,
                "normalized relation identity should merge equivalent raw members and retain direct required/cardinality evidence");
            assert(first.Diagnostics.Any(item =>
                    item.Kind == "direct-only"
                    && item.ConflictKey == Record + ".acceptanceNotice")
                    && !first.Diagnostics.Any(item =>
                        item.ConflictKey.Contains("EntityList", StringComparison.Ordinal)
                        || item.ConflictKey.Contains("DTOArray", StringComparison.Ordinal)),
                "direct-only diagnostic conflict keys should use normalized relation names");
            assert(repeated.Candidates
                    .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Relation)
                    .Select(item => item.Id)
                    .Order(StringComparer.Ordinal)
                    .SequenceEqual(
                        first.Candidates
                            .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Relation)
                            .Select(item => item.Id)
                            .Order(StringComparer.Ordinal),
                        StringComparer.Ordinal),
                "normalized relation candidate ids should remain stable across repeated projection");
        }
        finally
        {
            foreach (var relation in relations)
            {
                relation.Document.Dispose();
            }
        }

        static CodeSemanticClaimFact RelationClaim(
            string subjectId,
            string member,
            string rawType,
            string resolvedTypeSymbolId,
            bool collection,
            int line) =>
            ConstraintClaim(subjectId, CodeSemanticClaimKinds.TypedReference, new
            {
                collection,
                member,
                ownerSymbolId = "symbol:record",
                rawType,
                usageKind = "field",
                resolvedTypeName = rawType,
                resolvedTypeSymbolId,
            }, line);

        static CodeSemanticClaimFact ConstraintClaim(
            string subjectId,
            string kind,
            object payload,
            int line)
        {
            var json = JsonSerializer.Serialize(payload);
            return new CodeSemanticClaimFact(
                CodeSemanticClaimIdentity.Create("file:record", kind, json, line, line),
                subjectId,
                kind,
                json,
                "file:record",
                line,
                line,
                0.96,
                "treesitter",
                $"direct {kind}");
        }
    }

    private static async Task RejectsHighFanoutWorkflowCarrierRelationsAsync(Action<bool, string> assert)
    {
        const string RecordTransferDetail = OntologyId + ".RecordTransferDetail";
        const string RecordLocation = OntologyId + ".RecordLocation";
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        var accumulatorClaims = Enumerable.Range(1, 12)
            .Select(index => RelationClaim(
                $"symbol:acceptance-note-dto:effect-{index}",
                $"effectRecord{index}",
                "symbol:acceptance-note-dto",
                "symbol:supplier",
                10 + index))
            .ToArray();
        var entityClaims = Enumerable.Range(1, 12)
            .Select(index => RelationClaim(
                $"symbol:record-aggregate:detail-{index}",
                $"detail{index}",
                "symbol:record-aggregate",
                "symbol:supplier",
                40 + index))
            .ToArray();
        var lowFanoutClaims = new[]
        {
            RelationClaim(
                "symbol:record-transfer-dto:transfer-line",
                "transfer_line",
                "symbol:record-transfer-dto",
                "symbol:record-transfer-detail",
                70),
            RelationClaim(
                "symbol:record-city-vo:locations",
                "recordLocationList",
                "symbol:record-city-vo",
                "symbol:record-location",
                71),
            RelationClaim(
                "symbol:record-scrapped-dto:sync-data",
                "syncDataEntityList",
                "symbol:record-scrapped-dto",
                "symbol:supplier",
                72),
            RelationClaim(
                "symbol:impl-entity:supplier",
                "supplier",
                "symbol:impl-entity",
                "symbol:supplier",
                73),
        };

        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:owners", "repo:record", "src/WorkflowCarriers.java", "java"),
                new CodeFileFact("file:acceptance-dto", "repo:record", "src/service/data/dto/AcceptanceNoteDTO.java", "java"),
                new CodeFileFact("file:transfer-dto", "repo:record", "src/service/data/dto/RecordTransferDto.java", "java"),
                new CodeFileFact("file:city-vo", "repo:record", "src/common/vo/RecordCityVO.java", "java"),
                new CodeFileFact("file:scrapped-impl", "repo:record", "src/service/impl/RecordScrappedServiceImpl.java", "java"),
                new CodeFileFact("file:impl-entity", "repo:record", "src/service/impl/RecordAggregateServiceImpl.java", "java"),
                new CodeFileFact("file:supplier", "repo:record", "src/SupplierEntity.java", "java"),
                new CodeFileFact("file:transfer-detail", "repo:record", "src/RecordTransferDetail.java", "java"),
                new CodeFileFact("file:location", "repo:record", "src/RecordLocation.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:acceptance-note-dto", "file:acceptance-dto", "AcceptanceNoteDTO", "class", 1, 35, Lang: "java"),
                new CodeSymbolFact("symbol:record-transfer-dto", "file:transfer-dto", "RecordTransferDto", "class", 36, 45, Lang: "java"),
                new CodeSymbolFact("symbol:record-city-vo", "file:city-vo", "RecordCityVO", "class", 46, 55, Lang: "java"),
                new CodeSymbolFact("symbol:record-scrapped-dto", "file:scrapped-impl", "RecordScrappedDTO", "class", 1234, 1245, Lang: "java"),
                new CodeSymbolFact("symbol:impl-entity", "file:impl-entity", "RecordAggregateEntity", "class", 100, 150, Lang: "java"),
                new CodeSymbolFact("symbol:record-aggregate", "file:owners", "RecordAggregateEntity", "class", 56, 90, Lang: "java"),
                new CodeSymbolFact("symbol:supplier", "file:supplier", "SupplierEntity", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact("symbol:record-transfer-detail", "file:transfer-detail", "RecordTransferDetail", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact("symbol:record-location", "file:location", "RecordLocation", "class", 1, 20, Lang: "java"),
            ],
            SemanticClaims: [.. accumulatorClaims, .. entityClaims, .. lowFanoutClaims]));

        var baseGeneration = BaseGeneration();
        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(baseGeneration with
        {
            Concepts =
            [
                .. baseGeneration.Concepts,
                new BusinessOntologyConcept(RecordTransferDetail, "businessObject", "调拨明细", "调拨明细业务概念。", "accepted", 0.95, ["base:transfer-detail"]),
                new BusinessOntologyConcept(RecordLocation, "businessObject", "记录位置", "记录位置业务概念。", "accepted", 0.95, ["base:location"]),
            ],
            Mappings =
            [
                .. baseGeneration.Mappings,
                Mapping("AcceptanceNoteDTO", "symbol:acceptance-note-dto"),
                Mapping("RecordTransferDto", "symbol:record-transfer-dto"),
                Mapping("RecordCityVO", "symbol:record-city-vo"),
                Mapping("RecordScrappedDTO", "symbol:record-scrapped-dto"),
                Mapping("ImplRecordAggregateEntity", "symbol:impl-entity"),
                Mapping("RecordAggregateEntity", "symbol:record-aggregate"),
                TargetMapping("RecordTransferDetail", RecordTransferDetail, "symbol:record-transfer-detail", "base:transfer-detail"),
                TargetMapping("RecordLocation", RecordLocation, "symbol:record-location", "base:location"),
            ],
            Evidence =
            [
                .. baseGeneration.Evidence,
                new BusinessOntologyEvidence("base:transfer-detail", "repo:record", "src/RecordTransferDetail.java", "symbol:record-transfer-detail", 1, 20, "contractual", "treesitter", 0.95, "code", "调拨明细类型。"),
                new BusinessOntologyEvidence("base:location", "repo:record", "src/RecordLocation.java", "symbol:record-location", 1, 20, "contractual", "treesitter", 0.95, "code", "记录位置类型。"),
            ],
        });

        var result = await new BusinessOntologySemanticProjector(om, store)
            .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "semantic-workflow-carrier",
                "semantic-workflow-carrier-fingerprint",
                "2026-07-18T18:10:00Z"));
        var snapshot = await store.ReadExportableAsync(OntologyId);
        var relations = snapshot.Candidates
            .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Relation)
            .Select(item =>
            {
                using var document = JsonDocument.Parse(item.PayloadJson);
                var semantic = document.RootElement.GetProperty("semantic");
                return (
                    Name: semantic.GetProperty("name").GetString() ?? "",
                    ToConceptId: semantic.GetProperty("toConceptId").GetString() ?? "");
            })
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        var relationNames = relations.Select(item => item.Name).ToArray();

        assert(result.RelationCandidates == 15
                && relations.Contains(("transferline", RecordTransferDetail))
                && relations.Contains(("recordLocation", RecordLocation))
                && relationNames.Contains("supplier", StringComparer.Ordinal)
                && entityClaims.All(claim => relationNames.Contains(
                    JsonDocument.Parse(claim.PayloadJson).RootElement.GetProperty("member").GetString() ?? "",
                    StringComparer.Ordinal)),
            "low-fanout DTO/VO structural relations and high-fanout entity aggregate relations must survive workflow-carrier filtering");
        assert(!relationNames.Any(name => name.StartsWith("effectRecord", StringComparison.Ordinal)),
            "all structural references of a high-fanout DTO workflow accumulator must be suppressed as one owner-level decision");
        assert(!relationNames.Contains("syncData", StringComparer.Ordinal),
            "a low-fanout DTO nested in a service implementation source file must not create a stable business relation");
        var diagnostic = snapshot.Diagnostics.Single(item =>
            item.Kind == "workflow-carrier-relations-suppressed");
        assert(diagnostic.ConflictKey == "symbol:acceptance-note-dto"
                && diagnostic.DetailsJson.Contains("\"directStructuralFanout\":12", StringComparison.Ordinal)
                && diagnostic.DetailsJson.Contains("\"threshold\":8", StringComparison.Ordinal)
                && diagnostic.DetailsJson.Contains("\"ownerName\":\"AcceptanceNoteDTO\"", StringComparison.Ordinal)
                && diagnostic.DetailsJson.Contains("\"ownerPath\":\"src/service/data/dto/AcceptanceNoteDTO.java\"", StringComparison.Ordinal)
                && diagnostic.DetailsJson.Contains("\"reason\":\"high_fanout_workflow_carrier\"", StringComparison.Ordinal),
            "high-fanout suppression must remain explainable through a stable owner, path, observed fanout, and named threshold diagnostic");
        var implementationLocalDiagnostic = snapshot.Diagnostics.Single(item =>
            item.Kind == "implementation-local-workflow-carrier-relations-suppressed");
        assert(implementationLocalDiagnostic.ConflictKey == "symbol:record-scrapped-dto"
                && implementationLocalDiagnostic.DetailsJson.Contains("\"directStructuralFanout\":1", StringComparison.Ordinal)
                && implementationLocalDiagnostic.DetailsJson.Contains("\"threshold\":8", StringComparison.Ordinal)
                && implementationLocalDiagnostic.DetailsJson.Contains("\"ownerName\":\"RecordScrappedDTO\"", StringComparison.Ordinal)
                && implementationLocalDiagnostic.DetailsJson.Contains("\"ownerPath\":\"src/service/impl/RecordScrappedServiceImpl.java\"", StringComparison.Ordinal)
                && implementationLocalDiagnostic.DetailsJson.Contains("\"reason\":\"implementation_local_workflow_carrier\"", StringComparison.Ordinal),
            "implementation-local suppression must identify the owner source path independently of structural fanout");
        assert(!snapshot.Diagnostics.Any(item =>
                item.Kind is "workflow-carrier-relations-suppressed"
                    or "implementation-local-workflow-carrier-relations-suppressed"
                && item.ConflictKey is "symbol:record-transfer-dto"
                    or "symbol:record-city-vo"
                    or "symbol:record-aggregate"
                    or "symbol:impl-entity"),
            "dedicated low-fanout DTO/VO files and entity aggregate roots must not receive workflow-carrier suppression diagnostics");

        static CodeSemanticClaimFact RelationClaim(
            string subjectId,
            string member,
            string ownerSymbolId,
            string targetSymbolId,
            int line)
        {
            var payload = JsonSerializer.Serialize(new
            {
                collection = true,
                member,
                ownerSymbolId,
                rawType = "SupplierEntity",
                usageKind = "field",
                resolvedTypeName = "SupplierEntity",
                resolvedTypeSymbolId = targetSymbolId,
            });
            return new CodeSemanticClaimFact(
                CodeSemanticClaimIdentity.Create("file:owners", CodeSemanticClaimKinds.TypedReference, payload, line, line),
                subjectId,
                CodeSemanticClaimKinds.TypedReference,
                payload,
                "file:owners",
                line,
                line,
                0.96,
                "treesitter",
                "direct typed reference");
        }

        static BusinessOntologyMapping Mapping(string name, string symbolId) =>
            new(
                OntologyId + ".Mapping." + name,
                "concept",
                Record,
                "representedBy",
                "repo:record",
                "java",
                "class",
                symbolId,
                "src/WorkflowCarriers.java",
                "treesitter",
                0.95,
                "accepted",
                ["base:record"]);

        static BusinessOntologyMapping TargetMapping(
            string name,
            string conceptId,
            string symbolId,
            string evidenceId) =>
            new(
                OntologyId + ".Mapping." + name,
                "concept",
                conceptId,
                "representedBy",
                "repo:record",
                "java",
                "class",
                symbolId,
                "src/" + name + ".java",
                "treesitter",
                0.95,
                "accepted",
                [evidenceId]);
    }

    private static async Task AssistedModeExcludesFieldConstraintsFromProposalPackAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "onto-semantic-projector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "src", "RecordEntity.java"),
                "class RecordEntity {\n"
                + "  @JoinColumn(nullable = false)\n"
                + "  @NotNull\n"
                + "  SupplierEntity supplier;\n"
                + "}\n");
            var typedPayload = JsonSerializer.Serialize(new
            {
                collection = false,
                member = "supplier",
                ownerSymbolId = "symbol:record",
                rawType = "SupplierEntity",
                usageKind = "field",
                resolvedTypeName = "SupplierEntity",
                resolvedTypeSymbolId = "symbol:supplier",
            });
            var validationPayload = JsonSerializer.Serialize(new
            {
                annotation = "NotNull",
                member = "supplier",
                @operator = "required",
                ownerSymbolId = "symbol:record",
                value = true,
            });
            var persistencePayload = JsonSerializer.Serialize(new
            {
                annotation = "JoinColumn",
                constraintKind = "nullable",
                member = "supplier",
                ownerSymbolId = "symbol:record",
                value = false,
            });
            var typed = Claim("file:record", "symbol:record:supplier", CodeSemanticClaimKinds.TypedReference, typedPayload, 4);
            var validation = Claim("file:record", "symbol:record:supplier", CodeSemanticClaimKinds.ValidationConstraint, validationPayload, 3);
            var persistence = Claim("file:record", "symbol:record:supplier", CodeSemanticClaimKinds.PersistenceConstraint, persistencePayload, 2);

            using var db = new CozoDb("mem", "");
            var om = new CozoOm(db);
            await om.InitCodeKnowledgeAsync();
            await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Repositories: [new CodeRepositoryFact("repo:record", root, "record")],
                Files: [new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java")],
                SemanticClaims: [typed, validation, persistence]));
            var store = new BusinessOntologyStore(om);
            await store.ReplaceGenerationAsync(BaseGeneration());

            var llm = new FakeLlmClient(JsonSerializer.Serialize(new
            {
                schemaVersion = OntologySemanticCandidateValidator.SchemaVersion,
                candidates = Array.Empty<object>(),
            }));
            var projector = new BusinessOntologySemanticProjector(om, store, llmClient: llm);
            await projector.ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "semantic-assisted-constraints",
                "semantic-assisted-fingerprint",
                "2026-07-18T10:30:00Z",
                Mode: BusinessOntologySemanticProjectionModes.Assisted));

            var snapshot = await store.ReadExportableAsync(OntologyId);
            var relation = snapshot.Candidates
                .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Relation)
                .Select(item => JsonDocument.Parse(item.PayloadJson))
                .Single(document => document.RootElement.GetProperty("semantic").GetProperty("name").GetString() == "supplier");
            try
            {
                assert(
                    relation.RootElement.GetProperty("semantic").GetProperty("min").GetString() == "1"
                    && relation.RootElement.GetProperty("evidenceIds").EnumerateArray()
                        .Select(item => item.GetString() ?? "")
                        .Count(id => new[] { typed.ClaimId, validation.ClaimId, persistence.ClaimId }.Contains(id, StringComparer.Ordinal)) == 3,
                    "validation and persistence constraints should still support relation min/cardinality evidence");
            }
            finally
            {
                relation.Dispose();
            }

            assert(llm.UserPrompt.Contains(CodeSemanticClaimKinds.TypedReference, StringComparison.Ordinal)
                    && !llm.UserPrompt.Contains(CodeSemanticClaimKinds.ValidationConstraint, StringComparison.Ordinal)
                    && !llm.UserPrompt.Contains(CodeSemanticClaimKinds.PersistenceConstraint, StringComparison.Ordinal)
                    && !snapshot.Candidates.Any(item => item.SubjectKind == OntologySemanticCandidateKinds.Rule),
                "validation and persistence constraints must not enter assisted business-rule proposal evidence");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        static CodeSemanticClaimFact Claim(string fileId, string subjectId, string kind, string payload, int line) =>
            new(
                CodeSemanticClaimIdentity.Create(fileId, kind, payload, line, line),
                subjectId,
                kind,
                payload,
                fileId,
                line,
                line,
                0.96,
                "treesitter",
                $"direct {kind}");
    }

    private static async Task CorroboratesAndDiagnosesConflictsAsync(Action<bool, string> assert)
    {
        const string Warehouse = OntologyId + ".Warehouse";
        using var db = new CozoDb("mem", "");
        var innerStore = new CozoDbOmStore(db);
        await innerStore.RunAsync(":create depa_corroboration_sentinel {id => value}");
        await innerStore.RunAsync(
            """?[id, value] <- [["sentinel", "unchanged"]] :put depa_corroboration_sentinel {id => value}""");
        var guardedStore = new ForbiddenRelationStore(innerStore, "depa_");
        var om = new CozoOm(guardedStore);
        await om.InitCodeKnowledgeAsync();

        var claims = ConflictClaims();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java"),
                new CodeFileFact("file:supplier", "repo:record", "src/SupplierEntity.java", "java"),
                new CodeFileFact("file:warehouse", "repo:record", "src/WarehouseEntity.java", "java"),
                new CodeFileFact("file:controller", "repo:record", "src/RecordController.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:record", "file:record", "RecordEntity", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:supplier", "file:supplier", "SupplierEntity", "class", 1, 20, Lang: "java"),
                new CodeSymbolFact("symbol:warehouse", "file:warehouse", "WarehouseEntity", "class", 1, 20, Lang: "java"),
            ],
            SemanticClaims: claims));

        var store = new BusinessOntologyStore(om);
        var baseGeneration = BaseGeneration() with
        {
            Concepts =
            [
                .. BaseGeneration().Concepts,
                new BusinessOntologyConcept(Warehouse, "businessObject", "仓库", "仓库业务概念。", "accepted", 0.95, ["base:warehouse"]),
            ],
            Mappings =
            [
                .. BaseGeneration().Mappings,
                new BusinessOntologyMapping(OntologyId + ".Mapping.Warehouse", "concept", Warehouse, "representedBy", "repo:record", "java", "class", "symbol:warehouse", "src/WarehouseEntity.java", "treesitter", 0.95, "accepted", ["base:warehouse"]),
            ],
            Evidence =
            [
                .. BaseGeneration().Evidence,
                new BusinessOntologyEvidence("base:warehouse", "repo:record", "src/WarehouseEntity.java", "symbol:warehouse", 1, 20, "contractual", "treesitter", 0.95, "code", "仓库类型。"),
            ],
        };
        await store.ReplaceGenerationAsync(baseGeneration);

        var corroborations = new[]
        {
            new BusinessOntologyCorroborationObservation(
                "evidence:frontend:record-page",
                "repo:frontend",
                "src/pages/records/RecordPage.tsx",
                "RecordPage",
                3,
                44,
                BusinessOntologyCorroborationKinds.FrontendPage,
                "RecordPage",
                "route",
                "/records",
                "记录页面直接使用记录路由。"),
            new BusinessOntologyCorroborationObservation(
                "evidence:doc:supplier",
                "repo:docs",
                "docs/supplier.md",
                "SupplierContract",
                2,
                18,
                BusinessOntologyCorroborationKinds.Documentation,
                "Supplier",
                "type",
                "SupplierEntity",
                "供应商文档锚定后端类型。"),
            new BusinessOntologyCorroborationObservation(
                "evidence:frontend:wrong-token",
                "repo:frontend",
                "src/pages/procurement/ProcurementPage.tsx",
                "ProcurementPage",
                1,
                30,
                BusinessOntologyCorroborationKinds.FrontendPage,
                "Procurement",
                "route",
                "/records",
                "领域 token 不匹配。"),
            new BusinessOntologyCorroborationObservation(
                "evidence:doc:wrong-anchor",
                "repo:docs",
                "docs/record.md",
                "RecordDocument",
                1,
                10,
                BusinessOntologyCorroborationKinds.Documentation,
                "Record",
                "route",
                "/orders",
                "路由锚点不匹配。"),
        };
        var projector = new BusinessOntologySemanticProjector(om, store);
        var request = new BusinessOntologySemanticProjectionRequest(
            OntologyId,
            "semantic-conflict-1",
            "semantic-conflict-fingerprint",
            "2026-07-18T11:00:00Z",
            Corroborations: corroborations);
        var firstResult = await projector.ProjectAsync(request);
        var first = await store.ReadExportableAsync(OntologyId);
        var candidates = first.Candidates
            .Where(item => item.Id.StartsWith("candidate:semantic:", StringComparison.Ordinal))
            .ToArray();

        assert(firstResult.RelationCandidates == 2
                && firstResult.RuleCandidates == 0
                && firstResult.LifecycleCandidates == 2
                && candidates.Length == 4,
            "conflicting direct relation and lifecycle claims should remain four separate semantic candidates while annotation-only rules are suppressed");
        assert(candidates.All(item => item.Status == "pending")
                && first.Relations.Count == 0
                && first.Rules.Count == 0
                && first.Lifecycles.Count == 0,
            "corroboration and conflicts must not change pending status or materialize ontology objects");
        assert(candidates.All(item =>
                item.EvidenceIds.Any(id => claims.Any(claim => id == claim.ClaimId))),
            "every candidate must retain at least one direct ck_semantic_claim evidence reference");
        assert(candidates
                .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Relation)
                .All(item => !item.EvidenceIds.Contains("evidence:frontend:record-page", StringComparer.Ordinal)
                    && !item.EvidenceIds.Contains("evidence:doc:supplier", StringComparer.Ordinal)),
            "direct-only relations without a matching use-case slice must not be enriched by route/frontend/document corroboration");
        assert(candidates
                .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Lifecycle)
                .All(item => item.EvidenceIds.Contains("evidence:frontend:record-page", StringComparer.Ordinal)),
            "non-relation candidates may still receive matching corroboration under the older corroboration contract");
        assert(!first.Evidence.Any(item => item.Id is "evidence:frontend:wrong-token" or "evidence:doc:wrong-anchor"),
            "mismatched token or anchor must not create standalone corroboration evidence or semantic candidates");

        var relationShapes = candidates
            .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Relation)
            .Select(item =>
            {
                using var document = JsonDocument.Parse(item.PayloadJson);
                var semantic = document.RootElement.GetProperty("semantic");
                return (
                    Target: semantic.GetProperty("toConceptId").GetString() ?? "",
                    Min: semantic.GetProperty("min").GetString() ?? "",
                    Max: semantic.GetProperty("max").GetString() ?? "");
            })
            .ToHashSet();
        assert(relationShapes.SetEquals([(Supplier, "0", "1"), (Warehouse, "0", "many")]),
            "corroboration must not rewrite mutually exclusive relation targets or cardinalities");

        var conflictDiagnostics = first.Diagnostics
            .Where(item => item.Kind == "conflict")
            .ToArray();
        assert(conflictDiagnostics.Length == 2
                && conflictDiagnostics.All(item =>
                    item.Kind == "conflict"
                    && item.Severity == "warning"
                    && item.Id.StartsWith("diagnostic:semantic-conflict:", StringComparison.Ordinal))
                && conflictDiagnostics.Select(item => item.SubjectKind).ToHashSet(StringComparer.Ordinal)
                    .SetEquals([
                        OntologySemanticCandidateKinds.Relation,
                        OntologySemanticCandidateKinds.Lifecycle,
                    ]),
            "relation target/cardinality and lifecycle state-field conflicts should be auditable diagnostics without annotation-only rule conflicts");
        foreach (var diagnostic in conflictDiagnostics)
        {
            using var details = JsonDocument.Parse(diagnostic.DetailsJson);
            var ids = details.RootElement.GetProperty("candidateIds").EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToArray();
            assert(ids.Length == 2
                    && ids.All(id => candidates.Any(candidate => candidate.Id == id)),
                "each conflict diagnostic should reference the separate pending candidates it preserves");
        }

        var firstBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            first.Candidates,
            first.Evidence,
            first.Diagnostics,
            first.EvidenceReferences,
        });
        await projector.ProjectAsync(request);
        var repeated = await store.ReadExportableAsync(OntologyId);
        var repeatedBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            repeated.Candidates,
            repeated.Evidence,
            repeated.Diagnostics,
            repeated.EvidenceReferences,
        });
        assert(firstBytes.SequenceEqual(repeatedBytes),
            "identical direct and corroborating inputs should reproduce byte-identical candidates, evidence, refs, and diagnostics");

        var sentinel = await innerStore.RunAsync(
            """?[value] := *depa_corroboration_sentinel{id: "sentinel", value}""");
        assert(firstResult.ConflictDiagnostics == 2
                && guardedStore.ForbiddenAccessCount == 0
                && sentinel.Rows.Single()[0].GetString() == "unchanged",
            "corroboration and conflict detection must neither query nor mutate depa_* relations");
    }

    private static async Task RequiresExplicitReindexAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.Runtime.Store.RunAsync(":create ck_meta {key => value}");
        await om.Runtime.Store.RunAsync(
            """?[key, value] <- [["schema_version", 2]] :put ck_meta {key => value}""");
        var failed = false;
        try
        {
            await new BusinessOntologySemanticProjector(om, new BusinessOntologyStore(om))
                .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                    OntologyId,
                    "must-not-write",
                    "fingerprint",
                    "2026-07-18T10:00:00Z"));
        }
        catch (InvalidOperationException ex)
        {
            failed = ex.Message.Contains("Reindex required", StringComparison.OrdinalIgnoreCase)
                && ex.Message.Contains("--reindex", StringComparison.Ordinal);
        }

        var relationNames = (await om.Runtime.Store.RunAsync("::relations")).Rows
            .Select(row => row[0].GetString() ?? "")
            .ToArray();
        assert(failed && !relationNames.Any(name => name.StartsWith("onto_", StringComparison.Ordinal)),
            "missing semantic-claim schema should fail with actionable reindex guidance before onto initialization");
    }

    private static IReadOnlyList<CodeSemanticClaimFact> Claims()
    {
        var claims = new List<CodeSemanticClaimFact>();
        Add("file:record", "symbol:record:supplier", CodeSemanticClaimKinds.TypedReference,
            new { collection = false, member = "supplier", ownerSymbolId = "symbol:record", rawType = "SupplierEntity", usageKind = "field", resolvedTypeName = "SupplierEntity", resolvedTypeSymbolId = "symbol:supplier" }, 5);
        Add("file:record", "symbol:record:supplier", CodeSemanticClaimKinds.ValidationConstraint,
            new { annotation = "NotNull", member = "supplier", @operator = "required", ownerSymbolId = "symbol:record", value = true }, 4);
        Add("file:record", "symbol:record:supplier", CodeSemanticClaimKinds.PersistenceConstraint,
            new { annotation = "JoinColumn", constraintKind = "nullable", member = "supplier", ownerSymbolId = "symbol:record", value = false }, 3);
        Add("file:record", "symbol:record:suppliers", CodeSemanticClaimKinds.TypedReference,
            new { collection = true, member = "suppliers", ownerSymbolId = "symbol:record", rawType = "List<SupplierEntity>", usageKind = "field", resolvedTypeName = "SupplierEntity", resolvedTypeSymbolId = "symbol:supplier" }, 8);
        Add("file:record", "symbol:record:warehouse", CodeSemanticClaimKinds.TypedReference,
            new { collection = false, member = "warehouse", ownerSymbolId = "symbol:record", rawType = "WarehouseEntity", usageKind = "field", resolvedTypeName = "WarehouseEntity", resolvedTypeSymbolId = "symbol:unmapped" }, 9);
        Add("file:record", "symbol:record:code", CodeSemanticClaimKinds.ValidationConstraint,
            new { annotation = "Size", member = "code", @operator = "min", ownerSymbolId = "symbol:record", value = 2 }, 12);
        Add("file:record", "symbol:record:quantity", CodeSemanticClaimKinds.ValidationConstraint,
            new { annotation = "Max", member = "quantity", @operator = "max", ownerSymbolId = "symbol:record", value = 100 }, 15);
        Add("file:record", "symbol:record:code", CodeSemanticClaimKinds.ValidationConstraint,
            new { annotation = "Pattern", member = "code", @operator = "pattern", ownerSymbolId = "symbol:record", value = "[A-Z0-9-]+" }, 13);
        Add("file:record", "symbol:record:code", CodeSemanticClaimKinds.PersistenceConstraint,
            new { annotation = "Column", constraintKind = "unique", member = "code", ownerSymbolId = "symbol:record", value = true }, 14);
        Add("file:service", "symbol:service:route", CodeSemanticClaimKinds.RouteBinding,
            new { httpMethods = new[] { "POST" }, paths = new[] { "/records" }, site = "RecordController.create", symbolId = "symbol:service:route" }, 5);
        Add("file:service", "symbol:service:save", CodeSemanticClaimKinds.TransactionScope,
            new { annotation = "Transactional", scopeKind = "method", symbolId = "symbol:service:save", symbolName = "save" }, 10);
        Add("file:record", "symbol:record:status", CodeSemanticClaimKinds.StateField,
            new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", member = "status", ownerSymbolId = "symbol:record" }, 20);
        Add("file:record", "symbol:record-status:draft", CodeSemanticClaimKinds.StateValue,
            new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", value = "DRAFT" }, 21);
        Add("file:record", "symbol:record-status:approved", CodeSemanticClaimKinds.StateValue,
            new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", value = "APPROVED" }, 22);
        Add("file:record", "symbol:record-status:rejected", CodeSemanticClaimKinds.StateValue,
            new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", value = "REJECTED" }, 23);
        Add("file:record", "symbol:record:approve", CodeSemanticClaimKinds.StateAssignment,
            new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", field = "status", fromValue = "DRAFT", method = "approve", methodSymbolId = "symbol:record:approve", ownerSymbolId = "symbol:record", toValue = "APPROVED" }, 25);
        Add("file:record", "symbol:record:reject", CodeSemanticClaimKinds.StateAssignment,
            new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", field = "status", fromValue = (string?)null, method = "rejectUnknownFrom", methodSymbolId = "symbol:record:reject", ownerSymbolId = "symbol:record", toValue = "REJECTED" }, 26);
        Add("file:record", "symbol:record:invalid", CodeSemanticClaimKinds.StateAssignment,
            new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", field = "status", fromValue = "DRAFT", method = "approveInvalidValue", methodSymbolId = "symbol:record:invalid", ownerSymbolId = "symbol:record", toValue = "UNKNOWN" }, 27);
        Add("file:record", "symbol:record:wrong-enum", CodeSemanticClaimKinds.StateAssignment,
            new { enumType = "OtherStatus", enumTypeSymbolId = "symbol:other-status", field = "status", fromValue = "DRAFT", method = "approveWrongEnum", methodSymbolId = "symbol:record:wrong-enum", ownerSymbolId = "symbol:record", toValue = "APPROVED" }, 28);
        Add("file:record", "symbol:record:string-status", CodeSemanticClaimKinds.StateField,
            new { enumType = "String", enumTypeSymbolId = "symbol:string-status", member = "stringStatus", ownerSymbolId = "symbol:record" }, 30);
        Add("file:record", "symbol:record:single-status", CodeSemanticClaimKinds.StateField,
            new { enumType = "SingleStatus", enumTypeSymbolId = "symbol:single-status", member = "singleStatus", ownerSymbolId = "symbol:record" }, 31);
        Add("file:record", "symbol:single-status:only", CodeSemanticClaimKinds.StateValue,
            new { enumType = "SingleStatus", enumTypeSymbolId = "symbol:single-status", value = "ONLY" }, 32);
        Add("file:service", "symbol:service:approve-without-assignment", CodeSemanticClaimKinds.TransactionScope,
            new { annotation = "Transactional", scopeKind = "method", symbolId = "symbol:service:approve-without-assignment", symbolName = "approveWithoutAssignment" }, 15);
        return claims;

        void Add(string fileId, string subjectId, string kind, object payload, int line)
        {
            var json = JsonSerializer.Serialize(payload);
            claims.Add(new CodeSemanticClaimFact(
                CodeSemanticClaimIdentity.Create(fileId, kind, json, line, line),
                subjectId,
                kind,
                json,
                fileId,
                line,
                line,
                0.96,
                "treesitter",
                $"direct {kind}"));
        }
    }

    private static async Task ProjectsLifecycleTransitionsFromGuardedSetterUseCaseSlicesAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        var recordParameterPayload = JsonSerializer.Serialize(new
        {
            collection = false,
            member = "record",
            ownerSymbolId = "symbol:service:approve",
            rawType = "RecordEntity",
            usageKind = "parameter",
            resolvedTypeName = "RecordEntity",
            resolvedTypeSymbolId = "symbol:record",
        });
        var guardPayload = JsonSerializer.Serialize(new
        {
            effectKind = "throw",
            effectMessage = "只有草稿记录可以审批",
            effectSource = "throw new BusinessException(\"只有草稿记录可以审批\")",
            method = "approve",
            methodSymbolId = "symbol:service:approve",
            ownerSymbolId = "symbol:service",
            predicateSource = "!RecordStatus.DRAFT.getCode().equals(record.getStatus())",
            stateGuard = new
            {
                property = "status",
                receiver = "record",
                allowedValues = new[] { "DRAFT" },
                valueEncoding = "enum.getCode",
                source = "!RecordStatus.DRAFT.getCode().equals(record.getStatus())",
            },
        });
        var mutationPayload = JsonSerializer.Serialize(new
        {
            enumType = "RecordStatus",
            enumTypeSymbolId = "symbol:record-status",
            field = "status",
            fromValue = "DRAFT",
            method = "approve",
            methodSymbolId = "symbol:service:approve",
            mutationKind = "setter",
            ownerSymbolId = "symbol:record",
            property = "status",
            rawValue = "RecordStatus.APPROVED.getCode()",
            receiver = "record",
            toValue = "APPROVED",
            valueEncoding = "enum.getCode",
        });
        var setterWithoutGuardPayload = JsonSerializer.Serialize(new
        {
            enumType = "RecordStatus",
            enumTypeSymbolId = "symbol:record-status",
            field = "status",
            fromValue = (string?)null,
            method = "forceApprove",
            methodSymbolId = "symbol:service:force",
            mutationKind = "setter",
            ownerSymbolId = "symbol:record",
            property = "status",
            rawValue = "RecordStatus.APPROVED.getCode()",
            receiver = "record",
            toValue = "APPROVED",
            valueEncoding = "enum.getCode",
        });
        var parameter = Claim("file:service", "symbol:service:approve:param", CodeSemanticClaimKinds.TypedReference, recordParameterPayload, 18);
        var guard = Claim("file:service", "symbol:service:approve", CodeSemanticClaimKinds.BusinessGuard, guardPayload, 22);
        var mutation = Claim("file:service", "symbol:service:approve", CodeSemanticClaimKinds.StateAssignment, mutationPayload, 26);
        var setterWithoutGuard = Claim("file:service", "symbol:service:force", CodeSemanticClaimKinds.StateAssignment, setterWithoutGuardPayload, 35);
        var stateField = Claim("file:record", "symbol:record:status", CodeSemanticClaimKinds.StateField,
            JsonSerializer.Serialize(new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", member = "status", ownerSymbolId = "symbol:record" }), 8);
        var draft = Claim("file:record", "symbol:record-status:draft", CodeSemanticClaimKinds.StateValue,
            JsonSerializer.Serialize(new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", value = "DRAFT" }), 3);
        var approved = Claim("file:record", "symbol:record-status:approved", CodeSemanticClaimKinds.StateValue,
            JsonSerializer.Serialize(new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", value = "APPROVED" }), 4);
        var rejected = Claim("file:record", "symbol:record-status:rejected", CodeSemanticClaimKinds.StateValue,
            JsonSerializer.Serialize(new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", value = "REJECTED" }), 5);

        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:controller", "repo:record", "src/RecordController.java", "java"),
                new CodeFileFact("file:service", "repo:record", "src/RecordService.java", "java"),
                new CodeFileFact("file:repository", "repo:record", "src/RecordRepository.java", "java"),
                new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java"),
                new CodeFileFact("file:status", "repo:record", "src/RecordStatus.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:controller", "file:controller", "RecordController", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:route:approve", "file:controller", "approve", "method", 12, 24, ParentId: "symbol:controller", Lang: "java"),
                new CodeSymbolFact("symbol:service", "file:service", "RecordService", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:service:approve", "file:service", "approve", "method", 18, 30, ParentId: "symbol:service", Lang: "java"),
                new CodeSymbolFact("symbol:service:force", "file:service", "forceApprove", "method", 32, 40, ParentId: "symbol:service", Lang: "java"),
                new CodeSymbolFact("symbol:repo", "file:repository", "RecordRepository", "interface", 1, 40, Lang: "java"),
                new CodeSymbolFact("symbol:repo:save", "file:repository", "save", "method", 8, 8, ParentId: "symbol:repo", Lang: "java"),
                new CodeSymbolFact("symbol:record", "file:record", "RecordEntity", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:record-status", "file:status", "RecordStatus", "enum", 1, 10, Lang: "java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "spring_annotation", "controller"),
                new CodeEdgeFact("symbol:service", "spring:role:service", "SPRING_ROLE", "file:service", 1, 0.98, "spring_annotation", "service"),
                new CodeEdgeFact("symbol:repo", "spring:role:repository", "SPRING_ROLE", "file:repository", 1, 0.98, "spring_annotation", "repository"),
                new CodeEdgeFact("symbol:record", "spring:role:entity", "SPRING_ROLE", "file:record", 1, 0.98, "spring_annotation", "entity"),
                new CodeEdgeFact("symbol:route:approve", "symbol:service:approve", CodeEdgeKinds.Calls, "file:controller", 16, 1.0, "treesitter", "controller calls service"),
                new CodeEdgeFact("symbol:service:approve", "symbol:repo:save", CodeEdgeKinds.Calls, "file:service", 28, 1.0, "treesitter", "service calls repository"),
                new CodeEdgeFact("symbol:repo:save", "symbol:record", CodeEdgeKinds.Calls, "file:repository", 8, 1.0, "treesitter", "repository writes entity"),
            ],
            EntryPoints: [new CodeEntryPointFact("symbol:route:approve", "http_route", "POST /api/records/{id}/approve")],
            SemanticClaims: [parameter, guard, mutation, setterWithoutGuard, stateField, draft, approved, rejected]));

        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(BaseGeneration());
        await new BusinessOntologySemanticProjector(om, store)
            .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "semantic-lifecycle-slice",
                "semantic-lifecycle-slice-fingerprint",
                "2026-07-18T14:00:00Z"));
        var snapshot = await store.ReadExportableAsync(OntologyId);
        var lifecycle = snapshot.Candidates.Single(item => item.SubjectKind == OntologySemanticCandidateKinds.Lifecycle);
        using var document = JsonDocument.Parse(lifecycle.PayloadJson);
        var semantic = document.RootElement.GetProperty("semantic");
        var transitions = semantic.GetProperty("transitions").EnumerateArray().ToArray();
        assert(transitions.Length == 1
                && transitions[0].GetProperty("fromState").GetString() == "DRAFT"
                && transitions[0].GetProperty("toState").GetString() == "APPROVED"
                && transitions[0].GetProperty("guard").GetProperty("source").GetString() == "!RecordStatus.DRAFT.getCode().equals(record.getStatus())"
                && transitions[0].GetProperty("effect").GetProperty("set").GetProperty("valueEncoding").GetString() == "enum.getCode"
                && transitions[0].GetProperty("descriptionZh").GetString()!.Contains("只有草稿记录可以审批", StringComparison.Ordinal),
            "guarded setter state mutation in a cross-file use-case slice should produce one complete lifecycle transition");
        assert(lifecycle.EvidenceIds.Contains(guard.ClaimId, StringComparer.Ordinal)
                && lifecycle.EvidenceIds.Contains(mutation.ClaimId, StringComparer.Ordinal)
                && lifecycle.EvidenceIds.Contains(parameter.ClaimId, StringComparer.Ordinal)
                && lifecycle.EvidenceIds.Any(id => id.StartsWith("evidence:usecase-slice-", StringComparison.Ordinal)
                    && id.Count(ch => ch == ':') == 1)
                && !lifecycle.EvidenceIds.Contains(setterWithoutGuard.ClaimId, StringComparer.Ordinal),
            "lifecycle transition evidence should include guard, mutation, typed subject, and use-case slice, while excluding setter-only mutations");
    }

    private static async Task ProjectsRepairLifecycleThroughInterfaceAndLargeMethodAsync(Action<bool, string> assert)
    {
        const string damageConcept = OntologyId + ".RecordDamageInfo";
        const string repairMethod = "symbol:repair-impl:save";
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        var guard = Claim(
            "file:repair-impl",
            repairMethod,
            CodeSemanticClaimKinds.BusinessGuard,
            JsonSerializer.Serialize(new
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
            }),
            126);
        var code2 = Claim(
            "file:repair-impl",
            repairMethod,
            CodeSemanticClaimKinds.StateAssignment,
            RepairMutationPayload("CODE_2"),
            132);
        var code5 = Claim(
            "file:repair-impl",
            repairMethod,
            CodeSemanticClaimKinds.StateAssignment,
            RepairMutationPayload("CODE_5"),
            138);
        var methodTypedReferences = Enumerable.Range(0, 32)
            .Select(index => Claim(
                "file:repair-impl",
                repairMethod,
                CodeSemanticClaimKinds.TypedReference,
                JsonSerializer.Serialize(new
                {
                    collection = false,
                    declaringSymbolId = repairMethod,
                    member = index == 0 ? "damageInfosModel" : $"auxiliary{index:D2}",
                    ownerSymbolId = "symbol:repair-impl",
                    rawType = "RecordDamageInfo",
                    resolvedTypeName = "RecordDamageInfo",
                    resolvedTypeSymbolId = "symbol:damage-info",
                    usageKind = index == 0 ? "parameter" : "local",
                }),
                112 + index))
            .ToArray();
        var unrelatedCommonTypeClaims = Enumerable.Range(0, 48)
            .Select(index => Claim(
                "file:unrelated",
                $"symbol:unrelated:{index:D2}",
                CodeSemanticClaimKinds.TypedReference,
                JsonSerializer.Serialize(new
                {
                    collection = false,
                    declaringSymbolId = $"symbol:unrelated:{index:D2}",
                    member = $"result{index:D2}",
                    ownerSymbolId = index % 2 == 0 ? "symbol:damage-info" : "symbol:unrelated-service",
                    rawType = "Result",
                    resolvedTypeName = "Result",
                    resolvedTypeSymbolId = "symbol:result",
                    usageKind = "local",
                }),
                220 + index))
            .ToArray();

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
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:repair-controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "spring_annotation", "controller"),
                new CodeEdgeFact("symbol:repair-impl", "spring:role:service", "SPRING_ROLE", "file:repair-impl", 1, 0.98, "spring_annotation", "service"),
                new CodeEdgeFact("symbol:damage-mapper", "spring:role:repository", "SPRING_ROLE", "file:mapper", 1, 0.98, "spring_annotation", "repository"),
                new CodeEdgeFact("symbol:damage-info", "spring:role:entity", "SPRING_ROLE", "file:damage", 1, 0.98, "spring_annotation", "entity"),
                new CodeEdgeFact("symbol:repair-route", "symbol:repair-interface:save", CodeEdgeKinds.Calls, "file:controller", 66, 0.9, "treesitter", "controller calls service interface"),
                new CodeEdgeFact(repairMethod, "symbol:repair-interface:save", CodeEdgeKinds.MethodImplements, "file:repair-impl", 110, 0.9, "treesitter", "implementation dispatch"),
                new CodeEdgeFact(repairMethod, "symbol:damage-mapper:update", CodeEdgeKinds.Calls, "file:repair-impl", 150, 0.9, "treesitter", "persist damage info"),
                new CodeEdgeFact("symbol:damage-mapper:update", "symbol:damage-info", CodeEdgeKinds.Calls, "file:mapper", 20, 0.9, "treesitter", "mapper writes entity"),
                new CodeEdgeFact("symbol:repair-route", "symbol:result", CodeEdgeKinds.Calls, "file:controller", 68, 0.9, "treesitter", "route returns common Result"),
            ],
            EntryPoints: [new CodeEntryPointFact("symbol:repair-route", "http_route", "POST /api/v1/repair/save")],
            SemanticClaims:
            [
                guard,
                code2,
                code5,
                .. methodTypedReferences,
                .. unrelatedCommonTypeClaims,
            ]));

        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(new BusinessOntologyGenerationInput(
            OntologyId,
            "repair-base",
            "repair-base-fingerprint",
            "fixture/1",
            "2026-07-18T16:00:00Z",
            [
                new BusinessOntologyConcept(damageConcept, "businessObject", "记录损坏信息", "记录维修中的损坏信息。", "accepted", 0.95, ["base:damage"]),
            ],
            [],
            [],
            [],
            [],
            [],
            [],
            [
                new BusinessOntologyMapping(OntologyId + ".Mapping.RecordDamageInfo", "concept", damageConcept, "representedBy", "repo:record", "java", "class", "symbol:damage-info", "src/RecordDamageInfo.java", "treesitter", 0.95, "accepted", ["base:damage"]),
            ],
            [
                new BusinessOntologyEvidence("base:damage", "repo:record", "src/RecordDamageInfo.java", "symbol:damage-info", 1, 80, "contractual", "treesitter", 0.95, "code", "记录损坏信息实体。"),
            ],
            [],
            [],
            []));

        var result = await new BusinessOntologySemanticProjector(om, store)
            .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "semantic-repair-large-method",
                "semantic-repair-large-method-fingerprint",
                "2026-07-18T16:05:00Z"));
        var snapshot = await store.ReadExportableAsync(OntologyId);
        var lifecycle = snapshot.Candidates.Single(item =>
            item.SubjectKind == OntologySemanticCandidateKinds.Lifecycle);
        using var document = JsonDocument.Parse(lifecycle.PayloadJson);
        var semantic = document.RootElement.GetProperty("semantic");
        var transitions = semantic.GetProperty("transitions")
            .EnumerateArray()
            .Select(item => (
                From: item.GetProperty("fromState").GetString(),
                To: item.GetProperty("toState").GetString(),
                Receiver: item.GetProperty("guard").GetProperty("source").GetString()))
            .ToArray();

        assert(result.LifecycleCandidates == 1
                && semantic.GetProperty("subjectConceptId").GetString() == damageConcept
                && transitions.Length == 2
                && transitions.All(item => item.From == "CODE_1")
                && transitions.Select(item => item.To).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "CODE_2", "CODE_5" }, StringComparer.Ordinal),
            "controller -> interface -> METHOD_IMPLEMENTS -> large repair method should reconstruct both RecordDamageInfo.state transitions without MaxClaims loss");
        assert(lifecycle.EvidenceIds.Contains(guard.ClaimId, StringComparer.Ordinal)
                && lifecycle.EvidenceIds.Contains(code2.ClaimId, StringComparer.Ordinal)
                && lifecycle.EvidenceIds.Contains(code5.ClaimId, StringComparer.Ordinal)
                && lifecycle.EvidenceIds.Any(id => id.StartsWith("evidence:usecase-slice-", StringComparison.Ordinal))
                && !lifecycle.EvidenceIds.Any(id => unrelatedCommonTypeClaims.Any(item => item.ClaimId == id)),
            "repair lifecycle evidence should retain direct guard/mutations and exclude unrelated claims joined only through common Result or entity types");

        string RepairMutationPayload(string toValue) => JsonSerializer.Serialize(new
        {
            field = "state",
            fromValue = "CODE_1",
            method = "repairSave",
            methodSymbolId = repairMethod,
            mutationKind = "setter",
            ownerSymbolId = "symbol:damage-info",
            property = "state",
            rawValue = $"\"{toValue}\"",
            receiver = "damageInfosModel",
            toValue,
            valueEncoding = "string",
        });
    }

    private static async Task RequiresProvableLifecycleTransitionScopeAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        var parameter = Claim("file:service", "symbol:service:migrate:param", CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = false,
                member = "record",
                ownerSymbolId = "symbol:service:migrate",
                rawType = "RecordEntity",
                usageKind = "parameter",
                resolvedTypeName = "RecordEntity",
                resolvedTypeSymbolId = "symbol:record",
            }), 18);
        var multiGuard = Claim("file:service", "symbol:service:migrate", CodeSemanticClaimKinds.BusinessGuard,
            JsonSerializer.Serialize(new
            {
                effectKind = "throw",
                effectMessage = "只有新旧可用记录可以迁移",
                effectSource = "throw new BusinessException()",
                method = "migrate",
                methodSymbolId = "symbol:service:migrate",
                ownerSymbolId = "symbol:service",
                predicateSource = "!AVAILABLE_NEW.equals(record.getStatus()) && !AVAILABLE_OLD.equals(record.getStatus())",
                stateGuard = new
                {
                    property = "status",
                    receiver = "record",
                    allowedValues = new[] { "AVAILABLE_NEW", "AVAILABLE_OLD" },
                    valueEncoding = "string",
                    source = "!AVAILABLE_NEW.equals(record.getStatus()) && !AVAILABLE_OLD.equals(record.getStatus())",
                },
            }), 22);
        var multiMutation = Claim("file:service", "symbol:service:migrate", CodeSemanticClaimKinds.StateAssignment,
            JsonSerializer.Serialize(new
            {
                field = "status",
                fromValue = (string?)null,
                method = "migrate",
                methodSymbolId = "symbol:service:migrate",
                mutationKind = "setter",
                ownerSymbolId = "symbol:record",
                property = "status",
                rawValue = "\"MIGRATED\"",
                receiver = "record",
                toValue = "MIGRATED",
                valueEncoding = "string",
            }), 25);
        var mutationBeforeGuard = Claim("file:service", "symbol:service:migrate", CodeSemanticClaimKinds.StateAssignment,
            JsonSerializer.Serialize(new
            {
                enumType = "RecordStatus",
                enumTypeSymbolId = "symbol:record-status",
                field = "status",
                fromValue = "DRAFT",
                method = "migrate",
                methodSymbolId = "symbol:service:migrate",
                mutationKind = "setter",
                ownerSymbolId = "symbol:record",
                property = "status",
                rawValue = "RecordStatus.APPROVED",
                receiver = "record",
                toValue = "APPROVED",
                valueEncoding = "enum.member",
            }), 26);
        var guardAfterMutation = Claim("file:service", "symbol:service:migrate", CodeSemanticClaimKinds.BusinessGuard,
            JsonSerializer.Serialize(new
            {
                effectKind = "throw",
                effectMessage = "后置拒绝条件不能证明变更前状态",
                effectSource = "throw new BusinessException()",
                method = "migrate",
                methodSymbolId = "symbol:service:migrate",
                ownerSymbolId = "symbol:service",
                predicateSource = "record.getStatus() != RecordStatus.DRAFT",
                stateGuard = new
                {
                    property = "status",
                    receiver = "record",
                    allowedValues = new[] { "DRAFT" },
                    valueEncoding = "enum.member",
                    source = "record.getStatus() != RecordStatus.DRAFT",
                },
            }), 27);
        var historyMutation = Claim("file:service", "symbol:service:migrate", CodeSemanticClaimKinds.StateAssignment,
            JsonSerializer.Serialize(new
            {
                field = "status",
                fromValue = "CREATED",
                method = "migrate",
                methodSymbolId = "symbol:service:migrate",
                mutationKind = "setter",
                ownerSymbolId = "symbol:history",
                property = "status",
                rawValue = "\"DONE\"",
                receiver = "history",
                toValue = "DONE",
                valueEncoding = "string",
            }), 26);
        var uniqueGuard = Claim("file:service", "symbol:service:approve", CodeSemanticClaimKinds.BusinessGuard,
            JsonSerializer.Serialize(new
            {
                effectKind = "throw",
                effectMessage = "只有草稿记录可以审批",
                effectSource = "throw new BusinessException()",
                method = "approve",
                methodSymbolId = "symbol:service:approve",
                ownerSymbolId = "symbol:service",
                predicateSource = "record.getStatus() != RecordStatus.DRAFT",
                stateGuard = new
                {
                    property = "status",
                    receiver = "record",
                    allowedValues = new[] { "DRAFT" },
                    valueEncoding = "enum.member",
                    source = "record.getStatus() != RecordStatus.DRAFT",
                },
            }), 30);
        var uniqueMutation = Claim("file:service", "symbol:service:approve", CodeSemanticClaimKinds.StateAssignment,
            JsonSerializer.Serialize(new
            {
                enumType = "RecordStatus",
                enumTypeSymbolId = "symbol:record-status",
                field = "status",
                fromValue = "DRAFT",
                method = "approve",
                methodSymbolId = "symbol:service:approve",
                mutationKind = "setter",
                ownerSymbolId = "symbol:record",
                property = "status",
                rawValue = "RecordStatus.APPROVED",
                receiver = "record",
                toValue = "APPROVED",
                valueEncoding = "enum.member",
            }), 32);
        var stateField = Claim("file:record", "symbol:record:status", CodeSemanticClaimKinds.StateField,
            JsonSerializer.Serialize(new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", member = "status", ownerSymbolId = "symbol:record" }), 8);
        var states = new[]
        {
            ("symbol:record-status:draft", "DRAFT", 3),
            ("symbol:record-status:approved", "APPROVED", 4),
            ("symbol:record-status:new", "AVAILABLE_NEW", 5),
            ("symbol:record-status:old", "AVAILABLE_OLD", 6),
            ("symbol:record-status:migrated", "MIGRATED", 7),
        }.Select(item => Claim("file:record", item.Item1, CodeSemanticClaimKinds.StateValue,
            JsonSerializer.Serialize(new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", value = item.Item2 }), item.Item3)).ToArray();

        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:controller", "repo:record", "src/RecordController.java", "java"),
                new CodeFileFact("file:service", "repo:record", "src/RecordService.java", "java"),
                new CodeFileFact("file:repository", "repo:record", "src/RecordRepository.java", "java"),
                new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java"),
                new CodeFileFact("file:history", "repo:record", "src/RecordHistoryEntity.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:controller", "file:controller", "RecordController", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:route:migrate", "file:controller", "migrate", "method", 12, 24, ParentId: "symbol:controller", Lang: "java"),
                new CodeSymbolFact("symbol:route:approve", "file:controller", "approve", "method", 26, 38, ParentId: "symbol:controller", Lang: "java"),
                new CodeSymbolFact("symbol:service", "file:service", "RecordService", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:service:migrate", "file:service", "migrate", "method", 18, 28, ParentId: "symbol:service", Lang: "java"),
                new CodeSymbolFact("symbol:service:approve", "file:service", "approve", "method", 30, 38, ParentId: "symbol:service", Lang: "java"),
                new CodeSymbolFact("symbol:repo", "file:repository", "RecordRepository", "interface", 1, 40, Lang: "java"),
                new CodeSymbolFact("symbol:repo:save", "file:repository", "save", "method", 8, 8, ParentId: "symbol:repo", Lang: "java"),
                new CodeSymbolFact("symbol:record", "file:record", "RecordEntity", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:history", "file:history", "RecordHistoryEntity", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:record-status", "file:record", "RecordStatus", "enum", 1, 12, Lang: "java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "spring_annotation", "controller"),
                new CodeEdgeFact("symbol:service", "spring:role:service", "SPRING_ROLE", "file:service", 1, 0.98, "spring_annotation", "service"),
                new CodeEdgeFact("symbol:repo", "spring:role:repository", "SPRING_ROLE", "file:repository", 1, 0.98, "spring_annotation", "repository"),
                new CodeEdgeFact("symbol:record", "spring:role:entity", "SPRING_ROLE", "file:record", 1, 0.98, "spring_annotation", "entity"),
                new CodeEdgeFact("symbol:history", "spring:role:entity", "SPRING_ROLE", "file:history", 1, 0.98, "spring_annotation", "entity"),
                new CodeEdgeFact("symbol:route:migrate", "symbol:service:migrate", CodeEdgeKinds.Calls, "file:controller", 16, 1.0, "treesitter", "controller calls service"),
                new CodeEdgeFact("symbol:route:approve", "symbol:service:approve", CodeEdgeKinds.Calls, "file:controller", 29, 1.0, "treesitter", "controller calls service"),
                new CodeEdgeFact("symbol:service:migrate", "symbol:repo:save", CodeEdgeKinds.Calls, "file:service", 27, 1.0, "treesitter", "service persists record"),
                new CodeEdgeFact("symbol:service:approve", "symbol:repo:save", CodeEdgeKinds.Calls, "file:service", 36, 1.0, "treesitter", "service persists record"),
                new CodeEdgeFact("symbol:repo:save", "symbol:record", CodeEdgeKinds.Calls, "file:repository", 8, 1.0, "treesitter", "repository writes entity"),
            ],
            EntryPoints:
            [
                new CodeEntryPointFact("symbol:route:migrate", "http_route", "POST /api/records/{id}/migrate"),
                new CodeEntryPointFact("symbol:route:approve", "http_route", "POST /api/records/{id}/approve"),
            ],
            SemanticClaims:
            [
                parameter,
                multiGuard,
                multiMutation,
                mutationBeforeGuard,
                guardAfterMutation,
                historyMutation,
                uniqueGuard,
                uniqueMutation,
                stateField,
                .. states,
            ]));

        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(BaseGeneration());
        await new BusinessOntologySemanticProjector(om, store)
            .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "semantic-lifecycle-scope",
                "semantic-lifecycle-scope-fingerprint",
                "2026-07-18T15:10:00Z"));
        var snapshot = await store.ReadExportableAsync(OntologyId);
        var lifecycle = snapshot.Candidates.Single(item => item.SubjectKind == OntologySemanticCandidateKinds.Lifecycle);
        using var document = JsonDocument.Parse(lifecycle.PayloadJson);
        var semantic = document.RootElement.GetProperty("semantic");
        var transitions = semantic.GetProperty("transitions").EnumerateArray().ToArray();

        assert(transitions.Length == 1
                && transitions[0].GetProperty("action").GetString() == "approve"
                && transitions[0].GetProperty("fromState").GetString() == "DRAFT"
                && transitions[0].GetProperty("toState").GetString() == "APPROVED"
                && !lifecycle.PayloadJson.Contains("CREATED", StringComparison.Ordinal)
                && !lifecycle.EvidenceIds.Contains(multiMutation.ClaimId, StringComparer.Ordinal)
                && !lifecycle.EvidenceIds.Contains(mutationBeforeGuard.ClaimId, StringComparer.Ordinal)
                && !lifecycle.EvidenceIds.Contains(guardAfterMutation.ClaimId, StringComparer.Ordinal)
                && !lifecycle.EvidenceIds.Contains(historyMutation.ClaimId, StringComparer.Ordinal),
            "lifecycle transitions require a unique preceding same-method guard, same business object state write, and persistence/use-case evidence; later guards, multi-value guards, and history entity writes must not fabricate record transitions");
    }

    private static async Task ProjectsScalarLifecycleTransitionsFromObservedValuesAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        var parameter = Claim(
            "file:service",
            "symbol:service:complete:param",
            CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = false,
                member = "record",
                ownerSymbolId = "symbol:service:complete",
                rawType = "RecordEntity",
                usageKind = "parameter",
                resolvedTypeName = "RecordEntity",
                resolvedTypeSymbolId = "symbol:record",
            }),
            18);
        var stringGuard = Claim(
            "file:service",
            "symbol:service:complete",
            CodeSemanticClaimKinds.BusinessGuard,
            JsonSerializer.Serialize(new
            {
                effectKind = "throw",
                effectMessage = "只有打开的同步任务可以关闭",
                effectSource = "throw new BusinessException()",
                method = "complete",
                methodSymbolId = "symbol:service:complete",
                ownerSymbolId = "symbol:service",
                predicateSource = "!StringUtils.equals(record.getSyncStatus(), \"OPEN\")",
                stateGuard = new
                {
                    property = "syncStatus",
                    receiver = "record",
                    allowedValues = new[] { "OPEN" },
                    valueEncoding = "string",
                    source = "!StringUtils.equals(record.getSyncStatus(), \"OPEN\")",
                },
            }),
            22);
        var stringMutation = Claim(
            "file:service",
            "symbol:service:complete",
            CodeSemanticClaimKinds.StateAssignment,
            JsonSerializer.Serialize(new
            {
                enumType = "",
                enumTypeSymbolId = "",
                field = "syncStatus",
                fromValue = "OPEN",
                method = "complete",
                methodSymbolId = "symbol:service:complete",
                mutationKind = "setter",
                ownerSymbolId = "symbol:record",
                property = "syncStatus",
                rawValue = "\"CLOSED\"",
                receiver = "record",
                toValue = "CLOSED",
                valueEncoding = "string",
            }),
            25);
        var numericGuard = Claim(
            "file:service",
            "symbol:service:complete",
            CodeSemanticClaimKinds.BusinessGuard,
            JsonSerializer.Serialize(new
            {
                effectKind = "throw",
                effectMessage = "只有冻结码 1 可以推进",
                effectSource = "throw new BusinessException()",
                method = "complete",
                methodSymbolId = "symbol:service:complete",
                ownerSymbolId = "symbol:service",
                predicateSource = "record.getFreezeState() != 1",
                stateGuard = new
                {
                    property = "freezeState",
                    receiver = "record",
                    allowedValues = new[] { "CODE_1" },
                    valueEncoding = "numeric",
                    source = "record.getFreezeState() != 1",
                },
            }),
            27);
        var numericMutation = Claim(
            "file:service",
            "symbol:service:complete",
            CodeSemanticClaimKinds.StateAssignment,
            JsonSerializer.Serialize(new
            {
                enumType = "",
                enumTypeSymbolId = "",
                field = "freezeState",
                fromValue = "CODE_1",
                method = "complete",
                methodSymbolId = "symbol:service:complete",
                mutationKind = "setter",
                ownerSymbolId = "symbol:record",
                property = "freezeState",
                rawValue = "2",
                receiver = "record",
                toValue = "CODE_2",
                valueEncoding = "numeric",
            }),
            29);
        var enumGuard = Claim(
            "file:service",
            "symbol:service:complete",
            CodeSemanticClaimKinds.BusinessGuard,
            JsonSerializer.Serialize(new
            {
                effectKind = "throw",
                effectMessage = "只有草稿记录可以审批",
                effectSource = "throw new BusinessException()",
                method = "complete",
                methodSymbolId = "symbol:service:complete",
                ownerSymbolId = "symbol:service",
                predicateSource = "record.getRecordStatus() != RecordStatus.DRAFT.getCode()",
                stateGuard = new
                {
                    property = "recordStatus",
                    receiver = "record",
                    allowedValues = new[] { "DRAFT" },
                    valueEncoding = "enum.getCode",
                    source = "record.getRecordStatus() != RecordStatus.DRAFT.getCode()",
                },
            }),
            30);
        var enumMutation = Claim(
            "file:service",
            "symbol:service:complete",
            CodeSemanticClaimKinds.StateAssignment,
            JsonSerializer.Serialize(new
            {
                enumType = "RecordStatus",
                enumTypeSymbolId = "",
                field = "recordStatus",
                fromValue = "DRAFT",
                method = "complete",
                methodSymbolId = "symbol:service:complete",
                mutationKind = "setter",
                ownerSymbolId = "symbol:record",
                property = "recordStatus",
                rawValue = "RecordStatus.APPROVED.getCode()",
                receiver = "record",
                toValue = "APPROVED",
                valueEncoding = "enum.getCode",
            }),
            31);
        var incompleteMutation = Claim(
            "file:service",
            "symbol:service:complete",
            CodeSemanticClaimKinds.StateAssignment,
            JsonSerializer.Serialize(new
            {
                field = "repairStatus",
                fromValue = (string?)null,
                method = "complete",
                methodSymbolId = "symbol:service:complete",
                mutationKind = "setter",
                ownerSymbolId = "symbol:record",
                property = "repairStatus",
                rawValue = "RepairStatus.DONE.getCode()",
                receiver = "record",
                toValue = "DONE",
                valueEncoding = "enum.getCode",
            }),
            32);
        var nonStateGuard = Claim(
            "file:service",
            "symbol:service:complete",
            CodeSemanticClaimKinds.BusinessGuard,
            JsonSerializer.Serialize(new
            {
                effectKind = "throw",
                effectMessage = "标签不匹配",
                effectSource = "throw new BusinessException()",
                method = "complete",
                methodSymbolId = "symbol:service:complete",
                ownerSymbolId = "symbol:service",
                predicateSource = "!StringUtils.equals(record.getStatusLabel(), \"OLD\")",
                stateGuard = new
                {
                    property = "statusLabel",
                    receiver = "record",
                    allowedValues = new[] { "OLD" },
                    valueEncoding = "string",
                    source = "!StringUtils.equals(record.getStatusLabel(), \"OLD\")",
                },
            }),
            34);
        var nonStateMutation = Claim(
            "file:service",
            "symbol:service:complete",
            CodeSemanticClaimKinds.StateAssignment,
            JsonSerializer.Serialize(new
            {
                field = "statusLabel",
                fromValue = "OLD",
                method = "complete",
                methodSymbolId = "symbol:service:complete",
                mutationKind = "setter",
                ownerSymbolId = "symbol:record",
                property = "statusLabel",
                rawValue = "\"NEW\"",
                receiver = "record",
                toValue = "NEW",
                valueEncoding = "string",
            }),
            35);

        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:controller", "repo:record", "src/RecordController.java", "java"),
                new CodeFileFact("file:service", "repo:record", "src/RecordService.java", "java"),
                new CodeFileFact("file:repository", "repo:record", "src/RecordRepository.java", "java"),
                new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:controller", "file:controller", "RecordController", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:route:complete", "file:controller", "complete", "method", 12, 24, ParentId: "symbol:controller", Lang: "java"),
                new CodeSymbolFact("symbol:service", "file:service", "RecordService", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:service:complete", "file:service", "complete", "method", 18, 40, ParentId: "symbol:service", Lang: "java"),
                new CodeSymbolFact("symbol:repo", "file:repository", "RecordRepository", "interface", 1, 40, Lang: "java"),
                new CodeSymbolFact("symbol:repo:save", "file:repository", "save", "method", 8, 8, ParentId: "symbol:repo", Lang: "java"),
                new CodeSymbolFact("symbol:record", "file:record", "RecordEntity", "class", 1, 80, Lang: "java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "spring_annotation", "controller"),
                new CodeEdgeFact("symbol:service", "spring:role:service", "SPRING_ROLE", "file:service", 1, 0.98, "spring_annotation", "service"),
                new CodeEdgeFact("symbol:repo", "spring:role:repository", "SPRING_ROLE", "file:repository", 1, 0.98, "spring_annotation", "repository"),
                new CodeEdgeFact("symbol:record", "spring:role:entity", "SPRING_ROLE", "file:record", 1, 0.98, "spring_annotation", "entity"),
                new CodeEdgeFact("symbol:route:complete", "symbol:service:complete", CodeEdgeKinds.Calls, "file:controller", 16, 1.0, "treesitter", "controller calls service"),
                new CodeEdgeFact("symbol:service:complete", "symbol:repo:save", CodeEdgeKinds.Calls, "file:service", 36, 1.0, "treesitter", "service calls repository"),
                new CodeEdgeFact("symbol:repo:save", "symbol:record", CodeEdgeKinds.Calls, "file:repository", 8, 1.0, "treesitter", "repository writes entity"),
            ],
            EntryPoints: [new CodeEntryPointFact("symbol:route:complete", "http_route", "POST /api/records/{id}/complete")],
            SemanticClaims:
            [
                parameter,
                stringGuard,
                stringMutation,
                numericGuard,
                numericMutation,
                enumGuard,
                enumMutation,
                incompleteMutation,
                nonStateGuard,
                nonStateMutation,
            ]));

        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(BaseGeneration());
        var result = await new BusinessOntologySemanticProjector(om, store)
            .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "semantic-scalar-lifecycles",
                "semantic-scalar-lifecycles-fingerprint",
                "2026-07-18T14:10:00Z"));
        var snapshot = await store.ReadExportableAsync(OntologyId);
        var lifecycles = snapshot.Candidates
            .Where(item => item.SubjectKind == OntologySemanticCandidateKinds.Lifecycle)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        assert(result.LifecycleCandidates == 3 && lifecycles.Length == 3,
            "complete guarded string, numeric, and statically normalized enum mutations should each form a scalar lifecycle without state_field/state_value claims");
        var transitions = lifecycles.Select(candidate =>
        {
            using var document = JsonDocument.Parse(candidate.PayloadJson);
            var semantic = document.RootElement.GetProperty("semantic");
            var transition = semantic.GetProperty("transitions").EnumerateArray().Single();
            return (
                Property: semantic.GetProperty("stateProperty").GetString() ?? "",
                States: semantic.GetProperty("states").EnumerateArray()
                    .Select(item => item.GetProperty("id").GetString() ?? "")
                    .ToArray(),
                From: transition.GetProperty("fromState").GetString() ?? "",
                To: transition.GetProperty("toState").GetString() ?? "",
                Encoding: transition.GetProperty("effect").GetProperty("set").GetProperty("valueEncoding").GetString() ?? "");
        }).ToArray();
        assert(transitions.Any(item => item.Property == "syncStatus"
                    && item.States.SequenceEqual(new[] { "OPEN", "CLOSED" }, StringComparer.Ordinal)
                    && item.From == "OPEN"
                    && item.To == "CLOSED"
                    && item.Encoding == "string")
                && transitions.Any(item => item.Property == "freezeState"
                    && item.States.SequenceEqual(new[] { "CODE_1", "CODE_2" }, StringComparer.Ordinal)
                    && item.From == "CODE_1"
                    && item.To == "CODE_2"
                    && item.Encoding == "numeric")
                && transitions.Any(item => item.Property == "recordStatus"
                    && item.States.SequenceEqual(new[] { "DRAFT", "APPROVED" }, StringComparer.Ordinal)
                    && item.From == "DRAFT"
                    && item.To == "APPROVED"
                    && item.Encoding == "enum.getCode"),
            "scalar lifecycle payloads should expose observed finite states and complete string, numeric, and enum.getCode transitions");
        assert(!lifecycles.Any(item => item.PayloadJson.Contains("repairStatus", StringComparison.Ordinal)
                || item.PayloadJson.Contains("statusLabel", StringComparison.Ordinal)),
            "setter-only scalar mutations and non-state setter names must not create synthetic lifecycle candidates");
    }

    private static async Task ProjectsMixedEncodingScalarLifecycleTransitionsFromGuardedAssignmentsAsync(Action<bool, string> assert)
    {
        const string damageConcept = OntologyId + ".RecordDamageInfo";
        const string repairMethod = "symbol:repair:save";
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        var parameter = Claim(
            "file:service",
            repairMethod,
            CodeSemanticClaimKinds.TypedReference,
            JsonSerializer.Serialize(new
            {
                collection = false,
                declaringSymbolId = repairMethod,
                member = "damageInfosModel",
                ownerSymbolId = "symbol:repair-service",
                rawType = "RecordDamageInfosEntity",
                usageKind = "parameter",
                resolvedTypeName = "RecordDamageInfosEntity",
                resolvedTypeSymbolId = "symbol:damage-info",
            }),
            118);
        var guard = Claim(
            "file:service",
            repairMethod,
            CodeSemanticClaimKinds.BusinessGuard,
            JsonSerializer.Serialize(new
            {
                effectKind = "throw",
                effectMessage = "只有待维修损坏信息可以保存维修结果",
                effectSource = "throw new BusinessException()",
                method = "repairSave",
                methodSymbolId = repairMethod,
                ownerSymbolId = "symbol:repair-service",
                predicateSource = "damageInfosModel.getState() != 1",
                stateGuard = new
                {
                    property = "state",
                    receiver = "damageInfosModel",
                    allowedValues = new[] { "CODE_1" },
                    valueEncoding = "numeric",
                    source = "damageInfosModel.getState() != 1",
                },
            }),
            126);
        var enumSetterWithoutFrom = Claim(
            "file:service",
            repairMethod,
            CodeSemanticClaimKinds.StateAssignment,
            JsonSerializer.Serialize(new
            {
                enumType = "DamageStateEnum",
                enumTypeSymbolId = "",
                field = "state",
                fromValue = (string?)null,
                method = "repairSave",
                methodSymbolId = repairMethod,
                mutationKind = "setter",
                ownerSymbolId = "symbol:damage-info",
                property = "state",
                rawValue = "DamageStateEnum.IGNORED.getCode()",
                receiver = "damageInfosModel",
                toValue = "CODE_9",
                valueEncoding = "enum.getCode",
            }),
            128);
        var numericCode2 = RepairMutation("CODE_2", "2", 132);
        var numericCode5 = RepairMutation("CODE_5", "5", 138);

        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:controller", "repo:record", "src/RecordRepairController.java", "java"),
                new CodeFileFact("file:service", "repo:record", "src/RecordRepairServiceImpl.java", "java"),
                new CodeFileFact("file:mapper", "repo:record", "src/RecordDamageInfosMapper.java", "java"),
                new CodeFileFact("file:damage", "repo:record", "src/RecordDamageInfosEntity.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:controller", "file:controller", "RecordRepairController", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:route:repair", "file:controller", "repairSave", "method", 60, 72, ParentId: "symbol:controller", Lang: "java"),
                new CodeSymbolFact("symbol:repair-service", "file:service", "RecordRepairServiceImpl", "class", 1, 220, Lang: "java"),
                new CodeSymbolFact(repairMethod, "file:service", "repairSave", "method", 110, 180, ParentId: "symbol:repair-service", Lang: "java"),
                new CodeSymbolFact("symbol:mapper", "file:mapper", "RecordDamageInfosMapper", "interface", 1, 60, Lang: "java"),
                new CodeSymbolFact("symbol:mapper:update", "file:mapper", "updateById", "method", 20, 20, ParentId: "symbol:mapper", Lang: "java"),
                new CodeSymbolFact("symbol:damage-info", "file:damage", "RecordDamageInfosEntity", "class", 1, 80, Lang: "java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "spring_annotation", "controller"),
                new CodeEdgeFact("symbol:repair-service", "spring:role:service", "SPRING_ROLE", "file:service", 1, 0.98, "spring_annotation", "service"),
                new CodeEdgeFact("symbol:mapper", "spring:role:repository", "SPRING_ROLE", "file:mapper", 1, 0.98, "spring_annotation", "repository"),
                new CodeEdgeFact("symbol:damage-info", "spring:role:entity", "SPRING_ROLE", "file:damage", 1, 0.98, "spring_annotation", "entity"),
                new CodeEdgeFact("symbol:route:repair", repairMethod, CodeEdgeKinds.Calls, "file:controller", 66, 1.0, "treesitter", "controller calls repair service"),
                new CodeEdgeFact(repairMethod, "symbol:mapper:update", CodeEdgeKinds.Calls, "file:service", 150, 1.0, "treesitter", "service persists repair result"),
                new CodeEdgeFact("symbol:mapper:update", "symbol:damage-info", CodeEdgeKinds.Calls, "file:mapper", 20, 1.0, "treesitter", "mapper writes entity"),
            ],
            EntryPoints: [new CodeEntryPointFact("symbol:route:repair", "http_route", "POST /api/v1/repair/save")],
            SemanticClaims:
            [
                parameter,
                guard,
                enumSetterWithoutFrom,
                numericCode2,
                numericCode5,
            ]));

        var store = new BusinessOntologyStore(om);
        await store.ReplaceGenerationAsync(new BusinessOntologyGenerationInput(
            OntologyId,
            "mixed-scalar-base",
            "mixed-scalar-base-fingerprint",
            "fixture/1",
            "2026-07-18T17:00:00Z",
            [
                new BusinessOntologyConcept(damageConcept, "businessObject", "记录损坏信息", "记录维修中的损坏信息。", "accepted", 0.95, ["base:damage"]),
            ],
            [],
            [],
            [],
            [],
            [],
            [],
            [
                new BusinessOntologyMapping(OntologyId + ".Mapping.RecordDamageInfo", "concept", damageConcept, "representedBy", "repo:record", "java", "class", "symbol:damage-info", "src/RecordDamageInfosEntity.java", "treesitter", 0.95, "accepted", ["base:damage"]),
            ],
            [
                new BusinessOntologyEvidence("base:damage", "repo:record", "src/RecordDamageInfosEntity.java", "symbol:damage-info", 1, 80, "contractual", "treesitter", 0.95, "code", "记录损坏信息实体。"),
            ],
            [],
            [],
            []));

        var result = await new BusinessOntologySemanticProjector(om, store)
            .ProjectAsync(new BusinessOntologySemanticProjectionRequest(
                OntologyId,
                "semantic-mixed-scalar-lifecycle",
                "semantic-mixed-scalar-lifecycle-fingerprint",
                "2026-07-18T17:05:00Z"));
        var snapshot = await store.ReadExportableAsync(OntologyId);
        var lifecycle = snapshot.Candidates.Single(item => item.SubjectKind == OntologySemanticCandidateKinds.Lifecycle);
        using var document = JsonDocument.Parse(lifecycle.PayloadJson);
        var semantic = document.RootElement.GetProperty("semantic");
        var transitions = semantic.GetProperty("transitions")
            .EnumerateArray()
            .Select(item => (
                From: item.GetProperty("fromState").GetString() ?? "",
                To: item.GetProperty("toState").GetString() ?? "",
                Encoding: item.GetProperty("effect").GetProperty("set").GetProperty("valueEncoding").GetString() ?? "",
                RawValue: item.GetProperty("effect").GetProperty("set").GetProperty("rawValue").GetString() ?? ""))
            .OrderBy(item => item.To, StringComparer.Ordinal)
            .ToArray();

        assert(result.LifecycleCandidates == 1
                && semantic.GetProperty("subjectConceptId").GetString() == damageConcept
                && semantic.GetProperty("stateProperty").GetString() == "state"
                && transitions.Length == 2
                && transitions.All(item => item.From == "CODE_1")
                && transitions.Select(item => item.To).SequenceEqual(new[] { "CODE_2", "CODE_5" }, StringComparer.Ordinal)
                && transitions.All(item => item.Encoding == "numeric")
                && transitions.Select(item => item.RawValue).SequenceEqual(new[] { "2", "5" }, StringComparer.Ordinal),
            "mixed scalar encodings on one state property should keep guard-proved numeric transitions with each mutation's own encoding and raw value");
        assert(!lifecycle.PayloadJson.Contains("CODE_9", StringComparison.Ordinal)
                && !lifecycle.EvidenceIds.Contains(enumSetterWithoutFrom.ClaimId, StringComparer.Ordinal),
            "a same-property enum.getCode assignment without a proved from-state must not fabricate a transition or evidence");

        CodeSemanticClaimFact RepairMutation(string toValue, string rawValue, int line) => Claim(
            "file:service",
            repairMethod,
            CodeSemanticClaimKinds.StateAssignment,
            JsonSerializer.Serialize(new
            {
                enumType = "",
                enumTypeSymbolId = "",
                field = "state",
                fromValue = "CODE_1",
                method = "repairSave",
                methodSymbolId = repairMethod,
                mutationKind = "setter",
                ownerSymbolId = "symbol:damage-info",
                property = "state",
                rawValue,
                receiver = "damageInfosModel",
                toValue,
                valueEncoding = "numeric",
            }),
            line);
    }

    private static IReadOnlyList<CodeSemanticClaimFact> ConflictClaims()
    {
        var claims = new List<CodeSemanticClaimFact>();
        Add("file:record", "symbol:record:supplier", CodeSemanticClaimKinds.TypedReference,
            new { collection = false, member = "location", ownerSymbolId = "symbol:record", rawType = "SupplierEntity", usageKind = "field", resolvedTypeName = "SupplierEntity", resolvedTypeSymbolId = "symbol:supplier" }, 5);
        Add("file:record", "symbol:record:warehouse", CodeSemanticClaimKinds.TypedReference,
            new { collection = true, member = "location", ownerSymbolId = "symbol:record", rawType = "List<WarehouseEntity>", usageKind = "field", resolvedTypeName = "WarehouseEntity", resolvedTypeSymbolId = "symbol:warehouse" }, 6);
        Add("file:record", "symbol:record:code:min-two", CodeSemanticClaimKinds.ValidationConstraint,
            new { annotation = "Size", member = "code", @operator = "min", ownerSymbolId = "symbol:record", value = 2 }, 10);
        Add("file:record", "symbol:record:code:min-three", CodeSemanticClaimKinds.ValidationConstraint,
            new { annotation = "Size", member = "code", @operator = "min", ownerSymbolId = "symbol:record", value = 3 }, 11);
        Add("file:record", "symbol:record:status", CodeSemanticClaimKinds.StateField,
            new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", member = "status", ownerSymbolId = "symbol:record" }, 20);
        Add("file:record", "symbol:record-status:draft", CodeSemanticClaimKinds.StateValue,
            new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", value = "DRAFT" }, 21);
        Add("file:record", "symbol:record-status:approved", CodeSemanticClaimKinds.StateValue,
            new { enumType = "RecordStatus", enumTypeSymbolId = "symbol:record-status", value = "APPROVED" }, 22);
        Add("file:record", "symbol:record:phase", CodeSemanticClaimKinds.StateField,
            new { enumType = "RecordPhase", enumTypeSymbolId = "symbol:record-phase", member = "phase", ownerSymbolId = "symbol:record" }, 30);
        Add("file:record", "symbol:record-phase:new", CodeSemanticClaimKinds.StateValue,
            new { enumType = "RecordPhase", enumTypeSymbolId = "symbol:record-phase", value = "NEW" }, 31);
        Add("file:record", "symbol:record-phase:retired", CodeSemanticClaimKinds.StateValue,
            new { enumType = "RecordPhase", enumTypeSymbolId = "symbol:record-phase", value = "RETIRED" }, 32);
        Add("file:controller", "symbol:controller:records", CodeSemanticClaimKinds.RouteBinding,
            new { httpMethods = new[] { "GET" }, paths = new[] { "/records" }, site = "RecordController.list", symbolId = "symbol:controller:records" }, 8);
        return claims;

        void Add(string fileId, string subjectId, string kind, object payload, int line)
        {
            var json = JsonSerializer.Serialize(payload);
            claims.Add(new CodeSemanticClaimFact(
                CodeSemanticClaimIdentity.Create(fileId, kind, json, line, line),
                subjectId,
                kind,
                json,
                fileId,
                line,
                line,
                0.96,
                "treesitter",
                $"direct {kind}"));
        }
    }

    private static async Task SeedGuardRuleGraphAsync(
        CozoOm om,
        IReadOnlyList<CodeSemanticClaimFact> claims)
    {
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:controller", "repo:record", "src/RecordController.java", "java"),
                new CodeFileFact("file:service", "repo:record", "src/RecordService.java", "java"),
                new CodeFileFact("file:repository", "repo:record", "src/RecordRepository.java", "java"),
                new CodeFileFact("file:record", "repo:record", "src/RecordEntity.java", "java"),
                new CodeFileFact("file:supplier", "repo:record", "src/SupplierEntity.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:controller", "file:controller", "RecordController", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:route:create", "file:controller", "createRecord", "method", 12, 24, ParentId: "symbol:controller", Lang: "java"),
                new CodeSymbolFact("symbol:service", "file:service", "RecordService", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:service:create", "file:service", "createRecord", "method", 18, 44, ParentId: "symbol:service", Lang: "java"),
                new CodeSymbolFact("symbol:repo", "file:repository", "RecordRepository", "interface", 1, 40, Lang: "java"),
                new CodeSymbolFact("symbol:repo:save", "file:repository", "save", "method", 8, 8, ParentId: "symbol:repo", Lang: "java"),
                new CodeSymbolFact("symbol:record", "file:record", "RecordEntity", "class", 1, 80, Lang: "java"),
                new CodeSymbolFact("symbol:supplier", "file:supplier", "SupplierEntity", "class", 1, 20, Lang: "java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 1, 0.98, "spring_annotation", "controller"),
                new CodeEdgeFact("symbol:service", "spring:role:service", "SPRING_ROLE", "file:service", 1, 0.98, "spring_annotation", "service"),
                new CodeEdgeFact("symbol:repo", "spring:role:repository", "SPRING_ROLE", "file:repository", 1, 0.98, "spring_annotation", "repository"),
                new CodeEdgeFact("symbol:record", "spring:role:entity", "SPRING_ROLE", "file:record", 1, 0.98, "spring_annotation", "entity"),
                new CodeEdgeFact("symbol:route:create", "symbol:service:create", CodeEdgeKinds.Calls, "file:controller", 16, 1.0, "treesitter", "controller calls service"),
                new CodeEdgeFact("symbol:service:create", "symbol:repo:save", CodeEdgeKinds.Calls, "file:service", 24, 1.0, "treesitter", "service calls repository"),
                new CodeEdgeFact("symbol:repo:save", "symbol:record", CodeEdgeKinds.Calls, "file:repository", 8, 1.0, "treesitter", "repository writes entity"),
            ],
            EntryPoints: [new CodeEntryPointFact("symbol:route:create", "http_route", "POST /api/records")],
            SemanticClaims: claims));
    }

    private static CodeSemanticClaimFact Claim(
        string fileId,
        string subjectId,
        string kind,
        string payload,
        int line) =>
        new(
            CodeSemanticClaimIdentity.Create(fileId, kind, payload, line, line),
            subjectId,
            kind,
            payload,
            fileId,
            line,
            line,
            0.96,
            "treesitter",
            $"direct {kind}");

    private static BusinessOntologyGenerationInput BaseGeneration() => new(
        OntologyId,
        "base-1",
        "base-fingerprint",
        "fixture/1",
        "2026-07-18T09:00:00Z",
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
            new BusinessOntologyMapping(OntologyId + ".Mapping.Record", "concept", Record, "representedBy", "repo:record", "java", "class", "symbol:record", "src/RecordEntity.java", "treesitter", 0.95, "accepted", ["base:record"]),
            new BusinessOntologyMapping(OntologyId + ".Mapping.Supplier", "concept", Supplier, "representedBy", "repo:record", "java", "class", "symbol:supplier", "src/SupplierEntity.java", "treesitter", 0.95, "accepted", ["base:supplier"]),
        ],
        [
            new BusinessOntologyEvidence("base:record", "repo:record", "src/RecordEntity.java", "symbol:record", 1, 20, "contractual", "treesitter", 0.95, "code", "记录类型。"),
            new BusinessOntologyEvidence("base:supplier", "repo:record", "src/SupplierEntity.java", "symbol:supplier", 1, 20, "contractual", "treesitter", 0.95, "code", "供应商类型。"),
        ],
        [],
        [],
        []);

    private sealed class FakeLlmClient(params string[] responses) : ILlmClient
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public string UserPrompt { get; private set; } = "";
        private int callCount;

        public Task<LlmCompletion> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            LlmOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            UserPrompt = userPrompt;
            var index = Math.Min(callCount, responses.Length - 1);
            callCount++;
            return Task.FromResult(new LlmCompletion(responses[index], "fake"));
        }
    }
}
