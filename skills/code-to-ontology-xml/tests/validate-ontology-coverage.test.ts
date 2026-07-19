import { afterAll, describe, expect, test } from "bun:test";
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";
import { validateOntologyCoverage } from "../scripts/validate-ontology-coverage.ts";

const fixtureRoot = resolve(import.meta.dir, "fixtures/ontology-coverage");
const temporaryRoots: string[] = [];

afterAll(() => {
  for (const root of temporaryRoots) rmSync(root, { recursive: true, force: true });
});

function copyFixture(name: string): string {
  const destination = mkdtempSync(join(tmpdir(), `ontology-coverage-${name}-`));
  temporaryRoots.push(destination);
  cpSync(join(fixtureRoot, name), destination, { recursive: true });
  return destination;
}

function validateFixture(root: string) {
  return validateOntologyCoverage({
    ontologyPath: join(root, "ontology.xml"),
    inventoryPath: join(root, "inventory.json"),
    manifestPath: join(root, "manifest.json"),
  });
}

describe("validate-ontology-coverage", () => {
  test("rejects a thin skeleton with a missing required domain", () => {
    const result = validateFixture(join(fixtureRoot, "thin"));

    expect(result.ok).toBe(false);
    expect(result.diagnostics.map((diagnostic) => diagnostic.code)).toContain("DOMAIN_MISSING");
    expect(result.report.status).toBe("FAIL");
  });

  test("accepts a fully accounted fixture and produces a deterministic report", () => {
    const first = validateFixture(join(fixtureRoot, "full"));
    const second = validateFixture(join(fixtureRoot, "full"));

    expect(first.ok).toBe(true);
    expect(first.diagnostics).toEqual([]);
    expect(first.report).toEqual(second.report);
    expect(first.report.ontology).toEqual({
      evidence: 3,
      mappings: 1,
      relations: 1,
      rules: 1,
      stateMachines: 1,
      transitions: 1,
      types: 1,
    });
    expect(first.report.candidates).toEqual({
      mapped: 5,
      rejected: 1,
      total: 6,
      unresolved: 0,
    });
    expect(first.report.evidenceAnchors).toEqual({
      total: 3,
      valid: 3,
      unchecked: 0,
      invalid: 0,
    });
  });

  test("rejects a canonical semantic object without a mapped candidate", () => {
    const root = copyFixture("full");
    const manifestPath = join(root, "manifest.json");
    const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
    manifest.candidates = manifest.candidates.filter(
      (candidate: { candidateKey: string }) => candidate.candidateKey !== "record-mapping",
    );
    manifest.candidateTotals.mapped -= 1;
    writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);

    const result = validateFixture(root);
    expect(result.ok).toBe(false);
    expect(result.diagnostics).toContainEqual({
      code: "CANONICAL_OBJECT_UNCOVERED",
      location: "mapping:ExampleCatalog.Implementation.Record",
      message: "Canonical ImplementationMapping is not covered by any mapped candidate ontologyRefs.",
    });
  });

  test("rejects missing domains and unknown manifest references", () => {
    const root = copyFixture("full");
    const manifestPath = join(root, "manifest.json");
    const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
    manifest.requiredDomains[0].status = "missing";
    manifest.requiredDomains[0].typeRefs.push("ExampleCatalog.Unknown");
    writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);

    const result = validateFixture(root);
    const codes = result.diagnostics.map((diagnostic) => diagnostic.code);
    expect(codes).toContain("DOMAIN_MISSING");
    expect(codes).toContain("UNKNOWN_TYPE_REF");
  });

  test("rejects DEPA semantics anywhere in the ontology bundle", () => {
    const root = copyFixture("full");
    const evidencePath = join(root, "evidence/backend.xml");
    writeFileSync(
      evidencePath,
      readFileSync(evidencePath, "utf8").replace("Record repository mapping.", "depa_architecture mapping."),
    );

    const result = validateFixture(root);
    expect(result.ok).toBe(false);
    expect(result.diagnostics.map((diagnostic) => diagnostic.code)).toContain("DEPA_SEMANTICS");
  });
});
