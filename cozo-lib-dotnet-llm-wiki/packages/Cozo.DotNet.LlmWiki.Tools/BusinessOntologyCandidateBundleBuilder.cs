using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Cozo.DotNet.LlmWiki.Tools;

/// <summary>
/// Builds a standalone, review-only ontology XML hypothesis bundle from the isolated analysis
/// workspace. Accepted ontology rows are deliberately not an input to this type.
/// </summary>
public sealed record BusinessOntologyCandidateEvidenceFact(
    string Id,
    string Repository,
    string Path,
    string Symbol,
    int StartLine,
    int EndLine,
    string Grade,
    string Resolver,
    double Confidence,
    string SourceKind,
    string Summary);

public sealed record BusinessOntologyCandidateBundleRequest(
    string OntologyId,
    string GenerationId,
    string Version,
    IReadOnlyList<string> KnownConceptIds,
    IReadOnlyList<BusinessOntologyCandidateEvidenceFact> EvidenceFacts,
    string? AnalysisRunId = null);

public sealed record BusinessOntologyCandidateBundleMapping(
    string RunId,
    string RecordId,
    string CandidateXmlId,
    OntologyCandidateDraftXmlMappingDecision Decision);

public sealed record BusinessOntologyCandidateBundleExclusion(string RunId, string RecordId, string Reason);

public sealed record BusinessOntologyCandidateBundleBuildResult(
    string OntologyId,
    string GenerationId,
    IReadOnlyList<BusinessOntologyCandidateBundleMapping> Mappings,
    IReadOnlyList<BusinessOntologyCandidateBundleExclusion> Exclusions,
    IReadOnlyDictionary<string, XDocument> Files)
{
    public bool HasMappableCandidates => Mappings.Count != 0;
    public IReadOnlyList<string> ExcludedRecordIds => Exclusions.Select(item => item.RecordId).ToArray();
}

public sealed class BusinessOntologyCandidateBundleBuilder(BusinessOntologyAnalysisStore analysisStore)
{
    private sealed record LifecycleStatePropertySupport(string Name, IReadOnlyList<string> EvidenceIds);

    private static readonly Regex Fqn = new("^[A-Z][A-Za-z0-9]*(?:\\.[A-Za-z][A-Za-z0-9]*)+$", RegexOptions.CultureInvariant);
    // Keep this identical to ontology-xml-dsl's namespacedIdPattern. In particular, slash,
    // uppercase, and a second colon are not valid generated Evidence identifiers.
    private static readonly Regex EvidenceId = new("^[a-z][a-z0-9-]*:[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeRepository = new("^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> EvidenceGrades = new(["authoritative", "enforced", "contractual", "presentational", "inferred"], StringComparer.Ordinal);

    public async Task<BusinessOntologyCandidateBundleBuildResult> BuildAsync(
        BusinessOntologyCandidateBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var workspace = await analysisStore.ReadWorkspaceAsync(request.OntologyId, request.GenerationId, cancellationToken);
        var records = SelectRecords(workspace, request.AnalysisRunId);
        var evidenceById = request.EvidenceFacts
            .Select(ValidateEvidence)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var validatorEvidence = evidenceById.Values
            .Select(item => new OntologyCandidateDraftEvidence(item.Id, item.Grade))
            .ToArray();
        var mappingValidator = new OntologyCandidateDraftXmlMappingValidator();
        var decisions = records
            .Where(record => StringComparer.Ordinal.Equals(record.Kind, "candidate_draft"))
            .OrderBy(record => record.CreatedAt, StringComparer.Ordinal)
            .ThenBy(record => record.RunId, StringComparer.Ordinal)
            .ThenBy(record => record.RecordId, StringComparer.Ordinal)
            .Select(record => (Record: record, Decision: mappingValidator.Evaluate(record, request.KnownConceptIds, validatorEvidence)))
            .Where(item => item.Decision.Mappable)
            .ToArray();

        // XML declarations have globally unique semantic IDs. Keep the stable first record and
        // leave duplicate handling visible to the later diagnosis writer rather than inventing a
        // second declaration.
        var selected = new List<(BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision)>();
        var exclusions = new List<BusinessOntologyCandidateBundleExclusion>();
        var semanticIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in decisions)
        {
            var key = item.Decision.XmlKind == OntologyCandidateDraftXmlKinds.Property
                ? "property:" + item.Decision.SemanticId
                : item.Decision.SemanticId!;
            if (semanticIds.Add(key)) selected.Add(item);
            else exclusions.Add(new BusinessOntologyCandidateBundleExclusion(item.Record.RunId, item.Record.RecordId, "duplicate_semantic_id"));
        }

        var declaredRelations = selected
            .Where(item => item.Decision.XmlKind == OntologyCandidateDraftXmlKinds.Relation)
            .Select(item => item.Decision.SemanticId!)
            .ToHashSet(StringComparer.Ordinal);
        var closureSafe = new List<(BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision)>();
        foreach (var item in selected)
        {
            if (item.Decision.XmlKind == OntologyCandidateDraftXmlKinds.Rule
                && RuleKind(item.Decision) == "Custom")
            {
                exclusions.Add(new BusinessOntologyCandidateBundleExclusion(item.Record.RunId, item.Record.RecordId, "custom_rule_requires_runtime_binding"));
                continue;
            }
            var missingRelations = ReferencedRelationIds(item.Decision)
                .Where(relationId => !declaredRelations.Contains(relationId))
                .OrderBy(relationId => relationId, StringComparer.Ordinal)
                .ToArray();
            if (missingRelations.Length != 0)
            {
                exclusions.Add(new BusinessOntologyCandidateBundleExclusion(
                    item.Record.RunId,
                    item.Record.RecordId,
                    "unresolved_relation_reference:" + string.Join(",", missingRelations)));
                continue;
            }
            closureSafe.Add(item);
        }
        selected = closureSafe;

        var mappings = selected
            .Select(item => new BusinessOntologyCandidateBundleMapping(
                item.Record.RunId,
                item.Record.RecordId,
                item.Decision.SemanticId!,
                item.Decision))
            .OrderBy(item => item.CandidateXmlId, StringComparer.Ordinal)
            .ThenBy(item => item.RecordId, StringComparer.Ordinal)
            .ToArray();

        if (selected.Count == 0)
        {
            return new BusinessOntologyCandidateBundleBuildResult(
                request.OntologyId,
                request.GenerationId,
                mappings,
                exclusions.OrderBy(item => item.RecordId, StringComparer.Ordinal).ToArray(),
                new Dictionary<string, XDocument>(StringComparer.Ordinal));
        }

        var usedEvidence = selected
            .SelectMany(item => item.Decision.EvidenceIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToDictionary(id => id, id => evidenceById[id], StringComparer.Ordinal);
        var files = AssembleFiles(request, selected, usedEvidence);
        return new BusinessOntologyCandidateBundleBuildResult(
            request.OntologyId,
            request.GenerationId,
            mappings,
            exclusions.OrderBy(item => item.RecordId, StringComparer.Ordinal).ToArray(),
            files);
    }

    private static IReadOnlyList<BusinessOntologyAnalysisRecord> SelectRecords(
        BusinessOntologyAnalysisWorkspaceSnapshot workspace,
        string? analysisRunId)
    {
        if (string.IsNullOrWhiteSpace(analysisRunId)) return workspace.Records;
        if (!workspace.Runs.Any(run => StringComparer.Ordinal.Equals(run.RunId, analysisRunId)))
        {
            throw new ArgumentException(
                $"Analysis run '{analysisRunId}' does not belong to '{workspace.OntologyId}'/'{workspace.GenerationId}'.",
                nameof(analysisRunId));
        }
        return workspace.Records
            .Where(record => StringComparer.Ordinal.Equals(record.RunId, analysisRunId))
            .ToArray();
    }

    /// <summary>
    /// Writes only an explicitly empty staging directory. Validation and the final atomic move are
    /// intentionally owned by T2.2 so this method cannot overwrite a published bundle.
    /// </summary>
    public async Task<string?> WriteStagingAsync(
        BusinessOntologyCandidateBundleBuildResult bundle,
        string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (!bundle.HasMappableCandidates) return null;
        if (string.IsNullOrWhiteSpace(stagingDirectory)) throw new ArgumentException("stagingDirectory is required.", nameof(stagingDirectory));
        var root = Path.GetFullPath(stagingDirectory);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException($"Staging directory must not already exist: {root}");

        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in bundle.Files.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = ValidateBundleRelativePath(file.Key);
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var stream = File.Create(path);
                await file.Value.SaveAsync(stream, SaveOptions.None, cancellationToken);
            }
            return root;
        }
        catch
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            throw;
        }
    }

    private static IReadOnlyDictionary<string, XDocument> AssembleFiles(
        BusinessOntologyCandidateBundleRequest request,
        IReadOnlyList<(BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision)> selected,
        IReadOnlyDictionary<string, BusinessOntologyCandidateEvidenceFact> evidenceById)
    {
        var typeCandidates = selected.Where(item => item.Decision.XmlKind == OntologyCandidateDraftXmlKinds.Type).ToArray();
        var propertyCandidates = selected.Where(item => item.Decision.XmlKind == OntologyCandidateDraftXmlKinds.Property).ToArray();
        var relationCandidates = selected.Where(item => item.Decision.XmlKind == OntologyCandidateDraftXmlKinds.Relation).ToArray();
        var ruleCandidates = selected.Where(item => item.Decision.XmlKind == OntologyCandidateDraftXmlKinds.Rule).ToArray();
        var lifecycleCandidates = selected.Where(item => item.Decision.XmlKind == OntologyCandidateDraftXmlKinds.Lifecycle).ToArray();

        var files = new SortedDictionary<string, XDocument>(StringComparer.Ordinal);
        var typeDocument = TypeModule(request, typeCandidates, propertyCandidates, relationCandidates, ruleCandidates, lifecycleCandidates);
        if (typeDocument is not null) files.Add("types/candidates.xml", typeDocument);
        if (relationCandidates.Length != 0) files.Add("relations/candidates.xml", RelationModule(request, relationCandidates));
        if (lifecycleCandidates.Length != 0) files.Add("lifecycles/candidates.xml", LifecycleModule(request, lifecycleCandidates));
        if (ruleCandidates.Length != 0) files.Add("rules/candidates.xml", RuleModule(request, ruleCandidates));
        files.Add("evidence/analysis.xml", EvidenceModule(request, evidenceById.Values));

        var modules = new XElement("Modules");
        AddModuleRef(modules, "TypeModule", "types/candidates.xml", files);
        AddModuleRef(modules, "RelationModule", "relations/candidates.xml", files);
        AddModuleRef(modules, "LifecycleModule", "lifecycles/candidates.xml", files);
        AddModuleRef(modules, "RuleModule", "rules/candidates.xml", files);
        AddModuleRef(modules, "EvidenceModule", "evidence/analysis.xml", files);
        files.Add("ontology.xml", Document(new XElement("Ontology",
            new XAttribute("id", request.OntologyId),
            new XAttribute("version", request.Version),
            new XElement("Description", "基于隔离分析工作区生成的待审核业务本体候选。"),
            modules)));
        return files;
    }

    private static XDocument? TypeModule(
        BusinessOntologyCandidateBundleRequest request,
        IReadOnlyList<(BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision)> typeCandidates,
        IReadOnlyList<(BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision)> propertyCandidates,
        IReadOnlyList<(BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision)> relationCandidates,
        IReadOnlyList<(BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision)> ruleCandidates,
        IReadOnlyList<(BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision)> lifecycleCandidates)
    {
        var support = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void AddSupport(string conceptId, IEnumerable<string> evidenceIds)
        {
            if (!support.TryGetValue(conceptId, out var refs)) support[conceptId] = refs = new SortedSet<string>(StringComparer.Ordinal);
            refs.UnionWith(evidenceIds);
        }

        foreach (var item in typeCandidates)
        {
            AddSupport(item.Decision.SemanticId!, item.Decision.EvidenceIds);
            using var semantic = JsonDocument.Parse(item.Decision.CanonicalSemanticJson);
            if (semantic.RootElement.TryGetProperty("parentConceptId", out var parent))
            {
                AddSupport(parent.GetString()!, item.Decision.EvidenceIds);
            }
        }
        foreach (var item in propertyCandidates) AddSupport(PropertyOwner(item.Decision), item.Decision.EvidenceIds);
        foreach (var item in relationCandidates)
        {
            using var semantic = JsonDocument.Parse(item.Decision.CanonicalSemanticJson);
            AddSupport(semantic.RootElement.GetProperty("fromConceptId").GetString()!, item.Decision.EvidenceIds);
            AddSupport(semantic.RootElement.GetProperty("toConceptId").GetString()!, item.Decision.EvidenceIds);
        }
        foreach (var item in ruleCandidates)
        {
            using var semantic = JsonDocument.Parse(item.Decision.CanonicalSemanticJson);
            AddSupport(semantic.RootElement.GetProperty("scope").GetString()!, item.Decision.EvidenceIds);
            if (semantic.RootElement.TryGetProperty("when", out var when)) AddPredicateTypeSupport(when, item.Decision.EvidenceIds, AddSupport);
            AddPredicateTypeSupport(semantic.RootElement.GetProperty("require"), item.Decision.EvidenceIds, AddSupport);
        }
        foreach (var item in lifecycleCandidates)
        {
            using var semantic = JsonDocument.Parse(item.Decision.CanonicalSemanticJson);
            AddSupport(semantic.RootElement.GetProperty("subject").GetString()!, item.Decision.EvidenceIds);
            foreach (var transition in semantic.RootElement.GetProperty("transitions").EnumerateArray())
            {
                if (transition.TryGetProperty("guard", out var guard)) AddPredicateTypeSupport(guard, item.Decision.EvidenceIds, AddSupport);
            }
        }
        if (support.Count == 0) return null;

        var typesById = typeCandidates.ToDictionary(item => item.Decision.SemanticId!, StringComparer.Ordinal);
        var propertiesByOwner = propertyCandidates
            .GroupBy(item => PropertyOwner(item.Decision), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Decision.SemanticId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var lifecycleStatePropertiesByOwner = lifecycleCandidates
            .Select(item =>
            {
                using var semantic = JsonDocument.Parse(item.Decision.CanonicalSemanticJson);
                var root = semantic.RootElement;
                return (
                    Subject: root.GetProperty("subject").GetString()!,
                    StateProperty: root.GetProperty("stateProperty").GetString()!,
                    EvidenceIds: item.Decision.EvidenceIds);
            })
            .GroupBy(item => (item.Subject, item.StateProperty))
            .GroupBy(group => group.Key.Subject, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<LifecycleStatePropertySupport>)group
                    .Select(state => new LifecycleStatePropertySupport(
                        state.Key.StateProperty,
                        state.SelectMany(item => item.EvidenceIds)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(id => id, StringComparer.Ordinal)
                            .ToArray()))
                    .OrderBy(item => item.Name, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        return Document(new XElement("TypeModule",
            new XAttribute("id", request.OntologyId + ".Types.Candidates"),
            new XElement("Description", "待审核候选及其受控引用宿主。"),
            new XElement("Types", support.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item =>
                TypeElement(
                    item.Key,
                    item.Value,
                    typesById.TryGetValue(item.Key, out var candidate) ? candidate : null,
                    propertiesByOwner.GetValueOrDefault(item.Key, []),
                    lifecycleStatePropertiesByOwner.GetValueOrDefault(item.Key, []))))));
    }

    private static XElement TypeElement(
        string id,
        IEnumerable<string> evidenceIds,
        (BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision)? candidate,
        IReadOnlyList<(BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision)> properties,
        IReadOnlyList<LifecycleStatePropertySupport> lifecycleStateProperties)
    {
        string description;
        string? parent = null;
        var isAbstract = false;
        if (candidate is { } typeCandidate)
        {
            using var semantic = JsonDocument.Parse(typeCandidate.Decision.CanonicalSemanticJson);
            var root = semantic.RootElement;
            description = HypothesisDescription(root.GetProperty("descriptionZh").GetString()!);
            if (root.TryGetProperty("parentConceptId", out var parentElement)) parent = parentElement.GetString();
            if (root.TryGetProperty("abstract", out var abstractElement)) isAbstract = abstractElement.GetBoolean();
        }
        else
        {
            description = $"候选关系、规则、生命周期或属性引用的宿主类型 {id}；其独立语义仍待审核。";
        }
        var type = new XElement("Type", new XAttribute("id", id), new XAttribute("status", "hypothesis"));
        if (parent is not null) type.Add(new XAttribute("parent", parent));
        if (isAbstract) type.Add(new XAttribute("abstract", "true"));
        type.Add(new XElement("Description", description));
        var ordinaryPropertyNames = properties
            .Select(item =>
            {
                using var semantic = JsonDocument.Parse(item.Decision.CanonicalSemanticJson);
                return semantic.RootElement.GetProperty("name").GetString()!;
            })
            .ToHashSet(StringComparer.Ordinal);
        var inferredStateProperties = lifecycleStateProperties
            .Where(item => !ordinaryPropertyNames.Contains(item.Name))
            .ToArray();
        if (properties.Count != 0 || inferredStateProperties.Length != 0)
        {
            type.Add(new XElement("Attributes", properties.Select(PropertyElement).Concat(inferredStateProperties.Select(StatePropertyElement))));
        }
        type.Add(EvidenceRefs(evidenceIds));
        return type;
    }

    private static XElement PropertyElement((BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision) item)
    {
        using var semantic = JsonDocument.Parse(item.Decision.CanonicalSemanticJson);
        var root = semantic.RootElement;
        return new XElement("Attribute",
            new XAttribute("name", root.GetProperty("name").GetString()!),
            new XAttribute("type", root.GetProperty("valueType").GetString()!),
            new XAttribute("required", root.GetProperty("required").GetBoolean().ToString().ToLowerInvariant()),
            new XElement("Description", HypothesisDescription(root.GetProperty("descriptionZh").GetString()!)),
            EvidenceRefs(item.Decision.EvidenceIds));
    }

    private static XElement StatePropertyElement(LifecycleStatePropertySupport stateProperty) =>
        new("Attribute",
            new XAttribute("name", stateProperty.Name),
            new XAttribute("type", "String"),
            new XAttribute("required", "true"),
            new XElement("Description", $"生命周期状态属性 {stateProperty.Name}；由待审核生命周期候选受控补齐。"),
            EvidenceRefs(stateProperty.EvidenceIds));

    private static XDocument RelationModule(BusinessOntologyCandidateBundleRequest request, IReadOnlyList<(BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision)> candidates) =>
        Document(new XElement("RelationModule",
            new XAttribute("id", request.OntologyId + ".Relations.Candidates"),
            new XElement("Description", "待审核业务关系候选。"),
            new XElement("Relations", candidates.OrderBy(item => item.Decision.SemanticId, StringComparer.Ordinal).Select(RelationElement))));

    private static XElement RelationElement((BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision) item)
    {
        using var semantic = JsonDocument.Parse(item.Decision.CanonicalSemanticJson);
        var root = semantic.RootElement;
        return new XElement("Relation",
            new XAttribute("id", root.GetProperty("id").GetString()!),
            new XAttribute("name", root.GetProperty("name").GetString()!),
            new XAttribute("from", root.GetProperty("fromConceptId").GetString()!),
            new XAttribute("to", root.GetProperty("toConceptId").GetString()!),
            new XAttribute("directed", root.GetProperty("directed").GetBoolean().ToString().ToLowerInvariant()),
            new XAttribute("min", root.GetProperty("min").GetString()!),
            new XAttribute("max", root.GetProperty("max").GetString()!),
            new XAttribute("status", "hypothesis"),
            new XElement("Description", HypothesisDescription(root.GetProperty("descriptionZh").GetString()!)),
            EvidenceRefs(item.Decision.EvidenceIds));
    }

    private static XDocument RuleModule(BusinessOntologyCandidateBundleRequest request, IReadOnlyList<(BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision)> candidates) =>
        Document(new XElement("RuleModule",
            new XAttribute("id", request.OntologyId + ".Rules.Candidates"),
            new XElement("Description", "待审核业务规则候选。"),
            new XElement("Rules", candidates.OrderBy(item => item.Decision.SemanticId, StringComparer.Ordinal).Select(RuleElement))));

    private static XElement RuleElement((BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision) item)
    {
        using var semantic = JsonDocument.Parse(item.Decision.CanonicalSemanticJson);
        var root = semantic.RootElement;
        var rule = new XElement("Rule",
            new XAttribute("id", root.GetProperty("id").GetString()!),
            new XAttribute("scope", root.GetProperty("scope").GetString()!),
            new XAttribute("kind", root.GetProperty("kind").GetString()!),
            new XAttribute("status", "hypothesis"),
            new XElement("Statement", HypothesisDescription(root.GetProperty("statementZh").GetString()!)));
        if (root.TryGetProperty("when", out var when)) rule.Add(new XElement("When", PredicateElement(when)));
        rule.Add(new XElement("Require", PredicateElement(root.GetProperty("require"))));
        rule.Add(new XElement("Violation",
            new XAttribute("code", root.GetProperty("violationCode").GetString()!),
            new XAttribute("message", root.GetProperty("violationMessageZh").GetString()!)));
        rule.Add(EvidenceRefs(item.Decision.EvidenceIds));
        return rule;
    }

    private static XDocument LifecycleModule(BusinessOntologyCandidateBundleRequest request, IReadOnlyList<(BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision)> candidates) =>
        Document(new XElement("LifecycleModule",
            new XAttribute("id", request.OntologyId + ".Lifecycles.Candidates"),
            new XElement("Description", "待审核业务生命周期候选。"),
            new XElement("StateMachines", candidates.OrderBy(item => item.Decision.SemanticId, StringComparer.Ordinal).Select(LifecycleElement))));

    private static XElement LifecycleElement((BusinessOntologyAnalysisRecord Record, OntologyCandidateDraftXmlMappingDecision Decision) item)
    {
        using var semantic = JsonDocument.Parse(item.Decision.CanonicalSemanticJson);
        var root = semantic.RootElement;
        var machine = new XElement("StateMachine",
            new XAttribute("id", root.GetProperty("id").GetString()!),
            new XAttribute("subject", root.GetProperty("subject").GetString()!),
            new XAttribute("stateProperty", root.GetProperty("stateProperty").GetString()!),
            new XAttribute("initial", root.GetProperty("initial").GetString()!),
            new XAttribute("status", "hypothesis"),
            new XElement("Description", HypothesisDescription(root.GetProperty("descriptionZh").GetString()!)),
            new XElement("States", root.GetProperty("states").EnumerateArray().Select(state =>
                new XElement("State",
                    new XAttribute("id", state.GetProperty("id").GetString()!),
                    new XAttribute("terminal", state.GetProperty("terminal").GetBoolean().ToString().ToLowerInvariant()),
                    new XElement("Description", state.GetProperty("descriptionZh").GetString()!)))));
        machine.Add(new XElement("Transitions", root.GetProperty("transitions").EnumerateArray().Select(transition =>
        {
            var output = new XElement("Transition",
                new XAttribute("id", transition.GetProperty("id").GetString()!),
                new XAttribute("action", transition.GetProperty("action").GetString()!),
                new XAttribute("from", transition.GetProperty("from").GetString()!),
                new XAttribute("to", transition.GetProperty("to").GetString()!),
                new XAttribute("status", "hypothesis"),
                new XElement("Description", HypothesisDescription(transition.GetProperty("descriptionZh").GetString()!)));
            if (transition.TryGetProperty("guard", out var guard)) output.Add(new XElement("Guard", PredicateElement(guard)));
            if (transition.TryGetProperty("effects", out var effects)) output.Add(new XElement("Effects", effects.EnumerateArray().Select(EffectElement)));
            output.Add(EvidenceRefs(item.Decision.EvidenceIds));
            return output;
        })));
        machine.Add(EvidenceRefs(item.Decision.EvidenceIds));
        return machine;
    }

    private static XDocument EvidenceModule(BusinessOntologyCandidateBundleRequest request, IEnumerable<BusinessOntologyCandidateEvidenceFact> facts) =>
        Document(new XElement("EvidenceModule",
            new XAttribute("id", request.OntologyId + ".Evidence.Analysis"),
            new XElement("Description", "候选解释直接引用的已索引证据事实。"),
            new XElement("EvidenceItems", facts.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item =>
                new XElement("Evidence",
                    new XAttribute("id", item.Id),
                    new XAttribute("repository", item.Repository),
                    new XAttribute("path", item.Path),
                    new XAttribute("symbol", item.Symbol),
                    new XAttribute("startLine", item.StartLine),
                    new XAttribute("endLine", item.EndLine),
                    new XAttribute("grade", item.Grade),
                    new XAttribute("resolver", item.Resolver),
                    new XAttribute("confidence", item.Confidence.ToString("0.###", CultureInfo.InvariantCulture)),
                    new XAttribute("sourceKind", item.SourceKind),
                    new XElement("Summary", item.Summary))))));

    private static XElement PredicateElement(JsonElement element)
    {
        var property = element.EnumerateObject().Single();
        return property.Name switch
        {
            "All" or "Any" => new XElement(property.Name, property.Value.EnumerateArray().Select(PredicateElement)),
            "Not" => new XElement("Not", PredicateElement(property.Value)),
            "PropertyPresent" => new XElement("PropertyPresent", new XAttribute("property", property.Value.GetProperty("property").GetString()!)),
            "PropertyEquals" or "PropertyNotEquals" => new XElement(property.Name,
                new XAttribute("property", property.Value.GetProperty("property").GetString()!),
                new XAttribute("value", property.Value.GetProperty("value").GetString()!)),
            "PropertyCompare" => new XElement("PropertyCompare",
                new XAttribute("property", property.Value.GetProperty("property").GetString()!),
                new XAttribute("op", property.Value.GetProperty("op").GetString()!),
                new XAttribute("value", property.Value.GetProperty("value").GetString()!)),
            "TypeIs" => new XElement("TypeIs", new XAttribute("type", property.Value.GetProperty("type").GetString()!)),
            "RelatedExists" or "EveryRelated" => new XElement(property.Name,
                new XAttribute("relation", property.Value.GetProperty("relation").GetString()!),
                PredicateElement(property.Value.GetProperty("predicate"))),
            "RelatedCount" => new XElement("RelatedCount",
                new XAttribute("relation", property.Value.GetProperty("relation").GetString()!),
                new XAttribute("op", property.Value.GetProperty("op").GetString()!),
                new XAttribute("value", property.Value.GetProperty("value").ToString())),
            "ExistsRelated" => new XElement("ExistsRelated",
                new XAttribute("relation", property.Value.GetProperty("relation").GetString()!),
                new XAttribute("direction", property.Value.GetProperty("direction").GetString()!),
                new XAttribute("targetType", property.Value.GetProperty("targetType").GetString()!)),
            _ => throw new ArgumentException($"Unsupported validated predicate '{property.Name}'."),
        };
    }

    private static XElement EffectElement(JsonElement element)
    {
        var property = element.EnumerateObject().Single();
        return property.Name switch
        {
            "SetProperty" => new XElement("SetProperty", new XAttribute("property", property.Value.GetProperty("property").GetString()!), new XAttribute("value", property.Value.GetProperty("value").GetString()!)),
            "ClearProperty" => new XElement("ClearProperty", new XAttribute("property", property.Value.GetProperty("property").GetString()!)),
            "CreateRelation" or "RemoveRelation" => new XElement(property.Name, new XAttribute("relation", property.Value.GetProperty("relation").GetString()!), new XAttribute("targetRef", property.Value.GetProperty("targetRef").GetString()!)),
            _ => throw new ArgumentException($"Unsupported validated lifecycle effect '{property.Name}'."),
        };
    }

    private static string RuleKind(OntologyCandidateDraftXmlMappingDecision decision)
    {
        using var semantic = JsonDocument.Parse(decision.CanonicalSemanticJson);
        return semantic.RootElement.GetProperty("kind").GetString()!;
    }

    private static IReadOnlyList<string> ReferencedRelationIds(OntologyCandidateDraftXmlMappingDecision decision)
    {
        using var semantic = JsonDocument.Parse(decision.CanonicalSemanticJson);
        var references = new SortedSet<string>(StringComparer.Ordinal);
        var root = semantic.RootElement;
        if (decision.XmlKind == OntologyCandidateDraftXmlKinds.Rule)
        {
            if (root.TryGetProperty("when", out var when)) AddPredicateRelationRefs(when, references);
            AddPredicateRelationRefs(root.GetProperty("require"), references);
        }
        else if (decision.XmlKind == OntologyCandidateDraftXmlKinds.Lifecycle)
        {
            foreach (var transition in root.GetProperty("transitions").EnumerateArray())
            {
                if (transition.TryGetProperty("guard", out var guard)) AddPredicateRelationRefs(guard, references);
                if (transition.TryGetProperty("effects", out var effects)) AddEffectRelationRefs(effects, references);
            }
        }
        return references.ToArray();
    }

    private static void AddPredicateRelationRefs(JsonElement predicate, ISet<string> references)
    {
        var property = predicate.EnumerateObject().Single();
        switch (property.Name)
        {
            case "All":
            case "Any":
                foreach (var child in property.Value.EnumerateArray()) AddPredicateRelationRefs(child, references);
                break;
            case "Not":
                AddPredicateRelationRefs(property.Value, references);
                break;
            case "RelatedExists":
            case "EveryRelated":
                references.Add(property.Value.GetProperty("relation").GetString()!);
                AddPredicateRelationRefs(property.Value.GetProperty("predicate"), references);
                break;
            case "RelatedCount":
            case "ExistsRelated":
                references.Add(property.Value.GetProperty("relation").GetString()!);
                break;
        }
    }

    private static void AddEffectRelationRefs(JsonElement effects, ISet<string> references)
    {
        foreach (var effect in effects.EnumerateArray())
        {
            var property = effect.EnumerateObject().Single();
            if (property.Name is "CreateRelation" or "RemoveRelation")
            {
                references.Add(property.Value.GetProperty("relation").GetString()!);
            }
        }
    }

    private static void AddPredicateTypeSupport(
        JsonElement predicate,
        IEnumerable<string> evidenceIds,
        Action<string, IEnumerable<string>> addSupport)
    {
        var property = predicate.EnumerateObject().Single();
        switch (property.Name)
        {
            case "All":
            case "Any":
                foreach (var child in property.Value.EnumerateArray()) AddPredicateTypeSupport(child, evidenceIds, addSupport);
                break;
            case "Not":
                AddPredicateTypeSupport(property.Value, evidenceIds, addSupport);
                break;
            case "TypeIs":
                addSupport(property.Value.GetProperty("type").GetString()!, evidenceIds);
                break;
            case "ExistsRelated":
                addSupport(property.Value.GetProperty("targetType").GetString()!, evidenceIds);
                break;
            case "RelatedExists":
            case "EveryRelated":
                AddPredicateTypeSupport(property.Value.GetProperty("predicate"), evidenceIds, addSupport);
                break;
        }
    }

    private static string PropertyOwner(OntologyCandidateDraftXmlMappingDecision decision) =>
        decision.SemanticId![..decision.SemanticId!.LastIndexOf('.')];

    private static string HypothesisDescription(string value) => value.Trim() + "（候选假设，待人工审核。）";
    private static XElement EvidenceRefs(IEnumerable<string> ids) => new("EvidenceRefs", ids.OrderBy(id => id, StringComparer.Ordinal).Select(id => new XElement("EvidenceRef", new XAttribute("ref", id))));
    private static void AddModuleRef(XElement modules, string moduleKind, string path, IReadOnlyDictionary<string, XDocument> files)
    {
        if (files.ContainsKey(path)) modules.Add(new XElement(moduleKind, new XAttribute("href", "vfs://./" + path)));
    }
    private static XDocument Document(XElement root) => new(new XDeclaration("1.0", "UTF-8", null), root);

    private static BusinessOntologyCandidateEvidenceFact ValidateEvidence(BusinessOntologyCandidateEvidenceFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!EvidenceId.IsMatch(fact.Id)) throw new ArgumentException("Evidence ID must be a stable namespaced token.", nameof(fact));
        if (!SafeRepository.IsMatch(fact.Repository) || Path.IsPathRooted(fact.Repository)) throw new ArgumentException("Evidence repository is invalid.", nameof(fact));
        ValidateRepositoryPath(fact.Path);
        if (string.IsNullOrWhiteSpace(fact.Symbol) || fact.StartLine < 1 || fact.EndLine < fact.StartLine || !EvidenceGrades.Contains(fact.Grade) || double.IsNaN(fact.Confidence) || fact.Confidence is < 0 or > 1 || string.IsNullOrWhiteSpace(fact.Resolver) || string.IsNullOrWhiteSpace(fact.SourceKind) || string.IsNullOrWhiteSpace(fact.Summary))
        {
            throw new ArgumentException("Evidence fact metadata is invalid.", nameof(fact));
        }
        return fact with { Id = fact.Id.Trim(), Repository = fact.Repository.Trim(), Path = fact.Path.Trim(), Symbol = fact.Symbol.Trim(), Grade = fact.Grade.Trim(), Resolver = fact.Resolver.Trim(), SourceKind = fact.SourceKind.Trim(), Summary = fact.Summary.Trim() };
    }

    private static void ValidateRequest(BusinessOntologyCandidateBundleRequest request)
    {
        if (!Fqn.IsMatch(request.OntologyId)) throw new ArgumentException("OntologyId must be a PascalCase FQN.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.GenerationId) || string.IsNullOrWhiteSpace(request.Version)) throw new ArgumentException("GenerationId and Version are required.", nameof(request));
        if (request.KnownConceptIds is null || request.KnownConceptIds.Any(id => !Fqn.IsMatch(id))) throw new ArgumentException("KnownConceptIds must be ontology FQNs.", nameof(request));
        if (request.KnownConceptIds.Distinct(StringComparer.Ordinal).Count() != request.KnownConceptIds.Count) throw new ArgumentException("KnownConceptIds must be unique.", nameof(request));
        if (request.EvidenceFacts is null) throw new ArgumentException("EvidenceFacts are required.", nameof(request));
        if (request.EvidenceFacts.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != request.EvidenceFacts.Count) throw new ArgumentException("Evidence fact IDs must be unique.", nameof(request));
    }

    private static void ValidateRepositoryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains('\\') || value.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new ArgumentException("Evidence paths must be repository-relative POSIX paths.", nameof(value));
        }
    }

    private static string ValidateBundleRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains('\\') || value.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new ArgumentException("Bundle paths must be relative POSIX paths.", nameof(value));
        }
        return value;
    }
}
