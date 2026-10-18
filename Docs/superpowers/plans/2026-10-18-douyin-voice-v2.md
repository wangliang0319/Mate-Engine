# 语音交互 v2（sherpa 离线唤醒 + 手机式对话）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用 sherpa-onnx 离线关键词引擎替换 Windows KeywordRecognizer，并把语音助手改成手机式会话：唤醒 → 应答 → 聆听 → 处理 → 追问，支持打断她说话、唱歌时压低音乐。

**Architecture:** 时序规则全部放进 Core 程序集里的纯状态机 `VoiceSessionMachine`（单元测试覆盖），它只吃事件、吐动作，不碰麦克风和 Unity。sherpa 封装成不依赖 UnityEngine 的独立小程序集 `MateEngine.DouyinLive.Sherpa`，用官方测试音频做自动化测试。`VoiceCommandListener` 重写为「读麦克风 → 喂唤醒引擎/录音缓冲/状态机 → 执行动作」的薄壳；指令路由（`InjectVoiceChat` → `Route()`）完全不动。

**Tech Stack:** Unity 6000.2.6f2（Mono，Windows x64）/ C# netstandard2.1 / NUnit EditMode / sherpa-onnx 1.13.8 / onnxruntime（随 sherpa NuGet 包）。

**Spec:** [Docs/superpowers/specs/2026-10-18-douyin-voice-v2-design.md](../specs/2026-10-18-douyin-voice-v2-design.md)

## Global Constraints

- 从 main 开功能分支 `feat/douyin-voice-v2`，不直接提交 main。
- Core 程序集 `MateEngine.DouyinLive.Core`：`references` 必须保持为空——Core 代码禁用 UnityEngine / Newtonsoft / Debug.Log / Time.* / Mathf.*（用 `System.Math`）。
- Tests 程序集 `MateEngine.DouyinLive.Tests`：`overrideReferences: true`，测试里用不了 Newtonsoft；只按本计划增加引用。
- 新程序集 `MateEngine.DouyinLive.Sherpa`：`noEngineReferences: true`，只引用 Core 和预编译的 `sherpa-onnx.dll`。
- sherpa 依赖：NuGet `org.k2fsa.sherpa.onnx` 1.13.8（托管 DLL 取 `lib/netstandard2.0/sherpa-onnx.dll`）+ `org.k2fsa.sherpa.onnx.runtime.win-x64` 1.13.8（`onnxruntime.dll`、`sherpa-onnx-c-api.dll`）。托管 DLL 放 `Assets/Plugins/SherpaOnnx/`，原生 DLL 放 `Assets/Plugins/SherpaOnnx/x86_64/`。
- 模型：`sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01` 的 **fp32** 版（`encoder/decoder/joiner-epoch-12-avg-2-chunk-16-left-64.onnx` + `tokens.txt`），放 `Assets/StreamingAssets/SherpaOnnx/kws/`。不用 int8 版。
- `KeywordSpotter` 有 `Dispose` 方法但不保证实现 `IDisposable`：显式调用，不要 `using`。检出唤醒词后必须对 stream 调 `kws.Reset(stream)`。
- 默认唤醒词：`x iǎo m ěi x iǎo m ěi @小美小美`；sherpa 灵敏度阈值 `KeywordsThreshold = 0.25f`、`KeywordsScore = 1.0f`（spike 实测 10/10 的参数）。
- 时长默认值（spec）：判停顿 0.4s、一口气说完累计有声 0.15s、应答后放弃 5s、说完静音 1.2s、最长 8s、等回应 15s、追问 6s、追问起音 0.3s、压低音量 0.3。
- 指令路由不变：`DouyinLiveManager.InjectVoiceChat` → `Route()` 一行不改。
- 提交信息中文、`feat(douyin-live):` / `test(douyin-live):` / `docs(douyin-live):` 风格，结尾 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。
- 只 `git add` 任务列出的文件（含 Unity 生成的对应 `.meta`）；工作区里 link.xml 删除、TestResults-*.xml、.mat、UserSettings、ProjectSettings 等无关变更一律不碰。
- 注释中文、解释「为什么」，与 DouyinLive 现有代码风格一致。

### 验证命令（所有任务共用）

**Unity 编辑器必须关着**（开着时 batchmode 拿不到项目锁，返回 rc=21，`error CS` 的 grep 会假绿）。先检查：

```bash
tasklist //FI "IMAGENAME eq Unity.exe" | grep -i Unity.exe || echo NO_EDITOR
```

有输出就停下来请用户关闭编辑器，不要继续。

编译：

```bash
"/c/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe" -batchmode -quit -projectPath "E:/Work/AI/Mate-Engine" >/dev/null 2>&1; echo "rc=$?"
L="C:/Users/83529/AppData/Local/Unity/Editor/Editor.log"
echo "errorCS=$(grep -c 'error CS' "$L") anotherInstance=$(grep -c 'another Unity instance' "$L")"
grep "error CS" "$L" | head -10
```

Expected：`rc=0 errorCS=0 anotherInstance=0`。Unity 不往 stdout 写东西，结论只看 Editor.log。

EditMode 测试（结果文件在 LocalLow，**跑前必删**，不删会读到上一次的结果）：

```bash
R="C:/Users/83529/AppData/LocalLow/Shinymoon/MateEngineX/TestResults.xml"; rm -f "$R"
"/c/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe" -batchmode -runTests -projectPath "E:/Work/AI/Mate-Engine" -testPlatform EditMode -assemblyNames MateEngine.DouyinLive.Tests -testFilter "<过滤器>" >/dev/null 2>&1; echo "rc=$?"
grep -o '<test-run[^>]*' "$R" | grep -o 'result="[^"]*"\|total="[0-9]*"\|passed="[0-9]*"\|failed="[0-9]*"'
grep -o '<test-case [^>]*result="Failed"' "$R" | grep -o 'fullname="[^"]*"' | head
```

全量跑时去掉 `-testFilter` 参数。`rc=198` = Unity 许可证过期：停下来请用户打开一次 Unity Hub 刷新许可后重跑。编译失败时测试程序集不会生成、也没有结果文件（这就是 Unity 下的 RED）——去 Editor.log 看 `error CS`。

## Review Focus

计划里的自动化测试覆盖不到、最可能咬到主播的五类情况。审校逐条核对：

1. **打断/立即播放后口型和资源**：`InterruptCurrent()` 打断时，角色是否会卡在张嘴状态（`isTalking` 必须置 false），被打断那句的 TTS `AudioClip` 是否销毁（不能泄漏）；`PlayImmediate` 播的是缓存的应答音，**绝不能被销毁**（销毁后下次应答就没声了）。Task 4 归属。
2. **压低音乐的恢复**：会话期间新歌开播仍保持压低（`SearchAndPlay` 里音量用当前压低值而不是写死 1）；MMD 舞曲恢复到压低前的原音量而不是 1；停播/菜单打开/监听器被禁用时都能恢复。Task 4、Task 5 归属。
3. **背景声让「说完」判定失效**：游戏声或被压低的歌声持续超过 `VoicedRms=0.02` 时，聆听会一直判为有声，只能等录满 8 秒才提交，追问窗口也可能被误触发。审校确认 debugLog 下能看到判断依据（唤醒、转写日志齐全），且误触发的追问只会因不足 2 字被丢弃，不会把乱码当指令发出。Task 5 归属。
4. **打包版能否加载 sherpa**：原生 DLL 在 `x86_64` 子目录、其 `.meta` 对 Editor 和 Standalone Win64 启用；`StreamingAssets/SherpaOnnx/kws` 会随包发布。缺任何一个时，日志给出明确的「退回音量检测模式」警告，语音功能仍可用（一口气说完）。Task 3、Task 5 归属；手工验收第 13 条覆盖。
5. **ApplySettings 高频调用**（F9 切模式、设置页、测试按钮会反复调）：不能每次都重新合成应答音（`VoiceAckLines.Prepare` 有指纹判断），也不能每次都重启麦克风和 sherpa（`collectionKey` 指纹）。Task 5 归属。

---

### Task 1: Core 会话状态机 VoiceSessionMachine

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/VoiceSessionMachine.cs`
- Test: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/VoiceSessionMachineTests.cs`

**Interfaces:**
- Consumes: 无
- Produces（Task 5 使用）：
  - `enum VoiceSessionState { Idle, WakePending, Ack, Listening, Processing, AwaitReply, FollowUp }`
  - `enum VoiceAction { InterruptSpeech, HoldSpeech, ReleaseSpeech, DuckMusic, UnduckMusic, StartCapture, StartCaptureAfterAck, StopCapture, PlayAck, SubmitCapture, Inject, SayNotHeard }`
  - `struct VoiceSessionTiming`（字段 `PauseWindow, OneBreathVoiced, AckTimeout, EndSilence, MaxUtterance, ReplyTimeout, FollowUpWindow, FollowUpOnset, AckSafety, ProcessingSafety`，静态属性 `Default`）
  - `class VoiceSessionMachine`：构造 `(VoiceSessionTiming)`；属性 `State`、`PendingCommand`；方法 `OnWake(float t, bool talking, bool performing)`、`Update(float t, bool voiced, bool talking, bool performing)`、`OnAckFinished(float t)`、`OnTranscript(float t, string command)`、`Abort()`，全部返回 `IReadOnlyList<VoiceAction>`（内部复用的列表，调用方下次调用前用完或先拷贝）

- [ ] **Step 1: 开功能分支**

```bash
cd "E:/Work/AI/Mate-Engine" && git checkout -b feat/douyin-voice-v2 && git branch --show-current
```

Expected：`feat/douyin-voice-v2`

- [ ] **Step 2: 写失败测试**

创建 `Tests/VoiceSessionMachineTests.cs`：

```csharp
using NUnit.Framework;
using System.Collections.Generic;

namespace DouyinLive.Tests
{
    // 时间全部由测试驱动：Run 以 20ms 一帧推进，模拟 Unity 每帧调用 Update。
    public class VoiceSessionMachineTests
    {
        static VoiceSessionMachine M(float followUp = 6f)
        {
            var t = VoiceSessionTiming.Default;
            t.FollowUpWindow = followUp;
            return new VoiceSessionMachine(t);
        }

        static List<VoiceAction> L(IReadOnlyList<VoiceAction> a) => new List<VoiceAction>(a);

        static List<VoiceAction> Run(VoiceSessionMachine m, float from, float to, bool voiced,
                                     bool talking = false, bool performing = false)
        {
            var all = new List<VoiceAction>();
            int n = (int)System.Math.Round((to - from) / 0.02f);
            for (int i = 0; i <= n; i++) all.AddRange(m.Update(from + i * 0.02f, voiced, talking, performing));
            return all;
        }

        static int Count(List<VoiceAction> a, VoiceAction x)
        {
            int c = 0;
            foreach (var v in a) if (v == x) c++;
            return c;
        }

        // 唤醒(t=0) → 停顿 → 应答(t≈0.4) → 应答播完(t=1) → 聆听
        static VoiceSessionMachine ListeningAfterAck(bool performing = false)
        {
            var m = M();
            m.OnWake(0f, false, performing);
            Run(m, 0.02f, 0.5f, false, performing: performing);
            m.OnAckFinished(1f);
            return m;
        }

        // 聆听 → 说 1 秒 → 静音 1.2 秒 → 提交（t≈3.2）→ 处理中
        static VoiceSessionMachine Processing()
        {
            var m = ListeningAfterAck();
            Run(m, 1.02f, 2f, true);
            Run(m, 2.02f, 3.3f, false);
            return m;
        }

        // 转写成功(t=5) → 等回应 → 她开口(5.1) → 说完(7) → 追问
        static VoiceSessionMachine FollowUp()
        {
            var m = Processing();
            m.OnTranscript(5f, "唱一首黄昏");
            m.Update(5.1f, false, true, false);
            m.Update(7f, false, false, false);
            return m;
        }

        // 追问(7) → 连续开口进聆听(≈7.3) → 说到 7.5 → 静音 → 提交(≈8.7) → 处理中
        static VoiceSessionMachine ProcessingFromFollowUp()
        {
            var m = FollowUp();
            Run(m, 7.02f, 7.5f, true);
            Run(m, 7.52f, 8.8f, false);
            return m;
        }

        // ---------- 唤醒 ----------

        [Test]
        public void 空闲唤醒_暂停播报并开始录音()
        {
            var m = M();
            var a = L(m.OnWake(0f, false, false));
            CollectionAssert.AreEqual(new[] { VoiceAction.HoldSpeech, VoiceAction.StartCapture }, a);
            Assert.AreEqual(VoiceSessionState.WakePending, m.State);
        }

        [Test]
        public void 说话时唤醒_先打断她()
        {
            var a = L(M().OnWake(0f, true, false));
            Assert.AreEqual(VoiceAction.InterruptSpeech, a[0]);
        }

        [Test]
        public void 表演时唤醒_压低音乐()
        {
            Assert.Contains(VoiceAction.DuckMusic, L(M().OnWake(0f, false, true)));
        }

        // ---------- 判停顿 ----------

        [Test]
        public void 一口气说完_跳过应答直接聆听()
        {
            var m = M();
            m.OnWake(0f, false, false);
            var a = Run(m, 0.02f, 0.4f, true);
            Assert.AreEqual(VoiceSessionState.Listening, m.State);
            Assert.IsFalse(a.Contains(VoiceAction.PlayAck));
            Assert.IsFalse(a.Contains(VoiceAction.StopCapture));   // 录音不能中断，否则丢字
        }

        [Test]
        public void 只有唤醒词尾音_仍然应答()
        {
            // sherpa 在唤醒词快说完时才检出，尾音让窗口开头短暂有声，不能当成一口气说完
            var m = M();
            m.OnWake(0f, false, false);
            Run(m, 0.02f, 0.1f, true);            // 尾音 0.1 秒 < 0.15
            var a = Run(m, 0.12f, 0.5f, false);
            Assert.Contains(VoiceAction.PlayAck, a);
            Assert.AreEqual(VoiceSessionState.Ack, m.State);
        }

        [Test]
        public void 停顿_播应答且应答期间不录音()
        {
            var m = M();
            m.OnWake(0f, false, false);
            var a = Run(m, 0.02f, 0.5f, false);
            int stop = a.IndexOf(VoiceAction.StopCapture);
            int ack = a.IndexOf(VoiceAction.PlayAck);
            Assert.GreaterOrEqual(stop, 0);
            Assert.Greater(ack, stop);
        }

        // ---------- 应答 ----------

        [Test]
        public void 应答播完_开始无回看录音()
        {
            var m = M();
            m.OnWake(0f, false, false);
            Run(m, 0.02f, 0.5f, false);
            var a = L(m.OnAckFinished(1f));
            CollectionAssert.AreEqual(new[] { VoiceAction.StartCaptureAfterAck }, a);
            Assert.AreEqual(VoiceSessionState.Listening, m.State);
        }

        [Test]
        public void 应答回调丢失_安全超时后照常聆听()
        {
            var m = M();
            m.OnWake(0f, false, false);
            Run(m, 0.02f, 0.5f, false);
            var a = Run(m, 0.52f, 5.5f, false);
            Assert.Contains(VoiceAction.StartCaptureAfterAck, a);
            Assert.AreEqual(VoiceSessionState.Listening, m.State);
        }

        // ---------- 聆听 ----------

        [Test]
        public void 应答后不开口_安静回待机()
        {
            var m = ListeningAfterAck();
            var a = Run(m, 1.02f, 6.1f, false);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
            Assert.Contains(VoiceAction.StopCapture, a);
            Assert.Contains(VoiceAction.ReleaseSpeech, a);
            Assert.IsFalse(a.Contains(VoiceAction.SayNotHeard));
        }

        [Test]
        public void 说完静音够久_提交转写()
        {
            var m = ListeningAfterAck();
            Run(m, 1.02f, 2f, true);
            var before = Run(m, 2.02f, 3.1f, false);          // 静音 1.1 秒还不够
            Assert.IsFalse(before.Contains(VoiceAction.SubmitCapture));
            var a = Run(m, 3.12f, 3.3f, false);
            Assert.Contains(VoiceAction.SubmitCapture, a);
            Assert.AreEqual(VoiceSessionState.Processing, m.State);
        }

        [Test]
        public void 一直说话_录满上限提交()
        {
            var m = ListeningAfterAck();
            var a = Run(m, 1.02f, 9.1f, true);
            Assert.Contains(VoiceAction.SubmitCapture, a);
        }

        // ---------- 处理 ----------

        [Test]
        public void 转写成功_注入并放开播报()
        {
            var m = Processing();
            var a = L(m.OnTranscript(5f, " 唱一首黄昏 "));
            CollectionAssert.AreEqual(new[] { VoiceAction.Inject, VoiceAction.ReleaseSpeech }, a);
            Assert.AreEqual("唱一首黄昏", m.PendingCommand);
            Assert.AreEqual(VoiceSessionState.AwaitReply, m.State);
        }

        [Test]
        public void 转写失败_说没听清并回待机()
        {
            var m = Processing();
            var a = L(m.OnTranscript(5f, null));
            Assert.Contains(VoiceAction.SayNotHeard, a);
            Assert.Contains(VoiceAction.ReleaseSpeech, a);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        [Test]
        public void 唤醒后转写为空_说没听清()
        {
            var m = Processing();
            Assert.Contains(VoiceAction.SayNotHeard, L(m.OnTranscript(5f, "")));
        }

        // ---------- 等回应 ----------

        [Test]
        public void 她说完且没表演_进追问并暂停播报()
        {
            var m = Processing();
            m.OnTranscript(5f, "今天唱什么好");
            m.Update(5.1f, false, true, false);
            var a = L(m.Update(7f, false, false, false));
            CollectionAssert.AreEqual(new[] { VoiceAction.HoldSpeech }, a);
            Assert.AreEqual(VoiceSessionState.FollowUp, m.State);
        }

        [Test]
        public void 她开始表演_不开追问()
        {
            var m = Processing();
            m.OnTranscript(5f, "唱一首黄昏");
            m.Update(5.1f, false, true, false);
            m.Update(6f, false, false, true);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        [Test]
        public void 等回应超时_回待机()
        {
            var m = Processing();
            m.OnTranscript(5f, "你好");
            Run(m, 5.02f, 20.1f, false);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        [Test]
        public void 追问关闭_说完直接回待机()
        {
            var m = M(followUp: 0f);
            m.OnWake(0f, false, false);
            Run(m, 0.02f, 0.5f, false);
            m.OnAckFinished(1f);
            Run(m, 1.02f, 2f, true);
            Run(m, 2.02f, 3.3f, false);
            m.OnTranscript(5f, "你好");
            m.Update(5.1f, false, true, false);
            m.Update(7f, false, false, false);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        // ---------- 追问 ----------

        [Test]
        public void 追问内连续开口_进聆听()
        {
            var m = FollowUp();
            var a = Run(m, 7.02f, 7.4f, true);
            Assert.Contains(VoiceAction.StartCapture, a);
            Assert.AreEqual(VoiceSessionState.Listening, m.State);
        }

        [Test]
        public void 追问内短促声响_不进聆听()
        {
            var m = FollowUp();
            Run(m, 7.02f, 7.2f, true);             // 0.2 秒 < 0.3（咳嗽、键盘声）
            var a = Run(m, 7.22f, 7.6f, false);
            Assert.IsFalse(a.Contains(VoiceAction.StartCapture));
            Assert.AreEqual(VoiceSessionState.FollowUp, m.State);
        }

        [Test]
        public void 追问超时_回待机并放开播报()
        {
            var m = FollowUp();
            var a = Run(m, 7.02f, 13.1f, false);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
            Assert.Contains(VoiceAction.ReleaseSpeech, a);
        }

        [Test]
        public void 追问转写太短_静默丢弃()
        {
            var m = ProcessingFromFollowUp();
            Assert.AreEqual(VoiceSessionState.Processing, m.State);
            var a = L(m.OnTranscript(9f, "嗯"));
            Assert.IsFalse(a.Contains(VoiceAction.SayNotHeard));
            Assert.IsFalse(a.Contains(VoiceAction.Inject));
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        [Test]
        public void 追问转写失败_静默丢弃()
        {
            var m = ProcessingFromFollowUp();
            Assert.IsFalse(L(m.OnTranscript(9f, null)).Contains(VoiceAction.SayNotHeard));
        }

        [Test]
        public void 追问转写正常_照常注入()
        {
            var m = ProcessingFromFollowUp();
            Assert.Contains(VoiceAction.Inject, L(m.OnTranscript(9f, "那唱晴天吧")));
        }

        // ---------- 重复唤醒 / 中止 ----------

        [Test]
        public void 录音阶段再次唤醒_忽略()
        {
            var m = ListeningAfterAck();
            Assert.AreEqual(0, m.OnWake(1.5f, false, false).Count);
            Assert.AreEqual(VoiceSessionState.Listening, m.State);
        }

        [Test]
        public void 追问阶段唤醒_开新会话()
        {
            var m = FollowUp();
            var a = L(m.OnWake(8f, false, false));
            Assert.Contains(VoiceAction.StartCapture, a);
            Assert.IsFalse(a.Contains(VoiceAction.HoldSpeech));   // 追问时已暂停，不重复发
            Assert.AreEqual(VoiceSessionState.WakePending, m.State);
        }

        [Test]
        public void 音乐恢复恰好一次()
        {
            var m = ListeningAfterAck(performing: true);
            var a = Run(m, 1.02f, 6.1f, false, performing: true);
            a.AddRange(Run(m, 6.12f, 7f, false, performing: true));
            Assert.AreEqual(1, Count(a, VoiceAction.UnduckMusic));
        }

        [Test]
        public void 中止_恢复音乐与播报()
        {
            var m = M();
            m.OnWake(0f, false, true);
            var a = L(m.Abort());
            Assert.Contains(VoiceAction.StopCapture, a);
            Assert.Contains(VoiceAction.UnduckMusic, a);
            Assert.Contains(VoiceAction.ReleaseSpeech, a);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        [Test]
        public void 过期转写_忽略()
        {
            var m = M();
            Assert.AreEqual(0, m.OnTranscript(1f, "你好").Count);
            Assert.AreEqual(VoiceSessionState.Idle, m.State);
        }

        [Test]
        public void 待机中止_无动作()
        {
            Assert.AreEqual(0, M().Abort().Count);
        }
    }
}
```

- [ ] **Step 3: 跑测试确认失败（RED）**

按「验证命令」跑 EditMode 测试，`-testFilter "DouyinLive.Tests.VoiceSessionMachineTests"`。
Expected：`rc≠0`、无结果 XML；Editor.log 出现 `error CS0246`（找不到 `VoiceSessionMachine` / `VoiceSessionTiming` / `VoiceAction` / `VoiceSessionState`）。

- [ ] **Step 4: 写实现**

创建 `Core/VoiceSessionMachine.cs`：

```csharp
using System;
using System.Collections.Generic;

namespace DouyinLive
{
    public enum VoiceSessionState { Idle, WakePending, Ack, Listening, Processing, AwaitReply, FollowUp }

    // 状态机交给调用方执行的动作，按列表顺序执行。
    public enum VoiceAction
    {
        InterruptSpeech,       // 打断她当前这句（队列保留）
        HoldSpeech,            // 暂停播报队列出队（主播说话时她不能开口，否则声音混进录音）
        ReleaseSpeech,         // 恢复播报队列
        DuckMusic,             // 压低歌声
        UnduckMusic,           // 恢复歌声
        StartCapture,          // 开始录音，带短回看（唤醒、追问起音时盖住起音）
        StartCaptureAfterAck,  // 开始录音，零回看（回看会录进她刚说完的应答）
        StopCapture,           // 丢弃正在录的音频
        PlayAck,               // 播应答；播完由调用方回调 OnAckFinished
        SubmitCapture,         // 取出录音送 ASR（取出即停止录音）；转写完成回调 OnTranscript
        Inject,                // 把 PendingCommand 交给直播管线
        SayNotHeard            // 说「没听清～」
    }

    public struct VoiceSessionTiming
    {
        public float PauseWindow;       // 唤醒后观察多久，决定「停顿等应答」还是「一口气说完」
        public float OneBreathVoiced;   // 观察窗内累计有声多久算一口气说完（唤醒词尾音短于它）
        public float AckTimeout;        // 应答后多久没开口就放弃
        public float EndSilence;        // 开口后静音多久算说完
        public float MaxUtterance;      // 一句话最长秒数
        public float ReplyTimeout;      // 等她开口回应最多多久
        public float FollowUpWindow;    // 追问窗口，0=关闭
        public float FollowUpOnset;     // 追问时连续有声多久算开口（防咳嗽、键盘声）
        public float AckSafety;         // 应答播完回调丢失时的兜底
        public float ProcessingSafety;  // 转写回调丢失时的兜底（ASR 客户端超时 15 秒）

        public static VoiceSessionTiming Default => new VoiceSessionTiming
        {
            PauseWindow = 0.4f, OneBreathVoiced = 0.15f, AckTimeout = 5f, EndSilence = 1.2f,
            MaxUtterance = 8f, ReplyTimeout = 15f, FollowUpWindow = 6f, FollowUpOnset = 0.3f,
            AckSafety = 5f, ProcessingSafety = 20f
        };
    }

    // 语音会话状态机（手机助手式）：待机 → 判停顿 → 应答 → 聆听 → 处理 → 等回应 → 追问 → 待机。
    // 纯逻辑：时间和音频状态都由调用方喂入，返回要执行的动作；不碰麦克风、不碰 Unity，
    // 所以每条时序规则都能用模拟时钟单测。返回的列表是内部复用的，调用方下次调用前用完或先拷贝。
    public sealed class VoiceSessionMachine
    {
        readonly VoiceSessionTiming tm;
        readonly List<VoiceAction> output = new List<VoiceAction>();

        public VoiceSessionState State { get; private set; } = VoiceSessionState.Idle;
        public string PendingCommand { get; private set; }

        // 副作用账本：进待机时据此恢复，保证压低/暂停各只恢复一次
        bool ducked, held, capturing;
        bool heard, fromFollowUp, sawTalking;
        float enteredAt, lastVoicedAt, voicedAccum, onsetAccum;
        float lastT;
        bool hasLastT;

        public VoiceSessionMachine(VoiceSessionTiming timing) { tm = timing; }

        public IReadOnlyList<VoiceAction> OnWake(float t, bool talking, bool performing)
        {
            output.Clear();
            switch (State)
            {
                case VoiceSessionState.WakePending:
                case VoiceSessionState.Ack:
                case VoiceSessionState.Listening:
                case VoiceSessionState.Processing:
                    return output;   // 正在听/正在处理：重复的唤醒词不重置会话
            }
            if (talking) output.Add(VoiceAction.InterruptSpeech);
            if (performing && !ducked) { output.Add(VoiceAction.DuckMusic); ducked = true; }
            if (!held) { output.Add(VoiceAction.HoldSpeech); held = true; }
            output.Add(VoiceAction.StartCapture);
            capturing = true;
            voicedAccum = 0f;
            fromFollowUp = false;
            PendingCommand = null;
            Enter(VoiceSessionState.WakePending, t);
            return output;
        }

        // 每帧调用。voiced = 这一帧的麦克风音频是否有声；talking = 她正在出声；performing = 在唱歌/跳舞。
        public IReadOnlyList<VoiceAction> Update(float t, bool voiced, bool talking, bool performing)
        {
            output.Clear();
            float dt = hasLastT ? Math.Max(0f, t - lastT) : 0f;
            lastT = t;
            hasLastT = true;

            switch (State)
            {
                case VoiceSessionState.WakePending:
                    if (voiced) voicedAccum += dt;
                    if (voicedAccum >= tm.OneBreathVoiced)
                        EnterListening(t, true, false);          // 一口气说完：录音不断，直接聆听
                    else if (t - enteredAt >= tm.PauseWindow)
                    {
                        output.Add(VoiceAction.StopCapture);       // 应答期间不录，免得录进她的声音
                        capturing = false;
                        output.Add(VoiceAction.PlayAck);
                        Enter(VoiceSessionState.Ack, t);
                    }
                    break;

                case VoiceSessionState.Ack:
                    if (t - enteredAt >= tm.AckSafety) AckDone(t);
                    break;

                case VoiceSessionState.Listening:
                    if (voiced) { heard = true; lastVoicedAt = t; }
                    if (heard && t - lastVoicedAt >= tm.EndSilence) Submit(t);
                    else if (t - enteredAt >= tm.MaxUtterance) { if (heard) Submit(t); else GoIdle(); }
                    else if (!heard && t - enteredAt >= tm.AckTimeout) GoIdle();
                    break;

                case VoiceSessionState.Processing:
                    if (t - enteredAt >= tm.ProcessingSafety) GoIdle();
                    break;

                case VoiceSessionState.AwaitReply:
                    if (performing) GoIdle();                       // 表演完多半在干别的，不开追问
                    else if (talking) sawTalking = true;
                    else if (sawTalking)
                    {
                        if (tm.FollowUpWindow > 0f) EnterFollowUp(t);
                        else GoIdle();
                    }
                    else if (t - enteredAt >= tm.ReplyTimeout) GoIdle();
                    break;

                case VoiceSessionState.FollowUp:
                    if (performing) { GoIdle(); break; }
                    if (voiced) onsetAccum += dt; else onsetAccum = 0f;
                    if (onsetAccum >= tm.FollowUpOnset)
                    {
                        output.Add(VoiceAction.StartCapture);
                        capturing = true;
                        EnterListening(t, true, true);
                    }
                    else if (t - enteredAt >= tm.FollowUpWindow) GoIdle();
                    break;
            }
            return output;
        }

        public IReadOnlyList<VoiceAction> OnAckFinished(float t)
        {
            output.Clear();
            if (State == VoiceSessionState.Ack) AckDone(t);
            return output;
        }

        // command：剥完唤醒词的指令文字；null = ASR 失败
        public IReadOnlyList<VoiceAction> OnTranscript(float t, string command)
        {
            output.Clear();
            if (State != VoiceSessionState.Processing) return output;   // 中止后才回来的转写
            string c = command?.Trim();
            if (fromFollowUp)
            {
                // 追问没有唤醒词把关：噪音转写出的空串或单字不能当成一句话发给她
                if (string.IsNullOrEmpty(c) || c.Length < 2) { GoIdle(); return output; }
            }
            else if (string.IsNullOrEmpty(c))
            {
                GoIdle();
                output.Add(VoiceAction.SayNotHeard);   // 让主播知道要重说，而不是以为没唤醒
                return output;
            }
            PendingCommand = c;
            output.Add(VoiceAction.Inject);
            if (held) { output.Add(VoiceAction.ReleaseSpeech); held = false; }   // 放开让她回答
            sawTalking = false;
            Enter(VoiceSessionState.AwaitReply, t);
            return output;
        }

        // 停播、菜单打开、监听器停用：丢弃会话并恢复一切副作用
        public IReadOnlyList<VoiceAction> Abort()
        {
            output.Clear();
            GoIdle();
            PendingCommand = null;
            return output;
        }

        void AckDone(float t)
        {
            output.Add(VoiceAction.StartCaptureAfterAck);
            capturing = true;
            EnterListening(t, false, false);
        }

        void Submit(float t)
        {
            output.Add(VoiceAction.SubmitCapture);   // 取出即停止录音，不再单独发 StopCapture
            capturing = false;
            Enter(VoiceSessionState.Processing, t);
        }

        void EnterListening(float t, bool alreadyHeard, bool followUp)
        {
            heard = alreadyHeard;
            lastVoicedAt = t;
            fromFollowUp = followUp;
            Enter(VoiceSessionState.Listening, t);
        }

        void EnterFollowUp(float t)
        {
            if (!held) { output.Add(VoiceAction.HoldSpeech); held = true; }
            onsetAccum = 0f;
            Enter(VoiceSessionState.FollowUp, t);
        }

        void GoIdle()
        {
            if (capturing) { output.Add(VoiceAction.StopCapture); capturing = false; }
            if (ducked) { output.Add(VoiceAction.UnduckMusic); ducked = false; }
            if (held) { output.Add(VoiceAction.ReleaseSpeech); held = false; }
            State = VoiceSessionState.Idle;
        }

        void Enter(VoiceSessionState s, float t)
        {
            State = s;
            enteredAt = t;
            lastT = t;
            hasLastT = true;
        }
    }
}
```

- [ ] **Step 5: 跑测试确认通过（GREEN）**

同 Step 3 的命令。Expected：`result="Passed" total="30" passed="30" failed="0"`。

- [ ] **Step 6: 全量回归**

去掉 `-testFilter`。Expected：`total="206" passed="206" failed="0"`（原 176 + 30）。

- [ ] **Step 7: 提交**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/VoiceSessionMachine.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/VoiceSessionMachine.cs.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/VoiceSessionMachineTests.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/VoiceSessionMachineTests.cs.meta"
git commit -m "feat(douyin-live): 语音会话状态机 VoiceSessionMachine——应答/聆听/追问/打断/压低音乐

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Core 唤醒词校验 WakeKeywordSpec + 录音缓冲按需回看

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/WakeKeywordSpec.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/VoiceCaptureBuffer.cs`
- Test: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/WakeKeywordSpecTests.cs`
- Test: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/VoiceCaptureBufferTests.cs`（追加）

**Interfaces:**
- Consumes: 无
- Produces（Task 3、Task 5 使用）：
  - `class WakeKeywordResult { string KeywordsText; int ValidCount; List<string> Errors; }`
  - `static class WakeKeywordSpec`：`const string DefaultKeyword = "x iǎo m ěi x iǎo m ěi @小美小美"`；`HashSet<string> ParseVocabulary(string tokensText)`；`WakeKeywordResult Build(IReadOnlyList<string> lines, ICollection<string> vocabulary)`
  - `VoiceCaptureBuffer.NotifyWake(float preRollSeconds)`、`VoiceCaptureBuffer.TakeArmed(out float[] samples)`（返回 bool）；原 `NotifyWake()` / `TryTakeUtterance` 行为不变

- [ ] **Step 1: 写失败测试**

创建 `Tests/WakeKeywordSpecTests.cs`：

```csharp
using NUnit.Framework;
using System.Collections.Generic;

namespace DouyinLive.Tests
{
    public class WakeKeywordSpecTests
    {
        static readonly HashSet<string> Vocab = new HashSet<string> { "x", "iǎo", "m", "ěi" };
        const string Good = "x iǎo m ěi x iǎo m ěi @小美小美";

        [Test]
        public void 合法唤醒词_通过并原样输出()
        {
            var r = WakeKeywordSpec.Build(new[] { Good }, Vocab);
            Assert.AreEqual(1, r.ValidCount);
            Assert.AreEqual(0, r.Errors.Count);
            Assert.AreEqual(Good + "\n", r.KeywordsText);
        }

        [Test]
        public void 音节不在词表_报出该音节()
        {
            // 最常见的错：漏写声调
            var r = WakeKeywordSpec.Build(new[] { "x iao m ěi @小美" }, Vocab);
            Assert.AreEqual(0, r.ValidCount);
            StringAssert.Contains("iao", r.Errors[0]);
        }

        [Test]
        public void 缺少显示名_报错()
        {
            var r = WakeKeywordSpec.Build(new[] { "x iǎo m ěi" }, Vocab);
            Assert.AreEqual(0, r.ValidCount);
            StringAssert.Contains("@", r.Errors[0]);
        }

        [Test]
        public void 显示名不在最后_报错()
        {
            var r = WakeKeywordSpec.Build(new[] { "x iǎo @小美 m ěi" }, Vocab);
            Assert.AreEqual(0, r.ValidCount);
        }

        [Test]
        public void 空项_报错并跳过()
        {
            var r = WakeKeywordSpec.Build(new[] { "", null, Good }, Vocab);
            Assert.AreEqual(1, r.ValidCount);
            Assert.AreEqual(2, r.Errors.Count);
        }

        [Test]
        public void 有效无效混合_只输出有效()
        {
            var r = WakeKeywordSpec.Build(new[] { Good, "x iao @错" }, Vocab);
            Assert.AreEqual(Good + "\n", r.KeywordsText);
            Assert.AreEqual(1, r.Errors.Count);
        }

        [Test]
        public void 灵敏度后缀合法_通过()
        {
            var r = WakeKeywordSpec.Build(new[] { "x iǎo m ěi x iǎo m ěi :1.5 #0.2 @小美小美" }, Vocab);
            Assert.AreEqual(1, r.ValidCount);
        }

        [Test]
        public void 灵敏度后缀非数字_报错()
        {
            var r = WakeKeywordSpec.Build(new[] { "x iǎo m ěi :abc @小美" }, Vocab);
            Assert.AreEqual(0, r.ValidCount);
            StringAssert.Contains(":abc", r.Errors[0]);
        }

        [Test]
        public void 列表为空引用_报错()
        {
            var r = WakeKeywordSpec.Build(null, Vocab);
            Assert.AreEqual(0, r.ValidCount);
            Assert.AreEqual(1, r.Errors.Count);
        }

        [Test]
        public void 词表解析_取每行第一列()
        {
            var v = WakeKeywordSpec.ParseVocabulary("<blk> 0\nx 7\r\niǎo 120\n\n");
            Assert.AreEqual(3, v.Count);
            Assert.IsTrue(v.Contains("<blk>"));
            Assert.IsTrue(v.Contains("x"));
            Assert.IsTrue(v.Contains("iǎo"));
        }
    }
}
```

在 `Tests/VoiceCaptureBufferTests.cs` 类末尾（最后一个测试方法之后、类的右花括号之前）追加：

```csharp
        // ---------- v2：按需回看 + 无条件取出 ----------

        [Test]
        public void 指定回看秒数_只回看指定长度()
        {
            var b = new VoiceCaptureBuffer(1000, 3f, 1f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 2000));
            b.NotifyWake(0.5f);
            b.Push(Tone(0.2f, 300));
            Assert.IsTrue(b.TakeArmed(out var s));
            Assert.AreEqual(800, s.Length);   // 500 回看 + 300 唤醒后
        }

        [Test]
        public void 零回看_只含唤醒后()
        {
            var b = new VoiceCaptureBuffer(1000, 3f, 1f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 2000));
            b.NotifyWake(0f);
            b.Push(Tone(0.2f, 300));
            Assert.IsTrue(b.TakeArmed(out var s));
            Assert.AreEqual(300, s.Length);
        }

        [Test]
        public void TakeArmed不等静音判定()
        {
            // v2 的「说完没有」由状态机决定，缓冲区只负责交出音频
            var b = new VoiceCaptureBuffer(1000, 3f, 1f, 0.2f, 0.05f);
            b.NotifyWake(0f);
            b.Push(Tone(0.2f, 100));
            Assert.IsFalse(b.UtteranceReady);
            Assert.IsTrue(b.TakeArmed(out var s));
            Assert.AreEqual(100, s.Length);
        }

        [Test]
        public void 未唤醒TakeArmed失败()
        {
            var b = new VoiceCaptureBuffer(1000, 3f, 1f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 100));
            Assert.IsFalse(b.TakeArmed(out var s));
            Assert.IsNull(s);
        }

        [Test]
        public void TakeArmed后复位_旧接口回看不受影响()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.NotifyWake(0f);
            b.Push(Tone(0.2f, 100));
            b.TakeArmed(out _);
            Assert.IsFalse(b.Armed);
            b.Push(Tone(0.2f, 500));
            b.NotifyWake();                    // 旧接口：回看 PreWakeSeconds=0.1s
            b.Push(Tone(0.2f, 300));
            b.Push(new float[250]);
            Assert.IsTrue(b.TryTakeUtterance(out var s));
            Assert.AreEqual(650, s.Length);    // 100 pre + 300 + 250
        }
```

- [ ] **Step 2: 跑测试确认失败（RED）**

EditMode 测试，`-testFilter "DouyinLive.Tests.WakeKeywordSpecTests|DouyinLive.Tests.VoiceCaptureBufferTests"`。
Expected：`rc≠0`、无结果 XML；Editor.log 有 `error CS0103`（`WakeKeywordSpec` 不存在）和 `error CS1061`（`TakeArmed` 不存在）/ `error CS1501`（`NotifyWake` 无 1 参数重载）。

- [ ] **Step 3: 写 WakeKeywordSpec**

创建 `Core/WakeKeywordSpec.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DouyinLive
{
    public sealed class WakeKeywordResult
    {
        public string KeywordsText = "";   // 有效唤醒词，每行一个，直接喂给 sherpa
        public int ValidCount;
        public readonly List<string> Errors = new List<string>();
    }

    // sherpa 唤醒词校验：配置写的是带声调拼音（声母韵母分开），写错一个音节 sherpa 不报错、
    // 只是永远检不出——主播会以为引擎坏了。启动时逐个音节对照模型词表，把错处说清楚。
    // 格式：「x iǎo m ěi x iǎo m ěi :1.5 #0.2 @小美小美」，:boost / #阈值 可选，@显示名必填且在最后。
    public static class WakeKeywordSpec
    {
        public const string DefaultKeyword = "x iǎo m ěi x iǎo m ěi @小美小美";

        // tokens.txt：每行「音节 编号」
        public static HashSet<string> ParseVocabulary(string tokensText)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(tokensText)) return set;
            foreach (var raw in tokensText.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int sp = line.LastIndexOf(' ');
                set.Add(sp > 0 ? line.Substring(0, sp).Trim() : line);
            }
            return set;
        }

        public static WakeKeywordResult Build(IReadOnlyList<string> lines, ICollection<string> vocabulary)
        {
            var r = new WakeKeywordResult();
            if (lines == null) { r.Errors.Add("没有配置唤醒词"); return r; }
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i]?.Trim();
                if (string.IsNullOrEmpty(line)) { r.Errors.Add($"第 {i + 1} 个唤醒词为空"); continue; }
                if (TryValidate(line, vocabulary, out string err))
                {
                    sb.Append(line).Append('\n');
                    r.ValidCount++;
                }
                else r.Errors.Add($"唤醒词「{line}」：{err}");
            }
            r.KeywordsText = sb.ToString();
            return r;
        }

        static bool TryValidate(string line, ICollection<string> vocab, out string err)
        {
            err = null;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int syllables = 0;
            string display = null;
            foreach (var p in parts)
            {
                if (p[0] == '@')
                {
                    if (display != null) { err = "只能有一个 @显示名"; return false; }
                    display = p.Substring(1);
                    continue;
                }
                if (display != null) { err = "@显示名必须写在最后"; return false; }
                if (p[0] == ':' || p[0] == '#')
                {
                    if (!float.TryParse(p.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    { err = $"「{p}」不是有效的灵敏度数值"; return false; }
                    continue;
                }
                if (vocab == null || !vocab.Contains(p))
                { err = $"音节「{p}」不在模型词表里（检查声调，声母韵母要分开写，如 x iǎo）"; return false; }
                syllables++;
            }
            if (syllables == 0) { err = "没有拼音音节"; return false; }
            if (string.IsNullOrEmpty(display)) { err = "缺少 @显示名（例：@小美小美）"; return false; }
            return true;
        }
    }
}
```

- [ ] **Step 4: 改 VoiceCaptureBuffer**

在 `Core/VoiceCaptureBuffer.cs`：

① 字段区（`long silenceSamples;` 之后）加：

```csharp
        float armedPreRoll;    // 本次唤醒的回看秒数（v2 按场合给：唤醒 0.6s、应答后 0s）
```

② 把原 `NotifyWake()` 整个方法替换为：

```csharp
        public void NotifyWake() { NotifyWake(PreWakeSeconds); }

        // v2：sherpa 给出的唤醒时刻精确，回看只需盖住起音；应答刚播完时回看会录进她的声音，要给 0
        public void NotifyWake(float preRollSeconds)
        {
            if (armed) return;   // 已唤醒忽略（关键词连发）
            armed = true;
            wakeSample = totalWritten;
            silenceSamples = 0;
            armedPreRoll = Math.Max(0f, preRollSeconds);
        }
```

③ 把原 `TryTakeUtterance` 整个方法替换为（切片逻辑抽成 `SliceArmed`，回看改用 `armedPreRoll`；旧调用路径经 `NotifyWake()` 设的就是 `PreWakeSeconds`，行为不变）：

```csharp
        // 返回 [唤醒前回看 + 唤醒后全部]，取完复位回待唤醒。缓冲里的旧音频
        // 保留继续滚（下一次唤醒的回看还能用它）。
        public bool TryTakeUtterance(out float[] samples)
        {
            samples = null;
            if (!armed || !UtteranceReady) return false;
            samples = SliceArmed();
            Reset();
            return true;
        }

        // v2：「说完没有」由会话状态机判定，这里不看静音，已唤醒就交出
        public bool TakeArmed(out float[] samples)
        {
            samples = null;
            if (!armed) return false;
            samples = SliceArmed();
            Reset();
            return true;
        }

        float[] SliceArmed()
        {
            long start = wakeSample - (long)(armedPreRoll * SampleRate);
            if (start < totalWritten - ring.Length) start = totalWritten - ring.Length;
            if (start < 0) start = 0;

            int count = (int)(totalWritten - start);
            var samples = new float[count];
            for (int i = 0; i < count; i++)
                samples[i] = ring[(int)((start + i) % ring.Length)];
            return samples;
        }
```

- [ ] **Step 5: 跑测试确认通过（GREEN）**

同 Step 2 的命令。Expected：`result="Passed"`，两个类合计 `total="25"`（WakeKeywordSpec 10 + VoiceCaptureBuffer 原 10 + 新 5）、`failed="0"`。

- [ ] **Step 6: 全量回归**

Expected：`total="221" passed="221" failed="0"`（206 + 15）。

- [ ] **Step 7: 提交**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/WakeKeywordSpec.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/WakeKeywordSpec.cs.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/VoiceCaptureBuffer.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/WakeKeywordSpecTests.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/WakeKeywordSpecTests.cs.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/VoiceCaptureBufferTests.cs"
git commit -m "feat(douyin-live): 拼音唤醒词校验 WakeKeywordSpec；录音缓冲支持按需回看与无条件取出

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: 引入 sherpa-onnx + SherpaWakeDetector

**Files:**
- Create: `Assets/Plugins/SherpaOnnx/sherpa-onnx.dll`、`Assets/Plugins/SherpaOnnx/x86_64/onnxruntime.dll`、`Assets/Plugins/SherpaOnnx/x86_64/sherpa-onnx-c-api.dll`（及 Unity 生成的 .meta）
- Create: `Assets/StreamingAssets/SherpaOnnx/kws/{encoder,decoder,joiner}-epoch-12-avg-2-chunk-16-left-64.onnx`、`tokens.txt`（及 .meta）
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Sherpa/MateEngine.DouyinLive.Sherpa.asmdef`
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Sherpa/SherpaWakeDetector.cs`
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/TestData/kws_official_3.bytes`、`kws_official_keywords.txt`（及 .meta）
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/MateEngine.DouyinLive.Tests.asmdef`
- Test: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/SherpaWakeDetectorTests.cs`

**Interfaces:**
- Consumes: `WakeKeywordSpec.ParseVocabulary`、`WakeKeywordSpec.Build`、`WakeKeywordSpec.DefaultKeyword`（Task 2）
- Produces（Task 5 使用）：`sealed class SherpaWakeDetector`（命名空间 `DouyinLive`，程序集 `MateEngine.DouyinLive.Sherpa`）：
  - `static SherpaWakeDetector Create(string modelDir, IReadOnlyList<string> keywordLines, string scratchDir)`（永不抛异常；失败时 `IsAvailable=false`）
  - `bool IsAvailable`、`string Error`、`IReadOnlyList<string> KeywordErrors`、`string LastKeyword`
  - `bool Feed(float[] samples, int count, int sampleRate)`（本块检出唤醒词返回 true）
  - `void Dispose()`

- [ ] **Step 1: 放 DLL 和模型**

下载包在 spike 时已存于 `%TEMP%/sherpa-dl`；不在就重新下载（国内渠道，GitHub 直连太慢不可用）：

```bash
D="C:/Users/83529/AppData/Local/Temp/sherpa-dl"
M="$D/model/sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01"
if [ ! -f "$D/managed/lib/netstandard2.0/sherpa-onnx.dll" ]; then
  mkdir -p "$D/managed" "$D/native"
  curl -sS -L -o "$D/managed.nupkg" "https://nuget.azure.cn/v3-flatcontainer/org.k2fsa.sherpa.onnx/1.13.8/org.k2fsa.sherpa.onnx.1.13.8.nupkg"
  curl -sS -L -o "$D/native.nupkg" "https://nuget.azure.cn/v3-flatcontainer/org.k2fsa.sherpa.onnx.runtime.win-x64/1.13.8/org.k2fsa.sherpa.onnx.runtime.win-x64.1.13.8.nupkg"
  (cd "$D/managed" && unzip -q -o ../managed.nupkg) && (cd "$D/native" && unzip -q -o ../native.nupkg)
fi
R="pkufool/sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01"
mkdir -p "$M" "$D/official"
for f in tokens.txt encoder-epoch-12-avg-2-chunk-16-left-64.onnx decoder-epoch-12-avg-2-chunk-16-left-64.onnx joiner-epoch-12-avg-2-chunk-16-left-64.onnx; do
  [ -f "$M/$f" ] || curl -sS -L -o "$M/$f" "https://www.modelscope.cn/models/$R/resolve/master/$f"
done
for f in 3.wav test_keywords.txt; do
  [ -f "$D/official/$f" ] || curl -sS -L -o "$D/official/$f" "https://www.modelscope.cn/models/$R/resolve/master/test_wavs/$f"
done
P="E:/Work/AI/Mate-Engine/Assets/Plugins/SherpaOnnx"
S="E:/Work/AI/Mate-Engine/Assets/StreamingAssets/SherpaOnnx/kws"
T="E:/Work/AI/Mate-Engine/Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/TestData"
mkdir -p "$P/x86_64" "$S" "$T"
cp "$D/managed/lib/netstandard2.0/sherpa-onnx.dll" "$P/"
cp "$D/native/runtimes/win-x64/native/onnxruntime.dll" "$D/native/runtimes/win-x64/native/sherpa-onnx-c-api.dll" "$P/x86_64/"
cp "$M/tokens.txt" "$M/encoder-epoch-12-avg-2-chunk-16-left-64.onnx" "$M/decoder-epoch-12-avg-2-chunk-16-left-64.onnx" "$M/joiner-epoch-12-avg-2-chunk-16-left-64.onnx" "$S/"
cp "$D/official/3.wav" "$T/kws_official_3.bytes"
cp "$D/official/test_keywords.txt" "$T/kws_official_keywords.txt"
ls -la "$P" "$P/x86_64" "$S" "$T" | grep -v "^total"
```

Expected：`sherpa-onnx.dll`（约 51KB）、`onnxruntime.dll`（约 17.8MB）、`sherpa-onnx-c-api.dll`（约 4.6MB）、encoder 约 12.2MB、decoder 约 0.7MB、joiner 约 0.25MB、`tokens.txt` 1627 字节、`kws_official_3.bytes` 257006 字节。

测试数据用 `.bytes` 扩展名：`.wav` 会被 Unity 当音频资源导入，没必要。

- [ ] **Step 2: 建 Sherpa 程序集**

创建 `Sherpa/MateEngine.DouyinLive.Sherpa.asmdef`：

```json
{
    "name": "MateEngine.DouyinLive.Sherpa",
    "rootNamespace": "DouyinLive",
    "references": ["MateEngine.DouyinLive.Core"],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": ["sherpa-onnx.dll"],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": true
}
```

修改 `Tests/MateEngine.DouyinLive.Tests.asmdef`：`references` 数组加 `"MateEngine.DouyinLive.Sherpa"`，`precompiledReferences` 改为 `["nunit.framework.dll", "sherpa-onnx.dll"]`。改完的完整内容：

```json
{
    "name": "MateEngine.DouyinLive.Tests",
    "rootNamespace": "DouyinLive.Tests",
    "references": [
        "MateEngine.DouyinLive.Core",
        "MateEngine.DouyinLive.Sherpa",
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner"
    ],
    "includePlatforms": ["Editor"],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": ["nunit.framework.dll", "sherpa-onnx.dll"],
    "autoReferenced": false,
    "defineConstraints": ["UNITY_INCLUDE_TESTS"],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [ ] **Step 3: 写失败测试**

创建 `Tests/SherpaWakeDetectorTests.cs`：

```csharp
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DouyinLive.Tests
{
    // 用 sherpa 官方测试音频 + 官方唤醒词验证整条链路：原生库加载、逐块喂音频、检出、Reset 后继续检出。
    // 合成语音（硅基流动 CosyVoice）连 SenseVoice 都转不出来，不能当测试数据。
    public class SherpaWakeDetectorTests
    {
        static string ModelDir => Path.Combine(Application.streamingAssetsPath, "SherpaOnnx", "kws");
        static string Scratch => Path.Combine(Application.temporaryCachePath, "sherpa-test");
        static string TestData => Path.Combine(Application.dataPath,
            "MATE ENGINE - Scripts", "Game APIs", "DouyinLive", "Tests", "TestData");

        static float[] ReadWav(string path)
        {
            var b = File.ReadAllBytes(path);
            int p = 12;
            while (p + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, p, 4);
                int size = BitConverter.ToInt32(b, p + 4);
                if (id == "data")
                {
                    int remain = b.Length - p - 8;
                    int bytes = (size <= 0 || size > remain) ? remain : size;
                    var s = new float[bytes / 2];
                    for (int i = 0; i < s.Length; i++) s[i] = BitConverter.ToInt16(b, p + 8 + i * 2) / 32768f;
                    return s;
                }
                p += 8 + size + (size & 1);
            }
            throw new Exception("no data chunk: " + path);
        }

        // 按 100ms 一块喂，模拟麦克风流
        static void FeedAll(SherpaWakeDetector d, float[] wav, List<string> found)
        {
            for (int off = 0; off < wav.Length; off += 1600)
            {
                int n = Math.Min(1600, wav.Length - off);
                var part = new float[n];
                Array.Copy(wav, off, part, 0, n);
                if (d.Feed(part, n, 16000)) found.Add(d.LastKeyword);
            }
        }

        [Test]
        public void 官方音频_检出官方唤醒词()
        {
            var lines = File.ReadAllLines(Path.Combine(TestData, "kws_official_keywords.txt"));
            var d = SherpaWakeDetector.Create(ModelDir, lines, Scratch);
            try
            {
                Assert.IsTrue(d.IsAvailable, d.Error);
                var found = new List<string>();
                FeedAll(d, ReadWav(Path.Combine(TestData, "kws_official_3.bytes")), found);
                FeedAll(d, new float[16000], found);   // 1 秒静音把最后一个词冲出来
                CollectionAssert.Contains(found, "文森特卡索");
                CollectionAssert.Contains(found, "法国");   // 第二次检出：Reset 后能继续检出
            }
            finally { d.Dispose(); }
        }

        [Test]
        public void 缺少模型文件_不可用并说明原因()
        {
            var d = SherpaWakeDetector.Create(Path.Combine(Scratch, "no-model"),
                new[] { WakeKeywordSpec.DefaultKeyword }, Scratch);
            Assert.IsFalse(d.IsAvailable);
            StringAssert.Contains("缺少模型文件", d.Error);
        }

        [Test]
        public void 唤醒词全部无效_不可用()
        {
            var d = SherpaWakeDetector.Create(ModelDir, new[] { "abc @错" }, Scratch);
            Assert.IsFalse(d.IsAvailable);
            Assert.AreEqual(1, d.KeywordErrors.Count);
        }

        [Test]
        public void 部分无效_仍可用并报告()
        {
            var d = SherpaWakeDetector.Create(ModelDir, new[] { WakeKeywordSpec.DefaultKeyword, "abc @错" }, Scratch);
            try
            {
                Assert.IsTrue(d.IsAvailable, d.Error);
                Assert.AreEqual(1, d.KeywordErrors.Count);
            }
            finally { d.Dispose(); }
        }

        [Test]
        public void 默认唤醒词_在真实词表里有效()
        {
            var vocab = WakeKeywordSpec.ParseVocabulary(File.ReadAllText(Path.Combine(ModelDir, "tokens.txt")));
            var r = WakeKeywordSpec.Build(new[] { WakeKeywordSpec.DefaultKeyword }, vocab);
            Assert.AreEqual(1, r.ValidCount, string.Join("; ", r.Errors));
        }
    }
}
```

- [ ] **Step 4: 跑测试确认失败（RED）**

EditMode 测试，`-testFilter "DouyinLive.Tests.SherpaWakeDetectorTests"`。
Expected：`rc≠0`、无结果 XML；Editor.log 有 `error CS0103`/`CS0246`（`SherpaWakeDetector` 不存在）。若出现「找不到 sherpa-onnx.dll」之类的引用错误，说明 Step 1 的 DLL 没放对位置——回 Step 1 检查。

- [ ] **Step 5: 写 SherpaWakeDetector**

创建 `Sherpa/SherpaWakeDetector.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SherpaOnnx;

namespace DouyinLive
{
    // sherpa-onnx 离线唤醒：主线程逐帧 Feed 麦克风采样，检出唤醒词返回 true。
    // 不依赖 UnityEngine（模型目录由调用方给），单独成程序集，用官方测试音频做 EditMode 测试。
    // 2026-10-18 spike 实测：真人麦克风 10/10、无误报、可连续唤醒，实时率 0.019。
    public sealed class SherpaWakeDetector
    {
        public const string EncoderFile = "encoder-epoch-12-avg-2-chunk-16-left-64.onnx";
        public const string DecoderFile = "decoder-epoch-12-avg-2-chunk-16-left-64.onnx";
        public const string JoinerFile = "joiner-epoch-12-avg-2-chunk-16-left-64.onnx";
        public const string TokensFile = "tokens.txt";

        KeywordSpotter kws;
        OnlineStream stream;
        readonly List<string> keywordErrors = new List<string>();

        public bool IsAvailable => kws != null;
        public string Error { get; private set; }
        public IReadOnlyList<string> KeywordErrors => keywordErrors;
        public string LastKeyword { get; private set; }

        SherpaWakeDetector() { }

        // 永不抛异常：缺文件、唤醒词全错、原生库加载失败都只置 IsAvailable=false 并写 Error，
        // 调用方据此退回音量检测模式——语音出问题不能影响直播。
        public static SherpaWakeDetector Create(string modelDir, IReadOnlyList<string> keywordLines, string scratchDir)
        {
            var d = new SherpaWakeDetector();
            try
            {
                foreach (var f in new[] { EncoderFile, DecoderFile, JoinerFile, TokensFile })
                {
                    string path = Path.Combine(modelDir, f);
                    if (!File.Exists(path)) { d.Error = "缺少模型文件 " + path; return d; }
                }

                var vocab = WakeKeywordSpec.ParseVocabulary(File.ReadAllText(Path.Combine(modelDir, TokensFile)));
                var kw = WakeKeywordSpec.Build(keywordLines, vocab);
                d.keywordErrors.AddRange(kw.Errors);
                if (kw.ValidCount == 0) { d.Error = "没有有效的唤醒词"; return d; }

                // 唤醒词走文件（spike 验证过的路径）：写到可写的缓存目录，UTF-8 无 BOM
                Directory.CreateDirectory(scratchDir);
                string kwFile = Path.Combine(scratchDir, "sherpa_keywords.txt");
                File.WriteAllText(kwFile, kw.KeywordsText, new UTF8Encoding(false));

                var c = new KeywordSpotterConfig();
                c.FeatConfig.SampleRate = 16000;
                c.FeatConfig.FeatureDim = 80;
                c.ModelConfig.Transducer.Encoder = Path.Combine(modelDir, EncoderFile);
                c.ModelConfig.Transducer.Decoder = Path.Combine(modelDir, DecoderFile);
                c.ModelConfig.Transducer.Joiner = Path.Combine(modelDir, JoinerFile);
                c.ModelConfig.Tokens = Path.Combine(modelDir, TokensFile);
                c.ModelConfig.Provider = "cpu";
                c.ModelConfig.NumThreads = 1;
                c.KeywordsThreshold = 0.25f;
                c.KeywordsScore = 1.0f;
                c.KeywordsFile = kwFile;

                d.kws = new KeywordSpotter(c);
                d.stream = d.kws.CreateStream();
            }
            catch (DllNotFoundException ex)
            {
                d.kws = null;
                d.Error = "找不到 sherpa 原生库（Plugins/SherpaOnnx/x86_64）：" + ex.Message;
            }
            catch (Exception ex)
            {
                d.kws = null;
                d.Error = "sherpa 初始化失败：" + ex.Message;
            }
            return d;
        }

        public bool Feed(float[] samples, int count, int sampleRate)
        {
            if (kws == null || samples == null || count <= 0) return false;
            float[] chunk = samples;
            if (count < samples.Length)
            {
                chunk = new float[count];
                Array.Copy(samples, chunk, count);
            }
            stream.AcceptWaveform(sampleRate, chunk);
            bool hit = false;
            while (kws.IsReady(stream))
            {
                kws.Decode(stream);
                var r = kws.GetResult(stream);
                if (!string.IsNullOrEmpty(r.Keyword))
                {
                    LastKeyword = r.Keyword;
                    kws.Reset(stream);   // 不 Reset 就检不出下一次唤醒
                    hit = true;
                }
            }
            return hit;
        }

        // KeywordSpotter 有 Dispose 但不保证实现 IDisposable，显式调用；
        // OnlineStream 是否实现 IDisposable 不确定：先转 object 再判断，密封类直接 as 接口会编译失败（CS0039）
        public void Dispose()
        {
            object s = stream;
            (s as IDisposable)?.Dispose();
            stream = null;
            kws?.Dispose();
            kws = null;
        }
    }
}
```

- [ ] **Step 6: 跑测试确认通过（GREEN）**

同 Step 4 命令。Expected：`result="Passed" total="5" passed="5" failed="0"`。
若 `官方音频_检出官方唤醒词` 失败、而另外 4 个通过：先确认 Step 1 复制的 3.wav 是 257006 字节；仍失败就把 `found` 的内容打印进断言消息再分析（spike 时同样的音频、同样的参数检出了 `[文森特卡索, 法国]`），不要靠调低阈值让它通过。

- [ ] **Step 7: 确认原生 DLL 的平台导入设置**

```bash
cd "E:/Work/AI/Mate-Engine/Assets/Plugins/SherpaOnnx/x86_64"
for f in onnxruntime.dll.meta sherpa-onnx-c-api.dll.meta; do echo "== $f"; grep -A3 "Editor: Editor\|Standalone: Win64" "$f" | grep -i "enabled"; done
```

Expected：两个 .meta 里 Editor 和 Standalone Win64 都是 `enabled: 1`（Unity 对 `x86_64` 目录下的 DLL 默认这么配）。若某项为 0，把它改为 1 后重跑 Step 6 的测试，并在台账里记一条 Ruling。

- [ ] **Step 8: 全量回归**

Expected：`total="226" passed="226" failed="0"`（221 + 5）。

- [ ] **Step 9: 提交**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/Plugins/SherpaOnnx" "Assets/Plugins/SherpaOnnx.meta" \
        "Assets/StreamingAssets/SherpaOnnx" "Assets/StreamingAssets/SherpaOnnx.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Sherpa" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Sherpa.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/TestData" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/TestData.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/SherpaWakeDetectorTests.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/SherpaWakeDetectorTests.cs.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/MateEngine.DouyinLive.Tests.asmdef"
git status --short | grep -i "sherpa\|TestData" 
git commit -m "feat(douyin-live): 引入 sherpa-onnx 离线唤醒引擎与 SherpaWakeDetector（官方音频自动化测试）

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

`git status` 那行用来确认没有遗漏未暂存的 sherpa 文件（应无输出）。

---

### Task 4: 播报与音乐的会话接口（SpeechPipeline / SongService）

**Files:**
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/SpeechPipeline.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/SongService.cs`

**Interfaces:**
- Consumes: 无
- Produces（Task 5 使用）：
  - `SpeechPipeline.IsTalking`（bool：正在出声，含句间等合成；不含说完后的气泡停留）
  - `SpeechPipeline.SetHold(bool on)`
  - `SpeechPipeline.InterruptCurrent()`
  - `SpeechPipeline.PlayImmediate(string text, AudioClip clip, Action onFinished)`
  - `SongService.Duck(float level)`、`SongService.Unduck()`

这两个都是 MonoBehaviour，没有单元测试；验证靠编译 + 全量回归 + 手工验收（Review Focus 第 1、2 条）。

- [ ] **Step 1: SpeechPipeline——新增状态字段与属性**

确认文件顶部有 `using System;`（`Action` 要用），没有就加上。

在字段区 `Coroutine speakRoutine;` 之后加：

```csharp
        bool hold;              // 主播说话期间暂停出队（队列保留）
        bool lingering;         // 说完后气泡停留阶段
        AudioClip ownedClip;    // 当前正在播、归本管线所有的 TTS 片段（被打断时要销毁）
```

在 `public int QueueCount ...` 之后加：

```csharp
        // 只在真正出声（含句间等待合成）时为 true；说完后气泡停留阶段为 false。
        // 语音会话判断「她说完了」用这个：IsSpeaking 要多等 bubbleLinger 秒，追问窗口会晚开 3 秒。
        public bool IsTalking => speaking && !lingering;
```

- [ ] **Step 2: SpeechPipeline——出队受暂停控制**

`Update()` 里把：

```csharp
            if (!speaking)
            {
```

改为：

```csharp
            if (!speaking && !hold)
            {
```

- [ ] **Step 3: SpeechPipeline——SpeakRoutine 记录所有权与停留阶段**

在 `SpeakRoutine` 开头 `speaking = true;` 之后加一行：

```csharp
            lingering = false;
```

把创建并播放 TTS 片段那段：

```csharp
                    var clip = AudioClip.Create("tts", pcm.Samples.Length / pcm.Channels, pcm.Channels, pcm.SampleRate, false);
                    clip.SetData(pcm.Samples, 0);
```

改为：

```csharp
                    var clip = AudioClip.Create("tts", pcm.Samples.Length / pcm.Channels, pcm.Channels, pcm.SampleRate, false);
                    clip.SetData(pcm.Samples, 0);
                    ownedClip = clip;
```

把播完后的销毁：

```csharp
                    Destroy(clip);
```

改为：

```csharp
                    Destroy(clip);
                    ownedClip = null;
```

把协程末尾：

```csharp
            if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", false);
            if (anyShown) yield return new WaitForSeconds(bubbleLinger);
            RemoveBubble();
            speaking = false;
        }
```

改为：

```csharp
            if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", false);
            lingering = true;
            if (anyShown) yield return new WaitForSeconds(bubbleLinger);
            RemoveBubble();
            speaking = false;
            lingering = false;
        }
```

- [ ] **Step 4: SpeechPipeline——新增三个公开方法**

在 `public void ClearQueue()` 方法之后加：

```csharp
        // 主播说话期间暂停出队：队列保留，TTL 照常过期（过期的欢迎、点赞感谢该丢就丢）
        public void SetHold(bool on)
        {
            hold = on;
        }

        // 打断当前这一条（连同它剩下的句子），队列保留。主播唤醒时调用。
        public void InterruptCurrent()
        {
            if (speakRoutine != null) { StopCoroutine(speakRoutine); speakRoutine = null; }
            if (voiceSource != null && voiceSource.isPlaying) voiceSource.Stop();
            if (ownedClip != null) { Destroy(ownedClip); ownedClip = null; }
            if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", false);   // 不然会卡在张嘴
            RemoveBubble();
            speaking = false;
            lingering = false;
        }

        // 立即播放预合成音频（语音应答）：打断当前句，不进队列、不受 SetHold 影响。
        // clip 为空（TTS 没就绪）时只显示气泡。onFinished 在出声结束、气泡停留之前回调。
        // clip 归调用方所有（缓存反复用），这里绝不销毁。
        public void PlayImmediate(string text, AudioClip clip, Action onFinished)
        {
            InterruptCurrent();
            speakRoutine = StartCoroutine(ImmediateRoutine(text, clip, onFinished));
        }

        IEnumerator ImmediateRoutine(string text, AudioClip clip, Action onFinished)
        {
            speaking = true;
            lingering = false;
            ShowBubble(text);
            if (clip != null && voiceSource != null)
            {
                voiceSource.clip = clip;
                voiceSource.volume = volume;
                voiceSource.Play();
                if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", true);
                while (voiceSource != null && voiceSource.isPlaying) yield return null;
                if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", false);
            }
            lingering = true;
            onFinished?.Invoke();
            yield return new WaitForSeconds(bubbleLinger);
            RemoveBubble();
            speaking = false;
            lingering = false;
            speakRoutine = null;
        }
```

- [ ] **Step 5: SongService——压低/恢复**

在字段区 `public SpeechPipeline Speech;` 之后加：

```csharp
        float duckLevel = 1f;   // 语音会话期间的歌声比例，1=不压低
```

在 `public void StopSong()` 方法之前加：

```csharp
        // 主播唤醒时把歌声压低（像手机助手压低音乐），会话结束恢复。
        // 会话期间新歌开播也遵守当前压低状态（见 SearchAndPlay）。
        public void Duck(float level)
        {
            duckLevel = Mathf.Clamp01(level);
            if (musicSource != null) musicSource.volume = duckLevel;
        }

        public void Unduck()
        {
            duckLevel = 1f;
            if (musicSource != null) musicSource.volume = 1f;
        }
```

在 `SearchAndPlay` 里把：

```csharp
            musicSource.volume = 1f;   // 响度已在 PCM 上归一化，这里不再叠第二个音量系数
```

改为：

```csharp
            musicSource.volume = duckLevel;   // 响度已在 PCM 上归一化；只叠语音会话的压低比例（平时为 1）
```

- [ ] **Step 6: 编译验证**

Expected：`rc=0 errorCS=0 anotherInstance=0`。

- [ ] **Step 7: 全量回归**

Expected：`total="226" passed="226" failed="0"`（本任务不加测试）。

- [ ] **Step 8: 提交**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/SpeechPipeline.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/SongService.cs"
git commit -m "feat(douyin-live): 播报支持打断/立即播放/暂停出队，歌曲支持压低音量——语音会话所需接口

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: 应答话术 + 监听器重写 + 配置与接线 + 文档

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/VoiceAckLines.cs`
- Modify（整文件重写）: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/VoiceCommandListener.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs`
- Modify: `Docs/DouyinLive-Integration.md`

**Interfaces:**
- Consumes：`VoiceSessionMachine`/`VoiceSessionTiming`/`VoiceAction`（Task 1）；`VoiceCaptureBuffer.NotifyWake(float)`/`TakeArmed`、`WakeKeywordSpec.DefaultKeyword`（Task 2）；`SherpaWakeDetector`（Task 3）；`SpeechPipeline.IsTalking/SetHold/InterruptCurrent/PlayImmediate`、`SongService.Duck/Unduck`（Task 4）；既有 `ITTSProvider.SynthesizeAsync`、`TTSResult`、`MainThreadDispatcher.Post`、`VoiceCommandParser.ExtractCommand`、`WavEncoder.Encode`、`CloudASRProvider.TranscribeAsync`、`DouyinLiveManager.InjectVoiceChat`
- Produces：`class VoiceListenerSettings`；`VoiceCommandListener.Configure(SpeechPipeline, SongService, DouyinLiveManager, CloudASRProvider, VoiceListenerSettings)`；`DouyinLiveManager.VoiceBlocked`；设置字段 `douyinVoiceWakeKeywords/douyinVoiceAckLines/douyinVoiceAckTimeout/douyinVoiceFollowUpSeconds/douyinVoiceDuckVolume`

本任务全在 Assembly-CSharp（MonoBehaviour 与 Unity 音频），没有单元测试；时序规则已在 Task 1 测完，这里是接线。验证靠编译 + 全量回归 + 手工验收。

- [ ] **Step 1: 配置字段**

`SaveLoadHandler.cs` 的 SettingsData 语音块里，把：

```csharp
        public bool douyinVoicePauseWhilePerforming = true; // 角色出声时暂停监听，防自触发
        public bool douyinVoiceKeywordWake = true;          // false=强制音量检测模式（与本地识别器抢设备时的逃生门）
```

替换为：

```csharp
        public bool douyinVoicePauseWhilePerforming = true; // 已废弃（语音 v2 改为打断/压低音乐/暂停播报），保留字段兼容老配置，不再读取
        public bool douyinVoiceKeywordWake = true;          // true=sherpa 离线唤醒（推荐）；false=音量检测模式（必须一口气说完）
        // sherpa 离线唤醒词：带声调拼音、声母韵母分开写，@ 后是显示名。写错的音节启动日志会指出
        public List<string> douyinVoiceWakeKeywords = new() { DouyinLive.WakeKeywordSpec.DefaultKeyword };
        public List<string> douyinVoiceAckLines = new() { "我在～", "嗯？", "你说～", "在呢" };   // 唤醒应答，开播时预合成
        public float douyinVoiceAckTimeout = 5f;            // 应答后多久没开口就放弃（装配时钳 2~15）
        public float douyinVoiceFollowUpSeconds = 6f;       // 她回答后可直接追问的窗口，0=关闭（装配时钳 0~20）
        public float douyinVoiceDuckVolume = 0.3f;          // 唤醒时歌声压低到的比例
```

- [ ] **Step 2: 应答话术 VoiceAckLines**

创建 `VoiceAckLines.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DouyinLive
{
    // 语音应答话术：开播时用当前 TTS 预合成并缓存成 AudioClip，唤醒后零延迟播放
    // （现合成要等网络，主播喊完要干等一两秒）。合成失败或 TTS 关闭时只弹气泡。
    public class VoiceAckLines
    {
        public const string ListeningBubble = "我在听～";
        public const string NotHeardLine = "没听清～";

        public bool debugLog;

        readonly List<AudioClip> acks = new List<AudioClip>();
        AudioClip notHeard;
        string fingerprint;
        int generation;   // 配置变了作废在途合成，免得旧话术晚到混进新列表

        // ApplySettings 会高频调用（F9/设置页）：TTS 和话术都没变就什么也不做
        public void Prepare(ITTSProvider provider, IReadOnlyList<string> lines)
        {
            string fp = (provider != null ? provider.Name : "none") + "|" + (lines == null ? "" : string.Join("|", lines));
            if (fp == fingerprint) return;
            fingerprint = fp;
            generation++;
            Clear();
            if (provider == null || !provider.IsAvailable || lines == null) return;
            int gen = generation;
            foreach (var line in lines)
                if (!string.IsNullOrWhiteSpace(line)) Synth(provider, line.Trim(), gen, false);
            Synth(provider, NotHeardLine, gen, true);
        }

        void Synth(ITTSProvider provider, string text, int gen, bool isNotHeard)
        {
            Task.Run(() => provider.SynthesizeAsync(text, CancellationToken.None))
                .ContinueWith(t =>
                {
                    TTSResult r = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    if (r == null && debugLog)
                        Debug.Log($"[VoiceAck] 应答「{text}」合成失败：{t.Exception?.GetBaseException().Message}");
                    MainThreadDispatcher.Post(() =>
                    {
                        if (gen != generation || r == null || !r.IsValid) return;
                        var clip = AudioClip.Create("ack", r.Samples.Length / r.Channels, r.Channels, r.SampleRate, false);
                        clip.SetData(r.Samples, 0);
                        if (isNotHeard) notHeard = clip; else acks.Add(clip);
                    });
                }, TaskScheduler.Default);
        }

        public void PlayAck(SpeechPipeline speech, Action onFinished)
        {
            AudioClip clip = acks.Count > 0 ? acks[UnityEngine.Random.Range(0, acks.Count)] : null;
            speech.PlayImmediate(ListeningBubble, clip, onFinished);
        }

        public void PlayNotHeard(SpeechPipeline speech)
        {
            speech.PlayImmediate(NotHeardLine, notHeard, null);
        }

        void Clear()
        {
            foreach (var c in acks) if (c != null) UnityEngine.Object.Destroy(c);
            acks.Clear();
            if (notHeard != null) UnityEngine.Object.Destroy(notHeard);
            notHeard = null;
        }
    }
}
```

- [ ] **Step 3: 重写 VoiceCommandListener**

把 `VoiceCommandListener.cs` 整个文件替换为：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DouyinLive
{
    public class VoiceListenerSettings
    {
        public List<string> WakeWords = new List<string>();       // 中文唤醒词：从 ASR 文本里剥掉；音量检测模式靠它判定唤醒
        public List<string> WakeKeywords = new List<string>();    // sherpa 拼音唤醒词
        public List<string> AckLines = new List<string>();
        public float CommandWindow = 8f;
        public float SilenceSeconds = 1.2f;
        public float AckTimeout = 5f;
        public float FollowUpSeconds = 6f;
        public float DuckVolume = 0.3f;
        public bool KeywordWake = true;
        public bool DebugLog;
    }

    // 语音助手入口（v2）：麦克风常开 → sherpa 离线唤醒 → 会话状态机（应答/聆听/追问/打断/压低音乐）
    // → 云端 ASR → 剥唤醒词 → InjectVoiceChat。时序规则全在 VoiceSessionMachine，这里只负责
    // 读麦克风、喂引擎、执行状态机给出的动作。
    // sherpa 不可用（DLL/模型缺失、唤醒词全写错、配置关闭）时退回旧的音量检测流程：
    // 一口气说完、无应答、角色出声期间暂停监听。
    public class VoiceCommandListener : MonoBehaviour
    {
        const int SampleRate = 16000;
        const float PreWakeSeconds = 3f;      // 音量检测模式的回看（起音判定本身有 0.3 秒延迟）
        const float WakePreRoll = 0.6f;       // sherpa 模式唤醒/追问起音时的回看，盖住起音前的 0.3 秒
        const float VoicedRms = 0.02f;        // 会话状态机的「有声」门槛
        const float SilenceRms = 0.015f;      // 音量检测模式的静音判定（沿用 v1）
        const float VadStartSeconds = 0.3f;   // 音量检测模式：连续这么久超阈值才算起音
        const float VadStartRms = 0.03f;

        public bool debugLog;

        VoiceListenerSettings cfg = new VoiceListenerSettings();
        SpeechPipeline speech;
        SongService song;
        DouyinLiveManager manager;
        CloudASRProvider asr;
        readonly VoiceAckLines ackLines = new VoiceAckLines();

        string collectionKey;   // 影响采集的参数指纹，变了才重启麦克风/唤醒引擎
        VoiceCaptureBuffer buffer;
        AudioClip micClip;
        string micDevice;
        int lastReadPos;
        float frameRms;
        float vadLoudSince = -1f;
        bool asrInFlight;
        bool blockedLastFrame;
        CancellationTokenSource cancel;

        SherpaWakeDetector wake;
        VoiceSessionMachine machine;   // 非空 = sherpa 模式

        CustomDancePlayer.AvatarDanceHandler mmdDance;   // MMD 舞包的原曲不经 SongService，要单独盯
        float mmdNextFind;
        float mmdVolumeBeforeDuck = -1f;

        public void Configure(SpeechPipeline speechPipeline, SongService songService,
                              DouyinLiveManager managerRef, CloudASRProvider asrProvider,
                              VoiceListenerSettings settings)
        {
            speech = speechPipeline;
            song = songService;
            manager = managerRef;
            asr = asrProvider;
            cfg = settings ?? new VoiceListenerSettings();
            cfg.WakeWords = cfg.WakeWords ?? new List<string>();
            cfg.WakeKeywords = cfg.WakeKeywords ?? new List<string>();
            cfg.CommandWindow = Mathf.Clamp(cfg.CommandWindow, 2f, 15f);
            cfg.SilenceSeconds = Mathf.Clamp(cfg.SilenceSeconds, 0.5f, 3f);
            cfg.AckTimeout = Mathf.Clamp(cfg.AckTimeout, 2f, 15f);
            cfg.FollowUpSeconds = Mathf.Clamp(cfg.FollowUpSeconds, 0f, 20f);
            cfg.DuckVolume = Mathf.Clamp01(cfg.DuckVolume);
            debugLog = cfg.DebugLog;
            ackLines.debugLog = debugLog;
            ackLines.Prepare(speech != null ? speech.Provider : null, cfg.AckLines);

            // ApplySettings 高频调用：只有影响采集的参数变了才重启（重启有拾音空洞，还要重新加载模型）
            var key = string.Join("|", cfg.WakeWords) + "|" + string.Join("|", cfg.WakeKeywords) + "|" +
                      cfg.CommandWindow + "|" + cfg.SilenceSeconds + "|" + cfg.KeywordWake + "|" +
                      cfg.AckTimeout + "|" + cfg.FollowUpSeconds;
            if (isActiveAndEnabled && key != collectionKey) { StopMic(); StartMic(); }
            collectionKey = key;
        }

        // StartLive/StopLive 调用：丢弃进行中的会话与在途转写，恢复被压低的音乐和暂停的播报
        public void ResetSession()
        {
            if (machine != null) Execute(machine.Abort());
            cancel?.Cancel();
            cancel = new CancellationTokenSource();
            buffer?.Clear();
            vadLoudSince = -1f;
        }

        void OnEnable() { StartMic(); }
        void OnDisable() { StopMic(); }
        void OnDestroy() { StopMic(); }

        void StartMic()
        {
            if (Microphone.devices.Length == 0)
            {
                Debug.LogWarning("[Voice] 没有可用麦克风（检查 Windows 麦克风权限），语音助手不启动");
                enabled = false;
                return;
            }
            micDevice = Microphone.devices[0];
            // clip 长度必须盖住环形缓冲（回看 + 窗口 + 余量），环绕读取才安全
            int clipSec = Mathf.CeilToInt(PreWakeSeconds + cfg.CommandWindow + 1f);
            micClip = Microphone.Start(micDevice, true, clipSec, SampleRate);
            if (micClip == null)
            {
                Debug.LogWarning("[Voice] 麦克风打开失败，语音助手不启动");
                enabled = false;
                return;
            }
            buffer = new VoiceCaptureBuffer(micClip.frequency, PreWakeSeconds, cfg.CommandWindow, cfg.SilenceSeconds, SilenceRms);
            lastReadPos = Microphone.GetPosition(micDevice);
            cancel?.Cancel();
            cancel = new CancellationTokenSource();
            asrInFlight = false;
            vadLoudSince = -1f;
            blockedLastFrame = false;

            TryStartSherpa();
            if (machine == null && cfg.WakeWords.Count == 0)
            {
                Debug.LogWarning("[Voice] 音量检测模式需要中文唤醒词（douyinVoiceWakeWords 为空），语音助手不启动");
                StopMic();
                enabled = false;
                return;
            }
            Debug.Log($"[Voice] 已启动 {micDevice} @{micClip.frequency}Hz 模式={(machine != null ? "离线唤醒(sherpa)" : "音量检测")}");
        }

        void TryStartSherpa()
        {
            machine = null;
            if (!cfg.KeywordWake)
            {
                Debug.Log("[Voice] 已配置为音量检测模式（douyinVoiceKeywordWake=false）");
                return;
            }
            try
            {
                wake = SherpaWakeDetector.Create(
                    Path.Combine(Application.streamingAssetsPath, "SherpaOnnx", "kws"),
                    cfg.WakeKeywords, Application.temporaryCachePath);
            }
            catch (Exception ex)
            {
                // 托管 sherpa-onnx.dll 缺失时异常在这里抛（Create 内部只兜得住原生库缺失）
                wake = null;
                Debug.LogWarning("[Voice] sherpa 离线唤醒加载失败，退回音量检测模式：" + ex.Message);
                return;
            }
            foreach (var e in wake.KeywordErrors) Debug.LogWarning("[Voice] 唤醒词配置有误，已跳过：" + e);
            if (!wake.IsAvailable)
            {
                Debug.LogWarning("[Voice] sherpa 离线唤醒不可用，退回音量检测模式：" + wake.Error);
                wake.Dispose();
                wake = null;
                return;
            }
            var t = VoiceSessionTiming.Default;
            t.AckTimeout = cfg.AckTimeout;
            t.EndSilence = cfg.SilenceSeconds;
            t.MaxUtterance = cfg.CommandWindow;
            t.FollowUpWindow = cfg.FollowUpSeconds;
            machine = new VoiceSessionMachine(t);
        }

        void StopMic()
        {
            if (machine != null) Execute(machine.Abort());   // 先恢复音乐和播报，再拆
            machine = null;
            if (wake != null) { wake.Dispose(); wake = null; }
            if (!string.IsNullOrEmpty(micDevice) && Microphone.IsRecording(micDevice))
                Microphone.End(micDevice);
            micClip = null;
            buffer = null;
            cancel?.Cancel();
            cancel = null;
        }

        void Update()
        {
            if (asr == null || manager == null || buffer == null) return;   // 未装配/麦克风没起来

            // 停播、菜单打开：中止会话（恢复音乐和播报），丢弃这段时间的音频
            if (manager.VoiceBlocked)
            {
                if (!blockedLastFrame && machine != null) Execute(machine.Abort());
                blockedLastFrame = true;
                lastReadPos = Microphone.GetPosition(micDevice);
                buffer.Clear();
                vadLoudSince = -1f;
                return;
            }
            blockedLastFrame = false;

            if (machine != null) UpdateSherpa();
            else UpdateVad();
        }

        void UpdateSherpa()
        {
            float now = Time.unscaledTime;
            var chunk = ReadMic();
            bool talking = speech != null && speech.IsTalking;
            bool performing = IsPerforming();
            if (chunk != null && wake.Feed(chunk, chunk.Length, micClip.frequency))
            {
                if (debugLog) Debug.Log("[Voice] 唤醒: " + wake.LastKeyword);
                Execute(machine.OnWake(now, talking, performing));
            }
            if (machine != null)
                Execute(machine.Update(now, chunk != null && frameRms >= VoicedRms, talking, performing));
        }

        // 音量检测模式（v1 行为）：角色出声期间暂停监听防自触发；起音即唤醒；靠静音判定说完
        void UpdateVad()
        {
            if (IsCharacterAudible())
            {
                buffer.Clear();
                lastReadPos = Microphone.GetPosition(micDevice);
                vadLoudSince = -1f;
                return;
            }
            ReadMic();
            if (!asrInFlight)
            {
                if (frameRms >= VadStartRms) { if (vadLoudSince < 0f) vadLoudSince = Time.unscaledTime; }
                else vadLoudSince = -1f;
                if (vadLoudSince >= 0f && Time.unscaledTime - vadLoudSince >= VadStartSeconds)
                {
                    buffer.NotifyWake();
                    vadLoudSince = -1f;
                    if (debugLog) Debug.Log("[Voice] 音量检测唤醒");
                }
            }
            if (!asrInFlight && buffer.UtteranceReady && buffer.TryTakeUtterance(out var samples))
                SubmitToAsr(samples);
        }

        // 读出本帧新增的麦克风采样（跨环绕点分两段），推进录音缓冲，算本帧 RMS
        float[] ReadMic()
        {
            frameRms = 0f;
            if (micClip == null || string.IsNullOrEmpty(micDevice)) return null;
            int pos = Microphone.GetPosition(micDevice);
            int total = micClip.samples;
            int available = pos >= lastReadPos ? pos - lastReadPos : total - lastReadPos + pos;
            if (available <= 0) return null;
            if (available > total) available = total;

            var chunk = new float[available];
            int first = Mathf.Min(available, total - lastReadPos);
            if (first == available)
            {
                micClip.GetData(chunk, lastReadPos);
            }
            else
            {
                var head = new float[first];
                micClip.GetData(head, lastReadPos);
                Array.Copy(head, 0, chunk, 0, first);
                var tail = new float[available - first];
                micClip.GetData(tail, 0);
                Array.Copy(tail, 0, chunk, first, tail.Length);
            }
            lastReadPos = (lastReadPos + available) % total;

            double sum = 0;
            for (int i = 0; i < available; i++) sum += (double)chunk[i] * chunk[i];
            frameRms = (float)Math.Sqrt(sum / available);
            buffer.Push(chunk);
            return chunk;
        }

        void Execute(IReadOnlyList<VoiceAction> actions)
        {
            if (actions == null || actions.Count == 0) return;
            // 状态机返回的是内部复用列表；执行中的回调（应答没音频时同步回调 OnAckFinished）
            // 会再次调用状态机改写它，必须先拷贝
            var list = new VoiceAction[actions.Count];
            for (int i = 0; i < list.Length; i++) list[i] = actions[i];

            foreach (var a in list)
            {
                switch (a)
                {
                    case VoiceAction.InterruptSpeech: speech?.InterruptCurrent(); break;
                    case VoiceAction.HoldSpeech: speech?.SetHold(true); break;
                    case VoiceAction.ReleaseSpeech: speech?.SetHold(false); break;
                    case VoiceAction.DuckMusic: DuckMusic(); break;
                    case VoiceAction.UnduckMusic: UnduckMusic(); break;
                    case VoiceAction.StartCapture: buffer?.NotifyWake(WakePreRoll); break;
                    case VoiceAction.StartCaptureAfterAck: buffer?.NotifyWake(0f); break;
                    case VoiceAction.StopCapture: buffer?.Reset(); break;
                    case VoiceAction.PlayAck:
                        if (speech != null) ackLines.PlayAck(speech, OnAckFinished);
                        else OnAckFinished();
                        break;
                    case VoiceAction.SubmitCapture:
                        if (buffer != null && buffer.TakeArmed(out var samples)) SubmitToAsr(samples);
                        else if (machine != null) Execute(machine.OnTranscript(Time.unscaledTime, null));
                        break;
                    case VoiceAction.Inject:
                        if (machine != null && !string.IsNullOrEmpty(machine.PendingCommand))
                        {
                            if (debugLog) Debug.Log("[Voice] 命令: " + machine.PendingCommand);
                            manager?.InjectVoiceChat(machine.PendingCommand);
                        }
                        break;
                    case VoiceAction.SayNotHeard:
                        if (speech != null) ackLines.PlayNotHeard(speech);
                        break;
                }
            }
        }

        void OnAckFinished()
        {
            if (this == null || !enabled || machine == null) return;
            Execute(machine.OnAckFinished(Time.unscaledTime));
        }

        void DuckMusic()
        {
            song?.Duck(cfg.DuckVolume);
            FindMmd();
            if (mmdDance != null && mmdDance.audioSource != null && mmdVolumeBeforeDuck < 0f)
            {
                mmdVolumeBeforeDuck = mmdDance.audioSource.volume;
                mmdDance.audioSource.volume = mmdVolumeBeforeDuck * cfg.DuckVolume;
            }
        }

        void UnduckMusic()
        {
            song?.Unduck();
            // 恢复到压低前的原音量，而不是 1：MMD 舞包可能本来就不是满音量
            if (mmdVolumeBeforeDuck >= 0f && mmdDance != null && mmdDance.audioSource != null)
                mmdDance.audioSource.volume = mmdVolumeBeforeDuck;
            mmdVolumeBeforeDuck = -1f;
        }

        void FindMmd()
        {
            if (mmdDance == null && Time.unscaledTime >= mmdNextFind)
            {
                mmdDance = FindFirstObjectByType<CustomDancePlayer.AvatarDanceHandler>();
                mmdNextFind = Time.unscaledTime + 5f;   // 场景里没有时限频重找，别每帧扫
            }
        }

        // 在唱歌/跳舞（SongService 点歌，或 MMD 舞包/礼物舞的原曲）
        bool IsPerforming()
        {
            if (song != null && song.IsPlaying) return true;
            FindMmd();
            return mmdDance != null && mmdDance.audioSource != null && mmdDance.audioSource.isPlaying;
        }

        // 音量检测模式用：角色任何出声（说话、排队待说、唱歌）都暂停监听
        bool IsCharacterAudible()
        {
            if (speech != null && (speech.IsSpeaking || speech.QueueCount > 0)) return true;
            return IsPerforming();
        }

        void SubmitToAsr(float[] samples)
        {
            byte[] wav;
            try { wav = WavEncoder.Encode(samples, buffer.SampleRate); }
            catch (Exception ex)
            {
                if (debugLog) Debug.Log("[Voice] WAV 编码失败: " + ex.Message);
                if (machine != null) Execute(machine.OnTranscript(Time.unscaledTime, null));
                return;
            }
            asrInFlight = true;   // 音量检测模式：在飞期间忽略新起音
            var ct = cancel != null ? cancel.Token : CancellationToken.None;
            _ = TranscribeAndSubmit(wav, ct);
        }

        async Task TranscribeAndSubmit(byte[] wav, CancellationToken ct)
        {
            string text = null;
            try { text = await asr.TranscribeAsync(wav, ct); }
            catch (Exception ex) { if (debugLog) Debug.Log("[Voice] ASR 异常: " + ex.Message); }
            var t = text;
            // 转写可能从线程池线程回来，统一转主线程再碰 Unity 对象
            MainThreadDispatcher.Post(() => HandleTranscript(t));
        }

        void HandleTranscript(string text)
        {
            if (this == null) return;
            asrInFlight = false;

            if (machine != null)
            {
                // sherpa 已确认唤醒，文本里找不到唤醒词也放行；null = ASR 失败
                string cmd = text == null ? null : VoiceCommandParser.ExtractCommand(text, cfg.WakeWords, false);
                if (debugLog) Debug.Log($"[Voice] 转写: {text ?? "(失败)"} → 命令: {cmd ?? "(无)"}");
                Execute(machine.OnTranscript(Time.unscaledTime, cmd));
                return;
            }

            // 音量检测模式：文本里必须有唤醒词
            if (string.IsNullOrEmpty(text))
            {
                if (debugLog) Debug.Log("[Voice] ASR 无结果");
                return;
            }
            string command = VoiceCommandParser.ExtractCommand(text, cfg.WakeWords, true);
            if (command == null)
            {
                if (debugLog) Debug.Log("[Voice] 无唤醒词，丢弃: " + text);
                return;
            }
            if (command.Length == 0)
            {
                if (debugLog) Debug.Log("[Voice] 只有唤醒词: " + text);
                return;
            }
            if (debugLog) Debug.Log("[Voice] 命令: " + command);
            manager?.InjectVoiceChat(command);
        }
    }
}
```

- [ ] **Step 4: DouyinLiveManager 接线**

① 在 `public void InjectVoiceChat(string text)` 方法之前加：

```csharp
        // 语音监听器每帧查询：停播或菜单打开时中止会话、恢复音乐和播报
        public bool VoiceBlocked => !running || IsBlocked();
```

② `ApplySettings` 里把原来的：

```csharp
            voiceListener.Configure(speech, songService, this, asrProvider,
                d.douyinVoiceWakeWords, d.douyinVoiceCommandWindow, d.douyinVoiceSilenceSeconds,
                d.douyinVoicePauseWhilePerforming, d.douyinVoiceKeywordWake, debugLog);
```

替换为：

```csharp
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
```

③ 确认 `speech.Provider` 在这次 `Configure` 之前已被赋值（应答话术要用它预合成）：

```bash
cd "E:/Work/AI/Mate-Engine/Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive"
grep -n "speech.Provider =\|voiceListener.Configure" DouyinLiveManager.cs
```

Expected：`speech.Provider =` 的行号小于 `voiceListener.Configure` 的行号。若不是，把 `Configure` 调用整块移到 TTS 装配之后，并在台账记一条 Ruling。

- [ ] **Step 5: 编译验证**

Expected：`rc=0 errorCS=0 anotherInstance=0`。

- [ ] **Step 6: 全量回归**

Expected：`total="226" passed="226" failed="0"`。

- [ ] **Step 7: 文档**

`Docs/DouyinLive-Integration.md` 四处：

① settings.json 块里，把：

```jsonc
"douyinVoicePauseWhilePerforming": true,
"douyinVoiceKeywordWake": true,    // false=强制音量检测模式（与本地识别器抢设备时的逃生门）
```

替换为：

```jsonc
"douyinVoiceKeywordWake": true,    // true=sherpa 离线唤醒（推荐）；false=音量检测模式（必须一口气说完）
"douyinVoiceWakeKeywords": ["x iǎo m ěi x iǎo m ěi @小美小美"],  // 离线唤醒词：带声调拼音，声母韵母分开，@ 后写显示名
"douyinVoiceAckLines": ["我在～", "嗯？", "你说～", "在呢"],      // 唤醒应答，开播时预合成
"douyinVoiceAckTimeout": 5.0,      // 应答后多久没开口就放弃
"douyinVoiceFollowUpSeconds": 6.0, // 她回答后可直接追问的窗口，0=关闭
"douyinVoiceDuckVolume": 0.3,      // 唤醒时歌声压低到的比例
// douyinVoicePauseWhilePerforming 已废弃（v2 改为打断她、压低歌声、暂停播报）
```

② 「关键实现机制」里，把语音助手条目的前四行：

```markdown
- **语音助手**：麦克风常开环形缓冲（回看 3 秒），本地 KeywordRecognizer 命中唤醒词
  （系统无中文语音包自动降级音量检测）后录到静音 1.2 秒，WAV 送云端 ASR，文本剥唤醒词
  后伪装成 `UserId="voice"` 的弹幕进 Route()——触发规则/追问槽位/LLM 意图/点歌换角色全部复用；
  非命令内容走 AI 闲聊回复（陪播模式也不念出来）。角色出声期间暂停监听防自触发。
```

替换为：

```markdown
- **语音助手**：sherpa-onnx 离线关键词引擎常驻检测唤醒词（约 2% CPU，模型在
  `StreamingAssets/SherpaOnnx/kws`），会话状态机按手机助手方式交互：唤醒后停顿就播一句预合成
  应答再聆听，一口气说完则直接录；她说话时唤醒会打断当前句、唱歌时压低歌声；主播说话期间
  播报队列暂停；她回答后 6 秒内可直接追问。录音送云端 ASR，文本剥唤醒词后伪装成
  `UserId="voice"` 的弹幕进 Route()——触发规则/追问槽位/LLM 意图/点歌换角色全部复用；
  非命令内容走 AI 闲聊回复（陪播模式也不念出来）。sherpa 加载失败退回音量检测模式
  （一口气说完、角色出声期间暂停监听）。
```

（该条目后面关于开头词传话的几行保持不变。）

③ 「已知边界」末尾加一条：

```markdown
- 语音交互 v2：没有回声消除，建议戴耳机（外放时她的应答和歌声可能被录进指令）；离线唤醒词要写
  带声调的拼音（`x iǎo m ěi`，声母韵母分开），写错的音节启动日志会指出；sherpa 原生库约 22MB、
  模型约 13MB，打包体积相应增加。
```

④ 「语音助手自测步骤」里，保留开头的调试开关说明（引用块），把编号列表（从 `1. settings.json 开` 到最后一条）整体替换为：

```markdown
1. settings.json 开 `douyinVoiceEnabled`，确认 ASR 配置可用；启动日志出现 `[Voice] 已启动 ... 模式=离线唤醒(sherpa)`
2. 连续喊「小美小美」10 次（间隔两三秒）→ 每次都应答（日志 `[Voice] 唤醒`）
3. 喊「小美小美」后停下 → 她说「我在～」之类并弹气泡「我在听～」→ 再说「唱一首黄昏」→ 开唱+跟舞
4. 一口气说「小美小美唱一首黄昏」→ 不应答，直接执行
5. 喊「小美小美」后 5 秒不说话 → 安静回待机，不报错
6. 说「小美小美 今天直播什么好」→ AI 开口聊；她说完后 6 秒内直接说「那就聊游戏吧」（不喊唤醒词）→ 她接着回答
7. 她唱歌时喊「小美小美」→ 歌声明显变小，说完指令后恢复
8. 游戏陪播模式她念弹幕时喊「小美小美」→ 当前这句被打断、她应答；指令处理完后接着念剩下的弹幕
9. 一口气说「小美小美 告诉大家八点下播」→ 立刻原样播出「八点下播」
10. 说「小美小美 今天唱什么好」→ AI 对话回复，没有「传话播出」日志
11. 说「小美小美 告诉大家唱完这首有抽奖」→ 原样播出，不会被当成点歌
12. settings.json 设 `douyinVoiceRelayEnabled=false`（重启生效）→ 第 9 条变成 AI 对话回复。改回 true 再重启可恢复
13. 把 `Assets/Plugins/SherpaOnnx/x86_64/sherpa-onnx-c-api.dll` 临时改名后启动 → 日志警告「退回音量检测模式」，一口气说「小美小美唱一首黄昏」仍能用；测完改回
```

- [ ] **Step 8: 提交**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/VoiceAckLines.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/VoiceAckLines.cs.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/VoiceCommandListener.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs" \
        "Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs" \
        "Docs/DouyinLive-Integration.md"
git commit -m "feat(douyin-live): 语音交互 v2——sherpa 离线唤醒 + 手机式会话（应答/聆听/追问/打断/压低音乐）

移除 Windows KeywordRecognizer；sherpa 不可用时退回音量检测模式。
douyinVoicePauseWhilePerforming 废弃（保留字段兼容老配置）。

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### 手工验收（主播，非代码任务；Task 5 提交后）

前提：Unity 编辑器里 DouyinLiveManager（场景 `Settings` 对象）勾上 **Debug Log**；ASR 指向 SiliconFlow；**测试中途不要改代码**（Play 中热重载会清空单例、满屏报错）。

逐条执行 `Docs/DouyinLive-Integration.md`「语音助手自测步骤」1～13。重点对应 Review Focus：
- 第 7 条（压低后恢复）、第 8 条（打断后口型不卡、弹幕接着念）
- 第 13 条（缺 DLL 退回音量检测）
- 另做一次打包：确认 `MateEngineX_Data/Plugins/x86_64/` 下有 `onnxruntime.dll`、`sherpa-onnx-c-api.dll`，`MateEngineX_Data/StreamingAssets/SherpaOnnx/kws/` 下有模型四个文件，打包版启动日志为 `模式=离线唤醒(sherpa)`

## Spec 覆盖对照

| spec 条目 | 落点 |
|---|---|
| sherpa 替换 Windows 识别器、fp32 模型、目录布局 | Task 3 Step 1、Task 5 Step 3（`TryStartSherpa`） |
| 交互流程全部时序规则（判停顿/尾音/应答/聆听/处理/等回应/追问/重复唤醒/中止） | Task 1（30 个测试） |
| HoldSpeech/ReleaseSpeech 播报暂停 | Task 1 规则 + Task 4 `SetHold` + Task 5 执行 |
| 打断她说话、只停当前句 | Task 4 `InterruptCurrent` + Task 5 |
| 唱歌时压低到 30%、恢复（含 MMD 舞曲、新歌开播） | Task 4 `Duck/Unduck` + Task 5 `DuckMusic/UnduckMusic` |
| 预合成应答 4 句、合成失败只显示气泡、「没听清～」 | Task 5 `VoiceAckLines` + Task 4 `PlayImmediate` |
| 追问 6 秒、0.3 秒起音、不足 2 字静默丢弃 | Task 1 |
| 拼音唤醒词校验、写错音节报出、全部无效退回 | Task 2 `WakeKeywordSpec` + Task 3 `SherpaWakeDetector` |
| 加载失败退回音量检测（v1 行为） | Task 3（`IsAvailable`）+ Task 5（`UpdateVad`） |
| 停播/菜单打开中止并恢复 | Task 1 `Abort` + Task 5 `VoiceBlocked`/`ResetSession` |
| 新增配置 5 项、`douyinVoiceKeywordWake` 新含义、`douyinVoicePauseWhilePerforming` 废弃 | Task 5 Step 1 |
| 测试：状态机约 25 个、唤醒词校验、编译与回归、真机验收 | Task 1（30）、Task 2（15）、Task 3（5，含官方音频）、手工验收 |
| 文档 | Task 5 Step 7 |
