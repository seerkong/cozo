export class DslError extends Error {
  readonly code: string;
  constructor(code: string, message: string);
}

export class VarToken {
  readonly name: string;
}

export class ParamToken {
  readonly name: string;
  readonly value: any;
}

export class RawToken {
  readonly text: string;
}

export class AutoParamToken {
  readonly value: any;
}

export type QueryTerm =
  | string
  | number
  | boolean
  | null
  | Record<string, any>
  | any[]
  | VarToken
  | ParamToken
  | RawToken
  | AutoParamToken;

export type DslExpression =
  | {
      __dslExpr: true;
      kind: 'cmp';
      op: '==' | '!=' | '>' | '<' | '>=' | '<=';
      left: QueryTerm;
      right: QueryTerm;
    }
  | {
      __dslExpr: true;
      kind: 'in';
      left: QueryTerm;
      values: QueryTerm[];
    }
  | {
      __dslExpr: true;
      kind: 'and' | 'or';
      children: Array<DslExpression | RawToken | string>;
    }
  | {
      __dslExpr: true;
      kind: 'not';
      child: DslExpression | RawToken | string;
    };

export class CozoQueryBuilder {
  input(bindings: Record<string, QueryTerm>): CozoQueryBuilder;
  select(columns: string[]): CozoQueryBuilder;
  fromStored(relationName: string, bindings: Record<string, QueryTerm>): CozoQueryBuilder;
  atom(text: string): CozoQueryBuilder;
  where(condition: DslExpression | RawToken | string): CozoQueryBuilder;
  whereEq(leftVarName: string, rightTerm: QueryTerm): CozoQueryBuilder;

  mutation(
    kind: 'put' | 'insert' | 'update' | 'rm' | 'create',
    relationName: string,
    keyColumns: string[],
    valueColumns?: string[]
  ): CozoQueryBuilder;
  put(relationName: string, keyColumns: string[], valueColumns?: string[]): CozoQueryBuilder;
  insert(relationName: string, keyColumns: string[], valueColumns?: string[]): CozoQueryBuilder;
  update(relationName: string, keyColumns: string[], valueColumns?: string[]): CozoQueryBuilder;
  rm(relationName: string, keyColumns: string[]): CozoQueryBuilder;
  create(relationName: string, keyColumns: string[], valueColumns?: string[]): CozoQueryBuilder;
  createRaw(relationName: string, schemaText: string): CozoQueryBuilder;

  order(...columns: string[]): CozoQueryBuilder;
  limit(value: number): CozoQueryBuilder;
  offset(value: number): CozoQueryBuilder;
  rawDirective(text: string): CozoQueryBuilder;

  build(): { script: string; params: Record<string, any> };
  execute(runner: { run(script: string, params?: Record<string, any>): Promise<any> }): Promise<any>;
}

export function query(): CozoQueryBuilder;
export function variable(name: string): VarToken;
export { variable as var };
export function param(name: string, value: any): ParamToken;
export function p(name: string, value: any): ParamToken;
export function val(value: any): AutoParamToken;
export function raw(text: string): RawToken;

export function eq(left: QueryTerm, right: QueryTerm): DslExpression;
export function neq(left: QueryTerm, right: QueryTerm): DslExpression;
export function gt(left: QueryTerm, right: QueryTerm): DslExpression;
export function lt(left: QueryTerm, right: QueryTerm): DslExpression;
export function gte(left: QueryTerm, right: QueryTerm): DslExpression;
export function lte(left: QueryTerm, right: QueryTerm): DslExpression;
export function and(...children: Array<DslExpression | RawToken | string>): DslExpression;
export function or(...children: Array<DslExpression | RawToken | string>): DslExpression;
export function not(child: DslExpression | RawToken | string): DslExpression;
export function inExpr(left: QueryTerm, values: QueryTerm[]): DslExpression;
export { inExpr as in };

export function assertIdentifier(identifier: string, kind?: string): string;
