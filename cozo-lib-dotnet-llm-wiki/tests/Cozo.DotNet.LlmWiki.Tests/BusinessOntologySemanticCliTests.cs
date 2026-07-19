using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class BusinessOntologySemanticCliTests
{
    private const string OntologyId = "SampleDomain.Ontology";
    private const string Record = OntologyId + ".Record";
    private const string Supplier = OntologyId + ".Supplier";
    private static readonly string OntologyDslWorkspaceRoot = FindOntologyDslWorkspaceRoot();
    private static readonly string OntologyDslValidatorPath = Path.Combine(
        OntologyDslWorkspaceRoot,
        "skills",
        "ontology-xml-dsl",
        "scripts",
        "validate-ontology-xml.ts");
    private static readonly string BunPath = FindBunExecutable();

    public static async Task RunAsync(Action<bool, string> assert)
    {
        AssertStrictValidatorPrerequisites();
        var root = Path.Combine(
            Path.GetTempPath(),
            "onto-semantic-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await AssertArgumentContractAsync(root, assert);
            await AssertReviewPromotionAsync(root, assert);
            await AssertPurgeCommandAsync(root, assert);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort fixture cleanup must not hide an assertion.
            }
        }
    }

    private static async Task AssertArgumentContractAsync(
        string root,
        Action<bool, string> assert)
    {
        var repository = Path.Combine(root, "repository");
        var corroborationRepository = Path.Combine(root, "frontend");
        var corroborationDatabase = Path.Combine(root, "frontend.db");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(corroborationRepository);
        await File.WriteAllBytesAsync(corroborationDatabase, []);

        var parsed = BusinessOntologySemanticCli.ParseDeriveSemantics(
            LlmWikiCliOptions.Parse(
            [
                "--ontology-id", OntologyId,
                "--generation-id", "semantic-fixture",
                "--source-fingerprint", "ck-fixture",
                "--repo", repository,
                "--mode", "deterministic",
                "--corroboration-db", corroborationDatabase,
                "--corroboration-repo", corroborationRepository,
            ]));
        assert(
            parsed.OntologyId == OntologyId
            && parsed.Mode == BusinessOntologySemanticProjectionModes.Deterministic
            && parsed.CorroborationDatabasePath == Path.GetFullPath(corroborationDatabase),
            "derive-semantics should map and normalize its required and corroboration arguments");

        var v2Output = Path.Combine(root, "v2-output");
        var experiment = BusinessOntologySemanticCli.ParseDeriveSemantics(
            LlmWikiCliOptions.Parse(
            [
                "--ontology-id", OntologyId,
                "--generation-id", "semantic-v2-fixture",
                "--source-fingerprint", "ck-fixture",
                "--repo", repository,
                "--mode", "assisted",
                "--experiment", "v2",
                "--max-slices", "2",
                "--experiment-out", v2Output,
            ]));
        assert(
            experiment.Experiment == OntologySemanticExperimentProfiles.V2
            && experiment.MaxSlices == 2
            && experiment.ExperimentOutputDirectory == Path.GetFullPath(v2Output),
            "v2 should require an explicit assisted experiment profile, bounded slice count, and isolated output root");

        AssertRejected(
            () => BusinessOntologySemanticCli.ParseDeriveSemantics(
                LlmWikiCliOptions.Parse(
                [
                    "--ontology-id", OntologyId,
                    "--generation-id", "semantic-fixture",
                    "--source-fingerprint", "ck-fixture",
                    "--repo", repository,
                    "--mode", "automatic",
                ])),
            "--mode",
            assert,
            "derive-semantics should reject implicit or unknown execution modes");
        AssertRejected(
            () => BusinessOntologySemanticCli.ParseDeriveSemantics(
                LlmWikiCliOptions.Parse(
                [
                    "--ontology-id", OntologyId,
                    "--generation-id", "semantic-v2-fixture",
                    "--source-fingerprint", "ck-fixture",
                    "--repo", repository,
                    "--mode", "deterministic",
                    "--experiment", "v2",
                    "--experiment-out", v2Output,
                ])),
            "requires --mode assisted",
            assert,
            "v2/v3 experiments must not silently run through deterministic mode");
        AssertRejected(
            () => BusinessOntologySemanticCli.ParseDeriveSemantics(
                LlmWikiCliOptions.Parse(
                [
                    "--ontology-id", OntologyId,
                    "--generation-id", "semantic-v2-fixture",
                    "--source-fingerprint", "ck-fixture",
                    "--repo", repository,
                    "--mode", "assisted",
                    "--experiment", "v2",
                    "--max-slices", "9",
                    "--experiment-out", v2Output,
                ])),
            "1 to 8",
            assert,
            "experiment budget must reject a slice count above its hard profile maximum before a model call");
        AssertRejected(
            () => BusinessOntologySemanticCli.ParseDeriveSemantics(
                LlmWikiCliOptions.Parse(
                [
                    "--ontology-id", OntologyId,
                    "--generation-id", "semantic-fixture",
                    "--source-fingerprint", "ck-fixture",
                    "--repo", repository,
                    "--mode", "deterministic",
                    "--corroboration-db", corroborationDatabase,
                ])),
            "supplied together",
            assert,
            "corroboration database and repository must be an explicit pair");

        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        var cli = new BusinessOntologySemanticCli(om, new BusinessOntologyStore(om));
        var preflightRejected = false;
        try
        {
            await cli.DeriveSemanticsAsync(parsed with
            {
                CorroborationDatabasePath = null,
                CorroborationRepositoryPath = null,
            });
        }
        catch (InvalidOperationException ex)
        {
            preflightRejected = ex.Message.Contains(
                "reindex",
                StringComparison.OrdinalIgnoreCase);
        }
        var relationNames = (await om.Runtime.Store.RunAsync("::relations")).Rows
            .Select(row => row[0].GetString() ?? "")
            .ToArray();
        assert(
            preflightRejected
            && !relationNames.Any(name => name.StartsWith("onto_", StringComparison.Ordinal)),
            "derive-semantics should report reindex required without implicitly creating onto_* or CodeKnowledge schema");

        var assistedRejected = false;
        try
        {
            await cli.DeriveSemanticsAsync(parsed with
            {
                Mode = BusinessOntologySemanticProjectionModes.Assisted,
                CorroborationDatabasePath = null,
                CorroborationRepositoryPath = null,
            });
        }
        catch (InvalidOperationException ex)
        {
            assistedRejected = ex.Message.Contains(
                "LLM client",
                StringComparison.Ordinal);
        }
        assert(
            assistedRejected,
            "assisted derive-semantics should require an explicitly available LLM client");

        AssertRejected(
            () => BusinessOntologySemanticCli.ParsePurge(
                LlmWikiCliOptions.Parse(
                [
                    "--ontology-id", OntologyId,
                    "--generation-id", "semantic-fixture",
                ])),
            "--reason",
            assert,
            "ontology purge should require an explicit audit reason");
    }

    private static async Task AssertPurgeCommandAsync(
        string root,
        Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.Runtime.Store.RunAsync(":create ck_cli_sentinel {id => value}");
        await om.Runtime.Store.RunAsync(":create depa_cli_sentinel {id => value}");
        await om.Runtime.Store.RunAsync("?[id, value] <- [[\"ck\", \"unchanged\"]] :put ck_cli_sentinel {id => value}");
        await om.Runtime.Store.RunAsync("?[id, value] <- [[\"depa\", \"unchanged\"]] :put depa_cli_sentinel {id => value}");

        var store = new BusinessOntologyStore(om);
        var fixture = Fixture.Create();
        await store.ReplaceGenerationAsync(fixture.Generation);
        await store.AppendReviewsAsync(
            OntologyId,
            fixture.Generation.GenerationId,
            [
                new BusinessOntologyReviewEntry(
                    new BusinessOntologyReview(
                        "review:purge-cli",
                        fixture.Relation.Id,
                        "rejected",
                        "user:fixture",
                        "CLI purge 后仍保留审核日志。",
                        "2026-07-18T01:00:00.0000000+00:00"),
                    fixture.Relation.EvidenceIds),
            ]);

        var parsed = BusinessOntologySemanticCli.ParsePurge(
            LlmWikiCliOptions.Parse(
            [
                "--ontology-id", OntologyId,
                "--generation-id", fixture.Generation.GenerationId,
                "--reason", "remove invalid semantic dogfood generation",
                "--repo", root,
            ]));
        var summary = await new BusinessOntologySemanticCli(om, store)
            .PurgeAsync(parsed);
        var json = JsonSerializer.Serialize(summary);
        var reviewHistory = await store.ReadReviewHistoryAsync(OntologyId, fixture.Relation.Id);
        var ckRows = await om.Runtime.Store.RunAsync("?[value] := *ck_cli_sentinel{id: \"ck\", value}");
        var depaRows = await om.Runtime.Store.RunAsync("?[value] := *depa_cli_sentinel{id: \"depa\", value}");
        assert(
            summary.Purged
            && summary.TombstoneWritten
            && json.Contains("\"OntologyId\"", StringComparison.Ordinal)
            && reviewHistory.Count == 1
            && ckRows.Rows.Single()[0].GetString() == "unchanged"
            && depaRows.Rows.Single()[0].GetString() == "unchanged",
            "ontology purge should execute through the semantic CLI and return a machine-readable summary without touching review/ck/depa data");
    }

    private static async Task AssertReviewPromotionAsync(
        string root,
        Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        var store = new BusinessOntologyStore(om);
        var fixture = Fixture.Create();
        await store.ReplaceGenerationAsync(fixture.Generation);
        await store.AppendReviewsAsync(
            OntologyId,
            fixture.Generation.GenerationId,
            [
                new BusinessOntologyReviewEntry(
                    new BusinessOntologyReview(
                        "review:stale",
                        fixture.Stale.Id,
                        "accepted",
                        "user:fixture",
                        "旧 generation 中曾确认。",
                        "2026-07-17T00:00:00.0000000+00:00"),
                    fixture.Stale.EvidenceIds),
            ]);
        await store.ReplaceGenerationAsync(fixture.Generation with
        {
            GenerationId = "semantic-active",
            Candidates = fixture.Generation.Candidates
                .Where(item => item.Id != fixture.Stale.Id)
                .ToArray(),
        });

        var decisionsFile = Path.Combine(root, "decisions.json");
        await File.WriteAllTextAsync(
            decisionsFile,
            JsonSerializer.Serialize(new
            {
                ontologyId = OntologyId,
                decisions = new[]
                {
                    Decision(fixture.Relation, "accepted"),
                    Decision(fixture.Rule, "accepted"),
                    Decision(fixture.Lifecycle, "accepted"),
                    Decision(fixture.Rejected, "rejected"),
                },
            }));
        var request = BusinessOntologySemanticCli.ParseReviewApply(
            LlmWikiCliOptions.Parse(
            [
                "--ontology-id", OntologyId,
                "--decisions", decisionsFile,
            ]));
        var summary = await new BusinessOntologySemanticCli(om, store)
            .ApplyReviewAsync(request);
        assert(
            summary.GenerationId == "semantic-active"
            && summary.Decisions == 4
            && summary.AppendedReviews == 4
            && summary.ReplayedReviews == 0
            && summary.Relations == 1
            && summary.Rules == 1
            && summary.Lifecycles == 1
            && summary.States == 2
            && summary.Transitions == 1
            && summary.StaleReviews == 1,
            "review apply should append decisions and return a machine-readable promotion summary");

        var snapshot = await store.ReadExportableAsync(OntologyId);
        assert(
            snapshot.Candidates.Single(item => item.Id == fixture.Relation.Id).Status == "accepted"
            && snapshot.Candidates.Single(item => item.Id == fixture.Rule.Id).Status == "accepted"
            && snapshot.Candidates.Single(item => item.Id == fixture.Lifecycle.Id).Status == "accepted"
            && snapshot.Candidates.Single(item => item.Id == fixture.Rejected.Id).Status == "rejected"
            && snapshot.Diagnostics.Any(item =>
                item.Kind == "stale_review"
                && item.ConflictKey == fixture.Stale.Id),
            "promotion should retain rejected and stale decisions as auditable non-exportable state");

        var output = Path.Combine(root, "ontology");
        var exported = await new BusinessOntologyXmlExporter(store).ExportAsync(
            new BusinessOntologyXmlExportRequest(OntologyId, output));
        assert(
            exported.Relations == 1
            && exported.Rules == 1
            && exported.Lifecycles == 1,
            "promotion fixture should export one accepted relation, rule, and lifecycle");
        var relationXml = XDocument.Load(
            Path.Combine(output, "relations", "generated.xml")).ToString();
        var ruleXml = XDocument.Load(
            Path.Combine(output, "rules", "generated.xml")).ToString();
        var lifecycleXml = XDocument.Load(
            Path.Combine(output, "lifecycles", "generated.xml")).ToString();
        var semanticXml = relationXml + ruleXml + lifecycleXml;
        assert(
            semanticXml.Contains("记录关联供应商。", StringComparison.Ordinal)
            && semanticXml.Contains("记录编码不能为空。", StringComparison.Ordinal)
            && semanticXml.Contains("记录审批生命周期。", StringComparison.Ordinal)
            && !semanticXml.Contains(fixture.Rejected.SemanticId, StringComparison.Ordinal)
            && !semanticXml.Contains(fixture.Stale.SemanticId, StringComparison.Ordinal),
            "accepted Chinese semantic declarations should export while rejected and stale identities remain absent");
        assert(
            lifecycleXml.Contains("initial=\"draft\"", StringComparison.Ordinal)
            && lifecycleXml.Contains("id=\"approved\"", StringComparison.Ordinal)
            && lifecycleXml.Contains("from=\"draft\"", StringComparison.Ordinal)
            && lifecycleXml.Contains("to=\"approved\"", StringComparison.Ordinal),
            "source enum state tokens should project to lowerCamelCase ontology XML DSL state ids");
        var validation = await RunStrictValidatorAsync(
            Path.Combine(output, "ontology.xml"));
        assert(
            validation.ExitCode == 0,
            "accepted relation/rule/lifecycle bundle must pass the external ontology XML DSL "
            + $"strict validator (exit {validation.ExitCode}).\n"
            + $"stdout:\n{validation.Stdout}\n"
            + $"stderr:\n{validation.Stderr}");
        var audit = await File.ReadAllTextAsync(
            Path.Combine(output, "generation", "candidates.json"));
        assert(
            audit.Contains(fixture.Rejected.Id, StringComparison.Ordinal)
            && audit.Contains(fixture.Stale.Id, StringComparison.Ordinal)
            && audit.Contains("stale_review", StringComparison.Ordinal),
            "rejected and stale review data should remain in the generation audit sidecar");

        var replay = await new BusinessOntologySemanticCli(om, store)
            .ApplyReviewAsync(request);
        assert(
            replay.AppendedReviews == 0
            && replay.ReplayedReviews == 4
            && replay.Relations == 1
            && replay.Rules == 1
            && replay.Lifecycles == 1,
            "replaying the same review file should be idempotent and keep materialization stable");
    }

    private static void AssertStrictValidatorPrerequisites()
    {
        if (!Directory.Exists(OntologyDslWorkspaceRoot))
        {
            throw new InvalidOperationException(
                "Ontology XML DSL strict-validation prerequisite is missing: workspace root "
                + $"does not exist at '{OntologyDslWorkspaceRoot}'.");
        }
        if (!File.Exists(BunPath))
        {
            throw new InvalidOperationException(
                "Ontology XML DSL strict-validation prerequisite is missing: Bun executable "
                + $"does not exist at '{BunPath}'.");
        }
        if (!File.Exists(OntologyDslValidatorPath))
        {
            throw new InvalidOperationException(
                "Ontology XML DSL strict-validation prerequisite is missing: validator script "
                + $"does not exist at '{OntologyDslValidatorPath}'.");
        }
    }

    private static string FindOntologyDslWorkspaceRoot()
    {
        var configured = Environment.GetEnvironmentVariable("DEPA_WIKI_ONTOLOGY_DSL_WORKSPACE");
        var starts = string.IsNullOrWhiteSpace(configured)
            ? new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
            : new[] { configured };
        foreach (var start in starts)
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "skills", "ontology-xml-dsl", "scripts", "validate-ontology-xml.ts")))
                {
                    return directory.FullName;
                }
            }
        }
        throw new InvalidOperationException(
            "Could not locate the ontology XML DSL workspace. Set DEPA_WIKI_ONTOLOGY_DSL_WORKSPACE when running this test outside the repository.");
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
            ?? throw new InvalidOperationException("Bun is required for canonical ontology XML validation; set BUN_INSTALL or PATH.");
    }

    private static async Task<StrictValidationResult> RunStrictValidatorAsync(
        string ontologyPath)
    {
        var startInfo = new ProcessStartInfo(BunPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = OntologyDslWorkspaceRoot,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(OntologyDslValidatorPath);
        startInfo.ArgumentList.Add(ontologyPath);
        startInfo.ArgumentList.Add("--workspace-root");
        startInfo.ArgumentList.Add(OntologyDslWorkspaceRoot);
        startInfo.ArgumentList.Add("--generated");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Failed to start ontology XML DSL strict validator with Bun at '{BunPath}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                "Ontology XML DSL strict validator did not finish within 60 seconds.");
        }
        return new StrictValidationResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static object Decision(
        ValidatedOntologySemanticCandidate candidate,
        string decision) =>
        new
        {
            candidateId = candidate.Id,
            decision,
            reviewer = "user:fixture",
            rationale = decision == "accepted"
                ? "fixture 中的直接证据确认该业务语义。"
                : "fixture 明确拒绝该业务语义。",
            expectedEvidenceIds = candidate.EvidenceIds,
        };

    private static void AssertRejected(
        Action action,
        string expectedMessage,
        Action<bool, string> assert,
        string message)
    {
        var rejected = false;
        try
        {
            action();
        }
        catch (ArgumentException ex)
        {
            rejected = ex.Message.Contains(expectedMessage, StringComparison.Ordinal);
        }
        assert(rejected, message);
    }

    private sealed record StrictValidationResult(
        int ExitCode,
        string Stdout,
        string Stderr);

    private sealed record Fixture(
        BusinessOntologyGenerationInput Generation,
        ValidatedOntologySemanticCandidate Relation,
        ValidatedOntologySemanticCandidate Rule,
        ValidatedOntologySemanticCandidate Lifecycle,
        ValidatedOntologySemanticCandidate Rejected,
        ValidatedOntologySemanticCandidate Stale)
    {
        public static Fixture Create()
        {
            var validator = new OntologySemanticCandidateValidator();
            var concepts = new[] { Record, Supplier };
            var evidenceIds = new[]
            {
                "evidence:record",
                "evidence:supplier",
                "evidence:rule",
                "evidence:states",
                "evidence:transition",
                "evidence:rejected",
                "evidence:stale",
            };
            var relation = Validate(
                validator,
                concepts,
                evidenceIds,
                $$$"""
                {
                  "schemaVersion":"onto-semantic-v1",
                  "kind":"relation",
                  "semantic":{
                    "id":"{{{OntologyId}}}.Relation.RecordSupplier",
                    "fromConceptId":"{{{Record}}}",
                    "toConceptId":"{{{Supplier}}}",
                    "name":"supplier",
                    "min":"1",
                    "max":"1",
                    "descriptionZh":"记录关联供应商。"
                  },
                  "evidenceIds":["evidence:record","evidence:supplier"],
                  "basis":"deterministic",
                  "rationale":"字段类型和非空约束共同支持。"
                }
                """);
            var rule = Validate(
                validator,
                concepts,
                evidenceIds,
                $$$"""
                {
                  "schemaVersion":"onto-semantic-v1",
                  "kind":"rule",
                  "semantic":{
                    "id":"{{{OntologyId}}}.Rule.RecordCodeRequired",
                    "subjectConceptId":"{{{Record}}}",
                    "ruleKind":"required",
                    "descriptionZh":"记录编码不能为空。",
                    "predicate":{"property":"recordCode","operator":"present"},
                    "effect":{"type":"reject","messageZh":"记录编码不能为空。"}
                  },
                  "evidenceIds":["evidence:rule"],
                  "basis":"deterministic",
                  "rationale":"直接验证约束。"
                }
                """);
            var lifecycle = Validate(
                validator,
                concepts,
                evidenceIds,
                $$$"""
                {
                  "schemaVersion":"onto-semantic-v1",
                  "kind":"lifecycle",
                  "semantic":{
                    "id":"{{{OntologyId}}}.Lifecycle.RecordApproval",
                    "subjectConceptId":"{{{Record}}}",
                    "stateProperty":"status",
                    "initialState":"DRAFT",
                    "descriptionZh":"记录审批生命周期。",
                    "states":[
                      {"id":"DRAFT","terminal":false,"descriptionZh":"草稿"},
                      {"id":"APPROVED","terminal":true,"descriptionZh":"已批准"}
                    ],
                    "transitions":[{
                      "id":"{{{OntologyId}}}.Transition.ApproveRecord",
                      "action":"approve",
                      "fromState":"DRAFT",
                      "toState":"APPROVED",
                      "descriptionZh":"批准记录。",
                      "guard":{},
                      "effect":{"set":{"property":"status","value":"APPROVED"}}
                    }]
                  },
                  "evidenceIds":["evidence:states","evidence:transition"],
                  "basis":"deterministic",
                  "rationale":"有限状态和直接赋值共同支持。"
                }
                """);
            var rejected = Validate(
                validator,
                concepts,
                evidenceIds,
                $$$"""
                {
                  "schemaVersion":"onto-semantic-v1",
                  "kind":"relation",
                  "semantic":{
                    "id":"{{{OntologyId}}}.Relation.RejectedOwner",
                    "fromConceptId":"{{{Record}}}",
                    "toConceptId":"{{{Supplier}}}",
                    "name":"owner",
                    "min":"0",
                    "max":"1",
                    "descriptionZh":"审核拒绝的关系。"
                  },
                  "evidenceIds":["evidence:rejected"],
                  "basis":"deterministic",
                  "rationale":"用于拒绝路径测试。"
                }
                """);
            var stale = Validate(
                validator,
                concepts,
                evidenceIds,
                $$$"""
                {
                  "schemaVersion":"onto-semantic-v1",
                  "kind":"relation",
                  "semantic":{
                    "id":"{{{OntologyId}}}.Relation.StaleOwner",
                    "fromConceptId":"{{{Record}}}",
                    "toConceptId":"{{{Supplier}}}",
                    "name":"staleOwner",
                    "min":"0",
                    "max":"1",
                    "descriptionZh":"已经失效的关系。"
                  },
                  "evidenceIds":["evidence:stale"],
                  "basis":"deterministic",
                  "rationale":"用于失效路径测试。"
                }
                """);

            var evidence = evidenceIds.Select((id, index) =>
                new BusinessOntologyEvidence(
                    id,
                    "fixture",
                    $"src/Fixture{index}.java",
                    $"fixture:Fixture{index}",
                    1,
                    2,
                    "contractual",
                    "treesitter",
                    0.9,
                    "code-semantic-claim",
                    "fixture 直接源码证据。"))
                .ToArray();
            var candidates = new[]
            {
                Stored(relation),
                Stored(rule),
                Stored(lifecycle),
                Stored(rejected),
                Stored(stale),
            };
            var generation = new BusinessOntologyGenerationInput(
                OntologyId,
                "semantic-initial",
                "fixture-fingerprint",
                "onto-semantic-projector/1",
                "2026-07-18T00:00:00.0000000+00:00",
                [
                    new BusinessOntologyConcept(
                        Record,
                        "businessObject",
                        "记录",
                        "受管理的记录。",
                        "accepted",
                        1,
                        ["evidence:record"]),
                    new BusinessOntologyConcept(
                        Supplier,
                        "businessObject",
                        "供应商",
                        "记录供应商。",
                        "accepted",
                        1,
                        ["evidence:supplier"]),
                ],
                [
                    new BusinessOntologyAttribute(
                        Record,
                        "status",
                        "String",
                        true,
                        "记录审批状态。",
                        "accepted",
                        0.9,
                        ["evidence:states"]),
                ],
                [],
                [],
                [],
                [],
                [],
                [],
                evidence,
                candidates,
                [],
                []);
            return new Fixture(
                generation,
                relation,
                rule,
                lifecycle,
                rejected,
                stale);
        }

        private static ValidatedOntologySemanticCandidate Validate(
            OntologySemanticCandidateValidator validator,
            IReadOnlyCollection<string> concepts,
            IReadOnlyCollection<string> evidenceIds,
            string json) =>
            validator.Validate(json, concepts, evidenceIds);

        private static BusinessOntologyCandidate Stored(
            ValidatedOntologySemanticCandidate candidate) =>
            new(
                candidate.Id,
                candidate.Kind,
                candidate.SemanticId,
                candidate.CanonicalPayloadJson,
                candidate.Rationale,
                0.9,
                "pending",
                candidate.EvidenceIds);
    }
}
