using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<DatabaseProbe>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    product = "ReflectoPatronus",
    status = "starting",
    apiVersion = "v1"
}));

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", async (DatabaseProbe db, CancellationToken ct) =>
{
    var result = await db.CheckAsync(ct);
    return result.Ready
        ? Results.Ok(result)
        : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/v1/security/status", async (DatabaseProbe db, CancellationToken ct) =>
{
    var result = await db.CheckAsync(ct);
    return result.Ready
        ? Results.Ok(result)
        : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();

sealed class DatabaseProbe(IConfiguration configuration)
{
    // When true, readiness is denied until PQC key exchange can be verified (currently impossible via pg_stat_ssl).
    private readonly bool _requirePqc = configuration.GetValue<bool>("Security:RequirePqcVerification");

    private readonly string _connectionString =
        configuration["Database:ConnectionString"]
        ?? throw new InvalidOperationException("Database:ConnectionString is required.");

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
