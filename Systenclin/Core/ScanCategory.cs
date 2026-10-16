namespace Systenclin.Core;

/// <summary>O que uma varredura pode encontrar. A ordem define a ordem de exibição.</summary>
public enum ScanCategory
{
    /// <summary>%TEMP%, %LOCALAPPDATA%\Temp e equivalentes.</summary>
    TemporaryFiles = 0,

    /// <summary>Cache e relatórios do Windows Update.</summary>
    WindowsUpdateCache = 1,

    /// <summary>Miniaturas, cache de ícones e fontes.</summary>
    ThumbnailCache = 2,

    /// <summary>Relatórios de erro e despejos de memória (crash dumps).</summary>
    ErrorReports = 3,

    /// <summary>Cache de instaladores de pacotes (NuGet, npm, pip, etc.).</summary>
    PackageCache = 4,

    /// <summary>Logs antigos do Windows e de aplicativos.</summary>
    LogFiles = 5,

    /// <summary>Lixeira.</summary>
    RecycleBin = 6,
}

public static class ScanCategoryExtensions
{
    public static string DisplayName(this ScanCategory c) => c switch
    {
        ScanCategory.TemporaryFiles => "Arquivos temporários",
        ScanCategory.WindowsUpdateCache => "Cache do Windows Update",
        ScanCategory.ThumbnailCache => "Cache de miniaturas",
        ScanCategory.ErrorReports => "Relatórios de erro e despejos",
        ScanCategory.PackageCache => "Cache de instaladores",
        ScanCategory.LogFiles => "Logs antigos",
        ScanCategory.RecycleBin => "Lixeira",
        _ => c.ToString(),
    };

    public static string Explanation(this ScanCategory c) => c switch
    {
        ScanCategory.TemporaryFiles => "Arquivos que programas deixam para trás ao rodar. Seguros de apagar; o Windows recria o que precisar.",
        ScanCategory.WindowsUpdateCache => "Cópias dos pacotes de atualização já instalados. Apagar libera espaço; se precisar reinstalar uma atualização, o Windows baixa de novo.",
        ScanCategory.ThumbnailCache => "Miniaturas de imagens e vídeos. Apagar não perde nada; a próxima vez que abrir a pasta o Windows gera de novo.",
        ScanCategory.ErrorReports => "Relatórios e despejos de programas que travaram. Úteis só para depurar; se você não faz isso, pode apagar.",
        ScanCategory.PackageCache => "Cópias de pacotes baixados por gerenciadores (NuGet, npm, pip). Apagar deixa a próxima instalação mais lenta, mas nada quebra.",
        ScanCategory.LogFiles => "Arquivos de log com mais de 30 dias. Servem para investigar problemas passados.",
        ScanCategory.RecycleBin => "Arquivos que você já mandou para a lixeira. Apagar aqui é definitivo.",
        _ => string.Empty,
    };

    /// <summary>Marcado por padrão numa varredura inicial? A lixeira não é.</summary>
    public static bool DefaultChecked(this ScanCategory c) => c != ScanCategory.RecycleBin;
}
