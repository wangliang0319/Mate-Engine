using System;
using System.Collections.Generic;

namespace DouyinLive
{
    // 「点歌」没带歌名时的裁决。放 Core 是为了能单测——真正执行的 EffectRegistry
    // 在 Assembly-CSharp，测试程序集引用不到。
    //
    // 主播语音点歌（「小美小美，唱一首歌」）不带歌名时，反问一句「想听什么歌呀」然后
    // 停下来等，在直播里就是卡住：主播说完就等着，什么也不会发生。这里改成从配置歌单
    // 抽一首直接唱。观众点歌保持追问——那是弹幕里「想听什么歌呀」的互动环节。
    public static class SongRequestFallback
    {
        // 返回 null = 维持原行为（追问开槽）；否则 = 从歌单抽中的歌名。
        // randomIndex 由调用方用 UnityEngine.Random 取，Core 不依赖 UnityEngine。
        public static string PickForStreamer(bool fromVoice, string title, IReadOnlyList<string> songList,
                                             int randomIndex)
        {
            if (!fromVoice) return null;                       // 观众的「点歌」照旧追问
            if (!string.IsNullOrWhiteSpace(title)) return null; // 已经说了歌名，别覆盖

            int count = 0;
            for (int i = 0; i < songList?.Count; i++)
                if (!string.IsNullOrWhiteSpace(songList[i])) count++;
            if (count == 0) return null;                        // 没配歌单：只能追问

            // 取模兼容负数和越界（Random.Range 本身不会越界，但索引的来源将来可能变）
            int want = randomIndex % count;
            if (want < 0) want += count;

            for (int i = 0; i < songList.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(songList[i])) continue;
                if (want-- == 0) return songList[i].Trim();
            }
            return null;
        }
    }
}
