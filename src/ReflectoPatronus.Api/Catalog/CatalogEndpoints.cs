using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;
using ReflectoPatronus.Api.Auth;
using ReflectoPatronus.Api.Customers;

namespace ReflectoPatronus.Api.Catalog;

public record PriceRow(string? Category, decimal? Price1, decimal? Price2, decimal? Price3, decimal? Price4, decimal? MinPrice, decimal? LaborPrice, decimal? MinLabor, bool Taxable);
public record ThickRow(short Num, short Den, string? MarkupCategory, IReadOnlyList<PriceRow> Prices);
public record ItemInput(string? Code, string? Source, string? Description, short? UomId, short? LaborUomId, string? NotePrinted, string? NoteInternal,
    IReadOnlyList<ThickRow>? Thicknesses, DateTime? UpdatedAt);
public record ItemSummary(int Id, string Code, string? Description, string Source, int Thicknesses, string? Unit, string? ThicknessList, decimal? Price);
public record ItemDetail(int Id, string Code, string Source, string? Description, short? UomId, short? LaborUomId, string? NotePrinted, string? NoteInternal,
    IReadOnlyList<ThickRow> Thicknesses, DateTime UpdatedAt);
public record LossRow(short ThickNum, short ThickDen, short LossInt, short LossNum, short LossDen);
public record SpecialPrice(int PartId, string PartCode, short ThickNum, short ThickDen, string? PriceCategory, decimal Price);
public record SpecialPriceInput(int PartId, short ThickNum, short ThickDen, string? PriceCategory, decimal Price);

/// <summary>
/// The parts and pricing catalog: glass types and parts with their thicknesses and per-category prices, the waste (loss) table and
/// customer special prices. Everyone signed in can read; only administrators change prices. Saving a part is one transaction.
/// Prices are matched by (thickness, price category) and upserted, so supplier cost-increase history is never orphaned.
/// </summary>
public static class CatalogEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/catalog");
        g.MapGet("/items", List);
        g.MapGet("/items/{id:int}", Get);
        g.MapPost("/items", Create).RequirePermission("catalog.write");
        g.MapPut("/items/{id:int}", Update).RequirePermission("catalog.write");
        g.MapDelete("/items/{id:int}", Delete).RequirePermission("catalog.write");
        g.MapGet("/uoms", async (NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var c = db.CreateCommand("SELECT id, name, short_name FROM unit_of_measure ORDER BY id");
            var l = new List<object>(); await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) l.Add(new { id = r.GetInt16(0), name = r.GetString(1), shortName = CustomerEndpoints.Str(r, 2) });
            return Results.Ok(l);
        });
        g.MapGet("/loss", GetLoss);
        g.MapPut("/loss", PutLoss).RequirePermission("catalog.write");
        api.MapGet("/customers/{id:int}/prices", GetSpecial).RequirePermission("customers.read");
        api.MapPut("/customers/{id:int}/prices", PutSpecial).RequirePermission("customers.write");
    }

    static NpgsqlParameter P(object? v, NpgsqlDbType t) => new() { NpgsqlDbType = t, Value = v ?? DBNull.Value };
    static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    static IResult Bad(string m) => Results.BadRequest(new { error = m });
    static IResult Conflict(string m) => Results.Json(new { error = m }, statusCode: 409);

    static async Task<IResult> List(string? q, string? source, int? limit, NpgsqlDataSource db, CancellationToken ct)
    {
        q = N(q); var src = source?.ToUpperInvariant() is "G" or "P" ? source.ToUpperInvariant() : null;
        await using var c = db.CreateCommand("""
            SELECT p.id, p.code, p.description, p.source, (SELECT count(*) FROM part_thickness WHERE part_id = p.id), u.short_name,
                   (SELECT string_agg(CASE WHEN thick_den = 0 THEN '-' ELSE thick_num || '/' || thick_den END, ', ' ORDER BY thick_den, thick_num) FROM part_thickness WHERE part_id = p.id),
                   (SELECT coalesce(nullif(x.price1, 0), nullif(x.price2, 0), nullif(x.price3, 0), nullif(x.price4, 0)) FROM part_price x JOIN part_thickness th ON th.id = x.part_thickness_id
                    WHERE th.part_id = p.id ORDER BY (x.price_category IS NULL), x.price_category, th.thick_den, th.thick_num LIMIT 1)
            FROM part p LEFT JOIN unit_of_measure u ON u.id = p.uom_id
            WHERE ($1::text IS NULL OR upper(p.code) LIKE '%' || upper($1) || '%' OR lower(coalesce(p.description, '')) LIKE '%' || lower($1) || '%')
              AND ($2::text IS NULL OR p.source = $2)
            ORDER BY upper(p.code) LIMIT $3
            """);
        c.Parameters.Add(P(q is null ? null : CustomerEndpoints.EscapeLike(q), NpgsqlDbType.Text)); c.Parameters.Add(P(src, NpgsqlDbType.Char)); c.Parameters.AddWithValue(Math.Clamp(limit ?? 300, 1, 1000));
        var l = new List<ItemSummary>(); await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) l.Add(new(r.GetInt32(0), r.GetString(1), CustomerEndpoints.Str(r, 2), r.GetString(3).Trim(), (int)r.GetInt64(4), CustomerEndpoints.Str(r, 5), CustomerEndpoints.Str(r, 6), r.IsDBNull(7) ? null : r.GetDecimal(7)));
        return Results.Ok(l);
    }

    static async Task<IResult> Get(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cn = await db.OpenConnectionAsync(ct);
        var d = await Load(cn, null, id, ct);
        return d is null ? Results.NotFound() : Results.Ok(d);
    }

    static async Task<ItemDetail?> Load(NpgsqlConnection cn, NpgsqlTransaction? tx, int id, CancellationToken ct)
    {
        string code, source; string? desc, np, ni; short? uom, luom; DateTime upd;
        await using (var c = new NpgsqlCommand("SELECT code, source, description, uom_id, labor_uom_id, note_printed, note_internal, updated_at FROM part WHERE id = $1", cn, tx))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (code, source, desc) = (r.GetString(0), r.GetString(1).Trim(), CustomerEndpoints.Str(r, 2));
            uom = r.IsDBNull(3) ? null : r.GetInt16(3); luom = r.IsDBNull(4) ? null : r.GetInt16(4);
            (np, ni, upd) = (CustomerEndpoints.Str(r, 5), CustomerEndpoints.Str(r, 6), r.GetDateTime(7));
        }
        var th = new Dictionary<int, (short, short, string?, List<PriceRow>)>();
        await using (var c = new NpgsqlCommand("SELECT id, thick_num, thick_den, markup_category FROM part_thickness WHERE part_id = $1 ORDER BY thick_den, thick_num", cn, tx))
        {
            c.Parameters.AddWithValue(id);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) th[r.GetInt32(0)] = (r.GetInt16(1), r.GetInt16(2), CustomerEndpoints.Str(r, 3), []);
        }
        await using (var c = new NpgsqlCommand("""
            SELECT part_thickness_id, price_category, price1, price2, price3, price4, min_price, labor_price, min_labor, taxable
            FROM part_price WHERE part_thickness_id = ANY($1) ORDER BY price_category NULLS FIRST
            """, cn, tx))
        {
            c.Parameters.AddWithValue(th.Keys.ToArray());
            await using var r = await c.ExecuteReaderAsync(ct);
            decimal? D(int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
            while (await r.ReadAsync(ct)) th[r.GetInt32(0)].Item4.Add(new(CustomerEndpoints.Str(r, 1), D(2), D(3), D(4), D(5), D(6), D(7), D(8), r.GetBoolean(9)));
        }
        return new ItemDetail(id, code, source, desc, uom, luom, np, ni, th.Values.Select(t => new ThickRow(t.Item1, t.Item2, t.Item3, t.Item4)).ToList(), upd);
    }

    static string? Validate(ItemInput i)
    {
        if (N(i.Code) is not { } code) return "Code is required.";
        if (code.Length > 30) return "Code is too long (max 30).";
        if (i.Source is not ("G" or "P")) return "Type must be glass (G) or part (P).";
        if ((N(i.Description)?.Length ?? 0) > 120) return "Description is too long (max 120).";
        var seen = new HashSet<(short, short)>();
        foreach (var t in i.Thicknesses ?? [])
        {
            if (t.Den < 0 || t.Num < 0 || t.Den > 64 || t.Num > 999) return "A thickness is out of range.";
            if (t.Den == 0 && t.Num != 0) return "A thickness with no denominator must be 0/0 (no thickness).";
            if (!seen.Add((t.Num, t.Den))) return $"Thickness {t.Num}/{t.Den} is listed twice.";
            var cats = new HashSet<string?>();
            foreach (var p in t.Prices ?? [])
            {
                if (!cats.Add(N(p.Category))) return $"Thickness {t.Num}/{t.Den}: price category {p.Category ?? "(default)"} is listed twice.";
                foreach (var v in new[] { p.Price1, p.Price2, p.Price3, p.Price4, p.MinPrice, p.LaborPrice, p.MinLabor })
                    if (v is < 0 or > 99_999_999) return "Prices must be between 0 and 99,999,999.";
            }
        }
        return null;
    }

    static async Task Write(NpgsqlConnection cn, NpgsqlTransaction tx, int id, ItemInput i, short? uid, CancellationToken ct)
    {
        var keep = new List<int>();
        foreach (var t in i.Thicknesses ?? [])
        {
            int tid;
            await using (var c = new NpgsqlCommand("""
                INSERT INTO part_thickness (part_id, thick_num, thick_den, markup_category) VALUES ($1, $2, $3, $4)
                ON CONFLICT (part_id, thick_num, thick_den) DO UPDATE SET markup_category = EXCLUDED.markup_category RETURNING id
                """, cn, tx))
            {
                c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(t.Num); c.Parameters.AddWithValue(t.Den); c.Parameters.Add(P(N(t.MarkupCategory), NpgsqlDbType.Text));
                tid = (int)(await c.ExecuteScalarAsync(ct))!;
            }
            keep.Add(tid);
            var cats = new List<string?>();
            foreach (var p in t.Prices ?? [])
            {
                cats.Add(N(p.Category));
                await using var c = new NpgsqlCommand("""
                    INSERT INTO part_price (part_thickness_id, price_category, price1, price2, price3, price4, min_price, labor_price, min_labor, taxable)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                    ON CONFLICT (part_thickness_id, price_category) DO UPDATE SET price1 = EXCLUDED.price1, price2 = EXCLUDED.price2, price3 = EXCLUDED.price3,
                        price4 = EXCLUDED.price4, min_price = EXCLUDED.min_price, labor_price = EXCLUDED.labor_price, min_labor = EXCLUDED.min_labor, taxable = EXCLUDED.taxable
                    """, cn, tx);
                c.Parameters.AddWithValue(tid); c.Parameters.Add(P(N(p.Category), NpgsqlDbType.Text));
                foreach (var v in new[] { p.Price1, p.Price2, p.Price3, p.Price4, p.MinPrice, p.LaborPrice, p.MinLabor }) c.Parameters.Add(P(v, NpgsqlDbType.Numeric));
                c.Parameters.AddWithValue(p.Taxable);
                await c.ExecuteNonQueryAsync(ct);
            }
            await using (var c = new NpgsqlCommand("DELETE FROM part_price WHERE part_thickness_id = $1 AND NOT (coalesce(price_category, '') = ANY($2::text[]))", cn, tx))
            { c.Parameters.AddWithValue(tid); c.Parameters.Add(P(cats.Select(x => x ?? "").ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text)); await c.ExecuteNonQueryAsync(ct); }
        }
        await using (var c = new NpgsqlCommand("DELETE FROM part_thickness WHERE part_id = $1 AND NOT (id = ANY($2))", cn, tx))
        { c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(keep.ToArray()); await c.ExecuteNonQueryAsync(ct); }
    }

    static async Task<IResult> Create(ItemInput i, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (Validate(i) is { } err) return Bad(err);
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        try
        {
            int id;
            await using (var c = new NpgsqlCommand("""
                INSERT INTO part (code, source, description, uom_id, labor_uom_id, note_printed, note_internal, created_by, updated_by)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $8) RETURNING id
                """, cn, tx))
            {
                Header(c, i); c.Parameters.AddWithValue(user.UserId());
                id = (int)(await c.ExecuteScalarAsync(ct))!;
            }
            await Write(cn, tx, id, i, user.UserId(), ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/catalog/items/{id}", new { id });
        }
        catch (PostgresException e) when (e.SqlState == "23505") { return Conflict("A part with that code already exists."); }
        catch (PostgresException e) when (e.SqlState == "23503") { return Bad("Unknown price category or unit of measure."); }
    }

    static void Header(NpgsqlCommand c, ItemInput i)
    {
        c.Parameters.AddWithValue(N(i.Code)!); c.Parameters.Add(P(i.Source, NpgsqlDbType.Char)); c.Parameters.Add(P(N(i.Description), NpgsqlDbType.Text));
        c.Parameters.Add(P(i.UomId, NpgsqlDbType.Smallint)); c.Parameters.Add(P(i.LaborUomId, NpgsqlDbType.Smallint));
        c.Parameters.Add(P(N(i.NotePrinted), NpgsqlDbType.Text)); c.Parameters.Add(P(N(i.NoteInternal), NpgsqlDbType.Text));
    }

    static async Task<IResult> Update(int id, ItemInput i, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (Validate(i) is { } err) return Bad(err);
        if (i.UpdatedAt is null) return Bad("updatedAt (as read) is required.");
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        try
        {
            DateTime upd;
            await using (var c = new NpgsqlCommand("SELECT updated_at FROM part WHERE id = $1 FOR UPDATE", cn, tx))
            {
                c.Parameters.AddWithValue(id);
                if (await c.ExecuteScalarAsync(ct) is not DateTime u) return Results.NotFound();
                upd = u;
            }
            if (DateTime.SpecifyKind(i.UpdatedAt.Value, DateTimeKind.Utc) != DateTime.SpecifyKind(upd, DateTimeKind.Utc)) return Conflict("Someone else changed this part. Reload it and try again.");
            await using (var c = new NpgsqlCommand("UPDATE part SET code = $1, source = $2, description = $3, uom_id = $4, labor_uom_id = $5, note_printed = $6, note_internal = $7, updated_at = now(), updated_by = $9 WHERE id = $8", cn, tx))
            { Header(c, i); c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(user.UserId()); await c.ExecuteNonQueryAsync(ct); }
            await Write(cn, tx, id, i, user.UserId(), ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }
        catch (PostgresException e) when (e.SqlState == "23505") { return Conflict("A part with that code already exists."); }
        catch (PostgresException e) when (e.SqlState == "23503") { return Bad("Unknown price category or unit of measure."); }
    }

    static async Task<IResult> Delete(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT p.code, (SELECT count(DISTINCT document_id) FROM document_line WHERE part_code = p.code) FROM part p WHERE p.id = $1
            """);
        c.Parameters.AddWithValue(id);
        string code; long used;
        await using (var r = await c.ExecuteReaderAsync(ct)) { if (!await r.ReadAsync(ct)) return Results.NotFound(); (code, used) = (r.GetString(0), r.GetInt64(1)); }
        if (used > 0) return Conflict($"{code} is on {used} documents. Keep it (you can rename it or clear its prices) so history still makes sense.");
        await using var d = db.CreateCommand("DELETE FROM part WHERE id = $1");
        d.Parameters.AddWithValue(id); await d.ExecuteNonQueryAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> GetLoss(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("SELECT thick_num, thick_den, loss_int, loss_num, loss_den FROM glass_loss ORDER BY thick_den, thick_num");
        var l = new List<LossRow>(); await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) l.Add(new(r.GetInt16(0), r.GetInt16(1), r.GetInt16(2), r.GetInt16(3), r.GetInt16(4)));
        return Results.Ok(l);
    }

    /// <summary>Replaces the whole waste table (it is small). Allowance = whole inches + num/den, added to each side of the glass.</summary>
    static async Task<IResult> PutLoss(List<LossRow> rows, NpgsqlDataSource db, CancellationToken ct)
    {
        if (rows.Any(r => r.ThickNum < 0 || r.ThickDen < 1 || r.ThickDen > 64 || r.LossInt is < 0 or > 12 || r.LossNum < 0 || r.LossDen < 1 || r.LossDen > 64 || r.LossNum >= r.LossDen && r.LossNum != 0))
            return Bad("Each row needs a thickness (n/d), whole inches 0-12 and a fraction smaller than 1.");
        if (rows.GroupBy(r => (r.ThickNum, r.ThickDen)).Any(g => g.Count() > 1)) return Bad("A thickness is listed twice.");
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var c = new NpgsqlCommand("DELETE FROM glass_loss", cn, tx)) await c.ExecuteNonQueryAsync(ct);
        foreach (var r in rows)
        {
            await using var c = new NpgsqlCommand("INSERT INTO glass_loss (thick_num, thick_den, loss_int, loss_num, loss_den) VALUES ($1, $2, $3, $4, $5)", cn, tx);
            c.Parameters.AddWithValue(r.ThickNum); c.Parameters.AddWithValue(r.ThickDen); c.Parameters.AddWithValue(r.LossInt); c.Parameters.AddWithValue(r.LossNum); c.Parameters.AddWithValue(r.LossDen);
            await c.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> GetSpecial(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT s.part_id, p.code, s.thick_num, s.thick_den, s.price_category, s.price FROM customer_part_price s JOIN part p ON p.id = s.part_id
            WHERE s.customer_id = $1 ORDER BY upper(p.code), s.thick_den, s.thick_num
            """);
        c.Parameters.AddWithValue(id);
        var l = new List<SpecialPrice>(); await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) l.Add(new(r.GetInt32(0), r.GetString(1), r.GetInt16(2), r.GetInt16(3), CustomerEndpoints.Str(r, 4), r.GetDecimal(5)));
        return Results.Ok(l);
    }

    /// <summary>Replaces a customer's special prices (a customer has few). They override catalog prices on quotes.</summary>
    static async Task<IResult> PutSpecial(int id, List<SpecialPriceInput> rows, NpgsqlDataSource db, CancellationToken ct)
    {
        if (rows.Any(r => r.Price < 0 || r.Price > 99_999_999)) return Bad("Prices must be between 0 and 99,999,999.");
        if (rows.GroupBy(r => (r.PartId, r.ThickNum, r.ThickDen, Cat: N(r.PriceCategory))).Any(g => g.Count() > 1)) return Bad("The same part, thickness and category is listed twice.");
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        try
        {
            await using (var c = new NpgsqlCommand("SELECT 1 FROM customer WHERE id = $1 FOR UPDATE", cn, tx)) { c.Parameters.AddWithValue(id); if (await c.ExecuteScalarAsync(ct) is null) return Results.NotFound(); }
            await using (var c = new NpgsqlCommand("DELETE FROM customer_part_price WHERE customer_id = $1", cn, tx)) { c.Parameters.AddWithValue(id); await c.ExecuteNonQueryAsync(ct); }
            foreach (var r in rows)
            {
                await using var c = new NpgsqlCommand("INSERT INTO customer_part_price (customer_id, part_id, thick_num, thick_den, price_category, price) VALUES ($1, $2, $3, $4, $5, $6)", cn, tx);
                c.Parameters.AddWithValue(id); c.Parameters.AddWithValue(r.PartId); c.Parameters.AddWithValue(r.ThickNum); c.Parameters.AddWithValue(r.ThickDen);
                c.Parameters.Add(P(N(r.PriceCategory), NpgsqlDbType.Text)); c.Parameters.AddWithValue(r.Price);
                await c.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }
        catch (PostgresException e) when (e.SqlState == "23503") { return Bad("Unknown part or price category."); }
    }
}
