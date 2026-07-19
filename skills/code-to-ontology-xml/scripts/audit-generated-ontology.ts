#!/usr/bin/env bun

import { writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  validateOntologyCoverage,
  type CoverageDiagnostic,
} from "./validate-ontology-coverage.ts";

type StageStatus = "PASS" | "FAIL" | "SKIPPED" | "NOT_REQUESTED";

export type GeneratedOntologyAuditReport = {
  schemaVersion: 1;
  status: "PASS" | "FAIL";
  stages: {
    ontologyXml: {
      status: "PASS" | "FAIL";
      diagnostics: string[];
    };
    coverage: {
      status: StageStatus;
      diagnostics: CoverageDiagnostic[];
    };
  };
};

export type AuditGeneratedOntologyOptions = {
  ontologyPath: string;
  workspaceRoot?: string;
  inventoryPath?: string;
  manifestPath?: string;
};

const ontologyValidator = resolve(
  import.meta.dir,
  "../../ontology-xml-dsl/scripts/validate-ontology-xml.ts",
);

function outputLines(value: string): string[] {
  return value
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
    .sort((left, right) => left.localeCompare(right));
}

function coverageFailure(error: unknown): CoverageDiagnostic {
  return {
    code: "COVERAGE_VALIDATOR_ERROR",
    location: "coverage",
    message: error instanceof Error ? error.message : String(error),
  };
}

export function auditGeneratedOntology(
  options: AuditGeneratedOntologyOptions,
): GeneratedOntologyAuditReport {
  if (Boolean(options.inventoryPath) !== Boolean(options.manifestPath)) {
    throw new Error("--inventory and --manifest must be provided together.");
  }

  const ontologyPath = resolve(options.ontologyPath);
  const workspaceRoot = resolve(options.workspaceRoot ?? dirname(ontologyPath));
  const strictResult = spawnSync(
    process.execPath,
    [
      "run",
      ontologyValidator,
      ontologyPath,
      "--workspace-root",
      workspaceRoot,
      "--generated",
    ],
    {
      cwd: workspaceRoot,
      encoding: "utf8",
    },
  );
  const strictDiagnostics = outputLines(strictResult.stderr ?? "");
  if (strictResult.error) strictDiagnostics.push(strictResult.error.message);
  if (strictResult.status !== 0 && strictDiagnostics.length === 0) {
    strictDiagnostics.push(`Ontology validator exited with code ${String(strictResult.status)}.`);
  }

  const ontologyXml = {
    status: strictResult.status === 0 ? "PASS" as const : "FAIL" as const,
    diagnostics: [...new Set(strictDiagnostics)].sort((left, right) => left.localeCompare(right)),
  };
  let coverage: GeneratedOntologyAuditReport["stages"]["coverage"] = {
    status: options.inventoryPath ? "SKIPPED" : "NOT_REQUESTED",
    diagnostics: [],
  };

  if (ontologyXml.status === "PASS" && options.inventoryPath && options.manifestPath) {
    try {
      const result = validateOntologyCoverage({
        ontologyPath,
        inventoryPath: options.inventoryPath,
        manifestPath: options.manifestPath,
        workspaceRoot,
      });
      coverage = {
        status: result.ok ? "PASS" : "FAIL",
        diagnostics: result.diagnostics,
      };
    } catch (error) {
      coverage = {
        status: "FAIL",
        diagnostics: [coverageFailure(error)],
      };
    }
  }

  return {
    schemaVersion: 1,
    status: ontologyXml.status === "PASS"
        && (coverage.status === "PASS" || coverage.status === "NOT_REQUESTED")
      ? "PASS"
      : "FAIL",
    stages: { ontologyXml, coverage },
  };
}

export function serializeGeneratedOntologyAuditReport(
  report: GeneratedOntologyAuditReport,
): string {
  return `${JSON.stringify(report, null, 2)}\n`;
}

type ParsedArguments = AuditGeneratedOntologyOptions & {
  reportPath: string;
};

function parseArguments(args: string[]): ParsedArguments {
  const values = new Map<string, string>();
  for (let index = 0; index < args.length; index += 1) {
    const flag = args[index]!;
    const value = args[index + 1];
    if (!flag.startsWith("--") || !value || value.startsWith("--")) {
      throw new Error(`Expected a value after ${flag}.`);
    }
    if (!["--ontology", "--workspace-root", "--inventory", "--manifest", "--report"].includes(flag)) {
      throw new Error(`Unknown option '${flag}'.`);
    }
    values.set(flag, value);
    index += 1;
  }
  const required = (flag: string) => {
    const value = values.get(flag);
    if (!value) throw new Error(`Missing required argument ${flag}.`);
    return value;
  };
  return {
    ontologyPath: required("--ontology"),
    workspaceRoot: values.get("--workspace-root"),
    inventoryPath: values.get("--inventory"),
    manifestPath: values.get("--manifest"),
    reportPath: required("--report"),
  };
}

async function main(): Promise<void> {
  try {
    const options = parseArguments(process.argv.slice(2));
    const report = auditGeneratedOntology(options);
    writeFileSync(resolve(options.reportPath), serializeGeneratedOntologyAuditReport(report));
    if (report.status === "FAIL") {
      for (const diagnostic of report.stages.ontologyXml.diagnostics) {
        console.error(`ONTOLOGY_XML ${diagnostic}`);
      }
      for (const diagnostic of report.stages.coverage.diagnostics) {
        console.error(`${diagnostic.code} ${diagnostic.location}: ${diagnostic.message}`);
      }
      process.exitCode = 1;
      return;
    }
    console.log(
      `Generated ontology audit PASS: strict XML ${report.stages.ontologyXml.status}, ` +
      `coverage ${report.stages.coverage.status}.`,
    );
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    console.error(
      "Usage: audit-generated-ontology.ts --ontology <ontology.xml> --report <audit-report.json> " +
      "[--workspace-root <path>] [--inventory <inventory.json> --manifest <coverage-manifest.json>]",
    );
    process.exitCode = 2;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
