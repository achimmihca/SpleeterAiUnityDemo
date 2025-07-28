using UnityEngine;
using UnityEngine.UIElements;
using System.Collections;

public class SpleeterUIToolkit : MonoBehaviour
{
    [Header("UI References")] [SerializeField]
    private UIDocument uiDocument;

    [Header("Audio Sources")] [SerializeField]
    private AudioSource originalAudioSource;

    [SerializeField] private AudioSource vocalsAudioSource;
    [SerializeField] private AudioSource accompanimentAudioSource;

    // UI Elements
    private Label statusText;
    private ProgressBar progressBar;
    private Button stopButton;
    private Button playOriginalButton;
    private Button playVocalsButton;
    private Button playAccompanimentButton;
    private Label infoText;
    private TextField audioFileField;
    private Toggle saveFilesToggle;

    private SampleSceneControl spleeterControl;
    private bool isPlaying = false;
    private AudioSource currentPlayingSource;

    void Start()
    {
        // Find the Spleeter control component
        spleeterControl = FindObjectOfType<SampleSceneControl>();
        if (spleeterControl == null)
        {
            Debug.LogError("SampleSceneControl not found in scene!");
            return;
        }

        // Setup UI
        SetupUI();
    }

    private void SetupUI()
    {
        if (uiDocument == null)
        {
            Debug.LogError("UIDocument not assigned!");
            return;
        }

        var root = uiDocument.rootVisualElement;

        // Get UI elements
        statusText = root.Q<Label>("status-text");
        progressBar = root.Q<ProgressBar>("progress-bar");
        stopButton = root.Q<Button>("stop-button");
        playOriginalButton = root.Q<Button>("play-original-button");
        playVocalsButton = root.Q<Button>("play-vocals-button");
        playAccompanimentButton = root.Q<Button>("play-accompaniment-button");
        infoText = root.Q<Label>("info-text");
        audioFileField = root.Q<TextField>("audio-file-field");
        saveFilesToggle = root.Q<Toggle>("save-files-toggle");

        // Setup button events
        if (stopButton != null)
        {
            stopButton.clicked += StopAllAudio;
        }

        if (playOriginalButton != null)
        {
            playOriginalButton.clicked += () => PlayAudio(spleeterControl.GetOriginalAudioClip(), originalAudioSource);
        }

        if (playVocalsButton != null)
        {
            playVocalsButton.clicked += () => PlayAudio(spleeterControl.GetVocalsAudioClip(), vocalsAudioSource);
        }

        if (playAccompanimentButton != null)
        {
            playAccompanimentButton.clicked += () =>
                PlayAudio(spleeterControl.GetAccompanimentAudioClip(), accompanimentAudioSource);
        }

        // Setup settings
        if (audioFileField != null)
        {
            audioFileField.RegisterValueChangedCallback(OnAudioFileChanged);
        }

        if (saveFilesToggle != null)
        {
            saveFilesToggle.RegisterValueChangedCallback(OnSaveFilesChanged);
        }

        // Initial UI update
        UpdateButtonStates();
    }

    private void Update()
    {
        UpdateStatus();
        UpdateButtonStates();
        UpdateInfo();
    }

    private void UpdateStatus()
    {
        if (statusText == null) return;

        string status = "";

        if (spleeterControl.IsProcessing())
        {
            status = "Processing audio separation...";
            if (progressBar != null)
            {
                progressBar.visible = true;
                // Simple progress animation
                progressBar.value = Mathf.PingPong(Time.time * 0.5f, 1f);
            }
        }
        else if (spleeterControl.AreModelsLoaded())
        {
            status = "Ready for audio separation";
            if (progressBar != null)
            {
                progressBar.visible = false;
            }
        }
        else
        {
            status = "Loading models...";
            if (progressBar != null)
            {
                progressBar.visible = false;
            }
        }

        statusText.text = status;
    }

    private void UpdateButtonStates()
    {
        if (playOriginalButton != null)
        {
            playOriginalButton.SetEnabled(spleeterControl.GetOriginalAudioClip() != null &&
                                          !spleeterControl.IsProcessing());
        }

        if (playVocalsButton != null)
        {
            playVocalsButton.SetEnabled(spleeterControl.GetVocalsAudioClip() != null &&
                                        !spleeterControl.IsProcessing());
        }

        if (playAccompanimentButton != null)
        {
            playAccompanimentButton.SetEnabled(spleeterControl.GetAccompanimentAudioClip() != null &&
                                               !spleeterControl.IsProcessing());
        }

        if (stopButton != null)
        {
            stopButton.SetEnabled(isPlaying);
        }
    }

    private void UpdateInfo()
    {
        if (infoText == null) return;

        string info = "";
        AudioClip original = spleeterControl.GetOriginalAudioClip();
        AudioClip vocals = spleeterControl.GetVocalsAudioClip();
        AudioClip accompaniment = spleeterControl.GetAccompanimentAudioClip();

        if (original != null)
        {
            info += $"Original: {original.length:F1}s\n";
        }

        if (vocals != null)
        {
            info += $"Vocals: {vocals.length:F1}s\n";
        }

        if (accompaniment != null)
        {
            info += $"Accompaniment: {accompaniment.length:F1}s";
        }

        if (string.IsNullOrEmpty(info))
        {
            info = "No audio loaded";
        }

        infoText.text = info;
    }

    private void PlayAudio(AudioClip audioClip, AudioSource audioSource)
    {
        if (audioClip == null)
        {
            Debug.LogWarning("Audio clip is null!");
            return;
        }

        // Stop any currently playing audio
        StopAllAudio();

        // Play the selected audio
        if (audioSource != null)
        {
            audioSource.clip = audioClip;
            audioSource.Play();
            currentPlayingSource = audioSource;
            isPlaying = true;

            Debug.Log($"Playing: {audioClip.name}");
        }
    }

    private void StopAllAudio()
    {
        if (originalAudioSource != null) originalAudioSource.Stop();
        if (vocalsAudioSource != null) vocalsAudioSource.Stop();
        if (accompanimentAudioSource != null) accompanimentAudioSource.Stop();

        currentPlayingSource = null;
        isPlaying = false;
    }

    private void OnAudioFileChanged(ChangeEvent<string> evt)
    {
        Debug.Log($"Audio file changed to: {evt.newValue}");
        // You could add a method to SampleSceneControl to update the audio file name
    }

    private void OnSaveFilesChanged(ChangeEvent<bool> evt)
    {
        Debug.Log($"Save files setting changed to: {evt.newValue}");
        // You could add a method to SampleSceneControl to update the save setting
    }

    void OnDestroy()
    {
        StopAllAudio();
    }
}
