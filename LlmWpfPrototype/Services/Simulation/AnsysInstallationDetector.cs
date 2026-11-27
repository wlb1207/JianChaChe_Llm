using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LlmWpfPrototype.Services.Simulation;

public sealed record AnsysInstallationInfo(
    string LauncherPath,
    string SolverPath,
    string VersionTag,
    string DiscoverySource);

/// <summary>
/// Cross-machine, cross-version (ANSYS 18.0 / v180 through 2025 R1+ / v251+) silent ANSYS APDL installation
/// detector. Uses a 4-tier resolution order so developer paths never need to be hardcoded:
///   1. Official ANSYS environment variables (AWP_ROOT*, ANSYS*_DIR) — instant hit on ANSYS 18.0~2025+
///   2. Windows Registry (HKLM\SOFTWARE\ANSYS, Inc. and Uninstall keys)
///   3. Fixed-drive standard installation folders (Program Files\ANSYS Inc\v*, ANSYS*\...\v*)
///   4. Silent per-machine cache in %LocalAppData%\SanliuBridgeCar\machine_env.json
/// </summary>
public static class AnsysInstallationDetector
{
    private static readonly Regex AwpRootEnvPattern = new(@"^AWP_ROOT(?<ver>\d{2,4})$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AnsysDirEnvPattern = new(@"^ANSYS(?<ver>\d{2,4})_DIR$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VersionFolderPattern = new(@"^v(?<ver>\d{2,4})$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AnsysProcessPattern = new(@"^(?:ansys\d{2,4}|ansys|launcher|mapdl)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VersionedWrapperExePattern = new(@"^ANSYS\d{2,4}\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class MachineEnvCache
    {
        public string AnsysLauncherPath { get; set; } = string.Empty;
        public string AnsysSolverPath { get; set; } = string.Empty;
    }

    public static string MachineCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SanliuBridgeCar",
        "machine_env.json");

    public static bool IsAnsysProcessName(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && AnsysProcessPattern.IsMatch(processName.Trim());

    public static AnsysInstallationInfo? DetectBestInstallation(
        string? configuredLauncherPath = null,
        string? configuredSolverPath = null)
    {
        // Tier 0: Explicit valid configured paths (preserves unit test doubles and explicit overrides)
        var validConfiguredLauncher = IsValidLauncherFile(configuredLauncherPath) ? Path.GetFullPath(configuredLauncherPath!) : string.Empty;
        var validConfiguredSolver = IsValidSolverFile(configuredSolverPath) ? Path.GetFullPath(configuredSolverPath!) : string.Empty;
        if (!string.IsNullOrEmpty(validConfiguredLauncher))
        {
            var resolvedSolver = !string.IsNullOrEmpty(validConfiguredSolver)
                ? validConfiguredSolver
                : ResolveCompanionSolverInDirectory(Path.GetDirectoryName(validConfiguredLauncher)!);
            return new AnsysInstallationInfo(
                validConfiguredLauncher,
                resolvedSolver,
                ExtractVersionTagFromPath(validConfiguredLauncher),
                "configured");
        }

        // Tier 1: Official ANSYS environment variables (AWP_ROOT180..AWP_ROOT251, ANSYS180_DIR..)
        var fromEnv = DetectFromEnvironmentVariables(GetProcessEnvironmentDictionary());
        if (fromEnv is not null)
        {
            if (!string.IsNullOrEmpty(validConfiguredSolver))
            {
                return fromEnv with { SolverPath = validConfiguredSolver };
            }
            return fromEnv;
        }

        // Tier 2: Windows Registry
        var fromReg = DetectFromRegistry();
        if (fromReg is not null)
        {
            return !string.IsNullOrEmpty(validConfiguredSolver) ? fromReg with { SolverPath = validConfiguredSolver } : fromReg;
        }

        // Tier 3: Fixed drives standard directories (C:\..H:\)
        var fromDisk = DetectFromFixedDrives();
        if (fromDisk is not null)
        {
            return !string.IsNullOrEmpty(validConfiguredSolver) ? fromDisk with { SolverPath = validConfiguredSolver } : fromDisk;
        }

        // Tier 4: Per-machine silent user cache (%LocalAppData%)
        var fromCache = LoadFromMachineCache();
        if (fromCache is not null)
        {
            return !string.IsNullOrEmpty(validConfiguredSolver) ? fromCache with { SolverPath = validConfiguredSolver } : fromCache;
        }

        return null;
    }

    internal static AnsysInstallationInfo? DetectFromEnvironmentVariables(
        IReadOnlyDictionary<string, string> envVars,
        Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        var candidates = new List<(int VersionNum, string VersionTag, string BinDir, string Source)>();

        foreach (var (name, rawValue) in envVars)
        {
            if (string.IsNullOrWhiteSpace(rawValue)) continue;
            var val = rawValue.Trim().Trim('"');

            var awpMatch = AwpRootEnvPattern.Match(name);
            if (awpMatch.Success && int.TryParse(awpMatch.Groups["ver"].Value, out var awpVer))
            {
                candidates.Add((awpVer, $"v{awpVer}", Path.Combine(val, "ANSYS", "bin", "winx64"), $"env:{name}"));
                continue;
            }

            var dirMatch = AnsysDirEnvPattern.Match(name);
            if (dirMatch.Success && int.TryParse(dirMatch.Groups["ver"].Value, out var dirVer))
            {
                candidates.Add((dirVer, $"v{dirVer}", Path.Combine(val, "bin", "winx64"), $"env:{name}"));
            }
        }

        // Highest installed version first (e.g. v251 > v242 > v232 > v180), hitting v180 immediately when it's installed.
        foreach (var candidate in candidates.OrderByDescending(c => c.VersionNum))
        {
            var launcher = Path.Combine(candidate.BinDir, "launcher.exe");
            if (!exists(launcher)) continue;

            var solver = ResolveCompanionSolverInDirectory(candidate.BinDir, candidate.VersionNum, exists);
            return new AnsysInstallationInfo(launcher, solver, candidate.VersionTag, candidate.Source);
        }

        return null;
    }

    public static void SaveUserPickedLauncherPath(string launcherPath)
    {
        if (!IsValidLauncherFile(launcherPath)) return;
        try
        {
            var fullLauncher = Path.GetFullPath(launcherPath);
            var fullSolver = ResolveCompanionSolverInDirectory(Path.GetDirectoryName(fullLauncher)!);
            var cacheDir = Path.GetDirectoryName(MachineCachePath)!;
            Directory.CreateDirectory(cacheDir);
            var json = JsonSerializer.Serialize(new MachineEnvCache
            {
                AnsysLauncherPath = fullLauncher,
                AnsysSolverPath = fullSolver
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(MachineCachePath, json, new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    public static string ResolveCompanionSolverInDirectory(
        string binDirectory,
        int? preferredVersionNum = null,
        Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        if (string.IsNullOrWhiteSpace(binDirectory)) return string.Empty;

        var ansysExe = Path.Combine(binDirectory, "ANSYS.exe");
        if (exists(ansysExe)) return ansysExe;

        if (preferredVersionNum.HasValue)
        {
            var verExe = Path.Combine(binDirectory, $"ANSYS{preferredVersionNum.Value}.exe");
            if (exists(verExe)) return verExe;
        }

        var mapdlExe = Path.Combine(binDirectory, "mapdl.exe");
        if (exists(mapdlExe)) return mapdlExe;

        if (fileExists is null && Directory.Exists(binDirectory))
        {
            try
            {
                var wrapper = Directory.EnumerateFiles(binDirectory, "ANSYS*.exe", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(p => VersionedWrapperExePattern.IsMatch(Path.GetFileName(p)));
                if (!string.IsNullOrWhiteSpace(wrapper)) return wrapper;
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static AnsysInstallationInfo? DetectFromRegistry()
    {
        var roots = new List<string>();
        try
        {
            foreach (var regPath in new[]
                     {
                         @"SOFTWARE\ANSYS, Inc.",
                         @"SOFTWARE\WOW6432Node\ANSYS, Inc.",
                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                     })
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(regPath);
                if (baseKey is null) continue;
                foreach (var subName in baseKey.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = baseKey.OpenSubKey(subName);
                        if (sub is null) continue;
                        var display = sub.GetValue("DisplayName") as string ?? subName;
                        if (!display.Contains("ANSYS", StringComparison.OrdinalIgnoreCase)) continue;
                        var loc = (sub.GetValue("InstallLocation") as string)
                                  ?? (sub.GetValue("InstallDir") as string)
                                  ?? (sub.GetValue("Path") as string);
                        if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
                        {
                            roots.Add(loc.Trim().Trim('"'));
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        return ScanCandidateRoots(roots, "registry");
    }

    private static AnsysInstallationInfo? DetectFromFixedDrives()
    {
        var roots = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                var root = drive.RootDirectory.FullName;
                var pf = Path.Combine(root, "Program Files", "ANSYS Inc");
                if (Directory.Exists(pf)) roots.Add(pf);

                var topAnsysInc = Path.Combine(root, "ANSYS Inc");
                if (Directory.Exists(topAnsysInc)) roots.Add(topAnsysInc);

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root, "ANSYS*", SearchOption.TopDirectoryOnly))
                    {
                        roots.Add(dir);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return ScanCandidateRoots(roots, "disk-scan");
    }

    private static AnsysInstallationInfo? ScanCandidateRoots(IEnumerable<string> roots, string source)
    {
        var found = new List<(int VersionNum, string VersionTag, string BinDir)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void CollectVersionFolders(string dir, int depth)
        {
            if (depth > 3 || !visited.Add(dir) || !Directory.Exists(dir)) return;

            var name = Path.GetFileName(dir);
            var verMatch = VersionFolderPattern.Match(name);
            if (verMatch.Success && int.TryParse(verMatch.Groups["ver"].Value, out var verNum))
            {
                var binDir = Path.Combine(dir, "ANSYS", "bin", "winx64");
                if (File.Exists(Path.Combine(binDir, "launcher.exe")))
                {
                    found.Add((verNum, $"v{verNum}", binDir));
                }
                return;
            }

            // Direct ANSYS/bin/winx64 under root
            var directBin = Path.Combine(dir, "ANSYS", "bin", "winx64");
            if (File.Exists(Path.Combine(directBin, "launcher.exe")))
            {
                var tag = ExtractVersionTagFromPath(directBin);
                var num = ParseVersionNumber(tag);
                found.Add((num, tag, directBin));
                return;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    var childName = Path.GetFileName(child);
                    if (VersionFolderPattern.IsMatch(childName) ||
                        childName.Contains("ANSYS", StringComparison.OrdinalIgnoreCase))
                    {
                        CollectVersionFolders(child, depth + 1);
                    }
                }
            }
            catch
            {
            }
        }

        foreach (var root in roots)
        {
            CollectVersionFolders(root, 0);
        }

        var best = found.OrderByDescending(f => f.VersionNum).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(best.BinDir)) return null;

        var launcher = Path.Combine(best.BinDir, "launcher.exe");
        var solver = ResolveCompanionSolverInDirectory(best.BinDir, best.VersionNum > 0 ? best.VersionNum : null);
        return new AnsysInstallationInfo(launcher, solver, best.VersionTag, source);
    }

    private static AnsysInstallationInfo? LoadFromMachineCache()
    {
        try
        {
            if (!File.Exists(MachineCachePath)) return null;
            var cache = JsonSerializer.Deserialize<MachineEnvCache>(File.ReadAllText(MachineCachePath, Encoding.UTF8));
            if (cache is null || !IsValidLauncherFile(cache.AnsysLauncherPath)) return null;
            var solver = IsValidSolverFile(cache.AnsysSolverPath)
                ? cache.AnsysSolverPath
                : ResolveCompanionSolverInDirectory(Path.GetDirectoryName(cache.AnsysLauncherPath)!);
            return new AnsysInstallationInfo(
                cache.AnsysLauncherPath,
                solver,
                ExtractVersionTagFromPath(cache.AnsysLauncherPath),
                "machine-cache");
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> GetProcessEnvironmentDictionary()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v)
            {
                dict[k] = v;
            }
        }
        return dict;
    }

    private static bool IsValidLauncherFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetFileName(path), "launcher.exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);

    private static bool IsValidSolverFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) &&
        File.Exists(path);

    internal static string ExtractVersionTagFromPath(string path)
    {
        var match = Regex.Match(path ?? string.Empty, @"[\\/](v\d{2,4})[\\/]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : "auto";
    }

    private static int ParseVersionNumber(string tag)
    {
        var digits = Regex.Match(tag ?? string.Empty, @"\d{2,4}");
        return digits.Success && int.TryParse(digits.Value, out var n) ? n : 0;
    }
}
