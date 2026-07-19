export interface RecordDto {
  id: string;
  name: string;
  status: RecordStatus;
}

export type RecordFilter = {
  status?: RecordStatus;
};

export enum RecordStatus {
  Pending = "pending",
  Available = "available",
}

export class RecordApi {
  async getRecord(id: string): Promise<RecordDto> {
    return request.get(`/api/records/${id}`);
  }

  async createRecord(input: RecordDto): Promise<RecordDto> {
    return request.post("/api/records", input);
  }
}
