using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Systenclin.Core;

/// <summary>Fonte de onde o item de inicialização vem.</summary>
public enum StartupSource
{
    /// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
    UserRunKey,

    /// <summary>HKLM\Software\Microsoft\Windows\CurrentVersion\Run, visão 64-bit.</summary>
    MachineRunKey,

    /// <summary>HKLM\Software\Microsoft\Windows\CurrentVersion\Run, visão 32-bit.</summary>
    MachineRunKey32,

    /// <summary>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup.</summary>
    UserStartupFolder,

    /// <summary>%ProgramData%\Microsoft\Windows\Start Menu\Programs\Startup.</summary>
    MachineStartupFolder,
}

/// <summary>Um programa que sobe com o Windows, ativo ou desativado pelo app.</summary>
public sealed class StartupItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public StartupSource Source { get; init; }
    public bool Enabled { get; init; }
    public bool RequiresAdmin { get; init; }
}

/// <summary>Resultado de ativar/desativar; Error vem em português curto quando falha.</summary>
public sealed record StartupToggleResult(bool Success, string? Error);

/// <summary>
/// Gerencia o que sobe com o Windows: as cinco fontes (chave Run do usuário,
/// chave Run da máquina nas visões 64 e 32 bits e as duas pastas Inicializar).
///
/// Desativar não perde nada: o valor de Run é registrado em
/// %LOCALAPPDATA%\Systenclin\inicializacao.json (nome, tipo e dados originais)
/// ANTES de sair do registro, e arquivo de pasta é movido para
/// %LOCALAPPDATA%\Systenclin\inicializacao_desativados. Ativar desfaz o processo
/// com os dados guardados. Nenhum método público lança exceção.
/// </summary>
public static class StartupManager
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Systenclin");

    private static readonly string StorePath = Path.Combine(DataDirectory, "inicializacao.json");

    private static readonly string DisabledDirectory = Path.Combine(DataDirectory, "inicializacao_desativados");

    // Uma escrita de cada vez: dois cliques rápidos não podem se atropelar.
    private static readonly object StoreLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Ordem de exibição e de varredura: exatamente a ordem do enum.
    private static readonly StartupSource[] AllSources =
    [
        StartupSource.UserRunKey,
        StartupSource.MachineRunKey,
        StartupSource.MachineRunKey32,
        StartupSource.UserStartupFolder,
        StartupSource.MachineStartupFolder,
    ];

    // ── API pública ─────────────────────────────────────────────────────

    /// <summary>
    /// Enumera as cinco fontes sem lançar: falha em uma não derruba as outras.
    /// Itens desativados (store) entram com Enabled=false; se o mesmo Id existir
    /// fisicamente E no store, o físico vence e o store fica intocado.
    /// </summary>
    public static IReadOnlyList<StartupItem> List()
    {
        var items = new List<StartupItem>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in AllSources)
        {
            try
            {
                AddPhysicalItems(items, ids, source);
            }
            catch
            {
                // Fonte ilegível (permissão, chave ausente): segue para a próxima.
            }
        }

        foreach (var entry in LoadStore())
        {
            try
            {
                AddStoredItem(items, ids, entry);
            }
            catch
            {
                // Entrada malformada: ignora só ela.
            }
        }

        try
        {
            // Ordem estável: fonte pela ordem do enum e, dentro dela, nome sem caixa.
            return items
                .OrderBy(i => (int)i.Source)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return items;
        }
    }

    /// <summary>
    /// Desativa um item ativo. Registro: o store é gravado primeiro e revertido
    /// se o DeleteValue falhar. Pasta: o arquivo é movido para a pasta de
    /// desativados, com sufixo numérico se o nome colidir.
    /// </summary>
    public static StartupToggleResult Disable(string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
                return new(false, "Item inválido.");

            var item = List().FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            if (item is null)
                return new(false, "Item não encontrado.");
            if (!item.Enabled)
                return new(false, "Este item já está desativado.");
            if (item.RequiresAdmin && !ScanTargets.IsElevated)
                return new(false, "Requer administrador para alterar este item.");

            return IsFolderSource(item.Source) ? DisableFolder(item) : DisableRunKey(item);
        }
        catch
        {
            return new(false, "Não foi possível desativar o item.");
        }
    }

    /// <summary>
    /// Reativa um item que está no store. Registro: recria o valor com o tipo e
    /// os dados originais, na visão certa conforme a fonte. Pasta: move o arquivo
    /// de volta ao caminho original. Só remove a entrada do store após o sucesso.
    /// </summary>
    public static StartupToggleResult Enable(string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
                return new(false, "Item inválido.");

            var entry = LoadStore().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return new(false, "Este item não está desativado.");

            var entryId = entry.Id;
            if (string.IsNullOrEmpty(entryId))
                return new(false, "Dados de restauração inválidos.");

            if (!Enum.TryParse<StartupSource>(entry.Source, ignoreCase: true, out var source))
                return new(false, "Dados de restauração inválidos.");

            if (SourceRequiresAdmin(source) && !ScanTargets.IsElevated)
                return new(false, "Requer administrador para alterar este item.");

            return IsFolderSource(source)
                ? EnableFromFolder(entry, entryId)
                : EnableRunKey(entry, source, entryId);
        }
        catch
        {
            return new(false, "Não foi possível ativar o item.");
        }
    }

    // ── Desativar / ativar: chave Run ───────────────────────────────────

    /// <summary>
    /// Desativa um valor da chave Run. O Id perde a caixa do nome original,
    /// então o valor é reencontrado pelo Id; o store guarda nome, tipo e dados
    /// sem expandir variáveis de ambiente, para restaurar fielmente.
    /// </summary>
    private static StartupToggleResult DisableRunKey(StartupItem item)
    {
        var source = item.Source;

        var baseKey = TryOpenBaseKey(source);
        if (baseKey is null)
            return new(false, "Não foi possível acessar o registro.");

        using (baseKey)
        using (var runKey = TryOpenRunKey(baseKey, writable: true))
        {
            if (runKey is null)
                return new(false, "Não foi possível acessar o registro.");

            string? valueName = null;
            RegistryValueKind kind = default;
            var data = "";

            try
            {
                foreach (var candidate in runKey.GetValueNames())
                {
                    if (!string.Equals(BuildId(source, candidate), item.Id, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var candidateKind = runKey.GetValueKind(candidate);
                    if (candidateKind != RegistryValueKind.String && candidateKind != RegistryValueKind.ExpandString)
                        continue;

                    if (runKey.GetValue(candidate, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string candidateData)
                        continue;

                    valueName = candidate;
                    kind = candidateKind;
                    data = candidateData;
                    break;
                }
            }
            catch
            {
                return new(false, "Não foi possível acessar o registro.");
            }

            if (valueName is null)
                return new(false, "Item não encontrado no registro.");

            var entry = new StoreEntry
            {
                Id = item.Id,
                Source = source.ToString(),
                Name = valueName,
                Command = data,
                ValueName = valueName,
                ValueKind = kind.ToString(),
                ValueData = data,
                DisabledAt = DateTimeOffset.UtcNow.ToString("o"),
            };

            // Store primeiro: se o registro falhar, dá para reverter com segurança.
            if (!AddStoreEntry(entry))
                return new(false, "Não foi possível registrar a alteração.");

            try
            {
                runKey.DeleteValue(valueName, throwOnMissingValue: true);
            }
            catch
            {
                RemoveStoreEntry(item.Id);
                return new(false, "Não foi possível desativar o item no registro.");
            }

            return new(true, null);
        }
    }

    /// <summary>
    /// Reativa um valor da chave Run com o tipo e os dados guardados, na visão
    /// correta conforme a fonte. A elevação já foi validada pelo chamador.
    /// </summary>
    private static StartupToggleResult EnableRunKey(StoreEntry entry, StartupSource source, string entryId)
    {
        var valueName = entry.ValueName;
        var valueData = entry.ValueData;
        if (string.IsNullOrEmpty(valueName) || valueData is null)
            return new(false, "Dados de restauração inválidos.");

        if (!Enum.TryParse<RegistryValueKind>(entry.ValueKind, ignoreCase: true, out var kind) ||
            (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString))
            return new(false, "Dados de restauração inválidos.");

        var baseKey = TryOpenBaseKey(source);
        if (baseKey is null)
            return new(false, "Não foi possível acessar o registro.");

        using (baseKey)
        using (var runKey = TryOpenRunKey(baseKey, writable: true))
        {
            if (runKey is null)
                return new(false, "Não foi possível acessar o registro.");

            try
            {
                runKey.SetValue(valueName, valueData, kind);
            }
            catch
            {
                return new(false, "Não foi possível restaurar o item no registro.");
            }
        }

        RemoveStoreEntry(entryId);
        return new(true, null);
    }

    // ── Desativar / ativar: pasta Inicializar ───────────────────────────

    /// <summary>
    /// Desativa um arquivo da pasta Inicializar: move para a pasta de desativados
    /// (sufixo numérico se o nome colidir) e registra no store. Se o registro
    /// falhar, devolve o arquivo ao lugar — sem store não haveria como reativar.
    /// </summary>
    private static StartupToggleResult DisableFolder(StartupItem item)
    {
        var path = item.Command;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new(false, "Item não encontrado na pasta de inicialização.");

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
            return new(false, "Item não encontrado na pasta de inicialização.");

        try
        {
            Directory.CreateDirectory(DisabledDirectory);
        }
        catch
        {
            return new(false, "Não foi possível preparar a pasta de itens desativados.");
        }

        var dest = UniqueDisabledPath(fileName);
        if (dest is null)
            return new(false, "Não foi possível preparar a pasta de itens desativados.");

        try
        {
            File.Move(path, dest);
        }
        catch
        {
            return new(false, "Não foi possível mover o arquivo.");
        }

        var entry = new StoreEntry
        {
            Id = item.Id,
            Source = item.Source.ToString(),
            Name = fileName,
            Command = path,
            OriginalPath = path,
            DisabledPath = dest,
            DisabledAt = DateTimeOffset.UtcNow.ToString("o"),
        };

        if (!AddStoreEntry(entry))
        {
            try { File.Move(dest, path); } catch { }
            return new(false, "Não foi possível registrar a alteração.");
        }

        return new(true, null);
    }

    /// <summary>
    /// Reativa um arquivo movendo-o de volta ao caminho original. Arquivo ausente
    /// ou destino ocupado mantêm o store intacto e retornam erro em português.
    /// </summary>
    private static StartupToggleResult EnableFromFolder(StoreEntry entry, string entryId)
    {
        var original = entry.OriginalPath;
        var disabled = entry.DisabledPath;
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(disabled))
            return new(false, "Dados de restauração inválidos.");

        if (!File.Exists(disabled))
            return new(false, "Arquivo desativado não encontrado.");
        if (File.Exists(original))
            return new(false, "Já existe um arquivo de mesmo nome na pasta de inicialização.");

        try
        {
            var directory = Path.GetDirectoryName(original);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.Move(disabled, original);
        }
        catch
        {
            return new(false, "Não foi possível restaurar o arquivo.");
        }

        RemoveStoreEntry(entryId);
        return new(true, null);
    }

    // ── Enumeração física ───────────────────────────────────────────────

    /// <summary>Adiciona à lista os itens físicos de uma fonte (registro ou pasta).</summary>
    private static void AddPhysicalItems(List<StartupItem> items, HashSet<string> ids, StartupSource source)
    {
        if (IsRunKeySource(source))
        {
            foreach (var value in ReadRunValues(source))
            {
                var id = BuildId(source, value.ValueName);
                if (!ids.Add(id))
                    continue;

                items.Add(new StartupItem
                {
                    Id = id,
                    Name = value.ValueName,
                    Command = value.Data,
                    Source = source,
                    Enabled = true,
                    RequiresAdmin = SourceRequiresAdmin(source),
                });
            }

            return;
        }

        var folder = StartupFolderFor(source);
        if (folder is null || !Directory.Exists(folder))
            return;

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (string.IsNullOrEmpty(name))
                continue;

            // desktop.ini e afins (Hidden/System) não são itens de inicialização.
            if (IsHiddenOrSystem(file))
                continue;

            var id = BuildId(source, name);
            if (!ids.Add(id))
                continue;

            items.Add(new StartupItem
            {
                Id = id,
                Name = name,
                Command = file,
                Source = source,
                Enabled = true,
                RequiresAdmin = SourceRequiresAdmin(source),
            });
        }
    }

    /// <summary>
    /// Verdadeiro para arquivo oculto ou de sistema (ex.: desktop.ini das pastas
    /// Inicializar). Falha ao ler os atributos conta como "pular": mais seguro.
    /// </summary>
    private static bool IsHiddenOrSystem(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Adiciona um item desativado do store; o físico tem prioridade no dedupe.</summary>
    private static void AddStoredItem(List<StartupItem> items, HashSet<string> ids, StoreEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
            return;
        if (!Enum.TryParse<StartupSource>(entry.Source, ignoreCase: true, out var source))
            return;
        if (!ids.Add(entry.Id))
            return;

        items.Add(new StartupItem
        {
            Id = entry.Id,
            Name = entry.Name ?? "",
            Command = entry.Command ?? "",
            Source = source,
            Enabled = false,
            RequiresAdmin = SourceRequiresAdmin(source),
        });
    }

    /// <summary>
    /// Lê os valores de texto (String/ExpandString) da chave Run da fonte. Os
    /// dados são lidos SEM expandir variáveis de ambiente, para restaurar fiel.
    /// </summary>
    private static List<(string ValueName, RegistryValueKind Kind, string Data)> ReadRunValues(StartupSource source)
    {
        var values = new List<(string ValueName, RegistryValueKind Kind, string Data)>();

        // Em SO 32-bit não existe visão separada: enumerar duplicaria a fonte.
        if (source == StartupSource.MachineRunKey32 && !Environment.Is64BitOperatingSystem)
            return values;

        var baseKey = TryOpenBaseKey(source);
        if (baseKey is null)
            return values;

        using (baseKey)
        using (var runKey = TryOpenRunKey(baseKey, writable: false))
        {
            if (runKey is null)
                return values;

            foreach (var valueName in runKey.GetValueNames())
            {
                try
                {
                    var kind = runKey.GetValueKind(valueName);
                    if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString)
                        continue;

                    if (runKey.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string data)
                        continue;

                    values.Add((valueName, kind, data));
                }
                catch
                {
                    // Valor ilegível: ignora só este valor.
                }
            }
        }

        return values;
    }

    // ── Auxiliares de fonte ─────────────────────────────────────────────

    private static string BuildId(StartupSource source, string name) =>
        $"{source}|{name.ToLowerInvariant()}";

    private static bool IsRunKeySource(StartupSource source) =>
        source is StartupSource.UserRunKey or StartupSource.MachineRunKey or StartupSource.MachineRunKey32;

    private static bool IsFolderSource(StartupSource source) =>
        source is StartupSource.UserStartupFolder or StartupSource.MachineStartupFolder;

    private static bool SourceRequiresAdmin(StartupSource source) =>
        source is StartupSource.MachineRunKey or StartupSource.MachineRunKey32 or StartupSource.MachineStartupFolder;

    private static string? StartupFolderFor(StartupSource source) => source switch
    {
        StartupSource.UserStartupFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        StartupSource.MachineStartupFolder => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        _ => null,
    };

    private static (RegistryHive Hive, RegistryView View) RegistryLocationFor(StartupSource source) => source switch
    {
        // HKCU: uma visão só; a padrão do processo basta e evita surpresa.
        StartupSource.UserRunKey => (RegistryHive.CurrentUser, RegistryView.Default),
        // HKLM: visão 64-bit para a chave principal, 32-bit para a outra fonte.
        StartupSource.MachineRunKey => (RegistryHive.LocalMachine, RegistryView.Registry64),
        _ => (RegistryHive.LocalMachine, RegistryView.Registry32),
    };

    private static RegistryKey? TryOpenBaseKey(StartupSource source)
    {
        var (hive, view) = RegistryLocationFor(source);

        try
        {
            return RegistryKey.OpenBaseKey(hive, view);
        }
        catch
        {
            // Visão indisponível ou sem acesso: a fonte fica vazia.
            return null;
        }
    }

    private static RegistryKey? TryOpenRunKey(RegistryKey baseKey, bool writable)
    {
        try
        {
            return baseKey.OpenSubKey(RunSubKey, writable);
        }
        catch
        {
            return null;
        }
    }

    // ── Store JSON (%LOCALAPPDATA%\Systenclin\inicializacao.json) ───────

    /// <summary>
    /// Item desativado guardado em disco. Campos de registro (ValueName/ValueKind/
    /// ValueData) e de pasta (OriginalPath/DisabledPath) são mutuamente exclusivos.
    /// Tudo string e nulável: leitura tolerante, nada lança com JSON editado à mão.
    /// </summary>
    private sealed class StoreEntry
    {
        public string? Id { get; set; }
        public string? Source { get; set; }
        public string? Name { get; set; }
        public string? Command { get; set; }
        public string? ValueName { get; set; }
        public string? ValueKind { get; set; }
        public string? ValueData { get; set; }
        public string? OriginalPath { get; set; }
        public string? DisabledPath { get; set; }
        public string? DisabledAt { get; set; }
    }

    /// <summary>Lê o store; ausente ou corrompido vira lista vazia, nunca lança.</summary>
    private static List<StoreEntry> LoadStore()
    {
        try
        {
            lock (StoreLock)
            {
                if (!File.Exists(StorePath))
                    return [];

                var json = File.ReadAllText(StorePath);
                return JsonSerializer.Deserialize<List<StoreEntry>>(json, JsonOptions) ?? [];
            }
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Grava o store num .tmp e troca com File.Move: nunca fica pela metade.</summary>
    private static bool SaveStore(List<StoreEntry> entries)
    {
        lock (StoreLock)
        {
            var tmp = StorePath + ".tmp";

            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(tmp, JsonSerializer.Serialize(entries, JsonOptions));
                File.Move(tmp, StorePath, overwrite: true);
                return true;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }
    }

    /// <summary>Adiciona (ou substitui pelo Id) uma entrada, com escrita atômica.</summary>
    private static bool AddStoreEntry(StoreEntry entry)
    {
        lock (StoreLock)
        {
            var all = LoadStore();
            all.RemoveAll(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            all.Add(entry);
            return SaveStore(all);
        }
    }

    /// <summary>Remove a entrada pelo Id, com escrita atômica.</summary>
    private static void RemoveStoreEntry(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return;

        lock (StoreLock)
        {
            var all = LoadStore();
            if (all.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) > 0)
                SaveStore(all);
        }
    }

    /// <summary>
    /// Escolhe um nome livre na pasta de desativados, acrescentando sufixo
    /// numérico quando já existe arquivo com o mesmo nome.
    /// </summary>
    private static string? UniqueDisabledPath(string fileName)
    {
        var path = Path.Combine(DisabledDirectory, fileName);
        if (!File.Exists(path))
            return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var n = 1; n < 1000; n++)
        {
            var candidate = Path.Combine(DisabledDirectory, $"{stem}_{n}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
