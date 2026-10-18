using NUnit.Framework;
using System.Collections.Generic;

namespace DouyinLive.Tests
{
    public class VoiceCommandParserTests
    {
        static readonly List<string> Wake = new List<string> { "小美小美", "小美" };

        // ---------- 关键词模式（requireWakeWord=false：识别器已确认唤醒） ----------

        [Test]
        public void 带标点剥出命令()
        {
            Assert.AreEqual("唱一首黄昏", VoiceCommandParser.ExtractCommand("小美小美，唱一首黄昏", Wake, false));
            Assert.AreEqual("唱一首黄昏", VoiceCommandParser.ExtractCommand("小美小美。唱一首黄昏", Wake, false));
        }

        [Test]
        public void 无标点剥出命令()
        {
            Assert.AreEqual("唱一首黄昏", VoiceCommandParser.ExtractCommand("小美小美唱一首黄昏", Wake, false));
        }

        [Test]
        public void 前缀废话一起剥掉()
        {
            // 唤醒词不在句首：最早出现处连着前面的「你好」一起剥
            Assert.AreEqual("来首歌", VoiceCommandParser.ExtractCommand("你好小美小美。来首歌", Wake, false));
        }

        [Test]
        public void ASR空格重复唤醒词()
        {
            // 「小美小美」被 ASR 写成两段，开头残留的唤醒词要继续剥
            Assert.AreEqual("唱歌", VoiceCommandParser.ExtractCommand("小美 小美 唱歌", Wake, false));
        }

        [Test]
        public void 只有唤醒词返回空串()
        {
            Assert.AreEqual("", VoiceCommandParser.ExtractCommand("小美小美", Wake, false));
            Assert.AreEqual("", VoiceCommandParser.ExtractCommand("小美。", Wake, false));
            Assert.AreEqual("", VoiceCommandParser.ExtractCommand("你好小美小美", Wake, false));
        }

        [Test]
        public void 同位置取最长唤醒词()
        {
            // 「小美」先命中会把第二个「小美」留下来；必须整词「小美小美」剥干净
            Assert.AreEqual("唱一首黄昏", VoiceCommandParser.ExtractCommand("小美小美，唱一首黄昏", Wake, false));
        }

        [Test]
        public void 英文唤醒词忽略大小写()
        {
            var w = new List<string> { "HeyMei" };
            Assert.AreEqual("sing a song", VoiceCommandParser.ExtractCommand("heymei sing a song", w, false));
        }

        [Test]
        public void 没有唤醒词整句放行()
        {
            Assert.AreEqual("今天天气不错", VoiceCommandParser.ExtractCommand("今天天气不错", Wake, false));
        }

        // ---------- VAD 降级模式（requireWakeWord=true：文本里必须有唤醒词） ----------

        [Test]
        public void VAD无唤醒词拒绝()
        {
            Assert.IsNull(VoiceCommandParser.ExtractCommand("今天天气不错", Wake, true));
        }

        [Test]
        public void VAD有唤醒词剥出命令()
        {
            Assert.AreEqual("唱一首黄昏", VoiceCommandParser.ExtractCommand("小美小美，唱一首黄昏", Wake, true));
            Assert.AreEqual("唱歌", VoiceCommandParser.ExtractCommand("小美唱歌", Wake, true));
        }

        [Test]
        public void VAD只有唤醒词返回空串()
        {
            Assert.AreEqual("", VoiceCommandParser.ExtractCommand("小美小美", Wake, true));
        }

        // ---------- 表情过滤（SenseVoice 会在句尾加情绪表情） ----------

        [Test]
        public void 句尾表情被剥掉()
        {
            Assert.AreEqual("等会儿有抽奖哦", VoiceCommandParser.ExtractCommand("小美小美，等会儿有抽奖哦。😊", Wake, false));
        }

        [Test]
        public void 句中表情和变体选择符被剥掉()
        {
            // ❤️ = U+2764 + U+FE0F，两个都得剥
            Assert.AreEqual("八点下播", VoiceCommandParser.ExtractCommand("小美八点❤️下播", Wake, false));
        }

        [Test]
        public void 只有表情返回null()
        {
            Assert.IsNull(VoiceCommandParser.ExtractCommand("😊", Wake, false));
        }

        [Test]
        public void 中文和全角标点不受表情过滤影响()
        {
            Assert.AreEqual("唱一首《黄昏》吧～", VoiceCommandParser.ExtractCommand("小美小美，唱一首《黄昏》吧～", Wake, false));
        }

        // ---------- 输入防御 ----------

        [Test]
        public void 空输入返回null()
        {
            Assert.IsNull(VoiceCommandParser.ExtractCommand(null, Wake, false));
            Assert.IsNull(VoiceCommandParser.ExtractCommand("", Wake, false));
            Assert.IsNull(VoiceCommandParser.ExtractCommand("   ", Wake, false));
        }

        [Test]
        public void 唤醒词表为空()
        {
            Assert.IsNull(VoiceCommandParser.ExtractCommand("唱歌", new List<string>(), true));
            Assert.AreEqual("唱歌", VoiceCommandParser.ExtractCommand("唱歌", new List<string>(), false));
        }

        [Test]
        public void 词表里的空项不参与()
        {
            var w = new List<string> { "", null, "小美" };
            Assert.AreEqual("唱歌", VoiceCommandParser.ExtractCommand("小美唱歌", w, true));
        }
    }
}
