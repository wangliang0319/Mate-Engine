using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class ActionArbiterTests
    {
        float clock;
        ActionArbiter arb;
        TriggerGlobal g;

        [SetUp]
        public void SetUp()
        {
            clock = 1000f;
            arb = new ActionArbiter { Now = () => clock };
            g = new TriggerGlobal();
        }

        static ActionRequest Req(string id, string level, string user = "u1")
            => new ActionRequest
            {
                Rule = new TriggerRule { id = id, level = level, source = "gift" },
                UserId = user
            };

        [Test]
        public void L1L2在空闲时直接执行()
        {
            Assert.AreEqual(ArbitrationResult.Execute, arb.Submit(Req("a", "L1"), g));
            Assert.AreEqual(ArbitrationResult.Execute, arb.Submit(Req("b", "L2"), g));
        }

        [Test]
        public void L1在唱歌时照常执行()
        {
            arb.SingingNow = true;
            Assert.AreEqual(ArbitrationResult.Execute, arb.Submit(Req("a", "L1"), g));
        }

        [Test]
        public void L2在唱歌时也执行只是效果会被降级()
        {
            // 降级（只放粒子不播动画）由 EffectRegistry 按 SingingNow 判断，
            // 仲裁层不拦：拦了就连粒子都没有了
            arb.SingingNow = true;
            Assert.AreEqual(ArbitrationResult.Execute, arb.Submit(Req("a", "L2"), g));
        }

        [Test]
        public void L3在有其它L3执行时进队列()
        {
            Assert.AreEqual(ArbitrationResult.Execute, arb.Submit(Req("a", "L3"), g));
            arb.L3Busy = true;
            Assert.AreEqual(ArbitrationResult.Queued, arb.Submit(Req("b", "L3"), g));
            Assert.AreEqual(1, arb.QueuedCount);
        }

        [Test]
        public void 唱歌时L3默认排队等唱完()
        {
            arb.SingingNow = true;
            g.l3InterruptSinging = false;
            Assert.AreEqual(ArbitrationResult.Queued, arb.Submit(Req("a", "L3"), g));
        }

        [Test]
        public void 打开开关后L3可以打断唱歌()
        {
            arb.SingingNow = true;
            g.l3InterruptSinging = true;
            Assert.AreEqual(ArbitrationResult.Execute, arb.Submit(Req("a", "L3"), g));
        }

        [Test]
        public void 队列满时丢最旧的而不是拒绝新的()
        {
            arb.L3Busy = true;
            g.l3QueueSize = 2;
            arb.Submit(Req("first", "L3"), g);
            arb.Submit(Req("second", "L3"), g);
            Assert.AreEqual(ArbitrationResult.DroppedQueueFull, arb.Submit(Req("third", "L3"), g));
            Assert.AreEqual(2, arb.QueuedCount);
            // 最旧的 first 被挤掉
            arb.L3Busy = false;
            Assert.AreEqual("second", arb.TryDequeueL3(g).Rule.id);
        }

        [Test]
        public void 队列里已有同id的L3不重复入队()
        {
            arb.L3Busy = true;
            arb.Submit(Req("dance", "L3"), g);
            Assert.AreEqual(ArbitrationResult.Queued, arb.Submit(Req("dance", "L3", "u2"), g));
            Assert.AreEqual(1, arb.QueuedCount);
        }

        [Test]
        public void 忙碌时不出队()
        {
            arb.L3Busy = true;
            arb.Submit(Req("a", "L3"), g);
            Assert.IsNull(arb.TryDequeueL3(g));
            arb.L3Busy = false;
            Assert.IsNotNull(arb.TryDequeueL3(g));
        }

        [Test]
        public void 唱歌未结束时不出队()
        {
            arb.L3Busy = true;
            arb.Submit(Req("a", "L3"), g);
            arb.L3Busy = false;
            arb.SingingNow = true;
            Assert.IsNull(arb.TryDequeueL3(g));
            arb.SingingNow = false;
            Assert.IsNotNull(arb.TryDequeueL3(g));
        }
    }
}
