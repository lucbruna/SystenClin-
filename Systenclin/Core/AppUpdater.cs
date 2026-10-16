using System.Diagnostics;

namespace Systenclin.Core;

/// <summary>Um programa instalado com atualização disponível, como o winget o lista.</summary>
public sealed record WingetPackage(string Name, string Id, string CurrentVersion, string AvailableVersion, string Source);

/// <summary>Resultado da listagem de atualizações: pacotes, erro em PT e saída crua para a UI.</summary>
public sealed record WingetListResult(bool Success, string? Error, IReadOnlyList<WingetPackage> Packages, string? RawOutput);

/// <summary>Resultado de uma ação do winget (atualizar tudo ou um pacote): sucesso, erro em PT e saída.</summary>
public sealed record WingetActionResult(bool Success, string? Error, string? Output);

/// <summary>
/// Lista e aplica atualizações de programas instalados usando o winget
/// (Windows Package Manager). Processos sem janela e sem shell; nenhuma
/// exceção escapa: toda falha vira um resultado com mensagem em português.
/// </summary>
public static class AppUpdater
{
    // Guarda só a cauda da saída: é no fim que ficam o resumo e os erros,
    // e o corte evita texto sem limite na interface.
    private const int OutputLimit = 4000;

    private const string ListArguments =
        "upgrade --accept-source-agreements --include-unknown --disable-interactivity";

    private const string UpdateAllArguments =
        "upgrade --all --include-unknown --accept-package-agreements --accept-source-agreements --disable-interactivity";

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
    /// Lista os pacotes com atualização disponível (winget upgrade).
    /// Nunca lança; falha vira <see cref="WingetListResult"/> com Error em PT.
    /// </summary>
    public static WingetListResult ListUpgrades(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return new WingetListResult(false, "Operação cancelada.", Array.Empty<WingetPackage>(), null);

        var outcome = Run(ListArguments, ct);
        var raw = Tail(Compose(outcome.StdOut, outcome.StdErr));

        if (!outcome.Success)
            return new WingetListResult(false, outcome.Error, Array.Empty<WingetPackage>(), raw);

        IReadOnlyList<WingetPackage> packages;
        try
        {
            packages = ParseUpgrades(outcome.StdOut);
        }
        catch (Exception ex)
        {
            return new WingetListResult(
                false,
                $"Não foi possível interpretar a saída do winget: {ex.Message}",
                Array.Empty<WingetPackage>(),
                raw);
        }

        // Sem tabela parseável mas com saída: lista vazia + saída crua
        // (a UI mostra o estado vazio). Exit code diferente de zero é falha.
        return outcome.ExitCode == 0
            ? new WingetListResult(true, null, packages, raw)
            : new WingetListResult(false, $"winget retornou código {outcome.ExitCode}.", packages, raw);
    }

    /// <summary>Atualiza todos os pacotes com atualização disponível. Nunca lança.</summary>
    public static WingetActionResult UpdateAll(CancellationToken ct = default)
        => RunAction(UpdateAllArguments, ct);

    /// <summary>Atualiza um pacote pelo Id exato. Nunca lança.</summary>
    public static WingetActionResult Update(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return new WingetActionResult(false, "Identificador do pacote não informado.", null);

        // Aspas quebrariam o argumento e caracteres de controle injetariam
        // comandos: o Id vem da UI e é validado antes de virar linha de comando.
        if (id.Contains('"') || id.Any(char.IsControl))
            return new WingetActionResult(false, "Identificador do pacote inválido.", null);

        var arguments =
            $"upgrade --id \"{id}\" --exact --accept-package-agreements --accept-source-agreements --disable-interactivity";

        return RunAction(arguments, ct);
    }

    /// <summary>Executa uma ação do winget e converte exit code/saída em resultado.</summary>
    private static WingetActionResult RunAction(string arguments, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return new WingetActionResult(false, "Operação cancelada.", null);

        var outcome = Run(arguments, ct);
        var output = Tail(Compose(outcome.StdOut, outcome.StdErr));

        if (!outcome.Success)
            return new WingetActionResult(false, outcome.Error, output);

        return outcome.ExitCode == 0
            ? new WingetActionResult(true, null, output)
            : new WingetActionResult(false, $"winget retornou código {outcome.ExitCode}.", output);
    }

    // ── Execução do winget ──────────────────────────────────────────────

    /// <summary>
    /// Executa o winget sem shell e devolve a saída crua. Lê stdout+stderr
    /// antes do WaitForExit (pipe cheio travaria o filho); cancelamento mata
    /// a árvore de processos. Nunca lança: falha vira Error em PT.
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

            // Cancelar derruba a ÁRVORE do winget: um instalador em andamento
            // não pode continuar depois que o chamador desistiu.
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
    // O cabeçalho muda com o idioma, mas a linha de hífens não: os runs de
    // "-" dão o início e a largura de cada coluna, independente de texto.

    /// <summary>
    /// Fatia as linhas da tabela do winget nas colunas dadas pelos runs de
    /// hífens do separador. Linha curta demais ou tabela com menos de quatro
    /// colunas são puladas. Nunca lança por si só (falhas viram lista vazia).
    /// </summary>
    private static IReadOnlyList<WingetPackage> ParseUpgrades(string output)
    {
        var packages = new List<WingetPackage>();
        if (string.IsNullOrEmpty(output))
            return packages;

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
            return packages;

        var columns = FindColumns(lines[separator].TrimEnd('\r'));
        if (columns.Count < 4)
            return packages;

        // A linha seguinte ao separador é o cabeçalho (ignorada); os pacotes vêm depois.
        for (var i = separator + 2; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim().Length == 0)
                continue;

            var fields = SliceColumns(line, columns);
            if (fields.Count < 4)
                continue;

            var name = fields[0];
            var id = fields[1];
            if (name.Length == 0 || id.Length == 0)
                continue;

            var current = fields[2];
            var available = fields.Count > 3 ? fields[3] : string.Empty;
            var source = fields.Count > 4 ? fields[4] : string.Empty;

            packages.Add(new WingetPackage(name, id, current, available, source));
        }

        return packages;
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
}
