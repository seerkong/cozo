/** Thin database binding. This package deliberately excludes OM, ontology and DSL APIs. */
export class CozoTx {
    readonly txId: number;
    readonly state: 'open' | 'committed' | 'aborted';
    readonly pending: boolean;
    run(script: string, params?: Record<string, unknown>): Promise<unknown>;
    abort(): unknown;
    commit(): unknown;
}

export class CozoDb {
    readonly closed: boolean;
    constructor(engine?: string, databasePath?: string, options?: Record<string, unknown>);
    close(): void;
    multiTransact(write?: boolean): CozoTx;
    run(script: string, params?: Record<string, unknown>, immutable?: boolean): Promise<unknown>;
    exportRelations(relations: unknown): Promise<unknown>;
    importRelations(data: unknown): Promise<void>;
    importRelationsFromBackup(databasePath: string, relations: unknown): Promise<void>;
    backup(databasePath: string): Promise<void>;
    restore(databasePath: string): Promise<void>;
    registerCallback(relation: string, callback: (...args: unknown[]) => unknown, capacity?: number): unknown;
    unregisterCallback(callbackId: unknown): unknown;
    registerNamedRule(name: string, arity: number, callback: (inputs: unknown, options: unknown) => unknown | Promise<unknown>): unknown;
    unregisterNamedRule(name: string): unknown;
}
