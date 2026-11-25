using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services;
using LlmWpfPrototype.Services.Simulation;
using LlmWpfPrototype.ViewModels;

Console.OutputEncoding = Encoding.UTF8;
Require(Path.TrimEndingDirectorySeparator(AppPaths.BaseDirectory) == Path.TrimEndingDirectorySeparator(AppPaths.ProjectRoot),
    "Use a separate test output outside the application source tree.");

var workspaceRoot = Path.Combine(Path.GetTempPath(), "RagEntryChecks", Guid.NewGuid().ToString("N"));
var settings = AppConfigLoader.LoadSettings();
var configuredLlmOptions = JsonSerializer.Deserialize<LlmOptions>(JsonSerializer.Serialize(settings.Llm))!;
settings.Llm.Mode = "Mock";
settings.SolidWorksWorkspace = new SolidWorksWorkspaceOptions
{
    ModelRootPath = workspaceRoot,
    InitialModelFolder = Path.Combine(workspaceRoot, "initial"),
    WorkingModelFolder = Path.Combine(workspaceRoot, "working"),
    AssemblyFileName = "test.SLDASM"
};
File.WriteAllText(AppConfigLoader.GetConfigPath(), JsonSerializer.Serialize(settings));
var workspaceOptions = settings.SolidWorksWorkspace;

var service = DispatchProxy.Create<ISolidWorksService, FakeCad>();
var cad = (FakeCad)(object)service;
var workspace = new ModelWorkspaceManager(workspaceOptions, service);
var chord = new TrussChordSectionConfigService();
var members = new EditableTrussMemberCatalogService();
var dictionary = new ParameterDictionaryService(chord);
var parser = new TrussMemberCommandParser(members);
var scans = new DimensionScanCatalogService();
var recommendation = new TrussParameterMappingRecommendationService(scans);
var dispatcher = new CommandDispatcher(service, workspace, members, parser, scans, recommendation,
    new TrussParameterSetupStateService(service, scans, members));
var llm = new FakeLlm();
var simulation = new SimulationOptions { LauncherPath = Path.Combine(workspaceRoot, "launcher.exe") };
Directory.CreateDirectory(workspaceRoot);
File.WriteAllText(simulation.LauncherPath, "Launcher stand-in. The test injects a recording start delegate.");
var launcherStarts = new List<string>();
var vm = new MainViewModel(llm, dictionary, new ApdlReplacementService(), service, dispatcher, workspace,
    settings.Llm, chord, members, scans, recommendation, parser, simulation, launcherStarts.Add);

const string engineeringQuestion = "主梁的安全系数该怎么选？";
var reply = await SendAsync(vm, engineeringQuestion);
Require(reply.IsLocalKnowledgeReply && reply.LocalCitationStatus.Contains("有效引用编号：E1"),
    "The engineering query must use local knowledge and show a valid citation. " + reply.Text);
Require(reply.LocalSources.Any(source => source.Source.SourceDocument == "verified/08_safety_factor.md"),
    "The chat reply is missing the verified safety factor source.");
Require(llm.KnowledgeRequests.Count == 1 && llm.DesignRequests.Count == 0 && cad.MutatingCalls == 0,
    "The engineering query must only call the knowledge answer endpoint.");
using (var prompt = JsonDocument.Parse(llm.KnowledgeRequests.Single()))
{
    var transmitted = prompt.RootElement.GetProperty("evidence").EnumerateArray().ToArray();
    Require(transmitted.Any(source => source.GetProperty("filename").GetString() == "verified/08_safety_factor.md"),
        "The safety factor source did not reach the model context.");
    Require(transmitted.All(source =>
    {
        var mapped = reply.LocalSources.Single(record => record.Id == source.GetProperty("id").GetString());
        return mapped.SentText == source.GetProperty("text").GetString() &&
            source.GetProperty("source_ranges").GetArrayLength() > 0;
    }), "Citation sources do not match the selected evidence and source ranges.");
}
Console.WriteLine("PASS natural engineering question without a standard name retrieves protected evidence and produces a citation");

llm.KnowledgeAnswer = "未经验证的固定结论 [E999]。";
var invalid = await SendAsync(vm, engineeringQuestion);
Require(invalid.IsLocalKnowledgeReply && invalid.LocalCitationStatus.Contains("引用异常") &&
    !invalid.Text.Contains("未经验证的固定结论"), "An answer with an unbound citation was exposed. " + invalid.Text);
llm.KnowledgeAnswer = "没有引用的固定工程结论。";
var uncited = await SendAsync(vm, engineeringQuestion);
Require(uncited.IsLocalKnowledgeReply && uncited.LocalCitationStatus.Contains("引用缺失") &&
    !uncited.Text.Contains("没有引用的固定工程结论"), "An answer without citations was exposed. " + uncited.Text);
Require(llm.KnowledgeRequests.Count == 3 && llm.DesignRequests.Count == 0 && cad.MutatingCalls == 0,
    "Invalid citations fell through to a different reply or CAD path.");
Console.WriteLine("PASS unsupported model claims are suppressed on invalid or missing citations");

var noEvidence = await SendAsync(vm, "GB/T 999999-2099 的火星磁流体规范如何定义？");
Require(noEvidence.IsLocalKnowledgeReply && noEvidence.LocalSources.Count == 0 &&
    llm.KnowledgeRequests.Count == 3 && llm.DesignRequests.Count == 0 && cad.MutatingCalls == 0 &&
    noEvidence.Text.Contains("构件") && noEvidence.Text.Contains("使用工况"),
    "A query with no eligible evidence must return a local reply without model or CAD calls. " + noEvidence.Text);
Console.WriteLine("PASS missing evidence returns an answer locally without an unsupported model conclusion");

var ordinary = await SendAsync(vm, "给我讲一个笑话");
Require(!ordinary.IsLocalKnowledgeReply && llm.DesignRequests.Count == 1 && llm.KnowledgeRequests.Count == 3 &&
    cad.MutatingCalls == 0, "Non-engineering chat did not use the existing reply path.");
Require(llm.DesignRequests[0].History.All(item => item.KnowledgeProvenance is null &&
    !item.Content.Contains("[E1]") && !item.Content.Contains("verified/08_safety_factor.md")),
    "RAG evidence or its history entered the legacy model path.");
await SendAsync(vm, "不要打开检查车模型");
Require(cad.MutatingCalls == 0, "A negative model instruction reached a CAD action.");
Console.WriteLine("PASS ordinary chat remains intact and neither RAG history nor negation triggers CAD");

var designBeforeDimensionQuestion = llm.DesignRequests.Count;
llm.KnowledgeAnswer = "请按本轮相关规范资料核对 [E1]。";
var dimensionQuestion = await SendAsync(vm, "按 GB/T 3811-2008，上弦杆截面改成 60×60 是否符合要求？");
Require(dimensionQuestion.IsLocalKnowledgeReply && cad.MutatingCalls == 0 && cad.Updates.Count == 0 &&
    llm.DesignRequests.Count == designBeforeDimensionQuestion,
    "A dimension-bearing engineering question must stay in the evidence-backed RAG path and never dispatch CAD. " + dimensionQuestion.Text);
Console.WriteLine("PASS dimension-bearing engineering question stays in RAG and never dispatches CAD");

var upper = members.FindEnabledMemberById(EditableTrussMemberCatalogService.UpperChordMemberId)
    ?? throw new InvalidOperationException("The upper chord mapping is missing.");
var relativeParts = upper.PartMappings.Select(mapping =>
{
    // Use disposable fixture files with the same relative names as the existing model mapping.
    var legacyPath = mapping.PartFilePath;
    const string marker = "\\新模型\\";
    var start = legacyPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    var relative = string.IsNullOrWhiteSpace(mapping.RelativePartPath)
        ? start >= 0 ? legacyPath[(start + marker.Length)..] : throw new InvalidOperationException("No fixture relative path")
        : mapping.RelativePartPath;
    mapping.RelativePartPath = relative;
    mapping.PartFilePath = "";
    return relative;
}).ToArray();
Require(relativeParts.Length == 4, "Expected four upper chord mappings.");
var reds = new[] { @"桁架1\10-方管70×70×5×1280.SLDPRT", @"桁架2\20-方管70×70×5-1280.SLDPRT" };
foreach (var relative in relativeParts.Concat(reds).Append(workspaceOptions.AssemblyFileName))
    WriteFixture(relative);

var previousDesignCount = llm.DesignRequests.Count;
var knowledgeCount = llm.KnowledgeRequests.Count;
await SendAsync(vm, "打开当前模型");
Require(cad.Opened.SequenceEqual(new[] { Path.Combine(workspaceOptions.WorkingModelFolder, workspaceOptions.AssemblyFileName) }),
    "An explicit model-open request did not reach the existing workspace and CAD route. " + vm.ChatMessages.Last().Text);
Require(llm.KnowledgeRequests.Count == knowledgeCount && llm.DesignRequests.Count <= previousDesignCount + 1,
    "A model-open request was routed to knowledge chat.");

var cadCallsBeforeNegation = cad.MutatingCalls;
var codReadsBeforeNegation = cad.DimensionReadCalls;
var dimensionNegation = await SendAsync(vm, "不要把上弦杆截面改成 60×60");
Require(cad.MutatingCalls == cadCallsBeforeNegation && cad.DimensionReadCalls == codReadsBeforeNegation &&
    llm.KnowledgeRequests.Count == knowledgeCount && !dimensionNegation.Text.Contains("修改成功"),
    "A dimension-bearing negation must not dispatch CAD or claim success. " + dimensionNegation.Text);
Console.WriteLine("PASS dimension-bearing negation reads and writes no CAD dimensions on an open model");

const string sectionEdit = "把桁架上弦杆截面改成 60×60";
var pending = await SendAsync(vm, sectionEdit);
Require(pending.Text.Contains("确认") && cad.Updates.Count == 0, "The edit did not stop at confirmation. " + pending.Text);
await SendAsync(vm, "取消");
await SendAsync(vm, "确认修改");
Require(cad.Updates.Count == 0, "A cancelled edit was dispatched on a later confirmation.");
Console.WriteLine("PASS cancel clears the pending CAD plan and later confirmation cannot execute it");

var designCountBeforeEdit = llm.DesignRequests.Count;
pending = await SendAsync(vm, sectionEdit);
Require(pending.Text.Contains("确认") && cad.Updates.Count == 0, "The resubmitted edit did not stop at confirmation. " + pending.Text);
await SendAsync(vm, "确认修改");
Require(llm.KnowledgeRequests.Count == knowledgeCount && llm.DesignRequests.Count == designCountBeforeEdit,
    "The deterministic 60×60 edit went to an LLM.");
Require(cad.Updates.Count == 1 && cad.Updates[0].Count == 12, "The confirmed edit did not send one complete 12-item CAD plan.");
var plan = cad.Updates.Single();
Require(plan.Count(item => item.Value == "60") == 8, "Expected eight outer dimensions at 60 mm.");
Require(plan.Count(item => item.PartFilePath.EndsWith("-379.SLDPRT", StringComparison.OrdinalIgnoreCase) &&
    item.Value == "50" && item.DimensionName.Contains("@草图2@", StringComparison.Ordinal)) == 2,
    "The 379 inner profile did not retain the measured 5 mm wall.");
Require(plan.Count(item => item.Value == "1290" && item.ParameterName == "upper_chord.linked_red_tube_length") == 2,
    "The plan did not compensate both red tubes.");
Console.WriteLine("PASS model-open and confirmed 60x60 commands reach CAD sandbox fakes with all 12 dimensions");

var beforeSimulation = (llm.DesignRequests.Count, llm.KnowledgeRequests.Count, cad.MutatingCalls, cad.DimensionReadCalls);
var launch = await SendAsync(vm, "打开 ANSYS APDL");
Require(launcherStarts.SequenceEqual(new[] { simulation.LauncherPath }) &&
    launch.Text.Contains("启动器已启动") && !launch.Text.Contains("仿真完成"), "APDL launch route failed.");
var blockedStudy = await SendAsync(vm, "运行轻量化仿真");
Require(blockedStudy.Text.Contains("还没有配置") && !blockedStudy.Text.Contains("正在"), "Unconfigured study was not rejected.");
var baseline = await SendAsync(vm, "读取轻量化基线");
Require(baseline.Text.Contains("用户提供") && !baseline.Text.Contains("实测"), "No verified baseline must be distinguished from the reference.");
Require(beforeSimulation == (llm.DesignRequests.Count, llm.KnowledgeRequests.Count, cad.MutatingCalls, cad.DimensionReadCalls),
    "An explicit simulation command incorrectly reached the model or CAD.");
await SendAsync(vm, "不要打开 ANSYS APDL");
await SendAsync(vm, "怎么打开 ANSYS APDL？");
Require(launcherStarts.Count == 1 && cad.MutatingCalls == beforeSimulation.Item3,
    "An APDL question or negation launched an executable or reached CAD.");

foreach (var naturalLaunch in new[] { "打开仿真软件", "我看下仿真软件", "你先打开 apdl", "你可以打开 apdl 吗", "APDL" })
{
    var startCount = launcherStarts.Count;
    var requestCount = llm.DesignRequests.Count;
    var result = await SendAsync(vm, naturalLaunch);
    Require(launcherStarts.Count == startCount + 1 && llm.DesignRequests.Count == requestCount + 1 &&
        llm.LastOptions?.AllowApdlLauncherAction == true &&
        launcherStarts.LastOrDefault() == simulation.LauncherPath &&
        result.Text.Contains("启动器已启动") && !result.Text.Contains("仿真完成"),
        $"Natural-language APDL launch intent failed for '{naturalLaunch}'. {result.Text}");
}

var launchesAfterNaturalLanguage = launcherStarts.Count;
foreach (var nonLaunch in new[] { "APDL 是什么？", "不要打开 APDL", "帮我写 APDL 脚本", "怎么运行 APDL 求解？", "我只是问问你能不能打开 APDL" })
{
    await SendAsync(vm, nonLaunch);
}
Require(launcherStarts.Count == launchesAfterNaturalLanguage && cad.MutatingCalls == beforeSimulation.Item3,
    "An APDL information request, script request, solver question, or negation launched an executable or reached CAD.");

Require(ApdlLaunchIntentPolicy.IsCandidate("APDL") &&
    ApdlLaunchIntentPolicy.IsCandidate("我看下仿真软件") &&
    !ApdlLaunchIntentPolicy.IsCandidate("APDL 是什么？") &&
    !ApdlLaunchIntentPolicy.IsCandidate("我只是问问你能不能打开 APDL") &&
    !ApdlLaunchIntentPolicy.IsCandidate("帮我写 APDL 脚本"),
    "The local APDL intent boundary accepted an information or script request.");

llm.ForceMixedApdlAction = true;
var mixedIntent = await SendAsync(vm, "打开仿真软件");
Require(launcherStarts.Count == launchesAfterNaturalLanguage && mixedIntent.Text.Contains("其他操作"),
    "A mixed APDL launcher and CAD action was not rejected as a whole.");
llm.ForceMixedApdlAction = false;

llm.ForceApdlAction = true;
await SendAsync(vm, "帮我写 APDL 脚本");
foreach (var negative in new[] { "不打开APDL", "不用启动APDL", "不要帮我打开APDL", "别给我打开APDL", "不需要启动ANSYS" })
{
    Require(!ApdlLaunchIntentPolicy.IsCandidate(negative), "A negated request became a launcher candidate: " + negative);
    await SendAsync(vm, negative);
}
Require(launcherStarts.Count == launchesAfterNaturalLanguage,
    "A model-proposed launcher action bypassed the local user-intent safety check.");
llm.ForceApdlAction = false;

foreach (var pendingInput in new[] { "APDL", "我看下仿真软件" })
{
    vm.Parameters.Clear();
    vm.Parameters.Add(new ParameterItem { Name = "pending-test", MappingStatus = "OK", SolidWorksTargets = [new SolidWorksTarget()] });
    typeof(MainViewModel).GetProperty(nameof(MainViewModel.DesignStatus))!.SetValue(vm, "待确认");
    var beforePendingLaunch = launcherStarts.Count;
    var beforePendingCad = cad.MutatingCalls;
    var pendingLaunch = await SendAsync(vm, pendingInput);
    Require(launcherStarts.Count == beforePendingLaunch + 1 && cad.MutatingCalls == beforePendingCad &&
        pendingLaunch.Text.Contains("启动器已启动"),
        "The real primary-action entry in pending-confirmation state went to CAD instead of APDL.");
}
vm.Parameters.Clear();
typeof(MainViewModel).GetProperty(nameof(MainViewModel.DesignStatus))!.SetValue(vm, "待输入");

var productionLlm = new LlmService(settings.Llm, dictionary);
var normalizeLlmReply = typeof(LlmService).GetMethod("NormalizeModelResponseWithDiagnostics",
    BindingFlags.Instance | BindingFlags.NonPublic)!;
LlmParseResult NormalizeApdlReply(string json, bool allow) =>
    JsonSerializer.Deserialize<LlmParseResult>((string)normalizeLlmReply.Invoke(productionLlm,
        new object?[] { json, "我看下仿真软件", new LlmChatRequestOptions(AllowApdlLauncherAction: allow) })!)!;
const string launchJson = """{"reply":"准备启动。","actions":["open_apdl_launcher"],"commands":["open_apdl_launcher"],"parameters":[],"needConfirmation":false,"questions":[]}""";
Require(NormalizeApdlReply(launchJson, true).Actions.SequenceEqual(new[] { "open_apdl_launcher" }) &&
    NormalizeApdlReply(launchJson, false).Actions.Count == 0,
    "The production LLM parser did not preserve the explicitly allowed APDL action or exposed it to ordinary CAD requests.");
const string mixedJson = """{"reply":"已打开且修改。","actions":["update_solidworks_dimensions"],"commands":["open_apdl_launcher"],"parameters":[],"needConfirmation":false,"questions":[]}""";
var rejectedMixed = NormalizeApdlReply(mixedJson, true);
Require(rejectedMixed.Actions.Count == 0 && rejectedMixed.NeedConfirmation &&
    !rejectedMixed.Reply.Contains("已打开"), "The production parser hid a mixed action and allowed the launcher.");
var buildPrompt = typeof(LlmService).GetMethod("BuildUnifiedConversationSystemPrompt", BindingFlags.Static | BindingFlags.NonPublic)!;
var launchPrompt = (string)buildPrompt.Invoke(null, new object?[] { new LlmChatRequestOptions(AllowApdlLauncherAction: true) })!;
Require(launchPrompt.Contains("open_apdl_launcher") && launchPrompt.Contains("我看下仿真软件") &&
    launchPrompt.Contains("does not connect to a solver"), "The real model request did not expose the bounded launcher capability.");
Console.WriteLine("PASS APDL launcher uses bounded natural-language intent and keeps information, script, negation, and solver questions action-free");
Console.WriteLine("PASS production LLM prompt and response parser accept only the scoped APDL action and reject mixed actions");

if (args.Contains("--live-apdl", StringComparer.Ordinal))
{
    Require(configuredLlmOptions.Mode == "Cloud" && configuredLlmOptions.Model == "gpt-6.1-sol",
        "Live APDL intent checks require the current configured cloud model.");
    var liveStarts = new List<string>();
    var realLlm = new LlmService(configuredLlmOptions, dictionary);
    var liveVm = new MainViewModel(realLlm, dictionary, new ApdlReplacementService(), service, dispatcher, workspace,
        configuredLlmOptions, chord, members, scans, recommendation, parser, simulation, liveStarts.Add);
    foreach (var liveInput in new[] { "APDL", "打开仿真软件", "我看下仿真软件", "你先打开apdl" })
    {
        var beforeStarts = liveStarts.Count;
        var beforeCadCalls = cad.MutatingCalls;
        var answer = await SendAsync(liveVm, liveInput, TimeSpan.FromSeconds(240));
        Require(liveStarts.Count == beforeStarts + 1 && liveStarts.Last() == simulation.LauncherPath &&
            answer.Text.Contains("启动器已启动") && cad.MutatingCalls == beforeCadCalls,
            "Live GPT chat did not select only the launcher intent for: " + liveInput);
        Console.WriteLine("PASS LIVE GPT-6.1 chat intent: " + liveInput + "; configured launcher delegate recorded; no CAD or solver started");
    }
}

void WriteFixture(string relative)
{
    Require(!Path.IsPathRooted(relative), "A fake model path was absolute.");
    var path = Path.GetFullPath(Path.Combine(workspaceOptions.InitialModelFolder, relative));
    Require(path.StartsWith(workspaceOptions.InitialModelFolder + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase), "A fake model path left the fixture directory.");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "test fixture, not a CAD model");
}

static async Task<ChatMessage> SendAsync(MainViewModel viewModel, string text, TimeSpan? timeout = null)
{
    viewModel.UserInput = text;
    var command = viewModel.PrimaryActionCommand;
    Require(command.CanExecute(null), "The chat send command is disabled.");
    var messagesBefore = viewModel.ChatMessages.Count;
    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void OnCanExecuteChanged(object? sender, EventArgs args)
    {
        if (!command.IsExecuting) completed.TrySetResult();
    }
    command.CanExecuteChanged += OnCanExecuteChanged;
    try
    {
        command.Execute(null);
        await completed.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(12));
    }
    finally
    {
        command.CanExecuteChanged -= OnCanExecuteChanged;
    }
    Require(viewModel.ChatMessages.Count > messagesBefore && !viewModel.ChatMessages.Last().IsUser,
        "The chat command did not produce a reply.");
    return viewModel.ChatMessages.Last();
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

public class FakeCad : DispatchProxy
{
    public int MutatingCalls { get; private set; }
    public int DimensionReadCalls { get; private set; }
    public List<string> Opened { get; } = [];
    public List<IReadOnlyList<SolidWorksDimensionUpdateRequest>> Updates { get; } = [];

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case nameof(ISolidWorksService.OpenAssemblyAsync):
                MutatingCalls++;
                var path = (string)args![0]!;
                if (!File.Exists(path)) throw new FileNotFoundException("Fixture was not copied.", path);
                Opened.Add(path);
                return Task.FromResult(true);
            case nameof(ISolidWorksService.CloseDocumentsUnderFolderAsync):
                MutatingCalls++;
                return Task.FromResult(true);
            case nameof(ISolidWorksService.IsSolidWorksRunning):
            case nameof(ISolidWorksService.HasActiveDocument):
                return Opened.Count > 0;
            case nameof(ISolidWorksService.GetActiveDocumentPath):
                return Opened.LastOrDefault() ?? string.Empty;
            case nameof(ISolidWorksService.TryGetActiveDocumentInfoWithoutStartingSolidWorks):
                args![0] = Opened.LastOrDefault() ?? string.Empty;
                return Opened.Count > 0;
            case nameof(ISolidWorksService.ReadOpenDimensionAsync):
                DimensionReadCalls++;
                var part = (string)args![0]!;
                var dimension = (string)args[1]!;
                var redTube = Path.GetFileName(part).EndsWith("1280.SLDPRT", StringComparison.OrdinalIgnoreCase);
                var is379 = part.EndsWith("-379.SLDPRT", StringComparison.OrdinalIgnoreCase);
                return Task.FromResult(new SolidWorksDimensionProbeResult
                {
                    Found = !redTube || dimension.StartsWith("D1@拉伸-薄壁1@", StringComparison.Ordinal),
                    PartFilePath = part,
                    ResolvedDimensionName = dimension,
                    RequestedDimensionName = dimension,
                    Configuration = "Default",
                    ReferencedConfiguration = "Default",
                    ComponentInstances = "fixture",
                    CurrentValue = redTube ? 1280 : is379 && dimension.Contains("@草图2@", StringComparison.Ordinal) ? 60 : 70
                });
            case nameof(ISolidWorksService.UpdateDimensionsAsync):
                MutatingCalls++;
                Updates.Add(((IReadOnlyList<SolidWorksDimensionUpdateRequest>)args![0]!).ToArray());
                return Task.FromResult(true);
            default:
                throw new InvalidOperationException("Unexpected CAD call: " + method.Name);
        }
    }
}

sealed class FakeLlm : ILlmService
{
    public event Action<string>? DiagnosticLogEmitted { add { } remove { } }
    public List<(string Input, IReadOnlyList<ConversationMessage> History)> DesignRequests { get; } = [];
    public List<string> KnowledgeRequests { get; } = [];
    public string KnowledgeAnswer { get; set; } = "请参考安全系数资料 [E1]。";
    public bool ForceApdlAction { get; set; }
    public bool ForceMixedApdlAction { get; set; }
    public LlmChatRequestOptions? LastOptions { get; private set; }

    public Task<string> ParseDesignRequirementAsync(string userInput, IReadOnlyList<ConversationMessage>? history = null,
        LlmChatRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        DesignRequests.Add((userInput, history?.ToArray() ?? []));
        LastOptions = options;
        var answer = ForceMixedApdlAction
            ? """{"reply":"正在打开并修改 CAD。","actions":["open_apdl_launcher","update_solidworks_dimensions"],"commands":["open_apdl_launcher","update_solidworks_dimensions"],"parameters":[],"needConfirmation":false,"questions":[]}"""
            : ForceApdlAction || (options?.AllowApdlLauncherAction == true && ApdlLaunchIntentPolicy.IsCandidate(userInput))
            ? """{"reply":"正在打开 ANSYS APDL。","actions":["open_apdl_launcher"],"commands":["open_apdl_launcher"],"parameters":[],"needConfirmation":false,"questions":[]}"""
            : userInput == "打开当前模型" && options?.ReplyOnlyMode != true
            ? """{"reply":"","actions":["open_working_model"],"commands":[],"parameters":[],"needConfirmation":false,"questions":[]}"""
            : """{"reply":"这是普通对话答复。","actions":[],"commands":[],"parameters":[],"needConfirmation":false,"questions":[]}""";
        return Task.FromResult(answer);
    }

    public Task<string> AnswerFromLocalKnowledgeAsync(string evidencePrompt, CancellationToken cancellationToken = default)
    {
        KnowledgeDisclosureGuard.Validate(evidencePrompt);
        KnowledgeRequests.Add(evidencePrompt);
        return Task.FromResult(KnowledgeAnswer);
    }

    public Task<LlmConnectionTestResult> TestConnectionAsync(string userInput, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("The test must not call a remote model.");
}
