# 8Ball UGC Generation Workflows (ComfyUI API format)

Tested 2026-08-07 against ComfyUI 0.29.2 on localhost:8188 (RTX 5090).
Submit via POST http://127.0.0.1:8188/prompt — these are API-format graphs, not UI-canvas exports.

Test product: Weyland-Yutani Corporation Building Better Worlds T-Shirt
(handle: weyland-yutani-corporation-building-better-worlds-t-shirt, source image
downloaded to D:\ComfyUI-Data\input\weyland-yutani-original.jpg)

## Image workflows (Qwen Image Edit 2511)

| File | What it does | Notes |
|---|---|---|
| wf_black.json | Fast draft: woman wearing black tee, indoor UGC | Lightning 4-step LoRA, ~1 min, glossier look |
| wf_navy.json | Fast draft: man wearing navy recolour, street | Lightning 4-step LoRA |
| wf_black_v2.json | Photoreal: 20 steps, cfg 2.5, no Lightning | Amateur-iPhone prompting; the approved look |
| wf_navy_v2.json | Photoreal navy, street scene | Same recipe as black_v2 |
| wf_navy_v3.json | Photoreal navy targeting hex #263147 | Hex described in words + negative against bright blue; lit fabric sampled ≈ #2B3744, avg #1C2533 (shadows pull darker — expected) |

Key settings (v2/v3 recipe): qwen_image_edit_2511_fp8mixed + qwen_2.5_vl_7b CLIP +
qwen_image_vae, ModelSamplingAuraFlow shift 3.1, euler/simple, 20 steps, cfg 2.5,
1024x1360 empty latent, product photo as image1 reference into TextEncodeQwenImageEditPlus.

## Video workflow (Wan 2.2 I2V A14B)

| File | What it does | Notes |
|---|---|---|
| wf_video.json | 5s clip (81 frames @16fps) from a generated still | Two-stage: high-noise model steps 0-10, low-noise 10-20, cfg 3.5, 480x640 test res. ~12 min without accelerator |

Pending: Wan 2.2 Lightning I2V LoRAs (lightx2v/Wan2.2-Lightning, HIGH + LOW pair)
to be installed — then video drops to 4 steps (2+2), cfg 1.0, ~2-3 min/clip.

## Batch parameterisation

Per product, swap: LoadImage filename, positive prompt (person/setting/colour),
SaveImage/SaveVideo filename_prefix, and KSampler seed. Colour changes are described
in words (models don't read hex reliably); verify by pixel-sampling the output.

---

## Where this lives now (added when wired into Baldrick, 2026-08-07)

Canonical copy is here — `D:\ComfyUI-Data\baldrick\workflows\8ball-ugc\` — which is
version controlled. The originals in `D:\AI Work\8Ball\workflows` are left in place
and are no longer the source of truth; edit these.

The v3 recipe is now implemented in `percy_worker.py` as method
**`product_ugc_qwen`**, so batches do not run these JSON files directly. The worker
builds the same graph from the recipe held in Baldrick
(`client_output_types.render_params` for 8Ball / `ugc_post`), which is what lets a
batch vary per product without editing a workflow file. These files remain the
readable reference and the thing to diff against if output ever drifts.

**What happens to output:** the worker uploads to the asset host under
`8b/produced/`, then PATCHes the job to `generated`. Baldrick's API maps that to
`awaiting_approval` — so every image lands in the approval queue for review rather
than in a folder. Nothing posts without being approved.
