# 抖音直播互动 —— 集成说明

代码位于 `Assets/MATE ENGINE - Scripts/Game APIs/DouyinLive/`（14 个文件）+
`Settings/SettingsMenu/SettingsHandlerDouyinLive.cs` + `SaveLoadHandler.SettingsData` 新增字段。
纯代码集成，不修改现有系统内部逻辑。

## 场景接线（Unity Editor 内需手动完成）

1. **常驻对象**（建议放 SaveLoadHandler 同级）挂两个组件：
   - `SpeechPipeline`：
     - `voiceSource` → 新建一个 AudioSource（Play On Awake 关）
     - `chatContainer` → 复用 AvatarMinecraftMessages 用的气泡容器 Transform
     - `bubbleSprite/bubbleMaterial/font` → 照抄 AvatarMinecraftMessages 的同名配置
   - `DouyinLiveManager`：
     - `localCharacter` → 场景中的 LLMCharacter（可空，空则自动查找）
     - `blockObjects` → 设置菜单等打开时应静默的对象（照抄 Minecraft 桥的 blockObjects）
2. **设置页**：在设置菜单新建 "Douyin Live" 标签页，挂 `SettingsHandlerDouyinLive`，
   按字段名拖入 Toggle/Slider/InputField/Dropdown/Button（全部可选，缺省为 null 时跳过）。
   关键控件：`fetchModelsButton`（获取模型列表并自动选中推荐）、`ttsTestButton`（试听）。

## 运行前提

- 启动 [DouyinBarrageGrab](https://github.com/ape-byte/DouyinBarrageGrab)（默认 `ws://127.0.0.1:8888`），
  再打开抖音直播伴侣或浏览器直播页。
- 设置页打开"启用抖音直播互动"。状态文本显示"已连接"即通路正常。

## 配置说明

| 项 | 说明 |
|---|---|
| API 地址 / Key | OpenAI 兼容端点，如 `https://api.deepseek.com/v1`。Key 明文存 settings.json |
| 模型 | 点击"获取模型列表"自动拉取 `/models` 并推荐（优先 chat/turbo/flash 类小模型） |
| TTS 供应商 | 0=OpenAI兼容云端（`{baseUrl}/audio/speech`，留空则复用 AI 的地址和 Key）；1=EdgeTTS（免费，默认）；3=关闭（纯气泡） |
| 礼物规则 | `persistentDataPath/douyin_gift_rules.json`，首次运行自动生成默认规则 |

礼物规则格式：`{ "giftName": "玫瑰", "minDiamond": 0, "minCount": 1, "action": "randomDance" }`
action 取值：`thanks` / `randomDance` / `builtinDance` / `dance:<舞蹈标题>`。

## 行为一览

- **欢迎**：进房合并播报（冷却 8s，同一观众每场一次）；关注/分享/粉丝团即时致谢。
- **AI 回复**：弹幕过滤（过短/纯表情/重复）→ 云端 LLM 流式 → 失败降级本地 LLMUnity → 语音播报。礼物用户弹幕优先。
- **点赞**：累计阈值致谢 + 1k/5k/1w 里程碑。
- **点舞点歌**：弹幕 `点舞 xxx` / `点歌 xxx`（模糊匹配曲库）；礼物按规则触发随机舞/指定舞。
- **语音优先级**：礼物致谢 > AI 回复 > 关注/里程碑 > 欢迎 > 点赞；低优先级过期自动丢弃；高优先级句间打断。
- **口型**：播放音频 RMS 包络驱动 `UniversalBlendshapes.A`（VRM0/VRM1 通吃），`lipSyncGain` 可调。

## 已知边界

- PackMsgType 编号按 DouyinBarrageGrab 常见版本假定（1弹幕/2点赞/3进房/4关注/5礼物/6统计/7粉丝团/8分享），
  如实测不符，改 `DouyinEvents.cs` 的枚举值即可。
- EdgeTTS 为非官方接口（含 Sec-MS-GEC 签名），失效时切云端 TTS。
- mp3 解码用 NAudio ACM（Windows 自带 MP3 解码器），仅 Windows 可用——与本项目平台一致。
