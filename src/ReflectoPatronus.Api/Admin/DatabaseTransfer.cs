using System.Data;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Npgsql;
using ReflectoPatronus.Api.Auth;

namespace ReflectoPatronus.Api.Admin;

/// <summary>
/// Whole-database export and import (administrators only).
/// File format (.rpb): gzip of UTF-8 lines. Line 1 "#rp-export\t{header json}"; then "table\t{row json}" per row in
/// foreign-key order; last line "#end\t{row counts}" (an upload without it is rejected as truncated).
/// Never exported: sessions (device_session), API clients and their tokens, schema_migrations, password hashes, admin flags, feed credentials.
/// Users are never overwritten or wiped by an import; missing users are added without a password.
/// </summary>
public static class DatabaseTransfer
{
    const int FormatVersion = 1;
    static readonly HashSet<string> NotTransferred = ["schema_migrations", "device_session", "api_client", "api_token", "app_group", "group_permission", "user_group", "system_state"];   // access rules belong to the server, not to the data;
    static readonly HashSet<string> KeepAlways = ["app_user", "user_permission"];
    static readonly Dictionary<string, string[]> Strip = new()
    {
        ["app_user"] = ["password_hash", "is_admin", "failed_logins", "locked_until"],
        ["supplier"] = ["feed_auth_header"],
    };
    static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public record TableReport(string Table, int Inserted, int Updated, int Skipped, int Deleted);

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/admin").RequirePermission("transfer.manage");
        g.MapGet("/export", Export);
        g.MapPost("/import", Import);
    }

    sealed record Meta(string Name, string[] Columns, string[] Keys, bool OverrideIdentity, string? IdentityColumn);

    static async Task<(List<Meta> Ordered, string Schema)> LoadMeta(NpgsqlConnection c, CancellationToken ct)
    {
        var tables = new List<string>();
        await using (var q = new NpgsqlCommand("SELECT relname FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind = 'r' ORDER BY relname", c))
        await using (var r = await q.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) if (!NotTransferred.Contains(r.GetString(0))) tables.Add(r.GetString(0));

        var deps = tables.ToDictionary(t => t, _ => new HashSet<string>());
        await using (var q = new NpgsqlCommand("""
            SELECT a.relname, b.relname FROM pg_constraint k JOIN pg_class a ON a.oid = k.conrelid JOIN pg_class b ON b.oid = k.confrelid
            WHERE k.contype = 'f' AND k.connamespace = 'public'::regnamespace AND a.oid <> b.oid
            """, c))
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) { if (deps.TryGetValue(r.GetString(0), out var d) && deps.ContainsKey(r.GetString(1))) d.Add(r.GetString(1)); }

        var order = new List<string>(); var done = new HashSet<string>();
        while (order.Count < tables.Count)
        {
            var next = tables.Where(t => !done.Contains(t) && deps[t].All(done.Contains)).ToList();
            if (next.Count == 0) throw new InvalidOperationException("Circular foreign keys between tables: " + string.Join(", ", tables.Where(t => !done.Contains(t))));
            foreach (var t in next) { order.Add(t); done.Add(t); }
        }

        var metas = new List<Meta>();
        foreach (var t in order)
        {
            var strip = Strip.GetValueOrDefault(t) ?? [];
            var cols = new List<string>(); string? ident = null; var overrideId = false;
            await using (var q = new NpgsqlCommand("""
                SELECT attname, attidentity FROM pg_attribute WHERE attrelid = ('public.' || quote_ident($1))::regclass AND attnum > 0 AND NOT attisdropped AND attgenerated = '' ORDER BY attnum
                """, c))
            {
                q.Parameters.AddWithValue(t);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var n = r.GetString(0); if (strip.Contains(n)) continue;
                    cols.Add(n);
                    if (r.GetChar(1) == 'a') { overrideId = true; ident = n; } else if (r.GetChar(1) == 'd') ident = n;
                }
            }
            var keys = new List<string>();
            await using (var q = new NpgsqlCommand("""
                SELECT a.attname FROM pg_index i JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey)
                WHERE i.indrelid = ('public.' || quote_ident($1))::regclass AND i.indisprimary ORDER BY array_position(i.indkey::int2[], a.attnum)
                """, c))
            {
                q.Parameters.AddWithValue(t);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) keys.Add(r.GetString(0));
            }
            if (keys.Count == 0) throw new InvalidOperationException($"Table {t} has no primary key; it cannot be transferred.");
            metas.Add(new Meta(t, cols.ToArray(), keys.ToArray(), overrideId, ident));
        }
        string schema;
        // Structure fingerprint of the transferable tables (the restricted app role cannot read schema_migrations).
        await using (var q = new NpgsqlCommand("""
            SELECT substr(md5(coalesce(string_agg(table_name || '.' || column_name || ':' || data_type, ',' ORDER BY table_name, column_name), '')), 1, 12)
            FROM information_schema.columns WHERE table_schema = 'public' AND table_name <> ALL ($1)
              AND table_name IN (SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE')
            """, c))
        { q.Parameters.AddWithValue(NotTransferred.ToArray()); schema = (string)(await q.ExecuteScalarAsync(ct))!; }
        return (metas, schema);
    }

    static string Q(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

    // ---- export ---------------------------------------------------------------------------------------------------

    static readonly HashSet<string> AttachmentTables = ["attachment", "attachment_blob"];

    /// <param name="attachments">false leaves out photos/files (the bulk of the database) for a small, fast export.</param>
    internal static async Task WriteExport(Stream body, NpgsqlDataSource db, bool attachments)
    {
            await using var conn = await db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead);   // one consistent snapshot
            var (all, schema) = await LoadMeta(conn, default);
            var metas = all.Where(m => attachments || !AttachmentTables.Contains(m.Name)).ToList();
            var gz = new GZipStream(body, CompressionLevel.Fastest, leaveOpen: true);
            var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" };
            await w.WriteLineAsync("#rp-export\t" + JsonSerializer.Serialize(new { format = FormatVersion, schema, exportedAt = DateTime.UtcNow, tables = metas.Select(m => m.Name) }));
            var counts = new Dictionary<string, int>();
            foreach (var m in metas)
            {
                var strip = Strip.GetValueOrDefault(m.Name) ?? [];
                await using var cmd = new NpgsqlCommand($"SELECT (to_jsonb(t) - $1::text[])::text FROM {Q(m.Name)} t", conn, tx);
                cmd.Parameters.AddWithValue(strip);
                await using var r = await cmd.ExecuteReaderAsync();
                var n = 0;
                while (await r.ReadAsync()) { await w.WriteLineAsync(m.Name + "\t" + r.GetString(0)); n++; }
                counts[m.Name] = n;
            }
            await w.WriteLineAsync("#end\t" + JsonSerializer.Serialize(counts));
            await w.FlushAsync();
            await gz.DisposeAsync();   // writes the gzip trailer asynchronously (Kestrel forbids synchronous writes)
    }

    static IResult Export(NpgsqlDataSource db, bool attachments = true) =>
        Results.Stream(body => WriteExport(body, db, attachments), "application/gzip", $"reflectopatronus-{DateTime.UtcNow:yyyyMMdd-HHmm}.rpb");

    // ---- import ---------------------------------------------------------------------------------------------------

    static async Task<IResult> Import(HttpContext http, NpgsqlDataSource db, string mode = "merge", bool overwrite = false, bool dryRun = false)
    {
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
        return await ImportStream(http.Request.Body, db, mode, overwrite, dryRun, http.RequestAborted);
    }

    /// <summary>Imports from any stream: the request body, or a file on the server (used for very large databases).</summary>
    internal static async Task<IResult> ImportStream(Stream input, NpgsqlDataSource db, string mode, bool overwrite, bool dryRun, CancellationToken ct)
    {
        if (mode is not ("replace" or "merge")) return Results.BadRequest(new { error = "mode must be replace or merge." });
        if (!await OneAtATime.WaitAsync(0)) return Results.Conflict(new { error = "Another import is already running." });
        try
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var (metas, schema) = await LoadMeta(conn, ct);
            var byName = metas.ToDictionary(m => m.Name);

            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var rd = new StreamReader(gz, Encoding.UTF8);
            string? line;
            try { line = await rd.ReadLineAsync(ct); } catch (InvalidDataException) { return Bad("That is not a ReflectoPatronus export file."); }
            if (line is null || !line.StartsWith("#rp-export\t")) return Bad("That is not a ReflectoPatronus export file.");
            HashSet<string> fileTables;
            using (var h = JsonDocument.Parse(line["#rp-export\t".Length..]))
            {
                fileTables = h.RootElement.GetProperty("tables").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
                if (h.RootElement.GetProperty("format").GetInt32() != FormatVersion) return Bad("Unsupported export format version.");
                var fileSchema = h.RootElement.GetProperty("schema").GetString();
                if (fileSchema != schema) return Results.Json(new { error = $"This file was exported from a database with a different structure ({fileSchema}; this server is {schema}). Update both servers to the same release and apply migrations first." }, statusCode: 409);
            }

            var report = metas.ToDictionary(m => m.Name, m => new int[4]);   // inserted, updated, skipped, deleted
            if (mode == "replace" && !fileTables.Contains("attachment"))
            {
                await using var any = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM attachment)", conn, tx);
                if ((bool)(await any.ExecuteScalarAsync(ct))!)
                    return Bad("This file was exported without attachments, but this database has attachments, so Replace would have to delete them. Export again with attachments included, or use Merge.");
            }
            if (mode == "replace")
                foreach (var m in Enumerable.Reverse(metas).Where(m => !KeepAlways.Contains(m.Name) && fileTables.Contains(m.Name)))   // only tables the file actually carries
                {
                    await using var del = new NpgsqlCommand($"DELETE FROM {Q(m.Name)}", conn, tx) { CommandTimeout = 0 };
                    report[m.Name][3] = await del.ExecuteNonQueryAsync(ct);
                }

            string? cur = null; var batch = new List<string>(); long bytes = 0; var ended = false;
            async Task Flush()
            {
                if (cur is null || batch.Count == 0) return;
                var m = byName[cur];
                var counts = await InsertBatch(conn, tx, m, batch, mode, overwrite, ct);
                for (var i = 0; i < 3; i++) report[cur][i] += counts[i];
                batch.Clear(); bytes = 0;
            }
            while ((line = await rd.ReadLineAsync(ct)) is not null)
            {
                if (line.StartsWith("#end\t")) { await Flush(); ended = true; break; }
                var tab = line.IndexOf('\t');
                if (tab < 1) return Bad("The file is damaged (unexpected line).");
                var name = line[..tab];
                if (!byName.ContainsKey(name)) return Bad($"The file contains an unknown table '{name}'.");
                if (name != cur || batch.Count >= 500 || bytes > 8_000_000) { await Flush(); cur = name; }
                batch.Add(line[(tab + 1)..]); bytes += line.Length;
            }
            if (!ended) return Bad("The file is incomplete (upload was cut off). Nothing was changed.");

            if (!dryRun)
            {
                foreach (var m in metas.Where(m => m.IdentityColumn is not null))
                {
                    await using var sq = new NpgsqlCommand($"SELECT setval(pg_get_serial_sequence('{Q(m.Name).Replace("'", "''")}', '{m.IdentityColumn!.Replace("'", "''")}'), greatest((SELECT max({Q(m.IdentityColumn!)}) FROM {Q(m.Name)}), 1))", conn, tx) { CommandTimeout = 0 };
                    await sq.ExecuteScalarAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            else await tx.RollbackAsync(ct);

            var tables = metas.Select(m => new TableReport(m.Name, report[m.Name][0], report[m.Name][1], report[m.Name][2], report[m.Name][3])).Where(t => t.Inserted + t.Updated + t.Skipped + t.Deleted > 0).ToList();
            return Results.Ok(new { mode, overwrite, dryRun, applied = !dryRun, inserted = tables.Sum(t => t.Inserted), updated = tables.Sum(t => t.Updated), skipped = tables.Sum(t => t.Skipped), deleted = tables.Sum(t => t.Deleted), tables });
        }
        catch (PostgresException e) { return Results.BadRequest(new { error = $"Import stopped and was rolled back: {e.MessageText}" + (e.TableName is null ? "" : $" (table {e.TableName}{(e.ConstraintName is null ? "" : ", " + e.ConstraintName)})") }); }
        catch (JsonException) { return Bad("The file is damaged (invalid data)."); }
        catch (InvalidDataException) { return Bad("The file is damaged or not compressed correctly."); }
        finally { OneAtATime.Release(); }
    }

    static IResult Bad(string m) => Results.BadRequest(new { error = m });

    static async Task<int[]> InsertBatch(NpgsqlConnection c, NpgsqlTransaction tx, Meta m, List<string> rows, string mode, bool overwrite, CancellationToken ct)
    {
        var cols = string.Join(", ", m.Columns.Select(Q));
        var keep = KeepAlways.Contains(m.Name);
        if (m.Name == "app_user")
        {   // user ids are referenced everywhere: refuse a file whose ids mean different people here
            await using var chk = new NpgsqlCommand("SELECT count(*) FROM jsonb_populate_recordset(null::app_user, $1::jsonb) j JOIN app_user u ON u.id = j.id WHERE u.code <> j.code", c, tx);
            chk.Parameters.AddWithValue("[" + string.Join(",", rows) + "]");
            if ((long)(await chk.ExecuteScalarAsync(ct))! > 0) throw new PostgresException("The file's user list does not match this server's users (same id, different user code).", "ERROR", "ERROR", "P0001");
        }
        var nonKey = m.Columns.Where(x => !m.Keys.Contains(x)).ToArray();
        string conflict, returning;
        if (mode == "replace" && !keep) { conflict = ""; returning = ""; }
        else if (overwrite && !keep && nonKey.Length > 0)
        {
            conflict = $" ON CONFLICT ({string.Join(", ", m.Keys.Select(Q))}) DO UPDATE SET {string.Join(", ", nonKey.Select(x => $"{Q(x)} = EXCLUDED.{Q(x)}"))}";
            returning = " RETURNING (xmax = 0)";
        }
        else { conflict = " ON CONFLICT DO NOTHING"; returning = " RETURNING true"; }
        var sql = $"INSERT INTO {Q(m.Name)} ({cols}){(m.OverrideIdentity ? " OVERRIDING SYSTEM VALUE" : "")} SELECT {cols} FROM jsonb_populate_recordset(null::{Q(m.Name)}, $1::jsonb){conflict}{returning}";
        await using var cmd = new NpgsqlCommand(sql, c, tx) { CommandTimeout = 0 };
        cmd.Parameters.AddWithValue("[" + string.Join(",", rows) + "]");
        int ins = 0, upd = 0;
        if (returning == "") { ins = await cmd.ExecuteNonQueryAsync(ct); return [ins, 0, rows.Count - ins]; }
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { if (r.GetBoolean(0)) ins++; else upd++; }
        return [ins, upd, rows.Count - ins - upd];
    }
}
