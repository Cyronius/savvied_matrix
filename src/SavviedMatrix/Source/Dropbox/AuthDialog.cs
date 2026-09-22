using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SavviedMatrix.Ui;

namespace SavviedMatrix.Source.Dropbox;

/// <summary>
/// Collects the authorization code Dropbox shows in the browser. A window rather than a
/// console prompt because the app has no console attached when it is double-clicked, and
/// code-only rather than a markup file so the whole authorization flow stays readable in
/// one place.
/// </summary>
public sealed class AuthDialog : Window
{
    private readonly TextBox _input;

    /// <summary>The pasted code, or null if the operator cancelled.</summary>
    public string? Code { get; private set; }

    public AuthDialog()
    {
        Title = "SavviedMatrix - Dropbox authorization";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = MessageWindow.Panel;

        var label = new TextBlock
        {
            Text = "A browser window has opened for Dropbox.\n\n"
                 + "Click Allow, then copy the authorization code Dropbox shows\n"
                 + "and paste it below.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = MessageWindow.Ink
        };

        _input = new TextBox
        {
            FontFamily = FontFamily.Parse("monospace"),
            Background = MessageWindow.Field,
            Foreground = MessageWindow.Ink,
            CaretBrush = MessageWindow.Ink,
            PlaceholderText = "authorization code"
        };

        var okButton = new Button
        {
            Content = "OK",
            IsEnabled = false,
            IsDefault = true,
            Foreground = MessageWindow.Ink,
            Background = MessageWindow.Field,
            Padding = new Thickness(20, 6)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Foreground = MessageWindow.Ink,
            Background = MessageWindow.Field,
            Padding = new Thickness(20, 6)
        };

        _input.TextChanged += (_, _) =>
            okButton.IsEnabled = !string.IsNullOrWhiteSpace(_input.Text);

        okButton.Click += (_, _) =>
        {
            Code = _input.Text?.Trim();
            Close();
        };

        cancelButton.Click += (_, _) =>
        {
            Code = null;
            Close();
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                label,
                _input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, okButton }
                }
            }
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Activate();
        _input.Focus();
    }

    /// <summary>Escape cancels, which is what the platform's own dialogs do.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Code = null;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }
}
