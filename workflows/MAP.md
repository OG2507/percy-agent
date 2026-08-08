# Production map — what runs where, and where to change it

Open this when you want to know where something lives or where to change it.
Built from the live system on 2026-08-07. If it disagrees with anything, the
database wins — this is a map, not the territory.

---

## The one-line version

**Baldrick decides, Percy executes, ComfyUI renders, you approve, files land on D:.**

```
Baldrick (VPS)          →  Percy Worker (this PC)  →  ComfyUI (5090)
 holds the recipe           reads it, dispatches       renders
        ↑                                                  │
        │  approve / reject                                ↓
        └──────────  asset host  ←──── uploads ────────────┘
                          │
                          ↓  on approval
                D:\New 8Ball\<product>\...
```

---

## Where to change what — the short answer

| I want to change… | Change it here | Restart needed |
|---|---|---|
| The prompt for a client's output type | Baldrick → `client_output_types.prompt_template` | No |
| Model, LoRA, steps, cfg, size | Baldrick → `client_output_types.render_params` | No |
| Which colours a product uses | Baldrick → `garment_colours` | No |
| What a rejection reason offers | `apps/web/.../produce/approvals/page.tsx` → `REASONS` | Deploy |
| Where produced files upload to | `percy_worker.py` → the `folder` line near `upload()` | No |
| Where approved files are filed | `file_approved.py` → `ROOT` | No |
| A genuinely new *kind* of image | `percy_worker.py` → add a method (see below) | No |

**Nearly everything is a database row.** Only a new *kind* of work needs code.

---

## Methods — what Percy can actually make

Percy dispatches on `render_params.method` from the recipe. Unknown method =
refuses and names it. It never guesses.

| Method | Makes | Engine | Implemented |
|---|---|---|---|
| `birefnet_cutout+price_overlay` / `product_image` | Product advert: real cutout + price overlay | rembg + PIL | **Yes** |
| `ugc_post` / `scene+cutout` | Product on a surface, true-scale | SD 3.5 + PIL | **Yes** |
| `zimage_lora_still` / `character_still` | A character, from her own LoRA | Z-Image Turbo | **Yes** |
| `product_ugc_qwen` / `ugc_apparel` | Person wearing the real garment | Qwen-Image-Edit 2511 | **Yes** |
| `comfy+pil_per_product` | JKB carousel | — | **No — recipe says `proven`, nothing runs it** |
| `capable_model_simple_prompt` | JKB infographic | — | **No — same** |
| `scene_or_backdrop + designed_overlay` | JKB quote card | — | **No — same** |

The last three are worth knowing about: their recipes are marked `proven` but
Percy has no implementation, so those jobs fail with "no local implementation".
Marked `proven` means someone proved it by hand, not that the worker can do it.

## Live recipes

| Client | Output type | Method | Size | Status |
|---|---|---|---|---|
| 8Ball | `ugc_post` | `product_ugc_qwen` | 1200×1440 (5:6) | drafted |
| Erin Vale | `carousel` | `zimage_lora_still` | 1080×1350 | drafted |
| Erin Vale | `text_overlay_reel` | `zimage_lora_still` | 1024×1408 | drafted |
| Erin Vale | `talking_head_reel` | `zimage_lora_still` | 1024×1408 | not_started — no lipsync exists |
| JKB | `product_image` | `birefnet_cutout+price_overlay` | — | proven |

---

## Where files are, at each stage

| Stage | Location |
|---|---|
| **Workflows** (reference graphs) | `C:\ComfyUI\ComfyUI\user\default\workflows\baldrick\` — backed up. Junctioned into the toolkit repo so it is version controlled too |
| **Toolkit scripts** | `D:\ComfyUI-Data\baldrick\` — `percy_worker.py`, `make_social.py`, `file_approved.py` |
| **Raw renders** | `D:\ComfyUI-Data\output\` — PNG masters. **Contain the full workflow in a text chunk. Never publish these** |
| **Shipping copies** | Same folder, `_fin.jpg` — JPEG, workflow stripped |
| **Uploaded to** | asset host, `<client-code>/produced/` |
| **After approval** | `D:\New 8Ball\<product-handle>\04-lifestyle-images\` |
| **Social crops** | `…\05-social-exports\1x1\`, `\4x5\`, `\9x16\` |
| **Record** | `PRODUCT-RECORD.md` at each product root, rebuilt each time something is filed |

---

## Adding a new method

1. Write the function in `percy_worker.py` (copy `character_still` — it is the
   clearest example: build the graph, POST to ComfyUI, poll, return the path).
2. Add the method name to a set at the top, e.g. `CHARACTER_STILL`.
3. Add a dispatch branch in the main loop.
4. Point a recipe at it: `render_params.method` in `client_output_types`.
5. Wire it into `agent_modes` — a capability nothing invokes sits idle.

## Adding a new character

No code at all. A LoRA on disk, a client row, and a recipe naming the LoRA file
and trigger word. `zimage_lora_still` reads everything else from the recipe.

---

## Things that are not built, so nobody looks for them

- **Lipsync** — no implementation anywhere. Talking-head reels cannot be produced.
- **Approved-jobs endpoint** — `file_approved.py` is written but has nothing to call.
- **The social trigger** — approval does not yet kick off the social job.
- **Video** — one proven Wan 2.2 workflow, not wired to Percy.
