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
                // 合规：AI 输出含敏感词直接不播（宁可不说不能说错）
                if (!ContentFilter.IsSafe(reply))
                {
                    Debug.LogWarning("[DanmakuAI] Reply blocked by content filter");
                    return;
                }

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
