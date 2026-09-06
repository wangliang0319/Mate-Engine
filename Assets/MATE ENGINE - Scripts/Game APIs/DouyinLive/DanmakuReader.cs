using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DouyinLive
{
    // 游戏陪播模式的念弹幕服务：指令弹幕已被触发层/RewardService 消费掉，
    // 剩下没被任何路径接住的弹幕由角色带昵称念出来，主播听声人工回复。
    // 不调 LLM、不走 TTS 之外任何逻辑——这一层的价值就是「主播在游戏里不用盯弹幕区」。
    public class DanmakuReader
    {
        public SpeechPipeline Speech;
        public float Interval = 5f;      // 两条念读的最小间隔

        class PendingDanmaku { public string User; public string Text; public float At; }

        readonly List<PendingDanmaku> queue = new List<PendingDanmaku>();
        readonly Queue<string> recentContents = new Queue<string>();
        readonly HashSet<string> recentSet = new HashSet<string>();
        const int QueueLimit = 3;        // 念一条要几秒，积压多了必然过期，干脆少存

        float lastReadAt = -999f;

        static readonly Regex EmojiOnly = new Regex(@"^[\s\p{So}\p{Sk}\[\]【】]+$", RegexOptions.Compiled);

        public int ReadCount { get; private set; }

        public void OnDanmaku(DouyinEvent ev)
        {
            if (ev.Type != DouyinMsgType.Chat) return;
            var text = (ev.Content ?? "").Trim();
            if (text.Length < 2) return;          // 单字弹幕念出来没信息量还占队列
            if (EmojiOnly.IsMatch(text)) return;  // 纯表情没法念

            // 60 秒内同文弹幕只念第一条（跟 DanmakuAIService 同款去重），
            // 不然刷屏弹幕会把队列名额全吃掉
            if (recentSet.Contains(text)) return;
            recentSet.Add(text);
            recentContents.Enqueue(text);
            while (recentContents.Count > 30) recentSet.Remove(recentContents.Dequeue());

            if (queue.Count >= QueueLimit)
                queue.RemoveAt(0);    // 挤掉最旧的：最新的弹幕最值得念
            queue.Add(new PendingDanmaku
            {
                User = string.IsNullOrEmpty(ev.Nickname) ? "观众" : ev.Nickname,
                Text = text,
                At = Time.unscaledTime
            });
        }

        // 主线程每帧调用
        public void Tick()
        {
            if (Speech == null) return;
            if (Time.unscaledTime - lastReadAt < Interval) return;

            // 过期丢弃（念出来已经没有时效性）
            queue.RemoveAll(q => Time.unscaledTime - q.At > 45f);
            if (queue.Count == 0) return;

            var item = queue[0];
            queue.RemoveAt(0);

            lastReadAt = Time.unscaledTime;
            ReadCount++;
            // LikeThanks 优先级：礼物感谢(P0)/指令播报(P1)能插队到念读前面；
            // 且同优先级只保留最新，弹幕洪峰时积压的旧念读自动被挤掉
            Speech.Enqueue($"{item.User}说：{item.Text}", SpeechPipeline.Priority.LikeThanks, 30f);
        }

        public void ResetSession()
        {
            queue.Clear();
            recentContents.Clear();
            recentSet.Clear();
            ReadCount = 0;
        }
    }
}
