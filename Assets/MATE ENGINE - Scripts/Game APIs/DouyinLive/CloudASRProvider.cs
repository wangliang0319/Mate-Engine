using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace DouyinLive
{
    // OpenAI 兼容 POST {baseUrl}/audio/transcriptions（OpenAI / SiliconFlow / Groq 等）。
    // 只在唤醒后调用，费用可忽略；任何失败都返回 null，调用方静默丢弃 ——
    // 语音助手是锦上添花的功能，绝不能因为转写失败打扰直播。
    public class CloudASRProvider
    {
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public string BaseUrl = "";
        public string ApiKey = "";
        public string Model = "FunAudioLLM/SenseVoiceSmall";
        public bool debugLog = false;

        public bool IsAvailable =>
            !string.IsNullOrWhiteSpace(BaseUrl) &&
            !string.IsNullOrWhiteSpace(ApiKey) &&
            !string.IsNullOrWhiteSpace(Model);

        // 返回转写文本；失败/超时/空文本一律 null
        public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
        {
            if (!IsAvailable || wav == null || wav.Length == 0) return null;
            try
            {
                using var form = new MultipartFormDataContent();
                var file = new ByteArrayContent(wav);
                file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(file, "file", "audio.wav");
                form.Add(new StringContent(Model), "model");
                form.Add(new StringContent("zh"), "language");

                using var req = new HttpRequestMessage(HttpMethod.Post,
                    BaseUrl.Trim().TrimEnd('/') + "/audio/transcriptions");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
                req.Content = form;

                using var resp = await http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    if (debugLog) Debug.LogWarning($"[VoiceASR] HTTP {(int)resp.StatusCode}: {Truncate(body)}");
                    return null;
                }
                var text = JsonConvert.DeserializeObject<AsrResponse>(body)?.text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (debugLog) Debug.LogWarning("[VoiceASR] 响应里没有 text: " + Truncate(body));
                    return null;
                }
                return text.Trim();
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                if (debugLog) Debug.LogWarning("[VoiceASR] 转写失败：" + ex.Message);
                return null;
            }
        }

        static string Truncate(string s)
        {
            s = s ?? "";
            return s.Length <= 300 ? s : s.Substring(0, 300);
        }

        class AsrResponse { public string text; }
    }
}
