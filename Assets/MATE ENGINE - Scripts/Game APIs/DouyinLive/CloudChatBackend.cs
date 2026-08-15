using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DouyinLive
{
    // OpenAI 兼容 chat/completions 后端（SSE 流式）。
    // 适配 OpenAI / DeepSeek / DashScope兼容模式 / Kimi / 火山方舟 等。
    public class CloudChatBackend : IChatBackend
    {
        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient();
            c.Timeout = TimeSpan.FromSeconds(120); // 单请求超时由 CancellationToken 控制
            return c;
        }

        public string BaseUrl = "";   // 例如 https://api.deepseek.com/v1
        public string ApiKey = "";
        public string Model = "";
        public float Temperature = 0.9f;
        public int MaxTokens = 200;

        public bool IsAvailable =>
            !string.IsNullOrWhiteSpace(BaseUrl) &&
            !string.IsNullOrWhiteSpace(ApiKey) &&
            !string.IsNullOrWhiteSpace(Model);

        public string Name => "Cloud(" + Model + ")";

        static string Normalize(string baseUrl)
        {
            var u = (baseUrl ?? "").Trim().TrimEnd('/');
            return u;
        }

        public async Task<string> ChatAsync(string systemPrompt, IReadOnlyList<ChatMsg> history, string userMsg,
                                            Action<string> onDelta, CancellationToken ct)
        {
            var messages = new List<object>();
            if (!string.IsNullOrEmpty(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });
            if (history != null)
                foreach (var h in history)
                    messages.Add(new { role = h.Role, content = h.Content });
            messages.Add(new { role = "user", content = userMsg });

            var body = JsonConvert.SerializeObject(new
            {
                model = Model,
                messages,
                stream = true,
                temperature = Temperature,
                max_tokens = MaxTokens
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, Normalize(BaseUrl) + "/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                string err = await resp.Content.ReadAsStringAsync();
                throw new Exception($"HTTP {(int)resp.StatusCode}: {Truncate(err, 300)}");
            }

            var full = new StringBuilder();
            using var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data:")) continue;
                var payload = line.Substring(5).Trim();
                if (payload == "[DONE]") break;
                try
                {
                    var jo = JObject.Parse(payload);
                    var delta = jo["choices"]?[0]?["delta"]?["content"]?.ToString();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        full.Append(delta);
                        onDelta?.Invoke(delta);
                    }
                }
                catch { /* 跳过非JSON心跳行 */ }
            }
            return full.ToString();
        }

        // GET {baseUrl}/models —— 供设置页“获取模型列表”按钮使用
        public static async Task<List<string>> FetchModelsAsync(string baseUrl, string apiKey, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Normalize(baseUrl) + "/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"HTTP {(int)resp.StatusCode}: {Truncate(text, 300)}");

            var result = new List<string>();
            var jo = JObject.Parse(text);
            var data = jo["data"] as JArray ?? jo["models"] as JArray;
            if (data != null)
                foreach (var item in data)
                {
                    var id = item["id"]?.ToString() ?? item["name"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) result.Add(id);
                }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        // 从模型列表中推荐一个适合直播弹幕回复的（小/快/chat类优先）
        public static string RecommendModel(List<string> models)
        {
            if (models == null || models.Count == 0) return "";
            string[] preferred = {
                "deepseek-chat", "qwen-turbo", "qwen-flash", "glm-4-flash", "moonshot-v1-8k",
                "doubao-lite", "gpt-4o-mini", "gpt-5-mini", "claude-haiku-4-5", "claude-3-5-haiku",
            };
            foreach (var p in preferred)
                foreach (var m in models)
                    if (m.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return m;
            // 退化：含 chat/instruct/turbo/mini/flash/lite 的第一个
            string[] hints = { "turbo", "flash", "mini", "lite", "chat", "instruct" };
            foreach (var h in hints)
                foreach (var m in models)
                    if (m.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0) return m;
            return models[0];
        }

        static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n);
    }
}
