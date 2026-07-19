#!/usr/bin/env bun

import { existsSync, lstatSync, readFileSync, realpathSync } from "node:fs";
import { dirname, isAbsolute, relative, resolve, sep } from "node:path";
import { XMLParser, XMLValidator } from "fast-xml-parser";

type ParsedDslResource = { document: Record<string, unknown> };

// Keep the parser contract aligned with framework/dsl-core without making this skill depend on
// a workspace-local framework path. The validator is intentionally runnable from this skill.
function parseDslResource(source: string, options: { uri: string }): ParsedDslResource {
  // XDocument emits a UTF-8 BOM by default. Treat it as transport encoding, not a document node.
  const normalizedSource = source.replace(/^\uFEFF/, "");
  const validation = XMLValidator.validate(normalizedSource);
  if (validation !== true) {
    const error = validation.err ?? {};
    const line = typeof error.line === "number" ? error.line : "?";
    const column = typeof error.col === "number" ? error.col : "?";
    throw new Error(`XML parse error at ${options.uri}:${line}:${column}: ${error.msg ?? "invalid XML"}`);
  }

  const document = new XMLParser({
    ignoreAttributes: false,
    ignoreDeclaration: true,
    attributeNamePrefix: "@_",
    parseTagValue: false,
    trimValues: false,
  }).parse(normalizedSource) as Record<string, unknown>;
  if (Object.keys(document).length !== 1) {
    throw new Error(`XML resource must have exactly one root element at ${options.uri}`);
  }
  return { document };
}

const allowedRoots = new Set([
  "Ontology",
  "TypeModule",
  "RelationModule",
  "RuleModule",
  "LifecycleModule",
  "ImplementationMappingModule",
  "EvidenceModule",
  "SchemaEvolutionModule",
]);
const moduleRoots = new Set([...allowedRoots].filter((root) => root !== "Ontology"));
const primitiveTypes = new Set(["String", "Number", "Bool", "Json", "Validity"]);
const gradeValues = new Set(["authoritative", "enforced", "contractual", "presentational", "inferred"]);
const statusValues = new Set(["accepted", "hypothesis"]);
const ruleKinds = new Set([
  "Conditional",
  "CrossEntity",
  "ComputedDependency",
  "Existential",
  "Uniqueness",
  "Cardinality",
  "Custom",
]);
const predicateElements = new Set([
  "All",
  "Any",
  "Not",
  "PropertyPresent",
  "PropertyEquals",
  "PropertyNotEquals",
  "PropertyIn",
  "PropertyCompare",
  "TypeIs",
  "RelatedExists",
  "EveryRelated",
  "RelatedCount",
  "ExistsRelated",
]);
const fqnPattern = /^[A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*)+$/;
const localNamePattern = /^[a-z][A-Za-z0-9]*$/;
const namespacedIdPattern = /^[a-z][a-z0-9-]*:[a-z0-9][a-z0-9._-]*$/;
const versionPattern = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;
const depaPattern = /depa_/i;

type ChildRule = { min?: number; max?: number };
type ElementSpec = {
  attributes?: readonly string[];
  requiredAttributes?: readonly string[];
  children?: Record<string, ChildRule>;
  atLeastOneOf?: readonly string[];
  minElementChildren?: number;
  maxElementChildren?: number;
  text?: "forbidden" | "required";
};

const predicateChildren = Object.fromEntries(
  [...predicateElements].map((element) => [element, {}]),
) as Record<string, ChildRule>;
const moduleReferenceChildren = Object.fromEntries(
  [...moduleRoots].map((element) => [element, {}]),
) as Record<string, ChildRule>;

const elementSpecs: Record<string, ElementSpec> = {
  "root:Ontology": {
    attributes: ["id", "version"],
    requiredAttributes: ["id", "version"],
    children: { Description: { max: 1 }, Modules: { min: 1, max: 1 } },
  },
  "module-reference": {
    attributes: ["href"],
    requiredAttributes: ["href"],
  },
  Description: { text: "required" },
  Modules: {
    children: moduleReferenceChildren,
    minElementChildren: 1,
  },
  TypeModule: {
    attributes: ["id"],
    requiredAttributes: ["id"],
    children: {
      Description: { max: 1 },
      MixinDeclarations: { max: 1 },
      Types: { max: 1 },
    },
    atLeastOneOf: ["MixinDeclarations", "Types"],
  },
  MixinDeclarations: {
    children: { Mixin: { min: 1 } },
  },
  Mixin: {
    attributes: ["id", "status"],
    requiredAttributes: ["id"],
    children: {
      Description: { max: 1 },
      Mixins: { max: 1 },
      Attributes: { max: 1 },
      EvidenceRefs: { max: 1 },
    },
  },
  Types: {
    children: { Type: { min: 1 } },
  },
  Type: {
    attributes: ["id", "parent", "abstract", "status"],
    requiredAttributes: ["id"],
    children: {
      Description: { max: 1 },
      Mixins: { max: 1 },
      Attributes: { max: 1 },
      ComputedAttributes: { max: 1 },
      EvidenceRefs: { max: 1 },
    },
  },
  Mixins: {
    children: { MixinRef: { min: 1 } },
  },
  MixinRef: {
    attributes: ["ref"],
    requiredAttributes: ["ref"],
  },
  Attributes: {
    children: { Attribute: { min: 1 } },
  },
  Attribute: {
    attributes: ["name", "type", "required"],
    requiredAttributes: ["name", "type", "required"],
    children: { Description: { max: 1 }, EvidenceRefs: { max: 1 } },
  },
  ComputedAttributes: {
    children: { ComputedAttribute: { min: 1 } },
  },
  ComputedAttribute: {
    attributes: ["name", "type"],
    requiredAttributes: ["name", "type"],
    children: {
      Description: { max: 1 },
      ImplementationRef: { min: 1, max: 1 },
      EvidenceRefs: { max: 1 },
    },
  },
  ImplementationRef: {
    attributes: ["ref"],
    requiredAttributes: ["ref"],
  },
  EvidenceRefs: {
    children: { EvidenceRef: { min: 1 } },
  },
  EvidenceRef: {
    attributes: ["ref"],
    requiredAttributes: ["ref"],
  },
  RelationModule: {
    attributes: ["id"],
    requiredAttributes: ["id"],
    children: { Description: { max: 1 }, Relations: { min: 1, max: 1 } },
  },
  Relations: {
    children: { Relation: { min: 1 } },
  },
  Relation: {
    attributes: ["id", "name", "from", "to", "directed", "min", "max", "status"],
    requiredAttributes: ["id", "name", "from", "to", "directed"],
    children: {
      Description: { max: 1 },
      Properties: { max: 1 },
      EvidenceRefs: { max: 1 },
    },
  },
  Properties: {
    children: { Property: { min: 1 } },
  },
  Property: {
    attributes: ["name", "type", "required"],
    requiredAttributes: ["name", "type", "required"],
    children: { Description: { max: 1 }, EvidenceRefs: { max: 1 } },
  },
  RuleModule: {
    attributes: ["id"],
    requiredAttributes: ["id"],
    children: { Description: { max: 1 }, Rules: { min: 1, max: 1 } },
  },
  Rules: {
    children: { Rule: { min: 1 } },
  },
  Rule: {
    attributes: ["id", "scope", "kind", "status"],
    requiredAttributes: ["id", "scope", "kind"],
    children: {
      Statement: { min: 1, max: 1 },
      When: { max: 1 },
      Require: { min: 1, max: 1 },
      Violation: { min: 1, max: 1 },
      RuntimeBinding: { max: 1 },
      EvidenceRefs: { max: 1 },
    },
  },
  Statement: { text: "required" },
  When: {
    children: predicateChildren,
    minElementChildren: 1,
    maxElementChildren: 1,
  },
  Require: {
    children: predicateChildren,
    minElementChildren: 1,
    maxElementChildren: 1,
  },
  All: {
    children: predicateChildren,
    minElementChildren: 2,
  },
  Any: {
    children: predicateChildren,
    minElementChildren: 2,
  },
  Not: {
    children: predicateChildren,
    minElementChildren: 1,
    maxElementChildren: 1,
  },
  PropertyPresent: {
    attributes: ["property"],
    requiredAttributes: ["property"],
  },
  PropertyEquals: {
    attributes: ["property", "value"],
    requiredAttributes: ["property", "value"],
  },
  PropertyNotEquals: {
    attributes: ["property", "value"],
    requiredAttributes: ["property", "value"],
  },
  PropertyIn: {
    attributes: ["property"],
    requiredAttributes: ["property"],
    children: { Value: { min: 1 } },
  },
  Value: {
    attributes: ["value"],
    requiredAttributes: ["value"],
  },
  PropertyCompare: {
    attributes: ["property", "op", "value"],
    requiredAttributes: ["property", "op", "value"],
  },
  TypeIs: {
    attributes: ["type"],
    requiredAttributes: ["type"],
  },
  RelatedExists: {
    attributes: ["relation"],
    requiredAttributes: ["relation"],
    children: predicateChildren,
    minElementChildren: 1,
    maxElementChildren: 1,
  },
  EveryRelated: {
    attributes: ["relation"],
    requiredAttributes: ["relation"],
    children: predicateChildren,
    minElementChildren: 1,
    maxElementChildren: 1,
  },
  RelatedCount: {
    attributes: ["relation", "op", "value"],
    requiredAttributes: ["relation", "op", "value"],
  },
  ExistsRelated: {
    attributes: ["relation", "direction", "targetType"],
    requiredAttributes: ["relation", "direction", "targetType"],
  },
  Violation: {
    attributes: ["code", "message"],
    requiredAttributes: ["code", "message"],
  },
  RuntimeBinding: {
    attributes: ["ref"],
    requiredAttributes: ["ref"],
  },
  LifecycleModule: {
    attributes: ["id"],
    requiredAttributes: ["id"],
    children: { Description: { max: 1 }, StateMachines: { min: 1, max: 1 } },
  },
  StateMachines: {
    children: { StateMachine: { min: 1 } },
  },
  StateMachine: {
    attributes: ["id", "subject", "stateProperty", "initial", "status"],
    requiredAttributes: ["id", "subject", "stateProperty", "initial"],
    children: {
      Description: { max: 1 },
      States: { min: 1, max: 1 },
      Transitions: { min: 1, max: 1 },
      EvidenceRefs: { max: 1 },
    },
  },
  States: {
    children: { State: { min: 1 } },
  },
  State: {
    attributes: ["id", "terminal"],
    requiredAttributes: ["id"],
    children: { Description: { max: 1 } },
  },
  Transitions: {
    children: { Transition: { min: 1 } },
  },
  Transition: {
    attributes: ["id", "action", "from", "to", "status"],
    requiredAttributes: ["id", "action", "from", "to"],
    children: {
      Description: { max: 1 },
      Guard: { max: 1 },
      Effects: { max: 1 },
      ImplementationRef: { max: 1 },
      EvidenceRefs: { max: 1 },
    },
  },
  Guard: {
    children: predicateChildren,
    minElementChildren: 1,
    maxElementChildren: 1,
  },
  Effects: {
    children: {
      SetProperty: {},
      ClearProperty: {},
      CreateRelation: {},
      RemoveRelation: {},
    },
    minElementChildren: 1,
  },
  SetProperty: {
    attributes: ["property", "value"],
    requiredAttributes: ["property", "value"],
  },
  ClearProperty: {
    attributes: ["property"],
    requiredAttributes: ["property"],
  },
  CreateRelation: {
    attributes: ["relation", "targetRef"],
    requiredAttributes: ["relation", "targetRef"],
  },
  RemoveRelation: {
    attributes: ["relation", "targetRef"],
    requiredAttributes: ["relation", "targetRef"],
  },
  ImplementationMappingModule: {
    attributes: ["id"],
    requiredAttributes: ["id"],
    children: { Description: { max: 1 }, Mappings: { min: 1, max: 1 } },
  },
  Mappings: {
    children: { ImplementationMapping: { min: 1 } },
  },
  ImplementationMapping: {
    attributes: ["id", "conceptRef", "ruleRef", "lifecycleRef", "relationRef", "status"],
    requiredAttributes: ["id"],
    children: {
      Description: { max: 1 },
      RepresentedBy: { max: 1 },
      ImplementedBy: { max: 1 },
      PresentedBy: { max: 1 },
      StoredBy: { max: 1 },
      ExposedBy: { max: 1 },
      Enforces: { max: 1 },
      EvidenceRefs: { max: 1 },
    },
  },
  RepresentedBy: { children: { CodeRef: { min: 1 } } },
  ImplementedBy: { children: { CodeRef: { min: 1 } } },
  PresentedBy: { children: { CodeRef: { min: 1 } } },
  StoredBy: { children: { CodeRef: { min: 1 } } },
  ExposedBy: { children: { CodeRef: { min: 1 } } },
  CodeRef: {
    attributes: ["repository", "language", "kind", "symbol", "path", "resolver", "confidence"],
    requiredAttributes: ["repository", "language", "kind", "symbol"],
  },
  Enforces: {
    children: { RuleRef: { min: 1 } },
  },
  RuleRef: {
    attributes: ["ref"],
    requiredAttributes: ["ref"],
  },
  EvidenceModule: {
    attributes: ["id"],
    requiredAttributes: ["id"],
    children: { Description: { max: 1 }, EvidenceItems: { min: 1, max: 1 } },
  },
  EvidenceItems: {
    children: { Evidence: { min: 1 } },
  },
  Evidence: {
    attributes: [
      "id",
      "repository",
      "revision",
      "path",
      "symbol",
      "startLine",
      "endLine",
      "grade",
      "resolver",
      "confidence",
      "observedAt",
      "sourceKind",
    ],
    requiredAttributes: ["id", "repository", "path", "grade", "confidence"],
    children: { Summary: { max: 1 }, ExcerptHash: { max: 1 } },
  },
  Summary: { text: "required" },
  ExcerptHash: { text: "required" },
  SchemaEvolutionModule: {
    attributes: ["id"],
    requiredAttributes: ["id"],
    children: {
      Description: { max: 1 },
      Aliases: { max: 1 },
      Migrations: { max: 1 },
      GenerationSnapshots: { max: 1 },
    },
    atLeastOneOf: ["Aliases", "Migrations", "GenerationSnapshots"],
  },
  Aliases: {
    children: { Alias: { min: 1 } },
  },
  Alias: {
    attributes: ["id", "kind", "from", "to", "ownerRef", "sinceVersion", "untilVersion", "status"],
    requiredAttributes: ["id", "kind", "from", "to", "sinceVersion"],
    children: { Description: { max: 1 }, EvidenceRefs: { max: 1 } },
  },
  Migrations: {
    children: { Migration: { min: 1 } },
  },
  Migration: {
    attributes: ["id", "fromVersion", "toVersion", "compatibility", "status"],
    requiredAttributes: ["id", "fromVersion", "toVersion", "compatibility"],
    children: {
      Description: { max: 1 },
      Changes: { min: 1, max: 1 },
      EvidenceRefs: { max: 1 },
    },
  },
  Changes: {
    children: { Add: {}, Remove: {}, Rename: {}, Alter: {} },
    minElementChildren: 1,
  },
  Add: {
    attributes: ["kind", "targetRef", "ownerRef"],
    requiredAttributes: ["kind", "targetRef"],
  },
  Remove: {
    attributes: ["kind", "targetRef", "ownerRef"],
    requiredAttributes: ["kind", "targetRef"],
  },
  Rename: {
    attributes: ["kind", "from", "to", "ownerRef"],
    requiredAttributes: ["kind", "from", "to"],
  },
  Alter: {
    attributes: ["kind", "targetRef", "aspect", "ownerRef"],
    requiredAttributes: ["kind", "targetRef", "aspect"],
  },
  GenerationSnapshots: {
    children: { GenerationSnapshot: { min: 1 } },
  },
  GenerationSnapshot: {
    attributes: ["id", "ontologyVersion", "generatedAt", "generator", "generatorVersion"],
    requiredAttributes: ["id", "ontologyVersion", "generatedAt", "generator"],
    children: {
      SourceRevisions: { min: 1, max: 1 },
      OutputDigest: { min: 1, max: 1 },
    },
  },
  SourceRevisions: {
    children: { SourceRevision: { min: 1 } },
  },
  SourceRevision: {
    attributes: ["repository", "revision"],
    requiredAttributes: ["repository", "revision"],
  },
  OutputDigest: {
    attributes: ["algorithm", "value"],
    requiredAttributes: ["algorithm", "value"],
  },
};

const generatedEvidenceElements = new Set([
  "Type",
  "Mixin",
  "Attribute",
  "ComputedAttribute",
  "Relation",
  "Property",
  "Rule",
  "StateMachine",
  "Transition",
  "ImplementationMapping",
  "Alias",
  "Migration",
]);

type XmlObject = Record<string, unknown>;
type Diagnostic = { file: string; message: string };
type DeclarationKind =
  | "ontology"
  | "module"
  | "type"
  | "mixin"
  | "relation"
  | "rule"
  | "lifecycle"
  | "transition"
  | "implementation"
  | "evidence"
  | "alias"
  | "migration"
  | "snapshot";

type Declaration = {
  id: string;
  kind: DeclarationKind;
  element: string;
  node: XmlObject;
  file: string;
};

type ElementNode = {
  element: string;
  node: XmlObject;
  file: string;
};

type LoadedDocument = {
  file: string;
  rootName: string;
  root: XmlObject;
};

type ValidationContext = {
  workspaceRoot: string;
  generated: boolean;
  hypothesisOnly: boolean;
  documents: Map<string, LoadedDocument>;
  elements: ElementNode[];
  declarations: Map<string, Declaration>;
  diagnostics: Diagnostic[];
  referencedTargets: Set<string>;
};

function isObject(value: unknown): value is XmlObject {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function asArray(value: unknown): unknown[] {
  if (value === undefined) return [];
  return Array.isArray(value) ? value : [value];
}

function childObjects(node: XmlObject, name: string): XmlObject[] {
  return asArray(node[name]).filter(isObject);
}

function directChildEntries(node: XmlObject): Array<[string, XmlObject]> {
  const entries: Array<[string, XmlObject]> = [];
  for (const [element, value] of Object.entries(node)) {
    if (element.startsWith("@_") || element === "#text") continue;
    for (const child of asArray(value)) {
      if (isObject(child)) entries.push([element, child]);
    }
  }
  return entries;
}

function walkElements(node: XmlObject, visit: (element: string, child: XmlObject) => void): void {
  for (const [element, child] of directChildEntries(node)) {
    visit(element, child);
    walkElements(child, visit);
  }
}

function attribute(node: XmlObject, name: string): string {
  const value = node[`@_${name}`];
  return typeof value === "string" ? value : "";
}

function hasAttribute(node: XmlObject, name: string): boolean {
  return Object.hasOwn(node, `@_${name}`);
}

function add(context: ValidationContext, file: string, message: string): void {
  context.diagnostics.push({ file, message });
}

function grammarSpec(element: string, parent: string | null, isRoot: boolean): ElementSpec | undefined {
  if (isRoot) return elementSpecs[`root:${element}`] ?? elementSpecs[element];
  if (parent === "Modules" && moduleRoots.has(element)) return elementSpecs["module-reference"];
  return elementSpecs[element];
}

function childOccurrenceCount(value: unknown): number {
  return Array.isArray(value) ? value.length : value === undefined ? 0 : 1;
}

function nodeText(value: unknown): string {
  if (typeof value === "string") return value;
  if (!isObject(value)) return "";
  const text = value["#text"];
  return typeof text === "string" ? text : "";
}

function validateDepaBoundary(
  context: ValidationContext,
  file: string,
  element: string,
  value: unknown,
): void {
  if (depaPattern.test(element)) {
    add(context, file, `Element name <${element}> contains forbidden depa_* text.`);
  }
  if (isObject(value)) {
    for (const [name, rawValue] of Object.entries(value)) {
      if (!name.startsWith("@_")) continue;
      const attributeName = name.slice(2);
      if (depaPattern.test(attributeName)) {
        add(context, file, `<${element}> attribute name @${attributeName} contains forbidden depa_* text.`);
      }
      if (typeof rawValue === "string" && depaPattern.test(rawValue)) {
        add(context, file, `<${element}> @${attributeName} contains forbidden depa_* text '${rawValue}'.`);
      }
    }
  }
  const text = nodeText(value);
  if (depaPattern.test(text)) {
    add(context, file, `<${element}> text contains forbidden depa_* text.`);
  }
}

function validateElementGrammar(
  context: ValidationContext,
  file: string,
  element: string,
  value: unknown,
  parent: string | null,
  isRoot = false,
): void {
  validateDepaBoundary(context, file, element, value);
  const spec = grammarSpec(element, parent, isRoot);
  if (!spec) {
    add(context, file, `Unknown ontology XML element <${element}>.`);
  }

  const allowedAttributes = new Set(spec?.attributes ?? []);
  const requiredAttributes = new Set(spec?.requiredAttributes ?? []);
  if (isObject(value)) {
    for (const [name, rawValue] of Object.entries(value)) {
      if (!name.startsWith("@_")) continue;
      const attributeName = name.slice(2);
      if (!allowedAttributes.has(attributeName)) {
        add(context, file, `<${element}> does not allow attribute @${attributeName}.`);
      }
      if (typeof rawValue !== "string") {
        add(context, file, `<${element}> @${attributeName} must be a string attribute.`);
      }
    }
  }
  for (const required of requiredAttributes) {
    const rawValue = isObject(value) ? value[`@_${required}`] : undefined;
    if (typeof rawValue !== "string" || !rawValue.trim()) {
      add(context, file, `<${element}> requires non-empty @${required}.`);
    }
  }

  const text = nodeText(value);
  if (spec?.text === "required" && !text.trim()) {
    add(context, file, `<${element}> requires non-empty plain text.`);
  }
  if (spec?.text !== "required" && text.trim()) {
    add(context, file, `<${element}> does not allow text content.`);
  }

  const childEntries = isObject(value)
    ? Object.entries(value).filter(([name]) => !name.startsWith("@_") && name !== "#text")
    : [];
  const childCount = childEntries.reduce(
    (count, [, child]) => count + childOccurrenceCount(child),
    0,
  );
  if (spec?.minElementChildren !== undefined && childCount < spec.minElementChildren) {
    add(context, file, `<${element}> requires at least ${spec.minElementChildren} child element(s).`);
  }
  if (spec?.maxElementChildren !== undefined && childCount > spec.maxElementChildren) {
    add(context, file, `<${element}> allows at most ${spec.maxElementChildren} child element(s).`);
  }

  const allowedChildren = spec?.children ?? {};
  for (const [childName, childValue] of childEntries) {
    const count = childOccurrenceCount(childValue);
    const rule = allowedChildren[childName];
    if (!rule) {
      add(context, file, `<${childName}> is not allowed inside <${element}>.`);
    } else if (rule.max !== undefined && count > rule.max) {
      add(context, file, `<${element}> allows at most ${rule.max} <${childName}> child element(s).`);
    }
    for (const child of asArray(childValue)) {
      validateElementGrammar(context, file, childName, child, element);
    }
  }

  for (const [childName, rule] of Object.entries(allowedChildren)) {
    const count = isObject(value) ? childOccurrenceCount(value[childName]) : 0;
    if (rule.min !== undefined && count < rule.min) {
      add(context, file, `<${element}> requires at least ${rule.min} <${childName}> child element(s).`);
    }
  }
  if (
    spec?.atLeastOneOf
    && !spec.atLeastOneOf.some((childName) => isObject(value) && childOccurrenceCount(value[childName]) > 0)
  ) {
    add(context, file, `<${element}> requires at least one of ${spec.atLeastOneOf.map((name) => `<${name}>`).join(", ")}.`);
  }
}

function parseArgs(): { rootFile: string; workspaceRoot: string; generated: boolean; hypothesisOnly: boolean } {
  const args = process.argv.slice(2);
  let rootArgument = "";
  let workspaceArgument = "";
  let generated = false;
  let hypothesisOnly = false;

  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index]!;
    if (argument === "--generated") {
      generated = true;
      continue;
    }
    if (argument === "--hypothesis-only") {
      hypothesisOnly = true;
      continue;
    }
    if (argument === "--workspace-root") {
      const value = args[index + 1];
      if (!value || value.startsWith("--")) {
        console.error("--workspace-root requires a path.");
        process.exit(2);
      }
      workspaceArgument = value;
      index += 1;
      continue;
    }
    if (argument.startsWith("--")) {
      console.error(`Unknown option '${argument}'.`);
      process.exit(2);
    }
    if (rootArgument) {
      console.error(`Unexpected positional argument '${argument}'.`);
      process.exit(2);
    }
    rootArgument = argument;
  }

  if (!rootArgument) {
    console.error("Usage: validate-ontology-xml.ts <ontology.xml> [--workspace-root <path>] [--generated] [--hypothesis-only]");
    process.exit(2);
  }
  const rootFile = resolve(rootArgument);
  const workspaceRoot = workspaceArgument ? resolve(workspaceArgument) : dirname(rootFile);
  if (!existsSync(workspaceRoot)) {
    console.error(`Workspace root does not exist: ${workspaceRoot}`);
    process.exit(2);
  }
  return { rootFile, workspaceRoot: realpathSync(workspaceRoot), generated, hypothesisOnly };
}

function pathIsWithin(root: string, target: string): boolean {
  const path = relative(root, target);
  return path === "" || (!path.startsWith(`..${sep}`) && path !== ".." && !isAbsolute(path));
}

function resolveWorkspaceFile(workspaceRoot: string, candidate: string): { file?: string; error?: string } {
  try {
    if (!existsSync(candidate)) {
      return { error: "Referenced XML file does not exist or is not a regular file." };
    }
    if (lstatSync(candidate).isSymbolicLink()) {
      return { error: `Referenced XML file traverses a symbolic link: ${candidate}` };
    }
    if (!lstatSync(candidate).isFile()) {
      return { error: "Referenced XML file does not exist or is not a regular file." };
    }
    // macOS commonly exposes /var as a compatibility alias of /private/var. Containment is
    // decided on canonical paths, while component-level link rejection still applies whenever
    // the candidate was addressed through the canonical workspace tree.
    const canonicalFile = realpathSync(candidate);
    if (!pathIsWithin(workspaceRoot, canonicalFile)) {
      return { error: `Referenced XML file escapes workspace root after realpath resolution.` };
    }
    if (pathIsWithin(workspaceRoot, candidate)) {
      const pathFromRoot = relative(workspaceRoot, candidate);
      let current = workspaceRoot;
      for (const segment of pathFromRoot.split(sep).filter(Boolean)) {
        current = resolve(current, segment);
        if (lstatSync(current).isSymbolicLink()) {
          return { error: `Referenced XML file traverses a symbolic link: ${current}` };
        }
      }
    }
    return { file: canonicalFile };
  } catch {
    return { error: "Referenced XML file could not be resolved safely." };
  }
}

function resolveHref(
  file: string,
  href: string,
  workspaceRoot: string,
): { target?: string; error?: string } {
  if (href.includes("\\")) return { error: `Unsupported href '${href}': backslashes are forbidden.` };

  let target: string;
  if (href.startsWith("vfs://./")) {
    target = resolve(dirname(file), href.slice("vfs://./".length));
  } else if (href.startsWith("vfs://@/")) {
    target = resolve(workspaceRoot, href.slice("vfs://@/".length));
  } else {
    return { error: `Unsupported href '${href}'. Use vfs://./ or vfs://@/.` };
  }

  const resolution = resolveWorkspaceFile(workspaceRoot, target);
  return resolution.file
    ? { target: resolution.file }
    : { error: `Module href '${href}' is unsafe: ${resolution.error}` };
}

function declarationKind(element: string, isRoot: boolean): DeclarationKind | null {
  if (isRoot) return element === "Ontology" ? "ontology" : "module";
  switch (element) {
    case "Type":
      return "type";
    case "Mixin":
      return "mixin";
    case "Relation":
      return "relation";
    case "Rule":
      return "rule";
    case "StateMachine":
      return "lifecycle";
    case "Transition":
      return "transition";
    case "ImplementationMapping":
      return "implementation";
    case "Evidence":
      return "evidence";
    case "Alias":
      return "alias";
    case "Migration":
      return "migration";
    case "GenerationSnapshot":
      return "snapshot";
    default:
      return null;
  }
}

function registerDeclaration(
  context: ValidationContext,
  file: string,
  element: string,
  node: XmlObject,
  kind: DeclarationKind,
): void {
  const id = attribute(node, "id");
  const namespaced = kind === "evidence" || kind === "snapshot";
  const pattern = namespaced ? namespacedIdPattern : fqnPattern;
  const shape = namespaced ? "stable namespaced ID" : "dot-separated PascalCase FQN";
  if (!pattern.test(id)) add(context, file, `${element} id '${id}' is not a ${shape}.`);
  if (!id) return;

  const existing = context.declarations.get(id);
  if (existing) {
    add(
      context,
      file,
      `Duplicate id '${id}' was already declared as ${existing.kind} in ${relative(process.cwd(), existing.file)}.`,
    );
    return;
  }
  context.declarations.set(id, { id, kind, element, node, file });
}

function predicateChildCount(node: XmlObject): number {
  let count = 0;
  for (const [element, value] of Object.entries(node)) {
    if (predicateElements.has(element)) count += asArray(value).length;
  }
  return count;
}

function validateEvidence(context: ValidationContext, file: string, node: XmlObject): void {
  const id = attribute(node, "id");
  for (const required of ["repository", "path", "grade", "confidence"]) {
    if (!attribute(node, required).trim()) add(context, file, `Evidence '${id}' requires @${required}.`);
  }

  const grade = attribute(node, "grade");
  if (!gradeValues.has(grade)) add(context, file, `Evidence '${id}' has invalid grade '${grade}'.`);

  const confidenceText = attribute(node, "confidence");
  const confidence = Number(confidenceText);
  if (
    !confidenceText
    || !Number.isFinite(confidence)
    || confidence < 0
    || confidence > 1
  ) {
    add(context, file, `Evidence '${id}' confidence must be in [0,1].`);
  }

  const path = attribute(node, "path");
  if (!path || isAbsolute(path) || path.split(/[\\/]/).includes("..") || path.includes("\\")) {
    add(context, file, `Evidence '${id}' path must be a forward-slash repository-relative path.`);
  }

  const startLine = attribute(node, "startLine");
  const endLine = attribute(node, "endLine");
  const isPositiveInteger = (value: string) => /^[1-9]\d*$/.test(value);
  if (startLine && !isPositiveInteger(startLine)) {
    add(context, file, `Evidence '${id}' startLine must be a positive integer.`);
  }
  if (endLine && !isPositiveInteger(endLine)) {
    add(context, file, `Evidence '${id}' endLine must be a positive integer.`);
  }
  if (
    startLine
    && endLine
    && isPositiveInteger(startLine)
    && isPositiveInteger(endLine)
    && Number(endLine) < Number(startLine)
  ) {
    add(context, file, `Evidence '${id}' endLine must be greater than or equal to startLine.`);
  }
}

function validateElementPhaseOne(
  context: ValidationContext,
  file: string,
  element: string,
  node: XmlObject,
  isRoot = false,
): void {
  context.elements.push({ element, node, file });

  const kind = declarationKind(element, isRoot);
  if (kind) registerDeclaration(context, file, element, node, kind);

  if (element === "Evidence") validateEvidence(context, file, node);
  if (hasAttribute(node, "status") && !statusValues.has(attribute(node, "status"))) {
    add(context, file, `<${element}> has invalid status '${attribute(node, "status")}'.`);
  }
  if (element === "Rule" && !ruleKinds.has(attribute(node, "kind"))) {
    add(context, file, `Rule '${attribute(node, "id")}' has invalid kind '${attribute(node, "kind")}'.`);
  }
  if (element === "PropertyCompare" && !["lt", "lte", "gt", "gte"].includes(attribute(node, "op"))) {
    add(context, file, `<PropertyCompare> has invalid op '${attribute(node, "op")}'.`);
  }
  if (element === "RelatedCount") {
    if (!["eq", "neq", "lt", "lte", "gt", "gte"].includes(attribute(node, "op"))) {
      add(context, file, `<RelatedCount> has invalid op '${attribute(node, "op")}'.`);
    }
    if (!/^\d+$/.test(attribute(node, "value"))) {
      add(context, file, `<RelatedCount> value must be a non-negative integer.`);
    }
  }
  if (element === "ExistsRelated" && !["out", "in"].includes(attribute(node, "direction"))) {
    add(context, file, `<ExistsRelated> has invalid direction '${attribute(node, "direction")}'.`);
  }
  if (["When", "Require", "Guard", "Not", "RelatedExists", "EveryRelated"].includes(element)) {
    if (predicateChildCount(node) !== 1) add(context, file, `<${element}> must contain exactly one predicate.`);
  }
  if (["All", "Any"].includes(element) && predicateChildCount(node) < 2) {
    add(context, file, `<${element}> must contain at least two predicates.`);
  }
  if (["Code", "Script", "Callback", "FunctionBody"].includes(element)) {
    add(context, file, `Host-language element <${element}> is forbidden.`);
  }
}

function loadDocument(
  context: ValidationContext,
  file: string,
  expectedRoot: string | null,
): void {
  const resolution = resolveWorkspaceFile(context.workspaceRoot, file);
  if (!resolution.file) {
    add(context, file, resolution.error!);
    return;
  }
  file = resolution.file;
  if (context.documents.has(file)) {
    const loaded = context.documents.get(file)!;
    if (expectedRoot && loaded.rootName !== expectedRoot) {
      add(context, file, `Reference expects ${expectedRoot}, found ${loaded.rootName}.`);
    }
    return;
  }
  const source = readFileSync(file, "utf8");
  let document: XmlObject;
  try {
    document = parseDslResource(source, {
      uri: `vfs://validation/${relative(context.workspaceRoot, file)}`,
    }).document;
  } catch (error) {
    add(context, file, error instanceof Error ? error.message : String(error));
    return;
  }

  const roots = Object.keys(document);
  if (roots.length !== 1 || !allowedRoots.has(roots[0]!)) {
    add(context, file, `Expected exactly one allowed root; found ${roots.join(", ") || "none"}.`);
    return;
  }

  const rootName = roots[0]!;
  const root = document[rootName];
  if (!isObject(root)) {
    add(context, file, `<${rootName}> root must be an element object.`);
    return;
  }
  if (expectedRoot && rootName !== expectedRoot) {
    add(context, file, `Reference expects ${expectedRoot}, found ${rootName}.`);
  }

  context.documents.set(file, { file, rootName, root });
  validateElementGrammar(context, file, rootName, root, null, true);
  validateElementPhaseOne(context, file, rootName, root, true);
  walkElements(root, (element, node) => validateElementPhaseOne(context, file, element, node));

  if (rootName !== "Ontology") return;

  const version = attribute(root, "version");
  if (!version.trim()) add(context, file, "Ontology requires a non-empty version.");
  else if (!versionPattern.test(version)) add(context, file, `Ontology version '${version}' is not a valid version token.`);

  const modulesContainers = childObjects(root, "Modules");
  if (modulesContainers.length !== 1) {
    add(context, file, "Ontology must contain exactly one <Modules> element.");
    return;
  }

  let schemaEvolutionCount = 0;
  for (const [element, moduleRef] of directChildEntries(modulesContainers[0]!)) {
    if (!moduleRoots.has(element)) {
      add(context, file, `Unsupported module reference <${element}>.`);
      continue;
    }
    if (element === "SchemaEvolutionModule") schemaEvolutionCount += 1;

    const href = attribute(moduleRef, "href");
    if (!href) {
      add(context, file, `<${element}> module reference requires @href.`);
      continue;
    }
    const resolution = resolveHref(file, href, context.workspaceRoot);
    if (!resolution.target) {
      add(context, file, resolution.error!);
      continue;
    }
    if (context.referencedTargets.has(resolution.target)) {
      add(context, file, `Duplicate module href '${href}'.`);
      continue;
    }
    context.referencedTargets.add(resolution.target);
    loadDocument(context, resolution.target, element);
  }
  if (schemaEvolutionCount > 1) {
    add(context, file, "Ontology may reference at most one SchemaEvolutionModule.");
  }
}

function expectReference(
  context: ValidationContext,
  file: string,
  owner: string,
  value: string,
  expectedKinds: DeclarationKind[],
): Declaration | null {
  if (!value) {
    add(context, file, `${owner} requires a semantic reference.`);
    return null;
  }
  const declaration = context.declarations.get(value);
  if (!declaration) {
    add(context, file, `${owner} references undeclared id '${value}'.`);
    return null;
  }
  if (!expectedKinds.includes(declaration.kind)) {
    add(
      context,
      file,
      `${owner} references '${value}' as ${expectedKinds.join("|")}, but it is declared as ${declaration.kind}.`,
    );
    return null;
  }
  return declaration;
}

function validateLocalName(
  context: ValidationContext,
  file: string,
  owner: string,
  name: string,
): void {
  if (!localNamePattern.test(name)) add(context, file, `${owner} name '${name}' must be lowerCamelCase.`);
}

function validateBoolean(
  context: ValidationContext,
  file: string,
  owner: string,
  value: string,
): void {
  if (value !== "true" && value !== "false") add(context, file, `${owner} must be 'true' or 'false', found '${value}'.`);
}

function validateValueType(
  context: ValidationContext,
  file: string,
  owner: string,
  value: string,
  primitiveOnly: boolean,
): void {
  if (primitiveTypes.has(value)) return;
  if (primitiveOnly) {
    add(context, file, `${owner} type '${value}' must be one of ${[...primitiveTypes].join("|")}.`);
    return;
  }
  if (!fqnPattern.test(value)) {
    add(context, file, `${owner} type '${value}' must be a primitive or Type FQN.`);
    return;
  }
  expectReference(context, file, `${owner} @type`, value, ["type"]);
}

function validateUniqueNamedChildren(
  context: ValidationContext,
  file: string,
  owner: string,
  children: XmlObject[],
): void {
  const seen = new Set<string>();
  for (const child of children) {
    const name = attribute(child, "name");
    validateLocalName(context, file, owner, name);
    if (seen.has(name)) add(context, file, `${owner} declares duplicate name '${name}'.`);
    seen.add(name);
  }
}

function detectCycles(
  context: ValidationContext,
  graph: Map<string, string[]>,
  label: string,
  ownerFiles: Map<string, string>,
): void {
  const state = new Map<string, "visiting" | "done">();
  const stack: string[] = [];
  const reported = new Set<string>();

  const visit = (node: string): void => {
    if (state.get(node) === "done") return;
    if (state.get(node) === "visiting") {
      const start = stack.indexOf(node);
      const cycle = [...stack.slice(start), node];
      const key = [...new Set(cycle)].sort().join("|");
      if (!reported.has(key)) {
        reported.add(key);
        add(context, ownerFiles.get(node) ?? process.cwd(), `${label} cycle detected: ${cycle.join(" -> ")}.`);
      }
      return;
    }
    state.set(node, "visiting");
    stack.push(node);
    for (const target of graph.get(node) ?? []) {
      if (graph.has(target)) visit(target);
    }
    stack.pop();
    state.set(node, "done");
  };

  for (const node of [...graph.keys()].sort()) visit(node);
}

function validateGenericReferences(context: ValidationContext): void {
  const referenceKinds = new Map<string, DeclarationKind[]>([
    ["EvidenceRef", ["evidence"]],
    ["ImplementationRef", ["implementation"]],
    ["RuntimeBinding", ["implementation"]],
    ["MixinRef", ["mixin"]],
    ["RuleRef", ["rule"]],
  ]);
  for (const { element, node, file } of context.elements) {
    const expectedKinds = referenceKinds.get(element);
    if (!expectedKinds) continue;
    expectReference(context, file, `<${element}> @ref`, attribute(node, "ref"), expectedKinds);
  }
}

function validateGeneratedEvidence(context: ValidationContext): void {
  if (!context.generated) return;

  for (const { element, node, file } of context.elements) {
    if (!generatedEvidenceElements.has(element)) continue;
    const identity = attribute(node, "id") || attribute(node, "name") || "<anonymous>";
    const owner = `${element} '${identity}'`;
    const evidenceRefs = childObjects(node, "EvidenceRefs")
      .flatMap((container) => childObjects(container, "EvidenceRef"));

    if (evidenceRefs.length === 0) {
      add(context, file, `${owner} requires direct EvidenceRefs in --generated mode.`);
    }
    if (attribute(node, "status") !== "hypothesis") continue;

    const hasInferredEvidence = evidenceRefs.some((evidenceRef) => {
      const declaration = context.declarations.get(attribute(evidenceRef, "ref"));
      return declaration?.kind === "evidence" && attribute(declaration.node, "grade") === "inferred";
    });
    if (!hasInferredEvidence) {
      add(
        context,
        file,
        `${owner} status='hypothesis' must resolve at least one direct EvidenceRef to grade='inferred' in --generated mode.`,
      );
    }
  }
}

function validateHypothesisOnly(context: ValidationContext): void {
  if (!context.hypothesisOnly) return;
  const statusBearingSemanticElements = new Set([
    "Type",
    "Mixin",
    "Relation",
    "Rule",
    "StateMachine",
    "Transition",
    "ImplementationMapping",
    "Alias",
    "Migration",
  ]);
  for (const { element, node, file } of context.elements) {
    if (!statusBearingSemanticElements.has(element)) continue;
    if (attribute(node, "status") !== "hypothesis") {
      const identity = attribute(node, "id") || "<anonymous>";
      add(context, file, `${element} '${identity}' must use status='hypothesis' in --hypothesis-only mode.`);
    }
  }
}

type TypeModel = {
  typeNodes: Map<string, ElementNode>;
  mixinNodes: Map<string, ElementNode>;
  parentByType: Map<string, string>;
  mixinsByOwner: Map<string, string[]>;
  attributesByOwner: Map<string, Map<string, string>>;
};

function validateTypes(context: ValidationContext): TypeModel {
  const typeNodes = new Map<string, ElementNode>();
  const mixinNodes = new Map<string, ElementNode>();
  const parentByType = new Map<string, string>();
  const mixinsByOwner = new Map<string, string[]>();
  const attributesByOwner = new Map<string, Map<string, string>>();
  const ownerFiles = new Map<string, string>();

  for (const entry of context.elements) {
    if (entry.element !== "Type" && entry.element !== "Mixin") continue;
    const id = attribute(entry.node, "id");
    if (entry.element === "Type") typeNodes.set(id, entry);
    else mixinNodes.set(id, entry);
    ownerFiles.set(id, entry.file);

    const mixinRefs = childObjects(entry.node, "Mixins")
      .flatMap((mixins) => childObjects(mixins, "MixinRef"))
      .map((ref) => attribute(ref, "ref"));
    const seenMixins = new Set<string>();
    for (const mixinRef of mixinRefs) {
      if (seenMixins.has(mixinRef)) add(context, entry.file, `${entry.element} '${id}' has duplicate MixinRef '${mixinRef}'.`);
      seenMixins.add(mixinRef);
      if (mixinRef === id) add(context, entry.file, `${entry.element} '${id}' cannot compose itself.`);
    }
    mixinsByOwner.set(id, mixinRefs);

    const attributes = childObjects(entry.node, "Attributes")
      .flatMap((container) => childObjects(container, "Attribute"));
    const computed = entry.element === "Type"
      ? childObjects(entry.node, "ComputedAttributes")
          .flatMap((container) => childObjects(container, "ComputedAttribute"))
      : [];
    validateUniqueNamedChildren(context, entry.file, `${entry.element} '${id}' attributes`, [...attributes, ...computed]);

    const localAttributes = new Map<string, string>();
    for (const attributeNode of attributes) {
      const name = attribute(attributeNode, "name");
      const type = attribute(attributeNode, "type");
      localAttributes.set(name, type);
      validateValueType(context, entry.file, `${entry.element} '${id}' Attribute '${name}'`, type, false);
      validateBoolean(context, entry.file, `${entry.element} '${id}' Attribute '${name}' @required`, attribute(attributeNode, "required"));
    }
    for (const computedNode of computed) {
      const name = attribute(computedNode, "name");
      const type = attribute(computedNode, "type");
      localAttributes.set(name, type);
      validateValueType(context, entry.file, `Type '${id}' ComputedAttribute '${name}'`, type, false);
      const implementationRefs = childObjects(computedNode, "ImplementationRef");
      if (implementationRefs.length !== 1) {
        add(context, entry.file, `Type '${id}' ComputedAttribute '${name}' must contain exactly one ImplementationRef.`);
      }
    }
    attributesByOwner.set(id, localAttributes);

    if (entry.element === "Type") {
      const parent = attribute(entry.node, "parent");
      if (parent) {
        expectReference(context, entry.file, `Type '${id}' @parent`, parent, ["type"]);
        if (parent === id) add(context, entry.file, `Type '${id}' cannot inherit from itself.`);
        parentByType.set(id, parent);
      }
      if (hasAttribute(entry.node, "abstract")) {
        validateBoolean(context, entry.file, `Type '${id}' @abstract`, attribute(entry.node, "abstract"));
      }
    }
  }

  const parentGraph = new Map<string, string[]>();
  for (const id of typeNodes.keys()) parentGraph.set(id, parentByType.has(id) ? [parentByType.get(id)!] : []);
  detectCycles(context, parentGraph, "Type inheritance", ownerFiles);

  const mixinGraph = new Map<string, string[]>();
  for (const id of mixinNodes.keys()) mixinGraph.set(id, mixinsByOwner.get(id) ?? []);
  detectCycles(context, mixinGraph, "Mixin composition", ownerFiles);

  return { typeNodes, mixinNodes, parentByType, mixinsByOwner, attributesByOwner };
}

function effectiveAttributes(model: TypeModel, ownerId: string): Set<string> {
  const memo = new Map<string, Set<string>>();
  const resolveOwner = (id: string, visiting: Set<string>): Set<string> => {
    if (memo.has(id)) return memo.get(id)!;
    if (visiting.has(id)) return new Set();
    visiting.add(id);

    const names = new Set(model.attributesByOwner.get(id)?.keys() ?? []);
    const parent = model.parentByType.get(id);
    if (parent) for (const name of resolveOwner(parent, visiting)) names.add(name);
    for (const mixin of model.mixinsByOwner.get(id) ?? []) {
      for (const name of resolveOwner(mixin, visiting)) names.add(name);
    }

    visiting.delete(id);
    memo.set(id, names);
    return names;
  };
  return resolveOwner(ownerId, new Set());
}

function validateRelations(context: ValidationContext): void {
  for (const { element, node, file } of context.elements) {
    if (element !== "Relation") continue;
    const id = attribute(node, "id");
    validateLocalName(context, file, `Relation '${id}'`, attribute(node, "name"));
    expectReference(context, file, `Relation '${id}' @from`, attribute(node, "from"), ["type"]);
    expectReference(context, file, `Relation '${id}' @to`, attribute(node, "to"), ["type"]);
    validateBoolean(context, file, `Relation '${id}' @directed`, attribute(node, "directed"));

    const min = attribute(node, "min");
    const max = attribute(node, "max");
    if (min && !/^\d+$/.test(min)) add(context, file, `Relation '${id}' @min must be a non-negative integer.`);
    if (max && max !== "*" && !/^\d+$/.test(max)) {
      add(context, file, `Relation '${id}' @max must be a non-negative integer or '*'.`);
    }
    if (min && max && /^\d+$/.test(min) && /^\d+$/.test(max) && Number(max) < Number(min)) {
      add(context, file, `Relation '${id}' cardinality requires max >= min.`);
    }

    const properties = childObjects(node, "Properties")
      .flatMap((container) => childObjects(container, "Property"));
    validateUniqueNamedChildren(context, file, `Relation '${id}' properties`, properties);
    for (const property of properties) {
      const name = attribute(property, "name");
      validateValueType(context, file, `Relation '${id}' Property '${name}'`, attribute(property, "type"), true);
      validateBoolean(context, file, `Relation '${id}' Property '${name}' @required`, attribute(property, "required"));
    }
  }
}

function validateRules(context: ValidationContext): void {
  for (const { element, node, file } of context.elements) {
    if (element === "Rule") {
      const id = attribute(node, "id");
      expectReference(context, file, `Rule '${id}' @scope`, attribute(node, "scope"), ["type"]);
      if (attribute(node, "kind") === "Custom" && childObjects(node, "RuntimeBinding").length !== 1) {
        add(context, file, `Custom Rule '${id}' must contain exactly one RuntimeBinding.`);
      }
    }
    if (element === "TypeIs") {
      expectReference(context, file, "<TypeIs> @type", attribute(node, "type"), ["type"]);
    }
    if (["RelatedExists", "EveryRelated", "RelatedCount", "ExistsRelated"].includes(element)) {
      expectReference(context, file, `<${element}> @relation`, attribute(node, "relation"), ["relation"]);
    }
    if (element === "ExistsRelated") {
      expectReference(context, file, "<ExistsRelated> @targetType", attribute(node, "targetType"), ["type"]);
    }
  }
}

function validateLifecycles(context: ValidationContext, typeModel: TypeModel): void {
  for (const { element, node, file } of context.elements) {
    if (element === "CreateRelation" || element === "RemoveRelation") {
      expectReference(context, file, `<${element}> @relation`, attribute(node, "relation"), ["relation"]);
      continue;
    }
    if (element !== "StateMachine") continue;

    const id = attribute(node, "id");
    const subject = attribute(node, "subject");
    expectReference(context, file, `StateMachine '${id}' @subject`, subject, ["type"]);

    const stateProperty = attribute(node, "stateProperty");
    validateLocalName(context, file, `StateMachine '${id}' @stateProperty`, stateProperty);
    if (subject && stateProperty && typeModel.typeNodes.has(subject)) {
      if (!effectiveAttributes(typeModel, subject).has(stateProperty)) {
        add(context, file, `StateMachine '${id}' stateProperty '${stateProperty}' is not declared by Type '${subject}' or its parents/mixins.`);
      }
    }

    const states = childObjects(node, "States").flatMap((container) => childObjects(container, "State"));
    const stateIds = new Set<string>();
    for (const state of states) {
      const stateId = attribute(state, "id");
      validateLocalName(context, file, `StateMachine '${id}' State`, stateId);
      if (stateIds.has(stateId)) add(context, file, `StateMachine '${id}' declares duplicate State '${stateId}'.`);
      stateIds.add(stateId);
      if (hasAttribute(state, "terminal")) {
        validateBoolean(context, file, `StateMachine '${id}' State '${stateId}' @terminal`, attribute(state, "terminal"));
      }
    }

    const initial = attribute(node, "initial");
    if (!stateIds.has(initial)) add(context, file, `StateMachine '${id}' initial state '${initial}' is not declared.`);

    const transitions = childObjects(node, "Transitions")
      .flatMap((container) => childObjects(container, "Transition"));
    const transitionKeys = new Set<string>();
    const reachableGraph = new Map<string, Set<string>>();
    for (const stateId of stateIds) reachableGraph.set(stateId, new Set());
    for (const transition of transitions) {
      const transitionId = attribute(transition, "id");
      const action = attribute(transition, "action");
      const from = attribute(transition, "from");
      const to = attribute(transition, "to");
      validateLocalName(context, file, `Transition '${transitionId}' @action`, action);
      if (!stateIds.has(from)) add(context, file, `Transition '${transitionId}' from state '${from}' is not declared by '${id}'.`);
      if (!stateIds.has(to)) add(context, file, `Transition '${transitionId}' to state '${to}' is not declared by '${id}'.`);
      if (stateIds.has(from) && stateIds.has(to)) reachableGraph.get(from)!.add(to);
      const key = `${action}|${from}|${to}`;
      if (transitionKeys.has(key)) {
        add(context, file, `StateMachine '${id}' has duplicate transition action/from/to '${key}'.`);
      }
      transitionKeys.add(key);
    }

    if (stateIds.has(initial)) {
      const reachable = new Set<string>([initial]);
      const pending = [initial];
      while (pending.length > 0) {
        const current = pending.shift()!;
        for (const target of reachableGraph.get(current) ?? []) {
          if (reachable.has(target)) continue;
          reachable.add(target);
          pending.push(target);
        }
      }
      for (const unreachable of [...stateIds].filter((stateId) => !reachable.has(stateId)).sort()) {
        add(
          context,
          file,
          `StateMachine '${id}' State '${unreachable}' is unreachable from initial state '${initial}'.`,
        );
      }
    }
  }
}

function validateMappings(context: ValidationContext): void {
  const targetKinds: Record<string, DeclarationKind[]> = {
    conceptRef: ["type", "mixin"],
    ruleRef: ["rule"],
    lifecycleRef: ["lifecycle"],
    relationRef: ["relation"],
  };

  for (const { element, node, file } of context.elements) {
    if (element !== "ImplementationMapping") continue;
    const id = attribute(node, "id");
    const targets = Object.keys(targetKinds).filter((name) => hasAttribute(node, name));
    if (targets.length !== 1) {
      add(context, file, `ImplementationMapping '${id}' must declare exactly one of conceptRef, ruleRef, lifecycleRef, or relationRef.`);
      continue;
    }
    const target = targets[0]!;
    expectReference(context, file, `ImplementationMapping '${id}' @${target}`, attribute(node, target), targetKinds[target]!);
  }
}

function parseComparableVersion(version: string): number[] | null {
  const monotonic = /^v(\d+)$/.exec(version);
  if (monotonic) return [0, Number(monotonic[1])];
  const semver = /^(\d+)\.(\d+)\.(\d+)$/.exec(version);
  if (semver) return [1, Number(semver[1]), Number(semver[2]), Number(semver[3])];
  return null;
}

function compareVersions(left: string, right: string): number | null {
  const a = parseComparableVersion(left);
  const b = parseComparableVersion(right);
  if (!a || !b || a[0] !== b[0]) return null;
  for (let index = 1; index < Math.max(a.length, b.length); index += 1) {
    const difference = (a[index] ?? 0) - (b[index] ?? 0);
    if (difference !== 0) return Math.sign(difference);
  }
  return 0;
}

function validateVersionToken(
  context: ValidationContext,
  file: string,
  owner: string,
  value: string,
): void {
  if (!versionPattern.test(value)) add(context, file, `${owner} version '${value}' is not a valid version token.`);
}

function ownerHasAttribute(typeModel: TypeModel, owner: string, name: string): boolean {
  return typeModel.attributesByOwner.get(owner)?.has(name) ?? false;
}

function aliasKey(kind: string, owner: string, name: string): string {
  return `${kind}|${kind === "attribute" ? owner : ""}|${name}`;
}

function resolveAliasTarget(
  aliasesBySource: Map<string, ElementNode>,
  kind: string,
  ownerRef: string,
  initialTarget: string,
): string | null {
  let target = initialTarget;
  const visited = new Set<string>();
  while (aliasesBySource.has(aliasKey(kind, ownerRef, target))) {
    const key = aliasKey(kind, ownerRef, target);
    if (visited.has(key)) return null;
    visited.add(key);
    target = attribute(aliasesBySource.get(key)!.node, "to");
  }
  return target;
}

function currentEvolutionTargetExists(
  context: ValidationContext,
  typeModel: TypeModel,
  file: string,
  ownerLabel: string,
  kind: string,
  ownerRef: string,
  target: string,
): boolean {
  if (kind === "attribute") {
    if (!localNamePattern.test(target)) {
      add(context, file, `${ownerLabel} attribute target '${target}' must be lowerCamelCase.`);
      return false;
    }
    const owner = expectReference(context, file, `${ownerLabel} @ownerRef`, ownerRef, ["type", "mixin"]);
    if (!owner) return false;
    if (!ownerHasAttribute(typeModel, ownerRef, target)) {
      add(context, file, `${ownerLabel} references undeclared Attribute '${ownerRef}.${target}'.`);
      return false;
    }
    return true;
  }

  const kindMap: Record<string, DeclarationKind[]> = {
    type: ["type"],
    mixin: ["mixin"],
    relation: ["relation"],
    rule: ["rule"],
    lifecycle: ["lifecycle"],
  };
  if (!fqnPattern.test(target)) {
    add(context, file, `${ownerLabel} target '${target}' must be a semantic FQN.`);
    return false;
  }
  return Boolean(expectReference(context, file, ownerLabel, target, kindMap[kind] ?? []));
}

function validateAliases(
  context: ValidationContext,
  typeModel: TypeModel,
  aliases: ElementNode[],
  ontologyVersion: string,
): Map<string, ElementNode> {
  const allowedKinds = new Set(["type", "mixin", "relation", "attribute"]);
  const aliasesBySource = new Map<string, ElementNode>();
  const aliasGraph = new Map<string, string[]>();
  const ownerFiles = new Map<string, string>();

  for (const alias of aliases) {
    const id = attribute(alias.node, "id");
    const kind = attribute(alias.node, "kind");
    const ownerRef = attribute(alias.node, "ownerRef");
    const from = attribute(alias.node, "from");
    const to = attribute(alias.node, "to");
    const sinceVersion = attribute(alias.node, "sinceVersion");
    const untilVersion = attribute(alias.node, "untilVersion");
    const ownerLabel = `Alias '${id}'`;

    if (!allowedKinds.has(kind)) add(context, alias.file, `${ownerLabel} has invalid kind '${kind}'.`);
    validateVersionToken(context, alias.file, `${ownerLabel} @sinceVersion`, sinceVersion);
    if (untilVersion) validateVersionToken(context, alias.file, `${ownerLabel} @untilVersion`, untilVersion);
    const sinceVsUntil = untilVersion ? compareVersions(sinceVersion, untilVersion) : null;
    if (sinceVsUntil !== null && sinceVsUntil >= 0) {
      add(context, alias.file, `${ownerLabel} requires sinceVersion earlier than untilVersion.`);
    }
    const sinceVsCurrent = compareVersions(sinceVersion, ontologyVersion);
    if (sinceVsCurrent !== null && sinceVsCurrent > 0) {
      add(context, alias.file, `${ownerLabel} sinceVersion '${sinceVersion}' is later than Ontology version '${ontologyVersion}'.`);
    }

    if (kind === "attribute") {
      expectReference(context, alias.file, `${ownerLabel} @ownerRef`, ownerRef, ["type", "mixin"]);
      if (!localNamePattern.test(from) || !localNamePattern.test(to)) {
        add(context, alias.file, `${ownerLabel} attribute @from and @to must be lowerCamelCase.`);
      }
    } else {
      if (hasAttribute(alias.node, "ownerRef")) add(context, alias.file, `${ownerLabel} forbids @ownerRef for kind '${kind}'.`);
      if (!fqnPattern.test(from) || !fqnPattern.test(to)) {
        add(context, alias.file, `${ownerLabel} @from and @to must be semantic FQNs.`);
      }
    }
    if (from === to) add(context, alias.file, `${ownerLabel} cannot alias '${from}' to itself.`);

    const sourceKey = aliasKey(kind, ownerRef, from);
    const targetKey = aliasKey(kind, ownerRef, to);
    if (aliasesBySource.has(sourceKey)) {
      const previous = aliasesBySource.get(sourceKey)!;
      add(
        context,
        alias.file,
        `${ownerLabel} duplicates alias source '${from}' already declared in ${relative(process.cwd(), previous.file)}.`,
      );
    } else {
      aliasesBySource.set(sourceKey, alias);
    }
    aliasGraph.set(sourceKey, [targetKey]);
    ownerFiles.set(sourceKey, alias.file);

    if (kind === "attribute") {
      if (ownerHasAttribute(typeModel, ownerRef, from)) {
        add(context, alias.file, `${ownerLabel} source Attribute '${ownerRef}.${from}' is still a current declaration.`);
      }
    } else {
      const current = context.declarations.get(from);
      if (current && current.kind === kind) {
        add(context, alias.file, `${ownerLabel} source '${from}' is still a current ${kind} declaration.`);
      }
    }
  }

  detectCycles(context, aliasGraph, "Alias", ownerFiles);

  for (const alias of aliasesBySource.values()) {
    const kind = attribute(alias.node, "kind");
    const ownerRef = attribute(alias.node, "ownerRef");
    const target = resolveAliasTarget(aliasesBySource, kind, ownerRef, attribute(alias.node, "to"));
    if (target === null) continue;
    currentEvolutionTargetExists(
      context,
      typeModel,
      alias.file,
      `Alias '${attribute(alias.node, "id")}' @to`,
      kind,
      ownerRef,
      target,
    );
  }

  return aliasesBySource;
}

function validateMigrationOperation(
  context: ValidationContext,
  typeModel: TypeModel,
  aliasesBySource: Map<string, ElementNode>,
  migration: ElementNode,
  operation: string,
  node: XmlObject,
): void {
  const migrationId = attribute(migration.node, "id");
  const ownerLabel = `Migration '${migrationId}' <${operation}>`;
  const kind = attribute(node, "kind");
  const ownerRef = attribute(node, "ownerRef");

  if (operation === "Add" || operation === "Remove") {
    const allowed = new Set(["type", "mixin", "attribute", "relation", "rule", "lifecycle"]);
    const target = attribute(node, "targetRef");
    if (!allowed.has(kind)) add(context, migration.file, `${ownerLabel} has invalid kind '${kind}'.`);
    if (kind === "attribute") {
      if (!localNamePattern.test(target)) add(context, migration.file, `${ownerLabel} attribute targetRef '${target}' must be lowerCamelCase.`);
      expectReference(context, migration.file, `${ownerLabel} @ownerRef`, ownerRef, ["type", "mixin"]);
      if (operation === "Add" && !ownerHasAttribute(typeModel, ownerRef, target)) {
        add(context, migration.file, `${ownerLabel} references undeclared current Attribute '${ownerRef}.${target}'.`);
      }
    } else {
      if (hasAttribute(node, "ownerRef")) add(context, migration.file, `${ownerLabel} forbids @ownerRef for kind '${kind}'.`);
      if (!fqnPattern.test(target)) add(context, migration.file, `${ownerLabel} targetRef '${target}' must be a semantic FQN.`);
      if (operation === "Add") {
        currentEvolutionTargetExists(context, typeModel, migration.file, `${ownerLabel} @targetRef`, kind, "", target);
      } else {
        const current = context.declarations.get(target);
        const expected: Record<string, DeclarationKind> = {
          type: "type",
          mixin: "mixin",
          relation: "relation",
          rule: "rule",
          lifecycle: "lifecycle",
        };
        if (current && expected[kind] && current.kind !== expected[kind]) {
          add(context, migration.file, `${ownerLabel} historical target '${target}' is currently declared as ${current.kind}, not ${kind}.`);
        }
      }
    }
    return;
  }

  if (operation === "Rename") {
    const allowed = new Set(["type", "mixin", "relation", "attribute"]);
    const from = attribute(node, "from");
    const to = attribute(node, "to");
    if (!allowed.has(kind)) add(context, migration.file, `${ownerLabel} has invalid kind '${kind}'.`);
    if (kind === "attribute") {
      expectReference(context, migration.file, `${ownerLabel} @ownerRef`, ownerRef, ["type", "mixin"]);
      if (!localNamePattern.test(from) || !localNamePattern.test(to)) {
        add(context, migration.file, `${ownerLabel} attribute @from and @to must be lowerCamelCase.`);
      }
    } else {
      if (hasAttribute(node, "ownerRef")) add(context, migration.file, `${ownerLabel} forbids @ownerRef for kind '${kind}'.`);
      if (!fqnPattern.test(from) || !fqnPattern.test(to)) {
        add(context, migration.file, `${ownerLabel} @from and @to must be semantic FQNs.`);
      }
    }
    if (from === to) add(context, migration.file, `${ownerLabel} cannot rename '${from}' to itself.`);
    currentEvolutionTargetExists(context, typeModel, migration.file, `${ownerLabel} @to`, kind, ownerRef, to);

    const alias = aliasesBySource.get(aliasKey(kind, ownerRef, from));
    const aliasTarget = alias
      ? resolveAliasTarget(aliasesBySource, kind, ownerRef, attribute(alias.node, "to"))
      : null;
    if (alias && aliasTarget !== null && aliasTarget !== to) {
      add(
        context,
        migration.file,
        `${ownerLabel} conflicts with Alias '${attribute(alias.node, "id")}' canonical target '${aliasTarget}'.`,
      );
    }
    return;
  }

  if (operation === "Alter") {
    const allowed = new Set(["attribute", "requiredness", "cardinality", "lifecycle", "rule"]);
    const target = attribute(node, "targetRef");
    if (!allowed.has(kind)) add(context, migration.file, `${ownerLabel} has invalid kind '${kind}'.`);
    if (!attribute(node, "aspect").trim()) add(context, migration.file, `${ownerLabel} requires non-empty @aspect.`);
    if (kind === "attribute" || kind === "requiredness") {
      expectReference(context, migration.file, `${ownerLabel} @ownerRef`, ownerRef, ["type", "mixin"]);
      if (!ownerHasAttribute(typeModel, ownerRef, target)) {
        add(context, migration.file, `${ownerLabel} references undeclared current Attribute '${ownerRef}.${target}'.`);
      }
    } else {
      if (hasAttribute(node, "ownerRef")) add(context, migration.file, `${ownerLabel} forbids @ownerRef for kind '${kind}'.`);
      const expected: Record<string, DeclarationKind[]> = {
        cardinality: ["relation"],
        lifecycle: ["lifecycle"],
        rule: ["rule"],
      };
      expectReference(context, migration.file, `${ownerLabel} @targetRef`, target, expected[kind] ?? []);
    }
  }
}

function validateMigrations(
  context: ValidationContext,
  typeModel: TypeModel,
  migrations: ElementNode[],
  aliasesBySource: Map<string, ElementNode>,
  ontologyVersion: string,
): void {
  const graph = new Map<string, string[]>();
  const ownerFiles = new Map<string, string>();

  for (const migration of migrations) {
    const id = attribute(migration.node, "id");
    const fromVersion = attribute(migration.node, "fromVersion");
    const toVersion = attribute(migration.node, "toVersion");
    const compatibility = attribute(migration.node, "compatibility");
    validateVersionToken(context, migration.file, `Migration '${id}' @fromVersion`, fromVersion);
    validateVersionToken(context, migration.file, `Migration '${id}' @toVersion`, toVersion);
    if (fromVersion === toVersion) add(context, migration.file, `Migration '${id}' must use different version endpoints.`);
    const order = compareVersions(fromVersion, toVersion);
    if (order !== null && order >= 0) {
      add(context, migration.file, `Migration '${id}' requires fromVersion earlier than toVersion.`);
    }
    if (toVersion !== ontologyVersion) {
      add(context, migration.file, `Migration '${id}' toVersion '${toVersion}' must equal Ontology version '${ontologyVersion}'.`);
    }
    if (!["backward-compatible", "breaking"].includes(compatibility)) {
      add(context, migration.file, `Migration '${id}' has invalid compatibility '${compatibility}'.`);
    }

    graph.set(fromVersion, [...(graph.get(fromVersion) ?? []), toVersion]);
    if (!graph.has(toVersion)) graph.set(toVersion, []);
    ownerFiles.set(fromVersion, migration.file);
    ownerFiles.set(toVersion, migration.file);

    const changesContainers = childObjects(migration.node, "Changes");
    if (changesContainers.length !== 1) {
      add(context, migration.file, `Migration '${id}' must contain exactly one <Changes>.`);
      continue;
    }
    const operations = directChildEntries(changesContainers[0]!);
    if (operations.length === 0) add(context, migration.file, `Migration '${id}' <Changes> must contain at least one operation.`);
    for (const [operation, node] of operations) {
      if (!["Add", "Remove", "Rename", "Alter"].includes(operation)) {
        add(context, migration.file, `Migration '${id}' contains unsupported operation <${operation}>.`);
        continue;
      }
      validateMigrationOperation(context, typeModel, aliasesBySource, migration, operation, node);
    }
  }

  detectCycles(context, graph, "Migration version graph", ownerFiles);
}

function isRfc3339(value: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/.test(value)) return false;
  return Number.isFinite(Date.parse(value));
}

function validateSnapshots(
  context: ValidationContext,
  snapshots: ElementNode[],
  ontologyVersion: string,
): void {
  for (const snapshot of snapshots) {
    const id = attribute(snapshot.node, "id");
    const version = attribute(snapshot.node, "ontologyVersion");
    if (version !== ontologyVersion) {
      add(context, snapshot.file, `GenerationSnapshot '${id}' ontologyVersion '${version}' must equal Ontology version '${ontologyVersion}'.`);
    }
    if (!isRfc3339(attribute(snapshot.node, "generatedAt"))) {
      add(context, snapshot.file, `GenerationSnapshot '${id}' generatedAt must be an RFC 3339 timestamp.`);
    }
    if (!attribute(snapshot.node, "generator").trim()) {
      add(context, snapshot.file, `GenerationSnapshot '${id}' requires non-empty @generator.`);
    }

    const sourceContainers = childObjects(snapshot.node, "SourceRevisions");
    if (sourceContainers.length !== 1) {
      add(context, snapshot.file, `GenerationSnapshot '${id}' must contain exactly one <SourceRevisions>.`);
    } else {
      const revisions = childObjects(sourceContainers[0]!, "SourceRevision");
      if (revisions.length === 0) add(context, snapshot.file, `GenerationSnapshot '${id}' requires at least one SourceRevision.`);
      const repositories = new Set<string>();
      for (const revision of revisions) {
        const repository = attribute(revision, "repository");
        if (!repository.trim()) add(context, snapshot.file, `GenerationSnapshot '${id}' SourceRevision requires @repository.`);
        if (!attribute(revision, "revision").trim()) {
          add(context, snapshot.file, `GenerationSnapshot '${id}' SourceRevision '${repository}' requires @revision.`);
        }
        if (repositories.has(repository)) {
          add(context, snapshot.file, `GenerationSnapshot '${id}' has duplicate SourceRevision repository '${repository}'.`);
        }
        repositories.add(repository);
      }
    }

    const digests = childObjects(snapshot.node, "OutputDigest");
    if (digests.length !== 1) {
      add(context, snapshot.file, `GenerationSnapshot '${id}' must contain exactly one <OutputDigest>.`);
    } else {
      const digest = digests[0]!;
      if (attribute(digest, "algorithm") !== "sha256") {
        add(context, snapshot.file, `GenerationSnapshot '${id}' OutputDigest algorithm must be 'sha256'.`);
      }
      if (!/^[a-f0-9]{64}$/.test(attribute(digest, "value"))) {
        add(context, snapshot.file, `GenerationSnapshot '${id}' OutputDigest value must be 64 lowercase hex characters.`);
      }
    }

    let evidenceRefCount = 0;
    walkElements(snapshot.node, (element) => {
      if (element === "EvidenceRef") evidenceRefCount += 1;
    });
    if (evidenceRefCount > 0) add(context, snapshot.file, `GenerationSnapshot '${id}' must not contain EvidenceRefs.`);
  }
}

function validateEvolution(context: ValidationContext, typeModel: TypeModel): void {
  const ontology = [...context.documents.values()].find((document) => document.rootName === "Ontology");
  if (!ontology) return;
  const ontologyVersion = attribute(ontology.root, "version");
  const evolutionModules = [...context.documents.values()]
    .filter((document) => document.rootName === "SchemaEvolutionModule");
  if (evolutionModules.length > 1) {
    for (const module of evolutionModules.slice(1)) {
      add(context, module.file, "Bundle contains more than one SchemaEvolutionModule.");
    }
  }
  for (const module of evolutionModules) {
    const containers = ["Aliases", "Migrations", "GenerationSnapshots"]
      .flatMap((name) => childObjects(module.root, name).map((node) => ({ name, node })));
    if (containers.length === 0) {
      add(context, module.file, "SchemaEvolutionModule requires at least one Aliases, Migrations, or GenerationSnapshots container.");
    }
    for (const container of containers) {
      if (directChildEntries(container.node).length === 0) {
        add(context, module.file, `<${container.name}> must contain at least one declaration.`);
      }
    }
  }

  const aliases = context.elements.filter((entry) => entry.element === "Alias");
  const migrations = context.elements.filter((entry) => entry.element === "Migration");
  const snapshots = context.elements.filter((entry) => entry.element === "GenerationSnapshot");
  const aliasesBySource = validateAliases(context, typeModel, aliases, ontologyVersion);
  validateMigrations(context, typeModel, migrations, aliasesBySource, ontologyVersion);
  validateSnapshots(context, snapshots, ontologyVersion);
}

function validateBundleSemantics(context: ValidationContext): void {
  validateGenericReferences(context);
  validateGeneratedEvidence(context);
  validateHypothesisOnly(context);
  const typeModel = validateTypes(context);
  validateRelations(context);
  validateRules(context);
  validateLifecycles(context, typeModel);
  validateMappings(context);
  validateEvolution(context, typeModel);
}

const { rootFile, workspaceRoot, generated, hypothesisOnly } = parseArgs();
const context: ValidationContext = {
  workspaceRoot,
  generated,
  hypothesisOnly,
  documents: new Map(),
  elements: [],
  declarations: new Map(),
  diagnostics: [],
  referencedTargets: new Set(),
};

loadDocument(context, rootFile, "Ontology");
validateBundleSemantics(context);

context.diagnostics.sort((left, right) => {
  const fileOrder = left.file.localeCompare(right.file);
  return fileOrder !== 0 ? fileOrder : left.message.localeCompare(right.message);
});

if (context.diagnostics.length > 0) {
  for (const diagnostic of context.diagnostics) {
    console.error(`${relative(process.cwd(), diagnostic.file)}: ${diagnostic.message}`);
  }
  process.exit(1);
}

const mode = generated ? " (generated strict mode)" : "";
console.log(`Ontology XML bundle is structurally and mechanically valid${mode}: ${relative(process.cwd(), rootFile)}`);
