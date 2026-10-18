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
        ReleaseSpeech,         // 恢复播报队列
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
        readonly VoiceSessionTiming tm;
        readonly List<VoiceAction> output = new List<VoiceAction>();

        public VoiceSessionState State { get; private set; } = VoiceSessionState.Idle;
        public string PendingCommand { get; private set; }

        // 副作用账本：进待机时据此恢复，保证压低/暂停各只恢复一次
        bool ducked, held, capturing;
        bool heard, fromFollowUp, sawTalking;
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
                case VoiceSessionState.Listening:
                case VoiceSessionState.Processing:
                    return output;   // 正在听/正在处理：重复的唤醒词不重置会话
            }
            if (talking) output.Add(VoiceAction.InterruptSpeech);
            if (performing && !ducked) { output.Add(VoiceAction.DuckMusic); ducked = true; }
            if (!held) { output.Add(VoiceAction.HoldSpeech); held = true; }
            output.Add(VoiceAction.StartCapture);
            capturing = true;
            voicedAccum = 0f;
            fromFollowUp = false;
            PendingCommand = null;
            Enter(VoiceSessionState.WakePending, t);
            return output;
        }

        // 每帧调用。voiced = 这一帧的麦克风音频是否有声；talking = 她正在出声；performing = 在唱歌/跳舞。
        public IReadOnlyList<VoiceAction> Update(float t, bool voiced, bool talking, bool performing)
        {
            output.Clear();
            float dt = hasLastT ? Math.Max(0f, t - lastT) : 0f;
            lastT = t;
            hasLastT = true;

            switch (State)
            {
                case VoiceSessionState.WakePending:
                    if (voiced) voicedAccum += dt;
                    if (voicedAccum >= tm.OneBreathVoiced)
                        EnterListening(t, true, false);          // 一口气说完：录音不断，直接聆听
                    else if (t - enteredAt >= tm.PauseWindow)
                    {
                        output.Add(VoiceAction.StopCapture);       // 应答期间不录，免得录进她的声音
                        capturing = false;
                        output.Add(VoiceAction.PlayAck);
                        Enter(VoiceSessionState.Ack, t);
                    }
                    break;

                case VoiceSessionState.Ack:
                    if (t - enteredAt >= tm.AckSafety) AckDone(t);
                    break;

                case VoiceSessionState.Listening:
                    if (voiced) { heard = true; lastVoicedAt = t; }
                    if (heard && t - lastVoicedAt >= tm.EndSilence) Submit(t);
                    else if (t - enteredAt >= tm.MaxUtterance) { if (heard) Submit(t); else GoIdle(); }
                    else if (!heard && t - enteredAt >= tm.AckTimeout) GoIdle();
                    break;

                case VoiceSessionState.Processing:
                    if (t - enteredAt >= tm.ProcessingSafety) GoIdle();
                    break;

                case VoiceSessionState.AwaitReply:
                    if (performing) GoIdle();                       // 表演完多半在干别的，不开追问
                    else if (talking) sawTalking = true;
                    else if (sawTalking)
                    {
                        if (tm.FollowUpWindow > 0f) EnterFollowUp(t);
                        else GoIdle();
                    }
                    else if (t - enteredAt >= tm.ReplyTimeout) GoIdle();
                    break;

                case VoiceSessionState.FollowUp:
                    if (performing) { GoIdle(); break; }
                    if (voiced) onsetAccum += dt; else onsetAccum = 0f;
                    if (onsetAccum >= tm.FollowUpOnset)
                    {
                        output.Add(VoiceAction.StartCapture);
                        capturing = true;
                        EnterListening(t, true, true);
                    }
                    else if (t - enteredAt >= tm.FollowUpWindow) GoIdle();
                    break;
            }
            return output;
        }

        public IReadOnlyList<VoiceAction> OnAckFinished(float t)
        {
            output.Clear();
            if (State == VoiceSessionState.Ack) AckDone(t);
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
                if (string.IsNullOrEmpty(c) || c.Length < 2) { GoIdle(); return output; }
            }
            else if (string.IsNullOrEmpty(c))
            {
                GoIdle();
                output.Add(VoiceAction.SayNotHeard);   // 让主播知道要重说，而不是以为没唤醒
                return output;
            }
            PendingCommand = c;
            output.Add(VoiceAction.Inject);
            if (held) { output.Add(VoiceAction.ReleaseSpeech); held = false; }   // 放开让她回答
            sawTalking = false;
            Enter(VoiceSessionState.AwaitReply, t);
            return output;
        }

        // 停播、菜单打开、监听器停用：丢弃会话并恢复一切副作用
        public IReadOnlyList<VoiceAction> Abort()
        {
            output.Clear();
            GoIdle();
            PendingCommand = null;
            return output;
        }

        void AckDone(float t)
        {
            output.Add(VoiceAction.StartCaptureAfterAck);
            capturing = true;
            EnterListening(t, false, false);
        }

        void Submit(float t)
        {
            output.Add(VoiceAction.SubmitCapture);   // 取出即停止录音，不再单独发 StopCapture
            capturing = false;
            Enter(VoiceSessionState.Processing, t);
        }

        void EnterListening(float t, bool alreadyHeard, bool followUp)
        {
            heard = alreadyHeard;
            lastVoicedAt = t;
            fromFollowUp = followUp;
            Enter(VoiceSessionState.Listening, t);
        }

        void EnterFollowUp(float t)
        {
            if (!held) { output.Add(VoiceAction.HoldSpeech); held = true; }
            onsetAccum = 0f;
            Enter(VoiceSessionState.FollowUp, t);
        }

        void GoIdle()
        {
            if (capturing) { output.Add(VoiceAction.StopCapture); capturing = false; }
            if (ducked) { output.Add(VoiceAction.UnduckMusic); ducked = false; }
            if (held) { output.Add(VoiceAction.ReleaseSpeech); held = false; }
            State = VoiceSessionState.Idle;
        }

        void Enter(VoiceSessionState s, float t)
        {
            State = s;
            enteredAt = t;
            lastT = t;
            hasLastT = true;
        }
    }
}
