# 弹幕两轮点播：追问槽位 + LLM 意图兜底

**日期**：2026-09-02
**分支**：`feat/douyin-triggers`
**状态**：设计已确认，待实施

## 问题

观众发「点歌」，角色回一句「想听什么歌呀」，观众接着发歌名——**歌名不会触发唱歌**。

根因在 [EffectRegistry.cs:308-312](../../../Assets/MATE%20ENGINE%20-%20Scripts/Game%20APIs/DouyinLive/EffectRegistry.cs#L308-L312)：`song:request` 从弹幕正文里剥掉关键词后如果什么都不剩，就只说一句追问然后 `return true`，**不留下任何状态**。下一条弹幕是全新的一次匹配，「赤伶」两个字不在任何规则的 `keywords` 里，于是落到 AI 闲聊。

[RewardService.TryHandleDanmaku](../../../Assets/MATE%20ENGINE%20-%20Scripts/Game%20APIs/DouyinLive/RewardService.cs#L44) 的旧路径有同一个缺陷，同样只问不记。

点舞（`dance:random`）和换角色（`swapAvatar`）更极端：它们**根本不看弹幕正文**，「我想看小白」和「换角色」的效果完全一样。

## 目标

1. 观众答的歌名/舞名/角色名能接上前一句追问。
2. 观众一句话说完（「唱首赤伶」「换成小白」）也能一次到位。
3. 关键词没配到的自然语言（「我想听点音乐」）有兜底，但不能每条弹幕都烧 token。
4. 现有 `douyin_triggers.json` 不改也能继续用，行为不退化。

## 非目标

- 不做多轮对话状态机（超过两轮的追问）。
- 不做跨观众的上下文共享——追问只认发起人。
- 不改 `DanmakuAIService` 的队列/人设/过滤逻辑。

## 架构

三条路径，从确定性到概率性依次兜底。`Route()` 里 Chat 事件的顺序：

```
记账（audience / room）                     不变
  ↓
triggers.TryHandle(ev)                      关键词规则，不变
  ↓ 未命中
triggers.TryFillSlot(ev)                    新：这个人有没有开着的追问窗口
  ↓ 没有
intents.TryResolve(ev, HandleChatLegacy)    新：本地预筛命中 → 挂起等 LLM（≤1.5s）
  ↓ 预筛没中
HandleChatLegacy(ev)                        原有：RewardService → DanmakuAI
```

**关键词排在窗口补全之前是有意的。** 观众答的歌名如果恰好是《抱抱》，会命中 `love` 规则去播飞吻而不是唱歌。宁可漏一次也不要乱触发；顺序本身就替代了「这个回答是不是另一条命令」那道校验，不用写。

**LLM 那步是异步的**，所以预筛一命中就当场消费掉这条弹幕，不走 legacy。回调回来再分流：判出意图就执行，判不出（或超时/出错/被限流）就补回 `HandleChatLegacy(ev)`。这样避免「先闲聊回一句，一秒后又开始唱歌」的双重响应。

### 执行机制：临时规则副本

三条路径最终都汇入同一个执行口——**浅拷贝命中的规则，只把 `effects` 换成带参数的版本，`id` / `level` / `cooldown` 保持不变**，然后交给现有的 `ActionDirector.Submit`。

这个做法成立，是因为：

- [TriggerLimiter.RuleKey](../../../Assets/MATE%20ENGINE%20-%20Scripts/Game%20APIs/DouyinLive/Core/TriggerLimiter.cs#L107) 按 `rule.id` 字符串记账，副本和原规则共用全部四本冷却账。
- `ActionDirector.Submit(rule, ev, g)` 只读 `rule.effects` / `rule.LevelOrDefault` / `rule.pick`，接受任何 `TriggerRule` 实例。

所以「借用原规则的限流参数、只换参数执行」不需要动 `TriggerLimiter` 和 `ActionDirector` 一行代码。

### 限流：开槽时付费，补全时免检

追问的两轮是**一次请求的两半**，不该收两次费。

- **开槽**（观众发「点歌」）：走完整的四道闸，通过后 `Commit` 记账。和现在完全一样。
- **补全**（观众发「赤伶」）：**跳过 `limiter.Check` 和 `Commit`**，直接 `director.Submit`。

必须这么做，否则功能是坏的：`swap` 规则 `cooldown = 60`，开槽时刚记了账，30 秒内的回答必然被 `RuleCooldown` 拦掉；L3 的 `l3MinInterval = 45` 更是直接盖过整个 30 秒窗口。`song` 规则也一样会被 `perUserCooldown = 5` 挡住 5 秒内的回答。

免检不会被滥用：**要开槽必须先过闸**，一个槽只能被取走一次，所以「开槽 → 补全」的净速率和现在的「一次命中」完全相同。画面冲突仍由 `ActionDirector` 的 L1/L2/L3 仲裁负责——那才是它该管的事。

**LLM 意图路径不享受免检**：那是一次全新请求，没人付过费。走 `Check` → `Submit` → `Commit`。被闸拦下时**回落到 `HandleChatLegacy`**（而不是像 `TryHandle` 那样静默消费），因为「我想听首歌」本身是很好的闲聊素材，让 AI 回一句是体面的降级。

## 组件

### Core（零依赖，EditMode 可测）

`Core/` 下的代码不能引用 `UnityEngine`、`Newtonsoft`、`Debug.Log`、`Time.*`——`MateEngine.DouyinLive.Core.asmdef` 的 `references` 必须保持为空。时间源一律注入。

#### `Core/IntentSlots.cs`

```csharp
public enum IntentKind { None, Song, Dance, Avatar }

public class IntentSlot
{
    public string UserId;
    public string Nickname;
    public IntentKind Kind;
    public string RuleId;      // 开槽的规则，补全时按 id 反查回去
    public float OpenedAt;
}

public class IntentSlots
{
    public Func<float> Now = () => 0f;
    public float Window = 30f;
    public int Capacity = 8;

    public int Count { get; }
    public void Open(string userId, string nickname, IntentKind kind, string ruleId);
    public bool TryPeek(string userId, out IntentSlot slot);   // 只看不删
    public void Take(string userId);                           // 确认要用了才删
    public void Prune();
    public void Reset();
}
```

- **`TryPeek` / `Take` 分开是必须的**：补全内容通不过 `IsUsableArg` 时槽位要原样留着，而「取出来再放回去」会刷新 `OpenedAt`，观众连发十个「666」就能把 30 秒窗口无限续期。只看不删就没有这个洞。
- 按 `userId` 索引，同一个人重复开槽覆盖旧的（他改主意了）。
- 容量满时挤掉最旧的一个，不是拒绝新的——直播间里新请求比旧请求有价值。
- `TryPeek` 顺手 `Prune`，过期槽位不会返回。
- `userId` 为空的事件不开槽（认不出是谁，补全无从谈起）。

#### `Core/IntentText.cs`

三个纯函数：

```csharp
public static bool IsUsableArg(string s);
public static IntentKind LooksLikeIntent(string s);
public static bool TryParseIntentJson(string raw, out IntentKind kind, out string arg);
```

**`IsUsableArg`** —— 这段文本能不能当歌名/舞名/角色名用。拒绝：

- 空白，或长度 > 25
- **同一个字重复**（长度 ≥ 2 且所有字符相同）——「哈哈哈哈」「？？？」「。。。」是情绪不是答案
- 纯数字——「666」
- 没有任何字母或数字——「？！」「😀😭」（Emoji 是代理对，`char.IsLetterOrDigit` 对两半都返回 false，所以纯表情自然落在这一条里，不用单独判）

单字不拦：「刺」「浪」都可能是真歌名。这道校验挡掉接在追问后面的无意义弹幕，槽位保留到过期。

**`LooksLikeIntent`** —— 本地预筛，**只决定这条弹幕值不值得问 LLM**。`IntentResolver` 只看它是不是 `None`，返回的具体类别**不参与最终判定**——那是 LLM 的活。所以词表重叠（「换个歌」既含 Avatar 的「换个」又含 Song 的「歌」）不会导致误判，最多让一条本该问的弹幕被问、一条不该问的被跳过。返回第一个命中的类别，词表按「舞 → 角色 → 歌」的顺序判：

| 类别 | 触发词 |
|---|---|
| Dance | 跳舞、舞、扭一个、来段舞 |
| Avatar | 换角色、换个、变身、换成、换一个 |
| Song | 听、唱、歌、来一首、来首、点一首 |

长度 > 30 的弹幕一律返回 `None`——长句是聊天不是命令。

**`TryParseIntentJson`** —— 容忍 ` ```json ` 包裹、前后废话、单引号、缺字段。取不出合法的 `intent` 就返回 false。只认 `song` / `dance` / `avatar` / `none` 四个值，其它一律 `None`。

#### `Core/RuleQuery.cs`

```csharp
public static TriggerRule FindByEffectPrefix(TriggerConfig cfg, string prefix);
public static TriggerRule WithEffect(TriggerRule src, string effect);
```

`FindByEffectPrefix` 按 `cfg.rules` 顺序找第一条 `enabled` 且 `source == "chat"` 且任一 effect 以 `prefix` 开头的规则。前缀：Song → `"song:"`，Dance → `"dance:"`，Avatar → `"swapAvatar"`（这个没有冒号，`swapAvatar` 和 `swapAvatar:request` 都能命中）。

`WithEffect` 返回浅拷贝：除 `effects` 换成单元素列表、`pick` 强制为 `"all"` 外，其余字段（尤其是 `id`）全部照搬。`pick` 必须重置，否则原规则的 `pick = "random"` 会让副本的唯一效果有几率不执行。

#### `Core/NameMatch.cs`

```csharp
public static int PickIndex(IReadOnlyList<string> names, string query);
```

先精确（忽略大小写），再双向子串。给指名换角色用。和 `AvatarDancePlayer.FindIndexByTitleFuzzy` 同一套语义，但那个在 Unity 层且只服务舞包，不复用。

### Unity 层

#### `IntentResolver.cs`（新，普通类不是 MonoBehaviour）

```csharp
public class IntentResolver
{
    public IChatBackend Cloud;
    public bool debugLog;
    public void Reset();
    // 返回 true = 已接管这条弹幕，调用方不要再走 legacy
    public bool TryResolve(DouyinEvent ev, Action<DouyinEvent, IntentKind, string> onResolved,
                           Action<DouyinEvent> onGiveUp);
}
```

- **不用 `DanmakuAIService.GenerateOneShot`**：那条路会注入完整人设 prompt、追加「不超过30个字」、再过一遍 `Sanitize`（剥括号 + 60 字截断）。分类任务要的是干净 JSON，不是人设化的一句话。
- 直接用 `IChatBackend.ChatAsync`，system prompt 极简：

  > 你是弹幕意图分类器。判断这句弹幕是不是在点歌、点舞或要求换角色。
  > 只输出 JSON，不要任何解释：{"intent":"song|dance|avatar|none","arg":"名字，没有就留空"}

  `history` 传空列表，`onDelta` 传 null。
- 超时 1.5 秒（`CancellationTokenSource`），回调经 `MainThreadDispatcher` 转主线程。
- **三道自己的闸**（在发请求之前判，判不过直接返回 false 走原路径）：
  - `Cloud == null` 或 `Cloud.IsAvailable == false`
  - 同一 `userId` 15 秒内已问过一次
  - 全局同时在飞的请求 ≥ 2

  `global.intentFallbackEnabled` 这个总开关**不放在 `IntentResolver` 里**，而是由 `TriggerRouter.IntentFallbackEnabled` 现读 `Config` 暴露、`Route()` 调用前判——配置热重载会整个换掉 `Config` 对象，启动时抄一份就再也不会更新了。
- 输出**只当数据用**：只取 `intent` 和 `arg`，`arg` 过一遍 `IntentText.IsUsableArg` 才使用。模型返回的文本永远不会被当成指令执行。

#### `TriggerRouter.cs`

新增：

```csharp
public IntentSlots Slots { get; }
public bool TryFillSlot(DouyinEvent ev);
public bool TryHandleIntent(DouyinEvent ev, IntentKind kind, string arg);  // LLM 路径用
public void OpenSlot(DouyinEvent ev, IntentKind kind, string ruleId);      // EffectRegistry 回调用
```

`TryFillSlot`：非 Chat 或无 `UserId` 直接 false → `Slots.TryPeek` 拿不到 false → `IntentText.IsUsableArg` 不通过则返回 false 且**不 `Take`**（槽位原样留着，这条弹幕正常走闲聊，观众还有机会补答）→ 按 `slot.RuleId` 在 `Config.rules` 里找回原规则，找不到或已 `enabled == false` 则 `Take` 掉槽位并返回 false → `Take` → `WithEffect` 造副本 → `director.Submit`，**不 Check 不 Commit**。

`TryHandleIntent`：`RuleQuery.FindByEffectPrefix` 找规则 → 没找到返回 false（观众把点歌规则删了，那就别执行）→ `limiter.Check`，不 Pass 返回 false → `WithEffect` → `Submit` → `Commit`。

`Awake` 里 `Slots.Now = () => Time.unscaledTime`；`Tick` 里顺带 `Slots.Prune()`；`ResetSession` 里 `Slots.Reset()`；`Slots.Window` 在 `Reload` 后从 `Config.global.slotWindowSeconds` 同步。

#### `EffectRegistry.cs`

`PlaySong` 的空标题分支改为：说 `askPrompt`（或内置默认），然后回调 `TriggerRouter.OpenSlot(ctx.Event, IntentKind.Song, ctx.Rule.id)`。`TriggerRouter` 和 `EffectRegistry` 在同一个 GameObject 上（前者 `[RequireComponent(typeof(EffectRegistry))]`），按 `Particles` / `Song` 那几个属性的既有写法惰性 `GetComponent<TriggerRouter>()` 缓存即可。

新增 `dance:request`：从正文剥关键词，有舞名就 `FindIndexByTitleFuzzy` + `PlayIndex`（找不到退回 `PlayDance("random")` 并说一句「曲库里没有这支，随便来一个吧」），没舞名就追问 + 开槽。

`swapAvatar` 参数化：

| 效果 ID | 行为 |
|---|---|
| `swapAvatar` | 随机换。**和现在完全一样，老配置不受影响** |
| `swapAvatar:<角色名>` | 按 `displayName` 模糊匹配 |
| `swapAvatar:request` | 从正文剥关键词取名字；取不到就追问 + 开槽 |
| `swapAvatar:ask` | 不看正文，直接追问 + 开槽 |

`song` 和 `dance` 同样各多一个 `:ask`。**`ask` 和 `request` 必须分开**：LLM 判出「想听歌但没说歌名」时要的是直接追问，若复用 `request`，`StripKeywords` 会把「我想听点音乐」整句当歌名拿去搜——那条弹幕根本没命中关键词，没有词可剥。

追问文案默认值（规则的 `askPrompt` 留空时用），措辞要引导「直接发名字」——因为现在直接发真的有用了：

- Song：`{u} 想听什么歌呀？直接把歌名发出来就行~`
- Dance：`{u} 想看哪支舞呀？把舞名发出来~`
- Avatar：`{u} 想让我换成谁呀？说个名字~`

#### `RewardService.cs` / `DouyinLiveManager.cs`

`RewardService` 新增 `SwitchAvatarByName(string userName, string wanted)`：读 `avatars.json`，用 `NameMatch.PickIndex` 在 `displayName` 列表里找，命中就 `LoadVRM(filePath)`，**没命中就退回 `SwitchRandomAvatar` 并先说一句「衣柜里没有这个角色哦，随便换一个吧」**。30 秒 `SwitchCooldown` 和身高归一化逻辑照旧复用。

`DouyinLiveManager` 新增重载 `SwapAvatarFromTrigger(string userName, string wanted)`；`Route()` 按上面的顺序改造，Chat 的原有尾部抽成 `void HandleChatLegacy(DouyinEvent ev)`；`ApplySettings` 里装配 `IntentResolver`（`Cloud = cloudBackend`，`Enabled` 跟 `Config.global.intentFallbackEnabled` 走）。

## 配置

### 新增字段

```jsonc
"global": {
  "slotWindowSeconds": 30,        // 追问窗口秒数，0 = 关闭追问功能
  "intentFallbackEnabled": true   // 关键词没中时是否问 LLM 判意图
}
```

规则级新增可选 `askPrompt`（支持 `{u}`），留空用内置默认。

### 默认规则集变更

`TriggerConfig.Defaults()` 里两条规则的效果升级——**只影响新生成的配置文件，已存在的 `douyin_triggers.json` 不会被改写**：

| 规则 | 现在 | 改为 |
|---|---|---|
| `reqdance` | `dance:random` | `dance:request` |
| `swap` | `swapAvatar` | `swapAvatar:request` |

`song` 规则的 `song:request` 不变，行为自动升级。

### 向后兼容

老配置里的 `dance:random` 和 `swapAvatar` 行为**一字不变**，只是没有追问能力。想要就手动改成 `dance:request` / `swapAvatar:request`。`slotWindowSeconds` / `intentFallbackEnabled` 缺失时用字段默认值，`TriggerGlobal` 是普通类加默认值，Newtonsoft 反序列化老文件自然填默认。

## 错误处理

| 情况 | 行为 |
|---|---|
| LLM 不可用 / 超时 / 返回非法 JSON / intent 不认识 | 静默走 `HandleChatLegacy`，直播间不会哑 |
| LLM 判出意图但反查不到规则 | 不执行，走 legacy。行为和配置保持一致——删了规则就是不想要这个玩法 |
| 补全时规则已被删或禁用 | 丢弃槽位，走 legacy |
| 补全内容是「666」「？？」 | 槽位放回去，弹幕正常走闲聊，观众可以继续答 |
| 指名换角色没匹配上 | 说一句 + 退回随机换 |
| 点舞指名没匹配上 | 说一句 + 退回随机舞 |
| 歌名搜不到 | `SongService` 现有行为不变 |
| `userId` 为空的事件 | 不开槽、不补全、不问 LLM，直接走原路径 |
| 槽位表膨胀 | 容量 8 上限 + `Tick` 里 `Prune` |
| LLM 被刷 | 预筛 + 单人 15 秒 + 全局在飞 ≤2 三重限制 |

## 测试

### EditMode 单元测试（新增约 30 个用例）

`Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/Tests/` 下新增 `IntentSlotsTests.cs`、`IntentTextTests.cs`、`RuleQueryTests.cs`，用现有的 `MateEngine.DouyinLive.Tests` asmdef。

- **`IntentSlots`**：开了能 `TryPeek` 到；`Take` 之后没了；只 `Peek` 不 `Take` 槽位还在**且 `OpenedAt` 不变**（连续 Peek 三次后仍在原窗口内过期）；超过 `Window` 取不到；多用户互不干扰；同一人二次开槽覆盖；容量满挤掉最旧；`userId` 为空不开槽；`Reset` 清空。
- **`IntentText.IsUsableArg`**：正常歌名通过；空 / 纯空白 / 26 字 / 纯数字 / 纯标点 / 纯 Emoji 全部拒绝。
- **`IntentText.LooksLikeIntent`**：三类各两个正例；无关弹幕返回 `None`；超 30 字返回 `None`；「跳舞」判 Dance 不判 Song。
- **`IntentText.TryParseIntentJson`**：裸 JSON；```json 包裹；前后带废话；单引号；缺 `arg`；完全非 JSON；`intent` 值非法。
- **`RuleQuery.FindByEffectPrefix`**：命中第一条；跳过 `enabled == false`；跳过非 chat 规则；找不到返回 null；`swapAvatar` 无冒号前缀能命中。
- **`RuleQuery.WithEffect`**：`id` / `level` / `cooldown` / `perUserCooldown` 原样保留；`effects` 被替换；`pick` 被重置为 `all`；不修改源对象。

**现有 51 个测试必须全绿。**

### 编译与回归

Unity batchmode 编译零 `error CS`（注意先关掉编辑器，否则 `Temp/UnityLockfile` 被占，退出码 21，grep 结果无意义——必须同时 `grep -ci "another Unity instance"` 确认真的编译了）。

### 手工验证（只有主播能跑）

写进 `.superpowers/sdd/<plan>/手工验证清单.md`：

1. 弹幕「点歌」→ 角色问 → 弹幕「赤伶」→ **开始唱赤伶**
2. 弹幕「点歌 赤伶」→ 一次到位（回归，行为不变）
3. 弹幕「点歌」→ 隔 40 秒发「赤伶」→ 走闲聊（窗口过期）
4. A 发「点歌」→ B 发「赤伶」→ B 走闲聊，A 的槽位还在
5. 弹幕「点歌」→ 发「666」→ 走闲聊 → 再发「赤伶」→ **开始唱**
6. 弹幕「点舞」→ 角色问 → 发一个曲库里的舞名 → 播那支舞
7. 弹幕「换角色」→ 角色问 → 发一个模型库里的角色名 → **换成那个角色**
8. 同上但发一个不存在的名字 → 说一句 + 随机换
9. 弹幕「我想听首歌」（无关键词）→ LLM 判 song 无 arg → 追问 → 补全能接上
10. 弹幕「今天天气不错」→ 不问 LLM（日志无 IntentResolver 记录），正常闲聊
11. `intentFallbackEnabled` 设 false → 第 9 条退化成闲聊，第 1 条仍然工作
12. `slotWindowSeconds` 设 0 → 追问功能关闭，行为退回今天

## 任务拆分

| # | 任务 | 产出 |
|---|---|---|
| 1 | `Core/IntentSlots.cs` + 测试 | 槽位表 |
| 2 | `Core/IntentText.cs` + 测试 | 三个纯函数 |
| 3 | 配置 schema：两个 global 字段 + `askPrompt` + 默认规则集升级 + 文件头注释 | 配置层 |
| 4 | `Core/RuleQuery.cs` + `Core/NameMatch.cs` + 测试 | 规则反查与副本（`WithEffect` 要拷 `askPrompt`，所以排在配置之后） |
| 5 | `EffectRegistry` 三个效果改造 + `RewardService.SwitchAvatarByName` + `DouyinLiveManager` 重载 | 效果层 |
| 6 | `TriggerRouter.TryFillSlot/TryHandleIntent/OpenSlot` + `Route()` 改造 + `HandleChatLegacy` | 路由层（到这里第 1~8 条手工用例应全通） |
| 7 | `IntentResolver` + 接线 + README 更新 | LLM 兜底（第 9~12 条） |

任务 6 结束时确定性路径已完整可用；任务 7 是纯增量，砍掉也不影响前面。
