using System;
using System.Collections;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

public static class AudioClipLoader
{
    public static IEnumerator LoadCoroutine(string filePath, AudioClipWrapper target)
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
        target.audioClip = audioClip;
    }
    
    
    /**
     * Wraps a reference to an object to allow passing it by reference in coroutines.
     */
    public class AudioClipWrapper
    {
        public AudioClip audioClip;
    }
}
