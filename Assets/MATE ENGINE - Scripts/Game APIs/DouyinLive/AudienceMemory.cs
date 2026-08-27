using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace DouyinLive
{
    // 观众记忆：按 SecUid 记住常客（来访次数/礼物总额/最近弹幕），持久化到 douyin_audience.json。
    // 用途：老观众专属欢迎、大哥识别、AI 回复时注入观众画像。
    public class AudienceMemory
    {
        [Serializable]
        public class Viewer
        {
            public string nickname = "";
            public int visits;               // 来访场次
            public long lastSeenUnix;
            public int giftDiamonds;         // 累计礼物抖币
            public string lastMessage = "";  // 最近一条弹幕
        }

        Dictionary<string, Viewer> viewers = new Dictionary<string, Viewer>();
        bool dirty;
        float lastSaveAt;
        const int MaxViewers = 800;

        static string FilePath => Path.Combine(Application.persistentDataPath, "douyin_audience.json");

        public void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    viewers = JsonConvert.DeserializeObject<Dictionary<string, Viewer>>(File.ReadAllText(FilePath))
                              ?? new Dictionary<string, Viewer>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AudienceMemory] load failed: " + ex.Message);
                viewers = new Dictionary<string, Viewer>();
            }
        }

        public void SaveIfDirty(bool force = false)
        {
            if (!dirty && !force) return;
            if (!force && Time.unscaledTime - lastSaveAt < 60f) return;   // 至多每分钟落盘一次
            try
            {
                // 超量时淘汰最久未见的观众
                if (viewers.Count > MaxViewers)
                    viewers = viewers.OrderByDescending(kv => kv.Value.lastSeenUnix)
                                     .Take(MaxViewers)
                                     .ToDictionary(kv => kv.Key, kv => kv.Value);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(viewers));
                dirty = false;
                lastSaveAt = Time.unscaledTime;
            }
            catch (Exception ex) { Debug.LogWarning("[AudienceMemory] save failed: " + ex.Message); }
        }

        public Viewer Get(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            viewers.TryGetValue(userId, out var v);
            return v;
        }

        Viewer GetOrCreate(string userId, string nickname)
        {
            if (!viewers.TryGetValue(userId, out var v))
            {
                v = new Viewer();
                viewers[userId] = v;
            }
            if (!string.IsNullOrEmpty(nickname)) v.nickname = nickname;
            v.lastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            dirty = true;
            return v;
        }

        public void RecordVisit(string userId, string nickname)
        {
            if (string.IsNullOrEmpty(userId)) return;
            GetOrCreate(userId, nickname).visits++;
        }

        public void RecordGift(string userId, string nickname, int diamonds)
        {
            if (string.IsNullOrEmpty(userId)) return;
            GetOrCreate(userId, nickname).giftDiamonds += Mathf.Max(0, diamonds);
        }

        public void RecordMessage(string userId, string nickname, string msg)
        {
            if (string.IsNullOrEmpty(userId)) return;
            var v = GetOrCreate(userId, nickname);
            if (!string.IsNullOrEmpty(msg))
                v.lastMessage = msg.Length > 40 ? msg.Substring(0, 40) : msg;
        }

        // 供 AI prompt 使用的一句话观众画像；生客返回空
        public string DescribeForPrompt(string userId)
        {
            var v = Get(userId);
            if (v == null || v.visits <= 1) return "";
            var sb = new StringBuilder();
            sb.Append("这位观众是第").Append(v.visits).Append("次来直播间的老朋友");
            if (v.giftDiamonds >= 100) sb.Append("，也是送过 ").Append(v.giftDiamonds).Append(" 抖币礼物的大哥，要格外热情");
            else if (v.giftDiamonds > 0) sb.Append("，之前送过小礼物");
            sb.Append("。");
            return sb.ToString();
        }
    }

    // 直播间语境：最近弹幕 + 当前节目状态，注入 AI prompt 让回复"知道现场"
    public class RoomContext
    {
        readonly Queue<string> recentChats = new Queue<string>();
        const int MaxChats = 8;

        public string LastGiftDesc = "";     // "小明送的火箭"
        public SongService Song;             // 取 NowPlaying

        public void AddChat(string nickname, string content)
        {
            if (string.IsNullOrEmpty(content)) return;
            string line = (string.IsNullOrEmpty(nickname) ? "观众" : nickname) + "：" +
                          (content.Length > 30 ? content.Substring(0, 30) : content);
            recentChats.Enqueue(line);
            while (recentChats.Count > MaxChats) recentChats.Dequeue();
        }

        public string BuildPromptSection()
        {
            var sb = new StringBuilder();
            bool any = false;
            if (Song != null && !string.IsNullOrEmpty(Song.NowPlaying))
            {
                sb.Append("你正在唱《").Append(Song.NowPlaying).Append("》。");
                any = true;
            }
            if (!string.IsNullOrEmpty(LastGiftDesc))
            {
                sb.Append("刚刚收到了").Append(LastGiftDesc).Append("。");
                any = true;
            }
            if (recentChats.Count > 0)
            {
                sb.Append("直播间最近的弹幕：").Append(string.Join("；", recentChats)).Append("。");
                any = true;
            }
            return any ? "当前直播间情况（供你了解现场，不必逐条回应）：" + sb : "";
        }

        public void ResetSession()
        {
            recentChats.Clear();
            LastGiftDesc = "";
        }
    }
}
