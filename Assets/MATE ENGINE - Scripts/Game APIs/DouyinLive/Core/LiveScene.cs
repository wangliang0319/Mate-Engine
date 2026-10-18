using System;

namespace DouyinLive
{
    // 直播场景。编号与旧 douyinChatMode 的 0/1 含义一致，老配置可直接迁移。
    public enum LiveScene { Solo = 0, Game = 1, Duo = 2 }

    public enum DanmakuHandling
    {
        AiVerbose,   // AI 详细回复并引导观众继续聊（独播）
        AiBrief,     // AI 简短回复，把话头留给主播（搭档）
        Read,        // 带昵称念出来，主播人工回复（陪播）
        Ignore       // 不处理（AI 回复总开关关闭，或安静中）
    }

    // 现有的总开关：场景只能在它们允许的范围内收紧，不能打开总开关关掉的东西
    public struct LiveSwitches
    {
        public bool AiReply;
        public bool IdleChatter;
        public float IdleThreshold;
        public bool AutoSong;
        public bool AutoDance;
    }

    // 此刻实际生效的行为，DouyinLiveManager 据此下发给各服务
    public struct LiveBehavior
    {
        public DanmakuHandling Danmaku;
        public bool IdleChatter;
        public float IdleThreshold;
        public bool AutoSong;
        public bool AutoDance;
        public bool RespondToAudience;   // false = 观众侧只记账不响应（礼物只说答谢语）
        public bool MuteLowPriority;     // true = 欢迎/点赞答谢/报时等低优先级播报不开口
    }

    // 场景规则集中在这里：以前散在 douyinChatMode、暖场、自动唱跳几个互不相干的开关里，
    // 切到游戏陪播后暖场和自动唱歌照常触发。纯函数，场景表每一格都有单元测试。
    public static class LiveSceneResolver
    {
        public const float DuoMinIdleThreshold = 180f;   // 搭档主持时主播在说话，暖场别太勤

        public static LiveBehavior Resolve(LiveScene scene, bool quiet, LiveSwitches s)
        {
            var b = new LiveBehavior { IdleThreshold = s.IdleThreshold };
            if (quiet)
            {
                // 安静：只保留礼物答谢与主播语音响应（后两者不经过这里）
                b.Danmaku = DanmakuHandling.Ignore;
                b.MuteLowPriority = true;
                return b;
            }

            switch (scene)
            {
                case LiveScene.Game:
                    // 念弹幕不经过大模型，和 AI 回复总开关无关；暖场和自动唱跳会打断打游戏
                    b.Danmaku = DanmakuHandling.Read;
                    break;
                case LiveScene.Duo:
                    b.Danmaku = s.AiReply ? DanmakuHandling.AiBrief : DanmakuHandling.Ignore;
                    b.IdleChatter = s.IdleChatter;
                    b.IdleThreshold = Math.Max(s.IdleThreshold, DuoMinIdleThreshold);
                    break;   // 什么时候表演由主播决定，不自动唱跳
                default:
                    b.Danmaku = s.AiReply ? DanmakuHandling.AiVerbose : DanmakuHandling.Ignore;
                    b.IdleChatter = s.IdleChatter;
                    b.AutoSong = s.AutoSong;
                    b.AutoDance = s.AutoDance;
                    break;
            }
            b.RespondToAudience = true;
            return b;
        }
    }

    public static class LiveSceneMigration
    {
        // storedScene < 0 表示配置里还没有 douyinScene：从旧 douyinChatMode 推导（1=陪播，其余=独播），
        // 主播原来用的模式原样延续。越界值回退独播，outOfRange 让调用方写警告日志。
        public static LiveScene Resolve(int storedScene, int legacyChatMode, out bool outOfRange)
        {
            outOfRange = false;
            if (storedScene < 0) return legacyChatMode == 1 ? LiveScene.Game : LiveScene.Solo;
            if (storedScene > 2) { outOfRange = true; return LiveScene.Solo; }
            return (LiveScene)storedScene;
        }
    }
}
