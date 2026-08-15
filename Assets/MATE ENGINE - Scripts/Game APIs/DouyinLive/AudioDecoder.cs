using System;
using System.IO;
using NAudio.Wave;

namespace DouyinLive
{
    // mp3/wav 字节流 → float PCM。仅后台线程调用（NAudio 与 Unity API 无关）。
    public static class AudioDecoder
    {
        public static TTSResult Decode(byte[] data)
        {
            if (data == null || data.Length < 12) throw new ArgumentException("audio data too short");
            if (IsWav(data)) return DecodeWave(new WaveFileReader(new MemoryStream(data)));
            return DecodeWave(new Mp3FileReaderBase(new MemoryStream(data),
                wf => new AcmMp3FrameDecompressor(wf)));
        }

        static bool IsWav(byte[] d) =>
            d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' &&
            d[8] == 'W' && d[9] == 'A' && d[10] == 'V' && d[11] == 'E';

        static TTSResult DecodeWave(WaveStream reader)
        {
            using (reader)
            {
                var sp = reader.ToSampleProvider();
                var wf = sp.WaveFormat;
                var all = new System.Collections.Generic.List<float>(64 * 1024);
                var buf = new float[8192];
                int n;
                while ((n = sp.Read(buf, 0, buf.Length)) > 0)
                    for (int i = 0; i < n; i++) all.Add(buf[i]);
                return new TTSResult
                {
                    Samples = all.ToArray(),
                    Channels = wf.Channels,
                    SampleRate = wf.SampleRate
                };
            }
        }
    }
}
