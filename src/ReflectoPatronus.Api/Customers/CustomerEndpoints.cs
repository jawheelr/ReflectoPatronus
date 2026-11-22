using Npgsql;
using NpgsqlTypes;
using ReflectoPatronus.Api.Auth;

namespace ReflectoPatronus.Api.Customers;

public record CustomerSummary(int Id, string Number, string LastName, string? FirstName, string? City, string? State,
    string? Phone, decimal Balance);
public record CustomerPhone(int Slot, string Number, string? Ext);
public record CustomerDetail(int Id, string Number, string Type, string LastName, string? FirstName, string? Address1,
    string? Address2, string? City, string? State, string? Zip, string? Email, string? ContactName, string? TaxGroup,
    bool TaxExempt, string? TaxId, string? PriceCategory, string? Category, bool PrintStatement, bool NoPrices,
    decimal? CreditLimit, DateOnly? LastTransactionOn, string? NotePrinted, string? NoteInternal, decimal Balance,
    IReadOnlyList<CustomerPhone> Phones, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>Create/replace payload. UpdatedAt is the value read with the record (optimistic concurrency); ignored on create.</summary>
public record CustomerInput(string? Number, string? Type, string? LastName, string? FirstName, string? Address1, string? Address2,
    string? City, string? State, string? Zip, string? Email, string? ContactName, string? TaxGroup, bool TaxExempt, string? TaxId,
    string? PriceCategory, string? Category, bool PrintStatement, bool NoPrices, decimal? CreditLimit, string? NotePrinted,
    string? NoteInternal, IReadOnlyList<CustomerPhone>? Phones, DateTime? UpdatedAt);
public record CodeName(string Code, string? Description);
public record CustomerLookups(IReadOnlyList<CodeName> PriceCategories, IReadOnlyList<CodeName> CustomerCategories,
    IReadOnlyList<CodeName> TaxGroups, IReadOnlyList<string> Types);
public record Page<T>(IReadOnlyList<T> Items, int Limit, int Offset, bool HasMore);

public static class CustomerEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/customers");
        g.MapGet("", Search).RequirePermission("customers.read");
        g.MapGet("/{id:int}", GetById).RequirePermission("customers.read");
        g.MapPost("", Create).RequirePermission("customers.write");
        g.MapPut("/{id:int}", Update).RequirePermission("customers.write");
        g.MapDelete("/{id:int}", Delete).RequirePermission("customers.delete");   // rarer and irreversible
        api.MapGet("/lookups/customer", Lookups);   // category/tax lists every form needs
    }

    /// <summary>
    /// q: digits only = phone prefix; otherwise name fragment (trigram-indexed) or account-number prefix. Empty q lists by name.
    /// </summary>
    static async Task<IResult> Search(string? q, string? sort, int? limit, int? offset, NpgsqlDataSource db, CancellationToken ct)
    {
        var lim = Math.Clamp(limit ?? 25, 1, 100);
        var off = Math.Max(offset ?? 0, 0);
        q = q?.Trim();
        var digits = !string.IsNullOrEmpty(q) && q.All(char.IsDigit);

        // Ask for one extra row to know whether another page exists, without a COUNT(*).
        await using var cmd = db.CreateCommand($"""
            SELECT c.id, c.number, c.last_name, c.first_name, c.city, c.state, ph.number,
                   coalesce((SELECT sum(amount) FROM ar_entry a WHERE a.customer_id = c.id), 0)
            FROM customer c
            LEFT JOIN LATERAL (SELECT number FROM customer_phone WHERE customer_id = c.id ORDER BY slot LIMIT 1) ph ON true
            WHERE $1::text IS NULL
               OR ({(digits ? "c.number LIKE $1 || '%' OR EXISTS (SELECT 1 FROM customer_phone p WHERE p.customer_id = c.id AND p.number LIKE $1 || '%')"
                            : "lower(c.last_name || ' ' || coalesce(c.first_name, '')) LIKE '%' || lower($1) || '%' OR upper(c.number) LIKE upper($1) || '%'")})
            ORDER BY {(sort == "code" ? "upper(c.number), c.id" : "lower(c.last_name), lower(coalesce(c.first_name, '')), c.id")}
            LIMIT $2 OFFSET $3
            """);
        cmd.Parameters.AddWithValue((object?)(string.IsNullOrEmpty(q) ? null : EscapeLike(q)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue(lim + 1);
        cmd.Parameters.AddWithValue(off);
        var items = new List<CustomerSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new CustomerSummary(r.GetInt32(0), r.GetString(1), r.GetString(2), Str(r, 3), Str(r, 4), Str(r, 5), Str(r, 6), r.GetDecimal(7)));
        var more = items.Count > lim;
        return Results.Ok(new Page<CustomerSummary>(more ? items[..lim] : items, lim, off, more));
    }

    static async Task<IResult> GetById(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT c.id, c.number, c.type, c.last_name, c.first_name, c.address1, c.address2, c.city, c.state, c.zip, c.email,
                   c.contact_name, c.tax_group, c.tax_exempt, c.tax_id, c.price_category, c.category, c.print_statement,
                   c.no_prices, c.credit_limit, c.last_transaction_on, c.note_printed, c.note_internal,
                   coalesce((SELECT sum(amount) FROM ar_entry a WHERE a.customer_id = c.id), 0), c.created_at, c.updated_at
            FROM customer c WHERE c.id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        CustomerDetail? partial = null;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct))
                partial = new CustomerDetail(r.GetInt32(0), r.GetString(1), r.GetString(2).Trim(), r.GetString(3), Str(r, 4), Str(r, 5),
                    Str(r, 6), Str(r, 7), Str(r, 8)?.Trim(), Str(r, 9), Str(r, 10), Str(r, 11), Str(r, 12), r.GetBoolean(13), Str(r, 14),
                    Str(r, 15), Str(r, 16), r.GetBoolean(17), r.GetBoolean(18),
                    r.IsDBNull(19) ? null : r.GetDecimal(19), r.IsDBNull(20) ? null : DateOnly.FromDateTime(r.GetDateTime(20)),
                    Str(r, 21), Str(r, 22), r.GetDecimal(23), [], r.GetDateTime(24), r.GetDateTime(25));
        if (partial is null) return Results.NotFound();

        await using var pc = db.CreateCommand("SELECT slot, number, ext FROM customer_phone WHERE customer_id = $1 ORDER BY slot");
        pc.Parameters.AddWithValue(id);
        var phones = new List<CustomerPhone>();
        await using var pr = await pc.ExecuteReaderAsync(ct);
        while (await pr.ReadAsync(ct)) phones.Add(new CustomerPhone(pr.GetInt16(0), pr.GetString(1), Str(pr, 2)));
        return Results.Ok(partial with { Phones = phones });
    }

    // ---- editing ----

    static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Returns the cleaned input (trimmed, normalised), or an error message.</summary>
    static (CustomerInput? Clean, string? Error) Validate(CustomerInput i)
    {
        var number = N(i.Number); var type = N(i.Type)?.ToUpperInvariant(); var last = N(i.LastName) ?? "";
        if (number is null || number.Length > 20) return (null, "Code is required (20 characters at most).");
        if (type is null || type.Length != 1 || !char.IsAsciiLetter(type[0])) return (null, "Type must be a single letter.");
        if (last.Length == 0 && N(i.FirstName) is null) return (null, "Enter a last name (or at least a first name).");
        var state = N(i.State)?.ToUpperInvariant();
        if (state is not null && state.Length != 2) return (null, "State must be two letters.");
        var email = N(i.Email);
        if (email is not null && (email.Length > 254 || !email.Contains('@'))) return (null, "Email address looks invalid.");
        if (i.CreditLimit is < 0 or > 999_999_999_999m) return (null, "Credit limit is out of range.");
        var phones = new List<CustomerPhone>();
        foreach (var p in i.Phones ?? [])
        {
            var num = N(p.Number); if (num is null) continue;
            if (p.Slot is < 1 or > 5) return (null, "Phone slot must be 1-5.");
            if (phones.Any(x => x.Slot == p.Slot)) return (null, "Duplicate phone slot.");
            phones.Add(new CustomerPhone(p.Slot, num, N(p.Ext)));
        }
        return (i with { Number = number, Type = type, LastName = last, FirstName = N(i.FirstName), Address1 = N(i.Address1),
            Address2 = N(i.Address2), City = N(i.City), State = state, Zip = N(i.Zip), Email = email, ContactName = N(i.ContactName),
            TaxGroup = N(i.TaxGroup), TaxId = N(i.TaxId), PriceCategory = N(i.PriceCategory), Category = N(i.Category),
            NotePrinted = N(i.NotePrinted), NoteInternal = N(i.NoteInternal), Phones = phones }, null);
    }

    static NpgsqlParameter P(object? v, NpgsqlDbType t) => new() { NpgsqlDbType = t, Value = v ?? DBNull.Value };

    static void BindCommon(NpgsqlCommand c, CustomerInput i)
    {   // $1..$21 in both INSERT and UPDATE
        c.Parameters.Add(P(i.Number, NpgsqlDbType.Text)); c.Parameters.Add(P(i.Type, NpgsqlDbType.Char));
        c.Parameters.Add(P(i.LastName, NpgsqlDbType.Text)); c.Parameters.Add(P(i.FirstName, NpgsqlDbType.Text));
        c.Parameters.Add(P(i.Address1, NpgsqlDbType.Text)); c.Parameters.Add(P(i.Address2, NpgsqlDbType.Text));
        c.Parameters.Add(P(i.City, NpgsqlDbType.Text)); c.Parameters.Add(P(i.State, NpgsqlDbType.Char));
        c.Parameters.Add(P(i.Zip, NpgsqlDbType.Text)); c.Parameters.Add(P(i.Email, NpgsqlDbType.Text));
        c.Parameters.Add(P(i.ContactName, NpgsqlDbType.Text)); c.Parameters.Add(P(i.TaxGroup, NpgsqlDbType.Text));
        c.Parameters.Add(P(i.TaxExempt, NpgsqlDbType.Boolean)); c.Parameters.Add(P(i.TaxId, NpgsqlDbType.Text));
        c.Parameters.Add(P(i.PriceCategory, NpgsqlDbType.Text)); c.Parameters.Add(P(i.Category, NpgsqlDbType.Text));
        c.Parameters.Add(P(i.PrintStatement, NpgsqlDbType.Boolean)); c.Parameters.Add(P(i.NoPrices, NpgsqlDbType.Boolean));
        c.Parameters.Add(P(i.CreditLimit, NpgsqlDbType.Numeric)); c.Parameters.Add(P(i.NotePrinted, NpgsqlDbType.Text));
        c.Parameters.Add(P(i.NoteInternal, NpgsqlDbType.Text));
    }

    static IResult Problem(PostgresException e) => e.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation => Results.Json(new { error = "That customer code is already in use." }, statusCode: 409),
        PostgresErrorCodes.ForeignKeyViolation => Results.BadRequest(new { error = "Unknown tax group, price category or customer category." }),
        PostgresErrorCodes.StringDataRightTruncation => Results.BadRequest(new { error = "A value is too long." }),
        _ => throw e,
    };

    static async Task WritePhones(NpgsqlConnection cn, NpgsqlTransaction tx, int id, IReadOnlyList<CustomerPhone> phones, CancellationToken ct)
    {
        await using (var del = new NpgsqlCommand("DELETE FROM customer_phone WHERE customer_id = $1", cn, tx))
        { del.Parameters.AddWithValue(id); await del.ExecuteNonQueryAsync(ct); }
        foreach (var p in phones)
        {
            await using var ins = new NpgsqlCommand("INSERT INTO customer_phone (customer_id, slot, number, ext) VALUES ($1, $2, $3, $4)", cn, tx);
            ins.Parameters.AddWithValue(id); ins.Parameters.AddWithValue((short)p.Slot); ins.Parameters.AddWithValue(p.Number);
            ins.Parameters.Add(P(p.Ext, NpgsqlDbType.Text));
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    const string Cols = "number, type, last_name, first_name, address1, address2, city, state, zip, email, contact_name, tax_group, tax_exempt, tax_id, price_category, category, print_statement, no_prices, credit_limit, note_printed, note_internal";

    static async Task<IResult> Create(CustomerInput input, System.Security.Claims.ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        var (i, error) = Validate(input);
        if (i is null) return Results.BadRequest(new { error });
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand($"INSERT INTO customer ({Cols}, created_by, updated_by) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$22) RETURNING id", cn, tx);
            BindCommon(cmd, i); cmd.Parameters.AddWithValue(user.UserId());
            var id = (int)(await cmd.ExecuteScalarAsync(ct))!;
            await WritePhones(cn, tx, id, i.Phones!, ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/customers/{id}", new { id });
        }
        catch (PostgresException e) { return Problem(e); }
    }

    static async Task<IResult> Update(int id, CustomerInput input, System.Security.Claims.ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct)
    {
        var (i, error) = Validate(input);
        if (i is null) return Results.BadRequest(new { error });
        if (i.UpdatedAt is null) return Results.BadRequest(new { error = "updatedAt (from the record you loaded) is required." });
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand("""
                UPDATE customer SET number=$1, type=$2, last_name=$3, first_name=$4, address1=$5, address2=$6, city=$7, state=$8, zip=$9,
                    email=$10, contact_name=$11, tax_group=$12, tax_exempt=$13, tax_id=$14, price_category=$15, category=$16,
                    print_statement=$17, no_prices=$18, credit_limit=$19, note_printed=$20, note_internal=$21,
                    updated_at = now(), updated_by = $22
                WHERE id = $23 AND updated_at = $24
                """, cn, tx);
            BindCommon(cmd, i); cmd.Parameters.AddWithValue(user.UserId()); cmd.Parameters.AddWithValue(id);
            cmd.Parameters.Add(P(DateTime.SpecifyKind(i.UpdatedAt.Value, DateTimeKind.Utc), NpgsqlDbType.TimestampTz));
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            {
                await using var ex = new NpgsqlCommand("SELECT 1 FROM customer WHERE id = $1", cn, tx);
                ex.Parameters.AddWithValue(id);
                return await ex.ExecuteScalarAsync(ct) is null ? Results.NotFound()
                    : Results.Json(new { error = "Someone else changed this customer since you opened it. Reload it and try again." }, statusCode: 409);
            }
            await WritePhones(cn, tx, id, i.Phones!, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }
        catch (PostgresException e) { return Problem(e); }
    }

    /// <summary>Refused while the customer has documents or ledger entries: history is never orphaned.</summary>
    static async Task<IResult> Delete(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        try
        {
            await using var cmd = db.CreateCommand("DELETE FROM customer WHERE id = $1");
            cmd.Parameters.AddWithValue(id);
            return await cmd.ExecuteNonQueryAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return Results.Json(new { error = "This customer has documents or ledger entries and cannot be deleted." }, statusCode: 409);
        }
    }

    static async Task<IResult> Lookups(NpgsqlDataSource db, CancellationToken ct)
    {
        async Task<List<CodeName>> Codes(string sql)
        {
            var l = new List<CodeName>(); await using var c = db.CreateCommand(sql); await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) l.Add(new CodeName(r.GetString(0), Str(r, 1)));
            return l;
        }
        var types = new List<string>();
        await using (var c = db.CreateCommand("SELECT DISTINCT type FROM customer ORDER BY 1"))
        await using (var r = await c.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) types.Add(r.GetString(0).Trim());
        foreach (var t in new[] { "A", "B" }) if (!types.Contains(t)) types.Add(t);
        types.Sort(StringComparer.Ordinal);
        return Results.Ok(new CustomerLookups(
            await Codes("SELECT code, description FROM price_category ORDER BY code"),
            await Codes("SELECT code, description FROM customer_category ORDER BY code"),
            await Codes("SELECT code, description FROM tax_group ORDER BY code"), types));
    }

    internal static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>User text is matched literally: escape LIKE wildcards.</summary>
    internal static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
