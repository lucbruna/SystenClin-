using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Systenclin.Core;

/// <summary>Um adaptador de rede visível ao Windows: nome, tipo, estado, velocidade e IPv4.</summary>
public sealed record NetworkAdapterInfo(string Name, string Type, string Status, string Speed, string? Ipv4);

/// <summary>
/// Informações e ações de rede conservadoras: lista adaptadores e, só com
/// privilégio de administrador, renova o endereço IP e limpa o cache ARP.
/// Fica de fora qualquer ajuste de TCP/Nagle/registro, reset de winsock ou
/// algo que exija reinicialização. Nenhum método público lança.
/// </summary>
public static class NetworkInfo
{
    /// <summary>
    /// Lista os adaptadores físicos e virtuais úteis (ignora Loopback e túneis).
    /// Conectados primeiro e, dentro de cada grupo, ordem alfabética por nome.
    /// </summary>
    public static IReadOnlyList<NetworkAdapterInfo> List()
    {
        try
        {
            var adapters = new List<NetworkAdapterInfo>();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel)
                    continue;

                // Os filtros de rede do Windows aparecem como interfaces próprias
                // e terminam em "-0000" (ex.: "...-QoS Packet Scheduler-0000",
                // "...-WFP Native MAC Layer LightWeight Filter-0000").
                if (ni.Name.EndsWith("-0000", StringComparison.Ordinal))
                    continue;

                // Sem endereço unicast e (desligada ou sem endereço físico) é
                // interface fantasma (WAN Miniport, vSwitch, dispositivo ausente).
                // Adaptadores reais desligados, mas com MAC, permanecem na lista.
                if (!HasNonLoopbackUnicast(ni)
                    && (ni.OperationalStatus != OperationalStatus.Up || !HasPhysicalAddress(ni)))
                    continue;

                adapters.Add(new NetworkAdapterInfo(
                    ni.Name,
                    FriendlyType(ni),
                    ni.OperationalStatus == OperationalStatus.Up ? "Conectado" : "Desconectado",
                    FormatSpeed(ni.Speed),
                    FirstIpv4(ni)));
            }

            return adapters
                .OrderByDescending(a => a.Status == "Conectado")
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Renova o endereço IP com <c>ipconfig /renew</c> (oculto). Requer
    /// administrador. Sucesso = código de saída 0. Cancela matando o processo.
    /// </summary>
    public static async Task<ProcessActionResult> RenewIpAsync(CancellationToken ct = default)
    {
        if (!ScanTargets.IsElevated)
            return new ProcessActionResult(false, "Requer administrador para renovar o endereço IP.");

        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo("ipconfig", "/renew")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });

            if (process is null)
                return new ProcessActionResult(false, "Não foi possível iniciar o ipconfig.");

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                return new ProcessActionResult(false, "Operação cancelada.");
            }

            return process.ExitCode == 0
                ? new ProcessActionResult(true, null)
                : new ProcessActionResult(false, "O Windows não conseguiu renovar o endereço IP.");
        }
        catch (Exception ex)
        {
            return new ProcessActionResult(false, $"Não foi possível renovar o endereço IP: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Limpa o cache ARP com <c>netsh interface ip delete arpcache</c> (oculto).
    /// Requer administrador. Sucesso = código de saída 0.
    /// </summary>
    public static ProcessActionResult ClearArpCache()
    {
        if (!ScanTargets.IsElevated)
            return new ProcessActionResult(false, "Requer administrador para limpar o cache ARP.");

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("netsh", "interface ip delete arpcache")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });

            if (process is null)
                return new ProcessActionResult(false, "Não foi possível iniciar o netsh.");

            process.WaitForExit();

            return process.ExitCode == 0
                ? new ProcessActionResult(true, null)
                : new ProcessActionResult(false, "O Windows não conseguiu limpar o cache ARP.");
        }
        catch (Exception ex)
        {
            return new ProcessActionResult(false, $"Não foi possível limpar o cache ARP: {ex.Message}");
        }
    }

    /// <summary>Nome amigável em PT-BR para o tipo de interface.</summary>
    private static string FriendlyType(NetworkInterface ni)
    {
        // VLANs chegam como Ethernet; o nome ou a descrição costuma trazer "VLAN".
        var label = $"{ni.Name} {ni.Description}";
        if (label.Contains("vlan", StringComparison.OrdinalIgnoreCase))
            return "VLAN";

        return ni.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.Ethernet3Megabit
                or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.GigabitEthernet => "Ethernet",
            NetworkInterfaceType.AsymmetricDsl
                or NetworkInterfaceType.RateAdaptDsl
                or NetworkInterfaceType.SymmetricDsl
                or NetworkInterfaceType.VeryHighSpeedDsl => "DSL",
            NetworkInterfaceType.Ppp => "Conexão discada",
            NetworkInterfaceType.Wman
                or NetworkInterfaceType.Wwanpp
                or NetworkInterfaceType.Wwanpp2 => "Móvel",
            _ => "Rede",
        };
    }

    /// <summary>Velocidade em bits por segundo para um texto curto ("1 Gbps", "300 Mbps").</summary>
    private static string FormatSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "—";

        if (bitsPerSecond >= 1_000_000_000)
            return $"{bitsPerSecond / 1_000_000_000.0:0.##} Gbps";

        if (bitsPerSecond >= 1_000_000)
            return $"{bitsPerSecond / 1_000_000.0:0.##} Mbps";

        if (bitsPerSecond >= 1_000)
            return $"{bitsPerSecond / 1_000.0:0.##} Kbps";

        return $"{bitsPerSecond} bps";
    }

    /// <summary>Primeiro endereço IPv4 unicast do adaptador, ou nulo se não houver.</summary>
    private static string? FirstIpv4(NetworkInterface ni)
    {
        try
        {
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    return addr.Address.ToString();
            }
        }
        catch
        {
            // Adaptador sem propriedades acessíveis: segue sem IPv4.
        }

        return null;
    }

    /// <summary>Verdadeiro quando o adaptador tem algum endereço unicast não-loopback.</summary>
    private static bool HasNonLoopbackUnicast(NetworkInterface ni)
    {
        try
        {
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (!IPAddress.IsLoopback(addr.Address))
                    return true;
            }
        }
        catch
        {
            // Adaptador sem propriedades acessíveis: trata como sem endereço.
        }

        return false;
    }

    /// <summary>Verdadeiro quando o adaptador expõe um endereço físico (MAC).</summary>
    private static bool HasPhysicalAddress(NetworkInterface ni)
    {
        try
        {
            return ni.GetPhysicalAddress().GetAddressBytes().Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
