using System.Drawing;
using System.Net.Http;

namespace IPFlagTray;

internal sealed partial class AlertForm : Form
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string _ip;

    public AlertForm(string ip)
    {
        _ip = ip;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = false;
        ControlBox = false;
        Size = new Size(400, 200);
        BackColor = Color.White;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        var titleLabel = new Label
        {
            Text = "Внимание!",
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 53, 69),
            Location = new Point(120, 20),
            AutoSize = true
        };

        var messageLabel = new Label
        {
            Text = $"IP переключен на российский\n{_ip}",
            Font = new Font("Segoe UI", 12),
            ForeColor = Color.Black,
            Location = new Point(20, 65),
            AutoSize = true,
            Width = 360
        };

        var okButton = new Button
        {
            Text = "ОК",
            Size = new Size(100, 35),
            Location = new Point(150, 130),
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 123, 255),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10)
        };
        okButton.FlatAppearance.BorderSize = 0;

        var flagPictureBox = new PictureBox
        {
            Size = new Size(64, 48),
            Location = new Point(30, 25),
            SizeMode = PictureBoxSizeMode.StretchImage
        };

        Controls.Add(titleLabel);
        Controls.Add(messageLabel);
        Controls.Add(okButton);
        Controls.Add(flagPictureBox);

        Load += async (_, _) => await LoadFlagAsync(flagPictureBox);
    }

    private static async Task LoadFlagAsync(PictureBox pictureBox)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://flagcdn.com/w64/ru.png");
            request.Headers.ConnectionClose = true;
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            pictureBox.Image = new Bitmap(stream);
        }
        catch
        {
            pictureBox.Visible = false;
        }
    }
}