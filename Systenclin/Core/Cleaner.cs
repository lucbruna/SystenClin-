using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Systenclin.Core;

/// <summary>
/// Apaga o que foi selecionado. Três regras que não se negociam:
///
/// 1. Confere <see cref="ScanTargets.IsSafeToDelete"/> de novo, agora, na hora
///    de apagar. O disco mudou desde a varredura.
/// 2. Arquivo vai para a Lixeira (dá para restaurar). Pasta é removida de fato,
///    porque a Lixeira não aceita árvores grandes.
/// 3. Uma falha em um item não interrompe os outros. O resultado item a item é
///    registrado, inclusive o motivo da falha.
/// </summary>
public sealed class Cleaner
{
    public sealed record Result(
        CleanableItem Item,
        bool Succeeded,
        long BytesFreed,
        int FilesRemoved,
        bool Recoverable,
        string? Error);

    public sealed record Progress(int Done, int Total, long BytesFreed, string Current);

    public async Task<IReadOnlyList<Result>> CleanAsync(
        IReadOnlyList<CleanableItem> items,
        IProgress<Progress>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<Result>(items.Count);
        long freed = 0;
        int done = 0;

        try
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new Progress(done, items.Count, freed, item.Path));

                var result = await Task.Run(() => CleanOne(item, ct), ct).ConfigureAwait(false);

                results.Add(result);
                if (result.Succeeded) freed += result.BytesFreed;
                done++;
            }

            progress?.Report(new Progress(items.Count, items.Count, freed, "Concluído"));
        }
        finally
        {
            // Grava o histórico no fim — inclusive quando cancela no meio.
            try
            {
                new CleanLog().Append(results.Select(r => new CleanLog.Entry
                {
                    When = DateTimeOffset.Now,
                    Path = r.Item.Path,
                    Category = r.Item.Category.DisplayName(),
                    SizeBytes = r.Item.SizeBytes,
                    FileCount = r.Item.FileCount,
                    IsDirectory = r.Item.IsDirectory,
                    Succeeded = r.Succeeded,
                    Recoverable = r.Recoverable,
                    Error = r.Error,
                }));
            }
            catch
            {
                // Log é conveniência: não pode mascarar o resultado nem a exceção original.
            }
        }

        return results;
    }

    private static Result CleanOne(CleanableItem item, CancellationToken ct)
    {
        if (!ScanTargets.IsSafeToDelete(item.Path, out var reason))
        {
            return new Result(item, false, 0, 0, false, $"Recusado: {reason}");
        }

        try
        {
            if (item.IsDirectory)
            {
                if (!Directory.Exists(item.Path))
                    return new Result(item, true, 0, 0, false, null);

                var outcome = FileDeleter.DeleteTree(item.Path, ct);

                // Cancelamento: devolve o que já saiu, sem fingir sucesso.
                if (ct.IsCancellationRequested)
                    return new Result(item, false, outcome.BytesFreed, outcome.FilesDeleted,
                        Recoverable: false, "Cancelado — limpeza parcial");

                // Recusa de segurança (ex.: apagar através de um vínculo).
                if (outcome.Error is not null)
                    return new Result(item, false, outcome.BytesFreed, outcome.FilesDeleted,
                        Recoverable: false, outcome.Error);

                bool ok = outcome.FilesSkipped == 0 && outcome.DirsLeftBehind == 0;

                if (!ok)
                {
                    var error = $"Parcial: {outcome.FilesSkipped} arquivo(s) não puderam ser apagados";
                    if (outcome.DirsLeftBehind > 0)
                        error += $" e {outcome.DirsLeftBehind} pasta(s) ficaram";

                    return new Result(item, false, outcome.BytesFreed, outcome.FilesDeleted,
                        Recoverable: false, error);
                }

                return new Result(item, true, outcome.BytesFreed, outcome.FilesDeleted,
                    Recoverable: false, null);
            }

            if (!File.Exists(item.Path))
                return new Result(item, true, 0, 0, false, null);

            long size = 0;
            try { size = new FileInfo(item.Path).Length; } catch { }

            switch (RecycleBin.Send(item.Path))
            {
                case RecycleOutcome.Deleted:
                    // Recuperável pelo usuário.
                    return new Result(item, true, size, 1, Recoverable: true, null);

                case RecycleOutcome.Aborted:
                    // O usuário recusou a destruição definitiva: não apaga nada.
                    return new Result(item, false, 0, 0, false, "Cancelado pelo usuário");

                default:
                    // A Lixeira falhou: manter o arquivo. Apagar de vez seria
                    // irreversível e contraria a promessa feita na interface.
                    return new Result(item, false, 0, 0, Recoverable: false,
                        "não foi possível enviar para a Lixeira — o arquivo foi mantido");
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new Result(item, false, 0, 0, false, "Sem permissão (tente como administrador)");
        }
        catch (IOException ex)
        {
            return new Result(item, false, 0, 0, false, $"Em uso por outro programa ({ex.GetType().Name})");
        }
        catch (Exception ex)
        {
            return new Result(item, false, 0, 0, false, ex.Message);
        }
    }
}

/// <summary>
/// Manda arquivo para a Lixeira via SHFileOperation.
/// Sem dependência externa: é a API do próprio Windows.
/// Se o arquivo não puder ir para a Lixeira, o shell avisa antes de destruir
/// de vez (FOF_WANTNUKEWARNING), então o usuário ainda pode recusar.
/// </summary>
internal enum RecycleOutcome { Deleted, Aborted, Failed }

internal static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;
    private const int ERROR_CANCELLED = 1223;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    public static RecycleOutcome Send(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                // Duplo nulo no fim: a API exige lista terminada em \0\0.
                pFrom = path + '\0' + '\0',
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
                       | FOF_WANTNUKEWARNING,
            };

            int result = SHFileOperation(ref op);

            // Recusa do usuário: nada foi apagado.
            if (result == ERROR_CANCELLED || op.fAnyOperationsAborted)
                return RecycleOutcome.Aborted;

            if (result == 0 && !File.Exists(path))
                return RecycleOutcome.Deleted;

            return RecycleOutcome.Failed;
        }
        catch
        {
            return RecycleOutcome.Failed;
        }
    }
}
