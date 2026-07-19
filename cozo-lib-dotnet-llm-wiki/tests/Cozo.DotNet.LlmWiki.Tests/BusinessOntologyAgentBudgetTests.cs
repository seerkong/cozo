using System.Reflection;
using Cozo.DotNet.LlmWiki.LlmClient;
using Cozo.DotNet.LlmWiki.Tools;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyAgentBudgetTests
{
    public static Task RunAsync(Action<bool, string> assert)
    {
        var defaults = BusinessOntologyAgentBudgetLimits.Default;
        assert(defaults.MaxTurns == 12
                && defaults.MaxQueries == 8
                && defaults.MaxRows == 200
                && defaults.MaxSourceBytes == 32768
                && defaults.MaxInputTokens == 48000
                && defaults.MaxOutputTokens == 12000
                && defaults.MaxWallClock == TimeSpan.FromSeconds(180)
                && defaults.MaxConcurrency == 1,
            "agentic ontology budget defaults should match design.md");
        assert(GetSettableProperties(typeof(BusinessOntologyAgentBudgetLimits)).Length == 0,
            "budget limits should be immutable after construction");
        assert(GetSettableProperties(typeof(BusinessOntologyAgentBudgetState)).Length == 0,
            "budget state should be immutable and advanced by API calls only");

        var started = new DateTimeOffset(2026, 7, 19, 8, 0, 0, TimeSpan.Zero);
        var state = BusinessOntologyAgentBudgetState.Start(started);
        assert(BusinessOntologyAgentBudget.TryReserveQuery(
                defaults,
                state,
                started.AddSeconds(1),
                maxRows: 20,
                maxSourceBytes: 1024,
                out var queryReservation,
                out var queryRejection),
            "query budget should allow reservation before the tool effect");
        assert(queryRejection is null
                && queryReservation.State.AfterReservation.QueriesUsed == 1
                && queryReservation.State.AfterReservation.RowsRead == 20
                && queryReservation.State.AfterReservation.SourceBytesRead == 1024
                && queryReservation.State.AfterReservation.InFlightEffects == 1,
            "query reservation should account the worst-case rows/source bytes and occupy concurrency");
        var queryCommit = BusinessOntologyAgentBudget.CommitQueryResult(queryReservation, actualRows: 7, actualSourceBytes: 400);
        assert(queryCommit.Accepted
                && queryCommit.State.QueriesUsed == 1
                && queryCommit.State.RowsRead == 7
                && queryCommit.State.SourceBytesRead == 400
                && queryCommit.State.InFlightEffects == 0,
            "query commit should release concurrency and replace reserved worst-case usage with actual usage");

        var oneQueryLimit = new BusinessOntologyAgentBudgetLimits(maxQueries: 1);
        assert(!BusinessOntologyAgentBudget.TryReserveQuery(
                oneQueryLimit,
                queryCommit.State,
                started.AddSeconds(2),
                maxRows: 1,
                maxSourceBytes: 1,
                out _,
                out var rejectedQuery),
            "query reservation should reject before effect when the query budget is exhausted");
        assert(rejectedQuery is { Metric: BusinessOntologyAgentBudgetMetrics.Queries }
                && queryCommit.State.QueriesUsed == 1,
            "rejected reservation should name the exhausted metric without mutating the immutable state");

        var smallRows = new BusinessOntologyAgentBudgetLimits(maxRows: 8);
        assert(!BusinessOntologyAgentBudget.TryReserveQuery(
                smallRows,
                state,
                started.AddSeconds(3),
                maxRows: 9,
                maxSourceBytes: 1,
                out _,
                out var rowRejection),
            "query reservation should reject before effect when requested row capacity exceeds the budget");
        assert(rowRejection is { Metric: BusinessOntologyAgentBudgetMetrics.Rows },
            "row reservation rejection should be auditable by metric");

        assert(!BusinessOntologyAgentBudget.TryReserveTurn(
                defaults,
                BusinessOntologyAgentBudgetState.Accumulated(started, inFlightEffects: 1),
                started.AddSeconds(4),
                BusinessOntologyAgentTokenUsage.Estimated(inputTokens: 1, outputTokens: 1, audit: "unit test estimate"),
                out _,
                out var concurrencyRejection),
            "turn reservation should reject before model effect when concurrency is saturated");
        assert(concurrencyRejection is { Metric: BusinessOntologyAgentBudgetMetrics.Concurrency },
            "concurrency rejection should be explicit");

        assert(!BusinessOntologyAgentBudget.TryReserveTurn(
                defaults,
                state,
                started.AddSeconds(180),
                BusinessOntologyAgentTokenUsage.Estimated(inputTokens: 1, outputTokens: 1, audit: "unit test estimate"),
                out _,
                out var wallClockRejection),
            "turn reservation should reject before model effect when wall clock is exhausted");
        assert(wallClockRejection is { Metric: BusinessOntologyAgentBudgetMetrics.WallClock },
            "wall-clock rejection should be explicit");

        assert(BusinessOntologyAgentBudget.TryReserveTurn(
                defaults,
                state,
                started.AddSeconds(5),
                BusinessOntologyAgentTokenUsage.Estimated(inputTokens: 10, outputTokens: 6, audit: "preflight"),
                out var turnReservation,
                out _),
            "turn reservation should accept an explicit preflight token estimate");
        var providerCommit = BusinessOntologyAgentBudget.CommitTurnCompletion(
            turnReservation,
            BusinessOntologyAgentTokenUsage.FromProvider(new LlmUsage(PromptTokens: 12, CompletionTokens: 7)));
        assert(providerCommit.Accepted
                && providerCommit.State.TurnsUsed == 1
                && providerCommit.State.InputTokensUsed == 12
                && providerCommit.State.OutputTokensUsed == 7
                && providerCommit.State.InFlightEffects == 0
                && providerCommit.TokenUsage is { Source: BusinessOntologyAgentTokenUsageSources.Provider },
            "provider usage should replace preflight estimates when available");

        var estimatedUsage = BusinessOntologyAgentTokenUsage.FromProviderOrEstimate(
            usage: null,
            inputText: "123456789",
            outputText: "12345",
            auditLabel: "missing codex usage");
        assert(estimatedUsage is { InputTokens: 3, OutputTokens: 2, Source: BusinessOntologyAgentTokenUsageSources.Estimate }
                && estimatedUsage.Audit.Contains("missing codex usage", StringComparison.Ordinal)
                && estimatedUsage.Audit.Contains("charsPerToken=4", StringComparison.Ordinal),
            "missing provider usage should use an explicit auditable estimate");

        assert(BusinessOntologyAgentBudget.TryReserveTurn(
                new BusinessOntologyAgentBudgetLimits(maxInputTokens: 10, maxOutputTokens: 10),
                state,
                started.AddSeconds(6),
                BusinessOntologyAgentTokenUsage.Estimated(inputTokens: 1, outputTokens: 1, audit: "preflight"),
                out var underestimatedTurn,
                out _),
            "turn reservation should accept a conservative preflight estimate before model effect");
        var rejectedCommit = BusinessOntologyAgentBudget.CommitTurnCompletion(
            underestimatedTurn,
            BusinessOntologyAgentTokenUsage.Estimated(inputTokens: 20, outputTokens: 2, audit: "provider missing but output captured"));
        assert(!rejectedCommit.Accepted
                && rejectedCommit.Rejection is { Metric: BusinessOntologyAgentBudgetMetrics.InputTokens }
                && rejectedCommit.State.InputTokensUsed == 10
                && rejectedCommit.State.InFlightEffects == 0,
            "token reconciliation rejection should saturate the exhausted metric and release concurrency");
        assert(!BusinessOntologyAgentBudget.TryReserveTurn(
                new BusinessOntologyAgentBudgetLimits(maxInputTokens: 10, maxOutputTokens: 10),
                rejectedCommit.State,
                started.AddSeconds(7),
                BusinessOntologyAgentTokenUsage.Estimated(inputTokens: 1, outputTokens: 1, audit: "after rejected commit"),
                out _,
                out var afterRejectedCommit),
            "state returned from a token-overrun commit must reject further model effects");
        assert(afterRejectedCommit is { Metric: BusinessOntologyAgentBudgetMetrics.InputTokens },
            "post-overrun rejection should name the saturated token metric");

        var nearOverflow = BusinessOntologyAgentBudgetState.Accumulated(started, rowsRead: long.MaxValue - 3);
        assert(!BusinessOntologyAgentBudget.TryReserveQuery(
                new BusinessOntologyAgentBudgetLimits(maxRows: long.MaxValue),
                nearOverflow,
                started.AddSeconds(6),
                maxRows: 10,
                maxSourceBytes: 1,
                out _,
                out var overflowRejection),
            "budget arithmetic should reject overflowing counters instead of wrapping");
        assert(overflowRejection is { Metric: BusinessOntologyAgentBudgetMetrics.Rows }
                && nearOverflow.RowsRead == long.MaxValue - 3,
            "overflow rejection should preserve the previous immutable state");

        return Task.CompletedTask;
    }

    private static PropertyInfo[] GetSettableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is not null)
            .ToArray();
}
