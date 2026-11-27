using System.Globalization;

namespace LlmWpfPrototype.Services.Design;

public sealed record GbT6728RoundedSectionResult(
    decimal WidthMm,
    decimal HeightMm,
    decimal ThicknessMm,
    bool IsGeometryValid,
    decimal NominalOuterRadiusMm,
    decimal NominalInnerRadiusMm,
    string LowAlloyCornerRadiusRangeText,
    decimal WallToleranceMm,
    decimal MinAllowableWallMm,
    decimal RoundedAreaCm2,
    decimal RoundedLineMassKgPerM,
    decimal RoundedIxCm4,
    decimal RoundedIyCm4,
    decimal RoundedRxCm,
    decimal RoundedRyCm,
    bool IsStandardCatalogSpec,
    IReadOnlyList<string> NearestStandardSpecs);

/// <summary>
/// Deterministic GB/T 6728-2025 (结构用冷弯型钢) closed square &amp; rectangular hollow section catalog
/// and Appendix A (Table A.2) rounded-corner mechanics + Table 4 / Appendix B (Table B.1) tolerance checker.
/// All values and formulas strictly match the verified local GB_T_6728_2025 knowledge pack.
/// </summary>
public static class GbT6728SectionCatalogService
{
    private sealed record CatalogSpec(
        decimal SideA,
        decimal SideB,
        decimal Wall,
        decimal? TableIxCm4 = null,
        decimal? TableIyCm4 = null);

    // Verified standard catalog specs from GB/T 6728-2025 Table 1 (square) & Table 2 (rectangular)
    // stored with SideA >= SideB so lookups match regardless of B×H vs H×B orientation.
    // Core vehicle specs carry exact printed Table 1/2 Ix/Iy values; non-standard specs compute via exact Appendix A geometry.
    private static readonly CatalogSpec[] VerifiedCatalogSpecs =
    [
        // Table 1: Square hollow sections (40x40 .. 200x200)
        new(40, 40, 3.0m, 9.678m, 9.678m), new(40, 40, 4.0m, 12.194m, 12.194m),
        new(50, 50, 3.0m, 19.821m, 19.821m), new(50, 50, 4.0m, 24.856m, 24.856m), new(50, 50, 4.5m, 27.296m, 27.296m),
        new(60, 60, 3.0m, 35.489m, 35.489m), new(60, 60, 3.5m, 40.190m, 40.190m), new(60, 60, 4.0m, 44.671m, 44.671m), new(60, 60, 4.5m, 48.994m, 48.994m), new(60, 60, 5.0m, 53.228m, 53.228m),
        new(70, 70, 2.5m, 49.581m, 49.581m), new(70, 70, 3.0m, 57.881m, 57.881m), new(70, 70, 3.5m, 65.748m, 65.748m), new(70, 70, 4.0m, 73.240m, 73.240m), new(70, 70, 4.5m, 80.421m, 80.421m), new(70, 70, 5.0m, 87.363m, 87.363m),
        new(75, 75, 3.0m, 71.973m, 71.973m), new(75, 75, 4.0m, 91.307m, 91.307m), new(75, 75, 5.0m, 109.059m, 109.059m),
        new(80, 80, 3.0m, 88.197m, 88.197m), new(80, 80, 3.5m, 100.458m, 100.458m), new(80, 80, 4.0m, 112.163m, 112.163m), new(80, 80, 4.5m, 123.378m, 123.378m), new(80, 80, 5.0m, 134.176m, 134.176m), new(80, 80, 6.0m, 149.240m, 149.240m),
        new(90, 90, 3.0m, 127.637m, 127.637m), new(90, 90, 4.0m, 163.040m, 163.040m), new(90, 90, 5.0m, 195.667m, 195.667m), new(90, 90, 6.0m, 226.146m, 226.146m), new(90, 90, 8.0m, 255.070m, 255.070m),
        new(100, 100, 4.0m, 227.471m, 227.471m), new(100, 100, 5.0m, 273.836m, 273.836m), new(100, 100, 6.0m, 317.143m, 317.143m), new(100, 100, 8.0m, 366.430m, 366.430m), new(100, 100, 10.0m, 412.260m, 412.260m),
        new(120, 120, 4.0m, 403.396m, 403.396m), new(120, 120, 5.0m, 488.208m, 488.208m), new(120, 120, 6.0m, 567.826m, 567.826m), new(120, 120, 8.0m, 717.484m, 717.484m), new(120, 120, 10.0m, 777.990m, 777.990m),
        new(140, 140, 4.0m, 652.736m, 652.736m), new(140, 140, 5.0m, 793.293m, 793.293m), new(140, 140, 6.0m, 926.095m, 926.095m), new(140, 140, 8.0m, 1167.381m, 1167.381m),
        new(150, 150, 5.0m, 984.852m, 984.852m), new(150, 150, 6.0m, 1151.574m, 1151.574m), new(150, 150, 8.0m, 1452.441m, 1452.441m), new(150, 150, 10.0m, 1653.710m, 1653.710m),
        new(160, 160, 5.0m, 1205.090m, 1205.090m), new(160, 160, 8.0m, 1781.843m, 1781.843m), new(160, 160, 10.0m, 2048.850m, 2048.850m),
        new(200, 200, 5.0m, 2412.822m, 2412.822m), new(200, 200, 6.0m, 2838.417m, 2838.417m), new(200, 200, 8.0m, 3606.861m, 3606.861m), new(200, 200, 10.0m, 4350.202m, 4350.202m),

        // Table 2: Rectangular hollow sections (60x40 .. 200x120; TableIxCm4 = strong axis, TableIyCm4 = weak axis)
        new(60, 40, 3.0m, 25.733m, 13.794m), new(60, 40, 4.0m, 32.105m, 17.400m),
        new(70, 50, 3.0m, 44.405m, 26.457m), new(70, 50, 4.0m, 55.795m, 33.341m), new(70, 50, 5.0m, 66.196m, 39.938m),
        new(80, 40, 3.0m, 52.605m, 17.910m), new(80, 40, 4.0m, 65.912m, 22.605m), new(80, 40, 5.0m, 77.842m, 27.326m),
        new(80, 50, 3.0m, 61.503m, 29.775m), new(80, 50, 4.0m, 77.475m, 37.584m), new(80, 50, 5.0m, 91.926m, 45.021m),
        new(80, 60, 3.0m, 70.401m, 45.245m), new(80, 60, 4.0m, 89.038m, 57.236m), new(80, 60, 5.0m, 106.009m, 68.395m),
        new(100, 50, 3.0m, 106.811m, 36.411m), new(100, 50, 4.0m, 135.258m, 46.069m), new(100, 50, 5.0m, 160.919m, 55.188m),
        new(100, 60, 3.0m, 120.929m, 55.001m), new(100, 60, 4.0m, 153.701m, 69.801m), new(100, 60, 5.0m, 183.502m, 83.561m),
        new(100, 80, 3.0m, 149.165m, 105.993m), new(100, 80, 4.0m, 190.586m, 135.288m), new(100, 80, 5.0m, 228.669m, 162.342m),
        new(120, 60, 4.0m, 241.860m, 82.367m), new(120, 60, 5.0m, 289.708m, 98.728m), new(120, 60, 6.0m, 333.682m, 114.438m),
        new(120, 80, 4.0m, 295.705m, 158.414m), new(120, 80, 5.0m, 355.875m, 190.509m), new(120, 80, 6.0m, 411.730m, 220.701m),
        new(140, 80, 4.0m, 430.720m, 181.539m), new(140, 80, 5.0m, 519.793m, 218.676m), new(140, 80, 6.0m, 602.671m, 253.629m),
        new(150, 100, 4.0m, 595.723m, 319.685m), new(150, 100, 5.0m, 721.936m, 386.752m), new(150, 100, 6.0m, 840.354m, 449.863m), new(150, 100, 8.0m, 1048.734m, 576.258m),
        new(160, 80, 4.0m, 598.830m, 204.664m), new(160, 80, 5.0m, 724.423m, 246.842m), new(160, 80, 6.0m, 841.677m, 286.557m), new(160, 80, 8.0m, 1041.832m, 375.555m),
        new(180, 100, 4.0m, 927.162m, 375.013m), new(180, 100, 5.0m, 1126.933m, 454.502m), new(180, 100, 6.0m, 1315.278m, 529.495m), new(180, 100, 8.0m, 1639.099m, 678.082m),
        new(200, 100, 5.0m, 1461.988m, 499.669m), new(200, 100, 6.0m, 1708.977m, 582.583m), new(200, 100, 8.0m, 2131.448m, 745.965m), new(200, 100, 10.0m, 2444.400m, 564.400m),
        new(200, 120, 5.0m, 1652.155m, 752.875m), new(200, 120, 6.0m, 1934.865m, 880.018m), new(200, 120, 8.0m, 2426.531m, 1119.574m)
    ];

    public static GbT6728RoundedSectionResult Evaluate(decimal widthMm, decimal heightMm, decimal thicknessMm)
    {
        var b = (double)widthMm;
        var h = (double)heightMm;
        var t = (double)thicknessMm;
        if (b <= 0 || h <= 0 || t <= 0 || b - 2 * t <= 0 || h - 2 * t <= 0)
        {
            return new GbT6728RoundedSectionResult(
                widthMm, heightMm, thicknessMm, false,
                0m, 0m, string.Empty, 0m, 0m,
                0m, 0m, 0m, 0m, 0m, 0m,
                false, []);
        }

        // GB/T 6728-2025 Appendix A Table A.2 nominal corner radii
        var outerR = t <= 6.0 ? 2.0 * t : (t <= 10.0 ? 2.5 * t : 3.0 * t);
        var maxFeasibleR = Math.Min(b, h) / 2.0;
        if (outerR > maxFeasibleR)
        {
            outerR = maxFeasibleR;
        }
        var innerR = Math.Max(0.0, outerR - t);

        // Exact Appendix A Table A.2 area & moments of inertia
        var areaMm2 = 2.0 * t * (b + h - 2.0 * t) - (4.0 - Math.PI) * (outerR * outerR - innerR * innerR);
        var areaCm2 = areaMm2 / 100.0;
        var massKgPerM = 0.785 * areaCm2;
        var ixCm4 = ComputeRoundedInertiaCm4(b, h, t, outerR, innerR);
        var iyCm4 = ComputeRoundedInertiaCm4(h, b, t, outerR, innerR);
        var rxCm = areaCm2 > 0 ? Math.Sqrt(Math.Max(0.0, ixCm4 / areaCm2)) : 0.0;
        var ryCm = areaCm2 > 0 ? Math.Sqrt(Math.Max(0.0, iyCm4 / areaCm2)) : 0.0;

        var wallTol = ResolveWallToleranceMm(thicknessMm);
        var minWall = Math.Max(0m, thicknessMm - wallTol);
        var cornerRangeText = ResolveLowAlloyCornerRadiusRange(thicknessMm);

        var bigSide = Math.Max(widthMm, heightMm);
        var smallSide = Math.Min(widthMm, heightMm);
        var matchedCatalog = VerifiedCatalogSpecs.FirstOrDefault(s =>
            Math.Abs(s.SideA - bigSide) < 0.01m &&
            Math.Abs(s.SideB - smallSide) < 0.01m &&
            Math.Abs(s.Wall - thicknessMm) < 0.01m);
        var isStandard = matchedCatalog is not null;

        // For standard catalog entries, use exact printed GB/T 6728-2025 Table 1 / Table 2 Ix/Iy
        // aligned with section orientation (height >= width => strong axis Ix; width > height => swapped).
        if (matchedCatalog is { TableIxCm4: { } tabStrong, TableIyCm4: { } tabWeak })
        {
            if (heightMm >= widthMm)
            {
                ixCm4 = (double)tabStrong;
                iyCm4 = (double)tabWeak;
            }
            else
            {
                ixCm4 = (double)tabWeak;
                iyCm4 = (double)tabStrong;
            }
            rxCm = areaCm2 > 0 ? Math.Sqrt(Math.Max(0.0, ixCm4 / areaCm2)) : 0.0;
            ryCm = areaCm2 > 0 ? Math.Sqrt(Math.Max(0.0, iyCm4 / areaCm2)) : 0.0;
        }

        var nearest = isStandard
            ? Array.Empty<string>()
            : VerifiedCatalogSpecs
                .OrderBy(s =>
                {
                    var dA = (double)(s.SideA - bigSide);
                    var dB = (double)(s.SideB - smallSide);
                    var dT = (double)(s.Wall - thicknessMm) * 8.0; // weight wall match strongly
                    return dA * dA + dB * dB + dT * dT;
                })
                .Take(2)
                .Select(FormatCatalogSpec)
                .ToArray();

        return new GbT6728RoundedSectionResult(
            WidthMm: widthMm,
            HeightMm: heightMm,
            ThicknessMm: thicknessMm,
            IsGeometryValid: true,
            NominalOuterRadiusMm: Math.Round((decimal)outerR, 2),
            NominalInnerRadiusMm: Math.Round((decimal)innerR, 2),
            LowAlloyCornerRadiusRangeText: cornerRangeText,
            WallToleranceMm: wallTol,
            MinAllowableWallMm: Math.Round(minWall, 2),
            RoundedAreaCm2: Math.Round((decimal)areaCm2, 3),
            RoundedLineMassKgPerM: Math.Round((decimal)massKgPerM, 3),
            RoundedIxCm4: Math.Round((decimal)ixCm4, 3),
            RoundedIyCm4: Math.Round((decimal)iyCm4, 3),
            RoundedRxCm: Math.Round((decimal)rxCm, 3),
            RoundedRyCm: Math.Round((decimal)ryCm, 3),
            IsStandardCatalogSpec: isStandard,
            NearestStandardSpecs: nearest);
    }

    private static double ComputeRoundedInertiaCm4(double b, double h, double t, double rOut, double rIn)
    {
        var coeffI = (1.0 / 3.0) - (Math.PI / 16.0) - (1.0 / (3.0 * (12.0 - 3.0 * Math.PI)));
        var coeffA = 1.0 - (Math.PI / 4.0);
        var coeffH = (10.0 - 3.0 * Math.PI) / (12.0 - 3.0 * Math.PI);

        var iz = coeffI * Math.Pow(rOut, 4);
        var az = coeffA * rOut * rOut;
        var hz = (h / 2.0) - coeffH * rOut;

        var innerH = h - 2.0 * t;
        var innerB = b - 2.0 * t;
        var iS = coeffI * Math.Pow(rIn, 4);
        var aS = coeffA * rIn * rIn;
        var hS = (innerH / 2.0) - coeffH * rIn;

        var ixMm4 = (b * Math.Pow(h, 3) / 12.0)
            - (innerB * Math.Pow(innerH, 3) / 12.0)
            - 4.0 * (iz + az * hz * hz)
            + 4.0 * (iS + aS * hS * hS);
        return Math.Max(0.0, ixMm4 / 10000.0);
    }

    /// <summary>GB/T 6728-2025 Appendix B Table B.1 wall thickness allowable tolerance (±mm).</summary>
    public static decimal ResolveWallToleranceMm(decimal t) => t switch
    {
        <= 2.0m => 0.23m,
        <= 2.5m => 0.28m,
        <= 3.0m => 0.29m,
        <= 4.0m => 0.30m,
        <= 5.0m => 0.32m,
        <= 6.0m => 0.34m,
        <= 8.0m => 0.39m,
        <= 10.0m => 0.44m,
        <= 12.5m => 0.47m,
        <= 15.0m => 0.51m,
        <= 18.0m => 0.55m,
        _ => 1.10m
    };

    /// <summary>GB/T 6728-2025 Table 4 outer corner radius range for low-alloy steel (ReH &gt; 320 MPa, e.g. Q355B).</summary>
    public static string ResolveLowAlloyCornerRadiusRange(decimal t) => t switch
    {
        <= 3.0m => $"(1.5~2.5)t = {1.5m * t:0.#}~{2.5m * t:0.#} mm",
        <= 6.0m => $"(2.0~3.0)t = {2.0m * t:0.#}~{3.0m * t:0.#} mm",
        <= 10.0m => $"(2.0~3.5)t = {2.0m * t:0.#}~{3.5m * t:0.#} mm",
        _ => $"(2.5~4.0)t = {2.5m * t:0.#}~{4.0m * t:0.#} mm"
    };

    private static string FormatCatalogSpec(CatalogSpec spec) =>
        $"{spec.SideA.ToString("0.#", CultureInfo.InvariantCulture)}×{spec.SideB.ToString("0.#", CultureInfo.InvariantCulture)}×{spec.Wall.ToString("0.#", CultureInfo.InvariantCulture)} mm";
}
