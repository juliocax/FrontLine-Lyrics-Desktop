using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;

namespace FrontLine_Linux;

internal sealed record LanguageOption(string? Code, string Label)
{
    public override string ToString() => Label;
}

internal static class LanguagePicker
{
    internal static void Bind(string language, Button button, LanguageOption[] choices, string? selected, Action<string?> change)
    {
        var active = choices.FirstOrDefault(item => item.Code == selected);
        button.IsEnabled = active is not null;
        button.Content = active is null ? "—" : active.Label + "  ▾";
        var search = new TextBox
        {
            Watermark = DialogUi.L(language, "Search languages", "Buscar idiomas", "Buscar idiomas", "Cari bahasa")
        };
        var list = new ListBox { MaxHeight = 240, ItemsSource = choices };
        var panel = new StackPanel { Spacing = 6, MinWidth = 245 };
        panel.Children.Add(search);
        panel.Children.Add(list);
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        search.TextChanged += (_, _) =>
        {
            var query = search.Text?.Trim() ?? "";
            list.ItemsSource = choices.Where(item => CultureInfo.CurrentCulture.CompareInfo.IndexOf(
                item.Label, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0 ||
                item.Code?.Contains(query, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        };
        flyout.Opened += (_, _) =>
        {
            list.SelectedItem = null;
            search.Text = "";
            Dispatcher.UIThread.Post(() => search.Focus());
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not LanguageOption item) return;
            button.Content = item.Label + "  ▾";
            flyout.Hide();
            change(item.Code);
        };
        button.Flyout = flyout;
    }
}
