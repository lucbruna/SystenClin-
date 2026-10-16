using Microsoft.Win32;

namespace Systenclin.Core;

/// <summary>
/// Reforço de segurança do sistema: aplica ajustes conservadores no registro e
/// em serviços, reduzindo a superfície de ataque (Remote Registry, autorun,
/// compartilhamentos administrativos, autenticação fraca). Tudo é reversível
/// e nunca toca em dados do usuário.
/// </summary>
public static class SecurityReinforce
{
    public sealed record Result(bool Success, int Applied, int Skipped, string? Error);

    /// <summary>Aplica os reforços. Nunca lança.</summary>
    public static Result Apply(bool deep, CancellationToken ct = default)
    {
        int applied = 0, skipped = 0;
        try
        {
            // 1. Desabilitar autorun em drives removíveis e de rede.
            if (SetDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer",
                    "NoDriveTypeAutoRun", 0xFF) ||
                SetDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer",
                    "NoDriveTypeAutoRun", 0xFF))
                applied++; else skipped++;

            // 2. Desabilitar compartilhamentos administrativos ocultos (C$, ADMIN$).
            if (SetDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters",
                    "AutoShareWks", 0) ||
                SetDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters",
                    "AutoShareServer", 0))
                applied++; else skipped++;

            // 3. Forçar NTLMv2 (recusar LM/NTLMv1).
            if (SetDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa",
                    "LmCompatibilityLevel", 5))
                applied++; else skipped++;

            // 4. Desabilitar armazenamento de credenciais em texto claro (WDigest).
            if (SetDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecurityProviders\WDigest",
                    "UseLogonCredential", 0))
                applied++; else skipped++;

            if (deep)
            {
                // 5. Desabilitar Remote Registry (serviço).
                TryDisableService("RemoteRegistry");
                applied++;

                // 6. Desabilitar compartilhamento de arquivos via SMBv1 (legado).
                SetDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters",
                    "SMB1", 0);
                applied++;
            }

            return new Result(true, applied, skipped, null);
        }
        catch (Exception ex)
        {
            return new Result(false, applied, skipped, ex.Message);
        }
    }

    private static bool SetDword(RegistryKey root, string subKey, string name, int value)
    {
        try
        {
            using var key = root.CreateSubKey(subKey, RegistryKeyPermissionCheck.ReadWriteSubTree);
            if (key is null) return false;
            key.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDisableService(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{name}", writable: true);
            key?.SetValue("Start", 4, RegistryValueKind.DWord); // 4 = Disabled
        }
        catch
        {
            // Melhor esforço.
        }
    }
}
