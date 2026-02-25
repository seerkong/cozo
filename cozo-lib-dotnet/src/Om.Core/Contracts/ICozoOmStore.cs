using System.Text.Json;

namespace Cozo.DotNet.Om.Contracts;

public interface ICozoOmStore
{
    Task<OmQueryResult> RunAsync(
        string script,
        object? parameters = null,
        bool immutable = false,
        CancellationToken cancellationToken = default);

    Task<ICozoOmTransaction> BeginTransactionAsync(
        bool write = true,
        CancellationToken cancellationToken = default);
}

public interface ICozoOmTransaction : ICozoOmStore, IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);

    Task AbortAsync(CancellationToken cancellationToken = default);
}

public sealed record OmQueryResult(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<JsonElement>> Rows,
    JsonElement Raw);
