import { afterAll, describe, expect, test } from "bun:test";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";

const auditScript = resolve(import.meta.dir, "../scripts/audit-generated-ontology.ts");
const temporaryRoots: string[] = [];

afterAll(() => {
  for (const root of temporaryRoots) rmSync(root, { recursive: true, force: true });
});

function write(root: string, name: string, content: string): void {
  writeFileSync(join(root, name), content);
}

function createBundle(): string {
  const root = mkdtempSync(join(tmpdir(), "generated-ontology-audit-"));
  temporaryRoots.push(root);
  write(
    root,
    "ontology.xml",
    `<?xml version="1.0" encoding="UTF-8"?>
<Ontology id="Example.Ontology" version="v1">
  <Modules>
    <TypeModule href="vfs://./types.xml" />
    <EvidenceModule href="vfs://./evidence.xml" />
  </Modules>
</Ontology>
`,
  );
  write(
    root,
    "types.xml",
    `<?xml version="1.0" encoding="UTF-8"?>
<TypeModule id="Example.Types">
  <Types>
    <Type id="Example.Record" status="hypothesis">
      <Description>Candidate record identity.</Description>
      <EvidenceRefs><EvidenceRef ref="code:record" /></EvidenceRefs>
    </Type>
  </Types>
</TypeModule>
`,
  );
  write(
    root,
    "evidence.xml",
    `<?xml version="1.0" encoding="UTF-8"?>
<EvidenceModule id="Example.Evidence">
  <EvidenceItems>
    <Evidence id="code:record" repository="backend" revision="revision-1" path="src/Record.java" symbol="example.Record" startLine="1" grade="inferred" resolver="test" confidence="1" />
  </EvidenceItems>
</EvidenceModule>
`,
  );
  return root;
}

function runAudit(root: string, extraArguments: string[] = []) {
  const reportPath = join(root, "audit-report.json");
  const result = Bun.spawnSync({
    cmd: [
      process.execPath,
      "run",
      auditScript,
      "--ontology",
      join(root, "ontology.xml"),
      "--workspace-root",
      root,
      "--report",
      reportPath,
      ...extraArguments,
    ],
    cwd: root,
    stdout: "pipe",
    stderr: "pipe",
  });
  return {
    exitCode: result.exitCode,
    reportPath,
    report: existsSync(reportPath) ? JSON.parse(readFileSync(reportPath, "utf8")) : undefined,
  };
}

describe("audit-generated-ontology", () => {
  test("passes strict generated validation and writes a stable report", () => {
    const root = createBundle();

    const first = runAudit(root);
    const second = runAudit(root);

    expect(first.exitCode).toBe(0);
    expect(first.report).toEqual(second.report);
    expect(first.report).toEqual({
      schemaVersion: 1,
      status: "PASS",
      stages: {
        ontologyXml: { status: "PASS", diagnostics: [] },
        coverage: { status: "NOT_REQUESTED", diagnostics: [] },
      },
    });
  });

  test("returns nonzero when strict generated XML validation fails", () => {
    const root = createBundle();
    write(
      root,
      "types.xml",
      readFileSync(join(root, "types.xml"), "utf8")
        .replace("<EvidenceRefs><EvidenceRef ref=\"code:record\" /></EvidenceRefs>", ""),
    );

    const result = runAudit(root);

    expect(result.exitCode).not.toBe(0);
    expect(result.report.status).toBe("FAIL");
    expect(result.report.stages.ontologyXml.status).toBe("FAIL");
    expect(result.report.stages.ontologyXml.diagnostics.join("\n")).toContain(
      "requires direct EvidenceRefs in --generated mode",
    );
  });

  test("returns nonzero when optional coverage validation fails", () => {
    const root = createBundle();
    write(
      root,
      "inventory.json",
      `${JSON.stringify({
        schemaVersion: 1,
        repositories: [{
          key: "backend",
          revision: "revision-1",
          language: "java",
          include: ["**/*.java"],
          exclude: [],
          stats: { filesScanned: 1, filesExcluded: 0, filesFailed: 0, filesUnresolved: 0 },
        }],
        observations: [{
          observationKey: "backend|src/Record.java|1|java-type|example.Record",
          repository: "backend",
          revision: "revision-1",
          language: "java",
          kind: "java-type",
          symbol: "example.Record",
          path: "src/Record.java",
          line: 1,
          resolver: "test",
          confidence: 1,
        }],
      }, null, 2)}\n`,
    );
    write(
      root,
      "manifest.json",
      `${JSON.stringify({
        schemaVersion: 1,
        repositories: [{ key: "backend", revision: "revision-1" }],
        requiredDomains: [{
          domain: "records",
          status: "missing",
          typeRefs: ["Example.Record"],
          mappingRefs: [],
          evidenceRefs: ["code:record"],
        }],
        candidates: [],
        candidateTotals: { mapped: 0, rejected: 0, unresolved: 0 },
        signalDispositions: { rules: [], lifecycles: [] },
      }, null, 2)}\n`,
    );

    const result = runAudit(root, [
      "--inventory",
      join(root, "inventory.json"),
      "--manifest",
      join(root, "manifest.json"),
    ]);

    expect(result.exitCode).not.toBe(0);
    expect(result.report.stages.ontologyXml.status).toBe("PASS");
    expect(result.report.stages.coverage.status).toBe("FAIL");
    expect(result.report.stages.coverage.diagnostics.map(
      (diagnostic: { code: string }) => diagnostic.code,
    )).toContain("DOMAIN_MISSING");
  });
});
