using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Sentis;
using UnityEditor;
using UnityEngine;

public class SpleeterAudioSeparatorTest
{
    private ModelAsset vocalsModelAsset;
    private ModelAsset accompanimentModelAsset;
    private SpleeterAudioSeparator separator;

    private static string defaultAudioFile = "stereo_44100hz.ogg";

    private static string testAudioFolder = $"{Application.dataPath}/Tests/TestAudio";

    private static List<string> testAudioFiles = Directory.GetFiles(testAudioFolder, "*.ogg")
        .Select(path => Path.GetFileName(path))
        .ToList();

    private static List<BackendType> backendTypes = new List<BackendType>()
    {
        BackendType.CPU, BackendType.GPUCompute, BackendType.GPUPixel
    };

    [SetUp]
    public void SetUp()
    {
        vocalsModelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/AiModels/Spleeter/vocals.onnx");
        accompanimentModelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/AiModels/Spleeter/accompaniment.onnx");
        Assert.IsNotNull(vocalsModelAsset);
        Assert.IsNotNull(accompanimentModelAsset);

        using var d = new DisposableStopwatch($"loading models");
        separator = new SpleeterAudioSeparator(vocalsModelAsset, accompanimentModelAsset);
        separator.LoadModels();
    }

    [TearDown]
    public void TearDown()
    {
        separator.Dispose();
    }

    [Test]
    public void ShouldLoadModels()
    {
        Assert.IsTrue(separator.HasLoadedModels);
    }

    [Test]
    [TestCaseSource(nameof(testAudioFiles))]
    public void ShouldProcessAudio(string audioFileName)
    {
        AudioClip audioClip = LoadAudioClip($"{testAudioFolder}/{audioFileName}");
        Assert.IsNotNull(audioClip);

        using var d = new DisposableStopwatch($"processing AudioClip '{audioFileName}'");
        separator.Process(audioClip);
        AssertResult(separator.LastResult, audioClip);
    }

    [Test]
    [TestCaseSource(nameof(backendTypes))]
    public void ShouldProcessOnBackend(BackendType backendType)
    {
        AudioClip originalAudioClip = LoadAudioClip($"{testAudioFolder}/{defaultAudioFile}");

        using var d = new DisposableStopwatch($"processing with backend {backendType}");
        separator = new SpleeterAudioSeparator(vocalsModelAsset, accompanimentModelAsset, backendType);
        separator.LoadModels();
        separator.Process(originalAudioClip);
        AssertResult(separator.LastResult, originalAudioClip);
    }

    private static void AssertResult(SpleeterAudioSeparator.Result result, AudioClip originalAudioClip)
    {
        Assert.IsNotNull(result, "Result is null");
        Assert.IsNotNull(result.Vocals, "Vocals is null");
        Assert.IsNotNull(result.Accompaniment, "Accompaniment is null");
        Assert.AreEqual(44100, result.Vocals.frequency, "Vocals sample rate mismatch");
        Assert.AreEqual(44100, result.Accompaniment.frequency, "Accompaniment sample rate mismatch");
        Assert.AreEqual(originalAudioClip.channels, result.Vocals.channels, "Vocals channels mismatch");
        Assert.AreEqual(originalAudioClip.channels, result.Accompaniment.channels, "Accompaniment channels mismatch");
        Assert.AreEqual(originalAudioClip.length, result.Vocals.length, 0.1f, "Vocals length mismatch");
        Assert.AreEqual(originalAudioClip.length, result.Accompaniment.length, 0.1f, "Accompaniment length mismatch");
    }

    private static AudioClip LoadAudioClip(string path)
    {
        using var d = new DisposableStopwatch($"loading AudioClip '{path}'");
        
        if (path.StartsWith(Application.dataPath))
        {
            path = $"Assets/{path.Substring(Application.dataPath.Length)}";
        }

        return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
    }
}
