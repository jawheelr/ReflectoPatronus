using System.Text.Json;
using Npgsql;
using ReflectoPatronus.Api.Auth;

namespace ReflectoPatronus.Api.Updates;

public record UpdateStatus(string Mode, string? Current, string? CurrentShort, string? Latest, string? LatestShort, string? LatestMessage, DateTime? LatestDate, string? LatestUrl,
    bool Available, bool CanCompare, int? CommitsBehind, DateTime? CheckedAt, string? Error, bool Dismissed, bool Enabled, string Repository, string Branch, string[] Commands);

/// <summary>
/// Looks for new commits on the GitHub branch this server tracks (default: main of jawheelr/ReflectoPatronus) once a week and tells
/// administrators in the console. It only ASKS GitHub "what is the newest commit?": no customer, company or user data is sent, and the
/// request carries a generic client name. Applying an update is always a deliberate step by a person (scripts/update.sh for Docker,
/// rp-update inside an LXC), never automatic. Set Updates__Enabled=false for a server that must not call out.
/// </summary>
public sealed class UpdateService(NpgsqlDataSource db, IConfiguration cfg, IHttpClientFactory http, ILogger<UpdateService> log) : BackgroundService
{
    static readonly TimeSpan Wake = TimeSpan.FromHours(6);
    string Repo => cfg["Updates:Repository"] ?? "jawheelr/ReflectoPatronus";
    string Branch => cfg["Updates:Branch"] ?? "main";
    bool Enabled => !string.Equals(cfg["Updates:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
    TimeSpan Every => TimeSpan.FromDays(Math.Clamp(double.TryParse(cfg["Updates:IntervalDays"], out var d) ? d : 7, 1, 90));

    /// <summary>The commit this build was made from: baked into the Docker image, or written into an LXC template/update bundle.</summary>
    public string? CurrentSha()
    {
        var env = cfg["RP_BUILD_SHA"] ?? Environment.GetEnvironmentVariable("RP_BUILD_SHA");
        if (!string.IsNullOrWhiteSpace(env) && env != "unknown") return env.Trim();
        try { var f = "/opt/reflectopatronus/COMMIT"; if (File.Exists(f)) { var t = File.ReadAllText(f).Trim(); if (t.Length >= 7 && t != "unknown") return t; } } catch { /* unreadable */ }
        return null;
    }
    public string Mode() => File.Exists("/.dockerenv") || cfg["RP_DEPLOY"] == "docker" ? "docker" : Directory.Exists("/opt/reflectopatronus") ? "lxc" : "other";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Enabled)
                {
                    var last = (await Read("last"))?.GetProperty("checkedAt").GetDateTime();
                    if (last is null || DateTime.UtcNow - last.Value >= Every) await CheckNow(ct);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Update check skipped: {Message}", e.Message); }
            try { await Task.Delay(Wake, ct); } catch (OperationCanceledException) { return; }
        }
    }

    async Task<JsonElement?> Read(string key)
    {
        await using var c = db.CreateCommand("SELECT value::text FROM system_state WHERE key = $1");
        c.Parameters.AddWithValue("update." + key);
        return await c.ExecuteScalarAsync() is string s ? JsonDocument.Parse(s).RootElement.Clone() : null;
    }
    async Task Write(string key, object value)
    {
        await using var c = db.CreateCommand("INSERT INTO system_state (key, value) VALUES ($1, $2::jsonb) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()");
        c.Parameters.AddWithValue("update." + key); c.Parameters.AddWithValue(JsonSerializer.Serialize(value));
        await c.ExecuteNonQueryAsync();
    }

    /// <summary>Asks GitHub for the newest commit on the branch and how far behind this build is. Stores the answer.</summary>
    public async Task CheckNow(CancellationToken ct)
    {
        var client = http.CreateClient("updates");
        string? latest = null, message = null, url = null; DateTime? date = null; int? behind = null; string? error = null;
        try
        {
            using var r = await client.GetAsync($"repos/{Repo}/commits/{Uri.EscapeDataString(Branch)}", ct);
            if (!r.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)r.StatusCode}");
            using var doc = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement; latest = root.GetProperty("sha").GetString();
            var commit = root.GetProperty("commit");
            message = commit.GetProperty("message").GetString()?.Split('\n')[0];
            date = commit.GetProperty("committer").GetProperty("date").GetDateTime().ToUniversalTime(); url = root.GetProperty("html_url").GetString();
            if (CurrentSha() is { } cur && !latest!.StartsWith(cur, StringComparison.OrdinalIgnoreCase))
            {
                using var cmp = await client.GetAsync($"repos/{Repo}/compare/{cur}...{latest}", ct);   // how many commits behind (404 if this build is not on GitHub)
                if (cmp.IsSuccessStatusCode)
                {
                    using var cd = await JsonDocument.ParseAsync(await cmp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    behind = cd.RootElement.GetProperty("status").GetString() is "identical" or "behind" ? 0 : cd.RootElement.GetProperty("ahead_by").GetInt32();
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { error = "Could not reach GitHub: " + e.Message; }
        await Write("last", new { checkedAt = DateTime.UtcNow, latest, message, date, url, behind, error });
        log.LogInformation("Update check: {Latest} (behind {Behind}) {Error}", latest, behind, error);
    }

    public async Task<UpdateStatus> Status()
    {
        var last = await Read("last"); var dismissed = (await Read("dismissed"))?.GetProperty("sha").GetString();
        string? S(string n) => last is { } l && l.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        DateTime? D(string n) => last is { } l && l.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTime() : null;
        var cur = CurrentSha(); var latest = S("latest");
        int? behind = last is { } l2 && l2.TryGetProperty("behind", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : null;
        var canCompare = cur is not null && latest is not null;
        var available = canCompare && !latest!.StartsWith(cur!, StringComparison.OrdinalIgnoreCase) && (behind ?? 1) > 0;
        var mode = Mode();
        string[] commands = mode switch
        {
            "docker" => ["On the machine that runs Docker, in the ReflectoPatronus folder:  ./scripts/update.sh", "Pulled from GitHub images instead:  ./scripts/update.sh --images"],
            "lxc" => ["In this container's console (Proxmox: pct enter <id>):  rp-update", "Containers that hold only the database or only the API: update the DATABASE container first, then the API container."],
            _ => ["Update the code from the repository, then run the migrations and restart the API."],
        };
        return new UpdateStatus(mode, cur, Short(cur), latest, Short(latest), S("message"), D("date"), S("url"), available, canCompare, behind, D("checkedAt"), S("error"),
            dismissed is not null && dismissed == latest, Enabled, Repo, Branch, commands);
    }
    static string? Short(string? s) => s is null ? null : s[..Math.Min(7, s.Length)];
    public Task Dismiss(string sha) => Write("dismissed", new { sha });
}

public static class UpdateEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // Administrators only: it tells people how to change the server. 'users.manage' is held by Administrators only by default.
        var g = api.MapGroup("/updates").RequirePermission("users.manage");
        g.MapGet("", async (UpdateService u) => Results.Ok(await u.Status()));
        g.MapPost("/check", async (UpdateService u, CancellationToken ct) => { await u.CheckNow(ct); return Results.Ok(await u.Status()); });
        g.MapPost("/dismiss", async (UpdateService u) => { var s = await u.Status(); if (s.Latest is not null) await u.Dismiss(s.Latest); return Results.NoContent(); });
    }
}
