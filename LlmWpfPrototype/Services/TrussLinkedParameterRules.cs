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

    public static readonly TrussLinkedParameterRule UpperChordRedTubeLengthCompensation = new(
        Name: "RedTubeLengthCompensation",
        TriggerMemberId: UpperChordMemberId,
        BaseUpperChordSectionMm: 70m,
        BaseRedTubeExtrudeLengthMm: 1280m,
        TargetPartFileNames:
        [
            "20-方管70×70×5-1280.SLDPRT",
            "10-方管70x70x5x1280.SLDPRT",
            "10-方管70×70×5×1280.SLDPRT"
        ],
        DimensionNameCandidates:
        [
            "D1@拉伸-薄壁1@20-方管70×70×5-1280.Part",
            "D1@拉伸-薄壁1@10-方管70x70x5x1280.Part",
            "D1@拉伸-薄壁1@10-方管70×70×5×1280.Part",
            "D1@拉伸-薄壁1",
            "D1@Extrude-Thin1",
            "D1@凸台-拉伸1"
        ]);
}
