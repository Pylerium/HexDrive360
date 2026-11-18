using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MemoryEditor
{
    public class ValueScannerForm : Form
    {
        private TextBox txtHost;
        private NumericUpDown numPort;
        private TextBox txtStart;
        private TextBox txtEnd;
        private ComboBox cmbType;
        private TextBox txtValue;
        private NumericUpDown numChunk;
        private NumericUpDown numConcurrency;
        private Button btnConnect;
        private Button btnScan;
        private Button btnCancel;
        private ProgressBar progressBar;
        private ListBox lstResults;
        private Label lblStatus;

        private XBDMClient? _client;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _freezeCts;

        public ValueScannerForm()
        {
            Text = "Value Scanner";
            Size = new Size(780, 560);
            StartPosition = FormStartPosition.CenterParent;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            var marginTop = 12;

            txtHost = new TextBox() { Left = 12, Top = marginTop, Width = 180, Text = "127.0.0.1" };
            numPort = new NumericUpDown() { Left = txtHost.Right + 8, Top = marginTop, Width = 80, Minimum = 1, Maximum = 65535, Value = 730 };
            btnConnect = new Button() { Left = numPort.Right + 8, Top = marginTop - 2, Width = 120, Text = "Connect" };
            btnConnect.Click += BtnConnect_Click;

            var top2 = txtHost.Bottom + 8;
            txtStart = new TextBox() { Left = 12, Top = top2, Width = 160, Text = "0x80000000" };
            txtEnd = new TextBox() { Left = txtStart.Right + 8, Top = top2, Width = 160, Text = "0x8000FFFF" };

            cmbType = new ComboBox() { Left = txtEnd.Right + 8, Top = top2, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbType.Items.AddRange(new string[] { "int32", "uint32", "int64", "uint64", "float", "double", "hex" });
            cmbType.SelectedIndex = 0;

            txtValue = new TextBox() { Left = cmbType.Right + 8, Top = top2, Width = 200, Text = "123" };

            var top3 = txtStart.Bottom + 10;
            numChunk = new NumericUpDown() { Left = 12, Top = top3, Width = 120, Minimum = 256, Maximum = 0x100000, Value = 0x1000, Increment = 256 }; 
            numConcurrency = new NumericUpDown() { Left = numChunk.Right + 8, Top = top3, Width = 120, Minimum = 1, Maximum = 32, Value = 6 };

            btnScan = new Button() { Left = numConcurrency.Right + 12, Top = top3 - 2, Width = 120, Text = "Start Scan" };
            btnScan.Click += BtnScan_Click;
            btnCancel = new Button() { Left = btnScan.Right + 8, Top = top3 - 2, Width = 120, Text = "Cancel", Enabled = false };
            btnCancel.Click += BtnCancel_Click;

            progressBar = new ProgressBar() { Left = 12, Top = numChunk.Bottom + 12, Width = ClientSize.Width - 24, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };

            // Replace simple list with DataGridView for polished results
            var dgv = new DataGridView()
            {
                Left = 12,
                Top = progressBar.Bottom + 10,
                Width = ClientSize.Width - 24,
                Height = ClientSize.Height - progressBar.Bottom - 80,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom | AnchorStyles.Top,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };

            dgv.Columns.Add(new DataGridViewTextBoxColumn() { Name = "Address", HeaderText = "Address", Width = 140 });
            dgv.Columns.Add(new DataGridViewTextBoxColumn() { Name = "ValueHex", HeaderText = "Hex" });
            dgv.Columns.Add(new DataGridViewTextBoxColumn() { Name = "ValueText", HeaderText = "Value" });

            lstResults = new ListBox() { Visible = false }; // keep for compatibility
            lblStatus = new Label() { Left = 12, Top = dgv.Bottom + 6, Width = ClientSize.Width - 24, Height = 20, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };

            // Add quick action buttons
            var btnSave = new Button() { Left = dgv.Right - 360, Top = dgv.Bottom + 6, Width = 100, Text = "Save" };
            var btnLoad = new Button() { Left = btnSave.Right + 8, Top = dgv.Bottom + 6, Width = 100, Text = "Load" };
            var btnFreeze = new Button() { Left = btnLoad.Right + 8, Top = dgv.Bottom + 6, Width = 120, Text = "Freeze Selected" };

            btnSave.Click += (s, e) => SaveResults(dgv);
            btnLoad.Click += async (s, e) => await LoadResults(dgv);
            btnFreeze.Click += async (s, e) => await FreezeSelectedAsync(dgv);

            Controls.AddRange(new Control[] { txtHost, numPort, btnConnect, txtStart, txtEnd, cmbType, txtValue, numChunk, numConcurrency, btnScan, btnCancel, progressBar, dgv, lstResults, lblStatus, btnSave, btnLoad, btnFreeze });

            // Small labels
            var l1 = new Label() { Left = txtHost.Left, Top = txtHost.Bottom - 2, Text = "Host", AutoSize = true };
            var l2 = new Label() { Left = numPort.Left, Top = numPort.Bottom - 2, Text = "Port", AutoSize = true };
            var l3 = new Label() { Left = txtStart.Left, Top = txtStart.Bottom - 2, Text = "Start", AutoSize = true };
            var l4 = new Label() { Left = txtEnd.Left, Top = txtEnd.Bottom - 2, Text = "End", AutoSize = true };
            var l5 = new Label() { Left = cmbType.Left, Top = cmbType.Bottom - 2, Text = "Type", AutoSize = true };
            var l6 = new Label() { Left = txtValue.Left, Top = txtValue.Bottom - 2, Text = "Value", AutoSize = true };
            var l7 = new Label() { Left = numChunk.Left, Top = numChunk.Bottom - 2, Text = "Chunk (bytes)", AutoSize = true };
            var l8 = new Label() { Left = numConcurrency.Left, Top = numConcurrency.Bottom - 2, Text = "Concurrency", AutoSize = true };
            Controls.AddRange(new Control[] { l1, l2, l3, l4, l5, l6, l7, l8 });
        }

        private async void BtnConnect_Click(object? sender, EventArgs e)
        {
            btnConnect.Enabled = false;
            lblStatus.Text = "Connecting...";
            try
            {
                _client = new XBDMClient(txtHost.Text.Trim(), (int)numPort.Value);
                var ok = await _client.ConnectAsync();
                if (!ok)
                {
                    lblStatus.Text = "Not connected";
                    MessageBox.Show(this, "Connection failed. Ensure the console IP/port are reachable and XBDM is enabled.", "Connection Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // perform a lightweight read test to confirm we can actually talk to the console
                lblStatus.Text = "Connected — testing memory read...";
                try
                {
                    uint testAddr = ParseUint(txtStart.Text.Trim());
                    var mem = await _client.GetMemoryAsync(testAddr, 4);
                    if (mem != null && mem.Length >= 1)
                    {
                        lblStatus.Text = $"Connected — read {mem.Length} bytes @0x{testAddr:X8}";
                        var hex = BitConverter.ToString(mem).Replace('-', ' ');
                        MessageBox.Show(this, $"Read test succeeded:\nAddress: 0x{testAddr:X8}\nBytes: {hex}", "Connected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        lblStatus.Text = "Connected (no memory data)";
                        MessageBox.Show(this, "Connected but read returned no data. The console may be busy or require a different address.", "Connected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                catch (Exception ex)
                {
                    lblStatus.Text = "Connected (read test failed)";
                    MessageBox.Show(this, "Connected but memory read test failed: " + ex.Message, "Read Test Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Connect failed: " + ex.Message;
                _client = null;
            }
            finally
            {
                btnConnect.Enabled = true;
            }
        }

        private async void BtnScan_Click(object? sender, EventArgs e)
        {
            if (_client == null || !_client.IsConnected)
            {
                MessageBox.Show(this, "Please connect to a target first.", "Not connected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnScan.Enabled = false;
            btnCancel.Enabled = true;
            lstResults.Items.Clear();
            lblStatus.Text = "Scanning...";
            _cts = new CancellationTokenSource();

            // Build needle bytes (big-endian) - reuse small helper
            byte[] needle;
            try
            {
                var type = cmbType.SelectedItem?.ToString() ?? "int32";
                needle = ConvertToBytesBE(txtValue.Text.Trim(), type);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Value parse error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnScan.Enabled = true; btnCancel.Enabled = false; lblStatus.Text = "Ready"; return;
            }

            var backend = new ValueScannerBackend(_client);
            uint start = ParseUint(txtStart.Text.Trim());
            uint end = ParseUint(txtEnd.Text.Trim());
            int chunk = (int)numChunk.Value;
            int concurrency = (int)numConcurrency.Value;

            var progress = new Progress<int>(count => {
                // update small progress indicator text
                lblStatus.Text = $"Found: {count:n0}";
            });

            try
            {
                var results = await backend.ScanRangeAsync(start, end, needle, chunk, concurrency, progress, _cts.Token);
                foreach (var r in results)
                    lstResults.Items.Add($"0x{r:X8}");
                lblStatus.Text = $"Done - {results.Count} matches";
            }
            catch (OperationCanceledException)
            {
                lblStatus.Text = "Cancelled";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
            }
            finally
            {
                btnScan.Enabled = true;
                btnCancel.Enabled = false;
                _cts = null;
            }
        }

        private void BtnCancel_Click(object? sender, EventArgs e)
        {
            btnCancel.Enabled = false;
            _cts?.Cancel();
        }

        private static byte[] ConvertToBytesBE(string value, string type)
        {
            type = type.ToLowerInvariant();
            if (type == "int32")
            {
                int v;
                var s = value.Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = Convert.ToInt32(s.Substring(2), 16);
                else v = int.Parse(s);
                var b = BitConverter.GetBytes(v);
                if (BitConverter.IsLittleEndian) Array.Reverse(b);
                return b;
            }
            if (type == "uint32")
            {
                uint v;
                var s = value.Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = Convert.ToUInt32(s.Substring(2), 16);
                else v = Convert.ToUInt32(s);
                var b = BitConverter.GetBytes(v);
                if (BitConverter.IsLittleEndian) Array.Reverse(b);
                return b;
            }
            if (type == "int64")
            {
                long v;
                var s = value.Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = Convert.ToInt64(s.Substring(2), 16);
                else v = Convert.ToInt64(s);
                var b = BitConverter.GetBytes(v);
                if (BitConverter.IsLittleEndian) Array.Reverse(b);
                return b;
            }
            if (type == "uint64")
            {
                ulong v;
                var s = value.Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = Convert.ToUInt64(s.Substring(2), 16);
                else v = Convert.ToUInt64(s);
                var b = BitConverter.GetBytes(v);
                if (BitConverter.IsLittleEndian) Array.Reverse(b);
                return b;
            }
            if (type == "float")
            {
                var f = float.Parse(value);
                var b = BitConverter.GetBytes(f);
                if (BitConverter.IsLittleEndian) Array.Reverse(b);
                return b;
            }
            if (type == "double")
            {
                var d = double.Parse(value);
                var b = BitConverter.GetBytes(d);
                if (BitConverter.IsLittleEndian) Array.Reverse(b);
                return b;
            }
            if (type == "hex")
            {
                var s = value.Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
                if (s.Length % 2 == 1) s = "0" + s;
                return Enumerable.Range(0, s.Length / 2).Select(i => Convert.ToByte(s.Substring(i * 2, 2), 16)).ToArray();
            }
            throw new ArgumentException("Unknown type: " + type);
        }

        private static uint ParseUint(string s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt32(s.Substring(2), 16);
            return Convert.ToUInt32(s, 10);
        }

        private void SaveResults(DataGridView dgv)
        {
            try
            {
                var sfd = new SaveFileDialog() { Filter = "JSON files|*.json|All files|*.*", DefaultExt = "json" };
                if (sfd.ShowDialog(this) != DialogResult.OK) return;

                var session = new ScanSession();
                session.Options.Start = ParseUint(txtStart.Text.Trim());
                session.Options.End = ParseUint(txtEnd.Text.Trim());
                session.Options.ValueType = cmbType.SelectedItem?.ToString() ?? "int32";
                session.Options.ValueText = txtValue.Text.Trim();
                session.Options.ChunkSize = (int)numChunk.Value;
                session.Options.Concurrency = (int)numConcurrency.Value;

                foreach (DataGridViewRow row in dgv.Rows)
                {
                    if (row.Cells.Count >= 1 && row.Cells[0].Value != null)
                    {
                        var sr = new ScanResult();
                        var addrText = row.Cells[0].Value.ToString() ?? "";
                        if (addrText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            sr.Address = Convert.ToUInt32(addrText.Substring(2), 16);
                        else sr.Address = Convert.ToUInt32(addrText);
                        sr.ValueHex = row.Cells.Count > 1 ? (row.Cells[1].Value?.ToString() ?? "") : "";
                        sr.ValueText = row.Cells.Count > 2 ? (row.Cells[2].Value?.ToString() ?? "") : "";
                        session.Results.Add(sr);
                    }
                }

                var json = System.Text.Json.JsonSerializer.Serialize(session, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(sfd.FileName, json);
                MessageBox.Show(this, "Saved.", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task LoadResults(DataGridView dgv)
        {
            try
            {
                var ofd = new OpenFileDialog() { Filter = "JSON files|*.json|All files|*.*" };
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                var json = await System.IO.File.ReadAllTextAsync(ofd.FileName);
                var session = System.Text.Json.JsonSerializer.Deserialize<ScanSession>(json);
                if (session == null) return;

                txtStart.Text = "0x" + session.Options.Start.ToString("X8");
                txtEnd.Text = "0x" + session.Options.End.ToString("X8");
                txtValue.Text = session.Options.ValueText ?? txtValue.Text;
                numChunk.Value = session.Options.ChunkSize;
                numConcurrency.Value = session.Options.Concurrency;
                if (!string.IsNullOrEmpty(session.Options.ValueType) && cmbType.Items.Contains(session.Options.ValueType))
                    cmbType.SelectedItem = session.Options.ValueType;

                dgv.Rows.Clear();
                foreach (var r in session.Results)
                {
                    dgv.Rows.Add($"0x{r.Address:X8}", r.ValueHex ?? "", r.ValueText ?? "");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Load failed: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task FreezeSelectedAsync(DataGridView dgv)
        {
            if (_client == null || !_client.IsConnected)
            {
                MessageBox.Show(this, "Not connected.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (dgv.SelectedRows.Count == 0)
            {
                MessageBox.Show(this, "Select one or more rows to freeze.", "No selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Prompt for hex bytes
            string? hex = PromptForText("Enter hex bytes to write (e.g. 00 FF 11) or leave blank to use current value:", "Freeze Value");
            if (hex == null) return; // cancel
            hex = hex.Trim();
            if (hex.Length == 0)
            {
                MessageBox.Show(this, "No hex provided. Freeze requires a value to write.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // sanitize
            hex = hex.Replace(" ", "").Replace("0x", "").Replace("0X", "");
            if (hex.Length % 2 == 1) hex = "0" + hex;
            byte[] bytes;
            try { bytes = Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray(); }
            catch (Exception ex) { MessageBox.Show(this, "Invalid hex: " + ex.Message); return; }

            // start freeze task
            _freezeCts?.Cancel();
            _freezeCts = new CancellationTokenSource();
            var token = _freezeCts.Token;
            lblStatus.Text = "Freezing...";

            await Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        foreach (DataGridViewRow row in dgv.SelectedRows)
                        {
                            if (row.Cells.Count == 0) continue;
                            var addrText = row.Cells[0].Value?.ToString() ?? "";
                            uint addr = 0;
                            try
                            {
                                if (addrText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) addr = Convert.ToUInt32(addrText.Substring(2), 16);
                                else addr = Convert.ToUInt32(addrText);
                                await _client.SetMemoryAsync(addr, bytes);
                            }
                            catch { }
                        }
                        await Task.Delay(200, token);
                    }
                }
                catch (OperationCanceledException) { }
            }, token).ContinueWith(t => {
                BeginInvoke(() => lblStatus.Text = "Freeze stopped");
            });
        }

        private static string? PromptForText(string prompt, string title)
        {
            using (var f = new Form() { Width = 500, Height = 140, Text = title, StartPosition = FormStartPosition.CenterParent })
            {
                var tb = new TextBox() { Left = 12, Top = 12, Width = 460 };
                var ok = new Button() { Text = "OK", Left = 300, Width = 80, Top = 44, DialogResult = DialogResult.OK };
                var cancel = new Button() { Text = "Cancel", Left = 388, Width = 80, Top = 44, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { tb, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog() == DialogResult.OK) return tb.Text;
                return null;
            }
        }
    }
}
