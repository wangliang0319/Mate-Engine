using System.Collections;
using Kirurobo;
using UnityEngine;

namespace DouyinLive
{
    // 竖屏直播模式：把透明桌宠窗口改为 9:16 竖屏窗口（高=屏幕高，水平居中），
    // 角色自动移到画面中央，字幕切到头顶模式 —— 适配直播伴侣竖屏画布采集。
    // F8 随时切换；退出时窗口/字幕/贴合设置全部还原。
    public class PortraitWindowController : MonoBehaviour
    {
        public KeyCode hotkey = KeyCode.F10;   // F8 被原项目 MEValueChanger 占用
        [Range(0.3f, 1f)] public float aspect = 9f / 16f;
        [Range(0.5f, 1f)] public float heightRatio = 0.97f;   // 竖屏窗口高度占屏幕高度比例

        public bool Active { get; private set; }

        UniWindowController uniWin;
        SpeechPipeline speech;

        bool savedFit;
        Vector2 savedSize, savedPos;
        SpeechPipeline.BubbleAnchor savedAnchor;

        void Start()
        {
            uniWin = UniWindowController.current != null
                ? UniWindowController.current
                : FindFirstObjectByType<UniWindowController>();
            speech = GetComponent<SpeechPipeline>();
        }

        void Update()
        {
            if (Input.GetKeyDown(hotkey)) Toggle();
        }

        public void Toggle()
        {
            if (Active) ExitPortrait();
            else EnterPortrait();
        }

        public void EnterPortrait()
        {
            if (Active || uniWin == null) return;

            savedFit = uniWin.shouldFitMonitor;
            savedSize = uniWin.windowSize;
            savedPos = uniWin.windowPosition;
            if (speech != null) savedAnchor = speech.bubbleAnchor;

            int screenH = Screen.currentResolution.height;
            int screenW = Screen.currentResolution.width;
            int h = Mathf.RoundToInt(screenH * heightRatio);
            int w = Mathf.RoundToInt(h * aspect);

            uniWin.shouldFitMonitor = false;
            uniWin.windowSize = new Vector2(w, h);
            uniWin.windowPosition = new Vector2((screenW - w) / 2f, (screenH - h) / 2f);

            // 窄窗口里侧边字幕会被裁掉，切到头顶模式
            if (speech != null) speech.bubbleAnchor = SpeechPipeline.BubbleAnchor.Above;

            StartCoroutine(CenterAvatar());
            Active = true;
            Debug.Log($"[PortraitWindow] ON {w}x{h} —— 直播伴侣按窗口采集本程序即为竖屏画面，F8 退出");
        }

        public void ExitPortrait()
        {
            if (!Active || uniWin == null) return;

            uniWin.windowSize = savedSize;
            uniWin.windowPosition = savedPos;
            uniWin.shouldFitMonitor = savedFit;
            if (speech != null) speech.bubbleAnchor = savedAnchor;

            Active = false;
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
