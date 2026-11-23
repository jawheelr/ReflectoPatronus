using Npgsql;
using ReflectoPatronus.Api.Customers;

namespace ReflectoPatronus.Api.Company;

public record CompanyInfo(int Id, string Name, string? Address1, string? Address2, string? City, string? State, string? Zip,
    string? Phone, string? Fax, string? Email, string? InvoiceNote, string? Disclaimer, bool UseDisclaimer, string? TimeZone, bool HasLogo = false);

/// <summary>The shop's own details, printed at the top of every document.</summary>
public static class CompanyEndpoints
{
    public const int MaxLogoBytes = 1024 * 1024;
    static readonly HashSet<string> LogoTypes = ["image/png", "image/jpeg", "image/webp", "image/svg+xml"];

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/company", Get);
        api.MapGet("/company/logo", async (NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
        {
            await using var c = db.CreateCommand("SELECT logo, logo_type FROM company ORDER BY id LIMIT 1");
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || r.IsDBNull(0)) return Results.NotFound();
            // SVG can carry script; the CSP sandbox keeps it inert even if someone opens the URL directly.
            http.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; style-src 'unsafe-inline'"; http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File((byte[])r.GetValue(0), r.GetString(1), enableRangeProcessing: false);
        });
        api.MapPut("/company/logo", async (HttpRequest req, NpgsqlDataSource db, HttpContext ctx, CancellationToken ct) =>
        {
            if (!await Auth.Permissions.Has(ctx, "company.write")) return Results.Forbid();
            var type = (req.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            if (!LogoTypes.Contains(type)) return Results.BadRequest(new { error = "The logo must be a PNG, JPEG, WebP or SVG image." });
            using var ms = new MemoryStream();
            var buf = new byte[16384]; int n;
            while ((n = await req.Body.ReadAsync(buf, ct)) > 0) { ms.Write(buf, 0, n); if (ms.Length > MaxLogoBytes) return Results.BadRequest(new { error = "The logo is larger than 1 MB. Use a smaller image." }); }
            if (ms.Length == 0) return Results.BadRequest(new { error = "No image was sent." });
            await using var c = db.CreateCommand("UPDATE company SET logo = $1, logo_type = $2 WHERE id = (SELECT id FROM company ORDER BY id LIMIT 1)");
            c.Parameters.AddWithValue(ms.ToArray()); c.Parameters.AddWithValue(type);
            await c.ExecuteNonQueryAsync(ct);
            return Results.NoContent();
        });
        api.MapDelete("/company/logo", async (NpgsqlDataSource db, HttpContext ctx, CancellationToken ct) =>
        {
            if (!await Auth.Permissions.Has(ctx, "company.write")) return Results.Forbid();
            await using var c = db.CreateCommand("UPDATE company SET logo = NULL, logo_type = NULL");
            await c.ExecuteNonQueryAsync(ct); return Results.NoContent();
        });
    }

    static async Task<IResult> Get(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT id, name, address1, address2, city, state, zip, phone, fax, email, invoice_note, disclaimer, use_disclaimer, time_zone, logo IS NOT NULL
            FROM company ORDER BY id LIMIT 1
            """);
        await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return Results.NotFound();
        var s = (int i) => CustomerEndpoints.Str(r, i)?.Trim();
        return Results.Ok(new CompanyInfo(r.GetInt32(0), r.GetString(1), s(2), s(3), s(4), s(5), s(6), s(7), s(8), s(9), s(10), s(11), r.GetBoolean(12), s(13), r.GetBoolean(14)));
    }
}
