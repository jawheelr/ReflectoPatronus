using Npgsql;
using ReflectoPatronus.Api.Customers;

namespace ReflectoPatronus.Api.Company;

public record CompanyInfo(int Id, string Name, string? Address1, string? Address2, string? City, string? State, string? Zip,
    string? Phone, string? Fax, string? Email, string? InvoiceNote, string? Disclaimer, bool UseDisclaimer, string? TimeZone);

/// <summary>The shop's own details, printed at the top of every document.</summary>
public static class CompanyEndpoints
{
    public static void Map(RouteGroupBuilder api) => api.MapGet("/company", async (NpgsqlDataSource db, CancellationToken ct) =>
    {
        await using var c = db.CreateCommand("""
            SELECT id, name, address1, address2, city, state, zip, phone, fax, email, invoice_note, disclaimer, use_disclaimer, time_zone
            FROM company ORDER BY id LIMIT 1
            """);
        await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return Results.NotFound();
        var s = (int i) => CustomerEndpoints.Str(r, i)?.Trim();
        return Results.Ok(new CompanyInfo(r.GetInt32(0), r.GetString(1), s(2), s(3), s(4), s(5), s(6), s(7), s(8), s(9), s(10), s(11), r.GetBoolean(12), s(13)));
    });
}
