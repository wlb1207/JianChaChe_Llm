using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services;

public class FakeCad : DispatchProxy
{
    public int Updates;
    public string? MissingDimensionPart, UnknownConfigurationPart, ExternalProbePart, AmbiguousPart;
    public decimal CurrentLength = 1280m;
    public decimal CurrentProfileInner = 60m;
    public bool MissingInnerDimension;
    public bool Disconnected;
    public bool AllowStandaloneProbe;
    public IReadOnlyList<SolidWorksDimensionUpdateRequest> Requests = [];
    public List<string> Probes = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch(method!.Name)
        {
            case "ProbeDimensionAsync":
                if (!AllowStandaloneProbe)
                    throw new InvalidOperationException("Upper chord must not open a standalone part to probe dimensions.");
                var standalonePart = (string)args![0]!; var standaloneDim = (string)args[1]!;
                Probes.Add(standalonePart + "|" + standaloneDim);
                var isInner1159 = standaloneDim.StartsWith("D4@草图2@", StringComparison.Ordinal) ||
                                  standaloneDim.StartsWith("D2@草图2@1-方管", StringComparison.Ordinal);
                var isWall = standaloneDim.Contains("拉伸-薄壁", StringComparison.Ordinal);
                return Task.FromResult(new SolidWorksDimensionProbeResult
                {
                    Found = true,
                    PartFilePath = standalonePart,
                    ResolvedDimensionName = standaloneDim,
                    Configuration = "Default",
                    CurrentValue = isWall ? 5m : (isInner1159 ? 60m : 70m)
                });
            case "ReadOpenDimensionAsync":
                var part = (string)args![0]!; var dim = (string)args[1]!;
                Probes.Add(part+"|"+dim);
                if (Disconnected)
                    throw new COMException("RPC call failed", unchecked((int)0x800706BE));
                if (Path.GetFileNameWithoutExtension(part).EndsWith("-379", StringComparison.Ordinal))
                {
                    var inner = dim.StartsWith("D1@草图2@", StringComparison.Ordinal) ||
                                dim.StartsWith("D2@草图2@", StringComparison.Ordinal);
                    var outer = dim.StartsWith("D3@草图1@", StringComparison.Ordinal) ||
                                dim.StartsWith("D1@凸台-拉伸1@", StringComparison.Ordinal);
                    return Task.FromResult(new SolidWorksDimensionProbeResult {
                        Found = outer || (inner && !MissingInnerDimension),
                        PartFilePath = part, ResolvedDimensionName = dim, Configuration = "Default",
                        CurrentValue = inner ? CurrentProfileInner : 70m });
                }
                if (!Fixture.Reds.Any(relative => string.Equals(Path.GetFileName(relative), Path.GetFileName(part), StringComparison.OrdinalIgnoreCase)))
                    return Task.FromResult(new SolidWorksDimensionProbeResult {
                        Found = true, PartFilePath = part, ResolvedDimensionName = dim, Configuration = "Default", CurrentValue = 70m });
                var feature = Path.GetFileName(part).StartsWith("10-") ? "拉伸-薄壁1" : "凸台-拉伸1";
                var exact = dim == $"D1@{feature}@{Path.GetFileNameWithoutExtension(part)}.Part";
                if(AmbiguousPart == Path.GetFileName(part)) exact = true;
                return Task.FromResult(new SolidWorksDimensionProbeResult {
                    Found = exact && MissingDimensionPart != Path.GetFileName(part),
                    PartFilePath = ExternalProbePart == Path.GetFileName(part) ? @"X:\unapproved\part.SLDPRT" : part,
                    ResolvedDimensionName = dim, CurrentValue = CurrentLength,
                    Configuration = UnknownConfigurationPart == Path.GetFileName(part) ? "<unknown>" : "Default" });
            case "UpdateDimensionsAsync": Updates++; Requests=(IReadOnlyList<SolidWorksDimensionUpdateRequest>)args![0]!; return Task.FromResult(true);
            default: throw new InvalidOperationException("Unexpected CAD access: "+method.Name);
        }
    }
}

sealed class Fixture
{
    public readonly string Root, Work;
    public readonly FakeCad Cad;
    public readonly CommandDispatcher Dispatcher;
    public readonly EditableTrussMemberItem Upper;
    public List<string> Log=[];
    public static readonly string[] Reds = [@"桁架1\10-方管70×70×5×1280.SLDPRT", @"桁架2\20-方管70×70×5-1280.SLDPRT"];
    public Fixture(int missing = -1)
    {
        Root=Path.Combine(AppContext.BaseDirectory,"fake-models",Guid.NewGuid().ToString("N"));
        Work=Path.Combine(Root,"work"); Directory.CreateDirectory(Work);
        var service=DispatchProxy.Create<ISolidWorksService,FakeCad>(); Cad=(FakeCad)(object)service;
        var catalog=new EditableTrussMemberCatalogService();
        Upper=catalog.LoadOptions().EditableTrussMembers.Single(x=>x.Id=="upper_chord");
        Upper.RelativePartPath=""; Upper.PartFilePath="";
        for(int i=0;i<Upper.PartMappings.Count;i++)
        {
            var map=Upper.PartMappings[i]; var folder=i%2==0?"桁架1":"桁架2";
            map.PartFilePath=Path.Combine(Work,folder,Path.GetFileName(map.PartFilePath));
            map.RelativePartPath=""; FakeFile(map.PartFilePath);
        }
        for(int i=0;i<Reds.Length;i++) if(i!=missing) FakeFile(Path.Combine(Work,Reds[i]));
        // These must never be substituted for either required exact path.
        FakeFile(Path.Combine(Work,@"桁架2\20-下-方管70×70×5-1280.SLDPRT"));
        foreach(var red in Reds) FakeFile(Path.Combine(Work,"backup",red));
        var workspace=new ModelWorkspaceManager(new SolidWorksWorkspaceOptions {
            InitialModelFolder=Path.Combine(Root,"initial"), WorkingModelFolder=Work,
            ModelRootPath=Root, AssemblyFileName=@"总装配\总装配体.SLDASM" });
        var scan=new DimensionScanCatalogService();
        Dispatcher=new CommandDispatcher(service,workspace,catalog,new TrussMemberCommandParser(catalog),scan,
            new TrussParameterMappingRecommendationService(scan),new TrussParameterSetupStateService(service,scan,catalog));
    }
    public void FakeFile(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path,"FAKE FILE; NOT A SOLIDWORKS MODEL"); }
    public Task<CommandDispatchResult> Run(string instruction="把桁架上弦杆截面改成60x60")=>Dispatcher.ExecuteAsync(new LlmParseResult { Actions=["update_solidworks_dimensions"] },
        Array.Empty<ParameterItem>(),instruction,Log.Add);
}

sealed class FakeTransaction
{
    public double[] Values=[70,70,70,70], Disk=[70,70,70,70];
    public int Writes, Restores, Saves, TargetReads, RebuildCalls;
    public string? Failure;
    public bool ProvidePersistedVerifier = true;
    public bool SideEffectToUnattempted;
    public object? Outcome;
    public List<string> Log=[];
    public bool Run()
    {
        var asm=typeof(SolidWorksService).Assembly;
        var ct=asm.GetType("LlmWpfPrototype.Services.DimensionTransactionChange")!;
        var dt=asm.GetType("LlmWpfPrototype.Services.DimensionTransactionDocument")!;
        var changes=Array.CreateInstance(ct,4); var docs=Array.CreateInstance(dt,2);
        for(int i=0;i<4;i++)
        {
            int index=i;
            changes.SetValue(Activator.CreateInstance(ct,$"D{index}",$"P{index/2}",
                (Func<bool>)(()=>{ Writes++; Values[index]=60; if(SideEffectToUnattempted && index==0) Values[3]=60; if(Failure=="throw-write" && index==2) throw new COMException("test COM failure",unchecked((int)0x80004005)); if(Failure=="rpc-write" && index==1) throw new COMException("test RPC disconnect",unchecked((int)0x800706BE)); return !(Failure=="write" && index==2); }),
                (Func<bool>)(()=>{ Restores++; if(Failure=="rollback" && index==1) return false; Values[index]=70; return true; }),
                (Func<bool>)(()=>{ TargetReads++; return !(Failure=="readback" && index==2) && Values[index]==60; }),
                (Func<bool>)(()=>Values[index]==70)),i);
        }
        for(int i=0;i<2;i++)
        {
            int index=i;
            var document=Activator.CreateInstance(dt,$"P{index}",
                (Func<bool>)(()=>Failure!="preflight" || index!=1),
                (Func<bool>)(()=>{ RebuildCalls++; return !((Failure is "rebuild" or "rollback") && index==1 && RebuildCalls==2); }),
                (Func<bool>)(()=>{ Saves++; Disk[index*2]=Values[index*2]; Disk[index*2+1]=Values[index*2+1];
                    if(Failure=="post-save" && Saves==2) Values[1]=61;
                    return !((Failure is "save" or "rollback-save" or "missing-disk-verifier") && Saves==2)
                        && !(Failure=="rollback-save" && Saves>=3); }));
            if(ProvidePersistedVerifier)
            {
                dt.GetProperty("VerifyPersistedTarget")!.SetValue(document,(Func<bool>)(()=>Disk[index*2]==60 && Disk[index*2+1]==60));
                dt.GetProperty("VerifyPersistedOriginal")!.SetValue(document,(Func<bool>)(()=>Failure!="rollback-disk-read" && Disk[index*2]==70 && Disk[index*2+1]==70));
            }
            docs.SetValue(document,i);
        }
        Outcome=asm.GetType("LlmWpfPrototype.Services.DimensionTransaction")!.GetMethod("ExecuteDetailed",BindingFlags.Static|BindingFlags.NonPublic)!
            .Invoke(null,[changes,docs,(Action<string>)Log.Add,CancellationToken.None])!;
        return (bool)Outcome.GetType().GetProperty("Succeeded")!.GetValue(Outcome)!;
    }
}

static class Program
{
    static int Pass,Fail;
    static void Assert(bool b,string message) { if(!b) throw new Exception(message); }
    static async Task Test(string name,Func<Task> test) {try{await test(); Pass++; Console.WriteLine("PASS "+name);}catch(Exception ex){Fail++;Console.WriteLine("FAIL "+name+": "+ex);}}
    static Task Sync(Action action) { action(); return Task.CompletedTask; }
    [STAThread] static async Task<int> Main()
    {
        Console.WriteLine("Actual production DLL: "+typeof(CommandDispatcher).Assembly.Location);
        await Test("copied assembly references rebind recursively inside the working tree", () => Sync(() =>
        {
            var root = Path.Combine(AppContext.BaseDirectory, "reference-fixture", Guid.NewGuid().ToString("N"));
            var initial = Path.Combine(root, "initial");
            var working = Path.Combine(root, "working");
            var assembly = Path.Combine(working, "总装配", "总装配体.SLDASM");
            var child = Path.Combine(working, "桁架1", "桁架1.SLDASM");
            var part = Path.Combine(working, "桁架1", "part.SLDPRT");
            foreach (var file in new[] { assembly, child, part })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "test only");
            }
            var references = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase)
            {
                [assembly] = ["桁架1", Path.Combine(initial, "桁架1", "桁架1.SLDASM")],
                [child] = ["part", Path.Combine(initial, "桁架1", "part.SLDPRT")]
            };
            var calls = new List<string>();
            var count = CadReferenceBoundary.RebindWorkingAssembly(assembly, working, initial,
                owner => references[owner],
                (owner, oldPath, newPath) =>
                {
                    calls.Add(owner + "|" + oldPath + "|" + newPath);
                    references[owner][1] = newPath;
                    return true;
                },
                _ => false, _ => { });
            Assert(count == 2 && calls.Count == 2, "did not rewrite all copied assembly levels");
            Assert(references[assembly][1] as string == child && references[child][1] as string == part,
                "wrong references after rebind");
        }));
        await Test("eight outer dimensions, two derived inner dimensions and both red lengths in one plan",async()=>{
            var f=new Fixture();var r=await f.Run();Assert(r.Succeeded && f.Cad.Updates==1,"dispatch failure");
            Assert(f.Cad.Requests.Count==12,"expected 8 outer + 2 inner + 2 red dimensions");
            const string part = "@2-方管70×70×5-379.Part";
            var profile = f.Cad.Requests.Where(x=>x.PartFilePath.EndsWith("-379.SLDPRT", StringComparison.Ordinal)).ToDictionary(x=>x.DimensionName,x=>decimal.Parse(x.Value,System.Globalization.CultureInfo.InvariantCulture));
            Assert(profile.Count == 4 && profile["D3@草图1"+part] == 60m && profile["D1@凸台-拉伸1"+part] == 60m &&
                profile["D1@草图2"+part] == 50m && profile["D2@草图2"+part] == 50m,"379 profile");
            var reds=f.Cad.Requests.Where(x=>x.ParameterName=="upper_chord.linked_red_tube_length").ToList();
            Assert(reds.Count==2 && reds.All(x=>x.Value=="1290" && x.Configuration=="Default"),"linked plan");
            Assert(reds.Select(x=>x.DimensionName).Distinct().Count()==2,"independent dimensions");
            Assert(!reds.Any(x=>x.PartFilePath.Contains("backup")||x.PartFilePath.Contains("20-下-")),"wrong component");
        });
        await Test("preserves a measured wall other than 5 mm",async()=>{
            var f=new Fixture();f.Cad.CurrentProfileInner=58m;var r=await f.Run();
            var inner=f.Cad.Requests.Where(x=>x.PartFilePath.EndsWith("-379.SLDPRT",StringComparison.Ordinal) && x.DimensionName.Contains("@草图2@")).ToList();
            Assert(r.Succeeded && inner.Count==2 && inner.All(x=>decimal.Parse(x.Value,System.Globalization.CultureInfo.InvariantCulture)==48m),"wall was not preserved");
        });
        await Test("missing 379 inner dimension => zero writes",async()=>{
            var f=new Fixture();f.Cad.MissingInnerDimension=true;var r=await f.Run();
            Assert(!r.Succeeded && f.Cad.Updates==0,"incomplete 379 profile was dispatched");
        });
        for(int missing=0;missing<2;missing++) { int m=missing;await Test("missing required part "+m+" => zero execution calls",async()=>{var f=new Fixture(m);var r=await f.Run();Assert(!r.Succeeded && f.Cad.Updates==0,"partial update");}); }
        foreach(var red in Fixture.Reds) { var name=Path.GetFileName(red);
            await Test("missing length dimension "+name+" => zero execution calls",async()=>{var f=new Fixture();f.Cad.MissingDimensionPart=name;var r=await f.Run();Assert(!r.Succeeded && f.Cad.Updates==0,"partial update");});
        }
        await Test("unknown configuration => zero execution calls",async()=>{var f=new Fixture();f.Cad.UnknownConfigurationPart=Path.GetFileName(Fixture.Reds[1]);var r=await f.Run();Assert(!r.Succeeded&&f.Cad.Updates==0,"config not blocked");});
        await Test("probe outside approved path => zero execution calls",async()=>{var f=new Fixture();f.Cad.ExternalProbePart=Path.GetFileName(Fixture.Reds[0]);var r=await f.Run();Assert(!r.Succeeded&&f.Cad.Updates==0,"external path accepted");});
        await Test("ambiguous length dimensions => zero execution calls",async()=>{var f=new Fixture();f.Cad.AmbiguousPart=Path.GetFileName(Fixture.Reds[1]);var r=await f.Run();Assert(!r.Succeeded&&f.Cad.Updates==0,"ambiguous accepted");});
        await Test("same instruction does not accumulate compensation",async()=>{var f=new Fixture();await f.Run();f.Cad.CurrentLength=1290m;var r=await f.Run();Assert(r.Succeeded&&f.Cad.Requests.Where(x=>x.ParameterName=="upper_chord.linked_red_tube_length").All(x=>x.Value=="1290"),"accumulated compensation");});
        await Test("missing main mapping => zero execution calls",async()=>{var f=new Fixture();f.Upper.PartMappings[2].HeightDimensionName="";var r=await f.Run();Assert(!r.Succeeded&&f.Cad.Updates==0,"incomplete main plan");});
        await Test("assembly RPC failure aborts reads and writes", async () =>
        {
            var f = new Fixture(); f.Cad.Disconnected = true;
            var result = await f.Run();
            Assert(!result.Succeeded && f.Cad.Probes.Count == 1 && f.Cad.Updates == 0, "continued after losing assembly connection");
        });
        await Test("legacy absolute part outside working scope is not used as fallback",async()=>{
            var f=new Fixture();
            var outside=Path.Combine(f.Root,"outside","新模型","桁架1","fixture.SLDPRT");
            f.FakeFile(outside);
            f.Upper.PartMappings[0].PartFilePath=outside;
            var r=await f.Run();
            Assert(!r.Succeeded && f.Cad.Updates==0 && f.Cad.Probes.Count==0,"external file entered plan or probe");
        });
        await Test("production transaction success includes readback before and after save",()=>Sync(()=>{var f=new FakeTransaction();Assert(f.Run()&&f.TargetReads==8&&f.Saves==2&&f.Disk.All(x=>x==60),"success criteria");}));
        await Test("preflight failure never calls setter or rollback setter",()=>Sync(()=>{var f=new FakeTransaction{Failure="preflight"};Assert(!f.Run()&&f.Writes==0&&f.Restores==0&&f.Saves==0,"validation wrote values");}));
        await Test("RPC disconnect during write stops the transaction immediately", () => Sync(() =>
        {
            var f = new FakeTransaction { Failure = "rpc-write" };
            Assert(!f.Run() && f.Writes == 2 && f.Restores == 0 && f.RebuildCalls == 0 && f.Saves == 0,
                "transaction attempted more CAD operations after losing RPC connection");
        }));
        foreach(var failure in new[]{"write","throw-write","rebuild","readback","save","post-save"}) {
            await Test(failure+" failure restores all attempted values and reports failure",()=>Sync(()=>{
                var f=new FakeTransaction{Failure=failure};Assert(!f.Run(),"false success");
                Assert(f.Values.All(x=>x==70)&&f.Disk.All(x=>x==70),"restoration incomplete");
                Assert(f.Log.Any(x=>x.Contains("Rollback completed. Verified=True")),"unverified restoration");
                if(failure=="throw-write")Assert(f.Log.Any(x=>x.Contains("0x80004005")),"missing HRESULT");
                if(failure=="save")Assert(f.Saves==4,"failed save attempt not restored on disk");
            }));
        }
        await Test("setter side effect on an unattempted planned dimension is restored",()=>Sync(()=>{
            var f=new FakeTransaction{Failure="write",SideEffectToUnattempted=true};
            Assert(!f.Run()&&f.Values.All(x=>x==70),"unattempted side effect was not restored");
            Assert(f.Log.Any(x=>x.Contains("Rollback completed. Verified=True")),"side-effect recovery was not verified");
        }));
        await Test("rollback failure is explicit; never claims success",()=>Sync(()=>{var f=new FakeTransaction{Failure="rollback"};Assert(!f.Run()&&f.Values.Any(x=>x!=70),"not exercising failed rollback");Assert(f.Log.Any(x=>x.Contains("Verified=False")&&x.Contains("ManualInspectionRequired=True")),"rollback failure concealed");Assert(!f.Log.Any(x=>x.Contains("Transactional dimension update completed")),"false success");}));
        await Test("rollback save failure: memory restored, disk unknown, never success",()=>Sync(()=>{
            var f=new FakeTransaction{Failure="rollback-save"};Assert(!f.Run()&&f.Values.All(x=>x==70),"memory recovery failed");
            Assert(f.Log.Any(x=>x.Contains("MemoryVerified=True")&&x.Contains("Verified=False")&&x.Contains("UnknownPartialSave")),"disk recovery overstated");
        }));
        await Test("rollback saves acknowledged without persisted verifier remain unknown",()=>Sync(()=>{
            var f=new FakeTransaction{Failure="missing-disk-verifier",ProvidePersistedVerifier=false};Assert(!f.Run()&&f.Values.All(x=>x==70),"recovery not exercised");
            Assert(f.Log.Any(x=>x.Contains("RestoreSaveAcknowledgedNotReopened")&&x.Contains("Verified=False")),"in-memory read passed as disk evidence");
        }));
        await Test("missing reopen verifier cannot report successful modification",()=>Sync(()=>{
            var f=new FakeTransaction{ProvidePersistedVerifier=false};Assert(!f.Run()&&f.Disk.All(x=>x==60),"saved pending state not exercised");
            Assert(!f.Log.Any(x=>x.Contains("Transactional dimension update completed")),"false successful completion");
        }));
        await Test("recovery guard survives new service and blocks repeat transaction",()=>Sync(()=>{
            var root=Path.Combine(AppContext.BaseDirectory,"recovery-checks",Guid.NewGuid().ToString("N"));
            var initial=Path.Combine(root,"initial");var work=Path.Combine(root,"working");Directory.CreateDirectory(initial);Directory.CreateDirectory(work);
            var part=Path.Combine(work,"part.SLDPRT");File.WriteAllText(part,"OFFLINE NOT A MODEL");WorkspaceSafetyPolicy.MarkOwned(initial,work);
            var guard=new DimensionRecoveryGuard(initial,work);guard.Begin([part]);
            var blocked=false;try{new DimensionRecoveryGuard(initial,work).RequireClear();}catch(InvalidOperationException ex){blocked=ex.Message.Contains("状态未知");}
            Assert(blocked&&File.Exists(guard.MarkerPath),"restart lost recovery block");
            guard.CompleteVerified();guard.RequireClear();Assert(File.Exists(part),"guard removed model fixture");
        }));
        await Test("lower_truss resolves working model path, updates 1159+20-下+1530, and ignores stray 初始模型 subfolder", async () =>
        {
            var f = new Fixture();
            f.Cad.AllowStandaloneProbe = true;
            var primary1159 = Path.Combine(f.Work, @"桁架2\1-方管70×70×5-1159.SLDPRT");
            var linked1530 = Path.Combine(f.Work, @"桁架1\1-方管70×70×5-1530.SLDPRT");
            var stray20Lower = Path.Combine(f.Work, @"初始模型\桁架2\20-下-方管70×70×5-1280.SLDPRT");
            f.FakeFile(primary1159);
            f.FakeFile(linked1530);
            f.FakeFile(stray20Lower);

            var r = await f.Run("把下桁架改成 60x60x6");
            Assert(r.Succeeded && f.Cad.Updates == 1, "lower_truss dispatch failed: " + string.Join(" | ", r.AssistantMessages));
            Assert(f.Cad.Requests.Count == 9, $"expected 4 (1159) + 2 (20-下) + 3 (1530) = 9 requests, got {f.Cad.Requests.Count}");
            Assert(f.Cad.Requests.All(req => req.PartFilePath.StartsWith(f.Work, StringComparison.OrdinalIgnoreCase)), "request escaped working folder");
            Assert(!f.Cad.Requests.Any(req => req.PartFilePath.Contains("初始模型", StringComparison.OrdinalIgnoreCase)), "stray 初始模型 copy was selected");
            var innerRequests = f.Cad.Requests.Where(req => req.PartFilePath == primary1159 && (req.DimensionName.StartsWith("D4@") || req.DimensionName.StartsWith("D2@"))).ToList();
            Assert(innerRequests.Count == 2 && innerRequests.All(req => req.Value == "48"), "expected derived inner size 60 - 2*6 = 48");
        });
        await Test("lower_truss section-only edit preserves existing 5mm wall thickness", async () =>
        {
            var f = new Fixture();
            f.Cad.AllowStandaloneProbe = true;
            f.FakeFile(Path.Combine(f.Work, @"桁架2\1-方管70×70×5-1159.SLDPRT"));
            f.FakeFile(Path.Combine(f.Work, @"桁架1\1-方管70×70×5-1530.SLDPRT"));

            var r = await f.Run("把下桁架截面改成 60x60");
            Assert(r.Succeeded && f.Cad.Updates == 1, "lower_truss section-only dispatch failed: " + string.Join(" | ", r.AssistantMessages));
            var innerRequests = f.Cad.Requests.Where(req => req.PartFilePath.EndsWith("1-方管70×70×5-1159.SLDPRT", StringComparison.OrdinalIgnoreCase) &&
                (req.DimensionName.StartsWith("D4@") || req.DimensionName.StartsWith("D2@"))).ToList();
            Assert(innerRequests.Count == 2 && innerRequests.All(req => req.Value == "50"), "expected preserved wall 60 - 2*5 = 50");
        });
        await Test("lower_chord updates 3 main parts + linked 1159 + real 20-下 + linked 1530 and ignores stray 初始模型", async () =>
        {
            var f = new Fixture();
            f.Cad.AllowStandaloneProbe = true;
            f.FakeFile(Path.Combine(f.Work, @"桁架1\34-方管70×70×5×6575.SLDPRT"));
            f.FakeFile(Path.Combine(f.Work, @"桁架1\33-方管70×70×5×838.SLDPRT"));
            f.FakeFile(Path.Combine(f.Work, @"桁架2\10-方管70×70×5-6375.SLDPRT"));
            f.FakeFile(Path.Combine(f.Work, @"桁架2\1-方管70×70×5-1159.SLDPRT"));
            f.FakeFile(Path.Combine(f.Work, @"桁架1\1-方管70×70×5-1530.SLDPRT"));
            f.FakeFile(Path.Combine(f.Work, @"初始模型\桁架2\20-下-方管70×70×5-1280.SLDPRT"));

            var r = await f.Run("把桁架下弦杆截面改成 65x65x4");
            Assert(r.Succeeded && f.Cad.Updates == 1, "lower_chord dispatch failed: " + string.Join(" | ", r.AssistantMessages));
            Assert(f.Cad.Requests.Count == 18, $"expected 9 (3 main) + 4 (1159) + 2 (20-下) + 3 (1530) = 18 requests, got {f.Cad.Requests.Count}");
            Assert(f.Cad.Requests.All(req => req.PartFilePath.StartsWith(f.Work, StringComparison.OrdinalIgnoreCase)), "lower_chord request escaped working folder");
            Assert(!f.Cad.Requests.Any(req => req.PartFilePath.Contains("初始模型", StringComparison.OrdinalIgnoreCase)), "stray 初始模型 copy was selected");
        });
        Console.WriteLine($"RESULT Passed={Pass} Failed={Fail}; fake files and CAD delegates only; no network or SolidWorks started");
        return Fail==0?0:1;
    }
}
