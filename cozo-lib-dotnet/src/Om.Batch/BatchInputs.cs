namespace Cozo.DotNet.Om.Batch;

public sealed record OmBatchInput(
    IReadOnlyList<OmBatchEntity>? Entities = null,
    IReadOnlyList<OmBatchProperty>? Properties = null,
    IReadOnlyList<OmBatchEdge>? Edges = null);

public sealed record OmBatchEntity(string Id, string TypeName, string Label = "");

public sealed record OmBatchProperty(string EntityId, string AttrName, object? Value);

public sealed record OmBatchEdge(string FromId, string RelName, string ToId, object? Props = null);

public sealed record OmBatchOptions(bool ValidateRequired = true);

public sealed record OmBatchResult(int Entities, int Properties, int Edges, int ValidatedEntities);
