using System.IO;
using Microsoft.Win32;

namespace Systenclin.Core;

/// <summary>Qual navegador. A ordem define a ordem de exibição.</summary>
public enum BrowserKind
{
    Chrome = 0,
    Edge = 1,
    Firefox = 2,
    Brave = 3,
    Opera = 4,
    Vivaldi = 5,
}

public static class BrowserKindExtensions
{
    public static string DisplayName(this BrowserKind b) => b switch
    {
        BrowserKind.Chrome => "Google Chrome",
        BrowserKind.Edge => "Microsoft Edge",
        BrowserKind.Firefox => "Mozilla Firefox",
        BrowserKind.Brave => "Brave",
        BrowserKind.Opera => "Opera",
        BrowserKind.Vivaldi => "Vivaldi",
        _ => b.ToString(),
    };

    /// <summary>Processo que precisa estar fechado para o banco abrir com segurança.</summary>
    public static string ProcessName(this BrowserKind b) => b switch
    {
        BrowserKind.Chrome => "chrome",
        BrowserKind.Edge => "msedge",
        BrowserKind.Firefox => "firefox",
        BrowserKind.Brave => "brave",
        BrowserKind.Opera => "opera",
        BrowserKind.Vivaldi => "vivaldi",
        _ => b.ToString().ToLowerInvariant(),
    };

    /// <summary>Navegadores baseados em Chromium compartilham o mesmo layout de dados.</summary>
    public static bool IsChromium(this BrowserKind b) => b != BrowserKind.Firefox;
}

/// <summary>
/// Um perfil de navegador encontrado no disco.
/// Perfis são descobertos, não presumidos: o usuário pode ter 6 perfis do Chrome.
/// </summary>
public sealed record BrowserProfile(
    BrowserKind Browser,
    string ProfileName,
    string ProfileDirectory)
{
    public string Display => $"{Browser.DisplayName()} — {ProfileName}";

    /// <summary>Banco de histórico (Chromium) ou places.sqlite (Firefox).</summary>
    public string? HistoryPath => Browser.IsChromium()
        ? Path.Combine(ProfileDirectory, "History")
        : Path.Combine(ProfileDirectory, "places.sqlite");

    /// <summary>Banco de cookies.</summary>
    public string? CookiesPath => Browser switch
    {
        BrowserKind.Firefox => Path.Combine(ProfileDirectory, "cookies.sqlite"),
        _ => Path.Combine(ProfileDirectory, "Network", "Cookies"),
    };

    /// <summary>Autopreenchimento de formulários (Chromium) / formhistory.sqlite (Firefox).</summary>
    public string? FormHistoryPath => Browser.IsChromium()
        ? Path.Combine(ProfileDirectory, "Web Data")
        : Path.Combine(ProfileDirectory, "formhistory.sqlite");

    /// <summary>
    /// Diretórios de cache existentes no perfil — cobre os vários caches do
    /// Chromium (HTTP, código, GPU, mídia, service worker) e do Firefox.
    /// Só devolve o que existe no disco.
    /// </summary>
    public IReadOnlyList<string> CachePaths
    {
        get
        {
            var names = Browser == BrowserKind.Firefox
                ? FirefoxCacheDirs
                : ChromiumCacheDirs;

            var paths = new List<string>(names.Length);
            foreach (var name in names)
            {
                var dir = Path.Combine(ProfileDirectory, name);
                if (Directory.Exists(dir)) paths.Add(dir);
            }

            return paths;
        }
    }

    private static readonly string[] ChromiumCacheDirs =
    [
        "Cache",
        "Code Cache",
        "GPUCache",
        "Media Cache",
        Path.Combine("Service Worker", "CacheStorage"),
    ];

    private static readonly string[] FirefoxCacheDirs =
    [
        "cache2",
        "startupCache",
        "thumbnails",
    ];

    /// <summary>Sessões salvas (abas abertas). Apagar perde as abas restauradas.</summary>
    public string? SessionsPath => Browser.IsChromium()
        ? Path.Combine(ProfileDirectory, "Sessions")
        : Path.Combine(ProfileDirectory, "sessionstore-backups");
}

/// <summary>
/// Encontra os perfis de navegador instalados nesta máquina.
/// Tudo é descoberta em disco — nada é presumido.
/// </summary>
public static class BrowserLocator
{
    public static IReadOnlyList<BrowserProfile> Discover()
    {
        var found = new List<BrowserProfile>();

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        try
        {
            // ── Navegadores Chromium (estáveis e variantes beta/canário) ────
            var chromiumRoots = new (BrowserKind Kind, string Root)[]
            {
                (BrowserKind.Chrome,  Path.Combine(local, "Google", "Chrome", "User Data")),
                (BrowserKind.Chrome,  Path.Combine(local, "Google", "Chrome Beta", "User Data")),
                (BrowserKind.Chrome,  Path.Combine(local, "Google", "Chrome SxS", "User Data")),
                (BrowserKind.Edge,    Path.Combine(local, "Microsoft", "Edge", "User Data")),
                (BrowserKind.Edge,    Path.Combine(local, "Microsoft", "Edge Beta", "User Data")),
                (BrowserKind.Brave,   Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
                (BrowserKind.Brave,   Path.Combine(local, "BraveSoftware", "Brave-Browser-Beta", "User Data")),
                (BrowserKind.Vivaldi, Path.Combine(local, "Vivaldi", "User Data")),
            };

            foreach (var (kind, root) in chromiumRoots)
            {
                if (!Directory.Exists(root)) continue;

                foreach (var dir in EnumerateChromiumProfiles(root))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith("System Profile", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("Guest Profile", StringComparison.OrdinalIgnoreCase)) continue;

                    // Só entra perfil que tem pelo menos um banco.
                    if (File.Exists(Path.Combine(dir, "History")) ||
                        File.Exists(Path.Combine(dir, "Network", "Cookies")) ||
                        File.Exists(Path.Combine(dir, "Web Data")))
                    {
                        found.Add(new BrowserProfile(kind, PrettyName(name), dir));
                    }
                }
            }

            // Opera tem caminho próprio: versão estável e GX.
            foreach (var operaRoot in new[]
                     {
                         Path.Combine(roaming, "Opera Software", "Opera Stable"),
                         Path.Combine(roaming, "Opera Software", "Opera GX Stable"),
                     })
            {
                if (Directory.Exists(operaRoot) && File.Exists(Path.Combine(operaRoot, "History")))
                    found.Add(new BrowserProfile(BrowserKind.Opera, Path.GetFileName(operaRoot), operaRoot));
            }

            // ── Firefox ─────────────────────────────────────────────────────
            var ffRoot = Path.Combine(roaming, "Mozilla", "Firefox", "Profiles");
            if (Directory.Exists(ffRoot))
            {
                List<string> ffDirs;
                try { ffDirs = [.. Directory.EnumerateDirectories(ffRoot)]; }
                catch { ffDirs = []; }

                foreach (var dir in ffDirs)
                {
                    if (!File.Exists(Path.Combine(dir, "places.sqlite"))) continue;

                    var name = Path.GetFileName(dir);
                    found.Add(new BrowserProfile(BrowserKind.Firefox, PrettyName(name), dir));
                }
            }
        }
        catch
        {
            // Descoberta é melhor esforço: falha de disco não pode derrubar a
            // varredura inteira — devolve o que já foi encontrado.
        }

        return found
            .OrderBy(p => p.Browser)
            .ThenBy(p => p.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateChromiumProfiles(string root)
    {
        // "Default"
        var def = Path.Combine(root, "Default");
        if (Directory.Exists(def)) yield return def;

        // "Profile 1" … "Profile 99". Materializado dentro do try: a enumeração
        // é preguiçosa e, sem isso, erro de disco escaparia do catch no meio.
        List<string> dirs;
        try { dirs = [.. Directory.EnumerateDirectories(root, "Profile *")]; }
        catch { dirs = []; }

        foreach (var d in dirs) yield return d;
    }

    /// <summary>"Profile 2" → "Perfil 2"; "j3bq4wxp.default-release" → "default-release".</summary>
    private static string PrettyName(string raw)
    {
        if (raw.Equals("Default", StringComparison.OrdinalIgnoreCase)) return "Padrão";

        if (raw.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
            return "Perfil " + raw["Profile ".Length..];

        // Firefox: nome aleatório + "." + nome legível.
        var dot = raw.IndexOf('.');
        if (dot > 0 && dot < raw.Length - 1) return raw[(dot + 1)..];

        return raw;
    }
}
