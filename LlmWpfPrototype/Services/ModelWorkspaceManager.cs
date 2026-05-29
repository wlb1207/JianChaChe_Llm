using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed class ModelWorkspaceManager
{
    private readonly ISolidWorksService? _solidWorksService;

    public ModelWorkspaceManager(SolidWorksWorkspaceOptions options, ISolidWorksService? solidWorksService = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _solidWorksService = solidWorksService;
        ApplyWorkspaceOptions(options);
        ClearWorkingModelSessionCache();
    }

    public string InitialModelFolder { get; private set; } = string.Empty;

    public string WorkingModelFolder { get; private set; } = string.Empty;

    public string ModelRootPath { get; private set; } = string.Empty;

    public string AssemblyFileName { get; private set; } = string.Empty;

    public bool IsWorkspaceInitialized { get; private set; }

    public string WorkingModelPath { get; private set; } = string.Empty;

    public string CurrentAssemblyPath { get; private set; } = string.Empty;

    public string LastOpenedModelPath { get; private set; } = string.Empty;

    public string LastOpenedDocumentPath { get; private set; } = string.Empty;

    public bool HasOpenedWorkingModel { get; private set; }

    public async Task EnsureWorkspaceInitializedAsync(Action<string>? logWriter = null)
    {
        if (IsWorkspaceInitialized)
        {
            logWriter?.Invoke($"[Workspace] 当前会话工作区已初始化，直接复用：{WorkingModelFolder}");
            return;
        }

        await Task.Run(() =>
        {
            ValidateConfiguration();
            EnsureDirectoriesAreDifferent();
            logWriter?.Invoke($"[Workspace] 开始初始化工作区：{WorkingModelFolder}");
            logWriter?.Invoke($"[Workspace] 源模板目录：{InitialModelFolder}");

            if (Directory.Exists(WorkingModelFolder))
            {
                logWriter?.Invoke($"[Workspace] 正在删除旧工作区目录：{WorkingModelFolder}");
            }

            DeleteWorkingModelFolderIfExists();
            logWriter?.Invoke("[Workspace] 正在复制初始模型到工作区");
            CopyDirectory(InitialModelFolder, WorkingModelFolder);
            var workingAssemblyPath = GetWorkingAssemblyPath();
            MarkWorkspaceInitialized(WorkingModelFolder, workingAssemblyPath, logWriter);
            logWriter?.Invoke($"[Workspace] 工作区初始化完成：{WorkingModelFolder}");
        });
    }

    public async Task EnsureWorkingModelAvailableForOpenAsync(Action<string>? logWriter = null)
    {
        ReloadWorkspaceConfigurationFromAppSettings(logWriter);
        await RecreateWorkingModelForOpenAsync(logWriter);
    }

    public async Task ResetWorkspaceAsync(Action<string>? logWriter = null)
    {
        await Task.Run(() =>
        {
            ValidateConfiguration();
            EnsureDirectoriesAreDifferent();
            logWriter?.Invoke($"[Workspace] 开始重置工作区：{WorkingModelFolder}");

            if (Directory.Exists(WorkingModelFolder))
            {
                logWriter?.Invoke($"[Workspace] 正在删除当前工作区目录：{WorkingModelFolder}");
            }

            DeleteWorkingModelFolderIfExists();
            logWriter?.Invoke("[Workspace] 正在从初始模型重新复制到工作区");
            CopyDirectory(InitialModelFolder, WorkingModelFolder);
            var workingAssemblyPath = GetWorkingAssemblyPath();
            MarkWorkspaceInitialized(WorkingModelFolder, workingAssemblyPath, logWriter);
            logWriter?.Invoke($"[Workspace] 工作区重置完成：{WorkingModelFolder}");
        });
    }

    public async Task RecreateWorkingModelForOpenAsync(Action<string>? logWriter = null)
    {
        ReloadWorkspaceConfigurationFromAppSettings(logWriter);

        if (_solidWorksService is not null)
        {
            var closeSucceeded = await _solidWorksService.CloseDocumentsUnderFolderAsync(WorkingModelFolder, logWriter);
            if (!closeSucceeded)
            {
                throw new InvalidOperationException("无法重置新模型目录，文件可能正在被 SolidWorks 占用，请关闭模型后重试。");
            }
        }

        await Task.Run(() =>
        {
            ValidateConfiguration();
            EnsureDirectoriesAreDifferent();

            logWriter?.Invoke($"[Workspace] 初始模型目录 = {InitialModelFolder}");
            logWriter?.Invoke($"[Workspace] 新模型目录 = {WorkingModelFolder}");
            logWriter?.Invoke($"[Workspace] 旧新模型目录存在 = {Directory.Exists(WorkingModelFolder)}");
            logWriter?.Invoke($"[Workspace] 旧新模型目录最后修改时间 = {GetDirectoryLastWriteTimeText(WorkingModelFolder)}");

            try
            {
                if (Directory.Exists(WorkingModelFolder))
                {
                    logWriter?.Invoke($"[Workspace] 正在删除旧新模型目录：{WorkingModelFolder}");
                    DeleteWorkingModelFolderIfExists();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "无法重置新模型目录，文件可能正在被 SolidWorks 占用，请关闭模型后重试。",
                    ex);
            }

            if (Directory.Exists(WorkingModelFolder))
            {
                throw new InvalidOperationException($"删除旧新模型目录后目录仍然存在：{WorkingModelFolder}");
            }

            logWriter?.Invoke("[Workspace] 已删除旧新模型目录");
            logWriter?.Invoke("[Workspace] 开始复制初始模型");
            CopyDirectory(InitialModelFolder, WorkingModelFolder);
            var copyCompletedAt = DateTime.Now;

            var workingAssemblyPath = GetWorkingAssemblyPath();
            if (!File.Exists(workingAssemblyPath))
            {
                throw new FileNotFoundException($"新模型主装配体不存在：{workingAssemblyPath}", workingAssemblyPath);
            }

            var fileCount = Directory.GetFiles(WorkingModelFolder, "*", SearchOption.AllDirectories).Length;
            var folderCount = Directory.GetDirectories(WorkingModelFolder, "*", SearchOption.AllDirectories).Length;
            if (fileCount <= 0)
            {
                throw new InvalidOperationException($"复制完成后新模型目录中文件数量为 0：{WorkingModelFolder}");
            }

            var workingDirectoryInfo = new DirectoryInfo(WorkingModelFolder);
            if (!workingDirectoryInfo.Exists)
            {
                throw new DirectoryNotFoundException($"复制完成后新模型目录不存在：{WorkingModelFolder}");
            }

            logWriter?.Invoke($"[Workspace] 复制完成，文件数量 = {fileCount}，文件夹数量 = {folderCount}");
            logWriter?.Invoke($"[Workspace] 主装配体路径 = {workingAssemblyPath}");
            logWriter?.Invoke($"[Workspace] 主装配体最后修改时间 = {File.GetLastWriteTime(workingAssemblyPath):yyyy-MM-dd HH:mm:ss.fff}");
            logWriter?.Invoke($"[Workspace] 复制完成时间 = {copyCompletedAt:yyyy-MM-dd HH:mm:ss.fff}");
            logWriter?.Invoke($"[Workspace] 新模型目录创建时间 = {workingDirectoryInfo.CreationTime:yyyy-MM-dd HH:mm:ss.fff}");
            logWriter?.Invoke($"[Workspace] 新模型目录最后修改时间 = {workingDirectoryInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss.fff}");

            var initialAssemblyPath = NormalizePath(Path.Combine(InitialModelFolder, AssemblyFileName));
            if (File.Exists(initialAssemblyPath))
            {
                var initialAssemblyHash = ComputeFileSha256(initialAssemblyPath);
                var workingAssemblyHash = ComputeFileSha256(workingAssemblyPath);
                logWriter?.Invoke($"[Workspace] 初始模型主装配体 hash = {initialAssemblyHash}");
                logWriter?.Invoke($"[Workspace] 新模型主装配体 hash = {workingAssemblyHash}");

                if (!string.Equals(initialAssemblyHash, workingAssemblyHash, StringComparison.OrdinalIgnoreCase))
                {
                    logWriter?.Invoke("[Workspace] Warning: 主装配体 hash 不一致，请检查复制结果");
                }
            }

            MarkWorkspaceInitialized(WorkingModelFolder, workingAssemblyPath, logWriter);
            logWriter?.Invoke("[Workspace] 本次工作区重置完成");
        });
    }

    public Task StartNewSessionAsync()
    {
        ClearWorkingModelSessionCache();
        return Task.CompletedTask;
    }

    public Task StartNewSessionAsync(Action<string>? logWriter, bool fromClearConversation)
    {
        ClearWorkingModelSessionCache();

        if (fromClearConversation)
        {
            logWriter?.Invoke("[Session] 清空对话，已清空工作模型缓存");
        }

        logWriter?.Invoke("[Session] 新对话开始，已清空工作模型缓存");
        return Task.CompletedTask;
    }

    public void ResetSessionCacheOnStartup(Action<string>? logWriter = null)
    {
        ClearWorkingModelSessionCache();
        logWriter?.Invoke("[Session] 软件启动，已清空工作模型缓存");
    }

    public void MarkWorkspaceInitialized(string workingModelPath, string currentAssemblyPath, Action<string>? logWriter = null)
    {
        IsWorkspaceInitialized = true;
        WorkingModelPath = NormalizePathSafe(workingModelPath);
        CurrentAssemblyPath = NormalizePathSafe(currentAssemblyPath);

        logWriter?.Invoke($"[Session] WorkspaceLoaded={IsWorkspaceInitialized}");
        logWriter?.Invoke($"[Session] WorkingModelPath={WorkingModelPath}");
        logWriter?.Invoke($"[Session] CurrentAssemblyPath={CurrentAssemblyPath}");
    }

    public void MarkWorkingModelOpened(string requestedAssemblyPath, string actualDocumentPath, Action<string>? logWriter = null)
    {
        var normalizedRequestedPath = NormalizePathSafe(requestedAssemblyPath);
        var normalizedActualPath = NormalizePathSafe(actualDocumentPath);
        var effectiveDocumentPath = string.IsNullOrWhiteSpace(normalizedActualPath)
            ? normalizedRequestedPath
            : normalizedActualPath;

        if (!IsWorkspaceInitialized)
        {
            MarkWorkspaceInitialized(WorkingModelFolder, normalizedRequestedPath, logWriter);
        }
        else if (string.IsNullOrWhiteSpace(CurrentAssemblyPath))
        {
            CurrentAssemblyPath = normalizedRequestedPath;
        }

        LastOpenedModelPath = normalizedRequestedPath;
        LastOpenedDocumentPath = effectiveDocumentPath;
        HasOpenedWorkingModel = !string.IsNullOrWhiteSpace(LastOpenedDocumentPath);

        var activeDocumentName = string.IsNullOrWhiteSpace(LastOpenedDocumentPath)
            ? string.Empty
            : Path.GetFileName(LastOpenedDocumentPath);

        logWriter?.Invoke($"[Session] IsModelOpened={HasOpenedWorkingModel}");
        logWriter?.Invoke($"[Session] LastOpenedModelPath={LastOpenedModelPath}");
        logWriter?.Invoke($"[Session] LastOpenedDocumentPath={LastOpenedDocumentPath}");
        logWriter?.Invoke($"[Session] ActiveDocumentName={(string.IsNullOrWhiteSpace(activeDocumentName) ? "n/a" : activeDocumentName)}");
    }

    public string ToWorkingPath(string initialPath)
    {
        if (string.IsNullOrWhiteSpace(initialPath))
        {
            return initialPath;
        }

        var normalizedInitialPath = NormalizePath(initialPath);
        if (!IsPathInsideRoot(normalizedInitialPath, InitialModelFolder))
        {
            return initialPath;
        }

        var relativePath = Path.GetRelativePath(InitialModelFolder, normalizedInitialPath);
        return NormalizePath(Path.Combine(WorkingModelFolder, relativePath));
    }

    public string GetWorkingAssemblyPath()
    {
        ValidateConfiguration();
        return NormalizePath(Path.Combine(WorkingModelFolder, AssemblyFileName));
    }

    private void ReloadWorkspaceConfigurationFromAppSettings(Action<string>? logWriter)
    {
        var latestOptions = AppConfigLoader.LoadSettings().SolidWorksWorkspace;
        ApplyWorkspaceOptions(latestOptions);
        logWriter?.Invoke($"[Workspace] 本次从配置读取初始模型目录 = {InitialModelFolder}");
        logWriter?.Invoke($"[Workspace] 本次从配置读取新模型目录 = {WorkingModelFolder}");
    }

    private void ApplyWorkspaceOptions(SolidWorksWorkspaceOptions options)
    {
        ModelRootPath = NormalizePath(options.ModelRootPath);
        InitialModelFolder = NormalizePath(options.InitialModelFolder);
        WorkingModelFolder = NormalizePath(options.WorkingModelFolder);
        AssemblyFileName = NormalizeRelativePath(options.AssemblyFileName);
    }

    public bool HasValidModelRootPath()
    {
        return !string.IsNullOrWhiteSpace(ModelRootPath) && Directory.Exists(ModelRootPath);
    }

    private void ClearWorkingModelSessionCache()
    {
        WorkingModelPath = string.Empty;
        CurrentAssemblyPath = string.Empty;
        LastOpenedModelPath = string.Empty;
        LastOpenedDocumentPath = string.Empty;
        HasOpenedWorkingModel = false;
        IsWorkspaceInitialized = false;
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(InitialModelFolder))
        {
            throw new InvalidOperationException("SolidWorksWorkspace.InitialModelFolder 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(WorkingModelFolder))
        {
            throw new InvalidOperationException("SolidWorksWorkspace.WorkingModelFolder 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(AssemblyFileName))
        {
            throw new InvalidOperationException("SolidWorksWorkspace.AssemblyFileName 不能为空。");
        }

        if (!Directory.Exists(InitialModelFolder))
        {
            throw new DirectoryNotFoundException($"初始模型目录不存在：{InitialModelFolder}");
        }
    }

    private void EnsureDirectoriesAreDifferent()
    {
        if (string.Equals(
                NormalizePath(InitialModelFolder),
                NormalizePath(WorkingModelFolder),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("初始模型目录和新模型目录不能相同。");
        }
    }

    private void DeleteWorkingModelFolderIfExists()
    {
        if (Directory.Exists(WorkingModelFolder))
        {
            Directory.Delete(WorkingModelFolder, recursive: true);
        }
    }

    private static void CopyDirectory(string sourceFolder, string targetFolder)
    {
        var sourceDirectory = new DirectoryInfo(sourceFolder);
        if (!sourceDirectory.Exists)
        {
            throw new DirectoryNotFoundException($"初始模型目录不存在：{sourceFolder}");
        }

        Directory.CreateDirectory(targetFolder);

        foreach (var directory in sourceDirectory.GetDirectories("*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceFolder, directory.FullName);
            Directory.CreateDirectory(Path.Combine(targetFolder, relativePath));
        }

        foreach (var file in sourceDirectory.GetFiles("*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceFolder, file.FullName);
            var targetFilePath = Path.Combine(targetFolder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);
            file.CopyTo(targetFilePath, overwrite: true);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.GetFullPath(path.Trim());
    }

    private static string NormalizePathSafe(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? string.Empty : NormalizePath(path);
    }

    private static string NormalizeRelativePath(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool IsPathInsideRoot(string path, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        var normalizedRoot = NormalizeRootPath(rootPath);
        var normalizedPath = NormalizePath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRootPath(string path)
    {
        var normalizedPath = NormalizePath(path);
        return normalizedPath.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedPath
            : normalizedPath + Path.DirectorySeparatorChar;
    }

    private static string GetDirectoryLastWriteTimeText(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return "N/A";
        }

        return Directory.GetLastWriteTime(directoryPath).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    private static string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }
}
