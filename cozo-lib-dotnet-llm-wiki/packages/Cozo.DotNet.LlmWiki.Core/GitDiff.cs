using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Cozo.DotNet.LlmWiki.Core;

// Git capsule (add-llm-wiki-detect-changes track, design.md §1; decisions #1).
// Lives in Core because it is the only spot both Tools and Indexing can share without a new
// package or a reverse dependency. Everything here is internal by design: the tool output
// schema is the public contract, the capsule is not.

/// <summary>Which diff the caller wants (design.md §3 scope parameter).</summary>
internal enum GitDiffScope
{
    /// <summary>Working tree vs index: <c>git diff -U0</c>.</summary>
    Unstaged,

    /// <summary>Index vs HEAD: <c>git diff -U0 --cached</c>.</summary>
    Staged,

    /// <summary>Merge-base comparison: <c>git diff -U0 &lt;baseRef&gt;...HEAD</c>.</summary>
    Compare,
}

/// <summary>
/// One changed file with its new-side changed line ranges (1-based, inclusive).
/// Deleted files carry <see cref="IsDeleted"/> (map all of their symbols); binary files carry
/// <see cref="IsBinary"/> and no ranges (skipped, recorded in diagnostics).
/// </summary>
internal sealed record GitChangedFile(
    string Path,
    IReadOnlyList<(int Start, int End)> Ranges,
    bool IsDeleted = false,
    bool IsBinary = false);

/// <summary>
/// Diff outcome. Never throws for environmental reasons: a missing git binary or a non-git
/// directory yields empty <see cref="Files"/> plus an explanatory diagnostic (behavior delta
/// case non-git-safe).
/// </summary>
internal sealed record GitDiffResult(
    IReadOnlyList<GitChangedFile> Files,
    IReadOnlyList<string> Diagnostics)
{
    public static readonly GitDiffResult Empty = new([], []);
}

/// <summary>Contract seam so Indexing/Tools consumers stay testable without a real git binary.</summary>
internal interface IGitDiffProvider
{
    /// <summary>HEAD commit hash of <paramref name="directory"/>, or null when git/repo is unavailable.</summary>
    string? TryGetHeadCommit(string directory);

    /// <summary>Changed files with new-side line ranges for the given scope. Never throws for environmental failures.</summary>
    GitDiffResult GetDiff(string directory, GitDiffScope scope, string? baseRef = null);
}

/// <summary>
/// Production implementation: shells out to <c>git -C &lt;dir&gt; ...</c> and parses -U0 unified
/// diff output. Hunk headers <c>@@ -a,b +c,d @@</c> are read on the new side: an omitted d means
/// 1 → [c, c]; d = 0 marks a pure deletion at position c → [c, c]; otherwise [c, c + d - 1].
/// </summary>
internal sealed class GitCliDiffProvider : IGitDiffProvider
{
    public static readonly GitCliDiffProvider Default = new();

    private const int GitTimeoutMs = 10_000;

    // New-side capture of the -U0 hunk header: +c[,d].
    private static readonly Regex HunkHeader = new(
        @"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@",
        RegexOptions.Compiled);

    public string? TryGetHeadCommit(string directory)
    {
        var run = RunGit(directory, ["rev-parse", "HEAD"]);
        if (run is null || run.Value.ExitCode != 0)
        {
            return null;
        }

        var commit = run.Value.StdOut.Trim();
        return commit.Length > 0 ? commit : null;
    }

    public GitDiffResult GetDiff(string directory, GitDiffScope scope, string? baseRef = null)
    {
        string[] args;
        switch (scope)
        {
            case GitDiffScope.Unstaged:
                args = ["diff", "-U0"];
                break;
            case GitDiffScope.Staged:
                args = ["diff", "-U0", "--cached"];
                break;
            case GitDiffScope.Compare:
                if (string.IsNullOrWhiteSpace(baseRef))
                {
                    return new GitDiffResult([], ["compare scope requires a baseRef (e.g. origin/main); none was resolvable"]);
                }

                args = ["diff", "-U0", $"{baseRef.Trim()}...HEAD"];
                break;
            default:
                return new GitDiffResult([], [$"unsupported diff scope: {scope}"]);
        }

        var run = RunGit(directory, args);
        if (run is null)
        {
            return new GitDiffResult([], ["git is not available on this machine; change detection is disabled"]);
        }

        if (run.Value.ExitCode != 0)
        {
            var reason = run.Value.StdErr.Trim();
            return new GitDiffResult(
                [],
                [reason.Length > 0 ? $"git diff failed (exit {run.Value.ExitCode}): {reason}" : $"git diff failed (exit {run.Value.ExitCode})"]);
        }

        return ParseUnifiedDiff(run.Value.StdOut);
    }

    /// <summary>
    /// Parses <c>git diff -U0</c> output into per-file new-side line ranges. Internal (not
    /// private) so tests can cover hunk-header edge cases without a repo fixture.
    /// </summary>
    internal static GitDiffResult ParseUnifiedDiff(string output)
    {
        var files = new List<GitChangedFile>();
        var diagnostics = new List<string>();

        string headerPath = "";   // b-side path from "diff --git a/x b/y" (binary fallback).
        string? newPath = null;   // from "+++ b/<path>"; null until seen.
        string oldPath = "";      // from "--- a/<path>"; the surviving name for deletions.
        var isDeleted = false;
        var ranges = new List<(int Start, int End)>();
        var pendingFile = false;

        void Flush()
        {
            if (!pendingFile)
            {
                return;
            }

            var path = isDeleted ? oldPath : newPath ?? headerPath;
            if (path.Length > 0)
            {
                files.Add(new GitChangedFile(path, ranges.ToArray(), IsDeleted: isDeleted));
            }

            newPath = null;
            oldPath = "";
            isDeleted = false;
            ranges.Clear();
            pendingFile = false;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                headerPath = ParseHeaderBPath(line);
                pendingFile = true;
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) && line.EndsWith(" differ", StringComparison.Ordinal))
            {
                var binaryPath = newPath is { Length: > 0 } ? newPath : headerPath;
                if (binaryPath.Length > 0)
                {
                    files.Add(new GitChangedFile(binaryPath, [], IsBinary: true));
                }

                diagnostics.Add($"binary file skipped: {(binaryPath.Length > 0 ? binaryPath : line)}");
                newPath = null;
                oldPath = "";
                isDeleted = false;
                ranges.Clear();
                pendingFile = false;
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                // "--- /dev/null" = new file (nothing to record; the +1,N hunk already covers the
                // whole file); "--- a/<path>" remembers the old name for the deletion case.
                oldPath = line == "--- /dev/null" ? "" : StripPathPrefix(line[4..]);
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                if (line == "+++ /dev/null")
                {
                    isDeleted = true;
                }
                else
                {
                    newPath = StripPathPrefix(line[4..]);
                }
            }
            else if (HunkHeader.Match(line) is { Success: true } match)
            {
                var start = int.Parse(match.Groups[1].Value);
                var count = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
                // count == 0: pure deletion — anchor the range at the new-side position [c, c].
                ranges.Add(count == 0 ? (start, start) : (start, start + count - 1));
            }
        }

        Flush();
        return new GitDiffResult(files, diagnostics);
    }

    private static string ParseHeaderBPath(string diffGitLine)
    {
        // "diff --git a/<x> b/<y>" — take the last " b/" occurrence (paths with spaces are rare
        // and quoted paths are out of scope; the +++/--- lines override this value anyway).
        var index = diffGitLine.LastIndexOf(" b/", StringComparison.Ordinal);
        return index < 0 ? "" : diffGitLine[(index + 3)..].Trim();
    }

    private static string StripPathPrefix(string path)
    {
        var trimmed = CUnquote(path.Trim());
        if (trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal))
        {
            return trimmed[2..];
        }

        return trimmed;
    }

    /// <summary>
    /// Undoes git's C-style path quoting (<c>"a/\346\234\215.cs"</c>). RunGit already passes
    /// <c>core.quotepath=off</c> so non-ASCII prints raw; quoting still happens for paths that
    /// contain quotes, backslashes or control characters — decode octal escapes as UTF-8 bytes
    /// and the standard single-character escapes. Non-quoted input is returned unchanged.
    /// </summary>
    internal static string CUnquote(string path)
    {
        if (path.Length < 2 || path[0] != '"' || path[^1] != '"')
        {
            return path;
        }

        var inner = path[1..^1];
        var bytes = new List<byte>(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c != '\\' || i + 1 >= inner.Length)
            {
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }

            var next = inner[++i];
            if (next is >= '0' and <= '7')
            {
                var value = next - '0';
                for (var digits = 1; digits < 3 && i + 1 < inner.Length && inner[i + 1] is >= '0' and <= '7'; digits++)
                {
                    value = (value << 3) + (inner[++i] - '0');
                }

                bytes.Add((byte)value);
            }
            else
            {
                bytes.Add(next switch
                {
                    't' => (byte)'\t',
                    'n' => (byte)'\n',
                    'r' => (byte)'\r',
                    'a' => (byte)'\a',
                    'b' => (byte)'\b',
                    'f' => (byte)'\f',
                    'v' => (byte)'\v',
                    _ => (byte)next, // covers \" and \\ and anything unknown verbatim
                });
            }
        }

        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static (int ExitCode, string StdOut, string StdErr)? RunGit(string directory, string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("-C");
            info.ArgumentList.Add(directory);
            // Non-ASCII paths (e.g. Chinese file names) must print raw instead of C-quoted octal
            // escapes so they match ck_file.path; residual quoting (quotes/control chars in the
            // path itself) is handled by CUnquote.
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("core.quotepath=off");
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(GitTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best effort
                }

                return null;
            }

            return (process.ExitCode, stdOut, stdErr);
        }
        catch
        {
            // git missing (Win32Exception), permission issues, ... — the capsule never throws.
            return null;
        }
    }
}
