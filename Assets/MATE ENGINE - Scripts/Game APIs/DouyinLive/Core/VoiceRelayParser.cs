using System;
using System.Collections.Generic;

namespace DouyinLive
{
    // 语音传话的判定：命令以开头词起头（「告诉大家八点下播」）就是让角色播报给观众，
    // 剥掉开头词剩下的就是原话。本地判定、零延迟、结果可预期——实测用大模型实时分类
    // 在中转站上要 3~20 秒，直播里等不起。纯函数，Core 可测。
    public static class VoiceRelayParser
    {
        // 开头词前后的停顿标点（ASR 常写成「告诉大家，八点下播」）。句尾标点不剥，留给 TTS 断句和语气。
        static readonly char[] LeadingChars =
            { ' ', '\t', '，', '。', '！', '？', '、', '：', ',', '.', '!', '?', ':', '“', '”', '"' };

        // 返回 true = 是传话，message 为要播报的原话；false = 不是传话（照常走命令/聊天）。
        // 开头词只认句首：「我想告诉大家一件事」是在跟角色说话，不算。
        public static bool TryExtractRelay(string command, IReadOnlyList<string> prefixes, out string message)
        {
            message = "";
            if (string.IsNullOrWhiteSpace(command) || prefixes == null) return false;

            string cur = command.TrimStart(LeadingChars);
            int best = 0;
            for (int i = 0; i < prefixes.Count; i++)
            {
                string p = prefixes[i];
                // 空串开头词会让任何句子都命中，必须跳过
                if (string.IsNullOrEmpty(p) || cur.Length < p.Length) continue;
                // 同时命中取最长的：「告诉」先赢会把「大家」留进播报里
                if (p.Length > best &&
                    string.Compare(cur, 0, p, 0, p.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    best = p.Length;
            }
            if (best == 0) return false;

            string rest = cur.Substring(best).TrimStart(LeadingChars).TrimEnd();
            // 只剩标点（「告诉大家。」）等于没说内容
            if (rest.Trim(LeadingChars).Length == 0) return false;
            message = rest;
            return true;
        }
    }
}
