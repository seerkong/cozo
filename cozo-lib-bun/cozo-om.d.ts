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

declare const omRuntimeBrand: unique symbol;

export interface OmRuntime<TRunner extends CozoRunner = CozoRunner> {
  readonly runner: TRunner;
  readonly [omRuntimeBrand]: true;
}

export type OmRunner = CozoRunner | OmRuntime;
export type OmDbLike = CozoDbLike | OmRuntime<CozoDbLike>;

export type BehaviorCatalogKind =
  | 'constraint'
  | 'computed'
  | 'action'
  | 'mutation'
  | 'interceptor';

export type BehaviorCatalogCallbackSlot =
  | 'when'
  | 'then'
  | 'validator'
  | 'compute'
  | 'handler'
  | 'executor';

export type BehaviorReadiness = 'unbound' | 'unresolved' | 'ready';

export interface BehaviorCallbackBinding {
  readonly slot: BehaviorCatalogCallbackSlot;
  readonly bindingId: string | null;
  readonly readiness: BehaviorReadiness;
}

export interface BehaviorCatalogEntry {
  readonly kind: BehaviorCatalogKind;
  readonly ownerType: string;
  readonly name: string;
  readonly constraintType: string | null;
  readonly message: string | null;
  readonly description: string | null;
  readonly interceptorPhase: 'before' | 'after' | null;
  readonly interceptorSeq: number | null;
  readonly callbacks: readonly BehaviorCallbackBinding[];
}

export interface BehaviorCatalog {
  readonly behaviors: readonly BehaviorCatalogEntry[];
}

export interface BehaviorManifestDiagnostic {
  readonly code: string;
  readonly path: string;
  readonly message: string;
}

export interface BehaviorManifestDecodeResult {
  readonly catalog: BehaviorCatalog | null;
  readonly diagnostics: readonly BehaviorManifestDiagnostic[];
  readonly success: boolean;
}

export interface BehaviorImportDiagnostic extends BehaviorManifestDiagnostic {
  readonly kind: BehaviorCatalogKind | null;
  readonly ownerType: string | null;
  readonly behaviorName: string | null;
  readonly slot: BehaviorCatalogCallbackSlot | null;
  readonly bindingId: string | null;
  readonly interceptorPhase: 'before' | 'after' | null;
  readonly interceptorSeq: number | null;
}

export interface BehaviorTypedCallbackBinding<TCallback extends Function> {
  readonly bindingId: string;
  readonly callback: TCallback;
}

export interface BehaviorCallbackBindingSet {
  readonly constraints?: readonly BehaviorTypedCallbackBinding<
    (ctx: ConstraintContext) => Promise<boolean> | boolean
  >[];
  readonly validators?: readonly BehaviorTypedCallbackBinding<ConstraintValidator>[];
  readonly computed?: readonly BehaviorTypedCallbackBinding<ComputedFn>[];
  readonly actions?: readonly BehaviorTypedCallbackBinding<ActionHandler>[];
  readonly mutations?: readonly BehaviorTypedCallbackBinding<MutationExecutor>[];
  readonly interceptors?: readonly BehaviorTypedCallbackBinding<InterceptorHandler>[];
}

export interface BehaviorImportOptions {
  readonly requireReady?: boolean;
}

export interface BehaviorImportResult {
  readonly applied: boolean;
  readonly diagnostics: readonly BehaviorImportDiagnostic[];
  readonly unresolved: readonly BehaviorUnresolvedDiagnostic[];
}

export interface BehaviorUnresolvedDiagnostic {
  readonly code: 'OMR1001';
  readonly kind: BehaviorCatalogKind;
  readonly ownerType: string;
  readonly behaviorKey: string;
  readonly slot: BehaviorCatalogCallbackSlot;
  readonly bindingId: string;
  readonly interceptorPhase: 'before' | 'after' | null;
  readonly interceptorSeq: number | null;
}

export class BehaviorUnresolvedError extends Error {
  readonly diagnostic: BehaviorUnresolvedDiagnostic;
  readonly code: 'OMR1001';
  readonly kind: BehaviorCatalogKind;
  readonly ownerType: string;
  readonly behaviorKey: string;
  readonly slot: BehaviorCatalogCallbackSlot;
  readonly bindingId: string;
  readonly interceptorPhase: 'before' | 'after' | null;
  readonly interceptorSeq: number | null;
  constructor(diagnostic: BehaviorUnresolvedDiagnostic);
}

export class BehaviorImportError extends Error {
  readonly originalFailure: unknown;
  readonly compensationFailures: readonly unknown[];
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
  runtime: OmRuntime;
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
  runtime: OmRuntime;
  entityId: string;
  typeName: string;
  getProperty(attrName: string): Promise<any | undefined>;
  getNeighbors(relName?: string, direction?: 'outgoing' | 'incoming' | 'both'): Promise<NeighborResult>;
}

export interface ScopedConstraintDefinition {
  scope?: 'conditional' | 'cross-entity' | 'computed-dep';
  message?: string;
  when(ctx: ConstraintContext): Promise<boolean> | boolean;
  then(ctx: ConstraintContext): Promise<boolean> | boolean;
}

export interface CustomConstraintDefinition {
  scope: 'custom';
  message?: string;
  validator(ctx: ConstraintContext): Promise<string | null> | string | null;
}

export type ConstraintDefinition = ScopedConstraintDefinition | CustomConstraintDefinition;
export type ConstraintValidator =
  (ctx: ConstraintContext) => Promise<string | null> | string | null;

export interface ComputedContext {
  runner: CozoRunner;
  runtime: OmRuntime;
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

export type BehaviorConstraintSnapshotRow = readonly [
  typeName: string,
  constraintName: string,
  constraintType: string,
  message: string,
];

export type BehaviorComputedSnapshotRow = readonly [
  typeName: string,
  attrName: string,
  description: string,
];

export type BehaviorActionSnapshotRow = readonly [
  typeName: string,
  actionName: string,
  description: string,
];

export type BehaviorMutationSnapshotRow = readonly [
  typeName: string,
  mutationName: string,
  description: string,
];

export type BehaviorInterceptorSnapshotRow = readonly [
  typeName: string,
  actionName: string,
  phase: 'before' | 'after',
  seq: number,
  description: string,
];

export type BehaviorBindingSnapshotRow = readonly [
  behaviorKind: BehaviorCatalogKind,
  ownerType: string,
  behaviorName: string,
  callbackSlot: BehaviorCatalogCallbackSlot,
  phase: '' | 'before' | 'after',
  seq: number,
  bindingId: string,
];

export interface BehaviorSnapshotV1 {
  readonly formatVersion: 1;
  readonly om_constraint_def: readonly BehaviorConstraintSnapshotRow[];
  readonly om_computed_def: readonly BehaviorComputedSnapshotRow[];
  readonly om_action_def: readonly BehaviorActionSnapshotRow[];
  readonly om_mutation_def: readonly BehaviorMutationSnapshotRow[];
  readonly om_interceptor_def: readonly BehaviorInterceptorSnapshotRow[];
  readonly om_behavior_binding: readonly BehaviorBindingSnapshotRow[];
}

export interface SchemaSnapshot {
  version: number;
  createdAt: string;
  schema: any;
  behavior?: BehaviorSnapshotV1;
  checksum?: string | null;
}

export type BehaviorSnapshotPresence = 'present' | 'missing';

export type BehaviorSnapshotRelationName =
  | 'om_constraint_def'
  | 'om_computed_def'
  | 'om_action_def'
  | 'om_mutation_def'
  | 'om_interceptor_def'
  | 'om_behavior_binding';

export interface SchemaVersioningDiagnostic {
  readonly code: 'OMSV1001' | 'OMSV1002' | 'OMSV1003' | 'OMSV1004' | string;
  readonly path: string;
  readonly message: string;
  readonly allowedPolicies?: readonly ('preserve' | 'clear')[];
  readonly side?: 'from' | 'to';
  readonly relation?: BehaviorSnapshotRelationName;
  readonly key?: Readonly<Record<string, unknown>>;
  readonly expectedColumns?: number;
  readonly actualColumns?: number | null;
  readonly expectedFormatVersion?: number;
  readonly actualFormatVersion?: unknown;
}

export interface BehaviorConstraintSnapshotValue {
  readonly type_name: string;
  readonly constraint_name: string;
  readonly constraint_type: string;
  readonly message: string;
}

export interface BehaviorComputedSnapshotValue {
  readonly type_name: string;
  readonly attr_name: string;
  readonly description: string;
}

export interface BehaviorActionSnapshotValue {
  readonly type_name: string;
  readonly action_name: string;
  readonly description: string;
}

export interface BehaviorMutationSnapshotValue {
  readonly type_name: string;
  readonly mutation_name: string;
  readonly description: string;
}

export interface BehaviorInterceptorSnapshotValue {
  readonly type_name: string;
  readonly action_name: string;
  readonly phase: 'before' | 'after';
  readonly seq: number;
  readonly description: string;
}

export interface BehaviorBindingSnapshotValue {
  readonly behavior_kind: BehaviorCatalogKind;
  readonly owner_type: string;
  readonly behavior_name: string;
  readonly callback_slot: BehaviorCatalogCallbackSlot;
  readonly phase: '' | 'before' | 'after';
  readonly seq: number;
  readonly binding_id: string;
}

export type BehaviorConstraintSnapshotKey =
  Pick<BehaviorConstraintSnapshotValue, 'type_name' | 'constraint_name'>;
export type BehaviorComputedSnapshotKey =
  Pick<BehaviorComputedSnapshotValue, 'type_name' | 'attr_name'>;
export type BehaviorActionSnapshotKey =
  Pick<BehaviorActionSnapshotValue, 'type_name' | 'action_name'>;
export type BehaviorMutationSnapshotKey =
  Pick<BehaviorMutationSnapshotValue, 'type_name' | 'mutation_name'>;
export type BehaviorInterceptorSnapshotKey =
  Pick<BehaviorInterceptorSnapshotValue, 'type_name' | 'action_name' | 'phase' | 'seq'>;
export type BehaviorBindingSnapshotKey =
  Pick<
    BehaviorBindingSnapshotValue,
    'behavior_kind' | 'owner_type' | 'behavior_name' | 'callback_slot' | 'phase' | 'seq'
  >;

export interface BehaviorRelationUpdate<Row, Key> {
  readonly key: Key;
  readonly from: Row;
  readonly to: Row;
}

export interface BehaviorRelationDiff<Row, Key> {
  readonly added: readonly Row[];
  readonly removed: readonly Row[];
  readonly updated: readonly BehaviorRelationUpdate<Row, Key>[];
}

export interface BehaviorSchemaDiffRelations {
  readonly om_constraint_def: BehaviorRelationDiff<
    BehaviorConstraintSnapshotValue,
    BehaviorConstraintSnapshotKey
  >;
  readonly om_computed_def: BehaviorRelationDiff<
    BehaviorComputedSnapshotValue,
    BehaviorComputedSnapshotKey
  >;
  readonly om_action_def: BehaviorRelationDiff<
    BehaviorActionSnapshotValue,
    BehaviorActionSnapshotKey
  >;
  readonly om_mutation_def: BehaviorRelationDiff<
    BehaviorMutationSnapshotValue,
    BehaviorMutationSnapshotKey
  >;
  readonly om_interceptor_def: BehaviorRelationDiff<
    BehaviorInterceptorSnapshotValue,
    BehaviorInterceptorSnapshotKey
  >;
  readonly om_behavior_binding: BehaviorRelationDiff<
    BehaviorBindingSnapshotValue,
    BehaviorBindingSnapshotKey
  >;
}

export interface BehaviorSchemaDiff {
  readonly fromPresence: BehaviorSnapshotPresence;
  readonly toPresence: BehaviorSnapshotPresence;
  readonly comparable: boolean;
  readonly diagnostics: readonly SchemaVersioningDiagnostic[];
  readonly relations: Partial<BehaviorSchemaDiffRelations>;
}

export interface SchemaDiff {
  fromVersion: number;
  toVersion: number;
  schema: any;
  aliases: any;
  perm?: any;
  behavior: BehaviorSchemaDiff;
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
  legacyBehaviorPolicy?: 'preserve' | 'clear';
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
  compatibilityDiagnostics: readonly SchemaVersioningDiagnostic[];
  behaviorPolicyApplied: 'snapshot' | 'preserve' | 'clear' | null;
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

export function createOmRuntime<TRunner extends CozoRunner>(runner: TRunner): OmRuntime<TRunner>;
export function registerConstraint(
  runtime: OmRuntime,
  typeName: string,
  constraintName: string,
  when: (ctx: ConstraintContext) => Promise<boolean> | boolean,
  then: (ctx: ConstraintContext) => Promise<boolean> | boolean
): void;
export function registerConstraint(
  runtime: OmRuntime,
  typeName: string,
  constraintName: string,
  whenBindingId: string,
  when: (ctx: ConstraintContext) => Promise<boolean> | boolean,
  thenBindingId: string,
  then: (ctx: ConstraintContext) => Promise<boolean> | boolean
): void;
export function registerValidator(
  runtime: OmRuntime,
  typeName: string,
  constraintName: string,
  validator: ConstraintValidator
): void;
export function registerValidator(
  runtime: OmRuntime,
  typeName: string,
  constraintName: string,
  bindingId: string,
  validator: ConstraintValidator
): void;
export function registerComputed(
  runtime: OmRuntime,
  typeName: string,
  attrName: string,
  compute: ComputedFn
): void;
export function registerComputed(
  runtime: OmRuntime,
  typeName: string,
  attrName: string,
  bindingId: string,
  compute: ComputedFn
): void;
export function registerAction(
  runtime: OmRuntime,
  typeName: string,
  actionName: string,
  handler: ActionHandler
): void;
export function registerAction(
  runtime: OmRuntime,
  typeName: string,
  actionName: string,
  bindingId: string,
  handler: ActionHandler
): void;
export function registerMutation(
  runtime: OmRuntime,
  typeName: string,
  mutationName: string,
  executor: MutationExecutor
): void;
export function registerMutation(
  runtime: OmRuntime,
  typeName: string,
  mutationName: string,
  bindingId: string,
  executor: MutationExecutor
): void;
export function registerInterceptor(
  runtime: OmRuntime,
  typeName: string,
  actionName: string,
  phase: 'before' | 'after',
  seq: number,
  handler: InterceptorHandler,
  description?: string
): void;
export function registerInterceptor(
  runtime: OmRuntime,
  typeName: string,
  actionName: string,
  phase: 'before' | 'after',
  seq: number,
  bindingId: string,
  handler: InterceptorHandler,
  description?: string
): void;

export function initSchema(runner: OmRunner): Promise<void>;
export function createSchema(runner: OmRunner): Promise<void>;
export function getBehaviorCatalog(runner: OmRunner): Promise<BehaviorCatalog>;
export function encodeBehaviorManifestJson(catalog: BehaviorCatalog): Uint8Array;
export function decodeBehaviorManifestJson(
  json: string | Uint8Array
): BehaviorManifestDecodeResult;
export function exportBehaviorManifestJson(runner: OmRunner): Promise<Uint8Array>;
export function importBehaviorManifestJson(
  runtime: OmRuntime<CozoDbLike>,
  json: string | Uint8Array,
  callbacks?: BehaviorCallbackBindingSet,
  options?: BehaviorImportOptions
): Promise<BehaviorImportResult>;

export function seedPermissionMetadata(runner: OmRunner, data?: any): Promise<void>;

export function checkAccess(runner: OmRunner, input: CheckAccessInput): Promise<CheckAccessResult>;

export function getSchemaState(runner: OmRunner): Promise<SchemaState>;
export function listSchemaVersions(runner: OmRunner): Promise<SchemaVersion[]>;
export function applySchemaMigration(runner: OmRunner, spec: SchemaMigrationSpec): Promise<void>;
export function rollbackSchema(
  runner: OmRunner,
  targetVersion: number,
  options?: RollbackSchemaOptions
): Promise<RollbackSchemaResult>;

export function readSchemaSnapshot(runner: OmRunner, version: number): Promise<SchemaSnapshot | null>;
export function writeSchemaSnapshot(runner: OmRunner, version: number): Promise<SchemaSnapshot>;
export function diffSchemaVersions(runner: OmRunner, fromVersion: number, toVersion: number): Promise<SchemaDiff>;

export function defineType(runner: OmRunner, name: string, description: string, options?: DefineTypeOptions): Promise<void>;
export function defineMixin(runner: OmRunner, name: string, description: string): Promise<void>;
export function getAncestors(runner: OmRunner, typeName: string): Promise<string[]>;
export function getDescendants(runner: OmRunner, typeName: string): Promise<string[]>;
export function isSubtypeOf(runner: OmRunner, childType: string, parentType: string): Promise<boolean>;
export function getTypeHierarchy(runner: OmRunner): Promise<TypeHierarchy>;
export function defineAttribute(
  runner: OmRunner,
  typeName: string,
  attrName: string,
  valueType: OmValueType,
  required?: boolean,
  description?: string
): Promise<void>;
export function defineRelation(
  runner: OmRunner,
  relName: string,
  fromType: string,
  toType: string,
  directed?: boolean,
  description?: string
): Promise<void>;

export function resolveType(runner: OmRunner, typeName: string): Promise<string>;
export function resolveRel(runner: OmRunner, relName: string): Promise<string>;
export function resolveAttr(runner: OmRunner, typeName: string, attrName: string): Promise<string>;
export function defineTypeAlias(runner: OmRunner, alias: string, canonical: string): Promise<void>;
export function defineRelationAlias(runner: OmRunner, alias: string, canonical: string): Promise<void>;
export function defineAttributeAlias(
  runner: OmRunner,
  typeName: string,
  aliasAttr: string,
  canonicalAttr: string
): Promise<void>;

export function createEntity(
  runner: OmRunner,
  id: string,
  typeName: string,
  label: string
): Promise<void>;
export function upsertEntity(
  runner: OmRunner,
  id: string,
  typeName: string,
  label: string
): Promise<void>;
export function deleteEntity(runner: OmRunner, entityId: string): Promise<void>;

export function inferValueType(value: any): OmValueType | 'Unknown';
export function getEntityType(runner: OmRunner, entityId: string): Promise<string>;
export function validatePropertyType(
  runner: OmRunner,
  entityId: string,
  attrName: string,
  value: any
): Promise<void>;
export function setProperty(
  runner: OmRunner,
  entityId: string,
  attrName: string,
  value: any,
  options?: WriteOptions
): Promise<void>;
export function getProperty(
  runner: OmRunner,
  entityId: string,
  attrName: string
): Promise<any | undefined>;

export function validateRelation(
  runner: OmRunner,
  fromId: string,
  relName: string,
  toId: string
): Promise<void>;
export function linkEntities(
  runner: OmRunner,
  fromId: string,
  relName: string,
  toId: string,
  props?: Record<string, any>,
  options?: WriteOptions
): Promise<void>;
export function linkEntities(
  runner: OmRunner,
  fromId: string,
  relName: string,
  toId: string,
  options?: WriteOptions
): Promise<void>;

export function unlinkEntities(
  runner: OmRunner,
  fromId: string,
  relName: string,
  toId: string,
  options?: WriteOptions
): Promise<void>;

export function defineMutation(
  runner: OmRunner,
  typeName: string,
  mutationName: string,
  executor: MutationExecutor,
  description?: string
): Promise<void>;

export function executeMutations(
  db: OmDbLike,
  entityId: string,
  mutations: MutationSpec[]
): Promise<void>;

export function defineAction(
  runner: OmRunner,
  typeName: string,
  actionName: string,
  handler: ActionHandler,
  description?: string
): Promise<void>;

export function executeAction(
  db: OmDbLike,
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
  runner: OmRunner,
  typeName: string,
  actionName: string,
  phase: 'before' | 'after',
  handler: InterceptorHandler,
  description?: string
): Promise<void>;

export function defineConstraint(
  runner: OmRunner,
  typeName: string,
  constraintName: string,
  def: ConstraintDefinition
): Promise<void>;

export function validateConstraints(
  runner: OmRunner,
  entityId: string,
  options?: { types?: Array<'conditional' | 'cross-entity' | 'computed-dep' | 'custom' | string> }
): Promise<ConstraintResult>;

export function defineComputed(
  runner: OmRunner,
  typeName: string,
  attrName: string,
  computeFn: ComputedFn,
  description?: string
): Promise<void>;

export function clearRegistry(): void;
export function clearRegistry(runtime: OmRuntime): void | Promise<void>;

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
  runner: OmRunner,
  ruleName: string,
  spec: ExistentialRuleSpec
): Promise<ExistentialRule>;

export function listExistentialRules(runner: OmRunner): Promise<ExistentialRule[]>;

/**
 * Drop the per-runner alias resolution cache. Required after seeding
 * om_alias_* rows directly (out-of-band renames) on a long-lived runner.
 */
export function invalidateAliasCache(runner: OmRunner): void;

export interface ExistentialViolation {
  rule: string;
  entityId: string;
  message: string;
}

export function checkExistentialRules(
  runner: OmRunner,
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
  runner: OmRunner,
  options?: { rules?: string[]; maxIterations?: number; validTime?: string }
): Promise<ExistentialChaseResult>;

export function validateRequiredProperties(
  runner: OmRunner,
  entityId: string
): Promise<string[]>;
export function validateEntity(
  runner: OmRunner,
  entityId: string
): Promise<ValidationResult>;
export function finalizeEntity(runner: OmRunner, entityId: string): Promise<void>;

export function getEntityView(
  runner: OmRunner,
  entityId: string
): Promise<EntityView | null>;

export function getNeighbors(
  runner: OmRunner,
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
  runner: OmRunner,
  entityId: string,
  attrName: string,
  options?: HistoryRangeOptions
): Promise<PropertyHistoryEntry[]>;

export function getPropertyAsOf(
  runner: OmRunner,
  entityId: string,
  attrName: string,
  timestamp: string
): Promise<any | undefined>;

export function getEntityViewAsOf(
  runner: OmRunner,
  entityId: string,
  timestamp: string
): Promise<EntityView | null>;

export function getNeighborsAsOf(
  runner: OmRunner,
  entityId: string,
  relName: string | null | undefined,
  timestamp: string
): Promise<NeighborResult>;

export function getEdgeHistory(
  runner: OmRunner,
  fromId: string,
  relName: string,
  toId?: string,
  options?: HistoryRangeOptions
): Promise<EdgeHistoryEntry[]>;
export function getEdgeHistory(
  runner: OmRunner,
  fromId: string,
  relName: string,
  options?: HistoryRangeOptions
): Promise<EdgeHistoryEntry[]>;

export function traverse(
  runner: OmRunner,
  startId: string,
  relPath: string[]
): Promise<Array<{ id: string; typeName: string; label: string }>>;

export function findByType(
  runner: OmRunner,
  typeName: string,
  filter?: Record<string, any>,
  options?: FindByTypeOptions
): Promise<Array<{ id: string; label: string; properties: Record<string, any> }>>;

export function getAttributeDefinitions(
  runner: OmRunner,
  typeName: string
): Promise<Map<string, AttributeDefinition>>;

export function aggregateByType(
  runner: OmRunner,
  typeName: string,
  attrName: string,
  op: 'sum' | 'avg' | 'min' | 'max' | 'count',
  options?: FindByTypeOptions
): Promise<number>;

export function impactAnalysis(
  runner: OmRunner,
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
  runner: OmRunner,
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
  runner: OmRunner,
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
  db: OmDbLike,
  batch: BatchInput,
  options?: { validateRequired?: boolean }
): Promise<{ entities: number; properties: number; edges: number; validatedEntities: number }>;
