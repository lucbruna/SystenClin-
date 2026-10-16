using System.IO;
using Systenclin.Core;

// Verificação do núcleo do Systenclin — v0.1 (arquivos) + v0.2 (privacidade).
// Sem framework de teste: roda e conta.

int passed = 0, failed = 0;

void Check(string name, bool condition, string? detail = null)
{
    if (condition) { passed++; Console.WriteLine($"  ok    {name}"); }
    else { failed++; Console.WriteLine($"  FALHA {name}" + (detail is null ? "" : $"  → {detail}")); }
}

void Section(string title) => Console.WriteLine($"\n{title}");

Console.WriteLine("Systenclin — verificação do núcleo");
Console.WriteLine(new string('─', 66));

// ══════════════════════════════════════════════════════════════════════════
Section("1. Guarda de segurança (IsSafeToDelete)");
// ══════════════════════════════════════════════════════════════════════════

Check("recusa raiz C:\\", !ScanTargets.IsSafeToDelete(@"C:\", out _));
Check("recusa System32", !ScanTargets.IsSafeToDelete(@"C:\Windows\System32", out _));
Check("recusa System32\\drivers", !ScanTargets.IsSafeToDelete(@"C:\Windows\System32\drivers", out _));
Check("recusa WinSxS", !ScanTargets.IsSafeToDelete(@"C:\Windows\WinSxS\Temp", out _));
Check("recusa Fonts", !ScanTargets.IsSafeToDelete(@"C:\Windows\Fonts", out _));
Check("recusa Program Files", !ScanTargets.IsSafeToDelete(@"C:\Program Files\App", out _));
Check("recusa Program Files (x86)", !ScanTargets.IsSafeToDelete(@"C:\Program Files (x86)\App", out _));
Check("recusa Documents", !ScanTargets.IsSafeToDelete(@"C:\Users\x\Documents\lixo", out _));
Check("recusa Desktop", !ScanTargets.IsSafeToDelete(@"C:\Users\x\Desktop\temp", out _));
Check("recusa Downloads", !ScanTargets.IsSafeToDelete(@"C:\Users\x\Downloads", out _));
Check("recusa Pictures", !ScanTargets.IsSafeToDelete(@"C:\Users\x\Pictures\c", out _));
Check("recusa caminho raso", !ScanTargets.IsSafeToDelete(@"C:\Users", out _));
Check("recusa vazio", !ScanTargets.IsSafeToDelete("", out _));
Check("recusa travessia", !ScanTargets.IsSafeToDelete(@"C:\Temp\..\Windows\System32", out _));

Check("aceita TEMP", ScanTargets.IsSafeToDelete(Path.GetTempPath(), out var r1), r1);
Check("aceita cache do Explorer", ScanTargets.IsSafeToDelete(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Explorer"), out var r2), r2);
Check("aceita CrashDumps", ScanTargets.IsSafeToDelete(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrashDumps"), out var r3), r3);

// ══════════════════════════════════════════════════════════════════════════
Section("2. Medição de diretório");
// ══════════════════════════════════════════════════════════════════════════

var sandbox = Path.Combine(Path.GetTempPath(), "systenclin-t-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(sandbox);

try
{
    for (int i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(sandbox, $"a{i}.tmp"), new byte[1000]);
    var sub = Path.Combine(sandbox, "sub");
    Directory.CreateDirectory(sub);
    for (int i = 0; i < 2; i++) File.WriteAllBytes(Path.Combine(sub, $"b{i}.tmp"), new byte[500]);

    var m = DirectoryMeasurer.Measure(sandbox);
    Check("conta 5 arquivos", m.FileCount == 5, $"contou {m.FileCount}");
    Check("soma 4000 bytes", m.SizeBytes == 4000, $"somou {m.SizeBytes}");

    using var cts = new CancellationTokenSource();
    cts.Cancel();
    bool cancelled = false;
    try { DirectoryMeasurer.Measure(sandbox, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check("respeita cancelamento", cancelled);

    var missing = DirectoryMeasurer.Measure(Path.Combine(sandbox, "nao-existe"));
    Check("diretório inexistente devolve zero", missing.FileCount == 0);

    // ══════════════════════════════════════════════════════════════════════
    Section("3. Varredura e limpeza de arquivos (v0.1)");
    // ══════════════════════════════════════════════════════════════════════

    var scanner = new Scanner();
    var items = await scanner.ScanAsync();

    Check("varredura devolve itens", items.Count > 0, $"{items.Count} itens");
    Check("todo item tem caminho absoluto", items.All(i => Path.IsPathRooted(i.Path)));
    Check("nenhum item é área proibida",
        items.All(i => ScanTargets.IsSafeToDelete(i.Path, out _)),
        string.Join(", ", items.Where(i => !ScanTargets.IsSafeToDelete(i.Path, out _)).Select(i => i.Path)));
    Check("todo item tem tamanho > 0", items.All(i => i.SizeBytes > 0));

    var cleaner = new Cleaner();
    var fake = new CleanableItem
    {
        Path = sandbox, Category = ScanCategory.TemporaryFiles,
        SizeBytes = 4000, IsDirectory = true, FileCount = 5, Risk = RiskLevel.Low,
    };
    var cleanResult = await cleaner.CleanAsync([fake]);
    Check("limpeza de pasta funcionou", cleanResult[0].Succeeded, cleanResult[0].Error);
    Check("pasta removida", !Directory.Exists(sandbox));

    var dangerous = new CleanableItem
    {
        Path = @"C:\Windows\System32", Category = ScanCategory.TemporaryFiles,
        SizeBytes = 999, IsDirectory = true, Risk = RiskLevel.Low,
    };
    var refused = await cleaner.CleanAsync([dangerous]);
    Check("recusa System32 em tempo de execução", !refused[0].Succeeded, "aceitou!");
    Check("System32 intacto", Directory.Exists(@"C:\Windows\System32"));

    // ══════════════════════════════════════════════════════════════════════
    Section("4. Descoberta de perfis de navegador (v0.2)");
    // ══════════════════════════════════════════════════════════════════════

    var profiles = BrowserLocator.Discover();

    Check("encontrou pelo menos um perfil", profiles.Count > 0, $"{profiles.Count} perfis");

    Check("todo perfil tem diretório existente",
        profiles.All(p => Directory.Exists(p.ProfileDirectory)),
        string.Join(", ", profiles.Where(p => !Directory.Exists(p.ProfileDirectory)).Select(p => p.ProfileDirectory)));

    Check("todo perfil tem caminho de histórico",
        profiles.All(p => !string.IsNullOrEmpty(p.HistoryPath)));

    Check("todo perfil tem caminho de cookies",
        profiles.All(p => !string.IsNullOrEmpty(p.CookiesPath)));

    Check("todo perfil tem nome legível",
        profiles.All(p => !string.IsNullOrEmpty(p.ProfileName) && p.ProfileName.Length < 60),
        string.Join(", ", profiles.Select(p => p.ProfileName)));

    Check("nenhum perfil é 'System Profile'",
        profiles.All(p => !p.ProfileName.Contains("System Profile", StringComparison.OrdinalIgnoreCase)));

    Check("nenhum perfil é 'Guest Profile'",
        profiles.All(p => !p.ProfileName.Contains("Guest Profile", StringComparison.OrdinalIgnoreCase)));

    Check("perfis são únicos (sem duplicata)",
        profiles.Select(p => p.ProfileDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count() == profiles.Count);

    Console.WriteLine($"        {profiles.Count} perfil(is): " +
        string.Join(" · ", profiles.Select(p => p.Display)));

    // ══════════════════════════════════════════════════════════════════════
    Section("5. Leitura compartilhada de arquivo travado");
    // ══════════════════════════════════════════════════════════════════════

    var lockedFile = Path.Combine(Path.GetTempPath(), "systenclin-lock-" + Guid.NewGuid().ToString("N")[..8] + ".dat");
    File.WriteAllBytes(lockedFile, new byte[12345]);

    // Abre com bloqueio exclusivo, simulando um navegador.
    // FileShare.Read: outro processo PODE abrir para leitura, mas não para escrita.
    // Este é o caso real — o navegador não nega leitura, ele nega escrita.
    using (var exclusive = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
    {
        var copy = SharedFileReader.CopyToTemp(lockedFile, out var readError);
        Check("lê arquivo que outro processo mantém aberto", copy is not null, readError);

        if (copy is not null)
        {
            Check("cópia tem o tamanho correto", new FileInfo(copy).Length == 12345);
            SharedFileReader.TryDelete(copy);
        }
    }

    // Bloqueio verdadeiramente exclusivo (FileShare.None): recusar é o certo.
    using (var exclusive = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var copy = SharedFileReader.CopyToTemp(lockedFile, out var readError);
        Check("recusa (com motivo) quando o bloqueio é totalmente exclusivo", copy is null && readError is not null, readError);
    }

    var missingCopy = SharedFileReader.CopyToTemp(
        Path.Combine(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid().ToString("N")), out var mErr);
    Check("arquivo inexistente devolve null", missingCopy is null);
    Check("motivo do erro é informado", !string.IsNullOrEmpty(mErr), mErr);

    SharedFileReader.TryDelete(lockedFile);

    // ══════════════════════════════════════════════════════════════════════
    Section("6. Varredura de privacidade");
    // ══════════════════════════════════════════════════════════════════════

    var privacy = new PrivacyScanner();
    var privacyItems = await privacy.ScanAsync();

    Console.WriteLine($"        {privacyItems.Count} item(ns) de privacidade encontrados");

    Check("varredura de privacidade roda sem exceção", true);

    Check("todo item aponta para um perfil conhecido",
        privacyItems.All(i => profiles.Contains(i.Profile)));

    Check("item bloqueado informa o motivo",
        privacyItems.Where(i => i.BlockedReason is not null).All(i => i.Preview.Count > 0));

    Check("histórico tem prévia com conteúdo",
        !privacyItems.Any(i => i.Kind == PrivacyKind.BrowsingHistory
                            && i.BlockedReason is null
                            && i.RecordCount > 0
                            && i.Preview.Count == 0),
        "histórico sem prévia");

    Check("cookies nunca vêm marcados por padrão", !PrivacyKind.Cookies.DefaultChecked());
    Check("permissões nunca vêm marcadas", !PrivacyKind.SitePermissions.DefaultChecked());
    Check("histórico vem marcado", PrivacyKind.BrowsingHistory.DefaultChecked());
    Check("cache vem marcado", PrivacyKind.BrowserCache.DefaultChecked());
    Check("cookies são risco alto", PrivacyKind.Cookies.Risk() == RiskLevel.High);

    Check("toda categoria tem nome", Enum.GetValues<PrivacyKind>().All(k => k.DisplayName().Length > 0));
    Check("toda categoria tem explicação longa", Enum.GetValues<PrivacyKind>().All(k => k.Explanation().Length > 40));
    Check("toda categoria tem tipo de risco definido",
        Enum.GetValues<PrivacyKind>().All(k => Enum.IsDefined(k.Risk())));

    foreach (var i in privacyItems.Take(12))
    {
        var status = i.BlockedReason is not null
            ? $"bloqueado ({i.BlockedReason})"
            : $"{i.RecordCount} registros";
        Console.WriteLine($"        {i.Kind.DisplayName(),-26} {i.Profile.Display,-34} {status}");
    }

    // ══════════════════════════════════════════════════════════════════════
    Section("7. Limpeza de privacidade");
    // ══════════════════════════════════════════════════════════════════════

    var privacyCleaner = new PrivacyCleaner();
    var running = PrivacyCleaner.RunningBrowsers();

    Console.WriteLine($"        navegadores abertos agora: {(running.Count == 0 ? "nenhum" : string.Join(", ", running))}");

    // Um banco sintético, para exercitar backup + delete sem tocar em dado real.
    var fakeProfileDir = Path.Combine(sandbox + "-prof", "Default");
    Directory.CreateDirectory(fakeProfileDir);

    var fakeDb = Path.Combine(fakeProfileDir, "History");
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fakeDb}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            create table urls (id integer primary key, url text, title text,
                               visit_count integer, typed_count integer, last_visit_time integer, hidden integer);
            create table visits (id integer primary key, url integer, visit_time integer);
            insert into urls values (1,'https://exemplo.com','Exemplo',3,1,0,0);
            insert into urls values (2,'https://teste.org','Teste',1,0,0,0);
            insert into visits values (1,1,0);
            """;
        cmd.ExecuteNonQuery();
    }

    // Perfil sintético: usamos o construtor direto porque não há navegador real aqui.
    var profile = profiles.FirstOrDefault() ?? new BrowserProfile(BrowserKind.Chrome, "Teste", fakeProfileDir);

    var synthProfile = profile with { ProfileDirectory = fakeProfileDir };

    var item = new PrivacyItem
    {
        Kind = PrivacyKind.BrowsingHistory,
        Profile = synthProfile,
        RecordCount = 2,
        SizeBytes = new FileInfo(fakeDb).Length,
        Preview = ["teste"],
        PreviewOfTotal = 2,
    };

    var backupsBefore = PrivacyCleaner.ListBackups().Count;
    var results = await privacyCleaner.CleanAsync([item]);

    // Se o Chrome estiver aberto, o resultado esperado é "pulado" — e isso é correto.
    if (running.Contains(synthProfile.Browser.DisplayName()))
    {
        Check("pula item de banco quando o navegador está aberto", results[0].Skipped, results[0].Error);
        Console.WriteLine($"        (pulado: {results[0].Error})");
    }
    else
    {
        Check("limpeza de banco funcionou", results[0].Succeeded, results[0].Error);
        Check("backup foi criado", results[0].BackupPath is not null && File.Exists(results[0].BackupPath));
        Check("removeu os registros", results[0].RecordsRemoved == 2, $"removeu {results[0].RecordsRemoved}");

        // Confere que o banco está vazio de verdade.
        using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fakeDb}");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "select count(*) from urls";
        Check("banco ficou vazio", Convert.ToInt32(q.ExecuteScalar()) == 0);

        Check("backup é listado", PrivacyCleaner.ListBackups().Count > backupsBefore);
    }

    // Restauração: só quando nenhum navegador está aberto.
    if (running.Count == 0 && results[0].BackupPath is not null)
    {
        var restored = PrivacyCleaner.RestoreBackupTo(results[0].BackupPath, fakeDb, out var rerr);
        Check("restauração do backup funciona", restored, rerr);

        using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fakeDb}");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "select count(*) from urls";
        Check("registros voltaram após restaurar", Convert.ToInt32(q.ExecuteScalar()) == 2);
    }

    // Cache: cada item representa UMA pasta de disco. Limpar um item não pode
    // tocar nas outras pastas de cache do mesmo perfil (achado de granularidade).
    var cacheProfileDir = Path.Combine(sandbox + "-prof", "cache-gran");
    var cacheA = Path.Combine(cacheProfileDir, "Cache");
    var cacheB = Path.Combine(cacheProfileDir, "Media Cache");
    Directory.CreateDirectory(cacheA);
    Directory.CreateDirectory(cacheB);
    File.WriteAllBytes(Path.Combine(cacheA, "c1.bin"), new byte[100]);
    File.WriteAllBytes(Path.Combine(cacheA, "c2.bin"), new byte[100]);
    File.WriteAllBytes(Path.Combine(cacheB, "m1.bin"), new byte[100]);

    var cacheProfile = new BrowserProfile(BrowserKind.Chrome, "CacheTeste", cacheProfileDir);
    var cacheItem = new PrivacyItem
    {
        Kind = PrivacyKind.BrowserCache,
        Profile = cacheProfile,
        DirectoryPath = cacheA,
        RecordCount = 2,
        SizeBytes = 200,
    };

    var cacheResults = await new PrivacyCleaner().CleanAsync([cacheItem]);
    Check("cache: limpa só a pasta do item",
        cacheResults[0].Succeeded && !Directory.Exists(cacheA), cacheResults[0].Error);
    Check("cache: não toca nas outras pastas do perfil",
        Directory.Exists(cacheB) && File.Exists(Path.Combine(cacheB, "m1.bin")));
    Check("cache: contagem cobre só a pasta do item",
        cacheResults[0].RecordsRemoved == 2, $"removeu {cacheResults[0].RecordsRemoved}");

    // ══════════════════════════════════════════════════════════════════════
    Section("8. Formatação e histórico");
    // ══════════════════════════════════════════════════════════════════════

    Check("formata 0 B", CleanableItem.FormatBytes(0) == "0 B");
    Check("formata 1 GB", CleanableItem.FormatBytes(1024L * 1024 * 1024).Contains("GB"));
    Check("tamanho negativo não quebra", CleanableItem.FormatBytes(-1) == "—");

    Check("lixeira não vem marcada", !ScanCategory.RecycleBin.DefaultChecked());
    Check("temporários vêm marcados", ScanCategory.TemporaryFiles.DefaultChecked());

    var log = new CleanLog();
    Check("histórico de arquivos foi gravado", log.Load().Count >= 1);
    Check("histórico registra a recusa de System32",
        log.Load().Any(e => !e.Succeeded && e.Path.Contains("System32")));

    // ══════════════════════════════════════════════════════════════════════
    Section("9. Remoção segura de árvore de arquivos (FileDeleter)");
    // ══════════════════════════════════════════════════════════════════════

    var ws = Path.Combine(Path.GetTempPath(), "opencode", "systenclin-fd-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(ws);
    var heldLocks = new List<FileStream>();

    string MakeJunction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var err = proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return err.Trim();
    }

    try
    {
        // (a) Árvore aninhada: removida por completo, com contagens corretas.
        var caseA = Path.Combine(ws, "A");
        Directory.CreateDirectory(Path.Combine(caseA, "sub", "deep"));
        File.WriteAllBytes(Path.Combine(caseA, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(caseA, "sub", "b.bin"), new byte[500]);
        File.WriteAllBytes(Path.Combine(caseA, "sub", "deep", "c.bin"), new byte[250]);

        var outA = FileDeleter.DeleteTree(caseA);
        Check("árvore aninhada removida", !Directory.Exists(caseA));
        Check("contagem de arquivos correta", outA.FilesDeleted == 3, $"removeu {outA.FilesDeleted}");
        Check("bytes liberados corretos", outA.BytesFreed == 1750, $"liberou {outA.BytesFreed}");

        // (b) Junção FILHA: o vínculo sai, o alvo fica intacto.
        var targetB = Path.Combine(ws, "B-target");
        Directory.CreateDirectory(targetB);
        File.WriteAllBytes(Path.Combine(targetB, "keep.bin"), new byte[2000]);

        var caseB = Path.Combine(ws, "B");
        Directory.CreateDirectory(caseB);
        var linkB = Path.Combine(caseB, "link");
        var jb = MakeJunction(linkB, targetB);
        Check("junção filha criada", Directory.Exists(linkB), jb);

        FileDeleter.DeleteTree(caseB);
        Check("junção filha desvinculada (pasta-pai removida)", !Directory.Exists(caseB));
        Check("alvo da junção filha permanece", Directory.Exists(targetB));
        Check("arquivo do alvo da junção filha intacto", File.Exists(Path.Combine(targetB, "keep.bin")));

        // (c) RAIZ é a junção: não atravessa; só remove o vínculo.
        var targetC = Path.Combine(ws, "C-target");
        Directory.CreateDirectory(targetC);
        File.WriteAllBytes(Path.Combine(targetC, "keep.bin"), new byte[500]);
        var linkC = Path.Combine(ws, "C-link");
        var jc = MakeJunction(linkC, targetC);
        Check("junção-raiz criada", Directory.Exists(linkC), jc);

        FileDeleter.DeleteTree(linkC);
        Check("raiz-junção desvinculada", !Directory.Exists(linkC));
        Check("alvo da raiz-junção permanece", Directory.Exists(targetC));
        Check("arquivo do alvo da raiz-junção intacto", File.Exists(Path.Combine(targetC, "keep.bin")));

        // (d) Através de junção ANCESTRAL: recusa e não apaga nada.
        var targetD = Path.Combine(ws, "D-target");
        Directory.CreateDirectory(Path.Combine(targetD, "sub"));
        File.WriteAllBytes(Path.Combine(targetD, "sub", "keep.bin"), new byte[300]);
        var linkD = Path.Combine(ws, "D-link");
        var jd = MakeJunction(linkD, targetD);
        Check("junção ancestral criada", Directory.Exists(linkD), jd);

        var outD = FileDeleter.DeleteTree(Path.Combine(linkD, "sub"));
        Check("recusa apagar através de junção ancestral", outD.Error is not null, outD.Error);
        Check("nada apagado sob o alvo da junção ancestral",
            File.Exists(Path.Combine(targetD, "sub", "keep.bin")) && Directory.Exists(linkD));

        // (e) Cancelamento no meio: não lança e reporta falha.
        // Arquivos travados (FileShare.None) forçam uma pausa de 50 ms cada,
        // tornando o cancelamento no meio da árvore determinístico.
        var caseE = Path.Combine(ws, "E");
        Directory.CreateDirectory(caseE);
        for (int i = 0; i < 10; i++)
        {
            var p = Path.Combine(caseE, $"locked-{i}.bin");
            File.WriteAllBytes(p, new byte[64]);
            heldLocks.Add(new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }

        var eItem = new CleanableItem
        {
            Path = caseE, Category = ScanCategory.TemporaryFiles,
            SizeBytes = 10 * 64, IsDirectory = true, FileCount = 10, Risk = RiskLevel.Low,
        };

        using var eCts = new CancellationTokenSource();
        eCts.CancelAfter(100);
        bool threw = false;
        IReadOnlyList<Cleaner.Result> eResults = [];
        try { eResults = await new Cleaner().CleanAsync([eItem], null, eCts.Token); }
        catch { threw = true; }

        Check("cancelamento no meio não lança exceção", !threw);
        Check("cancelamento no meio reporta falha",
            eResults.Count == 1 && !eResults[0].Succeeded,
            eResults.Count == 1 ? eResults[0].Error : "sem resultado");
    }
    finally
    {
        foreach (var s in heldLocks) { try { s.Dispose(); } catch { } }

        // Ordem obrigatória: apaga os vínculos ANTES dos alvos.
        try { var l = Path.Combine(ws, "B", "link"); if (Directory.Exists(l)) Directory.Delete(l); } catch { }
        try { var l = Path.Combine(ws, "C-link"); if (Directory.Exists(l)) Directory.Delete(l); } catch { }
        try { var l = Path.Combine(ws, "D-link"); if (Directory.Exists(l)) Directory.Delete(l); } catch { }
        try { if (Directory.Exists(ws)) Directory.Delete(ws, true); } catch { }
    }

    // ══════════════════════════════════════════════════════════════════════
    Section("10. Scanners destrutivos: duplicados, grandes, pastas, disco");
    // ══════════════════════════════════════════════════════════════════════

    var ws16 = Path.Combine(Path.GetTempPath(), "opencode", "systenclin-sc-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(ws16);

    try
    {
        // ── DuplicateFinder ────────────────────────────────────────────────
        var dup = Path.Combine(ws16, "dup");
        Directory.CreateDirectory(dup);
        var d1 = Path.Combine(dup, "a.bin");
        var d2 = Path.Combine(dup, "b.bin");
        var d3 = Path.Combine(dup, "c.bin");
        File.WriteAllBytes(d1, new byte[200]);
        File.WriteAllBytes(d2, new byte[200]);
        var cBytes = new byte[200];
        cBytes[0] = 1;
        File.WriteAllBytes(d3, cBytes);

        var dupGroups = DuplicateFinder.Find(dup, minimumFileSizeBytes: 100);
        Check("duplicados: idênticos formam grupo de 2",
            dupGroups.Count == 1 && dupGroups[0].Files.Count == 2, $"{dupGroups.Count} grupo(s)");
        Check("duplicados: um byte de diferença não agrupa",
            !dupGroups.Any(g => g.Files.Any(f => f.Path == d3)), "c.bin entrou no grupo");

        // Limite: no tamanho mínimo entra; um byte abaixo fica fora.
        var edge = Path.Combine(ws16, "edge");
        Directory.CreateDirectory(edge);
        File.WriteAllBytes(Path.Combine(edge, "e1.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(edge, "e2.bin"), new byte[100]);
        Check("duplicados: no limite entra", DuplicateFinder.Find(edge, 100).Count == 1);
        Check("duplicados: abaixo do limite fica fora", DuplicateFinder.Find(edge, 101).Count == 0);

        // Cancelamento lança (contrato implementado em DuplicateFinder).
        bool dupCancelled = false;
        try { DuplicateFinder.Find(dup, 100, ct: new CancellationToken(canceled: true)); }
        catch (OperationCanceledException) { dupCancelled = true; }
        Check("duplicados: cancelamento lança", dupCancelled);

        // Arquivo sumindo no meio (aqui, logo após a fase de candidatos): não quebra.
        var gone = Path.Combine(ws16, "gone");
        Directory.CreateDirectory(gone);
        var g1 = Path.Combine(gone, "g1.bin");
        File.WriteAllBytes(g1, new byte[500]);
        File.WriteAllBytes(Path.Combine(gone, "g2.bin"), new byte[500]);
        bool goneDeleted = false;
        var killer = new SyncProgress<string>(_ =>
        {
            if (!goneDeleted) { goneDeleted = true; try { File.Delete(g1); } catch { } }
        });
        var goneGroups = DuplicateFinder.Find(gone, 100, killer);
        Check("duplicados: arquivo sumindo no meio não quebra",
            goneDeleted && goneGroups.Count == 0, $"{goneGroups.Count} grupo(s)");

        // ── LargeFileFinder ───────────────────────────────────────────────
        var large = Path.Combine(ws16, "large");
        Directory.CreateDirectory(large);
        File.WriteAllBytes(Path.Combine(large, "s10.bin"), new byte[10]);
        File.WriteAllBytes(Path.Combine(large, "s30.bin"), new byte[30]);
        File.WriteAllBytes(Path.Combine(large, "s20.bin"), new byte[20]);

        var minFiltered = LargeFileFinder.Find(large, minSizeBytes: 20);
        Check("grandes: filtro de tamanho mínimo respeitado",
            minFiltered.Count == 2 && minFiltered.All(f => f.Size >= 20), $"{minFiltered.Count} arquivo(s)");

        var ordered = LargeFileFinder.Find(large, minSizeBytes: 1);
        Check("grandes: ordenados por tamanho desc",
            ordered.Count == 3 && ordered[0].Size == 30 && ordered[1].Size == 20 && ordered[2].Size == 10);

        // Reparse de primeiro nível: o alvo não é atravessado.
        var lt = Path.Combine(ws16, "large-target");
        Directory.CreateDirectory(lt);
        File.WriteAllBytes(Path.Combine(lt, "big.bin"), new byte[999]);
        var lroot = Path.Combine(ws16, "large-root");
        Directory.CreateDirectory(lroot);
        MakeJunction(Path.Combine(lroot, "link"), lt);
        var jl = LargeFileFinder.Find(lroot, minSizeBytes: 1);
        Check("grandes: não atravessa junção",
            jl.Count == 0 && File.Exists(Path.Combine(lt, "big.bin")));

        bool largeCancelled = false;
        try { LargeFileFinder.Find(large, 1, ct: new CancellationToken(canceled: true)); }
        catch (OperationCanceledException) { largeCancelled = true; }
        Check("grandes: cancelamento lança", largeCancelled);

        // Limite MaxItems (cap real = 2000): 2001 arquivos → devolve 2000 maiores.
        var cap = Path.Combine(ws16, "cap");
        Directory.CreateDirectory(cap);
        for (int i = 0; i < 2001; i++)
            File.WriteAllBytes(Path.Combine(cap, $"f{i:D4}.bin"), new byte[i + 1]);
        var capped = LargeFileFinder.Find(cap, minSizeBytes: 1);
        Check("grandes: respeita o limite de 2000 itens",
            capped.Count == 2000 && capped.Count > 0 && capped[^1].Size == 2,
            $"{capped.Count} itens, menor {(capped.Count > 0 ? capped[^1].Size : 0)}");

        // ── FolderAnalyzer ────────────────────────────────────────────────
        var fa = Path.Combine(ws16, "folders");
        Directory.CreateDirectory(Path.Combine(fa, "A"));
        Directory.CreateDirectory(Path.Combine(fa, "B"));
        Directory.CreateDirectory(Path.Combine(fa, "C"));
        File.WriteAllBytes(Path.Combine(fa, "A", "a.bin"), new byte[10]);
        File.WriteAllBytes(Path.Combine(fa, "B", "b.bin"), new byte[30]);
        File.WriteAllBytes(Path.Combine(fa, "C", "c.bin"), new byte[20]);
        File.WriteAllBytes(Path.Combine(fa, "loose1.bin"), new byte[5]);
        File.WriteAllBytes(Path.Combine(fa, "loose2.bin"), new byte[5]);

        var analysis = FolderAnalyzer.Analyze(fa);
        Check("pastas: filhos ordenados por tamanho desc",
            analysis.Children.Count == 3 && analysis.Children[0].Name == "B"
            && analysis.Children[1].Name == "C" && analysis.Children[2].Name == "A",
            string.Join(", ", analysis.Children.Select(c => c.Name)));
        Check("pastas: arquivos soltos contam no total",
            analysis.LooseFiles == 2 && analysis.LooseBytes == 10
            && analysis.TotalBytes == 70 && analysis.TotalFiles == 5,
            $"soltos {analysis.LooseFiles}/{analysis.LooseBytes}; total {analysis.TotalFiles}/{analysis.TotalBytes}");

        var emptyDir = Path.Combine(ws16, "empty");
        Directory.CreateDirectory(emptyDir);
        var emptyAnalysis = FolderAnalyzer.Analyze(emptyDir);
        Check("pastas: pasta vazia totaliza zero",
            emptyAnalysis.TotalBytes == 0 && emptyAnalysis.TotalFiles == 0 && emptyAnalysis.Children.Count == 0);

        // Junção de primeiro nível: fora de Children e do total.
        var ft = Path.Combine(ws16, "folder-target");
        Directory.CreateDirectory(ft);
        File.WriteAllBytes(Path.Combine(ft, "big.bin"), new byte[1234]);
        var froot = Path.Combine(ws16, "folder-root");
        Directory.CreateDirectory(froot);
        MakeJunction(Path.Combine(froot, "link"), ft);
        var faJunction = FolderAnalyzer.Analyze(froot);
        Check("pastas: junção de primeiro nível fica fora",
            faJunction.Children.Count == 0 && faJunction.TotalBytes == 0);

        bool folderCancelled = false;
        try { FolderAnalyzer.Analyze(fa, ct: new CancellationToken(canceled: true)); }
        catch (OperationCanceledException) { folderCancelled = true; }
        Check("pastas: cancelamento lança", folderCancelled);

        // ── DiskOptimizer ─────────────────────────────────────────────────
        var media = DiskOptimizer.GetSystemDriveMediaType();
        Check("disco: tipo de mídia é um valor definido", Enum.IsDefined(media), media.ToString());

        if (!ScanTargets.IsElevated)
        {
            var opt = DiskOptimizer.OptimizeSystemDrive();
            Check("disco: sem elevação devolve erro amigável",
                !opt.Success && !string.IsNullOrEmpty(opt.Error), opt.Error);
        }
        else
        {
            Console.WriteLine("        (elevado: caminho de otimização sem elevação não testável)");
        }
    }
    finally
    {
        try { var l = Path.Combine(ws16, "large-root", "link"); if (Directory.Exists(l)) Directory.Delete(l); } catch { }
        try { var l = Path.Combine(ws16, "folder-root", "link"); if (Directory.Exists(l)) Directory.Delete(l); } catch { }
        try { if (Directory.Exists(ws16)) Directory.Delete(ws16, true); } catch { }
    }
}
finally
{
    try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); } catch { }
    try { if (Directory.Exists(sandbox + "-prof")) Directory.Delete(sandbox + "-prof", true); } catch { }
}

Console.WriteLine("\n" + new string('─', 66));
Console.WriteLine($"  {passed} passaram · {failed} falharam");
Console.WriteLine(new string('─', 66));

return failed == 0 ? 0 : 1;

// IProgress que dispara o callback na hora; o Progress<T> padrão despacha para
// outra thread e tornaria não determinístico o teste de "arquivo sumindo".
file sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
