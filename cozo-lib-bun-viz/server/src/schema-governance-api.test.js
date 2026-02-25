const { test, expect } = require('bun:test');

const { createApp } = require('./index');

async function requestJson(app, method, path, body) {
  const init = {
    method,
    headers: body ? { 'content-type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  };
  const res = await app.handle(new Request(`http://localhost${path}`, init));
  const data = await res.json();
  return { res, data };
}

test('server API: /api/schema/* happy path (state, versions, apply, diff, rollback)', async () => {
  const { app, close } = createApp();
  try {
    const s0 = await requestJson(app, 'GET', '/api/schema/state');
    expect(s0.res.status).toBe(200);
    expect(typeof s0.data.currentVersion).toBe('number');
    expect(s0.data.currentVersion).toBe(1);
    expect(s0.data.checksum === null || typeof s0.data.checksum === 'string').toBe(true);

    const v0 = await requestJson(app, 'GET', '/api/schema/versions');
    expect(v0.res.status).toBe(200);
    expect(Array.isArray(v0.data.versions)).toBe(true);
    expect(v0.data.versions.some((x) => x && x.version === 1)).toBe(true);

    const apply = await requestJson(app, 'POST', '/api/schema/apply', {
      spec: {
        migrationId: 'mig:demo:1-2',
        fromVersion: 1,
        toVersion: 2,
        label: 'demo v2',
        steps: [{ kind: 'addType', typeName: 'Project', description: 'Project' }],
      },
    });
    expect(apply.res.status).toBe(200);
    expect(apply.data.ok).toBe(true);
    expect(apply.data.state && typeof apply.data.state === 'object').toBe(true);
    expect(apply.data.state.currentVersion).toBe(2);

    const diff = await requestJson(app, 'POST', '/api/schema/diff', { fromVersion: 1, toVersion: 2 });
    expect(diff.res.status).toBe(200);
    expect(diff.data.diff && typeof diff.data.diff === 'object').toBe(true);
    const addedTypes = diff.data.diff?.schema?.om_type?.added;
    expect(Array.isArray(addedTypes)).toBe(true);
    expect(addedTypes.length).toBeGreaterThanOrEqual(1);

    const rollback = await requestJson(app, 'POST', '/api/schema/rollback', { targetVersion: 1, strict: true });
    expect(rollback.res.status).toBe(200);
    expect(rollback.data.ok).toBe(true);
    expect(rollback.data.result && typeof rollback.data.result === 'object').toBe(true);
    expect(rollback.data.result.ok).toBe(true);
    expect(rollback.data.state && typeof rollback.data.state === 'object').toBe(true);
    expect(rollback.data.state.currentVersion).toBe(1);
  } finally {
    close();
  }
});

test('server API: /api/governance/checkAccess happy path (after seed)', async () => {
  const { app, close } = createApp();
  try {
    const seed = await requestJson(app, 'POST', '/api/governance/seed');
    expect(seed.res.status).toBe(200);
    expect(seed.data.ok).toBe(true);

    const check = await requestJson(app, 'POST', '/api/governance/checkAccess', {
      subjectId: 'u:1',
      action: 'read',
      resourceId: 'a:1',
    });
    expect(check.res.status).toBe(200);
    expect(check.data.result && typeof check.data.result === 'object').toBe(true);
    expect(check.data.result.allow).toBe(true);
    expect(Array.isArray(check.data.result.matchedPolicies)).toBe(true);
    expect(check.data.result.matchedPolicies.length).toBeGreaterThanOrEqual(1);

    const explain = await requestJson(app, 'POST', '/api/governance/explain', {
      subjectId: 'u:1',
      action: 'read',
      resourceId: 'a:1',
    });
    expect(explain.res.status).toBe(200);
    expect(explain.data.result && typeof explain.data.result === 'object').toBe(true);
    expect(explain.data.result.allow).toBe(true);
    expect(explain.data.result.explanation && typeof explain.data.result.explanation === 'object').toBe(true);
  } finally {
    close();
  }
});

test('server API: /api/governance/seed-template + custom seed affects decision', async () => {
  const { app, close } = createApp();
  try {
    const tpl = await requestJson(app, 'GET', '/api/governance/seed-template');
    expect(tpl.res.status).toBe(200);
    expect(tpl.data.ok).toBe(true);
    expect(Array.isArray(tpl.data.tables)).toBe(true);

    // Modify subject.role to something non-admin so ABAC rule fails.
    const tables = (tpl.data.tables || []).map((t) => ({ ...t, rows: Array.isArray(t.rows) ? [...t.rows] : [] }));
    const props = tables.find((t) => t.name === '属性数据');
    if (props && Array.isArray(props.rows) && props.rows.length > 0) {
      const r0 = props.rows[0];
      if (Array.isArray(r0)) {
        // columns: [entityId, attrName, value]
        props.rows[0] = [r0[0], r0[1], 'viewer'];
      } else {
        props.rows[0] = { ...r0, value: 'viewer' };
      }
    }

    const seed = await requestJson(app, 'POST', '/api/governance/seed', { tables });
    expect(seed.res.status).toBe(200);
    expect(seed.data.ok).toBe(true);

    const check = await requestJson(app, 'POST', '/api/governance/checkAccess', {
      subjectId: 'u:1',
      action: 'read',
      resourceId: 'a:1',
    });
    expect(check.res.status).toBe(200);
    expect(check.data.result && typeof check.data.result === 'object').toBe(true);
    expect(check.data.result.allow).toBe(false);
  } finally {
    close();
  }
});
