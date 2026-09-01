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
        readonly AudienceMemory audience = new AudienceMemory();
        readonly RoomContext room = new RoomContext();
        readonly LiveOpsService liveOps = new LiveOpsService();
        bool audienceLoaded;
        TriggerRouter triggers;
        EffectRegistry triggerEffects;

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
            songService.targetLoudness = Mathf.Clamp(d.douyinSongLoudness, 0.01f, 0.3f);
            reward.Song = songService;

            danmakuAI.Speech = speech;
            danmakuAI.Enabled = d.douyinAIReplyEnabled;
            danmakuAI.MinInterval = d.douyinAIReplyMinInterval;
            danmakuAI.ExtraPersona = d.douyinLivePrompt;
            danmakuAI.Cloud = cloudBackend;
            danmakuAI.Local = localBackend;
            danmakuAI.FallbackToLocal = d.aiFallbackToLocal;
            danmakuAI.Persona = PersonaCard.LoadOrCreate();   // douyin_persona.json
            ContentFilter.Load();                             // douyin_blocked_words.txt
            if (!audienceLoaded) { audience.Load(); audienceLoaded = true; }
            room.Song = songService;
            danmakuAI.Audience = audience;
            danmakuAI.Room = room;
            welcome.Audience = audience;
            liveOps.Speech = speech;

            idleChatter.Speech = speech;
            idleChatter.AI = danmakuAI;
            idleChatter.Song = songService;
            idleChatter.Enabled = d.douyinIdleChatterEnabled;
            idleChatter.IdleThreshold = d.douyinIdleThreshold;
            idleChatter.AutoSongEnabled = d.douyinIdleAutoSongEnabled;
            idleChatter.AutoSongIdleThreshold = d.douyinIdleAutoSongThreshold;
            idleChatter.AutoSongMinInterval = d.douyinIdleAutoSongMinInterval;
            idleChatter.SongList = d.douyinIdleSongList ?? new List<string>();
            idleChatter.AutoDanceEnabled = d.douyinIdleAutoDanceEnabled;

            // 直播期间大头模式保持窗口尺寸不变（窗口突变会导致直播伴侣采集画面裁切错乱）
            if (d.enableDouyinLive)
            {
                var bigScreen = FindFirstObjectByType<AvatarBigScreenHandler>();
                if (bigScreen != null) bigScreen.keepWindowSize = true;
            }

            // 可配置触发层：命中 douyin_triggers.json 的规则就由它接管，
            // 未命中才走下面各 Service 的原有逻辑（旁路式，删配置文件即回退）
            if (triggers == null)
            {
                triggerEffects = GetComponent<EffectRegistry>();
                if (triggerEffects == null) triggerEffects = gameObject.AddComponent<EffectRegistry>();
                triggers = GetComponent<TriggerRouter>();
                if (triggers == null) triggers = gameObject.AddComponent<TriggerRouter>();
            }
            triggers.debugLog = debugLog;
            if (triggerEffects != null) triggerEffects.debugLog = debugLog;

            // DanceDirector 由上面的 TriggerRouter.Awake 挂载创建，只有到这里才能保证它已存在；
            // 放在 idleChatter 那段里会在首次 ApplySettings 时取到 null，导致自动跳舞永远不触发
            var danceDirector = GetComponent<DanceDirector>();
            idleChatter.Dance = danceDirector;
            if (danceDirector != null)
            {
                danceDirector.danceChainCount = Mathf.Max(1, d.douyinDanceChainCount);
                danceDirector.danceParticleTheme = d.douyinDanceParticleTheme;
                danceDirector.portraitSoftZoneRatio = Mathf.Clamp(d.douyinDancePortraitSoftZoneRatio, 0.05f, 0.4f);
            }

            // 竖屏直播窗口：完全由 douyinPortraitAspect 决定，>0 开启、<=0 保持普通窗口
            var portrait = GetComponent<PortraitWindowController>();
            if (portrait == null) portrait = gameObject.AddComponent<PortraitWindowController>();
            bool wantPortrait = d.enableDouyinLive && d.douyinPortraitAspect > 0f;
            if (wantPortrait)
            {
                float aspect = Mathf.Clamp(d.douyinPortraitAspect, 0.3f, 1.3f);
                bool aspectChanged = Mathf.Abs(portrait.aspect - aspect) > 0.01f;
                portrait.aspect = aspect;
                if (portrait.Active && aspectChanged) portrait.ExitPortrait();  // 比例变了重进
                if (!portrait.Active) portrait.EnterPortrait();
            }
            else
                portrait.RestoreDesktopWindow();   // 上次竖屏的窗口尺寸会被 Unity 记住，必须显式改回来

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
            room.ResetSession();
            liveOps.ResetSession();
            like.ResetSession();
            danmakuAI.ResetSession();
            idleChatter.ResetSession();
            if (triggers != null) triggers.ResetSession();
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
                if (triggers != null) triggers.Tick();
                welcome.Tick();
                danmakuAI.Tick();
                idleChatter.Tick();
                liveOps.Tick();
            }
            audience.SaveIfDirty();
        }

        void Route(DouyinEvent ev)
        {
            if (debugLog) Debug.Log($"[DouyinLive] {ev.Type} {ev.Nickname}: {ev.Content}{ev.GiftName}");
            idleChatter.NotifyInteraction();   // 任何观众事件都重置冷场计时

            // 观众记忆/房间上下文要在触发层之前记账：即使这条弹幕被规则消费掉，
            // 它也应该计入观众画像，否则 AI 回复会丢失上下文。
            if (ev.Type == DouyinMsgType.Chat)
            {
                audience.RecordMessage(ev.UserId, ev.Nickname, ev.Content);
                room.AddChat(ev.Nickname, ev.Content);
            }
            else if (ev.Type == DouyinMsgType.Gift)
            {
                danmakuAI.MarkGifter(ev.UserId);
                int value = Mathf.Max(1, ev.DiamondCount) * Mathf.Max(1, ev.GiftCount);
                audience.RecordGift(ev.UserId, ev.Nickname, value);
                room.LastGiftDesc = $"{ev.Nickname}送的{ev.GiftName}";
                liveOps.RecordGift(ev.UserId, ev.Nickname, value);
            }
            else if (ev.Type == DouyinMsgType.Like)
            {
                like.RecordOnly(ev);   // 会话点赞总数不能因为被触发规则消费而漏计
            }

            if (triggers != null && triggers.TryHandle(ev)) return;

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
                case DouyinMsgType.Share:
                case DouyinMsgType.FansClub:
                    welcome.OnEvent(ev);
                    break;
                case DouyinMsgType.Follow:
                    welcome.OnEvent(ev);
                    TriggerBigHeadMoment();   // 关注 → 大头特写致谢
                    break;
                case DouyinMsgType.Gift:
                    reward.OnGift(ev);
                    TriggerBigHeadMoment();   // 礼物 → 大头特写致谢
                    break;
            }
        }

        // ---------- 大头特写：关注/礼物时镜头推到脸部说感谢，说完恢复 ----------

        bool bigHeadBusy;

        public void TriggerBigHeadMoment()
        {
            var d = SaveLoadHandler.Instance != null ? SaveLoadHandler.Instance.data : null;
            if (d == null || !d.douyinBigHeadReaction) return;
            if (bigHeadBusy) return;
            // 唱歌/跳舞时不抢镜
            if (songService != null && songService.IsPlaying) return;
            StartCoroutine(BigHeadMoment());
        }

        System.Collections.IEnumerator BigHeadMoment()
        {
            var handler = FindFirstObjectByType<AvatarBigScreenHandler>();
            if (handler == null || handler.IsBigScreenActive) yield break;

            bigHeadBusy = true;
            handler.keepWindowSize = true;   // 直播采集时窗口尺寸不能变
            handler.SetBigScreen(true);

            // 等感谢语音说完（1秒起步，最长12秒兜底）
            yield return new WaitForSeconds(1f);
            float t = 0f;
            while (t < 12f && speech != null && (speech.IsSpeaking || speech.QueueCount > 0))
            {
                t += Time.deltaTime;
                yield return null;
            }
            yield return new WaitForSeconds(1.2f);

            handler.SetBigScreen(false);
            bigHeadBusy = false;
        }

        // 供 EffectRegistry 的 swapAvatar 效果调用
        public void SwapAvatarFromTrigger(string userName)
        {
            reward.SwitchRandomAvatar(userName);
        }

        // 供 EffectRegistry 的 sayAI: 效果使用
        public void GenerateFromTrigger(string prompt, System.Action<string> onDone)
        {
            danmakuAI.GenerateOneShot(prompt, onDone);
        }

        // L2 动作会打断闲聊的暖场话（唱歌/跳舞不受影响）
        public void InterruptIdleChatter()
        {
            idleChatter.NotifyInteraction();
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
            audience.SaveIfDirty(force: true);
        }

        // ---------- 换角色后身高归一化：新模型缩放到与上一个角色相同的显示高度 ----------

        // 必须在触发 LoadVRM 之前调用（先记录当前角色的高度基准）
        public void NormalizeNextAvatarHeight()
        {
            StartCoroutine(NormalizeAvatarRoutine());
        }

        System.Collections.IEnumerator NormalizeAvatarRoutine()
        {
            var loader = FindFirstObjectByType<VRMLoader>();
            if (loader == null) yield break;

            var prev = loader.GetCurrentModel();
            float prevHeight = MeasureWorldHeight(prev);
            if (prevHeight <= 0.05f) yield break;   // 没有可靠基准就不干预

            // 等新模型实例出现（异步加载，最长等30秒）
            GameObject cur = prev;
            float t = 0f;
            while (t < 30f)
            {
                cur = loader.GetCurrentModel();
                if (cur != null && cur != prev && cur.activeInHierarchy) break;
                t += Time.deltaTime;
                yield return null;
            }
            if (cur == null || cur == prev) yield break;

            yield return new WaitForSeconds(0.5f);  // 等模型初始化/全局缩放设置生效

            float newHeight = MeasureWorldHeight(cur);
            if (newHeight <= 0.05f) yield break;
            float factor = Mathf.Clamp(prevHeight / newHeight, 0.2f, 5f);
            if (Mathf.Abs(factor - 1f) < 0.05f) yield break;   // 差异5%以内不折腾

            cur.transform.localScale *= factor;
            Debug.Log($"[DouyinLive] 换角色身高归一化: {newHeight:F2}m -> {prevHeight:F2}m (缩放 x{factor:F2})");
        }

        static float MeasureWorldHeight(GameObject go)
        {
            if (go == null) return 0f;
            var renderers = go.GetComponentsInChildren<Renderer>(false);
            bool has = false;
            Bounds b = default;
            foreach (var r in renderers)
            {
                if (r is ParticleSystemRenderer) continue;
                if (!has) { b = r.bounds; has = true; }
                else b.Encapsulate(r.bounds);
            }
            return has ? b.size.y : 0f;
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
