using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.Contracts.Models;

internal static class ExistentialGovernanceParityFixtures
{
    public static async Task RunAsync()
    {
        using (var uninitialized = new CozoDb(engine: "mem", path: ""))
        {
            var om = new CozoOm(uninitialized);
            Require((await om.ListExistentialRulesAsync()).Count == 0
                    && (await om.CheckExistentialRulesAsync()).Count == 0
                    && (await om.ApplyExistentialRulesAsync()).Created.Count == 0,
                "existential list/check/apply must safely treat missing rule storage as empty");
        }

        using var db = new CozoDb(engine: "mem", path: "");
        var om2 = new CozoOm(db);
        await om2.InitSchemaAsync();
        await om2.DefineTypeAsync("ExistentialSource", "source");
        await om2.DefineTypeAsync("ExistentialTarget", "target");
        await om2.DefineAttributeAsync("ExistentialSource", "tier", OmValueType.Number);
        await om2.DefineRelationAsync("existential_rel", "ExistentialSource", "ExistentialTarget");

        await ExpectFailureAsync(() => om2.DefineExistentialRuleAsync(
            "invalid_where_operator",
            new ExistentialRuleSpec(
                new ExistentialForEachSpec("ExistentialSource",
                [new ExistentialWhereCondition("tier", "contains", JsonSerializer.SerializeToElement(1))]),
                new ExistentialExistsSpec("existential_rel", ExistentialDirection.Out, "ExistentialTarget"))),
            "invalid existential where operators must fail before persistence");
        Require((await om2.ListExistentialRulesAsync()).Count == 0,
            "rejected existential definitions must not persist a rule");

        await om2.DefineExistentialRuleAsync(
            "alias_runtime_rule",
            new ExistentialRuleSpec(
                new ExistentialForEachSpec("ExistentialSource",
                [new ExistentialWhereCondition("tier", ">=", JsonSerializer.SerializeToElement(2))]),
                new ExistentialExistsSpec("existential_rel", ExistentialDirection.Out, "ExistentialTarget")));
        await om2.DefineTypeAliasAsync("ExistentialSourceLegacy", "ExistentialSource");
        await om2.DefineRelationAliasAsync("existential_rel_legacy", "existential_rel");
        await om2.DefineAttributeAliasAsync("ExistentialSource", "tier_legacy", "tier");
        db.Run(
            """
            ?[rule_name, spec_json, mode, message, enabled] <- [[
              "alias_runtime_rule",
              "{\"forEach\":{\"type\":\"ExistentialSourceLegacy\",\"where\":[{\"attr\":\"tier_legacy\",\"op\":\">=\",\"value\":2}]},\"exists\":{\"rel\":\"existential_rel_legacy\",\"direction\":\"out\",\"toType\":\"ExistentialTarget\"}}",
              "check", "", true
            ]]
            :put om_existential_rule_def {rule_name => spec_json, mode, message, enabled}
            """);
        await om2.CreateEntityAsync("existential:source", "ExistentialSource", "source");
        await om2.SetPropertyAsync("existential:source", "tier", 2);

        var violations = await om2.CheckExistentialRulesAsync();
        Require(violations.Count == 1 && violations[0].EntityId == "existential:source",
            "stored existential rules must re-resolve aliases at evaluation time");

        await om2.DefineTypeAsync("ExistentialHistoricalSourceLegacy", "legacy source");
        await om2.DefineTypeAsync("ExistentialHistoricalTargetLegacy", "legacy target");
        await om2.DefineAttributeAsync("ExistentialHistoricalSourceLegacy", "tier_legacy", OmValueType.Number);
        await om2.DefineRelationAsync("existential_historical_rel_legacy", "ExistentialHistoricalSourceLegacy", "ExistentialHistoricalTargetLegacy");
        await om2.CreateEntityAsync("historical:source", "ExistentialHistoricalSourceLegacy", "legacy source");
        await om2.CreateEntityAsync("historical:target", "ExistentialHistoricalTargetLegacy", "legacy target");
        await om2.SetPropertyAsync("historical:source", "tier_legacy", 2);
        await om2.LinkEntitiesAsync("historical:source", "existential_historical_rel_legacy", "historical:target");
        await om2.CreateEntityAsync("historical:missing", "ExistentialHistoricalSourceLegacy", "legacy missing");
        await om2.SetPropertyAsync("historical:missing", "tier_legacy", 2);

        await om2.DefineTypeAsync("ExistentialHistoricalSource", "current source");
        await om2.DefineTypeAsync("ExistentialHistoricalTarget", "current target");
        await om2.DefineAttributeAsync("ExistentialHistoricalSource", "tier", OmValueType.Number);
        await om2.DefineRelationAsync("existential_historical_rel", "ExistentialHistoricalSource", "ExistentialHistoricalTarget");
        await om2.DefineTypeAliasAsync("ExistentialHistoricalSourceLegacy", "ExistentialHistoricalSource");
        await om2.DefineTypeAliasAsync("ExistentialHistoricalTargetLegacy", "ExistentialHistoricalTarget");
        await om2.DefineRelationAliasAsync("existential_historical_rel_legacy", "existential_historical_rel");
        await om2.DefineAttributeAliasAsync("ExistentialHistoricalSource", "tier_legacy", "tier");
        await om2.DefineExistentialRuleAsync(
            "historical_storage_rule",
            new ExistentialRuleSpec(
                new ExistentialForEachSpec("ExistentialHistoricalSource",
                [new ExistentialWhereCondition("tier", ">=", JsonSerializer.SerializeToElement(2))]),
                new ExistentialExistsSpec("existential_historical_rel", ExistentialDirection.Out, "ExistentialHistoricalTarget")));

        var historicalViolations = (await om2.CheckExistentialRulesAsync())
            .Where(v => v.Rule == "historical_storage_rule")
            .ToArray();
        Require(historicalViolations.Length == 1 && historicalViolations[0].EntityId == "historical:missing",
            "post-rename existential rules must read legacy entity, edge, target type, and property aliases deterministically");
    }

    private static async Task ExpectFailureAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (CozoException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
