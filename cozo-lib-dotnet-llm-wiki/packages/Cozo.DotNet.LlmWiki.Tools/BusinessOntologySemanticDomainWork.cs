using System.Security.Cryptography;
using System.Text;
using System.Collections.ObjectModel;

namespace Cozo.DotNet.LlmWiki.Tools;

internal static class BusinessOntologySemanticDomainWorkMetrics
{
    public const string Domains = "domains";
    public const string Completions = "completions";
    public const string InvestigationOperations = "investigation_operations";
}

/// <summary>Hard, local caps for one v3 domain-oriented semantic work batch.</summary>
internal sealed record BusinessOntologySemanticDomainWorkLimits
{
    public const int DefaultMaxDomains = 6;
    public const int DefaultMaxCompletionsPerDomain = 4;
    public const int DefaultMaxInvestigationOperationsPerDomain = 10;

    public static BusinessOntologySemanticDomainWorkLimits Default { get; } = new();

    public int MaxDomains { get; }
    public int MaxCompletionsPerDomain { get; }
    public int MaxInvestigationOperationsPerDomain { get; }

    public BusinessOntologySemanticDomainWorkLimits(
        int maxDomains = DefaultMaxDomains,
        int maxCompletionsPerDomain = DefaultMaxCompletionsPerDomain,
        int maxInvestigationOperationsPerDomain = DefaultMaxInvestigationOperationsPerDomain)
    {
        MaxDomains = RequireBounded(maxDomains, DefaultMaxDomains, nameof(maxDomains));
        MaxCompletionsPerDomain = RequireBounded(maxCompletionsPerDomain, DefaultMaxCompletionsPerDomain, nameof(maxCompletionsPerDomain));
        MaxInvestigationOperationsPerDomain = RequireBounded(
            maxInvestigationOperationsPerDomain,
            DefaultMaxInvestigationOperationsPerDomain,
            nameof(maxInvestigationOperationsPerDomain));
    }

    internal string CacheFingerprintMaterial =>
        $"{MaxDomains}:{MaxCompletionsPerDomain}:{MaxInvestigationOperationsPerDomain}";

    private static int RequireBounded(int value, int maximum, string name)
    {
        if (value <= 0 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must be between 1 and {maximum}.");
        }

        return value;
    }
}

/// <summary>Canonical, evidence-bound work identity. Instances originate from the planner only.</summary>
internal sealed record BusinessOntologySemanticDomainWorkItem(
    string DomainId,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> CharterFingerprints,
    string InputFingerprint);

internal static class BusinessOntologySemanticDomainWorkPlanner
{
    /// <summary>Opaque, immutable plan issued only by this planner.</summary>
    internal sealed class DomainWorkPlan
    {
        private DomainWorkPlan(IReadOnlyList<BusinessOntologySemanticDomainWorkItem> items)
        {
            if (items.Count > BusinessOntologySemanticDomainWorkLimits.DefaultMaxDomains)
            {
                throw new InvalidOperationException("Semantic domain work plan exceeds the hard domain cap.");
            }

            var frozenItems = items
                .Select(item => new BusinessOntologySemanticDomainWorkItem(
                    item.DomainId,
                    new ReadOnlyCollection<string>(item.EvidenceIds.ToArray()),
                    new ReadOnlyCollection<string>(item.CharterFingerprints.ToArray()),
                    item.InputFingerprint))
                .OrderBy(item => item.DomainId, StringComparer.Ordinal)
                .ThenBy(item => item.InputFingerprint, StringComparer.Ordinal)
                .ToList();
            Items = new ReadOnlyCollection<BusinessOntologySemanticDomainWorkItem>(frozenItems);
            if (Items.Select(item => item.DomainId).Distinct(StringComparer.Ordinal).Count() != Items.Count)
            {
                throw new ArgumentException("Semantic domain work plan contains duplicate domain ids.", nameof(items));
            }
        }

        internal IReadOnlyList<BusinessOntologySemanticDomainWorkItem> Items { get; }

        internal static DomainWorkPlan FromCharters(
            IReadOnlyList<BusinessOntologySemanticDomainCharter> charters,
            BusinessOntologySemanticDomainWorkLimits? limits)
        {
            ArgumentNullException.ThrowIfNull(charters);
            limits ??= BusinessOntologySemanticDomainWorkLimits.Default;

            var items = charters
                .GroupBy(charter => RequireDomainId(charter.Id), StringComparer.Ordinal)
                .Select(group => CreateItem(group.Key, group.ToArray()))
                .OrderBy(item => item.DomainId, StringComparer.Ordinal)
                .ToArray();
            if (items.Length > limits.MaxDomains)
            {
                throw new InvalidOperationException(
                    $"Semantic domain work exceeds the {limits.MaxDomains}-domain cap ({BusinessOntologySemanticDomainWorkMetrics.Domains}).");
            }

            return new DomainWorkPlan(items);
        }
    }

    public static DomainWorkPlan Plan(
        IReadOnlyList<BusinessOntologySemanticDomainCharter> charters,
        BusinessOntologySemanticDomainWorkLimits? limits = null)
        => DomainWorkPlan.FromCharters(charters, limits);

    private static BusinessOntologySemanticDomainWorkItem CreateItem(
        string domainId,
        IReadOnlyList<BusinessOntologySemanticDomainCharter> charters)
    {
        var normalizedEvidenceIds = charters
            .SelectMany(charter => charter.EvidenceIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (normalizedEvidenceIds.Length == 0)
        {
            throw new ArgumentException($"Semantic domain '{domainId}' requires at least one evidence id.", nameof(charters));
        }

        var charterFingerprints = charters
            .Select(charter =>
            {
                var charterEvidence = charter.EvidenceIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var workflows = charter.WorkflowNames
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var charterFields = new[]
                    {
                        charter.NameZh.Trim(),
                        charter.DescriptionZh.Trim(),
                        workflows.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }
                    .Concat(workflows)
                    .ToArray();
                return Fingerprint(charter.Id.Trim(), charterEvidence, charterFields);
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new BusinessOntologySemanticDomainWorkItem(
            domainId,
            normalizedEvidenceIds,
            charterFingerprints,
            Fingerprint(
                domainId,
                normalizedEvidenceIds,
                new[] { charterFingerprints.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                    .Concat(charterFingerprints)
                    .ToArray()));
    }

    internal static string Fingerprint(string domainId, IReadOnlyList<string> evidenceIds, params string[] additionalFields)
    {
        var builder = new StringBuilder();
        AppendField(builder, domainId);
        AppendField(builder, evidenceIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var evidenceId in evidenceIds) AppendField(builder, evidenceId);
        foreach (var field in additionalFields) AppendField(builder, field);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void AppendField(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append(';');

    private static string RequireDomainId(string value)
    {
        if (!BusinessOntologySemanticIdentifierGrammar.IsValidDomainId(value))
        {
            throw new ArgumentException("Semantic domain work requires a canonical business domain id.", nameof(value));
        }

        return value.Trim();
    }
}

/// <summary>Declared controlled effects for one domain. The coordinator owns all reservations.</summary>
internal sealed record BusinessOntologySemanticDomainWorkEffectsPlan(
    int CompletionCount,
    int InvestigationOperationCount)
{
    public void Validate(BusinessOntologySemanticDomainWorkLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (CompletionCount is < 0 || InvestigationOperationCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CompletionCount), "Domain effect counts cannot be negative.");
        }
        if (CompletionCount > limits.MaxCompletionsPerDomain)
        {
            throw new ArgumentOutOfRangeException(nameof(CompletionCount), "Domain completion count exceeds the hard cap.");
        }
        if (InvestigationOperationCount > limits.MaxInvestigationOperationsPerDomain)
        {
            throw new ArgumentOutOfRangeException(nameof(InvestigationOperationCount), "Domain investigation count exceeds the hard cap.");
        }
    }
}

/// <summary>The only external effect port. It has no persistence, artifact, or budget capability.</summary>
internal interface IBusinessOntologySemanticDomainWorkEffectPort<T>
{
    Task<T> CompleteAsync(BusinessOntologySemanticDomainWorkItem item, int ordinal, CancellationToken cancellationToken = default);
    Task<T> InvestigateAsync(BusinessOntologySemanticDomainWorkItem item, int ordinal, CancellationToken cancellationToken = default);
}

internal sealed record BusinessOntologySemanticDomainWorkOutput<T>(
    IReadOnlyList<T> CompletionOutputs,
    IReadOnlyList<T> InvestigationOutputs);

internal sealed record BusinessOntologySemanticDomainWorkResult<T>(
    BusinessOntologySemanticDomainWorkItem Item,
    string Status,
    bool FromCache,
    BusinessOntologySemanticDomainWorkOutput<T>? Value,
    string? RejectionMetric);

internal sealed class BusinessOntologySemanticDomainWorkCache<T>
{
    private readonly Dictionary<string, BusinessOntologySemanticDomainWorkOutput<T>> _completed = new(StringComparer.Ordinal);

    public int Count => _completed.Count;
    internal int ReadCount { get; private set; }

    public bool TryGet(string fingerprint, out BusinessOntologySemanticDomainWorkOutput<T> value)
    {
        ReadCount++;
        if (_completed.TryGetValue(fingerprint, out var cached))
        {
            value = cached;
            return true;
        }

        value = null!;
        return false;
    }

    public void StoreCompleted(string fingerprint, BusinessOntologySemanticDomainWorkOutput<T> value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(value);
        _completed.TryAdd(fingerprint, value);
    }
}

/// <summary>Runs controlled, reservation-first effects for each independently atomic business domain.</summary>
internal sealed class BusinessOntologySemanticDomainWorkCoordinator
{
    public async Task<IReadOnlyList<BusinessOntologySemanticDomainWorkResult<T>>> ExecuteAsync<T>(
        BusinessOntologySemanticDomainWorkPlanner.DomainWorkPlan plan,
        BusinessOntologySemanticDomainWorkLimits limits,
        IReadOnlyDictionary<string, BusinessOntologySemanticDomainWorkEffectsPlan> effectsByDomain,
        BusinessOntologySemanticDomainWorkCache<T> cache,
        IBusinessOntologySemanticDomainWorkEffectPort<T> effects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(effectsByDomain);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(effects);
        if (plan.Items.Count > limits.MaxDomains)
        {
            throw new InvalidOperationException(
                $"Semantic domain work exceeds the {limits.MaxDomains}-domain cap ({BusinessOntologySemanticDomainWorkMetrics.Domains}).");
        }
        if (effectsByDomain.Count != plan.Items.Count
            || plan.Items.Any(item => !effectsByDomain.ContainsKey(item.DomainId)))
        {
            throw new ArgumentException("Every planned domain requires exactly one controlled effects plan.", nameof(effectsByDomain));
        }

        var prepared = new List<(BusinessOntologySemanticDomainWorkItem Item, BusinessOntologySemanticDomainWorkEffectsPlan EffectsPlan, string CacheKey)>(plan.Items.Count);
        foreach (var item in plan.Items)
        {
            var effectsPlan = effectsByDomain[item.DomainId];
            effectsPlan.Validate(limits);
            var cacheKey = BusinessOntologySemanticDomainWorkPlanner.Fingerprint(
                item.DomainId,
                item.EvidenceIds,
                item.InputFingerprint,
                effectsPlan.CompletionCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                effectsPlan.InvestigationOperationCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                limits.CacheFingerprintMaterial);
            prepared.Add((item, effectsPlan, cacheKey));
        }

        var results = new List<BusinessOntologySemanticDomainWorkResult<T>>(plan.Items.Count);
        foreach (var (item, effectsPlan, cacheKey) in prepared)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new BusinessOntologySemanticDomainWorkResult<T>(item, "cancelled", false, null, null));
                continue;
            }
            if (cache.TryGet(cacheKey, out var cached))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    results.Add(new BusinessOntologySemanticDomainWorkResult<T>(item, "cancelled", false, null, null));
                    continue;
                }
                results.Add(new BusinessOntologySemanticDomainWorkResult<T>(item, "cached", true, cached, null));
                continue;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completionOutputs = new List<T>(effectsPlan.CompletionCount);
                var investigationOutputs = new List<T>(effectsPlan.InvestigationOperationCount);
                for (var ordinal = 0; ordinal < effectsPlan.CompletionCount; ordinal++)
                {
                    var output = await effects.CompleteAsync(item, ordinal, cancellationToken);
                    EnsureCompleteOutput(output);
                    completionOutputs.Add(output);
                }
                for (var ordinal = 0; ordinal < effectsPlan.InvestigationOperationCount; ordinal++)
                {
                    var output = await effects.InvestigateAsync(item, ordinal, cancellationToken);
                    EnsureCompleteOutput(output);
                    investigationOutputs.Add(output);
                }

                var staged = new BusinessOntologySemanticDomainWorkOutput<T>(completionOutputs.ToArray(), investigationOutputs.ToArray());
                cancellationToken.ThrowIfCancellationRequested();
                cache.StoreCompleted(cacheKey, staged);
                results.Add(new BusinessOntologySemanticDomainWorkResult<T>(item, "completed", false, staged, null));
            }
            catch (OperationCanceledException)
            {
                results.Add(new BusinessOntologySemanticDomainWorkResult<T>(item, "cancelled", false, null, null));
            }
            catch
            {
                results.Add(new BusinessOntologySemanticDomainWorkResult<T>(item, "failed", false, null, null));
            }
        }

        return results;
    }

    private static void EnsureCompleteOutput<T>(T output)
    {
        if (output is null)
        {
            throw new InvalidOperationException("A controlled domain effect returned no completed output.");
        }
    }
}
