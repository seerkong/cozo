using Cozo.DotNet.Om.Contracts.Models;

namespace Cozo.DotNet.Om.Scripting.Jint;

/// <summary>Identifies the stage at which a script callback failed.</summary>
public enum JintBehaviorScriptFailurePhase
{
    /// <summary>Callable preflight or JavaScript compilation failed.</summary>
    Compile,

    /// <summary>JavaScript callback execution failed.</summary>
    Execution,

    /// <summary>An allowlisted asynchronous OM host operation failed.</summary>
    HostInvocation,

    /// <summary>The JavaScript result did not match the typed callback contract.</summary>
    ResultConversion,

    /// <summary>The wall-clock timeout expired.</summary>
    Timeout,

    /// <summary>A configured statement, recursion, or memory limit was exceeded.</summary>
    Limit,
}

/// <summary>
/// Represents a source-redacted, binding-aware failure raised while executing a generated callback.
/// </summary>
public sealed class JintBehaviorScriptException : Exception
{
    /// <summary>Creates a structured script callback exception.</summary>
    /// <param name="code">The stable runtime error code.</param>
    /// <param name="bindingId">The exact canonical binding ID.</param>
    /// <param name="kind">The canonical behavior kind.</param>
    /// <param name="slot">The canonical callback slot.</param>
    /// <param name="phase">The failure phase.</param>
    /// <param name="sourceName">The optional programmatic source name.</param>
    /// <param name="innerException">The underlying engine, host, or conversion exception.</param>
    /// <param name="line">The optional one-based source line.</param>
    /// <param name="column">The optional one-based source column.</param>
    /// <param name="detail">An optional fixed, source-redacted public detail.</param>
    public JintBehaviorScriptException(
        string code,
        string bindingId,
        BehaviorCatalogKind kind,
        BehaviorCatalogCallbackSlot slot,
        JintBehaviorScriptFailurePhase phase,
        string? sourceName,
        Exception innerException,
        int? line = null,
        int? column = null,
        string? detail = null)
        : base(BuildMessage(code, bindingId, kind, slot, phase, detail), innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(bindingId);
        ArgumentNullException.ThrowIfNull(innerException);
        if (line is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(line), "Script source line must be positive when provided.");
        }

        if (column is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(column), "Script source column must be positive when provided.");
        }

        Code = code;
        BindingId = bindingId;
        Kind = kind;
        Slot = slot;
        Phase = phase;
        SourceName = sourceName;
        Line = line;
        Column = column;
    }

    /// <summary>Gets the stable <c>OMS2xxx</c> runtime error code.</summary>
    public string Code { get; }

    /// <summary>Gets the exact canonical binding ID.</summary>
    public string BindingId { get; }

    /// <summary>Gets the canonical behavior kind.</summary>
    public BehaviorCatalogKind Kind { get; }

    /// <summary>Gets the canonical callback slot.</summary>
    public BehaviorCatalogCallbackSlot Slot { get; }

    /// <summary>Gets the failure phase.</summary>
    public JintBehaviorScriptFailurePhase Phase { get; }

    /// <summary>Gets the optional programmatic source name.</summary>
    public string? SourceName { get; }

    /// <summary>Gets the optional one-based source line.</summary>
    public int? Line { get; }

    /// <summary>Gets the optional one-based source column.</summary>
    public int? Column { get; }

    /// <summary>Returns the public source-redacted exception type and message.</summary>
    public override string ToString() => $"{GetType().FullName}: {Message}";

    internal static JintBehaviorScriptException Conversion(
        string bindingId,
        BehaviorCatalogKind kind,
        BehaviorCatalogCallbackSlot slot,
        string? sourceName,
        Exception innerException) =>
        new(
            "OMS2003",
            bindingId,
            kind,
            slot,
            JintBehaviorScriptFailurePhase.ResultConversion,
            sourceName,
            innerException);

    private static string BuildMessage(
        string code,
        string bindingId,
        BehaviorCatalogKind kind,
        BehaviorCatalogCallbackSlot slot,
        JintBehaviorScriptFailurePhase phase,
        string? detail) =>
        $"Script callback failed. Code='{code}', BindingId='{bindingId}', Kind='{kind}', Slot='{slot}', Phase='{phase}'."
        + (string.IsNullOrWhiteSpace(detail) ? "" : $" Detail: {detail}");
}
