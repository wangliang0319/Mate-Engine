# 抖音直播可配置触发层 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把抖音直播的互动触发逻辑从硬编码的 `switch` 中抽出来，改由 `douyin_triggers.json` 配置驱动，并增强跳舞玩法。

**Architecture:** 旁路式三层结构。`TriggerRouter` 读配置匹配事件 → `ActionDirector` 做分层仲裁与限流 → `EffectRegistry` 按字符串 ID 执行效果。`DouyinLiveManager.Route()` 先问触发层，未命中才走现有代码路径；删掉配置文件即完全回退到当前行为。纯逻辑部分（匹配、限流）放进新的 `MateEngine.DouyinLive.Core` asmdef 以便写单元测试。

**Tech Stack:** Unity 6000.2.6f2 / C# 9 / Newtonsoft.Json 13.0.3（`Assets/Packages/` 下的预编译 DLL）/ Unity Test Framework（随 `com.unity.feature.development` 提供，无需改 manifest）

**Spec:** [Docs/superpowers/specs/2026-08-30-douyin-triggers-design.md](../specs/2026-08-30-douyin-triggers-design.md)

## Global Constraints

- **Unity 版本 6000.2.6f2**，目标平台 Windows Mono。
- **命名空间统一为 `DouyinLive`**，包括放进新 asmdef 的文件。新 asmdef 的 `rootNamespace` 也设为 `DouyinLive`。
- **所有新代码注释用中文**，与仓库现有风格一致（见 `DouyinLiveManager.cs`、`RewardService.cs`）。
- **配置文件路径统一用 `Application.persistentDataPath`**，与 `douyin_persona.json` / `douyin_gift_rules.json` 同目录。
- **Newtonsoft 反序列化一律显式传 `ObjectCreationHandling.Replace`**。默认的 `Auto` 会复用字段初始值建好的集合并**追加**磁盘内容 —— 本仓库已经因此踩过一次坑（`douyinIdleSongList` 涨到 166 条）。
- **任何解析失败都不能让直播间哑掉。** 失败时保留上一份可用配置并 `Debug.LogError`，绝不抛异常穿透到 `Update()`。
- **Animator 参数名大小写敏感**，与 `AvatarAnimatorControllerV2.controller` 保持一致。现存的交互参数只有 5 个：`Headpat`、`HairStroke`、`HoverFaceTrigger`、`HoverTrigger`、`IntimeRegion`。
- **粒子主题目前只有 `Dance Trail Blue` 一个**（`CustomVRM.prefab`）。配其它名字不会报错，只是静默无效果 —— 所以代码里要主动检测并警告。
- **提交信息格式：** `feat(douyin-live): ...` / `test(douyin-live): ...` / `refactor(douyin-live): ...`，正文末尾加 `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`。

> **对 spec 的一处更正：** spec §10 第 2 点写「并在 `manifest.json` 加 `"testables"`」。
> 这是错的 —— `testables` 只对**放在 package 里**的测试有用，本计划的测试在 `Assets/` 下，
> 不需要改 `manifest.json`。`com.unity.feature.development` 1.0.2 已经传递引入了测试框架。
> 按本计划执行，不要动 `manifest.json`。

## 如何运行测试

本仓库**此前没有任何测试工程**，Task 1 会建起来。建好后两种运行方式：

1. **主路径（Unity 编辑器）**：`Window → General → Test Runner → EditMode → Run All`。
2. **命令行**（CI 或无编辑器时）：
   ```bash
   "<Unity安装路径>/Unity.exe" -runTests -batchmode -projectPath "e:/Work/AI/Mate-Engine" \
     -testPlatform EditMode -testResults "e:/Work/AI/Mate-Engine/TestResults.xml"
   ```
   退出码 0 = 全通过。结果看 `TestResults.xml`。

**注意：** 每次改完 C# 后必须让 Unity 编辑器重新编译（切到编辑器窗口会自动触发）才能跑测试。计划里写「运行测试」的步骤都隐含这一步。

## 文件结构

```
Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/
├── Core/                                    ← 新建，纯逻辑，可单元测试
│   ├── MateEngine.DouyinLive.Core.asmdef
│   ├── DouyinEventTypes.cs                  ← 从 DouyinEvents.cs 拆出的 POCO
│   ├── TriggerRules.cs                      ← 配置数据结构 + 默认配置
│   ├── TriggerMatcher.cs                    ← 纯函数：事件 → 命中的规则
│   └── TriggerLimiter.cs                    ← 四道限流闸，时钟可注入
├── Tests/                                   ← 新建，EditMode 测试
│   ├── MateEngine.DouyinLive.Tests.asmdef
│   ├── TriggerMatcherTests.cs
│   └── TriggerLimiterTests.cs
├── DouyinEvents.cs                          ← 保留线格式部分（依赖 Newtonsoft）
├── TriggerConfigStore.cs                    ← 新建：读写 json + 生成默认文件
├── TriggerRouter.cs                         ← 新建：MonoBehaviour，热重载 + 路由
├── ActionDirector.cs                        ← 新建：分层仲裁 + L3 队列
├── EffectRegistry.cs                        ← 新建：效果 ID → 执行器
├── DanceDirector.cs                         ← 新建：舞蹈编排
├── DouyinLiveManager.cs                     ← 修改：Route() 前置匹配
├── RewardService.cs                         ← 修改：开放换角色/选舞接口
└── IdleChatterService.cs                    ← 修改：冷场唱跳交替
```

**为什么拆 `DouyinEventTypes.cs`：** Unity 的 asmdef 不能反向引用 `Assembly-CSharp`，所以被测代码必须搬进 asmdef。但 `DouyinEvents.cs` 里的线格式类（`DouyinMsg`）用了 `[JsonExtensionData]`，会把 Newtonsoft 依赖拖进 Core。只把 `DouyinMsgType` 和 `DouyinEvent` 这两个纯 POCO 搬过去，Core 就零外部依赖，asmdef 配置最简单、最不容易出问题。

---

## 第一期：触发层骨架

完成后即可通过改 json 配置弹幕关键词玩法。

### Task 1: 建立 Core asmdef 与测试工程

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/MateEngine.DouyinLive.Core.asmdef`
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/DouyinEventTypes.cs`
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/MateEngine.DouyinLive.Tests.asmdef`
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/SmokeTests.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinEvents.cs`

**Interfaces:**
- Consumes: 无
- Produces: `DouyinLive.DouyinMsgType`（枚举）、`DouyinLive.DouyinEvent`（POCO，公开字段 `Type/MsgId/UserId/Nickname/Content/LikeCount/GiftName/GiftId/GiftCount/DiamondCount/ReceivedAt`）、`DouyinLive.DouyinEventFactory.From(DouyinMsgType, DouyinMsg) → DouyinEvent`

- [ ] **Step 1: 创建 Core asmdef**

`Core/MateEngine.DouyinLive.Core.asmdef`：

```json
{
    "name": "MateEngine.DouyinLive.Core",
    "rootNamespace": "DouyinLive",
    "references": [],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

`autoReferenced: true` 让 `Assembly-CSharp` 自动引用它，所以现有代码的 `using` 一行都不用改。

- [ ] **Step 2: 把两个纯 POCO 搬进 Core**

`Core/DouyinEventTypes.cs`（内容整体从 `DouyinEvents.cs` 剪切而来，去掉 `From` 方法和 Newtonsoft 引用）：

```csharp
using System;

namespace DouyinLive
{
    // DouyinBarrageGrab PackMsgType
    public enum DouyinMsgType
    {
        None = 0,
        Chat = 1,       // 弹幕
        Like = 2,       // 点赞
        Enter = 3,      // 进入直播间
        Follow = 4,     // 关注
        Gift = 5,       // 礼物
        Stats = 6,      // 直播间统计
        FansClub = 7,   // 粉丝团
        Share = 8       // 分享
    }

    // 统一后的主线程事件。刻意不带任何序列化特性：本类型要放在
    // MateEngine.DouyinLive.Core 里供单元测试使用，Core 保持零外部依赖。
    // 从线格式 DouyinMsg 的转换见 DouyinEventFactory（在 Assembly-CSharp 里）。
    public class DouyinEvent
    {
        public DouyinMsgType Type;
        public long MsgId;
        public string UserId;      // SecUid 优先，退化用 Id
        public string Nickname;
        public string Content;
        public int LikeCount;
        public string GiftName;
        public long GiftId;
        public int GiftCount;
        public int DiamondCount;
        public DateTime ReceivedAt;
    }
}
```

- [ ] **Step 3: 在 DouyinEvents.cs 里留下线格式部分并新增工厂**

删除 `DouyinEvents.cs` 里的 `DouyinMsgType` 枚举和整个 `DouyinEvent` 类，保留 `DouyinEnvelope`、`DouyinUser`、`DouyinMsg`，并在文件末尾（`namespace DouyinLive` 内）加：

```csharp
    // DouyinMsg 带 [JsonExtensionData]，依赖 Newtonsoft，所以转换逻辑留在
    // Assembly-CSharp 这边，Core 只放不依赖任何第三方库的 DouyinEvent。
    public static class DouyinEventFactory
    {
        public static DouyinEvent From(DouyinMsgType type, DouyinMsg m)
        {
            if (m == null) return null;
            return new DouyinEvent
            {
                Type = type,
                MsgId = m.MsgId,
                UserId = !string.IsNullOrEmpty(m.User?.SecUid) ? m.User.SecUid : (m.User?.Id ?? 0).ToString(),
                Nickname = m.User?.Nickname ?? "",
                Content = m.Content ?? "",
                LikeCount = m.Count > 0 ? m.Count : 1,
                GiftName = m.GiftName ?? "",
                GiftId = m.GiftId,
                GiftCount = Math.Max(1, m.GiftCount > 0 ? m.GiftCount : m.RepeatCount),
                DiamondCount = m.DiamondCount,
                ReceivedAt = DateTime.UtcNow
            };
        }
    }
```

- [ ] **Step 4: 修好 `DouyinEvent.From` 的所有调用点**

```bash
cd "e:/Work/AI/Mate-Engine" && grep -rn "DouyinEvent.From" "Assets/MATE ENGINE - Scripts/"
```

把每处 `DouyinEvent.From(` 改成 `DouyinEventFactory.From(`。预期只有 `DouyinLiveClient.cs` 里有调用。

- [ ] **Step 5: 创建测试 asmdef**

`Tests/MateEngine.DouyinLive.Tests.asmdef`：

```json
{
    "name": "MateEngine.DouyinLive.Tests",
    "rootNamespace": "DouyinLive.Tests",
    "references": [
        "MateEngine.DouyinLive.Core",
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner"
    ],
    "includePlatforms": ["Editor"],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": ["nunit.framework.dll"],
    "autoReferenced": false,
    "defineConstraints": ["UNITY_INCLUDE_TESTS"],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [ ] **Step 6: 写冒烟测试，证明测试工程真的跑起来了**

`Tests/SmokeTests.cs`：

```csharp
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class SmokeTests
    {
        [Test]
        public void DouyinEvent_可以从测试程序集访问()
        {
            var ev = new DouyinEvent { Type = DouyinMsgType.Chat, Content = "拍头" };
            Assert.AreEqual(DouyinMsgType.Chat, ev.Type);
            Assert.AreEqual("拍头", ev.Content);
        }
    }
}
```

- [ ] **Step 7: 回到 Unity 编辑器等编译，然后跑测试**

Run: `Window → General → Test Runner → EditMode → Run All`
Expected: 1 个测试通过。Console 里**不能有任何编译错误**。

如果 Console 报 `The type or namespace name 'DouyinEvent' could not be found`，说明 asmdef 的 `autoReferenced` 没生效 —— 检查 `.asmdef` 文件是否被 Unity 识别（Project 窗口里应显示为一个「程序集定义」图标），以及 `Core/` 目录下是否误留了旧的 `.meta` 冲突。

- [ ] **Step 8: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/"
git commit -m "$(cat <<'EOF'
refactor(douyin-live): extract pure event POCOs into a testable asmdef

Unity asmdefs cannot reference Assembly-CSharp, so anything we want to unit
test has to live in its own assembly. DouyinMsgType and DouyinEvent move into
MateEngine.DouyinLive.Core; the wire-format types stay behind because their
[JsonExtensionData] attribute would drag Newtonsoft into Core.

Adds the project's first EditMode test assembly.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: 配置数据结构与默认配置

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/TriggerRules.cs`

**Interfaces:**
- Consumes: 无
- Produces: `TriggerGlobal`、`TriggerRule`、`TriggerConfig`（均为公开字段的 POCO）、`TriggerConfig.Defaults() → TriggerConfig`、`TriggerRule.LevelOrDefault → int`（1/2/3，解析不出来时返回 1）

- [ ] **Step 1: 写数据结构**

`Core/TriggerRules.cs`：

```csharp
using System.Collections.Generic;

namespace DouyinLive
{
    // douyin_triggers.json 的数据结构。全部用公开字段 + 默认值，
    // Newtonsoft 不需要任何特性就能正确反序列化，Core 因此保持零外部依赖。
    public class TriggerGlobal
    {
        public float chatCooldown = 0.5f;    // 弹幕来源的全局冷却
        public float likeCooldown = 3f;
        public float giftCooldown = 1.2f;
        public float perUserCooldown = 5f;   // 防单人刷屏的主力闸
        public float l2MinInterval = 3f;
        public float l3MinInterval = 45f;    // 重磅效果之间的最小间隔
        public int l3QueueSize = 3;
        public bool l3InterruptSinging = false;
        public bool giftUseTotalValue = true; // true=单价×数量 false=只看单价
    }

    public class TriggerRule
    {
        public string id = "";
        public bool enabled = true;
        public string source = "chat";       // chat|like|follow|gift|enter|share

        // 匹配条件（按 source 取用，不适用的字段忽略）
        public List<string> keywords = new List<string>();
        public string regex = "";
        public int everyN = 0;               // like: 每 N 个赞触发一次
        public long milestone = 0;           // like: 累计跨过该值触发一次
        public string giftName = "";
        public int minDiamond = 0;
        public int maxDiamond = 0;           // 0 = 不限上限
        public int minCount = 0;

        // 执行
        public List<string> effects = new List<string>();
        public string pick = "all";          // all=全执行 random=随机选一个
        public string level = "L1";          // L1|L2|L3
        public float cooldown = 0f;          // 本规则独立冷却
        public float perUserCooldown = -1f;  // -1 = 跟随 global
        public string sayFallback = "";      // sayAI: 失败时的兜底文案

        // 1/2/3。写错或留空一律当 L1，宁可效果轻也不要意外独占画面。
        public int LevelOrDefault
        {
            get
            {
                if (level == "L3") return 3;
                if (level == "L2") return 2;
                return 1;
            }
        }
    }

    public class TriggerConfig
    {
        public int version = 1;
        public TriggerGlobal global = new TriggerGlobal();
        public List<TriggerRule> rules = new List<TriggerRule>();
    }
}
```

- [ ] **Step 2: 在同文件里加默认配置**

在 `TriggerConfig` 类内追加（`Defaults()` 的规则集与 spec §5.4 一致）：

```csharp
        public static TriggerConfig Defaults()
        {
            return new TriggerConfig
            {
                version = 1,
                global = new TriggerGlobal(),
                rules = new List<TriggerRule>
                {
                    // ---- 弹幕 L1 ----
                    Chat("pat",  new[] { "拍头", "敲脑袋", "摸头" }, new[] { "anim:Headpat" }, "L1"),
                    Chat("hair", new[] { "捋头发", "顺毛" },         new[] { "anim:HairStroke" }, "L1"),
                    // 占位：捏脸/戳脸/挠痒痒没有专属动画，先复用摸脸反应
                    Chat("face", new[] { "捏脸", "戳脸", "挠痒痒" },
                         new[] { "anim:HoverFaceTrigger", "mood:happy" }, "L1"),

                    // ---- 弹幕 L2（当前全部为占位映射）----
                    Chat("love", new[] { "飞吻", "么么", "抱抱" },
                         new[] { "anim:HoverFaceTrigger", "mood:love", "particle:Dance Trail Blue" }, "L2"),
                    Chat("wave", new[] { "挥手", "你好", "打招呼" }, new[] { "anim:HoverTrigger" }, "L2"),

                    // ---- 弹幕特殊指令 ----
                    Chat("menu", new[] { "菜单", "玩法" }, new[] { "menu" }, "L1"),
                    Chat("song", new[] { "点歌" },         new[] { "song:request" }, "L1"),
                    Cd(Chat("swap", new[] { "换角色", "换装", "换个人" }, new[] { "swapAvatar" }, "L3"), 60f, 180f),
                    Cd(Chat("reqdance", new[] { "点舞", "跳舞", "来一段" }, new[] { "dance:random" }, "L3"), 90f, 300f),

                    // ---- 点赞 ----
                    new TriggerRule
                    {
                        id = "like30", source = "like", everyN = 30, level = "L1", pick = "random",
                        effects = new List<string> { "anim:Headpat", "anim:HairStroke", "anim:HoverFaceTrigger" }
                    },
                    new TriggerRule
                    {
                        id = "like3000", source = "like", milestone = 3000, level = "L2",
                        effects = new List<string>
                        {
                            "face:Happy", "particle:Dance Trail Blue",
                            "say:哇！我们已经破三千赞啦，谢谢家人们！"
                        }
                    },

                    // ---- 关注 ----
                    new TriggerRule
                    {
                        id = "follow", source = "follow", level = "L3",
                        effects = new List<string>
                        {
                            "bigscreen", "particle:Dance Trail Blue",
                            "say:感谢 {u} 的关注，欢迎来到直播间！"
                        }
                    },

                    // ---- 礼物三档（抖币阈值按实际直播间调）----
                    new TriggerRule
                    {
                        id = "gift1", source = "gift", maxDiamond = 9, level = "L1", pick = "random",
                        effects = new List<string> { "anim:Headpat", "anim:HairStroke" }
                    },
                    new TriggerRule
                    {
                        id = "gift2", source = "gift", minDiamond = 10, maxDiamond = 99, level = "L2",
                        effects = new List<string> { "face:Happy", "particle:Dance Trail Blue" }
                    },
                    new TriggerRule
                    {
                        id = "gift3", source = "gift", minDiamond = 100, level = "L3",
                        sayFallback = "哇！！{u} 送出了超级大礼 {g}！！谢谢老板，这支舞献给你！",
                        effects = new List<string>
                        {
                            "bigscreen", "dance:random",
                            "sayAI:观众{u}送了{g}，用一句话热情感谢并说要跳舞回报"
                        }
                    },
                }
            };
        }

        static TriggerRule Chat(string id, string[] words, string[] effects, string level)
        {
            return new TriggerRule
            {
                id = id, source = "chat", level = level,
                keywords = new List<string>(words),
                effects = new List<string>(effects)
            };
        }

        static TriggerRule Cd(TriggerRule r, float cooldown, float perUser)
        {
            r.cooldown = cooldown;
            r.perUserCooldown = perUser;
            return r;
        }
```

- [ ] **Step 3: 写测试验证默认配置自洽**

`Tests/TriggerRulesTests.cs`：

```csharp
using System.Collections.Generic;
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class TriggerRulesTests
    {
        [Test]
        public void 默认配置的规则id唯一()
        {
            var cfg = TriggerConfig.Defaults();
            var seen = new HashSet<string>();
            foreach (var r in cfg.rules)
                Assert.IsTrue(seen.Add(r.id), $"重复的规则 id: {r.id}");
        }

        [Test]
        public void 默认配置每条规则都有效果()
        {
            foreach (var r in TriggerConfig.Defaults().rules)
                Assert.IsNotEmpty(r.effects, $"规则 {r.id} 没有配任何效果");
        }

        [Test]
        public void 礼物三档的抖币区间不重叠且无空洞()
        {
            var cfg = TriggerConfig.Defaults();
            var g1 = cfg.rules.Find(r => r.id == "gift1");
            var g2 = cfg.rules.Find(r => r.id == "gift2");
            var g3 = cfg.rules.Find(r => r.id == "gift3");
            Assert.AreEqual(g1.maxDiamond + 1, g2.minDiamond);
            Assert.AreEqual(g2.maxDiamond + 1, g3.minDiamond);
        }

        [Test]
        public void 层级写错时降级为L1而不是L3()
        {
            Assert.AreEqual(1, new TriggerRule { level = "" }.LevelOrDefault);
            Assert.AreEqual(1, new TriggerRule { level = "l3" }.LevelOrDefault);   // 大小写不匹配
            Assert.AreEqual(3, new TriggerRule { level = "L3" }.LevelOrDefault);
        }
    }
}
```

- [ ] **Step 4: 跑测试**

Run: Test Runner → EditMode → Run All
Expected: 5 个测试全部 PASS（含 Task 1 的冒烟测试）。

- [ ] **Step 5: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/"
git commit -m "$(cat <<'EOF'
feat(douyin-live): add trigger rule schema and default rule set

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: 匹配器（纯函数）

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/TriggerMatcher.cs`
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/TriggerMatcherTests.cs`

**Interfaces:**
- Consumes: `TriggerConfig`、`TriggerRule`、`DouyinEvent`（Task 1、2）
- Produces: `TriggerMatcher.Match(DouyinEvent ev, TriggerConfig cfg, MatchContext ctx) → TriggerRule`（无命中返回 `null`）、`MatchContext { long LikeTotalBefore; long LikeTotalAfter; }`

- [ ] **Step 1: 先写测试（TDD）**

`Tests/TriggerMatcherTests.cs`：

```csharp
using System.Collections.Generic;
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class TriggerMatcherTests
    {
        static TriggerConfig Cfg(params TriggerRule[] rules)
            => new TriggerConfig { rules = new List<TriggerRule>(rules) };

        static DouyinEvent Chat(string text, string userId = "u1")
            => new DouyinEvent { Type = DouyinMsgType.Chat, Content = text, UserId = userId };

        static DouyinEvent Gift(int diamond, int count = 1, string name = "玫瑰")
            => new DouyinEvent { Type = DouyinMsgType.Gift, GiftName = name, DiamondCount = diamond, GiftCount = count, UserId = "u1" };

        static TriggerRule ChatRule(string id, params string[] words)
            => new TriggerRule { id = id, source = "chat", keywords = new List<string>(words), effects = new List<string> { "anim:Headpat" } };

        [Test]
        public void 关键词包含匹配()
        {
            var cfg = Cfg(ChatRule("pat", "拍头"));
            Assert.AreEqual("pat", TriggerMatcher.Match(Chat("主播拍头！"), cfg, new MatchContext())?.id);
        }

        [Test]
        public void 关键词不命中返回null()
        {
            var cfg = Cfg(ChatRule("pat", "拍头"));
            Assert.IsNull(TriggerMatcher.Match(Chat("今天天气不错"), cfg, new MatchContext()));
        }

        [Test]
        public void 数组顺序即优先级第一条命中即停()
        {
            var cfg = Cfg(ChatRule("first", "换装"), ChatRule("second", "换装"));
            Assert.AreEqual("first", TriggerMatcher.Match(Chat("换装"), cfg, new MatchContext())?.id);
        }

        [Test]
        public void 禁用的规则被跳过()
        {
            var disabled = ChatRule("off", "换装"); disabled.enabled = false;
            var cfg = Cfg(disabled, ChatRule("on", "换装"));
            Assert.AreEqual("on", TriggerMatcher.Match(Chat("换装"), cfg, new MatchContext())?.id);
        }

        [Test]
        public void 正则匹配()
        {
            var rule = new TriggerRule { id = "num", source = "chat", regex = @"^\d+$", effects = new List<string> { "menu" } };
            Assert.AreEqual("num", TriggerMatcher.Match(Chat("666"), Cfg(rule), new MatchContext())?.id);
            Assert.IsNull(TriggerMatcher.Match(Chat("666a"), Cfg(rule), new MatchContext()));
        }

        [Test]
        public void 非法正则不抛异常只是不命中()
        {
            var rule = new TriggerRule { id = "bad", source = "chat", regex = "[", effects = new List<string> { "menu" } };
            Assert.DoesNotThrow(() => TriggerMatcher.Match(Chat("x"), Cfg(rule), new MatchContext()));
            Assert.IsNull(TriggerMatcher.Match(Chat("x"), Cfg(rule), new MatchContext()));
        }

        [Test]
        public void 来源不同的规则不会被误匹配()
        {
            var cfg = Cfg(new TriggerRule { id = "g", source = "gift", effects = new List<string> { "menu" } });
            Assert.IsNull(TriggerMatcher.Match(Chat("玫瑰"), cfg, new MatchContext()));
        }

        // ---- 点赞 ----

        [Test]
        public void everyN在跨过整数倍时命中()
        {
            var cfg = Cfg(new TriggerRule { id = "l", source = "like", everyN = 30, effects = new List<string> { "menu" } });
            var ev = new DouyinEvent { Type = DouyinMsgType.Like, LikeCount = 5 };
            // 28 → 33 跨过了 30
            Assert.AreEqual("l", TriggerMatcher.Match(ev, cfg, new MatchContext { LikeTotalBefore = 28, LikeTotalAfter = 33 })?.id);
            // 31 → 36 没跨过下一个整数倍
            Assert.IsNull(TriggerMatcher.Match(ev, cfg, new MatchContext { LikeTotalBefore = 31, LikeTotalAfter = 36 }));
        }

        [Test]
        public void 里程碑只在跨过的那一次命中()
        {
            var cfg = Cfg(new TriggerRule { id = "m", source = "like", milestone = 3000, effects = new List<string> { "menu" } });
            var ev = new DouyinEvent { Type = DouyinMsgType.Like, LikeCount = 10 };
            Assert.AreEqual("m", TriggerMatcher.Match(ev, cfg, new MatchContext { LikeTotalBefore = 2995, LikeTotalAfter = 3005 })?.id);
            Assert.IsNull(TriggerMatcher.Match(ev, cfg, new MatchContext { LikeTotalBefore = 3005, LikeTotalAfter = 3015 }));
        }

        // ---- 礼物 ----

        [Test]
        public void 礼物档位边界_总价值口径()
        {
            var cfg = TriggerConfig.Defaults();
            cfg.global.giftUseTotalValue = true;
            Assert.AreEqual("gift1", TriggerMatcher.Match(Gift(9), cfg, new MatchContext())?.id);
            Assert.AreEqual("gift2", TriggerMatcher.Match(Gift(10), cfg, new MatchContext())?.id);
            Assert.AreEqual("gift2", TriggerMatcher.Match(Gift(99), cfg, new MatchContext())?.id);
            Assert.AreEqual("gift3", TriggerMatcher.Match(Gift(100), cfg, new MatchContext())?.id);
            // 连刷 20 个 1 抖币 = 20，走中档
            Assert.AreEqual("gift2", TriggerMatcher.Match(Gift(1, 20), cfg, new MatchContext())?.id);
        }

        [Test]
        public void 礼物档位_只看单价口径()
        {
            var cfg = TriggerConfig.Defaults();
            cfg.global.giftUseTotalValue = false;
            // 同样是连刷 20 个 1 抖币，只看单价仍是小额档
            Assert.AreEqual("gift1", TriggerMatcher.Match(Gift(1, 20), cfg, new MatchContext())?.id);
        }

        [Test]
        public void 礼物名过滤()
        {
            var rule = new TriggerRule { id = "rose", source = "gift", giftName = "玫瑰", effects = new List<string> { "menu" } };
            Assert.AreEqual("rose", TriggerMatcher.Match(Gift(1, 1, "玫瑰"), Cfg(rule), new MatchContext())?.id);
            Assert.IsNull(TriggerMatcher.Match(Gift(1, 1, "棒棒糖"), Cfg(rule), new MatchContext()));
        }

        // ---- 无条件来源 ----

        [Test]
        public void 关注事件无条件命中follow规则()
        {
            var cfg = TriggerConfig.Defaults();
            var ev = new DouyinEvent { Type = DouyinMsgType.Follow, Nickname = "小明", UserId = "u9" };
            Assert.AreEqual("follow", TriggerMatcher.Match(ev, cfg, new MatchContext())?.id);
        }

        [Test]
        public void 空配置和空事件不抛异常()
        {
            Assert.IsNull(TriggerMatcher.Match(null, TriggerConfig.Defaults(), new MatchContext()));
            Assert.IsNull(TriggerMatcher.Match(Chat("拍头"), null, new MatchContext()));
            Assert.IsNull(TriggerMatcher.Match(Chat("拍头"), new TriggerConfig(), null));
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: Test Runner → EditMode → Run All
Expected: 编译失败，`The name 'TriggerMatcher' does not exist`。

- [ ] **Step 3: 实现匹配器**

`Core/TriggerMatcher.cs`：

```csharp
using System;
using System.Text.RegularExpressions;

namespace DouyinLive
{
    // 匹配所需的外部累计状态。做成显式入参而不是内部字段，
    // 这样 Match 是纯函数，可以在 EditMode 测试里直接构造任意场景。
    public class MatchContext
    {
        public long LikeTotalBefore;   // 本次事件计入之前的累计点赞
        public long LikeTotalAfter;    // 计入之后的累计点赞
    }

    // 事件 → 命中的规则。数组顺序即优先级，第一条命中即停。
    // 刻意不做「更具体的规则优先」这类隐式启发式：出问题时能一眼看出为什么是那条赢了。
    public static class TriggerMatcher
    {
        public static TriggerRule Match(DouyinEvent ev, TriggerConfig cfg, MatchContext ctx)
        {
            if (ev == null || cfg == null || cfg.rules == null || ctx == null) return null;

            string source = SourceOf(ev.Type);
            if (source == null) return null;

            foreach (var r in cfg.rules)
            {
                if (r == null || !r.enabled) continue;
                if (r.source != source) continue;
                if (!Matches(r, ev, cfg.global, ctx)) continue;
                return r;
            }
            return null;
        }

        static string SourceOf(DouyinMsgType t)
        {
            switch (t)
            {
                case DouyinMsgType.Chat:   return "chat";
                case DouyinMsgType.Like:   return "like";
                case DouyinMsgType.Follow: return "follow";
                case DouyinMsgType.Gift:   return "gift";
                case DouyinMsgType.Enter:  return "enter";
                case DouyinMsgType.Share:  return "share";
                default: return null;   // Stats / FansClub / None 不参与触发
            }
        }

        static bool Matches(TriggerRule r, DouyinEvent ev, TriggerGlobal g, MatchContext ctx)
        {
            switch (r.source)
            {
                case "chat": return MatchesChat(r, ev.Content ?? "");
                case "like": return MatchesLike(r, ctx);
                case "gift": return MatchesGift(r, ev, g);
                default: return true;   // follow / enter / share：该源的事件一律命中
            }
        }

        // 关键词包含匹配（忽略大小写）或正则任一命中即算命中。
        // 两个条件都没写的弹幕规则永远不命中 —— 否则它会吞掉所有弹幕，
        // 让 AI 回复永远不生效，而这种配置几乎肯定是写漏了。
        static bool MatchesChat(TriggerRule r, string content)
        {
            if (r.keywords != null)
                foreach (var w in r.keywords)
                {
                    if (string.IsNullOrWhiteSpace(w)) continue;
                    if (content.IndexOf(w.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }

            if (!string.IsNullOrWhiteSpace(r.regex))
            {
                // 用户写错正则不能让整个直播间的弹幕响应崩掉，当作不命中处理。
                // 调用方（TriggerConfigStore）在加载时已经校验过一次并报过错。
                try { if (Regex.IsMatch(content, r.regex)) return true; }
                catch (ArgumentException) { }
            }

            return false;
        }

        static bool MatchesLike(TriggerRule r, MatchContext ctx)
        {
            if (r.everyN > 0)
                return ctx.LikeTotalBefore / r.everyN < ctx.LikeTotalAfter / r.everyN;

            if (r.milestone > 0)
                return ctx.LikeTotalBefore < r.milestone && ctx.LikeTotalAfter >= r.milestone;

            return true;   // 没写条件 = 每次点赞都命中
        }

        static bool MatchesGift(TriggerRule r, DouyinEvent ev, TriggerGlobal g)
        {
            if (!string.IsNullOrEmpty(r.giftName) &&
                (ev.GiftName ?? "").IndexOf(r.giftName, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            int count = Math.Max(1, ev.GiftCount);
            if (r.minCount > 0 && count < r.minCount) return false;

            bool useTotal = g == null || g.giftUseTotalValue;
            int value = useTotal ? Math.Max(1, ev.DiamondCount) * count : ev.DiamondCount;

            if (r.minDiamond > 0 && value < r.minDiamond) return false;
            if (r.maxDiamond > 0 && value > r.maxDiamond) return false;
            return true;
        }
    }
}
```

- [ ] **Step 4: 跑测试**

Run: Test Runner → EditMode → Run All
Expected: 全部 PASS（含 Task 1-2 的测试，共 19 个）。

- [ ] **Step 5: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/"
git commit -m "$(cat <<'EOF'
feat(douyin-live): add pure trigger matcher with unit tests

Array order is the priority: first match wins. Deliberately avoids the
implicit "most specific rule" heuristic RewardService.MatchRule uses, which
makes it hard to explain why a given rule won.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: 四道限流闸

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/TriggerLimiter.cs`
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/TriggerLimiterTests.cs`

**Interfaces:**
- Consumes: `TriggerRule`、`TriggerGlobal`（Task 2）
- Produces: `enum GateResult { Pass, SourceCooldown, UserCooldown, RuleCooldown, LevelInterval }`、`TriggerLimiter` 带 `Func<float> Now` 属性、`Check(TriggerRule, TriggerGlobal, string userId) → GateResult`、`Commit(TriggerRule, TriggerGlobal, string userId)`、`PruneUsers(float idleSeconds)`、`Reset()`

- [ ] **Step 1: 先写测试**

`Tests/TriggerLimiterTests.cs`：

```csharp
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class TriggerLimiterTests
    {
        float clock;
        TriggerLimiter lim;
        TriggerGlobal g;

        [SetUp]
        public void SetUp()
        {
            clock = 1000f;                       // 从非 0 开始，防止「未初始化 = 0」的假通过
            lim = new TriggerLimiter { Now = () => clock };
            g = new TriggerGlobal();
        }

        static TriggerRule Rule(string id = "r", string source = "chat", string level = "L1",
                                float cooldown = 0f, float perUser = -1f)
            => new TriggerRule { id = id, source = source, level = level, cooldown = cooldown, perUserCooldown = perUser };

        [Test]
        public void 首次触发直接通过()
        {
            Assert.AreEqual(GateResult.Pass, lim.Check(Rule(), g, "u1"));
        }

        [Test]
        public void 源冷却在时间未到时拦截()
        {
            var r = Rule();
            lim.Commit(r, g, "u1");
            clock += 0.2f;                                  // chatCooldown = 0.5
            Assert.AreEqual(GateResult.SourceCooldown, lim.Check(r, g, "u2"));
            clock += 0.4f;                                  // 累计 0.6 > 0.5
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "u2"));
        }

        [Test]
        public void 单人冷却只冻结该用户不影响别人()
        {
            g.chatCooldown = 0f;                            // 排除源冷却干扰
            var r = Rule();
            lim.Commit(r, g, "spammer");
            Assert.AreEqual(GateResult.UserCooldown, lim.Check(r, g, "spammer"));
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "innocent"));
        }

        [Test]
        public void 规则自带的单人冷却覆盖全局值()
        {
            g.chatCooldown = 0f;
            g.perUserCooldown = 5f;
            var r = Rule(perUser: 100f);
            lim.Commit(r, g, "u1");
            clock += 10f;                                   // 超过全局 5 秒，但没超过规则的 100 秒
            Assert.AreEqual(GateResult.UserCooldown, lim.Check(r, g, "u1"));
        }

        [Test]
        public void 规则冷却对所有用户生效()
        {
            g.chatCooldown = 0f;
            g.perUserCooldown = 0f;
            var r = Rule(cooldown: 60f);
            lim.Commit(r, g, "u1");
            Assert.AreEqual(GateResult.RuleCooldown, lim.Check(r, g, "another"));
            clock += 61f;
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "another"));
        }

        [Test]
        public void L3最小间隔跨规则生效()
        {
            g.chatCooldown = 0f;
            g.perUserCooldown = 0f;
            var swap = Rule("swap", level: "L3");
            var dance = Rule("dance", level: "L3");
            lim.Commit(swap, g, "u1");
            Assert.AreEqual(GateResult.LevelInterval, lim.Check(dance, g, "u2"));
            clock += g.l3MinInterval + 1f;
            Assert.AreEqual(GateResult.Pass, lim.Check(dance, g, "u2"));
        }

        [Test]
        public void L3的间隔不影响L1()
        {
            g.chatCooldown = 0f;
            g.perUserCooldown = 0f;
            lim.Commit(Rule("swap", level: "L3"), g, "u1");
            Assert.AreEqual(GateResult.Pass, lim.Check(Rule("pat", level: "L1"), g, "u2"));
        }

        [Test]
        public void 空UserId时单人冷却退化为不限制()
        {
            g.chatCooldown = 0f;
            var r = Rule();
            lim.Commit(r, g, null);
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, null));
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, ""));
        }

        [Test]
        public void 不同来源的源冷却互不干扰()
        {
            var chat = Rule("c", source: "chat");
            var gift = Rule("gf", source: "gift");
            lim.Commit(chat, g, "u1");
            Assert.AreEqual(GateResult.Pass, lim.Check(gift, g, "u2"));
        }

        [Test]
        public void Check不产生副作用可重复调用()
        {
            var r = Rule();
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "u1"));
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "u1"));
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "u1"));
        }

        [Test]
        public void PruneUsers清掉长时间不活跃的记账()
        {
            g.chatCooldown = 0f;
            var r = Rule(perUser: 99999f);
            lim.Commit(r, g, "u1");
            Assert.AreEqual(1, lim.TrackedUserCount);
            clock += 700f;
            lim.PruneUsers(600f);
            Assert.AreEqual(0, lim.TrackedUserCount);
            Assert.AreEqual(GateResult.Pass, lim.Check(r, g, "u1"));
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: Test Runner → EditMode → Run All
Expected: 编译失败，`TriggerLimiter` 不存在。

- [ ] **Step 3: 实现限流器**

`Core/TriggerLimiter.cs`：

```csharp
using System;
using System.Collections.Generic;

namespace DouyinLive
{
    // Check 返回被哪道闸拦下 —— 调玩法时能一眼看出该调哪个参数
    public enum GateResult
    {
        Pass,
        SourceCooldown,   // chatCooldown / likeCooldown / giftCooldown
        UserCooldown,     // perUserCooldown
        RuleCooldown,     // rule.cooldown
        LevelInterval     // l2MinInterval / l3MinInterval
    }

    // 四道限流闸，逐层收紧，全部通过才算过。
    // 刻意不采用「弹幕不能触发 L3」那种按来源禁止的做法：真正要防的是
    // 大效果连环炸，而不是弹幕这个来源有罪。禁掉来源会让观众永远玩不了
    // 换角色和点舞 —— 恰恰是低人气直播间最需要的零成本参与点。
    public class TriggerLimiter
    {
        // 时间源可注入，这样冷却逻辑能在 EditMode 测试里跑而不用进播放模式
        public Func<float> Now = () => 0f;

        readonly Dictionary<string, float> lastBySource = new Dictionary<string, float>();
        readonly Dictionary<string, float> lastByRule = new Dictionary<string, float>();
        readonly Dictionary<string, float> lastByLevel = new Dictionary<string, float>();
        readonly Dictionary<string, float> lastByUser = new Dictionary<string, float>();

        public int TrackedUserCount => lastByUser.Count;

        // 不产生任何副作用，可以重复调用。放行后必须显式 Commit 才记账 ——
        // ActionDirector 会有「检查通过但改为排队、暂不执行」的场景。
        public GateResult Check(TriggerRule rule, TriggerGlobal g, string userId)
        {
            if (rule == null) return GateResult.Pass;
            if (g == null) g = new TriggerGlobal();
            float now = Now();

            float srcCd = SourceCooldown(rule.source, g);
            if (srcCd > 0f && Elapsed(lastBySource, rule.source, now) < srcCd)
                return GateResult.SourceCooldown;

            float userCd = rule.perUserCooldown >= 0f ? rule.perUserCooldown : g.perUserCooldown;
            // UserId 缺失时（部分事件没带）退化为不限制，其余三道闸照常生效
            if (userCd > 0f && !string.IsNullOrEmpty(userId) &&
                Elapsed(lastByUser, UserKey(rule, userId), now) < userCd)
                return GateResult.UserCooldown;

            if (rule.cooldown > 0f && Elapsed(lastByRule, RuleKey(rule), now) < rule.cooldown)
                return GateResult.RuleCooldown;

            float lvlCd = LevelInterval(rule.LevelOrDefault, g);
            if (lvlCd > 0f && Elapsed(lastByLevel, LevelKey(rule), now) < lvlCd)
                return GateResult.LevelInterval;

            return GateResult.Pass;
        }

        public void Commit(TriggerRule rule, TriggerGlobal g, string userId)
        {
            if (rule == null) return;
            float now = Now();
            lastBySource[rule.source ?? ""] = now;
            lastByRule[RuleKey(rule)] = now;
            lastByLevel[LevelKey(rule)] = now;
            if (!string.IsNullOrEmpty(userId)) lastByUser[UserKey(rule, userId)] = now;
        }

        // 长时间直播会让按 UserId 的记账表无限增长，定期清理不活跃条目
        public void PruneUsers(float idleSeconds)
        {
            float now = Now();
            var stale = new List<string>();
            foreach (var kv in lastByUser)
                if (now - kv.Value >= idleSeconds) stale.Add(kv.Key);
            foreach (var k in stale) lastByUser.Remove(k);
        }

        public void Reset()
        {
            lastBySource.Clear();
            lastByRule.Clear();
            lastByLevel.Clear();
            lastByUser.Clear();
        }

        static float SourceCooldown(string source, TriggerGlobal g)
        {
            switch (source)
            {
                case "like": return g.likeCooldown;
                case "gift": return g.giftCooldown;
                default: return g.chatCooldown;   // chat / follow / enter / share
            }
        }

        static float LevelInterval(int level, TriggerGlobal g)
            => level == 3 ? g.l3MinInterval : level == 2 ? g.l2MinInterval : 0f;

        // 单人冷却按「用户 × 规则」记账：一个人刚点过舞，不该连带把他的拍头也冻住
        static string UserKey(TriggerRule r, string userId) => userId + "\u0001" + RuleKey(r);
        static string RuleKey(TriggerRule r) => string.IsNullOrEmpty(r.id) ? r.source + "\u0002" + r.level : r.id;
        static string LevelKey(TriggerRule r) => "L" + r.LevelOrDefault;

        static float Elapsed(Dictionary<string, float> map, string key, float now)
            => map.TryGetValue(key, out float last) ? now - last : float.MaxValue;
    }
}
```

- [ ] **Step 4: 跑测试**

Run: Test Runner → EditMode → Run All
Expected: 全部 PASS（累计 30 个）。

> **若 `单人冷却只冻结该用户不影响别人` 失败**：检查 `UserKey` 是否真的把 userId 拼进了 key。
> **若 `L3最小间隔跨规则生效` 失败**：检查 `LevelKey` 是否只用了层级、没混入 rule.id。

- [ ] **Step 5: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/"
git commit -m "$(cat <<'EOF'
feat(douyin-live): add four-stage trigger rate limiter

Source cooldown, per-user cooldown, per-rule cooldown, per-level interval.
Per-user is the main anti-spam lever: it freezes only the spammer, unlike a
global danmaku cooldown which would punish every viewer.

Check() is side-effect free; callers Commit() separately so ActionDirector can
queue an L3 without burning its cooldown.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: 配置读写与默认文件生成

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/TriggerConfigStore.cs`

**Interfaces:**
- Consumes: `TriggerConfig`（Task 2）
- Produces: `TriggerConfigStore.Path → string`、`TriggerConfigStore.LoadOrCreate() → TriggerConfig`、`TriggerConfigStore.TryParse(string json, out TriggerConfig cfg, out string error) → bool`、`TriggerConfigStore.WriteDefaultsWithComments()`

- [ ] **Step 1: 实现**

`TriggerConfigStore.cs`：

```csharp
using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace DouyinLive
{
    // douyin_triggers.json 的读写。放在 Assembly-CSharp 而不是 Core：
    // 它需要 Newtonsoft 和 Application.persistentDataPath，而 Core 要保持零外部依赖。
    public static class TriggerConfigStore
    {
        public const string FileName = "douyin_triggers.json";

        public static string Path => System.IO.Path.Combine(Application.persistentDataPath, FileName);

        // 反序列化必须传 Replace：默认的 Auto 会复用字段初始值建好的集合再追加
        // 磁盘内容，本仓库已经因此让 douyinIdleSongList 涨到过 166 条。
        static readonly JsonSerializerSettings LoadSettings = new JsonSerializerSettings
        {
            ObjectCreationHandling = ObjectCreationHandling.Replace
        };

        public static bool TryParse(string json, out TriggerConfig cfg, out string error)
        {
            cfg = null;
            error = null;
            try
            {
                cfg = JsonConvert.DeserializeObject<TriggerConfig>(json, LoadSettings);
                if (cfg == null) { error = "解析结果为空"; return false; }
                if (cfg.global == null) cfg.global = new TriggerGlobal();
                if (cfg.rules == null) cfg.rules = new System.Collections.Generic.List<TriggerRule>();
                Validate(cfg);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // 只警告不拦截：配错一条规则不该让整份配置作废
        static void Validate(TriggerConfig cfg)
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < cfg.rules.Count; i++)
            {
                var r = cfg.rules[i];
                if (r == null) continue;
                if (string.IsNullOrEmpty(r.id)) r.id = $"rule{i}";
                if (!seen.Add(r.id))
                    Debug.LogWarning($"[Triggers] 规则 id 重复: {r.id}（冷却会被这些规则共享）");
                if (r.effects == null || r.effects.Count == 0)
                    Debug.LogWarning($"[Triggers] 规则 {r.id} 没配任何效果，命中后什么都不会发生");
                if (!string.IsNullOrWhiteSpace(r.regex))
                {
                    try { System.Text.RegularExpressions.Regex.IsMatch("", r.regex); }
                    catch (ArgumentException ex)
                    { Debug.LogWarning($"[Triggers] 规则 {r.id} 的正则无效，将永不命中: {ex.Message}"); }
                }
            }
        }

        public static TriggerConfig LoadOrCreate()
        {
            try
            {
                if (File.Exists(Path))
                {
                    if (TryParse(File.ReadAllText(Path), out var cfg, out string err)) return cfg;
                    Debug.LogError($"[Triggers] {FileName} 解析失败，改用默认配置: {err}");
                    return TriggerConfig.Defaults();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Triggers] 读取配置失败: " + ex.Message);
                return TriggerConfig.Defaults();
            }

            WriteDefaultsWithComments();
            return TriggerConfig.Defaults();
        }

        // 首次运行写出的文件带注释，本身就是给用户看的文档 —— README 只需要指路。
        // 用手写头注释 + 序列化正文，避免引入 jsonc 解析依赖（Newtonsoft 读取时会忽略 // 注释）。
        public static void WriteDefaultsWithComments()
        {
            try
            {
                string body = JsonConvert.SerializeObject(TriggerConfig.Defaults(), Formatting.Indented);
                string header =
"// 抖音直播触发规则。改完存盘即生效，不用重启程序。\n" +
"// 解析失败会保留上一份可用配置并在日志里报错，不会让直播间哑掉。\n" +
"//\n" +
"// global 四道限流闸（一个请求要全部通过才执行）：\n" +
"//   chatCooldown/likeCooldown/giftCooldown  该来源的整体节奏\n" +
"//   perUserCooldown  同一观众的间隔，防单人刷屏的主力（只冻结他自己）\n" +
"//   cooldown         写在单条规则里，该玩法自己的节奏\n" +
"//   l2MinInterval / l3MinInterval  跨规则的层级总闸\n" +
"//\n" +
"// source: chat|like|follow|gift|enter|share\n" +
"// level:  L1 轻叠加(不打断唱歌) | L2 普通互动(唱歌时只出粒子) | L3 重磅独占\n" +
"// pick:   all 全部执行 | random 随机选一个\n" +
"//\n" +
"// 可用效果（详见 README）：\n" +
"//   anim:<Animator参数名>   现有参数只有 Headpat / HairStroke /\n" +
"//                           HoverFaceTrigger / HoverTrigger / IntimeRegion\n" +
"//   face:Happy|Angry|Cry|Fear\n" +
"//   mood:happy|love|sad|surprise\n" +
"//   particle:<主题名>       目前只有 \"Dance Trail Blue\" 一个主题\n" +
"//   bigscreen               大头特写\n" +
"//   dance:random | dance:<舞名> | dance:builtin\n" +
"//   song:<歌名> | song:request\n" +
"//   swapAvatar | outfit:random | outfit:<配件名>\n" +
"//   say:<文本> | sayAI:<给AI的提示> | menu\n" +
"//   say 支持占位符 {u}=昵称 {g}=礼物名 {n}=数量\n" +
"//\n" +
"// 规则按数组顺序匹配，第一条命中即停 —— 更具体的规则写在前面。\n";
                File.WriteAllText(Path, header + body);
                Debug.Log($"[Triggers] 已生成默认配置: {Path}");
            }
            catch (Exception ex)
            {
                Debug.LogError("[Triggers] 写默认配置失败: " + ex.Message);
            }
        }
    }
}
```

- [ ] **Step 2: 加解析测试**

这些测试要用到 Newtonsoft，而测试程序集只引用了 Core。**因此把解析测试放在 Core 侧不可行。** 改为在 `Tests/` 里测试**不依赖 Newtonsoft 的那部分契约**，并把 json 解析交给 Step 3 的手工验证。

在 `Tests/TriggerRulesTests.cs` 里追加：

```csharp
        [Test]
        public void 默认配置里引用的Animator参数都是项目里真实存在的()
        {
            // AvatarAnimatorControllerV2 目前只有这 5 个可用于一次性互动的参数。
            // 以后加了新动画，把参数名补进这个白名单。
            var known = new HashSet<string>
            { "Headpat", "HairStroke", "HoverFaceTrigger", "HoverTrigger", "IntimeRegion" };

            foreach (var r in TriggerConfig.Defaults().rules)
                foreach (var e in r.effects)
                    if (e.StartsWith("anim:"))
                        Assert.Contains(e.Substring(5), known, $"规则 {r.id} 用了不存在的 Animator 参数");
        }

        [Test]
        public void 默认配置里引用的粒子主题都存在()
        {
            // CustomVRM.prefab 目前只登记了这一个主题
            var known = new HashSet<string> { "Dance Trail Blue" };
            foreach (var r in TriggerConfig.Defaults().rules)
                foreach (var e in r.effects)
                    if (e.StartsWith("particle:"))
                        Assert.Contains(e.Substring(9), known, $"规则 {r.id} 用了不存在的粒子主题");
        }
```

- [ ] **Step 3: 手工验证默认文件生成与坏 json 容错**

先删掉可能存在的旧文件，然后在 Unity 编辑器里进入播放模式（此时还没接线，可临时用一个菜单项或直接在 Console 执行）。**简单做法**：临时在 `TriggerConfigStore` 上加一个编辑器菜单项：

```csharp
#if UNITY_EDITOR
        [UnityEditor.MenuItem("MateEngine/抖音直播/重新生成触发规则文件")]
        static void RegenerateMenu() => WriteDefaultsWithComments();
#endif
```

（这个菜单项**保留**，实际使用时也用得上。）

```bash
# 1) 生成：菜单 MateEngine → 抖音直播 → 重新生成触发规则文件
cat "$LOCALAPPDATA/../LocalLow/Shinymoon/MateEngineX/douyin_triggers.json" | head -40
# 2) 故意写坏：在文件开头插一个多余的 {
# 3) 再次通过菜单/播放模式加载，Console 应出现
#    "[Triggers] douyin_triggers.json 解析失败，改用默认配置: ..."
#    且不应有任何未捕获异常
```

- [ ] **Step 4: 跑测试**

Run: Test Runner → EditMode → Run All
Expected: 全部 PASS（累计 32 个）。

- [ ] **Step 5: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/"
git commit -m "$(cat <<'EOF'
feat(douyin-live): load/generate douyin_triggers.json

The generated default file carries a commented header documenting every
effect id and limiter knob, so the file itself is the user-facing doc.

Parse failures keep the last good config and log an error rather than
throwing - a typo in the config must never silence the live room.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: 效果注册表（第一期效果）

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/EffectRegistry.cs`

**Interfaces:**
- Consumes: `DouyinEvent`（Task 1）、`TriggerRule`（Task 2）
- Produces: `class EffectContext { DouyinEvent Event; TriggerRule Rule; bool SingingNow; }`、`EffectRegistry`（MonoBehaviour）带 `Execute(string effectId, EffectContext ctx)`、`FillPlaceholders(string tpl, DouyinEvent ev) → string`

本期实现 `anim:` / `face:` / `mood:` / `particle:` / `say:` / `menu`，其余 ID 记录为「未实现」警告，第二期补齐。

- [ ] **Step 1: 实现**

`EffectRegistry.cs`：

```csharp
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DouyinLive
{
    public class EffectContext
    {
        public DouyinEvent Event;
        public TriggerRule Rule;
        public bool SingingNow;    // 唱歌时 L2 只放粒子/表情，不播动画
    }

    // 字符串 ID → 具体执行器。用字符串而不是枚举，是为了让「以后往 Animator
    // 里加了新动画只改 json 不改代码」这件事真正成立。
    [RequireComponent(typeof(SpeechPipeline))]
    public class EffectRegistry : MonoBehaviour
    {
        public bool debugLog = false;

        SpeechPipeline speech;
        Animator avatarAnimator;
        AvatarParticleHandler particles;

        string particleThemeBeforeOverride;
        Coroutine particleRestore;

        // 未知/未实现的 ID 只警告一次，避免刷屏
        readonly HashSet<string> warned = new HashSet<string>();

        void Awake() { speech = GetComponent<SpeechPipeline>(); }

        // 换角色后 Animator 实例会变，每次执行前按需重解析
        void ResolveAvatar()
        {
            if (avatarAnimator != null && avatarAnimator.gameObject.activeInHierarchy) return;
            var loader = FindFirstObjectByType<VRMLoader>();
            var model = loader != null ? loader.GetCurrentModel() : null;
            if (model == null)
            {
                var ctrl = FindFirstObjectByType<AvatarAnimatorController>();
                model = ctrl != null ? ctrl.gameObject : null;
            }
            avatarAnimator = model != null ? model.GetComponentInChildren<Animator>(true) : null;
        }

        AvatarParticleHandler Particles
        {
            get
            {
                if (particles == null) particles = FindFirstObjectByType<AvatarParticleHandler>(FindObjectsInactive.Include);
                return particles;
            }
        }

        public void Execute(string effectId, EffectContext ctx)
        {
            if (string.IsNullOrWhiteSpace(effectId)) return;
            string id = effectId.Trim();
            string arg = "";
            int colon = id.IndexOf(':');
            if (colon >= 0) { arg = id.Substring(colon + 1); id = id.Substring(0, colon); }

            if (debugLog) Debug.Log($"[Effect] {id}:{arg}");

            switch (id)
            {
                case "anim":     if (!ctx.SingingNow || Level(ctx) == 1) PulseAnim(arg); break;
                case "face":     if (!ctx.SingingNow || Level(ctx) == 1) PlayFace(arg);  break;
                case "mood":     SetMood(arg);            break;
                case "particle": OverrideParticle(arg);   break;
                case "say":      Say(FillPlaceholders(arg, ctx.Event)); break;
                case "menu":     SayMenu();               break;
                default:         WarnOnce(id);            break;
            }
        }

        static int Level(EffectContext ctx) => ctx.Rule != null ? ctx.Rule.LevelOrDefault : 1;

        void WarnOnce(string id)
        {
            if (warned.Add(id))
                Debug.LogWarning($"[Effect] 未知或尚未实现的效果: {id}（该效果被跳过，同规则的其它效果照常执行）");
        }

        // ---------- anim ----------

        void PulseAnim(string param)
        {
            ResolveAvatar();
            if (avatarAnimator == null || string.IsNullOrEmpty(param)) return;

            var p = System.Array.Find(avatarAnimator.parameters,
                x => x.name == param && x.type == AnimatorControllerParameterType.Bool);
            if (p == null) { WarnOnce("anim:" + param); return; }

            StartCoroutine(PulseBool(param, 0.4f));
        }

        IEnumerator PulseBool(string param, float seconds)
        {
            avatarAnimator.SetBool(param, true);
            yield return new WaitForSeconds(seconds);
            if (avatarAnimator != null) avatarAnimator.SetBool(param, false);
        }

        // ---------- face ----------

        void PlayFace(string state)
        {
            ResolveAvatar();
            if (avatarAnimator == null || string.IsNullOrEmpty(state)) return;

            for (int layer = 0; layer < avatarAnimator.layerCount; layer++)
            {
                if (!avatarAnimator.HasState(layer, Animator.StringToHash(state))) continue;
                avatarAnimator.CrossFadeInFixedTime(state, 0.2f, layer);
                return;
            }
            WarnOnce("face:" + state);
        }

        // ---------- mood ----------

        // SpeechPipeline 的表情驱动是私有的，这里直接写 UniversalBlendshapes，
        // 并复用它的 0.8 强度约定，两边表现保持一致。
        void SetMood(string mood)
        {
            var bs = FindFirstObjectByType<UniversalBlendshapes>();
            if (bs == null) return;

            switch (mood)
            {
                case "happy":    bs.Joy = 0.8f; break;
                case "love":     bs.Fun = 0.8f; break;
                case "sad":      bs.Sorrow = 0.8f; break;
                case "surprise": bs.Joy = 0.5f; bs.Fun = 0.5f; break;
                default: WarnOnce("mood:" + mood); return;
            }
            // SpeechPipeline 每帧都在 MoveTowards 归位，不需要在这里主动清除
        }

        // ---------- particle ----------

        void OverrideParticle(string theme)
        {
            var ph = Particles;
            if (ph == null || string.IsNullOrEmpty(theme)) return;

            // 主题名打错时 SetTheme 不会报错，只是静默无效果 —— 主动检出来
            bool exists = false;
            foreach (var r in ph.rules)
                if (r != null && r.themeTag == theme) { exists = true; break; }
            if (!exists) { WarnOnce("particle:" + theme); return; }

            if (particleRestore == null) particleThemeBeforeOverride = ph.selectedTheme;
            else StopCoroutine(particleRestore);

            ph.SetTheme(theme);
            particleRestore = StartCoroutine(RestoreParticleAfter(6f));
        }

        IEnumerator RestoreParticleAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            var ph = Particles;
            if (ph != null && !string.IsNullOrEmpty(particleThemeBeforeOverride))
                ph.SetTheme(particleThemeBeforeOverride);
            particleRestore = null;
        }

        // ---------- say ----------

        void Say(string text)
        {
            if (speech == null || string.IsNullOrWhiteSpace(text)) return;
            speech.Enqueue(text, SpeechPipeline.Priority.GiftThanks, 30f);
        }

        void SayMenu()
        {
            Say("给大家报下玩法哦：发 点歌加歌名 我就唱给你听；发 换角色 我就换一身新形象；" +
                "发 拍头、捋头发、抱抱 都能和我互动；点赞关注我都会感谢，送礼物还能看我跳舞哦~");
        }

        public static string FillPlaceholders(string tpl, DouyinEvent ev)
        {
            if (string.IsNullOrEmpty(tpl) || ev == null) return tpl;
            string name = string.IsNullOrEmpty(ev.Nickname) ? "朋友" : ev.Nickname;
            return tpl.Replace("{u}", name)
                      .Replace("{g}", ev.GiftName ?? "")
                      .Replace("{n}", ev.GiftCount.ToString());
        }
    }
}
```

- [ ] **Step 2: 编译检查**

回到 Unity 编辑器，等待编译完成。
Expected: Console 无编译错误。

> `UniversalBlendshapes` 第 11 行确认是 `[Range(0f,1f)] public float A, I, U, E, O, Joy, Angry, Sorrow, Fun;`，
> 直接赋值可用。它自带 `fadeSpeed` 归位机制，所以 `SetMood` 只写一次值、不需要主动清除。

- [ ] **Step 3: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/"
git commit -m "$(cat <<'EOF'
feat(douyin-live): add effect registry with phase-1 effects

anim:/face:/mood:/particle:/say:/menu. Unknown ids warn once and are skipped;
the rule's other effects still run.

particle: verifies the theme actually exists in AvatarParticleHandler.rules -
SetTheme accepts any string and silently does nothing for unknown tags, and
only one theme is registered today.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: 路由器接线，第一期打通

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/TriggerRouter.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs`（`Route()` 约 222-257 行、`ApplySettings()`、`Update()`）

**Interfaces:**
- Consumes: `TriggerConfigStore`（Task 5）、`TriggerMatcher` / `MatchContext`（Task 3）、`TriggerLimiter` / `GateResult`（Task 4）、`EffectRegistry` / `EffectContext`（Task 6）
- Produces: `TriggerRouter`（MonoBehaviour）带 `bool TryHandle(DouyinEvent ev)`（true = 已被触发层消费）、`void Tick()`、`void Reload()`

- [ ] **Step 1: 实现路由器**

`TriggerRouter.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DouyinLive
{
    // 读 douyin_triggers.json，把事件匹配成效果并执行。
    // 旁路式：TryHandle 返回 false 时，DouyinLiveManager 继续走原有代码路径，
    // 所以删掉配置文件就完全回退到接入本层之前的行为。
    [RequireComponent(typeof(EffectRegistry))]
    public class TriggerRouter : MonoBehaviour
    {
        public bool debugLog = false;

        public TriggerConfig Config { get; private set; }

        EffectRegistry effects;
        SongService song;
        readonly TriggerLimiter limiter = new TriggerLimiter();
        readonly System.Random rng = new System.Random();

        long likeTotal;
        FileSystemWatcher watcher;
        volatile bool reloadRequested;
        float reloadAt;                 // debounce：编辑器保存常触发多次事件
        float nextPruneAt;

        void Awake()
        {
            effects = GetComponent<EffectRegistry>();
            limiter.Now = () => Time.unscaledTime;
            Config = TriggerConfigStore.LoadOrCreate();
            StartWatching();
        }

        public void Reload()
        {
            var cfg = TriggerConfigStore.LoadOrCreate();
            if (cfg != null) Config = cfg;   // 解析失败时 LoadOrCreate 已经报过错并给了兜底
            Debug.Log($"[Triggers] 已重新加载，共 {Config.rules.Count} 条规则");
        }

        public void ResetSession()
        {
            likeTotal = 0;
            limiter.Reset();
        }

        // ---------- 热重载 ----------

        void StartWatching()
        {
            try
            {
                string dir = Path.GetDirectoryName(TriggerConfigStore.Path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                watcher = new FileSystemWatcher(dir, TriggerConfigStore.FileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                };
                // 回调在后台线程，只置标志位，真正的重载放到 Tick 里做
                watcher.Changed += (_, __) => reloadRequested = true;
                watcher.Created += (_, __) => reloadRequested = true;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Triggers] 无法监听配置文件变更，改配置后需重启程序: " + ex.Message);
            }
        }

        public void Tick()
        {
            if (reloadRequested && reloadAt <= 0f)
                reloadAt = Time.unscaledTime + 0.5f;   // debounce 500ms

            if (reloadAt > 0f && Time.unscaledTime >= reloadAt)
            {
                reloadRequested = false;
                reloadAt = 0f;
                Reload();
            }

            if (Time.unscaledTime >= nextPruneAt)
            {
                nextPruneAt = Time.unscaledTime + 300f;   // 每 5 分钟清一次
                limiter.PruneUsers(600f);
            }
        }

        void OnDestroy()
        {
            if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
        }

        // ---------- 路由 ----------

        // 返回 true 表示该事件已被触发层消费，调用方不再走原有逻辑
        public bool TryHandle(DouyinEvent ev)
        {
            if (ev == null || Config == null) return false;

            var ctx = new MatchContext { LikeTotalBefore = likeTotal };
            if (ev.Type == DouyinMsgType.Like) likeTotal += Math.Max(1, ev.LikeCount);
            ctx.LikeTotalAfter = likeTotal;

            var rule = TriggerMatcher.Match(ev, Config, ctx);
            if (rule == null) return false;

            var gate = limiter.Check(rule, Config.global, ev.UserId);
            if (gate != GateResult.Pass)
            {
                if (debugLog) Debug.Log($"[Triggers] 规则 {rule.id} 被 {gate} 拦下");
                return true;   // 已匹配但被限流：消费掉，不要再退回去触发 AI 回复
            }

            limiter.Commit(rule, Config.global, ev.UserId);
            Run(rule, ev);
            return true;
        }

        void Run(TriggerRule rule, DouyinEvent ev)
        {
            if (song == null) song = GetComponent<SongService>();

            var ctx = new EffectContext
            {
                Event = ev,
                Rule = rule,
                SingingNow = song != null && song.IsPlaying
            };

            var list = rule.effects;
            if (list == null || list.Count == 0) return;

            if (rule.pick == "random")
            {
                effects.Execute(list[rng.Next(list.Count)], ctx);
                return;
            }
            foreach (var e in list) effects.Execute(e, ctx);
        }
    }
}
```

- [ ] **Step 2: 在 DouyinLiveManager 里接线**

在字段区（约第 37 行 `bool audienceLoaded;` 附近）加：

```csharp
        TriggerRouter triggers;
```

在 `ApplySettings()` 的「竖屏直播窗口」段落**之前**插入：

```csharp
            // 可配置触发层：命中 douyin_triggers.json 的规则就由它接管，
            // 未命中才走下面各 Service 的原有逻辑（旁路式，删配置文件即回退）
            if (triggers == null)
            {
                if (GetComponent<EffectRegistry>() == null) gameObject.AddComponent<EffectRegistry>();
                triggers = GetComponent<TriggerRouter>();
                if (triggers == null) triggers = gameObject.AddComponent<TriggerRouter>();
            }
            triggers.debugLog = debugLog;
```

- [ ] **Step 3: 在 Route() 里前置匹配**

把 `Route(DouyinEvent ev)` 开头改成：

```csharp
        void Route(DouyinEvent ev)
        {
            if (debugLog) Debug.Log($"[DouyinLive] {ev.Type} {ev.Nickname}: {ev.Content}{ev.GiftName}");
            idleChatter.NotifyInteraction();   // 任何观众事件都重置冷场计时

            // 观众记忆/房间上下文要在触发层之前记账：即使这条弹幕被规则消费掉，
            // 它也应该计入观众画像，否则 AI 回复会丢失上下文。
            if (ev.Type == DouyinMsgType.Chat)
            {
                audience.RecordMessage(ev.UserId, ev.Nickname, ev.Content);
                room.AddChat(ev.Nickname, ev.Content);
            }
            else if (ev.Type == DouyinMsgType.Gift)
            {
                danmakuAI.MarkGifter(ev.UserId);
                int value = Mathf.Max(1, ev.DiamondCount) * Mathf.Max(1, ev.GiftCount);
                audience.RecordGift(ev.UserId, ev.Nickname, value);
                room.LastGiftDesc = $"{ev.Nickname}送的{ev.GiftName}";
                liveOps.RecordGift(ev.UserId, ev.Nickname, value);
            }

            if (triggers != null && triggers.TryHandle(ev)) return;

            switch (ev.Type)
            {
                case DouyinMsgType.Chat:
                    if (reward.TryHandleDanmaku(ev)) return;
                    danmakuAI.OnDanmaku(ev);
                    break;
                case DouyinMsgType.Like:
                    like.OnEvent(ev);
                    break;
                case DouyinMsgType.Enter:
                case DouyinMsgType.Share:
                case DouyinMsgType.FansClub:
                    welcome.OnEvent(ev);
                    break;
                case DouyinMsgType.Follow:
                    welcome.OnEvent(ev);
                    TriggerBigHeadMoment();   // 关注 → 大头特写致谢
                    break;
                case DouyinMsgType.Gift:
                    reward.OnGift(ev);
                    TriggerBigHeadMoment();   // 礼物 → 大头特写致谢
                    break;
            }
        }
```

> 这里把记账逻辑从 `switch` 里提到触发层之前，是因为触发层命中后会 `return`。
> 不提前的话，被规则消费掉的弹幕就不会进观众记忆，AI 回复会认不出常客。

- [ ] **Step 4: 在 Update() 里驱动 Tick，在 StartLive 里重置会话**

`Update()` 的 `if (!blocked) { ... }` 块里，`welcome.Tick();` 之前加：

```csharp
                if (triggers != null) triggers.Tick();
```

`StartLive()` 里 `idleChatter.ResetSession();` 之后加：

```csharp
            if (triggers != null) triggers.ResetSession();
```

- [ ] **Step 5: 编译并用 mock server 手工验证**

```bash
cd "e:/Work/AI/Mate-Engine" && python Tools/douyin_mock_server.py
```

在 Unity 播放模式下（或跑打包版）依次验证，`debugLog` 打开看 Console：

| 输入 | 预期 |
|---|---|
| `c 主播拍头` | 角色播 Headpat 动画；Console 有 `[Effect] anim:Headpat` |
| `c 主播拍头`（1 秒内再发一次，同一用户）| Console 出现 `规则 pat 被 UserCooldown 拦下`，不重复播 |
| `c 今天天气不错` | **不**被触发层消费，走 AI 回复（Console 有 AI 请求日志）|
| `l`（连点到累计 30）| 随机播一个 L1 动作 |
| `f`（关注）| 目前 `bigscreen` 未实现 → Console 有「未知或尚未实现的效果: bigscreen」警告，但 `say:` 和 `particle:` 正常执行 |
| 编辑 json 把 `拍头` 改成 `摸摸`，存盘 | 0.5 秒内 Console 出现 `[Triggers] 已重新加载`；发 `c 摸摸` 生效，发 `c 拍头` 不生效 |

- [ ] **Step 6: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/"
git commit -m "$(cat <<'EOF'
feat(douyin-live): wire the configurable trigger layer into Route()

Bypass layer: TryHandle returns false when no rule matches and the existing
per-service logic runs unchanged, so deleting douyin_triggers.json fully
reverts to the previous behaviour.

Audience/room bookkeeping moves ahead of the trigger check - a danmaku
consumed by a rule must still count toward the viewer profile, or AI replies
stop recognising regulars.

Config hot-reloads on save via FileSystemWatcher with a 500ms debounce; the
watcher callback runs off the main thread so it only sets a flag.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

**第一期完成。** 此时改 json 就能配弹幕关键词玩法，重磅效果尚未接入。

---

## 第二期：分层仲裁与重磅效果

### Task 8: ActionDirector 分层仲裁与 L3 队列

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/ActionArbiter.cs`
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/ActionArbiterTests.cs`
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/ActionDirector.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/TriggerRouter.cs`（`Run` 改为提交给 Director）

**Interfaces:**
- Consumes: `TriggerRule`、`TriggerGlobal`、`TriggerLimiter`
- Produces:
  - `class ActionRequest { TriggerRule Rule; string UserId; string Nickname; string GiftName; int GiftCount; }`
  - `enum ArbitrationResult { Execute, Queued, DroppedQueueFull, DeferredBusy }`
  - `ActionArbiter`（纯逻辑，Core）：`Func<float> Now`、`bool SingingNow`、`bool L3Busy`、`Submit(ActionRequest, TriggerGlobal) → ArbitrationResult`、`TryDequeueL3(TriggerGlobal) → ActionRequest`、`int QueuedCount`
  - `ActionDirector`（MonoBehaviour）：`void Submit(TriggerRule rule, DouyinEvent ev, TriggerGlobal g)`、`void Tick(TriggerGlobal g)`、`void ResetSession()`、`ActionArbiter Arbiter { get; }`

- [ ] **Step 1: 先写仲裁测试**

`Tests/ActionArbiterTests.cs`：

```csharp
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class ActionArbiterTests
    {
        float clock;
        ActionArbiter arb;
        TriggerGlobal g;

        [SetUp]
        public void SetUp()
        {
            clock = 1000f;
            arb = new ActionArbiter { Now = () => clock };
            g = new TriggerGlobal();
        }

        static ActionRequest Req(string id, string level, string user = "u1")
            => new ActionRequest
            {
                Rule = new TriggerRule { id = id, level = level, source = "gift" },
                UserId = user
            };

        [Test]
        public void L1L2在空闲时直接执行()
        {
            Assert.AreEqual(ArbitrationResult.Execute, arb.Submit(Req("a", "L1"), g));
            Assert.AreEqual(ArbitrationResult.Execute, arb.Submit(Req("b", "L2"), g));
        }

        [Test]
        public void L1在唱歌时照常执行()
        {
            arb.SingingNow = true;
            Assert.AreEqual(ArbitrationResult.Execute, arb.Submit(Req("a", "L1"), g));
        }

        [Test]
        public void L2在唱歌时也执行只是效果会被降级()
        {
            // 降级（只放粒子不播动画）由 EffectRegistry 按 SingingNow 判断，
            // 仲裁层不拦：拦了就连粒子都没有了
            arb.SingingNow = true;
            Assert.AreEqual(ArbitrationResult.Execute, arb.Submit(Req("a", "L2"), g));
        }

        [Test]
        public void L3在有其它L3执行时进队列()
        {
            Assert.AreEqual(ArbitrationResult.Execute, arb.Submit(Req("a", "L3"), g));
            arb.L3Busy = true;
            Assert.AreEqual(ArbitrationResult.Queued, arb.Submit(Req("b", "L3"), g));
            Assert.AreEqual(1, arb.QueuedCount);
        }

        [Test]
        public void 唱歌时L3默认排队等唱完()
        {
            arb.SingingNow = true;
            g.l3InterruptSinging = false;
            Assert.AreEqual(ArbitrationResult.Queued, arb.Submit(Req("a", "L3"), g));
        }

        [Test]
        public void 打开开关后L3可以打断唱歌()
        {
            arb.SingingNow = true;
            g.l3InterruptSinging = true;
            Assert.AreEqual(ArbitrationResult.Execute, arb.Submit(Req("a", "L3"), g));
        }

        [Test]
        public void 队列满时丢最旧的而不是拒绝新的()
        {
            arb.L3Busy = true;
            g.l3QueueSize = 2;
            arb.Submit(Req("first", "L3"), g);
            arb.Submit(Req("second", "L3"), g);
            Assert.AreEqual(ArbitrationResult.DroppedQueueFull, arb.Submit(Req("third", "L3"), g));
            Assert.AreEqual(2, arb.QueuedCount);
            // 最旧的 first 被挤掉
            arb.L3Busy = false;
            Assert.AreEqual("second", arb.TryDequeueL3(g).Rule.id);
        }

        [Test]
        public void 队列里已有同id的L3不重复入队()
        {
            arb.L3Busy = true;
            arb.Submit(Req("dance", "L3"), g);
            Assert.AreEqual(ArbitrationResult.Queued, arb.Submit(Req("dance", "L3", "u2"), g));
            Assert.AreEqual(1, arb.QueuedCount);
        }

        [Test]
        public void 忙碌时不出队()
        {
            arb.L3Busy = true;
            arb.Submit(Req("a", "L3"), g);
            Assert.IsNull(arb.TryDequeueL3(g));
            arb.L3Busy = false;
            Assert.IsNotNull(arb.TryDequeueL3(g));
        }

        [Test]
        public void 唱歌未结束时不出队()
        {
            arb.L3Busy = true;
            arb.Submit(Req("a", "L3"), g);
            arb.L3Busy = false;
            arb.SingingNow = true;
            Assert.IsNull(arb.TryDequeueL3(g));
            arb.SingingNow = false;
            Assert.IsNotNull(arb.TryDequeueL3(g));
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: Test Runner → EditMode → Run All
Expected: 编译失败，`ActionArbiter` 不存在。

- [ ] **Step 3: 实现仲裁器**

`Core/ActionArbiter.cs`：

```csharp
using System;
using System.Collections.Generic;

namespace DouyinLive
{
    public class ActionRequest
    {
        public TriggerRule Rule;
        public string UserId;
        public string Nickname;
        public string GiftName;
        public int GiftCount;
    }

    public enum ArbitrationResult
    {
        Execute,           // 立即执行
        Queued,            // L3 排队，等当前 L3 或唱歌结束
        DroppedQueueFull,  // 队列满，本条挤掉了最旧的一条
        DeferredBusy
    }

    // 三层仲裁的纯逻辑部分。不碰 Unity 场景，可在 EditMode 测试里跑。
    // 状态（在唱歌吗、L3 在忙吗）由 ActionDirector 每帧写进来。
    public class ActionArbiter
    {
        public Func<float> Now = () => 0f;

        public bool SingingNow;   // SongService.IsPlaying
        public bool L3Busy;       // 有 L3 正在执行

        readonly List<ActionRequest> l3Queue = new List<ActionRequest>();

        public int QueuedCount => l3Queue.Count;

        public ArbitrationResult Submit(ActionRequest req, TriggerGlobal g)
        {
            if (req?.Rule == null) return ArbitrationResult.DeferredBusy;
            if (g == null) g = new TriggerGlobal();

            // L1/L2 从不排队。L2 在唱歌时也放行 —— 降级成「只放粒子不播动画」
            // 由 EffectRegistry 按 SingingNow 决定；在这里拦掉就连粒子都没有了。
            if (req.Rule.LevelOrDefault < 3) return ArbitrationResult.Execute;

            bool blocked = L3Busy || (SingingNow && !g.l3InterruptSinging);
            if (!blocked) { L3Busy = true; return ArbitrationResult.Execute; }

            // 同一条规则已经在队列里就不重复入队：大哥连刷同一种礼物时，
            // 观众想看的是效果播出来，不是同一个效果排三遍。
            foreach (var q in l3Queue)
                if (q.Rule.id == req.Rule.id) return ArbitrationResult.Queued;

            l3Queue.Add(req);
            int cap = Math.Max(1, g.l3QueueSize);
            if (l3Queue.Count > cap)
            {
                // 丢最旧的：宁可少播一次，也不要观众等了几分钟才看到自己的效果
                l3Queue.RemoveAt(0);
                return ArbitrationResult.DroppedQueueFull;
            }
            return ArbitrationResult.Queued;
        }

        public ActionRequest TryDequeueL3(TriggerGlobal g)
        {
            if (g == null) g = new TriggerGlobal();
            if (l3Queue.Count == 0) return null;
            if (L3Busy) return null;
            if (SingingNow && !g.l3InterruptSinging) return null;

            var req = l3Queue[0];
            l3Queue.RemoveAt(0);
            L3Busy = true;
            return req;
        }

        public void NotifyL3Finished() => L3Busy = false;

        public void Reset()
        {
            l3Queue.Clear();
            L3Busy = false;
            SingingNow = false;
        }
    }
}
```

- [ ] **Step 4: 跑测试**

Run: Test Runner → EditMode → Run All
Expected: 全部 PASS（累计 42 个）。

- [ ] **Step 5: 实现 ActionDirector（场景侧的壳）**

`ActionDirector.cs`：

```csharp
using UnityEngine;
using CustomDancePlayer;

namespace DouyinLive
{
    // ActionArbiter 的场景侧外壳：每帧把「在唱歌吗/在跳舞吗」同步给仲裁器，
    // 驱动 L3 出队，并把闲聊打断的副作用落到 IdleChatterService 上。
    [RequireComponent(typeof(EffectRegistry))]
    public class ActionDirector : MonoBehaviour
    {
        public bool debugLog = false;

        public ActionArbiter Arbiter { get; } = new ActionArbiter();

        EffectRegistry effects;
        SongService song;
        AvatarDanceHandler dance;

        float l3StartedAt;
        const float L3MaxSeconds = 180f;   // 兜底：舞包异常没回调时也要放开独占

        void Awake()
        {
            effects = GetComponent<EffectRegistry>();
            Arbiter.Now = () => Time.unscaledTime;
        }

        SongService Song { get { if (song == null) song = GetComponent<SongService>(); return song; } }

        AvatarDanceHandler Dance
        {
            get
            {
                if (dance == null) dance = FindFirstObjectByType<AvatarDanceHandler>(FindObjectsInactive.Include);
                return dance;
            }
        }

        public void Submit(TriggerRule rule, DouyinEvent ev, TriggerGlobal g)
        {
            var req = new ActionRequest
            {
                Rule = rule,
                UserId = ev?.UserId,
                Nickname = ev?.Nickname,
                GiftName = ev?.GiftName,
                GiftCount = ev?.GiftCount ?? 0
            };

            var result = Arbiter.Submit(req, g);
            if (debugLog) Debug.Log($"[Director] {rule.id} L{rule.LevelOrDefault} → {result}");

            if (result == ArbitrationResult.Execute) Execute(req, ev);
        }

        public void Tick(TriggerGlobal g)
        {
            Arbiter.SingingNow = Song != null && Song.IsPlaying;

            // L3 结束判定：既不在唱也不在跳，或超过兜底时长
            if (Arbiter.L3Busy)
            {
                bool busy = (Song != null && Song.IsPlaying) || (Dance != null && Dance.IsPlaying);
                if (!busy && Time.unscaledTime - l3StartedAt > 2f) Arbiter.NotifyL3Finished();
                else if (Time.unscaledTime - l3StartedAt > L3MaxSeconds)
                {
                    Debug.LogWarning("[Director] L3 超过兜底时长仍未结束，强制放开独占");
                    Arbiter.NotifyL3Finished();
                }
            }

            var queued = Arbiter.TryDequeueL3(g);
            if (queued != null) Execute(queued, null);
        }

        void Execute(ActionRequest req, DouyinEvent ev)
        {
            if (req.Rule.LevelOrDefault == 3) l3StartedAt = Time.unscaledTime;

            // L2 打断闲聊的暖场话（不打断唱歌/跳舞）
            if (req.Rule.LevelOrDefault == 2)
                DouyinLiveManager.Instance?.InterruptIdleChatter();

            var ctx = new EffectContext
            {
                Event = ev ?? Rebuild(req),
                Rule = req.Rule,
                SingingNow = Song != null && Song.IsPlaying
            };

            var list = req.Rule.effects;
            if (list == null || list.Count == 0) return;

            if (req.Rule.pick == "random")
            {
                effects.Execute(list[Random.Range(0, list.Count)], ctx);
                return;
            }
            foreach (var e in list) effects.Execute(e, ctx);
        }

        // 排队的请求出队时原始 DouyinEvent 已经不在了，用请求里存的字段重建
        // 一个够 say: 占位符替换用的最小事件。
        static DouyinEvent Rebuild(ActionRequest req) => new DouyinEvent
        {
            UserId = req.UserId,
            Nickname = req.Nickname,
            GiftName = req.GiftName,
            GiftCount = req.GiftCount
        };

        public void ResetSession() => Arbiter.Reset();
    }
}
```

- [ ] **Step 6: 给 DouyinLiveManager 加 InterruptIdleChatter**

在 `DouyinLiveManager` 里加：

```csharp
        // L2 动作会打断闲聊的暖场话（唱歌/跳舞不受影响）
        public void InterruptIdleChatter()
        {
            idleChatter.NotifyInteraction();
        }
```

在 `IdleChatterService` 里，`NotifyInteraction()` 已经会重置冷场计时，够用，不用改它。

- [ ] **Step 7: TriggerRouter 改为提交给 Director**

删掉 `TriggerRouter.Run()` 和 `rng` 字段，改为：

```csharp
        ActionDirector director;

        // Awake() 里，effects 赋值之后加：
        director = GetComponent<ActionDirector>();
        if (director == null) director = gameObject.AddComponent<ActionDirector>();
```

`TryHandle` 末尾的 `Run(rule, ev);` 改成：

```csharp
            director.Submit(rule, ev, Config.global);
```

`Tick()` 末尾加：

```csharp
            director.Tick(Config.global);
```

`ResetSession()` 里加 `director.ResetSession();`。

- [ ] **Step 8: 跑测试并编译**

Run: Test Runner → EditMode → Run All
Expected: 42 个全 PASS，Console 无编译错误。

- [ ] **Step 9: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/"
git commit -m "$(cat <<'EOF'
feat(douyin-live): add three-tier action arbitration with an L3 queue

L3 requests queue instead of being dropped - a viewer who tips during a dance
should still see their effect. Queue drops the oldest when full, and refuses
duplicate rule ids so a repeat tipper doesn't queue the same effect three times.

L2 is never blocked while singing; the "particles only, no animation"
degradation happens in EffectRegistry, because blocking here would remove the
particles too.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 9: 重磅效果接入

**Files:**
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/EffectRegistry.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/RewardService.cs`（`SwitchRandomAvatar` 改 public）
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs`（`TriggerBigHeadMoment` 改 public）

**Interfaces:**
- Consumes: `EffectRegistry.Execute`（Task 6）、`ActionDirector`（Task 8）
- Produces: `EffectRegistry` 支持 `bigscreen` / `dance:random` / `dance:<名>` / `dance:builtin` / `song:<名>` / `song:request` / `swapAvatar` / `outfit:random` / `outfit:<名>`

- [ ] **Step 1: 开放两个现有入口**

`RewardService.cs` 第 92 行：`void SwitchRandomAvatar(string userName)` → `public void SwitchRandomAvatar(string userName)`

`DouyinLiveManager.cs` 第 263 行：`void TriggerBigHeadMoment()` → `public void TriggerBigHeadMoment()`

- [ ] **Step 2: 在 EffectRegistry 的 switch 里补分支**

```csharp
                case "bigscreen": DouyinLiveManager.Instance?.TriggerBigHeadMoment(); break;
                case "dance":     PlayDance(arg);          break;
                case "song":      PlaySong(arg, ctx);      break;
                case "swapAvatar":SwapAvatar(ctx);         break;
                case "outfit":    SwitchOutfit(arg);       break;
```

- [ ] **Step 3: 实现这些执行器**

在 `EffectRegistry` 里追加（文件顶部加 `using CustomDancePlayer;`）：

```csharp
        // ---------- dance ----------

        AvatarDanceHandler danceHandler;
        AvatarDanceHandler Dance
        {
            get
            {
                if (danceHandler == null)
                    danceHandler = FindFirstObjectByType<AvatarDanceHandler>(FindObjectsInactive.Include);
                return danceHandler;
            }
        }

        void PlayDance(string arg)
        {
            var d = Dance;
            if (arg == "builtin" || d == null || d.EntryCount <= 0) { PlayBuiltinDance(); return; }

            if (arg == "random")
            {
                // 洗牌轮播在第三期由 DanceDirector 接管；这一期先用现有的随机
                if (!d.PlayIndex(UnityEngine.Random.Range(0, d.EntryCount))) PlayBuiltinDance();
                return;
            }

            int idx = d.FindIndexByTitleFuzzy(arg);
            if (idx < 0) { Debug.LogWarning($"[Effect] 曲库里没有舞包: {arg}"); return; }
            if (!d.PlayIndex(idx)) PlayBuiltinDance();
        }

        void PlayBuiltinDance()
        {
            var avatar = FindFirstObjectByType<AvatarAnimatorController>();
            if (avatar == null || avatar.animator == null) return;
            avatar.isDancing = true;
            avatar.animator.SetBool("isDancing", true);
        }

        // ---------- song ----------

        SongService songService;
        SongService Song { get { if (songService == null) songService = GetComponent<SongService>(); return songService; } }

        void PlaySong(string arg, EffectContext ctx)
        {
            var s = Song;
            if (s == null) { WarnOnce("song"); return; }
            string name = string.IsNullOrEmpty(ctx.Event?.Nickname) ? "朋友" : ctx.Event.Nickname;

            if (arg != "request") { s.RequestSong(arg, name); return; }

            // song:request 从弹幕正文里剥掉命中的关键词，剩下的就是歌名
            string title = StripKeywords(ctx.Event?.Content ?? "", ctx.Rule);
            if (string.IsNullOrWhiteSpace(title))
            {
                Say($"{name} 想点什么歌呀？发 点歌加歌名 哦~");
                return;
            }

            // 曲库里有同名舞包时优先播它：真编舞 + 原曲音频，效果比在线点歌好
            var d = Dance;
            if (d != null)
            {
                int idx = d.FindIndexByTitleFuzzy(title);
                if (idx >= 0 && d.PlayIndex(idx))
                {
                    Say($"好嘞！{name} 点的 {title}，舞蹈版走起！");
                    return;
                }
            }
            s.RequestSong(title, name);
        }

        static string StripKeywords(string content, TriggerRule rule)
        {
            if (rule?.keywords == null) return content.Trim();
            string s = content;
            foreach (var w in rule.keywords)
            {
                if (string.IsNullOrWhiteSpace(w)) continue;
                s = s.Replace(w.Trim(), "");
            }
            return s.Trim();
        }

        // ---------- swapAvatar ----------

        void SwapAvatar(EffectContext ctx)
        {
            string name = string.IsNullOrEmpty(ctx.Event?.Nickname) ? "朋友" : ctx.Event.Nickname;
            var mgr = DouyinLiveManager.Instance;
            if (mgr == null) { WarnOnce("swapAvatar"); return; }
            mgr.SwapAvatarFromTrigger(name);
        }

        // ---------- outfit ----------

        void SwitchOutfit(string arg)
        {
            var handlers = AccessoiresHandler.ActiveHandlers;
            if (handlers == null || handlers.Count == 0) { WarnOnce("outfit"); return; }

            var all = new List<AccessoiresHandler.AccessoryRule>();
            foreach (var h in handlers)
            {
                if (h == null || h.rules == null) continue;
                foreach (var r in h.rules) if (r != null) all.Add(r);
            }
            if (all.Count == 0) { WarnOnce("outfit"); return; }

            if (arg == "random")
            {
                var pick = all[UnityEngine.Random.Range(0, all.Count)];
                pick.isEnabled = !pick.isEnabled;
                if (debugLog) Debug.Log($"[Effect] outfit 切换 {pick.ruleName} → {pick.isEnabled}");
                return;
            }

            foreach (var r in all)
                if (r.ruleName == arg) { r.isEnabled = !r.isEnabled; return; }
            WarnOnce("outfit:" + arg);
        }
```

- [ ] **Step 4: 在 DouyinLiveManager 上加换角色转发**

`RewardService` 是普通类不是组件，`EffectRegistry` 拿不到实例，所以经 Manager 转发：

```csharp
        // 供 EffectRegistry 的 swapAvatar 效果调用
        public void SwapAvatarFromTrigger(string userName)
        {
            reward.SwitchRandomAvatar(userName);
        }
```

- [ ] **Step 5: 确认 `AccessoiresHandler.AccessoryRule` 是可从外部改的**

```bash
cd "e:/Work/AI/Mate-Engine" && sed -n '1,40p' "Assets/MATE ENGINE - Scripts/AvatarHandlers/AccessoiresHandler.cs"
```

确认 `AccessoryRule` 是 `public class` 且 `isEnabled` / `ruleName` 是 public 字段（已核实是）。如果改 `isEnabled` 后场景里没反应，说明 handler 只在 `Start` 读一次 —— 那就在 `SwitchOutfit` 末尾对每个 handler 调一次它的刷新入口；先看 `AccessoiresHandler.Update()` 是否每帧读 `isEnabled`（若是则无需额外处理）。

- [ ] **Step 6: mock server 手工验证**

```bash
cd "e:/Work/AI/Mate-Engine" && python Tools/douyin_mock_server.py
```

| 输入 | 预期 |
|---|---|
| `f`（关注）| 大头特写 + 粒子 + 说「感谢 xxx 的关注」；Console 无未实现警告 |
| `gg`（大礼物 ≥100）| 大头特写 + 跳一支舞 |
| `c 换角色` | 换一个 VRM，身高自动归一化 |
| `c 换角色`（60 秒内再发）| Console `规则 swap 被 RuleCooldown 拦下` |
| `c 点歌 千本樱` | 曲库有同名舞包则播舞包，否则在线搜歌 |
| 跳舞中再发 `c 点舞` | Console `swap/reqdance → Queued`，跳完后自动接着播 |

- [ ] **Step 7: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/"
git commit -m "$(cat <<'EOF'
feat(douyin-live): implement heavyweight effects

bigscreen / dance:* / song:* / swapAvatar / outfit:*. RewardService's avatar
swap and the manager's big-head moment are reused rather than reimplemented,
so the trigger layer and the legacy path behave identically.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 10: sayAI 与兜底文案

**Files:**
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DanmakuAIService.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/EffectRegistry.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs`

**Interfaces:**
- Consumes: `EffectRegistry.Execute`（Task 6）、`TriggerRule.sayFallback`（Task 2）
- Produces: `DanmakuAIService.GenerateOneShot(string prompt, Action<string> onDone)`（失败回传 `null`，回调在主线程）；`DouyinLiveManager.GenerateFromTrigger(string prompt, Action<string> onDone)`；`EffectRegistry` 支持 `sayAI:<提示>`

- [ ] **Step 1: 给 DanmakuAIService 加一次性生成入口**

`DanmakuAIService` 现有的 `OnDanmaku` → `Tick` → `ReplyAsync` 链路带排队、`MinInterval` 冷却、
句级流式播报和历史记录，这些对 `sayAI:` 都不合适（触发层自己已经限流，且要拿到整段文本再决定
说不说）。加一个旁路入口，复用它的后端、人设与过滤：

在 `DanmakuAIService` 里追加（文件已 `using System`、`System.Threading.Tasks`、`UnityEngine`）：

```csharp
        // 供触发层的 sayAI: 效果使用：给一段提示词、拿回一句可直接播报的文本。
        // 不进队列、不受 MinInterval 限制、不写历史 —— 触发层有自己的四道限流闸，
        // 再叠一层冷却只会让大礼物的感谢莫名其妙地不出声。
        // onDone 保证在主线程回调；失败或被敏感词拦下时回传 null，由调用方决定兜底文案。
        public void GenerateOneShot(string prompt, Action<string> onDone)
        {
            if (onDone == null) return;
            if (string.IsNullOrWhiteSpace(prompt)) { onDone(null); return; }
            _ = GenerateOneShotAsync(prompt, onDone);
        }

        async Task GenerateOneShotAsync(string prompt, Action<string> onDone)
        {
            string result = null;
            try
            {
                string systemPrompt = SystemPrompt + " 只回一句话，不超过30个字。";

                var backend = Cloud != null && Cloud.IsAvailable ? Cloud : null;
                if (backend != null)
                {
                    try { result = await RunBackend(backend, systemPrompt, prompt, null); }
                    catch (Exception ex) { Debug.LogWarning("[DanmakuAI] one-shot cloud failed: " + ex.Message); }
                }
                if (string.IsNullOrWhiteSpace(result) && FallbackToLocal && Local != null && Local.IsAvailable)
                {
                    try { result = await RunBackend(Local, systemPrompt, prompt, null); }
                    catch (Exception ex) { Debug.LogWarning("[DanmakuAI] one-shot local failed: " + ex.Message); }
                }

                result = Sanitize(result);
                // AI 生成的文案一律过敏感词表，直播合规不能因为走了旁路就放松
                if (!string.IsNullOrEmpty(result) && !ContentFilter.IsSafe(result))
                {
                    Debug.LogWarning("[DanmakuAI] one-shot blocked by content filter");
                    result = null;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DanmakuAI] one-shot failed: " + ex.Message);
                result = null;
            }

            // RunBackend 在后台线程完成，回调必须转回主线程才能碰 SpeechPipeline
            string final = result;
            MainThreadDispatcher.Post(() => onDone(final));
        }
```

> `RunBackend` 的 `onDelta` 传 `null`：`sayAI:` 要的是整段文本，不需要句级流式播报
> （流式是为了压低 AI 回复的开口延迟，而这里本来就有 3 秒超时兜底）。
> 实现时确认 `RunBackend` 内部对 `onDelta == null` 的处理；若它无条件调用，
> 就传一个空委托 `_ => { }`。

- [ ] **Step 2: 在 EffectRegistry 里加分支**

switch 里加：

```csharp
                case "sayAI": SayAI(arg, ctx); break;
```

实现：

```csharp
        // 只有礼物 L3 这类低频高价值事件才值得等 1-3 秒换一句定制文案；
        // 高频事件用 AI 会拖慢反馈节奏并烧 token，所以其余一律用固定模板。
        void SayAI(string prompt, EffectContext ctx)
        {
            string filled = FillPlaceholders(prompt, ctx.Event);
            string fallback = FillPlaceholders(
                string.IsNullOrWhiteSpace(ctx.Rule?.sayFallback)
                    ? "哇！谢谢 {u} 的 {g}，太感谢啦！"
                    : ctx.Rule.sayFallback,
                ctx.Event);

            var mgr = DouyinLiveManager.Instance;
            if (mgr == null) { Say(fallback); return; }

            bool answered = false;
            mgr.GenerateFromTrigger(filled, text =>
            {
                if (answered) return;
                answered = true;
                Say(string.IsNullOrWhiteSpace(text) ? fallback : text);
            });

            // 3 秒还没回来就先说兜底，绝不让大礼物没有反馈
            StartCoroutine(SayFallbackIfSilent(3f, () => answered, () => { answered = true; Say(fallback); }));
        }

        IEnumerator SayFallbackIfSilent(float seconds, System.Func<bool> answered, System.Action fallback)
        {
            yield return new WaitForSeconds(seconds);
            if (!answered()) fallback();
        }
```

- [ ] **Step 3: 在 DouyinLiveManager 上加转发**

```csharp
        // 供 EffectRegistry 的 sayAI: 效果使用
        public void GenerateFromTrigger(string prompt, System.Action<string> onDone)
        {
            danmakuAI.GenerateOneShot(prompt, onDone);
        }
```

- [ ] **Step 4: 手工验证**

```bash
cd "e:/Work/AI/Mate-Engine" && python Tools/douyin_mock_server.py
```

- 送 `gg` → 应说一句 AI 生成的定制感谢（内容每次不同）。
- 把 `settings.json` 里的 `aiBaseUrl` 改成一个不通的地址重启 → 送 `gg` 应在约 3 秒后说出 `gift3` 规则里的 `sayFallback` 文案，**不能沉默**。

- [ ] **Step 5: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/"
git commit -m "$(cat <<'EOF'
feat(douyin-live): add sayAI effect with a 3s fallback

Only high-value low-frequency events (big gifts) are worth 1-3s of latency for
a custom line; everything else uses fixed templates. A silent 3s means the
rule's sayFallback speaks instead - a big gift must never get no reaction.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

**第二期完成。**

---

## 第三期：舞蹈增强

### Task 11: 洗牌袋轮播

**Files:**
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Core/ShuffleBag.cs`
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/ShuffleBagTests.cs`
- Create: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DanceDirector.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/EffectRegistry.cs`（`dance:random` 改为委托）
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/TriggerRouter.cs`（`Awake` 确保 `DanceDirector` 存在）
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/RewardService.cs`（`TryPlayRandomCustom` 委托给 `DanceDirector`）

**Interfaces:**
- Consumes: `AvatarDanceHandler`（`EntryCount` / `PlayIndex` / `IsPlaying` / `FindIndexByTitleFuzzy`）
- Produces: `ShuffleBag`：`Reset(int count)`、`Next() → int`（-1 = 空）、`int Count`；`DanceDirector`（MonoBehaviour）：`bool PlayRandom()`、`bool PlayByTitle(string)`

- [ ] **Step 1: 先写测试**

`Tests/ShuffleBagTests.cs`：

```csharp
using System.Collections.Generic;
using NUnit.Framework;

namespace DouyinLive.Tests
{
    public class ShuffleBagTests
    {
        [Test]
        public void 一轮之内每个索引恰好出现一次()
        {
            var bag = new ShuffleBag(seed: 1);
            bag.Reset(5);
            var seen = new List<int>();
            for (int i = 0; i < 5; i++) seen.Add(bag.Next());
            seen.Sort();
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, seen);
        }

        [Test]
        public void 取完自动重洗继续供应()
        {
            var bag = new ShuffleBag(seed: 2);
            bag.Reset(3);
            for (int i = 0; i < 30; i++) Assert.GreaterOrEqual(bag.Next(), 0);
        }

        [Test]
        public void 重洗时不会紧接着重复上一支()
        {
            var bag = new ShuffleBag(seed: 3);
            bag.Reset(4);
            int prev = -1;
            for (int i = 0; i < 200; i++)
            {
                int cur = bag.Next();
                Assert.AreNotEqual(prev, cur, "跨轮出现了相邻重复");
                prev = cur;
            }
        }

        [Test]
        public void 只有一个元素时允许重复否则无从选择()
        {
            var bag = new ShuffleBag(seed: 4);
            bag.Reset(1);
            Assert.AreEqual(0, bag.Next());
            Assert.AreEqual(0, bag.Next());
        }

        [Test]
        public void 空集合返回负一()
        {
            var bag = new ShuffleBag(seed: 5);
            bag.Reset(0);
            Assert.AreEqual(-1, bag.Next());
        }

        [Test]
        public void 舞包数量变化后重置()
        {
            var bag = new ShuffleBag(seed: 6);
            bag.Reset(3);
            bag.Next();
            bag.Reset(10);
            for (int i = 0; i < 10; i++) Assert.Less(bag.Next(), 10);
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: Test Runner → EditMode → Run All
Expected: 编译失败，`ShuffleBag` 不存在。

- [ ] **Step 3: 实现**

`Core/ShuffleBag.cs`：

```csharp
using System;
using System.Collections.Generic;

namespace DouyinLive
{
    // 洗牌袋：一轮之内不重复，取完自动重洗。
    // 比 rng.Next(count) 的纯随机体验好得多 —— 10 个舞包里连着抽到同一支很常见，
    // 观众会以为主播只会跳这一支。
    public class ShuffleBag
    {
        readonly List<int> bag = new List<int>();
        readonly Random rng;
        int total;
        int lastServed = -1;

        public ShuffleBag(int seed = 0)
        {
            rng = seed == 0 ? new Random() : new Random(seed);
        }

        public int Count => total;

        public void Reset(int count)
        {
            total = Math.Max(0, count);
            bag.Clear();
            lastServed = -1;
        }

        public int Next()
        {
            if (total <= 0) return -1;
            if (bag.Count == 0) Refill();
            if (bag.Count == 0) return -1;

            int last = bag.Count - 1;
            int pick = bag[last];
            bag.RemoveAt(last);
            lastServed = pick;
            return pick;
        }

        void Refill()
        {
            for (int i = 0; i < total; i++) bag.Add(i);

            // Fisher-Yates
            for (int i = bag.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (bag[i], bag[j]) = (bag[j], bag[i]);
            }

            // Next() 从尾部取，所以尾部就是下一个要发的。跟上一轮最后发出去的
            // 撞上就跟头部换一下，避免观众连着看到同一支舞。只有一个元素时无从换起。
            if (total > 1 && bag[bag.Count - 1] == lastServed)
                (bag[bag.Count - 1], bag[0]) = (bag[0], bag[bag.Count - 1]);
        }
    }
}
```

- [ ] **Step 4: 跑测试**

Run: Test Runner → EditMode → Run All
Expected: 全部 PASS（累计 48 个）。

- [ ] **Step 5: 实现 DanceDirector 并接管 dance:random**

`DanceDirector.cs`：

```csharp
using UnityEngine;
using CustomDancePlayer;

namespace DouyinLive
{
    // 舞蹈编排：洗牌轮播 + 连播。表演增强（粒子/防出画）在 Task 12 补上。
    public class DanceDirector : MonoBehaviour
    {
        public bool debugLog = false;
        public int danceChainCount = 1;   // 一次触发连跳几支

        readonly ShuffleBag bag = new ShuffleBag();
        AvatarDanceHandler dance;
        int chainRemaining;
        bool wasPlaying;

        AvatarDanceHandler Dance
        {
            get
            {
                if (dance == null) dance = FindFirstObjectByType<AvatarDanceHandler>(FindObjectsInactive.Include);
                return dance;
            }
        }

        public bool PlayRandom()
        {
            var d = Dance;
            if (d == null || d.EntryCount <= 0) return false;

            // 舞包目录可能在运行时变（用户往 StreamingAssets 里丢新包），数量变了就重置
            if (bag.Count != d.EntryCount) bag.Reset(d.EntryCount);

            int idx = bag.Next();
            if (idx < 0) return false;

            if (!d.PlayIndex(idx)) return false;
            chainRemaining = Mathf.Max(0, danceChainCount - 1);
            if (debugLog) Debug.Log($"[Dance] 播放索引 {idx}，还要连播 {chainRemaining} 支");
            return true;
        }

        public bool PlayByTitle(string title)
        {
            var d = Dance;
            if (d == null) return false;
            int idx = d.FindIndexByTitleFuzzy(title);
            return idx >= 0 && d.PlayIndex(idx);
        }

        void Update()
        {
            var d = Dance;
            bool playing = d != null && d.IsPlaying;

            // 一支跳完 → 若还有连播次数就接着来一支
            if (wasPlaying && !playing && chainRemaining > 0)
            {
                chainRemaining--;
                PlayRandomKeepChain();
            }
            wasPlaying = playing;
        }

        void PlayRandomKeepChain()
        {
            int keep = chainRemaining;
            PlayRandom();
            chainRemaining = keep;   // PlayRandom 会重置连播计数，这里保住剩余次数
        }
    }
}
```

`EffectRegistry.PlayDance` 的 `arg == "random"` 分支改成：

```csharp
            if (arg == "random")
            {
                if (danceDirector == null) danceDirector = FindFirstObjectByType<DanceDirector>();
                if (danceDirector != null && danceDirector.PlayRandom()) return;
                PlayBuiltinDance();
                return;
            }
```

并在 `EffectRegistry` 加字段 `DanceDirector danceDirector;`；在 `TriggerRouter.Awake()` 里补一句确保组件存在：

```csharp
        if (GetComponent<DanceDirector>() == null) gameObject.AddComponent<DanceDirector>();
```

- [ ] **Step 6: 让旧路径也用上洗牌轮播**

`RewardService.TryPlayRandomCustom` 用的是 `rng.Next(0, count)` 纯随机。它在触发层未命中时
仍会跑（礼物规则 `douyin_gift_rules.json` 里的 `randomDance`），两条路径的选舞体验应该一致。

把 `TryPlayRandomCustom` 里选索引的那一行改成先问 `DanceDirector`：

```csharp
            // 洗牌轮播由 DanceDirector 统一管理，两条路径共用同一个袋子，
            // 否则旧路径播过的舞在新路径的"一轮不重复"里不算数。
            var director = UnityEngine.Object.FindFirstObjectByType<DanceDirector>();
            if (director != null && director.PlayRandom()) return true;
```

放在原有随机逻辑之前，`director` 为 null 或播放失败时自然回落到原实现，行为不退化。

- [ ] **Step 7: 手工验证**

用 mock server 连发 6 次 `c 点舞`（每次间隔超过 90 秒规则冷却，或临时把 `reqdance` 的 `cooldown` 改成 0 并热重载），观察 Console 的 `[Dance] 播放索引 N`：在舞包总数一轮之内不应出现重复索引。

- [ ] **Step 8: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/"
git commit -m "$(cat <<'EOF'
feat(douyin-live): shuffle-bag dance rotation instead of pure random

Pure rng.Next(count) hits the same pack twice in a row often enough that
viewers assume the streamer only knows one dance. The bag also swaps away from
the previous round's last pick so refills don't produce adjacent repeats.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 12: 跳舞表演增强

**Files:**
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DanceDirector.cs`

**Interfaces:**
- Consumes: `AvatarParticleHandler`、`AvatarDanceSafetyZone`、`PortraitWindowController`
- Produces: `DanceDirector` 在 `danceParticleTheme` 非空时跳舞期间切主题并还原；竖屏下自动启用防出画

- [ ] **Step 1: 加字段**

```csharp
        public string danceParticleTheme = "";   // 留空 = 不切粒子主题
        [Range(0.05f, 0.4f)] public float portraitSoftZoneRatio = 0.15f;

        AvatarParticleHandler particles;
        AvatarDanceSafetyZone safety;
        PortraitWindowController portrait;

        string themeBefore;
        bool safetyEnabledBefore, moveWindowBefore;
        float softLeftBefore, softRightBefore;
        bool decorated;
```

- [ ] **Step 2: 在 Update 的起跳/收尾边界上挂钩**

把 `Update()` 改成：

```csharp
        void Update()
        {
            var d = Dance;
            bool playing = d != null && d.IsPlaying;

            if (playing && !decorated) BeginPerformance();
            if (!playing && decorated) EndPerformance();

            if (wasPlaying && !playing && chainRemaining > 0)
            {
                chainRemaining--;
                PlayRandomKeepChain();
            }
            wasPlaying = playing;
        }
```

- [ ] **Step 3: 实现装饰与还原**

```csharp
        void BeginPerformance()
        {
            decorated = true;

            // 粒子：记录用户原本选的主题，跳完必须还原，否则会悄悄改掉他的设置
            if (!string.IsNullOrEmpty(danceParticleTheme))
            {
                if (particles == null) particles = FindFirstObjectByType<AvatarParticleHandler>(FindObjectsInactive.Include);
                if (particles != null)
                {
                    themeBefore = particles.selectedTheme;
                    particles.SetTheme(danceParticleTheme);
                }
            }

            // 防出画：只在竖屏直播时开。AvatarDanceSafetyZone 默认会跟着平移系统窗口，
            // 而直播伴侣是按窗口采集的，窗口一漂画面就毁了 —— 必须强制关掉。
            if (portrait == null) portrait = FindFirstObjectByType<PortraitWindowController>();
            if (portrait == null || !portrait.Active) return;

            if (safety == null) safety = FindFirstObjectByType<AvatarDanceSafetyZone>(FindObjectsInactive.Include);
            if (safety == null) return;

            safetyEnabledBefore = safety.enableSafety;
            moveWindowBefore = safety.moveWindowAlong;
            softLeftBefore = safety.softZoneLeftPx;
            softRightBefore = safety.softZoneRightPx;

            safety.moveWindowAlong = false;
            float soft = Screen.width * portraitSoftZoneRatio;
            safety.softZoneLeftPx = soft;
            safety.softZoneRightPx = soft;
            safety.SetSafetyEnabled(true);
        }

        void EndPerformance()
        {
            decorated = false;

            if (particles != null && !string.IsNullOrEmpty(themeBefore))
            {
                particles.SetTheme(themeBefore);
                themeBefore = null;
            }

            if (safety != null)
            {
                safety.SetSafetyEnabled(safetyEnabledBefore);
                safety.moveWindowAlong = moveWindowBefore;
                safety.softZoneLeftPx = softLeftBefore;
                safety.softZoneRightPx = softRightBefore;
            }
        }

        // 播放中被强制销毁（换角色/退出）时也要还原，否则用户设置被永久改掉
        void OnDisable() { if (decorated) EndPerformance(); }
```

- [ ] **Step 4: 手工验证**

1. `settings.json` 设 `douyinPortraitAspect: 0.75`，启动，确认窗口是竖屏。
2. mock server 发 `c 点舞`，跳舞过程中**窗口位置必须一动不动**（这是关键回归点）。
3. 角色走到画面边缘时应被镜头拉回，不出画。
4. 跳完后：若在设置里改过粒子主题，确认它没被改掉。

- [ ] **Step 5: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/"
git commit -m "$(cat <<'EOF'
feat(douyin-live): dance performance enhancements

Enables AvatarDanceSafetyZone during portrait-mode dances but forces
moveWindowAlong=false first: the component's default is to pan the OS window
along with the camera, which wrecks capture in 直播伴侣.

Particle theme is saved and restored (including on OnDisable) so a dance never
silently overwrites the user's own setting.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 13: 冷场唱跳交替

**Files:**
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/IdleChatterService.cs`
- Modify: `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/DouyinLiveManager.cs`（`ApplySettings` 接线）
- Modify: `Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs`（新增一个开关）

**Interfaces:**
- Consumes: `DanceDirector.PlayRandom()`（Task 11）
- Produces: `IdleChatterService.Dance`（`DanceDirector` 引用）、`IdleChatterService.AutoDanceEnabled`

- [ ] **Step 1: 加设置项**

`SaveLoadHandler.cs` 的 `SettingsData` 里，`douyinIdleSongList` 附近加：

```csharp
        // 深度冷场时在唱歌和跳舞之间交替，避免连着唱好几首
        public bool douyinIdleAutoDanceEnabled = true;
```

- [ ] **Step 2: 改 IdleChatterService**

加字段：

```csharp
        public DanceDirector Dance;                  // 冷场自动跳舞
        public bool AutoDanceEnabled = true;
        bool lastAutoWasSong;                        // 唱/跳交替用
```

把 `Tick()` 里「深度冷场」那一段（原第 75-88 行）改成：

```csharp
            // 深度冷场：唱一首或跳一支，两者交替，避免连着唱好几首。
            // 首次只看冷场时长；MinInterval 从第一次之后才计。
            bool songReady = Song != null && !Song.IsPlaying && SongList != null && SongList.Count > 0;
            bool danceReady = AutoDanceEnabled && Dance != null;

            if (AutoSongEnabled && (songReady || danceReady) &&
                idleFor >= AutoSongIdleThreshold &&
                (!autoSongClockStarted || now - lastAutoSongAt >= AutoSongMinInterval) &&
                !Speech.IsSpeaking && Speech.QueueCount == 0)
            {
                // 上次唱过就这次跳；对应一侧不可用时回退到另一侧（歌单为空 = 保持原有行为）
                bool wantDance = lastAutoWasSong && danceReady;
                if (!wantDance && !songReady) wantDance = danceReady;

                if (wantDance && Dance.PlayRandom())
                {
                    autoSongClockStarted = true;
                    lastAutoSongAt = now;
                    lastChatterAt = now;
                    lastAutoWasSong = false;
                    Speech.Enqueue("好像有点安静呢，那我来给大家跳一支舞吧~",
                        SpeechPipeline.Priority.LikeThanks, 30f);
                    return;
                }

                if (songReady)
                {
                    autoSongClockStarted = true;
                    lastAutoSongAt = now;
                    lastChatterAt = now;
                    lastAutoWasSong = true;
                    string pick = SongList[rng.Next(SongList.Count)];
                    Speech.Enqueue($"好像有点安静呢，那我来给大家唱一首 {pick} 吧~",
                        SpeechPipeline.Priority.LikeThanks, 30f);
                    Song.RequestSong(pick, null);   // null = 自动唱歌，不播报点歌提示
                    return;
                }
            }
```

`ResetSession()` 里加 `lastAutoWasSong = false;`。

- [ ] **Step 3: 接线**

`DouyinLiveManager.ApplySettings()` 里 `idleChatter.SongList = ...` 之后加：

```csharp
            idleChatter.AutoDanceEnabled = d.douyinIdleAutoDanceEnabled;
            idleChatter.Dance = GetComponent<DanceDirector>();
```

- [ ] **Step 4: 手工验证**

把 `settings.json` 的 `douyinIdleAutoSongThreshold` 临时改成 `30`、`douyinIdleAutoSongMinInterval` 相关项调小，启动后不做任何互动：
- 第一次触发应唱歌，第二次应跳舞，第三次再唱歌。
- 把 `douyinIdleSongList` 改成 `[]` 重启 → 应每次都跳舞。
- 把 `douyinIdleAutoDanceEnabled` 改成 `false` → 应回到只唱歌的旧行为。

- [ ] **Step 5: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add "Assets/MATE ENGINE - Scripts/"
git commit -m "$(cat <<'EOF'
feat(douyin-live): alternate singing and dancing on deep idle

Falls back to whichever side is available, so an empty song list keeps the
previous behaviour instead of going quiet.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 14: 文档

**Files:**
- Modify: `README.md`
- Modify: `Docs/DouyinLive-Integration.md`

- [ ] **Step 1: README 功能总览加一行**

在「玩法菜单」那一行之后插入：

```markdown
| 自定义触发 | 配置 `douyin_triggers.json` | 任意关键词/点赞数/礼物档位 → 任意效果组合，改完存盘即生效 |
```

- [ ] **Step 2: README 个性化数据文件表格加一行**

在 `douyin_gift_rules.json` 那一行之后加：

```markdown
| `douyin_triggers.json` | **触发规则总表**：谁触发、触发什么、多久能触发一次。首次运行自动生成，文件头部有完整注释。改完存盘即生效（无需重启）。详见下节 |
```

- [ ] **Step 3: README 新增一节**

放在「#### 竖屏直播窗口」之后：

```markdown
#### 自定义触发规则（douyin_triggers.json）

一条规则 = 「什么来源 + 什么条件 → 执行哪些效果」。想让「换角色、换装、换个人」都能换角色，
就把这三个词写进同一条规则的 `keywords`；想让弹幕和关注都放大屏，就写两条规则、`effects` 都填 `bigscreen`。

**可用效果：**

| 效果 | 说明 |
|---|---|
| `anim:<参数名>` | 播一次 Animator 动作。现有参数只有 `Headpat` / `HairStroke` / `HoverFaceTrigger` / `HoverTrigger` / `IntimeRegion`；自己往 Animator 里加了新动画，这里直接写新参数名即可，不用改代码 |
| `face:Happy` `face:Angry` `face:Cry` `face:Fear` | 面部表情状态 |
| `mood:happy` `mood:love` `mood:sad` `mood:surprise` | 表情混合形状 |
| `particle:<主题名>` | 切粒子主题 6 秒后还原。**目前只有 `Dance Trail Blue` 一个主题** |
| `bigscreen` | 大头特写 |
| `dance:random` / `dance:<舞名>` / `dance:builtin` | 跳舞。`random` 一轮之内不重复 |
| `song:<歌名>` / `song:request` | 唱歌。`request` 从弹幕正文里取歌名 |
| `swapAvatar` | 随机换 VRM 角色，自动身高归一化 |
| `outfit:random` / `outfit:<配件名>` | 切换配件 |
| `say:<文本>` | 固定文案，支持 `{u}` 昵称 / `{g}` 礼物名 / `{n}` 数量 |
| `sayAI:<提示词>` | 让大模型现场生成一句。3 秒没回来就说规则里的 `sayFallback` |
| `menu` | 口播玩法说明 |

**动作三层：** `L1` 轻叠加（不打断唱歌）/ `L2` 普通互动（唱歌时只出粒子不播动画）/ `L3` 重磅独占。

**防刷屏的四道闸**（一个请求要全部通过才执行，被拦下时日志会写明是哪道）：

| 参数 | 位置 | 作用 |
|---|---|---|
| `chatCooldown` / `likeCooldown` / `giftCooldown` | `global` | 该来源的整体节奏 |
| `perUserCooldown` | `global`（可在规则里覆盖）| 同一观众的间隔。**防刷屏主力**：只冻结他自己，不影响别人 |
| `cooldown` | 单条规则 | 这个玩法自己的节奏。换角色默认 60 秒 |
| `l2MinInterval` / `l3MinInterval` | `global` | 跨规则的层级总闸。`l3MinInterval` 默认 45 秒 |

**礼物档位**按 `minDiamond` / `maxDiamond` 配，默认 1-9 / 10-99 / ≥100 抖币，按自己直播间的实际礼物结构调。
`global.giftUseTotalValue` 为 `true`（默认）时按「单价 × 数量」算，连刷 20 个 1 抖币的小心心会命中中档；
改成 `false` 则只看单价。

改坏了不要紧：解析失败会保留上一份可用配置并在日志里报错，直播间不会哑掉。
想恢复出厂设置就把文件删掉重启。
```

- [ ] **Step 4: Docs/DouyinLive-Integration.md 补数据流**

在文档的架构/数据流章节加：

```markdown
### 触发层（2026-08 新增）

```
DouyinLiveClient → DouyinLiveManager.Route()
                        ├─ TriggerRouter.TryHandle(ev)      读 douyin_triggers.json，热重载
                        │     └─ TriggerMatcher.Match       纯函数，命中的第一条规则
                        │     └─ TriggerLimiter.Check       四道限流闸
                        │     └─ ActionDirector.Submit      三层仲裁 + L3 队列
                        │           └─ EffectRegistry.Execute
                        └─ 未命中 → 原有的 reward / danmakuAI / welcome / like 逻辑
```

旁路式：删掉 `douyin_triggers.json` 就完全回退到接入触发层之前的行为。

纯逻辑部分在 `Core/` 子目录里，属于 `MateEngine.DouyinLive.Core` 程序集，
对应的 EditMode 测试在 `Tests/`（Unity 的 asmdef 不能反向引用 `Assembly-CSharp`，
所以想单元测试的代码必须放进独立程序集）。
```

- [ ] **Step 5: 提交**

```bash
cd "e:/Work/AI/Mate-Engine"
git add README.md Docs/DouyinLive-Integration.md
git commit -m "$(cat <<'EOF'
docs: document douyin_triggers.json effects, tiers and rate limiting

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## 完成标准

三期全部完成后，下面每一条都应成立：

1. Test Runner → EditMode → Run All：**48 个测试全部通过**，Console 无编译错误。
2. 删掉 `douyin_triggers.json` 重启 → 行为与接入触发层之前完全一致（AI 回复、欢迎、点赞感谢、礼物三档、弹幕点歌/换角色全部照常）。
3. 在配置里加一条新规则并存盘 → **0.5 秒内**生效，不需要重启。
4. 单个用户狂刷同一个关键词 → 只有他自己被冻结，另一个用户立刻发同样的词仍能触发。
5. 竖屏模式下跳舞 → **窗口位置全程不动**，角色不出画。
6. 大礼物在 AI 不可用时 → 约 3 秒后说出 `sayFallback`，不沉默。
7. `README.md` 里的效果清单与 `EffectRegistry.Execute` 的 switch 分支**一一对应**，没有文档里有、代码里没有的效果。
