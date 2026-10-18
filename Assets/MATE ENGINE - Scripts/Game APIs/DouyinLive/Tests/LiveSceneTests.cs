using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class LiveSceneTests
    {
        static LiveSwitches AllOn(float idle = 90f) => new LiveSwitches
        {
            AiReply = true, IdleChatter = true, IdleThreshold = idle, AutoSong = true, AutoDance = true
        };

        // ---------- 场景行为表 ----------

        [Test]
        public void 独播_AI详细回复_暖场与自动唱跳开()
        {
            var b = LiveSceneResolver.Resolve(LiveScene.Solo, false, AllOn());
            Assert.AreEqual(DanmakuHandling.AiVerbose, b.Danmaku);
            Assert.IsTrue(b.IdleChatter);
            Assert.AreEqual(90f, b.IdleThreshold);
            Assert.IsTrue(b.AutoSong);
            Assert.IsTrue(b.AutoDance);
        }

        [Test]
        public void 陪播_念弹幕_不暖场不自动唱跳()
        {
            var b = LiveSceneResolver.Resolve(LiveScene.Game, false, AllOn());
            Assert.AreEqual(DanmakuHandling.Read, b.Danmaku);
            Assert.IsFalse(b.IdleChatter);
            Assert.IsFalse(b.AutoSong);
            Assert.IsFalse(b.AutoDance);
        }

        [Test]
        public void 搭档_AI简短回复_暖场间隔至少180_不自动唱跳()
        {
            var b = LiveSceneResolver.Resolve(LiveScene.Duo, false, AllOn(90f));
            Assert.AreEqual(DanmakuHandling.AiBrief, b.Danmaku);
            Assert.IsTrue(b.IdleChatter);
            Assert.AreEqual(180f, b.IdleThreshold);
            Assert.IsFalse(b.AutoSong);
            Assert.IsFalse(b.AutoDance);
        }

        [Test]
        public void 搭档_总间隔大于180时用总间隔()
        {
            Assert.AreEqual(300f, LiveSceneResolver.Resolve(LiveScene.Duo, false, AllOn(300f)).IdleThreshold);
        }

        [Test]
        public void 非安静_响应观众且不静音()
        {
            foreach (var s in new[] { LiveScene.Solo, LiveScene.Game, LiveScene.Duo })
            {
                var b = LiveSceneResolver.Resolve(s, false, AllOn());
                Assert.IsTrue(b.RespondToAudience, s.ToString());
                Assert.IsFalse(b.MuteLowPriority, s.ToString());
            }
        }

        // ---------- 总开关约束 ----------

        [Test]
        public void AI回复总开关关_独播与搭档不处理弹幕()
        {
            var s = AllOn(); s.AiReply = false;
            Assert.AreEqual(DanmakuHandling.Ignore, LiveSceneResolver.Resolve(LiveScene.Solo, false, s).Danmaku);
            Assert.AreEqual(DanmakuHandling.Ignore, LiveSceneResolver.Resolve(LiveScene.Duo, false, s).Danmaku);
        }

        [Test]
        public void AI回复总开关关_陪播照常念弹幕()
        {
            // 念弹幕不经过大模型，和 AI 回复总开关无关（与旧 douyinChatMode=1 行为一致）
            var s = AllOn(); s.AiReply = false;
            Assert.AreEqual(DanmakuHandling.Read, LiveSceneResolver.Resolve(LiveScene.Game, false, s).Danmaku);
        }

        [Test]
        public void 暖场总开关关_任何场景都不暖场()
        {
            var s = AllOn(); s.IdleChatter = false;
            foreach (var sc in new[] { LiveScene.Solo, LiveScene.Game, LiveScene.Duo })
                Assert.IsFalse(LiveSceneResolver.Resolve(sc, false, s).IdleChatter, sc.ToString());
        }

        [Test]
        public void 自动唱歌总开关关_独播也不自动唱()
        {
            var s = AllOn(); s.AutoSong = false;
            var b = LiveSceneResolver.Resolve(LiveScene.Solo, false, s);
            Assert.IsFalse(b.AutoSong);
            Assert.IsTrue(b.AutoDance);
        }

        // ---------- 安静 ----------

        [Test]
        public void 安静_只剩礼物答谢与主播响应()
        {
            foreach (var sc in new[] { LiveScene.Solo, LiveScene.Game, LiveScene.Duo })
            {
                var b = LiveSceneResolver.Resolve(sc, true, AllOn());
                Assert.AreEqual(DanmakuHandling.Ignore, b.Danmaku, sc.ToString());
                Assert.IsFalse(b.IdleChatter, sc.ToString());
                Assert.IsFalse(b.AutoSong, sc.ToString());
                Assert.IsFalse(b.AutoDance, sc.ToString());
                Assert.IsFalse(b.RespondToAudience, sc.ToString());
                Assert.IsTrue(b.MuteLowPriority, sc.ToString());
            }
        }

        // ---------- 迁移 ----------

        [Test]
        public void 迁移_已有场景值原样()
        {
            Assert.AreEqual(LiveScene.Duo, LiveSceneMigration.Resolve(2, 0, out bool oor));
            Assert.IsFalse(oor);
        }

        [Test]
        public void 迁移_缺失时旧模式1为陪播()
        {
            Assert.AreEqual(LiveScene.Game, LiveSceneMigration.Resolve(-1, 1, out _));
        }

        [Test]
        public void 迁移_缺失时旧模式0为独播()
        {
            Assert.AreEqual(LiveScene.Solo, LiveSceneMigration.Resolve(-1, 0, out _));
        }

        [Test]
        public void 迁移_缺失时旧模式异常值为独播()
        {
            Assert.AreEqual(LiveScene.Solo, LiveSceneMigration.Resolve(-1, 7, out _));
        }

        [Test]
        public void 迁移_越界回退独播并报告()
        {
            Assert.AreEqual(LiveScene.Solo, LiveSceneMigration.Resolve(5, 1, out bool oor));
            Assert.IsTrue(oor);
        }

        // ---------- 回复主播的详略 ----------

        [Test]
        public void 安静_独播回复主播仍然详细()
        {
            // 安静只针对观众：主播和她聊天的回复长度不该因为安静变短
            Assert.IsTrue(LiveSceneResolver.Resolve(LiveScene.Solo, true, AllOn()).VerboseChat);
            Assert.IsTrue(LiveSceneResolver.Resolve(LiveScene.Solo, false, AllOn()).VerboseChat);
        }

        [Test]
        public void 搭档与陪播_回复简短()
        {
            Assert.IsFalse(LiveSceneResolver.Resolve(LiveScene.Duo, false, AllOn()).VerboseChat);
            Assert.IsFalse(LiveSceneResolver.Resolve(LiveScene.Game, false, AllOn()).VerboseChat);
            Assert.IsFalse(LiveSceneResolver.Resolve(LiveScene.Duo, true, AllOn()).VerboseChat);
        }
    }
}
