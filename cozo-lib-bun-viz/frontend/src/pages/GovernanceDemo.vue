<script setup lang="ts">
import { computed, nextTick, ref } from "vue";
import { RouterLink } from "vue-router";
import { API_BASE } from "../lib/api";
import type { DemoTable } from "../lib/api";
import UniverWorkbook from "../components/UniverWorkbook.vue";

type GovernanceExplainRequest = {
  subjectId: string;
  resourceId: string;
  action: string;
  context?: any;
};

type GovernanceResponse = {
  status?: "ok" | "error";
  error?: string;
  [k: string]: any;
};

const subjectId = ref("u:1");
const resourceId = ref("a:1");
const action = ref("read");
const contextJson = ref("{}");

type GovernanceTab = "query" | "prep" | "integrity";
const activeTab = ref<GovernanceTab>("query");

// --- 完整性检查（existential rules）tab state ---
type IntegrityRule = {
  ruleName: string;
  mode: string;
  message: string;
  enabled: boolean;
  spec?: any;
};
type IntegrityViolation = { rule: string; entityId: string; message: string };
type IntegrityChaseResult = {
  created: Array<{ rule: string; triggerEntityId: string; skolemId: string; rel: string; toType: string }>;
  iterations: number;
  reachedFixpoint: boolean;
  diagnostics: Array<{ ruleName: string; remainingViolations: number }>;
};

const integrityRules = ref<IntegrityRule[]>([]);
const integrityViolations = ref<IntegrityViolation[] | null>(null);
const integrityApplyResult = ref<IntegrityChaseResult | null>(null);
const integrityStatus = ref("就绪");
const integrityBusy = ref(false);

// UniverWorkbook is stateful; if it gets unmounted, its sheet data is lost.
// Mount it lazily (only after first entering the prep tab) and then keep it alive via v-show.
const prepMounted = ref(false);

const seedTables = ref<DemoTable[] | null>(null);
const seedLoading = ref(false);
const seedApplying = ref(false);
const seedWorkbookRef = ref<InstanceType<typeof UniverWorkbook>>();

const seedSheetNames = [
  "类型定义",
  "属性定义",
  "关系定义",
  "实体数据",
  "属性数据",
  "边数据",
  "权限动作",
  "权限策略",
  "路径规则",
  "ABAC 规则",
];

const status = ref("就绪");
const running = ref(false);

const seedResult = ref<any>(null);
const runResult = ref<any>(null);

function safeParseJson(input: string): { ok: true; value: any } | { ok: false; error: string } {
  try {
    const trimmed = String(input ?? "").trim();
    if (!trimmed) return { ok: true, value: undefined };
    return { ok: true, value: JSON.parse(trimmed) };
  } catch (e: any) {
    return { ok: false, error: e?.message ?? String(e) };
  }
}

function pickFirst(obj: any, keys: string[]): any {
  for (const k of keys) {
    if (obj && Object.prototype.hasOwnProperty.call(obj, k)) return obj[k];
  }
  return undefined;
}

function extractAllow(obj: any): boolean | undefined {
  if (!obj || typeof obj !== "object") return undefined;
  for (const k of ["allow", "allowed", "isAllowed"]) {
    if (typeof obj[k] === "boolean") return obj[k];
  }
  if (obj.decision) return extractAllow(obj.decision);
  return undefined;
}

async function postJson<T>(path: string, body?: any): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new Error(`${res.status} ${res.statusText}${text ? `: ${text}` : ""}`);
  }
  // Server should return JSON, but keep error readable if it doesn't.
  const contentType = res.headers.get("content-type") ?? "";
  if (!contentType.includes("application/json")) {
    const text = await res.text().catch(() => "");
    return { status: "ok", raw: text } as any;
  }
  return res.json();
}

async function loadSeedTemplate() {
  seedLoading.value = true;
  try {
    const res = await fetch(`${API_BASE}/api/governance/seed-template`);
    if (!res.ok) throw new Error(`seed-template: ${res.status}`);
    const data: any = await res.json();
    const tables = Array.isArray(data?.tables) ? (data.tables as DemoTable[]) : [];
    seedTables.value = tables;
    // Ensure UniverWorkbook is mounted before calling into it.
    await nextTick();
    seedWorkbookRef.value?.loadWorkbook(tables);

    // If backend returns canonical ids, reflect them.
    if (typeof data?.subjectId === "string" && data.subjectId) subjectId.value = data.subjectId;
    if (typeof data?.resourceId === "string" && data.resourceId) resourceId.value = data.resourceId;
    if (typeof data?.action === "string" && data.action) action.value = data.action;
  } catch (err: any) {
    status.value = `加载 seed 模板失败: ${err?.message ?? err}`;
  } finally {
    seedLoading.value = false;
  }
}

async function applySeedFromWorkbook() {
  seedApplying.value = true;
  status.value = "Seeding...";
  seedResult.value = null;
  runResult.value = null;
  try {
    const workbookTables = seedWorkbookRef.value?.readTables() ?? [];
    const hasWorkbookData = workbookTables.some((t) => Array.isArray(t?.columns) && t.columns.length > 0);
    const tables = hasWorkbookData ? workbookTables : (seedTables.value ?? workbookTables);
    const resp: any = await postJson("/api/governance/seed", { tables });
    if (resp?.status === "error" || resp?.ok === false) {
      status.value = `Seed error: ${resp?.error ?? "unknown"}`;
      seedResult.value = resp;
      return;
    }

    seedResult.value = resp;
    if (typeof resp?.subjectId === "string" && resp.subjectId) subjectId.value = resp.subjectId;
    if (typeof resp?.resourceId === "string" && resp.resourceId) resourceId.value = resp.resourceId;
    if (typeof resp?.action === "string" && resp.action) action.value = resp.action;
    status.value = "Seeded";
    // After re-seed, bring user back to query tab to run the check.
    activeTab.value = "query";
  } catch (err: any) {
    status.value = `Seed failed: ${err?.message ?? err}`;
  } finally {
    seedApplying.value = false;
  }
}

function switchTab(next: GovernanceTab) {
  activeTab.value = next;
  if (next === "prep" && !seedTables.value && !seedLoading.value) {
    prepMounted.value = true;
    void loadSeedTemplate();
    return;
  }
  if (next === "prep") {
    prepMounted.value = true;
  }
  if (next === "integrity" && !integrityRules.value.length && !integrityBusy.value) {
    void integrityLoadRules();
  }
}

async function integrityLoadRules() {
  try {
    const res = await fetch(`${API_BASE}/api/governance/integrity/rules`);
    if (!res.ok) throw new Error(`integrity/rules: ${res.status}`);
    const data: any = await res.json();
    integrityRules.value = Array.isArray(data?.rules) ? data.rules : [];
  } catch (err: any) {
    integrityStatus.value = `加载规则失败: ${err?.message ?? err}`;
  }
}

async function integritySeedDemo() {
  integrityBusy.value = true;
  integrityStatus.value = "初始化中...";
  integrityViolations.value = null;
  integrityApplyResult.value = null;
  try {
    const resp: any = await postJson("/api/governance/integrity/seed-demo");
    integrityRules.value = Array.isArray(resp?.rules) ? resp.rules : [];
    integrityStatus.value = "演示数据已初始化（含孤儿资产）";
  } catch (err: any) {
    integrityStatus.value = `初始化失败: ${err?.message ?? err}`;
  } finally {
    integrityBusy.value = false;
  }
}

async function integrityCheck() {
  integrityBusy.value = true;
  integrityStatus.value = "检测中...";
  try {
    const resp: any = await postJson("/api/governance/integrity/check", {});
    integrityViolations.value = Array.isArray(resp?.violations) ? resp.violations : [];
    integrityStatus.value = integrityViolations.value.length
      ? `检测完成：${integrityViolations.value.length} 条违例`
      : "检测完成：无违例";
  } catch (err: any) {
    integrityStatus.value = `检测失败: ${err?.message ?? err}`;
  } finally {
    integrityBusy.value = false;
  }
}

async function integrityApply() {
  integrityBusy.value = true;
  integrityStatus.value = "物化中...";
  try {
    const resp: any = await postJson("/api/governance/integrity/apply", {});
    integrityApplyResult.value = resp?.result ?? null;
    // Refresh violations so the demo shows them clearing.
    await integrityCheck();
    const created = integrityApplyResult.value?.created?.length ?? 0;
    integrityStatus.value = integrityApplyResult.value?.reachedFixpoint
      ? `物化完成：新建 ${created} 个 Skolem 实体，已收敛`
      : `物化结束：新建 ${created} 个，未收敛（达迭代上限）`;
  } catch (err: any) {
    integrityStatus.value = `物化失败: ${err?.message ?? err}`;
  } finally {
    integrityBusy.value = false;
  }
}

async function handleRun() {
  const sid = String(subjectId.value ?? "").trim();
  const rid = String(resourceId.value ?? "").trim();
  const act = String(action.value ?? "").trim();
  if (!sid || !rid || !act) {
    status.value = "请输入 subjectId/resourceId/action";
    return;
  }

  const ctx = safeParseJson(contextJson.value);
  if (!ctx.ok) {
    status.value = `Context JSON invalid: ${ctx.error}`;
    return;
  }

  const req: GovernanceExplainRequest = {
    subjectId: sid,
    resourceId: rid,
    action: act,
    context: ctx.value,
  };

  running.value = true;
  status.value = "Running...";
  runResult.value = null;
  try {
    // Primary endpoint: /api/governance/explain
    let resp: GovernanceResponse | null = null;
    try {
      resp = await postJson("/api/governance/explain", req);
    } catch (e: any) {
      // Fallback endpoint (some servers name it checkAccess)
      const msg = String(e?.message ?? e);
      if (msg.includes("404") || msg.includes("Not Found")) {
        resp = await postJson("/api/governance/checkAccess", req);
      } else {
        throw e;
      }
    }

    if (resp?.status === "error") {
      status.value = `Error: ${resp.error ?? "unknown"}`;
      runResult.value = resp;
      return;
    }

    runResult.value = resp;
    const allow = extractAllow(resp);
    status.value = allow === undefined ? "Done" : allow ? "Done (ALLOW)" : "Done (DENY)";
  } catch (err: any) {
    status.value = `Run failed: ${err?.message ?? err}`;
  } finally {
    running.value = false;
  }
}

const allowValue = computed(() => extractAllow(runResult.value));
const matchedPolicies = computed(() =>
  pickFirst(runResult.value, ["matchedPolicies", "matched_policies", "matched", "policies"])
);
const witnessPath = computed(() =>
  pickFirst(runResult.value, ["witnessPath", "witness_path", "witness", "path"])
);
const explanationObj = computed(() =>
  pickFirst(runResult.value, ["explanation", "explain", "abac", "comparisons"])
);
const fieldVisibility = computed(() =>
  pickFirst(runResult.value, ["fieldVisibility", "field_visibility", "visibility"])
);

function prettyJson(v: any): string {
  if (v === undefined) return "—";
  try {
    return JSON.stringify(v, null, 2);
  } catch {
    return String(v);
  }
}
</script>

<template>
  <div class="page">
    <div class="header">
      <div>
        <h2 class="title">Governance</h2>
        <div class="subtitle">
          中文说明：本页演示“本体论 + 权限治理（Hybrid ABAC + 关系路径 witness）”的最小闭环。
          使用流程：先到“数据准备”初始化演示数据，再到“权限查询”执行（评估 subject/action/resource 是否允许）。
        </div>
      </div>
      <div class="header-links">
        <RouterLink class="link" to="/permission">Permission</RouterLink>
        <RouterLink class="link" to="/ontology">Ontology</RouterLink>
      </div>
    </div>

    <div class="tabs">
      <button
        class="tab"
        :class="{ active: activeTab === 'prep' }"
        @click="switchTab('prep')"
        data-testid="governance-tab-prep"
      >
        数据准备
      </button>
      <button
        class="tab"
        :class="{ active: activeTab === 'query' }"
        @click="switchTab('query')"
        data-testid="governance-tab-query"
      >
        权限查询
      </button>
      <button
        class="tab"
        :class="{ active: activeTab === 'integrity' }"
        @click="switchTab('integrity')"
        data-testid="governance-tab-integrity"
      >
        完整性检查
      </button>
    </div>

    <div class="help" v-if="activeTab === 'query'">
      <div class="help-title">使用说明</div>
      <div class="help-body">
        <div>
          1) 先到“数据准备”初始化：写入一组最小演示数据（User/Asset、关系 owns、用户属性 role、以及一条 allow policy）。
          初始化成功后，status 会显示 <code>Seeded</code>。
        </div>
        <div>
          2) 填写三要素并点击 <code>执行</code>：
          <code>subjectId</code>（主体，如用户）、<code>resourceId</code>（资源，如资产）、<code>action</code>（动作，如 read）。
          成功后 status 会显示 <code>Done</code>（可能带 <code>ALLOW</code>/<code>DENY</code>）。
        </div>
        <div>
          3) 如何读结果：
          <code>Decision</code> 是最终结论（ALLOW/DENY）；
          <code>Matched Policies</code> 是命中的策略；
          <code>Witness Path</code> 是关系路径证据（例如 owns）；
          <code>ABAC Explanation</code> 展示属性比较/条件命中细节；
          <code>Field Visibility</code> 表示字段级隐藏指令（如有）。
        </div>
        <div>
          4) 什么时候用 Advanced/context：默认保持 <code>{}</code>。
          只有当你想“额外注入”ABAC 计算所需的上下文（例如临时的 subject/resource 属性）时才需要改这里。
        </div>
      </div>
    </div>

    <div v-if="prepMounted" v-show="activeTab === 'prep'" class="prep">
      <div class="prep-head">
        <div class="prep-title">数据准备（Seed）</div>
        <div class="prep-actions">
          <button
            class="btn"
            :disabled="seedLoading || seedApplying"
            @click="loadSeedTemplate"
            data-testid="governance-seed-load-template"
          >
            {{ seedLoading ? "加载中..." : "加载默认模板" }}
          </button>
          <button
            class="btn primary"
            :disabled="seedApplying || seedLoading || !seedTables"
            @click="applySeedFromWorkbook"
            data-testid="governance-seed-apply"
          >
            {{ seedApplying ? "初始化中..." : "应用并重新初始化" }}
          </button>
        </div>
      </div>
      <div class="prep-hint">
        这里展示 governance demo 的初始数据与策略。你可以直接编辑表格内容，然后点击“应用并重新初始化”，再回到“权限查询”运行检查。
      </div>
      <div class="spreadsheet" data-testid="governance-seed-workbook">
        <UniverWorkbook ref="seedWorkbookRef" :sheetNames="seedSheetNames" />
      </div>
    </div>

    <div v-if="activeTab === 'integrity'" class="prep">
      <div class="prep-head">
        <div class="prep-title">完整性检查（存在规则）</div>
        <div class="prep-actions">
          <button
            class="btn"
            :disabled="integrityBusy"
            @click="integritySeedDemo"
            data-testid="integrity-seed"
          >
            初始化演示数据
          </button>
          <button
            class="btn"
            :disabled="integrityBusy"
            @click="integrityCheck"
            data-testid="integrity-check"
          >
            运行检测
          </button>
          <button
            class="btn primary"
            :disabled="integrityBusy"
            @click="integrityApply"
            data-testid="integrity-apply"
          >
            一键物化
          </button>
          <span class="status" data-testid="integrity-status">{{ integrityStatus }}</span>
        </div>
      </div>
      <div class="prep-hint">
        中文说明：存在规则表达「每个 X 必须存在一条 R 边指向某个 Y」。
        「初始化演示数据」会定义规则 <code>asset_must_have_owner</code> 并加入两个没有 owner 的孤儿资产；
        「运行检测」列出违例；「一键物化」执行 Skolem chase，为每个违例确定性地生成占位 owner 并连边（重复执行不会产生重复对象）。
      </div>

      <div class="card">
        <div class="card-title">规则列表</div>
        <table class="integrity-table" data-testid="integrity-rules">
          <thead>
            <tr><th>规则</th><th>模式</th><th>说明</th><th>启用</th></tr>
          </thead>
          <tbody>
            <tr v-for="r in integrityRules" :key="r.ruleName">
              <td>{{ r.ruleName }}</td>
              <td>{{ r.mode }}</td>
              <td>{{ r.message || "—" }}</td>
              <td>{{ r.enabled ? "是" : "否" }}</td>
            </tr>
            <tr v-if="!integrityRules.length">
              <td colspan="4" class="muted">暂无规则，请先「初始化演示数据」</td>
            </tr>
          </tbody>
        </table>
      </div>

      <div class="card">
        <div class="card-title">违例列表</div>
        <table class="integrity-table" data-testid="integrity-violations">
          <thead>
            <tr><th>规则</th><th>实体</th><th>说明</th></tr>
          </thead>
          <tbody>
            <tr v-for="v in integrityViolations ?? []" :key="`${v.rule}:${v.entityId}`">
              <td>{{ v.rule }}</td>
              <td>{{ v.entityId }}</td>
              <td>{{ v.message || "—" }}</td>
            </tr>
            <tr v-if="integrityViolations && !integrityViolations.length">
              <td colspan="3" class="muted" data-testid="integrity-no-violations">无违例 ✓</td>
            </tr>
            <tr v-if="!integrityViolations">
              <td colspan="3" class="muted">尚未检测</td>
            </tr>
          </tbody>
        </table>
      </div>

      <div class="card" v-if="integrityApplyResult">
        <div class="card-title">物化结果（Skolem chase）</div>
        <div class="prep-hint">
          迭代 {{ integrityApplyResult.iterations }} 轮，
          {{ integrityApplyResult.reachedFixpoint ? "已收敛（不动点）" : "未收敛（达迭代上限）" }}
        </div>
        <table class="integrity-table" data-testid="integrity-created">
          <thead>
            <tr><th>规则</th><th>触发实体</th><th>Skolem 实体</th><th>关系</th></tr>
          </thead>
          <tbody>
            <tr v-for="c in integrityApplyResult.created" :key="c.skolemId">
              <td>{{ c.rule }}</td>
              <td>{{ c.triggerEntityId }}</td>
              <td>{{ c.skolemId }}</td>
              <td>{{ c.rel }}</td>
            </tr>
            <tr v-if="!integrityApplyResult.created.length">
              <td colspan="4" class="muted">本轮没有新建实体</td>
            </tr>
          </tbody>
        </table>
        <pre class="json" v-if="integrityApplyResult.diagnostics?.length" data-testid="integrity-diagnostics">{{ prettyJson(integrityApplyResult.diagnostics) }}</pre>
      </div>
    </div>

    <div class="toolbar" v-if="activeTab === 'query'">
      <div class="toolbar-group">
        <label>subjectId</label>
        <input v-model="subjectId" placeholder="u:1" data-testid="governance-subject" />
      </div>

      <div class="toolbar-group">
        <label>resourceId</label>
        <input v-model="resourceId" placeholder="a:1" data-testid="governance-resource" />
      </div>

      <div class="toolbar-group">
        <label>action</label>
        <input v-model="action" placeholder="read" data-testid="governance-action" />
      </div>

      <button class="run-btn" :disabled="running" @click="handleRun" data-testid="governance-run">
        {{ running ? "执行中..." : "执行" }}
      </button>

      <span class="status" data-testid="governance-status">{{ status }}</span>
    </div>

    <details class="advanced" data-testid="governance-advanced" v-if="activeTab === 'query'">
      <summary>高级选项：context JSON（可选）</summary>
      <div class="advanced-body">
        <div class="hint">
          中文说明：这里会作为请求体的 `context` 发送给后端。除非你要注入临时的 ABAC 属性/上下文，否则保持 `{}`。
        </div>
        <textarea
          v-model="contextJson"
          spellcheck="false"
          data-testid="governance-context"
        ></textarea>
      </div>
    </details>

    <div class="results" v-if="activeTab === 'query'">
      <div class="card">
        <div class="card-title">Decision</div>
        <div class="decision-row">
          <span
            class="decision-pill"
            :class="{
              allow: allowValue === true,
              deny: allowValue === false,
              unknown: allowValue === undefined,
            }"
            data-testid="governance-decision"
          >
            {{ allowValue === undefined ? "—" : allowValue ? "ALLOW" : "DENY" }}
          </span>
          <span class="decision-meta">
            subject=`{{ subjectId }}` resource=`{{ resourceId }}` action=`{{ action }}`
          </span>
        </div>
      </div>

      <div class="card">
        <div class="card-title">Matched Policies</div>
        <pre class="json" data-testid="governance-matched">{{ prettyJson(matchedPolicies) }}</pre>
      </div>

      <div class="card">
        <div class="card-title">Witness Path</div>
        <pre class="json" data-testid="governance-witness">{{ prettyJson(witnessPath) }}</pre>
      </div>

      <div class="card">
        <div class="card-title">ABAC Explanation</div>
        <pre class="json" data-testid="governance-explanation">{{ prettyJson(explanationObj) }}</pre>
      </div>

      <div class="card">
        <div class="card-title">Field Visibility</div>
        <pre class="json" data-testid="governance-field-visibility">{{ prettyJson(fieldVisibility) }}</pre>
      </div>

      <div class="card">
        <div class="card-title">Raw Response</div>
        <pre class="json" data-testid="governance-raw">{{ prettyJson(runResult) }}</pre>
      </div>

      <div class="card" v-if="seedResult">
        <div class="card-title">Last Seed Response</div>
        <pre class="json" data-testid="governance-seed-raw">{{ prettyJson(seedResult) }}</pre>
      </div>

      <div class="empty" v-if="!seedResult && !runResult && !running">
        先到“数据准备”初始化演示数据，再点 <code>执行</code> 查看 ALLOW/DENY 与解释信息。
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
  margin-top: 3px;
  font-size: 12px;
  color: #6b7280;
  line-height: 1.5;
}

.tabs {
  display: flex;
  gap: 8px;
}

.tab {
  appearance: none;
  border: 1px solid #e5e7eb;
  background: #fff;
  color: #374151;
  padding: 7px 10px;
  border-radius: 999px;
  font-size: 13px;
  cursor: pointer;
}

.tab.active {
  border-color: #059669;
  background: #ecfdf5;
  color: #065f46;
  font-weight: 700;
}

.help {
  border: 1px solid #e5e7eb;
  background: #fff;
  border-radius: 8px;
  padding: 10px 12px;
}

.integrity-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 13px;
}

.integrity-table th,
.integrity-table td {
  border: 1px solid #e5e7eb;
  padding: 6px 8px;
  text-align: left;
}

.integrity-table th {
  background: #f9fafb;
  color: #374151;
  font-weight: 600;
}

.integrity-table .muted,
.muted {
  color: #9ca3af;
}

.prep {
  border: 1px solid #e5e7eb;
  background: #fff;
  border-radius: 8px;
  padding: 10px 12px;
}

.prep-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.prep-title {
  font-size: 13px;
  font-weight: 800;
  color: #111827;
}

.prep-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.prep-hint {
  margin-top: 6px;
  font-size: 12px;
  color: #6b7280;
  line-height: 1.55;
}

.btn {
  padding: 8px 12px;
  background: #111827;
  color: #fff;
  border: 0;
  border-radius: 6px;
  cursor: pointer;
  font-size: 13px;
  font-weight: 600;
}

.btn:disabled {
  background: #9ca3af;
  cursor: not-allowed;
}

.btn.primary {
  background: #059669;
}

.btn.primary:hover:not(:disabled) {
  background: #047857;
}

.spreadsheet {
  margin-top: 10px;
  height: 380px;
}

.help-title {
  font-size: 13px;
  font-weight: 700;
  color: #111827;
}

.help-body {
  margin-top: 8px;
  display: flex;
  flex-direction: column;
  gap: 6px;
  font-size: 12px;
  color: #374151;
  line-height: 1.55;
}

.help-body code {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace;
  font-size: 12px;
  background: #f3f4f6;
  border: 1px solid #e5e7eb;
  padding: 1px 6px;
  border-radius: 999px;
}

.header-links {
  display: flex;
  gap: 10px;
  flex-wrap: wrap;
}

.link {
  font-size: 13px;
  color: #059669;
  text-decoration: none;
}

.link:hover {
  text-decoration: underline;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.toolbar-group {
  display: flex;
  align-items: center;
  gap: 6px;
  background: #f3f4f6;
  border: 1px solid #e5e7eb;
  border-radius: 6px;
  padding: 4px 10px;
}

.toolbar-group label {
  font-size: 12px;
  font-weight: 600;
  color: #6b7280;
}

.toolbar-group input {
  padding: 6px 8px;
  border-radius: 4px;
  border: 1px solid #d1d5db;
  background: #fff;
  font-size: 13px;
  min-width: 160px;
}

.run-btn {
  padding: 8px 24px;
  background: #059669;
  color: #fff;
  border: 0;
  border-radius: 4px;
  cursor: pointer;
  font-size: 14px;
  font-weight: 500;
}

.run-btn:hover:not(:disabled) {
  background: #047857;
}

.run-btn:disabled {
  background: #9ca3af;
  cursor: not-allowed;
}

.status {
  margin-left: auto;
  font-size: 12px;
  color: #6b7280;
}

.advanced {
  border: 1px solid #e5e7eb;
  background: #f9fafb;
  border-radius: 8px;
  padding: 10px 12px;
}

.advanced summary {
  cursor: pointer;
  font-size: 13px;
  font-weight: 600;
  color: #374151;
}

.advanced-body {
  margin-top: 8px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.hint {
  font-size: 12px;
  color: #6b7280;
}

.advanced textarea {
  width: 100%;
  min-height: 120px;
  border: 1px solid #d1d5db;
  border-radius: 6px;
  padding: 8px 10px;
  font-family: "SF Mono", "Fira Code", monospace;
  font-size: 12px;
  background: #ffffff;
}

.results {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px;
}

.card {
  border: 1px solid #e5e7eb;
  background: #ffffff;
  border-radius: 8px;
  padding: 12px;
  overflow: hidden;
}

.card-title {
  font-size: 12px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: #6b7280;
  margin-bottom: 8px;
}

.decision-row {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.decision-pill {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 6px 10px;
  border-radius: 999px;
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.03em;
  border: 1px solid #e5e7eb;
}

.decision-pill.allow {
  background: #ecfdf5;
  border-color: #a7f3d0;
  color: #065f46;
}

.decision-pill.deny {
  background: #fef2f2;
  border-color: #fecaca;
  color: #7f1d1d;
}

.decision-pill.unknown {
  background: #f9fafb;
  border-color: #e5e7eb;
  color: #6b7280;
}

.decision-meta {
  font-size: 12px;
  color: #6b7280;
}

.json {
  margin: 0;
  font-family: "SF Mono", "Fira Code", monospace;
  font-size: 12px;
  background: #1f2937;
  color: #e5e7eb;
  padding: 10px 12px;
  border-radius: 6px;
  overflow: auto;
  max-height: 280px;
  white-space: pre;
}

.empty {
  grid-column: 1 / -1;
  height: 120px;
  border: 1px dashed #d1d5db;
  border-radius: 8px;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #9ca3af;
  font-size: 13px;
}

@media (max-width: 900px) {
  .results {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 640px) {
  .toolbar {
    flex-direction: column;
    align-items: stretch;
  }

  .toolbar-group {
    width: 100%;
  }

  .toolbar-group input {
    width: 100%;
    min-width: 0;
  }

  .status {
    margin-left: 0;
  }
}
</style>
