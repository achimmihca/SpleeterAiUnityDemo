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
        Wrapper originalAudioClipWrapper = new Wrapper();
        yield return LoadAudioFile($"{Application.dataPath}/Scenes/{audioFileName}", originalAudioClipWrapper);
        AudioClip originalAudioClip = originalAudioClipWrapper.Obj as AudioClip;
        Debug.Log($"Loaded AudioClip: samples: {originalAudioClip.samples}, channels: {originalAudioClip.channels}, frequency: {originalAudioClip.frequency}");

        Debug.Log("Loading Spleeter model");
        spleeterAudioSeparator?.Dispose();
        spleeterAudioSeparator = new SpleeterAudioSeparator(vocalsModelAsset, accompanimentModelAsset);
        spleeterAudioSeparator.LoadModels();
        Debug.Log("Loaded Spleeter model");
        
        Debug.Log("Processing audio with Spleeter");
        yield return spleeterAudioSeparator.Process(originalAudioClip);
        Debug.Log("Processed audio with Spleeter");

        if (saveSeparatedAudio)
        {
            SaveAudioClip(originalAudioClip, "original");
            SaveAudioClip(spleeterAudioSeparator.LastResult.Vocals);
            SaveAudioClip(spleeterAudioSeparator.LastResult.Accompaniment);
        }
    }

    private IEnumerator LoadAudioFile(string filePath, Wrapper target)
    {
        if (!File.Exists(filePath))
        {
            throw new ArgumentException($"Audio file not found: {filePath}");
        }

        // Load audio file using Unity's WWW (for older Unity versions) or UnityWebRequest
        string url = "file://" + filePath;
        using UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.OGGVORBIS);
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            throw new InvalidOperationException($"Failed to load audio file: {www.error}");
        }

        AudioClip audioClip = DownloadHandlerAudioClip.GetContent(www);
        target.Obj = audioClip;
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

    /**
     * Wraps a reference to an object to allow passing it by reference in coroutines.
     */
    private class Wrapper
    {
        public object Obj { get; set; }
    }
}
