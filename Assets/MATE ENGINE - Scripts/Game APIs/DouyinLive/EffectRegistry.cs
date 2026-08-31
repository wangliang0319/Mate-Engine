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

        // 未知/未实现的 ID 只警告一次，避免刷屏
        readonly HashSet<string> warned = new HashSet<string>();

        void Awake() { speech = GetComponent<SpeechPipeline>(); }

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
            if (string.IsNullOrWhiteSpace(effectId)) return;
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

            StartCoroutine(PulseBool(param, 0.4f));
        }

        IEnumerator PulseBool(string param, float seconds)
        {
            avatarAnimator.SetBool(param, true);
            yield return new WaitForSeconds(seconds);
            if (avatarAnimator != null) avatarAnimator.SetBool(param, false);
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

        // SpeechPipeline 的表情驱动是私有的，这里直接写 UniversalBlendshapes，
        // 并复用它的 0.8 强度约定，两边表现保持一致。
        void SetMood(string mood)
        {
            var bs = FindFirstObjectByType<UniversalBlendshapes>();
            if (bs == null) return;

            switch (mood)
            {
                case "happy":    bs.Joy = 0.8f; break;
                case "love":     bs.Fun = 0.8f; break;
                case "sad":      bs.Sorrow = 0.8f; break;
                case "surprise": bs.Joy = 0.5f; bs.Fun = 0.5f; break;
                default: WarnOnce("mood:" + mood); return;
            }
            // SpeechPipeline 每帧都在 MoveTowards 归位，不需要在这里主动清除
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
