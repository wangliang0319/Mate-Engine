using System.Collections.Generic;
using UnityEngine;

namespace DouyinLive
{
    // 直播时的动作调度：打开 AvatarAnimatorController 的随机轮换，
    // 并在她闲着的时候偶尔来一个现有的一次性小动作（挥手/摸脸），
    // 免得整场只有循环待机。停播时全部还原，不影响桌面正常使用。
    public class LiveMotionDirector : MonoBehaviour
    {
        [Header("Tuning")]
        public bool enableIdleGestures = true;
        public float gestureMinSeconds = 15f;
        public float gestureMaxSeconds = 25f;
        public float gestureHoldSeconds = 0.6f;

        // 只借现成的姿势参数：Controller 里有挥手和摸脸两种反应
        static readonly string[] GestureCandidates = { "HoverTrigger", "HoverFaceTrigger" };

        readonly List<string> availableGestures = new List<string>();
        AvatarAnimatorController avatar;
        bool live;
        float nextGestureAt;
        string pulsingParam;
        float pulseUntil;

        public bool IsLive => live;

        public void SetLive(bool on)
        {
            if (live == on) return;
            live = on;
            if (on)
            {
                BindAvatar();
                if (avatar != null) avatar.liveMotionVariety = true;
                nextGestureAt = Time.unscaledTime + Random.Range(gestureMinSeconds, gestureMaxSeconds);
            }
            else
            {
                if (avatar != null) avatar.liveMotionVariety = false;
                ReleasePulse();
                avatar = null;
                availableGestures.Clear();
            }
        }

        void OnDisable() => SetLive(false);

        void BindAvatar()
        {
            avatar = FindFirstObjectByType<AvatarAnimatorController>();
            availableGestures.Clear();
            var animator = avatar != null ? avatar.animator : null;
            if (animator == null) return;
            foreach (var p in animator.parameters)
            {
                if (p.type != AnimatorControllerParameterType.Bool) continue;
                foreach (var name in GestureCandidates)
                    if (p.name == name) availableGestures.Add(name);
            }
        }

        void Update()
        {
            if (!live) return;
            // 角色是后加载的（换模型、进场景）：补绑一次
            if (avatar == null)
            {
                BindAvatar();
                if (avatar == null) return;
                avatar.liveMotionVariety = true;
            }

            float now = Time.unscaledTime;

            if (pulsingParam != null)
            {
                if (now >= pulseUntil) ReleasePulse();
                return;
            }
            if (!enableIdleGestures || availableGestures.Count == 0) return;
            if (now < nextGestureAt) return;

            // 忙的时候别插动作：跳舞、被拖动、不在待机状态、菜单挡着
            if (avatar.isDancing || avatar.isDragging || !avatar.IsInIdleState() || MenuActions.IsMovementBlocked())
            {
                nextGestureAt = now + 2f;
                return;
            }

            pulsingParam = availableGestures[Random.Range(0, availableGestures.Count)];
            if (avatar.animator != null) avatar.animator.SetBool(pulsingParam, true);
            pulseUntil = now + gestureHoldSeconds;
            nextGestureAt = now + Random.Range(gestureMinSeconds, gestureMaxSeconds);
        }

        void ReleasePulse()
        {
            if (pulsingParam != null && avatar != null && avatar.animator != null)
                avatar.animator.SetBool(pulsingParam, false);
            pulsingParam = null;
        }
    }
}
