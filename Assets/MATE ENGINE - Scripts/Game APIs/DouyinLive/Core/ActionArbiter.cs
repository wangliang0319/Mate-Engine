using System;
using System.Collections.Generic;

namespace DouyinLive
{
    public class ActionRequest
    {
        public TriggerRule Rule;
        public string UserId;
        public string Nickname;
        public string GiftName;
        public int GiftCount;
        // 弹幕正文。dance:request / swapAvatar:request 是从正文里剥关键词拿到名字的，
        // 排队几分钟后出队时原始事件早没了，不带上这个字段就会退化成「再问一遍」。
        public string Content;
    }

    public enum ArbitrationResult
    {
        Execute,           // 立即执行
        Queued,            // L3 排队，等当前 L3 或唱歌结束
        DroppedQueueFull,  // 队列满，本条挤掉了最旧的一条
        DeferredBusy
    }

    // 三层仲裁的纯逻辑部分。不碰 Unity 场景，可在 EditMode 测试里跑。
    // 状态（在唱歌吗、L3 在忙吗）由 ActionDirector 每帧写进来。
    public class ActionArbiter
    {
        public Func<float> Now = () => 0f;

        public bool SingingNow;   // SongService.IsPlaying
        public bool L3Busy;       // 有 L3 正在执行

        readonly List<ActionRequest> l3Queue = new List<ActionRequest>();

        public int QueuedCount => l3Queue.Count;

        public ArbitrationResult Submit(ActionRequest req, TriggerGlobal g)
        {
            if (req?.Rule == null) return ArbitrationResult.DeferredBusy;
            if (g == null) g = new TriggerGlobal();

            // L1/L2 从不排队。L2 在唱歌时也放行 —— 降级成「只放粒子不播动画」
            // 由 EffectRegistry 按 SingingNow 决定；在这里拦掉就连粒子都没有了。
            if (req.Rule.LevelOrDefault < 3) return ArbitrationResult.Execute;

            bool blocked = L3Busy || (SingingNow && !g.l3InterruptSinging);
            if (!blocked) { L3Busy = true; return ArbitrationResult.Execute; }

            // 同一条规则已经在队列里就不重复入队：大哥连刷同一种礼物时，
            // 观众想看的是效果播出来，不是同一个效果排三遍。
            foreach (var q in l3Queue)
                if (q.Rule.id == req.Rule.id) return ArbitrationResult.Queued;

            l3Queue.Add(req);
            int cap = Math.Max(1, g.l3QueueSize);
            if (l3Queue.Count > cap)
            {
                // 丢最旧的：宁可少播一次，也不要观众等了几分钟才看到自己的效果
                l3Queue.RemoveAt(0);
                return ArbitrationResult.DroppedQueueFull;
            }
            return ArbitrationResult.Queued;
        }

        public ActionRequest TryDequeueL3(TriggerGlobal g)
        {
            if (g == null) g = new TriggerGlobal();
            if (l3Queue.Count == 0) return null;
            if (L3Busy) return null;
            if (SingingNow && !g.l3InterruptSinging) return null;

            var req = l3Queue[0];
            l3Queue.RemoveAt(0);
            L3Busy = true;
            return req;
        }

        public void NotifyL3Finished() => L3Busy = false;

        public void Reset()
        {
            l3Queue.Clear();
            L3Busy = false;
            SingingNow = false;
        }
    }
}
