#!/usr/bin/env python3
"""Percy Brain — the one controller that knows everything, locally.

THE HONESTY LAW: the model may only phrase facts fetched THIS call from the
registry's stations. No fetched fact -> "I don't know". The brain reads state;
it never writes, never acts. Turn it off and every station still runs.

CPU-ONLY by design: ollama is called with num_gpu=0 so the 5090 stays with
whoever owns it (training, renders, voice). See workflows/procedures/percy_brain.md.

    python percy_brain.py --ask "what's rendering?"
    python percy_brain.py --ask "what needs me?" --speak   (voice via Percy-Voice, GPU permitting)
    python percy_brain.py --serve                          (HTTP for the Percy Agent chat pane)
"""
import json, os, sqlite3, subprocess, sys, urllib.request

DB = os.environ.get("PERCY_DB", r"C:\GIT\percy-agent\publish\data\percy-agent.db")
OLLAMA = "http://127.0.0.1:11434"
MODEL = os.environ.get("PERCY_BRAIN_MODEL", "alibayram/Qwen3-30B-A3B-Instruct-2507:latest")
VOICE_URL = "http://127.0.0.1:7333"
VOICE_ID = "percy-321d"

SYSTEM = """You are Percy, the calm British operations controller for Stephen's businesses.
You will receive FACTS fetched seconds ago from the live systems, as JSON, station by station.
Answer Stephen's question in 1-4 plain sentences using ONLY those facts.
THE LAW: if the facts do not contain the answer, say "I don't know" and name which station
you'd need. Never guess, never invent numbers, never pad. Plain spoken, warm, brief."""


def fetch_station(name, kind, target, headers):
    try:
        if kind == "note":
            return {"note": target}
        if kind == "command":
            out = subprocess.run(target.split(), capture_output=True, text=True, timeout=10).stdout.strip()
            return {"output": out}
        req_headers = {}
        if headers.startswith("secret_file:"):
            secret = open(headers.split(":", 1)[1].strip()).read().strip()
            req_headers["x-production-secret"] = secret
        req = urllib.request.Request(target, headers=req_headers)
        with urllib.request.urlopen(req, timeout=15) as r:
            data = json.load(r)
        # keep payloads small for a small model: trim the noisiest fields
        if name == "baldrick" and isinstance(data, dict):
            prod = data.get("production", {})
            data = {
                "batches": prod.get("batches"),
                "renderingOrNext": (prod.get("lineup") or [])[:5],
                "threeStrikeFailures": prod.get("deadJobs"),
                "runsNeedingDecision": [i for i in data.get("items", []) if i.get("kind") == "awaiting-decision"],
                "failedRuns": [i for i in data.get("items", []) if i.get("kind") == "failed"],
                "queues": data.get("queues"),
            }
        return data
    except Exception as e:
        return {"unreachable": str(e)[:120]}


def gather():
    db = sqlite3.connect(DB)
    rows = db.execute("SELECT name, description, ask_kind, ask_target, headers FROM registry WHERE enabled = 1").fetchall()
    db.close()
    facts = {}
    for name, desc, kind, target, headers in rows:
        facts[name] = {"what_it_is": desc, "state": fetch_station(name, kind, target, headers)}
    return facts


def ask(question):
    facts = gather()
    body = json.dumps({
        "model": MODEL,
        "stream": False,
        "options": {"num_gpu": 0, "temperature": 0.2},
        "messages": [
            {"role": "system", "content": SYSTEM},
            {"role": "user", "content": "FACTS (fetched just now):\n" + json.dumps(facts, default=str)[:24000]
                + "\n\nSTEPHEN ASKS: " + question},
        ],
    }).encode()
    req = urllib.request.Request(OLLAMA + "/api/chat", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.load(r)["message"]["content"].strip()


def speak(text):
    """Voice the reply via Percy-Voice — only if it's up. TTS uses the GPU, so
    during training this quietly degrades to text-only (the caller prints anyway)."""
    try:
        body = json.dumps({"voice_id": VOICE_ID, "text": text[:900], "format": "wav"}).encode()
        req = urllib.request.Request(VOICE_URL + "/api/v1/synthesize", data=body,
                                     headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=120) as r:
            path = os.path.join(os.environ.get("TEMP", "."), "percy_reply.wav")
            open(path, "wb").write(r.read())
        subprocess.Popen(["powershell", "-NoProfile", "-c",
                          f"(New-Object Media.SoundPlayer '{path}').PlaySync()"])
        return True
    except Exception:
        return False


def serve(port=7401):
    from http.server import BaseHTTPRequestHandler, HTTPServer

    class H(BaseHTTPRequestHandler):
        def log_message(self, *a): pass

        def do_POST(self):
            if self.path != "/ask":
                self.send_response(404); self.end_headers(); return
            n = int(self.headers.get("Content-Length", 0))
            q = json.loads(self.rfile.read(n)).get("question", "")
            try:
                answer = ask(q)
                out = json.dumps({"answer": answer}).encode()
                self.send_response(200)
            except Exception as e:
                out = json.dumps({"error": str(e)[:200]}).encode()
                self.send_response(500)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(out)

    print(f"percy brain listening on 127.0.0.1:{port} (model {MODEL}, CPU-only)")
    HTTPServer(("127.0.0.1", port), H).serve_forever()


if __name__ == "__main__":
    if "--serve" in sys.argv:
        serve()
    elif "--ask" in sys.argv:
        q = sys.argv[sys.argv.index("--ask") + 1]
        answer = ask(q)
        print(answer)
        if "--speak" in sys.argv:
            speak(answer)
    else:
        # No arguments = talk to Percy. Type a question, get an answer.
        print("Percy here. Ask me anything about the operation (blank line to leave).")
        while True:
            try:
                q = input("\nyou > ").strip()
            except (EOFError, KeyboardInterrupt):
                break
            if not q:
                break
            try:
                answer = ask(q)
            except Exception as e:
                answer = f"Something's wrong on my side: {str(e)[:120]} (is ollama running?)"
            print("\npercy > " + answer)
            if speak(answer):
                pass  # spoken too when the voice service is up and the GPU is free
        print("Bye.")
