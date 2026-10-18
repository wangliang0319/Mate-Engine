using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DouyinLive
{
    public sealed class WakeKeywordResult
    {
        public string KeywordsText = "";   // 有效唤醒词，每行一个，直接喂给 sherpa
        public int ValidCount;
        public readonly List<string> Errors = new List<string>();
    }

    // sherpa 唤醒词校验：配置写的是带声调拼音（声母韵母分开），写错一个音节 sherpa 不报错、
    // 只是永远检不出——主播会以为引擎坏了。启动时逐个音节对照模型词表，把错处说清楚。
    // 格式：「x iǎo m ěi x iǎo m ěi :1.5 #0.2 @小美小美」，:boost / #阈值 可选，@显示名必填且在最后。
    public static class WakeKeywordSpec
    {
        public const string DefaultKeyword = "x iǎo m ěi x iǎo m ěi @小美小美";

        // tokens.txt：每行「音节 编号」
        public static HashSet<string> ParseVocabulary(string tokensText)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(tokensText)) return set;
            foreach (var raw in tokensText.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int sp = line.LastIndexOf(' ');
                set.Add(sp > 0 ? line.Substring(0, sp).Trim() : line);
            }
            return set;
        }

        public static WakeKeywordResult Build(IReadOnlyList<string> lines, ICollection<string> vocabulary)
        {
            var r = new WakeKeywordResult();
            if (lines == null) { r.Errors.Add("没有配置唤醒词"); return r; }
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i]?.Trim();
                if (string.IsNullOrEmpty(line)) { r.Errors.Add($"第 {i + 1} 个唤醒词为空"); continue; }
                if (TryValidate(line, vocabulary, out string err))
                {
                    sb.Append(line).Append('\n');
                    r.ValidCount++;
                }
                else r.Errors.Add($"唤醒词「{line}」：{err}");
            }
            r.KeywordsText = sb.ToString();
            return r;
        }

        static bool TryValidate(string line, ICollection<string> vocab, out string err)
        {
            err = null;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int syllables = 0;
            string display = null;
            foreach (var p in parts)
            {
                if (p[0] == '@')
                {
                    if (display != null) { err = "只能有一个 @显示名"; return false; }
                    display = p.Substring(1);
                    continue;
                }
                if (display != null) { err = "@显示名必须写在最后"; return false; }
                if (p[0] == ':' || p[0] == '#')
                {
                    if (!float.TryParse(p.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    { err = $"「{p}」不是有效的灵敏度数值"; return false; }
                    continue;
                }
                if (vocab == null || !vocab.Contains(p))
                { err = $"音节「{p}」不在模型词表里（检查声调，声母韵母要分开写，如 x iǎo）"; return false; }
                syllables++;
            }
            if (syllables == 0) { err = "没有拼音音节"; return false; }
            if (string.IsNullOrEmpty(display)) { err = "缺少 @显示名（例：@小美小美）"; return false; }
            return true;
        }
    }
}
