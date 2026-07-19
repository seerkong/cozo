using System.Collections;
using System.Collections.Immutable;
using System.Dynamic;
using System.Text.Json;
using Acornima;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;
using Cozo.DotNet.Om.Runtime;
using Jint;
using Jint.Constraints;
using Jint.Native;
using Jint.Runtime;

namespace Cozo.DotNet.Om.Scripting.Jint;

/// <summary>
/// Defines one process-local JavaScript callback source keyed by an exact canonical behavior binding ID.
/// </summary>
public sealed record JintBehaviorScriptDefinition
{
    /// <summary>
    /// Creates a script definition. The source must evaluate to one callable function.
    /// </summary>
    /// <param name="bindingId">The case-sensitive, ordinal binding ID referenced by the behavior catalog.</param>
    /// <param name="source">A JavaScript expression that evaluates to a callable function.</param>
    /// <param name="sourceName">An optional diagnostic source name. It is not persisted in the behavior manifest.</param>
    public JintBehaviorScriptDefinition(string bindingId, string source, string? sourceName = null)
    {
        BindingId = bindingId;
        Source = source;
        SourceName = sourceName;
    }

    /// <summary>Gets the exact canonical behavior binding ID.</summary>
    public string BindingId { get; init; }

    /// <summary>Gets the programmatic JavaScript source expression.</summary>
    public string Source { get; init; }

    /// <summary>Gets the optional non-persisted source name used in diagnostics.</summary>
    public string? SourceName { get; init; }
}

/// <summary>
/// Configures finite execution limits and caller cancellation for script preflight and invocation.
/// </summary>
/// <param name="Timeout">The wall-clock timeout, or <see langword="null"/> for <see cref="DefaultTimeout"/>.</param>
/// <param name="MaxStatements">The statement budget, or <see langword="null"/> for <see cref="DefaultMaxStatements"/>.</param>
/// <param name="MaxRecursionDepth">The recursion limit, or <see langword="null"/> for <see cref="DefaultMaxRecursionDepth"/>.</param>
/// <param name="MemoryLimitBytes">The memory limit in bytes, or <see langword="null"/> for <see cref="DefaultMemoryLimitBytes"/>.</param>
/// <param name="CancellationToken">The caller cancellation token propagated through script and host work.</param>
public sealed record JintBehaviorScriptOptions(
    TimeSpan? Timeout = null,
    long? MaxStatements = null,
    int? MaxRecursionDepth = null,
    long? MemoryLimitBytes = null,
    CancellationToken CancellationToken = default)
{
    /// <summary>Gets the default two-second wall-clock timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Gets the default statement budget.</summary>
    public const long DefaultMaxStatements = 250_000;

    /// <summary>Gets the default recursion depth limit.</summary>
    public const int DefaultMaxRecursionDepth = 128;

    /// <summary>Gets the default 32 MiB memory limit.</summary>
    public const long DefaultMemoryLimitBytes = 32L * 1024 * 1024;
}

/// <summary>
/// Supplies canonical behavior metadata, immutable script definitions, limits, and optional native callbacks.
/// </summary>
public sealed record JintBehaviorScriptBindingRequest
{
    /// <summary>
    /// Creates a binding request and snapshots <paramref name="definitions"/> into an immutable array.
    /// </summary>
    /// <param name="catalog">The decoded canonical behavior catalog.</param>
    /// <param name="definitions">Process-local JavaScript definitions keyed by exact binding ID.</param>
    /// <param name="options">Finite runtime options; defaults are used when omitted.</param>
    /// <param name="nativeCallbacks">Native callbacks to merge with generated callbacks.</param>
    public JintBehaviorScriptBindingRequest(
        BehaviorCatalog catalog,
        IEnumerable<JintBehaviorScriptDefinition>? definitions = null,
        JintBehaviorScriptOptions? options = null,
        BehaviorCallbackBindingSet? nativeCallbacks = null)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Definitions = definitions?.ToImmutableArray() ?? ImmutableArray<JintBehaviorScriptDefinition>.Empty;
        Options = options ?? new JintBehaviorScriptOptions();
        NativeCallbacks = nativeCallbacks ?? new BehaviorCallbackBindingSet();
    }

    /// <summary>Gets the canonical behavior catalog used to select callback shapes.</summary>
    public BehaviorCatalog Catalog { get; init; }

    /// <summary>Gets the immutable process-local script definitions.</summary>
    public ImmutableArray<JintBehaviorScriptDefinition> Definitions { get; init; }

    /// <summary>Gets the script preflight and invocation options.</summary>
    public JintBehaviorScriptOptions Options { get; init; }

    /// <summary>Gets native callbacks that may coexist with non-conflicting script callbacks.</summary>
    public BehaviorCallbackBindingSet NativeCallbacks { get; init; }
}

/// <summary>
/// Contains provider-neutral callback bindings and deterministic construction diagnostics.
/// </summary>
public sealed record JintBehaviorScriptBindingResult
{
    /// <summary>Creates a binding result from optional callbacks and diagnostics.</summary>
    /// <param name="bindings">The provider-neutral callback bindings.</param>
    /// <param name="diagnostics">Deterministic provider diagnostics.</param>
    public JintBehaviorScriptBindingResult(
        BehaviorCallbackBindingSet? bindings = null,
        IEnumerable<JintBehaviorScriptDiagnostic>? diagnostics = null)
    {
        Bindings = bindings ?? new BehaviorCallbackBindingSet();
        Diagnostics = diagnostics?.ToImmutableArray() ?? ImmutableArray<JintBehaviorScriptDiagnostic>.Empty;
    }

    /// <summary>Gets whether binding construction completed without diagnostics.</summary>
    public bool Success => Diagnostics.IsEmpty;

    /// <summary>Gets the immutable native and generated callback bindings.</summary>
    public BehaviorCallbackBindingSet Bindings { get; }

    /// <summary>Gets deterministic validation and preflight diagnostics.</summary>
    public ImmutableArray<JintBehaviorScriptDiagnostic> Diagnostics { get; }
}

/// <summary>Describes a binding construction or callable-preflight failure.</summary>
/// <param name="Code">The stable <c>OMS1xxx</c> diagnostic code.</param>
/// <param name="Message">The source-redacted public message.</param>
/// <param name="BindingId">The exact binding ID associated with the failure, when available.</param>
/// <param name="Kind">The canonical behavior kind.</param>
/// <param name="OwnerType">The behavior owner type.</param>
/// <param name="BehaviorName">The canonical behavior name.</param>
/// <param name="Slot">The canonical callback slot.</param>
/// <param name="SourceName">The optional programmatic source name.</param>
/// <param name="Phase">The failure phase, when applicable.</param>
/// <param name="Line">The one-based source line, when available.</param>
/// <param name="Column">The one-based source column, when available.</param>
public sealed record JintBehaviorScriptDiagnostic(
    string Code,
    string Message,
    string BindingId,
    BehaviorCatalogKind Kind,
    string OwnerType,
    string BehaviorName,
    BehaviorCatalogCallbackSlot Slot,
    string? SourceName = null,
    JintBehaviorScriptFailurePhase? Phase = null,
    int? Line = null,
    int? Column = null);

/// <summary>
/// Builds bounded Jint callbacks for canonical OM behavior bindings without persisting script source.
/// </summary>
public static class JintBehaviorScriptProvider
{
    /// <summary>
    /// Validates the request, performs bounded callable preflight, and returns provider-neutral callbacks.
    /// </summary>
    /// <param name="request">The canonical catalog, script definitions, limits, and native callbacks.</param>
    /// <returns>
    /// A result containing immutable bindings and deterministic diagnostics. Structural errors,
    /// conflicts, and callable-preflight failures produce no generated bindings; missing definitions
    /// remain absent while independently satisfied catalog bindings may still be returned.
    /// </returns>
    /// <exception cref="OperationCanceledException">The request cancellation token is canceled.</exception>
    public static JintBehaviorScriptBindingResult BuildBindings(JintBehaviorScriptBindingRequest? request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<JintBehaviorScriptDiagnostic>();
        if (!TryNormalizeRequest(request, diagnostics, out var normalized))
        {
            return new JintBehaviorScriptBindingResult(diagnostics: SortDiagnostics(diagnostics));
        }

        AddOptionDiagnostics(normalized.Options, diagnostics);
        diagnostics.AddRange(ValidateDefinitions(normalized.Definitions));

        var references = GetScriptReferences(normalized.Catalog, diagnostics);
        if (diagnostics.Any(IsStructuralDiagnostic))
        {
            return new JintBehaviorScriptBindingResult(diagnostics: SortDiagnostics(diagnostics));
        }

        normalized = normalized with { Options = ApplyFiniteDefaults(normalized.Options) };

        var duplicateIds = FindDuplicateDefinitions(normalized.Definitions);
        var definitions = normalized.Definitions
            .GroupBy(definition => definition.BindingId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var reference in references)
        {
            if (definitions.ContainsKey(reference.BindingId)
                || HasMatchingNativeCallback(normalized.NativeCallbacks, reference))
            {
                continue;
            }

            diagnostics.Add(new JintBehaviorScriptDiagnostic(
                "OMS1001",
                $"Script definition is missing for binding id '{reference.BindingId}'.",
                reference.BindingId,
                reference.Kind,
                reference.OwnerType,
                reference.BehaviorName,
                reference.Slot));
        }

        foreach (var duplicateId in duplicateIds)
        {
            var reference = references.FirstOrDefault(item => string.Equals(item.BindingId, duplicateId, StringComparison.Ordinal));
            diagnostics.Add(new JintBehaviorScriptDiagnostic(
                "OMS1003",
                $"Script definition is duplicated for binding id '{duplicateId}'.",
                duplicateId,
                reference?.Kind ?? BehaviorCatalogKind.Action,
                reference?.OwnerType ?? "",
                reference?.BehaviorName ?? "",
                reference?.Slot ?? BehaviorCatalogCallbackSlot.Handler));
        }

        foreach (var group in references.GroupBy(reference => reference.BindingId, StringComparer.Ordinal))
        {
            var shapes = group.Select(reference => reference.Shape).Distinct().ToImmutableArray();
            var reference = group.OrderBy(ReferenceSortKey, StringComparer.Ordinal).First();
            if (shapes.Length > 1)
            {
                diagnostics.Add(new JintBehaviorScriptDiagnostic(
                    "OMS1002",
                    $"Binding id '{group.Key}' is reused across incompatible callback shapes.",
                    group.Key,
                    reference.Kind,
                    reference.OwnerType,
                    reference.BehaviorName,
                    reference.Slot));
                continue;
            }

            var slots = group.Select(item => item.Slot).Distinct().ToImmutableArray();
            if (slots.Length > 1)
            {
                diagnostics.Add(new JintBehaviorScriptDiagnostic(
                    "OMS1002",
                    $"Binding id '{group.Key}' is reused across distinct callback slots, so per-slot failure identity cannot be preserved.",
                    group.Key,
                    reference.Kind,
                    reference.OwnerType,
                    reference.BehaviorName,
                    reference.Slot));
            }
        }

        foreach (var conflict in FindNativeScriptConflicts(normalized.NativeCallbacks, references, definitions.Keys))
        {
            diagnostics.Add(new JintBehaviorScriptDiagnostic(
                "OMS1004",
                $"Native and script callbacks both define binding id '{conflict.BindingId}'.",
                conflict.BindingId,
                conflict.Kind,
                conflict.OwnerType,
                conflict.BehaviorName,
                conflict.Slot));
        }

        if (diagnostics.Any(diagnostic => diagnostic.Code is "OMS1002" or "OMS1003" or "OMS1004"))
        {
            return new JintBehaviorScriptBindingResult(diagnostics: SortDiagnostics(diagnostics));
        }

        diagnostics.AddRange(PreflightReferencedDefinitions(references, definitions, normalized.Options));
        if (diagnostics.Any(IsCallablePreflightDiagnostic))
        {
            return new JintBehaviorScriptBindingResult(diagnostics: SortDiagnostics(diagnostics));
        }

        var constraints = ImmutableArray.CreateBuilder<BehaviorConstraintCallbackBinding>();
        constraints.AddRange(normalized.NativeCallbacks.Constraints);
        var validators = ImmutableArray.CreateBuilder<BehaviorValidatorCallbackBinding>();
        validators.AddRange(normalized.NativeCallbacks.Validators);
        var computed = ImmutableArray.CreateBuilder<BehaviorComputedCallbackBinding>();
        computed.AddRange(normalized.NativeCallbacks.Computed);
        var actions = ImmutableArray.CreateBuilder<BehaviorActionCallbackBinding>();
        actions.AddRange(normalized.NativeCallbacks.Actions);
        var mutations = ImmutableArray.CreateBuilder<BehaviorMutationCallbackBinding>();
        mutations.AddRange(normalized.NativeCallbacks.Mutations);
        var interceptors = ImmutableArray.CreateBuilder<BehaviorInterceptorCallbackBinding>();
        interceptors.AddRange(normalized.NativeCallbacks.Interceptors);

        var generated = new HashSet<(CallbackShape Shape, string BindingId)>();
        foreach (var reference in references.OrderBy(ReferenceSortKey, StringComparer.Ordinal))
        {
            if (!definitions.TryGetValue(reference.BindingId, out var definition)) continue;
            if (!generated.Add((reference.Shape, reference.BindingId))) continue;

            switch (reference.Shape)
            {
                case CallbackShape.Constraint:
                    constraints.Add(new BehaviorConstraintCallbackBinding(
                        reference.BindingId,
                        async ctx => ToBoolean(
                            await InvokeScriptAsync(definition, normalized.Options, reference, ctx).ConfigureAwait(false),
                            definition,
                            reference)));
                    break;
                case CallbackShape.Validator:
                    validators.Add(new BehaviorValidatorCallbackBinding(
                        reference.BindingId,
                        async ctx => ToValidatorResult(
                            await InvokeScriptAsync(definition, normalized.Options, reference, ctx).ConfigureAwait(false),
                            definition,
                            reference)));
                    break;
                case CallbackShape.Computed:
                    computed.Add(new BehaviorComputedCallbackBinding(
                        reference.BindingId,
                        async ctx => NormalizeJsonValue(
                            await InvokeScriptAsync(definition, normalized.Options, reference, ctx).ConfigureAwait(false),
                            definition,
                            reference)));
                    break;
                case CallbackShape.Action:
                    actions.Add(new BehaviorActionCallbackBinding(
                        reference.BindingId,
                        async (ctx, parameters) => ToMutationSpecs(
                            await InvokeScriptAsync(definition, normalized.Options, reference, ctx, parameters).ConfigureAwait(false),
                            definition,
                            reference)));
                    break;
                case CallbackShape.Mutation:
                    mutations.Add(new BehaviorMutationCallbackBinding(
                        reference.BindingId,
                        async (ctx, parameters) =>
                        {
                            await InvokeScriptAsync(definition, normalized.Options, reference, ctx, parameters).ConfigureAwait(false);
                        }));
                    break;
                case CallbackShape.Interceptor:
                    interceptors.Add(new BehaviorInterceptorCallbackBinding(
                        reference.BindingId,
                        async ctx =>
                        {
                            await InvokeScriptAsync(definition, normalized.Options, reference, ctx).ConfigureAwait(false);
                        }));
                    break;
            }
        }

        return new JintBehaviorScriptBindingResult(
            new BehaviorCallbackBindingSet(
                constraints.OrderBy(binding => binding.BindingId, StringComparer.Ordinal),
                validators.OrderBy(binding => binding.BindingId, StringComparer.Ordinal),
                computed.OrderBy(binding => binding.BindingId, StringComparer.Ordinal),
                actions.OrderBy(binding => binding.BindingId, StringComparer.Ordinal),
                mutations.OrderBy(binding => binding.BindingId, StringComparer.Ordinal),
                interceptors.OrderBy(binding => binding.BindingId, StringComparer.Ordinal)),
            SortDiagnostics(diagnostics));
    }

    private static bool TryNormalizeRequest(
        JintBehaviorScriptBindingRequest? request,
        ImmutableArray<JintBehaviorScriptDiagnostic>.Builder diagnostics,
        out NormalizedBindingRequest normalized)
    {
        normalized = default;
        if (request is null)
        {
            diagnostics.Add(GraphNullDiagnostic("Binding request must be non-null."));
            return false;
        }

        var valid = true;
        if (request.Catalog is null)
        {
            diagnostics.Add(GraphNullDiagnostic("Binding request catalog must be non-null."));
            valid = false;
        }

        if (request.Options is null)
        {
            diagnostics.Add(GraphNullDiagnostic("Binding request options must be non-null."));
            valid = false;
        }

        if (request.NativeCallbacks is null)
        {
            diagnostics.Add(GraphNullDiagnostic("Binding request native callbacks must be non-null."));
            valid = false;
        }

        if (request.Definitions.IsDefault)
        {
            diagnostics.Add(GraphNullDiagnostic("Binding request definitions collection must be initialized."));
            valid = false;
        }

        if (!valid)
        {
            return false;
        }

        var catalog = request.Catalog!;
        var options = request.Options!;
        var nativeCallbacks = request.NativeCallbacks!;
        ValidateDefinitionsCollection(request.Definitions, diagnostics, ref valid);
        ValidateCatalogGraph(catalog, diagnostics, ref valid);
        ValidateNativeCallbacks(nativeCallbacks, diagnostics, ref valid);
        if (!valid)
        {
            return false;
        }

        normalized = new NormalizedBindingRequest(
            catalog,
            request.Definitions,
            options,
            nativeCallbacks);
        return true;
    }

    private static void ValidateDefinitionsCollection(
        ImmutableArray<JintBehaviorScriptDefinition> definitions,
        ImmutableArray<JintBehaviorScriptDiagnostic>.Builder diagnostics,
        ref bool valid)
    {
        for (var index = 0; index < definitions.Length; index++)
        {
            if (definitions[index] is not null) continue;

            diagnostics.Add(GraphNullElementDiagnostic($"Script definition at index {index} must be non-null."));
            valid = false;
        }
    }

    private static void ValidateCatalogGraph(
        BehaviorCatalog catalog,
        ImmutableArray<JintBehaviorScriptDiagnostic>.Builder diagnostics,
        ref bool valid)
    {
        if (catalog.Behaviors.IsDefault)
        {
            diagnostics.Add(GraphNullDiagnostic("Catalog behavior collection must be initialized."));
            valid = false;
            return;
        }

        for (var behaviorIndex = 0; behaviorIndex < catalog.Behaviors.Length; behaviorIndex++)
        {
            var entry = catalog.Behaviors[behaviorIndex];
            if (entry is null)
            {
                diagnostics.Add(GraphNullElementDiagnostic($"Catalog behavior at index {behaviorIndex} must be non-null."));
                valid = false;
                continue;
            }

            if (entry.Callbacks.IsDefault)
            {
                diagnostics.Add(new JintBehaviorScriptDiagnostic(
                    "OMS1011",
                    $"Catalog callbacks for behavior at index {behaviorIndex} must be initialized.",
                    "",
                    entry.Kind,
                    entry.OwnerType ?? "",
                    entry.Name ?? "",
                    BehaviorCatalogCallbackSlot.Handler));
                valid = false;
                continue;
            }

            for (var callbackIndex = 0; callbackIndex < entry.Callbacks.Length; callbackIndex++)
            {
                if (entry.Callbacks[callbackIndex] is not null) continue;

                diagnostics.Add(new JintBehaviorScriptDiagnostic(
                    "OMS1012",
                    $"Catalog callback at behavior index {behaviorIndex}, callback index {callbackIndex} must be non-null.",
                    "",
                    entry.Kind,
                    entry.OwnerType ?? "",
                    entry.Name ?? "",
                    BehaviorCatalogCallbackSlot.Handler));
                valid = false;
            }
        }
    }

    private static void ValidateNativeCallbacks(
        BehaviorCallbackBindingSet nativeCallbacks,
        ImmutableArray<JintBehaviorScriptDiagnostic>.Builder diagnostics,
        ref bool valid)
    {
        ValidateNativeCallbackCollection(nativeCallbacks.Constraints, "Constraints", diagnostics, ref valid);
        ValidateNativeCallbackCollection(nativeCallbacks.Validators, "Validators", diagnostics, ref valid);
        ValidateNativeCallbackCollection(nativeCallbacks.Computed, "Computed", diagnostics, ref valid);
        ValidateNativeCallbackCollection(nativeCallbacks.Actions, "Actions", diagnostics, ref valid);
        ValidateNativeCallbackCollection(nativeCallbacks.Mutations, "Mutations", diagnostics, ref valid);
        ValidateNativeCallbackCollection(nativeCallbacks.Interceptors, "Interceptors", diagnostics, ref valid);
    }

    private static void ValidateNativeCallbackCollection<T>(
        ImmutableArray<T> callbacks,
        string collectionName,
        ImmutableArray<JintBehaviorScriptDiagnostic>.Builder diagnostics,
        ref bool valid)
        where T : class
    {
        if (callbacks.IsDefault)
        {
            diagnostics.Add(GraphNullDiagnostic($"Native callback collection '{collectionName}' must be initialized."));
            valid = false;
            return;
        }

        for (var index = 0; index < callbacks.Length; index++)
        {
            if (callbacks[index] is not null) continue;

            diagnostics.Add(GraphNullElementDiagnostic($"Native callback '{collectionName}' at index {index} must be non-null."));
            valid = false;
        }
    }

    private static ImmutableArray<ScriptReference> GetScriptReferences(
        BehaviorCatalog catalog,
        ImmutableArray<JintBehaviorScriptDiagnostic>.Builder diagnostics)
    {
        var references = ImmutableArray.CreateBuilder<ScriptReference>();
        foreach (var entry in catalog.Behaviors)
        {
            if (!Enum.IsDefined(entry.Kind))
            {
                diagnostics.Add(new JintBehaviorScriptDiagnostic(
                    "OMS1008",
                    $"Behavior kind '{entry.Kind}' is invalid.",
                    "",
                    entry.Kind,
                    entry.OwnerType,
                    entry.Name,
                    BehaviorCatalogCallbackSlot.Handler));
                continue;
            }

            foreach (var callback in entry.Callbacks)
            {
                if (string.IsNullOrWhiteSpace(callback.BindingId))
                {
                    diagnostics.Add(new JintBehaviorScriptDiagnostic(
                        "OMS1007",
                        "Catalog callback binding id must be non-blank.",
                        callback.BindingId ?? "",
                        entry.Kind,
                        entry.OwnerType,
                        entry.Name,
                        callback.Slot));
                    continue;
                }

                if (!Enum.IsDefined(callback.Slot) || !TryGetShape(entry.Kind, callback.Slot, out var shape))
                {
                    diagnostics.Add(new JintBehaviorScriptDiagnostic(
                        "OMS1009",
                        $"Callback slot '{callback.Slot}' is invalid for behavior kind '{entry.Kind}'.",
                        callback.BindingId,
                        entry.Kind,
                        entry.OwnerType,
                        entry.Name,
                        callback.Slot));
                    continue;
                }

                references.Add(new ScriptReference(
                    callback.BindingId,
                    entry.Kind,
                    entry.OwnerType,
                    entry.Name,
                    callback.Slot,
                    shape));
            }
        }

        return references.ToImmutable();
    }

    private static ImmutableArray<JintBehaviorScriptDiagnostic> ValidateDefinitions(
        ImmutableArray<JintBehaviorScriptDefinition> definitions)
    {
        var diagnostics = ImmutableArray.CreateBuilder<JintBehaviorScriptDiagnostic>();
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.BindingId))
            {
                diagnostics.Add(new JintBehaviorScriptDiagnostic(
                    "OMS1005",
                    "Script definition binding id must be non-blank.",
                    definition.BindingId ?? "",
                    BehaviorCatalogKind.Action,
                    "",
                    "",
                    BehaviorCatalogCallbackSlot.Handler));
            }

            if (string.IsNullOrWhiteSpace(definition.Source))
            {
                diagnostics.Add(new JintBehaviorScriptDiagnostic(
                    "OMS1006",
                    $"Script definition source must be non-blank for binding id '{definition.BindingId}'.",
                    definition.BindingId ?? "",
                    BehaviorCatalogKind.Action,
                    "",
                    "",
                    BehaviorCatalogCallbackSlot.Handler));
            }
        }

        return diagnostics.ToImmutable();
    }

    private static void AddOptionDiagnostics(
        JintBehaviorScriptOptions options,
        ImmutableArray<JintBehaviorScriptDiagnostic>.Builder diagnostics)
    {
        if (options.Timeout <= TimeSpan.Zero)
        {
            diagnostics.Add(OptionDiagnostic("Timeout must be positive when configured."));
        }

        if (options.MaxStatements <= 0 || options.MaxStatements > int.MaxValue)
        {
            diagnostics.Add(OptionDiagnostic($"MaxStatements must be between 1 and {int.MaxValue} when configured."));
        }

        if (options.MaxRecursionDepth <= 0)
        {
            diagnostics.Add(OptionDiagnostic("MaxRecursionDepth must be positive when configured."));
        }

        if (options.MemoryLimitBytes <= 0)
        {
            diagnostics.Add(OptionDiagnostic("MemoryLimitBytes must be positive when configured."));
        }
    }

    private static JintBehaviorScriptOptions ApplyFiniteDefaults(JintBehaviorScriptOptions options) =>
        options with
        {
            Timeout = options.Timeout ?? JintBehaviorScriptOptions.DefaultTimeout,
            MaxStatements = options.MaxStatements ?? JintBehaviorScriptOptions.DefaultMaxStatements,
            MaxRecursionDepth = options.MaxRecursionDepth ?? JintBehaviorScriptOptions.DefaultMaxRecursionDepth,
            MemoryLimitBytes = options.MemoryLimitBytes ?? JintBehaviorScriptOptions.DefaultMemoryLimitBytes,
        };

    private static JintBehaviorScriptDiagnostic OptionDiagnostic(string message) =>
        new(
            "OMS1010",
            message,
            "",
            BehaviorCatalogKind.Action,
            "",
            "",
            BehaviorCatalogCallbackSlot.Handler);

    private static JintBehaviorScriptDiagnostic GraphNullDiagnostic(string message) =>
        new(
            "OMS1011",
            message,
            "",
            BehaviorCatalogKind.Action,
            "",
            "",
            BehaviorCatalogCallbackSlot.Handler);

    private static JintBehaviorScriptDiagnostic GraphNullElementDiagnostic(string message) =>
        new(
            "OMS1012",
            message,
            "",
            BehaviorCatalogKind.Action,
            "",
            "",
            BehaviorCatalogCallbackSlot.Handler);

    private static ImmutableArray<JintBehaviorScriptDiagnostic> PreflightReferencedDefinitions(
        ImmutableArray<ScriptReference> references,
        IReadOnlyDictionary<string, JintBehaviorScriptDefinition> definitions,
        JintBehaviorScriptOptions options)
    {
        var diagnostics = ImmutableArray.CreateBuilder<JintBehaviorScriptDiagnostic>();
        foreach (var reference in references
                     .GroupBy(item => item.BindingId, StringComparer.Ordinal)
                     .Select(group => group.OrderBy(ReferenceSortKey, StringComparer.Ordinal).First())
                     .OrderBy(ReferenceSortKey, StringComparer.Ordinal))
        {
            if (!definitions.TryGetValue(reference.BindingId, out var definition)) continue;

            var diagnostic = PreflightDefinition(definition, reference, options);
            if (diagnostic is not null)
            {
                diagnostics.Add(diagnostic);
            }
        }

        return diagnostics.ToImmutable();
    }

    private static JintBehaviorScriptDiagnostic? PreflightDefinition(
        JintBehaviorScriptDefinition definition,
        ScriptReference reference,
        JintBehaviorScriptOptions options)
    {
        options.CancellationToken.ThrowIfCancellationRequested();

        try
        {
            var engine = CreateEngine(options, enableTaskInterop: false);
            var value = engine
                .Evaluate($"typeof ({definition.Source}) === 'function'", definition.SourceName ?? definition.BindingId)
                .ToObject();
            if (value is true)
            {
                return null;
            }

            return new JintBehaviorScriptDiagnostic(
                "OMS1014",
                $"Script definition source must evaluate to a callable function for binding id '{definition.BindingId}'.",
                definition.BindingId,
                reference.Kind,
                reference.OwnerType,
                reference.BehaviorName,
                reference.Slot);
        }
        catch (Exception exception) when (options.CancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Script callable preflight was cancelled by the caller.",
                exception,
                options.CancellationToken);
        }
        catch (Exception exception)
        {
            var (line, column) = GetSourceLocation(exception);
            return new JintBehaviorScriptDiagnostic(
                "OMS1013",
                $"Script definition source failed callable preflight for binding id '{definition.BindingId}'.",
                definition.BindingId,
                reference.Kind,
                reference.OwnerType,
                reference.BehaviorName,
                reference.Slot,
                definition.SourceName,
                JintBehaviorScriptFailurePhase.Compile,
                line,
                column);
        }
    }

    private static ImmutableArray<string> FindDuplicateDefinitions(ImmutableArray<JintBehaviorScriptDefinition> definitions) =>
        definitions
            .GroupBy(definition => definition.BindingId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToImmutableArray();

    private static bool HasMatchingNativeCallback(
        BehaviorCallbackBindingSet nativeCallbacks,
        ScriptReference reference) =>
        reference.Shape switch
        {
            CallbackShape.Constraint => nativeCallbacks.Constraints.Any(
                callback => string.Equals(callback.BindingId, reference.BindingId, StringComparison.Ordinal)),
            CallbackShape.Validator => nativeCallbacks.Validators.Any(
                callback => string.Equals(callback.BindingId, reference.BindingId, StringComparison.Ordinal)),
            CallbackShape.Computed => nativeCallbacks.Computed.Any(
                callback => string.Equals(callback.BindingId, reference.BindingId, StringComparison.Ordinal)),
            CallbackShape.Action => nativeCallbacks.Actions.Any(
                callback => string.Equals(callback.BindingId, reference.BindingId, StringComparison.Ordinal)),
            CallbackShape.Mutation => nativeCallbacks.Mutations.Any(
                callback => string.Equals(callback.BindingId, reference.BindingId, StringComparison.Ordinal)),
            CallbackShape.Interceptor => nativeCallbacks.Interceptors.Any(
                callback => string.Equals(callback.BindingId, reference.BindingId, StringComparison.Ordinal)),
            _ => false,
        };

    private static ImmutableArray<ScriptReference> FindNativeScriptConflicts(
        BehaviorCallbackBindingSet nativeCallbacks,
        ImmutableArray<ScriptReference> references,
        IEnumerable<string> definitionIds)
    {
        var scriptIds = definitionIds.ToHashSet(StringComparer.Ordinal);
        var nativeIds = nativeCallbacks.Constraints.Select(item => item.BindingId)
            .Concat(nativeCallbacks.Validators.Select(item => item.BindingId))
            .Concat(nativeCallbacks.Computed.Select(item => item.BindingId))
            .Concat(nativeCallbacks.Actions.Select(item => item.BindingId))
            .Concat(nativeCallbacks.Mutations.Select(item => item.BindingId))
            .Concat(nativeCallbacks.Interceptors.Select(item => item.BindingId))
            .ToHashSet(StringComparer.Ordinal);

        return references
            .Where(reference => scriptIds.Contains(reference.BindingId) && nativeIds.Contains(reference.BindingId))
            .GroupBy(reference => reference.BindingId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(ReferenceSortKey, StringComparer.Ordinal).First())
            .OrderBy(reference => reference.BindingId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static bool TryGetShape(
        BehaviorCatalogKind kind,
        BehaviorCatalogCallbackSlot slot,
        out CallbackShape shape)
    {
        switch (kind, slot)
        {
            case (BehaviorCatalogKind.Constraint, BehaviorCatalogCallbackSlot.When):
            case (BehaviorCatalogKind.Constraint, BehaviorCatalogCallbackSlot.Then):
                shape = CallbackShape.Constraint;
                return true;
            case (BehaviorCatalogKind.Constraint, BehaviorCatalogCallbackSlot.Validator):
                shape = CallbackShape.Validator;
                return true;
            case (BehaviorCatalogKind.Computed, BehaviorCatalogCallbackSlot.Compute):
                shape = CallbackShape.Computed;
                return true;
            case (BehaviorCatalogKind.Action, BehaviorCatalogCallbackSlot.Handler):
                shape = CallbackShape.Action;
                return true;
            case (BehaviorCatalogKind.Mutation, BehaviorCatalogCallbackSlot.Executor):
                shape = CallbackShape.Mutation;
                return true;
            case (BehaviorCatalogKind.Interceptor, BehaviorCatalogCallbackSlot.Handler):
                shape = CallbackShape.Interceptor;
                return true;
            default:
                shape = default;
                return false;
        }
    }

    private static async ValueTask<ScriptInvocationValue> InvokeScriptAsync(
        JintBehaviorScriptDefinition definition,
        JintBehaviorScriptOptions options,
        ScriptReference reference,
        object callbackContext,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        options.CancellationToken.ThrowIfCancellationRequested();

        using var timeoutCancellation = options.Timeout is { } timeout
            ? new CancellationTokenSource(timeout)
            : null;
        using var hostCancellation = timeoutCancellation is null
            ? CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken, timeoutCancellation.Token);

        try
        {
            var engine = CreateEngine(options, enableTaskInterop: true);

            var callable = engine.Evaluate($"({definition.Source})", definition.SourceName ?? definition.BindingId);
            var identity = CreateFrozenJsonValue(engine, ProjectIdentity(callbackContext));
            var host = CreateHost(engine, callbackContext, reference, hostCancellation.Token);
            var value = reference.Shape is CallbackShape.Action or CallbackShape.Mutation
                ? engine.Invoke(callable, identity, CreateFrozenJsonValue(engine, parameters), host)
                : engine.Invoke(callable, identity, host);
            var unwrapped = await value.UnwrapIfPromiseAsync(hostCancellation.Token).ConfigureAwait(false);
            return new ScriptInvocationValue(engine, unwrapped, options);
        }
        catch (Exception exception)
        {
            ThrowMappedExecutionException(
                exception,
                definition,
                options,
                reference,
                timeoutCancellation?.IsCancellationRequested == true);
            throw;
        }
    }

    private static Engine CreateEngine(
        JintBehaviorScriptOptions options,
        bool enableTaskInterop) =>
        new(engineOptions =>
        {
            if (enableTaskInterop)
            {
                engineOptions.ExperimentalFeatures = ExperimentalFeature.TaskInterop;
            }

            if (options.MaxStatements is { } maxStatements)
            {
                engineOptions.MaxStatements((int)maxStatements);
            }

            if (options.MaxRecursionDepth is { } maxRecursionDepth)
            {
                engineOptions.LimitRecursion(maxRecursionDepth);
            }

            if (options.MemoryLimitBytes is { } memoryLimitBytes)
            {
                engineOptions.LimitMemory(memoryLimitBytes);
            }

            if (options.Timeout is { } timeout)
            {
                engineOptions.TimeoutInterval(timeout);
            }

            if (options.CancellationToken.CanBeCanceled)
            {
                engineOptions.CancellationToken(options.CancellationToken);
            }
        });

    private static JsValue CreateFrozenJsonValue(Engine engine, object? value)
    {
        var json = JsonSerializer.Serialize(value, HostJsonOptions);
        var factory = engine.Evaluate($"({FrozenJsonFactorySource})");
        return engine.Invoke(factory, json);
    }

    private static JsValue CreateHost(
        Engine engine,
        object callbackContext,
        ScriptReference reference,
        CancellationToken cancellationToken)
    {
        Func<JsValue, Task<string>>? getProperty = callbackContext switch
        {
            OmValidationContext context => attr => InvokeHostAsync(
                "getProperty",
                cancellationToken,
                token => context.GetPropertyAsync(RequireString(attr, "attrName"), token)),
            OmComputedContext context => attr => InvokeHostAsync(
                "getProperty",
                cancellationToken,
                token => context.GetPropertyAsync(RequireString(attr, "attrName"), token)),
            OmMutationContext context => attr => InvokeHostAsync(
                "getProperty",
                cancellationToken,
                token => context.GetPropertyAsync(RequireString(attr, "attrName"), token)),
            _ => null,
        };

        Func<JsValue, JsValue, Task<string>>? getPropertyAsOf = callbackContext switch
        {
            OmValidationContext context => (attr, asOf) => InvokeHostAsync(
                "getPropertyAsOf",
                cancellationToken,
                token => context.GetPropertyAsOfAsync(
                    RequireString(attr, "attrName"),
                    RequireString(asOf, "asOf"),
                    token)),
            OmMutationContext context when callbackContext is not OmComputedContext => (attr, asOf) => InvokeHostAsync(
                "getPropertyAsOf",
                cancellationToken,
                token => context.GetPropertyAsOfAsync(
                    RequireString(attr, "attrName"),
                    RequireString(asOf, "asOf"),
                    token)),
            _ => null,
        };

        Func<JsValue, JsValue, Task<string>>? getNeighbors = callbackContext switch
        {
            OmValidationContext context => (relation, direction) => InvokeHostAsync(
                "getNeighbors",
                cancellationToken,
                token => context.GetNeighborsAsync(
                    OptionalString(relation, "relName"),
                    ParseDirection(direction),
                    token)),
            OmComputedContext context => (relation, direction) => InvokeHostAsync(
                "getNeighbors",
                cancellationToken,
                token => context.GetNeighborsAsync(
                    OptionalString(relation, "relName"),
                    ParseDirection(direction),
                    token)),
            OmMutationContext context => (relation, direction) => InvokeHostAsync(
                "getNeighbors",
                cancellationToken,
                token => context.GetNeighborsAsync(
                    OptionalString(relation, "relName"),
                    ParseDirection(direction),
                    token)),
            _ => null,
        };

        var writeContext = reference.Kind is BehaviorCatalogKind.Mutation
            or BehaviorCatalogKind.Action
            or BehaviorCatalogKind.Interceptor
            ? callbackContext as OmMutationContext
            : null;
        Func<JsValue, JsValue, JsValue, Task<string>>? setProperty = writeContext is null
            ? null
            : (attr, value, writeOptions) => InvokeHostAsync<object?>(
                "setProperty",
                cancellationToken,
                async token =>
                {
                    await writeContext.SetPropertyAsync(
                        RequireString(attr, "attrName"),
                        ReadJsonArgument(engine, value, "value"),
                        ReadWriteOptions(engine, writeOptions),
                        token).ConfigureAwait(false);
                    return null;
                });
        Func<JsValue, JsValue, JsValue, JsValue, Task<string>>? linkEntities = writeContext is null
            ? null
            : (relation, toId, props, writeOptions) => InvokeHostAsync<object?>(
                "linkEntities",
                cancellationToken,
                async token =>
                {
                    await writeContext.LinkEntitiesAsync(
                        RequireString(relation, "relName"),
                        RequireString(toId, "toId"),
                        ReadOptionalJsonArgument(engine, props, "props"),
                        ReadWriteOptions(engine, writeOptions),
                        token).ConfigureAwait(false);
                    return null;
                });

        var actionContext = reference.Kind == BehaviorCatalogKind.Action
            ? callbackContext as OmActionContext
            : null;
        Func<JsValue, JsValue, Task<string>>? callParentAction = actionContext is null
            ? null
            : (actionName, actionParameters) => InvokeHostAsync(
                "callParentAction",
                cancellationToken,
                token => actionContext.CallParentActionAsync(
                    RequireString(actionName, "actionName"),
                    ReadOptionalDictionary(engine, actionParameters, "params"),
                    token));

        var factory = engine.Evaluate($"({HostFactorySource})");
        return engine.Invoke(
            factory,
            getProperty,
            getPropertyAsOf,
            getNeighbors,
            setProperty,
            linkEntities,
            callParentAction);
    }

    private static async Task<string> InvokeHostAsync<T>(
        string operation,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> invoke)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var value = await invoke(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return JsonSerializer.Serialize(value, HostJsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HostCancellationMarker;
        }
        catch (Exception)
        {
            return HostFailureMarker + operation;
        }
    }

    private static object? ReadOptionalJsonArgument(Engine engine, JsValue? value, string name) =>
        value is null || value.IsUndefined() || value.IsNull() ? null : ReadJsonArgument(engine, value, name);

    private static object? ReadJsonArgument(Engine engine, JsValue value, string name)
    {
        var predicate = engine.Evaluate($"({JsonShapePredicateSource})");
        var valid = engine.Invoke(predicate, value);
        if (!valid.IsBoolean() || !valid.AsBoolean())
        {
            throw new ArgumentException($"Host argument '{name}' must be JSON-compatible.", name);
        }

        var stringify = engine.Evaluate("value => JSON.stringify(value)");
        var json = engine.Invoke(stringify, value);
        if (!json.IsString())
        {
            throw new ArgumentException($"Host argument '{name}' must be JSON-compatible.", name);
        }

        return JsonSerializer.Deserialize<object?>(json.AsString(), HostJsonOptions);
    }

    private static IReadOnlyDictionary<string, object?>? ReadOptionalDictionary(
        Engine engine,
        JsValue? value,
        string name)
    {
        if (value is null || value.IsUndefined() || value.IsNull()) return null;
        var normalized = ReadJsonArgument(engine, value, name);
        if (normalized is not JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            throw new ArgumentException($"Host argument '{name}' must be an object.", name);
        }

        return element.EnumerateObject()
            .ToImmutableDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.Ordinal);
    }

    private static WriteOptions? ReadWriteOptions(Engine engine, JsValue? value)
    {
        if (value is null || value.IsUndefined() || value.IsNull()) return null;
        var normalized = ReadJsonArgument(engine, value, "options");
        if (normalized is not JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            throw new ArgumentException("Host argument 'options' must be an object.", "options");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, "validTime", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Host write option '{property.Name}' is not allowed.", "options");
            }
        }

        var validTime = element.TryGetProperty("validTime", out var valid)
            ? valid.ValueKind == JsonValueKind.Null
                ? null
                : valid.ValueKind == JsonValueKind.String
                    ? valid.GetString()
                    : throw new ArgumentException("Host option 'validTime' must be a string or null.", "options")
            : null;
        return new WriteOptions(SkipConstraints: false, ValidTime: validTime);
    }

    private static string RequireString(JsValue? value, string name)
    {
        if (value is null || !value.IsString() || string.IsNullOrWhiteSpace(value.AsString()))
        {
            throw new ArgumentException($"Host argument '{name}' must be a non-blank string.", name);
        }

        return value.AsString();
    }

    private static string? OptionalString(JsValue? value, string name) =>
        value is null || value.IsUndefined() || value.IsNull() ? null : RequireString(value, name);

    private static OmDirection ParseDirection(JsValue? value)
    {
        if (value is null || value.IsUndefined() || value.IsNull()) return OmDirection.Both;
        if (!value.IsString())
        {
            throw new ArgumentException("Host argument 'direction' must be outgoing, incoming or both.", "direction");
        }

        return value.AsString().ToLowerInvariant() switch
        {
            "outgoing" => OmDirection.Outgoing,
            "incoming" => OmDirection.Incoming,
            "both" => OmDirection.Both,
            _ => throw new ArgumentException("Host argument 'direction' must be outgoing, incoming or both.", "direction"),
        };
    }

    private static bool ToBoolean(
        ScriptInvocationValue value,
        JintBehaviorScriptDefinition definition,
        ScriptReference reference) =>
        value.Value.IsBoolean()
            ? value.Value.AsBoolean()
            : throw ConversionException(definition, reference, "Script callback must return a boolean value.");

    private static string? ToValidatorResult(
        ScriptInvocationValue value,
        JintBehaviorScriptDefinition definition,
        ScriptReference reference)
    {
        if (value.Value.IsNull()) return null;
        if (value.Value.IsString()) return value.Value.AsString();
        throw ConversionException(definition, reference, "Validator script callback must return null or a string.");
    }

    private static IReadOnlyList<MutationSpec> ToMutationSpecs(
        ScriptInvocationValue value,
        JintBehaviorScriptDefinition definition,
        ScriptReference reference)
    {
        var normalized = NormalizeJsonValue(value, definition, reference);
        if (normalized is not IReadOnlyList<object?> items)
        {
            throw ConversionException(definition, reference, "Action script callback must return an array of mutation specs.");
        }

        var mutations = ImmutableArray.CreateBuilder<MutationSpec>();
        foreach (var item in items)
        {
            if (item is not IReadOnlyDictionary<string, object?> spec)
            {
                throw ConversionException(definition, reference, "Action script callback returned a non-object mutation spec.");
            }

            if (!spec.TryGetValue("mutation", out var mutationValue) || mutationValue is not string mutation || string.IsNullOrWhiteSpace(mutation))
            {
                throw ConversionException(definition, reference, "Action script mutation spec must contain a non-blank mutation string.");
            }

            IReadOnlyDictionary<string, object?>? parameters = null;
            if (spec.TryGetValue("params", out var paramsValue))
            {
                if (paramsValue is not null && paramsValue is not IReadOnlyDictionary<string, object?>)
                {
                    throw ConversionException(definition, reference, "Action script mutation spec params must be an object when provided.");
                }

                parameters = (IReadOnlyDictionary<string, object?>?)paramsValue;
            }

            mutations.Add(new MutationSpec(mutation, parameters));
        }

        return mutations.ToImmutable();
    }

    private static object? NormalizeJsonValue(
        ScriptInvocationValue value,
        JintBehaviorScriptDefinition definition,
        ScriptReference reference)
    {
        try
        {
            var predicate = value.Engine.Evaluate($"({JsonShapePredicateSource})");
            var isJsonShape = value.Engine.Invoke(predicate, value.Value);
            if (!isJsonShape.IsBoolean() || !isJsonShape.AsBoolean())
            {
                throw ConversionException(definition, reference, "Script callback returned a non JSON-compatible value.");
            }

            return NormalizeClrJsonValue(value.Value.ToObject(), definition, reference);
        }
        catch (JintBehaviorScriptException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (IsExecutionConstraintFailure(exception))
            {
                ThrowMappedExecutionException(exception, definition, value.Options, reference);
            }

            throw ConversionException(
                definition,
                reference,
                "Script callback result could not be converted to a JSON-compatible value.",
                exception);
        }
    }

    private static object? NormalizeClrJsonValue(
        object? value,
        JintBehaviorScriptDefinition definition,
        ScriptReference reference)
    {
        switch (value)
        {
            case null:
                return null;
            case string or bool:
                return value;
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return value;
            case ExpandoObject expando:
                return NormalizeDictionary(expando, definition, reference);
            case IReadOnlyDictionary<string, object?> dictionary:
                return NormalizeDictionary(dictionary, definition, reference);
            case IDictionary<string, object?> dictionary:
                return NormalizeDictionary(dictionary, definition, reference);
            case IDictionary dictionary:
                return NormalizeDictionary(dictionary, definition, reference);
            case IEnumerable enumerable when value is not string:
                return enumerable.Cast<object?>()
                    .Select(item => NormalizeClrJsonValue(item, definition, reference))
                    .ToImmutableArray<object?>();
            default:
                throw ConversionException(definition, reference, "Script callback returned a non JSON-compatible value.");
        }
    }

    private static IReadOnlyDictionary<string, object?> NormalizeDictionary(
        IEnumerable<KeyValuePair<string, object?>> dictionary,
        JintBehaviorScriptDefinition definition,
        ScriptReference reference) =>
        dictionary
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToImmutableDictionary(
                pair => pair.Key,
                pair => NormalizeClrJsonValue(pair.Value, definition, reference),
                StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, object?> NormalizeDictionary(
        IDictionary dictionary,
        JintBehaviorScriptDefinition definition,
        ScriptReference reference)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string key)
            {
                throw ConversionException(definition, reference, "Script callback returned an object with a non-string key.");
            }

            builder[key] = NormalizeClrJsonValue(entry.Value, definition, reference);
        }

        return builder
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static JintBehaviorScriptException ConversionException(
        JintBehaviorScriptDefinition definition,
        ScriptReference reference,
        string message,
        Exception? innerException = null) =>
        JintBehaviorScriptException.Conversion(
            reference.BindingId,
            reference.Kind,
            reference.Slot,
            definition.SourceName,
            innerException ?? new InvalidOperationException(message));

    private static void ThrowMappedExecutionException(
        Exception exception,
        JintBehaviorScriptDefinition definition,
        JintBehaviorScriptOptions options,
        ScriptReference reference,
        bool hostTimeout = false)
    {
        if (options.CancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Script callback execution was cancelled by the caller.",
                exception,
                options.CancellationToken);
        }

        var (code, phase) = ClassifyExecutionFailure(exception, hostTimeout);
        var (line, column) = GetSourceLocation(exception);
        throw new JintBehaviorScriptException(
            code,
            reference.BindingId,
            reference.Kind,
            reference.Slot,
            phase,
            definition.SourceName,
            exception,
            line,
            column,
            detail: PublicFailureDetail(phase));
    }

    private static (string Code, JintBehaviorScriptFailurePhase Phase) ClassifyExecutionFailure(
        Exception exception,
        bool hostTimeout)
    {
        if (exception.Message.Contains(HostFailureMarker, StringComparison.Ordinal))
        {
            return ("OMS2004", JintBehaviorScriptFailurePhase.HostInvocation);
        }

        if (hostTimeout
            && (ContainsException<OperationCanceledException>(exception)
                || exception.Message.Contains(HostCancellationMarker, StringComparison.Ordinal)))
        {
            return ("OMS2101", JintBehaviorScriptFailurePhase.Timeout);
        }

        if (ContainsException<StatementsCountOverflowException>(exception))
        {
            return ("OMS2102", JintBehaviorScriptFailurePhase.Limit);
        }

        if (ContainsException<RecursionDepthOverflowException>(exception))
        {
            return ("OMS2103", JintBehaviorScriptFailurePhase.Limit);
        }

        if (ContainsException<MemoryLimitExceededException>(exception))
        {
            return ("OMS2104", JintBehaviorScriptFailurePhase.Limit);
        }

        if (ContainsException<TimeoutException>(exception))
        {
            return ("OMS2101", JintBehaviorScriptFailurePhase.Timeout);
        }

        return ("OMS2002", JintBehaviorScriptFailurePhase.Execution);
    }

    private static bool IsExecutionConstraintFailure(Exception exception) =>
        ContainsException<StatementsCountOverflowException>(exception)
        || ContainsException<RecursionDepthOverflowException>(exception)
        || ContainsException<MemoryLimitExceededException>(exception)
        || ContainsException<TimeoutException>(exception)
        || ContainsException<ExecutionCanceledException>(exception)
        || ContainsException<OperationCanceledException>(exception);

    private static bool ContainsException<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException) return true;
        }

        return false;
    }

    private static string PublicFailureDetail(JintBehaviorScriptFailurePhase phase) => phase switch
    {
        JintBehaviorScriptFailurePhase.HostInvocation => "An allowlisted OM host operation failed.",
        JintBehaviorScriptFailurePhase.Timeout => "Script execution exceeded its wall-clock timeout.",
        JintBehaviorScriptFailurePhase.Limit => "Script execution exceeded a configured resource limit.",
        _ => "Script execution failed.",
    };

    private static (int? Line, int? Column) GetSourceLocation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ParseErrorException parseError)
            {
                return (
                    parseError.LineNumber > 0 ? parseError.LineNumber : null,
                    parseError.Column >= 0 ? parseError.Column + 1 : null);
            }

            if (JintException.TryGetJavaScriptLocation(current, out var location))
            {
                return (
                    location.Start.Line > 0 ? location.Start.Line : null,
                    location.Start.Column >= 0 ? location.Start.Column + 1 : null);
            }
        }

        return (null, null);
    }

    private static object ProjectIdentity(object context) => context switch
    {
        OmActionContext action => new
        {
            entityId = action.EntityId,
            typeName = action.TypeName,
            actionOwnerType = action.ActionOwnerType,
            parameters = action.Params,
        },
        OmComputedContext computed => new
        {
            entityId = computed.EntityId,
            typeName = computed.TypeName,
            asOf = computed.AsOf,
        },
        OmValidationContext validation => new
        {
            entityId = validation.EntityId,
            typeName = validation.TypeName,
        },
        OmMutationContext mutation => new
        {
            entityId = mutation.EntityId,
            typeName = mutation.TypeName,
        },
        _ => throw new ArgumentException("Unsupported OM callback context.", nameof(context)),
    };

    private static bool IsStructuralDiagnostic(JintBehaviorScriptDiagnostic diagnostic) =>
        diagnostic.Code is "OMS1005" or "OMS1006" or "OMS1007" or "OMS1008" or "OMS1009" or "OMS1010" or "OMS1011" or "OMS1012";

    private static bool IsCallablePreflightDiagnostic(JintBehaviorScriptDiagnostic diagnostic) =>
        diagnostic.Code is "OMS1013" or "OMS1014";

    private static ImmutableArray<JintBehaviorScriptDiagnostic> SortDiagnostics(
        IEnumerable<JintBehaviorScriptDiagnostic> diagnostics) =>
        diagnostics
            .OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.BindingId, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Kind)
            .ThenBy(diagnostic => diagnostic.OwnerType, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.BehaviorName, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Slot)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToImmutableArray();

    private static string ReferenceSortKey(ScriptReference reference) =>
        string.Join(
            "\u001f",
            reference.BindingId,
            ((int)reference.Shape).ToString("D2"),
            ((int)reference.Kind).ToString("D2"),
            reference.OwnerType,
            reference.BehaviorName,
            ((int)reference.Slot).ToString("D2"));

    private const string JsonShapePredicateSource = """
        value => {
          const seen = new Set();
          const visit = current => {
            if (current === null) return true;
            switch (typeof current) {
              case 'string':
              case 'boolean':
                return true;
              case 'number':
                return Number.isFinite(current);
              case 'object': {
                if (seen.has(current)) return false;
                seen.add(current);
                let valid;
                if (Array.isArray(current)) {
                  valid = current.every(visit);
                } else {
                  const prototype = Object.getPrototypeOf(current);
                  valid = (prototype === Object.prototype || prototype === null)
                    && Object.keys(current).every(key => visit(current[key]));
                }
                seen.delete(current);
                return valid;
              }
              default:
                return false;
            }
          };
          return visit(value);
        }
        """;

    private const string FrozenJsonFactorySource = """
        json => {
          const deepFreeze = value => {
            if (value !== null && typeof value === 'object' && !Object.isFrozen(value)) {
              Object.values(value).forEach(deepFreeze);
              Object.freeze(value);
            }
            return value;
          };
          return deepFreeze(JSON.parse(json));
        }
        """;

    private const string HostFactorySource = """
        (getProperty, getPropertyAsOf, getNeighbors, setProperty, linkEntities, callParentAction) => {
          const deepFreeze = value => {
            if (value !== null && typeof value === 'object' && !Object.isFrozen(value)) {
              Object.values(value).forEach(deepFreeze);
              Object.freeze(value);
            }
            return value;
          };
          const wrap = bridge => (...args) => Promise.resolve(bridge(...args))
            .then(payload => {
              if (payload.startsWith('__OMS_HOST_FAILURE__:')) throw new Error(payload);
              if (payload === '__OMS_HOST_CANCELLED__') throw new Error(payload);
              return deepFreeze(JSON.parse(payload));
            });
          const host = Object.create(null);
          if (typeof getProperty === 'function') host.getProperty = wrap(getProperty);
          if (typeof getPropertyAsOf === 'function') host.getPropertyAsOf = wrap(getPropertyAsOf);
          if (typeof getNeighbors === 'function') host.getNeighbors = wrap(getNeighbors);
          if (typeof setProperty === 'function') host.setProperty = wrap(setProperty);
          if (typeof linkEntities === 'function') host.linkEntities = wrap(linkEntities);
          if (typeof callParentAction === 'function') host.callParentAction = wrap(callParentAction);
          return Object.freeze(host);
        }
        """;

    private static readonly JsonSerializerOptions HostJsonOptions = new(JsonSerializerDefaults.Web);
    private const string HostFailureMarker = "__OMS_HOST_FAILURE__:";
    private const string HostCancellationMarker = "__OMS_HOST_CANCELLED__";

    private sealed record ScriptReference(
        string BindingId,
        BehaviorCatalogKind Kind,
        string OwnerType,
        string BehaviorName,
        BehaviorCatalogCallbackSlot Slot,
        CallbackShape Shape);

    private sealed record ScriptInvocationValue(
        Engine Engine,
        JsValue Value,
        JintBehaviorScriptOptions Options);

    private readonly record struct NormalizedBindingRequest(
        BehaviorCatalog Catalog,
        ImmutableArray<JintBehaviorScriptDefinition> Definitions,
        JintBehaviorScriptOptions Options,
        BehaviorCallbackBindingSet NativeCallbacks);

    private enum CallbackShape
    {
        Constraint,
        Validator,
        Computed,
        Action,
        Mutation,
        Interceptor,
    }
}
