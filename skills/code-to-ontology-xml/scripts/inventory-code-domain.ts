#!/usr/bin/env bun

import { existsSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { basename, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

export type InventoryLanguage = "java" | "typescript";

export type RepositoryInventoryOptions = {
  key?: string;
  revision?: string;
  language: InventoryLanguage;
  root: string;
  include?: string[];
  exclude?: string[];
};

export type CreateInventoryOptions = {
  repositories: RepositoryInventoryOptions[];
};

export type InventoryObservation = {
  observationKey: string;
  repository: string;
  revision: string;
  language: InventoryLanguage;
  kind: string;
  symbol: string;
  path: string;
  line: number;
  resolver: string;
  confidence: number;
  details?: Record<string, string | number | boolean | string[]>;
};

type RepositoryStats = {
  filesScanned: number;
  filesExcluded: number;
  filesFailed: number;
  filesUnresolved: number;
};

type InventoryRepository = {
  key: string;
  revision: string;
  language: InventoryLanguage;
  include: string[];
  exclude: string[];
  stats: RepositoryStats;
};

export type DomainInventory = {
  schemaVersion: 1;
  repositories: InventoryRepository[];
  observations: InventoryObservation[];
  summary: {
    filesScanned: number;
    filesExcluded: number;
    filesFailed: number;
    filesUnresolved: number;
    observations: number;
    byKind: Record<string, number>;
    signals: {
      rules: number;
      lifecycles: number;
    };
  };
};

const ignoredDirectories = new Set([".git", "node_modules", "build", "target", "dist"]);
const ruleSignalKinds = new Set(["rule-signal"]);
const lifecycleSignalKinds = new Set(["lifecycle-signal"]);
const lifecycleStateTokens = new Set(["status", "state", "lifecycle", "freeze", "approval", "checkstate"]);

function normalizePath(path: string): string {
  return path.split(sep).join("/");
}

function stableUnique(values: readonly string[]): string[] {
  return [...new Set(values.filter(Boolean))].sort((left, right) => left.localeCompare(right));
}

function globToRegExp(glob: string): RegExp {
  let pattern = "^";
  for (let index = 0; index < glob.length; index += 1) {
    const character = glob[index]!;
    if (character === "*") {
      if (glob[index + 1] === "*") {
        index += 1;
        if (glob[index + 1] === "/") {
          index += 1;
          pattern += "(?:.*/)?";
        } else {
          pattern += ".*";
        }
      } else {
        pattern += "[^/]*";
      }
    } else if (character === "?") {
      pattern += "[^/]";
    } else {
      pattern += character.replace(/[\\^$+?.()|[\]{}]/g, "\\$&");
    }
  }
  return new RegExp(`${pattern}$`);
}

function matchesAny(path: string, patterns: readonly string[]): boolean {
  return patterns.some((pattern) => globToRegExp(pattern).test(path));
}

function sourceFileFor(language: InventoryLanguage, path: string): boolean {
  if (language === "java") return path.endsWith(".java") || /(?:^|\/)[^/]*Mapper\.xml$/.test(path);
  return (path.endsWith(".ts") || path.endsWith(".tsx")) && !path.endsWith(".d.ts");
}

function discoverFiles(root: string, language: InventoryLanguage, include: string[], exclude: string[]) {
  const included: string[] = [];
  let excluded = 0;

  function visit(directory: string): void {
    const entries = readdirSync(directory, { withFileTypes: true })
      .sort((left, right) => left.name.localeCompare(right.name));
    for (const entry of entries) {
      if (entry.isDirectory() && ignoredDirectories.has(entry.name)) continue;
      const absolutePath = resolve(directory, entry.name);
      if (entry.isDirectory()) {
        visit(absolutePath);
        continue;
      }
      if (!entry.isFile()) continue;
      const relativePath = normalizePath(relative(root, absolutePath));
      if (!sourceFileFor(language, relativePath)) continue;
      if ((include.length > 0 && !matchesAny(relativePath, include)) || matchesAny(relativePath, exclude)) {
        excluded += 1;
        continue;
      }
      included.push(relativePath);
    }
  }

  visit(root);
  return { files: included.sort((left, right) => left.localeCompare(right)), excluded };
}

function repositoryRevision(root: string): string {
  const result = spawnSync("git", ["-C", root, "rev-parse", "HEAD"], { encoding: "utf8" });
  const revision = result.status === 0 ? result.stdout.trim() : "";
  return revision || "working-tree";
}

function lineNumberAt(source: string, offset: number): number {
  let line = 1;
  for (let index = 0; index < offset; index += 1) {
    if (source.charCodeAt(index) === 10) line += 1;
  }
  return line;
}

function addObservation(
  observations: InventoryObservation[],
  base: Omit<InventoryObservation, "observationKey">,
): void {
  const observationKey = [
    base.repository,
    base.path,
    base.line,
    base.kind,
    base.symbol,
  ].join("|");
  observations.push({ observationKey, ...base });
}

function annotationRoute(annotation: string): { method: string; route: string } {
  const name = annotation.match(/@(Request|Get|Post|Put|Patch|Delete)Mapping\b/)?.[1] ?? "Request";
  const route = annotation.match(/["'`]([^"'`]*)["'`]/)?.[1] ?? "";
  return { method: name === "Request" ? "ANY" : name.toUpperCase(), route };
}

function joinRoute(base: string, route: string): string {
  const joined = `${base.replace(/\/+$/, "")}/${route.replace(/^\/+/, "")}`;
  return joined.replace(/\/+/g, "/") || "/";
}

function javaSymbol(packageName: string, owner: string, member?: string): string {
  const type = packageName ? `${packageName}.${owner}` : owner;
  return member ? `${type}#${member}` : type;
}

function containsLifecycleStateToken(value: string): boolean {
  const tokens = value
    .replace(/([a-z0-9])([A-Z])/g, "$1_$2")
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter(Boolean);
  return tokens.some((token, index) =>
    lifecycleStateTokens.has(token) || (token === "check" && tokens[index + 1] === "state")
  );
}

function extractJava(
  source: string,
  path: string,
  repository: string,
  revision: string,
): InventoryObservation[] {
  const observations: InventoryObservation[] = [];
  if (path.endsWith(".xml")) {
    const namespace = source.match(/<mapper\s+namespace=["']([^"']+)["']/)?.[1] ?? basename(path, ".xml");
    addObservation(observations, {
      repository, revision, language: "java", kind: "mybatis-mapper", symbol: namespace,
      path, line: lineNumberAt(source, source.search(/<mapper\b/)), resolver: "regex", confidence: 0.98,
    });
    for (const match of source.matchAll(/<(select|insert|update|delete)\b[^>]*\bid=["']([^"']+)["'][^>]*>([\s\S]*?)<\/\1\s*>/g)) {
      const operation = match[1]!;
      const symbol = `${namespace}#${match[2]!}`;
      const statementBody = match[3]!;
      const line = lineNumberAt(source, match.index ?? 0);
      addObservation(observations, {
        repository, revision, language: "java", kind: "mybatis-operation", symbol,
        path, line, resolver: "regex", confidence: 0.96,
        details: { operation },
      });
      if (operation !== "select") {
        addObservation(observations, {
          repository, revision, language: "java", kind: "mybatis-write", symbol,
          path, line, resolver: "regex", confidence: 0.9,
          details: { operation },
        });
        if (containsLifecycleStateToken(statementBody)) {
          addObservation(observations, {
            repository, revision, language: "java", kind: "lifecycle-signal", symbol,
            path, line, resolver: "regex", confidence: 0.88,
            details: { operation, signal: "state-column-write" },
          });
        }
      }
    }
    return observations;
  }

  const lines = source.split(/\r?\n/);
  const packageName = source.match(/^\s*package\s+([\w.]+)\s*;/m)?.[1] ?? "";
  const declarations: Array<{ line: number; kind: string; name: string }> = [];

  for (const match of source.matchAll(/\b(class|interface|record|enum)\s+([A-Za-z_$][\w$]*)/g)) {
    const kind = match[1]!;
    const name = match[2]!;
    const line = lineNumberAt(source, match.index ?? 0);
    declarations.push({ line, kind, name });
    addObservation(observations, {
      repository, revision, language: "java", kind: "java-type",
      symbol: javaSymbol(packageName, name), path, line, resolver: "regex", confidence: 0.96,
      details: { declaration: kind },
    });

    if (kind === "record") {
      const declaration = source.slice(match.index ?? 0).match(/^record\s+\w+\s*\(([^)]*)\)/)?.[1] ?? "";
      for (const component of declaration.split(",")) {
        const componentMatch = component.trim().match(/(?:[\w.<>?,[\]]+\s+)+([A-Za-z_$][\w$]*)$/);
        if (!componentMatch) continue;
        addObservation(observations, {
          repository, revision, language: "java", kind: "java-field",
          symbol: javaSymbol(packageName, name, componentMatch[1]), path, line,
          resolver: "regex", confidence: 0.94, details: { owner: name, recordComponent: true },
        });
      }
    }
  }

  function declarationAt(line: number) {
    return declarations.filter((candidate) => candidate.line <= line).at(-1);
  }

  for (let index = 0; index < lines.length; index += 1) {
    const text = lines[index]!;
    const line = index + 1;
    const owner = declarationAt(line);
    if (!owner) continue;

    const field = text.match(/^\s*(?:public|protected|private)\s+(?:(?:static|final|volatile|transient)\s+)*([\w.<>?, \[\]]+)\s+([A-Za-z_$][\w$]*)\s*(?:[=;])/);
    if (field && !text.includes("(")) {
      addObservation(observations, {
        repository, revision, language: "java", kind: "java-field",
        symbol: javaSymbol(packageName, owner.name, field[2]), path, line,
        resolver: "regex", confidence: 0.9, details: { declaredType: field[1]!.trim(), owner: owner.name },
      });
    }

    const method = text.match(/^\s*(?:public|protected|private|default|static|final|synchronized|\s)+[\w.<>?, \[\]]+\s+([A-Za-z_$][\w$]*)\s*\([^;]*\)\s*(?:throws\s+[^{]+)?\{/);
    if (method) {
      addObservation(observations, {
        repository, revision, language: "java", kind: "java-method",
        symbol: javaSymbol(packageName, owner.name, method[1]), path, line,
        resolver: "regex", confidence: 0.84, details: { owner: owner.name },
      });
    }
  }

  for (const declaration of declarations.filter((candidate) => candidate.kind === "enum")) {
    const startOffset = lines.slice(0, declaration.line - 1).join("\n").length;
    const body = source.slice(startOffset);
    const open = body.indexOf("{");
    const closeCandidates = [body.indexOf(";", open + 1), body.indexOf("}", open + 1)].filter((value) => value >= 0);
    const close = Math.min(...closeCandidates);
    if (open < 0 || !Number.isFinite(close)) continue;
    const values = body.slice(open + 1, close);
    for (const match of values.matchAll(/\b([A-Z][A-Z0-9_]*)\b/g)) {
      addObservation(observations, {
        repository, revision, language: "java", kind: "java-enum-value",
        symbol: javaSymbol(packageName, declaration.name, match[1]), path,
        line: declaration.line + lineNumberAt(values, match.index ?? 0) - 1,
        resolver: "regex", confidence: 0.96, details: { owner: declaration.name, value: match[1]! },
      });
    }
  }

  for (const declaration of declarations.filter((candidate) => candidate.kind === "class")) {
    const annotationWindow = lines.slice(Math.max(0, declaration.line - 9), declaration.line - 1).join("\n");
    const isController = /@(?:Rest)?Controller\b/.test(annotationWindow) || declaration.name.endsWith("Controller");
    const isService = /@Service\b/.test(annotationWindow) || declaration.name.endsWith("Service");
    const isMapper = /@Mapper\b/.test(annotationWindow) || declaration.name.endsWith("Mapper");
    if (isMapper) {
      addObservation(observations, {
        repository, revision, language: "java", kind: "mybatis-mapper",
        symbol: javaSymbol(packageName, declaration.name), path, line: declaration.line,
        resolver: "regex", confidence: 0.92,
      });
    }
    if (!isController && !isService) continue;

    const baseRouteAnnotation = annotationWindow.match(/@RequestMapping[^\n]*/)?.[0] ?? "";
    const baseRoute = annotationRoute(baseRouteAnnotation).route;
    let currentMethod = "";
    for (let index = declaration.line; index < lines.length; index += 1) {
      const text = lines[index]!;
      const methodMatch = text.match(/^\s*(?:public|protected|private)\s+[\w.<>?, \[\]]+\s+([A-Za-z_$][\w$]*)\s*\(/);
      if (methodMatch) currentMethod = methodMatch[1]!;

      const mapping = text.match(/@(Request|Get|Post|Put|Patch|Delete)Mapping\b[^\n]*/)?.[0];
      if (isController && mapping) {
        const nextMethod = lines.slice(index + 1, index + 8).join("\n")
          .match(/(?:public|protected|private)\s+[\w.<>?, \[\]]+\s+([A-Za-z_$][\w$]*)\s*\(/)?.[1] ?? "anonymous";
        const route = annotationRoute(mapping);
        const symbol = javaSymbol(packageName, declaration.name, nextMethod);
        addObservation(observations, {
          repository, revision, language: "java", kind: "spring-controller-route", symbol,
          path, line: index + 1, resolver: "regex", confidence: 0.94,
          details: { httpMethod: route.method, route: joinRoute(baseRoute, route.route) },
        });
        addObservation(observations, {
          repository, revision, language: "java", kind: "spring-api-operation", symbol,
          path, line: index + 1, resolver: "regex", confidence: 0.92,
          details: { httpMethod: route.method, route: joinRoute(baseRoute, route.route) },
        });
      }

      if (isService && currentMethod) {
        const write = text.match(/\b([A-Za-z_$][\w$]*)\.(set[A-Z]\w*|save|insert|update\w*|delete\w*)\s*\(/);
        if (write && containsLifecycleStateToken(write[2]!)) {
          const symbol = javaSymbol(packageName, declaration.name, currentMethod);
          addObservation(observations, {
            repository, revision, language: "java", kind: "service-state-write", symbol,
            path, line: index + 1, resolver: "regex", confidence: 0.86,
            details: { target: write[1]!, operation: write[2]! },
          });
          addObservation(observations, {
            repository, revision, language: "java", kind: "lifecycle-signal", symbol,
            path, line: index + 1, resolver: "regex", confidence: 0.84,
            details: { operation: write[2]!, signal: "state-method-write" },
          });
        }
        if (/\bif\s*\(/.test(text) && lines.slice(index, index + 5).some((lineText) => /\bthrow\s+new\b/.test(lineText))) {
          addObservation(observations, {
            repository, revision, language: "java", kind: "rule-signal",
            symbol: javaSymbol(packageName, declaration.name, currentMethod),
            path, line: index + 1, resolver: "regex", confidence: 0.82,
            details: { signal: "conditional-rejection" },
          });
        }
      }
    }
  }

  for (const declaration of declarations.filter((candidate) => candidate.kind === "interface" && candidate.name.endsWith("Mapper"))) {
    addObservation(observations, {
      repository, revision, language: "java", kind: "mybatis-mapper",
      symbol: javaSymbol(packageName, declaration.name), path, line: declaration.line,
      resolver: "regex", confidence: 0.9,
    });
  }

  return observations;
}

function extractTypeScript(
  source: string,
  path: string,
  repository: string,
  revision: string,
): InventoryObservation[] {
  const observations: InventoryObservation[] = [];
  const lines = source.split(/\r?\n/);
  const featureMatch = path.match(/(?:^|\/)(?:features?|modules?)\/([^/]+)/);
  if (featureMatch) {
    addObservation(observations, {
      repository, revision, language: "typescript", kind: "typescript-feature",
      symbol: featureMatch[1]!, path, line: 1, resolver: "path", confidence: 0.88,
      details: { feature: featureMatch[1]! },
    });
  }

  const declarations: Array<{ line: number; kind: string; name: string }> = [];
  for (const match of source.matchAll(/\b(class|interface|type|enum)\s+([A-Za-z_$][\w$]*)/g)) {
    const declaration = { line: lineNumberAt(source, match.index ?? 0), kind: match[1]!, name: match[2]! };
    declarations.push(declaration);
    addObservation(observations, {
      repository, revision, language: "typescript", kind: "typescript-type",
      symbol: `${path}#${declaration.name}`, path, line: declaration.line,
      resolver: "regex", confidence: 0.94, details: { declaration: declaration.kind },
    });
  }

  for (const declaration of declarations.filter((candidate) => candidate.kind === "enum")) {
    const sourceOffset = lines.slice(0, declaration.line - 1).join("\n").length;
    const body = source.slice(sourceOffset);
    const open = body.indexOf("{");
    const close = body.indexOf("}", open + 1);
    if (open < 0 || close < 0) continue;
    for (const match of body.slice(open + 1, close).matchAll(/\b([A-Za-z_$][\w$]*)\s*(?:=|,)/g)) {
      addObservation(observations, {
        repository, revision, language: "typescript", kind: "typescript-enum-value",
        symbol: `${path}#${declaration.name}.${match[1]!}`, path,
        line: declaration.line + lineNumberAt(body.slice(open + 1, close), match.index ?? 0) - 1,
        resolver: "regex", confidence: 0.92,
        details: { owner: declaration.name, value: match[1]! },
      });
    }
  }

  const apiClasses = declarations.filter((candidate) =>
    candidate.kind === "class" && (/(?:Api|API|Client|Service)$/.test(candidate.name) || /(?:^|\/)api(?:\/|\.|$)/i.test(path))
  );
  for (const apiClass of apiClasses) {
    addObservation(observations, {
      repository, revision, language: "typescript", kind: "typescript-api-class",
      symbol: `${path}#${apiClass.name}`, path, line: apiClass.line,
      resolver: "regex", confidence: 0.9,
    });
  }

  let currentApiClass = "";
  let currentApiMethod = "";
  for (let index = 0; index < lines.length; index += 1) {
    const text = lines[index]!;
    const classMatch = text.match(/\bclass\s+([A-Za-z_$][\w$]*)/);
    if (classMatch) {
      currentApiClass = apiClasses.some((candidate) => candidate.name === classMatch[1]) ? classMatch[1]! : "";
      currentApiMethod = "";
    }
    const methodMatch = text.match(/^\s*(?:public\s+|private\s+|protected\s+)?(?:async\s+)?([A-Za-z_$][\w$]*)\s*\([^)]*\)\s*(?::[^{]+)?\{/);
    if (currentApiClass && methodMatch && methodMatch[1] !== "constructor") {
      currentApiMethod = methodMatch[1]!;
      addObservation(observations, {
        repository, revision, language: "typescript", kind: "typescript-api-method",
        symbol: `${path}#${currentApiClass}.${currentApiMethod}`, path, line: index + 1,
        resolver: "regex", confidence: 0.88, details: { owner: currentApiClass },
      });
    }

    const routeCall = text.match(/\b(fetch|(?:axios|request|client)\.(get|post|put|patch|delete))\s*\(\s*(["'`])([^"'`]+)\3/);
    if (routeCall) {
      const operation = routeCall[2] ?? (routeCall[1] === "fetch" ? "ANY" : "");
      const symbol = currentApiClass && currentApiMethod
        ? `${path}#${currentApiClass}.${currentApiMethod}`
        : `${path}#route:${index + 1}`;
      addObservation(observations, {
        repository, revision, language: "typescript", kind: "typescript-api-route", symbol,
        path, line: index + 1, resolver: "regex", confidence: 0.9,
        details: { httpMethod: operation.toUpperCase(), route: routeCall[4]! },
      });
    }

    for (const call of text.matchAll(/\b([A-Za-z_$][\w$]*)\.([A-Za-z_$][\w$]*)\s*\(/g)) {
      if (["console", "Math", "JSON", "Object", "Array", "Promise", "request", "axios", "client"].includes(call[1]!)) continue;
      addObservation(observations, {
        repository, revision, language: "typescript", kind: "typescript-api-call-site",
        symbol: `${call[1]!}.${call[2]!}`, path, line: index + 1,
        resolver: "regex", confidence: 0.72, details: { receiver: call[1]!, method: call[2]! },
      });
    }
  }

  return observations;
}

function compareObservations(left: InventoryObservation, right: InventoryObservation): number {
  const leftKey = [
    left.repository, left.language, left.path, String(left.line).padStart(10, "0"),
    left.kind, left.symbol, JSON.stringify(left.details ?? {}),
  ].join("\0");
  const rightKey = [
    right.repository, right.language, right.path, String(right.line).padStart(10, "0"),
    right.kind, right.symbol, JSON.stringify(right.details ?? {}),
  ].join("\0");
  return leftKey.localeCompare(rightKey);
}

function normalizedDetails(
  details: InventoryObservation["details"],
): InventoryObservation["details"] {
  if (!details) return undefined;
  return Object.fromEntries(
    Object.entries(details)
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([key, value]) => [key, Array.isArray(value) ? [...value] : value]),
  );
}

function compareDuplicatePreference(left: InventoryObservation, right: InventoryObservation): number {
  if (left.confidence !== right.confidence) return right.confidence - left.confidence;
  const resolverOrder = left.resolver.localeCompare(right.resolver);
  if (resolverOrder !== 0) return resolverOrder;
  return JSON.stringify(left.details ?? {}).localeCompare(JSON.stringify(right.details ?? {}));
}

function deduplicateObservations(observations: readonly InventoryObservation[]): InventoryObservation[] {
  const selected = new Map<string, InventoryObservation>();
  for (const observation of observations) {
    const normalized = { ...observation, details: normalizedDetails(observation.details) };
    const current = selected.get(observation.observationKey);
    if (!current || compareDuplicatePreference(normalized, current) < 0) {
      selected.set(observation.observationKey, normalized);
    }
  }
  return [...selected.values()];
}

export async function createDomainInventory(options: CreateInventoryOptions): Promise<DomainInventory> {
  if (options.repositories.length === 0) throw new Error("At least one repository is required.");
  const repositories: InventoryRepository[] = [];
  const observations: InventoryObservation[] = [];

  for (const requested of [...options.repositories].sort((left, right) =>
    `${left.key ?? basename(left.root)}:${left.language}`.localeCompare(`${right.key ?? basename(right.root)}:${right.language}`)
  )) {
    const root = resolve(requested.root);
    if (!existsSync(root) || !statSync(root).isDirectory()) {
      throw new Error(`Repository root does not exist or is not a directory: ${requested.root}`);
    }
    const key = requested.key?.trim() || basename(root);
    const revision = requested.revision?.trim() || repositoryRevision(root);
    const include = stableUnique(requested.include?.length
      ? requested.include
      : requested.language === "java"
        ? ["**/*.java", "**/*Mapper.xml"]
        : ["**/*.ts", "**/*.tsx"]);
    const exclude = stableUnique(requested.exclude ?? []);
    const discovered = discoverFiles(root, requested.language, include, exclude);
    const stats: RepositoryStats = {
      filesScanned: 0,
      filesExcluded: discovered.excluded,
      filesFailed: 0,
      filesUnresolved: 0,
    };

    for (const path of discovered.files) {
      try {
        const source = readFileSync(resolve(root, path), "utf8");
        const extracted = requested.language === "java"
          ? extractJava(source, path, key, revision)
          : extractTypeScript(source, path, key, revision);
        observations.push(...extracted);
        stats.filesScanned += 1;
        if (extracted.length === 0) stats.filesUnresolved += 1;
      } catch {
        stats.filesFailed += 1;
      }
    }
    repositories.push({ key, revision, language: requested.language, include, exclude, stats });
  }

  const sortedObservations = deduplicateObservations(observations).sort(compareObservations);
  const byKind = Object.fromEntries(
    [...new Set(sortedObservations.map((observation) => observation.kind))]
      .sort((left, right) => left.localeCompare(right))
      .map((kind) => [kind, sortedObservations.filter((observation) => observation.kind === kind).length]),
  );
  const sum = (field: keyof RepositoryStats) =>
    repositories.reduce((total, repository) => total + repository.stats[field], 0);

  return {
    schemaVersion: 1,
    repositories,
    observations: sortedObservations,
    summary: {
      filesScanned: sum("filesScanned"),
      filesExcluded: sum("filesExcluded"),
      filesFailed: sum("filesFailed"),
      filesUnresolved: sum("filesUnresolved"),
      observations: sortedObservations.length,
      byKind,
      signals: {
        rules: sortedObservations.filter((observation) => ruleSignalKinds.has(observation.kind)).length,
        lifecycles: sortedObservations.filter((observation) => lifecycleSignalKinds.has(observation.kind)).length,
      },
    },
  };
}

export function serializeInventory(inventory: DomainInventory): string {
  return `${JSON.stringify(inventory, null, 2)}\n`;
}

type ParsedArguments = {
  javaRepo: string;
  typescriptRepo: string;
  output: string;
  javaKey?: string;
  typescriptKey?: string;
  javaRevision?: string;
  typescriptRevision?: string;
  javaInclude: string[];
  typescriptInclude: string[];
  javaExclude: string[];
  typescriptExclude: string[];
};

function parseArguments(args: string[]): ParsedArguments {
  const values = new Map<string, string[]>();
  for (let index = 0; index < args.length; index += 1) {
    const flag = args[index]!;
    if (!flag.startsWith("--") || !args[index + 1] || args[index + 1]!.startsWith("--")) {
      throw new Error(`Expected a value after ${flag}.`);
    }
    const value = args[index + 1]!;
    values.set(flag, [...(values.get(flag) ?? []), value]);
    index += 1;
  }
  const required = (flag: string) => {
    const value = values.get(flag)?.at(-1);
    if (!value) throw new Error(`Missing required argument ${flag}.`);
    return value;
  };
  const repeated = (...flags: string[]) =>
    flags.flatMap((flag) => values.get(flag) ?? []).flatMap((value) => value.split(",")).filter(Boolean);
  const optional = (...flags: string[]) => flags.flatMap((flag) => values.get(flag) ?? []).at(-1);
  return {
    javaRepo: required("--java-repo"),
    typescriptRepo: required("--typescript-repo"),
    output: required("--out"),
    javaKey: optional("--java-key", "--java-repo-key"),
    typescriptKey: optional("--typescript-key", "--typescript-repo-key"),
    javaRevision: optional("--java-revision"),
    typescriptRevision: optional("--typescript-revision"),
    javaInclude: repeated("--include", "--java-include"),
    typescriptInclude: repeated("--include", "--typescript-include"),
    javaExclude: repeated("--exclude", "--java-exclude"),
    typescriptExclude: repeated("--exclude", "--typescript-exclude"),
  };
}

async function main(): Promise<void> {
  try {
    const args = parseArguments(process.argv.slice(2));
    const inventory = await createDomainInventory({
      repositories: [
        {
          root: args.javaRepo,
          key: args.javaKey,
          revision: args.javaRevision,
          language: "java",
          include: args.javaInclude.length > 0 ? args.javaInclude : undefined,
          exclude: args.javaExclude,
        },
        {
          root: args.typescriptRepo,
          key: args.typescriptKey,
          revision: args.typescriptRevision,
          language: "typescript",
          include: args.typescriptInclude.length > 0 ? args.typescriptInclude : undefined,
          exclude: args.typescriptExclude,
        },
      ],
    });
    writeFileSync(resolve(args.output), serializeInventory(inventory));
    console.log(`Wrote deterministic code inventory: ${args.output}`);
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    console.error(
      "Usage: inventory-code-domain.ts --java-repo <path> --typescript-repo <path> --out <json> " +
      "[--java-key <key>] [--typescript-key <key>] [--java-revision <revision>] " +
      "[--typescript-revision <revision>] [--java-include <glob>] [--typescript-include <glob>] " +
      "[--java-exclude <glob>] [--typescript-exclude <glob>]",
    );
    process.exitCode = 2;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
