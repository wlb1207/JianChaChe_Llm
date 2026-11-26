using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services;
using LlmWpfPrototype.Services.Design;
using LlmWpfPrototype.Services.Project;
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

// Plan B regression checks: runtime capability awareness, unified memory, side-question preservation,
// unified lower-chord / lower-truss confirmation, polite read-only queries, and LLM-powered greeting.
var beforeGreetingRequests = llm.DesignRequests.Count;
var greetingReply = await SendAsync(vm, "你好");
Require(llm.DesignRequests.Count == beforeGreetingRequests + 1 &&
    llm.LastOptions?.ReplyOnlyMode == true &&
    (llm.LastOptions?.RuntimeContextSummary?.Contains("本地 ANSYS APDL 启动器：已配置且本机可用") ?? false) &&
    !string.IsNullOrWhiteSpace(greetingReply.Text),
    "Greeting must call the LLM with live runtime capabilities instead of returning a hardcoded local template. " + greetingReply.Text);

var knowledgeBeforeApdlQuestions = llm.KnowledgeRequests.Count;
var launchesBeforeApdlQuestions = launcherStarts.Count;
await SendAsync(vm, "仿真软件是什么呢？");
var apdlQuestionOptions = llm.LastOptions ?? throw new InvalidOperationException("Missing LLM options for simulation question.");
Require(llm.KnowledgeRequests.Count == knowledgeBeforeApdlQuestions &&
    launcherStarts.Count == launchesBeforeApdlQuestions &&
    apdlQuestionOptions.ReplyOnlyMode &&
    !apdlQuestionOptions.AllowApdlLauncherAction &&
    (apdlQuestionOptions.RuntimeContextSummary?.Contains("本地 ANSYS APDL 启动器：已配置且本机可用") ?? false) &&
    !(apdlQuestionOptions.RuntimeContextSummary?.Contains(":\\") ?? false),
    "Simulation question did not carry privacy-safe live APDL capability context.");

var replyOnlyPrompt = (string)buildPrompt.Invoke(null, new object?[] { apdlQuestionOptions })!;
Require(replyOnlyPrompt.Contains("本地 ANSYS APDL 启动器：已配置且本机可用") &&
    replyOnlyPrompt.Contains("不要输出 JSON") &&
    !replyOnlyPrompt.Contains("reaction-frame") &&
    !replyOnlyPrompt.Contains("unless the program has explicitly connected a real solver"),
    "Reply-only system prompt still contains stale contradictions or misses live runtime context.");

var normalizeMethod = typeof(LlmService).GetMethod("NormalizeModelResponseWithDiagnostics",
    BindingFlags.Instance | BindingFlags.NonPublic)!;
var plainReplyJson = (string)normalizeMethod.Invoke(productionLlm,
    new object?[] { "本程序已配置本地 ANSYS APDL 启动器，可以直接说打开 APDL。", "这里面不是有ansys apdl吗？", apdlQuestionOptions })!;
var plainParsed = JsonSerializer.Deserialize<LlmParseResult>(plainReplyJson)!;
Require(plainParsed.Actions.Count == 0 && plainParsed.Reply.Contains("已配置本地 ANSYS APDL 启动器"),
    "Plain-text reply-only response was not wrapped cleanly.");

var launchesBeforeDirectApdl = launcherStarts.Count;
await SendAsync(vm, "打开apdl");
Require(launcherStarts.Count == launchesBeforeDirectApdl + 1, "Direct '打开apdl' did not start launcher.");
await SendAsync(vm, "刚才启动器打开了吗？");
var historyAfterApdl = llm.DesignRequests.Last().History;
Require(historyAfterApdl.Any(item => item.Role == "user" && item.Content == "打开apdl") &&
    historyAfterApdl.Any(item => item.Role == "assistant" && item.Content.Contains("启动器已启动")) &&
    (llm.LastOptions?.RuntimeContextSummary?.Contains("本会话 APDL 启动器记录：已启动") ?? false) &&
    (llm.LastOptions?.RuntimeContextSummary?.Contains("open_apdl_launcher") ?? false),
    "Direct '打开apdl' action was lost from unified conversation memory or runtime action audit.");

// Ensure unified history remains completely compliant with KnowledgeDisclosureGuard.RejectKnowledgeOnLegacyPath.
var rejectLegacy = typeof(KnowledgeDisclosureGuard).GetMethod("RejectKnowledgeOnLegacyPath",
    BindingFlags.Static | BindingFlags.NonPublic)!;
rejectLegacy.Invoke(null, new object?[] { historyAfterApdl, "刚才启动器打开了吗？" });

// Polite editable-parameter query must NOT be blocked by '?' or '吗'.
var politeQueryReply = await SendAsync(vm, "能帮我查一下可编辑参数吗？");
Require(llm.LastOptions?.ReplyOnlyMode == false && politeQueryReply.Text.Contains("当前可编辑项"),
    "Polite editable-parameter query was blocked by question guard. " + politeQueryReply.Text);

// Lower chord & lower truss MUST now require explicit confirmation, and side questions must preserve the pending plan.
var updatesBeforeLowerChord = cad.Updates.Count;
var lowerChordPending = await SendAsync(vm, "把下弦杆截面改成 80x80x6");
Require(lowerChordPending.Text.Contains("待确认修改") && cad.Updates.Count == updatesBeforeLowerChord &&
    vm.HasPendingCadConfirmation && vm.ConfirmBusinessCommand.CanExecute(null),
    "Lower chord edit bypassed confirmation gate or left ConfirmBusinessCommand disabled. " + lowerChordPending.Text);

await SendAsync(vm, "这里面不是有ansys apdl吗？");
Require((llm.LastOptions?.RuntimeContextSummary?.Contains("当前有待确认的尺寸修改计划") ?? false) &&
    cad.Updates.Count == updatesBeforeLowerChord,
    "Side question wiped the pending lower-chord confirmation plan.");
await SendAsync(vm, "取消");
await SendAsync(vm, "确认修改");
Require(cad.Updates.Count == updatesBeforeLowerChord, "Cancelled lower-chord edit still executed on confirmation.");

var lowerTrussPending = await SendAsync(vm, "把下桁架改成 60x60x6");
Require(lowerTrussPending.Text.Contains("待确认修改") && cad.Updates.Count == updatesBeforeLowerChord &&
    vm.ConfirmBusinessCommand.CanExecute(null),
    "Lower truss edit bypassed confirmation gate or left ConfirmBusinessCommand disabled. " + lowerTrussPending.Text);
await SendAsync(vm, "取消");

var invalidLowerThickness = await SendAsync(vm, "把下弦杆壁厚改成 15");
Require(invalidLowerThickness.Text.Contains("超出允许范围") && cad.Updates.Count == updatesBeforeLowerChord,
    "Invalid lower-chord wall thickness was not rejected before confirmation. " + invalidLowerThickness.Text);

Console.WriteLine("PASS Plan B runtime context injection, unified memory across APDL/CAD/RAG, polite parameter query, and unified lower-chord/lower-truss confirmation");

// =========================================================================
// Continuous Intelligent Design Upgrade Checks:
// 1) Deterministic Q355B Section Mechanics & 3-Tier Safety Domain
// 2) Live Structure Dimension Snapshot & Engineering Advisor Context
// 3) Multi-Project / Multi-Conversation Persistence Across Simulated Restarts
// =========================================================================
var baseCalc = SectionMechanicsCalculator.CalculateRectangularTube(70m, 70m, 5m);
Require(baseCalc.IsGeometryValid && baseCalc.AreaMm2 == 1300m && baseCalc.LineMassKgPerM == 10.205m &&
    Math.Round(baseCalc.IxMm4 / 100000m, 2) == 9.21m &&
    baseCalc.LinkedUpperRedTubeLengthMm == 1280m &&
    baseCalc.EffectiveHorizontalWalkwayWidthMm == 1270m &&
    baseCalc.DomainTier == SectionSafetyDomainTier.GeometricExecutable,
    "Baseline 70x70x5 mechanics calculation mismatch.");

var optCalc = SectionMechanicsCalculator.CalculateRectangularTube(80m, 80m, 4m);
Require(optCalc.IsGeometryValid && optCalc.MassDeltaPercentVsBase == -6.5m && optCalc.IxDeltaPercentVsBase == 27.5m &&
    optCalc.LinkedUpperRedTubeLengthMm == 1270m && optCalc.EffectiveHorizontalWalkwayWidthMm == 1260m &&
    optCalc.DomainTier == SectionSafetyDomainTier.GeometricExecutable,
    "Optimized 80x80x4 stiffening-while-lightening calculation mismatch.");

var thinCalc = SectionMechanicsCalculator.CalculateRectangularTube(70m, 70m, 4m);
Require(thinCalc.MassDeltaPercentVsBase == -18.8m && thinCalc.IxDeltaPercentVsBase == -16.4m,
    "70x70x4 wall-thinning calculation mismatch.");

var lightSmallCalc = SectionMechanicsCalculator.CalculateRectangularTube(50m, 50m, 4m);
Require(lightSmallCalc.IsGeometryValid && lightSmallCalc.DomainTier == SectionSafetyDomainTier.GeometricExecutable &&
    lightSmallCalc.EngineeringNote.Contains("仿真", StringComparison.Ordinal),
    "50x50x4 lightweighting candidate should remain open to simulation-driven verification.");

var degenerateCalc = SectionMechanicsCalculator.CalculateRectangularTube(40m, 40m, 20m);
Require(!degenerateCalc.IsGeometryValid && degenerateCalc.DomainTier == SectionSafetyDomainTier.InvalidGeometry,
    "40x40x20 zero-cavity tube must be rejected as InvalidGeometry.");
Console.WriteLine("PASS Q355B section mechanics calculator & simulation-driven evaluation");

// In vm above, upper chord was already confirmed at 60x60 (V1)! Verify natural read-only structure queries & advisor math:
var dimQueryCallsBefore = cad.MutatingCalls;
await SendAsync(vm, "现在的尺寸是多少？目前下桁架的截面尺寸是多少？");
var dimSummary = llm.LastOptions?.RuntimeContextSummary ?? string.Empty;
Require(llm.LastOptions?.ReplyOnlyMode == true && cad.MutatingCalls == dimQueryCallsBefore,
    "Read-only structure dimension question must stay in ReplyOnlyMode with zero CAD mutations.");
Require(dimSummary.Contains("【当前项目结构截面尺寸状态", StringComparison.Ordinal) &&
    dimSummary.Contains("上弦杆截面尺寸：外宽 60 mm × 外高 60 mm × 壁厚 5 mm", StringComparison.Ordinal) &&
    dimSummary.Contains("下弦杆截面尺寸：外宽 70 mm × 外高 70 mm × 壁厚 5 mm", StringComparison.Ordinal) &&
    dimSummary.Contains("上弦横向联动红管长度 = 1290 mm", StringComparison.Ordinal),
    "Structure dimension query did not inject live unified upper/lower chord snapshot: " + dimSummary);
rejectLegacy.Invoke(null, new object?[] { llm.DesignRequests.Last().History, dimSummary });

await SendAsync(vm, "上弦杆的取值范围是多少？如果改成 80x80x4 怎么样？");
var advisorSummary = llm.LastOptions?.RuntimeContextSummary ?? string.Empty;
Require(advisorSummary.Contains("有限元仿真", StringComparison.Ordinal) &&
    !advisorSummary.Contains("减重 -6.5%", StringComparison.Ordinal) &&
    advisorSummary.Contains("80×80×4", StringComparison.Ordinal) &&
    advisorSummary.Contains("1270 mm", StringComparison.Ordinal),
    "Engineering advisor guidance did not keep pre-simulation advice free of premature weight-reduction percentages: " + advisorSummary);
rejectLegacy.Invoke(null, new object?[] { llm.DesignRequests.Last().History, advisorSummary });
Console.WriteLine("PASS live structure dimension snapshot & engineering advisor guidance pass disclosure guard");

// Cross-restart multi-project & multi-conversation persistence test
var persistStoreDir = Path.Combine(workspaceRoot, "persisted_projects_store");
var workspace1 = new ModelWorkspaceManager(workspaceOptions, service);
var dispatcher1 = new CommandDispatcher(service, workspace1, members, parser, scans, recommendation,
    new TrussParameterSetupStateService(service, scans, members));
var store1 = new DesignProjectStoreService(persistStoreDir, workspaceOptions);
var vm1 = new MainViewModel(llm, dictionary, new ApdlReplacementService(), service, dispatcher1, workspace1,
    settings.Llm, chord, members, scans, recommendation, parser, simulation, launcherStarts.Add, store1);

Require(vm1.CurrentVersionTag == "V0" && vm1.FilteredProjects.Count == 1, "Fresh project store must start at V0 with 1 default project.");
await SendAsync(vm1, "打开当前模型");
await SendAsync(vm1, "把桁架上弦杆截面改成 60×60");
await SendAsync(vm1, "确认修改");
Require(vm1.CurrentVersionTag == "V1" && vm1.CurrentUpperChordHud.Contains("60×60×5", StringComparison.Ordinal),
    "Confirmed CAD edit did not advance project version to V1 (60x60x5). " + vm1.CurrentUpperChordHud);

// Create a 2nd follow-up conversation inside the same project; verify it shares V1 snapshot
await vm1.CreateNewConversationCommand.ExecuteAsync();
Require(vm1.ActiveProject?.Conversations.Count == 2, "Expected 2 conversations in default project.");
await SendAsync(vm1, "跟进一下，目前上弦杆尺寸是多少？");
Require((llm.LastOptions?.RuntimeContextSummary ?? string.Empty).Contains("上弦杆截面尺寸：外宽 60 mm × 外高 60 mm × 壁厚 5 mm", StringComparison.Ordinal),
    "Second conversation in same project did not inherit V1 structural state.");

// Fork a derived project from V1; verify isolated workspace path & inherited V1 state
var defaultProjVm = vm1.FilteredProjects.Single(p => p.Id == "proj-default");
await vm1.ForkProjectCommand.ExecuteAsync(defaultProjVm);
var forkedProjectId = vm1.ActiveProject?.Id ?? throw new InvalidOperationException("Forked project missing.");
Require(forkedProjectId != "proj-default" && vm1.CurrentVersionTag == "V1" &&
    vm1.CurrentUpperChordHud.Contains("60×60×5", StringComparison.Ordinal) &&
    !string.Equals(workspace1.WorkingModelFolder, workspaceOptions.WorkingModelFolder, StringComparison.OrdinalIgnoreCase),
    "Forked project did not inherit V1 state or switch to an isolated workspace directory.");

// Queue rollback to V0 checkpoint and verify confirmation gate is enforced, then cancel
var v0Checkpoint = vm1.ActiveVersionHistory.Last(cp => cp.VersionNumber == 0);
var updatesBeforeRollback = cad.Updates.Count;
await vm1.RollbackToVersionCommand.ExecuteAsync(v0Checkpoint);
Require(vm1.ChatMessages.Last().Text.Contains("待确认修改", StringComparison.Ordinal) && cad.Updates.Count == updatesBeforeRollback,
    "Version rollback bypassed confirmation gate.");
await SendAsync(vm1, "取消");

// Export thesis experiment Markdown report
var reportDir = Path.Combine(workspaceRoot, "exported_reports");
var reportPath = store1.ExportProjectMarkdownReport("proj-default", reportDir);
Require(File.Exists(reportPath) && File.ReadAllText(reportPath, Encoding.UTF8).Contains("60×60×5", StringComparison.Ordinal),
    "Exported Markdown report missing V1 60x60x5 record.");

// Save unsent draft and simulate full process exit + cold restart on disk!
vm1.UserInput = "重启后继续优化下弦杆 80x80x4";
vm1.FlushActiveSessionOnExit();

var workspace2 = new ModelWorkspaceManager(workspaceOptions, service);
var dispatcher2 = new CommandDispatcher(service, workspace2, members, parser, scans, recommendation,
    new TrussParameterSetupStateService(service, scans, members));
var store2 = new DesignProjectStoreService(persistStoreDir, workspaceOptions);
var vm2 = new MainViewModel(llm, dictionary, new ApdlReplacementService(), service, dispatcher2, workspace2,
    settings.Llm, chord, members, scans, recommendation, parser, simulation, launcherStarts.Add, store2);

Require(vm2.ActiveProject?.Id == forkedProjectId &&
    vm2.CurrentVersionTag == "V1" &&
    vm2.CurrentUpperChordHud.Contains("60×60×5", StringComparison.Ordinal) &&
    vm2.UserInput == "重启后继续优化下弦杆 80x80x4" &&
    vm2.FilteredProjects.Count == 2,
    "Cold-restarted ViewModel did not restore active project, V1 snapshot, draft input, or project list.");

// Switch back to proj-default in vm2 and verify its 2 conversations and history survived restart
var restoredDefaultVm = vm2.FilteredProjects.Single(p => p.Id == "proj-default");
await vm2.SelectProjectCommand.ExecuteAsync(restoredDefaultVm);
Require(vm2.ActiveProject?.Id == "proj-default" &&
    vm2.ActiveProject.Conversations.Count == 2 &&
    vm2.ChatMessages.Any(m => m.Text.Contains("跟进一下", StringComparison.Ordinal)) &&
    string.Equals(workspace2.WorkingModelFolder, workspaceOptions.WorkingModelFolder, StringComparison.OrdinalIgnoreCase),
    "Switching back to proj-default after restart did not restore conversations or default workspace folder.");

// Verify unique project numbering after deleting earlier projects (regression for duplicate "轻量化方案 2")
await vm2.DeleteProjectCommand.ExecuteAsync(restoredDefaultVm);
Require(vm2.FilteredProjects.Count == 1 && vm2.FilteredProjects[0].RawName == "轻量化方案 1",
    "Expected single remaining project named 轻量化方案 1, got: " + string.Join(", ", vm2.FilteredProjects.Select(p => p.RawName)));
await vm2.CreateProjectCommand.ExecuteAsync();
Require(vm2.FilteredProjects.Count == 2 && vm2.ActiveProject?.Name == "轻量化方案 2",
    "Expected second project named 轻量化方案 2, got: " + string.Join(", ", vm2.FilteredProjects.Select(p => p.RawName)));
var scheme1Vm = vm2.FilteredProjects.Single(p => p.RawName == "轻量化方案 1");
await vm2.DeleteProjectCommand.ExecuteAsync(scheme1Vm);
// Now only "轻量化方案 2" remains (Count == 1); creating another MUST produce "轻量化方案 3", NOT a duplicate "轻量化方案 2"!
await vm2.CreateProjectCommand.ExecuteAsync();
Require(vm2.FilteredProjects.Count == 2 &&
    vm2.FilteredProjects.Select(p => p.RawName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 &&
    vm2.ActiveProject?.Name == "轻量化方案 3",
    "Creating a project after deleting an older one caused duplicate numbering: " + string.Join(", ", vm2.FilteredProjects.Select(p => p.RawName)));

// Verify deleting ALL projects including the very last one enters Option 1A clean empty state (0 messages, HasActiveProject == false)
foreach (var projItem in vm2.FilteredProjects.ToArray())
{
    await vm2.DeleteProjectCommand.ExecuteAsync(projItem);
}
Require(vm2.FilteredProjects.Count == 0 && vm2.ActiveProject is null && vm2.HasZeroProjects && !vm2.HasActiveProject && vm2.ChatMessages.Count == 0,
    "Deleting the last remaining project did not enter clean Option 1A empty state.");

var workspace3 = new ModelWorkspaceManager(workspaceOptions, service);
var dispatcher3 = new CommandDispatcher(service, workspace3, members, parser, scans, recommendation,
    new TrussParameterSetupStateService(service, scans, members));
var store3 = new DesignProjectStoreService(persistStoreDir, workspaceOptions);
var vm3 = new MainViewModel(llm, dictionary, new ApdlReplacementService(), service, dispatcher3, workspace3,
    settings.Llm, chord, members, scans, recommendation, parser, simulation, launcherStarts.Add, store3);
Require(vm3.FilteredProjects.Count == 0 && vm3.ActiveProject is null && !vm3.HasActiveProject && vm3.ChatMessages.Count == 0,
    "Cold restart unexpectedly resurrected a deleted project or fake chat bubble.");

// Creating a new project from Option 1A empty state cleanly creates "轻量化方案 1" with welcome message (no hardcoded 60~90)
await vm3.CreateProjectCommand.ExecuteAsync();
Require(vm3.FilteredProjects.Count == 1 && vm3.ActiveProject?.Name == "轻量化方案 1" && vm3.HasActiveProject &&
    vm3.ChatMessages.Count == 1 && !vm3.ChatMessages[0].Text.Contains("60~90", StringComparison.Ordinal),
    "Creating project from Option 1A empty state failed: " + vm3.ActiveProject?.Name);

// Verify 4-state LLM status machine notifies both text AND dot color flags (regression for yellow dot staying stuck after online)
var notifiedProps = new List<string>();
vm3.PropertyChanged += (_, e) => { if (!string.IsNullOrEmpty(e.PropertyName)) notifiedProps.Add(e.PropertyName); };
llm.TestConnectionHandler = () => new LlmConnectionTestResult(
    true, "https://local.test", "gpt-test", true, 200, 120,
    null, "models_probe", "gpt-test", 0, 0, 0, "ok", "pong", "pong", null, null, null, null);
await vm3.TestLlmConnectionCommand.ExecuteAsync();
Require(vm3.GptStatusSummary.Contains("在线", StringComparison.Ordinal) &&
    vm3.IsLlmStatusOnline && !vm3.IsLlmStatusConnecting && !vm3.IsLlmStatusError &&
    notifiedProps.Contains(nameof(MainViewModel.IsLlmStatusOnline)),
    "TestLlmConnectionCommand updated text without flipping IsLlmStatusOnline green-dot flag.");

llm.TestConnectionHandler = () => new LlmConnectionTestResult(
    false, "https://local.test", "gpt-test", true, null, 50,
    null, null, null, null, null, null, null, "", "", null, "TimeoutException", "连接超时", null);
await vm3.TestLlmConnectionCommand.ExecuteAsync();
Require(vm3.IsLlmStatusError && !vm3.IsLlmStatusOnline && vm3.GptStatusSummary.Contains("异常", StringComparison.Ordinal),
    "Failed connection test did not transition to error red-dot state.");
llm.TestConnectionHandler = null;
Console.WriteLine("PASS 4-state LLM connectivity status machine, green/red dot synchronization, and reconnect probe");
Console.WriteLine("PASS multi-project lazy/forked workspaces, unique numbering, zero-project deletion, auto-creation, report export, and cold-restart persistence");

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
            : userInput == "能帮我查一下可编辑参数吗？" && options?.ReplyOnlyMode != true
            ? """{"reply":"","actions":["query_editable_parameters"],"commands":["query_editable_parameters"],"parameters":[],"needConfirmation":false,"questions":[]}"""
            : """{"reply":"这是普通对话答复。","actions":[],"commands":[],"parameters":[],"needConfirmation":false,"questions":[]}""";
        return Task.FromResult(answer);
    }

    public Task<string> AnswerFromLocalKnowledgeAsync(string evidencePrompt, CancellationToken cancellationToken = default)
    {
        KnowledgeDisclosureGuard.Validate(evidencePrompt);
        KnowledgeRequests.Add(evidencePrompt);
        return Task.FromResult(KnowledgeAnswer);
    }

    public Func<LlmConnectionTestResult>? TestConnectionHandler { get; set; }

    public Task<LlmConnectionTestResult> TestConnectionAsync(string userInput, CancellationToken cancellationToken = default)
        => TestConnectionHandler is not null
            ? Task.FromResult(TestConnectionHandler())
            : throw new InvalidOperationException("The test must not call a remote model.");
}
