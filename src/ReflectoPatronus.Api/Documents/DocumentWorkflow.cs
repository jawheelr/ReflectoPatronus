using System.Security.Claims;
using Npgsql;
using ReflectoPatronus.Api.Auth;

namespace ReflectoPatronus.Api.Documents;

public record ConvertRequest(string To, int[]? LineSeqs, bool? Force);

/// <summary>
/// Moves a job along: quote -> work order -> invoice, and invoice -> credit memo.
///   Q -> W   new work order (new number from the shared work-order / invoice series; the quote stays on file)
///   Q -> I   new invoice, for counter sales
///   W -> I   the work order BECOMES the invoice (same row, same number), which is how the legacy data is numbered
///   I -> C   new credit memo for all or selected lines, transferred against the invoice
/// Invoices and credit memos post to the AR ledger (see <see cref="Ledger"/>).
/// </summary>
public static class DocumentWorkflow
{
    public static void Map(RouteGroupBuilder docs) => docs.MapPost("/{id:int}/convert", Convert);

    const string HeaderCopy = """
        customer_id, bill_to_customer_id, staff_sales_id, loss_date, discount_pct, shop_or_mobile, addr_is_ship, terms, initials, agent_code,
        po_number, policy_number, claim_number, price_category, customer_category, tax_group,
        cust_last_name, cust_first_name, cust_address1, cust_address2, cust_city, cust_state, cust_zip, cust_phones,
        loc_address1, loc_address2, loc_city, loc_state, loc_zip, spec_instructions, wo_instructions, note_printed, note_internal
        """;

    static IResult Conflict(string m) => Results.Json(new { error = m }, statusCode: 409);

    static async Task<IResult> Convert(int id, ConvertRequest req, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        var to = req.To?.ToUpperInvariant();
        if (to is not ("W" or "I" or "C")) return Results.BadRequest(new { error = "to must be W, I or C." });
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(7001)", cn, tx)) await lk.ExecuteNonQueryAsync(ct);

        string from; bool voided; int? customer; int company; string srcNumber;
        await using (var c = new NpgsqlCommand("""
            SELECT doc_type, voided, coalesce(bill_to_customer_id, customer_id), company_id, doc_number_text(doc_prefix, doc_number, doc_suffix)
            FROM document WHERE id = $1 FOR UPDATE
            """, cn, tx))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            from = r.GetString(0).Trim(); voided = r.GetBoolean(1); customer = r.IsDBNull(2) ? null : r.GetInt32(2); company = r.GetInt32(3); srcNumber = r.GetString(4);
        }
        if (voided) return Conflict("A voided document cannot be converted.");
        var allowed = (from, to) is ("Q", "W") or ("Q", "I") or ("W", "I") or ("I", "C");
        if (!allowed) return Conflict($"A {Name(from).ToLowerInvariant()} cannot be turned into a {Name(to).ToLowerInvariant()}.");
        if (customer is null) return Conflict("This document has no customer; open it and choose one first.");

        if (from == "Q" && req.Force != true)
        {
            await using var c = new NpgsqlCommand("""
                SELECT doc_type, doc_number_text(doc_prefix, doc_number, doc_suffix) FROM document
                WHERE source_doc_number = $1 AND doc_type IN ('W', 'I') AND NOT voided AND company_id = $2 LIMIT 1
                """, cn, tx);
            c.Parameters.AddWithValue(srcNumber); c.Parameters.AddWithValue(company);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                return Results.Json(new { error = $"This quote was already turned into {Name(r.GetString(0).Trim()).ToLowerInvariant()} {r.GetString(1)}.", duplicate = true }, statusCode: 409);
        }

        var uid = user.UserId();
        int newId = id; string number = srcNumber;
        if (from == "W" && to == "I")
        {
            await using (var c = new NpgsqlCommand("UPDATE document SET doc_type = 'I', doc_date = (now() AT TIME ZONE coalesce((SELECT time_zone FROM company WHERE id = $2), 'America/Chicago'))::date, printed_at = NULL, updated_at = now(), updated_by = $3 WHERE id = $1", cn, tx))
            { c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(company); c.Parameters.AddWithValue(uid); await c.ExecuteNonQueryAsync(ct); }
        }
        else
        {
            var (prefix, suffix, next) = await NextNumber(cn, tx, company, ct);
            await using (var c = new NpgsqlCommand($"""
                INSERT INTO document (company_id, doc_type, doc_prefix, doc_suffix, doc_number, doc_date, source_doc_number, created_by, updated_by, {HeaderCopy})
                SELECT company_id, $2, $3, $4, $5, (now() AT TIME ZONE coalesce((SELECT time_zone FROM company WHERE id = d.company_id), 'America/Chicago'))::date, $6, $7, $7, {HeaderCopy}
                FROM document d WHERE id = $1 RETURNING id
                """, cn, tx))
            {
                c.Parameters.AddWithValue(id); c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Char, Value = to });
                c.Parameters.AddWithValue(prefix); c.Parameters.AddWithValue(suffix); c.Parameters.AddWithValue(next); c.Parameters.AddWithValue(srcNumber); c.Parameters.AddWithValue(uid);
                newId = (int)(await c.ExecuteScalarAsync(ct))!;
            }
            number = prefix + next.ToString("0000000") + suffix;
            var credit = to == "C"; var seqs = req.LineSeqs is { Length: > 0 } s ? s : null;
            if (credit && seqs is not null)
            {
                await using var chk = new NpgsqlCommand("SELECT count(*) FROM document_line WHERE document_id = $1 AND seq = ANY($2)", cn, tx);
                chk.Parameters.AddWithValue(id); chk.Parameters.AddWithValue(seqs);
                if (System.Convert.ToInt32(await chk.ExecuteScalarAsync(ct)) != seqs.Distinct().Count()) return Results.BadRequest(new { error = "Some chosen lines are not on the invoice." });
            }
            await using (var c = new NpgsqlCommand($"""
                INSERT INTO document_line (document_id, seq, parent_seq, qty, list_price, unit_price, multiplier, amount, uom_price, uom_units, thick_num, thick_den,
                                           source, taxable, amount_override, list_override, special_price, part_code, description, dims, shape, price_category)
                SELECT $2, seq, CASE WHEN parent_seq IS NULL OR $3::int[] IS NULL OR parent_seq = ANY($3) THEN parent_seq END, qty, list_price, unit_price, multiplier,
                       CASE WHEN $4 THEN -amount ELSE amount END, uom_price, uom_units, thick_num, thick_den,
                       source, taxable, amount_override, list_override, special_price, part_code, description, dims, shape, price_category
                FROM document_line WHERE document_id = $1 AND ($3::int[] IS NULL OR seq = ANY($3)) ORDER BY seq
                """, cn, tx))
            {
                c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(newId);
                c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer, Value = (object?)(credit ? seqs : null) ?? DBNull.Value });
                c.Parameters.AddWithValue(credit);
                await c.ExecuteNonQueryAsync(ct);
            }
            // Tax: copied as is; a partial credit scales each tax by the share of taxable amount being credited.
            await using (var c = new NpgsqlCommand("""
                WITH orig AS (SELECT coalesce(sum(amount) FILTER (WHERE taxable), 0) AS t FROM document_line WHERE document_id = $1),
                     pick AS (SELECT coalesce(sum(amount) FILTER (WHERE taxable), 0) AS t FROM document_line WHERE document_id = $1 AND ($3::int[] IS NULL OR seq = ANY($3)))
                INSERT INTO document_tax (document_id, tax_code, percent, amount)
                SELECT $2, x.tax_code, x.percent,
                       CASE WHEN $4 THEN -(CASE WHEN orig.t = 0 THEN 0 ELSE round(x.amount * pick.t / orig.t, 2) END)
                            ELSE x.amount END
                FROM document_tax x, orig, pick WHERE x.document_id = $1
                """, cn, tx))
            {
                c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(newId);
                c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer, Value = (object?)(credit ? seqs : null) ?? DBNull.Value });
                c.Parameters.AddWithValue(credit);
                await c.ExecuteNonQueryAsync(ct);
            }
        }

        decimal total;
        await using (var c = new NpgsqlCommand("SELECT total FROM document_totals WHERE document_id = $1", cn, tx))
        { c.Parameters.AddWithValue(newId); total = System.Convert.ToDecimal(await c.ExecuteScalarAsync(ct)); }
        await using (var c = new NpgsqlCommand("INSERT INTO document_revision (at, document_id, total, created_by, comment) VALUES (now(), $1, $2, $3, $4)", cn, tx))
        {
            c.Parameters.AddWithValue(newId); c.Parameters.AddWithValue(total); c.Parameters.AddWithValue(uid);
            c.Parameters.AddWithValue(from == "W" ? "Converted from work order to invoice" : $"Created from {Name(from).ToLowerInvariant()} {srcNumber}");
            await c.ExecuteNonQueryAsync(ct);
        }

        if (to == "I") await Ledger.Post(cn, tx, customer.Value, newId, null, null, total, 1, uid, ct);
        else if (to == "C")
        {
            if (total >= 0) return Results.BadRequest(new { error = "Nothing to credit: the chosen lines add up to zero." });
            await Ledger.Post(cn, tx, customer.Value, newId, null, null, total, 1, uid, ct);                // credit memo: -x
            await Ledger.Post(cn, tx, customer.Value, newId, null, id, -total, 3, uid, ct);                  // transferred away: +x
            await Ledger.Post(cn, tx, customer.Value, id, null, newId, total, 3, uid, ct);                   // invoice reduced: -x
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { id = newId, number, type = to, total });
    }

    static string Name(string t) => t switch { "Q" => "Quote", "W" => "Work order", "I" => "Invoice", "C" => "Credit memo", _ => "Document" };

    /// <summary>Work orders, invoices and credit memos share one number series (as in the legacy data). Continue the latest one's prefix/suffix.</summary>
    static async Task<(string, string, int)> NextNumber(NpgsqlConnection cn, NpgsqlTransaction tx, int company, CancellationToken ct)
    {
        string prefix = "", suffix = "";
        await using (var c = new NpgsqlCommand("SELECT doc_prefix, doc_suffix FROM document WHERE doc_type IN ('W', 'I', 'C') AND company_id = $1 ORDER BY id DESC LIMIT 1", cn, tx))
        {
            c.Parameters.AddWithValue(company);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { prefix = r.GetString(0); suffix = r.GetString(1); }
        }
        await using var m = new NpgsqlCommand("SELECT coalesce(max(doc_number), 0) + 1 FROM document WHERE doc_type IN ('W', 'I', 'C') AND company_id = $1 AND doc_prefix = $2 AND doc_suffix = $3", cn, tx);
        m.Parameters.AddWithValue(company); m.Parameters.AddWithValue(prefix); m.Parameters.AddWithValue(suffix);
        return (prefix, suffix, System.Convert.ToInt32(await m.ExecuteScalarAsync(ct)));
    }
}
