using System;
using System.Collections.Generic;
using UnityEngine;

namespace DouyinLive
{
    // 进房/关注/分享/粉丝团 → 模板欢迎语（P3，可合并播报）
    public class WelcomeService
    {
        public SpeechPipeline Speech;
        public float Cooldown = 8f;
        public bool Enabled = true;

        readonly HashSet<string> welcomedThisSession = new HashSet<string>();
        readonly List<string> pendingNames = new List<string>();
        float lastSpokenAt = -999f;

        static readonly string[] EnterTemplates = {
            "欢迎 {user} 来到直播间，点点关注，不迷路~",
            "{user} 来啦，欢迎欢迎，一起聊聊天~",
            "哇，{user} 来了，有问题随时提问~",
            "欢迎 {user}~ 来得正好，精彩马上开始~",
            "{user} 进来啦，喜欢的话点个关注呀~",
            "欢迎 {user}，刚来的可以扣个 1，让我看到你~",
            "{user} 来啦？想听什么歌可以直接点哦~",
            "欢迎 {user} 光临，宝子来得早不如来得巧~"
        };
        static readonly string[] FollowTemplates = {
            "谢谢 {user} 的关注，爱你哟~", "{user} 关注了我，太感动了！", "感谢 {user} 的关注，抱抱~"
        };
        static readonly string[] ShareTemplates = {
            "谢谢 {user} 帮我分享直播间！", "{user} 分享了直播间，笔芯~"
        };
        static readonly string[] FansClubTemplates = {
            "欢迎 {user} 加入粉丝团，我们是一家人啦！", "谢谢 {user} 加团，么么哒~"
        };

        readonly System.Random rng = new System.Random();

        public void OnEvent(DouyinEvent ev)
        {
            if (!Enabled || Speech == null) return;
            string name = string.IsNullOrEmpty(ev.Nickname) ? "朋友" : ev.Nickname;

            switch (ev.Type)
            {
                case DouyinMsgType.Enter:
                    if (!welcomedThisSession.Add(ev.UserId)) return;
                    pendingNames.Add(name);
                    break;
                case DouyinMsgType.Follow:
                    Speech.Enqueue(Pick(FollowTemplates).Replace("{user}", name), SpeechPipeline.Priority.Milestone, 30f);
                    return;
                case DouyinMsgType.Share:
                    Speech.Enqueue(Pick(ShareTemplates).Replace("{user}", name), SpeechPipeline.Priority.Milestone, 30f);
                    return;
                case DouyinMsgType.FansClub:
                    Speech.Enqueue(Pick(FansClubTemplates).Replace("{user}", name), SpeechPipeline.Priority.Milestone, 30f);
                    return;
                default:
                    return;
            }
        }

        // 每帧调用：冷却到了就把积累的进房观众合并成一条欢迎
        public void Tick()
        {
            if (!Enabled || Speech == null || pendingNames.Count == 0) return;
            if (Time.unscaledTime - lastSpokenAt < Cooldown) return;

            string text;
            if (pendingNames.Count == 1)
                text = Pick(EnterTemplates).Replace("{user}", pendingNames[0]);
            else if (pendingNames.Count <= 3)
                text = Pick(EnterTemplates).Replace("{user}", string.Join("、", pendingNames));
            else
                text = $"欢迎 {pendingNames[0]}、{pendingNames[1]} 等 {pendingNames.Count} 位朋友来到直播间~";

            pendingNames.Clear();
            lastSpokenAt = Time.unscaledTime;
            Speech.Enqueue(text, SpeechPipeline.Priority.Welcome, 30f);
        }

        public void ResetSession()
        {
            welcomedThisSession.Clear();
            pendingNames.Clear();
        }

        string Pick(string[] arr) => arr[rng.Next(arr.Length)];
    }

    // 点赞聚合 → 阈值/里程碑致谢（P4/P2）
    public class LikeService
    {
        public SpeechPipeline Speech;
        public bool Enabled = true;
        public int Threshold = 100;

        long sessionTotal;
        long lastThankedAt;
        static readonly long[] Milestones = { 1000, 5000, 10000, 50000, 100000 };
        int nextMilestone;

        public long SessionTotal => sessionTotal;

        public void OnEvent(DouyinEvent ev)
        {
            if (ev.Type != DouyinMsgType.Like) return;
            sessionTotal += ev.LikeCount;
            if (!Enabled || Speech == null) return;

            if (nextMilestone < Milestones.Length && sessionTotal >= Milestones[nextMilestone])
            {
                Speech.Enqueue($"哇！点赞突破 {Milestones[nextMilestone]} 了，谢谢大家，爱你们！",
                    SpeechPipeline.Priority.Milestone, 60f);
                nextMilestone++;
                lastThankedAt = sessionTotal;
                return;
            }

            if (sessionTotal - lastThankedAt >= Threshold)
            {
                lastThankedAt = sessionTotal;
                Speech.Enqueue($"谢谢大家的 {sessionTotal} 个赞~", SpeechPipeline.Priority.LikeThanks, 20f);
            }
        }

        public void ResetSession()
        {
            sessionTotal = 0; lastThankedAt = 0; nextMilestone = 0;
        }
    }
}
