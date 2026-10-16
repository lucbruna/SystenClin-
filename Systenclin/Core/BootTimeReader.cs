using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace Systenclin.Core;

/// <summary>Desempenho do último boot: instante do início e duração total.</summary>
public sealed record BootPerformance(DateTime BootTime, TimeSpan BootDuration);

/// <summary>
/// Lê o evento 100 (desempenho de boot) do log de diagnóstico do Windows.
/// Log ausente, sem permissão ou XML inesperado resultam em null, nunca exceção.
/// </summary>
public static class BootTimeReader
{
    // Canal onde o Windows registra o diagnóstico de desempenho do boot.
    private const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";

    // Evento 100 = boot performance; o resto do canal é ignorado.
    private const string EventXPath = "*[System[(EventID=100)]]";

    /// <summary>Último evento de desempenho de boot, ou null se indisponível.</summary>
    public static BootPerformance? GetLastBoot()
    {
        try
        {
            var query = new EventLogQuery(LogName, PathType.LogName, EventXPath);

            // A ordem de leitura não é garantida: guarda o registro mais recente.
            EventRecord? latest = null;

            using (var reader = new EventLogReader(query))
            {
                for (var evt = reader.ReadEvent(); evt is not null; evt = reader.ReadEvent())
                {
                    if (latest is null || TimeOf(evt) >= TimeOf(latest))
                    {
                        latest?.Dispose();
                        latest = evt;
                    }
                    else
                    {
                        evt.Dispose();
                    }
                }
            }

            if (latest is null)
                return null;

            using (latest)
            {
                return Parse(latest);
            }
        }
        catch
        {
            // Log inexistente, desabilitado ou sem permissão: sem dados de boot.
            return null;
        }
    }

    /// <summary>Últimas inicializações, da mais recente para a mais antiga. Lista vazia se indisponível.</summary>
    public static IReadOnlyList<BootPerformance> GetRecent(int max = 10)
    {
        if (max <= 0)
            return Array.Empty<BootPerformance>();

        if (max > 50)
            max = 50;

        var results = new List<BootPerformance>();

        try
        {
            var query = new EventLogQuery(LogName, PathType.LogName, EventXPath);
            var records = new List<EventRecord>();

            try
            {
                using (var reader = new EventLogReader(query))
                {
                    for (var evt = reader.ReadEvent(); evt is not null; evt = reader.ReadEvent())
                        records.Add(evt);
                }
            }
            catch
            {
                // Falha no meio da leitura: segue com os registros já obtidos.
            }

            try
            {
                // Mais recentes primeiro, até o limite pedido.
                foreach (var evt in records.OrderByDescending(TimeOf))
                {
                    var performance = Parse(evt);
                    if (performance is not null)
                        results.Add(performance);

                    if (results.Count >= max)
                        break;
                }
            }
            finally
            {
                foreach (var evt in records)
                    evt.Dispose();
            }
        }
        catch
        {
            // Log indisponível ou erro inesperado: devolve o parcial (ou vazio).
        }

        return results;
    }

    /// <summary>TimeCreated do registro; sem data, vale o mais antigo possível.</summary>
    private static DateTime TimeOf(EventRecord evt) => evt.TimeCreated ?? DateTime.MinValue;

    /// <summary>Extrai boot e duração do XML do evento, com fallback posicional.</summary>
    private static BootPerformance? Parse(EventRecord evt)
    {
        long? bootFileTime = null;
        long? mainPathMs = null;
        long? postBootMs = null;

        try
        {
            var doc = XDocument.Parse(evt.ToXml());
            bootFileTime = ReadDataByName(doc, "BootTime");
            mainPathMs = ReadDataByName(doc, "MainPathBootTime");
            postBootMs = ReadDataByName(doc, "BootPostBootTime");
        }
        catch
        {
            // XML inesperado: cai para os dados posicionais abaixo.
        }

        // Fallback: campos na ordem do evento 100 — índices 0/1/2.
        bootFileTime ??= ReadProperty(evt, 0);
        mainPathMs ??= ReadProperty(evt, 1);
        postBootMs ??= ReadProperty(evt, 2);

        if (bootFileTime is null || mainPathMs is null || postBootMs is null)
            return null;

        try
        {
            var bootTime = DateTime.FromFileTimeUtc(bootFileTime.Value).ToLocalTime();
            var duration = TimeSpan.FromMilliseconds(mainPathMs.Value + postBootMs.Value);
            return new BootPerformance(bootTime, duration);
        }
        catch
        {
            // FILETIME fora de faixa: resultado não confiável.
            return null;
        }
    }

    /// <summary>Valor numérico do &lt;Data Name="..."&gt; ou null se ausente/inválido.</summary>
    private static long? ReadDataByName(XDocument doc, string name)
    {
        try
        {
            var data = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Data"
                    && string.Equals((string?)e.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase));

            return data is null ? null : TryToInt64(data.Value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Valor da propriedade posicional do registro, se existir e for numérica.</summary>
    private static long? ReadProperty(EventRecord evt, int index)
    {
        try
        {
            var properties = evt.Properties;
            if (properties is null || index >= properties.Count)
                return null;

            return TryToInt64(properties[index].Value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Converte string decimal/hexa ou valor numérico do evento para long.</summary>
    private static long? TryToInt64(object? value)
    {
        try
        {
            switch (value)
            {
                case null:
                    return null;

                case string hex when hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase):
                    return long.TryParse(hex.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var fromHex)
                        ? fromHex
                        : null;

                case string text:
                    return long.TryParse(text, out var parsed) ? parsed : null;

                case IConvertible:
                    return Convert.ToInt64(value);

                default:
                    return null;
            }
        }
        catch
        {
            // Valor inesperado ou fora de faixa: tratado como indisponível.
            return null;
        }
    }
}
