using System.Diagnostics;
using System.Text.Json;

const string localUrl = "http://127.0.0.1:8765";
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(localUrl);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddSingleton<MailStore>();
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", (MailStore store) => new { ok = true, mode = "demo", mailboxes_connected = 0, checks = store.Checks() });
app.MapGet("/api/summary", (MailStore store) => store.Summary());
app.MapGet("/api/accounts", (MailStore store) => store.Data.Accounts);
app.MapGet("/api/rules", (MailStore store) => store.Rules());
app.MapGet("/api/messages", (MailStore store, string? status) => store.Messages(status ?? "open"));
app.MapPost("/api/rules", (MailStore store, RuleInput input) =>
{
    string[] fields = ["sender", "subject", "body", "recipient"];
    string[] operators = ["contains", "equals", "domain"];
    string[] actions = ["warmup", "automated", "bill", "important", "ignore", "reply"];
    if (!fields.Contains(input.Field) || !operators.Contains(input.Operator) ||
        !actions.Contains(input.Action) || string.IsNullOrWhiteSpace(input.Pattern) || input.Pattern.Length > 500)
        return Results.BadRequest(new { error = "Unsupported or incomplete rule" });
    var rule = store.AddRule(input);
    return Results.Created($"/api/rules/{rule.Id}", new { ok = true, id = rule.Id });
});
app.MapPost("/api/messages/{id:int}/decision", (MailStore store, int id, DecisionInput input) =>
{
    string[] decisions = ["done", "no_reply", "approved", "snoozed"];
    if (!decisions.Contains(input.Decision)) return Results.BadRequest(new { error = "Unsupported decision" });
    return store.Decide(id, input.Decision) ? Results.Ok(new { ok = true }) : Results.NotFound();
});
// ── Intake: anything that needs Stephen pushes an item here (Baldrick job failures,
// approvals, n8n-fetched email, character prompts…). Bound to 127.0.0.1, so only
// processes on this machine can reach it.
app.MapPost("/api/items", (MailStore store, ItemInput input) =>
{
    if (string.IsNullOrWhiteSpace(input.Subject) || input.Subject.Length > 300)
        return Results.BadRequest(new { error = "A subject is required (max 300 chars)" });
    var item = store.AddItem(input);
    return Results.Created($"/api/items/{item.Id}", new { ok = true, id = item.Id });
});

app.MapFallbackToFile("index.html");

if (args.Contains("--open"))
    _ = Task.Run(async () =>
    {
        await Task.Delay(900);
        Process.Start(new ProcessStartInfo(localUrl) { UseShellExecute = true });
    });

Console.WriteLine($"Percy Agent is running at {localUrl}");
Console.WriteLine("Demo mode: no live mailboxes are connected.");
app.Run();

sealed class MailStore
{
    readonly object gate = new();
    readonly string path;
    static readonly JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    public AppData Data { get; private set; }

    public MailStore()
    {
        var configuredDir = Environment.GetEnvironmentVariable("PERCY_AGENT_DATA_DIR");
        var dir = string.IsNullOrWhiteSpace(configuredDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PercyAgent")
            : configuredDir;
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "percy-agent.json");
        Data = Load();
    }

    AppData Load()
    {
        if (File.Exists(path))
            try
            {
                var loaded = JsonSerializer.Deserialize<AppData>(File.ReadAllText(path), json);
                if (loaded is not null) return loaded;
            }
            catch { File.Copy(path, path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), false); }
        var seeded = Demo.Create();
        Save(seeded);
        return seeded;
    }

    void Save(AppData? value = null)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value ?? Data, json));
        File.Move(temp, path, true);
    }

    public object Checks()
    {
        var queue = Summary().Queue;
        var personal = queue.Where(x => x.AccountAddress == "venus.stephenh@outlook.com").ToList();
        var outreach = queue.Where(x => x.AccountPolicy == "outreach" && x.NeedsReply).ToList();
        return new
        {
            finite_queue = queue.Count <= 10,
            triage_only_has_no_drafts = personal.Count > 0 && personal.All(x => !x.NeedsReply),
            outreach_has_draft = outreach.Count == 1 && !string.IsNullOrWhiteSpace(outreach[0].DraftBody),
            local_data_path = path
        };
    }

    public Summary Summary()
    {
        lock (gate)
        {
            var accounts = Data.Accounts.ToDictionary(x => x.Id);
            var now = DateTimeOffset.UtcNow;
            var queue = Data.Messages.Where(x => x.Status == "open" && x.NeedsHuman && (x.SnoozedUntil is null || x.SnoozedUntil <= now))
                .OrderBy(x => x.Category switch { "failure" => 0, "bill" => 1, "reply" => 2, "approval" => 2, "uncertain" => 3, _ => 4 })
                .ThenByDescending(x => x.ReceivedAt).Take(10)
                .Select(x => View(x, accounts.GetValueOrDefault(x.AccountId))).ToList();
            var handled = Data.Messages.Where(x => !x.NeedsHuman).GroupBy(x => x.Category)
                .Select(x => new CategoryCount(x.Key, x.Count())).OrderByDescending(x => x.Count).ToList();
            return new(DateTimeOffset.UtcNow, queue,
                new(queue.Count, queue.Count(x => x.NeedsReply), queue.Count(x => x.Category == "bill"), queue.Count(x => x.Category == "uncertain")),
                handled);
        }
    }

    // Account is optional: items pushed in from Baldrick, n8n or any other source
    // have no mailbox behind them.
    static MessageView View(MailMessage m, Account? a) =>
        new(m.Id, m.AccountId, a?.Label ?? m.Source, a?.Address ?? "—", a?.Policy ?? "system",
            m.Sender, m.Subject, m.Preview, m.ReceivedAt,
            m.Category, m.NeedsHuman, m.NeedsReply, m.Confidence, m.Reason, m.DraftBody, m.Status, m.SnoozedUntil,
            m.Source, m.Link);

    public IEnumerable<object> Rules()
    {
        lock (gate)
        {
            var labels = Data.Accounts.ToDictionary(x => x.Id, x => x.Label);
            return Data.Rules.Select(x => new
            {
                x.Id, x.AccountId,
                account_label = x.AccountId is int id && labels.TryGetValue(id, out var label) ? label : null,
                x.Field, x.Operator, x.Pattern, x.Action, x.Enabled, x.CreatedAt
            }).ToList();
        }
    }

    public IEnumerable<MessageView> Messages(string status)
    {
        lock (gate)
        {
            var accounts = Data.Accounts.ToDictionary(x => x.Id);
            return Data.Messages.Where(x => x.Status == status).OrderByDescending(x => x.ReceivedAt)
                .Select(x => View(x, accounts.GetValueOrDefault(x.AccountId))).ToList();
        }
    }

    public Rule AddRule(RuleInput input)
    {
        lock (gate)
        {
            var rule = new Rule(Data.Rules.Select(x => x.Id).DefaultIfEmpty().Max() + 1, input.AccountId,
                input.Field, input.Operator, input.Pattern.Trim(), input.Action, true, DateTimeOffset.UtcNow);
            Data.Rules.Add(rule); Save(); return rule;
        }
    }

    public MailMessage AddItem(ItemInput input)
    {
        lock (gate)
        {
            var item = new MailMessage(
                Data.Messages.Select(x => x.Id).DefaultIfEmpty().Max() + 1,
                accountId: 0,                                   // no mailbox behind it
                sender: (input.Sender ?? input.Source ?? "system").Trim(),
                subject: input.Subject.Trim(),
                preview: (input.Preview ?? "").Trim(),
                receivedAt: DateTimeOffset.UtcNow,
                category: (input.Category ?? "uncertain").Trim(),
                needsHuman: input.NeedsHuman ?? true,
                needsReply: false,
                confidence: 1,
                reason: (input.Reason ?? "Pushed in by " + (input.Source ?? "an external system")).Trim(),
                draftBody: null)
            {
                Source = (input.Source ?? "system").Trim(),
                Link = string.IsNullOrWhiteSpace(input.Link) ? null : input.Link.Trim()
            };
            Data.Messages.Add(item); Save(); return item;
        }
    }

    public bool Decide(int id, string decision)
    {
        lock (gate)
        {
            var message = Data.Messages.SingleOrDefault(x => x.Id == id);
            if (message is null) return false;
            message.Status = decision;
            message.SnoozedUntil = decision == "snoozed" ? DateTimeOffset.UtcNow.AddDays(1) : null;
            Save(); return true;
        }
    }
}

static class Demo
{
    public static AppData Create()
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            [
                new(1, "Personal Outlook", "venus.stephenh@outlook.com", "microsoft", "triage", true, false, 7, true, "demo"),
                new(2, "Outreach mailbox", "stephen@example-outreach.co.uk", "imap", "outreach", true, true, 7, true, "demo"),
                new(3, "Warm-up account", "warmup@example-mail.co.uk", "imap", "cleanup", true, false, 7, true, "demo"),
                new(4, "Accounts", "accounts@example-business.co.uk", "imap", "monitor", false, false, 30, true, "demo")
            ],
            [
                new(1, null, "body", "contains", "this is a warm-up email", "warmup", true, now),
                new(2, null, "subject", "contains", "delivery status notification", "automated", true, now),
                new(3, null, "subject", "contains", "invoice", "bill", true, now),
                new(4, null, "subject", "contains", "payment due", "bill", true, now),
                new(5, 3, "sender", "domain", "warmup-network.example", "warmup", true, now)
            ],
            [
                new(1, 1, "Alex Morgan <alex@example.net>", "Lunch next Thursday?", "Are you around next Thursday? No rush—just let me know.", now, "genuine", true, false, .98, "Personal message worth seeing; this account is triage-only.", null),
                new(2, 2, "Priya Shah <priya@example-manufacturing.co.uk>", "Re: succession planning", "I may be open to a confidential conversation. What did you have in mind?", now.AddMinutes(-18), "reply", true, true, .97, "A genuine prospect has asked a direct question.", "Thanks for coming back to me, Priya.\n\nA confidential initial conversation would be the sensible next step. I can work around you—would Tuesday or Wednesday afternoon suit?\n\nStephen"),
                new(3, 4, "Northfield Hosting <billing@northfield.example>", "Invoice 1842 — payment due 14 August", "Your latest invoice for £680 is attached and due on 14 August.", now.AddHours(-1), "bill", true, false, .99, "Invoice with a future payment deadline.", null),
                new(4, 3, "Warm Network <person@warmup-network.example>", "Quick question", "Hi, this is a warm-up email. Hope you are having a productive week.", now.AddHours(-2), "warmup", false, false, 1, "Matched a configured warm-up phrase and sender domain.", null),
                new(5, 2, "Mail Delivery System <mailer-daemon@example.net>", "Delivery Status Notification (Failure)", "Your message could not be delivered.", now.AddHours(-3), "automated", false, false, 1, "Known delivery-failure notification.", null),
                new(6, 1, "Unknown sender <hello@unexpected.example>", "A question about your website", "Could you tell me who looks after partnership requests?", now.AddHours(-4), "uncertain", true, false, .61, "Not enough history to decide whether this matters.", null)
            ]);
    }
}

sealed record AppData(List<Account> Accounts, List<Rule> Rules, List<MailMessage> Messages);
sealed record Account(int Id, string Label, string Address, string Provider, string Policy, bool DetectWarmup, bool DraftReplies, int CleanupDays, bool Active, string ConnectionState);
sealed record Rule(int Id, int? AccountId, string Field, string Operator, string Pattern, string Action, bool Enabled, DateTimeOffset CreatedAt);
sealed record RuleInput(int? AccountId, string Field, string Operator, string Pattern, string Action);
sealed record DecisionInput(string Decision);
sealed record CategoryCount(string Category, int Count);
sealed record QueueCounts(int TotalAttention, int Drafts, int Bills, int Uncertain);
sealed record Summary(DateTimeOffset GeneratedAt, List<MessageView> Queue, QueueCounts Counts, List<CategoryCount> Handled);
sealed record MessageView(int Id, int AccountId, string AccountLabel, string AccountAddress, string AccountPolicy, string Sender, string Subject, string Preview, DateTimeOffset ReceivedAt, string Category, bool NeedsHuman, bool NeedsReply, double Confidence, string Reason, string? DraftBody, string Status, DateTimeOffset? SnoozedUntil, string Source, string? Link);
sealed record ItemInput(string? Source, string? Sender, string Subject, string? Preview, string? Category, bool? NeedsHuman, string? Reason, string? Link);

sealed class MailMessage(int id, int accountId, string sender, string subject, string preview, DateTimeOffset receivedAt,
    string category, bool needsHuman, bool needsReply, double confidence, string reason, string? draftBody)
{
    public int Id { get; set; } = id;
    public int AccountId { get; set; } = accountId;
    public string Sender { get; set; } = sender;
    public string Subject { get; set; } = subject;
    public string Preview { get; set; } = preview;
    public DateTimeOffset ReceivedAt { get; set; } = receivedAt;
    public string Category { get; set; } = category;
    public bool NeedsHuman { get; set; } = needsHuman;
    public bool NeedsReply { get; set; } = needsReply;
    public double Confidence { get; set; } = confidence;
    public string Reason { get; set; } = reason;
    public string? DraftBody { get; set; } = draftBody;
    public string Status { get; set; } = "open";
    public DateTimeOffset? SnoozedUntil { get; set; }
    // Where this item came from: "email" (a mailbox) or any pushed source —
    // "baldrick", "n8n", "characters"… Defaults to email so existing data loads unchanged.
    public string Source { get; set; } = "email";
    // Optional deep link — the page to open to actually deal with it.
    public string? Link { get; set; }
}
