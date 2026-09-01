using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class IntentTextTests
    {
        // ---------- IsUsableArg ----------

        [Test]
        public void 正常歌名可用()
        {
            Assert.IsTrue(IntentText.IsUsableArg("赤伶"));
            Assert.IsTrue(IntentText.IsUsableArg(" 山外小楼夜听雨 "));
            Assert.IsTrue(IntentText.IsUsableArg("Always Online"));
            Assert.IsTrue(IntentText.IsUsableArg("你和我 (You And Me)"));
        }

        [Test]
        public void 空白不可用()
        {
            Assert.IsFalse(IntentText.IsUsableArg(null));
            Assert.IsFalse(IntentText.IsUsableArg(""));
            Assert.IsFalse(IntentText.IsUsableArg("   "));
        }

        [Test]
        public void 超长不可用()
        {
            // 刻意不用 new string('歌', 25)：那会同时撞上下面的「同字重复」规则，
            // 测出来的就不是长度边界了。这里用一个不重复的串。
            const string s22 = "山外小楼夜听雨春眠不觉晓处处闻啼鸟夜来风雨声";
            Assert.AreEqual(22, s22.Length, "基准串长度变了的话下面两条断言就失去意义");

            Assert.IsTrue(IntentText.IsUsableArg(s22 + "花落知"));    // 25：边界内
            Assert.IsFalse(IntentText.IsUsableArg(s22 + "花落知多"));  // 26：越界
        }

        [Test]
        public void 纯数字不可用()
        {
            Assert.IsFalse(IntentText.IsUsableArg("666"));
            Assert.IsFalse(IntentText.IsUsableArg("123456"));
        }

        [Test]
        public void 同一个字重复不可用()
        {
            Assert.IsFalse(IntentText.IsUsableArg("哈哈哈哈"));
            Assert.IsFalse(IntentText.IsUsableArg("？？？"));
            Assert.IsFalse(IntentText.IsUsableArg("。。。"));
            Assert.IsTrue(IntentText.IsUsableArg("哈"), "单字仍然可能是歌名，不拦");
        }

        [Test]
        public void 没有文字或数字的不可用()
        {
            Assert.IsFalse(IntentText.IsUsableArg("？！"));
            Assert.IsFalse(IntentText.IsUsableArg("😀😭"));
            Assert.IsFalse(IntentText.IsUsableArg("~!@#"));
        }

        // ---------- LooksLikeIntent ----------

        [Test]
        public void 预筛能认出点歌说法()
        {
            Assert.AreEqual(IntentKind.Song, IntentText.LooksLikeIntent("我想听点音乐"));
            Assert.AreEqual(IntentKind.Song, IntentText.LooksLikeIntent("来一首吧"));
        }

        [Test]
        public void 预筛能认出点舞说法()
        {
            Assert.AreEqual(IntentKind.Dance, IntentText.LooksLikeIntent("给我们跳舞看看"));
            Assert.AreEqual(IntentKind.Dance, IntentText.LooksLikeIntent("扭一个"));
        }

        [Test]
        public void 预筛能认出换角色说法()
        {
            Assert.AreEqual(IntentKind.Avatar, IntentText.LooksLikeIntent("变身给我们看看"));
            Assert.AreEqual(IntentKind.Avatar, IntentText.LooksLikeIntent("换成别的形象吧"));
        }

        [Test]
        public void 无关弹幕不触发预筛()
        {
            Assert.AreEqual(IntentKind.None, IntentText.LooksLikeIntent("今天天气不错"));
            Assert.AreEqual(IntentKind.None, IntentText.LooksLikeIntent("主播好可爱"));
            Assert.AreEqual(IntentKind.None, IntentText.LooksLikeIntent(""));
            Assert.AreEqual(IntentKind.None, IntentText.LooksLikeIntent(null));
        }

        [Test]
        public void 超过30字的长句不问LLM()
        {
            // 长句是聊天不是命令，问 LLM 只会白烧 token
            Assert.AreEqual(IntentKind.None, IntentText.LooksLikeIntent(new string('听', 31)));
        }

        [Test]
        public void 舞的判定优先于歌()
        {
            // 「跳舞」两个字里没有歌相关词，但词表顺序必须保证舞先判 ——
            // 返回值只用来决定「值不值得问 LLM」，具体类别由 LLM 定
            Assert.AreEqual(IntentKind.Dance, IntentText.LooksLikeIntent("跳舞"));
        }

        // ---------- TryParseIntentJson ----------

        [Test]
        public void 解析裸JSON()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson(
                "{\"intent\":\"song\",\"arg\":\"赤伶\"}", out var k, out var a));
            Assert.AreEqual(IntentKind.Song, k);
            Assert.AreEqual("赤伶", a);
        }

        [Test]
        public void 解析被markdown围栏包裹的JSON()
        {
            string raw = "```json\n{\"intent\": \"dance\", \"arg\": \"极乐净土\"}\n```";
            Assert.IsTrue(IntentText.TryParseIntentJson(raw, out var k, out var a));
            Assert.AreEqual(IntentKind.Dance, k);
            Assert.AreEqual("极乐净土", a);
        }

        [Test]
        public void 解析前后带废话的JSON()
        {
            string raw = "好的，我判断如下：{\"intent\":\"avatar\",\"arg\":\"小白\"} 希望有帮助！";
            Assert.IsTrue(IntentText.TryParseIntentJson(raw, out var k, out var a));
            Assert.AreEqual(IntentKind.Avatar, k);
            Assert.AreEqual("小白", a);
        }

        [Test]
        public void 解析单引号JSON()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson(
                "{'intent':'song','arg':'大鱼'}", out var k, out var a));
            Assert.AreEqual(IntentKind.Song, k);
            Assert.AreEqual("大鱼", a);
        }

        [Test]
        public void 缺arg字段时arg为空但解析成功()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"song\"}", out var k, out var a));
            Assert.AreEqual(IntentKind.Song, k);
            Assert.AreEqual("", a);
        }

        [Test]
        public void intent为none时解析成功且类别为None()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson(
                "{\"intent\":\"none\",\"arg\":\"\"}", out var k, out _));
            Assert.AreEqual(IntentKind.None, k);
        }

        [Test]
        public void 非JSON解析失败()
        {
            Assert.IsFalse(IntentText.TryParseIntentJson("我觉得他是想点歌", out _, out _));
            Assert.IsFalse(IntentText.TryParseIntentJson("", out _, out _));
            Assert.IsFalse(IntentText.TryParseIntentJson(null, out _, out _));
        }

        [Test]
        public void intent值非法时解析失败()
        {
            Assert.IsFalse(IntentText.TryParseIntentJson(
                "{\"intent\":\"唱歌\",\"arg\":\"赤伶\"}", out _, out _));
        }

        [Test]
        public void 解析带转义引号的arg()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson(
                "{\"intent\":\"song\",\"arg\":\"说\\\"再见\\\"\"}", out _, out var a));
            Assert.AreEqual("说\"再见\"", a);
        }
    }
}
