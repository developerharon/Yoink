using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Yoink.Views;

/// <summary>
/// The custom title-bar row shared by every small popup window in this app (AddDownloadDialog,
/// MessageBoxWindow, UpdatePromptDialog) now that they use <c>WindowDecorations="None"</c> the same
/// way MainWindow does, instead of a plain OS-drawn title bar — see MainWindow.axaml's own comment
/// for why (ExtendClientAreaToDecorationsHint doesn't actually merge on GNOME/Mutter). Factored out
/// as one control rather than copy-pasting the drag/close wiring into three windows.
/// </summary>
public partial class DialogTitleBar : UserControl
{
    public DialogTitleBar()
    {
        InitializeComponent();
    }

    /// <summary>
    /// A plain CLR property (not a styled/Avalonia property) is enough here — this never needs to be
    /// bound to anything live, just set once from XAML or, when a dialog's title varies at runtime
    /// (e.g. AddDownloadDialog's "Download detected" prefill), once more from code-behind.
    /// </summary>
    public string? Title
    {
        get => TxtTitle.Text;
        set => TxtTitle.Text = value;
    }

    /// <summary>
    /// Belt-and-braces alongside <c>chrome:WindowDecorationProperties.ElementRole="TitleBar"</c> in
    /// the XAML — see MainWindow's identical comment on why both are wired rather than trusting the
    /// attached property alone to grant drag-to-move.
    ///
    /// <see cref="TopLevel.GetTopLevel"/>, not <see cref="Visual.VisualRoot"/> — same lookup
    /// <see cref="SettingsView"/>'s Browse-folder button already uses. Tried <c>VisualRoot</c> first
    /// (matching MainWindow's own drag handler, which calls it on the Window itself rather than a
    /// nested control); reached via an actual headless click rather than assumed safe, it resolves to
    /// Avalonia's internal <c>TopLevelHost</c> wrapper for a control this deep in the visual tree —
    /// not a <see cref="Window"/> — since this control is nested a level below the Window's own
    /// content, unlike MainWindow's handler which runs on the Window itself. That silently no-opped
    /// <see cref="BtnClose_Click"/>'s <c>is Window</c> check, which is exactly why the close button
    /// stopped working. <c>GetTopLevel</c> walks up to the actual hosting <see cref="TopLevel"/>
    /// (the <see cref="Window"/> here) regardless of how deep this control sits.
    /// </summary>
    private void TitleBarRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && TopLevel.GetTopLevel(this) is Window window)
            window.BeginMoveDrag(e);
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
            window.Close();
    }
}
