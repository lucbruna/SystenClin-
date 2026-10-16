using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Systenclin.Core;

/// <summary>
/// Registra cada operação para poder desfazer.
///
/// O que dá para desfazer e o que não dá, sendo honesto:
/// - ARQUIVO apagado: vai para a Lixeira, então dá para restaurar.
/// - PASTA apagada: o conteúdo é removido, não vai para a Lixeira. NÃO dá para
///   desfazer. Por isso a UI avisa e o log registra exatamente o que saiu.
///
/// Guardar o conteúdo de pastas inteiras em backup seria caro demais (GBs).
/// A alternativa honesta é: dizer que não dá, em vez de fingir que dá.
/// </summary>
public sealed class CleanLog
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Systenclin");

    private static readonly string LogPath = Path.Combine(LogDirectory, "historico.json");

    public sealed class Entry
    {
        public DateTimeOffset When { get; set; }
        public required string Path { get; set; }
        public required string Category { get; set; }
        public long SizeBytes { get; set; }
        public int FileCount { get; set; }
        public bool IsDirectory { get; set; }
        public bool Succeeded { get; set; }
        public bool Recoverable { get; set; }
        public string? Error { get; set; }
        public string? BackupPath { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public IReadOnlyList<Entry> Load()
    {
        try
        {
            if (!File.Exists(LogPath)) return [];
            var json = File.ReadAllText(LogPath);
            return JsonSerializer.Deserialize<List<Entry>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Append(IEnumerable<Entry> entries)
    {
        var tmp = LogPath + ".tmp";

        try
        {
            Directory.CreateDirectory(LogDirectory);
            var all = Load().ToList();
            all.AddRange(entries);

            // Mantém o histórico enxuto: últimas 500 operações.
            if (all.Count > 500) all = all.TakeLast(500).ToList();

            // Escreve num temporário e só então troca: nunca deixa
            // historico.json pela metade se algo falhar no meio.
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOptions));
            File.Move(tmp, LogPath, overwrite: true);
        }
        catch
        {
            // Log é conveniência, não pode derrubar a limpeza.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public void Clear()
    {
        try { if (File.Exists(LogPath)) File.Delete(LogPath); }
        catch { }
    }

    public static string Location => LogPath;
}
