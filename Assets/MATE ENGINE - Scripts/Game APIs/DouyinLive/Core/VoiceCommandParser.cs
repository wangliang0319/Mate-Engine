using System;
using System.Collections.Generic;

namespace DouyinLive
{
    // 语音命令的文本清洗：云端 ASR 返回的是整句（「小美小美，唱一首黄昏」），
    // 把唤醒词剥掉，剩下的才是走 Route() 的命令文字。纯函数，Core 可测。
    //
    // 返回契约：null = 不执行（输入空，或 VAD 模式没找到唤醒词）；
    // "" = 只有唤醒词没内容；其它 = 命令文字。
    public static class VoiceCommandParser
    {
        // ASR 常见的首尾标点（全角为主）。剥完唤醒词把这些清掉再判空。
        static readonly char[] TrimChars =
            { ' ', '\t', '，', '。', '！', '？', '、', '~', '·', ',', '.', '!', '?', '“', '”', '"' };

        public static string ExtractCommand(string text, IReadOnlyList<string> wakeWords, bool requireWakeWord)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (wakeWords == null) wakeWords = Array.Empty<string>();

            string cur = text.Trim();
            if (!TryMatchEarliest(cur, wakeWords, out int end))
                return requireWakeWord ? null : cur;

            cur = cur.Substring(end).Trim(TrimChars);
            // ASR 可能把「小美小美」写成「小美 小美」：开头还挂着唤醒词就继续剥。
            // 轮数上限防病态输入（正常最多剥一两轮）。
            for (int n = 0; n < 4 && TryMatchAtStart(cur, wakeWords, out end); n++)
                cur = cur.Substring(end).Trim(TrimChars);
            return cur;
        }

        // 全句找最早的唤醒词；同一位置有多个命中取最长的
        // （「你好小美小美」里「小美」和「小美小美」都在 2，剥到 6 才干净）。
        static bool TryMatchEarliest(string s, IReadOnlyList<string> wakeWords, out int end)
        {
            end = 0;
            int bestAt = int.MaxValue, bestEnd = -1;
            for (int i = 0; i < wakeWords.Count; i++)
            {
                string w = wakeWords[i];
                if (string.IsNullOrEmpty(w)) continue;
                int at = s.IndexOf(w, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                int e = at + w.Length;
                if (at < bestAt || (at == bestAt && e > bestEnd)) { bestAt = at; bestEnd = e; }
            }
            if (bestEnd < 0) return false;
            end = bestEnd;
            return true;
        }

        // 唤醒词是否出现在句首（用于剥「小美 小美」这类 ASR 重复）。
        static bool TryMatchAtStart(string s, IReadOnlyList<string> wakeWords, out int end)
        {
            end = 0;
            for (int i = 0; i < wakeWords.Count; i++)
            {
                string w = wakeWords[i];
                if (string.IsNullOrEmpty(w) || s.Length < w.Length) continue;
                if (string.Compare(s, 0, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && w.Length > end)
                    end = w.Length;
            }
            return end > 0;
        }
    }
}
