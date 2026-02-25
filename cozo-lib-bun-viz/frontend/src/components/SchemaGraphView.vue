<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from "vue";
import { DataSet } from "vis-data";
import { Network } from "vis-network";

import "vis-network/styles/vis-network.css";

type DemoTable = { name: string; columns: string[]; rows: Record<string, any>[] };

const props = defineProps<{ tables: DemoTable[] }>();

const containerRef = ref<HTMLDivElement>();
let network: Network | null = null;

function toRowObjects(table: DemoTable | undefined): Record<string, any>[] {
  if (!table) return [];
  const cols = Array.isArray(table.columns) ? table.columns : [];
  const rows = Array.isArray(table.rows) ? table.rows : [];
  return rows.map((r) => {
    if (Array.isArray(r)) {
      return Object.fromEntries(cols.map((c, i) => [c, r[i]]));
    }
    return r;
  });
}

const schema = computed(() => {
  const map: Record<string, DemoTable | undefined> = Object.create(null);
  for (const t of props.tables ?? []) {
    map[t.name] = t;
  }

  const typeRows = toRowObjects(map["类型定义"]);
  const attrRows = toRowObjects(map["属性定义"]);
  const relRows = toRowObjects(map["关系定义"]);

  const nodes: Array<{ id: string; label: string; group: string }>
    = [];
  const edges: Array<{ from: string; to: string; label: string; _inheritance?: boolean }>
    = [];

  // Type nodes + inheritance edges
  for (const t of typeRows) {
    const typeName = String(t.typeName ?? "").trim();
    if (!typeName) continue;
    nodes.push({ id: `type:${typeName}`, label: typeName, group: "type" });
  }

  const typeSet = new Set(nodes.map((n) => n.id));

  // Inheritance edges (parent_type column)
  for (const t of typeRows) {
    const typeName = String(t.typeName ?? "").trim();
    const parentType = String(t.parent_type ?? "").trim();
    if (!typeName || !parentType) continue;
    const childId = `type:${typeName}`;
    const parentId = `type:${parentType}`;
    if (!typeSet.has(parentId)) {
      nodes.push({ id: parentId, label: parentType, group: "type" });
      typeSet.add(parentId);
    }
    edges.push({ from: childId, to: parentId, label: "extends", _inheritance: true });
  }

  // Attribute nodes + edges type->attr
  for (const a of attrRows) {
    const typeName = String(a.typeName ?? "").trim();
    const attrName = String(a.attrName ?? "").trim();
    const valueType = String(a.valueType ?? "").trim();
    if (!typeName || !attrName) continue;

    const typeId = `type:${typeName}`;
    if (!typeSet.has(typeId)) {
      nodes.push({ id: typeId, label: typeName, group: "type" });
      typeSet.add(typeId);
    }

    const attrId = `attr:${typeName}.${attrName}`;
    nodes.push({ id: attrId, label: `${attrName}${valueType ? `:${valueType}` : ""}`, group: "attr" });
    edges.push({ from: typeId, to: attrId, label: "attr" });
  }

  // Relation edges between types
  for (const r of relRows) {
    const relName = String(r.relName ?? "").trim();
    const fromType = String(r.fromType ?? "").trim();
    const toType = String(r.toType ?? "").trim();
    if (!relName || !fromType || !toType) continue;

    const fromId = `type:${fromType}`;
    const toId = `type:${toType}`;
    if (!typeSet.has(fromId)) {
      nodes.push({ id: fromId, label: fromType, group: "type" });
      typeSet.add(fromId);
    }
    if (!typeSet.has(toId)) {
      nodes.push({ id: toId, label: toType, group: "type" });
      typeSet.add(toId);
    }

    edges.push({ from: fromId, to: toId, label: relName });
  }

  return { nodes, edges };
});

function render() {
  if (!containerRef.value) return;
  if (network) {
    network.destroy();
    network = null;
  }

  if (schema.value.nodes.length === 0) return;

  const nodesDS = new DataSet(
    schema.value.nodes.map((n) => ({
      id: n.id,
      label: n.label,
      group: n.group,
    }))
  );
  const edgesDS = new DataSet(
    schema.value.edges.map((e) => ({
      from: e.from,
      to: e.to,
      label: e.label,
      arrows: "to",
      ...(e._inheritance
        ? { dashes: true, color: { color: "#9ca3af", highlight: "#6b7280" }, font: { color: "#9ca3af" } }
        : {}),
    }))
  );

  network = new Network(containerRef.value, { nodes: nodesDS, edges: edgesDS }, {
    physics: { stabilization: { iterations: 120 } },
    layout: { improvedLayout: true },
    interaction: { hover: true, tooltipDelay: 150 },
    nodes: {
      shape: "dot",
      font: { size: 13 },
      color: {
        background: "#e5e7eb",
        border: "#9ca3af",
        highlight: { background: "#a7f3d0", border: "#059669" },
      },
    },
    edges: {
      font: { size: 11, align: "middle" },
      smooth: { type: "cubicBezier" },
      color: { color: "#9ca3af" },
    },
    groups: {
      type: { color: { background: "#dbeafe", border: "#2563eb" }, shape: "box" },
      attr: { color: { background: "#fef3c7", border: "#d97706" }, shape: "dot" },
    },
  });
}

onMounted(render);
watch(schema, render, { deep: true });
onUnmounted(() => {
  if (network) {
    network.destroy();
    network = null;
  }
});
</script>

<template>
  <div class="schema-graph" data-testid="schema-graph">
    <div v-if="schema.nodes.length === 0" class="empty" data-testid="schema-empty">暂无对象关系定义</div>
    <div ref="containerRef" class="canvas"></div>
  </div>
</template>

<style scoped>
.schema-graph {
  height: 100%;
  position: relative;
  background: #fff;
  border: 1px solid #e0e0e0;
  border-radius: 4px;
  overflow: hidden;
}
.canvas {
  width: 100%;
  height: 100%;
}
.empty {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #9ca3af;
  font-size: 14px;
  z-index: 1;
}
</style>
