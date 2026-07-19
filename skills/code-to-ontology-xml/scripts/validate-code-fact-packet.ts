#!/usr/bin/env bun

import { existsSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { isAbsolute, resolve } from "node:path";
import { fileURLToPath } from "node:url";

type JsonObject = Record<string, unknown>;

export type CodeFactDiagnostic = {
  code: string;
  location: string;
  message: string;
};

export type CodeFactPacketReport = {
  schemaVersion: 1;
  status: "PASS" | "FAIL";
  repositories: number;
  observations: number;
  diagnostics: CodeFactDiagnostic[];
};

export type CodeFactPacketValidationResult = {
  ok: boolean;
  diagnostics: CodeFactDiagnostic[];
  report: CodeFactPacketReport;
};

const repositoryKeyPattern = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;
const depaPattern = /depa_/i;

function isObject(value: unknown): value is JsonObject {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function nonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function add(
  diagnostics: CodeFactDiagnostic[],
  code: string,
  location: string,
  message: string,
): void {
  diagnostics.push({ code, location, message });
}

function sortDiagnostics(diagnostics: CodeFactDiagnostic[]): CodeFactDiagnostic[] {
  return diagnostics.sort((left, right) =>
    `${left.code}\0${left.location}\0${left.message}`.localeCompare(
      `${right.code}\0${right.location}\0${right.message}`,
    )
  );
}

function portableRelativePath(value: unknown): value is string {
  if (!nonEmptyString(value) || isAbsolute(value) || value.includes("\\")) return false;
  const segments = value.split("/");
  return !segments.includes("") && !segments.includes(".") && !segments.includes("..");
}

function stableObservationKey(observation: JsonObject): string {
  return [
    observation.repository,
    observation.path,
    observation.line,
    observation.kind,
    observation.symbol,
  ].join("|");
}

function report(
  diagnostics: CodeFactDiagnostic[],
  repositoryCount: number,
  observationCount: number,
): CodeFactPacketValidationResult {
  const sorted = sortDiagnostics(diagnostics);
  const packetReport: CodeFactPacketReport = {
    schemaVersion: 1,
    status: sorted.length === 0 ? "PASS" : "FAIL",
    repositories: repositoryCount,
    observations: observationCount,
    diagnostics: sorted,
  };
  return { ok: sorted.length === 0, diagnostics: sorted, report: packetReport };
}

export function validateCodeFactPacket(packet: unknown): CodeFactPacketValidationResult {
  const diagnostics: CodeFactDiagnostic[] = [];
  if (!isObject(packet)) {
    add(diagnostics, "PACKET_SHAPE_INVALID", "packet", "Expected a JSON object.");
    return report(diagnostics, 0, 0);
  }

  if (packet.schemaVersion !== 1) {
    add(
      diagnostics,
      "SCHEMA_VERSION_UNSUPPORTED",
      "packet.schemaVersion",
      `Expected schemaVersion 1, found ${String(packet.schemaVersion)}.`,
    );
  }

  const repositories = Array.isArray(packet.repositories) ? packet.repositories : [];
  const observations = Array.isArray(packet.observations) ? packet.observations : [];
  if (!Array.isArray(packet.repositories)) {
    add(diagnostics, "REPOSITORIES_INVALID", "packet.repositories", "Expected an array.");
  }
  if (!Array.isArray(packet.observations)) {
    add(diagnostics, "OBSERVATIONS_INVALID", "packet.observations", "Expected an array.");
  }
  if (depaPattern.test(JSON.stringify(packet))) {
    add(
      diagnostics,
      "DEPA_SEMANTICS",
      "packet",
      "Code fact packets must not contain depa_* names or values.",
    );
  }

  const revisionsByRepository = new Map<string, string>();
  for (let index = 0; index < repositories.length; index += 1) {
    const repository = repositories[index];
    const location = `repositories[${index}]`;
    if (!isObject(repository)) {
      add(diagnostics, "REPOSITORY_INVALID", location, "Expected a repository object.");
      continue;
    }
    const key = repository.key;
    const revision = repository.revision;
    if (!nonEmptyString(key) || !repositoryKeyPattern.test(key)) {
      add(
        diagnostics,
        "REPOSITORY_KEY_INVALID",
        `${location}.key`,
        "Repository key must be a non-empty stable token.",
      );
    }
    if (!nonEmptyString(revision) || revision === "working-tree") {
      add(
        diagnostics,
        "REPOSITORY_REVISION_INVALID",
        `${location}.revision`,
        "Repository revision must be a non-empty immutable identity.",
      );
    }
    if (!nonEmptyString(key)) continue;
    if (revisionsByRepository.has(key)) {
      add(
        diagnostics,
        "REPOSITORY_KEY_DUPLICATE",
        `${location}.key`,
        `Repository key '${key}' is declared more than once.`,
      );
      continue;
    }
    if (nonEmptyString(revision)) revisionsByRepository.set(key, revision);
  }

  const observationKeys = new Set<string>();
  for (let index = 0; index < observations.length; index += 1) {
    const observation = observations[index];
    const location = `observations[${index}]`;
    if (!isObject(observation)) {
      add(diagnostics, "OBSERVATION_INVALID", location, "Expected an observation object.");
      continue;
    }

    const repository = observation.repository;
    const revision = observation.revision;
    const observationKey = observation.observationKey;
    if (!nonEmptyString(repository) || !revisionsByRepository.has(repository)) {
      add(
        diagnostics,
        "OBSERVATION_REPOSITORY_INVALID",
        `${location}.repository`,
        `Observation repository '${String(repository)}' is not declared.`,
      );
    }
    if (!nonEmptyString(revision)) {
      add(
        diagnostics,
        "OBSERVATION_REVISION_INVALID",
        `${location}.revision`,
        "Observation revision must be non-empty.",
      );
    } else if (nonEmptyString(repository) && revisionsByRepository.get(repository) !== revision) {
      add(
        diagnostics,
        "OBSERVATION_REVISION_MISMATCH",
        `${location}.revision`,
        `Observation revision '${revision}' does not match repository '${repository}'.`,
      );
    }
    if (!portableRelativePath(observation.path)) {
      add(
        diagnostics,
        "OBSERVATION_PATH_INVALID",
        `${location}.path`,
        "Observation path must be a forward-slash repository-relative path.",
      );
    }
    if (!Number.isInteger(observation.line) || Number(observation.line) <= 0) {
      add(
        diagnostics,
        "OBSERVATION_LINE_INVALID",
        `${location}.line`,
        "Observation line must be a positive integer.",
      );
    }
    for (const field of ["kind", "symbol", "resolver"] as const) {
      if (!nonEmptyString(observation[field])) {
        add(
          diagnostics,
          `OBSERVATION_${field.toUpperCase()}_INVALID`,
          `${location}.${field}`,
          `Observation ${field} must be non-empty.`,
        );
      }
    }
    if (
      typeof observation.confidence !== "number"
      || !Number.isFinite(observation.confidence)
      || observation.confidence < 0
      || observation.confidence > 1
    ) {
      add(
        diagnostics,
        "OBSERVATION_CONFIDENCE_INVALID",
        `${location}.confidence`,
        "Observation confidence must be a finite number in [0,1].",
      );
    }

    if (!nonEmptyString(observationKey)) {
      add(
        diagnostics,
        "OBSERVATION_KEY_INVALID",
        `${location}.observationKey`,
        "Observation key must be non-empty.",
      );
    } else {
      const expected = stableObservationKey(observation);
      if (observationKey !== expected) {
        add(
          diagnostics,
          "OBSERVATION_KEY_UNSTABLE",
          `${location}.observationKey`,
          `Expected stable key '${expected}'.`,
        );
      }
      if (observationKeys.has(observationKey)) {
        add(
          diagnostics,
          "OBSERVATION_KEY_DUPLICATE",
          `${location}.observationKey`,
          `Observation key '${observationKey}' is declared more than once.`,
        );
      }
      observationKeys.add(observationKey);
    }
  }

  return report(diagnostics, repositories.length, observations.length);
}

export function validateCodeFactPacketFile(path: string): CodeFactPacketValidationResult {
  const resolvedPath = resolve(path);
  if (!existsSync(resolvedPath) || !statSync(resolvedPath).isFile()) {
    return report([{
      code: "PACKET_NOT_FOUND",
      location: "packet",
      message: "Packet file does not exist or is not a regular file.",
    }], 0, 0);
  }
  try {
    return validateCodeFactPacket(JSON.parse(readFileSync(resolvedPath, "utf8")));
  } catch (error) {
    return report([{
      code: "PACKET_JSON_INVALID",
      location: "packet",
      message: error instanceof Error ? error.message : String(error),
    }], 0, 0);
  }
}

export function serializeCodeFactPacketReport(reportValue: CodeFactPacketReport): string {
  return `${JSON.stringify(reportValue, null, 2)}\n`;
}

function parseArguments(args: string[]): { packetPath: string; reportPath?: string } {
  let packetPath = "";
  let reportPath: string | undefined;
  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index]!;
    if (argument === "--report") {
      const value = args[index + 1];
      if (!value || value.startsWith("--")) throw new Error("--report requires a path.");
      reportPath = value;
      index += 1;
      continue;
    }
    if (argument.startsWith("--")) throw new Error(`Unknown option '${argument}'.`);
    if (packetPath) throw new Error(`Unexpected positional argument '${argument}'.`);
    packetPath = argument;
  }
  if (!packetPath) throw new Error("Missing code fact packet path.");
  return { packetPath, reportPath };
}

async function main(): Promise<void> {
  try {
    const options = parseArguments(process.argv.slice(2));
    const result = validateCodeFactPacketFile(options.packetPath);
    if (options.reportPath) {
      writeFileSync(resolve(options.reportPath), serializeCodeFactPacketReport(result.report));
    }
    if (!result.ok) {
      for (const diagnostic of result.diagnostics) {
        console.error(`${diagnostic.code} ${diagnostic.location}: ${diagnostic.message}`);
      }
      process.exitCode = 1;
      return;
    }
    console.log(
      `Code fact packet PASS: ${result.report.repositories} repositories, ` +
      `${result.report.observations} observations.`,
    );
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    console.error("Usage: validate-code-fact-packet.ts <packet.json> [--report <report.json>]");
    process.exitCode = 2;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
