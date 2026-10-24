using System;
using System.Collections.Generic;

namespace DouyinLive
{
    // 洗牌袋：一轮之内不重复，取完自动重洗。
    // 比 rng.Next(count) 的纯随机体验好得多 —— 10 个舞包里连着抽到同一支很常见，
    // 观众会以为主播只会跳这一支。
    public class ShuffleBag
    {
        readonly List<int> bag = new List<int>();
        // 袋子空着时被排除的项，要留到下次 Refill 再跳过；否则 Exclude 等于没做
        readonly HashSet<int> skipNextRefill = new HashSet<int>();
        readonly Random rng;
        int total;
        int lastServed = -1;

        public ShuffleBag(int seed = 0)
        {
            rng = seed == 0 ? new Random() : new Random(seed);
        }

        public int Count => total;

        public void Reset(int count)
        {
            total = Math.Max(0, count);
            bag.Clear();
            skipNextRefill.Clear();
            lastServed = -1;
        }

        public int Next()
        {
            if (total <= 0) return -1;
            if (bag.Count == 0) Refill();
            if (bag.Count == 0) return -1;

            int last = bag.Count - 1;
            int pick = bag[last];
            bag.RemoveAt(last);
            lastServed = pick;
            return pick;
        }

        // 外部已经播过这一项（观众点名点舞），本轮剩下的袋子里就别再发了。
        // 袋子已经空了时没什么可删的——NoteServed 会让重洗避开它。
        public void Exclude(int index)
        {
            bag.RemoveAll(i => i == index);
            if (bag.Count == 0 && total > 0) skipNextRefill.Add(index);
        }

        // 让洗牌袋知道「这一项刚播过」。即使袋子是空的，下一次 Refill 也会把
        // 它从末尾挪走，保证不会紧接着再发一次。
        public void NoteServed(int index)
        {
            lastServed = index;
        }

        void Refill()
        {
            for (int i = 0; i < total; i++)
                if (!skipNextRefill.Contains(i)) bag.Add(i);
            skipNextRefill.Clear();

            // Fisher-Yates
            for (int i = bag.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (bag[i], bag[j]) = (bag[j], bag[i]);
            }

            // Next() 从尾部取，所以尾部就是下一个要发的。跟上一轮最后发出去的
            // 撞上就跟头部换一下，避免观众连着看到同一支舞。只有一个元素时无从换起。
            if (total > 1 && bag[bag.Count - 1] == lastServed)
                (bag[bag.Count - 1], bag[0]) = (bag[0], bag[bag.Count - 1]);
        }
    }
}
