const { expect, test, describe } = require('bun:test');
const { createTestDb } = require('./helpers');

describe('relation inheritance', () => {
  test('linkEntities succeeds when from-entity is subtype of relation fromType', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Org', 'Organization');
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineType(db, 'ExecutableResource', 'Executable Resource', { parentType: 'Resource' });
      await om.defineType(db, 'ApiService', 'ApiService', { parentType: 'ExecutableResource' });

      await om.defineAttribute(db, 'Org', 'name', 'String', true);
      await om.defineAttribute(db, 'Resource', 'name', 'String', true);

      // Relation: Resource -> Org
      await om.defineRelation(db, 'owned_by', 'Resource', 'Org', true);

      await om.createEntity(db, 'org:1', 'Org', 'Acme Corp');
      await om.setProperty(db, 'org:1', 'name', 'Acme Corp');

      await om.createEntity(db, 'srv:1', 'ApiService', 'Web ApiService 1');
      await om.setProperty(db, 'srv:1', 'name', 'Web ApiService 1');

      // ApiService is subtype of Resource, so linking ApiService -> Org via owned_by should pass
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
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineType(db, 'ExecutableResource', 'Executable Resource', { parentType: 'Resource' });
      await om.defineType(db, 'ApiService', 'ApiService', { parentType: 'ExecutableResource' });

      await om.defineAttribute(db, 'Resource', 'name', 'String', true);

      // Relation: Resource -> Resource (self-referencing on base type)
      await om.defineRelation(db, 'depends_on', 'Resource', 'Resource', true);

      await om.createEntity(db, 'srv:1', 'ApiService', 'Web ApiService');
      await om.setProperty(db, 'srv:1', 'name', 'Web ApiService');

      await om.createEntity(db, 'resource:1', 'Resource', 'Root Resource');
      await om.setProperty(db, 'resource:1', 'name', 'Root Resource');

      // ApiService -> Resource via depends_on: both are subtypes of Resource
      await om.linkEntities(db, 'srv:1', 'depends_on', 'resource:1');

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
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineType(db, 'ExecutableResource', 'Executable Resource', { parentType: 'Resource' });
      await om.defineType(db, 'ApiService', 'ApiService', { parentType: 'ExecutableResource' });
      await om.defineType(db, 'Dataset', 'Dataset', { parentType: 'Resource' });

      await om.defineAttribute(db, 'Resource', 'name', 'String', true);

      // Relation expects ApiService -> ApiService
      await om.defineRelation(db, 'replicates', 'ApiService', 'ApiService', true);

      await om.createEntity(db, 'v:1', 'Dataset', 'Truck A');
      await om.setProperty(db, 'v:1', 'name', 'Truck A');

      await om.createEntity(db, 'srv:1', 'ApiService', 'DB ApiService');
      await om.setProperty(db, 'srv:1', 'name', 'DB ApiService');

      // Dataset -> ApiService should fail: Dataset is not a subtype of ApiService
      await expect(
        om.linkEntities(db, 'v:1', 'replicates', 'srv:1')
      ).rejects.toThrow(/expects ApiService -> ApiService, got Dataset -> ApiService/);
    } finally {
      db.close();
    }
  });

  test('linkEntities rejects mismatched to-type', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineType(db, 'ExecutableResource', 'Executable Resource', { parentType: 'Resource' });
      await om.defineType(db, 'ApiService', 'ApiService', { parentType: 'ExecutableResource' });
      await om.defineType(db, 'Dataset', 'Dataset', { parentType: 'Resource' });

      await om.defineAttribute(db, 'Resource', 'name', 'String', true);

      // Relation expects ApiService -> ApiService
      await om.defineRelation(db, 'replicates', 'ApiService', 'ApiService', true);

      await om.createEntity(db, 'srv:1', 'ApiService', 'DB ApiService');
      await om.setProperty(db, 'srv:1', 'name', 'DB ApiService');

      await om.createEntity(db, 'v:1', 'Dataset', 'Truck A');
      await om.setProperty(db, 'v:1', 'name', 'Truck A');

      // ApiService -> Dataset should fail: Dataset is not a subtype of ApiService
      await expect(
        om.linkEntities(db, 'srv:1', 'replicates', 'v:1')
      ).rejects.toThrow(/expects ApiService -> ApiService, got ApiService -> Dataset/);
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
