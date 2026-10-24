namespace DouyinLive
{
    // 直播时的动作轮换裁决。放 Core 是为了能单测——真正写 Animator 参数的
    // AvatarAnimatorController 在 Assembly-CSharp，测试程序集引用不到。
    //
    // 场景里的 Animator 混合树其实有空闲 20 支、舞蹈 13 支，但默认是 0→1→2 顺序
    // 循环，间隔固定，看久了完全可预测。直播中改成随机挑下一支，并且跳过当前这支，
    // 避免"刚换过又换回来"。
    public static class MotionVariety
    {
        // current：当前索引（可能越界，比如场景里改过总数）；count：动作总数；
        // randomValue：调用方用 UnityEngine.Random 取的随机数，Core 不依赖 UnityEngine。
        // 返回 [0, count) 内的索引，且 count > 1 时一定不等于 current。
        public static int NextIndex(int current, int count, int randomValue)
        {
            if (count <= 1) return 0;

            // 先夹到有效范围，再在"其余 count-1 支"里取模：负数也能落进来
            int safeCurrent = current < 0 ? 0 : current >= count ? count - 1 : current;
            int pick = randomValue % (count - 1);
            if (pick < 0) pick += count - 1;

            // 跳过当前支：把 [0, count-1) 映射到 [0, count) 去掉 safeCurrent
            return pick >= safeCurrent ? pick + 1 : pick;
        }
    }
}
