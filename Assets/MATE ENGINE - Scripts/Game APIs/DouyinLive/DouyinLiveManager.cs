using System;
using System.Collections.Generic;
using System.IO;
using LLMUnity;
using Newtonsoft.Json;
using UnityEngine;

namespace DouyinLive
{
    // 抖音直播互动总管理器：挂在场景常驻对象上。
    // 连接 DouyinBarrageGrab → 分发事件到各业务服务 → SpeechPipeline 输出。
    [RequireComponent(typeof(SpeechPipeline))]
    public class DouyinLiveManager : MonoBehaviour
    {
        public static DouyinLiveManager Instance { get; private set; }

        [Header("Debug")]
        public bool debugLog = false;

        [Header("Local LLM Fallback (optional)")]
        public LLMCharacter localCharacter;

        [Header("Gating")]
        public List<GameObject> blockObjects = new List<GameObject>();

        SpeechPipeline speech;
        DouyinLiveClient client;
        SongService songService;

        readonly WelcomeService welcome = new WelcomeService();
        readonly LikeService like = new LikeService();
        readonly RewardService reward = new RewardService();
        readonly DanmakuAIService danmakuAI = new DanmakuAIService();
        readonly IdleChatterService idleChatter = new IdleChatterService();

        CloudChatBackend cloudBackend;
        LocalChatBackend localBackend;
        OpenAICompatTTS cloudTTS;
        EdgeTTSProvider edgeTTS;

        bool running;

        public DouyinLiveClient.State ConnectionState =>
            client != null ? client.ConnectionState : DouyinLiveClient.State.Stopped;
        public long SessionLikes => like.SessionTotal;
        public int SessionReplies => danmakuAI.RepliedCount;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            speech = GetComponent<SpeechPipeline>();
        }

        void Start()
        {
            ApplySettings();
        }

        // 设置页改动后调用；也在启动时调用
        public void ApplySettings()
        {
            var d = SaveLoadHandler.Instance != null ? SaveLoadHandler.Instance.data : null;
            if (d == null) return;

            // AI 后端
            cloudBackend ??= new CloudChatBackend();
            cloudBackend.BaseUrl = d.aiBaseUrl;
            cloudBackend.ApiKey = d.aiApiKey;
            cloudBackend.Model = d.aiModel;

            localBackend ??= new LocalChatBackend();
            localBackend.Character = localCharacter != null ? localCharacter : FindFirstObjectByType<LLMCharacter>();

            // TTS
            cloudTTS ??= new OpenAICompatTTS();
            cloudTTS.BaseUrl = string.IsNullOrWhiteSpace(d.ttsBaseUrl) ? d.aiBaseUrl : d.ttsBaseUrl;
            cloudTTS.ApiKey = string.IsNullOrWhiteSpace(d.ttsApiKey) ? d.aiApiKey : d.ttsApiKey;
            if (!string.IsNullOrWhiteSpace(d.ttsModel)) cloudTTS.Model = d.ttsModel;
            if (!string.IsNullOrWhiteSpace(d.ttsVoice)) cloudTTS.Voice = d.ttsVoice;
            cloudTTS.Speed = d.ttsSpeed;
            cloudTTS.Instructions = string.IsNullOrWhiteSpace(d.ttsInstructions)
                ? "你是中国的甜美女主播，说地道的标准普通话，语气活泼亲切自然，绝对不要有外国口音"
                : d.ttsInstructions;

            edgeTTS ??= new EdgeTTSProvider();
            if (!string.IsNullOrWhiteSpace(d.ttsEdgeVoice)) edgeTTS.Voice = d.ttsEdgeVoice;

            speech.TTSEnabled = d.ttsProvider != 3;
            speech.Provider = d.ttsProvider == 1 ? (ITTSProvider)edgeTTS : cloudTTS;
            speech.FallbackProvider = edgeTTS;
            speech.volume = d.ttsVolume;
            speech.lipSyncGain = d.lipSyncGain;

            // 服务
            welcome.Speech = speech;
            welcome.Enabled = d.douyinWelcomeEnabled;
            welcome.Cooldown = d.douyinWelcomeCooldown;

            like.Speech = speech;
            like.Enabled = d.douyinLikeReactEnabled;
            like.Threshold = Mathf.Max(10, d.douyinLikeThreshold);

            reward.Speech = speech;
            reward.Enabled = d.douyinGiftEnabled;
            reward.Rules = LoadGiftRules();
            if (songService == null)
            {
                songService = GetComponent<SongService>();
                if (songService == null) songService = gameObject.AddComponent<SongService>();
            }
            songService.Speech = speech;
            reward.Song = songService;

            danmakuAI.Speech = speech;
            danmakuAI.Enabled = d.douyinAIReplyEnabled;
            danmakuAI.MinInterval = d.douyinAIReplyMinInterval;
            danmakuAI.ExtraPersona = d.douyinLivePrompt;
            danmakuAI.Cloud = cloudBackend;
            danmakuAI.Local = localBackend;
            danmakuAI.FallbackToLocal = d.aiFallbackToLocal;

            idleChatter.Speech = speech;
            idleChatter.AI = danmakuAI;
            idleChatter.Song = songService;
            idleChatter.Enabled = d.douyinIdleChatterEnabled;
            idleChatter.IdleThreshold = d.douyinIdleThreshold;
            idleChatter.AutoSongEnabled = d.douyinIdleAutoSongEnabled;
            idleChatter.AutoSongIdleThreshold = d.douyinIdleAutoSongThreshold;
            idleChatter.SongList = d.douyinIdleSongList ?? new List<string>();

            // 连接
            bool shouldRun = d.enableDouyinLive;
            if (shouldRun && !running) StartLive(d.douyinWsUrl);
            else if (!shouldRun && running) StopLive();
            else if (running && client != null && client.Url != d.douyinWsUrl)
            {
                StopLive();
                StartLive(d.douyinWsUrl);
            }
        }

        void StartLive(string url)
        {
            client = new DouyinLiveClient { Url = url, DebugLog = debugLog };
            client.Start();
            running = true;
            welcome.ResetSession();
            like.ResetSession();
            danmakuAI.ResetSession();
            idleChatter.ResetSession();
            if (debugLog) Debug.Log("[DouyinLive] Started, connecting " + url);
        }

        void StopLive()
        {
            client?.Stop();
            client = null;
            running = false;
            speech.ClearQueue();
        }

        void Update()
        {
            MainThreadDispatcher.Drain();
            if (!running || client == null) return;

            bool blocked = IsBlocked();
            int drained = 0;
            while (drained++ < 20 && client.TryDequeue(out var ev))
            {
                if (blocked) continue; // 菜单打开等场景：消费但不响应
                Route(ev);
            }

            if (!blocked)
            {
                welcome.Tick();
                danmakuAI.Tick();
                idleChatter.Tick();
            }
        }

        void Route(DouyinEvent ev)
        {
            if (debugLog) Debug.Log($"[DouyinLive] {ev.Type} {ev.Nickname}: {ev.Content}{ev.GiftName}");
            idleChatter.NotifyInteraction();   // 任何观众事件都重置冷场计时
            switch (ev.Type)
            {
                case DouyinMsgType.Chat:
                    if (reward.TryHandleDanmaku(ev)) return;
                    danmakuAI.OnDanmaku(ev);
                    break;
                case DouyinMsgType.Like:
                    like.OnEvent(ev);
                    break;
                case DouyinMsgType.Enter:
                case DouyinMsgType.Follow:
                case DouyinMsgType.Share:
                case DouyinMsgType.FansClub:
                    welcome.OnEvent(ev);
                    break;
                case DouyinMsgType.Gift:
                    danmakuAI.MarkGifter(ev.UserId);
                    reward.OnGift(ev);
                    break;
            }
        }

        bool IsBlocked()
        {
            foreach (var go in blockObjects)
                if (go != null && go.activeInHierarchy) return true;
            return false;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            client?.Stop();
        }

        // ---------- 礼物规则文件 ----------

        static string GiftRulesPath => Path.Combine(Application.persistentDataPath, "douyin_gift_rules.json");

        public static List<GiftRuleData> LoadGiftRules()
        {
            try
            {
                if (File.Exists(GiftRulesPath))
                    return JsonConvert.DeserializeObject<List<GiftRuleData>>(File.ReadAllText(GiftRulesPath))
                           ?? DefaultGiftRules();
            }
            catch (Exception ex) { Debug.LogWarning("[DouyinLive] Load gift rules failed: " + ex.Message); }
            var def = DefaultGiftRules();
            SaveGiftRules(def);
            return def;
        }

        public static void SaveGiftRules(List<GiftRuleData> rules)
        {
            try { File.WriteAllText(GiftRulesPath, JsonConvert.SerializeObject(rules, Formatting.Indented)); }
            catch (Exception ex) { Debug.LogWarning("[DouyinLive] Save gift rules failed: " + ex.Message); }
        }

        static List<GiftRuleData> DefaultGiftRules() => new List<GiftRuleData>
        {
            new GiftRuleData { giftName = "", minDiamond = 0, minCount = 1, action = "thanks" },
            new GiftRuleData { giftName = "", minDiamond = 10, minCount = 1, action = "randomDance" },
        };

        // ---------- 供设置页使用 ----------

        public void SpeakTest(string text)
        {
            ApplySettings();
            speech.Enqueue(string.IsNullOrWhiteSpace(text) ? "你好呀，我是你的桌宠，语音测试成功啦！" : text,
                SpeechPipeline.Priority.GiftThanks, 30f);
        }
    }
}
