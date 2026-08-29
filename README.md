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
| 点歌 | 弹幕 `点歌 歌名` | 网易云搜歌 → 播放高潮段 → 跟节奏跳舞 |
| 换角色 | 弹幕 `换角色` | 随机切换模型库中的 VRM，自动身高归一化 |
| 玩法菜单 | 弹幕 `菜单` | 口播玩法教学 |
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
| `ttsBaseUrl` / `ttsApiKey` | `""` | 留空自动复用 aiBaseUrl / aiApiKey |
| `ttsModel` | `"gpt-4o-mini-tts"` | 实测 1.6 秒返回，支持风格指令 |
| `ttsVoice` | `"shimmer"` | 甜美女声；候选 coral / nova / sage |
| `ttsInstructions` | 见下 | **消除中文洋腔的关键**，示例："你是一个20岁左右的中国甜美少女主播，声音清脆软糯、音调偏高，语气活泼带一点撒娇，说标准普通话，绝对不能有外国口音" |
| `ttsVolume` / `ttsSpeed` | `1.0` / `1.0` | 音量 / 语速 |
| `lipSyncGain` | `1.0` | 口型幅度，嘴张不开调大 |

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
| `douyinIdleSongList` | 默认18首古风 | 自动唱歌歌单，直接增删歌名；重复项启动时自动去重；空数组=不自动唱 |

#### 竖屏直播窗口

| 配置项 | 推荐值 | 说明 |
|---|---|---|
| `douyinPortraitAspect` | `0.75` | 窗口宽/高比，**唯一开关**：`>0` 开播即自动切竖屏窗口，`0`（或负数）保持普通桌宠窗口（Unity 会记住上次的窗口尺寸，程序会按窗口大小档位主动改回来）。0.5625=严格9:16；**跳舞走位出画就调大**（0.85~1.0）；配合“角色缩小60~70%+舞台背景”的布局效果最佳 |

#### 个性化数据文件（同目录，首次运行自动生成）

| 文件 | 说明 |
|---|---|
| `douyin_persona.json` | **人设卡**：name(名字)/identity(身份)/personality(性格)/background(背景)/catchphrases(口头禅)/taboos(禁忌话题)/speakingStyle(说话风格)，AI 人格的核心来源 |
| `douyin_blocked_words.txt` | 敏感词表（一行一词，# 开头为注释）。AI 输出含词即静默丢弃，挑衅弹幕不接茬——**直播合规，建议按需补充** |
| `douyin_audience.json` | 观众记忆（自动维护勿手改）：来访次数/礼物总额/最近弹幕 |
| `douyin_gift_rules.json` | 礼物规则：`{"giftName":"玫瑰","minDiamond":0,"minCount":1,"action":"randomDance"}`，action 可选 `thanks`/`randomDance`/`builtinDance`/`dance:舞名` |

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
