namespace LlmWpfPrototype.Services;

internal sealed record TrussLinkedParameterRule(
    string Name,
    string TriggerMemberId,
    decimal BaseUpperChordSectionMm,
    decimal BaseRedTubeExtrudeLengthMm,
    IReadOnlyList<string> TargetPartFileNames,
    IReadOnlyList<string> DimensionNameCandidates);

internal static class TrussLinkedParameterRules
{
    public const string UpperChordMemberId = "upper_chord";
    public const string UpperChordSectionWidthParameterName = "truss_top_chord_section_width";
    public const string UpperChordSectionHeightParameterName = "truss_top_chord_section_height";
    public const string UpperChordWallThicknessParameterName = "truss_top_chord_wall_thickness";
    public const string LinkedRedTubeLengthParameterName = "upper_chord.linked_red_tube_length";
    public const string LowerSectionLinkedCompensationLengthParameterName = "lower_section.linked_compensation_extrude_length";

    public static readonly TrussLinkedParameterRule UpperChordRedTubeLengthCompensation = new(
        Name: "RedTubeLengthCompensation",
        TriggerMemberId: UpperChordMemberId,
        BaseUpperChordSectionMm: 70m,
        BaseRedTubeExtrudeLengthMm: 1280m,
        TargetPartFileNames:
        [
            "20-鏂圭70脳70脳5-1280.SLDPRT",
            "10-鏂圭70x70x5x1280.SLDPRT",
            "10-鏂圭70脳70脳5脳1280.SLDPRT"
        ],
        DimensionNameCandidates:
        [
            "D1@鎷変几-钖勫1@20-鏂圭70脳70脳5-1280.Part",
            "D1@鎷変几-钖勫1@10-鏂圭70x70x5x1280.Part",
            "D1@鎷変几-钖勫1@10-鏂圭70脳70脳5脳1280.Part",
            "D1@鎷変几-钖勫1",
            "D1@Extrude-Thin1",
            "D1@鍑稿彴-鎷変几1"
        ]);

    public static readonly TrussLinkedParameterRule LowerSectionLinkedPartCompensation = new(
        Name: "LowerSectionLinkedPartCompensation",
        TriggerMemberId: EditableTrussMemberCatalogService.LowerChordMemberId,
        BaseUpperChordSectionMm: 70m,
        BaseRedTubeExtrudeLengthMm: 1281.78m,
        TargetPartFileNames:
        [
            "20-下-方管70×70×5-1280.SLDPRT"
        ],
        DimensionNameCandidates:
        [
            "D1@拉伸-薄壁1@20-下-方管70×70×5-1280.Part",
            "D1@拉伸1@20-下-方管70×70×5-1280.Part",
            "D1@凸台-拉伸1@20-下-方管70×70×5-1280.Part",
            "D1@Boss-Extrude1@20-下-方管70×70×5-1280.Part",
            "D1@Extrude1@20-下-方管70×70×5-1280.Part",
            "D1@拉伸-薄壁1",
            "D1@拉伸1",
            "D1@凸台-拉伸1",
            "D1@Boss-Extrude1",
            "D1@Extrude1"
        ]);
}
