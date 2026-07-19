using System.Text.Json;
using System.Text;
using System.Xml.Linq;
using System.Xml;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Tools;

public sealed record BusinessOntologyXmlExportRequest(string OntologyId, string OutputDirectory, string SemanticVersion = "0.1.0");
public sealed record BusinessOntologyXmlExportResult(string OutputDirectory, IReadOnlyList<string> Files, int Concepts, int Relations, int Rules, int Lifecycles, int Candidates);

/// <summary>Read-only XML projection for the independently persisted business ontology view.</summary>
public sealed class BusinessOntologyXmlExporter(
    BusinessOntologyStore store,
    BusinessOntologyQualityReportBuilder? qualityReportBuilder = null)
{
    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public async Task<BusinessOntologyXmlExportResult> ExportAsync(BusinessOntologyXmlExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.OutputDirectory)) throw new ArgumentException("Output directory is required.", nameof(request));
        return await ExportAsync(request, [await store.ReadExportableAsync(request.OntologyId, cancellationToken)], cancellationToken);
    }

    public Task<BusinessOntologyXmlExportResult> ExportAsync(BusinessOntologyXmlExportRequest request, IReadOnlyList<BusinessOntologySnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0 || snapshots.Any(item => item.OntologyId != request.OntologyId)) throw new ArgumentException("Every snapshot must belong to the requested ontology.", nameof(snapshots));
        return ExportMergedAsync(request, Merge(snapshots), cancellationToken);
    }

    private async Task<BusinessOntologyXmlExportResult> ExportMergedAsync(BusinessOntologyXmlExportRequest request, BusinessOntologySnapshot snapshot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OutputDirectory)) throw new ArgumentException("Output directory is required.", nameof(request));
        foreach (var rule in snapshot.Rules)
        {
            _ = BusinessOntologyRuleXmlProjector.Project(
                rule.Kind,
                rule.PredicateJson);
        }
        var root = PrepareEmptyOutputDirectory(request.OutputDirectory);
        var files = new List<string>();

        void Write(string relativePath, XDocument document)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = XmlWriter.Create(path, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true });
            document.Save(writer);
            files.Add(path);
        }

        var modules = new List<XElement>();
        if (snapshot.Concepts.Count > 0)
        {
            const string path = "types/generated.xml";
            Write(path, TypeModule(snapshot));
            modules.Add(ModuleRef("TypeModule", path));
        }
        if (snapshot.Relations.Count > 0)
        {
            const string path = "relations/generated.xml";
            Write(path, RelationModule(snapshot));
            modules.Add(ModuleRef("RelationModule", path));
        }
        if (snapshot.Lifecycles.Count > 0)
        {
            const string path = "lifecycles/generated.xml";
            Write(path, LifecycleModule(snapshot));
            modules.Add(ModuleRef("LifecycleModule", path));
        }
        if (snapshot.Rules.Count > 0)
        {
            const string path = "rules/generated.xml";
            Write(path, RuleModule(snapshot));
            modules.Add(ModuleRef("RuleModule", path));
        }
        if (snapshot.Mappings.Count > 0)
        {
            const string path = "mappings/generated.xml";
            Write(path, MappingModule(snapshot));
            modules.Add(ModuleRef("ImplementationMappingModule", path));
        }
        const string evidencePath = "evidence/generated.xml";
        Write(evidencePath, EvidenceModule(snapshot));
        modules.Add(ModuleRef("EvidenceModule", evidencePath));

        Write("ontology.xml", Document(new XElement("Ontology", new XAttribute("id", request.OntologyId), new XAttribute("version", request.SemanticVersion),
            new XElement("Description", "从 CodeKnowledge 证据投影生成的业务本体；所有推导均保留为可审计的假设或已确认声明。"),
            new XElement("Modules", modules))));

        var auditPath = Path.Combine(root, "generation", "candidates.json");
        Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
        await File.WriteAllTextAsync(auditPath, JsonSerializer.Serialize(new { snapshot.OntologyId, snapshot.GenerationId, candidates = snapshot.Candidates, reviews = snapshot.Reviews, diagnostics = snapshot.Diagnostics }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        files.Add(auditPath);
        if (qualityReportBuilder is not null)
        {
            var reportPath = Path.Combine(root, "generation", "quality-report.json");
            var report = await qualityReportBuilder.BuildAsync(
                new BusinessOntologyQualityReportRequest(snapshot.OntologyId),
                snapshot,
                cancellationToken);
            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(report, AuditJsonOptions),
                cancellationToken);
            files.Add(reportPath);
        }
        return new BusinessOntologyXmlExportResult(root, files.OrderBy(item => item, StringComparer.Ordinal).ToArray(), snapshot.Concepts.Count, snapshot.Relations.Count, snapshot.Rules.Count, snapshot.Lifecycles.Count, snapshot.Candidates.Count);
    }

    private static BusinessOntologySnapshot Merge(IReadOnlyList<BusinessOntologySnapshot> snapshots)
    {
        var first = snapshots[0];
        static IReadOnlyList<T> DistinctBy<T, TKey>(IEnumerable<T> values, Func<T, TKey> key) where TKey : notnull => values.GroupBy(key).Select(group => group.First()).ToArray();
        return new BusinessOntologySnapshot(
            first.OntologyId, first.GenerationId,
            DistinctBy(snapshots.SelectMany(item => item.Concepts), item => item.Id),
            DistinctBy(snapshots.SelectMany(item => item.Attributes), item => (item.ConceptId, item.Name)),
            DistinctBy(snapshots.SelectMany(item => item.Relations), item => item.Id),
            DistinctBy(snapshots.SelectMany(item => item.Rules), item => item.Id),
            DistinctBy(snapshots.SelectMany(item => item.Lifecycles), item => item.Id),
            DistinctBy(snapshots.SelectMany(item => item.States), item => (item.LifecycleId, item.Id)),
            DistinctBy(snapshots.SelectMany(item => item.Transitions), item => item.Id),
            DistinctBy(snapshots.SelectMany(item => item.Mappings), item => item.Id),
            DistinctBy(snapshots.SelectMany(item => item.Evidence), item => item.Id),
            DistinctBy(snapshots.SelectMany(item => item.Candidates), item => item.Id),
            DistinctBy(snapshots.SelectMany(item => item.Reviews), item => item.Id),
            DistinctBy(snapshots.SelectMany(item => item.Diagnostics), item => item.Id),
            DistinctBy(snapshots.SelectMany(item => item.EvidenceReferences), item => (item.SubjectKind, item.SubjectId, item.EvidenceId)));
    }

    private static XDocument TypeModule(BusinessOntologySnapshot snapshot) => Document(new XElement("TypeModule", new XAttribute("id", snapshot.OntologyId + ".Types.Generated"),
        new XElement("Description", "由已索引代码事实推导的业务类型与属性。"),
        new XElement("Types", snapshot.Concepts.Select(concept =>
            new XElement("Type", new XAttribute("id", concept.Id), new XAttribute("status", concept.Status),
                new XElement("Description", concept.Description),
                Attributes(snapshot, concept), EvidenceRefs(concept.EvidenceIds))))));

    private static XElement? Attributes(BusinessOntologySnapshot snapshot, BusinessOntologyConcept concept)
    {
        var attributes = snapshot.Attributes.Where(item => item.ConceptId == concept.Id).ToArray();
        return attributes.Length == 0 ? null : new XElement("Attributes", attributes.Select(item =>
            new XElement("Attribute", new XAttribute("name", item.Name), new XAttribute("type", item.ValueType), new XAttribute("required", item.Required.ToString().ToLowerInvariant()),
                new XElement("Description", item.Description), EvidenceRefs(item.EvidenceIds))));
    }

    private static XDocument RelationModule(BusinessOntologySnapshot snapshot) => Document(new XElement("RelationModule", new XAttribute("id", snapshot.OntologyId + ".Relations.Generated"),
        new XElement("Description", "由已确认业务关联投影的关系定义。"),
        new XElement("Relations", snapshot.Relations.Select(item =>
            new XElement("Relation", new XAttribute("id", item.Id), new XAttribute("name", item.Name), new XAttribute("from", item.FromConceptId), new XAttribute("to", item.ToConceptId), new XAttribute("directed", item.Directed.ToString().ToLowerInvariant()), new XAttribute("min", item.Min), new XAttribute("max", item.Max), new XAttribute("status", item.Status),
                new XElement("Description", item.Description), EvidenceRefs(item.EvidenceIds))))));

    private static XDocument RuleModule(BusinessOntologySnapshot snapshot) => Document(new XElement("RuleModule", new XAttribute("id", snapshot.OntologyId + ".Rules.Generated"),
        new XElement("Description", "由已有业务本体记录导出的声明式规则。"),
        new XElement("Rules", snapshot.Rules.Select(item =>
        {
            var projection = BusinessOntologyRuleXmlProjector.Project(
                item.Kind,
                item.PredicateJson);
            return new XElement("Rule", new XAttribute("id", item.Id), new XAttribute("scope", item.SubjectId), new XAttribute("kind", projection.DslRuleKind), new XAttribute("status", item.Status),
                new XElement("Statement", item.Description),
                new XElement("Require", projection.ToXml()),
                new XElement("Violation", new XAttribute("code", "ONTOLOGY_RULE_VIOLATION"), new XAttribute("message", item.Description)),
                EvidenceRefs(item.EvidenceIds));
        }))));

    private static XDocument LifecycleModule(BusinessOntologySnapshot snapshot) => Document(new XElement("LifecycleModule", new XAttribute("id", snapshot.OntologyId + ".Lifecycles.Generated"),
        new XElement("Description", "由已有业务本体记录导出的生命周期声明。"),
        new XElement("StateMachines", snapshot.Lifecycles.Select(lifecycle =>
            new XElement("StateMachine", new XAttribute("id", lifecycle.Id), new XAttribute("subject", lifecycle.SubjectId), new XAttribute("stateProperty", lifecycle.StateProperty), new XAttribute("initial", DslStateId(lifecycle.InitialState)), new XAttribute("status", lifecycle.Status),
                new XElement("Description", lifecycle.Description),
                new XElement("States", snapshot.States.Where(item => item.LifecycleId == lifecycle.Id).Select(state => new XElement("State", new XAttribute("id", DslStateId(state.Id)), new XAttribute("terminal", state.Terminal.ToString().ToLowerInvariant()), new XElement("Description", state.Description)))),
                new XElement("Transitions", snapshot.Transitions.Where(item => item.LifecycleId == lifecycle.Id).Select(transition =>
                    new XElement("Transition", new XAttribute("id", transition.Id), new XAttribute("action", transition.Action), new XAttribute("from", DslStateId(transition.FromState)), new XAttribute("to", DslStateId(transition.ToState)), new XAttribute("status", transition.Status), new XElement("Description", transition.Description), EvidenceRefs(transition.EvidenceIds)))),
                EvidenceRefs(lifecycle.EvidenceIds))))));

    private static XDocument MappingModule(BusinessOntologySnapshot snapshot) => Document(new XElement("ImplementationMappingModule", new XAttribute("id", snapshot.OntologyId + ".Mappings.Generated"),
        new XElement("Description", "业务概念到已索引实现位置的映射。"),
        new XElement("Mappings", snapshot.Mappings.Select(item =>
            new XElement("ImplementationMapping", new XAttribute("id", item.Id), new XAttribute(SubjectReferenceName(item.SubjectKind), item.SubjectId), new XAttribute("status", item.Status),
                new XElement("Description", $"{item.SubjectId} 与代码符号 {item.Symbol} 的实现映射。"),
                new XElement(MappingRoleName(item.MappingRole), new XElement("CodeRef", new XAttribute("repository", item.Repository), new XAttribute("language", item.Language), new XAttribute("kind", item.CodeKind), new XAttribute("symbol", item.Symbol), new XAttribute("path", item.Path), new XAttribute("resolver", item.Resolver), new XAttribute("confidence", item.Confidence.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))),
                EvidenceRefs(item.EvidenceIds))))));

    private static XDocument EvidenceModule(BusinessOntologySnapshot snapshot) => Document(new XElement("EvidenceModule", new XAttribute("id", snapshot.OntologyId + ".Evidence.Generated"),
        new XElement("Description", "支撑业务本体解释的代码观察证据。"),
        new XElement("EvidenceItems", snapshot.Evidence.Select(item =>
            new XElement("Evidence", new XAttribute("id", item.Id), new XAttribute("repository", item.Repository), new XAttribute("path", item.Path), new XAttribute("symbol", item.Symbol), new XAttribute("startLine", item.StartLine), new XAttribute("endLine", item.EndLine), new XAttribute("grade", item.Grade), new XAttribute("resolver", item.Resolver), new XAttribute("confidence", item.Confidence.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)), new XAttribute("sourceKind", item.SourceKind), new XElement("Summary", item.Summary))))));

    private static string SubjectReferenceName(string kind) => kind switch { "concept" => "conceptRef", "relation" => "relationRef", "rule" => "ruleRef", "lifecycle" => "lifecycleRef", _ => throw new InvalidOperationException($"Unsupported mapping subject kind '{kind}'.") };
    private static string MappingRoleName(string role) => role switch { "representedBy" => "RepresentedBy", "implementedBy" => "ImplementedBy", "presentedBy" => "PresentedBy", "storedBy" => "StoredBy", "exposedBy" => "ExposedBy", _ => throw new InvalidOperationException($"Unsupported mapping role '{role}'.") };
    private static string DslStateId(string value)
    {
        var parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new InvalidOperationException(
                $"Lifecycle state '{value}' cannot be represented as an ontology XML DSL state id.");
        }
        if (parts.Length == 1 && char.IsLower(parts[0][0]))
        {
            return parts[0];
        }
        return parts[0].ToLowerInvariant()
            + string.Concat(parts.Skip(1).Select(part =>
                char.ToUpperInvariant(part[0])
                + part[1..].ToLowerInvariant()));
    }
    private static XElement EvidenceRefs(IReadOnlyList<string> ids) => new("EvidenceRefs", ids.OrderBy(item => item, StringComparer.Ordinal).Select(id => new XElement("EvidenceRef", new XAttribute("ref", id))));
    private static XElement ModuleRef(string name, string path) => new(name, new XAttribute("href", "vfs://./" + path));
    private static XDocument Document(XElement root) => new(new XDeclaration("1.0", "UTF-8", null), root);
    private static string PrepareEmptyOutputDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any()) throw new IOException($"Output directory must be empty: {full}");
        Directory.CreateDirectory(full);
        return full;
    }
}
