using System.Collections.Generic;
using NUnit.Framework;

namespace DouyinLive.Tests
{
    // 舞蹈轮换的「一支舞」单位：左右版本、-1/-2 分段、不同目录里的同名包都算同一支。
    // 491 个包里这类重复不少，按文件逐个洗牌会让观众连着看到两个几乎一样的动作。
    public class DanceRosterTests
    {
        [Test]
        public void 同名舞包只占一个轮换位()
        {
            var r = new DanceRoster();
            r.Sync(new[] { "Hot Milk", "Hot Milk" });
            Assert.AreEqual(1, r.GroupCount);
            Assert.AreEqual(0, r.Next(0));
        }

        [Test]
        public void 一轮之内左右版本不会各来一遍()
        {
            var titles = new[] { "sparkle_左_", "sparkle_右_", "Nee-1", "Nee-2" };
            var r = new DanceRoster();
            r.Sync(titles);

            for (int round = 0; round < 20; round++)
            {
                int a = r.Next(round);
                int b = r.Next(round + 7);
                Assert.AreNotEqual(
                    DanceTitleMatch.VariantKey(titles[a]),
                    DanceTitleMatch.VariantKey(titles[b]));
            }
        }

        [Test]
        public void 点名播过之后随机不会立刻再来同一支()
        {
            var titles = new[] { "sparkle_左_", "Nee-1", "ECHO" };
            var r = new DanceRoster();
            r.Sync(titles);
            r.NotePlayed(0);

            int next = r.Next(0);
            Assert.AreNotEqual(
                DanceTitleMatch.VariantKey(titles[0]),
                DanceTitleMatch.VariantKey(titles[next]));
        }

        [Test]
        public void 数量变化后重建分组()
        {
            var r = new DanceRoster();
            r.Sync(new[] { "A", "B", "C" });
            r.Sync(new[] { "A", "A" });
            Assert.AreEqual(1, r.GroupCount);
            Assert.AreEqual(0, r.Next(0));
        }

        [Test]
        public void 越界索引不会打乱轮换()
        {
            var r = new DanceRoster();
            r.Sync(new[] { "A", "B" });
            Assert.DoesNotThrow(() => r.NotePlayed(99));
            int next = r.Next(0);
            Assert.GreaterOrEqual(next, 0);
            Assert.Less(next, 2);
        }
    }
}
