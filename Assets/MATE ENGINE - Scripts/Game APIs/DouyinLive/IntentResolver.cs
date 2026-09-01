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

        static readonly List<ChatMsg> EmptyHistory = new List<ChatMsg>();

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
            // 本地预筛先做：非点播弹幕不该产生任何 [IntentResolver] 日志，
            // 不然「后端不可用」的提示会在每条闲聊弹幕上刷一遍
            if (IntentText.LooksLikeIntent(ev.Content) == IntentKind.None) return false;
            if (Cloud == null || !Cloud.IsAvailable)
            {
                // 和「预筛没命中」区分开：这条日志代表设置页没配云端后端/本地 LLM
                // 不接这条兜底，不是这句弹幕真的判不出意图
                if (debugLog) Debug.Log("[IntentResolver] 云端后端未配置或不可用，本条弹幕不走意图兜底");
                return false;
            }
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
            // 丢弃的 Task 里逃出来的异常没人接，观测一下免得进 UnobservedTaskException。
            _ = ResolveAsync(ev, onResolved, onGiveUp)
                    .ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
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
            var kind = IntentKind.None;
            string arg = null;
            try
            {
                Task<string> call = null;
                var cts = new CancellationTokenSource();
                try
                {
                    call = Cloud.ChatAsync(SystemPrompt, EmptyHistory, ev.Content ?? "", null, cts.Token);
                    // 超时不能只靠 CancellationToken：CloudChatBackend 的 SSE 读循环用的是
                    // 不带 ct 的 ReadLineAsync，首字节迟迟不来的时候那个 token 拦不住它，
                    // 最坏一直挂到 HttpClient 的 120 秒。这条弹幕在开问的瞬间就被消费掉了，
                    // 它挂多久观众就沉默多久，所以这里自己掐表。
                    var done = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds)));
                    if (done == call) raw = await call;
                    else cts.Cancel();
                }
                catch (Exception ex)
                {
                    if (debugLog) Debug.Log("[IntentResolver] 意图判定失败：" + ex.Message);
                }
                finally
                {
                    // 不能用 using：超时那条路上被丢下的 call 还攥着 cts.Token，当场
                    // Dispose 会让它抛 ObjectDisposedException。挂个延续等它自己收尾，
                    // 顺手观测掉异常，免得变成 UnobservedTaskException。
                    if (call == null) cts.Dispose();
                    else call.ContinueWith(t => { _ = t.Exception; cts.Dispose(); }, TaskScheduler.Default);
                }

                // 模型返回的内容只当数据用：这里只取 intent 和 arg 两个字段，
                // arg 还要再过一遍 IsUsableArg 才会被当成歌名，永远不会被当指令执行
                if (raw != null && IntentText.TryParseIntentJson(raw, out var parsedKind, out var parsedArg))
                {
                    kind = parsedKind;
                    arg = parsedArg;
                }
            }
            finally
            {
                // Post 放 finally：inFlight 只在这个回调里减，中间任何一步抛出都会
                // 永久漏掉一个名额，漏两次这个功能就整场作废了。
                var k = kind;
                var a = arg ?? "";
                var e = ev;
                MainThreadDispatcher.Post(() =>
                {
                    // Reset() 可能在回调还排队时清零 inFlight，Max(0, …) 防止减成负数
                    inFlight = Mathf.Max(0, inFlight - 1);
                    if (debugLog) Debug.Log($"[IntentResolver] 「{e.Content}」→ {k} / {a}");
                    if (k == IntentKind.None) onGiveUp(e);
                    else onResolved(e, k, a);
                });
            }
        }
    }
}
