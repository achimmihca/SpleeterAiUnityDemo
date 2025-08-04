using System;
using UnityEngine;
using Unity.Sentis;
using System.Collections;
using System.IO;
using UnityEngine.Networking;

public class SampleSceneControl : MonoBehaviour
{
    public ModelAsset vocalsModelAsset;
    public ModelAsset accompanimentModelAsset;
    public BackendType backendType = BackendType.CPU;

    private string audioFileName = "audio_example_long.ogg";
    private bool saveSeparatedAudio = true;
    
    private SpleeterAudioSeparator spleeterAudioSeparator;
    
    void Start()
    {
        Debug.Log("SampleSceneControl Start");

        StartCoroutine(ProcessOriginalAudio());
    }

    public IEnumerator ProcessOriginalAudio()
    {
        Debug.Log("Loading AudioClip");
        AudioClipLoader.AudioClipWrapper originalAudioClipWrapper = new();
        yield return AudioClipLoader.LoadCoroutine($"{Application.dataPath}/Scenes/{audioFileName}", originalAudioClipWrapper);
        AudioClip originalAudioClip = originalAudioClipWrapper.audioClip;
        Debug.Log($"Loaded AudioClip: samples: {originalAudioClip.samples}, channels: {originalAudioClip.channels}, frequency: {originalAudioClip.frequency}");

        Debug.Log("Loading Spleeter model");
        spleeterAudioSeparator?.Dispose();
        spleeterAudioSeparator = new SpleeterAudioSeparator(vocalsModelAsset, accompanimentModelAsset, backendType);
        spleeterAudioSeparator.LoadModels();
        Debug.Log("Loaded Spleeter model");
        
        Debug.Log("Processing audio with Spleeter");
        spleeterAudioSeparator.Process(originalAudioClip);
        Debug.Log("Processed audio with Spleeter");

        if (saveSeparatedAudio)
        {
            SaveAudioClip(originalAudioClip, "original");
            SaveAudioClip(spleeterAudioSeparator.LastResult.Vocals);
            SaveAudioClip(spleeterAudioSeparator.LastResult.Accompaniment);
        }
    }

    private void SaveAudioClip(AudioClip audioClip, string name = null)
    {
        Debug.Log($"Saving AudioClip '{audioClip.name}'");
        string fileBaseName = name ?? audioClip.name;
        string absoluteOutputFile = $"{Application.persistentDataPath}/{fileBaseName}.wav";
        WavFileWriter.WriteFile(absoluteOutputFile, audioClip);
        Debug.Log($"Saved AudioClip '{audioClip.name}' to: {absoluteOutputFile}");
    }

    void OnDestroy()
    {
        spleeterAudioSeparator?.Dispose();
    }
}
