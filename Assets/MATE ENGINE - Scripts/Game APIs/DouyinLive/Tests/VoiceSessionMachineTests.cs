using NUnit.Framework;
using System.Collections.Generic;

namespace DouyinLive.Tests
{
    // 时间全部由测试驱动：Run 以 20ms 一帧推进，模拟 Unity 每帧调用 Update。
    public class VoiceSessionMachineTests
    {
        static VoiceSessionMachine M(float followUp = 6f)
        {
            var t = VoiceSessionTiming.Default;
            t.FollowUpWindow = followUp;
            return new VoiceSessionMachine(t);
        }

        static List<VoiceAction> L(IReadOnlyList<VoiceAction> a) => new List<VoiceAction>(a);

        static List<VoiceAction> Run(VoiceSessionMachine m, float from, float to, bool voiced,
                                     bool talking = false, bool performing = false)
        {
            var all = new List<VoiceAction>();
            int n = (int)System.Math.Round((to - from) / 0.02f);
            for (int i = 0; i <= n; i++) all.AddRange(m.Update(from + i * 0.02f, voiced, talking, performing));
            return all;
        }

        static int Count(List<VoiceAction> a, VoiceAction x)
        {
            int c = 0;
            foreach (var v in a) if (v == x) c++;
            return c;
        }

        // 唤醒(t=0) → 停顿 → 应答(t≈0.4) → 应答播完(t=1) → 聆听
        static VoiceSessionMachine ListeningAfterAck(bool performing = false)
        {
            var m = M();
            m.OnWake(0f, false, performing);
            Run(m, 0.02f, 0.5f, false, performing: performing);
            m.OnAckFinished(1f);
            return m;
        }

        // 聆听 → 说 1 秒 → 静音 1.2 秒 → 提交（t≈3.2）→ 处理中
        static VoiceSessionMachine Processing()
        {
            var m = ListeningAfterAck();
            Run(m, 1.02f, 2f, true);
            Run(m, 2.02f, 3.3f, false);
            return m;
        }

        // 转写成功(t=5) → 等回应 → 她开口(5.1) → 说完(7) → 追问
        static VoiceSessionMachine FollowUp()
        {
            var m = Processing();
            m.OnTranscript(5f, "唱一首黄昏");
            m.Update(5.1f, false, true, false);
            m.Update(7f, false, false, false);
            return m;
        }

        // 追问(7) → 连续开口进聆听(≈7.3) → 说到 7.5 → 静音 → 提交(≈8.7) → 处理中
        static VoiceSessionMachine ProcessingFromFollowUp()
        {
            var m = FollowUp();
            Run(m, 7.02f, 7.5f, true);
            Run(m, 7.52f, 8.8f, false);
            return m;
        }

        // ---------- 唤醒 ----------

        [Test]
        public void 空闲唤醒_暂停播报并开始录音()
        {
            var m = M();
            var a = L(m.OnWake(0f, false, false));
            CollectionAssert.AreEqual(new[] { VoiceAction.HoldSpeech, VoiceAction.StartCapture }, a);
            Assert.AreEqual(VoiceSessionState.WakePending, m.State);
        }

        [Test]
        public void 说话时唤醒_先打断她()
        {
            var a = L(M().OnWake(0f, true, false));
            Assert.AreEqual(VoiceAction.InterruptSpeech, a[0]);
        }

        [Test]
        public void 表演时唤醒_压低音乐()
        {
            Assert.Contains(VoiceAction.DuckMusic, L(M().OnWake(0f, false, true)));
        }

        // ---------- 判停顿 ----------

        [Test]
        public void 一口气说完_跳过应答直接聆听()
        {
            var m = M();
            m.OnWake(0f, false, false);
            var a = Run(m, 0.02f, 0.4f, true);
            Assert.AreEqual(VoiceSessionState.Listening, m.State);
            Assert.IsFalse(a.Contains(VoiceAction.PlayAck));
            Assert.IsFalse(a.Contains(VoiceAction.StopCapture));   // 录音不能中断，否则丢字
        }

        [Test]
        public void 只有唤醒词尾音_仍然应答()
        {
            // sherpa 在唤醒词快说完时才检出，尾音让窗口开头短暂有声，不能当成一口气说完
            var m = M();
            m.OnWake(0f, false, false);
            Run(m, 0.02f, 0.1f, true);            // 尾音 0.1 秒 < 0.15
            var a = Run(m, 0.12f, 0.5f, false);
            Assert.Contains(VoiceAction.PlayAck, a);
            Assert.AreEqual(VoiceSessionState.Ack, m.State);
        }

        [Test]
        public void 停顿_播应答且应答期间不录音()
        {
            var m = M();
            m.OnWake(0f, false, false);
            var a = Run(m, 0.02f, 0.5f, false);
            int stop = a.IndexOf(VoiceAction.StopCapture);
            int ack = a.IndexOf(VoiceAction.PlayAck);
            Assert.GreaterOrEqual(stop, 0);
            Assert.Greater(ack, stop);
        }

        // ---------- 应答 ----------

        [Test]
        public void 应答播完_开始无回看录音()
        {
            var m = M();
            m.OnWake(0f, false, false);
            Run(m, 0.02f, 0.5f, false);
            var a = L(m.OnAckFinished(1f));
            CollectionAssert.AreEqual(new[] { VoiceAction.StartCaptureAfterAck }, a);
            Assert.AreEqual(VoiceSessionState.Listening, m.State);
        }

        [Test]
        public void 应答回调丢失_安全超时后照常聆听()
        {
            var m = M();
            m.OnWake(0f, false, false);
            Run(m, 0.02f, 0.5f, false);
            var a = Run(m, 0.52f, 5.5f, false);
            Assert.Contains(VoiceAction.StartCaptureAfterAck, a);
            Assert.AreEqual(VoiceSessionState.Listening, m.State);
        }

        // ---------- 聆听 ----------

        [Test]
        public void 应答后不开口_安静回待机()
        {
            var m = ListeningAfterAck();
            var a = Run(m, 1.02f, 6.1f, false);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
            Assert.Contains(VoiceAction.StopCapture, a);
            Assert.Contains(VoiceAction.ReleaseSpeech, a);
            Assert.IsFalse(a.Contains(VoiceAction.SayNotHeard));
        }

        [Test]
        public void 说完静音够久_提交转写()
        {
            var m = ListeningAfterAck();
            Run(m, 1.02f, 2f, true);
            var before = Run(m, 2.02f, 3.1f, false);          // 静音 1.1 秒还不够
            Assert.IsFalse(before.Contains(VoiceAction.SubmitCapture));
            var a = Run(m, 3.12f, 3.3f, false);
            Assert.Contains(VoiceAction.SubmitCapture, a);
            Assert.AreEqual(VoiceSessionState.Processing, m.State);
        }

        [Test]
        public void 一直说话_录满上限提交()
        {
            var m = ListeningAfterAck();
            var a = Run(m, 1.02f, 9.1f, true);
            Assert.Contains(VoiceAction.SubmitCapture, a);
        }

        // ---------- 处理 ----------

        [Test]
        public void 转写成功_注入并只放开新播报()
        {
            // 只放行唤醒之后入队的（她的回答）；会话前积压的弹幕留到回待机再播，
            // 否则积压弹幕会被当成「她在回答」提前开追问，真正的回答反被压住
            var m = Processing();
            var a = L(m.OnTranscript(5f, " 唱一首黄昏 "));
            CollectionAssert.AreEqual(new[] { VoiceAction.Inject, VoiceAction.ReleaseNewSpeech }, a);
            Assert.AreEqual("唱一首黄昏", m.PendingCommand);
            Assert.AreEqual(VoiceSessionState.AwaitReply, m.State);
        }

        [Test]
        public void 转写失败_说没听清并回待机()
        {
            var m = Processing();
            var a = L(m.OnTranscript(5f, null));
            Assert.Contains(VoiceAction.SayNotHeard, a);
            Assert.Contains(VoiceAction.ReleaseSpeech, a);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        [Test]
        public void 唤醒后转写为空_说没听清()
        {
            var m = Processing();
            Assert.Contains(VoiceAction.SayNotHeard, L(m.OnTranscript(5f, "")));
        }

        // ---------- 等回应 ----------

        [Test]
        public void 她说完且没表演_进追问并暂停播报()
        {
            var m = Processing();
            m.OnTranscript(5f, "今天唱什么好");
            m.Update(5.1f, false, true, false);
            var a = L(m.Update(7f, false, false, false));
            CollectionAssert.AreEqual(new[] { VoiceAction.HoldSpeech }, a);
            Assert.AreEqual(VoiceSessionState.FollowUp, m.State);
        }

        [Test]
        public void 她开始表演_不开追问()
        {
            var m = Processing();
            m.OnTranscript(5f, "唱一首黄昏");
            m.Update(5.1f, false, true, false);
            m.Update(6f, false, false, true);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        [Test]
        public void 等回应超时_回待机()
        {
            var m = Processing();
            m.OnTranscript(5f, "你好");
            Run(m, 5.02f, 20.1f, false);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        [Test]
        public void 追问关闭_说完直接回待机()
        {
            var m = M(followUp: 0f);
            m.OnWake(0f, false, false);
            Run(m, 0.02f, 0.5f, false);
            m.OnAckFinished(1f);
            Run(m, 1.02f, 2f, true);
            Run(m, 2.02f, 3.3f, false);
            m.OnTranscript(5f, "你好");
            m.Update(5.1f, false, true, false);
            m.Update(7f, false, false, false);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        // ---------- 追问 ----------

        [Test]
        public void 追问内连续开口_进聆听()
        {
            var m = FollowUp();
            var a = Run(m, 7.02f, 7.4f, true);
            Assert.Contains(VoiceAction.StartCapture, a);
            Assert.AreEqual(VoiceSessionState.Listening, m.State);
        }

        [Test]
        public void 追问内短促声响_不进聆听()
        {
            var m = FollowUp();
            Run(m, 7.02f, 7.2f, true);             // 0.2 秒 < 0.3（咳嗽、键盘声）
            var a = Run(m, 7.22f, 7.6f, false);
            Assert.IsFalse(a.Contains(VoiceAction.StartCapture));
            Assert.AreEqual(VoiceSessionState.FollowUp, m.State);
        }

        [Test]
        public void 追问超时_回待机并放开播报()
        {
            var m = FollowUp();
            var a = Run(m, 7.02f, 13.1f, false);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
            Assert.Contains(VoiceAction.ReleaseSpeech, a);
        }

        [Test]
        public void 追问转写太短_静默丢弃()
        {
            var m = ProcessingFromFollowUp();
            Assert.AreEqual(VoiceSessionState.Processing, m.State);
            var a = L(m.OnTranscript(9f, "嗯"));
            Assert.IsFalse(a.Contains(VoiceAction.SayNotHeard));
            Assert.IsFalse(a.Contains(VoiceAction.Inject));
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        [Test]
        public void 追问转写失败_静默丢弃()
        {
            var m = ProcessingFromFollowUp();
            Assert.IsFalse(L(m.OnTranscript(9f, null)).Contains(VoiceAction.SayNotHeard));
        }

        [Test]
        public void 追问转写正常_照常注入()
        {
            var m = ProcessingFromFollowUp();
            Assert.Contains(VoiceAction.Inject, L(m.OnTranscript(9f, "那唱晴天吧")));
        }

        // ---------- 重复唤醒 / 中止 ----------

        [Test]
        public void 录音阶段再次唤醒_忽略()
        {
            var m = ListeningAfterAck();
            Assert.AreEqual(0, m.OnWake(1.5f, false, false).Count);
            Assert.AreEqual(VoiceSessionState.Listening, m.State);
        }

        [Test]
        public void 追问阶段唤醒_开新会话()
        {
            var m = FollowUp();
            var a = L(m.OnWake(8f, false, false));
            Assert.Contains(VoiceAction.StartCapture, a);
            Assert.IsFalse(a.Contains(VoiceAction.HoldSpeech));   // 追问时已暂停，不重复发
            Assert.AreEqual(VoiceSessionState.WakePending, m.State);
        }

        [Test]
        public void 音乐恢复恰好一次()
        {
            var m = ListeningAfterAck(performing: true);
            var a = Run(m, 1.02f, 6.1f, false, performing: true);
            a.AddRange(Run(m, 6.12f, 7f, false, performing: true));
            Assert.AreEqual(1, Count(a, VoiceAction.UnduckMusic));
        }

        [Test]
        public void 中止_恢复音乐与播报()
        {
            var m = M();
            m.OnWake(0f, false, true);
            var a = L(m.Abort());
            Assert.Contains(VoiceAction.StopCapture, a);
            Assert.Contains(VoiceAction.UnduckMusic, a);
            Assert.Contains(VoiceAction.ReleaseSpeech, a);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        [Test]
        public void 过期转写_忽略()
        {
            var m = M();
            Assert.AreEqual(0, m.OnTranscript(1f, "你好").Count);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        [Test]
        public void 待机中止_无动作()
        {
            Assert.AreEqual(0, M().Abort().Count);
        }

        // ---------- 审校修复：空帧、积压播报、转换原因 ----------

        // 麦克风有些帧没有新数据（null）：沿用上一帧的有声/无声，不能把起音计时清零
        static List<VoiceAction> RunGappy(VoiceSessionMachine m, float from, float to)
        {
            var all = new List<VoiceAction>();
            int n = (int)System.Math.Round((to - from) / 0.01f);
            for (int i = 0; i <= n; i++)
                all.AddRange(m.Update(from + i * 0.01f, i % 2 == 0 ? (bool?)true : null, false, false));
            return all;
        }

        [Test]
        public void 追问起音_空帧不清零()
        {
            var m = FollowUp();
            var a = RunGappy(m, 7.01f, 7.5f);
            Assert.Contains(VoiceAction.StartCapture, a);
            Assert.AreEqual(VoiceSessionState.Listening, m.State);
        }

        [Test]
        public void 追问_无声后的空帧不算开口()
        {
            var m = FollowUp();
            m.Update(7.01f, false, false, false);
            var a = new List<VoiceAction>();
            for (int i = 1; i <= 50; i++) a.AddRange(m.Update(7.01f + i * 0.01f, null, false, false));
            Assert.IsFalse(a.Contains(VoiceAction.StartCapture));
        }

        [Test]
        public void 等回应结束回待机_完全放开积压()
        {
            var m = Processing();
            m.OnTranscript(5f, "你好");
            var a = Run(m, 5.02f, 20.1f, false);
            Assert.Contains(VoiceAction.ReleaseSpeech, a);
        }

        [Test]
        public void 等回应阶段唤醒_重新完全暂停()
        {
            var m = Processing();
            m.OnTranscript(5f, "你好");
            Assert.Contains(VoiceAction.HoldSpeech, L(m.OnWake(6f, true, false)));
        }

        [Test]
        public void 转换原因_录满上限()
        {
            var m = ListeningAfterAck();
            Run(m, 1.02f, 9.1f, true);
            StringAssert.Contains("录满", m.LastTransition);
        }

        [Test]
        public void 转换原因_应答后没开口()
        {
            var m = ListeningAfterAck();
            Run(m, 1.02f, 6.1f, false);
            StringAssert.Contains("没开口", m.LastTransition);
        }

        [Test]
        public void 转换原因_追问转写太短()
        {
            var m = ProcessingFromFollowUp();
            m.OnTranscript(9f, "嗯");
            StringAssert.Contains("不足 2 字", m.LastTransition);
        }
    }
}
