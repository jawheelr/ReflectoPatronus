using Microsoft.AspNetCore.Authentication;
using Npgsql;
using ReflectoPatronus.Api.Auth;
using ReflectoPatronus.Api.Customers;
using ReflectoPatronus.Api.Documents;
using ReflectoPatronus.Api.Tax;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["Database:ConnectionString"]
    ?? throw new InvalidOperationException("Database:ConnectionString is required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<DatabaseProbe>();

builder.Services.AddAuthentication(AuthDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, TokenAuthHandler>(AuthDefaults.Scheme, null);
builder.Services.AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser().Build());   // everything requires a signed-in user unless it says AllowAnonymous
builder.Services.AddOpenApi();

var app = builder.Build();

// The React console (built by scripts/build-web.sh into wwwroot) is served from the same origin as the API: no CORS.
var serveWeb = File.Exists(Path.Combine(app.Environment.WebRootPath ?? "", "index.html"));
if (serveWeb)
{
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; base-uri 'none'";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        await next();
    });
    app.UseDefaultFiles();
    app.UseStaticFiles();
}
app.UseAuthentication();
app.UseAuthorization();

await AuthEndpoints.BootstrapAdminAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Configuration,
    app.Logger);

// With the console present, "/" is the SPA (a mapped endpoint would win over the static files).
if (!serveWeb)
    app.MapGet("/", () => Results.Ok(new { product = "ReflectoPatronus", apiVersion = "v1" })).AllowAnonymous();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (DatabaseProbe db, CancellationToken ct) =>
{
    var result = await db.CheckAsync(ct);
    return result.Ready ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

var api = app.MapGroup("/api/v1");
AuthEndpoints.Map(api);
MeEndpoints.Map(api);
CustomerEndpoints.Map(api);
DocumentEndpoints.Map(api);
TaxEndpoints.Map(api);
// Security status exposes TLS/cipher details of the DB link: signed-in users only (via the fallback policy).
api.MapGet("/security/status", async (DatabaseProbe db, CancellationToken ct) =>
{
    var result = await db.CheckAsync(ct);
    return result.Ready ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
});

// Client-side routes (/customers/12 ...) fall back to the SPA; API and health paths never do.
if (serveWeb)
    app.MapFallbackToFile("{*path:regex(^(?!api/|health/|openapi/|assets/).*$)}", "index.html").AllowAnonymous();

// The contract that the generated TypeScript and Swift clients are built from. Public in Development only.
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("OpenApi:Enabled"))
    app.MapOpenApi().AllowAnonymous();

app.Run();

sealed class DatabaseProbe(IConfiguration configuration)
{
    private readonly string _connectionString =
        configuration["Database:ConnectionString"]
        ?? throw new InvalidOperationException("Database:ConnectionString is required.");
    // When true, readiness is denied until PQC key exchange can be verified (currently impossible via pg_stat_ssl).
    private readonly bool _requirePqc = configuration.GetValue<bool>("Security:RequirePqcVerification");

    public async Task<SecurityProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand("""
                SELECT s.ssl, s.version, s.cipher, s.client_dn
                FROM pg_stat_ssl AS s
                WHERE s.pid = pg_backend_pid();
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return SecurityProbeResult.Failed("Could not read PostgreSQL TLS session details.");

            var tlsActive = reader.GetBoolean(0);
            var tlsVersion = reader.IsDBNull(1) ? null : reader.GetString(1);
            var cipher = reader.IsDBNull(2) ? null : reader.GetString(2);
            var clientDn = reader.IsDBNull(3) ? null : reader.GetString(3);
            var tls13 = string.Equals(tlsVersion, "TLSv1.3", StringComparison.OrdinalIgnoreCase);
            var clientCertificatePresented = !string.IsNullOrWhiteSpace(clientDn);

            // PostgreSQL pg_stat_ssl does not expose the negotiated TLS key-exchange group.
            // Do not claim PQC compliance from a TLS version/cipher check alone.
            var pqcVerified = false;
            return new SecurityProbeResult(
                Ready: tlsActive && tls13 && clientCertificatePresented && (!_requirePqc || pqcVerified),
                DatabaseConnected: true,
                TlsVersion: tlsVersion,
                Cipher: cipher,
                ClientCertificatePresented: clientCertificatePresented,
                PqcKeyExchangeVerified: pqcVerified,
                Message: _requirePqc
                    ? "mTLS/TLS session inspected. Readiness denied: post-quantum key exchange cannot be verified (pg_stat_ssl does not expose the negotiated group)."
                    : "mTLS/TLS session inspected. PQC verification is not required (Security:RequirePqcVerification=false); PQC is NOT verified.");
        }
        catch (Exception ex)
        {
            return SecurityProbeResult.Failed($"Database security probe failed: {ex.GetType().Name}");
        }
    }
}

sealed record SecurityProbeResult(
    bool Ready,
    bool DatabaseConnected,
    string? TlsVersion,
    string? Cipher,
    bool ClientCertificatePresented,
    bool PqcKeyExchangeVerified,
    string Message)
{
    public static SecurityProbeResult Failed(string message) => new(
        Ready: false,
        DatabaseConnected: false,
        TlsVersion: null,
        Cipher: null,
        ClientCertificatePresented: false,
        PqcKeyExchangeVerified: false,
        Message: message);
}
