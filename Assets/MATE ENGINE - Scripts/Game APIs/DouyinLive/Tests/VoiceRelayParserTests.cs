using NUnit.Framework;
using System.Collections.Generic;

namespace DouyinLive.Tests
{
    public class VoiceRelayParserTests
    {
        static readonly List<string> Prefixes = new List<string> { "告诉大家", "跟大家说", "通知大家" };

        [Test]
        public void 开头词剥出传话内容()
        {
            Assert.IsTrue(VoiceRelayParser.TryExtractRelay("告诉大家八点下播", Prefixes, out var msg));
            Assert.AreEqual("八点下播", msg);
        }

        [Test]
        public void 开头词后的标点一起剥掉_句尾标点保留()
        {
            // 句尾的「。」「！」留给 TTS 断句和语气
            Assert.IsTrue(VoiceRelayParser.TryExtractRelay("跟大家说，等会儿有抽奖哦！", Prefixes, out var msg));
            Assert.AreEqual("等会儿有抽奖哦！", msg);
        }

        [Test]
        public void 没有开头词不算传话()
        {
            Assert.IsFalse(VoiceRelayParser.TryExtractRelay("今天唱什么好", Prefixes, out _));
        }

        [Test]
        public void 开头词不在句首不算传话()
        {
            // 「我想告诉大家…」是在跟角色说话，不是让她播报
            Assert.IsFalse(VoiceRelayParser.TryExtractRelay("我想告诉大家一件事", Prefixes, out _));
        }

        [Test]
        public void 只有开头词不算传话()
        {
            Assert.IsFalse(VoiceRelayParser.TryExtractRelay("告诉大家", Prefixes, out _));
            Assert.IsFalse(VoiceRelayParser.TryExtractRelay("告诉大家。", Prefixes, out _));
        }

        [Test]
        public void 同时命中取最长开头词()
        {
            // 「告诉」先命中会把「大家」留在播报里
            var p = new List<string> { "告诉", "告诉大家" };
            Assert.IsTrue(VoiceRelayParser.TryExtractRelay("告诉大家八点下播", p, out var msg));
            Assert.AreEqual("八点下播", msg);
        }

        [Test]
        public void 开头词前的空格和标点不影响()
        {
            Assert.IsTrue(VoiceRelayParser.TryExtractRelay(" ，告诉大家八点下播", Prefixes, out var msg));
            Assert.AreEqual("八点下播", msg);
        }

        [Test]
        public void 空输入和空词表()
        {
            Assert.IsFalse(VoiceRelayParser.TryExtractRelay(null, Prefixes, out _));
            Assert.IsFalse(VoiceRelayParser.TryExtractRelay("", Prefixes, out _));
            Assert.IsFalse(VoiceRelayParser.TryExtractRelay("告诉大家八点下播", new List<string>(), out _));
            Assert.IsFalse(VoiceRelayParser.TryExtractRelay("告诉大家八点下播", null, out _));
        }

        [Test]
        public void 词表里的空项不参与()
        {
            // 空串开头词会让任何句子都命中
            var p = new List<string> { "", null, "告诉大家" };
            Assert.IsFalse(VoiceRelayParser.TryExtractRelay("今天唱什么好", p, out _));
            Assert.IsTrue(VoiceRelayParser.TryExtractRelay("告诉大家八点下播", p, out var msg));
            Assert.AreEqual("八点下播", msg);
        }
    }
}
