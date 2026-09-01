using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DouyinLive
{
    // 关键词规则和追问槽位都没接住时的最后一层兜底：花不超过 1.5 秒问一次
    // 大模型「这条弹幕是不是在点歌/点舞/换角色」。
    //
    // 刻意不复用 DanmakuAIService.GenerateOneShot：那条路会注入完整人设 prompt、
    // 追加「只回一句话，不超过30个字」、再过一遍 Sanitize（剥方括号 + 60 字截断）。
    // 分类任务要的是干净 JSON，人设化的一句话正好是它最不需要的东西。
    public class IntentResolver
    {
        public IChatBackend Cloud;
        public bool debugLog = false;
        public Func<float> Now = () => Time.unscaledTime;

        public const float PerUserCooldown = 15f;   // 同一个人问过就先歇着，防刷 token
        public const int MaxInFlight = 2;
        public const float TimeoutSeconds = 1.5f;   // 超过这个时间观众已经在等下一条弹幕了
        const int MaxTrackedUsers = 200;

        const string SystemPrompt =
            "你是弹幕意图分类器。判断这句弹幕是不是在点歌、点舞或要求换角色。" +
            "只输出 JSON，不要任何解释：" +
            "{\"intent\":\"song|dance|avatar|none\",\"arg\":\"歌名/舞名/角色名，没有就留空\"}";

        readonly Dictionary<string, float> lastAskedByUser = new Dictionary<string, float>();
        int inFlight;

        public void Reset()
        {
            lastAskedByUser.Clear();
            inFlight = 0;
        }

        // 返回 true = 这条弹幕已被接管（正在问大模型），调用方不要再走原路径。
        // 结果稍后一定会经 onResolved 或 onGiveUp 回到主线程，不会石沉大海。
        public bool TryResolve(DouyinEvent ev,
                               Action<DouyinEvent, IntentKind, string> onResolved,
                               Action<DouyinEvent> onGiveUp)
        {
            if (ev == null || onResolved == null || onGiveUp == null) return false;
            if (Cloud == null || !Cloud.IsAvailable) return false;
            // 本地预筛：没有任何点播痕迹的弹幕不值得花 token
            if (IntentText.LooksLikeIntent(ev.Content) == IntentKind.None) return false;
            if (inFlight >= MaxInFlight) return false;

            string uid = ev.UserId ?? "";
            float now = Now();
            if (!string.IsNullOrEmpty(uid))
            {
                if (lastAskedByUser.TryGetValue(uid, out float last) && now - last < PerUserCooldown)
                    return false;
                if (lastAskedByUser.Count >= MaxTrackedUsers) PruneUsers(now);
                lastAskedByUser[uid] = now;
            }

            inFlight++;
            _ = ResolveAsync(ev, onResolved, onGiveUp);
            return true;
        }

        void PruneUsers(float now)
        {
            var stale = new List<string>();
            foreach (var kv in lastAskedByUser)
                if (now - kv.Value >= PerUserCooldown) stale.Add(kv.Key);
            foreach (var k in stale) lastAskedByUser.Remove(k);
        }

        async Task ResolveAsync(DouyinEvent ev,
                                Action<DouyinEvent, IntentKind, string> onResolved,
                                Action<DouyinEvent> onGiveUp)
        {
            string raw = null;
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds)))
                    raw = await Cloud.ChatAsync(SystemPrompt, new List<ChatMsg>(), ev.Content ?? "",
                                                null, cts.Token);
            }
            catch (Exception ex)
            {
                // 超时是常态不是故障，默认不刷屏
                if (debugLog) Debug.Log("[IntentResolver] 判定失败，改走原路径: " + ex.Message);
            }

            IntentKind kind = IntentKind.None;
            string arg = "";
            if (raw != null && !IntentText.TryParseIntentJson(raw, out kind, out arg))
            {
                kind = IntentKind.None;
                arg = "";
            }

            // 模型返回的内容只当数据用：这里只取 intent 和 arg 两个字段，
            // arg 还要再过一遍 IsUsableArg 才会被当成歌名，永远不会被当指令执行
            var k = kind;
            var a = arg;
            var e = ev;
            MainThreadDispatcher.Post(() =>
            {
                inFlight--;
                if (debugLog) Debug.Log($"[IntentResolver] 「{e.Content}」→ {k} / {a}");
                if (k == IntentKind.None) onGiveUp(e);
                else onResolved(e, k, a);
            });
        }
    }
}
