using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services.Design;
using LlmWpfPrototype.Services.Rag;

namespace LlmWpfPrototype.Services.Project;

/// <summary>
/// Pure .NET OOXML (.docx) engineering report generator with zero external dependencies.
/// Produces a styled Word document containing project metadata, dual sharp/rounded section mechanics tables,
/// 6-standard deterministic boundary checklists, embedded high-resolution vector PNG diagrams,
/// version checkpoints, and conversation archive.
/// </summary>
public static class DesignReportWordExporter
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace CtNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    private sealed record EmbeddedImage(string RelId, string ZipMediaPath, string FullPath, string Caption, int WidthPx, int HeightPx, int DrawingId);

    public static string Export(
        DesignProjectRecord project,
        IReadOnlyList<(DesignConversationSummary Summary, DesignConversationRecord? Record)> conversations,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var safeName = string.Concat(project.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var reportPath = Path.Combine(outputDirectory, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.docx");

        var profile = DesignBoundaryProfileLoader.Load();
        var upperEval = DesignBoundaryEvaluationService.EvaluateMemberSection(
            project.UpperChord.WidthMm, project.UpperChord.HeightMm, project.UpperChord.ThicknessMm,
            DesignBoundaryMemberRole.UpperChord, project.UpperChord, profile);
        var lowerEval = DesignBoundaryEvaluationService.EvaluateMemberSection(
            project.LowerChord.WidthMm, project.LowerChord.HeightMm, project.LowerChord.ThicknessMm,
            DesignBoundaryMemberRole.LowerChord, project.LowerChord, profile);

        var embeddedImages = DiscoverKeyReportFigures();

        var body = new XElement(W + "body");

        // Cover Header
        body.Add(CreateParagraph($"桥梁检查车智能设计计算与方案报告", fontSizeHalfPt: 34, bold: true, colorHex: "0F172A", align: "center", spaceAfter: 120));
        body.Add(CreateParagraph($"项目名称：{project.Name}  ·  当前版本：{project.VersionTag}  ·  车型：{profile.Vehicle.VehicleName}（{profile.Vehicle.StandardModelCode}）",
            fontSizeHalfPt: 21, bold: false, colorHex: "334155", align: "center", spaceAfter: 240));

        // Project Metadata Table
        body.Add(CreateTable(
            ["项目属性", "当前内容", "项目属性", "当前内容"],
            [
                ["项目编号", project.Id, "标准车型归类", $"{profile.Vehicle.StandardModelCode}（{profile.Vehicle.MaterialGrade}）"],
                ["当前版本", project.VersionTag, "整车跨度 / 悬臂", $"{profile.Vehicle.TotalSpanMm / 1000m:0.00} m / 两侧 {profile.Vehicle.CantileverLengthMm / 1000m:0.00} m"],
                ["创建时间", project.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "最后更新", project.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)],
                ["累计交互 / 改模", $"{project.Metrics.TotalUserTurns} 轮 / {project.Metrics.ConfirmedCadModifications} 次确认改模", "设计目标备注", string.IsNullOrWhiteSpace(project.DesignGoalSummary) ? "常规结构优化" : project.DesignGoalSummary]
            ],
            colWidthsDxa: [1600, 2900, 1600, 2900]));

        // Section 1: Dual Mechanics Comparison Table
        body.Add(CreateHeading("一、当前结构截面与双口径力学指标对比（锐角 CAD vs GB/T 6728-2025 含圆角）"));
        body.Add(CreateParagraph("注：锐角口径用于与 SolidWorks 简化实体直接对照；GB/T 6728-2025 含公称外圆弧 R 口径用于与国标型钢手册及有限元精确建参核对。",
            fontSizeHalfPt: 18, bold: false, colorHex: "475569", spaceAfter: 100));

        var mechRows = new List<string[]>();
        foreach (var (member, eval) in new[] { (project.UpperChord, upperEval), (project.LowerChord, lowerEval) })
        {
            var m = member.Mechanics;
            var r = eval.RoundedSection;
            mechRows.Add([
                member.MemberName,
                $"{member.SpecText} mm",
                r is { IsStandardCatalogSpec: true } ? "国标常用" : "非标/协议",
                r is not null ? $"{r.RoundedLineMassKgPerM:0.000}" : $"{m.LineMassKgPerM:0.000}",
                $"{m.LineMassKgPerM:0.000} ({RectangularTubeMechanicsResult.FormatSignedPercent(m.MassDeltaPercentVsBase)})",
                r is not null ? $"{r.RoundedIxCm4:0.000}" : $"{m.IxMm4 / 10000m:0.000}",
                r is not null ? $"R={r.NominalOuterRadiusMm:0.#} / 下限 {r.MinAllowableWallMm:0.00}" : "-",
                eval.HasBoundaryWarning ? "含风险提示" : "初检合格"
            ]);
        }
        body.Add(CreateTable(
            ["构件", "当前规格", "GB/T 6728", "含圆角重(kg/m)", "CAD线重(变化)", "含圆角Ix(cm⁴)", "圆角/壁厚下限(mm)", "边界初检"],
            mechRows,
            colWidthsDxa: [950, 1180, 960, 1150, 1380, 1120, 1360, 900]));

        // Section 2: 6-Standard Deterministic Boundary Checklist
        body.Add(CreateHeading("二、六大核心工程规范边界核对清单"));
        var sixStdRows = new List<string[]>();
        foreach (var eval in new[] { upperEval, lowerEval })
        {
            sixStdRows.Add([eval.MemberDisplayName, "JT/T 1430-2022 & 模型联动", eval.ModelLinkageSummary]);
            sixStdRows.Add([eval.MemberDisplayName, "GB/T 3811-2008（最小壁厚）", eval.CorrosionWallSummary]);
            sixStdRows.Add([eval.MemberDisplayName, "GB 50017-2017（宽厚比/稳定）", eval.WidthThicknessSummary]);
            sixStdRows.Add([eval.MemberDisplayName, "GB/T 6728-2025（冷弯型钢）", eval.StandardSectionSummary]);
            sixStdRows.Add([eval.MemberDisplayName, "GB 50661-2011（焊接构造）", eval.WeldingDetailingSummary]);
            sixStdRows.Add([eval.MemberDisplayName, "GB/T 1591-2018（材质/冲击）", eval.SteelMaterialSummary]);
            sixStdRows.Add([eval.MemberDisplayName, "ANSYS APDL 仿真必复核红线", eval.SimulationChecklistSummary]);
        }
        body.Add(CreateTable(
            ["核对构件", "对应工程规范与主题", "确定性核对结果与工程依据"],
            sixStdRows,
            colWidthsDxa: [1100, 2250, 5650]));

        // Section 3: Embedded High-Res Vector Engineering Diagrams
        if (embeddedImages.Count > 0)
        {
            body.Add(CreateHeading("三、本车结构联动与核心规范高清矢量附图"));
            foreach (var img in embeddedImages)
            {
                body.Add(CreateImageParagraph(img));
                body.Add(CreateParagraph(img.Caption, fontSizeHalfPt: 18, bold: false, colorHex: "475569", align: "center", spaceAfter: 160));
            }
        }

        // Section 4: Version Evolution Timeline
        body.Add(CreateHeading("四、设计方案版本演进时间线（Checkpoints）"));
        var versionRows = project.VersionHistory.Select(cp => new[]
        {
            cp.VersionTag,
            cp.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            cp.Summary,
            cp.UpperChord.SpecText,
            cp.LowerChord.SpecText,
            cp.CadExecutionElapsedMs > 0 ? $"{cp.CadExecutionElapsedMs} ms" : "-"
        }).ToList();
        body.Add(CreateTable(
            ["版本", "时间", "变更摘要", "上弦杆 (mm)", "下弦杆 (mm)", "CAD耗时"],
            versionRows,
            colWidthsDxa: [760, 1450, 3250, 1280, 1280, 980]));

        // Section 5: Conversation Archive
        body.Add(CreateHeading("五、多轮设计对话与规范查证归档"));
        foreach (var (summary, record) in conversations)
        {
            body.Add(CreateParagraph($"对话：{summary.Title}（更新于 {summary.UpdatedAt:yyyy-MM-dd HH:mm}）",
                fontSizeHalfPt: 22, bold: true, colorHex: "0369A1", spaceBefore: 140, spaceAfter: 60));
            if (record is null || record.Messages.Count == 0)
            {
                body.Add(CreateParagraph("（无对话消息记录）", fontSizeHalfPt: 19, bold: false, colorHex: "64748B", spaceAfter: 80));
                continue;
            }

            foreach (var msg in record.Messages.Where(m => !m.IsSystemWelcomeAnchor))
            {
                var rolePrefix = $"[{msg.Timestamp:HH:mm:ss}] {msg.RoleDisplay}：";
                var cleanText = SanitizeXmlText(msg.Text);
                body.Add(CreateLabeledParagraph(rolePrefix, cleanText, msg.IsUser ? "0369A1" : "0F172A"));
            }
        }

        // Page margins & A4 size
        body.Add(new XElement(W + "sectPr",
            new XElement(W + "pgSz", new XAttribute(W + "w", 11906), new XAttribute(W + "h", 16838)),
            new XElement(W + "pgMar",
                new XAttribute(W + "top", 1134),
                new XAttribute(W + "right", 1134),
                new XAttribute(W + "bottom", 1134),
                new XAttribute(W + "left", 1134),
                new XAttribute(W + "header", 720),
                new XAttribute(W + "footer", 720),
                new XAttribute(W + "gutter", 0))));

        var documentXml = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(W + "document",
                new XAttribute(XNamespace.Xmlns + "w", W),
                new XAttribute(XNamespace.Xmlns + "r", R),
                new XAttribute(XNamespace.Xmlns + "wp", Wp),
                new XAttribute(XNamespace.Xmlns + "a", A),
                new XAttribute(XNamespace.Xmlns + "pic", Pic),
                body));

        WriteDocxPackage(reportPath, documentXml, embeddedImages);
        return reportPath;
    }

    private static List<EmbeddedImage> DiscoverKeyReportFigures()
    {
        var kbRoot = new KnowledgeBaseLoader().ResolveKnowledgeBaseRootPath();
        var candidates = new (string RelPath, string Caption)[]
        {
            ("JC_GD_NG/assets/fig_jc_gd_ng_chord_linkage.png", "图 1  本车 JC-GD-NG 主桁架弦杆截面与横向红管补偿联动断面图"),
            ("JT_T_1430_2022/assets/fig_jc_gd_ng_deflection_and_test.png", "图 2  本车 JC-GD-NG 总体构型、7.35 m 悬臂静挠度与静动载试验总图（JT/T 1430-2022）"),
            ("GB_T_6728_2025/assets/fig_rhs_section_and_tolerances.png", "图 3  冷弯闭口方矩管截面符号、外圆弧 R 与常用规格对比图（GB/T 6728-2025）")
        };

        var result = new List<EmbeddedImage>();
        var idx = 1;
        foreach (var (rel, caption) in candidates)
        {
            var full = Path.Combine(kbRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;
            var (w, h) = ReadPngDimensions(full);
            result.Add(new EmbeddedImage($"rIdImg{idx}", $"word/media/figure_{idx}.png", full, caption, w, h, idx));
            idx++;
        }

        return result;
    }

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            if (fs.Read(header) == 24 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            {
                var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                if (width > 0 && height > 0) return (width, height);
            }
        }
        catch
        {
        }

        return (1600, 900);
    }

    private static void WriteDocxPackage(string path, XDocument documentXml, IReadOnlyList<EmbeddedImage> images)
    {
        var tempPath = path + ".tmp";
        using (var fs = File.Create(tempPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            // [Content_Types].xml
            var contentTypes = new XDocument(
                new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(CtNs + "Types",
                    new XElement(CtNs + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                    new XElement(CtNs + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                    new XElement(CtNs + "Default", new XAttribute("Extension", "png"), new XAttribute("ContentType", "image/png")),
                    new XElement(CtNs + "Override", new XAttribute("PartName", "/word/document.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")),
                    new XElement(CtNs + "Override", new XAttribute("PartName", "/word/styles.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"))));
            AddXmlEntry(zip, "[Content_Types].xml", contentTypes);

            // _rels/.rels
            var rootRels = new XDocument(
                new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(RelNs + "Relationships",
                    new XElement(RelNs + "Relationship",
                        new XAttribute("Id", "rId1"),
                        new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                        new XAttribute("Target", "word/document.xml"))));
            AddXmlEntry(zip, "_rels/.rels", rootRels);

            // word/_rels/document.xml.rels
            var docRelsRoot = new XElement(RelNs + "Relationships",
                new XElement(RelNs + "Relationship",
                    new XAttribute("Id", "rIdStyles"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                    new XAttribute("Target", "styles.xml")));
            foreach (var img in images)
            {
                docRelsRoot.Add(new XElement(RelNs + "Relationship",
                    new XAttribute("Id", img.RelId),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"),
                    new XAttribute("Target", img.ZipMediaPath["word/".Length..])));
            }
            AddXmlEntry(zip, "word/_rels/document.xml.rels", new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), docRelsRoot));

            // word/styles.xml (clean Chinese Microsoft YaHei default style)
            var stylesXml = new XDocument(
                new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(W + "styles",
                    new XAttribute(XNamespace.Xmlns + "w", W),
                    new XElement(W + "docDefaults",
                        new XElement(W + "rPrDefault",
                            new XElement(W + "rPr",
                                new XElement(W + "rFonts",
                                    new XAttribute(W + "ascii", "Calibri"),
                                    new XAttribute(W + "hAnsi", "Calibri"),
                                    new XAttribute(W + "eastAsia", "Microsoft YaHei")),
                                new XElement(W + "sz", new XAttribute(W + "val", 20)),
                                new XElement(W + "szCs", new XAttribute(W + "val", 20)))))));
            AddXmlEntry(zip, "word/styles.xml", stylesXml);

            // word/document.xml
            AddXmlEntry(zip, "word/document.xml", documentXml);

            // word/media/*.png
            foreach (var img in images)
            {
                var entry = zip.CreateEntry(img.ZipMediaPath, CompressionLevel.NoCompression);
                using var dest = entry.Open();
                using var src = File.OpenRead(img.FullPath);
                src.CopyTo(dest);
            }
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static void AddXmlEntry(ZipArchive zip, string entryName, XDocument xml)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        xml.Save(writer, SaveOptions.DisableFormatting);
    }

    private static XElement CreateHeading(string text) =>
        CreateParagraph(text, fontSizeHalfPt: 26, bold: true, colorHex: "0F5A99", spaceBefore: 220, spaceAfter: 100);

    private static XElement CreateParagraph(
        string text,
        int fontSizeHalfPt = 20,
        bool bold = false,
        string colorHex = "0F172A",
        string? align = null,
        int spaceBefore = 0,
        int spaceAfter = 80)
    {
        var pPr = new XElement(W + "pPr",
            new XElement(W + "spacing", new XAttribute(W + "before", spaceBefore), new XAttribute(W + "after", spaceAfter), new XAttribute(W + "line", 300), new XAttribute(W + "lineRule", "auto")));
        if (!string.IsNullOrWhiteSpace(align))
        {
            pPr.Add(new XElement(W + "jc", new XAttribute(W + "val", align)));
        }

        var p = new XElement(W + "p", pPr);
        var lines = SanitizeXmlText(text).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                p.Add(new XElement(W + "r", new XElement(W + "br")));
            }
            p.Add(CreateRun(lines[i], fontSizeHalfPt, bold, colorHex));
        }

        return p;
    }

    private static XElement CreateLabeledParagraph(string label, string bodyText, string labelColorHex)
    {
        var p = new XElement(W + "p",
            new XElement(W + "pPr",
                new XElement(W + "spacing", new XAttribute(W + "before", 40), new XAttribute(W + "after", 80), new XAttribute(W + "line", 290), new XAttribute(W + "lineRule", "auto"))),
            CreateRun(label, fontSizeHalfPt: 19, bold: true, colorHex: labelColorHex));

        var lines = bodyText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) p.Add(new XElement(W + "r", new XElement(W + "br")));
            p.Add(CreateRun(lines[i], fontSizeHalfPt: 19, bold: false, colorHex: "1E293B"));
        }

        return p;
    }

    private static XElement CreateRun(string text, int fontSizeHalfPt, bool bold, string colorHex)
    {
        var rPr = new XElement(W + "rPr",
            new XElement(W + "rFonts",
                new XAttribute(W + "ascii", "Calibri"),
                new XAttribute(W + "hAnsi", "Calibri"),
                new XAttribute(W + "eastAsia", "Microsoft YaHei")),
            new XElement(W + "color", new XAttribute(W + "val", colorHex)),
            new XElement(W + "sz", new XAttribute(W + "val", fontSizeHalfPt)),
            new XElement(W + "szCs", new XAttribute(W + "val", fontSizeHalfPt)));
        if (bold) rPr.Add(new XElement(W + "b"));

        return new XElement(W + "r", rPr, new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));
    }

    private static XElement CreateTable(string[] headers, IReadOnlyList<string[]> rows, int[] colWidthsDxa)
    {
        var tbl = new XElement(W + "tbl",
            new XElement(W + "tblPr",
                new XElement(W + "tblW", new XAttribute(W + "w", colWidthsDxa.Sum()), new XAttribute(W + "type", "dxa")),
                new XElement(W + "tblBorders",
                    CreateBorder("top", "CBD5E1"),
                    CreateBorder("left", "CBD5E1"),
                    CreateBorder("bottom", "CBD5E1"),
                    CreateBorder("right", "CBD5E1"),
                    CreateBorder("insideH", "E2E8F0"),
                    CreateBorder("insideV", "E2E8F0")),
                new XElement(W + "tblCellMar",
                    new XElement(W + "top", new XAttribute(W + "w", 75), new XAttribute(W + "type", "dxa")),
                    new XElement(W + "left", new XAttribute(W + "w", 100), new XAttribute(W + "type", "dxa")),
                    new XElement(W + "bottom", new XAttribute(W + "w", 75), new XAttribute(W + "type", "dxa")),
                    new XElement(W + "right", new XAttribute(W + "w", 100), new XAttribute(W + "type", "dxa")))),
            new XElement(W + "tblGrid", colWidthsDxa.Select(w => new XElement(W + "gridCol", new XAttribute(W + "w", w)))));

        // Header row
        var headerTr = new XElement(W + "tr", new XElement(W + "trPr", new XElement(W + "tblHeader")));
        for (var c = 0; c < headers.Length; c++)
        {
            headerTr.Add(CreateCell(headers[c], colWidthsDxa[c], isHeader: true, shadeHex: "EFF6FF"));
        }
        tbl.Add(headerTr);

        // Data rows
        for (var r = 0; r < rows.Count; r++)
        {
            var tr = new XElement(W + "tr");
            var shade = r % 2 == 1 ? "F8FAFC" : null;
            for (var c = 0; c < headers.Length; c++)
            {
                var cellText = c < rows[r].Length ? rows[r][c] : string.Empty;
                tr.Add(CreateCell(cellText, colWidthsDxa[c], isHeader: false, shadeHex: shade));
            }
            tbl.Add(tr);
        }

        return tbl;
    }

    private static XElement CreateCell(string text, int widthDxa, bool isHeader, string? shadeHex)
    {
        var tcPr = new XElement(W + "tcPr",
            new XElement(W + "tcW", new XAttribute(W + "w", widthDxa), new XAttribute(W + "type", "dxa")),
            new XElement(W + "vAlign", new XAttribute(W + "val", "center")));
        if (!string.IsNullOrWhiteSpace(shadeHex))
        {
            tcPr.Add(new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", shadeHex)));
        }

        return new XElement(W + "tc",
            tcPr,
            CreateParagraph(text, fontSizeHalfPt: isHeader ? 18 : 17, bold: isHeader, colorHex: isHeader ? "0F5A99" : "1E293B", spaceBefore: 0, spaceAfter: 0));
    }

    private static XElement CreateBorder(string name, string colorHex) =>
        new(W + name,
            new XAttribute(W + "val", "single"),
            new XAttribute(W + "sz", 4),
            new XAttribute(W + "space", 0),
            new XAttribute(W + "color", colorHex));

    private static XElement CreateImageParagraph(EmbeddedImage img)
    {
        // Fit nicely within A4 printable width (16.5 cm ≈ 5,940,000 EMU), preserving exact aspect ratio.
        const long maxWidthEmu = 5_700_000L;
        const long maxHeightEmu = 3_900_000L;
        var ratio = img.HeightPx > 0 ? (double)img.WidthPx / img.HeightPx : 16.0 / 9.0;
        var cx = maxWidthEmu;
        var cy = (long)Math.Round(cx / ratio);
        if (cy > maxHeightEmu)
        {
            cy = maxHeightEmu;
            cx = (long)Math.Round(cy * ratio);
        }

        var drawing = new XElement(W + "drawing",
            new XElement(Wp + "inline",
                new XAttribute("distT", 0), new XAttribute("distB", 0), new XAttribute("distL", 0), new XAttribute("distR", 0),
                new XElement(Wp + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                new XElement(Wp + "docPr", new XAttribute("id", img.DrawingId), new XAttribute("name", $"Figure {img.DrawingId}")),
                new XElement(Wp + "cNvGraphicFramePr",
                    new XElement(A + "graphicFrameLocks", new XAttribute("noChangeAspect", 1))),
                new XElement(A + "graphic",
                    new XElement(A + "graphicData",
                        new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/picture"),
                        new XElement(Pic + "pic",
                            new XElement(Pic + "nvPicPr",
                                new XElement(Pic + "cNvPr", new XAttribute("id", img.DrawingId), new XAttribute("name", Path.GetFileName(img.FullPath))),
                                new XElement(Pic + "cNvPicPr")),
                            new XElement(Pic + "blipFill",
                                new XElement(A + "blip", new XAttribute(R + "embed", img.RelId)),
                                new XElement(A + "stretch", new XElement(A + "fillRect"))),
                            new XElement(Pic + "spPr",
                                new XElement(A + "xfrm",
                                    new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                                    new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                                new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))))))));

        return new XElement(W + "p",
            new XElement(W + "pPr",
                new XElement(W + "jc", new XAttribute(W + "val", "center")),
                new XElement(W + "spacing", new XAttribute(W + "before", 100), new XAttribute(W + "after", 40))),
            new XElement(W + "r", drawing));
    }

    private static string SanitizeXmlText(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (ch == '\t' || ch == '\n' || ch == '\r' || (ch >= 0x20 && ch <= 0xD7FF) || (ch >= 0xE000 && ch <= 0xFFFD))
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }
}
