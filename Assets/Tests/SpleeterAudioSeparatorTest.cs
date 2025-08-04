using NUnit.Framework;
using Unity.Sentis;
using UnityEditor;

public class SpleeterAudioSeparatorTest
{
    private ModelAsset vocalsModelAsset;
    private ModelAsset accompanimentModelAsset;
    private SpleeterAudioSeparator separator;
    
    [SetUp]
    public void SetUp()
    {
        vocalsModelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/AiModels/Spleeter/vocals.onnx");
        accompanimentModelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/AiModels/Spleeter/accompaniment.onnx");
        Assert.IsNotNull(vocalsModelAsset);
        Assert.IsNotNull(accompanimentModelAsset);
        
        separator = new SpleeterAudioSeparator(vocalsModelAsset, accompanimentModelAsset);
    }

    [TearDown]
    public void TearDown()
    {
        separator.Dispose();
    }
    
    [Test]
    public void ShouldLoadModels()
    {
        separator.LoadModels();
        Assert.IsTrue(separator.HasLoadedModels);
    }
}
