using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DouyinLive
{
    // 语音应答话术：开播时用当前 TTS 预合成并缓存成 AudioClip，唤醒后零延迟播放
    // （现合成要等网络，主播喊完要干等一两秒）。合成失败或 TTS 关闭时只弹气泡。
    public class VoiceAckLines
    {
        public const string ListeningBubble = "我在听～";
        public const string NotHeardLine = "没听清～";

        public bool debugLog;

        readonly List<AudioClip> acks = new List<AudioClip>();
        AudioClip notHeard;
        string fingerprint;
        int generation;   // 配置变了作废在途合成，免得旧话术晚到混进新列表

        // ApplySettings 会高频调用（F9/设置页）：TTS 和话术都没变就什么也不做
        public void Prepare(ITTSProvider provider, IReadOnlyList<string> lines)
        {
            string fp = (provider != null ? provider.Name : "none") + "|" + (lines == null ? "" : string.Join("|", lines));
            if (fp == fingerprint) return;
            fingerprint = fp;
            generation++;
            Clear();
            if (provider == null || !provider.IsAvailable || lines == null) return;
            int gen = generation;
            foreach (var line in lines)
                if (!string.IsNullOrWhiteSpace(line)) Synth(provider, line.Trim(), gen, false);
            Synth(provider, NotHeardLine, gen, true);
        }

        void Synth(ITTSProvider provider, string text, int gen, bool isNotHeard)
        {
            Task.Run(() => provider.SynthesizeAsync(text, CancellationToken.None))
                .ContinueWith(t =>
                {
                    TTSResult r = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    if (r == null && debugLog)
                        Debug.Log($"[VoiceAck] 应答「{text}」合成失败：{t.Exception?.GetBaseException().Message}");
                    MainThreadDispatcher.Post(() =>
                    {
                        if (gen != generation || r == null || !r.IsValid) return;
                        var clip = AudioClip.Create("ack", r.Samples.Length / r.Channels, r.Channels, r.SampleRate, false);
                        clip.SetData(r.Samples, 0);
                        if (isNotHeard) notHeard = clip; else acks.Add(clip);
                    });
                }, TaskScheduler.Default);
        }

        public void PlayAck(SpeechPipeline speech, Action onFinished)
        {
            AudioClip clip = acks.Count > 0 ? acks[UnityEngine.Random.Range(0, acks.Count)] : null;
            speech.PlayImmediate(ListeningBubble, clip, onFinished);
        }

        public void PlayNotHeard(SpeechPipeline speech)
        {
            speech.PlayImmediate(NotHeardLine, notHeard, null);
        }

        void Clear()
        {
            foreach (var c in acks) if (c != null) UnityEngine.Object.Destroy(c);
            acks.Clear();
            if (notHeard != null) UnityEngine.Object.Destroy(notHeard);
            notHeard = null;
        }
    }
}
