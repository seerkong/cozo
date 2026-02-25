using Cozo.DotNet.Om;
using Cozo.DotNet.Om.Inputs;
using Cozo.DotNet.Om.Logic;
using Cozo.DotNet.Om.Runtime;

namespace Cozo.DotNet.Om.Batch;

public static class CozoOmBatchExtensions
{
    public static async Task<OmBatchResult> IngestBatchAsync(
        this CozoOm om,
        OmBatchInput batch,
        OmBatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(om);
        ArgumentNullException.ThrowIfNull(batch);

        var entities = batch.Entities ?? [];
        var properties = batch.Properties ?? [];
        var edges = batch.Edges ?? [];
        var touchedEntityIds = new HashSet<string>(StringComparer.Ordinal);
        var writeOptions = new WriteOptions(SkipConstraints: true);

        await using var tx = await om.Runtime.Store.BeginTransactionAsync(write: true, cancellationToken);
        var txRuntime = om.Runtime with { Store = tx };

        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EntityLogic.CreateEntityAsync(txRuntime, new EntityInput(entity.Id, entity.TypeName, entity.Label), cancellationToken);
            touchedEntityIds.Add(entity.Id);
        }

        foreach (var property in properties)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EntityLogic.SetPropertyAsync(txRuntime, new SetPropertyInput(property.EntityId, property.AttrName, property.Value, writeOptions), cancellationToken);
            touchedEntityIds.Add(property.EntityId);
        }

        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RelationLogic.LinkEntitiesAsync(txRuntime, new LinkEntitiesInput(edge.FromId, edge.RelName, edge.ToId, edge.Props ?? new Dictionary<string, object?>(), writeOptions), cancellationToken);
        }

        if (options?.ValidateRequired != false)
        {
            foreach (var entityId in touchedEntityIds)
            {
                var validation = await ConstraintLogic.ValidateEntityAsync(txRuntime, entityId, cancellationToken);
                if (!validation.Valid)
                {
                    throw new CozoException(string.Join("; ", validation.Errors));
                }
            }
        }

        await tx.CommitAsync(cancellationToken);
        return new OmBatchResult(entities.Count, properties.Count, edges.Count, touchedEntityIds.Count);
    }
}
