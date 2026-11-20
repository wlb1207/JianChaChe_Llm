using System.Text.RegularExpressions;

namespace LlmWpfPrototype.Services.Rag;

public sealed record LocalChatSourceRange(int StartCharacter, int Length, string Section);
public sealed record LocalEvidenceSelection(LocalKnowledgeHit Source, string Text,
    IReadOnlyList<LocalChatSourceRange> Ranges, string Reason);

// Shared by application BuildRequest and preflight. Retrieval ranking is not changed.
public static class LocalKnowledgeEvidenceSelector
{
    // Object names are useful for retrieval but cannot establish answer-bearing evidence.
    private static readonly HashSet<string> ObjectTerms = new(StringComparer.OrdinalIgnoreCase)
        { "主梁", "反力架", "构件", "结构", "起重机", "桥式起重机", "门式起重机", "板件", "材料", "资料" };
    private static readonly (string From,string To)[] Synonyms =
        { ("允许应力","许用应力"), ("容许应力","许用应力"), ("安全因子","安全系数"), ("刚性","刚度"), ("挠曲","挠度") };
    private static readonly Regex Headings = new(@"(?m)^#{1,6}[ \t]+[^\r\n]+",RegexOptions.CultureInvariant);
    // Start/Length are document coordinates. BodyStart keeps paragraph ranges
    // aligned with the original text after the Markdown heading.
    private sealed record Block(int Start,int Length,string Heading,string Path,string Body,int BodyStart);
    private sealed record Choice(int Start,int Length,string Heading,int Coverage,bool Notice,bool HeadingMatch,bool Scope);
    public static string Normalize(string text)
    {
        foreach(var (from,to) in Synonyms) text=text.Replace(from,to,StringComparison.OrdinalIgnoreCase);
        return text;
    }
    public static IReadOnlyList<string> QueryTerms(string question,IEnumerable<string> vocabulary)
    {
        var query=Normalize(question);
        var matched=vocabulary.Select(Normalize).Select(x=>x.Trim())
            .Where(x=>x.Length>=2 && !ObjectTerms.Contains(x) && query.Contains(x,StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // A longer technical phrase subsumes an overlapping shorter phrase; no tuned score threshold.
        return matched.Where(x=>!matched.Any(y=>y.Length>x.Length && y.Contains(x,StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x=>query.IndexOf(x,StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private static IReadOnlyList<Block> Sections(string text)
    {
        var matches=Headings.Matches(text).Cast<Match>().ToList();
        if(matches.Count==0) return new[]{new Block(0,text.Length,"","",text,0)};
        var result=new List<Block>();var parents=new List<(int Level,string Heading)>();
        if(matches[0].Index>0) result.Add(new(0,matches[0].Index,"","",text[..matches[0].Index],0));
        for(int i=0;i<matches.Count;i++)
        {
            var m=matches[i];var level=m.Value.TakeWhile(c=>c=='#').Count();
            var title=m.Value[level..].Trim();
            parents.RemoveAll(x=>x.Level>=level);parents.Add((level,title));
            var end=i+1<matches.Count?matches[i+1].Index:text.Length;
            result.Add(new(m.Index,end-m.Index,title,string.Join(" / ",parents.Select(x=>x.Heading)),text[(m.Index+m.Length)..end],m.Index+m.Length));
        }
        return result;
    }
    private static string MaskMetadataLines(string body)
    {
        var masked = body.ToCharArray();
        foreach (Match match in Regex.Matches(body, @"(?m)^[ \t]*(?:Source|Topic|SourceType|Verified|Clause|Page):[^\r\n]*"))
            for (var i = match.Index; i < match.Index + match.Length; i++)
                if (masked[i] != '\r' && masked[i] != '\n') masked[i] = ' ';
        return new string(masked);
    }
    private static bool Notice(string heading)=>Regex.IsMatch(heading,"限制|注意|选用|条件|适用|范围|定义|基本|原则");
    public static IReadOnlyList<LocalEvidenceSelection> Select(string question,IReadOnlyList<LocalKnowledgeHit> hits,int limit)
    {
        var output=new List<LocalEvidenceSelection>();
        foreach(var hit in hits)
        {
            var terms=hit.EvidenceTerms;
            if(terms.Count==0) continue;
            var blocks=Sections(hit.FullText);
            var choices=new List<Choice>();var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var b in blocks)
            {
                // File labels/front matter are not evidence of an answer, even if their title matches.
                // Mask metadata without changing length, so all selected ranges
                // remain valid coordinates in hit.FullText.
                var body=MaskMetadataLines(b.Body);
                if(string.IsNullOrWhiteSpace(body)) continue;
                var normalized=Normalize(body);
                var coverage=terms.Count(t=>normalized.Contains(t,StringComparison.OrdinalIgnoreCase));
                var headingMatch=terms.Any(t=>Normalize(b.Heading).Contains(t,StringComparison.OrdinalIgnoreCase));
                var contextualFormula=headingMatch && (body.Contains(@"\[") || body.Contains(@"\leq") || body.Contains('≤'));
                if(coverage==0 && !contextualFormula) continue;
                // A directly matched subsection may travel whole; a broad parent section contributes
                // only matching paragraphs/bullets and their formula/definition continuations.
                if(headingMatch && b.Length<=limit)
                {
                    choices.Add(new(b.Start,b.Length,b.Path,coverage,Notice(b.Heading),true,false));
                    continue;
                }
                var units=Regex.Matches(body,@"(?s)\S.*?(?=\r?\n[ \t]*\r?\n|\z)").Cast<Match>().ToList();
                foreach(var unit in units)
                {
                    var lines=Regex.Matches(unit.Value,@"(?m)^[ \t]*(?:[-*•]|\d+[.)])[ \t]+[^\r\n]*").Cast<Match>().ToList();
                    if(lines.Count>1)
                    {
                        foreach(var line in lines)
                        {
                            if(!terms.Any(t=>Normalize(line.Value).Contains(t,StringComparison.OrdinalIgnoreCase))) continue;
                            var absolute=b.BodyStart+unit.Index+line.Index;
                            choices.Add(new(absolute,line.Length,b.Path,
                                terms.Count(t=>Normalize(line.Value).Contains(t,StringComparison.OrdinalIgnoreCase)),Notice(b.Heading),false,false));
                        }
                        continue;
                    }
                    if(!terms.Any(t=>Normalize(unit.Value).Contains(t,StringComparison.OrdinalIgnoreCase))) continue;
                    var unitIndex=units.IndexOf(unit);var last=unitIndex;
                    while(last+1<units.Count && Regex.IsMatch(units[last+1].Value,@"^([\-•]|其中|式中|条件|适用|常用|基本|\\\[)|\\frac|\\leq")) last++;
                    var lastUnit=units[last];var absoluteStart=b.BodyStart+unit.Index;
                    var length=lastUnit.Index+lastUnit.Length-unit.Index;
                    if(length<=limit) choices.Add(new(absoluteStart,length,b.Path,
                        terms.Count(t=>Normalize(hit.FullText.Substring(absoluteStart,length)).Contains(t,StringComparison.OrdinalIgnoreCase)),
                        Notice(b.Heading),false,false));
                }
            }
            if(choices.Count==0) continue;
            // Keep applicable scope with direct evidence, not as an independent object-word match.
            foreach(var b in blocks.Where(b=>Regex.IsMatch(b.Heading,"适用范围|适用条件")))
                if(b.Length<=limit && !choices.Any(x=>x.Start==b.Start))
                    choices.Add(new(b.Start,b.Length,b.Path,0,true,false,true));
            choices = choices.Where(c => seen.Add(Regex.Replace(
                hit.FullText.Substring(c.Start, c.Length), @"\s+", " ").Trim())).ToList();
            var selected=new List<Choice>();var used=0;var coveredTerms=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var c in choices.OrderByDescending(x=>x.Scope).ThenByDescending(x=>x.Notice)
                .ThenByDescending(x=>x.Coverage).ThenByDescending(x=>x.HeadingMatch).ThenBy(x=>x.Start))
            {
                var candidateText=Normalize(hit.FullText.Substring(c.Start,c.Length));
                var newlyCovered=terms.Where(t=>candidateText.Contains(t,StringComparison.OrdinalIgnoreCase)
                    && !coveredTerms.Contains(t)).ToArray();
                if(!c.Scope && newlyCovered.Length==0 && !c.Notice) continue;
                if(selected.Any(x=>c.Start<x.Start+x.Length && x.Start<c.Start+c.Length)) continue;
                var separator=selected.Count==0?0:2;
                if(used+c.Length+separator>limit) continue;
                selected.Add(c);used+=c.Length+separator;
                foreach(var term in newlyCovered) coveredTerms.Add(term);
            }
            if(selected.Count==0 || !selected.Any(x=>!x.Scope)) continue;
            selected=selected.OrderBy(x=>x.Start).ToList();
            var text=string.Join("\n\n",selected.Select(x=>hit.FullText.Substring(x.Start,x.Length)));
            output.Add(new(hit,text,selected.Select(x=>new LocalChatSourceRange(x.Start,x.Length,x.Heading)).ToArray(),
                "正文命中技术主题："+string.Join("、",terms)+"；保留完整原文段落及适用/限制上下文；对象词不单独入选。"));
        }
        return output;
    }
}
