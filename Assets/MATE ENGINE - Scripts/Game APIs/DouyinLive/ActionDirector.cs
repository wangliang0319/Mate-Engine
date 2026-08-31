using UnityEngine;
using CustomDancePlayer;

namespace DouyinLive
{
    // ActionArbiter 的场景侧外壳：每帧把「在唱歌吗/在跳舞吗」同步给仲裁器，
    // 驱动 L3 出队，并把闲聊打断的副作用落到 IdleChatterService 上。
    [RequireComponent(typeof(EffectRegistry))]
    public class ActionDirector : MonoBehaviour
    {
        public bool debugLog = false;

        public ActionArbiter Arbiter { get; } = new ActionArbiter();

        EffectRegistry effects;
        SongService song;
        AvatarDanceHandler dance;

        float l3StartedAt;
        const float L3MaxSeconds = 180f;   // 兜底：舞包异常没回调时也要放开独占

        void Awake()
        {
            effects = GetComponent<EffectRegistry>();
            Arbiter.Now = () => Time.unscaledTime;
        }

        SongService Song { get { if (song == null) song = GetComponent<SongService>(); return song; } }

        AvatarDanceHandler Dance
        {
            get
            {
                if (dance == null) dance = FindFirstObjectByType<AvatarDanceHandler>(FindObjectsInactive.Include);
                return dance;
            }
        }

        // 返回 true 表示该事件已被仲裁层消费（真的执行了效果，或者进了 L3 队列/
        // 挤掉了队列里最旧的一条），调用方不用再回落到原有逻辑。
        // 只有 DeferredBusy（当前定义下 Submit 永远不会走到这个分支，留着是为了
        // 未来扩展，比如给非 L 级请求加个「系统忙」出口）才返回 false。
        //
        // Execute 分支要看 Execute() 的真实执行结果：一条规则的效果如果全是
        // 尚未实现的占位符（比如 gift3 在后续任务落地前），什么都没真的执行，
        // 这时必须如实返回 false —— 否则 TriggerRouter 会误以为消费成功，
        // 白白占用这条规则的冷却名额，还会让本该退回原有逻辑（点歌/换角色/
        // AI 回复）的事件被吞掉。
        public bool Submit(TriggerRule rule, DouyinEvent ev, TriggerGlobal g)
        {
            var req = new ActionRequest
            {
                Rule = rule,
                UserId = ev?.UserId,
                Nickname = ev?.Nickname,
                GiftName = ev?.GiftName,
                GiftCount = ev?.GiftCount ?? 0
            };

            var result = Arbiter.Submit(req, g);
            if (debugLog) Debug.Log($"[Director] {rule.id} L{rule.LevelOrDefault} → {result}");

            switch (result)
            {
                case ArbitrationResult.Execute:
                    bool executed = Execute(req, ev);
                    // Arbiter.Submit 把 L3Busy 设成 true 才会返回 Execute；如果这次
                    // 什么都没真的执行，独占位必须立刻放开，否则下一条真正的 L3
                    // 请求要一直等到 Tick 里的 2 秒空闲判定、甚至 180 秒兜底才能出队。
                    if (!executed && rule.LevelOrDefault == 3) Arbiter.NotifyL3Finished();
                    return executed;

                case ArbitrationResult.Queued:
                case ArbitrationResult.DroppedQueueFull:
                    // 请求本身是真实的，只是还没轮到它执行（或者挤掉了排在它前面
                    // 更旧的一条）——这仍然算触发层接管了这个事件。
                    return true;

                default: // DeferredBusy
                    return false;
            }
        }

        public void Tick(TriggerGlobal g)
        {
            Arbiter.SingingNow = Song != null && Song.IsPlaying;

            // L3 结束判定：既不在唱也不在跳，或超过兜底时长
            if (Arbiter.L3Busy)
            {
                bool busy = (Song != null && Song.IsPlaying) || (Dance != null && Dance.IsPlaying);
                if (!busy && Time.unscaledTime - l3StartedAt > 2f) Arbiter.NotifyL3Finished();
                else if (Time.unscaledTime - l3StartedAt > L3MaxSeconds)
                {
                    Debug.LogWarning("[Director] L3 超过兜底时长仍未结束，强制放开独占");
                    Arbiter.NotifyL3Finished();
                }
            }

            var queued = Arbiter.TryDequeueL3(g);
            if (queued != null)
            {
                bool executed = Execute(queued, null);
                // 同样的道理：出队的这条如果是空炮，不能占着独占位不放
                if (!executed) Arbiter.NotifyL3Finished();
            }
        }

        // 返回 true 表示至少有一个效果真正执行了；全部未实现/被拦下则返回 false。
        // 聚合方式照抄 TriggerRouter.Run：pick=="random" 只看被选中的那一个的结果，
        // 否则逻辑或但不短路——每个效果都要尝试执行一遍。
        bool Execute(ActionRequest req, DouyinEvent ev)
        {
            if (req.Rule.LevelOrDefault == 3) l3StartedAt = Time.unscaledTime;

            // L2 打断闲聊的暖场话（不打断唱歌/跳舞）
            if (req.Rule.LevelOrDefault == 2)
                DouyinLiveManager.Instance?.InterruptIdleChatter();

            var ctx = new EffectContext
            {
                Event = ev ?? Rebuild(req),
                Rule = req.Rule,
                SingingNow = Song != null && Song.IsPlaying
            };

            var list = req.Rule.effects;
            if (list == null || list.Count == 0) return false;

            if (req.Rule.pick == "random")
                return effects.Execute(list[Random.Range(0, list.Count)], ctx);

            bool any = false;
            foreach (var e in list)
                any |= effects.Execute(e, ctx);   // 逻辑或但不短路：每个效果都要尝试执行
            return any;
        }

        // 排队的请求出队时原始 DouyinEvent 已经不在了，用请求里存的字段重建
        // 一个够 say: 占位符替换用的最小事件。
        static DouyinEvent Rebuild(ActionRequest req) => new DouyinEvent
        {
            UserId = req.UserId,
            Nickname = req.Nickname,
            GiftName = req.GiftName,
            GiftCount = req.GiftCount
        };

        public void ResetSession() => Arbiter.Reset();
    }
}
