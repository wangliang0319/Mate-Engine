using System.Collections;
using System.Collections.Generic;
using UnityEngine;

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
                if (particles != null && !string.IsNullOrEmpty(particleThemeBeforeOverride))
                    particles.SetTheme(particleThemeBeforeOverride);
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

        public void Execute(string effectId, EffectContext ctx)
        {
            if (string.IsNullOrWhiteSpace(effectId) || ctx == null) return;
            string id = effectId.Trim();
            string arg = "";
            int colon = id.IndexOf(':');
            if (colon >= 0) { arg = id.Substring(colon + 1); id = id.Substring(0, colon); }

            if (debugLog) Debug.Log($"[Effect] {id}:{arg}");

            switch (id)
            {
                case "anim":     if (!ctx.SingingNow || Level(ctx) == 1) PulseAnim(arg); break;
                case "face":     if (!ctx.SingingNow || Level(ctx) == 1) PlayFace(arg);  break;
                case "mood":     SetMood(arg);            break;
                case "particle": OverrideParticle(arg);   break;
                case "say":      Say(FillPlaceholders(arg, ctx.Event)); break;
                case "menu":     SayMenu();               break;
                default:         WarnOnce(id);            break;
            }
        }

        static int Level(EffectContext ctx) => ctx.Rule != null ? ctx.Rule.LevelOrDefault : 1;

        void WarnOnce(string id)
        {
            if (warned.Add(id))
                Debug.LogWarning($"[Effect] 未知或尚未实现的效果: {id}（该效果被跳过，同规则的其它效果照常执行）");
        }

        // ---------- anim ----------

        void PulseAnim(string param)
        {
            ResolveAvatar();
            if (avatarAnimator == null || string.IsNullOrEmpty(param)) return;

            var p = System.Array.Find(avatarAnimator.parameters,
                x => x.name == param && x.type == AnimatorControllerParameterType.Bool);
            if (p == null) { WarnOnce("anim:" + param); return; }

            // 捕获当前 Animator 实例，而不是让协程读共享字段——见 PendingPulse 上的注释
            var pending = new PendingPulse { Anim = avatarAnimator, Param = param };
            pendingPulses.Add(pending);
            StartCoroutine(PulseBool(pending, 0.4f));
        }

        IEnumerator PulseBool(PendingPulse pending, float seconds)
        {
            pending.Anim.SetBool(pending.Param, true);
            yield return new WaitForSeconds(seconds);
            if (pending.Anim != null) pending.Anim.SetBool(pending.Param, false);
            pendingPulses.Remove(pending);
        }

        // ---------- face ----------

        void PlayFace(string state)
        {
            ResolveAvatar();
            if (avatarAnimator == null || string.IsNullOrEmpty(state)) return;

            for (int layer = 0; layer < avatarAnimator.layerCount; layer++)
            {
                if (!avatarAnimator.HasState(layer, Animator.StringToHash(state))) continue;
                avatarAnimator.CrossFadeInFixedTime(state, 0.2f, layer);
                return;
            }
            WarnOnce("face:" + state);
        }

        // ---------- mood ----------

        // 不直接写 UniversalBlendshapes：SpeechPipeline.DriveEmotion 每帧都在把
        // 没有 active 表情的字段拉回 0（4/s），直接赋值的效果会在约 0.2s 内被这个
        // 归位吃掉，等于没生效。改为走 SpeechPipeline 自己的外部表情通道，
        // 这样也顺带保证了 mood: 效果和说话时自然带出的表情用同一套强度/映射。
        void SetMood(string mood)
        {
            if (speech == null) return;
            if (!speech.SetEmotionExternal(mood, MoodDurationSeconds))
                WarnOnce("mood:" + mood);
        }

        // ---------- particle ----------

        void OverrideParticle(string theme)
        {
            var ph = Particles;
            if (ph == null || string.IsNullOrEmpty(theme)) return;

            // 主题名打错时 SetTheme 不会报错，只是静默无效果 —— 主动检出来
            bool exists = false;
            foreach (var r in ph.rules)
                if (r != null && r.themeTag == theme) { exists = true; break; }
            if (!exists) { WarnOnce("particle:" + theme); return; }

            if (particleRestore == null) particleThemeBeforeOverride = ph.selectedTheme;
            else StopCoroutine(particleRestore);

            ph.SetTheme(theme);
            particleRestore = StartCoroutine(RestoreParticleAfter(6f));
        }

        IEnumerator RestoreParticleAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            var ph = Particles;
            if (ph != null && !string.IsNullOrEmpty(particleThemeBeforeOverride))
                ph.SetTheme(particleThemeBeforeOverride);
            particleRestore = null;
        }

        // ---------- say ----------

        void Say(string text)
        {
            if (speech == null || string.IsNullOrWhiteSpace(text)) return;
            speech.Enqueue(text, SpeechPipeline.Priority.GiftThanks, 30f);
        }

        void SayMenu()
        {
            Say("给大家报下玩法哦：发 点歌加歌名 我就唱给你听；发 换角色 我就换一身新形象；" +
                "发 拍头、捋头发、抱抱 都能和我互动；点赞关注我都会感谢，送礼物还能看我跳舞哦~");
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
