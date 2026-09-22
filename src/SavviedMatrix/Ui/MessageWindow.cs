using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SavviedMatrix.Ui;

/// <summary>
/// What a message box used to be. The app has no console on any platform when it is
/// launched by double-click, so anything the operator must read has to be a window.
/// </summary>
public sealed class MessageWindow : Window
{
    public static readonly IBrush Panel = new SolidColorBrush(Color.FromRgb(12, 16, 12));
    public static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(180, 255, 180));
    public static readonly IBrush Field = new SolidColorBrush(Color.FromRgb(24, 32, 24));

    public MessageWindow(string text, string caption)
    {
        Title = caption;
        SizeToContent = SizeToContent.Height;
        Width = 520;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Panel;

        var ok = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = Ink,
            Background = Field,
            Padding = new Thickness(20, 6)
        };
        ok.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Ink
                },
                ok
            }
        };
    }
}
