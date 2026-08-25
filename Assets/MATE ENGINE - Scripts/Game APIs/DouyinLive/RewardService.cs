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
        public SongService Song;                    // 在线点歌（网易云搜索+跟舞）
        public bool Enabled = true;
        public bool DanmakuRequestEnabled = true;   // 允许弹幕免费点播
        public List<GiftRuleData> Rules = new List<GiftRuleData>();

        AvatarDanceHandler dance;
        VRMLoader vrmLoader;
        float lastSwitchAt = -999f;
        const float SwitchCooldown = 30f;   // 换角色冷却，防刷屏
        readonly System.Random rng = new System.Random();
        static readonly Regex RequestRegex = new Regex(@"^\s*(点歌|换角色)\s*(.*)$", RegexOptions.Compiled);

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

            string cmd = m.Groups[1].Value;
            string title = m.Groups[2].Value.Trim();
            string name = string.IsNullOrEmpty(ev.Nickname) ? "朋友" : ev.Nickname;

            if (cmd == "点歌")
            {
                // 优先本地 MMD 舞蹈库：同名舞包是真编舞+原曲音频，效果最好
                var d = Dance;
                if (d != null && !string.IsNullOrEmpty(title))
                {
                    int idx = d.FindIndexByTitleFuzzy(title);
                    if (idx >= 0 && d.PlayIndex(idx))
                    {
                        Speech?.Enqueue($"好嘞！{name} 点的 {title}，舞蹈版走起！", SpeechPipeline.Priority.GiftThanks, 30f);
                        return true;
                    }
                }
                // 曲库没有 → 在线搜歌播放 + 跟随跳舞
                if (Song != null && !string.IsNullOrEmpty(title))
                    Song.RequestSong(title, name);
                else if (string.IsNullOrEmpty(title))
                    Speech?.Enqueue($"{name} 想点什么歌呀？发 点歌加歌名 哦~", SpeechPipeline.Priority.AIReply, 20f);
                else
                    HandleRequest(name, title); // SongService 未挂载时退回本地舞蹈库
                return true;
            }

            // 换角色：从模型库随机切换一个不同的已加载 VRM
            SwitchRandomAvatar(name);
            return true;
        }

        // ---------- 换角色 ----------

        void SwitchRandomAvatar(string userName)
        {
            if (Time.unscaledTime - lastSwitchAt < SwitchCooldown)
            {
                Speech?.Enqueue("刚换过啦，让我先穿一会儿这身嘛~", SpeechPipeline.Priority.AIReply, 20f);
                return;
            }
            if (vrmLoader == null) vrmLoader = UnityEngine.Object.FindFirstObjectByType<VRMLoader>();
            if (vrmLoader == null) return;

            string current = SaveLoadHandler.Instance != null ? SaveLoadHandler.Instance.data.selectedModelPath : "";
            var candidates = new List<string>();
            try
            {
                string jsonPath = System.IO.Path.Combine(Application.persistentDataPath, "avatars.json");
                if (System.IO.File.Exists(jsonPath))
                {
                    var entries = Newtonsoft.Json.JsonConvert.DeserializeObject<List<AvatarLibraryMenu.AvatarEntry>>(
                        System.IO.File.ReadAllText(jsonPath));
                    if (entries != null)
                        foreach (var e in entries)
                            if (e != null && !string.IsNullOrEmpty(e.filePath) && e.filePath != current &&
                                System.IO.File.Exists(e.filePath))
                                candidates.Add(e.filePath);
                }
            }
            catch (System.Exception ex) { Debug.LogWarning("[RewardService] read avatars.json failed: " + ex.Message); }

            // 默认模型也算一个候选（当前不是默认模型时）
            if (!string.IsNullOrEmpty(current)) candidates.Add("");

            if (candidates.Count == 0)
            {
                Speech?.Enqueue("衣柜里暂时没有别的角色啦~", SpeechPipeline.Priority.AIReply, 20f);
                return;
            }

            lastSwitchAt = Time.unscaledTime;
            string pick = candidates[rng.Next(candidates.Count)];
            Speech?.Enqueue($"{userName} 想看新角色是吧？看我变身！", SpeechPipeline.Priority.GiftThanks, 20f);
            // 先记录当前角色高度基准，新模型加载完成后自动缩放到相同显示高度
            DouyinLiveManager.Instance?.NormalizeNextAvatarHeight();
            if (string.IsNullOrEmpty(pick)) vrmLoader.ActivateDefaultModel();
            else vrmLoader.LoadVRM(pick);
        }

        public void OnGift(DouyinEvent ev)
        {
            if (!Enabled || ev.Type != DouyinMsgType.Gift) return;
            string name = string.IsNullOrEmpty(ev.Nickname) ? "朋友" : ev.Nickname;
            int totalValue = Mathf.Max(1, ev.DiamondCount) * Mathf.Max(1, ev.GiftCount);

            // 按总价值分三档庆祝，越贵越隆重（吸引送礼的核心展示）
            if (totalValue >= 100)
                CelebrateBig(name, ev);
            else if (totalValue >= 10)
                CelebrateMedium(name, ev);
            else
                CelebrateSmall(name, ev);

            // 礼物规则仍可触发额外动作（指定舞等）
            var rule = MatchRule(ev);
            if (rule != null && rule.action.StartsWith("dance:"))
                PlayNamedDance(rule.action.Substring(6).Trim(), name);
        }

        // 小礼物（<10抖币）：甜甜的致谢
        void CelebrateSmall(string name, DouyinEvent ev)
        {
            string[] lines = {
                "谢谢 {u} 送的 {g}，么么哒！",
                "收到 {u} 的 {g} 啦，谢谢宝贝！",
                "哇，{u} 送我 {g}，开心，爱你哟！",
            };
            Speech?.Enqueue(Fill(Pick(lines), name, ev), SpeechPipeline.Priority.GiftThanks, 60f);
        }

        // 中礼物（10~99抖币）：热情致谢 + 数量播报
        void CelebrateMedium(string name, DouyinEvent ev)
        {
            string[] lines = {
                "哇塞！谢谢 {u} 的 {g}，你也太好了吧，抱抱你！",
                "天呐，{u} 送了 {g}！大哥大气，爱你爱你！",
                "感谢 {u} 的 {g}！今天最喜欢你啦！",
            };
            Speech?.Enqueue(Fill(Pick(lines), name, ev), SpeechPipeline.Priority.GiftThanks, 60f);
        }

        // 大礼物（≥100抖币）：欢呼 + 自动跳一支舞庆祝
        void CelebrateBig(string name, DouyinEvent ev)
        {
            string[] lines = {
                "哇！！{u} 送出了超级大礼 {g}！！全体起立！谢谢老板，老板大气！为你跳一支舞！",
                "天呐天呐！感谢 {u} 的 {g}！这是今天最开心的时刻！这支舞献给你！",
                "呜哇，{u} 太壕了！{g} 收到！比心比心，看我为你跳舞！",
            };
            Speech?.Enqueue(Fill(Pick(lines), name, ev), SpeechPipeline.Priority.GiftThanks, 90f);
            PlayRandomDance();
        }

        static string Fill(string tpl, string name, DouyinEvent ev)
        {
            string gift = ev.GiftName;
            if (ev.GiftCount > 1) gift += " 乘 " + ev.GiftCount;
            return tpl.Replace("{u}", name).Replace("{g}", gift);
        }

        string Pick(string[] arr) => arr[rng.Next(arr.Length)];

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
