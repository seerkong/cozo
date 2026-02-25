using System.Text;
using Cozo.DotNet.LlmWiki.Core;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Wiki;

public sealed class WikiCompiler
{
    public async Task<WikiBuildResult> BuildAsync(
        CozoOm om,
        WikiBuildRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        request ??= new WikiBuildRequest();

        var plan = await om.BuildWikiPlanAsync(cancellationToken);
        var pages = plan.Pages.Select(ToDocument).ToArray();
        var index = BuildIndex(pages, plan.MissingDocs);
        var written = new List<string>();
        if (request.WriteFiles)
        {
            if (string.IsNullOrWhiteSpace(request.OutputDirectory))
            {
                throw new ArgumentException("OutputDirectory is required when WriteFiles is true.", nameof(request));
            }

            Directory.CreateDirectory(request.OutputDirectory);
            var indexPath = Path.Combine(request.OutputDirectory, "index.md");
            await File.WriteAllTextAsync(indexPath, index, cancellationToken);
            written.Add(indexPath);
            foreach (var page in pages)
            {
                var path = Path.Combine(request.OutputDirectory, $"{SafeFileName(page.PageId)}.md");
                await File.WriteAllTextAsync(path, page.Markdown, cancellationToken);
                written.Add(path);
            }
        }

        return new WikiBuildResult(index, pages, written, plan.MissingDocs);
    }

    private static WikiPageDocument ToDocument(WikiPlanPage page)
    {
        var markdown = new StringBuilder();
        markdown.Append("# ").AppendLine(page.Title);
        markdown.AppendLine();
        markdown.AppendLine("## Sources");
        foreach (var source in page.SourceFileIds)
        {
            markdown.Append("- `").Append(source).AppendLine("`");
        }

        markdown.AppendLine();
        markdown.AppendLine("## Symbols");
        if (page.SymbolIds.Count == 0)
        {
            markdown.AppendLine("- No symbols indexed.");
        }
        else
        {
            foreach (var symbol in page.SymbolIds)
            {
                markdown.Append("- `").Append(symbol).AppendLine("`");
            }
        }

        if (page.DocIds.Count > 0)
        {
            markdown.AppendLine();
            markdown.AppendLine("## Documentation Blocks");
            foreach (var doc in page.DocIds)
            {
                markdown.Append("- `").Append(doc).AppendLine("`");
            }
        }

        return new WikiPageDocument(page.PageId, page.Title, markdown.ToString(), page.SourceFileIds, page.SymbolIds, page.DocIds);
    }

    private static string BuildIndex(IReadOnlyList<WikiPageDocument> pages, IReadOnlyList<string> missingDocs)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine("# Code Wiki");
        markdown.AppendLine();
        markdown.AppendLine("## Pages");
        foreach (var page in pages)
        {
            markdown.Append("- ").Append(page.Title).Append(" (`").Append(page.PageId).AppendLine("`)");
        }

        markdown.AppendLine();
        markdown.AppendLine("## Missing Documentation");
        if (missingDocs.Count == 0)
        {
            markdown.AppendLine("- None");
        }
        else
        {
            foreach (var missing in missingDocs)
            {
                markdown.Append("- `").Append(missing).AppendLine("`");
            }
        }

        return markdown.ToString();
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(ch => invalid.Contains(ch) || ch is ':' or '/' or '\\' ? '-' : ch).ToArray());
    }
}
