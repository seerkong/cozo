<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import {
  fetchDemos,
  runQuery,
  type Demo,
  type DemoQuery,
  type DemoTable,
  type TreeNode,
  type GraphNode,
  type GraphEdge,
  type RunResponse,
} from "../lib/api";
import UniverWorkbook from "../components/UniverWorkbook.vue";
import QueryInspector from "../components/QueryInspector.vue";
import ResultTabs from "../components/ResultTabs.vue";

// ── State ──
const demos = ref<Demo[]>([]);
const selectedDemoId = ref("");
const selectedQueryId = ref("");
const status = ref("加载中...");
const running = ref(false);

// Result state
const activeTab = ref<"schema" | "table" | "tree" | "graph">("table");
const tableColumns = ref<string[]>([]);
const tableRows = ref<Record<string, any>[]>([]);
const treeNodes = ref<TreeNode[]>([]);
const graphNodes = ref<GraphNode[]>([]);
const graphEdges = ref<GraphEdge[]>([]);

// Keep a snapshot of current workbook tables (for schema graph)
const tableSnapshot = ref<DemoTable[]>([]);

// Workbook ref
const workbookRef = ref<InstanceType<typeof UniverWorkbook>>();

// ── Computed ──
const currentDemo = computed(() => demos.value.find((d) => d.id === selectedDemoId.value));
const currentQueries = computed(() => currentDemo.value?.queries ?? []);
const currentQuery = computed(() => currentQueries.value.find((q) => q.id === selectedQueryId.value));

// ── Lifecycle ──
onMounted(async () => {
  try {
    demos.value = await fetchDemos();
    if (demos.value.length > 0) {
      selectedDemoId.value = demos.value[0].id;
    }
    status.value = "就绪";
  } catch (err: any) {
    status.value = `Failed to load demos: ${err?.message ?? err}`;
  }
});

// When demo changes, reload workbook and reset query
watch(selectedDemoId, (id) => {
  if (!id) return;
  const demo = demos.value.find((d) => d.id === id);
  if (!demo) return;
  // Update dropdowns immediately; defer workbook load to avoid blocking UI
  tableSnapshot.value = demo.tables;
  setTimeout(() => {
    workbookRef.value?.loadWorkbook(demo.tables);
  }, 0);
  // Reset query selection
  if (demo.queries.length > 0) {
    selectedQueryId.value = demo.queries[0].id;
  } else {
    selectedQueryId.value = "";
  }
  // Clear results
  clearResults();
  status.value = `Loaded: ${demo.label}`;
});

// When query changes, auto-switch to its default view
watch(selectedQueryId, (qid) => {
  const q = currentQueries.value.find((qq) => qq.id === qid);
  if (q) {
    activeTab.value = q.defaultView;
  }
});

function clearResults() {
  tableColumns.value = [];
  tableRows.value = [];
  treeNodes.value = [];
  graphNodes.value = [];
  graphEdges.value = [];
}

async function handleRun() {
  if (!selectedDemoId.value || !selectedQueryId.value) {
    status.value = "Select a demo and query first";
    return;
  }
  running.value = true;
  status.value = "Running...";
  clearResults();

  try {
    const tables = workbookRef.value?.readTables() ?? [];
    // If Univer isn't ready yet, avoid clobbering the last known schema snapshot.
    if (tables.length > 0) tableSnapshot.value = tables;
    const resp: RunResponse = await runQuery({
      demoId: selectedDemoId.value,
      queryId: selectedQueryId.value,
      tables,
    });

    if (resp.status === "error") {
      status.value = `Error: ${resp.error}`;
      return;
    }

    // Populate results
    if (resp.table) {
      tableColumns.value = resp.table.columns;
      tableRows.value = resp.table.rows;
    }
    if (resp.tree) {
      treeNodes.value = resp.tree;
    }
    if (resp.graph) {
      graphNodes.value = resp.graph.nodes;
      graphEdges.value = resp.graph.edges;
    }

    // Switch to the query's default view
    const q = currentQuery.value;
    if (q) activeTab.value = q.defaultView;

    const rowCount = resp.table?.rows?.length ?? 0;
    status.value = `Done (${rowCount} rows)`;
  } catch (err: any) {
    status.value = `Error: ${err?.message ?? err}`;
  } finally {
    running.value = false;
  }
}
</script>

<template>
  <div class="page">
    <!-- Top: Controls -->
    <div class="toolbar">
      <div class="toolbar-group">
        <label>Demo</label>
        <select v-model="selectedDemoId" data-testid="demo-select">
          <option v-for="d in demos" :key="d.id" :value="d.id">{{ d.label }}</option>
        </select>
      </div>
      <div class="toolbar-group" v-if="currentQueries.length > 0">
        <label>查询</label>
        <select v-model="selectedQueryId" data-testid="query-select">
          <option v-for="q in currentQueries" :key="q.id" :value="q.id">{{ q.label }}</option>
        </select>
      </div>
      <button class="run-btn" @click="handleRun" :disabled="running" data-testid="run-button">
        {{ running ? "执行中..." : "执行" }}
      </button>
      <span class="status" data-testid="status">{{ status }}</span>
    </div>

    <!-- Query Inspector (collapsible) -->
    <QueryInspector
      v-if="currentQuery"
      :meaning="currentQuery.meaning"
      :dsl="currentQuery.dsl"
    />

    <!-- Middle: Univer Spreadsheet -->
    <div class="spreadsheet-section">
      <UniverWorkbook ref="workbookRef" />
    </div>

    <!-- Bottom: Results -->
    <div class="results-section">
      <ResultTabs
        v-model:activeTab="activeTab"
        :tables="tableSnapshot"
        :tableColumns="tableColumns"
        :tableRows="tableRows"
        :treeNodes="treeNodes"
        :graphNodes="graphNodes"
        :graphEdges="graphEdges"
      />
    </div>
  </div>
</template>

<style scoped>
.page {
  height: 100%;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  flex-shrink: 0;
}

.toolbar-group {
  display: flex;
  align-items: center;
  gap: 6px;
  background: #f3f4f6;
  border: 1px solid #e5e7eb;
  padding: 4px 10px;
  border-radius: 6px;
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
  background: white;
  font-size: 13px;
  min-width: 180px;
}

.run-btn {
  padding: 8px 24px;
  background: #059669;
  color: white;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  font-size: 14px;
  font-weight: 500;
  transition: background 0.15s;
}

.run-btn:hover:not(:disabled) {
  background: #047857;
}

.run-btn:disabled {
  background: #9ca3af;
  cursor: not-allowed;
}

.status {
  font-size: 12px;
  color: #6b7280;
  margin-left: auto;
}

.spreadsheet-section {
  height: 300px;
  flex-shrink: 0;
}

.results-section {
  flex: 1;
  min-height: 250px;
}
</style>
