# 语音传话筒：唤醒后 AI 意图路由（传话 / 聊天 / 表演）

**日期**：2026-10-18
**分支**：main（实施时开功能分支）
**状态**：设计已确认，待实施

## 问题

语音助手已上线（唤醒 → ASR → 伪装弹幕进 Route()），但非命令语音现在只有一种去向：小美把它当提问、AI 生成一句回复。主播要的是**传话筒**——直播时说「等会儿有抽奖哦」，小美把这句话**原样播报**给观众，而不是自作主张回一句。

用户明确要求**机制化**：唤醒后由 AI 统一理解意图，能做的事（唱/跳/换角色/传话/聊天）各自路由到各自的能力，而不是拿全局开关二选一。

三个已确认的形态决策：

1. **传话筒**：不针对具体弹幕，说什么播什么。
2. **原话直说**：ASR 转出什么就 TTS 什么，零 LLM 润色、零额外延迟，内容百分之百是主播自己的话（代价：口语停顿、重复也原样播出——主播自己适应措辞）。
3. **意图路由**（替代最初的配置切换方案）：AI 分类传话 vs 聊天；配置开关只决定传话是否进入指令集。

## 目标

1. 主播说「小美小美 等会儿有抽奖哦」→ 小美 3.5~5 秒内把这句话原样 TTS 播出。
2. 「小美小美 今天唱什么好」这类对角色说的话 → 仍走 AI 对话回复（现状不变）。
3. 唱跳/换角色/点歌/触发词/追问补槽 → 全部现状不变。
4. 指令集可扩展：以后新增能力就多一种意图。

## 非目标

- 不做润色（原话直说已确认）。
- 不针对弹幕定向回复（那是另一种形态，已排除）。
- 不做持久口述策略（已排除）。
- 分类器不做歌/舞/换角色三类的识别——它们继续走本地关键词预筛（`IntentText.LooksLikeIntent`），无关键词的表演说法（如「来段芭蕾」）维持现状走聊天，不属回归。
- 不动弹幕侧：分类器只服务语音路径。

## 架构

```
唤醒 → ASR → 剥唤醒词 → InjectVoiceChat → Route()
  ├─ 点歌/换角色/菜单 硬命令 ─────────→ RewardService        不变
  ├─ douyin_triggers.json 规则命中 ───→ 触发层               不变
  ├─ 槽位补全（她问、主播答）─────────→ TryFillSlot          不变
  ├─ 含 歌/唱/舞/换 表演关键词 ───────→ IntentResolver       不变
  └─ 其余（HandleChatLegacy 的 FromVoice 分支）
        ├─ douyinVoiceRelayEnabled=false ──→ danmakuAI        不变（回到今天）
        └─ VoiceIntentRouter（LLM ≤1.5s，一次调用）
              ├─ relay（传话）→ speech.Enqueue(原话)          新增
              └─ chat（聊天）→ danmakuAI.OnDanmaku            不变
```

前面四层全部复用现有管线，**唯一新增的是最后一层的二分类**。注入点在 `HandleChatLegacy` 的 `FromVoice` 分支——`Route()` 主干一行不动，语音特有行为全部收敛在既有分支里。

## 组件

### Core（零依赖，EditMode 可测）

#### `Core/IntentSlots.cs`（IntentKind 所在文件）

```csharp
public enum IntentKind { None, Song, Dance, Avatar, Relay }
```

#### `Core/IntentText.cs`

`TryParseIntentJson` 的 switch 加 `case "relay": kind = IntentKind.Relay; break;`。`chat`/`none`/其它 → `None`。输出只当数据用，永远不当指令执行（既有原则）。

### Unity 层（Assembly-CSharp）

#### `VoiceIntentRouter.cs`（新，普通类，照 [IntentResolver](../../../Assets/MATE%20ENGINE%20-%20Scripts/Game%20APIs/DouyinLive/IntentResolver.cs) 的异步骨架）

```csharp
public class VoiceIntentRouter
{
    public IChatBackend Cloud;
    public bool debugLog;
    public const float TimeoutSeconds = 1.5f;
    public void Reset();

    // 返回 true = 已接管（正在问大模型）；false = 后端不可用/已在飞，调用方直接走 chat
    public bool TryRoute(DouyinEvent ev, Action<string> onRelay, Action<DouyinEvent> onChat);
}
```

- 提示词（system）：

  > 你是主播语音意图分类器。主播正在对桌面虚拟角色说话，判断这句话的目的：
  > relay=让角色把这段话原样转达给直播间观众（公告/通知/交代事情），
  > chat=在和角色对话、问它问题、或让它做事。
  > 只输出 JSON，不要任何解释：{"intent":"relay|chat"}

  `history` 传空，`onDelta` 传 null。
- 异步骨架与 IntentResolver 相同：`CancellationTokenSource` + `Task.WhenAny` 掐 1.5 秒表、`ContinueWith` 观测丢弃任务、结果经 `MainThreadDispatcher.Post` 转主线程。
- **不做单人冷却、MaxInFlight = 1**：ASR 静音判定天然限速，且传话是主播连续说话的合法用法（15 秒冷却会废掉它）。
- 分类结果只取 intent：`relay` → `onRelay(ev.Content)`（**剥完唤醒词的原话，一字不改**）；`chat`/`none`/解析失败 → `onChat(ev)`。
- `Reset()` 清 inFlight（StartLive/StopLive 调用）；回调主线程判 `running`，停播后不动作。

#### `DouyinLiveManager.cs` 改动

- 字段：`VoiceIntentRouter voiceIntents; bool voiceRelayEnabled;`
- `ApplySettings` 装配：`voiceIntents ??= new VoiceIntentRouter(); voiceIntents.Cloud = cloudBackend; voiceIntents.debugLog = debugLog; voiceRelayEnabled = d.douyinVoiceRelayEnabled;`
- `StartLive`/`StopLive`：`voiceIntents.Reset()`（与 `intents.Reset()` 同位）。
- `HandleChatLegacy` 的 FromVoice 分支改为：

```csharp
if (ev.FromVoice)
{
    // 传话路由：开关开才问分类器；不可用/在飞/超时一律当聊天（不打扰直播）
    if (voiceRelayEnabled && voiceIntents.TryRoute(ev, OnVoiceRelay, OnVoiceChat)) return;
    danmakuAI.OnDanmaku(ev);
    return;
}

void OnVoiceRelay(string text)
{
    if (!running) return;
    speech.Enqueue(text, SpeechPipeline.Priority.AIReply, 30f);
}

void OnVoiceChat(DouyinEvent ev)
{
    if (!running) return;
    danmakuAI.OnDanmaku(ev);
}
```

- 传话播报优先级 `AIReply`（P1，与 AI 回复同级；礼物 P0 仍优先）。时长上限参数 30 秒，超长照既有队列规则处理。

## 配置

```jsonc
"douyinVoiceRelayEnabled": true,   // 传话意图是否进入指令集；false 时行为完全回到今天
```

`SaveLoadHandler.SettingsData` 加一个 bool，默认 **true**（这是本功能的本体）。关闭时 `HandleChatLegacy` 的 FromVoice 分支直接走 danmakuAI，路由器不产生任何 LLM 调用。

## 错误处理

| 情况 | 行为 |
|---|---|
| 分类器超时/失败/输出非法 | `onChat`——她和你对话而不是播报，体面降级 |
| Cloud 后端不可用/已在飞 | `TryRoute` 返回 false，直接 chat，零等待 |
| 传话开关关闭 | 不问分类器，行为回到今天 |
| 停播时在途分类回调 | 主线程判 `running` 丢弃（同意图判定守卫） |
| 菜单打开 | `InjectVoiceChat` 既有守卫，消费不响应 |
| 她正在唱跳 | 监听暂停（防自触发既有机制），传话根本进不来 |
| 超长话 | 8 秒录音窗天然截断，不做文本截断（原话直说原则） |
| 传话内容撞上已开的追问槽位 | 槽位补全先于路由（既有顺序），她问、你答歌名照常接住 |
| 分类器把提问误判成传话（或反之） | 接受：LLM 分类的固有误判率，误传话→说了本想问的话，误聊天→她回了句嘴；两个方向都不致命 |

## 测试

### EditMode 单元测试（Core，新增 1 个文件）

`Tests/IntentTextRelayTests.cs`（并入现有 IntentText 测试风格）：

- `TryParseIntentJson("{\"intent\":\"relay\"}")` → `IntentKind.Relay`
- `{"intent":"chat"}` → `None`；`{"intent":"none"}` → `None`
- ```json 包裹 + relay → Relay；非法 JSON → false（回归，确认没破坏原有四类解析）
- 现有全部测试保持全绿

### 编译与回归

Unity batchmode 编译零 `error CS`；全量 EditMode 测试通过。

### 手工验证（只有主播能跑）

1. 开 `douyinVoiceEnabled` + 默认 `douyinVoiceRelayEnabled` → 说「小美小美 等会儿有抽奖哦」→ 她原样播出这句话（不续唠）
2. 说「小美小美 今天唱什么好」→ AI 对话回复（不是播出你的问题）
3. 说「小美小美 唱一首黄昏」→ 照常唱跳（命令不受路由影响）
4. `douyinVoiceRelayEnabled=false` → 第 1 条变成 AI 对话回复（回到今天）
5. 连续传话两条（间隔 >3 秒）→ 两条都播出（验证无 15 秒冷却）
6. 她唱歌中说传话 → 不响应，唱完恢复（既有防自触发）
7. 菜单打开时说传话 → 无动作
8. AI 后端不可用（断网或没配 aiBaseUrl）→ 说传话 → `TryRoute` 返回 false 直接走聊天路径，按 danmakuAI 现有降级逻辑回应，不卡壳不报错

## 任务拆分

| # | 任务 | 产出 |
|---|---|---|
| 1 | Core：IntentKind.Relay + TryParseIntentJson 认 relay + 测试 | 分类数据层 |
| 2 | VoiceIntentRouter + 配置字段 + Manager 接线（HandleChatLegacy 分流/装配/Reset）+ 文档 | 路由器与集成（到这里手工 1~7 应全通） |
| 3 | 手工验证执行（主播） | 验收 |
