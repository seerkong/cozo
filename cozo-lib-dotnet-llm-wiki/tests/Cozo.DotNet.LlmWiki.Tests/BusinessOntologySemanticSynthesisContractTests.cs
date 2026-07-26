using Cozo.DotNet.LlmWiki.Tools;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologySemanticSynthesisContractTests
{
    public static void Run(Action<bool, string> assert)
    {
        var gate = new QualityGateTestHarness();
        var validBundle = ValidBundle();
        var valid = gate.Evaluate(validBundle, ValidBaseline());
        assert(valid.Status == BusinessOntologySemanticQualityGate.Passed
            && valid.DomainCount == 1 && valid.ConceptCount == 2
            && valid.RelationCount == 1 && valid.RuleCount == 1 && valid.LifecycleCount == 1
            && valid.SingleAnchorConceptRatio == 0 && valid.ProjectionLikeConceptRatio == 0
            && valid.Diagnostics.Count == 0,
            "v3 semantic gate should accept a domain-led bundle with verified external evidence, cross-role aggregation, claim bindings, and a non-equivalent baseline");

        var trustedContext = EvaluationContextFor(validBundle);
        var baselineProviderWithOldCandidateBaseline = new ContextSelectingBaselineProvider([
            ValidBaseline(),
            Baseline("old-source", "old-input", [
                new BusinessOntologySemanticProjectionCarrier("ItAsset.Asset", ["java:AssetEntity", "java:AssetDto"]),
                new BusinessOntologySemanticProjectionCarrier("ItAsset.Custodian", ["java:CustodianEntity"]),
            ]),
        ]);
        var selfReportedOldRun = validBundle with
        {
            SourceFingerprint = "old-source",
            InputFingerprint = "old-input",
            SynthesisRunId = "old-synthesis-run",
            CriticRun = validBundle.CriticRun with
            {
                RunId = "old-critic-run",
                SourceFingerprint = "old-source",
                InputFingerprint = "old-input",
                SnapshotDigest = "old-critic-snapshot",
            },
        };
        var selfReportedOldRunReport = gate.Evaluate(
            selfReportedOldRun, trustedContext, baselineProviderWithOldCandidateBaseline, new ApprovingCriticRunVerifier());
        assert(selfReportedOldRunReport.Status == BusinessOntologySemanticQualityGate.Failed
            && baselineProviderWithOldCandidateBaseline.RequestedContext == trustedContext
            && selfReportedOldRunReport.Diagnostics.Any(item => item.Code == "bundle_evaluation_context_mismatch")
            && selfReportedOldRunReport.Diagnostics.Any(item => item.Code == "critic_provenance_context_mismatch"),
            "a bundle that self-reports an old source, input, run, or critic snapshot must fail even when the trusted provider also has a baseline for that old candidate context");

        var verifierRejectedCandidate = gate.Evaluate(validBundle with
        {
            CriticRun = validBundle.CriticRun with { RunId = "forged-critic-run" },
        }, trustedContext, new DeterministicBaselineProvider(ValidBaseline()), new RejectingCriticRunVerifier());
        assert(verifierRejectedCandidate.Status == BusinessOntologySemanticQualityGate.Failed
            && verifierRejectedCandidate.Diagnostics.Any(item => item.Code == "critic_provenance_unverified"),
            "a candidate cannot prove critic independence merely by declaring a different run ID; a trusted verifier must approve it");

        var projectionBundle = ProjectionBundle();
        var projection = gate.Evaluate(projectionBundle, ProjectionBaseline());
        assert(projection.Status == BusinessOntologySemanticQualityGate.Failed
            && projection.ProjectionLikeConceptRatio == 1
            && projection.Diagnostics.Any(item => item.Code == "candidate_set_matches_projection_baseline")
            && projection.Diagnostics.Any(item => item.Code == "business_semantics_absent")
            && projection.Diagnostics.Any(item => item.Code == "projection_likeness_high"),
            "v3 semantic gate must reject the complete candidate set when it is a one-to-one verified baseline projection");

        var handlerIdentity = gate.Evaluate(ValidBundle() with
        {
            Clusters = ValidBundle().Clusters.Select(cluster => cluster.ConceptId == "ItAsset.Asset"
                ? cluster with { ConceptId = "ItAsset.AssetHandler" }
                : cluster).ToArray(),
        }, ValidBaseline());
        var managerIdentity = gate.Evaluate(ValidBundle() with
        {
            Clusters = ValidBundle().Clusters.Select(cluster => cluster.ConceptId == "ItAsset.Asset"
                ? cluster with { ConceptId = "ItAsset.AssetManager" }
                : cluster).ToArray(),
        }, ValidBaseline());
        var businessTermIdentity = gate.Evaluate(ValidBundle() with
        {
            Clusters = ValidBundle().Clusters.Select(cluster => cluster.ConceptId == "ItAsset.Asset"
                ? cluster with { ConceptId = "ItAsset.AssetManagement" }
                : cluster).ToArray(),
        }, ValidBaseline());
        assert(handlerIdentity.Diagnostics.Any(item => item.Code == "concept_implementation_identity" && item.SubjectId == "ItAsset.AssetHandler")
                && managerIdentity.Diagnostics.Any(item => item.Code == "concept_implementation_identity" && item.SubjectId == "ItAsset.AssetManager")
                && !businessTermIdentity.Diagnostics.Any(item => item.Code == "concept_implementation_identity" && item.SubjectId == "ItAsset.AssetManagement"),
            "semantic quality gate must reject Handler and Manager implementation suffixes without rejecting longer business terms");

        var handlerDomainIdentity = gate.Evaluate(ValidBundle() with
        {
            DomainCharters = [ValidBundle().DomainCharters[0] with { Id = "ItAsset.InventoryHandler" }],
            Clusters = ValidBundle().Clusters.Select(cluster => cluster with { DomainId = "ItAsset.InventoryHandler" }).ToArray(),
        }, ValidBaseline());
        var managerDomainIdentity = gate.Evaluate(ValidBundle() with
        {
            DomainCharters = [ValidBundle().DomainCharters[0] with { Id = "ItAsset.InventoryManager" }],
            Clusters = ValidBundle().Clusters.Select(cluster => cluster with { DomainId = "ItAsset.InventoryManager" }).ToArray(),
        }, ValidBaseline());
        var recordManagementDomain = gate.Evaluate(ValidBundle() with
        {
            DomainCharters = [ValidBundle().DomainCharters[0] with { Id = "ItAsset.RecordManagement" }],
            Clusters = ValidBundle().Clusters.Select(cluster => cluster with { DomainId = "ItAsset.RecordManagement" }).ToArray(),
        }, ValidBaseline());
        assert(handlerDomainIdentity.Diagnostics.Any(item => item.Code == "domain_charter_implementation_identity"
                    && item.SubjectId == "ItAsset.InventoryHandler")
                && handlerDomainIdentity.Diagnostics.Any(item => item.Code == "concept_domain_implementation_identity"
                    && item.SubjectId == "ItAsset.InventoryHandler")
                && managerDomainIdentity.Diagnostics.Any(item => item.Code == "domain_charter_implementation_identity"
                    && item.SubjectId == "ItAsset.InventoryManager")
                && managerDomainIdentity.Diagnostics.Any(item => item.Code == "concept_domain_implementation_identity"
                    && item.SubjectId == "ItAsset.InventoryManager")
                && !recordManagementDomain.Diagnostics.Any(item => item.Code is "domain_charter_implementation_identity" or "concept_domain_implementation_identity"),
            "semantic quality gate must reject Handler and Manager final domain segments while accepting business terms such as RecordManagement");

        var fqnShapedDomain = gate.Evaluate(ValidBundle() with
        {
            DomainCharters = [ValidBundle().DomainCharters[0] with { Id = "System.Xml.Linq" }],
            Clusters = ValidBundle().Clusters.Select(cluster => cluster with { DomainId = "System.Xml.Linq" }).ToArray(),
        }, ValidBaseline());
        assert(fqnShapedDomain.Status == BusinessOntologySemanticQualityGate.Failed
            && fqnShapedDomain.Diagnostics.Any(item => item.Code == "domain_charter_id_invalid" && item.SubjectId == "System.Xml.Linq")
            && fqnShapedDomain.Diagnostics.Any(item => item.Code == "concept_domain_id_invalid" && item.SubjectId == "System.Xml.Linq")
            && !fqnShapedDomain.Diagnostics.Any(item => item.Code == "concept_domain_missing"),
            "semantic quality gate must reject matching charter and cluster domains shaped like three-segment FQNs");

        var paddedBundle = ProjectionBundle() with
        {
            Clusters = [ProjectionBundle().Clusters[0] with
            {
                ImplementationAnchors = ProjectionBundle().Clusters[0].ImplementationAnchors.Concat(
                    [new BusinessOntologySemanticImplementationAnchor("web:AssetPage", "page", "web/AssetPage.tsx", "e:page")]).ToArray(),
            }],
        };
        var paddedProjection = gate.Evaluate(paddedBundle, ProjectionBaseline());
        assert(paddedProjection.Status == BusinessOntologySemanticQualityGate.Failed
            && paddedProjection.ProjectionLikeConceptRatio == 1
            && paddedProjection.Diagnostics.Any(item => item.Code == "candidate_set_matches_projection_baseline"),
            "adding an auxiliary UI anchor must not make an otherwise one-to-one baseline projection pass v3 quality");

        var partialBundle = ProjectionBundle() with
        {
            InputFingerprint = "input-partial",
        };
        var partialProjection = gate.Evaluate(partialBundle, Baseline(
            "source-fixture", "input-partial",
            [
                new BusinessOntologySemanticProjectionCarrier("ItAsset.Asset", ["java:AssetEntity", "java:AssetDto"]),
                new BusinessOntologySemanticProjectionCarrier("ItAsset.Custodian", ["java:CustodianEntity"]),
            ]));
        assert(partialProjection.Status == BusinessOntologySemanticQualityGate.Failed
            && partialProjection.ProjectionLikeConceptRatio == 1
            && partialProjection.Diagnostics.Any(item => item.Code == "candidate_set_partially_matches_projection_baseline"),
            "a partial candidate one-to-one symbol projection must fail even though it is not equivalent to every baseline carrier");

        var projectionWithExtraConcept = ProjectionBundle() with
        {
            Clusters = ProjectionBundle().Clusters.Concat([
                new BusinessOntologySemanticCluster("cluster:inventory", "ItAsset.Inventory", "ItAsset.Inventory", "资产台账", "用于管理资产登记和盘点的业务台账。", [
                    new BusinessOntologySemanticImplementationAnchor("java:AssetController", "controller", "src/AssetController.java", "e:controller"),
                    new BusinessOntologySemanticImplementationAnchor("java:AssetService", "service", "src/AssetService.java", "e:service"),
                ]),
            ]).ToArray(),
            Relations = [new BusinessOntologySemanticRelation("ItAsset.Relation.InventoryAsset", "ItAsset.Inventory", "ItAsset.Asset", "manages", "资产台账管理资产记录。", [
                Binding("service-call", "服务调用证明资产台账管理资产记录。", "e:service"),
            ])],
            CriticVerdicts = ProjectionBundle().CriticVerdicts.Concat([
                new BusinessOntologySemanticCriticVerdict("ItAsset.Inventory", BusinessOntologySemanticCriticDecisions.Keep, "控制器和服务共同证明资产台账概念。", ["e:controller", "e:service"]),
                new BusinessOntologySemanticCriticVerdict("ItAsset.Relation.InventoryAsset", BusinessOntologySemanticCriticDecisions.Keep, "服务绑定证明台账与资产关系。", ["e:service"]),
            ]).ToArray(),
        };
        var paddedCandidateSet = gate.Evaluate(projectionWithExtraConcept, ProjectionBaseline());
        assert(paddedCandidateSet.Status == BusinessOntologySemanticQualityGate.Failed
            && paddedCandidateSet.ProjectionLikeConceptRatio == 0
            && paddedCandidateSet.Diagnostics.Any(item => item.Code == "projection_baseline_fully_covered"),
            "an extra domain-looking concept cannot hide a candidate set that still one-to-one covers every baseline carrier");

        var fragmentedProjection = gate.Evaluate(ValidBundle(), Baseline(
            "source-fixture", "input-fixture-v3",
            [new BusinessOntologySemanticProjectionCarrier("projection:asset-and-custodian", [
                "java:AssetController", "java:AssetService", "java:CustodianEntity", "web:CustodianSelector",
            ])]));
        assert(fragmentedProjection.Status == BusinessOntologySemanticQualityGate.Failed
            && fragmentedProjection.ProjectionLikeConceptRatio == 0
            && fragmentedProjection.Diagnostics.Any(item => item.Code == "projection_baseline_union_fully_covered"),
            "splitting one verified carrier across semantic clusters below the per-cluster threshold must not bypass aggregate projection coverage");

        var wrongBaseline = gate.Evaluate(validBundle, ValidBaseline() with { InputFingerprint = "other-input" });
        var incompleteBaseline = gate.Evaluate(validBundle, ValidBaseline() with { Digest = "", IntegrityDigest = "" });
        var digestMismatch = gate.Evaluate(validBundle, ValidBaseline() with { Digest = "not-the-carrier-digest" });
        var forgedIntegrity = gate.Evaluate(validBundle, ValidBaseline() with { IntegrityDigest = "forged-envelope" });
        var reducedCarriers = ValidBaseline().Carriers.Take(1).ToArray();
        var reducedCarrierDigest = BusinessOntologySemanticProjectionBaselineDigest.Compute(reducedCarriers);
        var reducedBaseline = gate.Evaluate(validBundle, ValidBaseline() with
        {
            Digest = reducedCarrierDigest,
            IntegrityDigest = BusinessOntologySemanticProjectionBaselineDigest.ComputeIntegrityDigest(
                "source-fixture", "input-fixture-v3", reducedCarrierDigest, ValidBaseline().ExpectedCarrierCount),
            Carriers = reducedCarriers,
        });
        var tamperedCarriers = gate.Evaluate(validBundle, ValidBaseline() with
        {
            Carriers = [
                new BusinessOntologySemanticProjectionCarrier("ItAsset.Asset", ["java:AssetEntity", "java:AssetDto"]),
                new BusinessOntologySemanticProjectionCarrier("ItAsset.Custodian", ["java:TamperedCustodianEntity"]),
            ],
        });
        var reorderedCarrierDigest = BusinessOntologySemanticProjectionBaselineDigest.Compute([
            new BusinessOntologySemanticProjectionCarrier("ItAsset.Custodian", ["java:CustodianEntity"]),
            new BusinessOntologySemanticProjectionCarrier("ItAsset.Asset", ["java:AssetDto", "java:AssetEntity"]),
        ]);
        assert(wrongBaseline.Status == BusinessOntologySemanticQualityGate.Failed
            && wrongBaseline.Diagnostics.Any(item => item.Code == "verified_baseline_fingerprint_mismatch")
            && incompleteBaseline.Status == BusinessOntologySemanticQualityGate.Failed
            && incompleteBaseline.Diagnostics.Any(item => item.Code == "verified_baseline_integrity_missing")
            && digestMismatch.Status == BusinessOntologySemanticQualityGate.Failed
            && digestMismatch.Diagnostics.Any(item => item.Code == "verified_baseline_digest_mismatch")
            && forgedIntegrity.Status == BusinessOntologySemanticQualityGate.Failed
            && forgedIntegrity.Diagnostics.Any(item => item.Code == "verified_baseline_integrity_digest_mismatch")
            && reducedBaseline.Status == BusinessOntologySemanticQualityGate.Failed
            && reducedBaseline.Diagnostics.Any(item => item.Code == "verified_baseline_invalid")
            && tamperedCarriers.Status == BusinessOntologySemanticQualityGate.Failed
            && tamperedCarriers.Diagnostics.Any(item => item.Code == "verified_baseline_digest_mismatch")
            && reorderedCarrierDigest == ValidBaseline().Digest,
            "the deterministic provider baseline must bind matching fingerprints, an exact carrier digest, and a recomputable envelope digest without accepting reduced carrier sets");

        var unknownEvidence = gate.Evaluate(ValidBundle() with
        {
            Relations = [new BusinessOntologySemanticRelation("ItAsset.Relation.Unknown", "ItAsset.Asset", "ItAsset.Custodian", "hasCustodian", "资产由保管人负责。", [Binding("service-call", "服务调用证明资产与保管人的责任关系。", "e:missing")])],
            Rules = [], Lifecycles = [],
        }, ValidBaseline());
        assert(unknownEvidence.Status == BusinessOntologySemanticQualityGate.Failed
            && unknownEvidence.Diagnostics.Any(item => item.Code == "relation_evidence_binding_invalid"),
            "v3 semantic gate must reject claim bindings whose evidence identifiers are absent from the closed evidence registry");

        var missingBindings = gate.Evaluate(ValidBundle() with
        {
            Relations = [ValidBundle().Relations[0] with { EvidenceBindings = [] }],
            Rules = [ValidBundle().Rules[0] with { EvidenceBindings = [] }],
            Lifecycles = [ValidBundle().Lifecycles[0] with { EvidenceBindings = [] }],
        }, ValidBaseline());
        assert(missingBindings.Status == BusinessOntologySemanticQualityGate.Failed
            && missingBindings.Diagnostics.Any(item => item.Code == "relation_evidence_binding_invalid")
            && missingBindings.Diagnostics.Any(item => item.Code == "rule_evidence_binding_invalid")
            && missingBindings.Diagnostics.Any(item => item.Code == "lifecycle_evidence_binding_invalid"),
            "relations, rules, and lifecycles must each carry explicit binding type, Chinese summary, and closed evidence");

        var forgedPageBindings = gate.Evaluate(ValidBundle() with
        {
            Relations = [ValidBundle().Relations[0] with { EvidenceBindings = [Binding("service-call", "页面不能证明服务关系。", "e:page")] }],
            Rules = [ValidBundle().Rules[0] with { EvidenceBindings = [Binding("validation-branch", "页面不能证明校验分支。", "e:page")] }],
            Lifecycles = [ValidBundle().Lifecycles[0] with { EvidenceBindings = [Binding("state-update", "页面不能证明状态更新。", "e:page")] }],
        }, ValidBaseline());
        var unknownBindingType = gate.Evaluate(ValidBundle() with
        {
            Relations = [ValidBundle().Relations[0] with { EvidenceBindings = [Binding("free-form", "未知类型不能伪造业务关系。", "e:service")] }],
        }, ValidBaseline());
        assert(forgedPageBindings.Status == BusinessOntologySemanticQualityGate.Failed
            && forgedPageBindings.Diagnostics.Any(item => item.Code == "relation_evidence_binding_invalid")
            && forgedPageBindings.Diagnostics.Any(item => item.Code == "rule_evidence_binding_invalid")
            && forgedPageBindings.Diagnostics.Any(item => item.Code == "lifecycle_evidence_binding_invalid")
            && unknownBindingType.Status == BusinessOntologySemanticQualityGate.Failed
            && unknownBindingType.Diagnostics.Any(item => item.Code == "relation_evidence_binding_invalid"),
            "claim bindings must use the closed type vocabulary and evidence source kind assigned to each relation, rule, or lifecycle claim");

        var unrelatedCompatibleBindings = gate.Evaluate(ValidBundle() with
        {
            Relations = [ValidBundle().Relations[0] with
            {
                EvidenceBindings = [
                    Binding("service-call", "无关服务的来源类别正确但不能证明资产关系。", "e:unrelated-service"),
                    Binding("typed-reference", "保管人锚点证明关系的一端。", "e:custodian-entity"),
                ],
            }],
            Rules = [ValidBundle().Rules[0] with
            {
                EvidenceBindings = [Binding("validation-branch", "无关服务的来源类别正确但不能证明资产规则。", "e:unrelated-service")],
            }],
            Lifecycles = [ValidBundle().Lifecycles[0] with
            {
                EvidenceBindings = [Binding("state-update", "无关状态服务的来源类别正确但不能证明资产生命周期。", "e:unrelated-state")],
            }],
        }, ValidBaseline());
        assert(unrelatedCompatibleBindings.Status == BusinessOntologySemanticQualityGate.Failed
            && unrelatedCompatibleBindings.Diagnostics.Any(item => item.Code == "relation_evidence_not_connected_to_endpoint")
            && unrelatedCompatibleBindings.Diagnostics.Any(item => item.Code == "rule_evidence_not_connected_to_subject")
            && unrelatedCompatibleBindings.Diagnostics.Any(item => item.Code == "lifecycle_evidence_not_connected_to_subject"),
            "closed binding types and source kinds are insufficient when relation endpoints or rule and lifecycle subjects are not connected to their anchor evidence");

        var invalidAnchor = gate.Evaluate(ValidBundle() with
        {
            Clusters = [ValidBundle().Clusters[0] with
            {
                ImplementationAnchors = [
                    new BusinessOntologySemanticImplementationAnchor("java:AssetController", "controller", "src/AssetController.java", "e:unknown"),
                    new BusinessOntologySemanticImplementationAnchor("java:AssetController", "service", "src/AssetService.java", "e:service"),
                    new BusinessOntologySemanticImplementationAnchor("web:AssetPage", "page", "web/AssetPage.tsx", "e:page"),
                ],
            }, ValidBundle().Clusters[1]],
        }, ValidBaseline());
        assert(invalidAnchor.Status == BusinessOntologySemanticQualityGate.Failed
            && invalidAnchor.Diagnostics.Any(item => item.Code == "concept_anchor_evidence_missing")
            && invalidAnchor.Diagnostics.Any(item => item.Code == "concept_anchor_duplicate"),
            "v3 semantic gate must reject unknown and duplicated anchors instead of letting them inflate aggregation evidence");

        var reusedPath = gate.Evaluate(ValidBundle() with
        {
            Clusters = [ValidBundle().Clusters[0] with
            {
                ImplementationAnchors = [
                    new BusinessOntologySemanticImplementationAnchor("java:AssetController", "controller", "src/AssetController.java", "e:controller"),
                    new BusinessOntologySemanticImplementationAnchor("java:AssetService", "service", "src/AssetController.java", "e:service"),
                    new BusinessOntologySemanticImplementationAnchor("web:AssetPage", "page", "web/AssetPage.tsx", "e:page"),
                ],
            }, ValidBundle().Clusters[1]],
        }, ValidBaseline());
        assert(reusedPath.Status == BusinessOntologySemanticQualityGate.Failed
            && reusedPath.Diagnostics.Any(item => item.Code == "concept_anchor_path_reused"),
            "multiple symbols sharing one relative path cannot be counted as cross-role aggregation evidence");

        var nonChinese = gate.Evaluate(ValidBundle() with
        {
            Clusters = [ValidBundle().Clusters[0] with { NameZh = "AssetRecord", DescriptionZh = "AssetRecord aggregate." }, ValidBundle().Clusters[1]],
        }, ValidBaseline());
        assert(nonChinese.Status == BusinessOntologySemanticQualityGate.Failed
            && nonChinese.Diagnostics.Any(item => item.Code == "concept_business_description_missing"),
            "v3 semantic gate must reject code-like non-Chinese business labels rather than accepting a populated string field");

        var invalidConceptIds = new[]
        {
            "Cozo.DotNet.Asset",
            "src/Asset.cs",
            "Asset",
            "ItAsset.AssetEntity",
        }.Select(conceptId => gate.Evaluate(ValidBundle() with
        {
            Clusters = [ValidBundle().Clusters[0] with { ConceptId = conceptId }, ValidBundle().Clusters[1]],
            CriticVerdicts = ReplaceVerdict(ValidBundle(), "ItAsset.Asset", new BusinessOntologySemanticCriticVerdict(
                conceptId, BusinessOntologySemanticCriticDecisions.Keep, "跨层锚点共同证明资产业务概念。", ["e:controller", "e:service"])),
        }, ValidBaseline())).ToArray();
        assert(invalidConceptIds.All(report => report.Status == BusinessOntologySemanticQualityGate.Failed)
            && invalidConceptIds.Take(3).All(report => report.Diagnostics.Any(item => item.Code == "concept_semantic_id_invalid"))
            && invalidConceptIds.Last().Diagnostics.Any(item => item.Code == "concept_implementation_identity"),
            "concept IDs must be two-segment PascalCase semantic names under their domain root, rejecting FQNs, paths, bare class names, and technical suffixes");

        var duplicateIds = gate.Evaluate(ValidBundle() with
        {
            DomainCharters = [ValidBundle().DomainCharters[0], ValidBundle().DomainCharters[0]],
            Clusters = [ValidBundle().Clusters[0], ValidBundle().Clusters[0]],
            Relations = [ValidBundle().Relations[0], ValidBundle().Relations[0]],
            Rules = [ValidBundle().Rules[0], ValidBundle().Rules[0]],
            Lifecycles = [ValidBundle().Lifecycles[0], ValidBundle().Lifecycles[0]],
        }, ValidBaseline());
        var crossKindId = gate.Evaluate(ValidBundle() with
        {
            Relations = [ValidBundle().Relations[0] with { Id = "ItAsset.Asset" }],
        }, ValidBaseline());
        assert(duplicateIds.Status == BusinessOntologySemanticQualityGate.Failed
            && duplicateIds.Diagnostics.Any(item => item.Code == "domain_charter_duplicate")
            && duplicateIds.Diagnostics.Any(item => item.Code == "cluster_id_duplicate")
            && duplicateIds.Diagnostics.Any(item => item.Code == "relation_id_duplicate")
            && duplicateIds.Diagnostics.Any(item => item.Code == "rule_id_duplicate")
            && duplicateIds.Diagnostics.Any(item => item.Code == "lifecycle_id_duplicate")
            && crossKindId.Diagnostics.Any(item => item.Code == "candidate_id_duplicate"),
            "duplicate charter, cluster, relation, rule, lifecycle, and cross-kind candidate identifiers must be rejected before set membership can hide them");

        var incompleteIdentifiers = gate.Evaluate(ValidBundle() with
        {
            OntologyId = "",
            SynthesisRunId = "",
            DomainCharters = [ValidBundle().DomainCharters[0] with { Id = "" }],
            Clusters = [ValidBundle().Clusters[0] with { Id = "", DomainId = "", ConceptId = "" }, ValidBundle().Clusters[1]],
            Relations = [ValidBundle().Relations[0] with { Id = "", Name = "", DescriptionZh = "" }],
            Rules = [ValidBundle().Rules[0] with { Id = "" }],
            Lifecycles = [ValidBundle().Lifecycles[0] with { Id = "" }],
        }, ValidBaseline());
        assert(incompleteIdentifiers.Status == BusinessOntologySemanticQualityGate.Failed
            && incompleteIdentifiers.Diagnostics.Any(item => item.Code == "bundle_id_required")
            && incompleteIdentifiers.Diagnostics.Any(item => item.Code == "domain_charter_id_required")
            && incompleteIdentifiers.Diagnostics.Any(item => item.Code == "cluster_identity_required")
            && incompleteIdentifiers.Diagnostics.Any(item => item.Code == "relation_id_required")
            && incompleteIdentifiers.Diagnostics.Any(item => item.Code == "rule_id_required")
            && incompleteIdentifiers.Diagnostics.Any(item => item.Code == "lifecycle_id_required")
            && incompleteIdentifiers.Diagnostics.Any(item => item.Code == "relation_name_or_description_missing")
            && incompleteIdentifiers.Diagnostics.Any(item => item.Code == "business_semantics_absent"),
            "required identifiers and relation name and Chinese description must be present before a semantic edge can count or avoid critic validation");

        var keepOutsideClosure = gate.Evaluate(ValidBundle() with
        {
            CriticVerdicts = ReplaceVerdict(ValidBundle(), "ItAsset.Asset", new BusinessOntologySemanticCriticVerdict(
                "ItAsset.Asset", BusinessOntologySemanticCriticDecisions.Keep, "错误引用保管人证据。", ["e:custodian-entity"])),
        }, ValidBaseline());
        var mergeUnknown = gate.Evaluate(ValidBundle() with
        {
            CriticVerdicts = ReplaceVerdict(ValidBundle(), "ItAsset.Asset", new BusinessOntologySemanticCriticVerdict(
                "ItAsset.Asset", BusinessOntologySemanticCriticDecisions.Merge, "应与另一个候选合并。", ["e:service"], "ItAsset.Unknown")),
        }, ValidBaseline());
        var requestWithoutChinese = gate.Evaluate(ValidBundle() with
        {
            CriticVerdicts = ReplaceVerdict(ValidBundle(), "ItAsset.Asset", new BusinessOntologySemanticCriticVerdict(
                "ItAsset.Asset", BusinessOntologySemanticCriticDecisions.RequestEvidence, "需要补充证据。", ["e:service"], RequestZh: "more evidence")),
        }, ValidBaseline());
        assert(keepOutsideClosure.Diagnostics.Any(item => item.Code == "critic_verdict_invalid")
            && mergeUnknown.Diagnostics.Any(item => item.Code == "critic_verdict_invalid")
            && requestWithoutChinese.Diagnostics.Any(item => item.Code == "critic_verdict_invalid"),
            "keep evidence must intersect the candidate closure, merge must name another known candidate, and request_evidence must contain a Chinese request");

        var dropRouting = gate.Evaluate(IndependentReviewCandidateBundle(BusinessOntologySemanticCriticDecisions.Drop), ValidBaseline());
        var deferRouting = gate.Evaluate(IndependentReviewCandidateBundle(BusinessOntologySemanticCriticDecisions.Defer), ValidBaseline());
        var requestRouting = gate.Evaluate(IndependentReviewCandidateBundle(BusinessOntologySemanticCriticDecisions.RequestEvidence), ValidBaseline());
        var mergeRouting = gate.Evaluate(IndependentReviewCandidateBundle(BusinessOntologySemanticCriticDecisions.Merge), ValidBaseline());
        assert(dropRouting.Status == BusinessOntologySemanticQualityGate.Passed
            && deferRouting.Status == BusinessOntologySemanticQualityGate.Passed
            && requestRouting.Status == BusinessOntologySemanticQualityGate.Passed
            && mergeRouting.Status == BusinessOntologySemanticQualityGate.Passed
            && dropRouting.Diagnostics.Any(item => item.Code == "critic_review_routing" && item.Severity == "info")
            && deferRouting.Diagnostics.Any(item => item.Code == "critic_review_routing" && item.Severity == "info")
            && requestRouting.Diagnostics.Any(item => item.Code == "critic_review_routing" && item.Severity == "info")
            && mergeRouting.Diagnostics.Any(item => item.Code == "critic_review_routing" && item.Severity == "info"),
            "independent drop, defer, request_evidence, and merge candidates are valid review-routing outcomes");

        var mergeTargetReviewBundle = ValidBundle();
        var targetDroppedVerdicts = ReplaceVerdict(mergeTargetReviewBundle, "ItAsset.Asset", new BusinessOntologySemanticCriticVerdict(
            "ItAsset.Asset", BusinessOntologySemanticCriticDecisions.Drop, "资产候选需要进入人工审核。", ["e:service"]));
        var mergeTargetReviewRouted = gate.Evaluate(mergeTargetReviewBundle with
        {
            CriticVerdicts = targetDroppedVerdicts.Select(verdict => verdict.CandidateId == "ItAsset.Custodian"
                ? new BusinessOntologySemanticCriticVerdict("ItAsset.Custodian", BusinessOntologySemanticCriticDecisions.Merge, "保管人候选应合并到资产候选。", ["e:custodian-entity"], "ItAsset.Asset")
                : verdict).ToArray(),
        }, ValidBaseline());
        assert(mergeTargetReviewRouted.Status == BusinessOntologySemanticQualityGate.Failed
            && mergeTargetReviewRouted.Diagnostics.Any(item => item.Code == "critic_merge_target_not_kept"),
            "a merge verdict must target a candidate with an independently valid keep verdict");

        var keptEdgesDependOnReviewRoutedConcept = gate.Evaluate(ValidBundle() with
        {
            CriticVerdicts = ReplaceVerdict(ValidBundle(), "ItAsset.Asset", new BusinessOntologySemanticCriticVerdict(
                "ItAsset.Asset", BusinessOntologySemanticCriticDecisions.Drop, "当前证据不足以保留资产候选。", ["e:service"])),
        }, ValidBaseline());
        assert(keptEdgesDependOnReviewRoutedConcept.Status == BusinessOntologySemanticQualityGate.Failed
            && keptEdgesDependOnReviewRoutedConcept.Diagnostics.Count(item => item.Code == "keep_semantic_dependency_review_routed") == 3,
            "a kept relation, rule, or lifecycle cannot depend on a concept routed for review");

        var invalidCriticProvenance = gate.Evaluate(ValidBundle() with
        {
            CriticRun = new BusinessOntologySemanticCriticRunProvenance("fixture-v3", "source-fixture", "other-input", "critic-snapshot"),
        }, ValidBaseline());
        assert(invalidCriticProvenance.Status == BusinessOntologySemanticQualityGate.Failed
            && invalidCriticProvenance.Diagnostics.Any(item => item.Code == "critic_provenance_invalid"),
            "critic provenance must bind the same input snapshot and run independently from synthesis");
    }

    private static BusinessOntologySemanticCandidateBundle ValidBundle() => new(
        "ItAsset", "fixture-v3", "source-fixture", "input-fixture-v3",
        new BusinessOntologySemanticCriticRunProvenance("fixture-v3-critic", "source-fixture", "input-fixture-v3", "critic-snapshot-fixture-v3"),
        Evidence(),
        [new BusinessOntologySemanticDomainCharter("ItAsset.Inventory", "资产管理", "管理资产登记、保管、盘点和状态变更。", ["e:controller", "e:service"], ["资产登记"])],
        [
            new BusinessOntologySemanticCluster("cluster:asset", "ItAsset.Inventory", "ItAsset.Asset", "资产", "可登记、保管和盘点的实物资产。", [
                new BusinessOntologySemanticImplementationAnchor("java:AssetController", "controller", "src/AssetController.java", "e:controller"),
                new BusinessOntologySemanticImplementationAnchor("java:AssetService", "service", "src/AssetService.java", "e:service"),
                new BusinessOntologySemanticImplementationAnchor("java:AssetStateService", "state-machine", "src/AssetStateService.java", "e:state"),
                new BusinessOntologySemanticImplementationAnchor("web:AssetPage", "page", "web/AssetPage.tsx", "e:page"),
            ]),
            new BusinessOntologySemanticCluster("cluster:custodian", "ItAsset.Inventory", "ItAsset.Custodian", "保管人", "对资产负有保管责任的人员。", [
                new BusinessOntologySemanticImplementationAnchor("java:CustodianEntity", "entity", "src/CustodianEntity.java", "e:custodian-entity"),
                new BusinessOntologySemanticImplementationAnchor("web:CustodianSelector", "form", "web/CustodianSelector.tsx", "e:custodian-form"),
            ]),
        ],
        [new BusinessOntologySemanticRelation("ItAsset.Relation.AssetCustodian", "ItAsset.Asset", "ItAsset.Custodian", "hasCustodian", "资产由保管人负责。", [
            Binding("service-call", "服务调用证明资产与保管人的责任关系。", "e:service"),
            Binding("typed-reference", "实体引用证明保管人可以承担资产责任。", "e:custodian-entity"),
        ])],
        [new BusinessOntologySemanticRule("ItAsset.Rule.AssetRequiresCustodian", "ItAsset.Asset", "资产登记前必须指定保管人。", [
            Binding("validation-branch", "服务校验分支证明登记必须指定保管人。", "e:service"),
        ])],
        [new BusinessOntologySemanticLifecycle("ItAsset.Lifecycle.Asset", "ItAsset.Asset", "status", "资产在登记、在用和处置状态之间流转。", [
            Binding("state-update", "状态赋值证据证明资产生命周期。", "e:state"),
        ])],
        [
            new BusinessOntologySemanticCriticVerdict("ItAsset.Asset", BusinessOntologySemanticCriticDecisions.Keep, "跨层锚点共同证明资产业务概念。", ["e:controller", "e:service"]),
            new BusinessOntologySemanticCriticVerdict("ItAsset.Custodian", BusinessOntologySemanticCriticDecisions.Keep, "实体和表单共同证明保管人业务概念。", ["e:custodian-entity", "e:custodian-form"]),
            new BusinessOntologySemanticCriticVerdict("ItAsset.Relation.AssetCustodian", BusinessOntologySemanticCriticDecisions.Keep, "服务和实体绑定共同证明资产保管关系。", ["e:service"]),
            new BusinessOntologySemanticCriticVerdict("ItAsset.Rule.AssetRequiresCustodian", BusinessOntologySemanticCriticDecisions.Keep, "服务校验绑定共同证明登记约束。", ["e:service"]),
            new BusinessOntologySemanticCriticVerdict("ItAsset.Lifecycle.Asset", BusinessOntologySemanticCriticDecisions.Keep, "状态赋值绑定证明资产生命周期。", ["e:state"]),
        ]);

    private static BusinessOntologySemanticCandidateBundle ProjectionBundle() => new(
        "ItAsset", "fixture-projection", "source-fixture", "input-projection",
        new BusinessOntologySemanticCriticRunProvenance("fixture-projection-critic", "source-fixture", "input-projection", "critic-snapshot-projection"),
        Evidence(),
        [new BusinessOntologySemanticDomainCharter("ItAsset.Inventory", "资产管理", "资产模块。", ["e:entity", "e:dto"], [])],
        [new BusinessOntologySemanticCluster("cluster:asset", "ItAsset.Inventory", "ItAsset.Asset", "资产", "资产对象。", [
            new BusinessOntologySemanticImplementationAnchor("java:AssetEntity", "entity", "src/AssetEntity.java", "e:entity"),
            new BusinessOntologySemanticImplementationAnchor("java:AssetDto", "dto", "src/AssetDto.java", "e:dto"),
        ])],
        [], [], [], [new BusinessOntologySemanticCriticVerdict("ItAsset.Asset", BusinessOntologySemanticCriticDecisions.Keep, "两个实现载体看似支持资产概念。", ["e:entity", "e:dto"])]);

    private static BusinessOntologySemanticVerifiedProjectionBaseline ValidBaseline() => Baseline(
        "source-fixture", "input-fixture-v3",
        [
            new BusinessOntologySemanticProjectionCarrier("ItAsset.Asset", ["java:AssetEntity", "java:AssetDto"]),
            new BusinessOntologySemanticProjectionCarrier("ItAsset.Custodian", ["java:CustodianEntity"]),
        ]);

    private static BusinessOntologySemanticVerifiedProjectionBaseline ProjectionBaseline() => Baseline(
        "source-fixture", "input-projection",
        [new BusinessOntologySemanticProjectionCarrier("ItAsset.Asset", ["java:AssetEntity", "java:AssetDto"])]);

    private static BusinessOntologySemanticVerifiedProjectionBaseline Baseline(
        string sourceFingerprint,
        string inputFingerprint,
        IReadOnlyList<BusinessOntologySemanticProjectionCarrier> carriers)
    {
        var carrierDigest = BusinessOntologySemanticProjectionBaselineDigest.Compute(carriers);
        return new(
            sourceFingerprint,
            inputFingerprint,
            carrierDigest,
            BusinessOntologySemanticProjectionBaselineDigest.ComputeIntegrityDigest(
                sourceFingerprint, inputFingerprint, carrierDigest, carriers.Count),
            carriers.Count,
            carriers);
    }

    private static BusinessOntologySemanticCandidateBundle IndependentReviewCandidateBundle(string decision)
    {
        var bundle = ValidBundle();
        var candidateId = "ItAsset.Audit";
        return bundle with
        {
            Clusters = bundle.Clusters.Concat([
                new BusinessOntologySemanticCluster("cluster:audit", "ItAsset.Inventory", candidateId, "资产审计", "独立记录资产管理过程的审计业务概念。", [
                    new BusinessOntologySemanticImplementationAnchor("java:AssetController", "controller", "src/AssetController.java", "e:controller"),
                    new BusinessOntologySemanticImplementationAnchor("web:AssetPage", "page", "web/AssetPage.tsx", "e:page"),
                ]),
            ]).ToArray(),
            CriticVerdicts = bundle.CriticVerdicts.Concat([
                new BusinessOntologySemanticCriticVerdict(
                    candidateId,
                    decision,
                    "资产审计候选需要人工审核后再决定发布。",
                    ["e:controller"],
                    decision == BusinessOntologySemanticCriticDecisions.Merge ? "ItAsset.Asset" : null,
                    decision == BusinessOntologySemanticCriticDecisions.RequestEvidence ? "请补充资产审计流程的中文证据摘要。" : null),
            ]).ToArray(),
        };
    }

    private sealed class QualityGateTestHarness
    {
        private readonly BusinessOntologySemanticQualityGate qualityGate = new();

        public BusinessOntologySemanticQualityReport Evaluate(
            BusinessOntologySemanticCandidateBundle bundle,
            BusinessOntologySemanticVerifiedProjectionBaseline baseline) =>
            Evaluate(bundle, EvaluationContextFor(bundle), new DeterministicBaselineProvider(baseline), new ApprovingCriticRunVerifier());

        public BusinessOntologySemanticQualityReport Evaluate(
            BusinessOntologySemanticCandidateBundle bundle,
            BusinessOntologySemanticEvaluationContext context,
            IBusinessOntologySemanticVerifiedProjectionBaselineProvider baselineProvider,
            IBusinessOntologySemanticTrustedCriticRunVerifier criticRunVerifier) =>
            qualityGate.Evaluate(bundle, context, baselineProvider, criticRunVerifier);
    }

    private sealed class DeterministicBaselineProvider(BusinessOntologySemanticVerifiedProjectionBaseline baseline)
        : IBusinessOntologySemanticVerifiedProjectionBaselineProvider
    {
        public BusinessOntologySemanticVerifiedProjectionBaseline GetVerifiedBaseline(BusinessOntologySemanticEvaluationContext context) => baseline;
    }

    private sealed class ContextSelectingBaselineProvider(IReadOnlyList<BusinessOntologySemanticVerifiedProjectionBaseline> baselines)
        : IBusinessOntologySemanticVerifiedProjectionBaselineProvider
    {
        public BusinessOntologySemanticEvaluationContext? RequestedContext { get; private set; }

        public BusinessOntologySemanticVerifiedProjectionBaseline GetVerifiedBaseline(BusinessOntologySemanticEvaluationContext context)
        {
            RequestedContext = context;
            return baselines.Single(baseline => baseline.SourceFingerprint == context.SourceFingerprint
                && baseline.InputFingerprint == context.InputFingerprint);
        }
    }

    private sealed class ApprovingCriticRunVerifier : IBusinessOntologySemanticTrustedCriticRunVerifier
    {
        public bool VerifyCriticRun(
            BusinessOntologySemanticEvaluationContext context,
            BusinessOntologySemanticCriticRunProvenance criticRun) => true;
    }

    private sealed class RejectingCriticRunVerifier : IBusinessOntologySemanticTrustedCriticRunVerifier
    {
        public bool VerifyCriticRun(
            BusinessOntologySemanticEvaluationContext context,
            BusinessOntologySemanticCriticRunProvenance criticRun) => false;
    }

    private static BusinessOntologySemanticClaimEvidenceBinding Binding(string type, string summaryZh, string evidenceId) =>
        new(type, summaryZh, evidenceId);

    private static IReadOnlyList<BusinessOntologySemanticCriticVerdict> ReplaceVerdict(
        BusinessOntologySemanticCandidateBundle bundle,
        string candidateId,
        BusinessOntologySemanticCriticVerdict replacement) =>
        bundle.CriticVerdicts.Select(verdict => verdict.CandidateId == candidateId ? replacement : verdict).ToArray();

    private static BusinessOntologySemanticEvaluationContext EvaluationContextFor(
        BusinessOntologySemanticCandidateBundle bundle) =>
        new(bundle.SourceFingerprint, bundle.InputFingerprint, bundle.SynthesisRunId, bundle.CriticRun.SnapshotDigest);

    private static IReadOnlyList<BusinessOntologySemanticEvidence> Evidence() =>
    [
        new("e:controller", "route_binding", "backend", "src/AssetController.java", "java:AssetController"),
        new("e:service", "business_guard", "backend", "src/AssetService.java", "java:AssetService"),
        new("e:page", "frontend_page", "frontend", "web/AssetPage.tsx", "web:AssetPage"),
        new("e:custodian-entity", "typed_reference", "backend", "src/CustodianEntity.java", "java:CustodianEntity"),
        new("e:custodian-form", "frontend_form", "frontend", "web/CustodianSelector.tsx", "web:CustodianSelector"),
        new("e:entity", "typed_reference", "backend", "src/AssetEntity.java", "java:AssetEntity"),
        new("e:dto", "dto_mapping", "backend", "src/AssetDto.java", "java:AssetDto"),
        new("e:state", "state_assignment", "backend", "src/AssetStateService.java", "java:AssetStateService"),
        new("e:unrelated-service", "business_guard", "backend", "src/UnrelatedService.java", "java:UnrelatedService"),
        new("e:unrelated-state", "state_assignment", "backend", "src/UnrelatedStateService.java", "java:UnrelatedStateService"),
    ];
}
