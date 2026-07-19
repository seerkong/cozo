import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const generationDirectory = resolve(
  import.meta.dir,
  "../../../cozo-ontology/v0/ontology/sample-domain/generation",
);
const generator = resolve(generationDirectory, "build-coverage-manifest.ts");
const ontologyDirectory = resolve(generationDirectory, "..");

function readJson(name: string) {
  return JSON.parse(readFileSync(resolve(generationDirectory, name), "utf8"));
}

function runGenerator(): void {
  const result = Bun.spawnSync({
    cmd: [process.execPath, "run", generator],
    cwd: ontologyDirectory,
    stdout: "pipe",
    stderr: "pipe",
  });
  expect(result.exitCode).toBe(0);
}

function includesChinese(value: string): boolean {
  return /[\u3400-\u9fff]/u.test(value);
}

function machineProjection(coverageManifest: Record<string, unknown>) {
  const candidates = coverageManifest.candidates as Array<Record<string, unknown>>;
  const signals = coverageManifest.signalDispositions as Record<string, Array<Record<string, unknown>>>;
  return {
    repositories: coverageManifest.repositories,
    requiredDomains: coverageManifest.requiredDomains,
    acceptancePolicyRef: coverageManifest.acceptancePolicyRef,
    candidates: candidates.map((candidate) => ({
      candidateKey: candidate.candidateKey,
      domain: candidate.domain,
      family: candidate.family,
      proposedIdentity: candidate.proposedIdentity,
      status: candidate.status,
      disposition: candidate.disposition,
      observationKeys: candidate.observationKeys,
      ontologyRefs: candidate.ontologyRefs,
      evidenceRefs: candidate.evidenceRefs,
      evidenceGrades: candidate.evidenceGrades,
      strongestEvidenceGrade: candidate.strongestEvidenceGrade,
      sourcePaths: candidate.sourcePaths,
      tracedProcessPaths: candidate.tracedProcessPaths,
      conflictingEvidenceRefs: candidate.conflictingEvidenceRefs,
      acceptancePolicyRef: candidate.acceptancePolicyRef,
      reviewDecision: candidate.reviewDecision,
      attributeNames: candidate.attributeNames,
    })),
    signalDispositions: Object.fromEntries(
      Object.entries(signals).map(([family, dispositions]) => [
        family,
        dispositions.map((item) => ({
          observationKey: item.observationKey,
          disposition: item.disposition,
          ontologyRef: item.ontologyRef,
        })),
      ]),
    ),
  };
}

describe("localized ontology sidecars", () => {
  test("regenerates Chinese explanatory fields without changing machine projections", () => {
    runGenerator();
    const firstManifest = readJson("coverage-manifest.json");
    const firstProjection = machineProjection(firstManifest);

    runGenerator();
    const secondManifest = readJson("coverage-manifest.json");
    expect(machineProjection(secondManifest)).toEqual(firstProjection);

    const sourceManifest = readJson("source-manifest.json");
    const candidateRegister = readJson("candidate-register.json");
    const unresolvedCandidates = readJson("unresolved-candidates.json");
    const readPlan = readJson("read-plan.json");
    const semanticCoverage = readJson("semantic-coverage.json");

    const explanatoryFields = [
      sourceManifest.acceptancePolicy.scope,
      ...Object.values(sourceManifest.acceptancePolicy.gates),
      sourceManifest.acceptancePolicy.contradictionPolicy,
      sourceManifest.acceptancePolicy.hypothesisPolicy,
      sourceManifest.acceptancePolicy.reviewRecordPolicy,
      sourceManifest.policy.ontologySource,
      sourceManifest.policy.sourceReading,
      ...candidateRegister.candidates.flatMap((candidate: Record<string, unknown>) => [
        candidate.acceptanceRationale,
        ...((candidate.requiredFollowUpReads as string[]) ?? []),
      ]),
      ...unresolvedCandidates.signalDispositions.rules.map((item: Record<string, unknown>) => item.reason),
      ...unresolvedCandidates.signalDispositions.lifecycles.map((item: Record<string, unknown>) => item.reason),
      readPlan.readingMethod,
      readPlan.scopeLimitation,
      ...readPlan.contexts.flatMap((context: Record<string, unknown>) => context.limitations),
      ...semanticCoverage.contexts.flatMap((context: Record<string, unknown>) => context.limitations),
    ] as string[];

    expect(explanatoryFields).not.toHaveLength(0);
    expect(explanatoryFields.every(includesChinese)).toBe(true);

    const recordCandidate = candidateRegister.candidates.find(
      (candidate: Record<string, unknown>) => candidate.proposedIdentity === "ExampleCatalog.Record",
    );
    const recordDescription = readFileSync(resolve(ontologyDirectory, "types/records-storage.xml"), "utf8")
      .match(/<Type id="ExampleCatalog\.Record"[\s\S]*?<Description>([^<]+)<\/Description>/)?.[1];
    expect(recordCandidate.meaning).toBe(recordDescription);
  });
});
