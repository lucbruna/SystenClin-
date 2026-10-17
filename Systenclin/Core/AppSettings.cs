using System.IO;
using System.Text.Json;

namespace Systenclin.Core;

/// <summary>
/// Configuração persistida do app (hoje: idioma da UI) em
/// %LOCALAPPDATA%\Systenclin\settings.json. Falha de leitura/escrita nunca
/// derruba o app — na pior hipótese a configuração volta ao padrão.
/// </summary>
public static class AppSettings
{
    private sealed class Data
    {
        public string? Language { get; set; }
    }

    private static string _path = DefaultPath();
    private static Data _data = LoadFile();

    /// <summary>Troca o arquivo usado (testes). Recarrega na hora.</summary>
    public static void UsePath(string path)
    {
        _path = path;
        _data = LoadFile();
    }

    public static string? Language
    {
        get => _data.Language;
        set => _data.Language = value;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sem persistência: o app segue com o idioma apenas em memória.
        }
    }

    private static Data LoadFile()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(_path)) ?? new Data();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new Data();
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Systenclin",
        "settings.json");
}
