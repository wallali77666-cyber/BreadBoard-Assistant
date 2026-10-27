# Model drop folder

The repository intentionally does **not** commit model weights. Place the following files here before starting Phase 3/4 features:

| File | Used by | Notes |
| :--- | :--- | :--- |
| `yolov11s_breadboard_pose_fp16.onnx` | `3_BreadboardAOI.Vision` | YOLOv11s-Pose fine-tuned for breadboard components (2 keypoints = metal leads). |
| `Qwen3-VL-30B-A3B-Instruct-Q4_K_M.gguf` | `4_BreadboardAOI.Intelligence` | ~10.6 GB VRAM with `--n-gpu-layers 99 --n-cpu-moe 22`. |
| `mmproj-Qwen3-VL-30B-f16.gguf` | `4_BreadboardAOI.Intelligence` | Multimodal projector. |
| `kokoro-tts.onnx` + tokens/voices | `4_BreadboardAOI.Intelligence` | Sherpa-ONNX Kokoro-82M export. |
| `llama-server.exe` | `4_BreadboardAOI.Intelligence` | CUDA build, launched on P-Cores (0x00FFF), port 8089. |

All of the above are excluded by `.gitignore`. The intelligence layer handles missing files gracefully so the app still launches.
