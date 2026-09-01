using System;
using System.Collections.Generic;

namespace DouyinLive
{
    public enum IntentKind { None, Song, Dance, Avatar }

    // 一次「角色问了、等观众答」的待补状态
    public class IntentSlot
    {
        public string UserId = "";
        public string Nickname = "";
        public IntentKind Kind = IntentKind.None;
        public string RuleId = "";     // 开槽的规则，补全时按 id 反查回去借它的限流参数
        public float OpenedAt;
    }

    // 追问槽位表：角色问「想听什么歌呀」之后，这个观众接下来 Window 秒内发的
    // 第一条可用弹幕就是答案。按 UserId 索引 —— 只认发起人，别人插嘴不算数。
    public class IntentSlots
    {
        public Func<float> Now = () => 0f;
        public float Window = 30f;     // <= 0 等于关闭追问功能
        public int Capacity = 8;

        readonly Dictionary<string, IntentSlot> slots = new Dictionary<string, IntentSlot>();

        public int Count { get { return slots.Count; } }

        public void Open(string userId, string nickname, IntentKind kind, string ruleId)
        {
            if (string.IsNullOrEmpty(userId)) return;   // 认不出是谁，补全无从谈起
            if (kind == IntentKind.None || Window <= 0f) return;

            Prune();
            // 容量满时挤掉最旧的而不是拒绝新的：直播间里新请求比旧请求有价值
            if (!slots.ContainsKey(userId) && slots.Count >= Capacity) EvictOldest();

            slots[userId] = new IntentSlot
            {
                UserId = userId,
                Nickname = nickname ?? "",
                Kind = kind,
                RuleId = ruleId ?? "",
                OpenedAt = Now()
            };
        }

        // 只看不删。补全内容通不过校验时槽位要原样留着，而「取出来再放回去」
        // 会刷新 OpenedAt —— 观众连发十个「666」就能把 30 秒窗口无限续期。
        public bool TryPeek(string userId, out IntentSlot slot)
        {
            slot = null;
            if (string.IsNullOrEmpty(userId)) return false;
            Prune();
            return slots.TryGetValue(userId, out slot);
        }

        public void Take(string userId)
        {
            if (!string.IsNullOrEmpty(userId)) slots.Remove(userId);
        }

        public void Prune()
        {
            if (slots.Count == 0) return;
            float now = Now();
            List<string> stale = null;
            foreach (var kv in slots)
            {
                if (now - kv.Value.OpenedAt < Window) continue;
                if (stale == null) stale = new List<string>();
                stale.Add(kv.Key);
            }
            if (stale == null) return;
            foreach (var k in stale) slots.Remove(k);
        }

        public void Reset()
        {
            slots.Clear();
        }

        void EvictOldest()
        {
            string oldest = null;
            float best = float.MaxValue;
            foreach (var kv in slots)
                if (kv.Value.OpenedAt < best) { best = kv.Value.OpenedAt; oldest = kv.Key; }
            if (oldest != null) slots.Remove(oldest);
        }
    }
}
