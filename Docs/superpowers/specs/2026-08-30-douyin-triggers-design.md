# 抖音直播互动：可配置触发层 + 效果注册表 + 舞蹈编排

- 日期：2026-08-30
- 状态：设计已确认，待评审
- 需求来源：`Docs/new.md`（用户手写的玩法设计稿）

## 1. 背景与目标

当前 `DouyinLiveManager.Route()` 把「事件类型 → 业务服务」的映射硬编码在一个
`switch` 里，每个 Service 内部又各自硬编码了自己的关键词、档位和文案。想加一个
「弹幕说『抱抱』就播某个动作」这样的玩法，必须改 C# 并重新出包。

本设计要达成三件事：

1. **触发逻辑全部可配置。** 任意触发源（弹幕/点赞/关注/礼物/进房/分享）配任意
   效果组合，改 `douyin_triggers.json` 存盘即生效，不用重新出包。
2. **效果可扩展。** 效果用字符串 ID 寻址。以后往 Animator 里加了新动画，只改
   json 就能用上，不改代码。
3. **落地 `new.md` 的三层动作模型。** L1 轻叠加 / L2 普通互动 / L3 重磅独占，
   带冷却、唱歌保护、以及「弹幕不能触发 L3」的硬规则。

顺带把跳舞玩法做起来：不重复轮播、冷场接舞、跳舞期间的表演增强。

## 2. 前置事实（实现前必须知道）

### 2.1 Animator 里真实存在的交互钩子只有 5 个

`AvatarAnimatorControllerV2.controller` 的参数里，能用来触发「一次性互动动作」
的只有：`Headpat`、`HairStroke`、`HoverFaceTrigger`、`HoverTrigger`、
`IntimeRegion`。面部层另有 `Happy`、`Angry`、`Cry`、`Fear` 四个状态。

`new.md` 列的 **11 个动作在项目里没有动画资源**：飞吻、踢屁股、抱抱、挥手、
转圈、鞠躬、礼花、捏脸、挠痒痒、戳脸、开心小跳。这些需要新增 `.anim` 并接进
Animator，纯代码补不出来。

**本设计的应对：** 效果 ID 用 `anim:<参数名>` 的通用形式而不是写死枚举。现在先
用这 5 个钩子做近似映射（默认配置里会标注哪些是占位），将来补了动画只需要在
json 里把 `anim:Headpat` 改成 `anim:Kiss`，代码一行不动。

### 2.2 `AvatarDanceSafetyZone` 不能直接用于竖屏直播

`Assets/MATE ENGINE - Scripts/AvatarHandlers/AvatarDanceSafetyZone.cs` 已经实现了
跳舞防出画，但默认 `enableSafety = false`，并且它的做法是**移动摄像机的同时把系统
窗口一起平移**（`moveWindowAlong = true`）。直播伴侣按窗口采集时，窗口位置漂移会
直接把画面搞乱。

**本设计的应对：** 竖屏模式下由 `DanceDirector` 打开它，但强制
`moveWindowAlong = false`、并按竖屏窗口宽度收窄 `softZone*Px`。退出竖屏时还原。

### 2.3 粒子主题目前只有一个

`AvatarParticleHandler` 的 themeTag 机制是通的，但 `CustomVRM.prefab` 里只登记了
`Dance Trail Blue` 一个主题。所以 `particle:` 效果现在**只能切到这一个主题**，
配别的名字会匹配不到任何粒子规则（不报错，静默无效果）。

**本设计的应对：** 默认配置里所有 `particle:` 一律写 `Dance Trail Blue`。想要
「关注专属礼花」「爱心粒子」这类区分，需要先在 prefab 里加粒子规则并起 themeTag，
属于美术资源工作，不在本设计范围内。`EffectRegistry` 在切主题后会检查是否真的匹配
到了规则，没匹配到则打一条警告，避免这种静默失效。

### 2.4 配置入口只有 settings.json

抖音设置页组件 `SettingsHandlerDouyinLive` 从未接进任何场景，所以所有新配置项要么
进 `settings.json`，要么进独立的 json 文件。本设计选后者（见 §5）。

### 2.5 礼物「档位」的口径与现状不一致

`new.md` 按**单价抖币**分档；现有 `RewardService.OnGift` 按
**总价值 = 单价 × 数量** 分档（`>=100` / `>=10` / 其余）。两者对「连刷 20 个 1 抖币
礼物」的判定不同。本设计把口径做成配置项 `giftUseTotalValue`，默认沿用现状
（`true`，总价值），并在默认配置里把阈值按当前直播间实际情况留成可调。

## 3. 架构

```
DouyinLiveClient
      │ DouyinEvent
      ▼
DouyinLiveManager.Route()
      │
      ├──► TriggerRouter.Match(ev) ──► List<EffectRequest>?
      │         （读 douyin_triggers.json，热重载）
      │              │ 命中
      │              ▼
      │        ActionDirector.Submit(requests)
      │              │ 分层仲裁 / 冷却 / 唱歌保护
      │              ▼
      │        EffectRegistry.Execute(effectId, ctx)
      │              │
      │              ├─► Animator 参数脉冲 / 面部状态
      │              ├─► UniversalBlendshapes 表情
      │              ├─► AvatarParticleHandler 粒子主题
      │              ├─► AvatarBigScreenHandler 大屏
      │              ├─► DanceDirector 跳舞
      │              ├─► SongService 唱歌
      │              ├─► RewardService 换角色 / 换装
      │              └─► SpeechPipeline 口播
      │
      └──► 未命中 ──► 现有逻辑（AI 回复 / 欢迎 / 点赞感谢 / 礼物致谢）
```

关键点：**旁路式**。`TriggerRouter` 命中才接管，未命中原样走今天的代码路径。
删掉 `douyin_triggers.json` 就完全回到当前行为，回退成本为零。

各单元职责边界：

| 单元 | 职责 | 依赖 |
|---|---|---|
| `TriggerRules` | 配置数据结构、加载、热重载、生成默认文件 | 无 Unity 场景依赖 |
| `TriggerMatcher` | 纯函数：`(DouyinEvent, TriggerConfig) → 命中的规则` | 无 Unity 依赖 |
| `TriggerRouter` | 持有配置 + 文件监听，把 `TriggerMatcher` 的结果包成 `EffectRequest` | `TriggerRules` |
| `ActionDirector` | 分层仲裁、冷却计时、唱歌/跳舞保护、L3 排队 | `EffectRegistry` |
| `EffectRegistry` | 字符串 ID → 具体执行器 | 各 Handler / Service |
| `DanceDirector` | 选舞、连播、跳舞期间的表演增强 | `AvatarDanceHandler` |

## 4. EffectRegistry：效果词汇表

这是「我们支持哪些效果」的权威清单。ID 全部小写前缀 + 冒号参数。

| Effect ID | 建议层级 | 实现 | 状态 |
|---|---|---|---|
| `anim:<参数名>` | L1/L2 | Animator bool 脉冲（0.4s 后复位），参数不存在则忽略并打一次警告 | 现有参数：`Headpat` `HairStroke` `HoverFaceTrigger` `HoverTrigger` `IntimeRegion` |
| `face:<状态名>` | L1 | 面部层 `CrossFadeInFixedTime` | 现有：`Happy` `Angry` `Cry` `Fear` |
| `mood:<happy\|love\|sad\|surprise>` | L1 | 复用 `SpeechPipeline` 的 `DriveEmotion`（写 `UniversalBlendshapes`） | 可用 |
| `particle:<themeTag>` | L1 | `AvatarParticleHandler.SetTheme`，N 秒后还原原主题 | 可用，但**目前 `CustomVRM.prefab` 里只配了一个主题 `Dance Trail Blue`**，见 §2.5 |
| `bigscreen` | L3 | 复用 `DouyinLiveManager.BigHeadMoment()`（含 `keepWindowSize = true`） | 可用 |
| `dance:random` | L3 | `DanceDirector` 洗牌选舞 | 可用 |
| `dance:<舞名>` | L3 | `AvatarDanceHandler.FindIndexByTitleFuzzy` + `PlayIndex` | 可用 |
| `dance:builtin` | L3 | 置 `isDancing = true`（内置 5 个舞步） | 可用 |
| `song:<歌名>` | L3 | `SongService.RequestSong` | 可用 |
| `song:request` | 特殊 | 进入点歌流程，从弹幕正文提取歌名 | 可用 |
| `swapAvatar` | L3 | `RewardService.SwitchRandomAvatar`（需改为 public） | 可用 |
| `outfit:random` / `outfit:<名>` | L3 | `AccessoiresHandler` 切换配件 | **实现时需确认 API**，接不上则该 ID 降级为 no-op + 警告 |
| `say:<文本>` | 附加 | `SpeechPipeline.Enqueue`，支持 `{u}`(昵称) `{g}`(礼物) `{n}`(数量) 占位符 | 可用 |
| `sayAI:<提示词>` | 附加 | 走 `DanmakuAIService` 生成后再 TTS | 可用 |
| `menu` | 特殊 | 口播玩法说明 | 可用 |

设计约束：

- **未知 ID 不崩、不静默。** 解析失败在日志里打一条 `[Trigger] 未知效果: xxx`，
  该效果跳过，同一规则的其它效果照常执行。
- **`say:` / `sayAI:` 不占层级。** 它们和动作并行，不参与 L1/L2/L3 独占仲裁，只受
  `SpeechPipeline` 自己的队列和优先级管理。
- **`anim:` 的参数名大小写敏感**，与 Animator 保持一致。

## 5. 配置文件 `douyin_triggers.json`

位置：`%AppData%\LocalLow\Shinymoon\MateEngineX\douyin_triggers.json`
（与 `douyin_persona.json` / `douyin_gift_rules.json` 同目录）。

### 5.1 Schema

```jsonc
{
  "version": 1,
  "global": {
    "chatCooldown": 1.0,        // 弹幕触发全局冷却（秒）
    "likeCooldown": 3.0,        // 点赞触发全局冷却
    "giftCooldown": 1.2,        // 礼物触发全局冷却
    "allowChatL3": false,       // new.md 硬规则：弹幕不能炸大效果
    "l3InterruptSinging": false,// L3 是否可以打断正在唱的歌
    "giftUseTotalValue": true   // true=单价×数量 false=只看单价
  },
  "rules": [
    {
      "id": "pat",                       // 唯一标识，仅用于日志和冷却记账
      "enabled": true,
      "source": "chat",                  // chat|like|follow|gift|enter|share
      "keywords": ["拍头", "敲脑袋", "摸头"],
      "effects": ["anim:Headpat", "mood:happy"],
      "pick": "all",                     // all=全执行 random=随机选一个
      "level": "L1",                     // L1|L2|L3
      "cooldown": 0,                     // 本规则独立冷却，0=只受全局冷却
      "sayFallback": ""                  // 可选：本规则里的 sayAI: 失败时改说这句
    }
  ]
}
```

各 `source` 支持的匹配字段：

| source | 匹配字段 | 说明 |
|---|---|---|
| `chat` | `keywords`（包含匹配）/ `regex` | 两者都填则任一命中即算命中 |
| `like` | `everyN` / `milestone` | `everyN:30`＝累计每 30 赞触发一次；`milestone:3000`＝累计跨过 3000 时触发一次 |
| `gift` | `giftName`（包含匹配）/ `minDiamond` / `maxDiamond` / `minCount` | 省略即不限；抖币口径由 `giftUseTotalValue` 决定 |
| `follow` / `enter` / `share` | 无 | 该源的事件一律命中 |

### 5.2 匹配语义

- **数组顺序即优先级，第一条命中即停。** 更具体的规则写在前面。这条规则是刻意
  选的：现有 `RewardService.MatchRule` 用「礼物名精确 > 单价高」的隐式优先级启发式，
  出问题时很难解释为什么是那条规则赢了。顺序优先足够表达所有场景，且所见即所得。
- `enabled: false` 的规则在匹配时直接跳过（方便临时关掉某个玩法而不删配置）。
- 弹幕规则未命中时，事件继续交给现有的 `RewardService.TryHandleDanmaku` →
  `DanmakuAIService.OnDanmaku`，AI 回复功能完全不受影响。

### 5.3 热重载与容错

- 用 `FileSystemWatcher` 监听文件，变更后 debounce 500ms 再重新解析（编辑器保存
  常触发多次事件）。
- **解析失败时保留上一份可用配置**，并通过 `SpeechPipeline` 之外的渠道（`Debug.LogError`
  + 可选的屏幕角标）报错。绝不因为 json 写错就让直播间哑掉。
- 文件不存在时**自动写出一份带完整注释的默认配置**——这份文件本身就是给用户看的
  文档，README 只需要指路。

### 5.4 默认规则集

默认配置按 `new.md` 落地，并对没有动画资源的动作做近似映射（注释里标 `占位`）：

```jsonc
// 弹幕 L1
{ "source":"chat", "keywords":["拍头","敲脑袋","摸头"], "effects":["anim:Headpat"],          "level":"L1" },
{ "source":"chat", "keywords":["捋头发","顺毛"],         "effects":["anim:HairStroke"],       "level":"L1" },
{ "source":"chat", "keywords":["捏脸","戳脸","挠痒痒"],   "effects":["anim:HoverFaceTrigger","mood:happy"], "level":"L1" }, // 占位：等专属动画
// 弹幕 L2（当前全部为占位映射）
{ "source":"chat", "keywords":["飞吻","么么","抱抱"],     "effects":["anim:HoverFaceTrigger","mood:love","particle:Dance Trail Blue"], "level":"L2" },
{ "source":"chat", "keywords":["挥手","你好","打招呼"],   "effects":["anim:HoverTrigger"],     "level":"L2" },
// 弹幕特殊指令
{ "source":"chat", "keywords":["菜单","玩法"],           "effects":["menu"] },
{ "source":"chat", "keywords":["点歌"],                  "effects":["song:request"] },
{ "source":"chat", "keywords":["换角色","换装","换个人"], "effects":["swapAvatar"], "level":"L3" },  // allowChatL3=false 时默认不生效
// 点赞
{ "source":"like", "everyN":30,     "effects":["anim:Headpat","anim:HairStroke","anim:HoverFaceTrigger"], "pick":"random", "level":"L1" },
{ "source":"like", "milestone":3000,"effects":["face:Happy","particle:Dance Trail Blue","say:哇！我们已经破三千赞啦，谢谢家人们！"], "level":"L2" },
// 关注
{ "source":"follow", "effects":["bigscreen","particle:Dance Trail Blue","say:感谢 {u} 的关注，欢迎来到直播间！"], "level":"L3" },
// 礼物三档（阈值按实际直播间调）
{ "source":"gift", "maxDiamond":9,   "effects":["anim:Headpat","anim:HairStroke"], "pick":"random", "level":"L1" },
{ "source":"gift", "minDiamond":10, "maxDiamond":99, "effects":["face:Happy","particle:Dance Trail Blue"],   "level":"L2" },
{ "source":"gift", "minDiamond":100, "effects":["bigscreen","dance:random","sayAI:观众{u}送了{g}，用一句话热情感谢并说要跳舞回报"], "level":"L3" }
```

> `换角色` 这条默认写成 L3 且 `allowChatL3: false`，意味着**开箱即用时弹幕换角色是关的**。
> 用户想开只需把 `allowChatL3` 改 true，或把这条规则的 `level` 改成 `L2`。
> 这与现有行为（弹幕可以换角色，30 秒冷却）不同 —— 这是一处**行为变更**，
> 需要在 README 显式说明。

### 5.5 礼物档位说明

阈值 `maxDiamond:9` / `10~99` / `minDiamond:100` 只是起始默认值。用户按实际直播间
调整时改这三行即可；配合 `giftUseTotalValue`：

- `true`（默认，沿用现状）：连刷 20 个 1 抖币的小心心 = 20 抖币，会命中 L2 档。
  适合人气低、以小礼物为主的直播间——刷得多也能看到升级反馈。
- `false`（`new.md` 口径）：只看单价，20 个小心心仍然是 L1。适合防止刷屏炸效果。

## 6. ActionDirector：分层仲裁

按 `new.md` 的三层模型实现，行为定义如下：

| 层级 | 打断规则 | 唱歌时 | 冷却 |
|---|---|---|---|
| L1 | 不打断任何东西，可与其它 L1 叠加 | **正常播放** | 全局源冷却 + 规则冷却 |
| L2 | 打断闲聊（`IdleChatterService` 的暖场话），不打断唱歌/跳舞 | **降级为只执行 `particle:` / `mood:` 效果，不播动画** | 同上，另加 L2 最小间隔 |
| L3 | 独占；执行期间拒绝新的 L3（改为排队） | 由 `l3InterruptSinging` 决定；false 则排队等唱完 | 同上，另加 L3 最小间隔 |

要点：

- **L3 排队而不是丢弃。** 大哥连刷礼物时，丢弃会让观众只看到一次效果，体验很差。
  队列上限 3 条，超出丢最旧的，避免积压到几分钟后才播。
- **弹幕 L3 硬拦截。** `source == "chat"` 且 `level == "L3"` 且 `!allowChatL3` →
  直接丢弃并在 debug 日志里说明原因。这是 `new.md` 的硬规则。
- **判定「正在唱歌」** 用 `SongService.IsPlaying`；「正在跳舞」用
  `AvatarDanceHandler.IsPlaying`。
- **时间源可注入。** `ActionDirector` 内部不直接调 `Time.unscaledTime`，而是通过一个
  `Func<float> Now`（默认指向 `Time.unscaledTime`），这样冷却逻辑可以在 EditMode
  测试里跑而不需要进播放模式。

## 7. DanceDirector：跳舞增强

### 7.1 自动编排

- **洗牌袋轮播。** 把 `AvatarDanceHandler` 的全部舞包索引洗牌成一个队列，逐个消费，
  消费完重新洗牌。保证「一轮之内不重复」，比 `rng.Next(count)` 的纯随机体验好得多
  （现在 `RewardService.TryPlayRandomCustom` 就是纯随机，10 个舞包里连着抽到同一支很常见）。
  重洗时避开上一轮最后一支，防止跨轮相邻重复。
- **冷场接舞。** `IdleChatterService` 现在深度冷场只会唱歌。改成「唱歌 / 跳舞」交替：
  新增 `AutoDanceEnabled`，深度冷场时若上次是唱歌则这次跳舞，反之亦然。舞包为空时
  自动全部回退到唱歌（保持现有行为）。
- **连播。** 新增 `danceChainCount`（默认 1）。一支跳完后若队列里还有排队的 L3 舞蹈
  请求，或 `danceChainCount > 1`，则接着跳下一支再回 idle。

### 7.2 表演增强

- **粒子联动。** 开跳时按配置切到指定粒子主题，跳完还原用户原本选择的主题。必须记录
  进入前的 `selectedTheme` 并在 `StopPlay` / 异常退出时还原，否则用户的粒子设置会被
  悄悄改掉。注意 §2.3：目前只有一个主题，所以这一条在补充新粒子资源之前实际是空转，
  代码先做好、效果等资源。
- **防出画。** 竖屏模式下（`PortraitWindowController.Active == true`）开跳时打开
  `AvatarDanceSafetyZone`，但强制 `moveWindowAlong = false`（见 §2.2），并按当前窗口
  宽度把 `softZoneLeftPx/RightPx` 收窄到窗口宽度的 15%。跳完还原这些字段和
  `enableSafety` 的原值。
- **换装联动。** L3 舞蹈规则里可以配 `outfit:random`，效果注册表按数组顺序执行，
  换装会在起舞前完成。

## 8. 语音文案策略

- **固定文案打底。** 所有 `say:` 走模板 + 占位符替换，零延迟、零 API 成本、可控。
- **只有礼物 L3 走 AI**（`sayAI:`）。理由：贵重礼物是低频高价值事件，值得等 1-3 秒
  换一句有人味的定制感谢；其它高频事件用 AI 会拖慢反馈节奏并烧 token。
- `sayAI:` 失败或超时（3 秒）时**回落到同规则里可选的 `sayFallback` 字段**，
  没配则回落到现有 `RewardService.CelebrateBig` 的模板文案。绝不让大礼物没有反馈。
- 所有 AI 生成文案继续过 `ContentFilter`（`douyin_blocked_words.txt`）。

## 9. 改动清单

### 新增

| 文件 | 说明 |
|---|---|
| `Game APIs/DouyinLive/Core/DouyinEvents.cs` | **从现位置移入**（见 §10 测试） |
| `Game APIs/DouyinLive/Core/TriggerRules.cs` | 配置 POCO |
| `Game APIs/DouyinLive/Core/TriggerMatcher.cs` | 纯函数匹配逻辑 |
| `Game APIs/DouyinLive/Core/MateEngine.DouyinLive.Core.asmdef` | 为了能写单元测试 |
| `Game APIs/DouyinLive/TriggerRouter.cs` | 配置加载 + 热重载 + 事件路由 |
| `Game APIs/DouyinLive/ActionDirector.cs` | 分层仲裁 |
| `Game APIs/DouyinLive/EffectRegistry.cs` | 效果 ID → 执行器 |
| `Game APIs/DouyinLive/DanceDirector.cs` | 舞蹈编排 |
| `Tests/EditMode/DouyinLive/*` | 见 §10 |

### 修改

| 文件 | 改动 |
|---|---|
| `DouyinLiveManager.cs` | `Route()` 前置 `TriggerRouter` 匹配；持有并初始化新组件；`ApplySettings` 里接线；`TriggerBigHeadMoment` 改为 public 供 `EffectRegistry` 调用 |
| `RewardService.cs` | `SwitchRandomAvatar` 改 public；`TryPlayRandomCustom` 的随机选舞委托给 `DanceDirector`；礼物档位文案保留为 `sayAI` 的回落 |
| `IdleChatterService.cs` | 深度冷场增加跳舞分支（唱/跳交替） |
| `README.md` | 新增 `douyin_triggers.json` 说明；标注「弹幕换角色默认关闭」的行为变更 |
| `Docs/DouyinLive-Integration.md` | 补充新架构的数据流 |

### 不动

`SpeechPipeline`、`DanmakuAIService`、`WelcomeService`、`LikeService`、`SongService`、
`AudienceMemory`、`LiveOpsService`、`PersonaCard`、`ContentFilter`、
`PortraitWindowController` 全部不改。这是选旁路式架构的直接收益。

## 10. 测试策略

**现状：项目本身没有任何测试工程。** `Packages/manifest.json` 里没有 `testables`，
所有脚本都在 `Assembly-CSharp` 里，没有 asmdef。

Unity 的约束是：`Assembly-CSharp` 自动引用所有 asmdef，所以**测试 asmdef 无法反向
引用 `Assembly-CSharp`**。想给 `TriggerMatcher` 写单元测试，被测代码必须先搬进一个
自己的 asmdef。

采取的方案：

1. 新建 `MateEngine.DouyinLive.Core.asmdef`，把三个**纯逻辑、无场景依赖**的文件放进去：
   `DouyinEvents.cs`（移入）、`TriggerRules.cs`、`TriggerMatcher.cs`。
   `Assembly-CSharp` 会自动引用它，其余代码不用改 using（命名空间保持 `DouyinLive`）。
2. 新建 `Tests/EditMode` 测试 asmdef，引用 Core + `nunit.framework`，
   并在 `manifest.json` 加 `"testables"`。
3. 覆盖的场景（这些是最容易写出隐蔽 bug 又最难肉眼发现的部分）：
   - 关键词匹配、正则匹配、大小写与空白处理
   - 顺序优先：更具体的规则在前时确实先命中
   - `everyN` 跨事件累计、`milestone` 只触发一次
   - 礼物档位边界（9/10/99/100），`giftUseTotalValue` 两种口径
   - `allowChatL3 = false` 时弹幕 L3 被拦截
   - 冷却：注入假时钟验证全局冷却与规则冷却互不干扰
   - 坏 json（缺字段、类型错、语法错）不抛异常且保留旧配置
4. **不做单元测试的部分**：`EffectRegistry` 的执行、`DanceDirector`、
   `ActionDirector` 与真实 Animator 的交互。这些靠 `Tools/douyin_mock_server.py`
   手动验证，并为它补充按规则 id 直接触发的调试命令。

## 11. 分期实施建议

拆成三期，每期结束都是可用、可回退的状态：

- **第一期：触发层骨架。** `TriggerRules` + `TriggerMatcher` + `TriggerRouter` +
  `EffectRegistry`（先只实现 `anim:` `face:` `mood:` `particle:` `say:` `menu`）+
  Core asmdef + 单元测试。`ActionDirector` 只做最简单的全局冷却。
  这一期结束就能配弹幕关键词玩法了。
- **第二期：分层仲裁 + 重磅效果。** `ActionDirector` 完整三层逻辑、L3 排队、唱歌保护；
  `EffectRegistry` 补齐 `bigscreen` `dance:*` `song:*` `swapAvatar` `outfit:*` `sayAI:`。
- **第三期：舞蹈增强。** `DanceDirector` 洗牌轮播、冷场接舞、连播、粒子联动、
  竖屏防出画。

## 12. 明确不做（YAGNI）

- **观众点舞投票 / 礼物解锁舞蹈**：需求未确认，先不做。
- **`new.md` 第四节**（主动找人聊天、讲故事、小游戏）：独立能力，等前三期稳定后单独立项。
- **`new.md` 第三节的直播间侧边指令列表**：那是抖音直播伴侣里的贴纸/挂件，做一张静态
  图片加到伴侣的素材里即可，不需要本程序写任何代码。
- **触发规则的图形化编辑界面**：`SettingsHandlerDouyinLive` 都还没接进场景，
  先把带注释的 json 做好。
- **多套配置方案切换**（如「日常档 / 大促档」）：等真的有这个需求再说。
