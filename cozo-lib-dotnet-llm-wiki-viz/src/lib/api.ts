export const API_BASE = import.meta.env.VITE_API_BASE ?? "http://127.0.0.1:4176";

export interface GraphNode {
  id: string | number;
  label: string;
  group?: string;
  title?: string;
  description?: string;
  blockText?: string;
  fullId?: string;
  meta?: Record<string, unknown>;
}

export interface GraphEdge {
  from: string | number;
  to: string | number;
  label?: string;
  title?: string;
}

export interface ToolCallRequest {
  name: string;
  arguments: Record<string, unknown>;
}

export interface OverviewGraphOptions {
  categories?: string[];
  maxNodes?: number;
  maxEdges?: number;
}

async function json<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...(init?.headers ?? {}),
    },
  });
  if (!res.ok) {
    throw new Error(`${path}: ${res.status}`);
  }
  return res.json();
}

export function status() {
  return json<Record<string, unknown>>("/api/status");
}

export function tools() {
  return json<{ tools: any[] }>("/api/tools");
}

export function indexRepo(repoPath: string, options: Record<string, unknown> = {}) {
  return json<Record<string, unknown>>("/api/index", {
    method: "POST",
    body: JSON.stringify({ repoPath, ...options }),
  });
}

export function indexEmbeddings(options: Record<string, unknown> = {}) {
  return json<Record<string, unknown>>("/api/embeddings/index", {
    method: "POST",
    body: JSON.stringify(options),
  });
}

export function semanticSearch(query: string, limit = 10, sourceKinds: string[] = []) {
  return json<Record<string, unknown>>("/api/search/semantic", {
    method: "POST",
    body: JSON.stringify({ query, limit: String(limit), sourceKinds }),
  });
}

export function overviewGraph(options: OverviewGraphOptions = {}) {
  return json<Record<string, unknown>>("/api/graph/overview", {
    method: "POST",
    body: JSON.stringify({
      categories: options.categories ?? ["code", "docs"],
      maxNodes: String(options.maxNodes ?? 300),
      maxEdges: String(options.maxEdges ?? 600),
    }),
  });
}

export function symbolContext(symbolId: string) {
  return json<Record<string, unknown>>("/api/symbol/context", {
    method: "POST",
    body: JSON.stringify({ symbolId }),
  });
}

export function impactOfChange(symbolId: string) {
  return json<Record<string, unknown>>("/api/symbol/impact", {
    method: "POST",
    body: JSON.stringify({ symbolId }),
  });
}

export function buildWiki(outputDirectory?: string, writeFiles = false) {
  return json<Record<string, unknown>>("/api/wiki/build", {
    method: "POST",
    body: JSON.stringify({ outputDirectory, writeFiles }),
  });
}

export function queryNamed(name: string, parameters: Record<string, unknown>) {
  return json<Record<string, unknown>>("/api/query/named", {
    method: "POST",
    body: JSON.stringify({ name, parametersJson: JSON.stringify(parameters) }),
  });
}

export function callTool(req: ToolCallRequest) {
  return json<Record<string, unknown>>("/api/tools/call", {
    method: "POST",
    body: JSON.stringify(req),
  });
}

function truncateMiddle(value: string, max = 34): string {
  if (value.length <= max) return value;
  const keep = Math.max(4, Math.floor((max - 1) / 2));
  const tail = Math.max(4, max - keep - 1);
  return `${value.slice(0, keep)}…${value.slice(value.length - tail)}`;
}

function normalizeKnowledgeId(id: string): string {
  let value = id.trim();
  value = value.replace(/^embedding:[^:]+:/, "");
  value = value.replace(/^(doc|symbol|file):file:repo:[^:]+:/, "");
  value = value.replace(/^file:repo:[^:]+:/, "");
  value = value.replace(/^repo:[^:]+:/, "");
  return value;
}

function compactGraphLabel(raw: string, max = 34): string {
  const normalized = normalizeKnowledgeId(raw);
  const parts = normalized.split(":");
  const pathPart = parts[0] ?? normalized;
  const linePart = parts.length > 1 && /^\d+$/.test(parts[1]) ? parts[1] : undefined;
  const anchorPart = linePart ? parts.slice(2).join(":") : parts.slice(1).join(":");
  const pathSegments = pathPart.split("/").filter(Boolean);
  const fileName = pathSegments[pathSegments.length - 1] ?? pathPart;

  if (fileName) {
    const suffix = linePart ? `:${linePart}` : "";
    const anchor = anchorPart ? ` ${truncateMiddle(anchorPart, 16)}` : "";
    return truncateMiddle(`${fileName}${suffix}${anchor}`, max);
  }

  return truncateMiddle(normalized || raw, max);
}

function snippet(value: unknown, max = 240): string | undefined {
  if (typeof value !== "string") return undefined;
  const compact = value.replace(/\s+/g, " ").trim();
  return compact ? truncateMiddle(compact, max) : undefined;
}

function blockText(value: unknown): string | undefined {
  return typeof value === "string" && value.trim().length > 0 ? value : undefined;
}

function textTitle(parts: Array<string | undefined>): string {
  return parts.filter((part) => part && part.trim().length > 0).join("\n");
}

function isSemanticSearchResult(value: unknown): value is { query?: unknown; hits: Array<Record<string, unknown>>; model?: unknown; dimensions?: unknown } {
  if (!value || typeof value !== "object") return false;
  const record = value as Record<string, unknown>;
  return Array.isArray(record.hits) && record.hits.some((hit) => Boolean(hit && typeof hit === "object" && "sourceId" in hit));
}

function isGraphPayload(value: unknown): value is { nodes: Array<Record<string, unknown>>; edges: Array<Record<string, unknown>> } {
  if (!value || typeof value !== "object") return false;
  const record = value as Record<string, unknown>;
  return Array.isArray(record.nodes) && Array.isArray(record.edges);
}

function graphFromPayload(value: { nodes: Array<Record<string, unknown>>; edges: Array<Record<string, unknown>> }) {
  const nodes: GraphNode[] = value.nodes
    .map((node) => {
      const id = node.id;
      if (typeof id !== "string" && typeof id !== "number") return undefined;
      return {
        id,
        label: typeof node.label === "string" ? node.label : compactGraphLabel(String(id)),
        group: typeof node.group === "string" ? node.group : undefined,
        title: typeof node.title === "string" ? node.title : undefined,
        description: typeof node.description === "string" ? node.description : undefined,
        blockText: typeof node.blockText === "string" ? node.blockText : undefined,
        fullId: typeof node.fullId === "string" ? node.fullId : String(id),
        meta: node,
      };
    })
    .filter((node): node is GraphNode => Boolean(node));
  const edges: GraphEdge[] = value.edges
    .map((edge) => {
      const from = edge.from;
      const to = edge.to;
      if ((typeof from !== "string" && typeof from !== "number") || (typeof to !== "string" && typeof to !== "number")) return undefined;
      return {
        from,
        to,
        label: typeof edge.label === "string" ? edge.label : undefined,
        title: typeof edge.title === "string" ? edge.title : undefined,
      };
    })
    .filter((edge): edge is GraphEdge => Boolean(edge));
  return { nodes, edges };
}

function graphFromSemanticSearch(value: { query?: unknown; hits: Array<Record<string, unknown>>; model?: unknown; dimensions?: unknown }) {
  const queryText = typeof value.query === "string" && value.query.trim().length > 0 ? value.query.trim() : "semantic search";
  const rootId = `search:${queryText}`;
  const nodes = new Map<string, GraphNode>();
  const edges: GraphEdge[] = [];
  nodes.set(rootId, {
    id: rootId,
    label: `Search: ${truncateMiddle(queryText, 24)}`,
    group: "query",
    title: textTitle([
      `Search query: ${queryText}`,
      typeof value.model === "string" ? `Model: ${value.model}` : undefined,
      typeof value.dimensions === "number" ? `Dimensions: ${value.dimensions}` : undefined,
    ]),
    description: "Semantic search result root",
    fullId: rootId,
  });

  value.hits.slice(0, 50).forEach((hit, index) => {
    const sourceId = typeof hit.sourceId === "string" ? hit.sourceId : undefined;
    if (!sourceId) return;
    const sourceKind = typeof hit.sourceKind === "string" ? hit.sourceKind : "source";
    const distance = typeof hit.distance === "number" ? hit.distance : undefined;
    const description = snippet(hit.text);
    const label = compactGraphLabel(sourceId);

    nodes.set(sourceId, {
      id: sourceId,
      label,
      group: sourceKind,
      title: textTitle([
        `${sourceKind}: ${sourceId}`,
        distance === undefined ? undefined : `Distance: ${distance.toFixed(4)}`,
        description,
      ]),
      description,
      blockText: blockText(hit.text),
      fullId: sourceId,
      meta: hit,
    });
    edges.push({
      from: rootId,
      to: sourceId,
      label: "result",
      title: distance === undefined ? `Result #${index + 1}` : `Result #${index + 1}, distance ${distance.toFixed(4)}`,
    });
  });

  return { nodes: [...nodes.values()], edges };
}

export function graphFromAny(value: unknown): { nodes: GraphNode[]; edges: GraphEdge[] } {
  if (isGraphPayload(value)) {
    return graphFromPayload(value);
  }

  if (isSemanticSearchResult(value)) {
    return graphFromSemanticSearch(value);
  }

  const nodes = new Map<string, GraphNode>();
  const edges: GraphEdge[] = [];

  function addNode(id: string, label = id, group?: string, title?: string, description?: string, meta?: Record<string, unknown>) {
    if (!nodes.has(id)) {
      nodes.set(id, {
        id,
        label: compactGraphLabel(label),
        group,
        title: title ?? (group ? `${group}: ${id}` : id),
        description,
        blockText: blockText(meta?.text ?? meta?.content),
        fullId: id,
        meta,
      });
    }
  }

  function visit(node: unknown, parentId?: string, relation?: string) {
    if (Array.isArray(node)) {
      node.forEach((item, index) => visit(item, parentId, relation ?? String(index)));
      return;
    }
    if (!node || typeof node !== "object") {
      return;
    }

    const record = node as Record<string, unknown>;
    const idValue = record.id ?? record.itemId ?? record.sourceId ?? record.symbolId ?? record.targetId ?? record.fromId ?? record.toId ?? record.path;
    const nameValue = record.name ?? record.label ?? record.sourceId ?? record.itemId ?? record.kind ?? idValue;
    const id = typeof idValue === "string" || typeof idValue === "number" ? String(idValue) : undefined;
    if (id) {
      const group = typeof record.sourceKind === "string"
        ? record.sourceKind
        : typeof record.kind === "string"
          ? record.kind
          : undefined;
      addNode(id, String(nameValue ?? id), group, textTitle([
        group ? `${group}: ${id}` : id,
        snippet(record.text ?? record.content),
      ]), snippet(record.text ?? record.content), record);
      if (parentId && parentId !== id) {
        edges.push({ from: parentId, to: id, label: relation, title: relation });
      }
    }

    if (typeof record.itemId === "string" && typeof record.sourceId === "string" && record.itemId !== record.sourceId) {
      const sourceKind = typeof record.sourceKind === "string" ? record.sourceKind : "source";
      addNode(record.sourceId, record.sourceId, sourceKind, textTitle([
        `${sourceKind}: ${record.sourceId}`,
        snippet(record.text),
      ]), snippet(record.text), record);
    }

    if (typeof record.fromId === "string" && typeof record.toId === "string") {
      addNode(record.fromId);
      addNode(record.toId);
      edges.push({ from: record.fromId, to: record.toId, label: typeof record.kind === "string" ? record.kind : undefined, title: typeof record.kind === "string" ? record.kind : undefined });
    }

    for (const [key, child] of Object.entries(record)) {
      if (key === "text" || key === "content" || key === "vector") {
        continue;
      }
      visit(child, id ?? parentId, key);
    }
  }

  visit(value);
  return { nodes: [...nodes.values()].slice(0, 200), edges: edges.slice(0, 400) };
}
