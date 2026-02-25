using System.Text.Json;
using Cozo.DotNet.Om.Contracts;
using Cozo.DotNet;

namespace Cozo.DotNet.Om.Support;

public sealed class CozoDbOmStore : ICozoOmStore
{
    private readonly CozoDb _db;

    public CozoDbOmStore(CozoDb db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public Task<OmQueryResult> RunAsync(
        string script,
        object? parameters = null,
        bool immutable = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = _db.Run(script, parameters, immutable);
        var root = document.RootElement.Clone();
        if (!root.TryGetProperty("ok", out var okElement) || !okElement.GetBoolean())
        {
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : root.GetRawText();
            throw new CozoException(message ?? "Cozo query failed.", root.GetRawText());
        }

        var headers = new List<string>();
        if (root.TryGetProperty("headers", out var headersElement) && headersElement.ValueKind == JsonValueKind.Array)
        {
            headers.AddRange(headersElement.EnumerateArray().Select(h => h.GetString() ?? string.Empty));
        }

        var rows = new List<IReadOnlyList<JsonElement>>();
        if (root.TryGetProperty("rows", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var rowElement in rowsElement.EnumerateArray())
            {
                rows.Add(rowElement.EnumerateArray().Select(cell => cell.Clone()).ToArray());
            }
        }

        return Task.FromResult(new OmQueryResult(headers, rows, root));
    }

    public Task<ICozoOmTransaction> BeginTransactionAsync(
        bool write = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ICozoOmTransaction>(new CozoDbOmTransaction(_db.BeginTransaction(write)));
    }
}

internal sealed class CozoDbOmTransaction : ICozoOmTransaction
{
    private readonly CozoTransaction _transaction;
    private int _closed;

    public CozoDbOmTransaction(CozoTransaction transaction)
    {
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
    }

    public Task<OmQueryResult> RunAsync(
        string script,
        object? parameters = null,
        bool immutable = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = _transaction.Run(script, parameters);
        var root = document.RootElement.Clone();
        if (!root.TryGetProperty("ok", out var okElement) || !okElement.GetBoolean())
        {
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : root.GetRawText();
            throw new CozoException(message ?? "Cozo query failed.", root.GetRawText());
        }

        var headers = new List<string>();
        if (root.TryGetProperty("headers", out var headersElement) && headersElement.ValueKind == JsonValueKind.Array)
        {
            headers.AddRange(headersElement.EnumerateArray().Select(h => h.GetString() ?? string.Empty));
        }

        var rows = new List<IReadOnlyList<JsonElement>>();
        if (root.TryGetProperty("rows", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var rowElement in rowsElement.EnumerateArray())
            {
                rows.Add(rowElement.EnumerateArray().Select(cell => cell.Clone()).ToArray());
            }
        }

        return Task.FromResult(new OmQueryResult(headers, rows, root));
    }

    public Task<ICozoOmTransaction> BeginTransactionAsync(
        bool write = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ICozoOmTransaction>(new NestedCozoOmTransaction(this));
    }

    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            using var document = _transaction.Commit();
            ThrowIfNotOk(document.RootElement.Clone());
        }

        return Task.CompletedTask;
    }

    public Task AbortAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            using var document = _transaction.Abort();
            ThrowIfNotOk(document.RootElement.Clone());
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await AbortAsync();
    }

    private static void ThrowIfNotOk(JsonElement root)
    {
        if (!root.TryGetProperty("ok", out var okElement) || !okElement.GetBoolean())
        {
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : root.GetRawText();
            throw new CozoException(message ?? "Cozo transaction failed.", root.GetRawText());
        }
    }
}

internal sealed class NestedCozoOmTransaction : ICozoOmTransaction
{
    private readonly ICozoOmTransaction _inner;

    public NestedCozoOmTransaction(ICozoOmTransaction inner)
    {
        _inner = inner;
    }

    public Task<OmQueryResult> RunAsync(string script, object? parameters = null, bool immutable = false, CancellationToken cancellationToken = default) =>
        _inner.RunAsync(script, parameters, immutable, cancellationToken);

    public Task<ICozoOmTransaction> BeginTransactionAsync(bool write = true, CancellationToken cancellationToken = default) =>
        Task.FromResult<ICozoOmTransaction>(new NestedCozoOmTransaction(_inner));

    public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
