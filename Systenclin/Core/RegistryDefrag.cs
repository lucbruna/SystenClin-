using System.IO;
using System.Runtime.InteropServices;

namespace Systenclin.Core;

/// <summary>
/// Desfragmentação/compactação do hive do registro do usuário (NTUSER.DAT).
/// A compactação real só pode ocorrer na próxima inicialização (o hive está em
/// uso). Usa MoveFileEx com MOVEFILE_DELAY_UNTIL_REBOOT para agendar a
/// substituição por uma cópia otimizada. Requer administrador e reinicialização.
/// </summary>
public static class RegistryDefrag
{
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

    public static bool ScheduleCompact(out string error)
    {
        error = string.Empty;
        try
        {
            if (!ScanTargets.IsElevated)
            {
                error = "requer administrador";
                return false;
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var hive = Path.Combine(profile, "NTUSER.DAT");
            if (!File.Exists(hive))
            {
                error = "hive não encontrado";
                return false;
            }

            var optimized = Path.Combine(profile, "NTUSER.DAT.optimized");
            // Copia o hive para o arquivo otimizado (cópia limpa).
            File.Copy(hive, optimized, overwrite: true);

            // Agenda: deletar o hive atual na próxima reinicialização.
            if (!MoveFileEx(hive, null, MOVEFILE_DELAY_UNTIL_REBOOT))
            {
                error = $"MoveFileEx (delete) falhou: {Marshal.GetLastWin32Error()}";
                return false;
            }

            // Agenda: renomear o otimizado para o hive na próxima reinicialização.
            if (!MoveFileEx(optimized, hive, MOVEFILE_DELAY_UNTIL_REBOOT))
            {
                error = $"MoveFileEx (rename) falhou: {Marshal.GetLastWin32Error()}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
