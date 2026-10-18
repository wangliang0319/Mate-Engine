using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class ActionMarkerTests
    {
        // ---------- TryExtract ----------

        [Test]
        public void 无标记返回false()
        {
            Assert.IsFalse(ActionMarker.TryExtract("今天天气不错呀~", out var k, out var a));
            Assert.AreEqual(IntentKind.None, k);
            Assert.AreEqual("", a);
        }

        [Test]
        public void 空文本返回false()
        {
            Assert.IsFalse(ActionMarker.TryExtract(null, out _, out _));
            Assert.IsFalse(ActionMarker.TryExtract("", out _, out _));
        }

        [Test]
        public void 无参数标记()
        {
            Assert.IsTrue(ActionMarker.TryExtract("好嘞，那我给大家来一段！【唱歌】", out var k, out var a));
            Assert.AreEqual(IntentKind.Song, k);
            Assert.AreEqual("", a);
        }

        [Test]
        public void 带参数标记()
        {
            Assert.IsTrue(ActionMarker.TryExtract("晴天是吧，马上安排！【唱歌:晴天】", out var k, out var a));
            Assert.AreEqual(IntentKind.Song, k);
            Assert.AreEqual("晴天", a);
        }

        [Test]
        public void 三类标记都能识别()
        {
            Assert.AreEqual(IntentKind.Song,   Extract("【唱歌】").kind);
            Assert.AreEqual(IntentKind.Dance,  Extract("【跳舞】").kind);
            Assert.AreEqual(IntentKind.Avatar, Extract("【换角色】").kind);
            Assert.AreEqual(IntentKind.Song,   Extract("【唱歌:赤伶】").kind);
        }

        [Test]
        public void 括号里不是标记词的不算()
        {
            // 【开心】是普通括号话，不是动作标记
            Assert.IsFalse(ActionMarker.TryExtract("今天超开心【开心】", out _, out _));
        }

        [Test]
        public void 未闭合的括号不算()
        {
            // 流式切割的残片：只有半个标记
            Assert.IsFalse(ActionMarker.TryExtract("那我唱咯【唱", out _, out _));
        }

        [Test]
        public void 先跳过非标记括号再找到标记()
        {
            Assert.IsTrue(ActionMarker.TryExtract("【比心】好嘞这就唱！【唱歌:晴天】", out var k, out var a));
            Assert.AreEqual(IntentKind.Song, k);
            Assert.AreEqual("晴天", a);
        }

        [Test]
        public void 标记词后参数为空的带冒号形式不算标记()
        {
            // 「唱歌:」后面没有内容，当成普通括号话跳过，去找下一个括号
            Assert.IsTrue(ActionMarker.TryExtract("【唱歌:】那就随便唱咯【跳舞】", out var k, out var a));
            Assert.AreEqual(IntentKind.Dance, k);
        }

        static (IntentKind kind, string arg) Extract(string s)
        {
            ActionMarker.TryExtract(s, out var k, out var a);
            return (k, a);
        }

        // ---------- Strip ----------

        [Test]
        public void 剥除标记保留正文()
        {
            Assert.AreEqual("好嘞，那我给大家来一段！",
                ActionMarker.Strip("好嘞，那我给大家来一段！【唱歌】"));
        }

        [Test]
        public void 剥除带参数的标记()
        {
            Assert.AreEqual("晴天是吧，马上安排！",
                ActionMarker.Strip("晴天是吧，马上安排！【唱歌:晴天】"));
        }

        [Test]
        public void 没有标记原样返回()
        {
            Assert.AreEqual("今天天气不错呀~", ActionMarker.Strip("今天天气不错呀~"));
        }

        [Test]
        public void 剥除多个标记()
        {
            Assert.AreEqual("来咯来咯！",
                ActionMarker.Strip("来咯来咯！【唱歌】【跳舞】"));
        }

        [Test]
        public void 未闭合的尾巴整段剪掉()
        {
            // 流式残片：半个标记不能被念出来，但前面的正文要保住
            Assert.AreEqual("那我唱咯", ActionMarker.Strip("那我唱咯【唱"));
            Assert.AreEqual("", ActionMarker.Strip("【唱"));
        }

        [Test]
        public void 非标记括号话保留()
        {
            // 【开心】不是动作标记，是正文的一部分，照常朗读
            Assert.AreEqual("今天超开心【开心】", ActionMarker.Strip("今天超开心【开心】"));
        }

        [Test]
        public void 剥除后只剩标记则得到空串()
        {
            Assert.AreEqual("", ActionMarker.Strip("【唱歌:晴天】"));
            // null/空原样穿过——调用方对 null 的预期是「没动它」，不是「变成空串」
            Assert.IsNull(ActionMarker.Strip(null));
            Assert.AreEqual("", ActionMarker.Strip(""));
        }
    }
}
