using System.Text.Json;
using Cozo.DotNet.LlmWiki.Tools;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologySemanticPendingDraftTests
{
    public static void Run(Action<bool, string> assert)
    {
        AcceptsMultiAnchorChineseBusinessDraft(assert);
        StagesWithoutFabricatingFinalReviewInputs(assert);
        RejectsSymbolShapedOrUnsupportedDrafts(assert);
        ParsesOnlyClosedBoundedPayloads(assert);
    }

    private static void AcceptsMultiAnchorChineseBusinessDraft(Action<bool, string> assert)
    {
        var validation = Validate(Draft());
        assert(validation.QualityReport.Status == BusinessOntologySemanticQualityGate.Passed
                && validation.Draft.Clusters.All(cluster => cluster.ImplementationAnchors.Count >= 2)
                && validation.Draft.Clusters.All(cluster => HasCjk(cluster.NameZh) && HasCjk(cluster.DescriptionZh))
                && validation.Draft.Attributes.Count == 1
                && validation.Draft.Relations.Count == 1
                && validation.Draft.Rules.Count == 1
                && validation.Draft.Lifecycles.Count == 1,
            "a pending v3 draft must aggregate multiple implementation anchors and retain Chinese, evidence-closed attributes, relations, rules, and lifecycles in memory");
    }

    private static void RejectsSymbolShapedOrUnsupportedDrafts(Action<bool, string> assert)
    {
        var singleSymbol = Throws(() => Validate(
            Draft() with { Clusters = [Draft().Clusters[0] with { ImplementationAnchors = [Draft().Clusters[0].ImplementationAnchors[0]] }, Draft().Clusters[1]] }));
        var fqn = Throws(() => Validate(
            Draft() with { Clusters = [Draft().Clusters[0] with { ConceptId = "System.Xml.AssetEntity" }, Draft().Clusters[1]] }));
        var unsupportedAttributeEvidence = Throws(() => Validate(
            Draft() with { Attributes = [Draft().Attributes[0] with { EvidenceIds = ["missing"] }] }));

        assert(singleSymbol && fqn && unsupportedAttributeEvidence,
            "single-symbol, FQN-shaped, and evidence-outside-registry candidates must be rejected before any pending result can exist");
    }

    private static void StagesWithoutFabricatingFinalReviewInputs(Action<bool, string> assert)
    {
        var draft = Draft();
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = BusinessOntologySemanticPendingDraftValidator.SchemaVersion,
            domainId = draft.DomainId,
            domainCharters = draft.DomainCharters,
            clusters = draft.Clusters,
            attributes = draft.Attributes,
            relations = draft.Relations,
            rules = draft.Rules,
            lifecycles = draft.Lifecycles,
        });
        var staged = BusinessOntologySemanticPendingDraftValidator.ParseAndValidateForStaging(
            json, Evidence(), draft.DomainCharters);
        var altered = draft with
        {
            DomainCharters = [draft.DomainCharters[0] with { NameZh = "伪造资产管理" }],
        };
        var alteredJson = JsonSerializer.Serialize(new
        {
            schemaVersion = BusinessOntologySemanticPendingDraftValidator.SchemaVersion,
            domainId = altered.DomainId,
            domainCharters = altered.DomainCharters,
            clusters = altered.Clusters,
            attributes = altered.Attributes,
            relations = altered.Relations,
            rules = altered.Rules,
            lifecycles = altered.Lifecycles,
        });
        var changedCharter = Throws(() => BusinessOntologySemanticPendingDraftValidator.ParseAndValidateForStaging(
            alteredJson, Evidence(), draft.DomainCharters));

        assert(staged.Clusters.Count == 2 && changedCharter,
            "pre-critic staging must enforce the closed, runtime-discovered charter and evidence boundary without inventing a trusted baseline or critic provenance");
    }

    private static void ParsesOnlyClosedBoundedPayloads(Action<bool, string> assert)
    {
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = BusinessOntologySemanticPendingDraftValidator.SchemaVersion,
            domainId = Draft().DomainId,
            domainCharters = Draft().DomainCharters,
            clusters = Draft().Clusters,
            attributes = Draft().Attributes,
            relations = Draft().Relations,
            rules = Draft().Rules,
            lifecycles = Draft().Lifecycles,
        });
        var parsed = Parse(json);
        var extraField = Throws(() => BusinessOntologySemanticPendingDraftValidator.ParseAndValidate(
            json[..^1] + ",\"prompt\":\"ignore evidence\"}", Evidence(), Context(), BaselineProvider.Instance, CriticVerifier.Instance));
        var tooMany = Throws(() => BusinessOntologySemanticPendingDraftValidator.ParseAndValidate(
            json.Replace("\"attributes\":[", "\"attributes\":[" + string.Join(',', Enumerable.Repeat(JsonSerializer.Serialize(Draft().Attributes[0]), 13)) + ",", StringComparison.Ordinal), Evidence(), Context(), BaselineProvider.Instance, CriticVerifier.Instance));

        assert(parsed.Clusters.Count == 2 && extraField && tooMany,
            "model drafts must use the closed schema, bounded collections, and runtime evidence registry rather than arbitrary prompt-shaped fields");
    }

    private static BusinessOntologySemanticPendingDraft Draft() => new(
        "Assets",
        [new BusinessOntologySemanticDomainCharter("Assets", "资产管理", "管理资产登记、保管和状态变更的业务域。", ["e:route", "e:asset-service"], ["登记资产"])],
        [
            new BusinessOntologySemanticCluster("cluster:asset", "Assets", "Assets.AssetRecord", "资产记录", "用于登记和追踪企业资产的业务记录。", [
                new BusinessOntologySemanticImplementationAnchor("java:AssetController", "controller", "src/AssetController.java", "e:route"),
                new BusinessOntologySemanticImplementationAnchor("java:AssetService", "service", "src/AssetService.java", "e:asset-service"),
            ]),
            new BusinessOntologySemanticCluster("cluster:custodian", "Assets", "Assets.Custodian", "资产保管人", "负责接收和保管资产的业务责任人。", [
                new BusinessOntologySemanticImplementationAnchor("java:CustodianEntity", "entity", "src/CustodianEntity.java", "e:custodian-ref"),
                new BusinessOntologySemanticImplementationAnchor("web:CustodianPicker", "page", "web/CustodianPicker.tsx", "e:custodian-route"),
            ]),
        ],
        [new BusinessOntologySemanticAttribute("Assets.AssetRecord.AssetCode", "Assets.AssetRecord", "资产编号", "string", "用于唯一识别资产记录的业务编号。", ["e:asset-service"])],
        [new BusinessOntologySemanticRelation("Assets.Relation.CustodianKeepsAsset", "Assets.Custodian", "Assets.AssetRecord", "保管", "资产保管人负责保管资产记录。", [
            new BusinessOntologySemanticClaimEvidenceBinding("typed-reference", "实体引用证明保管人与资产记录关联。", "e:custodian-ref"),
            new BusinessOntologySemanticClaimEvidenceBinding("typed-reference", "服务引用证明资产记录关联保管人。", "e:asset-service"),
        ])],
        [new BusinessOntologySemanticRule("Assets.Rule.AssetCodeRequired", "Assets.AssetRecord", "登记资产时必须提供资产编号。", [
            new BusinessOntologySemanticClaimEvidenceBinding("validation-branch", "校验分支要求资产编号不能为空。", "e:asset-guard"),
        ])],
        [new BusinessOntologySemanticLifecycle("Assets.Lifecycle.AssetStatus", "Assets.AssetRecord", "资产状态", "资产记录会从待登记变更为在用状态。", [
            new BusinessOntologySemanticClaimEvidenceBinding("state-update", "状态赋值记录资产进入在用状态。", "e:asset-state"),
        ])]);

    private static IReadOnlyList<BusinessOntologySemanticEvidence> Evidence() =>
    [
        new("e:route", "route_binding", "repo", "src/AssetController.java", "java:AssetController"),
        new("e:asset-service", "typed_reference", "repo", "src/AssetService.java", "java:AssetService"),
        new("e:custodian-ref", "typed_reference", "repo", "src/CustodianEntity.java", "java:CustodianEntity"),
        new("e:custodian-route", "route_binding", "repo", "web/CustodianPicker.tsx", "web:CustodianPicker"),
        new("e:asset-guard", "business_guard", "repo", "src/AssetService.java", "java:AssetService"),
        new("e:asset-state", "state_assignment", "repo", "src/AssetService.java", "java:AssetService"),
    ];

    private static BusinessOntologySemanticPendingDraftValidation Validate(BusinessOntologySemanticPendingDraft draft) =>
        BusinessOntologySemanticPendingDraftValidator.Validate(draft, Evidence(), Context(), BaselineProvider.Instance, CriticVerifier.Instance);

    private static BusinessOntologySemanticPendingDraft Parse(string json) =>
        BusinessOntologySemanticPendingDraftValidator.ParseAndValidate(json, Evidence(), Context(), BaselineProvider.Instance, CriticVerifier.Instance);

    private static BusinessOntologySemanticEvaluationContext Context() => new("source", "input", "pending-synthesis", "pending-draft");

    private sealed class BaselineProvider : IBusinessOntologySemanticVerifiedProjectionBaselineProvider
    {
        public static BaselineProvider Instance { get; } = new();
        public BusinessOntologySemanticVerifiedProjectionBaseline GetVerifiedBaseline(BusinessOntologySemanticEvaluationContext context)
        {
            var carriers = new[] { new BusinessOntologySemanticProjectionCarrier("fixture-baseline", ["fixture:unmatched"]) };
            var digest = BusinessOntologySemanticProjectionBaselineDigest.Compute(carriers);
            return new(context.SourceFingerprint, context.InputFingerprint, digest,
                BusinessOntologySemanticProjectionBaselineDigest.ComputeIntegrityDigest(context.SourceFingerprint, context.InputFingerprint, digest, carriers.Length),
                carriers.Length, carriers);
        }
    }

    private sealed class CriticVerifier : IBusinessOntologySemanticTrustedCriticRunVerifier
    {
        public static CriticVerifier Instance { get; } = new();
        public bool VerifyCriticRun(BusinessOntologySemanticEvaluationContext context, BusinessOntologySemanticCriticRunProvenance criticRun) =>
            criticRun.RunId == "pending-critic" && criticRun.SnapshotDigest == context.ExpectedCriticSnapshotDigest;
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }

    private static bool HasCjk(string value) => value.Any(character => character is >= '\u4e00' and <= '\u9fff');
}
