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
        // gpt-4o-mini-tts 风格指令：消除中文“洋腔”的关键
        public string Instructions = "";

        public bool IsAvailable =>
            !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);

        public string Name => "OpenAI-Compat TTS";

        public async Task<TTSResult> SynthesizeAsync(string text, CancellationToken ct)
        {
            // CosyVoice 系列（硅基流动等）和 OpenAI 的请求形状不一样：
            //   1) voice 要写成 "模型名:音色名"，只写音色名会被拒（Invalid voice, code 20047）；
            //   2) 它没有 instructions 字段，风格指令无处可放——CosyVoice 官方文档说可以用
            //      <|endofprompt|> 把指令拼进正文，但硅基流动的部署实测不认这个标记：
            //      同一句 13 字的话，纯正文 1.66 秒，拼了指令变 9.7~24 秒，指令被当成正文念了出来。
            //      所以这里直接丢弃 Instructions，音色只能靠 ttsVoice 选。
            bool cosyVoice = Model != null &&
                             Model.IndexOf("CosyVoice", StringComparison.OrdinalIgnoreCase) >= 0;

            string voice = Voice;
            if (cosyVoice && !string.IsNullOrWhiteSpace(voice) && voice.IndexOf(':') < 0)
                voice = Model + ":" + voice;

            var payload = new System.Collections.Generic.Dictionary<string, object>
            {
                ["model"] = Model,
                ["input"] = text,
                ["voice"] = voice,
                ["speed"] = Speed,
                ["response_format"] = "mp3"
            };
            if (!cosyVoice && !string.IsNullOrWhiteSpace(Instructions)) payload["instructions"] = Instructions;
            var body = JsonConvert.SerializeObject(payload);

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
