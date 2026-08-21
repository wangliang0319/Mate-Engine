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

        class SpeechItem
        {
            public string Text;
            public Priority Prio;
            public float EnqueuedAt;
            public float TTLSeconds;   // 超时未播则丢弃，<=0 不过期
            public long Seq;
        }

        class SynthSentence
        {
            public string Text;
            public Task<TTSResult> Synth;
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
            var item = new SpeechItem
            {
                Text = text.Trim(),
                Prio = prio,
                EnqueuedAt = Time.unscaledTime,
                TTLSeconds = ttlSeconds,
                Seq = ++seqCounter
            };
            lock (pending)
            {
                // 同优先级欢迎/点赞类只保留最新，避免积压
                if (prio == Priority.Welcome || prio == Priority.LikeThanks)
                    pending.RemoveAll(p => p.Prio == prio);
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

        // ---------- 播报 ----------

        IEnumerator SpeakRoutine(SpeechItem item)
        {
            speaking = true;
            var sentences = SplitSentences(item.Text);
            var provider = PickProvider();

            if (provider == null || !TTSEnabled)
            {
                yield return FallbackBubbleOnly(item.Text);
                speaking = false;
                yield break;
            }

            ShowBubble("");
            // 注意：isTalking 延迟到第一句音频实际开播时再设，
            // 否则 TTS 合成的 1~2 秒里嘴已在动、声音未到（口型超前）。

            // 流水线：预取下一句的合成任务
            var synths = new List<SynthSentence>();
            foreach (var s in sentences)
                synths.Add(new SynthSentence { Text = s });

            const int Prefetch = 2;
            var shownText = new StringBuilder();
            bool anyPlayed = false;

            for (int i = 0; i < synths.Count; i++)
            {
                // 启动本句与预取句
                for (int k = i; k < Mathf.Min(i + Prefetch, synths.Count); k++)
                    if (synths[k].Synth == null)
                        synths[k].Synth = SynthesizeSafe(provider, synths[k].Text);

                var cur = synths[i];
                while (!cur.Synth.IsCompleted) yield return null;

                TTSResult pcm = cur.Synth.Status == TaskStatus.RanToCompletion ? cur.Synth.Result : null;
                if (pcm == null || !pcm.IsValid)
                {
                    // 本句失败：字幕仍显示，跳到下一句
                    shownText.Append(cur.Text);
                    if (activeBubble != null) activeBubble.SetText(shownText.ToString());
                    continue;
                }

                var clip = AudioClip.Create("tts", pcm.Samples.Length / pcm.Channels, pcm.Channels, pcm.SampleRate, false);
                clip.SetData(pcm.Samples, 0);

                shownText.Append(cur.Text);
                if (activeBubble != null) activeBubble.SetText(shownText.ToString());

                if (voiceSource != null)
                {
                    voiceSource.clip = clip;
                    voiceSource.volume = volume;
                    voiceSource.Play();
                    anyPlayed = true;
                    if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", true);
                    ApplyEmotionFromText(cur.Text);
                    while (voiceSource != null && voiceSource.isPlaying)
                    {
                        // P0 打断：句间检查在循环外，句内不打断
                        yield return null;
                    }
                }
                Destroy(clip);

                // 句间打断检查：有更高优先级排队 → 提前结束剩余句子
                if (HasHigherPriorityWaiting(item.Prio)) break;
            }

            if (!anyPlayed && shownText.Length > 0)
                yield return new WaitForSeconds(Mathf.Min(shownText.Length * 0.1f, fallbackLinger));

            if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", false);
            yield return new WaitForSeconds(bubbleLinger);
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

        // ---------- 纯气泡降级 ----------

        IEnumerator FallbackBubbleOnly(string text)
        {
            ShowBubble("");
            if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", true);
            float delay = 1f / Mathf.Max(streamSpeed, 1);
            for (int len = 1; len <= text.Length; len++)
            {
                if (activeBubble == null) break;
                activeBubble.SetText(text.Substring(0, len));
                yield return new WaitForSeconds(delay);
            }
            if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", false);
            yield return new WaitForSeconds(fallbackLinger);
            RemoveBubble();
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
