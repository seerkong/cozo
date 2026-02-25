/**
 * Shared helpers for parsing sheet data and running queries.
 */

/**
 * Parse a property value from sheet string representation back to typed value.
 * Sheets may stringify JSON/bools/numbers — we need to restore them.
 */
function parseValue(raw) {
  if (raw === undefined || raw === null) return raw;
  if (typeof raw === 'number' || typeof raw === 'boolean') return raw;
  if (typeof raw !== 'string') return raw; // already object (Json)
  if (raw === 'true') return true;
  if (raw === 'false') return false;
  const num = Number(raw);
  if (raw !== '' && !isNaN(num) && isFinite(num)) return num;
  // Try JSON parse for objects/arrays
  if ((raw.startsWith('{') && raw.endsWith('}')) || (raw.startsWith('[') && raw.endsWith(']'))) {
    try { return JSON.parse(raw); } catch (_) { /* keep as string */ }
  }
  return raw;
}

function coerceToSchemaType(raw, valueType) {
  if (raw === undefined || raw === null) return raw;

  const vt = String(valueType || '').trim();

  if (vt === 'String') {
    return typeof raw === 'string' ? raw : String(raw);
  }

  if (vt === 'Number') {
    if (typeof raw === 'number' && Number.isFinite(raw)) return raw;
    if (typeof raw === 'boolean') return raw ? 1 : 0;
    let text = String(raw).trim();
    // Strip common thousands separators and currency symbols
    text = text.replace(/,/g, '').replace(/^[$￥€£]\s*/g, '');
    const num = Number(text);
    if (text !== '' && !isNaN(num) && isFinite(num)) return num;
    return raw;
  }

  if (vt === 'Bool') {
    if (typeof raw === 'boolean') return raw;
    if (typeof raw === 'number') return raw !== 0;
    const text = String(raw).trim().toLowerCase();
    if (text === 'true' || text === '1' || text === 'yes' || text === 'y') return true;
    if (text === 'false' || text === '0' || text === 'no' || text === 'n') return false;
    return raw;
  }

  if (vt === 'Json') {
    if (raw !== null && typeof raw === 'object') return raw;
    if (typeof raw === 'string') {
      try {
        return JSON.parse(raw);
      } catch (_) {
        return raw;
      }
    }
    return raw;
  }

  return parseValue(raw);
}

/**
 * Parse sheet rows (array of objects or array of arrays with columns) into
 * the { entities, properties, edges } batch format expected by om.ingestBatch.
 */
function parseSheetsIntoBatch(sheets, attrSchema) {
  const entities = [];
  const properties = [];
  const edges = [];

  // entities sheet: { id, typeName, label }
  if (sheets.entities && Array.isArray(sheets.entities.rows)) {
    const cols = sheets.entities.columns;
    for (const row of sheets.entities.rows) {
      const obj = Array.isArray(row)
        ? Object.fromEntries(cols.map((c, i) => [c, row[i]]))
        : row;
      entities.push({ id: obj.id, typeName: obj.typeName, label: obj.label });
    }
  }

  // properties sheet: { entityId, attrName, value }
  if (sheets.properties && Array.isArray(sheets.properties.rows)) {
    const cols = sheets.properties.columns;
    for (const row of sheets.properties.rows) {
      const obj = Array.isArray(row)
        ? Object.fromEntries(cols.map((c, i) => [c, row[i]]))
        : row;

       const attrName = String(obj.attrName ?? '').trim();
       const schemaDef = attrSchema && typeof attrSchema.get === 'function' ? attrSchema.get(attrName) : undefined;
       const valueType = schemaDef && (schemaDef.valueType || schemaDef.value_type);
       const value = valueType ? coerceToSchemaType(obj.value, valueType) : parseValue(obj.value);

      properties.push({
        entityId: typeof obj.entityId === 'string' ? obj.entityId.trim() : obj.entityId,
        attrName,
        value,
      });
    }
  }

  // edges sheet: { fromId, relName, toId, props }
  if (sheets.edges && Array.isArray(sheets.edges.rows)) {
    const cols = sheets.edges.columns;
    for (const row of sheets.edges.rows) {
      const obj = Array.isArray(row)
        ? Object.fromEntries(cols.map((c, i) => [c, row[i]]))
        : row;
      let props = obj.props;
      if (typeof props === 'string') {
        try { props = JSON.parse(props); } catch (_) { props = {}; }
      }
      if (!props || typeof props !== 'object') props = {};
      edges.push({ fromId: obj.fromId, relName: obj.relName, toId: obj.toId, props });
    }
  }

  return { entities, properties, edges };
}

module.exports = { parseValue, coerceToSchemaType, parseSheetsIntoBatch };
