using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLMUnity;

namespace DouyinLive
{
    // 本地 LLMUnity 后备。单槽：同一时间只跑一个请求。
    // 注意：不写入用户私聊历史（addToHistory=false），systemPrompt/history 由直播上下文自行拼进 query。
    public class LocalChatBackend : IChatBackend
    {
        public LLMCharacter Character;
        readonly SemaphoreSlim slot = new SemaphoreSlim(1, 1);

        public bool IsAvailable => Character != null;
        public string Name => "Local";

        public async Task<string> ChatAsync(string systemPrompt, IReadOnlyList<ChatMsg> history, string userMsg,
                                            Action<string> onDelta, CancellationToken ct)
        {
            if (Character == null) throw new InvalidOperationException("No local LLMCharacter");
            if (!await slot.WaitAsync(0, ct)) throw new InvalidOperationException("Local LLM busy");
            try
            {
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(systemPrompt)) sb.AppendLine(systemPrompt);
                if (history != null)
                    foreach (var h in history)
                        sb.AppendLine((h.Role == "user" ? "观众: " : "你: ") + h.Content);
                sb.Append("观众: ").Append(userMsg).AppendLine().Append("你: ");

                string last = "";
                var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => { try { Character.CancelRequests(); } catch { } tcs.TrySetCanceled(); }))
                {
                    _ = Character.Chat(sb.ToString(),
                        partial =>
                        {
                            if (partial == null) return;
                            if (partial.Length > last.Length)
                            {
                                onDelta?.Invoke(partial.Substring(last.Length));
                                last = partial;
                            }
                        },
                        () => tcs.TrySetResult(last),
                        addToHistory: false);
                    return await tcs.Task;
                }
            }
            finally { slot.Release(); }
        }
    }
}
