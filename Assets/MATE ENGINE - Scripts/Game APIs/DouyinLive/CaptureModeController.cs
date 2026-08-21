using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Kirurobo;
using UnityEngine;

namespace DouyinLive
{
    // 直播采集模式：把透明桌宠窗口临时切换为"标准实体窗口 + 绿幕背景"，
    // 让抖音直播伴侣能以【窗口采集】方式捕获本程序，
    // 在伴侣中对该源添加【色度键(绿幕)】滤镜即可抠出透明桌宠。
    // 快捷键 F9 切换；F10 循环窗口尺寸。
    public class CaptureModeController : MonoBehaviour
    {
        [Header("Toggle")]
        public KeyCode hotkey = KeyCode.F9;
        public KeyCode sizeHotkey = KeyCode.F10;

        [Header("Chroma Key")]
        public Color greenScreen = new Color(0f, 0.85f, 0f, 1f);
        public Color blueScreen = new Color(0f, 0.2f, 0.9f, 1f);
        public bool useBlue = false;     // 角色带绿色元素时切蓝幕

        [Header("Capture Window (F10 循环切换)")]
        public Vector2[] sizePresets =
        {
            new Vector2(1280, 720),    // 横屏 720p（默认）
            new Vector2(1920, 1080),   // 横屏 1080p
            new Vector2(720, 1280),    // 竖屏
            new Vector2(800, 800),     // 方形小窗
        };
        public int sizeIndex = 0;

        public bool CaptureMode { get; private set; }

        UniWindowController uniWin;
        Camera mainCam;

        bool savedTransparent, savedClickThrough, savedTopmost;
        Vector2 savedPos, savedSize;
        CameraClearFlags savedClearFlags;
        Color savedBg;

        // ---------- Win32：把窗口改成标准可采集样式 ----------

        const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000,
                   WS_MINIMIZEBOX = 0x00020000, WS_SYSMENU = 0x00080000;
        const long WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020,
                   WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000,
                   WS_EX_APPWINDOW = 0x00040000;
        const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_FRAMECHANGED = 0x20;

        [DllImport("user32.dll")] static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        static IntPtr myHwnd = IntPtr.Zero;

        static IntPtr FindMyWindow()
        {
            if (myHwnd != IntPtr.Zero) return myHwnd;
            uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == myPid && IsWindowVisible(h)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            myHwnd = found;
            return found;
        }

        // 清掉所有"隐身"扩展样式 + 加上标准窗口边框，采集软件才能枚举到
        static void MakeWindowCapturable(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            ex &= ~(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            ex |= WS_EX_APPWINDOW;                 // 出现在任务栏/Alt-Tab/采集列表
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex);

            long st = GetWindowLongPtr(hwnd, GWL_STYLE);
            st |= WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_SYSMENU;  // 标准标题栏窗口
            SetWindowLongPtr(hwnd, GWL_STYLE, st);

            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        // ---------- 生命周期 ----------

        void Start()
        {
            uniWin = UniWindowController.current != null
                ? UniWindowController.current
                : FindFirstObjectByType<UniWindowController>();
            mainCam = Camera.main;
        }

        void Update()
        {
            if (Input.GetKeyDown(hotkey)) Toggle();
            if (CaptureMode && Input.GetKeyDown(sizeHotkey)) CycleSize();
        }

        public void Toggle()
        {
            if (CaptureMode) ExitCaptureMode();
            else EnterCaptureMode();
        }

        void CycleSize()
        {
            sizeIndex = (sizeIndex + 1) % sizePresets.Length;
            if (uniWin != null) uniWin.windowSize = sizePresets[sizeIndex];
            Debug.Log($"[CaptureMode] size -> {sizePresets[sizeIndex]}");
        }

        public void EnterCaptureMode()
        {
            if (CaptureMode || uniWin == null) return;
            if (mainCam == null) mainCam = Camera.main;

            savedTransparent = uniWin.isTransparent;
            savedClickThrough = uniWin.isClickThrough;
            savedTopmost = uniWin.isTopmost;
            savedPos = uniWin.windowPosition;
            savedSize = uniWin.windowSize;
            if (mainCam != null)
            {
                savedClearFlags = mainCam.clearFlags;
                savedBg = mainCam.backgroundColor;
            }

            uniWin.isClickThrough = false;
            uniWin.isTransparent = false;
            uniWin.isTopmost = false;
            sizeIndex = Mathf.Clamp(sizeIndex, 0, sizePresets.Length - 1);
            uniWin.windowSize = sizePresets[sizeIndex];
            uniWin.windowPosition = new Vector2(120, 120);   // 移到可见位置

            // 关键：Win32 强制标准窗口样式（UniWinC 只关透明还残留隐身样式）
            MakeWindowCapturable(FindMyWindow());

            if (mainCam != null)
            {
                mainCam.clearFlags = CameraClearFlags.SolidColor;
                mainCam.backgroundColor = useBlue ? blueScreen : greenScreen;
            }

            CaptureMode = true;
            Debug.Log("[CaptureMode] ON - 直播伴侣添加[窗口]素材选本程序，加色度键滤镜抠图。F10 切换尺寸");
        }

        public void ExitCaptureMode()
        {
            if (!CaptureMode || uniWin == null) return;

            // UniWinC 重设透明时会重建自己需要的窗口样式，无需手动还原 Win32 样式
            uniWin.isTransparent = savedTransparent;
            uniWin.isClickThrough = savedClickThrough;
            uniWin.isTopmost = savedTopmost;
            uniWin.windowPosition = savedPos;
            uniWin.windowSize = savedSize;
            if (mainCam != null)
            {
                mainCam.clearFlags = savedClearFlags;
                mainCam.backgroundColor = savedBg;
            }

            CaptureMode = false;
            Debug.Log("[CaptureMode] OFF - 已恢复桌宠形态");
        }

        void OnDestroy()
        {
            if (CaptureMode) ExitCaptureMode();
        }
    }
}
