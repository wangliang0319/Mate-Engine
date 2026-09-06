# 抖音弹幕双模式：游戏陪播（念弹幕）/ AI 聊天（详细回复）

日期：2026-09-06 · 分支：main · 规模：小（约 5 个文件 + 文档）

## 需求

1. **游戏陪播模式**：直播游戏时，指令弹幕（点歌/换角色/触发规则/槽位/意图兜底）照常工作；
   其余弹幕由角色**带昵称念出来**（「小明说：这游戏怎么玩」），主播人工回复。
2. **AI 聊天模式**（默认）：AI 全程回复弹幕，回复**比现在更详细**（两三句、结尾提问），
   引导观众继续聊。
3. 模式要能**直播中途一键切换**（游戏 ↔ 聊天），不能靠改 settings.json 重启。
4. AI 回复发回抖音弹幕：**不做**（DouyinBarrageGrab 只收不发，发送是作者私有付费服务；
   角色气泡已被直播伴侣采集，观众能看到）。

已确认的口径：念弹幕带昵称；冷场暖场/自动唱歌跳舞**不受模式影响**（仍由各自开关控制）。

## 设计

### 新增 settings 字段（SaveLoadHandler.SettingsData）

- `public int douyinChatMode = 0;`   // 0=AI聊天（默认，详细回复）1=游戏陪播（念弹幕）
- `public float douyinReadInterval = 5f;`  // 念弹幕最小间隔秒数

### 新文件 DanmakuReader.cs（DouyinLive/ 根目录，Assembly-CSharp 层）

念弹幕服务，照 DanmakuAIService 的骨架但更小：

- `OnDanmaku(ev)`：过滤（<2 字、纯 emoji——复用同款正则）、60 秒同文去重、
  队列上限 3（挤掉最旧）、45 秒过期。**不带昵称前缀的快捷回复逻辑——全念。**
- `Tick()`：距上次念不足 `Interval` 秒不发；发一条 `Speech.Enqueue($"{昵称}说：{内容}", Priority.LikeThanks)`。
  用 LikeThanks 优先级：礼物感谢(P0)、指令播报(P1) 都能插到它前面；同优先级只保留最新，
  弹幕洪峰时旧的念读自动被挤掉，天然限流。
- `ResetSession()`。

### DouyinLiveManager 改动

- 持有 `DanmakuReader reader`，ApplySettings 里接 Speech/Interval/mode。
- `HandleChatLegacy`：`reward.TryHandleDanmaku` 之后，游戏模式走 `reader.OnDanmaku`，
  AI 模式走 `danmakuAI.OnDanmaku`（原样）。意图判 none 的回调 `OnIntentGaveUp` 落到
  HandleChatLegacy，自动继承正确行为，不用改。
- `Update` 里加 `reader.Tick()`；StartLive/StopLive 里 `reader.ResetSession()`。
- **快捷键 F9 切换模式**（`public KeyCode chatModeKey = KeyCode.F9;` 可在 Inspector 改）：
  翻转 `douyinChatMode`、存盘、ApplySettings，并让角色念一句确认
  （「切到游戏陪播模式啦，弹幕我来念」/「切回AI聊天模式」）——主播在游戏里不用切出去就能确认切换成功。
  F9 与现有 B/F1/F8 无冲突（已查）。

### DanmakuAIService 改动（AI 模式 = 详细回复）

- `public int ChatMode`，SystemPrompt 按模式二选一：
  - 0（AI 聊天）：把「回复必须精简，一般一句话，最多不超过30个字」换成
    「回复要充实，通常两到三句话，结尾自然地向观众抛一个问题引导对方继续聊；
    不要表情符号/markdown/特殊符号；适合直接朗读」。
  - 1（游戏陪播）：danmakuAI 根本不会被喂弹幕，prompt 不用改第二份。
- `Sanitize` 长度上限参数化：整段回复上限提到 200 字（原来 60），逐句流式的单句上限
  维持 60 不动；`GenerateOneShot`（礼物感谢等 sayAI）维持 60 不动——那些场景短就是对的。
- 快捷反应（666/哈哈）两种模式都不变（游戏模式下压根不走这里）。

### SettingsHandlerDouyinLive.cs

按既有模式补两个控件绑定：`chatModeDropdown`（AI聊天/游戏陪播）、`readIntervalSlider`，
监听 + LoadSettings 双向。抖音设置 UI 仍未接进场景，这是给将来接线留的，行为以 settings.json + F9 为准。

### 文档

- README：新设置字段表行、F9 快捷键行、「已知限制」加一条「AI 回复无法发回抖音弹幕
  （DouyinBarrageGrab 只收不发）」。
- Docs/DouyinLive-Integration.md：settings 样例补两行、功能总览补一行。

### 不做的事

- 不动触发层/槽位/意图兜底（指令路径两种模式等价）。
- 不压制冷场暖场/自动唱歌（用户明确：只改弹幕处理）。
- 不发抖音弹幕（见上）。
- Core/ 零改动 → 无新增 EditMode 测试；行为验证走 mock 服务器手工清单（README 离线测试节补 3 条步骤）。

## 验证

- 批处理编译 error CS=0（含 "another Unity instance"=0 的锁检查）。
- EditMode 108/108 保持全绿（没动 Core）。
- 手工（mock 服务器）：① 默认模式发 `c 今天天气真好` → 角色详细回复且结尾带问句；
  ② 按 F9 → 听到切换确认；③ 发 `c 小明 今天这游戏怎么玩` → 角色念「xx说：今天这游戏怎么玩」不调 LLM；
  ④ 念弹幕期间发 `song 晴天` → 点歌照常、感谢播报插到念读前面。
