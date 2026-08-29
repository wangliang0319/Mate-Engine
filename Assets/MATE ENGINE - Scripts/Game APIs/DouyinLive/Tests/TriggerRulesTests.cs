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
    }
}
