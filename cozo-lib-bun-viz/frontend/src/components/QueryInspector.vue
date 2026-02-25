<script setup lang="ts">
import { ref } from "vue";

const props = defineProps<{
  meaning: string;
  dsl: string;
}>();

const open = ref(false);
</script>

<template>
  <div class="query-inspector">
    <button class="toggle-btn" @click="open = !open" data-testid="query-inspector-toggle">
      {{ open ? "▼" : "▶" }} 查询说明 / DSL
    </button>
    <div v-if="open" class="panel">
      <div class="section">
        <div class="label">含义</div>
        <div class="value">{{ props.meaning || "—" }}</div>
      </div>
      <div class="section">
        <div class="label">DSL / 代码</div>
        <pre class="code">{{ props.dsl || "—" }}</pre>
      </div>
    </div>
  </div>
</template>

<style scoped>
.query-inspector {
  margin-bottom: 8px;
}
.toggle-btn {
  background: none;
  border: 1px solid #d1d5db;
  border-radius: 4px;
  padding: 4px 12px;
  cursor: pointer;
  font-size: 13px;
  color: #374151;
}
.toggle-btn:hover {
  background: #f3f4f6;
}
.panel {
  margin-top: 6px;
  padding: 10px 14px;
  background: #f9fafb;
  border: 1px solid #e5e7eb;
  border-radius: 6px;
}
.section {
  margin-bottom: 8px;
}
.section:last-child {
  margin-bottom: 0;
}
.label {
  font-size: 11px;
  font-weight: 600;
  color: #6b7280;
  text-transform: uppercase;
  margin-bottom: 2px;
}
.value {
  font-size: 13px;
  color: #111827;
}
.code {
  font-family: "SF Mono", "Fira Code", monospace;
  font-size: 12px;
  background: #1f2937;
  color: #e5e7eb;
  padding: 8px 12px;
  border-radius: 4px;
  overflow-x: auto;
  margin: 0;
  white-space: pre-wrap;
  word-break: break-all;
}
</style>
