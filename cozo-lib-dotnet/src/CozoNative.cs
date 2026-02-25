using System.Reflection;
using System.Runtime.InteropServices;

namespace Cozo.DotNet;

internal static partial class CozoNative
{
    private const string LibraryName = "cozo_c";

    static CozoNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(CozoNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in GetCandidatePaths())
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        var baseDir = AppContext.BaseDirectory;
        var fileName = GetPlatformLibraryFileName();
        var rid = RuntimeInformation.RuntimeIdentifier;

        yield return Path.Combine(baseDir, fileName);
        yield return Path.Combine(baseDir, "runtimes", rid, "native", fileName);

        if (OperatingSystem.IsMacOS() && fileName.StartsWith("lib", StringComparison.Ordinal))
        {
            var noPrefix = fileName.Substring(3);
            yield return Path.Combine(baseDir, noPrefix);
            yield return Path.Combine(baseDir, "runtimes", rid, "native", noPrefix);
        }
    }

    private static string GetPlatformLibraryFileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "cozo_c.dll";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "libcozo_c.dylib";
        }

        return "libcozo_c.so";
    }

    [LibraryImport(LibraryName, EntryPoint = "cozo_open_db", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr OpenDbNative(string engine, string path, string options, out int dbId);

    [LibraryImport(LibraryName, EntryPoint = "cozo_close_db")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CloseDbNative(int dbId);

    [LibraryImport(LibraryName, EntryPoint = "cozo_run_query", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RunQueryNative(int dbId, string scriptRaw, string paramsRaw, [MarshalAs(UnmanagedType.I1)] bool immutableQuery);

    [LibraryImport(LibraryName, EntryPoint = "cozo_multi_transact")]
    private static partial IntPtr MultiTransactNative(int dbId, [MarshalAs(UnmanagedType.I1)] bool write, out int txId);

    [LibraryImport(LibraryName, EntryPoint = "cozo_run_tx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RunTxNative(int txId, string scriptRaw, string paramsRaw);

    [LibraryImport(LibraryName, EntryPoint = "cozo_commit_tx")]
    private static partial IntPtr CommitTxNative(int txId);

    [LibraryImport(LibraryName, EntryPoint = "cozo_abort_tx")]
    private static partial IntPtr AbortTxNative(int txId);

    [LibraryImport(LibraryName, EntryPoint = "cozo_import_relations", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr ImportRelationsNative(int dbId, string jsonPayload);

    [LibraryImport(LibraryName, EntryPoint = "cozo_export_relations", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr ExportRelationsNative(int dbId, string jsonPayload);

    [LibraryImport(LibraryName, EntryPoint = "cozo_backup", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr BackupNative(int dbId, string outPath);

    [LibraryImport(LibraryName, EntryPoint = "cozo_restore", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RestoreNative(int dbId, string inPath);

    [LibraryImport(LibraryName, EntryPoint = "cozo_import_from_backup", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr ImportFromBackupNative(int dbId, string jsonPayload);

    [LibraryImport(LibraryName, EntryPoint = "cozo_free_str")]
    private static partial void FreeStrNative(IntPtr ptr);

    internal static string? OpenDb(string engine, string path, string options, out int dbId)
    {
        var errPtr = OpenDbNative(engine, path, options, out dbId);
        if (errPtr == IntPtr.Zero)
        {
            return null;
        }

        return ConsumeString(errPtr);
    }

    internal static bool CloseDb(int dbId) => CloseDbNative(dbId);

    internal static string RunQuery(int dbId, string scriptRaw, string paramsRaw, bool immutableQuery) =>
        ConsumeString(RunQueryNative(dbId, scriptRaw, paramsRaw, immutableQuery));

    internal static string? MultiTransact(int dbId, bool write, out int txId)
    {
        var errPtr = MultiTransactNative(dbId, write, out txId);
        if (errPtr == IntPtr.Zero)
        {
            return null;
        }

        return ConsumeString(errPtr);
    }

    internal static string RunTx(int txId, string scriptRaw, string paramsRaw) =>
        ConsumeString(RunTxNative(txId, scriptRaw, paramsRaw));

    internal static string CommitTx(int txId) =>
        ConsumeString(CommitTxNative(txId));

    internal static string AbortTx(int txId) =>
        ConsumeString(AbortTxNative(txId));

    internal static string ImportRelations(int dbId, string jsonPayload) =>
        ConsumeString(ImportRelationsNative(dbId, jsonPayload));

    internal static string ExportRelations(int dbId, string jsonPayload) =>
        ConsumeString(ExportRelationsNative(dbId, jsonPayload));

    internal static string Backup(int dbId, string outPath) =>
        ConsumeString(BackupNative(dbId, outPath));

    internal static string Restore(int dbId, string inPath) =>
        ConsumeString(RestoreNative(dbId, inPath));

    internal static string ImportFromBackup(int dbId, string jsonPayload) =>
        ConsumeString(ImportFromBackupNative(dbId, jsonPayload));

    private static string ConsumeString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        }
        finally
        {
            FreeStrNative(ptr);
        }
    }
}
