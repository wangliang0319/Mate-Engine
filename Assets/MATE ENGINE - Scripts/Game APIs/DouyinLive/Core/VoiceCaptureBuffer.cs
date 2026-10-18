using System;

namespace DouyinLive
{
    // 麦克风音频的环形缓冲 + 「说完没有」判定。主循环每帧把增量样本 Push 进来；
    // 唤醒（关键词命中或 VAD 起音）后开始观察静音，静音够久或录够长就 Ready，
    // Take 出 [唤醒前 PreWake 秒 + 唤醒后全部] 交给 ASR。纯数据结构，Core 可测。
    //
    // PreWake 存在的理由：唤醒词和命令常常一口气说完（「小美小美唱一首黄昏」），
    // 关键词识别器回调到我们开始关注音频时命令已经说了一半，没有这段回看就丢字。
    public class VoiceCaptureBuffer
    {
        public int SampleRate { get; }
        public float PreWakeSeconds { get; }
        public float MaxUtteranceSeconds { get; }
        public float SilenceSeconds { get; }
        public float RmsThreshold { get; }

        readonly float[] ring;
        int writePos;          // 下一个写位置（样本）
        long totalWritten;     // 累计写入样本数（含被环形滚掉的）
        bool armed;            // 唤醒之后 true
        long wakeSample;       // 唤醒时刻的累计样本序号
        long silenceSamples;   // 唤醒后连续静音样本数
        float armedPreRoll;    // 本次唤醒的回看秒数（v2 按场合给：唤醒 0.6s、应答后 0s）

        public bool Armed { get { return armed; } }

        public VoiceCaptureBuffer(int sampleRate, float preWakeSeconds, float maxUtteranceSeconds,
                                  float silenceSeconds, float rmsThreshold)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException("sampleRate");
            SampleRate = sampleRate;
            PreWakeSeconds = preWakeSeconds;
            MaxUtteranceSeconds = maxUtteranceSeconds;
            SilenceSeconds = silenceSeconds;
            RmsThreshold = rmsThreshold;
            // 余量 1 秒：PreWake 切片起点要落在环形里
            int capacity = (int)((preWakeSeconds + maxUtteranceSeconds + 1f) * sampleRate);
            ring = new float[Math.Max(capacity, sampleRate)];
        }

        // 唤醒后已录秒数（诊断用）
        public float ArmedSeconds
        {
            get { return armed ? (totalWritten - wakeSample) / (float)SampleRate : 0f; }
        }

        public void Push(float[] samples)
        {
            if (samples != null) Push(samples, samples.Length);
        }

        public void Push(float[] samples, int count)
        {
            if (samples == null || count <= 0) return;
            if (count > samples.Length) count = samples.Length;

            // 单次灌入超过环形容量的极端情况：只写最后一段，前面的反正要滚掉
            int from = 0;
            if (count > ring.Length) { from = count - ring.Length; count = ring.Length; }
            for (int i = from; i < from + count; i++)
            {
                ring[writePos] = samples[i];
                writePos++;
                if (writePos == ring.Length) writePos = 0;
            }
            long prevTotal = totalWritten;
            totalWritten += count;

            if (!armed) return;
            double sum = 0;
            for (int i = 0; i < count; i++) sum += (double)samples[i] * samples[i];
            float rms = (float)Math.Sqrt(sum / count);
            if (rms < RmsThreshold) silenceSamples += totalWritten - prevTotal;   // 整段按静音计
            else silenceSamples = 0;                                              // 出声就重来
        }

        public void NotifyWake() { NotifyWake(PreWakeSeconds); }

        // v2：sherpa 给出的唤醒时刻精确，回看只需盖住起音；应答刚播完时回看会录进她的声音，要给 0
        public void NotifyWake(float preRollSeconds)
        {
            if (armed) return;   // 已唤醒忽略（关键词连发）
            armed = true;
            wakeSample = totalWritten;
            silenceSamples = 0;
            armedPreRoll = Math.Max(0f, preRollSeconds);
        }

        public bool UtteranceReady
        {
            get
            {
                if (!armed) return false;
                if (totalWritten - wakeSample >= (long)(MaxUtteranceSeconds * SampleRate)) return true;
                return silenceSamples >= (long)(SilenceSeconds * SampleRate);
            }
        }

        // 返回 [唤醒前回看 + 唤醒后全部]，取完复位回待唤醒。缓冲里的旧音频
        // 保留继续滚（下一次唤醒的回看还能用它）。
        public bool TryTakeUtterance(out float[] samples)
        {
            samples = null;
            if (!armed || !UtteranceReady) return false;
            samples = SliceArmed();
            Reset();
            return true;
        }

        // v2：「说完没有」由会话状态机判定，这里不看静音，已唤醒就交出
        public bool TakeArmed(out float[] samples)
        {
            samples = null;
            if (!armed) return false;
            samples = SliceArmed();
            Reset();
            return true;
        }

        float[] SliceArmed()
        {
            long start = wakeSample - (long)(armedPreRoll * SampleRate);
            if (start < totalWritten - ring.Length) start = totalWritten - ring.Length;
            if (start < 0) start = 0;

            int count = (int)(totalWritten - start);
            var samples = new float[count];
            for (int i = 0; i < count; i++)
                samples[i] = ring[(int)((start + i) % ring.Length)];
            return samples;
        }

        public void Reset()
        {
            armed = false;
            silenceSamples = 0;
            wakeSample = 0;
        }

        public void Clear()
        {
            Reset();
            Array.Clear(ring, 0, ring.Length);
            writePos = 0;
            totalWritten = 0;
        }
    }
}
