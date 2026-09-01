using System.Collections.Generic;

namespace DouyinLive
{
    // douyin_triggers.json 的数据结构。全部用公开字段 + 默认值，
    // Newtonsoft 不需要任何特性就能正确反序列化，Core 因此保持零外部依赖。
    public class TriggerGlobal
    {
        public float chatCooldown = 0.5f;    // 弹幕来源的全局冷却
        public float likeCooldown = 3f;
        public float giftCooldown = 1.2f;
        public float perUserCooldown = 5f;   // 防单人刷屏的主力闸
        public float l2MinInterval = 3f;
        public float l3MinInterval = 45f;    // 重磅效果之间的最小间隔
        public int l3QueueSize = 3;
        public bool l3InterruptSinging = false;
        public bool giftUseTotalValue = true; // true=单价×数量 false=只看单价
        // 角色问完「想听什么歌呀」之后，等这个观众回答的秒数。<= 0 关闭追问功能。
        public float slotWindowSeconds = 30f;
        // 关键词没命中时，是否花 1.5 秒问一次大模型这条弹幕想干嘛
        public bool intentFallbackEnabled = true;
    }

    public class TriggerRule
    {
        public string id = "";
        public bool enabled = true;
        public string source = "chat";       // chat|like|follow|gift|enter|share

        // 匹配条件（按 source 取用，不适用的字段忽略）
        public List<string> keywords = new List<string>();
        public string regex = "";
        public int everyN = 0;               // like: 每 N 个赞触发一次
        public long milestone = 0;           // like: 累计跨过该值触发一次
        public string giftName = "";
        public int minDiamond = 0;
        public int maxDiamond = 0;           // 0 = 不限上限
        public int minCount = 0;

        // 执行
        public List<string> effects = new List<string>();
        public string pick = "all";          // all=全执行 random=随机选一个
        public string level = "L1";          // L1|L2|L3
        public float cooldown = 0f;          // 本规则独立冷却
        public float perUserCooldown = -1f;  // -1 = 跟随 global
        public string sayFallback = "";      // sayAI: 失败时的兜底文案
        public string askPrompt = "";        // 追问文案，留空用内置默认；支持 {u}

        // 1/2/3。写错或留空一律当 L1，宁可效果轻也不要意外独占画面。
        public int LevelOrDefault
        {
            get
            {
                if (level == "L3") return 3;
                if (level == "L2") return 2;
                return 1;
            }
        }
    }

    public class TriggerConfig
    {
        public int version = 1;
        public TriggerGlobal global = new TriggerGlobal();
        public List<TriggerRule> rules = new List<TriggerRule>();

        public static TriggerConfig Defaults()
        {
            return new TriggerConfig
            {
                version = 1,
                global = new TriggerGlobal(),
                rules = new List<TriggerRule>
                {
                    // ---- 弹幕 L1 ----
                    Chat("pat",  new[] { "拍头", "敲脑袋", "摸头" }, new[] { "anim:Headpat" }, "L1"),
                    Chat("hair", new[] { "捋头发", "顺毛" },         new[] { "anim:HairStroke" }, "L1"),
                    // 占位：捏脸/戳脸/挠痒痒没有专属动画，先复用摸脸反应
                    Chat("face", new[] { "捏脸", "戳脸", "挠痒痒" },
                         new[] { "anim:HoverFaceTrigger", "mood:happy" }, "L1"),

                    // ---- 弹幕 L2（当前全部为占位映射）----
                    Chat("love", new[] { "飞吻", "么么", "抱抱" },
                         new[] { "anim:HoverFaceTrigger", "mood:love", "particle:Dance Trail Blue" }, "L2"),
                    Chat("wave", new[] { "挥手", "你好", "打招呼" }, new[] { "anim:HoverTrigger" }, "L2"),

                    // ---- 弹幕特殊指令 ----
                    Chat("menu", new[] { "菜单", "玩法" }, new[] { "menu" }, "L1"),
                    Chat("song", new[] { "点歌" },         new[] { "song:request" }, "L1"),
                    Cd(Chat("swap", new[] { "换角色", "换装", "换个人" }, new[] { "swapAvatar:request" }, "L3"), 60f, 180f),
                    Cd(Chat("reqdance", new[] { "点舞", "跳舞", "来一段" }, new[] { "dance:request" }, "L3"), 90f, 300f),

                    // ---- 点赞 ----
                    new TriggerRule
                    {
                        id = "like30", source = "like", everyN = 30, level = "L1", pick = "random",
                        effects = new List<string> { "anim:Headpat", "anim:HairStroke", "anim:HoverFaceTrigger" }
                    },
                    new TriggerRule
                    {
                        id = "like3000", source = "like", milestone = 3000, level = "L2",
                        effects = new List<string>
                        {
                            "face:Happy", "particle:Dance Trail Blue",
                            "say:哇！我们已经破三千赞啦，谢谢家人们！"
                        }
                    },

                    // ---- 关注 ----
                    new TriggerRule
                    {
                        id = "follow", source = "follow", level = "L3",
                        effects = new List<string>
                        {
                            "bigscreen", "particle:Dance Trail Blue",
                            "say:感谢 {u} 的关注，欢迎来到直播间！"
                        }
                    },

                    // ---- 礼物三档（抖币阈值按实际直播间调）----
                    new TriggerRule
                    {
                        id = "gift1", source = "gift", maxDiamond = 9, level = "L1", pick = "random",
                        effects = new List<string> { "anim:Headpat", "anim:HairStroke" }
                    },
                    new TriggerRule
                    {
                        id = "gift2", source = "gift", minDiamond = 10, maxDiamond = 99, level = "L2",
                        effects = new List<string> { "face:Happy", "particle:Dance Trail Blue" }
                    },
                    new TriggerRule
                    {
                        id = "gift3", source = "gift", minDiamond = 100, level = "L3",
                        sayFallback = "哇！！{u} 送出了超级大礼 {g}！！谢谢老板，这支舞献给你！",
                        effects = new List<string>
                        {
                            "bigscreen", "dance:random",
                            "sayAI:观众{u}送了{g}，用一句话热情感谢并说要跳舞回报"
                        }
                    },
                }
            };
        }

        static TriggerRule Chat(string id, string[] words, string[] effects, string level)
        {
            return new TriggerRule
            {
                id = id, source = "chat", level = level,
                keywords = new List<string>(words),
                effects = new List<string>(effects)
            };
        }

        static TriggerRule Cd(TriggerRule r, float cooldown, float perUser)
        {
            r.cooldown = cooldown;
            r.perUserCooldown = perUser;
            return r;
        }
    }
}
