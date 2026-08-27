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
| 竖屏直播窗口 | F10 | 窗口切 9:16 竖屏，适配直播伴侣采集 |
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

配置文件：`C:\用户\<你>\AppData\LocalLow\Shinymoon\MateEngineX\settings.json`（修改前先关闭程序）

```jsonc
"enableDouyinLive": true,
"aiBaseUrl": "https://api.xxx.com/v1",   // OpenAI 兼容地址
"aiApiKey": "sk-...",
"aiModel": "qwen3-30b-a3b-instruct-2507",
"ttsProvider": 0,                         // 云端 TTS（复用上面的地址和 Key）
"ttsModel": "gpt-4o-mini-tts",
"ttsVoice": "shimmer",
"douyinPortraitWindow": true,             // 竖屏直播窗口
"douyinIdleSongList": ["赤伶", "游山恋"]  // 冷场自动唱歌歌单
```

同目录下的个性化文件（首次运行自动生成）：

| 文件 | 用途 |
|---|---|
| `douyin_persona.json` | 人设卡：名字/性格/背景/口头禅/禁忌话题 |
| `douyin_blocked_words.txt` | 敏感词表，AI 输出含词即静默（直播合规） |
| `douyin_audience.json` | 观众记忆（自动维护） |
| `douyin_gift_rules.json` | 礼物 → 动作规则 |

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
