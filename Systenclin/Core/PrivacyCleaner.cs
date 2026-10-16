using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Systenclin.Core;

/// <summary>
/// Apaga dados de privacidade dos navegadores.
///
/// Três exigências que não se negociam:
///
/// 1. O navegador precisa estar FECHADO. Não existe forma segura de escrever
///    no banco de um navegador aberto: ele mantém o estado em memória e
///    sobrescreve, ou o banco corrompe. Aqui isso é verificado e recusado.
///
/// 2. Backup ANTES de apagar. O banco é copiado para uma pasta própria do app
///    e o caminho fica no histórico. É a única forma honesta de oferecer
///    desfazer — o v0.1 não conseguia, este consegue.
///
/// 3. Só categoria inteira. Nada de apagar cookie por nome: meia-medida deixa
///    o usuário achando que limpou e não limpou.
/// </summary>
public sealed class PrivacyCleaner
{
    public sealed record Result(
        PrivacyItem Item,
        bool Succeeded,
        bool Skipped,
        int RecordsRemoved,
        string? BackupPath,
        string? Error,
        string? Path = null);

    public sealed record Progress(int Done, int Total, int Records, string Current);

    private static readonly string BackupRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Systenclin", "backup-privacidade");

    public static string BackupLocation => BackupRoot;

    // ════════════════════════════════════════════════════════════════════════
    //  Verificações antes de qualquer coisa
    // ════════════════════════════════════════════════════════════════════════

    private static readonly object RunningLock = new();
    private static readonly Dictionary<BrowserKind, (bool Running, DateTime At)> RunningCache = [];

    /// <summary>
    /// Navegadores abertos agora, pelo nome de exibição.
    /// Tem cache curto (1,5 s) para não repetir a enumeração de processos a
    /// cada item; a lista devolvida é sempre uma cópia nova.
    /// </summary>
    public static IReadOnlyList<string> RunningBrowsers()
    {
        var running = new List<string>();

        foreach (var kind in Enum.GetValues<BrowserKind>())
        {
            if (IsBrowserRunning(kind)) running.Add(kind.DisplayName());
        }

        return running;
    }

    /// <summary>
    /// Mesma detecção de <see cref="RunningBrowsers"/>, mas roda fora da thread
    /// de chamada. Use na UI para não prender a interface durante a enumeração
    /// de processos; o cache curto de 1,5 s continua valendo.
    /// </summary>
    public static Task<IReadOnlyList<string>> RunningBrowsersAsync(CancellationToken ct = default)
        => Task.Run(RunningBrowsers, ct);

    /// <summary>
    /// Confere se o processo de um navegador está rodando. Falha FECHADO: se a
    /// enumeração falhar, trata como aberto — nunca escrever no banco de algo
    /// que pode estar em execução. <paramref name="fresh"/> = true ignora o
    /// cache (rechecagem imediatamente antes de escrever no banco).
    /// </summary>
    private static bool IsBrowserRunning(BrowserKind kind, bool fresh = false)
    {
        if (!fresh)
        {
            lock (RunningLock)
            {
                if (RunningCache.TryGetValue(kind, out var cached) &&
                    DateTime.UtcNow - cached.At < TimeSpan.FromMilliseconds(1500))
                {
                    return cached.Running;
                }
            }
        }

        bool running;
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(kind.ProcessName());
            running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
        }
        catch
        {
            running = true;
        }

        lock (RunningLock)
        {
            RunningCache[kind] = (running, DateTime.UtcNow);
        }

        return running;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Limpeza
    // ════════════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<Result>> CleanAsync(
        IReadOnlyList<PrivacyItem> items,
        IProgress<Progress>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<Result>(items.Count);
        int done = 0, records = 0;

        try
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new Progress(done, items.Count, records, item.Title));

                bool isDisk = item.Kind is PrivacyKind.BrowserCache or PrivacyKind.SessionTabs;

                Result result;
                if (isDisk)
                {
                    // Cache e sessões vivem em disco: vão para a limpeza SEMPRE,
                    // aberto ou não o navegador — arquivos em uso são pulados e
                    // contados, nunca reportados como sucesso falso.
                    result = await Task.Run(() => CleanDiskItem(item, ct), ct).ConfigureAwait(false);
                }
                else
                {
                    // Detecção e limpeza rodam fora da thread da UI: enumerar
                    // processos na thread de interface a prende sem necessidade.
                    result = await Task.Run(() =>
                    {
                        if (IsBrowserRunning(item.Profile.Browser))
                        {
                            // Banco SQLite de navegador aberto não pode ser
                            // tocado: ele mantém o estado em memória e o banco
                            // corromperia.
                            return new Result(item, false, Skipped: true, 0, null,
                                $"Feche o {item.Profile.Browser.DisplayName()} para limpar isto",
                                DbPathFor(item));
                        }

                        return CleanOne(item);
                    }, ct).ConfigureAwait(false);
                }

                results.Add(result);
                if (result.Succeeded) records += result.RecordsRemoved;
                done++;
            }

            progress?.Report(new Progress(items.Count, items.Count, records, "Concluído"));
        }
        finally
        {
            // Trilha de auditoria: registra o que foi feito, inclusive em
            // cancelamento no meio. Logar nunca pode mascarar o resultado.
            try
            {
                if (results.Count > 0)
                {
                    new CleanLog().Append(results.Select(r => new CleanLog.Entry
                    {
                        When = DateTimeOffset.Now,
                        Path = r.Path ?? r.Item.Profile.ProfileDirectory,
                        Category = $"Privacidade — {r.Item.Kind.DisplayName()}",
                        SizeBytes = r.Item.SizeBytes,
                        FileCount = r.RecordsRemoved,
                        IsDirectory = r.Item.Kind is PrivacyKind.BrowserCache or PrivacyKind.SessionTabs,
                        Succeeded = r.Succeeded,
                        Recoverable = r.BackupPath is not null,
                        BackupPath = r.BackupPath,
                        Error = r.Error,
                    }));
                }
            }
            catch { }
        }

        return results;
    }

    private static Result CleanOne(PrivacyItem item)
    {
        var dbPath = DbPathFor(item);

        if (item.RecordCount == 0 && item.BlockedReason is not null)
            return new Result(item, false, Skipped: true, 0, null, item.BlockedReason, dbPath);

        try
        {
            // Arquivo sumiu entre a varredura e agora: item não vazio sem nada
            // para apagar não é sucesso.
            if (dbPath is null || !File.Exists(dbPath))
                return item.RecordCount > 0
                    ? new Result(item, false, false, 0, null,
                        "nada encontrado para apagar (o arquivo pode ter sido removido por outro programa)", dbPath)
                    : new Result(item, true, false, 0, null, null, dbPath);

            // Backup primeiro. Sem backup, não apaga.
            var backup = MakeBackup(dbPath, item.Profile);
            if (backup is null)
                return new Result(item, false, false, 0, null, "não foi possível criar o backup; nada foi apagado", dbPath);

            // O navegador pode ter aberto entre a checagem inicial e agora.
            // Perto de escrever no banco, confere de novo, sem cache: gravar
            // com o navegador aberto corrompe ou é sobrescrito por ele.
            if (IsBrowserRunning(item.Profile.Browser, fresh: true))
                return new Result(item, false, Skipped: true, 0, backup,
                    "navegador abriu durante a limpeza", dbPath);

            var outcome = DeleteRecords(dbPath, item);

            if (outcome.FirstError is not null)
                return new Result(item, false, false, outcome.Removed, backup,
                    $"falha ao apagar: {outcome.FirstError}", dbPath);

            // Item não vazio que não perdeu nenhum registro: não é sucesso.
            if (outcome.Removed == 0 && item.RecordCount > 0)
                return new Result(item, false, false, 0, backup, "nenhum registro removido", dbPath);

            return new Result(item, true, false, outcome.Removed, backup, null, dbPath);
        }
        catch (Exception ex)
        {
            return new Result(item, false, false, 0, null, ex.Message, dbPath);
        }
    }

    private static string? DbPathFor(PrivacyItem item) => item.Kind switch
    {
        PrivacyKind.BrowsingHistory or PrivacyKind.DownloadHistory => item.Profile.HistoryPath,
        PrivacyKind.Cookies => item.Profile.CookiesPath,
        PrivacyKind.FormAutofill => item.Profile.FormHistoryPath,
        PrivacyKind.SitePermissions => Path.Combine(item.Profile.ProfileDirectory, "permissions.sqlite"),
        _ => null,
    };

    /// <summary>
    /// Cache e sessões são arquivos em disco: não precisam de SQLite.
    /// Usa <see cref="FileDeleter.DeleteTree"/> — arquivo em uso é pulado e
    /// contado, nunca derruba a limpeza nem vira "sucesso" falso.
    /// </summary>
    private static Result CleanDiskItem(PrivacyItem item, CancellationToken ct)
    {
        if (item.RecordCount == 0 && item.BlockedReason is not null)
            return new Result(item, false, Skipped: true, 0, null, item.BlockedReason);

        try
        {
            // Cada item de disco representa UMA pasta (ver ScanCache/ScanSessions).
            // A pasta vem no próprio item: apagar pelo perfil inteiro removeria
            // caches que o usuário não marcou, além de contar arquivos a mais.
            var path = item.DirectoryPath;

            if (path is null || !Directory.Exists(path))
                return item.RecordCount > 0
                    ? new Result(item, false, false, 0, null,
                        "nada encontrado para apagar (a pasta pode ter sido removida por outro programa)",
                        item.Profile.ProfileDirectory)
                    : new Result(item, true, false, 0, null, null, item.Profile.ProfileDirectory);

            if (!ScanTargets.IsSafeToDelete(path, out var reason))
                return new Result(item, false, false, 0, null, $"Recusado: {reason}", path);

            var outcome = FileDeleter.DeleteTree(path, ct);
            int deleted = outcome.FilesDeleted;
            int skipped = outcome.FilesSkipped;
            int dirsLeft = outcome.DirsLeftBehind;

            if (outcome.Error is not null)
                return new Result(item, false, false, deleted, null, outcome.Error, path);

            if (skipped == 0 && dirsLeft == 0)
                return new Result(item, true, false, deleted, null, null, path);

            var error = ct.IsCancellationRequested
                ? $"Cancelado: {deleted} arquivo(s) removidos; {skipped + dirsLeft} pendente(s)"
                : $"Parcial: {skipped} arquivo(s) em uso" +
                  (dirsLeft > 0 ? $" e {dirsLeft} pasta(s) restante(s)" : "");

            return new Result(item, false, false, deleted, null, error, path);
        }
        catch (Exception ex)
        {
            return new Result(item, false, false, 0, null, ex.Message);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Backup
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Copia o banco para a pasta de backup. Devolve o caminho, ou null se falhar.
    /// O nome inclui navegador, perfil e data, para o usuário achar depois.
    /// </summary>
    private static string? MakeBackup(string dbPath, BrowserProfile profile)
    {
        try
        {
            Directory.CreateDirectory(BackupRoot);

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var browser = Sanitize(profile.Browser.DisplayName());
            var label = Sanitize(profile.ProfileName);
            var file = Sanitize(Path.GetFileName(dbPath));

            var target = Path.Combine(BackupRoot, $"{stamp}_{browser}_{label}_{file}");

            // Evita colisão quando duas categorias do mesmo perfil são limpas
            // no mesmo segundo (ex.: History guarda histórico E downloads).
            int suffix = 1;
            while (File.Exists(target))
            {
                target = Path.Combine(BackupRoot, $"{stamp}_{suffix++}_{browser}_{label}_{file}");
            }

            // Usa o leitor compartilhado: funciona mesmo se algo ainda segura o arquivo.
            var temp = SharedFileReader.CopyToTemp(dbPath, out _);
            if (temp is null) return null;

            try
            {
                try
                {
                    File.Move(temp, target);
                }
                catch (IOException)
                {
                    // %TEMP% e %LOCALAPPDATA% podem estar em volumes diferentes;
                    // quando o move não atravessa, copia e apaga o temporário.
                    File.Copy(temp, target, overwrite: false);
                }

                return target;
            }
            finally
            {
                SharedFileReader.TryDelete(temp);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Copia o backup de volta para um destino explícito.</summary>
    public static bool RestoreBackupTo(string backupPath, string destinationPath, out string error)
    {
        error = string.Empty;

        try
        {
            if (!File.Exists(backupPath)) { error = "backup não encontrado"; return false; }

            if (string.Equals(Path.GetFullPath(backupPath), Path.GetFullPath(destinationPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "o destino é o próprio arquivo de backup";
                return false;
            }

            var running = RunningBrowsers();
            if (running.Count > 0) { error = $"feche primeiro: {string.Join(", ", running)}"; return false; }

            if (File.Exists(destinationPath))
            {
                var sidecar = destinationPath + ".antes-de-restaurar";
                File.Copy(destinationPath, sidecar, overwrite: true);
            }

            File.Copy(backupPath, destinationPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Backups existentes, mais recentes primeiro.</summary>
    public static IReadOnlyList<(string Path, long Size, DateTime When)> ListBackups()
    {
        try
        {
            if (!Directory.Exists(BackupRoot)) return [];

            return Directory.EnumerateFiles(BackupRoot)
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => (f.FullName, f.Length, f.LastWriteTime))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Apagar registros
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Resultado interno do apagar: quantos saíram e o primeiro erro real, se houve.</summary>
    private readonly record struct DeleteOutcome(int Removed, string? FirstError);

    private static DeleteOutcome DeleteRecords(string dbPath, PrivacyItem item)
    {
        bool chromium = item.Profile.Browser.IsChromium();

        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();

        // VACUUM depois de apagar libera o espaço de verdade. Sem isso o arquivo
        // continua do mesmo tamanho e o usuário acha que nada aconteceu.
        using var tx = conn.BeginTransaction();

        var outcome = item.Kind switch
        {
            PrivacyKind.BrowsingHistory when chromium => Exec(conn, tx,
                [("visits", "delete from visits"), ("urls", "delete from urls")]),

            PrivacyKind.BrowsingHistory => Exec(conn, tx,
                [("moz_historyvisits", "delete from moz_historyvisits"),
                 ("moz_places", "delete from moz_places")]),

            PrivacyKind.Cookies when chromium => Exec(conn, tx,
                [("cookies", "delete from cookies")]),

            PrivacyKind.Cookies => Exec(conn, tx,
                [("moz_cookies", "delete from moz_cookies")]),

            PrivacyKind.FormAutofill when chromium => Exec(conn, tx,
                [("autofill", "delete from autofill")]),

            PrivacyKind.FormAutofill => Exec(conn, tx,
                [("moz_formhistory", "delete from moz_formhistory")]),

            PrivacyKind.DownloadHistory => Exec(conn, tx,
                [("downloads", "delete from downloads"),
                 ("downloads_url_chains", "delete from downloads_url_chains")]),

            PrivacyKind.SitePermissions => Exec(conn, tx,
                [("moz_perms", "delete from moz_perms")]),

            _ => new DeleteOutcome(0, null),
        };

        // Falha parcial: desfaz a transação para que o resultado relatado bata
        // com o que ficou no banco. Sem isso, parte dos registros sairia e o
        // item ainda seria reportado como falha — estado ambíguo e enganoso.
        if (outcome.FirstError is not null)
        {
            try
            {
                tx.Rollback();
                // Nada persistiu: reporta zero removidos, coerente com o banco.
                return new DeleteOutcome(0, outcome.FirstError);
            }
            catch
            {
                // Rollback falhou (melhor esforço): não dá para garantir o
                // estado, então reporta o que foi tentado.
                return outcome;
            }
        }

        tx.Commit();

        try
        {
            using var vacuum = conn.CreateCommand();
            vacuum.CommandText = "VACUUM";
            vacuum.ExecuteNonQuery();
        }
        catch
        {
            // VACUUM pode falhar se houver leitor aberto. Não é motivo para
            // considerar a limpeza falhada: os registros já saíram.
        }

        return outcome;
    }

    /// <summary>
    /// Executa os comandos de apagar. Tabela inexistente é benigna (versão do
    /// navegador sem a tabela) e é detectada de forma tipada, sem depender da
    /// mensagem de erro do SQLite; qualquer outro erro é guardado e reportado ao
    /// chamador — nunca engolido. Os comandos restantes seguem executando.
    /// </summary>
    private static DeleteOutcome Exec(
        SqliteConnection conn,
        SqliteTransaction tx,
        (string Table, string Sql)[] statements)
    {
        int total = 0;
        string? firstError = null;

        foreach (var (table, sql) in statements)
        {
            if (!TableExists(conn, tx, table))
                continue;

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                total += cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
            }
        }

        return new DeleteOutcome(total, firstError);
    }

    /// <summary>
    /// Confere se a tabela existe no esquema. Consultar sqlite_master é estável;
    /// casar a mensagem "no such table" depende de idioma e de versão do SQLite.
    /// </summary>
    private static bool TableExists(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "select 1 from sqlite_master where type = 'table' and name = $name limit 1";
        cmd.Parameters.AddWithValue("$name", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
