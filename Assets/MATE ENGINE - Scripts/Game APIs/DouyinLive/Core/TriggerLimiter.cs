using System;
using System.Collections.Generic;

namespace DouyinLive
{
    // Check 返回被哪道闸拦下 —— 调玩法时能一眼看出该调哪个参数
    public enum GateResult
    {
        Pass,
        SourceCooldown,   // chatCooldown / likeCooldown / giftCooldown
        UserCooldown,     // perUserCooldown
        RuleCooldown,     // rule.cooldown
        LevelInterval     // l2MinInterval / l3MinInterval
    }

    // 四道限流闸，逐层收紧，全部通过才算过。
    // 刻意不采用「弹幕不能触发 L3」那种按来源禁止的做法：真正要防的是
    // 大效果连环炸，而不是弹幕这个来源有罪。禁掉来源会让观众永远玩不了
    // 换角色和点舞 —— 恰恰是低人气直播间最需要的零成本参与点。
    public class TriggerLimiter
    {
        // 时间源可注入，这样冷却逻辑能在 EditMode 测试里跑而不用进播放模式
        public Func<float> Now = () => 0f;

        readonly Dictionary<string, float> lastBySource = new Dictionary<string, float>();
        readonly Dictionary<string, float> lastByRule = new Dictionary<string, float>();
        readonly Dictionary<string, float> lastByLevel = new Dictionary<string, float>();
        readonly Dictionary<string, float> lastByUser = new Dictionary<string, float>();

        public int TrackedUserCount => lastByUser.Count;

        // 不产生任何副作用，可以重复调用。放行后必须显式 Commit 才记账 ——
        // ActionDirector 会有「检查通过但改为排队、暂不执行」的场景。
        public GateResult Check(TriggerRule rule, TriggerGlobal g, string userId)
        {
            if (rule == null) return GateResult.Pass;
            if (g == null) g = new TriggerGlobal();
            float now = Now();

            float srcCd = SourceCooldown(rule.source, g);
            if (srcCd > 0f && Elapsed(lastBySource, SourceKey(rule), now) < srcCd)
                return GateResult.SourceCooldown;

            float userCd = rule.perUserCooldown >= 0f ? rule.perUserCooldown : g.perUserCooldown;
            // UserId 缺失时（部分事件没带）退化为不限制，其余三道闸照常生效
            if (userCd > 0f && !string.IsNullOrEmpty(userId) &&
                Elapsed(lastByUser, UserKey(rule, userId), now) < userCd)
                return GateResult.UserCooldown;

            if (rule.cooldown > 0f && Elapsed(lastByRule, RuleKey(rule), now) < rule.cooldown)
                return GateResult.RuleCooldown;

            float lvlCd = LevelInterval(rule.LevelOrDefault, g);
            if (lvlCd > 0f && Elapsed(lastByLevel, LevelKey(rule), now) < lvlCd)
                return GateResult.LevelInterval;

            return GateResult.Pass;
        }

        public void Commit(TriggerRule rule, TriggerGlobal g, string userId)
        {
            if (rule == null) return;
            float now = Now();
            lastBySource[SourceKey(rule)] = now;
            lastByRule[RuleKey(rule)] = now;
            lastByLevel[LevelKey(rule)] = now;
            if (!string.IsNullOrEmpty(userId)) lastByUser[UserKey(rule, userId)] = now;
        }

        // 长时间直播会让按 UserId 的记账表无限增长，定期清理不活跃条目
        public void PruneUsers(float idleSeconds)
        {
            float now = Now();
            var stale = new List<string>();
            foreach (var kv in lastByUser)
                if (now - kv.Value >= idleSeconds) stale.Add(kv.Key);
            foreach (var k in stale) lastByUser.Remove(k);
        }

        public void Reset()
        {
            lastBySource.Clear();
            lastByRule.Clear();
            lastByLevel.Clear();
            lastByUser.Clear();
        }

        static float SourceCooldown(string source, TriggerGlobal g)
        {
            switch (source)
            {
                case "like": return g.likeCooldown;
                case "gift": return g.giftCooldown;
                default: return g.chatCooldown;   // chat / follow / enter / share
            }
        }

        static float LevelInterval(int level, TriggerGlobal g)
            => level == 3 ? g.l3MinInterval : level == 2 ? g.l2MinInterval : 0f;

        // Check 和 Commit 必须用同一把键，否则 source=null 的规则会在 Check 里
        // 直接拿 null 去查 Dictionary 抛 ArgumentNullException，与 Commit 的记账对不上
        static string SourceKey(TriggerRule r) => r.source ?? "";

        // 单人冷却按「用户 × 规则」记账：一个人刚点过舞，不该连带把他的拍头也冻住
        static string UserKey(TriggerRule r, string userId) => userId + "\u0001" + RuleKey(r);
        static string RuleKey(TriggerRule r) => string.IsNullOrEmpty(r.id) ? r.source + "\u0002" + r.level : r.id;
        static string LevelKey(TriggerRule r) => "L" + r.LevelOrDefault;

        static float Elapsed(Dictionary<string, float> map, string key, float now)
            => map.TryGetValue(key, out float last) ? now - last : float.MaxValue;
    }
}
