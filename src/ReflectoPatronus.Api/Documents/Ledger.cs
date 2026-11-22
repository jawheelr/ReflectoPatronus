using Npgsql;

namespace ReflectoPatronus.Api.Documents;

/// <summary>
/// Accounts-receivable posting rules (derived from the legacy ledger, see docs/DB-DESIGN.md). One signed amount per row:
/// debit positive, credit negative; the ledger is append-only, so corrections post offsetting rows and never rewrite history.
///   purpose 1: the document itself (invoice +total, credit memo -total)
///   purpose 3: transfer of a credit memo to an invoice (credit memo +x, invoice -x, each pointing at the other)
///   purpose 4: payment applied to a document      purpose 5: deposit applied to a work order or invoice
/// </summary>
public static class Ledger
{
    public static async Task Post(NpgsqlConnection cn, NpgsqlTransaction tx, int customerId, int? documentId, int? paymentId, int? relatedDocumentId,
        decimal amount, short purpose, short? userId, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("""
            INSERT INTO ar_entry (customer_id, document_id, payment_id, related_document_id, amount, purpose, created_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """, cn, tx);
        c.Parameters.AddWithValue(customerId);
        c.Parameters.AddWithValue((object?)documentId ?? DBNull.Value);
        c.Parameters.AddWithValue((object?)paymentId ?? DBNull.Value);
        c.Parameters.AddWithValue((object?)relatedDocumentId ?? DBNull.Value);
        c.Parameters.AddWithValue(amount); c.Parameters.AddWithValue(purpose);
        c.Parameters.AddWithValue((object?)userId ?? DBNull.Value);
        await c.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The customer an invoice is billed to (bill-to if set, else the customer).</summary>
    public static async Task<int?> BillCustomer(NpgsqlConnection cn, NpgsqlTransaction tx, int documentId, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("SELECT coalesce(bill_to_customer_id, customer_id) FROM document WHERE id = $1", cn, tx);
        c.Parameters.AddWithValue(documentId);
        var v = await c.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToInt32(v);
    }

    /// <summary>Make the invoice's debit equal <paramref name="total"/> by posting only the difference.</summary>
    public static async Task SyncInvoiceDebit(NpgsqlConnection cn, NpgsqlTransaction tx, int documentId, int customerId, decimal total, short? userId, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("SELECT coalesce(sum(amount), 0) FROM ar_entry WHERE document_id = $1 AND purpose = 1", cn, tx);
        c.Parameters.AddWithValue(documentId);
        var delta = total - Convert.ToDecimal(await c.ExecuteScalarAsync(ct));
        if (delta != 0) await Post(cn, tx, await BillCustomer(cn, tx, documentId, ct) ?? customerId, documentId, null, null, delta, 1, userId, ct);
    }

    /// <summary>Net payments and deposits applied to a document (negative = money received).</summary>
    public static async Task<decimal> AppliedPayments(NpgsqlConnection cn, NpgsqlTransaction tx, int documentId, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("SELECT coalesce(sum(amount), 0) FROM ar_entry WHERE document_id = $1 AND purpose IN (4, 5)", cn, tx);
        c.Parameters.AddWithValue(documentId);
        return Convert.ToDecimal(await c.ExecuteScalarAsync(ct));
    }

    /// <summary>Void: offset the document's own debit and any credit-memo transfers. Callers must have checked that no payments are applied.</summary>
    public static async Task ReverseDocument(NpgsqlConnection cn, NpgsqlTransaction tx, int documentId, short? userId, CancellationToken ct)
    {
        var rows = new List<(int Customer, short Purpose, int? Related, decimal Sum)>();
        await using (var c = new NpgsqlCommand("""
            SELECT customer_id, purpose, related_document_id, sum(amount) FROM ar_entry
            WHERE document_id = $1 AND purpose IN (1, 3) GROUP BY customer_id, purpose, related_document_id HAVING sum(amount) <> 0
            """, cn, tx))
        {
            c.Parameters.AddWithValue(documentId);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetInt32(0), r.GetInt16(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetDecimal(3)));
        }
        foreach (var (customer, purpose, related, sum) in rows)
        {
            await Post(cn, tx, customer, documentId, null, related, -sum, purpose, userId, ct);
            if (purpose == 3 && related is { } other)   // the counterpart document's side of the transfer
                await Post(cn, tx, await BillCustomer(cn, tx, other, ct) ?? customer, other, null, documentId, sum, 3, userId, ct);
        }
    }
}
