using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>Console information, power / execution control and a raw XBDM command terminal with history.</summary>
public sealed class ConsoleTool : ToolBase
{
    private static readonly string[] BinaryCommands = { "getmem2", "getfile", "screenshot", "getmemex", "sendfile", "setmemex" };

    private readonly RichTextBox _info = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Font = Theme.Mono, BackColor = Theme.Surface, ForeColor = Theme.Text, DetectUrls = false };
    private readonly RichTextBox _term = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Font = Theme.Mono, BackColor = Theme.CodeBg, ForeColor = Theme.Text, DetectUrls = false };
    private readonly TextBox _cmd = new() { Dock = DockStyle.Fill, Font = Theme.Mono, PlaceholderText = "XBDM command, e.g. modules · walkmem · systime · dbgname · threads   (↑/↓ history)" };
    private readonly List<string> _history = new();
    private int _histPos;
    private bool _busy;

    public override string Key => "console";
    public override string Title => "Console";
    public override string Glyph => Theme.Glyph.Terminal;
    public override string Description => "Console info, pause / restart and a raw XBDM terminal";

    public ConsoleTool(IToolHost host) : base(host)
    {
        Theme.Exclude(_info);
        Theme.Exclude(_term);
        _cmd.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SendAsync(); }
            else if (e.KeyCode == Keys.Up && _history.Count > 0) { e.SuppressKeyPress = true; _histPos = Math.Max(0, _histPos - 1); _cmd.Text = _history[_histPos]; _cmd.SelectionStart = _cmd.TextLength; }
            else if (e.KeyCode == Keys.Down && _history.Count > 0) { e.SuppressKeyPress = true; _histPos = Math.Min(_history.Count, _histPos + 1); _cmd.Text = _histPos < _history.Count ? _history[_histPos] : ""; _cmd.SelectionStart = _cmd.TextLength; }
        };

        var actions = Row(
            B("Refresh info", Theme.Glyph.Refresh, ButtonVariant.Primary, async (_, _) => await RefreshInfoAsync(true)),
            B("Pause game", Theme.Glyph.Stop, ButtonVariant.Warning, async (_, _) => await Run(c => c.StopExecutionAsync(), "Game paused")),
            B("Resume game", Theme.Glyph.Play, ButtonVariant.Success, async (_, _) => await Run(c => c.GoAsync(), "Game resumed")),
            B("Restart (warm)", Theme.Glyph.Refresh, ButtonVariant.Neutral, async (_, _) => await RebootAsync(false)),
            B("Restart (cold)", Theme.Glyph.Refresh, ButtonVariant.Danger, async (_, _) => await RebootAsync(true)),
            B("Clear terminal", Theme.Glyph.Clear, ButtonVariant.Ghost, (_, _) => _term.Clear()));

        var infoCard = Ui.Card(DockStyle.Top, 190, new Padding(14, 10, 10, 10));
        infoCard.Controls.Add(_info);
        var cmdRow = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0), Tag = "keep" };
        cmdRow.Controls.Add(_cmd);
        cmdRow.Controls.Add(B("Send", Theme.Glyph.Play, ButtonVariant.Primary, async (_, _) => await SendAsync()).Also(b => { b.Dock = DockStyle.Right; }));
        var termCard = Card(_term, new Padding(10, 8, 6, 6));

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 10, 0, 0), Tag = "keep" };
        body.Controls.Add(termCard);
        body.Controls.Add(cmdRow);

        Controls.Add(body);
        Controls.Add(infoCard);
        Controls.Add(actions);
        Write("XBDM terminal – responses are shown verbatim. Binary commands (getmem2, getfile, screenshot…) are blocked here.\n", Theme.TextDim);
    }

    public override void OnShown() => _ = RefreshInfoAsync(false);
    public override void OnConnectionChanged(bool connected) { if (Visible) _ = RefreshInfoAsync(false); }

    private async Task RefreshInfoAsync(bool user)
    {
        var c = user ? RequireClient() : Client;
        if (c == null) { _info.Text = "Not connected."; return; }
        if (_busy) return;
        _busy = true;
        try
        {
            var name = await c.GetConsoleName();
            var title = await c.GetRunningTitleAsync();
            var mods = await c.GetModulesAsync();
            var systime = await c.SendRawAsync("systime");
            var threads = await c.GetThreadsAsync();
            string Kv(XBDMClient.XbdmResponse r, string key) => XBDMClient.ParseKeyValues(r.Message).GetValueOrDefault(key) ?? "";
            DateTime? consoleTime = null;
            try
            {
                long hi = XBDMClient.ParseUInt(Kv(systime, "high")), lo = XBDMClient.ParseUInt(Kv(systime, "low"));
                if (hi != 0) consoleTime = DateTime.FromFileTimeUtc((hi << 32) | lo).ToLocalTime();
            }
            catch { }

            var lines = new List<string>
            {
                $"Console        {name}   ({c.IpAddress}:{c.Port})",
                $"Debug API      {c.GetConnectionStatus()}",
                $"Running        {(title?.Path is { Length: > 0 } p ? XBDMClient.ToXbdmPath(p) : "(dashboard / unknown)")}",
                $"Title build    {(title is { Timestamp: > 0 } t ? $"0x{t.Timestamp:X8} ({DateTimeOffset.FromUnixTimeSeconds(t.Timestamp):yyyy-MM-dd})  checksum 0x{t.Checksum:X8}" : "-")}",
                $"Modules        {mods.Count}   ·   threads {threads.Count}",
                $"Console clock  {(consoleTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-")}",
                $"Link           {c.LastLatencyMs:0} ms latency · ↓ {c.BytesRead / 1048576.0:0.0} MB  ↑ {c.BytesWritten / 1048576.0:0.00} MB this session",
            };
            _info.Text = string.Join("\n", lines);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { _info.Text = "Console did not answer."; }
        finally { _busy = false; }
    }

    private async Task SendAsync()
    {
        var text = _cmd.Text.Trim();
        if (text.Length == 0) return;
        var c = RequireClient();
        if (c == null) return;
        var verb = text.Split(' ')[0].ToLowerInvariant();
        if (BinaryCommands.Contains(verb))
        {
            Write($"> {text}\n  blocked: binary transfer commands would desynchronise the session – use the Memory / XEX tools instead\n", Theme.Warning);
            return;
        }
        _history.Remove(text);
        _history.Add(text);
        _histPos = _history.Count;
        _cmd.Clear();
        Write($"> {text}\n", Theme.Accent);
        try
        {
            var r = await c.SendRawAsync(text, 0, 15000);
            Write($"{r.Code}- {r.Message}\n", r.Ok ? Theme.Success : Theme.Danger);
            foreach (var l in r.Lines.Take(2000)) Write($"  {l}\n", Theme.Text);
            if (r.Lines.Count > 2000) Write($"  … {r.Lines.Count - 2000} more lines\n", Theme.TextDim);
        }
        catch (Exception ex) { Write($"  error: {ex.Message}\n", Theme.Danger); }
    }

    private void Write(string text, Color color)
    {
        _term.SelectionStart = _term.TextLength;
        _term.SelectionLength = 0;
        _term.SelectionColor = color;
        _term.AppendText(text);
        _term.ScrollToCaret();
    }

    private async Task Run(Func<XBDMClient, Task<bool>> op, string done)
    {
        var c = RequireClient();
        if (c == null) return;
        bool ok = await op(c);
        App.Toast(ok ? done : "The console refused the request", ok ? ToastHost.Kind.Success : ToastHost.Kind.Warning);
    }

    private async Task RebootAsync(bool cold)
    {
        var c = RequireClient();
        if (c == null) return;
        if (MessageBox.Show(FindForm(), $"{(cold ? "Cold" : "Warm")} restart the console now?\nThe connection is re-established automatically.", "Restart console",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await c.RebootAsync(cold);
        App.Log($"🔄 {(cold ? "Cold" : "Warm")} restart requested");
    }
}

internal static class ControlExtensions
{
    public static T Also<T>(this T c, Action<T> a) { a(c); return c; }
}
