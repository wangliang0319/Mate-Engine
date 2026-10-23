using NUnit.Framework;
using System.Collections.Generic;

namespace DouyinLive.Tests
{
    // 主播喊「小美小美 唱一首歌」不带歌名时的裁决：以前一律反问「想听什么歌呀」，
    // 主播说完就停在那里等，听起来像没听懂。现在主播语音直接从配置歌单抽一首唱。
    public class SongRequestFallbackTests
    {
        static List<string> L(params string[] items) => new List<string>(items);

        [Test]
        public void 主播没带歌名_从歌单抽一首()
        {
            var songs = L("赤伶", "探窗", "芒种");
            Assert.AreEqual("赤伶", SongRequestFallback.PickForStreamer(true, "", songs, 0));
            Assert.AreEqual("探窗", SongRequestFallback.PickForStreamer(true, "", songs, 1));
            Assert.AreEqual("芒种", SongRequestFallback.PickForStreamer(true, "", songs, 2));
        }

        [Test]
        public void 空白歌名按没带处理()
        {
            Assert.AreEqual("赤伶", SongRequestFallback.PickForStreamer(true, "   ", L("赤伶"), 0));
        }

        [Test]
        public void 主播带了歌名_不覆盖()
        {
            // 说了具体歌名就唱那首，歌单不能把它改掉
            Assert.IsNull(SongRequestFallback.PickForStreamer(true, "晴天", L("赤伶"), 0));
        }

        [Test]
        public void 观众没带歌名_返回null走追问()
        {
            // 观众点歌没带歌名时保留「想听什么歌呀」的追问和 30 秒槽位
            Assert.IsNull(SongRequestFallback.PickForStreamer(false, "", L("赤伶"), 0));
            Assert.IsNull(SongRequestFallback.PickForStreamer(false, "   ", L("赤伶"), 0));
        }

        [Test]
        public void 歌单为空_返回null()
        {
            Assert.IsNull(SongRequestFallback.PickForStreamer(true, "", null, 0));
            Assert.IsNull(SongRequestFallback.PickForStreamer(true, "", L(), 0));
        }

        [Test]
        public void 歌单全是空白项_返回null()
        {
            // 配置里可能留着空行，当成「没有歌单」而不是抽出一个空歌名
            Assert.IsNull(SongRequestFallback.PickForStreamer(true, "", L("", "   ", null), 5));
        }

        [Test]
        public void 抽中的歌名去掉首尾空白_并跳过空行()
        {
            Assert.AreEqual("赤伶", SongRequestFallback.PickForStreamer(true, "", L("", " 赤伶 ", ""), 0));
            Assert.AreEqual("探窗", SongRequestFallback.PickForStreamer(true, "", L("", " 赤伶 ", "探窗"), 1));
        }

        [Test]
        public void 索引用取模_负数和越界都能落到歌单里()
        {
            var songs = L("赤伶", "探窗", "芒种");
            Assert.AreEqual("赤伶", SongRequestFallback.PickForStreamer(true, "", songs, 3));
            Assert.AreEqual("芒种", SongRequestFallback.PickForStreamer(true, "", songs, -1));
            Assert.AreEqual("探窗", SongRequestFallback.PickForStreamer(true, "", songs, int.MaxValue));
        }
    }
}
