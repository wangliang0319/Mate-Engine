using NUnit.Framework;
using System.Collections.Generic;

namespace DouyinLive.Tests
{
    public class OperatorCommandParserTests
    {
        static readonly OperatorCommandWords W = new OperatorCommandWords();

        static OperatorCommandKind Kind(string text)
        {
            OperatorCommandParser.TryParse(text, W, out var k, out _);
            return k;
        }

        [Test]
        public void 切场景_游戏陪播()
        {
            Assert.IsTrue(OperatorCommandParser.TryParse("切到游戏陪播", W, out var k, out var s));
            Assert.AreEqual(OperatorCommandKind.SwitchScene, k);
            Assert.AreEqual(LiveScene.Game, s);
        }

        [Test]
        public void 切场景_各种说法()
        {
            void Check(string text, LiveScene want)
            {
                Assert.IsTrue(OperatorCommandParser.TryParse(text, W, out var k, out var s), text);
                Assert.AreEqual(OperatorCommandKind.SwitchScene, k, text);
                Assert.AreEqual(want, s, text);
            }
            Check("切换到搭档模式", LiveScene.Duo);
            Check("换到独播", LiveScene.Solo);
            Check("切到双人", LiveScene.Duo);
            Check("切到陪播模式吧", LiveScene.Game);
            Check("切到自己播", LiveScene.Solo);
        }

        [Test]
        public void 切场景_场景名识别不出()
        {
            Assert.IsTrue(OperatorCommandParser.TryParse("切到唱歌模式", W, out var k, out _));
            Assert.AreEqual(OperatorCommandKind.UnknownScene, k);
        }

        [Test]
        public void 停止表演()
        {
            Assert.AreEqual(OperatorCommandKind.StopPerformance, Kind("别唱了"));
            Assert.AreEqual(OperatorCommandKind.StopPerformance, Kind("别跳了"));
            Assert.AreEqual(OperatorCommandKind.StopPerformance, Kind("停一下吧"));
        }

        [Test]
        public void 清空弹幕()
        {
            Assert.AreEqual(OperatorCommandKind.ClearQueue, Kind("别念了"));
            Assert.AreEqual(OperatorCommandKind.ClearQueue, Kind("清空弹幕"));
        }

        [Test]
        public void 安静()
        {
            Assert.AreEqual(OperatorCommandKind.Quiet, Kind("安静一会儿"));
            Assert.AreEqual(OperatorCommandKind.Quiet, Kind("先别说话"));
        }

        [Test]
        public void 恢复()
        {
            Assert.AreEqual(OperatorCommandKind.Resume, Kind("可以说话了"));
            Assert.AreEqual(OperatorCommandKind.Resume, Kind("恢复"));
        }

        [Test]
        public void 只认句首()
        {
            // 「我想让你别唱了」是在跟她聊天，不是指令
            Assert.IsFalse(OperatorCommandParser.TryParse("我想让你别唱了", W, out _, out _));
        }

        [Test]
        public void 句首标点不影响()
        {
            Assert.AreEqual(OperatorCommandKind.StopPerformance, Kind("，别唱了！"));
        }

        [Test]
        public void 非指令_交给后续管线()
        {
            Assert.IsFalse(OperatorCommandParser.TryParse("唱一首黄昏", W, out _, out _));
            Assert.IsFalse(OperatorCommandParser.TryParse("告诉大家八点下播", W, out _, out _));
            Assert.IsFalse(OperatorCommandParser.TryParse("今天直播什么好", W, out _, out _));
        }

        [Test]
        public void 空输入()
        {
            Assert.IsFalse(OperatorCommandParser.TryParse(null, W, out _, out _));
            Assert.IsFalse(OperatorCommandParser.TryParse("", W, out _, out _));
            Assert.IsFalse(OperatorCommandParser.TryParse("别唱了", null, out _, out _));
        }

        [Test]
        public void 自定义说法()
        {
            var w = new OperatorCommandWords { Stop = new List<string> { "打住" } };
            Assert.IsTrue(OperatorCommandParser.TryParse("打住", w, out var k, out _));
            Assert.AreEqual(OperatorCommandKind.StopPerformance, k);
            Assert.IsFalse(OperatorCommandParser.TryParse("别唱了", w, out _, out _));   // 默认说法被替换
        }

        [Test]
        public void 同时命中取最长()
        {
            // 「停」是停止表演、「停止念」是清空弹幕：「停止念弹幕」要按更长的算
            var w = new OperatorCommandWords
            {
                Stop = new List<string> { "停" },
                ClearQueue = new List<string> { "停止念" }
            };
            Assert.IsTrue(OperatorCommandParser.TryParse("停止念弹幕", w, out var k, out _));
            Assert.AreEqual(OperatorCommandKind.ClearQueue, k);
        }
    }
}
