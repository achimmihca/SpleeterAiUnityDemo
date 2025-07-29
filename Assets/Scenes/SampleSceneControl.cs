using System;
using UnityEngine;
using Unity.Sentis;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NWaves.Signals;
using NWaves.Transforms;

public class SampleSceneControl : MonoBehaviour
{
    [Header("Audio Settings")]
    [SerializeField] private string audioFileName = "audio_example_long.ogg";

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
        catch (Exception e)
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

        // Load audio clip
        yield return LoadAudioFile(audioFilePath);

        if (originalAudioClip == null)
        {
            Debug.LogError("Failed to load audio file!");
            isProcessing = false;
            yield break;
        }

        Debug.Log("Converting to mono for STFT processing...");

        // Extract mono samples
        float[] audioData = new float[originalAudioClip.samples * originalAudioClip.channels];
        originalAudioClip.GetData(audioData, 0);
        float[] monoSamples = new float[originalAudioClip.samples];

        for (int i = 0; i < originalAudioClip.samples; i++)
        {
            float sum = 0f;
            for (int c = 0; c < originalAudioClip.channels; c++)
            {
                sum += audioData[i * originalAudioClip.channels + c];
            }
            monoSamples[i] = sum / originalAudioClip.channels;
        }

        int sampleRate = originalAudioClip.frequency;

        var stft = new Stft(1024, 256, NWaves.Windows.WindowType.Hann);

        Debug.Log("Processing signal in overlapping chunks...");

        List<float> vocalsCombined = new List<float>();
        List<float> accompCombined = new List<float>();

        int windowFrames = 1024;
        int hopSize = 512;

        int totalFrames = (int)Mathf.Floor(monoSamples.Length / (float)hopSize) - 2;
        int chunkCount = (int)Mathf.Floor(totalFrames / (float)windowFrames) + 1;
        if (chunkCount <= 0)
        {
            Debug.LogError("Not enough data to process in chunks.");
            isProcessing = false;
            yield break;
        }
        
        Debug.Log($"Samples: {monoSamples.Length}, HopSize: {hopSize}, WindowFrames: {windowFrames}, TotalFrames: {totalFrames}, ChunkCount: {chunkCount}");

        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            int startSample = chunk * hopSize * windowFrames;
            int length = 1024 * 256;  // 1024 frames × 256 hop = 262,144 samples
            if (startSample + length > monoSamples.Length)
                break;

            float[] chunkSamples = new float[length];
            Array.Copy(monoSamples, startSample, chunkSamples, 0, length);
            var chunkSignal = new DiscreteSignal(sampleRate, chunkSamples);

            var magPhase = stft.MagnitudePhaseSpectrogram(chunkSignal);

            if (magPhase.Magnitudes.Count != 1024 || magPhase.Magnitudes[0].Length != 512)
            {
                Debug.LogWarning($"Invalid shape: {magPhase.Magnitudes.Count} x {magPhase.Magnitudes[0].Length}");
                continue;
            }

            // === Prepare Input Tensor ===
            var inputTensor = new Tensor<float>(new TensorShape(2, 1, 512, 1024));
            for (int t = 0; t < 1024; t++)
            {
                for (int f = 0; f < 512; f++)
                {
                    inputTensor[0, 0, f, t] = magPhase.Magnitudes[t][f];
                    inputTensor[1, 0, f, t] = magPhase.Phases[t][f];
                }
            }

            // === Run Spleeter model ===
            yield return vocalsWorker.ScheduleIterable(inputTensor);
            var vocalsOut = vocalsWorker.PeekOutput() as Tensor<float>;

            yield return accompanimentWorker.ScheduleIterable(inputTensor);
            var accompOut = accompanimentWorker.PeekOutput() as Tensor<float>;

            // === Reconstruct waveform ===
            Debug.Log($"[Chunk {chunk}] ModelOutput shape: {vocalsOut.shape}, Phase frames: {magPhase.Phases.Count}");
            float[] vocalsSegment = ReconstructWithOriginalPhase(vocalsOut, magPhase.Phases, stft);
            float[] accompSegment = ReconstructWithOriginalPhase(accompOut, magPhase.Phases, stft);
            
            vocalsCombined.AddRange(vocalsSegment);
            accompCombined.AddRange(accompSegment);

            inputTensor.Dispose();
            vocalsOut.Dispose();
            accompOut.Dispose();
        }

        // === Finalize ===
        if (vocalsCombined.Count == 0)
        {
            Debug.LogError("No vocals audio data was reconstructed for vocals.");
            yield break;
        }
        if (accompCombined.Count == 0)
        {
            Debug.LogError("No instrumental audio data was reconstructed for vocals.");
            yield break;
        }
        
        vocalsAudioClip = AudioClip.Create("Vocals", vocalsCombined.Count, 1, sampleRate, false);
        vocalsAudioClip.SetData(vocalsCombined.ToArray(), 0);
        
        accompanimentAudioClip = AudioClip.Create("Accompaniment", accompCombined.Count, 1, sampleRate, false);
        accompanimentAudioClip.SetData(accompCombined.ToArray(), 0);

        if (saveSeparatedAudio)
            yield return SaveSeparatedAudioFiles();

        Debug.Log("Spleeter chunked processing finished.");
        isProcessing = false;
    }

    private float[] ReconstructWithOriginalPhase(Tensor<float> modelOutput, List<float[]> originalPhases, NWaves.Transforms.Stft stft)
    {
        if (originalPhases.Count != modelOutput.shape[3])
        {
            Debug.LogError($"Mismatch: originalPhases.Count = {originalPhases.Count}, modelOutput.shape[3] = {modelOutput.shape[3]}");
            return new float[0];
        }
        
        int timeFrames = modelOutput.shape[3];
        int freqBins = modelOutput.shape[2];

        var magnitudePhaseList = new MagnitudePhaseList
        {
            Magnitudes = new List<float[]>(timeFrames),
            Phases = originalPhases  // reuse original phase from input
        };

        for (int t = 0; t < timeFrames; t++)
        {
            float[] magFrame = new float[freqBins];
            for (int f = 0; f < freqBins; f++)
            {
                magFrame[f] = modelOutput[0, 0, f, t]; // only 1 channel
            }
            magnitudePhaseList.Magnitudes.Add(magFrame);
        }

        return stft.ReconstructMagnitudePhase(magnitudePhaseList);
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
