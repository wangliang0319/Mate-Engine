using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DouyinLive
{
    public struct ChatMsg
    {
        public string Role;    // "user" | "assistant"
        public string Content;
        public ChatMsg(string role, string content) { Role = role; Content = content; }
    }

    public interface IChatBackend
    {
        // 流式返回增量文本（onDelta 在后台线程回调，调用方负责转主线程）。
        // 返回完整回复文本；失败抛异常。
        Task<string> ChatAsync(string systemPrompt, IReadOnlyList<ChatMsg> history, string userMsg,
                               Action<string> onDelta, CancellationToken ct);
        bool IsAvailable { get; }
        string Name { get; }
    }
}
