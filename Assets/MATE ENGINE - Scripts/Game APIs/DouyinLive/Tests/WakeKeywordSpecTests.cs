using NUnit.Framework;
using System.Collections.Generic;

namespace DouyinLive.Tests
{
    public class WakeKeywordSpecTests
    {
        static readonly HashSet<string> Vocab = new HashSet<string> { "x", "iǎo", "m", "ěi" };
        const string Good = "x iǎo m ěi x iǎo m ěi @小美小美";

        [Test]
        public void 合法唤醒词_通过并原样输出()
        {
            var r = WakeKeywordSpec.Build(new[] { Good }, Vocab);
            Assert.AreEqual(1, r.ValidCount);
            Assert.AreEqual(0, r.Errors.Count);
            Assert.AreEqual(Good + "\n", r.KeywordsText);
        }

        [Test]
        public void 音节不在词表_报出该音节()
        {
            // 最常见的错：漏写声调
            var r = WakeKeywordSpec.Build(new[] { "x iao m ěi @小美" }, Vocab);
            Assert.AreEqual(0, r.ValidCount);
            StringAssert.Contains("iao", r.Errors[0]);
        }

        [Test]
        public void 缺少显示名_报错()
        {
            var r = WakeKeywordSpec.Build(new[] { "x iǎo m ěi" }, Vocab);
            Assert.AreEqual(0, r.ValidCount);
            StringAssert.Contains("@", r.Errors[0]);
        }

        [Test]
        public void 显示名不在最后_报错()
        {
            var r = WakeKeywordSpec.Build(new[] { "x iǎo @小美 m ěi" }, Vocab);
            Assert.AreEqual(0, r.ValidCount);
        }

        [Test]
        public void 空项_报错并跳过()
        {
            var r = WakeKeywordSpec.Build(new[] { "", null, Good }, Vocab);
            Assert.AreEqual(1, r.ValidCount);
            Assert.AreEqual(2, r.Errors.Count);
        }

        [Test]
        public void 有效无效混合_只输出有效()
        {
            var r = WakeKeywordSpec.Build(new[] { Good, "x iao @错" }, Vocab);
            Assert.AreEqual(Good + "\n", r.KeywordsText);
            Assert.AreEqual(1, r.Errors.Count);
        }

        [Test]
        public void 灵敏度后缀合法_通过()
        {
            var r = WakeKeywordSpec.Build(new[] { "x iǎo m ěi x iǎo m ěi :1.5 #0.2 @小美小美" }, Vocab);
            Assert.AreEqual(1, r.ValidCount);
        }

        [Test]
        public void 灵敏度后缀非数字_报错()
        {
            var r = WakeKeywordSpec.Build(new[] { "x iǎo m ěi :abc @小美" }, Vocab);
            Assert.AreEqual(0, r.ValidCount);
            StringAssert.Contains(":abc", r.Errors[0]);
        }

        [Test]
        public void 列表为空引用_报错()
        {
            var r = WakeKeywordSpec.Build(null, Vocab);
            Assert.AreEqual(0, r.ValidCount);
            Assert.AreEqual(1, r.Errors.Count);
        }

        [Test]
        public void 词表解析_取每行第一列()
        {
            var v = WakeKeywordSpec.ParseVocabulary("<blk> 0\nx 7\r\niǎo 120\n\n");
            Assert.AreEqual(3, v.Count);
            Assert.IsTrue(v.Contains("<blk>"));
            Assert.IsTrue(v.Contains("x"));
            Assert.IsTrue(v.Contains("iǎo"));
        }
    }
}
