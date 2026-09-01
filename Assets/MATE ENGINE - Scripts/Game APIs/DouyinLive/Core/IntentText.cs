using System;
using System.Text;

namespace DouyinLive
{
    // 弹幕文本的三个纯判断。放在 Core 是为了能进 EditMode 测试，
    // 所以 JSON 也只能手写解析 —— Core 不许引用 Newtonsoft。
    public static class IntentText
    {
        public const int MaxArgLength = 25;        // 超过这个长度的不像歌名/角色名
        public const int MaxPrefilterLength = 30;  // 超过这个长度的是聊天不是命令

        // 词表只决定「这条弹幕值不值得花 1.5 秒问 LLM」，返回的具体类别不参与
        // 最终判定 —— 那是 LLM 的活。所以词表重叠（「换个歌」既含「换个」又含
        // 「歌」）最多让一条弹幕被多问一次，不会造成误触发。
        static readonly string[] DanceWords = { "跳舞", "舞", "扭一个", "来段舞" };
        static readonly string[] AvatarWords = { "换角色", "换个", "变身", "换成", "换一个" };
        static readonly string[] SongWords = { "听", "唱", "歌", "来一首", "来首", "点一首" };

        // 这段文本能不能当歌名/舞名/角色名用。挡掉接在追问后面的无意义弹幕，
        // 让槽位留着等真正的答案。
        public static bool IsUsableArg(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.Length > MaxArgLength) return false;

            // 「哈哈哈哈」「？？？」这类同字重复：是情绪不是答案
            if (s.Length >= 2)
            {
                bool allSame = true;
                for (int i = 1; i < s.Length; i++)
                    if (s[i] != s[0]) { allSame = false; break; }
                if (allSame) return false;
            }

            bool allDigit = true;
            bool hasWord = false;
            foreach (char c in s)
            {
                if (!char.IsDigit(c)) allDigit = false;
                if (char.IsLetterOrDigit(c)) hasWord = true;
            }
            if (allDigit) return false;   // 「666」
            // Emoji 是代理对，char.IsLetterOrDigit 对两半都返回 false，所以
            // 纯表情弹幕会在这里被挡下，不需要单独判 Emoji
            return hasWord;
        }

        public static IntentKind LooksLikeIntent(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return IntentKind.None;
            s = s.Trim();
            if (s.Length > MaxPrefilterLength) return IntentKind.None;

            if (ContainsAny(s, DanceWords)) return IntentKind.Dance;
            if (ContainsAny(s, AvatarWords)) return IntentKind.Avatar;
            if (ContainsAny(s, SongWords)) return IntentKind.Song;
            return IntentKind.None;
        }

        static bool ContainsAny(string s, string[] words)
        {
            foreach (var w in words)
                if (s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // 大模型经常在 JSON 外面套 ```json 围栏或前后加两句解释，所以不做严格解析，
        // 只把 intent / arg 两个键的值挖出来。返回 false = 没解出合法 intent。
        public static bool TryParseIntentJson(string raw, out IntentKind kind, out string arg)
        {
            kind = IntentKind.None;
            arg = "";
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string intent = ExtractValue(raw, "intent");
            if (intent == null) return false;

            switch (intent.Trim().ToLowerInvariant())
            {
                case "song":   kind = IntentKind.Song; break;
                case "dance":  kind = IntentKind.Dance; break;
                case "avatar": kind = IntentKind.Avatar; break;
                case "none":   kind = IntentKind.None; break;
                default: return false;
            }

            string a = ExtractValue(raw, "arg");
            arg = a == null ? "" : a.Trim();
            return true;
        }

        static string ExtractValue(string s, string key)
        {
            int k = IndexOfKey(s, key);
            if (k < 0) return null;
            int colon = s.IndexOf(':', k);
            if (colon < 0) return null;

            int i = colon + 1;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) return null;

            char quote = s[i];
            if (quote != '"' && quote != '\'') return null;
            i++;

            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                if (c == quote) return sb.ToString();
                sb.Append(c);
                i++;
            }
            return null;   // 引号没闭合
        }

        static int IndexOfKey(string s, string key)
        {
            int i = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i >= 0) return i;
            return s.IndexOf("'" + key + "'", StringComparison.Ordinal);
        }
    }
}
