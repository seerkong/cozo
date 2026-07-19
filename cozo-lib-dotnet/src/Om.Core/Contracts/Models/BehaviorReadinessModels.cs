namespace Cozo.DotNet.Om.Contracts.Models;

public sealed record BehaviorUnresolvedDiagnostic(
    string Code,
    BehaviorCatalogKind Kind,
    string OwnerType,
    string BehaviorKey,
    BehaviorCatalogCallbackSlot Slot,
    string BindingId,
    string? InterceptorPhase = null,
    int? InterceptorSeq = null);

public sealed class BehaviorUnresolvedException : InvalidOperationException
{
    public BehaviorUnresolvedException(BehaviorUnresolvedDiagnostic diagnostic)
        : base(CreateMessage(diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))))
    {
        Diagnostic = diagnostic;
    }

    public BehaviorUnresolvedDiagnostic Diagnostic { get; }

    private static string CreateMessage(BehaviorUnresolvedDiagnostic diagnostic) =>
        $"Behavior callback is unresolved: {diagnostic.BehaviorKey} " +
        $"slot={SlotWire(diagnostic.Slot)} bindingId={diagnostic.BindingId}";

    private static string SlotWire(BehaviorCatalogCallbackSlot slot) => slot switch
    {
        BehaviorCatalogCallbackSlot.When => "when",
        BehaviorCatalogCallbackSlot.Then => "then",
        BehaviorCatalogCallbackSlot.Validator => "validator",
        BehaviorCatalogCallbackSlot.Compute => "compute",
        BehaviorCatalogCallbackSlot.Handler => "handler",
        BehaviorCatalogCallbackSlot.Executor => "executor",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown callback slot"),
    };
}
