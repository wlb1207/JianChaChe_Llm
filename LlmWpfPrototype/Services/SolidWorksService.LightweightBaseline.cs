using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using LlmWpfPrototype.Models;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace LlmWpfPrototype.Services;

public sealed partial class SolidWorksService
{
    public Task<LightweightBaselineResult> ReadConfiguredLightweightBaselineAsync(
        string expectedWorkingAssemblyPath,
        IReadOnlyList<EditableTrussMemberItem> members,
        Action<string>? log = null,
        CancellationToken cancellationToken = default) =>
        RunCadCommandAsync(() => Task.FromResult(
            ReadConfiguredLightweightBaseline(expectedWorkingAssemblyPath, members, log, cancellationToken)),
            cancellationToken);

    private LightweightBaselineResult ReadConfiguredLightweightBaseline(
        string expectedWorkingAssemblyPath,
        IReadOnlyList<EditableTrussMemberItem> members,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var capturedAt = DateTimeOffset.Now;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assemblyPath = ValidateBaselineModelPath(expectedWorkingAssemblyPath, ".SLDASM");
            if (!_requireIsolatedSession && (_swApp is null || !IsApplicationAlive(_swApp)) &&
                TryGetRunningApplication(out var runningApp) && runningApp is not null)
            {
                _swApp = runningApp;
            }
            if (_swApp is null)
                throw new InvalidOperationException("当前程序尚未持有 SolidWorks 会话，请在本程序打开工作总装后再采集。");
            if (_requireIsolatedSession && _swApp.GetProcessID() != _expectedSolidWorksProcessId)
                throw new InvalidOperationException("无法确认当前 SolidWorks 实例身份。");

            var assembly = RequireActiveWorkingAssembly(assemblyPath);
            var assemblyConfiguration = TryGetActiveConfigurationName(assembly);
            if (assemblyConfiguration == "<unknown>")
                throw new InvalidOperationException("无法确认工作总装活动配置。");

            var components = ((IAssemblyDoc)assembly).GetComponents(false) as object[]
                ?? throw new InvalidOperationException("无法读取工作总装的组件清单。");
            var byPath = new Dictionary<string, List<IComponent2>>(StringComparer.OrdinalIgnoreCase);
            var issues = new List<string>();
            foreach (var item in components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item is not IComponent2 component)
                {
                    issues.Add("组件清单中含无法识别的对象，采集范围可能不完整。");
                    continue;
                }

                try
                {
                    var rawPath = component.GetPathName();
                    if (string.IsNullOrWhiteSpace(rawPath) || !Path.IsPathFullyQualified(rawPath))
                        continue;
                    var path = WorkspaceSafetyPolicy.Full(rawPath);
                    if (!byPath.TryGetValue(path, out var matches))
                        byPath[path] = matches = [];
                    matches.Add(component);
                }
                catch (Exception ex) when (IsBaselineReadFailure(ex))
                {
                    issues.Add($"一项组件的路径无法读取，采集范围可能不完整（0x{ex.HResult:X8}）。");
                }
            }

            var observed = new List<LightweightMemberBaseline>();
            using var treeGuard = SuspendFeatureTreeUpdates(assembly);
            foreach (var member in members.Where(member => member.Enabled &&
                         (member.Id.Equals("upper_chord", StringComparison.OrdinalIgnoreCase) ||
                          member.Id.Equals("lower_chord", StringComparison.OrdinalIgnoreCase) ||
                          member.Id.Equals("lower_truss", StringComparison.OrdinalIgnoreCase))))
            {
                if (member.PartMappings.Count == 0)
                    issues.Add($"{member.Name}没有零件级尺寸映射。");
                foreach (var mapping in member.PartMappings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string partPath;
                    try
                    {
                        partPath = ResolveBaselinePartPath(mapping);
                    }
                    catch (Exception ex) when (IsBaselineReadFailure(ex))
                    {
                        observed.Add(UnavailableBaselineMember(member, mapping, "", ex.Message));
                        continue;
                    }

                    if (!byPath.TryGetValue(partPath, out var matches) || matches.Count == 0)
                    {
                        observed.Add(UnavailableBaselineMember(member, mapping, partPath, "当前总装没有引用该精确零件路径。"));
                        continue;
                    }

                    var instances = matches.Select(component => (Component: component, Name: ReadBaselineComponentName(component))).ToList();
                    if (!string.IsNullOrWhiteSpace(mapping.ComponentName) && !instances.Any(instance =>
                            instance.Name.Equals(mapping.ComponentName, StringComparison.OrdinalIgnoreCase) ||
                            instance.Name.EndsWith("/" + mapping.ComponentName, StringComparison.OrdinalIgnoreCase)))
                    {
                        observed.Add(UnavailableBaselineMember(member, mapping, partPath,
                            "零件路径匹配，但配置中指定的组件实例未找到。"));
                        continue;
                    }

                    foreach (var instance in instances)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        observed.Add(ReadBaselineMember(member, mapping, partPath, instance.Component, instance.Name));
                    }
                }
            }

            if (observed.Count == 0)
                issues.Add("没有配置可采集的上弦杆或下弦杆零件。");

            var finalAssembly = RequireActiveWorkingAssembly(assemblyPath);
            if (!string.Equals(assemblyConfiguration, TryGetActiveConfigurationName(finalAssembly), StringComparison.Ordinal))
                throw new InvalidOperationException("采集过程中工作总装配置发生变化，本次读数不作为基线。");

            WriteLog(log, $"Lightweight baseline read. Assembly={assemblyPath}, Configuration={assemblyConfiguration}, Observations={observed.Count}");
            return new LightweightBaselineResult
            {
                CapturedAt = capturedAt,
                AssemblyPath = assemblyPath,
                AssemblyConfiguration = assemblyConfiguration,
                AssemblyVerified = true,
                Members = observed,
                Issues = issues.Distinct().ToList()
            };
        }
        catch (Exception ex) when (IsBaselineReadFailure(ex))
        {
            WriteException(log, "轻量化基线无法读取。", ex);
            return new LightweightBaselineResult { CapturedAt = capturedAt, Issues = [ex.Message] };
        }
    }

    private string ResolveBaselinePartPath(EditableTrussMemberPartMapping mapping)
    {
        var path = !string.IsNullOrWhiteSpace(mapping.RelativePartPath)
            ? mapping.RelativePartPath.Trim()
            : mapping.PartFilePath.Trim();
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("零件映射没有配置路径。");
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(normalized))
        {
            path = Path.Combine(_workingModelFolder, normalized);
        }
        else
        {
            const string marker = "\\新模型\\";
            var idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var candidate = Path.Combine(_workingModelFolder, normalized[(idx + marker.Length)..]);
                if (File.Exists(candidate))
                    path = candidate;
            }
        }
        return ValidateBaselineModelPath(path, ".SLDPRT");
    }

    private string ValidateBaselineModelPath(string path, string extension)
    {
        var fullPath = WorkspaceSafetyPolicy.Full(path);
        if (string.IsNullOrWhiteSpace(_workingModelFolder) ||
            !WorkspaceSafetyPolicy.Within(fullPath, _workingModelFolder) ||
            (!string.IsNullOrWhiteSpace(_initialModelFolder) && WorkspaceSafetyPolicy.Within(fullPath, _initialModelFolder)) ||
            !Path.GetExtension(fullPath).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"基线路径须指向工作副本中的 {extension} 文件。");
        WorkspaceSafetyPolicy.RejectLinks(fullPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("配置中的工作副本文件不存在。", fullPath);
        return fullPath;
    }

    private static string ReadBaselineComponentName(IComponent2 component)
    {
        try { return component.Name2 ?? ""; }
        catch (COMException) { return ""; }
    }

    private static LightweightMemberBaseline ReadBaselineMember(
        EditableTrussMemberItem member,
        EditableTrussMemberPartMapping mapping,
        string partPath,
        IComponent2 component,
        string name)
    {
        string configuration = "";
        try
        {
            if (string.IsNullOrWhiteSpace(name) || !string.Equals(partPath,
                    WorkspaceSafetyPolicy.Full(component.GetPathName()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("组件身份无法确认或路径已变化。");
            configuration = component.ReferencedConfiguration;
            var state = (swComponentSuppressionState_e)component.GetSuppression();
            if (state is swComponentSuppressionState_e.swComponentLightweight or swComponentSuppressionState_e.swComponentFullyLightweight)
            {
                var resolveStatus = component.SetSuppression2((int)swComponentSuppressionState_e.swComponentResolved);
                if (resolveStatus == (int)swSuppressionError_e.swSuppressionChangeOk)
                    state = (swComponentSuppressionState_e)component.GetSuppression();
            }
            if (state is not (swComponentSuppressionState_e.swComponentResolved or
                swComponentSuppressionState_e.swComponentFullyResolved))
                throw new InvalidOperationException($"组件未完全解析（状态 {(int)state}）。请在 SolidWorks 中确认该组件后重新采集。");
            if (component.GetModelDoc2() is not IModelDoc2 doc || doc is not IPartDoc part ||
                !string.Equals(partPath, WorkspaceSafetyPolicy.Full(doc.GetPathName()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("已加载零件的实际路径与映射不一致。");
            if (string.IsNullOrWhiteSpace(configuration) || configuration == "<unknown>" ||
                !string.Equals(configuration, TryGetActiveConfigurationName(doc), StringComparison.Ordinal))
                throw new InvalidOperationException("装配引用配置与已加载零件的活动配置不一致。");

            var width = ReadBaselineDimension(doc, mapping.WidthDimensionName, verifyOuterProfileRole: true);
            var height = ReadBaselineDimension(doc, mapping.HeightDimensionName, verifyOuterProfileRole: true);
            var thickness = ReadBaselineDimension(doc, mapping.ThicknessDimensionName);
            var innerWidth = ReadBaselineDimension(doc, mapping.InnerWidthDimensionName);
            var innerHeight = ReadBaselineDimension(doc, mapping.InnerHeightDimensionName);
            var (wallX, wallY, wall, wallStatus) = CheckBaselineWallThickness(mapping, width, height, thickness, innerWidth, innerHeight);
            var (material, mass, massStatus) = ReadBaselineMass(doc, part, configuration);
            if (!string.Equals(configuration, component.ReferencedConfiguration, StringComparison.Ordinal) ||
                !string.Equals(configuration, TryGetActiveConfigurationName(doc), StringComparison.Ordinal))
                throw new InvalidOperationException("读取过程中组件或零件配置已变化。");

            return new LightweightMemberBaseline
            {
                MemberId = member.Id,
                MemberName = member.Name,
                PartPath = partPath,
                ExpectedComponentName = mapping.ComponentName,
                ComponentName = name,
                Configuration = configuration,
                ReferenceStatus = "verified",
                OuterWidth = width,
                OuterHeight = height,
                ConfiguredThickness = thickness,
                InnerWidth = innerWidth,
                InnerHeight = innerHeight,
                DerivedWallThicknessXmm = wallX,
                DerivedWallThicknessYmm = wallY,
                ConsistentWallThicknessMm = wall,
                WallThicknessStatus = wallStatus,
                HasUnsavedChanges = doc.GetSaveFlag(),
                Material = material,
                MassKg = mass,
                MassStatus = massStatus
            };
        }
        catch (Exception ex) when (IsBaselineReadFailure(ex))
        {
            return UnavailableBaselineMember(member, mapping, partPath, ex.Message, name, configuration);
        }
    }

    private static LightweightDimensionObservation ReadBaselineDimension(
        IModelDoc2 doc, string? name, bool verifyOuterProfileRole = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new LightweightDimensionObservation { Detail = "映射没有指定尺寸。"};
        try
        {
            var dimension = FindExactDimension(doc, name);
            if (dimension is null)
                return new LightweightDimensionObservation { DimensionName = name, Status = "not_found", Detail = "配置中的精确尺寸未找到。" };

            var type = (swDimensionType_e)dimension.GetType();
            if (type is not (swDimensionType_e.swLinearDimension or swDimensionType_e.swHorLinearDimension or
                swDimensionType_e.swVertLinearDimension))
                return new LightweightDimensionObservation { DimensionName = name, Status = "wrong_dimension_type", Detail = $"类型 {type} 无法作为直线尺寸。" };

            var mm = PositiveBaselineValue(dimension.SystemValue * 1000d);
            if (mm is null)
                return new LightweightDimensionObservation { DimensionName = name, Status = "invalid_value", Detail = "CAD 尺寸值为零、负数或不在有效数值范围。" };

            var feature = name.Split('@')[1];
            if (verifyOuterProfileRole &&
                (feature.Contains("拉伸", StringComparison.Ordinal) || feature.Contains("extrud", StringComparison.OrdinalIgnoreCase)))
                return new LightweightDimensionObservation
                {
                    DimensionName = name,
                    ResolvedDimensionName = dimension.FullName,
                    Status = "profile_role_unverified",
                    Detail = $"拉伸尺寸可读为 {mm.Value.ToString(CultureInfo.InvariantCulture)} mm，但其截面方向用途尚未核实。"
                };

            return new LightweightDimensionObservation
            {
                DimensionName = name,
                ResolvedDimensionName = dimension.FullName,
                Status = "measured",
                ValueMm = mm,
                Detail = "从装配引用的活动零件配置读取，单位 mm。"
            };
        }
        catch (Exception ex) when (IsBaselineReadFailure(ex))
        {
            return new LightweightDimensionObservation { DimensionName = name, Status = "read_failed", Detail = $"读取失败（0x{ex.HResult:X8}）。" };
        }
    }

    private static (decimal? X, decimal? Y, decimal? Wall, string Status) CheckBaselineWallThickness(
        EditableTrussMemberPartMapping mapping,
        LightweightDimensionObservation width,
        LightweightDimensionObservation height,
        LightweightDimensionObservation thickness,
        LightweightDimensionObservation innerWidth,
        LightweightDimensionObservation innerHeight)
    {
        const decimal toleranceMm = 0.001m;
        const string innerProfileMode = "OuterMinusTwoThickness";
        var explicitlyMappedInnerProfile = mapping.InnerWidthValueMode.Equals(innerProfileMode, StringComparison.OrdinalIgnoreCase) &&
                                          mapping.InnerHeightValueMode.Equals(innerProfileMode, StringComparison.OrdinalIgnoreCase);
        var direct = thickness.ValueMm;
        if (!explicitlyMappedInnerProfile)
            return direct is { } directWall &&
                (width.ValueMm is null || directWall < width.ValueMm / 2m) &&
                (height.ValueMm is null || directWall < height.ValueMm / 2m)
                    ? (null, null, direct, "measured_directly")
                    : (null, null, null, "wall_thickness_unverified");

        var x = (width.ValueMm - innerWidth.ValueMm) / 2m;
        var y = (height.ValueMm - innerHeight.ValueMm) / 2m;
        if (x is not > 0 || y is not > 0 || Math.Abs(x.Value - y.Value) > toleranceMm ||
            (direct is { } directValue && Math.Abs(directValue - x.Value) > toleranceMm))
            return (x is > 0 ? x : null, y is > 0 ? y : null, null, "inner_and_outer_profile_unverified");
        return (x, y, direct ?? (x + y) / 2m, direct is not null ? "profile_cross_checked" : "derived_from_measured_profile");
    }

    private static (string Material, decimal? Mass, string Status) ReadBaselineMass(
        IModelDoc2 doc, IPartDoc part, string configuration)
    {
        var material = "";
        try
        {
            material = part.GetMaterialPropertyName2(configuration, out var database) ?? "";
            if (string.IsNullOrWhiteSpace(material) || string.IsNullOrWhiteSpace(database))
                return (material, null, "material_not_verified");
            if (part.GetBodies2((int)swBodyType_e.swSolidBody, false) is not object[] { Length: 1 })
                return (material, null, "single_solid_body_not_verified");
            var properties = doc.Extension.CreateMassProperty();
            if (properties is null || !properties.UseSystemUnits || properties.UserAssigned || properties.OverrideMass ||
                PositiveBaselineValue(properties.Density) is null)
                return (material, null, "physical_mass_not_verified");
            var mass = PositiveBaselineValue(properties.Mass);
            return (material, mass, mass is null ? "invalid_mass" : "measured_in_cad");
        }
        catch (Exception ex) when (IsBaselineReadFailure(ex))
        {
            return (material, null, $"mass_read_failed_0x{ex.HResult:X8}");
        }
    }

    private static decimal? PositiveBaselineValue(double value)
    {
        if (!double.IsFinite(value) || value <= 0 || value >= (double)decimal.MaxValue)
            return null;
        var number = Convert.ToDecimal(value);
        return number > 0 ? number : null;
    }

    private static LightweightMemberBaseline UnavailableBaselineMember(
        EditableTrussMemberItem member, EditableTrussMemberPartMapping mapping,
        string partPath, string detail, string name = "", string configuration = "") => new()
    {
        MemberId = member.Id,
        MemberName = member.Name,
        PartPath = partPath,
        ExpectedComponentName = mapping.ComponentName,
        ComponentName = name,
        Configuration = configuration,
        ReferenceStatus = "unavailable",
        MappingNote = detail,
        WallThicknessStatus = "unavailable",
        MassStatus = "unavailable",
        OuterWidth = UnavailableBaselineDimension(mapping.WidthDimensionName),
        OuterHeight = UnavailableBaselineDimension(mapping.HeightDimensionName),
        ConfiguredThickness = UnavailableBaselineDimension(mapping.ThicknessDimensionName),
        InnerWidth = UnavailableBaselineDimension(mapping.InnerWidthDimensionName),
        InnerHeight = UnavailableBaselineDimension(mapping.InnerHeightDimensionName)
    };

    private static LightweightDimensionObservation UnavailableBaselineDimension(string? name) => new()
    {
        DimensionName = name ?? "",
        Status = "component_unavailable",
        Detail = "组件身份或配置尚未确认。"
    };

    private static bool IsBaselineReadFailure(Exception ex) =>
        ex is COMException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException;
}
