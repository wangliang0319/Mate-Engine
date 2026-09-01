# MateEngine 抖音直播 AI 虚拟主播版

基于开源桌宠项目 [MateEngine](https://store.steampowered.com/app/3625270/MateEngine/) 二次开发的**抖音直播 AI 聊天机器人**：
一只住在桌面上的 VRM 虚拟角色，能自动欢迎观众、AI 语音回复弹幕、点歌唱歌跳舞、答谢礼物——开播即用的 AI 虚拟主播。

![Mate Engine Preview](https://i.imgur.com/5cHHH8c.jpeg)

## 功能总览

| 功能 | 触发方式 | 效果 |
|---|---|---|
| 自动欢迎 | 观众进房/关注/分享 | 语音欢迎，高峰合并播报；老观众/大哥专属欢迎词 |
| AI 弹幕回复 | 普通弹幕 | 大模型生成人设化回复 → TTS 语音 + 口型 + 表情 |
| 快捷反应 | 666/哈哈/你好/晚安等 | 预制回复秒回，不消耗大模型 |
| 点赞感谢 | 任意点赞 | 点名感谢（15秒冷却合并），里程碑欢呼 |
| 礼物庆祝 | 送礼物 | 按价值三档反应，大礼物欢呼+跳舞+大头特写 |
| 点歌 | 弹幕 `点歌 歌名`，或先发 `点歌` 再发歌名 | 网易云搜歌 → 播放高潮段 → 跟节奏跳舞 |
| 换角色 | 弹幕 `换角色`，或在角色追问后说出角色名 | 切换模型库中的 VRM，自动身高归一化 |
| 两轮点播 | 角色追问后 30 秒内的回答 | 点歌/点舞/换角色都支持「先问再答」，答不上来的弹幕不算数 |
| 玩法菜单 | 弹幕 `菜单` | 口播玩法教学 |
| 自定义触发 | 配置 `douyin_triggers.json` | 任意关键词/点赞数/礼物档位 → 任意效果组合，改完存盘即生效 |
| 冷场暖场 | 90 秒无互动 | 自动求赞/求关注/闲聊；冷场 5 分钟自动唱歌 |
| 直播运营 | 自动 | 每 30 分钟礼物感谢榜、整点报时 |
| 竖屏直播窗口 | 配置自动 | 按 `douyinPortraitAspect` 切竖屏窗口，适配直播伴侣采集 |
| 观众记忆 | 自动 | 记住常客来访次数/礼物总额，回复更有人情味 |

## 快速开始

### 环境准备

1. **Unity 6000.2.6f2**（仅二次开发需要；直接使用打包版可跳过）
2. **弹幕抓取器**：[DouyinBarrageGrab](https://github.com/ape-byte/DouyinBarrageGrab)，管理员运行，默认推送 `ws://127.0.0.1:8888`
3. **大模型 API Key**：任意 OpenAI 兼容服务（DeepSeek / 通义 / 中转站均可）

### 运行流程

```
1. 管理员启动 WssBarrageServer.exe（只开一个实例）
2. 打开抖音直播伴侣并开播（顺序：抓取器在前，直播在后）
3. 启动 MateEngineX.exe
4. 直播伴侣添加素材 → 窗口/游戏进程 → 选择 MateEngineX
5. 观众互动，角色自动开口
```

### 配置

配置文件：`C:\用户\<你>\AppData\LocalLow\Shinymoon\MateEngineX\settings.json`
**修改前必须先关闭程序**（程序退出时会用内存中的旧值覆盖文件）。

#### 总开关与连接

| 配置项 | 推荐值 | 说明 |
|---|---|---|
| `enableDouyinLive` | `true` | 直播互动总开关 |
| `douyinWsUrl` | `"ws://127.0.0.1:8888"` | 弹幕抓取器推送地址，改过抓取器端口才需要动 |

#### 大模型（AI 回复）

| 配置项 | 推荐值 | 说明 |
|---|---|---|
| `aiBaseUrl` | `"https://xxx/v1"` | OpenAI 兼容地址，**结尾一般要带 /v1** |
| `aiApiKey` | `"sk-..."` | 明文存储，注意直播时不要展示此文件 |
| `aiModel` | `"qwen3-30b-a3b-instruct-2507"` | 实测最快（1.2秒）且中文自然；`deepseek-chat` 亦可 |
| `aiFallbackToLocal` | `true` | 云端失败时降级本地 LLMUnity，断网不哑场 |
| `douyinLivePrompt` | `""` | 追加人设（完整人设请用 douyin_persona.json） |

#### 语音 TTS

| 配置项 | 推荐值 | 说明 |
|---|---|---|
| `ttsProvider` | `0` | 0=云端 OpenAI 兼容；1=EdgeTTS（**已被微软封禁勿用**）；3=纯字幕无语音 |
| `ttsBaseUrl` / `ttsApiKey` | 见下 | 留空才复用 aiBaseUrl / aiApiKey。**TTS 和大模型可以是两家**，聊天用中转站、语音用专门的 TTS 服务是推荐配法 |
| `ttsModel` | `"FunAudioLLM/CosyVoice2-0.5B"` | 硅基流动，中文母语训练，流式延迟 150ms，约 $7.15/百万 UTF-8 字节（1000 汉字≈¥0.15） |
| `ttsVoice` | `"diana"` | CosyVoice2 预置 8 音色，女声：`diana` 欢快 / `claire` 温柔 / `bella` 激情 / `anna` 沉稳；男声：`alex` / `benjamin` / `charles` / `david` |
| `ttsInstructions` | 见下 | 风格指令，**只有 `gpt-4o-mini-tts` 这类带 `instructions` 字段的模型认**，CosyVoice 会忽略。示例："你是一个20岁左右的中国甜美少女主播，声音清脆软糯、音调偏高，语气活泼带一点撒娇，说标准普通话，绝对不能有外国口音" |
| `ttsVolume` / `ttsSpeed` | `1.0` / `1.0` | 音量 / 语速（CosyVoice2 语速范围 0.25~4.0） |
| `lipSyncGain` | `1.0` | 口型幅度，嘴张不开调大 |

**为什么单独配 TTS 服务**：很多 OpenAI 中转站只转发文本模型，`/v1/audio/speech` 要么没有通道、
要么返回 `not implemented`（模型列表里挂着 TTS 模型也一样，那只是列表）。语音出不来但文本正常，
基本就是这个原因。换一家专门做 TTS 的服务填进 `ttsBaseUrl` / `ttsApiKey` 即可，不影响聊天走原来的中转站。

配硅基流动要改的几行：

```json
"ttsProvider": 0,
"ttsBaseUrl": "https://api.siliconflow.cn/v1",
"ttsApiKey": "sk-你在硅基流动申请的key",
"ttsModel": "FunAudioLLM/CosyVoice2-0.5B",
"ttsVoice": "diana"
```

CosyVoice 系列和 OpenAI 的请求形状有两处差异：
`voice` 实际要发 `模型名:音色名`（只写 `diana` 会报 `Invalid voice`，程序已自动补全前缀，配置里照常只写音色名）；
它没有 `instructions` 字段，**所以配 CosyVoice 时 `ttsInstructions` 不起作用**，音调语气只能靠 `ttsVoice` 挑音色。
官方文档提到可以用 `<|endofprompt|>` 把风格指令拼进正文，但硅基流动的部署实测不认这个标记——
同一句 13 字的话，纯正文合成 1.66 秒，拼上指令后变成 9.7~24 秒，指令被当成正文念了出来，
所以程序会直接丢弃 `ttsInstructions`。想要甜美音就选 `diana`，想要温柔就选 `claire`。

#### 互动行为

| 配置项 | 推荐值 | 说明 |
|---|---|---|
| `douyinWelcomeEnabled` | `true` | 进房/关注/分享欢迎 |
| `douyinWelcomeCooldown` | `8.0` | 欢迎冷却秒数，人多的直播间可调到 15 |
| `douyinAIReplyEnabled` | `true` | AI 弹幕回复 |
| `douyinAIReplyMinInterval` | `8.0` | 两次回复最小间隔秒数；弹幕多调大（10~15），弹幕少调小（5） |
| `douyinLikeReactEnabled` | `true` | 点赞感谢（有赞就谢，15 秒冷却合并） |
| `douyinLikeThreshold` | `100` | 合并批量超过该值时改播总数而非点名 |
| `douyinGiftEnabled` | `true` | 礼物三档庆祝 + 点歌/换角色命令 |
| `douyinBigHeadReaction` | `true` | 关注/礼物时大头特写致谢 |

#### 冷场暖场

| 配置项 | 推荐值 | 说明 |
|---|---|---|
| `douyinIdleChatterEnabled` | `true` | 冷场自动暖场（求赞/求关注/闲聊轮换） |
| `douyinIdleThreshold` | `90.0` | 多少秒无互动算冷场 |
| `douyinIdleAutoSongEnabled` | `true` | 深度冷场自动唱歌 |
| `douyinIdleAutoSongThreshold` | `300.0` | 冷场多少秒后开唱（测试时可临时改 30） |
| `douyinIdleAutoSongMinInterval` | `600.0` | 两次深度冷场表演的最小间隔 |
| `douyinIdleAutoDanceEnabled` | `true` | 深度冷场自动跳舞，和唱歌交替 |
| `douyinDanceChainCount` | `1` | 一次触发连跳几支舞 |
| `douyinDanceParticleTheme` | `""` | 跳舞期间临时切的粒子主题（留空=不切；目前只有 `Dance Trail Blue`） |
| `douyinDancePortraitSoftZoneRatio` | `0.15` | 竖屏防出画软边界占屏宽的比例 |
| `douyinSongLoudness` | `0.055` | 唱歌响度。歌曲播放前会统一归一化到这个 RMS，嫌轻调大、嫌吵调小 |
| `douyinIdleSongList` | 默认18首古风 | 自动唱歌歌单，直接增删歌名；重复项启动时自动去重；空数组=不自动唱 |

`douyinIdleAutoSongEnabled` 以前是整个深度冷场表演的总开关，现在只管唱歌，跳舞由 `douyinIdleAutoDanceEnabled`（默认 `true`）独立控制，升级前关过自动唱歌的人升级后会开始看到自动跳舞。

#### 竖屏直播窗口

| 配置项 | 推荐值 | 说明 |
|---|---|---|
| `douyinPortraitAspect` | `0.75` | 窗口宽/高比，**唯一开关**：`>0` 开播即自动切竖屏窗口，`0`（或负数）保持普通桌宠窗口（Unity 会记住上次的窗口尺寸，程序会按窗口大小档位主动改回来）。0.5625=严格9:16；**跳舞走位出画就调大**（0.85~1.0）；配合“角色缩小60~70%+舞台背景”的布局效果最佳 |

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
| `dance:request` / `dance:ask` | 点舞。`request` 先从弹幕正文取舞名，取不到就追问；`ask` 直接追问 |
| `song:<歌名>` / `song:request` / `song:ask` | 唱歌。`request` 先从弹幕正文取歌名，取不到就追问；`ask` 直接追问 |
| `swapAvatar` / `swapAvatar:<角色名>` / `swapAvatar:request` / `swapAvatar:ask` | 换 VRM 角色，自动身高归一化。不带参数=随机；带名字按 `avatars.json` 的 `displayName` 模糊匹配 |
| `outfit:random` / `outfit:<配件名>` | 切换配件 |
| `say:<文本>` | 固定文案，支持 `{u}` 昵称 / `{g}` 礼物名 / `{n}` 数量 |
| `sayAI:<提示词>` | 让大模型现场生成一句。3 秒没回来就说规则里的 `sayFallback` |
| `menu` | 口播玩法说明 |

**追问（两轮点播）：** `request` / `ask` 会让角色先问一句，然后**记住是谁在被问**。
这个观众接下来 30 秒内发的第一条像样的弹幕就当成答案——发「点歌」，角色问「想听什么歌呀」，
再发「赤伶」，直接开唱。答案是「666」「哈哈哈」这类无意义内容时不算数，槽位留着继续等。
只认发起人，别人插嘴不影响。规则里可以加 `askPrompt` 自定义追问文案（支持 `{u}`）。

**关键词没配到的说法**（「我想听点音乐」）会先过一遍本地词表预筛，命中才花 1.5 秒
问一次大模型判意图，判不出来就正常走 AI 闲聊。同一观众 15 秒最多问一次，
全局同时最多 2 个在飞，所以刷屏刷不动它。不想烧这个 token 就把 `intentFallbackEnabled` 设成 `false`。

**动作三层：** `L1` 轻叠加（不打断唱歌）/ `L2` 普通互动（唱歌时只出粒子不播动画）/ `L3` 重磅独占。

**防刷屏的四道闸**（一个请求要全部通过才执行，被拦下时日志会写明是哪道）：

| 参数 | 位置 | 作用 |
|---|---|---|
| `chatCooldown` / `likeCooldown` / `giftCooldown` | `global` | 该来源的整体节奏 |
| `perUserCooldown` | `global`（可在规则里覆盖）| 同一观众的间隔。**防刷屏主力**：只冻结他自己，不影响别人 |
| `cooldown` | 单条规则 | 这个玩法自己的节奏。换角色默认 60 秒 |
| `l2MinInterval` / `l3MinInterval` | `global` | 跨规则的层级总闸。`l3MinInterval` 默认 45 秒 |
| `l3QueueSize` | `global` | L3 排队上限，默认 3，满了挤掉最旧的一条 |
| `l3InterruptSinging` | `global` | 唱歌时 L3 是否可以打断，默认 `false` |
| `slotWindowSeconds` | `global` | 追问后等观众回答的秒数，默认 30。设 `0` 关闭追问功能 |
| `intentFallbackEnabled` | `global` | 关键词没中时是否问大模型判意图，默认 `true` |

**追问的回答不再走这四道闸**：开槽那一次已经过闸并记账了，追问的两轮是一次请求的两半。
不这么做的话，换角色 60 秒的规则冷却和 45 秒的 L3 间隔会把 30 秒窗口内的回答全部拦死。
滥用也不成立——要开槽必须先过闸，一个槽只能被取走一次。

**礼物档位**按 `minDiamond` / `maxDiamond` 配，默认 1-9 / 10-99 / ≥100 抖币，按自己直播间的实际礼物结构调。
`global.giftUseTotalValue` 为 `true`（默认）时按「单价 × 数量」算，连刷 20 个 1 抖币的小心心会命中中档；
改成 `false` 则只看单价。

**匹配细节与注意事项：**

- `keywords` 匹配大小写**不敏感**（`OrdinalIgnoreCase`），而 `regex` 走裸 `Regex.IsMatch`，大小写**敏感**。想让正则忽略大小写要自己写 `(?i)`。
- 不要写 `source: "enter"` 的规则。触发层跑在 `WelcomeLikeServices` 之前，命中后进场事件被消费掉，`Audience.RecordVisit` 就不会执行，「大哥回归 / 老朋友」识别会被永久破坏。默认规则集刻意不含 enter 规则。

改坏了不要紧：解析失败会保留上一份可用配置并在日志里报错，直播间不会哑掉。
想恢复出厂设置就把文件删掉重启。

#### 个性化数据文件（同目录，首次运行自动生成）

| 文件 | 说明 |
|---|---|
| `douyin_persona.json` | **人设卡**：name(名字)/identity(身份)/personality(性格)/background(背景)/catchphrases(口头禅)/taboos(禁忌话题)/speakingStyle(说话风格)，AI 人格的核心来源 |
| `douyin_blocked_words.txt` | 敏感词表（一行一词，# 开头为注释）。AI 输出含词即静默丢弃，挑衅弹幕不接茬——**直播合规，建议按需补充** |
| `douyin_audience.json` | 观众记忆（自动维护勿手改）：来访次数/礼物总额/最近弹幕 |
| `douyin_gift_rules.json` | 礼物规则：`{"giftName":"玫瑰","minDiamond":0,"minCount":1,"action":"randomDance"}`，action 可选 `thanks`/`randomDance`/`builtinDance`/`dance:舞名` |
| `douyin_triggers.json` | **触发规则总表**：谁触发、触发什么、多久能触发一次。首次运行自动生成，文件头部有完整注释。改完存盘即生效（无需重启）。详见上面的「自定义触发规则」一节 |

#### 程序内快捷键

| 按键 | 功能 |
|---|---|
| `B` | 大头模式开/关（原生功能，直播中窗口尺寸不变） |
| `F1` | 径向菜单（原生） |
| `F8` | 数值调试面板（原生，误按再按一次关闭） |

### 离线测试（不开直播）

```bash
python Tools/douyin_mock_server.py        # 模拟弹幕服务器
# 命令：c 弹幕 | e 进房 | f 关注 | l 点赞 | g 礼物 | gg 大礼物
#       song 歌名 | sw 换角色 | auto 自动压测 | q 退出
```

## 开发者文档

- 集成与实现细节：[Docs/DouyinLive-Integration.md](Docs/DouyinLive-Integration.md)
- 系统设计：[Docs/DouyinLive-Design.md](Docs/DouyinLive-Design.md)
- 直播相关代码：`Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/`
- 打包：Unity → File → Build Profiles → Windows(Mono) → Build

## 增强舞蹈效果

- 内置舞步仅 5 个动画；把 MMD 舞包放入 `StreamingAssets/CustomDances`，
  点歌时自动匹配同名舞包（真编舞 + 原曲音频，效果最佳）
- 舞包资源可在 B 站 / 爱发电搜索 "MateEngine 舞蹈" 获取

## 已知限制

- EdgeTTS 免费接口已被微软封禁（2026-08 实测 403），默认使用云端 TTS
- 多数 OpenAI 中转站不提供 `/v1/audio/speech` 通道，TTS 建议单独配 `ttsBaseUrl`
- 网易云点歌只能播非 VIP 版本（通常为翻唱版）
- 弹幕抓取依赖 DouyinBarrageGrab 的系统代理机制，抖音协议变更可能需要更新该工具

## 许可与致谢

本项目基于以下开源项目二次开发，请遵守其原始许可：

- **MateEngine**（原项目）：GNU AGPL v3 与 MateProv2 双许可，[Steam 页面](https://store.steampowered.com/app/3625270/MateEngine/)
- **默认模型**：版权归 [Yorshka Shop](https://yorshkasencho.booth.pm/) 所有，禁止在自己的发行版中再分发
- **DouyinBarrageGrab**：弹幕抓取，[ape-byte/DouyinBarrageGrab](https://github.com/ape-byte/DouyinBarrageGrab)
- **Custom Dance Player**：[maoxig/MateEngine-CustomDancePlayer](https://github.com/maoxig/MateEngine-CustomDancePlayer)
- LLMUnity / UniVRM / UniWindowController / NAudio 等依赖见各自目录内许可文件

仅供学习交流。直播内容请遵守平台规范与相关法律法规。
