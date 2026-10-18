# 抖音直播语音助手(麦克风控制角色) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 主播对麦克风说「小美小美 唱一首黄昏」，角色 2.5~4 秒内开始唱跳；闲聊提问 AI 开口回答；复用现有弹幕意图链路。

**Architecture:** 麦克风常开环形缓冲 → 本地关键词唤醒(缺失降级音量检测) → 静音判定截取一句话 → 云端 ASR(OpenAI 兼容 /audio/transcriptions) → 剥唤醒词 → 伪装 `Chat` 弹幕(UserId="voice") 经 `DouyinLiveManager.InjectVoiceChat` 进现有 `Route()` 全链路。Core 纯逻辑(解析/缓冲/WAV)进 `MateEngine.DouyinLive.Core` 程序集配 EditMode 测试；Unity API 胶水保持薄。

**Tech Stack:** Unity 6000.2.6f2 / C# (netstandard2.1)、`UnityEngine.Microphone`、`UnityEngine.Windows.Speech.KeywordRecognizer`(Windows)、HttpClient multipart、NUnit EditMode。

**Spec:** [Docs/superpowers/specs/2026-10-18-douyin-voice-control-design.md](../specs/2026-10-18-douyin-voice-control-design.md)（本计划从 spec 论证，执行者两份都要读）

## Global Constraints

- **跑 Unity 前必须确认编辑器已关**：`ls "E:/Work/AI/Mate-Engine/Temp/UnityLockfile"` 必须不存在；编辑器开着时批处理 exit 21 且日志无 `error CS`，grep 会被骗绿。两个 grep（`error CS` 和 `another Unity instance`）都必须为 0。
- **禁止并行跑任何调用 Unity 的命令/子代理**，会互相抢 `Temp/UnityLockfile`，只能串行。
- EditMode 测试结果**固定落** `C:/Users/83529/AppData/LocalLow/Shinymoon/MateEngineX/TestResults.xml`（`-testResults` 参数不生效），读根节点 `total`/`passed`/`failed`。
- `MateEngine.DouyinLive.Core`：`references: []`，**不许** using UnityEngine / Newtonsoft / Debug.Log / Time.* / Mathf.* / Application.*；时间靠注入，样本由外部喂。
- `MateEngine.DouyinLive.Tests`：`overrideReferences: true`，**测试程序集拿不到 Newtonsoft**，测试只用 NUnit + Core 类型。
- batchmode 编译会为新建 .cs 生成 .meta，**每个任务的 commit 必须带上新文件的 .meta**。
- 批处理编译的是整个工程（含工作区里未提交的既有改动，如 ActionMarker 系列）；报错时先分清是不是既有改动的问题。
- 分支：开工建 `feat/douyin-voice`（若执行方式选了 worktree，由 using-git-worktrees 建隔离工作区，分支同名）。
- Commit 风格沿用仓库惯例：`feat(douyin-live): …` / `test(douyin-live): …` / `docs(douyin-live): …`，结尾加 `Co-Authored-By: Claude Code <noreply@anthropic.com>`。
- 代码注释用中文，风格对齐 `DouyinLive/` 既有文件（解释"为什么"，不解释"是什么"）。
- 全部新代码放 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/`（Core 子目录的走 Core 程序集）。

### 验证命令（各任务复用，下文以「跑编译」「跑测试」指代）

跑编译：

```bash
ls "E:/Work/AI/Mate-Engine/Temp/UnityLockfile" 2>/dev/null && echo "编辑器开着，先关 Unity 再跑" && exit 1
"/c/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe" -batchmode -quit \
  -projectPath "E:/Work/AI/Mate-Engine" -logFile /tmp/voice-compile.log
grep -c "error CS" /tmp/voice-compile.log                 # 必须 0
grep -ci "another Unity instance" /tmp/voice-compile.log  # 必须 0
```

跑测试（`<FILTER>` 换成测试类全名；最后回归留空跑全量）：

```bash
"/c/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe" -batchmode -runTests \
  -projectPath "E:/Work/AI/Mate-Engine" -testPlatform EditMode \
  -assemblyNames MateEngine.DouyinLive.Tests \
  -testFilter "<FILTER>" -logFile /tmp/voice-tests.log
R="C:/Users/83529/AppData/LocalLow/Shinymoon/MateEngineX/TestResults.xml"
grep -o 'total="[0-9]*"' "$R" | head -1     # 用例数符合预期
grep -o 'passed="[0-9]*"' "$R" | head -1
grep -o 'failed="[0-9]*"' "$R" | head -1    # 必须 failed="0"
```

## Review Focus

spec 暗示但没有单测覆盖、最容易咬到真实主播的五种输入：

1. **角色自己出声期间（TTS 播放/队列/唱歌）的音频绝不能进识别**——否则她唱的歌被麦克风拾到乱触发。钉子：Task 6 的 `Update` 早退分支（实现步含完整代码）+ Task 7 手工清单步骤 5。
2. **KeywordRecognizer 与 Unity Microphone 同时占用设备**（WASAPI 共享，理论可并存）——实测拾音异常时降级路径必须可用。钉子：Task 7 手工清单步骤 1（声音正常）与步骤 9（降级可用）。
3. **麦克风 clip 环绕点跨界读取**（连续说话/环境声超过 clip 长度后唤醒仍能取到完整音频）——钉子：Task 2 单测「环形滚掉旧数据后仍能取出」+ Task 7 手工清单步骤 11。
4. **停播/菜单打开时在途 ASR 回调不动作**——钉子：Task 4 `InjectVoiceChat` 的 `!running || IsBlocked()` 守卫 + Task 7 手工清单步骤 10。
5. **命令超过 CommandWindow 被截断**——宁可截断送去 ASR 也不能卡死等静音。钉子：Task 2 单测「超长强制就绪」。

---

### Task 1: Core/VoiceCommandParser —— 唤醒词剥离

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/VoiceCommandParser.cs`
- Test: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/VoiceCommandParserTests.cs`

**Interfaces:**
- Consumes: 无（纯函数，零依赖）
- Produces: `static string VoiceCommandParser.ExtractCommand(string text, IReadOnlyList<string> wakeWords, bool requireWakeWord)`。返回契约：`null` = 不执行（输入空，或 VAD 模式没找到唤醒词）；`""` = 只有唤醒词，丢弃；其它 = 命令文字。Task 6 的 listener 以 `ExtractCommand(text, WakeWords, requireWakeWord: !keywordMode)` 调用。

- [ ] **Step 1: 建分支（若还没有）**

```bash
cd "E:/Work/AI/Mate-Engine" && git checkout -b feat/douyin-voice 2>/dev/null || git checkout feat/douyin-voice
```

- [ ] **Step 2: 写失败测试**

创建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/VoiceCommandParserTests.cs`：

```csharp
using NUnit.Framework;
using System.Collections.Generic;

namespace DouyinLive.Tests
{
    public class VoiceCommandParserTests
    {
        static readonly List<string> Wake = new List<string> { "小美小美", "小美" };

        // ---------- 关键词模式（requireWakeWord=false：识别器已确认唤醒） ----------

        [Test]
        public void 带标点剥出命令()
        {
            Assert.AreEqual("唱一首黄昏", VoiceCommandParser.ExtractCommand("小美小美，唱一首黄昏", Wake, false));
            Assert.AreEqual("唱一首黄昏", VoiceCommandParser.ExtractCommand("小美小美。唱一首黄昏", Wake, false));
        }

        [Test]
        public void 无标点剥出命令()
        {
            Assert.AreEqual("唱一首黄昏", VoiceCommandParser.ExtractCommand("小美小美唱一首黄昏", Wake, false));
        }

        [Test]
        public void 前缀废话一起剥掉()
        {
            // 唤醒词不在句首：最早出现处连着前面的「你好」一起剥
            Assert.AreEqual("来首歌", VoiceCommandParser.ExtractCommand("你好小美小美。来首歌", Wake, false));
        }

        [Test]
        public void ASR空格重复唤醒词()
        {
            // 「小美小美」被 ASR 写成两段，开头残留的唤醒词要继续剥
            Assert.AreEqual("唱歌", VoiceCommandParser.ExtractCommand("小美 小美 唱歌", Wake, false));
        }

        [Test]
        public void 只有唤醒词返回空串()
        {
            Assert.AreEqual("", VoiceCommandParser.ExtractCommand("小美小美", Wake, false));
            Assert.AreEqual("", VoiceCommandParser.ExtractCommand("小美。", Wake, false));
            Assert.AreEqual("", VoiceCommandParser.ExtractCommand("你好小美小美", Wake, false));
        }

        [Test]
        public void 同位置取最长唤醒词()
        {
            // 「小美」先命中会把第二个「小美」留下来；必须整词「小美小美」剥干净
            Assert.AreEqual("唱一首黄昏", VoiceCommandParser.ExtractCommand("小美小美，唱一首黄昏", Wake, false));
        }

        [Test]
        public void 英文唤醒词忽略大小写()
        {
            var w = new List<string> { "HeyMei" };
            Assert.AreEqual("sing a song", VoiceCommandParser.ExtractCommand("heymei sing a song", w, false));
        }

        [Test]
        public void 没有唤醒词整句放行()
        {
            Assert.AreEqual("今天天气不错", VoiceCommandParser.ExtractCommand("今天天气不错", Wake, false));
        }

        // ---------- VAD 降级模式（requireWakeWord=true：文本里必须有唤醒词） ----------

        [Test]
        public void VAD无唤醒词拒绝()
        {
            Assert.IsNull(VoiceCommandParser.ExtractCommand("今天天气不错", Wake, true));
        }

        [Test]
        public void VAD有唤醒词剥出命令()
        {
            Assert.AreEqual("唱一首黄昏", VoiceCommandParser.ExtractCommand("小美小美，唱一首黄昏", Wake, true));
            Assert.AreEqual("唱歌", VoiceCommandParser.ExtractCommand("小美唱歌", Wake, true));
        }

        [Test]
        public void VAD只有唤醒词返回空串()
        {
            Assert.AreEqual("", VoiceCommandParser.ExtractCommand("小美小美", Wake, true));
        }

        // ---------- 输入防御 ----------

        [Test]
        public void 空输入返回null()
        {
            Assert.IsNull(VoiceCommandParser.ExtractCommand(null, Wake, false));
            Assert.IsNull(VoiceCommandParser.ExtractCommand("", Wake, false));
            Assert.IsNull(VoiceCommandParser.ExtractCommand("   ", Wake, false));
        }

        [Test]
        public void 唤醒词表为空()
        {
            Assert.IsNull(VoiceCommandParser.ExtractCommand("唱歌", new List<string>(), true));
            Assert.AreEqual("唱歌", VoiceCommandParser.ExtractCommand("唱歌", new List<string>(), false));
        }

        [Test]
        public void 词表里的空项不参与()
        {
            var w = new List<string> { "", null, "小美" };
            Assert.AreEqual("唱歌", VoiceCommandParser.ExtractCommand("小美唱歌", w, true));
        }
    }
}
```

- [ ] **Step 3: 跑编译确认失败**

跑编译。Expected：`error CS` 计数 **> 0**（`CS0246: VoiceCommandParser 未找到`）。若为 0 说明测试没进编译，先排查。

- [ ] **Step 4: 写实现**

创建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/VoiceCommandParser.cs`：

```csharp
using System;
using System.Collections.Generic;

namespace DouyinLive
{
    // 语音命令的文本清洗：云端 ASR 返回的是整句（「小美小美，唱一首黄昏」），
    // 把唤醒词剥掉，剩下的才是走 Route() 的命令文字。纯函数，Core 可测。
    //
    // 返回契约：null = 不执行（输入空，或 VAD 模式没找到唤醒词）；
    // "" = 只有唤醒词没内容；其它 = 命令文字。
    public static class VoiceCommandParser
    {
        // ASR 常见的首尾标点（全角为主）。剥完唤醒词把这些清掉再判空。
        static readonly char[] TrimChars =
            { ' ', '\t', '，', '。', '！', '？', '、', '~', '·', ',', '.', '!', '?', '“', '”', '"' };

        public static string ExtractCommand(string text, IReadOnlyList<string> wakeWords, bool requireWakeWord)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (wakeWords == null) wakeWords = Array.Empty<string>();

            string cur = text.Trim();
            if (!TryMatchEarliest(cur, wakeWords, out int end))
                return requireWakeWord ? null : cur;

            cur = cur.Substring(end).Trim(TrimChars);
            // ASR 可能把「小美小美」写成「小美 小美」：开头还挂着唤醒词就继续剥。
            // 轮数上限防病态输入（正常最多剥一两轮）。
            for (int n = 0; n < 4 && TryMatchAtStart(cur, wakeWords, out end); n++)
                cur = cur.Substring(end).Trim(TrimChars);
            return cur;
        }

        // 全句找最早的唤醒词；同一位置有多个命中取最长的
        // （「你好小美小美」里「小美」和「小美小美」都在 2，剥到 6 才干净）。
        static bool TryMatchEarliest(string s, IReadOnlyList<string> wakeWords, out int end)
        {
            end = 0;
            int bestAt = int.MaxValue, bestEnd = -1;
            for (int i = 0; i < wakeWords.Count; i++)
            {
                string w = wakeWords[i];
                if (string.IsNullOrEmpty(w)) continue;
                int at = s.IndexOf(w, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                int e = at + w.Length;
                if (at < bestAt || (at == bestAt && e > bestEnd)) { bestAt = at; bestEnd = e; }
            }
            if (bestEnd < 0) return false;
            end = bestEnd;
            return true;
        }

        // 唤醒词是否出现在句首（用于剥「小美 小美」这类 ASR 重复）。
        static bool TryMatchAtStart(string s, IReadOnlyList<string> wakeWords, out int end)
        {
            end = 0;
            for (int i = 0; i < wakeWords.Count; i++)
            {
                string w = wakeWords[i];
                if (string.IsNullOrEmpty(w) || s.Length < w.Length) continue;
                if (string.Compare(s, 0, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && w.Length > end)
                    end = w.Length;
            }
            return end > 0;
        }
    }
}
```

- [ ] **Step 5: 跑编译确认通过**

跑编译。Expected：两个 grep 都是 0。

- [ ] **Step 6: 跑测试确认通过**

跑测试，FILTER=`DouyinLive.Tests.VoiceCommandParserTests`。Expected：`total="15"`、`failed="0"`（用例数与 Step 2 一致）。

- [ ] **Step 7: Commit**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/VoiceCommandParser.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/VoiceCommandParser.cs.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/VoiceCommandParserTests.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/VoiceCommandParserTests.cs.meta"
git commit -m "test(douyin-live): 语音命令文本清洗 VoiceCommandParser，剥唤醒词配单测

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 2: Core/VoiceCaptureBuffer —— 环形缓冲 + 静音判定

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/VoiceCaptureBuffer.cs`
- Test: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/VoiceCaptureBufferTests.cs`

**Interfaces:**
- Consumes: 无
- Produces（Task 6 的 listener 全都用到）:
  - `new VoiceCaptureBuffer(int sampleRate, float preWakeSeconds, float maxUtteranceSeconds, float silenceSeconds, float rmsThreshold)`
  - `void Push(float[] samples)` / `void Push(float[] samples, int count)`
  - `void NotifyWake()` / `bool UtteranceReady { get; }` / `bool TryTakeUtterance(out float[] samples)`
  - `void Reset()` / `void Clear()` / `int SampleRate`

- [ ] **Step 1: 写失败测试**

创建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/VoiceCaptureBufferTests.cs`：

```csharp
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class VoiceCaptureBufferTests
    {
        // 方波 ±rms·√2 → RMS 恰为 rms（正弦要积分，方波直接算）
        static float[] Tone(float rms, int n)
        {
            var a = new float[n];
            float amp = rms * 1.4142f;
            for (int i = 0; i < n; i++) a[i] = i % 2 == 0 ? amp : -amp;
            return a;
        }

        [Test]
        public void 未唤醒取不出()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 0.5f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 500));
            float[] s;
            Assert.IsFalse(b.TryTakeUtterance(out s));
            Assert.IsFalse(b.UtteranceReady);
            Assert.IsNull(s);
        }

        [Test]
        public void 取出含唤醒前片段和唤醒后片段()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 500));   // 唤醒前 0.5 秒说话，只保留 PreWake 0.1s=100
            b.NotifyWake();
            b.Push(Tone(0.2f, 300));   // 唤醒后命令 0.3s
            b.Push(new float[250]);    // 静音 0.25s ≥ 0.2 → 说完了
            float[] s;
            Assert.IsTrue(b.TryTakeUtterance(out s));
            Assert.AreEqual(650, s.Length);   // 100 pre + 300 命令 + 250 静音尾
        }

        [Test]
        public void 唤醒后静音不足不就绪()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.NotifyWake();
            b.Push(Tone(0.2f, 300));
            b.Push(new float[150]);            // 0.15 < 0.2
            Assert.IsFalse(b.UtteranceReady);
            b.Push(new float[60]);             // 累计 0.21
            Assert.IsTrue(b.UtteranceReady);
        }

        [Test]
        public void 说话打断静音累计()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.NotifyWake();
            b.Push(new float[150]);
            b.Push(Tone(0.2f, 100));           // 出声 → 静音清零
            b.Push(new float[150]);
            Assert.IsFalse(b.UtteranceReady);
        }

        [Test]
        public void 超长强制就绪()
        {
            // 命令说个没完：宁可截断也不能卡死等静音
            var b = new VoiceCaptureBuffer(1000, 0.1f, 0.5f, 0.2f, 0.05f);
            b.NotifyWake();
            b.Push(Tone(0.2f, 600));           // 0.6s > 0.5 且一直出声
            Assert.IsTrue(b.UtteranceReady);
        }

        [Test]
        public void 取完复位可再次唤醒()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.NotifyWake();
            b.Push(Tone(0.2f, 300));
            b.Push(new float[250]);
            float[] s;
            Assert.IsTrue(b.TryTakeUtterance(out s));
            Assert.IsFalse(b.UtteranceReady);          // 复位
            Assert.IsFalse(b.TryTakeUtterance(out s));
            b.NotifyWake();                             // 第二次唤醒
            b.Push(Tone(0.2f, 100));
            b.Push(new float[250]);
            Assert.IsTrue(b.TryTakeUtterance(out s));
            Assert.AreEqual(450, s.Length);             // PreWake 100（上一段残留）+ 350
        }

        [Test]
        public void 环形滚掉旧数据后仍能取出()
        {
            // 容量 = (0.1+0.5+1)×1000 = 1600，先灌 2000 滚掉最旧的
            var b = new VoiceCaptureBuffer(1000, 0.1f, 0.5f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 2000));
            b.NotifyWake();
            b.Push(new float[250]);
            float[] s;
            Assert.IsTrue(b.TryTakeUtterance(out s));
            Assert.AreEqual(350, s.Length);   // PreWake 100 + 250，起点被环形容量钳住
        }

        [Test]
        public void 唤醒很早也能取到完整前段()
        {
            // 唤醒时累计样本还不足 PreWake：起点钳到 0，不许负索引
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.Push(Tone(0.2f, 50));
            b.NotifyWake();
            b.Push(new float[250]);
            float[] s;
            Assert.IsTrue(b.TryTakeUtterance(out s));
            Assert.AreEqual(300, s.Length);
        }

        [Test]
        public void 带计数的Push只取前count个()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            var big = new float[500];
            b.Push(big, 200);          // 静音（全 0）
            b.NotifyWake();
            b.Push(big, 100);          // 只取 100 个
            b.Push(new float[250]);
            float[] s;
            Assert.IsTrue(b.TryTakeUtterance(out s));
            Assert.AreEqual(450, s.Length);   // PreWake 100 + 100 + 250
        }

        [Test]
        public void Clear清空()
        {
            var b = new VoiceCaptureBuffer(1000, 0.1f, 1f, 0.2f, 0.05f);
            b.NotifyWake();
            b.Push(Tone(0.2f, 300));
            b.Push(new float[250]);
            b.Clear();
            float[] s;
            Assert.IsFalse(b.TryTakeUtterance(out s));
            Assert.IsFalse(b.UtteranceReady);
        }
    }
}
```

- [ ] **Step 2: 跑编译确认失败**

跑编译。Expected：`error CS` > 0（`VoiceCaptureBuffer` 未找到）。

- [ ] **Step 3: 写实现**

创建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/VoiceCaptureBuffer.cs`：

```csharp
using System;

namespace DouyinLive
{
    // 麦克风音频的环形缓冲 + 「说完没有」判定。主循环每帧把增量样本 Push 进来；
    // 唤醒（关键词命中或 VAD 起音）后开始观察静音，静音够久或录够长就 Ready，
    // Take 出 [唤醒前 PreWake 秒 + 唤醒后全部] 交给 ASR。纯数据结构，Core 可测。
    //
    // PreWake 存在的理由：唤醒词和命令常常一口气说完（「小美小美唱一首黄昏」），
    // 关键词识别器回调到我们开始关注音频时命令已经说了一半，没有这段回看就丢字。
    public class VoiceCaptureBuffer
    {
        public int SampleRate { get; }
        public float PreWakeSeconds { get; }
        public float MaxUtteranceSeconds { get; }
        public float SilenceSeconds { get; }
        public float RmsThreshold { get; }

        readonly float[] ring;
        int writePos;          // 下一个写位置（样本）
        long totalWritten;     // 累计写入样本数（含被环形滚掉的）
        bool armed;            // 唤醒之后 true
        long wakeSample;       // 唤醒时刻的累计样本序号
        long silenceSamples;   // 唤醒后连续静音样本数

        public bool Armed { get { return armed; } }

        public VoiceCaptureBuffer(int sampleRate, float preWakeSeconds, float maxUtteranceSeconds,
                                  float silenceSeconds, float rmsThreshold)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException("sampleRate");
            SampleRate = sampleRate;
            PreWakeSeconds = preWakeSeconds;
            MaxUtteranceSeconds = maxUtteranceSeconds;
            SilenceSeconds = silenceSeconds;
            RmsThreshold = rmsThreshold;
            // 余量 1 秒：PreWake 切片起点要落在环形里
            int capacity = (int)((preWakeSeconds + maxUtteranceSeconds + 1f) * sampleRate);
            ring = new float[Math.Max(capacity, sampleRate)];
        }

        // 唤醒后已录秒数（诊断用）
        public float ArmedSeconds
        {
            get { return armed ? (totalWritten - wakeSample) / (float)SampleRate : 0f; }
        }

        public void Push(float[] samples)
        {
            if (samples != null) Push(samples, samples.Length);
        }

        public void Push(float[] samples, int count)
        {
            if (samples == null || count <= 0) return;
            if (count > samples.Length) count = samples.Length;

            // 单次灌入超过环形容量的极端情况：只写最后一段，前面的反正要滚掉
            int from = 0;
            if (count > ring.Length) { from = count - ring.Length; count = ring.Length; }
            for (int i = from; i < from + count; i++)
            {
                ring[writePos] = samples[i];
                writePos++;
                if (writePos == ring.Length) writePos = 0;
            }
            long prevTotal = totalWritten;
            totalWritten += count;

            if (!armed) return;
            double sum = 0;
            for (int i = 0; i < count; i++) sum += (double)samples[i] * samples[i];
            float rms = (float)Math.Sqrt(sum / count);
            if (rms < RmsThreshold) silenceSamples += totalWritten - prevTotal;   // 整段按静音计
            else silenceSamples = 0;                                              // 出声就重来
        }

        public void NotifyWake()
        {
            if (armed) return;   // 已唤醒忽略（关键词连发）
            armed = true;
            wakeSample = totalWritten;
            silenceSamples = 0;
        }

        public bool UtteranceReady
        {
            get
            {
                if (!armed) return false;
                if (totalWritten - wakeSample >= (long)(MaxUtteranceSeconds * SampleRate)) return true;
                return silenceSamples >= (long)(SilenceSeconds * SampleRate);
            }
        }

        // 返回 [唤醒前 PreWake 秒 + 唤醒后全部]，取完复位回待唤醒。缓冲里的旧音频
        // 保留继续滚（下一次唤醒的 PreWake 还能用它）。
        public bool TryTakeUtterance(out float[] samples)
        {
            samples = null;
            if (!armed || !UtteranceReady) return false;

            long start = wakeSample - (long)(PreWakeSeconds * SampleRate);
            if (start < totalWritten - ring.Length) start = totalWritten - ring.Length;
            if (start < 0) start = 0;

            int count = (int)(totalWritten - start);
            samples = new float[count];
            for (int i = 0; i < count; i++)
                samples[i] = ring[(int)((start + i) % ring.Length)];
            Reset();
            return true;
        }

        public void Reset()
        {
            armed = false;
            silenceSamples = 0;
            wakeSample = 0;
        }

        public void Clear()
        {
            Reset();
            Array.Clear(ring, 0, ring.Length);
            writePos = 0;
            totalWritten = 0;
        }
    }
}
```

- [ ] **Step 4: 跑编译确认通过**

跑编译。Expected：两个 grep 都是 0。

- [ ] **Step 5: 跑测试确认通过**

跑测试，FILTER=`DouyinLive.Tests.VoiceCaptureBufferTests`。Expected：`total="10"`、`failed="0"`。

- [ ] **Step 6: Commit**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/VoiceCaptureBuffer.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/VoiceCaptureBuffer.cs.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/VoiceCaptureBufferTests.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/VoiceCaptureBufferTests.cs.meta"
git commit -m "feat(douyin-live): 麦克风环形缓冲与静音判定 VoiceCaptureBuffer，PreWake 回看防丢字

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 3: Core/WavEncoder —— float[] → 16bit PCM WAV

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/WavEncoder.cs`
- Test: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/WavEncoderTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `static byte[] WavEncoder.Encode(float[] samples, int sampleRate)`（Task 6 调用）。44 字节 RIFF 头 + 16bit 单声道 PCM；采样率原样写头（服务端自己重采样）。

- [ ] **Step 1: 写失败测试**

创建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/WavEncoderTests.cs`：

```csharp
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class WavEncoderTests
    {
        [Test]
        public void 头部标记和长度正确()
        {
            var wav = WavEncoder.Encode(new float[100], 16000);
            Assert.AreEqual(44 + 200, wav.Length);
            Assert.AreEqual('R', (char)wav[0]);
            Assert.AreEqual('I', (char)wav[1]);
            Assert.AreEqual('F', (char)wav[2]);
            Assert.AreEqual('F', (char)wav[3]);
            Assert.AreEqual('W', (char)wav[8]);
            Assert.AreEqual('f', (char)wav[12]);   // fmt 块
            Assert.AreEqual('d', (char)wav[36]);   // data 块
            Assert.AreEqual(36 + 200, wav[4] | (wav[5] << 8) | (wav[6] << 16) | (wav[7] << 24));
            Assert.AreEqual(16000, wav[24] | (wav[25] << 8) | (wav[26] << 16) | (wav[27] << 24));
            Assert.AreEqual(200, wav[40] | (wav[41] << 8) | (wav[42] << 16) | (wav[43] << 24));
        }

        [Test]
        public void 单声道16位参数正确()
        {
            var wav = WavEncoder.Encode(new float[10], 44100);
            Assert.AreEqual(1, wav[22] | (wav[23] << 8));        // 声道数
            Assert.AreEqual(1, wav[20] | (wav[21] << 8));        // PCM 格式
            Assert.AreEqual(44100, wav[28] | (wav[29] << 8) | (wav[30] << 16) | (wav[31] << 24)); // byte rate = 44100×2
            Assert.AreEqual(2, wav[32] | (wav[33] << 8));        // block align
            Assert.AreEqual(16, wav[34] | (wav[35] << 8));       // 位深
        }

        [Test]
        public void 半幅样本编码()
        {
            var wav = WavEncoder.Encode(new float[] { 0.5f }, 16000);
            short s = (short)(wav[44] | (wav[45] << 8));
            Assert.AreEqual(16384, s);   // 0.5 × 32767 = 16383.5，四舍五入到偶数
        }

        [Test]
        public void 越幅钳制()
        {
            var wav = WavEncoder.Encode(new float[] { 2f, -2f }, 16000);
            short s0 = (short)(wav[44] | (wav[45] << 8));
            short s1 = (short)(wav[46] | (wav[47] << 8));
            Assert.AreEqual(short.MaxValue, s0);
            Assert.AreEqual(-32767, s1);   // -1 × 32767，不是 short.MinValue
        }

        [Test]
        public void 空样本抛异常()
        {
            Assert.Throws<System.ArgumentNullException>(() => WavEncoder.Encode(null, 16000));
            Assert.Throws<System.ArgumentException>(() => WavEncoder.Encode(new float[0], 16000));
        }
    }
}
```

- [ ] **Step 2: 跑编译确认失败**

跑编译。Expected：`error CS` > 0（`WavEncoder` 未找到）。

- [ ] **Step 3: 写实现**

创建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/WavEncoder.cs`：

```csharp
using System;

namespace DouyinLive
{
    // float PCM → 16bit 单声道 WAV。服务端（whisper/SenseVoice）自己重采样，
    // 这边只把真实采样率写进头里，客户端不做任何重采样。
    public static class WavEncoder
    {
        public static byte[] Encode(float[] samples, int sampleRate)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (samples.Length == 0) throw new ArgumentException("没有样本", "samples");
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException("sampleRate");

            int dataBytes = samples.Length * 2;
            var wav = new byte[44 + dataBytes];

            wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
            WriteInt32(wav, 4, 36 + dataBytes);
            wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';
            wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
            WriteInt32(wav, 16, 16);              // fmt 块长
            WriteInt16(wav, 20, 1);               // PCM
            WriteInt16(wav, 22, 1);               // mono
            WriteInt32(wav, 24, sampleRate);
            WriteInt32(wav, 28, sampleRate * 2);  // byte rate = rate × block align
            WriteInt16(wav, 32, 2);               // block align
            WriteInt16(wav, 34, 16);              // bits per sample
            wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
            WriteInt32(wav, 40, dataBytes);

            int o = 44;
            for (int i = 0; i < samples.Length; i++)
            {
                float f = samples[i];
                if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
                short s = (short)Math.Round(f * 32767f);
                wav[o++] = (byte)(s & 0xFF);
                wav[o++] = (byte)((s >> 8) & 0xFF);
            }
            return wav;
        }

        static void WriteInt32(byte[] b, int at, int v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }

        static void WriteInt16(byte[] b, int at, short v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8);
        }
    }
}
```

- [ ] **Step 4: 跑编译确认通过**

跑编译。Expected：两个 grep 都是 0。

- [ ] **Step 5: 跑测试确认通过**

跑测试，FILTER=`DouyinLive.Tests.WavEncoderTests`。Expected：`total="5"`、`failed="0"`。

- [ ] **Step 6: Commit**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/WavEncoder.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/WavEncoder.cs.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/WavEncoderTests.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/WavEncoderTests.cs.meta"
git commit -m "feat(douyin-live): WAV 编码器 WavEncoder，float 采样转 16bit PCM 供云端 ASR

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 4: 语音事件注入点 —— DouyinEvent.FromVoice + InjectVoiceChat

**Files:**
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/DouyinEventTypes.cs`（`DouyinEvent` 类，在 `public DateTime ReceivedAt;` 之后加一个字段）
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs`（三处：`HandleChatLegacy` 分流、新增 `InjectVoiceChat`、新增两个字段）

**Interfaces:**
- Consumes: 现有 `Route(DouyinEvent)` / `HandleChatLegacy(DouyinEvent)` / `IsBlocked()` / `running`
- Produces（Task 6 的 listener 调用）: `public void DouyinLiveManager.InjectVoiceChat(string text)`；`DouyinEvent.FromVoice`（bool）。manager 的 `asrProvider` / `voiceListener` 字段分别由 Task 5 / Task 6 各自声明，本任务**不加任何字段**。

- [ ] **Step 1: Core 加 FromVoice 字段**

`Core/DouyinEventTypes.cs` 的 `DouyinEvent` 类，`public DateTime ReceivedAt;` 之后加：

```csharp
        public bool FromVoice;     // 语音助手注入的虚拟弹幕（UserId 固定 "voice"）
```

- [ ] **Step 2: HandleChatLegacy 分流**

`DouyinLiveManager.cs` 现有 `HandleChatLegacy`（约 370 行）整体替换为：

```csharp
        // 触发层、槽位、意图判定都没接住的弹幕走这里。抽成方法是因为意图判定
        // 是异步的：1.5 秒后判不出来，回调要能把这条弹幕补回原路径。
        void HandleChatLegacy(DouyinEvent ev)
        {
            if (reward.TryHandleDanmaku(ev)) return;
            // 语音闲聊不走「念弹幕」：主播自己的话被角色念回给主播没有意义，
            // 无论什么模式都走 AI 回复。
            if (ev.FromVoice) { danmakuAI.OnDanmaku(ev); return; }
            // 游戏陪播：AI 不掺和，带昵称念出来，主播听声人工回复。
            // 指令弹幕（触发规则/槽位/点歌等）在进到这里之前就已被消费。
            if (ChatMode == 1) reader.OnDanmaku(ev);
            else danmakuAI.OnDanmaku(ev);
        }
```

- [ ] **Step 3: 新增 InjectVoiceChat**

`DouyinLiveManager.cs`，放在 `HandleChatLegacy` 之后：

```csharp
        // ---------- 语音助手：麦克风命令伪装成弹幕进同一条链路 ----------

        // VoiceCommandListener 转写完成后的唯一入口。停播/菜单打开时静默丢弃——
        // 与弹幕「消费不响应」一致；在途 ASR 回调晚于停播到达也在这道闸拦住。
        public void InjectVoiceChat(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!running || IsBlocked()) return;
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
```

- [ ] **Step 4: 跑编译确认通过**

跑编译。Expected：两个 grep 都是 0。

- [ ] **Step 5: Commit**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/DouyinEventTypes.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs"
git commit -m "feat(douyin-live): 语音事件注入点 InjectVoiceChat，陪播模式下语音闲聊不走念弹幕

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 5: 云端 ASR Provider + 语音设置字段

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/CloudASRProvider.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs`（`douyinIdleSongList` 块之后、`// Cloud AI` 注释之前插入）
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs`（补回 `CloudASRProvider asrProvider;` 字段声明）

**Interfaces:**
- Consumes: 无（照 `OpenAICompatTTS` 的 HttpClient 惯例：`SendAsync(req, ct)`、`ReadAsStringAsync()` 无 ct 重载、`BaseUrl.Trim().TrimEnd('/') + "/audio/transcriptions"`、Bearer 头）
- Produces（Task 6 装配用）:
  - `class CloudASRProvider`：`string BaseUrl` / `string ApiKey` / `string Model` / `bool debugLog` / `bool IsAvailable { get; }` / `Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)`（失败一律返回 null，绝不抛）
  - `SaveLoadHandler.SettingsData` 9 个新字段：`douyinVoiceEnabled` / `douyinVoiceWakeWords` / `douyinVoiceAsrBaseUrl` / `douyinVoiceAsrApiKey` / `douyinVoiceAsrModel` / `douyinVoiceCommandWindow` / `douyinVoiceSilenceSeconds` / `douyinVoicePauseWhilePerforming` / `douyinVoiceNickname`

- [ ] **Step 1: 写 CloudASRProvider**

创建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/CloudASRProvider.cs`：

```csharp
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace DouyinLive
{
    // OpenAI 兼容 POST {baseUrl}/audio/transcriptions（OpenAI / SiliconFlow / Groq 等）。
    // 只在唤醒后调用，费用可忽略；任何失败都返回 null，调用方静默丢弃 ——
    // 语音助手是锦上添花的功能，绝不能因为转写失败打扰直播。
    public class CloudASRProvider
    {
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public string BaseUrl = "";
        public string ApiKey = "";
        public string Model = "FunAudioLLM/SenseVoiceSmall";
        public bool debugLog = false;

        public bool IsAvailable =>
            !string.IsNullOrWhiteSpace(BaseUrl) &&
            !string.IsNullOrWhiteSpace(ApiKey) &&
            !string.IsNullOrWhiteSpace(Model);

        // 返回转写文本；失败/超时/空文本一律 null
        public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
        {
            if (!IsAvailable || wav == null || wav.Length == 0) return null;
            try
            {
                using var form = new MultipartFormDataContent();
                var file = new ByteArrayContent(wav);
                file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(file, "file", "audio.wav");
                form.Add(new StringContent(Model), "model");
                form.Add(new StringContent("zh"), "language");

                using var req = new HttpRequestMessage(HttpMethod.Post,
                    BaseUrl.Trim().TrimEnd('/') + "/audio/transcriptions");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
                req.Content = form;

                using var resp = await http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    if (debugLog) Debug.LogWarning($"[VoiceASR] HTTP {(int)resp.StatusCode}: {Truncate(body)}");
                    return null;
                }
                var text = JsonConvert.DeserializeObject<AsrResponse>(body)?.text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (debugLog) Debug.LogWarning("[VoiceASR] 响应里没有 text: " + Truncate(body));
                    return null;
                }
                return text.Trim();
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                if (debugLog) Debug.LogWarning("[VoiceASR] 转写失败：" + ex.Message);
                return null;
            }
        }

        static string Truncate(string s)
        {
            s = s ?? "";
            return s.Length <= 300 ? s : s.Substring(0, 300);
        }

        class AsrResponse { public string text; }
    }
}
```

- [ ] **Step 2: SaveLoadHandler 加设置字段**

`SaveLoadHandler.cs`，`douyinIdleSongList = new() { … };` 块结束的 `};` 之后、`// Cloud AI (OpenAI 兼容)` 注释之前插入：

```csharp
        // ---------- 语音助手（麦克风控制角色，见 Docs/superpowers/specs/2026-10-18-douyin-voice-control-design.md） ----------
        public bool douyinVoiceEnabled = false;
        // 唤醒词列表：包含即命中；建议放角色人设的名字和叠字变体（「小美」能接住「小美小美」）
        public List<string> douyinVoiceWakeWords = new() { "小美小美", "小美" };
        // 云端 ASR（OpenAI 兼容 /v1/audio/transcriptions）；留空复用 aiBaseUrl/aiApiKey
        public string douyinVoiceAsrBaseUrl = "";
        public string douyinVoiceAsrApiKey = "";
        // SiliconFlow 用 FunAudioLLM/SenseVoiceSmall；OpenAI 官方用 whisper-1
        public string douyinVoiceAsrModel = "FunAudioLLM/SenseVoiceSmall";
        public float douyinVoiceCommandWindow = 8f;         // 唤醒后最长录音秒数（装配时钳 2~15）
        public float douyinVoiceSilenceSeconds = 1.2f;      // 静音多久算说完（装配时钳 0.5~3）
        public bool douyinVoicePauseWhilePerforming = true; // 角色出声时暂停监听，防自触发
        public string douyinVoiceNickname = "主播";          // 语音事件在观众记忆/上下文里的名字
```

- [ ] **Step 3: Manager 加字段声明**

`DouyinLiveManager.cs`，`EdgeTTSProvider edgeTTS;` 之后加一行：

```csharp
        CloudASRProvider asrProvider;
```

- [ ] **Step 4: 跑编译确认通过**

跑编译。Expected：两个 grep 都是 0。

- [ ] **Step 5: Commit**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/CloudASRProvider.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/CloudASRProvider.cs.meta" \
        "Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs"
git commit -m "feat(douyin-live): 云端 ASR Provider(OpenAI 兼容 audio/transcriptions)与语音设置字段

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 6: VoiceCommandListener + Manager 接线

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/VoiceCommandListener.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs`（四处：补 `voiceListener` 字段、`ApplySettings` 装配、`StartLive`/`StopLive` 各一行）

**Interfaces:**
- Consumes: Task 1 `VoiceCommandParser.ExtractCommand`、Task 2 `VoiceCaptureBuffer`、Task 3 `WavEncoder.Encode`、Task 4 `InjectVoiceChat`/`DouyinEvent.FromVoice`、Task 5 `CloudASRProvider` + 设置字段；现有 `SpeechPipeline.IsSpeaking`/`QueueCount`、`SongService.IsPlaying`、`MainThreadDispatcher.Post`
- Produces: `class VoiceCommandListener : MonoBehaviour`，方法 `Configure(SpeechPipeline, SongService, DouyinLiveManager, CloudASRProvider, List<string>, float commandWindow, float silenceSeconds, bool pauseWhilePerforming, bool debug)` 与 `ResetSession()`；启用由 `MonoBehaviour.enabled` 控制。

- [ ] **Step 1: 写 VoiceCommandListener**

创建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/VoiceCommandListener.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR
using UnityEngine.Windows.Speech;
#endif

namespace DouyinLive
{
    // 语音助手入口：麦克风常开 → 唤醒（本地关键词，系统不支持就降级音量检测）→
    // 录一句话（静音判定）→ 云端 ASR → 剥唤醒词 → InjectVoiceChat 伪装弹幕。
    // 只做采集和转写，不做任何意图判断；角色出声时暂停监听（防自触发）。
    //
    // 启停统一走 MonoBehaviour.enabled：ApplySettings 里 Configure + enabled 赋值。
    public class VoiceCommandListener : MonoBehaviour
    {
        public bool debugLog = false;

        public List<string> WakeWords = new List<string> { "小美小美", "小美" };
        public float CommandWindow = 8f;      // 唤醒后最长录音秒数
        public float SilenceSeconds = 1.2f;   // 静音多久算说完
        public bool PauseWhilePerforming = true;

        SpeechPipeline speech;      // 角色正在说话？
        SongService song;           // 角色正在唱歌？
        DouyinLiveManager manager;  // 命令上交
        CloudASRProvider asr;

        VoiceCaptureBuffer buffer;
        AudioClip micClip;
        string micDevice;
        int lastReadPos;            // 上次读到 micClip 的位置（环绕剪裁）
        float vadRms;               // 最近一次读入的 RMS（VAD 起音用）
        float vadLoudSince = -1f;   // 连续超阈值起点
        bool keywordMode;           // false = VAD 降级
        bool recognizing;           // 关键词识别器当前是否在跑
        bool asrInFlight;
        CancellationTokenSource cancel;

        const float VadStartSeconds = 0.3f;  // 连续这么久超阈值才算起音
        const float VadStartRms = 0.03f;     // 起音阈值比静音判定高一点，防底噪

#if UNITY_STANDALONE_WIN || UNITY_EDITOR
        KeywordRecognizer recognizer;
#endif

        public void Configure(SpeechPipeline speechPipeline, SongService songService,
                              DouyinLiveManager managerRef, CloudASRProvider asrProvider,
                              List<string> wakeWords, float commandWindow, float silenceSeconds,
                              bool pauseWhilePerforming, bool debug)
        {
            speech = speechPipeline;
            song = songService;
            manager = managerRef;
            asr = asrProvider;
            WakeWords = wakeWords != null ? new List<string>(wakeWords) : new List<string>();
            CommandWindow = Mathf.Clamp(commandWindow, 2f, 15f);
            SilenceSeconds = Mathf.Clamp(silenceSeconds, 0.5f, 3f);
            PauseWhilePerforming = pauseWhilePerforming;
            debugLog = debug;
            if (isActiveAndEnabled) { StopMic(); StartMic(); }   // 参数变了重启采集
        }

        // StartLive/StopLive 调用：丢弃在途转写与缓冲，别让上一场的话进这一场
        public void ResetSession()
        {
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
            if (WakeWords.Count == 0)
            {
                Debug.LogWarning("[Voice] 唤醒词列表为空，语音助手不启动");
                enabled = false;
                return;
            }
            if (Microphone.devices.Length == 0)
            {
                Debug.LogWarning("[Voice] 没有可用麦克风（检查 Windows 麦克风权限），语音助手不启动");
                enabled = false;
                return;
            }
            micDevice = Microphone.devices[0];
            // clip 长度必须盖住环形缓冲（PreWake + 窗口 + 余量），环绕读取才安全
            int clipSec = Mathf.CeilToInt(3f + CommandWindow + 1f);
            micClip = Microphone.Start(micDevice, true, clipSec, 16000);
            if (micClip == null)
            {
                Debug.LogWarning("[Voice] 麦克风打开失败，语音助手不启动");
                enabled = false;
                return;
            }
            int micRate = micClip.frequency;
            buffer = new VoiceCaptureBuffer(micRate, 3f, CommandWindow, SilenceSeconds, 0.015f);
            lastReadPos = Microphone.GetPosition(micDevice);
            cancel?.Cancel();
            cancel = new CancellationTokenSource();
            asrInFlight = false;
            vadLoudSince = -1f;
            TryStartKeyword();
            if (debugLog)
                Debug.Log($"[Voice] 已启动 {micDevice} @{micRate}Hz 模式={(keywordMode ? "关键词" : "音量检测")}");
        }

        void StopMic()
        {
            TryStopKeyword();
            if (!string.IsNullOrEmpty(micDevice) && Microphone.IsRecording(micDevice))
                Microphone.End(micDevice);
            micClip = null;
            buffer = null;
            cancel?.Cancel();
            cancel = null;
        }

        void TryStartKeyword()
        {
            keywordMode = false;
            recognizing = false;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR
            try
            {
                if (!PhraseRecognitionSystem.isSupported)
                {
                    Debug.Log("[Voice] 系统不支持语音识别（多半没装中文语音包），降级音量检测模式");
                    return;
                }
                recognizer = new KeywordRecognizer(WakeWords.ToArray(), ConfidenceLevel.Medium);
                recognizer.OnPhraseRecognized += OnPhraseRecognized;
                recognizer.Start();
                keywordMode = true;
                recognizing = true;
            }
            catch (Exception ex)
            {
                Debug.Log("[Voice] 关键词唤醒启动失败，降级音量检测模式：" + ex.Message);
                if (recognizer != null) { recognizer.Dispose(); recognizer = null; }
                keywordMode = false;
            }
#endif
        }

        void TryStopKeyword()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR
            if (recognizer != null)
            {
                recognizer.OnPhraseRecognized -= OnPhraseRecognized;
                try { recognizer.Stop(); } catch { /* 已停就随它去 */ }
                recognizer.Dispose();
                recognizer = null;
            }
            recognizing = false;
#endif
        }

        // 角色出声期间把识别器停掉（它还在监听麦克风的话会把角色自己的话当输入）
        void SetRecognitionActive(bool on)
        {
            if (!keywordMode || recognizing == on) return;
            recognizing = on;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR
            try { if (on) recognizer?.Start(); else recognizer?.Stop(); }
            catch (Exception ex) { if (debugLog) Debug.Log("[Voice] 切换识别状态失败: " + ex.Message); }
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR
        void OnPhraseRecognized(PhraseRecognizedEventArgs args)
        {
            if (this == null || !enabled) return;
            if (asrInFlight || buffer == null) return;
            buffer.NotifyWake();
            if (debugLog) Debug.Log($"[Voice] 唤醒: {args.text} ({args.confidence})");
        }
#endif

        bool IsPerforming()
        {
            return (speech != null && (speech.IsSpeaking || speech.QueueCount > 0)) ||
                   (song != null && song.IsPlaying);
        }

        void Update()
        {
            if (asr == null || manager == null || buffer == null) return;   // 未装配/麦克风没起来

            // 防自触发闭环：角色出声期间停识别、清缓冲、丢积压，说完自然恢复
            if (PauseWhilePerforming && IsPerforming())
            {
                SetRecognitionActive(false);
                buffer.Clear();
                lastReadPos = Microphone.GetPosition(micDevice);
                vadRms = 0f;
                vadLoudSince = -1f;
                return;
            }
            SetRecognitionActive(true);

            ReadMicIntoBuffer();

            // VAD 降级模式：连续 0.3 秒超阈值当作起音（关键词模式唤醒交给识别器）
            if (!keywordMode && !asrInFlight)
            {
                if (vadRms >= VadStartRms)
                {
                    if (vadLoudSince < 0f) vadLoudSince = Time.unscaledTime;
                }
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

        void ReadMicIntoBuffer()
        {
            if (micClip == null || string.IsNullOrEmpty(micDevice)) return;
            int pos = Microphone.GetPosition(micDevice);
            int total = micClip.samples;
            int available = pos >= lastReadPos ? pos - lastReadPos : total - lastReadPos + pos;
            if (available <= 0) { vadRms = 0f; return; }
            if (available > total) available = total;

            var chunk = new float[available];
            int first = Mathf.Min(available, total - lastReadPos);
            if (first == available)
            {
                micClip.GetData(chunk, lastReadPos);
            }
            else
            {
                // 跨环绕点分两段读
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
            vadRms = (float)Math.Sqrt(sum / available);
            buffer.Push(chunk);
        }

        void SubmitToAsr(float[] samples)
        {
            byte[] wav;
            try { wav = WavEncoder.Encode(samples, buffer.SampleRate); }
            catch (Exception ex) { if (debugLog) Debug.Log("[Voice] WAV 编码失败: " + ex.Message); return; }

            asrInFlight = true;   // 在飞期间忽略新唤醒，防连发重入
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
            asrInFlight = false;
            if (string.IsNullOrEmpty(text))
            {
                if (debugLog) Debug.Log("[Voice] ASR 无结果");
                return;
            }
            // 关键词模式信任识别器（文本里找不到唤醒词也放行）；VAD 模式必须有
            string command = VoiceCommandParser.ExtractCommand(text, WakeWords, !keywordMode);
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

- [ ] **Step 2: Manager 接线（三处）**

`DouyinLiveManager.cs`：

2a. 字段区，`CloudASRProvider asrProvider;` 之后加：

```csharp
        VoiceCommandListener voiceListener;
```

2b. `ApplySettings()` 里，`// 连接` 注释（`bool shouldRun = d.enableDouyinLive;` 之前）插入：

```csharp
            // ---------- 语音助手：麦克风唤醒 → 云端 ASR → 伪装弹幕进 Route() ----------
            asrProvider ??= new CloudASRProvider();
            asrProvider.BaseUrl = string.IsNullOrWhiteSpace(d.douyinVoiceAsrBaseUrl) ? d.aiBaseUrl : d.douyinVoiceAsrBaseUrl;
            asrProvider.ApiKey = string.IsNullOrWhiteSpace(d.douyinVoiceAsrApiKey) ? d.aiApiKey : d.douyinVoiceAsrApiKey;
            asrProvider.Model = string.IsNullOrWhiteSpace(d.douyinVoiceAsrModel) ? "FunAudioLLM/SenseVoiceSmall" : d.douyinVoiceAsrModel;
            asrProvider.debugLog = debugLog;

            if (voiceListener == null) voiceListener = gameObject.AddComponent<VoiceCommandListener>();
            voiceListener.Configure(speech, songService, this, asrProvider,
                d.douyinVoiceWakeWords, d.douyinVoiceCommandWindow, d.douyinVoiceSilenceSeconds,
                d.douyinVoicePauseWhilePerforming, debugLog);
            bool wantVoice = d.enableDouyinLive && d.douyinVoiceEnabled;
            voiceListener.enabled = wantVoice && asrProvider.IsAvailable;
```

2c. `StartLive()` 里 `reader.ResetSession();` 之后加一行：

```csharp
            voiceListener?.ResetSession();
```

`StopLive()` 里 `intents.Reset();` 之后加一行：

```csharp
            voiceListener?.ResetSession();   // 丢弃在途转写，停播后语音不动作
```

- [ ] **Step 3: 跑编译确认通过**

跑编译。Expected：两个 grep 都是 0。

- [ ] **Step 4: 跑全量 EditMode 测试**

跑测试（不带 `-testFilter`，跑全量）。Expected：现有全部测试 + 本计划 30 个新用例，`failed="0"`。

- [ ] **Step 5: Commit**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/VoiceCommandListener.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/VoiceCommandListener.cs.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs"
git commit -m "feat(douyin-live): 语音助手监听器 VoiceCommandListener，唤醒→录音→ASR→注入链路接线

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 7: 文档更新 + 全量回归 + 手工验证交接

**Files:**
- Modify: `Docs/DouyinLive-Integration.md`

**Interfaces:**
- Consumes: Task 1~6 的全部产出（文档如实描述）
- Produces: 用户可照着开关和排障的文档；手工验证清单交给主播执行。

- [ ] **Step 1: 更新 Docs/DouyinLive-Integration.md**

七处修改：

1a. 「功能总览」表格加一行（放「快捷反应」行之后）：

```markdown
| 语音助手 | 麦克风说唤醒词 | 「小美小美 唱一首黄昏」→ 开口应答+唱跳；闲聊提问 AI 回答；唱歌/TTS 期间自动暂停监听；需 `douyinVoiceEnabled` |
```

1b. 「场景接线」一节末尾加一条：

```markdown
3. **语音助手**（可选）：`VoiceCommandListener` 由 Manager 自动挂载，无需手动接线；需要麦克风权限。
```

1c. 「运行前提」加一条：

```markdown
- 语音助手：「关键词唤醒」模式要求系统装了中文语音包，没有会自动降级为音量检测模式
  （每句话都送云端识别，延迟多 ~1 秒）；ASR 模型名按服务商填（SiliconFlow `FunAudioLLM/SenseVoiceSmall`，OpenAI 官方 `whisper-1`）。
```

1d. 「settings.json 配置」代码块 `douyinLivePrompt` 行之后加：

```jsonc
// 语音助手（麦克风控制角色）
"douyinVoiceEnabled": false,
"douyinVoiceWakeWords": ["小美小美", "小美"],   // 建议改成角色人设的名字
"douyinVoiceAsrBaseUrl": "",       // 空 = 复用 aiBaseUrl
"douyinVoiceAsrApiKey": "",        // 空 = 复用 aiApiKey
"douyinVoiceAsrModel": "FunAudioLLM/SenseVoiceSmall",
"douyinVoiceCommandWindow": 8.0,   // 唤醒后最长录音秒数
"douyinVoiceSilenceSeconds": 1.2,  // 静音多久算说完
"douyinVoicePauseWhilePerforming": true,
"douyinVoiceNickname": "主播",
```

1e. 「关键实现机制」一节加一条 bullet：

```markdown
- **语音助手**：麦克风常开环形缓冲（回看 3 秒），本地 KeywordRecognizer 命中唤醒词
  （系统无中文语音包自动降级音量检测）后录到静音 1.2 秒，WAV 送云端 ASR，文本剥唤醒词
  后伪装成 `UserId="voice"` 的弹幕进 Route()——触发规则/追问槽位/LLM 意图/点歌换角色全部复用；
  非命令内容走 AI 闲聊回复（陪播模式也不念出来）。角色出声期间暂停监听防自触发。
```

1f. 「已知边界」加三条：

```markdown
- 语音助手：ASR 同音字转错歌名等同观众打错字，属正常容错；VAD 降级模式下直播外放声也会被
  送识别（有防自触发暂停兜底，口误提到唤醒词仍可能误触发）；唤醒词嵌在句子中间会被整段剥掉
  （正常唤醒词在句首，不踩）。
```

1g. 文档末尾加「语音助手自测步骤」一节：

```markdown
## 语音助手自测步骤（需真机麦克风）

1. settings.json 开 `douyinVoiceEnabled`，确认 ASR 配置可用（复用 AI 的 key 时 aiBaseUrl 服务商必须有转写接口）
2. 说「小美小美 唱一首黄昏」→ 她开口应答并开唱+跟舞
3. 说「小美小美 今天直播什么好」→ AI 开口聊
4. 她正在说话/唱歌时喊唤醒词 → 不响应；说完恢复
5. F9 切游戏陪播后再试 2/3 → 命令照常执行、闲聊走 AI（不被念出来）
6. 不说话/放游戏声 → 无动作（开 debugLog 看无 ASR 请求）
7. 连续说话 15 秒以上再唤醒 → 仍能识别完整命令（环形缓冲跨环绕点）
```

- [ ] **Step 2: 跑编译 + 全量测试（最终回归）**

跑编译，然后跑全量 EditMode 测试。Expected：两个 grep 都是 0；`failed="0"`，`total` ≥ 现有用例数 + 30。

- [ ] **Step 3: Commit**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Docs/DouyinLive-Integration.md"
git commit -m "docs(douyin-live): 语音助手配置说明、机制与自测步骤

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

- [ ] **Step 4: 手工验证交接（用户执行，代码任务到此完成）**

把下面清单发给用户，逐条确认后本功能才算验收（只有主播能跑，需要真机麦克风和直播环境）：

1. settings.json 开 `douyinVoiceEnabled` → 说「小美小美 唱一首黄昏」→ 她开口接话并开唱黄昏+跟舞
2. 说「小美小美 换角色」→ 换角色（30 秒冷却内第二次被拦属正常）
3. 说「小美小美 今天直播什么好」→ AI 开口聊
4. 她问「想听什么歌呀」→ 口头答「小美小美 黄昏」→ 开唱（补槽链路）
5. 她正在说话/唱歌时喊唤醒词 → 不响应；唱完恢复监听
6. F9 切游戏陪播后：说命令 → 照常执行；说闲聊 → AI 回复（不被「念弹幕」念出来）
7. 不说话/放游戏声/刷视频 → 无动作、无 ASR 请求（debugLog 观察）
8. `douyinVoiceEnabled=false` 或删掉字段 → 完全回到现状
9. （无中文语音包的机器）自动降级 VAD 模式仍可用，日志有降级提示
10. 菜单打开时说话 → 无动作，关菜单后正常
11. 连续说话 15 秒以上再唤醒 → 仍能识别完整命令

若第 1 步出现拾音异常/识别不到，先试「关掉关键词唤醒」（`douyinVoiceWakeWords` 留空数组 `[]` 会整体禁用；若只想降级，把列表留一个词并观察日志的降级提示—— KeywordRecognizer 与 Microphone 抢设备时按日志提示排查）。

---

## Self-Review 记录

- **Spec 覆盖**：spec 组件五件（Parser/Buffer/WavEncoder/ASRProvider/Listener）→ Task 1/2/3/5/6；`FromVoice`+注入点+陪播分流 → Task 4；配置九字段 → Task 5；StartLive/StopLive ResetSession → Task 6；防自触发/失败静默/限流对称 → Task 6 代码与 Task 4 守卫；文档 → Task 7；手工验证 10 条 → Task 7（补第 11 条环绕点）。
- **占位符扫描**：无 TBD/TODO；所有代码块完整可编译。
- **类型一致性**：`ExtractCommand(string, IReadOnlyList<string>, bool)`（T1 定义 = T6 调用，`List<string>` 隐式转 IReadOnlyList）；`VoiceCaptureBuffer` 五参构造 + `Push` 两重载（T2 = T6）；`WavEncoder.Encode(float[], int)`（T3 = T6）；`CloudASRProvider` 字段与 `TranscribeAsync(byte[], CancellationToken)`（T5 = T6）；`Configure` 九参签名（T6 定义 = T6 接线调用一致）；`InjectVoiceChat(string)`（T4 = T6）。
- **Review Focus**：五条均已钉（见 Review Focus 节各条标注的归属任务）。
