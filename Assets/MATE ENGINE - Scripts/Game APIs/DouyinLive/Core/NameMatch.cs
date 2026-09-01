using System;
using System.Collections.Generic;

namespace DouyinLive
{
    // 观众打的名字和库里的名字很难完全一致（「初音」对「初音未来 V4X」），
    // 先精确再双向子串。和 AvatarDancePlayer.FindIndexByTitleFuzzy 同一套语义，
    // 但那个在 Unity 层且只服务舞包，这里给角色库用。
    public static class NameMatch
    {
        public static int PickIndex(IReadOnlyList<string> names, string query)
        {
            if (names == null || names.Count == 0 || string.IsNullOrWhiteSpace(query)) return -1;
            string q = query.Trim();

            for (int i = 0; i < names.Count; i++)
            {
                if (string.IsNullOrEmpty(names[i])) continue;
                if (string.Equals(names[i].Trim(), q, StringComparison.OrdinalIgnoreCase)) return i;
            }

            for (int i = 0; i < names.Count; i++)
            {
                if (string.IsNullOrEmpty(names[i])) continue;
                string n = names[i].Trim();
                if (n.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return i;
                if (q.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            }
            return -1;
        }
    }
}
