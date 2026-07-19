using System.Collections.Immutable;
using System.Text.Json;
using Cozo.DotNet.Om.Contracts;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;
using Cozo.DotNet.Om.Logic;

namespace Cozo.DotNet.Om.Runtime;

public sealed record CozoOmRuntime(
    ICozoOmStore Store,
    CozoOmOptions Options,
    CozoOmRegistry Registry)
{
    internal BehaviorRuntimeGate BehaviorGate { get; init; } = new();
    internal BehaviorImportFaultHooks BehaviorImportFaults { get; init; } = new();
}

public sealed record CozoOmOptions
{
    public int DefaultMaxChaseIterations { get; init; } = 10;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public sealed class CozoOmRegistry
{
    private readonly object _writeLock = new();
    private CozoOmRegistrySnapshot _snapshot = CozoOmRegistrySnapshot.Empty;

    internal CozoOmRegistrySnapshot CaptureSnapshot() => Volatile.Read(ref _snapshot);

    internal void Clear() => Update(static _ => CozoOmRegistrySnapshot.Empty);

    public void RegisterValidator(string typeName, string constraintName, Func<OmValidationContext, ValueTask<string?>> validator)
        => RegisterValidatorCore(typeName, constraintName, validator, null);

    public void RegisterValidator(
        string typeName,
        string constraintName,
        string bindingId,
        Func<OmValidationContext, ValueTask<string?>> validator)
        => RegisterValidatorCore(typeName, constraintName, validator, RequireBindingId(bindingId));

    private void RegisterValidatorCore(
        string typeName,
        string constraintName,
        Func<OmValidationContext, ValueTask<string?>> validator,
        string? bindingId)
    {
        ArgumentNullException.ThrowIfNull(validator);
        Update(snapshot => snapshot with
        {
            Validators = snapshot.Validators.SetItem(
                (typeName, constraintName),
                new OmCallbackRegistration<Func<OmValidationContext, ValueTask<string?>>>(validator, bindingId)),
        });
    }

    public bool TryGetValidator(string typeName, string constraintName, out Func<OmValidationContext, ValueTask<string?>> validator)
    {
        if (CaptureSnapshot().Validators.TryGetValue((typeName, constraintName), out var registration))
        {
            validator = registration.Callback;
            return true;
        }

        validator = null!;
        return false;
    }

    public void RegisterConstraint(
        string typeName,
        string constraintName,
        Func<OmValidationContext, ValueTask<bool>> when,
        Func<OmValidationContext, ValueTask<bool>> then)
        => RegisterConstraintCore(typeName, constraintName, when, null, then, null);

    public void RegisterConstraint(
        string typeName,
        string constraintName,
        string whenBindingId,
        Func<OmValidationContext, ValueTask<bool>> when,
        string thenBindingId,
        Func<OmValidationContext, ValueTask<bool>> then)
        => RegisterConstraintCore(
            typeName,
            constraintName,
            when,
            RequireBindingId(whenBindingId),
            then,
            RequireBindingId(thenBindingId));

    private void RegisterConstraintCore(
        string typeName,
        string constraintName,
        Func<OmValidationContext, ValueTask<bool>> when,
        string? whenBindingId,
        Func<OmValidationContext, ValueTask<bool>> then,
        string? thenBindingId)
    {
        var registration = new OmConstraintRegistration(
            when ?? throw new ArgumentNullException(nameof(when)),
            then ?? throw new ArgumentNullException(nameof(then)),
            whenBindingId,
            thenBindingId);
        Update(snapshot => snapshot with
        {
            Constraints = snapshot.Constraints.SetItem((typeName, constraintName), registration),
        });
    }

    public bool TryGetConstraint(string typeName, string constraintName, out OmConstraintRegistration constraint)
    {
        return CaptureSnapshot().Constraints.TryGetValue((typeName, constraintName), out constraint!);
    }

    internal void RestoreConstraintRegistration(
        string typeName,
        string constraintName,
        OmConstraintRegistration registration) =>
        RegisterConstraintCore(
            typeName,
            constraintName,
            registration.When,
            registration.WhenBindingId,
            registration.Then,
            registration.ThenBindingId);

    internal void UnregisterConstraint(string typeName, string constraintName)
    {
        Update(snapshot => snapshot with
        {
            Constraints = snapshot.Constraints.Remove((typeName, constraintName)),
        });
    }

    public void RegisterComputed(string typeName, string attrName, Func<OmComputedContext, ValueTask<object?>> compute)
        => RegisterComputedCore(typeName, attrName, compute, null);

    public void RegisterComputed(
        string typeName,
        string attrName,
        string bindingId,
        Func<OmComputedContext, ValueTask<object?>> compute)
        => RegisterComputedCore(typeName, attrName, compute, RequireBindingId(bindingId));

    private void RegisterComputedCore(
        string typeName,
        string attrName,
        Func<OmComputedContext, ValueTask<object?>> compute,
        string? bindingId)
    {
        ArgumentNullException.ThrowIfNull(compute);
        Update(snapshot => snapshot with
        {
            Computed = snapshot.Computed.SetItem(
                (typeName, attrName),
                new OmCallbackRegistration<Func<OmComputedContext, ValueTask<object?>>>(compute, bindingId)),
        });
    }

    public bool TryGetComputed(string typeName, string attrName, out Func<OmComputedContext, ValueTask<object?>> compute)
    {
        if (CaptureSnapshot().Computed.TryGetValue((typeName, attrName), out var registration))
        {
            compute = registration.Callback;
            return true;
        }

        compute = null!;
        return false;
    }

    public void RegisterMutation(string typeName, string mutationName, Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask> executor)
        => RegisterMutationCore(typeName, mutationName, executor, null);

    public void RegisterMutation(
        string typeName,
        string mutationName,
        string bindingId,
        Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask> executor)
        => RegisterMutationCore(typeName, mutationName, executor, RequireBindingId(bindingId));

    private void RegisterMutationCore(
        string typeName,
        string mutationName,
        Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask> executor,
        string? bindingId)
    {
        ArgumentNullException.ThrowIfNull(executor);
        Update(snapshot => snapshot with
        {
            Mutations = snapshot.Mutations.SetItem(
                (typeName, mutationName),
                new OmCallbackRegistration<Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask>>(executor, bindingId)),
        });
    }

    public bool TryGetMutation(string typeName, string mutationName, out Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask> executor)
    {
        if (CaptureSnapshot().Mutations.TryGetValue((typeName, mutationName), out var registration))
        {
            executor = registration.Callback;
            return true;
        }

        executor = null!;
        return false;
    }

    internal bool TryGetMutationRegistration(
        string typeName,
        string mutationName,
        out OmCallbackRegistration<Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask>> registration) =>
        CaptureSnapshot().Mutations.TryGetValue((typeName, mutationName), out registration!);

    internal void RestoreMutationRegistration(
        string typeName,
        string mutationName,
        OmCallbackRegistration<Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask>> registration) =>
        RegisterMutationCore(typeName, mutationName, registration.Callback, registration.BindingId);

    internal void UnregisterMutation(string typeName, string mutationName)
    {
        Update(snapshot => snapshot with
        {
            Mutations = snapshot.Mutations.Remove((typeName, mutationName)),
        });
    }

    public void RegisterAction(
        string typeName,
        string actionName,
        Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>> handler)
        => RegisterActionCore(typeName, actionName, handler, null);

    public void RegisterAction(
        string typeName,
        string actionName,
        string bindingId,
        Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>> handler)
        => RegisterActionCore(typeName, actionName, handler, RequireBindingId(bindingId));

    private void RegisterActionCore(
        string typeName,
        string actionName,
        Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>> handler,
        string? bindingId)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Update(snapshot => snapshot with
        {
            Actions = snapshot.Actions.SetItem(
                (typeName, actionName),
                new OmCallbackRegistration<Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>>>(handler, bindingId)),
        });
    }

    public bool TryGetAction(
        string typeName,
        string actionName,
        out Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>> handler)
    {
        if (CaptureSnapshot().Actions.TryGetValue((typeName, actionName), out var registration))
        {
            handler = registration.Callback;
            return true;
        }

        handler = null!;
        return false;
    }

    internal bool TryGetActionRegistration(
        string typeName,
        string actionName,
        out OmCallbackRegistration<Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>>> registration) =>
        CaptureSnapshot().Actions.TryGetValue((typeName, actionName), out registration!);

    internal void RestoreActionRegistration(
        string typeName,
        string actionName,
        OmCallbackRegistration<Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>>> registration) =>
        RegisterActionCore(typeName, actionName, registration.Callback, registration.BindingId);

    internal void UnregisterAction(string typeName, string actionName)
    {
        Update(snapshot => snapshot with
        {
            Actions = snapshot.Actions.Remove((typeName, actionName)),
        });
    }

    public int RegisterInterceptor(
        string typeName,
        string actionName,
        string phase,
        Func<OmActionContext, ValueTask> handler,
        string description = "")
    {
        var normalizedPhase = NormalizePhase(phase);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_writeLock)
        {
            var snapshot = _snapshot;
            var target = InterceptorTarget(snapshot, normalizedPhase);
            var key = (typeName, actionName);
            var list = target.TryGetValue(key, out var existing) ? existing : [];
            var seq = list.IsDefaultOrEmpty ? 0 : list.Max(item => item.Seq) + 1;
            var next = list.Add(new OmInterceptorRegistration(handler, seq, description, typeName));
            Publish(SetInterceptorTarget(snapshot, normalizedPhase, target.SetItem(key, next)));
            return seq;
        }
    }

    public void RegisterInterceptor(
        string typeName,
        string actionName,
        string phase,
        int seq,
        string bindingId,
        Func<OmActionContext, ValueTask> handler,
        string description = "")
        => RegisterInterceptorCore(
            typeName,
            actionName,
            phase,
            seq,
            handler,
            description,
            RequireBindingId(bindingId));

    internal int NextInterceptorSeq(string typeName, string actionName, string phase)
    {
        var target = InterceptorTarget(CaptureSnapshot(), NormalizePhase(phase));
        return target.TryGetValue((typeName, actionName), out var list) && !list.IsDefaultOrEmpty
            ? list.Max(item => item.Seq) + 1
            : 0;
    }

    internal void RegisterInterceptor(
        string typeName,
        string actionName,
        string phase,
        int seq,
        Func<OmActionContext, ValueTask> handler,
        string description = "")
        => RegisterInterceptorCore(typeName, actionName, phase, seq, handler, description, null);

    private void RegisterInterceptorCore(
        string typeName,
        string actionName,
        string phase,
        int seq,
        Func<OmActionContext, ValueTask> handler,
        string description,
        string? bindingId)
    {
        var normalizedPhase = NormalizePhase(phase);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_writeLock)
        {
            var snapshot = _snapshot;
            var target = InterceptorTarget(snapshot, normalizedPhase);
            var key = (typeName, actionName);
            var list = target.TryGetValue(key, out var existing) ? existing : [];
            var registration = new OmInterceptorRegistration(handler, seq, description, typeName, bindingId);
            var existingIndex = -1;
            for (var index = 0; index < list.Length; index++)
            {
                if (list[index].Seq == seq)
                {
                    existingIndex = index;
                    break;
                }
            }
            var next = existingIndex >= 0 ? list.SetItem(existingIndex, registration) : list.Add(registration);
            Publish(SetInterceptorTarget(snapshot, normalizedPhase, target.SetItem(key, next)));
        }
    }

    internal bool TryGetInterceptor(string typeName, string actionName, string phase, int seq, out OmInterceptorRegistration registration)
    {
        var target = InterceptorTarget(CaptureSnapshot(), NormalizePhase(phase));
        if (target.TryGetValue((typeName, actionName), out var list))
        {
            foreach (var item in list)
            {
                if (item.Seq == seq)
                {
                    registration = item;
                    return true;
                }
            }
        }

        registration = null!;
        return false;
    }

    internal void RestoreInterceptorRegistration(
        string typeName,
        string actionName,
        string phase,
        OmInterceptorRegistration registration) =>
        RegisterInterceptorCore(
            typeName,
            actionName,
            phase,
            registration.Seq,
            registration.Handler,
            registration.Description,
            registration.BindingId);

    internal void UnregisterInterceptor(string typeName, string actionName, string phase, int seq)
    {
        var normalizedPhase = NormalizePhase(phase);
        lock (_writeLock)
        {
            var snapshot = _snapshot;
            var target = InterceptorTarget(snapshot, normalizedPhase);
            var key = (typeName, actionName);
            if (!target.TryGetValue(key, out var list)) return;
            var next = list.RemoveAll(item => item.Seq == seq);
            target = next.IsEmpty ? target.Remove(key) : target.SetItem(key, next);
            Publish(SetInterceptorTarget(snapshot, normalizedPhase, target));
        }
    }

    public IReadOnlyList<OmInterceptorRegistration> GetInterceptors(string typeName, string actionName, string phase)
    {
        var target = InterceptorTarget(CaptureSnapshot(), NormalizePhase(phase));
        return target.TryGetValue((typeName, actionName), out var list)
            ? list.OrderBy(item => item.Seq).ToImmutableArray()
            : ImmutableArray<OmInterceptorRegistration>.Empty;
    }

    private void Update(Func<CozoOmRegistrySnapshot, CozoOmRegistrySnapshot> update)
    {
        lock (_writeLock)
        {
            Publish(update(_snapshot));
        }
    }

    private void Publish(CozoOmRegistrySnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);

    internal bool TryPublishSnapshot(
        CozoOmRegistrySnapshot expected,
        CozoOmRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_writeLock)
        {
            if (!ReferenceEquals(_snapshot, expected))
            {
                return false;
            }

            Publish(snapshot);
            return true;
        }
    }

    private static string RequireBindingId(string bindingId) =>
        !string.IsNullOrWhiteSpace(bindingId)
            ? bindingId
            : throw new ArgumentException("Binding id must be non-empty", nameof(bindingId));

    private static string NormalizePhase(string phase) => phase.Trim().ToLowerInvariant() switch
    {
        "before" => "before",
        "after" => "after",
        _ => throw new ArgumentException("Interceptor phase must be 'before' or 'after'", nameof(phase)),
    };

    private static ImmutableDictionary<(string TypeName, string ActionName), ImmutableArray<OmInterceptorRegistration>> InterceptorTarget(
        CozoOmRegistrySnapshot snapshot,
        string phase) => phase == "before" ? snapshot.BeforeInterceptors : snapshot.AfterInterceptors;

    private static CozoOmRegistrySnapshot SetInterceptorTarget(
        CozoOmRegistrySnapshot snapshot,
        string phase,
        ImmutableDictionary<(string TypeName, string ActionName), ImmutableArray<OmInterceptorRegistration>> target) =>
        phase == "before"
            ? snapshot with { BeforeInterceptors = target }
            : snapshot with { AfterInterceptors = target };
}

internal sealed class BehaviorRuntimeGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private int _waitingCount;

    internal int WaitingCount => Volatile.Read(ref _waitingCount);

    internal async ValueTask<BehaviorRuntimeGateLease> EnterAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _waitingCount);
        try
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waitingCount);
        }

        return new BehaviorRuntimeGateLease(_semaphore);
    }

    internal async Task<T> ResolveAsync<T>(
        CozoOmRuntime runtime,
        Func<BehaviorResolutionScope, CancellationToken, Task<T>> resolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(resolver);
        await using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);
        var scope = new BehaviorResolutionScope(runtime.Registry.CaptureSnapshot());
        return await resolver(scope, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<T> ResolveAsync<T>(
        BehaviorResolutionScope scope,
        Func<BehaviorResolutionScope, CancellationToken, Task<T>> resolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(resolver);
        await using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);
        return await resolver(scope, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class BehaviorRuntimeGateLease(SemaphoreSlim semaphore) : IAsyncDisposable, IDisposable
{
    private SemaphoreSlim? _semaphore = semaphore;

    public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();

    internal void PublishRegistrySnapshot(
        CozoOmRegistry registry,
        CozoOmRegistrySnapshot expected,
        CozoOmRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Volatile.Read(ref _semaphore) is null)
        {
            throw new ObjectDisposedException(nameof(BehaviorRuntimeGateLease));
        }

        if (!registry.TryPublishSnapshot(expected, snapshot))
        {
            throw new BehaviorRegistryPublicationConflictException();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class BehaviorRegistryPublicationConflictException()
    : InvalidOperationException("Behavior registry changed after import staging; staged publication was rejected.");

internal sealed class BehaviorImportFaultHooks
{
    internal Func<CancellationToken, Task>? BeforePersistentCommitAsync { get; set; }
    internal Func<Task>? AfterPersistentCommitAsync { get; set; }
    internal Action? BeforeRegistryPublish { get; set; }
    internal Func<Task>? BeforePersistentCompensationAsync { get; set; }

    internal void Reset()
    {
        BeforePersistentCommitAsync = null;
        AfterPersistentCommitAsync = null;
        BeforeRegistryPublish = null;
        BeforePersistentCompensationAsync = null;
    }
}

internal sealed record BehaviorResolutionScope(CozoOmRegistrySnapshot RegistrySnapshot);

internal sealed record OmCallbackRegistration<TDelegate>(TDelegate Callback, string? BindingId)
    where TDelegate : Delegate;

internal sealed record CozoOmRegistrySnapshot(
    ImmutableDictionary<(string TypeName, string ConstraintName), OmCallbackRegistration<Func<OmValidationContext, ValueTask<string?>>>> Validators,
    ImmutableDictionary<(string TypeName, string ConstraintName), OmConstraintRegistration> Constraints,
    ImmutableDictionary<(string TypeName, string AttrName), OmCallbackRegistration<Func<OmComputedContext, ValueTask<object?>>>> Computed,
    ImmutableDictionary<(string TypeName, string MutationName), OmCallbackRegistration<Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask>>> Mutations,
    ImmutableDictionary<(string TypeName, string ActionName), OmCallbackRegistration<Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>>>> Actions,
    ImmutableDictionary<(string TypeName, string ActionName), ImmutableArray<OmInterceptorRegistration>> BeforeInterceptors,
    ImmutableDictionary<(string TypeName, string ActionName), ImmutableArray<OmInterceptorRegistration>> AfterInterceptors)
{
    internal static CozoOmRegistrySnapshot Empty { get; } = new(
        ImmutableDictionary<(string, string), OmCallbackRegistration<Func<OmValidationContext, ValueTask<string?>>>>.Empty,
        ImmutableDictionary<(string, string), OmConstraintRegistration>.Empty,
        ImmutableDictionary<(string, string), OmCallbackRegistration<Func<OmComputedContext, ValueTask<object?>>>>.Empty,
        ImmutableDictionary<(string, string), OmCallbackRegistration<Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask>>>.Empty,
        ImmutableDictionary<(string, string), OmCallbackRegistration<Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>>>>.Empty,
        ImmutableDictionary<(string, string), ImmutableArray<OmInterceptorRegistration>>.Empty,
        ImmutableDictionary<(string, string), ImmutableArray<OmInterceptorRegistration>>.Empty);

    internal bool TryGetBindingId(BehaviorBindingKey key, out string? bindingId)
    {
        bindingId = key.BehaviorKind switch
        {
            BehaviorKind.Constraint when key.CallbackSlot == BehaviorCallbackSlot.Validator &&
                                             Validators.TryGetValue((key.OwnerType, key.BehaviorName), out var validator) => validator.BindingId,
            BehaviorKind.Constraint when key.CallbackSlot == BehaviorCallbackSlot.When &&
                                             Constraints.TryGetValue((key.OwnerType, key.BehaviorName), out var constraint) => constraint.WhenBindingId,
            BehaviorKind.Constraint when key.CallbackSlot == BehaviorCallbackSlot.Then &&
                                             Constraints.TryGetValue((key.OwnerType, key.BehaviorName), out var constraint) => constraint.ThenBindingId,
            BehaviorKind.Computed when Computed.TryGetValue((key.OwnerType, key.BehaviorName), out var computed) => computed.BindingId,
            BehaviorKind.Action when Actions.TryGetValue((key.OwnerType, key.BehaviorName), out var action) => action.BindingId,
            BehaviorKind.Mutation when Mutations.TryGetValue((key.OwnerType, key.BehaviorName), out var mutation) => mutation.BindingId,
            BehaviorKind.Interceptor => GetInterceptorBindingId(key),
            _ => null,
        };
        return bindingId is not null;
    }

    private string? GetInterceptorBindingId(BehaviorBindingKey key)
    {
        var source = key.Phase == "before" ? BeforeInterceptors : AfterInterceptors;
        if (!source.TryGetValue((key.OwnerType, key.BehaviorName), out var registrations)) return null;
        foreach (var registration in registrations)
        {
            if (registration.Seq == key.Seq) return registration.BindingId;
        }

        return null;
    }
}

public sealed record OmConstraintRegistration(
    Func<OmValidationContext, ValueTask<bool>> When,
    Func<OmValidationContext, ValueTask<bool>> Then,
    string? WhenBindingId = null,
    string? ThenBindingId = null);

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
    string OwnerType,
    string? BindingId = null);

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
    : OmMutationContext(Runtime, EntityId, TypeName)
{
    internal BehaviorResolutionScope? BehaviorResolution { get; init; }

    public Task<IReadOnlyList<MutationSpec>> CallParentActionAsync(
        string actionName,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default) =>
        Logic.ConstraintLogic.CallParentActionAsync(
            Runtime,
            EntityId,
            TypeName,
            ActionOwnerType,
            actionName,
            parameters,
            BehaviorResolution,
            cancellationToken);
}
