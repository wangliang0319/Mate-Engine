# 语音传话（意图路由）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 主播唤醒后说非命令的话，AI 花一次 ≤1.5 秒的分类判断是「传话」（原话播报给观众）还是「聊天」（走现有 AI 回复），唱跳/换角色/点歌/触发规则全部现状不变。

**Architecture:** 唯一新增组件 `VoiceIntentRouter`（普通类，照抄 `IntentResolver` 的异步骨架：CancellationTokenSource + Task.WhenAny 掐表 + MainThreadDispatcher.Post 回主线程），挂在 `HandleChatLegacy` 的 `FromVoice` 分支里，`Route()` 主干一行不动。分类判定逻辑抽成 Core 纯函数 `IntentText.IsRelayVerdict` 进 EditMode 测试。

**Tech Stack:** Unity 6000.2.6f2 / C# netstandard2.1 / NUnit EditMode 测试 / OpenAI 兼容 IChatBackend。

**Spec:** [Docs/superpowers/specs/2026-10-18-douyin-voice-relay-design.md](../specs/2026-10-18-douyin-voice-relay-design.md)

## Global Constraints

- 从 main 开功能分支 `feat/douyin-voice-relay`，不直接提交 main。
- Core 程序集 `MateEngine.DouyinLive.Core`：references 为空——Core 代码禁用 UnityEngine / Newtonsoft / Debug.Log / Time.* / Mathf.*。
- Tests 程序集 `MateEngine.DouyinLive.Tests`：overrideReferences=true——测试里用不了 Newtonsoft。
- 提交信息中文、`feat(douyin-live):` 风格、结尾加 `Co-Authored-By: Claude Code <noreply@anthropic.com>`。
- 只 `git add` 任务明确列出的文件；工作区里 link.xml 删除、TestResults-*.xml、.mat、UserSettings 等无关变更一律不碰。
- 注释中文、解释「为什么」，与 DouyinLive 现有代码风格一致。

### 验证命令（两个任务共用，照抄即可）

编译检查（有编辑器开着会撞 lockfile，先确认没有 Unity.exe 进程；有就请用户关掉）：

```bash
tasklist //FI "IMAGENAME eq Unity.exe" | grep -i Unity.exe   # 有输出=编辑器开着，跳过编译让用户在编辑器看 Console
"/c/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe" -batchmode -quit -projectPath "E:/Work/AI/Mate-Engine" >/dev/null 2>&1; echo "rc=$?"
grep -c "error CS" "C:/Users/83529/AppData/Local/Unity/Editor/Editor.log"
grep -c "another Unity instance" "C:/Users/83529/AppData/Local/Unity/Editor/Editor.log"
```

Expected：`errorCS=0`、`anotherInstance=0`。Unity 不往 stdout 写任何东西，结论只看 Editor.log 的 grep。

EditMode 测试（结果文件在 LocalLow，**跑前必删**，不删会误读上一次的结果）：

```bash
rm -f "C:/Users/83529/AppData/LocalLow/Shinymoon/MateEngineX/TestResults.xml"
"/c/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe" -batchmode -runTests -projectPath "E:/Work/AI/Mate-Engine" -testPlatform EditMode -assemblyNames MateEngine.DouyinLive.Tests -testFilter "DouyinLive.Tests.IntentTextRelayTests" >/dev/null 2>&1; echo "rc=$?"
ls -la "C:/Users/83529/AppData/LocalLow/Shinymoon/MateEngineX/TestResults.xml"   # 时间戳必须是刚刚
grep -o 'result="[^"]*"\|total="[0-9]*"\|passed="[0-9]*"\|failed="[0-9]*"' "C:/Users/83529/AppData/LocalLow/Shinymoon/MateEngineX/TestResults.xml" | tail -4
```

Expected：`rc=0` 且 XML 显示 `result="Passed"`。**rc=198 = Unity 许可证过期**，停止并向用户说明「请打开一次 Unity Hub 刷新许可」后重跑。全量跑时去掉 `-testFilter` 参数即可。

## Review Focus

计划测试覆盖不到、最容易咬到真人主播的五种输入（reviewer 逐条核对，前一条已有测试钉住）：

1. 分类器越权输出表演意图（`{"intent":"song"}`）→ 必须判为聊天，绝不能触发表演 —— Task 1 测试 `表演意图不算传话` 钉住。
2. 传话文本是 ASR 原样（全角标点、口语重复、前后空格）→ 原样入队，不 Sanitize、不 Trim、不截断 —— reviewer 核对 `OnVoiceRelay` 只有 Enqueue 一行（Task 2）。
3. 没配 AI 后端（Cloud 不可用）时说传话 → `TryRoute` 返回 false 直接聊天，零等待 —— reviewer 核对不可用分支只留 debugLog 日志（Task 2）。
4. 停播后/开播前在途分类回调回来 → `running` 守卫丢弃，停播后绝不开口 —— reviewer 核对 `OnVoiceRelay`/`OnVoiceChat` 首行守卫（Task 2）。
5. MaxInFlight=1 在飞时又来一条语音 → 直接聊天，不排队不等 —— reviewer 核对 `TryRoute` 的 inFlight 分支（Task 2）。

---

### Task 1: Core 分类数据层——IntentKind.Relay + 解析 + IsRelayVerdict

**Files:**
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/IntentSlots.cs:6`（枚举加 Relay）
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/IntentText.cs:94-101`（switch 加 relay/chat）、106 后追加 `IsRelayVerdict`
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/IntentTextRelayTests.cs`

**Interfaces:**
- Consumes: `IntentText.TryParseIntentJson(string raw, out IntentKind kind, out string arg)`（现有，[IntentText.cs:85](../../Assets/MATE%20ENGINE%20-%20Scripts/Game%20APIs/DouyinLive/Core/IntentText.cs)）
- Produces: `IntentKind.Relay`（枚举值）；`public static bool IntentText.IsRelayVerdict(string raw)` —— Task 2 的 VoiceIntentRouter 靠它判定传话

- [ ] **Step 1: 写失败测试**

创建 `Tests/IntentTextRelayTests.cs`（namespace 与现有 `VoiceCommandParserTests.cs` 一致）：

```csharp
using NUnit.Framework;

namespace DouyinLive.Tests
{
    // 语音传话分类的数据层：relay/chat 意图解析 + 传话判定 IsRelayVerdict。
    // 分类结果永远只当数据用，绝不当指令执行（IntentText 一贯原则）。
    public class IntentTextRelayTests
    {
        [Test]
        public void Relay意图解析为Relay()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"relay\",\"arg\":\"\"}", out var k, out _));
            Assert.AreEqual(IntentKind.Relay, k);
        }

        [Test]
        public void Chat意图解析为None()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"chat\"}", out var k, out _));
            Assert.AreEqual(IntentKind.None, k);
        }

        [Test]
        public void None意图照旧解析为None()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"none\"}", out var k, out _));
            Assert.AreEqual(IntentKind.None, k);
        }

        [Test]
        public void 围栏包裹的Relay也能解析()
        {
            // 大模型常在 JSON 外套 ```json 围栏，解析必须剥掉
            var raw = "```json\n{\"intent\": \"relay\", \"arg\": \"等会儿有抽奖哦\"}\n```";
            Assert.IsTrue(IntentText.TryParseIntentJson(raw, out var k, out _));
            Assert.AreEqual(IntentKind.Relay, k);
        }

        [Test]
        public void 原有四类解析不受影响()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"song\",\"arg\":\"黄昏\"}", out var k1, out var a1));
            Assert.AreEqual(IntentKind.Song, k1);
            Assert.AreEqual("黄昏", a1);
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"dance\"}", out var k2, out _));
            Assert.AreEqual(IntentKind.Dance, k2);
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"avatar\"}", out var k3, out _));
            Assert.AreEqual(IntentKind.Avatar, k3);
        }

        [Test]
        public void 非法输入照旧返回False()
        {
            Assert.IsFalse(IntentText.TryParseIntentJson("我觉得他是想点歌", out _, out _));
            Assert.IsFalse(IntentText.TryParseIntentJson("{\"intent\":\"不知道\"}", out _, out _));
            Assert.IsFalse(IntentText.TryParseIntentJson(null, out _, out _));
        }

        // ---------- IsRelayVerdict：VoiceIntentRouter 的传话判定 ----------

        [Test]
        public void Relay判定为传话()
        {
            Assert.IsTrue(IntentText.IsRelayVerdict("{\"intent\":\"relay\"}"));
        }

        [Test]
        public void Chat和None判定为聊天()
        {
            Assert.IsFalse(IntentText.IsRelayVerdict("{\"intent\":\"chat\"}"));
            Assert.IsFalse(IntentText.IsRelayVerdict("{\"intent\":\"none\"}"));
        }

        [Test]
        public void 表演意图不算传话()
        {
            // 分类器偶尔越权输出表演意图：这不是传话，必须回聊天路径
            Assert.IsFalse(IntentText.IsRelayVerdict("{\"intent\":\"song\",\"arg\":\"黄昏\"}"));
        }

        [Test]
        public void 解析失败判定为聊天()
        {
            Assert.IsFalse(IntentText.IsRelayVerdict("我觉得他在跟你聊天"));
            Assert.IsFalse(IntentText.IsRelayVerdict(null));
            Assert.IsFalse(IntentText.IsRelayVerdict(""));
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败（RED）**

用「验证命令」的 EditMode 测试命令。Expected：rc≠0，Editor.log 出现 `error CS0117: "IntentKind" 未包含 "Relay"` 和 `error CS0117: "IntentText" 未包含 "IsRelayVerdict"`——整个测试程序集编译不过，这正是 Unity 下的 RED。

- [ ] **Step 3: 最小实现**

`Core/IntentSlots.cs:6` 枚举加 `Relay`：

```csharp
public enum IntentKind { None, Song, Dance, Avatar, Relay }
```

`Core/IntentText.cs:94-101` 的 switch 加两个 case（`chat` 也要显式接住：分类器输出 chat 时解析成功返回 None，和 none 同待遇）：

```csharp
            switch (intent.Trim().ToLowerInvariant())
            {
                case "song":   kind = IntentKind.Song; break;
                case "dance":  kind = IntentKind.Dance; break;
                case "avatar": kind = IntentKind.Avatar; break;
                case "relay":  kind = IntentKind.Relay; break;
                case "chat":   kind = IntentKind.None; break;   // 语音传话分类器的「聊天」：不是表演意图
                case "none":   kind = IntentKind.None; break;
                default: return false;
            }
```

`Core/IntentText.cs` 的 `TryParseIntentJson` 方法结束后（约 106 行 `}` 之后）追加：

```csharp
        // 语音传话路由的判定：relay 算传话，其余（chat/none/越权输出/解析失败）一律回聊天路径。
        // 放 Core 是为了能进 EditMode 测试——路由器本体在 Assembly-CSharp，测不了。
        public static bool IsRelayVerdict(string raw)
        {
            return TryParseIntentJson(raw, out var kind, out _) && kind == IntentKind.Relay;
        }
```

- [ ] **Step 4: 跑测试确认通过（GREEN）**

同一测试命令。Expected：`result="Passed"`，total=10 passed=10 failed=0。

- [ ] **Step 5: 全量回归**

去掉 `-testFilter` 跑全量（`-assemblyNames MateEngine.DouyinLive.Tests`）。Expected：total=163 passed=163 failed=0（原 153 + 新 10）。

- [ ] **Step 6: 提交**

```bash
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/IntentSlots.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/IntentText.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/IntentTextRelayTests.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/IntentTextRelayTests.cs.meta"
git commit -m "feat(douyin-live): Core 意图解析支持 relay/chat 与传话判定 IsRelayVerdict

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

（Unity 会生成 .meta，若还没有就用 `git status` 看实际路径补上；.cs.meta 必须进版本库。）

---

### Task 2: VoiceIntentRouter + 配置 + Manager 接线 + 文档

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/VoiceIntentRouter.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs`（字段 ~53、装配 ~184、StartLive ~259、StopLive ~273、HandleChatLegacy ~396-406）
- Modify: `Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs:242`（voice 设置块尾部加一字段）
- Modify: `Docs/DouyinLive-Integration.md`（4 处）

**Interfaces:**
- Consumes: `IntentText.IsRelayVerdict(string raw)`（Task 1）；`IChatBackend.ChatAsync(string systemPrompt, IReadOnlyList<ChatMsg> history, string userMsg, Action<string> onDelta, CancellationToken ct)`；`SpeechPipeline.Enqueue(string text, Priority prio, float ttlSeconds = 30f)`；`SpeechPipeline.Priority.AIReply`
- Produces: `VoiceIntentRouter.TryRoute(DouyinEvent ev, Action<string> onRelay, Action<DouyinEvent> onChat)` 返回 bool（true=已接管）；`SaveLoadHandler.SettingsData.douyinVoiceRelayEnabled`（bool，默认 true）

本任务在 Assembly-CSharp（Unity 层），路由器本身没有单测——验证靠编译零错 + 全量测试绿 + 手工验收。**判定逻辑已在 Task 1 进了 Core 测试**，这里是纯接线。

- [ ] **Step 1: 创建 VoiceIntentRouter.cs**

路径 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/VoiceIntentRouter.cs`，整文件内容：

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DouyinLive
{
    // 语音传话路由：非命令语音（硬命令/触发规则/槽位/表演意图都已被前几层消费）
    // 到这里二分——是让角色对外播报（relay），还是在和角色聊天（chat）。
    // chat/超时/失败一律回聊天路径，体面降级，绝不让直播停下来等分类。
    //
    // 与 IntentResolver 的两点差别：没有本地预筛（传话句不含表演关键词，预筛必漏）、
    // 没有单人冷却（传话是主播连续说话的合法用法，15 秒冷却会废掉它）。
    // 异步骨架与 IntentResolver 相同：自掐 1.5 秒表 + 结果经 MainThreadDispatcher 回主线程。
    public class VoiceIntentRouter
    {
        public IChatBackend Cloud;
        public bool debugLog = false;

        public const int MaxInFlight = 1;
        public const float TimeoutSeconds = 1.5f;

        const string SystemPrompt =
            "你是主播语音意图分类器。主播正在对桌面虚拟角色说话，判断这句话的目的：" +
            "relay=让角色把这段话原样转达给直播间观众（公告/通知/交代事情），" +
            "chat=在和角色对话、问它问题、或让它做事。" +
            "只输出 JSON，不要任何解释：{\"intent\":\"relay|chat\"}";

        static readonly List<ChatMsg> EmptyHistory = new List<ChatMsg>();

        int inFlight;

        // StartLive/StopLive 调用：丢弃在途分类，别让上一场的话进这一场
        public void Reset()
        {
            inFlight = 0;
        }

        // 返回 true = 已接管（正在问大模型），调用方不要再走聊天路径；
        // false = 后端不可用/已在飞，调用方直接走聊天。
        // 结果稍后一定会经 onRelay 或 onChat 回到主线程，不会石沉大海。
        public bool TryRoute(DouyinEvent ev, Action<string> onRelay, Action<DouyinEvent> onChat)
        {
            if (ev == null || onRelay == null || onChat == null) return false;
            if (Cloud == null || !Cloud.IsAvailable)
            {
                if (debugLog) Debug.Log("[VoiceIntentRouter] 云端后端未配置，传话分类不可用，走聊天");
                return false;
            }
            if (inFlight >= MaxInFlight) return false;

            inFlight++;
            // 丢弃的 Task 里逃出来的异常没人接，观测一下免得进 UnobservedTaskException。
            _ = RouteAsync(ev, onRelay, onChat)
                    .ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            return true;
        }

        async Task RouteAsync(DouyinEvent ev, Action<string> onRelay, Action<DouyinEvent> onChat)
        {
            string raw = null;
            var isRelay = false;
            try
            {
                Task<string> call = null;
                var cts = new CancellationTokenSource();
                try
                {
                    call = Cloud.ChatAsync(SystemPrompt, EmptyHistory, ev.Content ?? "", null, cts.Token);
                    // 超时不能只靠 CancellationToken：CloudChatBackend 的 SSE 读循环用的是
                    // 不带 ct 的 ReadLineAsync，首字节迟迟不来时 token 拦不住，自己掐表
                    var done = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds)));
                    if (done == call) raw = await call;
                    else cts.Cancel();
                }
                catch (Exception ex)
                {
                    if (debugLog) Debug.Log("[VoiceIntentRouter] 分类失败：" + ex.Message);
                }
                finally
                {
                    // 不能 using：超时那条路上被丢下的 call 还攥着 cts.Token，当场
                    // Dispose 会抛 ObjectDisposedException。挂延续等它收尾，顺手观测异常。
                    if (call == null) cts.Dispose();
                    else call.ContinueWith(t => { _ = t.Exception; cts.Dispose(); }, TaskScheduler.Default);
                }

                // 分类结果只当数据用：relay 之外的一切（chat/none/越权输出/解析失败）都回聊天
                isRelay = IntentText.IsRelayVerdict(raw);
            }
            finally
            {
                // Post 放 finally：inFlight 只在这个回调里减，中间任何一步抛出都会
                // 永久漏掉一个名额，漏两次传话功能整场作废。
                var e = ev;
                var relay = isRelay;
                MainThreadDispatcher.Post(() =>
                {
                    // Reset() 可能在回调还排队时清零 inFlight，Max(0, …) 防止减成负数
                    inFlight = Mathf.Max(0, inFlight - 1);
                    if (debugLog) Debug.Log($"[VoiceIntentRouter] 「{e.Content}」→ {(relay ? "传话" : "聊天")}");
                    if (relay) onRelay(e.Content);
                    else onChat(e);
                });
            }
        }
    }
}
```

- [ ] **Step 2: SaveLoadHandler 加配置字段**

`SaveLoadHandler.cs` 的 SettingsData 语音块尾部（`douyinVoiceNickname` 行之后）加：

```csharp
        public bool douyinVoiceRelayEnabled = true;          // 传话意图：AI 分类非命令语音是播报给观众还是聊天；false=回到纯聊天
```

- [ ] **Step 3: DouyinLiveManager 接线（五处）**

① 字段（`VoiceCommandListener voiceListener;` 行后）：

```csharp
        readonly VoiceIntentRouter voiceIntents = new VoiceIntentRouter();
        bool voiceRelayEnabled;
```

② 装配（ApplySettings 里 `intents.debugLog = debugLog;` 行后；`d` 在该方法第 80 行已定义）：

```csharp
            voiceIntents.Cloud = cloudBackend;
            voiceIntents.debugLog = debugLog;
            voiceRelayEnabled = d.douyinVoiceRelayEnabled;
```

③ StartLive（`intents.Reset();` 行后）：

```csharp
            voiceIntents.Reset();
```

④ StopLive（`intents.Reset();` 行后）：

```csharp
            voiceIntents.Reset();   // 在途传话分类同上，停播后不动作
```

⑤ HandleChatLegacy 整个方法替换（原 396-406 行）为下面内容，并在其后追加两个回调方法：

```csharp
        // 触发层、槽位、意图判定都没接住的弹幕走这里。抽成方法是因为意图判定
        // 是异步的：1.5 秒后判不出来，回调要能把这条弹幕补回原路径。
        void HandleChatLegacy(DouyinEvent ev)
        {
            if (reward.TryHandleDanmaku(ev)) return;
            // 语音不走「念弹幕」：主播自己的话被角色念回给主播没有意义，无论什么模式都走 AI 回复。
            // 传话开关开着时先问分类器：是让角色对外播报（relay），还是在和角色聊天（chat）；
            // 后端不可用/已在飞/超时一律当聊天，绝不让直播停下来等分类。
            if (ev.FromVoice)
            {
                if (voiceRelayEnabled && voiceIntents.TryRoute(ev, OnVoiceRelay, OnVoiceChat)) return;
                danmakuAI.OnDanmaku(ev);
                return;
            }
            // 游戏陪播：AI 不掺和，带昵称念出来，主播听声人工回复。
            // 指令弹幕（触发规则/槽位/点歌等）在进到这里之前就已被消费。
            if (ChatMode == 1) reader.OnDanmaku(ev);
            else danmakuAI.OnDanmaku(ev);
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
```

- [ ] **Step 4: 编译验证**

跑「验证命令」的编译检查。Expected：`errorCS=0`。若编辑器开着，请用户在编辑器看 Console 后口头确认无红。

- [ ] **Step 5: 全量测试回归**

全量 EditMode（无 -testFilter）。Expected：total=163 passed=163 failed=0（本任务不加测试，数字与 Task 1 结束时一致）。

- [ ] **Step 6: 文档更新（Docs/DouyinLive-Integration.md 四处）**

① 功能总览表（`| 语音助手 |` 行后加一行）：

```markdown
| 语音传话 | 唤醒后说公告 | 「小美小美 等会儿有抽奖哦」→ 原话播报给观众；说给角色的话照旧 AI 回复；需 `douyinVoiceRelayEnabled` |
```

② settings.json 块（`"douyinVoiceNickname": "主播",` 行后加）：

```jsonc
"douyinVoiceRelayEnabled": true,   // 传话意图：AI 分类非命令语音是播报还是聊天；false=回到纯聊天
```

③ 「关键实现机制」的 **语音助手** 条目末尾追加一句（在「角色出声期间暂停监听防自触发。」之后）：

```markdown
  非命令内容由 VoiceIntentRouter 花 ≤1.5 秒做一次 LLM 分类：relay=传话（原话进播报队列，
  不过 AI 人设/敏感词通路），chat/超时/失败=走 AI 回复；关 `douyinVoiceRelayEnabled` 回到纯聊天。
```

④ 「语音助手自测步骤」末尾追加（原 1-8 条之后接着编号）：

```markdown
9. 说「小美小美 等会儿有抽奖哦」→ 她原样播出这句话（不续唠）；隔 3 秒再说一条 → 两条都播出（无冷却）
10. 说「小美小美 今天唱什么好」→ AI 对话回复（不是播出你的问题）
11. settings.json 设 `douyinVoiceRelayEnabled=false` → 第 9 条变成 AI 对话回复（回到纯聊天）
```

- [ ] **Step 7: 提交**

```bash
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/VoiceIntentRouter.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/VoiceIntentRouter.cs.meta" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs" \
        "Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs" \
        "Docs/DouyinLive-Integration.md"
git commit -m "feat(douyin-live): 语音传话 VoiceIntentRouter——非命令语音 AI 分类传话/聊天

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### 手工验收（主播，非代码任务；Task 2 提交后）

前提：真机麦克风 + ASR/AI 后端已配置（语音助手自测步骤 1-8 的前置）。逐条对照 Docs/DouyinLive-Integration.md「语音助手自测步骤」1-11，重点新行为是 9（原样播出、连续两条）、10（聊天不播报）、11（关开关回到纯聊天）。断网或没配 aiBaseUrl 时说传话 → 她按聊天回了句嘴、不卡壳（TryRoute 直接 false）。

## Spec 覆盖对照

| spec 条目 | 落点 |
|---|---|
| IntentKind.Relay + TryParseIntentJson 认 relay/chat | Task 1 Step 3 |
| IsRelayVerdict 判定（含越权输出不算传话） | Task 1 Step 1/3（测试钉住 Review Focus #1） |
| VoiceIntentRouter（无预筛、无冷却、MaxInFlight=1、1.5s 超时、Reset、Post-finally） | Task 2 Step 1 |
| douyinVoiceRelayEnabled 默认 true | Task 2 Step 2 |
| HandleChatLegacy FromVoice 分流 + OnVoiceRelay/OnVoiceChat running 守卫 | Task 2 Step 3⑤ |
| ApplySettings 装配 + StartLive/StopLive Reset | Task 2 Step 3①②③④ |
| 传话 Enqueue AIReply 优先级 30s | Task 2 Step 3⑤（OnVoiceRelay） |
| 文档四处 | Task 2 Step 6 |
| 手工验证 8 条 | 手工验收节 + 文档自测步骤 9-11 |
