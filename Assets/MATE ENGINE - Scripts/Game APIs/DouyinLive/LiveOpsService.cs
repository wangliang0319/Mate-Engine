using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DouyinLive
{
    // 直播运营播报：本场礼物感谢榜（周期口播 Top3）+ 整点报时
    public class LiveOpsService
    {
        public SpeechPipeline Speech;
        public bool Enabled = true;
        public float GiftBoardInterval = 1800f;   // 感谢榜播报间隔（默认30分钟）

        readonly Dictionary<string, (string name, int value)> sessionGifts =
            new Dictionary<string, (string, int)>();
        float lastBoardAt;
        int lastChimeHour = -1;

        public void RecordGift(string userId, string nickname, int value)
        {
            if (string.IsNullOrEmpty(userId) || value <= 0) return;
            sessionGifts.TryGetValue(userId, out var cur);
            sessionGifts[userId] = (string.IsNullOrEmpty(nickname) ? cur.name : nickname, cur.value + value);
        }

        public void Tick()
        {
            if (!Enabled || Speech == null) return;
            if (Speech.IsSpeaking || Speech.QueueCount > 0) return;

            TickHourChime();
            TickGiftBoard();
        }

        // 整点报时（首次进入的整点不播，避免开播即报时）
        void TickHourChime()
        {
            int hour = DateTime.Now.Hour;
            if (lastChimeHour < 0) { lastChimeHour = hour; return; }
            if (hour == lastChimeHour) return;
            lastChimeHour = hour;

            int h12 = hour % 12 == 0 ? 12 : hour % 12;
            string line;
            if (hour >= 23 || hour < 6)
                line = $"已经{h12}点啦，还没睡的都是真爱，熬夜陪我记得多喝水哦~";
            else if (hour < 11)
                line = $"上午{h12}点整，新的一小时也要元气满满~";
            else if (hour < 14)
                line = $"{h12}点啦，宝子们记得吃饭，别饿着肚子看直播~";
            else if (hour < 18)
                line = $"下午{h12}点整，跟我一起打起精神来~";
            else
                line = $"晚上{h12}点整，陪伴大家的每一小时都很开心~";

            Speech.Enqueue(line, SpeechPipeline.Priority.LikeThanks, 60f);
        }

        // 礼物感谢榜：周期口播本场 Top3
        void TickGiftBoard()
        {
            if (lastBoardAt <= 0f) { lastBoardAt = Time.unscaledTime; return; }
            if (Time.unscaledTime - lastBoardAt < GiftBoardInterval) return;
            if (sessionGifts.Count == 0) { lastBoardAt = Time.unscaledTime; return; }

            lastBoardAt = Time.unscaledTime;
            var top = sessionGifts.Values
                .Where(g => !string.IsNullOrEmpty(g.name))
                .OrderByDescending(g => g.value)
                .Take(3)
                .Select(g => g.name)
                .ToList();
            if (top.Count == 0) return;

            string line = top.Count == 1
                ? $"感谢榜时间！特别感谢 {top[0]} 今天的礼物支持，爱你！"
                : $"感谢榜时间！特别感谢 {string.Join("、", top)} 的礼物支持，你们是最棒的！";
            Speech.Enqueue(line, SpeechPipeline.Priority.Milestone, 60f);
        }

        public void ResetSession()
        {
            sessionGifts.Clear();
            lastBoardAt = Time.unscaledTime;
            lastChimeHour = DateTime.Now.Hour;
        }
    }
}
