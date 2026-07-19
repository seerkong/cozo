using System.Reflection;
using System.Runtime.InteropServices;

namespace Cozo.DotNet.LlmWiki.SemanticParsing;

[StructLayout(LayoutKind.Sequential)]
internal struct TsNode
{
    public uint Context0, Context1, Context2, Context3;
    public IntPtr Id;
    public IntPtr Tree;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TsPoint
{
    public uint Row;
    public uint Column;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TsQueryCapture
{
    public TsNode Node;
    public uint Index;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TsQueryMatch
{
    public uint Id;
    public ushort PatternIndex;
    public ushort CaptureCount;
    public IntPtr Captures; // TsQueryCapture*
}

/// <summary>
/// P/Invoke surface for libtree-sitter plus the pinned grammar dylibs.
/// Library resolution mirrors CozoNative: probe AppContext.BaseDirectory and
/// runtimes/&lt;rid&gt;/native via NativeLibrary.SetDllImportResolver.
/// </summary>
internal static class TreeSitterNative
{
    internal const string RuntimeLibrary = "tree-sitter";
    internal const string CSharpLibrary = "tree-sitter-c-sharp";
    internal const string TypeScriptLibrary = "tree-sitter-typescript";
    internal const string JavaLibrary = "tree-sitter-java";

    /// <summary>Grammar ABI window supported by the bundled libtree-sitter runtime (v0.27.0: TREE_SITTER_MIN_COMPATIBLE_LANGUAGE_VERSION..TREE_SITTER_LANGUAGE_VERSION).</summary>
    public const uint MinCompatibleLanguageAbi = 13;
    public const uint MaxSupportedLanguageAbi = 15;

    private static readonly object Gate = new();
    private static IReadOnlyList<string>? _additionalSearchDirectories;

    static TreeSitterNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(TreeSitterNative).Assembly, Resolve);
    }

    /// <summary>Extra directories consulted first by both availability probing and DllImport resolution. Used by TryCreate injection (tests / custom deployments).</summary>
    internal static IReadOnlyList<string>? AdditionalSearchDirectories
    {
        get { lock (Gate) { return _additionalSearchDirectories; } }
        set { lock (Gate) { _additionalSearchDirectories = value; } }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is not (RuntimeLibrary or CSharpLibrary or TypeScriptLibrary or JavaLibrary))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in GetCandidatePaths(libraryName))
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    internal static IEnumerable<string> GetCandidatePaths(string libraryName, IReadOnlyList<string>? probeDirectories = null)
    {
        var fileName = GetPlatformLibraryFileName(libraryName);
        foreach (var dir in GetSearchDirectories(probeDirectories))
        {
            yield return Path.Combine(dir, fileName);
        }
    }

    private static IEnumerable<string> GetSearchDirectories(IReadOnlyList<string>? probeDirectories)
    {
        if (probeDirectories is not null)
        {
            foreach (var dir in probeDirectories)
            {
                yield return dir;
            }

            yield break;
        }

        var additional = AdditionalSearchDirectories;
        if (additional is not null)
        {
            foreach (var dir in additional)
            {
                yield return dir;
            }
        }

        var baseDir = AppContext.BaseDirectory;
        yield return baseDir;
        yield return Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
    }

    internal static string GetPlatformLibraryFileName(string libraryName)
    {
        if (OperatingSystem.IsWindows())
        {
            return libraryName + ".dll";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "lib" + libraryName + ".dylib";
        }

        return "lib" + libraryName + ".so";
    }

    /// <summary>Probe whether a native library can actually be loaded from the given directories (without binding DllImport).</summary>
    internal static bool TryProbeLibrary(string libraryName, IReadOnlyList<string>? probeDirectories, out string? loadedFrom)
    {
        foreach (var candidate in GetCandidatePaths(libraryName, probeDirectories))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                // Intentionally do not free: dlopen is reference-counted and the library will be reused by DllImport.
                loadedFrom = candidate;
                return true;
            }
        }

        loadedFrom = null;
        return false;
    }

    // ---- runtime API ----
    [DllImport(RuntimeLibrary)] internal static extern IntPtr ts_parser_new();
    [DllImport(RuntimeLibrary)] internal static extern void ts_parser_delete(IntPtr parser);
    [DllImport(RuntimeLibrary)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool ts_parser_set_language(IntPtr parser, IntPtr language);
    [DllImport(RuntimeLibrary)] internal static extern IntPtr ts_parser_parse_string(IntPtr parser, IntPtr oldTree, byte[] source, uint length);

    [DllImport(RuntimeLibrary)] internal static extern TsNode ts_tree_root_node(IntPtr tree);
    [DllImport(RuntimeLibrary)] internal static extern void ts_tree_delete(IntPtr tree);

    [DllImport(RuntimeLibrary)] internal static extern IntPtr ts_node_string(TsNode node); // malloc'd; caller must FreeCString
    [DllImport(RuntimeLibrary)] internal static extern uint ts_node_child_count(TsNode node);
    [DllImport(RuntimeLibrary)] internal static extern IntPtr ts_node_type(TsNode node);
    [DllImport(RuntimeLibrary)] internal static extern uint ts_node_start_byte(TsNode node);
    [DllImport(RuntimeLibrary)] internal static extern uint ts_node_end_byte(TsNode node);
    [DllImport(RuntimeLibrary)] internal static extern TsPoint ts_node_start_point(TsNode node);
    [DllImport(RuntimeLibrary)] internal static extern TsPoint ts_node_end_point(TsNode node);
    [DllImport(RuntimeLibrary)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool ts_node_has_error(TsNode node);
    [DllImport(RuntimeLibrary)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool ts_node_is_null(TsNode node);

    // tree-sitter 0.25+ renamed ts_language_version to ts_language_abi_version.
    [DllImport(RuntimeLibrary, EntryPoint = "ts_language_abi_version")] internal static extern uint ts_language_abi_version(IntPtr language);

    // ---- query API ----
    [DllImport(RuntimeLibrary)] internal static extern IntPtr ts_query_new(IntPtr language, byte[] source, uint length, out uint errorOffset, out int errorType);
    [DllImport(RuntimeLibrary)] internal static extern void ts_query_delete(IntPtr query);
    [DllImport(RuntimeLibrary)] internal static extern IntPtr ts_query_capture_name_for_id(IntPtr query, uint captureId, out uint length);
    [DllImport(RuntimeLibrary)] internal static extern IntPtr ts_query_cursor_new();
    [DllImport(RuntimeLibrary)] internal static extern void ts_query_cursor_delete(IntPtr cursor);
    [DllImport(RuntimeLibrary)] internal static extern void ts_query_cursor_exec(IntPtr cursor, IntPtr query, TsNode node);
    [DllImport(RuntimeLibrary)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool ts_query_cursor_next_match(IntPtr cursor, out TsQueryMatch match);

    // ---- grammar entry points ----
    [DllImport(CSharpLibrary)] internal static extern IntPtr tree_sitter_c_sharp();
    [DllImport(TypeScriptLibrary)] internal static extern IntPtr tree_sitter_typescript();
    [DllImport(JavaLibrary)] internal static extern IntPtr tree_sitter_java();

    // ---- libc free for ts_node_string ----
    [DllImport("libSystem.B.dylib", EntryPoint = "free")] private static extern void FreeDarwin(IntPtr ptr);
    [DllImport("libc", EntryPoint = "free")] private static extern void FreeLibc(IntPtr ptr);

    internal static void FreeCString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            FreeDarwin(ptr);
        }
        else
        {
            FreeLibc(ptr);
        }
    }

    internal static string ConsumeNodeString(TsNode node)
    {
        var ptr = ts_node_string(node);
        try
        {
            return Marshal.PtrToStringAnsi(ptr) ?? "";
        }
        finally
        {
            FreeCString(ptr);
        }
    }
}

internal sealed class TsParserHandle : SafeHandle
{
    private TsParserHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    public static TsParserHandle Create()
    {
        var wrapper = new TsParserHandle();
        wrapper.SetHandle(TreeSitterNative.ts_parser_new());
        return wrapper;
    }

    protected override bool ReleaseHandle()
    {
        TreeSitterNative.ts_parser_delete(handle);
        return true;
    }
}

internal sealed class TsTreeHandle : SafeHandle
{
    private TsTreeHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    public static TsTreeHandle Wrap(IntPtr tree)
    {
        var wrapper = new TsTreeHandle();
        wrapper.SetHandle(tree);
        return wrapper;
    }

    public TsNode RootNode() => TreeSitterNative.ts_tree_root_node(handle);

    protected override bool ReleaseHandle()
    {
        TreeSitterNative.ts_tree_delete(handle);
        return true;
    }
}

internal sealed class TsQueryHandle : SafeHandle
{
    private TsQueryHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    public IntPtr Value => handle;

    public static TsQueryHandle Wrap(IntPtr query)
    {
        var wrapper = new TsQueryHandle();
        wrapper.SetHandle(query);
        return wrapper;
    }

    protected override bool ReleaseHandle()
    {
        TreeSitterNative.ts_query_delete(handle);
        return true;
    }
}

internal sealed class TsQueryCursorHandle : SafeHandle
{
    private TsQueryCursorHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    public IntPtr Value => handle;

    public static TsQueryCursorHandle Create()
    {
        var wrapper = new TsQueryCursorHandle();
        wrapper.SetHandle(TreeSitterNative.ts_query_cursor_new());
        return wrapper;
    }

    protected override bool ReleaseHandle()
    {
        TreeSitterNative.ts_query_cursor_delete(handle);
        return true;
    }
}
