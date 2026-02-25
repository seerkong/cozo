<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import GraphView from "./components/GraphView.vue";
import {
  buildWiki,
  callTool,
  graphFromAny,
  impactOfChange,
  indexEmbeddings,
  indexRepo,
  overviewGraph,
  queryNamed,
  semanticSearch,
  status,
  symbolContext,
  tools,
  type GraphEdge,
  type GraphNode,
} from "./lib/api";

type Tab = "search" | "graph" | "wiki" | "query" | "tools";

const activeTab = ref<Tab>("search");
const busy = ref(false);
const error = ref("");
const serverStatus = ref<Record<string, unknown>>({});
const toolList = ref<any[]>([]);
const repoPath = ref("");
const query = ref("");
const symbolId = ref("");
const includeCode = ref(true);
const includeDocs = ref(true);
const namedQuery = ref("code_impact");
const namedParams = ref("{\"symbolId\":\"\"}");
const toolName = ref("semantic_search");
const toolArgs = ref("{\"query\":\"SampleService\",\"limit\":\"5\"}");
const result = ref<Record<string, unknown> | null>(null);
const graphNodes = ref<GraphNode[]>([]);
const graphEdges = ref<GraphEdge[]>([]);

const prettyResult = computed(() => JSON.stringify(result.value ?? {}, null, 2));
const statusRows = computed(() => Object.entries(serverStatus.value));
const selectedCategories = computed(() => {
  const categories: string[] = [];
  if (includeCode.value) categories.push("code");
  if (includeDocs.value) categories.push("docs");
  return categories.length > 0 ? categories : ["code", "docs"];
});

async function run<T>(action: () => Promise<T>, useGraph = false) {
  busy.value = true;
  error.value = "";
  try {
    const data = await action();
    result.value = data as Record<string, unknown>;
    if (useGraph) {
      const graph = graphFromAny(data);
      graphNodes.value = graph.nodes;
      graphEdges.value = graph.edges;
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err);
  } finally {
    busy.value = false;
  }
}

async function refreshStatus() {
  await run(async () => {
    const [s, t] = await Promise.all([status(), tools()]);
    serverStatus.value = s;
    toolList.value = t.tools ?? [];
    repoPath.value ||= String(s.workDirectory ?? "");
    return s;
  });
}

async function doIndex() {
  await run(() => indexRepo(repoPath.value || String(serverStatus.value.workDirectory ?? "")));
}

async function doEmbeddings() {
  await run(() => indexEmbeddings({ includeSymbols: true, includeDocs: true, limit: "1000" }));
}

async function doSearch() {
  await run(() => semanticSearch(query.value || "SampleService", 10, selectedCategories.value), true);
  activeTab.value = "search";
}

async function doOverview() {
  await run(() => overviewGraph({ categories: selectedCategories.value, maxNodes: 400, maxEdges: 900 }), true);
  activeTab.value = "graph";
}

async function doContext() {
  await run(() => symbolContext(symbolId.value), true);
  activeTab.value = "graph";
}

async function doImpact() {
  await run(() => impactOfChange(symbolId.value), true);
  activeTab.value = "graph";
}

async function doWiki() {
  await run(() => buildWiki(undefined, false));
  activeTab.value = "wiki";
}

async function doNamedQuery() {
  await run(() => queryNamed(namedQuery.value, JSON.parse(namedParams.value || "{}")), true);
  activeTab.value = "query";
}

async function doTool() {
  await run(() => callTool({ name: toolName.value, arguments: JSON.parse(toolArgs.value || "{}") }), true);
  activeTab.value = "tools";
}

onMounted(refreshStatus);
</script>

<template>
  <main class="shell">
    <aside class="sidebar">
      <div class="brand">Cozo Wiki</div>
      <button class="primary" :disabled="busy" @click="refreshStatus">Refresh</button>

      <section class="side-section">
        <h2>Status</h2>
        <dl>
          <template v-for="[key, value] in statusRows" :key="key">
            <dt>{{ key }}</dt>
            <dd>{{ value }}</dd>
          </template>
        </dl>
      </section>

      <section class="side-section">
        <h2>Repo</h2>
        <input v-model="repoPath" placeholder="Repository path" />
        <button :disabled="busy" @click="doIndex">Index Repo</button>
        <button :disabled="busy" @click="doEmbeddings">Index Embeddings</button>
      </section>
    </aside>

    <section class="workspace">
      <header class="toolbar">
        <input v-model="query" class="search" placeholder="Search code, docs, symbols" @keyup.enter="doSearch" />
        <label class="check-control">
          <input v-model="includeCode" type="checkbox" />
          <span>Code</span>
        </label>
        <label class="check-control">
          <input v-model="includeDocs" type="checkbox" />
          <span>Docs</span>
        </label>
        <button class="primary" :disabled="busy" @click="doSearch">Search</button>
        <button :disabled="busy" @click="doOverview">Overview</button>
        <button :disabled="busy" @click="doWiki">Build Wiki</button>
      </header>

      <div v-if="error" class="error">{{ error }}</div>

      <nav class="tabs">
        <button :class="{ active: activeTab === 'search' }" @click="activeTab = 'search'">Search</button>
        <button :class="{ active: activeTab === 'graph' }" @click="activeTab = 'graph'">Graph</button>
        <button :class="{ active: activeTab === 'wiki' }" @click="activeTab = 'wiki'">Wiki</button>
        <button :class="{ active: activeTab === 'query' }" @click="activeTab = 'query'">Named Query</button>
        <button :class="{ active: activeTab === 'tools' }" @click="activeTab = 'tools'">Tools</button>
      </nav>

      <section v-show="activeTab === 'search'" class="panel">
        <div class="split">
          <div class="stack">
            <h1>Search</h1>
            <pre>{{ prettyResult }}</pre>
          </div>
          <GraphView :nodes="graphNodes" :edges="graphEdges" />
        </div>
      </section>

      <section v-show="activeTab === 'graph'" class="panel">
        <div class="controls">
          <input v-model="symbolId" placeholder="Symbol id" />
          <button :disabled="busy || !symbolId" @click="doContext">Context</button>
          <button :disabled="busy || !symbolId" @click="doImpact">Impact</button>
          <button :disabled="busy" @click="doOverview">Overview</button>
        </div>
        <GraphView :nodes="graphNodes" :edges="graphEdges" />
      </section>

      <section v-show="activeTab === 'wiki'" class="panel">
        <h1>Wiki</h1>
        <button :disabled="busy" @click="doWiki">Build Preview</button>
        <pre>{{ prettyResult }}</pre>
      </section>

      <section v-show="activeTab === 'query'" class="panel">
        <h1>Named Query</h1>
        <div class="controls">
          <input v-model="namedQuery" placeholder="Query name" />
          <button :disabled="busy" @click="doNamedQuery">Run</button>
        </div>
        <textarea v-model="namedParams" spellcheck="false"></textarea>
        <pre>{{ prettyResult }}</pre>
      </section>

      <section v-show="activeTab === 'tools'" class="panel">
        <h1>Tools</h1>
        <div class="controls">
          <select v-model="toolName">
            <option v-for="tool in toolList" :key="tool.name" :value="tool.name">{{ tool.name }}</option>
          </select>
          <button :disabled="busy" @click="doTool">Call</button>
        </div>
        <textarea v-model="toolArgs" spellcheck="false"></textarea>
        <pre>{{ prettyResult }}</pre>
      </section>
    </section>
  </main>
</template>
