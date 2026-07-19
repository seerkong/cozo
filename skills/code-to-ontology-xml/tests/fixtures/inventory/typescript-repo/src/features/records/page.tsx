import { RecordApi } from "./api";

const api = new RecordApi();

export async function loadRecord(id: string) {
  const first = api.getRecord(id); const duplicate = api.getRecord(id);
  return first ?? duplicate;
}
