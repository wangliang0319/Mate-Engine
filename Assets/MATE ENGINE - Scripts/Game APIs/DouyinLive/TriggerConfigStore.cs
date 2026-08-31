using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace DouyinLive
{
    // douyin_triggers.json 的读写。放在 Assembly-CSharp 而不是 Core：
    // 它需要 Newtonsoft 和 Application.persistentDataPath，而 Core 要保持零外部依赖。
    public static class TriggerConfigStore
    {
        public const string FileName = "douyin_triggers.json";

        public static string Path => System.IO.Path.Combine(Application.persistentDataPath, FileName);

        // 反序列化必须传 Replace：默认的 Auto 会复用字段初始值建好的集合再追加
        // 磁盘内容，本仓库已经因此让 douyinIdleSongList 涨到过 166 条。
        static readonly JsonSerializerSettings LoadSettings = new JsonSerializerSettings
        {
            ObjectCreationHandling = ObjectCreationHandling.Replace
        };

        public static bool TryParse(string json, out TriggerConfig cfg, out string error)
        {
            cfg = null;
            error = null;
            try
            {
                cfg = JsonConvert.DeserializeObject<TriggerConfig>(json, LoadSettings);
                if (cfg == null) { error = "解析结果为空"; return false; }
                if (cfg.global == null) cfg.global = new TriggerGlobal();
                if (cfg.rules == null) cfg.rules = new System.Collections.Generic.List<TriggerRule>();
                Validate(cfg);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // 只警告不拦截：配错一条规则不该让整份配置作废
        static void Validate(TriggerConfig cfg)
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < cfg.rules.Count; i++)
            {
                var r = cfg.rules[i];
                if (r == null) continue;
                if (string.IsNullOrEmpty(r.id)) r.id = $"rule{i}";
                if (!seen.Add(r.id))
                    Debug.LogWarning($"[Triggers] 规则 id 重复: {r.id}（冷却会被这些规则共享）");
                if (r.effects == null || r.effects.Count == 0)
                    Debug.LogWarning($"[Triggers] 规则 {r.id} 没配任何效果，命中后什么都不会发生");
                if (!string.IsNullOrWhiteSpace(r.regex))
                {
                    try { System.Text.RegularExpressions.Regex.IsMatch("", r.regex); }
                    catch (ArgumentException ex)
                    { Debug.LogWarning($"[Triggers] 规则 {r.id} 的正则无效，将永不命中: {ex.Message}"); }
                }
            }
        }

        public static TriggerConfig LoadOrCreate()
        {
            try
            {
                if (File.Exists(Path))
                {
                    if (TryParse(File.ReadAllText(Path), out var cfg, out string err)) return cfg;
                    Debug.LogError($"[Triggers] {FileName} 解析失败，改用默认配置: {err}");
                    return TriggerConfig.Defaults();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Triggers] 读取配置失败: " + ex.Message);
                return TriggerConfig.Defaults();
            }

            WriteDefaultsWithComments();
            return TriggerConfig.Defaults();
        }

        // 首次运行写出的文件带注释，本身就是给用户看的文档 —— README 只需要指路。
        // 用手写头注释 + 序列化正文，避免引入 jsonc 解析依赖（Newtonsoft 读取时会忽略 // 注释）。
        public static void WriteDefaultsWithComments()
        {
            try
            {
                string body = JsonConvert.SerializeObject(TriggerConfig.Defaults(), Formatting.Indented);
                string header =
"// 抖音直播触发规则。改完存盘即生效，不用重启程序。\n" +
"// 解析失败会保留上一份可用配置并在日志里报错，不会让直播间哑掉。\n" +
"//\n" +
"// global 四道限流闸（一个请求要全部通过才执行）：\n" +
"//   chatCooldown/likeCooldown/giftCooldown  该来源的整体节奏\n" +
"//   perUserCooldown  同一观众的间隔，防单人刷屏的主力（只冻结他自己）\n" +
"//   cooldown         写在单条规则里，该玩法自己的节奏\n" +
"//   l2MinInterval / l3MinInterval  跨规则的层级总闸\n" +
"//\n" +
"// source: chat|like|follow|gift|enter|share\n" +
"// level:  L1 轻叠加(不打断唱歌) | L2 普通互动(唱歌时只出粒子) | L3 重磅独占\n" +
"// pick:   all 全部执行 | random 随机选一个\n" +
"//\n" +
"// 可用效果（详见 README）：\n" +
"//   anim:<Animator参数名>   现有参数只有 Headpat / HairStroke /\n" +
"//                           HoverFaceTrigger / HoverTrigger / IntimeRegion\n" +
"//   face:Happy|Angry|Cry|Fear\n" +
"//   mood:happy|love|sad|surprise\n" +
"//   particle:<主题名>       目前只有 \"Dance Trail Blue\" 一个主题\n" +
"//   bigscreen               大头特写\n" +
"//   dance:random | dance:<舞名> | dance:builtin\n" +
"//   song:<歌名> | song:request\n" +
"//   swapAvatar | outfit:random | outfit:<配件名>\n" +
"//   say:<文本> | sayAI:<给AI的提示> | menu\n" +
"//   say 支持占位符 {u}=昵称 {g}=礼物名 {n}=数量\n" +
"//\n" +
"// 规则按数组顺序匹配，第一条命中即停 —— 更具体的规则写在前面。\n";
                File.WriteAllText(Path, header + body);
                Debug.Log($"[Triggers] 已生成默认配置: {Path}");
            }
            catch (Exception ex)
            {
                Debug.LogError("[Triggers] 写默认配置失败: " + ex.Message);
            }
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem("MateEngine/抖音直播/重新生成触发规则文件")]
        static void RegenerateMenu() => WriteDefaultsWithComments();
#endif
    }
}
