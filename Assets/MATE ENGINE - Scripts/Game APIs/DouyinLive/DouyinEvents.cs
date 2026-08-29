using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace DouyinLive
{
    // 外层信封: { "Type": <int>, "Data": "<内层JSON字符串>" }
    [Serializable]
    public class DouyinEnvelope
    {
        public int Type;
        public string Data;
    }

    [Serializable]
    public class DouyinUser
    {
        public long Id;
        public string ShortId;
        public string DisplayId;
        public string Nickname;
        public int Level;
        public int PayLevel;
        public string SecUid;
        public int FansClubLevel;
        public bool FollowingStatus;
        public string HeadImgUrl;
    }

    // 内层消息通用字段（各类型消息的超集，缺失字段保持默认值）
    [Serializable]
    public class DouyinMsg
    {
        public long MsgId;
        public DouyinUser User;
        public string Content;
        public long RoomId;

        // Like
        public int Count;
        public long Total;

        // Gift
        public long GiftId;
        public string GiftName;
        public int GiftCount;      // 本次数量
        public int GroupCount;     // 连击组数量
        public int RepeatCount;    // 连击次数
        public int DiamondCount;   // 抖币单价

        // Stats
        public int OnlineUserCount;
        public long TotalUserCount;

        // Enter / Share 等的附带字段容忍未知
        [JsonExtensionData]
        public IDictionary<string, object> Extra;
    }

    // DouyinMsg 带 [JsonExtensionData]，依赖 Newtonsoft，所以转换逻辑留在
    // Assembly-CSharp 这边，Core 只放不依赖任何第三方库的 DouyinEvent。
    public static class DouyinEventFactory
    {
        public static DouyinEvent From(DouyinMsgType type, DouyinMsg m)
        {
            if (m == null) return null;
            return new DouyinEvent
            {
                Type = type,
                MsgId = m.MsgId,
                UserId = !string.IsNullOrEmpty(m.User?.SecUid) ? m.User.SecUid : (m.User?.Id ?? 0).ToString(),
                Nickname = m.User?.Nickname ?? "",
                Content = m.Content ?? "",
                LikeCount = m.Count > 0 ? m.Count : 1,
                GiftName = m.GiftName ?? "",
                GiftId = m.GiftId,
                GiftCount = Math.Max(1, m.GiftCount > 0 ? m.GiftCount : m.RepeatCount),
                DiamondCount = m.DiamondCount,
                ReceivedAt = DateTime.UtcNow
            };
        }
    }
}
