using NUnit.Framework;

namespace DouyinLive.Tests
{
    // 直播中的动作轮换：空闲姿势和舞蹈都要随机、且不连续重复同一支。
    // 场景里明明有 20 支空闲动作、13 支舞蹈，以前按顺序循环，看久了完全可预测。
    public class MotionVarietyTests
    {
        [Test]
        public void 只有一支动作时_永远回到0()
        {
            Assert.AreEqual(0, MotionVariety.NextIndex(0, 1, 12345));
        }

        [Test]
        public void 不会连续重复当前动作()
        {
            for (int current = 0; current < 5; current++)
                for (int roll = 0; roll < 200; roll++)
                    Assert.AreNotEqual(current, MotionVariety.NextIndex(current, 5, roll));
        }

        [Test]
        public void 除当前外每个动作都能抽到_且只出现一次()
        {
            // 5 支动作、当前第 2 支：4 个随机值应该正好铺满其余 4 支，不偏不重
            var seen = new int[4];
            for (int roll = 0; roll < 4; roll++)
                seen[roll] = MotionVariety.NextIndex(2, 5, roll);
            CollectionAssert.AreEquivalent(new[] { 0, 1, 3, 4 }, seen);
        }

        [Test]
        public void 随机值为负也能落到有效范围()
        {
            for (int roll = -50; roll < 0; roll++)
            {
                int next = MotionVariety.NextIndex(1, 6, roll);
                Assert.GreaterOrEqual(next, 0);
                Assert.Less(next, 6);
                Assert.AreNotEqual(1, next);
            }
        }

        [Test]
        public void 当前索引越界也不返回非法索引()
        {
            // 场景里 totalIdleAnimations 被人改过时，残留的 idleState 可能越界
            Assert.Less(MotionVariety.NextIndex(99, 6, 0), 6);
            Assert.GreaterOrEqual(MotionVariety.NextIndex(-3, 6, 0), 0);
        }
    }
}
