using System.Collections.Generic;
using NUnit.Framework;

namespace DouyinLive.Tests
{
    // 观众点舞时的名字匹配。旧实现是「双向子串 + 按文件名顺序取第一个」，实测有四个坑：
    // 带书名号匹配不到、永远命中左版本、分段/同名包被当成不同的舞、英文短标题会反向误命中。
    public class DanceTitleMatchTests
    {
        [Test]
        public void 带书名号和下划线也能匹配到()
        {
            var titles = new List<string> { "sparkle_左_", "sparkle_右_", "Nee-1" };
            int i = DanceTitleMatch.FindBest(titles, "《sparkle》", 0);
            Assert.GreaterOrEqual(i, 0);
            StringAssert.StartsWith("sparkle", titles[i]);
        }

        [Test]
        public void 左右版本算同一支_随机挑一个()
        {
            var titles = new List<string> { "DRMIX_(左)", "DRMIX_(右)" };
            var seen = new HashSet<int>();
            for (int roll = 0; roll < 2; roll++)
                seen.Add(DanceTitleMatch.FindBest(titles, "DRMIX", roll));
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, seen);
        }

        [Test]
        public void 精确匹配优先于变体匹配()
        {
            var titles = new List<string> { "MMD-ECHO", "ECHO", "ECHO-2" };
            Assert.AreEqual(1, DanceTitleMatch.FindBest(titles, "ECHO", 0));
        }

        [Test]
        public void 分段编号视为同一支()
        {
            Assert.AreEqual(
                DanceTitleMatch.VariantKey("MMD-CarryMeOff-1"),
                DanceTitleMatch.VariantKey("MMD-CarryMeOff-2"));
            Assert.AreEqual(
                DanceTitleMatch.VariantKey("sparkle_左_"),
                DanceTitleMatch.VariantKey("sparkle_右_"));
        }

        [Test]
        public void 纯数字标题不会被当成编号吃掉()
        {
            Assert.AreEqual("39", DanceTitleMatch.VariantKey("39"));
        }

        [Test]
        public void 查不到时返回负一_不会乱点一支()
        {
            var titles = new List<string> { "lap tap love", "Nee-1" };
            Assert.AreEqual(-1, DanceTitleMatch.FindBest(titles, "Love Story", 0));
        }

        [Test]
        public void 只打英文部分也能命中日文标题()
        {
            var titles = new List<string> { "ラストダンス (Last Dance)（右）" };
            Assert.AreEqual(0, DanceTitleMatch.FindBest(titles, "last dance", 0));
        }

        [Test]
        public void 平手时每个候选都能被抽到()
        {
            var titles = new List<string> { "Hot Milk", "Hot Milk" };
            var seen = new HashSet<int>();
            for (int roll = 0; roll < 2; roll++)
                seen.Add(DanceTitleMatch.FindBest(titles, "hot milk", roll));
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, seen);
        }

        [Test]
        public void 随机值为负也不越界()
        {
            var titles = new List<string> { "A", "B" };
            int i = DanceTitleMatch.FindBest(titles, "a", -7);
            Assert.GreaterOrEqual(i, 0);
            Assert.Less(i, titles.Count);
        }
    }
}
