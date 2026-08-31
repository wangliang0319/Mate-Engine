using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DouyinLive
{
    // 读 douyin_triggers.json，把事件匹配成效果并执行。
    // 旁路式：TryHandle 返回 false 时，DouyinLiveManager 继续走原有代码路径，
    // 所以删掉配置文件就完全回退到接入本层之前的行为。
    [RequireComponent(typeof(EffectRegistry))]
    public class TriggerRouter : MonoBehaviour
    {
        public bool debugLog = false;

        public TriggerConfig Config { get; private set; }

        EffectRegistry effects;
        SongService song;
        readonly TriggerLimiter limiter = new TriggerLimiter();
        readonly System.Random rng = new System.Random();

        long likeTotal;
        FileSystemWatcher watcher;
        volatile bool reloadRequested;
        float reloadAt;                 // debounce：编辑器保存常触发多次事件
        float nextPruneAt;

        void Awake()
        {
            effects = GetComponent<EffectRegistry>();
            limiter.Now = () => Time.unscaledTime;
            Config = TriggerConfigStore.LoadOrCreate();
            StartWatching();
        }

        // 不用 TriggerConfigStore.LoadOrCreate：它解析失败时会直接返回
        // TriggerConfig.Defaults()，热重载路径这样做等于「存盘手误多打个逗号」
        // 就把主播正在用的规则集当场换成默认规则集 —— 直播中途不能接受。
        // 这里自己读文件 + TryParse，失败就原样保留 Config，只报错不替换。
        public void Reload()
        {
            string path = TriggerConfigStore.Path;
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[Triggers] {TriggerConfigStore.FileName} 不存在，保留当前配置");
                return;
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Triggers] 读取配置失败，沿用上一份可用配置: {ex.Message}");
                return;
            }

            if (TriggerConfigStore.TryParse(json, out var cfg, out string err))
            {
                Config = cfg;
                Debug.Log($"[Triggers] 已重新加载，共 {Config.rules.Count} 条规则");
            }
            else
            {
                Debug.LogError($"[Triggers] {TriggerConfigStore.FileName} 解析失败，沿用上一份可用配置: {err}");
            }
        }

        public void ResetSession()
        {
            likeTotal = 0;
            limiter.Reset();
        }

        // ---------- 热重载 ----------

        void StartWatching()
        {
            try
            {
                string dir = Path.GetDirectoryName(TriggerConfigStore.Path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                watcher = new FileSystemWatcher(dir, TriggerConfigStore.FileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                };
                // 回调在后台线程，只置标志位，真正的重载放到 Tick 里做
                watcher.Changed += (_, __) => reloadRequested = true;
                watcher.Created += (_, __) => reloadRequested = true;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Triggers] 无法监听配置文件变更，改配置后需重启程序: " + ex.Message);
            }
        }

        public void Tick()
        {
            if (reloadRequested && reloadAt <= 0f)
                reloadAt = Time.unscaledTime + 0.5f;   // debounce 500ms

            if (reloadAt > 0f && Time.unscaledTime >= reloadAt)
            {
                reloadRequested = false;
                reloadAt = 0f;
                Reload();
            }

            if (Time.unscaledTime >= nextPruneAt)
            {
                nextPruneAt = Time.unscaledTime + 300f;   // 每 5 分钟清一次
                limiter.PruneUsers(600f);
            }
        }

        void OnDestroy()
        {
            if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
        }

        // ---------- 路由 ----------

        // 返回 true 表示该事件已被触发层消费，调用方不再走原有逻辑
        public bool TryHandle(DouyinEvent ev)
        {
            if (ev == null || Config == null) return false;

            var ctx = new MatchContext { LikeTotalBefore = likeTotal };
            if (ev.Type == DouyinMsgType.Like) likeTotal += Math.Max(1, ev.LikeCount);
            ctx.LikeTotalAfter = likeTotal;

            var rule = TriggerMatcher.Match(ev, Config, ctx);
            if (rule == null) return false;

            var gate = limiter.Check(rule, Config.global, ev.UserId);
            if (gate != GateResult.Pass)
            {
                if (debugLog) Debug.Log($"[Triggers] 规则 {rule.id} 被 {gate} 拦下");
                return true;   // 已匹配但被限流：消费掉，不要再退回去触发 AI 回复
            }

            limiter.Commit(rule, Config.global, ev.UserId);
            Run(rule, ev);
            return true;
        }

        void Run(TriggerRule rule, DouyinEvent ev)
        {
            if (song == null) song = GetComponent<SongService>();

            var ctx = new EffectContext
            {
                Event = ev,
                Rule = rule,
                SingingNow = song != null && song.IsPlaying
            };

            var list = rule.effects;
            if (list == null || list.Count == 0) return;

            if (rule.pick == "random")
            {
                effects.Execute(list[rng.Next(list.Count)], ctx);
                return;
            }
            foreach (var e in list) effects.Execute(e, ctx);
        }
    }
}
