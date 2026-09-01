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

        ActionDirector director;
        readonly TriggerLimiter limiter = new TriggerLimiter();

        long likeTotal;
        FileSystemWatcher watcher;
        volatile bool reloadRequested;
        float reloadAt;                 // debounce：编辑器保存常触发多次事件
        float nextPruneAt;

        // IO 失败大概率是编辑器保存瞬间的文件锁，值得重试；解析失败是确定性的，
        // 重试只会把同一条报错刷三遍，所以只有 IO 失败会走这条重试计数。
        enum ReloadOutcome { Success, Missing, IoError, ParseError }
        int ioRetriesLeft;
        const int MaxIoRetries = 2;

        void Awake()
        {
            director = GetComponent<ActionDirector>();
            if (director == null) director = gameObject.AddComponent<ActionDirector>();
            if (GetComponent<DanceDirector>() == null) gameObject.AddComponent<DanceDirector>();
            limiter.Now = () => Time.unscaledTime;
            Slots.Now = () => Time.unscaledTime;
            Config = TriggerConfigStore.LoadOrCreate();
            StartWatching();
        }

        // 不用 TriggerConfigStore.LoadOrCreate：它解析失败时会直接返回
        // TriggerConfig.Defaults()，热重载路径这样做等于「存盘手误多打个逗号」
        // 就把主播正在用的规则集当场换成默认规则集 —— 直播中途不能接受。
        // 这里自己读文件 + TryParse，失败就原样保留 Config，只报错不替换。
        public void Reload()
        {
            DoReload();
        }

        // 内部版本返回结果，供 Tick 判断要不要重试；对外仍暴露无返回值的 Reload()，
        // 与任务约定的公开接口保持一致。
        ReloadOutcome DoReload()
        {
            string path = TriggerConfigStore.Path;
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[Triggers] {TriggerConfigStore.FileName} 不存在，保留当前配置");
                return ReloadOutcome.Missing;
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Triggers] 读取配置失败，沿用上一份可用配置: {ex.Message}");
                return ReloadOutcome.IoError;
            }

            if (TriggerConfigStore.TryParse(json, out var cfg, out string err))
            {
                Config = cfg;
                Debug.Log($"[Triggers] 已重新加载，共 {Config.rules.Count} 条规则");
                return ReloadOutcome.Success;
            }

            Debug.LogError($"[Triggers] {TriggerConfigStore.FileName} 解析失败，沿用上一份可用配置: {err}");
            return ReloadOutcome.ParseError;
        }

        public void ResetSession()
        {
            likeTotal = 0;
            limiter.Reset();
            director.ResetSession();
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
                    // FileName 是必须的：不少编辑器（vim、VS Code 的原子保存）用「写临时
                    // 文件再改名覆盖目标」的方式存盘，只有 Created/Renamed 能捕捉到，
                    // 少了 NotifyFilters.FileName 这两个事件根本不会触发。
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName
                };
                // 回调在后台线程，只置标志位，真正的重载放到 Tick 里做
                watcher.Changed += (_, __) => reloadRequested = true;
                watcher.Created += (_, __) => reloadRequested = true;
                watcher.Renamed += (_, __) => reloadRequested = true;
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
            {
                reloadAt = Time.unscaledTime + 0.5f;   // debounce 500ms
                ioRetriesLeft = MaxIoRetries;          // 新一轮编辑触发，重置重试计数
            }

            if (reloadAt > 0f && Time.unscaledTime >= reloadAt)
            {
                reloadRequested = false;
                reloadAt = 0f;
                var outcome = DoReload();
                // 只重试 IO 失败（多半是编辑器还占着文件写锁），解析失败是确定性的，
                // 重试没有意义、只会把同一条报错刷三遍
                if (outcome == ReloadOutcome.IoError && ioRetriesLeft > 0)
                {
                    ioRetriesLeft--;
                    reloadAt = Time.unscaledTime + 1f;   // 1 秒后重试
                }
            }

            if (Time.unscaledTime >= nextPruneAt)
            {
                nextPruneAt = Time.unscaledTime + 300f;   // 每 5 分钟清一次
                limiter.PruneUsers(600f);
            }

            director.Tick(Config.global);
        }

        void OnDestroy()
        {
            if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
        }

        // ---------- 路由 ----------

        public IntentSlots Slots { get; } = new IntentSlots();

        public void OpenSlot(DouyinEvent ev, IntentKind kind, string ruleId)
        {
            if (ev == null) return;
            Slots.Open(ev.UserId, ev.Nickname, kind, ruleId);
            if (debugLog) Debug.Log($"[Triggers] 为 {ev.Nickname} 开了 {kind} 追问槽位");
        }

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

            // Commit 必须放在 Submit 之后、且只在 Director 真的消费了这条事件时才做：
            // 如果一条规则的效果全部尚未实现（比如 gift3 全是占位效果），Submit 会
            // 返回 false、事件要回落到原有逻辑，这时候不能先占用冷却名额——
            // 否则下一次本该命中的同类事件会被这次「空炮」的冷却误伞掉，
            // 出现「偶尔安静」的间歇性静音。排队/挤掉队首这两种情况算作消费，
            // 因为请求本身是真实的，只是还没轮到它执行。
            bool executed = director.Submit(rule, ev, Config.global);
            if (executed) limiter.Commit(rule, Config.global, ev.UserId);
            return executed;
        }
    }
}
