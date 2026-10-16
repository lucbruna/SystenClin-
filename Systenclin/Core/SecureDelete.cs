using System.IO;
using System.Security.Cryptography;

namespace Systenclin.Core;

/// <summary>
/// Destruição segura de arquivos (shredding): sobrescreve o conteúdo N vezes
/// (zeros, uns e dados aleatórios) antes de apagar, dificultando a recuperação
/// por ferramentas forenses. Segue o padrão DoD 5220.22-M (3 passagens).
/// </summary>
public static class SecureDelete
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Destrói o arquivo em <paramref name="path"/>. Devolve true em sucesso.
    /// Nunca lança: falha vira false com motivo em <paramref name="error"/>.
    /// </summary>
    public static bool Shred(string path, int passes, out string error, CancellationToken ct = default)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            error = "arquivo não encontrado";
            return false;
        }

        if (passes < 1) passes = 1;
        if (passes > 7) passes = 7;

        try
        {
            var info = new FileInfo(path);
            long length = info.Length;

            // Passagens: zeros, uns, aleatório (repete conforme passes).
            var patterns = new byte?[] { 0x00, 0xFF, null };
            for (int p = 0; p < passes; p++)
            {
                ct.ThrowIfCancellationRequested();

                byte? pattern = patterns[p % patterns.Length];
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    fs.SetLength(length);
                    fs.Position = 0;

                    var buffer = new byte[BufferSize];
                    if (pattern.HasValue)
                    {
                        Array.Fill(buffer, pattern.Value);
                    }

                    long remaining = length;
                    while (remaining > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        int chunk = (int)Math.Min(buffer.Length, remaining);
                        if (!pattern.HasValue)
                        {
                            RandomNumberGenerator.Fill(buffer.AsSpan(0, chunk));
                        }
                        fs.Write(buffer, 0, chunk);
                        remaining -= chunk;
                    }

                    fs.Flush(flushToDisk: true);
                }
            }

            // Renomeia para dificultar recuperação do nome.
            try
            {
                var dir = Path.GetDirectoryName(path)!;
                var tempName = Path.Combine(dir, Guid.NewGuid().ToString("N"));
                File.Move(path, tempName);
                path = tempName;
            }
            catch
            {
                // Renomear é melhor esforço.
            }

            File.Delete(path);
            return true;
        }
        catch (OperationCanceledException)
        {
            error = "cancelado";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
