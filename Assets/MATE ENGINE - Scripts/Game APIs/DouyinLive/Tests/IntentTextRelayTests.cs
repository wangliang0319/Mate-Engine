using NUnit.Framework;

namespace DouyinLive.Tests
{
    // 语音传话分类的数据层：relay/chat 意图解析 + 传话判定 IsRelayVerdict。
    // 分类结果永远只当数据用，绝不当指令执行（IntentText 一贯原则）。
    public class IntentTextRelayTests
    {
        [Test]
        public void Relay意图解析为Relay()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"relay\",\"arg\":\"\"}", out var k, out _));
            Assert.AreEqual(IntentKind.Relay, k);
        }

        [Test]
        public void Chat意图解析为None()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"chat\"}", out var k, out _));
            Assert.AreEqual(IntentKind.None, k);
        }

        [Test]
        public void None意图照旧解析为None()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"none\"}", out var k, out _));
            Assert.AreEqual(IntentKind.None, k);
        }

        [Test]
        public void 围栏包裹的Relay也能解析()
        {
            // 大模型常在 JSON 外套 ```json 围栏，解析必须剥掉
            var raw = "```json\n{\"intent\": \"relay\", \"arg\": \"等会儿有抽奖哦\"}\n```";
            Assert.IsTrue(IntentText.TryParseIntentJson(raw, out var k, out _));
            Assert.AreEqual(IntentKind.Relay, k);
        }

        [Test]
        public void 原有四类解析不受影响()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"song\",\"arg\":\"黄昏\"}", out var k1, out var a1));
            Assert.AreEqual(IntentKind.Song, k1);
            Assert.AreEqual("黄昏", a1);
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"dance\"}", out var k2, out _));
            Assert.AreEqual(IntentKind.Dance, k2);
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"avatar\"}", out var k3, out _));
            Assert.AreEqual(IntentKind.Avatar, k3);
        }

        [Test]
        public void 非法输入照旧返回False()
        {
            Assert.IsFalse(IntentText.TryParseIntentJson("我觉得他是想点歌", out _, out _));
            Assert.IsFalse(IntentText.TryParseIntentJson("{\"intent\":\"不知道\"}", out _, out _));
            Assert.IsFalse(IntentText.TryParseIntentJson(null, out _, out _));
        }

        // ---------- IsRelayVerdict：VoiceIntentRouter 的传话判定 ----------

        [Test]
        public void Relay判定为传话()
        {
            Assert.IsTrue(IntentText.IsRelayVerdict("{\"intent\":\"relay\"}"));
        }

        [Test]
        public void Chat和None判定为聊天()
        {
            Assert.IsFalse(IntentText.IsRelayVerdict("{\"intent\":\"chat\"}"));
            Assert.IsFalse(IntentText.IsRelayVerdict("{\"intent\":\"none\"}"));
        }

        [Test]
        public void 表演意图不算传话()
        {
            // 分类器偶尔越权输出表演意图：这不是传话，必须回聊天路径
            Assert.IsFalse(IntentText.IsRelayVerdict("{\"intent\":\"song\",\"arg\":\"黄昏\"}"));
        }

        [Test]
        public void 解析失败判定为聊天()
        {
            Assert.IsFalse(IntentText.IsRelayVerdict("我觉得他在跟你聊天"));
            Assert.IsFalse(IntentText.IsRelayVerdict(null));
            Assert.IsFalse(IntentText.IsRelayVerdict(""));
        }
    }
}
