# Spleeter AI Unity Demo

This Unity project demonstrates how to integrate Spleeter AI for vocals isolation using Unity Sentis and ONNX models.

## Features

- **Audio Separation**: Separate vocals and accompaniment from mixed audio tracks
- **Unity Sentis Integration**: Uses Unity's AI inference engine for real-time processing
- **ONNX Model Support**: Loads pre-trained Spleeter models in ONNX format
- **Audio Output**: Generates separate AudioClips for vocals and accompaniment
- **File Export**: Saves separated audio as WAV files
- **UI Controls**: Simple interface for testing and controlling the separation process

## Project Structure

```
Assets/
├── Scenes/
│   ├── SampleSceneControl.cs      # Main Spleeter integration script
│   ├── SpleeterUIControl.cs      # UI control script
│   ├── audio_example.ogg         # Sample audio file
│   └── SampleScene.unity         # Main scene
└── StreamingAssets/
    ├── vocals.onnx               # Vocals separation model
    └── accompaniment.onnx        # Accompaniment separation model
```

## Setup Instructions

### 1. Prerequisites

- Unity 2022.3 LTS or later
- Unity Sentis package (already included in this project)
- ONNX models for Spleeter (already included)

### 2. Scene Setup

1. Open the `SampleScene` in Unity
2. Ensure the `SampleSceneControl` script is attached to a GameObject
3. (Optional) Add UI elements and attach the `SpleeterUIControl` script

### 3. Audio Sources Setup (for UI testing)

If you want to test the audio playback through the UI:

1. Create three AudioSource components in your scene
2. Assign them to the `SpleeterUIControl` script:
   - `originalAudioSource`
   - `vocalsAudioSource`
   - `accompanimentAudioSource`

## Usage

### Automatic Processing

The audio separation starts automatically when the scene loads. The system will:

1. Load the ONNX models from `StreamingAssets`
2. Load the audio file specified in `audioFileName`
3. Process the audio through both models
4. Generate separate AudioClips for vocals and accompaniment
5. Save the separated audio as WAV files (if `saveSeparatedAudio` is enabled)

### Manual Control

You can control the process through the UI or by calling methods directly:

```csharp
// Get the separated audio clips
AudioClip vocals = spleeterControl.GetVocalsAudioClip();
AudioClip accompaniment = spleeterControl.GetAccompanimentAudioClip();

// Check processing status
bool isProcessing = spleeterControl.IsProcessing();
bool modelsLoaded = spleeterControl.AreModelsLoaded();
```

### Configuration

In the `SampleSceneControl` component, you can configure:

- **Audio File Name**: The audio file to process (default: "audio_example.ogg")
- **Save Separated Audio**: Whether to save output files (default: true)
- **Output Folder**: Folder name for saved files (default: "SeparatedAudio")

## Output Files

When `saveSeparatedAudio` is enabled, the separated audio files are saved to:
```
[Application.persistentDataPath]/SeparatedAudio/
├── vocals.wav
└── accompaniment.wav
```

## Technical Details

### Model Architecture

The implementation uses two separate ONNX models:
- **Vocals Model**: Extracts vocal tracks from mixed audio
- **Accompaniment Model**: Extracts instrumental tracks from mixed audio

### Audio Processing Pipeline

1. **Audio Loading**: Loads audio file using UnityWebRequest
2. **Tensor Conversion**: Converts AudioClip to TensorFloat for model input
3. **Model Inference**: Processes audio through both ONNX models using Unity Sentis
4. **Output Conversion**: Converts model outputs back to AudioClips
5. **File Export**: Saves results as WAV files

### Performance Considerations

- Models are loaded once at startup and reused
- GPU acceleration is used when available
- Memory is properly managed with using statements
- Resources are disposed when the component is destroyed

## Troubleshooting

### Common Issues

1. **Models not loading**
   - Ensure ONNX files are in `StreamingAssets` folder
   - Check file permissions and paths

2. **Audio not loading**
   - Verify audio file exists in the specified path
   - Check audio file format (OGG is supported)

3. **Processing errors**
   - Check Unity Console for detailed error messages
   - Ensure sufficient memory for large audio files

### Debug Information

The system provides detailed logging:
- Model loading status
- Audio file loading progress
- Processing steps and tensor shapes
- File save operations

## Customization

### Using Different Audio Files

1. Place your audio file in the `Assets/Scenes/` folder
2. Update the `audioFileName` field in the inspector
3. Ensure the file format is supported (OGG recommended)

### Using Different Models

1. Replace the ONNX files in `StreamingAssets`
2. Ensure the new models have compatible input/output shapes
3. Update the tensor conversion code if needed

### UI Customization

The `SpleeterUIControl` script can be customized to add:
- Progress bars with actual progress
- Volume controls
- Waveform visualization
- Batch processing capabilities

## Dependencies

- **Unity Sentis**: AI inference engine
- **Unity Input System**: For UI interactions
- **TextMesh Pro**: For UI text elements

## License

This project demonstrates Spleeter AI integration in Unity. The Spleeter models are subject to their respective licenses.

## Contributing

Feel free to submit issues and enhancement requests!
