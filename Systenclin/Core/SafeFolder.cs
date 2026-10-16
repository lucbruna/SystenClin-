using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Systenclin.Core;

/// <summary>
/// Cria uma pasta protegida (Safe Folder) com ACLs restritivas: apenas o
/// usuário atual tem acesso total; outros usuários e processos não elevados
/// são bloqueados. Ajuda a proteger dados sensíveis contra acesso não autorizado.
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

            var allowRule = new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);

            security.AddAccessRule(allowRule);
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
            if (!Directory.Exists(DefaultPath))
            {
                if (!Create(out error, out _)) return false;
            }
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
