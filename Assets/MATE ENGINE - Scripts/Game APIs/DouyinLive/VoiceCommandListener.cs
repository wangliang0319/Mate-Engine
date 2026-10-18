using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR
using UnityEngine.Windows.Speech;
#endif

namespace DouyinLive
{
    // 语音助手入口：麦克风常开 → 唤醒（本地关键词，系统不支持就降级音量检测）→
    // 录一句话（静音判定）→ 云端 ASR → 剥唤醒词 → InjectVoiceChat 伪装弹幕。
    // 只做采集和转写，不做任何意图判断；角色出声时暂停监听（防自触发）。
    //
    // 启停统一走 MonoBehaviour.enabled：ApplySettings 里 Configure + enabled 赋值。
    public class VoiceCommandListener : MonoBehaviour
    {
        public bool debugLog = false;

        public List<string> WakeWords = new List<string> { "小美小美", "小美" };
        public float CommandWindow = 8f;      // 唤醒后最长录音秒数
        public float SilenceSeconds = 1.2f;   // 静音多久算说完
        public bool PauseWhilePerforming = true;
        public bool KeywordWake = true;       // false=强制音量检测模式（与本地识别器抢设备时的逃生门）

        SpeechPipeline speech;      // 角色正在说话？
        SongService song;           // 角色正在唱歌？
        DouyinLiveManager manager;  // 命令上交
        CloudASRProvider asr;

        string collectionKey;   // 影响采集的参数指纹，变了才重启麦克风/识别器
        CustomDancePlayer.AvatarDanceHandler mmdDance;   // MMD 舞包/礼物舞的原曲不经 SongService，要单独盯
        float mmdNextFind;

        VoiceCaptureBuffer buffer;
        AudioClip micClip;
        string micDevice;
        int lastReadPos;            // 上次读到 micClip 的位置（环绕剪裁）
        float vadRms;               // 最近一次读入的 RMS（VAD 起音用）
        float vadLoudSince = -1f;   // 连续超阈值起点
        bool keywordMode;           // false = VAD 降级
        bool recognizing;           // 关键词识别器当前是否在跑
        bool asrInFlight;
        CancellationTokenSource cancel;

        const float VadStartSeconds = 0.3f;  // 连续这么久超阈值才算起音
        const float VadStartRms = 0.03f;     // 起音阈值比静音判定高一点，防底噪

#if UNITY_STANDALONE_WIN || UNITY_EDITOR
        KeywordRecognizer recognizer;
#endif

        public void Configure(SpeechPipeline speechPipeline, SongService songService,
                              DouyinLiveManager managerRef, CloudASRProvider asrProvider,
                              List<string> wakeWords, float commandWindow, float silenceSeconds,
                              bool pauseWhilePerforming, bool keywordWake, bool debug)
        {
            speech = speechPipeline;
            song = songService;
            manager = managerRef;
            asr = asrProvider;
            WakeWords = wakeWords != null ? new List<string>(wakeWords) : new List<string>();
            CommandWindow = Mathf.Clamp(commandWindow, 2f, 15f);
            SilenceSeconds = Mathf.Clamp(silenceSeconds, 0.5f, 3f);
            PauseWhilePerforming = pauseWhilePerforming;
            KeywordWake = keywordWake;
            debugLog = debug;
            // ApplySettings 高频调用（F9/设置页/测试按钮）：只有影响采集的参数变了
            // 才重启麦克风和识别器，纯引用赋值不动设备（重启有 100~300ms 拾音空洞）
            var key = string.Join("|", WakeWords) + "|" + CommandWindow + "|" + SilenceSeconds + "|" + KeywordWake;
            if (isActiveAndEnabled && key != collectionKey) { StopMic(); StartMic(); }
            collectionKey = key;
        }

        // StartLive/StopLive 调用：丢弃在途转写与缓冲，别让上一场的话进这一场
        public void ResetSession()
        {
            cancel?.Cancel();
            cancel = new CancellationTokenSource();
            buffer?.Clear();
            vadLoudSince = -1f;
        }

        void OnEnable() { StartMic(); }
        void OnDisable() { StopMic(); }
        void OnDestroy() { StopMic(); }

        void StartMic()
        {
            if (WakeWords.Count == 0)
            {
                Debug.LogWarning("[Voice] 唤醒词列表为空，语音助手不启动");
                enabled = false;
                return;
            }
            if (Microphone.devices.Length == 0)
            {
                Debug.LogWarning("[Voice] 没有可用麦克风（检查 Windows 麦克风权限），语音助手不启动");
                enabled = false;
                return;
            }
            micDevice = Microphone.devices[0];
            // clip 长度必须盖住环形缓冲（PreWake + 窗口 + 余量），环绕读取才安全
            int clipSec = Mathf.CeilToInt(3f + CommandWindow + 1f);
            micClip = Microphone.Start(micDevice, true, clipSec, 16000);
            if (micClip == null)
            {
                Debug.LogWarning("[Voice] 麦克风打开失败，语音助手不启动");
                enabled = false;
                return;
            }
            int micRate = micClip.frequency;
            buffer = new VoiceCaptureBuffer(micRate, 3f, CommandWindow, SilenceSeconds, 0.015f);
            lastReadPos = Microphone.GetPosition(micDevice);
            cancel?.Cancel();
            cancel = new CancellationTokenSource();
            asrInFlight = false;
            vadLoudSince = -1f;
            TryStartKeyword();
            if (debugLog)
                Debug.Log($"[Voice] 已启动 {micDevice} @{micRate}Hz 模式={(keywordMode ? "关键词" : "音量检测")}");
        }

        void StopMic()
        {
            TryStopKeyword();
            if (!string.IsNullOrEmpty(micDevice) && Microphone.IsRecording(micDevice))
                Microphone.End(micDevice);
            micClip = null;
            buffer = null;
            cancel?.Cancel();
            cancel = null;
        }

        void TryStartKeyword()
        {
            keywordMode = false;
            recognizing = false;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR
            if (!KeywordWake)
            {
                Debug.Log("[Voice] 已配置为音量检测模式（douyinVoiceKeywordWake=false）");
                return;
            }
            try
            {
                if (!PhraseRecognitionSystem.isSupported)
                {
                    Debug.Log("[Voice] 系统不支持语音识别（多半没装中文语音包），降级音量检测模式");
                    return;
                }
                recognizer = new KeywordRecognizer(WakeWords.ToArray(), ConfidenceLevel.Medium);
                recognizer.OnPhraseRecognized += OnPhraseRecognized;
                recognizer.Start();
                keywordMode = true;
                recognizing = true;
            }
            catch (Exception ex)
            {
                Debug.Log("[Voice] 关键词唤醒启动失败，降级音量检测模式：" + ex.Message);
                if (recognizer != null) { recognizer.Dispose(); recognizer = null; }
                keywordMode = false;
            }
#endif
        }

        void TryStopKeyword()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR
            if (recognizer != null)
            {
                recognizer.OnPhraseRecognized -= OnPhraseRecognized;
                try { recognizer.Stop(); } catch { /* 已停就随它去 */ }
                recognizer.Dispose();
                recognizer = null;
            }
            recognizing = false;
#endif
        }

        // 角色出声期间把识别器停掉（它还在监听麦克风的话会把角色自己的话当输入）
        void SetRecognitionActive(bool on)
        {
            if (!keywordMode || recognizing == on) return;
            recognizing = on;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR
            try { if (on) recognizer?.Start(); else recognizer?.Stop(); }
            catch (Exception ex) { if (debugLog) Debug.Log("[Voice] 切换识别状态失败: " + ex.Message); }
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR
        // 识别回调来自语音识别的后台线程：这里绝不碰 Unity API（enabled 是原生
        // getter，后台线程一碰就抛 get_enabled 异常），转主线程处理。Drain 每帧跑
        // （Manager.Update 第一行），延迟 ≤1 帧，PreWake 3 秒回看完全吸收。
        void OnPhraseRecognized(PhraseRecognizedEventArgs args)
        {
            var text = args.text;
            var conf = args.confidence;
            MainThreadDispatcher.Post(() => OnWakeFromMainThread(text, conf));
        }

        void OnWakeFromMainThread(string text, ConfidenceLevel conf)
        {
            if (this == null || !enabled) return;
            if (asrInFlight || buffer == null) return;
            buffer.NotifyWake();
            if (debugLog) Debug.Log($"[Voice] 唤醒: {text} ({conf})");
        }
#endif

        bool IsPerforming()
        {
            if (speech != null && (speech.IsSpeaking || speech.QueueCount > 0)) return true;
            if (song != null && song.IsPlaying) return true;
            // MMD 舞包的点歌/点舞/礼物舞用 AvatarDanceHandler 自己的 AudioSource 放原曲，
            // 不经 SongService——漏了它，MMD 表演期间监听不暂停，歌声会进识别
            if (mmdDance == null && Time.unscaledTime >= mmdNextFind)
            {
                mmdDance = FindFirstObjectByType<CustomDancePlayer.AvatarDanceHandler>();
                mmdNextFind = Time.unscaledTime + 5f;   // 场景里没有时限频重找，别每帧扫
            }
            return mmdDance != null && mmdDance.audioSource != null && mmdDance.audioSource.isPlaying;
        }

        void Update()
        {
            if (asr == null || manager == null || buffer == null) return;   // 未装配/麦克风没起来

            // 防自触发闭环：角色出声期间停识别、清缓冲、丢积压，说完自然恢复
            if (PauseWhilePerforming && IsPerforming())
            {
                SetRecognitionActive(false);
                buffer.Clear();
                lastReadPos = Microphone.GetPosition(micDevice);
                vadRms = 0f;
                vadLoudSince = -1f;
                return;
            }
            SetRecognitionActive(true);

            ReadMicIntoBuffer();

            // VAD 降级模式：连续 0.3 秒超阈值当作起音（关键词模式唤醒交给识别器）
            if (!keywordMode && !asrInFlight)
            {
                if (vadRms >= VadStartRms)
                {
                    if (vadLoudSince < 0f) vadLoudSince = Time.unscaledTime;
                }
                else vadLoudSince = -1f;
                if (vadLoudSince >= 0f && Time.unscaledTime - vadLoudSince >= VadStartSeconds)
                {
                    buffer.NotifyWake();
                    vadLoudSince = -1f;
                    if (debugLog) Debug.Log("[Voice] 音量检测唤醒");
                }
            }

            if (!asrInFlight && buffer.UtteranceReady && buffer.TryTakeUtterance(out var samples))
                SubmitToAsr(samples);
        }

        void ReadMicIntoBuffer()
        {
            if (micClip == null || string.IsNullOrEmpty(micDevice)) return;
            int pos = Microphone.GetPosition(micDevice);
            int total = micClip.samples;
            int available = pos >= lastReadPos ? pos - lastReadPos : total - lastReadPos + pos;
            if (available <= 0) { vadRms = 0f; return; }
            if (available > total) available = total;

            var chunk = new float[available];
            int first = Mathf.Min(available, total - lastReadPos);
            if (first == available)
            {
                micClip.GetData(chunk, lastReadPos);
            }
            else
            {
                // 跨环绕点分两段读
                var head = new float[first];
                micClip.GetData(head, lastReadPos);
                Array.Copy(head, 0, chunk, 0, first);
                var tail = new float[available - first];
                micClip.GetData(tail, 0);
                Array.Copy(tail, 0, chunk, first, tail.Length);
            }
            lastReadPos = (lastReadPos + available) % total;

            double sum = 0;
            for (int i = 0; i < available; i++) sum += (double)chunk[i] * chunk[i];
            vadRms = (float)Math.Sqrt(sum / available);
            buffer.Push(chunk);
        }

        void SubmitToAsr(float[] samples)
        {
            byte[] wav;
            try { wav = WavEncoder.Encode(samples, buffer.SampleRate); }
            catch (Exception ex) { if (debugLog) Debug.Log("[Voice] WAV 编码失败: " + ex.Message); return; }

            asrInFlight = true;   // 在飞期间忽略新唤醒，防连发重入
            var ct = cancel != null ? cancel.Token : CancellationToken.None;
            _ = TranscribeAndSubmit(wav, ct);
        }

        async Task TranscribeAndSubmit(byte[] wav, CancellationToken ct)
        {
            string text = null;
            try { text = await asr.TranscribeAsync(wav, ct); }
            catch (Exception ex) { if (debugLog) Debug.Log("[Voice] ASR 异常: " + ex.Message); }
            var t = text;
            // 转写可能从线程池线程回来，统一转主线程再碰 Unity 对象
            MainThreadDispatcher.Post(() => HandleTranscript(t));
        }

        void HandleTranscript(string text)
        {
            asrInFlight = false;
            if (string.IsNullOrEmpty(text))
            {
                if (debugLog) Debug.Log("[Voice] ASR 无结果");
                return;
            }
            // 关键词模式信任识别器（文本里找不到唤醒词也放行）；VAD 模式必须有
            string command = VoiceCommandParser.ExtractCommand(text, WakeWords, !keywordMode);
            if (command == null)
            {
                if (debugLog) Debug.Log("[Voice] 无唤醒词，丢弃: " + text);
                return;
            }
            if (command.Length == 0)
            {
                if (debugLog) Debug.Log("[Voice] 只有唤醒词: " + text);
                return;
            }
            if (debugLog) Debug.Log("[Voice] 命令: " + command);
            manager?.InjectVoiceChat(command);
        }
    }
}
