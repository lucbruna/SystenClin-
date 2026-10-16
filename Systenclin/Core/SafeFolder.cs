using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Systenclin.Core;

/// <summary>
/// Cria uma pasta protegida (Safe Folder) com ACLs restritivas: apenas o
/// usuário atual, SYSTEM e Administradores têm acesso total; outros usuários
/// e processos não elevados são bloqueados. Ajuda a proteger dados sensíveis.
/// </summary>
public static class SafeFolder
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Systenclin Safe Folder");

    public static bool Create(out string error, out string path)
    {
        error = string.Empty;
        path = DefaultPath;
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);

            var dirInfo = new DirectoryInfo(path);
            var security = dirInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // Remove regras existentes.
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(NTAccount)))
            {
                security.RemoveAccessRule(rule);
            }

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null)
            {
                error = "não foi possível identificar o usuário atual";
                return false;
            }

            // Usuário atual, SYSTEM e Administradores. Sem SYSTEM/Administradores,
            // backup, indexação e manutenção da pasta falham.
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                inherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                inherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                inherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirInfo.SetAccessControl(security);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Open(out string error)
    {
        error = string.Empty;
        try
        {
            // Reaplica a política de ACLs também em pasta existente
            // (corrige pastas criadas antes desta correção).
            if (!Create(out error, out _)) return false;
            Process.Start(new ProcessStartInfo
            {
                FileName = DefaultPath,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
