using Cozo.DotNet.LlmWiki.Tools;
using System.Reflection;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologySemanticDomainWorkTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var limits = new BusinessOntologySemanticDomainWorkLimits(
            maxDomains: 3,
            maxCompletionsPerDomain: 1,
            maxInvestigationOperationsPerDomain: 1);
        var plan = BusinessOntologySemanticDomainWorkPlanner.Plan(
        [
            Charter("Records", "e:record-b", "e:record-a"),
            Charter("ItAsset.Inventory", "e:asset"),
            Charter("Records", "e:record-a", "e:record-c"),
        ],
        limits);
        assert(plan.Items.Select(item => item.DomainId).SequenceEqual(["ItAsset.Inventory", "Records"], StringComparer.Ordinal)
                && plan.Items[1].EvidenceIds.SequenceEqual(["e:record-a", "e:record-b", "e:record-c"], StringComparer.Ordinal),
            "planner should produce one deterministic, evidence-merged work item per business domain");

        assert(typeof(BusinessOntologySemanticDomainWorkPlanner.DomainWorkPlan)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .All(constructor => constructor.IsPrivate),
            "only the planner should be able to issue an executable domain work plan");
        var callerEvidence = new[] { "e:frozen" };
        var frozenPlan = BusinessOntologySemanticDomainWorkPlanner.Plan([Charter("Custody", callerEvidence)], limits);
        callerEvidence[0] = "e:mutated";
        var planItems = (IList<BusinessOntologySemanticDomainWorkItem>)frozenPlan.Items;
        var frozenEvidence = (IList<string>)frozenPlan.Items.Single().EvidenceIds;
        assert(frozenPlan.Items.Single().EvidenceIds.Single() == "e:frozen"
                && Throws<NotSupportedException>(() =>
                {
                    planItems.RemoveAt(0);
                    return new object();
                })
                && Throws<NotSupportedException>(() =>
                {
                    frozenEvidence[0] = "e:mutated-again";
                    return new object();
                }),
            "plans must defensively freeze both their item collection and nested evidence collection");
        assert(Throws<InvalidOperationException>(() => BusinessOntologySemanticDomainWorkPlanner.Plan(
            [Charter("Accounts", "e:account"), Charter("Records", "e:record"), Charter("Inventory", "e:inventory"), Charter("Orders", "e:order")],
            limits)),
            "planner should reject a configured domain cap before any effect can be invoked");

        var invalidLimits = new[]
        {
            new Func<BusinessOntologySemanticDomainWorkLimits>(() => new BusinessOntologySemanticDomainWorkLimits(maxDomains: 0)),
            new Func<BusinessOntologySemanticDomainWorkLimits>(() => new BusinessOntologySemanticDomainWorkLimits(maxDomains: 7)),
            new Func<BusinessOntologySemanticDomainWorkLimits>(() => new BusinessOntologySemanticDomainWorkLimits(maxCompletionsPerDomain: 5)),
            new Func<BusinessOntologySemanticDomainWorkLimits>(() => new BusinessOntologySemanticDomainWorkLimits(maxInvestigationOperationsPerDomain: 11)),
        };
        assert(invalidLimits.All(factory => Throws<ArgumentOutOfRangeException>(factory)),
            "callers cannot disable or raise the immutable local domain-work caps");

        var coordinator = new BusinessOntologySemanticDomainWorkCoordinator();
        var cache = new BusinessOntologySemanticDomainWorkCache<string>();
        var effects = new FakeEffects();
        var first = await coordinator.ExecuteAsync(plan, limits, Effects(plan, completionCount: 1, investigationCount: 1), cache, effects);
        assert(first.All(result => result is { Status: "completed", FromCache: false, Value: not null })
                && effects.CompletionCalls == 2
                && effects.InvestigationCalls == 2
                && cache.Count == 2,
            "the coordinator should pre-plan controlled effects and cache only fully completed domain outputs");

        var replay = await coordinator.ExecuteAsync(plan, limits, Effects(plan, completionCount: 1, investigationCount: 1), cache, effects);
        assert(replay.Select(result => result.Item.DomainId).SequenceEqual(["ItAsset.Inventory", "Records"], StringComparer.Ordinal)
                && replay.All(result => result is { Status: "cached", FromCache: true })
                && effects.CompletionCalls == 2
                && effects.InvestigationCalls == 2
                && cache.ReadCount == 4,
            "same normalized domain, evidence, effects plan, and limits should hit cache without calling an effect port");

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        var cancelledCachedReplay = await coordinator.ExecuteAsync(
            plan,
            limits,
            Effects(plan, completionCount: 1, investigationCount: 1),
            cache,
            effects,
            alreadyCancelled.Token);
        assert(cancelledCachedReplay.All(result => result is { Status: "cancelled", FromCache: false, Value: null })
                && effects.CompletionCalls == 2
                && effects.InvestigationCalls == 2
                && cache.Count == 2
                && cache.ReadCount == 4,
            "a cancelled request must not read cached values or invoke controlled effects");

        var changedLimits = new BusinessOntologySemanticDomainWorkLimits(
            maxDomains: 3,
            maxCompletionsPerDomain: 2,
            maxInvestigationOperationsPerDomain: 1);
        var limitsIsolated = await coordinator.ExecuteAsync(plan, changedLimits, Effects(plan, completionCount: 1, investigationCount: 1), cache, effects);
        var newlineEvidencePlan = BusinessOntologySemanticDomainWorkPlanner.Plan([Charter("Records", "e:record-a\ne:record-b")], limits);
        var splitEvidencePlan = BusinessOntologySemanticDomainWorkPlanner.Plan([Charter("Records", "e:record-a", "e:record-b")], limits);
        _ = await coordinator.ExecuteAsync(newlineEvidencePlan, limits, Effects(newlineEvidencePlan, 1, 0), cache, effects);
        _ = await coordinator.ExecuteAsync(splitEvidencePlan, limits, Effects(splitEvidencePlan, 1, 0), cache, effects);
        var changedCharterPlan = BusinessOntologySemanticDomainWorkPlanner.Plan(
            [new BusinessOntologySemanticDomainCharter("Records", "已变更名称", "已变更描述。", ["e:record-a", "e:record-b"], ["已变更工作流"])],
            limits);
        var changedCharter = await coordinator.ExecuteAsync(changedCharterPlan, limits, Effects(changedCharterPlan, 1, 0), cache, effects);
        assert(limitsIsolated.All(result => result.Status == "completed")
                && changedCharter.Single() is { Status: "completed", FromCache: false }
                && effects.CompletionCalls == 7
                && effects.InvestigationCalls == 4
                && cache.Count == 7,
            "structured cache identity must distinguish limits, delimiter-containing evidence, and full charter text/workflows");

        var overCompletionPlan = BusinessOntologySemanticDomainWorkPlanner.Plan([Charter("Notifications", "e:notification")], limits);
        var callsBeforeRejectedPlan = effects.TotalCalls;
        var overBudgetRejected = await ThrowsAsync<ArgumentOutOfRangeException>(() => coordinator.ExecuteAsync(
            overCompletionPlan,
            limits,
            Effects(overCompletionPlan, completionCount: 2, investigationCount: 0),
            cache,
            effects));
        var mixedBatch = BusinessOntologySemanticDomainWorkPlanner.Plan(
            [Charter("Accounts", "e:account"), Charter("Orders", "e:order")],
            limits);
        var mixedBatchRejected = await ThrowsAsync<ArgumentOutOfRangeException>(() => coordinator.ExecuteAsync(
            mixedBatch,
            limits,
            new Dictionary<string, BusinessOntologySemanticDomainWorkEffectsPlan>(StringComparer.Ordinal)
            {
                ["Accounts"] = EffectsPlan(1, 0),
                ["Orders"] = EffectsPlan(2, 0),
            },
            cache,
            effects));
        assert(overBudgetRejected
                && mixedBatchRejected
                && effects.TotalCalls == callsBeforeRejectedPlan,
            "all effects plans must be rejected as a batch before the controlled port receives any effect");

        var lowerDomainLimitRejected = await ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteAsync(
            plan,
            new BusinessOntologySemanticDomainWorkLimits(maxDomains: 1),
            Effects(plan, 1, 0),
            cache,
            effects));
        assert(lowerDomainLimitRejected,
            "execution must recheck the opaque plan against the active hard domain cap before any cache or effect access");

        var failurePlan = BusinessOntologySemanticDomainWorkPlanner.Plan([Charter("Workflows", "e:workflow")], limits);
        var failureEffects = new FakeEffects
        {
            Investigate = (_, _, _) => throw new InvalidOperationException("fixture failure"),
        };
        var failed = await coordinator.ExecuteAsync(failurePlan, limits, Effects(failurePlan, 1, 1), cache, failureEffects);
        var cancelledEffects = new FakeEffects
        {
            Investigate = (_, _, _) => throw new OperationCanceledException(),
        };
        var cancelled = await coordinator.ExecuteAsync(
            BusinessOntologySemanticDomainWorkPlanner.Plan([Charter("Policies", "e:policy")], limits),
            limits,
            EffectsFor("Policies", 1, 1),
            cache,
            cancelledEffects);
        var nullEffects = new FakeEffects
        {
            Investigate = (_, _, _) => Task.FromResult<string>(null!),
        };
        var nullOutput = await coordinator.ExecuteAsync(
            BusinessOntologySemanticDomainWorkPlanner.Plan([Charter("Audits", "e:audit")], limits),
            limits,
            EffectsFor("Audits", 1, 1),
            cache,
            nullEffects);
        assert(failed.Single() is { Status: "failed", Value: null }
                && cancelled.Single() is { Status: "cancelled", Value: null }
                && nullOutput.Single() is { Status: "failed", Value: null }
                && failureEffects is { CompletionCalls: 1, InvestigationCalls: 1 }
                && cancelledEffects is { CompletionCalls: 1, InvestigationCalls: 1 }
                && nullEffects is { CompletionCalls: 1, InvestigationCalls: 1 }
                && cache.Count == 7,
            "failed, cancelled, and null controlled effects must not expose partial output or populate cache");
    }

    private static BusinessOntologySemanticDomainWorkEffectsPlan EffectsPlan(int completionCount, int investigationCount) =>
        new(completionCount, investigationCount);

    private static IReadOnlyDictionary<string, BusinessOntologySemanticDomainWorkEffectsPlan> Effects(
        BusinessOntologySemanticDomainWorkPlanner.DomainWorkPlan plan,
        int completionCount,
        int investigationCount) =>
        plan.Items.ToDictionary(item => item.DomainId, _ => EffectsPlan(completionCount, investigationCount), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, BusinessOntologySemanticDomainWorkEffectsPlan> EffectsFor(
        string domainId,
        int completionCount,
        int investigationCount) =>
        new Dictionary<string, BusinessOntologySemanticDomainWorkEffectsPlan>(StringComparer.Ordinal)
        {
            [domainId] = EffectsPlan(completionCount, investigationCount),
        };

    private static BusinessOntologySemanticDomainCharter Charter(string domainId, params string[] evidenceIds) =>
        new(domainId, "测试业务域", "用于验证领域工作协调器。", evidenceIds, []);

    private static bool Throws<TException>(Func<object> action)
        where TException : Exception
    {
        try
        {
            _ = action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static async Task<bool> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private sealed class FakeEffects : IBusinessOntologySemanticDomainWorkEffectPort<string>
    {
        public Func<BusinessOntologySemanticDomainWorkItem, int, CancellationToken, Task<string>> Complete { get; init; } =
            (item, ordinal, _) => Task.FromResult($"complete:{item.DomainId}:{ordinal}");
        public Func<BusinessOntologySemanticDomainWorkItem, int, CancellationToken, Task<string>> Investigate { get; init; } =
            (item, ordinal, _) => Task.FromResult($"investigate:{item.DomainId}:{ordinal}");

        public int CompletionCalls { get; private set; }
        public int InvestigationCalls { get; private set; }
        public int TotalCalls => CompletionCalls + InvestigationCalls;

        public Task<string> CompleteAsync(BusinessOntologySemanticDomainWorkItem item, int ordinal, CancellationToken cancellationToken = default)
        {
            CompletionCalls++;
            return Complete(item, ordinal, cancellationToken);
        }

        public Task<string> InvestigateAsync(BusinessOntologySemanticDomainWorkItem item, int ordinal, CancellationToken cancellationToken = default)
        {
            InvestigationCalls++;
            return Investigate(item, ordinal, cancellationToken);
        }
    }
}
