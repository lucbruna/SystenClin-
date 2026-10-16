using System.Runtime.InteropServices;

namespace Systenclin.Core;

/// <summary>Resultado da criação do ponto de restauração. Nunca lança: falha vira Success=false com a razão em Error.</summary>
public sealed record RestorePointResult(bool Success, string? Error);

/// <summary>
/// Cria um ponto de restauração do Windows antes da limpeza, dando ao usuário
/// um caminho de volta pelo Painel de Controle. Nunca lança: qualquer falha
/// vira um <see cref="RestorePointResult"/> com a razão em português.
/// </summary>
public static class RestorePoint
{
    // Constantes da API de restauração (restorpt.h).
    private const uint BeginSystemChange = 100; // BEGIN_SYSTEM_CHANGE
    private const uint ModifySettings = 12;     // MODIFY_SETTINGS

    private const string DefaultDescription = "Systenclin — antes da limpeza";

    // O campo nativo é WCHAR[256], terminador incluso: sobram 255 para o texto.
    private const int MaxDescriptionLength = 255;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RESTOREPOINTINFO
    {
        public uint dwEventType;
        public uint dwRestorePtType;
        public long llSequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STATEMGRSTATUS
    {
        public uint nStatus;
        public long llSequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SRSetRestorePointW(ref RESTOREPOINTINFO pRestorePtSpec, out STATEMGRSTATUS pSMgrStatus);

    /// <summary>
    /// Cria o ponto de restauração com a descrição informada (ou a padrão, se
    /// vazia). Sem elevação, nem toca na API nativa: devolve o aviso direto.
    /// </summary>
    public static RestorePointResult Create(string description)
    {
        // A API nativa exige administrador; sem ele, a chamada só gastaria tempo.
        if (!ScanTargets.IsElevated)
            return new RestorePointResult(false, "Requer administrador para criar o ponto de restauração.");

        try
        {
            var spec = new RESTOREPOINTINFO
            {
                dwEventType = BeginSystemChange,
                dwRestorePtType = ModifySettings,
                llSequenceNumber = 0,
                szDescription = Sanitize(description),
            };

            var ok = SRSetRestorePointW(ref spec, out var status);

            // Sucesso de verdade exige os DOIS: retorno true e nStatus 0.
            if (ok && status.nStatus == 0)
                return new RestorePointResult(true, null);

            return new RestorePointResult(false, DescribeStatus(status.nStatus));
        }
        catch (Exception ex)
        {
            // srclient.dll ausente ou marshaling recusado: vira mensagem, nunca exceção.
            return new RestorePointResult(false, $"Não foi possível criar o ponto de restauração: {ex.Message}");
        }
    }

    /// <summary>Descrição vazia vira a padrão; maior que o campo nativo é truncada.</summary>
    private static string Sanitize(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return DefaultDescription;

        return description.Length <= MaxDescriptionLength
            ? description
            : description[..MaxDescriptionLength];
    }

    /// <summary>Traduz o nStatus devolvido pela API para a mensagem mostrada ao usuário.</summary>
    private static string DescribeStatus(uint nStatus) => nStatus switch
    {
        5 => "Acesso negado. Execute o programa como administrador.",
        1058 or 1062 => "A Proteção do Sistema está desativada. Ative em Painel de Controle → Sistema → Proteção do Sistema.",
        _ => $"Não foi possível criar o ponto de restauração (código {nStatus}).",
    };
}
