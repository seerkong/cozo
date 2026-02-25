using System.Text.RegularExpressions;

namespace Cozo.DotNet.LlmWiki.Wiki;

/// <summary>One violation of the fractal docs spec: RuleId is the fixtures checklist number (e.g. E-A1).</summary>
internal sealed record FractalSpecViolation(string RuleId, string Path, string Detail);

/// <summary>
/// Programmatic verifier of the fractal docs spec. Implements every engineering [P] item
/// (add-llm-wiki-engineering-fractal track T2.1, design §3) AND every modeling [P] item
/// (add-llm-wiki-modeling-fractal track T2.1, design §3) of the G3 track's analysis/fixtures.md —
/// RuleIds are the fixtures numbers — over a docs root directory. M-* rules run whenever a
/// <c>modeling/</c> knowledge system exists at the root (a root without one is engineering-only
/// and M-* does not apply). Pure filesystem + regex checks, no LLM, no graph store — reusable by
/// tests, dogfood probes and the G5 verification pass. The optional repository root enables the
/// M-D1 source-path resolution slice; without it path existence is skipped (structure still checked).
/// </summary>
internal static class FractalSpecChecker
{
    /// <summary>Fixtures E-D2: slim manifest block, single-line blockquote.</summary>
    private static readonly Regex SlimManifestPattern = new(
        @"^> 目录职责 · holds: .+ · excludes: .+ · tier: (stable|dated)( · ⬆from: .+)?( · ⬇to: .+)?$",
        RegexOptions.CultureInvariant);

    private static readonly Regex DatePattern = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);

    /// <summary>Markdown inline links (target up to an optional #fragment), for E-H1 resolution.</summary>
    private static readonly Regex LinkPattern = new(
        @"\]\(([^)\s#]+)(?:#[^)]*)?\)", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> DefaultCategories = new(StringComparer.Ordinal)
    {
        "overview", "howto", "rules", "examples", "reference", "troubleshooting",
    };

    private static readonly HashSet<string> AllowedDocRoles = new(StringComparer.Ordinal)
    {
        "canonical", "derived", "guide", "rules", "howto", "example",
        "reference", "troubleshooting", "compat", "legacy",
    };

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "active", "draft", "compat", "legacy", "deprecated",
    };

    private static readonly HashSet<string> MigrationStatuses = new(StringComparer.Ordinal)
    {
        "migrated", "absorbed", "compat", "deprecated",
    };

    /// <summary>doc_role expected per category (fixtures E-E6); index pages are always guide.</summary>
    private static readonly Dictionary<string, string> CategoryRoles = new(StringComparer.Ordinal)
    {
        ["overview"] = "guide",
        ["howto"] = "howto",
        ["rules"] = "rules",
        ["examples"] = "example",
        ["reference"] = "reference",
        ["troubleshooting"] = "troubleshooting",
    };

    /// <summary>E-F1 weak assertion: prose (non-navigation) characters allowed on an index page.</summary>
    private const int IndexProseBudget = 300;

    /// <summary>E-G4 [P] approximation: single-leaf topic-folder ratio above this (with ≥2 such folders) is flagged.</summary>
    private const double SingleLeafFolderRatioThreshold = 0.5;

    /// <summary>
    /// Historical interface of the G3→G4 handover: every deferred M-* rule is now implemented
    /// (add-llm-wiki-modeling-fractal T2.1), so the list is empty. Kept so callers probing the
    /// declared gap keep compiling.
    /// </summary>
    public static readonly IReadOnlyList<string> NotImplementedRuleIds = [];

    /// <summary>
    /// Runs every implemented [P] assertion against a docs root. Empty list = spec-clean.
    /// <paramref name="repositoryRoot"/> (optional) is the source repository the code-map paths
    /// point into: when given, M-D1 also asserts every mapped source path exists.
    /// </summary>
    public static IReadOnlyList<FractalSpecViolation> Check(string directory, string? repositoryRoot = null)
    {
        var root = Path.GetFullPath(directory);
        var violations = new List<FractalSpecViolation>();
        if (!Directory.Exists(root))
        {
            violations.Add(new("E-A1", ".", "docs root directory does not exist"));
            return violations;
        }

        CheckRoot(root, violations);
        foreach (var plane in EnumeratePlanes(root))
        {
            CheckPlane(root, plane, violations);
        }

        CheckEvolution(root, violations);
        CheckMigrationMap(root, violations);
        foreach (var file in EnumerateEngineeringMarkdown(root))
        {
            CheckDocument(root, file, violations);
        }

        CheckModeling(root, repositoryRoot, violations);

        return violations
            .OrderBy(v => v.RuleId, StringComparer.Ordinal)
            .ThenBy(v => v.Path, StringComparer.Ordinal)
            .ThenBy(v => v.Detail, StringComparer.Ordinal)
            .ToArray();
    }

    // ---- E-A root structure -----------------------------------------------------------------

    private static void CheckRoot(string root, List<FractalSpecViolation> violations)
    {
        if (!File.Exists(Path.Combine(root, "index.md")))
        {
            violations.Add(new("E-A1", "index.md", "docs root must carry index.md"));
        }

        if (!File.Exists(Path.Combine(root, "migration-map.md")))
        {
            violations.Add(new("E-A1", "migration-map.md", "docs root must carry migration-map.md"));
        }

        foreach (var file in Directory.GetFiles(root, "*.md"))
        {
            var name = Path.GetFileName(file);
            if (name is not ("index.md" or "migration-map.md"))
            {
                violations.Add(new("E-A1", name, "root layer .md files are exactly index.md + migration-map.md"));
            }
        }

        if (!Directory.Exists(Path.Combine(root, "impl")))
        {
            violations.Add(new("E-A1", "impl", "docs root must carry the impl/ knowledge system"));
        }

        // E-A3: planes live under impl/ — no docs/<plane>/ form (modeling/ and _assets/ allowed).
        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (name is not ("impl" or "modeling" or "_assets"))
            {
                violations.Add(new("E-A3", name,
                    "root layer directories are impl/, modeling/ and _assets/ only — planes belong under impl/<plane>/"));
            }
        }

        if (!Directory.Exists(Path.Combine(root, "impl", "global")))
        {
            violations.Add(new("E-A4", "impl/global", "the recommended global plane must exist"));
        }
    }

    private static IEnumerable<string> EnumeratePlanes(string root)
    {
        var impl = Path.Combine(root, "impl");
        return Directory.Exists(impl) ? Directory.GetDirectories(impl) : [];
    }

    // ---- E-B/E-C/E-G2/E-G4 plane recursion ---------------------------------------------------

    private static void CheckPlane(string root, string planeDir, List<FractalSpecViolation> violations)
    {
        // E-B1: plane first level (besides index.md) carries category directories only.
        foreach (var file in Directory.GetFiles(planeDir, "*.md"))
        {
            if (Path.GetFileName(file) != "index.md")
            {
                violations.Add(new("E-B1", Relative(root, file),
                    "plane first level must not carry stray leaf .md files (categories only)"));
            }
        }

        var singleLeafFolders = 0;
        var topicFolders = 0;
        foreach (var categoryDir in Directory.GetDirectories(planeDir))
        {
            var category = Path.GetFileName(categoryDir);
            var categoryRelative = Relative(root, categoryDir);

            // E-C1: no empty categories — at least one non-index leaf somewhere below.
            var leaves = Directory.GetFiles(categoryDir, "*.md", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) != "index.md")
                .ToArray();
            if (leaves.Length == 0)
            {
                violations.Add(new("E-C1", categoryRelative,
                    "empty category (index.md without any leaf): 缺哪类就不建"));
            }

            // E-D4 (also E-B3 [P] part / E-C2): custom categories need a FULL manifest block.
            if (!DefaultCategories.Contains(category))
            {
                var index = Path.Combine(categoryDir, "index.md");
                if (!File.Exists(index) || !HasFullManifest(ReadBody(index)))
                {
                    violations.Add(new("E-D4", categoryRelative,
                        $"custom category '{category}' must declare itself with a full 目录职责 section in index.md"));
                }
            }

            // E-B2: <category>/<topic>.md or <category>/<topic>/<leaf>.md — no deeper nesting.
            foreach (var topicDir in Directory.GetDirectories(categoryDir))
            {
                topicFolders++;
                foreach (var deeper in Directory.GetDirectories(topicDir))
                {
                    violations.Add(new("E-B2", Relative(root, deeper),
                        "depth exceeds 类目/主题/叶子: topic folders hold flat leaf .md files only"));
                }

                var topicLeaves = Directory.GetFiles(topicDir, "*.md")
                    .Count(path => Path.GetFileName(path) != "index.md");
                if (topicLeaves == 1)
                {
                    singleLeafFolders++;
                }
            }
        }

        // E-G4 [P] approximation: mostly single-leaf topic folders suggest premature splitting.
        if (topicFolders >= 2 && singleLeafFolders > topicFolders * SingleLeafFolderRatioThreshold)
        {
            violations.Add(new("E-G4", Relative(root, planeDir),
                $"{singleLeafFolders}/{topicFolders} topic folders carry a single leaf — leaves should start as single files"));
        }
    }

    // ---- E-G1/E-G2 evolution invariants -------------------------------------------------------

    private static void CheckEvolution(string root, List<FractalSpecViolation> violations)
    {
        foreach (var dir in EnumerateEngineeringDirectories(root))
        {
            // E-G1: <name>.md and <name>/ must not coexist at the same level.
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                if (File.Exists(subDir + ".md"))
                {
                    violations.Add(new("E-G1", Relative(root, subDir),
                        "sibling <name>.md and <name>/ coexist — the upgrade must absorb the single file"));
                }
            }

            // E-G2: every structural directory carries index.md (upgraded folders included);
            // no pre-built empty directories.
            if (string.Equals(dir, root, StringComparison.Ordinal))
            {
                continue; // root index.md already covered by E-A1.
            }

            if (Directory.GetFileSystemEntries(dir).Length == 0)
            {
                violations.Add(new("E-G2", Relative(root, dir), "empty directory: 不预建更深空目录"));
            }
            else if (!File.Exists(Path.Combine(dir, "index.md")))
            {
                violations.Add(new("E-G2", Relative(root, dir), "directory lacks index.md"));
            }
        }
    }

    // ---- E-A2/E-G3 migration map ---------------------------------------------------------------

    private static void CheckMigrationMap(string root, List<FractalSpecViolation> violations)
    {
        var path = Path.Combine(root, "migration-map.md");
        if (!File.Exists(path))
        {
            return; // absence already reported as E-A1.
        }

        var content = File.ReadAllText(path);
        var parts = SplitFrontmatter(content);
        var fields = parts is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : ParseFields(parts.Value.Frontmatter);
        foreach (var (key, expected) in new[]
        {
            ("knowledge_system", "impl"), ("knowledge_plane", "global"),
            ("doc_role", "reference"), ("status", "active"),
        })
        {
            if (!string.Equals(fields.GetValueOrDefault(key), expected, StringComparison.Ordinal))
            {
                violations.Add(new("E-A2", "migration-map.md",
                    $"frontmatter must carry {key}: {expected} (got '{fields.GetValueOrDefault(key) ?? "<missing>"}')"));
            }
        }

        if (!DatePattern.IsMatch(fields.GetValueOrDefault("last_verified") ?? ""))
        {
            violations.Add(new("E-A2", "migration-map.md", "frontmatter must carry a YYYY-MM-DD last_verified"));
        }

        if (!content.Contains("| Old Path | New Path | Status | Notes |", StringComparison.Ordinal))
        {
            violations.Add(new("E-A2", "migration-map.md",
                "body must carry the | Old Path | New Path | Status | Notes | table header"));
            return;
        }

        foreach (var line in content.Split('\n'))
        {
            var cells = ParseTableRow(line);
            if (cells is null || cells.Length < 4 || cells[0] is "Old Path")
            {
                continue;
            }

            var status = cells[2];
            if (!MigrationStatuses.Contains(status))
            {
                violations.Add(new("E-A2", "migration-map.md",
                    $"Status '{status}' outside the controlled set {{migrated, absorbed, compat, deprecated}}"));
            }

            // E-G3 (directory-checkable slice): a migrated row's New Path must exist.
            if (string.Equals(status, "migrated", StringComparison.Ordinal)
                && !File.Exists(Path.Combine(root, cells[1].Replace('/', Path.DirectorySeparatorChar))))
            {
                violations.Add(new("E-G3", "migration-map.md",
                    $"migrated row target '{cells[1]}' does not exist under the docs root"));
            }
        }
    }

    /// <summary>Splits a markdown table row into trimmed cells; null when the line is no data row.</summary>
    private static string[]? ParseTableRow(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|') || trimmed.All(ch => ch is '|' or '-' or ' ' or ':'))
        {
            return null;
        }

        return trimmed[1..^1].Split('|').Select(cell => cell.Trim()).ToArray();
    }

    // ---- per-document checks (E-D/E-E/E-F/E-H) -------------------------------------------------

    private static void CheckDocument(string root, string file, List<FractalSpecViolation> violations)
    {
        var relative = Relative(root, file);
        var content = File.ReadAllText(file);
        var parts = SplitFrontmatter(content);

        // E-E1: frontmatter with the four fixed fields.
        if (parts is null)
        {
            violations.Add(new("E-E1", relative, "document must start with a frontmatter block"));
            return;
        }

        var fields = ParseFields(parts.Value.Frontmatter);
        CheckFrontmatter(relative, parts.Value.Frontmatter, fields, violations);
        CheckDocRolePlacement(relative, fields, violations);

        var body = parts.Value.Body;
        if (Path.GetFileName(file) == "index.md")
        {
            CheckManifest(root, relative, body, violations);
            CheckIndexNavigationOnly(relative, body, violations);
        }

        CheckModelingLinks(root, file, relative, body, violations);
    }

    private static void CheckFrontmatter(
        string relative,
        IReadOnlyList<string> lines,
        Dictionary<string, string> fields,
        List<FractalSpecViolation> violations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            // E-E3: no YAML arrays / multi-line values (indent or list-item continuation lines).
            if (line.StartsWith(' ') || line.StartsWith('-') || line.StartsWith('\t'))
            {
                violations.Add(new("E-E3", relative, $"frontmatter carries a multi-line/array value ('{line.Trim()}')"));
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                violations.Add(new("E-E1", relative, $"frontmatter line '{line}' is not key: value"));
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key is "code_paths" or "topics")
            {
                violations.Add(new("E-E3", relative, $"forbidden array-accumulation field '{key}' in frontmatter"));
            }

            if (value.StartsWith('['))
            {
                violations.Add(new("E-E3", relative, $"frontmatter field {key} must not be a YAML array"));
            }

            if (!seen.Add(key))
            {
                violations.Add(new("E-E3", relative, $"frontmatter field {key} appears more than once (one line max)"));
            }

            // E-E4 / M-E3: modeling-only fields never appear on engineering/impl documents.
            if (key is "context" or "derived_from")
            {
                violations.Add(new("E-E4", relative, $"modeling-only frontmatter field '{key}' on an impl document"));
                violations.Add(new("M-E3", relative, $"'{key}:' may only appear under docs/modeling/** (found on an impl document)"));
            }
        }

        foreach (var required in new[] { "knowledge_plane", "doc_role", "status", "last_verified" })
        {
            if (!fields.ContainsKey(required))
            {
                violations.Add(new("E-E1", relative, $"frontmatter lacks the fixed field {required}"));
            }
        }

        // E-E2: controlled value domains.
        if (fields.TryGetValue("doc_role", out var role) && !AllowedDocRoles.Contains(role))
        {
            violations.Add(new("E-E2", relative, $"doc_role '{role}' outside the controlled domain"));
        }

        if (fields.TryGetValue("status", out var status) && !AllowedStatuses.Contains(status))
        {
            violations.Add(new("E-E2", relative, $"status '{status}' outside the controlled domain"));
        }

        if (fields.TryGetValue("last_verified", out var verified) && !DatePattern.IsMatch(verified))
        {
            violations.Add(new("E-E2", relative, $"last_verified '{verified}' must be YYYY-MM-DD"));
        }

        // E-E5: knowledge_plane matches the plane path segment (impl/<plane>/**).
        var segments = relative.Split('/');
        if (segments.Length >= 3 && segments[0] == "impl"
            && fields.TryGetValue("knowledge_plane", out var plane)
            && !string.Equals(plane, segments[1], StringComparison.Ordinal))
        {
            violations.Add(new("E-E5", relative,
                $"knowledge_plane '{plane}' does not match the plane path segment '{segments[1]}'"));
        }
    }

    /// <summary>E-E6: doc_role matches the hosting category; every index.md is a guide.</summary>
    private static void CheckDocRolePlacement(string relative, Dictionary<string, string> fields, List<FractalSpecViolation> violations)
    {
        if (!fields.TryGetValue("doc_role", out var role))
        {
            return; // E-E1 already reported.
        }

        var segments = relative.Split('/');
        string? expected = null;
        if (segments[^1] == "index.md")
        {
            expected = "guide";
        }
        else if (segments.Length >= 4 && segments[0] == "impl"
            && CategoryRoles.TryGetValue(segments[2], out var categoryRole))
        {
            expected = categoryRole;
        }

        if (expected is not null && !string.Equals(role, expected, StringComparison.Ordinal))
        {
            violations.Add(new("E-E6", relative, $"doc_role should be {expected} here (got {role})"));
        }
    }

    // ---- E-D manifest blocks --------------------------------------------------------------------

    private static void CheckManifest(string root, string relative, string body, List<FractalSpecViolation> violations) =>
        CheckManifest(relative, body, violations, missingRule: "E-D1", slimRule: "E-D2", fullRule: "E-D3");

    /// <summary>Manifest-block checks shared by both sides: engineering reports E-D1/E-D2/E-D3, modeling M-D2 (fixtures: 同 E-D1~E-D5).</summary>
    private static void CheckManifest(
        string relative,
        string body,
        List<FractalSpecViolation> violations,
        string missingRule,
        string slimRule,
        string fullRule)
    {
        var slimLines = body.Split('\n').Where(line => line.StartsWith("> 目录职责", StringComparison.Ordinal)).ToArray();
        var hasValidSlim = false;
        foreach (var line in slimLines)
        {
            // E-D2: a slim block must match the exact single-line format.
            if (SlimManifestPattern.IsMatch(line))
            {
                hasValidSlim = true;
            }
            else
            {
                violations.Add(new(slimRule, relative, "slim 目录职责 block does not match the required single-line format"));
            }
        }

        var hasFullSection = body.Contains("## 目录职责", StringComparison.Ordinal);
        var hasValidFull = HasFullManifest(body);
        if (hasFullSection && !hasValidFull)
        {
            violations.Add(new(fullRule, relative,
                "full 目录职责 section lacks required **holds**/**excludes**/**tier**(stable|dated) entries"));
        }

        // E-D1: some manifest block must sit on every index.md.
        if (slimLines.Length == 0 && !hasFullSection)
        {
            violations.Add(new(missingRule, relative, "index.md carries no 目录职责 block (slim or full)"));
            return;
        }

        // E-D3: structure nodes (docs root, knowledge-system root, plane roots) use the full form.
        var depth = relative.Split('/').Length - 1; // index.md at root=0, impl=1, impl/<plane>=2.
        if (depth <= 2 && !hasValidFull)
        {
            violations.Add(new(fullRule, relative, "structure-node index.md must use the full 目录职责 section"));
        }
    }

    private static bool HasFullManifest(string body) =>
        body.Contains("## 目录职责", StringComparison.Ordinal)
        && body.Contains("**holds**", StringComparison.Ordinal)
        && body.Contains("**excludes**", StringComparison.Ordinal)
        && Regex.IsMatch(body, @"\*\*tier\*\*[：:]\s*`?(stable|dated)`?");

    // ---- E-F1 index navigation-only --------------------------------------------------------------

    private static void CheckIndexNavigationOnly(string relative, string body, List<FractalSpecViolation> violations) =>
        CheckIndexNavigationOnly(relative, body, violations, "E-F1");

    private static void CheckIndexNavigationOnly(string relative, string body, List<FractalSpecViolation> violations, string ruleId)
    {
        var prose = 0;
        var inFence = false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || line.Length == 0)
            {
                continue;
            }

            // Navigation/structure lines: headings, manifest blockquotes, tables, list items.
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#') || trimmed.StartsWith('>') || trimmed.StartsWith('|')
                || trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal)
                || Regex.IsMatch(trimmed, @"^\d+\. "))
            {
                continue;
            }

            prose += trimmed.Length;
        }

        if (prose > IndexProseBudget)
        {
            violations.Add(new(ruleId, relative,
                $"index.md carries {prose} chars of prose outside navigation structures (budget {IndexProseBudget}): 真源应在叶子"));
        }
    }

    // ---- E-H1 modeling link resolution ------------------------------------------------------------

    private static void CheckModelingLinks(string root, string file, string relative, string body, List<FractalSpecViolation> violations)
    {
        var fileDir = Path.GetDirectoryName(file)!;
        foreach (Match match in LinkPattern.Matches(body))
        {
            var target = match.Groups[1].Value;
            if (target.StartsWith("http://", StringComparison.Ordinal)
                || target.StartsWith("https://", StringComparison.Ordinal)
                || target.StartsWith("mailto:", StringComparison.Ordinal))
            {
                continue;
            }

            string resolved;
            try
            {
                resolved = Path.GetFullPath(Path.Combine(fileDir, target.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (ArgumentException)
            {
                continue;
            }

            var resolvedRelative = Path.GetRelativePath(root, resolved).Replace(Path.DirectorySeparatorChar, '/');
            if (resolvedRelative.StartsWith("modeling/", StringComparison.Ordinal) && !File.Exists(resolved))
            {
                violations.Add(new("E-H1", relative,
                    $"modeling concept link '{target}' does not resolve to an existing docs/modeling file"));
            }
        }
    }

    // ---- M-* modeling checks (add-llm-wiki-modeling-fractal T2.1, fixtures §三) ------------------

    private static readonly HashSet<string> DomainCategories = new(StringComparer.Ordinal)
    {
        "objects", "policies", "workflows",
    };

    /// <summary>Source-code path shape (M-D3): code paths belong in code-map.md / prose, never in frontmatter.</summary>
    private static readonly Regex CodePathPattern = new(
        @"\.(cs|fs|vb|ts|tsx|js|jsx|mjs|py|rs|go|java|c|h|cpp|hpp)(:\d+)?$",
        RegexOptions.CultureInvariant);

    /// <summary>`path:line` cell shape of a code-map mapping table (M-D1).</summary>
    private static readonly Regex CodeMapLocationPattern = new(
        @"`([^`\s:]+):(\d+)`", RegexOptions.CultureInvariant);

    /// <summary>Entry point of the modeling side: no-op when the root has no modeling/ knowledge system.</summary>
    private static void CheckModeling(string root, string? repositoryRoot, List<FractalSpecViolation> violations)
    {
        var modeling = Path.Combine(root, "modeling");
        if (!Directory.Exists(modeling))
        {
            return; // engineering-only root: M-* does not apply.
        }

        // M-A1: the canonical domain plane is the one mandatory plane.
        if (!Directory.Exists(Path.Combine(modeling, "domain")))
        {
            violations.Add(new("M-A1", "modeling/domain", "docs/modeling/domain/ (the mandatory canonical plane) must exist"));
        }

        foreach (var planeDir in Directory.GetDirectories(modeling).Order(StringComparer.Ordinal))
        {
            CheckModelingPlane(root, planeDir, repositoryRoot, violations);
        }

        CheckModelingEvolution(root, modeling, violations);
        foreach (var file in EnumerateModelingMarkdown(root))
        {
            CheckModelingDocument(root, file, violations);
        }
    }

    private static void CheckModelingPlane(string root, string planeDir, string? repositoryRoot, List<FractalSpecViolation> violations)
    {
        var plane = Path.GetFileName(planeDir);
        var planeRelative = Relative(root, planeDir);
        var isDomain = string.Equals(plane, "domain", StringComparison.Ordinal);

        // M-A2: plane root fixed three — index.md, glossary.md, contexts/index.md.
        foreach (var required in new[] { "index.md", "glossary.md", Path.Combine("contexts", "index.md") })
        {
            if (!File.Exists(Path.Combine(planeDir, required)))
            {
                violations.Add(new("M-A2", $"{planeRelative}/{required.Replace(Path.DirectorySeparatorChar, '/')}",
                    "plane root must carry the fixed three: index.md, glossary.md, contexts/index.md"));
            }
        }

        CheckModelingGlossary(root, planeDir, isDomain, violations);

        var contextsDir = Path.Combine(planeDir, "contexts");
        if (!Directory.Exists(contextsDir))
        {
            return;
        }

        var contextDirs = Directory.GetDirectories(contextsDir).Order(StringComparer.Ordinal).ToArray();
        foreach (var contextDir in contextDirs)
        {
            CheckModelingContext(root, contextDir, isDomain, contextDirs.Length, repositoryRoot, violations);
        }
    }

    /// <summary>M-F3: domain glossary terms carry no derived_from references; derived glossary terms link back into domain.</summary>
    private static void CheckModelingGlossary(string root, string planeDir, bool isDomain, List<FractalSpecViolation> violations)
    {
        var path = Path.Combine(planeDir, "glossary.md");
        if (!File.Exists(path))
        {
            return; // absence already reported as M-A2.
        }

        var relative = Relative(root, path);
        var body = ReadBody(path);
        if (isDomain)
        {
            if (body.Contains("derived_from", StringComparison.Ordinal))
            {
                violations.Add(new("M-F3", relative, "domain glossary terms must not carry derived_from references"));
            }

            return;
        }

        // Derived plane: every term entry (list item or table data row) links back to a domain term.
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            var isEntry = trimmed.StartsWith("- ", StringComparison.Ordinal)
                || (ParseTableRow(line) is { } cells && cells.Length > 0 && !trimmed.Contains("---", StringComparison.Ordinal)
                    && !string.Equals(cells[0], "Term", StringComparison.OrdinalIgnoreCase));
            if (!isEntry)
            {
                continue;
            }

            var resolvesToDomain = false;
            foreach (Match match in LinkPattern.Matches(trimmed))
            {
                var target = match.Groups[1].Value;
                var resolved = ResolveDocPath(root, Path.GetDirectoryName(path)!, target);
                if (resolved is not null
                    && resolved.StartsWith("modeling/domain/", StringComparison.Ordinal)
                    && File.Exists(Path.Combine(root, resolved.Replace('/', Path.DirectorySeparatorChar))))
                {
                    resolvesToDomain = true;
                    break;
                }
            }

            if (!resolvesToDomain)
            {
                violations.Add(new("M-F3", relative,
                    $"derived glossary term entry '{Truncate(trimmed)}' carries no resolvable link back to a docs/modeling/domain term"));
            }
        }
    }

    private static void CheckModelingContext(
        string root,
        string contextDir,
        bool isDomain,
        int contextCount,
        string? repositoryRoot,
        List<FractalSpecViolation> violations)
    {
        var contextRelative = Relative(root, contextDir);

        // M-B1: every context carries index.md + code-map.md.
        foreach (var required in new[] { "index.md", "code-map.md" })
        {
            if (!File.Exists(Path.Combine(contextDir, required)))
            {
                violations.Add(new("M-B1", $"{contextRelative}/{required}",
                    "every context directory must carry index.md and code-map.md"));
            }
        }

        // M-B3: context first level carries category directories only (besides the fixed two files).
        foreach (var file in Directory.GetFiles(contextDir, "*.md"))
        {
            if (Path.GetFileName(file) is not ("index.md" or "code-map.md"))
            {
                violations.Add(new("M-B3", Relative(root, file),
                    "context first level must not carry stray leaf .md files (categories only)"));
            }
        }

        var categoryDirs = Directory.GetDirectories(contextDir).Order(StringComparer.Ordinal).ToArray();

        // M-B4: first-level category count stays within 3–6 (1–2 allowed with a generator-side
        // warning; the [P] violation is exceeding the ceiling).
        if (categoryDirs.Length > 6)
        {
            violations.Add(new("M-B4", contextRelative,
                $"{categoryDirs.Length} first-level categories exceed the 3–6 budget"));
        }

        // M-B2: Boundary mandatory; Not Owned Here mandatory with adjacent contexts; the index
        // navigates every category.
        var indexPath = Path.Combine(contextDir, "index.md");
        if (File.Exists(indexPath))
        {
            var indexBody = ReadBody(indexPath);
            if (!indexBody.Contains("## Boundary", StringComparison.Ordinal))
            {
                violations.Add(new("M-B2", $"{contextRelative}/index.md", "context index.md must carry a ## Boundary section"));
            }

            if (contextCount > 1 && !indexBody.Contains("## Not Owned Here", StringComparison.Ordinal))
            {
                violations.Add(new("M-B2", $"{contextRelative}/index.md",
                    "## Not Owned Here is mandatory when adjacent contexts exist"));
            }

            foreach (var categoryDir in categoryDirs)
            {
                var category = Path.GetFileName(categoryDir);
                if (!indexBody.Contains($"{category}/index.md", StringComparison.Ordinal))
                {
                    violations.Add(new("M-B2", $"{contextRelative}/index.md",
                        $"context index.md must navigate to its category '{category}' ({category}/index.md)"));
                }
            }
        }

        foreach (var categoryDir in categoryDirs)
        {
            CheckModelingCategory(root, categoryDir, isDomain, violations);
        }

        CheckModelingCodeMap(root, contextDir, repositoryRoot, violations);
    }

    private static void CheckModelingCategory(string root, string categoryDir, bool isDomain, List<FractalSpecViolation> violations)
    {
        var category = Path.GetFileName(categoryDir);
        var categoryRelative = Relative(root, categoryDir);

        // M-C1: no empty categories (缺哪类不建); non-default domain categories must declare
        // themselves with a full 目录职责 section (联动 M-D2).
        var leaves = Directory.GetFiles(categoryDir, "*.md", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "index.md")
            .ToArray();
        if (leaves.Length == 0)
        {
            violations.Add(new("M-C1", categoryRelative, "empty category (index.md without any leaf): 缺哪类就不建"));
        }

        if (isDomain && !DomainCategories.Contains(category))
        {
            var index = Path.Combine(categoryDir, "index.md");
            if (!File.Exists(index) || !HasFullManifest(ReadBody(index)))
            {
                violations.Add(new("M-C1", categoryRelative,
                    $"non-default domain category '{category}' must declare itself with a full 目录职责 section in index.md"));
            }
        }

        if (string.Equals(category, "objects", StringComparison.Ordinal))
        {
            // M-C2: objects/<object>/data.md+behavior.md OR objects/<object>.md — never both forms
            // for the same name; the folder form must actually carry data.md/behavior.md.
            foreach (var objectDir in Directory.GetDirectories(categoryDir))
            {
                if (File.Exists(objectDir + ".md"))
                {
                    violations.Add(new("M-C2", Relative(root, objectDir),
                        "objects/<name>/ and objects/<name>.md coexist — pick one object form"));
                }

                if (!File.Exists(Path.Combine(objectDir, "data.md")) && !File.Exists(Path.Combine(objectDir, "behavior.md")))
                {
                    violations.Add(new("M-C2", Relative(root, objectDir),
                        "the object folder form must carry data.md and/or behavior.md"));
                }
            }
        }
        else if (DomainCategories.Contains(category))
        {
            // M-C3: policies/workflows leaves are single files; an evolved topic folder holds flat
            // leaf files only (no deeper nesting).
            foreach (var topicDir in Directory.GetDirectories(categoryDir))
            {
                foreach (var deeper in Directory.GetDirectories(topicDir))
                {
                    violations.Add(new("M-C3", Relative(root, deeper),
                        $"{category}/ leaves are single files (evolved topic folders hold flat .md leaves only)"));
                }
            }
        }
    }

    /// <summary>M-D1: code-map.md exists, is non-empty, carries a symbol→source mapping table, and (with a repository root) every mapped path resolves.</summary>
    private static void CheckModelingCodeMap(string root, string contextDir, string? repositoryRoot, List<FractalSpecViolation> violations)
    {
        var path = Path.Combine(contextDir, "code-map.md");
        if (!File.Exists(path))
        {
            return; // absence already reported as M-B1.
        }

        var relative = Relative(root, path);
        var body = ReadBody(path);
        var locations = CodeMapLocationPattern.Matches(body);
        if (body.Trim().Length == 0 || locations.Count == 0)
        {
            violations.Add(new("M-D1", relative, "code-map.md must carry a non-empty symbol/file → source path mapping table"));
            return;
        }

        if (repositoryRoot is null)
        {
            return; // path resolution slice needs the source repository root.
        }

        foreach (var mapped in locations.Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(repositoryRoot, mapped.Replace('/', Path.DirectorySeparatorChar))))
            {
                violations.Add(new("M-D1", relative, $"mapped source path '{mapped}' does not exist under the repository root"));
            }
        }
    }

    /// <summary>M-F2: 同名文件夹演化规则同 E-G1~E-G3 over the modeling tree (object data/behavior folders exempt from index.md).</summary>
    private static void CheckModelingEvolution(string root, string modeling, List<FractalSpecViolation> violations)
    {
        var stack = new Stack<string>([modeling]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                stack.Push(subDir);
                if (File.Exists(subDir + ".md"))
                {
                    violations.Add(new("M-F2", Relative(root, subDir),
                        "sibling <name>.md and <name>/ coexist — the upgrade must absorb the single file"));
                }
            }

            if (string.Equals(dir, modeling, StringComparison.Ordinal))
            {
                continue;
            }

            if (Directory.GetFileSystemEntries(dir).Length == 0)
            {
                violations.Add(new("M-F2", Relative(root, dir), "empty directory: 不预建更深空目录"));
            }
            else if (!File.Exists(Path.Combine(dir, "index.md")) && !IsObjectFormFolder(dir))
            {
                violations.Add(new("M-F2", Relative(root, dir), "directory lacks index.md"));
            }
        }
    }

    /// <summary>objects/&lt;object&gt;/ folders using the data.md/behavior.md form need no index.md (M-C2 form A).</summary>
    private static bool IsObjectFormFolder(string dir) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(dir)), "objects", StringComparison.Ordinal)
        && (File.Exists(Path.Combine(dir, "data.md")) || File.Exists(Path.Combine(dir, "behavior.md")));

    /// <summary>Per-document modeling checks: M-E1/M-E2/M-E4/M-E5/M-D3 frontmatter rules plus M-F1/M-D2 on index pages.</summary>
    private static void CheckModelingDocument(string root, string file, List<FractalSpecViolation> violations)
    {
        var relative = Relative(root, file);
        var segments = relative.Split('/');
        var plane = segments.Length >= 2 ? segments[1] : "";
        var isDomain = string.Equals(plane, "domain", StringComparison.Ordinal);
        var isPlaneDoc = segments.Length >= 3; // below modeling/<plane>/
        var content = File.ReadAllText(file);
        var parts = SplitFrontmatter(content);
        if (parts is null)
        {
            violations.Add(new("M-E1", relative, "modeling document must start with a frontmatter block"));
            return;
        }

        var fields = ParseFields(parts.Value.Frontmatter);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in parts.Value.Frontmatter)
        {
            if (line.StartsWith(' ') || line.StartsWith('-') || line.StartsWith('\t'))
            {
                violations.Add(new("M-E1", relative, $"frontmatter carries a multi-line/array value ('{line.Trim()}')"));
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                violations.Add(new("M-E1", relative, $"frontmatter line '{line}' is not key: value"));
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (!seen.Add(key))
            {
                var rule = string.Equals(key, "derived_from", StringComparison.Ordinal) ? "M-E2" : "M-E1";
                violations.Add(new(rule, relative, $"frontmatter field {key} appears more than once (one line max)"));
            }

            if (value.StartsWith('['))
            {
                var rule = string.Equals(key, "derived_from", StringComparison.Ordinal) ? "M-E2" : "M-E1";
                violations.Add(new(rule, relative, $"frontmatter field {key} must not be a YAML array"));
            }

            // M-D3: code paths never sit in modeling frontmatter (derived_from is a docs path).
            if (!string.Equals(key, "derived_from", StringComparison.Ordinal) && CodePathPattern.IsMatch(value))
            {
                violations.Add(new("M-D3", relative,
                    $"code path '{value}' in modeling frontmatter (belongs in code-map.md or prose)"));
            }
        }

        // M-E1: the common four fixed fields (同 E-E1/E-E2) with controlled value domains.
        foreach (var required in new[] { "knowledge_plane", "doc_role", "status", "last_verified" })
        {
            if (!fields.ContainsKey(required))
            {
                violations.Add(new("M-E1", relative, $"frontmatter lacks the fixed field {required}"));
            }
        }

        if (fields.TryGetValue("doc_role", out var role) && !AllowedDocRoles.Contains(role))
        {
            violations.Add(new("M-E1", relative, $"doc_role '{role}' outside the controlled domain"));
        }

        if (fields.TryGetValue("status", out var status) && !AllowedStatuses.Contains(status))
        {
            violations.Add(new("M-E1", relative, $"status '{status}' outside the controlled domain"));
        }

        if (fields.TryGetValue("last_verified", out var verified) && !DatePattern.IsMatch(verified))
        {
            violations.Add(new("M-E1", relative, $"last_verified '{verified}' must be YYYY-MM-DD"));
        }

        if (isPlaneDoc && fields.TryGetValue("knowledge_plane", out var declaredPlane)
            && !string.Equals(declaredPlane, plane, StringComparison.Ordinal))
        {
            violations.Add(new("M-E1", relative,
                $"knowledge_plane '{declaredPlane}' does not match the plane path segment '{plane}'"));
        }

        // M-E1: context: mandatory on context-scoped documents (plane root three exempt).
        var isContextScoped = segments.Length >= 5 && string.Equals(segments[2], "contexts", StringComparison.Ordinal);
        if (isContextScoped && !fields.ContainsKey("context"))
        {
            violations.Add(new("M-E1", relative, "context-scoped modeling document must carry context: in frontmatter"));
        }

        // M-E2/M-A3/M-E4: derived_from discipline.
        var hasDerivedFrom = fields.TryGetValue("derived_from", out var derivedFrom);
        if (isDomain && hasDerivedFrom)
        {
            violations.Add(new("M-E2", relative, "domain documents must not carry derived_from (they ARE the truth source)"));
        }
        else if (!isDomain && isPlaneDoc)
        {
            var isIndex = string.Equals(segments[^1], "index.md", StringComparison.Ordinal);
            if (!hasDerivedFrom && !isIndex && !string.Equals(segments[^1], "glossary.md", StringComparison.Ordinal))
            {
                violations.Add(new("M-A3", relative, "derived-plane non-index document must carry derived_from:"));
            }

            if (hasDerivedFrom)
            {
                if (derivedFrom!.Contains(',') || derivedFrom.Contains(' '))
                {
                    violations.Add(new("M-E2", relative, "derived_from must be a single value (one path, one line)"));
                }
                else
                {
                    var target = derivedFrom.StartsWith("docs/", StringComparison.Ordinal) ? derivedFrom[5..] : derivedFrom;
                    if (!target.StartsWith("modeling/domain/", StringComparison.Ordinal)
                        || !File.Exists(Path.Combine(root, target.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        violations.Add(new("M-E4", relative,
                            $"derived_from target '{derivedFrom}' must exist under docs/modeling/domain/"));
                    }
                }
            }
        }

        // M-E5: category leaves are canonical on the domain plane, derived on derived planes.
        if (isContextScoped && segments.Length >= 6 && DomainCategories.Contains(segments[4])
            && !string.Equals(segments[^1], "index.md", StringComparison.Ordinal)
            && fields.TryGetValue("doc_role", out var leafRole))
        {
            var expected = isDomain ? "canonical" : "derived";
            if (!string.Equals(leafRole, expected, StringComparison.Ordinal))
            {
                violations.Add(new("M-E5", relative, $"category leaf doc_role should be {expected} here (got {leafRole})"));
            }
        }

        if (string.Equals(segments[^1], "index.md", StringComparison.Ordinal))
        {
            // M-D2: 职责块规则同 E-D1~E-D5; M-F1: index 只导航 (同 E-F1).
            CheckManifest(relative, parts.Value.Body, violations, missingRule: "M-D2", slimRule: "M-D2", fullRule: "M-D2");
            CheckIndexNavigationOnly(relative, parts.Value.Body, violations, "M-F1");
        }
    }

    /// <summary>Resolves a relative markdown link against the docs root; null when unresolvable.</summary>
    private static string? ResolveDocPath(string root, string fromDir, string target)
    {
        if (target.StartsWith("http://", StringComparison.Ordinal)
            || target.StartsWith("https://", StringComparison.Ordinal)
            || target.StartsWith("mailto:", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var resolved = Path.GetFullPath(Path.Combine(fromDir, target.Replace('/', Path.DirectorySeparatorChar)));
            return Path.GetRelativePath(root, resolved).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Truncate(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private static IEnumerable<string> EnumerateModelingMarkdown(string root)
    {
        var modeling = Path.Combine(root, "modeling");
        return Directory.Exists(modeling)
            ? Directory.GetFiles(modeling, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            : [];
    }

    // ---- traversal helpers -------------------------------------------------------------------------

    /// <summary>Engineering scope: the docs root plus impl/**; modeling/** (G4) and _assets/** skipped.</summary>
    private static IEnumerable<string> EnumerateEngineeringDirectories(string root)
    {
        yield return root;
        var stack = new Stack<string>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (Path.GetFileName(dir) is "modeling" or "_assets")
            {
                continue;
            }

            stack.Push(dir);
        }

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            yield return dir;
            foreach (var child in Directory.GetDirectories(dir))
            {
                stack.Push(child);
            }
        }
    }

    private static IEnumerable<string> EnumerateEngineeringMarkdown(string root) =>
        EnumerateEngineeringDirectories(root)
            .SelectMany(dir => Directory.GetFiles(dir, "*.md"))
            .Order(StringComparer.Ordinal);

    /// <summary>File body with any leading frontmatter stripped (missing file → empty).</summary>
    private static string ReadBody(string path)
    {
        if (!File.Exists(path))
        {
            return "";
        }

        var content = File.ReadAllText(path);
        return SplitFrontmatter(content)?.Body ?? content;
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static (IReadOnlyList<string> Frontmatter, string Body)? SplitFrontmatter(string content)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            return null;
        }

        var end = content.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        return (content[4..end].Split('\n'), content[(end + 5)..]);
    }

    private static Dictionary<string, string> ParseFields(IReadOnlyList<string> lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && !line.StartsWith(' ') && !line.StartsWith('-'))
            {
                fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return fields;
    }
}
