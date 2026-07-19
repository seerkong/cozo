package example;

public enum RecordStatus {
    PENDING("PENDING"),
    AVAILABLE("AVAILABLE"),
    RETIRED("RETIRED");

    RecordStatus(String wireValue) {}
}
