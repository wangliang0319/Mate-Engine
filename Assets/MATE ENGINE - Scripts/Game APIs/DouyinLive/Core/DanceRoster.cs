using System;
using System.Collections.Generic;

namespace DouyinLive
{
    // 舞蹈轮换：洗牌的单位是「一支舞」，不是「一个文件」。
    // 491 个包里同名包、左右版、-1/-2 分段不少，按文件洗牌会让观众连着看到
    // 两个几乎一样的动作，一轮里也会把同一支舞放两遍。
    //
    // 另外把「点名播过的那支」也记进来：观众刚点过 sparkle，紧接着的随机
    // 不该又是 sparkle——旧的 ShuffleBag 只知道自己发过什么，不知道外部播过什么。
    public sealed class DanceRoster
    {
        readonly ShuffleBag bag = new ShuffleBag();
        List<string> titles = new List<string>();
        int[] groupOf = Array.Empty<int>();
        List<int>[] members = Array.Empty<List<int>>();

        public int Count => titles.Count;
        public int GroupCount => members.Length;

        // 舞包目录可能在运行时变（用户往 StreamingAssets 里丢新包）。
        // 调用方只在数量变化时重建，避免每次随机都分配 491 个字符串。
        public void Sync(IReadOnlyList<string> titles)
        {
            this.titles = new List<string>(titles?.Count ?? 0);
            for (int i = 0; i < (titles?.Count ?? 0); i++)
                this.titles.Add(titles[i] ?? string.Empty);

            BuildGroups();
            bag.Reset(GroupCount);
        }

        public int Next(int randomValue)
        {
            if (GroupCount == 0) return -1;

            int group = bag.Next();
            if (group < 0 || group >= members.Length) return -1;

            var list = members[group];
            int want = randomValue % list.Count;
            if (want < 0) want += list.Count;
            return list[want];
        }

        // 点名点播走的是另一条路，不经过洗牌袋。告诉它「这支刚播过」，
        // 本轮不再发，并且下一轮重洗时也不会紧挨着再来。
        public void NotePlayed(int index)
        {
            if (index < 0 || index >= groupOf.Length) return;
            int group = groupOf[index];
            bag.Exclude(group);
            bag.NoteServed(group);
        }

        public int Find(string query, int randomValue)
            => DanceTitleMatch.FindBest(titles, query, randomValue);

        void BuildGroups()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            groupOf = new int[titles.Count];
            var lists = new List<List<int>>();

            for (int i = 0; i < titles.Count; i++)
            {
                string key = DanceTitleMatch.VariantKey(titles[i]);
                // 空标题（或全是标点）各自成组，别把无关的包混成一支
                if (key.Length == 0) key = "#" + i;

                if (!map.TryGetValue(key, out int group))
                {
                    group = lists.Count;
                    map[key] = group;
                    lists.Add(new List<int>());
                }
                lists[group].Add(i);
                groupOf[i] = group;
            }

            members = lists.ToArray();
        }
    }
}
