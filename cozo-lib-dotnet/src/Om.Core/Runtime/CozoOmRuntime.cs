using System.Text.Json;
using Cozo.DotNet.Om.Contracts;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;

namespace Cozo.DotNet.Om.Runtime;

public sealed record CozoOmRuntime(
    ICozoOmStore Store,
    CozoOmOptions Options,
    CozoOmRegistry Registry);

public sealed record CozoOmOptions
{
    public int DefaultMaxChaseIterations { get; init; } = 10;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public sealed class CozoOmRegistry
{
    private readonly Dictionary<(string TypeName, string ConstraintName), Func<OmValidationContext, ValueTask<string?>>> _validators = new();
    private readonly Dictionary<(string TypeName, string ConstraintName), OmConstraintRegistration> _constraints = new();
    private readonly Dictionary<(string TypeName, string AttrName), Func<OmComputedContext, ValueTask<object?>>> _computed = new();
    private readonly Dictionary<(string TypeName, string MutationName), Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask>> _mutations = new();
    private readonly Dictionary<(string TypeName, string ActionName), Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>>> _actions = new();
    private readonly Dictionary<(string TypeName, string ActionName), List<OmInterceptorRegistration>> _beforeInterceptors = new();
    private readonly Dictionary<(string TypeName, string ActionName), List<OmInterceptorRegistration>> _afterInterceptors = new();

    public void RegisterValidator(string typeName, string constraintName, Func<OmValidationContext, ValueTask<string?>> validator)
    {
        _validators[(typeName, constraintName)] = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public bool TryGetValidator(string typeName, string constraintName, out Func<OmValidationContext, ValueTask<string?>> validator)
    {
        return _validators.TryGetValue((typeName, constraintName), out validator!);
    }

    public void RegisterConstraint(
        string typeName,
        string constraintName,
        Func<OmValidationContext, ValueTask<bool>> when,
        Func<OmValidationContext, ValueTask<bool>> then)
    {
        _constraints[(typeName, constraintName)] = new OmConstraintRegistration(
            when ?? throw new ArgumentNullException(nameof(when)),
            then ?? throw new ArgumentNullException(nameof(then)));
    }

    public bool TryGetConstraint(string typeName, string constraintName, out OmConstraintRegistration constraint)
    {
        return _constraints.TryGetValue((typeName, constraintName), out constraint!);
    }

    public void RegisterComputed(string typeName, string attrName, Func<OmComputedContext, ValueTask<object?>> compute)
    {
        _computed[(typeName, attrName)] = compute ?? throw new ArgumentNullException(nameof(compute));
    }

    public bool TryGetComputed(string typeName, string attrName, out Func<OmComputedContext, ValueTask<object?>> compute)
    {
        return _computed.TryGetValue((typeName, attrName), out compute!);
    }

    public void RegisterMutation(string typeName, string mutationName, Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask> executor)
    {
        _mutations[(typeName, mutationName)] = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public bool TryGetMutation(string typeName, string mutationName, out Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask> executor)
    {
        return _mutations.TryGetValue((typeName, mutationName), out executor!);
    }

    public void RegisterAction(
        string typeName,
        string actionName,
        Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>> handler)
    {
        _actions[(typeName, actionName)] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public bool TryGetAction(
        string typeName,
        string actionName,
        out Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>> handler)
    {
        return _actions.TryGetValue((typeName, actionName), out handler!);
    }

    public int RegisterInterceptor(
        string typeName,
        string actionName,
        string phase,
        Func<OmActionContext, ValueTask> handler,
        string description = "")
    {
        var target = string.Equals(phase, "after", StringComparison.OrdinalIgnoreCase)
            ? _afterInterceptors
            : _beforeInterceptors;
        var key = (typeName, actionName);
        if (!target.TryGetValue(key, out var list))
        {
            list = [];
            target[key] = list;
        }

        var seq = list.Count;
        list.Add(new OmInterceptorRegistration(handler ?? throw new ArgumentNullException(nameof(handler)), seq, description, typeName));
        return seq;
    }

    public IReadOnlyList<OmInterceptorRegistration> GetInterceptors(string typeName, string actionName, string phase)
    {
        var target = string.Equals(phase, "after", StringComparison.OrdinalIgnoreCase)
            ? _afterInterceptors
            : _beforeInterceptors;
        return target.TryGetValue((typeName, actionName), out var list)
            ? list.OrderBy(item => item.Seq).ToArray()
            : [];
    }
}

public sealed record OmConstraintRegistration(
    Func<OmValidationContext, ValueTask<bool>> When,
    Func<OmValidationContext, ValueTask<bool>> Then);

public sealed record OmValidationContext(CozoOmRuntime Runtime, string EntityId, string TypeName)
{
    public Task<JsonElement?> GetPropertyAsync(string attrName, CancellationToken cancellationToken = default) =>
        Logic.EntityLogic.GetPropertyAsync(Runtime, EntityId, attrName, cancellationToken);

    public Task<JsonElement?> GetPropertyAsOfAsync(string attrName, string asOf, CancellationToken cancellationToken = default) =>
        Logic.EntityLogic.GetPropertyAsOfAsync(Runtime, EntityId, attrName, asOf, cancellationToken);

    public Task<NeighborResult> GetNeighborsAsync(string? relName = null, OmDirection direction = OmDirection.Both, CancellationToken cancellationToken = default) =>
        Logic.RelationLogic.GetNeighborsAsync(Runtime, EntityId, relName, direction, cancellationToken);
}

public sealed record OmComputedContext(CozoOmRuntime Runtime, string EntityId, string TypeName, string? AsOf = null)
{
    public Task<JsonElement?> GetPropertyAsync(string attrName, CancellationToken cancellationToken = default) =>
        AsOf is null
            ? Logic.EntityLogic.GetPropertyAsync(Runtime, EntityId, attrName, cancellationToken)
            : Logic.EntityLogic.GetPropertyAsOfAsync(Runtime, EntityId, attrName, AsOf, cancellationToken);

    public Task<NeighborResult> GetNeighborsAsync(string? relName = null, OmDirection direction = OmDirection.Both, CancellationToken cancellationToken = default) =>
        AsOf is null
            ? Logic.RelationLogic.GetNeighborsAsync(Runtime, EntityId, relName, direction, cancellationToken)
            : Logic.RelationLogic.GetNeighborsAsOfAsync(Runtime, EntityId, relName, AsOf, direction, cancellationToken);
}

public sealed record MutationSpec(string Mutation, IReadOnlyDictionary<string, object?>? Params = null);

public sealed record OmInterceptorRegistration(
    Func<OmActionContext, ValueTask> Handler,
    int Seq,
    string Description,
    string OwnerType);

public record OmMutationContext(CozoOmRuntime Runtime, string EntityId, string TypeName)
{
    public Task<JsonElement?> GetPropertyAsync(string attrName, CancellationToken cancellationToken = default) =>
        Logic.EntityLogic.GetPropertyAsync(Runtime, EntityId, attrName, cancellationToken);

    public Task<JsonElement?> GetPropertyAsOfAsync(string attrName, string asOf, CancellationToken cancellationToken = default) =>
        Logic.EntityLogic.GetPropertyAsOfAsync(Runtime, EntityId, attrName, asOf, cancellationToken);

    public Task SetPropertyAsync(string attrName, object? value, WriteOptions? options = null, CancellationToken cancellationToken = default) =>
        Logic.EntityLogic.SetPropertyAsync(Runtime, new SetPropertyInput(EntityId, attrName, value, options), cancellationToken);

    public Task LinkEntitiesAsync(string relName, string toId, object? props = null, WriteOptions? options = null, CancellationToken cancellationToken = default) =>
        Logic.RelationLogic.LinkEntitiesAsync(Runtime, new LinkEntitiesInput(EntityId, relName, toId, props, options), cancellationToken);

    public Task<NeighborResult> GetNeighborsAsync(string? relName = null, OmDirection direction = OmDirection.Both, CancellationToken cancellationToken = default) =>
        Logic.RelationLogic.GetNeighborsAsync(Runtime, EntityId, relName, direction, cancellationToken);
}

public sealed record OmActionContext(
    CozoOmRuntime Runtime,
    string EntityId,
    string TypeName,
    string ActionOwnerType,
    IReadOnlyDictionary<string, object?> Params)
    : OmMutationContext(Runtime, EntityId, TypeName);
