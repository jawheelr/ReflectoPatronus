using Npgsql;

namespace ReflectoPatronus.Api.Customers;

public record CustomerSummary(int Id, string Number, string LastName, string? FirstName, string? City, string? State,
    string? Phone, decimal Balance);
public record CustomerPhone(int Slot, string Number, string? Ext);
public record CustomerDetail(int Id, string Number, string Type, string LastName, string? FirstName, string? Address1,
    string? Address2, string? City, string? State, string? Zip, string? Email, string? ContactName, string? TaxGroup,
    bool TaxExempt, string? TaxId, string? PriceCategory, string? Category, bool PrintStatement, bool NoPrices,
    decimal? CreditLimit, DateOnly? LastTransactionOn, string? NotePrinted, string? NoteInternal, decimal Balance,
    IReadOnlyList<CustomerPhone> Phones);
public record Page<T>(IReadOnlyList<T> Items, int Limit, int Offset, bool HasMore);

public static class CustomerEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/customers");
        g.MapGet("", Search);
        g.MapGet("/{id:int}", GetById);
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
                   coalesce((SELECT sum(amount) FROM ar_entry a WHERE a.customer_id = c.id), 0)
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
                    Str(r, 21), Str(r, 22), r.GetDecimal(23), []);
        if (partial is null) return Results.NotFound();

        await using var pc = db.CreateCommand("SELECT slot, number, ext FROM customer_phone WHERE customer_id = $1 ORDER BY slot");
        pc.Parameters.AddWithValue(id);
        var phones = new List<CustomerPhone>();
        await using var pr = await pc.ExecuteReaderAsync(ct);
        while (await pr.ReadAsync(ct)) phones.Add(new CustomerPhone(pr.GetInt16(0), pr.GetString(1), Str(pr, 2)));
        return Results.Ok(partial with { Phones = phones });
    }

    internal static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>User text is matched literally: escape LIKE wildcards.</summary>
    internal static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
