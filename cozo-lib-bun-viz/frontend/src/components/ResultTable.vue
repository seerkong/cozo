<script setup lang="ts">
import { computed, ref } from "vue";
import {
  FlexRender,
  getCoreRowModel,
  type ColumnDef,
  type RowSelectionState,
  useVueTable,
} from "@tanstack/vue-table";

const props = defineProps<{
  columns: string[];
  rows: Record<string, any>[];
}>();

const columnDefs = computed<ColumnDef<Record<string, any>>[]>(() =>
  props.columns.map((key) => ({
    accessorKey: key,
    header: key,
    cell: (info: any) => String(info.getValue() ?? ""),
  }))
);

const rowSelection = ref<RowSelectionState>({});

const table = useVueTable({
  get data() { return props.rows; },
  get columns() { return columnDefs.value; },
  getCoreRowModel: getCoreRowModel(),
  enableRowSelection: true,
  enableMultiRowSelection: true,
  onRowSelectionChange: (updater) => {
    rowSelection.value = typeof updater === "function" ? updater(rowSelection.value) : updater;
  },
  state: {
    get rowSelection() { return rowSelection.value; },
  },
});

const selecting = ref(false);
const anchor = ref<{ row: number; col: number } | null>(null);
const focus = ref<{ row: number; col: number } | null>(null);

const normalizedRange = computed(() => {
  if (!anchor.value || !focus.value) return null;
  return {
    rowStart: Math.min(anchor.value.row, focus.value.row),
    rowEnd: Math.max(anchor.value.row, focus.value.row),
    colStart: Math.min(anchor.value.col, focus.value.col),
    colEnd: Math.max(anchor.value.col, focus.value.col),
  };
});

const isCellSelected = (ri: number, ci: number) => {
  const r = normalizedRange.value;
  if (!r) return false;
  return ri >= r.rowStart && ri <= r.rowEnd && ci >= r.colStart && ci <= r.colEnd;
};

const onCellMouseDown = (ri: number, ci: number) => {
  selecting.value = true;
  anchor.value = { row: ri, col: ci };
  focus.value = { row: ri, col: ci };
};
const onCellMouseOver = (ri: number, ci: number) => {
  if (selecting.value) focus.value = { row: ri, col: ci };
};
const onMouseUp = () => { selecting.value = false; };
</script>

<template>
  <div class="result-table-wrap" data-testid="result-table" @mouseup="onMouseUp" @mouseleave="onMouseUp">
    <div class="table-container" v-if="columns.length > 0">
      <table class="data-grid">
        <thead>
          <tr v-for="hg in table.getHeaderGroups()" :key="hg.id">
            <th v-for="header in hg.headers" :key="header.id">
              <FlexRender v-if="!header.isPlaceholder" :render="header.column.columnDef.header" :props="header.getContext()" />
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(row, ri) in table.getRowModel().rows" :key="row.id">
            <td v-for="(cell, ci) in row.getVisibleCells()" :key="cell.id"
              class="data-cell"
              :class="{ 'cell-selected': isCellSelected(ri, ci) }"
              @mousedown.prevent="onCellMouseDown(ri, ci)"
              @mouseover="onCellMouseOver(ri, ci)">
              <FlexRender :render="cell.column.columnDef.cell" :props="cell.getContext()" />
            </td>
          </tr>
        </tbody>
      </table>
    </div>
    <div class="empty-state" v-else data-testid="result-empty">暂无数据</div>
  </div>
</template>

<style scoped>
.result-table-wrap { display: flex; flex-direction: column; height: 100%; background: #fff; border: 1px solid #e0e0e0; border-radius: 4px; overflow: hidden; }
.table-container { flex: 1; overflow: auto; }
.data-grid { width: 100%; border-collapse: collapse; font-size: 13px; }
.data-grid th, .data-grid td { border: 1px solid #e5e7eb; padding: 6px 10px; text-align: left; user-select: none; }
.data-grid th { background: #f9fafb; font-weight: 600; color: #374151; position: sticky; top: 0; z-index: 1; }
.data-cell { cursor: cell; }
.cell-selected { background: #dbeafe !important; }
.empty-state { flex: 1; display: flex; align-items: center; justify-content: center; color: #9ca3af; font-size: 14px; }
</style>
