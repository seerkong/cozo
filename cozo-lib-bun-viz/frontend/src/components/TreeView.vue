<script setup lang="ts">
import { ref, watchEffect } from "vue";
import { BaseTree } from "@he-tree/vue";
import "@he-tree/vue/style/default.css";
import type { TreeNode } from "../lib/api";

const props = defineProps<{ nodes: TreeNode[] }>();

// he-tree uses v-model to mutate tree data (expand/collapse/drag).
// Keep an internal copy so parent result data stays immutable.
const treeData = ref<TreeNode[]>([]);

watchEffect(() => {
  treeData.value = props.nodes ? JSON.parse(JSON.stringify(props.nodes)) : [];
});
</script>

<template>
  <div class="tree-wrap" data-testid="tree-view">
    <div v-if="treeData.length === 0" class="empty">暂无树形结果</div>
    <BaseTree v-else v-model="treeData">
      <template #default="{ node }">
        <span class="node">{{ node.label }}</span>
      </template>
    </BaseTree>
  </div>
</template>

<style scoped>
.tree-wrap {
  height: 100%;
  overflow: auto;
  background: #fff;
  border: 1px solid #e0e0e0;
  border-radius: 4px;
  padding: 8px;
  box-sizing: border-box;
}
.empty {
  color: #9ca3af;
  text-align: center;
  padding: 24px;
}
.node {
  font-size: 13px;
  color: #111827;
}
</style>
