using System.IO;
using System.Security.Principal;

namespace Systenclin.Core;

/// <summary>
/// Define O QUE é varrido. Nenhuma varredura é destrutiva aqui: só descreve alvos.
///
/// Princípio de projeto: cada alvo é uma pasta concreta e conhecida, nunca um
/// curinga amplo. Nada de "apagar todo *.log do disco".
/// </summary>
public static class ScanTargets
{
    public sealed record Target(
        string Path,
        ScanCategory Category,
        RiskLevel Risk,
        string? Note = null,
        bool WholeDirectory = true,
        bool RequiresAdmin = false);

    /// <summary>
    /// Indica se o processo está elevado (administrador). Calculado uma única
    /// vez; alvos marcados com RequiresAdmin são ignorados quando falso.
    /// </summary>
    public static bool IsElevated => IsElevatedOnce;

    private static readonly bool IsElevatedOnce = ComputeIsElevated();

    private static bool ComputeIsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // Sem como saber: assume não elevado, o caminho mais conservador.
            return false;
        }
    }

    public static IEnumerable<Target> All()
    {
        // Lista completa primeiro; a emissão abaixo remove duplicatas.
        var targets = BuildTargets().ToList();

        // Path.GetTempPath() costuma ser a MESMA pasta de %LocalAppData%\Temp;
        // sem deduplicar, os bytes seriam contados e varridos duas vezes.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (seen.Add(NormalizeKey(target.Path)))
                yield return target;
        }
    }

    /// <summary>
    /// Chave de deduplicação de alvos: caminho completo sem separador final.
    /// Para curingas, normaliza só a pasta e mantém o padrão.
    /// </summary>
    private static string NormalizeKey(string path)
    {
        try
        {
            var pattern = string.Empty;
            var dir = path;

            if (path.Contains('*'))
            {
                dir = Path.GetDirectoryName(path) ?? path;
                pattern = Path.GetFileName(path);
            }

            var full = Path.GetFullPath(dir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return pattern.Length == 0 ? full : full + Path.DirectorySeparatorChar + pattern;
        }
        catch
        {
            // Sem normalizar, usa o texto bruto; pior caso, não deduplica.
            return path;
        }
    }

    /// <summary>
    /// Monta a lista bruta de alvos, sem deduplicação (ver <see cref="All"/>).
    /// </summary>
    private static IEnumerable<Target> BuildTargets()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // ── Arquivos temporários ────────────────────────────────────────────
        yield return new Target(Path.GetTempPath(), ScanCategory.TemporaryFiles, RiskLevel.Low,
            "Pasta TEMP do usuário");

        yield return new Target(Path.Combine(windir, "Temp"), ScanCategory.TemporaryFiles, RiskLevel.Low,
            "TEMP do sistema (exige administrador)", RequiresAdmin: true);

        yield return new Target(Path.Combine(local, "Temp"), ScanCategory.TemporaryFiles, RiskLevel.Low);

        yield return new Target(Path.Combine(local, "Microsoft", "Windows", "INetCache"),
            ScanCategory.TemporaryFiles, RiskLevel.Low, "Cache da Internet");

        yield return new Target(Path.Combine(local, "Microsoft", "Windows", "WebCache"),
            ScanCategory.TemporaryFiles, RiskLevel.Low);

        // ── Cache de navegadores (Chrome, Edge, Firefox, Brave, Opera, Vivaldi) ─
        // Descoberta em disco: um alvo por pasta de cache de cada perfil. O
        // navegador recria o que for apagado; arquivo em uso é pulado (não é
        // erro). Varredura de melhor esforço: falha de disco não derruba o resto.
        // Yield dentro de try/catch não é permitido em iterator; materializa
        // primeiro e emite depois.
        List<Target>? browserTargets = null;
        try
        {
            browserTargets = [];
            foreach (var profile in BrowserLocator.Discover())
            {
                foreach (var cachePath in profile.CachePaths)
                {
                    browserTargets.Add(new Target(cachePath, ScanCategory.BrowserCache, RiskLevel.Low,
                        $"{profile.Browser.DisplayName()} — {profile.ProfileName}"));
                }
            }
        }
        catch
        {
            browserTargets = null;
        }

        if (browserTargets is not null)
        {
            foreach (var t in browserTargets) yield return t;
        }

        yield return new Target(Path.Combine(local, "CrashDumps"),
            ScanCategory.ErrorReports, RiskLevel.Low, "Despejos de falha de aplicativos");

        // ── Windows Update ──────────────────────────────────────────────────
        yield return new Target(Path.Combine(windir, "SoftwareDistribution", "Download"),
            ScanCategory.WindowsUpdateCache, RiskLevel.Medium,
            "Pacotes de atualização já aplicados (exige administrador)", RequiresAdmin: true);

        yield return new Target(Path.Combine(windir, "SoftwareDistribution", "DeliveryOptimization"),
            ScanCategory.WindowsUpdateCache, RiskLevel.Medium, "Cache de otimização de entrega",
            RequiresAdmin: true);

        yield return new Target(Path.Combine(windir, "Logs", "CBS"),
            ScanCategory.LogFiles, RiskLevel.Medium, "Logs de manutenção do componente Windows",
            RequiresAdmin: true);

        yield return new Target(Path.Combine(windir, "Logs", "DISM"),
            ScanCategory.LogFiles, RiskLevel.Low, null, RequiresAdmin: true);

        // ── Miniaturas e cache gráfico ──────────────────────────────────────
        yield return new Target(Path.Combine(local, "Microsoft", "Windows", "Explorer"),
            ScanCategory.ThumbnailCache, RiskLevel.Low,
            "Cache de miniaturas do Explorer");

        // ── Relatórios de erro ──────────────────────────────────────────────
        yield return new Target(Path.Combine(local, "Microsoft", "Windows", "WER"),
            ScanCategory.ErrorReports, RiskLevel.Low, "Relatórios de Erro do Windows");

        yield return new Target(Path.Combine(roaming, "Microsoft", "Windows", "WER"),
            ScanCategory.ErrorReports, RiskLevel.Low);

        // ── Cache de gerenciadores de pacote ────────────────────────────────
        yield return new Target(Path.Combine(local, "NuGet", "v3-cache"),
            ScanCategory.PackageCache, RiskLevel.Medium, "Cache do NuGet");

        yield return new Target(Path.Combine(local, "pip", "Cache"),
            ScanCategory.PackageCache, RiskLevel.Medium, "Cache do pip");

        yield return new Target(Path.Combine(roaming, "npm-cache", "_cacache"),
            ScanCategory.PackageCache, RiskLevel.Medium, "Cache do npm");

        yield return new Target(Path.Combine(local, "Yarn", "Cache"),
            ScanCategory.PackageCache, RiskLevel.Medium, "Cache do Yarn");

        yield return new Target(Path.Combine(local, "Temp", "chocolatey"),
            ScanCategory.PackageCache, RiskLevel.Medium, "Cache do Chocolatey");

        // ── Logs antigos do perfil ──────────────────────────────────────────
        // Alvo "AppData\Local\Temp\*.log" removido: os mesmos bytes já são
        // contados pelo alvo da pasta TEMP (dupla contagem). O motor de
        // padrões (ScanPattern) segue disponível para alvos futuros.
    }

    /// <summary>
    /// Pastas que NUNCA podem ser alvo, mesmo que apareçam em algum alvo acima.
    /// É uma segunda linha de defesa contra junction ou caminho mal formado.
    /// </summary>
    private static readonly string[] ForbiddenFragments =
    [
        @"\Windows\System32",
        @"\Windows\SysWOW64",
        @"\Windows\WinSxS",
        @"\Windows\Fonts",
        @"\Windows\assembly",
        @"\Program Files",
        @"\Program Files (x86)",
        @"\ProgramData\Microsoft\Windows\Start Menu",
        @"\Documents",
        @"\Desktop",
        @"\Pictures",
        @"\Downloads",
    ];

    /// <summary>
    /// Confere se um caminho pode ser apagado. Chamado antes de cada remoção,
    /// não só na varredura — o disco pode ter mudado entre as duas etapas.
    /// </summary>
    public static bool IsSafeToDelete(string path, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "caminho vazio";
            return false;
        }

        // Travessia é recusada no texto ORIGINAL: a normalização abaixo já
        // resolveria ".." e apagaria o rastro antes da checagem.
        if (path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(s => s == ".."))
        {
            reason = "caminho com travessia";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            reason = "caminho inválido";
            return false;
        }

        // Recusa raízes de unidade e pastas muito rasas.
        var root = Path.GetPathRoot(full);
        if (string.Equals(full.TrimEnd('\\'), root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            reason = "é a raiz da unidade";
            return false;
        }

        var depth = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth < 3)
        {
            reason = "caminho raso demais para ser cache";
            return false;
        }

        // Comparação por SEGMENTO contíguo, não por substring: "DownloadsOld"
        // não é a pasta "Downloads" e deixa de ser bloqueado por engano, mas
        // "C:\...\Downloads" continua bloqueado.
        var segments = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var forbidden in ForbiddenFragments)
        {
            var fragment = forbidden.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (ContainsSegmentSequence(segments, fragment))
            {
                reason = $"contém área protegida ({forbidden.TrimStart('\\')})";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Verdadeiro quando os segmentos do fragmento aparecem, na ordem, como
    /// subsequência contígua dos segmentos do caminho (sem diferenciar caixa).
    /// </summary>
    private static bool ContainsSegmentSequence(string[] pathSegments, string[] fragmentSegments)
    {
        if (fragmentSegments.Length == 0 || fragmentSegments.Length > pathSegments.Length)
            return false;

        for (int i = 0; i <= pathSegments.Length - fragmentSegments.Length; i++)
        {
            var match = true;
            for (int j = 0; j < fragmentSegments.Length; j++)
            {
                if (!string.Equals(pathSegments[i + j], fragmentSegments[j], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match) return true;
        }

        return false;
    }
}
