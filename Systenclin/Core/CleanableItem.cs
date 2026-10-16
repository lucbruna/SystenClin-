namespace Systenclin.Core;

/// <summary>
/// Um arquivo ou pasta candidato à limpeza.
/// Imutável: a varredura produz, a UI marca, o limpador consome.
/// </summary>
public sealed class CleanableItem
{
    public required string Path { get; init; }
    public required ScanCategory Category { get; init; }

    /// <summary>Tamanho em bytes. Para pastas, é o total somado.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>True quando o alvo é um diretório (apaga a árvore inteira).</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Quantidade de arquivos dentro, quando é diretório.</summary>
    public int FileCount { get; init; }

    /// <summary>
    /// Risco de apagar. Itens de risco alto ficam desmarcados e pedem confirmação
    /// explícita do usuário.
    /// </summary>
    public RiskLevel Risk { get; init; } = RiskLevel.Low;

    /// <summary>Por que este item foi incluído. Vai para o log e para o tooltip.</summary>
    public string? Note { get; init; }

    public string SizeDisplay => FormatBytes(SizeBytes);

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes} B";

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = -1;

        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < units.Length - 1);

        return value >= 100
            ? $"{value:F0} {units[unit]}"
            : $"{value:F1} {units[unit]}";
    }
}

public enum RiskLevel
{
    /// <summary>Apagar não perde nada. Cache, miniaturas, temporários.</summary>
    Low = 0,

    /// <summary>Apagar tem consequência pequena e reversível (rebaixa algo, perde histórico).</summary>
    Medium = 1,

    /// <summary>Apagar é irreversível ou pode afetar outro programa. Nunca marcado por padrão.</summary>
    High = 2,
}
