# SherpaOnnx embedded models

This folder contains configuration and model assets for the in-process `SherpaOnnxLocal` transcription backend.

- No separate local server is required.
- The hot path stays in-memory for the sherpa backend.
- Add one subfolder per profile/model id, e.g. `multilingual-streaming/`.
- For the first real integration, use an official sherpa-onnx omnilingual ASR model layout directly.

Required files for the current real integration:

- `model.int8.onnx`
- `tokens.txt`

Expected default folder tree for the active test profile:

```text
sherpa-onnx/
└─ multilingual-streaming/
   ├─ model.int8.onnx
   ├─ tokens.txt
```

If any referenced file is missing, Argus will fail clearly on Sherpa startup and will not fall back to Whisper.
