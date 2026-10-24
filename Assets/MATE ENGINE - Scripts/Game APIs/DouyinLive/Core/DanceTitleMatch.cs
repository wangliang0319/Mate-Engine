using System;
using System.Collections.Generic;
using System.Text;

namespace DouyinLive
{
    // 观众点舞时把「说的名字」对到「舞包标题」。放 Core 是为了能单测——
    // 真正的文件列表在 Assembly-CSharp 的 AvatarDancePlayer 里。
    //
    // 旧实现是双向子串 + 按文件名顺序取第一个，实测有四个坑：
    //   · 「点舞《sparkle》」带书名号时匹配不到（标题实际是 "sparkle_右_"）
    //   · 左右版本永远命中排在前面的左版
    //   · 英文短标题反向命中长句（"love" 会把 "Love Story" 错认成别的舞）
    //   · 同名/分段包被当成不同的舞，随机轮播会连着放两支几乎一样的
    // 这里改成先归一化（去掉空格、下划线、括号、书名号，统一大小写），再按
    // 精确 → 变体 → 前缀 → 包含 → 反向包含 打分，同分随机挑一个。
    public static class DanceTitleMatch
    {
        const int ScoreExact = 10000;
        const int ScoreVariant = 9000;
        const int ScorePrefix = 8000;
        const int ScoreContains = 7000;
        const int ScoreReverse = 6000;

        // 只保留字母和数字（含中日文汉字/假名），统一小写。全角英数字先转半角，
        // 因为舞包标题里全角括号、全角字母都出现过。
        public static string Normalize(string title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            var sb = new StringBuilder(title.Length);
            foreach (char raw in title)
            {
                char c = raw;
                if (c >= '０' && c <= '９') c = (char)('0' + (c - '０'));
                else if (c >= 'Ａ' && c <= 'Ｚ') c = (char)(c - 'Ａ' + 'a');
                else if (c >= 'ａ' && c <= 'ｚ') c = (char)(c - 'ａ' + 'a');
                else c = char.ToLowerInvariant(c);

                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        // 「同一支舞」的键：去掉结尾的 左/右 和 1~2 位分段编号。
        // 例：sparkle_左_ / sparkle_右_ → sparkle；MMD-CarryMeOff-1/2 → mmdcarrymeoff。
        // 纯数字标题（如 "39"）不能把数字剥光，否则会跟别的空键混在一起。
        public static string VariantKey(string title)
        {
            string n = Normalize(title);
            while (n.Length > 0 && (n[n.Length - 1] == '左' || n[n.Length - 1] == '右'))
                n = n.Substring(0, n.Length - 1);

            int digits = n.Length;
            while (digits > 0 && n[digits - 1] >= '0' && n[digits - 1] <= '9') digits--;
            // 只剥 1~2 位编号，且前面还要留得下标题（"Miku2020" 是年份不是分段）
            if (digits > 0 && n.Length - digits <= 2) n = n.Substring(0, digits);
            return n;
        }

        // 返回 -1 表示不匹配。分数只用于内部排序，不要求稳定。
        public static int Score(string title, string query)
        {
            string t = Normalize(title);
            string q = Normalize(query);
            if (t.Length == 0 || q.Length == 0) return -1;

            if (t == q) return ScoreExact;

            string tk = VariantKey(title);
            string qk = VariantKey(query);
            if (tk.Length > 0 && tk == qk)
                return ScoreVariant - Math.Min(999, Math.Abs(t.Length - q.Length));

            if (t.StartsWith(q, StringComparison.Ordinal))
                return ScorePrefix - Math.Min(999, t.Length - q.Length);

            int at = t.IndexOf(q, StringComparison.Ordinal);
            if (at >= 0)
                return ScoreContains - Math.Min(999, t.Length - q.Length + at);

            // 反向包含：观众说的是长句「帮我跳一首xxx」，标题只是其中一段。
            // 太短的标题不算，否则 "love" 会把 "lap tap love" 当成 "Love Story"。
            if (t.Length >= 4 && t.Length * 2 >= q.Length &&
                q.IndexOf(t, StringComparison.Ordinal) >= 0)
                return ScoreReverse - Math.Min(999, q.Length - t.Length);

            return -1;
        }

        // 同分的候选（左右版本、同名包）用 randomValue 轮着挑，避免永远放同一支。
        // randomValue 由调用方用 UnityEngine.Random 取，Core 不依赖 UnityEngine。
        public static int FindBest(IReadOnlyList<string> titles, string query, int randomValue)
        {
            if (titles == null || titles.Count == 0 || string.IsNullOrWhiteSpace(query)) return -1;

            int best = -1, ties = 0;
            for (int i = 0; i < titles.Count; i++)
            {
                int score = Score(titles[i], query);
                if (score < 0) continue;
                if (score > best) { best = score; ties = 1; }
                else if (score == best) ties++;
            }
            if (best < 0) return -1;

            int want = randomValue % ties;
            if (want < 0) want += ties;

            for (int i = 0; i < titles.Count; i++)
            {
                if (Score(titles[i], query) != best) continue;
                if (want-- == 0) return i;
            }
            return -1;
        }
    }
}
