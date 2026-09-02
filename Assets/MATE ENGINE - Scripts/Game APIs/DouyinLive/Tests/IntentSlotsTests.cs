using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class IntentSlotsTests
    {
        float clock;
        IntentSlots slots;

        [SetUp]
        public void SetUp()
        {
            clock = 500f;                        // 从非 0 起步，防止「未初始化 = 0」的假通过
            slots = new IntentSlots { Now = () => clock };
        }

        [Test]
        public void 开槽之后能看到()
        {
            slots.Open("u1", "小明", IntentKind.Song, "song");
            Assert.IsTrue(slots.TryPeek("u1", out var s));
            Assert.AreEqual(IntentKind.Song, s.Kind);
            Assert.AreEqual("song", s.RuleId);
            Assert.AreEqual("小明", s.Nickname);
        }

        [Test]
        public void Take之后槽位消失()
        {
            slots.Open("u1", "小明", IntentKind.Song, "song");
            slots.Take("u1");
            Assert.IsFalse(slots.TryPeek("u1", out _));
        }

        [Test]
        public void 只Peek不Take时槽位保留且开槽时间不刷新()
        {
            slots.Open("u1", "小明", IntentKind.Song, "song");
            clock += 10f;
            Assert.IsTrue(slots.TryPeek("u1", out _));
            clock += 10f;
            Assert.IsTrue(slots.TryPeek("u1", out _));
            clock += 11f;                        // 累计 31 秒 > Window 30
            Assert.IsFalse(slots.TryPeek("u1", out _),
                "反复 Peek 不能续期，否则观众连发几个「666」就能把窗口无限延长");
        }

        [Test]
        public void 超过窗口就取不到()
        {
            slots.Open("u1", "小明", IntentKind.Song, "song");
            clock += 31f;
            Assert.IsFalse(slots.TryPeek("u1", out _));
        }

        [Test]
        public void 多个用户的槽位互不干扰()
        {
            slots.Open("a", "A", IntentKind.Song, "song");
            slots.Open("b", "B", IntentKind.Dance, "reqdance");
            Assert.IsTrue(slots.TryPeek("a", out var sa));
            Assert.IsTrue(slots.TryPeek("b", out var sb));
            Assert.AreEqual(IntentKind.Song, sa.Kind);
            Assert.AreEqual(IntentKind.Dance, sb.Kind);
            slots.Take("a");
            Assert.IsTrue(slots.TryPeek("b", out _));
        }

        [Test]
        public void 同一人再次开槽覆盖旧的()
        {
            slots.Open("u1", "小明", IntentKind.Song, "song");
            clock += 5f;
            slots.Open("u1", "小明", IntentKind.Avatar, "swap");
            Assert.AreEqual(1, slots.Count);
            Assert.IsTrue(slots.TryPeek("u1", out var s));
            Assert.AreEqual(IntentKind.Avatar, s.Kind);
        }

        [Test]
        public void 容量满时挤掉最旧的一个()
        {
            slots.Capacity = 3;
            slots.Open("a", "A", IntentKind.Song, "song"); clock += 1f;
            slots.Open("b", "B", IntentKind.Song, "song"); clock += 1f;
            slots.Open("c", "C", IntentKind.Song, "song"); clock += 1f;
            slots.Open("d", "D", IntentKind.Song, "song");
            Assert.AreEqual(3, slots.Count);
            Assert.IsFalse(slots.TryPeek("a", out _), "最旧的 a 应该被挤掉");
            Assert.IsTrue(slots.TryPeek("d", out _));
        }

        [Test]
        public void 空UserId不开槽()
        {
            slots.Open("", "匿名", IntentKind.Song, "song");
            slots.Open(null, "匿名", IntentKind.Song, "song");
            Assert.AreEqual(0, slots.Count);
            Assert.IsFalse(slots.TryPeek("", out _));
            Assert.IsFalse(slots.TryPeek(null, out _));
        }

        [Test]
        public void Kind为None不开槽()
        {
            slots.Open("u1", "小明", IntentKind.None, "song");
            Assert.AreEqual(0, slots.Count);
        }

        [Test]
        public void 窗口设为0等于关闭追问功能()
        {
            slots.Window = 0f;
            slots.Open("u1", "小明", IntentKind.Song, "song");
            Assert.AreEqual(0, slots.Count);
            Assert.IsFalse(slots.TryPeek("u1", out _));
        }

        [Test]
        public void Prune清掉过期槽位()
        {
            slots.Open("a", "A", IntentKind.Song, "song");
            clock += 31f;
            slots.Open("b", "B", IntentKind.Song, "song");
            slots.Prune();
            Assert.AreEqual(1, slots.Count);
            Assert.IsTrue(slots.TryPeek("b", out _));
        }

        [Test]
        public void Reset清空全部()
        {
            slots.Open("a", "A", IntentKind.Song, "song");
            slots.Open("b", "B", IntentKind.Song, "song");
            slots.Reset();
            Assert.AreEqual(0, slots.Count);
        }
    }
}
