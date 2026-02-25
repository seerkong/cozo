export type OmValueType = 'String' | 'Number' | 'Bool' | 'Json' | 'Validity';

export interface DefineTypeOptions {
  parentType?: string | null;
  mixins?: string[];
}

export interface TypeHierarchyNode {
  name: string;
  description: string;
  parentType: string | null;
  mixins: string[];
  children: string[];
}

export interface TypeHierarchy {
  types: Record<string, TypeHierarchyNode>;
  roots: string[];
}

export interface FindByTypeOptions {
  exact?: boolean;
}

export interface AttributeDefinition {
  valueType: OmValueType;
  required: boolean;
  description?: string;
}

export interface CozoRunner {
  run(script: string, params?: Record<string, any>): Promise<any>;
}

export interface CozoDbLike extends CozoRunner {
  multiTransact(write?: boolean): CozoRunner & { commit(): void; abort(): void };
}

export interface EntityInput {
  id: string;
  typeName: string;
  label: string;
}

export interface PropertyInput {
  entityId: string;
  attrName: string;
  value: any;
}

export interface EdgeInput {
  fromId: string;
  relName: string;
  toId: string;
  props?: Record<string, any>;
}

export interface BatchInput {
  entities?: EntityInput[];
  properties?: PropertyInput[];
  edges?: EdgeInput[];
}

export interface EntityView {
  id: string;
  typeName: string;
  label: string;
  properties: Record<string, any>;
  outgoing: Array<{
    relName: string;
    toId: string;
    toType: string;
    toLabel: string;
  }>;
}

export interface ValidationResult {
  valid: boolean;
  errors: string[];
}

export interface ConstraintResult extends ValidationResult {}

export interface WriteOptions {
  skipConstraints?: boolean;
  validTime?: string;
}

export interface MutationSpec {
  mutation: string;
  params?: Record<string, any>;
}

export interface MutationContext {
  runner: CozoRunner;
  entityId: string;
  typeName: string;
  getProperty(attrName: string, options?: { asOf?: string }): Promise<any | undefined>;
  setProperty(attrName: string, value: any, options?: WriteOptions): Promise<void>;
  linkEntities(relName: string, toId: string, props?: Record<string, any>, options?: WriteOptions): Promise<void>;
  getNeighbors(relName?: string, direction?: 'outgoing' | 'incoming' | 'both'): Promise<NeighborResult>;
}

export interface ActionContext extends MutationContext {
  params: Record<string, any>;
  actionOwnerType?: string;
  callParentAction(actionName: string, params?: Record<string, any>): Promise<MutationSpec[]>;
}

export type MutationExecutor = (ctx: MutationContext, params: Record<string, any>) => Promise<void> | void;
export type ActionHandler = (ctx: ActionContext, params: Record<string, any>) => Promise<MutationSpec[]> | MutationSpec[];
export type InterceptorHandler = (ctx: ActionContext) => Promise<void> | void;

export interface ConstraintContext {
  runner: CozoRunner;
  entityId: string;
  typeName: string;
  getProperty(attrName: string): Promise<any | undefined>;
  getNeighbors(relName?: string, direction?: 'outgoing' | 'incoming' | 'both'): Promise<NeighborResult>;
}

export interface ConstraintDefinition {
  scope?: 'conditional' | 'cross-entity' | 'computed-dep' | string;
  message?: string;
  when(ctx: ConstraintContext): Promise<boolean> | boolean;
  then(ctx: ConstraintContext): Promise<boolean> | boolean;
}

export interface ComputedContext {
  runner: CozoRunner;
  entityId: string;
  typeName: string;
  asOf?: string;
  getProperty(attrName: string, options?: { asOf?: string }): Promise<any | undefined>;
  getNeighbors(relName?: string, direction?: 'outgoing' | 'incoming' | 'both'): Promise<NeighborResult>;
}

export type ComputedFn = (ctx: ComputedContext) => Promise<any> | any;

export interface NeighborResult {
  outgoing: Array<{ relName: string; entityId: string; typeName: string; label: string }>;
  incoming: Array<{ relName: string; entityId: string; typeName: string; label: string }>;
}

export interface TemplateResult<TInput = Record<string, any>, TData = Record<string, any>, TStats = Record<string, any>> {
  template: string;
  version: 'v1';
  input: TInput;
  data: TData;
  stats: TStats;
  warnings: string[];
}

export interface GraphVisualNode {
  id: string;
  label: string;
  kind: string;
  group: string;
  depth: number;
  metrics: Record<string, any>;
  flags: { isRoot: boolean };
}

export interface GraphVisualEdge {
  id: string;
  source: string;
  target: string;
  kind: string;
  label: string;
  direction: 'outgoing' | 'incoming';
  weight: number;
  flags: Record<string, any>;
}

export interface GraphVisualAdjacencyEdge {
  toId: string;
  relName: string;
  direction: 'outgoing' | 'incoming';
}

export interface GraphVisual {
  nodes: GraphVisualNode[];
  edges: GraphVisualEdge[];
  nodeMap: Record<string, GraphVisualNode>;
  adjacency: Record<string, GraphVisualAdjacencyEdge[]>;
}

export interface TreeVisual {
  rootId: string;
  childrenById: Record<string, GraphVisualAdjacencyEdge[]>;
  crossEdges: any[];
}

export interface RankingVisualEntry {
  rank: number;
  id: string;
  label: string;
  score: number;
  factors: { baseScore: number; degree: number; degreeWeight: number };
}

export interface RankingVisual {
  ranking: RankingVisualEntry[];
  series: { labels: string[]; values: number[] };
}

export interface SchemaState {
  currentVersion: number;
  checksum: string | null;
}

export interface SchemaSnapshot {
  version: number;
  createdAt: string;
  schema: any;
  checksum?: string | null;
}

export interface SchemaDiff {
  fromVersion: number;
  toVersion: number;
  added?: any;
  removed?: any;
  changed?: any;
}

export interface SchemaVersion {
  version: number;
  createdAt: string;
  label?: string | null;
  description?: string | null;
  parentVersion?: number | null;
  checksum?: string | null;
}

export type SchemaMigrationStep = any;

export interface SchemaMigrationSpec {
  migrationId: string;
  fromVersion: number;
  toVersion: number;
  label?: string;
  description?: string;
  strict?: boolean;
  steps: SchemaMigrationStep[];
}

export interface RollbackSchemaOptions {
  strict?: boolean;
}

export interface RollbackSchemaDiagnostic {
  entityId: string;
  typeName?: string;
  errors: string[];
}

export interface RollbackSchemaResult {
  ok: boolean;
  strict: boolean;
  targetVersion: number;
  fromVersion: number;
  diagnostics: RollbackSchemaDiagnostic[];
}

export interface CheckAccessInput {
  subjectId: string;
  action: string;
  resourceId: string;
  asOf?: string;
}

export interface CheckAccessHop {
  fromId: string;
  relName: string;
  toId: string;
}

export interface CheckAccessMatchedPolicy {
  policyId: string;
  effect: 'allow' | 'deny' | string;
  action: string;
  resourceType?: string;
  description?: string;
  witness?: CheckAccessHop[] | null;
}

export interface CheckAccessResult {
  allow: boolean;
  matchedPolicies: CheckAccessMatchedPolicy[];
  explanation: any;
  fieldVisibility?: Record<string, 'visible' | 'hidden'>;
}

export function initSchema(runner: CozoRunner): Promise<void>;
export function createSchema(runner: CozoRunner): Promise<void>;

export function seedPermissionMetadata(runner: CozoRunner, data?: any): Promise<void>;

export function checkAccess(runner: CozoRunner, input: CheckAccessInput): Promise<CheckAccessResult>;

export function getSchemaState(runner: CozoRunner): Promise<SchemaState>;
export function listSchemaVersions(runner: CozoRunner): Promise<SchemaVersion[]>;
export function applySchemaMigration(runner: CozoRunner, spec: SchemaMigrationSpec): Promise<void>;
export function rollbackSchema(
  runner: CozoRunner,
  targetVersion: number,
  options?: RollbackSchemaOptions
): Promise<RollbackSchemaResult>;

export function readSchemaSnapshot(runner: CozoRunner, version: number): Promise<SchemaSnapshot | null>;
export function writeSchemaSnapshot(runner: CozoRunner, version: number): Promise<SchemaSnapshot>;
export function diffSchemaVersions(runner: CozoRunner, fromVersion: number, toVersion: number): Promise<SchemaDiff>;

export function defineType(runner: CozoRunner, name: string, description: string, options?: DefineTypeOptions): Promise<void>;
export function defineMixin(runner: CozoRunner, name: string, description: string): Promise<void>;
export function getAncestors(runner: CozoRunner, typeName: string): Promise<string[]>;
export function getDescendants(runner: CozoRunner, typeName: string): Promise<string[]>;
export function isSubtypeOf(runner: CozoRunner, childType: string, parentType: string): Promise<boolean>;
export function getTypeHierarchy(runner: CozoRunner): Promise<TypeHierarchy>;
export function defineAttribute(
  runner: CozoRunner,
  typeName: string,
  attrName: string,
  valueType: OmValueType,
  required?: boolean,
  description?: string
): Promise<void>;
export function defineRelation(
  runner: CozoRunner,
  relName: string,
  fromType: string,
  toType: string,
  directed?: boolean,
  description?: string
): Promise<void>;

export function resolveType(runner: CozoRunner, typeName: string): Promise<string>;
export function resolveRel(runner: CozoRunner, relName: string): Promise<string>;
export function resolveAttr(runner: CozoRunner, typeName: string, attrName: string): Promise<string>;
export function defineTypeAlias(runner: CozoRunner, alias: string, canonical: string): Promise<void>;
export function defineRelationAlias(runner: CozoRunner, alias: string, canonical: string): Promise<void>;
export function defineAttributeAlias(
  runner: CozoRunner,
  typeName: string,
  aliasAttr: string,
  canonicalAttr: string
): Promise<void>;

export function createEntity(
  runner: CozoRunner,
  id: string,
  typeName: string,
  label: string
): Promise<void>;
export function upsertEntity(
  runner: CozoRunner,
  id: string,
  typeName: string,
  label: string
): Promise<void>;
export function deleteEntity(runner: CozoRunner, entityId: string): Promise<void>;

export function inferValueType(value: any): OmValueType | 'Unknown';
export function getEntityType(runner: CozoRunner, entityId: string): Promise<string>;
export function validatePropertyType(
  runner: CozoRunner,
  entityId: string,
  attrName: string,
  value: any
): Promise<void>;
export function setProperty(
  runner: CozoRunner,
  entityId: string,
  attrName: string,
  value: any,
  options?: WriteOptions
): Promise<void>;
export function getProperty(
  runner: CozoRunner,
  entityId: string,
  attrName: string
): Promise<any | undefined>;

export function validateRelation(
  runner: CozoRunner,
  fromId: string,
  relName: string,
  toId: string
): Promise<void>;
export function linkEntities(
  runner: CozoRunner,
  fromId: string,
  relName: string,
  toId: string,
  props?: Record<string, any>,
  options?: WriteOptions
): Promise<void>;
export function linkEntities(
  runner: CozoRunner,
  fromId: string,
  relName: string,
  toId: string,
  options?: WriteOptions
): Promise<void>;

export function unlinkEntities(
  runner: CozoRunner,
  fromId: string,
  relName: string,
  toId: string,
  options?: WriteOptions
): Promise<void>;

export function defineMutation(
  runner: CozoRunner,
  typeName: string,
  mutationName: string,
  executor: MutationExecutor,
  description?: string
): Promise<void>;

export function executeMutations(
  db: CozoDbLike,
  entityId: string,
  mutations: MutationSpec[]
): Promise<void>;

export function defineAction(
  runner: CozoRunner,
  typeName: string,
  actionName: string,
  handler: ActionHandler,
  description?: string
): Promise<void>;

export function executeAction(
  db: CozoDbLike,
  entityId: string,
  actionName: string,
  params?: Record<string, any>
): Promise<void>;

export function callParentAction(
  ctx: ActionContext,
  actionName: string,
  params?: Record<string, any>
): Promise<MutationSpec[]>;

export function addInterceptor(
  runner: CozoRunner,
  typeName: string,
  actionName: string,
  phase: 'before' | 'after',
  handler: InterceptorHandler,
  description?: string
): Promise<void>;

export function defineConstraint(
  runner: CozoRunner,
  typeName: string,
  constraintName: string,
  def: ConstraintDefinition
): Promise<void>;

export function validateConstraints(
  runner: CozoRunner,
  entityId: string,
  options?: { types?: Array<'conditional' | 'cross-entity' | 'computed-dep' | string> }
): Promise<ConstraintResult>;

export function defineComputed(
  runner: CozoRunner,
  typeName: string,
  attrName: string,
  computeFn: ComputedFn,
  description?: string
): Promise<void>;

export function clearRegistry(): void;

// --- Existential rules (OM-024 ~ OM-027) ---

export type ExistentialWhereOp = '=' | '!=' | '>' | '>=' | '<' | '<=';

export interface ExistentialWhereCondition {
  attr: string;
  op: ExistentialWhereOp;
  value: unknown;
}

export interface ExistentialRuleSpec {
  forEach: {
    type: string;
    where?: ExistentialWhereCondition[];
  };
  exists: {
    rel: string;
    direction?: 'out' | 'in';
    toType: string;
  };
  materialize?: {
    labelTemplate?: string;
    props?: Record<string, unknown>;
  };
  mode?: 'check' | 'materialize';
  message?: string;
  enabled?: boolean;
}

export interface ExistentialRule {
  ruleName: string;
  spec: Pick<ExistentialRuleSpec, 'forEach' | 'exists' | 'materialize'>;
  mode: 'check' | 'materialize';
  message: string;
  enabled: boolean;
}

export function defineExistentialRule(
  runner: CozoRunner,
  ruleName: string,
  spec: ExistentialRuleSpec
): Promise<ExistentialRule>;

export function listExistentialRules(runner: CozoRunner): Promise<ExistentialRule[]>;

/**
 * Drop the per-runner alias resolution cache. Required after seeding
 * om_alias_* rows directly (out-of-band renames) on a long-lived runner.
 */
export function invalidateAliasCache(runner: CozoRunner): void;

export interface ExistentialViolation {
  rule: string;
  entityId: string;
  message: string;
}

export function checkExistentialRules(
  runner: CozoRunner,
  options?: { rules?: string[]; asOf?: string }
): Promise<ExistentialViolation[]>;

export interface ExistentialChaseResult {
  created: Array<{
    rule: string;
    triggerEntityId: string;
    skolemId: string;
    rel: string;
    toType: string;
  }>;
  iterations: number;
  reachedFixpoint: boolean;
  diagnostics: Array<{ ruleName: string; remainingViolations: number }>;
}

export function applyExistentialRules(
  runner: CozoRunner,
  options?: { rules?: string[]; maxIterations?: number; validTime?: string }
): Promise<ExistentialChaseResult>;

export function validateRequiredProperties(
  runner: CozoRunner,
  entityId: string
): Promise<string[]>;
export function validateEntity(
  runner: CozoRunner,
  entityId: string
): Promise<ValidationResult>;
export function finalizeEntity(runner: CozoRunner, entityId: string): Promise<void>;

export function getEntityView(
  runner: CozoRunner,
  entityId: string
): Promise<EntityView | null>;

export function getNeighbors(
  runner: CozoRunner,
  entityId: string,
  relName?: string,
  direction?: 'outgoing' | 'incoming' | 'both'
): Promise<NeighborResult>;

export interface HistoryRangeOptions {
  from?: string;
  to?: string;
}

export interface PropertyHistoryEntry {
  value: any;
  valid_time: string;
  tx_time: string;
}

export interface EdgeHistoryEntry {
  fromId: string;
  relName: string;
  toId: string;
  props: Record<string, any>;
  valid_time: string;
  tx_time: string;
  is_assert: boolean;
}

export function getPropertyHistory(
  runner: CozoRunner,
  entityId: string,
  attrName: string,
  options?: HistoryRangeOptions
): Promise<PropertyHistoryEntry[]>;

export function getPropertyAsOf(
  runner: CozoRunner,
  entityId: string,
  attrName: string,
  timestamp: string
): Promise<any | undefined>;

export function getEntityViewAsOf(
  runner: CozoRunner,
  entityId: string,
  timestamp: string
): Promise<EntityView | null>;

export function getNeighborsAsOf(
  runner: CozoRunner,
  entityId: string,
  relName: string | null | undefined,
  timestamp: string
): Promise<NeighborResult>;

export function getEdgeHistory(
  runner: CozoRunner,
  fromId: string,
  relName: string,
  toId?: string,
  options?: HistoryRangeOptions
): Promise<EdgeHistoryEntry[]>;
export function getEdgeHistory(
  runner: CozoRunner,
  fromId: string,
  relName: string,
  options?: HistoryRangeOptions
): Promise<EdgeHistoryEntry[]>;

export function traverse(
  runner: CozoRunner,
  startId: string,
  relPath: string[]
): Promise<Array<{ id: string; typeName: string; label: string }>>;

export function findByType(
  runner: CozoRunner,
  typeName: string,
  filter?: Record<string, any>,
  options?: FindByTypeOptions
): Promise<Array<{ id: string; label: string; properties: Record<string, any> }>>;

export function getAttributeDefinitions(
  runner: CozoRunner,
  typeName: string
): Promise<Map<string, AttributeDefinition>>;

export function aggregateByType(
  runner: CozoRunner,
  typeName: string,
  attrName: string,
  op: 'sum' | 'avg' | 'min' | 'max' | 'count',
  options?: FindByTypeOptions
): Promise<number>;

export function impactAnalysis(
  runner: CozoRunner,
  input: {
    rootId: string;
    relNames?: string[];
    maxDepth?: number;
    direction?: 'outgoing' | 'incoming' | 'both';
  }
): Promise<TemplateResult<
  { rootId: string; relNames: string[]; maxDepth: number; direction: 'outgoing' | 'incoming' | 'both' },
  {
    nodes: Array<{ id: string; typeName: string; label: string; depth: number }>;
    edges: Array<{ fromId: string; toId: string; relName: string; direction: 'outgoing' | 'incoming' }>;
    visual: {
      primary: 'graph';
      graph: GraphVisual;
      legend: { byType: Record<string, number> };
    };
  },
  {
    impactedCount: number;
    byType: Record<string, number>;
    maxDepthReached: number;
    cycleDetected: boolean;
    truncated: boolean;
  }
>>;

export function ownershipTree(
  runner: CozoRunner,
  input: {
    rootId: string;
    ownerRelNames?: string[];
    maxDepth?: number;
  }
): Promise<TemplateResult<
  { rootId: string; ownerRelNames: string[]; maxDepth: number },
  {
    rootId: string;
    nodes: Array<{ id: string; typeName: string; label: string; depth: number }>;
    edges: Array<{ fromId: string; toId: string; relName: string; direction: 'outgoing' | 'incoming' }>;
    visual: {
      primary: 'tree';
      tree: TreeVisual;
      graph: GraphVisual;
    };
  },
  {
    nodeCount: number;
    edgeCount: number;
    maxDepthReached: number;
    cycleDetected: boolean;
    truncated: boolean;
  }
>>;

export function riskHotspot(
  runner: CozoRunner,
  input?: {
    typeName?: string;
    riskAttr?: string;
    topK?: number;
    minScore?: number;
    degreeWeight?: number;
  }
): Promise<TemplateResult<
  {
    typeName: string;
    riskAttr: string;
    minScore: number;
    topK: number;
    degreeWeight: number;
  },
  {
    hotspots: Array<{
      rank: number;
      entity: { id: string; label: string; typeName: string };
      score: number;
      factors: { baseScore: number; degree: number; degreeWeight: number };
    }>;
    visual: {
      primary: 'ranking';
      ranking: RankingVisualEntry[];
      series: { labels: string[]; values: number[] };
    };
  },
  { evaluatedCount: number; returnedCount: number }
>>;

export function ingestBatch(
  db: CozoDbLike,
  batch: BatchInput,
  options?: { validateRequired?: boolean }
): Promise<{ entities: number; properties: number; edges: number; validatedEntities: number }>;
