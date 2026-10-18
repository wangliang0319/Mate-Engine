using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class VoiceCaptureBufferTests
    {
        // 方波 ±rms·√2 → RMS 恰为 rms（正弦要积分，方波直接算）
        static float[] Tone(float rms, int n)
        {
            var a = new float[n];
            float amp = rms * 1.4142f;
            for (int i = 0; i < n; i++) a[i] = i % 2 == 0 ? amp : -amp;
            return a;
        }

        [Test]
        public void 未唤醒取不出()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 0.5f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 500));
            float[] s;
            Assert.IsFalse(b.TryTakeUtterance(out s));
            Assert.IsFalse(b.UtteranceReady);
            Assert.IsNull(s);
        }

        [Test]
        public void 取出含唤醒前片段和唤醒后片段()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 500));   // 唤醒前 0.5 秒说话，只保留 PreWake 0.1s=100
            b.NotifyWake();
            b.Push(Tone(0.2f, 300));   // 唤醒后命令 0.3s
            b.Push(new float[250]);    // 静音 0.25s ≥ 0.2 → 说完了
            float[] s;
            Assert.IsTrue(b.TryTakeUtterance(out s));
            Assert.AreEqual(650, s.Length);   // 100 pre + 300 命令 + 250 静音尾
        }

        [Test]
        public void 唤醒后静音不足不就绪()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.NotifyWake();
            b.Push(Tone(0.2f, 300));
            b.Push(new float[150]);            // 0.15 < 0.2
            Assert.IsFalse(b.UtteranceReady);
            b.Push(new float[60]);             // 累计 0.21
            Assert.IsTrue(b.UtteranceReady);
        }

        [Test]
        public void 说话打断静音累计()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.NotifyWake();
            b.Push(new float[150]);
            b.Push(Tone(0.2f, 100));           // 出声 → 静音清零
            b.Push(new float[150]);
            Assert.IsFalse(b.UtteranceReady);
        }

        [Test]
        public void 超长强制就绪()
        {
            // 命令说个没完：宁可截断也不能卡死等静音
            var b = new VoiceCaptureBuffer(1000, 0.1f, 0.5f, 0.2f, 0.05f);
            b.NotifyWake();
            b.Push(Tone(0.2f, 600));           // 0.6s > 0.5 且一直出声
            Assert.IsTrue(b.UtteranceReady);
        }

        [Test]
        public void 取完复位可再次唤醒()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.NotifyWake();
            b.Push(Tone(0.2f, 300));
            b.Push(new float[250]);
            float[] s;
            Assert.IsTrue(b.TryTakeUtterance(out s));
            Assert.IsFalse(b.UtteranceReady);          // 复位
            Assert.IsFalse(b.TryTakeUtterance(out s));
            b.NotifyWake();                             // 第二次唤醒
            b.Push(Tone(0.2f, 100));
            b.Push(new float[250]);
            Assert.IsTrue(b.TryTakeUtterance(out s));
            Assert.AreEqual(450, s.Length);             // PreWake 100（上一段残留）+ 350
        }

        [Test]
        public void 环形滚掉旧数据后仍能取出()
        {
            // 容量 = (0.1+0.5+1)×1000 = 1600，先灌 2000 滚掉最旧的
            var b = new VoiceCaptureBuffer(1000, 0.1f, 0.5f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 2000));
            b.NotifyWake();
            b.Push(new float[250]);
            float[] s;
            Assert.IsTrue(b.TryTakeUtterance(out s));
            Assert.AreEqual(350, s.Length);   // PreWake 100 + 250，起点被环形容量钳住
        }

        [Test]
        public void 唤醒很早也能取到完整前段()
        {
            // 唤醒时累计样本还不足 PreWake：起点钳到 0，不许负索引
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 50));
            b.NotifyWake();
            b.Push(new float[250]);
            float[] s;
            Assert.IsTrue(b.TryTakeUtterance(out s));
            Assert.AreEqual(300, s.Length);
        }

        [Test]
        public void 带计数的Push只取前count个()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            var big = new float[500];
            b.Push(big, 200);          // 静音（全 0）
            b.NotifyWake();
            b.Push(big, 100);          // 只取 100 个
            b.Push(new float[250]);
            float[] s;
            Assert.IsTrue(b.TryTakeUtterance(out s));
            Assert.AreEqual(450, s.Length);   // PreWake 100 + 100 + 250
        }

        [Test]
        public void Clear清空()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.NotifyWake();
            b.Push(Tone(0.2f, 300));
            b.Push(new float[250]);
            b.Clear();
            float[] s;
            Assert.IsFalse(b.TryTakeUtterance(out s));
            Assert.IsFalse(b.UtteranceReady);
        }

        // ---------- v2：按需回看 + 无条件取出 ----------

        [Test]
        public void 指定回看秒数_只回看指定长度()
        {
            var b = new VoiceCaptureBuffer(1000, 3f, 1f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 2000));
            b.NotifyWake(0.5f);
            b.Push(Tone(0.2f, 300));
            Assert.IsTrue(b.TakeArmed(out var s));
            Assert.AreEqual(800, s.Length);   // 500 回看 + 300 唤醒后
        }

        [Test]
        public void 零回看_只含唤醒后()
        {
            var b = new VoiceCaptureBuffer(1000, 3f, 1f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 2000));
            b.NotifyWake(0f);
            b.Push(Tone(0.2f, 300));
            Assert.IsTrue(b.TakeArmed(out var s));
            Assert.AreEqual(300, s.Length);
        }

        [Test]
        public void TakeArmed不等静音判定()
        {
            // v2 的「说完没有」由状态机决定，缓冲区只负责交出音频
            var b = new VoiceCaptureBuffer(1000, 3f, 1f, 0.2f, 0.05f);
            b.NotifyWake(0f);
            b.Push(Tone(0.2f, 100));
            Assert.IsFalse(b.UtteranceReady);
            Assert.IsTrue(b.TakeArmed(out var s));
            Assert.AreEqual(100, s.Length);
        }

        [Test]
        public void 未唤醒TakeArmed失败()
        {
            var b = new VoiceCaptureBuffer(1000, 3f, 1f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 100));
            Assert.IsFalse(b.TakeArmed(out var s));
            Assert.IsNull(s);
        }

        [Test]
        public void TakeArmed后复位_旧接口回看不受影响()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.NotifyWake(0f);
            b.Push(Tone(0.2f, 100));
            b.TakeArmed(out _);
            Assert.IsFalse(b.Armed);
            b.Push(Tone(0.2f, 500));
            b.NotifyWake();                    // 旧接口：回看 PreWakeSeconds=0.1s
            b.Push(Tone(0.2f, 300));
            b.Push(new float[250]);
            Assert.IsTrue(b.TryTakeUtterance(out var s));
            Assert.AreEqual(650, s.Length);    // 100 pre + 300 + 250
        }
    }
}
