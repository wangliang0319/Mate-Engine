using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace DouyinLive
{
    // 人设卡：主播的完整人格设定，AI回复/暖场共享，存 douyin_persona.json 可自由编辑
    [Serializable]
    public class PersonaCard
    {
        public string name = "小梦";
        public string identity = "20岁的虚拟主播，一只住在桌面上的小精灵";
        public string personality = "活泼开朗、嘴甜爱撒娇，偶尔调皮吐槽，但从不刻薄";
        public string background = "喜欢古风歌曲和跳舞，梦想是成为最会唱歌的桌宠主播";
        public List<string> catchphrases = new List<string> { "宝子们", "啊呀", "嘿嘿" };
        public List<string> taboos = new List<string> { "政治时事", "涉黄内容", "赌博", "引战对骂" };
        public string speakingStyle = "口语化中文短句，像朋友聊天，偶尔用口头禅";

        public string ToPromptSection()
        {
            var sb = new StringBuilder();
            sb.Append("你的名字是").Append(name).Append("，").Append(identity).Append("。");
            sb.Append("性格：").Append(personality).Append("。");
            if (!string.IsNullOrEmpty(background)) sb.Append(background).Append("。");
            if (catchphrases != null && catchphrases.Count > 0)
                sb.Append("你的口头禅：").Append(string.Join("、", catchphrases)).Append("（偶尔自然使用，别句句都带）。");
            if (taboos != null && taboos.Count > 0)
                sb.Append("绝对不聊的话题：").Append(string.Join("、", taboos)).Append("，被问到就俏皮地岔开。");
            sb.Append("说话风格：").Append(speakingStyle).Append("。");
            return sb.ToString();
        }

        static string FilePath => Path.Combine(Application.persistentDataPath, "douyin_persona.json");

        public static PersonaCard LoadOrCreate()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var p = JsonConvert.DeserializeObject<PersonaCard>(File.ReadAllText(FilePath));
                    if (p != null) return p;
                }
            }
            catch (Exception ex) { Debug.LogWarning("[PersonaCard] load failed: " + ex.Message); }

            var def = new PersonaCard();
            try { File.WriteAllText(FilePath, JsonConvert.SerializeObject(def, Formatting.Indented)); }
            catch { }
            return def;
        }
    }

    // 敏感词过滤：AI 输出播出前过一遍（直播合规），词表 douyin_blocked_words.txt 一行一词
    public static class ContentFilter
    {
        static List<string> words;

        static readonly string[] DefaultWords = {
            "政治", "共产党", "国家领导", "习近平", "台独", "港独", "疆独", "六四",
            "法轮", "邪教", "赌博", "博彩", "毒品", "吸毒", "色情", "裸聊", "约炮",
            "自杀", "自残", "加微信", "加QQ", "转账", "刷单", "代练包赢"
        };

        static string FilePath => Path.Combine(Application.persistentDataPath, "douyin_blocked_words.txt");

        public static void Load()
        {
            words = new List<string>();
            try
            {
                if (!File.Exists(FilePath))
                    File.WriteAllLines(FilePath, DefaultWords);
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var w = line.Trim();
                    if (w.Length > 0 && !w.StartsWith("#")) words.Add(w);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ContentFilter] load failed: " + ex.Message);
                words.AddRange(DefaultWords);
            }
        }

        public static bool IsSafe(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            if (words == null) Load();
            foreach (var w in words)
                if (text.Contains(w)) return false;
            return true;
        }
    }
}
