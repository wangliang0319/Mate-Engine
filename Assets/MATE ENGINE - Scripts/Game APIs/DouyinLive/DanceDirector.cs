using UnityEngine;
using CustomDancePlayer;

namespace DouyinLive
{
    // 舞蹈编排：洗牌轮播 + 连播。表演增强（粒子/防出画）在 Task 12 补上。
    public class DanceDirector : MonoBehaviour
    {
        public bool debugLog = false;
        public int danceChainCount = 1;   // 一次触发连跳几支

        readonly ShuffleBag bag = new ShuffleBag();
        AvatarDanceHandler dance;
        int chainRemaining;
        bool wasPlaying;

        AvatarDanceHandler Dance
        {
            get
            {
                if (dance == null) dance = FindFirstObjectByType<AvatarDanceHandler>(FindObjectsInactive.Include);
                return dance;
            }
        }

        public bool PlayRandom()
        {
            var d = Dance;
            if (d == null || d.EntryCount <= 0) return false;

            // 舞包目录可能在运行时变（用户往 StreamingAssets 里丢新包），数量变了就重置
            if (bag.Count != d.EntryCount) bag.Reset(d.EntryCount);

            int idx = bag.Next();
            if (idx < 0) return false;

            if (!d.PlayIndex(idx)) return false;
            chainRemaining = Mathf.Max(0, danceChainCount - 1);
            if (debugLog) Debug.Log($"[Dance] 播放索引 {idx}，还要连播 {chainRemaining} 支");
            return true;
        }

        // 连播尚未开始时（chainRemaining == 0，danceChainCount 默认为 1）为 false，
        // ActionDirector.Tick 用它和 Dance.IsPlaying 一起判断 L3 独占位能不能放开——
        // 见该文件里的注释。
        public bool ChainPending => chainRemaining > 0;

        void Update()
        {
            // Dance 的 getter 在缓存失效时会做一次 FindFirstObjectByType 全场景扫描
            // （含未激活对象），代价不小。danceChainCount 默认是 1，chainRemaining
            // 平时恒为 0，没有连播要处理时提前退出，避免每帧白扫一次。连播链本身
            // 不受影响：触发它要求 wasPlaying 曾经被观测为 true，只有真的调用过
            // PlayRandom 并把 chainRemaining 置为正数之后才会发生。
            if (chainRemaining <= 0) { wasPlaying = false; return; }

            var d = Dance;
            bool playing = d != null && d.IsPlaying;

            // 一支跳完 → 若还有连播次数就接着来一支
            if (wasPlaying && !playing && chainRemaining > 0)
            {
                chainRemaining--;
                PlayRandomKeepChain();
            }
            wasPlaying = playing;
        }

        void PlayRandomKeepChain()
        {
            int keep = chainRemaining;
            PlayRandom();
            chainRemaining = keep;   // PlayRandom 会重置连播计数，这里保住剩余次数
        }
    }
}
