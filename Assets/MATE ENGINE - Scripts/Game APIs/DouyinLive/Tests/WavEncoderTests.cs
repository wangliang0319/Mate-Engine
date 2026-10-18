using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class WavEncoderTests
    {
        [Test]
        public void 头部标记和长度正确()
        {
            var wav = WavEncoder.Encode(new float[100], 16000);
            Assert.AreEqual(44 + 200, wav.Length);
            Assert.AreEqual('R', (char)wav[0]);
            Assert.AreEqual('I', (char)wav[1]);
            Assert.AreEqual('F', (char)wav[2]);
            Assert.AreEqual('F', (char)wav[3]);
            Assert.AreEqual('W', (char)wav[8]);
            Assert.AreEqual('f', (char)wav[12]);   // fmt 块
            Assert.AreEqual('d', (char)wav[36]);   // data 块
            Assert.AreEqual(36 + 200, wav[4] | (wav[5] << 8) | (wav[6] << 16) | (wav[7] << 24));
            Assert.AreEqual(16000, wav[24] | (wav[25] << 8) | (wav[26] << 16) | (wav[27] << 24));
            Assert.AreEqual(200, wav[40] | (wav[41] << 8) | (wav[42] << 16) | (wav[43] << 24));
        }

        [Test]
        public void 单声道16位参数正确()
        {
            var wav = WavEncoder.Encode(new float[10], 44100);
            Assert.AreEqual(1, wav[22] | (wav[23] << 8));        // 声道数
            Assert.AreEqual(1, wav[20] | (wav[21] << 8));        // PCM 格式
            Assert.AreEqual(88200, wav[28] | (wav[29] << 8) | (wav[30] << 16) | (wav[31] << 24)); // byte rate = 44100×2
            Assert.AreEqual(2, wav[32] | (wav[33] << 8));        // block align
            Assert.AreEqual(16, wav[34] | (wav[35] << 8));       // 位深
        }

        [Test]
        public void 半幅样本编码()
        {
            var wav = WavEncoder.Encode(new float[] { 0.5f }, 16000);
            short s = (short)(wav[44] | (wav[45] << 8));
            Assert.AreEqual(16384, s);   // 0.5 × 32767 = 16383.5，四舍五入到偶数
        }

        [Test]
        public void 越幅钳制()
        {
            var wav = WavEncoder.Encode(new float[] { 2f, -2f }, 16000);
            short s0 = (short)(wav[44] | (wav[45] << 8));
            short s1 = (short)(wav[46] | (wav[47] << 8));
            Assert.AreEqual(short.MaxValue, s0);
            Assert.AreEqual(-32767, s1);   // -1 × 32767，不是 short.MinValue
        }

        [Test]
        public void 空样本抛异常()
        {
            Assert.Throws<System.ArgumentNullException>(() => WavEncoder.Encode(null, 16000));
            Assert.Throws<System.ArgumentException>(() => WavEncoder.Encode(new float[0], 16000));
        }
    }
}
