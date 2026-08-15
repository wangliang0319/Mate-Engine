using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace DouyinLive
{
    // 连接 DouyinBarrageGrab 的 WebSocket 推送端，后台线程收包，
    // 主线程通过 TryDequeue 消费统一 DouyinEvent。
    public class DouyinLiveClient : IDisposable
    {
        public enum State { Stopped, Connecting, Connected, Reconnecting }

        public string Url = "ws://127.0.0.1:8888";
        public volatile bool DebugLog;

        readonly ConcurrentQueue<DouyinEvent> queue = new ConcurrentQueue<DouyinEvent>();
        readonly Queue<long> dedupOrder = new Queue<long>();
        readonly HashSet<long> dedupSet = new HashSet<long>();
        const int DedupWindow = 512;

        CancellationTokenSource cts;
        Task loopTask;
        volatile State state = State.Stopped;
        public State ConnectionState => state;
        public DateTime LastMessageAt { get; private set; }

        public void Start()
        {
            if (loopTask != null && !loopTask.IsCompleted) return;
            cts = new CancellationTokenSource();
            state = State.Connecting;
            loopTask = Task.Run(() => RunLoop(cts.Token));
        }

        public void Stop()
        {
            try { cts?.Cancel(); } catch { }
            state = State.Stopped;
        }

        public void Dispose() => Stop();

        public bool TryDequeue(out DouyinEvent ev) => queue.TryDequeue(out ev);

        async Task RunLoop(CancellationToken ct)
        {
            float backoff = 1f;
            while (!ct.IsCancellationRequested)
            {
                ClientWebSocket ws = null;
                try
                {
                    ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                    state = backoff > 1f ? State.Reconnecting : State.Connecting;
                    using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        connectCts.CancelAfter(TimeSpan.FromSeconds(5));
                        await ws.ConnectAsync(new Uri(Url), connectCts.Token);
                    }
                    state = State.Connected;
                    backoff = 1f;
                    if (DebugLog) Debug.Log("[DouyinLiveClient] Connected " + Url);

                    var buffer = new byte[64 * 1024];
                    var sb = new StringBuilder();
                    while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        if (!result.EndOfMessage) continue;
                        var json = sb.ToString();
                        sb.Length = 0;
                        HandleMessage(json);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (DebugLog) Debug.LogWarning("[DouyinLiveClient] " + ex.Message);
                }
                finally
                {
                    try { ws?.Dispose(); } catch { }
                }

                if (ct.IsCancellationRequested) break;
                state = State.Reconnecting;
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
                catch (OperationCanceledException) { break; }
                backoff = Mathf.Min(backoff * 2f, 30f);
            }
            state = State.Stopped;
        }

        void HandleMessage(string json)
        {
            try
            {
                var env = JsonConvert.DeserializeObject<DouyinEnvelope>(json);
                if (env == null || string.IsNullOrEmpty(env.Data)) return;
                var type = (DouyinMsgType)env.Type;
                if (type == DouyinMsgType.None || type == DouyinMsgType.Stats) return;

                var msg = JsonConvert.DeserializeObject<DouyinMsg>(env.Data);
                var ev = DouyinEvent.From(type, msg);
                if (ev == null) return;

                if (ev.MsgId != 0)
                {
                    lock (dedupSet)
                    {
                        if (!dedupSet.Add(ev.MsgId)) return;
                        dedupOrder.Enqueue(ev.MsgId);
                        while (dedupOrder.Count > DedupWindow)
                            dedupSet.Remove(dedupOrder.Dequeue());
                    }
                }

                LastMessageAt = DateTime.UtcNow;
                queue.Enqueue(ev);
                // 队列积压保护：主线程卡顿时丢弃最老事件
                while (queue.Count > 200 && queue.TryDequeue(out _)) { }
            }
            catch (Exception ex)
            {
                if (DebugLog) Debug.LogWarning("[DouyinLiveClient] Parse failed: " + ex.Message);
            }
        }
    }
}
