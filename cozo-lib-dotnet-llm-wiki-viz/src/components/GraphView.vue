<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from "vue";
import { Network } from "vis-network";
import { DataSet } from "vis-data";
import type { GraphEdge, GraphNode } from "../lib/api";

import "vis-network/styles/vis-network.css";

const props = defineProps<{
  nodes: GraphNode[];
  edges: GraphEdge[];
}>();

const containerRef = ref<HTMLDivElement>();
const selectedNode = ref<GraphNode | null>(null);
const modalNode = ref<GraphNode | null>(null);
let network: Network | null = null;

function openBlockModal() {
  if (selectedNode.value?.blockText) {
    modalNode.value = selectedNode.value;
  }
}

function closeBlockModal() {
  modalNode.value = null;
}

function render() {
  if (!containerRef.value) return;
  if (network) {
    network.destroy();
    network = null;
  }
  if (props.nodes.length === 0) return;

  const nodes = new DataSet(
    props.nodes.map((node) => ({
      id: node.id,
      label: node.label,
      group: node.group,
      title: node.title ?? (node.group ? `${node.group}: ${node.fullId ?? node.label}` : node.fullId ?? node.label),
      widthConstraint: { maximum: 150 },
    }))
  );
  const edges = new DataSet(
    props.edges.map((edge) => ({
      from: edge.from,
      to: edge.to,
      label: "",
      title: edge.title ?? edge.label ?? "",
      arrows: "to",
    }))
  );

  network = new Network(containerRef.value, { nodes, edges }, {
    layout: { improvedLayout: true },
    physics: {
      solver: "forceAtlas2Based",
      stabilization: { iterations: 100 },
      forceAtlas2Based: { gravitationalConstant: -48, springLength: 120, springConstant: 0.04 },
    },
    nodes: {
      shape: "dot",
      size: 13,
      borderWidth: 1.5,
      font: { size: 11, vadjust: -16 },
    },
    edges: {
      color: { color: "#9bb8ad", highlight: "#176b5d", hover: "#176b5d" },
      width: 1,
      font: { size: 0 },
      smooth: { type: "cubicBezier" },
    },
    groups: {
      query: { color: { background: "#176b5d", border: "#0d4f45" }, font: { color: "#123f39" }, size: 18 },
      doc: { color: { background: "#bad7ff", border: "#2e73d6" } },
      symbol: { color: { background: "#f7d680", border: "#b77b00" } },
      file: { color: { background: "#b9e6c8", border: "#2b8a55" } },
      source: { color: { background: "#d5dde8", border: "#7a8aa0" } },
    },
    interaction: { hover: true, tooltipDelay: 120, navigationButtons: true, keyboard: false },
  });

  network.once("stabilizationIterationsDone", () => {
    network?.fit({ animation: { duration: 180, easingFunction: "easeInOutQuad" } });
  });
  for (const delay of [250, 800, 1400]) {
    window.setTimeout(() => network?.fit({ animation: false }), delay);
  }

  network.on("click", (params) => {
    const nodeId = params.nodes[0];
    selectedNode.value = props.nodes.find((node) => node.id === nodeId) ?? null;
  });
}

onMounted(render);
watch(() => [props.nodes, props.edges], () => {
  selectedNode.value = null;
  render();
}, { deep: true });
onUnmounted(() => network?.destroy());
</script>

<template>
  <div class="graph-view" data-testid="graph-view">
    <div v-if="nodes.length === 0" class="empty">No graph data</div>
    <div ref="containerRef" class="graph-canvas"></div>
    <aside v-if="selectedNode" class="graph-detail">
      <div class="detail-head">
        <strong>{{ selectedNode.label }}</strong>
        <span v-if="selectedNode.group">{{ selectedNode.group }}</span>
      </div>
      <p v-if="selectedNode.description">{{ selectedNode.description }}</p>
      <button class="detail-action" :disabled="!selectedNode.blockText" @click="openBlockModal">Open block</button>
      <code>{{ selectedNode.fullId ?? selectedNode.id }}</code>
    </aside>

    <div v-if="modalNode" class="modal-backdrop" @click.self="closeBlockModal">
      <section class="block-modal" role="dialog" aria-modal="true" aria-label="Block content">
        <header class="modal-head">
          <div>
            <h2>{{ modalNode.label }}</h2>
            <span v-if="modalNode.group">{{ modalNode.group }}</span>
          </div>
          <button class="icon-button" aria-label="Close block modal" @click="closeBlockModal">x</button>
        </header>
        <div class="modal-body">
          <section>
            <h3>Block</h3>
            <pre class="block-content">{{ modalNode.blockText }}</pre>
          </section>
          <section>
            <h3>Full id</h3>
            <code class="modal-id">{{ modalNode.fullId ?? modalNode.id }}</code>
          </section>
          <section v-if="modalNode.meta">
            <h3>Metadata</h3>
            <pre class="metadata">{{ JSON.stringify(modalNode.meta, null, 2) }}</pre>
          </section>
        </div>
      </section>
    </div>
  </div>
</template>
