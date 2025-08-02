using System;
using UnityEngine;
using Unity.Sentis;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Dmo;
using NWaves.Signals;
using NWaves.Transforms;
using NWaves.Windows;
using Unity.VisualScripting;

public class SpleeterAudioSeparator : IDisposable
{
    private const int ModelSampleRate = 44100;
    private const int FftWindowSize = 1024;
    private const int FftHopSize = 512;
    private const int FftFrequencyBins = FftWindowSize / 2 + 1;
    
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

        // Time Frames must be multiples of 64, due to the model architecture.
        // Smaller values (64, 128): faster processing, but less accurate.
        // Larger values (256, 512): slower processing, but more accurate.
        int timeFrames = 64;
        int chunkLength = (timeFrames - 1) * FftHopSize + FftWindowSize;
        int chunkCount = (int)Math.Ceiling(monoSamples.Length / (double)chunkLength);

        // TODO: Implement padding for last chunk. Missing padding is why currently only 1 chunk is processed.
        if (chunkCount <= 1)
        {
            throw new ArgumentException($"Not enough samples to process in multiple chunks");
        }
        chunkCount = 1;

        List<float> outputSamples = new List<float>();
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            Debug.Log($"Processing chunk {chunk + 1}/{chunkCount}");
            yield return ProcessChunk(monoSamples, sampleRate, channels, chunk, chunkLength, outputSamples);
        }

        // Create result
        if (outputSamples.Count == 0)
        {
            throw new Exception("No output audio data was reconstructed");
        }

        LastResult = new Result
        {
            AudioClip = AudioClip.Create("Vocals", outputSamples.Count, 1, sampleRate, false)
        };
        LastResult.AudioClip.SetData(outputSamples.ToArray(), 0);
    }

    private IEnumerator ProcessChunk(float[] monoSamples, int sampleRate, int channels, int chunk, int chunkLength, List<float> outputSamples)
    {
        // Prepare samples of chunk to be processed
        long startSample = chunk * chunkLength;
        if (startSample + chunkLength > monoSamples.Length)
        {
            throw new ArgumentException($"Chunk is out of range: chunk index {chunk}, startSample {startSample}, chunkLength {chunkLength}, total samples {monoSamples.Length}");
        }

        float[] chunkSamples = new float[chunkLength];
        Array.Copy(monoSamples, startSample, chunkSamples, 0, chunkLength);

        // Prepare input tensor
        Stft stft = new Stft(windowSize: FftWindowSize, hopSize: FftHopSize, window: WindowType.Hann);
        DiscreteSignal discreteSignal = new DiscreteSignal(sampleRate, chunkSamples);
        MagnitudePhaseList magPhase = stft.MagnitudePhaseSpectrogram(discreteSignal);

        // Expected input tensor shape is (2, d0, 512, 1024)
        using Tensor<float> inputTensor = new Tensor<float>(new TensorShape(2, 0, FftHopSize, FftWindowSize));
        for (int t = 0; t < FftWindowSize; t++)
        {
            for (int f = 0; f < FftHopSize; f++)
            {
                inputTensor[0, 0, f, t] = magPhase.Magnitudes[t][f];
                inputTensor[1, 0, f, t] = magPhase.Phases[t][f];
            }
        }

        // Run the model
        yield return worker.ScheduleIterable(inputTensor);

        // Reconstruct output samples
        using Tensor<float> outputTensor = worker.PeekOutput().ReadbackAndClone() as Tensor<float>;
        Debug.Log($"Processed chunk {chunk}: model output shape: {outputTensor.shape}, Phase frames: {magPhase.Phases.Count}"); 
        float[] outputSamplesOfChunk = ReconstructWithOriginalPhase(outputTensor, magPhase.Phases, stft);
        outputSamples.AddRange(outputSamplesOfChunk);
    }

    private float[] ReconstructWithOriginalPhase(Tensor<float> modelOutput, List<float[]> originalPhases, NWaves.Transforms.Stft stft)
    {
        int timeFrames = modelOutput.shape[3];
        int freqBins = modelOutput.shape[2];

        MagnitudePhaseList magnitudePhaseList = new MagnitudePhaseList
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
