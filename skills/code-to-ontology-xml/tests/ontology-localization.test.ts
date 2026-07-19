import { describe, expect, test } from "bun:test";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative, resolve } from "node:path";

const workspace = resolve(import.meta.dir, "../../..");
const localized = join(workspace, "cozo-ontology/v0/ontology/sample-domain");
const localizer = join(localized, "generation/localize-ontology-descriptions.ts");

function xmlFiles(root: string): string[] {
  const result: string[] = [];
  const visit = (directory: string) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) visit(path);
      else if (entry.name.endsWith(".xml")) result.push(relative(root, path));
    }
  };
  visit(root);
  return result.sort();
}

function machineProjection(source: string): string {
  return source
    .replace(/<(Description|Statement|Summary)>[\s\S]*?<\/\1>/g, "")
    .replace(/\smessage="[^"]*"/g, "");
}

function runLocalizer(): void {
  const result = Bun.spawnSync({ cmd: [process.execPath, "run", localizer], stdout: "pipe", stderr: "pipe" });
  expect(result.exitCode).toBe(0);
}

describe("localized ontology XML", () => {
  test("is Chinese, idempotent, and does not change machine semantics", () => {
    const files = xmlFiles(localized);
    expect(files).toHaveLength(17);
    const before = new Map(files.map((file) => [file, readFileSync(join(localized, file), "utf8")]));

    runLocalizer();
    const first = new Map(files.map((file) => [file, readFileSync(join(localized, file), "utf8")]));
    runLocalizer();

    for (const file of files) {
      const output = readFileSync(join(localized, file), "utf8");
      expect(output).toBe(first.get(file));
      expect(machineProjection(first.get(file)!)).toBe(machineProjection(before.get(file)!));
    }

    const rules = readFileSync(join(localized, "rules/movement.xml"), "utf8");
    const evidence = readFileSync(join(localized, "evidence/backend.xml"), "utf8");
    expect(rules).toContain("实物记录仅在状态为 available-new 或 available-old 时可以出库。");
    expect(rules).toContain('code="OUTBOUND_RECORD_NOT_AVAILABLE" message="出库记录必须处于可出库状态。"');
    expect(evidence).toContain("的源码证据；其可核验范围由 repository、revision、path、symbol 及行号限定。");
  });
});
