using System.Text.Json;
using Npgsql;
using ReflectoPatronus.Api.Auth;

namespace ReflectoPatronus.Api.Updates;

public record UpdateStatus(string Mode, string? Current, string? CurrentShort, string? Latest, string? LatestShort, string? LatestMessage, DateTime? LatestDate, string? LatestUrl,
    bool Available, bool CanCompare, int? CommitsBehind, DateTime? CheckedAt, string? Error, bool Dismissed, bool Enabled, string Repository, string Branch, string[] Commands,
    bool UpdaterPresent, JsonElement? Staged, bool Ready, string? JobState, string? JobVersion);

/// <summary>
/// Looks for new commits on the GitHub branch this server tracks (default: main of jawheelr/ReflectoPatronus) once a week and tells
/// administrators in the console. It only ASKS GitHub "what is the newest commit?": no customer, company or user data is sent, and the
/// request carries a generic client name. When it finds a newer commit it asks the UPDATER (a separate Docker service, or a systemd unit
/// in an LXC) to download it ("staging"). Installing is always a deliberate click by an administrator: the API writes a request file, the
/// updater does the work (backup, build, migrate, restart, health check, rollback) and writes progress that the /update-progress page shows.
/// The API itself never touches Docker or systemd. Set Updates__Enabled=false for a server that must not call out.
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
        // Newer commit found: have the updater download it now, so installing later is one click.
        try { if (error is null && latest is not null && CurrentSha() is { } c0 && !latest.StartsWith(c0, StringComparison.OrdinalIgnoreCase) && (behind ?? 1) > 0 && UpdaterPresent() && !JobActive()) Request("stage", latest); }
        catch (Exception e) { log.LogWarning("Could not ask the updater to download the update: {Message}", e.Message); }
    }

    // ---- the hand-off folder shared with the updater (status.json written by it, request.json written here) ----
    public string Dir => cfg["Updates:Directory"] ?? "/data/update";
    string StatusFile => Path.Combine(Dir, "status.json");
    string RequestFile => Path.Combine(Dir, "request.json");
    /// <summary>Docker: the updater service touches 'heartbeat' every few seconds. LXC: a marker file says the systemd units are installed.</summary>
    public bool UpdaterPresent()
    {
        try
        {
            var hb = Path.Combine(Dir, "heartbeat");
            if (File.Exists(hb) && DateTime.UtcNow - File.GetLastWriteTimeUtc(hb) < TimeSpan.FromSeconds(90)) return true;
            return File.Exists(Path.Combine(Dir, "updater-lxc"));
        }
        catch { return false; }
    }
    public JsonElement? JobFile()
    {
        try { return File.Exists(StatusFile) ? JsonDocument.Parse(File.ReadAllText(StatusFile)).RootElement.Clone() : null; } catch { return null; }
    }
    static string? Str(JsonElement? j, string n) => j is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public bool JobActive()
    {
        var j = JobFile(); var st = Str(j, "state");
        if (st is not ("backup" or "checking" or "downloading" or "building" or "migrating" or "switching" or "verifying" or "staging")) return false;
        return j is { } e && e.TryGetProperty("updated", out var u) && u.ValueKind == JsonValueKind.Number && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - u.GetDouble() < 1800;
    }
    /// <summary>Atomically writes the request the updater picks up: {action: "stage" | "apply", sha, backup}.</summary>
    public void Request(string action, string? sha = null, bool backup = true, string? by = null)
    {
        Directory.CreateDirectory(Dir);
        var tmp = RequestFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { action, sha, backup, by, at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
        File.Move(tmp, RequestFile, true);
    }
    public async Task<(bool ok, string? error)> StartApply(bool backup, string? by)
    {
        var s = await Status();
        if (!s.UpdaterPresent) return (false, "No updater is running on this server, so it cannot update itself. Use the command shown under 'How to update'.");
        if (JobActive()) return (false, "An update is already in progress.");
        if (s.Latest is null || !s.Available) return (false, "This server is already up to date.");
        Request("apply", s.Latest, backup, by);
        return (true, null);
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
        var job = JobFile(); JsonElement? staged = job is { } je && je.TryGetProperty("staged", out var sg) && sg.ValueKind == JsonValueKind.Object ? sg.Clone() : null;
        var ready = available && Str(staged, "sha") is { } ss && latest is not null && (ss.StartsWith(latest, StringComparison.OrdinalIgnoreCase) || latest.StartsWith(ss, StringComparison.OrdinalIgnoreCase));
        return new UpdateStatus(mode, cur, Short(cur), latest, Short(latest), S("message"), D("date"), S("url"), available, canCompare, behind, D("checkedAt"), S("error"),
            dismissed is not null && dismissed == latest, Enabled, Repo, Branch, commands, UpdaterPresent(), staged, ready, Str(job, "state"), Str(job, "version"));
    }
    static string? Short(string? s) => s is null ? null : s[..Math.Min(7, s.Length)];
    public Task Dismiss(string sha) => Write("dismissed", new { sha });
}

public record ApplyBody(bool Backup = true);

public static class UpdateEndpoints
{
    /// <summary>Public (no sign-in) on purpose: the page must keep working while the API restarts and sessions are in flux. Shows no secrets.</summary>
    public static void MapPublic(WebApplication app)
    {
        app.MapGet("/update-status", (UpdateService u) =>
        {
            var j = u.JobFile();
            var o = new Dictionary<string, object?>();
            if (j is { } e && e.ValueKind == JsonValueKind.Object)
                foreach (var k in new[] { "state", "step", "version", "from", "percent", "message", "started", "updated", "log", "steps", "backupFile", "rolledBackTo" })
                    if (e.TryGetProperty(k, out var v)) o[k] = v.Clone();
            o["runningVersion"] = u.CurrentSha(); o["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return Results.Json(o, new JsonSerializerOptions { PropertyNamingPolicy = null }, statusCode: 200);
        }).AllowAnonymous();
        app.MapGet("/update-progress", (HttpContext ctx) =>
        {
            // The page is one self-contained file with inline style and script, so it gets its own policy (the global one allows only same-origin files).
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Content(UpdatePage.Html, "text/html; charset=utf-8");
        }).AllowAnonymous();
    }

    public static void Map(RouteGroupBuilder api)
    {
        // Administrators only: it tells people how to change the server. 'users.manage' is held by Administrators only by default.
        var g = api.MapGroup("/updates").RequirePermission("users.manage");
        g.MapGet("", async (UpdateService u) => Results.Ok(await u.Status()));
        g.MapPost("/check", async (UpdateService u, CancellationToken ct) => { await u.CheckNow(ct); return Results.Ok(await u.Status()); });
        g.MapPost("/apply", async (UpdateService u, HttpContext ctx, ApplyBody? b) =>
        {
            var by = ctx.User.Identity?.Name;
            var (ok, error) = await u.StartApply(b?.Backup ?? true, by);
            return ok ? Results.Accepted("/update-progress", new { progressUrl = $"/update-progress?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}" }) : Results.Conflict(new { error });
        });
        g.MapPost("/dismiss", async (UpdateService u) => { var s = await u.Status(); if (s.Latest is not null) await u.Dismiss(s.Latest); return Results.NoContent(); });
    }
}
