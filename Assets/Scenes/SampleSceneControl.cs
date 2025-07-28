using UnityEngine;
using Unity.Sentis;
using System.Collections;
using System.IO;

public class SampleSceneControl : MonoBehaviour
{
    [Header("Audio Settings")]
    [SerializeField] private string audioFileName = "audio_example.ogg";

    [SerializeField] private bool saveSeparatedAudio = true;
    [SerializeField] private string outputFolder = "SeparatedAudio";

    [SerializeField] private ModelAsset vocalsModelAsset;
    [SerializeField] private ModelAsset accompanimentModelAsset;
    
    private Model vocalsModel;
    private Model accompanimentModel;
    private Worker vocalsWorker;
    private Worker accompanimentWorker;

    private AudioClip originalAudioClip;
    private AudioClip vocalsAudioClip;
    private AudioClip accompanimentAudioClip;

    private bool isProcessing;
    private bool modelsLoaded;

    void Start()
    {
        Debug.Log("SampleSceneControl Start");
        StartCoroutine(InitializeSpleeter());
    }

    public IEnumerator InitializeSpleeter()
    {
        Debug.Log("Initializing Spleeter models...");

        // Load ONNX models
        yield return LoadModels();

        if (modelsLoaded)
        {
            Debug.Log("Models loaded successfully. Starting audio separation...");
            string audioFilePath = Path.Combine(Application.dataPath, "Scenes", audioFileName);
            yield return SpleeterAudioSeparation(audioFilePath);
        }
        else
        {
            Debug.LogError("Failed to load Spleeter models!");
        }
    }

    private IEnumerator LoadModels()
    {
        try
        {
            // Load vocals model
            vocalsModel = ModelLoader.Load(vocalsModelAsset);
            vocalsWorker = new Worker(vocalsModel, BackendType.GPUCompute);
            Debug.Log("Vocals model loaded successfully");

            // Load accompaniment model
            accompanimentModel = ModelLoader.Load(accompanimentModelAsset);
            accompanimentWorker = new Worker(accompanimentModel, BackendType.GPUCompute);
            Debug.Log("Accompaniment model loaded successfully");

            modelsLoaded = true;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error loading models: {e.Message}");
            modelsLoaded = false;
        }
        yield return null;
    }

    private IEnumerator SpleeterAudioSeparation(string audioFilePath)
    {
        if (isProcessing)
        {
            Debug.LogWarning("Audio separation already in progress!");
            yield break;
        }

        isProcessing = true;
        Debug.Log($"Starting audio separation for: {audioFilePath}");

        // Load audio file
        yield return LoadAudioFile(audioFilePath);

        if (originalAudioClip == null)
        {
            Debug.LogError("Failed to load audio file!");
            isProcessing = false;
            yield break;
        }

        // Convert audio to tensor
        Tensor<float> audioTensor = ConvertAudioToTensor(originalAudioClip);

        // Process with both models
        yield return ProcessAudioWithModels(audioTensor);

        // Save separated audio files if requested
        if (saveSeparatedAudio)
        {
            yield return SaveSeparatedAudioFiles();
        }

        Debug.Log("Audio separation completed successfully!");
        isProcessing = false;
    }

    private IEnumerator LoadAudioFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError($"Audio file not found: {filePath}");
            yield break;
        }

        // Load audio file using Unity's WWW (for older Unity versions) or UnityWebRequest
        string url = "file://" + filePath;
        using (UnityEngine.Networking.UnityWebRequest www =
               UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(url, AudioType.OGGVORBIS))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                originalAudioClip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(www);
                Debug.Log(
                    $"Audio loaded successfully: {originalAudioClip.name}, Length: {originalAudioClip.length}s, Channels: {originalAudioClip.channels}, Frequency: {originalAudioClip.frequency}");
            }
            else
            {
                Debug.LogError($"Failed to load audio file: {www.error}");
            }
        }
    }

    private Tensor<float> ConvertAudioToTensor(AudioClip audioClip)
    {
        // Get audio data
        float[] audioData = new float[audioClip.samples * audioClip.channels];
        audioClip.GetData(audioData, 0);

        // Reshape data for model input (assuming mono or stereo)
        int samples = audioClip.samples;
        int channels = audioClip.channels;

        // Create tensor with shape [1, channels, samples] for ONNX model
        Tensor<float> tensor = new Tensor<float>(new TensorShape(1, channels, samples));

        // Copy audio data to tensor
        for (int i = 0; i < samples; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                int audioIndex = i * channels + c;
                tensor[0, c, i] = audioData[audioIndex];
            }
        }

        Debug.Log($"Converted audio to tensor: {tensor.shape}");
        return tensor;
    }

    private IEnumerator ProcessAudioWithModels(Tensor<float> audioTensor)
    {
        Debug.Log("Processing audio with Spleeter models...");

        // Process with vocals model
        yield return vocalsWorker.ScheduleIterable(audioTensor);
        using Tensor<float> vocalsOutput = vocalsWorker.PeekOutput() as Tensor<float>;
        Debug.Log($"Vocals model output shape: {vocalsOutput.shape}");

        // Process with accompaniment model
        yield return accompanimentWorker.ScheduleIterable(audioTensor);
        using Tensor<float> accompanimentOutput = accompanimentWorker.PeekOutput() as Tensor<float>;
        Debug.Log($"Accompaniment model output shape: {accompanimentOutput.shape}");

        // Store results for later conversion
        yield return ConvertTensorToAudioData(vocalsOutput, accompanimentOutput);
    }

    private IEnumerator ConvertTensorToAudioData(Tensor<float> vocalsTensor, Tensor<float> accompanimentTensor)
    {
        // Convert tensors back to audio data
        float[] vocalsData = new float[vocalsTensor.count];
        float[] accompanimentData = new float[accompanimentTensor.count];

        // Copy tensor data to arrays
        for (int i = 0; i < vocalsTensor.count; i++)
        {
            vocalsData[i] = vocalsTensor[i];
        }

        for (int i = 0; i < accompanimentTensor.count; i++)
        {
            accompanimentData[i] = accompanimentTensor[i];
        }

        // Create AudioClips from the separated data
        vocalsAudioClip = AudioClip.Create("Vocals", originalAudioClip.samples, originalAudioClip.channels,
            originalAudioClip.frequency, false);
        vocalsAudioClip.SetData(vocalsData, 0);

        accompanimentAudioClip = AudioClip.Create("Accompaniment", originalAudioClip.samples,
            originalAudioClip.channels, originalAudioClip.frequency, false);
        accompanimentAudioClip.SetData(accompanimentData, 0);

        Debug.Log("Converted tensors to AudioClips");

        yield return null;
    }

    private IEnumerator SaveSeparatedAudioFiles()
    {
        Debug.Log("Saving separated audio files...");

        // Create output directory
        string outputPath = Path.Combine(Application.persistentDataPath, outputFolder);
        if (!Directory.Exists(outputPath))
        {
            Directory.CreateDirectory(outputPath);
        }

        // Save vocals
        yield return SaveAudioClipToFile(vocalsAudioClip, Path.Combine(outputPath, "vocals.wav"));

        // Save accompaniment
        yield return SaveAudioClipToFile(accompanimentAudioClip, Path.Combine(outputPath, "accompaniment.wav"));

        Debug.Log($"Separated audio files saved to: {outputPath}");
    }

    private IEnumerator SaveAudioClipToFile(AudioClip audioClip, string filePath)
    {
        try
        {
            WavFileWriter.WriteFile(filePath, audioClip);
            Debug.Log($"Saved audio file: {filePath}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to save audio file {filePath}: {e.Message}");
        }

        yield return null;
    }

    void OnDestroy()
    {
        // Clean up resources
        if (vocalsWorker != null)
        {
            vocalsWorker.Dispose();
            vocalsWorker = null;
        }

        if (accompanimentWorker != null)
        {
            accompanimentWorker.Dispose();
            accompanimentWorker = null;
        }

        if (vocalsModel != null)
        {
            // Model doesn't need explicit disposal in newer Unity Sentis versions
            vocalsModel = null;
        }

        if (accompanimentModel != null)
        {
            // Model doesn't need explicit disposal in newer Unity Sentis versions
            accompanimentModel = null;
        }
    }

    // Public methods for external access
    public AudioClip GetVocalsAudioClip() => vocalsAudioClip;
    public AudioClip GetAccompanimentAudioClip() => accompanimentAudioClip;
    public AudioClip GetOriginalAudioClip() => originalAudioClip;
    public bool IsProcessing() => isProcessing;
    public bool AreModelsLoaded() => modelsLoaded;
}
