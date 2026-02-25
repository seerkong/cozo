<script setup lang="ts">
import { onMounted, onUnmounted, ref } from "vue";
import { UniverSheetsCorePreset } from "@univerjs/preset-sheets-core";
import UniverPresetSheetsCoreEnUS from "@univerjs/preset-sheets-core/locales/en-US";
import { createUniver, LocaleType, mergeLocales } from "@univerjs/presets";
import type { FUniver, FWorkbook } from "@univerjs/presets";
import type { DemoTable } from "../lib/api";

import "@univerjs/preset-sheets-core/lib/index.css";

const DEFAULT_SHEET_NAMES = ["类型定义", "属性定义", "关系定义", "实体数据", "属性数据", "边数据"];
const MAX_ROWS = 200;
const MAX_COLS = 24;

const props = withDefaults(defineProps<{ sheetNames?: string[] }>(), {
  sheetNames: undefined,
});

/** Resolve effective sheet names: prop > names derived from tables > default */
function getSheetNames(tables?: DemoTable[]): string[] {
  if (props.sheetNames && props.sheetNames.length > 0) return props.sheetNames;
  if (tables && tables.length > 0) return tables.map((t) => t.name);
  return DEFAULT_SHEET_NAMES;
}

const containerRef = ref<HTMLDivElement>();
let univerAPI: FUniver | null = null;
let workbook: FWorkbook | null = null;
const ready = ref(false);
let pendingTables: DemoTable[] | null = null;

onMounted(() => {
  // Defer heavy Univer bootstrap to let UI paint first.
  setTimeout(() => {
    if (!containerRef.value) return;
    const { univerAPI: api } = createUniver({
      locale: LocaleType.EN_US,
      locales: { [LocaleType.EN_US]: mergeLocales(UniverPresetSheetsCoreEnUS) },
      presets: [
        UniverSheetsCorePreset({
          container: containerRef.value,
          formulaBar: false,
        }),
      ],
    });
    univerAPI = api;
    createEmptyWorkbook();
    ready.value = true;
    if (pendingTables) {
      const tables = pendingTables;
      pendingTables = null;
      loadWorkbook(tables);
    }
  }, 0);
});

onUnmounted(() => {
  const api = univerAPI as any;
  if (api && typeof api.disposeUniver === "function") {
    api.disposeUniver();
  } else if (api && typeof api.dispose === "function") {
    api.dispose();
  }
});

function createEmptyWorkbook() {
  if (!univerAPI) return;
  const names = getSheetNames();
  const sheets: Record<string, any> = {};
  const sheetOrder: string[] = [];
  names.forEach((name, i) => {
    const id = `sheet-${i}`;
    sheetOrder.push(id);
    sheets[id] = { id, name, cellData: {}, rowCount: MAX_ROWS, columnCount: MAX_COLS };
  });
  workbook = univerAPI.createWorkbook({ id: "ontology-wb", name: "Workbook", sheetOrder, sheets });
}

function buildCellData(tbl?: DemoTable) {
  const cellData: Record<number, Record<number, { v: any }>> = {};
  if (!tbl || !Array.isArray(tbl.columns) || !Array.isArray(tbl.rows)) {
    return cellData;
  }

  cellData[0] = {};
  tbl.columns.forEach((col, ci) => {
    if (ci >= MAX_COLS) return;
    cellData[0][ci] = { v: col };
  });

  for (let ri = 0; ri < tbl.rows.length && ri + 1 < MAX_ROWS; ri++) {
    const row = tbl.rows[ri];
    const rowCells: Record<number, { v: any }> = {};
    for (let ci = 0; ci < tbl.columns.length && ci < MAX_COLS; ci++) {
      const col = tbl.columns[ci];
      let val: any = Array.isArray(row) ? row[ci] : (row as any)?.[col];
      if (val == null) val = "";
      else if (typeof val === "object") val = JSON.stringify(val);
      else if (typeof val === "boolean") val = val ? "true" : "false";
      rowCells[ci] = { v: val };
    }
    cellData[ri + 1] = rowCells;
  }

  return cellData;
}

/** Load tables from backend into sheets. Each DemoTable maps to a sheet by name. */
function loadWorkbook(tables: DemoTable[]) {
  if (!univerAPI) {
    pendingTables = tables;
    return;
  }

  // Fast path: rebuild workbook with pre-populated cellData (no per-cell setValue)
  try {
    if (workbook && typeof (workbook as any).dispose === "function") {
      (workbook as any).dispose();
    }
  } catch (_) {
    // ignore
  }

  const names = getSheetNames(tables);
  const sheets: Record<string, any> = {};
  const sheetOrder: string[] = [];

  for (let i = 0; i < names.length; i++) {
    const name = names[i];
    const id = `sheet-${i}`;
    sheetOrder.push(id);
    const tbl = tables.find((t) => t.name === name);
    sheets[id] = {
      id,
      name,
      cellData: buildCellData(tbl),
      rowCount: MAX_ROWS,
      columnCount: MAX_COLS,
    };
  }

  workbook = univerAPI.createWorkbook({
    id: `ontology-wb-${Date.now()}`,
    name: "Workbook",
    sheetOrder,
    sheets,
  });
}

/** Read all sheets back into DemoTable[] */
function readTables(): DemoTable[] {
  if (!workbook) return [];
  const result: DemoTable[] = [];
  const sheetCount = workbook.getSheetCount?.() ?? 0;

  // Collect names from actual workbook sheets; fall back to prop/default
  const names: string[] = [];
  if (sheetCount > 0) {
    for (let i = 0; i < sheetCount; i++) {
      const s = (workbook as any).getSheetByIndex?.(i);
      if (s) names.push(s.getSheetName());
    }
  }
  const effectiveNames = names.length > 0 ? names : getSheetNames();

  effectiveNames.forEach((name) => {
    const sheet = workbook!.getSheetByName(name);
    if (!sheet) return;

    const range = (sheet as any).getRange?.(0, 0, MAX_ROWS, MAX_COLS);
    const allValues: any[][] | null = typeof range?.getValues === "function" ? range.getValues() : null;

    // Read header from row 0
    const columns: string[] = [];
    for (let c = 0; c < MAX_COLS; c++) {
      const v = allValues ? allValues[0]?.[c] : sheet.getRange(0, c)?.getValue();
      if (v == null || v === "") break;
      columns.push(String(v).trim());
    }
    if (columns.length === 0) {
      result.push({ name, columns: [], rows: [] });
      return;
    }
    // Read data rows
    const rows: Record<string, any>[] = [];
    for (let r = 1; r < MAX_ROWS; r++) {
      const firstVal = allValues ? allValues[r]?.[0] : sheet.getRange(r, 0)?.getValue();
      if (firstVal == null || firstVal === "") break;
      const row: Record<string, any> = {};
      columns.forEach((col, ci) => {
        const raw = allValues ? allValues[r]?.[ci] : sheet.getRange(r, ci)?.getValue();
        row[col] = typeof raw === "string" ? raw.trim() : (raw ?? "");
      });
      rows.push(row);
    }
    result.push({ name, columns, rows });
  });
  return result;
}

defineExpose({ loadWorkbook, readTables, ready });
</script>

<template>
  <div class="univer-workbook" ref="containerRef"></div>
</template>

<style scoped>
.univer-workbook {
  width: 100%;
  height: 100%;
  min-height: 300px;
  border: 1px solid #e0e0e0;
  border-radius: 4px;
  overflow: hidden;
}
</style>
