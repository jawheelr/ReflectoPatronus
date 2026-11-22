using Npgsql;
using ReflectoPatronus.Api.Auth;
using ReflectoPatronus.Api.Customers;

namespace ReflectoPatronus.Api.Documents;

public record DocumentSummary(int Id, string Type, string Number, DateOnly Date, int? CustomerId, string? CustomerName,
    string? PoNumber, bool Voided, decimal Total);
public record DocumentLine(int Seq, int? ParentSeq, string Source, int? Qty, string? PartCode, string? Description, string? Dims,
    string? Shape, string? Thickness, decimal? UnitPrice, decimal? ListPrice, decimal Multiplier, decimal Amount, bool Taxable,
    short? ThickNum, short? ThickDen, decimal? WidthIn, decimal? HeightIn, int? PartId, decimal? Units, bool SpecialPrice, bool AmountOverride);
public record DocumentTaxLine(string Code, decimal Percent, decimal Amount);
public record DocumentInstaller(string Code, string? Name, decimal? Hours, decimal? Amount);
public record DocumentStatusEvent(DateTime? At, string Status, string? Note);
public record DocumentRevision(DateTime At, decimal? Total, string? Comment);
public record DocumentTotals(decimal Lines, decimal Taxable, decimal Tax, decimal Total, decimal Glass, decimal Part, decimal Labor, decimal Custom);
public record DocumentParty(string? Name, string? Address1, string? Address2, string? City, string? State, string? Zip,
    IReadOnlyList<string> Phones);
public record DocumentDetail(int Id, string Type, string TypeName, string Number, DateOnly Date, DateOnly? LossDate, bool Voided,
    int? CustomerId, int? BillToCustomerId, DocumentParty Customer, DocumentParty? JobSite, string? PoNumber, string? PolicyNumber,
    string? ClaimNumber, string? SourceDocumentNumber, string? Terms, string? SalesPerson, string? TaxGroup, string? PriceCategory,
    decimal? DiscountPercent, string? SpecialInstructions, string? WorkOrderInstructions, string? NotePrinted, string? NoteInternal,
    DateTime CreatedAt, DateTime? PrintedAt, DocumentTotals Totals, IReadOnlyList<DocumentLine> Lines,
    IReadOnlyList<DocumentTaxLine> Taxes, IReadOnlyList<DocumentInstaller> Installers,
    IReadOnlyList<DocumentStatusEvent> Statuses, IReadOnlyList<DocumentRevision> Revisions, string? ShopOrMobile,
    DateTime UpdatedAt, int? SalesPersonId, bool Editable, decimal Balance, decimal Paid);

public static class DocumentEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/documents");
        g.MapGet("", List).RequirePermission("documents.read");
        g.MapGet("/{id:int}", GetById).RequirePermission("documents.read");
        DocumentEdit.Map(api, g);
        DocumentWorkflow.Map(g);
    }

    static readonly string[] Types = ["Q", "W", "I", "C", "R"];

    /// <summary>
    /// type = Q|W|I|C (quote, work order, invoice, credit), customerId, q (document number, PO number or customer name),
    /// from/to (document date, inclusive). Voided documents are hidden unless includeVoided=true. Newest first.
    /// The page is selected first and totals are computed only for its rows.
    /// </summary>
    static async Task<IResult> List(string? type, int? customerId, string? q, DateOnly? from, DateOnly? to, bool? includeVoided,
        int? limit, int? offset, NpgsqlDataSource db, CancellationToken ct)
    {
        if (type is not null && !Types.Contains(type.ToUpperInvariant()))
            return Results.BadRequest(new { error = "type must be one of Q, W, I, C." });
        var lim = Math.Clamp(limit ?? 25, 1, 100);
        var off = Math.Max(offset ?? 0, 0);
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        await using var cmd = db.CreateCommand("""
            WITH page AS (
                SELECT d.id, d.doc_type, d.doc_prefix, d.doc_number, d.doc_suffix, d.doc_date, d.customer_id, d.po_number, d.voided,
                       nullif(trim(coalesce(d.cust_last_name, '') || ', ' || coalesce(d.cust_first_name, ''), ', '), '') AS cust_name
                FROM document d
                WHERE ($1::char IS NULL OR d.doc_type = $1)
                  AND ($2::int IS NULL OR d.customer_id = $2)
                  AND ($3::date IS NULL OR d.doc_date >= $3)
                  AND ($4::date IS NULL OR d.doc_date <= $4)
                  AND ($5 OR NOT d.voided)
                  AND ($6::text IS NULL
                       OR d.doc_number::text = $6
                       OR doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix) ILIKE $6 || '%'
                       OR d.po_number ILIKE $6 || '%'
                       OR lower(coalesce(d.cust_last_name, '') || ' ' || coalesce(d.cust_first_name, '')) LIKE '%' || lower($6) || '%')
                ORDER BY d.doc_date DESC, d.id DESC
                LIMIT $7 OFFSET $8)
            SELECT p.id, p.doc_type, doc_number_text(p.doc_prefix, p.doc_number, p.doc_suffix), p.doc_date, p.customer_id,
                   p.cust_name, p.po_number, p.voided,
                   coalesce(l.s, 0) + coalesce(t.s, 0)
            FROM page p
            LEFT JOIN LATERAL (SELECT sum(amount) s FROM document_line WHERE document_id = p.id) l ON true
            LEFT JOIN LATERAL (SELECT sum(amount) s FROM document_tax WHERE document_id = p.id) t ON true
            ORDER BY p.doc_date DESC, p.id DESC
            """);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Char, Value = (object?)type?.ToUpperInvariant() ?? DBNull.Value });
        cmd.Parameters.AddWithValue((object?)customerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Date, (object?)from ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Date, (object?)to ?? DBNull.Value);
        cmd.Parameters.AddWithValue(includeVoided ?? false);
        cmd.Parameters.AddWithValue((object?)(q is null ? null : CustomerEndpoints.EscapeLike(q)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue(lim + 1);
        cmd.Parameters.AddWithValue(off);

        var items = new List<DocumentSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new DocumentSummary(r.GetInt32(0), r.GetString(1).Trim(), r.GetString(2), DateOnly.FromDateTime(r.GetDateTime(3)),
                r.IsDBNull(4) ? null : r.GetInt32(4), CustomerEndpoints.Str(r, 5), CustomerEndpoints.Str(r, 6), r.GetBoolean(7), r.GetDecimal(8)));
        var more = items.Count > lim;
        return Results.Ok(new Page<DocumentSummary>(more ? items[..lim] : items, lim, off, more));
    }

    static async Task<IResult> GetById(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var h = db.CreateCommand("""
            SELECT d.doc_type, dt.name, doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix), d.doc_date, d.loss_date, d.voided,
                   d.customer_id, d.bill_to_customer_id,
                   nullif(trim(coalesce(d.cust_last_name, '') || ', ' || coalesce(d.cust_first_name, ''), ', '), ''),
                   d.cust_address1, d.cust_address2, d.cust_city, d.cust_state, d.cust_zip, d.cust_phones,
                   d.loc_address1, d.loc_address2, d.loc_city, d.loc_state, d.loc_zip,
                   d.po_number, d.policy_number, d.claim_number, d.source_doc_number, d.terms, s.name, d.tax_group, d.price_category,
                   d.discount_pct, d.spec_instructions, d.wo_instructions, d.note_printed, d.note_internal, d.created_at, d.printed_at, d.shop_or_mobile,
                   d.updated_at, d.staff_sales_id
            FROM document d
            JOIN doc_type dt ON dt.code = d.doc_type
            LEFT JOIN staff s ON s.id = d.staff_sales_id
            WHERE d.id = $1
            """);
        h.Parameters.AddWithValue(id);
        await using var r = await h.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return Results.NotFound();
        static string? S(NpgsqlDataReader x, int i) => CustomerEndpoints.Str(x, i)?.Trim();
        var phones = r.IsDBNull(14) ? [] : r.GetFieldValue<string[]>(14).ToList();
        var customer = new DocumentParty(S(r, 8), S(r, 9), S(r, 10), S(r, 11), S(r, 12), S(r, 13), phones);
        var hasSite = Enumerable.Range(15, 5).Any(i => !r.IsDBNull(i) && S(r, i) != "");
        var site = hasSite ? new DocumentParty(null, S(r, 15), S(r, 16), S(r, 17), S(r, 18), S(r, 19), []) : null;
        var typeCode = r.GetString(0).Trim(); var typeName = r.GetString(1);
        var number = r.GetString(2); var date = DateOnly.FromDateTime(r.GetDateTime(3));
        DateOnly? loss = r.IsDBNull(4) ? null : DateOnly.FromDateTime(r.GetDateTime(4));
        var voided = r.GetBoolean(5);
        int? custId = r.IsDBNull(6) ? null : r.GetInt32(6); int? billId = r.IsDBNull(7) ? null : r.GetInt32(7);
        string? po = S(r, 20), policy = S(r, 21), claim = S(r, 22), src = S(r, 23), terms = S(r, 24), sales = S(r, 25),
            taxGroup = S(r, 26), priceCat = S(r, 27);
        decimal? disc = r.IsDBNull(28) ? null : r.GetDecimal(28);
        string? spec = S(r, 29), wo = S(r, 30), notePrinted = S(r, 31), noteInternal = S(r, 32);
        var created = r.GetDateTime(33); DateTime? printed = r.IsDBNull(34) ? null : r.GetDateTime(34);
        var shopOrMobile = S(r, 35); var updatedAt = r.GetDateTime(36); int? salesId = r.IsDBNull(37) ? null : r.GetInt32(37);
        await r.CloseAsync();

        var lines = new List<DocumentLine>();
        await using (var c = db.CreateCommand("""
            SELECT seq, parent_seq, source::text, qty, part_code, description, dims, shape,
                   CASE WHEN thick_num IS NULL THEN NULL ELSE thickness_text(thick_num, thick_den) END,
                   unit_price, list_price, multiplier, amount, taxable, thick_num, thick_den, dims, (SELECT id FROM part WHERE upper(code) = upper(part_code)), uom_units,
                   special_price, amount_override
            FROM document_line WHERE document_id = $1 ORDER BY seq, id
            """))
        {
            c.Parameters.AddWithValue(id);
            await using var lr = await c.ExecuteReaderAsync(ct);
            while (await lr.ReadAsync(ct))
                lines.Add(new DocumentLine(lr.GetInt16(0), lr.IsDBNull(1) ? null : lr.GetInt16(1), lr.GetString(2),
                    lr.IsDBNull(3) ? null : lr.GetInt32(3), CustomerEndpoints.Str(lr, 4), CustomerEndpoints.Str(lr, 5),
                    CustomerEndpoints.Str(lr, 6), CustomerEndpoints.Str(lr, 7), CustomerEndpoints.Str(lr, 8),
                    lr.IsDBNull(9) ? null : lr.GetDecimal(9), lr.IsDBNull(10) ? null : lr.GetDecimal(10), lr.GetDecimal(11),
                    lr.GetDecimal(12), lr.GetBoolean(13), lr.IsDBNull(14) ? null : lr.GetInt16(14), lr.IsDBNull(15) ? null : lr.GetInt16(15),
                    PricingEngine.DecodeDims(CustomerEndpoints.Str(lr, 16))?.W, PricingEngine.DecodeDims(CustomerEndpoints.Str(lr, 16))?.H,
                    lr.IsDBNull(17) ? null : lr.GetInt32(17), lr.IsDBNull(18) ? null : lr.GetDecimal(18), lr.GetBoolean(19), lr.GetBoolean(20)));
        }

        var taxes = new List<DocumentTaxLine>();
        await using (var c = db.CreateCommand("SELECT tax_code, percent, amount FROM document_tax WHERE document_id = $1 ORDER BY tax_code"))
        {
            c.Parameters.AddWithValue(id);
            await using var tr = await c.ExecuteReaderAsync(ct);
            while (await tr.ReadAsync(ct)) taxes.Add(new DocumentTaxLine(tr.GetString(0), tr.GetDecimal(1), tr.GetDecimal(2)));
        }

        var installers = new List<DocumentInstaller>();
        await using (var c = db.CreateCommand("""
            SELECT i.installer_code, s.name, i.hours, i.amount FROM document_installer i
            LEFT JOIN staff s ON s.id = i.staff_id WHERE i.document_id = $1 ORDER BY i.id
            """))
        {
            c.Parameters.AddWithValue(id);
            await using var ir = await c.ExecuteReaderAsync(ct);
            while (await ir.ReadAsync(ct))
                installers.Add(new DocumentInstaller(ir.GetString(0), CustomerEndpoints.Str(ir, 1),
                    ir.IsDBNull(2) ? null : ir.GetDecimal(2), ir.IsDBNull(3) ? null : ir.GetDecimal(3)));
        }

        var statuses = new List<DocumentStatusEvent>();
        await using (var c = db.CreateCommand("""
            SELECT e.at, st.name, e.note FROM document_status_event e JOIN document_status st ON st.id = e.status_id
            WHERE e.document_id = $1 ORDER BY e.at NULLS FIRST, e.id
            """))
        {
            c.Parameters.AddWithValue(id);
            await using var sr = await c.ExecuteReaderAsync(ct);
            while (await sr.ReadAsync(ct))
                statuses.Add(new DocumentStatusEvent(sr.IsDBNull(0) ? null : sr.GetDateTime(0), sr.GetString(1), CustomerEndpoints.Str(sr, 2)));
        }

        var revisions = new List<DocumentRevision>();
        await using (var c = db.CreateCommand("SELECT at, total, comment FROM document_revision WHERE document_id = $1 ORDER BY at DESC, id DESC"))
        {
            c.Parameters.AddWithValue(id);
            await using var rr = await c.ExecuteReaderAsync(ct);
            while (await rr.ReadAsync(ct))
                revisions.Add(new DocumentRevision(rr.GetDateTime(0), rr.IsDBNull(1) ? null : rr.GetDecimal(1), CustomerEndpoints.Str(rr, 2)));
        }

        // Total = SUM(line amount) + SUM(tax amount): verified against the AR ledger (see docs/DB-DESIGN.md).
        decimal Sum(Func<DocumentLine, bool> f) => lines.Where(f).Sum(l => l.Amount);
        var linesTotal = lines.Sum(l => l.Amount); var taxTotal = taxes.Sum(t => t.Amount);
        var totals = new DocumentTotals(linesTotal, Sum(l => l.Taxable), taxTotal, linesTotal + taxTotal,
            Sum(l => l.Source == "glass"), Sum(l => l.Source == "part"), Sum(l => l.Source == "labor"), Sum(l => l.Source == "custom"));

        decimal balance = 0, paid = 0;
        await using (var c = db.CreateCommand("SELECT coalesce(sum(amount), 0), coalesce(-sum(amount) FILTER (WHERE purpose IN (4, 5)), 0) FROM ar_entry WHERE document_id = $1"))
        {
            c.Parameters.AddWithValue(id);
            await using var br = await c.ExecuteReaderAsync(ct);
            if (await br.ReadAsync(ct)) { balance = br.GetDecimal(0); paid = br.GetDecimal(1); }
        }

        return Results.Ok(new DocumentDetail(id, typeCode, typeName, number, date, loss, voided, custId, billId, customer, site, po, policy,
            claim, src, terms, sales, taxGroup, priceCat, disc, spec, wo, notePrinted, noteInternal, created, printed, totals,
            lines, taxes, installers, statuses, revisions, shopOrMobile, updatedAt, salesId, typeCode is "Q" or "W" or "I" && !voided, balance, paid));
    }
}
