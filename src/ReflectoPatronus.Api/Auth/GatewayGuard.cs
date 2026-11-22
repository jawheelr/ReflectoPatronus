using System.Security.Cryptography;
using System.Text;

namespace ReflectoPatronus.Api.Auth;

/// <summary>
/// Remote access goes through the mTLS gateway (deploy/gateway): it terminates TLS 1.3 (post-quantum hybrid key exchange),
/// REQUIRES a device client certificate, and forwards the request with two headers. With Gateway:Mode=require the API
/// serves only requests that carry the shared secret (so nothing can bypass the gateway on the internal network) and that
/// name the verified client certificate; sessions are then bound to that certificate's fingerprint.
/// Gateway:Mode=off (default) ignores the headers entirely, so a client cannot fake a certificate on a direct connection.
/// </summary>
public static class GatewayGuard
{
    public const string KeyHeader = "X-RP-Gateway-Key";
    public const string FingerprintHeader = "X-RP-Client-Cert-Fp";
    const string ItemKey = "rp.cert_fp";

    /// <summary>SHA-256 fingerprint of the client certificate the gateway verified; null when no gateway is in use.</summary>
    public static string? Fingerprint(HttpContext ctx) => ctx.Items[ItemKey] as string;

    public static void Use(WebApplication app)
    {
        var mode = app.Configuration["Gateway:Mode"] ?? "off";
        if (mode.Equals("off", StringComparison.OrdinalIgnoreCase)) return;
        if (!mode.Equals("require", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Gateway:Mode must be 'off' or 'require'.");
        var secret = app.Configuration["Gateway:SharedSecret"];
        if (string.IsNullOrEmpty(secret) || secret.Length < 24)
            throw new InvalidOperationException("Gateway:SharedSecret (24+ characters) is required when Gateway:Mode=require.");
        var expected = Encoding.UTF8.GetBytes(secret);
        app.Logger.LogInformation("Gateway mode 'require': only requests from the mTLS gateway are served.");

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.Equals("/health/live"))   // container/LB liveness only; reveals nothing
            { await next(); return; }
            var given = Encoding.UTF8.GetBytes(ctx.Request.Headers[KeyHeader].ToString());
            var fp = ctx.Request.Headers[FingerprintHeader].ToString().Trim();
            if (given.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(given, expected) || fp.Length == 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = "Requests must come through the secure gateway with a device certificate." });
                return;
            }
            ctx.Items[ItemKey] = fp.ToLowerInvariant();
            await next();
        });
    }
}
