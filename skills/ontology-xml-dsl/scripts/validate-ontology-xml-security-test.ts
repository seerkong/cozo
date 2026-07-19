#!/usr/bin/env bun

import { mkdirSync, mkdtempSync, realpathSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const root = mkdtempSync(join(tmpdir(), "ontology-xml-validator-security-"));
const workspace = join(root, "workspace");
const outside = join(root, "outside");
const validator = join(import.meta.dir, "validate-ontology-xml.ts");
const ontology = `<?xml version="1.0"?><Ontology id="Example.Ontology" version="1.0.0"><Description>test</Description><Modules><TypeModule href="vfs://./modules/types.xml"/></Modules></Ontology>`;
const module = `<?xml version="1.0"?><TypeModule id="Example.Types"><Description>test</Description><Types/></TypeModule>`;

function assertRejected(path: string, label: string): void {
  const result = Bun.spawnSync([process.execPath, "run", validator, path, "--workspace-root", workspace]);
  const output = new TextDecoder().decode(result.stderr);
  if (result.exitCode === 0 || !/(symbolic link|escapes workspace root)/.test(output)) {
    throw new Error(`${label} must be rejected as a symbolic-link traversal; stderr: ${output}`);
  }
}

try {
  mkdirSync(workspace, { recursive: true });
  mkdirSync(outside, { recursive: true });
  const canonicalWorkspace = realpathSync(workspace);
  writeFileSync(join(root, "outside-root.xml"), ontology);
  writeFileSync(join(root, "outside-module.xml"), module);
  writeFileSync(join(workspace, "ontology.xml"), ontology);
  symlinkSync(join(root, "outside-root.xml"), join(workspace, "root-link.xml"));
  assertRejected(join(canonicalWorkspace, "root-link.xml"), "root XML symlink");

  mkdirSync(join(workspace, "modules"));
  rmSync(join(workspace, "modules"), { recursive: true, force: true });
  symlinkSync(outside, join(workspace, "modules"));
  writeFileSync(join(outside, "types.xml"), module);
  assertRejected(join(canonicalWorkspace, "ontology.xml"), "module-directory symlink");
  console.log("Ontology XML validator symlink-security reproductions passed.");
} finally {
  rmSync(root, { recursive: true, force: true });
}
