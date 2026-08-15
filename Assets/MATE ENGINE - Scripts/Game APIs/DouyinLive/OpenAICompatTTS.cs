using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DouyinLive
{
    // OpenAI 兼容 POST {baseUrl}/audio/speech（OpenAI / 硅基流动 / Minimax 等）
    public class OpenAICompatTTS : ITTSProvider
    {
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        public string BaseUrl = "";
        public string ApiKey = "";
        public string Model = "tts-1";
        public string Voice = "alloy";
        public float Speed = 1f;

        public bool IsAvailable =>
            !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);

        public string Name => "OpenAI-Compat TTS";

        public async Task<TTSResult> SynthesizeAsync(string text, CancellationToken ct)
        {
            var body = JsonConvert.SerializeObject(new
            {
                model = Model,
                input = text,
                voice = Voice,
                speed = Speed,
                response_format = "mp3"
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl.Trim().TrimEnd('/') + "/audio/speech");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                string err = await resp.Content.ReadAsStringAsync();
                throw new Exception($"TTS HTTP {(int)resp.StatusCode}: {(err.Length > 200 ? err.Substring(0, 200) : err)}");
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            ct.ThrowIfCancellationRequested();
            return AudioDecoder.Decode(bytes);
        }
    }
}
