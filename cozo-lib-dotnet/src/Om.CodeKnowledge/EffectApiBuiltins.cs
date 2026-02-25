namespace Cozo.DotNet.Om.CodeKnowledge;

/// <summary>
/// A single effect-API classification rule: a dot-segmented FQN glob pattern mapped to an
/// effect category and a data direction ("read" | "write" | "both").
/// </summary>
internal sealed record EffectApiRule(string Pattern, string Category, string Direction = "both");

/// <summary>
/// Built-in effect-API vocabulary (design §4.1: compiled into Om.CodeKnowledge, the observation
/// layer). The index-time write path pre-classifies ck_external_call rows against this table;
/// Om.Depa reuses it as the base of its merged (built-in + user depa-effects.json) whitelist,
/// keeping the dependency direction Om.Depa → Om.CodeKnowledge (track decision #1).
/// </summary>
internal static class EffectApiBuiltins
{
    // Design §4.1 starter table. NOTE: the more specific "System.IO.File.Read*" is listed
    // before "System.IO.**" — with first-hit-wins ordering this is what makes read-only
    // file APIs classify as direction=read instead of falling into the broad both-bucket.
    internal static IReadOnlyList<EffectApiRule> Rules { get; } =
    [
        new("System.IO.File.Read*", "file_io", "read"),
        new("System.IO.**", "file_io", "both"),
        new("System.Net.Http.**", "network", "both"),
        new("System.Data.**", "db", "both"),
        new("Microsoft.Data.Sqlite.**", "db", "both"),
        new("System.Diagnostics.Process.**", "process", "both"),
        new("System.Console.**", "console", "write"),
        new("System.Environment.GetEnvironment*", "env", "read"),
        new("System.Random.**", "nondeterminism", "read"),
        new("System.DateTime.Now", "nondeterminism", "read"),
    ];

    /// <summary>Returns the first rule whose pattern matches the target FQN, or null.</summary>
    internal static EffectApiRule? Match(IReadOnlyList<EffectApiRule> rules, string targetFqn)
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
    /// Dot-segmented FQN glob match: '*' matches within a single segment (may be partial,
    /// e.g. "Read*" matches "ReadAllText"), '**' matches any number of segments (including zero).
    /// </summary>
    internal static bool GlobMatch(string pattern, string targetFqn)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(targetFqn))
        {
            return false;
        }

        return MatchSegments(pattern.Split('.'), 0, targetFqn.Split('.'), 0);
    }

    private static bool MatchSegments(string[] pattern, int pi, string[] target, int ti)
    {
        if (pi == pattern.Length)
        {
            return ti == target.Length;
        }

        if (pattern[pi] == "**")
        {
            // '**' consumes zero segments, or one segment and stays greedy.
            return MatchSegments(pattern, pi + 1, target, ti)
                || (ti < target.Length && MatchSegments(pattern, pi, target, ti + 1));
        }

        return ti < target.Length
            && MatchSegment(pattern[pi], target[ti])
            && MatchSegments(pattern, pi + 1, target, ti + 1);
    }

    private static bool MatchSegment(string pattern, string segment)
    {
        // In-segment wildcard match; '*' matches any run of non-dot characters.
        return MatchChars(pattern, 0, segment, 0);
    }

    private static bool MatchChars(string pattern, int pi, string segment, int si)
    {
        while (true)
        {
            if (pi == pattern.Length)
            {
                return si == segment.Length;
            }

            if (pattern[pi] == '*')
            {
                if (MatchChars(pattern, pi + 1, segment, si))
                {
                    return true;
                }

                if (si == segment.Length)
                {
                    return false;
                }

                si++;
                continue;
            }

            if (si == segment.Length || pattern[pi] != segment[si])
            {
                return false;
            }

            pi++;
            si++;
        }
    }
}
