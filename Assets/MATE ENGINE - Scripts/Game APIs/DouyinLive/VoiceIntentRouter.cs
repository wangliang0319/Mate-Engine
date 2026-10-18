using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DouyinLive
{
    // 语音传话路由：非命令语音（硬命令/触发规则/槽位/表演意图都已被前几层消费）
    // 到这里二分——是让角色对外播报（relay），还是在和角色聊天（chat）。
    // chat/超时/失败一律回聊天路径，体面降级，绝不让直播停下来等分类。
    //
    // 与 IntentResolver 的两点差别：没有本地预筛（传话句不含表演关键词，预筛必漏）、
    // 没有单人冷却（传话是主播连续说话的合法用法，15 秒冷却会废掉它）。
    // 异步骨架与 IntentResolver 相同：自掐 1.5 秒表 + 结果经 MainThreadDispatcher 回主线程。
    public class VoiceIntentRouter
    {
        public IChatBackend Cloud;
        public bool debugLog = false;

        public const int MaxInFlight = 1;
        public const float TimeoutSeconds = 1.5f;

        const string SystemPrompt =
            "你是主播语音意图分类器。主播正在对桌面虚拟角色说话，判断这句话的目的：" +
            "relay=让角色把这段话原样转达给直播间观众（公告/通知/交代事情），" +
            "chat=在和角色对话、问它问题、或让它做事。" +
            "只输出 JSON，不要任何解释：{\"intent\":\"relay|chat\"}";

        static readonly List<ChatMsg> EmptyHistory = new List<ChatMsg>();

        int inFlight;

        // StartLive/StopLive 调用：丢弃在途分类，别让上一场的话进这一场
        public void Reset()
        {
            inFlight = 0;
        }

        // 返回 true = 已接管（正在问大模型），调用方不要再走聊天路径；
        // false = 后端不可用/已在飞，调用方直接走聊天。
        // 结果稍后一定会经 onRelay 或 onChat 回到主线程，不会石沉大海。
        public bool TryRoute(DouyinEvent ev, Action<string> onRelay, Action<DouyinEvent> onChat)
        {
            if (ev == null || onRelay == null || onChat == null) return false;
            if (Cloud == null || !Cloud.IsAvailable)
            {
                if (debugLog) Debug.Log("[VoiceIntentRouter] 云端后端未配置，传话分类不可用，走聊天");
                return false;
            }
            if (inFlight >= MaxInFlight) return false;

            inFlight++;
            // 丢弃的 Task 里逃出来的异常没人接，观测一下免得进 UnobservedTaskException。
            _ = RouteAsync(ev, onRelay, onChat)
                    .ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            return true;
        }

        async Task RouteAsync(DouyinEvent ev, Action<string> onRelay, Action<DouyinEvent> onChat)
        {
            string raw = null;
            var isRelay = false;
            try
            {
                Task<string> call = null;
                var cts = new CancellationTokenSource();
                try
                {
                    call = Cloud.ChatAsync(SystemPrompt, EmptyHistory, ev.Content ?? "", null, cts.Token);
                    // 超时不能只靠 CancellationToken：CloudChatBackend 的 SSE 读循环用的是
                    // 不带 ct 的 ReadLineAsync，首字节迟迟不来时 token 拦不住，自己掐表
                    var done = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds)));
                    if (done == call) raw = await call;
                    else cts.Cancel();
                }
                catch (Exception ex)
                {
                    if (debugLog) Debug.Log("[VoiceIntentRouter] 分类失败：" + ex.Message);
                }
                finally
                {
                    // 不能 using：超时那条路上被丢下的 call 还攥着 cts.Token，当场
                    // Dispose 会抛 ObjectDisposedException。挂延续等它收尾，顺手观测异常。
                    if (call == null) cts.Dispose();
                    else call.ContinueWith(t => { _ = t.Exception; cts.Dispose(); }, TaskScheduler.Default);
                }

                // 分类结果只当数据用：relay 之外的一切（chat/none/越权输出/解析失败）都回聊天
                isRelay = IntentText.IsRelayVerdict(raw);
            }
            finally
            {
                // Post 放 finally：inFlight 只在这个回调里减，中间任何一步抛出都会
                // 永久漏掉一个名额，漏两次传话功能整场作废。
                var e = ev;
                var relay = isRelay;
                MainThreadDispatcher.Post(() =>
                {
                    // Reset() 可能在回调还排队时清零 inFlight，Max(0, …) 防止减成负数
                    inFlight = Mathf.Max(0, inFlight - 1);
                    if (debugLog) Debug.Log($"[VoiceIntentRouter] 「{e.Content}」→ {(relay ? "传话" : "聊天")}");
                    if (relay) onRelay(e.Content);
                    else onChat(e);
                });
            }
        }
    }
}
