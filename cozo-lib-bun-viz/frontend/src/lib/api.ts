/** Shared types for the ontology demo frontend */

export const API_BASE = import.meta.env.VITE_API_BASE ?? "http://127.0.0.1:4175";

function assertOk(res: Response, context: string) {
  if (!res.ok) throw new Error(`${context}: ${res.status}`);
}

export interface DemoTable {
  name: string;
  columns: string[];
  rows: Record<string, any>[];
}

export interface PermissionTable extends DemoTable {
  schema?: string;
}

export interface DemoQuery {
  id: string;
  label: string;
  description: string;
  meaning: string;
  dsl: string;
  defaultView: "schema" | "table" | "tree" | "graph";
}

export interface Demo {
  id: string;
  label: string;
  tables: DemoTable[];
  queries: DemoQuery[];
}

export interface DemosResponse {
  demos: Demo[];
}

export interface RunRequest {
  demoId: string;
  queryId: string;
  tables: DemoTable[];
}

export interface RunResponse {
  status: "ok" | "error";
  error?: string;
  table?: { columns: string[]; rows: Record<string, any>[] };
  tree?: TreeNode[];
  graph?: { nodes: GraphNode[]; edges: GraphEdge[] };
}

export interface TreeNode {
  id: string;
  label: string;
  children?: TreeNode[];
}

export interface GraphNode {
  id: string | number;
  label: string;
  group?: string;
}

export interface GraphEdge {
  from: string | number;
  to: string | number;
  label?: string;
}

export async function fetchDemos(): Promise<Demo[]> {
  const res = await fetch(`${API_BASE}/api/demos`);
  if (!res.ok) throw new Error(`Failed to fetch demos: ${res.status}`);
  const data: DemosResponse = await res.json();
  return data.demos;
}

export async function runQuery(req: RunRequest): Promise<RunResponse> {
  const res = await fetch(`${API_BASE}/api/run`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(req),
  });
  if (!res.ok) throw new Error(`Run failed: ${res.status}`);
  return res.json();
}

export interface PermissionParam {
  key: string;
  label: string;
  placeholder: string;
  required: boolean;
}

export interface PermissionQuery {
  id: string;
  label: string;
  meaning: string;
  cozo: string;
  params: PermissionParam[];
}

export interface PermissionModel {
  id: string;
  label: string;
  description: string;
  tables?: PermissionTable[];
  queries: PermissionQuery[];
}

export interface PermissionModelsResponse {
  models: PermissionModel[];
}

export interface PermissionRunRequest {
  modelId: string;
  queryId: string;
  params: Record<string, string>;
  tables?: PermissionTable[];
}

export interface PermissionRunSection {
  id: string;
  label: string;
  query: string;
  table: { columns: string[]; rows: Record<string, any>[] };
}

export interface PermissionRunResponse {
  status: "ok" | "error";
  error?: string;
  sections?: PermissionRunSection[];
}

export async function fetchPermissionModels(): Promise<PermissionModel[]> {
  const res = await fetch(`${API_BASE}/api/permission/models`);
  if (!res.ok) throw new Error(`Failed to fetch permission models: ${res.status}`);
  const data: PermissionModelsResponse = await res.json();
  return data.models;
}

export async function runPermissionQuery(req: PermissionRunRequest): Promise<PermissionRunResponse> {
  const res = await fetch(`${API_BASE}/api/permission/run`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(req),
  });
  if (!res.ok) throw new Error(`Permission run failed: ${res.status}`);
  return res.json();
}

// --- Schema/governance endpoints ---

export interface SchemaState {
  currentVersion: number;
  checksum: string;
  // Keep permissive in case server adds fields.
  [k: string]: any;
}

export interface SchemaVersionsResponse {
  versions: number[];
  [k: string]: any;
}

export interface SchemaDiffRequest {
  fromVersion: number;
  toVersion: number;
  [k: string]: any;
}

export interface SchemaDiffResponse {
  diff: any;
  [k: string]: any;
}

export interface SchemaApplyRequest {
  spec: any;
  [k: string]: any;
}

export interface SchemaApplyResponse {
  ok: boolean;
  state?: SchemaState;
  [k: string]: any;
}

export interface SchemaRollbackRequest {
  targetVersion: number;
  strict: boolean;
  [k: string]: any;
}

export interface SchemaRollbackResponse {
  ok: boolean;
  result: any;
  state?: SchemaState;
  [k: string]: any;
}

export interface GovernanceSeedResponse {
  ok: boolean;
  [k: string]: any;
}

export interface GovernanceAccessRequest {
  subjectId: string | number;
  action: string;
  resourceId: string | number;
  [k: string]: any;
}

export interface GovernanceAccessResponse {
  result: any;
  [k: string]: any;
}

export async function fetchSchemaState(): Promise<SchemaState> {
  const res = await fetch(`${API_BASE}/api/schema/state`);
  assertOk(res, "Failed to fetch schema state");
  return res.json();
}

export async function fetchSchemaVersions(): Promise<number[]> {
  const res = await fetch(`${API_BASE}/api/schema/versions`);
  assertOk(res, "Failed to fetch schema versions");
  const data: SchemaVersionsResponse = await res.json();
  return data.versions;
}

export async function diffSchemaVersions(fromVersion: number, toVersion: number): Promise<any> {
  const req: SchemaDiffRequest = { fromVersion, toVersion };
  const res = await fetch(`${API_BASE}/api/schema/diff`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(req),
  });
  assertOk(res, "Failed to diff schema versions");
  const data: SchemaDiffResponse = await res.json();
  return data.diff;
}

export async function applySchemaMigration(spec: any): Promise<SchemaApplyResponse> {
  const req: SchemaApplyRequest = { spec };
  const res = await fetch(`${API_BASE}/api/schema/apply`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(req),
  });
  assertOk(res, "Failed to apply schema migration");
  return res.json();
}

export async function rollbackSchema(targetVersion: number, strict: boolean): Promise<SchemaRollbackResponse> {
  const req: SchemaRollbackRequest = { targetVersion, strict };
  const res = await fetch(`${API_BASE}/api/schema/rollback`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(req),
  });
  assertOk(res, "Failed to rollback schema");
  return res.json();
}

export async function seedGovernanceDemo(): Promise<boolean> {
  const res = await fetch(`${API_BASE}/api/governance/seed`, { method: "POST" });
  assertOk(res, "Failed to seed governance demo");
  const data: GovernanceSeedResponse = await res.json();
  return data.ok;
}

export async function checkGovernanceAccess(
  subjectId: string | number,
  action: string,
  resourceId: string | number,
): Promise<any> {
  const req: GovernanceAccessRequest = { subjectId, action, resourceId };
  const res = await fetch(`${API_BASE}/api/governance/checkAccess`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(req),
  });
  assertOk(res, "Failed to check governance access");
  const data: GovernanceAccessResponse = await res.json();
  return data.result;
}

export async function explainGovernanceAccess(
  subjectId: string | number,
  action: string,
  resourceId: string | number,
): Promise<any> {
  const req: GovernanceAccessRequest = { subjectId, action, resourceId };
  const res = await fetch(`${API_BASE}/api/governance/explain`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(req),
  });
  assertOk(res, "Failed to explain governance access");
  const data: GovernanceAccessResponse = await res.json();
  return data.result;
}
