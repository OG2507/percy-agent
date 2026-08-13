#!/usr/bin/env python3
"""The methods table, as a file you can read, diff and restore.

WHY THIS EXISTS. The seven render methods — which workflow file, which values go
into which node, which finishing steps — are what turns an approved job into an
image. They live in ONE SQLite file on ONE machine: percy-agent.db, which is
gitignored because it sits under publish/. percy_worker.py reads them from there
and nowhere else. Lose that file and every local render stops, with nothing in
any repo or any database to rebuild from.

So the table gets exported to methods/methods.json, which IS in the repo. JSON
rather than a copy of the .db because the point is to be able to read it: the
injections and settings come out as real nested objects, so a changed prompt or
a swapped workflow file shows up as one changed line in a diff instead of an
opaque blob.

    python scripts/methods.py export     # database  ->  methods/methods.json
    python scripts/methods.py import     # methods/methods.json  ->  database
    python scripts/methods.py check      # do they differ? (exit 1 if they do)

Export after changing anything in Percy Agent's Methods tab, then commit. Import
onto a new machine, or to undo a change you regret.

PERCY_DB overrides the database path.
"""
import json, os, sqlite3, sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
DB = os.environ.get("PERCY_DB", os.path.join(REPO, "publish", "data", "percy-agent.db"))
OUT = os.path.join(REPO, "methods", "methods.json")

# The columns the file carries. updated_at is deliberately NOT among them: it
# changes whenever the app touches a row, which would put noise in every diff
# and make `check` cry wolf. The file records what a method IS, not when it was
# last saved.
COLUMNS = ["method_key", "aliases", "label", "engine", "workflow_file",
           "injections", "steps", "settings", "notes", "enabled"]

# These are stored as JSON strings in SQLite. Held as real objects in the file
# so the diff is line-by-line.
AS_JSON = ("injections", "settings")


def connect():
    if not os.path.exists(DB):
        sys.exit(f"no database at {DB}\n"
                 f"open Percy Agent once to create it, or set PERCY_DB")
    c = sqlite3.connect(DB)
    c.row_factory = sqlite3.Row
    return c


def read_db():
    c = connect()
    try:
        rows = c.execute(f"SELECT {', '.join(COLUMNS)} FROM methods "
                         f"ORDER BY method_key").fetchall()
    finally:
        c.close()
    out = []
    for r in rows:
        m = {k: r[k] for k in COLUMNS}
        for k in AS_JSON:
            try:
                m[k] = json.loads(m[k] or "{}")
            except (TypeError, ValueError):
                pass          # leave malformed JSON exactly as it is, visibly
        out.append(m)
    return out


def read_file():
    if not os.path.exists(OUT):
        sys.exit(f"no exported methods at {OUT} — run `export` first")
    with open(OUT, encoding="utf-8") as f:
        return json.load(f)["methods"]


def do_export():
    methods = read_db()
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump({
            "_": "Percy's render methods, exported from percy-agent.db. "
                 "Edit in Percy Agent's Methods tab and re-export; or edit here "
                 "and run `python scripts/methods.py import`.",
            "source": os.path.basename(DB),
            "methods": methods,
        }, f, indent=2, ensure_ascii=False, sort_keys=False)
        f.write("\n")
    print(f"{len(methods)} methods -> {os.path.relpath(OUT, REPO)}")
    for m in methods:
        mark = "" if m["enabled"] else "  (disabled)"
        print(f"  {m['method_key']:<32} {m['engine'] or '-'}{mark}")


def do_import():
    methods = read_file()
    c = connect()
    try:
        cur = c.cursor()
        added = updated = 0
        for m in methods:
            row = dict(m)
            for k in AS_JSON:
                if not isinstance(row.get(k), str):
                    row[k] = json.dumps(row.get(k) or {})
            exists = cur.execute("SELECT 1 FROM methods WHERE method_key = ?",
                                 (row["method_key"],)).fetchone()
            cols = ", ".join(f"{k} = ?" for k in COLUMNS if k != "method_key")
            if exists:
                cur.execute(f"UPDATE methods SET {cols}, updated_at = datetime('now') "
                            f"WHERE method_key = ?",
                            [row[k] for k in COLUMNS if k != "method_key"] + [row["method_key"]])
                updated += 1
            else:
                cur.execute(f"INSERT INTO methods ({', '.join(COLUMNS)}, updated_at) "
                            f"VALUES ({', '.join('?' * len(COLUMNS))}, datetime('now'))",
                            [row[k] for k in COLUMNS])
                added += 1
        c.commit()
    finally:
        c.close()
    # Nothing is ever deleted here. A method missing from the file is left alone
    # rather than dropped — restoring a backup must not silently destroy a
    # method somebody added since.
    extra = {m["method_key"] for m in read_db()} - {m["method_key"] for m in methods}
    print(f"{added} added, {updated} updated")
    if extra:
        print(f"left alone (in the database, not in the file): {', '.join(sorted(extra))}")


def do_check():
    if read_db() == read_file():
        print("the file matches the database")
        return
    sys.exit("the file and the database DIFFER — run `export` to update the file, "
             "or `import` to put the file back into the database")


if __name__ == "__main__":
    action = (sys.argv[1] if len(sys.argv) > 1 else "").lower()
    if action == "export":
        do_export()
    elif action == "import":
        do_import()
    elif action == "check":
        do_check()
    else:
        sys.exit(__doc__)
