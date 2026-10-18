# MateEngine 抖音直播 AI 虚拟主播版

基于开源桌宠项目 [MateEngine](https://store.steampowered.com/app/3625270/MateEngine/) 二次开发的**抖音直播 AI 聊天机器人**：
一只住在桌面上的 VRM 虚拟角色，能自动欢迎观众、AI 语音回复弹幕、点歌唱歌跳舞、答谢礼物——开播即用的 AI 虚拟主播。

![Mate Engine Preview](https://i.imgur.com/5cHHH8c.jpeg)

## 功能总览

| 功能 | 触发方式 | 效果 |
|---|---|---|
| 自动欢迎 | 观众进房/关注/分享 | 语音欢迎，高峰合并播报；老观众/大哥专属欢迎词 |
| AI 弹幕回复 | 普通弹幕 | 大模型生成人设化回复 → TTS 语音 + 口型 + 表情；任何问题都能答（先答题再主播腔）；模型可自己触发表演（答应唱歌就真的唱） |
| 快捷反应 | 666/哈哈/你好/晚安等 | 预制回复秒回，不消耗大模型 |
| 点赞感谢 | 任意点赞 | 点名感谢（15秒冷却合并），里程碑欢呼 |
| 礼物庆祝 | 送礼物 | 按价值三档反应，大礼物欢呼+跳舞+大头特写 |
| 点歌 | 弹幕 `点歌 歌名`，或先发 `点歌` 再发歌名 | 网易云搜歌 → 播放高潮段 → 跟节奏跳舞 |
| 换角色 | 弹幕 `换角色`，或在角色追问后说出角色名 | 切换模型库中的 VRM，自动身高归一化 |
| 两轮点播 | 角色追问后 30 秒内的回答 | 点歌/点舞/换角色都支持「先问再答」，答不上来的弹幕不算数 |
| 玩法菜单 | 弹幕 `菜单` | 口播玩法教学 |
| 自定义触发 | 配置 `douyin_triggers.json` | 任意关键词/点赞数/礼物档位 → 任意效果组合，改完存盘即生效 |
| 冷场暖场 | 90 秒无互动 | 自动求赞/求关注/才艺引导/玩法介绍/闲聊五类轮换；冷场 5 分钟自动唱歌 |
| 直播运营 | 自动 | 每 30 分钟礼物感谢榜、整点报时 |
| 竖屏直播窗口 | 配置自动 | 按 `douyinPortraitAspect` 切竖屏窗口，适配直播伴侣采集 |
| 语音助手 | 主播对麦克风喊「小美小美」 | 像手机语音助手：她先应答「我在～」，再听你说「唱一首黄昏」「换个角色」或直接聊天；她回答后 6 秒内可直接追问；离线唤醒，需开 `douyinVoiceEnabled` |
| 语音传话 | 唤醒后说「告诉大家…」 | 「小美小美，告诉大家八点下播」→ 她把「八点下播」原样播给观众，打游戏时不用自己转头打字 |
| 直播场景 | F9 / 语音「切到游戏陪播」 | AI 独播、游戏陪播、搭档主持三个预设，弹幕处理、暖场、自动表演一键切好；「安静一会儿」让她暂时只答谢礼物 |
| 观众记忆 | 自动 | 记住常客来访次数/礼物总额，回复更有人情味 |

## 快速开始

### 环境准备

1. **Unity 6000.2.6f2**（仅二次开发需要；直接使用打包版可跳过）
2. **弹幕抓取器**：[DouyinBarrageGrab](https://github.com/ape-byte/DouyinBarrageGrab)，管理员运行，默认推送 `ws://127.0.0.1:8888`
3. **大模型 API Key**：任意 OpenAI 兼容服务（DeepSeek / 通义 / 中转站均可）
4. **（可选，用语音助手才需要）麦克风 + 带语音转写接口的服务**：推荐硅基流动，和 TTS 共用一个 key 即可。
   多数大模型中转站没有转写通道，见下文「语音助手」一节

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
| `aiModel` | `"qwen3-30b-a3b-instruct-2507"` | 实测最快（1.2秒）且中文自然；`deepseek-chat` 亦可。**想要更好的知识问答可换更大模型**，答案质量取决于所配模型 |
| `aiFallbackToLocal` | `true` | 云端失败时降级本地 LLMUnity，断网不哑场 |
| `douyinLivePrompt` | `""` | 追加人设（完整人设请用 douyin_persona.json） |

**AI 聊天能力**：不只是陪聊——观众问**任何问题**（百科常识、脑筋急转弯、数学、生活难题）
都会先给准确答案再用主播腔带一句俏皮话，不确定的问题会老实说不确定；说「扣个1」观众发
「1」能接住。模型还会自己触发表演：顺着聊天答应了「那我唱咯」就会真的唱（末尾的
【唱歌】【跳舞:赤伶】标记会被剥掉不念出来，动作经限流闸执行），观众之前点过名字的话
参数直接从对话上下文里带出来——「你会唱晴天吗」「那唱吧」→ 直接唱《晴天》。

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

#### 冷场暖场

| 配置项 | 推荐值 | 说明 |
|---|---|---|
| `douyinIdleChatterEnabled` | `true` | 冷场自动暖场。五类话术轮换且不与上次重复：求赞 / 求关注 / 才艺引导 / **玩法介绍**（把点歌、点舞、换角色、互动词整个报一遍，给新进直播间的观众指路）/ 闲聊 |
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

#### 语音助手（主播用麦克风指挥角色）

像手机语音助手一样用：

```
你：小美小美            （停一下）
她：我在～              （同时弹出气泡「我在听～」）
你：唱一首黄昏
她：开唱 + 跟舞
    …她回答完后 6 秒内，你可以不喊唤醒词直接接着说
```

也可以一口气说完「小美小美唱一首黄昏」，她不应答，直接执行。你说的话和观众发弹幕效果一样：
点歌、点舞、换角色、触发规则、闲聊全部可用；另外支持「告诉大家…」传话（见下文）。

**她会怎么配合你：**

- 她正在说话（念弹幕、回复观众）时被唤醒：立刻停下这句来听你，你说完后剩下的弹幕接着念
- 她正在唱歌跳舞时被唤醒：歌声压到 30%，说完指令后恢复
- 你说话期间，念弹幕、欢迎、感谢一律先暂停，免得她的声音被录进你的指令；会话结束后补上
- 停播、打开菜单时，语音不响应

**最少要改的 4 项配置**（其余保持默认即可；改配置前先关程序，改完重新启动）：

```json
"enableDouyinLive": true,
"douyinVoiceEnabled": true,
"douyinVoiceAsrBaseUrl": "https://api.siliconflow.cn/v1",
"douyinVoiceAsrApiKey": "sk-你的硅基流动key（和 ttsApiKey 用同一个即可）"
```

**确认生效**：启动后日志（打包版在 `AppData/LocalLow/Shinymoon/MateEngineX/Player.log`）里出现
`[Voice] 已启动 麦克风(...) @16000Hz 模式=离线唤醒(sherpa)`。如果显示的是「模式=音量检测」，
说明离线唤醒没加载成功，这行上方会有一条警告写明原因（见下方「排查」）。

**开关与转写**

| 配置项 | 默认值 | 作用与影响 |
|---|---|---|
| `douyinVoiceEnabled` | `false` | 语音助手总开关（还需 `enableDouyinLive` 为 `true`）。开启后麦克风常开，唤醒词在本地离线检测（约占一个 CPU 核的 2%，不上传声音），只有喊出唤醒词之后说的那一句才会上传转写 |
| `douyinVoiceAsrBaseUrl` / `douyinVoiceAsrApiKey` | `""` | 语音转写服务的地址和 key。**留空会复用 `aiBaseUrl` / `aiApiKey`**，但多数大模型中转站没有转写通道，症状是每次唤醒后都没下文（Debug Log 里是 `HTTP 503 ... model_not_found`）。两项都没有可用值时，语音助手不启动，日志给出警告 |
| `douyinVoiceAsrModel` | `"FunAudioLLM/SenseVoiceSmall"` | 转写模型。硅基流动用默认值；用 OpenAI 官方接口时改为 `"whisper-1"` |

**唤醒**

| 配置项 | 默认值 | 作用与影响 |
|---|---|---|
| `douyinVoiceKeywordWake` | `true` | `true`=离线唤醒（推荐：可靠，支持应答、追问、打断、压低歌声）。`false`=音量检测模式：只要有人说话就送去转写，再从文字里找唤醒词；必须一口气说完，没有应答和追问，她说话或唱歌期间不监听，且转写请求明显增多。只建议在离线唤醒加载失败时临时使用 |
| `douyinVoiceWakeKeywords` | `["x iǎo m ěi x iǎo m ěi @小美小美"]` | 离线唤醒词，用带声调的拼音写（写法见下）。可以写多个。**给角色改了名字一定要改这里**，否则喊新名字唤不醒 |
| `douyinVoiceWakeWords` | `["小美小美", "小美"]` | 中文唤醒词：把转写文字里的唤醒词剥掉，剩下的才当指令；音量检测模式也靠它判断有没有被唤醒。改名时和上一项一起改 |
| `douyinVoiceNickname` | `"主播"` | 你的语音指令在 AI 上下文和观众记忆里显示的名字 |

**对话节奏**

| 配置项 | 默认值 | 作用与影响 |
|---|---|---|
| `douyinVoiceAckLines` | `["我在～", "嗯？", "你说～", "在呢"]` | 唤醒后的应答话术。开播时用你配置的 TTS 音色预先合成，唤醒后立即随机播放一句。TTS 没配好时只弹气泡「我在听～」，流程照常 |
| `douyinVoiceAckTimeout` | `5.0` | 应答后多少秒没开口就放弃，安静回到待机（她不会说话）。范围 2~15 |
| `douyinVoiceSilenceSeconds` | `1.2` | 说完后静音多久算一句话结束。说话爱停顿、经常被截断就调大（1.5~2）；嫌她反应慢就调小。范围 0.5~3 |
| `douyinVoiceCommandWindow` | `8.0` | 一句指令最长录几秒，到时间强制结束并转写。范围 2~15 |
| `douyinVoiceFollowUpSeconds` | `6.0` | 她回答完之后，多少秒内可以不喊唤醒词直接接着说。`0`=关闭追问。范围 0~20。她开始唱歌或跳舞时不开追问 |
| `douyinVoiceDuckVolume` | `0.3` | 唱歌时被唤醒，歌声压到原音量的多少（0.3=30%）。`1`=不压低，`0`=暂时静音 |
| `douyinVoicePauseWhilePerforming` | `true` | **已废弃**，只为兼容旧配置文件而保留，改了也不起作用 |

**传话**

| 配置项 | 默认值 | 作用与影响 |
|---|---|---|
| `douyinVoiceRelayEnabled` | `true` | 传话开关。`false`=「告诉大家…」也当普通聊天，由 AI 回答 |
| `douyinVoiceRelayPrefixes` | 告诉大家 / 跟大家说 / 和大家说 / 给大家说 / 通知大家 / 提醒大家 / 转告大家 | 传话开头词。**只认句首**：「我想告诉大家一件事」是在跟她说话，不算传话。同时命中多个时按最长的剥 |
| `douyinVoiceRelayAiFallback` | `false` | 没说开头词时，再问一次大模型「这句是不是要传话」（限时 1.5 秒）。需要响应很快的模型；中转站实测一次要 3~20 秒，开了也基本超时，还会偶尔误判，所以默认关闭 |

**传话怎么用**：「小美小美，告诉大家八点下播」→ 她播出「八点下播」。播的是你的原话：转写出什么就播什么
（表情符号会被去掉），不经过 AI 改写，**也不过敏感词表**，内容由你负责。后半句里带「唱」「歌」也不会被当成点歌。

**拼音唤醒词写法**（`douyinVoiceWakeKeywords`）：

- 每个字拆成「声母 + 带声调的韵母」，用空格隔开；零声母的字只写韵母；最后用 `@` 接显示名，`@` 后面不能有空格
- 示例：`x iǎo m ěi x iǎo m ěi @小美小美`、`x iǎo x uě x iǎo x uě @小雪小雪`、`n ǐ h ǎo x iǎo m ěi @你好小美`
- 写错时（漏了声调、声母韵母没分开），启动日志会指出是哪个音节不对，写错的那条被跳过；全部写错就退回音量检测模式
- **建议用叠词或四个字以上**：「小美小美」比「小美」可靠得多，太短容易被日常说话误触发。叠词还有个好处：她自己念弹幕时几乎不会连说两遍自己的名字，所以在她说话时也能放心监听
- 嫌唤醒太迟钝或太灵敏，可以在 `@` 前加灵敏度参数：`x iǎo m ěi x iǎo m ěi :2.0 #0.15 @小美小美`。
  `:` 后的数字越大越容易唤醒（默认 1.0）；`#` 后的数字越小越容易唤醒，但误唤醒也越多（默认 0.25）。一次只调一个，每次调一点

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

**建议戴耳机**：程序没有回声消除。外放时，她的应答和歌声会被麦克风录进你的指令，导致转写错乱，
或者一句话一直被判定为「还在说」，录满 8 秒才结束。

**排查**：在 Unity 编辑器里，给场景中 `Settings` 对象上的 DouyinLiveManager 组件勾选 Debug Log，
能看到唤醒、转写、每一步状态变化及原因；打包版只记录启动信息和警告。

| 现象 | 日志里看什么 | 怎么处理 |
|---|---|---|
| 完全没反应 | 没有 `[Voice] 已启动` | 检查 `douyinVoiceEnabled`、`enableDouyinLive`；看有没有「ASR 未配置」「没有可用麦克风」警告（Windows 设置 → 隐私 → 麦克风，允许应用访问） |
| 显示「模式=音量检测」 | 上方有「sherpa 离线唤醒不可用 / 加载失败」或「唤醒词配置有误」 | 按警告修正拼音；提示缺文件就重新安装（`MateEngineX_Data/Plugins/x86_64/` 下要有 `onnxruntime.dll` 和 `sherpa-onnx-c-api.dll`，`StreamingAssets/SherpaOnnx/kws/` 下要有 4 个模型文件） |
| 唤醒了，说完没下文 | `[VoiceASR] HTTP 503 ... model_not_found` | 转写服务没有这个模型，把 ASR 指向硅基流动 |
| 她说「没听清～」 | `[Voice] 转写: (失败)`，或转写里只有唤醒词 | 网络波动或没说清楚，重说一遍 |
| 等很久才响应 | `会话 Listening→Processing：录满 8 秒上限` | 背景声太大，一直被当成在说话：戴耳机、调小游戏音量 |
| 追问接不上 | `会话 FollowUp→Idle：追问窗口 6 秒内没开口` | 说得太晚，可调大 `douyinVoiceFollowUpSeconds` |

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
| `F9` | 直播场景循环切换：AI 独播 → 游戏陪播 → 搭档主持 → AI 独播，角色会念一句确认 |

### 离线测试（不开直播）

```bash
python Tools/douyin_mock_server.py        # 模拟弹幕服务器
# 命令：c 弹幕 | e 进房 | f 关注 | l 点赞 | g 礼物 | gg 大礼物
#       song 歌名 | sw 换角色 | auto 自动压测 | q 退出
```

## 开发者文档

- 集成与实现细节：[Docs/DouyinLive-Integration.md](Docs/DouyinLive-Integration.md)（含语音助手自测步骤）
- 系统设计：[Docs/DouyinLive-Design.md](Docs/DouyinLive-Design.md)
- 语音交互设计：[Docs/superpowers/specs/2026-10-18-douyin-voice-v2-design.md](Docs/superpowers/specs/2026-10-18-douyin-voice-v2-design.md)
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
- AI 回复无法发回抖音弹幕：DouyinBarrageGrab 是纯抓取（只收不发）工具。角色的回复气泡
  会被直播伴侣采集进画面，观众看得到
- 语音助手没有回声消除，外放会把她的声音录进指令，建议戴耳机；追问窗口不需要唤醒词，
  背景里清晰的人声（游戏台词、视频）也可能被当成你的追问
- 离线唤醒词要用带声调的拼音配置，不能直接写中文；给角色改名时要同步改
- 语音转写依赖云端服务（只有唤醒后那一句会上传）；离线唤醒组件使安装包增加约 35MB

## 许可与致谢

本项目基于以下开源项目二次开发，请遵守其原始许可：

- **MateEngine**（原项目）：GNU AGPL v3 与 MateProv2 双许可，[Steam 页面](https://store.steampowered.com/app/3625270/MateEngine/)
- **默认模型**：版权归 [Yorshka Shop](https://yorshkasencho.booth.pm/) 所有，禁止在自己的发行版中再分发
- **DouyinBarrageGrab**：弹幕抓取，[ape-byte/DouyinBarrageGrab](https://github.com/ape-byte/DouyinBarrageGrab)
- **Custom Dance Player**：[maoxig/MateEngine-CustomDancePlayer](https://github.com/maoxig/MateEngine-CustomDancePlayer)
- LLMUnity / UniVRM / UniWindowController / NAudio 等依赖见各自目录内许可文件

仅供学习交流。直播内容请遵守平台规范与相关法律法规。
