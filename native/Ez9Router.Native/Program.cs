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
                using var overlay = new SnipOverlay2(settings.StealthMode);
                if (overlay.ShowDialog() != DialogResult.OK || overlay.SnipBitmap == null) return;
                var snipPrompt = settings.SnipPrompt;
                var answer = await router.AskImageAsync(snipPrompt, overlay.SnipBitmap);
                new AnswerWindow2(answer, settings.StealthMode, settings.SemiStealthSnip).Show();
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
            new AnswerWindow2(result, settings.StealthMode, false).Show();
        }
        catch (Exception ex)
        {
            tray.ShowBalloonTip(4000, "ez-9router error", ex.Message, ToolTipIcon.Error);
        }
    }

    void OpenSettings()
    {
        using var form = new SettingsForm2(settings, router);
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
    public bool StealthMode { get; set; }
    public bool SemiStealthSnip { get; set; }

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
            else if (part == "ESC") key = (uint)Keys.Escape;
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
    public AnswerWindow2(string answer, bool stealth, bool persistentStealth)
    {
        StartPosition = stealth ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        Width = stealth ? 430 : 560;
        Height = stealth ? 220 : 360;
        KeyPreview = true;
        closeOnHoverX = stealth && persistentStealth;
        if (stealth)
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = ClampNearCursor(area, new Size(Width, Height));
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.White;
            ForeColor = Color.Black;
            Controls.Add(new Label { Dock = DockStyle.Fill, Text = answer, BackColor = Color.White, ForeColor = Color.Black, Font = new Font("Arial", 10.5f), Padding = new Padding(12), AutoEllipsis = true });
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
}

sealed class SettingsForm2 : Form
{
    readonly AppSettings settings;
    readonly RouterClient router;
    readonly Dictionary<string, Control> fields = new();
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
        root.Controls.Add(modes);

        var prompts = Card("Prompts");
        AddText(prompts, "Answer prompt", settings.AnswerPrompt, false, 64);
        AddText(prompts, "Snip prompt", settings.SnipPrompt, false, 64);
        root.Controls.Add(prompts);

        var keys = Card("Hotkeys");
        AddHotkey(keys, "Answer hotkey", settings.AnswerHotkey);
        AddHotkey(keys, "Snip hotkey", settings.SnipHotkey);
        AddHotkey(keys, "Custom hotkey", settings.CustomHotkey);
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
        var box = new TextBox { Text = value, Width = 460, Height = height, UseSystemPasswordChar = password, BackColor = Color.FromArgb(20, 19, 17), ForeColor = text, BorderStyle = BorderStyle.FixedSingle, Multiline = height > 40 };
        fields[label] = box;
        card.Controls.Add(box);
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
        settings.SnipPrompt = ((TextBox)fields["Snip prompt"]).Text.Trim();
        settings.AnswerHotkey = ((TextBox)fields["Answer hotkey"]).Text.Trim();
        settings.SnipHotkey = ((TextBox)fields["Snip hotkey"]).Text.Trim();
        settings.CustomHotkey = ((TextBox)fields["Custom hotkey"]).Text.Trim();
        settings.StealthMode = ((CheckBox)fields["Stealth mode"]).Checked;
        settings.SemiStealthSnip = ((CheckBox)fields["Semi-stealth snip"]).Checked;
        DialogResult = DialogResult.OK;
        Close();
    }
}

sealed class HotkeyBox : TextBox
{
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey) return true;
        var parts = new List<string>();
        if (keyData.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keyData.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (keyData.HasFlag(Keys.Shift)) parts.Add("Shift");
        parts.Add(FormatKey(key));
        Text = string.Join("+", parts);
        return true;
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
            _ => key.ToString()
        };
    }
}

sealed class SnipOverlay2 : Form
{
    readonly bool quiet;
    Point start;
    Rectangle rect;
    public Bitmap? SnipBitmap { get; private set; }
    public SnipOverlay2(bool stealth)
    {
        quiet = stealth;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        Opacity = stealth ? .85 : .22;
        BackColor = stealth ? Color.Magenta : Color.Black;
        TransparencyKey = stealth ? Color.Magenta : Color.Empty;
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
    protected override void OnPaint(PaintEventArgs e) { using var pen = new Pen(quiet ? Color.Red : Color.OrangeRed, quiet ? 1 : 2); e.Graphics.DrawRectangle(pen, rect); }
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
    static Rectangle Normalize(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
