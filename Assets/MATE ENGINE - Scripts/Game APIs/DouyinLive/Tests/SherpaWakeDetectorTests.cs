using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DouyinLive.Tests
{
    // 用 sherpa 官方测试音频 + 官方唤醒词验证整条链路：原生库加载、逐块喂音频、检出、Reset 后继续检出。
    // 合成语音（硅基流动 CosyVoice）连 SenseVoice 都转不出来，不能当测试数据。
    public class SherpaWakeDetectorTests
    {
        static string ModelDir => Path.Combine(Application.streamingAssetsPath, "SherpaOnnx", "kws");
        static string Scratch => Path.Combine(Application.temporaryCachePath, "sherpa-test");
        static string TestData => Path.Combine(Application.dataPath,
            "MATE ENGINE - Scripts", "Game APIs", "DouyinLive", "Tests", "TestData");

        static float[] ReadWav(string path)
        {
            var b = File.ReadAllBytes(path);
            int p = 12;
            while (p + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, p, 4);
                int size = BitConverter.ToInt32(b, p + 4);
                if (id == "data")
                {
                    int remain = b.Length - p - 8;
                    int bytes = (size <= 0 || size > remain) ? remain : size;
                    var s = new float[bytes / 2];
                    for (int i = 0; i < s.Length; i++) s[i] = BitConverter.ToInt16(b, p + 8 + i * 2) / 32768f;
                    return s;
                }
                p += 8 + size + (size & 1);
            }
            throw new Exception("no data chunk: " + path);
        }

        // 按 100ms 一块喂，模拟麦克风流
        static void FeedAll(SherpaWakeDetector d, float[] wav, List<string> found)
        {
            for (int off = 0; off < wav.Length; off += 1600)
            {
                int n = Math.Min(1600, wav.Length - off);
                var part = new float[n];
                Array.Copy(wav, off, part, 0, n);
                if (d.Feed(part, n, 16000)) found.Add(d.LastKeyword);
            }
        }

        [Test]
        public void 官方音频_检出官方唤醒词()
        {
            var lines = File.ReadAllLines(Path.Combine(TestData, "kws_official_keywords.txt"));
            var d = SherpaWakeDetector.Create(ModelDir, lines, Scratch);
            try
            {
                Assert.IsTrue(d.IsAvailable, d.Error);
                var found = new List<string>();
                FeedAll(d, ReadWav(Path.Combine(TestData, "kws_official_3.bytes")), found);
                FeedAll(d, new float[16000], found);   // 1 秒静音把最后一个词冲出来
                CollectionAssert.Contains(found, "文森特卡索");
                CollectionAssert.Contains(found, "法国");   // 第二次检出：Reset 后能继续检出
            }
            finally { d.Dispose(); }
        }

        [Test]
        public void 缺少模型文件_不可用并说明原因()
        {
            var d = SherpaWakeDetector.Create(Path.Combine(Scratch, "no-model"),
                new[] { WakeKeywordSpec.DefaultKeyword }, Scratch);
            Assert.IsFalse(d.IsAvailable);
            StringAssert.Contains("缺少模型文件", d.Error);
        }

        [Test]
        public void 唤醒词全部无效_不可用()
        {
            var d = SherpaWakeDetector.Create(ModelDir, new[] { "abc @错" }, Scratch);
            Assert.IsFalse(d.IsAvailable);
            Assert.AreEqual(1, d.KeywordErrors.Count);
        }

        [Test]
        public void 部分无效_仍可用并报告()
        {
            var d = SherpaWakeDetector.Create(ModelDir, new[] { WakeKeywordSpec.DefaultKeyword, "abc @错" }, Scratch);
            try
            {
                Assert.IsTrue(d.IsAvailable, d.Error);
                Assert.AreEqual(1, d.KeywordErrors.Count);
            }
            finally { d.Dispose(); }
        }

        [Test]
        public void 默认唤醒词_在真实词表里有效()
        {
            var vocab = WakeKeywordSpec.ParseVocabulary(File.ReadAllText(Path.Combine(ModelDir, "tokens.txt")));
            var r = WakeKeywordSpec.Build(new[] { WakeKeywordSpec.DefaultKeyword }, vocab);
            Assert.AreEqual(1, r.ValidCount, string.Join("; ", r.Errors));
        }
    }
}
