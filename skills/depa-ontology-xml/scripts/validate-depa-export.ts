#!/usr/bin/env bun

import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, resolve } from "node:path";

const root = process.argv[2] ? resolve(process.argv[2]) : "";
if (!root || !existsSync(root) || !statSync(root).isDirectory()) {
  console.error("Usage: validate-depa-export.ts <export-directory>");
  process.exit(2);
}

const files = new Set<string>();
function visit(dir: string) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) visit(path);
    else if (entry.isFile()) files.add(relative(root, path).replaceAll("\\", "/"));
  }
}
visit(root);

const hasSnapshot = files.has("depa-runtime-snapshot.xml");
const hasOntology = files.has("ontology.xml");
const hasJudgments = files.has("judgments/depa-judgments.xml");
const hasWiki = files.has("INDEX.md") && files.has("_index/manifest.json");
if (!hasSnapshot && !hasOntology && !hasWiki) {
  console.error("Export directory contains none of depa-runtime-snapshot.xml, ontology.xml, or INDEX.md.");
  process.exit(1);
}
if (hasOntology && !hasJudgments) {
  console.error("ontology-xml export must include judgments/depa-judgments.xml.");
  process.exit(1);
}
if (hasJudgments) {
  const text = readFileSync(join(root, "judgments/depa-judgments.xml"), "utf8");
  if (!text.includes("<DepaJudgments ") || !text.includes("repository=")) {
    console.error("DEPA judgment sidecar must have a DepaJudgments root with repository metadata.");
    process.exit(1);
  }
}
if (hasWiki) {
  const manifest = JSON.parse(readFileSync(join(root, "_index/manifest.json"), "utf8")) as { pages?: unknown };
  if (!Array.isArray(manifest.pages) || manifest.pages.some((page) => typeof page !== "string" || !files.has(page))) {
    console.error("Wiki manifest must list generated files using existing relative paths.");
    process.exit(1);
  }
}
console.log(`DEPA export structure is valid: ${root}`);
