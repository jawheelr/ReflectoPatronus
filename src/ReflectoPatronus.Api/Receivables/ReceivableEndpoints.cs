using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;
using ReflectoPatronus.Api.Auth;
using ReflectoPatronus.Api.Customers;
using ReflectoPatronus.Api.Documents;

namespace ReflectoPatronus.Api.Receivables;

public record ReceivableCustomer(int Id, string Number, string Name, decimal Balance, DateTime? LastActivity);
public record OpenDocument(int Id, string Type, string Number, DateOnly Date, string? PoNumber, bool Voided, decimal Total, decimal Paid, decimal Balance);
public record PaymentRow(int Id, DateTime PaidAt, int CustomerId, string? CustomerName, decimal Amount, string Type, string? Category, string? Description, bool Voided, decimal Applied);
public record Account(int CustomerId, string Number, string Name, decimal Balance, decimal OnAccount, IReadOnlyList<OpenDocument> Documents, IReadOnlyList<PaymentRow> Payments);
public record Application(int DocumentId, decimal Amount);
public record PaymentInput(int? CustomerId, decimal Amount, string? Type, string? Category, string? Description, DateTime? PaidAt, IReadOnlyList<Application>? Applications, bool? AutoApply);

/// <summary>
/// Receivables and payments. A payment is one row plus one ledger credit per document it is applied to; any part not applied
/// is held "on account" (a ledger credit with no document), so the customer's balance is always the sum of their ledger.
/// Type P is a payment (applied as ledger purpose 4), type D a deposit (purpose 5), as in the legacy data.
/// </summary>
public static class ReceivableEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/receivables", Customers).RequirePermission("receivables.read");
        api.MapGet("/customers/{id:int}/account", GetAccount).RequirePermission("receivables.read");
        api.MapGet("/payments", Payments).RequirePermission("receivables.read");
        api.MapPost("/payments", Create).RequirePermission("payments.write");
        api.MapPost("/payments/{id:int}/void", Void).RequirePermission("payments.void");
        api.MapPost("/customers/{id:int}/apply-credits", ApplyCredits).RequirePermission("payments.write");
    }

    static NpgsqlParameter P(object? v, NpgsqlDbType t) => new() { NpgsqlDbType = t, Value = v ?? DBNull.Value };

    /// <summary>Customers who owe money (or are owed: credit=true), largest first.</summary>
    static async Task<IResult> Customers(string? q, bool? credit, int? limit, NpgsqlDataSource db, CancellationToken ct)
    {
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        await using var c = db.CreateCommand("""
            SELECT c.id, c.number, trim(c.last_name || ' ' || coalesce(c.first_name, '')), b.balance, b.last
            FROM (SELECT customer_id, sum(amount) AS balance, max(created_at) AS last FROM ar_entry GROUP BY customer_id) b
            JOIN customer c ON c.id = b.customer_id
            WHERE (CASE WHEN $2 THEN b.balance < 0 ELSE b.balance > 0 END)
              AND ($1::text IS NULL OR lower(c.last_name || ' ' || coalesce(c.first_name, '')) LIKE '%' || lower($1) || '%' OR upper(c.number) LIKE upper($1) || '%')
            ORDER BY abs(b.balance) DESC LIMIT $3
            """);
        c.Parameters.Add(P(q is null ? null : CustomerEndpoints.EscapeLike(q), NpgsqlDbType.Text)); c.Parameters.AddWithValue(credit == true); c.Parameters.AddWithValue(Math.Clamp(limit ?? 100, 1, 500));
        var list = new List<ReceivableCustomer>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetDateTime(4)));
        return Results.Ok(list);
    }

    static async Task<IResult> GetAccount(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        string number, name;
        await using (var c = db.CreateCommand("SELECT number, trim(last_name || ' ' || coalesce(first_name, '')) FROM customer WHERE id = $1"))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            (number, name) = (r.GetString(0), r.GetString(1));
        }
        var docs = new List<OpenDocument>();
        await using (var c = db.CreateCommand("""
            SELECT d.id, d.doc_type, doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix), d.doc_date, d.po_number, d.voided,
                   coalesce(t.total, 0), coalesce(-a.paid, 0), a.balance
            FROM (SELECT document_id, sum(amount) AS balance, sum(amount) FILTER (WHERE purpose IN (4, 5)) AS paid
                  FROM ar_entry WHERE customer_id = $1 AND document_id IS NOT NULL GROUP BY document_id HAVING sum(amount) <> 0) a
            JOIN document d ON d.id = a.document_id
            LEFT JOIN document_totals t ON t.document_id = d.id
            ORDER BY d.doc_date, d.id
            """))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) docs.Add(new(r.GetInt32(0), r.GetString(1).Trim(), r.GetString(2), DateOnly.FromDateTime(r.GetDateTime(3)), CustomerEndpoints.Str(r, 4), r.GetBoolean(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8)));
        }
        decimal onAccount, balance;
        await using (var c = db.CreateCommand("SELECT coalesce(sum(amount), 0), coalesce(sum(amount) FILTER (WHERE document_id IS NULL), 0) FROM ar_entry WHERE customer_id = $1"))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
            (balance, onAccount) = (r.GetDecimal(0), -r.GetDecimal(1));
        }
        return Results.Ok(new Account(id, number, name, balance, onAccount, docs, await Query(db, id, 25, ct)));
    }

    static async Task<List<PaymentRow>> Query(NpgsqlDataSource db, int? customerId, int limit, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT p.id, p.paid_at, p.customer_id, trim(c.last_name || ' ' || coalesce(c.first_name, '')), p.amount, p.type, p.category, p.description, p.voided,
                   coalesce((SELECT -sum(amount) FROM ar_entry WHERE payment_id = p.id AND document_id IS NOT NULL), 0)
            FROM payment p JOIN customer c ON c.id = p.customer_id
            WHERE $1::int IS NULL OR p.customer_id = $1 ORDER BY p.paid_at DESC, p.id DESC LIMIT $2
            """);
        c.Parameters.Add(P(customerId, NpgsqlDbType.Integer)); c.Parameters.AddWithValue(limit);
        var list = new List<PaymentRow>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new(r.GetInt32(0), r.GetDateTime(1), r.GetInt32(2), r.GetString(3), r.GetDecimal(4), r.GetString(5).Trim(), CustomerEndpoints.Str(r, 6), CustomerEndpoints.Str(r, 7), r.GetBoolean(8), r.GetDecimal(9)));
        return list;
    }

    static async Task<IResult> Payments(int? customerId, int? limit, NpgsqlDataSource db, CancellationToken ct)
        => Results.Ok(await Query(db, customerId, Math.Clamp(limit ?? 50, 1, 200), ct));

    static IResult Bad(string m) => Results.BadRequest(new { error = m });
    static IResult Conflict(string m) => Results.Json(new { error = m }, statusCode: 409);

    static async Task<IResult> Create(PaymentInput i, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (i.CustomerId is null) return Bad("Choose a customer.");
        if (i.Amount <= 0 || i.Amount != decimal.Round(i.Amount, 2)) return Bad("Amount must be more than zero, in whole cents.");
        var type = (i.Type ?? "P").ToUpperInvariant();
        if (type is not ("P" or "D")) return Bad("Type must be P (payment) or D (deposit).");
        var apps = (i.Applications ?? []).Where(a => a.Amount != 0).ToList();
        if (apps.Any(a => a.Amount < 0 || a.Amount != decimal.Round(a.Amount, 2))) return Bad("Applied amounts must be positive, in whole cents.");
        if (apps.GroupBy(a => a.DocumentId).Any(g => g.Count() > 1)) return Bad("A document can appear only once.");
        if (apps.Sum(a => a.Amount) > i.Amount) return Bad("More is applied than the amount received.");

        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var c = new NpgsqlCommand("SELECT category FROM (SELECT code AS category FROM pay_category WHERE code = $1) x", cn, tx))
        {
            c.Parameters.AddWithValue((object?)i.Category ?? DBNull.Value);
            if (i.Category is not null && await c.ExecuteScalarAsync(ct) is null) return Bad("Unknown payment category.");
        }
        var uid = user.UserId();
        var purpose = (short)(type == "D" ? 5 : 4);

        if (i.AutoApply == true && apps.Count == 0)   // oldest first
        {
            var left = i.Amount;
            await using var c = new NpgsqlCommand("""
                SELECT a.document_id, a.balance FROM (SELECT document_id, sum(amount) AS balance FROM ar_entry WHERE customer_id = $1 AND document_id IS NOT NULL GROUP BY document_id HAVING sum(amount) > 0) a
                JOIN document d ON d.id = a.document_id WHERE NOT d.voided ORDER BY d.doc_date, d.id
                """, cn, tx);
            c.Parameters.AddWithValue(i.CustomerId.Value);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (left > 0 && await r.ReadAsync(ct)) { var take = Math.Min(left, r.GetDecimal(1)); apps.Add(new(r.GetInt32(0), take)); left -= take; }
        }

        // Every applied document must be this customer's (or billed to them) and not voided; it may not be overpaid.
        foreach (var a in apps)
        {
            await using var c = new NpgsqlCommand("""
                SELECT d.voided, coalesce((SELECT sum(amount) FROM ar_entry WHERE document_id = d.id), 0), coalesce(d.bill_to_customer_id, d.customer_id)
                FROM document d WHERE d.id = $1
                """, cn, tx);
            c.Parameters.AddWithValue(a.DocumentId);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Bad($"Document {a.DocumentId} does not exist.");
            if (r.GetBoolean(0)) return Conflict("A voided document cannot be paid.");
            if (r.GetInt32(2) != i.CustomerId) return Bad("A chosen document belongs to a different customer.");
            if (a.Amount > r.GetDecimal(1)) return Conflict($"A payment of {a.Amount:0.00} is more than the {r.GetDecimal(1):0.00} owed on one document.");
        }

        int id;
        await using (var c = new NpgsqlCommand("INSERT INTO payment (paid_at, customer_id, amount, created_by, type, category, description) VALUES (coalesce($1, now()), $2, $3, $4, $5, $6, $7) RETURNING id", cn, tx))
        {
            c.Parameters.Add(P(i.PaidAt is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null, NpgsqlDbType.TimestampTz)); c.Parameters.AddWithValue(i.CustomerId.Value); c.Parameters.AddWithValue(i.Amount);
            c.Parameters.AddWithValue(uid); c.Parameters.Add(P(type, NpgsqlDbType.Char)); c.Parameters.Add(P(i.Category, NpgsqlDbType.Text));
            c.Parameters.Add(P(string.IsNullOrWhiteSpace(i.Description) ? null : i.Description.Trim(), NpgsqlDbType.Text));
            id = (int)(await c.ExecuteScalarAsync(ct))!;
        }
        foreach (var a in apps) await Ledger.Post(cn, tx, i.CustomerId.Value, a.DocumentId, id, null, -a.Amount, purpose, uid, ct);
        var onAccount = i.Amount - apps.Sum(a => a.Amount);
        if (onAccount > 0) await Ledger.Post(cn, tx, i.CustomerId.Value, null, id, null, -onAccount, purpose, uid, ct);
        await tx.CommitAsync(ct);
        return Results.Created($"/api/v1/payments/{id}", new { id, applied = apps.Sum(a => a.Amount), onAccount });
    }

    static async Task<IResult> Void(int id, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        bool voided;
        await using (var c = new NpgsqlCommand("SELECT voided FROM payment WHERE id = $1 FOR UPDATE", cn, tx))
        {
            c.Parameters.AddWithValue(id);
            if (await c.ExecuteScalarAsync(ct) is not bool v) return Results.NotFound();
            voided = v;
        }
        if (voided) return Conflict("Already voided.");
        var rows = new List<(int Customer, int? Doc, short Purpose, decimal Sum)>();
        await using (var c = new NpgsqlCommand("SELECT customer_id, document_id, purpose, sum(amount) FROM ar_entry WHERE payment_id = $1 GROUP BY customer_id, document_id, purpose HAVING sum(amount) <> 0", cn, tx))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetInt32(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.GetInt16(2), r.GetDecimal(3)));
        }
        foreach (var (customer, doc, purpose, sum) in rows) await Ledger.Post(cn, tx, customer, doc, id, null, -sum, purpose, user.UserId(), ct);
        await using (var c = new NpgsqlCommand("UPDATE payment SET voided = true, voided_at = now(), voided_by = $2 WHERE id = $1", cn, tx))
        { c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(user.UserId()); await c.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Uses a customer's credit against what they owe, oldest invoice first. Credit is money held on account (from payments) or an
    /// over-credited document (a negative balance, e.g. a credit memo larger than its invoice); the latter moves by a ledger transfer.
    /// </summary>
    static async Task<IResult> ApplyCredits(int id, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var lk = new NpgsqlCommand("SELECT 1 FROM customer WHERE id = $1 FOR UPDATE", cn, tx)) { lk.Parameters.AddWithValue(id); if (await lk.ExecuteScalarAsync(ct) is null) return Results.NotFound(); }
        var sources = new List<(int? Payment, int? Doc, short Purpose, decimal Left)>(); var targets = new List<(int Doc, decimal Left)>();
        await using (var c = new NpgsqlCommand("""
            SELECT a.payment_id, p.type, -sum(a.amount) FROM ar_entry a JOIN payment p ON p.id = a.payment_id
            WHERE a.customer_id = $1 AND a.document_id IS NULL GROUP BY a.payment_id, p.type HAVING sum(a.amount) < 0 ORDER BY a.payment_id
            """, cn, tx))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) sources.Add((r.GetInt32(0), null, (short)(r.GetString(1).Trim() == "D" ? 5 : 4), r.GetDecimal(2)));
        }
        await using (var c = new NpgsqlCommand("""
            SELECT a.document_id, sum(a.amount) FROM ar_entry a JOIN document d ON d.id = a.document_id
            WHERE a.customer_id = $1 AND NOT d.voided GROUP BY a.document_id HAVING sum(a.amount) <> 0 ORDER BY min(d.doc_date), a.document_id
            """, cn, tx))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { var b = r.GetDecimal(1); if (b > 0) targets.Add((r.GetInt32(0), b)); else sources.Add((null, r.GetInt32(0), 3, -b)); }
        }
        var applied = 0m; var ti = 0; var uid = user.UserId();
        for (var si = 0; si < sources.Count && ti < targets.Count; si++)
        {
            var s = sources[si];
            while (s.Left > 0 && ti < targets.Count)
            {
                var t = targets[ti]; var take = Math.Min(s.Left, t.Left);
                if (s.Payment is { } pay) { await Ledger.Post(cn, tx, id, null, pay, null, take, s.Purpose, uid, ct); await Ledger.Post(cn, tx, id, t.Doc, pay, null, -take, s.Purpose, uid, ct); }
                else { await Ledger.Post(cn, tx, id, s.Doc, null, t.Doc, take, 3, uid, ct); await Ledger.Post(cn, tx, id, t.Doc, null, s.Doc, -take, 3, uid, ct); }
                s.Left -= take; targets[ti] = (t.Doc, t.Left - take); applied += take;
                if (targets[ti].Left == 0) ti++;
            }
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { applied });
    }
}
