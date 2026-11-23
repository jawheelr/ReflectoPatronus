using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Npgsql;
using ReflectoPatronus.Api.Auth;

namespace ReflectoPatronus.Api.Accounting;

public record QueueRow(string Kind, int RefId, string Label, string? Party, DateTime? Date, decimal? Amount, string Status, string? Note, DateTime? SentAt);
public record RetryBody(string? Kind, int? RefId);

/// <summary>The Accounting window's API. Everything needs 'accounting.manage'; the Web Connector endpoint itself is in <see cref="QbWebConnector"/>.</summary>
public static class AccountingEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/accounting").RequirePermission("accounting.manage");
        g.MapGet("", Overview);
        g.MapPut("/config", async (AccountingConfig body, NpgsqlDataSource db, CancellationToken ct) =>
        {
            var old = await AccountingStore.Load(db, ct);
            body.QbwcPassword = string.IsNullOrWhiteSpace(body.QbwcPassword) ? old.QbwcPassword : body.QbwcPassword;
            body.OwnerId = old.OwnerId;
            if (old.Target != "qbd" && body.Target == "qbd" && string.IsNullOrWhiteSpace(body.Since)) body.Since = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
            if (body.Since is { Length: > 0 } s && !DateOnly.TryParse(s, out _)) return Results.BadRequest(new { error = "The start date is not a valid date." });
            await AccountingStore.Save(db, body, ct);
            return Results.Ok(await AccountingStore.Load(db, ct));
        });
        g.MapPost("/qbwc/password", async (NpgsqlDataSource db, CancellationToken ct) =>
        {
            var cfg = await AccountingStore.Load(db, ct); cfg.QbwcPassword = AccountingStore.NewPassword(); await AccountingStore.Save(db, cfg, ct);
            return Results.Ok(new { password = cfg.QbwcPassword });
        });
        g.MapGet("/qbwc.qwc", async (HttpContext ctx, string? url, NpgsqlDataSource db, CancellationToken ct) =>
        {
            var cfg = await AccountingStore.Load(db, ct);
            if (string.IsNullOrEmpty(cfg.OwnerId)) { await AccountingStore.Save(db, cfg, ct); cfg = await AccountingStore.Load(db, ct); }
            var baseUrl = (url ?? cfg.PublicUrl ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}").TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) return Results.BadRequest(new { error = "Give the address people use to reach ReflectoPatronus, for example https://glass.example.com" });
            var fileId = "{" + new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("rp-qbwc-" + cfg.OwnerId))).ToString().ToUpperInvariant() + "}";
            var doc = new XDocument(new XDeclaration("1.0", null, null), new XElement("QBWCXML",
                new XElement("AppName", "ReflectoPatronus"), new XElement("AppID", ""), new XElement("AppURL", baseUrl + "/qbwc"),
                new XElement("AppDescription", "Sends invoices, credit memos, payments and customers from ReflectoPatronus to QuickBooks."),
                new XElement("AppSupport", baseUrl + "/"), new XElement("UserName", cfg.User), new XElement("OwnerID", cfg.OwnerId), new XElement("FileID", fileId),
                new XElement("QBType", "QBFS"), new XElement("Scheduler", new XElement("RunEveryNMinutes", cfg.Every)), new XElement("IsReadOnly", "false")));
            return Results.File(Encoding.UTF8.GetBytes(doc.Declaration + Environment.NewLine + doc.ToString()), "application/xml", "ReflectoPatronus.qwc");
        });
        g.MapGet("/queue", Queue);
        g.MapPost("/retry", async (RetryBody b, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var c = db.CreateCommand("DELETE FROM accounting_export WHERE target = 'qbd' AND status IN ('error', 'skipped') AND ($1::text IS NULL OR (kind = $1 AND ref_id = $2))");
            c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)b.Kind ?? DBNull.Value }); c.Parameters.AddWithValue(b.RefId ?? 0);
            return Results.Ok(new { cleared = await c.ExecuteNonQueryAsync(ct) });
        });
        g.MapPost("/skip", async (RetryBody b, NpgsqlDataSource db, CancellationToken ct) =>
        {
            if (b.Kind is not ("invoice" or "credit" or "payment" or "customer") || b.RefId is null) return Results.BadRequest(new { error = "Say what to skip." });
            await AccountingStore.Mark(db, "qbd", b.Kind, b.RefId.Value, "skipped", null, "Not sent, on purpose.", ct);
            return Results.NoContent();
        });
        g.MapGet("/export/{format}", Export);
    }

    static async Task<IResult> Overview(NpgsqlDataSource db, CancellationToken ct)
    {
        var cfg = await AccountingStore.Load(db, ct);
        var counts = new Dictionary<string, long>();
        await using (var c = db.CreateCommand("SELECT status, count(*) FROM accounting_export WHERE target = 'qbd' AND kind <> 'customer' GROUP BY status"))
        { await using var r = await c.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) counts[r.GetString(0)] = r.GetInt64(1); }
        var since = cfg.SinceDate;
        var pendingDocs = (await AccountingData.PendingDocuments(db, "qbd", since, null, 100000, ct)).Count;
        var pendingPays = (await AccountingData.PendingPayments(db, "qbd", since, null, 100000, ct)).Count;
        var voids = (await AccountingData.PendingVoids(db, "qbd", ct)).Count;
        var last = await AccountingStore.Get(db, "qbwc_last", ct);
        return Results.Ok(new
        {
            config = cfg, effective = new { income = cfg.Income, agency = cfg.Agency, ar = cfg.Ar, taxPayable = cfg.TaxPayable, undeposited = cfg.Undeposited, user = cfg.User, every = cfg.Every, naming = cfg.Naming, since = cfg.SinceDate.ToString("yyyy-MM-dd") },
            pending = new { documents = pendingDocs, payments = pendingPays, voids }, sent = counts.GetValueOrDefault("sent"), errors = counts.GetValueOrDefault("error"), skipped = counts.GetValueOrDefault("skipped"),
            last,
        });
    }

    static async Task<IResult> Queue(string? status, int? limit, NpgsqlDataSource db, CancellationToken ct)
    {
        var cfg = await AccountingStore.Load(db, ct);
        var rows = new List<QueueRow>(); var lim = Math.Clamp(limit ?? 100, 1, 500);
        if (status is null or "pending")
        {
            await using var c = db.CreateCommand("""
                SELECT CASE d.doc_type WHEN 'C' THEN 'credit' ELSE 'invoice' END, d.id, doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix),
                       trim(cu.last_name || ' ' || coalesce(cu.first_name, '')), d.doc_date::timestamp,
                       (SELECT coalesce(sum(l.amount), 0) FROM document_line l WHERE l.document_id = d.id) + (SELECT coalesce(sum(t.amount), 0) FROM document_tax t WHERE t.document_id = d.id)
                FROM document d JOIN customer cu ON cu.id = d.customer_id
                WHERE d.id = ANY($1) ORDER BY d.doc_date, d.id
                """);
            c.Parameters.AddWithValue((await AccountingData.PendingDocuments(db, "qbd", cfg.SinceDate, null, lim, ct)).ToArray());
            await using (var r = await c.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) rows.Add(new(r.GetString(0), r.GetInt32(1), (r.GetString(0) == "credit" ? "Credit memo " : "Invoice ") + r.GetString(2), r.GetString(3), r.GetDateTime(4), r.GetDecimal(5), "pending", null, null));
            await using var p = db.CreateCommand("SELECT p.id, trim(cu.last_name || ' ' || coalesce(cu.first_name, '')), p.paid_at, p.amount FROM payment p JOIN customer cu ON cu.id = p.customer_id WHERE p.id = ANY($1) ORDER BY p.paid_at");
            p.Parameters.AddWithValue((await AccountingData.PendingPayments(db, "qbd", cfg.SinceDate, null, lim, ct)).ToArray());
            await using var pr = await p.ExecuteReaderAsync(ct);
            while (await pr.ReadAsync(ct)) rows.Add(new("payment", pr.GetInt32(0), "Payment P" + pr.GetInt32(0), pr.GetString(1), pr.GetDateTime(2), pr.GetDecimal(3), "pending", null, null));
        }
        else
        {
            await using var c = db.CreateCommand("""
                SELECT e.kind, e.ref_id, e.status, e.note, e.sent_at,
                       CASE e.kind WHEN 'payment' THEN 'Payment P' || e.ref_id WHEN 'customer' THEN 'Customer ' || coalesce(cu.number, e.ref_id::text)
                                   WHEN 'credit' THEN 'Credit memo ' || coalesce(doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix), '') ELSE 'Invoice ' || coalesce(doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix), '') END,
                       CASE e.kind WHEN 'customer' THEN trim(cu.last_name || ' ' || coalesce(cu.first_name, ''))
                                   WHEN 'payment' THEN (SELECT trim(x.last_name || ' ' || coalesce(x.first_name, '')) FROM customer x WHERE x.id = p.customer_id)
                                   ELSE (SELECT trim(x.last_name || ' ' || coalesce(x.first_name, '')) FROM customer x WHERE x.id = d.customer_id) END,
                       CASE e.kind WHEN 'payment' THEN p.paid_at WHEN 'customer' THEN NULL ELSE d.doc_date::timestamp END,
                       CASE e.kind WHEN 'payment' THEN p.amount WHEN 'customer' THEN NULL
                                   ELSE (SELECT coalesce(sum(l.amount), 0) FROM document_line l WHERE l.document_id = d.id) + (SELECT coalesce(sum(t.amount), 0) FROM document_tax t WHERE t.document_id = d.id) END
                FROM accounting_export e
                LEFT JOIN document d ON e.kind IN ('invoice', 'credit') AND d.id = e.ref_id
                LEFT JOIN payment p ON e.kind = 'payment' AND p.id = e.ref_id
                LEFT JOIN customer cu ON e.kind = 'customer' AND cu.id = e.ref_id
                WHERE e.target = 'qbd' AND e.status = $1 AND e.kind <> 'customer' OR (e.target = 'qbd' AND e.status = $1 AND e.status = 'error')
                ORDER BY e.updated_at DESC LIMIT $2
                """);
            c.Parameters.AddWithValue(status); c.Parameters.AddWithValue(lim);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new(r.GetString(0), r.GetInt32(1), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetDateTime(7), r.IsDBNull(8) ? null : r.GetDecimal(8), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetDateTime(4)));
        }
        return Results.Ok(rows);
    }

    static async Task<IResult> Export(string format, string? from, string? to, bool? includeSent, bool? mark, NpgsqlDataSource db, CancellationToken ct)
    {
        var cfg = await AccountingStore.Load(db, ct);
        var f = DateOnly.TryParse(from, out var fd) ? fd : cfg.SinceDate; DateOnly? t = DateOnly.TryParse(to, out var td) ? td : null;
        var all = includeSent == true;
        FileExports.Result res; string target;
        switch (format)
        {
            case "iif": res = await FileExports.Iif(db, cfg, f, t, all, ct); target = "iif"; break;
            case "sage50-invoices": res = await FileExports.SageInvoices(db, cfg, f, t, all, ct); target = "sage50"; break;
            case "sage50-receipts": res = await FileExports.SageReceipts(db, cfg, f, t, all, ct); target = "sage50"; break;
            case "sage50-customers": res = await FileExports.SageCustomers(db, f, t, all, ct); target = "sage50"; break;
            default: return Results.NotFound(new { error = "Unknown format." });
        }
        if (mark != false) foreach (var (kind, id) in res.Items) await AccountingStore.Mark(db, target, kind, id, "sent", null, "Exported to a file.", ct);
        return Results.File(Encoding.UTF8.GetBytes(res.Text), res.ContentType, res.FileName);
    }
}
