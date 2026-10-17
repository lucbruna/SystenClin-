using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Systenclin.L10n;

/// <summary>
/// Propriedade anexada que marca um elemento com a chave de tradução do resx
/// (Tr.Key = título/conteúdo; Tr.TipKey = tooltip). Tr.Apply varre a árvore e
/// escreve os textos no idioma atual — no Loaded e a cada troca de idioma.
/// </summary>
internal static class Tr
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(Tr), new PropertyMetadata(null));

    public static readonly DependencyProperty TipKeyProperty = DependencyProperty.RegisterAttached(
        "TipKey", typeof(string), typeof(Tr), new PropertyMetadata(null));

    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);

    public static void SetKey(DependencyObject d, string? value) => d.SetValue(KeyProperty, value);

    public static string? GetTipKey(DependencyObject d) => (string?)d.GetValue(TipKeyProperty);

    public static void SetTipKey(DependencyObject d, string? value) => d.SetValue(TipKeyProperty, value);

    /// <summary>Aplica as traduções em toda a subárvore visual.</summary>
    public static void Apply(DependencyObject root)
    {
        if (GetKey(root) is { } key) ApplyText(root, key);
        if (GetTipKey(root) is { } tip) ToolTipService.SetToolTip(root, Loc.T(tip));

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++) Apply(VisualTreeHelper.GetChild(root, i));
    }

    private static void ApplyText(DependencyObject target, string key)
    {
        var text = Loc.T(key);
        switch (target)
        {
            case Window window: window.Title = text; break;
            case TextBlock block: block.Text = text; break;
            case TextBox box: box.Text = text; break;
            case HeaderedContentControl headered: headered.Header = text; break;
            case ContentControl content: content.Content = text; break;
        }
    }
}
