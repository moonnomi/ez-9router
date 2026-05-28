using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

ApplicationConfiguration.Initialize();
Application.Run(new TrayAppContext());

sealed class TrayAppContext : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly HotkeySink hotkeys;
    readonly AppSettings settings;
    readonly RouterClient router;

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
        menu.Items.Add("Answer selected text", null, async (_, _) => await RunAction(AppAction.AnswerSelection));
        menu.Items.Add("Snip mode", null, async (_, _) => await RunAction(AppAction.Snip));
        menu.Items.Add("Custom prompt for selection", null, async (_, _) => await RunAction(AppAction.CustomPrompt));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    async Task RunAction(AppAction action)
    {
        try
        {
            if (action == AppAction.Snip)
            {
                using var overlay = new SnipOverlay();
                if (overlay.ShowDialog() != DialogResult.OK || overlay.SnipBitmap == null) return;
                var snipPrompt = settings.SnipPrompt;
                var answer = await router.AskImageAsync(snipPrompt, overlay.SnipBitmap);
                new AnswerWindow(answer).Show();
                return;
            }

            var text = await ClipboardTools.GetSelectedTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                tray.ShowBalloonTip(1800, "ez-9router", "No selected text found.", ToolTipIcon.Info);
                return;
            }

            var prompt = action == AppAction.CustomPrompt
                ? PromptDialog.Ask("Custom prompt", "Prompt", settings.AnswerPrompt)
                : settings.AnswerPrompt;
            if (string.IsNullOrWhiteSpace(prompt)) return;

            var result = await router.AskTextAsync(prompt!, text);
            new AnswerWindow(result).Show();
        }
        catch (Exception ex)
        {
            tray.ShowBalloonTip(4000, "ez-9router error", ex.Message, ToolTipIcon.Error);
        }
    }

    void OpenSettings()
    {
        using var form = new SettingsForm(settings);
        if (form.ShowDialog() != DialogResult.OK) return;
        settings.Save();
        hotkeys.RegisterAll();
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

enum AppAction { AnswerSelection = 1, Snip = 2, CustomPrompt = 3 }

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
    public string CustomHotkey { get; set; } = "Ctrl+Alt+3";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(PathName)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathName)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

sealed class RouterClient
{
    readonly AppSettings settings;
    readonly HttpClient http = new();

    public RouterClient(AppSettings settings) => this.settings = settings;

    public Task<string> AskTextAsync(string prompt, string text)
    {
        object content = prompt + "\n\nSelected content:\n" + text;
        return AskAsync(content);
    }

    public Task<string> AskImageAsync(string prompt, Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Jpeg);
        var data = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        object content = new object[]
        {
            new { type = "text", text = prompt },
            new { type = "image_url", image_url = new { url = data } }
        };
        return AskAsync(content);
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
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
    }

    protected override void OnMouseDown(MouseEventArgs e) { start = e.Location; rect = new Rectangle(e.Location, Size.Empty); }
    protected override void OnMouseMove(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { rect = Normalize(start, e.Location); Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e)
    {
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
    readonly AppSettings settings;
    readonly Func<AppAction, Task> handler;
    readonly Dictionary<int, AppAction> actions = new();

    public HotkeySink(AppSettings settings, Func<AppAction, Task> handler)
    {
        this.settings = settings;
        this.handler = handler;
        CreateHandle(new CreateParams());
    }

    public void RegisterAll()
    {
        UnregisterAll();
        Register(1, settings.AnswerHotkey, AppAction.AnswerSelection);
        Register(2, settings.SnipHotkey, AppAction.Snip);
        Register(3, settings.CustomHotkey, AppAction.CustomPrompt);
    }

    void Register(int id, string chord, AppAction action)
    {
        if (!Hotkey.Parse(chord, out var mods, out var key)) return;
        if (RegisterHotKey(Handle, id, mods, key)) actions[id] = action;
    }

    void UnregisterAll()
    {
        foreach (var id in actions.Keys.ToArray()) UnregisterHotKey(Handle, id);
        actions.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && actions.TryGetValue(m.WParam.ToInt32(), out var action)) _ = handler(action);
        base.WndProc(ref m);
    }

    public void Dispose() { UnregisterAll(); DestroyHandle(); }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

static class Hotkey
{
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
            else if (part.StartsWith('F') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24) key = (uint)(0x70 + f - 1);
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