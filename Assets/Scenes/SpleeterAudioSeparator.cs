using System;
using UnityEngine;
using Unity.Sentis;
using System.Collections;
using System.Collections.Generic;
using NWaves.Operations;
using NWaves.Signals;
using NWaves.Transforms;
using NWaves.Windows;

/**
 * Isolates vocals and instrumental parts from audio via Spleeter model.
 * Implementation is based on
 * https://github.com/k2-fsa/sherpa-onnx/blob/master/scripts/spleeter/separate_onnx.py (Apache 2.0 License).
 */
public class SpleeterAudioSeparator : IDisposable
{
    // Spleeter model configuration
    // Expected input tensor shape of the ONNX model is [2, 'num_splits', 512, 1024].
    // This corresponds to (BatchSize, 'num_splits', TimeFrameCount, FrequencyBinCount).
    private const int BatchSize = 2;
    private const int TimeFrameCount = 512;
    private const int FrequencyBinCount = 1024;

    const float Epsilon = 1e-10f;
    
    // STFT Config
    private const int ModelSampleRate = 44100;
    private const int FftWindowSize = 4096;
    private const int FftSize = FftWindowSize;
    private const int FftHopSize = 1024;
    private readonly WindowType fftWindowType = WindowType.Hann;

    /**
     * ----------inputs for ./2stems/vocals.onnx----------
     * NodeArg(name='x', type='tensor(float)', shape=[2, 'num_splits', 512, 1024])
     * ----------outputs for ./2stems/vocals.onnx----------
     * NodeArg(name='y', type='tensor(float)', shape=[2, 'Transposey_dim_1', 512, 1024])
     *
     * ----------inputs for ./2stems/accompaniment.onnx----------
     * NodeArg(name='x', type='tensor(float)', shape=[2, 'num_splits', 512, 1024])
     * ----------outputs for ./2stems/accompaniment.onnx----------
     * NodeArg(name='y', type='tensor(float)', shape=[2, 'Transposey_dim_1', 512, 1024])
     */
    private readonly ModelAsset vocalsModelAsset;
    private readonly ModelAsset accompanimentModelAsset;
    private readonly BackendType backendType;

    private Model vocalsModel;
    private Worker vocalsWorker;

    private Model accompanimentModel;
    private Worker accompanimentWorker;
    
    public bool IsProcessing { get; private set; }
    public Result LastResult { get; private set; }

    public SpleeterAudioSeparator(ModelAsset vocalsModelAsset, ModelAsset accompanimentModelAsset, BackendType backendType = BackendType.CPU)
    {
        this.vocalsModelAsset = vocalsModelAsset;
        this.accompanimentModelAsset = accompanimentModelAsset;
    }

    public void LoadModels()
    {
        try
        {
            vocalsModel = ModelLoader.Load(vocalsModelAsset);
            vocalsWorker = new Worker(vocalsModel, backendType);
            
            accompanimentModel = ModelLoader.Load(accompanimentModelAsset);
            accompanimentWorker = new Worker(accompanimentModel, backendType);
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
        if (vocalsModel == null)
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

    private IEnumerator ProcessInternal(float[] inputSamples, int inputSampleRate, int channels)
    {
        float[] resampledSamples = Resample(inputSamples, inputSampleRate, ModelSampleRate);
        
        // De-interleave stereo audio samples into left and right channels
        int totalSamples = resampledSamples.Length / channels;
        float[] left = new float[totalSamples];
        float[] right = new float[totalSamples];

        for (int i = 0; i < totalSamples; i++)
        {
            left[i] = resampledSamples[i * channels];
            right[i] = resampledSamples[i * channels + 1];
        }

        // Compute STFT - get complex spectrogram (magnitude and phase)
        Stft stft = new Stft(FftWindowSize, FftHopSize, fftWindowType);
        MagnitudePhaseList stftLeft = stft.MagnitudePhaseSpectrogram(left);
        MagnitudePhaseList stftRight = stft.MagnitudePhaseSpectrogram(right);

        int numFrames = stftLeft.Magnitudes.Count;

        // Convert magnitude/phase to magnitude-only spectrograms for model input
        float[][] leftMagnitudes = new float[numFrames][];
        float[][] rightMagnitudes = new float[numFrames][];

        for (int i = 0; i < numFrames; i++)
        {
            // Keep only first 1024 frequency bins
            leftMagnitudes[i] = new float[FrequencyBinCount];
            rightMagnitudes[i] = new float[FrequencyBinCount];

            for (int f = 0; f < FrequencyBinCount && f < stftLeft.Magnitudes[i].Length; f++)
            {
                leftMagnitudes[i][f] = stftLeft.Magnitudes[i][f];
                rightMagnitudes[i][f] = stftRight.Magnitudes[i][f];
            }
        }

        // Pad to multiple of 512 frames (TimeFrameCount)
        int padding = (TimeFrameCount - (numFrames % TimeFrameCount)) % TimeFrameCount;
        int paddedFrames = numFrames + padding;
        int numSplits = paddedFrames / TimeFrameCount;

        Debug.Log($"Original frames: {numFrames}, Padding: {padding}, Padded frames: {paddedFrames}, Splits: {numSplits}");

        // Pad magnitude arrays
        float[][] paddedLeftMag = PadMagnitudes(leftMagnitudes, paddedFrames);
        float[][] paddedRightMag = PadMagnitudes(rightMagnitudes, paddedFrames);

        // Prepare input tensor: [2, numSplits, 512, 1024]
        // Channel 0 = Left, Channel 1 = Right
        using Tensor<float> inputTensor = new Tensor<float>(new TensorShape(BatchSize, numSplits, TimeFrameCount, FrequencyBinCount));

        for (int split = 0; split < numSplits; split++)
        {
            for (int t = 0; t < TimeFrameCount; t++)
            {
                int frame = split * TimeFrameCount + t;
                for (int f = 0; f < FrequencyBinCount; f++)
                {
                    inputTensor[0, split, t, f] = paddedLeftMag[frame][f]; // Left channel
                    inputTensor[1, split, t, f] = paddedRightMag[frame][f]; // Right channel
                }
            }
        }

        // Run model inference
        float[] vocalsStereoSamples = RunModel(
            vocalsWorker,
            inputTensor,
            numFrames,
            numSplits,
            leftMagnitudes,
            rightMagnitudes,
            stftLeft,
            stftRight,
            stft);
        
        float[] accompanimentStereoSamples = RunModel(
            accompanimentWorker,
            inputTensor,
            numFrames,
            numSplits,
            leftMagnitudes,
            rightMagnitudes,
            stftLeft,
            stftRight,
            stft);
        
        LastResult = new Result
        {
            Vocals = AudioClip.Create("vocals", vocalsStereoSamples.Length, channels, ModelSampleRate, false),
            Accompaniment = AudioClip.Create("accompaniment", accompanimentStereoSamples.Length, channels, ModelSampleRate, false),
        };
        LastResult.Vocals.SetData(vocalsStereoSamples, 0);
        LastResult.Accompaniment.SetData(accompanimentStereoSamples, 0);
        yield return null;
    }

    private float[] RunModel(Worker worker, Tensor<float> inputTensor, int numFrames, int numSplits, float[][] leftMagnitudes,
        float[][] rightMagnitudes, MagnitudePhaseList stftLeft, MagnitudePhaseList stftRight, Stft stft)
    {
        Debug.Log($"Running model inference with input tensor shape: {inputTensor.shape}");
        
        worker.Schedule(inputTensor);
        using Tensor<float> outputTensor = worker.PeekOutput().ReadbackAndClone() as Tensor<float>;

        // Run accompaniment model inference (you'll need a separate worker for this)
        // For now, we'll compute it as: accompaniment = sqrt(original^2 - vocals^2)
        Debug.Log($"Output tensor shape: {outputTensor.shape}");

        // Process the output using Wiener filtering
        // spec = (spec^2 + 1e-10/2) / (spec^2 + accompaniment_spec^2 + 1e-10)

        float[][] leftMask = new float[numFrames][];
        float[][] rightMask = new float[numFrames][];

        for (int i = 0; i < numFrames; i++)
        {
            leftMask[i] = new float[FrequencyBinCount];
            rightMask[i] = new float[FrequencyBinCount];
        }

        // Extract masks from model output
        for (int split = 0; split < numSplits; split++)
        {
            for (int t = 0; t < TimeFrameCount; t++)
            {
                int frame = split * TimeFrameCount + t;
                if (frame >= numFrames) continue;

                for (int f = 0; f < FrequencyBinCount; f++)
                {
                    float left = outputTensor[0, split, t, f];
                    float right = outputTensor[1, split, t, f];

                    // Get original magnitudes
                    float origLeft = leftMagnitudes[frame][f];
                    float origRight = rightMagnitudes[frame][f];

                    // Compute soft masks using Wiener filtering approach
                    // This is a simplified version - ideally you'd have both vocals and accompaniment predictions
                    float powerLeft = left * left + Epsilon / 2f;
                    float powerRight = right * right + Epsilon / 2f;

                    // TODO: For simplification, assume `accompaniment = original - vocals` ?
                    float otherPowerLeft = Mathf.Max(0, origLeft * origLeft - powerLeft) + Epsilon / 2f;
                    float otherPowerRight = Mathf.Max(0, origRight * origRight - powerRight) + Epsilon / 2f;

                    float totalPowerLeft = powerLeft + otherPowerLeft + Epsilon;
                    float totalPowerRight = powerRight + otherPowerRight + Epsilon;

                    leftMask[frame][f] = powerLeft / totalPowerLeft;
                    rightMask[frame][f] = powerRight / totalPowerRight;
                }
            }
        }

        // Apply masks to original complex spectrograms
        MagnitudePhaseList vocalsLeftSpec = ApplyMaskToComplexSpectrum(stftLeft, leftMask, numFrames);
        MagnitudePhaseList vocalsRightSpec = ApplyMaskToComplexSpectrum(stftRight, rightMask, numFrames);

        // Reconstruct time-domain signals
        float[] vocalsLeftOut = stft.ReconstructMagnitudePhase(vocalsLeftSpec);
        float[] vocalsRightOut = stft.ReconstructMagnitudePhase(vocalsRightSpec);

        // Ensure both channels have the same length
        int outputLength = Mathf.Min(vocalsLeftOut.Length, vocalsRightOut.Length);

        // Interleave left and right channels into stereo output
        float[] stereoOutput = new float[outputLength * 2];
        for (int i = 0; i < outputLength; i++)
        {
            stereoOutput[2 * i] = vocalsLeftOut[i];
            stereoOutput[2 * i + 1] = vocalsRightOut[i];
        }

        Debug.Log($"Audio separation complete. Output length: {stereoOutput.Length}");
        return stereoOutput;
    }

    // Helper method to pad magnitude arrays
    private float[][] PadMagnitudes(float[][] magnitudes, int targetFrames)
    {
        float[][] padded = new float[targetFrames][];
        int originalFrames = magnitudes.Length;

        for (int i = 0; i < targetFrames; i++)
        {
            padded[i] = new float[FrequencyBinCount];
            if (i < originalFrames)
            {
                // Copy original data
                Array.Copy(magnitudes[i], padded[i], Mathf.Min(magnitudes[i].Length, FrequencyBinCount));
            }
            // Padding frames are already initialized to zero
        }

        return padded;
    }

    // Helper method to apply mask to complex spectrum while preserving phase
    private MagnitudePhaseList ApplyMaskToComplexSpectrum(MagnitudePhaseList originalSpectrum, float[][] mask, int numFrames)
    {
        List<float[]> maskedMagnitudes = new List<float[]>();
        List<float[]> maskedPhases = new List<float[]>();

        for (int i = 0; i < numFrames; i++)
        {
            float[] maskedMag = new float[originalSpectrum.Magnitudes[i].Length];
            float[] originalPhase = originalSpectrum.Phases[i];

            // Apply mask to magnitude, keep original phase
            for (int f = 0; f < maskedMag.Length; f++)
            {
                if (f < FrequencyBinCount)
                {
                    maskedMag[f] = originalSpectrum.Magnitudes[i][f] * mask[i][f];
                }
                else
                {
                    // For frequencies beyond 1024, you might want to zero them out or copy original
                    maskedMag[f] = originalSpectrum.Magnitudes[i][f] * (f < 1024 ? mask[i][f] : 0f);
                }
            }

            maskedMagnitudes.Add(maskedMag);
            maskedPhases.Add(originalPhase); // Keep original phase
        }

        return new MagnitudePhaseList { Magnitudes = maskedMagnitudes, Phases = maskedPhases };
    }
    
    public static float[] Resample(float[] inputSamples, int originalSampleRate, int targetSampleRate)
    {
        if (originalSampleRate == targetSampleRate)
        {
            return inputSamples;
        }
        
        DiscreteSignal inputSignal = new DiscreteSignal(originalSampleRate, inputSamples);
        Resampler resampler = new Resampler();
        DiscreteSignal resampledSignal = resampler.Resample(inputSignal, targetSampleRate);
        return resampledSignal.Samples;
    }

    public void Dispose()
    {
        vocalsWorker?.Dispose();
        vocalsWorker = null;
        
        accompanimentWorker?.Dispose();
        accompanimentWorker = null;
    }

    public class Result
    {
        public AudioClip Vocals { get; set; }
        public AudioClip Accompaniment { get; set; }
    }
}
