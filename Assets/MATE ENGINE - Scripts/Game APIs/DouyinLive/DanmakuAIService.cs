using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DouyinLive
{
    // 弹幕 → 过滤/限流 → 云端LLM（失败降级本地）→ 语音管线（P1）
    public class DanmakuAIService
    {
        public SpeechPipeline Speech;
        public bool Enabled = true;
        public float MinInterval = 8f;       // 两次回复最小间隔
        public int QueueLimit = 5;
        public float RequestTimeout = 15f;
        public string ExtraPersona = "";     // 用户自定义人设追加

        public IChatBackend Cloud;
        public IChatBackend Local;
        public bool FallbackToLocal = true;
        public PersonaCard Persona;          // 人设卡（douyin_persona.json）
        public AudienceMemory Audience;      // 观众记忆
        public RoomContext Room;             // 直播间语境

        public int RepliedCount { get; private set; }

        class PendingDanmaku { public string User; public string UserId; public string Text; public float At; public bool FromGifter; }

        readonly List<PendingDanmaku> queue = new List<PendingDanmaku>();
        readonly List<ChatMsg> history = new List<ChatMsg>();
        readonly Queue<string> recentContents = new Queue<string>();
        readonly HashSet<string> recentSet = new HashSet<string>();
        readonly HashSet<string> gifters = new HashSet<string>();
        const int HistoryTurns = 6;

        float lastReplyAt = -999f;
        bool busy;

        static readonly Regex EmojiOnly = new Regex(@"^[\s\p{So}\p{Sk}\[\]【】]+$", RegexOptions.Compiled);

        string SystemPrompt =>
            (Persona != null ? Persona.ToPromptSection() : "你是一个桌面宠物虚拟主播。") +
            "你正在抖音直播，观众会发弹幕，你要活泼简短地回应。" +
            "规则：回复必须精简，一般一句话，最多不超过30个字；不要使用表情符号、颜文字、markdown或任何特殊符号；" +
            "适合直接朗读出来；直接说内容，不要加引号或前缀；不聊政治宗教等敏感话题。" +
            (string.IsNullOrEmpty(ExtraPersona) ? "" : ("补充设定：" + ExtraPersona));

        // 快捷反应：高频弹幕不走 LLM，秒回且省钱
        static readonly (Regex pattern, string[] replies)[] QuickReplies =
        {
            (new Regex(@"^6+$|^666+", RegexOptions.Compiled), new[]
                { "谢谢宝子的666，我会更努力的~", "666收到，你们也很棒！", "嘿嘿，谢谢夸奖~" }),
            (new Regex(@"^哈+$|^(哈哈)+哈*$|^233+$", RegexOptions.Compiled), new[]
                { "被你们笑得我也想笑了~", "开心就好呀哈哈~", "笑什么呢，带我一个~" }),
            (new Regex(@"^(主播|宝宝|小姐姐)?(你好|您好|hi|hello|哈喽|嗨)$", RegexOptions.Compiled | RegexOptions.IgnoreCase), new[]
                { "你好呀，欢迎欢迎~", "哈喽哈喽，很高兴见到你~", "嗨，来啦就别走啦~" }),
            (new Regex(@"^(晚安|睡了|下了|溜了|拜拜|再见|88)$", RegexOptions.Compiled), new[]
                { "晚安好梦，明天再来看我哦~", "拜拜，路上小心，记得想我~", "下次见啦，爱你~" }),
            (new Regex(@"^在吗?[?？]?$", RegexOptions.Compiled), new[]
                { "在的在的，一直都在~", "我在呀，怎么啦~" }),
            (new Regex(@"^(好可爱|太可爱了|可爱)$", RegexOptions.Compiled), new[]
                { "嘿嘿，人家会害羞的啦~", "你眼光真好~", "可爱担当就是我！" }),
        };
        float lastQuickReplyAt = -999f;

        public void MarkGifter(string userId)
        {
            if (!string.IsNullOrEmpty(userId)) gifters.Add(userId);
        }

        public void OnDanmaku(DouyinEvent ev)
        {
            if (!Enabled || ev.Type != DouyinMsgType.Chat) return;
            var text = (ev.Content ?? "").Trim();
            if (text.Length < 2) return;
            if (EmojiOnly.IsMatch(text)) return;

            // 快捷反应：高频弹幕直接秒回（带20秒冷却防复读）
            foreach (var (pattern, replies) in QuickReplies)
            {
                if (!pattern.IsMatch(text)) continue;
                if (Time.unscaledTime - lastQuickReplyAt < 20f) return;  // 冷却中直接吞掉
                lastQuickReplyAt = Time.unscaledTime;
                Speech?.Enqueue(replies[UnityEngine.Random.Range(0, replies.Length)],
                    SpeechPipeline.Priority.AIReply, 20f);
                return;
            }

            // 敏感弹幕不接茬（不回复即不给节奏）
            if (!ContentFilter.IsSafe(text)) return;

            // 60秒重复内容不回
            if (recentSet.Contains(text)) return;
            recentSet.Add(text);
            recentContents.Enqueue(text);
            while (recentContents.Count > 30) recentSet.Remove(recentContents.Dequeue());

            bool vip = gifters.Contains(ev.UserId);
            lock (queue)
            {
                if (queue.Count >= QueueLimit)
                {
                    // 满了：礼物用户挤掉最老的普通弹幕，普通弹幕直接丢
                    if (!vip) return;
                    int drop = queue.FindIndex(q => !q.FromGifter);
                    queue.RemoveAt(drop >= 0 ? drop : 0);
                }
                queue.Add(new PendingDanmaku
                {
                    User = string.IsNullOrEmpty(ev.Nickname) ? "观众" : ev.Nickname,
                    UserId = ev.UserId,
                    Text = text,
                    At = Time.unscaledTime,
                    FromGifter = vip
                });
            }
        }

        // 主线程每帧调用
        public void Tick()
        {
            if (!Enabled || busy || Speech == null) return;
            if (Time.unscaledTime - lastReplyAt < MinInterval) return;

            PendingDanmaku item = null;
            lock (queue)
            {
                if (queue.Count == 0) return;
                // 礼物用户优先，同级先进先出；过期(45s)丢弃
                queue.RemoveAll(q => Time.unscaledTime - q.At > 45f);
                foreach (var q in queue)
                    if (item == null || (q.FromGifter && !item.FromGifter)) item = q;
                if (item == null) return;
                queue.Remove(item);
            }

            busy = true;
            lastReplyAt = Time.unscaledTime;
            _ = ReplyAsync(item);
        }

        async Task ReplyAsync(PendingDanmaku item)
        {
            try
            {
                string userMsg = item.User + " 说：" + item.Text;

                // 动态上下文：直播间实况 + 观众画像（老朋友/大哥识别）
                string dynamicContext = "";
                if (Room != null) dynamicContext += Room.BuildPromptSection();
                if (Audience != null) dynamicContext += Audience.DescribeForPrompt(item.UserId);
                string systemPrompt = SystemPrompt +
                    (dynamicContext.Length > 0 ? " " + dynamicContext : "");

                // 句级流水线：LLM 边流式输出边切句入流，首句即开始 TTS 合成播报，
                // 首句开口延迟 ≈ LLM 首句时间 + 单句合成时间（原来要等整段生成完）。
                var full = new StringBuilder();     // 完整回复（入历史）
                var carry = new StringBuilder();    // 未成句缓冲
                SpeechPipeline.SpeechStream stream = null;
                bool emittedAny = false;
                object gate = new object();

                void EmitSentences(bool flush)
                {
                    // gate 已持有。从 carry 切出完整句子逐句入流
                    var buf = carry.ToString();
                    int start = 0;
                    for (int i = 0; i < buf.Length; i++)
                    {
                        char c = buf[i];
                        bool boundary = c == '。' || c == '！' || c == '？' || c == '!' || c == '?' ||
                                        c == '；' || c == ';' || c == '\n';
                        if (boundary && i - start >= 3)
                        {
                            EmitOne(buf.Substring(start, i - start + 1));
                            start = i + 1;
                        }
                    }
                    carry.Clear();
                    if (start < buf.Length)
                    {
                        if (flush) EmitOne(buf.Substring(start));
                        else carry.Append(buf, start, buf.Length - start);
                    }
                }

                void EmitOne(string raw)
                {
                    var clean = Sanitize(raw);
                    if (string.IsNullOrEmpty(clean)) return;
                    if (!ContentFilter.IsSafe(clean))
                    {
                        Debug.LogWarning("[DanmakuAI] Sentence blocked by content filter");
                        return;
                    }
                    if (stream == null)
                    {
                        var s = new SpeechPipeline.SpeechStream();
                        stream = s;
                        MainThreadDispatcher.Post(() =>
                            Speech.EnqueueStream(s, SpeechPipeline.Priority.AIReply, 45f));
                    }
                    stream.Append(clean);
                    emittedAny = true;
                }

                void OnDelta(string delta)
                {
                    if (string.IsNullOrEmpty(delta)) return;
                    lock (gate)
                    {
                        full.Append(delta);
                        carry.Append(delta);
                        EmitSentences(false);
                    }
                }

                string reply = null;
                var backend = Cloud != null && Cloud.IsAvailable ? Cloud : null;
                if (backend != null)
                {
                    try { reply = await RunBackend(backend, systemPrompt, userMsg, OnDelta); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[DanmakuAI] Cloud failed: " + ex.Message);
                    }
                }
                // 云端已开口的话不再走本地兜底（避免前后音色/人格断裂）
                if (string.IsNullOrEmpty(reply) && !emittedAny &&
                    FallbackToLocal && Local != null && Local.IsAvailable)
                {
                    try { reply = await RunBackend(Local, systemPrompt, userMsg, OnDelta); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[DanmakuAI] Local failed: " + ex.Message);
                    }
                }

                lock (gate)
                {
                    EmitSentences(true);      // 冲出残句
                    stream?.Complete();
                }

                string fullReply = Sanitize(!string.IsNullOrEmpty(reply) ? reply : full.ToString());
                if (string.IsNullOrEmpty(fullReply) || !emittedAny) return;

                history.Add(new ChatMsg("user", userMsg));
                history.Add(new ChatMsg("assistant", fullReply));
                while (history.Count > HistoryTurns * 2) history.RemoveAt(0);

                RepliedCount++;
            }
            finally { busy = false; }
        }

        async Task<string> RunBackend(IChatBackend backend, string systemPrompt, string userMsg, Action<string> onDelta)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RequestTimeout));
            return await backend.ChatAsync(systemPrompt, history, userMsg, onDelta, cts.Token);
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim().Trim('"', '“', '”', '\'');
            // 移除 emoji 与 markdown 痕迹，保证适合朗读
            s = Regex.Replace(s, @"[\p{So}\p{Sk}*#`_~\[\]]", "");
            if (s.Length > 60) s = s.Substring(0, 60);
            return s.Trim();
        }

        public void ResetSession()
        {
            lock (queue) queue.Clear();
            history.Clear();
            recentContents.Clear();
            recentSet.Clear();
            gifters.Clear();
            RepliedCount = 0;
        }
    }
}
