using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace DouyinLive
{
    // 统一“说话”出口：文案带优先级入队 → 切句 → TTS 后台合成（预取）→
    // AudioSource 顺序播放 → RMS 包络驱动口型 → 气泡同步字幕。
    // TTS 不可用时降级为纯气泡模式。
    public class SpeechPipeline : MonoBehaviour
    {
        public enum Priority { GiftThanks = 0, AIReply = 1, Milestone = 2, Welcome = 3, LikeThanks = 4 }

        // 句子流：生产方（如流式LLM）边生成边 Append，说完 Complete；
        // 普通文本播报在内部也会转成一个"已完成"的流，播报路径统一。
        public class SpeechStream
        {
            readonly System.Collections.Concurrent.ConcurrentQueue<string> q =
                new System.Collections.Concurrent.ConcurrentQueue<string>();
            volatile bool completed;
            public bool Completed => completed;
            public void Append(string sentence)
            {
                if (!string.IsNullOrWhiteSpace(sentence)) q.Enqueue(sentence.Trim());
            }
            public void Complete() => completed = true;
            internal bool TryTake(out string s) => q.TryDequeue(out s);
        }

        class SpeechItem
        {
            public string Text;
            public SpeechStream Stream;
            public Priority Prio;
            public float EnqueuedAt;
            public float TTLSeconds;   // 超时未播则丢弃，<=0 不过期
            public long Seq;
        }

        [Header("Output")]
        public AudioSource voiceSource;
        [Range(0f, 1f)] public float volume = 1f;

        [Header("Lip Sync")]
        public float lipSyncGain = 1f;
        public float lipAttack = 25f;   // 张嘴速度
        public float lipRelease = 8f;   // 闭嘴速度

        [Header("Bubble")]
        public Transform chatContainer;
        public bool transparentBubble = true;   // 只显示文字，不渲染气泡背景
        public enum BubbleAnchor { Above, Left, Right }

        public bool followAvatarHead = true;    // 文字跟随角色
        public BubbleAnchor bubbleAnchor = BubbleAnchor.Right;  // 默认在角色右侧
        public float headClearance = 0.05f;     // 头骨上方的世界空间偏移(米)
        public float sideClearance = 0.18f;     // 侧边模式：距头部中线的世界偏移(米)
        public Vector2 followOffset = new Vector2(0f, 0f);  // 额外像素偏移
        public Sprite bubbleSprite;
        public Material bubbleMaterial;
        public Color bubbleColor = new Color32(255, 120, 160, 255);
        public Color fontColor = new Color(1f, 0.96f, 0.75f, 1f);  // 暖黄白，直播画面醒目
        public Font font;
        public int fontSize = 30;
        public int bubbleWidth = 360;
        public bool boldText = true;
        public Color outlineColor = new Color(0.1f, 0.05f, 0.15f, 1f);  // 深紫黑描边
        public float outlineThickness = 2f;
        public float textPadding = 10f;
        public float bubbleSpacing = 10f;
        public float bubbleLinger = 3f;   // 说完后气泡停留

        [Header("Fallback (no TTS)")]
        [Range(5, 100)] public int streamSpeed = 35;
        public float fallbackLinger = 6f;

        [Header("Emotion")]
        public bool emotionFromText = true;     // 按说话内容驱动表情/反应动作
        [Range(0f, 1f)] public float emotionStrength = 0.8f;

        public ITTSProvider Provider;        // 主 TTS
        public ITTSProvider FallbackProvider; // 备选（EdgeTTS）
        public bool TTSEnabled = true;

        readonly List<SpeechItem> pending = new List<SpeechItem>();
        long seqCounter;
        bool speaking;
        Coroutine speakRoutine;

        LLMUnitySamples.Bubble activeBubble;
        Animator avatarAnimator;
        UniversalBlendshapes blendshapes;
        Transform headBone;
        Camera uiCam;
        float lipWeight;
        float[] rmsBuf = new float[256];

        public bool IsSpeaking => speaking;
        public int QueueCount { get { lock (pending) return pending.Count; } }

        void Update()
        {
            RefreshAvatarRefs();
            DriveLipSync();
            DriveEmotion();
            FollowHead();

            if (!speaking)
            {
                var item = DequeueBest();
                if (item != null)
                    speakRoutine = StartCoroutine(SpeakRoutine(item));
            }
        }

        // 弹幕文字锚定在角色头顶上方
        void FollowHead()
        {
            if (!followAvatarHead || activeBubble == null) return;
            if (headBone == null || uiCam == null) return;
            var canvasRT = chatContainer as RectTransform;
            if (canvasRT == null) return;

            float scale = Mathf.Max(0.2f, headBone.lossyScale.magnitude);
            Vector3 anchorWorld;
            Vector2 pivot;
            switch (bubbleAnchor)
            {
                case BubbleAnchor.Left:
                    // 屏幕视角的左侧 = 相机 right 的负方向
                    anchorWorld = headBone.position - uiCam.transform.right * sideClearance * scale;
                    pivot = new Vector2(1f, 0.5f);   // 右边缘贴角色，文字向左伸展
                    break;
                case BubbleAnchor.Right:
                    anchorWorld = headBone.position + uiCam.transform.right * sideClearance * scale;
                    pivot = new Vector2(0f, 0.5f);   // 左边缘贴角色，文字向右伸展
                    break;
                default:
                    anchorWorld = headBone.position + Vector3.up * headClearance * scale;
                    pivot = new Vector2(0.5f, 0f);   // 底边中心，向上增长
                    break;
            }
            Vector3 screen = uiCam.WorldToScreenPoint(anchorWorld);
            if (screen.z <= 0f) return; // 头在相机后方，保持原位

            // 钳制在窗口内（窄窗口/角色贴边时文字不出界）
            float halfW = bubbleWidth * 0.55f;
            float minX = pivot.x >= 1f ? halfW * 2f + 20f : (pivot.x > 0f ? halfW : 20f);
            float maxX = pivot.x <= 0f ? Screen.width - halfW * 2f - 20f : (pivot.x < 1f ? Screen.width - halfW : Screen.width - 20f);
            if (minX < maxX) screen.x = Mathf.Clamp(screen.x, minX, maxX);
            screen.y = Mathf.Clamp(screen.y, 60f, Screen.height - 120f);

            var rt = activeBubble.GetRectTransform();
            // 屏幕坐标 → chatContainer 本地坐标
            Camera camForCanvas = null;
            var canvas = canvasRT.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                camForCanvas = canvas.worldCamera != null ? canvas.worldCamera : uiCam;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRT, (Vector2)screen + followOffset, camForCanvas, out var local))
            {
                rt.pivot = pivot;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = local;
            }
        }

        void RefreshAvatarRefs()
        {
            if (avatarAnimator != null && avatarAnimator.gameObject.activeInHierarchy) return;
            avatarAnimator = null; blendshapes = null;
            var loader = FindFirstObjectByType<VRMLoader>();
            GameObject model = loader != null ? loader.GetCurrentModel() : null;
            if (model == null)
            {
                var parent = GameObject.Find("Model");
                if (parent != null && parent.transform.childCount > 0)
                    model = parent.transform.GetChild(0).gameObject;
            }
            if (model != null)
            {
                avatarAnimator = model.GetComponentInChildren<Animator>(true);
                blendshapes = model.GetComponentInChildren<UniversalBlendshapes>(true);
                if (avatarAnimator != null && avatarAnimator.isHuman)
                    headBone = avatarAnimator.GetBoneTransform(HumanBodyBones.Head);
            }
            if (blendshapes == null)
                blendshapes = FindFirstObjectByType<UniversalBlendshapes>();
            if (uiCam == null) uiCam = Camera.main;
        }

        // ---------- 入队 ----------

        public void Enqueue(string text, Priority prio, float ttlSeconds = 30f)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var stream = new SpeechStream();
            foreach (var s in SplitSentences(text.Trim())) stream.Append(s);
            stream.Complete();
            EnqueueInternal(new SpeechItem
            {
                Text = text.Trim(),
                Stream = stream,
                Prio = prio,
                EnqueuedAt = Time.unscaledTime,
                TTLSeconds = ttlSeconds,
                Seq = ++seqCounter
            });
        }

        // 流式入队：立即返回流句柄，生产方随后 Append/Complete（仅主线程调用）
        public SpeechStream EnqueueStream(Priority prio, float ttlSeconds = 45f)
        {
            var stream = new SpeechStream();
            EnqueueInternal(new SpeechItem
            {
                Text = "",
                Stream = stream,
                Prio = prio,
                EnqueuedAt = Time.unscaledTime,
                TTLSeconds = ttlSeconds,
                Seq = ++seqCounter
            });
            return stream;
        }

        // 已有流对象入队（生产方先建流再入队的场景）
        public void EnqueueStream(SpeechStream stream, Priority prio, float ttlSeconds = 45f)
        {
            if (stream == null) return;
            EnqueueInternal(new SpeechItem
            {
                Text = "",
                Stream = stream,
                Prio = prio,
                EnqueuedAt = Time.unscaledTime,
                TTLSeconds = ttlSeconds,
                Seq = ++seqCounter
            });
        }

        void EnqueueInternal(SpeechItem item)
        {
            lock (pending)
            {
                // 同优先级欢迎/点赞类只保留最新，避免积压
                if (item.Prio == Priority.Welcome || item.Prio == Priority.LikeThanks)
                    pending.RemoveAll(p => p.Prio == item.Prio);
                pending.Add(item);
            }
        }

        public void ClearQueue()
        {
            lock (pending) pending.Clear();
        }

        SpeechItem DequeueBest()
        {
            lock (pending)
            {
                float now = Time.unscaledTime;
                pending.RemoveAll(p => p.TTLSeconds > 0 && now - p.EnqueuedAt > p.TTLSeconds);
                if (pending.Count == 0) return null;
                SpeechItem best = null;
                foreach (var p in pending)
                    if (best == null || p.Prio < best.Prio || (p.Prio == best.Prio && p.Seq < best.Seq))
                        best = p;
                pending.Remove(best);
                return best;
            }
        }

        // ---------- 播报：统一消费句子流（边播当前句、边合成下一句） ----------

        IEnumerator SpeakRoutine(SpeechItem item)
        {
            speaking = true;
            var stream = item.Stream;
            var provider = PickProvider();
            bool useTTS = provider != null && TTSEnabled;

            ShowBubble("");
            var shownText = new StringBuilder();
            bool anyShown = false;
            string prefetchText = null;
            Task<TTSResult> prefetchSynth = null;
            float starve = 0f;

            while (true)
            {
                // 取本句（优先用已预取的）
                string sent;
                Task<TTSResult> synth;
                if (prefetchText != null)
                {
                    sent = prefetchText; synth = prefetchSynth;
                    prefetchText = null; prefetchSynth = null;
                }
                else
                {
                    string s = null;
                    while (!stream.TryTake(out s))
                    {
                        if (stream.Completed) break;
                        starve += Time.deltaTime;
                        if (starve > 20f) break;    // 上游卡死兜底
                        yield return null;
                    }
                    if (s == null) break;           // 流结束
                    starve = 0f;
                    sent = s;
                    synth = useTTS ? SynthesizeSafe(provider, s) : null;
                }

                // 等本句合成；期间下一句一到就开始预取合成（流水线核心）
                if (synth != null)
                {
                    while (!synth.IsCompleted)
                    {
                        if (prefetchText == null && stream.TryTake(out var peek))
                        {
                            prefetchText = peek;
                            prefetchSynth = SynthesizeSafe(provider, peek);
                        }
                        yield return null;
                    }
                }

                shownText.Append(sent);
                if (activeBubble != null) activeBubble.SetText(shownText.ToString());
                anyShown = true;

                TTSResult pcm = synth != null && synth.Status == TaskStatus.RanToCompletion ? synth.Result : null;
                if (pcm != null && pcm.IsValid && voiceSource != null)
                {
                    var clip = AudioClip.Create("tts", pcm.Samples.Length / pcm.Channels, pcm.Channels, pcm.SampleRate, false);
                    clip.SetData(pcm.Samples, 0);
                    voiceSource.clip = clip;
                    voiceSource.volume = volume;
                    voiceSource.Play();
                    if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", true);
                    ApplyEmotionFromText(sent);
                    while (voiceSource != null && voiceSource.isPlaying)
                    {
                        if (prefetchText == null && stream.TryTake(out var peek2))
                        {
                            prefetchText = peek2;
                            prefetchSynth = SynthesizeSafe(provider, peek2);
                        }
                        yield return null;
                    }
                    Destroy(clip);
                }
                else if (!useTTS)
                {
                    // 无TTS降级：字幕逐句停留
                    if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", true);
                    yield return new WaitForSeconds(Mathf.Clamp(sent.Length * 0.12f, 0.6f, 4f));
                }

                // 句间打断检查：有更高优先级排队 → 提前结束剩余句子
                if (HasHigherPriorityWaiting(item.Prio)) break;
            }

            if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", false);
            if (anyShown) yield return new WaitForSeconds(bubbleLinger);
            RemoveBubble();
            speaking = false;
        }

        bool HasHigherPriorityWaiting(Priority current)
        {
            if (current == Priority.GiftThanks) return false;
            lock (pending)
            {
                foreach (var p in pending)
                    if (p.Prio < current) return true;
            }
            return false;
        }

        ITTSProvider PickProvider()
        {
            if (!TTSEnabled) return null;
            if (Provider != null && Provider.IsAvailable) return Provider;
            if (FallbackProvider != null && FallbackProvider.IsAvailable) return FallbackProvider;
            return null;
        }

        async Task<TTSResult> SynthesizeSafe(ITTSProvider provider, string text)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                return await Task.Run(() => provider.SynthesizeAsync(text, cts.Token));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpeechPipeline] TTS failed: " + ex.Message);
                // 主TTS失败时尝试备选一次
                if (provider == Provider && FallbackProvider != null && FallbackProvider.IsAvailable)
                {
                    try
                    {
                        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        return await Task.Run(() => FallbackProvider.SynthesizeAsync(text, cts2.Token));
                    }
                    catch (Exception ex2)
                    {
                        Debug.LogWarning("[SpeechPipeline] Fallback TTS failed: " + ex2.Message);
                    }
                }
                return null;
            }
        }

        // ---------- 内容驱动表情/动作 ----------

        // Joy/Fun/Sorrow/Angry 由 UniversalBlendshapes 双版本(VRM0/1)映射；
        // HoverFaceTrigger 是现有摸脸反应动画，借来当"开心互动"动作。
        enum Emotion { None, Happy, Love, Sad, Surprise }

        Emotion currentEmotion = Emotion.None;
        float emotionUntil;

        static readonly (string[] keys, Emotion emo)[] EmotionRules =
        {
            (new[]{ "谢谢", "感谢", "开心", "太好", "棒", "耶", "哈哈", "笑", "好呀", "欢迎" }, Emotion.Happy),
            (new[]{ "爱你", "抱抱", "么么", "喜欢", "笔芯", "亲亲", "心动", "宝贝" }, Emotion.Love),
            (new[]{ "呜呜", "难过", "伤心", "抱歉", "对不起", "可惜", "找不到" }, Emotion.Sad),
            (new[]{ "哇", "天呐", "厉害", "突破", "太强", "惊", "！！" }, Emotion.Surprise),
        };

        void ApplyEmotionFromText(string sentence)
        {
            if (!emotionFromText || string.IsNullOrEmpty(sentence)) return;
            foreach (var (keys, emo) in EmotionRules)
                foreach (var k in keys)
                    if (sentence.Contains(k))
                    {
                        currentEmotion = emo;
                        emotionUntil = Time.unscaledTime + Mathf.Clamp(sentence.Length * 0.22f, 1.5f, 6f);
                        // 开心/亲昵时触发一次摸脸反应动作（挥手互动感）
                        if ((emo == Emotion.Happy || emo == Emotion.Love) && avatarAnimator != null)
                        {
                            var p = System.Array.Find(avatarAnimator.parameters,
                                x => x.name == "HoverFaceTrigger" && x.type == AnimatorControllerParameterType.Bool);
                            if (p != null) StartCoroutine(PulseBool("HoverFaceTrigger", 0.4f));
                        }
                        return;
                    }
        }

        IEnumerator PulseBool(string param, float seconds)
        {
            if (avatarAnimator == null) yield break;
            avatarAnimator.SetBool(param, true);
            yield return new WaitForSeconds(seconds);
            if (avatarAnimator != null) avatarAnimator.SetBool(param, false);
        }

        void DriveEmotion()
        {
            if (blendshapes == null) return;
            bool active = emotionFromText && Time.unscaledTime < emotionUntil;
            float s = active ? emotionStrength : 0f;
            float speed = 4f * Time.deltaTime;
            blendshapes.Joy = Mathf.MoveTowards(blendshapes.Joy,
                (active && (currentEmotion == Emotion.Happy || currentEmotion == Emotion.Love)) ? s : 0f, speed);
            blendshapes.Fun = Mathf.MoveTowards(blendshapes.Fun,
                (active && currentEmotion == Emotion.Surprise) ? s : 0f, speed);
            blendshapes.Sorrow = Mathf.MoveTowards(blendshapes.Sorrow,
                (active && currentEmotion == Emotion.Sad) ? s * 0.8f : 0f, speed);
        }

        // ---------- 口型 ----------

        void DriveLipSync()
        {
            float target = 0f;
            if (voiceSource != null && voiceSource.isPlaying)
            {
                voiceSource.GetOutputData(rmsBuf, 0);
                float sum = 0f;
                for (int i = 0; i < rmsBuf.Length; i++) sum += rmsBuf[i] * rmsBuf[i];
                float rms = Mathf.Sqrt(sum / rmsBuf.Length);
                target = Mathf.Clamp01(rms * 8f * lipSyncGain);
            }
            float speed = target > lipWeight ? lipAttack : lipRelease;
            lipWeight = Mathf.MoveTowards(lipWeight, target, speed * Time.deltaTime);
            if (blendshapes != null) blendshapes.A = lipWeight;
        }

        // ---------- 气泡 ----------

        void ShowBubble(string text)
        {
            RemoveBubble();
            if (chatContainer == null) return;
            var ui = new LLMUnitySamples.BubbleUI
            {
                sprite = bubbleSprite,
                font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"),
                fontSize = fontSize,
                fontColor = fontColor,
                bubbleColor = bubbleColor,
                bottomPosition = 0,
                leftPosition = 1,
                textPadding = textPadding,
                bubbleOffset = bubbleSpacing,
                bubbleWidth = bubbleWidth,
                bubbleHeight = -1
            };
            activeBubble = new LLMUnitySamples.Bubble(chatContainer, ui, "DouyinSpeechBubble", text);
            var rt = activeBubble.GetRectTransform();
            foreach (var img in rt.GetComponentsInChildren<Image>(true))
            {
                if (transparentBubble)
                {
                    // 透明气泡：隐藏背景图，仅保留文字
                    img.enabled = false;
                    continue;
                }
                if (bubbleMaterial != null) img.material = bubbleMaterial;
                img.pixelsPerUnitMultiplier = 0.25f;
            }
            // 文字样式：加粗 + 双层描边（外粗内细），直播采集画面里清晰醒目
            foreach (var txt in rt.GetComponentsInChildren<Text>(true))
            {
                if (boldText) txt.fontStyle = FontStyle.Bold;
                if (transparentBubble && txt.GetComponent<UnityEngine.UI.Outline>() == null)
                {
                    var o1 = txt.gameObject.AddComponent<UnityEngine.UI.Outline>();
                    o1.effectColor = outlineColor;
                    o1.effectDistance = new Vector2(outlineThickness, -outlineThickness);
                    var o2 = txt.gameObject.AddComponent<UnityEngine.UI.Outline>();
                    o2.effectColor = outlineColor;
                    o2.effectDistance = new Vector2(-outlineThickness, outlineThickness);
                }
            }
        }

        void RemoveBubble()
        {
            if (activeBubble != null) { activeBubble.Destroy(); activeBubble = null; }
        }

        void OnDisable()
        {
            if (speakRoutine != null) { StopCoroutine(speakRoutine); speakRoutine = null; }
            if (voiceSource != null && voiceSource.isPlaying) voiceSource.Stop();
            if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", false);
            if (blendshapes != null)
            {
                blendshapes.A = 0f;
                blendshapes.Joy = 0f; blendshapes.Fun = 0f; blendshapes.Sorrow = 0f;
            }
            emotionUntil = 0f;
            RemoveBubble();
            speaking = false;
        }

        // ---------- 切句 ----------

        static readonly char[] SentenceEnds = { '。', '！', '？', '；', '!', '?', ';', '\n' };

        public static List<string> SplitSentences(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            var sb = new StringBuilder();
            foreach (var c in text)
            {
                sb.Append(c);
                if (Array.IndexOf(SentenceEnds, c) >= 0 && sb.Length >= 6)
                {
                    result.Add(sb.ToString().Trim());
                    sb.Length = 0;
                }
            }
            var rest = sb.ToString().Trim();
            if (rest.Length > 0) result.Add(rest);
            // 过长句再按逗号切
            for (int i = 0; i < result.Count; i++)
            {
                if (result[i].Length > 60)
                {
                    var parts = result[i].Split('，', ',');
                    if (parts.Length > 1)
                    {
                        result.RemoveAt(i);
                        for (int k = parts.Length - 1; k >= 0; k--)
                        {
                            var p = parts[k].Trim();
                            if (p.Length > 0) result.Insert(i, p + (k < parts.Length - 1 ? "，" : ""));
                        }
                    }
                }
            }
            result.RemoveAll(string.IsNullOrWhiteSpace);
            return result;
        }
    }
}
