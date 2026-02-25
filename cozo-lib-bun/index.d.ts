export class CozoDb {
  constructor(engine?: string, path?: string, options?: object);

  close(): void;

  run(script: string, params?: Record<string, any>, immutable?: boolean): Promise<any>;

  exportRelations(relations: Array<string>): Promise<any>;

  importRelations(data: object): Promise<object>;

  backup(path: string): Promise<any>;

  restore(path: string): Promise<object>;

  importRelationsFromBackup(path: string, rels: Array<string>): Promise<any>;

  multiTransact(write?: boolean): CozoTx;

  registerCallback(relation: string, cb: (...args: any[]) => any, capacity?: number): number;

  unregisterCallback(cb_id: number): void;

  registerNamedRule(name: string, arity: number, cb: (inputs: any[], options: any) => any): void;

  unregisterNamedRule(name: string): void;
}

export class CozoTx {
  run(script: string, params?: Record<string, any>): Promise<any>;

  abort(): void;

  commit(): void;
}

export * as om from './cozo-om';
export * as dsl from './cozo-dsl';
