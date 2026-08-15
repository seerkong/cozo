'use strict';

const fs = require('fs');
const { resolveNativePath } = require('./lib/native-path');

const nativePath = resolveNativePath(__dirname);
if (!fs.existsSync(nativePath)) {
    throw new Error(
        `depa-cozo native binary is missing: ${nativePath}. ` +
        'Install a published package for this platform, or run npm run build-native from a source checkout.'
    );
}

// The addon is the Cozo N-API compilation artifact.  This package intentionally
// exposes only database primitives; object modeling and ontology APIs live above it.
const native = require(nativePath);

class CozoTx {
    constructor(id) {
        this.txId = id;
    }

    run(script, params = {}) {
        return new Promise((resolve, reject) => {
            native.query_tx(this.txId, script, params, (error, result) => {
                if (error) reject(parseError(error)); else resolve(result);
            });
        });
    }

    abort() {
        return native.abort_tx(this.txId);
    }

    commit() {
        return native.commit_tx(this.txId);
    }
}

class CozoDb {
    constructor(engine = 'mem', databasePath = 'data.db', options = {}) {
        this.dbId = native.open_db(engine, databasePath, JSON.stringify(options));
        this.closed = false;
    }

    close() {
        if (!this.closed) {
            native.close_db(this.dbId);
            this.closed = true;
        }
    }

    multiTransact(write = false) {
        this.#assertOpen();
        return new CozoTx(native.multi_transact(this.dbId, Boolean(write)));
    }

    run(script, params = {}, immutable = false) {
        this.#assertOpen();
        return new Promise((resolve, reject) => {
            native.query_db(this.dbId, script, params, (error, result) => {
                if (error) reject(parseError(error)); else resolve(result);
            }, Boolean(immutable));
        });
    }

    exportRelations(relations) {
        this.#assertOpen();
        return new Promise((resolve, reject) => {
            native.export_relations(this.dbId, relations, (error, result) => {
                if (error) reject(parseError(error)); else resolve(result);
            });
        });
    }

    importRelations(data) {
        return this.#complete('import_relations', data);
    }

    importRelationsFromBackup(databasePath, relations) {
        return this.#complete('import_from_backup', databasePath, relations);
    }

    backup(databasePath) {
        return this.#complete('backup_db', databasePath);
    }

    restore(databasePath) {
        return this.#complete('restore_db', databasePath);
    }

    registerCallback(relation, callback, capacity = -1) {
        this.#assertOpen();
        return native.register_callback(this.dbId, relation, callback, capacity);
    }

    unregisterCallback(callbackId) {
        this.#assertOpen();
        return native.unregister_callback(this.dbId, callbackId);
    }

    registerNamedRule(name, arity, callback) {
        this.#assertOpen();
        return native.register_named_rule(this.dbId, name, arity, async (invocationId, inputs, options) => {
            try {
                native.respond_to_named_rule_invocation(invocationId, await callback(inputs, options));
            } catch (error) {
                native.respond_to_named_rule_invocation(invocationId, String(error));
            }
        });
    }

    unregisterNamedRule(name) {
        this.#assertOpen();
        return native.unregister_named_rule(this.dbId, name);
    }

    #complete(method, ...args) {
        this.#assertOpen();
        return new Promise((resolve, reject) => {
            native[method](this.dbId, ...args, (error) => {
                if (error) reject(parseError(error)); else resolve();
            });
        });
    }

    #assertOpen() {
        if (this.closed) throw new Error('The Cozo database is closed.');
    }
}

function parseError(error) {
    try {
        return JSON.parse(error);
    } catch (_) {
        return new Error(String(error));
    }
}

module.exports = { CozoDb, CozoTx };
