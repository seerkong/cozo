<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import {
  fetchPermissionModels,
  runPermissionQuery,
  type DemoTable,
  type PermissionModel,
  type PermissionQuery,
  type PermissionRunSection,
} from "../lib/api";
import ResultSection from "../components/ResultSection.vue";
import UniverWorkbook from "../components/UniverWorkbook.vue";

const models = ref<PermissionModel[]>([]);
const selectedModelId = ref("");
const selectedQueryId = ref("");
const running = ref(false);
const status = ref("加载中...");
const sections = ref<PermissionRunSection[]>([]);

const paramValues = ref<Record<string, string>>({});

const workbookRef = ref<InstanceType<typeof UniverWorkbook>>();

/** Whether the current model has editable tables */
const hasTables = computed(() => (currentModel.value?.tables?.length ?? 0) > 0);

/** Sheet names derived from current model tables */
const sheetNames = computed(() => currentModel.value?.tables?.map((t) => t.name) ?? []);

const currentModel = computed(() => models.value.find((m) => m.id === selectedModelId.value));
const currentQueries = computed(() => currentModel.value?.queries ?? []);
const currentQuery = computed<PermissionQuery | undefined>(() =>
  currentQueries.value.find((q) => q.id === selectedQueryId.value)
);

function resetQueryParams() {
  const next: Record<string, string> = {};
  for (const p of currentQuery.value?.params ?? []) {
    next[p.key] = "";
  }
  paramValues.value = next;
}

onMounted(async () => {
  try {
    models.value = await fetchPermissionModels();
    if (models.value.length > 0) {
      selectedModelId.value = models.value[0].id;
    }
    status.value = "就绪";
  } catch (err: any) {
    status.value = `Failed to load permission models: ${err?.message ?? err}`;
  }
});

watch(selectedModelId, (id) => {
  const model = models.value.find((m) => m.id === id);
  if (!model) return;
  selectedQueryId.value = model.queries[0]?.id ?? "";
  sections.value = [];
  status.value = `Loaded: ${model.label}`;

  // Load model tables into workbook if available
  if (model.tables && model.tables.length > 0) {
    setTimeout(() => {
      workbookRef.value?.loadWorkbook(model.tables!);
    }, 0);
  }
});

/**
 * E2E test helper: reload workbook with custom tables.
 * Usage from Playwright: await page.evaluate(tables => window.__permReloadTables(tables), [...])
 */
if (typeof window !== "undefined") {
  (window as any).__permReloadTables = (tables: import("../lib/api").DemoTable[]) => {
    workbookRef.value?.loadWorkbook(tables);
  };
}

watch(selectedQueryId, () => {
  resetQueryParams();
  sections.value = [];
});

async function handleRun() {
  if (!selectedModelId.value || !selectedQueryId.value) {
    status.value = "请选择模型和查询";
    return;
  }

  for (const p of currentQuery.value?.params ?? []) {
    if (p.required && !String(paramValues.value[p.key] ?? "").trim()) {
      status.value = `参数缺失: ${p.label}`;
      return;
    }
  }

  running.value = true;
  status.value = "Running...";
  sections.value = [];
  try {
    // Read edited tables from workbook if model has tables
    let tables: DemoTable[] | undefined;
    if (hasTables.value) {
      const editedTables = workbookRef.value?.readTables() ?? [];
      const schemaByName = Object.fromEntries(
        (currentModel.value?.tables ?? []).map((t) => [t.name, t.schema])
      );
      tables = editedTables.map((t) => ({
        ...t,
        schema: schemaByName[t.name],
      }));
    }

    const resp = await runPermissionQuery({
      modelId: selectedModelId.value,
      queryId: selectedQueryId.value,
      params: paramValues.value,
      tables,
    });
    if (resp.status === "error") {
      status.value = `Error: ${resp.error}`;
      return;
    }

    sections.value = resp.sections ?? [];
    let rowCount = 0;
    for (const s of sections.value) rowCount += s.table.rows.length;
    status.value = `Done (${sections.value.length} sections / ${rowCount} rows)`;
  } catch (err: any) {
    status.value = `Error: ${err?.message ?? err}`;
  } finally {
    running.value = false;
  }
}
</script>

<template>
  <div class="page">
    <div class="header">
      <h2 class="title">Permission</h2>
      <div class="subtitle">
        中文说明：本 Demo 演示“直接使用 CozoDb 跑表（:replace 写入）+ 查询（db.run）”来实现权限模型与校验；
        刻意不使用 `cozo-om` 的本体论封装能力，避免后续迭代误替换该路径。
      </div>
    </div>

    <div class="toolbar">
      <div class="toolbar-group">
        <label>模型</label>
        <select v-model="selectedModelId" data-testid="permission-model-select">
          <option v-for="m in models" :key="m.id" :value="m.id">{{ m.label }}</option>
        </select>
      </div>

      <div class="toolbar-group" v-if="currentQueries.length > 0">
        <label>查询</label>
        <select v-model="selectedQueryId" data-testid="permission-query-select">
          <option v-for="q in currentQueries" :key="q.id" :value="q.id">{{ q.label }}</option>
        </select>
      </div>

      <button class="run-btn" :disabled="running" @click="handleRun" data-testid="permission-run-button">
        {{ running ? "执行中..." : "执行" }}
      </button>

      <span class="status" data-testid="permission-status">{{ status }}</span>
    </div>

    <div class="meaning" v-if="currentQuery">
      <span class="meaning-label">含义</span>
      <span class="meaning-value">{{ currentQuery.meaning }}</span>
    </div>

    <div class="params" v-if="(currentQuery?.params?.length ?? 0) > 0">
      <div class="param-item" v-for="p in currentQuery?.params || []" :key="p.key">
        <label>
          {{ p.label }}
          <span class="required" v-if="p.required">*</span>
        </label>
        <input
          v-model="paramValues[p.key]"
          :placeholder="p.placeholder || ''"
          :data-testid="`permission-param-${p.key}`"
        />
      </div>
    </div>

    <!-- Editable tables (Univer workbook) -->
    <div class="spreadsheet-section" v-if="hasTables" data-testid="permission-workbook">
      <UniverWorkbook ref="workbookRef" :sheetNames="sheetNames" />
    </div>

    <div class="sections" data-testid="permission-sections">
      <ResultSection
        v-for="(section, idx) in sections"
        :key="section.id"
        :title="section.label"
        :query="section.query"
        :columns="section.table.columns"
        :rows="section.table.rows"
        :defaultOpen="idx === 0"
        :testId="`permission-section-${section.id}`"
      />

      <div v-if="!running && sections.length === 0" class="empty">执行后在这里显示查询结果（支持点击展开）</div>
    </div>
  </div>
</template>

<style scoped>
.page {
  min-height: 100%;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.header {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.title {
  margin: 0;
  font-size: 18px;
  font-weight: 800;
  letter-spacing: 0.2px;
}

.subtitle {
  font-size: 13px;
  color: #6b7280;
  line-height: 1.5;
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

.toolbar-group select {
  padding: 5px 8px;
  border-radius: 4px;
  border: 1px solid #d1d5db;
  background: #fff;
  font-size: 13px;
  min-width: 190px;
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

.meaning {
  border: 1px solid #e5e7eb;
  background: #f9fafb;
  border-radius: 6px;
  padding: 8px 10px;
}

.meaning-label {
  font-size: 11px;
  font-weight: 600;
  text-transform: uppercase;
  color: #6b7280;
  margin-right: 8px;
}

.meaning-value {
  font-size: 13px;
  color: #111827;
}

.params {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 10px;
}

.param-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.param-item label {
  font-size: 12px;
  font-weight: 600;
  color: #374151;
}

.required {
  color: #dc2626;
}

.param-item input {
  border: 1px solid #d1d5db;
  border-radius: 4px;
  padding: 7px 8px;
  font-size: 13px;
}

.spreadsheet-section {
  height: 300px;
  flex-shrink: 0;
}

.sections {
  flex: 0 0 auto;
  min-height: 120px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  overflow: visible;
  padding-bottom: 8px;
}

.empty {
  height: 120px;
  border: 1px dashed #d1d5db;
  border-radius: 8px;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #9ca3af;
  font-size: 13px;
}

@media (max-width: 640px) {
  .toolbar {
    flex-direction: column;
    align-items: stretch;
  }

  .toolbar-group {
    width: 100%;
  }

  .toolbar-group select {
    width: 100%;
    min-width: 0;
  }

  .status {
    margin-left: 0;
  }
}
</style>
