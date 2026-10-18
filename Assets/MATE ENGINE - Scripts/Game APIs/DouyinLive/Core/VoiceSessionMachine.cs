using System;
using System.Collections.Generic;

namespace DouyinLive
{
    public enum VoiceSessionState { Idle, WakePending, Ack, Listening, Processing, AwaitReply, FollowUp }

    // 状态机交给调用方执行的动作，按列表顺序执行。
    public enum VoiceAction
    {
        InterruptSpeech,       // 打断她当前这句（队列保留）
        HoldSpeech,            // 暂停播报队列出队（主播说话时她不能开口，否则声音混进录音）
        ReleaseSpeech,         // 完全恢复播报队列（含会话期间积压的）
        ReleaseNewSpeech,      // 只放行此刻之后入队的（她的回答），会话前积压的继续压着
        DuckMusic,             // 压低歌声
        UnduckMusic,           // 恢复歌声
        StartCapture,          // 开始录音，带短回看（唤醒、追问起音时盖住起音）
        StartCaptureAfterAck,  // 开始录音，零回看（回看会录进她刚说完的应答）
        StopCapture,           // 丢弃正在录的音频
        PlayAck,               // 播应答；播完由调用方回调 OnAckFinished
        SubmitCapture,         // 取出录音送 ASR（取出即停止录音）；转写完成回调 OnTranscript
        Inject,                // 把 PendingCommand 交给直播管线
        SayNotHeard            // 说「没听清～」
    }

    public struct VoiceSessionTiming
    {
        public float PauseWindow;       // 唤醒后观察多久，决定「停顿等应答」还是「一口气说完」
        public float OneBreathVoiced;   // 观察窗内累计有声多久算一口气说完（唤醒词尾音短于它）
        public float AckTimeout;        // 应答后多久没开口就放弃
        public float EndSilence;        // 开口后静音多久算说完
        public float MaxUtterance;      // 一句话最长秒数
        public float ReplyTimeout;      // 等她开口回应最多多久
        public float FollowUpWindow;    // 追问窗口，0=关闭
        public float FollowUpOnset;     // 追问时连续有声多久算开口（防咳嗽、键盘声）
        public float AckSafety;         // 应答播完回调丢失时的兜底
        public float ProcessingSafety;  // 转写回调丢失时的兜底（ASR 客户端超时 15 秒）

        public static VoiceSessionTiming Default => new VoiceSessionTiming
        {
            PauseWindow = 0.4f, OneBreathVoiced = 0.15f, AckTimeout = 5f, EndSilence = 1.2f,
            MaxUtterance = 8f, ReplyTimeout = 15f, FollowUpWindow = 6f, FollowUpOnset = 0.3f,
            AckSafety = 5f, ProcessingSafety = 20f
        };
    }

    // 语音会话状态机（手机助手式）：待机 → 判停顿 → 应答 → 聆听 → 处理 → 等回应 → 追问 → 待机。
    // 纯逻辑：时间和音频状态都由调用方喂入，返回要执行的动作；不碰麦克风、不碰 Unity，
    // 所以每条时序规则都能用模拟时钟单测。返回的列表是内部复用的，调用方下次调用前用完或先拷贝。
    public sealed class VoiceSessionMachine
    {
        // 播报暂停的程度：会话中全停；等她回答时只放行新入队的（积压的留到会话结束）
        enum HoldLevel { None, Backlog, Full }

        readonly VoiceSessionTiming tm;
        readonly List<VoiceAction> output = new List<VoiceAction>();

        public VoiceSessionState State { get; private set; } = VoiceSessionState.Idle;
        public string PendingCommand { get; private set; }
        // 最近一次状态转换及原因（调试日志用：主播问「她为什么等了 8 秒才回」时靠它回答）
        public string LastTransition { get; private set; } = "";

        // 副作用账本：进待机时据此恢复，保证压低/暂停各只恢复一次
        bool ducked, capturing;
        HoldLevel hold;
        bool heard, fromFollowUp, sawTalking;
        bool lastVoiced;   // 上一次有数据的帧是否有声：空帧沿用它
        float enteredAt, lastVoicedAt, voicedAccum, onsetAccum;
        float lastT;
        bool hasLastT;

        public VoiceSessionMachine(VoiceSessionTiming timing) { tm = timing; }

        public IReadOnlyList<VoiceAction> OnWake(float t, bool talking, bool performing)
        {
            output.Clear();
            switch (State)
            {
                case VoiceSessionState.WakePending:
                case VoiceSessionState.Ack:
                case VoiceSessionState.Processing:
                    return output;   // 正在听/正在处理：重复的唤醒词不重置会话
                case VoiceSessionState.Listening:
                    // 追问录音里检出唤醒词：主播是在重新唤醒（喊完会停下等应答），按新唤醒走；
                    // 否则转写只剩唤醒词，追问分支会把空命令静默丢掉，主播以为没唤醒
                    if (!fromFollowUp) return output;
                    break;
            }
            if (talking) output.Add(VoiceAction.InterruptSpeech);
            if (performing && !ducked) { output.Add(VoiceAction.DuckMusic); ducked = true; }
            HoldFull();
            output.Add(VoiceAction.StartCapture);
            capturing = true;
            voicedAccum = 0f;
            fromFollowUp = false;
            PendingCommand = null;
            Enter(VoiceSessionState.WakePending, t, "检出唤醒词");
            return output;
        }

        // 每帧调用。voiced = 这一帧新读到的麦克风音频是否有声；null = 这一帧麦克风没有新数据
        // （设备按块推进，高帧率下常见），沿用上一帧的结论——当成无声会把追问的起音计时清零。
        // talking = 她正在出声；performing = 在唱歌/跳舞。
        public IReadOnlyList<VoiceAction> Update(float t, bool? voiced, bool talking, bool performing)
        {
            output.Clear();
            float dt = hasLastT ? Math.Max(0f, t - lastT) : 0f;
            lastT = t;
            hasLastT = true;
            if (voiced.HasValue) lastVoiced = voiced.Value;
            bool v = lastVoiced;

            switch (State)
            {
                case VoiceSessionState.WakePending:
                    if (v) voicedAccum += dt;
                    if (voicedAccum >= tm.OneBreathVoiced)
                        EnterListening(t, true, false, "一口气说完，跳过应答");   // 录音不断，直接聆听
                    else if (t - enteredAt >= tm.PauseWindow)
                    {
                        output.Add(VoiceAction.StopCapture);       // 应答期间不录，免得录进她的声音
                        capturing = false;
                        output.Add(VoiceAction.PlayAck);
                        Enter(VoiceSessionState.Ack, t, "唤醒后停顿，播应答");
                    }
                    break;

                case VoiceSessionState.Ack:
                    if (t - enteredAt >= tm.AckSafety) AckDone(t, "应答回调超时，兜底开始聆听");
                    break;

                case VoiceSessionState.Listening:
                    if (v) { heard = true; lastVoicedAt = t; }
                    if (heard && t - lastVoicedAt >= tm.EndSilence) Submit(t, $"静音 {tm.EndSilence:0.#} 秒，说完了");
                    else if (t - enteredAt >= tm.MaxUtterance)
                    {
                        if (heard) Submit(t, $"录满 {tm.MaxUtterance:0.#} 秒上限（背景声可能一直被判为有声）");
                        else GoIdle($"录满 {tm.MaxUtterance:0.#} 秒仍没开口");
                    }
                    else if (!heard && t - enteredAt >= tm.AckTimeout) GoIdle($"应答后 {tm.AckTimeout:0.#} 秒没开口，放弃");
                    break;

                case VoiceSessionState.Processing:
                    if (t - enteredAt >= tm.ProcessingSafety) GoIdle("转写回调超时，放弃");
                    break;

                case VoiceSessionState.AwaitReply:
                    if (performing) GoIdle("她开始表演，不开追问");   // 表演完多半在干别的
                    else if (talking) sawTalking = true;
                    else if (sawTalking)
                    {
                        if (tm.FollowUpWindow > 0f) EnterFollowUp(t);
                        else GoIdle("她说完了（追问已关闭）");
                    }
                    else if (t - enteredAt >= tm.ReplyTimeout) GoIdle($"等她回应超过 {tm.ReplyTimeout:0.#} 秒");
                    break;

                case VoiceSessionState.FollowUp:
                    if (performing) { GoIdle("追问期间她开始表演"); break; }
                    if (v) onsetAccum += dt; else onsetAccum = 0f;
                    if (onsetAccum >= tm.FollowUpOnset)
                    {
                        output.Add(VoiceAction.StartCapture);
                        capturing = true;
                        EnterListening(t, true, true, $"追问：连续开口 {tm.FollowUpOnset:0.#} 秒");
                    }
                    else if (t - enteredAt >= tm.FollowUpWindow) GoIdle($"追问窗口 {tm.FollowUpWindow:0.#} 秒内没开口");
                    break;
            }
            return output;
        }

        public IReadOnlyList<VoiceAction> OnAckFinished(float t)
        {
            output.Clear();
            if (State == VoiceSessionState.Ack) AckDone(t, "应答播完，开始聆听");
            return output;
        }

        // command：剥完唤醒词的指令文字；null = ASR 失败
        public IReadOnlyList<VoiceAction> OnTranscript(float t, string command)
        {
            output.Clear();
            if (State != VoiceSessionState.Processing) return output;   // 中止后才回来的转写
            string c = command?.Trim();
            if (fromFollowUp)
            {
                // 追问没有唤醒词把关：噪音转写出的空串或单字不能当成一句话发给她
                if (string.IsNullOrEmpty(c) || c.Length < 2)
                {
                    GoIdle(command == null ? "追问转写失败，静默丢弃" : $"追问转写「{c}」不足 2 字，静默丢弃");
                    return output;
                }
            }
            else if (string.IsNullOrEmpty(c))
            {
                GoIdle(command == null ? "转写失败" : "转写里只有唤醒词");
                output.Add(VoiceAction.SayNotHeard);   // 让主播知道要重说，而不是以为没唤醒
                return output;
            }
            PendingCommand = c;
            output.Add(VoiceAction.Inject);
            // 只放行她的回答（此刻之后入队的）；会话前积压的弹幕若先播，会被误当成「她在回答」
            if (hold == HoldLevel.Full) { output.Add(VoiceAction.ReleaseNewSpeech); hold = HoldLevel.Backlog; }
            sawTalking = false;
            Enter(VoiceSessionState.AwaitReply, t, $"注入「{c}」，等她回应");
            return output;
        }

        // 停播、菜单打开、监听器停用：丢弃会话并恢复一切副作用
        public IReadOnlyList<VoiceAction> Abort()
        {
            output.Clear();
            if (State != VoiceSessionState.Idle) GoIdle("中止（停播/菜单/停用）");
            else GoIdle(null);
            PendingCommand = null;
            return output;
        }

        void AckDone(float t, string reason)
        {
            output.Add(VoiceAction.StartCaptureAfterAck);
            capturing = true;
            EnterListening(t, false, false, reason);
        }

        void Submit(float t, string reason)
        {
            output.Add(VoiceAction.SubmitCapture);   // 取出即停止录音，不再单独发 StopCapture
            capturing = false;
            Enter(VoiceSessionState.Processing, t, reason);
        }

        void EnterListening(float t, bool alreadyHeard, bool followUp, string reason)
        {
            heard = alreadyHeard;
            lastVoicedAt = t;
            fromFollowUp = followUp;
            Enter(VoiceSessionState.Listening, t, reason);
        }

        void EnterFollowUp(float t)
        {
            HoldFull();
            onsetAccum = 0f;
            Enter(VoiceSessionState.FollowUp, t, "她说完了，开追问窗口");
        }

        void HoldFull()
        {
            if (hold != HoldLevel.Full) { output.Add(VoiceAction.HoldSpeech); hold = HoldLevel.Full; }
        }

        // reason 为 null：本来就在待机，不记转换
        void GoIdle(string reason)
        {
            if (capturing) { output.Add(VoiceAction.StopCapture); capturing = false; }
            if (ducked) { output.Add(VoiceAction.UnduckMusic); ducked = false; }
            if (hold != HoldLevel.None) { output.Add(VoiceAction.ReleaseSpeech); hold = HoldLevel.None; }
            if (reason != null) LastTransition = $"{State}→Idle：{reason}";
            State = VoiceSessionState.Idle;
        }

        void Enter(VoiceSessionState s, float t, string reason)
        {
            LastTransition = $"{State}→{s}：{reason}";
            State = s;
            enteredAt = t;
            lastT = t;
            hasLastT = true;
        }
    }
}
