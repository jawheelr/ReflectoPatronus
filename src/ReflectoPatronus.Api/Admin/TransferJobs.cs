using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using ReflectoPatronus.Api.Auth;

namespace ReflectoPatronus.Api.Admin;

/// <summary>
/// Moving a very large database (10 GB+, mostly photos) without a browser holding it: exports and imports run on the SERVER as
/// background jobs that read and write files in one transfer directory (config Transfer:Directory), with progress to poll.
/// The browser only ever (a) starts a job, (b) downloads a finished file with its own download manager (HTTP Range, so an
/// interrupted download resumes), or (c) streams an upload from disk. Staff can equally copy .rpb files in and out of the
/// directory with scp, SMB or `docker cp`, which is the most robust route for the very largest files.
/// </summary>
public static class TransferJobs
{
    sealed class Job
    {
        public string Id = Guid.NewGuid().ToString("N")[..12]; public string Kind = ""; public string State = "running"; public string? Message; public string? File;
        public long Total; public Func<long> Progress = () => 0; public JsonElement? Result; public DateTime Started = DateTime.UtcNow; public DateTime? Finished;
    }
    sealed record Ticket(string? File, bool Attachments, DateTime Expires, bool SingleUse);

    static readonly ConcurrentDictionary<string, Job> Jobs = new();
    static readonly ConcurrentDictionary<string, Ticket> Tickets = new();
    static readonly SemaphoreSlim ExportLock = new(1, 1);

    public record StartExport(bool Attachments);
    public record StartImport(string? File, string? Mode, bool Overwrite, bool DryRun);
    public record TicketRequest(string? File, bool Attachments);

    static string Dir(IConfiguration cfg)
    {
        var d = cfg["Transfer:Directory"] ?? "/var/lib/reflectopatronus/transfer";
        Directory.CreateDirectory(d); return d;
    }
    static string? Safe(string dir, string? name)
        => name is not null && name == Path.GetFileName(name) && name.EndsWith(".rpb", StringComparison.OrdinalIgnoreCase) && !name.StartsWith('.') && name.Length < 120 ? Path.Combine(dir, name) : null;

    public static void Map(WebApplication app, RouteGroupBuilder api)
    {
        var g = api.MapGroup("/admin/transfer").RequireAdmin();
        g.MapGet("/files", (IConfiguration cfg) =>
        {
            var d = Dir(cfg);
            long free = 0; try { free = new DriveInfo(Path.GetFullPath(d)).AvailableFreeSpace; } catch { /* unknown */ }
            return Results.Ok(new
            {
                directory = d, freeBytes = free,
                files = new DirectoryInfo(d).GetFiles("*.rpb").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => new { name = f.Name, size = f.Length, modified = f.LastWriteTimeUtc }),
                partial = new DirectoryInfo(d).GetFiles("*.partial").Select(f => new { name = f.Name, size = f.Length }),
            });
        });
        g.MapDelete("/files/{name}", (string name, IConfiguration cfg) => Safe(Dir(cfg), name) is { } p && File.Exists(p) ? DeleteFile(p) : Results.NotFound());
        g.MapPut("/files/{name}", Upload);
        g.MapPost("/export", (StartExport r, IConfiguration cfg, NpgsqlDataSource db) => StartExportJob(r, Dir(cfg), db));
        g.MapPost("/import", (StartImport r, IConfiguration cfg, NpgsqlDataSource db) => StartImportJob(app, r, Dir(cfg), db));
        g.MapGet("/jobs/{id}", (string id) => Jobs.TryGetValue(id, out var j) ? Results.Ok(View(j)) : Results.NotFound());
        g.MapPost("/ticket", (TicketRequest r, IConfiguration cfg) =>
        {
            if (r.File is not null && (Safe(Dir(cfg), r.File) is not { } p || !File.Exists(p))) return Results.NotFound();
            foreach (var kv in Tickets.Where(t => t.Value.Expires < DateTime.UtcNow)) Tickets.TryRemove(kv.Key, out _);
            var t = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            Tickets[t] = new Ticket(r.File, r.Attachments, DateTime.UtcNow.AddMinutes(r.File is null ? 2 : 60), r.File is null);
            return Results.Ok(new { url = $"/api/v1/admin/transfer/download/{t}" });
        });
        // The browser's own download manager cannot send the bearer token, so a short-lived unguessable ticket stands in for it.
        app.MapGet("/api/v1/admin/transfer/download/{ticket}", (string ticket, IConfiguration cfg, NpgsqlDataSource db) =>
        {
            if (!Tickets.TryGetValue(ticket, out var t) || t.Expires < DateTime.UtcNow) return Results.NotFound();
            if (t.SingleUse) Tickets.TryRemove(ticket, out _);
            if (t.File is not null) return Results.File(Safe(Dir(cfg), t.File)!, "application/gzip", t.File, enableRangeProcessing: true);
            return Results.Stream(body => DatabaseTransfer.WriteExport(body, db, t.Attachments), "application/gzip", $"reflectopatronus-{DateTime.UtcNow:yyyyMMdd-HHmm}.rpb");
        }).AllowAnonymous();
    }

    static IResult DeleteFile(string p) { File.Delete(p); return Results.NoContent(); }

    static object View(Job j) => new { id = j.Id, kind = j.Kind, state = j.State, message = j.Message, file = j.File, bytes = j.Progress(), total = j.Total, result = j.Result, started = j.Started, finished = j.Finished };

    static async Task<IResult> Upload(string name, HttpContext http, IConfiguration cfg)
    {
        var dir = Dir(cfg);
        if (Safe(dir, name) is not { } path) return Results.BadRequest(new { error = "The file name must end in .rpb." });
        if (File.Exists(path)) return Results.Json(new { error = "A file with that name is already on the server. Delete it first or rename yours." }, statusCode: 409);
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
        var tmp = path + ".partial";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                await http.Request.Body.CopyToAsync(fs, 1 << 20, http.RequestAborted);
            File.Move(tmp, path);
            return Results.Ok(new { name, size = new FileInfo(path).Length });
        }
        catch { try { File.Delete(tmp); } catch { /* best effort */ } throw; }
    }

    static IResult StartExportJob(StartExport r, string dir, NpgsqlDataSource db)
    {
        if (!ExportLock.Wait(0)) return Results.Json(new { error = "An export is already running." }, statusCode: 409);
        long free = 0; try { free = new DriveInfo(Path.GetFullPath(dir)).AvailableFreeSpace; } catch { /* unknown */ }
        if (free is > 0 and < 1L << 30) { ExportLock.Release(); return Results.Json(new { error = "Less than 1 GB is free in the transfer directory." }, statusCode: 409); }
        var name = $"reflectopatronus-{DateTime.UtcNow:yyyyMMdd-HHmmss}{(r.Attachments ? "" : "-nofiles")}.rpb";
        var path = Path.Combine(dir, name); var tmp = path + ".partial";
        var job = new Job { Kind = "export", File = name }; Jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            try
            {
                await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    job.Progress = () => fs.Length;
                    await DatabaseTransfer.WriteExport(fs, db, r.Attachments);
                }
                File.Move(tmp, path); job.State = "done"; job.Message = $"Saved {name} ({new FileInfo(path).Length / 1048576.0:0.#} MB)";
            }
            catch (Exception e) { job.State = "error"; job.Message = e.Message; try { File.Delete(tmp); } catch { /* best effort */ } }
            finally { job.Finished = DateTime.UtcNow; ExportLock.Release(); }
        });
        return Results.Accepted($"/api/v1/admin/transfer/jobs/{job.Id}", new { id = job.Id });
    }

    static IResult StartImportJob(WebApplication app, StartImport r, string dir, NpgsqlDataSource db)
    {
        if (Safe(dir, r.File) is not { } path || !File.Exists(path)) return Results.NotFound(new { error = "That file is not in the transfer directory." });
        if (Jobs.Values.Any(j => j.Kind == "import" && j.State == "running")) return Results.Json(new { error = "An import is already running." }, statusCode: 409);
        var job = new Job { Kind = "import", File = r.File }; Jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
                job.Total = fs.Length; job.Progress = () => { try { return fs.Position; } catch { return job.Total; } };
                var result = await DatabaseTransfer.ImportStream(fs, db, r.Mode ?? "merge", r.Overwrite, r.DryRun, CancellationToken.None);
                var ctx = new DefaultHttpContext { RequestServices = app.Services }; ctx.Response.Body = new MemoryStream();
                await result.ExecuteAsync(ctx);
                ctx.Response.Body.Position = 0;
                var json = await JsonDocument.ParseAsync(ctx.Response.Body);
                if (ctx.Response.StatusCode is >= 200 and < 300) { job.Result = json.RootElement.Clone(); job.State = "done"; job.Message = r.DryRun ? "Checked: nothing was changed." : "Imported."; }
                else { job.State = "error"; job.Message = json.RootElement.TryGetProperty("error", out var m) ? m.GetString() : "The import failed."; }
            }
            catch (Exception e) { job.State = "error"; job.Message = e.Message; }
            finally { job.Finished = DateTime.UtcNow; }
        });
        return Results.Accepted($"/api/v1/admin/transfer/jobs/{job.Id}", new { id = job.Id });
    }
}
