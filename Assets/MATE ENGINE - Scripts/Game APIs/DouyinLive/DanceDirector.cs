using UnityEngine;
using CustomDancePlayer;

namespace DouyinLive
{
    // 舞蹈编排：洗牌轮播 + 连播 + 表演增强（跳舞期间切粒子主题、竖屏下防出画）。
    public class DanceDirector : MonoBehaviour
    {
        public bool debugLog = false;
        public int danceChainCount = 1;   // 一次触发连跳几支

        public string danceParticleTheme = "";   // 留空 = 不切粒子主题
        [Range(0.05f, 0.4f)] public float portraitSoftZoneRatio = 0.15f;

        readonly ShuffleBag bag = new ShuffleBag();
        AvatarDanceHandler dance;
        int chainRemaining;
        bool wasPlaying;

        AvatarParticleHandler particles;
        AvatarDanceSafetyZone safety;
        PortraitWindowController portrait;

        string themeBefore;
        bool safetyEnabledBefore, moveWindowBefore;
        float softLeftBefore, softRightBefore;
        bool decorated;
        bool particleOverridden, safetyOverridden;

        // dance 找到之前每 2 秒重扫一次场景。FindFirstObjectByType(..., Include)
        // 是全场景扫描（含未激活对象），代价不小，不能每帧做；节流期内 Dance 会
        // 短暂返回 null，PlayRandom 因此退化到内置动画 —— 这只发生在头像还没
        // 加载完的开局阶段，下一次重扫命中后自动恢复，无需额外处理。
        float danceScanCooldown;

        AvatarDanceHandler Dance
        {
            get
            {
                if (dance == null)
                {
                    if (Time.unscaledTime < danceScanCooldown) return null;
                    dance = FindFirstObjectByType<AvatarDanceHandler>(FindObjectsInactive.Include);
                    if (dance == null) danceScanCooldown = Time.unscaledTime + 2f;
                }
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

        // 正在跳（或连播还没跳完）。冷场自动跳舞用它避免在舞跳到一半时又起一支。
        public bool Busy => (Dance != null && Dance.IsPlaying) || ChainPending;

        void Update()
        {
            var d = Dance;
            bool playing = d != null && d.IsPlaying;

            if (playing && !decorated) BeginPerformance();
            if (!playing && decorated) EndPerformance();

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

        void BeginPerformance()
        {
            decorated = true;

            // 粒子：记录用户原本选的主题，跳完必须还原，否则会悄悄改掉他的设置
            if (!string.IsNullOrEmpty(danceParticleTheme))
            {
                if (particles == null) particles = FindFirstObjectByType<AvatarParticleHandler>(FindObjectsInactive.Include);
                if (particles != null)
                {
                    themeBefore = particles.selectedTheme;
                    particles.SetTheme(danceParticleTheme);
                    particleOverridden = true;
                }
            }

            // 防出画：只在竖屏直播时开。AvatarDanceSafetyZone 默认会跟着平移系统窗口，
            // 而直播伴侣是按窗口采集的，窗口一漂画面就毁了 —— 必须强制关掉。
            if (portrait == null) portrait = FindFirstObjectByType<PortraitWindowController>();
            if (portrait == null || !portrait.Active) return;

            if (safety == null) safety = FindFirstObjectByType<AvatarDanceSafetyZone>(FindObjectsInactive.Include);
            if (safety == null) return;

            safetyEnabledBefore = safety.enableSafety;
            moveWindowBefore = safety.moveWindowAlong;
            softLeftBefore = safety.softZoneLeftPx;
            softRightBefore = safety.softZoneRightPx;
            safetyOverridden = true;

            safety.moveWindowAlong = false;
            float soft = Screen.width * portraitSoftZoneRatio;
            safety.softZoneLeftPx = soft;
            safety.softZoneRightPx = soft;
            safety.SetSafetyEnabled(true);
        }

        void EndPerformance()
        {
            decorated = false;

            if (particleOverridden && particles != null)
            {
                particles.SetTheme(themeBefore);
                themeBefore = null;
            }
            particleOverridden = false;

            if (safetyOverridden && safety != null)
            {
                safety.SetSafetyEnabled(safetyEnabledBefore);
                safety.moveWindowAlong = moveWindowBefore;
                safety.softZoneLeftPx = softLeftBefore;
                safety.softZoneRightPx = softRightBefore;
            }
            safetyOverridden = false;
        }

        // 播放中被强制销毁（换角色/退出）时也要还原，否则用户设置被永久改掉
        void OnDisable() { if (decorated) EndPerformance(); }
    }
}
