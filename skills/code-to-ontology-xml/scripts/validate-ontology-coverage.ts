#!/usr/bin/env bun

import { existsSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { dirname, isAbsolute, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { parseDslResource } from "../../../framework/dsl-core/src/parser.ts";

type XmlObject = Record<string, unknown>;
type Disposition = "mapped" | "rejected" | "unresolved";

export type CoverageDiagnostic = {
  code: string;
  location: string;
  message: string;
};

type InventoryObservation = {
  observationKey: string;
  repository: string;
  revision: string;
  language: string;
  kind: string;
  symbol: string;
  path: string;
  line: number;
  resolver: string;
  confidence: number;
};

type InventoryRepository = {
  key: string;
  revision: string;
  language: string;
  include: string[];
  exclude: string[];
  stats: {
    filesScanned: number;
    filesExcluded: number;
    filesFailed: number;
    filesUnresolved: number;
  };
};

type Inventory = {
  schemaVersion: number;
  repositories: InventoryRepository[];
  observations: InventoryObservation[];
};

type ManifestRepository = {
  key: string;
  revision: string;
  root?: string;
};

type RequiredDomain = {
  domain: string;
  status: "covered" | "partial" | "missing";
  typeRefs: string[];
  relationRefs?: string[];
  ruleRefs?: string[];
  stateMachineRefs?: string[];
  transitionRefs?: string[];
  mappingRefs: string[];
  evidenceRefs: string[];
  waiverRef?: string;
};

type Candidate = {
  candidateKey: string;
  domain: string;
  family: string;
  disposition: Disposition;
  observationKeys?: string[];
  ontologyRefs?: string[];
  evidenceRefs?: string[];
  reason?: string;
};

type SignalDisposition = {
  observationKey: string;
  disposition: Disposition;
  candidateRef?: string;
  ontologyRef?: string;
  reason?: string;
};

type CoverageManifest = {
  schemaVersion: number;
  repositories: ManifestRepository[];
  requiredDomains: RequiredDomain[];
  candidates: Candidate[];
  candidateTotals: Record<Disposition, number>;
  signalDispositions: {
    rules: SignalDisposition[];
    lifecycles: SignalDisposition[];
  };
  completion?: {
    allowUnresolved?: boolean;
  };
  waivers?: Array<{
    id: string;
    owner: string;
    reason: string;
    metrics?: string[];
  }>;
};

type OntologyCounts = {
  types: number;
  relations: number;
  rules: number;
  stateMachines: number;
  transitions: number;
  mappings: number;
  evidence: number;
};

export type CoverageReport = {
  schemaVersion: 1;
  status: "PASS" | "FAIL";
  ontology: OntologyCounts;
  repositories: Array<{
    key: string;
    language: string;
    revision: string;
    filesScanned: number;
    filesExcluded: number;
    filesFailed: number;
    filesUnresolved: number;
    observations: number;
  }>;
  domains: Array<{
    domain: string;
    status: string;
    types: number;
    mappings: number;
    evidence: number;
    waiverRef?: string;
  }>;
  candidates: {
    total: number;
    mapped: number;
    rejected: number;
    unresolved: number;
  };
  signals: {
    rules: SignalReport;
    lifecycles: SignalReport;
  };
  evidenceAnchors: {
    total: number;
    valid: number;
    unchecked: number;
    invalid: number;
  };
  diagnostics: CoverageDiagnostic[];
};

type SignalReport = {
  inventory: number;
  disposed: number;
  mapped: number;
  rejected: number;
  unresolved: number;
};

export type ValidateCoverageOptions = {
  ontologyPath: string;
  inventoryPath: string;
  manifestPath: string;
  workspaceRoot?: string;
};

export type CoverageValidationResult = {
  ok: boolean;
  diagnostics: CoverageDiagnostic[];
  report: CoverageReport;
};

const moduleRootNames = new Set([
  "TypeModule",
  "RelationModule",
  "RuleModule",
  "LifecycleModule",
  "ImplementationMappingModule",
  "EvidenceModule",
  "SchemaEvolutionModule",
]);
const dispositionValues = new Set<Disposition>(["mapped", "rejected", "unresolved"]);
const ruleSignalKinds = new Set(["rule-signal"]);
const lifecycleSignalKinds = new Set(["lifecycle-signal"]);
const depaPattern = /(^|[^a-z0-9])depa(?:[_:./-]|$)/i;

function asArray<T>(value: T | T[] | undefined): T[] {
  if (value === undefined) return [];
  return Array.isArray(value) ? value : [value];
}

function attribute(node: XmlObject, name: string): string {
  return typeof node[`@_${name}`] === "string" ? String(node[`@_${name}`]) : "";
}

function objectEntriesDeep(value: unknown): Array<[string, XmlObject]> {
  const result: Array<[string, XmlObject]> = [];
  if (!value || typeof value !== "object") return result;
  for (const [key, child] of Object.entries(value as XmlObject)) {
    for (const item of asArray(child as unknown)) {
      if (!item || typeof item !== "object") continue;
      result.push([key, item as XmlObject]);
      result.push(...objectEntriesDeep(item));
    }
  }
  return result;
}

function normalizeLocation(base: string, path: string): string {
  const value = relative(base, path).split(sep).join("/");
  return value || ".";
}

function diagnostic(
  diagnostics: CoverageDiagnostic[],
  code: string,
  location: string,
  message: string,
): void {
  diagnostics.push({ code, location, message });
}

function sortDiagnostics(diagnostics: CoverageDiagnostic[]): CoverageDiagnostic[] {
  return diagnostics.sort((left, right) =>
    `${left.code}\0${left.location}\0${left.message}`.localeCompare(`${right.code}\0${right.location}\0${right.message}`)
  );
}

type OntologyIndex = {
  documents: Array<{ file: string; rootName: string; source: string; root: XmlObject }>;
  transitionOwners: Map<string, string>;
  ids: {
    types: Map<string, XmlObject>;
    relations: Map<string, XmlObject>;
    rules: Map<string, XmlObject>;
    stateMachines: Map<string, XmlObject>;
    transitions: Map<string, XmlObject>;
    mappings: Map<string, XmlObject>;
    evidence: Map<string, XmlObject>;
  };
};

function emptyIndex(): OntologyIndex {
  return {
    documents: [],
    transitionOwners: new Map(),
    ids: {
      types: new Map(),
      relations: new Map(),
      rules: new Map(),
      stateMachines: new Map(),
      transitions: new Map(),
      mappings: new Map(),
      evidence: new Map(),
    },
  };
}

function resolveHref(file: string, href: string, workspaceRoot: string): string | null {
  if (href.startsWith("vfs://./")) return resolve(dirname(file), href.slice("vfs://./".length));
  if (href.startsWith("vfs://@/")) return resolve(workspaceRoot, href.slice("vfs://@/".length));
  return null;
}

function loadOntologyBundle(
  rootFile: string,
  workspaceRoot: string,
  diagnostics: CoverageDiagnostic[],
): OntologyIndex {
  const index = emptyIndex();
  const visited = new Set<string>();
  const base = dirname(rootFile);

  function load(file: string, expectedRoot?: string): void {
    if (visited.has(file)) return;
    visited.add(file);
    const location = normalizeLocation(base, file);
    if (!existsSync(file) || !statSync(file).isFile()) {
      diagnostic(diagnostics, "MODULE_NOT_FOUND", location, "Ontology module does not exist or is not a regular file.");
      return;
    }
    const source = readFileSync(file, "utf8");
    let document: XmlObject;
    let rootName: string;
    try {
      const parsed = parseDslResource(source, { uri: `vfs://coverage/${location}` });
      document = parsed.document;
      rootName = parsed.rootTag;
    } catch (error) {
      diagnostic(
        diagnostics,
        "XML_PARSE_ERROR",
        location,
        error instanceof Error ? error.message : String(error),
      );
      return;
    }
    if (expectedRoot && rootName !== expectedRoot) {
      diagnostic(diagnostics, "MODULE_ROOT_MISMATCH", location, `Expected ${expectedRoot}, found ${rootName}.`);
    }
    if (file === rootFile && rootName !== "Ontology") {
      diagnostic(diagnostics, "ONTOLOGY_ROOT_INVALID", location, `Expected Ontology root, found ${rootName}.`);
    }
    const root = document[rootName] as XmlObject;
    index.documents.push({ file, rootName, source, root });

    for (const [element, node] of objectEntriesDeep(root)) {
      const href = attribute(node, "href");
      if (!href || !moduleRootNames.has(element)) continue;
      const target = resolveHref(file, href, workspaceRoot);
      if (!target) {
        diagnostic(diagnostics, "MODULE_HREF_INVALID", location, `Unsupported module href '${href}'.`);
        continue;
      }
      load(target, element);
    }
  }

  load(rootFile, "Ontology");

  const elementCollections: Record<string, keyof OntologyIndex["ids"]> = {
    Type: "types",
    Relation: "relations",
    Rule: "rules",
    StateMachine: "stateMachines",
    Transition: "transitions",
    ImplementationMapping: "mappings",
    Evidence: "evidence",
  };
  for (const document of index.documents) {
    const location = normalizeLocation(base, document.file);
    for (const [element, node] of objectEntriesDeep(document.root)) {
      const collectionName = elementCollections[element];
      if (!collectionName) continue;
      const id = attribute(node, "id");
      if (!id) continue;
      const collection = index.ids[collectionName];
      if (collection.has(id)) {
        diagnostic(diagnostics, "DUPLICATE_ONTOLOGY_ID", location, `Duplicate ${element} id '${id}'.`);
      } else {
        collection.set(id, node);
      }
    }
  }
  for (const [stateMachineId, stateMachine] of index.ids.stateMachines) {
    for (const [element, node] of objectEntriesDeep(stateMachine)) {
      if (element !== "Transition") continue;
      const transitionId = attribute(node, "id");
      if (transitionId && !index.transitionOwners.has(transitionId)) {
        index.transitionOwners.set(transitionId, stateMachineId);
      }
    }
  }
  return index;
}

function validatePortablePath(path: string): boolean {
  return Boolean(path) && !isAbsolute(path) && !path.includes("\\") && !path.split("/").includes("..");
}

function knownSemanticIds(index: OntologyIndex): Set<string> {
  return new Set([
    ...index.ids.types.keys(),
    ...index.ids.relations.keys(),
    ...index.ids.rules.keys(),
    ...index.ids.stateMachines.keys(),
    ...index.ids.transitions.keys(),
    ...index.ids.mappings.keys(),
  ]);
}

function validateManifestShape(
  inventory: Inventory,
  manifest: CoverageManifest,
  diagnostics: CoverageDiagnostic[],
): void {
  if (inventory.schemaVersion !== 1) {
    diagnostic(diagnostics, "INVENTORY_SCHEMA_UNSUPPORTED", "inventory", `Expected schemaVersion 1, found ${inventory.schemaVersion}.`);
  }
  if (manifest.schemaVersion !== 1) {
    diagnostic(diagnostics, "MANIFEST_SCHEMA_UNSUPPORTED", "manifest", `Expected schemaVersion 1, found ${manifest.schemaVersion}.`);
  }
  for (const [label, value] of [
    ["inventory.repositories", inventory.repositories],
    ["inventory.observations", inventory.observations],
    ["manifest.repositories", manifest.repositories],
    ["manifest.requiredDomains", manifest.requiredDomains],
    ["manifest.candidates", manifest.candidates],
  ] as const) {
    if (!Array.isArray(value)) diagnostic(diagnostics, "SCHEMA_FIELD_INVALID", label, "Expected an array.");
  }
  if (!manifest.signalDispositions || !Array.isArray(manifest.signalDispositions.rules) ||
    !Array.isArray(manifest.signalDispositions.lifecycles)) {
    diagnostic(diagnostics, "SCHEMA_FIELD_INVALID", "manifest.signalDispositions", "Expected rules and lifecycles arrays.");
  }
}

function validateRepositories(
  inventory: Inventory,
  manifest: CoverageManifest,
  diagnostics: CoverageDiagnostic[],
): Map<string, ManifestRepository> {
  const selected = new Map<string, ManifestRepository>();
  const inventoryByKey = new Map(inventory.repositories.map((repository) => [repository.key, repository]));
  for (const repository of manifest.repositories ?? []) {
    const location = `repository:${repository.key || "<missing>"}`;
    if (!repository.key || !repository.revision) {
      diagnostic(diagnostics, "REPOSITORY_IDENTITY_MISSING", location, "Repository key and revision are required.");
      continue;
    }
    if (selected.has(repository.key)) {
      diagnostic(diagnostics, "DUPLICATE_REPOSITORY", location, `Repository '${repository.key}' is declared more than once.`);
      continue;
    }
    selected.set(repository.key, repository);
    const observed = inventoryByKey.get(repository.key);
    if (!observed) {
      diagnostic(diagnostics, "REPOSITORY_NOT_IN_INVENTORY", location, "Selected repository is absent from inventory.");
      continue;
    }
    if (observed.revision !== repository.revision) {
      diagnostic(
        diagnostics,
        "REPOSITORY_REVISION_MISMATCH",
        location,
        `Manifest revision '${repository.revision}' does not match inventory revision '${observed.revision}'.`,
      );
    }
    if (!Array.isArray(observed.include) || !Array.isArray(observed.exclude)) {
      diagnostic(diagnostics, "REPOSITORY_SCOPE_MISSING", location, "Inventory must retain include and exclude scope.");
    }
  }
  for (const repository of inventory.repositories ?? []) {
    if (!selected.has(repository.key)) {
      diagnostic(
        diagnostics,
        "INVENTORY_REPOSITORY_UNSELECTED",
        `repository:${repository.key}`,
        "Inventory repository is absent from manifest.",
      );
    }
  }
  return selected;
}

function validateEvidence(
  index: OntologyIndex,
  inventory: Inventory,
  selectedRepositories: Map<string, ManifestRepository>,
  manifestDirectory: string,
  diagnostics: CoverageDiagnostic[],
): CoverageReport["evidenceAnchors"] {
  const inventoryRepositories = new Map(inventory.repositories.map((repository) => [repository.key, repository]));
  let valid = 0;
  let unchecked = 0;
  let invalid = 0;

  for (const [id, evidence] of [...index.ids.evidence.entries()].sort(([left], [right]) => left.localeCompare(right))) {
    const location = `evidence:${id}`;
    const repositoryKey = attribute(evidence, "repository");
    const revision = attribute(evidence, "revision");
    const path = attribute(evidence, "path");
    let anchorInvalid = false;
    if (!validatePortablePath(path)) {
      diagnostic(diagnostics, "EVIDENCE_PATH_INVALID", location, `Evidence path '${path}' must be repository-relative.`);
      anchorInvalid = true;
    }
    const selected = selectedRepositories.get(repositoryKey);
    const observed = inventoryRepositories.get(repositoryKey);
    if (!selected || !observed) {
      diagnostic(diagnostics, "EVIDENCE_REPOSITORY_UNKNOWN", location, `Repository '${repositoryKey}' is not selected.`);
      anchorInvalid = true;
    } else if (revision !== selected.revision || revision !== observed.revision) {
      diagnostic(
        diagnostics,
        "EVIDENCE_REVISION_MISMATCH",
        location,
        `Evidence revision '${revision}' does not match selected revision '${selected.revision}'.`,
      );
      anchorInvalid = true;
    }

    if (anchorInvalid) {
      invalid += 1;
      continue;
    }
    if (!selected?.root) {
      unchecked += 1;
      continue;
    }
    const root = resolve(manifestDirectory, selected.root);
    const target = resolve(root, path);
    if (!(target === root || target.startsWith(`${root}${sep}`)) || !existsSync(target) || !statSync(target).isFile()) {
      diagnostic(diagnostics, "EVIDENCE_ANCHOR_MISSING", location, `Evidence anchor '${path}' does not exist under its repository root.`);
      invalid += 1;
      continue;
    }
    valid += 1;
  }
  return { total: index.ids.evidence.size, valid, unchecked, invalid };
}

const domainRefCollections: Array<{
  field: keyof RequiredDomain;
  label: string;
  ids: keyof OntologyIndex["ids"];
  code: string;
}> = [
  { field: "typeRefs", label: "type", ids: "types", code: "UNKNOWN_TYPE_REF" },
  { field: "relationRefs", label: "relation", ids: "relations", code: "UNKNOWN_RELATION_REF" },
  { field: "ruleRefs", label: "rule", ids: "rules", code: "UNKNOWN_RULE_REF" },
  { field: "stateMachineRefs", label: "state machine", ids: "stateMachines", code: "UNKNOWN_STATE_MACHINE_REF" },
  { field: "transitionRefs", label: "transition", ids: "transitions", code: "UNKNOWN_TRANSITION_REF" },
  { field: "mappingRefs", label: "mapping", ids: "mappings", code: "UNKNOWN_MAPPING_REF" },
  { field: "evidenceRefs", label: "evidence", ids: "evidence", code: "UNKNOWN_EVIDENCE_REF" },
];

function validateDomains(
  manifest: CoverageManifest,
  index: OntologyIndex,
  diagnostics: CoverageDiagnostic[],
): CoverageReport["domains"] {
  const waiverById = new Map((manifest.waivers ?? []).map((waiver) => [waiver.id, waiver]));
  const seen = new Set<string>();
  const reports: CoverageReport["domains"] = [];
  for (const domain of [...(manifest.requiredDomains ?? [])].sort((left, right) => left.domain.localeCompare(right.domain))) {
    const location = `domain:${domain.domain || "<missing>"}`;
    if (!domain.domain) diagnostic(diagnostics, "DOMAIN_NAME_MISSING", location, "Required domain needs a stable name.");
    if (seen.has(domain.domain)) diagnostic(diagnostics, "DUPLICATE_DOMAIN", location, `Domain '${domain.domain}' is declared more than once.`);
    seen.add(domain.domain);
    if (domain.status === "missing") {
      diagnostic(diagnostics, "DOMAIN_MISSING", location, "Required domain status must not be missing.");
    } else if (domain.status === "partial") {
      const waiver = domain.waiverRef ? waiverById.get(domain.waiverRef) : undefined;
      if (!waiver || !waiver.owner?.trim() || !waiver.reason?.trim()) {
        diagnostic(diagnostics, "PARTIAL_DOMAIN_UNWAIVED", location, "Partial domains require a waiver with owner and reason.");
      }
    } else if (domain.status !== "covered") {
      diagnostic(diagnostics, "DOMAIN_STATUS_INVALID", location, `Unsupported domain status '${String(domain.status)}'.`);
    }

    for (const requiredField of ["typeRefs", "mappingRefs", "evidenceRefs"] as const) {
      if (!Array.isArray(domain[requiredField])) {
        diagnostic(diagnostics, "DOMAIN_REFS_MISSING", location, `${requiredField} must be an array.`);
      } else if (domain.status === "covered" && domain[requiredField].length === 0) {
        diagnostic(diagnostics, "DOMAIN_FAMILY_EMPTY", location, `Covered domain requires at least one ${requiredField}.`);
      }
    }
    for (const definition of domainRefCollections) {
      const refs = asArray(domain[definition.field] as string[] | undefined);
      for (const ref of [...refs].sort((left, right) => left.localeCompare(right))) {
        if (!index.ids[definition.ids].has(ref)) {
          diagnostic(diagnostics, definition.code, location, `Unknown ${definition.label} reference '${ref}'.`);
        }
      }
    }
    reports.push({
      domain: domain.domain,
      status: domain.status,
      types: asArray(domain.typeRefs).length,
      mappings: asArray(domain.mappingRefs).length,
      evidence: asArray(domain.evidenceRefs).length,
      ...(domain.waiverRef ? { waiverRef: domain.waiverRef } : {}),
    });
  }
  if ((manifest.requiredDomains ?? []).length === 0) {
    diagnostic(diagnostics, "REQUIRED_DOMAINS_EMPTY", "manifest.requiredDomains", "At least one core domain is required.");
  }
  return reports;
}

function validateCandidates(
  manifest: CoverageManifest,
  inventory: Inventory,
  index: OntologyIndex,
  diagnostics: CoverageDiagnostic[],
): CoverageReport["candidates"] {
  const observations = new Set((inventory.observations ?? []).map((observation) => observation.observationKey));
  const domains = new Set((manifest.requiredDomains ?? []).map((domain) => domain.domain));
  const semanticIds = knownSemanticIds(index);
  const counts = { total: 0, mapped: 0, rejected: 0, unresolved: 0 };
  const seen = new Set<string>();

  for (const candidate of [...(manifest.candidates ?? [])].sort((left, right) =>
    String(left.candidateKey).localeCompare(String(right.candidateKey))
  )) {
    const location = `candidate:${candidate.candidateKey || "<missing>"}`;
    counts.total += 1;
    if (seen.has(candidate.candidateKey)) {
      diagnostic(diagnostics, "DUPLICATE_CANDIDATE", location, `Candidate '${candidate.candidateKey}' is declared more than once.`);
    }
    seen.add(candidate.candidateKey);
    if (!dispositionValues.has(candidate.disposition)) {
      diagnostic(diagnostics, "CANDIDATE_DISPOSITION_INVALID", location, `Unsupported disposition '${String(candidate.disposition)}'.`);
      continue;
    }
    counts[candidate.disposition] += 1;
    if (!domains.has(candidate.domain)) {
      diagnostic(diagnostics, "CANDIDATE_DOMAIN_UNKNOWN", location, `Candidate domain '${candidate.domain}' is not required.`);
    }
    for (const observationKey of candidate.observationKeys ?? []) {
      if (!observations.has(observationKey)) {
        diagnostic(diagnostics, "CANDIDATE_OBSERVATION_UNKNOWN", location, `Unknown observation '${observationKey}'.`);
      }
    }
    if (!candidate.observationKeys?.length) {
      diagnostic(diagnostics, "CANDIDATE_OBSERVATIONS_EMPTY", location, "Candidate requires at least one source observation.");
    }
    for (const evidenceRef of candidate.evidenceRefs ?? []) {
      if (!index.ids.evidence.has(evidenceRef)) {
        diagnostic(diagnostics, "CANDIDATE_EVIDENCE_UNKNOWN", location, `Unknown evidence '${evidenceRef}'.`);
      }
    }
    if (candidate.disposition === "mapped") {
      if (!candidate.ontologyRefs?.length) {
        diagnostic(diagnostics, "MAPPED_CANDIDATE_UNREACHABLE", location, "Mapped candidate requires ontologyRefs.");
      }
      for (const ontologyRef of candidate.ontologyRefs ?? []) {
        if (!semanticIds.has(ontologyRef)) {
          diagnostic(diagnostics, "CANDIDATE_ONTOLOGY_REF_UNKNOWN", location, `Unknown ontology object '${ontologyRef}'.`);
        }
      }
      if (!candidate.evidenceRefs?.length) {
        diagnostic(diagnostics, "MAPPED_CANDIDATE_UNEVIDENCED", location, "Mapped candidate requires evidenceRefs.");
      }
    } else if (!candidate.reason?.trim()) {
      diagnostic(diagnostics, "CANDIDATE_REASON_MISSING", location, `${candidate.disposition} candidate requires a durable reason.`);
    }
    if (candidate.disposition === "unresolved" && !manifest.completion?.allowUnresolved) {
      diagnostic(diagnostics, "UNRESOLVED_CANDIDATE_FORBIDDEN", location, "Completion policy does not allow unresolved candidates.");
    }
  }

  for (const disposition of ["mapped", "rejected", "unresolved"] as const) {
    const expected = manifest.candidateTotals?.[disposition];
    if (!Number.isInteger(expected) || expected < 0) {
      diagnostic(diagnostics, "CANDIDATE_TOTAL_INVALID", `manifest.candidateTotals.${disposition}`, "Expected a non-negative integer.");
    } else if (expected !== counts[disposition]) {
      diagnostic(
        diagnostics,
        "CANDIDATE_TOTAL_MISMATCH",
        `manifest.candidateTotals.${disposition}`,
        `Declared ${expected}, found ${counts[disposition]} candidates.`,
      );
    }
  }
  return counts;
}

function validateCanonicalCandidateCoverage(
  manifest: CoverageManifest,
  index: OntologyIndex,
  diagnostics: CoverageDiagnostic[],
): void {
  const mappedRefs = new Set(
    (manifest.candidates ?? [])
      .filter((candidate) => candidate.disposition === "mapped")
      .flatMap((candidate) => candidate.ontologyRefs ?? []),
  );
  const canonicalCollections: Array<{
    ids: Map<string, XmlObject>;
    locationKind: string;
    label: string;
  }> = [
    { ids: index.ids.types, locationKind: "type", label: "Type" },
    { ids: index.ids.relations, locationKind: "relation", label: "Relation" },
    { ids: index.ids.rules, locationKind: "rule", label: "Rule" },
    { ids: index.ids.stateMachines, locationKind: "state-machine", label: "StateMachine" },
    { ids: index.ids.mappings, locationKind: "mapping", label: "ImplementationMapping" },
  ];

  for (const collection of canonicalCollections) {
    for (const id of [...collection.ids.keys()].sort((left, right) => left.localeCompare(right))) {
      if (mappedRefs.has(id)) continue;
      diagnostic(
        diagnostics,
        "CANONICAL_OBJECT_UNCOVERED",
        `${collection.locationKind}:${id}`,
        `Canonical ${collection.label} is not covered by any mapped candidate ontologyRefs.`,
      );
    }
  }

  for (const transitionId of [...index.ids.transitions.keys()].sort((left, right) => left.localeCompare(right))) {
    const owner = index.transitionOwners.get(transitionId);
    if (mappedRefs.has(transitionId) || (owner && mappedRefs.has(owner))) continue;
    diagnostic(
      diagnostics,
      "CANONICAL_OBJECT_UNCOVERED",
      `transition:${transitionId}`,
      "Canonical Transition is not covered by any mapped candidate ontologyRefs or its owning StateMachine candidate.",
    );
  }
}

function validateSignals(
  family: "rules" | "lifecycles",
  inventory: Inventory,
  manifest: CoverageManifest,
  index: OntologyIndex,
  candidateKeys: Set<string>,
  diagnostics: CoverageDiagnostic[],
): SignalReport {
  const kinds = family === "rules" ? ruleSignalKinds : lifecycleSignalKinds;
  const inventorySignals = (inventory.observations ?? []).filter((observation) => kinds.has(observation.kind));
  const inventoryKeys = new Set(inventorySignals.map((observation) => observation.observationKey));
  const dispositions = manifest.signalDispositions?.[family] ?? [];
  const seen = new Set<string>();
  const report: SignalReport = {
    inventory: inventorySignals.length,
    disposed: dispositions.length,
    mapped: 0,
    rejected: 0,
    unresolved: 0,
  };
  for (const item of [...dispositions].sort((left, right) => left.observationKey.localeCompare(right.observationKey))) {
    const location = `${family}-signal:${item.observationKey || "<missing>"}`;
    if (seen.has(item.observationKey)) {
      diagnostic(diagnostics, "SIGNAL_DISPOSITION_DUPLICATE", location, "Signal has more than one disposition.");
    }
    seen.add(item.observationKey);
    if (!inventoryKeys.has(item.observationKey)) {
      diagnostic(diagnostics, "SIGNAL_OBSERVATION_UNKNOWN", location, "Disposition does not resolve to an inventory signal.");
    }
    if (!dispositionValues.has(item.disposition)) {
      diagnostic(diagnostics, "SIGNAL_DISPOSITION_INVALID", location, `Unsupported disposition '${String(item.disposition)}'.`);
      continue;
    }
    report[item.disposition] += 1;
    if (item.candidateRef && !candidateKeys.has(item.candidateRef)) {
      diagnostic(diagnostics, "SIGNAL_CANDIDATE_UNKNOWN", location, `Unknown candidate '${item.candidateRef}'.`);
    }
    if (item.disposition === "mapped") {
      const validTarget = family === "rules"
        ? index.ids.rules.has(item.ontologyRef ?? "")
        : index.ids.transitions.has(item.ontologyRef ?? "") || index.ids.stateMachines.has(item.ontologyRef ?? "");
      if (!validTarget) {
        diagnostic(
          diagnostics,
          "SIGNAL_ONTOLOGY_REF_UNKNOWN",
          location,
          `Mapped ${family} signal does not resolve to a ${family === "rules" ? "rule" : "state machine or transition"}.`,
        );
      }
    } else if (!item.reason?.trim()) {
      diagnostic(diagnostics, "SIGNAL_REASON_MISSING", location, `${item.disposition} signal requires a reason.`);
    }
    if (item.disposition === "unresolved" && !manifest.completion?.allowUnresolved) {
      diagnostic(diagnostics, "UNRESOLVED_SIGNAL_FORBIDDEN", location, "Completion policy does not allow unresolved signals.");
    }
  }
  for (const observation of inventorySignals) {
    if (!seen.has(observation.observationKey)) {
      diagnostic(
        diagnostics,
        "SIGNAL_UNDISPOSED",
        `${family}-signal:${observation.observationKey}`,
        "Inventory signal has no mapped, rejected, or unresolved disposition.",
      );
    }
  }
  return report;
}

function validateXmlEvidenceRefs(index: OntologyIndex, diagnostics: CoverageDiagnostic[]): void {
  for (const document of index.documents) {
    for (const [element, node] of objectEntriesDeep(document.root)) {
      if (element !== "EvidenceRef") continue;
      const ref = attribute(node, "ref");
      if (!index.ids.evidence.has(ref)) {
        diagnostic(diagnostics, "XML_EVIDENCE_REF_UNKNOWN", `${document.rootName}:${ref}`, `EvidenceRef '${ref}' does not resolve.`);
      }
    }
  }
}

function validateDepa(
  index: OntologyIndex,
  inventorySource: string,
  manifest: CoverageManifest,
  ontologyDirectory: string,
  diagnostics: CoverageDiagnostic[],
): void {
  const portableManifest = {
    ...manifest,
    repositories: (manifest.repositories ?? []).map(({ root: _root, ...repository }) => repository),
  };
  const sources = [
    ...index.documents.map((document) => ({
      location: normalizeLocation(ontologyDirectory, document.file),
      source: document.source,
    })),
    { location: "inventory", source: inventorySource },
    { location: "manifest", source: JSON.stringify(portableManifest) },
  ];
  for (const source of sources.sort((left, right) => left.location.localeCompare(right.location))) {
    if (depaPattern.test(source.source)) {
      diagnostic(diagnostics, "DEPA_SEMANTICS", source.location, "DEPA semantics are forbidden in code-to-ontology inputs and outputs.");
    }
  }
}

function repositoryReport(inventory: Inventory): CoverageReport["repositories"] {
  return [...(inventory.repositories ?? [])]
    .sort((left, right) => left.key.localeCompare(right.key))
    .map((repository) => ({
      key: repository.key,
      language: repository.language,
      revision: repository.revision,
      filesScanned: repository.stats?.filesScanned ?? 0,
      filesExcluded: repository.stats?.filesExcluded ?? 0,
      filesFailed: repository.stats?.filesFailed ?? 0,
      filesUnresolved: repository.stats?.filesUnresolved ?? 0,
      observations: (inventory.observations ?? []).filter((observation) => observation.repository === repository.key).length,
    }));
}

export function validateOntologyCoverage(options: ValidateCoverageOptions): CoverageValidationResult {
  const ontologyPath = resolve(options.ontologyPath);
  const inventoryPath = resolve(options.inventoryPath);
  const manifestPath = resolve(options.manifestPath);
  const workspaceRoot = resolve(options.workspaceRoot ?? process.cwd());
  const diagnostics: CoverageDiagnostic[] = [];

  for (const [label, path] of [
    ["ontology", ontologyPath],
    ["inventory", inventoryPath],
    ["manifest", manifestPath],
  ] as const) {
    if (!existsSync(path) || !statSync(path).isFile()) {
      diagnostic(diagnostics, "INPUT_NOT_FOUND", label, "Input file does not exist or is not a regular file.");
    }
  }
  if (diagnostics.length > 0) {
    const sorted = sortDiagnostics(diagnostics);
    return {
      ok: false,
      diagnostics: sorted,
      report: {
        schemaVersion: 1,
        status: "FAIL",
        ontology: { types: 0, relations: 0, rules: 0, stateMachines: 0, transitions: 0, mappings: 0, evidence: 0 },
        repositories: [],
        domains: [],
        candidates: { total: 0, mapped: 0, rejected: 0, unresolved: 0 },
        signals: {
          rules: { inventory: 0, disposed: 0, mapped: 0, rejected: 0, unresolved: 0 },
          lifecycles: { inventory: 0, disposed: 0, mapped: 0, rejected: 0, unresolved: 0 },
        },
        evidenceAnchors: { total: 0, valid: 0, unchecked: 0, invalid: 0 },
        diagnostics: sorted,
      },
    };
  }

  const inventorySource = readFileSync(inventoryPath, "utf8");
  const manifestSource = readFileSync(manifestPath, "utf8");
  let inventory: Inventory;
  let manifest: CoverageManifest;
  try {
    inventory = JSON.parse(inventorySource) as Inventory;
  } catch (error) {
    diagnostic(diagnostics, "INVENTORY_JSON_INVALID", "inventory", error instanceof Error ? error.message : String(error));
    inventory = { schemaVersion: 0, repositories: [], observations: [] };
  }
  try {
    manifest = JSON.parse(manifestSource) as CoverageManifest;
  } catch (error) {
    diagnostic(diagnostics, "MANIFEST_JSON_INVALID", "manifest", error instanceof Error ? error.message : String(error));
    manifest = {
      schemaVersion: 0,
      repositories: [],
      requiredDomains: [],
      candidates: [],
      candidateTotals: { mapped: 0, rejected: 0, unresolved: 0 },
      signalDispositions: { rules: [], lifecycles: [] },
    };
  }

  validateManifestShape(inventory, manifest, diagnostics);
  const index = loadOntologyBundle(ontologyPath, workspaceRoot, diagnostics);
  const selectedRepositories = validateRepositories(inventory, manifest, diagnostics);
  validateXmlEvidenceRefs(index, diagnostics);
  const evidenceAnchors = validateEvidence(
    index,
    inventory,
    selectedRepositories,
    dirname(manifestPath),
    diagnostics,
  );
  const domains = validateDomains(manifest, index, diagnostics);
  const candidates = validateCandidates(manifest, inventory, index, diagnostics);
  validateCanonicalCandidateCoverage(manifest, index, diagnostics);
  const candidateKeys = new Set((manifest.candidates ?? []).map((candidate) => candidate.candidateKey));
  const signals = {
    rules: validateSignals("rules", inventory, manifest, index, candidateKeys, diagnostics),
    lifecycles: validateSignals("lifecycles", inventory, manifest, index, candidateKeys, diagnostics),
  };
  validateDepa(index, inventorySource, manifest, dirname(ontologyPath), diagnostics);

  const sortedDiagnostics = sortDiagnostics(diagnostics);
  const report: CoverageReport = {
    schemaVersion: 1,
    status: sortedDiagnostics.length === 0 ? "PASS" : "FAIL",
    ontology: {
      types: index.ids.types.size,
      relations: index.ids.relations.size,
      rules: index.ids.rules.size,
      stateMachines: index.ids.stateMachines.size,
      transitions: index.ids.transitions.size,
      mappings: index.ids.mappings.size,
      evidence: index.ids.evidence.size,
    },
    repositories: repositoryReport(inventory),
    domains,
    candidates,
    signals,
    evidenceAnchors,
    diagnostics: sortedDiagnostics,
  };
  return { ok: sortedDiagnostics.length === 0, diagnostics: sortedDiagnostics, report };
}

export function serializeCoverageReport(report: CoverageReport): string {
  return `${JSON.stringify(report, null, 2)}\n`;
}

function parseArguments(args: string[]) {
  const values = new Map<string, string>();
  for (let index = 0; index < args.length; index += 1) {
    const flag = args[index]!;
    const value = args[index + 1];
    if (!flag.startsWith("--") || !value || value.startsWith("--")) {
      throw new Error(`Expected a value after ${flag}.`);
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
    inventoryPath: required("--inventory"),
    manifestPath: required("--manifest"),
    reportPath: values.get("--report"),
    workspaceRoot: values.get("--workspace-root"),
  };
}

async function main(): Promise<void> {
  try {
    const options = parseArguments(process.argv.slice(2));
    const result = validateOntologyCoverage(options);
    if (options.reportPath) writeFileSync(resolve(options.reportPath), serializeCoverageReport(result.report));
    if (!result.ok) {
      for (const item of result.diagnostics) {
        console.error(`${item.code} ${item.location}: ${item.message}`);
      }
      process.exitCode = 1;
      return;
    }
    console.log(
      `Ontology coverage PASS: ${result.report.domains.length} domains, ` +
      `${result.report.candidates.total} candidates, ${result.report.ontology.evidence} evidence items.`,
    );
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    console.error(
      "Usage: validate-ontology-coverage.ts --ontology <ontology.xml> --inventory <inventory.json> " +
      "--manifest <coverage-manifest.json> [--report <coverage-report.json>] [--workspace-root <path>]",
    );
    process.exitCode = 2;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
