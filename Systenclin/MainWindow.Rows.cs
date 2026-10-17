using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Systenclin.Core;

namespace Systenclin;

public partial class MainWindow : Window
{
    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de inicialização
    // ════════════════════════════════════════════════════════════════════════

    public sealed class StartupRow
    {
        private readonly StartupItem _item;
        private readonly bool _elevated;

        public StartupRow(StartupItem item, bool elevated)
        {
            _item = item;
            _elevated = elevated;
        }

        public string Id => _item.Id;
        public string Name => _item.Name;
        public string Command => _item.Command;
        public bool ItemEnabled => _item.Enabled;

        public string SourceLabel => _item.Source switch
        {
            StartupSource.UserRunKey => "Usuário — Registro",
            StartupSource.MachineRunKey => "Máquina — Registro (64 bits)",
            StartupSource.MachineRunKey32 => "Máquina — Registro (32 bits)",
            StartupSource.UserStartupFolder => "Usuário — Pasta Inicializar",
            _ => "Máquina — Pasta Inicializar",
        };

        public string ActionLabel => _item.Enabled ? "Desativar" : "Ativar";

        public bool ActionEnabled => !(_item.RequiresAdmin && !_elevated);

        public string? ActionToolTip => ActionEnabled ? null : "Requer administrador";

        public Visibility DisabledBadgeVisibility =>
            _item.Enabled ? Visibility.Collapsed : Visibility.Visible;

        public double RowOpacity => _item.Enabled ? 1.0 : 0.55;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linhas da lista de duplicados
    // ════════════════════════════════════════════════════════════════════════

    public sealed class DuplicateGroupRow : INotifyPropertyChanged
    {
        public DuplicateGroupRow(DuplicateGroup group)
        {
            Size = group.Size;

            // O mais antigo (índice 0, já ordenado pelo Core) fica desmarcado:
            // é o candidato natural a "original" que permanece.
            Files = new ObservableCollection<DuplicateFileRow>(
                group.Files.Select((file, index) => new DuplicateFileRow(file, isChecked: index > 0)));
        }

        public long Size { get; }

        public ObservableCollection<DuplicateFileRow> Files { get; }

        public string Header =>
            $"{Files.Count} arquivos • {CleanableItem.FormatBytes(Size)} cada • " +
            $"recuperável ~{CleanableItem.FormatBytes(Size * Math.Max(0, Files.Count - 1))}";

        public void NotifyChanged()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Header)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class DuplicateFileRow : INotifyPropertyChanged
    {
        private bool _isChecked;

        public DuplicateFileRow(DuplicateFile file, bool isChecked)
        {
            FilePath = file.Path;
            Size = file.Size;
            DateDisplay = file.LastWriteTimeUtc.ToLocalTime().ToString("dd/MM/yy HH:mm");
            _isChecked = isChecked;
        }

        public string FilePath { get; }
        public long Size { get; }
        public string DateDisplay { get; }
        public string SizeDisplay => CleanableItem.FormatBytes(Size);

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de atalhos
    // ════════════════════════════════════════════════════════════════════════

    public sealed class ShortcutRow : INotifyPropertyChanged
    {
        private bool _isChecked = true;

        public ShortcutRow(ShortcutItem item)
        {
            Path = item.Path;
            Name = item.Name;
            Location = item.Location;
            Target = item.Target;
        }

        public string Path { get; }
        public string Name { get; }
        public ShortcutLocation Location { get; }
        public string? Target { get; }

        public string LocationLabel => Location switch
        {
            ShortcutLocation.UserDesktop => "Área de trabalho",
            ShortcutLocation.CommonDesktop => "Área de trabalho pública",
            ShortcutLocation.UserStartMenu => "Menu Iniciar",
            _ => "Menu Iniciar (todos os usuários)",
        };

        public string TargetLabel => string.IsNullOrWhiteSpace(Target)
            ? "Destino não informado"
            : $"Destino: {Target.Trim()}";

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de aplicativos
    // ════════════════════════════════════════════════════════════════════════

    public sealed class AppRow
    {
        public AppRow(WingetPackage package)
        {
            Name = package.Name;
            Id = package.Id;
            CurrentVersion = package.CurrentVersion;
            AvailableVersion = package.AvailableVersion;
        }

        public string Name { get; }
        public string Id { get; }
        public string CurrentVersion { get; }
        public string AvailableVersion { get; }

        public string VersionsLabel =>
            $"{(string.IsNullOrWhiteSpace(CurrentVersion) ? "—" : CurrentVersion)} → " +
            $"{(string.IsNullOrWhiteSpace(AvailableVersion) ? "—" : AvailableVersion)}";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de arquivos grandes
    // ════════════════════════════════════════════════════════════════════════

    public sealed class LargeFileRow : INotifyPropertyChanged
    {
        // Desmarcada por padrão: remover arquivo grande é decisão consciente.
        private bool _isChecked;

        public LargeFileRow(LargeFileItem item)
        {
            Path = item.Path;
            Size = item.Size;
            Name = System.IO.Path.GetFileName(item.Path);
            DateDisplay = item.LastWriteTimeUtc.ToLocalTime().ToString("dd/MM/yyyy");
        }

        public string Path { get; }
        public string Name { get; }
        public long Size { get; }
        public string DateDisplay { get; }

        public string SizeDisplay => CleanableItem.FormatBytes(Size);

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de espaço em disco
    // ════════════════════════════════════════════════════════════════════════

    public sealed class SpaceRow : INotifyPropertyChanged
    {
        private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

        private string _name;
        private double _percent;
        private bool _canEnter;

        public SpaceRow(
            string name, string path, long size, int fileCount, long totalBytes, bool canEnter)
        {
            _name = name;
            Path = path;
            Size = size;
            FileCount = fileCount;
            // Fatia do total; total zero (pasta vazia) vira 0% em vez de NaN.
            _percent = totalBytes > 0 ? size * 100.0 / totalBytes : 0;
            _canEnter = canEnter;
        }

        public string Path { get; }
        public long Size { get; }
        public int FileCount { get; }

        public string Name
        {
            get => _name;
            set { if (_name == value) return; _name = value; OnPropertyChanged(); }
        }

        /// <summary>Percentual do total (0–100).</summary>
        public double Percent
        {
            get => _percent;
            set
            {
                if (_percent == value) return;
                _percent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PercentDisplay));
            }
        }

        /// <summary>Só subpastas navegam (duplo clique); a linha de soltos não.</summary>
        public bool CanEnter
        {
            get => _canEnter;
            set { if (_canEnter == value) return; _canEnter = value; OnPropertyChanged(); }
        }

        public string SizeDisplay => CleanableItem.FormatBytes(Size);

        public string CountDisplay => FileCount == 1 ? "1 arquivo" : $"{FileCount} arquivos";

        public string PercentDisplay => _percent.ToString("0.0", PtBr) + "%";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de processos
    // ════════════════════════════════════════════════════════════════════════

    public sealed class ProcRow : INotifyPropertyChanged
    {
        private bool _confirming;

        public ProcRow(ProcessItem item)
        {
            Pid = item.Pid;
            Name = item.Name;
            WorkingSetBytes = item.WorkingSetBytes;
        }

        public int Pid { get; }
        public string Name { get; }
        public long WorkingSetBytes { get; }

        public string PidLabel => $"PID {Pid}";
        public string MemoryDisplay => CleanableItem.FormatBytes(WorkingSetBytes);

        /// <summary>Dois cliques para encerrar: o próprio botão vira "Confirmar".</summary>
        public string ActionLabel => _confirming ? "Confirmar" : "Encerrar";

        public bool IsConfirming
        {
            get => _confirming;
            set
            {
                if (_confirming == value) return;
                _confirming = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActionLabel));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de desinstalação
    // ════════════════════════════════════════════════════════════════════════

    public sealed class UninsRow : INotifyPropertyChanged
    {
        private bool _confirming;

        public UninsRow(InstalledApp app)
        {
            Id = app.Id;
            Name = app.Name;
            Version = app.Version;
        }

        public string Id { get; }
        public string Name { get; }
        public string Version { get; }

        public string VersionLabel => string.IsNullOrWhiteSpace(Version)
            ? Id
            : $"Versão {Version} · {Id}";

        /// <summary>Dois cliques para desinstalar: o próprio botão vira "Confirmar?".</summary>
        public string ActionLabel => _confirming ? "Confirmar?" : "Desinstalar";

        public bool IsConfirming
        {
            get => _confirming;
            set
            {
                if (_confirming == value) return;
                _confirming = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActionLabel));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Linha da lista de adaptadores de rede (só exibição).</summary>
    public sealed class NetRow
    {
        public NetRow(NetworkAdapterInfo adapter)
        {
            Name = adapter.Name;
            Detail = $"{adapter.Type} · {adapter.Status} · {adapter.Speed}";
            Ipv4 = adapter.Ipv4 is { Length: > 0 } ip ? ip : "sem endereço IPv4";
        }

        public string Name { get; }
        public string Detail { get; }
        public string Ipv4 { get; }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista
    // ════════════════════════════════════════════════════════════════════════

    public sealed class ItemRow : INotifyPropertyChanged
    {
        private bool _isChecked;

        public CleanableItem? FileItem { get; }
        public PrivacyItem? PrivacyItem { get; }

        /// <summary>Linha de arquivo (v0.1).</summary>
        public ItemRow(CleanableItem item)
        {
            FileItem = item;
            _isChecked = item.Risk != RiskLevel.High && item.Category.DefaultChecked();
        }

        /// <summary>Linha de privacidade (v0.2).</summary>
        public ItemRow(PrivacyItem item)
        {
            PrivacyItem = item;
            // Item bloqueado nunca vem marcado: não dá para apagar mesmo.
            _isChecked = item.BlockedReason is null && item.Kind.DefaultChecked();
        }

        public bool IsPrivacy => PrivacyItem is not null;

        public string Title => PrivacyItem?.Title ?? FileItem!.Category.DisplayName();

        public string Subtitle => PrivacyItem is not null
            ? PrivacyItem.Profile.Display
            : FileItem!.IsDirectory
                ? $"{FileItem.Path}  ·  {FileItem.FileCount:N0} arquivo(s)"
                : FileItem!.Path;

        public long ItemSizeBytes => PrivacyItem?.SizeBytes ?? FileItem!.SizeBytes;
        public int ItemRecordCount => PrivacyItem?.RecordCount ?? FileItem!.FileCount;

        public string SizeDisplay => CleanableItem.FormatBytes(ItemSizeBytes);

        public bool CanToggle => PrivacyItem is not null
            ? PrivacyItem.BlockedReason is null
            : FileItem!.Risk != RiskLevel.High;

        public RiskLevel Risk => PrivacyItem?.Risk ?? FileItem!.Risk;

        public string Detail
        {
            get
            {
                if (PrivacyItem is not null)
                {
                    var parts = new List<string>();

                    if (PrivacyItem.BlockedReason is not null)
                        parts.Add($"Não foi possível ler: {PrivacyItem.BlockedReason}.");

                    parts.Add(PrivacyItem.Kind.Explanation());

                    if (PrivacyItem.RecordCount > 0)
                        parts.Add($"{PrivacyItem.RecordCount:N0} registro(s).");

                    if (PrivacyItem.Risk == RiskLevel.High)
                        parts.Add("Risco alto: não vem marcado.");

                    return string.Join("  ", parts);
                }

                var fileParts = new List<string>();
                if (!string.IsNullOrEmpty(FileItem!.Note)) fileParts.Add(FileItem.Note);
                fileParts.Add(FileItem.Category.Explanation());
                if (FileItem.Risk == RiskLevel.Medium)
                    fileParts.Add("Risco médio: pode deixar a próxima operação mais lenta.");
                return string.Join("  ", fileParts);
            }
        }

        public IReadOnlyList<string> Preview => PrivacyItem?.Preview ?? [];

        public Visibility PreviewVisibility =>
            PrivacyItem is not null && PrivacyItem.Preview.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        public string RiskLabel => Risk switch
        {
            RiskLevel.Low => "baixo",
            RiskLevel.Medium => "médio",
            _ => "alto",
        };

        // Pincéis congelados e compartilhados: antes era uma alocação nova
        // a cada leitura de propriedade, uma por linha realizada na tela.
        private static readonly SolidColorBrush LowRiskBackground = Frozen(0x1B, 0x3A, 0x24);
        private static readonly SolidColorBrush MediumRiskBackground = Frozen(0x3A, 0x30, 0x12);
        private static readonly SolidColorBrush HighRiskBackground = Frozen(0x3A, 0x1B, 0x1A);
        private static readonly SolidColorBrush LowRiskForeground = Frozen(0x7E, 0xE0, 0x8F);
        private static readonly SolidColorBrush MediumRiskForeground = Frozen(0xF0, 0xC6, 0x74);
        private static readonly SolidColorBrush HighRiskForeground = Frozen(0xFF, 0x9A, 0x94);

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public Brush RiskBackground => Risk switch
        {
            RiskLevel.Low => LowRiskBackground,
            RiskLevel.Medium => MediumRiskBackground,
            _ => HighRiskBackground,
        };

        public Brush RiskForeground => Risk switch
        {
            RiskLevel.Low => LowRiskForeground,
            RiskLevel.Medium => MediumRiskForeground,
            _ => HighRiskForeground,
        };

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
