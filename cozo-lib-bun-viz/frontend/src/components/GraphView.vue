<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from "vue";
import { Network } from "vis-network";
import { DataSet } from "vis-data";
import type { GraphNode, GraphEdge } from "../lib/api";

import "vis-network/styles/vis-network.css";

const props = defineProps<{
  nodes: GraphNode[];
  edges: GraphEdge[];
}>();

const containerRef = ref<HTMLDivElement>();
let network: Network | null = null;

function render() {
  if (!containerRef.value) return;
  if (network) { network.destroy(); network = null; }
  if (props.nodes.length === 0) return;

  const nodesDS = new DataSet(
    props.nodes.map((n) => ({
      id: n.id,
      label: n.label,
      group: n.group,
    }))
  );
  const edgesDS = new DataSet(
    props.edges.map((e) => ({
      from: e.from,
      to: e.to,
      label: e.label ?? "",
      arrows: "to",
    }))
  );

  network = new Network(containerRef.value, { nodes: nodesDS, edges: edgesDS }, {
    layout: { improvedLayout: true },
    physics: { stabilization: { iterations: 100 } },
    nodes: {
      shape: "dot",
      size: 16,
      font: { size: 13 },
    },
    edges: {
      font: { size: 11, align: "middle" },
      smooth: { type: "cubicBezier" },
    },
    interaction: { hover: true, tooltipDelay: 200 },
  });
}

onMounted(render);
watch(() => [props.nodes, props.edges], render, { deep: true });
onUnmounted(() => { if (network) { network.destroy(); network = null; } });
</script>

<template>
  <div class="graph-view" data-testid="graph-view">
    <div v-if="nodes.length === 0" class="empty">No graph data</div>
    <div ref="containerRef" class="graph-canvas"></div>
  </div>
</template>

<style scoped>
.graph-view { height: 100%; position: relative; background: #fff; border: 1px solid #e0e0e0; border-radius: 4px; overflow: hidden; }
.graph-canvas { width: 100%; height: 100%; }
.empty { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; color: #9ca3af; font-size: 14px; z-index: 1; }
</style>
