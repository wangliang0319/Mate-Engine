using System;
using System.Text.RegularExpressions;

namespace DouyinLive
{
    // 匹配所需的外部累计状态。做成显式入参而不是内部字段，
    // 这样 Match 是纯函数，可以在 EditMode 测试里直接构造任意场景。
    public class MatchContext
    {
        public long LikeTotalBefore;   // 本次事件计入之前的累计点赞
        public long LikeTotalAfter;    // 计入之后的累计点赞
    }

    // 事件 → 命中的规则。数组顺序即优先级，第一条命中即停。
    // 刻意不做「更具体的规则优先」这类隐式启发式：出问题时能一眼看出为什么是那条赢了。
    public static class TriggerMatcher
    {
        public static TriggerRule Match(DouyinEvent ev, TriggerConfig cfg, MatchContext ctx)
        {
            if (ev == null || cfg == null || cfg.rules == null || ctx == null) return null;

            string source = SourceOf(ev.Type);
            if (source == null) return null;

            foreach (var r in cfg.rules)
            {
                if (r == null || !r.enabled) continue;
                if (r.source != source) continue;
                if (!Matches(r, ev, cfg.global, ctx)) continue;
                return r;
            }
            return null;
        }

        static string SourceOf(DouyinMsgType t)
        {
            switch (t)
            {
                case DouyinMsgType.Chat:   return "chat";
                case DouyinMsgType.Like:   return "like";
                case DouyinMsgType.Follow: return "follow";
                case DouyinMsgType.Gift:   return "gift";
                case DouyinMsgType.Enter:  return "enter";
                case DouyinMsgType.Share:  return "share";
                default: return null;   // Stats / FansClub / None 不参与触发
            }
        }

        static bool Matches(TriggerRule r, DouyinEvent ev, TriggerGlobal g, MatchContext ctx)
        {
            switch (r.source)
            {
                case "chat": return MatchesChat(r, ev.Content ?? "");
                case "like": return MatchesLike(r, ctx);
                case "gift": return MatchesGift(r, ev, g);
                default: return true;   // follow / enter / share：该源的事件一律命中
            }
        }

        // 关键词包含匹配（忽略大小写）或正则任一命中即算命中。
        // 两个条件都没写的弹幕规则永远不命中 —— 否则它会吞掉所有弹幕，
        // 让 AI 回复永远不生效，而这种配置几乎肯定是写漏了。
        // 这一点弹幕和点赞/礼物不同：弹幕未命中要继续走到
        // RewardService.TryHandleDanmaku → DanmakuAIService.OnDanmaku（spec §5.2），
        // 空条件弹幕规则会把这条回落路径永久堵死；点赞/礼物没有这个下游消费者，
        // 「省略即不限」对它们是安全且刻意的（见下方 MatchesLike/MatchesGift）。
        static bool MatchesChat(TriggerRule r, string content)
        {
            if (r.keywords != null)
                foreach (var w in r.keywords)
                {
                    if (string.IsNullOrWhiteSpace(w)) continue;
                    if (content.IndexOf(w.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }

            if (!string.IsNullOrWhiteSpace(r.regex))
            {
                // 用户写错正则不能让整个直播间的弹幕响应崩掉，当作不命中处理。
                // 调用方（TriggerConfigStore）在加载时已经校验过一次并报过错。
                try { if (Regex.IsMatch(content, r.regex)) return true; }
                catch (ArgumentException) { }
            }

            return false;
        }

        // 点赞没有下游回落路径（不像弹幕要留给 AI 回复，spec §5.2），
        // 所以「省略即不限」（spec §5.1）在这里是刻意设计：
        // 一条不写 everyN/milestone 的点赞规则就是有意的兜底，命中每一次点赞，
        // 不能照搬 chat 的「空条件永不命中」写法。
        static bool MatchesLike(TriggerRule r, MatchContext ctx)
        {
            if (r.everyN > 0)
                return ctx.LikeTotalBefore / r.everyN < ctx.LikeTotalAfter / r.everyN;

            if (r.milestone > 0)
                return ctx.LikeTotalBefore < r.milestone && ctx.LikeTotalAfter >= r.milestone;

            return true;   // 没写条件 = 每次点赞都命中
        }

        // 礼物同样没有下游回落路径，「省略即不限」（spec §5.1）同样是刻意设计：
        // 不写 giftName/minCount/minDiamond/maxDiamond 的礼物规则就是兜底，
        // 命中每一份礼物，可用来在数组末尾写「其它礼物都这样处理」这类默认规则。
        static bool MatchesGift(TriggerRule r, DouyinEvent ev, TriggerGlobal g)
        {
            if (!string.IsNullOrEmpty(r.giftName) &&
                (ev.GiftName ?? "").IndexOf(r.giftName, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            int count = Math.Max(1, ev.GiftCount);
            if (r.minCount > 0 && count < r.minCount) return false;

            bool useTotal = g == null || g.giftUseTotalValue;
            int value = useTotal ? Math.Max(1, ev.DiamondCount) * count : ev.DiamondCount;

            if (r.minDiamond > 0 && value < r.minDiamond) return false;
            if (r.maxDiamond > 0 && value > r.maxDiamond) return false;
            return true;
        }
    }
}
