using System.IO;
using Microsoft.Data.Sqlite;

namespace PercyAgent;

/// <summary>
/// One portable SQLite file holds everything: the queue, the rules and the
/// decisions. Copy the data folder to another machine and carry on where you
/// left off — there is no server and nothing to install.
/// </summary>
public sealed class Store
{
    readonly string connectionString;
    public string DatabasePath { get; }

    public Store()
    {
        // Portable by default: a "data" folder beside the executable, so the whole
        // app folder can be copied to the laptop. PERCY_AGENT_DATA_DIR overrides it
        // (e.g. point both machines at a synced folder).
        var configured = Environment.GetEnvironmentVariable("PERCY_AGENT_DATA_DIR");
        var dir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : configured;
        Directory.CreateDirectory(dir);
        DatabasePath = Path.Combine(dir, "percy-agent.db");
        connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString();
        Initialise();
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }

    void Initialise()
    {
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS items (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                source        TEXT NOT NULL,
                sender        TEXT NOT NULL,
                subject       TEXT NOT NULL,
                preview       TEXT NOT NULL DEFAULT '',
                reason        TEXT NOT NULL DEFAULT '',
                category      TEXT NOT NULL,
                needs_human   INTEGER NOT NULL DEFAULT 1,
                link          TEXT,
                external_id   TEXT,
                received_at   TEXT NOT NULL,
                status        TEXT NOT NULL DEFAULT 'open',
                decided_at    TEXT,
                snoozed_until TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_items_external
                ON items(source, external_id) WHERE external_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_items_status ON items(status, needs_human);

            CREATE TABLE IF NOT EXISTS rules (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                field      TEXT NOT NULL,
                op         TEXT NOT NULL,
                pattern    TEXT NOT NULL,
                category   TEXT NOT NULL,
                surface    INTEGER NOT NULL DEFAULT 0,
                enabled    INTEGER NOT NULL DEFAULT 1,
                note       TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            -- The methods map (SYSTEM-SPEC.md): HOW each kind of thing is made.
            -- Percy Worker reads this table to dispatch; the Methods tab shows it.
            -- This is the knowledge that used to be buried in percy_worker.py.
            CREATE TABLE IF NOT EXISTS methods (
                method_key    TEXT PRIMARY KEY,
                aliases       TEXT NOT NULL DEFAULT '',
                label         TEXT NOT NULL,
                engine        TEXT NOT NULL,
                workflow_file TEXT NOT NULL DEFAULT '',
                injections    TEXT NOT NULL DEFAULT '{}',
                steps         TEXT NOT NULL DEFAULT '',
                settings      TEXT NOT NULL DEFAULT '{}',
                notes         TEXT NOT NULL DEFAULT '',
                enabled       INTEGER NOT NULL DEFAULT 1,
                updated_at    TEXT NOT NULL
            );
            """);
        SeedRules(c);
        SeedMethods(c);
    }

    // ── Methods: the map of HOW things are made ─────────────────────────────
    // Seeded once with the truth as of 2026-08-08 (extracted from percy_worker.py
    // when the knowledge moved out of code). After that, this table is the truth
    // and the app is where it gets edited.
    static void SeedMethods(SqliteConnection c)
    {
        using (var check = c.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM methods";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        }

        const string wfRoot = @"C:\ComfyUI\ComfyUI\user\default\workflows\baldrick\";
        (string key, string aliases, string label, string engine, string wf, string inj, string steps, string settings, string notes)[] seed =
        [
            ("zimage_lora_still", "character_still",
             "Character still — her own LoRA on Z-Image",
             "local:comfy",
             wfRoot + @"percy\zimage-character-still-v001.api.json",
             """{"2.inputs.lora_name":"lora.name","2.inputs.strength_model":"lora.strength","3.inputs.shift":"sampler.shift","5.inputs.text":"prompt","7.inputs.width":"width","7.inputs.height":"height","9.inputs.seed":"seed","9.inputs.steps":"sampler.steps","9.inputs.cfg":"sampler.cfg","9.inputs.sampler_name":"sampler.sampler","9.inputs.scheduler":"sampler.scheduler","11.inputs.filename_prefix":"prefix"}""",
             "jpeg,collect",
             "{}",
             "Proven on Lucy's channel/site shots and Erin's self-test. A new character = a Baldrick recipe row naming her LoRA + trigger — no change here."),

            ("product_ugc_qwen", "ugc_apparel",
             "Person wearing the real garment — Qwen edit",
             "local:comfy",
             wfRoot + @"percy\qwen-product-ugc-v001.api.json",
             """{"5.inputs.image":"source_image","6.inputs.prompt":"prompt","7.inputs.prompt":"negative","8.inputs.width":"width","8.inputs.height":"height","9.inputs.seed":"seed","11.inputs.filename_prefix":"prefix"}""",
             "jpeg,collect",
             "{}",
             "The 8Ball recipe: real product photo as image1, 20 steps cfg 2.5, deliberately NOT the 4-step Lightning LoRA (reads as an advert)."),

            ("birefnet_cutout+price_overlay", "product_image",
             "Plain product advert — real photo, price overlay",
             "local:toolbox",
             "",
             "{}",
             "cutout,hybrid_advert,jpeg,collect",
             "{}",
             "Deliberately NO AI render and NO prompt: re-rendering made products plasticky and dropped hardware (learned 2026-07-26). Real photo, always."),

            ("ugc_post", "scene+cutout",
             "Product on a surface — SD3.5 scene + true-scale cutout",
             "local:comfy+toolbox",
             wfRoot + @"percy\sd35-ugc-scene-v001.api.json",
             """{"6.inputs.text":"scene_prompt","3.inputs.seed":"seed","9.inputs.filename_prefix":"prefix"}""",
             "ugc_composite,jpeg,collect",
             """{"surfaces":{"Iron On Patch":[{"prompt":"candid close-up photograph of the back of a person wearing an ordinary everyday mid-blue indigo denim jacket, natural medium wash with soft fading, real cotton denim weave, visible seams and orange topstitching, soft natural daylight outdoors, softly blurred street background, photographic, authentic real jacket","scene_mm":280,"posx":0.5,"posy":0.52}]},"fallback":"birefnet_cutout+price_overlay"}""",
             "Pin badges removed from UGC 2026-07-27 (Stephen): placement never convincing — they fall back to the plain advert. Surface prompts live in settings here, not in code."),

            ("product_enhanced_advert", "",
             "Enhanced advert — GPT Image 2 with the developed prompt",
             "cloud:gpt-image-2",
             "",
             "{}",
             "",
             "{}",
             "CLOUD route — no local worker exists. The developed prompt lives in the Baldrick recipe. Made the 32 JKB enhanced adverts. Runs agent-driven until a cloud worker is built."),

            ("capable_model_simple_prompt", "",
             "Infographic — capable cloud model, simple purpose prompt",
             "cloud:gpt-image-2",
             "",
             "{}",
             "",
             "{}",
             "CLOUD route — no local worker. Marked 'proven' in Baldrick because it was done by hand (the 6 JKB infographics); the worker cannot run it."),
        ];

        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var m in seed)
            Exec(c, """
                INSERT INTO methods (method_key, aliases, label, engine, workflow_file, injections, steps, settings, notes, enabled, updated_at)
                VALUES ($k, $a, $l, $e, $w, $i, $s, $set, $n, 1, $t)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("$k", m.key);
                cmd.Parameters.AddWithValue("$a", m.aliases);
                cmd.Parameters.AddWithValue("$l", m.label);
                cmd.Parameters.AddWithValue("$e", m.engine);
                cmd.Parameters.AddWithValue("$w", m.wf);
                cmd.Parameters.AddWithValue("$i", m.inj);
                cmd.Parameters.AddWithValue("$s", m.steps);
                cmd.Parameters.AddWithValue("$set", m.settings);
                cmd.Parameters.AddWithValue("$n", m.notes);
                cmd.Parameters.AddWithValue("$t", now);
            });
    }

    public List<MethodRow> Methods()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT method_key, aliases, label, engine, workflow_file, injections, steps, settings, notes, enabled
            FROM methods ORDER BY engine, method_key
            """;
        var list = new List<MethodRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new MethodRow
            {
                MethodKey = r.GetString(0), Aliases = r.GetString(1), Label = r.GetString(2),
                Engine = r.GetString(3), WorkflowFile = r.GetString(4), Injections = r.GetString(5),
                Steps = r.GetString(6), Settings = r.GetString(7), Notes = r.GetString(8),
                Enabled = r.GetInt64(9) != 0,
            });
        return list;
    }

    public void SaveMethod(MethodRow m) =>
        ExecOne("""
            INSERT INTO methods (method_key, aliases, label, engine, workflow_file, injections, steps, settings, notes, enabled, updated_at)
            VALUES ($k, $a, $l, $e, $w, $i, $s, $set, $n, $en, $t)
            ON CONFLICT(method_key) DO UPDATE SET
                aliases=$a, label=$l, engine=$e, workflow_file=$w, injections=$i,
                steps=$s, settings=$set, notes=$n, enabled=$en, updated_at=$t
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$k", m.MethodKey);
            cmd.Parameters.AddWithValue("$a", m.Aliases);
            cmd.Parameters.AddWithValue("$l", m.Label);
            cmd.Parameters.AddWithValue("$e", m.Engine);
            cmd.Parameters.AddWithValue("$w", m.WorkflowFile);
            cmd.Parameters.AddWithValue("$i", m.Injections);
            cmd.Parameters.AddWithValue("$s", m.Steps);
            cmd.Parameters.AddWithValue("$set", m.Settings);
            cmd.Parameters.AddWithValue("$n", m.Notes);
            cmd.Parameters.AddWithValue("$en", m.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        });

    void ExecOne(string sql, Action<SqliteCommand> bind)
    {
        using var c = Open();
        Exec(c, sql, bind);
    }

    static void Exec(SqliteConnection c, string sql, Action<SqliteCommand>? bind = null)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        cmd.ExecuteNonQuery();
    }

    // ── Rules ────────────────────────────────────────────────────────────────
    // Derived from a real pass over Stephen's inbox (5 Aug 2026): ~80% of mail
    // is broadcast that needs nobody. The test that matters is not "is it
    // important" but "did a human or an institution write to *him*".
    static void SeedRules(SqliteConnection c)
    {
        using (var check = c.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM rules";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        }

        (string field, string op, string pattern, string category, int surface, string note)[] seed =
        [
            // Always surface — institutions and money
            ("sender", "domain", "notifications.service.gov.uk", "official", 1, "Companies House / gov.uk"),
            ("sender", "domain", "tide.co",        "official", 1, "Bank"),
            ("sender", "domain", "stripe.com",     "bill",     1, "Payments"),
            ("subject", "contains", "payment failed",   "bill", 1, "Failed payment"),
            ("subject", "contains", "payment due",      "bill", 1, "Invoice due"),
            ("subject", "contains", "invoice",          "bill", 1, "Invoice"),
            ("subject", "contains", "confirmation statement", "official", 1, "Filing deadline"),
            ("subject", "contains", "verify their identity",  "official", 1, "Statutory verification"),

            // Dispose — broadcast marketing and course/guru mail
            ("sender", "domain", "highticket.io",        "marketing", 0, "Course marketing"),
            ("sender", "domain", "industryrockstar.com", "marketing", 0, "Course marketing"),
            ("sender", "domain", "jtfoxxorg.com",        "marketing", 0, "Course marketing"),
            ("sender", "domain", "liamottley.com",       "marketing", 0, "Course marketing"),
            ("sender", "domain", "getmakerai.com",       "marketing", 0, "Course marketing"),
            ("sender", "domain", "5dprogram.com",        "marketing", 0, "Course marketing"),
            ("sender", "domain", "brandonlucero.com",    "marketing", 0, "Course marketing"),
            ("sender", "domain", "wesmcdowell.com",      "marketing", 0, "Course marketing"),
            ("sender", "domain", "storybrand.com",       "marketing", 0, "Course marketing"),

            // Dispose — trade newsletters
            ("sender", "domain", "email.packagingnews.co.uk", "newsletter", 0, "Trade newsletter"),
            ("sender", "domain", "thegrocer.co.uk",  "newsletter", 0, "Trade newsletter"),
            ("sender", "domain", "corpdata.co.uk",   "newsletter", 0, "List sales"),
            ("sender", "domain", "datateam.co.uk",   "newsletter", 0, "Trade awards"),
            ("sender", "domain", "freeola.co.uk",    "newsletter", 0, "Hosting newsletter"),
            ("sender", "domain", "sahilbloom.com",   "newsletter", 0, "Newsletter"),

            // Dispose — social and community digests
            ("sender", "domain", "skool.com",           "digest", 0, "Community digest"),
            ("sender", "domain", "linkedin.com",        "digest", 0, "LinkedIn"),
            ("sender", "domain", "mail.instagram.com",  "digest", 0, "Instagram"),
            ("sender", "domain", "discover.pinterest.com", "digest", 0, "Pinterest"),

            // Dispose — expiring login links are useless by the time they are read
            ("subject", "contains", "your login link",  "transient", 0, "Magic link"),
            ("subject", "contains", "secure link",      "transient", 0, "Magic link"),
            ("subject", "contains", "sign in to",       "transient", 0, "Magic link"),
        ];

        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var (field, op, pattern, category, surface, note) in seed)
            Exec(c, """
                INSERT INTO rules (field, op, pattern, category, surface, enabled, note, created_at)
                VALUES ($f, $o, $p, $c, $s, 1, $n, $t)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("$f", field);
                cmd.Parameters.AddWithValue("$o", op);
                cmd.Parameters.AddWithValue("$p", pattern);
                cmd.Parameters.AddWithValue("$c", category);
                cmd.Parameters.AddWithValue("$s", surface);
                cmd.Parameters.AddWithValue("$n", note);
                cmd.Parameters.AddWithValue("$t", now);
            });
    }

    // ── Queue ────────────────────────────────────────────────────────────────

    /// <summary>The finite list. Capped deliberately: a queue you cannot finish is a feed.</summary>
    public List<QueueItem> Queue(int cap = 12)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, source, sender, subject, preview, reason, category, link, received_at
            FROM items
            WHERE status = 'open' AND needs_human = 1
              AND (snoozed_until IS NULL OR snoozed_until <= $now)
            ORDER BY CASE category
                       WHEN 'failure'  THEN 0
                       WHEN 'official' THEN 1
                       WHEN 'bill'     THEN 2
                       WHEN 'approval' THEN 3
                       WHEN 'reply'    THEN 4
                       WHEN 'uncertain'THEN 5
                       ELSE 6 END,
                     received_at DESC
            LIMIT $cap
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$cap", cap);
        var list = new List<QueueItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new QueueItem(
                r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                DateTimeOffset.Parse(r.GetString(8))));
        return list;
    }

    /// <summary>How much never reached him — the number that proves the thing is working.</summary>
    public int HandledCount()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items WHERE needs_human = 0";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int Add(string source, string sender, string subject, string preview,
                   string reason, string category, bool needsHuman, string? link,
                   string? externalId = null, DateTimeOffset? receivedAt = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO items
                (source, sender, subject, preview, reason, category, needs_human, link, external_id, received_at)
            VALUES ($src, $from, $subj, $prev, $why, $cat, $need, $link, $ext, $at);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$from", sender);
        cmd.Parameters.AddWithValue("$subj", subject);
        cmd.Parameters.AddWithValue("$prev", preview ?? "");
        cmd.Parameters.AddWithValue("$why", reason ?? "");
        cmd.Parameters.AddWithValue("$cat", category);
        cmd.Parameters.AddWithValue("$need", needsHuman ? 1 : 0);
        cmd.Parameters.AddWithValue("$link", (object?)link ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ext", (object?)externalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", (receivedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Decide(int id, string decision)
    {
        using var c = Open();
        Exec(c, """
            UPDATE items
               SET status = CASE WHEN $d = 'snoozed' THEN 'open' ELSE $d END,
                   decided_at = $now,
                   snoozed_until = CASE WHEN $d = 'snoozed' THEN $tomorrow ELSE NULL END
             WHERE id = $id
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$d", decision);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$tomorrow", DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        });
    }

    public int RuleCount()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rules WHERE enabled = 1";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}

public sealed record QueueItem(
    int Id, string Source, string Sender, string Subject, string Preview,
    string Reason, string Category, string? Link, DateTimeOffset ReceivedAt)
{
    public string CategoryLabel => Category switch
    {
        "failure" => "FAILED", "official" => "OFFICIAL", "bill" => "MONEY",
        "approval" => "APPROVE", "reply" => "REPLY", "uncertain" => "UNSURE",
        _ => Category.ToUpperInvariant()
    };
    public string Age => (DateTimeOffset.UtcNow - ReceivedAt) switch
    {
        { TotalMinutes: < 60 } t => $"{(int)t.TotalMinutes}m ago",
        { TotalHours: < 24 } t => $"{(int)t.TotalHours}h ago",
        var t => $"{(int)t.TotalDays}d ago"
    };
    public bool HasLink => !string.IsNullOrWhiteSpace(Link);
}

/// <summary>One row of the methods map — HOW one kind of thing is made.
/// Mutable on purpose: the Methods tab edits these in a grid and saves back.</summary>
public sealed class MethodRow
{
    public string MethodKey { get; set; } = "";
    public string Aliases { get; set; } = "";
    public string Label { get; set; } = "";
    public string Engine { get; set; } = "";
    public string WorkflowFile { get; set; } = "";
    public string Injections { get; set; } = "{}";
    public string Steps { get; set; } = "";
    public string Settings { get; set; } = "{}";
    public string Notes { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
