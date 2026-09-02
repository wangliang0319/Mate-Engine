using System.Collections.Generic;
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class TriggerRulesTests
    {
        [Test]
        public void 默认配置的规则id唯一()
        {
            var cfg = TriggerConfig.Defaults();
            var seen = new HashSet<string>();
            foreach (var r in cfg.rules)
                Assert.IsTrue(seen.Add(r.id), $"重复的规则 id: {r.id}");
        }

        [Test]
        public void 默认配置每条规则都有效果()
        {
            foreach (var r in TriggerConfig.Defaults().rules)
                Assert.IsNotEmpty(r.effects, $"规则 {r.id} 没有配任何效果");
        }

        [Test]
        public void 礼物三档的抖币区间不重叠且无空洞()
        {
            var cfg = TriggerConfig.Defaults();
            var g1 = cfg.rules.Find(r => r.id == "gift1");
            var g2 = cfg.rules.Find(r => r.id == "gift2");
            var g3 = cfg.rules.Find(r => r.id == "gift3");
            Assert.AreEqual(g1.maxDiamond + 1, g2.minDiamond);
            Assert.AreEqual(g2.maxDiamond + 1, g3.minDiamond);
        }

        [Test]
        public void 层级写错时降级为L1而不是L3()
        {
            Assert.AreEqual(1, new TriggerRule { level = "" }.LevelOrDefault);
            Assert.AreEqual(1, new TriggerRule { level = "l3" }.LevelOrDefault);   // 大小写不匹配
            Assert.AreEqual(3, new TriggerRule { level = "L3" }.LevelOrDefault);
        }

        [Test]
        public void 默认配置里引用的Animator参数都是项目里真实存在的()
        {
            // AvatarAnimatorControllerV2 目前只有这 5 个可用于一次性互动的参数。
            // 以后加了新动画，把参数名补进这个白名单。
            var known = new HashSet<string>
            { "Headpat", "HairStroke", "HoverFaceTrigger", "HoverTrigger", "IntimeRegion" };

            foreach (var r in TriggerConfig.Defaults().rules)
                foreach (var e in r.effects)
                    if (e.StartsWith("anim:"))
                        Assert.IsTrue(known.Contains(e.Substring(5)), $"规则 {r.id} 用了不存在的 Animator 参数");
        }

        [Test]
        public void 默认配置里引用的粒子主题都存在()
        {
            // CustomVRM.prefab 目前只登记了这一个主题
            var known = new HashSet<string> { "Dance Trail Blue" };
            foreach (var r in TriggerConfig.Defaults().rules)
                foreach (var e in r.effects)
                    if (e.StartsWith("particle:"))
                        Assert.IsTrue(known.Contains(e.Substring(9)), $"规则 {r.id} 用了不存在的粒子主题");
        }

        [Test]
        public void 新增的全局字段有合理默认值()
        {
            var g = new TriggerGlobal();
            Assert.AreEqual(30f, g.slotWindowSeconds);
            Assert.IsTrue(g.intentFallbackEnabled);
        }

        [Test]
        public void 规则的追问文案默认为空表示用内置文案()
        {
            Assert.AreEqual("", new TriggerRule().askPrompt);
        }

        [Test]
        public void 默认规则集里点舞和换角色都支持追问()
        {
            var cfg = TriggerConfig.Defaults();
            var dance = cfg.rules.Find(r => r.id == "reqdance");
            var swap = cfg.rules.Find(r => r.id == "swap");
            Assert.IsNotNull(dance);
            Assert.IsNotNull(swap);
            Assert.Contains("dance:request", dance.effects);
            Assert.Contains("swapAvatar:request", swap.effects);
        }

        [Test]
        public void 默认规则集里点歌仍然是request()
        {
            var song = TriggerConfig.Defaults().rules.Find(r => r.id == "song");
            Assert.IsNotNull(song);
            Assert.Contains("song:request", song.effects);
        }
    }
}
