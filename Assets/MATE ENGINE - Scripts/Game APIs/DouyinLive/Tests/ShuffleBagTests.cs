using System.Collections.Generic;
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class ShuffleBagTests
    {
        [Test]
        public void 一轮之内每个索引恰好出现一次()
        {
            var bag = new ShuffleBag(seed: 1);
            bag.Reset(5);
            var seen = new List<int>();
            for (int i = 0; i < 5; i++) seen.Add(bag.Next());
            seen.Sort();
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, seen);
        }

        [Test]
        public void 取完自动重洗继续供应()
        {
            var bag = new ShuffleBag(seed: 2);
            bag.Reset(3);
            for (int i = 0; i < 30; i++) Assert.GreaterOrEqual(bag.Next(), 0);
        }

        [Test]
        public void 重洗时不会紧接着重复上一支()
        {
            var bag = new ShuffleBag(seed: 3);
            bag.Reset(4);
            int prev = -1;
            for (int i = 0; i < 200; i++)
            {
                int cur = bag.Next();
                Assert.AreNotEqual(prev, cur, "跨轮出现了相邻重复");
                prev = cur;
            }
        }

        [Test]
        public void 只有一个元素时允许重复否则无从选择()
        {
            var bag = new ShuffleBag(seed: 4);
            bag.Reset(1);
            Assert.AreEqual(0, bag.Next());
            Assert.AreEqual(0, bag.Next());
        }

        [Test]
        public void 空集合返回负一()
        {
            var bag = new ShuffleBag(seed: 5);
            bag.Reset(0);
            Assert.AreEqual(-1, bag.Next());
        }

        [Test]
        public void 舞包数量变化后重置()
        {
            var bag = new ShuffleBag(seed: 6);
            bag.Reset(3);
            bag.Next();
            bag.Reset(10);
            for (int i = 0; i < 10; i++) Assert.Less(bag.Next(), 10);
        }
    }
}
