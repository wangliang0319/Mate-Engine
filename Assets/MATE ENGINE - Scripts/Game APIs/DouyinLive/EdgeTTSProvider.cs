using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DouyinLive
{
    // 微软 Edge 朗读 wss 协议实现（免费，中文音色好）。
    // 每次合成建立一条短连接，输出 mp3 → AudioDecoder 解码。
    public class EdgeTTSProvider : ITTSProvider
    {
        const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
        const string WssBase = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=" + TrustedClientToken;

        // Sec-MS-GEC = SHA256(时间戳取整到5分钟的Windows FileTime + Token) 大写十六进制
        static string BuildUrl()
        {
            long ticks = DateTime.UtcNow.ToFileTimeUtc();
            ticks -= ticks % 3_000_000_000L; // 5分钟窗口（100ns单位）
            string raw = ticks.ToString() + TrustedClientToken;
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(raw));
            var sb = new StringBuilder(64);
            foreach (var b in hash) sb.Append(b.ToString("X2"));
            return WssBase + "&Sec-MS-GEC=" + sb + "&Sec-MS-GEC-Version=1-130.0.2849.68" +
                   "&ConnectionId=" + Guid.NewGuid().ToString("N");
        }

        public string Voice = "zh-CN-XiaoxiaoNeural";
        public string Rate = "+0%";      // 例如 +10%
        public string VolumePct = "+0%";

        public bool IsAvailable => true;
        public string Name => "EdgeTTS(" + Voice + ")";

        public async Task<TTSResult> SynthesizeAsync(string text, CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
            ws.Options.SetRequestHeader("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 Edg/122.0.0.0");

            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(8));
                await ws.ConnectAsync(new Uri(BuildUrl()), connectCts.Token);
            }

            string requestId = Guid.NewGuid().ToString("N");
            string ts = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'");

            string config = "X-Timestamp:" + ts + "\r\n" +
                "Content-Type:application/json; charset=utf-8\r\n" +
                "Path:speech.config\r\n\r\n" +
                "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";
            await SendText(ws, config, ct);

            string ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='zh-CN'>" +
                "<voice name='" + Voice + "'>" +
                "<prosody rate='" + Rate + "' volume='" + VolumePct + "'>" +
                EscapeXml(text) +
                "</prosody></voice></speak>";
            string ssmlMsg = "X-RequestId:" + requestId + "\r\n" +
                "Content-Type:application/ssml+xml\r\n" +
                "X-Timestamp:" + ts + "\r\n" +
                "Path:ssml\r\n\r\n" + ssml;
            await SendText(ws, ssmlMsg, ct);

            var audio = new MemoryStream();
            var buffer = new byte[32 * 1024];
            var msg = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                ct.ThrowIfCancellationRequested();
                msg.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    msg.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;

                var payload = msg.ToArray();
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var s = Encoding.UTF8.GetString(payload);
                    if (s.Contains("Path:turn.end")) break;
                }
                else
                {
                    // 二进制帧：前2字节大端 = 头部长度，音频数据在头部之后
                    if (payload.Length < 2) continue;
                    int headerLen = (payload[0] << 8) | payload[1];
                    int offset = 2 + headerLen;
                    if (offset >= payload.Length) continue;
                    var header = Encoding.UTF8.GetString(payload, 2, headerLen);
                    if (header.Contains("Path:audio"))
                        audio.Write(payload, offset, payload.Length - offset);
                }
            }
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }

            if (audio.Length == 0) throw new Exception("EdgeTTS returned no audio");
            return AudioDecoder.Decode(audio.ToArray());
        }

        static Task SendText(ClientWebSocket ws, string s, CancellationToken ct) =>
            ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(s)), WebSocketMessageType.Text, true, ct);

        static string EscapeXml(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&apos;").Replace("\"", "&quot;");

        public static readonly string[] ChineseVoices = {
            "zh-CN-XiaoxiaoNeural", "zh-CN-XiaoyiNeural", "zh-CN-YunxiNeural",
            "zh-CN-YunyangNeural", "zh-CN-YunjianNeural", "zh-CN-YunxiaNeural",
            "zh-CN-liaoning-XiaobeiNeural", "zh-CN-shaanxi-XiaoniNeural"
        };
    }
}
