#!/usr/bin/env bun

import { existsSync, readFileSync, statSync } from "node:fs";
import { join, resolve } from "node:path";

const root = process.argv[2] ? resolve(process.argv[2]) : "";
const required = ["INDEX.md", "00-overview/project.md", "01-architecture/depa.md", "02-code-evidence/evidence.md", "03-uncertainty/blocked.md", "_index/manifest.json"];
if (!root || !existsSync(root) || !statSync(root).isDirectory()) {
  console.error("Usage: validate-wiki-export.ts <wiki-directory>");
  process.exit(2);
}
for (const path of required) {
  if (!existsSync(join(root, path))) {
    console.error(`Missing required Wiki artifact: ${path}`);
    process.exit(1);
  }
}
const index = readFileSync(join(root, "INDEX.md"), "utf8");
for (const path of required.slice(1, 5)) {
  if (!index.includes(`](${path})`)) {
    console.error(`INDEX.md does not link to ${path}`);
    process.exit(1);
  }
}
const manifest = JSON.parse(readFileSync(join(root, "_index/manifest.json"), "utf8")) as { pages?: unknown };
if (!Array.isArray(manifest.pages) || manifest.pages.some((page) => typeof page !== "string" || !existsSync(join(root, page)))) {
  console.error("Manifest pages must be existing relative files.");
  process.exit(1);
}
console.log(`Wiki export is valid: ${root}`);
