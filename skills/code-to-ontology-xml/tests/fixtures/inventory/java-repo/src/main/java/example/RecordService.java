package example;

import org.springframework.stereotype.Service;

@Service
public class RecordService {
    private final RecordMapper mapper;
    private final RecordRepository repository;

    public Record createRecord(Record record) {
        if (record.getName() == null) {
            throw new IllegalArgumentException("name is required");
        }
        record.setName("renamed");
        repository.save(record);
        record.setRecordStatus(RecordStatus.AVAILABLE);
        mapper.insert(record);
        return record;
    }
}

record Record(String id, String name, RecordStatus status) {}
