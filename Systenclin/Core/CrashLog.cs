using System.IO;
using System.Reflection;
using System.Text;

namespace Systenclin.Core;

/// <summary>
/// Grava erros fatais em arquivos de log locais (%LOCALAPPDATA%\Systenclin\crashlogs),
/// mantendo só os mais recentes. Nunca lança: falhar ao logar não pode derrubar
/// o tratamento do erro original.
/// </summary>
public static class CrashLog
{
    /// <summary>Quantos arquivos de crash ficam guardados; os mais antigos são apagados.</summary>
    public const int KeepFiles = 20;

    /// <summary>Pasta dos logs de erro do app (criada só ao gravar).</summary>
    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Systenclin", "crashlogs");

    /// <summary>
    /// Monta o texto do relatório: quando, versão do app, sistema, contexto e o
    /// erro completo (tipo, mensagem, pilha e causas encadeadas). Função pura,
    /// para a UI e os testes poderem inspecionar o conteúdo.
    /// </summary>
    public static string BuildReport(string context, Exception ex, DateTime timestamp, string appVersion)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Systenclin — relatório de erro");
        sb.AppendLine($"Data (UTC): {timestamp.ToUniversalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Versão do app: {appVersion}");
        sb.AppendLine($"Sistema: {Environment.OSVersion} · .NET {Environment.Version}");
        sb.AppendLine($"Contexto: {context}");
        sb.AppendLine();

        var depth = 0;
        for (Exception? current = ex; current is not null; current = current.InnerException, depth++)
        {
            sb.AppendLine(depth == 0 ? "Exceção:" : $"Exceção interna ({depth}):");
            sb.AppendLine($"  Tipo: {current.GetType().FullName}");
            sb.AppendLine($"  Mensagem: {current.Message}");
            sb.AppendLine("  Pilha:");
            sb.AppendLine(current.StackTrace ?? "  (sem pilha)");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Grava o relatório de um erro e devolve o caminho do arquivo; null quando
    /// não foi possível gravar (nunca lança). Depois de gravar, rotaciona:
    /// mantém só os <see cref="KeepFiles"/> arquivos mais recentes.
    /// </summary>
    public static string? Write(string context, Exception ex, string? dir = null)
    {
        try
        {
            dir ??= DefaultDir;
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var report = BuildReport(context, ex, DateTime.Now, AppVersion());
            File.WriteAllText(path, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            PruneOld(dir);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Versão do aplicativo em execução (ex.: "0.13.0.0"), ou "desconhecida".</summary>
    public static string AppVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "desconhecida";

    /// <summary>
    /// Apaga os logs mais antigos além de <paramref name="keep"/> arquivos.
    /// Devolve quantos foram apagados; falha em arquivo isolado não interrompe
    /// a limpeza dos demais.
    /// </summary>
    public static int PruneOld(string dir, int keep = KeepFiles)
    {
        var deleted = 0;

        try
        {
            if (keep < 0 || !Directory.Exists(dir))
                return 0;

            // O nome tem data/hora de largura fixa: ordenação alfabética
            // decrescente já é a ordem cronológica (mais novo primeiro).
            var old = Directory.GetFiles(dir, "crash-*.log")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .Skip(keep);

            foreach (var file in old)
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    // Arquivo travado: fica para a próxima limpeza.
                }
            }
        }
        catch
        {
            // Pasta inacessível: sem limpeza agora.
        }

        return deleted;
    }
}
