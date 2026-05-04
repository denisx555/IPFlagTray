using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace IPFlagTray;

/// <summary>
/// Контекст приложения, управляющий иконкой в системном трее.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMinutes(1);
    private readonly SocketsHttpHandler _httpHandler;
    private readonly HttpClient _httpClient;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _ipItem;
    private readonly ToolStripMenuItem _countryItem;
    private readonly ToolStripMenuItem _updatedItem;
    private readonly System.Windows.Forms.Timer _timer;
    private Icon? _currentIcon;
    private bool _isUpdating;
    private string? _previousCountryCode;
    private bool _hasShownRussianIpAlert;

    /// <summary>
    /// Инициализирует контекст приложения и запускает первое обновление.
    /// </summary>
    public TrayApplicationContext()
    {
        _httpHandler = new SocketsHttpHandler
        {
            // После смены VPN/маршрута старые соединения из пула могут давать устаревший «внешний» IP.
            PooledConnectionLifetime = TimeSpan.FromSeconds(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(2),
        };
        _httpClient = new HttpClient(_httpHandler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
        ConfigureHttpClient();

        _ipItem = new ToolStripMenuItem("IP: определяем...") { Enabled = false };
        _countryItem = new ToolStripMenuItem("Страна: определяем...") { Enabled = false };
        _updatedItem = new ToolStripMenuItem("Обновлено: -") { Enabled = false };

        var refreshItem = new ToolStripMenuItem("Обновить сейчас");
        refreshItem.Click += async (_, _) => await UpdateTrayInfoAsync();

        var openIpPageItem = new ToolStripMenuItem("Открыть проверку IP");
        openIpPageItem.Click += (_, _) => OpenIpPage();

        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => ExitThread();

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "IPFlagTray",
            Icon = SystemIcons.Information,
            ContextMenuStrip = new ContextMenuStrip()
        };

        _notifyIcon.ContextMenuStrip.Items.Add(_ipItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_countryItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_updatedItem);
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(refreshItem);
        _notifyIcon.ContextMenuStrip.Items.Add(openIpPageItem);
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(exitItem);

        _notifyIcon.DoubleClick += (_, _) => OpenIpPage();

        _timer = new System.Windows.Forms.Timer
        {
            Interval = (int)UpdateInterval.TotalMilliseconds
        };
        _timer.Tick += async (_, _) => await UpdateTrayInfoAsync();
        _timer.Start();

        _ = UpdateTrayInfoAsync();
    }

    /// <summary>
    /// Освобождает ресурсы при завершении приложения.
    /// </summary>
    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _timer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
        _httpClient.Dispose();

        base.ExitThreadCore();
    }

    /// <summary>
    /// Запрашивает текущий IP и обновляет текст и иконку в трее.
    /// </summary>
    private async Task UpdateTrayInfoAsync()
    {
        if (_isUpdating)
        {
            return;
        }

        _isUpdating = true;
        try
        {
            var info = await LoadIpInfoAsync();
            _ipItem.Text = $"IP: {info.IpAddress}";
            _countryItem.Text = $"Страна: {info.CountryName} ({info.CountryCode})";
            _updatedItem.Text = $"Обновлено: {DateTime.Now:HH:mm:ss}";

            var icon = await CreateFlagIconAsync(info.CountryCode);
            SetTrayIcon(icon);
            _notifyIcon.Text = $"IPFlagTray | {info.IpAddress} | {info.CountryCode}";

            if (info.CountryCode == "RU" && !_hasShownRussianIpAlert)
            {
                _hasShownRussianIpAlert = true;
                ShowAlertOnUiThread(info.IpAddress);
            }

            if (_previousCountryCode == "RU" && info.CountryCode != "RU")
            {
                _hasShownRussianIpAlert = false;
            }

            _previousCountryCode = info.CountryCode;
        }
        catch (Exception ex)
        {
            _ipItem.Text = "IP: ошибка получения";
            _countryItem.Text = "Страна: неизвестно";
            _updatedItem.Text = $"Обновлено: {DateTime.Now:HH:mm:ss}";
            SetTrayIcon(SystemIcons.Warning);
            _notifyIcon.Text = "IPFlagTray | Ошибка обновления";
            Debug.WriteLine($"Ошибка обновления IP: {ex}");
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// Загружает информацию о внешнем IP и стране.
    /// </summary>
    /// <returns>Данные внешнего IP.</returns>
    private async Task<IpInfo> LoadIpInfoAsync()
    {
        var externalIp = await LoadExternalIpFromIfConfigAsync();
        var ipWhoIsUrl = $"https://ipwho.is/{externalIp}?fields=success,message,country,country_code";

        using var responseMessage = await GetWithoutConnectionReuseAsync(ipWhoIsUrl);
        var rawBody = await responseMessage.Content.ReadAsStringAsync();

        if (!responseMessage.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ipwho.is вернул HTTP {(int)responseMessage.StatusCode} ({responseMessage.StatusCode}).");
        }

        var response = DeserializeIpWhoIsResponse(rawBody);

        if (response is null)
        {
            throw new InvalidOperationException("IP-сервис вернул пустой ответ.");
        }

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message ?? "IP-сервис вернул ошибку.");
        }

        if (string.IsNullOrWhiteSpace(response.CountryCode))
        {
            throw new InvalidOperationException("IP-сервис вернул неполные данные.");
        }

        return new IpInfo(
            externalIp,
            response.Country ?? "Неизвестно",
            response.CountryCode.ToUpperInvariant());
    }

    /// <summary>
    /// Загружает внешний IP-адрес через сервис ifconfig.me.
    /// </summary>
    /// <returns>Строковое представление внешнего IP-адреса.</returns>
    private async Task<string> LoadExternalIpFromIfConfigAsync()
    {
        const string ifConfigUrl = "https://ifconfig.me/ip";

        using var response = await GetWithoutConnectionReuseAsync(ifConfigUrl);
        var rawBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ifconfig.me вернул HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        var trimmedIp = rawBody.Trim();
        if (string.IsNullOrWhiteSpace(trimmedIp))
        {
            throw new InvalidOperationException("ifconfig.me вернул пустой IP-адрес.");
        }

        if (!IPAddress.TryParse(trimmedIp, out _))
        {
            throw new InvalidOperationException($"ifconfig.me вернул невалидный IP: {trimmedIp}");
        }

        return trimmedIp;
    }

    /// <summary>
    /// Создаёт иконку на основе флага страны.
    /// </summary>
    /// <param name="countryCode">Двухбуквенный код страны.</param>
    /// <returns>Иконка для трея.</returns>
    private async Task<Icon> CreateFlagIconAsync(string countryCode)
    {
        if (countryCode.Length < 2)
        {
            return SystemIcons.Application;
        }

        var lowerCode = countryCode.ToLowerInvariant();
        var flagUrl = $"https://flagcdn.com/w80/{lowerCode}.png";
        using var response = await GetWithoutConnectionReuseAsync(flagUrl);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var sourceBitmap = new Bitmap(stream);
        using var targetBitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(targetBitmap);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);
        graphics.DrawImage(sourceBitmap, new Rectangle(0, 0, 32, 32));

        var hIcon = targetBitmap.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// Заменяет текущую иконку в трее с корректным освобождением ресурсов.
    /// </summary>
    /// <param name="icon">Новая иконка.</param>
    private void SetTrayIcon(Icon icon)
    {
        var previous = _currentIcon;
        _currentIcon = (Icon)icon.Clone();
        _notifyIcon.Icon = _currentIcon;
        previous?.Dispose();
    }

    /// <summary>
    /// Открывает страницу проверки внешнего IP в браузере по умолчанию.
    /// </summary>
    private static void OpenIpPage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://ifconfig.me/",
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Настраивает параметры HTTP-клиента для запросов к внешним сервисам.
    /// </summary>
    private void ConfigureHttpClient()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("IPFlagTray/1.0 (+https://ifconfig.me)");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain;q=0.9, */*;q=0.8");
    }

    /// <summary>
    /// Выполняет GET-запрос без повторного использования TCP-соединения (Connection: close),
    /// чтобы после смены VPN не отдавался устаревший внешний IP из пула соединений.
    /// </summary>
    /// <param name="requestUri">Адрес запроса.</param>
    /// <returns>Ответ HTTP; вызывающий обязан освободить через using.</returns>
    private Task<HttpResponseMessage> GetWithoutConnectionReuseAsync(string requestUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.ConnectionClose = true;
        return _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    /// <summary>
    /// Десериализует JSON-ответ сервиса ipwho.is.
    /// </summary>
    /// <param name="rawBody">Текст ответа в формате JSON.</param>
    /// <returns>Десериализованная модель ответа.</returns>
    private static IpWhoIsResponse? DeserializeIpWhoIsResponse(string rawBody)
    {
        return System.Text.Json.JsonSerializer.Deserialize<IpWhoIsResponse>(
            rawBody,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
    }

    /// <summary>
    /// Внутренняя модель ответа сервиса ipwho.is.
    /// </summary>
    private sealed class IpWhoIsResponse
    {
        /// <summary>
        /// Признак успешного ответа.
        /// </summary>
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        /// <summary>
        /// Сообщение об ошибке, если запрос неуспешен.
        /// </summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        /// <summary>
        /// Название страны.
        /// </summary>
        [JsonPropertyName("country")]
        public string? Country { get; set; }

        /// <summary>
        /// Двухбуквенный код страны.
        /// </summary>
        [JsonPropertyName("country_code")]
        public string? CountryCode { get; set; }
    }

    /// <summary>
    /// Модель данных для отображения информации об IP.
    /// </summary>
    /// <param name="IpAddress">Внешний IP-адрес.</param>
    /// <param name="CountryName">Название страны.</param>
    /// <param name="CountryCode">Код страны.</param>
    private sealed record IpInfo(string IpAddress, string CountryName, string CountryCode);

    /// <summary>
    /// Нативные методы для работы с дескрипторами иконок.
    /// </summary>
    private static class NativeMethods
    {
        /// <summary>
        /// Освобождает дескриптор иконки, созданный WinAPI.
        /// </summary>
        /// <param name="hIcon">Дескриптор иконки.</param>
        /// <returns>Результат вызова WinAPI.</returns>
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }

    /// <summary>
    /// Показывает предупреждение о российском IP без скрытой «owner»-формы (иначе <c>Show()</c> делает её видимой — второе пустое окно).
    /// </summary>
    private void ShowAlertOnUiThread(string ipAddress)
    {
        try
        {
            using var alert = new AlertForm(ipAddress);
            alert.ShowDialog();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ошибка показа alert: {ex}");
        }
    }
}
