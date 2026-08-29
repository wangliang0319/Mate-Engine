using System.Collections.Generic;
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class TriggerMatcherTests
    {
        static TriggerConfig Cfg(params TriggerRule[] rules)
            => new TriggerConfig { rules = new List<TriggerRule>(rules) };

        static DouyinEvent Chat(string text, string userId = "u1")
            => new DouyinEvent { Type = DouyinMsgType.Chat, Content = text, UserId = userId };

        static DouyinEvent Gift(int diamond, int count = 1, string name = "玫瑰")
            => new DouyinEvent { Type = DouyinMsgType.Gift, GiftName = name, DiamondCount = diamond, GiftCount = count, UserId = "u1" };

        static TriggerRule ChatRule(string id, params string[] words)
            => new TriggerRule { id = id, source = "chat", keywords = new List<string>(words), effects = new List<string> { "anim:Headpat" } };

        [Test]
        public void 关键词包含匹配()
        {
            var cfg = Cfg(ChatRule("pat", "拍头"));
            Assert.AreEqual("pat", TriggerMatcher.Match(Chat("主播拍头！"), cfg, new MatchContext())?.id);
        }

        [Test]
        public void 关键词不命中返回null()
        {
            var cfg = Cfg(ChatRule("pat", "拍头"));
            Assert.IsNull(TriggerMatcher.Match(Chat("今天天气不错"), cfg, new MatchContext()));
        }

        [Test]
        public void 数组顺序即优先级第一条命中即停()
        {
            var cfg = Cfg(ChatRule("first", "换装"), ChatRule("second", "换装"));
            Assert.AreEqual("first", TriggerMatcher.Match(Chat("换装"), cfg, new MatchContext())?.id);
        }

        [Test]
        public void 禁用的规则被跳过()
        {
            var disabled = ChatRule("off", "换装"); disabled.enabled = false;
            var cfg = Cfg(disabled, ChatRule("on", "换装"));
            Assert.AreEqual("on", TriggerMatcher.Match(Chat("换装"), cfg, new MatchContext())?.id);
        }

        [Test]
        public void 正则匹配()
        {
            var rule = new TriggerRule { id = "num", source = "chat", regex = @"^\d+$", effects = new List<string> { "menu" } };
            Assert.AreEqual("num", TriggerMatcher.Match(Chat("666"), Cfg(rule), new MatchContext())?.id);
            Assert.IsNull(TriggerMatcher.Match(Chat("666a"), Cfg(rule), new MatchContext()));
        }

        [Test]
        public void 非法正则不抛异常只是不命中()
        {
            var rule = new TriggerRule { id = "bad", source = "chat", regex = "[", effects = new List<string> { "menu" } };
            Assert.DoesNotThrow(() => TriggerMatcher.Match(Chat("x"), Cfg(rule), new MatchContext()));
            Assert.IsNull(TriggerMatcher.Match(Chat("x"), Cfg(rule), new MatchContext()));
        }

        [Test]
        public void 来源不同的规则不会被误匹配()
        {
            var cfg = Cfg(new TriggerRule { id = "g", source = "gift", effects = new List<string> { "menu" } });
            Assert.IsNull(TriggerMatcher.Match(Chat("玫瑰"), cfg, new MatchContext()));
        }

        // ---- 点赞 ----

        [Test]
        public void everyN在跨过整数倍时命中()
        {
            var cfg = Cfg(new TriggerRule { id = "l", source = "like", everyN = 30, effects = new List<string> { "menu" } });
            var ev = new DouyinEvent { Type = DouyinMsgType.Like, LikeCount = 5 };
            // 28 → 33 跨过了 30
            Assert.AreEqual("l", TriggerMatcher.Match(ev, cfg, new MatchContext { LikeTotalBefore = 28, LikeTotalAfter = 33 })?.id);
            // 31 → 36 没跨过下一个整数倍
            Assert.IsNull(TriggerMatcher.Match(ev, cfg, new MatchContext { LikeTotalBefore = 31, LikeTotalAfter = 36 }));
        }

        [Test]
        public void 里程碑只在跨过的那一次命中()
        {
            var cfg = Cfg(new TriggerRule { id = "m", source = "like", milestone = 3000, effects = new List<string> { "menu" } });
            var ev = new DouyinEvent { Type = DouyinMsgType.Like, LikeCount = 10 };
            Assert.AreEqual("m", TriggerMatcher.Match(ev, cfg, new MatchContext { LikeTotalBefore = 2995, LikeTotalAfter = 3005 })?.id);
            Assert.IsNull(TriggerMatcher.Match(ev, cfg, new MatchContext { LikeTotalBefore = 3005, LikeTotalAfter = 3015 }));
        }

        // ---- 礼物 ----

        [Test]
        public void 礼物档位边界_总价值口径()
        {
            var cfg = TriggerConfig.Defaults();
            cfg.global.giftUseTotalValue = true;
            Assert.AreEqual("gift1", TriggerMatcher.Match(Gift(9), cfg, new MatchContext())?.id);
            Assert.AreEqual("gift2", TriggerMatcher.Match(Gift(10), cfg, new MatchContext())?.id);
            Assert.AreEqual("gift2", TriggerMatcher.Match(Gift(99), cfg, new MatchContext())?.id);
            Assert.AreEqual("gift3", TriggerMatcher.Match(Gift(100), cfg, new MatchContext())?.id);
            // 连刷 20 个 1 抖币 = 20，走中档
            Assert.AreEqual("gift2", TriggerMatcher.Match(Gift(1, 20), cfg, new MatchContext())?.id);
        }

        [Test]
        public void 礼物档位_只看单价口径()
        {
            var cfg = TriggerConfig.Defaults();
            cfg.global.giftUseTotalValue = false;
            // 同样是连刷 20 个 1 抖币，只看单价仍是小额档
            Assert.AreEqual("gift1", TriggerMatcher.Match(Gift(1, 20), cfg, new MatchContext())?.id);
        }

        [Test]
        public void 礼物名过滤()
        {
            var rule = new TriggerRule { id = "rose", source = "gift", giftName = "玫瑰", effects = new List<string> { "menu" } };
            Assert.AreEqual("rose", TriggerMatcher.Match(Gift(1, 1, "玫瑰"), Cfg(rule), new MatchContext())?.id);
            Assert.IsNull(TriggerMatcher.Match(Gift(1, 1, "棒棒糖"), Cfg(rule), new MatchContext()));
        }

        // ---- 无条件来源 ----

        [Test]
        public void 关注事件无条件命中follow规则()
        {
            var cfg = TriggerConfig.Defaults();
            var ev = new DouyinEvent { Type = DouyinMsgType.Follow, Nickname = "小明", UserId = "u9" };
            Assert.AreEqual("follow", TriggerMatcher.Match(ev, cfg, new MatchContext())?.id);
        }

        [Test]
        public void 空配置和空事件不抛异常()
        {
            Assert.IsNull(TriggerMatcher.Match(null, TriggerConfig.Defaults(), new MatchContext()));
            Assert.IsNull(TriggerMatcher.Match(Chat("拍头"), null, new MatchContext()));
            Assert.IsNull(TriggerMatcher.Match(Chat("拍头"), new TriggerConfig(), null));
        }
    }
}
