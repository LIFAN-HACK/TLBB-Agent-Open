using TlbbAgent.Contracts;

namespace TlbbAgent.Core;
public sealed class AppInitService : IAppInitService
{
    private const string MigrationMarkerName = ".legacy-config-migrated-v1";
    private static readonly string[] MigratedDirectories =
    {
        "profiles",
        "data",
        "ocr",
        "backups",
        "imports",
        "legacy-imports"
    };

    public AppPaths Paths { get; }

    public AppInitService()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ProductIdentity.DataFolderName);

        Paths = new AppPaths
        {
            Root = root,
            Profiles = Path.Combine(root, "profiles"),
            Logs = Path.Combine(root, "logs"),
            Data = Path.Combine(root, "data"),
            Ocr = Path.Combine(root, "ocr"),
            Backups = Path.Combine(root, "backups"),
            LegacyImports = Path.Combine(root, "imports"),
        };
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Paths.Root);
        TryMigrateLegacyUserData();

        foreach (var dir in new[]
                 {
                     Paths.Root, Paths.Profiles, Paths.Logs, Paths.Data,
                     Paths.Ocr, Paths.Backups, Paths.LegacyImports
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }

    private void TryMigrateLegacyUserData()
    {
        var markerPath = Path.Combine(Paths.Root, MigrationMarkerName);
        if (File.Exists(markerPath))
            return;

        var legacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ProductIdentity.LegacyDataFolderName);
        if (!Directory.Exists(legacyRoot))
            return;

        var completed = true;
        foreach (var directoryName in MigratedDirectories)
        {
            var source = Path.Combine(legacyRoot, directoryName);
            if (!Directory.Exists(source))
                continue;

            completed &= CopyMissingFiles(source, Path.Combine(Paths.Root, directoryName));
        }

        if (!completed)
            return;

        try
        {
            File.WriteAllText(markerPath, "TLBB Agent legacy config migration completed.");
        }
        catch
        {
            // 下次启动重试；旧目录始终保留，不移动、不删除。
        }
    }

    private static bool CopyMissingFiles(string sourceRoot, string targetRoot)
    {
        var completed = true;
        var pending = new Stack<(string Source, string Target)>();
        pending.Push((sourceRoot, targetRoot));

        while (pending.Count > 0)
        {
            var (source, target) = pending.Pop();
            try
            {
                Directory.CreateDirectory(target);

                foreach (var file in Directory.EnumerateFiles(source))
                {
                    try
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                            continue;

                        var destination = Path.Combine(target, Path.GetFileName(file));
                        if (!File.Exists(destination))
                            File.Copy(file, destination, overwrite: false);
                    }
                    catch
                    {
                        completed = false;
                    }
                }

                foreach (var directory in Directory.EnumerateDirectories(source))
                {
                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                            continue;

                        pending.Push((directory, Path.Combine(target, Path.GetFileName(directory))));
                    }
                    catch
                    {
                        completed = false;
                    }
                }
            }
            catch
            {
                completed = false;
            }
        }

        return completed;
    }
}
