using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SherpaOnnx;

namespace DouyinLive
{
    // sherpa-onnx 离线唤醒：主线程逐帧 Feed 麦克风采样，检出唤醒词返回 true。
    // 不依赖 UnityEngine（模型目录由调用方给），单独成程序集，用官方测试音频做 EditMode 测试。
    // 2026-10-18 spike 实测：真人麦克风 10/10、无误报、可连续唤醒，实时率 0.019。
    public sealed class SherpaWakeDetector
    {
        public const string EncoderFile = "encoder-epoch-12-avg-2-chunk-16-left-64.onnx";
        public const string DecoderFile = "decoder-epoch-12-avg-2-chunk-16-left-64.onnx";
        public const string JoinerFile = "joiner-epoch-12-avg-2-chunk-16-left-64.onnx";
        public const string TokensFile = "tokens.txt";

        KeywordSpotter kws;
        OnlineStream stream;
        readonly List<string> keywordErrors = new List<string>();

        public bool IsAvailable => kws != null;
        public string Error { get; private set; }
        public IReadOnlyList<string> KeywordErrors => keywordErrors;
        public string LastKeyword { get; private set; }

        SherpaWakeDetector() { }

        // 永不抛异常：缺文件、唤醒词全错、原生库加载失败都只置 IsAvailable=false 并写 Error，
        // 调用方据此退回音量检测模式——语音出问题不能影响直播。
        public static SherpaWakeDetector Create(string modelDir, IReadOnlyList<string> keywordLines, string scratchDir)
        {
            var d = new SherpaWakeDetector();
            try
            {
                foreach (var f in new[] { EncoderFile, DecoderFile, JoinerFile, TokensFile })
                {
                    string path = Path.Combine(modelDir, f);
                    if (!File.Exists(path)) { d.Error = "缺少模型文件 " + path; return d; }
                }

                var vocab = WakeKeywordSpec.ParseVocabulary(File.ReadAllText(Path.Combine(modelDir, TokensFile)));
                var kw = WakeKeywordSpec.Build(keywordLines, vocab);
                d.keywordErrors.AddRange(kw.Errors);
                if (kw.ValidCount == 0) { d.Error = "没有有效的唤醒词"; return d; }

                // 唤醒词走文件（spike 验证过的路径）：写到可写的缓存目录，UTF-8 无 BOM
                Directory.CreateDirectory(scratchDir);
                string kwFile = Path.Combine(scratchDir, "sherpa_keywords.txt");
                File.WriteAllText(kwFile, kw.KeywordsText, new UTF8Encoding(false));

                var c = new KeywordSpotterConfig();
                c.FeatConfig.SampleRate = 16000;
                c.FeatConfig.FeatureDim = 80;
                c.ModelConfig.Transducer.Encoder = Path.Combine(modelDir, EncoderFile);
                c.ModelConfig.Transducer.Decoder = Path.Combine(modelDir, DecoderFile);
                c.ModelConfig.Transducer.Joiner = Path.Combine(modelDir, JoinerFile);
                c.ModelConfig.Tokens = Path.Combine(modelDir, TokensFile);
                c.ModelConfig.Provider = "cpu";
                c.ModelConfig.NumThreads = 1;
                c.KeywordsThreshold = 0.25f;
                c.KeywordsScore = 1.0f;
                c.KeywordsFile = kwFile;

                d.kws = new KeywordSpotter(c);
                d.stream = d.kws.CreateStream();
            }
            catch (DllNotFoundException ex)
            {
                d.kws = null;
                d.Error = "找不到 sherpa 原生库（Plugins/SherpaOnnx/x86_64）：" + ex.Message;
            }
            catch (Exception ex)
            {
                d.kws = null;
                d.Error = "sherpa 初始化失败：" + ex.Message;
            }
            return d;
        }

        public bool Feed(float[] samples, int count, int sampleRate)
        {
            if (kws == null || samples == null || count <= 0) return false;
            float[] chunk = samples;
            if (count < samples.Length)
            {
                chunk = new float[count];
                Array.Copy(samples, chunk, count);
            }
            stream.AcceptWaveform(sampleRate, chunk);
            bool hit = false;
            while (kws.IsReady(stream))
            {
                kws.Decode(stream);
                var r = kws.GetResult(stream);
                if (!string.IsNullOrEmpty(r.Keyword))
                {
                    LastKeyword = r.Keyword;
                    kws.Reset(stream);   // 不 Reset 就检不出下一次唤醒
                    hit = true;
                }
            }
            return hit;
        }

        // KeywordSpotter 有 Dispose 但不保证实现 IDisposable，显式调用；
        // OnlineStream 是否实现 IDisposable 不确定：先转 object 再判断，密封类直接 as 接口会编译失败（CS0039）
        public void Dispose()
        {
            object s = stream;
            (s as IDisposable)?.Dispose();
            stream = null;
            kws?.Dispose();
            kws = null;
        }
    }
}
