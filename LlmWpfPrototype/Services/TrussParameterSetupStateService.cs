using System.Text;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public enum TrussParameterSetupStage
{
    Stage0_NoSolidWorksOrModel = 0,
    Stage1_ModelOpenedButNotScanned = 1,
    Stage2_ScannedButNotMapped = 2,
    Stage3_PartiallyMapped = 3,
    Stage4_ReadyToModify = 4
}

public sealed class TrussParameterSetupStateSnapshot
{
    public bool HasOpenedSolidWorks { get; init; }

    public bool HasActiveDocument { get; init; }

    public bool HasDimensionScanResult { get; init; }

    public bool HasUpperChordMapping { get; init; }

    public bool HasLowerChordMapping { get; init; }

    public bool HasUpperChordPartialMapping { get; init; }

    public bool HasLowerChordPartialMapping { get; init; }

    public bool IsReadyToModify => HasUpperChordMapping && HasLowerChordMapping;
}

public sealed class TrussParameterSetupStateService
{
    private readonly ISolidWorksService _solidWorksService;
    private readonly DimensionScanCatalogService _dimensionScanCatalogService;
    private readonly EditableTrussMemberCatalogService _editableTrussMemberCatalogService;

    public TrussParameterSetupStateService(
        ISolidWorksService solidWorksService,
        DimensionScanCatalogService dimensionScanCatalogService,
        EditableTrussMemberCatalogService editableTrussMemberCatalogService)
    {
        _solidWorksService = solidWorksService;
        _dimensionScanCatalogService = dimensionScanCatalogService;
        _editableTrussMemberCatalogService = editableTrussMemberCatalogService;
    }

    public TrussParameterSetupStateSnapshot GetCurrentSetupState()
    {
        var upperChord = _editableTrussMemberCatalogService.FindEnabledMemberById(EditableTrussMemberCatalogService.UpperChordMemberId);
        var lowerChord = _editableTrussMemberCatalogService.FindEnabledMemberById(EditableTrussMemberCatalogService.LowerChordMemberId);

        return new TrussParameterSetupStateSnapshot
        {
            HasOpenedSolidWorks = _solidWorksService.IsSolidWorksRunning(),
            HasActiveDocument = _solidWorksService.HasActiveDocument(),
            HasDimensionScanResult = _dimensionScanCatalogService.HasLatestScanResult(),
            HasUpperChordMapping = upperChord is not null && _editableTrussMemberCatalogService.CanExecuteFullSectionUpdate(upperChord),
            HasLowerChordMapping = lowerChord is not null && _editableTrussMemberCatalogService.CanExecuteFullSectionUpdate(lowerChord),
            HasUpperChordPartialMapping = upperChord is not null && _editableTrussMemberCatalogService.HasAnySolidWorksMapping(upperChord),
            HasLowerChordPartialMapping = lowerChord is not null && _editableTrussMemberCatalogService.HasAnySolidWorksMapping(lowerChord)
        };
    }

    public TrussParameterSetupStage GetCurrentSetupStage()
    {
        var state = GetCurrentSetupState();
        return GetCurrentSetupStage(state);
    }

    public string BuildNextStepGuideMessage()
    {
        var state = GetCurrentSetupState();
        return GetCurrentSetupStage(state) switch
        {
            TrussParameterSetupStage.Stage0_NoSolidWorksOrModel => BuildFirstTimeWelcomeMessage(),
            TrussParameterSetupStage.Stage1_ModelOpenedButNotScanned => BuildAfterMappingGuideMessage(),
            TrussParameterSetupStage.Stage2_ScannedButNotMapped => BuildAfterMappingGuideMessage(),
            TrussParameterSetupStage.Stage3_PartiallyMapped => BuildPartialMappingGuideMessage(state),
            TrussParameterSetupStage.Stage4_ReadyToModify => BuildAfterMappingGuideMessage(),
            _ => BuildFirstTimeWelcomeMessage()
        };
    }

    public string BuildFirstTimeWelcomeMessage()
    {
        return """
你好，我可以帮助你打开当前桥检车模型、查询可编辑参数，或根据你提供的参数修改模型尺寸。
""".Trim();
    }

    public string BuildAfterScanGuideMessage()
    {
        return BuildAfterMappingGuideMessage();
    }

    public string BuildAfterMappingGuideMessage()
    {
        return """
当前工作模型已打开。你可以输入“查询可编辑参数”查看支持修改的参数，或直接告诉我要修改的尺寸值。
""".Trim();
    }

    private TrussParameterSetupStage GetCurrentSetupStage(TrussParameterSetupStateSnapshot state)
    {
        if (!state.HasOpenedSolidWorks || !state.HasActiveDocument)
        {
            return TrussParameterSetupStage.Stage0_NoSolidWorksOrModel;
        }

        if (state.IsReadyToModify)
        {
            return TrussParameterSetupStage.Stage4_ReadyToModify;
        }

        if (state.HasUpperChordPartialMapping || state.HasLowerChordPartialMapping)
        {
            return TrussParameterSetupStage.Stage3_PartiallyMapped;
        }

        if (!state.HasDimensionScanResult)
        {
            return TrussParameterSetupStage.Stage1_ModelOpenedButNotScanned;
        }

        return TrussParameterSetupStage.Stage2_ScannedButNotMapped;
    }

    private string BuildPartialMappingGuideMessage(TrussParameterSetupStateSnapshot state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("已根据固定配置完成映射。");
        builder.AppendLine();
        builder.AppendLine("可直接修改尺寸。");
        builder.AppendLine("你可以输入“查询可编辑参数”，或直接告诉我要修改的尺寸值。");
        return builder.ToString().TrimEnd();
    }
}
