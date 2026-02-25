const { describe, test, expect } = require('bun:test');

const { createTestDb } = require('./helpers');

async function listRows(db, relation) {
  const scripts = {
    om_entity: '?[id] := *om_entity{ id, type_name: _type_name, label: _label }',
    om_property: '?[entity_id, attr_name, valid_time] := *om_property{ entity_id, attr_name, valid_time, value: _value, tx_time: _tx_time }',
    om_edge: '?[from_id, rel_name, to_id, valid_time] := *om_edge{ from_id, rel_name, to_id, valid_time, props: _props, tx_time: _tx_time }',
  };
  const result = await db.run(scripts[relation], {});
  return result.rows;
}

describe('OM governance API parity: cascading entity deletion', () => {
  test('deleteEntity physically removes entity property history and all touching edge history', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Person', 'Person');
      await om.defineAttribute(db, 'Person', 'note', 'String', false);
      await om.defineRelation(db, 'knows', 'Person', 'Person', true);
      for (const id of ['target', 'incoming', 'outgoing', 'unrelated']) {
        await om.createEntity(db, id, 'Person', id);
      }

      await om.setProperty(db, 'target', 'note', 'old', { validTime: '2000-01-01T00:00:00Z' });
      await om.setProperty(db, 'target', 'note', 'new', { validTime: '2001-01-01T00:00:00Z' });
      await om.setProperty(db, 'unrelated', 'note', 'keep', { validTime: '2000-01-01T00:00:00Z' });
      await om.linkEntities(db, 'incoming', 'knows', 'target', {}, { validTime: '2000-01-01T00:00:00Z' });
      await om.linkEntities(db, 'target', 'knows', 'outgoing', {}, { validTime: '2000-01-01T00:00:00Z' });
      await om.linkEntities(db, 'target', 'knows', 'outgoing', {}, { validTime: '2001-01-01T00:00:00Z' });
      await om.linkEntities(db, 'incoming', 'knows', 'outgoing', {}, { validTime: '2000-01-01T00:00:00Z' });

      await om.deleteEntity(db, 'target');

      expect((await listRows(db, 'om_entity')).map(([id]) => id).sort()).toEqual(['incoming', 'outgoing', 'unrelated']);
      expect((await listRows(db, 'om_property')).filter(([entityId]) => entityId === 'target')).toEqual([]);
      expect((await listRows(db, 'om_property')).map(([entityId, attrName]) => [entityId, attrName])).toEqual([['unrelated', 'note']]);
      expect((await listRows(db, 'om_edge')).some(([fromId, _relName, toId]) => fromId === 'target' || toId === 'target')).toBe(false);
      expect((await listRows(db, 'om_edge')).map(([fromId, relName, toId]) => [fromId, relName, toId])).toEqual([['incoming', 'knows', 'outgoing']]);
    } finally {
      db.close();
    }
  });

  test('deleteEntity is a harmless no-op for a missing entity', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Person', 'Person');
      await om.createEntity(db, 'survivor', 'Person', 'Survivor');

      await om.deleteEntity(db, 'missing');
      await om.deleteEntity(db, 'missing');

      expect((await listRows(db, 'om_entity')).map(([id]) => id)).toEqual(['survivor']);
    } finally {
      db.close();
    }
  });
});
