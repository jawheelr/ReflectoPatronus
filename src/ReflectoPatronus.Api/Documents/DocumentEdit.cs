using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;
using ReflectoPatronus.Api.Auth;
using ReflectoPatronus.Api.Customers;

namespace ReflectoPatronus.Api.Documents;

/// <summary>Create/replace payload for a quote. UpdatedAt is the value read with the document (optimistic concurrency; ignored on create).</summary>
public record DocumentInput(int? CustomerId, DateOnly? Date, string? PoNumber, int? SalesPersonId, string? TaxGroup, string? PriceCategory,
    decimal? DiscountPercent, string? Terms, string? ShopOrMobile, string? SpecialInstructions, string? WorkOrderInstructions,
    string? NotePrinted, string? NoteInternal, DocumentParty? JobSite, DateOnly? LossDate, string? PolicyNumber, string? ClaimNumber,
    IReadOnlyList<DraftLine>? Lines, DateTime? UpdatedAt);
public record CatalogThickness(short Num, short Den, string Text);
public record CatalogPart(int Id, string Code, string? Description, string Source, int? UomId, IReadOnlyList<CatalogThickness> Thicknesses);

/// <summary>
/// Document entry: quotes are created here; quotes, work orders and invoices can be edited (credit memos are fixed once created). The
/// ledger follows invoices (see Ledger). Prices are always computed here by <see cref="PricingEngine"/>; the client never sends totals.
/// </summary>
public static class DocumentEdit
{
    public static void Map(RouteGroupBuilder api, RouteGroupBuilder docs)
    {
        docs.MapPost("/calculate", Calculate).RequirePermission("quotes.write", "workorders.write", "invoices.write");
        docs.MapPost("", Create).RequirePermission("quotes.write");
        docs.MapPut("/{id:int}", Update);   // the permission depends on the document type: checked inside
        docs.MapPost("/{id:int}/void", Void).RequirePermission("documents.void");
        api.MapGet("/catalog/parts", Parts);   // pickers: open to anyone signed in
    }

    static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    static NpgsqlParameter P(object? v, NpgsqlDbType t) => new() { NpgsqlDbType = t, Value = v ?? DBNull.Value };

    static async Task<IResult> Calculate(DraftDocument doc, NpgsqlDataSource db, CancellationToken ct)
    {
        try { return Results.Ok(await PricingEngine.CalculateAsync(db, doc, ct)); }
        catch (PricingException e) { return Results.BadRequest(new { error = e.Message }); }
    }

    static async Task<IResult> Parts(string? q, string? source, NpgsqlDataSource db, CancellationToken ct)
    {
        q = N(q); var src = source?.ToUpperInvariant() switch { "GLASS" or "G" => "G", "PART" or "P" => "P", _ => null };
        await using var cmd = db.CreateCommand("""
            SELECT id, code, description, source, uom_id FROM part
            WHERE ($1::text IS NULL OR upper(code) LIKE '%' || upper($1) || '%' OR lower(coalesce(description, '')) LIKE '%' || lower($1) || '%')
              AND ($2::text IS NULL OR source = $2)
            ORDER BY ($1::text IS NOT NULL AND upper(code) LIKE upper($1) || '%') DESC, upper(code) LIMIT 40
            """);
        cmd.Parameters.Add(P(q is null ? null : CustomerEndpoints.EscapeLike(q), NpgsqlDbType.Text)); cmd.Parameters.Add(P(src, NpgsqlDbType.Char));
        var parts = new List<(int, string, string?, string, int?)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) parts.Add((r.GetInt32(0), r.GetString(1), CustomerEndpoints.Str(r, 2), r.GetString(3).Trim(), r.IsDBNull(4) ? null : r.GetInt16(4)));
        var ids = parts.Select(p => p.Item1).ToArray();
        var thick = new Dictionary<int, List<CatalogThickness>>();
        await using (var c = db.CreateCommand("SELECT part_id, thick_num, thick_den FROM part_thickness WHERE part_id = ANY($1) ORDER BY part_id, thick_den, thick_num"))
        {
            c.Parameters.AddWithValue(ids);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var (n, d) = (r.GetInt16(1), r.GetInt16(2));
                (thick.TryGetValue(r.GetInt32(0), out var l) ? l : thick[r.GetInt32(0)] = []).Add(new CatalogThickness(n, d, d == 0 ? "" : $"{n}/{d}"));
            }
        }
        return Results.Ok(parts.Select(p => new CatalogPart(p.Item1, p.Item2, p.Item3, p.Item4, p.Item5, thick.GetValueOrDefault(p.Item1) ?? [])));
    }

    static string? Validate(DocumentInput i)
    {
        if (i.CustomerId is null) return "Choose a customer.";
        if (i.DiscountPercent is < 0 or > 100) return "Discount must be between 0 and 100 percent.";
        if (N(i.ShopOrMobile) is { } m && m is not ("S" or "M")) return "Shop or mobile must be S or M.";
        foreach (var (v, max, name) in new[] { (i.PoNumber, 40, "PO number"), (i.Terms, 40, "Terms"), (i.PolicyNumber, 40, "Policy number"), (i.ClaimNumber, 40, "Claim number") })
            if (v is not null && v.Trim().Length > max) return $"{name} is too long (max {max}).";
        if (i.JobSite?.State is { } s && N(s) is { } st && st.Length != 2) return "Job site state must be two letters.";
        return null;
    }

    /// <summary>Current customer details copied onto the document, as printed (the document keeps its own copy).</summary>
    static async Task<(string?, string?, string?, string?, string?, string?, string?, string[])> Snapshot(NpgsqlConnection cn, NpgsqlTransaction tx, int customerId, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("SELECT last_name, first_name, address1, address2, city, state, zip FROM customer WHERE id = $1", cn, tx);
        c.Parameters.AddWithValue(customerId);
        string?[] f = new string?[7];
        await using (var r = await c.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) throw new PricingException("Unknown customer.");
            for (var k = 0; k < 7; k++) f[k] = r.IsDBNull(k) ? null : r.GetString(k).Trim();
        }
        var phones = new List<string>();
        await using var p = new NpgsqlCommand("SELECT number, ext FROM customer_phone WHERE customer_id = $1 ORDER BY slot", cn, tx);
        p.Parameters.AddWithValue(customerId);
        await using var pr = await p.ExecuteReaderAsync(ct);
        while (await pr.ReadAsync(ct)) phones.Add(pr.GetString(0) + (pr.IsDBNull(1) || pr.GetString(1) == "" ? "" : "x" + pr.GetString(1)));
        return (f[0], f[1], f[2], f[3], f[4], f[5], f[6], phones.ToArray());
    }

    static void Header(NpgsqlCommand c, DocumentInput i, CalcResult calc, string? snapshotMarker)
    {
        // $1.. shared by INSERT and UPDATE (see the SQL in each caller)
        c.Parameters.Add(P(i.Date?.ToDateTime(TimeOnly.MinValue), NpgsqlDbType.Date));                     // 1 doc_date
        c.Parameters.Add(P(N(i.PoNumber), NpgsqlDbType.Text));                                             // 2
        c.Parameters.Add(P(i.SalesPersonId, NpgsqlDbType.Integer));                                        // 3
        c.Parameters.Add(P(calc.TaxGroup, NpgsqlDbType.Text));                                             // 4
        c.Parameters.Add(P(calc.PriceCategory, NpgsqlDbType.Text));                                        // 5
        c.Parameters.Add(P(i.DiscountPercent is null or 0 ? null : i.DiscountPercent, NpgsqlDbType.Numeric)); // 6
        c.Parameters.Add(P(N(i.Terms), NpgsqlDbType.Text));                                                // 7
        c.Parameters.Add(P(N(i.ShopOrMobile), NpgsqlDbType.Char));                                         // 8
        c.Parameters.Add(P(N(i.SpecialInstructions), NpgsqlDbType.Text));                                  // 9
        c.Parameters.Add(P(N(i.WorkOrderInstructions), NpgsqlDbType.Text));                                // 10
        c.Parameters.Add(P(N(i.NotePrinted), NpgsqlDbType.Text));                                          // 11
        c.Parameters.Add(P(N(i.NoteInternal), NpgsqlDbType.Text));                                         // 12
        c.Parameters.Add(P(N(i.JobSite?.Address1), NpgsqlDbType.Text));                                    // 13
        c.Parameters.Add(P(N(i.JobSite?.Address2), NpgsqlDbType.Text));                                    // 14
        c.Parameters.Add(P(N(i.JobSite?.City), NpgsqlDbType.Text));                                        // 15
        c.Parameters.Add(P(N(i.JobSite?.State)?.ToUpperInvariant(), NpgsqlDbType.Char));                   // 16
        c.Parameters.Add(P(N(i.JobSite?.Zip), NpgsqlDbType.Text));                                         // 17
        c.Parameters.Add(P(i.LossDate?.ToDateTime(TimeOnly.MinValue), NpgsqlDbType.Date));                 // 18
        c.Parameters.Add(P(N(i.PolicyNumber), NpgsqlDbType.Text));                                         // 19
        c.Parameters.Add(P(N(i.ClaimNumber), NpgsqlDbType.Text));                                          // 20
    }

    static async Task WriteLines(NpgsqlConnection cn, NpgsqlTransaction tx, int docId, CalcResult calc, CancellationToken ct)
    {
        foreach (var l in calc.Lines)
        {
            await using var c = new NpgsqlCommand("""
                INSERT INTO document_line (document_id, seq, qty, list_price, unit_price, multiplier, amount, uom_price, uom_units, thick_num, thick_den,
                                           source, taxable, amount_override, list_override, special_price, part_code, description, dims, shape, price_category)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12::line_source, $13, $14, false, $15, $16, $17, $18, $19, $20)
                """, cn, tx);
            c.Parameters.AddWithValue(docId); c.Parameters.AddWithValue((short)l.Seq); c.Parameters.AddWithValue(l.Qty);
            c.Parameters.Add(P(l.ListPrice, NpgsqlDbType.Numeric)); c.Parameters.Add(P(l.UnitPrice, NpgsqlDbType.Numeric));
            c.Parameters.AddWithValue(l.Multiplier); c.Parameters.AddWithValue(l.Amount);
            c.Parameters.Add(P(l.Rate, NpgsqlDbType.Numeric)); c.Parameters.Add(P(l.SqFt ?? l.Units, NpgsqlDbType.Numeric));
            c.Parameters.Add(P(l.ThickNum, NpgsqlDbType.Smallint)); c.Parameters.Add(P(l.ThickDen, NpgsqlDbType.Smallint));
            c.Parameters.AddWithValue(l.Source); c.Parameters.AddWithValue(l.Taxable); c.Parameters.AddWithValue(l.AmountOverride);
            c.Parameters.AddWithValue(l.SpecialPrice);
            c.Parameters.Add(P(l.PartCode, NpgsqlDbType.Text)); c.Parameters.Add(P(l.Description, NpgsqlDbType.Text));
            c.Parameters.Add(P(l.Dims, NpgsqlDbType.Text)); c.Parameters.Add(P(l.Shape, NpgsqlDbType.Text)); c.Parameters.Add(P(calc.PriceCategory, NpgsqlDbType.Text));
            await c.ExecuteNonQueryAsync(ct);
        }
        foreach (var t in calc.Taxes)
        {
            await using var c = new NpgsqlCommand("INSERT INTO document_tax (document_id, tax_code, percent, amount) VALUES ($1, $2, $3, $4)", cn, tx);
            c.Parameters.AddWithValue(docId); c.Parameters.AddWithValue(t.Code); c.Parameters.AddWithValue(t.Percent); c.Parameters.AddWithValue(t.Amount);
            await c.ExecuteNonQueryAsync(ct);
        }
    }

    static async Task<IResult> Create(DocumentInput input, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (Validate(input) is { } err) return Results.BadRequest(new { error = err });
        try
        {
            var calc = await PricingEngine.CalculateAsync(db, new DraftDocument(input.CustomerId, input.PriceCategory, input.TaxGroup, input.DiscountPercent, input.Lines), ct);
            await using var cn = await db.OpenConnectionAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(ct);
            await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(7001)", cn, tx)) await lk.ExecuteNonQueryAsync(ct);   // one number at a time

            // number: the latest quote's prefix/suffix, then the next number in that series
            int companyId;
            await using (var c = new NpgsqlCommand("SELECT id FROM company ORDER BY id LIMIT 1", cn, tx))
                companyId = Convert.ToInt32(await c.ExecuteScalarAsync(ct) ?? throw new PricingException("No company is set up."));
            string prefix = "", suffix = ""; var next = 1;
            await using (var c = new NpgsqlCommand("""
                SELECT doc_prefix, doc_suffix FROM document WHERE doc_type = 'Q' AND company_id = $1 ORDER BY id DESC LIMIT 1
                """, cn, tx))
            {
                c.Parameters.AddWithValue(companyId);
                await using var r = await c.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { prefix = r.GetString(0); suffix = r.GetString(1); }
            }
            await using (var c = new NpgsqlCommand("SELECT coalesce(max(doc_number), 0) + 1 FROM document WHERE doc_type = 'Q' AND company_id = $1 AND doc_prefix = $2 AND doc_suffix = $3", cn, tx))
            { c.Parameters.AddWithValue(companyId); c.Parameters.AddWithValue(prefix); c.Parameters.AddWithValue(suffix); next = Convert.ToInt32(await c.ExecuteScalarAsync(ct)); }

            var snap = await Snapshot(cn, tx, input.CustomerId!.Value, ct);
            var date = input.Date ?? await Today(cn, tx, ct);
            await using var ins = new NpgsqlCommand("""
                INSERT INTO document (doc_date, po_number, staff_sales_id, tax_group, price_category, discount_pct, terms, shop_or_mobile, spec_instructions,
                    wo_instructions, note_printed, note_internal, loc_address1, loc_address2, loc_city, loc_state, loc_zip, loss_date, policy_number, claim_number,
                    company_id, doc_type, doc_prefix, doc_suffix, doc_number, customer_id, created_by, updated_by,
                    cust_last_name, cust_first_name, cust_address1, cust_address2, cust_city, cust_state, cust_zip, cust_phones)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20, $34, 'Q', $21, $22, $23, $24, $25, $25, $26,$27,$28,$29,$30,$31,$32,$33)
                RETURNING id
                """, cn, tx);
            Header(ins, input with { Date = date }, calc, null);
            ins.Parameters.AddWithValue(prefix); ins.Parameters.AddWithValue(suffix); ins.Parameters.AddWithValue(next);
            ins.Parameters.AddWithValue(input.CustomerId!.Value); ins.Parameters.AddWithValue(user.UserId());
            ins.Parameters.Add(P(snap.Item1, NpgsqlDbType.Text)); ins.Parameters.Add(P(snap.Item2, NpgsqlDbType.Text)); ins.Parameters.Add(P(snap.Item3, NpgsqlDbType.Text));
            ins.Parameters.Add(P(snap.Item4, NpgsqlDbType.Text)); ins.Parameters.Add(P(snap.Item5, NpgsqlDbType.Text)); ins.Parameters.Add(P(snap.Item6, NpgsqlDbType.Char));
            ins.Parameters.Add(P(snap.Item7, NpgsqlDbType.Text)); ins.Parameters.Add(P(snap.Item8, NpgsqlDbType.Array | NpgsqlDbType.Text));
            ins.Parameters.AddWithValue(companyId);
            var id = (int)(await ins.ExecuteScalarAsync(ct))!;
            await WriteLines(cn, tx, id, calc, ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/documents/{id}", new { id, number = prefix + next.ToString("0000000") + suffix, total = calc.Total });
        }
        catch (PricingException e) { return Results.BadRequest(new { error = e.Message }); }
    }

    static async Task<DateOnly> Today(NpgsqlConnection cn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("SELECT (now() AT TIME ZONE coalesce((SELECT time_zone FROM company ORDER BY id LIMIT 1), 'America/Chicago'))::date", cn, tx);
        var v = await c.ExecuteScalarAsync(ct);
        return v is DateOnly d ? d : DateOnly.FromDateTime((DateTime)v!);
    }

    static async Task<IResult> Update(int id, DocumentInput input, ClaimsPrincipal user, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        if (Validate(input) is { } err) return Results.BadRequest(new { error = err });
        if (input.UpdatedAt is null) return Results.BadRequest(new { error = "updatedAt (from the document you loaded) is required." });
        try
        {
            var calc = await PricingEngine.CalculateAsync(db, new DraftDocument(input.CustomerId, input.PriceCategory, input.TaxGroup, input.DiscountPercent, input.Lines), ct);
            await using var cn = await db.OpenConnectionAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(ct);
            string type; bool voided; DateTime updated; int? oldCustomer; decimal? oldTotal;
            await using (var c = new NpgsqlCommand("""
                SELECT doc_type, voided, updated_at, customer_id,
                       (SELECT coalesce(sum(amount), 0) FROM document_line WHERE document_id = d.id) + (SELECT coalesce(sum(amount), 0) FROM document_tax WHERE document_id = d.id)
                FROM document d WHERE id = $1 FOR UPDATE
                """, cn, tx))
            {
                c.Parameters.AddWithValue(id);
                await using var r = await c.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound();
                type = r.GetString(0).Trim(); voided = r.GetBoolean(1); updated = r.GetDateTime(2); oldCustomer = r.IsDBNull(3) ? null : r.GetInt32(3); oldTotal = r.GetDecimal(4);
            }
            if (type is "Q" or "W" or "I" && !await Permissions.Has(http, Permissions.WriteFor(type))) return Permissions.Denied(Permissions.WriteFor(type));
            if (type is not ("Q" or "W" or "I")) return Results.Json(new { error = "Credit memos cannot be edited; void it and create a new one." }, statusCode: 409);
            if (voided) return Results.Json(new { error = "A voided document cannot be edited." }, statusCode: 409);
            if (DateTime.SpecifyKind(input.UpdatedAt.Value, DateTimeKind.Utc) != DateTime.SpecifyKind(updated, DateTimeKind.Utc))
                return Results.Json(new { error = "Someone else changed this document since you opened it. Reload it and try again." }, statusCode: 409);

            await using var up = new NpgsqlCommand("""
                UPDATE document SET doc_date = coalesce($1, doc_date), po_number = $2, staff_sales_id = $3, tax_group = $4, price_category = $5, discount_pct = $6,
                    terms = $7, shop_or_mobile = $8, spec_instructions = $9, wo_instructions = $10, note_printed = $11, note_internal = $12,
                    loc_address1 = $13, loc_address2 = $14, loc_city = $15, loc_state = $16, loc_zip = $17, loss_date = $18, policy_number = $19, claim_number = $20,
                    customer_id = $21, updated_at = now(), updated_by = $22
                WHERE id = $23
                """, cn, tx);
            Header(up, input, calc, null);
            up.Parameters.AddWithValue(input.CustomerId!.Value); up.Parameters.AddWithValue(user.UserId()); up.Parameters.AddWithValue(id);
            await up.ExecuteNonQueryAsync(ct);
            if (oldCustomer != input.CustomerId)   // a different customer: refresh the printed name/address/phones
            {
                var s = await Snapshot(cn, tx, input.CustomerId!.Value, ct);
                await using var sn = new NpgsqlCommand("UPDATE document SET cust_last_name=$1, cust_first_name=$2, cust_address1=$3, cust_address2=$4, cust_city=$5, cust_state=$6, cust_zip=$7, cust_phones=$8 WHERE id=$9", cn, tx);
                sn.Parameters.Add(P(s.Item1, NpgsqlDbType.Text)); sn.Parameters.Add(P(s.Item2, NpgsqlDbType.Text)); sn.Parameters.Add(P(s.Item3, NpgsqlDbType.Text));
                sn.Parameters.Add(P(s.Item4, NpgsqlDbType.Text)); sn.Parameters.Add(P(s.Item5, NpgsqlDbType.Text)); sn.Parameters.Add(P(s.Item6, NpgsqlDbType.Char));
                sn.Parameters.Add(P(s.Item7, NpgsqlDbType.Text)); sn.Parameters.Add(P(s.Item8, NpgsqlDbType.Array | NpgsqlDbType.Text)); sn.Parameters.AddWithValue(id);
                await sn.ExecuteNonQueryAsync(ct);
            }
            foreach (var t in new[] { "document_line", "document_tax" })
            {
                await using var del = new NpgsqlCommand($"DELETE FROM {t} WHERE document_id = $1", cn, tx);
                del.Parameters.AddWithValue(id); await del.ExecuteNonQueryAsync(ct);
            }
            await WriteLines(cn, tx, id, calc, ct);
            await using (var rev = new NpgsqlCommand("INSERT INTO document_revision (at, document_id, total, created_by, comment) VALUES (now(), $1, $2, $3, $4)", cn, tx))
            {
                rev.Parameters.AddWithValue(id); rev.Parameters.AddWithValue(calc.Total); rev.Parameters.AddWithValue(user.UserId());
                rev.Parameters.AddWithValue(oldTotal == calc.Total ? "Edited" : $"Edited (was {oldTotal:0.00})");
                await rev.ExecuteNonQueryAsync(ct);
            }
            if (type == "I") await Ledger.SyncInvoiceDebit(cn, tx, id, input.CustomerId!.Value, calc.Total, user.UserId(), ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { total = calc.Total });
        }
        catch (PricingException e) { return Results.BadRequest(new { error = e.Message }); }
    }

    static async Task<IResult> Void(int id, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        string type; bool voided;
        await using (var e = new NpgsqlCommand("SELECT doc_type, voided FROM document WHERE id = $1 FOR UPDATE", cn, tx))
        {
            e.Parameters.AddWithValue(id);
            await using var r = await e.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            (type, voided) = (r.GetString(0).Trim(), r.GetBoolean(1));
        }
        if (voided) return Results.Json(new { error = "Already voided." }, statusCode: 409);
        if (type == "R") return Results.Json(new { error = "This document type cannot be voided." }, statusCode: 409);
        if (await Ledger.AppliedPayments(cn, tx, id, ct) != 0)
            return Results.Json(new { error = "Payments are applied to this document. Void those payments first." }, statusCode: 409);
        await Ledger.ReverseDocument(cn, tx, id, user.UserId(), ct);
        await using (var c = new NpgsqlCommand("UPDATE document SET voided = true, updated_at = now(), updated_by = $2 WHERE id = $1", cn, tx))
        { c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(user.UserId()); await c.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
