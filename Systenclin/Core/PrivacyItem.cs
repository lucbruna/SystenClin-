using System.IO;
using Microsoft.Data.Sqlite;

namespace Systenclin.Core;

/// <summary>Que tipo de dado de privacidade. A ordem define a exibição.</summary>
public enum PrivacyKind
{
    BrowsingHistory = 0,
    Cookies = 1,
    FormAutofill = 2,
    DownloadHistory = 3,
    SessionTabs = 4,
    BrowserCache = 5,
    SitePermissions = 6,
}

public static class PrivacyKindExtensions
{
    public static string DisplayName(this PrivacyKind k) => k switch
    {
        PrivacyKind.BrowsingHistory => "Histórico de navegação",
        PrivacyKind.Cookies => "Cookies",
        PrivacyKind.FormAutofill => "Formulários preenchidos",
        PrivacyKind.DownloadHistory => "Histórico de downloads",
        PrivacyKind.SessionTabs => "Abas salvas da sessão",
        PrivacyKind.BrowserCache => "Cache do navegador",
        PrivacyKind.SitePermissions => "Permissões de site",
        _ => k.ToString(),
    };

    public static string Explanation(this PrivacyKind k) => k switch
    {
        PrivacyKind.BrowsingHistory => "Endereços e títulos de páginas que você visitou, com data e contagem de visitas.",
        PrivacyKind.Cookies => "Pequenos arquivos que sites gravam para lembrar você. Apagar desconecta de todos os sites e pode remover carrinhos de compra.",
        PrivacyKind.FormAutofill => "O que você digitou em formulários e deixou o navegador lembrar — nomes, endereços, e-mails.",
        PrivacyKind.DownloadHistory => "Lista de arquivos baixados. Apagar não remove os arquivos, só o registro.",
        PrivacyKind.SessionTabs => "As abas que o navegador restauraria ao abrir. Apagar perde essa lista; os favoritos continuam.",
        PrivacyKind.BrowserCache => "Cópias de páginas e imagens para abrir mais rápido. Apagar não perde nada; o primeiro acesso fica mais lento.",
        PrivacyKind.SitePermissions => "Decisões que você tomou por site (câmera, microfone, notificações). Apagar faz o navegador perguntar de novo.",
        _ => string.Empty,
    };

    /// <summary>
    /// Cookies e permissões são destrutivos de verdade: desconectam contas e
    /// apagam escolhas. Nunca vêm marcados por padrão.
    /// </summary>
    public static RiskLevel Risk(this PrivacyKind k) => k switch
    {
        PrivacyKind.BrowsingHistory => RiskLevel.Low,
        PrivacyKind.Cookies => RiskLevel.High,
        PrivacyKind.FormAutofill => RiskLevel.Medium,
        PrivacyKind.DownloadHistory => RiskLevel.Low,
        PrivacyKind.SessionTabs => RiskLevel.Medium,
        PrivacyKind.BrowserCache => RiskLevel.Low,
        PrivacyKind.SitePermissions => RiskLevel.Medium,
        _ => RiskLevel.Medium,
    };

    public static bool DefaultChecked(this PrivacyKind k) => k switch
    {
        PrivacyKind.BrowsingHistory => true,
        PrivacyKind.Cookies => false,       // desconecta de tudo
        PrivacyKind.FormAutofill => false,  // apaga dado que o usuário digitou
        PrivacyKind.DownloadHistory => true,
        PrivacyKind.SessionTabs => false,
        PrivacyKind.BrowserCache => true,
        PrivacyKind.SitePermissions => false,
        _ => false,
    };
}

/// <summary>
/// Um dado de privacidade encontrado, com o que ele contém.
///
/// A diferença em relação ao v0.1: aqui não basta medir bytes. O usuário precisa
/// VER o que vai apagar — quais sites, quais cookies, quais abas. Um item de
/// privacidade sem prévia é um item que o usuário apaga às cegas.
/// </summary>
public sealed class PrivacyItem
{
    public required PrivacyKind Kind { get; init; }
    public required BrowserProfile Profile { get; init; }

    /// <summary>Registros que serão removidos.</summary>
    public int RecordCount { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>Amostra do conteúdo: até 8 linhas legíveis.</summary>
    public IReadOnlyList<string> Preview { get; init; } = [];

    /// <summary>Quantos registros distintos existem no total (para "mostrando 8 de 4017").</summary>
    public int PreviewOfTotal { get; init; }

    /// <summary>Por que não deu para ler, quando for o caso.</summary>
    public string? BlockedReason { get; init; }

    public RiskLevel Risk => Kind.Risk();
    public string SizeDisplay => CleanableItem.FormatBytes(SizeBytes);
    public string Title => $"{Kind.DisplayName()} — {Profile.Display}";
}
