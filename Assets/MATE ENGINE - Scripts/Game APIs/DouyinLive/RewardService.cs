using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CustomDancePlayer;
using UnityEngine;

namespace DouyinLive
{
    [Serializable]
    public class GiftRuleData
    {
        public string giftName = "";       // 匹配 GiftName（包含匹配，忽略大小写）；空=任意礼物
        public int minDiamond = 0;         // 单价下限（抖币），0=不限
        public int minCount = 1;           // 数量下限
        public string action = "thanks";   // thanks | randomDance | dance:<stableId或标题> | builtinDance
    }

    // 礼物 → 点歌/点舞/致谢；弹幕点播命令 "点舞 xxx" / "点歌 xxx"
    public class RewardService
    {
        public SpeechPipeline Speech;
        public bool Enabled = true;
        public bool DanmakuRequestEnabled = true;   // 允许弹幕免费点播
        public List<GiftRuleData> Rules = new List<GiftRuleData>();

        AvatarDanceHandler dance;
        readonly System.Random rng = new System.Random();
        static readonly Regex RequestRegex = new Regex(@"^\s*(点舞|点歌)\s*(.*)$", RegexOptions.Compiled);

        AvatarDanceHandler Dance
        {
            get
            {
                if (dance == null) dance = UnityEngine.Object.FindFirstObjectByType<AvatarDanceHandler>(FindObjectsInactive.Include);
                return dance;
            }
        }

        // 返回 true 表示该弹幕是点播命令，已被消费（不再交给 AI 回复）
        public bool TryHandleDanmaku(DouyinEvent ev)
        {
            if (ev.Type != DouyinMsgType.Chat) return false;
            var m = RequestRegex.Match(ev.Content ?? "");
            if (!m.Success) return false;
            if (!Enabled || !DanmakuRequestEnabled) return true; // 是命令但功能关闭，静默吞掉

            HandleRequest(ev.Nickname, m.Groups[2].Value.Trim());
            return true;
        }

        public void OnGift(DouyinEvent ev)
        {
            if (!Enabled || ev.Type != DouyinMsgType.Gift) return;
            string name = string.IsNullOrEmpty(ev.Nickname) ? "朋友" : ev.Nickname;
            string thanks = $"谢谢 {name} 送的 {ev.GiftName}";
            if (ev.GiftCount > 1) thanks += $" 乘 {ev.GiftCount}";
            thanks += "！";

            var rule = MatchRule(ev);
            if (rule == null || rule.action == "thanks" || string.IsNullOrEmpty(rule.action))
            {
                Speech?.Enqueue(thanks, SpeechPipeline.Priority.GiftThanks, 60f);
                return;
            }

            if (rule.action == "randomDance")
            {
                Speech?.Enqueue(thanks + "为你跳一支舞~", SpeechPipeline.Priority.GiftThanks, 60f);
                PlayRandomDance();
            }
            else if (rule.action == "builtinDance")
            {
                Speech?.Enqueue(thanks + "开始跳舞咯~", SpeechPipeline.Priority.GiftThanks, 60f);
                PlayBuiltinDance();
            }
            else if (rule.action.StartsWith("dance:"))
            {
                string target = rule.action.Substring(6).Trim();
                Speech?.Enqueue(thanks, SpeechPipeline.Priority.GiftThanks, 60f);
                PlayNamedDance(target, name);
            }
        }

        GiftRuleData MatchRule(DouyinEvent ev)
        {
            GiftRuleData best = null;
            foreach (var r in Rules)
            {
                if (r == null) continue;
                if (!string.IsNullOrEmpty(r.giftName) &&
                    (ev.GiftName ?? "").IndexOf(r.giftName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (ev.DiamondCount < r.minDiamond) continue;
                if (ev.GiftCount < r.minCount) continue;
                // 更具体的规则优先：礼物名精确 > 单价高
                if (best == null ||
                    (!string.IsNullOrEmpty(r.giftName) && string.IsNullOrEmpty(best.giftName)) ||
                    r.minDiamond > best.minDiamond)
                    best = r;
            }
            return best;
        }

        void HandleRequest(string userName, string title)
        {
            string name = string.IsNullOrEmpty(userName) ? "朋友" : userName;
            if (string.IsNullOrEmpty(title))
            {
                Speech?.Enqueue($"收到 {name} 的点播，随机来一支！", SpeechPipeline.Priority.GiftThanks, 30f);
                PlayRandomDance();
                return;
            }
            PlayNamedDance(title, name);
        }

        void PlayNamedDance(string title, string userName)
        {
            var d = Dance;
            if (d == null)
            {
                Speech?.Enqueue("舞蹈播放器还没准备好呢~", SpeechPipeline.Priority.AIReply, 20f);
                return;
            }
            int idx = d.FindIndexByTitleFuzzy(title);
            if (idx < 0)
            {
                Speech?.Enqueue($"曲库里没有找到 {title} 哦~", SpeechPipeline.Priority.AIReply, 20f);
                return;
            }
            if (d.PlayIndex(idx))
                Speech?.Enqueue($"好嘞，{userName} 点的 {title}，马上安排！", SpeechPipeline.Priority.GiftThanks, 30f);
        }

        void PlayRandomDance()
        {
            var d = Dance;
            if (d == null || !TryPlayRandomCustom(d)) PlayBuiltinDance();
        }

        bool TryPlayRandomCustom(AvatarDanceHandler d)
        {
            int count = d.EntryCount;
            if (count <= 0) return false;
            return d.PlayIndex(rng.Next(0, count));
        }

        void PlayBuiltinDance()
        {
            var avatar = UnityEngine.Object.FindFirstObjectByType<AvatarAnimatorController>();
            if (avatar != null && avatar.animator != null)
            {
                avatar.isDancing = true;
                avatar.animator.SetBool("isDancing", true);
            }
        }
    }
}
