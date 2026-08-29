using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class SmokeTests
    {
        [Test]
        public void DouyinEvent_可以从测试程序集访问()
        {
            var ev = new DouyinEvent { Type = DouyinMsgType.Chat, Content = "拍头" };
            Assert.AreEqual(DouyinMsgType.Chat, ev.Type);
            Assert.AreEqual("拍头", ev.Content);
        }
    }
}
