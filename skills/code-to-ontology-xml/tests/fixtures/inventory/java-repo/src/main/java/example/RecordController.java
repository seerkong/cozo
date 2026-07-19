package example;

import org.springframework.web.bind.annotation.*;

@RestController
@RequestMapping("/api/records")
public class RecordController {
    private final RecordService recordService;

    @GetMapping("/{id}")
    public Record getRecord(@PathVariable String id) {
        return recordService.getRecord(id);
    }

    @PostMapping
    public Record createRecord(@RequestBody Record record) {
        return recordService.createRecord(record);
    }
}
