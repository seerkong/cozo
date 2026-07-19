using System.Collections.Immutable;
using Cozo.DotNet.Om.Runtime;

namespace Cozo.DotNet.Om.Contracts.Models;

public sealed record BehaviorImportOptions(bool RequireReady = false);

public sealed record BehaviorConstraintCallbackBinding(
    string BindingId,
    Func<OmValidationContext, ValueTask<bool>> Callback);

public sealed record BehaviorValidatorCallbackBinding(
    string BindingId,
    Func<OmValidationContext, ValueTask<string?>> Callback);

public sealed record BehaviorComputedCallbackBinding(
    string BindingId,
    Func<OmComputedContext, ValueTask<object?>> Callback);

public sealed record BehaviorActionCallbackBinding(
    string BindingId,
    Func<OmActionContext, IReadOnlyDictionary<string, object?>, ValueTask<IReadOnlyList<MutationSpec>>> Callback);

public sealed record BehaviorMutationCallbackBinding(
    string BindingId,
    Func<OmMutationContext, IReadOnlyDictionary<string, object?>, ValueTask> Callback);

public sealed record BehaviorInterceptorCallbackBinding(
    string BindingId,
    Func<OmActionContext, ValueTask> Callback);

public sealed record BehaviorCallbackBindingSet
{
    public BehaviorCallbackBindingSet(
        IEnumerable<BehaviorConstraintCallbackBinding>? constraints = null,
        IEnumerable<BehaviorValidatorCallbackBinding>? validators = null,
        IEnumerable<BehaviorComputedCallbackBinding>? computed = null,
        IEnumerable<BehaviorActionCallbackBinding>? actions = null,
        IEnumerable<BehaviorMutationCallbackBinding>? mutations = null,
        IEnumerable<BehaviorInterceptorCallbackBinding>? interceptors = null)
    {
        Constraints = Copy(constraints);
        Validators = Copy(validators);
        Computed = Copy(computed);
        Actions = Copy(actions);
        Mutations = Copy(mutations);
        Interceptors = Copy(interceptors);
    }

    public ImmutableArray<BehaviorConstraintCallbackBinding> Constraints { get; }
    public ImmutableArray<BehaviorValidatorCallbackBinding> Validators { get; }
    public ImmutableArray<BehaviorComputedCallbackBinding> Computed { get; }
    public ImmutableArray<BehaviorActionCallbackBinding> Actions { get; }
    public ImmutableArray<BehaviorMutationCallbackBinding> Mutations { get; }
    public ImmutableArray<BehaviorInterceptorCallbackBinding> Interceptors { get; }

    private static ImmutableArray<T> Copy<T>(IEnumerable<T>? values) =>
        values?.ToImmutableArray() ?? ImmutableArray<T>.Empty;
}

public sealed record BehaviorImportDiagnostic(
    string Code,
    string Path,
    string Message,
    BehaviorCatalogKind? Kind = null,
    string? OwnerType = null,
    string? BehaviorName = null,
    BehaviorCatalogCallbackSlot? Slot = null,
    string? BindingId = null,
    string? InterceptorPhase = null,
    int? InterceptorSeq = null);

public sealed record BehaviorImportResult
{
    public BehaviorImportResult(
        bool applied,
        IEnumerable<BehaviorImportDiagnostic>? diagnostics = null,
        IEnumerable<BehaviorUnresolvedDiagnostic>? unresolved = null)
    {
        Applied = applied;
        Diagnostics = diagnostics?.ToImmutableArray() ?? ImmutableArray<BehaviorImportDiagnostic>.Empty;
        Unresolved = unresolved?.ToImmutableArray() ?? ImmutableArray<BehaviorUnresolvedDiagnostic>.Empty;
    }

    public bool Applied { get; }
    public ImmutableArray<BehaviorImportDiagnostic> Diagnostics { get; }
    public ImmutableArray<BehaviorUnresolvedDiagnostic> Unresolved { get; }
}

public sealed class BehaviorImportException : Exception
{
    public BehaviorImportException(
        string message,
        Exception originalFailure,
        IEnumerable<Exception>? compensationFailures = null)
        : base(message, originalFailure)
    {
        OriginalFailure = originalFailure;
        CompensationFailures = compensationFailures?.ToImmutableArray() ?? ImmutableArray<Exception>.Empty;
    }

    public Exception OriginalFailure { get; }
    public ImmutableArray<Exception> CompensationFailures { get; }
}
