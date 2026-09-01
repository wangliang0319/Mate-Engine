# 弹幕两轮点播：追问槽位 + LLM 意图兜底 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让观众「先说点歌、再发歌名」这种两轮对话真的能触发唱歌，点舞和换角色同样支持指名与追问，并给关键词覆盖不到的说法加一层 LLM 意图兜底。

**Architecture:** 三条路径依次兜底——关键词规则（现有）→ 追问槽位补全（新）→ LLM 意图判定（新）。三条最终都汇入同一个执行口：浅拷贝命中的规则、只替换 `effects`、保留 `id`，交给现有的 `ActionDirector.Submit`。因为 `TriggerLimiter` 按 `rule.id` 记账，副本与原规则共用全部冷却账，所以限流层一行都不用改。

**Tech Stack:** Unity 6000.2.6f2 / C# 9 / Windows Mono；NUnit EditMode 测试（`MateEngine.DouyinLive.Tests` asmdef）；Newtonsoft.Json（仅 Unity 层）。

**Spec:** [Docs/superpowers/specs/2026-09-02-douyin-intent-slots-design.md](../specs/2026-09-02-douyin-intent-slots-design.md)

## Global Constraints

- **`Core/` 必须零依赖。** `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/MateEngine.DouyinLive.Core.asmdef` 的 `"references"` 必须保持 `[]`。Core 里的代码**不得**出现 `using UnityEngine`、`using Newtonsoft.Json`、`Debug.Log`、`Time.*`、`Application.*`、`Mathf.*`。需要时间就注入 `public Func<float> Now`。这是唯一能让代码进 EditMode 测试的方式（`Assembly-CSharp` 自动引用所有 asmdef，测试 asmdef 因此无法反向引用它）。
- **Unity 层代码放在 `DouyinLive/` 根目录**，不要放进 `Core/`。
- **老配置不能退化。** 效果 ID `swapAvatar`（无参数）和 `dance:random` 的行为必须与今天完全一致。`TriggerGlobal` / `TriggerRule` 的新字段一律带默认值，缺字段的老 `douyin_triggers.json` 反序列化后必须能正常工作。
- **注释写「为什么」不写「做了什么」**，用中文，与本目录既有代码风格一致。
- **测试方法名用中文**，与 `TriggerLimiterTests.cs` 一致。测试时钟从非 0 值起步（防止「未初始化 = 0」的假通过）。
- **现有 51 个 EditMode 测试必须保持全绿。**
- **编译验证前必须关闭 Unity 编辑器。** 只有一个 Unity 实例能持有 `Temp/UnityLockfile`；编辑器开着时 batchmode 会以退出码 21 失败并打印 "another Unity instance"，此时 `grep -c "error CS"` 结果为 0 是假象。每次编译后**必须同时**跑 `grep -ci "another Unity instance"` 确认为 0。
- **Unity 会为新 `.cs` 生成 `.cs.meta`**，提交时要一起 `git add`。
- 分支：`feat/douyin-triggers`。每个任务结束后提交一次。

## 通用命令

**编译验证**（每个任务的最后一步之前跑）：

```bash
cd "e:/Work/AI/Mate-Engine"
"/c/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe" \
  -batchmode -quit -projectPath "E:/Work/AI/Mate-Engine" \
  -logFile "E:/Work/AI/Mate-Engine/compile.log"
grep -ci "another Unity instance" compile.log   # 必须是 0，否则上面根本没编译
grep -c "error CS" compile.log                  # 必须是 0
```

**跑 EditMode 测试**：

```bash
cd "e:/Work/AI/Mate-Engine"
"/c/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe" \
  -runTests -batchmode -projectPath "E:/Work/AI/Mate-Engine" \
  -testPlatform EditMode -assemblyNames MateEngine.DouyinLive.Tests \
  -logFile "E:/Work/AI/Mate-Engine/test.log"
# -testResults 参数不生效，结果固定落在下面这个路径
grep -o 'total="[0-9]*" passed="[0-9]*" failed="[0-9]*"' \
  "C:/Users/83529/AppData/LocalLow/Shinymoon/MateEngineX/TestResults.xml" | head -1
```

---

### Task 1: 追问槽位表 IntentSlots

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/IntentSlots.cs`
- Test: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/IntentSlotsTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `enum IntentKind { None, Song, Dance, Avatar }`；`class IntentSlot { string UserId; string Nickname; IntentKind Kind; string RuleId; float OpenedAt; }`；`class IntentSlots` 带 `Func<float> Now`、`float Window`、`int Capacity`、`int Count`、`void Open(string userId, string nickname, IntentKind kind, string ruleId)`、`bool TryPeek(string userId, out IntentSlot slot)`、`void Take(string userId)`、`void Prune()`、`void Reset()`

- [ ] **Step 1: 写失败的测试**

新建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/IntentSlotsTests.cs`：

```csharp
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class IntentSlotsTests
    {
        float clock;
        IntentSlots slots;

        [SetUp]
        public void SetUp()
        {
            clock = 500f;                        // 从非 0 起步，防止「未初始化 = 0」的假通过
            slots = new IntentSlots { Now = () => clock };
        }

        [Test]
        public void 开槽之后能看到()
        {
            slots.Open("u1", "小明", IntentKind.Song, "song");
            Assert.IsTrue(slots.TryPeek("u1", out var s));
            Assert.AreEqual(IntentKind.Song, s.Kind);
            Assert.AreEqual("song", s.RuleId);
            Assert.AreEqual("小明", s.Nickname);
        }

        [Test]
        public void Take之后槽位消失()
        {
            slots.Open("u1", "小明", IntentKind.Song, "song");
            slots.Take("u1");
            Assert.IsFalse(slots.TryPeek("u1", out _));
        }

        [Test]
        public void 只Peek不Take时槽位保留且开槽时间不刷新()
        {
            slots.Open("u1", "小明", IntentKind.Song, "song");
            clock += 10f;
            Assert.IsTrue(slots.TryPeek("u1", out _));
            clock += 10f;
            Assert.IsTrue(slots.TryPeek("u1", out _));
            clock += 11f;                        // 累计 31 秒 > Window 30
            Assert.IsFalse(slots.TryPeek("u1", out _),
                "反复 Peek 不能续期，否则观众连发几个「666」就能把窗口无限延长");
        }

        [Test]
        public void 超过窗口就取不到()
        {
            slots.Open("u1", "小明", IntentKind.Song, "song");
            clock += 31f;
            Assert.IsFalse(slots.TryPeek("u1", out _));
        }

        [Test]
        public void 多个用户的槽位互不干扰()
        {
            slots.Open("a", "A", IntentKind.Song, "song");
            slots.Open("b", "B", IntentKind.Dance, "reqdance");
            Assert.IsTrue(slots.TryPeek("a", out var sa));
            Assert.IsTrue(slots.TryPeek("b", out var sb));
            Assert.AreEqual(IntentKind.Song, sa.Kind);
            Assert.AreEqual(IntentKind.Dance, sb.Kind);
            slots.Take("a");
            Assert.IsTrue(slots.TryPeek("b", out _));
        }

        [Test]
        public void 同一人再次开槽覆盖旧的()
        {
            slots.Open("u1", "小明", IntentKind.Song, "song");
            clock += 5f;
            slots.Open("u1", "小明", IntentKind.Avatar, "swap");
            Assert.AreEqual(1, slots.Count);
            Assert.IsTrue(slots.TryPeek("u1", out var s));
            Assert.AreEqual(IntentKind.Avatar, s.Kind);
        }

        [Test]
        public void 容量满时挤掉最旧的一个()
        {
            slots.Capacity = 3;
            slots.Open("a", "A", IntentKind.Song, "song"); clock += 1f;
            slots.Open("b", "B", IntentKind.Song, "song"); clock += 1f;
            slots.Open("c", "C", IntentKind.Song, "song"); clock += 1f;
            slots.Open("d", "D", IntentKind.Song, "song");
            Assert.AreEqual(3, slots.Count);
            Assert.IsFalse(slots.TryPeek("a", out _), "最旧的 a 应该被挤掉");
            Assert.IsTrue(slots.TryPeek("d", out _));
        }

        [Test]
        public void 空UserId不开槽()
        {
            slots.Open("", "匿名", IntentKind.Song, "song");
            slots.Open(null, "匿名", IntentKind.Song, "song");
            Assert.AreEqual(0, slots.Count);
            Assert.IsFalse(slots.TryPeek("", out _));
            Assert.IsFalse(slots.TryPeek(null, out _));
        }

        [Test]
        public void Kind为None不开槽()
        {
            slots.Open("u1", "小明", IntentKind.None, "song");
            Assert.AreEqual(0, slots.Count);
        }

        [Test]
        public void 窗口设为0等于关闭追问功能()
        {
            slots.Window = 0f;
            slots.Open("u1", "小明", IntentKind.Song, "song");
            Assert.AreEqual(0, slots.Count);
            Assert.IsFalse(slots.TryPeek("u1", out _));
        }

        [Test]
        public void Prune清掉过期槽位()
        {
            slots.Open("a", "A", IntentKind.Song, "song");
            clock += 31f;
            slots.Open("b", "B", IntentKind.Song, "song");
            slots.Prune();
            Assert.AreEqual(1, slots.Count);
            Assert.IsTrue(slots.TryPeek("b", out _));
        }

        [Test]
        public void Reset清空全部()
        {
            slots.Open("a", "A", IntentKind.Song, "song");
            slots.Open("b", "B", IntentKind.Song, "song");
            slots.Reset();
            Assert.AreEqual(0, slots.Count);
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

跑「通用命令」里的 EditMode 测试。预期：编译失败，`test.log` 里出现 `error CS0246: The type or namespace name 'IntentSlots' could not be found`。

- [ ] **Step 3: 实现 IntentSlots**

新建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/IntentSlots.cs`：

```csharp
using System;
using System.Collections.Generic;

namespace DouyinLive
{
    public enum IntentKind { None, Song, Dance, Avatar }

    // 一次「角色问了、等观众答」的待补状态
    public class IntentSlot
    {
        public string UserId = "";
        public string Nickname = "";
        public IntentKind Kind = IntentKind.None;
        public string RuleId = "";     // 开槽的规则，补全时按 id 反查回去借它的限流参数
        public float OpenedAt;
    }

    // 追问槽位表：角色问「想听什么歌呀」之后，这个观众接下来 Window 秒内发的
    // 第一条可用弹幕就是答案。按 UserId 索引 —— 只认发起人，别人插嘴不算数。
    public class IntentSlots
    {
        public Func<float> Now = () => 0f;
        public float Window = 30f;     // <= 0 等于关闭追问功能
        public int Capacity = 8;

        readonly Dictionary<string, IntentSlot> slots = new Dictionary<string, IntentSlot>();

        public int Count { get { return slots.Count; } }

        public void Open(string userId, string nickname, IntentKind kind, string ruleId)
        {
            if (string.IsNullOrEmpty(userId)) return;   // 认不出是谁，补全无从谈起
            if (kind == IntentKind.None || Window <= 0f) return;

            Prune();
            // 容量满时挤掉最旧的而不是拒绝新的：直播间里新请求比旧请求有价值
            if (!slots.ContainsKey(userId) && slots.Count >= Capacity) EvictOldest();

            slots[userId] = new IntentSlot
            {
                UserId = userId,
                Nickname = nickname ?? "",
                Kind = kind,
                RuleId = ruleId ?? "",
                OpenedAt = Now()
            };
        }

        // 只看不删。补全内容通不过校验时槽位要原样留着，而「取出来再放回去」
        // 会刷新 OpenedAt —— 观众连发十个「666」就能把 30 秒窗口无限续期。
        public bool TryPeek(string userId, out IntentSlot slot)
        {
            slot = null;
            if (string.IsNullOrEmpty(userId)) return false;
            Prune();
            return slots.TryGetValue(userId, out slot);
        }

        public void Take(string userId)
        {
            if (!string.IsNullOrEmpty(userId)) slots.Remove(userId);
        }

        public void Prune()
        {
            if (slots.Count == 0) return;
            float now = Now();
            List<string> stale = null;
            foreach (var kv in slots)
            {
                if (now - kv.Value.OpenedAt < Window) continue;
                if (stale == null) stale = new List<string>();
                stale.Add(kv.Key);
            }
            if (stale == null) return;
            foreach (var k in stale) slots.Remove(k);
        }

        public void Reset()
        {
            slots.Clear();
        }

        void EvictOldest()
        {
            string oldest = null;
            float best = float.MaxValue;
            foreach (var kv in slots)
                if (kv.Value.OpenedAt < best) { best = kv.Value.OpenedAt; oldest = kv.Key; }
            if (oldest != null) slots.Remove(oldest);
        }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

跑 EditMode 测试。预期：`failed="0"`，`total` 比之前多 12（51 → 63）。

- [ ] **Step 5: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/IntentSlots.cs"* \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/IntentSlotsTests.cs"*
git commit -m "feat(douyin-live): add per-user follow-up intent slots

Peek and Take are separate on purpose: when the answer fails validation
the slot must stay with its original timestamp, and take-then-reopen
would refresh it — ten \"666\" messages would extend the 30s window
forever."
```

---

### Task 2: 文本判定 IntentText

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/IntentText.cs`
- Test: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/IntentTextTests.cs`

**Interfaces:**
- Consumes: `IntentKind`（Task 1）
- Produces: `static class IntentText`，含 `const int MaxArgLength = 25`、`const int MaxPrefilterLength = 30`、`static bool IsUsableArg(string s)`、`static IntentKind LooksLikeIntent(string s)`、`static bool TryParseIntentJson(string raw, out IntentKind kind, out string arg)`

- [ ] **Step 1: 写失败的测试**

新建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/IntentTextTests.cs`：

```csharp
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class IntentTextTests
    {
        // ---------- IsUsableArg ----------

        [Test]
        public void 正常歌名可用()
        {
            Assert.IsTrue(IntentText.IsUsableArg("赤伶"));
            Assert.IsTrue(IntentText.IsUsableArg(" 山外小楼夜听雨 "));
            Assert.IsTrue(IntentText.IsUsableArg("Always Online"));
            Assert.IsTrue(IntentText.IsUsableArg("你和我 (You And Me)"));
        }

        [Test]
        public void 空白不可用()
        {
            Assert.IsFalse(IntentText.IsUsableArg(null));
            Assert.IsFalse(IntentText.IsUsableArg(""));
            Assert.IsFalse(IntentText.IsUsableArg("   "));
        }

        [Test]
        public void 超长不可用()
        {
            Assert.IsTrue(IntentText.IsUsableArg(new string('歌', 25)));
            Assert.IsFalse(IntentText.IsUsableArg(new string('歌', 26)));
        }

        [Test]
        public void 纯数字不可用()
        {
            Assert.IsFalse(IntentText.IsUsableArg("666"));
            Assert.IsFalse(IntentText.IsUsableArg("123456"));
        }

        [Test]
        public void 同一个字重复不可用()
        {
            Assert.IsFalse(IntentText.IsUsableArg("哈哈哈哈"));
            Assert.IsFalse(IntentText.IsUsableArg("？？？"));
            Assert.IsFalse(IntentText.IsUsableArg("。。。"));
            Assert.IsTrue(IntentText.IsUsableArg("哈"), "单字仍然可能是歌名，不拦");
        }

        [Test]
        public void 没有文字或数字的不可用()
        {
            Assert.IsFalse(IntentText.IsUsableArg("？！"));
            Assert.IsFalse(IntentText.IsUsableArg("😀😭"));
            Assert.IsFalse(IntentText.IsUsableArg("~!@#"));
        }

        // ---------- LooksLikeIntent ----------

        [Test]
        public void 预筛能认出点歌说法()
        {
            Assert.AreEqual(IntentKind.Song, IntentText.LooksLikeIntent("我想听点音乐"));
            Assert.AreEqual(IntentKind.Song, IntentText.LooksLikeIntent("来一首吧"));
        }

        [Test]
        public void 预筛能认出点舞说法()
        {
            Assert.AreEqual(IntentKind.Dance, IntentText.LooksLikeIntent("给我们跳舞看看"));
            Assert.AreEqual(IntentKind.Dance, IntentText.LooksLikeIntent("扭一个"));
        }

        [Test]
        public void 预筛能认出换角色说法()
        {
            Assert.AreEqual(IntentKind.Avatar, IntentText.LooksLikeIntent("变身给我们看看"));
            Assert.AreEqual(IntentKind.Avatar, IntentText.LooksLikeIntent("换成别的形象吧"));
        }

        [Test]
        public void 无关弹幕不触发预筛()
        {
            Assert.AreEqual(IntentKind.None, IntentText.LooksLikeIntent("今天天气不错"));
            Assert.AreEqual(IntentKind.None, IntentText.LooksLikeIntent("主播好可爱"));
            Assert.AreEqual(IntentKind.None, IntentText.LooksLikeIntent(""));
            Assert.AreEqual(IntentKind.None, IntentText.LooksLikeIntent(null));
        }

        [Test]
        public void 超过30字的长句不问LLM()
        {
            // 长句是聊天不是命令，问 LLM 只会白烧 token
            Assert.AreEqual(IntentKind.None, IntentText.LooksLikeIntent(new string('听', 31)));
        }

        [Test]
        public void 舞的判定优先于歌()
        {
            // 「跳舞」两个字里没有歌相关词，但词表顺序必须保证舞先判 ——
            // 返回值只用来决定「值不值得问 LLM」，具体类别由 LLM 定
            Assert.AreEqual(IntentKind.Dance, IntentText.LooksLikeIntent("跳舞"));
        }

        // ---------- TryParseIntentJson ----------

        [Test]
        public void 解析裸JSON()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson(
                "{\"intent\":\"song\",\"arg\":\"赤伶\"}", out var k, out var a));
            Assert.AreEqual(IntentKind.Song, k);
            Assert.AreEqual("赤伶", a);
        }

        [Test]
        public void 解析被markdown围栏包裹的JSON()
        {
            string raw = "```json\n{\"intent\": \"dance\", \"arg\": \"极乐净土\"}\n```";
            Assert.IsTrue(IntentText.TryParseIntentJson(raw, out var k, out var a));
            Assert.AreEqual(IntentKind.Dance, k);
            Assert.AreEqual("极乐净土", a);
        }

        [Test]
        public void 解析前后带废话的JSON()
        {
            string raw = "好的，我判断如下：{\"intent\":\"avatar\",\"arg\":\"小白\"} 希望有帮助！";
            Assert.IsTrue(IntentText.TryParseIntentJson(raw, out var k, out var a));
            Assert.AreEqual(IntentKind.Avatar, k);
            Assert.AreEqual("小白", a);
        }

        [Test]
        public void 解析单引号JSON()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson(
                "{'intent':'song','arg':'大鱼'}", out var k, out var a));
            Assert.AreEqual(IntentKind.Song, k);
            Assert.AreEqual("大鱼", a);
        }

        [Test]
        public void 缺arg字段时arg为空但解析成功()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson("{\"intent\":\"song\"}", out var k, out var a));
            Assert.AreEqual(IntentKind.Song, k);
            Assert.AreEqual("", a);
        }

        [Test]
        public void intent为none时解析成功且类别为None()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson(
                "{\"intent\":\"none\",\"arg\":\"\"}", out var k, out _));
            Assert.AreEqual(IntentKind.None, k);
        }

        [Test]
        public void 非JSON解析失败()
        {
            Assert.IsFalse(IntentText.TryParseIntentJson("我觉得他是想点歌", out _, out _));
            Assert.IsFalse(IntentText.TryParseIntentJson("", out _, out _));
            Assert.IsFalse(IntentText.TryParseIntentJson(null, out _, out _));
        }

        [Test]
        public void intent值非法时解析失败()
        {
            Assert.IsFalse(IntentText.TryParseIntentJson(
                "{\"intent\":\"唱歌\",\"arg\":\"赤伶\"}", out _, out _));
        }

        [Test]
        public void 解析带转义引号的arg()
        {
            Assert.IsTrue(IntentText.TryParseIntentJson(
                "{\"intent\":\"song\",\"arg\":\"说\\\"再见\\\"\"}", out _, out var a));
            Assert.AreEqual("说\"再见\"", a);
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

跑 EditMode 测试。预期：编译失败，`error CS0103: The name 'IntentText' does not exist`。

- [ ] **Step 3: 实现 IntentText**

新建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/IntentText.cs`：

```csharp
using System;
using System.Text;

namespace DouyinLive
{
    // 弹幕文本的三个纯判断。放在 Core 是为了能进 EditMode 测试，
    // 所以 JSON 也只能手写解析 —— Core 不许引用 Newtonsoft。
    public static class IntentText
    {
        public const int MaxArgLength = 25;        // 超过这个长度的不像歌名/角色名
        public const int MaxPrefilterLength = 30;  // 超过这个长度的是聊天不是命令

        // 词表只决定「这条弹幕值不值得花 1.5 秒问 LLM」，返回的具体类别不参与
        // 最终判定 —— 那是 LLM 的活。所以词表重叠（「换个歌」既含「换个」又含
        // 「歌」）最多让一条弹幕被多问一次，不会造成误触发。
        static readonly string[] DanceWords = { "跳舞", "舞", "扭一个", "来段舞" };
        static readonly string[] AvatarWords = { "换角色", "换个", "变身", "换成", "换一个" };
        static readonly string[] SongWords = { "听", "唱", "歌", "来一首", "来首", "点一首" };

        // 这段文本能不能当歌名/舞名/角色名用。挡掉接在追问后面的无意义弹幕，
        // 让槽位留着等真正的答案。
        public static bool IsUsableArg(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.Length > MaxArgLength) return false;

            // 「哈哈哈哈」「？？？」这类同字重复：是情绪不是答案
            if (s.Length >= 2)
            {
                bool allSame = true;
                for (int i = 1; i < s.Length; i++)
                    if (s[i] != s[0]) { allSame = false; break; }
                if (allSame) return false;
            }

            bool allDigit = true;
            bool hasWord = false;
            foreach (char c in s)
            {
                if (!char.IsDigit(c)) allDigit = false;
                if (char.IsLetterOrDigit(c)) hasWord = true;
            }
            if (allDigit) return false;   // 「666」
            // Emoji 是代理对，char.IsLetterOrDigit 对两半都返回 false，所以
            // 纯表情弹幕会在这里被挡下，不需要单独判 Emoji
            return hasWord;
        }

        public static IntentKind LooksLikeIntent(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return IntentKind.None;
            s = s.Trim();
            if (s.Length > MaxPrefilterLength) return IntentKind.None;

            if (ContainsAny(s, DanceWords)) return IntentKind.Dance;
            if (ContainsAny(s, AvatarWords)) return IntentKind.Avatar;
            if (ContainsAny(s, SongWords)) return IntentKind.Song;
            return IntentKind.None;
        }

        static bool ContainsAny(string s, string[] words)
        {
            foreach (var w in words)
                if (s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // 大模型经常在 JSON 外面套 ```json 围栏或前后加两句解释，所以不做严格解析，
        // 只把 intent / arg 两个键的值挖出来。返回 false = 没解出合法 intent。
        public static bool TryParseIntentJson(string raw, out IntentKind kind, out string arg)
        {
            kind = IntentKind.None;
            arg = "";
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string intent = ExtractValue(raw, "intent");
            if (intent == null) return false;

            switch (intent.Trim().ToLowerInvariant())
            {
                case "song":   kind = IntentKind.Song; break;
                case "dance":  kind = IntentKind.Dance; break;
                case "avatar": kind = IntentKind.Avatar; break;
                case "none":   kind = IntentKind.None; break;
                default: return false;
            }

            string a = ExtractValue(raw, "arg");
            arg = a == null ? "" : a.Trim();
            return true;
        }

        static string ExtractValue(string s, string key)
        {
            int k = IndexOfKey(s, key);
            if (k < 0) return null;
            int colon = s.IndexOf(':', k);
            if (colon < 0) return null;

            int i = colon + 1;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) return null;

            char quote = s[i];
            if (quote != '"' && quote != '\'') return null;
            i++;

            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                if (c == quote) return sb.ToString();
                sb.Append(c);
                i++;
            }
            return null;   // 引号没闭合
        }

        static int IndexOfKey(string s, string key)
        {
            int i = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i >= 0) return i;
            return s.IndexOf("'" + key + "'", StringComparison.Ordinal);
        }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

跑 EditMode 测试。预期：`failed="0"`，`total` 从 63 增加到 83。

- [ ] **Step 5: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/IntentText.cs"* \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/IntentTextTests.cs"*
git commit -m "feat(douyin-live): add argument validation, intent prefilter, and JSON extraction

The prefilter's returned kind only decides whether a danmaku is worth a
1.5s classifier call — the model decides the actual intent, so overlapping
word lists cost at most one extra call. JSON is hand-parsed because Core
may not reference Newtonsoft, and models wrap their answers in fences and
prose anyway."
```

---

### Task 3: 配置字段与默认规则集

**Files:**
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/TriggerRules.cs`（`TriggerGlobal` 加两个字段、`TriggerRule` 加 `askPrompt`、`Defaults()` 里两条规则升级）
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/TriggerConfigStore.cs:107-120`（文件头注释里的效果清单）
- Test: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/TriggerRulesTests.cs`（追加用例）

**Interfaces:**
- Consumes: 无
- Produces: `TriggerGlobal.slotWindowSeconds`（`float`，默认 `30f`）、`TriggerGlobal.intentFallbackEnabled`（`bool`，默认 `true`）、`TriggerRule.askPrompt`（`string`，默认 `""`）

- [ ] **Step 1: 写失败的测试**

在 `Tests/TriggerRulesTests.cs` 的类里追加：

```csharp
        [Test]
        public void 新增的全局字段有合理默认值()
        {
            var g = new TriggerGlobal();
            Assert.AreEqual(30f, g.slotWindowSeconds);
            Assert.IsTrue(g.intentFallbackEnabled);
        }

        [Test]
        public void 规则的追问文案默认为空表示用内置文案()
        {
            Assert.AreEqual("", new TriggerRule().askPrompt);
        }

        [Test]
        public void 默认规则集里点舞和换角色都支持追问()
        {
            var cfg = TriggerConfig.Defaults();
            var dance = cfg.rules.Find(r => r.id == "reqdance");
            var swap = cfg.rules.Find(r => r.id == "swap");
            Assert.IsNotNull(dance);
            Assert.IsNotNull(swap);
            Assert.Contains("dance:request", dance.effects);
            Assert.Contains("swapAvatar:request", swap.effects);
        }

        [Test]
        public void 默认规则集里点歌仍然是request()
        {
            var song = TriggerConfig.Defaults().rules.Find(r => r.id == "song");
            Assert.IsNotNull(song);
            Assert.Contains("song:request", song.effects);
        }
```

如果 `TriggerRulesTests.cs` 顶部没有 `using System.Collections.Generic;`，`List.Find` 仍然可用（`List<T>.Find` 是实例方法），不需要额外 using。

- [ ] **Step 2: 跑测试确认失败**

跑 EditMode 测试。预期：编译失败，`error CS1061: 'TriggerGlobal' does not contain a definition for 'slotWindowSeconds'`。

- [ ] **Step 3: 加字段**

在 `Core/TriggerRules.cs` 的 `TriggerGlobal` 里，`giftUseTotalValue` 那一行后面加：

```csharp
        // 角色问完「想听什么歌呀」之后，等这个观众回答的秒数。<= 0 关闭追问功能。
        public float slotWindowSeconds = 30f;
        // 关键词没命中时，是否花 1.5 秒问一次大模型这条弹幕想干嘛
        public bool intentFallbackEnabled = true;
```

在 `TriggerRule` 里，`sayFallback` 那一行后面加：

```csharp
        public string askPrompt = "";        // 追问文案，留空用内置默认；支持 {u}
```

- [ ] **Step 4: 升级默认规则集**

在 `Core/TriggerRules.cs` 的 `Defaults()` 里，把这两行：

```csharp
                    Cd(Chat("swap", new[] { "换角色", "换装", "换个人" }, new[] { "swapAvatar" }, "L3"), 60f, 180f),
                    Cd(Chat("reqdance", new[] { "点舞", "跳舞", "来一段" }, new[] { "dance:random" }, "L3"), 90f, 300f),
```

改成：

```csharp
                    Cd(Chat("swap", new[] { "换角色", "换装", "换个人" }, new[] { "swapAvatar:request" }, "L3"), 60f, 180f),
                    Cd(Chat("reqdance", new[] { "点舞", "跳舞", "来一段" }, new[] { "dance:request" }, "L3"), 90f, 300f),
```

只影响新生成的配置文件；已存在的 `douyin_triggers.json` 不会被改写，老的 `swapAvatar` / `dance:random` 行为保持不变。

- [ ] **Step 5: 更新配置文件的头注释**

在 `TriggerConfigStore.cs` 的 `WriteDefaultsWithComments()` 里，把这三行：

```csharp
"//   dance:random | dance:<舞名> | dance:builtin\n" +
"//   song:<歌名> | song:request\n" +
"//   swapAvatar | outfit:random | outfit:<配件名>\n" +
```

改成：

```csharp
"//   dance:random | dance:<舞名> | dance:builtin | dance:request | dance:ask\n" +
"//   song:<歌名> | song:request | song:ask\n" +
"//   swapAvatar | swapAvatar:<角色名> | swapAvatar:request | swapAvatar:ask\n" +
"//   outfit:random | outfit:<配件名>\n" +
"//   request = 先从弹幕正文取名字，取不到就追问并等这个观众下一句回答\n" +
"//   ask     = 不看正文，直接追问\n" +
"//   规则可加 askPrompt 自定义追问文案（支持 {u}），留空用内置默认\n" +
```

同时在 global 那段注释后面加一行：

```csharp
"//   slotWindowSeconds  追问后等观众回答的秒数，0 = 关闭追问\n" +
"//   intentFallbackEnabled  关键词没中时是否问大模型判意图\n" +
```

（加在 `"//   l2MinInterval / l3MinInterval  跨规则的层级总闸\n" +` 这一行之后。）

- [ ] **Step 6: 跑测试确认通过**

跑 EditMode 测试。预期：`failed="0"`，`total` 从 83 增加到 87。

- [ ] **Step 7: 编译验证**

跑「通用命令」里的编译验证。预期：`another Unity instance` 计数 0，`error CS` 计数 0。

- [ ] **Step 8: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/TriggerRules.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/TriggerConfigStore.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/TriggerRulesTests.cs"
git commit -m "feat(douyin-live): add slot window, intent fallback, and askPrompt config

Defaults() now ships dance:request and swapAvatar:request so a fresh
install gets follow-up questions. Existing douyin_triggers.json files are
never rewritten, and bare swapAvatar / dance:random keep today's behaviour."
```

---

### Task 4: 规则反查与名字匹配

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/RuleQuery.cs`
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/NameMatch.cs`
- Test: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/RuleQueryTests.cs`

**Interfaces:**
- Consumes: `IntentKind`（Task 1）、`TriggerRule.askPrompt`（Task 3）
- Produces: `static class RuleQuery`，含 `static TriggerRule FindByEffectPrefix(TriggerConfig cfg, string prefix)`、`static TriggerRule WithEffect(TriggerRule src, string effect)`、`static string EffectPrefix(IntentKind kind)`、`static string BuildEffect(IntentKind kind, string arg)`；`static class NameMatch`，含 `static int PickIndex(IReadOnlyList<string> names, string query)`

- [ ] **Step 1: 写失败的测试**

新建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/RuleQueryTests.cs`：

```csharp
using System.Collections.Generic;
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class RuleQueryTests
    {
        static TriggerRule Rule(string id, string level, params string[] effects)
            => new TriggerRule { id = id, source = "chat", level = level, effects = new List<string>(effects) };

        static TriggerConfig Cfg(params TriggerRule[] rules)
            => new TriggerConfig { global = new TriggerGlobal(), rules = new List<TriggerRule>(rules) };

        // ---------- FindByEffectPrefix ----------

        [Test]
        public void 按前缀找到第一条命中的规则()
        {
            var cfg = Cfg(Rule("pat", "L1", "anim:Headpat"),
                          Rule("song", "L1", "song:request"),
                          Rule("song2", "L1", "song:赤伶"));
            Assert.AreEqual("song", RuleQuery.FindByEffectPrefix(cfg, "song:").id);
        }

        [Test]
        public void 跳过被禁用的规则()
        {
            var off = Rule("song", "L1", "song:request");
            off.enabled = false;
            var cfg = Cfg(off, Rule("song2", "L1", "song:赤伶"));
            Assert.AreEqual("song2", RuleQuery.FindByEffectPrefix(cfg, "song:").id);
        }

        [Test]
        public void 跳过非弹幕来源的规则()
        {
            var gift = Rule("gift3", "L3", "song:request");
            gift.source = "gift";
            var cfg = Cfg(gift);
            Assert.IsNull(RuleQuery.FindByEffectPrefix(cfg, "song:"));
        }

        [Test]
        public void 没有对应规则时返回null()
        {
            var cfg = Cfg(Rule("pat", "L1", "anim:Headpat"));
            Assert.IsNull(RuleQuery.FindByEffectPrefix(cfg, "song:"));
            Assert.IsNull(RuleQuery.FindByEffectPrefix(null, "song:"));
        }

        [Test]
        public void 无冒号的swapAvatar前缀能命中()
        {
            var cfg = Cfg(Rule("swap", "L3", "swapAvatar"));
            Assert.AreEqual("swap", RuleQuery.FindByEffectPrefix(cfg, "swapAvatar").id);

            var cfg2 = Cfg(Rule("swap", "L3", "swapAvatar:request"));
            Assert.AreEqual("swap", RuleQuery.FindByEffectPrefix(cfg2, "swapAvatar").id);
        }

        // ---------- WithEffect ----------

        [Test]
        public void 副本保留id和限流参数只换效果()
        {
            var src = Rule("swap", "L3", "swapAvatar:request");
            src.cooldown = 60f;
            src.perUserCooldown = 180f;
            src.askPrompt = "换成谁呀";
            src.sayFallback = "兜底";

            var copy = RuleQuery.WithEffect(src, "swapAvatar:小白");

            // id 相同是全部设计的基石：TriggerLimiter 按 id 记账，副本因此
            // 和原规则共用冷却账，不会绕开限流
            Assert.AreEqual("swap", copy.id);
            Assert.AreEqual("L3", copy.level);
            Assert.AreEqual(60f, copy.cooldown);
            Assert.AreEqual(180f, copy.perUserCooldown);
            Assert.AreEqual("换成谁呀", copy.askPrompt);
            Assert.AreEqual("兜底", copy.sayFallback);
            Assert.AreEqual(1, copy.effects.Count);
            Assert.AreEqual("swapAvatar:小白", copy.effects[0]);
        }

        [Test]
        public void 副本的pick强制为all()
        {
            var src = Rule("like30", "L1", "anim:a", "anim:b");
            src.pick = "random";
            var copy = RuleQuery.WithEffect(src, "song:赤伶");
            Assert.AreEqual("all", copy.pick,
                "副本只有一个效果，pick=random 会让它有几率不执行");
        }

        [Test]
        public void 副本不修改源规则()
        {
            var src = Rule("song", "L1", "song:request");
            RuleQuery.WithEffect(src, "song:赤伶");
            Assert.AreEqual(1, src.effects.Count);
            Assert.AreEqual("song:request", src.effects[0]);
        }

        [Test]
        public void 源规则为null时返回null()
        {
            Assert.IsNull(RuleQuery.WithEffect(null, "song:赤伶"));
        }

        // ---------- EffectPrefix / BuildEffect ----------

        [Test]
        public void 前缀与效果串对得上()
        {
            Assert.AreEqual("song:", RuleQuery.EffectPrefix(IntentKind.Song));
            Assert.AreEqual("dance:", RuleQuery.EffectPrefix(IntentKind.Dance));
            Assert.AreEqual("swapAvatar", RuleQuery.EffectPrefix(IntentKind.Avatar));
            Assert.IsNull(RuleQuery.EffectPrefix(IntentKind.None));

            Assert.AreEqual("song:赤伶", RuleQuery.BuildEffect(IntentKind.Song, "赤伶"));
            Assert.AreEqual("dance:极乐净土", RuleQuery.BuildEffect(IntentKind.Dance, "极乐净土"));
            Assert.AreEqual("swapAvatar:小白", RuleQuery.BuildEffect(IntentKind.Avatar, "小白"));
            Assert.AreEqual("song:ask", RuleQuery.BuildEffect(IntentKind.Song, "ask"));
            Assert.IsNull(RuleQuery.BuildEffect(IntentKind.None, "x"));
        }

        // ---------- NameMatch ----------

        [Test]
        public void 名字精确匹配优先()
        {
            var names = new List<string> { "小白兔", "小白", "白" };
            Assert.AreEqual(1, NameMatch.PickIndex(names, "小白"));
        }

        [Test]
        public void 名字大小写不敏感()
        {
            var names = new List<string> { "Miku", "Rin" };
            Assert.AreEqual(0, NameMatch.PickIndex(names, "miku"));
        }

        [Test]
        public void 精确没中时双向子串匹配()
        {
            var names = new List<string> { "初音未来 V4X" };
            Assert.AreEqual(0, NameMatch.PickIndex(names, "初音"));      // 查询是名字的子串
            Assert.AreEqual(0, NameMatch.PickIndex(names, "初音未来 V4X 模型"));  // 名字是查询的子串
        }

        [Test]
        public void 匹配不上返回负一()
        {
            var names = new List<string> { "小白", "小黑" };
            Assert.AreEqual(-1, NameMatch.PickIndex(names, "小红"));
            Assert.AreEqual(-1, NameMatch.PickIndex(names, ""));
            Assert.AreEqual(-1, NameMatch.PickIndex(names, null));
            Assert.AreEqual(-1, NameMatch.PickIndex(null, "小白"));
            Assert.AreEqual(-1, NameMatch.PickIndex(new List<string>(), "小白"));
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

跑 EditMode 测试。预期：编译失败，`error CS0103: The name 'RuleQuery' does not exist`。

- [ ] **Step 3: 实现 RuleQuery**

新建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/RuleQuery.cs`：

```csharp
using System;
using System.Collections.Generic;

namespace DouyinLive
{
    // 「借用某条规则的限流参数、只换执行参数」这套做法的两个零件。
    //
    // 成立的前提是 TriggerLimiter 按 rule.id 字符串记账（见 TriggerLimiter.RuleKey），
    // 所以带同一个 id 的副本和原规则共用全部四本冷却账 —— 换句话说，用副本执行
    // 不会绕开任何一道限流闸。ActionDirector.Submit 只读 effects / level / pick，
    // 接受任何 TriggerRule 实例，因此这两个零件不需要动限流层和仲裁层。
    public static class RuleQuery
    {
        // 找出「负责某个玩法」的那条弹幕规则。按数组顺序取第一条，
        // 和 TriggerMatcher 的「先写的优先」保持一致。
        public static TriggerRule FindByEffectPrefix(TriggerConfig cfg, string prefix)
        {
            if (cfg == null || cfg.rules == null || string.IsNullOrEmpty(prefix)) return null;
            foreach (var r in cfg.rules)
            {
                if (r == null || !r.enabled || r.source != "chat" || r.effects == null) continue;
                foreach (var e in r.effects)
                {
                    if (string.IsNullOrEmpty(e)) continue;
                    if (e.Trim().StartsWith(prefix, StringComparison.Ordinal)) return r;
                }
            }
            return null;
        }

        public static TriggerRule WithEffect(TriggerRule src, string effect)
        {
            if (src == null) return null;
            return new TriggerRule
            {
                id = src.id,                       // 必须一致，否则冷却账会分家
                enabled = src.enabled,
                source = src.source,
                keywords = src.keywords,
                regex = src.regex,
                everyN = src.everyN,
                milestone = src.milestone,
                giftName = src.giftName,
                minDiamond = src.minDiamond,
                maxDiamond = src.maxDiamond,
                minCount = src.minCount,
                effects = new List<string> { effect },
                pick = "all",                      // 副本只有一个效果，random 会让它有几率不执行
                level = src.level,
                cooldown = src.cooldown,
                perUserCooldown = src.perUserCooldown,
                sayFallback = src.sayFallback,
                askPrompt = src.askPrompt
            };
        }

        // swapAvatar 没有冒号（裸 swapAvatar 是合法效果），所以前缀不能写死带冒号
        public static string EffectPrefix(IntentKind kind)
        {
            switch (kind)
            {
                case IntentKind.Song:   return "song:";
                case IntentKind.Dance:  return "dance:";
                case IntentKind.Avatar: return "swapAvatar";
                default: return null;
            }
        }

        public static string BuildEffect(IntentKind kind, string arg)
        {
            switch (kind)
            {
                case IntentKind.Song:   return "song:" + arg;
                case IntentKind.Dance:  return "dance:" + arg;
                case IntentKind.Avatar: return "swapAvatar:" + arg;
                default: return null;
            }
        }
    }
}
```

- [ ] **Step 4: 实现 NameMatch**

新建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/NameMatch.cs`：

```csharp
using System;
using System.Collections.Generic;

namespace DouyinLive
{
    // 观众打的名字和库里的名字很难完全一致（「初音」对「初音未来 V4X」），
    // 先精确再双向子串。和 AvatarDancePlayer.FindIndexByTitleFuzzy 同一套语义，
    // 但那个在 Unity 层且只服务舞包，这里给角色库用。
    public static class NameMatch
    {
        public static int PickIndex(IReadOnlyList<string> names, string query)
        {
            if (names == null || names.Count == 0 || string.IsNullOrWhiteSpace(query)) return -1;
            string q = query.Trim();

            for (int i = 0; i < names.Count; i++)
            {
                if (string.IsNullOrEmpty(names[i])) continue;
                if (string.Equals(names[i].Trim(), q, StringComparison.OrdinalIgnoreCase)) return i;
            }

            for (int i = 0; i < names.Count; i++)
            {
                if (string.IsNullOrEmpty(names[i])) continue;
                string n = names[i].Trim();
                if (n.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return i;
                if (q.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            }
            return -1;
        }
    }
}
```

- [ ] **Step 5: 跑测试确认通过**

跑 EditMode 测试。预期：`failed="0"`，`total` 从 87 增加到 101。

- [ ] **Step 6: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/RuleQuery.cs"* \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/NameMatch.cs"* \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/RuleQueryTests.cs"*
git commit -m "feat(douyin-live): add rule lookup by effect prefix and parameterised rule copies

The copy keeps the original id, which is what makes the whole approach
safe: TriggerLimiter keys its four ledgers off rule.id, so executing
through a copy shares every cooldown with the rule it came from instead
of bypassing them."
```

---

### Task 5: 效果层——追问、指名点舞、指名换角色

**Files:**
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/EffectRegistry.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/RewardService.cs:92-136`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs:342-345`

**与 spec 的两处偏差**（写计划时发现更好的做法，spec 已同步更新）：
1. spec 只写了 `request`，本计划另加 `:ask` —— LLM 判出「想听歌但没说歌名」时需要一个「不看正文、直接追问」的效果。若复用 `request`，`StripKeywords` 会把「我想听点音乐」整句当成歌名去搜。
2. `IntentResolver` 不再自带 `Enabled` 字段，开关改由 `TriggerRouter.IntentFallbackEnabled` 现读 —— 配置热重载会换掉 `Config` 对象，启动时抄一份就失效了。

**Interfaces:**
- Consumes: `IntentKind`（Task 1）、`NameMatch.PickIndex`（Task 4）、`TriggerRule.askPrompt`（Task 3）
- Produces: 效果 ID `song:ask` / `dance:request` / `dance:ask` / `swapAvatar:<名字>` / `swapAvatar:request` / `swapAvatar:ask`；`RewardService.SwitchAvatarByName(string userName, string wanted)`；`DouyinLiveManager.SwapAvatarFromTrigger(string userName, string wanted)`；`EffectRegistry` 通过 `GetComponent<TriggerRouter>()` 调用 `TriggerRouter.OpenSlot(DouyinEvent ev, IntentKind kind, string ruleId)`（该方法在 Task 6 实现——**本任务先不要调用它，见 Step 4**）

**本任务无法写 EditMode 测试**：`EffectRegistry` 和 `RewardService` 都要求 Unity 场景、`VRMLoader`、`Animator`。验证手段是编译干净 + Task 6 之后的手工清单。不要为了凑测试而写断言什么都不断言的测试。

- [ ] **Step 1: 在 EffectRegistry 里加追问辅助**

在 `EffectRegistry.cs` 的 `// ---------- song ----------` 这一节之前（`PlayBuiltinDance` 方法之后）插入：

```csharp
        // ---------- 追问 ----------

        TriggerRouter routerCache;
        // TriggerRouter 一定和本组件在同一个 GameObject 上（它标了
        // [RequireComponent(typeof(EffectRegistry))]），惰性取一次缓存住
        TriggerRouter Router
        {
            get
            {
                if (routerCache == null) routerCache = GetComponent<TriggerRouter>();
                return routerCache;
            }
        }

        static string DefaultAsk(IntentKind kind)
        {
            // 措辞刻意引导「直接把名字发出来」——现在直接发真的能接上了，
            // 再教观众「发 点歌加歌名」等于把新能力藏起来
            if (kind == IntentKind.Dance)  return "{u} 想看哪支舞呀？把舞名发出来~";
            if (kind == IntentKind.Avatar) return "{u} 想让我换成谁呀？说个名字~";
            return "{u} 想听什么歌呀？直接把歌名发出来就行~";
        }

        // 问一句 + 记下「在等这个人回答」。返回 true 表示确实说了话，不算空炮。
        bool AskAndOpenSlot(IntentKind kind, EffectContext ctx)
        {
            string tpl = ctx.Rule != null && !string.IsNullOrWhiteSpace(ctx.Rule.askPrompt)
                ? ctx.Rule.askPrompt
                : DefaultAsk(kind);
            if (!Say(FillPlaceholders(tpl, ctx.Event))) return false;

            var r = Router;
            if (r != null && ctx.Event != null && ctx.Rule != null)
                r.OpenSlot(ctx.Event, kind, ctx.Rule.id);
            return true;
        }
```

- [ ] **Step 2: 改 PlaySong**

把 `EffectRegistry.cs` 里 `PlaySong` 开头这一段：

```csharp
            if (arg != "request") { s.RequestSong(arg, name); return true; }

            // song:request 从弹幕正文里剥掉命中的关键词，剩下的就是歌名
            string title = StripKeywords(ctx.Event?.Content ?? "", ctx.Rule);
            if (string.IsNullOrWhiteSpace(title))
            {
                Say($"{name} 想点什么歌呀？发 点歌加歌名 哦~");
                return true;
            }
```

改成：

```csharp
            if (arg == "ask") return AskAndOpenSlot(IntentKind.Song, ctx);
            if (arg != "request") { s.RequestSong(arg, name); return true; }

            // song:request 从弹幕正文里剥掉命中的关键词，剩下的就是歌名
            string title = StripKeywords(ctx.Event?.Content ?? "", ctx.Rule);
            // 只问不记是这个功能原来的 bug：观众答的歌名下一轮匹配不到任何规则，
            // 直接掉进 AI 闲聊。现在问的同时开一个槽位等他回答。
            if (string.IsNullOrWhiteSpace(title)) return AskAndOpenSlot(IntentKind.Song, ctx);
```

- [ ] **Step 3: 改 PlayDance 支持指名与追问**

把 `PlayDance` 的签名和实现整体替换为：

```csharp
        bool PlayDance(string arg, EffectContext ctx)
        {
            if (arg == "ask") return AskAndOpenSlot(IntentKind.Dance, ctx);

            var d = Dance;

            if (arg == "request")
            {
                string title = StripKeywords(ctx.Event?.Content ?? "", ctx.Rule);
                if (string.IsNullOrWhiteSpace(title)) return AskAndOpenSlot(IntentKind.Dance, ctx);

                if (d != null)
                {
                    int i = d.FindIndexByTitleFuzzy(title);
                    if (i >= 0 && d.PlayIndex(i)) { Say($"好嘞，{title} 来咯！"); return true; }
                }
                // 点名的舞包没有就随便来一支，别让观众的请求石沉大海
                Say($"曲库里还没有 {title}，先随便来一支吧~");
                return PlayDance("random", ctx);
            }

            if (arg == "builtin" || d == null || d.EntryCount <= 0) return PlayBuiltinDance();

            if (arg == "random")
            {
                if (danceDirector == null) danceDirector = FindFirstObjectByType<DanceDirector>();
                if (danceDirector != null && danceDirector.PlayRandom()) return true;
                return PlayBuiltinDance();
            }

            int idx = d.FindIndexByTitleFuzzy(arg);
            if (idx < 0) { Debug.LogWarning($"[Effect] 曲库里没有舞包: {arg}"); return false; }
            if (d.PlayIndex(idx)) return true;
            return PlayBuiltinDance();
        }
```

- [ ] **Step 4: 改 SwapAvatar 支持参数**

把 `SwapAvatar` 整体替换为：

```csharp
        bool SwapAvatar(string arg, EffectContext ctx)
        {
            string name = string.IsNullOrEmpty(ctx.Event?.Nickname) ? "朋友" : ctx.Event.Nickname;
            var mgr = DouyinLiveManager.Instance;
            if (mgr == null) { WarnOnce("swapAvatar"); return false; }

            // 裸 swapAvatar 保持老行为：随机换。老配置不能因为这次改动变样。
            if (string.IsNullOrEmpty(arg)) { mgr.SwapAvatarFromTrigger(name); return true; }

            if (arg == "ask") return AskAndOpenSlot(IntentKind.Avatar, ctx);

            string wanted = arg;
            if (arg == "request")
            {
                wanted = StripKeywords(ctx.Event?.Content ?? "", ctx.Rule);
                if (string.IsNullOrWhiteSpace(wanted)) return AskAndOpenSlot(IntentKind.Avatar, ctx);
            }
            mgr.SwapAvatarFromTrigger(name, wanted);
            return true;
        }
```

- [ ] **Step 5: 改效果分发**

把 `Execute` 里这两行：

```csharp
                case "dance":     return PlayDance(arg);
                ...
                case "swapAvatar": return SwapAvatar(ctx);
```

改成：

```csharp
                case "dance":     return PlayDance(arg, ctx);
                ...
                case "swapAvatar": return SwapAvatar(arg, ctx);
```

- [ ] **Step 6: 在 RewardService 里抽出角色库读取**

在 `RewardService.cs` 的 `// ---------- 换角色 ----------` 之后、`SwitchRandomAvatar` 之前插入：

```csharp
        // 读 avatars.json，把可切换的角色填进 names/paths（下标一一对应）。
        // 排除当前正在用的角色和文件已经不在的条目。
        void LoadAvatarLibrary(List<string> names, List<string> paths)
        {
            string current = SaveLoadHandler.Instance != null
                ? SaveLoadHandler.Instance.data.selectedModelPath : "";
            try
            {
                string jsonPath = System.IO.Path.Combine(Application.persistentDataPath, "avatars.json");
                if (!System.IO.File.Exists(jsonPath)) return;

                var entries = Newtonsoft.Json.JsonConvert.DeserializeObject<List<AvatarLibraryMenu.AvatarEntry>>(
                    System.IO.File.ReadAllText(jsonPath));
                if (entries == null) return;

                foreach (var e in entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.filePath)) continue;
                    if (e.filePath == current || !System.IO.File.Exists(e.filePath)) continue;
                    names.Add(string.IsNullOrEmpty(e.displayName)
                        ? System.IO.Path.GetFileNameWithoutExtension(e.filePath)
                        : e.displayName);
                    paths.Add(e.filePath);
                }
            }
            catch (System.Exception ex)
            { Debug.LogWarning("[RewardService] read avatars.json failed: " + ex.Message); }
        }
```

- [ ] **Step 7: 让 SwitchRandomAvatar 复用它**

把 `SwitchRandomAvatar` 里从 `if (vrmLoader == null) vrmLoader = ...` 到 `if (!string.IsNullOrEmpty(current)) candidates.Add("");` 这一整段（原来内联读 avatars.json 的代码）替换为：

```csharp
            if (vrmLoader == null) vrmLoader = UnityEngine.Object.FindFirstObjectByType<VRMLoader>();
            if (vrmLoader == null) return;

            var names = new List<string>();
            var candidates = new List<string>();
            LoadAvatarLibrary(names, candidates);

            // 默认模型也算一个候选（当前不是默认模型时）
            string current = SaveLoadHandler.Instance != null
                ? SaveLoadHandler.Instance.data.selectedModelPath : "";
            if (!string.IsNullOrEmpty(current)) candidates.Add("");
```

方法余下部分（候选为空时的提示、冷却写回、`Speech.Enqueue`、`NormalizeNextAvatarHeight`、`ActivateDefaultModel` / `LoadVRM`）保持不变。

- [ ] **Step 8: 实现 SwitchAvatarByName**

在 `SwitchRandomAvatar` 之后插入：

```csharp
        // 指名换角色：按 avatars.json 里的 displayName 模糊匹配。
        // 匹配不上不是失败——说一句然后随机换，观众的请求至少有回应。
        public void SwitchAvatarByName(string userName, string wanted)
        {
            if (string.IsNullOrWhiteSpace(wanted)) { SwitchRandomAvatar(userName); return; }

            var names = new List<string>();
            var paths = new List<string>();
            LoadAvatarLibrary(names, paths);

            int idx = NameMatch.PickIndex(names, wanted);
            if (idx < 0)
            {
                Speech?.Enqueue($"衣柜里没有 {wanted} 这个角色哦，随便换一个吧~",
                                SpeechPipeline.Priority.AIReply, 20f);
                SwitchRandomAvatar(userName);
                return;
            }

            if (Time.unscaledTime - lastSwitchAt < SwitchCooldown)
            {
                Speech?.Enqueue("刚换过啦，让我先穿一会儿这身嘛~", SpeechPipeline.Priority.AIReply, 20f);
                return;
            }
            if (vrmLoader == null) vrmLoader = UnityEngine.Object.FindFirstObjectByType<VRMLoader>();
            if (vrmLoader == null) return;

            lastSwitchAt = Time.unscaledTime;
            Speech?.Enqueue($"{userName} 想看 {names[idx]} 是吧？看我变身！",
                            SpeechPipeline.Priority.GiftThanks, 20f);
            DouyinLiveManager.Instance?.NormalizeNextAvatarHeight();
            vrmLoader.LoadVRM(paths[idx]);
        }
```

- [ ] **Step 9: 给 DouyinLiveManager 加重载**

把 `DouyinLiveManager.cs:342-345` 的：

```csharp
        // 供 EffectRegistry 的 swapAvatar 效果调用
        public void SwapAvatarFromTrigger(string userName)
        {
            reward.SwitchRandomAvatar(userName);
        }
```

改成：

```csharp
        // 供 EffectRegistry 的 swapAvatar 效果调用
        public void SwapAvatarFromTrigger(string userName)
        {
            reward.SwitchRandomAvatar(userName);
        }

        // swapAvatar:<角色名> —— 名字为空或匹配不上时退回随机换
        public void SwapAvatarFromTrigger(string userName, string wanted)
        {
            if (string.IsNullOrWhiteSpace(wanted)) reward.SwitchRandomAvatar(userName);
            else reward.SwitchAvatarByName(userName, wanted);
        }
```

- [ ] **Step 10: 临时桩掉 OpenSlot 以便本任务能编译**

`TriggerRouter.OpenSlot` 要到 Task 6 才实现。为了本任务能独立编译通过，先在 `TriggerRouter.cs` 的 `TryHandle` 之前加一个最小实现（Task 6 会把它补全）：

```csharp
        public IntentSlots Slots { get; } = new IntentSlots();

        public void OpenSlot(DouyinEvent ev, IntentKind kind, string ruleId)
        {
            if (ev == null) return;
            Slots.Open(ev.UserId, ev.Nickname, kind, ruleId);
            if (debugLog) Debug.Log($"[Triggers] 为 {ev.Nickname} 开了 {kind} 追问槽位");
        }
```

并在 `Awake()` 里 `limiter.Now = () => Time.unscaledTime;` 那一行后面加：

```csharp
            Slots.Now = () => Time.unscaledTime;
```

- [ ] **Step 11: 编译验证**

跑「通用命令」里的编译验证。预期：`another Unity instance` 计数 0，`error CS` 计数 0。

- [ ] **Step 12: 跑测试确认没打破现有用例**

跑 EditMode 测试。预期：`failed="0"`，`total="101"`。

- [ ] **Step 13: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/EffectRegistry.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/RewardService.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/TriggerRouter.cs"
git commit -m "feat(douyin-live): make song, dance, and avatar effects ask and take names

song:request used to ask 'which song?' and return without recording
anything, so the answer matched no rule. It now opens a slot. dance and
swapAvatar previously ignored the danmaku body entirely; they can now be
given a name, ask for one, or fall back to random when the name misses."
```

---

### Task 6: 路由层——槽位补全与 Route 改造

**Files:**
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/TriggerRouter.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs:252-302`

**Interfaces:**
- Consumes: `IntentSlots`（Task 1）、`IntentText.IsUsableArg`（Task 2）、`TriggerGlobal.slotWindowSeconds`（Task 3）、`RuleQuery.WithEffect` / `BuildEffect` / `FindByEffectPrefix` / `EffectPrefix`（Task 4）、`TriggerRouter.OpenSlot`（Task 5 已加）
- Produces: `TriggerRouter.TryFillSlot(DouyinEvent ev) -> bool`、`TriggerRouter.TryHandleIntent(DouyinEvent ev, IntentKind kind, string arg) -> bool`、`TriggerRouter.IntentFallbackEnabled -> bool`、`DouyinLiveManager.HandleChatLegacy(DouyinEvent ev)`

**本任务无法写 EditMode 测试**（`TriggerRouter` 和 `DouyinLiveManager` 都是 MonoBehaviour 且依赖场景）。验证是编译干净 + 手工清单第 1~8 条。

- [ ] **Step 1: 让槽位窗口跟着配置走**

在 `TriggerRouter.cs` 的 `Awake()` 里，`Config = TriggerConfigStore.LoadOrCreate();` 之后加：

```csharp
            SyncSlotWindow();
```

在 `DoReload()` 的成功分支里，`Config = cfg;` 之后加：

```csharp
                SyncSlotWindow();
```

在 `ResetSession()` 里加：

```csharp
            Slots.Reset();
```

在 `OnDestroy()` 之前加：

```csharp
        void SyncSlotWindow()
        {
            if (Config != null && Config.global != null)
                Slots.Window = Config.global.slotWindowSeconds;
        }

        // 热重载会换掉 Config，所以这个开关要现读而不是启动时抄一份
        public bool IntentFallbackEnabled
        {
            get { return Config != null && Config.global != null && Config.global.intentFallbackEnabled; }
        }
```

在 `Tick()` 的 `limiter.PruneUsers(600f);` 那一行后面加：

```csharp
                Slots.Prune();
```

- [ ] **Step 2: 实现 TryFillSlot 和 TryHandleIntent**

在 `TriggerRouter.cs` 的 `TryHandle` 方法之后追加：

```csharp
        // 观众在回答角色刚才的追问。返回 true = 已消费。
        public bool TryFillSlot(DouyinEvent ev)
        {
            if (ev == null || Config == null || ev.Type != DouyinMsgType.Chat) return false;
            if (!Slots.TryPeek(ev.UserId, out var slot)) return false;

            string arg = (ev.Content ?? "").Trim();
            // 通不过校验时刻意不 Take：槽位连同开槽时间原样留着，这条弹幕正常
            // 走闲聊，观众还有机会补答。取出来再放回去会刷新时间戳，连发几个
            // 「666」就能把 30 秒窗口无限续期。
            if (!IntentText.IsUsableArg(arg)) return false;

            var rule = FindRuleById(slot.RuleId);
            if (rule == null || !rule.enabled)
            {
                // 主播在追问期间把规则删了/禁用了：丢掉槽位，按普通弹幕处理
                Slots.Take(ev.UserId);
                return false;
            }

            string effect = RuleQuery.BuildEffect(slot.Kind, arg);
            if (effect == null) { Slots.Take(ev.UserId); return false; }

            Slots.Take(ev.UserId);

            // 刻意不过限流闸。追问的两轮是一次请求的两半，开槽那一次已经过闸
            // 并记账了。真收第二次费的话，swap 的 60 秒规则冷却和 45 秒 L3 间隔
            // 会把 30 秒窗口内的回答全部拦死，功能等于不存在。滥用也不成立：
            // 开槽必须先过闸，一个槽只能被取走一次，净速率和一次命中完全相同。
            bool executed = director.Submit(RuleQuery.WithEffect(rule, effect), ev, Config.global);
            if (debugLog)
                Debug.Log($"[Triggers] 槽位补全 {slot.Kind}:{arg} → {(executed ? "已执行" : "空炮")}");
            return executed;
        }

        // 大模型判出了意图，按对应玩法的规则执行。返回 false = 让调用方走原有逻辑。
        public bool TryHandleIntent(DouyinEvent ev, IntentKind kind, string arg)
        {
            if (ev == null || Config == null || kind == IntentKind.None) return false;

            var rule = RuleQuery.FindByEffectPrefix(Config, RuleQuery.EffectPrefix(kind));
            // 主播把这个玩法的规则删了就是不想要它，别越过配置替他开
            if (rule == null) return false;

            // 这是一次全新请求，没人付过费，四道闸照走
            var gate = limiter.Check(rule, Config.global, ev.UserId);
            if (gate != GateResult.Pass)
            {
                if (debugLog) Debug.Log($"[Triggers] 意图 {kind} 被 {gate} 拦下，改走闲聊");
                return false;
            }

            // 名字不可用就只问不做：ask 分支会开槽等观众补答
            string effect = RuleQuery.BuildEffect(
                kind, IntentText.IsUsableArg(arg) ? arg.Trim() : "ask");

            bool executed = director.Submit(RuleQuery.WithEffect(rule, effect), ev, Config.global);
            if (executed) limiter.Commit(rule, Config.global, ev.UserId);
            return executed;
        }

        TriggerRule FindRuleById(string id)
        {
            if (Config == null || Config.rules == null || string.IsNullOrEmpty(id)) return null;
            foreach (var r in Config.rules)
                if (r != null && r.id == id) return r;
            return null;
        }
```

- [ ] **Step 3: 把 Route 的弹幕尾巴抽成 HandleChatLegacy**

在 `DouyinLiveManager.cs` 里，把 `Route` 结尾的 `switch` 从：

```csharp
                case DouyinMsgType.Chat:
                    if (reward.TryHandleDanmaku(ev)) return;
                    danmakuAI.OnDanmaku(ev);
                    break;
```

改成：

```csharp
                case DouyinMsgType.Chat:
                    HandleChatLegacy(ev);
                    break;
```

并在 `Route` 方法之后加：

```csharp
        // 触发层、槽位、意图判定都没接住的弹幕走这里。抽成方法是因为意图判定
        // 是异步的：1.5 秒后判不出来，回调要能把这条弹幕补回原路径。
        void HandleChatLegacy(DouyinEvent ev)
        {
            if (reward.TryHandleDanmaku(ev)) return;
            danmakuAI.OnDanmaku(ev);
        }
```

- [ ] **Step 4: 在 Route 里插入槽位补全**

把 `Route` 里这一行：

```csharp
            if (triggers != null && triggers.TryHandle(ev)) return;
```

改成：

```csharp
            if (triggers != null)
            {
                // 关键词规则排在槽位补全之前是有意的：观众答的歌名如果恰好叫
                // 《抱抱》，会命中 love 规则去播飞吻而不是唱歌。宁可漏一次也不
                // 要乱触发，这个顺序也顺带省掉了「答案是不是另一条命令」那道校验。
                if (triggers.TryHandle(ev)) return;
                if (triggers.TryFillSlot(ev)) return;
            }
```

- [ ] **Step 5: 编译验证**

跑「通用命令」里的编译验证。预期：`another Unity instance` 计数 0，`error CS` 计数 0。

- [ ] **Step 6: 跑测试确认没打破现有用例**

跑 EditMode 测试。预期：`failed="0"`，`total="101"`。

- [ ] **Step 7: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/TriggerRouter.cs" \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs"
git commit -m "feat(douyin-live): route follow-up answers back to the rule that asked

The fill deliberately skips the four rate-limit gates: the open already
paid, and charging twice would let swap's 60s rule cooldown and the 45s
L3 interval swallow every answer inside the 30s window. It cannot be
abused — opening a slot requires passing the gates, and a slot is taken
exactly once."
```

---

### Task 7: LLM 意图兜底与文档

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/IntentResolver.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs`
- Modify: `README.md`

**Interfaces:**
- Consumes: `IChatBackend.ChatAsync(string systemPrompt, IReadOnlyList<ChatMsg> history, string userMsg, Action<string> onDelta, CancellationToken ct)`、`MainThreadDispatcher.Post`、`IntentText.LooksLikeIntent` / `TryParseIntentJson`（Task 2）、`TriggerRouter.TryHandleIntent` / `IntentFallbackEnabled`（Task 6）、`DouyinLiveManager.HandleChatLegacy`（Task 6）
- Produces: `class IntentResolver`，含 `IChatBackend Cloud`、`bool debugLog`、`void Reset()`、`bool TryResolve(DouyinEvent ev, Action<DouyinEvent, IntentKind, string> onResolved, Action<DouyinEvent> onGiveUp)`

**本任务无法写 EditMode 测试**（需要真实 LLM 后端和 Unity 主线程派发）。它依赖的纯逻辑（预筛、JSON 解析）已在 Task 2 覆盖。验证是编译干净 + 手工清单第 9~12 条。

- [ ] **Step 1: 实现 IntentResolver**

新建 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/IntentResolver.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DouyinLive
{
    // 关键词规则和追问槽位都没接住时的最后一层兜底：花不超过 1.5 秒问一次
    // 大模型「这条弹幕是不是在点歌/点舞/换角色」。
    //
    // 刻意不复用 DanmakuAIService.GenerateOneShot：那条路会注入完整人设 prompt、
    // 追加「只回一句话，不超过30个字」、再过一遍 Sanitize（剥方括号 + 60 字截断）。
    // 分类任务要的是干净 JSON，人设化的一句话正好是它最不需要的东西。
    public class IntentResolver
    {
        public IChatBackend Cloud;
        public bool debugLog = false;
        public Func<float> Now = () => Time.unscaledTime;

        public const float PerUserCooldown = 15f;   // 同一个人问过就先歇着，防刷 token
        public const int MaxInFlight = 2;
        public const float TimeoutSeconds = 1.5f;   // 超过这个时间观众已经在等下一条弹幕了
        const int MaxTrackedUsers = 200;

        const string SystemPrompt =
            "你是弹幕意图分类器。判断这句弹幕是不是在点歌、点舞或要求换角色。" +
            "只输出 JSON，不要任何解释：" +
            "{\"intent\":\"song|dance|avatar|none\",\"arg\":\"歌名/舞名/角色名，没有就留空\"}";

        readonly Dictionary<string, float> lastAskedByUser = new Dictionary<string, float>();
        int inFlight;

        public void Reset()
        {
            lastAskedByUser.Clear();
            inFlight = 0;
        }

        // 返回 true = 这条弹幕已被接管（正在问大模型），调用方不要再走原路径。
        // 结果稍后一定会经 onResolved 或 onGiveUp 回到主线程，不会石沉大海。
        public bool TryResolve(DouyinEvent ev,
                               Action<DouyinEvent, IntentKind, string> onResolved,
                               Action<DouyinEvent> onGiveUp)
        {
            if (ev == null || onResolved == null || onGiveUp == null) return false;
            if (Cloud == null || !Cloud.IsAvailable) return false;
            // 本地预筛：没有任何点播痕迹的弹幕不值得花 token
            if (IntentText.LooksLikeIntent(ev.Content) == IntentKind.None) return false;
            if (inFlight >= MaxInFlight) return false;

            string uid = ev.UserId ?? "";
            float now = Now();
            if (!string.IsNullOrEmpty(uid))
            {
                if (lastAskedByUser.TryGetValue(uid, out float last) && now - last < PerUserCooldown)
                    return false;
                if (lastAskedByUser.Count >= MaxTrackedUsers) PruneUsers(now);
                lastAskedByUser[uid] = now;
            }

            inFlight++;
            _ = ResolveAsync(ev, onResolved, onGiveUp);
            return true;
        }

        void PruneUsers(float now)
        {
            var stale = new List<string>();
            foreach (var kv in lastAskedByUser)
                if (now - kv.Value >= PerUserCooldown) stale.Add(kv.Key);
            foreach (var k in stale) lastAskedByUser.Remove(k);
        }

        async Task ResolveAsync(DouyinEvent ev,
                                Action<DouyinEvent, IntentKind, string> onResolved,
                                Action<DouyinEvent> onGiveUp)
        {
            string raw = null;
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds)))
                    raw = await Cloud.ChatAsync(SystemPrompt, new List<ChatMsg>(), ev.Content ?? "",
                                                null, cts.Token);
            }
            catch (Exception ex)
            {
                // 超时是常态不是故障，默认不刷屏
                if (debugLog) Debug.Log("[IntentResolver] 判定失败，改走原路径: " + ex.Message);
            }

            IntentKind kind = IntentKind.None;
            string arg = "";
            if (raw != null && !IntentText.TryParseIntentJson(raw, out kind, out arg))
            {
                kind = IntentKind.None;
                arg = "";
            }

            // 模型返回的内容只当数据用：这里只取 intent 和 arg 两个字段，
            // arg 还要再过一遍 IsUsableArg 才会被当成歌名，永远不会被当指令执行
            var k = kind;
            var a = arg;
            var e = ev;
            MainThreadDispatcher.Post(() =>
            {
                inFlight--;
                if (debugLog) Debug.Log($"[IntentResolver] 「{e.Content}」→ {k} / {a}");
                if (k == IntentKind.None) onGiveUp(e);
                else onResolved(e, k, a);
            });
        }
    }
}
```

- [ ] **Step 2: 在 DouyinLiveManager 里接线**

在字段区 `TriggerRouter triggers;` 那一行后面加：

```csharp
        readonly IntentResolver intents = new IntentResolver();
```

在 `ApplySettings()` 里，`triggers.debugLog = debugLog;` 那一行后面加：

```csharp
            intents.Cloud = cloudBackend;
            intents.debugLog = debugLog;
```

在 `StartLive()` 里 `if (triggers != null) triggers.ResetSession();` 后面加：

```csharp
            intents.Reset();
```

- [ ] **Step 3: 在 Route 里插入意图判定**

在 `Route` 里，Step 4（Task 6）加的那个 `if (triggers != null) { ... }` 块之后、`switch (ev.Type)` 之前插入：

```csharp
            // 关键词和槽位都没接住：本地预筛命中的才去问大模型。异步，所以命中就
            // 当场消费掉这条弹幕，判不出来再由回调补回 HandleChatLegacy ——
            // 否则会出现「先闲聊回一句、一秒后又开始唱歌」的双重响应。
            if (ev.Type == DouyinMsgType.Chat && triggers != null && triggers.IntentFallbackEnabled &&
                intents.TryResolve(ev, OnIntentResolved, HandleChatLegacy))
                return;
```

并在 `HandleChatLegacy` 之后加：

```csharp
        void OnIntentResolved(DouyinEvent ev, IntentKind kind, string arg)
        {
            if (triggers != null && triggers.TryHandleIntent(ev, kind, arg)) return;
            // 规则不存在或被限流拦下：「我想听首歌」本身是很好的闲聊素材，
            // 让 AI 回一句是体面的降级
            HandleChatLegacy(ev);
        }
```

- [ ] **Step 4: 编译验证**

跑「通用命令」里的编译验证。预期：`another Unity instance` 计数 0，`error CS` 计数 0。

- [ ] **Step 5: 跑测试确认没打破现有用例**

跑 EditMode 测试。预期：`failed="0"`，`total="101"`。

- [ ] **Step 6: 更新 README**

在 `README.md` 的「可用效果」表里，把这三行：

```
| `dance:random` / `dance:<舞名>` / `dance:builtin` | 跳舞。`random` 一轮之内不重复 |
| `song:<歌名>` / `song:request` | 唱歌。`request` 从弹幕正文里取歌名 |
| `swapAvatar` | 随机换 VRM 角色，自动身高归一化 |
```

替换为：

```
| `dance:random` / `dance:<舞名>` / `dance:builtin` | 跳舞。`random` 一轮之内不重复 |
| `dance:request` / `dance:ask` | 点舞。`request` 先从弹幕正文取舞名，取不到就追问；`ask` 直接追问 |
| `song:<歌名>` / `song:request` / `song:ask` | 唱歌。`request` 先从弹幕正文取歌名，取不到就追问；`ask` 直接追问 |
| `swapAvatar` / `swapAvatar:<角色名>` / `swapAvatar:request` / `swapAvatar:ask` | 换 VRM 角色，自动身高归一化。不带参数=随机；带名字按 `avatars.json` 的 `displayName` 模糊匹配 |
```

在同一节「动作三层」那一行之前插入：

```markdown
**追问（两轮点播）：** `request` / `ask` 会让角色先问一句，然后**记住是谁在被问**。
这个观众接下来 30 秒内发的第一条像样的弹幕就当成答案——发「点歌」，角色问「想听什么歌呀」，
再发「赤伶」，直接开唱。答案是「666」「哈哈哈」这类无意义内容时不算数，槽位留着继续等。
只认发起人，别人插嘴不影响。规则里可以加 `askPrompt` 自定义追问文案（支持 `{u}`）。

**关键词没配到的说法**（「我想听点音乐」）会先过一遍本地词表预筛，命中才花 1.5 秒
问一次大模型判意图，判不出来就正常走 AI 闲聊。同一观众 15 秒最多问一次，
全局同时最多 2 个在飞，所以刷屏刷不动它。不想烧这个 token 就把 `intentFallbackEnabled` 设成 `false`。
```

在「防刷屏的四道闸」表之后加一段：

```markdown
**追问的回答不再走这四道闸**：开槽那一次已经过闸并记账了，追问的两轮是一次请求的两半。
不这么做的话，换角色 60 秒的规则冷却和 45 秒的 L3 间隔会把 30 秒窗口内的回答全部拦死。
滥用也不成立——要开槽必须先过闸，一个槽只能被取走一次。
```

在同一节的 `l3InterruptSinging` 那一行之后，往表里补两行：

```
| `slotWindowSeconds` | `global` | 追问后等观众回答的秒数，默认 30。设 `0` 关闭追问功能 |
| `intentFallbackEnabled` | `global` | 关键词没中时是否问大模型判意图，默认 `true` |
```

在「功能总览」表里，把这三行：

```
| 点歌 | 弹幕 `点歌 歌名` | 网易云搜歌 → 播放高潮段 → 跟节奏跳舞 |
| 换角色 | 弹幕 `换角色` | 随机切换模型库中的 VRM，自动身高归一化 |
```

替换为：

```
| 点歌 | 弹幕 `点歌 歌名`，或先发 `点歌` 再发歌名 | 网易云搜歌 → 播放高潮段 → 跟节奏跳舞 |
| 换角色 | 弹幕 `换角色`，或说出角色名 | 切换模型库中的 VRM，自动身高归一化 |
| 两轮点播 | 角色追问后 30 秒内的回答 | 点歌/点舞/换角色都支持「先问再答」，答不上来的弹幕不算数 |
```

- [ ] **Step 7: 写手工验证清单**

新建 `.superpowers/sdd/2026-09-02-douyin-intent-slots/手工验证清单.md`（目录若不存在先建）：

```markdown
# 两轮点播手工验证清单

前置：关掉 MateEngineX.exe → 确认 settings.json 里 `enableDouyinLive: true` →
启动 `python Tools/douyin_mock_server.py` → 启动 MateEngineX.exe。
用 `c <昵称> <内容>` 发弹幕（模拟服务器的用法见脚本内提示）。

同一个模拟观众连续发弹幕才算「同一个人」，换昵称等于换人。

- [ ] 1. 发「点歌」→ 角色问「想听什么歌呀」→ 发「赤伶」→ **开始唱赤伶**
- [ ] 2. 发「点歌 赤伶」→ 一次到位开唱（回归：老用法不能坏）
- [ ] 3. 发「点歌」→ 等 40 秒 → 发「赤伶」→ 走 AI 闲聊，不唱歌（窗口过期）
- [ ] 4. A 发「点歌」→ B 发「赤伶」→ B 走闲聊；A 再发「大鱼」→ A 的歌开唱
- [ ] 5. 发「点歌」→ 发「666」→ 走闲聊 → 再发「赤伶」→ **开始唱**（槽位没被 666 消耗掉）
- [ ] 6. 发「点舞」→ 角色问 → 发一个曲库里真实存在的舞名 → 播那支舞
- [ ] 7. 发「换角色」→ 角色问 → 发一个模型库里存在的角色名 → **换成那个角色**
- [ ] 8. 发「换角色」→ 角色问 → 发一个不存在的名字 → 说「衣柜里没有…」+ 随机换
- [ ] 9. 发「我想听点音乐」→ 角色追问 → 发「赤伶」→ 开唱（LLM 兜底 + 槽位）
- [ ] 10. 发「今天天气不错」→ 日志里**没有** `[IntentResolver]` 记录，正常闲聊
- [ ] 11. `douyin_triggers.json` 里 `intentFallbackEnabled` 改 `false` 存盘（热重载生效）
        → 第 9 条退化成纯闲聊，第 1 条仍然工作
- [ ] 12. `slotWindowSeconds` 改 `0` 存盘 → 追问功能关闭，发「点歌」只问不等，
        再发歌名走闲聊（退回今天的行为）
- [ ] 13. 把 `douyin_triggers.json` 里的 `song` 规则 `enabled` 改成 `false` 存盘
        → 发「点歌」走闲聊，不报错、不哑掉
```

- [ ] **Step 8: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/IntentResolver.cs"* \
        "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs" \
        README.md
git commit -m "feat(douyin-live): fall back to a classifier call when no keyword matches

A local word-list prefilter decides whether a danmaku is worth 1.5s of
model time; per-user and in-flight caps keep a spammer from hammering it.
The model's answer is data, never instructions: only intent and arg are
read, and arg still has to pass IsUsableArg before it becomes a song title."
```

---

## 完成后

七个任务做完后，`.superpowers/sdd/2026-09-02-douyin-intent-slots/手工验证清单.md` 里的 13 条只有主播本人能跑。清单全绿之前不要合并 `feat/douyin-triggers`。

任务 6 结束时确定性路径（清单第 1~8 条）已完整可用；任务 7 是纯增量，砍掉不影响前面任何一条。
