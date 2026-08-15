using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace DouyinLive
{
    // 极简主线程派发器：后台线程 Post，主线程（DouyinLiveManager.Update）Drain
    public static class MainThreadDispatcher
    {
        static readonly ConcurrentQueue<Action> actions = new ConcurrentQueue<Action>();

        public static void Post(Action a)
        {
            if (a != null) actions.Enqueue(a);
        }

        public static void Drain()
        {
            while (actions.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception ex) { Debug.LogError("[MainThreadDispatcher] " + ex); }
            }
        }
    }
}
