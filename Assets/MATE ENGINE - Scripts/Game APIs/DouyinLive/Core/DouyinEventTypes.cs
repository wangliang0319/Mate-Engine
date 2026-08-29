using System;

namespace DouyinLive
{
    // DouyinBarrageGrab PackMsgType
    public enum DouyinMsgType
    {
        None = 0,
        Chat = 1,       // 弹幕
        Like = 2,       // 点赞
        Enter = 3,      // 进入直播间
        Follow = 4,     // 关注
        Gift = 5,       // 礼物
        Stats = 6,      // 直播间统计
        FansClub = 7,   // 粉丝团
        Share = 8       // 分享
    }

    // 统一后的主线程事件。刻意不带任何序列化特性：本类型要放在
    // MateEngine.DouyinLive.Core 里供单元测试使用，Core 保持零外部依赖。
    // 从线格式 DouyinMsg 的转换见 DouyinEventFactory（在 Assembly-CSharp 里）。
    public class DouyinEvent
    {
        public DouyinMsgType Type;
        public long MsgId;
        public string UserId;      // SecUid 优先，退化用 Id
        public string Nickname;
        public string Content;
        public int LikeCount;
        public string GiftName;
        public long GiftId;
        public int GiftCount;
        public int DiamondCount;
        public DateTime ReceivedAt;
    }
}
