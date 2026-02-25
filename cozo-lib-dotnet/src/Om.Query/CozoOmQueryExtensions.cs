namespace Cozo.DotNet.Om.Query;

public static class CozoOmQueryExtensions
{
    public static OmQueryEngine CreateQueryEngine(this CozoOm om, OmQueryRegistry? registry = null) =>
        new(om, registry);

    public static Task<OmQueryExecutionResult> ExecuteNamedQueryAsync(
        this CozoOm om,
        NamedQueryInput input,
        OmQueryRegistry registry,
        CancellationToken cancellationToken = default) =>
        new OmQueryEngine(om, registry).ExecuteNamedAsync(input, cancellationToken);

    public static Task<OmQueryExecutionResult> ExecutePortableDatalogAsync(
        this CozoOm om,
        PortableDatalogQueryInput input,
        CancellationToken cancellationToken = default) =>
        new OmQueryEngine(om).ExecutePortableAsync(input, cancellationToken: cancellationToken);
}
