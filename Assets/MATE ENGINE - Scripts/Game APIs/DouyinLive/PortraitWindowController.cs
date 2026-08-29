using System.Collections;
using Kirurobo;
using UnityEngine;

namespace DouyinLive
{
    // 竖屏直播模式：把透明桌宠窗口改为竖屏窗口（高=屏幕高，水平居中），
    // 角色自动移到画面中央，字幕切到头顶模式 —— 适配直播伴侣竖屏画布采集。
    // 完全由 settings.json 的 douyinPortraitAspect 驱动（>0 开启）；关闭时窗口/字幕/贴合设置全部还原。
    public class PortraitWindowController : MonoBehaviour
    {
        [Range(0.3f, 1.3f)] public float aspect = 0.75f;      // 窗口宽/高比；3:4 给跳舞走位留余量
        [Range(0.5f, 1f)] public float heightRatio = 0.97f;   // 竖屏窗口高度占屏幕高度比例

        public bool Active { get; private set; }

        UniWindowController uniWin;
        SpeechPipeline speech;

        bool savedFit;
        Vector2 savedPos;
        SpeechPipeline.BubbleAnchor savedAnchor;
        bool desktopRestored;

        void Start()
        {
            Resolve();
        }

        // DouyinLiveManager 会在自己的 Start() 里 AddComponent 本组件再立刻调用，
        // 那时本组件的 Start() 还没跑，所以这里按需解析而不是只在 Start 里赋值。
        void Resolve()
        {
            if (uniWin == null)
                uniWin = UniWindowController.current != null
                    ? UniWindowController.current
                    : FindFirstObjectByType<UniWindowController>();
            if (speech == null) speech = GetComponent<SpeechPipeline>();
        }

        // 关闭竖屏时必须显式改回桌宠尺寸：Unity 会把窗口尺寸存进注册表
        // (HKCU\Software\Shinymoon\MateEngineX\Screenmanager Resolution *)，
        // 下次启动直接按上次的竖屏尺寸开窗，而这一次 Active 一直是 false，
        // ExitPortrait() 的守卫会直接 return，没人把窗口改回来。
        public void RestoreDesktopWindow()
        {
            if (Active) { ExitPortrait(); return; }

            Resolve();
            if (uniWin == null || desktopRestored) return;

            desktopRestored = true;   // 每次运行只强制一次，之后不干扰用户自己调窗口
            uniWin.shouldFitMonitor = false;
            uniWin.windowSize = DesktopWindowSize();
        }

        // 与 AvatarSettingsMenu.RestoreWindowSize() 保持一致的三档桌宠窗口尺寸
        static Vector2 DesktopWindowSize()
        {
            var state = SaveLoadHandler.Instance != null
                ? SaveLoadHandler.Instance.data.windowSizeState
                : SaveLoadHandler.SettingsData.WindowSizeState.Normal;

            switch (state)
            {
                case SaveLoadHandler.SettingsData.WindowSizeState.Small: return new Vector2(768, 512);
                case SaveLoadHandler.SettingsData.WindowSizeState.Big: return new Vector2(2048, 1536);
                default: return new Vector2(1536, 1024);
            }
        }

        public void EnterPortrait()
        {
            Resolve();
            if (Active || uniWin == null) return;

            savedFit = uniWin.shouldFitMonitor;
            savedPos = uniWin.windowPosition;
            if (speech != null) savedAnchor = speech.bubbleAnchor;

            int screenH = Screen.currentResolution.height;
            int screenW = Screen.currentResolution.width;
            int h = Mathf.RoundToInt(screenH * heightRatio);
            int w = Mathf.RoundToInt(h * aspect);

            uniWin.shouldFitMonitor = false;
            uniWin.windowSize = new Vector2(w, h);
            uniWin.windowPosition = new Vector2((screenW - w) / 2f, (screenH - h) / 2f);

            // 竖屏直播：字幕固定在窗口顶部字幕区（跟随头部在窄窗里容易与角色重叠）
            if (speech != null) speech.bubbleAnchor = SpeechPipeline.BubbleAnchor.FixedTop;

            StartCoroutine(CenterAvatar());
            Active = true;
            Debug.Log($"[PortraitWindow] ON {w}x{h} —— 直播伴侣按窗口采集本程序即为竖屏画面");
        }

        public void ExitPortrait()
        {
            if (!Active || uniWin == null) return;

            // 按 windowSizeState 还原，而不是记录"进入竖屏前的尺寸"：如果上次退出时窗口就是竖屏，
            // Unity 会把竖屏尺寸从注册表带回来，那么记下来的"原尺寸"本身就是个竖屏值。
            uniWin.windowSize = DesktopWindowSize();
            uniWin.windowPosition = savedPos;
            uniWin.shouldFitMonitor = savedFit;
            if (speech != null) speech.bubbleAnchor = savedAnchor;

            Active = false;
            desktopRestored = true;
            Debug.Log("[PortraitWindow] OFF —— 已还原桌宠窗口");
        }

        // 窗口变形后视野区域变了，把角色水平移到新画面中央，避免站在画面外
        IEnumerator CenterAvatar()
        {
            yield return null;
            yield return null;   // 等窗口尺寸对 viewport 生效

            var cam = Camera.main;
            var avatar = FindFirstObjectByType<AvatarAnimatorController>();
            if (cam == null || avatar == null) yield break;

            var t = avatar.transform;
            float depth = Mathf.Abs(cam.transform.position.z - t.position.z);
            Vector3 center = cam.ViewportToWorldPoint(new Vector3(0.5f, 0f, depth));
            t.position = new Vector3(center.x, t.position.y, t.position.z);
        }

        void OnDestroy()
        {
            if (Active) ExitPortrait();
        }
    }
}
