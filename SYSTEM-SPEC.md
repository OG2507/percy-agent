# The Percy system — the agreed specification

Agreed line by line with Stephen, night of 2026-08-08. This is the contract the
build is measured against. Changes to this design happen in conversation with
Stephen and land here — never silently in code.

## The flow

Percy asks Baldrick: "is there a job for me?" Baldrick says yes and sends the
job. Percy does the job. The image is converted (PNG master → JPEG for
shipping), collected into `D:\Generation\<CLIENT>\<year-month>\`, uploaded to
the asset host, and reported back — into Stephen's approval queue.

## Where knowledge lives — the whole point

| Knowledge | Lives | Never lives |
|---|---|---|
| WHAT to make (jobs, prompts, client specifics, LoRA names, sizes) | Baldrick (server) | — |
| HOW to make it (method → workflow file → injections → finishing steps) | **the methods table in Percy Agent's SQLite database** | Python scripts, text files |
| The workflow graphs themselves | `C:\ComfyUI\ComfyUI\user\default\workflows\baldrick\` — backed up, openable in ComfyUI | inside code |
| Every generated file (local copies) | `D:\Generation\<CLIENT>\<year-month>\` — one folder, backupable | scattered output dirs |
| The record (uploads, approvals, posting proof) | Baldrick + the asset host | — |

- Baldrick always sends the full parcel; the method's **injection map** decides
  what gets consumed and where it goes. Baldrick never knows what any method
  needs — the method name is the only contract between the two sides.
- The worker is a dumb engine: it looks up the row and does what it says.
  Unknown method = loud refusal, never a guess.
- ComfyUI's own setup is untouched — other systems run on it. Only this
  pipeline's outputs relocate themselves (the collect step).

## Growing it — copy and change

- New look for an existing kind: copy the nearest workflow file, change the
  copy, add a methods row pointing at it. No code.
- New client on existing methods: a Baldrick recipe row. No code.
- Genuinely new kind (e.g. the planned Claymation badge videos): build the new
  workflow in ComfyUI where it can be seen, save the file, add its row. Code is
  touched only if a never-before-seen finishing step is needed — it then joins
  the toolbox for everyone.

## The two advert rows (so this never confuses anyone again)

1. **Plain product advert** (local, proven): real photo → cutout → price/size
   overlay. Deliberately NO AI render and NO prompt — re-rendering made
   products plasticky and dropped hardware (learned 2026-07-26).
2. **Enhanced advert** (cloud, GPT Image 2): uses the developed prompt.
   Different method, own row. No local worker exists for the cloud route yet.

## Judgement jobs (analysis, schedule design)

Every judgement job = a readable prompt file + a trigger Stephen can reach:
spoken in a session now, a button later, the VPS (API key) eventually.
The analysis cap of 3 limits what asks for attention per run — the monthly
pool is UNCAPPED: everything good-but-not-now is banked with a reason and
dealt with the month. Nothing is ever dropped.
Schedule design waits on Stephen's posting rules being dictated.

## The build (2026-08-08, authorised: pieces 1–5)

1. Methods table in the app's SQLite, seeded truthfully
2. Methods tab in Percy Agent
3. Worker rewritten as the generic engine; inline graphs become workflow files
4. Collect step → `D:\Generation`
5. Run the JKB analysis per the prompt file (3 proposed + uncapped pool)
