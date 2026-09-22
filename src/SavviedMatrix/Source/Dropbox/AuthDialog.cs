namespace SavviedMatrix.Source.Dropbox;

/// <summary>
/// Collects the authorization code Dropbox shows in the browser. A dialog rather than a console
/// prompt because the app is a WinExe with no console attached, and code-only rather than a
/// designer form so the whole authorization flow stays readable in one place.
/// </summary>
public sealed class AuthDialog : Form
{
    private static readonly Color Background = Color.FromArgb(12, 16, 12);
    private static readonly Color Foreground = Color.FromArgb(180, 255, 180);
    private static readonly Color FieldBackground = Color.FromArgb(24, 32, 24);

    private readonly TextBox _input;

    /// <summary>The pasted code, or null if the operator cancelled.</summary>
    public string? Code { get; private set; }

    public AuthDialog()
    {
        Text = "SavviedMatrix - Dropbox authorization";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Background;
        ForeColor = Foreground;
        ClientSize = new Size(520, 200);
        Padding = new Padding(16);

        var label = new Label
        {
            Text = "A browser window has opened for Dropbox.\r\n\r\n"
                 + "Click Allow, then copy the authorization code Dropbox shows\r\n"
                 + "and paste it below.",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 84,
            ForeColor = Foreground,
            BackColor = Background
        };

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Enabled = false,
            ForeColor = Foreground,
            BackColor = FieldBackground,
            FlatStyle = FlatStyle.Flat
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            ForeColor = Foreground,
            BackColor = FieldBackground,
            FlatStyle = FlatStyle.Flat
        };

        _input = new TextBox
        {
            Dock = DockStyle.Top,
            Font = new Font(FontFamily.GenericMonospace, 10f),
            BackColor = FieldBackground,
            ForeColor = Foreground,
            BorderStyle = BorderStyle.FixedSingle
        };
        _input.TextChanged += (_, _) => okButton.Enabled = _input.Text.Trim().Length > 0;

        okButton.Click += (_, _) => Code = _input.Text.Trim();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            BackColor = Background,
            Padding = new Padding(0, 8, 0, 0)
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);

        // Docked controls fill from the outside in, so add the innermost one first.
        Controls.Add(_input);
        Controls.Add(label);
        Controls.Add(buttons);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _input.Focus();
    }
}
