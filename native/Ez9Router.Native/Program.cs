using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Windows.Forms;

ApplicationConfiguration.Initialize();
Application.Run(new TrayAppContext());

sealed class TrayAppContext : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly HotkeySink hotkeys;
    readonly AppSettings settings;
    readonly RouterClient router;
    readonly Dictionary<int, List<Bitmap>> pendingSnips = new();
    readonly Dictionary<int, Rectangle> pendingSnipAnchors = new();

    public TrayAppContext()
    {
        settings = AppSettings.Load();
        router = new RouterClient(settings);
        tray = new NotifyIcon
        {
            Text = "ez-9router native",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        hotkeys = new HotkeySink(settings, RunAction);
        hotkeys.RegisterAll();
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Answer selected text", null, async (_, _) => await RunAction(new AppCommand(AppAction.AnswerSelection)));
        var snipPrompts = settings.GetSnipPrompts();
        for (var i = 0; i < snipPrompts.Count; i++)
        {
            var index = i;
            var title = string.IsNullOrWhiteSpace(snipPrompts[i].Title) ? $"Snip prompt {i + 1}" : snipPrompts[i].Title;
            menu.Items.Add($"{title} - store image", null, async (_, _) => await RunAction(new AppCommand(AppAction.StoreSnipImage, index)));
            menu.Items.Add($"{title} - capture & submit", null, async (_, _) => await RunAction(new AppCommand(AppAction.SubmitSnipImages, index)));
        }
        menu.Items.Add("Custom prompt for selection", null, async (_, _) => await RunAction(new AppCommand(AppAction.CustomPrompt)));
        menu.Items.Add("Toggle semi-stealth", null, async (_, _) => await RunAction(new AppCommand(AppAction.ToggleSemiStealth)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    async Task RunAction(AppCommand command)
    {
        try
        {
            if (command.Action == AppAction.ToggleSemiStealth)
            {
                settings.SemiStealthSnip = !settings.SemiStealthSnip;
                settings.Save();
                ToggleToast.Show(settings.SemiStealthSnip);
                return;
            }

            if (command.Action is AppAction.StoreSnipImage or AppAction.SubmitSnipImages)
            {
                await RunSnipAction(command);
                return;
            }

            var text = await ClipboardTools.GetSelectedTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                tray.ShowBalloonTip(1800, "ez-9router", "No selected text found.", ToolTipIcon.Info);
                return;
            }

            var prompt = command.Action == AppAction.CustomPrompt
                ? PromptDialog.Ask("Custom prompt", "Prompt", settings.AnswerPrompt)
                : settings.AnswerPrompt;
            if (string.IsNullOrWhiteSpace(prompt)) return;

            var result = await router.AskTextAsync(prompt!, text);
            new AnswerWindow2(result, settings.StealthMode, false, null, settings.TextBoxOpacity, settings.WindowLocation).Show();
        }
        catch (Exception ex)
        {
            tray.ShowBalloonTip(4000, "ez-9router error", ex.Message, ToolTipIcon.Error);
        }
    }

    async Task RunSnipAction(AppCommand command)
    {
        var capture = CaptureImage();
        if (capture == null) return;

        var index = Math.Clamp(command.SnipIndex, 0, settings.GetSnipPrompts().Count - 1);
        if (command.Action == AppAction.StoreSnipImage)
        {
            if (!pendingSnips.TryGetValue(index, out var stored)) pendingSnips[index] = stored = new List<Bitmap>();
            stored.Add(capture.Bitmap);
            pendingSnipAnchors[index] = capture.Bounds;
            if (settings.StealthMode) ToggleToast.Show($"stored #{stored.Count}");
            else tray.ShowBalloonTip(1200, "ez-9router", $"Stored image #{stored.Count}.", ToolTipIcon.Info);
            return;
        }

        var images = new List<Bitmap>();
        if (pendingSnips.TryGetValue(index, out var pending)) images.AddRange(pending);
        images.Add(capture.Bitmap);
        pendingSnips.Remove(index);
        var anchor = pendingSnipAnchors.TryGetValue(index, out var storedAnchor) ? storedAnchor : capture.Bounds;
        pendingSnipAnchors.Remove(index);

        try
        {
            var prompt = settings.GetSnipPrompts()[index].Prompt;
            var answer = await router.AskImagesAsync(prompt, images);
            new AnswerWindow2(answer, settings.StealthMode, settings.SemiStealthSnip, anchor, settings.TextBoxOpacity, settings.WindowLocation).Show();
        }
        finally
        {
            foreach (var image in images) image.Dispose();
        }
    }

    CaptureResult? CaptureImage()
    {
        if (settings.FullscreenScreenshotMode) return ScreenshotTools.CaptureVirtualScreen();
        using var overlay = new SnipOverlay2(settings.StealthMode, settings.SnipOutlineColorValue, settings.SnipCancelKeyValue);
        return overlay.ShowDialog() == DialogResult.OK && overlay.SnipBitmap != null
            ? new CaptureResult((Bitmap)overlay.SnipBitmap.Clone(), overlay.SnipScreenRect)
            : null;
    }
    void OpenSettings()
    {
        using var form = new SettingsForm2(settings, router);
        if (form.ShowDialog() != DialogResult.OK) return;
        settings.Save();
        hotkeys.RegisterAll();
        tray.ContextMenuStrip = BuildMenu();
        tray.ShowBalloonTip(1500, "ez-9router", "Settings saved.", ToolTipIcon.Info);
    }

    protected override void ExitThreadCore()
    {
        hotkeys.Dispose();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}

enum AppAction { AnswerSelection = 1, StoreSnipImage = 2, SubmitSnipImages = 3, CustomPrompt = 4, ToggleSemiStealth = 5 }
enum AnswerWindowLocation { Cursor = 0, Center = 1, TopLeft = 2, TopRight = 3, BottomLeft = 4, BottomRight = 5 }

sealed record AppCommand(AppAction Action, int SnipIndex = 0);

sealed class SnipPromptConfig
{
    public string Title { get; set; } = "Snip prompt";
    public string Prompt { get; set; } = "Analyze this browser snip and answer with the useful details.";
    public string Hotkey { get; set; } = "Ctrl+Alt+2";
    public string StoreHotkey { get; set; } = "Ctrl+Alt+Shift+2";
    public string SubmitHotkey { get; set; } = "Ctrl+Alt+2";
}

sealed class AppSettings
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ez-9router-native");
    static readonly string PathName = Path.Combine(Dir, "settings.json");

    public string Endpoint { get; set; } = "http://127.0.0.1:20128";
    public string ApiKey { get; set; } = "sk_9-router";
    public string Model { get; set; } = "cx/gpt-5.5";
    public string AnswerPrompt { get; set; } = "Answer the selected question clearly and concisely.";
    public string SnipPrompt { get; set; } = "Analyze this browser snip and answer with the useful details.";
    public string AnswerHotkey { get; set; } = "Ctrl+Alt+1";
    public string SnipHotkey { get; set; } = "Ctrl+Alt+2";
    public List<SnipPromptConfig> SnipPrompts { get; set; } = new();
    public string CustomHotkey { get; set; } = "Ctrl+Alt+3";
    public string SemiStealthHotkey { get; set; } = "Ctrl+Alt+4";
    public string SnipCancelKey { get; set; } = "Esc";
    public string SnipOutlineColor { get; set; } = "#ff2b2b";
    public double SnipOutlineOpacity { get; set; } = 1.0;
    public bool FullscreenScreenshotMode { get; set; }
    public int TextBoxHeight { get; set; } = 32;
    public double TextBoxOpacity { get; set; } = 1.0;
    public AnswerWindowLocation WindowLocation { get; set; } = AnswerWindowLocation.Cursor;
    public bool StealthMode { get; set; }
    public bool SemiStealthSnip { get; set; }

    [JsonIgnore]
    public Keys SnipCancelKeyValue => Hotkey.ParseKey(SnipCancelKey, out var key) ? key : Keys.Escape;

    [JsonIgnore]
    public Color SnipOutlineColorValue
    {
        get
        {
            try { return ColorTranslator.FromHtml(SnipOutlineColor); }
            catch { return Color.FromArgb(255, 43, 43); }
        }
    }
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(PathName))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathName)) ?? new AppSettings();
                settings.NormalizeSnipPrompts();
                return settings;
            }
        }
        catch { }
        var fresh = new AppSettings();
        fresh.NormalizeSnipPrompts();
        return fresh;
    }

    public List<SnipPromptConfig> GetSnipPrompts()
    {
        NormalizeSnipPrompts();
        return SnipPrompts;
    }

    public void NormalizeSnipPrompts()
    {
        if (SnipPrompts.Count == 0)
        {
            SnipPrompts.Add(new SnipPromptConfig { Title = "Snip prompt 1", Prompt = SnipPrompt, Hotkey = SnipHotkey, SubmitHotkey = SnipHotkey, StoreHotkey = "Ctrl+Alt+Shift+2" });
        }
        for (var i = 0; i < SnipPrompts.Count; i++)
        {
            SnipPrompts[i].Title = string.IsNullOrWhiteSpace(SnipPrompts[i].Title) ? $"Snip prompt {i + 1}" : SnipPrompts[i].Title.Trim();
            SnipPrompts[i].Prompt = string.IsNullOrWhiteSpace(SnipPrompts[i].Prompt) ? SnipPrompt : SnipPrompts[i].Prompt;
            SnipPrompts[i].Hotkey = string.IsNullOrWhiteSpace(SnipPrompts[i].Hotkey) ? $"Ctrl+Alt+{Math.Min(i + 2, 9)}" : SnipPrompts[i].Hotkey.Trim();
            SnipPrompts[i].SubmitHotkey = string.IsNullOrWhiteSpace(SnipPrompts[i].SubmitHotkey) ? SnipPrompts[i].Hotkey : SnipPrompts[i].SubmitHotkey.Trim();
            SnipPrompts[i].StoreHotkey = string.IsNullOrWhiteSpace(SnipPrompts[i].StoreHotkey) ? $"Ctrl+Alt+Shift+{Math.Min(i + 2, 9)}" : SnipPrompts[i].StoreHotkey.Trim();
        }
        SnipPrompt = SnipPrompts[0].Prompt;
        SnipHotkey = SnipPrompts[0].SubmitHotkey;
        TextBoxHeight = Math.Clamp(TextBoxHeight, 22, 120);
        TextBoxOpacity = Math.Clamp(TextBoxOpacity, 0.1, 1.0);
        SnipOutlineOpacity = Math.Clamp(SnipOutlineOpacity, 0.1, 1.0);
    }

    public void Save()
    {
        NormalizeSnipPrompts();
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

sealed record CaptureResult(Bitmap Bitmap, Rectangle Bounds);

static class ScreenshotTools
{
    public static CaptureResult CaptureVirtualScreen()
    {
        var bounds = SystemInformation.VirtualScreen;
        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return new CaptureResult(bitmap, bounds);
    }
}

sealed class RouterClient
{
    readonly AppSettings settings;
    readonly HttpClient http = new();

    public RouterClient(AppSettings settings) => this.settings = settings;


    public async Task<List<string>> FetchModelsAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, settings.Endpoint.TrimEnd('/') + "/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        using var res = await http.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(raw);
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(x => x.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList();
    }
    public Task<string> AskTextAsync(string prompt, string text)
    {
        object content = prompt + "\n\nSelected content:\n" + text;
        return AskAsync(content);
    }

    public Task<string> AskImageAsync(string prompt, Bitmap bitmap) => AskImagesAsync(prompt, new[] { bitmap });

    public Task<string> AskImagesAsync(string prompt, IEnumerable<Bitmap> bitmaps)
    {
        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (var bitmap in bitmaps)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Jpeg);
            var data = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
            content.Add(new { type = "image_url", image_url = new { url = data } });
        }
        return AskAsync(content.ToArray());
    }

    async Task<string> AskAsync(object userContent)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint.TrimEnd('/') + "/v1/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        var payload = new
        {
            model = settings.Model,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = "You are a precise desktop assistant. Return a direct, useful answer." },
                new { role = "user", content = userContent }
            }
        };
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(raw);
        var body = JsonSerializer.Deserialize<ChatResponse>(raw);
        return body?.Choices?.FirstOrDefault()?.Message?.Content ?? "No answer.";
    }
}

sealed class ChatResponse
{
    [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
}
sealed class Choice
{
    [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
}
sealed class ChatMessage
{
    [JsonPropertyName("content")] public string? Content { get; set; }
}

sealed class AnswerWindow : Form
{
    public AnswerWindow(string answer)
    {
        Text = "ez-9router";
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        Width = 560;
        Height = 360;
        TopMost = true;
        BackColor = Color.FromArgb(18, 17, 15);
        ForeColor = Color.White;
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Text = answer,
            BackColor = Color.FromArgb(25, 23, 20),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 10.5f),
            Margin = new Padding(12)
        };
        Controls.Add(box);
    }
}

sealed class SettingsForm : Form
{
    readonly AppSettings settings;
    readonly Dictionary<string, TextBox> fields = new();

    public SettingsForm(AppSettings settings)
    {
        this.settings = settings;
        Text = "ez-9router settings";
        Width = 520;
        Height = 540;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14), AutoScroll = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(panel);

        Add(panel, "Endpoint", settings.Endpoint);
        Add(panel, "API key", settings.ApiKey, true);
        Add(panel, "Model", settings.Model);
        Add(panel, "Answer prompt", settings.AnswerPrompt);
        Add(panel, "Snip prompt", settings.SnipPrompt);
        Add(panel, "Answer hotkey", settings.AnswerHotkey);
        Add(panel, "Snip hotkey", settings.SnipHotkey);
        Add(panel, "Custom hotkey", settings.CustomHotkey);

        var save = new Button { Text = "Save", Dock = DockStyle.Fill, Height = 34 };
        save.Click += (_, _) => SaveAndClose();
        panel.Controls.Add(new Label());
        panel.Controls.Add(save);
    }

    void Add(TableLayoutPanel panel, string label, string value, bool password = false)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
        var box = new TextBox { Text = value, Dock = DockStyle.Fill, UseSystemPasswordChar = password };
        fields[label] = box;
        panel.Controls.Add(box);
    }

    void SaveAndClose()
    {
        settings.Endpoint = fields["Endpoint"].Text.Trim();
        settings.ApiKey = fields["API key"].Text.Trim();
        settings.Model = fields["Model"].Text.Trim();
        settings.AnswerPrompt = fields["Answer prompt"].Text.Trim();
        settings.SnipPrompt = fields["Snip prompt"].Text.Trim();
        settings.AnswerHotkey = fields["Answer hotkey"].Text.Trim();
        settings.SnipHotkey = fields["Snip hotkey"].Text.Trim();
        settings.CustomHotkey = fields["Custom hotkey"].Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }
}

sealed class SnipOverlay : Form
{
    Point start;
    Rectangle rect;
    public Bitmap? SnipBitmap { get; private set; }

    public SnipOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        Opacity = .22;
        BackColor = Color.Black;
        DoubleBuffered = true;
    }

    protected override void OnMouseDown(MouseEventArgs e) { start = e.Location; rect = new Rectangle(e.Location, Size.Empty); }
    protected override void OnMouseMove(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { rect = Normalize(start, e.Location); Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; Close(); return; }
        if (e.Button != MouseButtons.Left) return;
        rect = Normalize(start, e.Location);
        if (rect.Width < 8 || rect.Height < 8) { DialogResult = DialogResult.Cancel; Close(); return; }
        Hide();
        Thread.Sleep(80);
        SnipBitmap = new Bitmap(rect.Width, rect.Height);
        using var g = Graphics.FromImage(SnipBitmap);
        g.CopyFromScreen(PointToScreen(rect.Location), Point.Empty, rect.Size);
        DialogResult = DialogResult.OK;
        Close();
    }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } }
    protected override void OnPaint(PaintEventArgs e) { using var pen = new Pen(Color.OrangeRed, 2); e.Graphics.DrawRectangle(pen, rect); }
    static Rectangle Normalize(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}

sealed class HotkeySink : NativeWindow, IDisposable
{
    const int WmHotkey = 0x0312;
    const int WhKeyboardLl = 13;
    const int WhMouseLl = 14;
    const int WmKeydown = 0x0100;
    const int WmSyskeydown = 0x0104;
    const int WmXbuttondown = 0x020B;
    const uint VkXbutton1 = 0x05;
    const uint VkXbutton2 = 0x06;
    const uint ModAlt = 0x0001;
    const uint ModCtrl = 0x0002;
    const uint ModShift = 0x0004;
    const uint ModWin = 0x0008;
    const uint ModNoRepeat = 0x4000;

    readonly AppSettings settings;
    readonly Func<AppCommand, Task> handler;
    readonly Dictionary<int, AppCommand> actions = new();
    readonly Dictionary<(uint Mods, uint Key), AppCommand> hookActions = new();
    readonly Dictionary<AppCommand, long> lastFire = new();
    readonly LowLevelProc keyboardHookProc;
    readonly LowLevelProc mouseHookProc;
    IntPtr keyboardHookHandle;
    IntPtr mouseHookHandle;

    public HotkeySink(AppSettings settings, Func<AppCommand, Task> handler)
    {
        this.settings = settings;
        this.handler = handler;
        keyboardHookProc = KeyboardHook;
        mouseHookProc = MouseHook;
        CreateHandle(new CreateParams());
        EnsureHook();
    }

    public void RegisterAll()
    {
        UnregisterAll();
        Register(1, settings.AnswerHotkey, new AppCommand(AppAction.AnswerSelection));
        var snipPrompts = settings.GetSnipPrompts();
        for (var i = 0; i < snipPrompts.Count; i++)
        {
            Register(100 + i, snipPrompts[i].StoreHotkey, new AppCommand(AppAction.StoreSnipImage, i));
            Register(200 + i, snipPrompts[i].SubmitHotkey, new AppCommand(AppAction.SubmitSnipImages, i));
        }
        Register(3, settings.CustomHotkey, new AppCommand(AppAction.CustomPrompt));
        Register(4, settings.SemiStealthHotkey, new AppCommand(AppAction.ToggleSemiStealth));
    }

    void Register(int id, string chord, AppCommand action)
    {
        if (!Hotkey.Parse(chord, out var mods, out var key)) return;
        hookActions[(mods, key)] = action;
        if (key is VkXbutton1 or VkXbutton2) return;
        if (RegisterHotKey(Handle, id, mods | ModNoRepeat, key)) actions[id] = action;
        else if (RegisterHotKey(Handle, id, mods, key)) actions[id] = action;
    }

    void UnregisterAll()
    {
        foreach (var id in actions.Keys.ToArray()) UnregisterHotKey(Handle, id);
        actions.Clear();
        hookActions.Clear();
        lastFire.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && actions.TryGetValue(m.WParam.ToInt32(), out var action)) Fire(action);
        base.WndProc(ref m);
    }

    void Fire(AppCommand action)
    {
        var now = Environment.TickCount64;
        if (lastFire.TryGetValue(action, out var previous) && now - previous < 350) return;
        lastFire[action] = now;
        _ = handler(action);
    }

    void EnsureHook()
    {
        if (keyboardHookHandle == IntPtr.Zero) keyboardHookHandle = SetWindowsHookEx(WhKeyboardLl, keyboardHookProc, IntPtr.Zero, 0);
        if (mouseHookHandle == IntPtr.Zero) mouseHookHandle = SetWindowsHookEx(WhMouseLl, mouseHookProc, IntPtr.Zero, 0);
    }

    IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WmKeydown || wParam == (IntPtr)WmSyskeydown))
        {
            var key = (uint)Marshal.ReadInt32(lParam);
            if (hookActions.TryGetValue((CurrentMods(), key), out var action)) Fire(action);
        }
        return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);
    }

    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WmXbuttondown)
        {
            var mouseData = Marshal.ReadInt32(lParam, 8);
            var button = (mouseData >> 16) & 0xffff;
            var key = button == 1 ? VkXbutton1 : button == 2 ? VkXbutton2 : 0;
            if (key != 0 && hookActions.TryGetValue((CurrentMods(), key), out var action)) Fire(action);
        }
        return CallNextHookEx(mouseHookHandle, nCode, wParam, lParam);
    }

    static uint CurrentMods()
    {
        uint mods = 0;
        if (Down(Keys.Menu)) mods |= ModAlt;
        if (Down(Keys.ControlKey)) mods |= ModCtrl;
        if (Down(Keys.ShiftKey)) mods |= ModShift;
        if (Down(Keys.LWin) || Down(Keys.RWin)) mods |= ModWin;
        return mods;
    }

    static bool Down(Keys key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    public void Dispose()
    {
        UnregisterAll();
        if (keyboardHookHandle != IntPtr.Zero) UnhookWindowsHookEx(keyboardHookHandle);
        if (mouseHookHandle != IntPtr.Zero) UnhookWindowsHookEx(mouseHookHandle);
        DestroyHandle();
    }

    delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
}
static class Hotkey
{
    public static bool ParseKey(string text, out Keys key)
    {
        key = Keys.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var part = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()?.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(part)) return false;
        if (part.Length == 1) { key = (Keys)part[0]; return true; }
        if (part == "ESC") { key = Keys.Escape; return true; }
        if (part is "MOUSE4" or "XBUTTON1" or "X1") { key = Keys.XButton1; return true; }
        if (part is "MOUSE5" or "XBUTTON2" or "X2") { key = Keys.XButton2; return true; }
        if (part.StartsWith("NUM") && int.TryParse(part[3..], out var n) && n is >= 0 and <= 9) { key = Keys.NumPad0 + n; return true; }
        if (part.StartsWith('F') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24) { key = Keys.F1 + f - 1; return true; }
        return Enum.TryParse(part, true, out key);
    }

    public static bool Parse(string text, out uint mods, out uint key)
    {
        mods = 0; key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.ToUpperInvariant();
            if (part is "CTRL" or "CONTROL") mods |= 0x0002;
            else if (part == "ALT") mods |= 0x0001;
            else if (part == "SHIFT") mods |= 0x0004;
            else if (part == "WIN") mods |= 0x0008;
            else if (part.Length == 1) key = part[0];
            else if (part == "ESC") key = (uint)Keys.Escape;
            else if (part is "MOUSE4" or "XBUTTON1" or "X1") key = 0x05;
            else if (part is "MOUSE5" or "XBUTTON2" or "X2") key = 0x06;
            else if (part.StartsWith("NUM") && int.TryParse(part[3..], out var n) && n is >= 0 and <= 9) key = (uint)(Keys.NumPad0 + n);
            else if (part.StartsWith('F') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24) key = (uint)(0x70 + f - 1);
            else if (Enum.TryParse<Keys>(part, true, out var parsed)) key = (uint)parsed;
        }
        return key != 0;
    }
}

static class ClipboardTools
{
    public static async Task<string> GetSelectedTextAsync()
    {
        string? previous = null;
        try { if (Clipboard.ContainsText()) previous = Clipboard.GetText(); } catch { }
        SendKeys.SendWait("^c");
        await Task.Delay(160);
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (previous != null && text != previous) Clipboard.SetText(previous);
        return text;
    }
}

static class PromptDialog
{
    public static string? Ask(string title, string label, string value)
    {
        using var form = new Form { Text = title, Width = 430, Height = 160, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog };
        var box = new TextBox { Left = 16, Top = 38, Width = 380, Text = value };
        form.Controls.Add(new Label { Left = 16, Top = 14, Width = 380, Text = label });
        form.Controls.Add(box);
        var ok = new Button { Text = "OK", Left = 286, Width = 110, Top = 72, DialogResult = DialogResult.OK };
        form.Controls.Add(ok);
        form.AcceptButton = ok;
        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }
}
sealed class AnswerWindow2 : Form
{
    readonly bool closeOnHoverX;
    public AnswerWindow2(string answer, bool stealth, bool persistentStealth, Rectangle? anchor = null, double opacity = 1.0, AnswerWindowLocation locationMode = AnswerWindowLocation.Cursor)
    {
        StartPosition = stealth ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        Width = stealth ? 430 : 560;
        Height = stealth ? 220 : 360;
        KeyPreview = true;
        closeOnHoverX = stealth && persistentStealth;
        Opacity = opacity;
        if (stealth)
        {
            var font = new Font("Arial", 10.5f);
            var padding = 12;
            var maxW = 430;
            var maxH = 220;
            var textSize = TextRenderer.MeasureText(answer, font, new Size(maxW - padding * 2, maxH - padding * 2), TextFormatFlags.WordBreak);
            Width = Math.Max(32, Math.Min(maxW, textSize.Width + padding * 2));
            Height = Math.Max(32, Math.Min(maxH, textSize.Height + padding * 2));

            var area = anchor.HasValue ? Screen.FromRectangle(anchor.Value).WorkingArea : Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = locationMode switch
            {
                AnswerWindowLocation.Center => new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2),
                AnswerWindowLocation.TopLeft => new Point(area.Left + 14, area.Top + 14),
                AnswerWindowLocation.TopRight => new Point(area.Right - Width - 14, area.Top + 14),
                AnswerWindowLocation.BottomLeft => new Point(area.Left + 14, area.Bottom - Height - 14),
                AnswerWindowLocation.BottomRight => new Point(area.Right - Width - 14, area.Bottom - Height - 14),
                _ => anchor.HasValue ? ClampNearRect(area, anchor.Value, new Size(Width, Height)) : ClampNearCursor(area, new Size(Width, Height))
            };
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.White;
            ForeColor = Color.Black;
            Controls.Add(new Label { Dock = DockStyle.Fill, Text = answer, BackColor = Color.White, ForeColor = Color.Black, Font = font, Padding = new Padding(padding), AutoEllipsis = true });
            if (!persistentStealth)
            {
                var timer = new System.Windows.Forms.Timer { Interval = 1000 };
                timer.Tick += (_, _) => { timer.Stop(); Close(); };
                timer.Start();
            }
            return;
        }
        Text = "ez-9router";
        BackColor = Color.FromArgb(18, 17, 15);
        ForeColor = Color.White;
        Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = answer, BackColor = Color.FromArgb(25, 23, 20), ForeColor = Color.White, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10.5f), Margin = new Padding(12) });
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (closeOnHoverX && e.KeyCode == Keys.X && ClientRectangle.Contains(PointToClient(Cursor.Position))) Close();
        base.OnKeyDown(e);
    }

    static Point ClampNearCursor(Rectangle area, Size size)
    {
        var x = Math.Min(Cursor.Position.X + 14, area.Right - size.Width);
        var y = Math.Min(Cursor.Position.Y + 14, area.Bottom - size.Height);
        return new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
    }

    static Point ClampNearRect(Rectangle area, Rectangle anchor, Size size)
    {
        var rightX = anchor.Right + 12;
        var leftX = anchor.Left - size.Width - 12;
        var x = rightX + size.Width <= area.Right ? rightX : leftX;
        var y = Math.Min(anchor.Top, area.Bottom - size.Height);
        return new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
    }
}

sealed class SettingsForm2 : Form
{
    readonly AppSettings settings;
    readonly RouterClient router;
    readonly Dictionary<string, Control> fields = new();
    int snipPromptCount;
    readonly Color bg = Color.FromArgb(18, 17, 15);
    readonly Color panel = Color.FromArgb(31, 29, 25);
    readonly Color text = Color.FromArgb(248, 240, 232);
    readonly Color muted = Color.FromArgb(185, 170, 160);
    readonly Color accent = Color.FromArgb(255, 76, 37);

    public SettingsForm2(AppSettings settings, RouterClient router)
    {
        this.settings = settings;
        this.router = router;
        Text = "ez-9router settings";
        Width = 560;
        Height = 690;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = bg;
        ForeColor = text;

        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16), FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = bg };
        Controls.Add(root);
        root.Controls.Add(new Label { Text = "ez-9router", Font = new Font("Segoe UI", 20, FontStyle.Bold), ForeColor = text, Width = 500, Height = 42 });

        var conn = Card("Connection");
        AddText(conn, "Endpoint", settings.Endpoint);
        AddText(conn, "API key", settings.ApiKey, true);
        AddModel(conn);
        var refresh = Button("Refresh models");
        refresh.Click += async (_, _) => await LoadModelsAsync();
        conn.Controls.Add(refresh);
        root.Controls.Add(conn);

        var modes = Card("Modes");
        AddCheck(modes, "Stealth mode", settings.StealthMode);
        AddCheck(modes, "Semi-stealth snip", settings.SemiStealthSnip);
        AddCheck(modes, "Fullscreen screenshots", settings.FullscreenScreenshotMode);
        AddText(modes, "Text box height", settings.TextBoxHeight.ToString());
        AddText(modes, "Text box opacity", settings.TextBoxOpacity.ToString("0.0#"));
        AddEnum(modes, "Window location", settings.WindowLocation);
        root.Controls.Add(modes);

        var prompts = Card("Prompts");
        AddText(prompts, "Answer prompt", settings.AnswerPrompt, false, 64);
        AddText(prompts, "Snip outline color", settings.SnipOutlineColor);
        AddText(prompts, "Snip outline opacity", settings.SnipOutlineOpacity.ToString("0.0#"));
        foreach (var slot in settings.GetSnipPrompts()) AddSnipPrompt(prompts, slot);
        var addSnip = Button("Add snip prompt");
        addSnip.Click += (_, _) =>
        {
            prompts.Controls.Remove(addSnip);
            AddSnipPrompt(prompts, new SnipPromptConfig { Title = $"Snip prompt {snipPromptCount + 1}", StoreHotkey = NextSnipStoreHotkey(), SubmitHotkey = NextSnipHotkey(), Hotkey = NextSnipHotkey() });
            prompts.Controls.Add(addSnip);
        };
        prompts.Controls.Add(addSnip);
        root.Controls.Add(prompts);

        var keys = Card("Hotkeys");
        AddHotkey(keys, "Answer hotkey", settings.AnswerHotkey);
        AddHotkey(keys, "Custom hotkey", settings.CustomHotkey);
        AddHotkey(keys, "Semi-stealth toggle", settings.SemiStealthHotkey);
        AddHotkey(keys, "Snip cancel key", settings.SnipCancelKey);
        root.Controls.Add(keys);

        var save = Button("Save");
        save.Width = 500;
        save.Height = 38;
        save.Click += (_, _) => SaveAndClose();
        root.Controls.Add(save);
        Shown += async (_, _) => await LoadModelsAsync();
    }

    FlowLayoutPanel Card(string title)
    {
        var card = new FlowLayoutPanel { Width = 500, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = panel, Padding = new Padding(12), Margin = new Padding(0, 8, 0, 8) };
        card.Controls.Add(new Label { Text = title, Width = 460, Height = 24, ForeColor = text, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold) });
        return card;
    }

    void AddText(FlowLayoutPanel card, string label, string value, bool password = false, int height = 32)
    {
        card.Controls.Add(new Label { Text = label, Width = 460, Height = 18, ForeColor = muted });
        var actualHeight = height > 40 ? height : settings.TextBoxHeight;
        var box = new TextBox { Text = value, Width = height > 40 ? 460 : 280, Height = actualHeight, UseSystemPasswordChar = password, BackColor = Color.FromArgb(20, 19, 17), ForeColor = text, BorderStyle = BorderStyle.FixedSingle, Multiline = height > 40 || actualHeight > 32 };
        fields[label] = box;
        card.Controls.Add(box);
    }

    void AddSnipPrompt(FlowLayoutPanel card, SnipPromptConfig slot)
    {
        var index = ++snipPromptCount;
        card.Controls.Add(new Label { Text = $"Snip prompt {index}", Width = 460, Height = 22, ForeColor = text, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 0) });
        AddText(card, $"Snip {index} title", slot.Title);
        AddText(card, $"Snip {index} prompt", slot.Prompt, false, 64);
        AddHotkey(card, $"Snip {index} store hotkey", slot.StoreHotkey);
        AddHotkey(card, $"Snip {index} submit hotkey", slot.SubmitHotkey);
    }

    string NextSnipHotkey() => $"Ctrl+Alt+{Math.Min(snipPromptCount + 2, 9)}";
    string NextSnipStoreHotkey() => $"Ctrl+Alt+Shift+{Math.Min(snipPromptCount + 2, 9)}";
    void AddEnum<T>(FlowLayoutPanel card, string label, T value) where T : struct, Enum
    {
        card.Controls.Add(new Label { Text = label, Width = 460, Height = 18, ForeColor = muted });
        var combo = new ComboBox { Width = 460, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(20, 19, 17), ForeColor = text };
        foreach (var name in Enum.GetNames<T>()) combo.Items.Add(name);
        combo.Text = value.ToString();
        fields[label] = combo;
        card.Controls.Add(combo);
    }
    void AddModel(FlowLayoutPanel card)
    {
        card.Controls.Add(new Label { Text = "Model", Width = 460, Height = 18, ForeColor = muted });
        var combo = new ComboBox { Width = 460, DropDownStyle = ComboBoxStyle.DropDown, BackColor = Color.FromArgb(20, 19, 17), ForeColor = text };
        combo.Items.Add(settings.Model);
        combo.Text = settings.Model;
        fields["Model"] = combo;
        card.Controls.Add(combo);
    }

    void AddCheck(FlowLayoutPanel card, string label, bool value)
    {
        var cb = new CheckBox { Text = label, Checked = value, Width = 460, Height = 28, ForeColor = text, BackColor = panel };
        fields[label] = cb;
        card.Controls.Add(cb);
    }

    void AddHotkey(FlowLayoutPanel card, string label, string value)
    {
        card.Controls.Add(new Label { Text = label, Width = 460, Height = 18, ForeColor = muted });
        var box = new HotkeyBox { Text = value, Width = 460, Height = 32, BackColor = Color.FromArgb(20, 19, 17), ForeColor = text, BorderStyle = BorderStyle.FixedSingle, ReadOnly = true };
        fields[label] = box;
        card.Controls.Add(box);
    }

    Button Button(string label) => new() { Text = label, Width = 460, Height = 34, BackColor = accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };

    async Task LoadModelsAsync()
    {
        if (fields["Model"] is not ComboBox combo) return;
        try
        {
            var selected = combo.Text;
            combo.Items.Clear();
            foreach (var item in await router.FetchModelsAsync()) combo.Items.Add(item);
            combo.Text = selected;
        }
        catch { }
    }

    void SaveAndClose()
    {
        settings.Endpoint = ((TextBox)fields["Endpoint"]).Text.Trim();
        settings.ApiKey = ((TextBox)fields["API key"]).Text.Trim();
        settings.Model = ((ComboBox)fields["Model"]).Text.Trim();
        settings.AnswerPrompt = ((TextBox)fields["Answer prompt"]).Text.Trim();
        settings.SnipOutlineColor = ((TextBox)fields["Snip outline color"]).Text.Trim();
        if (double.TryParse(((TextBox)fields["Snip outline opacity"]).Text.Trim(), out var snipOutlineOpacity)) settings.SnipOutlineOpacity = snipOutlineOpacity;
        settings.SnipPrompts = Enumerable.Range(1, snipPromptCount)
            .Select(i => new SnipPromptConfig
            {
                Title = ((TextBox)fields[$"Snip {i} title"]).Text.Trim(),
                Prompt = ((TextBox)fields[$"Snip {i} prompt"]).Text.Trim(),
                Hotkey = ((TextBox)fields[$"Snip {i} submit hotkey"]).Text.Trim(),
                StoreHotkey = ((TextBox)fields[$"Snip {i} store hotkey"]).Text.Trim(),
                SubmitHotkey = ((TextBox)fields[$"Snip {i} submit hotkey"]).Text.Trim()
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Prompt))
            .ToList();
        settings.NormalizeSnipPrompts();
        settings.AnswerHotkey = ((TextBox)fields["Answer hotkey"]).Text.Trim();
        settings.CustomHotkey = ((TextBox)fields["Custom hotkey"]).Text.Trim();
        settings.SemiStealthHotkey = ((TextBox)fields["Semi-stealth toggle"]).Text.Trim();
        settings.SnipCancelKey = ((TextBox)fields["Snip cancel key"]).Text.Trim();
        settings.StealthMode = ((CheckBox)fields["Stealth mode"]).Checked;
        settings.SemiStealthSnip = ((CheckBox)fields["Semi-stealth snip"]).Checked;
        settings.FullscreenScreenshotMode = ((CheckBox)fields["Fullscreen screenshots"]).Checked;
        if (int.TryParse(((TextBox)fields["Text box height"]).Text.Trim(), out var textBoxHeight)) settings.TextBoxHeight = textBoxHeight;
        if (double.TryParse(((TextBox)fields["Text box opacity"]).Text.Trim(), out var textBoxOpacity)) settings.TextBoxOpacity = textBoxOpacity;
        if (Enum.TryParse<AnswerWindowLocation>(((ComboBox)fields["Window location"]).Text, out var loc)) settings.WindowLocation = loc;
        DialogResult = DialogResult.OK;
        Close();
    }
}

sealed class HotkeyBox : TextBox
{
    protected override void WndProc(ref Message m)
    {
        const int wmXbuttondown = 0x020B;
        if (m.Msg == wmXbuttondown)
        {
            var button = ((m.WParam.ToInt64() >> 16) & 0xffff) == 1 ? "Mouse4" : "Mouse5";
            Text = BuildChord(button);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button is MouseButtons.XButton1 or MouseButtons.XButton2)
        {
            Text = BuildChord(e.Button == MouseButtons.XButton1 ? "Mouse4" : "Mouse5");
            return;
        }
        base.OnMouseDown(e);
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey) return true;
        Text = BuildChord(FormatKey(key));
        return true;
    }

    static string BuildChord(string key)
    {
        var parts = new List<string>();
        if (ModifierKeys.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (ModifierKeys.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (ModifierKeys.HasFlag(Keys.Shift)) parts.Add("Shift");
        parts.Add(key);
        return string.Join("+", parts);
    }

    static string FormatKey(Keys key)
    {
        if (key is >= Keys.D0 and <= Keys.D9) return ((int)(key - Keys.D0)).ToString();
        if (key is >= Keys.NumPad0 and <= Keys.NumPad9) return "Num" + (int)(key - Keys.NumPad0);
        return key switch
        {
            Keys.Space => "Space",
            Keys.Return => "Enter",
            Keys.Escape => "Esc",
            Keys.XButton1 => "Mouse4",
            Keys.XButton2 => "Mouse5",
            _ => key.ToString()
        };
    }
}

sealed class SnipOverlay2 : Form
{
    readonly bool quiet;
    readonly SnipGuideWindow? guide;
    readonly Color outline;
    Point start;
    Rectangle rect;
    public Bitmap? SnipBitmap { get; private set; }
    public Rectangle SnipScreenRect { get; private set; }
    readonly Keys cancelKey;
    public SnipOverlay2(bool stealth, Color outlineColor, Keys cancelKey)
    {
        quiet = stealth;
        outline = outlineColor;
        this.cancelKey = cancelKey;
        guide = new SnipGuideWindow(outlineColor);
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        Opacity = stealth ? .01 : .22;
        BackColor = Color.Black;
        TransparencyKey = Color.Empty;
        DoubleBuffered = true;
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { Cancel(); return; }
        if (e.Button != MouseButtons.Left) return;
        start = e.Location;
        rect = new Rectangle(e.Location, Size.Empty);
        guide?.ShowAt(PointToScreen(e.Location));
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) guide?.ShowAt(PointToScreen(e.Location));
        if (e.Button == MouseButtons.Left)
        {
            rect = Normalize(start, e.Location);
            guide?.ShowRect(ToScreen(rect));
            Invalidate();
        }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { Cancel(); return; }
        if (e.Button != MouseButtons.Left) return;
        rect = Normalize(start, e.Location);
        if (rect.Width < 8 || rect.Height < 8) { DialogResult = DialogResult.Cancel; Close(); return; }
        SnipScreenRect = ToScreen(rect);
        guide?.Hide();
        Hide();
        Thread.Sleep(80);
        SnipBitmap = new Bitmap(rect.Width, rect.Height);
        using var g = Graphics.FromImage(SnipBitmap);
        g.CopyFromScreen(PointToScreen(rect.Location), Point.Empty, rect.Size);
        DialogResult = DialogResult.OK;
        Close();
    }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == cancelKey) Cancel(); }
    void Cancel() { DialogResult = DialogResult.Cancel; Close(); }
    protected override void OnFormClosed(FormClosedEventArgs e) { guide?.Dispose(); base.OnFormClosed(e); }
    protected override void OnPaint(PaintEventArgs e) { using var pen = new Pen(quiet ? outline : Color.OrangeRed, quiet ? 1 : 2); e.Graphics.DrawRectangle(pen, rect); }
    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= wsExToolWindow;
            return cp;
        }
    }
    Rectangle ToScreen(Rectangle value) => new(PointToScreen(value.Location), value.Size);
    static Rectangle Normalize(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}

sealed class ToggleToast : Form
{
    public static void Show(bool value) => Show(value ? "true" : "false");

    public static void Show(string text)
    {
        var toast = new ToggleToast(text);
        toast.Show();
    }

    ToggleToast(string value)
    {
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        Width = 96;
        Height = 42;
        BackColor = Color.White;
        ForeColor = Color.Black;
        var area = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        Location = new Point(area.Right - Width - 18, area.Bottom - Height - 18);
        Controls.Add(new Label { Dock = DockStyle.Fill, Text = value, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Arial", 12, FontStyle.Bold), BackColor = Color.White, ForeColor = Color.Black });
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => { timer.Stop(); Close(); Dispose(); };
        timer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExNoActivate = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= wsExToolWindow | wsExNoActivate;
            return cp;
        }
    }
}
sealed class SnipGuideWindow : Form
{
    readonly Color outline;
    public SnipGuideWindow(Color outlineColor)
    {
        outline = outlineColor;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Size = new Size(18, 18);
        DoubleBuffered = true;
    }

    public void ShowAt(Point cursor)
    {
        Bounds = new Rectangle(cursor.X + 12, cursor.Y + 12, 18, 18);
        if (!Visible) Show();
        Invalidate();
    }

    public void ShowRect(Rectangle rect)
    {
        Bounds = rect.Width < 8 || rect.Height < 8
            ? new Rectangle(Cursor.Position.X + 12, Cursor.Position.Y + 12, 18, 18)
            : rect;
        if (!Visible) Show();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(outline, 2);
        e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExNoActivate = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= wsExToolWindow | wsExNoActivate;
            return cp;
        }
    }
}
