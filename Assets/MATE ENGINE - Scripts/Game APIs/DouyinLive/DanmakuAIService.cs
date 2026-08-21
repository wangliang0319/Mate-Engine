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

        public int RepliedCount { get; private set; }

        class PendingDanmaku { public string User; public string Text; public float At; public bool FromGifter; }

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
            "你是一个桌面宠物虚拟主播，正在抖音直播。观众会发弹幕，你要用活泼、简短、口语化的中文回应。" +
            "规则：回复必须精简，一般一句话，最多不超过30个字；不要使用表情符号、颜文字、markdown或任何特殊符号；" +
            "语气亲切自然，适合直接朗读出来；直接说内容，不要加引号或前缀。" +
            (string.IsNullOrEmpty(ExtraPersona) ? "" : ("你的人设补充：" + ExtraPersona));

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
                string reply = null;

                var backend = Cloud != null && Cloud.IsAvailable ? Cloud : null;
                if (backend != null)
                {
                    try { reply = await RunBackend(backend, userMsg); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[DanmakuAI] Cloud failed: " + ex.Message);
                    }
                }
                if (string.IsNullOrEmpty(reply) && FallbackToLocal && Local != null && Local.IsAvailable)
                {
                    try { reply = await RunBackend(Local, userMsg); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[DanmakuAI] Local failed: " + ex.Message);
                    }
                }
                if (string.IsNullOrEmpty(reply)) return;

                reply = Sanitize(reply);
                if (string.IsNullOrEmpty(reply)) return;

                history.Add(new ChatMsg("user", userMsg));
                history.Add(new ChatMsg("assistant", reply));
                while (history.Count > HistoryTurns * 2) history.RemoveAt(0);

                RepliedCount++;
                // 回主线程入队由 Speech.Enqueue 内部无 Unity API 依赖的部分保证线程安全：
                // Enqueue 仅操作 lock 保护的列表与 Time —— Time 必须主线程，因此这里派发回主线程
                MainThreadDispatcher.Post(() => Speech.Enqueue(reply, SpeechPipeline.Priority.AIReply, 45f));
            }
            finally { busy = false; }
        }

        async Task<string> RunBackend(IChatBackend backend, string userMsg)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RequestTimeout));
            return await backend.ChatAsync(SystemPrompt, history, userMsg, null, cts.Token);
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
