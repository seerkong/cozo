using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.Depa;

namespace Cozo.DotNet.LlmWiki.Tools;

/// <summary>
/// Read-only projection of a CodeKnowledge/DEPA database.  The exporter deliberately owns no
/// indexing or scan logic: callers must opt into a scan before passing its report here.
/// </summary>
public sealed class DepaOntologyExporter
{
    public async Task<DepaExportResult> ExportAsync(
        CozoOm om,
        DepaExportRequest request,
        DepaConformanceReport? report = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentNullException.ThrowIfNull(request);

        var format = request.Format.Trim().ToLowerInvariant();
        if (format is not ("runtime-snapshot" or "ontology-xml" or "wiki"))
        {
            throw new ArgumentException("Invalid export format. Expected runtime-snapshot, ontology-xml, or wiki.", nameof(request));
        }

        var output = PrepareEmptyOutputDirectory(request.OutputDirectory);
        var snapshot = await CollectAsync(om, request, report, cancellationToken);
        var files = format switch
        {
            "runtime-snapshot" => await WriteRuntimeSnapshotAsync(output, snapshot, cancellationToken),
            "ontology-xml" => await WriteOntologyAsync(output, snapshot, cancellationToken),
            "wiki" => await WriteWikiAsync(output, snapshot, cancellationToken),
            _ => throw new InvalidOperationException("Unexpected export format."),
        };

        return new DepaExportResult(
            format,
            output,
            files.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            snapshot.Entities.Count,
            snapshot.Edges.Count,
            snapshot.Evidence.Count,
            snapshot.Diagnostics);
    }

    private static async Task<DepaSnapshot> CollectAsync(
        CozoOm om,
        DepaExportRequest request,
        DepaConformanceReport? report,
        CancellationToken cancellationToken)
    {
        var relationNames = await RelationNamesAsync(om, cancellationToken);
        if (!relationNames.Contains("om_entity"))
        {
            throw new InvalidOperationException(
                "The selected database has no OM schema. Run depa-wiki index and depa-wiki scan before exporting.");
        }

        var types = relationNames.Contains("om_type")
            ? (await QueryAsync(om, "?[name, description, parent_type] := *om_type{ name, description, parent_type }", cancellationToken))
                .Select(row => new DepaType(RowString(row, 0), RowString(row, 1), RowString(row, 2)))
                .Where(type => type.Name.StartsWith("depa_", StringComparison.Ordinal))
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .ToArray()
            : [];

        var typeNames = types.Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        var attributes = relationNames.Contains("om_attr_def")
            ? (await QueryAsync(om, "?[type_name, attr_name, value_type, required] := *om_attr_def{ type_name, attr_name, value_type, required }", cancellationToken))
                .Select(row => new DepaAttribute(RowString(row, 0), RowString(row, 1), RowString(row, 2), RowBool(row, 3)))
                .Where(attribute => typeNames.Contains(attribute.TypeName))
                .OrderBy(attribute => attribute.TypeName, StringComparer.Ordinal)
                .ThenBy(attribute => attribute.Name, StringComparer.Ordinal)
                .ToArray()
            : [];

        var relations = relationNames.Contains("om_rel_def")
            ? (await QueryAsync(om, "?[rel_name, from_type, to_type, directed] := *om_rel_def{ rel_name, from_type, to_type, directed }", cancellationToken))
                .Select(row => new DepaRelationDefinition(RowString(row, 0), RowString(row, 1), RowString(row, 2), RowBool(row, 3)))
                .Where(relation => typeNames.Contains(relation.FromType) && typeNames.Contains(relation.ToType))
                .OrderBy(relation => relation.Name, StringComparer.Ordinal)
                .ToArray()
            : [];

        var entities = (await QueryAsync(om, "?[id, type_name, label] := *om_entity{ id, type_name, label }", cancellationToken))
            .Select(row => new DepaEntity(RowString(row, 0), RowString(row, 1), RowString(row, 2)))
            .Where(entity => typeNames.Contains(entity.TypeName))
            .OrderBy(entity => entity.TypeName, StringComparer.Ordinal)
            .ThenBy(entity => entity.Id, StringComparer.Ordinal)
            .ToArray();
        var entityIds = entities.Select(entity => entity.Id).ToHashSet(StringComparer.Ordinal);

        var properties = relationNames.Contains("om_property")
            ? (await QueryAsync(om, "?[entity_id, attr_name, value] := *om_property{ entity_id, attr_name, value @ \"NOW\" }", cancellationToken))
                .Select(row => new DepaProperty(RowString(row, 0), RowString(row, 1), row[2].GetRawText()))
                .Where(property => entityIds.Contains(property.EntityId))
                .OrderBy(property => property.EntityId, StringComparer.Ordinal)
                .ThenBy(property => property.Name, StringComparer.Ordinal)
                .ToArray()
            : [];

        var edges = relationNames.Contains("om_edge")
            ? (await QueryAsync(om, "?[from_id, rel_name, to_id, props] := *om_edge{ from_id, rel_name, to_id, props @ \"NOW\" }", cancellationToken))
                .Select(row => new DepaEdge(RowString(row, 0), RowString(row, 1), RowString(row, 2), row[3].GetRawText()))
                .Where(edge => entityIds.Contains(edge.FromId) && entityIds.Contains(edge.ToId))
                .OrderBy(edge => edge.Relation, StringComparer.Ordinal)
                .ThenBy(edge => edge.FromId, StringComparer.Ordinal)
                .ThenBy(edge => edge.ToId, StringComparer.Ordinal)
                .ToArray()
            : [];

        var metadata = relationNames.Contains("ck_meta")
            ? (await QueryAsync(om, "?[key, value] := *ck_meta{ key, value }", cancellationToken))
                .OrderBy(row => RowString(row, 0), StringComparer.Ordinal)
                .ToDictionary(row => RowString(row, 0), row => row[1].GetRawText(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var diagnostics = new List<string>();
        if (types.Length == 0)
        {
            diagnostics.Add("No DEPA ontology types are present. Run depa-wiki scan explicitly before requesting an ontology export.");
        }
        if (report is null)
        {
            diagnostics.Add("No fresh DEPA conformance report was supplied. The export contains persisted depa_* rows only; PASS/BLOCKED rule verdicts are absent by design.");
        }

        var propertiesByEntity = properties
            .GroupBy(property => property.EntityId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToDictionary(p => p.Name, p => JsonScalar(p.JsonValue), StringComparer.Ordinal), StringComparer.Ordinal);
        var evidence = entities
            .Select(entity => CreateEvidence(entity, propertiesByEntity.GetValueOrDefault(entity.Id), request))
            .Where(item => item is not null)
            .Cast<DepaEvidence>()
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        return new DepaSnapshot(
            request.RepositoryKey.Length == 0 ? "repository" : request.RepositoryKey,
            request.RepositoryRoot,
            metadata,
            types,
            attributes,
            relations,
            entities,
            properties,
            edges,
            evidence,
            report,
            diagnostics);
    }

    private static async Task<IReadOnlyList<IReadOnlyList<JsonElement>>> QueryAsync(CozoOm om, string script, CancellationToken cancellationToken)
    {
        var result = await om.Runtime.Store.RunAsync(script, cancellationToken: cancellationToken);
        return result.Rows;
    }

    private static async Task<HashSet<string>> RelationNamesAsync(CozoOm om, CancellationToken cancellationToken)
    {
        var result = await om.Runtime.Store.RunAsync("::relations", cancellationToken: cancellationToken);
        return result.Rows
            .Where(row => row.Count > 0 && row[0].ValueKind == JsonValueKind.String)
            .Select(row => row[0].GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static DepaEvidence? CreateEvidence(DepaEntity entity, IReadOnlyDictionary<string, string>? properties, DepaExportRequest request)
    {
        if (properties is null || !properties.TryGetValue("path", out var rawPath) || string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var path = NormalizeRepositoryPath(rawPath, request.RepositoryRoot);
        if (path.Length == 0)
        {
            return null;
        }

        var symbol = properties.GetValueOrDefault("sym_key") ?? properties.GetValueOrDefault("symbol_id") ?? entity.Id;
        var line = int.TryParse(properties.GetValueOrDefault("line"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLine) && parsedLine > 0
            ? parsedLine
            : 1;
        var confidence = double.TryParse(properties.GetValueOrDefault("confidence"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedConfidence)
            ? Math.Clamp(parsedConfidence, 0, 1)
            : 0.7;
        var assignedBy = properties.GetValueOrDefault("assigned_by") ?? "heuristic";
        var grade = assignedBy == "config" ? "contractual" : "inferred";
        return new DepaEvidence(
            "depa:" + SafeEvidencePart(entity.Id),
            request.RepositoryKey.Length == 0 ? "repository" : request.RepositoryKey,
            request.SourceRevision ?? "",
            path,
            symbol,
            line,
            grade,
            "depa-scan",
            confidence,
            $"{entity.TypeName} judgment for {entity.Label}");
    }

    private static async Task<IReadOnlyList<string>> WriteRuntimeSnapshotAsync(string output, DepaSnapshot snapshot, CancellationToken cancellationToken)
    {
        var file = Path.Combine(output, "depa-runtime-snapshot.xml");
        var text = new StringBuilder();
        text.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        text.Append("<DepaRuntimeSnapshot repository=\"").Append(Xml(snapshot.RepositoryKey)).Append("\" exportedAt=\"")
            .Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)).Append("\">\n");
        AppendMetadata(text, snapshot);
        text.AppendLine("  <Types>");
        foreach (var type in snapshot.Types)
        {
            text.Append("    <Type name=\"").Append(Xml(type.Name)).Append("\" parent=\"").Append(Xml(type.ParentType)).Append("\" description=\"").Append(Xml(type.Description)).AppendLine("\" />");
        }
        text.AppendLine("  </Types>");
        text.AppendLine("  <Entities>");
        var properties = snapshot.Properties.GroupBy(property => property.EntityId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group, StringComparer.Ordinal);
        foreach (var entity in snapshot.Entities)
        {
            text.Append("    <Entity id=\"").Append(Xml(entity.Id)).Append("\" type=\"").Append(Xml(entity.TypeName)).Append("\" label=\"").Append(Xml(entity.Label)).AppendLine("\">");
            foreach (var property in properties.TryGetValue(entity.Id, out var entityProperties) ? entityProperties : Enumerable.Empty<DepaProperty>())
            {
                text.Append("      <Property name=\"").Append(Xml(property.Name)).Append("\" json=\"").Append(Xml(property.JsonValue)).AppendLine("\" />");
            }
            text.AppendLine("    </Entity>");
        }
        text.AppendLine("  </Entities>");
        text.AppendLine("  <Edges>");
        foreach (var edge in snapshot.Edges)
        {
            text.Append("    <Edge from=\"").Append(Xml(edge.FromId)).Append("\" relation=\"").Append(Xml(edge.Relation)).Append("\" to=\"").Append(Xml(edge.ToId)).Append("\" propsJson=\"").Append(Xml(edge.PropsJson)).AppendLine("\" />");
        }
        text.AppendLine("  </Edges>");
        AppendReport(text, snapshot.Report);
        AppendDiagnostics(text, snapshot.Diagnostics);
        text.AppendLine("</DepaRuntimeSnapshot>");
        await File.WriteAllTextAsync(file, text.ToString(), cancellationToken);
        return [file];
    }

    private static async Task<IReadOnlyList<string>> WriteOntologyAsync(string output, DepaSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Types.Count == 0)
        {
            throw new InvalidOperationException("Cannot produce ontology XML without DEPA types. Run depa-wiki scan explicitly, then export again.");
        }

        var root = OntologyRoot(snapshot.RepositoryKey);
        var files = new List<string>();
        var typeFile = Path.Combine(output, "types", "depa.xml");
        var relationFile = Path.Combine(output, "relations", "depa.xml");
        var evidenceFile = Path.Combine(output, "evidence", "depa.xml");
        var judgmentFile = Path.Combine(output, "judgments", "depa-judgments.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(typeFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(relationFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(evidenceFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(judgmentFile)!);

        var typeIds = snapshot.Types.ToDictionary(type => type.Name, type => $"{root}.Types.{Pascal(type.Name)}", StringComparer.Ordinal);
        var attributes = snapshot.Attributes.GroupBy(attribute => attribute.TypeName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group, StringComparer.Ordinal);
        var types = new StringBuilder();
        types.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        types.Append("<TypeModule id=\"").Append(root).AppendLine(".Types.Runtime\">");
        types.AppendLine("  <Description>DEPA runtime interpretation types projected from Cozo OM; this module is generated evidence, not editable ontology truth.</Description>");
        types.AppendLine("  <Types>");
        foreach (var type in snapshot.Types)
        {
            types.Append("    <Type id=\"").Append(typeIds[type.Name]).Append("\"");
            if (!string.IsNullOrWhiteSpace(type.ParentType) && typeIds.TryGetValue(type.ParentType, out var parentId)) types.Append(" parent=\"").Append(parentId).Append("\"");
            types.AppendLine(" status=\"hypothesis\">");
            types.Append("      <Description>").Append(XmlText(type.Description.Length == 0 ? type.Name : type.Description)).AppendLine("</Description>");
            var ownAttributes = attributes.GetValueOrDefault(type.Name)?.ToArray() ?? [];
            if (ownAttributes.Length > 0)
            {
                types.AppendLine("      <Attributes>");
                foreach (var attribute in ownAttributes)
                {
                    types.Append("        <Attribute name=\"").Append(Xml(attribute.Name)).Append("\" type=\"").Append(XmlValueType(attribute.ValueType)).Append("\" required=\"").Append(attribute.Required ? "true" : "false").AppendLine("\" />");
                }
                types.AppendLine("      </Attributes>");
            }
            types.AppendLine("    </Type>");
        }
        types.AppendLine("  </Types>");
        types.AppendLine("</TypeModule>");
        await File.WriteAllTextAsync(typeFile, types.ToString(), cancellationToken);
        files.Add(typeFile);

        if (snapshot.Relations.Count > 0)
        {
            var relations = new StringBuilder();
            relations.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            relations.Append("<RelationModule id=\"").Append(root).AppendLine(".Relations.Runtime\">");
            relations.AppendLine("  <Relations>");
            foreach (var relation in snapshot.Relations)
            {
                relations.Append("    <Relation id=\"").Append(root).Append(".Relation.").Append(Pascal(relation.Name)).Append("\" name=\"").Append(Xml(LowerCamel(relation.Name))).Append("\" from=\"").Append(typeIds[relation.FromType]).Append("\" to=\"").Append(typeIds[relation.ToType]).Append("\" directed=\"").Append(relation.Directed ? "true" : "false").AppendLine("\" status=\"hypothesis\" />");
            }
            relations.AppendLine("  </Relations>");
            relations.AppendLine("</RelationModule>");
            await File.WriteAllTextAsync(relationFile, relations.ToString(), cancellationToken);
            files.Add(relationFile);
        }

        if (snapshot.Evidence.Count > 0)
        {
            var evidence = new StringBuilder();
            evidence.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            evidence.Append("<EvidenceModule id=\"").Append(root).AppendLine(".Evidence.Runtime\">");
            evidence.AppendLine("  <EvidenceItems>");
            foreach (var item in snapshot.Evidence)
            {
                evidence.Append("    <Evidence id=\"").Append(Xml(item.Id)).Append("\" repository=\"").Append(Xml(item.Repository)).Append("\" path=\"").Append(Xml(item.Path)).Append("\" symbol=\"").Append(Xml(item.Symbol)).Append("\" startLine=\"").Append(item.Line.ToString(CultureInfo.InvariantCulture)).Append("\" endLine=\"").Append(item.Line.ToString(CultureInfo.InvariantCulture)).Append("\" grade=\"").Append(item.Grade).Append("\" resolver=\"").Append(item.Resolver).Append("\" confidence=\"").Append(item.Confidence.ToString("0.###", CultureInfo.InvariantCulture)).Append("\"");
                if (item.Revision.Length > 0) evidence.Append(" revision=\"").Append(Xml(item.Revision)).Append("\"");
                evidence.Append("><Summary>").Append(XmlText(item.Summary)).AppendLine("</Summary></Evidence>");
            }
            evidence.AppendLine("  </EvidenceItems>");
            evidence.AppendLine("</EvidenceModule>");
            await File.WriteAllTextAsync(evidenceFile, evidence.ToString(), cancellationToken);
            files.Add(evidenceFile);
        }

        await File.WriteAllTextAsync(judgmentFile, RenderJudgmentSidecar(snapshot), cancellationToken);
        files.Add(judgmentFile);

        var ontology = new StringBuilder();
        ontology.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        ontology.Append("<Ontology id=\"").Append(root).AppendLine("\" version=\"v0\">");
        ontology.AppendLine("  <Description>Generated DEPA interpretation projection. The runtime snapshot and judgment sidecar retain operational details; this root is a derived XML projection.</Description>");
        ontology.AppendLine("  <Modules>");
        ontology.AppendLine("    <TypeModule href=\"vfs://./types/depa.xml\" />");
        if (snapshot.Relations.Count > 0) ontology.AppendLine("    <RelationModule href=\"vfs://./relations/depa.xml\" />");
        if (snapshot.Evidence.Count > 0) ontology.AppendLine("    <EvidenceModule href=\"vfs://./evidence/depa.xml\" />");
        ontology.AppendLine("  </Modules>");
        ontology.AppendLine("</Ontology>");
        var rootFile = Path.Combine(output, "ontology.xml");
        await File.WriteAllTextAsync(rootFile, ontology.ToString(), cancellationToken);
        files.Add(rootFile);
        return files;
    }

    private static async Task<IReadOnlyList<string>> WriteWikiAsync(string output, DepaSnapshot snapshot, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var overview = Path.Combine(output, "00-overview");
        var architecture = Path.Combine(output, "01-architecture");
        var evidence = Path.Combine(output, "02-code-evidence");
        var uncertainty = Path.Combine(output, "03-uncertainty");
        var indexDirectory = Path.Combine(output, "_index");
        Directory.CreateDirectory(overview);
        Directory.CreateDirectory(architecture);
        Directory.CreateDirectory(evidence);
        Directory.CreateDirectory(uncertainty);
        Directory.CreateDirectory(indexDirectory);

        var root = Path.Combine(output, "INDEX.md");
        var index = new StringBuilder();
        index.AppendLine("# DEPA Project Wiki");
        index.AppendLine();
        index.AppendLine("This tree is generated from a selected Cozo CodeKnowledge/DEPA snapshot. It is a reading projection, not an editable ontology source.");
        index.AppendLine();
        index.AppendLine("- [Project overview](00-overview/project.md)");
        index.AppendLine("- [DEPA architecture](01-architecture/depa.md)");
        index.AppendLine("- [Code evidence](02-code-evidence/evidence.md)");
        index.AppendLine("- [Uncertainty and blocked findings](03-uncertainty/blocked.md)");
        await File.WriteAllTextAsync(root, index.ToString(), cancellationToken);
        files.Add(root);

        var project = new StringBuilder();
        project.AppendLine("# Project Overview");
        project.AppendLine();
        project.AppendLine($"- Repository: `{snapshot.RepositoryKey}`");
        project.AppendLine($"- DEPA entities: {snapshot.Entities.Count}");
        project.AppendLine($"- DEPA edges: {snapshot.Edges.Count}");
        project.AppendLine($"- Evidence anchors: {snapshot.Evidence.Count}");
        project.AppendLine();
        project.AppendLine("## Indexed Metadata");
        foreach (var item in snapshot.Metadata.OrderBy(item => item.Key, StringComparer.Ordinal)) project.Append("- `").Append(item.Key).Append("`: `").Append(EscapeMarkdown(JsonScalar(item.Value))).AppendLine("`");
        if (snapshot.Metadata.Count == 0) project.AppendLine("- No CodeKnowledge metadata is available.");
        var projectFile = Path.Combine(overview, "project.md");
        await File.WriteAllTextAsync(projectFile, project.ToString(), cancellationToken);
        files.Add(projectFile);

        var depa = new StringBuilder();
        depa.AppendLine("# DEPA Architecture");
        depa.AppendLine();
        foreach (var group in snapshot.Entities.GroupBy(entity => entity.TypeName, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            depa.Append("## ").Append(group.Key).AppendLine();
            foreach (var entity in group) depa.Append("- `").Append(entity.Id).Append("` - ").Append(EscapeMarkdown(entity.Label)).AppendLine();
            depa.AppendLine();
        }
        if (snapshot.Entities.Count == 0) depa.AppendLine("No persisted DEPA entities. Run an explicit DEPA scan before reading this export as an architecture interpretation.");
        var depaFile = Path.Combine(architecture, "depa.md");
        await File.WriteAllTextAsync(depaFile, depa.ToString(), cancellationToken);
        files.Add(depaFile);

        var evidencePage = new StringBuilder();
        evidencePage.AppendLine("# Code Evidence");
        evidencePage.AppendLine();
        foreach (var item in snapshot.Evidence)
        {
            evidencePage.Append("- `").Append(item.Id).Append("`: `").Append(item.Path).Append(':').Append(item.Line.ToString(CultureInfo.InvariantCulture)).Append("` - ").Append(EscapeMarkdown(item.Summary)).AppendLine();
        }
        if (snapshot.Evidence.Count == 0) evidencePage.AppendLine("No DEPA entity has a repository-relative code anchor.");
        var evidenceFile = Path.Combine(evidence, "evidence.md");
        await File.WriteAllTextAsync(evidenceFile, evidencePage.ToString(), cancellationToken);
        files.Add(evidenceFile);

        var blocked = new StringBuilder();
        blocked.AppendLine("# Uncertainty And Blocked Findings");
        blocked.AppendLine();
        if (snapshot.Report is null)
        {
            blocked.AppendLine("- No fresh conformance report was supplied. Persistent violation entities alone cannot distinguish PASS from BLOCKED.");
        }
        else
        {
            foreach (var dimension in snapshot.Report.Dimensions)
            {
                foreach (var rule in dimension.Rules.Where(rule => rule.Verdict == "BLOCKED"))
                {
                    blocked.Append("- `").Append(rule.RuleId).Append("` (").Append(dimension.Dimension).Append("): ").Append(EscapeMarkdown(rule.BlockedReason)).AppendLine();
                }
            }
        }
        foreach (var diagnostic in snapshot.Diagnostics) blocked.Append("- ").Append(EscapeMarkdown(diagnostic)).AppendLine();
        var blockedFile = Path.Combine(uncertainty, "blocked.md");
        await File.WriteAllTextAsync(blockedFile, blocked.ToString(), cancellationToken);
        files.Add(blockedFile);

        var manifestFile = Path.Combine(indexDirectory, "manifest.json");
        await File.WriteAllTextAsync(manifestFile, JsonSerializer.Serialize(new
        {
            repository = snapshot.RepositoryKey,
            entityCount = snapshot.Entities.Count,
            edgeCount = snapshot.Edges.Count,
            evidenceCount = snapshot.Evidence.Count,
            diagnostics = snapshot.Diagnostics,
            pages = files.Select(path => Path.GetRelativePath(output, path).Replace(Path.DirectorySeparatorChar, '/')).OrderBy(path => path, StringComparer.Ordinal)
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        files.Add(manifestFile);
        return files;
    }

    private static string RenderJudgmentSidecar(DepaSnapshot snapshot)
    {
        var text = new StringBuilder();
        text.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        text.Append("<DepaJudgments repository=\"").Append(Xml(snapshot.RepositoryKey)).AppendLine("\">");
        var props = snapshot.Properties.GroupBy(property => property.EntityId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group, StringComparer.Ordinal);
        foreach (var entity in snapshot.Entities)
        {
            text.Append("  <Judgment id=\"").Append(Xml(entity.Id)).Append("\" type=\"").Append(Xml(entity.TypeName)).Append("\" label=\"").Append(Xml(entity.Label)).AppendLine("\">");
            foreach (var property in props.TryGetValue(entity.Id, out var entityProperties) ? entityProperties : Enumerable.Empty<DepaProperty>()) text.Append("    <Property name=\"").Append(Xml(property.Name)).Append("\" json=\"").Append(Xml(property.JsonValue)).AppendLine("\" />");
            text.AppendLine("  </Judgment>");
        }
        AppendReport(text, snapshot.Report, "  ");
        AppendDiagnostics(text, snapshot.Diagnostics, "  ");
        text.AppendLine("</DepaJudgments>");
        return text.ToString();
    }

    private static void AppendMetadata(StringBuilder text, DepaSnapshot snapshot)
    {
        text.AppendLine("  <Metadata>");
        foreach (var item in snapshot.Metadata.OrderBy(item => item.Key, StringComparer.Ordinal)) text.Append("    <Entry key=\"").Append(Xml(item.Key)).Append("\" json=\"").Append(Xml(item.Value)).AppendLine("\" />");
        text.AppendLine("  </Metadata>");
    }

    private static void AppendReport(StringBuilder text, DepaConformanceReport? report, string indentation = "")
    {
        if (report is null) return;
        text.Append(indentation).AppendLine("<ConformanceReport>");
        foreach (var dimension in report.Dimensions)
        {
            foreach (var rule in dimension.Rules)
            {
                text.Append(indentation).Append("  <Rule dimension=\"").Append(Xml(dimension.Dimension)).Append("\" id=\"").Append(Xml(rule.RuleId)).Append("\" verdict=\"").Append(Xml(rule.Verdict)).Append("\" blockedReason=\"").Append(Xml(rule.BlockedReason)).AppendLine("\" />");
            }
        }
        text.Append(indentation).AppendLine("</ConformanceReport>");
    }

    private static void AppendDiagnostics(StringBuilder text, IReadOnlyList<string> diagnostics, string indentation = "")
    {
        if (diagnostics.Count == 0) return;
        text.Append(indentation).AppendLine("<Diagnostics>");
        foreach (var diagnostic in diagnostics) text.Append(indentation).Append("  <Diagnostic message=\"").Append(Xml(diagnostic)).AppendLine("\" />");
        text.Append(indentation).AppendLine("</Diagnostics>");
    }

    private static string PrepareEmptyOutputDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An explicit output directory is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any())
        {
            throw new IOException($"Export output directory must be empty: {fullPath}");
        }
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static string RowString(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.String ? row[index].GetString() ?? "" : row[index].GetRawText();

    private static bool RowBool(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.True || (row[index].ValueKind == JsonValueKind.String && bool.TryParse(row[index].GetString(), out var value) && value);

    private static string JsonScalar(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() ?? "" : document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string NormalizeRepositoryPath(string path, string? repositoryRoot)
    {
        var normalized = path.Replace('\\', '/');
        if (!Path.IsPathRooted(path)) return normalized.TrimStart('/');
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return "";
        var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
        return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) ? "" : relative;
    }

    private static string OntologyRoot(string repositoryKey) => "Depa." + Pascal(repositoryKey);

    private static string Pascal(string value)
    {
        var parts = value.Split(['-', '_', '.', ' ', '/', '\\', ':'], StringSplitOptions.RemoveEmptyEntries);
        var text = string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        if (text.Length == 0) text = "Repository";
        return char.IsLetter(text[0]) ? text : "N" + text;
    }

    private static string LowerCamel(string value)
    {
        var pascal = Pascal(value);
        return char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    private static string XmlValueType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "string" => "String",
        "number" or "int" or "float" => "Number",
        "bool" or "boolean" => "Bool",
        "validity" => "Validity",
        _ => "Json",
    };

    private static string SafeEvidencePart(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value.ToLowerInvariant()) builder.Append(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '-');
        return builder.ToString().Trim('-');
    }

    private static string Xml(string value) => SecurityElement.Escape(value) ?? "";
    private static string XmlText(string value) => Xml(value);
    private static string EscapeMarkdown(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}

public sealed record DepaExportRequest(
    string Format,
    string OutputDirectory,
    string RepositoryKey,
    string? RepositoryRoot = null,
    string? SourceRevision = null);

public sealed record DepaExportResult(
    string Format,
    string OutputDirectory,
    IReadOnlyList<string> Files,
    int EntityCount,
    int EdgeCount,
    int EvidenceCount,
    IReadOnlyList<string> Diagnostics);

internal sealed record DepaSnapshot(
    string RepositoryKey,
    string? RepositoryRoot,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<DepaType> Types,
    IReadOnlyList<DepaAttribute> Attributes,
    IReadOnlyList<DepaRelationDefinition> Relations,
    IReadOnlyList<DepaEntity> Entities,
    IReadOnlyList<DepaProperty> Properties,
    IReadOnlyList<DepaEdge> Edges,
    IReadOnlyList<DepaEvidence> Evidence,
    DepaConformanceReport? Report,
    IReadOnlyList<string> Diagnostics);

internal sealed record DepaType(string Name, string Description, string ParentType);
internal sealed record DepaAttribute(string TypeName, string Name, string ValueType, bool Required);
internal sealed record DepaRelationDefinition(string Name, string FromType, string ToType, bool Directed);
internal sealed record DepaEntity(string Id, string TypeName, string Label);
internal sealed record DepaProperty(string EntityId, string Name, string JsonValue);
internal sealed record DepaEdge(string FromId, string Relation, string ToId, string PropsJson);
internal sealed record DepaEvidence(string Id, string Repository, string Revision, string Path, string Symbol, int Line, string Grade, string Resolver, double Confidence, string Summary);
