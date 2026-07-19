package example;

public class RecordService {
    public Record create(Record record) {
        if (record == null) {
            throw new IllegalArgumentException();
        }
        record.setStatus("available");
        return record;
    }
}
