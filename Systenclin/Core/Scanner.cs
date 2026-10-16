using System.IO;

namespace Systenclin.Core;

/// <summary>
/// Percorre os alvos e devolve o que dá para limpar, com progresso.
/// Somente leitura: não apaga nada.
/// </summary>
public sealed class Scanner
{
    public sealed record Progress(
        string CurrentTarget,
        int TargetsDone,
        int TargetsTotal,
        long BytesFound,
        int ItemsFound);

    public async Task<IReadOnlyList<CleanableItem>> ScanAsync(
        IProgress<Progress>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<CleanableItem>();

        var targets = ScanTargets.All()
            .Where(t => Directory.Exists(t.Path) || File.Exists(t.Path) || t.Path.Contains('*'))
            .ToList();

        long bytesFound = 0;
        int done = 0;

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new Progress(target.Path, done, targets.Count, bytesFound, results.Count));

            // Alvos do sistema (ex.: C:\Windows\Temp) só fazem sentido elevado.
            if (target.RequiresAdmin && !ScanTargets.IsElevated)
            {
                progress?.Report(new Progress(
                    $"Ignorado (exige administrador): {target.Path}", done, targets.Count, bytesFound, results.Count));
                done++;
                continue;
            }

            if (target.Path.Contains('*'))
            {
                // Alvo por padrão de arquivo (curinga). A enumeração roda fora
                // da thread chamadora para não travar a interface.
                var found = await Task.Run(() => ScanPattern(target, ct).ToList(), ct).ConfigureAwait(false);

                foreach (var item in found)
                {
                    results.Add(item);
                    bytesFound += item.SizeBytes;
                }
            }
            else if (Directory.Exists(target.Path))
            {
                // A checagem de segurança vem ANTES de medir: pasta proibida
                // não deve nem ser percorrida nem contar bytes.
                if (!ScanTargets.IsSafeToDelete(target.Path, out var reason))
                {
                    progress?.Report(new Progress(
                        $"Ignorado ({reason}): {target.Path}", done, targets.Count, bytesFound, results.Count));
                    done++;
                    continue;
                }

                var measured = await Task.Run(
                    () => DirectoryMeasurer.Measure(target.Path, ct), ct).ConfigureAwait(false);

                if (measured.FileCount == 0) { done++; continue; }

                results.Add(new CleanableItem
                {
                    Path = target.Path,
                    Category = target.Category,
                    SizeBytes = measured.SizeBytes,
                    IsDirectory = true,
                    FileCount = measured.FileCount,
                    Risk = target.Risk,
                    Note = target.Note,
                });

                bytesFound += measured.SizeBytes;
            }

            done++;
        }

        progress?.Report(new Progress("Concluído", targets.Count, targets.Count, bytesFound, results.Count));

        return results
            .OrderBy(r => r.Category)
            .ThenByDescending(r => r.SizeBytes)
            .ToList();
    }

    private static IEnumerable<CleanableItem> ScanPattern(ScanTargets.Target target, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(target.Path);
        var pattern = Path.GetFileName(target.Path);

        if (dir is null || !Directory.Exists(dir)) yield break;

        IEnumerator<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).GetEnumerator();
        }
        catch
        {
            yield break;
        }

        using (files)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // MoveNext/Current podem falhar no MEIO da enumeração (pasta
                // removida, acesso negado etc.); nesse caso encerra sem erro.
                // O try/catch cobre só a enumeração: yield return não é
                // permitido dentro de try/catch.
                bool hasNext;
                string file;
                try
                {
                    hasNext = files.MoveNext();
                    file = hasNext ? files.Current : string.Empty;
                }
                catch
                {
                    hasNext = false;
                    file = string.Empty;
                }

                if (!hasNext) yield break;

                long size = 0;
                try { size = new FileInfo(file).Length; }
                catch { continue; }

                if (size == 0) continue;

                // Mesma checagem de segurança dos diretórios. Um arquivo pode cair
                // em área proibida mesmo quando o padrão foi montado a partir de
                // uma pasta permitida.
                if (!ScanTargets.IsSafeToDelete(file, out _)) continue;

                yield return new CleanableItem
                {
                    Path = file,
                    Category = target.Category,
                    SizeBytes = size,
                    IsDirectory = false,
                    FileCount = 1,
                    Risk = target.Risk,
                    Note = target.Note,
                };
            }
        }
    }
}
