using UnityEngine;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using System;

public class SaveLoadHandler : MonoBehaviour
{
    public static SaveLoadHandler Instance { get; private set; }

    public SettingsData data;

    // Multi-Instance Variablen
    private static string fileName = "settings.json";
    private static string customDataDir = null;

    private string BaseDir => string.IsNullOrEmpty(customDataDir)
        ? Application.persistentDataPath
        : Path.Combine(Application.persistentDataPath, customDataDir);

    private string FilePath => Path.Combine(BaseDir, fileName);

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Kommandozeilen-Argumente lesen
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--savefile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                fileName = args[i + 1].Trim('"');

            if (args[i].Equals("--datadir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                customDataDir = args[i + 1].Trim('"');
        }

        LoadFromDisk();
        ApplyAllSettingsToAllAvatars();

        var theme = FindFirstObjectByType<ThemeManager>();
        if (theme != null)
        {
            theme.SetHue(data.uiHueShift);
            theme.SetSaturation(data.uiSaturation);
        }


        var limiters = FindObjectsByType<FPSLimiter>(FindObjectsSortMode.None);
        foreach (var limiter in limiters)
        {
            limiter.targetFPS = data.fpsLimit;
            limiter.ApplyFPSLimit();
        }
    }

    // Speichern
    public void SaveToDisk()
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            File.WriteAllText(FilePath, json);
            Debug.Log("[SaveLoadHandler] Saved settings to: " + FilePath);
        }
        catch (Exception e)
        {
            Debug.LogError("[SaveLoadHandler] Failed to save: " + e);
        }
    }

    // Json.NET reuses collections created by the field initializers and APPENDS the values from
    // disk (ObjectCreationHandling.Auto). Any list with a non-empty default (douyinIdleSongList)
    // would therefore grow by its default count on every launch. Replace = always start fresh.
    private static readonly JsonSerializerSettings LoadSettings = new()
    {
        ObjectCreationHandling = ObjectCreationHandling.Replace
    };

    // Laden
    public void LoadFromDisk()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                string json = File.ReadAllText(FilePath);
                data = JsonConvert.DeserializeObject<SettingsData>(json, LoadSettings);
            }
            catch
            {
                data = new SettingsData();
            }
        }
        else
        {
            data = new SettingsData();
        }
        MigrateAfterLoad();
    }


    [Serializable]
    public class SettingsData
    {
        public enum WindowSizeState { Normal, Big, Small }
        public WindowSizeState windowSizeState = WindowSizeState.Normal;

        public float soundThreshold = 0.2f;
        public float idleSwitchTime = 10f;
        public float idleTransitionTime = 1f;
        public bool enableDanceSwitch = false;
        public float danceSwitchTime = 15f;
        public float danceTransitionTime = 2f;
        public float avatarSize = 1.0f;
        public bool enableDancing = true;
        public bool enableMouseTracking = true;
        public int fpsLimit = 90;
        public bool isTopmost = true;

        public List<string> allowedApps = new();
        public bool bloom = false;
        public bool dayNight = true;

        public bool enableParticles = true;
        public float petVolume = 1f;
        public float effectsVolume = 1f;
        public float menuVolume = 1f;

        public float headBlend = 0.7f;
        public float eyeBlend = 1f;
        public float spineBlend = 0.5f;

        public bool enableHandHolding = true;
        public bool enableWindowSitting = false;
        public bool ambientOcclusion = false;

        public float uiHueShift = 0f;
        public float uiSaturation = 1.0f;

        public bool enableDiscordRPC = true;

        public bool tutorialDone = false;

        public string selectedLocaleCode = "en";
        public bool enableIK = true;

        public int bigScreenScreenSaverTimeoutIndex = 0;
        public bool bigScreenScreenSaverEnabled = false;
        public float windowSitYOffset = 0f;

        public Dictionary<string, float> lightIntensities = new();
        public Dictionary<string, float> lightSaturations = new();
        public Dictionary<string, float> lightHues = new();
        public Dictionary<string, bool> groupToggles = new();

        public Dictionary<string, bool> modStates = new();
        public int graphicsQualityLevel = 1;
        public Dictionary<string, bool> accessoryStates = new();

        public bool startWithWindows = false;
        public bool enableRandomMessages = false;

        public string selectedModelPath = "";
        public int contextLength = 4096;
        public bool enableHusbandoMode = false;
        public bool enableAutoMemoryTrim = false;

        public int settingsVersion = 0;
        public bool alarmsEnabled = true;
        public bool enableMinecraftMessages = false;

        public string selectedParticleTheme = "Standard";
        public bool enableFeedSystem = false;
        public bool enableRandomAvatar = false;

        public bool enableLocomotion = false;

        // ---------- Douyin Live ----------
        public bool enableDouyinLive = false;
        public string douyinWsUrl = "ws://127.0.0.1:8888";
        public bool douyinWelcomeEnabled = true;
        public bool douyinAIReplyEnabled = true;
        public bool douyinLikeReactEnabled = true;
        public bool douyinGiftEnabled = true;
        public float douyinWelcomeCooldown = 8f;
        public float douyinAIReplyMinInterval = 8f;
        public int douyinLikeThreshold = 100;
        public string douyinLivePrompt = "";
        public bool douyinIdleChatterEnabled = true;
        public float douyinIdleThreshold = 90f;
        public bool douyinBigHeadReaction = true;   // 关注/礼物时大头特写致谢
        // 竖屏直播窗口宽高比(宽/高)：>0 开启竖屏窗口，<=0 保持普通桌宠窗口；跳舞走位多可调大
        public float douyinPortraitAspect = 0.75f;
        public bool douyinIdleAutoSongEnabled = true;
        public float douyinIdleAutoSongThreshold = 300f;
        public List<string> douyinIdleSongList = new()
        {
            "赤伶", "游山恋", "探窗", "辞九门回忆", "半生雪", "踏山河",
            "燕无歇", "牵丝戏", "红昭愿", "芒种", "不谓侠", "琵琶行",
            "大鱼", "典狱司", "精卫", "画离弦", "殊途", "山外小楼夜听雨"
        };

        // Cloud AI (OpenAI 兼容)
        public string aiBaseUrl = "";
        public string aiApiKey = "";
        public string aiModel = "";
        public bool aiFallbackToLocal = true;

        // TTS: 0=OpenAI兼容 1=EdgeTTS 2=Local(预留) 3=关闭(纯气泡)
        public int ttsProvider = 1;
        public string ttsBaseUrl = "";
        public string ttsApiKey = "";
        public string ttsModel = "";
        public string ttsVoice = "";
        public string ttsEdgeVoice = "zh-CN-XiaoxiaoNeural";
        public string ttsInstructions = "";
        public float ttsVolume = 1f;
        public float ttsSpeed = 1f;
        public float lipSyncGain = 1f;

        //ALARM
        [Serializable]
        public class AlarmEntry
        {
            public string id;
            public bool enabled;
            public int hour;
            public int minute;
            public byte daysMask;
            public string text;
            public long lastTriggeredUnixMinute;
        }

        public List<AlarmEntry> alarms = new List<AlarmEntry>();

        //Timer
        [Serializable]
        public class TimerEntry
        {
            public string id;
            public bool enabled;
            public int hours;
            public int minutes;
            public int presetSeconds;
            public bool running;
            public long targetUnix;
            public string text;
        }

        public List<TimerEntry> timers = new List<TimerEntry>();


    }
    //ALARM
    void MigrateAfterLoad()
    {
        if (data == null) data = new SettingsData();
        if (data.timers == null) data.timers = new List<SettingsData.TimerEntry>();
        if (string.IsNullOrEmpty(data.selectedParticleTheme)) data.selectedParticleTheme = "Standard";
        if (data.alarms == null) data.alarms = new List<SettingsData.AlarmEntry>();

        int versionBefore = data.settingsVersion;
        if (data.settingsVersion < 1)
            data.settingsVersion = 1;
        bool changed = data.settingsVersion != versionBefore;

        // 老版本每次启动都会把默认歌单再追加一遍（原因见 LoadSettings 注释），已存盘的重复项在此清掉。
        // 故意不挂 settingsVersion 门槛：历史构建写出过 v2/v3 而当前源码没有对应迁移，
        // 版本号不可靠，按内容判断才稳。
        var songs = DedupeStrings(data.douyinIdleSongList);
        if (data.douyinIdleSongList == null || songs.Count != data.douyinIdleSongList.Count)
        {
            data.douyinIdleSongList = songs;
            changed = true;
        }

        if (changed) SaveToDisk();
    }

    static List<string> DedupeStrings(List<string> source)
    {
        var result = new List<string>();
        if (source == null) return result;

        var seen = new HashSet<string>();
        foreach (var item in source)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            string name = item.Trim();
            if (seen.Add(name)) result.Add(name);
        }
        return result;
    }

    public static void SyncAllowedAppsToAllAvatars()
    {
        var allAvatars = Resources.FindObjectsOfTypeAll<AvatarAnimatorController>();
        var list = new List<string>(Instance.data.allowedApps);

        foreach (var avatar in allAvatars)
            avatar.allowedApps = list;
    }

    public static void ApplyAllSettingsToAllAvatars()
    {
        var data = Instance.data;
        var avatars = Resources.FindObjectsOfTypeAll<AvatarAnimatorController>();

        foreach (var avatar in avatars)
        {
            avatar.SOUND_THRESHOLD = data.soundThreshold;
            avatar.IDLE_SWITCH_TIME = data.idleSwitchTime;
            avatar.IDLE_TRANSITION_TIME = data.idleTransitionTime;
            avatar.enableDancing = data.enableDancing;
            avatar.allowedApps = new List<string>(data.allowedApps);
            avatar.transform.localScale = Vector3.one * data.avatarSize;
            avatar.DANCE_SWITCH_TIME = data.danceSwitchTime;
            avatar.DANCE_TRANSITION_TIME = data.danceTransitionTime;
            avatar.enableDanceSwitch = data.enableDanceSwitch;
            avatar.enableHusbandoMode = data.enableHusbandoMode;

            foreach (var tracker in avatar.GetComponentsInChildren<AvatarMouseTracking>(true))
            {
                tracker.enableMouseTracking = data.enableMouseTracking;
                tracker.headBlend = data.headBlend;
                tracker.spineBlend = data.spineBlend;
                tracker.eyeBlend = data.eyeBlend;
            }

            foreach (var ik in avatar.GetComponentsInChildren<IKFix>(true))
                ik.enableIK = data.enableIK;

            foreach (var handler in avatar.GetComponentsInChildren<AvatarParticleHandler>(true))
            {
                handler.featureEnabled = data.enableParticles;
                handler.enabled = data.enableParticles;
                handler.selectedTheme = data.selectedParticleTheme;
                try { handler.SetTheme(data.selectedParticleTheme); } catch { }
            }

            foreach (var holder in avatar.GetComponentsInChildren<HandHolder>(true))
                holder.enableHandHolding = data.enableHandHolding;

            if (avatar.animator != null &&
                avatar.animator.isActiveAndEnabled &&
                avatar.animator.runtimeAnimatorController != null)
            {
                avatar.animator.SetBool("isDancing", false);
                avatar.animator.SetBool("isDragging", false);
                avatar.isDancing = false;
                avatar.isDragging = false;
            }

            foreach (var food in Resources.FindObjectsOfTypeAll<AvatarFoodController>())
                food.SetFeatureEnabled(Instance.data.enableFeedSystem);

            foreach (var handler in Resources.FindObjectsOfTypeAll<AvatarWindowHandler>())
                handler.windowSitYOffset = data.windowSitYOffset;

            foreach (var loco in Resources.FindObjectsOfTypeAll<AvatarLocomotionController>())
                loco.EnableLocomotion = data.enableLocomotion;

        }
    }
}
