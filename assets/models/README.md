# Model drop folder

The repository intentionally does **not** commit model weights, and **no model is required
to launch the workstation**: Demo Mode, the synthetic board and the rule-based offline tutor
are the default path. Missing files are reported in the Hardware dock and the affected
feature degrades (CPU ONNX / rule-based tutor / Windows SAPI voice).

| File | Used by | Notes |
| :--- | :--- | :--- |
| `yolov11s_breadboard_pose_fp16.onnx` | `3_BreadboardAOI.Vision` | YOLOv11s-Pose fine-tuned for breadboard components (2 keypoints = metal leads). Optional. |
| `Qwen3-VL-30B-A3B-Instruct-Q4_K_M.gguf` | `4_BreadboardAOI.Intelligence` | ~10.6 GB VRAM with `--n-gpu-layers 99 --n-cpu-moe 22`. Only for local inference. |
| `mmproj-Qwen3-VL-30B-f16.gguf` | `4_BreadboardAOI.Intelligence` | Multimodal projector for the local model. |
| `kokoro-tts.onnx` + tokens/voices | `4_BreadboardAOI.Intelligence` | Sherpa-ONNX Kokoro-82M export. Optional (SAPI fallback). |
| `llama-server.exe` | `4_BreadboardAOI.Intelligence` | CUDA build, launched on P-Cores (0x00FFF), port 8089. Only for local inference. |

All weights are excluded by `.gitignore`, including nested model folders such as
`kokoro/`. The intelligence layer handles missing files gracefully so the app still launches.

## Remote inference (recommended on laptops)

The **REMOTE AI** tab connects the tutor to the operator's own Google Colab runtime, so the
Qwen weights never need to live on this PC. See `assets/colab/README.md` and the
`BreadboardAOI_Qwen.ipynb` notebook. Local files are only used when the Colab route is not
configured or its tunnel is down.

## Cloud mirror (recommended)

The `models-v1` release is produced by `.github/workflows/publish-models.yml`. Trigger it once from **Actions → Publish Qwen3-VL model release → Run workflow**: GitHub's runners download the official weights, split the GGUF below the 2 GB asset limit and publish the release. Neither your PC nor Colab talks to the model publisher; Colab pulls only from GitHub.

Assets produced:

| Asset | Size | Notes |
| :--- | ---: | :--- |
| `Qwen3VL-8B-Instruct-Q4_K_M.gguf.part_00` … `part_02` | ~4.8 GB total | Concatenate in order. |
| `mmproj-Qwen3VL-8B-Instruct-F16.gguf` | 1.1 GB | Vision projector. |
| `SHA256SUMS.txt` | tiny | Verify the joined model. |

The Colab notebook's configuration cell already points at these URLs.

## Manual download (GitHub only)

Keep downloads on GitHub to avoid third-party CDN traffic:

```powershell
gh release download models-v1 `
  --repo wallali77666-cyber/BreadBoard-Assistant `
  --dir assets/models
```

GitHub caps a single release asset at 2 GB, so large GGUFs are published as split parts
(`*.gguf.part_00`, …). The Colab notebook joins them automatically; locally, concatenate
with `Get-Content -AsByteStream` or `cat` before use. For the CUDA `llama-server.exe`, use the
upstream `ggml-org/llama.cpp` GitHub Releases (also GitHub-hosted). Avoid Hugging Face or
vendor mirrors unless a release asset is unavailable.
