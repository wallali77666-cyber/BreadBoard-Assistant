# Colab AI backend

This folder holds the notebook that turns a **Colab GPU runtime** into the cognitive loop of BreadboardAOI. The desktop workstation keeps the 60 FPS reflex loop (capture → homography → YOLO → PinMatrix → DRC → SkiaSharp) entirely local; only the Qwen3-VL tutor is streamed from the tunnel.

```
Workstation ──(Drive API, drive.file)──► MyDrive/breadboardaoi-colab.json
     ▲                                            │
     │  endpointUrl + apiKey                      │  read by the notebook
     └────────────── cloudflared tunnel ◄──────── llama-server on Colab
```

## 1. Create the OAuth client (once, ~5 minutes)

The app performs a native OAuth 2.0 authorization-code flow with PKCE. Google requires an app registration; there is no way around it for a desktop client.

1. Open <https://console.cloud.google.com/> and create (or pick) a project.
2. **APIs & Services → Library → Google Drive API → Enable.**
3. **APIs & Services → OAuth consent screen.**
   * User type: **External**.
   * Add your own Google account under **Test users** (or publish the app).
   * Scopes: `openid`, `email`, `profile`, `https://www.googleapis.com/auth/drive.file`.
4. **APIs & Services → Credentials → Create credentials → OAuth client ID.**
   * Application type: **Desktop app**.
   * No redirect URI needs to be registered: the app uses an ephemeral `http://127.0.0.1:<port>/oauth2/callback` loopback, which Google allows for installed apps.
5. Copy the **Client ID** (and the client secret, which Google still requires at the token endpoint) into **REMOTE AI / COLAB** in the workstation, then press **LINK GOOGLE**.

The refresh token is stored in `%LOCALAPPDATA%\BreadboardAOI\google-session.json`. The client registration is stored next to it in `google-client.json`. **UNLINK** revokes the grant with Google and deletes the local token.

Environment overrides also work:

```powershell
$env:BREADBOARD_GOOGLE_CLIENT_ID     = "<id>.apps.googleusercontent.com"
$env:BREADBOARD_GOOGLE_CLIENT_SECRET = "<secret>"
$env:BREADBOARD_USE_COLAB            = "1"
```

## 2. Publish the one-time session key

Press **PUBLISH SESSION**. The app creates `breadboardaoi-colab.json` in your Drive with:

```json
{
  "status": "waiting",
  "apiKey": "bbaoi_...",
  "notebookUrl": "https://colab.research.google.com/github/...",
  "updatedUtc": "2026-10-27T12:00:00+00:00"
}
```

Only the narrow `drive.file` scope is requested, so the app sees **only files it created** — not the rest of your Drive.

## 3. Run the notebook

**OPEN NOTEBOOK** launches <https://colab.research.google.com/github/wallali77666-cyber/BreadBoard-Assistant/blob/main/assets/colab/BreadboardAOI_Qwen.ipynb>.

1. **Runtime → Change runtime type → GPU** (T4 is enough for the 8B model).
2. **Run all.**
3. Cell 2 authenticates the runtime to the same Google account and mounts Drive.
4. Cells 3–4 build `llama-server` with CUDA from GitHub and fetch the weights.
5. Cell 5 starts `llama-server --api-key <session key>`, opens a `cloudflared` quick tunnel, health-checks the public URL and writes `status: "serving"` back into the Drive file.
6. Cell 6 keeps the session alive and marks it `stopped` when it exits.

The workstation polls Drive every 15 seconds (or on **REFRESH ENDPOINT**), probes `/health`, and only then swaps the tutor. If the tunnel dies, the next diagnosis silently falls back to local `llama-server`, then to the offline rule-based tutor.

## 4. Weights are mirrored to GitHub by a workflow

The project policy is GitHub-only downloads at runtime, and copying ~6 GB from your PC is unnecessary. A one-time GitHub Actions workflow does the mirroring in the cloud:

1. GitHub → **Actions → Publish Qwen3-VL model release → Run workflow** (defaults are correct).
2. The runner downloads the official **Qwen3-VL-8B-Instruct Q4_K_M** GGUF (4.8 GB) and **F16 projector** (1.1 GB), splits the GGUF into three sub-2 GB assets (`part_00` … `part_02`) and publishes the `models-v1` release.
3. Notebook cell 1 already points at those URLs, so **Run all** needs no edits.

Your PC transfers none of the weights; Google Colab pulls only from the GitHub release. The workflow also publishes `SHA256SUMS.txt` for integrity checks.

### Manual alternative

If you already have the files locally:

```powershell
# 1.9 GB parts keep you safely under the 2 GB asset limit
split -b 1900M -d -a 2 Qwen3VL-8B-Instruct-Q4_K_M.gguf Qwen3VL-8B-Instruct-Q4_K_M.gguf.part_

gh release create models-v1 `
  Qwen3VL-8B-Instruct-Q4_K_M.gguf.part_* `
  mmproj-Qwen3VL-8B-Instruct-F16.gguf `
  --repo wallali77666-cyber/BreadBoard-Assistant `
  --title "BreadboardAOI models v1" `
  --notes "Split GGUF weights for the Colab tutor. Concatenate parts in order."
```

Then update `MODEL_PARTS` / `GITHUB_MMPROJ_URL` in cell 1 if the file names differ.

### Runtime sizes

| Runtime | Model | Notes |
| :--- | :--- | :--- |
| Free T4 (16 GB VRAM / 12.7 GB RAM) | `Qwen3VL-8B-Instruct Q4_K_M` | ~4.8 GB weights + 1.1 GB projector; runs fully on the GPU. |
| Colab Pro L4 (24 GB) | `Qwen3VL-30B-A3B-Instruct Q4_K_M` | ~17.7 GB weights + 1.0 GB projector; tight but usable. |
| Colab Pro A100 40 GB | `Qwen3VL-30B-A3B-Instruct Q4_K_M` | Comfortable; matches the local `--n-gpu-layers 99` path. |

The 30B-A3B cannot run on a free T4: its weights alone exceed VRAM, and CPU offload on a 2-vCPU runtime is too slow.

## 5. Manual endpoint (no Drive)

If you would rather not link an account, start the notebook and paste the printed `https://….trycloudflare.com` URL plus the key into **MANUAL TUNNEL** and press **APPLY MANUAL ENDPOINT**. The same probe and fallback rules apply.

## 6. Limits and caveats

- Colab's terms restrict always-on, non-interactive use. Long-idle runtimes are reclaimed and heavy use can be throttled; this integration is meant for interactive lab sessions.
- A quick tunnel URL is public. `llama-server` is started with `--api-key`, so unauthenticated requests are rejected, but treat the key like a password.
- Rotating the key is just **PUBLISH SESSION** again: the notebook re-reads the Drive file at startup, and the app picks up the new key on the next refresh.
- Qwen is the only remote workload. YOLO pose inference, the hand-motion gate, the DRC engine and the SkiaSharp viewport always run locally.
