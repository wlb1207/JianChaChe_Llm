using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed partial class SolidWorksService
{
    // Desktop startup uses the normal session.
    private readonly bool _requireIsolatedSession = false;
    private readonly int _expectedSolidWorksProcessId = 0;
    private bool _isolatedAttachAttempted;
    public string LastDimensionOperationMessage { get; private set; } = string.Empty;
    private AssemblyComponentIndex? _assemblyComponentIndex;
    private sealed record ResolvedComponentEntry(IModelDoc2 Document, string Configuration, string Instances);
    private sealed record AssemblyComponentIndex(
        string AssemblyPath,
        string Configuration,
        Dictionary<string, List<IComponent2>> ByPath,
        Dictionary<string, ResolvedComponentEntry> ResolvedByPath,
        int Count);

    // No creation, Visible/UserControl setters, UI automation or fallback to another instance.
    private bool TryGetIsolatedApplication(Action<string>? log, out SldWorks? application)
    {
        lock (_syncRoot)
        {
            application = null;
            if (_expectedSolidWorksProcessId <= 0)
            {
                WriteLog(log, "隔离测试未设置 ExpectedSolidWorksProcessId，禁止 CAD 连接。");
                return false;
            }
            if (_swApp is null)
            {
                if (_isolatedAttachAttempted) return false;
                _isolatedAttachAttempted = true;
                if (!TryGetActiveInstance(log, out var candidate) || candidate is null) return false;
                // ROT lookup alone is never evidence that this is the approved instance.
                var pid = candidate.GetProcessID();
                WriteLog(log, $"Isolated session candidate. ActualPID={pid}, ExpectedPID={_expectedSolidWorksProcessId}");
                if (pid != _expectedSolidWorksProcessId) return false;
                _swApp = candidate;
            }
            RequireIsolatedSessionBoundary(log);
            application = _swApp;
            return true;
        }
    }

    private void RequireIsolatedSessionBoundary(Action<string>? log)
    {
        if (!_requireIsolatedSession || _swApp is null || _expectedSolidWorksProcessId <= 0 ||
            _swApp.GetProcessID() != _expectedSolidWorksProcessId)
            throw new InvalidOperationException("隔离实例身份无法确认，禁止操作。");
        foreach (var doc in GetOpenDocuments(_swApp))
        {
            var path = doc.GetPathName();
            if (string.IsNullOrWhiteSpace(path) || !WorkspaceSafetyPolicy.Within(path, _workingModelFolder) ||
                WorkspaceSafetyPolicy.Within(path, _initialModelFolder))
                throw new InvalidOperationException($"隔离实例含范围外或未命名文档，已停止。PID={_expectedSolidWorksProcessId}; Path={path}");
            WorkspaceSafetyPolicy.RejectLinks(path);
            WriteLog(log, $"Isolated document. PID={_expectedSolidWorksProcessId}; Path={path}; Configuration={TryGetActiveConfigurationName(doc)}");
            if (doc is IAssemblyDoc assembly)
            {
                var components = assembly.GetComponents(false) as object[]
                    ?? throw new InvalidOperationException($"装配组件清单未知，禁止继续：{path}");
                foreach (var value in components)
                {
                    if (value is not IComponent2 component) throw new InvalidOperationException("组件身份未知，禁止继续。");
                    var reference = component.GetPathName();
                    var loaded = component.GetModelDoc2() as IModelDoc2;
                    var configuration = component.ReferencedConfiguration;
                    WriteLog(log, $"Loaded reference. Component={component.Name2}; Path={reference}; ReferencedConfiguration={configuration}; Loaded={loaded is not null}");
                    if (loaded is null || string.IsNullOrWhiteSpace(configuration) ||
                        !string.Equals(reference, loaded.GetPathName(), StringComparison.OrdinalIgnoreCase) ||
                        !WorkspaceSafetyPolicy.Within(reference, _workingModelFolder) || WorkspaceSafetyPolicy.Within(reference, _initialModelFolder) ||
                        !string.Equals(configuration, TryGetActiveConfigurationName(loaded), StringComparison.Ordinal))
                        throw new InvalidOperationException($"组件未解析、路径越界或引用配置与活动配置不一致；未知状态阻断。Component={component.Name2}; Path={reference}");
                    WorkspaceSafetyPolicy.RejectLinks(reference);
                }
            }
        }
    }

    // Reads the saved reference table with Searchflag=false BEFORE OpenDoc6.
    // Does not repair, search for a substitute, or redirect a reference.
    private void RequireSavedReferenceBoundary(string path, Action<string>? log)
    {
        if (_swApp is null) throw new InvalidOperationException("No held CAD instance");
        WorkspaceSafetyPolicy.RequireWritableModel(path, _initialModelFolder, _workingModelFolder);
        var raw = _swApp.GetDocumentDependencies2(path, true, false, false);
        if (raw is not null && raw is not object[])
            throw new InvalidOperationException("CAD 返回的已保存引用表类型未知，禁止打开。");
        var references = raw as object[] ?? [];
        if (string.Equals(Path.GetExtension(path), ".SLDASM", StringComparison.OrdinalIgnoreCase) && references.Length == 0)
            throw new InvalidOperationException($"未能取得测试装配的已保存引用，隔离未确认：{path}");
        CadReferenceBoundary.ValidateSavedPairs(path, _workingModelFolder, _initialModelFolder, references,
            (reference) => File.Exists(reference), log);
    }

    private IModelDoc2? OpenIsolatedDocument(string path, int type, Action<string>? log)
    {
        RequireIsolatedSessionBoundary(log);
        RequireSavedReferenceBoundary(path, log);
        var existing = GetOpenedDocumentByPath(path);
        if (existing is not null) return existing;
        var errors = 0; var warnings = 0;
        var doc = _swApp!.OpenDoc6(path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
            "", ref errors, ref warnings) as IModelDoc2;
        WriteLog(log, $"Isolated OpenDoc6. Requested={path}; Actual={doc?.GetPathName()}; Errors={errors}; Warnings={warnings}; NoConfigurationSwitch=True");
        if (doc is null || errors != 0 || !string.Equals(Path.GetFullPath(doc.GetPathName()),
            Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"隔离文件打开失败或路径不一致。Path={path}; Errors={errors}; Warnings={warnings}");
        RequireIsolatedSessionBoundary(log);
        return doc;
    }

    private IModelDoc2 RequireOpenTransactionDocument(string path)
    {
        var doc = GetOpenedDocumentByPath(path) ?? throw new InvalidOperationException($"事务文档不在已持有实例内：{path}");
        if (!string.Equals(Path.GetFullPath(doc.GetPathName()), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("事务文档实际路径不一致。");
        return doc;
    }

    private static string TryGetActiveConfigurationName(IModelDoc2 document)
    {
        const string unknown = "<unknown>";
        try
        {
            var name = document.ConfigurationManager?.ActiveConfiguration?.Name;
            return string.IsNullOrWhiteSpace(name) ? unknown : name;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return unknown;
        }
    }

    private bool VerifyTransactionDimension(PreparedDimensionUpdate item, double expected, string phase, Action<string>? log)
    {
        try
        {
            var document = RequireOpenTransactionDocument(item.Request.PartFilePath);
            if (item.ConfigurationOption == (int)swInConfigurationOpts_e.swThisConfiguration &&
                TryGetActiveConfigurationName(document).StartsWith('<'))
                return false;

            var dimension = RequireExactIndependentDimension(document, item.ResolvedDimensionName);
            object? raw = dimension.GetSystemValue3(item.ConfigurationOption, item.ConfigurationNames);
            var matched = DimensionReadbackVerifier.Matches(raw, expected, out var actual);
            WriteLog(log, $"{phase}. Part={item.Request.PartFilePath}; Dimension={item.ResolvedDimensionName}; Expected={expected:R}; Actual={actual:R}; Verified={matched}");
            return matched;
        }
        catch (Exception ex) when (ex is not AccessViolationException)
        {
            WriteLog(log, $"{phase}. Part={item.Request.PartFilePath}; Dimension={item.ResolvedDimensionName}; Verified=False; {ex.Message}");
            return false;
        }
    }

    private static IDimension RequireExactIndependentDimension(IModelDoc2 doc, string fullName)
    {
        var dimension = FindExactDimension(doc, fullName);
        if (dimension is null)
            throw new InvalidOperationException($"无法精确核实尺寸身份：{doc.GetPathName()} | {fullName}");
        if (dimension.ReadOnly || dimension.DrivenState != (int)swDimensionDrivenState_e.swDimensionDriving)
            throw new InvalidOperationException($"尺寸非独立可写驱动，禁止强改：{fullName}");
        var equations = doc.GetEquationMgr();
        if (equations is not null)
        {
            var local = fullName[..fullName.LastIndexOf('@')];
            for (var i = 0; i < equations.GetCount(); i++)
            {
                var equation = equations.Equation[i];
                var left = equation.Split('=')[0].Trim().Trim('"');
                if (string.Equals(left, local, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(left, fullName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"尺寸由方程驱动，禁止强改：{fullName}; EquationIndex={i}");
            }
        }
        return dimension;
    }

    private static IDimension? FindExactDimension(IModelDoc2 document, string name)
    {
        var first = name.IndexOf('@');
        var last = name.LastIndexOf('@');
        if (first <= 0 || last <= first) return null;
        var dimension = document.Parameter(name[..last]) as IDimension;
        if (dimension is null || !string.Equals(dimension.FullName, name, StringComparison.Ordinal))
            dimension = document.Parameter(name) as IDimension;
        return string.Equals(dimension?.FullName, name, StringComparison.Ordinal) ? dimension : null;
    }

    // Reopen the entire isolated scope so an assembly cannot keep a supposedly reopened
    // part alive in memory. Dirty documents cause a stop; there is no discard fallback.
    private bool ReopenIsolatedAndVerify(IReadOnlyList<PreparedDimensionUpdate> prepared, bool target, Action<string>? log)
    {
        RequireIsolatedSessionBoundary(log);
        var docs = GetOpenDocuments(_swApp!);
        var identities = docs.Select(doc => new { Path = doc.GetPathName(), Title = doc.GetTitle(),
            Type = doc.GetType(), Configuration = TryGetActiveConfigurationName(doc), Dirty = doc.GetSaveFlag() }).ToList();
        if (identities.Any(doc => doc.Configuration.StartsWith('<')))
            throw new InvalidOperationException("重开验证被阻断：无法确认当前配置。");
        if (identities.Any(doc => doc.Dirty) || identities.GroupBy(doc => doc.Title, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            throw new InvalidOperationException("重开验证被阻断：存在脏文档或重名标题，不能安全关闭；保留现场。");
        foreach (var doc in identities) RequireSavedReferenceBoundary(doc.Path, log);
        foreach (var identity in identities.OrderByDescending(doc => doc.Type == (int)swDocumentTypes_e.swDocASSEMBLY))
        {
            RequireIsolatedSessionBoundary(log);
            var live = GetOpenedDocumentByPath(identity.Path);
            if (live is null) continue; // Closing a containing test assembly may release a clean reference.
            if (live.GetSaveFlag()) throw new InvalidOperationException("关闭前文档变脏，停止重开验证。");
            _swApp!.CloseDoc(identity.Title);
            WriteLog(log, $"Isolated verification CloseDoc. PID={_expectedSolidWorksProcessId}; Path={identity.Path}");
        }
        if (GetOpenDocuments(_swApp!).Count != 0)
            throw new InvalidOperationException("测试文档仍驻留内存，不能声称已独立重开。");
        foreach (var identity in identities.OrderBy(doc => doc.Type == (int)swDocumentTypes_e.swDocASSEMBLY))
        {
            var doc = OpenIsolatedDocument(identity.Path, identity.Type, log)!;
            if (!string.Equals(TryGetActiveConfigurationName(doc), identity.Configuration, StringComparison.Ordinal))
                throw new InvalidOperationException($"重开配置不一致，不自动切换。Path={identity.Path}");
        }
        var verified = true;
        foreach (var item in prepared)
            verified &= VerifyTransactionDimension(item, target ? item.TargetSystemValue : item.OriginalSystemValue,
                target ? "PersistedTargetAfterReopen" : "PersistedOriginalAfterReopen", log);
        WriteLog(log, $"Isolated persisted verification. Target={target}; Verified={verified}; ReopenedScope={identities.Count}");
        return verified;
    }

    private IModelDoc2 RequireActiveWorkingAssembly(string? expectedPath = null)
    {
        if (!_requireIsolatedSession && (_swApp is null || !IsApplicationAlive(_swApp)))
            TryGetRunningApplication(out _);
        var document = _swApp?.ActiveDoc as IModelDoc2;
        if (document is not IAssemblyDoc)
            throw new InvalidOperationException("当前 SolidWorks 会话没有活动的工作总装配，请先切回工作总装配。");
        var path = document.GetPathName();
        if (string.IsNullOrWhiteSpace(_workingModelFolder) || string.IsNullOrWhiteSpace(path) ||
            !WorkspaceSafetyPolicy.Within(path, _workingModelFolder) ||
            (!string.IsNullOrWhiteSpace(_initialModelFolder) && WorkspaceSafetyPolicy.Within(path, _initialModelFolder)) ||
            (expectedPath is not null && !string.Equals(NormalizeFilePath(path), expectedPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("当前活动装配已变化或不属于工作副本，已停止尺寸修改。");
        WorkspaceSafetyPolicy.RejectLinks(path);
        return document;
    }

    private static bool IsApplicationAlive(SldWorks application)
    {
        try
        {
            _ = application.GetProcessID();
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal IDisposable SuspendFeatureTreeUpdates(IModelDoc2? document)
        => new FeatureTreeUpdateScope(document);

    private sealed class FeatureTreeUpdateScope : IDisposable
    {
        private readonly IFeatureManager? _featureManager;
        private readonly bool _restore;
        private readonly bool _previousTree;
        private readonly bool _previousWindow;
        private bool _disposed;

        public FeatureTreeUpdateScope(IModelDoc2? document)
        {
            try
            {
                _featureManager = document?.FeatureManager;
                if (_featureManager is null)
                    return;
                _previousTree = _featureManager.EnableFeatureTree;
                _previousWindow = _featureManager.EnableFeatureTreeWindow;
                _featureManager.EnableFeatureTree = false;
                _featureManager.EnableFeatureTreeWindow = false;
                _restore = true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                _restore = false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (!_restore || _featureManager is null)
                return;
            try
            {
                _featureManager.EnableFeatureTree = _previousTree;
                _featureManager.EnableFeatureTreeWindow = _previousWindow;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        }
    }

    internal bool TryGetLoadedAssemblyComponentModel(
        string partFilePath,
        Action<string>? log,
        CancellationToken cancellationToken,
        out IModelDoc2? document)
    {
        document = null;
        try
        {
            if (!_requireIsolatedSession && (_swApp is null || !IsApplicationAlive(_swApp)))
                TryGetRunningApplication(out _);
            if (_swApp?.ActiveDoc is not IModelDoc2 activeDoc || activeDoc is not IAssemblyDoc)
                return false;
            var activePath = activeDoc.GetPathName();
            if (string.IsNullOrWhiteSpace(_workingModelFolder) ||
                string.IsNullOrWhiteSpace(activePath) ||
                !WorkspaceSafetyPolicy.Within(activePath, _workingModelFolder) ||
                (!string.IsNullOrWhiteSpace(_initialModelFolder) && WorkspaceSafetyPolicy.Within(activePath, _initialModelFolder)))
                return false;
            if (!WorkspaceSafetyPolicy.Within(partFilePath, _workingModelFolder) ||
                (!string.IsNullOrWhiteSpace(_initialModelFolder) && WorkspaceSafetyPolicy.Within(partFilePath, _initialModelFolder)))
                return false;

            var loaded = RequireLoadedComponent(activeDoc, partFilePath, log, cancellationToken);
            document = loaded.Document;
            return document is not null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            document = null;
            return false;
        }
    }

    private bool TryUpdateAssemblyComponents(
        IReadOnlyList<SolidWorksDimensionUpdateRequest> updates,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            var assembly = RequireActiveWorkingAssembly();
            var assemblyPath = NormalizeFilePath(assembly.GetPathName());
            var prepared = new List<(SolidWorksDimensionUpdateRequest Request, IModelDoc2 Model,
                IDimension Dimension, string Configuration, double Target, double Original)>();
            foreach (var request in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var component = RequireLoadedComponent(assembly, request.PartFilePath, log, cancellationToken);
                EnsureSafeDocumentOperation(component.Document, log);
                WorkspaceSafetyPolicy.RejectLinks(request.PartFilePath);
                if ((File.GetAttributes(request.PartFilePath) & FileAttributes.ReadOnly) != 0)
                    throw new InvalidOperationException($"工作零件是只读文件：{Path.GetFileName(request.PartFilePath)}。");
                if (!string.Equals(request.Configuration, component.Configuration, StringComparison.Ordinal))
                    throw new InvalidOperationException($"尺寸目标配置与总装配引用不一致：{Path.GetFileName(request.PartFilePath)} / {request.Configuration}。");
                if (!TryParseNumericValue(request.Value, out var numeric))
                    throw new InvalidOperationException($"尺寸目标值无效：{request.Value}。");
                var dimension = RequireExactIndependentDimension(component.Document, request.DimensionName);
                object? originalRaw = dimension.GetSystemValue3((int)swInConfigurationOpts_e.swSpecifyConfiguration,
                    new[] { component.Configuration });
                if (!DimensionReadbackVerifier.Matches(originalRaw, dimension.SystemValue, out var original))
                    throw new InvalidOperationException($"无法读取配置 {component.Configuration} 下尺寸 {request.DimensionName} 的原值。");
                prepared.Add((request, component.Document, dimension, component.Configuration,
                    ConvertToSystemValue(numeric, request.Unit), original));
            }

            var changes = prepared.Select(item => new DimensionTransactionChange(
                $"{NormalizeFilePath(item.Request.PartFilePath)}|{item.Configuration}|{item.Request.DimensionName}",
                NormalizeFilePath(item.Request.PartFilePath),
                () =>
                {
                    RequireActiveWorkingAssembly(assemblyPath);
                    return item.Dimension.SetSystemValue3(item.Target, (int)swInConfigurationOpts_e.swSpecifyConfiguration,
                        new[] { item.Configuration }) == (int)swSetValueReturnStatus_e.swSetValue_Successful;
                },
                () => item.Dimension.SetSystemValue3(item.Original, (int)swInConfigurationOpts_e.swSpecifyConfiguration,
                    new[] { item.Configuration }) == (int)swSetValueReturnStatus_e.swSetValue_Successful,
                () => VerifyAssemblyDimension(item.Model, item.Dimension, item.Configuration, item.Target, log),
                () => VerifyAssemblyDimension(item.Model, item.Dimension, item.Configuration, item.Original, log)))
                .ToList();
            var groups = prepared.GroupBy(item => NormalizeFilePath(item.Request.PartFilePath), StringComparer.OrdinalIgnoreCase).ToList();
            var documents = groups.Select((group, index) => new DimensionTransactionDocument(group.Key,
                    () =>
                    {
                        RequireActiveWorkingAssembly(assemblyPath);
                        var loaded = RequireLoadedComponent(assembly, group.First().Request.PartFilePath, log, cancellationToken);
                        return string.Equals(loaded.Configuration, group.First().Configuration, StringComparison.Ordinal) &&
                               string.Equals(NormalizeFilePath(loaded.Document.GetPathName()), group.Key, StringComparison.OrdinalIgnoreCase);
                    },
                    () =>
                    {
                        var active = RequireActiveWorkingAssembly(assemblyPath);
                        using var _ = SuspendFeatureTreeUpdates(active);
                        WriteLog(log, $"正在重建工作零件：{Path.GetFileName(group.Key)}");
                        if (!group.First().Model.ForceRebuild3(false)) return false;
                        if (index != groups.Count - 1) return true;
                        WriteLog(log, "工作零件重建完成，正在重建总装配。");
                        return active.ForceRebuild3(false);
                    },
                    () =>
                    {
                        RequireActiveWorkingAssembly(assemblyPath);
                        var model = group.First().Model;
                        EnsureSafeDocumentOperation(model, log);
                        var errors = 0;
                        var warnings = 0;
                        WriteLog(log, $"正在保存工作零件：{Path.GetFileName(group.Key)}");
                        var saved = model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
                        WriteLog(log, $"Assembly component save. Path={group.Key}; Saved={saved}; Errors={errors}; Warnings={warnings}");
                        return saved && errors == 0;
                    }))
                .ToList();

            var result = DimensionTransaction.ExecuteInSession(changes, documents, message => WriteLog(log, message), cancellationToken);
            WriteLog(log, result.UserMessage);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.UserMessage);
            return result.Succeeded;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not AccessViolationException)
        {
            WriteException(log, "从活动总装配更新桁架尺寸失败，未继续执行。", ex);
            throw;
        }
    }

    private static bool VerifyAssemblyDimension(IModelDoc2 document, IDimension dimension,
        string configuration, double expected, Action<string>? log)
    {
        var exact = FindExactDimension(document, dimension.FullName);
        if (exact is null || !string.Equals(TryGetActiveConfigurationName(document), configuration, StringComparison.Ordinal))
            return false;
        object? value = exact.GetSystemValue3((int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { configuration });
        var matched = DimensionReadbackVerifier.Matches(value, expected, out var actual);
        WriteLog(log, $"Assembly transaction readback. Path={document.GetPathName()}; Configuration={configuration}; Dimension={dimension.FullName}; Expected={expected:R}; Actual={actual:R}; Verified={matched}");
        return matched;
    }

    private static (Dictionary<string, List<IComponent2>> ByPath, int Count) ScanAssemblyComponentsByPath(
        IModelDoc2 assembly,
        CancellationToken cancellationToken)
    {
        var byPath = new Dictionary<string, List<IComponent2>>(StringComparer.OrdinalIgnoreCase);
        var components = ((IAssemblyDoc)assembly).GetComponents(false) as object[]
            ?? throw new InvalidOperationException("装配组件清单未知，禁止继续修改。");
        foreach (var value in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value is not IComponent2 candidate)
                throw new InvalidOperationException("装配组件身份未知，禁止继续修改。");
            var raw = candidate.GetPathName();
            if (!Path.IsPathFullyQualified(raw))
                continue;
            var candidatePath = NormalizeFilePath(raw);
            if (!byPath.TryGetValue(candidatePath, out var matches))
                byPath[candidatePath] = matches = [];
            matches.Add(candidate);
        }

        return (byPath, components.Length);
    }

    // IMPORTANT: Project compiles against SolidWorks 2022 Interop (30.5.1), while runtime may be
    // SolidWorks 2018 (v26, 165 vtable slots on IComponent2). Never call GetSuppression2() (vtable[165]),
    // which jumps past SW2018's vtable and throws AccessViolationException. GetSuppression() is vtable[33].
    private static swComponentSuppressionState_e GetComponentSuppressionState(IComponent2 component)
        => (swComponentSuppressionState_e)component.GetSuppression();

    private (IModelDoc2 Document, string Configuration, string Instances) RequireLoadedComponent(
        IModelDoc2 assembly, string path, Action<string>? log, CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeFilePath(path);
        var assemblyPath = NormalizeFilePath(assembly.GetPathName());
        var assemblyConfiguration = TryGetActiveConfigurationName(assembly);
        if (assemblyConfiguration == "<unknown>")
            throw new InvalidOperationException("无法确认当前总装配配置。");
        if (_assemblyComponentIndex is { } existing &&
            (!string.Equals(existing.AssemblyPath, assemblyPath, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(existing.Configuration, assemblyConfiguration, StringComparison.Ordinal)))
            throw new InvalidOperationException("修改过程中活动总装配或配置已切换。");

        if (_assemblyComponentIndex is not null &&
            _assemblyComponentIndex.ResolvedByPath.TryGetValue(normalizedPath, out var cached))
        {
            try
            {
                var livePath = NormalizeFilePath(cached.Document.GetPathName());
                var liveConfiguration = TryGetActiveConfigurationName(cached.Document);
                if (string.Equals(livePath, normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(liveConfiguration, cached.Configuration, StringComparison.Ordinal))
                {
                    return (cached.Document, cached.Configuration, cached.Instances);
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }

            _assemblyComponentIndex.ResolvedByPath.Remove(normalizedPath);
        }

        if (_assemblyComponentIndex is null)
        {
            WriteLog(log, "正在读取总装配组件清单（首次扫描约需 30 秒，本轮后续读取直接走内存缓存）...");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var (byPath, count) = ScanAssemblyComponentsByPath(assembly, cancellationToken);
            _assemblyComponentIndex = new AssemblyComponentIndex(
                assemblyPath,
                assemblyConfiguration,
                byPath,
                new Dictionary<string, ResolvedComponentEntry>(StringComparer.OrdinalIgnoreCase),
                count);
            WriteLog(log, $"总装配组件清单已就绪。Components={count}; UniquePaths={byPath.Count}; ElapsedMs={stopwatch.ElapsedMilliseconds}");
        }

        var instances = new List<string>();
        var suppressedMatches = 0;
        IModelDoc2? document = null;
        string? configuration = null;
        _assemblyComponentIndex.ByPath.TryGetValue(normalizedPath, out var targetComponents);
        using (SuspendFeatureTreeUpdates(assembly))
        {
            foreach (var component in targetComponents ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(NormalizeFilePath(component.GetPathName()), normalizedPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("修改过程中组件引用路径已变化。");
                var state = GetComponentSuppressionState(component);
                if (state == swComponentSuppressionState_e.swComponentSuppressed)
                {
                    suppressedMatches++;
                    continue;
                }

                if (state is swComponentSuppressionState_e.swComponentLightweight
                    or swComponentSuppressionState_e.swComponentFullyLightweight)
                {
                    WriteLog(log, $"正在解析目标组件：{component.Name2}");
                    var status = component.SetSuppression2((int)swComponentSuppressionState_e.swComponentResolved);
                    if (status != (int)swSuppressionError_e.swSuppressionChangeOk)
                        throw new InvalidOperationException($"目标组件 {component.Name2} 无法解析，SolidWorks 状态码：{status}。");
                }
                else if (state is not (swComponentSuppressionState_e.swComponentResolved or swComponentSuppressionState_e.swComponentFullyResolved))
                {
                    throw new InvalidOperationException($"目标组件状态异常：{component.Name2} / {state}。");
                }

                var referencedConfiguration = component.ReferencedConfiguration;
                if (component.GetModelDoc2() is not IModelDoc2 loaded ||
                    !string.Equals(NormalizeFilePath(loaded.GetPathName()), normalizedPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"组件 {component.Name2} 尚未完整加载，请在总装配中将组件设为完全解析后重试。");
                if (string.IsNullOrWhiteSpace(referencedConfiguration) ||
                    !string.Equals(referencedConfiguration, TryGetActiveConfigurationName(loaded), StringComparison.Ordinal) ||
                    (configuration is not null && !string.Equals(configuration, referencedConfiguration, StringComparison.Ordinal)))
                    throw new InvalidOperationException($"组件 {component.Name2} 的活动配置与装配引用配置不一致。");
                configuration = referencedConfiguration;
                document = loaded;
                instances.Add(component.Name2);
            }
        }
        if (document is null || configuration is null)
        {
            var sameNameReferences = _assemblyComponentIndex.ByPath.Keys.Where(candidate =>
                string.Equals(Path.GetFileName(candidate), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)).ToList();
            var detail = sameNameReferences.Count > 0
                ? $"同名组件指向其他位置：{string.Join("; ", sameNameReferences)}。请重新打开工作总装配。"
                : suppressedMatches > 0 ? "该零件在当前装配配置中被压缩。"
                : $"当前装配索引含 {_assemblyComponentIndex.Count} 个组件，未找到此零件。";
            throw new InvalidOperationException($"当前总装配无法加载工作零件 {Path.GetFileName(path)}。{detail}");
        }

        var instancesSummary = string.Join(";", instances);
        _assemblyComponentIndex.ResolvedByPath[normalizedPath] = new ResolvedComponentEntry(document, configuration, instancesSummary);
        return (document, configuration, instancesSummary);
    }

    // A dimension probe never opens a document or connects to a different session.
    public Task<SolidWorksDimensionProbeResult> ReadOpenDimensionAsync(string path, string name,
        Action<string>? logWriter = null, CancellationToken cancellationToken = default)
    {
        if (!IsCadThread)
            return RunCadCommandAsync(() => ReadOpenDimensionAsync(path, name, logWriter, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new SolidWorksDimensionProbeResult { PartFilePath = path, RequestedDimensionName = name };
        try
        {
            if (_requireIsolatedSession)
                RequireIsolatedSessionBoundary(logWriter);
            var assembly = RequireActiveWorkingAssembly();
            var (doc, configuration, instances) = RequireLoadedComponent(assembly, path, logWriter, cancellationToken);
            if (!WorkspaceSafetyPolicy.Within(doc.GetPathName(), _workingModelFolder) ||
                (!string.IsNullOrWhiteSpace(_initialModelFolder) && WorkspaceSafetyPolicy.Within(doc.GetPathName(), _initialModelFolder)))
                throw new InvalidOperationException("已打开文档超出工作副本边界。");
            WorkspaceSafetyPolicy.RejectLinks(doc.GetPathName());
            result.PartFilePath = doc.GetPathName();
            result.Configuration = result.ReferencedConfiguration = configuration;
            result.ComponentInstances = instances;
            var dim = FindExactDimension(doc, name);
            if (dim is null)
                result.Message = "当前总装配引用中没有匹配的尺寸。";
            else
            {
                var value = dim.SystemValue;
                if (!double.IsFinite(value)) throw new InvalidOperationException($"尺寸值无效：{name}。");
                result.CurrentValue = Convert.ToDecimal(value * 1000d);
                result.ResolvedDimensionName = dim.FullName;
                result.FeatureName = name.Split('@')[1]; result.Message = "已核对当前工作装配组件实例、引用配置及零件活动配置的真实只读值。";
                result.Found = true;
            }
        }
        catch (Exception ex) when (ex is not (AccessViolationException or OperationCanceledException))
        {
            WriteException(logWriter, "从总装配读取尺寸失败，已停止本轮修改。", ex);
            throw new InvalidOperationException($"无法从当前总装配读取 {Path.GetFileName(path)} 的尺寸，未写入尺寸：{ex.Message} (0x{ex.HResult:X8})", ex);
        }
        WriteLog(logWriter, $"ReadOpenDimension. Path={path}; Dimension={name}; Configuration={result.Configuration}; ReferencedConfiguration={result.ReferencedConfiguration}; Components={result.ComponentInstances}; Found={result.Found}; ValueMm={result.CurrentValue}; Detail={result.Message}");
        return Task.FromResult(result);
    }

    public Task<Dictionary<string, (decimal WidthMm, decimal HeightMm, decimal ThicknessMm)>> TryReadLiveMemberSectionsAsync(
        IReadOnlyList<EditableTrussMemberItem> members,
        Action<string>? logWriter = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsCadThread)
            return RunCadCommandAsync(() => TryReadLiveMemberSectionsAsync(members, logWriter, cancellationToken), cancellationToken);

        var measured = new Dictionary<string, (decimal WidthMm, decimal HeightMm, decimal ThicknessMm)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!_requireIsolatedSession && (_swApp is null || !IsApplicationAlive(_swApp)))
                TryGetRunningApplication(out _);
            if (_swApp?.ActiveDoc is not IModelDoc2 assembly || assembly is not IAssemblyDoc)
                return Task.FromResult(measured);

            var activePath = assembly.GetPathName();
            if (string.IsNullOrWhiteSpace(_workingModelFolder) ||
                string.IsNullOrWhiteSpace(activePath) ||
                !WorkspaceSafetyPolicy.Within(activePath, _workingModelFolder) ||
                (!string.IsNullOrWhiteSpace(_initialModelFolder) && WorkspaceSafetyPolicy.Within(activePath, _initialModelFolder)))
            {
                return Task.FromResult(measured);
            }

            foreach (var targetId in new[] { "upper_chord", "lower_chord", "lower_truss" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var member = members.FirstOrDefault(m => m.Enabled && string.Equals(m.Id, targetId, StringComparison.OrdinalIgnoreCase));
                var mapping = member?.PartMappings.FirstOrDefault();
                if (member is null || mapping is null)
                    continue;

                var partPath = ResolvePortableMemberPartPathInWorkingFolder(mapping);
                if (string.IsNullOrWhiteSpace(partPath) || !File.Exists(partPath))
                    continue;

                try
                {
                    var (doc, _, _) = RequireLoadedComponent(assembly, partPath, logWriter, cancellationToken);
                    var wDim = string.IsNullOrWhiteSpace(mapping.WidthDimensionName) ? null : FindExactDimension(doc, mapping.WidthDimensionName);
                    var hDim = string.IsNullOrWhiteSpace(mapping.HeightDimensionName) ? null : FindExactDimension(doc, mapping.HeightDimensionName);
                    if (wDim is null || hDim is null)
                        continue;

                    var wMm = Convert.ToDecimal(wDim.SystemValue * 1000d);
                    var hMm = Convert.ToDecimal(hDim.SystemValue * 1000d);
                    decimal? tMm = null;

                    if (!string.IsNullOrWhiteSpace(mapping.ThicknessDimensionName) &&
                        FindExactDimension(doc, mapping.ThicknessDimensionName) is { } tDim)
                    {
                        tMm = Convert.ToDecimal(tDim.SystemValue * 1000d);
                    }
                    else if (!string.IsNullOrWhiteSpace(mapping.InnerWidthDimensionName) &&
                             FindExactDimension(doc, mapping.InnerWidthDimensionName) is { } innerWDim)
                    {
                        var innerWMm = Convert.ToDecimal(innerWDim.SystemValue * 1000d);
                        var derived = (wMm - innerWMm) / 2m;
                        if (derived > 0m)
                            tMm = derived;
                    }

                    if (wMm > 0m && hMm > 0m && tMm is > 0m)
                    {
                        measured[targetId] = (Math.Round(wMm, 3), Math.Round(hMm, 3), Math.Round(tMm.Value, 3));
                    }
                }
                catch (Exception ex) when (ex is not (AccessViolationException or OperationCanceledException))
                {
                    WriteLog(logWriter, $"Live section read skipped for {targetId}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is not (AccessViolationException or OperationCanceledException))
        {
            WriteLog(logWriter, $"Live member section snapshot skipped: {ex.Message}");
        }

        return Task.FromResult(measured);
    }

    private string ResolvePortableMemberPartPathInWorkingFolder(EditableTrussMemberPartMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(_workingModelFolder))
            return string.Empty;

        var raw = !string.IsNullOrWhiteSpace(mapping.RelativePartPath)
            ? mapping.RelativePartPath.Trim()
            : mapping.PartFilePath.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var normalized = raw.Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(normalized))
            return NormalizeFilePath(Path.Combine(_workingModelFolder, normalized));

        const string marker = "\\新模型\\";
        var idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var tail = normalized[(idx + marker.Length)..];
            var candidate = NormalizeFilePath(Path.Combine(_workingModelFolder, tail));
            if (File.Exists(candidate))
                return candidate;
        }

        return NormalizeFilePath(normalized);
    }
}
