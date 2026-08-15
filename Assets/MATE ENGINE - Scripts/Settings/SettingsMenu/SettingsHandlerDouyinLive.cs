using System;
using System.Collections.Generic;
using System.Threading;
using DouyinLive;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// "Douyin Live" 设置页处理器。按现有 SettingsHandler* 模式：
// Start 时绑定监听 + 载入设置；控件改动 → 写 data → Save → Manager.ApplySettings()。
public class SettingsHandlerDouyinLive : MonoBehaviour
{
    [Header("总开关与连接")]
    public Toggle enableToggle;
    public TMP_InputField wsUrlInput;
    public TMP_Text statusText;

    [Header("功能开关")]
    public Toggle welcomeToggle;
    public Toggle aiReplyToggle;
    public Toggle likeToggle;
    public Toggle giftToggle;

    [Header("节流")]
    public Slider welcomeCooldownSlider;    // 3..30
    public Slider aiIntervalSlider;         // 3..30
    public Slider likeThresholdSlider;      // 50..1000

    [Header("Cloud AI")]
    public TMP_InputField aiBaseUrlInput;
    public TMP_InputField aiApiKeyInput;
    public TMP_Dropdown aiModelDropdown;
    public Button fetchModelsButton;
    public TMP_Text aiStatusText;
    public Toggle aiFallbackToggle;
    public TMP_InputField personaInput;

    [Header("TTS")]
    public TMP_Dropdown ttsProviderDropdown;   // 0=云端 1=EdgeTTS 2=Local(预留) 3=关闭
    public TMP_InputField ttsBaseUrlInput;
    public TMP_InputField ttsApiKeyInput;
    public TMP_InputField ttsModelInput;
    public TMP_InputField ttsVoiceInput;
    public TMP_Dropdown edgeVoiceDropdown;
    public Slider ttsVolumeSlider;
    public Slider ttsSpeedSlider;              // 0.5..2
    public Slider lipSyncGainSlider;           // 0.2..3
    public Button ttsTestButton;

    CancellationTokenSource fetchCts;

    void Start()
    {
        SetupListeners();
        LoadSettings();
    }

    void OnDestroy() => fetchCts?.Cancel();

    void Update()
    {
        if (statusText == null) return;
        var mgr = DouyinLiveManager.Instance;
        if (mgr == null) { statusText.text = ""; return; }
        statusText.text = mgr.ConnectionState switch
        {
            DouyinLiveClient.State.Connected => $"已连接 | 本场点赞 {mgr.SessionLikes} | AI已回复 {mgr.SessionReplies} 条",
            DouyinLiveClient.State.Connecting => "连接中…",
            DouyinLiveClient.State.Reconnecting => "重连中…（请确认弹幕抓取器已启动）",
            _ => "未连接"
        };
    }

    void SetupListeners()
    {
        enableToggle?.onValueChanged.AddListener(v => { Data.enableDouyinLive = v; Apply(); });
        wsUrlInput?.onEndEdit.AddListener(v => { Data.douyinWsUrl = v.Trim(); Apply(); });

        welcomeToggle?.onValueChanged.AddListener(v => { Data.douyinWelcomeEnabled = v; Apply(); });
        aiReplyToggle?.onValueChanged.AddListener(v => { Data.douyinAIReplyEnabled = v; Apply(); });
        likeToggle?.onValueChanged.AddListener(v => { Data.douyinLikeReactEnabled = v; Apply(); });
        giftToggle?.onValueChanged.AddListener(v => { Data.douyinGiftEnabled = v; Apply(); });

        welcomeCooldownSlider?.onValueChanged.AddListener(v => { Data.douyinWelcomeCooldown = v; Apply(); });
        aiIntervalSlider?.onValueChanged.AddListener(v => { Data.douyinAIReplyMinInterval = v; Apply(); });
        likeThresholdSlider?.onValueChanged.AddListener(v => { Data.douyinLikeThreshold = (int)v; Apply(); });

        aiBaseUrlInput?.onEndEdit.AddListener(v => { Data.aiBaseUrl = v.Trim(); Apply(); });
        aiApiKeyInput?.onEndEdit.AddListener(v => { Data.aiApiKey = v.Trim(); Apply(); });
        aiModelDropdown?.onValueChanged.AddListener(i =>
        {
            if (aiModelDropdown.options.Count > i && i >= 0)
            {
                Data.aiModel = aiModelDropdown.options[i].text;
                Apply();
            }
        });
        fetchModelsButton?.onClick.AddListener(FetchModels);
        aiFallbackToggle?.onValueChanged.AddListener(v => { Data.aiFallbackToLocal = v; Apply(); });
        personaInput?.onEndEdit.AddListener(v => { Data.douyinLivePrompt = v; Apply(); });

        ttsProviderDropdown?.onValueChanged.AddListener(i => { Data.ttsProvider = i; Apply(); });
        ttsBaseUrlInput?.onEndEdit.AddListener(v => { Data.ttsBaseUrl = v.Trim(); Apply(); });
        ttsApiKeyInput?.onEndEdit.AddListener(v => { Data.ttsApiKey = v.Trim(); Apply(); });
        ttsModelInput?.onEndEdit.AddListener(v => { Data.ttsModel = v.Trim(); Apply(); });
        ttsVoiceInput?.onEndEdit.AddListener(v => { Data.ttsVoice = v.Trim(); Apply(); });
        edgeVoiceDropdown?.onValueChanged.AddListener(i =>
        {
            if (i >= 0 && i < EdgeTTSProvider.ChineseVoices.Length)
            {
                Data.ttsEdgeVoice = EdgeTTSProvider.ChineseVoices[i];
                Apply();
            }
        });
        ttsVolumeSlider?.onValueChanged.AddListener(v => { Data.ttsVolume = v; Apply(); });
        ttsSpeedSlider?.onValueChanged.AddListener(v => { Data.ttsSpeed = v; Apply(); });
        lipSyncGainSlider?.onValueChanged.AddListener(v => { Data.lipSyncGain = v; Apply(); });
        ttsTestButton?.onClick.AddListener(() => DouyinLiveManager.Instance?.SpeakTest(null));
    }

    public void LoadSettings()
    {
        var d = Data;
        enableToggle?.SetIsOnWithoutNotify(d.enableDouyinLive);
        wsUrlInput?.SetTextWithoutNotify(d.douyinWsUrl);
        welcomeToggle?.SetIsOnWithoutNotify(d.douyinWelcomeEnabled);
        aiReplyToggle?.SetIsOnWithoutNotify(d.douyinAIReplyEnabled);
        likeToggle?.SetIsOnWithoutNotify(d.douyinLikeReactEnabled);
        giftToggle?.SetIsOnWithoutNotify(d.douyinGiftEnabled);
        welcomeCooldownSlider?.SetValueWithoutNotify(d.douyinWelcomeCooldown);
        aiIntervalSlider?.SetValueWithoutNotify(d.douyinAIReplyMinInterval);
        likeThresholdSlider?.SetValueWithoutNotify(d.douyinLikeThreshold);

        aiBaseUrlInput?.SetTextWithoutNotify(d.aiBaseUrl);
        aiApiKeyInput?.SetTextWithoutNotify(d.aiApiKey);
        SetModelDropdown(new List<string>(new[] { d.aiModel }), d.aiModel);
        aiFallbackToggle?.SetIsOnWithoutNotify(d.aiFallbackToLocal);
        personaInput?.SetTextWithoutNotify(d.douyinLivePrompt);

        if (ttsProviderDropdown != null) ttsProviderDropdown.SetValueWithoutNotify(d.ttsProvider);
        ttsBaseUrlInput?.SetTextWithoutNotify(d.ttsBaseUrl);
        ttsApiKeyInput?.SetTextWithoutNotify(d.ttsApiKey);
        ttsModelInput?.SetTextWithoutNotify(d.ttsModel);
        ttsVoiceInput?.SetTextWithoutNotify(d.ttsVoice);
        if (edgeVoiceDropdown != null)
        {
            edgeVoiceDropdown.ClearOptions();
            edgeVoiceDropdown.AddOptions(new List<string>(EdgeTTSProvider.ChineseVoices));
            int idx = Array.IndexOf(EdgeTTSProvider.ChineseVoices, d.ttsEdgeVoice);
            edgeVoiceDropdown.SetValueWithoutNotify(Mathf.Max(0, idx));
        }
        ttsVolumeSlider?.SetValueWithoutNotify(d.ttsVolume);
        ttsSpeedSlider?.SetValueWithoutNotify(d.ttsSpeed);
        lipSyncGainSlider?.SetValueWithoutNotify(d.lipSyncGain);
    }

    // “获取模型列表”：拉取 /models，填充下拉框并自动选中推荐模型
    async void FetchModels()
    {
        var d = Data;
        if (string.IsNullOrWhiteSpace(d.aiBaseUrl) || string.IsNullOrWhiteSpace(d.aiApiKey))
        {
            SetAIStatus("请先填写 API 地址和 Key");
            return;
        }
        SetAIStatus("获取模型列表中…");
        fetchCts?.Cancel();
        fetchCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var models = await CloudChatBackend.FetchModelsAsync(d.aiBaseUrl, d.aiApiKey, fetchCts.Token);
            if (models.Count == 0) { SetAIStatus("接口可用，但未返回模型"); return; }

            string pick = !string.IsNullOrEmpty(d.aiModel) && models.Contains(d.aiModel)
                ? d.aiModel
                : CloudChatBackend.RecommendModel(models);
            SetModelDropdown(models, pick);
            d.aiModel = pick;
            Apply();
            SetAIStatus($"共 {models.Count} 个模型，已选推荐：{pick}");
        }
        catch (Exception ex)
        {
            SetAIStatus("获取失败：" + ex.Message);
        }
    }

    void SetModelDropdown(List<string> models, string selected)
    {
        if (aiModelDropdown == null) return;
        models.RemoveAll(string.IsNullOrEmpty);
        if (models.Count == 0) models.Add("");
        aiModelDropdown.ClearOptions();
        aiModelDropdown.AddOptions(models);
        int idx = models.IndexOf(selected);
        aiModelDropdown.SetValueWithoutNotify(Mathf.Max(0, idx));
    }

    void SetAIStatus(string s) { if (aiStatusText != null) aiStatusText.text = s; }

    static SaveLoadHandler.SettingsData Data => SaveLoadHandler.Instance.data;

    void Apply()
    {
        SaveLoadHandler.Instance.SaveToDisk();
        DouyinLiveManager.Instance?.ApplySettings();
    }
}
