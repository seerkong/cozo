package example;

import org.apache.ibatis.annotations.Mapper;

@Mapper
public interface RecordMapper {
    Record findById(String id);
    int insert(Record record);
    int updateStatus(String id, RecordStatus status);
}
