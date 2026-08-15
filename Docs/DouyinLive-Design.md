# 抖音直播互动系统设计（Douyin Live Interaction）v2

> 目标：在 Mate-Engine 桌宠基础上，接入抖音直播间事件，实现
> ① 自动欢迎进房观众 ② AI 智能回复弹幕（**在线大模型**）③ 点赞反应、礼物触发点歌/点舞
> ④ **TTS 真语音播报 + 口型同步**。

## 1. 总体架构

```
┌────────────────────┐   系统代理抓包    ┌──────────────────────┐
│  抖音直播伴侣 /     │ ───────────────► │  DouyinBarrageGrab    │  (独立外部进程)
│  浏览器直播页       │                  │  ws://127.0.0.1:8888  │
└────────────────────┘                  └──────────┬───────────┘
                                                   │ WebSocket JSON 推送
                                                   ▼
┌──────────────────────────── Mate-Engine (Unity) ────────────────────────────┐
│  DouyinLiveClient          后台线程 ClientWebSocket + 自动重连                │
│        │  ConcurrentQueue<DouyinEvent>（主线程 Update() 消费）                │
│        ▼                                                                     │
│  DouyinEventRouter         按 Type 分发 + 全局开关 + 状态门控                 │
│    ├─► WelcomeService      进房/关注/分享 → 模板文案                          │
│    ├─► DanmakuAIService    弹幕 → 过滤/限流 → IChatBackend（云端LLM优先）     │
│    ├─► LikeService         点赞 → 聚合计数 → 反应文案                         │
│    └─► RewardService       礼物 → 映射表 → 点歌/点舞/致谢文案                 │
│                     所有文案 ▼                                               │
│  SpeechPipeline（新，核心输出通道）                                           │
│    优先级队列 → 逐句切分 → ITTSProvider 合成 → AudioClip 队列播放             │
│    → 音量包络驱动 VRM `Aa` 口型 + isTalking + 气泡同步显示文字                │
│                              │                                               │
│  动作层：AvatarDanceHandler.PlayByStableId()（点歌点舞）                      │
│          AvatarAnimatorController isDancing/DanceIndex（内置舞）              │
│  配置层：SaveLoadHandler.SettingsData 扩展 + "Douyin Live" 设置页            │
└─────────────────────────────────────────────────────────────────────────────┘
         │ HTTPS                          │ HTTPS
         ▼                                ▼
   在线大模型 API                     TTS 服务 API
  (OpenAI 兼容 chat/completions)    (云端 TTS / Edge-TTS)
```

与 v1 的两个关键变化：

1. **AI 后端抽象为 `IChatBackend`**，默认用**在线大模型**（OpenAI 兼容协议），本地 LLMUnity 降为可选后备。云端并发能力解除了本地"单槽"限制，弹幕回复吞吐和质量都大幅提升。
2. **新增 `SpeechPipeline` 作为统一输出通道**：所有要"说"的内容（欢迎、AI 回复、礼物致谢、点赞感谢）不再各自弹气泡，而是排入语音管线，TTS 合成真语音，气泡文字与语音同步，口型由音频驱动。

## 2. 上游数据源：DouyinBarrageGrab

- 独立 .NET 进程，基于系统代理嗅探抖音 wss 弹幕流，支持直播伴侣、Chrome、Edge 等来源，进程过滤可配。
- WebSocket 推送：默认 `ws://127.0.0.1:8888`（`wsListenPort` 配置；`listenAny=false` 仅本机）。
- 消息为 JSON 信封 `{ "Type": <int>, "Data": "<内层JSON字符串>" }`，`Data` 需**二次反序列化**。
- 消息类型（PackMsgType，接入 P1 阶段实测确认编号）：

  | Type | 含义 | 用途 |
  |---|---|---|
  | 1 | 弹幕聊天 | AI 回复 / 点播命令 |
  | 2 | 点赞（含次数） | 点赞反应 |
  | 3 | 进入直播间 | 自动欢迎 |
  | 4 | 关注 | 感谢关注 |
  | 5 | 礼物（GiftName/GiftCount/DiamondCount） | 点歌/点舞 |
  | 6 | 直播间统计 | 忽略 |
  | 7 | 粉丝团 | 感谢 |
  | 8 | 分享 | 感谢分享 |

- 内层通用字段：`MsgId`、`User{Id, SecUid, Nickname, Level}`、`Content`、`RoomId`、`Owner`。`RoomId` 每场变化仅作场次标识，识别主播用 `Owner.SecUid`。
- 部署：随应用附带于 `StreamingAssets/Tools/`，设置页"启动弹幕抓取器"按钮 `Process.Start`，Unity 侧只管连接与重连。

## 3. Unity 侧模块设计

新增目录：`Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/`

### 3.1 DouyinLiveClient（网络层）

参照 [AvatarMinecraftMessages.cs](../Assets/MATE%20ENGINE%20-%20Scripts/Game%20APIs/AvatarMinecraftMessages.cs) 的"后台线程 + ConcurrentQueue + 主线程消费"模式，UDP 换成 `System.Net.WebSockets.ClientWebSocket`（.NET 自带）：

- 后台 Task 循环 `ReceiveAsync` → Newtonsoft 反序列化信封 → 二次反序列化 `Data` → 统一 `DouyinEvent` 入队。
- 断线指数退避重连（上限 30s），连接状态暴露给设置页。
- 按 `MsgId` 滑动窗口去重。
- 随 `enableDouyinLive` 开关启停，`OnDestroy` 取消 Task。

### 3.2 DouyinEventRouter（分发层）

主线程每帧最多 drain 20 条，按 Type 分发。统一门控：总开关 + 分功能开关、状态门控（拖拽/睡眠/菜单打开时暂缓）、用户黑名单与关键词过滤。

### 3.3 WelcomeService（自动欢迎）

- 进房/关注/分享/粉丝团 → 模板池随机 + `{user}` 昵称替换 → 提交 `SpeechPipeline`（P3 优先级）。
- 防刷屏：全局冷却（默认 8s）；高峰合并播报（"欢迎 A、B 等 5 位朋友"）；同一 `SecUid` 场次内只欢迎一次。
- 模板走 Unity Localization 表（`Languages (UI)`）。

### 3.4 DanmakuAIService（智能回复，在线大模型）

**后端抽象：**

```csharp
public interface IChatBackend {
    // 流式返回增量文本；CancellationToken 支持超时/打断
    Task ChatAsync(string systemPrompt, IList<ChatMsg> history, string userMsg,
                   Action<string> onDelta, CancellationToken ct);
    bool SupportsConcurrency { get; }
}
```

- **CloudChatBackend（默认）**：OpenAI 兼容 `POST {baseUrl}/chat/completions`，`stream: true` SSE 流式解析。只要配 `baseUrl + apiKey + model` 即可适配 OpenAI / DeepSeek / 通义千问(DashScope 兼容模式) / Kimi / 豆包(火山方舟) 等所有兼容端点。实现用 `HttpClient` + 后台线程读 SSE，增量经 ConcurrentQueue 回主线程。
- **LocalChatBackend（后备）**：包一层现有 `LLMCharacter.Chat`，云端未配置或断网时降级使用，沿用 v1 的单槽排队约束。

**直播回复策略：**

- 云端支持并发，但**语音出口是串行的**（一次只能说一句话），所以节流点从"LLM 单槽"移到"说话带宽"：维护待回复队列（上限 5），按最小间隔（默认 8s）出队请求。
- 筛选：点播命令先被 RewardService 截走；过短/纯表情/60s 内重复内容不回复；礼物用户提权。
- Prompt：独立直播人设（叠加用户自定义 ZomeAI 人设），要求"口语化中文、不超过 50 字、适合朗读（不要输出表情符号/markdown）"——**为 TTS 优化的输出约束**。历史独立维护（最近 N 轮直播间上下文），不污染用户私聊历史。
- 超时（默认 15s）取消并跳下一条；失败自动降级 LocalChatBackend（若可用）或跳过。

**API Key 安全：** key 不进 `settings.json` 明文。单独存 `persistentDataPath/douyin_ai.cfg`，用 `System.Security.Cryptography.ProtectedData`（DPAPI，CurrentUser 范围）加密；设置页输入框密码模式显示。文档提醒：直播推流时不要露出设置界面。

### 3.5 LikeService（点赞反应）

- 30s 窗口聚合点赞数，越阈值（默认 100）→ "感谢大家 {n} 个赞！"入语音管线（P4，可被挤掉）+ 可选开心动作脉冲。
- 里程碑（1k/5k/1w）特殊致谢，优先级提到 P2。

### 3.6 RewardService（礼物 → 点歌/点舞）

1. **礼物直接映射**：`GiftRule { giftNameOrId, minCount, action }`，action ∈ { 随机舞, 指定舞(stableId), 随机歌, 仅致谢 }。调 `AvatarDanceHandler.PlayByStableId()/PlayIndex()` 或内置 `isDancing`。
2. **弹幕点播**：`^点歌\s*(.+)` / `^点舞\s*(.*)` → `FindIndexByTitle()` 模糊匹配曲库；可配"需礼物解锁"；失败语音回"曲库里没有《xxx》哦"。
3. **播放队列**：跳舞中新点播排队（`SetQueueByIndices` / `IsPlaying` 轮询），语音播报排队位置。
4. 致谢文案 "谢谢 {user} 的 {gift} ×{n}！" 走 P0 优先级，跳过冷却。

### 3.7 SpeechPipeline（TTS 语音管线，新核心）

统一的"说话"出口，替代 v1 的气泡仲裁器（仲裁逻辑并入此处）：

```
文案(带优先级) → PriorityQueue → 逐句切分(。！？；换行) → ITTSProvider.SynthesizeAsync(句子)
  → AudioClip 队列 → AudioSource 顺序播放
  → 播放中：音量包络(RMS) → VRM `Aa` 表情口型 + Animator isTalking=true
  → 气泡同步逐句显示文字（复用 Bubble，替掉 FakeStreamText 的假流式）
```

**TTS 抽象：**

```csharp
public interface ITTSProvider {
    Task<AudioClip> SynthesizeAsync(string text, CancellationToken ct);
    bool IsAvailable { get; }
}
```

三个实现，按配置选择：

| 实现 | 说明 | 推荐场景 |
|---|---|---|
| **OpenAICompatTTS** | `POST {baseUrl}/audio/speech`（OpenAI 兼容，硅基流动 CosyVoice、Minimax、火山等中文供应商均有兼容端点），返回 mp3/wav | **默认**——你已有云端 key，同一供应商体系内配置最省事 |
| **EdgeTTSProvider** | 微软 Edge 朗读接口（免费、中文音色好，`zh-CN-XiaoxiaoNeural` 等）。实现方式：内置一个 edge-tts 小工具进程或直接实现其 wss 协议 | 零成本备选 / 云 TTS 未配置时的默认降级 |
| **LocalTTSProvider** | 预留接口，对接 GPT-SoVITS/Piper 本地 HTTP 服务（自定义音色，如角色定制声线） | v3 进阶，接口先留 |

**音频解码**：mp3 → PCM 用工程**已内置的 NAudio 2.2.1**（`Mp3FileReader`/`WaveFormatConversionStream`），转 `AudioClip.Create`；wav 直接解析。不需要新增依赖。

**低延迟流水线**：LLM 流式输出攒到句末标点即切句提交 TTS，第一句合成完就开播，后续句子边播边合成（预取 1-2 句）。用户体感延迟 ≈ LLM 首句时间 + 单句 TTS 时间（1~2s 量级）。

**口型同步（v1 方案：音量驱动）**：

- 播放中每帧 `AudioSource.GetOutputData` 取 RMS → 映射 0~1 → 写 VRM10 `Aa` 表情权重（经 `Vrm10RuntimeExpression.SetWeight`，工程已含 VRM10 包；VRM0 模型走 UniVRM BlendShapeProxy 等价路径，参照现有 `UniversalBlendshapes.cs` 的双版本兼容写法）。
- 平滑：Attack/Release 包络（快张慢合），避免嘴部抖动。
- 与现有系统协调：说话期间置 `isTalking=true`（沿用现有动画反应），结束淡出口型归零。音素级精确口型（uLipSync 等）留作后续增强，音量驱动在桌宠场景已足够自然。

**优先级与打断**：P0 礼物致谢 > P1 AI 回复 > P2 关注/里程碑 > P3 欢迎 > P4 点赞。队列里低优先级过期即弃（欢迎超 30s 没轮到就丢）；P0 可打断当前播报（当前句播完即切）。跳舞时舞蹈音乐与语音冲突：默认"跳舞期间只播 P0，语音时暂降舞曲音量（ducking，AudioMixer 一条 duck 通道即可）"。

### 3.8 文字气泡

保留气泡与语音同步显示（听障观众/静音场景友好），复用现有 `Bubble` 预制体，文字按当前正在播放的句子刷新。TTS 全部不可用时自动降级为 v1 纯气泡 + 咕噜音模式——**功能不因断网而失效**。

## 4. 配置与设置 UI

扩展 [SaveLoadHandler.cs](../Assets/MATE%20ENGINE%20-%20Scripts/Settings/SaveLoadHandler.cs) 的 `SettingsData`：

```csharp
// Douyin Live
public bool  enableDouyinLive = false;
public string douyinWsUrl = "ws://127.0.0.1:8888";
public bool  douyinWelcomeEnabled = true, douyinAIReplyEnabled = true;
public bool  douyinLikeReactEnabled = true, douyinGiftEnabled = true;
public float douyinWelcomeCooldown = 8f, douyinAIReplyMinInterval = 8f;
public int   douyinLikeThreshold = 100;
public List<GiftRuleData> douyinGiftRules = new();
public string douyinLivePrompt = "";

// Cloud AI（key 不在此，DPAPI 单独存）
public string aiBaseUrl = "";      // 如 https://api.deepseek.com/v1
public string aiModel = "";        // 如 deepseek-chat
public bool   aiFallbackToLocal = true;

// TTS
public int    ttsProvider = 0;     // 0=OpenAI兼容 1=EdgeTTS 2=Local 3=关闭(纯气泡)
public string ttsBaseUrl = "", ttsModel = "", ttsVoice = "";
public float  ttsVolume = 1f, ttsSpeed = 1f;
public float  lipSyncGain = 1f;
```

设置页新增 "Douyin Live" 标签（按现有 `SettingsHandler*` 模式）：总开关、连接状态灯、各功能开关与冷却滑条、AI 供应商配置（baseUrl/model/key 密码框 + "测试连接"按钮）、TTS 供应商与音色下拉 + "试听"按钮、礼物规则（v1 先 JSON 文件手编）。

## 5. 关键约束与风险

| 风险 | 对策 |
|---|---|
| 上游抓包工具随抖音协议变更失效 | 网络层 `IDanmakuSource` 接口化，可换官方 OpenLive 或其他抓取器 |
| 系统代理与用户已有代理冲突 | 文档说明 + 设置页状态提示；工具自带代理还原 |
| 云 API 断网/限流/欠费 | 三级降级：云 LLM→本地 LLM→不回复；云 TTS→EdgeTTS→纯气泡。每级独立可用性探测 |
| API key 泄露（直播是屏幕共享场景！） | DPAPI 加密存储、密码框显示、文档提醒不要在推流中打开设置页 |
| 云端费用 | 回复限流本身就是费用闸门；设置页显示本场已回复条数；TTS 按句合成、欢迎语等短文案可加本地缓存（同文案不重复合成） |
| TTS 延迟 | 句级流水线（3.7）；欢迎/致谢等固定模板预合成缓存 |
| 语音与舞曲/系统声音打架 | AudioMixer ducking；跳舞期间仅 P0 播报 |
| 口型在不同模型上表现差异 | VRM10/VRM0 双路径 + `lipSyncGain` 用户可调 |
| 合规 | 只读公开弹幕、本机使用，不自动发送/伪造互动；TTS 播报内容含 AI 生成声明可选开关 |

## 6. 实施计划

1. **P1 通路**：DouyinLiveClient + Router + 弹幕原文气泡直显。验收：直播间发弹幕，桌宠气泡显示；实测确认各 Type 编号与字段。
2. **P2 语音管线**：SpeechPipeline + OpenAICompatTTS + EdgeTTS 降级 + NAudio 解码 + 音量口型。验收：任意文案入队即开口说话、口型同步、优先级生效。
3. **P3 欢迎+点赞**：WelcomeService、LikeService 接入语音管线，模板预合成缓存，设置页雏形。
4. **P4 AI 回复**：CloudChatBackend（SSE 流式）+ 限流筛选 + 直播 Prompt + 句级 LLM→TTS 流水线 + 本地降级。
5. **P5 礼物点歌点舞**：RewardService + 礼物规则 + 点播命令 + 播放队列 + 舞曲 ducking。

新增代码集中在 `Game APIs/DouyinLive/`（约 10 个类）+ `SettingsData` 字段 + 一个设置页；对现有系统零内部改动，全部经公开方法集成。
