const { describe, expect, test } = require('bun:test');
const { createTestDb, createTestDbWithHierarchy } = require('./helpers');

describe('defineType inheritance validation', () => {
  test('defineType with parentType works and persists parent_type', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineType(db, 'ITAsset', 'IT Asset', { parentType: 'Asset' });

      const ancestors = await om.getAncestors(db, 'ITAsset');
      expect(ancestors).toEqual(['Asset']);
    } finally {
      db.close();
    }
  });

  test('defineType with missing parentType throws', async () => {
    const { db, om } = await createTestDb();
    try {
      await expect(
        om.defineType(db, 'Orphan', 'Orphan', { parentType: 'NonExistent' })
      ).rejects.toThrow("Parent type 'NonExistent' does not exist");
    } finally {
      db.close();
    }
  });

  test('defineType detects circular inheritance (A->B, then B parent to A)', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'A', 'Type A');
      await om.defineType(db, 'B', 'Type B', { parentType: 'A' });

      // Attempting to set A's parent to B should detect the cycle
      await expect(
        om.defineType(db, 'A', 'Type A', { parentType: 'B' })
      ).rejects.toThrow('Circular inheritance detected');
    } finally {
      db.close();
    }
  });

  test('defineType detects self-referencing parent', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'X', 'Type X');
      await expect(
        om.defineType(db, 'X', 'Type X', { parentType: 'X' })
      ).rejects.toThrow('Circular inheritance detected');
    } finally {
      db.close();
    }
  });

  test('defineType without options preserves existing parent and mixins', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineMixin(db, 'Auditable', 'Auditable');
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineType(db, 'Server', 'Server', {
        parentType: 'Asset',
        mixins: ['Auditable'],
      });

      await om.defineType(db, 'Server', 'Server updated');

      expect(await om.getAncestors(db, 'Server')).toEqual(['Asset']);
      const hierarchy = await om.getTypeHierarchy(db);
      expect(hierarchy.types.Server.mixins).toContain('Auditable');
    } finally {
      db.close();
    }
  });
});

describe('getAncestors', () => {
  test('getAncestors(Server) returns [ITAsset, Asset]', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const ancestors = await om.getAncestors(db, 'Server');
      expect(ancestors).toEqual(['ITAsset', 'Asset']);
    } finally {
      db.close();
    }
  });

  test('getAncestors of root type returns empty array', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const ancestors = await om.getAncestors(db, 'Asset');
      expect(ancestors).toEqual([]);
    } finally {
      db.close();
    }
  });
});

describe('getDescendants', () => {
  test('getDescendants(Asset) contains ITAsset, Server, Laptop, Vehicle', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const descendants = await om.getDescendants(db, 'Asset');
      expect(descendants).toContain('ITAsset');
      expect(descendants).toContain('Server');
      expect(descendants).toContain('Laptop');
      expect(descendants).toContain('Vehicle');
      expect(descendants.length).toBe(4);
    } finally {
      db.close();
    }
  });

  test('getDescendants of leaf type returns empty array', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const descendants = await om.getDescendants(db, 'Server');
      expect(descendants).toEqual([]);
    } finally {
      db.close();
    }
  });
});

describe('isSubtypeOf', () => {
  test('isSubtypeOf(Server, Asset) is true', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const result = await om.isSubtypeOf(db, 'Server', 'Asset');
      expect(result).toBe(true);
    } finally {
      db.close();
    }
  });

  test('isSubtypeOf(Asset, Server) is false', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const result = await om.isSubtypeOf(db, 'Asset', 'Server');
      expect(result).toBe(false);
    } finally {
      db.close();
    }
  });

  test('isSubtypeOf(Server, Server) is true (identity)', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const result = await om.isSubtypeOf(db, 'Server', 'Server');
      expect(result).toBe(true);
    } finally {
      db.close();
    }
  });

  test('isSubtypeOf(Vehicle, ITAsset) is false (sibling branches)', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const result = await om.isSubtypeOf(db, 'Vehicle', 'ITAsset');
      expect(result).toBe(false);
    } finally {
      db.close();
    }
  });
});

describe('getTypeHierarchy', () => {
  test('returns roots including Asset and types include all defined types', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const hierarchy = await om.getTypeHierarchy(db);

      expect(hierarchy.roots).toContain('Asset');
      expect(hierarchy.roots.length).toBe(1);

      const typeNames = Object.keys(hierarchy.types);
      expect(typeNames).toContain('Asset');
      expect(typeNames).toContain('ITAsset');
      expect(typeNames).toContain('Server');
      expect(typeNames).toContain('Laptop');
      expect(typeNames).toContain('Vehicle');

      expect(hierarchy.types.ITAsset.parentType).toBe('Asset');
      expect(hierarchy.types.Asset.parentType).toBeNull();
      expect(hierarchy.types.Asset.children).toContain('ITAsset');
      expect(hierarchy.types.ITAsset.children).toContain('Server');
    } finally {
      db.close();
    }
  });

  test('multiple roots when hierarchy has independent trees', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Asset', 'Asset');
      await om.defineType(db, 'Person', 'Person');
      await om.defineType(db, 'Server', 'Server', { parentType: 'Asset' });

      const hierarchy = await om.getTypeHierarchy(db);
      expect(hierarchy.roots).toContain('Asset');
      expect(hierarchy.roots).toContain('Person');
      expect(hierarchy.roots.length).toBe(2);

      expect(hierarchy.types.Server.parentType).toBe('Asset');
    } finally {
      db.close();
    }
  });
});
