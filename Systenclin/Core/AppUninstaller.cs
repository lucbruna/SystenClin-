using System.Diagnostics;

namespace Systenclin.Core;

/// <summary>Um programa instalado neste PC, como o winget o lista.</summary>
public sealed record InstalledApp(string Id, string Name, string Version);

/// <summary>
/// Lista e remove programas instalados usando o winget (Windows Package
/// Manager). Processos sem janela e sem shell; a listagem nunca lança (falha
/// vira lista vazia) e a remoção só deixa escapar o cancelamento (OCE).
/// </summary>
public static class AppUninstaller
{
    // Guarda só a cauda da saída: é no fim que ficam o resumo e os erros.
    private const int OutputLimit = 4000;

    // Limite da espera pela desinstalação: instaladores lentos são comuns,
    // mas a interface não pode ficar presa para sempre.
    private static readonly TimeSpan UninstallTimeout = TimeSpan.FromMinutes(10);

    private const string ListArguments =
        "list --accept-source-agreements --disable-interactivity";

    // Lazy default é thread-safe (ExecutionAndPublication) e testa UMA vez.
    private static readonly Lazy<bool> Available = new(ProbeWinget);

    /// <summary>O winget existe e responde neste sistema? Resultado cacheado.</summary>
    public static bool IsAvailable => Available.Value;

    /// <summary>
    /// Sonda o winget com --version: exit code 0 significa disponível.
    /// Qualquer exceção vira false — nunca lança.
    /// </summary>
    private static bool ProbeWinget()
    {
        try
        {
            var outcome = Run("--version", CancellationToken.None);
            return outcome.Success && outcome.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ── API pública ─────────────────────────────────────────────────────

    /// <summary>
    /// Lista os programas instalados (winget list), ordenados por nome.
    /// Nunca lança, exceto cancelamento (OCE); falha vira lista vazia.
    /// </summary>
    public static async Task<IReadOnlyList<InstalledApp>> ListInstalledAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        progress?.Report("Consultando os programas instalados…");

        RunOutcome outcome;
        try
        {
            outcome = await Task.Run(() => Run(ListArguments, ct));
        }
        catch
        {
            return Array.Empty<InstalledApp>();
        }

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        if (!outcome.Success)
            return Array.Empty<InstalledApp>();

        IReadOnlyList<InstalledApp> apps;
        try
        {
            apps = ParseInstalled(outcome.StdOut);
        }
        catch
        {
            return Array.Empty<InstalledApp>();
        }

        return apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Remove um programa pelo Id exato (winget uninstall). Nunca lança, exceto
    /// cancelamento (OCE). O cancelamento encerra apenas a ESPERA: o winget
    /// NÃO é morto — ele segue rodando e conclui sozinho, como se o app
    /// tivesse fechado depois de disparar a remoção.
    /// </summary>
    public static async Task<ProcessActionResult> UninstallAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            return new ProcessActionResult(false, "Identificador do programa não informado.");

        // Mesma defesa do AppUpdater: o Id vem da UI e caracteres de controle
        // não têm lugar em uma linha de comando.
        if (id.Contains('"') || id.Any(char.IsControl))
            return new ProcessActionResult(false, "Identificador do programa inválido.");

        var info = new ProcessStartInfo
        {
            FileName = "winget",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };

        // ArgumentList monta a linha sem aspas manuais; sem --silent de
        // propósito: o comportamento padrão do winget (com as telas do
        // instalador, quando houver) é exatamente o desejado aqui.
        info.ArgumentList.Add("uninstall");
        info.ArgumentList.Add("--id");
        info.ArgumentList.Add(id);
        info.ArgumentList.Add("--exact");
        info.ArgumentList.Add("--accept-source-agreements");
        info.ArgumentList.Add("--disable-interactivity");

        Process proc;
        try
        {
            proc = new Process { StartInfo = info };
            proc.Start();
        }
        catch (Exception ex)
        {
            return new ProcessActionResult(false, $"Não foi possível executar o winget: {ex.Message}");
        }

        // Lê stdout+stderr antes do WaitForExit (pipe cheio travaria o filho).
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        // A espera é limitada e cancelável, mas o winget NÃO é morto: mesmo
        // que o app feche no meio, ele termina a desinstalação sozinho.
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(UninstallTimeout);

            try
            {
                await proc.WaitForExitAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new ProcessActionResult(
                    false,
                    "O winget demorou mais de 10 minutos; a operação foi abandonada e pode continuar em segundo plano.");
            }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var exitCode = proc.ExitCode;
        proc.Dispose();

        if (exitCode == 0)
            return new ProcessActionResult(true, null);

        // Falha: mensagem em PT com as últimas linhas úteis de stderr/stdout.
        var detail = LastRelevantLines(Tail(Compose(stdout, stderr)));
        return new ProcessActionResult(false, detail is null
            ? $"winget retornou código {exitCode}."
            : $"winget retornou código {exitCode} — {detail}");
    }

    // ── Execução do winget ──────────────────────────────────────────────

    /// <summary>
    /// Executa o winget sem shell e devolve a saída crua. Lê stdout+stderr
    /// antes do WaitForExit (pipe cheio travaria o filho); cancelamento mata
    /// a árvore de processos (vale só para a listagem). Nunca lança.
    /// </summary>
    private static RunOutcome Run(string arguments, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };

            using var proc = new Process { StartInfo = info };

            proc.Start();

            using var registration = ct.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            });

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();

            proc.WaitForExit();

            if (ct.IsCancellationRequested)
                return new RunOutcome(false, -1, stdout, stderr, "Operação cancelada.");

            return new RunOutcome(true, proc.ExitCode, stdout, stderr, null);
        }
        catch (Exception ex)
        {
            return new RunOutcome(false, -1, string.Empty, string.Empty, $"Não foi possível executar o winget: {ex.Message}");
        }
    }

    /// <summary>Resultado cru de uma execução do winget; Error só existe em falha/cancelamento.</summary>
    private readonly record struct RunOutcome(bool Success, int ExitCode, string StdOut, string StdErr, string? Error);

    // ── Parsing da tabela do winget ─────────────────────────────────────
    // Mesmo esquema do AppUpdater: o cabeçalho muda com o idioma, mas a linha
    // de hífens não — os runs de "-" dão o início e a largura de cada coluna.
    // A duplicação entre os dois parsers é intencional, como no restante do
    // projeto (ex.: a reciclagem duplicada em ShortcutFixer/LargeFileFinder).

    /// <summary>
    /// Fatia as linhas da tabela de "winget list" (Nome, Id, Versão, ...) nas
    /// colunas dadas pelos runs de hífens do separador. Linha curta demais ou
    /// tabela com menos de três colunas são puladas; linhas sem Id são
    /// ignoradas. Nunca lança por si só (falhas viram lista vazia).
    /// </summary>
    private static IReadOnlyList<InstalledApp> ParseInstalled(string output)
    {
        var apps = new List<InstalledApp>();
        if (string.IsNullOrEmpty(output))
            return apps;

        var lines = output.Split('\n');

        // Localiza o separador: linha só com espaços e hífens.
        var separator = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsSeparatorLine(lines[i].TrimEnd('\r')))
            {
                separator = i;
                break;
            }
        }

        if (separator < 0)
            return apps;

        var columns = FindColumns(lines[separator].TrimEnd('\r'));
        if (columns.Count < 3)
            return apps;

        // A linha seguinte ao separador é o cabeçalho (ignorada); os programas vêm depois.
        for (var i = separator + 2; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim().Length == 0)
                continue;

            var fields = SliceColumns(line, columns);
            if (fields.Count < 3)
                continue;

            var name = fields[0];
            var id = fields[1];
            if (name.Length == 0 || id.Length == 0)
                continue;

            apps.Add(new InstalledApp(id, name, fields[2]));
        }

        return apps;
    }

    /// <summary>Linha composta só de espaços e hífens, com pelo menos um hífen.</summary>
    private static bool IsSeparatorLine(string line)
    {
        var hasDash = false;

        foreach (var c in line)
        {
            if (c == '-')
            {
                hasDash = true;
            }
            else if (c != ' ')
            {
                return false;
            }
        }

        return hasDash;
    }

    /// <summary>
    /// Cada run de hífens do separador dá o início e a largura de uma coluna.
    /// </summary>
    private static List<(int Start, int Width)> FindColumns(string separator)
    {
        var columns = new List<(int Start, int Width)>();

        var i = 0;
        while (i < separator.Length)
        {
            if (separator[i] != '-')
            {
                i++;
                continue;
            }

            var start = i;
            while (i < separator.Length && separator[i] == '-')
                i++;

            columns.Add((start, i - start));
        }

        return columns;
    }

    /// <summary>
    /// Fatia a linha pelas colunas: campos além do fim da linha são omitidos
    /// e cada campo é aparado. Linha curta resulta em menos campos.
    /// </summary>
    private static List<string> SliceColumns(string line, List<(int Start, int Width)> columns)
    {
        var fields = new List<string>(columns.Count);

        foreach (var (start, width) in columns)
        {
            if (start >= line.Length)
                break;

            var length = Math.Min(width, line.Length - start);
            fields.Add(line.Substring(start, length).Trim());
        }

        return fields;
    }

    // ── Saída ───────────────────────────────────────────────────────────

    /// <summary>Junta stdout e stderr para exibição, sem limite de tamanho.</summary>
    private static string Compose(string stdout, string stderr)
        => stderr.Length == 0 ? stdout : stdout + Environment.NewLine + stderr;

    /// <summary>Cauda da saída até <see cref="OutputLimit"/>; vazia vira null para a UI.</summary>
    private static string? Tail(string text)
        => string.IsNullOrEmpty(text) ? null : text.Length > OutputLimit ? text[^OutputLimit..] : text;

    /// <summary>As últimas linhas não vazias da saída, espremidas numa frase; null se não há nada.</summary>
    private static string? LastRelevantLines(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var lines = output
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .TakeLast(3)
            .ToList();

        return lines.Count == 0 ? null : string.Join(" ", lines);
    }
}
