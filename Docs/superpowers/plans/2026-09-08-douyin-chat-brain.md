# 聊天大脑升级：通用问答 + 动作标记协议

日期：2026-09-08 · 分支：main · 规模：中（4 个代码文件 + 2 个新文件 + 文档）

## 问题与根因

1. **通用问题答不上**：SystemPrompt 只定义了「活泼主播」人设和说话规则，没有「知识渊博、认真答题」的定位，模型用人设搪塞知识问题。
2. **对话前后不连贯**：
   - 「扣个1」→「1」被 `text.Length < 2` 丢弃（DanmakuAIService.OnDanmaku 入口）。
   - 「我只会唱歌跳舞」→「唱吧」：IntentResolver 空历史冷判定，不知道上一轮承诺；
     超时/判不出就掉进闲聊，闲聊模型只会说话不会触发动作。
   - 本质：**会说话的大脑（DanmakuAIService，有 6 轮历史）和会做动作的大脑
     （IntentResolver，冷判定）是两个割裂的系统**，谁也不知道对方说过什么。

## 设计

### A. 动作标记协议（核心）

让**聊天模型**在回复末尾输出标记表达「要表演」，管线剥掉标记再朗读，
同时把动作路由到既有触发层：

- 标记形式：`【唱歌】` `【跳舞】` `【换角色】`，观众点过名就带参数 `【唱歌:赤伶】`。
- SystemPrompt 增加协议说明：「你真的会唱歌、跳舞、换角色。当观众想看你表演、
  或你顺着聊天答应了要表演时，在回复最末尾加标记（点名了就带参数）；
  没有要表演就绝不要加。」
- 带参数的标记让模型能利用**对话历史**补全：「你会唱晴天吗」→「唱吧」→
  `【唱歌:晴天】`——这是冷判定的 IntentResolver 做不到的。
- 动作经 `TriggerRouter.TryHandleIntent` 走既有四道限流闸 + L3 仲裁，
  不带参数落到 `song:ask` 追问开槽，复用两轮点播的全部机制。
  闸门天然防模型滥用标记。
- 标记剥除：`【】` 和标记词不能被念出来。流式路径里标记没有句读边界，
  只会在 flush 或句尾整体出现，单次 EmitOne 内完整；在 EmitOne 的 clean 步骤
  和最终 fullReply 上都过一遍剥除。

### B. Core/ActionMarker.cs（新，纯函数可测）

- `TryExtract(string text, out IntentKind kind, out string arg)`：识别
  `【(唱歌|跳舞|换角色)(:参数)?】`，返回类别+参数；`StripMarkers(string)` 返回
  剥除后的干净文本（供朗读和历史）。
- 放 Core 是为了 EditMode 测试（asmdef 零依赖约束）。
- Tests/ActionMarkerTests.cs：带参/不带参/无标记/多个标记/跨类别参数合法性
  （参数仍要过 IsUsableArg + IsReservedArg，在消费端做）。

### C. DanmakuAIService 改动

1. **SystemPrompt 重写**：
   - 加「你知识渊博，观众问任何问题（百科常识、天文地理、脑筋急转弯、数学、
     生活难题）都要先给准确答案，再用主播口吻带一句俏皮话；不确定就说不确定，
     不要瞎编」。
   - 加动作标记协议（见 A）。
   - 保留人设、敏感话题规避、朗读友好等既有规则。
2. **「1」的接住**：快捷反应表加 `^[1１]+$` 模式（「看到啦看到啦，都在呢~」
   等三条），并把快捷反应检查挪到 `text.Length < 2` 闸门**之前**——
   AI 和冷场暖场（「在的话扣个1让我看到你呀」）的邀请就都能接住了。
   快捷反应自带 20 秒冷却，不怕刷。
3. **标记检测与回调**：`ReplyAsync` 完成后检测 fullReply 中的标记，剥除后的
   文本入历史；发现标记则经 `MainThreadDispatcher.Post` 触发
   `OnActionMarker(ev, kind, arg)` 回调（新公共字段，Manager 接线）。
   流式 EmitOne 中同步剥除，保证标记不被 TTS 念出。
4. `GenerateOneShot`（礼物感谢等）不受影响——那条路没有协议说明，模型不会加标记。

### D. DouyinLiveManager 改动

- ApplySettings 里 `danmakuAI.OnActionMarker = OnChatActionMarker`。
- `OnChatActionMarker(ev, kind, arg)`：`if (!running) return;`（停播守卫，
  和 OnIntentResolved 同款）→ `triggers.TryHandleIntent(ev, kind, arg)` →
  未接住则不回落闲聊（模型已经口头回应过了，再回一句就双重响应）。

### E. 不动的部分

- IntentResolver 保留原样：预筛命中的弹幕仍然先走它（快、省 token）。
  动作标记是它的**兜底**——超时、判 none、或需要历史上下文才能理解的
  弹幕，最终都会流到聊天大脑，由带历史的模型补上。
- 触发层/槽位/限流闸零改动。
- 游戏陪播模式（ChatMode=1）不喂 danmakuAI，天然不涉及。

### F. 文档

- README：AI 弹幕回复一节说明「任何问题都能答」和动作标记；
  顺带注明答案质量取决于所配模型，想要更好的知识问答可换更大模型。
- Docs/DouyinLive-Integration.md：功能总览同步。

## 验证

- 批处理编译 error CS=0（含 lockfile 检查）。
- EditMode 108 + 新增 ActionMarker 测试全绿。
- 手工（mock 服务器）：
  1. `c 地球到月球多远` → 认真回答距离（不再用人设搪塞）
  2. `c 扣个1让我看看` →（AI 回复邀请）→ `c 1` → 「看到啦」快捷回复
  3. `c 你会唱晴天吗` → `c 那唱吧` → 回复末尾带标记 → 直接唱《晴天》
  4. `c 来个节目吧` → 唱歌/跳舞任一 → 无参数 → 追问开槽 → 答歌名接上
  5. 连续多次让模型表演 → 四道闸限流，不会连续唱跳
