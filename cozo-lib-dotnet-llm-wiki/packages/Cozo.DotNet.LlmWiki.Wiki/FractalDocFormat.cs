using System.Text;

namespace Cozo.DotNet.LlmWiki.Wiki;

/// <summary>
/// Deterministic building blocks of the engineering fractal pages (add-llm-wiki-engineering-fractal
/// track T1.1, design §2): the controlled frontmatter of std/attractors/model-driven-docs (four
/// fixed scalar fields, no arrays, no modeling-only fields) and the 目录职责 manifest blocks of
/// std/spec/folder-manifest.md (slim one-line blockquote for standard leaf categories, full
/// section for structure nodes). Everything is a pure string constructor — no I/O, no LLM.
/// </summary>
internal static class FractalDocFormat
{
    // doc_role controlled vocabulary (model-driven-docs): the generator emits only this subset.
    public const string RoleCanonical = "canonical";
    public const string RoleGuide = "guide";
    public const string RoleHowto = "howto";
    public const string RoleRules = "rules";
    public const string RoleExample = "example";
    public const string RoleReference = "reference";
    public const string RoleTroubleshooting = "troubleshooting";

    /// <summary>
    /// Controlled frontmatter block, trailing newline included. Fixed fields only:
    /// knowledge_plane / doc_role / status(active) / last_verified. Two conditional scalars:
    /// <paramref name="knowledgeSystem"/> exists for the root migration-map template (E-A2)
    /// and <paramref name="context"/> for modeling documents (docs-modeling-fractal §5:
    /// context is mandatory on context-scoped pages, one line, never an array). derived_from
    /// is never emitted (this generator only writes canonical domain pages).
    /// </summary>
    public static string Frontmatter(string knowledgePlane, string docRole, string lastVerified, string? knowledgeSystem = null, string? context = null)
    {
        var builder = new StringBuilder();
        builder.Append("---\n");
        if (knowledgeSystem is { Length: > 0 })
        {
            builder.Append("knowledge_system: ").Append(knowledgeSystem).Append('\n');
        }

        builder.Append("knowledge_plane: ").Append(knowledgePlane).Append('\n');
        builder.Append("doc_role: ").Append(docRole).Append('\n');
        builder.Append("status: active\n");
        if (context is { Length: > 0 })
        {
            builder.Append("context: ").Append(context).Append('\n');
        }

        builder.Append("last_verified: ").Append(lastVerified).Append('\n');
        builder.Append("---\n");
        return builder.ToString();
    }

    /// <summary>
    /// Slim manifest block (one-line blockquote) matching the fixtures E-D2 regex:
    /// <c>^&gt; 目录职责 · holds: … · excludes: … · tier: stable · ⬆from: … · ⬇to: …$</c>.
    /// Values must not contain the <c>·</c> separator or newlines.
    /// </summary>
    public static string SlimManifest(string holds, string excludes, string promotesFrom, string promotesTo) =>
        $"> 目录职责 · holds: {holds} · excludes: {excludes} · tier: stable · ⬆from: {promotesFrom} · ⬇to: {promotesTo}";

    /// <summary>Full manifest section for plane roots / structure nodes (fixtures E-D3), trailing newline included.</summary>
    public static string FullManifest(string holds, string excludes, string promotesFrom, string promotesTo) =>
        "## 目录职责\n\n"
        + $"- **holds**：{holds}\n"
        + $"- **excludes**：{excludes}\n"
        + "- **tier**：`stable`\n"
        + $"- **promotes_from**：{promotesFrom}\n"
        + $"- **promotes_to**：{promotesTo}\n";

    /// <summary>ASCII slug for topic file names (howto/working-with-&lt;slug&gt;.md): lowercase, non-alphanumeric runs collapse to '-'.</summary>
    public static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        var pendingDash = false;
        foreach (var ch in name)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingDash = false;
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingDash = true;
            }
        }

        return builder.Length > 0 ? builder.ToString() : "group";
    }
}
