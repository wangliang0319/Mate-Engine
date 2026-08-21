# 抖音直播互动 —— 集成说明

代码位于 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/`（17 个文件）+
`Settings/SettingsMenu/SettingsHandlerDouyinLive.cs` + `SaveLoadHandler.SettingsData` 新增字段。
纯代码集成，不修改现有系统内部逻辑。

## 功能总览

| 功能 | 触发 | 行为 |
|---|---|---|
| 自动欢迎 | 观众进房/关注/分享/粉丝团 | 语音欢迎；高峰合并播报；同一观众每场一次 |
| AI 弹幕回复 | 普通弹幕 | 云端 LLM 生成口语化回复 → TTS 语音 + 口型 + 表情 |
| 点赞反应 | 点赞聚合 | 阈值致谢 + 1k/5k/1w 里程碑欢呼 |
| 礼物三档庆祝 | 礼物（按抖币×数量） | <10 甜甜致谢；10~99 热情惊呼；≥100 欢呼+跳舞献礼 |
| 点歌 | 弹幕 `点歌 <歌名>` | 优先本地 MMD 舞包；否则网易云搜歌→高潮段→原生跟舞 |
| 点舞 | 弹幕 `点舞 [舞名]` | 本地 MMD 舞蹈库（模糊匹配，无名=随机） |
| 冷场暖场 | 90 秒无互动 | 自动求赞/求关注/才艺引导/闲聊，四类轮换 |
| 情绪动作 | 说话内容关键词 | 谢谢/抱抱→开心表情+互动动画；难过/惊喜同理 |
| 直播采集模式 | 快捷键 F9 | 窗口变实体+绿幕，供直播伴侣窗口采集；F10 循环尺寸 |

## 场景接线（Unity Editor 内一次性完成）

1. **常驻对象**（挂 SaveLoadHandler 的 `Settings` 对象）挂：
   - `SpeechPipeline`：`voiceSource` → 专用 AudioSource（Play On Awake 关）；
     `chatContainer` → `MinecraftPanel`；透明模式下 Sprite/Material 可不填
   - `DouyinLiveManager`：`blockObjects` → 设置菜单面板；`localCharacter` 可空
   - `SongService` 由 Manager 自动挂载，无需手动添加
2. **设置页**（可选）：挂 `SettingsHandlerDouyinLive`，控件拖对应字段。
   未做 UI 时直接编辑 settings.json。

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

### CaptureModeController（直播采集，自动挂载）

- **F9**：切换采集模式。透明桌宠 ⇆ 标准实体窗口+绿幕（Win32 强制清除
  WS_EX_TOOLWINDOW/LAYERED 等隐身样式，直播伴侣【窗口】采集必然可见）。
- **F10**：循环窗口尺寸预设（1280×720 / 1920×1080 / 720×1280 / 800×800，
  Inspector 可改）；也可直接拖窗口边缘调整。
- 直播伴侣中对该窗口源添加【色度键】滤镜（绿色）抠出透明桌宠；
  角色带绿色部件时勾 `Use Blue` 换蓝幕。
- 退出采集模式自动还原窗口位置/大小/透明/置顶/穿透。

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

## settings.json 配置（`AppData/LocalLow/Shinymoon/MateEngineX/`）

```jsonc
"enableDouyinLive": true,
"douyinWsUrl": "ws://127.0.0.1:8888",
// 各功能开关
"douyinWelcomeEnabled": true, "douyinAIReplyEnabled": true,
"douyinLikeReactEnabled": true, "douyinGiftEnabled": true,
"douyinIdleChatterEnabled": true,    // 冷场暖场
"douyinIdleThreshold": 90.0,         // 冷场判定秒数
"douyinWelcomeCooldown": 8.0, "douyinAIReplyMinInterval": 8.0,
"douyinLikeThreshold": 100,
"douyinLivePrompt": "",              // 追加人设

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

## 弹幕模拟器（Tools/douyin_mock_server.py）

模拟抓取器推送，无直播全链路测试。运行后命令：

```
c <内容>   弹幕        e/f/s [昵称]  进房/关注/分享
l [次数]   点赞        g <礼物> [数量] [抖币]  礼物
gg         大礼物(火箭300抖币,测隆重庆祝)
song <歌名> 点歌       dance [舞名]  点舞
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

## 已知边界

- PackMsgType 编号按 DouyinBarrageGrab 常见版本假定（1弹幕/2点赞/3进房/4关注/5礼物/6统计/7粉丝团/8分享），
  实测不符改 `DouyinEvents.cs` 枚举值。
- EdgeTTS 已被微软 403 封禁（2026-08 实测），保留代码但默认走云端 TTS。
- 网易云外链只能播非 VIP 版本（通常是翻唱）；要原唱需自行接会员 Cookie。
- 内置舞步共 5 个动画片段；"点歌跟舞"是节奏对齐的舞步轮换，精确卡点编舞请用
  MMD 舞包（`StreamingAssets/CustomDances`），点歌会自动优先匹配同名舞包。
- mp3 解码用 NAudio ACM（Windows 自带解码器），仅 Windows —— 与项目平台一致。
