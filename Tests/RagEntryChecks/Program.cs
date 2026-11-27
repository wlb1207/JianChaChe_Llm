using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Documents;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Presentation;
using LlmWpfPrototype.Services;
using LlmWpfPrototype.Services.Chat;
using LlmWpfPrototype.Services.Design;
using LlmWpfPrototype.Services.Project;
using LlmWpfPrototype.Services.Rag;
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
    var relative = !string.IsNullOrWhiteSpace(mapping.RelativePartPath)
        ? mapping.RelativePartPath
        : start >= 0
            ? legacyPath[(start + marker.Length)..]
            : !string.IsNullOrWhiteSpace(legacyPath) && !Path.IsPathRooted(legacyPath)
                ? legacyPath
                : throw new InvalidOperationException("No fixture relative path");
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

// --- Per-conversation log isolation, persistence, and SaveFileDialog + clipboard .txt export checks ---
Require(vm3.LogEntries.Count > 0, "Expected TestLlmConnectionCommand logs in vm3 current conversation.");
var conv1Id = vm3.ActiveProject!.ActiveConversationId;
var conv1LogCount = vm3.LogEntries.Count;
Require(vm3.Logs.Contains("[LLM Test]", StringComparison.Ordinal), "Expected LLM test logs in conversation 1.");

// Test ExportLogsCommand cancel path (must not write or touch clipboard)
string? copiedByExport = null;
vm3.ClipboardWriterOverride = text => { copiedByExport = text; return Task.FromResult(true); };
vm3.SelectLogExportPathHandler = (_, _) => null;
await vm3.ExportLogsCommand.ExecuteAsync();
Require(copiedByExport is null && vm3.StatusMessage.Contains("已取消导出日志", StringComparison.Ordinal),
    "Cancelling log export unexpectedly wrote clipboard or wrong status: " + vm3.StatusMessage);

// Test ExportLogsCommand save + sync clipboard copy path
var exportedLogDir = Path.Combine(workspaceRoot, "exported_logs");
string? capturedSuggestedName = null;
string? exportedTxtPath = null;
vm3.SelectLogExportPathHandler = (suggestedName, _) =>
{
    capturedSuggestedName = suggestedName;
    exportedTxtPath = Path.Combine(exportedLogDir, suggestedName);
    return exportedTxtPath;
};
await vm3.ExportLogsCommand.ExecuteAsync();
Require(!string.IsNullOrWhiteSpace(capturedSuggestedName) && capturedSuggestedName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase),
    "Log export did not suggest a .txt file name: " + capturedSuggestedName);
Require(!string.IsNullOrWhiteSpace(exportedTxtPath) && File.Exists(exportedTxtPath),
    "Log export did not create the chosen .txt file.");
var exportedFileText = File.ReadAllText(exportedTxtPath!, Encoding.UTF8);
Require(exportedFileText.Contains("单对话运行日志", StringComparison.Ordinal) &&
    exportedFileText.Contains("轻量化方案 1", StringComparison.Ordinal) &&
    exportedFileText.Contains("[LLM Test]", StringComparison.Ordinal) &&
    string.Equals(copiedByExport, exportedFileText, StringComparison.Ordinal) &&
    vm3.StatusMessage.Contains("并已同步复制到剪贴板", StringComparison.Ordinal),
    "Exported .txt log content or synced clipboard copy did not match expectations: " + vm3.StatusMessage);

// Creating a new conversation MUST start with clean 0 logs (no accumulation from conv1!)
await vm3.CreateNewConversationCommand.ExecuteAsync();
var conv2Id = vm3.ActiveProject!.ActiveConversationId;
Require(conv2Id != conv1Id && vm3.LogEntries.Count == 0 && string.IsNullOrEmpty(vm3.Logs),
    $"New conversation did not isolate logs cleanly (count={vm3.LogEntries.Count}).");

await SendAsync(vm3, "解释当前能力");
Require(vm3.LogEntries.Count > 0 && !vm3.Logs.Contains("[LLM Test]", StringComparison.Ordinal),
    "Conversation 2 logs were polluted by Conversation 1 logs.");
var conv2LogCount = vm3.LogEntries.Count;

// Switching back to conv1 MUST restore conv1's saved logs!
var conv1VmItem = vm3.FilteredProjects.Single().Conversations.Single(c => c.Id == conv1Id);
await vm3.SelectConversationCommand.ExecuteAsync(conv1VmItem);
Require(vm3.LogEntries.Count == conv1LogCount && vm3.Logs.Contains("[LLM Test]", StringComparison.Ordinal),
    "Switching back to Conversation 1 did not restore its persisted logs.");

// ClearLogsCommand clears ONLY conv1 and persists cleared state; conv2 remains intact
await vm3.ClearLogsCommand.ExecuteAsync();
Require(vm3.LogEntries.Count == 0, "ClearLogsCommand did not clear Conversation 1 logs.");
var conv2VmItem = vm3.FilteredProjects.Single().Conversations.Single(c => c.Id == conv2Id);
await vm3.SelectConversationCommand.ExecuteAsync(conv2VmItem);
Require(vm3.LogEntries.Count == conv2LogCount, "Clearing Conversation 1 logs unexpectedly affected Conversation 2.");
conv1VmItem = vm3.FilteredProjects.Single().Conversations.Single(c => c.Id == conv1Id);
await vm3.SelectConversationCommand.ExecuteAsync(conv1VmItem);
Require(vm3.LogEntries.Count == 0, "Cleared state of Conversation 1 did not persist after switching away and back.");
Console.WriteLine("PASS per-conversation log isolation, persistence, and SaveFileDialog + clipboard .txt log export");

// --- Phase 1 LLM parsing & safety guard regression checks ---
// 1. "打开模型，我看一下，我看看最初的模型是什么样子的" must NOT be trapped in PureQuestion / ReplyOnlyMode!
var openBeforeNatural = cad.Opened.Count;
var naturalOpenReply = await SendAsync(vm3, "打开模型，我看一下，我看看最初的模型是什么样子的");
Require(llm.LastOptions?.ReplyOnlyMode == false && cad.Opened.Count == openBeforeNatural + 1 &&
    naturalOpenReply.Text.Contains("模型已打开", StringComparison.Ordinal),
    $"Natural open-model command was blocked by PureQuestion: ReplyOnly={llm.LastOptions?.ReplyOnlyMode}, Opened={cad.Opened.Count}, Text={naturalOpenReply.Text}");

// 2. "解释当前能力" and "你这个智能设计系统可以做什么?" route to CapabilityQuestion ReplyOnlyMode (no JSON repair needed)
//    AND when SolidWorks IS genuinely open, factual "当前 SolidWorks 模型已打开..." reply is preserved (no false-positive kill or double rewrite!)
llm.CustomDesignReply = """{"reply":"当前 SolidWorks 模型已打开，我可以协助你查询截面尺寸、评估上/下弦杆调整方案并启动 APDL。","actions":[],"commands":[],"parameters":[],"needConfirmation":false,"questions":[]}""";
var explainOpenReply = await SendAsync(vm3, "解释当前能力");
Require(llm.LastOptions?.ReplyOnlyMode == true && llm.LastOptions.IntentLabel == "CapabilityQuestion" &&
    explainOpenReply.Text.Contains("当前 SolidWorks 模型已打开", StringComparison.Ordinal) &&
    !explainOpenReply.Text.Contains("我没有解析到可执行动作", StringComparison.Ordinal),
    "Capability explanation while model is open was falsely killed or misrouted: " + explainOpenReply.Text);

var systemCanDoReply = await SendAsync(vm3, "你这个智能设计系统可以做什么?");
Require(llm.LastOptions?.ReplyOnlyMode == true && llm.LastOptions.IntentLabel == "CapabilityQuestion" &&
    systemCanDoReply.Text.Contains("当前 SolidWorks 模型已打开", StringComparison.Ordinal),
    "System capability question was not routed to CapabilityQuestion or falsely killed: " + systemCanDoReply.Text);

// 3. Even while SolidWorks is open, explicit hallucinated execution claims ("已为你打开并已完成修改，已保存") MUST still be blocked
//    AND must fall back to CapabilityQuestion guidance (NEVER to "我没有解析到可执行动作..." via double rewrite!).
llm.CustomDesignReply = """{"reply":"已为你打开并已完成修改，已保存模型。","actions":[],"commands":[],"parameters":[],"needConfirmation":false,"questions":[]}""";
var blockedHallucinationReply = await SendAsync(vm3, "你可以做什么？");
Require(!blockedHallucinationReply.Text.Contains("已完成修改", StringComparison.Ordinal) &&
    !blockedHallucinationReply.Text.Contains("我没有解析到可执行动作", StringComparison.Ordinal) &&
    blockedHallucinationReply.Text.Contains("可以协助您完成以下工作", StringComparison.Ordinal) &&
    blockedHallucinationReply.Text.Contains("工程规范与附图查证", StringComparison.Ordinal),
    "Hallucinated execution claim was not safely rewritten to CapabilityQuestion fallback: " + blockedHallucinationReply.Text);

// 4. When SolidWorks is CLOSED, claiming "模型已打开" on a reply-only turn MUST still be blocked!
cad.Opened.Clear();
llm.CustomDesignReply = """{"reply":"当前 SolidWorks 模型已打开，你可以直接修改。","actions":[],"commands":[],"parameters":[],"needConfirmation":false,"questions":[]}""";
var blockedClosedOpenClaim = await SendAsync(vm3, "你可以做什么？");
Require(!blockedClosedOpenClaim.Text.Contains("你可以直接修改", StringComparison.Ordinal) &&
    !blockedClosedOpenClaim.Text.Contains("我没有解析到可执行动作", StringComparison.Ordinal),
    "False model-open claim while SolidWorks is closed was not blocked: " + blockedClosedOpenClaim.Text);
llm.CustomDesignReply = null;
Console.WriteLine("PASS Phase 1 LLM parsing & state-aware safety guard regression checks");

// --- Extensible Multi-Standard Knowledge Base checks (GB/T 3811 + JT/T 1430 + GB 50017 + GB/T 6728 + GB/T 1591 + GB 50661 + live dynamic drop-in) ---
var kbSearch = LocalKnowledgeSearchService.CreateForApplication();
var catalogSummary = kbSearch.BuildKnowledgeBaseCatalogSummary();
Require(catalogSummary.Contains("GB/T 3811-2008", StringComparison.Ordinal) &&
    catalogSummary.Contains("JT/T 1430-2022", StringComparison.Ordinal) &&
    catalogSummary.Contains("GB 50017-2017", StringComparison.Ordinal) &&
    catalogSummary.Contains("GB/T 6728-2025", StringComparison.Ordinal) &&
    catalogSummary.Contains("GB/T 1591-2018", StringComparison.Ordinal) &&
    catalogSummary.Contains("GB 50661-2011", StringComparison.Ordinal),
    "Multi-standard knowledge base catalog did not discover all 6 standards: " + catalogSummary);

// 1. JT/T 1430-2022 end-to-end RAG query & DisclosureGuard pass
llm.KnowledgeAnswer = "依据检查车规范，钢桁架悬臂端最大竖向挠度不应大于 L'/400 [E1]。";
var jttReply = await SendAsync(vm3, "按 JT/T 1430-2022，钢桁架检查车悬臂端竖向挠度限值是多少？");
Require(jttReply.IsLocalKnowledgeReply &&
    jttReply.LocalCitationStatus.Contains("有效引用编号：E1", StringComparison.Ordinal) &&
    jttReply.LocalSources.Any(s => s.Source.SourceDocument == "JT_T_1430_2022/verified/02_deflection_and_load_capacity.md" &&
                                   s.Source.Standard == "JT/T 1430-2022"),
    "JT/T 1430-2022 deflection query failed RAG retrieval or disclosure check: " + jttReply.Text);

// 2. GB 50017-2017 end-to-end RAG query & DisclosureGuard pass
llm.KnowledgeAnswer = "依据钢结构设计标准，轴心受压箱形截面壁板不出现局部失稳的宽厚比限值为 b/t <= 40εk [E1]。";
var gb50017Reply = await SendAsync(vm3, "按 GB 50017-2017，轴心受压箱形方管壁板宽厚比限值是多少？");
Require(gb50017Reply.IsLocalKnowledgeReply &&
    gb50017Reply.LocalCitationStatus.Contains("有效引用编号：E1", StringComparison.Ordinal) &&
    gb50017Reply.LocalSources.Any(s => s.Source.Standard == "GB 50017-2017" &&
                                       s.Source.SourceDocument.StartsWith("GB_50017_2017/verified/", StringComparison.Ordinal)),
    "GB 50017-2017 width-thickness query failed RAG retrieval or disclosure check: " + gb50017Reply.Text);

// 3. Previously locked GB/T 3811-2008 deflection/stiffness card now passes DisclosureGuard too
llm.KnowledgeAnswer = "依据 GB/T 3811-2008，有效悬臂位置挠度推荐不大于 L1/350 [E1]。";
var gb3811StiffnessReply = await SendAsync(vm3, "按 GB/T 3811-2008，主梁悬臂静挠度 L1/350 的刚度要求是什么？");
Require(gb3811StiffnessReply.IsLocalKnowledgeReply &&
    gb3811StiffnessReply.LocalCitationStatus.Contains("有效引用编号：E1", StringComparison.Ordinal) &&
    gb3811StiffnessReply.LocalSources.Any(s => s.Source.SourceDocument is "verified/02_main_girder_stiffness.md" or "verified/09_deflection_requirement.md"),
    "Unlocked GB/T 3811-2008 stiffness/deflection rule failed RAG or disclosure check: " + gb3811StiffnessReply.Text);

// 3b. Audited & expanded GB/T 3811-2008 structural/simulation rules end-to-end RAG + DisclosureGuard verification
var gb3811UpgradedChecks = new (string Query, string ExpectedFile, string ExpectedClauseFragment, string MockAnswer)[]
{
    ("按 GB/T 3811-2008 表28，主要承载桁架弦杆受压和受拉容许长细比是多少？",
        "verified/02_main_girder_stiffness.md", "表28",
        "依据 GB/T 3811-2008 表28，主要承载桁架弦杆受压容许长细比为 150、受拉为 180 [E1]。"),
    ("按 GB/T 3811-2008 第5.8节应力比法和附录O，结构疲劳校核怎么确定K2和K4等级？",
        "verified/04_fatigue_check.md", "附录O",
        "依据 GB/T 3811-2008 第5.8节与附录O，结构疲劳采用应力比法，E4级及以上应校核，推荐K2并尽量避免K4 [E1]。"),
    ("按 GB/T 3811-2008 表34，闭口截面方管的最小防腐蚀壁厚是多少？",
        "verified/13_structural_detailing_and_minimum_thickness.md", "表34",
        "依据 GB/T 3811-2008 表34，闭口截面在微弱、中等、严重腐蚀环境下的最小厚度分别为 2.5 mm、4 mm、6 mm [E1]。"),
    ("按 GB/T 3811-2008 附录I，如何通过 ZA、ZB、ZC 评价脆性破坏并选择钢材质量组别？",
        "verified/14_brittle_fracture_and_steel_grade.md", "附录I",
        "依据 GB/T 3811-2008 附录I，按焊缝类别ZA、厚度ZB与最低环境温度ZC之和确定钢材质量组别 1~4 [E1]。"),
    ("按 GB/T 3811-2008 第6.3.4节，车轮在工字钢下翼缘运行时的有效接触宽度和允许轮压怎么校核？",
        "verified/15_wheel_rail_and_mechanical_parts.md", "6.3.4",
        "依据 GB/T 3811-2008 第6.3.4节，车轮在工字钢下翼缘运行有效接触宽度 l = w - r，静强校核 Pmax <= 1.9 k D l [E1]。"),
    ("按 GB/T 3811-2008 第9.3.4节，安全栏杆的水平集中载荷 F=300L 和最大位移限值是多少？",
        "verified/16_structural_safety_clearance_and_guardrails.md", "9.3.4",
        "依据 GB/T 3811-2008 第9.3.4节，手扶栏杆应承受水平集中载荷 F = 300L 且无永久变形、最大位移不超过 30 mm [E1]。")
};

foreach (var check in gb3811UpgradedChecks)
{
    llm.KnowledgeAnswer = check.MockAnswer;
    var checkReply = await SendAsync(vm3, check.Query);
    Require(checkReply.IsLocalKnowledgeReply &&
        checkReply.LocalCitationStatus.Contains("有效引用编号：E1", StringComparison.Ordinal) &&
        checkReply.LocalSources.Any(s =>
            s.Source.SourceDocument == check.ExpectedFile &&
            s.Source.Clause.Contains(check.ExpectedClauseFragment, StringComparison.Ordinal)),
        $"Upgraded GB/T 3811 check failed for '{check.ExpectedFile}': status={checkReply.LocalCitationStatus}, sources={string.Join(",", checkReply.LocalSources.Select(x => x.Source.SourceDocument))}, reply={checkReply.Text}");
}
Console.WriteLine("PASS audited & expanded GB/T 3811-2008 structural/simulation rules (表28/附录O/表34/附录I/6.3.4/9.3.4)");

// 3b-2. Newly integrated GB/T 6728-2025, GB/T 1591-2018 & GB 50661-2011 end-to-end RAG + DisclosureGuard + evidence table/figure verification
var newStandardChecks = new (string Query, string ExpectedStandard, string ExpectedFile, string MustContainInEvidence, string MockAnswer)[]
{
    ("按 GB/T 6728-2025 表1，70×70×5 方管的理论重量和惯性矩是多少？",
        "GB/T 6728-2025", "GB_T_6728_2025/verified/01_square_hollow_sections.md", "87.363",
        "依据 GB/T 6728-2025 表1，70×70×5 方管理论重量 9.700 kg/m，惯性矩 Ix = 87.363 cm⁴ [E1]。"),
    ("按 GB/T 6728-2025 表2，80×60×5 和 120×80×5 矩管的惯性矩和回转半径是多少？",
        "GB/T 6728-2025", "GB_T_6728_2025/verified/02_rectangular_hollow_sections.md", "106.009",
        "依据 GB/T 6728-2025 表2，80×60×5 矩管强弱轴惯性矩为 106.009 / 68.395 cm⁴ [E1]。"),
    ("按 GB/T 6728-2025 表4 和 附录B，Q355B 低合金钢方管的外圆弧半径和壁厚允许偏差是多少？",
        "GB/T 6728-2025", "GB_T_6728_2025/verified/03_tolerances_corner_radius_and_formulas.md", "±0.32",
        "依据 GB/T 6728-2025 表4与表B.1，3<t<=6 mm 低合金钢外圆弧半径为 (2.0~3.0)t，4<t<=5 mm 壁厚偏差为 ±0.32 mm [E1]。"),
    ("按 GB/T 6728-2025 表25 和 6.11，355 级冷弯方管的力学性能和热浸镀锌层重量要求是什么？",
        "GB/T 6728-2025", "GB_T_6728_2025/verified/04_material_mechanical_and_weld_quality.md", "200 g/m²",
        "依据 GB/T 6728-2025 表25与6.11，355级冷弯型钢 ReH>=355 MPa、闭口热浸镀锌层重量 >=200 g/m² [E1]。"),
    ("按 GB/T 1591-2018 表1 和 表2，热轧 Q355B 钢材的碳当量 CEV 计算公式和上限是多少？",
        "GB/T 1591-2018", "GB_T_1591_2018/verified/01_grade_designation_and_chemical_composition.md", "0.45",
        "依据 GB/T 1591-2018 第7.1.4条与表2，厚度 <=30 mm 热轧 Q355B 碳当量 CEV 不大于 0.45% [E1]。"),
    ("按 GB/T 1591-2018 表7 和 表8，厚度不大于 16 mm 的 Q355B 上屈服强度和抗拉强度是多少？",
        "GB/T 1591-2018", "GB_T_1591_2018/verified/02_mechanical_properties_tensile_and_bending.md", "470~630",
        "依据 GB/T 1591-2018 表7与表8，t<=16 mm 热轧 Q355B 上屈服强度 ReH>=355 MPa、抗拉强度 470~630 MPa [E1]。"),
    ("按 GB/T 1591-2018 表11 和 7.4.2.2，Q355B、Q355C、Q355ND 的夏比低温冲击吸收能量和小尺寸试样怎么规定？",
        "GB/T 1591-2018", "GB_T_1591_2018/verified/03_impact_toughness_surface_and_inspection.md", "75%",
        "依据 GB/T 1591-2018 表11与7.4.2.2，Q355B 为 +20℃ 34 J，Q355ND 为 -20℃ 40 J，公称厚度 >=6 mm 应做冲击试验 [E1]。"),
    ("按 GB 50661-2011 第5.1.5条，承受动荷载且需要疲劳验算的构件横向对接焊缝质量等级怎么选用？",
        "GB 50661-2011", "GB_50661_2011/verified/01_weld_quality_grades_and_designations.md", "受拉时应为一级",
        "依据 GB 50661-2011 第5.1.5条，承受动荷载且需疲劳验算构件的横向对接焊缝受拉应为一级、受压不应低于二级 [E1]。"),
    ("按 GB 50661-2011 第5.3.4条和表5.3.4，斜角角焊缝两面角 30°~60° 时的计算厚度和锐角折减值 z 怎么取？",
        "GB 50661-2011", "GB_50661_2011/verified/02_weld_effective_throat_calculation.md", "fig_5_3_4_skew_fillet_weld.png",
        "依据 GB 50661-2011 第5.3.4条与表5.3.4，两面角 30°<=Ψ<60° 时需减去折减值 z [E1]。"),
    ("按 GB 50661-2011 第5.3.6条和表5.3.6-2，Q355B 矩形管相贯节点焊缝怎么分区以及角焊缝计算厚度取多少？",
        "GB 50661-2011", "GB_50661_2011/verified/03_hollow_section_tube_joint_welds.md", "fig_5_3_6_5_rhs_fillet_details.png",
        "依据 GB 50661-2011 第5.3.6条与表5.3.6-2，Q355B 矩形管相贯节点角焊缝计算厚度取主管壁厚与 1.07 tb 的较小值 [E1]。"),
    ("按 GB 50661-2011 表5.4.2 和 5.4.3，承受动荷载的角焊缝最小焊脚尺寸和搭接接头沿母材棱边最大焊脚是多少？",
        "GB 50661-2011", "GB_50661_2011/verified/04_fillet_size_lap_transition_and_inspection.md", "fig_5_4_3_lap_and_max_fillet.png",
        "依据 GB 50661-2011 表5.4.2与5.4.3，承受动荷载的角焊缝最小焊脚尺寸为 5 mm，板厚 <=6 mm 时沿棱边最大焊脚为 t [E1]。"),
    ("按 GB 50661-2011 第8.2.1条和表8.2.1，一级焊缝和二级焊缝的咬边、未焊满和表面气孔外观质量限值是多少？",
        "GB 50661-2011", "GB_50661_2011/verified/04_fillet_size_lap_transition_and_inspection.md", "0.05t",
        "依据 GB 50661-2011 表8.2.1，一级焊缝不允许咬边、未焊满和表面气孔；二级焊缝咬边深度不大于 0.05t 且不大于 0.5 mm [E1]。")
};

foreach (var check in newStandardChecks)
{
    llm.KnowledgeAnswer = check.MockAnswer;
    var checkReply = await SendAsync(vm3, check.Query);
    var matchedSource = checkReply.LocalSources.FirstOrDefault(s =>
        s.Source.SourceDocument == check.ExpectedFile &&
        s.Source.Standard == check.ExpectedStandard);
    Require(checkReply.IsLocalKnowledgeReply &&
        checkReply.LocalCitationStatus.Contains("有效引用编号：E1", StringComparison.Ordinal) &&
        matchedSource is not null &&
        matchedSource.SentText.Contains(check.MustContainInEvidence, StringComparison.Ordinal),
        $"New standard KB check failed for '{check.ExpectedFile}': isLocal={checkReply.IsLocalKnowledgeReply}, status={checkReply.LocalCitationStatus}, sources={string.Join(",", checkReply.LocalSources.Select(x => x.Source.SourceDocument))}, reply={checkReply.Text}, sentSnippet={matchedSource?.SentText?[..Math.Min(200, matchedSource.SentText.Length)]}");
}

foreach (var naturalNewKbQuery in new[]
{
    "70×70×5 方管的惯性矩和理论重量是多少？",
    "Q355B 的碳当量上限和低温冲击功要求是多少？",
    "冷弯方管外圆弧半径和壁厚允许偏差怎么规定？",
    "承受动荷载的角焊缝最小焊脚尺寸和二级焊缝咬边限值是多少？",
    "矩形管相贯节点趾部、侧部、跟部焊缝怎么分区？",
    "不等厚板材受拉对接的最大允许坡度和斜角角焊缝折减值怎么规定？"
})
{
    Require(ChatTurnRouter.ShouldRouteToKnowledgeChat(
            naturalNewKbQuery, naturalNewKbQuery, false, false, false, false, false, false, [], null),
        "Natural query for GB/T 6728 / GB/T 1591 / GB 50661 did not route to RAG: " + naturalNewKbQuery);
}

foreach (var newFormula in new[]
{
    @"\text{CEV}(\%) = C + \frac{Mn}{6} + \frac{Cr + Mo + V}{5} + \frac{Ni + Cu}{15}",
    @"A = \frac{2t(B + H - 2t) - (4 - \pi)(R^2 - r^2)}{10^2}\;(\text{cm}^2)",
    @"I_x = \frac{1}{10^4}\left[\frac{BH^3}{12} - \frac{(B-2t)(H-2t)^3}{12} - 4(I_z + A_z h_z^2) + 4(I_s + A_s' h_s^2)\right]\;(\text{cm}^4)",
    @"h_e = h_f \cos\frac{\Psi}{2}",
    @"h_e = \left[h_f - \frac{b(\text{或 }b_1, b_2)}{\sin\Psi}\right]\cos\frac{\Psi}{2}"
})
{
    Require(RagFormulaText.TryFormat(newFormula, out var renderedFormula) && !renderedFormula.Contains('\\'),
        $"New standard LaTeX formula failed clean formatting: '{newFormula}' -> '{renderedFormula}'");
}
Console.WriteLine("PASS GB/T 6728-2025, GB/T 1591-2018 & GB 50661-2011 knowledge packs (11 verified rules, disclosure guard, natural routing & formula typography)");

// 3b-3. Exact user figure-lookup query ("我先看一下钢结构焊接规范，这里面的具体的规范图的是什么样子的呢？")
//       must route to RAG, pass DisclosureGuard, retrieve all 9 GB 50661 vector figures, expose DirectReplyFigures
//       (deduplicating inline markdown figures), and discover all 19 embedded figures for the gallery window.
const string exactUserFigureQuery = "我先看一下钢结构焊接规范，这里面的具体的规范图的是什么样子的呢？";
Require(ChatTurnRouter.ShouldRouteToKnowledgeChat(
        exactUserFigureQuery, exactUserFigureQuery, false, false, false, false, false, false, [], null),
    "Exact user GB 50661 figure-lookup query did not route to RAG.");
llm.KnowledgeAnswer =
    "以下为 GB 50661-2011《钢结构焊接规范》内置的高清矢量构造规范图 [E1][E2][E3][E4]：\n\n![图 5.2.1 管结构节点形式](GB_50661_2011/assets/fig_5_2_1_tube_joint_types.png)";
var figureLookupReply = await SendAsync(vm3, exactUserFigureQuery);
var retrievedGb50661FigurePaths = figureLookupReply.LocalSources
    .SelectMany(s => s.Figures)
    .Select(f => f.RelativeAssetPath)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();
Require(figureLookupReply.IsLocalKnowledgeReply &&
    !figureLookupReply.Text.Contains("现有资料不足", StringComparison.Ordinal) &&
    figureLookupReply.LocalSources.All(s => s.Source.Standard == "GB 50661-2011") &&
    retrievedGb50661FigurePaths.Count == 9 &&
    figureLookupReply.HasDirectReplyFigures &&
    figureLookupReply.DirectReplyFigures.Count == 8 &&
    !figureLookupReply.DirectReplyFigures.Any(f => f.RelativeAssetPath.EndsWith("fig_5_2_1_tube_joint_types.png", StringComparison.OrdinalIgnoreCase)),
    $"GB 50661 figure-lookup query failed: isLocal={figureLookupReply.IsLocalKnowledgeReply}, sources={figureLookupReply.LocalSources.Count}, totalFigs={retrievedGb50661FigurePaths.Count}, directFigs={figureLookupReply.DirectReplyFigures.Count}, reply={figureLookupReply.Text}");

var allGalleryFigures = LocalKnowledgeSearchService.CreateForApplication().DiscoverAllEmbeddedFigures();
var updatedCatalogSummary = LocalKnowledgeSearchService.CreateForApplication().BuildKnowledgeBaseCatalogSummary();
Require(allGalleryFigures.Count == 19 &&
    allGalleryFigures.Count(f => f.StandardLabel == "GB 50661-2011") == 9 &&
    updatedCatalogSummary.Contains("含 9 张高清规范图", StringComparison.Ordinal) &&
    updatedCatalogSummary.Contains("共内置 19 张高清矢量工程图", StringComparison.Ordinal),
    $"Figure gallery discovery or catalog summary incomplete: count={allGalleryFigures.Count}, summary={updatedCatalogSummary}");
Console.WriteLine("PASS standard-figure RAG retrieval (9/9 GB 50661 plates), DirectReplyFigures deduplication, and 19-figure gallery discovery");

// 3c. Formula typography, Markdown table FlowDocument rendering, visible verified clauses, and multi-line formula/table evidence preservation
var engineeringFormulas = new (string Latex, string MustContain, string MustNotContain)[]
{
    (@"\sqrt{\sigma^2 + \sigma_m^2 - \sigma\sigma_m + 3\tau^2} \le [\sigma]", "√(", @"\sqrt"),
    (@"\frac{N}{\varphi A} + \frac{1}{1 - \frac{N}{N_{Ex}}} \cdot \frac{M_x}{W_x} \le [\sigma]", "N_Ex", @"\frac"),
    (@"\left(\frac{\sigma_{x\max}}{[\sigma_{xr}]}\right)^2 + \left(\frac{\tau_{xy\max}}{[\tau_{xyr}]}\right)^2 \le 1.1", "(σₓₘₐₓ / [σₓᵣ])²", @"(("),
    (@"K_P = \sum_{i=1}^{n} \frac{C_i}{C_T} \left[\frac{P_{Qi}}{P_{Q\max}}\right]^3", "∑", @"\sum"),
    (@"\sigma_E = 18.62\left(\frac{100t}{b}\right)^2 \text{ MPa}", "MPa", @"\text"),
    (@"40^\circ \sim 70^\circ,\; f_1 \le \frac{L^\prime}{400}", "40°", @"\circ"),
    (@"\sqrt[3]{K_m}", "∛(Kₘ)", @"\sqrt"),
    (@"\begin{cases} [\sigma_{rt}] = \frac{5}{3-2r}[\sigma_{-1}], & -1 \le r \le 0 \\ [\sigma_{rc}] = \frac{2}{1-r}[\sigma_{-1}], & -1 \le r \le 0 \end{cases}", "\n", @"\begin")
};

foreach (var (latex, mustContain, mustNotContain) in engineeringFormulas)
{
    Require(RagFormulaText.TryFormat(latex, out var formatted) &&
        formatted.Contains(mustContain, StringComparison.Ordinal) &&
        !formatted.Contains(mustNotContain, StringComparison.Ordinal) &&
        !formatted.Contains('\\'),
        $"Formula formatting failed for '{latex}': got '{formatted}'");
}

Exception? staError = null;
var staThread = new Thread(() =>
{
    try
    {
        var sampleEvidence = new[]
        {
            new LocalChatEvidence(
                "E1",
                new LocalKnowledgeHit(1, "结构疲劳强度校核", "verified/04_fatigue_check.md", "5.8, 附录O", "verified-rule-0004", 10.0, "依据测试片段", "疲劳"),
                "依据测试片段")
        };
        const string sampleMarkup = """
            依据 GB/T 3811—2008 第 5.8 节与附录 O，当$\lambda \le 150$且\(\sigma_{\max} \le [\sigma]\)时 [E1]：

            \[\left(\frac{\sigma_{x\max}}{[\sigma_{xr}]}\right)^2 + \left(\frac{\tau_{xy\max}}{[\tau_{xyr}]}\right)^2 \le 1.1\]

            | 载荷组合 | 安全系数 n | 基本许用应力 |
            |---|---:|---|
            | 组合 A | 1.48 | \(\sigma_s / 1.48\) |
            | 组合 B | 1.34 | \(\sigma_s / 1.34\) |

            > 工程提示：焊接接头推荐采用 K2，避免 K4。
            """;

        var doc = RagFormattedText.BuildDocument(sampleMarkup, sampleEvidence, hideSourceReferences: false);
        var tables = doc.Blocks.OfType<Table>().ToArray();
        Require(tables.Length == 1 && tables[0].RowGroups.Count == 2 &&
            tables[0].RowGroups[0].Rows.Count == 1 && tables[0].RowGroups[1].Rows.Count == 2,
            $"Expected 1 rendered FlowDocument Table with 1 header row and 2 body rows, got {tables.Length}.");

        var plainText = new TextRange(doc.ContentStart, doc.ContentEnd).Text;
        Require(plainText.Contains("第 5.8 节", StringComparison.Ordinal) &&
            plainText.Contains("附录 O", StringComparison.Ordinal) &&
            !plainText.Contains("相关章节", StringComparison.Ordinal) &&
            !plainText.Contains("相关条文", StringComparison.Ordinal) &&
            plainText.Contains("λ ≤ 150", StringComparison.Ordinal) &&
            plainText.Contains("σₘₐₓ ≤ [σ]", StringComparison.Ordinal) &&
            plainText.Contains("(σₓₘₐₓ / [σₓᵣ])²", StringComparison.Ordinal) &&
            plainText.Contains("〔依据 1〕", StringComparison.Ordinal) &&
            !plainText.Contains(@"\frac", StringComparison.Ordinal) &&
            !plainText.Contains(@"\left", StringComparison.Ordinal) &&
            !plainText.Contains("|---|", StringComparison.Ordinal),
            "Rendered FlowDocument lost verified clause text, failed Chinese-adjacent/display formula formatting, or left raw table separators: " + plainText);

        // Verify all 19 high-res 240 DPI vector engineering plates across 6 standards + JC-GD-NG render cleanly into BlockUIContainer + BitmapImage
        var expectedVectorPlates = new[]
        {
            "JC_GD_NG/assets/fig_jc_gd_ng_chord_linkage.png",
            "JT_T_1430_2022/assets/fig_jc_gd_ng_deflection_and_test.png",
            "GB_T_6728_2025/assets/fig_rhs_section_and_tolerances.png",
            "GB_T_1591_2018/assets/fig_q355_impact_and_cev.png",
            "GB_50017_2017/assets/fig_3_5_1_box_width_thickness.png",
            "GB_50017_2017/assets/fig_7_4_truss_effective_length.png",
            "GB_50017_2017/assets/fig_13_1_rhs_tube_joint_params.png",
            "GB_T_3811_2008/assets/fig_appendix_o_fatigue_k_grades.png",
            "GB_T_3811_2008/assets/fig_6_3_4_wheel_rail_contact.png",
            "GB_T_3811_2008/assets/fig_9_3_4_guardrail_load.png",
            "GB_50661_2011/assets/fig_5_2_1_tube_joint_types.png",
            "GB_50661_2011/assets/fig_5_3_1_full_pen_throat.png",
            "GB_50661_2011/assets/fig_5_3_2_partial_pen_grooves.png",
            "GB_50661_2011/assets/fig_5_3_4_skew_fillet_weld.png",
            "GB_50661_2011/assets/fig_5_3_6_1_rhs_joint_zones.png",
            "GB_50661_2011/assets/fig_5_3_6_3_tube_full_pen_grooves.png",
            "GB_50661_2011/assets/fig_5_3_6_5_rhs_fillet_details.png",
            "GB_50661_2011/assets/fig_5_4_3_lap_and_max_fillet.png",
            "GB_50661_2011/assets/fig_5_4_4_thickness_transition.png"
        };
        foreach (var plateRelPath in expectedVectorPlates)
        {
            var plateDoc = RagFormattedText.BuildDocument($"![高清矢量工程图]({plateRelPath})", null, hideSourceReferences: false);
            var uiContainers = plateDoc.Blocks.OfType<BlockUIContainer>().ToArray();
            Require(uiContainers.Length == 1 &&
                uiContainers[0].Child is System.Windows.Controls.Border border &&
                border.Child is System.Windows.Controls.StackPanel stack &&
                stack.Children.OfType<System.Windows.Controls.Image>().FirstOrDefault() is { Source: System.Windows.Media.Imaging.BitmapImage bmp } &&
                bmp.PixelWidth >= 2000 && bmp.PixelHeight >= 1100,
                $"Expected high-res 240 DPI vector plate for '{plateRelPath}', got containers={uiContainers.Length}.");
        }

        // Verify LocalChatEvidence figure-badge metadata
        var figEvidence = new LocalChatEvidence(
            "E1",
            new LocalKnowledgeHit(1, "矩形管相贯节点", "GB_50661_2011/verified/03_hollow_section_tube_joint_welds.md", "5.3.6", "gb_50661_2011-rule-0003", 10.0, "全文", "相贯节点") { Standard = "GB 50661-2011" },
            "说明正文\n\n![图 5.3.6-1 分区](GB_50661_2011/assets/fig_5_3_6_1_rhs_joint_zones.png)\n\n![图 5.3.6-5 角焊缝](GB_50661_2011/assets/fig_5_3_6_5_rhs_fillet_details.png)");
        Require(figEvidence.HasFigures && figEvidence.FigureCount == 2 &&
            figEvidence.FigureBadgeText == "含 2 张高清图" &&
            figEvidence.FooterSummary.Contains("含高清矢量附图", StringComparison.Ordinal),
            $"LocalChatEvidence figure badge mismatch: count={figEvidence.FigureCount}, badge={figEvidence.FigureBadgeText}");

        // Verify unsafe or external image paths are strictly rejected (zero BlockUIContainer loaded)
        const string unsafeFigureMarkup = """
            ![traversal](../appsettings.json)
            ![external](https://example.com/evil.png)
            ![missing](GB_50661_2011/assets/does_not_exist.png)
            """;
        var unsafeDoc = RagFormattedText.BuildDocument(unsafeFigureMarkup, null, hideSourceReferences: false);
        Require(!unsafeDoc.Blocks.OfType<BlockUIContainer>().Any(),
            "Unsafe, external, or missing image paths must never render a BlockUIContainer image.");
    }
    catch (Exception ex)
    {
        staError = ex;
    }
});
staThread.SetApartmentState(ApartmentState.STA);
staThread.Start();
staThread.Join();
if (staError is not null) throw new InvalidOperationException("STA FlowDocument check failed.", staError);

// Verify LocalKnowledgeEvidenceSelector retains indented multi-line formulas under bullets AND trailing Markdown tables
var strengthSearch = kbSearch.Search("式 32 局部压应力 复合应力");
var strengthSelected = LocalKnowledgeEvidenceSelector.Select("式 32 局部压应力 复合应力", strengthSearch.Hits, LocalKnowledgeChatService.MaxFragmentCharacters);
var strengthEvidence = strengthSelected.FirstOrDefault(s => s.Source.SourceDocument == "verified/01_main_girder_strength.md");
Require(strengthEvidence is not null &&
    strengthEvidence.Text.Contains("sqrt(σ² + σ_m² - σ·σ_m + 3τ²) ≤ [σ]", StringComparison.Ordinal),
    "Evidence selector dropped indented formula line under bullet in 01_main_girder_strength.md: " + strengthEvidence?.Text);

var thicknessSearch = kbSearch.Search("闭口截面 最小厚度 表 34");
var thicknessSelected = LocalKnowledgeEvidenceSelector.Select("闭口截面 最小厚度 表 34", thicknessSearch.Hits, LocalKnowledgeChatService.MaxFragmentCharacters);
var thicknessEvidence = thicknessSelected.FirstOrDefault(s => s.Source.SourceDocument == "verified/13_structural_detailing_and_minimum_thickness.md");
Require(thicknessEvidence is not null &&
    thicknessEvidence.Text.Contains("| 中等 |", StringComparison.Ordinal) &&
    thicknessEvidence.Text.Contains("**4**", StringComparison.Ordinal),
    "Evidence selector dropped Markdown table in 13_structural_detailing_and_minimum_thickness.md: " + thicknessEvidence?.Text);

Console.WriteLine("PASS formula typography, Markdown Table rendering, visible verified clauses, and multi-line formula/table evidence selection");

// 4. True dynamic extensibility: drop a brand-new folder + Markdown doc at runtime -> auto-discovered & indexed without code changes!
var dynamicPackDir = Path.Combine(AppContext.BaseDirectory, "KnowledgeBase", "Dynamic_Test_Pack");
try
{
    Directory.CreateDirectory(dynamicPackDir);
    File.WriteAllText(
        Path.Combine(dynamicPackDir, "custom_boundary_note.md"),
        """
        # 自定义企业检查车焊缝探伤补充边界

        Source: Q/SL-2026
        Topic: 企业检查车焊缝探伤补充边界
        Clause: 4.2

        ## 适用范围

        本补充资料规定企业内部特有超长悬臂检查车关键折线法兰焊缝采用 100% 超声波探伤与磁粉复检。
        """,
        Encoding.UTF8);

    var dynamicSearch = kbSearch.Search("Q/SL-2026 超长悬臂检查车 折线法兰 磁粉复检");
    Require(!dynamicSearch.HasError &&
        dynamicSearch.Hits.Any(h => h.SourceDocument == "Dynamic_Test_Pack/custom_boundary_note.md" &&
                                    h.Standard == "Q/SL-2026"),
        "Dynamically dropped Markdown pack was not auto-discovered and indexed at runtime.");
}
finally
{
    if (Directory.Exists(dynamicPackDir))
    {
        Directory.Delete(dynamicPackDir, recursive: true);
    }
}

var postCleanupSearch = kbSearch.Search("Q/SL-2026 磁粉复检");
Require(!postCleanupSearch.HasError &&
    !postCleanupSearch.Hits.Any(h => h.SourceDocument == "Dynamic_Test_Pack/custom_boundary_note.md"),
    "Removed dynamic pack still lingered in stale RAG index.");
Console.WriteLine("PASS extensible multi-standard knowledge base (GB/T 3811 + JT/T 1430 + GB 50017 + live dynamic drop-in auto-indexing)");

// =========================================================================
// Phase 2 Checks: Model-Anchored Multi-Standard Design Boundary Integration
// 1) Externalized Config/design-boundary-profile.json loading
// 2) Deterministic model-anchored three-standard section evaluation
// 3) Updated RAG routing triggers (without standard code) & model-anchored design routing
// 4) Main design advisor live-model + candidate + wall-only three-standard guidance
// 5) Pre-confirmation CAD edit boundary advisories (non-blocking & disclosure-safe)
// =========================================================================
var boundaryProfilePath = DesignBoundaryProfileLoader.ResolveProfilePath();
Require(!string.IsNullOrWhiteSpace(boundaryProfilePath) && File.Exists(boundaryProfilePath),
    "Externalized Config/design-boundary-profile.json was not copied/resolved.");
var loadedProfile = DesignBoundaryProfileLoader.Load();
Require(loadedProfile.Vehicle.CantileverLengthMm == 7350m &&
    loadedProfile.Vehicle.MaterialGrade == "Q355B" &&
    loadedProfile.GbT3811.ClosedSectionMinWallModerateMm == 4.0m &&
    loadedProfile.JtT1430.SteelCantileverDeflectionDivisor == 400 &&
    loadedProfile.Gb50017.AxialCompressionLocalStabilityFactor == 40.0m,
    "Loaded design-boundary-profile.json values mismatch.");

var baseEval = DesignBoundaryEvaluationService.EvaluateMemberSection(
    70m, 70m, 5m, DesignBoundaryMemberRole.UpperChord,
    MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆"), loadedProfile);
Require(!baseEval.HasBoundaryWarning &&
    baseEval.SectionWidthThicknessClass == "S1 级" &&
    baseEval.NetFlatWidthThicknessRatio == 12.0m &&
    baseEval.OuterWidthThicknessRatio == 14.0m &&
    baseEval.CorrosionWallRisk == DesignBoundaryRiskLevel.Ok,
    "Baseline 70x70x5 UpperChord boundary evaluation mismatch.");

var thinLowerEval = DesignBoundaryEvaluationService.EvaluateMemberSection(
    60m, 60m, 3m, DesignBoundaryMemberRole.LowerChord,
    MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆"), loadedProfile);
Require(thinLowerEval.HasBoundaryWarning &&
    thinLowerEval.CorrosionWallRisk == DesignBoundaryRiskLevel.Warning &&
    thinLowerEval.CorrosionWallSummary.Contains("表34", StringComparison.Ordinal) &&
    thinLowerEval.ModelLinkageSummary.Contains("13.2.1", StringComparison.Ordinal) &&
    thinLowerEval.SimulationChecklistSummary.Contains("150", StringComparison.Ordinal) &&
    thinLowerEval.SimulationChecklistSummary.Contains("18.4 mm", StringComparison.Ordinal),
    "60x60x3 LowerChord boundary evaluation missed table-34 corrosion warning or lower-chord compression checklist.");

var slenderEval = DesignBoundaryEvaluationService.EvaluateMemberSection(
    120m, 120m, 3m, DesignBoundaryMemberRole.LowerChord,
    MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆"), loadedProfile);
Require(slenderEval.HasBoundaryWarning &&
    slenderEval.ExceedsAxialLocalBucklingLimit &&
    slenderEval.ExceedsTubeJointOuterRatioLimit &&
    slenderEval.SectionWidthThicknessClass.Contains("S5", StringComparison.Ordinal),
    "120x120x3 slender box section must flag GB 50017 local buckling & tube-joint outer ratio warnings.");

var wallOnlyEval = DesignBoundaryEvaluationService.EvaluateMemberSection(
    70m, 70m, 4m, DesignBoundaryMemberRole.LowerChord,
    MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆"), loadedProfile);
Require(!wallOnlyEval.HasBoundaryWarning &&
    wallOnlyEval.ModelLinkageSummary.Contains("仅调整壁厚", StringComparison.Ordinal) &&
    wallOnlyEval.CorrosionWallRisk == DesignBoundaryRiskLevel.Ok,
    "70x70x4 wall-only adjustment should preserve outer geometry and meet Table 34 moderate corrosion >= 4 mm.");

// New RAG trigger keywords without explicit standard codes must route to RAG
foreach (var pureNormQuery in new[]
{
    "车轮允许轮压怎么算？",
    "闭口方管最小壁厚是多少？",
    "静载试验和动载试验载荷系数怎么取？",
    "钢材抗脆断质量组别怎么选？"
})
{
    Require(ChatTurnRouter.ShouldRouteToKnowledgeChat(
            pureNormQuery, pureNormQuery, false, false, false, false, false, false, [], null),
        "Updated strong normative query did not route to RAG: " + pureNormQuery);
}

// Model-anchored design evaluations stay on main design advisor even when mentioning stiffness/deflection/wall thickness
foreach (var modelEvalQuery in new[]
{
    "把上弦杆改成 80x80x4，刚度和挠度怎么样？",
    "下弦杆壁厚减到 3 mm 行不行？",
    "我这台车的上弦杆最小壁厚改成 3 合适吗？"
})
{
    Require(!ChatTurnRouter.ShouldRouteToKnowledgeChat(
            modelEvalQuery, modelEvalQuery, false, false, false, false, false, false, [], null),
        "Model-anchored design evaluation was mistakenly diverted to RAG away from live model context: " + modelEvalQuery);
}

// Explicit standard compliance queries MUST still route to RAG
Require(ChatTurnRouter.ShouldRouteToKnowledgeChat(
        "按 GB/T 3811-2008，上弦杆截面改成 60×60 是否符合要求？",
        "按GB/T3811-2008上弦杆截面改成60×60是否符合要求",
        false, false, false, false, false, false, [], null),
    "Explicit standard compliance question stopped routing to RAG.");

// End-to-end main design advisor checks (using vm where upper chord is already modified to 60x60x5!)
await SendAsync(vm, "把上弦杆改成 80x80x4，刚度和挠度怎么样？");
var modelBoundSummary = llm.LastOptions?.RuntimeContextSummary ?? string.Empty;
Require(llm.LastOptions?.ReplyOnlyMode == true &&
    modelBoundSummary.Contains("当前上弦杆（60×60×5 mm", StringComparison.Ordinal) &&
    modelBoundSummary.Contains("80×80×4", StringComparison.Ordinal) &&
    modelBoundSummary.Contains("1270 mm", StringComparison.Ordinal) &&
    modelBoundSummary.Contains("表34", StringComparison.Ordinal) &&
    modelBoundSummary.Contains("表3.5.1", StringComparison.Ordinal) &&
    modelBoundSummary.Contains("18.4 mm", StringComparison.Ordinal) &&
    !modelBoundSummary.Contains("减重 -6.5%", StringComparison.Ordinal),
    "Model-anchored design advisor missed live model dimensions or concrete three-standard boundaries: " + modelBoundSummary);
rejectLegacy.Invoke(null, new object?[] { llm.DesignRequests.Last().History, modelBoundSummary });

await SendAsync(vm, "下弦杆壁厚减到 3 mm 行不行？");
var wallConsultSummary = llm.LastOptions?.RuntimeContextSummary ?? string.Empty;
Require(llm.LastOptions?.ReplyOnlyMode == true &&
    wallConsultSummary.Contains("70×70×3", StringComparison.Ordinal) &&
    wallConsultSummary.Contains("低于 GB/T 3811-2008 表34", StringComparison.Ordinal) &&
    wallConsultSummary.Contains("仅调整壁厚", StringComparison.Ordinal) &&
    wallConsultSummary.Contains("受压长细比 ≤150", StringComparison.Ordinal),
    "Standalone wall-thickness consultation did not anchor to active model lower chord (70x70) and Table 34 warning: " + wallConsultSummary);
rejectLegacy.Invoke(null, new object?[] { llm.DesignRequests.Last().History, wallConsultSummary });

// End-to-end CAD pre-confirmation boundary advisory checks (non-blocking & disclosure-safe)
var cadUpdatesBeforePhase2 = cad.Updates.Count;
var thinUpperPending = await SendAsync(vm, "把上弦杆改成 60x60x3");
Require(thinUpperPending.Text.Contains("待确认修改", StringComparison.Ordinal) &&
    thinUpperPending.Text.Contains("含规范风险提示", StringComparison.Ordinal) &&
    thinUpperPending.Text.Contains("仅调整壁厚", StringComparison.Ordinal) &&
    thinUpperPending.Text.Contains("表34", StringComparison.Ordinal) &&
    thinUpperPending.Text.Contains("1290 mm", StringComparison.Ordinal) &&
    thinUpperPending.Text.Contains("18.4 mm", StringComparison.Ordinal) &&
    cad.Updates.Count == cadUpdatesBeforePhase2 &&
    vm.ConfirmBusinessCommand.CanExecute(null),
    "Pre-confirmation CAD edit did not surface model-anchored three-standard advisory or blocked confirmation: " + thinUpperPending.Text);
rejectLegacy.Invoke(null, new object?[] { llm.DesignRequests.Last().History, thinUpperPending.Text });
await SendAsync(vm, "取消");

var widerUpperPending = await SendAsync(vm, "把上弦杆改成 80x80x3");
Require(widerUpperPending.Text.Contains("待确认修改", StringComparison.Ordinal) &&
    widerUpperPending.Text.Contains("自动补偿至 1270 mm", StringComparison.Ordinal) &&
    widerUpperPending.Text.Contains("1260 / 1190 mm", StringComparison.Ordinal) &&
    widerUpperPending.Text.Contains("表34", StringComparison.Ordinal) &&
    cad.Updates.Count == cadUpdatesBeforePhase2 &&
    vm.ConfirmBusinessCommand.CanExecute(null),
    "Outer-width changing CAD pre-confirmation did not surface linked red-tube compensation + walkway clearance: " + widerUpperPending.Text);
await SendAsync(vm, "取消");

var thinLowerWallPending = await SendAsync(vm, "把下弦杆壁厚改成 3");
Require(thinLowerWallPending.Text.Contains("待确认修改", StringComparison.Ordinal) &&
    thinLowerWallPending.Text.Contains("70×70×3 mm", StringComparison.Ordinal) &&
    thinLowerWallPending.Text.Contains("仅调整壁厚", StringComparison.Ordinal) &&
    thinLowerWallPending.Text.Contains("表34", StringComparison.Ordinal) &&
    thinLowerWallPending.Text.Contains("受压长细比 ≤150", StringComparison.Ordinal) &&
    cad.Updates.Count == cadUpdatesBeforePhase2 &&
    vm.ConfirmBusinessCommand.CanExecute(null),
    "Wall-only CAD pre-confirmation advisory did not combine active lower chord 70x70 with Table 34 & compression checklist: " + thinLowerWallPending.Text);
await SendAsync(vm, "取消");

Console.WriteLine("PASS Phase 2 model-anchored three-standard design boundaries, updated RAG triggers, and CAD pre-confirmation advisories");

// =========================================================================
// Plan A Checks: Live Workbench Welcome Dashboard, 2x2 Scenario Cards & Chief-Engineer Greeting
// =========================================================================
await vm3.CreateProjectCommand.ExecuteAsync();
Require(vm3.ShowWelcomeDashboard && !vm3.ShowChatMessageStream,
    "Fresh V0 project must show the live workbench welcome dashboard before the user's first message.");
var dashV0 = vm3.WelcomeDashboard;
Require(dashV0.ModelCodeBadge.Contains("不可伸缩单层桥梁检查车", StringComparison.Ordinal) &&
    dashV0.VehicleSubtitle.Contains("JC-GD-NG", StringComparison.Ordinal) &&
    dashV0.VehicleSubtitle.Contains("梁底固定轨道", StringComparison.Ordinal) &&
    dashV0.UpperChord.SpecText == "70×70×5 mm" &&
    dashV0.LowerChord.SpecText == "70×70×5 mm" &&
    !dashV0.UpperChord.HasWarning &&
    dashV0.RedlineItems.Any(r => r.Contains("18.4 mm", StringComparison.Ordinal)) &&
    dashV0.KbStatusText == "6 本规范已挂载（43 项）" &&
    dashV0.ScenarioCards.Count == 4 &&
    vm3.PreviewVehicleLinkageDiagramCommand.CanExecute(null) &&
    vm3.PreviewVehicleDeflectionDiagramCommand.CanExecute(null),
    "V0 welcome dashboard snapshot missing clear vehicle name / standard mapping / live metrics.");

// Verify every scenario card routes to its intended pipeline
var cardConsult = dashV0.ScenarioCards[0];
var cardRag = dashV0.ScenarioCards[2];
Require(!ChatTurnRouter.ShouldRouteToKnowledgeChat(
        cardConsult.Prompt, cardConsult.Prompt, false, false, false, false, false, false, [], null),
    "Scenario Card 1 (model+standard consultation) was mistakenly diverted to RAG: " + cardConsult.Prompt);
Require(ChatTurnRouter.ShouldRouteToKnowledgeChat(
        cardRag.Prompt, cardRag.Prompt, false, false, false, false, false, false, [], null),
    "Scenario Card 3 (cross-standard formula lookup) did not route to RAG: " + cardRag.Prompt);

// Verify welcome anchor message is hidden as a fake AI bubble (IsSystemWelcomeAnchor == true)
Require(vm3.ChatMessages[0].IsSystemWelcomeAnchor &&
    !vm3.ChatMessages[0].Text.Contains("您好，我是桥梁检查车智能设计助手", StringComparison.Ordinal) &&
    vm3.ChatMessages[0].Text.Contains("不可伸缩单层桥梁检查车", StringComparison.Ordinal) &&
    vm3.ChatMessages[0].Text.Contains("JC-GD-NG", StringComparison.Ordinal),
    "Welcome anchor message missing IsSystemWelcomeAnchor flag or clear vehicle naming: " + vm3.ChatMessages[0].Text);

// Clicking/sending Scenario Card 1 on fresh V0 switches to chat stream + compact context bar + collapsible briefing
await SendAsync(vm3, cardConsult.Prompt);
Require(!vm3.ShowWelcomeDashboard && vm3.ShowChatMessageStream &&
    vm3.ShowCompactChatContextBar && !vm3.ShowExpandedInlineBriefing &&
    vm3.CompactChatContextText.Contains("不可伸缩单层桥梁检查车", StringComparison.Ordinal) &&
    vm3.CompactChatContextText.Contains("上/下弦 70×70×5 mm", StringComparison.Ordinal) &&
    !vm3.CompactChatContextText.Contains("本规范已挂载", StringComparison.Ordinal) &&
    vm3.CompactChatContextTooltip.Contains("本规范已挂载", StringComparison.Ordinal) &&
    (llm.LastOptions?.RuntimeContextSummary?.Contains("表34", StringComparison.Ordinal) ?? false) &&
    (llm.LastOptions?.RuntimeContextSummary?.Contains("JT/T 1430-2022", StringComparison.Ordinal) ?? false) &&
    dashV0.ScenarioCards[1].Tag == "SolidWorks 建模" &&
    dashV0.ScenarioCards[3].Tag == "ANSYS APDL 仿真",
    "Scenario Card 1 did not switch cleanly to chat stream, Plan 1A compact bar, or 4-domain card layout.");

vm3.ToggleChatBriefingCommand.Execute(null);
Require(vm3.ShowExpandedInlineBriefing && vm3.BriefingToggleButtonText.Contains("收起", StringComparison.Ordinal),
    "ToggleChatBriefingCommand did not expand inline briefing card.");
// Clicking a scenario card inside the expanded inline briefing must auto-collapse the briefing & execute
await vm3.SendQuickPromptCommand.ExecuteAsync(dashV0.ScenarioCards[0].Prompt);
Require(!vm3.ShowExpandedInlineBriefing && vm3.BriefingToggleButtonText.Contains("展开", StringComparison.Ordinal),
    "SendQuickPromptCommand from expanded inline briefing did not auto-collapse briefing.");

// On iterated V1 project (vm1), creating a fresh follow-up conversation displays V1 live snapshot + follow-up banner
await vm1.CreateNewConversationCommand.ExecuteAsync();
Require(vm1.ShowWelcomeDashboard &&
    vm1.WelcomeDashboard.VersionTag == "V1" &&
    vm1.WelcomeDashboard.HasFollowUpNote &&
    vm1.WelcomeDashboard.UpperChord.SpecText == "60×60×5 mm" &&
    vm1.WelcomeDashboard.UpperChord.Line1.Contains("1290 mm", StringComparison.Ordinal),
    "Iterated V1 follow-up conversation welcome dashboard did not reflect 60x60x5 and 1290 mm linked red tube.");

// Sending first message in that conversation switches cleanly from Welcome Dashboard to Chat Stream
// and uses Plan 1A asymmetric chord compact bar + Plan 2A Greeting intent options & fallback
await SendAsync(vm1, "你好");
var buildGreetingMethod = typeof(MainViewModel).GetMethod("BuildDynamicGreetingReply",
    BindingFlags.Instance | BindingFlags.NonPublic)!;
var dynamicGreeting = (string)buildGreetingMethod.Invoke(vm1, null)!;
var buildPromptMethod = typeof(LlmService).GetMethod("BuildUnifiedConversationSystemPrompt",
    BindingFlags.Static | BindingFlags.NonPublic)!;
var greetingPrompt = (string)buildPromptMethod.Invoke(null, new object?[]
{
    new LlmChatRequestOptions(ReplyOnlyMode: true, IntentLabel: "Greeting", AllowApdlLauncherAction: false)
})!;
Require(!vm1.ShowWelcomeDashboard && vm1.ShowChatMessageStream &&
    vm1.CompactChatContextText.Contains("上 60×60×5 mm / 下 70×70×5 mm", StringComparison.Ordinal) &&
    llm.LastOptions?.ReplyOnlyMode == true &&
    string.Equals(llm.LastOptions?.IntentLabel, "Greeting", StringComparison.Ordinal) &&
    dynamicGreeting.Contains("欢迎使用**桥梁检查车智能设计助手**", StringComparison.Ordinal) &&
    dynamicGreeting.Contains("工程规范与附图查证", StringComparison.Ordinal) &&
    dynamicGreeting.Contains("SolidWorks 参数化改模", StringComparison.Ordinal) &&
    dynamicGreeting.Contains("ANSYS APDL 仿真协同", StringComparison.Ordinal) &&
    greetingPrompt.Contains("工程规范与附图查证", StringComparison.Ordinal) &&
    greetingPrompt.Contains("GB 50661", StringComparison.Ordinal),
    "Welcome dashboard transition, Plan 1A asymmetric chord bar, or Plan 2A greeting welcome/capabilities did not match expected format.");
Console.WriteLine("PASS Plan A live workbench welcome dashboard, Plan 1A compact bar, Plan 2A greeting welcome, and V1 follow-up sync");

// =========================================================================
// SolidWorks 4-State Connection & Attachment Diagnostics Regression Checks
// =========================================================================
var buildModelStatusMethod = typeof(MainViewModel).GetMethod("BuildModelStatusReply",
    BindingFlags.Instance | BindingFlags.NonPublic)!;
try
{
    // 1) Process running but COM unavailable (0x800401E3) -> both LLM runtime context & local status reply must report real COM reason
    cad.CustomDiagnostics = new SolidWorksSessionDiagnostics(
        SolidWorksSessionState.ProcessRunningComUnavailable,
        HasRunningProcess: true,
        RunningProcessCount: 1,
        IsComAttached: false,
        ActiveDocumentPath: string.Empty,
        ActiveDocumentName: string.Empty,
        ActiveDocumentIsAssembly: false,
        HasOpenWorkingAssemblyInSession: false,
        OpenWorkingAssemblyPath: string.Empty,
        ExpectedWorkingFolder: workspaceOptions.WorkingModelFolder,
        LastAttachHResult: unchecked((int)0x800401E3),
        DetailMessage: "检测到本机已有 1 个 SolidWorks 进程正在运行，但当前程序无法附着其 COM 会话（HRESULT=0x800401E3）。");
    await vm3.CreateNewConversationCommand.ExecuteAsync();
    Require(vm3.WelcomeDashboard.SolidWorksStatusText.Contains("COM 待重连", StringComparison.Ordinal),
        "Welcome dashboard badge did not surface COM-unavailable state: " + vm3.WelcomeDashboard.SolidWorksStatusText);
    await SendAsync(vm3, "我现在模型打开了，你看不到吗？");
    var comCtx = llm.LastOptions?.RuntimeContextSummary ?? string.Empty;
    var comLocal = (string)buildModelStatusMethod.Invoke(vm3, null)!;
    Require(comCtx.Contains("0x800401E3", StringComparison.Ordinal) && comCtx.Contains("COM", StringComparison.Ordinal) &&
        comLocal.Contains("0x800401E3", StringComparison.Ordinal) && comLocal.Contains("COM", StringComparison.Ordinal),
        "Model status under 0x800401E3 did not explain COM attachment failure. Ctx=" + comCtx + " | Local=" + comLocal);

    // 2) Connected to a different document (e.g., user opened initial model or another project)
    cad.CustomDiagnostics = new SolidWorksSessionDiagnostics(
        SolidWorksSessionState.ConnectedDifferentDocument,
        HasRunningProcess: true,
        RunningProcessCount: 1,
        IsComAttached: true,
        ActiveDocumentPath: Path.Combine(workspaceOptions.InitialModelFolder, @"总装配\总装配体.SLDASM"),
        ActiveDocumentName: "总装配体.SLDASM",
        ActiveDocumentIsAssembly: true,
        HasOpenWorkingAssemblyInSession: false,
        OpenWorkingAssemblyPath: string.Empty,
        ExpectedWorkingFolder: workspaceOptions.WorkingModelFolder,
        LastAttachHResult: 0,
        DetailMessage: "已连接到 SolidWorks，当前打开的是「总装配体.SLDASM」，但该文件位于只读初始模型目录。");
    await SendAsync(vm3, "当前模型打开了吗？");
    var wrongDocCtx = llm.LastOptions?.RuntimeContextSummary ?? string.Empty;
    var wrongDocLocal = (string)buildModelStatusMethod.Invoke(vm3, null)!;
    Require(wrongDocCtx.Contains("不属于当前项目工作副本", StringComparison.Ordinal) &&
        wrongDocLocal.Contains("只读初始模型目录", StringComparison.Ordinal) &&
        wrongDocLocal.Contains("打开当前模型", StringComparison.Ordinal),
        "Model status did not warn about non-working-copy document. Ctx=" + wrongDocCtx + " | Local=" + wrongDocLocal);

    // 3) Connected while user clicked into a sub-part tab, with working assembly still open in session
    cad.CustomDiagnostics = new SolidWorksSessionDiagnostics(
        SolidWorksSessionState.ConnectedDifferentDocument,
        HasRunningProcess: true,
        RunningProcessCount: 1,
        IsComAttached: true,
        ActiveDocumentPath: Path.Combine(workspaceOptions.WorkingModelFolder, @"桁架1\4-方管70×70×5-1603.SLDPRT"),
        ActiveDocumentName: "4-方管70×70×5-1603.SLDPRT",
        ActiveDocumentIsAssembly: false,
        HasOpenWorkingAssemblyInSession: true,
        OpenWorkingAssemblyPath: Path.Combine(workspaceOptions.WorkingModelFolder, @"总装配\总装配体.SLDASM"),
        ExpectedWorkingFolder: workspaceOptions.WorkingModelFolder,
        LastAttachHResult: 0,
        DetailMessage: "已连接到 SolidWorks：当前激活窗口是「4-方管70×70×5-1603.SLDPRT」，本项目工作总装配（总装配体.SLDASM）已在后台打开（执行改模时会自动切回）。");
    await SendAsync(vm3, "模型现在开着吗？");
    var partTabCtx = llm.LastOptions?.RuntimeContextSummary ?? string.Empty;
    var partTabLocal = (string)buildModelStatusMethod.Invoke(vm3, null)!;
    Require(partTabCtx.Contains("自动切回", StringComparison.Ordinal) &&
        partTabLocal.Contains("自动切回", StringComparison.Ordinal) &&
        partTabLocal.Contains("4-方管70×70×5-1603.SLDPRT", StringComparison.Ordinal),
        "Model status did not detect open background working assembly while viewing part tab. Ctx=" + partTabCtx + " | Local=" + partTabLocal);
}
finally
{
    cad.CustomDiagnostics = null;
}

Console.WriteLine("PASS SolidWorks 4-state COM diagnostics, non-working-copy alerts, and part-tab background assembly detection");

// =========================================================================
// Module A Checks: Per-Conversation Task Isolation, Unified Cancel, Zero Cross-Talk & CAD Exclusive Lock
// =========================================================================
{
    // 1) Pure LLM request in Conv 1 -> create & switch to a brand-new Project 2 while Conv 1 is STILL in flight:
    //    Project 2 must be completely unlocked (IsAssistantBusy == false), StatusMessage must NOT leak into BusyBannerText,
    //    and Conv 1's reply must persist back into Conv 1 with ZERO cross-talk into Project 2!
    var conv1IdBefore = vm3.ActiveProject!.ActiveConversationId;
    var proj1IdBefore = vm3.ActiveProject!.Id;
    var slowLlmTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var slowLlmStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    llm.AsyncDesignHandler = async (inputText, ct) =>
    {
        slowLlmStarted.TrySetResult();
        using var reg = ct.Register(() => slowLlmTcs.TrySetCanceled(ct));
        return await slowLlmTcs.Task;
    };

    try
    {
        vm3.UserInput = "请分析我当前模型减重方案";
        var inFlightTask = vm3.PrimaryActionCommand.ExecuteAsync();
        await slowLlmStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Require(vm3.IsCurrentConversationBusy && vm3.IsAssistantBusy &&
            vm3.CanCancelActiveTask && vm3.CancelChatCommand.CanExecute(null) &&
            vm3.CanSwitchProjectOrConversation && vm3.CreateProjectCommand.CanExecute(null) &&
            !vm3.BusyBannerText.Contains("已新建项目", StringComparison.Ordinal),
            "In-flight pure LLM task did not expose cancelable non-CAD-exclusive state properly.");

        // Switch away by creating a brand-new project while Conv 1 is STILL waiting on the LLM!
        await vm3.CreateProjectCommand.ExecuteAsync();
        var proj2Id = vm3.ActiveProject!.Id;
        Require(proj2Id != proj1IdBefore &&
            !vm3.IsCurrentConversationBusy &&
            !vm3.IsAssistantBusy &&
            vm3.SendQuickPromptCommand.CanExecute("test") &&
            vm3.ShowWelcomeDashboard,
            "Brand-new project was falsely frozen/busy by Conv 1's background LLM request! IsAssistantBusy=" + vm3.IsAssistantBusy + " Banner=" + vm3.BusyBannerText);

        vm3.UserInput = "项目2的新问题";
        Require(vm3.PrimaryActionCommand.CanExecute(null),
            "Send button remained disabled in fresh Project 2 while Conv 1 ran in background.");
        vm3.UserInput = string.Empty;

        // Complete Conv 1's background LLM call and verify ZERO pollution of Project 2 + clean persistence in Conv 1
        slowLlmTcs.TrySetResult("""{"reply":"这是归属对话1的后台完成回复_UNIQUE_TAG","actions":[],"commands":[],"parameters":[],"needConfirmation":false,"questions":[]}""");
        await inFlightTask.WaitAsync(TimeSpan.FromSeconds(5));

        Require(!vm3.ChatMessages.Any(m => m.Text.Contains("UNIQUE_TAG", StringComparison.Ordinal)),
            "Conv 1 background reply polluted Project 2's active ChatMessages!");

        // Switch back to Project 1 / Conv 1 and verify the background reply was persisted cleanly!
        var proj1Vm = vm3.FilteredProjects.Single(p => p.Id == proj1IdBefore);
        await vm3.SelectProjectCommand.ExecuteAsync(proj1Vm);
        Require(vm3.ActiveProject!.ActiveConversationId == conv1IdBefore &&
            vm3.ChatMessages.Any(m => m.Text.Contains("UNIQUE_TAG", StringComparison.Ordinal)),
            "Conv 1 background reply was lost instead of persisting into Conv 1.");
    }
    finally
    {
        llm.AsyncDesignHandler = null;
    }

    // 2) Unified Cancel ('停止等待') on a slow main LLM request must cancel cleanly without red error dot
    var cancelLlmTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancelLlmStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    llm.AsyncDesignHandler = async (_, ct) =>
    {
        cancelLlmStarted.TrySetResult();
        using var reg = ct.Register(() => cancelLlmTcs.TrySetCanceled(ct));
        return await cancelLlmTcs.Task;
    };
    try
    {
        vm3.UserInput = "请帮我评估上弦杆方案";
        var cancelableTask = vm3.PrimaryActionCommand.ExecuteAsync();
        await cancelLlmStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(vm3.CanCancelActiveTask && vm3.CancelChatCommand.CanExecute(null),
            "Main LLM request did not enable '停止等待' cancel button.");
        await vm3.CancelChatCommand.ExecuteAsync();
        await cancelableTask.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!vm3.IsAssistantBusy && !vm3.IsLlmStatusError &&
            vm3.ChatMessages.Last().Text.Contains("已取消等待", StringComparison.Ordinal),
            "Canceling main LLM request left app busy, flagged false LLM error, or missed cancel notice.");
    }
    finally
    {
        llm.AsyncDesignHandler = null;
    }

    // 3) CAD-exclusive operation ('打开当前模型') MUST lock project switching AND allow immediate cancel via '停止等待'
    var slowCadTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var slowCadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    cad.AsyncOpenHandler = async (_, ct) =>
    {
        slowCadStarted.TrySetResult();
        using var reg = ct.Register(() => slowCadTcs.TrySetCanceled(ct));
        return await slowCadTcs.Task;
    };
    try
    {
        var openTask = vm3.OpenCurrentProjectModelCommand.ExecuteAsync();
        await slowCadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(vm3.IsCadExclusiveOperationRunning && vm3.IsAssistantBusy &&
            !vm3.CanSwitchProjectOrConversation &&
            !vm3.CreateProjectCommand.CanExecute(null) &&
            !vm3.CreateNewConversationCommand.CanExecute(null) &&
            vm3.CanCancelActiveTask && vm3.CancelChatCommand.CanExecute(null),
            "CAD-exclusive open operation did not lock project/conversation switching or enable cancel button.");

        await vm3.CancelChatCommand.ExecuteAsync();
        await openTask.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!vm3.IsCadExclusiveOperationRunning && !vm3.IsAssistantBusy &&
            vm3.CanSwitchProjectOrConversation && vm3.CreateProjectCommand.CanExecute(null),
            "Canceling CAD-exclusive open operation did not release exclusive lock cleanly.");
    }
    finally
    {
        cad.AsyncOpenHandler = null;
    }

    Console.WriteLine("PASS Module A per-conversation task isolation, zero cross-talk persistence, unified cancel, and CAD-exclusive switching lock");

    // =========================================================================
    // 4-Step Runtime Quality Hardening Checks:
    // 1) Strip external OpenAI web-search citation URLs in LlmService & RagFormattedText
    // 2) Pass real SolidWorks open failure reason through CommandDispatcher (no generic license blame)
    // 3) Unified 6-level wind speed wording in BridgeCarDesignKnowledgeService
    // 4) Tail-preserving log path redaction, structured debug JSON mode, and streaming metadata preservation
    // =========================================================================
    var pollutedPlain = "钢结构设计需要同时满足承载力、刚度和稳定要求。([jncc.jinan.gov.cn](https://jncc.jinan.gov.cn/api-gateway/download?fileName=abc.pdf&utm_source=openai)) " +
                        "JT/T 1430 适用于本类公路桥梁悬挂式检查车。([std.samr.gov.cn](https://std.samr.gov.cn/hb/search/stdHBDetailed?id=123&utm_source=openai)) " +
                        "保留公式 \\(b_0/t \\le 32.5\\) 与本地证据 [E1]。";
    var cleanedReplyJson = (string)normalizeMethod.Invoke(productionLlm,
        new object?[] { pollutedPlain, "后续我要做轻量化要做什么？", new LlmChatRequestOptions(ReplyOnlyMode: true, IntentLabel: "PureQuestion") })!;
    var cleanedParsed = JsonSerializer.Deserialize<LlmParseResult>(cleanedReplyJson)!;
    Require(!cleanedParsed.Reply.Contains("http", StringComparison.OrdinalIgnoreCase) &&
        !cleanedParsed.Reply.Contains("utm_source", StringComparison.OrdinalIgnoreCase) &&
        !cleanedParsed.Reply.Contains("jncc.jinan.gov.cn", StringComparison.OrdinalIgnoreCase) &&
        !cleanedParsed.Reply.Contains("std.samr.gov.cn", StringComparison.OrdinalIgnoreCase) &&
        cleanedParsed.Reply.Contains("\\(b_0/t \\le 32.5\\)", StringComparison.Ordinal) &&
        cleanedParsed.Reply.Contains("[E1]", StringComparison.Ordinal),
        "External web-search citation URLs were not stripped cleanly or damaged formula/citation. " + cleanedParsed.Reply);

    var pollutedStructuredJson = """{"reply":"建议校核宽厚比。([std.samr.gov.cn](https://std.samr.gov.cn/hb?utm_source=openai))","actions":[],"commands":[],"parameters":[],"needConfirmation":false,"questions":[]}""";
    var cleanedStructured = JsonSerializer.Deserialize<LlmParseResult>((string)normalizeMethod.Invoke(productionLlm,
        new object?[] { pollutedStructuredJson, "分析方案", new LlmChatRequestOptions(ReplyOnlyMode: false) })!)!;
    Require(!cleanedStructured.Reply.Contains("http", StringComparison.OrdinalIgnoreCase) &&
        cleanedStructured.Reply.Contains("建议校核宽厚比。", StringComparison.Ordinal),
        "Structured JSON reply did not strip external web-search URL. " + cleanedStructured.Reply);

    var systemPromptCheck = (string)buildPrompt.Invoke(null, new object?[] { new LlmChatRequestOptions(ReplyOnlyMode: true) })!;
    Require(systemPromptCheck.Contains("严禁联网搜索与外部网页链接", StringComparison.Ordinal),
        "System prompt missing explicit prohibition on external web search / URL citations.");

    // Streaming metadata merge must NOT overwrite valid ResponseId/ResponseModel with empty trailing chunk strings.
    var metaType = typeof(LlmService).GetNestedType("LlmResponseMetadata", BindingFlags.NonPublic)!;
    var emptyMeta = metaType.GetProperty("Empty", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    var updateMeta = typeof(LlmService).GetMethod("MergeStreamingMetadata", BindingFlags.Static | BindingFlags.NonPublic)!;
    using var firstChunk = JsonDocument.Parse("""{"id":"resp_real_123","object":"chat.completion.chunk","model":"gpt-6.1-sol","choices":[{"delta":{"content":"hi"},"finish_reason":null}]}""");
    using var lastChunk = JsonDocument.Parse("""{"id":"","object":"chat.completion.chunk","model":"","choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""");
    var m1 = updateMeta.Invoke(null, new[] { emptyMeta, (object)firstChunk.RootElement })!;
    var m2 = updateMeta.Invoke(null, new[] { m1, (object)lastChunk.RootElement })!;
    Require((string?)metaType.GetProperty("ResponseId")!.GetValue(m2) == "resp_real_123" &&
        (string?)metaType.GetProperty("ResponseModel")!.GetValue(m2) == "gpt-6.1-sol" &&
        (string?)metaType.GetProperty("FinishReason")!.GetValue(m2) == "stop",
        "Streaming metadata was overwritten by blank trailing chunk fields.");

    // Wind speed 6-level unification wording check
    var windSummary = BridgeCarDesignKnowledgeService.BuildVehicleBaselineAndRangeSummary();
    Require(windSummary.Contains("6 级风口径", StringComparison.Ordinal) &&
        windSummary.Contains("13.8 m/s 包络校核", StringComparison.Ordinal),
        "Wind speed 6-level unified explanation missing from BridgeCarDesignKnowledgeService.");

    // SanitizeLogText must keep last 3 path segments without drive letter and MUST NOT corrupt normal Chinese prose mentioning .SLDASM
    var sanitizeLogMethod = typeof(MainViewModel).GetMethod("SanitizeLogText", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var sanitizedErrLine = (string)sanitizeLogMethod.Invoke(vm3,
        new object?[] { @"打开 SolidWorks 装配体失败。装配引用既不在工作副本也不在初始模型目录：E:\反力架\反力架\三六重工v2\不伸缩单层检查车\Solidworks模型\项目工作区\proj-20261126-211855-71a0ce\桁架2\桁架2(3-3).SLDASM (0x80131509)" })!;
    Require(!sanitizedErrLine.Contains("E:\\", StringComparison.OrdinalIgnoreCase) &&
        sanitizedErrLine.Contains(@"<redacted-path>\proj-20261126-211855-71a0ce\桁架2\桁架2(3-3).SLDASM", StringComparison.Ordinal) &&
        sanitizedErrLine.Contains("(0x80131509)", StringComparison.Ordinal),
        "SanitizeLogText failed to preserve tail segments or leaked drive letter: " + sanitizedErrLine);
    var sanitizedProseLine = (string)sanitizeLogMethod.Invoke(vm3,
        new object?[] { "SolidWorks 已连接，当前打开的是“总装配体.SLDASM”，工作区也已初始化。" })!;
    Require(!sanitizedProseLine.Contains("<redacted-path>", StringComparison.Ordinal) &&
        sanitizedProseLine.Contains("当前打开的是“总装配体.SLDASM”", StringComparison.Ordinal),
        "SanitizeLogText falsely corrupted plain Chinese prose mentioning .SLDASM: " + sanitizedProseLine);

    // Debug JSON output must expose mode & intentLabel cleanly on pure-question turns
    llm.CustomDesignReply = """{"reply":"评估建议正文。([jncc.jinan.gov.cn](https://jncc.jinan.gov.cn/doc.pdf?utm_source=openai))","actions":[],"commands":[],"parameters":[],"needConfirmation":false,"questions":[]}""";
    try
    {
        await SendAsync(vm3, "请问轻量化应该先关注哪个构件？");
        Require(vm3.JsonOutput.Contains("ReplyOnly", StringComparison.Ordinal) &&
            vm3.JsonOutput.Contains("intentLabel", StringComparison.Ordinal) &&
            !vm3.JsonOutput.Contains("utm_source", StringComparison.OrdinalIgnoreCase),
            "Debug JsonOutput did not expose mode/intentLabel or leaked external citation URL: " + vm3.JsonOutput);
    }
    finally
    {
        llm.CustomDesignReply = null;
    }

    // CommandDispatcher must surface SolidWorksService.GetLastOpenFailureMessage instead of generic license blame
    cad.CustomOpenFailureMessage = "SolidWorks 当前打开了其他目录的同名或关联模型（总装配体.SLDASM），且存在未保存修改。请先保存并关闭旧模型后重试。";
    cad.AsyncOpenHandler = (_, _) => Task.FromResult(false);
    try
    {
        var openFailResult = await dispatcher.ExecuteAsync(
            new LlmParseResult { Actions = ["open_working_model"], Commands = ["open_working_model"] },
            [],
            "打开当前模型",
            _ => { });
        Require(!openFailResult.Succeeded &&
            openFailResult.AssistantMessages.Any(m => m.Contains("其他目录的同名或关联模型", StringComparison.Ordinal)) &&
            !openFailResult.AssistantMessages.Any(m => m.Contains("许可证", StringComparison.Ordinal)),
            "CommandDispatcher swallowed real open failure message or fell back to misleading license blame.");
    }
    finally
    {
        cad.AsyncOpenHandler = null;
        cad.CustomOpenFailureMessage = null;
    }

    Console.WriteLine("PASS 4-step runtime hardening: external URL citation sanitizer, real open-failure passthrough, unified 6-level wind wording, tail-preserving log sanitization, and debug JSON mode");

    // =========================================================================
    // Workspace Slimming, Verified Rebind Cache & Fork-Origin Rebind Checks
    // =========================================================================
    var cacheRoot = Path.Combine(Path.GetTempPath(), "rebind-cache-check-" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        var cacheInitial = Path.Combine(cacheRoot, "初始模型");
        var cacheWorkA = Path.Combine(cacheRoot, "项目工作区", "proj-a");
        var cacheWorkB = Path.Combine(cacheRoot, "项目工作区", "proj-b");
        Directory.CreateDirectory(Path.Combine(cacheInitial, "总装配"));
        Directory.CreateDirectory(Path.Combine(cacheInitial, "桁架1"));
        Directory.CreateDirectory(Path.Combine(cacheInitial, "初始模型", "桁架1")); // stray nested duplicate
        File.WriteAllText(Path.Combine(cacheInitial, "总装配", "总装配体.SLDASM"), "asm-root-v1");
        File.WriteAllText(Path.Combine(cacheInitial, "桁架1", "part1.SLDPRT"), "part-1-v1");
        File.WriteAllText(Path.Combine(cacheInitial, "初始模型", "桁架1", "duplicate.SLDPRT"), "stray-duplicate");

        var slimMgr = new ModelWorkspaceManager(new SolidWorksWorkspaceOptions
        {
            ModelRootPath = cacheRoot,
            InitialModelFolder = cacheInitial,
            WorkingModelFolder = cacheWorkA,
            AssemblyFileName = @"总装配\总装配体.SLDASM"
        });
        await slimMgr.EnsureWorkspaceInitializedAsync();
        var workAAsm = Path.Combine(cacheWorkA, "总装配", "总装配体.SLDASM");
        var workAPart = Path.Combine(cacheWorkA, "桁架1", "part1.SLDPRT");
        Require(File.Exists(workAAsm) && File.Exists(workAPart) &&
            !Directory.Exists(Path.Combine(cacheWorkA, "初始模型")),
            "ModelWorkspaceManager copied stray nested '初始模型' subfolder into working directory.");

        // Cache must miss before first write, hit after WriteRebindCache, and invalidate on edit or path move
        Require(!CadReferenceBoundary.TryValidateRebindCache(workAAsm, cacheWorkA, cacheInitial, out _),
            "Rebind cache unexpectedly hit before stamp was written.");
        CadReferenceBoundary.WriteRebindCache(workAAsm, cacheWorkA, reboundCount: 1);
        Require(CadReferenceBoundary.TryValidateRebindCache(workAAsm, cacheWorkA, cacheInitial, out var cachedCount) &&
            cachedCount == 1,
            "Rebind cache did not hit immediately after verified write.");

        // Simulate editing the assembly -> cache must invalidate
        File.WriteAllText(workAAsm, "asm-root-v2-modified");
        Require(!CadReferenceBoundary.TryValidateRebindCache(workAAsm, cacheWorkA, cacheInitial, out _),
            "Rebind cache failed to invalidate after assembly content/length changed.");
        CadReferenceBoundary.WriteRebindCache(workAAsm, cacheWorkA, reboundCount: 0);

        // Forked workspace: must reject copied cache stamp, and cleanly rebind references from verified fork source
        Directory.CreateDirectory(Path.Combine(cacheWorkB, "总装配"));
        Directory.CreateDirectory(Path.Combine(cacheWorkB, "桁架1"));
        var workBAsm = Path.Combine(cacheWorkB, "总装配", "总装配体.SLDASM");
        var workBPart = Path.Combine(cacheWorkB, "桁架1", "part1.SLDPRT");
        File.Copy(workAAsm, workBAsm, overwrite: true);
        File.Copy(workAPart, workBPart, overwrite: true);
        File.Copy(Path.Combine(cacheWorkA, ".workspace-rebind-verified.json"), Path.Combine(cacheWorkB, ".workspace-rebind-verified.json"), overwrite: true);
        Require(!CadReferenceBoundary.TryValidateRebindCache(workBAsm, cacheWorkB, cacheInitial, out _),
            "Copied rebind cache stamp from another project directory must NOT validate in forked workspace.");

        CadReferenceBoundary.WriteForkOrigin(cacheWorkB, cacheWorkA);
        var forkDeps = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFullPath(workBAsm)] = ["part1", Path.GetFullPath(workAPart)]
        };
        var forkRebound = CadReferenceBoundary.RebindWorkingAssembly(
            workBAsm, cacheWorkB, cacheInitial,
            owner => forkDeps[Path.GetFullPath(owner)],
            (owner, oldP, newP) =>
            {
                forkDeps[Path.GetFullPath(owner)] = ["part1", newP];
                return true;
            },
            _ => false,
            _ => { });
        Require(forkRebound == 1 &&
            string.Equals(forkDeps[Path.GetFullPath(workBAsm)][1], Path.GetFullPath(workBPart), StringComparison.OrdinalIgnoreCase),
            "Forked workspace failed to rebind verified source-workspace references into forked folder.");
        CadReferenceBoundary.WriteRebindCache(workBAsm, cacheWorkB, forkRebound);
        Require(!File.Exists(Path.Combine(cacheWorkB, ".workspace-fork-origin.json")) &&
            CadReferenceBoundary.TryValidateRebindCache(workBAsm, cacheWorkB, cacheInitial, out _),
            "Fork origin marker was not cleaned up or new rebind cache failed after fork rebind.");
    }
    finally
    {
        if (Directory.Exists(cacheRoot))
        {
            try { Directory.Delete(cacheRoot, recursive: true); } catch { }
        }
    }

    Console.WriteLine("PASS workspace slimming (skips stray 初始模型), verified rebind cache hit/invalidation, and SHA-256 fork-origin rebinding");

    // =========================================================================
    // Conversation Continuity Checks (Discuss -> Open Model -> Deviated Edit -> Button Confirm -> Versioned Receipt)
    // =========================================================================
    await vm3.CreateProjectCommand.ExecuteAsync();
    llm.CustomDesignReply = """{"reply":"建议首轮方案：上弦杆由当前的70×70×5 mm改为70×70×4 mm；下弦杆保持70×70×5 mm。先输入“打开当前模型”。","actions":[],"commands":[],"parameters":[],"needConfirmation":false,"questions":[]}""";
    try
    {
        await SendAsync(vm3, "我想做轻量化设计，先给我一个具体方案");
    }
    finally
    {
        llm.CustomDesignReply = null;
    }

    var openedContinuityReply = await SendAsync(vm3, "打开当前模型");
    Require(openedContinuityReply.Text.Contains("模型已打开", StringComparison.Ordinal) &&
        openedContinuityReply.Text.Contains("70×70×4 mm", StringComparison.Ordinal) &&
        openedContinuityReply.Text.Contains("把上弦杆壁厚改成 4", StringComparison.Ordinal) &&
        !openedContinuityReply.Text.Contains("修改指令示例：", StringComparison.Ordinal) &&
        !openedContinuityReply.Text.Contains("60x60", StringComparison.Ordinal),
        "Open-model reply lost prior 70x70x4 plan context or fell back to rigid 60x60 tutorial template: " + openedContinuityReply.Text);

    // Submitting a different target (60x60) must gently flag the deviation from the discussed 70x70x4 plan
    var deviatedPendingReply = await SendAsync(vm3, "把桁架上弦杆截面改成 60x60");
    Require(deviatedPendingReply.Text.Contains("待确认修改", StringComparison.Ordinal) &&
        deviatedPendingReply.Text.Contains("【方案衔接提醒】", StringComparison.Ordinal) &&
        deviatedPendingReply.Text.Contains("70×70×4 mm", StringComparison.Ordinal) &&
        deviatedPendingReply.Text.Contains("60×60×5 mm", StringComparison.Ordinal),
        "Pending confirmation missed gentle deviation notice from prior discussed plan: " + deviatedPendingReply.Text);

    // Clicking the Confirm button must record a user confirmation bubble AND emit a rich versioned receipt (V1 + 1290 mm)
    await vm3.ConfirmBusinessCommand.ExecuteAsync();
    var continuityMessages = vm3.ChatMessages.TakeLast(2).ToList();
    Require(continuityMessages.Count == 2 &&
        continuityMessages[0].IsUser &&
        continuityMessages[0].Text.Contains("确认执行改模", StringComparison.Ordinal) &&
        !continuityMessages[1].IsUser &&
        continuityMessages[1].Text.Contains("已完成：", StringComparison.Ordinal) &&
        continuityMessages[1].Text.Contains("V1", StringComparison.Ordinal) &&
        continuityMessages[1].Text.Contains("60×60×5 mm", StringComparison.Ordinal) &&
        continuityMessages[1].Text.Contains("1290 mm", StringComparison.Ordinal) &&
        continuityMessages[1].Text.Contains("把上弦杆截面改成 70x70x4", StringComparison.Ordinal),
        "Button confirmation did not leave user bubble or emit rich versioned CAD completion receipt: " +
        string.Join(" || ", continuityMessages.Select(m => $"{(m.IsUser ? "U" : "A")}:{m.Text}")));

    // Also verify IsUnderWorkingModelPath=True is NOT corrupted by WorkingModelPath regex
    var safetyBoolLine = (string)sanitizeLogMethod.Invoke(vm3, new object?[] { "[Safety] IsUnderWorkingModelPath=True" })!;
    Require(safetyBoolLine == "[Safety] IsUnderWorkingModelPath=True",
        "SanitizeLogText corrupted IsUnderWorkingModelPath=True: " + safetyBoolLine);

    Console.WriteLine("PASS conversation continuity across discuss -> open model -> deviation notice -> button confirmation bubble -> versioned CAD receipt");

    // =========================================================================
    // UI Clarity & Multi-Standard Provenance Checks:
    // 1) Status badges distinguish '未打开' vs running (no false green APDL badge when not running)
    // 2) Welcome cards & redlines explicitly include standard codes (no bare '表34'/'表28'/'节点13.1.2')
    // 3) LocalChatEvidence exposes StandardLabel/ClauseLabel/FooterSummary for RagReplyView headers
    // =========================================================================
    cad.CustomDiagnostics = new SolidWorksSessionDiagnostics(
        SolidWorksSessionState.NotRunning, false, 0, false,
        string.Empty, string.Empty, false, false, string.Empty, string.Empty, 0,
        "当前未检测到运行中的 SolidWorks 进程。");
    try
    {
        await vm3.CreateProjectCommand.ExecuteAsync();
        var freshDash = vm3.WelcomeDashboard;
        Require(freshDash.SolidWorksStatusText == "SolidWorks 未打开" && !freshDash.IsSolidWorksOpened,
            "Fresh dashboard should clearly show 'SolidWorks 未打开' when SW is not running: " + freshDash.SolidWorksStatusText);
        Require(!freshDash.IsApdlRunning && freshDash.ApdlStatusText == "ANSYS APDL 未打开",
            "Fresh dashboard should show 'ANSYS APDL 未打开' (not a misleading green ready badge) when APDL is not running: " + freshDash.ApdlStatusText);
        Require(freshDash.UpperChord.StatusBadgeText == "壁厚 / 宽厚比合格" &&
            freshDash.UpperChord.StatusBadgeTooltip.Contains("GB/T 3811-2008 表34", StringComparison.Ordinal) &&
            freshDash.UpperChord.Line1.Contains("JT/T 1430-2022", StringComparison.Ordinal) &&
            freshDash.UpperChord.Line2.Contains("GB 50017-2017", StringComparison.Ordinal) &&
            freshDash.LowerChord.Line1.Contains("GB/T 3811-2008 表28", StringComparison.Ordinal) &&
            freshDash.LowerChord.Line2.Contains("GB 50017-2017 13.1.2", StringComparison.Ordinal),
            "Welcome member cards still contain ambiguous bare table/clause numbers without standard codes.");
        Require(freshDash.RedlineItems.All(item =>
            item.Contains("JT/T 1430-2022", StringComparison.Ordinal) ||
            item.Contains("GB/T 3811-2008", StringComparison.Ordinal) ||
            item.Contains("GB 50017-2017", StringComparison.Ordinal)),
            "Every redline chip must carry its explicit standard code: " + string.Join(" | ", freshDash.RedlineItems));
    }
    finally
    {
        cad.CustomDiagnostics = null;
    }

    var sampleEvidence = new LocalChatEvidence(
        "E5",
        new LocalKnowledgeHit(1, "钢材与连接许用应力（表22至表27）", "verified/04_allowable_stress.md", "表22~表27", "chunk-5", 10, "原文内容", "许用应力")
        {
            Standard = "GB/T 3811-2008"
        },
        "原文内容");
    Require(sampleEvidence.StandardLabel == "GB/T 3811-2008" &&
        sampleEvidence.ClauseLabel == "条款 表22~表27" &&
        sampleEvidence.Heading.Contains("[GB/T 3811-2008]", StringComparison.Ordinal) &&
        sampleEvidence.FooterSummary.Contains("GB/T 3811-2008", StringComparison.Ordinal),
        "LocalChatEvidence did not expose StandardLabel / ClauseLabel / FooterSummary for evidence headers.");

    Console.WriteLine("PASS UI clarity: truthful unopened SW/APDL badges, explicit standard codes on all dashboard cards/redlines, and standard-prefixed evidence metadata");
}

// =========================================================================
// Comprehensive P0/P1/P2 Upgrades Regression Checks:
//   1. PersistedChatMessage retains LocalSources & DirectReplyFigures across cold restart
//   2. Dual-chord rollback queues BOTH upper & lower chord steps cleanly
//   3. Dynamic KnowledgeFigureGalleryButtonText ("规范图库 (19)")
//   4. 6-standard boundary engine + GB/T 6728-2025 rounded-corner exact math & catalog check
//   5. Illustrated Word (.docx) engineering report package integrity + 3 embedded PNG figures
//   6. AppConfigLoader.SaveSettings atomic JSON persistence
// =========================================================================
{
    // 1. Persistence of RAG LocalSources & DirectReplyFigures across JSON serialization
    var sampleFigureEvidence = new LocalChatEvidence(
        "E1",
        new LocalKnowledgeHit(1, "矩形管相贯节点焊缝", "GB_50661_2011/verified/03_hollow_section_tube_joint_welds.md", "5.3.6", "gb_50661_2011-rule-0003", 10,
            "![图 5.3.6-1 焊缝分区](GB_50661_2011/assets/fig_5_3_6_1_rhs_joint_zones.png)", "焊缝分区")
        {
            Standard = "GB 50661-2011"
        },
        "![图 5.3.6-1 焊缝分区](GB_50661_2011/assets/fig_5_3_6_1_rhs_joint_zones.png)");
    var runtimeRagMsg = new ChatMessage
    {
        IsUser = false,
        RoleDisplay = "智能设计助手",
        Text = "请参考相贯节点分区 [E1]。",
        IsLocalKnowledgeReply = true,
        LocalSources = [sampleFigureEvidence],
        LocalCitationStatus = "有效引用编号：E1。"
    };
    var persistedJson = JsonSerializer.Serialize(PersistedChatMessage.FromRuntimeChatMessage(runtimeRagMsg));
    var restoredRagMsg = JsonSerializer.Deserialize<PersistedChatMessage>(persistedJson)!.ToRuntimeChatMessage();
    Require(restoredRagMsg.IsLocalKnowledgeReply &&
        restoredRagMsg.LocalSources.Count == 1 &&
        restoredRagMsg.LocalSources[0].StandardLabel == "GB 50661-2011" &&
        restoredRagMsg.HasDirectReplyFigures &&
        restoredRagMsg.DirectReplyFigures.Count == 1 &&
        restoredRagMsg.DirectReplyFigures[0].RelativeAssetPath == "GB_50661_2011/assets/fig_5_3_6_1_rhs_joint_zones.png",
        "PersistedChatMessage did not preserve LocalSources or DirectReplyFigures across JSON round-trip.");

    // 2. Dual-chord rollback queues BOTH upper chord & lower chord steps
    var rbStoreRoot = Path.Combine(Path.GetTempPath(), "LlmWpfRollbackTest_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(rbStoreRoot);
    try
    {
        var rbStore = new DesignProjectStoreService(rbStoreRoot, workspaceOptions);
        var rbVm = new MainViewModel(llm, dictionary, new ApdlReplacementService(), service, dispatcher, workspace,
            configuredLlmOptions, chord, members, scans, recommendation, parser, simulation, null, rbStore);
        var rbProj = rbStore.ActiveProject!;
        var rbV0Checkpoint = rbProj.VersionHistory[0];
        rbProj.UpperChord = new MemberSectionSpecRecord { MemberId = "upper_chord", MemberName = "上弦杆", WidthMm = 60m, HeightMm = 60m, ThicknessMm = 5m, Source = "测试" };
        rbProj.LowerChord = new MemberSectionSpecRecord { MemberId = "lower_chord", MemberName = "下弦杆", WidthMm = 70m, HeightMm = 70m, ThicknessMm = 4m, Source = "测试" };
        await rbVm.RollbackToVersionCommand.ExecuteAsync(rbV0Checkpoint);
        var followUpField = typeof(MainViewModel).GetField("_pendingFollowUpRollbackSteps", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var queuedSteps = (System.Collections.ICollection)followUpField.GetValue(rbVm)!;
        var lastRollbackBubble = rbVm.ChatMessages.Last().Text;
        Require(rbVm.HasPendingCadConfirmation &&
            queuedSteps.Count == 1 &&
            lastRollbackBubble.Contains("随后自动执行：下弦杆 -> 70×70×5", StringComparison.Ordinal),
            $"Dual-chord rollback did not queue both upper and lower chord steps: queued={queuedSteps.Count}, bubble={lastRollbackBubble}");

        // 3. Dynamic KnowledgeFigureGalleryButtonText
        Require(rbVm.WelcomeDashboard.KnowledgeFigureGalleryButtonText == "规范图库 (19)",
            "Dynamic gallery button text mismatch: " + rbVm.WelcomeDashboard.KnowledgeFigureGalleryButtonText);

        // 4. 6-standard boundary engine + GB/T 6728-2025 rounded-corner exact math & catalog check
        var base6728 = GbT6728SectionCatalogService.Evaluate(70m, 70m, 5m);
        Require(base6728.IsStandardCatalogSpec &&
            base6728.RoundedAreaCm2 == 12.356m &&
            base6728.RoundedLineMassKgPerM == 9.700m &&
            Math.Abs(base6728.RoundedIxCm4 - 87.363m) <= 0.05m &&
            base6728.WallToleranceMm == 0.32m &&
            base6728.MinAllowableWallMm == 4.68m,
            $"GB/T 6728-2025 70x70x5 exact rounded properties mismatch: A={base6728.RoundedAreaCm2}, M={base6728.RoundedLineMassKgPerM}, Ix={base6728.RoundedIxCm4}");

        var nonStd6728 = GbT6728SectionCatalogService.Evaluate(65m, 65m, 4.5m);
        Require(!nonStd6728.IsStandardCatalogSpec && nonStd6728.NearestStandardSpecs.Count == 2,
            "Non-standard 65x65x4.5 was not flagged or missing nearest catalog recommendations.");

        var eval6Std = DesignBoundaryEvaluationService.EvaluateMemberSection(65m, 65m, 4.5m, DesignBoundaryMemberRole.UpperChord);
        Require(eval6Std.Highlights.Count == 7 &&
            eval6Std.StandardSectionSummary.Contains("非 GB/T 6728-2025", StringComparison.Ordinal) &&
            eval6Std.WeldingDetailingSummary.Contains("表5.4.2", StringComparison.Ordinal) &&
            eval6Std.WeldingDetailingSummary.Contains("1:2.5", StringComparison.Ordinal) &&
            eval6Std.SteelMaterialSummary.Contains("CEV≤0.45%", StringComparison.Ordinal),
            "6-standard deterministic boundary evaluation missing GB/T 6728, GB 50661, or GB/T 1591 outputs.");

        // 5. Illustrated Word (.docx) engineering report package integrity + 3 embedded PNG figures
        var docxOutDir = Path.Combine(rbStoreRoot, "reports");
        var docxPath = rbStore.ExportProjectWordReport(rbProj.Id, docxOutDir);
        Require(File.Exists(docxPath) && docxPath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase),
            "Word report (.docx) was not created.");
        using (var zip = System.IO.Compression.ZipFile.OpenRead(docxPath))
        {
            var entryNames = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Require(entryNames.Contains("[Content_Types].xml") &&
                entryNames.Contains("_rels/.rels") &&
                entryNames.Contains("word/document.xml") &&
                entryNames.Contains("word/styles.xml") &&
                entryNames.Contains("word/_rels/document.xml.rels") &&
                entryNames.Contains("word/media/figure_1.png") &&
                entryNames.Contains("word/media/figure_2.png") &&
                entryNames.Contains("word/media/figure_3.png"),
                "Generated .docx archive missing required OOXML parts or embedded figures: " + string.Join(", ", entryNames));
            using var docReader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            var docXmlText = docReader.ReadToEnd();
            var parsedDoc = System.Xml.Linq.XDocument.Parse(docXmlText);
            Require(parsedDoc.Root is not null &&
                docXmlText.Contains("六大核心工程规范边界核对清单", StringComparison.Ordinal) &&
                docXmlText.Contains("GB/T 6728-2025", StringComparison.Ordinal) &&
                docXmlText.Contains("GB 50661-2011", StringComparison.Ordinal) &&
                docXmlText.Contains("GB/T 1591-2018", StringComparison.Ordinal),
                "Generated word/document.xml missing expected 6-standard report sections.");
        }

        // 6. AppConfigLoader.SaveSettings atomic JSON persistence
        var tempCfgPath = Path.Combine(rbStoreRoot, "appsettings.test.json");
        AppConfigLoader.SaveSettings(new AppSettings
        {
            Llm = new LlmOptions { Mode = "Cloud", Provider = "OpenAI", ApiBaseUrl = "https://example.invalid/v1", ApiKeyEnvName = "OPENAI_API_KEY", Model = "gpt-6.1-sol", TimeoutSeconds = 120 },
            Simulation = new SimulationOptions { LauncherPath = @"C:\ANSYS\launcher.exe", SolverPath = @"C:\ANSYS\ANSYS.exe", StudyFile = @"C:\ANSYS\study.json" },
            SolidWorksWorkspace = new SolidWorksWorkspaceOptions { ModelRootPath = @"C:\Models", InitialModelFolder = @"C:\Models\Init", WorkingModelFolder = @"C:\Models\Work", AssemblyFileName = "asm.SLDASM" }
        }, tempCfgPath);
        var savedCfgJson = File.ReadAllText(tempCfgPath);
        Require(savedCfgJson.Contains("\"Model\": \"gpt-6.1-sol\"", StringComparison.Ordinal) &&
            savedCfgJson.Contains("study.json", StringComparison.Ordinal) &&
            savedCfgJson.Contains("asm.SLDASM", StringComparison.Ordinal),
            "AppConfigLoader.SaveSettings output missing saved fields.");
    }
    finally
    {
        try { Directory.Delete(rbStoreRoot, true); } catch { }
    }

    Console.WriteLine("PASS P0/P1/P2 upgrades: RAG figure persistence, dual-chord rollback, 6-standard GB/T 6728/GB 50661/GB/T 1591 evaluator, illustrated .docx export, and settings save");
}

// =========================================================================
// Cross-Machine & Cross-Version Portability + Privacy Hardening Checks:
//   - Plaintext Config/appsettings.json & editable-truss-members.json contain ZERO private URLs, model names, or E:\ paths
//   - AppConfigLoader resolves compiled-in LLM defaults + auto-detects ANSYS 18.0 (AWP_ROOT180) & SW workspace on this PC
//   - AnsysInstallationDetector supports ANSYS 18.0 (v180) and modern 2021~2025 R1+ (v212/v242/v251)
//   - ApdlBatchRunner.ResolveVersionedBatchWrapper supports both legacy 18.0 and modern 2024 R2 / 2025 R1
//   - WorkspacePathResolver discovers portable bundled <App>\Models\初始模型 on any arbitrary drive/folder
// =========================================================================
{
    var repoConfigDir = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(Program).Assembly.Location) is not null &&
        Directory.Exists(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "LlmWpfPrototype", "Config")))
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "LlmWpfPrototype", "Config"))
            : @"e:\反力架\反力架\三六重工v2\LlmWpfPrototype\Config"));
    var rawAppSettingsText = File.ReadAllText(Path.Combine(repoConfigDir, "appsettings.json"));
    Require(!rawAppSettingsText.Contains("api.kgnas.top", StringComparison.OrdinalIgnoreCase) &&
        !rawAppSettingsText.Contains("gpt-6.1-sol", StringComparison.OrdinalIgnoreCase) &&
        !rawAppSettingsText.Contains(@"E:\", StringComparison.OrdinalIgnoreCase),
        "Bundled appsettings.json still leaks private URL, model name, or developer E:\\ path.");

    var rawTrussMembersText = File.ReadAllText(Path.Combine(repoConfigDir, "editable-truss-members.json"));
    Require(!rawTrussMembersText.Contains(@"E:\", StringComparison.OrdinalIgnoreCase) &&
        rawTrussMembersText.Contains(@"桁架1\\4-方管70×70×5-1603.SLDPRT", StringComparison.Ordinal),
        "Bundled editable-truss-members.json still contains hardcoded E:\\ paths.");

    var loadedAll = AppConfigLoader.LoadSettings();
    Require((loadedAll.Llm.Mode is "Cloud" or "Mock") &&
        !string.IsNullOrWhiteSpace(loadedAll.Llm.ApiBaseUrl) &&
        !string.IsNullOrWhiteSpace(loadedAll.Llm.Model) &&
        StartupValidator.Validate().IsValid,
        "AppConfigLoader built-in defaults or StartupValidator failed.");

    // On this PC, ANSYS 18.0 (AWP_ROOT180) must be auto-detected automatically even with empty appsettings.json
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWP_ROOT180")))
    {
        Require(!string.IsNullOrWhiteSpace(loadedAll.Simulation.LauncherPath) &&
            File.Exists(loadedAll.Simulation.LauncherPath) &&
            loadedAll.Simulation.LauncherPath.Contains("v180", StringComparison.OrdinalIgnoreCase),
            "Failed to auto-detect local ANSYS 18.0 via AWP_ROOT180: " + loadedAll.Simulation.LauncherPath);
    }

    // Simulated multi-version ANSYS environment detection (18.0 vs 2021 R2 vs 2024 R2 / 2025 R1)
    var detectEnvMethod = typeof(AnsysInstallationDetector).GetMethod("DetectFromEnvironmentVariables",
        BindingFlags.Static | BindingFlags.NonPublic)!;
    var simEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["AWP_ROOT180"] = @"E:\ANSYS\v180",
        ["AWP_ROOT212"] = @"C:\Program Files\ANSYS Inc\v212",
        ["AWP_ROOT242"] = @"D:\Ansys2024\v242"
    };
    Func<string, bool> fakeExists = p =>
        p.Equals(@"D:\Ansys2024\v242\ANSYS\bin\winx64\launcher.exe", StringComparison.OrdinalIgnoreCase) ||
        p.Equals(@"D:\Ansys2024\v242\ANSYS\bin\winx64\ANSYS242.exe", StringComparison.OrdinalIgnoreCase) ||
        p.Equals(@"E:\ANSYS\v180\ANSYS\bin\winx64\launcher.exe", StringComparison.OrdinalIgnoreCase);
    var detectedMulti = (AnsysInstallationInfo?)detectEnvMethod.Invoke(null, [simEnv, fakeExists]);
    Require(detectedMulti is not null &&
        detectedMulti.VersionTag == "v242" &&
        detectedMulti.LauncherPath.EndsWith(@"v242\ANSYS\bin\winx64\launcher.exe", StringComparison.OrdinalIgnoreCase) &&
        detectedMulti.SolverPath.EndsWith("ANSYS242.exe", StringComparison.OrdinalIgnoreCase),
        "Multi-version ANSYS env detection failed to prefer highest installed version (v242).");

    Require(AnsysInstallationDetector.IsAnsysProcessName("ansys180") &&
        AnsysInstallationDetector.IsAnsysProcessName("ansys242") &&
        AnsysInstallationDetector.IsAnsysProcessName("ansys251") &&
        AnsysInstallationDetector.IsAnsysProcessName("launcher") &&
        AnsysInstallationDetector.IsAnsysProcessName("mapdl") &&
        !AnsysInstallationDetector.IsAnsysProcessName("notepad"),
        "Dynamic ANSYS process-name recognition failed for v180/v242/v251.");

    // Batch wrapper resolution for both ANSYS 18.0 and ANSYS 2024 R2 / 2025 R1
    var wrapperTestDir = Path.Combine(Path.GetTempPath(), "LlmWpfAnsysWrap_" + Guid.NewGuid().ToString("N")[..8]);
    var v242Bin = Path.Combine(wrapperTestDir, "v242", "ANSYS", "bin", "winx64");
    var v180Bin = Path.Combine(wrapperTestDir, "v180", "ANSYS", "bin", "winx64");
    var portableRoot = Path.Combine(wrapperTestDir, "PortableRelease");
    Directory.CreateDirectory(v242Bin);
    Directory.CreateDirectory(v180Bin);
    Directory.CreateDirectory(Path.Combine(portableRoot, "Models", "初始模型", "总装配"));
    try
    {
        File.WriteAllText(Path.Combine(v180Bin, "ANSYS180.exe"), "");
        File.WriteAllText(Path.Combine(v242Bin, "ANSYS242.exe"), "");
        File.WriteAllText(Path.Combine(portableRoot, "Models", "初始模型", "总装配", "总装配体.SLDASM"), "");

        var resolveWrapperMethod = typeof(ApdlBatchRunner).GetMethod("ResolveVersionedBatchWrapper",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var w180 = (string?)resolveWrapperMethod.Invoke(null, [v180Bin, "Mechanical APDL Release 18.0"]);
        var w242 = (string?)resolveWrapperMethod.Invoke(null, [v242Bin, "2024 R2"]);
        Require(w180 is not null && w180.EndsWith("ANSYS180.exe", StringComparison.OrdinalIgnoreCase) &&
            w242 is not null && w242.EndsWith("ANSYS242.exe", StringComparison.OrdinalIgnoreCase),
            $"ResolveVersionedBatchWrapper failed: w180={w180}, w242={w242}");

        var discoverInitialMethod = typeof(WorkspacePathResolver).GetMethod("DiscoverInitialModelFolder",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var discoveredPortable = (string?)discoverInitialMethod.Invoke(null, [@"总装配\总装配体.SLDASM", new[] { portableRoot }]);
        Require(!string.IsNullOrWhiteSpace(discoveredPortable) &&
            discoveredPortable.EndsWith(Path.Combine("Models", "初始模型"), StringComparison.OrdinalIgnoreCase),
            "WorkspacePathResolver failed to discover bundled portable Models\\初始模型 folder.");

        // Out-of-tree build output simulation: <SubRoot>\构建输出\AppBin\Debug\net8.0-windows -> sibling <SubRoot>\反力架\三六重工v2\不伸缩单层检查车\Solidworks模型\初始模型
        var outOfTreeRoot = Path.Combine(wrapperTestDir, "OutOfTreeTest");
        var outOfTreeBin = Path.Combine(outOfTreeRoot, "构建输出", "LlmWpfPrototype_bin", "Debug", "net8.0-windows");
        var siblingInitial = Path.Combine(outOfTreeRoot, "反力架", "三六重工v2", "不伸缩单层检查车", "Solidworks模型", "初始模型", "总装配");
        Directory.CreateDirectory(outOfTreeBin);
        Directory.CreateDirectory(siblingInitial);
        File.WriteAllText(Path.Combine(siblingInitial, "总装配体.SLDASM"), "");
        var discoveredOutOfTree = (string?)discoverInitialMethod.Invoke(null, [@"总装配\总装配体.SLDASM", new[] { outOfTreeBin }]);
        Require(!string.IsNullOrWhiteSpace(discoveredOutOfTree) &&
            discoveredOutOfTree.EndsWith(Path.Combine("Solidworks模型", "初始模型"), StringComparison.OrdinalIgnoreCase),
            "WorkspacePathResolver failed to discover out-of-tree sibling repo 初始模型 folder: " + discoveredOutOfTree);

        // Verify real official build output path on this PC resolves real InitialModelFolder!
        var realOfficialBase = @"E:\反力架\构建输出\LlmWpfPrototype_bin\Debug\net8.0-windows";
        if (Directory.Exists(realOfficialBase))
        {
            var discoveredReal = (string?)discoverInitialMethod.Invoke(null, [@"总装配\总装配体.SLDASM", new[] { realOfficialBase }]);
            Require(!string.IsNullOrWhiteSpace(discoveredReal) &&
                File.Exists(Path.Combine(discoveredReal, @"总装配\总装配体.SLDASM")),
                "WorkspacePathResolver failed to discover real 初始模型 from official build output directory: " + discoveredReal);
        }
    }
    finally
    {
        try { Directory.Delete(wrapperTestDir, true); } catch { }
    }

    Console.WriteLine("PASS cross-machine portability & privacy: zero plaintext leaks, SW 2018-2025+ relative workspace, and ANSYS 18.0-2025+ auto-detection");
}

// =========================================================================
// Multimodal Image Recognition (Method 2 + Max 4 Images) Regression Checks:
//   1. Image preprocessing: oversized >1600px image scaled down cleanly, small crisp PNG stays lossless, path traversal blocked
//   2. Max 4 images limit & zero-typing send enabled when HasPendingImages=true
//   3. OpenAI multimodal payload wire format (content array with text + image_url data URI + detail=high + rule #8 safety prompt)
//   4. Method-2 quick scene preset ("contour") sends UserImages, clears pending queue, renders user bubble thumbnails & persists round-trip
//   5. Image edit safety guard (explicit edit directive vs analysis/negation)
// =========================================================================
{
    var visionTempRoot = Path.Combine(Path.GetTempPath(), "LlmWpfVisionTest_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(visionTempRoot);
    try
    {
        // Create a small crisp PNG (200x80) and an oversized PNG (2400x1200) using WPF imaging
        string CreateSamplePng(string name, int w, int h)
        {
            var stride = w * 3;
            var pixels = new byte[stride * h];
            for (var i = 0; i < pixels.Length; i += 3)
            {
                pixels[i] = 240;
                pixels[i + 1] = 248;
                pixels[i + 2] = 255;
            }
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, pixels, stride);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            var outPath = Path.Combine(visionTempRoot, name);
            using var fs = File.Create(outPath);
            encoder.Save(fs);
            return outPath;
        }

        var smallPng = CreateSamplePng("small_spec.png", 200, 80);
        var bigPng = CreateSamplePng("big_contour.png", 2400, 1200);
        var badFile = Path.Combine(visionTempRoot, "not_an_image.exe");
        File.WriteAllText(badFile, "fake");

        var attachRoot = Path.Combine(visionTempRoot, "attachments");
        var savedSmall = ChatImageAttachmentService.SaveFromFile(smallPng, "conv-vision-test", attachRoot);
        var savedBig = ChatImageAttachmentService.SaveFromFile(bigPng, "conv-vision-test", attachRoot);
        Require(savedSmall.Width == 200 && savedSmall.Height == 80 && savedSmall.MimeType == "image/png",
            $"Small crisp PNG should stay lossless 200x80: {savedSmall.Width}x{savedSmall.Height} {savedSmall.MimeType}");
        Require(Math.Max(savedBig.Width, savedBig.Height) <= ChatImageAttachmentService.MaxLongEdgePixels && savedBig.Width == 1600 && savedBig.Height == 800,
            $"Oversized 2400x1200 image should scale cleanly to 1600x800: {savedBig.Width}x{savedBig.Height}");
        Require(!ChatImageAttachmentService.IsSupportedImageFile(badFile),
            "Unsupported .exe extension should be rejected.");
        Require(ChatImageAttachmentService.TryResolveSafeAttachmentPath("../outside.png", attachRoot) is null,
            "Path traversal in TryResolveSafeAttachmentPath must be blocked.");

        // 2. Verify OpenAI multimodal payload wire format (content array + data URI + detail=high + rule #8)
        var payloads = ChatImageAttachmentService.LoadPayloadsForLlm([savedSmall, savedBig], attachRoot);
        Require(payloads.Count == 2 && payloads[0].DataUri.StartsWith("data:image/png;base64,", StringComparison.Ordinal),
            "LoadPayloadsForLlm did not generate valid base64 data URIs.");
        var realLlmForPayload = new LlmService(configuredLlmOptions, dictionary);
        var buildPayloadMethod = typeof(LlmService).GetMethod("BuildPayloadJsonForTest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        var multimodalJson = (string)buildPayloadMethod.Invoke(realLlmForPayload, [
            "请解读这两张云图",
            Array.Empty<ConversationMessage>(),
            new LlmChatRequestOptions(ReplyOnlyMode: true, IntentLabel: "MultimodalVisionAnalysis", UserImages: payloads),
            true
        ])!;
        using var payloadDoc = JsonDocument.Parse(multimodalJson);
        var messagesArr = payloadDoc.RootElement.GetProperty("messages");
        var sysContent = messagesArr[0].GetProperty("content").GetString() ?? string.Empty;
        var userContent = messagesArr[messagesArr.GetArrayLength() - 1].GetProperty("content");
        Require(sysContent.Contains("多模态工程识图与改模安全护栏", StringComparison.Ordinal) &&
            sysContent.Contains("附带了 2 张工程图片", StringComparison.Ordinal),
            "System prompt did not inject multimodal engineering vision safety guard.");
        Require(userContent.ValueKind == JsonValueKind.Array && userContent.GetArrayLength() == 3 &&
            userContent[0].GetProperty("type").GetString() == "text" &&
            userContent[1].GetProperty("type").GetString() == "image_url" &&
            userContent[1].GetProperty("image_url").GetProperty("url").GetString()!.StartsWith("data:image/", StringComparison.Ordinal) &&
            userContent[1].GetProperty("image_url").GetProperty("detail").GetString() == "high",
            "Multimodal OpenAI payload user.content structure mismatch.");

        // 3. Verify ViewModel 4-image cap, zero-typing send, Method-2 preset, and PersistedChatMessage round-trip
        var visionLlm = new FakeLlm { CustomDesignReply = "已识别云图最大应力 142.6 MPa，满足 Q355B 许用应力要求。" };
        var visionStoreRoot = Path.Combine(visionTempRoot, "proj-store");
        var visionStore = new DesignProjectStoreService(visionStoreRoot, workspaceOptions);
        var visionVm = new MainViewModel(visionLlm, dictionary, new ApdlReplacementService(), service, dispatcher, workspace,
            configuredLlmOptions, chord, members, scans, recommendation, parser, simulation, null, visionStore);

        var fiveFiles = new[] { smallPng, bigPng, smallPng, bigPng, smallPng };
        var addedCount = visionVm.TryAddImageFiles(fiveFiles, attachRoot);
        Require(addedCount == 4 &&
            visionVm.PendingImageAttachments.Count == 4 &&
            visionVm.HasPendingImages &&
            !visionVm.CanAddMoreImages &&
            visionVm.PendingImagesCountText == "已选 4/4 张",
            $"Expected 4-image cap enforcement, got added={addedCount}, pending={visionVm.PendingImageAttachments.Count}");
        Require(string.IsNullOrEmpty(visionVm.UserInput) && visionVm.PrimaryActionCommand.CanExecute(null),
            "PrimaryActionCommand should be enabled for zero-typing image-only submission when HasPendingImages=true.");

        // Remove 3 so 1 remains, then trigger Method-2 "解读云图" quick scene capsule!
        while (visionVm.PendingImageAttachments.Count > 1)
        {
            visionVm.RemovePendingImageCommand.Execute(visionVm.PendingImageAttachments[^1]);
        }
        await visionVm.SendImagePresetCommand.ExecuteAsync("contour");
        Require(!visionVm.HasPendingImages &&
            visionLlm.LastOptions is { ReplyOnlyMode: true, UserImages.Count: 1 } &&
            visionVm.ChatMessages.Any(m => m.IsUser && m.HasAttachedImages && m.AttachedImages.Count == 1 && m.Text == "解读云图") &&
            visionVm.ChatMessages.Last().Text.Contains("142.6 MPa", StringComparison.Ordinal),
            "Method-2 preset 'contour' did not send multimodal UserImages, clear pending strip, or render bubble thumbnails.");

        // 4. Verify PersistedChatMessage round-trips AttachedImages cleanly
        var userImgBubble = visionVm.ChatMessages.Last(m => m.IsUser && m.HasAttachedImages);
        var persistedImgJson = JsonSerializer.Serialize(PersistedChatMessage.FromRuntimeChatMessage(userImgBubble));
        var restoredImgBubble = JsonSerializer.Deserialize<PersistedChatMessage>(persistedImgJson)!.ToRuntimeChatMessage();
        Require(restoredImgBubble.HasAttachedImages &&
            restoredImgBubble.AttachedImages.Count == 1 &&
            restoredImgBubble.AttachedImages[0].FileName == userImgBubble.AttachedImages[0].FileName &&
            !Path.IsPathRooted(restoredImgBubble.AttachedImages[0].RelativePath),
            "PersistedChatMessage did not preserve portable AttachedImages across JSON round-trip.");

        // 5. Verify explicit edit directive guard
        var editDirectiveMethod = typeof(MainViewModel).GetMethod("HasExplicitImageEditDirective", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        Require((bool)editDirectiveMethod.Invoke(null, ["按图修改上弦杆"])!, "Expected explicit image edit directive to return true.");
        Require(!(bool)editDirectiveMethod.Invoke(null, ["帮我看看这张图，先别改"])!, "Negated image message must not trigger edit mode.");
        Require(!(bool)editDirectiveMethod.Invoke(null, ["这张云图超标了吗？"])!, "Question image message must stay ReplyOnly.");

        // 6. Verify Windows clipboard 32-bit Bgra32 (Alpha=0) bug fix:
        //    (a) SaveFromBitmapSource converts dirty zero-alpha Bgra32 to opaque Bgr24 PNG preserving exact RGB
        //    (b) TryLoadBitmap repairs legacy zero-alpha 32-bit PNGs already on disk into opaque Bgr24
        var dirtyW = 64;
        var dirtyH = 32;
        var dirtyPixels = new byte[dirtyW * dirtyH * 4];
        for (var i = 0; i < dirtyPixels.Length; i += 4)
        {
            dirtyPixels[i] = 200;     // B
            dirtyPixels[i + 1] = 100; // G
            dirtyPixels[i + 2] = 40;  // R
            dirtyPixels[i + 3] = 0;   // Dirty Windows GDI Alpha=0
        }
        var dirtyClipboardBmp = System.Windows.Media.Imaging.BitmapSource.Create(
            dirtyW, dirtyH, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, dirtyPixels, dirtyW * 4);
        dirtyClipboardBmp.Freeze();

        var savedClipboard = ChatImageAttachmentService.SaveFromBitmapSource(dirtyClipboardBmp, "剪贴板截图", "conv-alpha-test", attachRoot);
        var loadedClipboard = ChatImageAttachmentService.TryLoadBitmap(savedClipboard.RelativePath, 0, attachRoot);
        Require(loadedClipboard is not null && loadedClipboard.Format == System.Windows.Media.PixelFormats.Bgr24,
            "Saved clipboard screenshot must be opaque Bgr24, got: " + (loadedClipboard?.Format.ToString() ?? "null"));
        var verifyBgr = new byte[dirtyW * dirtyH * 3];
        loadedClipboard!.CopyPixels(verifyBgr, dirtyW * 3, 0);
        Require(verifyBgr[0] == 200 && verifyBgr[1] == 100 && verifyBgr[2] == 40,
            $"Clipboard RGB pixels were altered: ({verifyBgr[0]},{verifyBgr[1]},{verifyBgr[2]})");

        var legacyZeroAlphaPath = Path.Combine(visionTempRoot, "legacy_zero_alpha.png");
        var legacyEnc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        legacyEnc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(dirtyClipboardBmp));
        using (var lfs = File.Create(legacyZeroAlphaPath)) { legacyEnc.Save(lfs); }
        var repairedLegacy = ChatImageAttachmentService.TryLoadBitmap(legacyZeroAlphaPath);
        Require(repairedLegacy is not null && repairedLegacy.Format == System.Windows.Media.PixelFormats.Bgr24,
            "TryLoadBitmap did not repair legacy zero-alpha PNG to opaque Bgr24: " + (repairedLegacy?.Format.ToString() ?? "null"));
        var verifyLegacy = new byte[dirtyW * dirtyH * 3];
        repairedLegacy!.CopyPixels(verifyLegacy, dirtyW * 3, 0);
        Require(verifyLegacy[0] == 200 && verifyLegacy[1] == 100 && verifyLegacy[2] == 40,
            $"Legacy zero-alpha RGB recovery failed: ({verifyLegacy[0]},{verifyLegacy[1]},{verifyLegacy[2]})");
    }
    finally
    {
        try { Directory.Delete(visionTempRoot, true); } catch { }
    }

    Console.WriteLine("PASS multimodal image recognition: preprocessing, 4-image cap, zero-typing send, Method-2 presets, OpenAI image_url payload & persistence");
}

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
    public SolidWorksSessionDiagnostics? CustomDiagnostics { get; set; }
    public Func<string, CancellationToken, Task<bool>>? AsyncOpenHandler { get; set; }
    public string? CustomOpenFailureMessage { get; set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case nameof(ISolidWorksService.GetLastOpenFailureMessage):
                return CustomOpenFailureMessage ?? string.Empty;
            case nameof(ISolidWorksService.GetSessionDiagnostics):
                if (CustomDiagnostics is not null)
                {
                    return CustomDiagnostics;
                }
                if (Opened.Count == 0)
                {
                    return new SolidWorksSessionDiagnostics(
                        SolidWorksSessionState.NotRunning, false, 0, false,
                        string.Empty, string.Empty, false, false, string.Empty, string.Empty, 0,
                        "当前未检测到运行中的 SolidWorks 进程。");
                }
                var lastPath = Opened[^1];
                var lastName = Path.GetFileName(lastPath);
                var isAsm = lastPath.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
                return new SolidWorksSessionDiagnostics(
                    SolidWorksSessionState.ConnectedWorkingAssembly, true, 1, true,
                    lastPath, lastName, isAsm, isAsm, isAsm ? lastPath : string.Empty, string.Empty, 0,
                    $"已连接当前项目工作总装配：{lastName}。");
            case nameof(ISolidWorksService.OpenAssemblyAsync):
                MutatingCalls++;
                var path = (string)args![0]!;
                if (!File.Exists(path)) throw new FileNotFoundException("Fixture was not copied.", path);
                if (AsyncOpenHandler is not null)
                {
                    var ct = args.Length > 2 && args[2] is CancellationToken token ? token : CancellationToken.None;
                    return AsyncOpenHandler(path, ct);
                }
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
    public string? CustomDesignReply { get; set; }
    public Func<string, CancellationToken, Task<string>>? AsyncDesignHandler { get; set; }
    public LlmChatRequestOptions? LastOptions { get; private set; }

    public Task<string> ParseDesignRequirementAsync(string userInput, IReadOnlyList<ConversationMessage>? history = null,
        LlmChatRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        DesignRequests.Add((userInput, history?.ToArray() ?? []));
        LastOptions = options;
        if (AsyncDesignHandler is not null)
        {
            return AsyncDesignHandler(userInput, cancellationToken);
        }
        var answer = !string.IsNullOrWhiteSpace(CustomDesignReply)
            ? CustomDesignReply
            : ForceMixedApdlAction
            ? """{"reply":"正在打开并修改 CAD。","actions":["open_apdl_launcher","update_solidworks_dimensions"],"commands":["open_apdl_launcher","update_solidworks_dimensions"],"parameters":[],"needConfirmation":false,"questions":[]}"""
            : ForceApdlAction || (options?.AllowApdlLauncherAction == true && ApdlLaunchIntentPolicy.IsCandidate(userInput))
            ? """{"reply":"正在打开 ANSYS APDL。","actions":["open_apdl_launcher"],"commands":["open_apdl_launcher"],"parameters":[],"needConfirmation":false,"questions":[]}"""
            : (userInput == "打开当前模型" || userInput.Contains("打开模型", StringComparison.Ordinal)) && options?.ReplyOnlyMode != true
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
