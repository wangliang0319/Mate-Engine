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

        // 深度冷场自动跳舞：和唱歌交替，避免连着唱好几首
        public DanceDirector Dance;
        public bool AutoDanceEnabled = true;

        float lastInteractionAt;
        float lastChatterAt;
        float lastAutoSongAt;
        bool autoSongClockStarted;   // 首唱只受冷场阈值限制，MinInterval 从第一首后才生效
        bool lastAutoWasSong;        // 唱/跳交替用
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

            // 深度冷场：唱一首或跳一支，两者交替，避免连着唱好几首。
            // 首次只看冷场时长；MinInterval 从第一次之后才计。
            // 唱歌自带跳舞，不能在唱歌途中再起一支自动舞——这条挡在最外层而不是塞进
            // songReady，因为 Song 为 null（未接歌曲服务）不该连带把跳舞路也堵死。
            bool notSinging = Song == null || !Song.IsPlaying;
            bool songReady = Song != null && SongList != null && SongList.Count > 0;
            bool danceReady = AutoDanceEnabled && Dance != null && !Dance.Busy;
            bool wantSong = AutoSongEnabled && songReady;

            if (notSinging && (wantSong || danceReady) &&
                idleFor >= AutoSongIdleThreshold &&
                (!autoSongClockStarted || now - lastAutoSongAt >= AutoSongMinInterval) &&
                !Speech.IsSpeaking && Speech.QueueCount == 0)
            {
                // 上次唱过就这次跳；对应一侧不可用（开关关掉/歌单为空/舞跳到一半）时回退到另一侧
                bool wantDance = lastAutoWasSong && danceReady;
                if (!wantDance && !wantSong) wantDance = danceReady;

                if (wantDance && Dance.PlayRandom())
                {
                    autoSongClockStarted = true;
                    lastAutoSongAt = now;
                    lastChatterAt = now;   // 表演也算一次暖场，避免紧跟着说话
                    lastAutoWasSong = false;
                    Speech.Enqueue("好像有点安静呢，那我来给大家跳一支舞吧~",
                        SpeechPipeline.Priority.LikeThanks, 30f);
                    return;
                }

                if (wantSong)
                {
                    autoSongClockStarted = true;
                    lastAutoSongAt = now;
                    lastChatterAt = now;
                    lastAutoWasSong = true;
                    string pick = SongList[rng.Next(SongList.Count)];
                    Speech.Enqueue($"好像有点安静呢，那我来给大家唱一首 {pick} 吧~",
                        SpeechPipeline.Priority.LikeThanks, 30f);
                    Song.RequestSong(pick, null);   // null = 自动唱歌，不播报点歌提示
                    return;
                }
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
            autoSongClockStarted = false;
            lastAutoWasSong = false;
            lastCategory = -1;
            RollJitter();
        }
    }
}
