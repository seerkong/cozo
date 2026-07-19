using System.Xml.Linq;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyMaterializationServiceTests
{
    private const string OntologyId = "SampleDomain.Ontology";
    private const string Record = OntologyId + ".Record";
    private const string Supplier = OntologyId + ".Supplier";

    public static async Task RunAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var store = new BusinessOntologyStore(new CozoOm(db));
        var fixture = BuildFixture();
        await store.ReplaceGenerationAsync(fixture.InitialGeneration);
        await store.AppendReviewsAsync(
            OntologyId,
            fixture.InitialGeneration.GenerationId,
            fixture.Reviews);

        // Review history is intentionally generation-independent. Removing the accepted stale
        // candidate must preserve its decision and make materialization emit a stable diagnostic.
        await store.ReplaceGenerationAsync(fixture.ActiveGeneration);
        var service = new BusinessOntologyMaterializationService(store);
        var result = await service.MaterializeAsync(OntologyId);

        assert(
            result.Relations == 1
            && result.Rules == 1
            && result.Lifecycles == 1
            && result.States == 2
            && result.Transitions == 1
            && result.StaleReviews == 1,
            "only the three effectively accepted, still-valid semantic candidates should materialize");

        var snapshot = await store.ReadExportableAsync(OntologyId);
        AssertMaterializedSnapshot(snapshot, fixture, assert);
        var staleDiagnosticId = snapshot.Diagnostics.Single(
            item => item.Kind == "stale_review").Id;

        var repeated = await service.MaterializeAsync(OntologyId);
        var repeatedSnapshot = await store.ReadExportableAsync(OntologyId);
        assert(
            repeated == result
            && repeatedSnapshot.Diagnostics.Single(item => item.Kind == "stale_review").Id
                == staleDiagnosticId,
            "repeat materialization should replace derived records deterministically and retain a stable stale diagnostic");

        await AssertExportAsync(store, repeatedSnapshot, fixture, assert);
        await AssertAtomicRollbackAsync(store, repeatedSnapshot, fixture, assert);
        await AssertRuleProjectionMatrixAsync(assert);
    }

    private static async Task AssertRuleProjectionMatrixAsync(
        Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var store = new BusinessOntologyStore(new CozoOm(db));
        var validator = new OntologySemanticCandidateValidator();
        var concepts = new[] { Record, Supplier };
        var evidenceIds = new[]
        {
            "evidence:record",
            "evidence:supplier",
            "evidence:rule",
            "evidence:states",
            "evidence:transition",
            "evidence:pending",
            "evidence:superseded",
            "evidence:stale",
        };

        CandidateFixture Rule(
            string suffix,
            string property,
            string ruleKind,
            string predicate,
            string description) =>
            ValidateCandidate(
                validator,
                concepts,
                evidenceIds,
                $$$"""
                {
                  "schemaVersion":"onto-semantic-v1",
                  "kind":"rule",
                  "semantic":{
                    "id":"{{{OntologyId}}}.Rule.{{{suffix}}}",
                    "subjectConceptId":"{{{Record}}}",
                    "ruleKind":"{{{ruleKind}}}",
                    "descriptionZh":"{{{description}}}",
                    "predicate":{"property":"{{{property}}}",{{{predicate}}}},
                    "effect":{"type":"reject","messageZh":"{{{description}}}"}
                  },
                  "evidenceIds":["evidence:rule"],
                  "basis":"deterministic",
                  "rationale":"projector rule round-trip fixture"
                }
                """);

        var required = Rule(
            "RecordCodeRequired",
            "recordCode",
            "required",
            "\"operator\":\"present\"",
            "记录编码必须提供。");
        var min = Rule(
            "PurchasePriceMin",
            "purchasePrice",
            "min",
            "\"operator\":\"min\",\"value\":2",
            "采购价格不得低于 2。");
        var max = Rule(
            "QuantityMax",
            "quantity",
            "max",
            "\"operator\":\"max\",\"value\":100",
            "数量不得超过 100。");
        var pattern = Rule(
            "RecordCodePattern",
            "recordCode",
            "pattern",
            "\"operator\":\"matches\",\"value\":\"[A-Z0-9-]+\"",
            "记录编码必须符合格式。");
        var unique = Rule(
            "RecordCodeUnique",
            "recordCode",
            "unique",
            "\"operator\":\"unique\"",
            "记录编码必须唯一。");
        var minLength = Rule(
            "DisplayNameMinLength",
            "displayName",
            "min",
            "\"operator\":\"minLength\",\"value\":3",
            "显示名称长度不得小于 3。");
        var candidates = new[]
        {
            required,
            min,
            max,
            pattern,
            unique,
            minLength,
        };
        var generation = Generation(
            "rule-projection-generation",
            candidates.Select(item => item.Candidate).ToArray());
        await store.ReplaceGenerationAsync(generation);
        await store.AppendReviewsAsync(
            OntologyId,
            generation.GenerationId,
            candidates.Select((candidate, index) =>
                Review(
                    candidate,
                    "accepted",
                    $"2026-07-18T09:{index:00}:00.0000000+00:00"))
                .ToArray());

        var result = await new BusinessOntologyMaterializationService(store)
            .MaterializeAsync(OntologyId);
        var snapshot = await store.ReadExportableAsync(OntologyId);
        assert(
            result.Rules == 4
            && result.StaleReviews == 0
            && snapshot.Rules.Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(
                [
                    required.SemanticId,
                    min.SemanticId,
                    max.SemanticId,
                    unique.SemanticId,
                ]),
            "required, numeric min/max, and unique should materialize without changing their semantics");

        var unsupported = snapshot.Diagnostics
            .Where(item => item.Kind == "unsupported_rule_projection")
            .OrderBy(item => item.ConflictKey, StringComparer.Ordinal)
            .ToArray();
        assert(
            unsupported.Length == 2
            && unsupported.Any(item =>
                item.ConflictKey == pattern.Id
                && item.DetailsJson.Contains(
                    "\"reasonCode\":\"pattern_predicate_unsupported\"",
                    StringComparison.Ordinal))
            && unsupported.Any(item =>
                item.ConflictKey == minLength.Id
                && item.DetailsJson.Contains(
                    "\"reasonCode\":\"property_length_predicate_unsupported\"",
                    StringComparison.Ordinal)),
            "pattern and minLength should produce explicit, stable unsupported projection diagnostics");
        assert(
            snapshot.Candidates.Single(item => item.Id == pattern.Id).Status == "pending"
            && snapshot.Candidates.Single(item => item.Id == minLength.Id).Status == "pending"
            && snapshot.Reviews.Any(item =>
                item.CandidateId == pattern.Id && item.Decision == "accepted")
            && snapshot.Reviews.Any(item =>
                item.CandidateId == minLength.Id && item.Decision == "accepted"),
            "unsupported rules should retain their candidates and accepted review history without materializing");

        var root = Path.Combine(
            Path.GetTempPath(),
            "onto-rule-projection-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new BusinessOntologyXmlExporter(store).ExportAsync(
                new BusinessOntologyXmlExportRequest(OntologyId, root),
                [snapshot]);
            var document = XDocument.Load(
                Path.Combine(root, "rules", "generated.xml"));
            var rules = document
                .Descendants("Rule")
                .ToDictionary(
                    item => item.Attribute("id")!.Value,
                    StringComparer.Ordinal);

            static XElement RequiredPredicate(
                IReadOnlyDictionary<string, XElement> rules,
                string id) =>
                rules[id].Element("Require")!.Elements().Single();

            var requiredXml = RequiredPredicate(rules, required.SemanticId);
            var minXml = RequiredPredicate(rules, min.SemanticId);
            var maxXml = RequiredPredicate(rules, max.SemanticId);
            var uniqueXml = RequiredPredicate(rules, unique.SemanticId);
            assert(
                rules[required.SemanticId].Attribute("kind")?.Value == "Conditional"
                && requiredXml.Name.LocalName == "PropertyPresent"
                && requiredXml.Attribute("property")?.Value == "recordCode",
                "required/present should round-trip as Conditional + PropertyPresent");
            assert(
                rules[min.SemanticId].Attribute("kind")?.Value == "Conditional"
                && minXml.Name.LocalName == "PropertyCompare"
                && minXml.Attribute("property")?.Value == "purchasePrice"
                && minXml.Attribute("op")?.Value == "gte"
                && minXml.Attribute("value")?.Value == "2",
                "numeric min should round-trip as Conditional + PropertyCompare gte");
            assert(
                rules[max.SemanticId].Attribute("kind")?.Value == "Conditional"
                && maxXml.Name.LocalName == "PropertyCompare"
                && maxXml.Attribute("property")?.Value == "quantity"
                && maxXml.Attribute("op")?.Value == "lte"
                && maxXml.Attribute("value")?.Value == "100",
                "numeric max should round-trip as Conditional + PropertyCompare lte");
            assert(
                rules[unique.SemanticId].Attribute("kind")?.Value == "Uniqueness"
                && uniqueXml.Name.LocalName == "PropertyPresent"
                && uniqueXml.Attribute("property")?.Value == "recordCode",
                "unique should round-trip as Uniqueness with an explicit property predicate");
            assert(
                !rules.ContainsKey(pattern.SemanticId)
                && !rules.ContainsKey(minLength.SemanticId),
                "unsupported pattern and property-length semantics must not appear in XML rules");

            var audit = await File.ReadAllTextAsync(
                Path.Combine(root, "generation", "candidates.json"));
            assert(
                audit.Contains(pattern.Id, StringComparison.Ordinal)
                && audit.Contains(minLength.Id, StringComparison.Ordinal)
                && audit.Contains(
                    "unsupported_rule_projection",
                    StringComparison.Ordinal),
                "unsupported candidates, reviews, and diagnostics should remain auditable in the generation sidecar");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup must not hide the contract assertion.
            }
        }

        var unsupportedRoot = Path.Combine(
            Path.GetTempPath(),
            "onto-rule-projection-reject-" + Guid.NewGuid().ToString("N"));
        var unsafeSnapshot = snapshot with
        {
            Rules =
            [
                .. snapshot.Rules,
                new BusinessOntologyRule(
                    pattern.SemanticId,
                    Record,
                    "pattern",
                    "记录编码必须符合格式。",
                    """{"operator":"matches","property":"recordCode","value":"[A-Z0-9-]+"}""",
                    "{}",
                    "accepted",
                    0.9,
                    ["evidence:rule"]),
            ],
        };
        var rejected = false;
        try
        {
            await new BusinessOntologyXmlExporter(store).ExportAsync(
                new BusinessOntologyXmlExportRequest(
                    OntologyId,
                    unsupportedRoot),
                [unsafeSnapshot]);
        }
        catch (InvalidOperationException ex)
        {
            rejected = ex.Message.Contains(
                "pattern_predicate_unsupported",
                StringComparison.Ordinal);
        }
        assert(
            rejected && !Directory.Exists(unsupportedRoot),
            "export preflight should explicitly reject an unsupported persisted rule before writing a partial bundle");
    }

    private static void AssertMaterializedSnapshot(
        BusinessOntologySnapshot snapshot,
        Fixture fixture,
        Action<bool, string> assert)
    {
        var relation = snapshot.Relations.Single();
        assert(
            relation.Id == fixture.Relation.SemanticId
            && relation.Status == "accepted"
            && relation.Description == "记录直接关联供应商。"
            && relation.EvidenceIds.SequenceEqual(
                ["evidence:record", "evidence:supplier"],
                StringComparer.Ordinal),
            "accepted relation materialization should preserve its Chinese description and direct evidence refs");

        var rule = snapshot.Rules.Single();
        assert(
            rule.Id == fixture.Rule.SemanticId
            && rule.Status == "accepted"
            && rule.Description == "记录编码不能为空。"
            && rule.EvidenceIds.SequenceEqual(["evidence:rule"], StringComparer.Ordinal),
            "accepted rule materialization should preserve its Chinese description and direct evidence refs");

        var lifecycle = snapshot.Lifecycles.Single();
        assert(
            lifecycle.Id == fixture.Lifecycle.SemanticId
            && lifecycle.Status == "accepted"
            && lifecycle.Description == "记录审批生命周期。"
            && lifecycle.EvidenceIds.SequenceEqual(
                ["evidence:states", "evidence:transition"],
                StringComparer.Ordinal),
            "accepted lifecycle materialization should preserve its Chinese description and direct evidence refs");
        assert(
            snapshot.States.Count == 2
            && snapshot.States.All(item =>
                item.Description is "草稿" or "已批准"
                && (item.EvidenceIds ?? []).SequenceEqual(
                    ["evidence:states", "evidence:transition"],
                    StringComparer.Ordinal)),
            "materialized states should retain Chinese descriptions and lifecycle direct evidence refs");
        assert(
            snapshot.Transitions.Count == 1
            && snapshot.Transitions[0].Description == "批准记录。"
            && snapshot.Transitions[0].Status == "accepted"
            && snapshot.Transitions[0].EvidenceIds.SequenceEqual(
                ["evidence:states", "evidence:transition"],
                StringComparer.Ordinal),
            "materialized transitions should retain Chinese descriptions and direct evidence refs");

        assert(
            snapshot.Relations.All(item =>
                item.Id != fixture.Rejected.SemanticId
                && item.Id != fixture.Superseded.SemanticId
                && item.Id != fixture.Stale.SemanticId)
            && snapshot.Rules.All(item => item.Id != fixture.Pending.SemanticId),
            "rejected, superseded, stale, and pending candidates must not become exportable ontology records");
        assert(
            snapshot.Candidates.Single(item => item.Id == fixture.Rejected.Id).Status == "rejected"
            && snapshot.Candidates.Single(item => item.Id == fixture.Superseded.Id).Status == "superseded"
            && snapshot.Candidates.Single(item => item.Id == fixture.Pending.Id).Status == "pending"
            && snapshot.Candidates.All(item => item.Id != fixture.Stale.Id),
            "candidate audit state should retain rejected, superseded, and pending entries while the removed stale identity stays absent");

        var stale = snapshot.Diagnostics.Single(item => item.Kind == "stale_review");
        assert(
            stale.ConflictKey == fixture.Stale.Id
            && stale.Message.Contains("重新审核", StringComparison.Ordinal)
            && stale.DetailsJson.Contains("\"reasonCode\":\"candidate_absent\"", StringComparison.Ordinal),
            "an accepted review for an absent candidate should emit a stable actionable stale diagnostic");
    }

    private static async Task AssertExportAsync(
        BusinessOntologyStore store,
        BusinessOntologySnapshot snapshot,
        Fixture fixture,
        Action<bool, string> assert)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "onto-materialization-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new BusinessOntologyXmlExporter(store)
                .ExportAsync(
                    new BusinessOntologyXmlExportRequest(OntologyId, root),
                    [snapshot]);
            assert(
                result.Relations == 1 && result.Rules == 1 && result.Lifecycles == 1,
                "XML export should report one accepted module declaration of each semantic kind");

            var expectedModules = new[]
            {
                Path.Combine(root, "relations", "generated.xml"),
                Path.Combine(root, "rules", "generated.xml"),
                Path.Combine(root, "lifecycles", "generated.xml"),
            };
            assert(
                expectedModules.All(File.Exists),
                "accepted semantic records should enable relation, rule, and lifecycle XML modules");

            var relationXml = XDocument.Load(expectedModules[0]).ToString();
            var ruleXml = XDocument.Load(expectedModules[1]).ToString();
            var lifecycleXml = XDocument.Load(expectedModules[2]).ToString();
            var semanticXml = relationXml + ruleXml + lifecycleXml;
            assert(
                semanticXml.Contains("记录直接关联供应商。", StringComparison.Ordinal)
                && semanticXml.Contains("记录编码不能为空。", StringComparison.Ordinal)
                && semanticXml.Contains("记录审批生命周期。", StringComparison.Ordinal)
                && semanticXml.Contains("草稿", StringComparison.Ordinal)
                && semanticXml.Contains("批准记录。", StringComparison.Ordinal)
                && semanticXml.Contains("ref=\"evidence:", StringComparison.Ordinal),
                "semantic XML modules should retain Chinese descriptions and direct EvidenceRef elements");
            assert(
                !semanticXml.Contains(fixture.Rejected.SemanticId, StringComparison.Ordinal)
                && !semanticXml.Contains(fixture.Superseded.SemanticId, StringComparison.Ordinal)
                && !semanticXml.Contains(fixture.Stale.SemanticId, StringComparison.Ordinal)
                && !semanticXml.Contains(fixture.Pending.SemanticId, StringComparison.Ordinal),
                "rejected, superseded, stale, and pending identities must not appear in ontology modules");

            var candidates = await File.ReadAllTextAsync(
                Path.Combine(root, "generation", "candidates.json"));
            assert(
                candidates.Contains(fixture.Pending.Id, StringComparison.Ordinal)
                && candidates.Contains(fixture.Rejected.Id, StringComparison.Ordinal)
                && candidates.Contains(fixture.Superseded.Id, StringComparison.Ordinal)
                && candidates.Contains(fixture.Stale.Id, StringComparison.Ordinal)
                && !semanticXml.Contains(fixture.Pending.Id, StringComparison.Ordinal),
                "pending and non-promoted audit data should remain only in candidates.json");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup must not hide the contract assertion.
            }
        }
    }

    private static async Task AssertAtomicRollbackAsync(
        BusinessOntologyStore store,
        BusinessOntologySnapshot before,
        Fixture fixture,
        Action<bool, string> assert)
    {
        var rejected = false;
        try
        {
            // The invalid status is detected after prior derived records are removed inside the
            // write transaction. Aborting must restore every previously materialized record.
            await store.ApplyMaterializationAsync(new BusinessOntologyMaterializationBatch(
                before.OntologyId,
                before.GenerationId,
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [fixture.Pending.Id] = "invalid",
                }));
        }
        catch (ArgumentException ex)
        {
            rejected = ex.Message.Contains("invalid materialized status", StringComparison.Ordinal);
        }

        var after = await store.ReadExportableAsync(OntologyId);
        assert(rejected, "invalid materialization input should fail inside the write transaction");
        assert(
            after.Relations.Select(item => item.Id).SequenceEqual(
                before.Relations.Select(item => item.Id),
                StringComparer.Ordinal)
            && after.Rules.Select(item => item.Id).SequenceEqual(
                before.Rules.Select(item => item.Id),
                StringComparer.Ordinal)
            && after.Lifecycles.Select(item => item.Id).SequenceEqual(
                before.Lifecycles.Select(item => item.Id),
                StringComparer.Ordinal)
            && after.States.Select(item => (item.LifecycleId, item.Id)).SequenceEqual(
                before.States.Select(item => (item.LifecycleId, item.Id)))
            && after.Transitions.Select(item => item.Id).SequenceEqual(
                before.Transitions.Select(item => item.Id),
                StringComparer.Ordinal),
            "a failed materialization must atomically preserve prior relation, rule, lifecycle, state, and transition records");
    }

    private static Fixture BuildFixture()
    {
        var validator = new OntologySemanticCandidateValidator();
        var concepts = new[] { Record, Supplier };
        var evidenceIds = new[]
        {
            "evidence:record",
            "evidence:supplier",
            "evidence:rule",
            "evidence:states",
            "evidence:transition",
            "evidence:pending",
            "evidence:superseded",
            "evidence:stale",
        };

        var relation = ValidateCandidate(
            validator,
            concepts,
            evidenceIds,
            $$$"""
            {
              "schemaVersion":"onto-semantic-v1",
              "kind":"relation",
              "semantic":{
                "id":"{{{OntologyId}}}.Relation.RecordSupplier",
                "fromConceptId":"{{{Record}}}",
                "toConceptId":"{{{Supplier}}}",
                "name":"supplier",
                "min":"1",
                "max":"1",
                "descriptionZh":"记录直接关联供应商。"
              },
              "evidenceIds":["evidence:record","evidence:supplier"],
              "basis":"deterministic",
              "rationale":"字段类型和非空约束共同支持。"
            }
            """);
        var rule = ValidateCandidate(
            validator,
            concepts,
            evidenceIds,
            $$$"""
            {
              "schemaVersion":"onto-semantic-v1",
              "kind":"rule",
              "semantic":{
                "id":"{{{OntologyId}}}.Rule.RecordCodeRequired",
                "subjectConceptId":"{{{Record}}}",
                "ruleKind":"required",
                "descriptionZh":"记录编码不能为空。",
                "predicate":{"property":"recordCode","operator":"present"},
                "effect":{"type":"reject","messageZh":"记录编码不能为空。"}
              },
              "evidenceIds":["evidence:rule"],
              "basis":"deterministic",
              "rationale":"直接验证约束。"
            }
            """);
        var lifecycle = ValidateCandidate(
            validator,
            concepts,
            evidenceIds,
            $$$"""
            {
              "schemaVersion":"onto-semantic-v1",
              "kind":"lifecycle",
              "semantic":{
                "id":"{{{OntologyId}}}.Lifecycle.RecordApproval",
                "subjectConceptId":"{{{Record}}}",
                "stateProperty":"status",
                "initialState":"DRAFT",
                "descriptionZh":"记录审批生命周期。",
                "states":[
                  {"id":"DRAFT","terminal":false,"descriptionZh":"草稿"},
                  {"id":"APPROVED","terminal":true,"descriptionZh":"已批准"}
                ],
                "transitions":[
                  {
                    "id":"{{{OntologyId}}}.Transition.ApproveRecord",
                    "action":"approve",
                    "fromState":"DRAFT",
                    "toState":"APPROVED",
                    "descriptionZh":"批准记录。",
                    "guard":{},
                    "effect":{"set":{"property":"status","value":"APPROVED"}}
                  }
                ]
              },
              "evidenceIds":["evidence:states","evidence:transition"],
              "basis":"deterministic",
              "rationale":"有限状态和值赋值共同支持。"
            }
            """);
        var rejected = ValidateCandidate(
            validator,
            concepts,
            evidenceIds,
            $$$"""
            {
              "schemaVersion":"onto-semantic-v1",
              "kind":"relation",
              "semantic":{
                "id":"{{{OntologyId}}}.Relation.RecordOwner",
                "fromConceptId":"{{{Record}}}",
                "toConceptId":"{{{Supplier}}}",
                "name":"owner",
                "min":"0",
                "max":"1",
                "descriptionZh":"不应提升的所有者关系。"
              },
              "evidenceIds":["evidence:record"],
              "basis":"deterministic",
              "rationale":"审核后拒绝。"
            }
            """);
        var pending = ValidateCandidate(
            validator,
            concepts,
            evidenceIds,
            $$$"""
            {
              "schemaVersion":"onto-semantic-v1",
              "kind":"rule",
              "semantic":{
                "id":"{{{OntologyId}}}.Rule.RecordCodeUnique",
                "subjectConceptId":"{{{Record}}}",
                "ruleKind":"unique",
                "descriptionZh":"待审核的记录编码唯一规则。",
                "predicate":{"property":"recordCode","operator":"unique"},
                "effect":{"type":"reject","messageZh":"记录编码重复。"}
              },
              "evidenceIds":["evidence:pending"],
              "basis":"deterministic",
              "rationale":"等待人工审核。"
            }
            """);
        var superseded = ValidateCandidate(
            validator,
            concepts,
            evidenceIds,
            $$$"""
            {
              "schemaVersion":"onto-semantic-v1",
              "kind":"relation",
              "semantic":{
                "id":"{{{OntologyId}}}.Relation.LegacySupplier",
                "fromConceptId":"{{{Record}}}",
                "toConceptId":"{{{Supplier}}}",
                "name":"legacySupplier",
                "min":"0",
                "max":"1",
                "descriptionZh":"已由新语义替代的供应商关系。"
              },
              "evidenceIds":["evidence:superseded"],
              "basis":"deterministic",
              "rationale":"由后续候选替代。"
            }
            """);
        var stale = ValidateCandidate(
            validator,
            concepts,
            evidenceIds,
            $$$"""
            {
              "schemaVersion":"onto-semantic-v1",
              "kind":"relation",
              "semantic":{
                "id":"{{{OntologyId}}}.Relation.RecordCategory",
                "fromConceptId":"{{{Record}}}",
                "toConceptId":"{{{Supplier}}}",
                "name":"category",
                "min":"0",
                "max":"many",
                "descriptionZh":"已失效的分类关系。"
              },
              "evidenceIds":["evidence:stale"],
              "basis":"deterministic",
              "rationale":"下一 generation 不再存在。"
            }
            """);

        var candidates = new[]
        {
            relation,
            rule,
            lifecycle,
            rejected,
            pending,
            superseded,
            stale,
        };
        var initial = Generation(
            "materialization-generation-1",
            candidates.Select(item => item.Candidate).ToArray());
        var active = Generation(
            "materialization-generation-2",
            candidates
                .Where(item => item.Id != stale.Id)
                .Select(item => item.Candidate)
                .ToArray());
        var reviews = new[]
        {
            Review(relation, "accepted", "2026-07-18T08:00:00.0000000+00:00"),
            Review(rule, "accepted", "2026-07-18T08:01:00.0000000+00:00"),
            Review(lifecycle, "accepted", "2026-07-18T08:02:00.0000000+00:00"),
            Review(rejected, "rejected", "2026-07-18T08:03:00.0000000+00:00"),
            Review(superseded, "superseded", "2026-07-18T08:04:00.0000000+00:00"),
            Review(stale, "accepted", "2026-07-18T08:05:00.0000000+00:00"),
        };
        return new Fixture(
            initial,
            active,
            reviews,
            relation,
            rule,
            lifecycle,
            rejected,
            pending,
            superseded,
            stale);
    }

    private static CandidateFixture ValidateCandidate(
        OntologySemanticCandidateValidator validator,
        IReadOnlyList<string> concepts,
        IReadOnlyList<string> evidenceIds,
        string payload)
    {
        var validated = validator.Validate(payload, concepts, evidenceIds);
        return new CandidateFixture(
            validated.Id,
            validated.SemanticId,
            new BusinessOntologyCandidate(
                validated.Id,
                validated.Kind,
                validated.SemanticId,
                validated.CanonicalPayloadJson,
                validated.Rationale,
                0.9,
                "pending",
                validated.EvidenceIds));
    }

    private static BusinessOntologyReviewEntry Review(
        CandidateFixture candidate,
        string decision,
        string reviewedAt) =>
        new(
            new BusinessOntologyReview(
                "review:" + candidate.Id["candidate:semantic:".Length..],
                candidate.Id,
                decision,
                "user:fixture",
                decision == "accepted" ? "确认业务语义。" : "拒绝提升。",
                reviewedAt),
            candidate.Candidate.EvidenceIds);

    private static BusinessOntologyGenerationInput Generation(
        string generationId,
        IReadOnlyList<BusinessOntologyCandidate> candidates)
    {
        var evidence = new[]
        {
            Evidence("evidence:record", "src/Record.java", "java:Record", "记录声明。"),
            Evidence("evidence:supplier", "src/Supplier.java", "java:Supplier", "供应商声明。"),
            Evidence("evidence:rule", "src/Record.java", "java:Record#recordCode", "记录编码验证约束。"),
            Evidence("evidence:states", "src/RecordStatus.java", "java:RecordStatus", "记录状态枚举。"),
            Evidence("evidence:transition", "src/RecordService.java", "java:RecordService#approve", "记录状态直接赋值。"),
            Evidence("evidence:pending", "src/Record.java", "java:Record#recordCode", "待审核唯一约束。"),
            Evidence("evidence:superseded", "src/Record.java", "java:Record#legacySupplier", "已替代供应商观察。"),
            Evidence("evidence:stale", "src/Record.java", "java:Record#category", "即将失效的分类观察。"),
        };
        return new BusinessOntologyGenerationInput(
            OntologyId,
            generationId,
            "fingerprint-" + generationId,
            "onto-semantic/1",
            "2026-07-18T07:50:00Z",
            [
                new BusinessOntologyConcept(
                    Record,
                    "record",
                    "记录",
                    "可管理记录。",
                    "accepted",
                    0.95,
                    ["evidence:record"]),
                new BusinessOntologyConcept(
                    Supplier,
                    "party",
                    "供应商",
                    "记录供应方。",
                    "accepted",
                    0.95,
                    ["evidence:supplier"]),
            ],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            evidence,
            candidates,
            [],
            []);
    }

    private static BusinessOntologyEvidence Evidence(
        string id,
        string path,
        string symbol,
        string summary) =>
        new(
            id,
            "is-record-new",
            path,
            symbol,
            1,
            10,
            "contractual",
            "tree-sitter-java",
            0.9,
            "code",
            summary);

    private sealed record CandidateFixture(
        string Id,
        string SemanticId,
        BusinessOntologyCandidate Candidate);

    private sealed record Fixture(
        BusinessOntologyGenerationInput InitialGeneration,
        BusinessOntologyGenerationInput ActiveGeneration,
        IReadOnlyList<BusinessOntologyReviewEntry> Reviews,
        CandidateFixture Relation,
        CandidateFixture Rule,
        CandidateFixture Lifecycle,
        CandidateFixture Rejected,
        CandidateFixture Pending,
        CandidateFixture Superseded,
        CandidateFixture Stale);
}
