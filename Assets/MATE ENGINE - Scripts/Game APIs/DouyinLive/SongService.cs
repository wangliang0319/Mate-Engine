using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DouyinLive
{
    // 真·点歌：网易云公开接口搜索 → 探测可播放版本（VIP歌自动跳到翻唱版）→
    // 下载 mp3 → NAudio 解码 → AudioSource 播放，同时让角色进入跳舞状态跟着音乐跳。
    public class SongService : MonoBehaviour
    {
        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            // 绕开系统代理（DouyinBarrageGrab 会设系统代理）
            var handler = new HttpClientHandler { UseProxy = false };
            var c = new HttpClient(handler);
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            return c;
        }

        [Header("Playback")]
        public AudioSource musicSource;
        [Range(0f, 1f)] public float musicVolume = 0.85f;
        public bool chorusOnly = true;      // 只播高潮段
        public int chorusSeconds = 60;      // 高潮段目标时长
        public int maxSongSeconds = 300;   // 单曲最长播放时长（防超长占播）

        [Header("Rhythm")]
        public bool rhythmDance = true;     // 按节拍切舞步/调速
        public int beatsPerMove = 8;        // 每N拍换一个舞步
        public float minSpeed = 0.75f, maxSpeed = 1.35f;

        public SpeechPipeline Speech;

        Coroutine playRoutine;
        string nowPlaying;
        Animator cachedAnimator;
        AvatarAnimatorController cachedAvatar;

        public bool IsPlaying => playRoutine != null;
        public string NowPlaying => nowPlaying;

        // ---------- 入口 ----------

        public void RequestSong(string keyword, string userName)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return;
            if (playRoutine != null)
            {
                Speech?.Enqueue($"正在播放 {nowPlaying}，稍后再点哦~", SpeechPipeline.Priority.AIReply, 20f);
                return;
            }
            playRoutine = StartCoroutine(SearchAndPlay(keyword.Trim(), userName));
        }

        public void StopSong()
        {
            if (playRoutine != null) { StopCoroutine(playRoutine); playRoutine = null; }
            if (musicSource != null) musicSource.Stop();
            SetDancing(false);
            ReleaseNativeDance();
            nowPlaying = null;
        }

        // ---------- 主流程 ----------

        IEnumerator SearchAndPlay(string keyword, string userName)
        {
            Speech?.Enqueue($"收到 {userName} 点的 {keyword}，我找找哈~", SpeechPipeline.Priority.GiftThanks, 30f);

            // 1) 搜索 + 探测可播放版本（后台线程）
            var findTask = FindPlayableSong(keyword);
            while (!findTask.IsCompleted) yield return null;

            var song = findTask.Status == TaskStatus.RanToCompletion ? findTask.Result : null;
            if (song == null)
            {
                Speech?.Enqueue($"呜呜，{keyword} 这首歌找不到能播的版本，换一首试试嘛~",
                    SpeechPipeline.Priority.AIReply, 30f);
                playRoutine = null;
                yield break;
            }

            // 2) 下载 + 解码（后台线程）
            var decodeTask = DownloadAndDecode(song.Url);
            while (!decodeTask.IsCompleted) yield return null;

            var pcm = decodeTask.Status == TaskStatus.RanToCompletion ? decodeTask.Result : null;
            if (pcm == null || !pcm.IsValid)
            {
                Speech?.Enqueue("这首歌下载失败了，再点一次或换一首吧~", SpeechPipeline.Priority.AIReply, 30f);
                playRoutine = null;
                yield break;
            }

            // 只播高潮段：基于能量包络找最"燃"的一段
            if (chorusOnly && pcm.Samples.Length / pcm.Channels > pcm.SampleRate * (chorusSeconds * 3 / 2))
            {
                pcm = ExtractChorus(pcm, chorusSeconds);
            }

            // 3) 播放 + 跳舞
            var clip = AudioClip.Create("song", pcm.Samples.Length / pcm.Channels, pcm.Channels, pcm.SampleRate, false);
            clip.SetData(pcm.Samples, 0);

            nowPlaying = $"{song.Name} - {song.Artist}";
            Speech?.Enqueue($"找到啦！{nowPlaying}，一起摇起来~", SpeechPipeline.Priority.GiftThanks, 30f);

            // 等报幕说完再起歌，避免和语音撞车
            float wait = 0f;
            while (Speech != null && Speech.IsSpeaking && wait < 8f) { wait += Time.deltaTime; yield return null; }

            // 节拍分析（后台线程，很快）
            float bpm = 0f;
            if (rhythmDance)
            {
                var bpmTask = Task.Run(() => EstimateBPM(pcm));
                while (!bpmTask.IsCompleted) yield return null;
                bpm = bpmTask.Status == TaskStatus.RanToCompletion ? bpmTask.Result : 0f;
                Debug.Log($"[SongService] BPM ≈ {bpm:F0}");
            }

            if (musicSource == null) musicSource = gameObject.AddComponent<AudioSource>();
            musicSource.clip = clip;
            musicSource.volume = musicVolume;
            musicSource.loop = false;
            musicSource.Play();

            // 原生跟舞：把本进程临时加入 allowedApps 并放宽声音阈值，
            // 让 AvatarAnimatorController 的原生声音检测自己发现"音乐在放"，
            // 走原生 StartDancing / danceTimer / SmoothDanceTransition 全流程。
            EngageNativeDance();
            if (rhythmDance && bpm > 0 && cachedAvatar != null)
            {
                // 舞步轮换周期对齐到 N 拍、过渡2拍，动画速度贴 BPM
                float beat = 60f / bpm;
                cachedAvatar.DANCE_SWITCH_TIME = Mathf.Clamp(beat * beatsPerMove, 6f, 30f);
                cachedAvatar.DANCE_TRANSITION_TIME = Mathf.Clamp(beat * 2f, 0.8f, 2.5f);
                ApplyDanceSpeed(bpm);
            }

            bool interrupted = false;
            float t = 0f, dur = Mathf.Min(clip.length, maxSongSeconds);
            while (t < dur && musicSource != null && musicSource.isPlaying)
            {
                t += Time.deltaTime;
                // 更高优先级动作接管（拖拽/菜单/MMD点舞礼物舞）→ 立刻停唱停跳
                if (ShouldInterrupt()) { interrupted = true; break; }
                // 兜底：静音/极小音量下原生检测可能收不到声音，3秒还没跳就强制进舞
                if (t > 3f) ReassertDancing();
                yield return null;
            }

            if (musicSource != null) musicSource.Stop();
            ReleaseNativeDance();
            if (interrupted) Debug.Log("[SongService] Song interrupted by higher-priority action");
            else SetDancing(false);
            Destroy(clip);
            nowPlaying = null;
            playRoutine = null;
        }

        // ---------- 跳舞控制：复用内置舞蹈动画 ----------

        int pickedDanceIndex = -1;
        const int BuiltinDanceCount = 5;   // AvatarAnimatorController.DANCE_CLIP_COUNT 默认值

        // 播歌期间临时改写原生舞蹈设置，结束后还原
        bool savedDanceSwitch;
        float savedSwitchTime, savedTransitionTime, savedThreshold;
        bool savedAppAdded;
        bool danceSettingsSaved;
        string selfProcessName;

        Animator ActiveAnimator =>
            cachedAvatar != null && cachedAvatar.animator != null ? cachedAvatar.animator : cachedAnimator;

        // 原生跟舞接管：把本进程加入 allowedApps + 放宽阈值 + 开启舞步轮换。
        // 之后 AvatarAnimatorController.CheckForSound 会检测到"允许的应用在出声"，
        // 自己调用原生 StartDancing（随机起舞步）并按 DANCE_SWITCH_TIME 用
        // SmoothDanceTransition 平滑轮换 —— 全部原生流程，零手动干预。
        void EngageNativeDance()
        {
            pickedDanceIndex = UnityEngine.Random.Range(0, BuiltinDanceCount);
            if (cachedAvatar == null || !cachedAvatar.isActiveAndEnabled)
                cachedAvatar = FindFirstObjectByType<AvatarAnimatorController>();
            if (cachedAvatar == null) return;

            if (!danceSettingsSaved)
            {
                savedDanceSwitch = cachedAvatar.enableDanceSwitch;
                savedSwitchTime = cachedAvatar.DANCE_SWITCH_TIME;
                savedTransitionTime = cachedAvatar.DANCE_TRANSITION_TIME;
                savedThreshold = cachedAvatar.SOUND_THRESHOLD;
                danceSettingsSaved = true;
            }

            // 本进程名加入白名单，原生检测才会认这份音乐
            try { selfProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName; }
            catch { selfProcessName = null; }
            if (!string.IsNullOrEmpty(selfProcessName) && !cachedAvatar.allowedApps.Contains(selfProcessName))
            {
                cachedAvatar.allowedApps.Add(selfProcessName);
                savedAppAdded = true;
            }

            cachedAvatar.SOUND_THRESHOLD = 0.005f;   // 小音量也能被检测到
            cachedAvatar.enableDanceSwitch = true;    // 开启原生舞步轮换
        }

        void ReleaseNativeDance()
        {
            if (cachedAvatar != null && danceSettingsSaved)
            {
                cachedAvatar.enableDanceSwitch = savedDanceSwitch;
                cachedAvatar.DANCE_SWITCH_TIME = savedSwitchTime;
                cachedAvatar.DANCE_TRANSITION_TIME = savedTransitionTime;
                cachedAvatar.SOUND_THRESHOLD = savedThreshold;
                if (savedAppAdded && !string.IsNullOrEmpty(selfProcessName))
                    cachedAvatar.allowedApps.Remove(selfProcessName);
            }
            savedAppAdded = false;
            danceSettingsSaved = false;
            ResetDanceSpeed();
        }

        // 高优先级动作检测：拖拽 / 菜单打开 / MMD自定义舞（礼物舞、点舞）接管
        bool ShouldInterrupt()
        {
            if (cachedAvatar != null && cachedAvatar.isDragging) return true;
            if (MenuActions.IsMovementBlocked()) return true;
            var anim = ActiveAnimator;
            if (anim != null)
            {
                // MMD 自定义舞开始 = 点舞/礼物舞抢占
                foreach (var p in anim.parameters)
                    if (p.name == "isCustomDancing" && p.type == AnimatorControllerParameterType.Bool)
                        return anim.GetBool("isCustomDancing");
            }
            return false;
        }

        // 按 BPM 调整舞蹈动画速度：以 120BPM 为基准 1x
        void ApplyDanceSpeed(float bpm)
        {
            var anim = ActiveAnimator;
            if (anim == null || bpm <= 0) return;
            anim.speed = Mathf.Clamp(bpm / 120f, minSpeed, maxSpeed);
        }

        void ResetDanceSpeed()
        {
            var anim = ActiveAnimator;
            if (anim != null) anim.speed = 1f;
        }

        // 点歌期间每帧压住 isDancing，对抗 AvatarAnimatorController 的声音检测关闭。
        // 注意：不动 DanceIndex —— 舞步轮换由原生 SmoothDanceTransition 平滑处理。
        void ReassertDancing()
        {
            if (cachedAvatar != null && cachedAvatar.animator != null)
            {
                if (!cachedAvatar.isDancing || !cachedAvatar.animator.GetBool("isDancing"))
                {
                    cachedAvatar.isDancing = true;
                    cachedAvatar.animator.SetBool("isDancing", true);
                }
            }
            else if (cachedAnimator != null && !cachedAnimator.GetBool("isDancing"))
            {
                cachedAnimator.SetBool("isDancing", true);
                cachedAnimator.SetFloat("DanceIndex", pickedDanceIndex);
            }
        }

        void SetDancing(bool on)
        {
            if (cachedAvatar == null || !cachedAvatar.isActiveAndEnabled)
                cachedAvatar = FindFirstObjectByType<AvatarAnimatorController>();
            if (cachedAvatar != null && cachedAvatar.animator != null)
            {
                cachedAvatar.isDancing = on;
                cachedAvatar.animator.SetBool("isDancing", on);
                if (on)
                    cachedAvatar.animator.SetFloat("DanceIndex", pickedDanceIndex >= 0 ? pickedDanceIndex : UnityEngine.Random.Range(0, BuiltinDanceCount));
                else
                    cachedAvatar.animator.speed = 1f;
                return;
            }
            // 兜底：直接找 Animator
            if (cachedAnimator == null)
            {
                var loader = FindFirstObjectByType<VRMLoader>();
                var model = loader != null ? loader.GetCurrentModel() : null;
                if (model != null) cachedAnimator = model.GetComponentInChildren<Animator>(true);
            }
            if (cachedAnimator != null) cachedAnimator.SetBool("isDancing", on);
        }

        // ---------- BPM 估计：能量通量 onset + 自相关 ----------

        static float EstimateBPM(TTSResult pcm)
        {
            int ch = pcm.Channels, sr = pcm.SampleRate;
            int hop = sr / 50;                       // 20ms 一帧
            int nFrames = pcm.Samples.Length / ch / hop;
            if (nFrames < 200) return 0f;

            // 帧能量
            var e = new float[nFrames];
            for (int f = 0; f < nFrames; f++)
            {
                double sum = 0;
                int start = f * hop * ch, end = Mathf.Min(start + hop * ch, pcm.Samples.Length);
                for (int i = start; i < end; i++) sum += pcm.Samples[i] * pcm.Samples[i];
                e[f] = (float)sum;
            }
            // onset 通量：能量增量（只取正）
            var flux = new float[nFrames];
            for (int f = 1; f < nFrames; f++)
                flux[f] = Mathf.Max(0f, e[f] - e[f - 1]);

            // 自相关：lag 范围对应 60~180 BPM（1拍 = 0.33s~1s = 16~50帧）
            int minLag = sr / hop * 60 / 180;   // 180BPM
            int maxLag = sr / hop * 60 / 60;    // 60BPM
            float bestCorr = 0f; int bestLag = 0;
            for (int lag = minLag; lag <= maxLag && lag < nFrames / 2; lag++)
            {
                double corr = 0;
                for (int f = 0; f + lag < nFrames; f++) corr += flux[f] * flux[f + lag];
                // 轻微偏好较快节拍（防止对半拍锁定）
                float norm = (float)corr / (nFrames - lag) * (1f + 0.1f * (maxLag - lag) / (float)(maxLag - minLag));
                if (norm > bestCorr) { bestCorr = norm; bestLag = lag; }
            }
            if (bestLag <= 0) return 0f;
            float secondsPerBeat = bestLag * hop / (float)sr;
            return 60f / secondsPerBeat;
        }

        // ---------- 高潮提取：能量包络滑窗，取全曲最高能量的一段 ----------

        static TTSResult ExtractChorus(TTSResult pcm, int targetSeconds)
        {
            int ch = pcm.Channels, sr = pcm.SampleRate;
            int totalFrames = pcm.Samples.Length / ch;
            int hop = sr / 2;                        // 0.5秒一格
            int nCells = totalFrames / hop;
            if (nCells < 8) return pcm;

            // 每格RMS能量
            var energy = new float[nCells];
            for (int c = 0; c < nCells; c++)
            {
                double sum = 0;
                int start = c * hop * ch, end = Mathf.Min(start + hop * ch, pcm.Samples.Length);
                for (int i = start; i < end; i++) sum += pcm.Samples[i] * pcm.Samples[i];
                energy[c] = (float)System.Math.Sqrt(sum / Mathf.Max(1, end - start));
            }

            // 滑窗求和，找能量最高的窗口（跳过前10%规避前奏渐入）
            int winCells = Mathf.Min(nCells, targetSeconds * 2);
            int from = nCells / 10, bestStart = from;
            float bestSum = 0;
            float runSum = 0;
            for (int c = from; c < from + winCells && c < nCells; c++) runSum += energy[c];
            bestSum = runSum;
            for (int c = from + 1; c + winCells <= nCells; c++)
            {
                runSum += energy[c + winCells - 1] - energy[c - 1];
                if (runSum > bestSum) { bestSum = runSum; bestStart = c; }
            }

            // 起点向前回溯到局部低能量格（乐句边界），切入更自然
            int back = bestStart;
            for (int c = bestStart; c > Mathf.Max(from, bestStart - 8); c--)
                if (energy[c] < energy[back]) back = c;
            bestStart = back;

            int startFrame = bestStart * hop;
            int frames = Mathf.Min(winCells * hop, totalFrames - startFrame);
            var cut = new float[frames * ch];
            System.Array.Copy(pcm.Samples, startFrame * ch, cut, 0, cut.Length);

            // 首尾各1秒淡入淡出，避免爆音
            int fade = Mathf.Min(sr, frames / 4);
            for (int f = 0; f < fade; f++)
            {
                float g = (float)f / fade;
                for (int k = 0; k < ch; k++)
                {
                    cut[f * ch + k] *= g;
                    cut[(frames - 1 - f) * ch + k] *= g;
                }
            }
            return new TTSResult { Samples = cut, Channels = ch, SampleRate = sr };
        }

        // ---------- 网易云接口 ----------

        class SongHit { public long Id; public string Name; public string Artist; public string Url; }

        static async Task<SongHit> FindPlayableSong(string keyword)
        {
            try
            {
                string url = "https://music.163.com/api/search/get/web?s=" +
                             Uri.EscapeDataString(keyword) + "&type=1&limit=8";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri("https://music.163.com");
                using var resp = await http.SendAsync(req);
                var jo = JObject.Parse(await resp.Content.ReadAsStringAsync());
                var songs = jo["result"]?["songs"] as JArray;
                if (songs == null || songs.Count == 0) return null;

                // 依次探测：VIP/版权歌的外链会 302 到 /404，翻唱版通常可播
                foreach (var s in songs)
                {
                    long id = s["id"]?.Value<long>() ?? 0;
                    if (id == 0) continue;
                    string streamUrl = $"https://music.163.com/song/media/outer/url?id={id}.mp3";
                    using var head = new HttpRequestMessage(HttpMethod.Get, streamUrl);
                    using var hr = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead);
                    var finalUrl = hr.RequestMessage?.RequestUri?.ToString() ?? "";
                    var ctype = hr.Content?.Headers?.ContentType?.MediaType ?? "";
                    if (hr.IsSuccessStatusCode && !finalUrl.EndsWith("/404") && !ctype.StartsWith("text"))
                    {
                        return new SongHit
                        {
                            Id = id,
                            Name = s["name"]?.ToString() ?? keyword,
                            Artist = s["artists"]?[0]?["name"]?.ToString() ?? "",
                            Url = finalUrl
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SongService] Search failed: " + ex.Message);
            }
            return null;
        }

        static async Task<TTSResult> DownloadAndDecode(string url)
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url);
                if (bytes == null || bytes.Length < 1024) return null;
                return await Task.Run(() => AudioDecoder.Decode(bytes));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SongService] Download/decode failed: " + ex.Message);
                return null;
            }
        }
    }
}
