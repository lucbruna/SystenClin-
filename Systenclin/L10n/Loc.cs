using System.Globalization;
using System.Resources;

namespace Systenclin.L10n;

/// <summary>
/// Localização da UI: idioma atual (pt-BR / en / es), T(chave) e troca em runtime.
/// O resx neutro carrega pt-BR; en e es são satélites.
/// </summary>
internal static class Loc
{
    private static readonly ResourceManager Rm = new("Systenclin.L10n.Strings", typeof(Loc).Assembly);

    public static string Current { get; private set; } = "pt-BR";

    /// <summary>Disparado quando o idioma muda; o MainWindow re-varre a árvore.</summary>
    public static event Action? Changed;

    /// <summary>Carrega o idioma salvo; sem escolha, segue o idioma do Windows.</summary>
    public static void Init()
    {
        Current = Normalize(Core.AppSettings.Language) ?? FromOs();
    }

    public static void Set(string code)
    {
        if (Normalize(code) is not { } safe || safe == Current) return;

        Current = safe;
        Core.AppSettings.Language = safe;
        Core.AppSettings.Save();
        Changed?.Invoke();
    }

    public static string T(string key)
    {
        try
        {
            return Rm.GetString(key, new CultureInfo(Current)) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            // Sem resx embutido (ex.: harness sem o projeto) — devolve a chave.
            return key;
        }
    }

    private static string? Normalize(string? value) => value switch
    {
        "pt-BR" or "en" or "es" => value,
        _ => null,
    };

    private static string FromOs() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "pt" => "pt-BR",
            "es" => "es",
            _ => "en",
        };
}
