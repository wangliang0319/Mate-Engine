using System.Collections.Generic;
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class RuleQueryTests
    {
        static TriggerRule Rule(string id, string level, params string[] effects)
            => new TriggerRule { id = id, source = "chat", level = level, effects = new List<string>(effects) };

        static TriggerConfig Cfg(params TriggerRule[] rules)
            => new TriggerConfig { global = new TriggerGlobal(), rules = new List<TriggerRule>(rules) };

        // ---------- FindByEffectPrefix ----------

        [Test]
        public void 按前缀找到第一条命中的规则()
        {
            var cfg = Cfg(Rule("pat", "L1", "anim:Headpat"),
                          Rule("song", "L1", "song:request"),
                          Rule("song2", "L1", "song:赤伶"));
            Assert.AreEqual("song", RuleQuery.FindByEffectPrefix(cfg, "song:").id);
        }

        [Test]
        public void 跳过被禁用的规则()
        {
            var off = Rule("song", "L1", "song:request");
            off.enabled = false;
            var cfg = Cfg(off, Rule("song2", "L1", "song:赤伶"));
            Assert.AreEqual("song2", RuleQuery.FindByEffectPrefix(cfg, "song:").id);
        }

        [Test]
        public void 跳过非弹幕来源的规则()
        {
            var gift = Rule("gift3", "L3", "song:request");
            gift.source = "gift";
            var cfg = Cfg(gift);
            Assert.IsNull(RuleQuery.FindByEffectPrefix(cfg, "song:"));
        }

        [Test]
        public void 没有对应规则时返回null()
        {
            var cfg = Cfg(Rule("pat", "L1", "anim:Headpat"));
            Assert.IsNull(RuleQuery.FindByEffectPrefix(cfg, "song:"));
            Assert.IsNull(RuleQuery.FindByEffectPrefix(null, "song:"));
        }

        [Test]
        public void 无冒号的swapAvatar前缀能命中()
        {
            var cfg = Cfg(Rule("swap", "L3", "swapAvatar"));
            Assert.AreEqual("swap", RuleQuery.FindByEffectPrefix(cfg, "swapAvatar").id);

            var cfg2 = Cfg(Rule("swap", "L3", "swapAvatar:request"));
            Assert.AreEqual("swap", RuleQuery.FindByEffectPrefix(cfg2, "swapAvatar").id);
        }

        // ---------- WithEffect ----------

        [Test]
        public void 副本保留id和限流参数只换效果()
        {
            var src = Rule("swap", "L3", "swapAvatar:request");
            src.cooldown = 60f;
            src.perUserCooldown = 180f;
            src.askPrompt = "换成谁呀";
            src.sayFallback = "兜底";

            var copy = RuleQuery.WithEffect(src, "swapAvatar:小白");

            // id 相同是全部设计的基石：TriggerLimiter 按 id 记账，副本因此
            // 和原规则共用冷却账，不会绕开限流
            Assert.AreEqual("swap", copy.id);
            Assert.AreEqual("L3", copy.level);
            Assert.AreEqual(60f, copy.cooldown);
            Assert.AreEqual(180f, copy.perUserCooldown);
            Assert.AreEqual("换成谁呀", copy.askPrompt);
            Assert.AreEqual("兜底", copy.sayFallback);
            Assert.AreEqual(1, copy.effects.Count);
            Assert.AreEqual("swapAvatar:小白", copy.effects[0]);
        }

        [Test]
        public void 副本的pick强制为all()
        {
            var src = Rule("like30", "L1", "anim:a", "anim:b");
            src.pick = "random";
            var copy = RuleQuery.WithEffect(src, "song:赤伶");
            Assert.AreEqual("all", copy.pick,
                "副本只有一个效果，pick=random 会让它有几率不执行");
        }

        [Test]
        public void 副本不修改源规则()
        {
            var src = Rule("song", "L1", "song:request");
            RuleQuery.WithEffect(src, "song:赤伶");
            Assert.AreEqual(1, src.effects.Count);
            Assert.AreEqual("song:request", src.effects[0]);
        }

        [Test]
        public void 源规则为null时返回null()
        {
            Assert.IsNull(RuleQuery.WithEffect(null, "song:赤伶"));
        }

        // ---------- EffectPrefix / BuildEffect ----------

        [Test]
        public void 前缀与效果串对得上()
        {
            Assert.AreEqual("song:", RuleQuery.EffectPrefix(IntentKind.Song));
            Assert.AreEqual("dance:", RuleQuery.EffectPrefix(IntentKind.Dance));
            Assert.AreEqual("swapAvatar", RuleQuery.EffectPrefix(IntentKind.Avatar));
            Assert.IsNull(RuleQuery.EffectPrefix(IntentKind.None));

            Assert.AreEqual("song:赤伶", RuleQuery.BuildEffect(IntentKind.Song, "赤伶"));
            Assert.AreEqual("dance:极乐净土", RuleQuery.BuildEffect(IntentKind.Dance, "极乐净土"));
            Assert.AreEqual("swapAvatar:小白", RuleQuery.BuildEffect(IntentKind.Avatar, "小白"));
            Assert.AreEqual("song:ask", RuleQuery.BuildEffect(IntentKind.Song, "ask"));
            Assert.IsNull(RuleQuery.BuildEffect(IntentKind.None, "x"));
        }

        // ---------- NameMatch ----------

        [Test]
        public void 名字精确匹配优先()
        {
            var names = new List<string> { "小白兔", "小白", "白" };
            Assert.AreEqual(1, NameMatch.PickIndex(names, "小白"));
        }

        [Test]
        public void 名字大小写不敏感()
        {
            var names = new List<string> { "Miku", "Rin" };
            Assert.AreEqual(0, NameMatch.PickIndex(names, "miku"));
        }

        [Test]
        public void 精确没中时双向子串匹配()
        {
            var names = new List<string> { "初音未来 V4X" };
            Assert.AreEqual(0, NameMatch.PickIndex(names, "初音"));      // 查询是名字的子串
            Assert.AreEqual(0, NameMatch.PickIndex(names, "初音未来 V4X 模型"));  // 名字是查询的子串
        }

        [Test]
        public void 匹配不上返回负一()
        {
            var names = new List<string> { "小白", "小黑" };
            Assert.AreEqual(-1, NameMatch.PickIndex(names, "小红"));
            Assert.AreEqual(-1, NameMatch.PickIndex(names, ""));
            Assert.AreEqual(-1, NameMatch.PickIndex(names, null));
            Assert.AreEqual(-1, NameMatch.PickIndex(null, "小白"));
            Assert.AreEqual(-1, NameMatch.PickIndex(new List<string>(), "小白"));
        }
    }
}
