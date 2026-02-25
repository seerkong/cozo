using System.Text.Json;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Indexing;

/// <summary>
/// File-level diff computation for incremental indexing (add-llm-wiki-incremental-indexing
/// track, design.md §1). The baseline is the ck_file rows of the same repository; the current
/// side is the freshly built batch (BuildBatchAsync parses the whole tree, so every enumerated
/// file already carries its content hash). Comparing those hashes against the baseline is the
/// exact diff for git and non-git repositories alike — the git capsule is not consulted here
/// because the ck_file baseline may have been indexed from a dirty working tree, in which case
/// commit-range name-status would disagree with what the index actually contains (ruling
/// recorded in the track findings/decisions). No baseline ⇒ null ⇒ the caller falls back to
/// the full path (behavior delta case fallback-full).
/// </summary>
internal static class IncrementalDiff
{
    internal sealed record DiffResult(
        IReadOnlyList<CodeFileFact> Added,
        IReadOnlyList<CodeFileFact> Changed,
        IReadOnlyList<string> RemovedFileIds,
        IReadOnlyList<CodeFileFact> Reused);

    /// <summary>ck_file rows of the repository: file_id → hash. Empty means no usable baseline.</summary>
    internal static async Task<IReadOnlyDictionary<string, string>> LoadBaselineAsync(
        CozoOm om,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        var rows = await om.Runtime.Store.RunAsync(
            """
            ?[file_id, hash] := *ck_file{ file_id, repo_id, hash }, repo_id = $repo_id
            """,
            new Dictionary<string, object?> { ["repo_id"] = repositoryId },
            cancellationToken: cancellationToken);
        var baseline = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows.Rows)
        {
            if (row.Count >= 2 && row[0].ValueKind == JsonValueKind.String)
            {
                baseline[row[0].GetString()!] = row[1].ValueKind == JsonValueKind.String ? row[1].GetString()! : "";
            }
        }

        return baseline;
    }

    internal static DiffResult Compute(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyList<CodeFileFact> current)
    {
        var added = new List<CodeFileFact>();
        var changed = new List<CodeFileFact>();
        var reused = new List<CodeFileFact>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in current)
        {
            seen.Add(file.FileId);
            if (!baseline.TryGetValue(file.FileId, out var oldHash))
            {
                added.Add(file);
            }
            else if (!string.Equals(oldHash, file.Hash, StringComparison.Ordinal))
            {
                changed.Add(file);
            }
            else
            {
                reused.Add(file);
            }
        }

        var removed = baseline.Keys.Where(fileId => !seen.Contains(fileId)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return new DiffResult(added, changed, removed, reused);
    }
}
