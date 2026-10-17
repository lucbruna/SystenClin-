using System.Diagnostics;

namespace Systenclin.Core;

/// <summary>Um programa instalado com atualização disponível, como o winget o lista.</summary>
public sealed record WingetPackage(string Name, string Id, string CurrentVersion, string AvailableVersion, string Source);

/// <summary>Resultado da listagem de atualizações: pacotes, erro em PT e saída crua para a UI.</summary>
public sealed record WingetListResult(bool Success, string? Error, IReadOnlyList<WingetPackage> Packages, string? RawOutput);

/// <summary>Resultado de uma ação do winget (atualizar tudo ou um pacote): sucesso, erro em PT e saída.</summary>
public sealed record WingetActionResult(bool Success, string? Error, string? Output, int ExitCode = 0)
{
    /// <summary>
    /// "Sem atualização aplicável" (0x8A15002B): existe versão nova, mas ela não
    /// se aplica a este sistema (escopo, arquitetura ou idioma). Não é falha real.
    /// </summary>
    public bool NotApplicable =>
        !Success && (ExitCode == unchecked((int)0x8A15002B)
            || Output?.Contains("No applicable upgrade", StringComparison.OrdinalIgnoreCase) == true);
}

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
            : new WingetListResult(false, $"winget retornou código 0x{outcome.ExitCode:X8}.", packages, raw);
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

        // --include-unknown alinha com a listagem: pacotes cuja versão instalada
        // não foi detectada também entram na atualização (mesma flag do UpdateAll).
        var arguments =
            $"upgrade --id \"{id}\" --exact --include-unknown --accept-package-agreements --accept-source-agreements --disable-interactivity";

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

        // Exit codes do winget são HRESULTs: mostrados em hex, como na documentação.
        return outcome.ExitCode == 0
            ? new WingetActionResult(true, null, output, 0)
            : new WingetActionResult(false, $"winget retornou código 0x{outcome.ExitCode:X8}.", output, outcome.ExitCode);
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
    // A tabela mudou: o cabeçalho é localizado (pt-BR/en) e, nas versões
    // recentes, a linha de hífens vem como um ÚNICO run contínuo, sem espaço
    // entre as colunas — o que quebra qualquer corte por posição. Por isso o
    // parsing é por ESTRUTURA: da direita para a esquerda, o último token é a
    // Origem, o penúltimo a Disponível, antes vem a Versão (aceita "< versão"),
    // o ID (um token sem espaços) e o resto é o Nome (que pode ter espaços).

    /// <summary>
    /// Converte a saída de <c>winget upgrade</c> em pacotes. Acha a linha
    /// separadora de hífens e lê a partir da linha seguinte (o cabeçalho fica
    /// ANTES do separador). Nunca lança: linha estranha é apenas ignorada.
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

        // Os pacotes começam logo depois do separador. Sem separador (formato
        // inesperado), tenta a partir da segunda linha, pulando o cabeçalho.
        var start = separator >= 0 ? separator + 1 : 1;

        for (var i = start; i < lines.Length; i++)
        {
            if (TryParseRow(lines[i].TrimEnd('\r'), out var package))
                packages.Add(package);
        }

        return packages;
    }

    /// <summary>
    /// Lê uma linha de dados da direita para a esquerda: Origem, Disponível,
    /// Versão (aceita o prefixo "&lt; "), ID (um token) e Nome (o restante).
    /// Linhas curtas, o cabeçalho e o resumo não viram pacote. Nunca lança.
    /// </summary>
    private static bool TryParseRow(string line, out WingetPackage package)
    {
        package = null!;

        if (line.Trim().Length == 0 || IsSeparatorLine(line))
            return false;

        // Divide por espaços em branco: o Nome (com espaços) fica preservado.
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 5)
            return false; // mínimo: Nome, ID, Versão, Disponível, Origem

        var last = tokens.Length - 1;
        var source = tokens[last];
        var available = tokens[last - 1];

        var cursor = last - 2;
        string current;

        // A versão pode vir como "< 2026.3.1": o "<" é o token ANTERIOR ao
        // número (dois tokens), então consome ambos.
        if (tokens[cursor - 1] == "<")
        {
            current = $"< {tokens[cursor]}";
            cursor -= 2;
        }
        else
        {
            current = tokens[cursor];
            cursor -= 1;
        }

        // Precisa sobrar pelo menos Nome (1 token) + ID (1 token).
        if (cursor < 1)
            return false;

        var id = tokens[cursor];
        var name = string.Join(' ', tokens, 0, cursor);

        // Cabeçalho localizado: o rótulo da coluna ID é "ID" em pt-BR e inglês.
        if (id.Equals("ID", StringComparison.OrdinalIgnoreCase))
            return false;

        if (name.Length == 0 || id.Length == 0 || source.Length == 0)
            return false;

        package = new WingetPackage(name, id, current, available, source);
        return true;
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

    // ── Saída ───────────────────────────────────────────────────────────

    /// <summary>Junta stdout e stderr para exibição, sem limite de tamanho.</summary>
    private static string Compose(string stdout, string stderr)
        => stderr.Length == 0 ? stdout : stdout + Environment.NewLine + stderr;

    /// <summary>Cauda da saída até <see cref="OutputLimit"/>; vazia vira null para a UI.</summary>
    private static string? Tail(string text)
        => string.IsNullOrEmpty(text) ? null : text.Length > OutputLimit ? text[^OutputLimit..] : text;

    /// <summary>
    /// Primeira linha da saída com cara de erro (en/pt), para resumir na UI sem
    /// despejar o log inteiro. Null quando não há nada com essa cara.
    /// </summary>
    public static string? FirstErrorLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("erro", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("falh", StringComparison.OrdinalIgnoreCase))
            {
                return line.Length > 160 ? line[..160] : line;
            }
        }

        return null;
    }
}
