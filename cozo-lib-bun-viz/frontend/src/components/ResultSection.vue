<script setup lang="ts">
import { ref } from "vue";
import ResultTable from "./ResultTable.vue";

const props = defineProps<{
  title: string;
  query: string;
  columns: string[];
  rows: Record<string, any>[];
  defaultOpen?: boolean;
  testId?: string;
}>();

const open = ref(Boolean(props.defaultOpen));
</script>

<template>
  <div class="section">
    <button
      class="section-header"
      @click="open = !open"
      :data-testid="props.testId || undefined"
    >
      <span class="arrow">{{ open ? "▼" : "▶" }}</span>
      <span class="title">{{ props.title }}</span>
      <span class="meta">{{ props.rows.length }} rows</span>
    </button>

    <div v-if="open" class="section-body">
      <div class="query-block">
        <div class="query-label">CozoDB 查询</div>
        <pre class="query-code">{{ props.query || "—" }}</pre>
      </div>

      <div class="table-wrap">
        <ResultTable :columns="props.columns" :rows="props.rows" />
      </div>
    </div>
  </div>
</template>

<style scoped>
.section {
  border: 1px solid #e5e7eb;
  border-radius: 8px;
  overflow: hidden;
  background: #ffffff;
}

.section-header {
  width: 100%;
  border: 0;
  border-left: 3px solid #7c3aed;
  background: #f9fafb;
  color: #374151;
  display: flex;
  align-items: center;
  gap: 8px;
  cursor: pointer;
  padding: 10px 14px;
  text-align: left;
}

.section-header:hover {
  background: #f3f4f6;
}

.arrow {
  width: 16px;
  color: #6b7280;
  flex-shrink: 0;
}

.title {
  font-size: 13px;
  font-weight: 600;
}

.meta {
  margin-left: auto;
  font-size: 12px;
  color: #6b7280;
}

.section-body {
  border-top: 1px solid #e5e7eb;
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.query-label {
  font-size: 11px;
  font-weight: 600;
  color: #6b7280;
  text-transform: uppercase;
  margin-bottom: 4px;
}

.query-code {
  margin: 0;
  font-family: "SF Mono", "Fira Code", monospace;
  font-size: 12px;
  background: #1f2937;
  color: #e5e7eb;
  padding: 8px 12px;
  border-radius: 4px;
  overflow-x: auto;
  white-space: pre-wrap;
  word-break: break-all;
}

.table-wrap {
  height: 260px;
}

@media (max-width: 640px) {
  .table-wrap {
    height: 220px;
  }
}
</style>
