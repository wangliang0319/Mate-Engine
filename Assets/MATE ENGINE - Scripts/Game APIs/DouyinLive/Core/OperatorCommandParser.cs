using System;
using System.Collections.Generic;

namespace DouyinLive
{
    public enum OperatorCommandKind { None, SwitchScene, UnknownScene, StopPerformance, ClearQueue, Quiet, Resume }

    // 主播控制指令的说法。字段初始值即默认说法；settings.json 里的列表会整体替换对应字段。
    public sealed class OperatorCommandWords
    {
        public List<string> SwitchPrefixes = new List<string> { "切到", "切换到", "换到" };
        public List<string> SceneGame = new List<string> { "游戏", "陪播" };
        public List<string> SceneDuo = new List<string> { "搭档", "主持", "双人" };
        public List<string> SceneSolo = new List<string> { "独播", "自己播" };
        public List<string> Stop = new List<string> { "别唱了", "别跳了", "停一下" };
        public List<string> ClearQueue = new List<string> { "别念了", "清空弹幕" };
        public List<string> Quiet = new List<string> { "安静一会儿", "先别说话" };
        public List<string> Resume = new List<string> { "可以说话了", "恢复" };
    }

    // 主播控制指令识别：只用于主播语音（观众弹幕里同样的文字按普通弹幕处理）。
    // 规则同传话开头词：去掉句首标点后只认句首，同时命中取最长，本地匹配零延迟。
    // 必须排在传话和弹幕管线之前：「别唱了」先进弹幕管线，「唱」字会被当成点歌意图。
    public static class OperatorCommandParser
    {
        static readonly char[] LeadingChars =
            { ' ', '\t', '，', '。', '！', '？', '、', '：', ',', '.', '!', '?', ':', '“', '”', '"' };

        // 返回 true = 是控制指令（含「切场景但场景名识别不出」的 UnknownScene）；false = 交给后续管线
        public static bool TryParse(string command, OperatorCommandWords words,
                                    out OperatorCommandKind kind, out LiveScene scene)
        {
            kind = OperatorCommandKind.None;
            scene = LiveScene.Solo;
            if (string.IsNullOrWhiteSpace(command) || words == null) return false;

            string s = command.TrimStart(LeadingChars);
            int best = 0;
            var bestKind = OperatorCommandKind.None;
            Consider(s, words.SwitchPrefixes, OperatorCommandKind.SwitchScene, ref best, ref bestKind);
            Consider(s, words.Stop, OperatorCommandKind.StopPerformance, ref best, ref bestKind);
            Consider(s, words.ClearQueue, OperatorCommandKind.ClearQueue, ref best, ref bestKind);
            Consider(s, words.Quiet, OperatorCommandKind.Quiet, ref best, ref bestKind);
            Consider(s, words.Resume, OperatorCommandKind.Resume, ref best, ref bestKind);
            if (best == 0) return false;

            if (bestKind == OperatorCommandKind.SwitchScene && !TryFindScene(s.Substring(best), words, out scene))
            {
                kind = OperatorCommandKind.UnknownScene;
                return true;
            }
            kind = bestKind;
            return true;
        }

        static void Consider(string s, List<string> phrases, OperatorCommandKind k,
                             ref int best, ref OperatorCommandKind bestKind)
        {
            if (phrases == null) return;
            foreach (var p in phrases)
            {
                // 空串说法会让任何句子都命中，必须跳过
                if (string.IsNullOrEmpty(p) || s.Length < p.Length || p.Length <= best) continue;
                if (string.Compare(s, 0, p, 0, p.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    best = p.Length;
                    bestKind = k;
                }
            }
        }

        // 场景名可以出现在开头词之后任意位置（「切到游戏陪播模式吧」），取最早出现的那个
        static bool TryFindScene(string rest, OperatorCommandWords w, out LiveScene scene)
        {
            scene = LiveScene.Solo;
            int bestAt = int.MaxValue;
            Find(rest, w.SceneGame, LiveScene.Game, ref bestAt, ref scene);
            Find(rest, w.SceneDuo, LiveScene.Duo, ref bestAt, ref scene);
            Find(rest, w.SceneSolo, LiveScene.Solo, ref bestAt, ref scene);
            return bestAt != int.MaxValue;
        }

        static void Find(string rest, List<string> aliases, LiveScene sc, ref int bestAt, ref LiveScene scene)
        {
            if (aliases == null) return;
            foreach (var a in aliases)
            {
                if (string.IsNullOrEmpty(a)) continue;
                int at = rest.IndexOf(a, StringComparison.OrdinalIgnoreCase);
                if (at >= 0 && at < bestAt) { bestAt = at; scene = sc; }
            }
        }
    }
}
