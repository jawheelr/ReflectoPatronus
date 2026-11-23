using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Npgsql;

namespace ReflectoPatronus.Api.Accounting;

/// <summary>
/// The QuickBooks Desktop side of "send everything automatically". QuickBooks Desktop cannot be called from a server, so it calls us:
/// the QuickBooks Web Connector (a small Intuit program that comes with QuickBooks) asks this service every few minutes whether there
/// is anything to send, and we answer with QBXML requests one at a time. Nothing here reads QuickBooks data.
/// The endpoint is anonymous at the HTTP level and protected by its own user name and password (shown in the Accounting window).
/// </summary>
public sealed class QbWebConnector(NpgsqlDataSource db, ILogger<QbWebConnector> log)
{
    const string Ns = "http://developer.intuit.com/";
    const string Target = "qbd";
    record Unit(string Kind, int RefId);
    sealed class Session
    {
        public string Ticket = Guid.NewGuid().ToString("D").ToUpperInvariant();
        public DateTime Created = DateTime.UtcNow;
        public Queue<Unit> Queue = new();
        public int Total, Done, Sent, Errors;
        public string? LastError, Version;
        public Unit? Current; public decimal CurrentTotal; public string? CurrentCustomerName; public string? CurrentSetup;
        public AccountingConfig Cfg = new();
        public List<string> Warnings = [];
    }
    readonly ConcurrentDictionary<string, Session> sessions = new();

    public static void Map(WebApplication app)
    {
        app.MapGet("/qbwc", () => Results.Text("ReflectoPatronus QuickBooks Web Connector endpoint. Load the .qwc file from the Accounting window into the Web Connector.")).AllowAnonymous();
        app.MapPost("/qbwc", async (HttpContext ctx, QbWebConnector s) =>
        {
            using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await sr.ReadToEndAsync(ctx.RequestAborted);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Text(await s.Handle(body, ctx.RequestAborted), "text/xml; charset=utf-8");
        }).AllowAnonymous();
    }

    public async Task<string> Handle(string soap, CancellationToken ct)
    {
        XElement call;
        try { call = XDocument.Parse(soap).Root!.Elements().First(e => e.Name.LocalName == "Body").Elements().First(); }
        catch (Exception) { return Envelope("unknown", ""); }
        var method = call.Name.LocalName;
        string Arg(string n) => call.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value ?? "";
        try
        {
            switch (method)
            {
                case "serverVersion": return Envelope(method, "ReflectoPatronus 1");
                case "clientVersion": return Envelope(method, "");
                case "authenticate": return Envelope(method, null, await Authenticate(Arg("strUserName"), Arg("strPassword"), ct));
                case "sendRequestXML": return Envelope(method, await Next(Arg("ticket"), Arg("qbXMLMajorVers"), Arg("qbXMLMinorVers"), ct));
                case "receiveResponseXML": return Envelope(method, (await Receive(Arg("ticket"), Arg("response"), Arg("hresult"), Arg("message"), ct)).ToString(CultureInfo.InvariantCulture));
                case "getLastError": return Envelope(method, sessions.TryGetValue(Arg("ticket"), out var le) ? le.LastError ?? "" : "");
                case "connectionError": { if (sessions.TryGetValue(Arg("ticket"), out var ce)) ce.LastError = Arg("message"); return Envelope(method, "done"); }
                case "closeConnection": return Envelope(method, await Close(Arg("ticket"), ct));
                default: return Envelope(method, "");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "QuickBooks Web Connector call {Method} failed", method);
            if (sessions.TryGetValue(Arg("ticket"), out var s)) s.LastError = "ReflectoPatronus error: " + e.Message;
            return Envelope(method, method == "receiveResponseXML" ? "-1" : "");
        }
    }

    static string Envelope(string method, string? text, string[]? list = null)
    {
        XNamespace n = Ns;
        var result = new XElement(n + method + "Result");
        if (list is not null) foreach (var s in list) result.Add(new XElement(n + "string", s)); else result.Value = text ?? "";
        return new XDocument(new XElement("{http://schemas.xmlsoap.org/soap/envelope/}Envelope",
            new XElement("{http://schemas.xmlsoap.org/soap/envelope/}Body", new XElement(n + method + "Response", result)))).ToString(SaveOptions.DisableFormatting);
    }

    async Task<string[]> Authenticate(string user, string pass, CancellationToken ct)
    {
        var cfg = await AccountingStore.Load(db, ct);
        var okUser = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(user), Encoding.UTF8.GetBytes(cfg.User));
        var okPass = !string.IsNullOrEmpty(cfg.QbwcPassword) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pass), Encoding.UTF8.GetBytes(cfg.QbwcPassword));
        if (!okUser || !okPass) { await Task.Delay(1000, ct); return ["", "nvu"]; }
        foreach (var old in sessions.Where(kv => DateTime.UtcNow - kv.Value.Created > TimeSpan.FromHours(1)).Select(kv => kv.Key)) sessions.TryRemove(old, out _);
        var s = new Session { Cfg = cfg };
        await Touch(null, "Connected", s);
        if (!cfg.On) return [s.Ticket, "none"];

        var since = cfg.SinceDate;
        var docs = await AccountingData.PendingDocuments(db, Target, since, null, 200, ct);
        var pays = await AccountingData.PendingPayments(db, Target, since, null, 200, ct);
        var custs = await AccountingData.CustomersNeeded(db, Target, docs, pays, ct);
        var voids = await AccountingData.PendingVoids(db, Target, ct);
        if (docs.Count > 0) s.Queue.Enqueue(new("setup", 0));
        foreach (var c in custs) s.Queue.Enqueue(new("customer", c));
        // Documents are found by id; the type decides the kind when each is loaded.
        foreach (var d in docs) s.Queue.Enqueue(new("document", d));
        foreach (var p in pays) s.Queue.Enqueue(new("payment", p));
        foreach (var (k, id) in voids) s.Queue.Enqueue(new("void-" + k, id));
        s.Total = s.Queue.Count;
        if (s.Total == 0) return [s.Ticket, "none"];
        sessions[s.Ticket] = s;
        s.Total = s.Queue.Count;
        return [s.Ticket, ""];   // empty = use the company file that is open in QuickBooks
    }

    async Task<string> Next(string ticket, string major, string minor, CancellationToken ct)
    {
        if (!sessions.TryGetValue(ticket, out var s)) return "";
        var q = double.TryParse($"{major}.{minor}", NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? Math.Min(v, 13.0) : 13.0;
        s.Version = q.ToString("0.0", CultureInfo.InvariantCulture);
        while (s.Queue.Count > 0)
        {
            var u = s.Queue.Dequeue(); s.Current = u; s.CurrentSetup = null;
            var xml = await Build(s, u, ct);
            if (xml is not null) return xml;
            s.Done++;
        }
        s.Current = null;
        return "";
    }

    async Task<string?> Build(Session s, Unit u, CancellationToken ct)
    {
        var cfg = s.Cfg;
        switch (u.Kind)
        {
            case "setup": return await BuildSetup(s, ct);
            case "customer":
            {
                var c = await AccountingData.Customer(db, u.RefId, ct);
                if (c is null) { await AccountingStore.Mark(db, Target, "customer", u.RefId, "error", null, "Customer no longer exists.", ct); s.Errors++; return null; }
                s.CurrentCustomerName = AccountingData.CustomerName(cfg, c);
                return QbXml.Envelope(s.Version!, QbXml.Customer(cfg, c));
            }
            case "document":
            {
                var d = await AccountingData.Document(db, u.RefId, ct);
                if (d is null || d.Voided) return null;
                s.Current = new(d.Kind, d.Id);
                var name = await AccountingStore.ExternalId(db, Target, "customer", d.CustomerId, ct);
                if (name is null) { await AccountingStore.Mark(db, Target, d.Kind, d.Id, "error", null, "The customer was not accepted by QuickBooks, see the customer entry.", ct); s.Errors++; return null; }
                s.CurrentTotal = d.Total;
                var cust = await AccountingData.Customer(db, d.CustomerId, ct);
                return QbXml.Envelope(s.Version!, QbXml.Invoice(cfg, d, name, cust));
            }
            case "payment":
            {
                var p = await AccountingData.Payment(db, u.RefId, ct);
                if (p is null || p.Voided) return null;
                var name = await AccountingStore.ExternalId(db, Target, "customer", p.CustomerId, ct);
                if (name is null) { await AccountingStore.Mark(db, Target, "payment", p.Id, "error", null, "The customer was not accepted by QuickBooks, see the customer entry.", ct); s.Errors++; return null; }
                var applied = new List<(string, decimal)>(); var left = p.Amount;
                foreach (var (docId, amount) in p.Applied)
                {
                    var txn = await AccountingStore.ExternalId(db, Target, "invoice", docId, ct);
                    if (txn is null || left <= 0) continue;
                    var a = Math.Min(amount, left); applied.Add((txn, a)); left -= a;
                }
                return QbXml.Envelope(s.Version!, QbXml.Payment(cfg, p, name, applied));
            }
            default:
                if (u.Kind.StartsWith("void-"))
                {
                    var kind = u.Kind[5..];
                    var txn = await AccountingStore.ExternalId(db, Target, kind, u.RefId, ct);
                    if (txn is null) { await AccountingData.MarkVoidSent(db, Target, kind, u.RefId, ct); return null; }
                    return QbXml.Envelope(s.Version!, QbXml.Void(kind, u.RefId, txn));
                }
                return null;
        }
    }

    /// <summary>Creates, once, the account, items and sales tax items the documents refer to. Anything that already exists is left alone.</summary>
    async Task<string?> BuildSetup(Session s, CancellationToken ct)
    {
        var cfg = s.Cfg;
        var done = new HashSet<string>();
        if (await AccountingStore.Get(db, "qbd_setup", ct) is { ValueKind: JsonValueKind.Array } arr) foreach (var e in arr.EnumerateArray()) done.Add(e.GetString() ?? "");
        var reqs = new List<XElement>();
        void Add(string key, Func<XElement> make) { if (!done.Contains(key)) reqs.Add(make()); }
        Add("acct:" + cfg.Income, () => Tag("acct:" + cfg.Income, QbXml.Account(cfg.Income)));
        Add("vendor:" + cfg.Agency, () => Tag("vendor:" + cfg.Agency, QbXml.Vendor(cfg.Agency)));
        foreach (var src in new[] { "glass", "part", "labor", "other" }) { var n = cfg.Item(src); Add("item:" + n, () => Tag("item:" + n, QbXml.ServiceItem(n, cfg.Income, "item:" + n))); }

        var codes = new Dictionary<string, decimal>(); var groups = new Dictionary<string, List<string>>();
        await using (var c = db.CreateCommand("""
            SELECT DISTINCT dt.tax_code, dt.percent, d.tax_group, (SELECT count(*) FROM document_tax x WHERE x.document_id = d.id)
            FROM document d JOIN document_tax dt ON dt.document_id = d.id
            WHERE d.doc_type IN ('I', 'C') AND NOT d.voided AND d.customer_id IS NOT NULL AND d.doc_date >= $1
              AND NOT EXISTS (SELECT 1 FROM accounting_export e WHERE e.target = 'qbd' AND e.ref_id = d.id AND e.kind = CASE d.doc_type WHEN 'C' THEN 'credit' ELSE 'invoice' END)
            """))
        {
            c.Parameters.AddWithValue(cfg.SinceDate);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                codes[r.GetString(0)] = r.GetDecimal(1);
                if (r.GetInt64(3) > 1 && !r.IsDBNull(2)) { if (!groups.TryGetValue(r.GetString(2), out var l)) groups[r.GetString(2)] = l = []; if (!l.Contains(r.GetString(0))) l.Add(r.GetString(0)); }
            }
        }
        foreach (var (code, pct) in codes) { var n = cfg.TaxItem(code); Add("tax:" + n, () => Tag("tax:" + n, QbXml.SalesTaxItem(n, pct, cfg.Agency, "tax:" + n))); }
        foreach (var (g, members) in groups) { var n = cfg.TaxGroupItem(g); Add("group:" + n, () => Tag("group:" + n, QbXml.SalesTaxGroup(n, members.Select(cfg.TaxItem), "group:" + n))); }
        if (reqs.Count == 0) return null;
        s.CurrentSetup = "yes";
        return QbXml.Envelope(s.Version!, reqs.ToArray());
    }
    static XElement Tag(string id, XElement rq) { rq.SetAttributeValue("requestID", id); return rq; }

    async Task<int> Receive(string ticket, string response, string hresult, string message, CancellationToken ct)
    {
        if (!sessions.TryGetValue(ticket, out var s)) return -1;
        if (!string.IsNullOrEmpty(hresult)) { s.LastError = $"QuickBooks could not process the request: {message} ({hresult})"; return -1; }
        var replies = QbXml.Parse(response);
        var u = s.Current;
        if (s.CurrentSetup is not null)
        {
            var done = new List<string>();
            if (await AccountingStore.Get(db, "qbd_setup", ct) is { ValueKind: JsonValueKind.Array } arr) foreach (var e in arr.EnumerateArray()) done.Add(e.GetString() ?? "");
            foreach (var r in replies)
            {
                if (r.Ok || r.AlreadyExists) done.Add(r.RequestId);
                else s.Warnings.Add($"{r.RequestId}: {r.Message}");
            }
            await AccountingStore.Put(db, "qbd_setup", JsonSerializer.Serialize(done.Distinct()), ct);
            s.CurrentSetup = null;
        }
        else if (u is not null && replies.FirstOrDefault() is { } r)
        {
            await Record(s, u, r, ct);
        }
        s.Done++; s.Current = null;
        await Touch(s, null, s, ct);
        return s.Queue.Count == 0 ? 100 : Math.Min(99, s.Done * 100 / Math.Max(1, s.Total));
    }

    async Task Record(Session s, Unit u, QbXml.Reply r, CancellationToken ct)
    {
        if (u.Kind.StartsWith("void-"))
        {
            var kind = u.Kind[5..];
            await AccountingData.MarkVoidSent(db, Target, kind, u.RefId, ct);
            if (!r.Ok) { await Note(kind, u.RefId, $"Void was not accepted by QuickBooks ({r.Message}); void it there by hand.", ct); s.Errors++; } else s.Sent++;
            return;
        }
        if (!r.Ok && !(u.Kind == "customer" && r.AlreadyExists)) { await AccountingStore.Mark(db, Target, u.Kind, u.RefId, "error", null, Trim(r.Message), ct); s.Errors++; return; }
        string? note = null;
        if (u.Kind is "invoice" or "credit" && r.Number("Subtotal") is { } sub)
        {
            var total = sub + (r.Number("SalesTaxTotal") ?? 0);
            if (Math.Abs(total - s.CurrentTotal) > 0.01m) note = $"QuickBooks total {total:0.00} differs from {s.CurrentTotal:0.00} here (sales tax rounding or tax setup); check the invoice in QuickBooks.";
        }
        await AccountingStore.Mark(db, Target, u.Kind, u.RefId, "sent", u.Kind == "customer" ? s.CurrentCustomerName : r.TxnId, note, ct);
        s.Sent++;
    }
    static string Trim(string m) => m.Length > 500 ? m[..500] : m;

    async Task Note(string kind, int refId, string note, CancellationToken ct)
    {
        await using var c = db.CreateCommand("UPDATE accounting_export SET note = $4, updated_at = now() WHERE target = $1 AND kind = $2 AND ref_id = $3");
        c.Parameters.AddWithValue(Target); c.Parameters.AddWithValue(kind); c.Parameters.AddWithValue(refId); c.Parameters.AddWithValue(note);
        await c.ExecuteNonQueryAsync(ct);
    }

    async Task<string> Close(string ticket, CancellationToken ct)
    {
        if (sessions.TryRemove(ticket, out var s))
        {
            var msg = s.LastError ?? (s.Warnings.Count > 0 ? "Finished with notes: " + string.Join("; ", s.Warnings.Take(3)) : "Finished");
            await Touch(s, msg, s, ct, finished: true);
        }
        return "OK";
    }

    /// <summary>What the Accounting window shows as "last contact".</summary>
    async Task Touch(Session? s, string? message, Session counts, CancellationToken ct = default, bool finished = false)
    {
        var prev = await AccountingStore.Get(db, "qbwc_last", ct);
        var o = new Dictionary<string, object?>
        {
            ["at"] = DateTime.UtcNow, ["sent"] = counts.Sent, ["errors"] = counts.Errors,
            ["message"] = message ?? (prev is { } p && p.TryGetProperty("message", out var m) ? m.GetString() : null), ["finished"] = finished,
        };
        await AccountingStore.Put(db, "qbwc_last", JsonSerializer.Serialize(o, AccountingStore.Json), ct);
    }
}
