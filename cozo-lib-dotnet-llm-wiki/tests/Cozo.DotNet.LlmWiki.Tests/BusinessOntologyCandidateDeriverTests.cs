using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyCandidateDeriverTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        await SingleCarrierYieldsOnlyImplementationEvidenceAsync(assert);
        await SameDtoCarrierFamilyYieldsOnlyImplementationEvidenceAsync(assert);
        await PureFlowCarriersDoNotYieldCanonicalConceptAsync(assert);
        await ConvergedCarrierFamilyYieldsCanonicalConceptAsync(assert);
        await CanonicalAttributesComeOnlyFromDomainDeclarationCarriersAsync(assert);
        await EntityAndBehaviorSupportYieldCanonicalConceptWithConservativeAliasesAsync(assert);
    }

    private static async Task EntityAndBehaviorSupportYieldCanonicalConceptWithConservativeAliasesAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:record-infos", "repo:record", "src/main/java/example/RecordInfosEntity.java", "java"),
                new CodeFileFact("file:record-info-dto", "repo:record", "src/main/java/example/RecordInfoDTO.java", "java"),
                new CodeFileFact("file:record-info-vo", "repo:record", "src/main/java/example/RecordInfoVO.java", "java"),
                new CodeFileFact("file:borrow-entity", "repo:record", "src/main/java/example/RecordBorrowEntity.java", "java"),
                new CodeFileFact("file:borrow-dto", "repo:record", "src/main/java/example/RecordBorrowDTO.java", "java"),
                new CodeFileFact("file:borrow-service", "repo:record", "src/main/java/example/RecordBorrowServiceImpl.java", "java"),
                new CodeFileFact("file:service-only", "repo:record", "src/main/java/example/RecordRepairServiceImpl.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:record-infos", "file:record-infos", "RecordInfosEntity", "class", 3, 80, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record-infos:status", "file:record-infos", "recordStatus", "field", 18, 18, ParentId: "symbol:record-infos", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record-info-dto", "file:record-info-dto", "RecordInfoDTO", "class", 3, 40, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record-info-vo", "file:record-info-vo", "RecordInfoVO", "class", 3, 40, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:borrow-entity", "file:borrow-entity", "RecordBorrowEntity", "class", 3, 80, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:borrow-entity:code", "file:borrow-entity", "borrowCode", "field", 12, 12, ParentId: "symbol:borrow-entity", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:borrow-dto", "file:borrow-dto", "RecordBorrowDTO", "class", 3, 60, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:borrow-service", "file:borrow-service", "RecordBorrowServiceImpl", "class", 3, 120, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:borrow-service:method", "file:borrow-service", "save", "method", 12, 20, ParentId: "symbol:borrow-service", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:repair-service", "file:service-only", "RecordRepairServiceImpl", "class", 3, 120, Lang: "java", Resolver: "tree-sitter-java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:record-infos", "spring:role:entity", "SPRING_ROLE", "file:record-infos", 3, 0.96, "spring-semantic", "@Entity"),
                new CodeEdgeFact("symbol:record-info-dto", "spring:role:dto", "SPRING_ROLE", "file:record-info-dto", 3, 0.94, "spring-semantic", "@Data DTO"),
                new CodeEdgeFact("symbol:record-info-vo", "spring:role:vo", "SPRING_ROLE", "file:record-info-vo", 3, 0.94, "spring-semantic", "@Data VO"),
                new CodeEdgeFact("symbol:borrow-entity", "spring:role:entity", "SPRING_ROLE", "file:borrow-entity", 3, 0.96, "spring-semantic", "@Entity"),
                new CodeEdgeFact("symbol:borrow-dto", "spring:role:dto", "SPRING_ROLE", "file:borrow-dto", 3, 0.94, "spring-semantic", "@Data DTO"),
                new CodeEdgeFact("symbol:borrow-service", "spring:role:service", "SPRING_ROLE", "file:borrow-service", 3, 0.96, "spring-semantic", "@Service"),
                new CodeEdgeFact("symbol:borrow-service:method", "spring:role:service", "SPRING_ROLE", "file:borrow-service", 12, 0.96, "spring-semantic", "method-level role should not become carrier"),
                new CodeEdgeFact("symbol:repair-service", "spring:role:service", "SPRING_ROLE", "file:service-only", 3, 0.96, "spring-semantic", "@Service"),
            ]));

        var store = new BusinessOntologyStore(om);
        await new BusinessOntologyCandidateDeriver(om, store)
            .DeriveAsync(new BusinessOntologyDerivationRequest("SampleDomain.Ontology", "fixture-behavior-support", "fixture-fingerprint", "2026-07-18T10:00:00Z"));
        var snapshot = await store.ReadExportableAsync("SampleDomain.Ontology");

        assert(snapshot.Concepts.Any(item => item.Id == "SampleDomain.Ontology.RecordInfo"), "RecordInfosEntity and RecordInfoDTO/VO should merge through the conservative Info/Infos alias");
        assert(snapshot.Mappings.Any(item => item.SubjectId == "SampleDomain.Ontology.RecordInfo" && item.Symbol == "symbol:record-infos"), "the original RecordInfosEntity mapping must be retained under the merged canonical concept");
        assert(snapshot.Concepts.Any(item => item.Id == "SampleDomain.Ontology.RecordBorrow"), "RecordBorrowEntity plus RecordBorrowServiceImpl plus RecordBorrowDTO should form one consistently named RecordBorrow concept");
        assert(snapshot.Mappings.Count(item => item.SubjectId == "SampleDomain.Ontology.RecordBorrow") == 3, "RecordBorrow entity, DTO, and service implementation should map to the same canonical concept without splitting stems");
        assert(!snapshot.Concepts.Any(item => item.Id == "SampleDomain.Ontology.Repair"), "a service implementation without data/domain carrier must not create a concept");
        assert(!snapshot.Candidates.Any(item => item.Id.Contains("symbol:borrow-service:method", StringComparison.Ordinal)), "implementation role edges on methods must not become carriers");
    }

    private static async Task SingleCarrierYieldsOnlyImplementationEvidenceAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();
        await om.Runtime.Store.RunAsync(":create depa_sentinel {id => value}");
        await om.Runtime.Store.RunAsync("?[id, value] <- [[\"depa\", \"unchanged\"]] :put depa_sentinel {id => value}");
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:record", "repo:record", "src/main/java/example/RecordDto.java", "java"),
                new CodeFileFact("file:controller", "repo:record", "src/main/java/example/RecordController.java", "java"),
                new CodeFileFact("file:web", "repo:record", "src/pages/RecordPage.tsx", "tsx"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:record", "file:record", "RecordDto", "class", 3, 18, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record:code", "file:record", "recordCode", "field", 5, 5, ParentId: "symbol:record", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record:name", "file:record", "recordName", "field", 6, 6, ParentId: "symbol:record", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:controller", "file:controller", "RecordController", "class", 3, 24, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:route", "file:controller", "createRecord", "method", 10, 16, ParentId: "symbol:controller", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:web", "file:web", "RecordPage", "function", 4, 28, Lang: "tsx", Resolver: "tree-sitter-tsx"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:record", "spring:role:dto", "SPRING_ROLE", "file:record", 3, 0.94, "spring-semantic", "@Data DTO"),
                new CodeEdgeFact("symbol:controller", "spring:role:controller", "SPRING_ROLE", "file:controller", 3, 0.96, "spring-semantic", "@RestController"),
            ],
            EntryPoints: [new CodeEntryPointFact("symbol:route", "http_route", "POST /records")]));

        var store = new BusinessOntologyStore(om);
        var deriver = new BusinessOntologyCandidateDeriver(om, store);
        var request = new BusinessOntologyDerivationRequest("SampleDomain.Ontology", "fixture-1", "fixture-fingerprint", "2026-07-17T10:00:00Z");
        var first = await deriver.DeriveAsync(request);
        var snapshot = await store.ReadExportableAsync("SampleDomain.Ontology");
        assert(first.Concepts == 0 && snapshot.Concepts.Count == 0, "a single DTO/entity/VO/request/response carrier must not yield a business concept");
        assert(snapshot.Attributes.Count == 0, "fields on a single implementation carrier must not yield business attributes");
        assert(!snapshot.Mappings.Any(item => item.SubjectKind == "concept"), "a single implementation carrier must not create mapping-as-concept rows");
        assert(snapshot.Candidates.Any(item =>
                item.Id == "candidate:implementation:symbol:record:spring:role:dto"
                && item.SubjectKind == "implementation"),
            "a single carrier should remain an implementation candidate with evidence");
        assert(snapshot.Candidates.Any(item => item.Id == "candidate:implementation:symbol:controller:spring:role:controller") && snapshot.Candidates.Any(item => item.Id == "candidate:route:symbol:route"), "controller and route observations should remain implementation candidates");
        assert(snapshot.Candidates.Any(item => item.Id == "candidate:frontend:symbol:web"), "a frontend page should become presentational corroboration only");
        assert(snapshot.Evidence.Any(item => item.Grade == "presentational") && snapshot.Evidence.Where(item => item.Grade == "inferred").All(item => item.Path.StartsWith("src/", StringComparison.Ordinal)), "all evidence should retain repository-relative source anchors and grades");
        var depa = await om.Runtime.Store.RunAsync("?[value] := *depa_sentinel{id: \"depa\", value}");
        assert(depa.Rows.Single()[0].GetString() == "unchanged", "candidate derivation must not alter DEPA rows");

        var second = await deriver.DeriveAsync(request);
        var repeated = await store.ReadExportableAsync("SampleDomain.Ontology");
        assert(second.Candidates == first.Candidates && repeated.Candidates.Select(item => item.Id).SequenceEqual(snapshot.Candidates.Select(item => item.Id)), "identical CodeKnowledge inputs should create stable candidate identities");
    }

    private static async Task SameDtoCarrierFamilyYieldsOnlyImplementationEvidenceAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:explicit-dto", "repo:record", "src/main/java/example/RecordDto.java", "java"),
                new CodeFileFact("file:inferred-dto", "repo:record", "src/main/java/example/RecordDTO.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:record-explicit", "file:explicit-dto", "RecordDto", "class", 3, 18, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record-explicit:code", "file:explicit-dto", "recordCode", "field", 5, 5, ParentId: "symbol:record-explicit", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record-inferred", "file:inferred-dto", "RecordDTO", "class", 3, 18, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record-inferred:code", "file:inferred-dto", "recordCode", "field", 5, 5, ParentId: "symbol:record-inferred", Lang: "java", Resolver: "tree-sitter-java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:record-explicit", "spring:role:dto", "SPRING_ROLE", "file:explicit-dto", 3, 0.94, "spring-semantic", "@Data DTO"),
            ]));

        var store = new BusinessOntologyStore(om);
        var deriver = new BusinessOntologyCandidateDeriver(om, store);
        await deriver.DeriveAsync(new BusinessOntologyDerivationRequest("SampleDomain.Ontology", "fixture-dto-family", "fixture-fingerprint", "2026-07-17T10:00:00Z"));
        var snapshot = await store.ReadExportableAsync("SampleDomain.Ontology");

        assert(snapshot.Concepts.Count == 0, "two DTO carriers in the same role family must not yield a business concept");
        assert(snapshot.Attributes.Count == 0, "same-role DTO carrier fields must not yield business attributes");
        assert(!snapshot.Mappings.Any(item => item.SubjectKind == "concept"), "same-role DTO carriers must not create concept mappings");
        assert(snapshot.Candidates.Count(item => item.SubjectKind == "implementation") == 2
                && snapshot.Candidates.Any(item => item.Id == "candidate:implementation:symbol:record-explicit:spring:role:dto")
                && snapshot.Candidates.Any(item => item.Id == "candidate:implementation:symbol:record-inferred:carrier:dto"),
            "explicit and suffix-inferred DTO carriers should remain separate implementation candidates with evidence");
    }

    private static async Task PureFlowCarriersDoNotYieldCanonicalConceptAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:query", "repo:record", "src/main/java/example/ApproveUserQuery.java", "java"),
                new CodeFileFact("file:vo", "repo:record", "src/main/java/example/ApproveUserVO.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:query", "file:query", "ApproveUserQuery", "class", 3, 30, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:query:user", "file:query", "userId", "field", 8, 8, ParentId: "symbol:query", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:vo", "file:vo", "ApproveUserVO", "class", 3, 30, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:vo:name", "file:vo", "userName", "field", 8, 8, ParentId: "symbol:vo", Lang: "java", Resolver: "tree-sitter-java"),
            ]));

        var store = new BusinessOntologyStore(om);
        await new BusinessOntologyCandidateDeriver(om, store)
            .DeriveAsync(new BusinessOntologyDerivationRequest("SampleDomain.Ontology", "fixture-flow", "fixture-fingerprint", "2026-07-18T10:00:00Z"));
        var snapshot = await store.ReadExportableAsync("SampleDomain.Ontology");

        assert(snapshot.Concepts.Count == 0, "pure Query/VO flow carriers must not become a canonical business concept without an entity/domain anchor");
        assert(snapshot.Attributes.Count == 0, "pure flow carrier fields must not become business attributes");
        assert(snapshot.Candidates.Count(item => item.SubjectKind == "implementation") == 2, "pure flow carriers should remain implementation candidates for audit");
    }

    private static async Task ConvergedCarrierFamilyYieldsCanonicalConceptAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:dto", "repo:record", "src/main/java/example/RecordDto.java", "java"),
                new CodeFileFact("file:entity", "repo:record", "src/main/java/example/RecordEntity.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:record:dto", "file:dto", "RecordDto", "class", 3, 18, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record:dto:code", "file:dto", "recordCode", "field", 5, 5, ParentId: "symbol:record:dto", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record:entity", "file:entity", "RecordEntity", "class", 4, 24, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record:entity:code", "file:entity", "recordCode", "field", 8, 8, ParentId: "symbol:record:entity", Lang: "java", Resolver: "tree-sitter-java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:record:dto", "spring:role:dto", "SPRING_ROLE", "file:dto", 3, 0.94, "spring-semantic", "@Data DTO"),
                new CodeEdgeFact("symbol:record:entity", "spring:role:entity", "SPRING_ROLE", "file:entity", 4, 0.96, "spring-semantic", "@Entity"),
            ]));

        var store = new BusinessOntologyStore(om);
        var deriver = new BusinessOntologyCandidateDeriver(om, store);
        await deriver.DeriveAsync(new BusinessOntologyDerivationRequest("SampleDomain.Ontology", "fixture-2", "fixture-fingerprint", "2026-07-17T10:00:00Z"));
        var snapshot = await store.ReadExportableAsync("SampleDomain.Ontology");

        assert(snapshot.Concepts.Count == 1 && snapshot.Concepts.Single().Id == "SampleDomain.Ontology.Record", "two independent carrier roles in the same canonical family should yield one business concept proposal");
        assert(snapshot.Mappings.Count(item =>
                item.SubjectKind == "concept"
                && item.SubjectId == "SampleDomain.Ontology.Record"
                && item.MappingRole == "representedBy") == 2,
            "the canonical concept should retain mappings to both implementation carriers");
        assert(snapshot.Attributes.Select(item => item.Name).SequenceEqual(["recordCode"]), "canonical concept attributes should be merged across carrier fields");
        assert(snapshot.Candidates.Any(item => item.Id == "candidate:concept:SampleDomain.Ontology.Record"), "the canonical concept proposal should have a stable family-level candidate id");
    }

    private static async Task CanonicalAttributesComeOnlyFromDomainDeclarationCarriersAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:record", "/fixture", "record")],
            Files:
            [
                new CodeFileFact("file:entity", "repo:record", "src/main/java/example/RecordEntity.java", "java"),
                new CodeFileFact("file:query", "repo:record", "src/main/java/example/RecordQuery.java", "java"),
            ],
            Symbols:
            [
                new CodeSymbolFact("symbol:record:entity", "file:entity", "RecordEntity", "class", 4, 90, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record:code", "file:entity", "recordCode", "field", 8, 8, ParentId: "symbol:record:entity", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record:serial", "file:entity", "serialVersionUID", "field", 9, 9, ParentId: "symbol:record:entity", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record:cache", "file:entity", "cacheLock", "field", 10, 10, ParentId: "symbol:record:entity", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record:query", "file:query", "RecordQuery", "class", 4, 60, Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record:sync-list", "file:query", "syncDataEntityList", "field", 11, 11, ParentId: "symbol:record:query", Lang: "java", Resolver: "tree-sitter-java"),
                new CodeSymbolFact("symbol:record:keyword", "file:query", "keyword", "field", 12, 12, ParentId: "symbol:record:query", Lang: "java", Resolver: "tree-sitter-java"),
            ],
            Edges:
            [
                new CodeEdgeFact("symbol:record:entity", "spring:role:entity", "SPRING_ROLE", "file:entity", 4, 0.96, "spring-semantic", "@Entity"),
            ]));

        var store = new BusinessOntologyStore(om);
        await new BusinessOntologyCandidateDeriver(om, store)
            .DeriveAsync(new BusinessOntologyDerivationRequest("SampleDomain.Ontology", "fixture-attribute-boundary", "fixture-fingerprint", "2026-07-18T10:00:00Z"));
        var snapshot = await store.ReadExportableAsync("SampleDomain.Ontology");

        assert(snapshot.Concepts.Count == 1 && snapshot.Concepts.Single().Id == "SampleDomain.Ontology.Record", "entity plus query may identify the Record concept because the entity is the domain anchor");
        assert(snapshot.Attributes.Select(item => item.Name).SequenceEqual(["recordCode"]), "canonical attributes must come only from domain declaration carriers and exclude serial/cache/query-list fields");
    }
}
