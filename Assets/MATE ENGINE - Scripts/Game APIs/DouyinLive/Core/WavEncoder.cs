using System;

namespace DouyinLive
{
    // float PCM → 16bit 单声道 WAV。服务端（whisper/SenseVoice）自己重采样，
    // 这边只把真实采样率写进头里，客户端不做任何重采样。
    public static class WavEncoder
    {
        public static byte[] Encode(float[] samples, int sampleRate)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (samples.Length == 0) throw new ArgumentException("没有样本", "samples");
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException("sampleRate");

            int dataBytes = samples.Length * 2;
            var wav = new byte[44 + dataBytes];

            wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
            WriteInt32(wav, 4, 36 + dataBytes);
            wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';
            wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
            WriteInt32(wav, 16, 16);              // fmt 块长
            WriteInt16(wav, 20, 1);               // PCM
            WriteInt16(wav, 22, 1);               // mono
            WriteInt32(wav, 24, sampleRate);
            WriteInt32(wav, 28, sampleRate * 2);  // byte rate = rate × block align
            WriteInt16(wav, 32, 2);               // block align
            WriteInt16(wav, 34, 16);              // bits per sample
            wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
            WriteInt32(wav, 40, dataBytes);

            int o = 44;
            for (int i = 0; i < samples.Length; i++)
            {
                float f = samples[i];
                if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
                short s = (short)Math.Round(f * 32767f);
                wav[o++] = (byte)(s & 0xFF);
                wav[o++] = (byte)((s >> 8) & 0xFF);
            }
            return wav;
        }

        static void WriteInt32(byte[] b, int at, int v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }

        static void WriteInt16(byte[] b, int at, short v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8);
        }
    }
}
