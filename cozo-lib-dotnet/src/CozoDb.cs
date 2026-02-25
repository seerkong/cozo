using System.Text.Json;
using System.Threading;

namespace Cozo.DotNet;

public sealed class CozoDb : IDisposable
{
    private readonly int _dbId;
    private int _closed;

    public CozoDb(string engine = "mem", string path = "data.db", object? options = null)
    {
        var err = CozoNative.OpenDb(engine, path, ToJson(options, "{}"), out var dbId);
        if (!string.IsNullOrEmpty(err))
        {
            throw new CozoException($"Failed to open Cozo DB: {err}", err);
        }

        _dbId = dbId;
    }

    ~CozoDb()
    {
        Close();
    }

    public bool Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return false;
        }

        return CozoNative.CloseDb(_dbId);
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    public string RunRaw(string script, object? parameters = null, bool immutable = false)
    {
        ThrowIfClosed();
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new ArgumentException("Query script cannot be empty.", nameof(script));
        }

        return CozoNative.RunQuery(_dbId, script, ToJson(parameters, "{}"), immutable);
    }

    public JsonDocument Run(string script, object? parameters = null, bool immutable = false)
    {
        return ParseJson(RunRaw(script, parameters, immutable));
    }

    public CozoTransaction BeginTransaction(bool write = true)
    {
        ThrowIfClosed();
        var err = CozoNative.MultiTransact(_dbId, write, out var txId);
        if (!string.IsNullOrEmpty(err))
        {
            throw new CozoException($"Failed to start Cozo transaction: {err}", err);
        }

        return new CozoTransaction(txId);
    }

    public string ImportRelationsRaw(object data)
    {
        ThrowIfClosed();
        return CozoNative.ImportRelations(_dbId, ToJson(data, "{}"));
    }

    public JsonDocument ImportRelations(object data)
    {
        return ParseJson(ImportRelationsRaw(data));
    }

    public string ExportRelationsRaw(object request)
    {
        ThrowIfClosed();
        return CozoNative.ExportRelations(_dbId, ToJson(request, "{}"));
    }

    public JsonDocument ExportRelations(object request)
    {
        return ParseJson(ExportRelationsRaw(request));
    }

    public string BackupRaw(string outPath)
    {
        ThrowIfClosed();
        if (string.IsNullOrWhiteSpace(outPath))
        {
            throw new ArgumentException("Backup path cannot be empty.", nameof(outPath));
        }

        return CozoNative.Backup(_dbId, outPath);
    }

    public JsonDocument Backup(string outPath)
    {
        return ParseJson(BackupRaw(outPath));
    }

    public string RestoreRaw(string inPath)
    {
        ThrowIfClosed();
        if (string.IsNullOrWhiteSpace(inPath))
        {
            throw new ArgumentException("Restore path cannot be empty.", nameof(inPath));
        }

        return CozoNative.Restore(_dbId, inPath);
    }

    public JsonDocument Restore(string inPath)
    {
        return ParseJson(RestoreRaw(inPath));
    }

    public string ImportFromBackupRaw(object request)
    {
        ThrowIfClosed();
        return CozoNative.ImportFromBackup(_dbId, ToJson(request, "{}"));
    }

    public JsonDocument ImportFromBackup(object request)
    {
        return ParseJson(ImportFromBackupRaw(request));
    }

    private static JsonDocument ParseJson(string raw)
    {
        try
        {
            return JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new CozoException("Cozo returned invalid JSON.", raw, ex);
        }
    }

    private static string ToJson(object? value, string defaultJson)
    {
        if (value is null)
        {
            return defaultJson;
        }

        if (value is string raw)
        {
            return string.IsNullOrWhiteSpace(raw) ? defaultJson : raw;
        }

        return JsonSerializer.Serialize(value);
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            throw new ObjectDisposedException(nameof(CozoDb));
        }
    }

    internal static JsonDocument ParseRawJson(string raw) => ParseJson(raw);

    internal static string SerializeParameters(object? value, string defaultJson) => ToJson(value, defaultJson);
}

public sealed class CozoTransaction : IDisposable
{
    private readonly int _txId;
    private int _closed;

    internal CozoTransaction(int txId)
    {
        _txId = txId;
    }

    ~CozoTransaction()
    {
        Abort();
    }

    public string RunRaw(string script, object? parameters = null)
    {
        ThrowIfClosed();
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new ArgumentException("Query script cannot be empty.", nameof(script));
        }

        return CozoNative.RunTx(_txId, script, CozoDb.SerializeParameters(parameters, "{}"));
    }

    public JsonDocument Run(string script, object? parameters = null)
    {
        return CozoDb.ParseRawJson(RunRaw(script, parameters));
    }

    public string CommitRaw()
    {
        ThrowIfClosed();
        Interlocked.Exchange(ref _closed, 1);
        return CozoNative.CommitTx(_txId);
    }

    public JsonDocument Commit()
    {
        return CozoDb.ParseRawJson(CommitRaw());
    }

    public string AbortRaw()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return """{"ok":true}""";
        }

        return CozoNative.AbortTx(_txId);
    }

    public JsonDocument Abort()
    {
        return CozoDb.ParseRawJson(AbortRaw());
    }

    public void Dispose()
    {
        Abort();
        GC.SuppressFinalize(this);
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            throw new ObjectDisposedException(nameof(CozoTransaction));
        }
    }
}
