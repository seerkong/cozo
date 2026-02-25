<script setup lang="ts">
import { RouterLink, RouterView, useRoute } from "vue-router";
import { computed } from "vue";

const route = useRoute();
const activePage = computed(() => {
  switch (route.path) {
    case "/permission":
      return "permission";
    case "/governance":
      return "governance";
    case "/schema":
      return "schema";
    case "/ontology":
    default:
      return "ontology";
  }
});
</script>

<template>
  <div class="app">
    <div class="top-nav">
      <RouterLink
        to="/ontology"
        class="nav-btn"
        :class="{ active: activePage === 'ontology' }"
        data-testid="nav-ontology"
      >
        对象建模分析
      </RouterLink>
      <RouterLink
        to="/permission"
        class="nav-btn"
        :class="{ active: activePage === 'permission' }"
        data-testid="nav-permission"
      >
        权限模型 Demo
      </RouterLink>
      <RouterLink
        to="/governance"
        class="nav-btn"
        :class="{ active: activePage === 'governance' }"
        data-testid="nav-governance"
      >
        Governance
      </RouterLink>
      <RouterLink
        to="/schema"
        class="nav-btn"
        :class="{ active: activePage === 'schema' }"
        data-testid="nav-schema"
      >
        Schema
      </RouterLink>
    </div>

    <div class="page-wrap">
      <RouterView />
    </div>
  </div>
</template>

<style>
.app {
  width: 100%;
  height: 100%;
  padding: 16px;
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.top-nav {
  display: flex;
  gap: 0;
  border-bottom: 2px solid #e5e7eb;
  flex-shrink: 0;
}

.nav-btn {
  padding: 8px 20px;
  border: none;
  background: none;
  cursor: pointer;
  font-size: 13px;
  font-weight: 500;
  color: #6b7280;
  border-bottom: 2px solid transparent;
  margin-bottom: -2px;
  transition: all 0.15s;
  text-decoration: none;
}

.nav-btn:hover {
  color: #111827;
  background: #f9fafb;
}

.nav-btn.active {
  color: #059669;
  border-bottom-color: #059669;
}

.page-wrap {
  flex: 1;
  min-height: 0;
  overflow: auto;
}
</style>
