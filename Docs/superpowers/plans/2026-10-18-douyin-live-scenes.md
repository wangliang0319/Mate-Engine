# 直播场景统一（三场景预设 + 安静状态 + 主播控制指令）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用三个场景预设（AI 独播 / 游戏陪播 / 搭档主持）统一弹幕处理、冷场暖场、自动唱跳的行为，F9 与语音可切换；新增「安静」状态与主播专属语音控制指令。

**Architecture:** 场景规则集中在 Core 纯函数 `LiveSceneResolver`（输入总开关 + 场景 + 是否安静，输出生效行为），主播指令识别在 Core 纯函数 `OperatorCommandParser`，两者都有单元测试。`DouyinLiveManager` 只负责把解析结果下发给各服务、在 `Route` 里按结果放行或拦截、在语音入口最先识别控制指令。安静期间欢迎/点赞答谢/报时等服务照常记账，只在 `SpeechPipeline` 按优先级吞掉它们的语音。

**Tech Stack:** Unity 6000.2.6f2（Mono）/ C# netstandard2.1 / NUnit EditMode。

**Spec:** [Docs/superpowers/specs/2026-10-18-douyin-live-scenes-design.md](../specs/2026-10-18-douyin-live-scenes-design.md)

## Global Constraints

- 从 main 开功能分支 `feat/douyin-live-scenes`，不直接提交 main。
- Core 程序集 `MateEngine.DouyinLive.Core`：`references` 必须保持为空——Core 代码禁用 UnityEngine / Newtonsoft / Debug.Log / Time.* / Mathf.*（用 `System.Math`）。
- Tests 程序集 `MateEngine.DouyinLive.Tests`：`overrideReferences: true`，不加引用。
- 场景编号：`0`=AI 独播（`LiveScene.Solo`）、`1`=游戏陪播（`LiveScene.Game`）、`2`=搭档主持（`LiveScene.Duo`）。与旧 `douyinChatMode` 的 0/1 含义一致。
- 搭档主持的暖场间隔下限 180 秒；安静默认时长 `douyinQuietMinutes = 5`。
- 安静期间：只保留礼物语音答谢与主播语音的全部响应；低优先级播报 = `Priority.Welcome`、`Priority.LikeThanks`、`Priority.Milestone`。
- 主播指令确认语一律 `SpeechPipeline.Priority.AIReply` 入队（原 F9 确认语的 `Milestone` 改为 `AIReply`）。
- 指令识别顺序：控制指令 → 开头词传话 → 原有管线。只在 `InjectVoiceChat`（主播语音）里识别，观众弹幕不识别。
- `douyinChatMode` 废弃：只在 `douyinScene` 缺失（-1）时迁移一次。
- 提交信息中文、`feat(douyin-live):` / `docs(douyin-live):` 风格，结尾 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。
- 只 `git add` 任务列出的文件（含 Unity 生成的对应 `.meta`）。工作区里与本计划无关的变更（主题材质颜色漂移、UserSettings、场景文件等 Unity 运行时副产物）一律不碰。
- 注释中文、解释「为什么」，与 DouyinLive 现有代码风格一致。

### 验证命令（所有任务共用）

**Unity 编辑器必须关着**（开着时 batchmode 拿不到项目锁，rc=21，`error CS` 的 grep 会假绿）：

```bash
tasklist //FI "IMAGENAME eq Unity.exe" | grep -i Unity.exe || echo NO_EDITOR
```

有输出就停下来请用户关闭编辑器。

编译：

```bash
"/c/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe" -batchmode -quit -projectPath "E:/Work/AI/Mate-Engine" >/dev/null 2>&1; echo "rc=$?"
L="C:/Users/83529/AppData/Local/Unity/Editor/Editor.log"
echo "errorCS=$(grep -c 'error CS' "$L") anotherInstance=$(grep -c 'another Unity instance' "$L")"
grep "error CS" "$L" | sort -u | head -10
```

Expected：`rc=0 errorCS=0 anotherInstance=0`。

EditMode 测试（**跑前必删**旧结果文件）：

```bash
R="C:/Users/83529/AppData/LocalLow/Shinymoon/MateEngineX/TestResults.xml"; rm -f "$R"
"/c/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe" -batchmode -runTests -projectPath "E:/Work/AI/Mate-Engine" -testPlatform EditMode -assemblyNames MateEngine.DouyinLive.Tests -testFilter "<过滤器>" >/dev/null 2>&1; echo "rc=$?"
grep -o '<test-run[^>]*' "$R" | grep -o 'result="[^"]*"\|total="[0-9]*"\|passed="[0-9]*"\|failed="[0-9]*"'
grep -o '<test-case [^>]*result="Failed"' "$R" | grep -o 'fullname="[^"]*"' | head
```

全量去掉 `-testFilter`。`rc=198` = 许可证过期，请用户打开一次 Unity Hub。编译失败时没有结果文件（Unity 下的 RED），去 Editor.log 看 `error CS`。

## Review Focus

自动化测试覆盖不到、最可能咬到主播的五类情况，审校逐条核对：

1. **安静期间主播语音必须照常工作**：`Route` 的安静拦截要放过 `ev.FromVoice`；安静不能关 `danmakuAI.Enabled`（主播和她聊天走的就是它）；她回答主播用 `AIReply` 优先级，不在低优先级静音范围。Task 4 归属。
2. **退出安静要恢复干净**：到期、说「可以说话了」、停播三条路径都要关掉低优先级静音并按当前场景恢复暖场/自动唱跳；停播时处于安静不能把静音留到下次开播。Task 4 归属。
3. **安静期间记账不丢**：观众记忆、聊天上下文、礼物记录在拦截之前完成（`Route` 开头已有）；进房/关注仍要调用 `welcome.OnEvent` 让它记录来访（欢迎语由静音吞掉）。Task 4 归属。
4. **场景持久化与迁移**：F9/语音切场景写入 `douyinScene` 并存盘，重启后沿用；老配置只有 `douyinChatMode: 1` 时启动后是游戏陪播；`douyinScene` 越界时日志警告并按独播。Task 1（迁移函数）、Task 4（接线）归属；手工验收覆盖。
5. **前缀匹配吞话**：「别唱了，给我讲个故事」会被当成停止表演、后半句丢弃；「恢复」「停一下」这类短说法开头的聊天同理。这是与传话开头词一致的取舍，审校确认只在主播语音生效、观众同样文字的弹幕按普通弹幕处理。Task 2、Task 4 归属。

---

### Task 1: Core 场景解析 LiveSceneResolver + 迁移 LiveSceneMigration

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/LiveScene.cs`
- Test: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/LiveSceneTests.cs`

**Interfaces:**
- Consumes: 无
- Produces（Task 2、Task 4 使用）：
  - `enum LiveScene { Solo = 0, Game = 1, Duo = 2 }`
  - `enum DanmakuHandling { AiVerbose, AiBrief, Read, Ignore }`
  - `struct LiveSwitches { bool AiReply; bool IdleChatter; float IdleThreshold; bool AutoSong; bool AutoDance; }`
  - `struct LiveBehavior { DanmakuHandling Danmaku; bool IdleChatter; float IdleThreshold; bool AutoSong; bool AutoDance; bool RespondToAudience; bool MuteLowPriority; }`
  - `static class LiveSceneResolver { const float DuoMinIdleThreshold = 180f; LiveBehavior Resolve(LiveScene scene, bool quiet, LiveSwitches s); }`
  - `static class LiveSceneMigration { LiveScene Resolve(int storedScene, int legacyChatMode, out bool outOfRange); }`

- [ ] **Step 1: 开功能分支**

```bash
cd "E:/Work/AI/Mate-Engine" && git checkout -b feat/douyin-live-scenes && git branch --show-current
```

Expected：`feat/douyin-live-scenes`

- [ ] **Step 2: 写失败测试**

创建 `Tests/LiveSceneTests.cs`：

```csharp
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class LiveSceneTests
    {
        static LiveSwitches AllOn(float idle = 90f) => new LiveSwitches
        {
            AiReply = true, IdleChatter = true, IdleThreshold = idle, AutoSong = true, AutoDance = true
        };

        // ---------- 场景行为表 ----------

        [Test]
        public void 独播_AI详细回复_暖场与自动唱跳开()
        {
            var b = LiveSceneResolver.Resolve(LiveScene.Solo, false, AllOn());
            Assert.AreEqual(DanmakuHandling.AiVerbose, b.Danmaku);
            Assert.IsTrue(b.IdleChatter);
            Assert.AreEqual(90f, b.IdleThreshold);
            Assert.IsTrue(b.AutoSong);
            Assert.IsTrue(b.AutoDance);
        }

        [Test]
        public void 陪播_念弹幕_不暖场不自动唱跳()
        {
            var b = LiveSceneResolver.Resolve(LiveScene.Game, false, AllOn());
            Assert.AreEqual(DanmakuHandling.Read, b.Danmaku);
            Assert.IsFalse(b.IdleChatter);
            Assert.IsFalse(b.AutoSong);
            Assert.IsFalse(b.AutoDance);
        }

        [Test]
        public void 搭档_AI简短回复_暖场间隔至少180_不自动唱跳()
        {
            var b = LiveSceneResolver.Resolve(LiveScene.Duo, false, AllOn(90f));
            Assert.AreEqual(DanmakuHandling.AiBrief, b.Danmaku);
            Assert.IsTrue(b.IdleChatter);
            Assert.AreEqual(180f, b.IdleThreshold);
            Assert.IsFalse(b.AutoSong);
            Assert.IsFalse(b.AutoDance);
        }

        [Test]
        public void 搭档_总间隔大于180时用总间隔()
        {
            Assert.AreEqual(300f, LiveSceneResolver.Resolve(LiveScene.Duo, false, AllOn(300f)).IdleThreshold);
        }

        [Test]
        public void 非安静_响应观众且不静音()
        {
            foreach (var s in new[] { LiveScene.Solo, LiveScene.Game, LiveScene.Duo })
            {
                var b = LiveSceneResolver.Resolve(s, false, AllOn());
                Assert.IsTrue(b.RespondToAudience, s.ToString());
                Assert.IsFalse(b.MuteLowPriority, s.ToString());
            }
        }

        // ---------- 总开关约束 ----------

        [Test]
        public void AI回复总开关关_独播与搭档不处理弹幕()
        {
            var s = AllOn(); s.AiReply = false;
            Assert.AreEqual(DanmakuHandling.Ignore, LiveSceneResolver.Resolve(LiveScene.Solo, false, s).Danmaku);
            Assert.AreEqual(DanmakuHandling.Ignore, LiveSceneResolver.Resolve(LiveScene.Duo, false, s).Danmaku);
        }

        [Test]
        public void AI回复总开关关_陪播照常念弹幕()
        {
            // 念弹幕不经过大模型，和 AI 回复总开关无关（与旧 douyinChatMode=1 行为一致）
            var s = AllOn(); s.AiReply = false;
            Assert.AreEqual(DanmakuHandling.Read, LiveSceneResolver.Resolve(LiveScene.Game, false, s).Danmaku);
        }

        [Test]
        public void 暖场总开关关_任何场景都不暖场()
        {
            var s = AllOn(); s.IdleChatter = false;
            foreach (var sc in new[] { LiveScene.Solo, LiveScene.Game, LiveScene.Duo })
                Assert.IsFalse(LiveSceneResolver.Resolve(sc, false, s).IdleChatter, sc.ToString());
        }

        [Test]
        public void 自动唱歌总开关关_独播也不自动唱()
        {
            var s = AllOn(); s.AutoSong = false;
            var b = LiveSceneResolver.Resolve(LiveScene.Solo, false, s);
            Assert.IsFalse(b.AutoSong);
            Assert.IsTrue(b.AutoDance);
        }

        // ---------- 安静 ----------

        [Test]
        public void 安静_只剩礼物答谢与主播响应()
        {
            foreach (var sc in new[] { LiveScene.Solo, LiveScene.Game, LiveScene.Duo })
            {
                var b = LiveSceneResolver.Resolve(sc, true, AllOn());
                Assert.AreEqual(DanmakuHandling.Ignore, b.Danmaku, sc.ToString());
                Assert.IsFalse(b.IdleChatter, sc.ToString());
                Assert.IsFalse(b.AutoSong, sc.ToString());
                Assert.IsFalse(b.AutoDance, sc.ToString());
                Assert.IsFalse(b.RespondToAudience, sc.ToString());
                Assert.IsTrue(b.MuteLowPriority, sc.ToString());
            }
        }

        // ---------- 迁移 ----------

        [Test]
        public void 迁移_已有场景值原样()
        {
            Assert.AreEqual(LiveScene.Duo, LiveSceneMigration.Resolve(2, 0, out bool oor));
            Assert.IsFalse(oor);
        }

        [Test]
        public void 迁移_缺失时旧模式1为陪播()
        {
            Assert.AreEqual(LiveScene.Game, LiveSceneMigration.Resolve(-1, 1, out _));
        }

        [Test]
        public void 迁移_缺失时旧模式0为独播()
        {
            Assert.AreEqual(LiveScene.Solo, LiveSceneMigration.Resolve(-1, 0, out _));
        }

        [Test]
        public void 迁移_缺失时旧模式异常值为独播()
        {
            Assert.AreEqual(LiveScene.Solo, LiveSceneMigration.Resolve(-1, 7, out _));
        }

        [Test]
        public void 迁移_越界回退独播并报告()
        {
            Assert.AreEqual(LiveScene.Solo, LiveSceneMigration.Resolve(5, 1, out bool oor));
            Assert.IsTrue(oor);
        }
    }
}
```

- [ ] **Step 3: 跑测试确认失败（RED）**

EditMode 测试，`-testFilter "DouyinLive.Tests.LiveSceneTests"`。
Expected：`rc≠0`、无结果 XML；Editor.log 有 `error CS0246`（`LiveSwitches` / `LiveScene` 等不存在）。

- [ ] **Step 4: 写实现**

创建 `Core/LiveScene.cs`：

```csharp
using System;

namespace DouyinLive
{
    // 直播场景。编号与旧 douyinChatMode 的 0/1 含义一致，老配置可直接迁移。
    public enum LiveScene { Solo = 0, Game = 1, Duo = 2 }

    public enum DanmakuHandling
    {
        AiVerbose,   // AI 详细回复并引导观众继续聊（独播）
        AiBrief,     // AI 简短回复，把话头留给主播（搭档）
        Read,        // 带昵称念出来，主播人工回复（陪播）
        Ignore       // 不处理（AI 回复总开关关闭，或安静中）
    }

    // 现有的总开关：场景只能在它们允许的范围内收紧，不能打开总开关关掉的东西
    public struct LiveSwitches
    {
        public bool AiReply;
        public bool IdleChatter;
        public float IdleThreshold;
        public bool AutoSong;
        public bool AutoDance;
    }

    // 此刻实际生效的行为，DouyinLiveManager 据此下发给各服务
    public struct LiveBehavior
    {
        public DanmakuHandling Danmaku;
        public bool IdleChatter;
        public float IdleThreshold;
        public bool AutoSong;
        public bool AutoDance;
        public bool RespondToAudience;   // false = 观众侧只记账不响应（礼物只说答谢语）
        public bool MuteLowPriority;     // true = 欢迎/点赞答谢/报时等低优先级播报不开口
    }

    // 场景规则集中在这里：以前散在 douyinChatMode、暖场、自动唱跳几个互不相干的开关里，
    // 切到游戏陪播后暖场和自动唱歌照常触发。纯函数，场景表每一格都有单元测试。
    public static class LiveSceneResolver
    {
        public const float DuoMinIdleThreshold = 180f;   // 搭档主持时主播在说话，暖场别太勤

        public static LiveBehavior Resolve(LiveScene scene, bool quiet, LiveSwitches s)
        {
            var b = new LiveBehavior { IdleThreshold = s.IdleThreshold };
            if (quiet)
            {
                // 安静：只保留礼物答谢与主播语音响应（后两者不经过这里）
                b.Danmaku = DanmakuHandling.Ignore;
                b.MuteLowPriority = true;
                return b;
            }

            switch (scene)
            {
                case LiveScene.Game:
                    // 念弹幕不经过大模型，和 AI 回复总开关无关；暖场和自动唱跳会打断打游戏
                    b.Danmaku = DanmakuHandling.Read;
                    break;
                case LiveScene.Duo:
                    b.Danmaku = s.AiReply ? DanmakuHandling.AiBrief : DanmakuHandling.Ignore;
                    b.IdleChatter = s.IdleChatter;
                    b.IdleThreshold = Math.Max(s.IdleThreshold, DuoMinIdleThreshold);
                    break;   // 什么时候表演由主播决定，不自动唱跳
                default:
                    b.Danmaku = s.AiReply ? DanmakuHandling.AiVerbose : DanmakuHandling.Ignore;
                    b.IdleChatter = s.IdleChatter;
                    b.AutoSong = s.AutoSong;
                    b.AutoDance = s.AutoDance;
                    break;
            }
            b.RespondToAudience = true;
            return b;
        }
    }

    public static class LiveSceneMigration
    {
        // storedScene < 0 表示配置里还没有 douyinScene：从旧 douyinChatMode 推导（1=陪播，其余=独播），
        // 主播原来用的模式原样延续。越界值回退独播，outOfRange 让调用方写警告日志。
        public static LiveScene Resolve(int storedScene, int legacyChatMode, out bool outOfRange)
        {
            outOfRange = false;
            if (storedScene < 0) return legacyChatMode == 1 ? LiveScene.Game : LiveScene.Solo;
            if (storedScene > 2) { outOfRange = true; return LiveScene.Solo; }
            return (LiveScene)storedScene;
        }
    }
}
```

- [ ] **Step 5: 跑测试确认通过（GREEN）**

同 Step 3 命令。Expected：`result="Passed" total="15" passed="15" failed="0"`。

- [ ] **Step 6: 全量回归**

Expected：`total="248" passed="248" failed="0"`（233 + 15）。

- [ ] **Step 7: 提交**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/LiveScene.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/LiveScene.cs.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/LiveSceneTests.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/LiveSceneTests.cs.meta"
git commit -m "feat(douyin-live): 直播场景解析 LiveSceneResolver 与配置迁移 LiveSceneMigration

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Core 主播控制指令识别 OperatorCommandParser

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/OperatorCommandParser.cs`
- Test: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/OperatorCommandParserTests.cs`

**Interfaces:**
- Consumes: `LiveScene`（Task 1）
- Produces（Task 4 使用）：
  - `enum OperatorCommandKind { None, SwitchScene, UnknownScene, StopPerformance, ClearQueue, Quiet, Resume }`
  - `sealed class OperatorCommandWords`：`List<string> SwitchPrefixes, SceneGame, SceneDuo, SceneSolo, Stop, ClearQueue, Quiet, Resume`（字段初始化为默认说法）
  - `static class OperatorCommandParser { bool TryParse(string command, OperatorCommandWords words, out OperatorCommandKind kind, out LiveScene scene); }`

- [ ] **Step 1: 写失败测试**

创建 `Tests/OperatorCommandParserTests.cs`：

```csharp
using NUnit.Framework;
using System.Collections.Generic;

namespace DouyinLive.Tests
{
    public class OperatorCommandParserTests
    {
        static readonly OperatorCommandWords W = new OperatorCommandWords();

        static OperatorCommandKind Kind(string text)
        {
            OperatorCommandParser.TryParse(text, W, out var k, out _);
            return k;
        }

        [Test]
        public void 切场景_游戏陪播()
        {
            Assert.IsTrue(OperatorCommandParser.TryParse("切到游戏陪播", W, out var k, out var s));
            Assert.AreEqual(OperatorCommandKind.SwitchScene, k);
            Assert.AreEqual(LiveScene.Game, s);
        }

        [Test]
        public void 切场景_各种说法()
        {
            void Check(string text, LiveScene want)
            {
                Assert.IsTrue(OperatorCommandParser.TryParse(text, W, out var k, out var s), text);
                Assert.AreEqual(OperatorCommandKind.SwitchScene, k, text);
                Assert.AreEqual(want, s, text);
            }
            Check("切换到搭档模式", LiveScene.Duo);
            Check("换到独播", LiveScene.Solo);
            Check("切到双人", LiveScene.Duo);
            Check("切到陪播模式吧", LiveScene.Game);
            Check("切到自己播", LiveScene.Solo);
        }

        [Test]
        public void 切场景_场景名识别不出()
        {
            Assert.IsTrue(OperatorCommandParser.TryParse("切到唱歌模式", W, out var k, out _));
            Assert.AreEqual(OperatorCommandKind.UnknownScene, k);
        }

        [Test]
        public void 停止表演()
        {
            Assert.AreEqual(OperatorCommandKind.StopPerformance, Kind("别唱了"));
            Assert.AreEqual(OperatorCommandKind.StopPerformance, Kind("别跳了"));
            Assert.AreEqual(OperatorCommandKind.StopPerformance, Kind("停一下吧"));
        }

        [Test]
        public void 清空弹幕()
        {
            Assert.AreEqual(OperatorCommandKind.ClearQueue, Kind("别念了"));
            Assert.AreEqual(OperatorCommandKind.ClearQueue, Kind("清空弹幕"));
        }

        [Test]
        public void 安静()
        {
            Assert.AreEqual(OperatorCommandKind.Quiet, Kind("安静一会儿"));
            Assert.AreEqual(OperatorCommandKind.Quiet, Kind("先别说话"));
        }

        [Test]
        public void 恢复()
        {
            Assert.AreEqual(OperatorCommandKind.Resume, Kind("可以说话了"));
            Assert.AreEqual(OperatorCommandKind.Resume, Kind("恢复"));
        }

        [Test]
        public void 只认句首()
        {
            // 「我想让你别唱了」是在跟她聊天，不是指令
            Assert.IsFalse(OperatorCommandParser.TryParse("我想让你别唱了", W, out _, out _));
        }

        [Test]
        public void 句首标点不影响()
        {
            Assert.AreEqual(OperatorCommandKind.StopPerformance, Kind("，别唱了！"));
        }

        [Test]
        public void 非指令_交给后续管线()
        {
            Assert.IsFalse(OperatorCommandParser.TryParse("唱一首黄昏", W, out _, out _));
            Assert.IsFalse(OperatorCommandParser.TryParse("告诉大家八点下播", W, out _, out _));
            Assert.IsFalse(OperatorCommandParser.TryParse("今天直播什么好", W, out _, out _));
        }

        [Test]
        public void 空输入()
        {
            Assert.IsFalse(OperatorCommandParser.TryParse(null, W, out _, out _));
            Assert.IsFalse(OperatorCommandParser.TryParse("", W, out _, out _));
            Assert.IsFalse(OperatorCommandParser.TryParse("别唱了", null, out _, out _));
        }

        [Test]
        public void 自定义说法()
        {
            var w = new OperatorCommandWords { Stop = new List<string> { "打住" } };
            Assert.IsTrue(OperatorCommandParser.TryParse("打住", w, out var k, out _));
            Assert.AreEqual(OperatorCommandKind.StopPerformance, k);
            Assert.IsFalse(OperatorCommandParser.TryParse("别唱了", w, out _, out _));   // 默认说法被替换
        }

        [Test]
        public void 同时命中取最长()
        {
            // 「停」是停止表演、「停止念」是清空弹幕：「停止念弹幕」要按更长的算
            var w = new OperatorCommandWords
            {
                Stop = new List<string> { "停" },
                ClearQueue = new List<string> { "停止念" }
            };
            Assert.IsTrue(OperatorCommandParser.TryParse("停止念弹幕", w, out var k, out _));
            Assert.AreEqual(OperatorCommandKind.ClearQueue, k);
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败（RED）**

EditMode 测试，`-testFilter "DouyinLive.Tests.OperatorCommandParserTests"`。
Expected：`rc≠0`、无结果 XML；Editor.log 有 `error CS0246`（`OperatorCommandWords` 等不存在）。

- [ ] **Step 3: 写实现**

创建 `Core/OperatorCommandParser.cs`：

```csharp
using System;
using System.Collections.Generic;

namespace DouyinLive
{
    public enum OperatorCommandKind { None, SwitchScene, UnknownScene, StopPerformance, ClearQueue, Quiet, Resume }

    // 主播控制指令的说法。字段初始值即默认说法；settings.json 里的列表会整体替换对应字段。
    public sealed class OperatorCommandWords
    {
        public List<string> SwitchPrefixes = new List<string> { "切到", "切换到", "换到" };
        public List<string> SceneGame = new List<string> { "游戏", "陪播" };
        public List<string> SceneDuo = new List<string> { "搭档", "主持", "双人" };
        public List<string> SceneSolo = new List<string> { "独播", "自己播" };
        public List<string> Stop = new List<string> { "别唱了", "别跳了", "停一下" };
        public List<string> ClearQueue = new List<string> { "别念了", "清空弹幕" };
        public List<string> Quiet = new List<string> { "安静一会儿", "先别说话" };
        public List<string> Resume = new List<string> { "可以说话了", "恢复" };
    }

    // 主播控制指令识别：只用于主播语音（观众弹幕里同样的文字按普通弹幕处理）。
    // 规则同传话开头词：去掉句首标点后只认句首，同时命中取最长，本地匹配零延迟。
    // 必须排在传话和弹幕管线之前：「别唱了」先进弹幕管线，「唱」字会被当成点歌意图。
    public static class OperatorCommandParser
    {
        static readonly char[] LeadingChars =
            { ' ', '\t', '，', '。', '！', '？', '、', '：', ',', '.', '!', '?', ':', '“', '”', '"' };

        // 返回 true = 是控制指令（含「切场景但场景名识别不出」的 UnknownScene）；false = 交给后续管线
        public static bool TryParse(string command, OperatorCommandWords words,
                                    out OperatorCommandKind kind, out LiveScene scene)
        {
            kind = OperatorCommandKind.None;
            scene = LiveScene.Solo;
            if (string.IsNullOrWhiteSpace(command) || words == null) return false;

            string s = command.TrimStart(LeadingChars);
            int best = 0;
            var bestKind = OperatorCommandKind.None;
            Consider(s, words.SwitchPrefixes, OperatorCommandKind.SwitchScene, ref best, ref bestKind);
            Consider(s, words.Stop, OperatorCommandKind.StopPerformance, ref best, ref bestKind);
            Consider(s, words.ClearQueue, OperatorCommandKind.ClearQueue, ref best, ref bestKind);
            Consider(s, words.Quiet, OperatorCommandKind.Quiet, ref best, ref bestKind);
            Consider(s, words.Resume, OperatorCommandKind.Resume, ref best, ref bestKind);
            if (best == 0) return false;

            if (bestKind == OperatorCommandKind.SwitchScene && !TryFindScene(s.Substring(best), words, out scene))
            {
                kind = OperatorCommandKind.UnknownScene;
                return true;
            }
            kind = bestKind;
            return true;
        }

        static void Consider(string s, List<string> phrases, OperatorCommandKind k,
                             ref int best, ref OperatorCommandKind bestKind)
        {
            if (phrases == null) return;
            foreach (var p in phrases)
            {
                // 空串说法会让任何句子都命中，必须跳过
                if (string.IsNullOrEmpty(p) || s.Length < p.Length || p.Length <= best) continue;
                if (string.Compare(s, 0, p, 0, p.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    best = p.Length;
                    bestKind = k;
                }
            }
        }

        // 场景名可以出现在开头词之后任意位置（「切到游戏陪播模式吧」），取最早出现的那个
        static bool TryFindScene(string rest, OperatorCommandWords w, out LiveScene scene)
        {
            scene = LiveScene.Solo;
            int bestAt = int.MaxValue;
            Find(rest, w.SceneGame, LiveScene.Game, ref bestAt, ref scene);
            Find(rest, w.SceneDuo, LiveScene.Duo, ref bestAt, ref scene);
            Find(rest, w.SceneSolo, LiveScene.Solo, ref bestAt, ref scene);
            return bestAt != int.MaxValue;
        }

        static void Find(string rest, List<string> aliases, LiveScene sc, ref int bestAt, ref LiveScene scene)
        {
            if (aliases == null) return;
            foreach (var a in aliases)
            {
                if (string.IsNullOrEmpty(a)) continue;
                int at = rest.IndexOf(a, StringComparison.OrdinalIgnoreCase);
                if (at >= 0 && at < bestAt) { bestAt = at; scene = sc; }
            }
        }
    }
}
```

- [ ] **Step 4: 跑测试确认通过（GREEN）**

同 Step 2 命令。Expected：`result="Passed" total="13" passed="13" failed="0"`。

- [ ] **Step 5: 全量回归**

Expected：`total="261" passed="261" failed="0"`（248 + 13）。

- [ ] **Step 6: 提交**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/OperatorCommandParser.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/OperatorCommandParser.cs.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/OperatorCommandParserTests.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/OperatorCommandParserTests.cs.meta"
git commit -m "feat(douyin-live): 主播控制指令识别 OperatorCommandParser——切场景/停止表演/清空弹幕/安静/恢复

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: 服务层接口——低优先级静音、只答谢、停舞、清空待回复

**Files:**
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/SpeechPipeline.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/RewardService.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DanceDirector.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DanmakuAIService.cs`

**Interfaces:**
- Consumes: 无
- Produces（Task 4 使用）：
  - `SpeechPipeline.SetLowPriorityMute(bool on)`
  - `RewardService.ThankOnly(DouyinEvent ev)`
  - `DanceDirector.StopAll()`
  - `DanmakuAIService.ClearPending()`

都是 MonoBehaviour / 依赖 Unity 的服务，没有单元测试；验证靠编译 + 全量回归 + 手工验收。

- [ ] **Step 1: SpeechPipeline——低优先级静音**

在字段区 `long backlogSeq = -1;` 那一行之后加：

```csharp
        bool muteLow;           // 安静状态：欢迎/点赞答谢/报时等低优先级播报不开口（服务照常记账）
```

在 `public void ReleaseNewOnly()` 方法之后加：

```csharp
        static bool IsLowPriority(Priority p) =>
            p == Priority.Welcome || p == Priority.LikeThanks || p == Priority.Milestone;

        // 安静状态：丢弃新入队的低优先级条目，开启时顺带清掉已排队的。
        // 欢迎/点赞/报时服务照常运行（来访记录、点赞计数都在里面），只是不开口——
        // 欢迎服务的「记录来访」和「说欢迎词」绑在一起，从服务侧关会丢记录。
        public void SetLowPriorityMute(bool on)
        {
            muteLow = on;
            if (on) lock (pending) pending.RemoveAll(p => IsLowPriority(p.Prio));
        }
```

在 `EnqueueInternal` 的 `lock (pending)` 块里、第一行（`// 同优先级欢迎/点赞类只保留最新` 注释）之前加：

```csharp
                if (muteLow && IsLowPriority(item.Prio)) return;
```

- [ ] **Step 2: RewardService——只答谢**

在 `public void OnGift(DouyinEvent ev)` 方法之前加：

```csharp
        // 安静状态：只说答谢语，不跳舞、不走礼物规则。大礼物那几句台词说要跳舞，
        // 安静时说出来自相矛盾，所以大礼物也用中档的答谢语。
        public void ThankOnly(DouyinEvent ev)
        {
            if (!Enabled || ev.Type != DouyinMsgType.Gift) return;
            string name = string.IsNullOrEmpty(ev.Nickname) ? "朋友" : ev.Nickname;
            int totalValue = Mathf.Max(1, ev.DiamondCount) * Mathf.Max(1, ev.GiftCount);
            if (totalValue >= 10) CelebrateMedium(name, ev);
            else CelebrateSmall(name, ev);
        }
```

- [ ] **Step 3: DanceDirector——停舞并取消连跳**

在 `public bool Busy => ...` 那一行之后加：

```csharp
        // 主播说「别跳了」：先清连跳计数（否则 Update 里看到这支停了会接着跳下一支），再停当前这支
        public void StopAll()
        {
            chainRemaining = 0;
            var d = Dance;
            if (d != null && d.IsPlaying) d.StopPlay();
        }
```

- [ ] **Step 4: DanmakuAIService——清空待回复**

在 `public void ResetSession()` 方法之前加：

```csharp
        // 安静/清空弹幕：丢掉排队待回复的弹幕，对话历史和观众上下文保留（ResetSession 会连这些一起清）
        public void ClearPending()
        {
            lock (queue) queue.Clear();
        }
```

- [ ] **Step 5: 编译验证**

Expected：`rc=0 errorCS=0 anotherInstance=0`。

- [ ] **Step 6: 全量回归**

Expected：`total="261" passed="261" failed="0"`。

- [ ] **Step 7: 提交**

```bash
cd "E:/Work/AI/Mate-Engine"
D="Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive"
git add "$D/SpeechPipeline.cs" "$D/RewardService.cs" "$D/DanceDirector.cs" "$D/DanmakuAIService.cs"
git commit -m "feat(douyin-live): 低优先级播报静音、礼物只答谢、停舞取消连跳、清空待回复——场景与安静所需接口

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: 场景/安静/主播指令接线 + 配置迁移 + 文档

**Files:**
- Modify: `Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs`
- Modify: `README.md`
- Modify: `Docs/DouyinLive-Integration.md`

**Interfaces:**
- Consumes：Task 1（`LiveScene`、`LiveSceneResolver`、`LiveSwitches`、`LiveBehavior`、`DanmakuHandling`、`LiveSceneMigration`）；Task 2（`OperatorCommandParser`、`OperatorCommandWords`、`OperatorCommandKind`）；Task 3（`SetLowPriorityMute`、`ThankOnly`、`DanceDirector.StopAll`、`ClearPending`）
- Produces：`DouyinLiveManager.SwitchScene(LiveScene target, bool announce)`、`DouyinLiveManager.Scene`；设置字段 `douyinScene`、`douyinQuietMinutes`、`douyinCmd*`

- [ ] **Step 1: 配置字段与迁移（SaveLoadHandler）**

把：

```csharp
        // 弹幕处理模式：0=AI聊天（AI 详细回复并引导观众继续聊）1=游戏陪播（带昵称念弹幕，主播人工回复）
        public int douyinChatMode = 0;
```

替换为：

```csharp
        // 已废弃：被 douyinScene 取代，只在 douyinScene 缺失时读取一次用于迁移（0=AI聊天 1=游戏陪播）
        public int douyinChatMode = 0;
        // 直播场景：0=AI独播 1=游戏陪播 2=搭档主持（见 Docs/superpowers/specs/2026-10-18-douyin-live-scenes-design.md）。
        // -1=尚未迁移，启动时由 MigrateAfterLoad 从 douyinChatMode 推导一次
        public int douyinScene = -1;
        public float douyinQuietMinutes = 5f;   // 「安静一会儿」的时长（装配时钳 0.5~120）
        // 主播控制指令的说法（只认句首）。默认值与 DouyinLive.OperatorCommandWords 一致
        public List<string> douyinCmdSwitchPrefixes = new() { "切到", "切换到", "换到" };
        public List<string> douyinCmdSceneGame = new() { "游戏", "陪播" };
        public List<string> douyinCmdSceneDuo = new() { "搭档", "主持", "双人" };
        public List<string> douyinCmdSceneSolo = new() { "独播", "自己播" };
        public List<string> douyinCmdStop = new() { "别唱了", "别跳了", "停一下" };
        public List<string> douyinCmdClearQueue = new() { "别念了", "清空弹幕" };
        public List<string> douyinCmdQuiet = new() { "安静一会儿", "先别说话" };
        public List<string> douyinCmdResume = new() { "可以说话了", "恢复" };
```

在 `MigrateAfterLoad` 里，`if (changed) SaveToDisk();` 之前加：

```csharp
        // 直播场景：老配置只有 douyinChatMode，迁移一次后存盘，之后只读 douyinScene
        if (data.douyinScene < 0)
        {
            data.douyinScene = (int)DouyinLive.LiveSceneMigration.Resolve(-1, data.douyinChatMode, out _);
            changed = true;
        }
```

- [ ] **Step 2: Manager——字段与 ChatMode 兼容属性**

把：

```csharp
        // 0=AI聊天 1=游戏陪播。直播中可变（F9/设置页），读方要现读现用
        public int ChatMode { get; private set; }
```

替换为：

```csharp
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
```

- [ ] **Step 3: Manager——ApplySettings 装配**

把：

```csharp
            danmakuAI.VerboseChat = d.douyinChatMode == 0;   // AI 聊天模式回复要详细并引导话题
            danmakuAI.OnActionMarker = OnChatActionMarker;   // 聊天模型说「那我唱咯【唱歌:晴天】」
            ChatMode = d.douyinChatMode;
```

替换为（`VerboseChat` 改由 `ApplyBehavior` 按场景设置）：

```csharp
            danmakuAI.OnActionMarker = OnChatActionMarker;   // 聊天模型说「那我唱咯【唱歌:晴天】」
```

在 idleChatter 装配块里删掉这四行（改由 `ApplyBehavior` 按场景设置）：

```csharp
            idleChatter.Enabled = d.douyinIdleChatterEnabled;
            idleChatter.IdleThreshold = d.douyinIdleThreshold;
            idleChatter.AutoSongEnabled = d.douyinIdleAutoSongEnabled;
            idleChatter.AutoDanceEnabled = d.douyinIdleAutoDanceEnabled;
```

并在 `idleChatter.AutoDanceEnabled = ...` 原来那行的位置（即 `idleChatter.SongList = ...` 之后）加：

```csharp

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
```

- [ ] **Step 4: Manager——ApplyBehavior / SwitchScene / 安静 / 指令执行**

把整个 `UpdateChatMode()` 方法（含它上方的两行注释）替换为：

```csharp
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
            danmakuAI.VerboseChat = behavior.Danmaku == DanmakuHandling.AiVerbose;
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
                danmakuAI.ClearPending();    // 排队待回复的弹幕作废（对话历史保留）
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
            if (announce) Say("我回来啦～");
            if (debugLog) Debug.Log("[DouyinLive] 退出安静");
        }

        void StopPerformance()
        {
            bool stopped = false;
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
```

- [ ] **Step 5: Manager——Update、StopLive、HandleChatLegacy、Route、InjectVoiceChat**

① `Update()` 里把：

```csharp
            UpdateChatMode();
            if (!running || client == null) return;
```

替换为：

```csharp
            UpdateSceneKey();
            if (quiet && running && Time.unscaledTime >= quietUntil) ExitQuiet(true);
            if (!running || client == null) return;
```

② `StopLive()` 里，`running = false;` 之后加：

```csharp
            ExitQuiet(false);   // 安静不跨场次：别把低优先级静音留到下次开播
```

③ `HandleChatLegacy` 里把：

```csharp
            if (ChatMode == 1) reader.OnDanmaku(ev);
            else danmakuAI.OnDanmaku(ev);
```

替换为：

```csharp
            if (behavior.Danmaku == DanmakuHandling.Read) reader.OnDanmaku(ev);
            else if (behavior.Danmaku != DanmakuHandling.Ignore) danmakuAI.OnDanmaku(ev);
```

④ `Route` 里，在记账块之后、`if (triggers != null)` 之前加：

```csharp
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
```

⑤ `InjectVoiceChat` 里，在 `if (!running || IsBlocked()) return;` 之后、开头词传话判断之前加：

```csharp
            // 主播控制指令最先：「别唱了」若先进弹幕管线，「唱」字会被当成点歌意图
            if (OperatorCommandParser.TryParse(text, cmdWords, out var cmdKind, out var cmdScene))
            {
                if (debugLog) Debug.Log($"[DouyinLive] 主播指令: {cmdKind} {cmdScene}（{text}）");
                ExecuteOperatorCommand(cmdKind, cmdScene);
                return;
            }
```

- [ ] **Step 6: 确认没有遗留引用**

```bash
cd "E:/Work/AI/Mate-Engine/Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive"
grep -n "UpdateChatMode\|ChatMode = \|d.douyinChatMode == 0" DouyinLiveManager.cs || echo "无遗留"
```

Expected：`无遗留`（`LiveSceneMigration.Resolve(d.douyinScene, d.douyinChatMode, ...)` 那一行不匹配上面的模式）。

- [ ] **Step 7: 编译验证**

Expected：`rc=0 errorCS=0 anotherInstance=0`。

- [ ] **Step 8: 全量回归**

Expected：`total="261" passed="261" failed="0"`。

- [ ] **Step 9: README**

① 把：

```markdown
| `douyinChatMode` | `0` | 弹幕处理模式：`0`=AI聊天（AI 详细回复并引导观众继续聊）；`1`=游戏陪播（带昵称念弹幕，主播人工回复）。**直播中按 F9 随时切**，不用重启 |
| `douyinReadInterval` | `5.0` | 游戏陪播模式下两条念读的最小间隔秒数 |

**两种弹幕模式**：

- **AI 聊天（默认）**：AI 全程回复弹幕，回复两三句、结尾带问句引导观众继续聊。
- **游戏陪播**：直播游戏时用。指令弹幕（点歌/换角色/触发规则）照常工作，其余弹幕由角色
  **带昵称念出来**（「小明说：这游戏怎么玩」），主播听声人工回复——人在游戏里不用盯弹幕区。
  冷场暖场/自动唱歌跳舞不受模式影响，开播前记得自己关（见「冷场暖场」）。
  模式切换即时生效，会念一句确认。
```

替换为：

```markdown
| `douyinScene` | `0` | 直播场景：`0`=AI 独播、`1`=游戏陪播、`2`=搭档主持（见下表）。**直播中按 F9 或用语音随时切**，切换后写回配置，下次启动沿用 |
| `douyinReadInterval` | `5.0` | 游戏陪播场景下两条念读的最小间隔秒数 |
| `douyinChatMode` | — | **已废弃**，由 `douyinScene` 取代。老配置里有它、没有 `douyinScene` 时，启动会自动迁移一次（`1` → 游戏陪播，其余 → AI 独播） |

**三个直播场景**：选一个场景，弹幕、暖场、自动表演就自动配好。

| 行为 | AI 独播（0） | 游戏陪播（1） | 搭档主持（2） |
|---|---|---|---|
| 普通弹幕 | AI 详细回复，并引导观众继续聊 | 带昵称念出来（「小明说：这游戏怎么玩」），你来回 | AI 简短回复，把话头留给你 |
| 冷场暖场 | 开 | 关（不打断你打游戏） | 开，间隔至少 3 分钟 |
| 冷场自动唱跳 | 开 | 关 | 关（什么时候表演由你决定） |
| 欢迎、点赞答谢、礼物答谢、整点报时 | 开 | 开 | 开 |

- 点歌、换角色、触发规则等指令弹幕三个场景都照常工作。
- 场景只能在总开关允许的范围内收紧：比如 `douyinIdleChatterEnabled=false`，三个场景都不暖场；
  `douyinAIReplyEnabled=false`，独播和搭档都不回弹幕（陪播念弹幕不受影响）。
- 主播语音不随场景变化，只看 `douyinVoiceEnabled`。
- 切换即时生效，她会念一句确认。
```

② 在「语音助手」章节的「**和弹幕模式（F9）的关系**」段落，把：

```markdown
**和弹幕模式（F9）的关系**：语音不受 `douyinChatMode` 影响。游戏陪播模式下观众弹幕是被念出来的，
但你对她说的话仍然由 AI 回答（不会把你的话念回给你）；点歌、换角色等指令两种模式下都照常执行。
```

替换为：

```markdown
**和直播场景（F9）的关系**：语音不受 `douyinScene` 影响。游戏陪播时观众弹幕是被念出来的，
但你对她说的话仍然由 AI 回答（不会把你的话念回给你）；点歌、换角色等指令在三个场景下都照常执行。

**主播控制指令**（只有你的语音能触发，观众发同样的弹幕按普通弹幕处理；和传话一样只认句首）：

| 你说 | 效果 | 她的反馈 |
|---|---|---|
| 「切到游戏陪播」「切换到搭档模式」「换到独播」 | 切换直播场景并写回配置。场景名的说法：游戏/陪播、搭档/主持/双人、独播/自己播 | 念确认语；说了不认识的场景名会提示可选的三个；已经是该场景会告诉你 |
| 「别唱了」「别跳了」「停一下」 | 停掉正在唱的歌、正在跳的舞（连跳的后续几支也取消） | 「好～」；本来没在唱跳就不出声 |
| 「别念了」「清空弹幕」 | 丢掉排队等着念/回复的弹幕和欢迎（她正在说的那句在你唤醒时已被打断） | 不出声 |
| 「安静一会儿」「先别说话」 | 进入安静状态（`douyinQuietMinutes` 分钟，默认 5），再说一次重新计时 | 「好，我先安静 5 分钟～」 |
| 「可以说话了」「恢复」 | 提前结束安静 | 「我回来啦～」 |

**安静状态**：接电话、有人敲门时用。期间她**只做两件事**：给送礼物的观众说答谢语（大礼物也只道谢、不跳舞），
以及回应你的语音。AI 回弹幕、念弹幕、暖场、自动表演、观众点歌换角色、欢迎和点赞答谢、整点报时都暂停。
观众记忆照常记录（弹幕、来访、礼物），恢复后她依然认得老观众、接得上话。时间到了她会说「我回来啦」；
停播或重启程序也会结束安静。

注意：指令按句首匹配，「别唱了，给我讲个故事」会被当成停止表演，后半句不处理——想聊天就别用指令开头。

| 配置项 | 默认值 | 作用 |
|---|---|---|
| `douyinQuietMinutes` | `5.0` | 安静状态的时长（分钟），范围 0.5~120 |
| `douyinCmdSwitchPrefixes` | 切到 / 切换到 / 换到 | 切场景的开头词 |
| `douyinCmdSceneGame` / `douyinCmdSceneDuo` / `douyinCmdSceneSolo` | 见上表 | 三个场景名的说法 |
| `douyinCmdStop` / `douyinCmdClearQueue` / `douyinCmdQuiet` / `douyinCmdResume` | 见上表 | 各指令的说法，可自行增减；同时命中按最长的算 |
```

③ 快捷键表里把：

```markdown
| `F9` | 弹幕模式切换：AI聊天 ⇄ 游戏陪播（念弹幕），角色会念一句确认 |
```

替换为：

```markdown
| `F9` | 直播场景循环切换：AI 独播 → 游戏陪播 → 搭档主持 → AI 独播，角色会念一句确认 |
```

④ 功能总览表里，在「语音传话」那一行之后加一行：

```markdown
| 直播场景 | F9 / 语音「切到游戏陪播」 | AI 独播、游戏陪播、搭档主持三个预设，弹幕处理、暖场、自动表演一键切好；「安静一会儿」让她暂时只答谢礼物 |
```

- [ ] **Step 10: 集成文档**

`Docs/DouyinLive-Integration.md`：

⓪ 功能表里把：

```markdown
| 游戏陪播念弹幕 | 普通弹幕（`douyinChatMode: 1`） | 带昵称念出来（「小明说：…」），主播人工回复；F9 直播中随时切换 |
```

替换为：

```markdown
| 直播场景 | `douyinScene`：0=AI独播 1=游戏陪播 2=搭档主持 | 统一弹幕处理（AI 详细/念弹幕/AI 简短）、冷场暖场、自动唱跳；F9 或主播语音「切到…」随时切；「安静一会儿」只留礼物答谢 |
```

① settings.json 块里把：

```jsonc
"douyinChatMode": 0,             // 0=AI聊天（详细回复） 1=游戏陪播（念弹幕），F9 直播中随时切
```

替换为：

```jsonc
"douyinScene": 0,                // 0=AI独播 1=游戏陪播 2=搭档主持；缺失时从已废弃的 douyinChatMode 迁移；F9 直播中循环切
"douyinQuietMinutes": 5.0,       // 「安静一会儿」时长（分钟）
```

② 「关键实现机制」末尾（`### 触发层（2026-08 新增）` 标题之前）加：

```markdown
- **直播场景与安静**：`LiveSceneResolver`（Core 纯函数）输入总开关 + 场景 + 是否安静，输出生效行为，
  `DouyinLiveManager.ApplyBehavior` 下发给 DanmakuAI（VerboseChat）、IdleChatter（暖场/自动唱跳）、
  SpeechPipeline（低优先级静音）。安静时 `Route` 对观众事件只记账不响应（礼物走 `RewardService.ThankOnly`，
  进房/关注仍调欢迎服务以记录来访）；欢迎/点赞答谢/报时的语音在 SpeechPipeline 按优先级丢弃。
  主播控制指令由 `OperatorCommandParser` 在 `InjectVoiceChat` 最先识别，排在传话与弹幕管线之前。
```

③ 「语音助手自测步骤」编号列表末尾接着加：

```markdown
14. 按 F9 三次 → 依次切到游戏陪播、搭档主持、AI 独播，每次念确认语；重启程序后场景保持
15. 游戏陪播下放置 6 分钟不互动 → 不暖场、不自动唱歌
16. 搭档主持下发弹幕 → AI 回复比独播时简短
17. 说「小美小美 切到游戏陪播」→ 切换并确认；「切到唱歌模式」→ 提示可选场景；再说「切到游戏陪播」→「已经是游戏陪播模式啦」
18. 说「小美小美 安静一会儿」→ 发弹幕、点赞、点歌、进房都没反应；送大礼物只道谢不跳舞；5 分钟后她说「我回来啦」
19. 安静中说「小美小美 可以说话了」→ 立即恢复
20. 她唱歌时说「小美小美 别唱了」→ 停歌；跳舞时「别跳了」→ 停舞且不接着跳下一支
21. 陪播模式积压多条弹幕时说「小美小美 别念了」→ 积压的不再念
22. 把 settings.json 里的 `douyinScene` 删掉、`douyinChatMode` 设为 1，启动 → 场景为游戏陪播，配置里写回了 `"douyinScene": 1`
```

- [ ] **Step 11: 提交**

```bash
cd "E:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs" \
        README.md Docs/DouyinLive-Integration.md
git commit -m "feat(douyin-live): 直播场景统一——三场景预设、F9/语音切场景、安静状态、主播控制指令

douyinChatMode 废弃，由 douyinScene 取代（启动时自动迁移）。

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### 手工验收（主播，非代码任务；Task 4 提交后）

逐条执行 `Docs/DouyinLive-Integration.md`「语音助手自测步骤」14～22。重点对应 Review Focus：第 18 条（安静期间她仍回应你的语音，用「小美小美 今天天气怎么样」试一次）、第 18～19 条结束后暖场与欢迎恢复、第 22 条迁移。

## Spec 覆盖对照

| spec 条目 | 落点 |
|---|---|
| 三场景行为表、总开关约束、搭档暖场 ≥180 秒 | Task 1（15 个测试） |
| 安静：只留礼物答谢与主播响应；记账照常；低优先级语音丢弃；大礼物不跳舞 | Task 1 规则 + Task 3（`SetLowPriorityMute`、`ThankOnly`）+ Task 4（`Route` 拦截） |
| 安静到期说「我回来啦」、停播退出、重复安静重新计时 | Task 4 Step 4、5 |
| 主播指令五类 + 场景名识别 + 只认句首 + 取最长 + 自定义说法 | Task 2（13 个测试） |
| 指令只来自主播语音、识别顺序最先 | Task 4 Step 5⑤ |
| 停止表演含连跳取消；没在唱跳不出声 | Task 3 `StopAll` + Task 4 `StopPerformance` |
| 清空弹幕 | Task 3 `ClearPending` + Task 4 `ExecuteOperatorCommand` |
| F9 三场景循环、确认语改 AIReply | Task 4 Step 4 |
| 切场景持久化；已是当前场景/识别不出的反馈 | Task 4 `SwitchScene` / `ExecuteOperatorCommand` |
| `douyinScene` 迁移与越界警告 | Task 1（迁移测试）+ Task 4 Step 1、3 |
| 配置 `douyinQuietMinutes`、`douyinCmd*` | Task 4 Step 1、3 |
| README 与集成文档 | Task 4 Step 9、10 |
