using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services.Rag;

namespace LlmWpfPrototype.Services;

public sealed class KnowledgeDisclosureException : InvalidOperationException
{
    public KnowledgeDisclosureException(string detail) : base(
        "资料外发已阻止：" + detail + "。本轮未请求模型；未删除命中资料、未重试或回退。") { }
}

internal sealed record DisclosureVersion(string File, string Sha256);

// Process-local provenance, issued only by shared QA; never imported from JSON/citation strings.
public sealed class KnowledgeHistoryProvenance
{
    private readonly string _content;
    internal IReadOnlyList<DisclosureVersion> Versions { get; }
    internal KnowledgeHistoryProvenance(string content, IReadOnlyList<DisclosureVersion> versions)
    { _content = content; Versions = versions.ToArray(); }
    internal bool Matches(string prefix) => _content.StartsWith(prefix, StringComparison.Ordinal);
}

public static class KnowledgeDisclosureGuard
{
    private const string PolicyHash = "8BB909E35725219189776FC6D73233FAE85F267A37BE58E925492A55D2D77E4D";
    private static string PolicyPath => Path.Combine(AppContext.BaseDirectory, "DisclosurePolicy", "approved-sources.json");
    private static string KnowledgeRoot => Path.Combine(AppContext.BaseDirectory, "KnowledgeBase");
    private sealed record EvidenceProof(string File, string FullText, string SentText, LocalChatSourceRange[] Ranges, string Clause, string ChunkId);
    private sealed record Proof(EvidenceProof[] Evidence, DisclosureVersion[] Inherited, bool UnknownHistory)
    { public DisclosureVersion[]? ValidatedVersions { get; set; } }
    // Weak keys avoid retaining historical prompts indefinitely. Imported/re-created payloads with
    // evidence or history do not acquire approval by asserting filenames or hashes in JSON.
    private static readonly ConditionalWeakTable<string, Proof> Prepared = new();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool SafeName(string name) => System.Text.RegularExpressions.Regex.IsMatch(
        name, @"^(?:verified/[a-zA-Z0-9_]+\.md|[a-zA-Z0-9_-]+/(?:verified/)?[a-zA-Z0-9_]+\.md)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string ResolveApprovedDocumentPath(string name)
    {
        var normalized = name.Replace('/', Path.DirectorySeparatorChar);
        return name.StartsWith("verified/", StringComparison.Ordinal)
            ? Path.Combine(KnowledgeRoot, "GB_T_3811_2008", normalized)
            : Path.Combine(KnowledgeRoot, normalized);
    }

    internal static void Register(string prompt, IReadOnlyList<LocalChatEvidence> evidence,
        IReadOnlyList<ConversationMessage> context)
    {
        var inherited = new List<DisclosureVersion>();
        var unknown = false;
        foreach (var message in context)
        {
            if (message.KnowledgeProvenance is not { } origin || !origin.Matches(message.Content)) unknown = true;
            else inherited.AddRange(origin.Versions);
        }
        Prepared.Add(prompt, new(evidence.Select(e => new EvidenceProof(e.Source.SourceDocument,
            e.FullText, e.SentText, e.Ranges?.ToArray() ?? [], e.Source.Clause, e.Source.ChunkId)).ToArray(),
            inherited.Distinct().ToArray(), unknown));
    }

    // Before model dispatch, and again in LlmService before authentication and the actual HTTP send.
    public static void Validate(string prompt, string? destination = null)
        => _ = ValidateAndGetVersions(prompt, destination);

    private static DisclosureVersion[] ValidateAndGetVersions(string prompt, string? destination)
    {
        if (!Prepared.TryGetValue(prompt, out var proof))
            throw new KnowledgeDisclosureException("资料或历史来源无法追溯，请使用当前会话的自动问答入口重新组装");
        if (proof.UnknownHistory)
            throw new KnowledgeDisclosureException("随追问携带的历史来源无法确认；旧会话、导入内容或被修改的历史不会默认获准外发");
        if (proof.Evidence.Length == 0 && proof.Inherited.Length == 0) return proof.ValidatedVersions = [];
        var names = proof.Evidence.Select(e => e.File).Concat(proof.Inherited.Select(v => v.File)).Distinct().ToArray();
        try
        {
            var bytes = File.ReadAllBytes(PolicyPath);
            if (Hash(bytes) != PolicyHash) throw new KnowledgeDisclosureException("批准策略缺失、损坏或版本不符，需确认策略后再发送本地资料");
            using var policy = JsonDocument.Parse(bytes);
            var root = policy.RootElement;
            var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
            if (schemaVersion is not (1 or 2))
                throw new KnowledgeDisclosureException("批准策略格式无法确认");
            if (destination is not null && !string.Equals(destination,
                root.GetProperty("endpoint").GetString(), StringComparison.Ordinal))
                throw new KnowledgeDisclosureException("当前服务目标不在资料批准范围内");
            var approved = root.GetProperty("documents").EnumerateArray().ToDictionary(
                x => x.GetProperty("file").GetString()!, x => x.GetProperty("sha256").GetString()!, StringComparer.Ordinal);
            var denied = names.Where(n => !SafeName(n) || !approved.ContainsKey(n)).ToArray();
            if (denied.Length > 0)
                throw new KnowledgeDisclosureException("需要另行确认的资料：" + string.Join("、", denied.Select(n => SafeName(n) ? n : "无法确认来源的资料")));
            var versions = new List<DisclosureVersion>();
            foreach (var name in names)
            {
                var fileBytes = File.ReadAllBytes(ResolveApprovedDocumentPath(name));
                var hash = Hash(fileBytes);
                if (!string.Equals(hash, approved[name], StringComparison.Ordinal)
                    || proof.Inherited.Any(v => v.File == name && v.Sha256 != hash))
                    throw new KnowledgeDisclosureException("资料内容版本已变化或无法确认，需要重新批准：" + name);
                // Existing keyword loader indexes Markdown.Trim(); validate the actual original ranges.
                var original = Encoding.UTF8.GetString(fileBytes).TrimStart('\uFEFF').Trim();
                var approvedEntry = root.GetProperty("documents").EnumerateArray().Single(x => x.GetProperty("file").GetString() == name);
                var headings = original.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith('#'))
                    .Select(line => line.TrimStart('#').Trim()).ToHashSet(StringComparer.Ordinal);
                foreach (var e in proof.Evidence.Where(e => e.File == name))
                {
                    if (e.Clause != approvedEntry.GetProperty("clause").GetString()
                        || e.ChunkId != approvedEntry.GetProperty("chunkId").GetString()
                        || e.Ranges.Any(r => r.Section.Split(" / ").Any(part => !headings.Contains(part)))
                        || e.FullText != original || e.Ranges.Length == 0
                        || e.Ranges.Any(r => r.StartCharacter < 0 || r.Length <= 0
                            || r.StartCharacter > original.Length - r.Length)
                        || string.Join("\n\n", e.Ranges.Select(r => original.Substring(r.StartCharacter, r.Length))) != e.SentText)
                        throw new KnowledgeDisclosureException("片段与批准版本原文位置不一致：" + name);
                }
                versions.Add(new(name, hash));
            }
            return proof.ValidatedVersions = versions.ToArray();
        }
        catch (KnowledgeDisclosureException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw new KnowledgeDisclosureException("批准策略或资料内容无法读取/校验，已按禁止外发处理");
        }
    }

    internal static KnowledgeHistoryProvenance HistoryFor(string prompt, string actualContent)
    {
        // Preserve the version dependencies validated before the model call. If files change while
        // awaiting the response, the next send must reject them; do not claim this completed call was unsent.
        if (!Prepared.TryGetValue(prompt, out var proof) || proof.ValidatedVersions is null)
            throw new InvalidOperationException("历史来源记录无法建立。");
        return new(actualContent, proof.ValidatedVersions);
    }

    internal static void RejectKnowledgeOnLegacyPath(IReadOnlyList<ConversationMessage>? history, string? input = null)
    {
        static bool Marked(string text) => System.Text.RegularExpressions.Regex.IsMatch(
            text, @"\[E\d+\]|(?:[a-zA-Z0-9_-]+/)?verified/[a-zA-Z0-9_]+\.md");
        var inputHasKnowledge = input is not null && (Marked(input)
            || (Prepared.TryGetValue(input, out var proof)
                && (proof.Evidence.Length > 0 || proof.Inherited.Length > 0 || proof.UnknownHistory)));
        if (input is not null && !inputHasKnowledge)
        {
            try
            {
                using var json = JsonDocument.Parse(input);
                var root = json.RootElement;
                inputHasKnowledge = (root.TryGetProperty("evidence", out var evidence) && (evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() > 0))
                    || (root.TryGetProperty("conversation_context", out var context) && (context.ValueKind != JsonValueKind.Array || context.GetArrayLength() > 0));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }
        if (inputHasKnowledge || history?.Any(m => m.KnowledgeProvenance?.Versions.Count > 0 || Marked(m.Content)) == true)
            throw new KnowledgeDisclosureException("业务/辅助模型路径不接收资料载荷或资料派生历史；请在资料文字问答链路中处理");
    }
}
