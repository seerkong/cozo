using System.Collections.Immutable;

namespace Cozo.DotNet.Om.Contracts.Models;

public enum BehaviorCatalogKind
{
    Constraint,
    Computed,
    Action,
    Mutation,
    Interceptor,
}

public enum BehaviorCatalogCallbackSlot
{
    When,
    Then,
    Validator,
    Compute,
    Handler,
    Executor,
}

public enum BehaviorReadiness
{
    Unbound,
    Unresolved,
    Ready,
}

public sealed record BehaviorCallbackBinding(
    BehaviorCatalogCallbackSlot Slot,
    string? BindingId,
    BehaviorReadiness Readiness);

public sealed record BehaviorCatalogEntry
{
    public BehaviorCatalogEntry(
        BehaviorCatalogKind kind,
        string ownerType,
        string name,
        string? constraintType,
        string? message,
        string? description,
        string? interceptorPhase,
        int? interceptorSeq,
        IEnumerable<BehaviorCallbackBinding> callbacks)
    {
        Kind = kind;
        OwnerType = ownerType;
        Name = name;
        ConstraintType = constraintType;
        Message = message;
        Description = description;
        InterceptorPhase = interceptorPhase;
        InterceptorSeq = interceptorSeq;
        Callbacks = callbacks?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(callbacks));
    }

    public BehaviorCatalogKind Kind { get; init; }
    public string OwnerType { get; init; }
    public string Name { get; init; }
    public string? ConstraintType { get; init; }
    public string? Message { get; init; }
    public string? Description { get; init; }
    public string? InterceptorPhase { get; init; }
    public int? InterceptorSeq { get; init; }
    public ImmutableArray<BehaviorCallbackBinding> Callbacks { get; init; }
}

public sealed record BehaviorCatalog
{
    public BehaviorCatalog(IEnumerable<BehaviorCatalogEntry> behaviors)
    {
        Behaviors = behaviors?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(behaviors));
    }

    public ImmutableArray<BehaviorCatalogEntry> Behaviors { get; init; }
}
