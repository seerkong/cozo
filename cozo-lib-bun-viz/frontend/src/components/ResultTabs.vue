<script setup lang="ts">
import { ref } from "vue";
import type { TreeNode, GraphNode, GraphEdge } from "../lib/api";
import ResultTable from "./ResultTable.vue";
import TreeView from "./TreeView.vue";
import GraphView from "./GraphView.vue";
import SchemaGraphView from "./SchemaGraphView.vue";

const props = defineProps<{
  activeTab: "schema" | "table" | "tree" | "graph";
  tableColumns: string[];
  tableRows: Record<string, any>[];
  treeNodes: TreeNode[];
  graphNodes: GraphNode[];
  graphEdges: GraphEdge[];
  tables: { name: string; columns: string[]; rows: Record<string, any>[] }[];
}>();

const emit = defineEmits<{
  (e: "update:activeTab", tab: "schema" | "table" | "tree" | "graph"): void;
}>();

const tabs = [
  { key: "schema" as const, label: "对象关系" },
  { key: "table" as const, label: "列表" },
  { key: "tree" as const, label: "树" },
  { key: "graph" as const, label: "图" },
];
</script>

<template>
  <div class="result-tabs">
    <div class="tab-bar">
      <button
        v-for="tab in tabs"
        :key="tab.key"
        class="tab-btn"
        :class="{ active: props.activeTab === tab.key }"
        @click="emit('update:activeTab', tab.key)"
        :data-testid="`tab-${tab.key}`"
      >
        {{ tab.label }}
      </button>
    </div>
    <div class="tab-content">
      <SchemaGraphView
        v-if="props.activeTab === 'schema'"
        :tables="props.tables"
      />
      <ResultTable v-else-if="props.activeTab === 'table'" :columns="props.tableColumns" :rows="props.tableRows" />
      <TreeView v-else-if="props.activeTab === 'tree'" :nodes="props.treeNodes" />
      <GraphView v-else :nodes="props.graphNodes" :edges="props.graphEdges" />
    </div>
  </div>
</template>

<style scoped>
.result-tabs { display: flex; flex-direction: column; height: 100%; }
.tab-bar { display: flex; gap: 0; border-bottom: 2px solid #e5e7eb; flex-shrink: 0; }
.tab-btn {
  padding: 8px 20px; border: none; background: none; cursor: pointer;
  font-size: 13px; font-weight: 500; color: #6b7280;
  border-bottom: 2px solid transparent; margin-bottom: -2px; transition: all 0.15s;
}
.tab-btn:hover { color: #111827; background: #f9fafb; }
.tab-btn.active { color: #059669; border-bottom-color: #059669; }
.tab-content { flex: 1; min-height: 0; overflow: hidden; }
</style>
