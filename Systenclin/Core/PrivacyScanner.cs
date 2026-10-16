using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Systenclin.Core;

/// <summary>
/// Lê e apaga dados de privacidade dos navegadores.
///
/// Regras que valem aqui:
///
/// 1. LEITURA é sempre somente leitura e funciona com o navegador aberto,
///    via cópia com compartilhamento de arquivo.
///
/// 2. ESCRITA (apagar) exige o navegador FECHADO. Não existe forma segura de
///    escrever no banco de um navegador rodando — o navegador mantém o estado
///    em memória e sobrescreve, ou corrompe o banco. Produtos que prometem
///    "limpar com o navegador aberto" ou mentem, ou corrompem dados.
///
/// 3. Antes de apagar, o banco é COPIADO para backup. É a única forma honesta
///    de oferecer undo aqui — o v0.1 não conseguia, este consegue.
///
/// 4. Nada de apagar cookie individual por valor: ou apaga a categoria inteira,
///    ou não apaga. Meia-medida deixa o usuário achando que limpou e não limpou.
/// </summary>
public sealed class PrivacyScanner
{
    public sealed record Progress(string Current, int Done, int Total, int Found);

    /// <summary>Localiza os perfis e mede o que cada um tem.</summary>
    public async Task<IReadOnlyList<PrivacyItem>> ScanAsync(
        IProgress<Progress>? progress = null,
        CancellationToken ct = default)
    {
        // A descoberta percorre o disco: não pode rodar na thread de UI.
        var profiles = await Task.Run(() => BrowserLocator.Discover(), ct).ConfigureAwait(false);
        var results = new List<PrivacyItem>();

        int done = 0;

        foreach (var profile in profiles)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new Progress(profile.Display, done, profiles.Count, results.Count));

            try
            {
                // ToList() dentro do Task.Run executa a varredura de fato: sem ele,
                // o Task.Run devolveria apenas o iterador, ainda não executado.
                results.AddRange(await Task.Run(() => ScanProfile(profile, ct).ToList(), ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Um perfil problemático não derruba a varredura dos outros.
                progress?.Report(new Progress(
                    $"{profile.Display}: falha ao ler ({ex.Message})", done, profiles.Count, results.Count));
            }

            done++;
        }

        progress?.Report(new Progress("Concluído", profiles.Count, profiles.Count, results.Count));

        return results
            .OrderBy(r => r.Profile.Browser)
            .ThenBy(r => r.Profile.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Kind)
            .ToList();
    }

    private static IEnumerable<PrivacyItem> ScanProfile(BrowserProfile profile, CancellationToken ct)
    {
        // ── Histórico de navegação e downloads ──────────────────────────────
        if (profile.Browser.IsChromium())
        {
            // Histórico e downloads saem do mesmo banco: uma única cópia.
            foreach (var item in ScanChromiumHistoryAndDownloads(profile, ct))
            {
                yield return item;
            }
        }
        else
        {
            var historyItem = ScanFirefoxHistory(profile, ct);
            if (historyItem is not null) yield return historyItem;
        }

        // ── Cookies ─────────────────────────────────────────────────────────
        var cookies = ScanCookies(profile, ct);
        if (cookies is not null) yield return cookies;

        // ── Formulários ─────────────────────────────────────────────────────
        var forms = ScanFormHistory(profile, ct);
        if (forms is not null) yield return forms;

        // ── Permissões de site (Firefox) ────────────────────────────────────
        if (profile.Browser == BrowserKind.Firefox)
        {
            var perms = ScanFirefoxPermissions(profile, ct);
            if (perms is not null) yield return perms;
        }

        // ── Cache em disco ──────────────────────────────────────────────────
        foreach (var item in ScanCache(profile, ct))
        {
            yield return item;
        }

        // ── Abas salvas ─────────────────────────────────────────────────────
        var sessions = ScanSessions(profile, ct);
        if (sessions is not null) yield return sessions;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Chromium: histórico e downloads (mesma cópia do banco)
    // ════════════════════════════════════════════════════════════════════════

    private static List<PrivacyItem> ScanChromiumHistoryAndDownloads(BrowserProfile profile, CancellationToken ct)
    {
        var items = new List<PrivacyItem>();

        var path = profile.HistoryPath;
        if (path is null || !File.Exists(path)) return items;

        long size;
        try { size = new FileInfo(path).Length; }
        catch { return items; }

        // A cópia temporária só pode ser apagada DEPOIS que a conexão SQLite
        // fecha: com a conexão aberta o arquivo fica em uso (sem FILE_SHARE_DELETE)
        // e o delete falharia, deixando a cópia do banco vazar em %TEMP%.
        string? temp = null;
        try
        {
            using var db = OpenReadOnly(path, ct, out temp, out var error);
            if (db is null)
            {
                items.Add(Blocked(profile, PrivacyKind.BrowsingHistory, size, error));
                return items;
            }

            var history = ReadChromiumHistory(profile, db, size);
            if (history is not null) items.Add(history);

            var downloads = ReadChromiumDownloads(profile, db);
            if (downloads is not null) items.Add(downloads);
        }
        catch (Exception ex)
        {
            items.Add(Blocked(profile, PrivacyKind.BrowsingHistory, size, ex.Message));
        }
        finally
        {
            SharedFileReader.TryDelete(temp);
        }

        return items;
    }

    private static PrivacyItem? ReadChromiumHistory(BrowserProfile profile, SqliteConnection db, long size)
    {
        try
        {
            int total = Scalar(db, "select count(*) from urls");
            int visits = Scalar(db, "select count(*) from visits");

            if (total == 0)
            {
                return null;
            }

            // Chromium guarda tempo em microssegundos desde 1601-01-01.
            var preview = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    select url, title, visit_count, last_visit_time
                    from urls
                    where hidden = 0
                    order by last_visit_time desc
                    limit 8
                    """;

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var url = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var title = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    int count = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    long time = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);

                    preview.Add($"{FormatChromiumTime(time)}  ×{count}  {Truncate(title, 60)}");
                    preview.Add($"    {Truncate(url, 110)}");
                }
            }

            return new PrivacyItem
            {
                Kind = PrivacyKind.BrowsingHistory,
                Profile = profile,
                RecordCount = total,
                SizeBytes = size,
                Preview = preview,
                PreviewOfTotal = total,
            };
        }
        catch (Exception ex)
        {
            return Blocked(profile, PrivacyKind.BrowsingHistory, size, ex.Message);
        }
    }

    private static PrivacyItem? ReadChromiumDownloads(BrowserProfile profile, SqliteConnection db)
    {
        try
        {
            int total;
            try { total = Scalar(db, "select count(*) from downloads"); }
            catch { return null; }

            if (total == 0) return null;

            var preview = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    select target_path, total_bytes, start_time
                    from downloads
                    order by start_time desc
                    limit 8
                    """;

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var target = reader.IsDBNull(0) ? "?" : reader.GetString(0);
                    long bytes = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    long time = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);

                    preview.Add($"{FormatChromiumTime(time)}  {CleanableItem.FormatBytes(bytes),9}");
                    preview.Add($"    {Truncate(System.IO.Path.GetFileName(target), 100)}");
                }
            }

            preview.Insert(0, "Apagar remove só o registro. Os arquivos baixados continuam no disco.");

            return new PrivacyItem
            {
                Kind = PrivacyKind.DownloadHistory,
                Profile = profile,
                RecordCount = total,
                SizeBytes = 0,
                Preview = preview,
                PreviewOfTotal = total,
            };
        }
        catch
        {
            return null;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Firefox: histórico
    // ════════════════════════════════════════════════════════════════════════

    private static PrivacyItem? ScanFirefoxHistory(BrowserProfile profile, CancellationToken ct)
    {
        var path = profile.HistoryPath;
        if (path is null || !File.Exists(path)) return null;

        long size;
        try { size = new FileInfo(path).Length; }
        catch { return null; }

        string? temp = null;
        try
        {
            using var db = OpenReadOnly(path, ct, out temp, out var error);
            if (db is null) return Blocked(profile, PrivacyKind.BrowsingHistory, size, error);

            int total = Scalar(db, "select count(*) from moz_places where hidden = 0");
            if (total == 0) return null;

            var preview = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                // Firefox guarda tempo em microssegundos desde 1970.
                cmd.CommandText = """
                    select url, title, visit_count, last_visit_date
                    from moz_places
                    where hidden = 0
                    order by last_visit_date desc
                    limit 8
                    """;

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var url = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var title = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    int count = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    long time = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);

                    preview.Add($"{FormatUnixTime(time)}  ×{count}  {Truncate(title, 60)}");
                    preview.Add($"    {Truncate(url, 110)}");
                }
            }

            return new PrivacyItem
            {
                Kind = PrivacyKind.BrowsingHistory,
                Profile = profile,
                RecordCount = total,
                SizeBytes = size,
                Preview = preview,
                PreviewOfTotal = total,
            };
        }
        catch (Exception ex)
        {
            return Blocked(profile, PrivacyKind.BrowsingHistory, size, ex.Message);
        }
        finally
        {
            SharedFileReader.TryDelete(temp);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Cookies
    // ════════════════════════════════════════════════════════════════════════

    private static PrivacyItem? ScanCookies(BrowserProfile profile, CancellationToken ct)
    {
        var path = profile.CookiesPath;
        if (path is null || !File.Exists(path)) return null;

        long size;
        try { size = new FileInfo(path).Length; }
        catch { return null; }

        string? temp = null;
        try
        {
            using var db = OpenReadOnly(path, ct, out temp, out var error);
            if (db is null) return Blocked(profile, PrivacyKind.Cookies, size, error);

            string table = profile.Browser == BrowserKind.Firefox ? "moz_cookies" : "cookies";
            string hostCol = profile.Browser == BrowserKind.Firefox ? "host" : "host_key";

            int total = Scalar(db, $"select count(*) from {table}");
            if (total == 0) return null;

            int hosts = Scalar(db, $"select count(distinct {hostCol}) from {table}");

            // Agrupa por site: mais útil que listar cookie por cookie.
            var preview = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = $"""
                    select {hostCol} as h, count(*) as c
                    from {table}
                    group by h
                    order by c desc
                    limit 10
                    """;

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var host = reader.IsDBNull(0) ? "?" : reader.GetString(0);
                    int count = reader.GetInt32(1);
                    preview.Add($"{Truncate(host, 50),-52} {count,4} cookie(s)");
                }
            }

            preview.Insert(0, $"{hosts} sites com cookie gravado");
            preview.Insert(1, "Apagar desconecta você de todas as contas nesses sites.");

            return new PrivacyItem
            {
                Kind = PrivacyKind.Cookies,
                Profile = profile,
                RecordCount = total,
                SizeBytes = size,
                Preview = preview,
                PreviewOfTotal = hosts,
            };
        }
        catch (Exception ex)
        {
            return Blocked(profile, PrivacyKind.Cookies, size, ex.Message);
        }
        finally
        {
            SharedFileReader.TryDelete(temp);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Formulários
    // ════════════════════════════════════════════════════════════════════════

    private static PrivacyItem? ScanFormHistory(BrowserProfile profile, CancellationToken ct)
    {
        var path = profile.FormHistoryPath;
        if (path is null || !File.Exists(path)) return null;

        long size;
        try { size = new FileInfo(path).Length; }
        catch { return null; }

        string? temp = null;
        try
        {
            using var db = OpenReadOnly(path, ct, out temp, out var error);
            if (db is null) return Blocked(profile, PrivacyKind.FormAutofill, size, error);

            string table = profile.Browser == BrowserKind.Firefox ? "moz_formhistory" : "autofill";
            string nameCol = profile.Browser == BrowserKind.Firefox ? "fieldname" : "name";

            int total;
            try { total = Scalar(db, $"select count(*) from {table}"); }
            catch { return null; }

            if (total == 0) return null;

            var preview = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                // Só os nomes dos campos: valores podem conter dado pessoal e
                // não é necessário exibi-los para o usuário decidir.
                cmd.CommandText = $"""
                    select {nameCol} as n, count(*) as c
                    from {table}
                    group by n
                    order by c desc
                    limit 10
                    """;

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.IsDBNull(0) ? "?" : reader.GetString(0);
                    int count = reader.GetInt32(1);
                    preview.Add($"{Truncate(name, 40),-42} {count,4} entrada(s)");
                }
            }

            preview.Insert(0, "Os valores digitados não são exibidos aqui por conterem dado pessoal.");
            preview.Insert(1, $"O que o navegador lembra: {total} entrada(s) em {preview.Count - 2} campo(s).");

            return new PrivacyItem
            {
                Kind = PrivacyKind.FormAutofill,
                Profile = profile,
                RecordCount = total,
                SizeBytes = size,
                Preview = preview,
                PreviewOfTotal = total,
            };
        }
        catch (Exception ex)
        {
            return Blocked(profile, PrivacyKind.FormAutofill, size, ex.Message);
        }
        finally
        {
            SharedFileReader.TryDelete(temp);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Permissões (Firefox)
    // ════════════════════════════════════════════════════════════════════════

    private static PrivacyItem? ScanFirefoxPermissions(BrowserProfile profile, CancellationToken ct)
    {
        var path = System.IO.Path.Combine(profile.ProfileDirectory, "permissions.sqlite");
        if (!File.Exists(path)) return null;

        long size;
        try { size = new FileInfo(path).Length; }
        catch { return null; }

        string? temp = null;
        try
        {
            using var db = OpenReadOnly(path, ct, out temp, out _);
            if (db is null) return null;

            int total = Scalar(db, "select count(*) from moz_perms");
            if (total == 0) return null;

            var preview = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    select origin, type, permission
                    from moz_perms
                    order by origin
                    limit 10
                    """;

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var origin = reader.IsDBNull(0) ? "?" : reader.GetString(0);
                    var type = reader.IsDBNull(1) ? "?" : reader.GetString(1);
                    int perm = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);

                    var verdict = perm == 1 ? "permitido" : perm == 2 ? "bloqueado" : $"valor {perm}";
                    preview.Add($"{Truncate(origin, 46),-48} {type,-22} {verdict}");
                }
            }

            preview.Insert(0, "Apagar faz o navegador perguntar de novo em cada site.");

            return new PrivacyItem
            {
                Kind = PrivacyKind.SitePermissions,
                Profile = profile,
                RecordCount = total,
                SizeBytes = size,
                Preview = preview,
                PreviewOfTotal = total,
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            SharedFileReader.TryDelete(temp);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Cache e sessões (medidos em disco, sem SQLite)
    // ════════════════════════════════════════════════════════════════════════

    private static IEnumerable<PrivacyItem> ScanCache(BrowserProfile profile, CancellationToken ct)
    {
        // Cada pasta de cache vira um item próprio: medir e apagar por pasta
        // mantém o comportamento anterior (uma pasta, um item).
        foreach (var path in profile.CachePaths)
        {
            if (!Directory.Exists(path)) continue;

            var measured = DirectoryMeasurer.Measure(path, ct);
            if (measured.FileCount == 0) continue;

            yield return new PrivacyItem
            {
                Kind = PrivacyKind.BrowserCache,
                Profile = profile,
                DirectoryPath = path,
                RecordCount = measured.FileCount,
                SizeBytes = measured.SizeBytes,
                Preview =
                [
                    $"{measured.FileCount:N0} arquivo(s) de cache em disco",
                    "Apagar não perde nada. O primeiro acesso a cada site fica mais lento.",
                    path,
                ],
                PreviewOfTotal = measured.FileCount,
            };
        }
    }

    private static PrivacyItem? ScanSessions(BrowserProfile profile, CancellationToken ct)
    {
        var path = profile.SessionsPath;
        if (path is null || !Directory.Exists(path)) return null;

        var measured = DirectoryMeasurer.Measure(path, ct);
        if (measured.FileCount == 0) return null;

        return new PrivacyItem
        {
            Kind = PrivacyKind.SessionTabs,
            Profile = profile,
            DirectoryPath = path,
            RecordCount = measured.FileCount,
            SizeBytes = measured.SizeBytes,
            Preview =
            [
                "As abas que seriam restauradas ao abrir o navegador",
                "Apagar perde essa lista. Favoritos não são afetados.",
                path,
            ],
            PreviewOfTotal = measured.FileCount,
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Auxiliares
    // ════════════════════════════════════════════════════════════════════════

    private static PrivacyItem Blocked(BrowserProfile profile, PrivacyKind kind, long size, string? reason)
        => new()
        {
            Kind = kind,
            Profile = profile,
            RecordCount = 0,
            SizeBytes = size,
            BlockedReason = reason ?? "não foi possível ler",
            Preview = [$"Não foi possível ler: {reason ?? "motivo desconhecido"}"],
        };

    /// <summary>
    /// Abre o banco por uma cópia temporária, em modo somente leitura.
    /// Devolve null (com motivo em <paramref name="error"/>) se não der.
    /// </summary>
    private static SqliteConnection? OpenReadOnly(string path, CancellationToken ct, out string? temp, out string? error)
    {
        temp = SharedFileReader.CopyToTemp(path, out error, ct);
        if (temp is null) return null;

        SqliteConnection? conn = null;
        try
        {
            // Mode=ReadOnly: garantia em nível de biblioteca de que nada será escrito.
            conn = new SqliteConnection($"Data Source={temp};Mode=ReadOnly;Pooling=False");
            conn.Open();
            return conn;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            // Fecha qualquer handle parcialmente aberto antes de apagar a cópia.
            conn?.Dispose();
            SharedFileReader.TryDelete(temp);
            temp = null;
            return null;
        }
    }

    private static int Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>Microssegundos desde 1601-01-01 (Chromium).</summary>
    private static string FormatChromiumTime(long microseconds)
    {
        if (microseconds <= 0) return "—";
        try
        {
            var when = DateTimeOffset.FromFileTime(microseconds * 10);
            return when.LocalDateTime.ToString("dd/MM/yy HH:mm");
        }
        catch { return "—"; }
    }

    /// <summary>Microssegundos desde 1970-01-01 (Firefox).</summary>
    private static string FormatUnixTime(long microseconds)
    {
        if (microseconds <= 0) return "—";
        try
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000);
            return when.LocalDateTime.ToString("dd/MM/yy HH:mm");
        }
        catch { return "—"; }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";
}
