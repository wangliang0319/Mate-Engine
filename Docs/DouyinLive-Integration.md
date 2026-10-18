# 抖音直播互动 —— 集成说明

代码位于 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/`（17 个文件）+
`Settings/SettingsMenu/SettingsHandlerDouyinLive.cs` + `SaveLoadHandler.SettingsData` 新增字段。
纯代码集成，不修改现有系统内部逻辑。

## 功能总览

| 功能 | 触发 | 行为 |
|---|---|---|
| 自动欢迎 | 观众进房/关注/分享/粉丝团 | 语音欢迎；高峰合并播报；同一观众每场一次 |
| AI 弹幕回复 | 普通弹幕 | 云端 LLM 生成口语化回复 → TTS 语音 + 口型 + 表情；AI 聊天模式回复两三句并引导观众继续聊；通用问题先答题再主播腔；动作标记（【唱歌:晴天】）让模型顺着聊天自己触发表演 |
| 游戏陪播念弹幕 | 普通弹幕（`douyinChatMode: 1`） | 带昵称念出来（「小明说：…」），主播人工回复；F9 直播中随时切换 |
| 点赞反应 | 点赞聚合 | 阈值致谢 + 1k/5k/1w 里程碑欢呼 |
| 礼物三档庆祝 | 礼物（按抖币×数量） | <10 甜甜致谢；10~99 热情惊呼；≥100 欢呼+跳舞献礼 |
| 点歌 | 弹幕 `点歌 <歌名>` | 优先本地 MMD 舞包；否则网易云搜歌→高潮段→原生跟舞 |
| 换角色 | 弹幕 `换角色` | 从模型库随机切换一个 VRM（30 秒冷却） |
| 大头特写 | 关注/礼物 | 镜头推近脸部说感谢，说完自动恢复（不改窗口尺寸） |
| 冷场暖场 | 90 秒无互动 | 自动求赞/求关注/才艺引导/玩法介绍/闲聊，五类轮换 |
| 冷场自动唱歌 | 冷场超 5 分钟 | 从歌单随机唱一首+跳舞（默认内置古风热歌榜，可自定义） |
| 情绪动作 | 说话内容关键词 | 谢谢/抱抱→开心表情+互动动画；难过/惊喜同理 |
| 竖屏直播窗口 | 配置自动 | `douyinPortraitAspect > 0` 时窗口变竖屏、角色居中，适配竖屏直播采集 |
| 快捷反应 | 666/哈哈/你好/晚安等 | 预制回复秒回，不走 LLM |
| 语音助手 | 麦克风说唤醒词 | 「小美小美 唱一首黄昏」→ 开口应答+唱跳；闲聊提问 AI 回答；唱歌/TTS 期间自动暂停监听；需 `douyinVoiceEnabled` |
| 语音传话 | 唤醒后说「告诉大家…」 | 「小美小美 告诉大家八点下播」→ 原样播出「八点下播」；不带开头词的话照旧 AI 回复；开头词可配 `douyinVoiceRelayPrefixes` |
| 观众记忆 | 自动 | 常客/大哥识别，专属欢迎；AI 回复带观众画像 |
| 玩法菜单 | 弹幕 `菜单` | 口播可用玩法 |
| 感谢榜+报时 | 自动 | 每30分钟播礼物Top3；整点报时 |

## 场景接线（Unity Editor 内一次性完成）

1. **常驻对象**（挂 SaveLoadHandler 的 `Settings` 对象）挂：
   - `SpeechPipeline`：`voiceSource` → 专用 AudioSource（Play On Awake 关）；
     `chatContainer` → `MinecraftPanel`；透明模式下 Sprite/Material 可不填
   - `DouyinLiveManager`：`blockObjects` → 设置菜单面板；`localCharacter` 可空
   - `SongService` 由 Manager 自动挂载，无需手动添加
2. **设置页**（可选）：挂 `SettingsHandlerDouyinLive`，控件拖对应字段。
   未做 UI 时直接编辑 settings.json。
3. **语音助手**（可选）：`VoiceCommandListener` 由 Manager 自动挂载，无需手动接线；需要麦克风权限。

### SpeechPipeline 关键 Inspector 参数

| 参数 | 默认 | 说明 |
|---|---|---|
| Transparent Bubble | 开 | 无背景纯文字+双层描边 |
| Bubble Anchor | Right | 文字位置：Above 头顶 / Left / Right |
| Side Clearance | 0.18 | 侧边模式距角色距离(米，随缩放) |
| Font / Font Size / Bubble Width | NotoSansSC-Medium / 30 / 360 | 直播采集下清晰可读；Font 需手动在 Inspector 指定 |
| Font Color / Outline Color | 暖黄白 / 深紫黑 | 直播字幕经典配色，自动加粗 |
| Emotion From Text | 开 | 说话内容驱动表情/互动动画 |
| Lip Sync Gain | 1 | 口型幅度 |

### SongService 关键参数

| 参数 | 默认 | 说明 |
|---|---|---|
| Chorus Only / Chorus Seconds | 开 / 60 | 只播能量最高的高潮段 |
| Rhythm Dance | 开 | BPM 对齐舞步轮换周期与动画速度 |
| Beats Per Move | 8 | 每 N 拍换舞步 |
| Music Volume / Max Song Seconds | 0.85 / 300 | 音量 / 最长播放 |

## 运行前提

- 启动 [DouyinBarrageGrab](https://github.com/ape-byte/DouyinBarrageGrab)（管理员），默认 `ws://127.0.0.1:8888`，
  再打开抖音直播伴侣或浏览器直播页。**注意：直播页要在抓取器启动之后打开**，否则抓不到流。
- 无直播测试：关闭抓取器，运行 `python Tools/douyin_mock_server.py`（见下）。
- 语音助手：「关键词唤醒」模式要求系统装了中文语音包，没有会自动降级为音量检测模式
  （每句话都送云端识别，延迟多 ~1 秒）；ASR 模型名按服务商填（SiliconFlow `FunAudioLLM/SenseVoiceSmall`，OpenAI 官方 `whisper-1`）。

## settings.json 配置（`AppData/LocalLow/Shinymoon/MateEngineX/`）

```jsonc
"enableDouyinLive": true,
"douyinWsUrl": "ws://127.0.0.1:8888",
// 各功能开关
"douyinWelcomeEnabled": true, "douyinAIReplyEnabled": true,
"douyinLikeReactEnabled": true, "douyinGiftEnabled": true,
"douyinIdleChatterEnabled": true,    // 冷场暖场
"douyinIdleThreshold": 90.0,         // 冷场判定秒数
"douyinIdleAutoSongEnabled": true,   // 冷场自动唱歌
"douyinIdleAutoSongThreshold": 300.0,// 冷场多少秒后自动唱
"douyinIdleSongList": ["赤伶", "..."],  // 自动唱歌歌单(默认18首古风热歌，直接在此增删)；空=不自动唱
"douyinWelcomeCooldown": 8.0, "douyinAIReplyMinInterval": 8.0,
"douyinLikeThreshold": 100,
"douyinChatMode": 0,             // 0=AI聊天（详细回复） 1=游戏陪播（念弹幕），F9 直播中随时切
"douyinReadInterval": 5.0,       // 念弹幕最小间隔秒数
"douyinLivePrompt": "",              // 追加人设

// 语音助手（麦克风控制角色）
"douyinVoiceEnabled": false,
"douyinVoiceWakeWords": ["小美小美", "小美"],   // 建议改成角色人设的名字
"douyinVoiceAsrBaseUrl": "",       // 空 = 复用 aiBaseUrl
"douyinVoiceAsrApiKey": "",        // 空 = 复用 aiApiKey
"douyinVoiceAsrModel": "FunAudioLLM/SenseVoiceSmall",
"douyinVoiceCommandWindow": 8.0,   // 唤醒后最长录音秒数
"douyinVoiceSilenceSeconds": 1.2,  // 静音多久算说完
"douyinVoicePauseWhilePerforming": true,
"douyinVoiceKeywordWake": true,    // false=强制音量检测模式（与本地识别器抢设备时的逃生门）
"douyinVoiceNickname": "主播",
"douyinVoiceRelayEnabled": true,   // 传话总开关；false=语音一律走聊天
"douyinVoiceRelayPrefixes": ["告诉大家", "跟大家说", "和大家说", "给大家说", "通知大家", "提醒大家", "转告大家"],
"douyinVoiceRelayAiFallback": false,  // 没说开头词时问大模型是不是传话；需要很快的模型，默认关

// Cloud AI（OpenAI 兼容；key 明文存储）
"aiBaseUrl": "https://xxx/v1", "aiApiKey": "sk-...",
"aiModel": "qwen3-30b-a3b-instruct-2507",   // 实测最快最自然
"aiFallbackToLocal": true,

// TTS: 0=OpenAI兼容云端 1=EdgeTTS(已被微软封,勿用) 3=纯气泡
"ttsProvider": 0,
"ttsBaseUrl": "", "ttsApiKey": "",   // 留空=复用 AI 的地址和 key
"ttsModel": "gpt-4o-mini-tts", "ttsVoice": "shimmer",
"ttsInstructions": "你是一个20岁左右的中国甜美少女主播…",  // 消除洋腔的关键
"ttsVolume": 1.0, "ttsSpeed": 1.0, "lipSyncGain": 1.0
```

礼物规则 `douyin_gift_rules.json`（同目录）：
`{ "giftName": "玫瑰", "minDiamond": 0, "minCount": 1, "action": "randomDance" }`，
action：`thanks` / `randomDance` / `builtinDance` / `dance:<舞名>`。
三档庆祝始终生效，规则只追加指定舞。

## 数据文件（AppData/LocalLow/Shinymoon/MateEngineX/）

| 文件 | 用途 |
|---|---|
| douyin_persona.json | 人设卡：名字/性格/背景/口头禅/禁忌，AI人格来源 |
| douyin_blocked_words.txt | 敏感词表（一行一词），AI输出含词即静默丢弃 |
| douyin_audience.json | 观众记忆：来访次数/礼物总额/最近弹幕（自动维护） |
| douyin_gift_rules.json | 礼物→动作规则 |

## 弹幕模拟器（Tools/douyin_mock_server.py）

模拟抓取器推送，无直播全链路测试。运行后命令：

```
c <内容>   弹幕        e/f/s [昵称]  进房/关注/分享
l [次数]   点赞        g <礼物> [数量] [抖币]  礼物
gg         大礼物(火箭300抖币,测隆重庆祝)
song <歌名> 点歌       sw  换角色
auto       自动模式(含随机点歌/点舞/大礼物)     q 退出
```

端口被占时：`--port 8899`（同时改 douyinWsUrl）。

## 关键实现机制

- **语音管线**：所有播报走统一优先级队列（礼物P0 > AI回复P1 > 关注/里程碑P2 > 欢迎P3 > 点赞/暖场P4），
  句级 TTS 流水线（预取2句），音频 RMS 驱动口型，`isTalking` 在音频实际开播帧才置位（嘴声同步）。
- **原生跟舞**（点歌）：播放时把本进程加入 `AvatarAnimatorController.allowedApps` 并临时放宽
  `SOUND_THRESHOLD`，让原生声音检测自行进入跳舞流程（原生 StartDancing → danceTimer →
  SmoothDanceTransition 平滑轮换）；BPM 分析仅用于把轮换周期对齐到 8 拍、动画速度贴节奏。
  歌毕全部还原。静音兜底：3 秒未起舞则强制置 isDancing。
- **点歌打断**：拖拽角色 / 打开菜单 / MMD 自定义舞启动（点舞、礼物舞）→ 立即停唱停跳并还原设置。
- **点歌优先级**：本地 MMD 同名舞包（真编舞）> 网易云在线（VIP 歌自动落到可播的翻唱版）。
- **三级降级**：云 LLM→本地 LLMUnity→不回复；云 TTS→(EdgeTTS 已失效)→纯气泡字幕。
- **语音助手**：麦克风常开环形缓冲（回看 3 秒），本地 KeywordRecognizer 命中唤醒词
  （系统无中文语音包自动降级音量检测）后录到静音 1.2 秒，WAV 送云端 ASR，文本剥唤醒词
  后伪装成 `UserId="voice"` 的弹幕进 Route()——触发规则/追问槽位/LLM 意图/点歌换角色全部复用；
  非命令内容走 AI 闲聊回复（陪播模式也不念出来）。角色出声期间暂停监听防自触发。
  句首带开头词（「告诉大家…」）的话在进 Route() 之前就判为传话：剥掉开头词、原话进播报队列
  （不过 AI 人设/敏感词通路，后半句带「唱/歌」也不会被当成点歌）；其余照常走命令/聊天。
  可选的大模型兜底（VoiceIntentRouter，≤1.5 秒）默认关。

### 触发层（2026-08 新增）

```
DouyinLiveClient → DouyinLiveManager.Route()
                        ├─ TriggerRouter.TryHandle(ev)      读 douyin_triggers.json，热重载
                        │     └─ TriggerMatcher.Match       纯函数，命中的第一条规则
                        │     └─ TriggerLimiter.Check       四道限流闸
                        │     └─ ActionDirector.Submit      三层仲裁 + L3 队列
                        │           └─ EffectRegistry.Execute
                        └─ 未命中 → 原有的 reward / danmakuAI / welcome / like 逻辑
```

旁路式：删掉 `douyin_triggers.json` 就完全回退到接入触发层之前的行为。

纯逻辑部分在 `Core/` 子目录里，属于 `MateEngine.DouyinLive.Core` 程序集，
对应的 EditMode 测试在 `Tests/`（Unity 的 asmdef 不能反向引用 `Assembly-CSharp`，
所以想单元测试的代码必须放进独立程序集）。

## 已知边界

- PackMsgType 编号按 DouyinBarrageGrab 常见版本假定（1弹幕/2点赞/3进房/4关注/5礼物/6统计/7粉丝团/8分享），
  实测不符改 `DouyinEvents.cs` 枚举值。
- EdgeTTS 已被微软 403 封禁（2026-08 实测），保留代码但默认走云端 TTS。
- 网易云外链只能播非 VIP 版本（通常是翻唱）；要原唱需自行接会员 Cookie。
- 内置舞步共 5 个动画片段；"点歌跟舞"是节奏对齐的舞步轮换，精确卡点编舞请用
  MMD 舞包（`StreamingAssets/CustomDances`），点歌会自动优先匹配同名舞包。
- mp3 解码用 NAudio ACM（Windows 自带解码器），仅 Windows —— 与项目平台一致。
- 语音助手：ASR 同音字转错歌名等同观众打错字，属正常容错；VAD 降级模式下直播外放声也会被
  送识别（有防自触发暂停兜底，口误提到唤醒词仍可能误触发）；唤醒词嵌在句子中间会被整段剥掉
  （正常唤醒词在句首，不踩）。
- 语音传话：只认句首开头词，「我想告诉大家…」不算（那是在跟角色说话）；开头词后面的话原样直出
  （ASR 转出什么播什么，含口语停顿和重复，表情符号会被剥掉），不过 AI 人设与敏感词通路。
  大模型兜底 `douyinVoiceRelayAiFallback` 默认关：实测中转站上一次分类 3~20 秒，1.5 秒内基本回不来，
  只有换成响应很快的模型才有意义；它还会偶有误判（想说给角色的话被播出去，或反过来）。
- ASR 转写接口按模型收费开通：`douyinVoiceAsrBaseUrl` 留空复用 `aiBaseUrl`，但多数 LLM 中转站
  没有转写模型通道（症状：debugLog 里 `[VoiceASR] HTTP 503 ... model_not_found`，语音永远
  「ASR 无结果」）。把 `douyinVoiceAsrBaseUrl/ApiKey` 指到有转写接口的服务商即可（SiliconFlow
  实测可用，模型保持 `FunAudioLLM/SenseVoiceSmall`）。

## 语音助手自测步骤（需真机麦克风）

> 调试开关：Unity Editor 里 DouyinLiveManager 组件（挂在场景的 `Settings` 对象上）Inspector 的
> **Debug Log** 勾选项。开启后 Console/日志能看到 `[Voice]`（唤醒/命令）、`[VoiceASR]`（转写）、
> `[DouyinLive]`（传话播出）三组关键日志，下面的预期日志都以它为准。
> 改代码前先停 Play：Play 中途热重载会清空单例，满屏 AvatarBigScreenTimer/Blink 报错
> （建议 Preferences → General → Script Changes While Playing 设为 Recompile After Finished Playing）。

1. settings.json 开 `douyinVoiceEnabled`，确认 ASR 配置可用（复用 AI 的 key 时 aiBaseUrl 服务商必须有转写接口）
2. 说「小美小美 唱一首黄昏」→ 她开口应答并开唱+跟舞
3. 说「小美小美 今天直播什么好」→ AI 开口聊
4. 她正在说话/唱歌时喊唤醒词 → 不响应；说完恢复
5. F9 切游戏陪播后再试 2/3 → 命令照常执行、闲聊走 AI（不被念出来）
6. 不说话/放游戏声 → 无动作（开 debugLog 看无 ASR 请求）
7. 连续说话 15 秒以上再唤醒 → 仍能识别完整命令（环形缓冲跨环绕点）
8. 开 debugLog 喊一次唤醒词 → 日志出现 `[Voice] 唤醒:` 且无 UnityException（有异常说明唤醒回调线程有问题，截图反馈）
9. 一口气说「小美小美 告诉大家八点下播」→ 她立刻原样播出「八点下播」（不续唠）；隔 3 秒再说一条 → 两条都播出。
   预期日志：`[Voice] 命令: 告诉大家八点下播` → `[DouyinLive] 传话播出: 八点下播`
10. 说「小美小美 今天唱什么好」→ AI 对话回复（不是播出你的问题），且没有「传话播出」日志
11. 说「小美小美 告诉大家唱完这首有抽奖」→ 原样播出「唱完这首有抽奖」，不会被当成点歌
12. settings.json 设 `douyinVoiceRelayEnabled=false`（重启生效）→ 第 9 条变成 AI 对话回复。改回 true 再重启可恢复
