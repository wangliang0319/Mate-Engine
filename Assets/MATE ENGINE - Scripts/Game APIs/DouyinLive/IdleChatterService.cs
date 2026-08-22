using System;
using System.Collections.Generic;
using UnityEngine;

namespace DouyinLive
{
    // 冷场暖场：直播间一段时间无互动时，主动说话活跃气氛
    // （求点赞/求关注/闲聊/才艺预告轮换）；长时间冷场则自动唱歌跳舞。
    public class IdleChatterService
    {
        public SpeechPipeline Speech;
        public DanmakuAIService AI;          // 可用时让大模型生成暖场词，更自然
        public SongService Song;             // 冷场自动唱歌
        public bool Enabled = true;
        public float IdleThreshold = 90f;    // 无互动多少秒算冷场
        public float MinInterval = 60f;      // 两次暖场最小间隔
        public float MaxInterval = 150f;     // 实际间隔在 Min~Max 随机

        // 冷场自动唱歌：冷场持续更久后从歌单随机来一首（歌单在 settings.json 配置）
        public bool AutoSongEnabled = true;
        public float AutoSongIdleThreshold = 300f;   // 冷场超5分钟自动唱
        public float AutoSongMinInterval = 600f;     // 两首自动歌最少隔10分钟
        public List<string> SongList = new List<string>();

        float lastInteractionAt;
        float lastChatterAt;
        float lastAutoSongAt;
        float nextIntervalJitter;
        int lastCategory = -1;
        readonly System.Random rng = new System.Random();

        // 分类模板池：轮换类别，同类内随机
        static readonly string[][] Pools =
        {
            new[] { // 求点赞
                "喜欢我的家人们，帮我点点小红心呀，你们的赞就是我的动力！",
                "听说点赞的宝宝今天都会有好运哦，动动小手试试嘛~",
                "小红心快到碗里来！让我看看今天能不能破纪录！",
            },
            new[] { // 求关注
                "新来的宝宝点个关注再走嘛，不然下次就找不到我啦~",
                "关注主播不迷路，我每天都在这里等你们哦！",
                "偷偷说一句，现在关注的都是我最爱的宝贝~",
            },
            new[] { // 才艺引导
                "想看我跳舞的话，发弹幕点舞就可以啦，我可是会很多舞的哦！",
                "无聊的话可以点歌呀，发 点歌加歌名，我唱跳给你们看！",
                "今天还没人点舞呢，人家准备了好久的说…",
            },
            new[] { // 闲聊拉近
                "大家今天过得怎么样呀？开心的事情可以发弹幕分享哦~",
                "屏幕前的你在忙什么呢？陪我聊聊天嘛~",
                "有人在看我吗？在的话扣个1让我看到你呀！",
            },
        };

        public void NotifyInteraction()
        {
            lastInteractionAt = Time.unscaledTime;
        }

        public void Tick()
        {
            if (!Enabled || Speech == null) return;
            float now = Time.unscaledTime;
            if (lastInteractionAt <= 0f) lastInteractionAt = now;
            if (lastChatterAt <= 0f) { lastChatterAt = now; RollJitter(); }
            if (lastAutoSongAt <= 0f) lastAutoSongAt = now;

            float idleFor = now - lastInteractionAt;

            // 深度冷场：自动唱一首（歌本身会带跳舞）；歌单为空则跳过
            if (AutoSongEnabled && Song != null && !Song.IsPlaying &&
                SongList != null && SongList.Count > 0 &&
                idleFor >= AutoSongIdleThreshold &&
                now - lastAutoSongAt >= AutoSongMinInterval &&
                !Speech.IsSpeaking && Speech.QueueCount == 0)
            {
                lastAutoSongAt = now;
                lastChatterAt = now;   // 唱歌也算一次暖场，避免紧跟着说话
                string pick = SongList[rng.Next(SongList.Count)];
                Speech.Enqueue($"好像有点安静呢，那我来给大家唱一首 {pick} 吧~", SpeechPipeline.Priority.LikeThanks, 30f);
                Song.RequestSong(pick, "我自己");
                return;
            }

            // 普通冷场：说暖场话
            if (idleFor < IdleThreshold) return;
            if (now - lastChatterAt < MinInterval + nextIntervalJitter) return;
            if (Speech.IsSpeaking || Speech.QueueCount > 0) return;
            if (Song != null && Song.IsPlaying) return;   // 唱歌中不插嘴

            lastChatterAt = now;
            RollJitter();

            // 轮换类别（不与上次重复）
            int cat = rng.Next(Pools.Length - 1);
            if (cat >= lastCategory) cat++;
            lastCategory = cat;
            var pool = Pools[cat];
            Speech.Enqueue(pool[rng.Next(pool.Length)], SpeechPipeline.Priority.LikeThanks, 30f);
        }

        void RollJitter()
        {
            nextIntervalJitter = (float)rng.NextDouble() * Mathf.Max(0f, MaxInterval - MinInterval);
        }

        public void ResetSession()
        {
            lastInteractionAt = Time.unscaledTime;
            lastChatterAt = Time.unscaledTime;
            lastAutoSongAt = Time.unscaledTime;
            lastCategory = -1;
            RollJitter();
        }
    }
}
