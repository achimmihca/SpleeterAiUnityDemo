using UnityEngine;

public class SampleSceneControl : MonoBehaviour
{
    void Start()
    {
        Debug.Log("SampleSceneControl Start");
        string audioFilePath = Application.dataPath + "Scenes/audio.ogg";
        SpleeterAudioSeparation(audioFilePath);
    }

    void SpleeterAudioSeparation(string audioFilePath) {
        // TODO: Implement Spleeter Audio Separation
    }
}
