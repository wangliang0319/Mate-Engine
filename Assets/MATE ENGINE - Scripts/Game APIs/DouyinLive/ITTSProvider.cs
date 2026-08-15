using System.Threading;
using System.Threading.Tasks;

namespace DouyinLive
{
    // TTS 合成结果：PCM float 数据（主线程用 AudioClip.Create 包装）
    public class TTSResult
    {
        public float[] Samples;
        public int Channels;
        public int SampleRate;
        public bool IsValid => Samples != null && Samples.Length > 0 && SampleRate > 0;
    }

    public interface ITTSProvider
    {
        // 后台线程调用，返回解码后的 PCM；失败抛异常
        Task<TTSResult> SynthesizeAsync(string text, CancellationToken ct);
        bool IsAvailable { get; }
        string Name { get; }
    }
}
