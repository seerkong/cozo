using System.Text;
using System.Text.Json;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tools;

public sealed record SemanticEvidenceAnchorSource(
    string EvidenceId,
    string Repository,
    string RepositoryRoot,
    string Path,
    string SubjectId,
    string ClaimKind,
    string ClaimPayloadJson,
    int StartLine,
    int EndLine,
    IReadOnlyList<string> ExistingOntologyIds,
    string CorroborationExcerpt = "");

public sealed record SemanticEvidencePackAnchor(
    string EvidenceId,
    string Repository,
    string Path,
    string SubjectId,
    string ClaimKind,
    string ClaimPayloadJson,
    int StartLine,
    int EndLine,
    int ExcerptStartLine,
    int ExcerptEndLine,
    string SourceExcerpt,
    IReadOnlyList<string> ExistingOntologyIds,
    string CorroborationExcerpt,
    bool TextTruncated);

public sealed record SemanticEvidencePack(
    IReadOnlyList<SemanticEvidencePackAnchor> Anchors,
    int SourceUtf8Bytes,
    int OmittedAnchors,
    bool TextTruncated)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string ToCanonicalJson() => OntologySemanticJson.Canonicalize(JsonSerializer.Serialize(new
    {
        anchors = Anchors,
        sourceUtf8Bytes = SourceUtf8Bytes,
        omittedAnchors = OmittedAnchors,
        textTruncated = TextTruncated,
    }, JsonOptions));
}

/// <summary>Builds deterministic, source-bounded semantic evidence packs without serializing repository roots.</summary>
public sealed class SemanticEvidencePackBuilder
{
    public const int MaxAnchors = 24;
    public const int MaxSourceUtf8Bytes = 20 * 1024;
    public const int SurroundingLines = 12;

    public async Task<SemanticEvidencePack> BuildEvidencePackAsync(
        IEnumerable<SemanticEvidenceAnchorSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var ordered = sources
            .OrderBy(item => item.Repository, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.StartLine)
            .ThenBy(item => item.EndLine)
            .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ToArray();
        if (ordered.GroupBy(item => item.EvidenceId, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Semantic evidence pack contains a duplicate evidence identity.", nameof(sources));
        }

        var validated = ordered.Select(ValidateSource).ToArray();
        var anchors = new List<SemanticEvidencePackAnchor>();
        var remainingBytes = MaxSourceUtf8Bytes;
        var anyTextTruncated = false;

        foreach (var source in validated.Take(MaxAnchors))
        {
            if (remainingBytes == 0)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(source.AbsolutePath, cancellationToken);
            var lines = NormalizeLines(content);
            if (source.Input.EndLine > lines.Length)
            {
                throw new ArgumentException(
                    $"Evidence '{source.Input.EvidenceId}' line range exceeds '{source.RelativePath}'.",
                    nameof(sources));
            }

            var excerptStart = Math.Max(1, source.Input.StartLine - SurroundingLines);
            var requestedExcerptEnd = Math.Min(lines.Length, source.Input.EndLine + SurroundingLines);
            var excerpt = string.Join('\n', lines[(excerptStart - 1)..requestedExcerptEnd]);
            var sourceExcerpt = TakeUtf8Prefix(excerpt, remainingBytes, out var sourceTruncated);
            remainingBytes -= Encoding.UTF8.GetByteCount(sourceExcerpt);

            var corroboration = TakeUtf8Prefix(
                source.Input.CorroborationExcerpt,
                remainingBytes,
                out var corroborationTruncated);
            remainingBytes -= Encoding.UTF8.GetByteCount(corroboration);
            var textTruncated = sourceTruncated || corroborationTruncated;
            anyTextTruncated |= textTruncated;
            var excerptEnd = Math.Min(
                requestedExcerptEnd,
                excerptStart + sourceExcerpt.Count(character => character == '\n'));

            anchors.Add(new SemanticEvidencePackAnchor(
                source.Input.EvidenceId,
                source.Input.Repository,
                source.RelativePath,
                source.Input.SubjectId,
                source.Input.ClaimKind,
                source.CanonicalClaimPayload,
                source.Input.StartLine,
                source.Input.EndLine,
                excerptStart,
                excerptEnd,
                sourceExcerpt,
                source.Input.ExistingOntologyIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                corroboration,
                textTruncated));
        }

        var omitted = ordered.Length - anchors.Count;
        return new SemanticEvidencePack(
            anchors,
            MaxSourceUtf8Bytes - remainingBytes,
            omitted,
            anyTextTruncated || omitted > 0);
    }

    private static ValidatedSource ValidateSource(SemanticEvidenceAnchorSource input)
    {
        if (string.IsNullOrWhiteSpace(input.EvidenceId) ||
            string.IsNullOrWhiteSpace(input.Repository) ||
            string.IsNullOrWhiteSpace(input.RepositoryRoot) ||
            string.IsNullOrWhiteSpace(input.Path) ||
            string.IsNullOrWhiteSpace(input.SubjectId))
        {
            throw new ArgumentException("Semantic evidence identity, repository, root, path, and subject are required.");
        }
        if (!CodeSemanticClaimKinds.IsSupported(input.ClaimKind))
        {
            throw new ArgumentException($"Unsupported semantic claim kind '{input.ClaimKind}'.");
        }
        if (Path.IsPathRooted(input.Path) ||
            input.Path.Contains('\\') ||
            input.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException("Semantic evidence paths must be repository-relative POSIX paths.");
        }
        if (input.StartLine < 1 || input.EndLine < input.StartLine)
        {
            throw new ArgumentException("Semantic evidence line ranges must be positive and ordered.");
        }

        var root = Path.GetFullPath(input.RepositoryRoot);
        var absolutePath = Path.GetFullPath(Path.Combine(root, input.Path));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!absolutePath.StartsWith(rootPrefix, StringComparison.Ordinal) || !File.Exists(absolutePath))
        {
            throw new ArgumentException($"Semantic evidence path '{input.Path}' is outside its repository or does not exist.");
        }
        var relativePath = Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
        var canonicalClaimPayload = OntologySemanticJson.Canonicalize(input.ClaimPayloadJson);
        return new ValidatedSource(input, absolutePath, relativePath, canonicalClaimPayload);
    }

    private static string[] NormalizeLines(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static string TakeUtf8Prefix(string value, int byteBudget, out bool truncated)
    {
        if (string.IsNullOrEmpty(value))
        {
            truncated = false;
            return "";
        }
        if (byteBudget <= 0)
        {
            truncated = true;
            return "";
        }
        if (Encoding.UTF8.GetByteCount(value) <= byteBudget)
        {
            truncated = false;
            return value;
        }

        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > byteBudget)
            {
                break;
            }
            builder.Append(rune);
            used += rune.Utf8SequenceLength;
        }
        truncated = true;
        return builder.ToString();
    }

    private sealed record ValidatedSource(
        SemanticEvidenceAnchorSource Input,
        string AbsolutePath,
        string RelativePath,
        string CanonicalClaimPayload);
}
