using System.IO;

namespace LlmWpfPrototype.Services;

public static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string ProjectRoot => _projectRoot.Value;

    public static string UserDataRoot => ProjectRoot;

    public static string LogsDirectory => EnsureDirectory(Path.Combine(UserDataRoot, "logs"));

    public static string ReportsDirectory => EnsureDirectory(Path.Combine(UserDataRoot, "reports"));

    public static string DataDirectory => EnsureDirectory(Path.Combine(UserDataRoot, "data"));

    public static string UserConfigDirectory => EnsureDirectory(Path.Combine(UserDataRoot, "Config"));

    public static string BundledConfigDirectory => Path.Combine(BaseDirectory, "Config");

    private static readonly Lazy<string> _projectRoot = new(ResolveProjectRoot);

    public static string GetBundledConfigPath(string fileName)
    {
        return Path.Combine(BundledConfigDirectory, fileName);
    }

    public static string GetUserConfigPath(string fileName)
    {
        return Path.Combine(UserConfigDirectory, fileName);
    }

    public static string EnsureSeededUserConfig(string fileName)
    {
        var userConfigPath = GetUserConfigPath(fileName);
        if (File.Exists(userConfigPath))
        {
            return userConfigPath;
        }

        var bundledConfigPath = GetBundledConfigPath(fileName);
        if (File.Exists(bundledConfigPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(userConfigPath)!);
            File.Copy(bundledConfigPath, userConfigPath, overwrite: false);
            return userConfigPath;
        }

        return userConfigPath;
    }

    private static string EnsureDirectory(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

    private static string ResolveProjectRoot()
    {
        var current = new DirectoryInfo(BaseDirectory);
        while (current is not null)
        {
            var projectFilePath = Path.Combine(current.FullName, "LlmWpfPrototype.csproj");
            if (File.Exists(projectFilePath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return BaseDirectory;
    }
}
