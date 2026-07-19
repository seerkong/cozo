using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Core;
using Cozo.DotNet.LlmWiki.Indexing;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;
using Cozo.DotNet.Om.Support;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class JavaSourceSemanticClaimTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"java-source-claims-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await WriteFixtureAsync(root);
            var request = new RepositoryIndexRequest(
                root,
                RepositoryId: "repo:java-source-claims",
                RepositoryName: "java-source-claims",
                UseGitIgnore: false,
                EnableRoslynEnhancement: false);
            var indexer = new RepositoryIndexer();
            var first = await indexer.BuildBatchAsync(request);
            var second = await indexer.BuildBatchAsync(request);
            var claims = first.Batch.SemanticClaims ?? [];
            var diagnostics = first.Batch.Diagnostics ?? [];

            var kinds = claims.Select(claim => claim.Kind).ToHashSet(StringComparer.Ordinal);
            foreach (var kind in CodeSemanticClaimKinds.All)
            {
                assert(kinds.Contains(kind), $"Java semantic fixture should emit {kind}");
            }

            foreach (var claim in claims)
            {
                const string fileIdPrefix = "file:repo:java-source-claims:";
                var relativePath = claim.FileId.StartsWith(fileIdPrefix, StringComparison.Ordinal)
                    ? claim.FileId[fileIdPrefix.Length..]
                    : "";
                var sourcePath = Path.Combine(root, relativePath);
                var sourceLines = File.Exists(sourcePath)
                    ? await File.ReadAllLinesAsync(sourcePath)
                    : [];
                assert(relativePath.Length > 0
                        && !Path.IsPathRooted(relativePath)
                        && !claim.PayloadJson.Contains(root, StringComparison.Ordinal),
                    $"{claim.Kind} should use repository-relative source identity");
                assert(claim.StartLine > 0
                        && claim.EndLine >= claim.StartLine
                        && claim.EndLine <= sourceLines.Length
                        && sourceLines
                            .Skip(claim.StartLine - 1)
                            .Take(claim.EndLine - claim.StartLine + 1)
                            .Any(line => !string.IsNullOrWhiteSpace(line)),
                    $"{claim.Kind} should carry a direct, non-empty one-based range into its source file");
                assert(claim.Resolver is "treesitter" or "spring_annotation",
                    $"{claim.Kind} should use an allowed direct-source resolver");
                assert(claim.PayloadJson == CodeSemanticClaimIdentity.CanonicalizePayload(claim.PayloadJson),
                    $"{claim.Kind} payload should already be canonical");
                assert(claim.ClaimId == CodeSemanticClaimIdentity.Create(
                        claim.FileId, claim.Kind, claim.PayloadJson, claim.StartLine, claim.EndLine),
                    $"{claim.Kind} id should be reproducible from canonical source identity");
            }

            var firstStable = claims.Select(StableClaim).Order(StringComparer.Ordinal).ToArray();
            var secondStable = (second.Batch.SemanticClaims ?? [])
                .Select(StableClaim)
                .Order(StringComparer.Ordinal)
                .ToArray();
            assert(firstStable.SequenceEqual(secondStable, StringComparer.Ordinal),
                "repeated Java source derivation should produce identical claim ids and payloads");

            var typedReferences = Payloads(claims, CodeSemanticClaimKinds.TypedReference).ToArray();
            var typedMembers = typedReferences
                .Select(payload => $"{payload.GetProperty("usageKind").GetString()}:{payload.GetProperty("member").GetString()}")
                .Order(StringComparer.Ordinal)
                .ToArray();
            assert(typedMembers.SequenceEqual(new[]
                {
                    "field:explicitSupplier",
                    "field:partnerSupplier",
                    "field:supplier",
                    "field:supplier",
                    "field:suppliers",
                    "local:recordInfo",
                    "parameter:replacement",
                    "parameter:replacements",
                    "return:loadSupplier",
                }, StringComparer.Ordinal),
                "typed_reference should include field, DTO field, parameter, return, local variable, import/same-package/FQN/generic local carriers while excluding primitive, String, external, and unresolved types");
            foreach (var payload in typedReferences)
            {
                assert(payload.GetProperty("declaringSymbolId").GetString()?.Length > 0
                        && payload.GetProperty("ownerSymbolId").GetString()?.Length > 0
                        && payload.GetProperty("resolvedTypeSymbolId").GetString()?.Length > 0,
                    "typed_reference payload should retain declaringSymbolId/ownerSymbolId/resolvedTypeSymbolId");
            }

            var supplierTargets = typedReferences
                .Where(payload => payload.GetProperty("member").GetString() is "supplier" or "suppliers")
                .Select(payload => payload.GetProperty("resolvedTypeName").GetString())
                .ToArray();
            assert(supplierTargets.All(target => target == "SupplierEntity"),
                "same-package SupplierEntity references should resolve despite another package defining the same simple name");
            var partnerSupplier = typedReferences.Single(payload => payload.GetProperty("member").GetString() == "partnerSupplier");
            assert(partnerSupplier.GetProperty("resolvedTypeQualifiedName").GetString() == "fixture.partner.SupplierEntity",
                "FQN type text should resolve to the exact local type rather than the same-package same simple name");
            var explicitSupplier = typedReferences.Single(payload => payload.GetProperty("member").GetString() == "explicitSupplier");
            assert(explicitSupplier.GetProperty("resolvedTypeQualifiedName").GetString() == "fixture.record.SupplierEntity",
                "explicit imports should resolve duplicate simple names without silently dropping the claim");
            var suppliers = typedReferences.Single(payload => payload.GetProperty("member").GetString() == "suppliers");
            assert(suppliers.GetProperty("collection").GetBoolean(),
                "collection of a local carrier should retain collection=true");
            var replacementCollection = typedReferences.Single(payload => payload.GetProperty("member").GetString() == "replacements");
            assert(replacementCollection.GetProperty("collection").GetBoolean()
                    && replacementCollection.GetProperty("usageKind").GetString() == "parameter",
                "generic collection formal parameters should retain collection=true and resolve the element type");
            assert(diagnostics.Any(diagnostic => diagnostic.Kind == "semantic_typed_reference_ambiguous"
                    && diagnostic.Message.Contains("ambiguousSupplier", StringComparison.Ordinal)),
                "ambiguous duplicate simple-name references should produce a diagnostic and no typed_reference claim");

            var validationPayloads = Payloads(claims, CodeSemanticClaimKinds.ValidationConstraint).ToArray();
            var validationOperators = validationPayloads
                .Select(payload => payload.GetProperty("operator").GetString() ?? "")
                .ToHashSet(StringComparer.Ordinal);
            assert(new[] { "required", "minLength", "maxLength", "min", "max", "pattern" }
                    .All(validationOperators.Contains),
                "direct Bean Validation annotations should retain required/range/pattern operators");
            assert(!validationPayloads.Any(payload => payload.GetProperty("member").GetString() == "commentOnly"),
                "annotation-looking comments must not produce validation claims");

            var persistenceKinds = Payloads(claims, CodeSemanticClaimKinds.PersistenceConstraint)
                .Select(payload => payload.GetProperty("constraintKind").GetString() ?? "")
                .ToHashSet(StringComparer.Ordinal);
            assert(new[] { "association", "optional", "nullable", "unique" }.All(persistenceKinds.Contains),
                "direct persistence annotations should retain association/nullability/uniqueness");

            var stateFields = Payloads(claims, CodeSemanticClaimKinds.StateField).ToArray();
            assert(stateFields.Length == 1
                    && stateFields[0].GetProperty("member").GetString() == "status"
                    && stateFields[0].GetProperty("enumType").GetString() == "RecordStatus",
                "only enum-typed status should become a state_field; String status must stay excluded");
            var stateValues = Payloads(claims, CodeSemanticClaimKinds.StateValue)
                .Select(payload => payload.GetProperty("value").GetString() ?? "")
                .Order(StringComparer.Ordinal)
                .ToArray();
            assert(stateValues.SequenceEqual(new[] { "APPROVED", "DRAFT", "REJECTED" }, StringComparer.Ordinal),
                "referenced state enum constants should become finite state_value claims");
            var assignments = Payloads(claims, CodeSemanticClaimKinds.StateAssignment).ToArray();
            var assignmentTuples = assignments
                .Select(payload => (
                    Method: payload.GetProperty("method").GetString() ?? "",
                    Property: payload.GetProperty("field").GetString() ?? "",
                    From: payload.TryGetProperty("fromValue", out var from) && from.ValueKind == JsonValueKind.String ? from.GetString() ?? "" : "",
                    To: payload.GetProperty("toValue").GetString() ?? "",
                    Kind: payload.TryGetProperty("mutationKind", out var kind) ? kind.GetString() ?? "" : "",
                    Encoding: payload.TryGetProperty("valueEncoding", out var encoding) ? encoding.GetString() ?? "" : ""))
                .ToArray();
            var assignmentDiagnostics = assignmentTuples
                .Select(item => new
                {
                    item.Method,
                    item.Property,
                    item.From,
                    item.To,
                    item.Kind,
                    item.Encoding,
                })
                .ToArray();
            assert(assignments.Length == 8
                    && assignmentTuples.Any(item => item is { Method: "approve", Kind: "assignment", From: "DRAFT", To: "APPROVED" })
                    && assignmentTuples.Any(item => item is { Method: "reject", Kind: "assignment", To: "REJECTED" })
                    && assignmentTuples.Any(item => item is { Method: "approveByCodeSetter", Kind: "setter", From: "DRAFT", To: "APPROVED", Encoding: "enum.getCode" })
                    && assignmentTuples.Any(item => item is { Method: "approveByNameSetter", Kind: "setter", From: "DRAFT", To: "APPROVED", Encoding: "enum.name" })
                    && assignmentTuples.Any(item => item is { Method: "approveLocal", Kind: "setter", From: "DRAFT", To: "APPROVED", Encoding: "enum.member" })
                    && assignmentTuples.Any(item => item is { Method: "mutateBeforeGuard", Kind: "assignment", From: "", To: "APPROVED" })
                    && assignmentTuples.Any(item => item is { Method: "close", Kind: "setter", Property: "repairStatus", From: "OPEN", To: "CLOSED", Encoding: "string" })
                    && assignmentTuples.Any(item => item is { Method: "finishCode", Kind: "setter", Property: "freezeState", From: "CODE_1", To: "CODE_2", Encoding: "numeric" })
                    && !assignments.Any(payload => payload.GetProperty("method").GetString() is "approveWithoutAssignment" or "renameStatusLabel"),
                "state_assignment should derive fromValue only from a same-method rejecting guard that ends before the mutation. Actual: "
                + JsonSerializer.Serialize(assignmentDiagnostics));

            var transactions = Payloads(claims, CodeSemanticClaimKinds.TransactionScope)
                .Select(payload => payload.GetProperty("symbolName").GetString() ?? "")
                .ToHashSet(StringComparer.Ordinal);
            assert(transactions.SetEquals(new[] { "approve", "reject", "approveByCodeSetter", "approveByNameSetter", "approveLocal" }),
                "transaction_scope should be emitted only for directly annotated methods");
            var routes = Payloads(claims, CodeSemanticClaimKinds.RouteBinding).ToArray();
            assert(routes.Length == 1
                    && routes[0].GetProperty("site").GetString() == "RecordController.approve"
                    && routes[0].GetProperty("httpMethods").EnumerateArray().Single().GetString() == "POST"
                    && routes[0].GetProperty("paths").EnumerateArray().Single().GetString() == "/records/{id}/approve",
                "route_binding should retain normalized HTTP method/path from direct annotations");
            var guards = Payloads(claims, CodeSemanticClaimKinds.BusinessGuard).ToArray();
            var approveGuard = guards.Single(payload => payload.GetProperty("method").GetString() == "approve");
            assert(guards.Length == 8
                    && approveGuard.GetProperty("ownerSymbolId").GetString()?.Length > 0
                    && approveGuard.GetProperty("methodSymbolId").GetString()?.Length > 0
                    && approveGuard.GetProperty("predicateSource").GetString() == "status != RecordStatus.DRAFT"
                    && approveGuard.GetProperty("effectKind").GetString() == "throw"
                    && approveGuard.GetProperty("effectMessage").GetString() == "只有草稿记录可以审批",
                "business_guard should retain AST condition, enclosing method/owner, and explicit throw/error effect");
            assert(guards.Any(payload => payload.GetProperty("method").GetString() == "approveByCodeSetter"
                        && payload.GetProperty("stateGuard").GetProperty("allowedValues").EnumerateArray().Single().GetString() == "DRAFT"
                        && payload.GetProperty("stateGuard").GetProperty("valueEncoding").GetString() == "enum.getCode")
                    && guards.Any(payload => payload.GetProperty("method").GetString() == "approveByNameSetter"
                        && payload.GetProperty("stateGuard").GetProperty("valueEncoding").GetString() == "enum.name")
                    && guards.Any(payload => payload.GetProperty("method").GetString() == "close"
                        && payload.GetProperty("stateGuard").GetProperty("allowedValues").EnumerateArray().Single().GetString() == "OPEN")
                    && guards.Any(payload => payload.GetProperty("method").GetString() == "finishCode"
                        && payload.GetProperty("stateGuard").GetProperty("allowedValues").EnumerateArray().Single().GetString() == "CODE_1"),
                "business_guard should expose static allowed from-states for enum code/name, string, and numeric state predicates");
            assert(guards.Any(payload => payload.GetProperty("method").GetString() == "renameStatusLabel")
                    && !assignments.Any(payload => payload.GetProperty("method").GetString() == "renameStatusLabel"),
                "a setter whose lowerCamel property does not end in State or Status must remain a general guard and never become a state mutation");
            assert(!guards.Any(payload =>
                    payload.GetProperty("predicateSource").GetString()?.Contains("displayName", StringComparison.Ordinal) == true),
                "ordinary if branches without throw or error return must not produce business_guard claims");

            await ExpectPersistenceAndIncrementalFilteringAsync(root, request, claims.Count, assert);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort fixture cleanup.
            }
        }
    }

    private static IEnumerable<JsonElement> Payloads(
        IReadOnlyList<CodeSemanticClaimFact> claims,
        string kind)
    {
        foreach (var claim in claims.Where(claim => claim.Kind == kind))
        {
            using var document = JsonDocument.Parse(claim.PayloadJson);
            yield return document.RootElement.Clone();
        }
    }

    private static string StableClaim(CodeSemanticClaimFact claim) =>
        $"{claim.ClaimId}\n{claim.PayloadJson}\n{claim.Evidence}";

    private static async Task ExpectPersistenceAndIncrementalFilteringAsync(
        string root,
        RepositoryIndexRequest request,
        int expectedClaimCount,
        Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var innerStore = new CozoDbOmStore(db);
        await innerStore.RunAsync(":create depa_source_claim_sentinel {id => value}");
        await innerStore.RunAsync(
            """
            ?[id, value] <- [["sentinel", "unchanged"]]
            :put depa_source_claim_sentinel {id => value}
            """);
        var guardedStore = new ForbiddenRelationStore(innerStore, "depa_");
        var om = new CozoOm(guardedStore);
        var indexer = new RepositoryIndexer();
        var indexRequest = request with
        {
            ComputeCommunities = false,
            ExtractProcesses = false,
        };
        var first = await indexer.IndexAsync(om, indexRequest);
        var initial = await om.Runtime.Store.RunAsync(
            "?[kind, payload_json] := *ck_semantic_claim{ kind, payload_json }");
        assert(!first.IncrementalUsed && initial.Rows.Count == expectedClaimCount,
            "full indexing should persist every derived semantic claim");

        var controllerPath = Path.Combine(root, "RecordController.java");
        var source = await File.ReadAllTextAsync(controllerPath);
        await File.WriteAllTextAsync(controllerPath, source.Replace(
            "@PostMapping(\"/{id}/approve\")",
            "@PostMapping(\"/{id}/authorize\")",
            StringComparison.Ordinal));

        var second = await indexer.IndexAsync(om, indexRequest);
        var after = await om.Runtime.Store.RunAsync(
            "?[kind, payload_json] := *ck_semantic_claim{ kind, payload_json }");
        var payloads = after.Rows.Select(row => row[1].GetString() ?? "").ToArray();
        assert(second.IncrementalUsed && second.ChangedFiles == 1,
            "a one-file route edit should take the incremental filtered-batch path");
        assert(after.Rows.Count == expectedClaimCount
                && payloads.Any(payload => payload.Contains("/records/{id}/authorize", StringComparison.Ordinal))
                && !payloads.Any(payload => payload.Contains("/records/{id}/approve", StringComparison.Ordinal)),
            "incremental filtering should replace changed-file route claims without dropping unchanged-file claims");

        var controllerFile = await om.Runtime.Store.RunAsync(
            """
            ?[file_id] :=
              *ck_file{
                file_id,
                repo_id: "repo:java-source-claims",
                path: "RecordController.java"
              }
            """);
        assert(controllerFile.Rows.Count == 1,
            "the indexed real-source fixture should retain the controller file identity before deletion");
        var controllerFileId = controllerFile.Rows[0][0].GetString()!;

        File.Delete(controllerPath);
        var third = await indexer.IndexAsync(om, indexRequest);
        var deletedClaims = await om.Runtime.Store.RunAsync(
            """?[claim_id] := *ck_semantic_claim{claim_id, file_id: $file_id}""",
            new Dictionary<string, object?> { ["file_id"] = controllerFileId });
        var remainingRoutes = await om.Runtime.Store.RunAsync(
            """?[claim_id] := *ck_semantic_claim{claim_id, kind: "route_binding"}""");
        var deletedFile = await om.Runtime.Store.RunAsync(
            """?[file_id] := *ck_file{file_id}, file_id = $file_id""",
            new Dictionary<string, object?> { ["file_id"] = controllerFileId });
        assert(third.IncrementalUsed
               && third.RemovedFiles == 1
               && deletedClaims.Rows.Count == 0
               && remainingRoutes.Rows.Count == 0
               && deletedFile.Rows.Count == 0,
            "deleting a real source file should incrementally remove its semantic claims and ck_file row");

        var sentinel = await innerStore.RunAsync(
            """?[value] := *depa_source_claim_sentinel{id: "sentinel", value}""");
        assert(guardedStore.ForbiddenAccessCount == 0
               && sentinel.Rows.Count == 1
               && sentinel.Rows[0][0].GetString() == "unchanged",
            "source indexing, changed-file replacement, and deletion must neither query nor mutate depa_* data");
    }

    private static async Task WriteFixtureAsync(string root)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "SupplierEntity.java"), """
        package fixture.record;

        import jakarta.persistence.Entity;

        @Entity
        public class SupplierEntity {
            private String id;
        }
        """);
        Directory.CreateDirectory(Path.Combine(root, "partner"));
        await File.WriteAllTextAsync(Path.Combine(root, "partner", "SupplierEntity.java"), """
        package fixture.partner;

        public class SupplierEntity {
            private String id;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(root, "RecordStatus.java"), """
        package fixture.record;

        public enum RecordStatus {
            DRAFT,
            APPROVED,
            REJECTED
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(root, "RecordEntity.java"), """
        package fixture.record;

        import jakarta.persistence.Column;
        import jakarta.persistence.Entity;
        import jakarta.persistence.JoinColumn;
        import jakarta.persistence.ManyToOne;
        import jakarta.validation.constraints.Max;
        import jakarta.validation.constraints.Min;
        import jakarta.validation.constraints.NotNull;
        import jakarta.validation.constraints.Pattern;
        import jakarta.validation.constraints.Size;
        import java.util.List;
        import org.springframework.transaction.annotation.Transactional;

        @Entity
        public class RecordEntity {
            @NotNull
            @ManyToOne(optional = false)
            @JoinColumn(nullable = false, unique = true)
            private SupplierEntity supplier;

            private List<SupplierEntity> suppliers;
            private fixture.partner.SupplierEntity partnerSupplier;

            @Size(min = 2, max = 64)
            @Pattern(regexp = "[A-Z0-9-]+")
            @Column(nullable = false, unique = true)
            private String code;

            @Min(1)
            @Max(100)
            private int quantity;

            private RecordStatus status;
            private String displayName;
            private List<String> tags;
            private ExternalMoney price;

            /*
            @NotNull
            */
            private String commentOnly;

            @Transactional
            public void approve() {
                if (status != RecordStatus.DRAFT) {
                    throw new BusinessException("只有草稿记录可以审批");
                }
                if (displayName != null) {
                    displayName = displayName.trim();
                }
                status = RecordStatus.APPROVED;
            }

            @Transactional
            public void reject() {
                this.status = RecordStatus.REJECTED;
            }

            public void mutateBeforeGuard() {
                status = RecordStatus.APPROVED;
                if (status != RecordStatus.DRAFT) {
                    throw new BusinessException("后置拒绝条件不能证明变更前状态");
                }
            }

            @Transactional
            public void approveByCodeSetter() {
                if (!RecordStatus.DRAFT.getCode().equals(this.status.getCode())) {
                    throw new BusinessException("只有草稿记录可以编码审批");
                }
                setStatus(RecordStatus.APPROVED.getCode());
            }

            @Transactional
            public void approveByNameSetter() {
                if (!StringUtils.equals(this.status.name(), RecordStatus.DRAFT.name())) {
                    throw new BusinessException("只有草稿记录可以名称审批");
                }
                this.setStatus(RecordStatus.APPROVED.name());
            }

            public void approveWithoutAssignment() {
                displayName = "approved";
            }

            public void setStatus(RecordStatus status) {
                this.status = status;
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(root, "RepairTicket.java"), """
        package fixture.record;

        public class RepairTicket {
            private String repairStatus;
            private int freezeState;
            private String statusLabel;

            public void close() {
                if (!StringUtils.equals(repairStatus, "OPEN")) {
                    throw new BusinessException("只有打开的维修单可以关闭");
                }
                setRepairStatus("CLOSED");
            }

            public void finishCode() {
                if (getFreezeState() != 1) {
                    throw new BusinessException("只有状态码 1 可以完结");
                }
                this.setFreezeState(2);
            }

            public void renameStatusLabel() {
                if (!StringUtils.equals(statusLabel, "OLD")) {
                    throw new BusinessException("只有旧标签可以重命名");
                }
                setStatusLabel("NEW");
            }

            public void setRepairStatus(String repairStatus) {
                this.repairStatus = repairStatus;
            }

            public int getFreezeState() {
                return freezeState;
            }

            public void setFreezeState(int freezeState) {
                this.freezeState = freezeState;
            }

            public void setStatusLabel(String statusLabel) {
                this.statusLabel = statusLabel;
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(root, "RecordCreateDTO.java"), """
        package fixture.record;

        public class RecordCreateDTO {
            private SupplierEntity supplier;
            private String status;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(root, "ImportedRecord.java"), """
        package fixture.imported;

        import fixture.record.SupplierEntity;
        import java.util.List;

        public class ImportedRecord {
            private SupplierEntity explicitSupplier;
            private MissingSupplier missingSupplier;

            public SupplierEntity loadSupplier(SupplierEntity replacement, List<SupplierEntity> replacements) {
                return replacement;
            }

            public int primitiveReturn(String externalName) {
                return 0;
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(root, "RecordWorkflowService.java"), """
        package fixture.record;

        import org.springframework.transaction.annotation.Transactional;

        public class RecordWorkflowService {
            @Transactional
            public void approveLocal() {
                RecordEntity recordInfo = null;
                if (recordInfo.getStatus() != RecordStatus.DRAFT) {
                    throw new BusinessException("只有草稿记录可以局部审批");
                }
                recordInfo.setStatus(RecordStatus.APPROVED);
            }
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(root, "AmbiguousRecord.java"), """
        package fixture.ambiguous;

        public class AmbiguousRecord {
            private SupplierEntity ambiguousSupplier;
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(root, "RecordController.java"), """
        package fixture.record;

        import org.springframework.web.bind.annotation.PostMapping;
        import org.springframework.web.bind.annotation.RequestMapping;
        import org.springframework.web.bind.annotation.RestController;

        @RestController
        @RequestMapping("/records")
        public class RecordController {
            @PostMapping("/{id}/approve")
            public void approve() {
            }
        }
        """);
    }
}
