using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class TriggerLimiterTests
    {
        float clock;
        TriggerLimiter lim;
        TriggerGlobal g;

        [SetUp]
        public void SetUp()
        {
            clock = 1000f;                       // 从非 0 开始，防止「未初始化 = 0」的假通过
            lim = new TriggerLimiter { Now = () => clock };
            g = new TriggerGlobal();
        }

        static TriggerRule Rule(string id = "r", string source = "chat", string level = "L1",
                                float cooldown = 0f, float perUser = -1f)
            => new TriggerRule { id = id, source = source, level = level, cooldown = cooldown, perUserCooldown = perUser };

        [Test]
        public void 首次触发直接通过()
        {
            Assert.AreEqual(GateResult.Pass, lim.Check(Rule(), g, "u1"));
        }

        [Test]
        public void 源冷却在时间未到时拦截()
        {
            var r = Rule();
            lim.Commit(r, g, "u1");
            clock += 0.2f;                                  // chatCooldown = 0.5
            Assert.AreEqual(GateResult.SourceCooldown, lim.Check(r, g, "u2"));
            clock += 0.4f;                                  // 累计 0.6 > 0.5
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "u2"));
        }

        [Test]
        public void 单人冷却只冻结该用户不影响别人()
        {
            g.chatCooldown = 0f;                            // 排除源冷却干扰
            var r = Rule();
            lim.Commit(r, g, "spammer");
            Assert.AreEqual(GateResult.UserCooldown, lim.Check(r, g, "spammer"));
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "innocent"));
        }

        [Test]
        public void 规则自带的单人冷却覆盖全局值()
        {
            g.chatCooldown = 0f;
            g.perUserCooldown = 5f;
            var r = Rule(perUser: 100f);
            lim.Commit(r, g, "u1");
            clock += 10f;                                   // 超过全局 5 秒，但没超过规则的 100 秒
            Assert.AreEqual(GateResult.UserCooldown, lim.Check(r, g, "u1"));
        }

        [Test]
        public void 规则冷却对所有用户生效()
        {
            g.chatCooldown = 0f;
            g.perUserCooldown = 0f;
            var r = Rule(cooldown: 60f);
            lim.Commit(r, g, "u1");
            Assert.AreEqual(GateResult.RuleCooldown, lim.Check(r, g, "another"));
            clock += 61f;
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "another"));
        }

        [Test]
        public void L3最小间隔跨规则生效()
        {
            g.chatCooldown = 0f;
            g.perUserCooldown = 0f;
            var swap = Rule("swap", level: "L3");
            var dance = Rule("dance", level: "L3");
            lim.Commit(swap, g, "u1");
            Assert.AreEqual(GateResult.LevelInterval, lim.Check(dance, g, "u2"));
            clock += g.l3MinInterval + 1f;
            Assert.AreEqual(GateResult.Pass, lim.Check(dance, g, "u2"));
        }

        [Test]
        public void L3的间隔不影响L1()
        {
            g.chatCooldown = 0f;
            g.perUserCooldown = 0f;
            lim.Commit(Rule("swap", level: "L3"), g, "u1");
            Assert.AreEqual(GateResult.Pass, lim.Check(Rule("pat", level: "L1"), g, "u2"));
        }

        [Test]
        public void 空UserId时单人冷却退化为不限制()
        {
            g.chatCooldown = 0f;
            var r = Rule();
            lim.Commit(r, g, null);
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, null));
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, ""));
        }

        [Test]
        public void 不同来源的源冷却互不干扰()
        {
            var chat = Rule("c", source: "chat");
            var gift = Rule("gf", source: "gift");
            lim.Commit(chat, g, "u1");
            Assert.AreEqual(GateResult.Pass, lim.Check(gift, g, "u2"));
        }

        [Test]
        public void Check不产生副作用可重复调用()
        {
            var r = Rule();
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "u1"));
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "u1"));
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "u1"));
        }

        [Test]
        public void PruneUsers清掉长时间不活跃的记账()
        {
            g.chatCooldown = 0f;
            var r = Rule(perUser: 99999f);
            lim.Commit(r, g, "u1");
            Assert.AreEqual(1, lim.TrackedUserCount);
            clock += 700f;
            lim.PruneUsers(600f);
            Assert.AreEqual(0, lim.TrackedUserCount);
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "u1"));
        }

        [Test]
        public void source为null时Check与Commit共用同一键不抛异常()
        {
            var r = Rule(source: null);
            lim.Commit(r, g, "u1");
            Assert.DoesNotThrow(() => lim.Check(r, g, "u2"));
            Assert.AreEqual(GateResult.SourceCooldown, lim.Check(r, g, "u2"));
            clock += g.chatCooldown + 0.1f;                 // source=null 走 chatCooldown 兜底
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "u2"));
        }
    }
}
