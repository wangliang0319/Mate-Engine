using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using CustomDancePlayer;

namespace DouyinLive
{
    public class EffectContext
    {
        public DouyinEvent Event;
        public TriggerRule Rule;
        public bool SingingNow;    // 唱歌时 L2 只放粒子/表情，不播动画
    }

    // 字符串 ID → 具体执行器。用字符串而不是枚举，是为了让「以后往 Animator
    // 里加了新动画只改 json 不改代码」这件事真正成立。
    [RequireComponent(typeof(SpeechPipeline))]
    public class EffectRegistry : MonoBehaviour
    {
        public bool debugLog = false;

        SpeechPipeline speech;
        Animator avatarAnimator;
        AvatarParticleHandler particles;

        string particleThemeBeforeOverride;
        bool particleOverridden;   // 记录“这次是否真的覆盖过”，而不是靠还原值非空判断——
                                   // 用户把粒子关掉时 selectedTheme 本来就是空字符串，
                                   // 空值判断会导致还原被跳过、覆盖值永久卡住
        Coroutine particleRestore;

        // mood: 效果的可见时长。要长到在直播画面里能被观众看清楚，
        // 又不能长到占用表情通道太久、挡住下一次说话触发的情绪。
        const float MoodDurationSeconds = 3f;

        // 正在等待复位的 anim: 脉冲（Animator Bool 设为 true、延时后要设回 false）。
        // 记录发起脉冲时的 Animator 实例而不是当时的 avatarAnimator 字段引用，
        // 这样换角色导致字段被重新指向新 Animator 时，脉冲复位不会写错对象。
        class PendingPulse
        {
            public Animator Anim;
            public string Param;
        }
        readonly List<PendingPulse> pendingPulses = new List<PendingPulse>();

        // 未知/未实现的 ID 只警告一次，避免刷屏
        readonly HashSet<string> warned = new HashSet<string>();

        void Awake() { speech = GetComponent<SpeechPipeline>(); }

        // 组件被禁用/销毁时，Unity 会静默取消所有正在运行的协程（不执行 yield 之后
        // 的代码），Update 之外没有其它地方会同步收尾。Unity 保证 OnDestroy 前一定
        // 先调用 OnDisable，所以这一处收尾就同时覆盖了禁用和销毁两种路径：
        // - 未完成的 anim: 脉冲：把 Animator Bool 设回 false，否则卡在 true（卡姿势）。
        // - 未完成的 particle: 覆盖：立即把主题复位成用户原来选的，否则永久停在覆盖值上。
        void OnDisable()
        {
            foreach (var p in pendingPulses)
                if (p.Anim != null) p.Anim.SetBool(p.Param, false);
            pendingPulses.Clear();

            if (particleRestore != null)
            {
                StopCoroutine(particleRestore);
                particleRestore = null;
                if (particleOverridden && particles != null)
                    particles.SetTheme(particleThemeBeforeOverride);
                particleOverridden = false;
            }
        }

        // 换角色后 Animator 实例会变，每次执行前按需重解析
        void ResolveAvatar()
        {
            if (avatarAnimator != null && avatarAnimator.gameObject.activeInHierarchy) return;
            var loader = FindFirstObjectByType<VRMLoader>();
            var model = loader != null ? loader.GetCurrentModel() : null;
            if (model == null)
            {
                var ctrl = FindFirstObjectByType<AvatarAnimatorController>();
                model = ctrl != null ? ctrl.gameObject : null;
            }
            avatarAnimator = model != null ? model.GetComponentInChildren<Animator>(true) : null;
        }

        AvatarParticleHandler Particles
        {
            get
            {
                if (particles == null) particles = FindFirstObjectByType<AvatarParticleHandler>(FindObjectsInactive.Include);
                return particles;
            }
        }

        // 返回 true 表示确实执行了某个效果；false 表示效果 ID 未实现，或被
        // 唱歌闸门/内部校验拦下什么都没做。TriggerRouter 用这个返回值判断
        // 一条规则是不是「全体效果都是空炮」，空炮要让事件回落到原有逻辑。
        public bool Execute(string effectId, EffectContext ctx)
        {
            if (string.IsNullOrWhiteSpace(effectId) || ctx == null) return false;
            string id = effectId.Trim();
            string arg = "";
            int colon = id.IndexOf(':');
            if (colon >= 0) { arg = id.Substring(colon + 1); id = id.Substring(0, colon); }

            if (debugLog) Debug.Log($"[Effect] {id}:{arg}");

            switch (id)
            {
                case "anim":     return (!ctx.SingingNow || Level(ctx) == 1) && PulseAnim(arg);
                case "face":     return (!ctx.SingingNow || Level(ctx) == 1) && PlayFace(arg);
                case "mood":     return SetMood(arg);
                case "particle": return OverrideParticle(arg);
                case "say":       return Say(FillPlaceholders(arg, ctx.Event));
                case "menu":      return SayMenu();
                case "bigscreen": return TriggerBigScreen();
                case "dance":     return PlayDance(arg);
                case "song":      return PlaySong(arg, ctx);
                case "swapAvatar": return SwapAvatar(ctx);
                case "outfit":    return SwitchOutfit(arg);
                case "sayAI":     return SayAI(arg, ctx);
                default:          WarnOnce(id); return false;
            }
        }

        static int Level(EffectContext ctx) => ctx.Rule != null ? ctx.Rule.LevelOrDefault : 1;

        void WarnOnce(string id)
        {
            if (warned.Add(id))
                Debug.LogWarning($"[Effect] 未知或尚未实现的效果: {id}（该效果被跳过，同规则的其它效果照常执行）");
        }

        // ---------- anim ----------

        bool PulseAnim(string param)
        {
            ResolveAvatar();
            if (avatarAnimator == null || string.IsNullOrEmpty(param)) return false;

            var p = System.Array.Find(avatarAnimator.parameters,
                x => x.name == param && x.type == AnimatorControllerParameterType.Bool);
            if (p == null) { WarnOnce("anim:" + param); return false; }

            // 捕获当前 Animator 实例，而不是让协程读共享字段——见 PendingPulse 上的注释
            var pending = new PendingPulse { Anim = avatarAnimator, Param = param };
            pendingPulses.Add(pending);
            StartCoroutine(PulseBool(pending, 0.4f));
            return true;
        }

        IEnumerator PulseBool(PendingPulse pending, float seconds)
        {
            pending.Anim.SetBool(pending.Param, true);
            yield return new WaitForSeconds(seconds);
            if (pending.Anim != null) pending.Anim.SetBool(pending.Param, false);
            pendingPulses.Remove(pending);
        }

        // ---------- face ----------

        bool PlayFace(string state)
        {
            ResolveAvatar();
            if (avatarAnimator == null || string.IsNullOrEmpty(state)) return false;

            for (int layer = 0; layer < avatarAnimator.layerCount; layer++)
            {
                if (!avatarAnimator.HasState(layer, Animator.StringToHash(state))) continue;
                avatarAnimator.CrossFadeInFixedTime(state, 0.2f, layer);
                return true;
            }
            WarnOnce("face:" + state);
            return false;
        }

        // ---------- mood ----------

        // 不直接写 UniversalBlendshapes：SpeechPipeline.DriveEmotion 每帧都在把
        // 没有 active 表情的字段拉回 0（4/s），直接赋值的效果会在约 0.2s 内被这个
        // 归位吃掉，等于没生效。改为走 SpeechPipeline 自己的外部表情通道，
        // 这样也顺带保证了 mood: 效果和说话时自然带出的表情用同一套强度/映射。
        bool SetMood(string mood)
        {
            if (speech == null) return false;
            if (!speech.SetEmotionExternal(mood, MoodDurationSeconds))
            {
                WarnOnce("mood:" + mood);
                return false;
            }
            return true;
        }

        // ---------- particle ----------

        bool OverrideParticle(string theme)
        {
            var ph = Particles;
            if (ph == null || string.IsNullOrEmpty(theme)) return false;

            // 主题名打错时 SetTheme 不会报错，只是静默无效果 —— 主动检出来
            bool exists = false;
            foreach (var r in ph.rules)
                if (r != null && r.themeTag == theme) { exists = true; break; }
            if (!exists) { WarnOnce("particle:" + theme); return false; }

            if (particleRestore == null) particleThemeBeforeOverride = ph.selectedTheme;
            else StopCoroutine(particleRestore);
            particleOverridden = true;

            ph.SetTheme(theme);
            particleRestore = StartCoroutine(RestoreParticleAfter(6f));
            return true;
        }

        IEnumerator RestoreParticleAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            var ph = Particles;
            if (particleOverridden && ph != null)
                ph.SetTheme(particleThemeBeforeOverride);
            particleOverridden = false;
            particleRestore = null;
        }

        // ---------- say ----------

        bool Say(string text)
        {
            if (speech == null || string.IsNullOrWhiteSpace(text)) return false;
            speech.Enqueue(text, SpeechPipeline.Priority.GiftThanks, 30f);
            return true;
        }

        bool SayMenu()
        {
            return Say("给大家报下玩法哦：发 点歌加歌名 我就唱给你听；发 换角色 我就换一身新形象；" +
                "发 拍头、捋头发、抱抱 都能和我互动；点赞关注我都会感谢，送礼物还能看我跳舞哦~");
        }

        // ---------- bigscreen ----------

        bool TriggerBigScreen()
        {
            var mgr = DouyinLiveManager.Instance;
            if (mgr == null) return false;
            mgr.TriggerBigHeadMoment();
            return true;
        }

        // ---------- dance ----------

        AvatarDanceHandler danceHandler;
        AvatarDanceHandler Dance
        {
            get
            {
                if (danceHandler == null)
                    danceHandler = FindFirstObjectByType<AvatarDanceHandler>(FindObjectsInactive.Include);
                return danceHandler;
            }
        }

        DanceDirector danceDirector;

        bool PlayDance(string arg)
        {
            var d = Dance;
            if (arg == "builtin" || d == null || d.EntryCount <= 0) return PlayBuiltinDance();

            if (arg == "random")
            {
                if (danceDirector == null) danceDirector = FindFirstObjectByType<DanceDirector>();
                if (danceDirector != null && danceDirector.PlayRandom()) return true;
                return PlayBuiltinDance();
            }

            int idx = d.FindIndexByTitleFuzzy(arg);
            if (idx < 0) { Debug.LogWarning($"[Effect] 曲库里没有舞包: {arg}"); return false; }
            if (d.PlayIndex(idx)) return true;
            return PlayBuiltinDance();
        }

        bool PlayBuiltinDance()
        {
            var avatar = FindFirstObjectByType<AvatarAnimatorController>();
            if (avatar == null || avatar.animator == null) return false;
            avatar.isDancing = true;
            avatar.animator.SetBool("isDancing", true);
            return true;
        }

        // ---------- song ----------

        SongService songService;
        SongService Song { get { if (songService == null) songService = GetComponent<SongService>(); return songService; } }

        bool PlaySong(string arg, EffectContext ctx)
        {
            var s = Song;
            if (s == null) { WarnOnce("song"); return false; }
            string name = string.IsNullOrEmpty(ctx.Event?.Nickname) ? "朋友" : ctx.Event.Nickname;

            if (arg != "request") { s.RequestSong(arg, name); return true; }

            // song:request 从弹幕正文里剥掉命中的关键词，剩下的就是歌名
            string title = StripKeywords(ctx.Event?.Content ?? "", ctx.Rule);
            if (string.IsNullOrWhiteSpace(title))
            {
                Say($"{name} 想点什么歌呀？发 点歌加歌名 哦~");
                return true;
            }

            // 曲库里有同名舞包时优先播它：真编舞 + 原曲音频，效果比在线点歌好
            var d = Dance;
            if (d != null)
            {
                int idx = d.FindIndexByTitleFuzzy(title);
                if (idx >= 0 && d.PlayIndex(idx))
                {
                    Say($"好嘞！{name} 点的 {title}，舞蹈版走起！");
                    return true;
                }
            }
            s.RequestSong(title, name);
            return true;
        }

        static string StripKeywords(string content, TriggerRule rule)
        {
            if (rule?.keywords == null) return content.Trim();
            string s = content;
            foreach (var w in rule.keywords)
            {
                if (string.IsNullOrWhiteSpace(w)) continue;
                s = s.Replace(w.Trim(), "");
            }
            return s.Trim();
        }

        // ---------- swapAvatar ----------

        bool SwapAvatar(EffectContext ctx)
        {
            string name = string.IsNullOrEmpty(ctx.Event?.Nickname) ? "朋友" : ctx.Event.Nickname;
            var mgr = DouyinLiveManager.Instance;
            if (mgr == null) { WarnOnce("swapAvatar"); return false; }
            mgr.SwapAvatarFromTrigger(name);
            return true;
        }

        // ---------- outfit ----------

        bool SwitchOutfit(string arg)
        {
            var handlers = AccessoiresHandler.ActiveHandlers;
            if (handlers == null || handlers.Count == 0) { WarnOnce("outfit"); return false; }

            var all = new List<AccessoiresHandler.AccessoryRule>();
            foreach (var h in handlers)
            {
                if (h == null || h.rules == null) continue;
                foreach (var r in h.rules) if (r != null) all.Add(r);
            }
            if (all.Count == 0) { WarnOnce("outfit"); return false; }

            if (arg == "random")
            {
                var pick = all[UnityEngine.Random.Range(0, all.Count)];
                pick.isEnabled = !pick.isEnabled;
                if (debugLog) Debug.Log($"[Effect] outfit 切换 {pick.ruleName} → {pick.isEnabled}");
                return true;
            }

            foreach (var r in all)
                if (r.ruleName == arg) { r.isEnabled = !r.isEnabled; return true; }
            WarnOnce("outfit:" + arg);
            return false;
        }

        // ---------- sayAI ----------

        // 只有礼物 L3 这类低频高价值事件才值得等 1-3 秒换一句定制文案；
        // 高频事件用 AI 会拖慢反馈节奏并烧 token，所以其余一律用固定模板。
        bool SayAI(string prompt, EffectContext ctx)
        {
            string filled = FillPlaceholders(prompt, ctx.Event);
            string fallback = FillPlaceholders(
                string.IsNullOrWhiteSpace(ctx.Rule?.sayFallback)
                    ? "哇！谢谢 {u} 的 {g}，太感谢啦！"
                    : ctx.Rule.sayFallback,
                ctx.Event);

            var mgr = DouyinLiveManager.Instance;
            if (mgr == null) return Say(fallback);

            bool answered = false;
            mgr.GenerateFromTrigger(filled, text =>
            {
                if (answered) return;
                answered = true;
                Say(string.IsNullOrWhiteSpace(text) ? fallback : text);
            });

            // 3 秒还没回来就先说兜底，绝不让大礼物没有反馈
            StartCoroutine(SayFallbackIfSilent(3f, () => answered, () => { answered = true; Say(fallback); }));
            return true;
        }

        IEnumerator SayFallbackIfSilent(float seconds, System.Func<bool> answered, System.Action fallback)
        {
            yield return new WaitForSeconds(seconds);
            if (!answered()) fallback();
        }

        public static string FillPlaceholders(string tpl, DouyinEvent ev)
        {
            if (string.IsNullOrEmpty(tpl) || ev == null) return tpl;
            string name = string.IsNullOrEmpty(ev.Nickname) ? "朋友" : ev.Nickname;
            return tpl.Replace("{u}", name)
                      .Replace("{g}", ev.GiftName ?? "")
                      .Replace("{n}", ev.GiftCount.ToString());
        }
    }
}
