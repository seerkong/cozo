using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;

internal static class PermissionGovernanceParityFixtures
{
    public static async Task RunAsync()
    {
        HarnessDiagnostics.Start("permission governance fixture setup");
        using var db = new CozoDb(engine: "mem", path: "");
        var om = new CozoOm(db);
        await om.InitSchemaAsync();
        await om.DefineTypeAsync("PermissionUser", "Permission user");
        await om.DefineTypeAsync("PermissionAsset", "Permission asset");
        await om.DefineTypeAsync("PermissionBridge", "Permission bridge");
        await om.DefineRelationAsync("permission_owns", "PermissionUser", "PermissionAsset");
        await om.DefineRelationAsync("permission_delegates", "PermissionUser", "PermissionBridge");
        await om.DefineRelationAsync("permission_grants", "PermissionBridge", "PermissionAsset");
        await om.DefineRelationAliasAsync("permission_delegates_alias", "permission_delegates");
        await om.DefineAttributeAsync("PermissionUser", "role", OmValueType.String);
        await om.DefineAttributeAsync("PermissionUser", "clearance", OmValueType.Number);
        await om.DefineAttributeAsync("PermissionUser", "active", OmValueType.Bool);
        await om.DefineAttributeAsync("PermissionAsset", "classification", OmValueType.String);
        await om.CreateEntityAsync("permission:user", "PermissionUser", "Permission user");
        await om.CreateEntityAsync("permission:asset", "PermissionAsset", "Permission asset");
        await om.CreateEntityAsync("permission:bridge", "PermissionBridge", "Permission bridge");
        var schemaSnapshot = await om.WriteSchemaSnapshotAsync(1, "permission evaluator baseline");

        await om.SeedPermissionMetadataAsync(new PermissionSeedInput(
            Policies:
            [
                new PermissionPolicySeed("permission:unwitnessed", "allow", "unwitnessed", "PermissionAsset"),
                new PermissionPolicySeed("permission:witness", "allow", "witness", "PermissionAsset"),
                new PermissionPolicySeed("permission:temporal-graph", "allow", "temporal-graph", "PermissionAsset"),
                new PermissionPolicySeed("permission:allow:admin", "allow", "abac", "PermissionAsset"),
                new PermissionPolicySeed("permission:deny:low-clearance", "deny", "abac", "PermissionAsset"),
                new PermissionPolicySeed("permission:temporal-role", "allow", "temporal-role", "PermissionAsset"),
                new PermissionPolicySeed("permission:hide-secret", "allow", "hide", "PermissionAsset"),
                new PermissionPolicySeed("permission:hide-conditional", "allow", "hide-conditional", "PermissionAsset"),
                new PermissionPolicySeed("permission:hide-unmatched", "allow", "hide-unmatched", "PermissionAsset"),
                new PermissionPolicySeed("permission:hide-deny", "deny", "hide-deny", "PermissionAsset"),
                new PermissionPolicySeed("permission:hide-failed-abac", "allow", "hide-failed-abac", "PermissionAsset"),
                new PermissionPolicySeed("permission:hide-allowed", "allow", "hide-allowed", "PermissionAsset"),
                new PermissionPolicySeed("permission:entity-aware", "allow", "entity-aware", "PermissionAsset"),
                new PermissionPolicySeed("permission:missing-value", "allow", "missing-value", "PermissionAsset"),
                new PermissionPolicySeed("permission:invalid-path", "allow", "invalid-path", "PermissionAsset"),
                new PermissionPolicySeed("permission:malformed-path", "allow", "malformed-path", "PermissionAsset"),
                new PermissionPolicySeed("permission:invalid-operator", "allow", "invalid-operator", "PermissionAsset"),
                new PermissionPolicySeed("permission:invalid-reference", "allow", "invalid-reference", "PermissionAsset"),
                new PermissionPolicySeed("permission:arrow-alias", "allow", "arrow-alias", "PermissionAsset"),
                new PermissionPolicySeed("permission:wrong-direction", "allow", "wrong-direction", "PermissionAsset"),
                new PermissionPolicySeed("permission:empty-self", "allow", "empty-self", "PermissionUser"),
                new PermissionPolicySeed("permission:empty-other", "allow", "empty-other", "PermissionAsset"),
                new PermissionPolicySeed("permission:first-witness", "allow", "first-witness", "PermissionAsset"),
                new PermissionPolicySeed("permission:stable:z", "allow", "stable", "PermissionAsset"),
                new PermissionPolicySeed("permission:stable:a", "allow", "stable", "PermissionAsset"),
                new PermissionPolicySeed("permission:legacy-subject-project", "allow", "legacy-subject-project", "PermissionAsset"),
                new PermissionPolicySeed("permission:wildcard-action:allow", "allow", "*", "PermissionAsset"),
                new PermissionPolicySeed("permission:wildcard-action:deny", "deny", "*", "PermissionAsset"),
                new PermissionPolicySeed("permission:wildcard-action:explicit", "allow", "wildcard-action", "PermissionAsset"),
                new PermissionPolicySeed("permission:wildcard-resource:allow", "allow", "wildcard-resource-only", "*"),
                new PermissionPolicySeed("permission:wildcard-resource:deny", "deny", "wildcard-resource", "*"),
                new PermissionPolicySeed("permission:wildcard-resource:explicit", "allow", "wildcard-resource", "PermissionAsset"),
                new PermissionPolicySeed("permission:compat-subject-id", "allow", "compat-subject-id", "PermissionAsset"),
                new PermissionPolicySeed("permission:compat-action", "allow", "compat-action", "PermissionAsset"),
                new PermissionPolicySeed("permission:compat-resource-id", "allow", "compat-resource-id", "PermissionAsset"),
                new PermissionPolicySeed("permission:compat-resource-field", "allow", "compat-resource-field", "PermissionAsset"),
            ],
            AbacRules:
            [
                new PermissionAbacRuleSeed("permission:allow:admin", "subject.role", "==", "admin"),
                new PermissionAbacRuleSeed("permission:allow:admin", "subject.clearance", ">=", "5"),
                new PermissionAbacRuleSeed("permission:deny:low-clearance", "subject.clearance", "<", "5"),
                new PermissionAbacRuleSeed("permission:temporal-role", "subject.role", "==", "admin"),
                new PermissionAbacRuleSeed("permission:hide-secret", "field.secret", "hide", "true"),
                new PermissionAbacRuleSeed("permission:hide-conditional", "field.conditional", "hide", "true"),
                new PermissionAbacRuleSeed("permission:hide-conditional", "subject.role", "==", "\"admin\""),
                new PermissionAbacRuleSeed("permission:hide-unmatched", "field.unmatched", "hide", "true"),
                new PermissionAbacRuleSeed("permission:hide-deny", "field.denied", "hide", "true"),
                new PermissionAbacRuleSeed("permission:hide-failed-abac", "field.failed", "hide", "true"),
                new PermissionAbacRuleSeed("permission:hide-failed-abac", "subject.role", "==", "\"admin\""),
                new PermissionAbacRuleSeed("permission:hide-allowed", "field.allowed", "hide", "true"),
                new PermissionAbacRuleSeed("permission:entity-aware", "subject.type", "==", "\"PermissionUser\""),
                new PermissionAbacRuleSeed("permission:entity-aware", "subject.active", "==", "true"),
                new PermissionAbacRuleSeed("permission:entity-aware", "resource.classification", "==", "\"internal\""),
                new PermissionAbacRuleSeed("permission:missing-value", "subject.not_present", "==", "true"),
                new PermissionAbacRuleSeed("permission:invalid-operator", "subject.type", "contains", "PermissionUser"),
                new PermissionAbacRuleSeed("permission:invalid-reference", "subject.", "==", "PermissionUser"),
                new PermissionAbacRuleSeed("permission:compat-subject-id", "subject.id", "==", "permission:user"),
                new PermissionAbacRuleSeed("permission:compat-action", "action", "==", "compat-action"),
                new PermissionAbacRuleSeed("permission:compat-resource-id", "resource.id", "==", "permission:asset"),
                new PermissionAbacRuleSeed("permission:compat-resource-field", "resource.field", "==", "classification"),
            ],
            PathRules:
            [
                new PermissionPathRuleSeed("permission:unwitnessed", "permission_owns"),
                new PermissionPathRuleSeed("permission:witness", "[\"permission_owns\"]"),
                new PermissionPathRuleSeed("permission:temporal-graph", "permission_owns"),
                new PermissionPathRuleSeed("permission:allow:admin", "permission_owns"),
                new PermissionPathRuleSeed("permission:deny:low-clearance", "permission_owns"),
                new PermissionPathRuleSeed("permission:temporal-role", "permission_owns"),
                new PermissionPathRuleSeed("permission:hide-secret", "permission_owns"),
                new PermissionPathRuleSeed("permission:hide-conditional", "permission_owns"),
                new PermissionPathRuleSeed("permission:hide-unmatched", "permission_owns"),
                new PermissionPathRuleSeed("permission:hide-deny", "permission_owns"),
                new PermissionPathRuleSeed("permission:hide-failed-abac", "permission_owns"),
                new PermissionPathRuleSeed("permission:hide-allowed", "permission_owns"),
                new PermissionPathRuleSeed("permission:entity-aware", "permission_owns"),
                new PermissionPathRuleSeed("permission:missing-value", "permission_owns"),
                new PermissionPathRuleSeed("permission:invalid-path", "missing_relation"),
                new PermissionPathRuleSeed("permission:malformed-path", "permission_owns//permission_grants"),
                new PermissionPathRuleSeed("permission:invalid-operator", "permission_owns"),
                new PermissionPathRuleSeed("permission:invalid-reference", "permission_owns"),
                new PermissionPathRuleSeed("permission:arrow-alias", "permission_delegates_alias->permission_grants"),
                new PermissionPathRuleSeed("permission:wrong-direction", "permission_grants/permission_delegates"),
                new PermissionPathRuleSeed("permission:empty-self", "[]"),
                new PermissionPathRuleSeed("permission:empty-other", "[]"),
                new PermissionPathRuleSeed("permission:first-witness", "permission_delegates_alias->permission_grants"),
                new PermissionPathRuleSeed("permission:first-witness", "[\"permission_owns\"]"),
                new PermissionPathRuleSeed("permission:stable:z", "permission_owns"),
                new PermissionPathRuleSeed("permission:stable:a", "permission_owns"),
                new PermissionPathRuleSeed("permission:legacy-subject-project", "subject->project"),
                new PermissionPathRuleSeed("permission:wildcard-action:allow", "permission_owns"),
                new PermissionPathRuleSeed("permission:wildcard-action:deny", "permission_owns"),
                new PermissionPathRuleSeed("permission:wildcard-action:explicit", "permission_owns"),
                new PermissionPathRuleSeed("permission:wildcard-resource:allow", "permission_owns"),
                new PermissionPathRuleSeed("permission:wildcard-resource:deny", "permission_owns"),
                new PermissionPathRuleSeed("permission:wildcard-resource:explicit", "permission_owns"),
                new PermissionPathRuleSeed("permission:compat-subject-id", "permission_owns"),
                new PermissionPathRuleSeed("permission:compat-action", "permission_owns"),
                new PermissionPathRuleSeed("permission:compat-resource-id", "permission_owns"),
                new PermissionPathRuleSeed("permission:compat-resource-field", "permission_owns"),
            ]));

        var preservedSnapshot = await om.ReadSchemaSnapshotAsync(schemaSnapshot.Version);
        Require(preservedSnapshot?.Checksum == schemaSnapshot.Checksum,
            "permission seed and facade calls must preserve the schema snapshot surface");

        HarnessDiagnostics.Start("permission governance invalid-policy checks");
        var contractProjection = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "invalid-path", "permission:asset"));
        Require(!contractProjection.Allow,
            "typed permission result must not claim an invalid path policy allowed");
        Require(contractProjection.FieldVisibility.IsEmpty,
            "typed permission result must expose immutable field visibility even when no fields are hidden");
        Require(contractProjection.PolicyEvaluations.Length == 1
                && contractProjection.PolicyEvaluations[0].Witness.Status == PermissionEvaluationStatus.Invalid
                && contractProjection.PolicyEvaluations[0].Diagnostics.Contains("unknown_relation", StringComparer.Ordinal),
            "typed permission result must expose an invalid path diagnostic instead of treating it as an explanatory-only allow");
        Require(contractProjection.Explanation.TryGetProperty("evaluatedPolicies", out var evaluatedPolicies)
                && evaluatedPolicies.GetArrayLength() == contractProjection.PolicyEvaluations.Length
                && evaluatedPolicies[0].GetProperty("path").GetProperty("status").GetString() == "invalid"
                && contractProjection.Explanation.TryGetProperty("fieldVisibility", out var fieldVisibility)
                && fieldVisibility.ValueKind == JsonValueKind.Object,
            "permission explanation must remain compatible with the typed diagnostics projection");

        var malformedPath = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "malformed-path", "permission:asset"));
        Require(!malformedPath.Allow
                && malformedPath.PolicyEvaluations[0].Witness.Status == PermissionEvaluationStatus.Invalid
                && malformedPath.PolicyEvaluations[0].Diagnostics.Contains("malformed_path", StringComparer.Ordinal),
            "malformed path syntax must fail closed with the same typed diagnostic as the JSON explanation");

        var abacProjection = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "invalid-operator", "permission:asset"));
        Require(!abacProjection.Allow
                && abacProjection.PolicyEvaluations.Length == 1
                && abacProjection.PolicyEvaluations[0].AbacDiagnostics.Length == 1
                && abacProjection.PolicyEvaluations[0].AbacDiagnostics[0].Status == PermissionEvaluationStatus.Invalid,
            "typed permission result must expose an invalid ABAC predicate as fail closed");

        var malformedReference = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "invalid-reference", "permission:asset"));
        Require(!malformedReference.Allow
                && malformedReference.PolicyEvaluations.Length == 1
                && malformedReference.PolicyEvaluations[0].AbacDiagnostics[0].Detail == "malformed_reference",
            "typed permission result must expose malformed ABAC references as fail closed");

        var missingSubject = await om.CheckAccessAsync(new CheckAccessInput("permission:missing", "witness", "permission:asset", "2024-06-01T00:00:00Z"));
        Require(!missingSubject.Allow
                && missingSubject.Diagnostics.Contains("missing_subject", StringComparer.Ordinal)
                && missingSubject.Explanation.GetProperty("reason").GetString() == "missing_subject"
                && missingSubject.AsOf == "2024-06-01T00:00:00.000Z"
                && missingSubject.Explanation.GetProperty("asOf").GetString() == missingSubject.AsOf,
            "missing subject must fail closed with a typed diagnostic");

        var legacySubjectProject = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "legacy-subject-project", "permission:asset"));
        Require(!legacySubjectProject.Allow
                && legacySubjectProject.PolicyEvaluations.Single().Witness.Status == PermissionEvaluationStatus.Invalid
                && legacySubjectProject.PolicyEvaluations.Single().Diagnostics.Contains("unknown_relation", StringComparer.Ordinal),
            "legacy subject->project path text must not normalize into a permissive grant");

        var unwitnessed = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "unwitnessed", "permission:asset"));
        Require(!unwitnessed.Allow,
            $"strict permission fixture: an allow policy with no permission_owns witness must deny; current result={unwitnessed.Allow}, explanation={unwitnessed.Explanation.GetRawText()}");
        var unmatchedHide = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "hide-unmatched", "permission:asset"));
        Require(!unmatchedHide.Allow
                && !unmatchedHide.FieldVisibility.ContainsKey("unmatched")
                && !FieldIsHidden(unmatchedHide.Explanation, "unmatched"),
            "an unmatched policy must not project a field hide result");

        await om.LinkEntitiesAsync("permission:user", "permission_owns", "permission:asset", options: new WriteOptions(ValidTime: "2024-01-01T00:00:00Z"));
        HarnessDiagnostics.Start("permission governance witness and empty-path checks");
        var witnessed = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "witness", "permission:asset"));
        Require(witnessed.Allow, "strict permission fixture: a directed permission_owns witness must allow");
        Require(ContainsWitnessHop(witnessed.Explanation, "permission:user", "permission_owns", "permission:asset"),
            $"strict permission fixture: a successful policy must explain its canonical witness hop; explanation={witnessed.Explanation.GetRawText()}");
        Require(witnessed.PolicyEvaluations[0].Witness.Status == PermissionEvaluationStatus.Matched
                && witnessed.PolicyEvaluations[0].Witness.Hops.Length == 1,
            "typed permission witness must agree with the explained canonical hop");

        await om.LinkEntitiesAsync("permission:user", "permission_delegates", "permission:bridge");
        await om.LinkEntitiesAsync("permission:bridge", "permission_grants", "permission:asset");
        var arrowAlias = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "arrow-alias", "permission:asset"));
        var wrongDirection = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "wrong-direction", "permission:asset"));
        var emptySelf = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "empty-self", "permission:user"));
        var emptyOther = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "empty-other", "permission:asset"));
        Require(arrowAlias.Allow
                && ContainsWitnessHop(arrowAlias.Explanation, "permission:user", "permission_delegates", "permission:bridge")
                && !wrongDirection.Allow
                && emptySelf.Allow
                && !emptyOther.Allow,
            $"strict permission fixture: aliases, arrow paths, direction, and empty paths must be fail closed; arrow={arrowAlias.Allow}, reverse={wrongDirection.Allow}, self={emptySelf.Allow}, other={emptyOther.Allow}");

        var firstWitness = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "first-witness", "permission:asset"));
        var repeatedWitness = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "first-witness", "permission:asset"));
        Require(firstWitness.Allow
                && firstWitness.PolicyEvaluations[0].Witness.Hops.Length == 1
                && firstWitness.PolicyEvaluations[0].Witness.Hops[0].RelName == "permission_owns"
                && firstWitness.Explanation.GetRawText() == repeatedWitness.Explanation.GetRawText(),
            "the shortest canonical complete witness must be selected deterministically");

        await om.UnlinkEntitiesAsync("permission:user", "permission_owns", "permission:asset", new WriteOptions(ValidTime: "2025-01-01T00:00:00Z"));
        var historicalGraph = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "temporal-graph", "permission:asset", "2024-06-01T00:00:00Z"));
        var retractedGraph = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "temporal-graph", "permission:asset", "2025-06-01T00:00:00Z"));
        Require(historicalGraph.Allow && !retractedGraph.Allow,
            $"strict permission fixture: AsOf graph witness must allow before retract and deny after it; historical={historicalGraph.Allow}, retracted={retractedGraph.Allow}");

        await om.LinkEntitiesAsync("permission:user", "permission_owns", "permission:asset", options: new WriteOptions(ValidTime: "2026-01-01T00:00:00Z"));
        await om.SetPropertyAsync("permission:user", "role", "admin", new WriteOptions(ValidTime: "2024-01-01T00:00:00Z"));
        await om.SetPropertyAsync("permission:user", "clearance", 7);
        await om.SetPropertyAsync("permission:user", "active", true);
        await om.SetPropertyAsync("permission:asset", "classification", "internal");
        var highClearance = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "abac", "permission:asset"));
        Require(highClearance.Allow, "strict permission fixture: witnessed subject.role == admin and clearance >= 5 must allow");
        var entityAware = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "entity-aware", "permission:asset"));
        Require(entityAware.Allow,
            "strict permission fixture: subject/resource type and property references with JSON literals must allow");
        var missingValue = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "missing-value", "permission:asset"));
        Require(!missingValue.Allow
                && missingValue.PolicyEvaluations.Single().AbacDiagnostics.Single().Detail == "missing_value",
            "strict permission fixture: a missing ABAC property must fail closed with deterministic detail");
        await om.SetPropertyAsync("permission:user", "clearance", 3);
        var lowClearance = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "abac", "permission:asset"));
        Require(!lowClearance.Allow,
            $"strict permission fixture: a matching witnessed deny policy must override allow; explanation={lowClearance.Explanation.GetRawText()}");

        await om.SetPropertyAsync("permission:user", "role", "viewer", new WriteOptions(ValidTime: "2025-01-01T00:00:00Z"));
        var historicalRole = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "temporal-role", "permission:asset", "2024-06-01T00:00:00Z"));
        var currentRole = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "temporal-role", "permission:asset", "2025-06-01T00:00:00Z"));
        Require(historicalRole.Allow && !currentRole.Allow,
            $"strict permission fixture: AsOf ABAC property resolution must use the same time as the graph; historical={historicalRole.Allow}, current={currentRole.Allow}");
        Require(historicalRole.AsOf == "2024-06-01T00:00:00.000Z"
                && historicalRole.Explanation.GetProperty("asOf").GetString() == historicalRole.AsOf,
            "typed and JSON permission explanations must expose the same normalized AsOf");

        var wildcardActionOnly = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "wildcard-action-only", "permission:asset"));
        var wildcardActionWithExplicitAllow = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "wildcard-action", "permission:asset"));
        var wildcardResourceOnly = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "wildcard-resource-only", "permission:asset"));
        var wildcardResourceWithExplicitAllow = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "wildcard-resource", "permission:asset"));
        Require(!wildcardActionOnly.Allow
                && wildcardActionWithExplicitAllow.Allow
                && !wildcardResourceOnly.Allow
                && wildcardResourceWithExplicitAllow.Allow,
            "wildcard action/resource policies must neither grant access nor deny an explicit matching allow");

        HarnessDiagnostics.Start("permission governance field-hide gating checks");
        var fieldHide = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "hide", "permission:asset"));
        Require(fieldHide.Allow
                && fieldHide.FieldVisibility.TryGetValue("secret", out var secretVisibility)
                && secretVisibility == PermissionFieldVisibility.Hidden
                && FieldIsHidden(fieldHide.Explanation, "secret"),
            $"strict permission fixture: a matched field.secret hide rule must preserve allow and mark secret hidden; explanation={fieldHide.Explanation.GetRawText()}");
        var conditionalHide = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "hide-conditional", "permission:asset"));
        Require(!conditionalHide.Allow
                && !conditionalHide.FieldVisibility.ContainsKey("conditional")
                && !FieldIsHidden(conditionalHide.Explanation, "conditional"),
            "strict permission fixture: a hide result must not apply when a non-hide ABAC predicate rejects the policy");
        var denyHide = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "hide-deny", "permission:asset"));
        var failedAbacHide = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "hide-failed-abac", "permission:asset"));
        var allowedHide = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "hide-allowed", "permission:asset"));
        Require(!denyHide.Allow
                && !denyHide.FieldVisibility.ContainsKey("denied")
                && !FieldIsHidden(denyHide.Explanation, "denied")
                && !failedAbacHide.Allow
                && !failedAbacHide.FieldVisibility.ContainsKey("failed")
                && !FieldIsHidden(failedAbacHide.Explanation, "failed")
                && allowedHide.Allow
                && allowedHide.FieldVisibility.TryGetValue("allowed", out var allowedVisibility)
                && allowedVisibility == PermissionFieldVisibility.Hidden
                && FieldIsHidden(allowedHide.Explanation, "allowed"),
            "only a matched allow policy may project a field hide result");

        var invalidPath = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "invalid-path", "permission:asset"));
        var invalidOperator = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "invalid-operator", "permission:asset"));
        var invalidReference = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "invalid-reference", "permission:asset"));
        Require(!invalidPath.Allow && !invalidOperator.Allow && !invalidReference.Allow,
            $"strict permission fixture: invalid path/operator/reference must all fail closed; path={invalidPath.Allow}, operator={invalidOperator.Allow}, reference={invalidReference.Allow}");

        var prohibitedReferences = new[]
        {
            await om.CheckAccessAsync(new CheckAccessInput("permission:user", "compat-subject-id", "permission:asset")),
            await om.CheckAccessAsync(new CheckAccessInput("permission:user", "compat-action", "permission:asset")),
            await om.CheckAccessAsync(new CheckAccessInput("permission:user", "compat-resource-id", "permission:asset")),
            await om.CheckAccessAsync(new CheckAccessInput("permission:user", "compat-resource-field", "permission:asset", FieldName: "classification")),
        };
        Require(prohibitedReferences.All(result => !result.Allow
                && result.PolicyEvaluations.Single().AbacDiagnostics.Single().Status == PermissionEvaluationStatus.Invalid
                && result.PolicyEvaluations.Single().AbacDiagnostics.Single().Detail == "malformed_reference"
                && result.Explanation.GetRawText().Contains("\"detail\":\"malformed_reference\"", StringComparison.Ordinal)),
            "subject.id/action/resource.id/resource.field compatibility references must fail closed with matching typed and JSON malformed_reference diagnostics");

        var stableFirst = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "stable", "permission:asset"));
        var stableSecond = await om.CheckAccessAsync(new CheckAccessInput("permission:user", "stable", "permission:asset"));
        Require(PolicyIds(stableFirst.Explanation).SequenceEqual(["permission:stable:a", "permission:stable:z"], StringComparer.Ordinal)
                && stableFirst.Explanation.GetRawText() == stableSecond.Explanation.GetRawText(),
            $"strict permission fixture: explanation policy order must be stable and lexical; first={stableFirst.Explanation.GetRawText()}, second={stableSecond.Explanation.GetRawText()}");

        HarnessDiagnostics.Start("permission governance cancellation check");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await RequireCanceledAsync(
            async () => _ = await om.CheckAccessAsync(
                new CheckAccessInput("permission:user", "witness", "permission:asset"),
                cancellation.Token),
            "CheckAccessAsync must preserve cancellation through the typed facade");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task RequireCanceledAsync(Func<Task> operation, string message)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static bool ContainsWitnessHop(JsonElement explanation, string fromId, string relName, string toId) =>
        explanation.TryGetProperty("evaluatedPolicies", out var policies)
        && policies.EnumerateArray().Any(policy =>
            policy.TryGetProperty("path", out var path)
            && path.TryGetProperty("witness", out var witness)
            && witness.ValueKind == JsonValueKind.Array
            && witness.EnumerateArray().Any(hop =>
                hop.TryGetProperty("fromId", out var from) && from.GetString() == fromId
                && hop.TryGetProperty("relName", out var relation) && relation.GetString() == relName
                && hop.TryGetProperty("toId", out var to) && to.GetString() == toId));

    private static bool FieldIsHidden(JsonElement explanation, string field) =>
        explanation.TryGetProperty("fieldVisibility", out var visibility)
        && visibility.TryGetProperty(field, out var value)
        && value.GetString() == "hidden";

    private static IEnumerable<string> PolicyIds(JsonElement explanation)
    {
        if (!explanation.TryGetProperty("matchedPolicies", out var policies)) yield break;
        foreach (var policy in policies.EnumerateArray())
        {
            if (policy.TryGetProperty("policyId", out var policyId) && policyId.GetString() is { Length: > 0 } value)
            {
                yield return value;
            }
        }
    }
}
