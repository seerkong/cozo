const { describe, expect, test } = require('bun:test');
const { createTestDb, createTestDbWithHierarchy } = require('./helpers');

describe('defineType inheritance validation', () => {
  test('defineType with parentType works and persists parent_type', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineType(db, 'ExecutableResource', 'Executable Resource', { parentType: 'Resource' });

      const ancestors = await om.getAncestors(db, 'ExecutableResource');
      expect(ancestors).toEqual(['Resource']);
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
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineType(db, 'ApiService', 'ApiService', {
        parentType: 'Resource',
        mixins: ['Auditable'],
      });

      await om.defineType(db, 'ApiService', 'ApiService updated');

      expect(await om.getAncestors(db, 'ApiService')).toEqual(['Resource']);
      const hierarchy = await om.getTypeHierarchy(db);
      expect(hierarchy.types.ApiService.mixins).toContain('Auditable');
    } finally {
      db.close();
    }
  });

  test('defineType with explicit null parentType clears the existing parent', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineType(db, 'ApiService', 'ApiService', { parentType: 'Resource' });

      await om.defineType(db, 'ApiService', 'ApiService detached', { parentType: null });

      expect(await om.getAncestors(db, 'ApiService')).toEqual([]);
      const hierarchy = await om.getTypeHierarchy(db);
      expect(hierarchy.types.ApiService.parentType).toBeNull();
      expect(hierarchy.roots).toContain('ApiService');
    } finally {
      db.close();
    }
  });
});

describe('getAncestors', () => {
  test('getAncestors(ApiService) returns [ExecutableResource, Resource]', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const ancestors = await om.getAncestors(db, 'ApiService');
      expect(ancestors).toEqual(['ExecutableResource', 'Resource']);
    } finally {
      db.close();
    }
  });

  test('getAncestors of root type returns empty array', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const ancestors = await om.getAncestors(db, 'Resource');
      expect(ancestors).toEqual([]);
    } finally {
      db.close();
    }
  });
});

describe('getDescendants', () => {
  test('getDescendants(Resource) contains ExecutableResource, ApiService, Worker, Dataset', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const descendants = await om.getDescendants(db, 'Resource');
      expect(descendants).toContain('ExecutableResource');
      expect(descendants).toContain('ApiService');
      expect(descendants).toContain('Worker');
      expect(descendants).toContain('Dataset');
      expect(descendants.length).toBe(4);
    } finally {
      db.close();
    }
  });

  test('getDescendants of leaf type returns empty array', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const descendants = await om.getDescendants(db, 'ApiService');
      expect(descendants).toEqual([]);
    } finally {
      db.close();
    }
  });
});

describe('isSubtypeOf', () => {
  test('isSubtypeOf(ApiService, Resource) is true', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const result = await om.isSubtypeOf(db, 'ApiService', 'Resource');
      expect(result).toBe(true);
    } finally {
      db.close();
    }
  });

  test('isSubtypeOf(Resource, ApiService) is false', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const result = await om.isSubtypeOf(db, 'Resource', 'ApiService');
      expect(result).toBe(false);
    } finally {
      db.close();
    }
  });

  test('isSubtypeOf(ApiService, ApiService) is true (identity)', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const result = await om.isSubtypeOf(db, 'ApiService', 'ApiService');
      expect(result).toBe(true);
    } finally {
      db.close();
    }
  });

  test('isSubtypeOf(Dataset, ExecutableResource) is false (sibling branches)', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const result = await om.isSubtypeOf(db, 'Dataset', 'ExecutableResource');
      expect(result).toBe(false);
    } finally {
      db.close();
    }
  });
});

describe('getTypeHierarchy', () => {
  test('returns roots including Resource and types include all defined types', async () => {
    const { db, om } = await createTestDbWithHierarchy();
    try {
      const hierarchy = await om.getTypeHierarchy(db);

      expect(hierarchy.roots).toContain('Resource');
      expect(hierarchy.roots.length).toBe(1);

      const typeNames = Object.keys(hierarchy.types);
      expect(typeNames).toContain('Resource');
      expect(typeNames).toContain('ExecutableResource');
      expect(typeNames).toContain('ApiService');
      expect(typeNames).toContain('Worker');
      expect(typeNames).toContain('Dataset');

      expect(hierarchy.types.ExecutableResource.parentType).toBe('Resource');
      expect(hierarchy.types.Resource.parentType).toBeNull();
      expect(hierarchy.types.Resource.children).toContain('ExecutableResource');
      expect(hierarchy.types.ExecutableResource.children).toContain('ApiService');
    } finally {
      db.close();
    }
  });

  test('multiple roots when hierarchy has independent trees', async () => {
    const { db, om } = await createTestDb();
    try {
      await om.defineType(db, 'Resource', 'Resource');
      await om.defineType(db, 'Person', 'Person');
      await om.defineType(db, 'ApiService', 'ApiService', { parentType: 'Resource' });

      const hierarchy = await om.getTypeHierarchy(db);
      expect(hierarchy.roots).toContain('Resource');
      expect(hierarchy.roots).toContain('Person');
      expect(hierarchy.roots.length).toBe(2);

      expect(hierarchy.types.ApiService.parentType).toBe('Resource');
    } finally {
      db.close();
    }
  });
});
