using System.Runtime.Versioning;

namespace RemoteHelper.Listener;

/// <summary>
/// The Windows pairing popup: a small, always-on-top window showing the
/// 6-digit code for the device that's trying to connect. Appears only
/// during pairing (once per device, ever), so it doesn't disturb the
/// otherwise-silent tray app. Server callbacks arrive on background
/// threads, so every UI touch is marshalled onto the WinForms thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPairingUI : IPairingUI
{
    private readonly Control _marshal;
    private readonly Dictionary<Guid, Form> _forms = new();

    public WindowsPairingUI(Control marshal) => _marshal = marshal;

    public void Show(Guid session, string deviceName, string pin)
    {
        _marshal.BeginInvoke(new Action(() =>
        {
            var form = BuildForm(deviceName, pin);
            _forms[session] = form;
            form.Show();
            form.Activate();
        }));
    }

    public void Close(Guid session, bool success)
    {
        _marshal.BeginInvoke(new Action(() =>
        {
            if (_forms.Remove(session, out var form))
            {
                form.Close();
                form.Dispose();
            }
        }));
    }

    private static Form BuildForm(string deviceName, string pin)
    {
        var form = new Form
        {
            Text = "Remote Helper — pairing",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true,
            ShowInTaskbar = true,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(380, 220),
            BackColor = Color.FromArgb(10, 10, 18),
        };

        var title = new Label
        {
            Text = $"“{deviceName}” wants to connect",
            ForeColor = Color.FromArgb(64, 230, 255),
            Font = new Font("Consolas", 12F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 56,
        };
        var code = new Label
        {
            Text = pin,
            ForeColor = Color.FromArgb(255, 90, 230),
            Font = new Font("Consolas", 44F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
        };
        var hint = new Label
        {
            Text = "Type this code on the device.\nYou'll only be asked once per device.",
            ForeColor = Color.Gray,
            Font = new Font("Consolas", 9F),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Bottom,
            Height = 48,
        };

        form.Controls.Add(code);
        form.Controls.Add(hint);
        form.Controls.Add(title);
        return form;
    }
}
