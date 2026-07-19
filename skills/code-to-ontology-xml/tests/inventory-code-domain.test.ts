import { afterAll, describe, expect, test } from "bun:test";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { isAbsolute, join, resolve } from "node:path";
import { tmpdir } from "node:os";
import {
  createDomainInventory,
  serializeInventory,
} from "../scripts/inventory-code-domain.ts";

const fixtureRoot = resolve(import.meta.dir, "fixtures/inventory");
const temporaryRoots: string[] = [];

afterAll(() => {
  for (const root of temporaryRoots) rmSync(root, { recursive: true, force: true });
});

describe("inventory-code-domain", () => {
  test("produces byte-identical inventories with repository-relative anchors", async () => {
    const options = {
      repositories: [
        {
          key: "backend",
          revision: "java-fixture",
          language: "java" as const,
          root: join(fixtureRoot, "java-repo"),
          include: ["**/*.java", "**/*Mapper.xml"],
          exclude: [],
        },
        {
          key: "frontend",
          revision: "typescript-fixture",
          language: "typescript" as const,
          root: join(fixtureRoot, "typescript-repo"),
          include: ["**/*.ts", "**/*.tsx"],
          exclude: [],
        },
      ],
    };

    const first = serializeInventory(await createDomainInventory(options));
    const second = serializeInventory(await createDomainInventory(options));

    expect(second).toBe(first);
    expect(first).not.toContain(fixtureRoot);
    expect(first).not.toMatch(/generatedAt|createdAt|timestamp/);

    const inventory = JSON.parse(first);
    expect(inventory.observations.length).toBeGreaterThan(12);
    const observationKeys = inventory.observations.map(
      (observation: { observationKey: string }) => observation.observationKey,
    );
    expect(new Set(observationKeys).size).toBe(observationKeys.length);
    expect(
      inventory.observations.filter((observation: { kind: string; path: string; symbol: string }) =>
        observation.kind === "typescript-api-call-site" &&
        observation.path.endsWith("page.tsx") &&
        observation.symbol === "api.getRecord"
      ),
    ).toHaveLength(1);
    expect(
      inventory.observations.filter((observation: { kind: string; symbol: string }) =>
        observation.kind === "java-enum-value" &&
        observation.symbol === "example.RecordStatus#AVAILABLE"
      ),
    ).toHaveLength(1);
    expect(inventory.observations.every((observation: { path: string }) =>
      !isAbsolute(observation.path) && !observation.path.includes("\\") && !observation.path.includes(".."),
    )).toBe(true);
    expect(inventory.observations.map((observation: { kind: string }) => observation.kind)).toEqual(
      expect.arrayContaining([
        "java-type",
        "java-field",
        "java-enum-value",
        "spring-controller-route",
        "spring-api-operation",
        "service-state-write",
        "mybatis-mapper",
        "typescript-feature",
        "typescript-type",
        "typescript-api-method",
        "typescript-api-route",
        "typescript-api-call-site",
      ]),
    );
    expect(
      inventory.observations
        .filter((observation: { kind: string }) => observation.kind === "typescript-api-route")
        .map((observation: { details: { route: string } }) => observation.details.route),
    ).toEqual(["/api/records/${id}", "/api/records"]);
    const serviceWrites = inventory.observations.filter((observation: {
      kind: string;
      path: string;
    }) => observation.kind === "service-state-write" && observation.path.endsWith("RecordService.java"));
    expect(serviceWrites.map((observation: { details: { operation: string } }) =>
      observation.details.operation
    )).toEqual(["setRecordStatus"]);
    expect(
      inventory.observations
        .filter((observation: { kind: string; path: string }) =>
          observation.kind === "lifecycle-signal" && observation.path.endsWith("RecordService.java")
        )
        .map((observation: { details: { operation: string } }) => observation.details.operation),
    ).toEqual(["setRecordStatus"]);
    expect(inventory.summary.signals.lifecycles).toBe(
      inventory.observations.filter((observation: { kind: string }) => observation.kind === "lifecycle-signal").length,
    );
    const mapperLifecycleSymbols = inventory.observations
      .filter((observation: { kind: string; path: string }) =>
        observation.kind === "lifecycle-signal" && observation.path.endsWith("RecordMapper.xml")
      )
      .map((observation: { symbol: string }) => observation.symbol);
    expect(mapperLifecycleSymbols).toEqual([
      "example.RecordMapper#insert",
      "example.RecordMapper#updateStatus",
    ]);
    expect(
      inventory.observations.some((observation: { kind: string; symbol: string }) =>
        observation.kind === "mybatis-write" && observation.symbol === "example.RecordMapper#updateName"
      ),
    ).toBe(true);
    expect(mapperLifecycleSymbols).not.toContain("example.RecordMapper#updateName");

    const outputRoot = mkdtempSync(join(tmpdir(), "ontology-inventory-"));
    temporaryRoots.push(outputRoot);
    const output = join(outputRoot, "inventory.json");
    writeFileSync(output, first);
    expect(readFileSync(output, "utf8")).toBe(second);
  });
});
