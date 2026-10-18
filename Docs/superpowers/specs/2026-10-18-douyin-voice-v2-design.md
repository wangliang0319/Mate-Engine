# 语音交互 v2：离线唤醒 + 手机式对话

**日期**：2026-10-18
**状态**：设计已确认，待实施
**所属规划**：直播功能统筹规划的子项目 1（共 2 个，见文末「后续」）

## 问题

语音助手实机测试暴露三个问题：

1. **唤醒不可靠**：Windows 自带的 KeywordRecognizer 对中文短唤醒词识别差。三次 Play 里有一次完全没唤醒，另两次各唤醒一次后就再也唤不醒，「小美」还被听成「小米」。我们自己的状态机逐行查过，没有卡死点，问题在识别器内部，无法观测。
2. **必须一口气说完**：唤醒后 1.2 秒静音就结束录音。主播习惯了手机助手，喊完「小美小美」会停下来等回应，结果只录到唤醒词。
3. **她说话时唤不醒**：为防止把她自己的声音当指令，她一出声就暂停监听。游戏陪播时她不停念弹幕，主播几乎找不到空档唤醒她。

## 目标

1. 唤醒率接近 100%，可以连续多次唤醒。
2. 手机式交互：喊「小美小美」→ 她应答 →（停顿）→ 下指令 → 执行。同时兼容一口气说完。
3. 她说话、唱歌时也能被唤醒。
4. 回答之后可以直接追问，不用再喊唤醒词。

## 非目标

- 指令路由不变：开头词传话、唱跳换角色、其余对话，全部沿用现有逻辑（`InjectVoiceChat` → `Route()`）。
- 主播专属控制指令（切场景、让她安静、停止表演）不在本期，归子项目 2。
- 不做回声消除；不做中文到拼音的自动转换。
- 音量检测（VAD）降级模式保持现有行为（必须一口气说完、无应答）。

## 唤醒引擎：sherpa-onnx（已验证）

2026-10-18 做过可行性验证（spike，代码已丢弃）：

| 指标 | Windows 识别器 | sherpa-onnx |
|---|---|---|
| 真人麦克风唤醒率 | 3 次 Play 共 2 次 | 10/10，无误报 |
| 连续唤醒 | 一次后失灵 | 30 秒内 10 次，最短间隔 1.6 秒 |
| CPU | — | 实时率 0.019（约一个核的 2%） |

- 依赖：NuGet `org.k2fsa.sherpa.onnx` 1.13.8（托管 DLL 用 netstandard2.0 版）+ `org.k2fsa.sherpa.onnx.runtime.win-x64` 1.13.8（`onnxruntime.dll` + `sherpa-onnx-c-api.dll`，约 22MB）。放 `Assets/Plugins/SherpaOnnx/`（原生 DLL 在 `x86_64/` 子目录），Unity Mono 直接加载。
- 模型：`sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01` 的 **fp32** 版（encoder/decoder/joiner + tokens.txt，约 13MB），放 `Assets/StreamingAssets/SherpaOnnx/kws/`。int8 版没有实测过，不用。
- 下载渠道（国内）：NuGet 用 `nuget.azure.cn` 镜像；模型用魔搭 `pkufool/sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01`。GitHub release 直连太慢，不可用。
- API 注意：唤醒词可用 `KeywordsBuf` 直接传字符串；检出后必须对 stream 调 `Reset`；`KeywordSpotter` 有 `Dispose` 但不保证实现 `IDisposable`，显式调用。
- 安装包体积增加约 35MB。

## 交互流程

```
待机：sherpa 常驻检测唤醒词
  │ 检出唤醒词（她正在说话 → 打断当前句；正在唱歌/跳舞 → 音乐压到 30%）
  │ 播报队列暂停出队（弹幕念读等不开口，队列保留），直到她该回答时放开
  ▼
判停顿：唤醒时刻起立即开始录音，观察 0.4 秒
  ├─ 0.4 秒内累计有声 ≥ 0.15 秒（一口气说完）──► 聆听（跳过应答）
  └─ 未达标（唤醒词尾音不算）
        ▼
   应答：立即播放一句预合成的应答（「我在～」等，随机）+ 气泡「我在听～」
         应答期间不录音（无回声消除，避免把她的声音录进去）
        ▼
   聆听：说完后静音 1.2 秒即结束，最长 8 秒
         应答结束后 5 秒内没开口 → 安静地回待机
        ▼
   处理：WAV → 云端 ASR → 剥唤醒词 → InjectVoiceChat（路由不变）
         ASR 失败/超时 → 她说「没听清～」→ 回待机
        ▼
   等回应：等她说完话（最长 15 秒）
         ├─ 她说完时没在唱歌/跳舞 → 追问
         └─ 开始表演或超时 → 回待机
        ▼
   追问：6 秒窗口，连续开口 0.3 秒即进入聆听，无需唤醒词（窗口内播报队列同样暂停）
         转写为空或不足 2 个字 → 静默丢弃（噪音不当成一句话）
         6 秒没开口 → 回待机
```

会话回到待机时，恢复被压低的音乐音量，放开播报队列。

## 组件

### Core（纯逻辑，EditMode 测试）

**`VoiceSessionMachine`**（新）：会话状态机，不依赖 Unity。

- 状态：`Idle`、`WakePending`、`Ack`、`Listening`、`Processing`、`AwaitReply`、`FollowUp`。
- 输入（调用方每帧/每事件喂入，时间由调用方传入，便于测试）：
  - `OnWake(t, characterSpeaking, characterPerforming)`
  - `OnVoice(t, voiced)`：每段音频是否有声（RMS 超阈值）
  - `OnAckFinished(t)`
  - `OnTranscript(t, success)`
  - `OnCharacterState(t, speaking, performing)`
  - `Tick(t)`
  - `Abort(t)`：停播/菜单打开
- 输出：动作列表，调用方按顺序执行：`InterruptSpeech`、`HoldSpeech`、`ReleaseSpeech`、`DuckMusic`、`UnduckMusic`、`StartCapture`、`StopCapture`、`PlayAck`、`SubmitCapture`、`SayNotHeard`。
  - `HoldSpeech`/`ReleaseSpeech`：主播说话期间暂停播报队列出队（弹幕念读、欢迎等不再开口，队列保留），否则她的声音会混进主播的录音。应答走 `PlayImmediate`，不受暂停影响；`AwaitReply` 阶段放开，让她回答。
- 规则：
  - `Idle` 收到唤醒：她在说话 → `InterruptSpeech`；在表演 → `DuckMusic`；然后 `HoldSpeech` + `StartCapture`，进入 `WakePending`。
  - `WakePending`：0.4 秒窗口内**累计有声 ≥ 0.15 秒** → `Listening`；满 0.4 秒未达标 → `StopCapture` + `PlayAck`，进入 `Ack`。（sherpa 在唤醒词快说完时才检出，尾音会让窗口开头短暂有声；只要求「有声」会把尾音误判成一口气说完。）
  - `Ack` 收到 `OnAckFinished` → `StartCapture`，进入 `Listening`。
  - `Listening`：开口后静音满 1.2 秒，或录满 8 秒 → `StopCapture` + `SubmitCapture`，进入 `Processing`；进入后 5 秒未开口 → `StopCapture`，回 `Idle`。
  - `Processing`：转写成功 → `ReleaseSpeech`，进入 `AwaitReply`；失败 → `SayNotHeard`，回 `Idle`。
  - `AwaitReply`：观察到她「说话 → 不说话」且此刻未表演 → `HoldSpeech`，进入 `FollowUp`；开始表演 → 回 `Idle`；15 秒超时 → 回 `Idle`。
  - `FollowUp`：**连续有声 ≥ 0.3 秒**（与 VAD 起音同一标准，防咳嗽、键盘声）→ `StartCapture`，进入 `Listening`；6 秒无声 → 回 `Idle`。
  - 从 `FollowUp` 进入的聆听，转写结果为空或少于 2 个字时**静默丢弃**（不说「没听清」，不注入）：追问没有唤醒词把关，噪音转写出的乱码不能当成一句话发给她。
  - `WakePending/Ack/Listening/Processing` 期间再检出唤醒词：忽略。`AwaitReply/FollowUp` 期间检出：按 `Idle` 处理，开新会话。
  - 任何状态进入 `Idle` 时，若压低过音乐 → `UnduckMusic`，若暂停过播报 → `ReleaseSpeech`（各只发一次）。
  - `Abort`：正在录音则 `StopCapture`，并按上一条恢复音乐与播报，回 `Idle`。
- 所有时长作为构造参数（来自配置）。

**`WakeKeywordSpec`**（新）：解析、校验拼音唤醒词。

- 输入一行唤醒词（`x iǎo m ěi x iǎo m ěi @小美小美`，可带 sherpa 的单词灵敏度后缀）和模型词表。
- 每个音节必须在词表里；不在时返回具体是哪个音节。空行、缺少 `@显示名` 时也报错。
- 输出：有效唤醒词拼成的 `KeywordsBuf` 字符串，以及每个无效项的错误信息。

### Unity 层

| 组件 | 改动 |
|---|---|
| `SherpaWakeDetector`（新） | 加载 StreamingAssets 里的模型；用 `WakeKeywordSpec` 校验配置；`Feed(float[] samples)` 逐帧解码，检出时触发事件并 `Reset` stream；加载失败时 `IsAvailable=false` 并给出原因 |
| `VoiceCommandListener`（重写） | 管麦克风；每帧把新音频同时喂给 `SherpaWakeDetector`、`VoiceCaptureBuffer` 和音量检测；把事件喂给 `VoiceSessionMachine` 并执行它输出的动作；ASR 提交和转写回调沿用。**删除 Windows KeywordRecognizer 和 `PauseWhilePerforming` 暂停逻辑**（打断/压低音乐取而代之）。sherpa 不可用时走现有 VAD 降级流程 |
| `VoiceAckLines`（新） | 开播时用当前 TTS 预合成应答话术（默认「我在～」「嗯？」「你说～」「在呢」）缓存为 AudioClip，随机取用；合成失败或 TTS 关闭时只显示气泡 |
| `SpeechPipeline` | 新增 `InterruptCurrent()`：停当前句（复用现有停止逻辑），队列保留；新增 `PlayImmediate(string text, AudioClip clip)`：立即播放缓存音频，带口型和气泡；新增 `SetHold(bool)`：暂停/恢复队列出队，暂停期间队列保留，过期规则照常（TTL 到了照样丢） |
| `SongService` | 新增 `Duck(float level)` / `Unduck()`，作用于 `musicSource`；MMD 舞曲的 `AvatarDanceHandler.audioSource` 由监听器直接压低和恢复 |
| `DouyinLiveManager` | 停播、菜单打开时通知监听器 `Abort`；`ApplySettings` 传入新配置 |

`VoiceCaptureBuffer`、`WavEncoder`、`CloudASRProvider`、`VoiceCommandParser`、`VoiceRelayParser` 保持不变。

## 配置（SaveLoadHandler.SettingsData）

```jsonc
"douyinVoiceKeywordWake": true,          // 含义改为：true=sherpa 离线唤醒，false=音量检测模式
"douyinVoiceWakeKeywords": ["x iǎo m ěi x iǎo m ěi @小美小美"],  // 新：sherpa 拼音唤醒词
"douyinVoiceWakeWords": ["小美小美", "小美"],  // 保留：从 ASR 文本里剥唤醒词
"douyinVoiceAckLines": ["我在～", "嗯？", "你说～", "在呢"],      // 新
"douyinVoiceAckTimeout": 5.0,            // 新：应答后多久没开口就放弃
"douyinVoiceFollowUpSeconds": 6.0,       // 新：追问窗口，0=关闭
"douyinVoiceDuckVolume": 0.3,            // 新：唤醒时音乐压低到原音量的比例
```

判停顿 0.4 秒、累计有声 0.15 秒、追问起音 0.3 秒、等回应 15 秒作为代码常量，不开放配置。

`douyinVoicePauseWhilePerforming` 废弃：字段保留（老配置文件里有这个键，反序列化不能报错），不再读取。她说话、唱歌时的处理由打断、压低音乐和播报暂停取代。

## 出错处理

| 情况 | 处理 |
|---|---|
| sherpa DLL/模型缺失或加载失败 | 退回 VAD 模式；日志警告写明缺哪个文件 |
| 拼音唤醒词写错 | 跳过该词，日志指出写错的音节；全部无效则退回 VAD 模式 |
| 应答合成失败 | 只显示气泡，流程照常 |
| 应答后 5 秒没开口 | 安静回待机 |
| ASR 失败/超时 | 她说「没听清～」，回待机 |
| 停播/菜单打开 | `Abort`：丢弃会话，恢复音乐音量，放开播报队列 |
| 打断她说话 | 只停当前句，弹幕队列保留，会话结束后继续念 |
| 会话中再次检出唤醒词 | 录音/处理阶段忽略；等回应/追问阶段开新会话 |
| 追问窗口被噪音触发 | 转写为空或不足 2 字静默丢弃，不注入、不说「没听清」 |

## 测试

1. **`VoiceSessionMachine` 单元测试**（主体，约 25 个，模拟时钟）：一口气说完跳过应答；只有唤醒词尾音（有声 < 0.15 秒）仍触发应答；停顿触发应答；应答后 5 秒放弃；聆听 1.2 秒静音结束与 8 秒上限；ASR 失败说「没听清」；对话后开追问、表演后不开；追问窗口内连续开口 0.3 秒进聆听、短促声响不进；追问转写不足 2 字静默丢弃；追问超时回待机；说话时唤醒发 `InterruptSpeech`；唤醒发 `HoldSpeech`、进 `AwaitReply` 发 `ReleaseSpeech`、进追问再 `HoldSpeech`；表演时唤醒发 `DuckMusic`；回待机时 `UnduckMusic`/`ReleaseSpeech` 各恰好一次；`Abort` 恢复音量与播报；各阶段再次唤醒的忽略/开新会话。
2. **`WakeKeywordSpec` 单元测试**：合法拼音、音节不在词表、缺 `@`、空行、多个唤醒词混合有效与无效、带灵敏度后缀。
3. **编译 + 全量回归**：现有 176 个测试保持通过。
4. **真机验收**（主播，约 5 分钟）：连续唤醒 10 次；手机式对话；一口气说完；她念弹幕时打断；唱歌时唤醒（音乐压低后恢复）；追问；挪走 sherpa DLL 后退回 VAD 模式并有警告。

## 后续：子项目 2「直播场景统一」

本期完成后另起 spec。方向（已与主播确认）：

- 三个场景预设：**AI 独播 / 游戏陪播 / 搭档主持**，每个预设打包弹幕处理方式（AI 回复/念弹幕）、暖场与自动唱跳开关、语音能力；F9 从切 `douyinChatMode` 升级为切场景，`douyinChatMode` 被吸收但老配置兼容。
- 主播语音成为独立的操作者通道，新增主播专属控制指令：切场景、让她安静一会儿、停止唱歌/跳舞。
- README 重写，补齐语音助手与传话的说明。
