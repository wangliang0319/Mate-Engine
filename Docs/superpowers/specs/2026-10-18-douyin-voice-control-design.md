# 麦克风语音控制：唤醒词 → 云端 ASR → 复用现有意图链路

**日期**：2026-10-18
**分支**：main（实施时开功能分支）
**状态**：设计已确认，待实施

## 问题

直播时主播想用嘴控制角色，像手机语音助手：说「小美小美 唱一首黄昏」，角色就开始唱跳。现在所有互动只能靠弹幕，主播自己没有入口。

**意图链路已经完整存在**——弹幕「点歌 黄昏」→ `Route()` → 触发规则 → 追问槽位 → LLM 意图判定 → 点歌/换角色/AI 闲聊，全链路可复用（[DouyinLiveManager.Route](../../../Assets/MATE%20ENGINE%20-%20Scripts/Game%20APIs/DouyinLive/DouyinLiveManager.cs#L296)）。缺的只是**前面一段**：麦克风 → 唤醒词 → 录音 → 语音转文字 → 剥唤醒词 → 伪装成弹幕塞进 `Route()`。项目目前没有任何麦克风代码。

```
小美小美 唱一首黄昏
  → 唤醒(本地关键词) → 录到静音 → 云端 ASR「小美小美,唱一首黄昏」
  → 剥唤醒词「唱一首黄昏」→ 伪装 Chat 弹幕 → 现有 Route() 全链路
  → 意图判定 song:黄昏 → 开唱 + 原生跟舞
```

## 目标

1. 主播对麦克风说唤醒词 + 命令，角色 2.5~4 秒内开始表演（唱跳/换角色/触发词动作）。
2. 非命令内容（闲聊/提问）走现有 AI 回复引擎，角色用 TTS 开口回答。
3. 追问槽位免费获得：她问「想听什么歌呀」，口头答「小美小美 黄昏」能补槽。
4. 平时（游戏声、直播说话、角色自己出声）不误触发。

## 非目标

- 不做本地语音识别（Windows 听写）——中文识别质量差，云端 ASR 已确认。
- 不做语音打断（角色说话/唱歌中喊命令无效，说完自然恢复）。
- 不做说话人识别——麦克风默认就是主播的；现场观众喊也能触发，属线下互动特性。
- 不做设置 UI——按项目惯例直接编辑 settings.json。
- 不做多轮免唤醒——每轮命令都要带唤醒词（追问补槽除外，见行为细节）。

## 架构

```
麦克风 ──> VoiceCommandListener (MonoBehaviour, 与 DouyinLiveManager 同物体)
             ├─ 本地唤醒: KeywordRecognizer(wakeWords, Medium)
             │    └─ PhraseRecognitionSystem 不支持/无中文语音包 → 自动降级纯音量检测(VAD)
             ├─ 环形缓冲: 持续缓存最近 ~3 秒音频(唤醒词和命令一口气说完不丢头)
             ├─ 唤醒后录到「静音 1.2s」或最长 8s → 取 [前 3 秒 + 后段] → WAV
             ├─ CloudASRProvider: POST {baseUrl}/audio/transcriptions (OpenAI 兼容)
             ├─ VoiceCommandParser: 从文本剥唤醒词 → 纯命令文字
             └─ DouyinLiveManager.InjectVoiceChat(命令文字)
                   └─ 伪装 Chat 弹幕(UserId="voice") → 现有 Route() 全链路原样复用
```

**注入点是复用而不是旁路**：语音命令就是一条 `Type=Chat, UserId="voice", Nickname="主播", FromVoice=true` 的 `DouyinEvent`，走完整的 `Route()`——触发规则的限流、槽位、LLM 意图、观众记忆、冷场计时全部与弹幕行为一致，不另立执行通道。

「唱一首黄昏」含「唱」，命中 [IntentText.LooksLikeIntent](../../../Assets/MATE%20ENGINE%20-%20Scripts/Game%20APIs/DouyinLive/Core/IntentText.cs#L64) 预筛 → LLM 判 `song:黄昏` → `TryHandleIntent` 执行，已验证链路成立。

## 组件

### Core（零依赖，EditMode 可测）

`Core/` 保持不引用 `UnityEngine`、`Newtonsoft`，时间/样本由外部喂。

#### `Core/VoiceCommandParser.cs`

```csharp
public static class VoiceCommandParser
{
    // 从 ASR 文本剥掉唤醒词，返回命令文字。
    // requireWakeWord=true（VAD 模式）：文本里找不到唤醒词 → 返回 null（不执行）。
    // requireWakeWord=false（关键词模式）：识别器已确认唤醒，整句放行，找得到才剥。
    // 只剥掉唤醒词、没有任何命令内容 → 返回 ""。
    public static string ExtractCommand(string text, IReadOnlyList<string> wakeWords, bool requireWakeWord);
}
```

- 多个唤醒词取**最早出现**的位置，剥到该词结尾（「你好小美小美，唱歌」剥掉「你好小美小美」）。
- 「小美小美」「小美」并存时剥整词不剥一半——先匹配长词。
- 剥后 Trim 首尾标点/空白（`,。!?~、 ` 等，含全角）。
- 忽略大小写（英文唤醒词场景）。
- ASR 无标点输出（「小美小美唱一首黄昏」）与有空格变体（「小美 小美 唱歌」）都能剥。

#### `Core/VoiceCaptureBuffer.cs`

```csharp
public class VoiceCaptureBuffer
{
    public int SampleRate = 16000;
    public float PreWakeSeconds = 3f;       // 环形缓冲常驻长度
    public float MaxUtteranceSeconds = 8f;  // 默认值与 douyinVoiceCommandWindow 对齐，ApplySettings 用配置覆盖
    public float SilenceSeconds = 1.2f;
    public float RmsThreshold = 0.015f;     // 低于视为静音

    public void Push(float[] samples);      // 主循环每帧增量喂
    public void NotifyWake();               // 关键词命中 / VAD 起音
    public bool UtteranceReady { get; }     // 静音达标或超长
    public bool TryTakeUtterance(out float[] samples);  // [前 PreWake 秒 + 唤醒后全部]
    public void Clear();
}
```

- 环形数组容量 = PreWake + MaxUtterance + 1 秒余量，旧数据自然滚掉。
- 每次 Push 算该 chunk 的 RMS。唤醒（`NotifyWake`）后开始累计：连续静音 ≥ `SilenceSeconds` 或唤醒后总长 ≥ `MaxUtteranceSeconds` → `UtteranceReady`。
- `TryTakeUtterance` 返回 `[wakeIndex − PreWake 样本数, 当前写点)` 的拼接片段，取完复位状态机（回到待唤醒）。
- 未唤醒时 `TryTakeUtterance` 返回 false；`Clear` 丢弃全部缓冲。

#### `Core/WavEncoder.cs`

```csharp
public static class WavEncoder
{
    public static byte[] Encode(float[] samples, int sampleRate);  // 16bit PCM mono WAV
}
```

44 字节 RIFF 头 + 16bit PCM，clamp 到 [−1,1]。采样率写进头里（设备实际采样率可能是 44.1k/48k，服务端自己重采样，客户端不转）。

#### `Core/DouyinEventTypes.cs`

`DouyinEvent` 加一个字段：

```csharp
public bool FromVoice;   // 语音助手注入的虚拟弹幕
```

### Unity 层（Assembly-CSharp，保持薄）

#### `CloudASRProvider.cs`（普通类，照 [OpenAICompatTTS](../../../Assets/MATE%20ENGINE%20-%20Scripts/Game%20APIs/DouyinLive/OpenAICompatTTS.cs) 的样式）

```csharp
public class CloudASRProvider
{
    public string BaseUrl = "";       // 空 = 复用 aiBaseUrl（装配时填）
    public string ApiKey = "";        // 空 = 复用 aiApiKey
    public string Model = "FunAudioLLM/SenseVoiceSmall";
    public bool IsAvailable { get; }
    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct);  // 失败/空 → null
}
```

- `POST {BaseUrl.Trim().TrimEnd('/')}/audio/transcriptions`，`Bearer` 鉴权，`multipart/form-data`：`file=audio.wav`、`model`、`language=zh`。
- 响应 `{"text": "..."}`，Newtonsoft 取 `text`；取不到/失败/超时（15s）→ null。
- 静态共享 `HttpClient`，与现有 Provider 一致。

#### `VoiceCommandListener.cs`（MonoBehaviour，`DouyinLiveManager.ApplySettings` 挂到同物体）

职责：麦克风采集、唤醒、拼装、调 ASR、上交命令。**不做任何意图判断**。

```
配置注入: wakeWords / Enabled / 参数 / CloudASRProvider / speech / songService / manager 引用

Update:
  1. 防自触发: speech.IsSpeaking || speech.QueueCount > 0 || songService.IsPlaying
     → 停 PhraseRecognitionSystem、清环形缓冲、不喂 VAD；恢复时重启识别
  2. Microphone.GetPosition 增量 GetData → buffer.Push
  3. VAD 模式: RMS 连续 0.3s 超阈值 → NotifyWake（一次性）
  4. UtteranceReady → TryTakeUtterance → WavEncoder.Encode
     → TranscribeAsync（在飞标记，期间忽略新唤醒）
     → 主线程回调: ExtractCommand → 空串/空/异常 → debugLog 丢弃
     → manager.InjectVoiceChat(command)
唤醒模式:
  KeywordRecognizer(wakeWords, ConfidenceLevel.Medium) + OnPhraseRecognized → buffer.NotifyWake()
  PhraseRecognitionSystem 不支持或初始化抛异常 → keywordAvailable=false，
  自动落 VAD 模式并 log 一次（「无中文语音包，已降级音量检测」）
OnDestroy: Microphone.End + PhraseRecognitionSystem.Shutdown
```

- `Microphone.Start(设备, true, 15, 16000)` 常开循环录音；`Microphone.devices` 为空（无设备/系统权限关）→ log 一次并禁用自己。
- KeywordRecognizer 与 Unity Microphone 同时占用设备：两者都走 WASAPI 共享模式，理论可并存；若实测拾音异常，关掉本地唤醒配置即落 VAD 模式（见手工验证）。
- ASR 在飞时新的唤醒被忽略——防连发重入。

#### `DouyinLiveManager.cs` 改动

```csharp
public void InjectVoiceChat(string text)
{
    if (!running || IsBlocked()) return;        // 停播/菜单打开：消费不响应，与弹幕一致
    var ev = new DouyinEvent {
        Type = DouyinMsgType.Chat, UserId = "voice",
        Nickname = data.douyinVoiceNickname, Content = text,
        FromVoice = true, ReceivedAt = DateTime.Now
    };
    Route(ev);
}
```

`HandleChatLegacy` 分流（陪播模式下语音不能被「念弹幕」——给自己念自己的话）：

```csharp
void HandleChatLegacy(DouyinEvent ev)
{
    if (reward.TryHandleDanmaku(ev)) return;
    if (ev.FromVoice) { danmakuAI.OnDanmaku(ev); return; }   // 语音闲聊始终走 AI
    if (ChatMode == 1) reader.OnDanmaku(ev);
    else danmakuAI.OnDanmaku(ev);
}
```

`ApplySettings` 装配：`CloudASRProvider`（BaseUrl/ApiKey 空则复用 AI 的）、`VoiceCommandListener`（惰性 AddComponent，`enableDouyinLive && douyinVoiceEnabled` 才启用）；`StartLive`/`StopLive` 调 `listener.ResetSession()`（清缓冲；停播时在途 ASR 回调判 `running` 丢弃，与意图判定的停播守卫同款）。

## 配置

settings.json 新增（`SaveLoadHandler.SettingsData` 字段默认值如下，老文件缺失自动落默认）：

```jsonc
"douyinVoiceEnabled": false,           // 总开关，默认关
"douyinVoiceWakeWords": ["小美小美", "小美"],   // 建议改成角色人设的名字；含「小美」时
                                                  // 说「小美小美」也能命中（包含即命中）
"douyinVoiceAsrBaseUrl": "",           // 空 = 复用 aiBaseUrl
"douyinVoiceAsrApiKey": "",            // 空 = 复用 aiApiKey
"douyinVoiceAsrModel": "FunAudioLLM/SenseVoiceSmall",  // SiliconFlow；OpenAI 官方用 whisper-1
"douyinVoiceCommandWindow": 8.0,       // 唤醒后最长录音秒数
"douyinVoiceSilenceSeconds": 1.2,      // 静音多久算说完
"douyinVoicePauseWhilePerforming": true,   // 角色出声时暂停监听（防自触发）
"douyinVoiceNickname": "主播"          // 语音事件在观众记忆/房间上下文里的名字
```

- `douyinVoiceCommandWindow` / `douyinVoiceSilenceSeconds` 钳制范围：2~15 / 0.5~3。
- 语音功能整体在 `enableDouyinLive` 开启时才工作（与现有系统同门）。

## 行为细节

- **防自触发闭环**：角色出声期间（TTS 播放中、队列有词、唱歌中）暂停唤醒并清缓冲——否则她唱歌的歌声被麦克风拾到会乱触发。`douyinVoicePauseWhilePerforming=false` 可关（唱歌中也听命令，误触发自担）。默认开。
- **追问槽位**：槽按 `UserId` 索引，语音命令固定 `UserId="voice"`——她自己开的槽自己口头补得上；观众开的槽语音补不了（正确行为）。
- **限流与弹幕一致**：TriggerLimiter 四闸、IntentResolver 单人 15 秒冷却、MaxInFlight 2——语音命令 15 秒内连发需要 LLM 判意图的第二条会落闲聊。与弹幕行为对称，不 special-case。
- **失败静默**：ASR 失败/超时/空文本/只有唤醒词没内容 → 只记 debugLog，不开口、不出气泡，不打扰直播。
- **延迟预算**：说完话 → 1.2s 静音判定 → ASR ~1s → 意图判定 ≤1.5s → 开唱。全程约 2.5~4 秒。
- **观众记忆/房间上下文**：语音事件照常记入（`audience.RecordMessage`/`room.AddChat`，昵称=主播）——AI 回复弹幕时能看到主播刚说过什么，是上下文不是污染。

## 错误处理

| 情况 | 行为 |
|---|---|
| 无麦克风设备/系统权限关闭 | log 一次，listener 自禁用，不影响其它功能 |
| wakeWords 列表为空 | log 一次，listener 自禁用（KeywordRecognizer 不接受空表） |
| PhraseRecognitionSystem 不支持/无中文语音包 | 自动降级 VAD 模式，log 一次 |
| KeywordRecognizer 与 Microphone 抢设备（实测冲突） | 设置里关掉本地唤醒 → VAD 模式兜底 |
| ASR 请求失败/超时/响应无 text | 静默丢弃（debugLog） |
| 转写文本无唤醒词（VAD 模式） | 丢弃（关键词模式信任识别器，整句放行） |
| 只有唤醒词没命令内容 | 静默丢弃 |
| ASR 在飞时又唤醒 | 忽略新唤醒 |
| 停播时在途 ASR 回调 | 判 `running` 丢弃（同意图判定守卫） |
| 菜单打开时说话 | `InjectVoiceChat` 直接 return，与弹幕「消费不响应」一致 |
| 语音命令连发 | 四闸 + 单人冷却拦，与弹幕对称 |

## 测试

### EditMode 单元测试（新增 3 个文件，约 25 个用例）

`Tests/` 下沿用 `MateEngine.DouyinLive.Tests` asmdef：

- **`VoiceCommandParserTests`**：
  - 「小美小美，唱一首黄昏」→「唱一首黄昏」；无标点「小美小美唱一首黄昏」同
  - 「你好小美小美。来首歌」→「来首歌」（嵌套前缀）
  - 「小美。」「小美小美」→ ""（只有唤醒词）
  - requireWakeWord=true 无唤醒词 → null；requireWakeWord=false → 整句放行
  - 「小美 小美 唱歌」（ASR 空格变体）；多唤醒词取最早出现；长词不被短词拆坏；英文大小写
- **`VoiceCaptureBufferTests`**：Push 环形滚掉旧数据；NotifyWake 后片段含 PreWake 长度；连续出声不 Ready、静音 SilenceSeconds 后 Ready；MaxUtteranceSeconds 强制 Ready；Take 后复位；Clear 清空；未唤醒 TryTake 返回 false
- **`WavEncoderTests`**：44 字节头 / RIFF、WAVE 标记正确；DataLength = 2×样本数；0.5 → 16384；±1 clamp；采样率写进头

**现有全部测试必须保持全绿。**

### 编译与回归

Unity batchmode 编译零 `error CS`（先关编辑器防 lockfile 假绿，`grep -ci "another Unity instance"` 确认真的编译了；测试结果在 LocalLow 不听 `-testResults`）。

### 手工验证（只有主播能跑）

1. settings.json 开 `douyinVoiceEnabled` → 说「小美小美 唱一首黄昏」→ 她开口接话并开唱黄昏 + 跟舞
2. 「小美小美 换角色」→ 换角色（30 秒冷却内第二次会被拦，正常）
3. 「小美小美 今天直播什么好」→ AI 开口聊
4. 她问「想听什么歌呀」→ 口头答「小美小美 黄昏」→ 开唱（补槽链路）
5. 她正在说话/唱歌时喊唤醒词 → 不响应；唱完恢复监听
6. F9 切游戏陪播后：说命令 → 照常执行；说闲聊 → AI 回复（不被「念弹幕」念出来）
7. 不说话/放游戏声/刷视频 → 无动作、无 ASR 请求（debugLog 观察）
8. `douyinVoiceEnabled=false` 或删掉字段 → 完全回到现状
9. （无中文语音包的机器）自动降级 VAD 模式仍可用，日志有降级提示
10. 菜单打开时说话 → 无动作，关菜单后正常

## 任务拆分

| # | 任务 | 产出 |
|---|---|---|
| 1 | `Core/VoiceCommandParser.cs` + 测试 | 唤醒词剥离 |
| 2 | `Core/VoiceCaptureBuffer.cs` + 测试 | 环形缓冲 + 静音状态机 |
| 3 | `Core/WavEncoder.cs` + 测试 | WAV 编码 |
| 4 | `DouyinEvent.FromVoice` + `InjectVoiceChat` + `HandleChatLegacy` 分流 | 注入点 |
| 5 | `CloudASRProvider` | 云端转写 |
| 6 | `VoiceCommandListener` + `ApplySettings` 装配 + StartLive/StopLive 接线 | 采集与编排（到这里手工 1~7 应全通） |
| 7 | settings 字段 + `DouyinLive-Integration.md` 文档 | 配置与文档（手工 8~10） |

任务 1~3 纯 Core 可并行先行；任务 6 结束功能已完整可用，任务 7 是收尾。
