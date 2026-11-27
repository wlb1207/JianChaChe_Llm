using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;

namespace LlmWpfPrototype.Services;

public sealed partial class SolidWorksService
{
    private static readonly TimeSpan InteractiveLaunchAttachTimeout = TimeSpan.FromSeconds(20);
    private int _lastAttachHResult;
    private SolidWorksSessionDiagnostics? _lastSessionDiagnostics;

    public SolidWorksSessionDiagnostics GetSessionDiagnostics()
        => ReadCadStatus(ReadSessionDiagnosticsCore, () =>
            Volatile.Read(ref _lastSessionDiagnostics) ?? BuildFallbackDiagnostics());

    private SolidWorksSessionDiagnostics BuildFallbackDiagnostics()
    {
        var cachedPath = Volatile.Read(ref _lastActiveCadPath);
        var hasPath = !string.IsNullOrWhiteSpace(cachedPath);
        var processCount = GetSolidWorksProcessCountSafe();
        if (hasPath)
        {
            var name = Path.GetFileName(cachedPath);
            var isWorking = IsWorkingAssemblyPath(cachedPath, expectedPath: null);
            return new SolidWorksSessionDiagnostics(
                isWorking ? SolidWorksSessionState.ConnectedWorkingAssembly : SolidWorksSessionState.ConnectedDifferentDocument,
                HasRunningProcess: true,
                RunningProcessCount: Math.Max(1, processCount),
                IsComAttached: true,
                ActiveDocumentPath: cachedPath,
                ActiveDocumentName: name,
                ActiveDocumentIsAssembly: cachedPath.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase),
                HasOpenWorkingAssemblyInSession: isWorking,
                OpenWorkingAssemblyPath: isWorking ? cachedPath : string.Empty,
                ExpectedWorkingFolder: _workingModelFolder,
                LastAttachHResult: Volatile.Read(ref _lastAttachHResult),
                DetailMessage: isWorking
                    ? $"已连接当前项目工作总装配：{name}。"
                    : $"SolidWorks 当前文档为 {name}，不属于当前项目工作区。");
        }

        if (processCount > 0)
        {
            var hr = Volatile.Read(ref _lastAttachHResult);
            return new SolidWorksSessionDiagnostics(
                SolidWorksSessionState.ProcessRunningComUnavailable,
                HasRunningProcess: true,
                RunningProcessCount: processCount,
                IsComAttached: false,
                ActiveDocumentPath: string.Empty,
                ActiveDocumentName: string.Empty,
                ActiveDocumentIsAssembly: false,
                HasOpenWorkingAssemblyInSession: false,
                OpenWorkingAssemblyPath: string.Empty,
                ExpectedWorkingFolder: _workingModelFolder,
                LastAttachHResult: hr,
                DetailMessage: BuildComUnavailableDiagnosticMessage(processCount, hr));
        }

        return new SolidWorksSessionDiagnostics(
            SolidWorksSessionState.NotRunning,
            HasRunningProcess: false,
            RunningProcessCount: 0,
            IsComAttached: false,
            ActiveDocumentPath: string.Empty,
            ActiveDocumentName: string.Empty,
            ActiveDocumentIsAssembly: false,
            HasOpenWorkingAssemblyInSession: false,
            OpenWorkingAssemblyPath: string.Empty,
            ExpectedWorkingFolder: _workingModelFolder,
            LastAttachHResult: 0,
            DetailMessage: "当前未检测到运行中的 SolidWorks 进程。");
    }

    private SolidWorksSessionDiagnostics ReadSessionDiagnosticsCore()
    {
        var processCount = GetSolidWorksProcessCountSafe();
        var attached = TryGetRunningApplication(out var application) && application is not null;
        if (!attached || application is null)
        {
            Volatile.Write(ref _lastActiveCadPath, string.Empty);
            var hr = Volatile.Read(ref _lastAttachHResult);
            var result = processCount > 0
                ? new SolidWorksSessionDiagnostics(
                    SolidWorksSessionState.ProcessRunningComUnavailable,
                    HasRunningProcess: true,
                    RunningProcessCount: processCount,
                    IsComAttached: false,
                    ActiveDocumentPath: string.Empty,
                    ActiveDocumentName: string.Empty,
                    ActiveDocumentIsAssembly: false,
                    HasOpenWorkingAssemblyInSession: false,
                    OpenWorkingAssemblyPath: string.Empty,
                    ExpectedWorkingFolder: _workingModelFolder,
                    LastAttachHResult: hr,
                    DetailMessage: BuildComUnavailableDiagnosticMessage(processCount, hr))
                : new SolidWorksSessionDiagnostics(
                    SolidWorksSessionState.NotRunning,
                    HasRunningProcess: false,
                    RunningProcessCount: 0,
                    IsComAttached: false,
                    ActiveDocumentPath: string.Empty,
                    ActiveDocumentName: string.Empty,
                    ActiveDocumentIsAssembly: false,
                    HasOpenWorkingAssemblyInSession: false,
                    OpenWorkingAssemblyPath: string.Empty,
                    ExpectedWorkingFolder: _workingModelFolder,
                    LastAttachHResult: 0,
                    DetailMessage: "当前未检测到运行中的 SolidWorks 进程。");

            Volatile.Write(ref _lastSessionDiagnostics, result);
            return result;
        }

        try
        {
            var activeDoc = application.ActiveDoc as IModelDoc2;
            var activePath = NormalizePathSafe(activeDoc?.GetPathName());
            var activeName = !string.IsNullOrWhiteSpace(activePath)
                ? Path.GetFileName(activePath)
                : (activeDoc?.GetTitle() ?? string.Empty);
            var activeIsAssembly = activeDoc is IAssemblyDoc;
            var activeIsWorkingAssembly = activeIsAssembly && IsWorkingAssemblyPath(activePath, expectedPath: null);

            var openWorkingAssembly = activeIsWorkingAssembly
                ? activeDoc
                : FindOpenWorkingAssemblyInSession(application, expectedPath: null);
            var openWorkingPath = openWorkingAssembly is not null
                ? NormalizePathSafe(openWorkingAssembly.GetPathName())
                : string.Empty;
            var hasOpenWorkingAssembly = !string.IsNullOrWhiteSpace(openWorkingPath);

            Volatile.Write(ref _lastActiveCadPath, !string.IsNullOrWhiteSpace(activePath) ? activePath : openWorkingPath);

            SolidWorksSessionDiagnostics diag;
            if (activeIsWorkingAssembly)
            {
                diag = new SolidWorksSessionDiagnostics(
                    SolidWorksSessionState.ConnectedWorkingAssembly,
                    HasRunningProcess: true,
                    RunningProcessCount: Math.Max(1, processCount),
                    IsComAttached: true,
                    ActiveDocumentPath: activePath,
                    ActiveDocumentName: activeName,
                    ActiveDocumentIsAssembly: true,
                    HasOpenWorkingAssemblyInSession: true,
                    OpenWorkingAssemblyPath: activePath,
                    ExpectedWorkingFolder: _workingModelFolder,
                    LastAttachHResult: 0,
                    DetailMessage: $"已连接当前项目工作总装配：{activeName}。");
            }
            else if (activeDoc is null && !hasOpenWorkingAssembly)
            {
                diag = new SolidWorksSessionDiagnostics(
                    SolidWorksSessionState.ConnectedNoActiveDocument,
                    HasRunningProcess: true,
                    RunningProcessCount: Math.Max(1, processCount),
                    IsComAttached: true,
                    ActiveDocumentPath: string.Empty,
                    ActiveDocumentName: string.Empty,
                    ActiveDocumentIsAssembly: false,
                    HasOpenWorkingAssemblyInSession: false,
                    OpenWorkingAssemblyPath: string.Empty,
                    ExpectedWorkingFolder: _workingModelFolder,
                    LastAttachHResult: 0,
                    DetailMessage: "已连接到 SolidWorks，但当前未打开任何模型文档。");
            }
            else if (hasOpenWorkingAssembly)
            {
                var openName = Path.GetFileName(openWorkingPath);
                var detail = string.IsNullOrWhiteSpace(activeName)
                    ? $"已连接到 SolidWorks，本项目工作总装配（{openName}）已在会话中打开（执行改模时会自动激活）。"
                    : $"已连接到 SolidWorks：当前激活窗口是「{activeName}」，本项目工作总装配（{openName}）已在后台打开（执行改模时会自动切回）。";
                diag = new SolidWorksSessionDiagnostics(
                    SolidWorksSessionState.ConnectedDifferentDocument,
                    HasRunningProcess: true,
                    RunningProcessCount: Math.Max(1, processCount),
                    IsComAttached: true,
                    ActiveDocumentPath: activePath,
                    ActiveDocumentName: activeName,
                    ActiveDocumentIsAssembly: activeIsAssembly,
                    HasOpenWorkingAssemblyInSession: true,
                    OpenWorkingAssemblyPath: openWorkingPath,
                    ExpectedWorkingFolder: _workingModelFolder,
                    LastAttachHResult: 0,
                    DetailMessage: detail);
            }
            else
            {
                var isInitial = !string.IsNullOrWhiteSpace(_initialModelFolder) &&
                                !string.IsNullOrWhiteSpace(activePath) &&
                                WorkspaceSafetyPolicy.Within(activePath, _initialModelFolder);
                var scopeNote = isInitial
                    ? "该文件位于只读初始模型目录（为防误改原始模板，不会直接修改）"
                    : "该文件不属于当前项目的工作副本目录（为保护原始模型与其他项目，不会直接修改）";
                diag = new SolidWorksSessionDiagnostics(
                    SolidWorksSessionState.ConnectedDifferentDocument,
                    HasRunningProcess: true,
                    RunningProcessCount: Math.Max(1, processCount),
                    IsComAttached: true,
                    ActiveDocumentPath: activePath,
                    ActiveDocumentName: activeName,
                    ActiveDocumentIsAssembly: activeIsAssembly,
                    HasOpenWorkingAssemblyInSession: false,
                    OpenWorkingAssemblyPath: string.Empty,
                    ExpectedWorkingFolder: _workingModelFolder,
                    LastAttachHResult: 0,
                    DetailMessage: $"已连接到 SolidWorks，当前打开的是「{activeName}」，但{scopeNote}。");
            }

            Volatile.Write(ref _lastSessionDiagnostics, diag);
            return diag;
        }
        catch
        {
            return BuildFallbackDiagnostics();
        }
    }

    private bool IsWorkingAssemblyPath(string? path, string? expectedPath)
    {
        var normalized = NormalizePathSafe(path);
        if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(_workingModelFolder))
        {
            return false;
        }

        if (!WorkspaceSafetyPolicy.Within(normalized, _workingModelFolder))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_initialModelFolder) &&
            WorkspaceSafetyPolicy.Within(normalized, _initialModelFolder))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedPath) &&
            !string.Equals(normalized, NormalizeFilePath(expectedPath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private IModelDoc2? FindOpenWorkingAssemblyInSession(SldWorks application, string? expectedPath)
    {
        try
        {
            var candidates = GetOpenDocuments(application)
                .Where(doc => doc is IAssemblyDoc && IsWorkingAssemblyPath(doc.GetPathName(), expectedPath))
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            // Prefer the top-level working assembly (e.g. 总装配\总装配体.SLDASM) over sub-assemblies.
            return candidates
                .OrderByDescending(doc => NormalizePathSafe(doc.GetPathName()).Contains("总装配", StringComparison.OrdinalIgnoreCase))
                .ThenBy(doc => NormalizePathSafe(doc.GetPathName()).Length)
                .First();
        }
        catch
        {
            return null;
        }
    }

    private bool TryReactivateOpenWorkingAssembly(string? expectedPath, out IModelDoc2? assemblyDocument)
    {
        assemblyDocument = null;
        if (_swApp is null)
        {
            return false;
        }

        var candidate = FindOpenWorkingAssemblyInSession(_swApp, expectedPath);
        if (candidate is null)
        {
            return false;
        }

        ActivateDocument(candidate, logWriter: null);
        var afterActivate = _swApp.ActiveDoc as IModelDoc2;
        if (afterActivate is IAssemblyDoc && IsWorkingAssemblyPath(afterActivate.GetPathName(), expectedPath))
        {
            assemblyDocument = afterActivate;
            return true;
        }

        // Even if ActivateDoc3 did not steal UI focus, return the verified in-memory working assembly doc if valid.
        assemblyDocument = candidate;
        return true;
    }

    private bool TryAttachActiveInstanceMultiPath(Action<string>? logWriter, out SldWorks? activeInstance)
    {
        activeInstance = null;
        var lastHResult = 0;

        try
        {
            // 1. Try default ProgID + installed versioned ProgIDs (e.g. CurVer / SldWorks.Application.26..34)
            foreach (var progId in EnumerateSolidWorksProgIds())
            {
                if (!TryGetProgIdClsid(progId, out var clsid))
                {
                    continue;
                }

                var hr = GetActiveObject(ref clsid, IntPtr.Zero, out var activeObject);
                if (hr == 0 && activeObject is SldWorks swApp && IsApplicationAlive(swApp))
                {
                    Volatile.Write(ref _lastAttachHResult, 0);
                    activeInstance = swApp;
                    if (!string.Equals(progId, ProgId, StringComparison.OrdinalIgnoreCase))
                    {
                        WriteLog(logWriter, $"已通过版本化 ProgID 连接现有 SolidWorks 实例：{progId}");
                    }

                    return true;
                }

                lastHResult = hr;
            }

            // 2. Fallback: enumerate Windows ROT directly for SolidWorks_PID_* or SldWorks monikers
            if (TryAttachFromRunningObjectTable(logWriter, out var rotSwApp) && rotSwApp is not null)
            {
                Volatile.Write(ref _lastAttachHResult, 0);
                activeInstance = rotSwApp;
                return true;
            }

            Volatile.Write(ref _lastAttachHResult, lastHResult);
            var runningCount = GetSolidWorksProcessCountSafe();
            if (runningCount > 0)
            {
                WriteLog(
                    logWriter,
                    $"未连接到现有 SolidWorks 实例，HRESULT=0x{lastHResult:X8}（本机检测到 {runningCount} 个 SLDWORKS 进程；可能为旧 COM 会话未注册 ROT 或管理员权限不一致）");
            }
            else
            {
                WriteLog(logWriter, $"未连接到现有 SolidWorks 实例，HRESULT=0x{lastHResult:X8}");
            }

            return false;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastAttachHResult, ex.HResult);
            WriteException(logWriter, "连接现有 SolidWorks 实例失败。", ex);
            activeInstance = null;
            return false;
        }
    }

    private static IEnumerable<string> EnumerateSolidWorksProgIds()
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (yielded.Add(ProgId))
        {
            yield return ProgId;
        }

        var curVer = TryReadRegistryCurVerProgId();
        if (!string.IsNullOrWhiteSpace(curVer) && yielded.Add(curVer))
        {
            yield return curVer;
        }

        // Probe SolidWorks 2018 (.26) through 2026 (.34) if registered on this machine.
        for (var version = 34; version >= 26; version--)
        {
            var candidate = $"{ProgId}.{version}";
            if (yielded.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static string? TryReadRegistryCurVerProgId()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"{ProgId}\CurVer");
            var val = key?.GetValue(null) as string;
            return string.IsNullOrWhiteSpace(val) ? null : val.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryAttachFromRunningObjectTable(Action<string>? logWriter, out SldWorks? activeInstance)
    {
        activeInstance = null;
        IRunningObjectTable? rot = null;
        IBindCtx? bindCtx = null;
        IEnumMoniker? enumMoniker = null;
        try
        {
            if (GetRunningObjectTable(0, out rot) != 0 || rot is null)
            {
                return false;
            }

            if (CreateBindCtx(0, out bindCtx) != 0 || bindCtx is null)
            {
                return false;
            }

            rot.EnumRunning(out enumMoniker);
            if (enumMoniker is null)
            {
                return false;
            }

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                if (moniker is null)
                {
                    continue;
                }

                try
                {
                    moniker.GetDisplayName(bindCtx, null, out var displayName);
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    if (!displayName.Contains("SolidWorks_PID_", StringComparison.OrdinalIgnoreCase) &&
                        !displayName.Contains("SldWorks", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (rot.GetObject(moniker, out var rawObj) == 0 &&
                        rawObj is SldWorks swApp &&
                        IsApplicationAlive(swApp))
                    {
                        WriteLog(logWriter, $"已通过 Windows ROT 枚举连接现有 SolidWorks 实例：{displayName}");
                        activeInstance = swApp;
                        return true;
                    }
                }
                catch
                {
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (enumMoniker is not null) Marshal.ReleaseComObject(enumMoniker);
            if (bindCtx is not null) Marshal.ReleaseComObject(bindCtx);
            if (rot is not null) Marshal.ReleaseComObject(rot);
        }

        return false;
    }

    private SldWorks? TryLaunchInteractiveSolidWorksAndAttach(Action<string>? logWriter, CancellationToken cancellationToken = default)
    {
        // Only prefer interactive launch when no SLDWORKS process is currently running;
        // interactive desktop launch registers SolidWorks into Windows ROT so subsequent app restarts can attach directly!
        if (GetSolidWorksProcessCountSafe() > 0)
        {
            return null;
        }

        var exePath = TryResolveSolidWorksExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteLog(logWriter, $"正在以交互进程方式启动 SolidWorks（以便支持跨重启 COM 附着）：{exePath}");
            logWriter?.Invoke("[Progress] 正在启动 SolidWorks 并等待系统 COM 就绪...");
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = true
            };
            Process.Start(startInfo);

            var deadline = DateTime.UtcNow + InteractiveLaunchAttachTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (cancellationToken.WaitHandle.WaitOne(500))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (TryAttachActiveInstanceMultiPath(logWriter: null, out var attached) && attached is not null)
                {
                    WriteLog(logWriter, "交互启动 SolidWorks 成功，并已完成 ROT COM 附着。");
                    return attached;
                }
            }

            WriteLog(logWriter, "交互启动 SolidWorks 后等待 ROT 注册超时，将回退至 COM CreateObject。");
            return null;
        }
        catch (OperationCanceledException)
        {
            WriteLog(logWriter, "已取消等待 SolidWorks 启动。");
            throw;
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "交互方式启动 SolidWorks 失败，将回退至 COM CreateObject。", ex);
            return null;
        }
    }

    private static string? TryResolveSolidWorksExecutablePath()
    {
        try
        {
            foreach (var progId in EnumerateSolidWorksProgIds())
            {
                if (!TryGetProgIdClsid(progId, out var clsid))
                {
                    continue;
                }

                using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid:B}\LocalServer32");
                var rawCommand = key?.GetValue(null) as string;
                var parsed = ExtractExecutablePathFromCommand(rawCommand);
                if (!string.IsNullOrWhiteSpace(parsed) && File.Exists(parsed))
                {
                    return parsed;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? ExtractExecutablePathFromCommand(string? rawCommand)
    {
        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            return null;
        }

        var trimmed = rawCommand.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                return trimmed.Substring(1, closingQuote - 1).Trim();
            }
        }

        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
        {
            return trimmed[..(exeIndex + 4)].Trim();
        }

        return trimmed;
    }

    private static int GetSolidWorksProcessCountSafe()
    {
        try
        {
            return Process.GetProcessesByName("SLDWORKS").Length;
        }
        catch
        {
            return 0;
        }
    }

    private bool TryCloseConflictingExternalDocumentsForWorkingAssembly(
        SldWorks application,
        string targetAssemblyPath,
        Action<string>? logWriter,
        CancellationToken cancellationToken,
        out string conflictFailureMessage)
    {
        conflictFailureMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(_workingModelFolder))
        {
            return true;
        }

        var openDocs = GetOpenDocuments(application);
        if (openDocs.Count == 0)
        {
            return true;
        }

        var workingFileNames = CollectWorkingCadFileNames(_workingModelFolder, targetAssemblyPath);
        var workspaceContainerDir = TryGetWorkspaceContainerDirectory(_workingModelFolder);
        var conflictingDocs = new List<(IModelDoc2 Doc, string Path, string Title, bool IsDirty)>();

        foreach (var doc in openDocs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var docPath = NormalizePathSafe(doc.GetPathName());
            if (string.IsNullOrWhiteSpace(docPath) || WorkspaceSafetyPolicy.Within(docPath, _workingModelFolder))
            {
                continue;
            }

            var fileName = Path.GetFileName(docPath);
            var sameCadFileName = !string.IsNullOrWhiteSpace(fileName) && workingFileNames.Contains(fileName);
            var insideInitial = !string.IsNullOrWhiteSpace(_initialModelFolder) &&
                                WorkspaceSafetyPolicy.Within(docPath, _initialModelFolder);
            var insideSiblingWorkspace = !string.IsNullOrWhiteSpace(workspaceContainerDir) &&
                                         WorkspaceSafetyPolicy.Within(docPath, workspaceContainerDir);

            if (!sameCadFileName && !insideInitial && !insideSiblingWorkspace)
            {
                continue;
            }

            var title = SafeGetDocumentTitle(doc, fileName);
            var isDirty = SafeGetDocumentSaveFlag(doc);
            conflictingDocs.Add((doc, docPath, title, isDirty));
        }

        if (conflictingDocs.Count == 0)
        {
            return true;
        }

        var dirtyDocs = conflictingDocs
            .Where(item => item.IsDirty)
            .Select(item => Path.GetFileName(item.Path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        if (dirtyDocs.Count > 0)
        {
            conflictFailureMessage =
                $"SolidWorks 当前打开了其他目录的同名或关联模型（{string.Join("、", dirtyDocs)}），且存在未保存修改。"
                + "为避免丢失您的修改或引起项目间装配引用串扰，请先在 SolidWorks 中保存并关闭旧模型后，再发送“打开当前模型”。";
            return false;
        }

        WriteLog(
            logWriter,
            $"检测到 SolidWorks 中打开了 {conflictingDocs.Count} 个其他目录的同名/旧项目模型（均无未保存修改），正在自动安全关闭以避免装配引用冲突。");
        logWriter?.Invoke("[Progress] 正在安全关闭 SolidWorks 中打开的旧项目同名模型...");

        foreach (var item in conflictingDocs
                     .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(entry => GetDocumentClosePriority(entry.Doc)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                application.CloseDoc(item.Title);
            }

            WriteLog(logWriter, $"已自动关闭冲突旧模型：{item.Path}");
        }

        var remainingConflictingAssemblies = GetOpenDocuments(application)
            .Select(doc => NormalizePathSafe(doc.GetPathName()))
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                           !WorkspaceSafetyPolicy.Within(path, _workingModelFolder) &&
                           workingFileNames.Contains(Path.GetFileName(path)))
            .ToList();
        if (remainingConflictingAssemblies.Count > 0)
        {
            var sampleName = Path.GetFileName(remainingConflictingAssemblies[0]);
            conflictFailureMessage =
                $"尝试自动关闭 SolidWorks 中的旧项目同名模型（{sampleName} 等共 {remainingConflictingAssemblies.Count} 个）未完全释放。"
                + "请先手动关闭 SolidWorks 中已打开的旧模型窗口，再发送“打开当前模型”。";
            return false;
        }

        WriteLog(logWriter, "已清理 SolidWorks 中的旧项目同名模型，继续校验并打开当前项目工作副本。");
        return true;
    }

    private static HashSet<string> CollectWorkingCadFileNames(string workingFolder, string targetAssemblyPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetName = Path.GetFileName(targetAssemblyPath);
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            names.Add(targetName);
        }

        try
        {
            if (Directory.Exists(workingFolder))
            {
                foreach (var file in Directory.EnumerateFiles(workingFolder, "*.sld*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch
        {
        }

        return names;
    }

    private static string? TryGetWorkspaceContainerDirectory(string workingFolder)
    {
        try
        {
            var parent = Directory.GetParent(workingFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null)
            {
                return null;
            }

            return string.Equals(parent.Name, "项目工作区", StringComparison.OrdinalIgnoreCase)
                ? parent.FullName
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeGetDocumentTitle(IModelDoc2 doc, string fallback)
    {
        try
        {
            var title = doc.GetTitle();
            return string.IsNullOrWhiteSpace(title) ? fallback : title;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool SafeGetDocumentSaveFlag(IModelDoc2 doc)
    {
        try
        {
            return doc.GetSaveFlag();
        }
        catch
        {
            return false;
        }
    }

    private static string BuildFriendlyOpenAssemblyFailureMessage(Exception ex)
    {
        var raw = ex.Message?.Trim() ?? string.Empty;
        if (raw.Contains("装配引用既不在工作副本也不在初始模型目录", StringComparison.Ordinal))
        {
            var fileName = Path.GetFileName(raw.Split('：').LastOrDefault()?.Trim() ?? string.Empty);
            var targetHint = string.IsNullOrWhiteSpace(fileName) ? "子装配/零件" : $"“{fileName}”";
            return $"未能打开当前项目工作副本：工作装配体引用的 {targetHint} 指向了当前项目之外的目录（常见原因是 SolidWorks 内存中仍占用了其他项目的同名装配体）。请关闭 SolidWorks 中已打开的旧模型后，再发送“打开当前模型”。";
        }

        if (raw.Contains("工作装配已打开且仍引用初始模型", StringComparison.Ordinal))
        {
            return "当前工作装配已在 SolidWorks 中打开且仍指向旧引用，请先在 SolidWorks 中关闭该装配体后重试。";
        }

        if (raw.Contains("未能更新工作装配引用", StringComparison.Ordinal))
        {
            return "SolidWorks 未能更新当前项目工作副本的组件引用，请确认工作区文件未被只读锁定或被其他进程占用后重试。";
        }

        return string.IsNullOrWhiteSpace(raw)
            ? "打开当前项目工作模型时发生异常，请查看高级调试日志。"
            : $"打开当前项目工作模型失败：{raw}";
    }

    private static string BuildComUnavailableDiagnosticMessage(int processCount, int hResult)
    {
        var hrText = hResult != 0 ? $"0x{hResult:X8}" : "0x800401E3";
        return $"检测到本机已有 {processCount} 个 SolidWorks 进程正在运行，但当前程序无法附着其 COM 会话（HRESULT={hrText}，常见原因：软件重启前旧会话未注册 ROT，或 SolidWorks 与本程序的管理员权限不一致）。";
    }

    private static string NormalizePathSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return NormalizeFilePath(path);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static bool TryGetProgIdClsid(string progId, out Guid clsid)
    {
        var result = CLSIDFromProgIDEx(progId, out clsid);
        if (result == 0)
        {
            return true;
        }

        result = CLSIDFromProgID(progId, out clsid);
        return result == 0;
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);
}
