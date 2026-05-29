using System.Collections.Generic;

namespace LlmWpfPrototype.Models;

public sealed class TrussMemberCommandParseResult
{
    public bool IsMatched { get; set; }

    public string RawText { get; set; } = string.Empty;

    public List<string> TargetMemberIds { get; set; } = [];

    public decimal? Width { get; set; }

    public decimal? Height { get; set; }

    public decimal? Thickness { get; set; }

    public string Unit { get; set; } = "mm";

    public bool IsFullSectionUpdate { get; set; }
}
