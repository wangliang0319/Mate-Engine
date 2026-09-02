using System;
using System.Collections.Generic;

namespace DouyinLive
{
    // 「借用某条规则的限流参数、只换执行参数」这套做法的两个零件。
    //
    // 成立的前提是 TriggerLimiter 按 rule.id 字符串记账（见 TriggerLimiter.RuleKey），
    // 所以带同一个 id 的副本和原规则共用全部四本冷却账 —— 换句话说，用副本执行
    // 不会绕开任何一道限流闸。ActionDirector.Submit 只读 effects / level / pick，
    // 接受任何 TriggerRule 实例，因此这两个零件不需要动限流层和仲裁层。
    public static class RuleQuery
    {
        // 找出「负责某个玩法」的那条弹幕规则。按数组顺序取第一条，
        // 和 TriggerMatcher 的「先写的优先」保持一致。
        public static TriggerRule FindByEffectPrefix(TriggerConfig cfg, string prefix)
        {
            if (cfg == null || cfg.rules == null || string.IsNullOrEmpty(prefix)) return null;
            foreach (var r in cfg.rules)
            {
                if (r == null || !r.enabled || r.source != "chat" || r.effects == null) continue;
                foreach (var e in r.effects)
                {
                    if (string.IsNullOrEmpty(e)) continue;
                    if (e.Trim().StartsWith(prefix, StringComparison.Ordinal)) return r;
                }
            }
            return null;
        }

        public static TriggerRule WithEffect(TriggerRule src, string effect)
        {
            if (src == null) return null;
            return new TriggerRule
            {
                id = src.id,                       // 必须一致，否则冷却账会分家
                enabled = src.enabled,
                source = src.source,
                keywords = src.keywords,
                regex = src.regex,
                everyN = src.everyN,
                milestone = src.milestone,
                giftName = src.giftName,
                minDiamond = src.minDiamond,
                maxDiamond = src.maxDiamond,
                minCount = src.minCount,
                effects = new List<string> { effect },
                pick = "all",                      // 副本只有一个效果，random 会让它有几率不执行
                level = src.level,
                cooldown = src.cooldown,
                perUserCooldown = src.perUserCooldown,
                sayFallback = src.sayFallback,
                askPrompt = src.askPrompt
            };
        }

        // swapAvatar 没有冒号（裸 swapAvatar 是合法效果），所以前缀不能写死带冒号
        public static string EffectPrefix(IntentKind kind)
        {
            switch (kind)
            {
                case IntentKind.Song:   return "song:";
                case IntentKind.Dance:  return "dance:";
                case IntentKind.Avatar: return "swapAvatar";
                default: return null;
            }
        }

        public static string BuildEffect(IntentKind kind, string arg)
        {
            switch (kind)
            {
                case IntentKind.Song:   return "song:" + arg;
                case IntentKind.Dance:  return "dance:" + arg;
                case IntentKind.Avatar: return "swapAvatar:" + arg;
                default: return null;
            }
        }
    }
}
