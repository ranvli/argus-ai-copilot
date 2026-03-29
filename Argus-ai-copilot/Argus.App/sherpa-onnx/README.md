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
