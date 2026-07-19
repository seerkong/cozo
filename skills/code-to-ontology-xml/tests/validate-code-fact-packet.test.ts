import { afterAll, describe, expect, test } from "bun:test";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { validateCodeFactPacketFile } from "../scripts/validate-code-fact-packet.ts";

const temporaryRoots: string[] = [];

afterAll(() => {
  for (const root of temporaryRoots) rmSync(root, { recursive: true, force: true });
});

function validPacket() {
  return {
    schemaVersion: 1,
    repositories: [
      {
        key: "backend",
        revision: "revision-1",
        language: "java",
        include: ["**/*.java"],
        exclude: [],
        stats: {
          filesScanned: 1,
          filesExcluded: 0,
          filesFailed: 0,
          filesUnresolved: 0,
        },
      },
    ],
    observations: [
      {
        observationKey: "backend|src/Record.java|7|java-type|example.Record",
        repository: "backend",
        revision: "revision-1",
        language: "java",
        kind: "java-type",
        symbol: "example.Record",
        path: "src/Record.java",
        line: 7,
        resolver: "treesitter",
        confidence: 0.95,
      },
    ],
    summary: {
      filesScanned: 1,
      filesExcluded: 0,
      filesFailed: 0,
      filesUnresolved: 0,
      observations: 1,
      byKind: { "java-type": 1 },
      signals: { rules: 0, lifecycles: 0 },
    },
  };
}

function writePacket(packet: unknown): string {
  const root = mkdtempSync(join(tmpdir(), "code-fact-packet-"));
  temporaryRoots.push(root);
  const path = join(root, "packet.json");
  writeFileSync(path, `${JSON.stringify(packet, null, 2)}\n`);
  return path;
}

describe("validate-code-fact-packet", () => {
  test("accepts the existing domain inventory shape with a deterministic report", () => {
    const packetPath = writePacket(validPacket());

    const first = validateCodeFactPacketFile(packetPath);
    const second = validateCodeFactPacketFile(packetPath);

    expect(first.ok).toBe(true);
    expect(first.report).toEqual(second.report);
    expect(first.report).toEqual({
      schemaVersion: 1,
      status: "PASS",
      repositories: 1,
      observations: 1,
      diagnostics: [],
    });
  });

  test("rejects missing revisions and non-relative paths", () => {
    const packet = validPacket();
    packet.repositories[0]!.revision = "";
    packet.observations[0]!.revision = "";
    packet.observations[0]!.path = "../outside/Record.java";

    const result = validateCodeFactPacketFile(writePacket(packet));
    const codes = result.diagnostics.map((diagnostic) => diagnostic.code);

    expect(result.ok).toBe(false);
    expect(codes).toContain("REPOSITORY_REVISION_INVALID");
    expect(codes).toContain("OBSERVATION_REVISION_INVALID");
    expect(codes).toContain("OBSERVATION_PATH_INVALID");
  });

  test("rejects depa_* content", () => {
    const packet = validPacket();
    packet.observations[0]!.symbol = "depa_architecture.Record";

    const result = validateCodeFactPacketFile(writePacket(packet));

    expect(result.ok).toBe(false);
    expect(result.diagnostics.map((diagnostic) => diagnostic.code)).toContain("DEPA_SEMANTICS");
  });

  test("rejects unstable or duplicate observation keys", () => {
    const packet = validPacket();
    packet.observations.push({ ...packet.observations[0]! });
    packet.observations.push({
      ...packet.observations[0]!,
      observationKey: "not-derived-from-anchor",
    });

    const result = validateCodeFactPacketFile(writePacket(packet));
    const codes = result.diagnostics.map((diagnostic) => diagnostic.code);

    expect(result.ok).toBe(false);
    expect(codes).toContain("OBSERVATION_KEY_DUPLICATE");
    expect(codes).toContain("OBSERVATION_KEY_UNSTABLE");
  });
});
