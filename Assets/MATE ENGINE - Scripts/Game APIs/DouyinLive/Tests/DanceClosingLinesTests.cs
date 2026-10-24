using System.Collections.Generic;
using NUnit.Framework;

namespace DouyinLive.Tests
{
    // 跳完一支舞的谢幕词。以前跳完就安静下来，直播间经常冷场几秒。
    public class DanceClosingLinesTests
    {
        [Test]
        public void 谢幕词带上舞名()
        {
            StringAssert.Contains("sparkle", DanceClosingLines.Pick("sparkle", 0));
        }

        [Test]
        public void 不同随机值会轮换台词()
        {
            var lines = new HashSet<string>();
            for (int roll = 0; roll < 6; roll++)
                lines.Add(DanceClosingLines.Pick("sparkle", roll));
            Assert.GreaterOrEqual(lines.Count, 2);
        }

        [Test]
        public void 舞名为空也能说一句完整的()
        {
            Assert.IsNotEmpty(DanceClosingLines.Pick("", 0));
        }

        [Test]
        public void 随机值为负也不越界()
        {
            Assert.IsNotEmpty(DanceClosingLines.Pick("x", -3));
        }
    }
}
