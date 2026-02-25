const { expect, test, describe } = require('bun:test');
const { createTestDb } = require('./helpers');

describe('relation inheritance', () => {
  test('linkEntities succeeds when from-entity is subtype of relation fromType', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Org', 'Organization');
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineType(db, 'ITAsset', 'IT Asset', { parentType: 'Asset' });
      await om.defineType(db, 'Server', 'Server', { parentType: 'ITAsset' });

      await om.defineAttribute(db, 'Org', 'name', 'String', true);
      await om.defineAttribute(db, 'Asset', 'name', 'String', true);

      // Relation: Asset -> Org
      await om.defineRelation(db, 'owned_by', 'Asset', 'Org', true);

      await om.createEntity(db, 'org:1', 'Org', 'Acme Corp');
      await om.setProperty(db, 'org:1', 'name', 'Acme Corp');

      await om.createEntity(db, 'srv:1', 'Server', 'Web Server 1');
      await om.setProperty(db, 'srv:1', 'name', 'Web Server 1');

      // Server is subtype of Asset, so linking Server -> Org via owned_by should pass
      await om.linkEntities(db, 'srv:1', 'owned_by', 'org:1');

      // Verify the edge exists via entity view
      const view = await om.getEntityView(db, 'srv:1');
      expect(view.outgoing.length).toBe(1);
      expect(view.outgoing[0].relName).toBe('owned_by');
      expect(view.outgoing[0].toId).toBe('org:1');
    } finally {
      db.close();
    }
  });

  test('linkEntities succeeds when to-entity is subtype of relation toType', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineType(db, 'ITAsset', 'IT Asset', { parentType: 'Asset' });
      await om.defineType(db, 'Server', 'Server', { parentType: 'ITAsset' });

      await om.defineAttribute(db, 'Asset', 'name', 'String', true);

      // Relation: Asset -> Asset (self-referencing on base type)
      await om.defineRelation(db, 'depends_on', 'Asset', 'Asset', true);

      await om.createEntity(db, 'srv:1', 'Server', 'Web Server');
      await om.setProperty(db, 'srv:1', 'name', 'Web Server');

      await om.createEntity(db, 'asset:1', 'Asset', 'Root Asset');
      await om.setProperty(db, 'asset:1', 'name', 'Root Asset');

      // Server -> Asset via depends_on: both are subtypes of Asset
      await om.linkEntities(db, 'srv:1', 'depends_on', 'asset:1');

      const view = await om.getEntityView(db, 'srv:1');
      expect(view.outgoing.length).toBe(1);
      expect(view.outgoing[0].relName).toBe('depends_on');
    } finally {
      db.close();
    }
  });

  test('linkEntities rejects mismatched from-type', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineType(db, 'ITAsset', 'IT Asset', { parentType: 'Asset' });
      await om.defineType(db, 'Server', 'Server', { parentType: 'ITAsset' });
      await om.defineType(db, 'Vehicle', 'Vehicle', { parentType: 'Asset' });

      await om.defineAttribute(db, 'Asset', 'name', 'String', true);

      // Relation expects Server -> Server
      await om.defineRelation(db, 'replicates', 'Server', 'Server', true);

      await om.createEntity(db, 'v:1', 'Vehicle', 'Truck A');
      await om.setProperty(db, 'v:1', 'name', 'Truck A');

      await om.createEntity(db, 'srv:1', 'Server', 'DB Server');
      await om.setProperty(db, 'srv:1', 'name', 'DB Server');

      // Vehicle -> Server should fail: Vehicle is not a subtype of Server
      await expect(
        om.linkEntities(db, 'v:1', 'replicates', 'srv:1')
      ).rejects.toThrow(/expects Server -> Server, got Vehicle -> Server/);
    } finally {
      db.close();
    }
  });

  test('linkEntities rejects mismatched to-type', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineType(db, 'ITAsset', 'IT Asset', { parentType: 'Asset' });
      await om.defineType(db, 'Server', 'Server', { parentType: 'ITAsset' });
      await om.defineType(db, 'Vehicle', 'Vehicle', { parentType: 'Asset' });

      await om.defineAttribute(db, 'Asset', 'name', 'String', true);

      // Relation expects Server -> Server
      await om.defineRelation(db, 'replicates', 'Server', 'Server', true);

      await om.createEntity(db, 'srv:1', 'Server', 'DB Server');
      await om.setProperty(db, 'srv:1', 'name', 'DB Server');

      await om.createEntity(db, 'v:1', 'Vehicle', 'Truck A');
      await om.setProperty(db, 'v:1', 'name', 'Truck A');

      // Server -> Vehicle should fail: Vehicle is not a subtype of Server
      await expect(
        om.linkEntities(db, 'srv:1', 'replicates', 'v:1')
      ).rejects.toThrow(/expects Server -> Server, got Server -> Vehicle/);
    } finally {
      db.close();
    }
  });

  test('linkEntities accepts reverse endpoint types for undirected relation', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'SourceNode', 'Source node');
      await om.defineType(db, 'TargetNode', 'Target node');
      await om.defineAttribute(db, 'SourceNode', 'name', 'String', true);
      await om.defineAttribute(db, 'TargetNode', 'name', 'String', true);
      await om.defineRelation(db, 'paired_with', 'SourceNode', 'TargetNode', false);

      await om.createEntity(db, 'target:1', 'TargetNode', 'Target');
      await om.setProperty(db, 'target:1', 'name', 'Target');
      await om.createEntity(db, 'source:1', 'SourceNode', 'Source');
      await om.setProperty(db, 'source:1', 'name', 'Source');

      await om.linkEntities(db, 'target:1', 'paired_with', 'source:1');

      const view = await om.getEntityView(db, 'target:1');
      expect(view.outgoing.length).toBe(1);
      expect(view.outgoing[0].relName).toBe('paired_with');
    } finally {
      db.close();
    }
  });
});
