using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologyCandidateBundleBuilderTests
{
    private const string OntologyId = "SampleDomain.Ontology";
    private const string GenerationId = "candidate-fixture";
    private const string Asset = "SampleDomain.Asset";
    private const string Owner = "SampleDomain.Owner";

    public static async Task RunAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();
        var evidence = await SeedEvidenceAsync(om);
        var analysis = new BusinessOntologyAnalysisStore(om);
        var run = new BusinessOntologyAnalysisRunInput(
            "analysis:candidate-bundle",
            OntologyId,
            GenerationId,
            "fixture-agent",
            "fixture-model",
            "assemble candidate hypothesis bundle",
            "completed",
            "2026-07-19T00:00:00Z",
            "2026-07-19T00:01:00Z",
            "sha256:fixture");
        await analysis.AppendRunAsync(run);
        await analysis.AppendRecordsAsync(Records(run.RunId, evidence.Select(item => item.Id).ToArray()));

        var builder = new BusinessOntologyCandidateBundleBuilder(analysis);
        var result = await builder.BuildAsync(new BusinessOntologyCandidateBundleRequest(
            OntologyId,
            GenerationId,
            "0.0.0-hypothesis",
            [Asset, Owner],
            evidence));

        assert(result.Mappings.Count == 5 && result.Exclusions.Select(item => item.RecordId).OrderBy(item => item, StringComparer.Ordinal).SequenceEqual([
                    "draft:unresolved-lifecycle-effect", "draft:unresolved-relation"
                ]) && result.Exclusions.All(item => item.Reason.StartsWith("unresolved_relation_reference:", StringComparison.Ordinal)),
            "type, property, relation, rule, and lifecycle drafts should map deterministically while unresolved relation references are excluded");
        assert(result.Files.Keys.OrderBy(item => item, StringComparer.Ordinal).SequenceEqual([
                "evidence/analysis.xml", "lifecycles/candidates.xml", "ontology.xml", "relations/candidates.xml", "rules/candidates.xml", "types/candidates.xml"
            ]), "staged bundle should use fixed module paths only");

        var types = result.Files["types/candidates.xml"];
        var attribute = types.Descendants("Attribute").Single(item => item.Attribute("name")?.Value == "assetCode");
        assert(attribute.Attribute("name")?.Value == "assetCode" && types.Descendants("Property").Any() == false,
            "a property draft must become an Attribute under its owning Type, never an edge Property");
        assert(types.Descendants("Type")
                   .Where(item => item.Attribute("id")?.Value == Asset)
                   .SelectMany(item => item.Descendants("Attribute"))
                   .Any(item => item.Attribute("name")?.Value == "status" && item.Attribute("type")?.Value == "String"),
            "a lifecycle candidate must add an evidence-bound state attribute to its subject Type for generated DSL validation");
        assert(types.Descendants("Type").Any(item => item.Attribute("id")?.Value == Asset) &&
               types.Descendants("Type").Any(item => item.Attribute("id")?.Value == Owner),
            "relation, rule, lifecycle, and property references should receive controlled hypothesis type hosts");
        assert(result.Files.Values.SelectMany(document => document.Descendants())
                .Where(element => element.Name.LocalName is "Type" or "Relation" or "Rule" or "StateMachine" or "Transition")
                .All(element => element.Attribute("status")?.Value == "hypothesis" && element.Element("EvidenceRefs")?.Elements("EvidenceRef").Any() == true) &&
               result.Files.Values.SelectMany(document => document.Descendants("Attribute"))
                .All(element => element.Element("EvidenceRefs")?.Elements("EvidenceRef").Any() == true),
            "every generated semantic object should be a direct-evidence hypothesis");
        assert(result.Files["evidence/analysis.xml"].Descendants("Evidence").All(item => item.Attribute("grade")?.Value == "inferred") &&
               !result.Files["evidence/analysis.xml"].ToString().Contains("/tmp/", StringComparison.Ordinal),
            "evidence module should contain safe, repository-relative known facts without source excerpts");

        var staging = Path.Combine(Path.GetTempPath(), "onto-candidate-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = await builder.WriteStagingAsync(result, staging);
            assert(written == Path.GetFullPath(staging) && File.Exists(Path.Combine(staging, "ontology.xml")),
                "builder should write a new staging directory without publishing it");
            var overwriteRejected = false;
            try { await builder.WriteStagingAsync(result, staging); }
            catch (IOException) { overwriteRejected = true; }
            assert(overwriteRejected, "staging must refuse an existing directory");
            var root = XDocument.Load(Path.Combine(staging, "ontology.xml"));
            assert(root.Root?.Name.LocalName == "Ontology" && root.Root.Attribute("id")?.Value == OntologyId,
                "staged root should assemble ontology XML modules");
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }

        var empty = await builder.BuildAsync(new BusinessOntologyCandidateBundleRequest(
            OntologyId,
            GenerationId,
            "0.0.0-hypothesis",
            [Asset, Owner],
            []));
        assert(!empty.HasMappableCandidates && empty.Files.Count == 0 && await builder.WriteStagingAsync(empty, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))) is null,
            "unavailable evidence must produce no hypothesis bundle and no filesystem output");

        var uppercaseEvidenceRejected = false;
        try
        {
            await builder.BuildAsync(new BusinessOntologyCandidateBundleRequest(
                OntologyId,
                GenerationId,
                "0.0.0-hypothesis",
                [Asset, Owner],
                [evidence[0] with { Id = "Evidence:uppercase" }]));
        }
        catch (ArgumentException)
        {
            uppercaseEvidenceRejected = true;
        }
        assert(uppercaseEvidenceRejected,
            "generated evidence IDs must be lowercase namespaced tokens accepted by the XML DSL");

        var partialEvidence = await builder.BuildAsync(new BusinessOntologyCandidateBundleRequest(
            OntologyId,
            GenerationId,
            "0.0.0-hypothesis",
            [Asset, Owner],
            [evidence[0]]));
        assert(partialEvidence.Mappings.Select(mapping => mapping.RecordId).SequenceEqual(["draft:type"], StringComparer.Ordinal)
               && partialEvidence.Files.Values.SelectMany(document => document.Descendants("EvidenceRef"))
                   .All(reference => reference.Attribute("ref")?.Value == evidence[0].Id)
               && !partialEvidence.Files.Values.SelectMany(document => document.Descendants("EvidenceRef"))
                   .Any(reference => reference.Attribute("ref")?.Value == evidence[1].Id),
            "a draft whose direct inferred evidence is absent from the export request must be excluded rather than emitted with an indirect or unavailable EvidenceRef");

        await PublicationContractAsync(builder, result, evidence, assert);
        await ArtifactOutputContractAsync(analysis, builder, result, evidence, assert);
        await SharedExportOperationContractAsync(om, assert);
    }

    private static async Task PublicationContractAsync(
        BusinessOntologyCandidateBundleBuilder builder,
        BusinessOntologyCandidateBundleBuildResult result,
        IReadOnlyList<BusinessOntologyCandidateEvidenceFact> evidence,
        Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "onto-candidate-publication-" + Guid.NewGuid().ToString("N"));
        try
        {
            var output = Path.Combine(root, "v2");
            var publisher = new BusinessOntologyCandidateBundlePublisher(builder);
            var request = new BusinessOntologyCandidateBundlePublicationRequest(
                new BusinessOntologyCandidateBundleRequest(
                    OntologyId,
                    GenerationId,
                    "0.0.0-hypothesis",
                    [Asset, Owner],
                    evidence),
                output,
                FindBunExecutable(),
                Path.Combine(FindWorkspaceRoot(), "skills", "ontology-xml-dsl", "scripts", "validate-ontology-xml.ts"),
                "candidate-fixture");

            var published = await publisher.PublishBuiltAsync(result, request);
            assert(published.Published && published.Validation is { Succeeded: true } &&
                   File.Exists(Path.Combine(published.PublishedDirectory!, "ontology.xml")),
                "a valid candidate bundle must pass the real generated XML validator before its atomic publication: "
                + (published.Validation?.StandardOutput ?? "") + (published.Validation?.StandardError ?? ""));
            assert(!Directory.EnumerateDirectories(output, ".staging-*", SearchOption.TopDirectoryOnly).Any(),
                "a successful publication must leave no staging directory behind");

            var sentinel = Path.Combine(output, "existing");
            Directory.CreateDirectory(sentinel);
            await File.WriteAllTextAsync(Path.Combine(sentinel, "keep.txt"), "unchanged");
            var invalidFiles = result.Files.ToDictionary(item => item.Key, item => new XDocument(item.Value), StringComparer.Ordinal);
            invalidFiles["types/candidates.xml"].Root!.Element("Types")!.Add(new XElement("UnknownCandidateElement"));
            var invalid = result with { Files = invalidFiles };
            var invalidRejected = false;
            try { await publisher.PublishBuiltAsync(invalid, request with { BundleName = "invalid-fixture" }); }
            catch (ArgumentException) { invalidRejected = true; }
            assert(invalidRejected &&
                   !Directory.Exists(Path.Combine(output, "invalid-fixture")) &&
                   await File.ReadAllTextAsync(Path.Combine(sentinel, "keep.txt")) == "unchanged" &&
                   !Directory.EnumerateDirectories(output, ".staging-*", SearchOption.TopDirectoryOnly).Any(),
                "a caller-tampered prebuilt bundle must be rejected before staging and leave existing output untouched");

            var acceptedFiles = result.Files.ToDictionary(item => item.Key, item => new XDocument(item.Value), StringComparer.Ordinal);
            acceptedFiles["types/candidates.xml"].Descendants("Type").First().SetAttributeValue("status", "accepted");
            var accepted = result with { Files = acceptedFiles };
            var acceptedRejected = false;
            try { await publisher.PublishBuiltAsync(accepted, request with { BundleName = "accepted-fixture" }); }
            catch (ArgumentException) { acceptedRejected = true; }
            assert(acceptedRejected &&
                   !Directory.Exists(Path.Combine(output, "accepted-fixture")) &&
                   !Directory.EnumerateDirectories(output, ".staging-*", SearchOption.TopDirectoryOnly).Any(),
                "candidate publication must reject a caller-injected accepted declaration before it can reach the filesystem");

            var mismatchedWorkspaceRejected = false;
            try
            {
                await publisher.PublishBuiltAsync(result with { OntologyId = "OtherDomain.Ontology" }, request with { BundleName = "mismatch-fixture" });
            }
            catch (ArgumentException)
            {
                mismatchedWorkspaceRejected = true;
            }
            assert(mismatchedWorkspaceRejected && !Directory.Exists(Path.Combine(output, "mismatch-fixture")),
                "prebuilt publication seams must reject a different analysis workspace");

            var emptyOutput = Path.Combine(root, "empty");
            var empty = await publisher.PublishAsync(request with
            {
                BundleRequest = request.BundleRequest with { EvidenceFacts = [] },
                OutputDirectory = emptyOutput,
                BundleName = "must-not-exist",
            });
            assert(!empty.Published && empty.Validation is null && !Directory.Exists(emptyOutput),
                "zero mappable candidates must not create an output directory or bundle");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task ArtifactOutputContractAsync(
        BusinessOntologyAnalysisStore analysis,
        BusinessOntologyCandidateBundleBuilder builder,
        BusinessOntologyCandidateBundleBuildResult build,
        IReadOnlyList<BusinessOntologyCandidateEvidenceFact> evidence,
        Action<bool, string> assert)
    {
        await analysis.AppendRecordsAsync([
            new BusinessOntologyAnalysisRecordInput(
                "analysis:candidate-bundle", "observation:asset-service", "observation", "use_case", Asset,
                "观察到资产服务", "{\"observation\":\"asset service\"}", "observed", 0.1,
                "sha256:observation", "2026-07-19T00:00:11Z", [evidence[0].Id]),
            new BusinessOntologyAnalysisRecordInput(
                "analysis:candidate-bundle", "conflict:asset-owner", "conflict", "relation", Asset,
                "资产归属存在冲突", "{\"conflict\":\"owner\"}", "open", 0.7,
                "sha256:conflict", "2026-07-19T00:00:12Z", [evidence[1].Id]),
        ]);

        var root = Path.Combine(Path.GetTempPath(), "onto-candidate-artifacts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var output = Path.Combine(root, "v2");
            var publication = await new BusinessOntologyCandidateBundlePublisher(builder).PublishBuiltAsync(
                build,
                new BusinessOntologyCandidateBundlePublicationRequest(
                    new BusinessOntologyCandidateBundleRequest(OntologyId, GenerationId, "0.0.0-hypothesis", [Asset, Owner], evidence),
                    output,
                    FindBunExecutable(),
                    Path.Combine(FindWorkspaceRoot(), "skills", "ontology-xml-dsl", "scripts", "validate-ontology-xml.ts"),
                    "candidate-fixture"));
            var propertyMapping = build.Mappings.Single(mapping => mapping.CandidateXmlId == Asset + ".assetCode");
            var typesPath = Path.Combine(publication.PublishedDirectory!, "types", "candidates.xml");
            var types = XDocument.Load(typesPath);
            types.Descendants("Attribute").Single(attribute => attribute.Attribute("name")?.Value == "assetCode").Remove();
            types.Save(typesPath);

            var writer = new BusinessOntologyCandidateArtifactWriter(analysis);
            var written = await writer.WriteAsync(
                build,
                new BusinessOntologyCandidateArtifactWriteRequest(
                    new BusinessOntologyCandidateBundleRequest(OntologyId, GenerationId, "0.0.0-hypothesis", [Asset, Owner], evidence),
                    publication.PublishedDirectory!,
                    "2026-07-19T00:02:00Z",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [build.Mappings[0].CandidateXmlId] = BusinessOntologyReviewSuggestions.Accept,
                    }));

            assert(File.Exists(Path.Combine(written.ArtifactDirectory, "diagnosis.json"))
                   && File.Exists(Path.Combine(written.ArtifactDirectory, "diagnosis.md"))
                   && File.Exists(Path.Combine(written.ArtifactDirectory, "advisory-review.json"))
                   && File.Exists(Path.Combine(written.ArtifactDirectory, "advisory-review.md")),
                "diagnosis and advisory review artifacts must be published together in a new artifact directory");
            assert(written.Diagnosis.Items.Count == 9
                   && written.Diagnosis.Items.Any(item => item.RecordId == "observation:asset-service"
                       && item.CandidateXmlId is null
                       && item.MappingDecision.RejectReasons.SequenceEqual([OntologyCandidateDraftRejectReasons.NotCandidateDraftRecord], StringComparer.Ordinal))
                   && written.Diagnosis.Items.Any(item => item.RecordId == "draft:unresolved-relation"
                       && item.CandidateXmlId is null
                       && item.MappingDecision.ExportSuppressionReasons.Single().StartsWith("unresolved_relation_reference:", StringComparison.Ordinal))
                   && written.Diagnosis.Items.Any(item => item.RecordId == propertyMapping.RecordId
                       && item.CandidateXmlId is null
                       && item.MappingDecision.ExportSuppressionReasons.SequenceEqual(["candidate_xml_not_declared_in_published_bundle"], StringComparer.Ordinal)),
                "diagnosis must include every workspace record and give XML-missing candidates no id plus an exact publication suppression reason");
            assert(written.ReviewPacket.Items.Count == build.Mappings.Count - 1
                   && written.ReviewPacket.Items.All(item => item.CandidateXmlId.Length != 0)
                   && written.ReviewPacket.Items.All(item => item.CandidateXmlId != propertyMapping.CandidateXmlId)
                   && written.ReviewPacket.Items.All(item => item.SuggestedDecision is "accept" or "reject" or "defer" or "request_evidence"),
                "review packet must contain only IDs parsed from the published hypothesis XML with closed advisory suggestions");
            var diagnosisJson = await File.ReadAllTextAsync(Path.Combine(written.ArtifactDirectory, "diagnosis.json"));
            assert(!diagnosisJson.Contains("onto_review", StringComparison.OrdinalIgnoreCase)
                   && !diagnosisJson.Contains("materialization", StringComparison.OrdinalIgnoreCase),
                "artifact output must remain independent of accepted review and materialization relations");

            var overwriteRejected = false;
            try
            {
                await writer.WriteAsync(build, new BusinessOntologyCandidateArtifactWriteRequest(
                    new BusinessOntologyCandidateBundleRequest(OntologyId, GenerationId, "0.0.0-hypothesis", [Asset, Owner], evidence),
                    publication.PublishedDirectory!, "2026-07-19T00:03:00Z", new Dictionary<string, string>()));
            }
            catch (IOException) { overwriteRejected = true; }
            assert(overwriteRejected, "artifact writer must never overwrite an existing artifact directory");

            var symlinkPublication = await new BusinessOntologyCandidateBundlePublisher(builder).PublishBuiltAsync(
                build,
                new BusinessOntologyCandidateBundlePublicationRequest(
                    new BusinessOntologyCandidateBundleRequest(OntologyId, GenerationId, "0.0.0-hypothesis", [Asset, Owner], evidence),
                    output,
                    FindBunExecutable(),
                    Path.Combine(FindWorkspaceRoot(), "skills", "ontology-xml-dsl", "scripts", "validate-ontology-xml.ts"),
                    "candidate-symlink-fixture"));
            var outsideModules = Path.Combine(root, "outside-modules");
            Directory.CreateDirectory(outsideModules);
            await File.WriteAllTextAsync(
                Path.Combine(outsideModules, "candidates.xml"),
                await File.ReadAllTextAsync(Path.Combine(symlinkPublication.PublishedDirectory!, "types", "candidates.xml")));
            Directory.Delete(Path.Combine(symlinkPublication.PublishedDirectory!, "types"), recursive: true);
            Directory.CreateSymbolicLink(Path.Combine(symlinkPublication.PublishedDirectory!, "types"), outsideModules);
            var symlinkRejected = false;
            try
            {
                await writer.WriteAsync(build, new BusinessOntologyCandidateArtifactWriteRequest(
                    new BusinessOntologyCandidateBundleRequest(OntologyId, GenerationId, "0.0.0-hypothesis", [Asset, Owner], evidence),
                    symlinkPublication.PublishedDirectory!, "2026-07-19T00:04:00Z", new Dictionary<string, string>(), "analysis-symlink"));
            }
            catch (ArgumentException)
            {
                symlinkRejected = true;
            }
            assert(symlinkRejected && !Directory.Exists(Path.Combine(symlinkPublication.PublishedDirectory!, "analysis-symlink")),
                "artifact writer must reject published bundles whose modules are replaced with symbolic links after validation");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string FindWorkspaceRoot()
    {
        for (var current = new DirectoryInfo(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "skills", "ontology-xml-dsl", "scripts", "validate-ontology-xml.ts")))
            {
                return current.FullName;
            }
        }
        throw new InvalidOperationException("Could not locate the ontology XML DSL workspace root.");
    }

    private static string FindBunExecutable()
    {
        var candidates = new List<string>();
        var bunInstall = Environment.GetEnvironmentVariable("BUN_INSTALL");
        if (!string.IsNullOrWhiteSpace(bunInstall)) candidates.Add(Path.Combine(bunInstall, "bin", "bun"));
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, "bun")));
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("Bun is required for generated ontology XML validation tests.");
    }

    private static async Task<IReadOnlyList<BusinessOntologyCandidateEvidenceFact>> SeedEvidenceAsync(CozoOm om)
    {
        var root = Path.Combine(Path.GetTempPath(), "onto-candidate-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "AssetService.java"), "class AssetService { }");
        var claims = Enumerable.Range(1, 5).Select(index =>
        {
            var payload = JsonSerializer.Serialize(new { index });
            var claimId = CodeSemanticClaimIdentity.Create(
                "file:asset-service",
                CodeSemanticClaimKinds.StateAssignment,
                payload,
                index,
                index);
            return new CodeSemanticClaimFact(
                claimId,
                "symbol:asset-service",
                CodeSemanticClaimKinds.StateAssignment,
                payload,
                "file:asset-service",
                index,
                index,
                0.9,
                "treesitter",
                "候选业务语义的索引证据。");
        }).ToArray();
        await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            [new CodeRepositoryFact("repo:sample", root, "sample")],
            [new CodeFileFact("file:asset-service", "repo:sample", "src/AssetService.java", "java")],
            [new CodeSymbolFact("symbol:asset-service", "file:asset-service", "AssetService", "class", 1, 1, Lang: "java")],
            SemanticClaims: claims));
        return claims.Select((claim, index) => new BusinessOntologyCandidateEvidenceFact(
            claim.ClaimId,
            "sample-repository",
            "src/AssetService.java",
            "java:sample.AssetService",
            index + 1,
            index + 1,
            "inferred",
            "treesitter",
            0.9,
            "code",
            "已索引代码事实支持该候选。"))
            .ToArray();
    }

    private static IReadOnlyList<BusinessOntologyAnalysisRecordInput> Records(string runId, IReadOnlyList<string> evidence) =>
    [
        Record("draft:type", "concept", "type", "SampleDomain.Ticket", "候选工单类型", """
            {"schemaVersion":"candidate-draft-to-ontology-xml-v1","xmlKind":"type","semantic":{"id":"SampleDomain.Ticket","descriptionZh":"资产工单。","abstract":false}}
            """, evidence[0]),
        Record("draft:property", "attribute", "property", Asset + ".assetCode", "候选资产编码", """
            {"schemaVersion":"candidate-draft-to-ontology-xml-v1","xmlKind":"property","semantic":{"ownerConceptId":"__ASSET__","name":"assetCode","valueType":"String","required":true,"descriptionZh":"资产编码。"}}
            """.Replace("__ASSET__", Asset, StringComparison.Ordinal), evidence[1]),
        Record("draft:relation", "relation", "relation", "SampleDomain.Relation.AssetOwnedBy", "候选资产归属", """
            {"schemaVersion":"candidate-draft-to-ontology-xml-v1","xmlKind":"relation","semantic":{"id":"SampleDomain.Relation.AssetOwnedBy","name":"ownedBy","fromConceptId":"__ASSET__","toConceptId":"__OWNER__","directed":true,"min":"0","max":"1","descriptionZh":"资产归属责任人。"}}
            """.Replace("__ASSET__", Asset, StringComparison.Ordinal).Replace("__OWNER__", Owner, StringComparison.Ordinal), evidence[2]),
        Record("draft:rule", "rule", "rule", "SampleDomain.Rule.AssetCodeRequired", "候选编码规则", """
            {"schemaVersion":"candidate-draft-to-ontology-xml-v1","xmlKind":"rule","semantic":{"id":"SampleDomain.Rule.AssetCodeRequired","scope":"__ASSET__","kind":"Conditional","statementZh":"资产编码必须存在。","require":{"PropertyPresent":{"property":"assetCode"}},"violationCode":"ASSET_CODE_REQUIRED","violationMessageZh":"资产编码不能为空。"}}
            """.Replace("__ASSET__", Asset, StringComparison.Ordinal), evidence[3]),
        Record("draft:lifecycle", "lifecycle", "lifecycle", "SampleDomain.Lifecycle.Asset", "候选资产生命周期", """
            {"schemaVersion":"candidate-draft-to-ontology-xml-v1","xmlKind":"lifecycle","semantic":{"id":"SampleDomain.Lifecycle.Asset","subject":"__ASSET__","stateProperty":"status","initial":"draft","descriptionZh":"资产登记生命周期。","states":[{"id":"draft","terminal":false,"descriptionZh":"草稿"},{"id":"registered","terminal":true,"descriptionZh":"已登记"}],"transitions":[{"id":"SampleDomain.Transition.RegisterAsset","action":"register","from":"draft","to":"registered","descriptionZh":"登记资产。","effects":[{"SetProperty":{"property":"status","value":"registered"}}]}]}}
            """.Replace("__ASSET__", Asset, StringComparison.Ordinal), evidence[4]),
        Record("draft:unresolved-relation", "rule", "rule", "SampleDomain.Rule.RequiresMissingRelation", "未闭包规则", """
            {"schemaVersion":"candidate-draft-to-ontology-xml-v1","xmlKind":"rule","semantic":{"id":"SampleDomain.Rule.RequiresMissingRelation","scope":"__ASSET__","kind":"Existential","statementZh":"资产需要未声明关系。","require":{"ExistsRelated":{"relation":"SampleDomain.Relation.NotExported","direction":"out","targetType":"__OWNER__"}},"violationCode":"RELATION_REQUIRED","violationMessageZh":"关联缺失。"}}
            """.Replace("__ASSET__", Asset, StringComparison.Ordinal).Replace("__OWNER__", Owner, StringComparison.Ordinal), evidence[4]),
        Record("draft:unresolved-lifecycle-effect", "lifecycle", "lifecycle", "SampleDomain.Lifecycle.AssetQueue", "未闭包生命周期", """
            {"schemaVersion":"candidate-draft-to-ontology-xml-v1","xmlKind":"lifecycle","semantic":{"id":"SampleDomain.Lifecycle.AssetQueue","subject":"__ASSET__","stateProperty":"queueState","initial":"pending","descriptionZh":"资产排队生命周期。","states":[{"id":"pending","terminal":false,"descriptionZh":"待处理"},{"id":"queued","terminal":true,"descriptionZh":"已排队"}],"transitions":[{"id":"SampleDomain.Transition.QueueAsset","action":"queue","from":"pending","to":"queued","descriptionZh":"加入队列。","effects":[{"CreateRelation":{"relation":"SampleDomain.Relation.NotExported","targetRef":"subject.queue"}}]}]}}
            """.Replace("__ASSET__", Asset, StringComparison.Ordinal), evidence[4])
    ];

    private static BusinessOntologyAnalysisRecordInput Record(string recordId, string subjectKind, string kind, string subjectId, string title, string body, string evidenceId) =>
        new("analysis:candidate-bundle", recordId, "candidate_draft", subjectKind, subjectId, title, body, "proposed", 0.2, "sha256:" + recordId, "2026-07-19T00:00:10Z", [evidenceId]);

    private static async Task SharedExportOperationContractAsync(CozoOm om, Action<bool, string> assert)
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "onto-candidate-shared-operation-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("DEPA_WIKI_CANDIDATE_ONTOLOGY_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("DEPA_WIKI_CANDIDATE_ONTOLOGY_ROOT", outputRoot);
            var runner = new LlmWikiToolRunner(om);
            var operation = "export_business_ontology_candidates";
            assert(LlmWikiToolRunner.ToolsJson().Any(tool => tool?["name"]?.GetValue<string>() == operation),
                "the shared CLI/MCP/HTTP tool matrix must advertise the fixed candidate export operation");

            var result = await runner.CallAsync(operation, new JsonObject
            {
                ["ontologyId"] = OntologyId,
                ["generationId"] = GenerationId,
                ["analysisRunId"] = "analysis:candidate-bundle",
                ["version"] = "0.0.0-hypothesis",
                ["bundleId"] = "shared-export",
            });
            var summary = JsonSerializer.SerializeToNode(result)?.AsObject()
                ?? throw new InvalidOperationException("candidate export should return a JSON object");
            assert(summary["published"]?.GetValue<bool>() == true &&
                   summary["bundlePath"]?.GetValue<string>() == "shared-export" &&
                   summary["candidates"]?["exported"]?.GetValue<int>() == 5 &&
                   summary["artifacts"]?["diagnosisItems"]?.GetValue<int>() == 9,
                "the shared operation should publish only a relative bundle path with candidate and diagnosis counts");
            assert(File.Exists(Path.Combine(outputRoot, "shared-export", "ontology.xml")) &&
                   File.Exists(Path.Combine(outputRoot, "shared-export", "analysis", "diagnosis.json")),
                "the shared operation should compose publication and independent diagnosis artifacts under its configured root");
            assert(!summary.ToJsonString().Contains(outputRoot, StringComparison.Ordinal),
                "the shared operation response must not disclose the configured absolute output root");

            var rawSqlRejected = false;
            try
            {
                await runner.CallAsync(operation, new JsonObject
                {
                    ["ontologyId"] = OntologyId,
                    ["generationId"] = GenerationId,
                    ["analysisRunId"] = "analysis:candidate-bundle",
                    ["sql"] = "?[x] := *onto_concept{x}",
                });
            }
            catch (ArgumentException)
            {
                rawSqlRejected = true;
            }
            assert(rawSqlRejected, "the shared operation must reject raw SQL and every non-contract argument before publication");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEPA_WIKI_CANDIDATE_ONTOLOGY_ROOT", previousRoot);
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { }
        }
    }

}
