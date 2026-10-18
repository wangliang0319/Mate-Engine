using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DouyinLive
{
    public class VoiceListenerSettings
    {
        public List<string> WakeWords = new List<string>();       // 中文唤醒词：从 ASR 文本里剥掉；音量检测模式靠它判定唤醒
        public List<string> WakeKeywords = new List<string>();    // sherpa 拼音唤醒词
        public List<string> AckLines = new List<string>();
        public float CommandWindow = 8f;
        public float SilenceSeconds = 1.2f;
        public float AckTimeout = 5f;
        public float FollowUpSeconds = 6f;
        public float DuckVolume = 0.3f;
        public bool KeywordWake = true;
        public bool DebugLog;
    }

    // 语音助手入口（v2）：麦克风常开 → sherpa 离线唤醒 → 会话状态机（应答/聆听/追问/打断/压低音乐）
    // → 云端 ASR → 剥唤醒词 → InjectVoiceChat。时序规则全在 VoiceSessionMachine，这里只负责
    // 读麦克风、喂引擎、执行状态机给出的动作。
    // sherpa 不可用（DLL/模型缺失、唤醒词全写错、配置关闭）时退回旧的音量检测流程：
    // 一口气说完、无应答、角色出声期间暂停监听。
    public class VoiceCommandListener : MonoBehaviour
    {
        const int SampleRate = 16000;
        const float PreWakeSeconds = 3f;      // 音量检测模式的回看（起音判定本身有 0.3 秒延迟）
        const float WakePreRoll = 0.6f;       // sherpa 模式唤醒/追问起音时的回看，盖住起音前的 0.3 秒
        const float VoicedRms = 0.02f;        // 会话状态机的「有声」门槛
        const float SilenceRms = 0.015f;      // 音量检测模式的静音判定（沿用 v1）
        const float VadStartSeconds = 0.3f;   // 音量检测模式：连续这么久超阈值才算起音
        const float VadStartRms = 0.03f;

        public bool debugLog;

        VoiceListenerSettings cfg = new VoiceListenerSettings();
        SpeechPipeline speech;
        SongService song;
        DouyinLiveManager manager;
        CloudASRProvider asr;
        readonly VoiceAckLines ackLines = new VoiceAckLines();

        string collectionKey;   // 影响采集的参数指纹，变了才重启麦克风/唤醒引擎
        VoiceCaptureBuffer buffer;
        AudioClip micClip;
        string micDevice;
        int lastReadPos;
        float frameRms;
        float vadLoudSince = -1f;
        bool asrInFlight;
        bool blockedLastFrame;
        CancellationTokenSource cancel;

        SherpaWakeDetector wake;
        VoiceSessionMachine machine;   // 非空 = sherpa 模式
        string loggedTransition;

        CustomDancePlayer.AvatarDanceHandler mmdDance;   // MMD 舞包的原曲不经 SongService，要单独盯
        float mmdNextFind;
        float mmdVolumeBeforeDuck = -1f;

        public void Configure(SpeechPipeline speechPipeline, SongService songService,
                              DouyinLiveManager managerRef, CloudASRProvider asrProvider,
                              VoiceListenerSettings settings)
        {
            speech = speechPipeline;
            song = songService;
            manager = managerRef;
            asr = asrProvider;
            cfg = settings ?? new VoiceListenerSettings();
            cfg.WakeWords = cfg.WakeWords ?? new List<string>();
            cfg.WakeKeywords = cfg.WakeKeywords ?? new List<string>();
            cfg.CommandWindow = Mathf.Clamp(cfg.CommandWindow, 2f, 15f);
            cfg.SilenceSeconds = Mathf.Clamp(cfg.SilenceSeconds, 0.5f, 3f);
            cfg.AckTimeout = Mathf.Clamp(cfg.AckTimeout, 2f, 15f);
            cfg.FollowUpSeconds = Mathf.Clamp(cfg.FollowUpSeconds, 0f, 20f);
            cfg.DuckVolume = Mathf.Clamp01(cfg.DuckVolume);
            debugLog = cfg.DebugLog;
            ackLines.debugLog = debugLog;
            ackLines.Prepare(speech != null ? speech.Provider : null, cfg.AckLines);

            // ApplySettings 高频调用：只有影响采集的参数变了才重启（重启有拾音空洞，还要重新加载模型）
            var key = string.Join("|", cfg.WakeWords) + "|" + string.Join("|", cfg.WakeKeywords) + "|" +
                      cfg.CommandWindow + "|" + cfg.SilenceSeconds + "|" + cfg.KeywordWake + "|" +
                      cfg.AckTimeout + "|" + cfg.FollowUpSeconds;
            if (isActiveAndEnabled && key != collectionKey) { StopMic(); StartMic(); }
            collectionKey = key;
        }

        // StartLive/StopLive 调用：丢弃进行中的会话与在途转写，恢复被压低的音乐和暂停的播报
        public void ResetSession()
        {
            if (machine != null) Execute(machine.Abort());
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
            if (Microphone.devices.Length == 0)
            {
                Debug.LogWarning("[Voice] 没有可用麦克风（检查 Windows 麦克风权限），语音助手不启动");
                enabled = false;
                return;
            }
            micDevice = Microphone.devices[0];
            // clip 长度必须盖住环形缓冲（回看 + 窗口 + 余量），环绕读取才安全
            int clipSec = Mathf.CeilToInt(PreWakeSeconds + cfg.CommandWindow + 1f);
            micClip = Microphone.Start(micDevice, true, clipSec, SampleRate);
            if (micClip == null)
            {
                Debug.LogWarning("[Voice] 麦克风打开失败，语音助手不启动");
                enabled = false;
                return;
            }
            buffer = new VoiceCaptureBuffer(micClip.frequency, PreWakeSeconds, cfg.CommandWindow, cfg.SilenceSeconds, SilenceRms);
            lastReadPos = Microphone.GetPosition(micDevice);
            cancel?.Cancel();
            cancel = new CancellationTokenSource();
            asrInFlight = false;
            vadLoudSince = -1f;
            blockedLastFrame = false;

            TryStartSherpa();
            if (machine == null && cfg.WakeWords.Count == 0)
            {
                Debug.LogWarning("[Voice] 音量检测模式需要中文唤醒词（douyinVoiceWakeWords 为空），语音助手不启动");
                StopMic();
                enabled = false;
                return;
            }
            Debug.Log($"[Voice] 已启动 {micDevice} @{micClip.frequency}Hz 模式={(machine != null ? "离线唤醒(sherpa)" : "音量检测")}");
        }

        void TryStartSherpa()
        {
            machine = null;
            if (!cfg.KeywordWake)
            {
                Debug.Log("[Voice] 已配置为音量检测模式（douyinVoiceKeywordWake=false）");
                return;
            }
            try
            {
                wake = SherpaWakeDetector.Create(
                    Path.Combine(Application.streamingAssetsPath, "SherpaOnnx", "kws"),
                    cfg.WakeKeywords, Application.temporaryCachePath);
            }
            catch (Exception ex)
            {
                // 托管 sherpa-onnx.dll 缺失时异常在这里抛（Create 内部只兜得住原生库缺失）
                wake = null;
                Debug.LogWarning("[Voice] sherpa 离线唤醒加载失败，退回音量检测模式：" + ex.Message);
                return;
            }
            foreach (var e in wake.KeywordErrors) Debug.LogWarning("[Voice] 唤醒词配置有误，已跳过：" + e);
            if (!wake.IsAvailable)
            {
                Debug.LogWarning("[Voice] sherpa 离线唤醒不可用，退回音量检测模式：" + wake.Error);
                wake.Dispose();
                wake = null;
                return;
            }
            var t = VoiceSessionTiming.Default;
            t.AckTimeout = cfg.AckTimeout;
            t.EndSilence = cfg.SilenceSeconds;
            t.MaxUtterance = cfg.CommandWindow;
            t.FollowUpWindow = cfg.FollowUpSeconds;
            machine = new VoiceSessionMachine(t);
        }

        void StopMic()
        {
            if (machine != null) Execute(machine.Abort());   // 先恢复音乐和播报，再拆
            machine = null;
            if (wake != null) { wake.Dispose(); wake = null; }
            if (!string.IsNullOrEmpty(micDevice) && Microphone.IsRecording(micDevice))
                Microphone.End(micDevice);
            micClip = null;
            buffer = null;
            cancel?.Cancel();
            cancel = null;
        }

        void Update()
        {
            if (asr == null || manager == null || buffer == null) return;   // 未装配/麦克风没起来

            // 停播、菜单打开：中止会话（恢复音乐和播报），丢弃这段时间的音频
            if (manager.VoiceBlocked)
            {
                if (!blockedLastFrame && machine != null) Execute(machine.Abort());
                blockedLastFrame = true;
                lastReadPos = Microphone.GetPosition(micDevice);
                buffer.Clear();
                vadLoudSince = -1f;
                return;
            }
            blockedLastFrame = false;

            if (machine != null) UpdateSherpa();
            else UpdateVad();
        }

        void UpdateSherpa()
        {
            float now = Time.unscaledTime;
            var chunk = ReadMic();
            bool talking = speech != null && speech.IsTalking;
            bool performing = IsPerforming();
            if (chunk != null && wake.Feed(chunk, chunk.Length, micClip.frequency))
            {
                if (debugLog) Debug.Log("[Voice] 唤醒: " + wake.LastKeyword);
                Execute(machine.OnWake(now, talking, performing));
            }
            if (machine != null)
                // 本帧麦克风没有新数据（设备按块推进，高帧率常见）传 null：状态机沿用上一帧，
                // 当成无声会把追问「连续开口 0.3 秒」的计时清零
                Execute(machine.Update(now, chunk == null ? (bool?)null : frameRms >= VoicedRms, talking, performing));
        }

        // 音量检测模式（v1 行为）：角色出声期间暂停监听防自触发；起音即唤醒；靠静音判定说完
        void UpdateVad()
        {
            if (IsCharacterAudible())
            {
                buffer.Clear();
                lastReadPos = Microphone.GetPosition(micDevice);
                vadLoudSince = -1f;
                return;
            }
            ReadMic();
            if (!asrInFlight)
            {
                if (frameRms >= VadStartRms) { if (vadLoudSince < 0f) vadLoudSince = Time.unscaledTime; }
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

        // 读出本帧新增的麦克风采样（跨环绕点分两段），推进录音缓冲，算本帧 RMS
        float[] ReadMic()
        {
            frameRms = 0f;
            if (micClip == null || string.IsNullOrEmpty(micDevice)) return null;
            int pos = Microphone.GetPosition(micDevice);
            int total = micClip.samples;
            int available = pos >= lastReadPos ? pos - lastReadPos : total - lastReadPos + pos;
            if (available <= 0) return null;
            if (available > total) available = total;

            var chunk = new float[available];
            int first = Mathf.Min(available, total - lastReadPos);
            if (first == available)
            {
                micClip.GetData(chunk, lastReadPos);
            }
            else
            {
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
            frameRms = (float)Math.Sqrt(sum / available);
            buffer.Push(chunk);
            return chunk;
        }

        void Execute(IReadOnlyList<VoiceAction> actions)
        {
            // 每次状态转换打一行原因：「她为什么等了 8 秒才回」「追问为什么没接上」全靠它回答
            if (debugLog && machine != null && machine.LastTransition != loggedTransition)
            {
                loggedTransition = machine.LastTransition;
                Debug.Log($"[Voice] 会话 {loggedTransition}（本帧 rms={frameRms:F3}）");
            }
            if (actions == null || actions.Count == 0) return;
            // 状态机返回的是内部复用列表；执行中的回调（应答没音频时同步回调 OnAckFinished）
            // 会再次调用状态机改写它，必须先拷贝
            var list = new VoiceAction[actions.Count];
            for (int i = 0; i < list.Length; i++) list[i] = actions[i];

            foreach (var a in list)
            {
                switch (a)
                {
                    case VoiceAction.InterruptSpeech: speech?.InterruptCurrent(); break;
                    case VoiceAction.HoldSpeech: speech?.SetHold(true); break;
                    case VoiceAction.ReleaseSpeech: speech?.SetHold(false); break;
                    case VoiceAction.ReleaseNewSpeech: speech?.ReleaseNewOnly(); break;
                    case VoiceAction.DuckMusic: DuckMusic(); break;
                    case VoiceAction.UnduckMusic: UnduckMusic(); break;
                    case VoiceAction.StartCapture: buffer?.NotifyWake(WakePreRoll); break;
                    case VoiceAction.StartCaptureAfterAck: buffer?.NotifyWake(0f); break;
                    case VoiceAction.StopCapture: buffer?.Reset(); break;
                    case VoiceAction.PlayAck:
                        if (speech != null) ackLines.PlayAck(speech, OnAckFinished);
                        else OnAckFinished();
                        break;
                    case VoiceAction.SubmitCapture:
                        if (buffer != null && buffer.TakeArmed(out var samples)) SubmitToAsr(samples);
                        else if (machine != null) Execute(machine.OnTranscript(Time.unscaledTime, null));
                        break;
                    case VoiceAction.Inject:
                        if (machine != null && !string.IsNullOrEmpty(machine.PendingCommand))
                        {
                            if (debugLog) Debug.Log("[Voice] 命令: " + machine.PendingCommand);
                            manager?.InjectVoiceChat(machine.PendingCommand);
                        }
                        break;
                    case VoiceAction.SayNotHeard:
                        if (speech != null) ackLines.PlayNotHeard(speech);
                        break;
                }
            }
        }

        void OnAckFinished()
        {
            if (this == null || !enabled || machine == null) return;
            Execute(machine.OnAckFinished(Time.unscaledTime));
        }

        void DuckMusic()
        {
            song?.Duck(cfg.DuckVolume);
            FindMmd();
            if (mmdDance != null && mmdDance.audioSource != null && mmdVolumeBeforeDuck < 0f)
            {
                mmdVolumeBeforeDuck = mmdDance.audioSource.volume;
                mmdDance.audioSource.volume = mmdVolumeBeforeDuck * cfg.DuckVolume;
            }
        }

        void UnduckMusic()
        {
            song?.Unduck();
            // 恢复到压低前的原音量，而不是 1：MMD 舞包可能本来就不是满音量
            if (mmdVolumeBeforeDuck >= 0f && mmdDance != null && mmdDance.audioSource != null)
                mmdDance.audioSource.volume = mmdVolumeBeforeDuck;
            mmdVolumeBeforeDuck = -1f;
        }

        void FindMmd()
        {
            if (mmdDance == null && Time.unscaledTime >= mmdNextFind)
            {
                mmdDance = FindFirstObjectByType<CustomDancePlayer.AvatarDanceHandler>();
                mmdNextFind = Time.unscaledTime + 5f;   // 场景里没有时限频重找，别每帧扫
            }
        }

        // 在唱歌/跳舞（SongService 点歌，或 MMD 舞包/礼物舞的原曲）
        bool IsPerforming()
        {
            if (song != null && song.IsPlaying) return true;
            FindMmd();
            return mmdDance != null && mmdDance.audioSource != null && mmdDance.audioSource.isPlaying;
        }

        // 音量检测模式用：角色任何出声（说话、排队待说、唱歌）都暂停监听
        bool IsCharacterAudible()
        {
            if (speech != null && (speech.IsSpeaking || speech.QueueCount > 0)) return true;
            return IsPerforming();
        }

        void SubmitToAsr(float[] samples)
        {
            byte[] wav;
            try { wav = WavEncoder.Encode(samples, buffer.SampleRate); }
            catch (Exception ex)
            {
                if (debugLog) Debug.Log("[Voice] WAV 编码失败: " + ex.Message);
                if (machine != null) Execute(machine.OnTranscript(Time.unscaledTime, null));
                return;
            }
            asrInFlight = true;   // 音量检测模式：在飞期间忽略新起音
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
            if (this == null) return;
            asrInFlight = false;

            if (machine != null)
            {
                // sherpa 已确认唤醒，文本里找不到唤醒词也放行；null = ASR 失败
                string cmd = text == null ? null : VoiceCommandParser.ExtractCommand(text, cfg.WakeWords, false);
                if (debugLog) Debug.Log($"[Voice] 转写: {text ?? "(失败)"} → 命令: {cmd ?? "(无)"}");
                Execute(machine.OnTranscript(Time.unscaledTime, cmd));
                return;
            }

            // 音量检测模式：文本里必须有唤醒词
            if (string.IsNullOrEmpty(text))
            {
                if (debugLog) Debug.Log("[Voice] ASR 无结果");
                return;
            }
            string command = VoiceCommandParser.ExtractCommand(text, cfg.WakeWords, true);
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
