using Avalonia;
using Avalonia.Themes.Simple;

namespace SavviedMatrix.Ui;

/// <summary>
/// The Avalonia application object. The viewer draws every pixel itself and needs no theme,
/// but the authorization dialog is made of ordinary controls, so a minimal one is loaded.
/// </summary>
public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new SimpleTheme());
        base.Initialize();
    }
}
