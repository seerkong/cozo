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

test('server API: /api/governance/integrity/* happy path (seed-demo, rules, check, apply)', async () => {
  const { app, close } = createApp();
  try {
    // Seed the integrity demo: ontology + materialize rule + unowned resources.
    const seed = await requestJson(app, 'POST', '/api/governance/integrity/seed-demo');
    expect(seed.res.status).toBe(200);
    expect(seed.data.ok).toBe(true);
    expect(seed.data.rules.some((r) => r.ruleName === 'resource_must_have_owner')).toBe(true);

    const rules = await requestJson(app, 'GET', '/api/governance/integrity/rules');
    expect(rules.res.status).toBe(200);
    expect(Array.isArray(rules.data.rules)).toBe(true);
    const rule = rules.data.rules.find((r) => r.ruleName === 'resource_must_have_owner');
    expect(rule.mode).toBe('materialize');

    // Unowned resources violate; the owned demo resource (r:1) does not.
    const check = await requestJson(app, 'POST', '/api/governance/integrity/check', {});
    expect(check.res.status).toBe(200);
    const violatingIds = check.data.violations.map((v) => v.entityId);
    expect(violatingIds).toContain('r:unowned-1');
    expect(violatingIds).toContain('r:unowned-2');
    expect(violatingIds).not.toContain('r:1');

    // One-click chase: Skolem owners materialized, violations cleared.
    const apply = await requestJson(app, 'POST', '/api/governance/integrity/apply', {});
    expect(apply.res.status).toBe(200);
    expect(apply.data.result.reachedFixpoint).toBe(true);
    expect(apply.data.result.created.length).toBe(2);
    expect(apply.data.result.created.every((c) => c.skolemId.startsWith('skolem:'))).toBe(true);

    const recheck = await requestJson(app, 'POST', '/api/governance/integrity/check', {});
    expect(recheck.data.violations).toEqual([]);
  } finally {
    close();
  }
});

test('server API: integrity endpoints do not break the existing governance flow', async () => {
  const { app, close } = createApp();
  try {
    await requestJson(app, 'POST', '/api/governance/integrity/seed-demo');

    // The pre-existing governance flow (seed + checkAccess) is untouched.
    await requestJson(app, 'POST', '/api/governance/seed', {});
    const access = await requestJson(app, 'POST', '/api/governance/checkAccess', {
      subjectId: 'u:1',
      action: 'read',
      resourceId: 'r:1',
    });
    expect(access.res.status).toBe(200);
    expect(access.data.result.allow).toBe(true);

    // And the integrity demo state is isolated from the governance re-seed.
    const rules = await requestJson(app, 'GET', '/api/governance/integrity/rules');
    expect(rules.data.rules.length).toBeGreaterThan(0);
  } finally {
    close();
  }
});

test('server API: integrity seed-demo is a deterministic reset', async () => {
  const { app, close } = createApp();
  try {
    await requestJson(app, 'POST', '/api/governance/integrity/seed-demo');
    await requestJson(app, 'POST', '/api/governance/integrity/apply', {});
    const cleared = await requestJson(app, 'POST', '/api/governance/integrity/check', {});
    expect(cleared.data.violations).toEqual([]);

    // Re-seeding rebuilds the demo from scratch: violations are back.
    await requestJson(app, 'POST', '/api/governance/integrity/seed-demo');
    const reset = await requestJson(app, 'POST', '/api/governance/integrity/check', {});
    expect(reset.data.violations.length).toBe(2);
  } finally {
    close();
  }
});
