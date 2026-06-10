using System.Text;

namespace LlmWpfPrototype.Services.Rag;

public sealed class RagPromptBuilder
{
    public string BuildPrompt(
        string query,
        IReadOnlyList<RagSearchResult> results,
        KnowledgeBaseLoader.KnowledgeRuleDocument? ruleDocument = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("你将基于以下 GB/T 3811-2008 检索片段回答问题。");
        builder.AppendLine($"用户问题：{query}");
        builder.AppendLine();
        builder.AppendLine("回答约束：");
        AppendGlobalAnswerPolicy(builder, ruleDocument?.GlobalAnswerPolicy);
        builder.AppendLine();
        builder.AppendLine("检索结果：");

        if (results.Count == 0)
        {
            builder.AppendLine("1. 未检索到相关条文。");
            return builder.ToString().Trim();
        }

        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            builder.AppendLine($"{index + 1}. 标题：{result.Chunk.Title}");
            if (!string.IsNullOrWhiteSpace(result.Chunk.RuleId))
            {
                builder.AppendLine($"   规则ID：{result.Chunk.RuleId}");
            }

            if (!string.IsNullOrWhiteSpace(result.Chunk.Topic))
            {
                builder.AppendLine($"   Topic：{result.Chunk.Topic}");
            }

            if (!string.IsNullOrWhiteSpace(result.Chunk.Clause))
            {
                builder.AppendLine($"   Clause：{result.Chunk.Clause}");
            }

            if (!string.IsNullOrWhiteSpace(result.Chunk.SourceDocument))
            {
                builder.AppendLine($"   SourceDocument：{result.Chunk.SourceDocument}");
            }

            builder.AppendLine($"   分数：{result.Score:0.##}");
            if (!string.IsNullOrWhiteSpace(result.Chunk.Summary))
            {
                builder.AppendLine($"   Summary：{result.Chunk.Summary}");
            }

            AppendList(builder, "MustRequire", result.Chunk.MustRequire);
            AppendList(builder, "DoNotAnswerWithout", result.Chunk.DoNotAnswerWithout);
            AppendList(builder, "KeyLimits", result.Chunk.KeyLimits);
            AppendList(builder, "KeyFormulas", result.Chunk.KeyFormulas);

            builder.AppendLine($"   正文片段：{BuildSnippet(result.Chunk.Text)}");
            if (result.MatchedKeywords.Count > 0)
            {
                builder.AppendLine($"   命中关键词：{string.Join("、", result.MatchedKeywords)}");
            }
        }

        return builder.ToString().Trim();
    }

    private static void AppendGlobalAnswerPolicy(
        StringBuilder builder,
        KnowledgeBaseLoader.GlobalAnswerPolicy? policy)
    {
        if (policy is null)
        {
            builder.AppendLine("- 未提供 global_answer_policy。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(policy.IfParametersMissing))
        {
            builder.AppendLine($"- IfParametersMissing: {policy.IfParametersMissing}");
        }

        if (!string.IsNullOrWhiteSpace(policy.IfSafetyRelated))
        {
            builder.AppendLine($"- IfSafetyRelated: {policy.IfSafetyRelated}");
        }

        AppendList(builder, "DoNotFabricate", policy.DoNotFabricate, "- ");
        AppendList(builder, "DistinguishChecks", policy.DistinguishChecks, "- ");
    }

    private static void AppendList(
        StringBuilder builder,
        string label,
        IReadOnlyList<string> items,
        string prefix = "   ")
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.AppendLine($"{prefix}{label}: {string.Join("；", items)}");
    }

    private static string BuildSnippet(string text)
    {
        const int maxLength = 1800;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }
}
