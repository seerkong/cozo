using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Tools;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.Depa;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class DepaOntologyExporterTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"depa-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var db = new CozoDb("mem", "");
            var om = new CozoOm(db);
            await om.InitDepaOntologyAsync();
            await om.UpsertEntityAsync("depa:impl:record-service", "depa_impl", "RecordService");
            await om.SetPropertyAsync("depa:impl:record-service", "assigned_by", "config");
            await om.SetPropertyAsync("depa:impl:record-service", "confidence", 1.0);
            await om.SetPropertyAsync("depa:impl:record-service", "path", "src/main/java/example/RecordService.java");
            await om.SetPropertyAsync("depa:impl:record-service", "line", 12);
            await om.SetPropertyAsync("depa:impl:record-service", "sym_key", "java:example.RecordService");

            var exporter = new DepaOntologyExporter();
            var requestBase = new DepaExportRequest("runtime-snapshot", Path.Combine(root, "snapshot"), "is-record-new", root, "abc123");
            var snapshot = await exporter.ExportAsync(om, requestBase);
            assert(snapshot.Files.Select(Path.GetFileName).Contains("depa-runtime-snapshot.xml"),
                "runtime-snapshot export should emit depa-runtime-snapshot.xml");
            var snapshotXml = await File.ReadAllTextAsync(Path.Combine(root, "snapshot", "depa-runtime-snapshot.xml"));
            assert(snapshotXml.Contains("depa:impl:record-service", StringComparison.Ordinal)
                    && snapshotXml.Contains("RecordService", StringComparison.Ordinal),
                "runtime-snapshot export should preserve DEPA entity identity and label");

            var ontology = await exporter.ExportAsync(om, requestBase with { Format = "ontology-xml", OutputDirectory = Path.Combine(root, "ontology") });
            assert(File.Exists(Path.Combine(root, "ontology", "ontology.xml"))
                    && File.Exists(Path.Combine(root, "ontology", "types", "depa.xml"))
                    && File.Exists(Path.Combine(root, "ontology", "judgments", "depa-judgments.xml")),
                "ontology-xml export should emit root, type module, and DEPA judgment sidecar");
            var evidenceXml = await File.ReadAllTextAsync(Path.Combine(root, "ontology", "evidence", "depa.xml"));
            assert(evidenceXml.Contains("src/main/java/example/RecordService.java", StringComparison.Ordinal)
                    && !evidenceXml.Contains(root, StringComparison.Ordinal),
                "normalized ontology evidence must preserve a repository-relative path and omit the local root");
            assert(ontology.EvidenceCount == 1, "ontology export should materialize one evidence anchor for the DEPA entity");

            var wiki = await exporter.ExportAsync(om, requestBase with { Format = "wiki", OutputDirectory = Path.Combine(root, "wiki") });
            assert(File.Exists(Path.Combine(root, "wiki", "INDEX.md"))
                    && File.Exists(Path.Combine(root, "wiki", "03-uncertainty", "blocked.md"))
                    && File.Exists(Path.Combine(root, "wiki", "_index", "manifest.json")),
                "wiki export should emit the navigable wiki tree and manifest");
            assert(wiki.Diagnostics.Any(message => message.Contains("No fresh DEPA conformance report", StringComparison.Ordinal)),
                "read-only export without a scan report should disclose that PASS/BLOCKED are unavailable");

            var rejected = false;
            try
            {
                await exporter.ExportAsync(om, requestBase with { OutputDirectory = Path.Combine(root, "snapshot") });
            }
            catch (IOException)
            {
                rejected = true;
            }
            assert(rejected, "export should reject a non-empty output directory instead of overwriting generated artifacts");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }
}
