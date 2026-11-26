using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services.Design;

namespace LlmWpfPrototype.Services.Project;

/// <summary>
/// Persistent repository managing multiple bridge-inspection-vehicle design projects,
/// multi-conversation threads per project, version checkpoints (V0 -> V1 -> V2...),
/// lazy per-project SolidWorks model workspace directories, and Markdown thesis experiment exports.
/// </summary>
public sealed class DesignProjectStoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _storageRoot;
    private readonly string _defaultWorkingModelFolder;
    private readonly string _projectsWorkspaceRoot;
    private readonly List<DesignProjectRecord> _projects = [];
    private string _activeProjectId = string.Empty;

    public DesignProjectStoreService(
        string storageRoot,
        SolidWorksWorkspaceOptions workspaceOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentNullException.ThrowIfNull(workspaceOptions);

        _storageRoot = Path.GetFullPath(storageRoot);
        Directory.CreateDirectory(_storageRoot);

        _defaultWorkingModelFolder = string.IsNullOrWhiteSpace(workspaceOptions.WorkingModelFolder)
            ? Path.Combine(_storageRoot, "default-workspace")
            : Path.GetFullPath(workspaceOptions.WorkingModelFolder);

        var parentDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_defaultWorkingModelFolder))
                        ?? _storageRoot;
        _projectsWorkspaceRoot = Path.Combine(parentDir, "项目工作区");

        LoadOrInitialize();
    }

    public string StorageRoot => _storageRoot;

    public IReadOnlyList<DesignProjectRecord> Projects => _projects;

    public DesignProjectRecord? ActiveProject =>
        _projects.FirstOrDefault(p => string.Equals(p.Id, _activeProjectId, StringComparison.OrdinalIgnoreCase))
        ?? _projects.FirstOrDefault();

    public DesignProjectRecord CreateProject(string? customName = null)
    {
        var name = string.IsNullOrWhiteSpace(customName)
            ? GenerateUniqueDefaultProjectName()
            : customName.Trim();

        var id = $"proj-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var workspaceFolder = Path.Combine(_projectsWorkspaceRoot, id);

        var project = new DesignProjectRecord
        {
            Id = id,
            Name = name,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            IsDefaultProject = false,
            WorkspaceFolder = workspaceFolder,
            IsWorkspaceMaterialized = Directory.Exists(workspaceFolder) && Directory.EnumerateFileSystemEntries(workspaceFolder).Any(),
            CurrentVersionNumber = 0,
            UpperChord = MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆"),
            LowerChord = MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆"),
            LowerTruss = MemberSectionSpecRecord.CreateBaseline("lower_truss", "下桁架")
        };

        project.VersionHistory.Add(CreateCheckpoint(project, 0, "项目初始化（继承母型基准 70×70×5 mm）", 0));

        var firstConv = CreateConversationCore(project, "新对话");
        project.ActiveConversationId = firstConv.Id;

        _projects.Insert(0, project);
        _activeProjectId = project.Id;

        SaveProject(project);
        SaveConversation(firstConv);
        SaveIndex();
        return project;
    }

    public DesignProjectRecord ForkProject(string? sourceProjectId, string? customName = null)
    {
        var source = GetProjectById(sourceProjectId) ?? ActiveProject;
        if (source is null)
        {
            return CreateProject(customName);
        }

        var id = $"proj-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var workspaceFolder = Path.Combine(_projectsWorkspaceRoot, id);
        var name = string.IsNullOrWhiteSpace(customName)
            ? GenerateUniqueDefaultProjectName()
            : customName.Trim();

        var forked = new DesignProjectRecord
        {
            Id = id,
            Name = name,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            IsDefaultProject = false,
            WorkspaceFolder = workspaceFolder,
            IsWorkspaceMaterialized = false,
            DesignGoalSummary = source.DesignGoalSummary,
            CurrentVersionNumber = source.CurrentVersionNumber,
            UpperChord = source.UpperChord.Clone(),
            LowerChord = source.LowerChord.Clone(),
            LowerTruss = source.LowerTruss.Clone(),
            VersionHistory = source.VersionHistory.Select(v => v.Clone()).ToList()
        };

        // If source already has materialized CAD files on disk, copy them so the forked branch starts from the exact modified 3D geometry!
        if (!string.IsNullOrWhiteSpace(source.WorkspaceFolder) &&
            Directory.Exists(source.WorkspaceFolder) &&
            Directory.EnumerateFiles(source.WorkspaceFolder, "*.SLD*", SearchOption.AllDirectories).Any())
        {
            try
            {
                CopyDirectoryExcludingSubtree(source.WorkspaceFolder, workspaceFolder);
                forked.IsWorkspaceMaterialized = true;
            }
            catch
            {
                forked.IsWorkspaceMaterialized = false;
            }
        }

        var conv = CreateConversationCore(forked, "新对话");
        forked.ActiveConversationId = conv.Id;

        _projects.Insert(0, forked);
        _activeProjectId = forked.Id;

        SaveProject(forked);
        SaveConversation(conv);
        SaveIndex();
        return forked;
    }

    public DesignConversationRecord CreateConversation(string? projectId, string? customTitle = null)
    {
        var project = GetProjectById(projectId) ?? ActiveProject ?? CreateProject();
        var title = string.IsNullOrWhiteSpace(customTitle)
            ? "新对话"
            : customTitle.Trim();

        var conv = CreateConversationCore(project, title);
        conv.IsTitleAutoGenerated = string.IsNullOrWhiteSpace(customTitle);
        project.ActiveConversationId = conv.Id;
        project.UpdatedAt = DateTime.Now;

        SaveConversation(conv);
        SaveProject(project);
        SaveIndex();
        return conv;
    }

    public DesignProjectRecord? GetProjectById(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        return _projects.FirstOrDefault(p => string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
    }

    public void SetActiveProject(string projectId)
    {
        var project = GetProjectById(projectId);
        if (project is null)
        {
            return;
        }

        _activeProjectId = project.Id;
        SaveIndex();
    }

    public DesignConversationRecord LoadOrCreateActiveConversation(DesignProjectRecord project)
    {
        if (!string.IsNullOrWhiteSpace(project.ActiveConversationId))
        {
            var loaded = TryLoadConversation(project.Id, project.ActiveConversationId);
            if (loaded is not null)
            {
                return loaded;
            }
        }

        if (project.Conversations.Count > 0)
        {
            var fallback = TryLoadConversation(project.Id, project.Conversations[0].Id);
            if (fallback is not null)
            {
                project.ActiveConversationId = fallback.Id;
                SaveProject(project);
                return fallback;
            }
        }

        return CreateConversation(project.Id, "新对话");
    }

    public DesignConversationRecord? TryLoadConversation(string projectId, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }

        var path = GetConversationFilePath(projectId, conversationId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<DesignConversationRecord>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void SaveConversationState(DesignProjectRecord project, DesignConversationRecord conversation)
    {
        conversation.UpdatedAt = DateTime.Now;
        project.UpdatedAt = conversation.UpdatedAt;
        project.ActiveConversationId = conversation.Id;

        var summary = project.Conversations.FirstOrDefault(c =>
            string.Equals(c.Id, conversation.Id, StringComparison.OrdinalIgnoreCase));
        if (summary is null)
        {
            summary = new DesignConversationSummary
            {
                Id = conversation.Id,
                ProjectId = project.Id,
                CreatedAt = conversation.CreatedAt
            };
            project.Conversations.Insert(0, summary);
        }

        summary.Title = conversation.Title;
        summary.UpdatedAt = conversation.UpdatedAt;
        summary.MessageCount = conversation.Messages.Count;

        SaveConversation(conversation);
        SaveProject(project);
        SaveIndex();
    }

    public void AutoUpdateConversationTitleIfDefault(
        DesignProjectRecord project,
        DesignConversationRecord conversation,
        string userMessage)
    {
        if (!conversation.IsTitleAutoGenerated || string.IsNullOrWhiteSpace(userMessage))
        {
            return;
        }

        var trimmed = userMessage.Trim().Replace("\r", " ").Replace("\n", " ");
        if (trimmed.Length <= 2 && (trimmed.Contains("你好", StringComparison.Ordinal) || trimmed.Equals("hi", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        conversation.Title = trimmed.Length <= 16 ? trimmed : trimmed[..16] + "…";
        conversation.IsTitleAutoGenerated = false;
        SaveConversationState(project, conversation);
    }

    public void RenameProject(string projectId, string newName)
    {
        var project = GetProjectById(projectId);
        if (project is null || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        project.Name = newName.Trim();
        project.UpdatedAt = DateTime.Now;
        SaveProject(project);
    }

    public void TogglePinProject(string projectId)
    {
        var project = GetProjectById(projectId);
        if (project is null)
        {
            return;
        }

        project.IsPinned = !project.IsPinned;
        SaveProject(project);
    }

    public void ToggleArchiveProject(string projectId)
    {
        var project = GetProjectById(projectId);
        if (project is null)
        {
            return;
        }

        project.IsArchived = !project.IsArchived;
        project.UpdatedAt = DateTime.Now;
        SaveProject(project);
    }

    public bool DeleteConversation(string projectId, string conversationId)
    {
        var project = GetProjectById(projectId);
        if (project is null)
        {
            return false;
        }

        project.Conversations.RemoveAll(c => string.Equals(c.Id, conversationId, StringComparison.OrdinalIgnoreCase));
        var file = GetConversationFilePath(projectId, conversationId);
        if (File.Exists(file))
        {
            try { File.Delete(file); } catch { }
        }
        DeleteConversationLogs(projectId, conversationId);

        if (project.Conversations.Count == 0)
        {
            var fresh = CreateConversationCore(project, "新对话");
            project.ActiveConversationId = fresh.Id;
            SaveConversation(fresh);
        }
        else if (string.Equals(project.ActiveConversationId, conversationId, StringComparison.OrdinalIgnoreCase))
        {
            project.ActiveConversationId = project.Conversations[0].Id;
        }

        SaveProject(project);
        return true;
    }

    public bool DeleteProject(string projectId)
    {
        var project = GetProjectById(projectId);
        if (project is null)
        {
            return false;
        }

        _projects.Remove(project);
        var projectDir = GetProjectDirectory(projectId);
        if (Directory.Exists(projectDir))
        {
            try { Directory.Delete(projectDir, recursive: true); } catch { }
        }

        if (string.Equals(_activeProjectId, projectId, StringComparison.OrdinalIgnoreCase))
        {
            _activeProjectId = _projects.FirstOrDefault()?.Id ?? string.Empty;
        }

        SaveIndex();
        return true;
    }

    private string GenerateUniqueDefaultProjectName()
    {
        const string prefix = "轻量化方案 ";
        var existingNames = new HashSet<string>(
            _projects.Select(p => p.Name.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var maxNumber = 0;
        foreach (var project in _projects)
        {
            var trimmed = project.Name.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(trimmed[prefix.Length..].Trim(), out var parsed) &&
                parsed > maxNumber)
            {
                maxNumber = parsed;
            }
        }

        var nextNumber = maxNumber + 1;
        while (existingNames.Contains($"{prefix}{nextNumber}"))
        {
            nextNumber++;
        }

        return $"{prefix}{nextNumber}";
    }

    public DesignVersionCheckpoint RecordConfirmedModification(
        DesignProjectRecord project,
        string summary,
        (decimal? Width, decimal? Height, decimal? Thickness)? upperUpdate,
        (decimal? Width, decimal? Height, decimal? Thickness)? lowerChordUpdate,
        (decimal? Width, decimal? Height, decimal? Thickness)? lowerTrussUpdate,
        long elapsedMs = 0)
    {
        var now = DateTime.Now;
        if (upperUpdate is { } u)
        {
            if (u.Width.HasValue) project.UpperChord.WidthMm = u.Width.Value;
            if (u.Height.HasValue) project.UpperChord.HeightMm = u.Height.Value;
            if (u.Thickness.HasValue) project.UpperChord.ThicknessMm = u.Thickness.Value;
            project.UpperChord.Source = "已确认改模";
            project.UpperChord.UpdatedAt = now;
        }

        if (lowerChordUpdate is { } lc)
        {
            if (lc.Width.HasValue) project.LowerChord.WidthMm = lc.Width.Value;
            if (lc.Height.HasValue) project.LowerChord.HeightMm = lc.Height.Value;
            if (lc.Thickness.HasValue) project.LowerChord.ThicknessMm = lc.Thickness.Value;
            project.LowerChord.Source = "已确认改模";
            project.LowerChord.UpdatedAt = now;
        }

        if (lowerTrussUpdate is { } lt)
        {
            if (lt.Width.HasValue) project.LowerTruss.WidthMm = lt.Width.Value;
            if (lt.Height.HasValue) project.LowerTruss.HeightMm = lt.Height.Value;
            if (lt.Thickness.HasValue) project.LowerTruss.ThicknessMm = lt.Thickness.Value;
            project.LowerTruss.Source = "已确认改模";
            project.LowerTruss.UpdatedAt = now;
        }

        project.CurrentVersionNumber++;
        project.Metrics.ConfirmedCadModifications++;
        project.IsWorkspaceMaterialized = true;
        project.UpdatedAt = now;

        var checkpoint = CreateCheckpoint(project, project.CurrentVersionNumber, summary, elapsedMs);
        project.VersionHistory.Add(checkpoint);
        SaveProject(project);
        return checkpoint;
    }

    public void ResetProjectSnapshotToBaseline(DesignProjectRecord project)
    {
        project.UpperChord = MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆");
        project.LowerChord = MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆");
        project.LowerTruss = MemberSectionSpecRecord.CreateBaseline("lower_truss", "下桁架");
        project.CurrentVersionNumber++;
        project.UpdatedAt = DateTime.Now;
        project.VersionHistory.Add(CreateCheckpoint(project, project.CurrentVersionNumber, "重置恢复至母型基准 70×70×5 mm", 0));
        SaveProject(project);
    }

    public void UpdateLiveMeasuredSections(
        DesignProjectRecord project,
        IReadOnlyDictionary<string, (decimal WidthMm, decimal HeightMm, decimal ThicknessMm)> measured)
    {
        var now = DateTime.Now;
        if (measured.TryGetValue("upper_chord", out var u))
        {
            project.UpperChord.WidthMm = Math.Round(u.WidthMm, 3);
            project.UpperChord.HeightMm = Math.Round(u.HeightMm, 3);
            project.UpperChord.ThicknessMm = Math.Round(u.ThicknessMm, 3);
            project.UpperChord.Source = "CAD实测同步";
            project.UpperChord.UpdatedAt = now;
        }

        if (measured.TryGetValue("lower_chord", out var lc))
        {
            project.LowerChord.WidthMm = Math.Round(lc.WidthMm, 3);
            project.LowerChord.HeightMm = Math.Round(lc.HeightMm, 3);
            project.LowerChord.ThicknessMm = Math.Round(lc.ThicknessMm, 3);
            project.LowerChord.Source = "CAD实测同步";
            project.LowerChord.UpdatedAt = now;
        }

        if (measured.TryGetValue("lower_truss", out var lt))
        {
            project.LowerTruss.WidthMm = Math.Round(lt.WidthMm, 3);
            project.LowerTruss.HeightMm = Math.Round(lt.HeightMm, 3);
            project.LowerTruss.ThicknessMm = Math.Round(lt.ThicknessMm, 3);
            project.LowerTruss.Source = "CAD实测同步";
            project.LowerTruss.UpdatedAt = now;
        }

        project.IsWorkspaceMaterialized = true;
        project.UpdatedAt = now;
        SaveProject(project);
    }

    public void SaveProject(DesignProjectRecord project)
    {
        var dir = GetProjectDirectory(project.Id);
        Directory.CreateDirectory(dir);
        WriteJsonAtomically(Path.Combine(dir, "project.json"), project);
    }

    public string ExportProjectMarkdownReport(string projectId, string outputDirectory)
    {
        var project = GetProjectById(projectId) ?? ActiveProject
            ?? throw new InvalidOperationException("当前没有可导出的设计项目。");
        Directory.CreateDirectory(outputDirectory);
        var safeName = string.Concat(project.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var reportPath = Path.Combine(outputDirectory, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.md");

        var sb = new StringBuilder();
        sb.AppendLine($"# 桥梁检查车智能设计项目报告：{project.Name}");
        sb.AppendLine();
        sb.AppendLine($"- **项目编号**：`{project.Id}`");
        sb.AppendLine($"- **创建时间**：{project.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **最后更新**：{project.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **当前版本**：`{project.VersionTag}`");
        sb.AppendLine($"- **设计目标与约束备注**：{project.DesignGoalSummary}");
        sb.AppendLine($"- **累计交互轮次**：{project.Metrics.TotalUserTurns} 轮 | **确认改模次数**：{project.Metrics.ConfirmedCadModifications} 次");
        sb.AppendLine();
        sb.AppendLine("## 一、当前结构截面状态与力学指标对比（相对 70×70×5 母型）");
        sb.AppendLine();
        sb.AppendLine("| 构件名称 | 当前规格 (mm) | 来源 | 线质量 (kg/m) | 重量变化 | 惯性矩 Ix (10^5 mm^4) | 刚度变化 | 安全区间评价 |");
        sb.AppendLine("| :--- | :--- | :--- | :---: | :---: | :---: | :---: | :--- |");
        foreach (var member in new[] { project.UpperChord, project.LowerChord, project.LowerTruss })
        {
            var m = member.Mechanics;
            sb.AppendLine(
                $"| {member.MemberName} | `{member.SpecText}` | {member.Source} | {m.LineMassKgPerM:0.00} | " +
                $"{RectangularTubeMechanicsResult.FormatSignedPercent(m.MassDeltaPercentVsBase)} | {(m.IxMm4 / 100000m):0.00} | " +
                $"{RectangularTubeMechanicsResult.FormatSignedPercent(m.IxDeltaPercentVsBase)} | {m.DomainTierLabel} |");
        }

        sb.AppendLine();
        sb.AppendLine("## 二、版本演进时间线（Checkpoints）");
        sb.AppendLine();
        sb.AppendLine("| 版本 | 时间 | 变更摘要 | 上弦杆 | 下弦杆 | 下桁架 | CAD耗时(ms) |");
        sb.AppendLine("| :---: | :--- | :--- | :---: | :---: | :---: | :---: |");
        foreach (var cp in project.VersionHistory)
        {
            sb.AppendLine($"| `{cp.VersionTag}` | {cp.CreatedAt:yyyy-MM-dd HH:mm:ss} | {cp.Summary} | `{cp.UpperChord.SpecText}` | `{cp.LowerChord.SpecText}` | `{cp.LowerTruss.SpecText}` | {cp.CadExecutionElapsedMs} |");
        }

        sb.AppendLine();
        sb.AppendLine("## 三、多轮设计对话归档");
        foreach (var summary in project.Conversations)
        {
            sb.AppendLine();
            sb.AppendLine($"### 对话：{summary.Title}（更新于 {summary.UpdatedAt:yyyy-MM-dd HH:mm}）");
            var conv = TryLoadConversation(project.Id, summary.Id);
            if (conv is null) continue;
            foreach (var msg in conv.Messages)
            {
                sb.AppendLine($"- **[{msg.Timestamp:HH:mm:ss}] {msg.RoleDisplay}**：{msg.Text}");
            }
        }

        File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
        return reportPath;
    }

    private void LoadOrInitialize()
    {
        var indexPath = GetIndexFilePath();
        var indexLoaded = false;
        if (File.Exists(indexPath))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<DesignProjectsIndexDocument>(File.ReadAllText(indexPath, Encoding.UTF8), JsonOptions);
                if (doc is not null)
                {
                    indexLoaded = true;
                    foreach (var id in doc.ProjectIds)
                    {
                        var projPath = Path.Combine(GetProjectDirectory(id), "project.json");
                        if (!File.Exists(projPath)) continue;
                        var proj = JsonSerializer.Deserialize<DesignProjectRecord>(File.ReadAllText(projPath, Encoding.UTF8), JsonOptions);
                        if (proj is not null)
                        {
                            _projects.Add(proj);
                        }
                    }

                    _activeProjectId = doc.ActiveProjectId;
                }
            }
            catch
            {
            }
        }

        if (!indexLoaded && _projects.Count == 0)
        {
            var defaultProj = new DesignProjectRecord
            {
                Id = "proj-default",
                Name = "默认检查车设计项目",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                IsDefaultProject = true,
                WorkspaceFolder = _defaultWorkingModelFolder,
                IsWorkspaceMaterialized = Directory.Exists(_defaultWorkingModelFolder) &&
                                          Directory.EnumerateFiles(_defaultWorkingModelFolder, "*.SLD*", SearchOption.AllDirectories).Any(),
                CurrentVersionNumber = 0,
                UpperChord = MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆"),
                LowerChord = MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆"),
                LowerTruss = MemberSectionSpecRecord.CreateBaseline("lower_truss", "下桁架")
            };
            defaultProj.VersionHistory.Add(CreateCheckpoint(defaultProj, 0, "初始母型基准（70×70×5 mm）", 0));

            var conv = CreateConversationCore(defaultProj, "新对话");
            defaultProj.ActiveConversationId = conv.Id;

            _projects.Add(defaultProj);
            _activeProjectId = defaultProj.Id;

            SaveProject(defaultProj);
            SaveConversation(conv);
            SaveIndex();
        }
        else if (_projects.Count == 0)
        {
            _activeProjectId = string.Empty;
        }
        else if (!_projects.Any(p => string.Equals(p.Id, _activeProjectId, StringComparison.OrdinalIgnoreCase)))
        {
            _activeProjectId = _projects[0].Id;
            SaveIndex();
        }
    }

    private static DesignVersionCheckpoint CreateCheckpoint(
        DesignProjectRecord project,
        int versionNumber,
        string summary,
        long elapsedMs) => new()
    {
        VersionNumber = versionNumber,
        Summary = summary,
        CreatedAt = DateTime.Now,
        UpperChord = project.UpperChord.Clone(),
        LowerChord = project.LowerChord.Clone(),
        LowerTruss = project.LowerTruss.Clone(),
        CadExecutionElapsedMs = elapsedMs
    };

    private static DesignConversationRecord CreateConversationCore(DesignProjectRecord project, string title)
    {
        var conv = new DesignConversationRecord
        {
            Id = $"conv-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            ProjectId = project.Id,
            Title = title,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        project.Conversations.Insert(0, new DesignConversationSummary
        {
            Id = conv.Id,
            ProjectId = project.Id,
            Title = conv.Title,
            CreatedAt = conv.CreatedAt,
            UpdatedAt = conv.UpdatedAt,
            MessageCount = 0
        });

        return conv;
    }

    private void SaveConversation(DesignConversationRecord conversation)
    {
        var dir = Path.Combine(GetProjectDirectory(conversation.ProjectId), "conversations");
        Directory.CreateDirectory(dir);
        WriteJsonAtomically(GetConversationFilePath(conversation.ProjectId, conversation.Id), conversation);
    }

    private void SaveIndex()
    {
        var doc = new DesignProjectsIndexDocument
        {
            SchemaVersion = 1,
            ActiveProjectId = _activeProjectId,
            ProjectIds = _projects.Select(p => p.Id).ToList()
        };
        WriteJsonAtomically(GetIndexFilePath(), doc);
    }

    private const int MaxPersistedConversationLogs = 1000;

    public List<LogEntryItem> LoadConversationLogs(string projectId, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(conversationId))
        {
            return [];
        }

        var path = GetConversationLogFilePath(projectId, conversationId);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<LogEntryItem>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveConversationLogs(string projectId, string conversationId, IEnumerable<LogEntryItem> entries)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        var list = entries.ToList();
        var path = GetConversationLogFilePath(projectId, conversationId);
        if (list.Count == 0)
        {
            DeleteConversationLogs(projectId, conversationId);
            return;
        }

        if (list.Count > MaxPersistedConversationLogs)
        {
            list = list.Skip(list.Count - MaxPersistedConversationLogs).ToList();
        }

        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            WriteJsonAtomically(path, list);
        }
        catch
        {
            // Never let log persistence failure interrupt design operations.
        }
    }

    public void DeleteConversationLogs(string projectId, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        var path = GetConversationLogFilePath(projectId, conversationId);
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
    }

    private string GetIndexFilePath() => Path.Combine(_storageRoot, "projects-index.json");

    private string GetProjectDirectory(string projectId) => Path.Combine(_storageRoot, projectId);

    private string GetConversationFilePath(string projectId, string conversationId) =>
        Path.Combine(GetProjectDirectory(projectId), "conversations", $"{conversationId}.json");

    private string GetConversationLogFilePath(string projectId, string conversationId) =>
        Path.Combine(GetProjectDirectory(projectId), "conversations", $"{conversationId}.logs.json");

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(tempPath, json, new UTF8Encoding(false));
        File.Move(tempPath, path, overwrite: true);
    }

    private static void CopyDirectoryExcludingSubtree(string sourceFolder, string targetFolder)
    {
        var sourceDir = new DirectoryInfo(sourceFolder);
        Directory.CreateDirectory(targetFolder);
        foreach (var dir in sourceDir.GetDirectories("*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceFolder, dir.FullName);
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(seg => string.Equals(seg, "backup", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(seg, "初始模型", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(targetFolder, rel));
        }

        foreach (var file in sourceDir.GetFiles("*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceFolder, file.FullName);
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(seg => string.Equals(seg, "backup", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(seg, "初始模型", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var dest = Path.Combine(targetFolder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            file.CopyTo(dest, overwrite: true);
        }
    }
}
