# SherpaOnnx embedded models

This folder contains configuration and model assets for the in-process `SherpaOnnxLocal` transcription backend.

- No separate local server is required.
- The hot path stays in-memory for the sherpa backend.
- Add one subfolder per profile/model id, e.g. `multilingual-streaming/`.
- Each profile folder should contain a `profile.json` file plus the referenced model files.

Example expected files:

- `profile.json`
- `tokens.txt`
- `silero_vad.onnx`
- `lid-encoder.onnx`
- `lid-decoder.onnx`
- language-routed ASR files (zipformer/transducer/omnilingual/etc.)
- diarization segmentation and embedding models

Expected default folder tree for the active test profile:

```text
sherpa-onnx/
└─ multilingual-streaming/
   ├─ profile.json
   ├─ tokens.txt
   ├─ vad/
   │  └─ silero_vad.onnx
   ├─ lid/
   │  ├─ lid-encoder.onnx
   │  └─ lid-decoder.onnx
   ├─ diarization/
   │  ├─ segmentation.onnx
   │  └─ speaker-embedding.onnx
   └─ asr/
      ├─ es/
      │  └─ model.onnx
      ├─ en/
      │  └─ model.onnx
      ├─ pt/
      │  └─ model.onnx
      ├─ it/
      │  └─ model.onnx
      ├─ de/
      │  └─ model.onnx
      └─ zh/
         └─ model.onnx
```

If any referenced file is missing, Argus will fail clearly on Sherpa startup and will not fall back to Whisper.
