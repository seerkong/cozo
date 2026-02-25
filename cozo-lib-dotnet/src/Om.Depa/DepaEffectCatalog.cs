using System.Text.Json;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.Om.Depa;

/// <summary>
/// Effect-API whitelist (design §4.1): the built-in starter table lives in the observation
/// layer (<see cref="EffectApiBuiltins"/>, Om.CodeKnowledge) and is reused here, optionally
/// extended/overridden by a user depa-effects.json. Matching is in-order, first hit wins;
/// user entries are consulted before built-ins so a user entry with the same pattern
/// overrides the built-in classification. Dependency direction stays
/// Om.Depa → Om.CodeKnowledge (track decision #1).
/// </summary>
internal static class DepaEffectCatalog
{
    /// <summary>The built-in table, projected from Om.CodeKnowledge's compiled-in vocabulary.</summary>
    internal static IReadOnlyList<DepaEffectRule> BuiltIn { get; } =
        [.. EffectApiBuiltins.Rules.Select(r => new DepaEffectRule(r.Pattern, r.Category, r.Direction))];

    /// <summary>
    /// Loads user effects from depa-effects.json. Missing file yields an empty list
    /// (built-ins still apply). Entries without pattern/category are skipped; a missing
    /// direction defaults to "both".
    /// </summary>
    internal static IReadOnlyList<DepaEffectRule> LoadUserEffects(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("effects", out var effects)
            || effects.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rules = new List<DepaEffectRule>();
        foreach (var item in effects.EnumerateArray())
        {
            var pattern = GetString(item, "pattern");
            var category = GetString(item, "category");
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(category))
            {
                continue;
            }

            var direction = GetString(item, "direction");
            rules.Add(new DepaEffectRule(pattern!, category!, string.IsNullOrWhiteSpace(direction) ? "both" : direction!));
        }

        return rules;
    }

    /// <summary>
    /// Merges user entries (first, in file order) with the built-in table (after).
    /// Duplicate patterns keep the first occurrence, so a user entry with a built-in
    /// pattern overrides it; new patterns append.
    /// </summary>
    internal static IReadOnlyList<DepaEffectRule> Merge(IReadOnlyList<DepaEffectRule>? userEffects)
    {
        var merged = new List<DepaEffectRule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in (userEffects ?? []).Concat(BuiltIn))
        {
            if (seen.Add(rule.Pattern))
            {
                merged.Add(rule);
            }
        }

        return merged;
    }

    /// <summary>Returns the first rule whose pattern matches the target FQN, or null.</summary>
    internal static DepaEffectRule? Match(IReadOnlyList<DepaEffectRule> rules, string targetFqn)
    {
        foreach (var rule in rules)
        {
            if (GlobMatch(rule.Pattern, targetFqn))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>
    /// Dot-segmented FQN glob match, delegated to the observation layer's shared matcher:
    /// '*' matches within a single segment (may be partial), '**' matches any number of
    /// segments (including zero).
    /// </summary>
    internal static bool GlobMatch(string pattern, string targetFqn) =>
        EffectApiBuiltins.GlobMatch(pattern, targetFqn);

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
