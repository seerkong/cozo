using System.Text;
using Cozo.DotNet.LlmWiki.LlmClient;

namespace Cozo.DotNet.LlmWiki.Tools;

internal static class BusinessOntologyAgentBudgetMetrics
{
    public const string Turns = "turns";
    public const string Queries = "queries";
    public const string Rows = "rows";
    public const string SourceBytes = "source_bytes";
    public const string InputTokens = "input_tokens";
    public const string OutputTokens = "output_tokens";
    public const string WallClock = "wall_clock";
    public const string Concurrency = "concurrency";
}

internal static class BusinessOntologyAgentTokenUsageSources
{
    public const string Provider = "provider";
    public const string Estimate = "estimate";
}

internal sealed class BusinessOntologyAgentBudgetLimits
{
    public static BusinessOntologyAgentBudgetLimits Default { get; } = new();

    public BusinessOntologyAgentBudgetLimits(
        long maxTurns = 12,
        long maxQueries = 8,
        long maxRows = 200,
        long maxSourceBytes = 32768,
        long maxInputTokens = 48000,
        long maxOutputTokens = 12000,
        TimeSpan? maxWallClock = null,
        long maxConcurrency = 1)
    {
        MaxTurns = Positive(maxTurns, nameof(maxTurns));
        MaxQueries = Positive(maxQueries, nameof(maxQueries));
        MaxRows = Positive(maxRows, nameof(maxRows));
        MaxSourceBytes = Positive(maxSourceBytes, nameof(maxSourceBytes));
        MaxInputTokens = Positive(maxInputTokens, nameof(maxInputTokens));
        MaxOutputTokens = Positive(maxOutputTokens, nameof(maxOutputTokens));
        MaxWallClock = maxWallClock ?? TimeSpan.FromSeconds(180);
        if (MaxWallClock <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWallClock), "Wall-clock budget must be positive.");
        }
        MaxConcurrency = Positive(maxConcurrency, nameof(maxConcurrency));
    }

    public long MaxTurns { get; }
    public long MaxQueries { get; }
    public long MaxRows { get; }
    public long MaxSourceBytes { get; }
    public long MaxInputTokens { get; }
    public long MaxOutputTokens { get; }
    public TimeSpan MaxWallClock { get; }
    public long MaxConcurrency { get; }

    private static long Positive(long value, string name) =>
        value > 0 ? value : throw new ArgumentOutOfRangeException(name, "Budget limit must be positive.");
}

internal sealed class BusinessOntologyAgentBudgetState
{
    private BusinessOntologyAgentBudgetState(
        DateTimeOffset startedAtUtc,
        long turnsUsed,
        long queriesUsed,
        long rowsRead,
        long sourceBytesRead,
        long inputTokensUsed,
        long outputTokensUsed,
        long inFlightEffects)
    {
        StartedAtUtc = startedAtUtc;
        TurnsUsed = NonNegative(turnsUsed, nameof(turnsUsed));
        QueriesUsed = NonNegative(queriesUsed, nameof(queriesUsed));
        RowsRead = NonNegative(rowsRead, nameof(rowsRead));
        SourceBytesRead = NonNegative(sourceBytesRead, nameof(sourceBytesRead));
        InputTokensUsed = NonNegative(inputTokensUsed, nameof(inputTokensUsed));
        OutputTokensUsed = NonNegative(outputTokensUsed, nameof(outputTokensUsed));
        InFlightEffects = NonNegative(inFlightEffects, nameof(inFlightEffects));
    }

    public DateTimeOffset StartedAtUtc { get; }
    public long TurnsUsed { get; }
    public long QueriesUsed { get; }
    public long RowsRead { get; }
    public long SourceBytesRead { get; }
    public long InputTokensUsed { get; }
    public long OutputTokensUsed { get; }
    public long InFlightEffects { get; }

    public static BusinessOntologyAgentBudgetState Start(DateTimeOffset startedAtUtc) =>
        Accumulated(startedAtUtc);

    public static BusinessOntologyAgentBudgetState Accumulated(
        DateTimeOffset startedAtUtc,
        long turnsUsed = 0,
        long queriesUsed = 0,
        long rowsRead = 0,
        long sourceBytesRead = 0,
        long inputTokensUsed = 0,
        long outputTokensUsed = 0,
        long inFlightEffects = 0) =>
        new(
            startedAtUtc,
            turnsUsed,
            queriesUsed,
            rowsRead,
            sourceBytesRead,
            inputTokensUsed,
            outputTokensUsed,
            inFlightEffects);

    internal BusinessOntologyAgentBudgetState Advance(
        long turnsUsed,
        long queriesUsed,
        long rowsRead,
        long sourceBytesRead,
        long inputTokensUsed,
        long outputTokensUsed,
        long inFlightEffects) =>
        new(
            StartedAtUtc,
            turnsUsed,
            queriesUsed,
            rowsRead,
            sourceBytesRead,
            inputTokensUsed,
            outputTokensUsed,
            inFlightEffects);

    private static long NonNegative(long value, string name) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(name, "Budget counter must not be negative.");
}

internal sealed record BusinessOntologyAgentTokenUsage(
    long InputTokens,
    long OutputTokens,
    string Source,
    string Audit)
{
    private const int EstimateCharsPerToken = 4;

    public static BusinessOntologyAgentTokenUsage Estimated(long inputTokens, long outputTokens, string audit) =>
        new(NonNegative(inputTokens, nameof(inputTokens)), NonNegative(outputTokens, nameof(outputTokens)),
            BusinessOntologyAgentTokenUsageSources.Estimate,
            string.IsNullOrWhiteSpace(audit) ? "explicit estimate" : audit);

    public static BusinessOntologyAgentTokenUsage FromProvider(LlmUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.PromptTokens is not { } inputTokens || usage.CompletionTokens is not { } outputTokens)
        {
            throw new ArgumentException("Provider usage must include prompt and completion tokens.", nameof(usage));
        }
        return new BusinessOntologyAgentTokenUsage(
            NonNegative(inputTokens, nameof(usage.PromptTokens)),
            NonNegative(outputTokens, nameof(usage.CompletionTokens)),
            BusinessOntologyAgentTokenUsageSources.Provider,
            "provider reported prompt/completion token usage");
    }

    public static BusinessOntologyAgentTokenUsage FromProviderOrEstimate(
        LlmUsage? usage,
        string inputText,
        string outputText,
        string auditLabel)
    {
        if (usage?.PromptTokens is { } inputTokens && usage.CompletionTokens is { } outputTokens)
        {
            return FromProvider(usage);
        }

        ArgumentNullException.ThrowIfNull(inputText);
        ArgumentNullException.ThrowIfNull(outputText);
        var inputBytes = Encoding.UTF8.GetByteCount(inputText);
        var outputBytes = Encoding.UTF8.GetByteCount(outputText);
        return new BusinessOntologyAgentTokenUsage(
            EstimateFromUtf8Bytes(inputBytes),
            EstimateFromUtf8Bytes(outputBytes),
            BusinessOntologyAgentTokenUsageSources.Estimate,
            $"{auditLabel}; providerUsage=missing; estimate=utf8Bytes/charsPerToken={EstimateCharsPerToken}; inputUtf8Bytes={inputBytes}; outputUtf8Bytes={outputBytes}");
    }

    private static long EstimateFromUtf8Bytes(int bytes) =>
        bytes == 0 ? 0 : (bytes + EstimateCharsPerToken - 1L) / EstimateCharsPerToken;

    private static long NonNegative(long value, string name) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(name, "Token usage must not be negative.");
}

internal sealed record BusinessOntologyAgentBudgetRejection(
    string Metric,
    long Limit,
    long Used,
    long Requested,
    string Audit);

internal sealed record BusinessOntologyAgentBudgetReservationState(
    BusinessOntologyAgentBudgetState BeforeReservation,
    BusinessOntologyAgentBudgetState AfterReservation);

internal sealed record BusinessOntologyAgentBudgetReservation(
    string Kind,
    BusinessOntologyAgentBudgetLimits Limits,
    BusinessOntologyAgentBudgetReservationState State,
    long ReservedRows,
    long ReservedSourceBytes,
    long ReservedInputTokens,
    long ReservedOutputTokens);

internal sealed record BusinessOntologyAgentBudgetCommitResult(
    bool Accepted,
    BusinessOntologyAgentBudgetState State,
    BusinessOntologyAgentBudgetRejection? Rejection,
    BusinessOntologyAgentTokenUsage? TokenUsage = null);

internal static class BusinessOntologyAgentBudget
{
    private const string QueryReservationKind = "query";
    private const string TurnReservationKind = "turn";

    public static bool TryReserveQuery(
        BusinessOntologyAgentBudgetLimits limits,
        BusinessOntologyAgentBudgetState state,
        DateTimeOffset nowUtc,
        long maxRows,
        long maxSourceBytes,
        out BusinessOntologyAgentBudgetReservation reservation,
        out BusinessOntologyAgentBudgetRejection? rejection)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(state);
        NonNegative(maxRows, nameof(maxRows));
        NonNegative(maxSourceBytes, nameof(maxSourceBytes));
        reservation = EmptyReservation(QueryReservationKind, limits, state);

        if (!TryCheckClockAndConcurrency(limits, state, nowUtc, out rejection)
            || !TryAddWithin(state.QueriesUsed, 1, limits.MaxQueries, BusinessOntologyAgentBudgetMetrics.Queries, out var queries, out rejection)
            || !TryAddWithin(state.RowsRead, maxRows, limits.MaxRows, BusinessOntologyAgentBudgetMetrics.Rows, out var rows, out rejection)
            || !TryAddWithin(
                state.SourceBytesRead,
                maxSourceBytes,
                limits.MaxSourceBytes,
                BusinessOntologyAgentBudgetMetrics.SourceBytes,
                out var sourceBytes,
                out rejection)
            || !TryAddWithin(state.InFlightEffects, 1, limits.MaxConcurrency, BusinessOntologyAgentBudgetMetrics.Concurrency, out var inFlight, out rejection))
        {
            return false;
        }

        var after = state.Advance(
            state.TurnsUsed,
            queries,
            rows,
            sourceBytes,
            state.InputTokensUsed,
            state.OutputTokensUsed,
            inFlight);
        reservation = new BusinessOntologyAgentBudgetReservation(
            QueryReservationKind,
            limits,
            new BusinessOntologyAgentBudgetReservationState(state, after),
            maxRows,
            maxSourceBytes,
            0,
            0);
        return true;
    }

    public static bool TryReserveTurn(
        BusinessOntologyAgentBudgetLimits limits,
        BusinessOntologyAgentBudgetState state,
        DateTimeOffset nowUtc,
        BusinessOntologyAgentTokenUsage preflightTokenUsage,
        out BusinessOntologyAgentBudgetReservation reservation,
        out BusinessOntologyAgentBudgetRejection? rejection)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(preflightTokenUsage);
        reservation = EmptyReservation(TurnReservationKind, limits, state);

        if (!TryCheckClockAndConcurrency(limits, state, nowUtc, out rejection)
            || !TryAddWithin(state.TurnsUsed, 1, limits.MaxTurns, BusinessOntologyAgentBudgetMetrics.Turns, out var turns, out rejection)
            || !TryAddWithin(
                state.InputTokensUsed,
                preflightTokenUsage.InputTokens,
                limits.MaxInputTokens,
                BusinessOntologyAgentBudgetMetrics.InputTokens,
                out var inputTokens,
                out rejection)
            || !TryAddWithin(
                state.OutputTokensUsed,
                preflightTokenUsage.OutputTokens,
                limits.MaxOutputTokens,
                BusinessOntologyAgentBudgetMetrics.OutputTokens,
                out var outputTokens,
                out rejection)
            || !TryAddWithin(state.InFlightEffects, 1, limits.MaxConcurrency, BusinessOntologyAgentBudgetMetrics.Concurrency, out var inFlight, out rejection))
        {
            return false;
        }

        var after = state.Advance(
            turns,
            state.QueriesUsed,
            state.RowsRead,
            state.SourceBytesRead,
            inputTokens,
            outputTokens,
            inFlight);
        reservation = new BusinessOntologyAgentBudgetReservation(
            TurnReservationKind,
            limits,
            new BusinessOntologyAgentBudgetReservationState(state, after),
            0,
            0,
            preflightTokenUsage.InputTokens,
            preflightTokenUsage.OutputTokens);
        return true;
    }

    public static BusinessOntologyAgentBudgetCommitResult CommitQueryResult(
        BusinessOntologyAgentBudgetReservation reservation,
        long actualRows,
        long actualSourceBytes)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        RequireKind(reservation, QueryReservationKind);
        NonNegative(actualRows, nameof(actualRows));
        NonNegative(actualSourceBytes, nameof(actualSourceBytes));

        if (actualRows > reservation.ReservedRows)
        {
            return RejectedCommit(
                reservation,
                BusinessOntologyAgentBudgetMetrics.Rows,
                reservation.ReservedRows,
                actualRows,
                "actual query rows exceeded reserved query capacity");
        }
        if (actualSourceBytes > reservation.ReservedSourceBytes)
        {
            return RejectedCommit(
                reservation,
                BusinessOntologyAgentBudgetMetrics.SourceBytes,
                reservation.ReservedSourceBytes,
                actualSourceBytes,
                "actual query source bytes exceeded reserved query capacity");
        }

        var after = reservation.State.AfterReservation;
        var state = after.Advance(
            after.TurnsUsed,
            after.QueriesUsed,
            after.RowsRead - reservation.ReservedRows + actualRows,
            after.SourceBytesRead - reservation.ReservedSourceBytes + actualSourceBytes,
            after.InputTokensUsed,
            after.OutputTokensUsed,
            after.InFlightEffects - 1);
        return new BusinessOntologyAgentBudgetCommitResult(true, state, null);
    }

    public static BusinessOntologyAgentBudgetCommitResult CommitTurnCompletion(
        BusinessOntologyAgentBudgetReservation reservation,
        BusinessOntologyAgentTokenUsage actualTokenUsage)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(actualTokenUsage);
        RequireKind(reservation, TurnReservationKind);

        var after = reservation.State.AfterReservation;
        if (!TryReconcileWithin(
                after.InputTokensUsed,
                reservation.ReservedInputTokens,
                actualTokenUsage.InputTokens,
                reservation.Limits.MaxInputTokens,
                BusinessOntologyAgentBudgetMetrics.InputTokens,
                out var inputTokens,
                out var rejection)
            || !TryReconcileWithin(
                after.OutputTokensUsed,
                reservation.ReservedOutputTokens,
                actualTokenUsage.OutputTokens,
                reservation.Limits.MaxOutputTokens,
                BusinessOntologyAgentBudgetMetrics.OutputTokens,
                out var outputTokens,
                out rejection))
        {
            return new BusinessOntologyAgentBudgetCommitResult(false, after.Advance(
                after.TurnsUsed,
                after.QueriesUsed,
                after.RowsRead,
                after.SourceBytesRead,
                SaturatingReconcile(
                    after.InputTokensUsed,
                    reservation.ReservedInputTokens,
                    actualTokenUsage.InputTokens,
                    reservation.Limits.MaxInputTokens),
                SaturatingReconcile(
                    after.OutputTokensUsed,
                    reservation.ReservedOutputTokens,
                    actualTokenUsage.OutputTokens,
                    reservation.Limits.MaxOutputTokens),
                Math.Max(0, after.InFlightEffects - 1)), rejection, actualTokenUsage);
        }

        var state = after.Advance(
            after.TurnsUsed,
            after.QueriesUsed,
            after.RowsRead,
            after.SourceBytesRead,
            inputTokens,
            outputTokens,
            after.InFlightEffects - 1);
        return new BusinessOntologyAgentBudgetCommitResult(true, state, null, actualTokenUsage);
    }

    private static bool TryCheckClockAndConcurrency(
        BusinessOntologyAgentBudgetLimits limits,
        BusinessOntologyAgentBudgetState state,
        DateTimeOffset nowUtc,
        out BusinessOntologyAgentBudgetRejection? rejection)
    {
        var elapsed = nowUtc < state.StartedAtUtc ? TimeSpan.Zero : nowUtc - state.StartedAtUtc;
        if (elapsed >= limits.MaxWallClock)
        {
            rejection = new BusinessOntologyAgentBudgetRejection(
                BusinessOntologyAgentBudgetMetrics.WallClock,
                (long)limits.MaxWallClock.TotalMilliseconds,
                (long)elapsed.TotalMilliseconds,
                1,
                "wall-clock budget exhausted before effect reservation");
            return false;
        }
        if (state.InFlightEffects >= limits.MaxConcurrency)
        {
            rejection = new BusinessOntologyAgentBudgetRejection(
                BusinessOntologyAgentBudgetMetrics.Concurrency,
                limits.MaxConcurrency,
                state.InFlightEffects,
                1,
                "concurrency budget saturated before effect reservation");
            return false;
        }
        rejection = null;
        return true;
    }

    private static bool TryAddWithin(
        long used,
        long requested,
        long limit,
        string metric,
        out long value,
        out BusinessOntologyAgentBudgetRejection? rejection)
    {
        NonNegative(requested, nameof(requested));
        try
        {
            value = checked(used + requested);
        }
        catch (OverflowException)
        {
            value = used;
            rejection = new BusinessOntologyAgentBudgetRejection(
                metric,
                limit,
                used,
                requested,
                "budget counter addition would overflow");
            return false;
        }

        if (value > limit)
        {
            rejection = new BusinessOntologyAgentBudgetRejection(
                metric,
                limit,
                used,
                requested,
                "budget would be exhausted by this reservation");
            return false;
        }

        rejection = null;
        return true;
    }

    private static bool TryReconcileWithin(
        long reservedTotal,
        long reservedAmount,
        long actualAmount,
        long limit,
        string metric,
        out long value,
        out BusinessOntologyAgentBudgetRejection? rejection)
    {
        try
        {
            value = checked(reservedTotal - reservedAmount + actualAmount);
        }
        catch (OverflowException)
        {
            value = reservedTotal;
            rejection = new BusinessOntologyAgentBudgetRejection(
                metric,
                limit,
                reservedTotal,
                actualAmount,
                "budget counter reconciliation would overflow");
            return false;
        }

        if (value > limit)
        {
            rejection = new BusinessOntologyAgentBudgetRejection(
                metric,
                limit,
                reservedTotal - reservedAmount,
                actualAmount,
                "actual usage exceeded budget during effect reconciliation");
            return false;
        }

        rejection = null;
        return true;
    }

    private static long SaturatingReconcile(
        long reservedTotal,
        long reservedAmount,
        long actualAmount,
        long limit)
    {
        try
        {
            return Math.Min(checked(reservedTotal - reservedAmount + actualAmount), limit);
        }
        catch (OverflowException)
        {
            return limit;
        }
    }

    private static BusinessOntologyAgentBudgetCommitResult RejectedCommit(
        BusinessOntologyAgentBudgetReservation reservation,
        string metric,
        long reserved,
        long actual,
        string audit)
    {
        var after = reservation.State.AfterReservation;
        var released = after.Advance(
            after.TurnsUsed,
            after.QueriesUsed,
            after.RowsRead - reservation.ReservedRows,
            after.SourceBytesRead - reservation.ReservedSourceBytes,
            after.InputTokensUsed - reservation.ReservedInputTokens,
            after.OutputTokensUsed - reservation.ReservedOutputTokens,
            Math.Max(0, after.InFlightEffects - 1));
        return new BusinessOntologyAgentBudgetCommitResult(
            false,
            released,
            new BusinessOntologyAgentBudgetRejection(metric, reserved, 0, actual, audit));
    }

    private static BusinessOntologyAgentBudgetReservation EmptyReservation(
        string kind,
        BusinessOntologyAgentBudgetLimits limits,
        BusinessOntologyAgentBudgetState state) =>
        new(
            kind,
            limits,
            new BusinessOntologyAgentBudgetReservationState(state, state),
            0,
            0,
            0,
            0);

    private static void RequireKind(BusinessOntologyAgentBudgetReservation reservation, string expected)
    {
        if (!StringComparer.Ordinal.Equals(reservation.Kind, expected))
        {
            throw new ArgumentException($"Reservation kind must be '{expected}'.", nameof(reservation));
        }
    }

    private static long NonNegative(long value, string name) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(name, "Budget amount must not be negative.");
}
