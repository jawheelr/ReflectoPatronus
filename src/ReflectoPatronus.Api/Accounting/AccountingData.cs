using Npgsql;

namespace ReflectoPatronus.Api.Accounting;

public record AcctLine(int Seq, string Source, string? PartCode, string? Description, int? Qty, decimal Amount, bool Taxable);
public record AcctTax(string Code, decimal Percent, decimal Amount);
public record AcctDoc(int Id, string Type, string Number, DateOnly Date, int CustomerId, string? Po, string? TaxGroup, string? Notes, decimal Total, bool Voided,
    IReadOnlyList<AcctLine> Lines, IReadOnlyList<AcctTax> Taxes)
{
    public decimal Tax => Taxes.Sum(t => t.Amount);
    public decimal Subtotal => Lines.Sum(l => l.Amount);
    public string Kind => Type == "C" ? "credit" : "invoice";
}
public record AcctCustomer(int Id, string Number, string Last, string? First, string? Address1, string? Address2, string? City, string? State, string? Zip,
    string? Phone, string? Email, string? Contact, string? TaxId, bool TaxExempt)
{
    public string Display => string.Join(' ', new[] { Last, First }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
}
public record AcctPayment(int Id, DateTime PaidAt, int CustomerId, decimal Amount, string? Category, string? CategoryName, string? Description, bool Voided, IReadOnlyList<(int DocId, decimal Amount)> Applied);

/// <summary>What is waiting to be sent, and the data to send it. Shared by the QuickBooks Web Connector and the file exports.</summary>
public static class AccountingData
{
    static string KindOf(string docType) => docType == "C" ? "credit" : "invoice";

    public static async Task<List<int>> PendingDocuments(NpgsqlDataSource db, string target, DateOnly from, DateOnly? to, int limit, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT d.id FROM document d
            WHERE d.doc_type IN ('I', 'C') AND NOT d.voided AND d.customer_id IS NOT NULL AND d.doc_date >= $2 AND ($3::date IS NULL OR d.doc_date <= $3)
              AND NOT EXISTS (SELECT 1 FROM accounting_export e WHERE e.target = $1 AND e.ref_id = d.id AND e.kind = CASE d.doc_type WHEN 'C' THEN 'credit' ELSE 'invoice' END)
            ORDER BY d.doc_date, d.id LIMIT $4
            """);
        c.Parameters.AddWithValue(target); c.Parameters.AddWithValue(from); c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = (object?)to ?? DBNull.Value }); c.Parameters.AddWithValue(limit);
        return await Ids(c, ct);
    }

    public static async Task<List<int>> PendingPayments(NpgsqlDataSource db, string target, DateOnly from, DateOnly? to, int limit, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT p.id FROM payment p
            WHERE NOT p.voided AND p.amount > 0 AND p.paid_at >= $2::date AND ($3::date IS NULL OR p.paid_at < $3::date + 1)
              AND NOT EXISTS (SELECT 1 FROM accounting_export e WHERE e.target = $1 AND e.kind = 'payment' AND e.ref_id = p.id)
            ORDER BY p.paid_at, p.id LIMIT $4
            """);
        c.Parameters.AddWithValue(target); c.Parameters.AddWithValue(from); c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = (object?)to ?? DBNull.Value }); c.Parameters.AddWithValue(limit);
        return await Ids(c, ct);
    }

    /// <summary>Things already sent whose source has since been voided: (export row kind, ref id).</summary>
    public static async Task<List<(string Kind, int RefId)>> PendingVoids(NpgsqlDataSource db, string target, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT e.kind, e.ref_id FROM accounting_export e
            WHERE e.target = $1 AND e.status = 'sent' AND NOT e.void_sent AND e.kind <> 'customer'
              AND ((e.kind IN ('invoice', 'credit') AND EXISTS (SELECT 1 FROM document d WHERE d.id = e.ref_id AND d.voided))
                OR (e.kind = 'payment' AND EXISTS (SELECT 1 FROM payment p WHERE p.id = e.ref_id AND p.voided)))
            ORDER BY (e.kind = 'payment') DESC, e.id
            """);
        c.Parameters.AddWithValue(target);
        var list = new List<(string, int)>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetInt32(1)));
        return list;
    }

    public static async Task MarkVoidSent(NpgsqlDataSource db, string target, string kind, int refId, CancellationToken ct)
    {
        await using var c = db.CreateCommand("UPDATE accounting_export SET void_sent = true, updated_at = now() WHERE target = $1 AND kind = $2 AND ref_id = $3");
        c.Parameters.AddWithValue(target); c.Parameters.AddWithValue(kind); c.Parameters.AddWithValue(refId);
        await c.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Customers that the given documents and payments need but that have not been sent yet.</summary>
    public static async Task<List<int>> CustomersNeeded(NpgsqlDataSource db, string target, IEnumerable<int> docIds, IEnumerable<int> paymentIds, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT DISTINCT cid FROM (
                SELECT coalesce(d.bill_to_customer_id, d.customer_id) AS cid FROM document d WHERE d.id = ANY($2)
                UNION SELECT p.customer_id FROM payment p WHERE p.id = ANY($3)) x
            WHERE cid IS NOT NULL AND NOT EXISTS (SELECT 1 FROM accounting_export e WHERE e.target = $1 AND e.kind = 'customer' AND e.ref_id = cid)
            ORDER BY cid
            """);
        c.Parameters.AddWithValue(target); c.Parameters.AddWithValue(docIds.ToArray()); c.Parameters.AddWithValue(paymentIds.ToArray());
        return await Ids(c, ct);
    }

    static async Task<List<int>> Ids(NpgsqlCommand c, CancellationToken ct)
    {
        var list = new List<int>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetInt32(0));
        return list;
    }

    public static async Task<AcctDoc?> Document(NpgsqlDataSource db, int id, CancellationToken ct)
    {
        string type, number; DateOnly date; int customer; string? po, group, notes; bool voided;
        await using (var c = db.CreateCommand("""
            SELECT doc_type, doc_number_text(doc_prefix, doc_number, doc_suffix), doc_date, coalesce(bill_to_customer_id, customer_id), po_number, tax_group, note_printed, voided
            FROM document WHERE id = $1 AND customer_id IS NOT NULL
            """))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            type = r.GetString(0); number = r.GetString(1); date = DateOnly.FromDateTime(r.GetDateTime(2)); customer = r.GetInt32(3);
            po = r.IsDBNull(4) ? null : r.GetString(4); group = r.IsDBNull(5) ? null : r.GetString(5); notes = r.IsDBNull(6) ? null : r.GetString(6); voided = r.GetBoolean(7);
        }
        var lines = new List<AcctLine>();
        await using (var c = db.CreateCommand("SELECT seq, source::text, part_code, description, qty, amount, taxable FROM document_line WHERE document_id = $1 ORDER BY seq, id"))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lines.Add(new(r.GetInt16(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetDecimal(5), r.GetBoolean(6)));
        }
        var taxes = new List<AcctTax>();
        await using (var c = db.CreateCommand("SELECT tax_code, percent, amount FROM document_tax WHERE document_id = $1 ORDER BY id"))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) taxes.Add(new(r.GetString(0), r.GetDecimal(1), r.GetDecimal(2)));
        }
        // Credit memos are stored with negative amounts here; accounting programs want a positive credit memo.
        if (type == "C" && lines.Sum(l => l.Amount) + taxes.Sum(t => t.Amount) < 0)
        {
            lines = lines.Select(l => l with { Amount = -l.Amount }).ToList();
            taxes = taxes.Select(t => t with { Amount = -t.Amount }).ToList();
        }
        return new(id, type, number, date, customer, po, group, notes, lines.Sum(l => l.Amount) + taxes.Sum(t => t.Amount), voided, lines, taxes);
    }

    public static async Task<AcctCustomer?> Customer(NpgsqlDataSource db, int id, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT c.id, c.number, c.last_name, c.first_name, c.address1, c.address2, c.city, c.state, c.zip,
                   (SELECT p.number FROM customer_phone p WHERE p.customer_id = c.id ORDER BY p.slot LIMIT 1), c.email, c.contact_name, c.tax_id, c.tax_exempt
            FROM customer c WHERE c.id = $1
            """);
        c.Parameters.AddWithValue(id);
        await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i).Trim() is { Length: > 0 } s ? s : null;
        return new(r.GetInt32(0), r.GetString(1), r.GetString(2), S(3), S(4), S(5), S(6), S(7), S(8), S(9), S(10), S(11), S(12), r.GetBoolean(13));
    }

    public static async Task<AcctPayment?> Payment(NpgsqlDataSource db, int id, CancellationToken ct)
    {
        int customer; DateTime at; decimal amount; string? cat, catName, desc; bool voided;
        await using (var c = db.CreateCommand("SELECT p.customer_id, p.paid_at, p.amount, p.category, pc.description, p.description, p.voided FROM payment p LEFT JOIN pay_category pc ON pc.code = p.category WHERE p.id = $1"))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            customer = r.GetInt32(0); at = r.GetDateTime(1); amount = r.GetDecimal(2); cat = r.IsDBNull(3) ? null : r.GetString(3); catName = r.IsDBNull(4) ? null : r.GetString(4); desc = r.IsDBNull(5) ? null : r.GetString(5); voided = r.GetBoolean(6);
        }
        var applied = new List<(int, decimal)>();
        await using (var c = db.CreateCommand("SELECT document_id, -sum(amount) FROM ar_entry WHERE payment_id = $1 AND document_id IS NOT NULL AND purpose IN (4, 5) GROUP BY document_id HAVING -sum(amount) > 0 ORDER BY document_id"))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) applied.Add((r.GetInt32(0), r.GetDecimal(1)));
        }
        return new(id, at, customer, amount, cat, catName, desc, voided, applied);
    }

    /// <summary>The name a customer goes by in the accounting software (QuickBooks names are at most 41 characters).</summary>
    public static string CustomerName(AccountingConfig cfg, AcctCustomer c)
    {
        var name = c.Display; if (name.Length == 0) name = c.Number;
        return cfg.Naming switch
        {
            "number" => Clip(c.Number, 41),
            "name" => Clip(name, 41),
            _ => Clip(name, Math.Max(1, 41 - c.Number.Length - 3)) + " (" + c.Number + ")",
        };
    }
    public static string Clip(string? s, int n) { s = (s ?? "").Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ").Trim().Replace(":", "-"); return s.Length <= n ? s : s[..n].TrimEnd(); }
}
