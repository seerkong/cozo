/*
 * Copyright 2022, The Cozo Project Authors.
 *
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file,
 * You can obtain one at https://mozilla.org/MPL/2.0/.
 */

const path = require('path');
const native = require(path.join(__dirname, 'native', 'cozo_bun.node'));
const om = require('./cozo-om');
const dsl = require('./cozo-dsl');

class CozoTx {
    constructor(id) {
        this.tx_id = id;
    }

    run(script, params) {
        return new Promise((resolve, reject) => {
            params = params || {};
            native.query_tx(this.tx_id, script, params, (err, result) => {
                if (err) {
                    reject(JSON.parse(err));
                } else {
                    resolve(result);
                }
            });
        });
    }

    abort() {
        return native.abort_tx(this.tx_id);
    }

    commit() {
        return native.commit_tx(this.tx_id);
    }
}

class CozoDb {
    constructor(engine, pathValue, options) {
        this.db_id = native.open_db(
            engine || 'mem',
            pathValue || 'data.db',
            JSON.stringify(options || {})
        );
    }

    close() {
        native.close_db(this.db_id);
    }

    multiTransact(write) {
        return new CozoTx(native.multi_transact(this.db_id, !!write));
    }

    run(script, params, immutable) {
        return new Promise((resolve, reject) => {
            params = params || {};
            native.query_db(this.db_id, script, params, (err, result) => {
                if (err) {
                    reject(JSON.parse(err));
                } else {
                    resolve(result);
                }
            }, !!immutable);
        });
    }

    exportRelations(relations) {
        return new Promise((resolve, reject) => {
            native.export_relations(this.db_id, relations, (err, data) => {
                if (err) {
                    reject(JSON.parse(err));
                } else {
                    resolve(data);
                }
            });
        });
    }

    importRelations(data) {
        return new Promise((resolve, reject) => {
            native.import_relations(this.db_id, data, (err) => {
                if (err) {
                    reject(JSON.parse(err));
                } else {
                    resolve();
                }
            });
        });
    }

    importRelationsFromBackup(pathValue, relations) {
        return new Promise((resolve, reject) => {
            native.import_from_backup(this.db_id, pathValue, relations, (err) => {
                if (err) {
                    reject(JSON.parse(err));
                } else {
                    resolve();
                }
            });
        });
    }

    backup(pathValue) {
        return new Promise((resolve, reject) => {
            native.backup_db(this.db_id, pathValue, (err) => {
                if (err) {
                    reject(JSON.parse(err));
                } else {
                    resolve();
                }
            });
        });
    }

    restore(pathValue) {
        return new Promise((resolve, reject) => {
            native.restore_db(this.db_id, pathValue, (err) => {
                if (err) {
                    reject(JSON.parse(err));
                } else {
                    resolve();
                }
            });
        });
    }

    registerCallback(relation, cb, capacity = -1) {
        return native.register_callback(this.db_id, relation, cb, capacity);
    }

    unregisterCallback(cb_id) {
        return native.unregister_callback(this.db_id, cb_id);
    }

    registerNamedRule(name, arity, cb) {
        return native.register_named_rule(this.db_id, name, arity, async (ret_id, inputs, options) => {
            let ret;
            try {
                ret = await cb(inputs, options);
            } catch (e) {
                console.error(e);
                native.respond_to_named_rule_invocation(ret_id, `${e}`);
                return;
            }
            try {
                native.respond_to_named_rule_invocation(ret_id, ret);
            } catch (e) {
                console.error(e);
            }
        });
    }

    unregisterNamedRule(name) {
        return native.unregister_named_rule(this.db_id, name);
    }
}

module.exports = { CozoDb, om, dsl };
