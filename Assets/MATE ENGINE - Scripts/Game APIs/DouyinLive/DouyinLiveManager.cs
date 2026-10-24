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

        [Header("Chat Mode")]
        // 直播中途切换 AI聊天/游戏陪播。默认 F9：与现有 B/F1/F8 无冲突，
        // 也不在 Unity 的 Input 模拟按键之列
        public KeyCode chatModeKey = KeyCode.F9;

        SpeechPipeline speech;
        DouyinLiveClient client;
        SongService songService;
        LiveMotionDirector motionDirector;

        readonly WelcomeService welcome = new WelcomeService();
        readonly LikeService like = new LikeService();
        readonly RewardService reward = new RewardService();
        readonly DanmakuAIService danmakuAI = new DanmakuAIService();
        readonly DanmakuReader reader = new DanmakuReader();
        readonly IdleChatterService idleChatter = new IdleChatterService();
        readonly AudienceMemory audience = new AudienceMemory();
        readonly RoomContext room = new RoomContext();
        readonly LiveOpsService liveOps = new LiveOpsService();
        bool audienceLoaded;
        TriggerRouter triggers;
        EffectRegistry triggerEffects;
        readonly IntentResolver intents = new IntentResolver();

        CloudChatBackend cloudBackend;
        LocalChatBackend localBackend;
        OpenAICompatTTS cloudTTS;
        EdgeTTSProvider edgeTTS;
        CloudASRProvider asrProvider;
        VoiceCommandListener voiceListener;
        readonly VoiceIntentRouter voiceIntents = new VoiceIntentRouter();
        bool voiceRelayEnabled;
        bool voiceRelayAiFallback;
        List<string> voiceRelayPrefixes = new List<string>();

        bool running;

        public DouyinLiveClient.State ConnectionState =>
            client != null ? client.ConnectionState : DouyinLiveClient.State.Stopped;
        public long SessionLikes => like.SessionTotal;
        public int SessionReplies => danmakuAI.RepliedCount;
        // 直播场景与安静状态。直播中可变（F9/主播语音），读方要现读现用
        LiveScene scene = LiveScene.Solo;
        bool quiet;
        float quietUntil;
        float quietMinutes = 5f;
        LiveBehavior behavior;
        OperatorCommandWords cmdWords = new OperatorCommandWords();
        public LiveScene Scene => scene;
        // 旧接口兼容：1=游戏陪播，其余=0
        public int ChatMode => scene == LiveScene.Game ? 1 : 0;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            speech = GetComponent<SpeechPipeline>();
            // 直播时让动作随机轮换、偶尔来个小动作；组件常驻，开播才生效
            motionDirector = GetComponent<LiveMotionDirector>();
            if (motionDirector == null) motionDirector = gameObject.AddComponent<LiveMotionDirector>();
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
            danmakuAI.OnActionMarker = OnChatActionMarker;   // 聊天模型说「那我唱咯【唱歌:晴天】」

            reader.Speech = speech;
            reader.Interval = Mathf.Max(1f, d.douyinReadInterval);
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
            idleChatter.AutoSongIdleThreshold = d.douyinIdleAutoSongThreshold;
            idleChatter.AutoSongMinInterval = d.douyinIdleAutoSongMinInterval;
            idleChatter.SongList = d.douyinIdleSongList ?? new List<string>();

            // 直播场景：douyinScene 已由 SaveLoadHandler.MigrateAfterLoad 迁移；越界按独播并警告
            scene = LiveSceneMigration.Resolve(d.douyinScene, d.douyinChatMode, out bool sceneOutOfRange);
            if (sceneOutOfRange)
                Debug.LogWarning($"[DouyinLive] douyinScene={d.douyinScene} 无效（只能是 0/1/2），按 AI 独播处理");
            quietMinutes = Mathf.Clamp(d.douyinQuietMinutes, 0.5f, 120f);
            cmdWords = new OperatorCommandWords();
            if (d.douyinCmdSwitchPrefixes != null) cmdWords.SwitchPrefixes = d.douyinCmdSwitchPrefixes;
            if (d.douyinCmdSceneGame != null) cmdWords.SceneGame = d.douyinCmdSceneGame;
            if (d.douyinCmdSceneDuo != null) cmdWords.SceneDuo = d.douyinCmdSceneDuo;
            if (d.douyinCmdSceneSolo != null) cmdWords.SceneSolo = d.douyinCmdSceneSolo;
            if (d.douyinCmdStop != null) cmdWords.Stop = d.douyinCmdStop;
            if (d.douyinCmdClearQueue != null) cmdWords.ClearQueue = d.douyinCmdClearQueue;
            if (d.douyinCmdQuiet != null) cmdWords.Quiet = d.douyinCmdQuiet;
            if (d.douyinCmdResume != null) cmdWords.Resume = d.douyinCmdResume;
            ApplyBehavior();

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
            intents.Cloud = cloudBackend;
            intents.debugLog = debugLog;
            voiceIntents.Cloud = cloudBackend;
            voiceIntents.debugLog = debugLog;
            voiceRelayEnabled = d.douyinVoiceRelayEnabled;
            voiceRelayAiFallback = d.douyinVoiceRelayAiFallback;
            voiceRelayPrefixes = d.douyinVoiceRelayPrefixes != null
                ? new List<string>(d.douyinVoiceRelayPrefixes) : new List<string>();
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

            // ---------- 语音助手：麦克风唤醒 → 云端 ASR → 伪装弹幕进 Route() ----------
            asrProvider ??= new CloudASRProvider();
            asrProvider.BaseUrl = string.IsNullOrWhiteSpace(d.douyinVoiceAsrBaseUrl) ? d.aiBaseUrl : d.douyinVoiceAsrBaseUrl;
            asrProvider.ApiKey = string.IsNullOrWhiteSpace(d.douyinVoiceAsrApiKey) ? d.aiApiKey : d.douyinVoiceAsrApiKey;
            asrProvider.Model = string.IsNullOrWhiteSpace(d.douyinVoiceAsrModel) ? "FunAudioLLM/SenseVoiceSmall" : d.douyinVoiceAsrModel;
            asrProvider.debugLog = debugLog;

            if (voiceListener == null)
            {
                voiceListener = gameObject.AddComponent<VoiceCommandListener>();
                // AddComponent 会同步跑 OnEnable→StartMic：先按死再 Configure，
                // 别让关着的功能用默认参数开一次真麦克风
                voiceListener.enabled = false;
            }
            voiceListener.Configure(speech, songService, this, asrProvider, new VoiceListenerSettings
            {
                WakeWords = d.douyinVoiceWakeWords,
                WakeKeywords = d.douyinVoiceWakeKeywords,
                AckLines = d.douyinVoiceAckLines,
                CommandWindow = d.douyinVoiceCommandWindow,
                SilenceSeconds = d.douyinVoiceSilenceSeconds,
                AckTimeout = d.douyinVoiceAckTimeout,
                FollowUpSeconds = d.douyinVoiceFollowUpSeconds,
                DuckVolume = d.douyinVoiceDuckVolume,
                KeywordWake = d.douyinVoiceKeywordWake,
                DebugLog = debugLog
            });
            bool wantVoice = d.enableDouyinLive && d.douyinVoiceEnabled;
            voiceListener.enabled = wantVoice && asrProvider.IsAvailable;
            if (wantVoice && !asrProvider.IsAvailable)
                Debug.LogWarning("[DouyinLive] 语音助手开关已开但 ASR 未配置（douyinVoiceAsrBaseUrl/ApiKey 或 aiBaseUrl/aiApiKey 为空），不启动");

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
            motionDirector?.SetLive(true);
            welcome.ResetSession();
            room.ResetSession();
            liveOps.ResetSession();
            like.ResetSession();
            danmakuAI.ResetSession();
            reader.ResetSession();
            idleChatter.ResetSession();
            if (triggers != null) triggers.ResetSession();
            intents.Reset();
            voiceIntents.Reset();
            voiceListener?.ResetSession();
            if (debugLog) Debug.Log("[DouyinLive] Started, connecting " + url);
        }

        void StopLive()
        {
            client?.Stop();
            client = null;
            running = false;
            motionDirector?.SetLive(false);
            ExitQuiet(false);   // 安静不跨场次：别把低优先级静音留到下次开播
            speech.ClearQueue();
            // 意图判定是 1.5 秒的异步调用，停播时可能还有一次在途；它的回调走
            // MainThreadDispatcher，不受下面 Update 的 running 早退保护，所以这里
            // 要把在等的请求和追问槽位一起清掉，别让角色在停播后自己动起来。
            intents.Reset();
            voiceIntents.Reset();   // 在途传话分类同上，停播后不动作
            voiceListener?.ResetSession();   // 丢弃在途转写，停播后语音不动作
            if (triggers != null) triggers.ResetSession();
        }

        void Update()
        {
            MainThreadDispatcher.Drain();
            UpdateSceneKey();
            if (quiet && running && Time.unscaledTime >= quietUntil) ExitQuiet(true);
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
                reader.Tick();
                idleChatter.Tick();
                liveOps.Tick();
            }
            audience.SaveIfDirty();
        }

        // 场景与安静变化后重新解析并下发。场景规则全在 LiveSceneResolver，这里只搬运结果
        void ApplyBehavior()
        {
            var d = SaveLoadHandler.Instance != null ? SaveLoadHandler.Instance.data : null;
            if (d == null) return;
            behavior = LiveSceneResolver.Resolve(scene, quiet, new LiveSwitches
            {
                AiReply = d.douyinAIReplyEnabled,
                IdleChatter = d.douyinIdleChatterEnabled,
                IdleThreshold = d.douyinIdleThreshold,
                AutoSong = d.douyinIdleAutoSongEnabled,
                AutoDance = d.douyinIdleAutoDanceEnabled
            });
            // danmakuAI.Enabled 不随场景/安静变：主播和她聊天也走它，安静时观众弹幕在 Route 里拦
            danmakuAI.VerboseChat = behavior.VerboseChat;
            idleChatter.Enabled = behavior.IdleChatter;
            idleChatter.IdleThreshold = behavior.IdleThreshold;
            idleChatter.AutoSongEnabled = behavior.AutoSong;
            idleChatter.AutoDanceEnabled = behavior.AutoDance;
            speech.SetLowPriorityMute(behavior.MuteLowPriority);
        }

        // F9 在三个场景间循环。游戏陪播的主播人在游戏里，切换成功与否只能靠听，所以必须念确认
        void UpdateSceneKey()
        {
            if (chatModeKey == KeyCode.None || !Input.GetKeyDown(chatModeKey)) return;
            SwitchScene((LiveScene)(((int)scene + 1) % 3), true);
        }

        static string SceneName(LiveScene s) =>
            s == LiveScene.Game ? "游戏陪播" : s == LiveScene.Duo ? "搭档主持" : "AI独播";

        // 主播指令的反馈用 AIReply：安静期间低优先级静音不能吞掉它，主播要听到确认
        void Say(string text) => speech.Enqueue(text, SpeechPipeline.Priority.AIReply, 15f);

        // F9 与主播语音「切到…」共用。写入配置并存盘，重启后沿用
        public void SwitchScene(LiveScene target, bool announce)
        {
            if (target == scene)
            {
                if (announce) Say($"已经是{SceneName(target)}模式啦～");
                return;
            }
            scene = target;
            var d = SaveLoadHandler.Instance != null ? SaveLoadHandler.Instance.data : null;
            if (d != null)
            {
                d.douyinScene = (int)target;
                SaveLoadHandler.Instance.SaveToDisk();
            }
            ApplyBehavior();
            if (announce)
                Say(target == LiveScene.Game ? "切到游戏陪播模式啦，弹幕我来念，你安心打游戏～"
                  : target == LiveScene.Duo ? "切到搭档主持模式啦，我少说两句，把话头留给你～"
                  : "切到AI独播模式啦，弹幕我来接～");
            if (debugLog) Debug.Log($"[DouyinLive] 场景 -> {target}");
        }

        void EnterQuiet()
        {
            quietUntil = Time.unscaledTime + quietMinutes * 60f;   // 安静中再说一次 = 重新计时
            if (!quiet)
            {
                quiet = true;
                reader.ResetSession();       // 排队待念的弹幕作废
                danmakuAI.ClearPending();    // 排队/生成中的观众回复作废（对话历史保留）
                triggers?.ClearActionQueue();   // 排队的大礼物舞、换角色等 L3 效果作废
                ApplyBehavior();             // 关暖场/自动唱跳，开低优先级静音（顺带清掉已排队的欢迎/答谢）
            }
            Say($"好，我先安静{quietMinutes:0.#}分钟～");
            if (debugLog) Debug.Log($"[DouyinLive] 进入安静 {quietMinutes:0.#} 分钟");
        }

        void ExitQuiet(bool announce)
        {
            if (!quiet) return;
            quiet = false;
            ApplyBehavior();
            // 安静期间没人互动时冷场计时一直在涨，不重置的话「我回来啦」后紧接着就开始暖场/自动唱歌
            idleChatter.NotifyInteraction();
            if (announce) Say("我回来啦～");
            if (debugLog) Debug.Log("[DouyinLive] 退出安静");
        }

        void StopPerformance()
        {
            bool stopped = false;
            // 先清排队的 L3：否则当前这支一停，排在后面的大礼物舞/点舞会在同一帧接着上
            triggers?.ClearActionQueue();
            if (songService != null && songService.IsPlaying) { songService.StopSong(); stopped = true; }
            var director = GetComponent<DanceDirector>();
            if (director != null && director.Busy) { director.StopAll(); stopped = true; }
            if (stopped) Say("好～");   // 本来就没在唱跳：不出声
        }

        void ExecuteOperatorCommand(OperatorCommandKind kind, LiveScene target)
        {
            switch (kind)
            {
                case OperatorCommandKind.SwitchScene: SwitchScene(target, true); break;
                case OperatorCommandKind.UnknownScene: Say("没有这个模式哦，可以说游戏陪播、搭档主持或独播～"); break;
                case OperatorCommandKind.StopPerformance: StopPerformance(); break;
                case OperatorCommandKind.ClearQueue:
                    speech.ClearQueue();
                    reader.ResetSession();
                    danmakuAI.ClearPending();
                    break;
                case OperatorCommandKind.Quiet: EnterQuiet(); break;
                case OperatorCommandKind.Resume: ExitQuiet(true); break;   // 没在安静：ExitQuiet 直接返回，不出声
            }
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

            // 安静：观众侧只记账不响应（上面已记账）。礼物只说答谢语、不跳舞不特写、不走触发规则；
            // 进房/关注仍交给欢迎服务——它负责记录来访，欢迎语由播报低优先级静音吞掉。
            // 主播语音（FromVoice）不受影响。
            if (!behavior.RespondToAudience && !ev.FromVoice)
            {
                if (ev.Type == DouyinMsgType.Gift) reward.ThankOnly(ev);
                else if (ev.Type == DouyinMsgType.Enter || ev.Type == DouyinMsgType.Share ||
                         ev.Type == DouyinMsgType.FansClub || ev.Type == DouyinMsgType.Follow)
                    welcome.OnEvent(ev);
                return;
            }

            if (triggers != null)
            {
                // 关键词规则排在槽位补全之前是有意的：观众答的歌名如果恰好叫
                // 《抱抱》，会命中 love 规则去播飞吻而不是唱歌。宁可漏一次也不
                // 要乱触发，这个顺序也顺带省掉了「答案是不是另一条命令」那道校验。
                if (triggers.TryHandle(ev)) return;
                // RewardService 的硬编码命令（点歌/换角色/菜单/玩法）和关键词规则
                // 同级，同样要排在槽位补全之前。观众在追问期间发「点歌 晴天」是
                // 一条新命令，不是答案——当答案处理会把「点歌 晴天」整串当歌名。
                // 槽位刻意不动，这条弹幕交给 HandleChatLegacy 里的 RewardService。
                if (!RewardService.IsDanmakuCommand(ev.Content) && triggers.TryFillSlot(ev)) return;
            }

            // 关键词和槽位都没接住：本地预筛命中的才去问大模型。异步，所以命中就
            // 当场消费掉这条弹幕，判不出来再由回调补回 HandleChatLegacy ——
            // 否则会出现「先闲聊回一句、一秒后又开始唱歌」的双重响应。
            // 排除 RewardService 命令：「点歌 晴天」会命中 LooksLikeIntent(「歌」)，
            // 不挡住的话每条命令都会先烧一次 LLM 调用，再抢 RewardService 的活。
            if (ev.Type == DouyinMsgType.Chat && !RewardService.IsDanmakuCommand(ev.Content) &&
                triggers != null && triggers.IntentFallbackEnabled &&
                intents.TryResolve(ev, OnIntentResolved, OnIntentGaveUp))
                return;

            switch (ev.Type)
            {
                case DouyinMsgType.Chat:
                    HandleChatLegacy(ev);
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

        // 触发层、槽位、意图判定都没接住的弹幕走这里。抽成方法是因为意图判定
        // 是异步的：1.5 秒后判不出来，回调要能把这条弹幕补回原路径。
        void HandleChatLegacy(DouyinEvent ev)
        {
            if (reward.TryHandleDanmaku(ev)) return;
            // 语音不走「念弹幕」：主播自己的话被角色念回给主播没有意义，无论什么模式都走 AI 回复。
            // 开头词传话已在 InjectVoiceChat 入口处理；这里是可选的大模型兜底（默认关）：
            // 没说开头词的话再问一次分类器，后端不可用/已在飞/超时一律当聊天。
            if (ev.FromVoice)
            {
                if (voiceRelayEnabled && voiceRelayAiFallback && voiceIntents.TryRoute(ev, OnVoiceRelay, OnVoiceChat)) return;
                danmakuAI.OnDanmaku(ev);
                return;
            }
            // 游戏陪播：AI 不掺和，带昵称念出来，主播听声人工回复。
            // 指令弹幕（触发规则/槽位/点歌等）在进到这里之前就已被消费。
            if (behavior.Danmaku == DanmakuHandling.Read) reader.OnDanmaku(ev);
            else if (behavior.Danmaku != DanmakuHandling.Ignore) danmakuAI.OnDanmaku(ev);
        }

        // 传话/聊天回调：分类是 1.5 秒异步，停播后回来不能开口（同 OnIntentResolved 的 running 守卫）
        void OnVoiceRelay(string text)
        {
            if (!running) return;
            speech.Enqueue(text, SpeechPipeline.Priority.AIReply, 30f);
            if (debugLog) Debug.Log("[DouyinLive] 传话播出: " + text);
        }

        void OnVoiceChat(DouyinEvent ev)
        {
            if (!running) return;
            danmakuAI.OnDanmaku(ev);
        }

        // ---------- 语音助手：麦克风命令伪装成弹幕进同一条链路 ----------

        // 语音监听器每帧查询：停播或菜单打开时中止会话、恢复音乐和播报
        public bool VoiceBlocked => !running || IsBlocked();

        // VoiceCommandListener 转写完成后的唯一入口。停播/菜单打开时静默丢弃——
        // 与弹幕「消费不响应」一致；在途 ASR 回调晚于停播到达也在这道闸拦住。
        public void InjectVoiceChat(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!running || IsBlocked()) return;
            // 主播控制指令最先：「别唱了」若先进弹幕管线，「唱」字会被当成点歌意图
            if (OperatorCommandParser.TryParse(text, cmdWords, out var cmdKind, out var cmdScene))
            {
                if (debugLog) Debug.Log($"[DouyinLive] 主播指令: {cmdKind} {cmdScene}（{text}）");
                ExecuteOperatorCommand(cmdKind, cmdScene);
                return;
            }
            // 开头词传话（「告诉大家八点下播」）先于 Route()：主播明确说了要播报，
            // 后半句里带「唱/歌」也不该先被当成点歌
            if (voiceRelayEnabled && VoiceRelayParser.TryExtractRelay(text, voiceRelayPrefixes, out var relay))
            {
                OnVoiceRelay(relay);
                return;
            }
            var d = SaveLoadHandler.Instance != null ? SaveLoadHandler.Instance.data : null;
            var ev = new DouyinEvent
            {
                Type = DouyinMsgType.Chat,
                UserId = "voice",
                Nickname = d != null && !string.IsNullOrWhiteSpace(d.douyinVoiceNickname) ? d.douyinVoiceNickname : "主播",
                Content = text,
                FromVoice = true,
                ReceivedAt = DateTime.Now
            };
            if (debugLog) Debug.Log("[DouyinLive] 语音命令: " + text);
            Route(ev);
        }

        void OnIntentResolved(DouyinEvent ev, IntentKind kind, string arg)
        {
            // 停播前发起的判定回来时直播已经结束了，这条回调也绕过了 IsBlocked()
            if (!running) return;
            if (QuietDrops(ev)) return;
            if (triggers != null && triggers.TryHandleIntent(ev, kind, arg)) return;
            // 规则不存在或被限流拦下：「我想听首歌」本身是很好的闲聊素材，
            // 让 AI 回一句是体面的降级
            HandleChatLegacy(ev);
        }

        // 模型判出 none 走的是另一条回调，同样是停播前发起、停播后才回来的，
        // 直接把 HandleChatLegacy 交出去会在 StopLive 清完队列之后又塞一句话进去
        void OnIntentGaveUp(DouyinEvent ev)
        {
            if (!running) return;
            if (QuietDrops(ev)) return;
            HandleChatLegacy(ev);
        }

        // 安静前发起、安静后才回来的异步回调（意图判定、聊天动作标记）：观众的作废，主播的照常
        bool QuietDrops(DouyinEvent ev) => !behavior.RespondToAudience && !ev.FromVoice;

        // 聊天模型的动作标记（【唱歌:晴天】）。模型承诺要表演时话已经念出去了，
        // 这里把动作补上——经 TryHandleIntent 走和意图判定同一条路：四道限流闸、
        // L3 仲裁、参数不可用转追问开槽，全都复用。未接住（规则没了/被限流）也不
        // 回落闲聊：模型已经口头回应过这条弹幕，再回一句就是双重响应。
        void OnChatActionMarker(DouyinEvent ev, IntentKind kind, string arg)
        {
            if (!running) return;
            if (QuietDrops(ev)) return;
            if (debugLog) Debug.Log($"[DouyinLive] 聊天动作标记: {kind} / {arg}");
            triggers?.TryHandleIntent(ev, kind, arg);
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

        // swapAvatar:<角色名> —— 名字为空或匹配不上时退回随机换
        public void SwapAvatarFromTrigger(string userName, string wanted)
        {
            if (string.IsNullOrWhiteSpace(wanted)) reward.SwitchRandomAvatar(userName);
            else reward.SwitchAvatarByName(userName, wanted);
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
