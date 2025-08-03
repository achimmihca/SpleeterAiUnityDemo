using System;
using UnityEngine;
using Unity.Sentis;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NWaves.Signals;
using NWaves.Transforms;
using NWaves.Windows;

public class SpleeterAudioSeparator : IDisposable
{
    // Expected input tensor shape is [2, 'num_splits', 512, 1024] (provided by python).
    // This corresponds to (BatchSize, 'num_splits', TimeFrameCount, FrequencyBinCount).
    // This leads to required parameters for the STFT: windowSize = 2048, hopSize = 512.
    // Further, the model expects mono audio with 44100 Hz sample rate.
    private const int BatchSize = 2;
    private const int TimeFrameCount = 512;
    private const int FrequencyBinCount = 1024;

    private const int ModelSampleRate = 44100;
    private const int FftWindowSize = 2048;
    private const int FftHopSize = 512;

    private readonly ModelAsset modelAsset;
    private Model model;
    private Worker worker;

    public bool IsProcessing { get; private set; }
    public Result LastResult { get; private set; }

    public SpleeterAudioSeparator(ModelAsset modelAsset)
    {
        this.modelAsset = modelAsset;
    }

    public void LoadModel()
    {
        try
        {
            model = ModelLoader.Load(modelAsset);
            worker = new Worker(model, BackendType.GPUCompute);
        }
        catch (Exception e)
        {
            Debug.LogError($"Error loading model: {e.Message}");
        }
    }

    public IEnumerator Process(AudioClip audioClip)
    {
        int sampleRate = audioClip.frequency;
        int channels = audioClip.channels;
        float[] samples = new float[audioClip.samples * audioClip.channels];
        audioClip.GetData(samples, 0);

        yield return Process(samples, sampleRate, channels);
    }

    public IEnumerator Process(float[] samples, int sampleRate, int channels)
    {
        if (model == null)
        {
            throw new InvalidOperationException("Model not loaded yet");
        }

        if (IsProcessing)
        {
            throw new InvalidOperationException("Audio separation already in progress!");
        }

        IsProcessing = true;

        try
        {
            yield return ProcessInternal(samples, sampleRate, channels);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private IEnumerator ProcessInternal(float[] samples, int sampleRate, int channels)
    {
        if (sampleRate != ModelSampleRate)
        {
            throw new ArgumentException($"Sample rate must be {ModelSampleRate} Hz");
        }

        float[] monoSamples = ToMono(samples, channels);
        // WavFileWriter.WriteFile($"{Application.persistentDataPath}/mono.wav", sampleRate, 1, monoSamples);

        // Process audio
        List<float> outputSamples = new List<float>();
        yield return ProcessMonoSamples(monoSamples, sampleRate, outputSamples);

        // Create result
        if (outputSamples.Count == 0)
        {
            throw new Exception("No output audio data was reconstructed");
        }

        LastResult = new Result { AudioClip = AudioClip.Create("Vocals", outputSamples.Count, 1, sampleRate, false) };
        LastResult.AudioClip.SetData(outputSamples.ToArray(), 0);
    }

    private IEnumerator ProcessMonoSamples(float[] monoSamples, int sampleRate, List<float> outputSamples)
    {
        // Step 1: Run STFT
        Stft stft = new Stft(FftWindowSize, FftHopSize, WindowType.Hann);
        DiscreteSignal discreteSignal = new DiscreteSignal(sampleRate, monoSamples);
        MagnitudePhaseList magPhase = stft.MagnitudePhaseSpectrogram(discreteSignal);

        List<float[]> magnitudes = magPhase.Magnitudes;
        int totalFrames = magnitudes.Count;
        int binsPerFrame = magnitudes[0].Length;

        // Step 2: Validate
        if (binsPerFrame < FrequencyBinCount)
        {
            Debug.LogError(
                $"STFT returned {binsPerFrame} frequency bins, but model requires at least {FrequencyBinCount}.");
            yield break;
        }

        int framesPerSplit = TimeFrameCount;
        int numSplits = totalFrames / framesPerSplit;
        if (numSplits < 1)
        {
            Debug.LogError(
                $"Not enough STFT frames ({totalFrames}) for at least one split of {framesPerSplit} frames.");
            yield break;
        }

        // Step 3: Prepare tensor
        Debug.Log($"input tensor shape: [BatchSize={BatchSize}, numSplits={numSplits}, framesPerSplit={framesPerSplit}, FrequencyBinCount={FrequencyBinCount}]");
        TensorShape inputTensorShape = new TensorShape(BatchSize, numSplits, framesPerSplit, FrequencyBinCount);
        using Tensor<float> inputTensor = new Tensor<float>(inputTensorShape);

        // Step 4: Fill batch 0 (first audio clip)
        for (int splitIdx = 0; splitIdx < numSplits; splitIdx++)
        {
            for (int frameIdx = 0; frameIdx < framesPerSplit; frameIdx++)
            {
                int globalFrameIdx = splitIdx * framesPerSplit + frameIdx;
                float[] frameMagnitude = magnitudes[globalFrameIdx];

                for (int binIdx = 0; binIdx < FrequencyBinCount; binIdx++)
                {
                    inputTensor[0, splitIdx, frameIdx, binIdx] = frameMagnitude[binIdx];
                }
            }
        }

        // Step 5: Zero out batch 1 (optional)
        // for (int batchIdx = 1; batchIdx < BatchSize; batchIdx++)
        // {
        //     for (int splitIdx = 0; splitIdx < numSplits; splitIdx++)
        //     {
        //         for (int frameIdx = 0; frameIdx < framesPerSplit; frameIdx++)
        //         {
        //             for (int binIdx = 0; binIdx < FrequencyBinCount; binIdx++)
        //             {
        //                 inputTensor[batchIdx, splitIdx, frameIdx, binIdx] = 0f;
        //             }
        //         }
        //     }
        // }

        // Run the model
        worker.Schedule(inputTensor);
        using Tensor<float> outputTensor = worker.PeekOutput().ReadbackAndClone() as Tensor<float>;
        Debug.Log($"Processed audio samples: model output shape: {outputTensor.shape}");

        // Reconstruct samples from outputTensor
        float[] resultSamples = ReconstructOutputSamples(outputTensor, stft, magPhase);
        outputSamples.AddRange(resultSamples);
    }

    private float[] ReconstructOutputSamples(Tensor<float> outputTensor, Stft stft, MagnitudePhaseList magPhase)
    {
        List<float[]> outputMagnitudes = new List<float[]>();
        List<float[]> outputPhases = magPhase.Phases; // original phases, same frame count

        int numSplits = outputTensor.shape[1];
        int timeFrameCount = outputTensor.shape[2]; // TimeFrameCount
        int frequencyBinCount = outputTensor.shape[3]; // FrequencyBinCount

        // Step 1: Flatten [numSplits, timeFrameCount, frequencyBinCount] into [totalFrames, frequencyBinCount]
        for (int splitIdx = 0; splitIdx < numSplits; splitIdx++)
        {
            for (int frameIdx = 0; frameIdx < timeFrameCount; frameIdx++)
            {
                float[] magnitude = new float[frequencyBinCount];
                for (int binIdx = 0; binIdx < frequencyBinCount; binIdx++)
                {
                    magnitude[binIdx] = outputTensor[0, splitIdx, frameIdx, binIdx];
                }
                outputMagnitudes.Add(magnitude);
            }
        }

        // Step 2: Reconstruct full spectrogram using original phase
        int totalFrames = outputMagnitudes.Count;
        if (outputPhases.Count < totalFrames)
        {
            Debug.LogWarning("Phase frame count is smaller than required. Truncating magnitude list.");
            outputMagnitudes = outputMagnitudes.GetRange(0, outputPhases.Count);
            totalFrames = outputMagnitudes.Count;
        }

        MagnitudePhaseList reconstructedSpectrogram = new MagnitudePhaseList
        {
            Magnitudes = outputMagnitudes,
            Phases = outputPhases.GetRange(0, totalFrames) // Match frame count
        };

        // Step 3: Inverse STFT to reconstruct time-domain signal
        float[] outputSamples = stft.ReconstructMagnitudePhase(reconstructedSpectrogram);

        return outputSamples;
    }

    public void Dispose()
    {
        worker?.Dispose();
        worker = null;
    }

    private static float[] ToMono(float[] samples, int channels)
    {
        if (channels == 1)
        {
            return samples.ToArray();
        }

        int monoSamplesLength = samples.Length / channels;
        float[] monoSamples = new float[monoSamplesLength];
        for (int monoSamplesIndex = 0; monoSamplesIndex < monoSamplesLength; monoSamplesIndex++)
        {
            float sum = 0f;
            for (int channel = 0; channel < channels; channel++)
            {
                sum += samples[monoSamplesIndex * channels + channel];
            }

            monoSamples[monoSamplesIndex] = sum / channels;
        }

        return monoSamples;
    }

    public class Result
    {
        public AudioClip AudioClip { get; set; }
    }
}
