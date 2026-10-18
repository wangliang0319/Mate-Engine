using System;

namespace DouyinLive
{
    // 聊天模型的动作标记协议：模型在回复末尾输出【唱歌】【跳舞:赤伶】这类标记
    // 表达「要表演」，管线剥掉标记再朗读，标记本身路由到触发层执行。
    // 纯函数、放 Core 是为了 EditMode 测试 —— Core 不许引用 Newtonsoft/UnityEngine。
    public static class ActionMarker
    {
        // 标记词 → 意图类别。和 IntentResolver 用同一套 IntentKind，
        // 这样标记和意图判定汇入同一条执行路（TryHandleIntent → 四道闸）。
        // 词表用模型最容易想到的口语动词，SystemPrompt 里的协议说明与之对齐。
        static readonly (string word, IntentKind kind)[] Markers =
        {
            ("唱歌", IntentKind.Song),
            ("跳舞", IntentKind.Dance),
            ("换角色", IntentKind.Avatar),
        };

        // 从文本里取出第一个动作标记。返回 false = 没有标记（绝大多数回复）。
        // 参数（「:」后到「】」前）原样带回，是否可用由消费端过
        // IsUsableArg/IsReservedArg —— 这里只做识别不做裁决。
        public static bool TryExtract(string text, out IntentKind kind, out string arg)
        {
            kind = IntentKind.None;
            arg = "";
            if (string.IsNullOrEmpty(text)) return false;

            int start = text.IndexOf('【');
            while (start >= 0)
            {
                int end = text.IndexOf('】', start + 1);
                if (end < 0) return false;   // 没闭合：是普通方括号话不是标记

                string inner = text.Substring(start + 1, end - start - 1);
                foreach (var (word, k) in Markers)
                {
                    if (inner == word)
                    {
                        kind = k;
                        arg = "";
                        return true;
                    }
                    if (inner.Length > word.Length + 1 &&
                        inner[word.Length] == ':' &&
                        inner.StartsWith(word, StringComparison.Ordinal))
                    {
                        kind = k;
                        arg = inner.Substring(word.Length + 1);
                        return true;
                    }
                }
                // 这个括号里不是已知标记，继续找下一个括号
                start = text.IndexOf('【', end + 1);
            }
            return false;
        }

        // 剥掉动作标记，返回适合朗读/入历史的干净文本。
        // 只有识别为动作标记的括号才剥——【开心】这类普通括号话是正文，要保留。
        // 未闭合的「【」尾巴也剪掉：流式输出里标记可能正好被句读切开，
        // 残下半截不能被念出来；正文都在它前面，不受影响。
        public static string Strip(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = new System.Text.StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '【')
                {
                    int end = text.IndexOf('】', i + 1);
                    if (end < 0) break;          // 未闭合的尾巴：整段不要
                    if (IsMarker(text, i, end))
                    {
                        i = end + 1;             // 闭合的动作标记：跳过
                        continue;
                    }
                    sb.Append(text, i, end - i + 1);   // 普通括号话：保留
                    i = end + 1;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString().Trim();
        }

        // text[start..=end] 是不是已知动作标记（【唱歌】【唱歌:xxx】）
        static bool IsMarker(string text, int start, int end)
        {
            string inner = text.Substring(start + 1, end - start - 1);
            foreach (var (word, _) in Markers)
            {
                if (inner == word) return true;
                if (inner.Length > word.Length + 1 && inner[word.Length] == ':' &&
                    inner.StartsWith(word, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
