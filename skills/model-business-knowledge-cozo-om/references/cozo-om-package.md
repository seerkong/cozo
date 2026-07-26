# Runnable Cozo OM package reference

Use the public package surface:

```js
import cozoLib from 'cozo-lib-bun';
const { CozoDb, om } = cozoLib;

const db = new CozoDb();
const runtime = om.createOmRuntime(db);
await om.initSchema(runtime);
await om.defineType(runtime, 'Domain.Order', '订单');
await om.defineAttribute(runtime, 'Domain.Order', 'orderNumber', 'String', true, '订单号');
await om.defineConstraint(runtime, 'Domain.Order', 'number_required', {
  scope: 'conditional',
  when: async (ctx) => (await ctx.getProperty('orderNumber')) !== undefined,
  then: async (ctx) => String(await ctx.getProperty('orderNumber')).trim().length > 0,
  message: '订单号不得为空。',
});
```

Supported attribute value types are `String`, `Number`, `Bool`, `Json`, and `Validity`. `finalizeEntity` checks required fields; `setProperty` automatically evaluates conditional constraints and `linkEntities` automatically evaluates cross-entity constraints. Use `validateConstraints` or `validateEntity` to report all violations.

For package manifests, use an explicit local dependency:

```json
{
  "type": "module",
  "dependencies": {
    "cozo-lib-bun": "file:/absolute/path/to/cozo/cozo-lib-bun"
  }
}
```

Test with a real `CozoDb` and close it in cleanup. Assert type and relation definitions plus failures for at least one invalid business condition. Keep package-local type declarations aligned with the public import surface; do not reference a non-exported `cozo-lib-bun` subpath from TypeScript declarations.
