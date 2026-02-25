namespace Cozo.DotNet.LlmWiki.Tools;

public sealed record CozoWikiStorageOptions(
    string Engine,
    string DbPath,
    string GlobalDataDirectory,
    string WorkDataDirectory)
{
    public static CozoWikiStorageOptions From(IReadOnlyDictionary<string, string> options, string defaultWorkDirectory)
    {
        var engine = options.GetValueOrDefault("--engine", "sqlite");
        var dataFolderName = options.GetValueOrDefault("--data-folder-name", ".depa-wiki");
        if (Path.IsPathRooted(dataFolderName) || dataFolderName.Contains(Path.DirectorySeparatorChar) || dataFolderName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("--data-folder-name must be a folder name, not a path.");
        }

        var globalDir = FullPath(options.GetValueOrDefault("--global-dir", "~/"));
        var workDir = FullPath(options.GetValueOrDefault("--work-dir", defaultWorkDirectory));
        var globalDataDir = Path.Combine(globalDir, dataFolderName);
        var workDataDir = Path.Combine(workDir, dataFolderName);
        Directory.CreateDirectory(globalDataDir);
        Directory.CreateDirectory(workDataDir);

        var dbPath = string.Equals(engine, "mem", StringComparison.Ordinal)
            ? ""
            : FullPath(options.GetValueOrDefault("--db", Path.Combine(workDataDir, "depa-wiki.db")));

        if (!string.Equals(engine, "mem", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? workDataDir);
        }

        return new CozoWikiStorageOptions(engine, dbPath, globalDataDir, workDataDir);
    }

    public static string FullPath(string path) => Path.GetFullPath(ExpandHome(path));

    public static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return HomeDirectory();
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(HomeDirectory(), path[2..]);
        }

        return path;
    }

    private static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? Directory.GetCurrentDirectory() : home;
    }
}
