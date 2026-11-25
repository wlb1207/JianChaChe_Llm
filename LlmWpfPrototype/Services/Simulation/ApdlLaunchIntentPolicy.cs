namespace LlmWpfPrototype.Services.Simulation;

public static class ApdlLaunchIntentPolicy
{
    private static readonly string[] NegativePhrases =
    [
        "不要打开", "不要启动", "不要开启", "别打开", "别启动", "先别打开", "先别启动",
        "暂时不打开", "暂时不启动", "不需要打开", "不用打开", "取消打开", "取消启动", "不启动",
        "不想打开", "不想启动", "不要运行", "别运行", "先不要", "暂时不要", "我只是问", "只是问问",
        "不要", "别", "不用", "不需要", "取消", "不打开", "暂不", "不运行", "不想"
    ];

    private static readonly string[] InformationPhrases =
    [
        "是什么", "什么意思", "怎么用", "如何使用", "怎么写", "如何写", "脚本", "代码", "教程",
        "语法", "怎么打开", "如何打开", "怎样打开", "怎么", "如何", "怎样", "请问", "载荷", "边界条件",
        "材料", "求解结果", "分析流程", "怎么运行", "如何运行", "怎样运行", "如何求解", "怎么求解",
        "怎么设置", "如何设置", "配置方法", "为什么打不开", "打不开怎么办"
    ];

    private static readonly string[] LaunchPhrases =
    [
        "打开", "启动", "开启", "看下", "看一下", "看看", "瞧下", "调出", "进入", "我想用",
        "我要用", "能打开", "可以打开", "能不能打开", "我看下", "我看看", "打开一下", "启动一下", "想看看"
    ];

    public static bool IsCandidate(string? input)
    {
        var normalized = Normalize(input);
        if (normalized.Length == 0 || ContainsAny(normalized, NegativePhrases))
            return false;

        if (normalized is "apdl" or "ansys" or "ansysapdl" or "仿真软件")
            return true;

        if (!HasApplicationTarget(normalized))
            return false;

        if (ContainsAny(normalized, InformationPhrases))
            return false;

        return ContainsAny(normalized, LaunchPhrases);
    }

    private static bool HasApplicationTarget(string normalized) =>
        normalized.Contains("apdl", StringComparison.OrdinalIgnoreCase) ||
        normalized.Contains("ansys", StringComparison.OrdinalIgnoreCase) ||
        normalized.Contains("仿真软件", StringComparison.Ordinal) ||
        normalized.Contains("仿真程序", StringComparison.Ordinal) ||
        normalized.Contains("分析软件", StringComparison.Ordinal) ||
        normalized.Contains("求解器", StringComparison.Ordinal);

    private static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        return new string(input
            .Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character) && !char.IsSymbol(character))
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static bool ContainsAny(string input, IEnumerable<string> phrases) =>
        phrases.Any(phrase => input.Contains(phrase, StringComparison.OrdinalIgnoreCase));
}
