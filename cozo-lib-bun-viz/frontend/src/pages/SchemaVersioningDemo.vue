<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { API_BASE } from "../lib/api";

type JsonValue = any;

type SchemaState = {
  currentVersion?: number;
  checksum?: string | null;
  [k: string]: JsonValue;
};

type SchemaVersionRow = {
  version?: number;
  label?: string;
  createdAt?: string;
  created_at?: string;
  appliedAt?: string;
  applied_at?: string;
  checksum?: string | null;
  [k: string]: JsonValue;
};

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, init);
  const text = await res.text();
  let data: any;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = text;
  }
  if (!res.ok) {
    const msg = typeof data === "string" ? data : JSON.stringify(data);
    throw new Error(`${res.status} ${res.statusText}${msg ? `: ${msg}` : ""}`);
  }
  return data as T;
}

function prettyJson(v: any): string {
  try {
    return JSON.stringify(v, null, 2);
  } catch {
    return String(v);
  }
}

const stateStatus = ref("Idle");
const versionsStatus = ref("Idle");
const diffStatus = ref("Idle");
const applyStatus = ref("Idle");
const rollbackStatus = ref("Idle");

const state = ref<SchemaState | null>(null);
const versions = ref<SchemaVersionRow[]>([]);
const diffResp = ref<any>(null);
const applyResp = ref<any>(null);
const rollbackResp = ref<any>(null);

const diffFrom = ref<number>(1);
const diffTo = ref<number>(1);

const rollbackTargetVersion = ref<number>(1);
const rollbackStrict = ref(true);

const defaultMigrationSpecText = `{
  "migrationId": "mig:demo:1-2",
  "migration_id": "mig:demo:1-2",
  "fromVersion": 1,
  "from_version": 1,
  "toVersion": 2,
  "to_version": 2,
  "label": "v2: add Employee + rename org_unit -> department",
  "strict": true,
  "steps": [
    {
      "kind": "addType",
      "type": "addType",
      "typeName": "Employee",
      "type_name": "Employee",
      "description": "Employee"
    },
    {
      "kind": "addAttribute",
      "type": "addAttribute",
      "typeName": "Employee",
      "type_name": "Employee",
      "attrName": "org_unit",
      "attr_name": "org_unit",
      "valueType": "String",
      "value_type": "String",
      "required": false
    },
    {
      "kind": "renameAttribute",
      "type": "renameAttribute",
      "typeName": "Employee",
      "type_name": "Employee",
      "fromAttr": "org_unit",
      "from_attr": "org_unit",
      "toAttr": "department",
      "to_attr": "department"
    }
  ]
}`;

const migrationSpecText = ref(defaultMigrationSpecText);

const versionOptions = computed(() => {
  const seen = new Set<number>();
  const out: number[] = [];
  for (const v of versions.value) {
    const ver = Number((v as any)?.version);
    if (Number.isFinite(ver) && !seen.has(ver)) {
      seen.add(ver);
      out.push(ver);
    }
  }
  out.sort((a, b) => a - b);
  return out;
});

function coerceVersion(val: unknown, fallback: number): number {
  const n = typeof val === "number" ? val : Number(val);
  return Number.isFinite(n) ? n : fallback;
}

async function loadState() {
  stateStatus.value = "Loading...";
  try {
    state.value = await fetchJson<SchemaState>("/api/schema/state");
    const cv = coerceVersion(state.value?.currentVersion, diffFrom.value);
    // Keep controls in a sane range.
    if (versionOptions.value.length) {
      diffFrom.value = versionOptions.value[0] ?? cv;
      diffTo.value = cv;
      rollbackTargetVersion.value = cv;
    } else {
      diffFrom.value = cv;
      diffTo.value = cv;
      rollbackTargetVersion.value = cv;
    }
    stateStatus.value = "OK";
  } catch (err: any) {
    stateStatus.value = `Error: ${err?.message ?? err}`;
  }
}

async function loadVersions() {
  versionsStatus.value = "Loading...";
  try {
    const data = await fetchJson<{ versions: SchemaVersionRow[] }>("/api/schema/versions");
    versions.value = Array.isArray(data?.versions) ? data.versions : [];

    const cv = coerceVersion(state.value?.currentVersion, 1);
    if (versionOptions.value.length) {
      diffFrom.value = versionOptions.value[0] ?? 1;
      diffTo.value = versionOptions.value.includes(cv) ? cv : versionOptions.value[versionOptions.value.length - 1];
      rollbackTargetVersion.value = versionOptions.value.includes(1) ? 1 : (versionOptions.value[0] ?? 1);
    }

    versionsStatus.value = `OK (${versions.value.length})`;
  } catch (err: any) {
    versionsStatus.value = `Error: ${err?.message ?? err}`;
  }
}

async function runDiff() {
  diffStatus.value = "Loading...";
  diffResp.value = null;
  try {
    diffResp.value = await fetchJson<{ diff: any }>("/api/schema/diff", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ fromVersion: diffFrom.value, toVersion: diffTo.value }),
    });
    diffStatus.value = "OK";
  } catch (err: any) {
    diffStatus.value = `Error: ${err?.message ?? err}`;
  }
}

async function applyMigration() {
  applyStatus.value = "Applying...";
  applyResp.value = null;
  try {
    const spec = JSON.parse(migrationSpecText.value);
    applyResp.value = await fetchJson<any>("/api/schema/apply", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ spec }),
    });
    applyStatus.value = "OK";
    // Refresh state+versions after apply.
    await loadState();
    await loadVersions();
  } catch (err: any) {
    applyStatus.value = `Error: ${err?.message ?? err}`;
  }
}

async function runRollback() {
  rollbackStatus.value = "Rolling back...";
  rollbackResp.value = null;
  try {
    rollbackResp.value = await fetchJson<any>("/api/schema/rollback", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ targetVersion: rollbackTargetVersion.value, strict: rollbackStrict.value }),
    });
    rollbackStatus.value = "OK";
    await loadState();
    await loadVersions();
  } catch (err: any) {
    rollbackStatus.value = `Error: ${err?.message ?? err}`;
  }
}

function resetMigrationSpec() {
  migrationSpecText.value = defaultMigrationSpecText;
}

onMounted(async () => {
  await loadState();
  await loadVersions();
});
</script>

<template>
  <div class="page">
    <div class="header">
      <h2 class="title">Schema</h2>
      <div class="subtitle">Schema state, versions, diff, apply, rollback</div>
    </div>

    <div class="grid">
      <div class="card">
        <div class="card-title">Current schema state</div>
        <div class="row">
          <button class="btn" @click="loadState" data-testid="schema-state-refresh">Refresh</button>
          <span class="status" data-testid="schema-status-state">{{ stateStatus }}</span>
        </div>
        <div class="kv" v-if="state">
          <div class="kv-item">
            <div class="kv-k">currentVersion</div>
            <div class="kv-v" data-testid="schema-current-version">{{ state.currentVersion ?? "—" }}</div>
          </div>
          <div class="kv-item">
            <div class="kv-k">checksum</div>
            <div class="kv-v mono">{{ state.checksum ?? "null" }}</div>
          </div>
        </div>
        <pre class="json" v-if="state" data-testid="schema-state-json">{{ prettyJson(state) }}</pre>
        <div v-else class="empty">No state loaded</div>
      </div>

      <div class="card">
        <div class="card-title">Versions list</div>
        <div class="row">
          <button class="btn" @click="loadVersions" data-testid="schema-versions-refresh">Refresh</button>
          <span class="status" data-testid="schema-status-versions">{{ versionsStatus }}</span>
        </div>

        <div class="table-wrap" v-if="versions.length > 0">
          <table class="simple-table" data-testid="schema-versions-table">
            <thead>
              <tr>
                <th>version</th>
                <th>label</th>
                <th>created/applied</th>
                <th>checksum</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(v, idx) in versions" :key="String(v.version ?? idx)">
                <td class="mono">{{ v.version ?? "—" }}</td>
                <td>{{ v.label ?? "—" }}</td>
                <td class="mono">{{ v.appliedAt ?? v.applied_at ?? v.createdAt ?? v.created_at ?? "—" }}</td>
                <td class="mono">{{ v.checksum ?? "—" }}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <pre class="json" v-else data-testid="schema-versions-json">{{ prettyJson({ versions }) }}</pre>
      </div>

      <div class="card">
        <div class="card-title">Diff viewer</div>
        <div class="controls">
          <div class="control">
            <label>From</label>
            <select v-if="versionOptions.length > 0" v-model.number="diffFrom" data-testid="schema-diff-from">
              <option v-for="v in versionOptions" :key="`from-${v}`" :value="v">v{{ v }}</option>
            </select>
            <input v-else type="number" v-model.number="diffFrom" min="1" data-testid="schema-diff-from" />
          </div>
          <div class="control">
            <label>To</label>
            <select v-if="versionOptions.length > 0" v-model.number="diffTo" data-testid="schema-diff-to">
              <option v-for="v in versionOptions" :key="`to-${v}`" :value="v">v{{ v }}</option>
            </select>
            <input v-else type="number" v-model.number="diffTo" min="1" data-testid="schema-diff-to" />
          </div>
          <button class="btn primary" @click="runDiff" data-testid="schema-diff-run">Run diff</button>
          <span class="status" data-testid="schema-status-diff">{{ diffStatus }}</span>
        </div>
        <pre class="json" v-if="diffResp" data-testid="schema-diff-json">{{ prettyJson(diffResp) }}</pre>
        <div v-else class="empty">No diff yet</div>
      </div>

      <div class="card">
        <div class="card-title">Apply migration</div>
        <div class="hint">
          POST <span class="mono">/api/schema/apply</span> with <span class="mono">{ spec }</span>. Spec is a JSON object.
        </div>
        <textarea
          class="spec"
          v-model="migrationSpecText"
          spellcheck="false"
          data-testid="schema-migration-spec"
        />
        <div class="row">
          <button class="btn" @click="resetMigrationSpec">Reset default</button>
          <button class="btn primary" @click="applyMigration" data-testid="schema-apply-run">Apply</button>
          <span class="status" data-testid="schema-status-apply">{{ applyStatus }}</span>
        </div>
        <pre class="json" v-if="applyResp" data-testid="schema-apply-json">{{ prettyJson(applyResp) }}</pre>
        <div v-else class="empty">No apply result yet</div>
      </div>

      <div class="card">
        <div class="card-title">Rollback</div>
        <div class="controls">
          <div class="control">
            <label>Target version</label>
            <select v-if="versionOptions.length > 0" v-model.number="rollbackTargetVersion" data-testid="schema-rollback-target">
              <option v-for="v in versionOptions" :key="`rb-${v}`" :value="v">v{{ v }}</option>
            </select>
            <input v-else type="number" v-model.number="rollbackTargetVersion" min="1" data-testid="schema-rollback-target" />
          </div>
          <label class="toggle">
            <input type="checkbox" v-model="rollbackStrict" data-testid="schema-rollback-strict" />
            <span>strict</span>
          </label>
          <button class="btn danger" @click="runRollback" data-testid="schema-rollback-run">Rollback</button>
          <span class="status" data-testid="schema-status-rollback">{{ rollbackStatus }}</span>
        </div>
        <pre class="json" v-if="rollbackResp" data-testid="schema-rollback-json">{{ prettyJson(rollbackResp) }}</pre>
        <div v-else class="empty">No rollback result yet</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.page {
  min-height: 100%;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.header {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 12px;
  padding-bottom: 8px;
  border-bottom: 1px solid #e5e7eb;
}

.title {
  margin: 0;
  font-size: 18px;
  font-weight: 700;
  color: #111827;
}

.subtitle {
  font-size: 12px;
  color: #6b7280;
}

.grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}

@media (max-width: 920px) {
  .grid {
    grid-template-columns: 1fr;
  }
}

.card {
  border: 1px solid #e5e7eb;
  background: #ffffff;
  border-radius: 8px;
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.card-title {
  font-size: 12px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: #6b7280;
}

.row {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.controls {
  display: flex;
  align-items: flex-end;
  gap: 10px;
  flex-wrap: wrap;
}

.control {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.control label {
  font-size: 12px;
  font-weight: 600;
  color: #374151;
}

.control select,
.control input[type="number"] {
  padding: 6px 8px;
  border-radius: 4px;
  border: 1px solid #d1d5db;
  background: #fff;
  font-size: 13px;
  min-width: 140px;
}

.btn {
  padding: 7px 12px;
  border-radius: 6px;
  border: 1px solid #d1d5db;
  background: #f9fafb;
  color: #111827;
  cursor: pointer;
  font-size: 13px;
  font-weight: 600;
}

.btn:hover {
  background: #f3f4f6;
}

.btn.primary {
  background: #059669;
  border-color: #059669;
  color: #fff;
}

.btn.primary:hover {
  background: #047857;
  border-color: #047857;
}

.btn.danger {
  background: #dc2626;
  border-color: #dc2626;
  color: #fff;
}

.btn.danger:hover {
  background: #b91c1c;
  border-color: #b91c1c;
}

.status {
  font-size: 12px;
  color: #6b7280;
}

.mono {
  font-family: "SF Mono", "Fira Code", ui-monospace, SFMono-Regular, Menlo, monospace;
}

.json {
  margin: 0;
  border: 1px solid #e5e7eb;
  background: #0b1220;
  color: #e5e7eb;
  border-radius: 6px;
  padding: 10px;
  font-size: 12px;
  line-height: 1.5;
  overflow: auto;
  white-space: pre;
  min-height: 120px;
}

.empty {
  border: 1px dashed #d1d5db;
  border-radius: 6px;
  padding: 10px;
  color: #9ca3af;
  font-size: 13px;
}

.kv {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px;
}

.kv-item {
  border: 1px solid #e5e7eb;
  background: #f9fafb;
  border-radius: 6px;
  padding: 8px 10px;
}

.kv-k {
  font-size: 11px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: #6b7280;
  margin-bottom: 2px;
}

.kv-v {
  font-size: 14px;
  font-weight: 700;
  color: #111827;
}

.table-wrap {
  width: 100%;
  overflow: auto;
  border: 1px solid #e5e7eb;
  border-radius: 6px;
}

.simple-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 13px;
}

.simple-table th,
.simple-table td {
  padding: 8px 10px;
  border-bottom: 1px solid #e5e7eb;
  text-align: left;
  vertical-align: top;
}

.simple-table th {
  position: sticky;
  top: 0;
  background: #f9fafb;
  color: #374151;
  font-weight: 700;
  font-size: 12px;
}

.hint {
  font-size: 13px;
  color: #374151;
}

.spec {
  width: 100%;
  min-height: 240px;
  resize: vertical;
  border-radius: 6px;
  border: 1px solid #d1d5db;
  padding: 10px;
  font-size: 12px;
  line-height: 1.5;
  font-family: "SF Mono", "Fira Code", ui-monospace, SFMono-Regular, Menlo, monospace;
  background: #f9fafb;
  color: #111827;
}

.toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: #111827;
  user-select: none;
}
</style>
