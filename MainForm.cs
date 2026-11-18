using System.Text;
using MemoryEditor.Runtime;
using MemoryEditor.Scripting;
using MemoryEditor.UI;
using MemoryEditor.Xex;

namespace MemoryEditor;

public partial class MainForm : Form, IAppHost
{
    private XBDMClient? _xbdmClient;
    private System.Windows.Forms.Timer? _statusTimer;
    private System.Windows.Forms.Timer? _autoRefreshTimer;

    private TextBox txtIpAddress = null!;
    private TextBox txtPort = null!;
    private IconButton btnConnect = null!;
    private IconButton btnDisconnect = null!;
    private StatusPill lblStatus = null!;
    
    private TextBox txtAddress = null!;
    private TextBox txtLength = null!;
    private Button btnRead = null!;
    private Button btnWrite = null!;
    private RichTextBox txtMemoryView = null!;
    
    private TextBox txtSearchHex = null!;
    private Button btnSearch = null!;
    private ListBox lstSearchResults = null!;
    
    private TextBox txtWriteAddress = null!;
    private ComboBox cmbWriteType = null!;
    private TextBox txtWriteValue = null!;
    private Button btnQuickWrite = null!;
    
    private RichTextBox txtConsole = null!;
    private IconButton btnClearConsole = null!;
    
    private Button btnSaveSnapshot = null!;
    private Button btnLoadSnapshot = null!;
    private Button btnCompare = null!;
    private CheckBox chkAutoRefresh = null!;
    private NumericUpDown numRefreshInterval = null!;
    
    private Button btnPokeInt32 = null!;
    private Button btnPokeFloat = null!;
    private Button btnFreeze = null!;
    
    private byte[]? _currentMemory;
    private byte[]? _snapshotMemory;
    private uint _currentAddress;
    
    private RichTextBox? txtScript;
    private ListBox? lstSavedScripts;
    private ListBox? lstQuickMods;
    private Dictionary<string, string> _savedScripts = new();
    private Dictionary<string, bool> _activeMods = new();
    
    // Offset Manager fields
    private OffsetManager _offsetManager = new();
    private DataGridView? dgvOffsets;
    private ComboBox? cmbOffsetCategory;
    private TextBox? txtOffsetSearch;
    
    // Register Viewer fields
    private DataGridView? dgvRegisters;
    private Button? btnRefreshRegisters;
    private Button? btnSetRegister;
    private TextBox? txtRegisterName;
    private TextBox? txtRegisterValue;
    private Dictionary<string, ulong> _currentRegisters = new();
    private TextBox? txtPointerValue;
    private Button? btnResolveOffsets;
    private Button? btnApplySelected;
    private Button? btnExportScript;
    private Dictionary<uint, uint> _resolvedPointers = new();
    
    // GUI Builder fields
    private GUIBuilder _guiBuilder = new();
    private ScriptManager _scriptManager = new();
    private ComboBox? cmbGUITemplate;
    private TextBox? txtProjectName;
    private ListBox? lstSelectedOffsets;
    private Button? btnBuildGUI;
    private PictureBox? picTemplatePreview;
    
    // UI Layout fields
    private Panel? consolePanel;
    private IconButton? btnToggleConsole;
    private bool _consoleExpanded = true;
    private int _consoleCollapsedHeight = 45;
    private int _consoleExpandedHeight = 250;

    // Advanced Memory Engine
    private AdvancedMemoryEngine _memoryEngine = new();
    private DataGridView? dgvScanResults;
    private DataGridView? dgvWatchList;
    private TextBox? txtScanValue;
    private ComboBox? cmbScanType;
    private ComboBox? cmbValueType;
    private TextBox? txtStartAddress;
    private TextBox? txtEndAddress;
    private Button? btnFirstScan;
    private Button? btnNextScan;
    private Button? btnNewScan;
    private Button? btnAddToWatchList;
    private Button? btnPointerScan;
    private Button? btnImportAddresses;
    private Button? btnExportAddresses;
    private Button? btnStructWalk;
    private System.Windows.Forms.Timer? _watchListTimer;
    
    // Memory Recording controls
    private DataGridView? dgvRecordingChanges;
    private TextBox? txtRecordStartAddr;
    private TextBox? txtRecordEndAddr;
    private TextBox? txtRecordSize;
    private Button? btnStartRecording;
    private Button? btnStopRecording;
    private Button? btnExportRecording;
    private Label? lblRecordingStatus;
    private System.Windows.Forms.Timer? _recordingUpdateTimer;
    private CheckBox? chkMonitorBaseAddr;
    private ListBox? lstRecentChanges;
    private Label? lblRecentActivity;
    private ScriptExecutor? _scriptExecutor;
    
    // Static Analysis controls
    private RichTextBox? txtTextSection;
    private RichTextBox? txtDataSection;
    private RichTextBox? txtRDataSection;
    private ListBox? lstStrings;
    private ListBox? lstXRefs;
    private TextBox? txtSearchPattern;
    private byte[]? _loadedBinary;
    private uint _loadedBase;
    private Dictionary<uint, string> _stringReferences = new();
    private Dictionary<uint, List<uint>> _crossReferences = new();

    public MainForm()
    {
        this.AutoScaleMode = AutoScaleMode.Dpi;
        this.AutoScaleDimensions = new SizeF(96F, 96F);

        InitializeComponent();
        SetupStatusTimer();
        LoadSavedLayout();
        _ = LoadOffsetsAsync();

        this.FormClosing += (s, e) => SaveLayout();
    }

    // Xbox 360 memory is big-endian – every value written to the console goes through these
    private static byte[] BeBytes(short v) { var b = new byte[2]; System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(b, v); return b; }
    private static byte[] BeBytes(int v) { var b = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }
    private static byte[] BeBytes(long v) { var b = new byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(b, v); return b; }
    private static byte[] BeBytes(float v) { var b = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b, v); return b; }
    private static byte[] BeBytes(double v) { var b = new byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(b, v); return b; }

    /// <summary>
    /// Maps the scan-type drop-down by name. It used to be cast by index, but the list order differs from the enum
    /// (Changed → ValueBetween, Unchanged → Increased, Increased → Decreased, Decreased → Changed).
    /// </summary>
    private AdvancedMemoryEngine.ScanType SelectedScanType() => cmbScanType?.SelectedItem?.ToString() switch
    {
        "Bigger Than" => AdvancedMemoryEngine.ScanType.BiggerThan,
        "Smaller Than" => AdvancedMemoryEngine.ScanType.SmallerThan,
        "Changed Value" => AdvancedMemoryEngine.ScanType.ChangedValue,
        "Unchanged Value" => AdvancedMemoryEngine.ScanType.UnchangedValue,
        "Increased Value" => AdvancedMemoryEngine.ScanType.IncreasedValue,
        "Decreased Value" => AdvancedMemoryEngine.ScanType.DecreasedValue,
        "Unknown Initial Value" => AdvancedMemoryEngine.ScanType.UnknownInitialValue,
        _ => AdvancedMemoryEngine.ScanType.ExactValue,
    };

    /// <summary>Parses the scan value for the selected type (decimal or 0x hex); logs a clear message when invalid.</summary>
    private bool TryParseScanValue(AdvancedMemoryEngine.ValueType type, string text, out object? value)
    {
        value = null;
        text = text.Trim();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        long ParseInt() => hex ? long.Parse(text[2..], System.Globalization.NumberStyles.HexNumber) : long.Parse(text, ci);
        try
        {
            value = type switch
            {
                AdvancedMemoryEngine.ValueType.Byte => (object)checked((byte)ParseInt()),
                AdvancedMemoryEngine.ValueType.Int16 => checked((short)ParseInt()),
                AdvancedMemoryEngine.ValueType.Int64 => ParseInt(),
                AdvancedMemoryEngine.ValueType.Float => float.Parse(text, ci),
                AdvancedMemoryEngine.ValueType.Double => double.Parse(text, ci),
                _ => unchecked((int)ParseInt()),
            };
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            LogMessage($"❌ “{text}” is not a valid {type} value (use a number like 100, -5, 1.5 or 0x64)");
            Toast($"“{text}” is not a valid {type} value", UI.ToastHost.Kind.Warning);
            return false;
        }
    }

    private async Task LoadOffsetsAsync()
    {
        // The Game Library owns the offset database: it imports the bundled BO2 TU18 file on first run and restores
        // the last selected game / build. The direct file load below is kept as the fallback.
        if (_libraryPage != null)
        {
            try { await _libraryPage.InitializeAsync(); return; }
            catch (Exception ex) { LogMessage($"⚠️ Game library failed to load ({ex.Message}) – falling back to the bundled offsets"); }
        }
        try
        {
            var offsetFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bo2_tu18_offsets.txt");
            
            if (!File.Exists(offsetFilePath))
            {
                LogMessage($"❌ Offset file not found: {offsetFilePath}");
                LogMessage("💡 Please ensure bo2_tu18_offsets.txt is in the application directory");
                return;
            }
            
            LogMessage($"📂 Loading offsets from: {Path.GetFileName(offsetFilePath)}");
            var success = await _offsetManager.LoadFromFileAsync(offsetFilePath);
            
            if (success)
            {
                LogMessage($"✅ Loaded offset database: {_offsetManager.GetAllOffsets().Count} entries, {_offsetManager.Categories.Count} categories");
                RefreshOffsetGrid();
            }
            else
            {
                LogMessage("⚠️ Failed to parse offset database - check file format");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Error loading offsets: {ex.Message}");
            LogMessage($"   Stack: {ex.StackTrace?.Split('\n')[0]}");
        }
    }

    private TabPage CreateMemoryTab()
    {
        var tab = new TabPage("💾 Memory Editor");
        tab.BackColor = Color.FromArgb(250, 250, 250);

        var controlsPanel = new Panel
        {
            Location = new Point(15, 15),
            Size = new Size(1500, 65),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblAddress = new Label { 
            Text = "Address (Hex):", 
            Location = new Point(15, 20), 
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };
        txtAddress = new TextBox { 
            Location = new Point(135, 17), 
            Size = new Size(170, 27), 
            Text = "0x82000000",
            Font = new Font("Consolas", 10F),
            BackColor = Color.FromArgb(250, 250, 250)
        };

        var lblLength = new Label { 
            Text = "Length:", 
            Location = new Point(320, 20), 
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };
        txtLength = new TextBox { 
            Location = new Point(390, 17), 
            Size = new Size(110, 27), 
            Text = "256",
            Font = new Font("Consolas", 10F),
            BackColor = Color.FromArgb(250, 250, 250)
        };

        btnRead = new Button { 
            Text = "📖 Read", 
            Location = new Point(520, 15), 
            Size = new Size(100, 32), 
            BackColor = Color.FromArgb(33, 150, 243),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnRead.FlatAppearance.BorderSize = 0;
        btnRead.Click += BtnRead_Click;

        btnWrite = new Button { 
            Text = "✍️ Write", 
            Location = new Point(630, 15), 
            Size = new Size(100, 32), 
            BackColor = Color.FromArgb(255, 152, 0),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnWrite.FlatAppearance.BorderSize = 0;
        btnWrite.Click += BtnWrite_Click;

        btnSaveSnapshot = new Button { 
            Text = "📸 Snapshot", 
            Location = new Point(740, 15), 
            Size = new Size(110, 32),
            BackColor = Color.FromArgb(156, 39, 176),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnSaveSnapshot.FlatAppearance.BorderSize = 0;
        btnSaveSnapshot.Click += BtnSaveSnapshot_Click;

        chkAutoRefresh = new CheckBox { 
            Text = "🔄 Auto-Refresh", 
            Location = new Point(870, 20), 
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        chkAutoRefresh.CheckedChanged += ChkAutoRefresh_CheckedChanged;

        var lblInterval = new Label { 
            Text = "ms:", 
            Location = new Point(1010, 22), 
            AutoSize = true,
            Font = new Font("Segoe UI", 9F)
        };

        numRefreshInterval = new NumericUpDown
        {
            Location = new Point(1040, 18),
            Size = new Size(80, 27),
            Minimum = 100,
            Maximum = 10000,
            Value = 1000,
            Increment = 100,
            Font = new Font("Consolas", 10F)
        };

        controlsPanel.Controls.AddRange(new Control[] { 
            lblAddress, txtAddress, lblLength, txtLength, 
            btnRead, btnWrite, btnSaveSnapshot, 
            chkAutoRefresh, lblInterval, numRefreshInterval 
        });

        txtMemoryView = new RichTextBox
        {
            Location = new Point(15, 90),
            Size = new Size(1500, 340),
            Font = new Font("Consolas", 9.5F),
            ReadOnly = false,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(0, 255, 127),
            BorderStyle = BorderStyle.FixedSingle
        };

        tab.Controls.AddRange(new Control[] { controlsPanel, txtMemoryView });
        return tab;
    }

    private TabPage CreateOffsetCalculatorTab()
    {
        var tab = new TabPage("🎯 Offset Calculator");
        tab.BackColor = Color.FromArgb(250, 250, 250);

        // Header Panel
        var headerPanel = new Panel
        {
            Location = new Point(15, 15),
            Size = new Size(1500, 70),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblTitle = new Label
        {
            Text = "🎯 Advanced Offset Calculator & Pointer Resolver",
            Location = new Point(15, 12),
            AutoSize = true,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(33, 150, 243)
        };

        var lblDesc = new Label
        {
            Text = "Automatically loaded from bo2_tu18_offsets.txt • Supports dynamic pointer resolution • One-click apply",
            Location = new Point(15, 40),
            AutoSize = true,
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(96, 125, 139)
        };

        headerPanel.Controls.AddRange(new Control[] { lblTitle, lblDesc });

        // Filter and Control Panel
        var controlPanel = new Panel
        {
            Location = new Point(15, 95),
            Size = new Size(1500, 65),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblCategory = new Label
        {
            Text = "Category:",
            Location = new Point(15, 22),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };

        cmbOffsetCategory = new ComboBox
        {
            Location = new Point(95, 19),
            Size = new Size(220, 27),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 9.5F)
        };
        cmbOffsetCategory.Items.Add("-- All Categories --");
        cmbOffsetCategory.SelectedIndex = 0;
        cmbOffsetCategory.SelectedIndexChanged += (s, e) => FilterOffsets();

        var lblSearch = new Label
        {
            Text = "Search:",
            Location = new Point(335, 22),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };

        txtOffsetSearch = new TextBox
        {
            Location = new Point(405, 19),
            Size = new Size(270, 27),
            PlaceholderText = "Search by name...",
            Font = new Font("Segoe UI", 9.5F),
            BackColor = Color.FromArgb(250, 250, 250)
        };
        txtOffsetSearch.TextChanged += (s, e) => FilterOffsets();

        btnExportScript = new Button
        {
            Text = "📄 Export to Script",
            Location = new Point(695, 17),
            Size = new Size(150, 32),
            BackColor = Color.FromArgb(156, 39, 176),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnExportScript.FlatAppearance.BorderSize = 0;
        btnExportScript.Click += BtnExportScript_Click;

        var btnRefreshOffsets = new Button
        {
            Text = "🔄 Reload File",
            Location = new Point(860, 17),
            Size = new Size(130, 32),
            BackColor = Color.FromArgb(33, 150, 243),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnRefreshOffsets.FlatAppearance.BorderSize = 0;
        btnRefreshOffsets.Click += async (s, e) => await LoadOffsetsAsync();

        var btnClearFilters = new Button
        {
            Text = "✨ Clear",
            Location = new Point(1005, 17),
            Size = new Size(90, 32),
            BackColor = Color.FromArgb(96, 125, 139),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnClearFilters.FlatAppearance.BorderSize = 0;
        btnClearFilters.Click += (s, e) =>
        {
            cmbOffsetCategory!.SelectedIndex = 0;
            txtOffsetSearch!.Clear();
        };

        controlPanel.Controls.AddRange(new Control[] {
            lblCategory, cmbOffsetCategory, lblSearch, txtOffsetSearch,
            btnExportScript, btnRefreshOffsets, btnClearFilters
        });

        // DataGridView for Offsets
        dgvOffsets = new DataGridView
        {
            Location = new Point(15, 170),
            Size = new Size(1500, 185),
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            Font = new Font("Consolas", 9F),
            EnableHeadersVisualStyles = false
        };

        dgvOffsets.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(33, 150, 243);
        dgvOffsets.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        dgvOffsets.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        dgvOffsets.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);

        // Setup columns
        dgvOffsets.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name",
            HeaderText = "Name",
            FillWeight = 25
        });

        dgvOffsets.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Category",
            HeaderText = "Category",
            FillWeight = 15
        });

        dgvOffsets.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Address",
            HeaderText = "Base/Formula",
            FillWeight = 20
        });

        dgvOffsets.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Resolved",
            HeaderText = "Resolved Address",
            FillWeight = 15
        });

        dgvOffsets.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Value",
            HeaderText = "Value",
            FillWeight = 12,
            ReadOnly = false
        });

        dgvOffsets.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Type",
            HeaderText = "Type",
            FillWeight = 8
        });

        dgvOffsets.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Bytes",
            HeaderText = "Bytes",
            FillWeight = 15
        });

        // Pointer Resolver Panel
        var resolverPanel = new Panel
        {
            Location = new Point(15, 365),
            Size = new Size(1500, 65),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblPointer = new Label
        {
            Text = "🎯 Pointer Resolver:",
            Location = new Point(15, 12),
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold)
        };

        var lblPointerDesc = new Label
        {
            Text = "Enter runtime pointer value (e.g., from PlayerState at 0x83551A10):",
            Location = new Point(15, 35),
            AutoSize = true,
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(96, 125, 139)
        };

        txtPointerValue = new TextBox
        {
            Location = new Point(450, 32),
            Size = new Size(250, 27),
            PlaceholderText = "0x12345678",
            Font = new Font("Consolas", 10F),
            BackColor = Color.FromArgb(250, 250, 250)
        };

        btnResolveOffsets = new Button
        {
            Text = "🔍 Resolve Addresses",
            Location = new Point(715, 30),
            Size = new Size(160, 32),
            BackColor = Color.FromArgb(76, 175, 80),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnResolveOffsets.FlatAppearance.BorderSize = 0;
        btnResolveOffsets.Click += BtnResolveOffsets_Click;

        var btnReadPointer = new Button
        {
            Text = "📖 Read from Xbox",
            Location = new Point(890, 30),
            Size = new Size(160, 32),
            BackColor = Color.FromArgb(33, 150, 243),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnReadPointer.FlatAppearance.BorderSize = 0;
        btnReadPointer.Click += BtnReadPointer_Click;

        resolverPanel.Controls.AddRange(new Control[] {
            lblPointer, lblPointerDesc, txtPointerValue, btnResolveOffsets, btnReadPointer
        });

        // Action Buttons Panel
        var actionPanel = new Panel
        {
            Location = new Point(15, 435),
            Size = new Size(1500, 25),
            BackColor = Color.Transparent
        };

        btnApplySelected = new Button
        {
            Text = "✅ Apply Selected to Xbox",
            Location = new Point(0, 0),
            Size = new Size(190, 25),
            BackColor = Color.FromArgb(76, 175, 80),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Enabled = false
        };
        btnApplySelected.FlatAppearance.BorderSize = 0;
        btnApplySelected.Click += BtnApplySelected_Click;

        var btnCopyAddress = new Button
        {
            Text = "📋 Copy Address",
            Location = new Point(200, 0),
            Size = new Size(135, 25),
            BackColor = Color.FromArgb(63, 81, 181),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnCopyAddress.FlatAppearance.BorderSize = 0;
        btnCopyAddress.Click += (s, e) => CopySelectedAddress();

        var btnSelectAll = new Button
        {
            Text = "☑️ Select All",
            Location = new Point(345, 0),
            Size = new Size(110, 25),
            BackColor = Color.FromArgb(96, 125, 139),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F),
            Cursor = Cursors.Hand
        };
        btnSelectAll.FlatAppearance.BorderSize = 0;
        btnSelectAll.Click += (s, e) => dgvOffsets?.SelectAll();

        var lblInfo = new Label
        {
            Text = $"💡 Total Offsets: {_offsetManager.GetAllOffsets().Count} | Selected: 0",
            Location = new Point(480, 5),
            AutoSize = true,
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(96, 125, 139)
        };

        dgvOffsets.SelectionChanged += (s, e) =>
        {
            lblInfo.Text = $"💡 Total Offsets: {_offsetManager.GetAllOffsets().Count} | Selected: {dgvOffsets.SelectedRows.Count}";
            btnApplySelected!.Enabled = dgvOffsets.SelectedRows.Count > 0 && _xbdmClient?.IsConnected == true;
        };

        actionPanel.Controls.AddRange(new Control[] {
            btnApplySelected, btnCopyAddress, btnSelectAll, lblInfo
        });

        tab.Controls.AddRange(new Control[] {
            headerPanel, controlPanel, dgvOffsets, resolverPanel, actionPanel
        });

        return tab;
    }

    private TabPage CreateScriptingTab()
    {
        var tab = new TabPage("Script IDE") { Padding = new Padding(0) };
        _scriptIde = new ScriptIde(() => _scriptExecutor, _scriptManager, LogMessage)
        {
            OffsetNames = AllOffsetNames,
            ResolveOffset = ResolveOffsetName,
            Toast = (m, k) => _toasts?.Show(m, k),
        };
        tab.Controls.Add(_scriptIde);
        return tab;
    }

    private TabPage CreateStaticAnalysisTab()
    {
        var tab = new TabPage("🔬 Static Analysis");
        tab.BackColor = Color.FromArgb(245, 245, 250);

        // Main container
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(10)
        };
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        // ===== TOP TOOLBAR =====
        var toolbarPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(32, 33, 36),
            Padding = new Padding(10, 10, 10, 10)
        };

        var lblTitle = new Label
        {
            Text = "🔬 Static Binary Analysis",
            Location = new Point(15, 15),
            AutoSize = true,
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            ForeColor = Color.White
        };

        var btnLoadBinary = new Button
        {
            Text = "📂 Load Binary",
            Location = new Point(280, 12),
            Size = new Size(120, 36),
            BackColor = Color.FromArgb(66, 133, 244),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnLoadBinary.FlatAppearance.BorderSize = 0;
        btnLoadBinary.Click += BtnLoadBinary_Click;

        var btnImportText = new Button
        {
            Text = "📥 .text",
            Location = new Point(410, 12),
            Size = new Size(90, 36),
            BackColor = Color.FromArgb(76, 175, 80),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnImportText.FlatAppearance.BorderSize = 0;
        btnImportText.Click += BtnImportText_Click;

        var btnImportData = new Button
        {
            Text = "📥 .data",
            Location = new Point(510, 12),
            Size = new Size(90, 36),
            BackColor = Color.FromArgb(76, 175, 80),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnImportData.FlatAppearance.BorderSize = 0;
        btnImportData.Click += BtnImportData_Click;

        var btnExportAnalysis = new Button
        {
            Text = "💾 Export",
            Location = new Point(610, 12),
            Size = new Size(100, 36),
            BackColor = Color.FromArgb(52, 168, 83),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Enabled = false
        };
        btnExportAnalysis.FlatAppearance.BorderSize = 0;
        btnExportAnalysis.Name = "btnExportAnalysis";
        btnExportAnalysis.Click += BtnExportAnalysis_Click;

        var btnClearAnalysis = new Button
        {
            Text = "🗑️ Clear",
            Location = new Point(720, 12),
            Size = new Size(90, 36),
            BackColor = Color.FromArgb(234, 67, 53),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnClearAnalysis.FlatAppearance.BorderSize = 0;
        btnClearAnalysis.Click += (s, e) => ClearStaticAnalysis();

        toolbarPanel.Controls.AddRange(new Control[] { lblTitle, btnLoadBinary, btnImportText, btnImportData, btnExportAnalysis, btnClearAnalysis });

        // ===== LEFT PANEL =====
        var leftPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(5),
            Margin = new Padding(0, 0, 5, 0)
        };

        var lblFileInfo = new Label
        {
            Text = "📄 File Information",
            Dock = DockStyle.Top,
            Height = 35,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 33, 36),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(5, 0, 0, 0)
        };

        var txtFileInfo = new RichTextBox
        {
            Name = "txtFileInfo",
            Dock = DockStyle.Top,
            Height = 150,
            ReadOnly = true,
            BackColor = Color.FromArgb(250, 250, 250),
            Font = new Font("Consolas", 9F),
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblStrings = new Label
        {
            Text = "🔤 Detected Strings",
            Dock = DockStyle.Top,
            Height = 35,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 33, 36),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(5, 0, 0, 0)
        };

        var txtStringSearch = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 25,
            PlaceholderText = "🔍 Filter strings...",
            Font = new Font("Segoe UI", 9F),
            BorderStyle = BorderStyle.FixedSingle
        };
        txtStringSearch.TextChanged += (s, e) => FilterStrings(txtStringSearch.Text);

        lstStrings = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            SelectionMode = SelectionMode.One
        };
        lstStrings.DoubleClick += LstStrings_DoubleClick;

        leftPanel.Controls.Add(lstStrings);
        leftPanel.Controls.Add(txtStringSearch);
        leftPanel.Controls.Add(lblStrings);
        leftPanel.Controls.Add(txtFileInfo);
        leftPanel.Controls.Add(lblFileInfo);

        // ===== CENTER PANEL =====
        var centerPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Margin = new Padding(0)
        };

        var centerSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 400,
            BorderStyle = BorderStyle.FixedSingle
        };

        // .text section (left side)
        var textPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(5) };
        var lblTextSection = new Label
        {
            Text = "📜 .text Section (Code)",
            Dock = DockStyle.Top,
            Height = 35,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 33, 36),
            TextAlign = ContentAlignment.MiddleLeft
        };

        txtTextSection = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(220, 220, 220),
            Font = new Font("Consolas", 9F),
            BorderStyle = BorderStyle.None,
            WordWrap = false
        };
        txtTextSection.MouseClick += TxtTextSection_MouseClick;

        textPanel.Controls.Add(txtTextSection);
        textPanel.Controls.Add(lblTextSection);

        // Right side: .data and .rdata sections
        var dataSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 250,
            BorderStyle = BorderStyle.FixedSingle
        };

        // .data section (top right)
        var dataPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(5) };
        var lblDataSection = new Label
        {
            Text = "📊 .data Section (Data)",
            Dock = DockStyle.Top,
            Height = 35,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 33, 36),
            TextAlign = ContentAlignment.MiddleLeft
        };

        txtDataSection = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(220, 220, 220),
            Font = new Font("Consolas", 9F),
            BorderStyle = BorderStyle.None,
            WordWrap = false
        };
        txtDataSection.MouseClick += TxtDataSection_MouseClick;

        dataPanel.Controls.Add(txtDataSection);
        dataPanel.Controls.Add(lblDataSection);

        // .rdata section (bottom right)
        var rdataPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(5) };
        var lblRDataSection = new Label
        {
            Text = "📋 .rdata Section (Read-only Data)",
            Dock = DockStyle.Top,
            Height = 35,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 33, 36),
            TextAlign = ContentAlignment.MiddleLeft
        };

        txtRDataSection = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(220, 220, 220),
            Font = new Font("Consolas", 9F),
            BorderStyle = BorderStyle.None,
            WordWrap = false
        };
        txtRDataSection.MouseClick += TxtRDataSection_MouseClick;

        rdataPanel.Controls.Add(txtRDataSection);
        rdataPanel.Controls.Add(lblRDataSection);

        dataSplit.Panel1.Controls.Add(dataPanel);
        dataSplit.Panel2.Controls.Add(rdataPanel);

        centerSplit.Panel1.Controls.Add(textPanel);
        centerSplit.Panel2.Controls.Add(dataSplit);
        centerPanel.Controls.Add(centerSplit);

        // ===== RIGHT PANEL =====
        var rightPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(5),
            Margin = new Padding(5, 0, 0, 0)
        };

        var lblAnalysis = new Label
        {
            Text = "🔍 Analysis Tools",
            Dock = DockStyle.Top,
            Height = 35,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 33, 36),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var lblPattern = new Label
        {
            Text = "Pattern Search:",
            Dock = DockStyle.Top,
            Height = 25,
            Font = new Font("Segoe UI", 9F),
            TextAlign = ContentAlignment.BottomLeft
        };

        txtSearchPattern = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 25,
            PlaceholderText = "e.g., 48 8B ?? 48 89",
            Font = new Font("Consolas", 9F),
            BorderStyle = BorderStyle.FixedSingle
        };

        var btnSearchPattern = new Button
        {
            Text = "🔎 Search Pattern",
            Dock = DockStyle.Top,
            Height = 32,
            BackColor = Color.FromArgb(66, 133, 244),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 5, 0, 0)
        };
        btnSearchPattern.FlatAppearance.BorderSize = 0;
        btnSearchPattern.Click += BtnSearchPattern_Click;

        var separator1 = new Panel { Dock = DockStyle.Top, Height = 20 };

        var lblXRefs = new Label
        {
            Text = "🔗 Cross References:",
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(234, 67, 53),
            TextAlign = ContentAlignment.BottomLeft
        };

        lstXRefs = new ListBox
        {
            Dock = DockStyle.Top,
            Height = 150,
            Font = new Font("Consolas", 8.5F),
            BackColor = Color.FromArgb(250, 250, 250),
            BorderStyle = BorderStyle.FixedSingle,
            SelectionMode = SelectionMode.One
        };
        lstXRefs.DoubleClick += LstXRefs_DoubleClick;

        var btnFindXRefs = new Button
        {
            Text = "🔗 Find Cross-References",
            Dock = DockStyle.Top,
            Height = 32,
            BackColor = Color.FromArgb(234, 67, 53),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 5, 0, 0)
        };
        btnFindXRefs.FlatAppearance.BorderSize = 0;
        btnFindXRefs.Click += BtnFindXRefs_Click;

        var separator2 = new Panel { Dock = DockStyle.Top, Height = 20 };

        var lblStats = new Label
        {
            Text = "📊 Statistics:",
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(52, 168, 83),
            TextAlign = ContentAlignment.BottomLeft
        };

        var txtStats = new RichTextBox
        {
            Name = "txtStats",
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(250, 250, 250),
            Font = new Font("Consolas", 9F),
            BorderStyle = BorderStyle.FixedSingle
        };

        rightPanel.Controls.Add(txtStats);
        rightPanel.Controls.Add(lblStats);
        rightPanel.Controls.Add(separator2);
        rightPanel.Controls.Add(btnFindXRefs);
        rightPanel.Controls.Add(lstXRefs);
        rightPanel.Controls.Add(lblXRefs);
        rightPanel.Controls.Add(separator1);
        rightPanel.Controls.Add(btnSearchPattern);
        rightPanel.Controls.Add(txtSearchPattern);
        rightPanel.Controls.Add(lblPattern);
        rightPanel.Controls.Add(lblAnalysis);

        mainPanel.SetColumnSpan(toolbarPanel, 3);
        mainPanel.Controls.Add(toolbarPanel, 0, 0);
        mainPanel.Controls.Add(leftPanel, 0, 1);
        mainPanel.Controls.Add(centerPanel, 1, 1);
        mainPanel.Controls.Add(rightPanel, 2, 1);

        tab.Controls.Add(mainPanel);
        return tab;
    }

    private TabPage CreateRegisterViewerTab()
    {
        var tab = new TabPage("🔍 Register Viewer");
        tab.BackColor = Color.FromArgb(250, 250, 250);

        // Main container
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(10)
        };
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70F)); // Registers grid
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F)); // Control panel
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F)); // Toolbar
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // Content

        // ===== TOOLBAR =====
        var toolbarPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(32, 33, 36),
            Padding = new Padding(10, 10, 10, 10)
        };

        var lblTitle = new Label
        {
            Text = "🔍 PowerPC Register Viewer",
            Location = new Point(15, 15),
            AutoSize = true,
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            ForeColor = Color.White
        };

        btnRefreshRegisters = new Button
        {
            Text = "🔄 Refresh Registers",
            Location = new Point(300, 12),
            Size = new Size(160, 36),
            BackColor = Color.FromArgb(66, 133, 244),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnRefreshRegisters.FlatAppearance.BorderSize = 0;
        btnRefreshRegisters.Click += BtnRefreshRegisters_Click;

        var btnClearRegisters = new Button
        {
            Text = "🗑️ Clear",
            Location = new Point(470, 12),
            Size = new Size(100, 36),
            BackColor = Color.FromArgb(234, 67, 53),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnClearRegisters.FlatAppearance.BorderSize = 0;
        btnClearRegisters.Click += (s, e) => ClearRegisterView();

        toolbarPanel.Controls.AddRange(new Control[] { lblTitle, btnRefreshRegisters, btnClearRegisters });

        // ===== REGISTERS GRID =====
        var gridPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(5)
        };

        var lblRegisters = new Label
        {
            Text = "📊 CPU Registers (PowerPC)",
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 33, 36)
        };

        dgvRegisters = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            Font = new Font("Consolas", 9F)
        };

        // Configure columns
        dgvRegisters.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Register",
            HeaderText = "Register",
            Width = 100,
            ReadOnly = true
        });

        dgvRegisters.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Value",
            HeaderText = "Value (Hex)",
            Width = 150,
            ReadOnly = true
        });

        dgvRegisters.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ValueDec",
            HeaderText = "Value (Dec)",
            Width = 120,
            ReadOnly = true
        });

        dgvRegisters.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Description",
            HeaderText = "Description",
            Width = 200,
            ReadOnly = true
        });

        gridPanel.Controls.Add(dgvRegisters);
        gridPanel.Controls.Add(lblRegisters);

        // ===== CONTROL PANEL =====
        var controlPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(5),
            Margin = new Padding(5, 0, 0, 0)
        };

        var lblControls = new Label
        {
            Text = "⚙️ Register Controls",
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 33, 36)
        };

        var lblSetRegister = new Label
        {
            Text = "Set Register Value:",
            Location = new Point(5, 40),
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };

        txtRegisterName = new TextBox
        {
            Location = new Point(5, 60),
            Size = new Size(120, 25),
            PlaceholderText = "e.g., r0, lr, pc",
            Font = new Font("Consolas", 9F),
            BorderStyle = BorderStyle.FixedSingle
        };

        txtRegisterValue = new TextBox
        {
            Location = new Point(5, 90),
            Size = new Size(120, 25),
            PlaceholderText = "0xDEADBEEF",
            Font = new Font("Consolas", 9F),
            BorderStyle = BorderStyle.FixedSingle
        };

        btnSetRegister = new Button
        {
            Text = "💾 Set Register",
            Location = new Point(5, 125),
            Size = new Size(120, 35),
            BackColor = Color.FromArgb(52, 168, 83),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnSetRegister.FlatAppearance.BorderSize = 0;
        btnSetRegister.Click += BtnSetRegister_Click;

        var separator1 = new Panel { Location = new Point(5, 170), Size = new Size(120, 2), BackColor = Color.Gray };

        var lblInfo = new Label
        {
            Text = "ℹ️ Register Info:",
            Location = new Point(5, 180),
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };

        var txtRegisterInfo = new RichTextBox
        {
            Location = new Point(5, 200),
            Size = new Size(120, 150),
            ReadOnly = true,
            BackColor = Color.FromArgb(250, 250, 250),
            Font = new Font("Segoe UI", 8F),
            BorderStyle = BorderStyle.FixedSingle
        };
        txtRegisterInfo.Text = "Select a register from the grid to view detailed information about its purpose and usage in PowerPC architecture.";

        // Handle register selection
        dgvRegisters.SelectionChanged += (s, e) =>
        {
            if (dgvRegisters.SelectedRows.Count > 0)
            {
                var row = dgvRegisters.SelectedRows[0];
                var registerName = row.Cells["Register"].Value?.ToString() ?? "";
                txtRegisterInfo.Text = GetRegisterDescription(registerName);
            }
        };

        controlPanel.Controls.AddRange(new Control[] {
            lblControls, lblSetRegister, txtRegisterName, txtRegisterValue,
            btnSetRegister, separator1, lblInfo, txtRegisterInfo
        });

        // Add to table layout
        mainPanel.SetColumnSpan(toolbarPanel, 2);
        mainPanel.Controls.Add(toolbarPanel, 0, 0);
        mainPanel.Controls.Add(gridPanel, 0, 1);
        mainPanel.Controls.Add(controlPanel, 1, 1);

        tab.Controls.Add(mainPanel);
        return tab;
    }

    private TabPage CreateBuildTab()
    {
        var tab = new TabPage("🏗️ GUI Builder");
        tab.BackColor = Color.FromArgb(250, 250, 250);
        
        // Title Label
        var lblTitle = new Label
        {
            Text = "⚡ Build Standalone Mod Tool",
            Location = new Point(20, 20),
            Size = new Size(700, 35),
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 90, 160)
        };
        tab.Controls.Add(lblTitle);
        
        var lblSubtitle = new Label
        {
            Text = "Generate a custom GUI application from your selected offsets with professional templates",
            Location = new Point(20, 60),
            Size = new Size(700, 25),
            Font = new Font("Segoe UI", 10F),
            ForeColor = Color.FromArgb(100, 100, 100)
        };
        tab.Controls.Add(lblSubtitle);
        
        // Configuration Panel
        var configPanel = new GroupBox
        {
            Text = " ⚙️ Project Configuration ",
            Location = new Point(20, 100),
            Size = new Size(500, 200),
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            BackColor = Color.White
        };
        
        var lblProject = new Label { Text = "Project Name:", Location = new Point(20, 35), AutoSize = true };
        txtProjectName = new TextBox
        {
            Location = new Point(140, 32),
            Size = new Size(330, 27),
            Text = "MyModTool",
            Font = new Font("Segoe UI", 10F)
        };
        
        var lblTemplate = new Label { Text = "GUI Template:", Location = new Point(20, 75), AutoSize = true };
        cmbGUITemplate = new ComboBox
        {
            Location = new Point(140, 72),
            Size = new Size(330, 27),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 10F)
        };
        
        cmbGUITemplate.Items.AddRange(new object[]
        {
            "Modern Dark",
            "Gaming RGB",
            "Clean White",
            "Cyberpunk Neon",
            "Minimal Flat",
            "Matrix Green",
            "Retro Arcade",
            "Professional Blue"
        });
        cmbGUITemplate.SelectedIndex = 0;
        cmbGUITemplate.SelectedIndexChanged += (s, e) => UpdateTemplatePreview();
        
        var chkAutoConnect = new CheckBox
        {
            Text = "Auto-connect on startup",
            Location = new Point(140, 115),
            Size = new Size(200, 25),
            Checked = true,
            Font = new Font("Segoe UI", 9.5F)
        };
        
        var chkIncludeScript = new CheckBox
        {
            Text = "Include script engine",
            Location = new Point(140, 145),
            Size = new Size(200, 25),
            Checked = true,
            Font = new Font("Segoe UI", 9.5F)
        };
        
        configPanel.Controls.AddRange(new Control[] { lblProject, txtProjectName, lblTemplate, cmbGUITemplate, chkAutoConnect, chkIncludeScript });
        tab.Controls.Add(configPanel);
        
        // Template Preview Panel
        var previewPanel = new GroupBox
        {
            Text = " 👁️ Template Preview ",
            Location = new Point(540, 100),
            Size = new Size(430, 400),
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            BackColor = Color.White
        };
        
        picTemplatePreview = new PictureBox
        {
            Location = new Point(15, 30),
            Size = new Size(400, 330),
            BorderStyle = BorderStyle.FixedSingle,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(240, 240, 245)
        };
        
        var lblPreviewHint = new Label
        {
            Text = "Preview shows approximate color scheme",
            Location = new Point(15, 370),
            Size = new Size(400, 20),
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleCenter
        };
        
        previewPanel.Controls.AddRange(new Control[] { picTemplatePreview, lblPreviewHint });
        tab.Controls.Add(previewPanel);
        
        // Offset Selection Panel
        var offsetPanel = new GroupBox
        {
            Text = " ✅ Select Offsets to Include ",
            Location = new Point(20, 320),
            Size = new Size(500, 180),
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            BackColor = Color.White
        };
        
        lstSelectedOffsets = new ListBox
        {
            Location = new Point(15, 30),
            Size = new Size(470, 100),
            SelectionMode = SelectionMode.MultiExtended,
            Font = new Font("Consolas", 9F)
        };
        
        var btnAddOffsets = new Button
        {
            Text = "➕ Add from Offset Calculator",
            Location = new Point(15, 140),
            Size = new Size(225, 30),
            BackColor = Color.FromArgb(70, 130, 255),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        btnAddOffsets.FlatAppearance.BorderSize = 0;
        btnAddOffsets.Click += BtnAddOffsets_Click;
        
        var btnClearOffsets = new Button
        {
            Text = "🗑️ Clear",
            Location = new Point(250, 140),
            Size = new Size(110, 30),
            BackColor = Color.FromArgb(220, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        btnClearOffsets.FlatAppearance.BorderSize = 0;
        btnClearOffsets.Click += (s, e) => lstSelectedOffsets.Items.Clear();
        
        offsetPanel.Controls.AddRange(new Control[] { lstSelectedOffsets, btnAddOffsets, btnClearOffsets });
        tab.Controls.Add(offsetPanel);
        
        // Build Button
        btnBuildGUI = new Button
        {
            Text = "🚀 BUILD STANDALONE GUI",
            Location = new Point(20, 520),
            Size = new Size(500, 50),
            BackColor = Color.FromArgb(0, 180, 80),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 14F, FontStyle.Bold)
        };
        btnBuildGUI.FlatAppearance.BorderSize = 0;
        btnBuildGUI.Click += BtnBuildGUI_Click;
        tab.Controls.Add(btnBuildGUI);
        
        // Status/Info Panel
        var infoPanel = new GroupBox
        {
            Text = " ℹ️ Build Information ",
            Location = new Point(540, 520),
            Size = new Size(430, 50),
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            BackColor = Color.White
        };
        
        var lblInfo = new Label
        {
            Text = "✨ The generated project will be ready to compile and distribute!",
            Location = new Point(15, 25),
            Size = new Size(400, 20),
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(70, 130, 255)
        };
        
        infoPanel.Controls.Add(lblInfo);
        tab.Controls.Add(infoPanel);
        
        // Initialize template preview
        UpdateTemplatePreview();
        
        return tab;
    }
    
    private void UpdateTemplatePreview()
    {
        if (cmbGUITemplate == null || picTemplatePreview == null) return;
        
        // Create a preview bitmap showing the template colors
        var bmp = new Bitmap(400, 330);
        using (var g = Graphics.FromImage(bmp))
        {
            var templateName = cmbGUITemplate.SelectedItem?.ToString() ?? "Modern Dark";
            var template = templateName switch
            {
                "Modern Dark" => GUIBuilder.GUITemplate.ModernDark,
                "Gaming RGB" => GUIBuilder.GUITemplate.GamingRGB,
                "Clean White" => GUIBuilder.GUITemplate.CleanWhite,
                "Cyberpunk Neon" => GUIBuilder.GUITemplate.CyberpunkNeon,
                "Minimal Flat" => GUIBuilder.GUITemplate.MinimalFlat,
                "Matrix Green" => GUIBuilder.GUITemplate.MatrixGreen,
                "Retro Arcade" => GUIBuilder.GUITemplate.RetroArcade,
                "Professional Blue" => GUIBuilder.GUITemplate.ProfessionalBlue,
                _ => GUIBuilder.GUITemplate.ModernDark
            };
            
            // Draw a mockup based on template
            var colors = GetPreviewColors(template);
            
            // Background
            g.FillRectangle(new SolidBrush(colors.Background), 0, 0, 400, 330);
            
            // Header
            g.FillRectangle(new SolidBrush(colors.Panel), 0, 0, 400, 60);
            g.DrawString(templateName, new Font("Segoe UI", 12F, FontStyle.Bold), new SolidBrush(colors.Text), 10, 20);
            
            // Buttons
            g.FillRectangle(new SolidBrush(colors.Button), 20, 80, 360, 40);
            g.DrawString("Example Mod Button", new Font("Segoe UI", 10F), Brushes.White, 30, 92);
            
            g.FillRectangle(new SolidBrush(colors.AccentButton), 20, 130, 360, 40);
            g.DrawString("Another Mod Button", new Font("Segoe UI", 10F), Brushes.White, 30, 142);
            
            // Console
            g.FillRectangle(new SolidBrush(colors.Console), 20, 190, 360, 120);
            g.DrawString($"Console Output ({templateName})", new Font("Consolas", 9F), new SolidBrush(colors.ConsoleText), 25, 200);
        }
        
        picTemplatePreview.Image = bmp;
    }
    
    private PreviewColors GetPreviewColors(GUIBuilder.GUITemplate template)
    {
        return template switch
        {
            GUIBuilder.GUITemplate.ModernDark => new PreviewColors
            {
                Background = Color.FromArgb(20, 20, 25),
                Panel = Color.FromArgb(30, 30, 35),
                Button = Color.FromArgb(70, 130, 255),
                AccentButton = Color.FromArgb(255, 90, 130),
                Console = Color.FromArgb(15, 15, 20),
                ConsoleText = Color.FromArgb(0, 255, 100),
                Text = Color.FromArgb(220, 220, 220)
            },
            GUIBuilder.GUITemplate.GamingRGB => new PreviewColors
            {
                Background = Color.FromArgb(18, 18, 20),
                Panel = Color.FromArgb(25, 25, 28),
                Button = Color.FromArgb(255, 0, 150),
                AccentButton = Color.FromArgb(0, 255, 200),
                Console = Color.FromArgb(10, 10, 15),
                ConsoleText = Color.FromArgb(0, 255, 255),
                Text = Color.White
            },
            GUIBuilder.GUITemplate.CleanWhite => new PreviewColors
            {
                Background = Color.FromArgb(250, 250, 252),
                Panel = Color.FromArgb(240, 240, 245),
                Button = Color.FromArgb(0, 120, 215),
                AccentButton = Color.FromArgb(255, 140, 0),
                Console = Color.FromArgb(245, 245, 248),
                ConsoleText = Color.FromArgb(60, 60, 60),
                Text = Color.FromArgb(30, 30, 30)
            },
            GUIBuilder.GUITemplate.CyberpunkNeon => new PreviewColors
            {
                Background = Color.FromArgb(10, 10, 15),
                Panel = Color.FromArgb(15, 15, 22),
                Button = Color.FromArgb(255, 0, 255),
                AccentButton = Color.FromArgb(0, 255, 255),
                Console = Color.FromArgb(5, 5, 10),
                ConsoleText = Color.FromArgb(0, 255, 255),
                Text = Color.FromArgb(0, 255, 255)
            },
            GUIBuilder.GUITemplate.MatrixGreen => new PreviewColors
            {
                Background = Color.Black,
                Panel = Color.FromArgb(5, 10, 5),
                Button = Color.FromArgb(0, 180, 0),
                AccentButton = Color.FromArgb(0, 255, 100),
                Console = Color.Black,
                ConsoleText = Color.FromArgb(0, 255, 0),
                Text = Color.FromArgb(0, 255, 0)
            },
            _ => new PreviewColors
            {
                Background = Color.FromArgb(240, 244, 248),
                Panel = Color.FromArgb(230, 236, 242),
                Button = Color.FromArgb(30, 90, 160),
                AccentButton = Color.FromArgb(20, 120, 200),
                Console = Color.FromArgb(235, 240, 245),
                ConsoleText = Color.FromArgb(40, 60, 80),
                Text = Color.FromArgb(20, 40, 60)
            }
        };
    }
    
    private class PreviewColors
    {
        public Color Background { get; set; }
        public Color Panel { get; set; }
        public Color Button { get; set; }
        public Color AccentButton { get; set; }
        public Color Console { get; set; }
        public Color ConsoleText { get; set; }
        public Color Text { get; set; }
    }
    
    private void BtnAddOffsets_Click(object? sender, EventArgs e)
    {
        if (lstSelectedOffsets == null) return;
        
        // Show offset selection dialog
        var selectionForm = new Form
        {
            Text = "Select Offsets for GUI Builder",
            Size = new Size(900, 650),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.FromArgb(250, 250, 250)
        };
        
        var lblTitle = new Label
        {
            Text = "✅ Select Offsets and Categories",
            Location = new Point(20, 20),
            Size = new Size(850, 30),
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 90, 160)
        };
        selectionForm.Controls.Add(lblTitle);
        
        // Category filter
        var lblCategory = new Label
        {
            Text = "Filter by Category:",
            Location = new Point(20, 60),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F)
        };
        
        var cmbCategoryFilter = new ComboBox
        {
            Location = new Point(150, 57),
            Size = new Size(200, 25),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 9.5F)
        };
        cmbCategoryFilter.Items.Add("All Categories");
        foreach (var category in _offsetManager.GetAllOffsets().Select(o => o.Category).Distinct().OrderBy(c => c))
        {
            cmbCategoryFilter.Items.Add(category);
        }
        cmbCategoryFilter.SelectedIndex = 0;
        
        selectionForm.Controls.AddRange(new Control[] { lblCategory, cmbCategoryFilter });
        
        // DataGridView for offset selection
        var dgvSelection = new DataGridView
        {
            Location = new Point(20, 95),
            Size = new Size(850, 400),
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.Fixed3D,
            Font = new Font("Segoe UI", 9F),
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        
        // Add checkbox column
        var chkCol = new DataGridViewCheckBoxColumn
        {
            HeaderText = "Select",
            Name = "Select",
            Width = 60,
            ReadOnly = false
        };
        dgvSelection.Columns.Add(chkCol);
        
        dgvSelection.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", Name = "Name", ReadOnly = true });
        dgvSelection.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Category", Name = "Category", ReadOnly = true });
        dgvSelection.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Address", Name = "Address", ReadOnly = true });
        dgvSelection.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type", Name = "Type", ReadOnly = true });
        dgvSelection.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Description", Name = "Description", ReadOnly = true });
        
        // Style header
        dgvSelection.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(70, 130, 255);
        dgvSelection.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        dgvSelection.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        dgvSelection.EnableHeadersVisualStyles = false;
        
        // Populate grid
        Action refreshGrid = () =>
        {
            dgvSelection.Rows.Clear();
            var filter = cmbCategoryFilter.SelectedItem?.ToString();
            
            foreach (var offset in _offsetManager.GetAllOffsets())
            {
                if (filter != "All Categories" && offset.Category != filter)
                    continue;
                
                var row = dgvSelection.Rows.Add();
                dgvSelection.Rows[row].Cells["Select"].Value = false;
                dgvSelection.Rows[row].Cells["Name"].Value = offset.Name;
                dgvSelection.Rows[row].Cells["Category"].Value = offset.Category;
                dgvSelection.Rows[row].Cells["Address"].Value = offset.BaseAddress.ToString("X8");
                dgvSelection.Rows[row].Cells["Type"].Value = offset.Type;
                dgvSelection.Rows[row].Cells["Description"].Value = offset.Description;
                dgvSelection.Rows[row].Tag = offset; // Store offset object
            }
        };
        
        refreshGrid();
        cmbCategoryFilter.SelectedIndexChanged += (s, e) => refreshGrid();
        
        selectionForm.Controls.Add(dgvSelection);
        
        // Buttons
        var btnSelectAll = new Button
        {
            Text = "✓ Select All",
            Location = new Point(20, 510),
            Size = new Size(120, 35),
            BackColor = Color.FromArgb(70, 130, 255),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        btnSelectAll.FlatAppearance.BorderSize = 0;
        btnSelectAll.Click += (s, e) =>
        {
            foreach (DataGridViewRow row in dgvSelection.Rows)
            {
                row.Cells["Select"].Value = true;
            }
        };
        
        var btnDeselectAll = new Button
        {
            Text = "✗ Deselect All",
            Location = new Point(150, 510),
            Size = new Size(120, 35),
            BackColor = Color.FromArgb(150, 150, 150),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        btnDeselectAll.FlatAppearance.BorderSize = 0;
        btnDeselectAll.Click += (s, e) =>
        {
            foreach (DataGridViewRow row in dgvSelection.Rows)
            {
                row.Cells["Select"].Value = false;
            }
        };
        
        var btnAdd = new Button
        {
            Text = "➕ Add Selected to GUI Builder",
            Location = new Point(600, 510),
            Size = new Size(270, 50),
            BackColor = Color.FromArgb(0, 180, 80),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold)
        };
        btnAdd.FlatAppearance.BorderSize = 0;
        btnAdd.Click += (s, e) =>
        {
            int added = 0;
            foreach (DataGridViewRow row in dgvSelection.Rows)
            {
                if (row.Cells["Select"].Value is bool selected && selected)
                {
                    if (row.Tag is OffsetManager.OffsetEntry offset)
                    {
                        var displayText = $"{offset.Name} ({offset.Category})";
                        if (!lstSelectedOffsets.Items.Contains(displayText))
                        {
                            lstSelectedOffsets.Items.Add(displayText);
                            added++;
                        }
                    }
                }
            }
            
            LogMessage($"✅ Added {added} offset(s) to GUI builder");
            selectionForm.Close();
        };
        
        var btnCancel = new Button
        {
            Text = "✗ Cancel",
            Location = new Point(20, 560),
            Size = new Size(120, 35),
            BackColor = Color.FromArgb(200, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.Click += (s, e) => selectionForm.Close();
        
        selectionForm.Controls.AddRange(new Control[] { btnSelectAll, btnDeselectAll, btnAdd, btnCancel });
        
        // Show dialog
        selectionForm.ShowDialog(this);
    }
    
    private async void BtnBuildGUI_Click(object? sender, EventArgs e)
    {
        if (txtProjectName == null || cmbGUITemplate == null || lstSelectedOffsets == null) return;
        
        if (lstSelectedOffsets.Items.Count == 0)
        {
            MessageBox.Show("Please add at least one offset to include in the GUI.",
                "No Offsets Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        
        // Select output directory
        using var folderDialog = new FolderBrowserDialog
        {
            Description = "Select where to create the project folder",
            UseDescriptionForTitle = true
        };
        
        if (folderDialog.ShowDialog() != DialogResult.OK) return;
        
        try
        {
            LogMessage("🏗️ Building standalone GUI project...");
            if (btnBuildGUI != null)
            {
                btnBuildGUI.Enabled = false;
                btnBuildGUI.Text = "⏳ Building...";
            }
            
            // Get selected offsets
            var selectedOffsets = new List<OffsetManager.OffsetEntry>();
            foreach (var item in lstSelectedOffsets.Items)
            {
                var itemStr = item.ToString()!;
                var offsetName = itemStr.Substring(0, itemStr.LastIndexOf(" ("));
                var offset = _offsetManager.GetOffset(offsetName);
                if (offset != null)
                {
                    selectedOffsets.Add(offset);
                }
            }
            
            // Build options
            var templateSelection = cmbGUITemplate.SelectedItem?.ToString() switch
            {
                "Modern Dark" => GUIBuilder.GUITemplate.ModernDark,
                "Gaming RGB" => GUIBuilder.GUITemplate.GamingRGB,
                "Clean White" => GUIBuilder.GUITemplate.CleanWhite,
                "Cyberpunk Neon" => GUIBuilder.GUITemplate.CyberpunkNeon,
                "Minimal Flat" => GUIBuilder.GUITemplate.MinimalFlat,
                "Matrix Green" => GUIBuilder.GUITemplate.MatrixGreen,
                "Retro Arcade" => GUIBuilder.GUITemplate.RetroArcade,
                "Professional Blue" => GUIBuilder.GUITemplate.ProfessionalBlue,
                _ => (GUIBuilder.GUITemplate?)null
            } ?? GUIBuilder.GUITemplate.ModernDark;
            
            var options = new GUIBuilder.BuildOptions
            {
                ProjectName = txtProjectName.Text,
                Template = templateSelection,
                XboxIP = txtIpAddress.Text,
                XboxPort = int.Parse(txtPort.Text),
                Offsets = selectedOffsets,
                OutputDirectory = folderDialog.SelectedPath
            };
            
            // Generate project
            var success = await _guiBuilder.GenerateProject(options);
            
            if (success)
            {
                var projectPath = Path.Combine(folderDialog.SelectedPath, txtProjectName.Text);
                LogMessage($"✅ Project generated successfully!");
                LogMessage($"📁 Location: {projectPath}");
                LogMessage($"🔨 To build: cd \"{projectPath}\" && dotnet build");
                LogMessage($"▶️ To run: cd \"{projectPath}\" && dotnet run");
                
                var result = MessageBox.Show(
                    $"Project created successfully!\n\n" +
                    $"Location: {projectPath}\n\n" +
                    $"Would you like to open the project folder?",
                    "Build Complete",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                
                if (result == DialogResult.Yes)
                {
                    System.Diagnostics.Process.Start("explorer.exe", projectPath);
                }
            }
            else
            {
                LogMessage("❌ Failed to generate project");
                MessageBox.Show("Failed to generate project. Check console for details.",
                    "Build Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Error building GUI: {ex.Message}");
            MessageBox.Show($"Error: {ex.Message}", "Build Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (btnBuildGUI != null)
            {
                btnBuildGUI.Enabled = true;
                btnBuildGUI.Text = "🚀 BUILD STANDALONE GUI";
            }
        }
    }

    private TabPage CreateToolsTab()
    {
        var tab = new TabPage("🔧 Tools & Search");
        tab.BackColor = Color.FromArgb(250, 250, 250);

        // Search Panel
        var searchPanel = new Panel
        {
            Location = new Point(15, 15),
            Size = new Size(750, 220),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblSearchTitle = new Label { 
            Text = "🔍 Memory Search", 
            Location = new Point(15, 15), 
            Font = new Font("Segoe UI", 10F, FontStyle.Bold), 
            AutoSize = true 
        };

        var lblSearch = new Label { 
            Text = "Hex Pattern:", 
            Location = new Point(15, 50), 
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F)
        };
        txtSearchHex = new TextBox { 
            Location = new Point(120, 47), 
            Size = new Size(400, 27), 
            PlaceholderText = "e.g., 48 65 6C 6C 6F or 4865 6C6C6F",
            Font = new Font("Consolas", 10F),
            BackColor = Color.FromArgb(250, 250, 250)
        };

        btnSearch = new Button { 
            Text = "🔎 Search", 
            Location = new Point(535, 45), 
            Size = new Size(120, 32), 
            BackColor = Color.FromArgb(33, 150, 243),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnSearch.FlatAppearance.BorderSize = 0;
        btnSearch.Click += BtnSearch_Click;

        var lblResults = new Label { 
            Text = "Search Results:", 
            Location = new Point(15, 90), 
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };

        lstSearchResults = new ListBox
        {
            Location = new Point(15, 115),
            Size = new Size(720, 90),
            Font = new Font("Consolas", 9.5F),
            BackColor = Color.FromArgb(250, 250, 250)
        };
        lstSearchResults.DoubleClick += LstSearchResults_DoubleClick;

        searchPanel.Controls.AddRange(new Control[] { 
            lblSearchTitle, lblSearch, txtSearchHex, btnSearch, lblResults, lstSearchResults 
        });

        // Quick Write Panel
        var writePanel = new Panel
        {
            Location = new Point(780, 15),
            Size = new Size(730, 220),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblWriteTitle = new Label { 
            Text = "⚡ Quick Write", 
            Location = new Point(15, 15), 
            Font = new Font("Segoe UI", 10F, FontStyle.Bold), 
            AutoSize = true 
        };

        var lblWriteAddr = new Label { 
            Text = "Address:", 
            Location = new Point(15, 50), 
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F)
        };
        txtWriteAddress = new TextBox { 
            Location = new Point(100, 47), 
            Size = new Size(180, 27), 
            Text = "0x82000000",
            Font = new Font("Consolas", 10F),
            BackColor = Color.FromArgb(250, 250, 250)
        };

        var lblType = new Label { 
            Text = "Type:", 
            Location = new Point(300, 50), 
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F)
        };
        cmbWriteType = new ComboBox
        {
            Location = new Point(350, 47),
            Size = new Size(150, 27),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 9.5F)
        };
        cmbWriteType.Items.AddRange(new[] { "Byte", "Int16", "Int32", "Int64", "Float", "Double", "String", "Hex Bytes" });
        cmbWriteType.SelectedIndex = 2;

        var lblValue = new Label { 
            Text = "Value:", 
            Location = new Point(15, 90), 
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F)
        };
        txtWriteValue = new TextBox { 
            Location = new Point(100, 87), 
            Size = new Size(400, 27),
            Font = new Font("Consolas", 10F),
            BackColor = Color.FromArgb(250, 250, 250)
        };

        btnQuickWrite = new Button { 
            Text = "✍️ Write", 
            Location = new Point(515, 85), 
            Size = new Size(100, 32), 
            BackColor = Color.FromArgb(255, 152, 0),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnQuickWrite.FlatAppearance.BorderSize = 0;
        btnQuickWrite.Click += BtnQuickWrite_Click;

        btnPokeInt32 = new Button { 
            Text = "🔢 Poke Int32", 
            Location = new Point(15, 135), 
            Size = new Size(130, 35),
            BackColor = Color.FromArgb(156, 39, 176),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnPokeInt32.FlatAppearance.BorderSize = 0;
        btnPokeInt32.Click += BtnPokeInt32_Click;

        btnPokeFloat = new Button { 
            Text = "📊 Poke Float", 
            Location = new Point(155, 135), 
            Size = new Size(130, 35),
            BackColor = Color.FromArgb(63, 81, 181),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnPokeFloat.FlatAppearance.BorderSize = 0;
        btnPokeFloat.Click += BtnPokeFloat_Click;

        btnFreeze = new Button { 
            Text = "❄️ Freeze/Unfreeze", 
            Location = new Point(295, 135), 
            Size = new Size(160, 35), 
            BackColor = Color.FromArgb(0, 188, 212),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnFreeze.FlatAppearance.BorderSize = 0;
        btnFreeze.Click += BtnFreeze_Click;

        writePanel.Controls.AddRange(new Control[] {
            lblWriteTitle, lblWriteAddr, txtWriteAddress, lblType, cmbWriteType,
            lblValue, txtWriteValue, btnQuickWrite, btnPokeInt32, btnPokeFloat, btnFreeze
        });

        // Snapshot Panel
        var snapshotPanel = new Panel
        {
            Location = new Point(15, 250),
            Size = new Size(1495, 165),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblSnapshotTitle = new Label { 
            Text = "📸 Memory Snapshot & Compare", 
            Location = new Point(15, 15), 
            Font = new Font("Segoe UI", 10F, FontStyle.Bold), 
            AutoSize = true 
        };

        var lblSnapshotDesc = new Label { 
            Text = "Capture memory snapshots to track changes over time. Useful for finding dynamic values.", 
            Location = new Point(15, 45), 
            AutoSize = true,
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(96, 125, 139)
        };

        btnSaveSnapshot = new Button { 
            Text = "📸 Take Snapshot", 
            Location = new Point(15, 75), 
            Size = new Size(160, 40),
            BackColor = Color.FromArgb(156, 39, 176),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnSaveSnapshot.FlatAppearance.BorderSize = 0;
        btnSaveSnapshot.Click += BtnSaveSnapshot_Click;

        btnLoadSnapshot = new Button { 
            Text = "📂 Load Snapshot", 
            Location = new Point(190, 75), 
            Size = new Size(160, 40),
            BackColor = Color.FromArgb(33, 150, 243),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnLoadSnapshot.FlatAppearance.BorderSize = 0;
        btnLoadSnapshot.Click += BtnLoadSnapshot_Click;

        btnCompare = new Button { 
            Text = "🔄 Compare Snapshots", 
            Location = new Point(365, 75), 
            Size = new Size(180, 40),
            BackColor = Color.FromArgb(255, 152, 0),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnCompare.FlatAppearance.BorderSize = 0;
        btnCompare.Click += BtnCompare_Click;

        var lblSnapshotInfo = new Label { 
            Text = "💡 Tip: Read memory first, take a snapshot, perform an action, read again, then compare!", 
            Location = new Point(15, 125), 
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Italic),
            ForeColor = Color.FromArgb(33, 150, 243)
        };

        snapshotPanel.Controls.AddRange(new Control[] {
            lblSnapshotTitle, lblSnapshotDesc, btnSaveSnapshot, btnLoadSnapshot, btnCompare, lblSnapshotInfo
        });

        tab.Controls.AddRange(new Control[] { searchPanel, writePanel, snapshotPanel });
        return tab;
    }

    private async Task ExecuteScript(string script)
    {
        if (_scriptExecutor == null)
        {
            LogMessage("❌ Not connected! Connect to Xbox first.");
            return;
        }
        
        await _scriptExecutor.ExecuteAsync(script);
    }

    private async Task RefreshMemoryView()
    {
        try
        {
            uint address = ParseAddress(txtAddress.Text);
            uint length = uint.Parse(txtLength.Text);

            var data = await _xbdmClient!.GetMemoryAsync(address, length);

            if (data != null)
            {
                _currentMemory = data;
                _currentAddress = address;

                txtMemoryView.Clear();
                txtMemoryView.Text = FormatHexDump(data, address);

                // Update the address display to ensure it's properly formatted
                txtAddress.Text = $"0x{address:X8}";
            }
        }
        catch { }
    }

    private void ChkAutoRefresh_CheckedChanged(object? sender, EventArgs e)
    {
        if (chkAutoRefresh.Checked)
        {
            _autoRefreshTimer!.Interval = (int)numRefreshInterval.Value;
            _autoRefreshTimer.Start();
            LogMessage($"✅ Auto-refresh enabled ({numRefreshInterval.Value}ms)");
        }
        else
        {
            _autoRefreshTimer!.Stop();
            LogMessage("Auto-refresh disabled");
        }
    }

    private async void BtnRead_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !_xbdmClient.IsConnected)
        {
            LogMessage("❌ Not connected to Xbox!");
            return;
        }

        try
        {
            uint address = ParseAddress(txtAddress.Text);
            uint length = uint.Parse(txtLength.Text);

            LogMessage($"Reading {length} bytes from 0x{address:X8}...");

            var data = await _xbdmClient.GetMemoryAsync(address, length);

            if (data != null)
            {
                _currentMemory = data;
                _currentAddress = address;

                txtMemoryView.Clear();
                txtMemoryView.Text = FormatHexDump(data, address);

                // Update the address display to ensure it's properly formatted
                txtAddress.Text = $"0x{address:X8}";

                LogMessage($"✅ Read {data.Length} bytes successfully");
            }
            else
            {
                LogMessage("❌ Failed to read memory - check address and length");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Error: {ex.Message}");
        }
    }

    private async void BtnWrite_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !_xbdmClient.IsConnected)
        {
            LogMessage("❌ Not connected to Xbox!");
            return;
        }

        var hexData = Microsoft.VisualBasic.Interaction.InputBox(
            "Enter hex data (space or comma separated):\ne.g., 00 01 02 FF or 00,01,02,FF", 
            "Write Memory");
            
        if (string.IsNullOrEmpty(hexData))
            return;

        try
        {
            uint address = ParseAddress(txtAddress.Text);

            var cleanedData = hexData.Replace(" ", "").Replace(",", "").Replace("0x", "");
            var bytes = new List<byte>();

            for (int i = 0; i < cleanedData.Length; i += 2)
            {
                bytes.Add(Convert.ToByte(cleanedData.Substring(i, 2), 16));
            }

            LogMessage($"Writing {bytes.Count} bytes to 0x{address:X8}...");

            bool success = await _xbdmClient.SetMemoryAsync(address, bytes.ToArray());

            if (success)
            {
                LogMessage($"✅ Written {bytes.Count} bytes successfully");
                await Task.Delay(100);
                btnRead.PerformClick();
            }
            else
            {
                LogMessage("❌ Failed to write memory");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Error: {ex.Message}");
        }
    }

    private async void BtnQuickWrite_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !_xbdmClient.IsConnected)
        {
            LogMessage("❌ Not connected to Xbox!");
            return;
        }

        try
        {
            uint address = ParseAddress(txtWriteAddress.Text);
            byte[] bytes;

            switch (cmbWriteType.SelectedItem?.ToString())
            {
                case "Byte":
                    bytes = new[] { byte.Parse(txtWriteValue.Text) };
                    break;
                case "Int16":
                    bytes = BeBytes(short.Parse(txtWriteValue.Text));
                    break;
                case "Int32":
                    bytes = BeBytes(int.Parse(txtWriteValue.Text));
                    break;
                case "Int64":
                    bytes = BeBytes(long.Parse(txtWriteValue.Text));
                    break;
                case "Float":
                    bytes = BeBytes(float.Parse(txtWriteValue.Text));
                    break;
                case "Double":
                    bytes = BeBytes(double.Parse(txtWriteValue.Text));
                    break;
                case "String":
                    bytes = Encoding.ASCII.GetBytes(txtWriteValue.Text);
                    break;
                case "Hex Bytes":
                    var hex = txtWriteValue.Text.Replace(" ", "").Replace(",", "");
                    bytes = Enumerable.Range(0, hex.Length / 2)
                        .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                        .ToArray();
                    break;
                default:
                    LogMessage("❌ Please select a data type");
                    return;
            }

            LogMessage($"Writing {cmbWriteType.SelectedItem} ({bytes.Length} bytes) to 0x{address:X8}...");
            
            bool success = await _xbdmClient.SetMemoryAsync(address, bytes);
            
            if (success)
            {
                LogMessage($"✅ Value written: {BitConverter.ToString(bytes).Replace("-", " ")}");
            }
            else
            {
                LogMessage("❌ Failed to write value");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Error: {ex.Message}");
        }
    }

    private async void BtnPokeInt32_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !_xbdmClient.IsConnected)
        {
            LogMessage("❌ Not connected!");
            return;
        }

        try
        {
            uint address = ParseAddress(txtWriteAddress.Text);
            int value = int.Parse(txtWriteValue.Text);
            var bytes = BeBytes(value);
            
            bool success = await _xbdmClient.SetMemoryAsync(address, bytes);
            LogMessage(success ? $"✅ Poked Int32: {value}" : "❌ Failed");
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Error: {ex.Message}");
        }
    }

    private async void BtnPokeFloat_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !_xbdmClient.IsConnected)
        {
            LogMessage("❌ Not connected!");
            return;
        }

        try
        {
            uint address = ParseAddress(txtWriteAddress.Text);
            float value = float.Parse(txtWriteValue.Text);
            var bytes = BeBytes(value);
            
            bool success = await _xbdmClient.SetMemoryAsync(address, bytes);
            LogMessage(success ? $"✅ Poked Float: {value}" : "❌ Failed");
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Error: {ex.Message}");
        }
    }

    private void BtnFreeze_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !_xbdmClient.IsConnected)
        {
            LogMessage("❌ Not connected!");
            return;
        }

        try
        {
            uint address = ParseAddress(txtWriteAddress.Text);
            
            // Xbox 360 memory is big-endian (the old code froze little-endian values)
            byte[] bytes = new byte[4];
            switch (cmbWriteType.SelectedItem?.ToString())
            {
                case "Int32":
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, int.Parse(txtWriteValue.Text));
                    break;
                case "Float":
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(bytes, float.Parse(txtWriteValue.Text, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    LogMessage("❌ Select Int32 or Float to freeze");
                    return;
            }

            if (_freezer == null) return;
            if (_freezer.Unfreeze(address))
            {
                LogMessage($"✅ Unfroze 0x{address:X8}");
            }
            else
            {
                _freezer.Freeze(address, bytes, $"0x{address:X8}");
                LogMessage($"✅ Froze 0x{address:X8} = {BitConverter.ToString(bytes).Replace("-", " ")}");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Error: {ex.Message}");
        }
    }

    private void BtnSearch_Click(object? sender, EventArgs e)
    {
        if (_currentMemory == null)
        {
            LogMessage("❌ No memory loaded. Read memory first!");
            return;
        }

        try
        {
            var searchHex = txtSearchHex.Text.Replace(" ", "").Replace(",", "");
            var searchBytes = Enumerable.Range(0, searchHex.Length / 2)
                .Select(i => Convert.ToByte(searchHex.Substring(i * 2, 2), 16))
                .ToArray();

            lstSearchResults.Items.Clear();
            int found = 0;

            for (int i = 0; i <= _currentMemory.Length - searchBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < searchBytes.Length; j++)
                {
                    if (_currentMemory[i + j] != searchBytes[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    uint foundAddress = _currentAddress + (uint)i;
                    lstSearchResults.Items.Add($"0x{foundAddress:X8}");
                    found++;
                }
            }

            LogMessage($"Search complete. Found {found} match(es)");
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Search error: {ex.Message}");
        }
    }

    private void LstSearchResults_DoubleClick(object? sender, EventArgs e)
    {
        if (lstSearchResults.SelectedItem != null)
        {
            txtAddress.Text = lstSearchResults.SelectedItem.ToString() ?? "";
            txtWriteAddress.Text = lstSearchResults.SelectedItem.ToString() ?? "";
        }
    }

    private void BtnSaveSnapshot_Click(object? sender, EventArgs e)
    {
        if (_currentMemory == null)
        {
            LogMessage("❌ No memory loaded. Read memory first!");
            return;
        }

        _snapshotMemory = (byte[])_currentMemory.Clone();
        LogMessage($"✅ Snapshot saved ({_snapshotMemory.Length} bytes)");
    }

    private void BtnLoadSnapshot_Click(object? sender, EventArgs e)
    {
        if (_snapshotMemory == null)
        {
            LogMessage("❌ No snapshot saved!");
            return;
        }

        _currentMemory = (byte[])_snapshotMemory.Clone();
        txtMemoryView.Clear();
        txtMemoryView.Text = FormatHexDump(_currentMemory, _currentAddress);
        LogMessage($"✅ Snapshot loaded ({_currentMemory.Length} bytes)");
    }

    private void BtnCompare_Click(object? sender, EventArgs e)
    {
        if (_currentMemory == null || _snapshotMemory == null)
        {
            LogMessage("❌ Need both current memory and snapshot to compare!");
            return;
        }

        if (_currentMemory.Length != _snapshotMemory.Length)
        {
            LogMessage("❌ Memory sizes don't match!");
            return;
        }

        var differences = new List<int>();
        for (int i = 0; i < _currentMemory.Length; i++)
        {
            if (_currentMemory[i] != _snapshotMemory[i])
            {
                differences.Add(i);
            }
        }

        if (differences.Count == 0)
        {
            LogMessage("✅ No differences found - memory is identical");
        }
        else
        {
            LogMessage($"Found {differences.Count} difference(s):");
            foreach (var offset in differences.Take(50))
            {
                uint addr = _currentAddress + (uint)offset;
                LogMessage($"  0x{addr:X8}: {_snapshotMemory[offset]:X2} -> {_currentMemory[offset]:X2}");
            }
            if (differences.Count > 50)
                LogMessage($"  ... and {differences.Count - 50} more");
        }
    }

    private string FormatHexDump(byte[] data, uint baseAddress)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Offset(h)  00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F  Decoded text");
        sb.AppendLine("════════════════════════════════════════════════════════════════════════════");

        for (int i = 0; i < data.Length; i += 16)
        {
            // Format address as 8-digit hex with 0x prefix for clarity
            sb.Append($"0x{baseAddress + i:X8}  ");

            for (int j = 0; j < 16; j++)
            {
                if (i + j < data.Length)
                    sb.Append($"{data[i + j]:X2} ");
                else
                    sb.Append("   ");

                if (j == 7) sb.Append(" ");
            }

            sb.Append(" ");

            for (int j = 0; j < 16 && i + j < data.Length; j++)
            {
                char c = (char)data[i + j];
                sb.Append(c >= 32 && c < 127 ? c : '.');
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses an address string that may contain various formats (0x prefix, hex, decimal)
    /// </summary>
    private uint ParseAddress(string addressText)
    {
        // addresses are hex everywhere in this tool (bare "82000000" used to be read as decimal)
        return MemorySafety.ParseAddress(addressText);
    }

    // ═══════════════════════════════════════════════════════════
    // Offset Calculator Methods
    // ═══════════════════════════════════════════════════════════
    
    private void RefreshOffsetGrid()
    {
        if (dgvOffsets == null || cmbOffsetCategory == null)
        {
            LogMessage("💡 Offset grid not ready yet - will populate when tab is opened");
            return;
        }
        
        // Populate category dropdown
        cmbOffsetCategory.Items.Clear();
        cmbOffsetCategory.Items.Add("-- All Categories --");
        foreach (var category in _offsetManager.Categories.Keys.OrderBy(k => k))
        {
            cmbOffsetCategory.Items.Add(category);
        }
        cmbOffsetCategory.SelectedIndex = 0;
        
        LogMessage($"📊 Populating grid with {_offsetManager.GetAllOffsets().Count} offsets");
        
        // Populate grid
        FilterOffsets();
    }
    
    private void FilterOffsets()
    {
        if (dgvOffsets == null || cmbOffsetCategory == null || txtOffsetSearch == null)
            return;
        
        dgvOffsets.Rows.Clear();
        
        var allOffsets = _offsetManager.GetAllOffsets();
        var filtered = allOffsets.AsEnumerable();
        
        // Filter by category
        if (cmbOffsetCategory.SelectedIndex > 0)
        {
            var category = cmbOffsetCategory.SelectedItem?.ToString();
            if (!string.IsNullOrEmpty(category))
            {
                filtered = filtered.Where(o => o.Category == category);
            }
        }
        
        // Filter by search text
        var searchText = txtOffsetSearch.Text.Trim().ToLower();
        if (!string.IsNullOrEmpty(searchText))
        {
            filtered = filtered.Where(o =>
                o.Name.ToLower().Contains(searchText) ||
                o.Category.ToLower().Contains(searchText));
        }
        
        // Build rows off-grid and insert them in one batch (row-by-row Add re-lays out the grid each time,
        // which froze the UI for runtime-pulled databases with tens of thousands of entries)
        var rows = new List<DataGridViewRow>();
        foreach (var offset in filtered)
        {
            var row = new DataGridViewRow();
            row.CreateCells(dgvOffsets);
            row.Tag = offset;
            
            row.Cells[0].Value = offset.Name;
            row.Cells[1].Value = offset.Category;
            row.Cells[2].Value = offset.GetDisplayAddress();
            row.Cells[3].Value = "Not Resolved";
            row.Cells[4].Value = "";
            row.Cells[5].Value = offset.Type;
            row.Cells[6].Value = offset.ByteValues ?? "";
            
            // Check if resolved
            if (_resolvedPointers.TryGetValue(offset.BaseAddress, out uint pointerValue))
            {
                var finalAddr = offset.CalculateFinalAddress(pointerValue);
                row.Cells[3].Value = $"0x{finalAddr:X8}";
                row.Cells[3].Style.ForeColor = Color.FromArgb(76, 175, 80);
            }

            rows.Add(row);
        }

        var autoSize = dgvOffsets.AutoSizeColumnsMode;
        dgvOffsets.SuspendLayout();
        try
        {
            dgvOffsets.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            dgvOffsets.Rows.AddRange(rows.ToArray());
        }
        finally
        {
            dgvOffsets.AutoSizeColumnsMode = autoSize;
            dgvOffsets.ResumeLayout();
        }
    }
    
    private void BtnResolveOffsets_Click(object? sender, EventArgs e)
    {
        if (txtPointerValue == null || dgvOffsets == null)
            return;
        
        try
        {
            var pointerText = txtPointerValue.Text.Trim();
            if (string.IsNullOrEmpty(pointerText))
            {
                LogMessage("⚠️ Please enter a pointer value");
                return;
            }
            
            // Parse the pointer value
            pointerText = pointerText.Replace("0x", "").Replace(" ", "");
            if (!uint.TryParse(pointerText, System.Globalization.NumberStyles.HexNumber, null, out uint pointerValue))
            {
                LogMessage("❌ Invalid pointer value format");
                return;
            }
            
            LogMessage($"🔍 Resolving offsets with pointer value: 0x{pointerValue:X8}");
            
            // Try to find which base this pointer belongs to
            var knownPointers = _offsetManager.KnownPointers;
            var matchingBase = knownPointers.FirstOrDefault(kp =>
                Math.Abs((long)kp.Value - (long)pointerValue) < 0x10000); // Within 64KB range
            
            if (matchingBase.Key != null)
            {
                LogMessage($"💡 Detected pointer base: {matchingBase.Key}");
                _resolvedPointers[matchingBase.Value] = pointerValue;
            }
            else
            {
                // Add all known pointer bases with this value
                foreach (var knownPointer in knownPointers)
                {
                    _resolvedPointers[knownPointer.Value] = pointerValue;
                }
            }
            
            // Update grid
            int resolvedCount = 0;
            foreach (DataGridViewRow row in dgvOffsets.Rows)
            {
                if (row.Tag is OffsetManager.OffsetEntry offset)
                {
                    if (offset.IsPointerBased && _resolvedPointers.TryGetValue(offset.BaseAddress, out uint baseValue))
                    {
                        var finalAddr = offset.CalculateFinalAddress(baseValue);
                        row.Cells[3].Value = $"0x{finalAddr:X8}";
                        row.Cells[3].Style.ForeColor = Color.FromArgb(76, 175, 80);
                        row.Cells[3].Style.Font = new Font("Consolas", 9F, FontStyle.Bold);
                        resolvedCount++;
                    }
                }
            }
            
            LogMessage($"✅ Resolved {resolvedCount} pointer-based offsets");
            btnApplySelected!.Enabled = dgvOffsets.SelectedRows.Count > 0 && _xbdmClient?.IsConnected == true;
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Error resolving offsets: {ex.Message}");
        }
    }
    
    private async void BtnReadPointer_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !_xbdmClient.IsConnected)
        {
            LogMessage("❌ Not connected to Xbox!");
            return;
        }
        
        try
        {
            // Read PlayerState pointer as default
            var playerStateAddr = _offsetManager.GetOffset("PlayerState");
            if (playerStateAddr == null)
            {
                LogMessage("⚠️ PlayerState address not found in offset database");
                return;
            }
            
            LogMessage($"📖 Reading pointer from 0x{playerStateAddr.BaseAddress:X8}...");
            
            var data = await _xbdmClient.GetMemoryAsync(playerStateAddr.BaseAddress, 4);
            if (data != null && data.Length == 4)
            {
                var pointerValue = BitConverter.ToUInt32(data.Reverse().ToArray(), 0);
                txtPointerValue!.Text = $"0x{pointerValue:X8}";
                LogMessage($"✅ Read pointer value: 0x{pointerValue:X8}");
                
                // Auto-resolve
                BtnResolveOffsets_Click(sender, e);
            }
            else
            {
                LogMessage("❌ Failed to read pointer value");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Error reading pointer: {ex.Message}");
        }
    }
    
    private async void BtnApplySelected_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !_xbdmClient.IsConnected)
        {
            LogMessage("❌ Not connected to Xbox!");
            return;
        }
        
        if (dgvOffsets == null || dgvOffsets.SelectedRows.Count == 0)
        {
            LogMessage("⚠️ No offsets selected");
            return;
        }
        
        try
        {
            LogMessage($"🚀 Applying {dgvOffsets.SelectedRows.Count} offset(s) to Xbox...");
            int successCount = 0;
            int failCount = 0;
            
            foreach (DataGridViewRow row in dgvOffsets.SelectedRows)
            {
                if (row.Tag is OffsetManager.OffsetEntry offset)
                {
                    // Determine final address
                    uint finalAddress;
                    var resolvedText = row.Cells[3].Value?.ToString();
                    
                    if (resolvedText != null && resolvedText.StartsWith("0x"))
                    {
                        finalAddress = Convert.ToUInt32(resolvedText.Replace("0x", ""), 16);
                    }
                    else
                    {
                        finalAddress = offset.BaseAddress;
                    }
                    
                    // Get value to write
                    var valueText = row.Cells[4].Value?.ToString()?.Trim();
                    var bytesText = row.Cells[6].Value?.ToString()?.Trim();
                    
                    bool success = false;
                    
                    // Prefer bytes if available
                    if (!string.IsNullOrEmpty(bytesText))
                    {
                        try
                        {
                            var cleanedBytes = bytesText.Replace(" ", "").Replace(",", "")
                                .Replace("0x", "").Replace("(", "").Replace(")", "");
                            
                            var bytes = new List<byte>();
                            for (int i = 0; i < cleanedBytes.Length; i += 2)
                            {
                                if (i + 1 < cleanedBytes.Length)
                                {
                                    bytes.Add(Convert.ToByte(cleanedBytes.Substring(i, 2), 16));
                                }
                            }
                            
                            if (bytes.Count > 0)
                            {
                                success = await _xbdmClient.SetMemoryAsync(finalAddress, bytes.ToArray());
                                if (success)
                                {
                                    LogMessage($"  ✅ {offset.Name}: Wrote {bytes.Count} bytes to 0x{finalAddress:X8}");
                                    successCount++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"  ❌ {offset.Name}: Byte write error - {ex.Message}");
                            failCount++;
                        }
                    }
                    else if (!string.IsNullOrEmpty(valueText))
                    {
                        // Write value based on type
                        try
                        {
                            var type = row.Cells[5].Value?.ToString() ?? "Int32";
                            
                            switch (type)
                            {
                                case "Int32":
                                    var intValue = Convert.ToInt32(valueText, valueText.StartsWith("0x") ? 16 : 10);
                                    var intBytes = BitConverter.GetBytes(intValue).Reverse().ToArray();
                                    success = await _xbdmClient.SetMemoryAsync(finalAddress, intBytes);
                                    break;
                                    
                                case "Float":
                                    var floatValue = float.Parse(valueText);
                                    var floatBytes = BitConverter.GetBytes(floatValue).Reverse().ToArray();
                                    success = await _xbdmClient.SetMemoryAsync(finalAddress, floatBytes);
                                    break;
                                    
                                case "Byte":
                                    var byteValue = Convert.ToByte(valueText, valueText.StartsWith("0x") ? 16 : 10);
                                    success = await _xbdmClient.SetMemoryAsync(finalAddress, new[] { byteValue });
                                    break;
                            }
                            
                            if (success)
                            {
                                LogMessage($"  ✅ {offset.Name}: Wrote {type} value to 0x{finalAddress:X8}");
                                successCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"  ❌ {offset.Name}: Value write error - {ex.Message}");
                            failCount++;
                        }
                    }
                    else
                    {
                        LogMessage($"  ⚠️ {offset.Name}: No value or bytes to write");
                    }
                    
                    if (!success && !string.IsNullOrEmpty(valueText) || !string.IsNullOrEmpty(bytesText))
                    {
                        failCount++;
                    }
                }
            }
            
            LogMessage($"✅ Applied {successCount} offset(s) successfully, {failCount} failed");
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Error applying offsets: {ex.Message}");
        }
    }
    
    private void BtnExportScript_Click(object? sender, EventArgs e)
    {
        if (dgvOffsets == null)
            return;
        
        try
        {
            var selectedOffsets = new List<OffsetManager.OffsetEntry>();
            
            if (dgvOffsets.SelectedRows.Count > 0)
            {
                foreach (DataGridViewRow row in dgvOffsets.SelectedRows)
                {
                    if (row.Tag is OffsetManager.OffsetEntry offset)
                    {
                        selectedOffsets.Add(offset);
                    }
                }
            }
            else
            {
                // Export all visible
                foreach (DataGridViewRow row in dgvOffsets.Rows)
                {
                    if (row.Tag is OffsetManager.OffsetEntry offset)
                    {
                        selectedOffsets.Add(offset);
                    }
                }
            }
            
            if (selectedOffsets.Count == 0)
            {
                LogMessage("⚠️ No offsets to export");
                return;
            }
            
            var script = _offsetManager.ExportToScript(selectedOffsets, _resolvedPointers);
            
            if (_scriptIde != null)
            {
                _scriptIde.OpenDocument("offsets.xs", script);
                Navigate("script");
                LogMessage($"✅ Exported {selectedOffsets.Count} offset(s) to the Script IDE");
            }
            else
            {
                Clipboard.SetText(script);
                LogMessage($"✅ Exported {selectedOffsets.Count} offset(s) to clipboard");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Error exporting script: {ex.Message}");
        }
    }
    
    private void CopySelectedAddress()
    {
        if (dgvOffsets == null || dgvOffsets.SelectedRows.Count == 0)
            return;
        
        var addresses = new List<string>();
        foreach (DataGridViewRow row in dgvOffsets.SelectedRows)
        {
            var resolved = row.Cells[3].Value?.ToString();
            if (resolved != null && resolved.StartsWith("0x"))
            {
                addresses.Add(resolved);
            }
            else
            {
                addresses.Add(row.Cells[2].Value?.ToString() ?? "");
            }
        }
        
        if (addresses.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, addresses));
            LogMessage($"📋 Copied {addresses.Count} address(es) to clipboard");
        }
    }

    private TabPage CreateAdvancedScanTab()
    {
        var tab = new TabPage("🔬 Advanced Scanner");
        tab.BackColor = Color.FromArgb(250, 250, 250);

        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 400
        };

        // Left Panel - Scan Controls
        var leftPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };

        var lblTitle = new Label
        {
            Text = "🎯 Memory Scanner",
            Location = new Point(10, 10),
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            AutoSize = true
        };

        // Scan Type
        var lblScanType = new Label
        {
            Text = "Scan Type:",
            Location = new Point(10, 50),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F)
        };

        cmbScanType = new ComboBox
        {
            Location = new Point(120, 47),
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cmbScanType.Items.AddRange(new object[]
        {
            "Exact Value",
            "Bigger Than",
            "Smaller Than",
            "Changed Value",
            "Unchanged Value",
            "Increased Value",
            "Decreased Value"
        });
        cmbScanType.SelectedIndex = 0;

        // Value Type
        var lblValueType = new Label
        {
            Text = "Value Type:",
            Location = new Point(10, 85),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F)
        };

        cmbValueType = new ComboBox
        {
            Location = new Point(120, 82),
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cmbValueType.Items.AddRange(new object[] { "Byte", "Int16", "Int32", "Int64", "Float", "Double" });
        cmbValueType.SelectedIndex = 2; // Int32

        // Search Value
        var lblValue = new Label
        {
            Text = "Value:",
            Location = new Point(10, 120),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F)
        };

        txtScanValue = new TextBox
        {
            Location = new Point(120, 117),
            Width = 200,
            Font = new Font("Consolas", 10F)
        };

        // Address Range
        var lblRange = new Label
        {
            Text = "Address Range:",
            Location = new Point(10, 155),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };

        var lblStart = new Label
        {
            Text = "Start:",
            Location = new Point(10, 185),
            AutoSize = true
        };

        txtStartAddress = new TextBox
        {
            Location = new Point(120, 182),
            Width = 200,
            Text = "0x80000000",
            Font = new Font("Consolas", 10F)
        };

        var lblEnd = new Label
        {
            Text = "End:",
            Location = new Point(10, 220),
            AutoSize = true
        };

        txtEndAddress = new TextBox
        {
            Location = new Point(120, 217),
            Width = 200,
            Text = "0x82000000",
            Font = new Font("Consolas", 10F)
        };

        // Scan Buttons
        btnFirstScan = new Button
        {
            Text = "🔍 First Scan",
            Location = new Point(10, 260),
            Size = new Size(150, 40),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold)
        };
        btnFirstScan.FlatAppearance.BorderSize = 0;
        btnFirstScan.Click += BtnFirstScan_Click;

        btnNextScan = new Button
        {
            Text = "➡️ Next Scan",
            Location = new Point(170, 260),
            Size = new Size(150, 40),
            BackColor = Color.FromArgb(16, 110, 190),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Enabled = false
        };
        btnNextScan.FlatAppearance.BorderSize = 0;
        btnNextScan.Click += BtnNextScan_Click;

        btnNewScan = new Button
        {
            Text = "🔄 New Scan",
            Location = new Point(10, 310),
            Size = new Size(150, 35),
            BackColor = Color.FromArgb(70, 70, 70),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnNewScan.FlatAppearance.BorderSize = 0;
        btnNewScan.Click += (s, e) =>
        {
            dgvScanResults?.Rows.Clear();
            if (btnNextScan != null) btnNextScan.Enabled = false;
            if (btnFirstScan != null) btnFirstScan.Enabled = true;
            _memoryEngine.ClearScanResults();
            LogMessage("🆕 Scan results cleared - ready for new scan");
        };
        
        // Add Refresh Values button
        var btnRefreshValues = new Button
        {
            Text = "🔄 Refresh",
            Location = new Point(230, 260),
            Size = new Size(90, 35),
            BackColor = Color.FromArgb(100, 149, 237),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        btnRefreshValues.FlatAppearance.BorderSize = 0;
        btnRefreshValues.Click += async (s, e) =>
        {
            if (_memoryEngine.GetScanResultCount() == 0)
            {
                LogMessage("⚠️ No scan results to refresh");
                return;
            }
            
            try
            {
                btnRefreshValues.Enabled = false;
                btnRefreshValues.Text = "⏳...";
                
                var results = await Task.Run(async () => await _memoryEngine.RefreshScanValues());
                
                if (InvokeRequired)
                {
                    Invoke(() => UpdateNextScanResults(results));
                }
                else
                {
                    UpdateNextScanResults(results);
                }
                
                LogMessage($"✅ Refreshed {results.Count} values");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Refresh error: {ex.Message}");
            }
            finally
            {
                btnRefreshValues.Enabled = true;
                btnRefreshValues.Text = "🔄 Refresh";
            }
        };

        btnPointerScan = new Button
        {
            Text = "🎯 Pointer Scan",
            Location = new Point(170, 310),
            Size = new Size(150, 35),
            BackColor = Color.FromArgb(139, 69, 19),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnPointerScan.FlatAppearance.BorderSize = 0;
        btnPointerScan.Click += BtnPointerScan_Click;

        // Import/Export
        btnImportAddresses = new Button
        {
            Text = "📥 Import",
            Location = new Point(10, 360),
            Size = new Size(100, 30),
            BackColor = Color.FromArgb(34, 139, 34),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnImportAddresses.FlatAppearance.BorderSize = 0;
        btnImportAddresses.Click += BtnImportAddresses_Click;

        btnExportAddresses = new Button
        {
            Text = "💾 Export",
            Location = new Point(120, 360),
            Size = new Size(100, 30),
            BackColor = Color.FromArgb(70, 130, 180),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnExportAddresses.FlatAppearance.BorderSize = 0;
        btnExportAddresses.Click += BtnExportAddresses_Click;

        btnStructWalk = new Button
        {
            Text = "🚶 Struct Walk",
            Location = new Point(10, 400),
            Size = new Size(210, 35),
            BackColor = Color.FromArgb(138, 43, 226),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        btnStructWalk.FlatAppearance.BorderSize = 0;
        btnStructWalk.Click += BtnStructWalk_Click;

        leftPanel.Controls.AddRange(new Control[]
        {
            lblTitle, lblScanType, cmbScanType, lblValueType, cmbValueType,
            lblValue, txtScanValue, lblRange, lblStart, txtStartAddress,
            lblEnd, txtEndAddress, btnFirstScan, btnNextScan, btnNewScan,
            btnRefreshValues, btnPointerScan, btnImportAddresses, btnExportAddresses, btnStructWalk
        });

        mainSplit.Panel1.Controls.Add(leftPanel);

        // Right Panel - Results
        var rightSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 300
        };

        // Scan Results Grid
        var resultsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(5)
        };

        var lblResults = new Label
        {
            Text = "🔍 Scan Results",
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };

        dgvScanResults = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        dgvScanResults.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Address", Name = "Address" },
            new DataGridViewTextBoxColumn { HeaderText = "Value", Name = "Value" },
            new DataGridViewTextBoxColumn { HeaderText = "Previous", Name = "Previous" },
            new DataGridViewTextBoxColumn { HeaderText = "Type", Name = "Type" }
        });
        dgvScanResults.DoubleClick += (s, e) =>
        {
            if (dgvScanResults.SelectedRows.Count > 0)
            {
                var row = dgvScanResults.SelectedRows[0];
                var address = row.Cells["Address"].Value?.ToString();
                var type = row.Cells["Type"].Value?.ToString();
                if (address != null && type != null)
                {
                    AddAddressToWatchList(address, type);
                }
            }
        };

        btnAddToWatchList = new Button
        {
            Text = "➕ Add to Watch List",
            Dock = DockStyle.Bottom,
            Height = 35,
            BackColor = Color.FromArgb(0, 150, 136),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };
        btnAddToWatchList.FlatAppearance.BorderSize = 0;
        btnAddToWatchList.Click += (s, e) =>
        {
            foreach (DataGridViewRow row in dgvScanResults!.SelectedRows)
            {
                var address = row.Cells["Address"].Value?.ToString();
                var type = row.Cells["Type"].Value?.ToString();
                if (address != null && type != null)
                {
                    AddAddressToWatchList(address, type);
                }
            }
        };

        resultsPanel.Controls.Add(dgvScanResults);
        resultsPanel.Controls.Add(lblResults);
        resultsPanel.Controls.Add(btnAddToWatchList);
        rightSplit.Panel1.Controls.Add(resultsPanel);

        // Watch List Grid
        var watchPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(5)
        };

        var lblWatch = new Label
        {
            Text = "👁️ Watch List (Live Updates)",
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };

        dgvWatchList = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        dgvWatchList.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewCheckBoxColumn { HeaderText = "Frozen", Name = "Frozen", Width = 60 },
            new DataGridViewTextBoxColumn { HeaderText = "Address", Name = "Address" },
            new DataGridViewTextBoxColumn { HeaderText = "Description", Name = "Description" },
            new DataGridViewTextBoxColumn { HeaderText = "Type", Name = "Type", Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "Value", Name = "Value" }
        });
        dgvWatchList.CellValueChanged += DgvWatchList_CellValueChanged;
        dgvWatchList.CellDoubleClick += DgvWatchList_CellDoubleClick;

        var watchButtonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(5)
        };

        var btnRemoveWatch = new Button
        {
            Text = "🗑️ Remove",
            Location = new Point(5, 5),
            Size = new Size(100, 30),
            BackColor = Color.FromArgb(178, 34, 34),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnRemoveWatch.FlatAppearance.BorderSize = 0;
        btnRemoveWatch.Click += (s, e) =>
        {
            foreach (DataGridViewRow row in dgvWatchList.SelectedRows)
            {
                dgvWatchList.Rows.Remove(row);
            }
        };

        var btnDissect = new Button
        {
            Text = "🔬 Dissect Structure",
            Location = new Point(115, 5),
            Size = new Size(150, 30),
            BackColor = Color.FromArgb(106, 90, 205),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnDissect.FlatAppearance.BorderSize = 0;
        btnDissect.Click += BtnDissect_Click;

        watchButtonPanel.Controls.AddRange(new Control[] { btnRemoveWatch, btnDissect });

        watchPanel.Controls.Add(dgvWatchList);
        watchPanel.Controls.Add(lblWatch);
        watchPanel.Controls.Add(watchButtonPanel);
        rightSplit.Panel2.Controls.Add(watchPanel);

        mainSplit.Panel2.Controls.Add(rightSplit);
        tab.Controls.Add(mainSplit);

        // Setup watch list timer
        _watchListTimer = new System.Windows.Forms.Timer
        {
            Interval = 500 // Update every 500ms
        };
        _watchListTimer.Tick += async (s, e) => await UpdateWatchList();

        // Setup memory engine events
        _memoryEngine.LogMessage += (s, msg) => LogMessage(msg);
        _memoryEngine.AddressChanged += (s, watched) =>
        {
            BeginInvoke(() =>
            {
                foreach (DataGridViewRow row in dgvWatchList!.Rows)
                {
                    if (row.Cells["Address"].Value?.ToString() == $"0x{watched.Address:X8}")
                    {
                        row.Cells["Value"].Value = _memoryEngine.FormatValue(watched.CurrentValue, watched.Type);
                        row.Cells["Value"].Style.BackColor = Color.FromArgb(255, 255, 200); // Highlight change
                        break;
                    }
                }
            });
        };

        return tab;
    }

    private async void BtnFirstScan_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !_xbdmClient.IsConnected)
        {
            LogMessage("❌ Not connected to Xbox");
            return;
        }

        if (string.IsNullOrEmpty(txtScanValue?.Text) && cmbScanType?.SelectedIndex == 0)
        {
            LogMessage("❌ Please enter a value to search");
            return;
        }

        try
        {
            // Disable all scan buttons during scan
            btnFirstScan!.Enabled = false;
            btnFirstScan.Text = "⏳ Scanning...";
            if (btnNextScan != null) btnNextScan.Enabled = false;
            if (btnNewScan != null) btnNewScan.Enabled = false;

            _memoryEngine.SetClient(_xbdmClient);

            var startAddr = Convert.ToUInt32(txtStartAddress!.Text.Replace("0x", ""), 16);
            var endAddr = Convert.ToUInt32(txtEndAddress!.Text.Replace("0x", ""), 16);
            var valueType = (AdvancedMemoryEngine.ValueType)Enum.Parse(typeof(AdvancedMemoryEngine.ValueType), cmbValueType!.SelectedItem!.ToString()!);
            var scanType = SelectedScanType();

            object? searchValue = null;
            if (scanType is AdvancedMemoryEngine.ScanType.ExactValue or AdvancedMemoryEngine.ScanType.BiggerThan or AdvancedMemoryEngine.ScanType.SmallerThan)
            {
                if (!TryParseScanValue(valueType, txtScanValue?.Text ?? "", out searchValue)) return;
            }

            var results = await Task.Run(async () => 
                await _memoryEngine.FirstScan(startAddr, endAddr, valueType, searchValue!, scanType)
            );

            // Update UI on UI thread
            if (InvokeRequired)
            {
                Invoke(() => UpdateScanResults(results));
            }
            else
            {
                UpdateScanResults(results);
            }

            LogMessage($"✅ First scan complete: {results.Count} results");
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Scan error: {ex.Message}");
            if (btnNextScan != null) btnNextScan.Enabled = false;
        }
        finally
        {
            // Always re-enable scan buttons
            btnFirstScan!.Enabled = true;
            btnFirstScan.Text = "🔍 First Scan";
            if (btnNewScan != null) btnNewScan.Enabled = true;
        }
    }

    private void UpdateScanResults(List<AdvancedMemoryEngine.ScanResult> results)
    {
        dgvScanResults!.SuspendLayout();
        try
        {
            dgvScanResults.Rows.Clear();
            
            int displayCount = Math.Min(results.Count, 10000);
            foreach (var result in results.Take(displayCount))
            {
                var currentVal = _memoryEngine.FormatValue(result.Value, result.Type);
                var prevVal = result.PreviousValue != null && result.PreviousValue.Length > 0 
                    ? _memoryEngine.FormatValue(result.PreviousValue, result.Type) 
                    : "N/A";
                
                int rowIndex = dgvScanResults.Rows.Add(
                    $"0x{result.Address:X8}",
                    currentVal,
                    prevVal,
                    result.Type.ToString()
                );
                
                // Color code rows if value changed from previous
                if (result.PreviousValue != null && result.PreviousValue.Length > 0)
                {
                    if (!result.Value.SequenceEqual(result.PreviousValue))
                    {
                        dgvScanResults.Rows[rowIndex].DefaultCellStyle.BackColor = Color.FromArgb(255, 255, 200); // Light yellow
                    }
                }
            }
            
            if (results.Count > displayCount)
            {
                LogMessage($"⚠️ Showing {displayCount} of {results.Count} results (display limit)");
            }

            // Enable Next Scan only if we have results
            if (btnNextScan != null)
            {
                btnNextScan.Enabled = results.Count > 0;
            }
        }
        finally
        {
            dgvScanResults.ResumeLayout();
        }
    }

    private async void BtnNextScan_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !_xbdmClient.IsConnected)
        {
            LogMessage("❌ Not connected to Xbox");
            return;
        }

        try
        {
            // Disable buttons during scan
            btnNextScan!.Enabled = false;
            btnNextScan.Text = "⏳ Filtering...";
            if (btnFirstScan != null) btnFirstScan.Enabled = false;
            if (btnNewScan != null) btnNewScan.Enabled = false;

            var valueType = (AdvancedMemoryEngine.ValueType)Enum.Parse(typeof(AdvancedMemoryEngine.ValueType), cmbValueType!.SelectedItem!.ToString()!);
            var scanType = SelectedScanType();

            object? searchValue = null;
            if (scanType is AdvancedMemoryEngine.ScanType.ExactValue or AdvancedMemoryEngine.ScanType.BiggerThan or AdvancedMemoryEngine.ScanType.SmallerThan)
            {
                if (!TryParseScanValue(valueType, txtScanValue?.Text ?? "", out searchValue)) return;
            }

            var results = await Task.Run(async () => 
                await _memoryEngine.NextScan(valueType, searchValue!, scanType)
            );

            // Update UI on UI thread
            if (InvokeRequired)
            {
                Invoke(() => UpdateNextScanResults(results));
            }
            else
            {
                UpdateNextScanResults(results);
            }

            LogMessage($"✅ Filtered to {results.Count} results");
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Next scan error: {ex.Message}");
        }
        finally
        {
            // Always re-enable buttons
            btnNextScan!.Enabled = true;
            btnNextScan.Text = "➡️ Next Scan";
            if (btnFirstScan != null) btnFirstScan.Enabled = true;
            if (btnNewScan != null) btnNewScan.Enabled = true;
        }
    }

    private void UpdateNextScanResults(List<AdvancedMemoryEngine.ScanResult> results)
    {
        dgvScanResults!.SuspendLayout();
        try
        {
            dgvScanResults.Rows.Clear();
            
            int displayCount = Math.Min(results.Count, 10000);
            foreach (var result in results.Take(displayCount))
            {
                var currentVal = _memoryEngine.FormatValue(result.Value, result.Type);
                var prevVal = result.PreviousValue != null && result.PreviousValue.Length > 0
                    ? _memoryEngine.FormatValue(result.PreviousValue, result.Type)
                    : "N/A";
                
                int rowIndex = dgvScanResults.Rows.Add(
                    $"0x{result.Address:X8}",
                    currentVal,
                    prevVal,
                    result.Type.ToString()
                );
                
                // Highlight changed values
                if (result.PreviousValue != null && result.PreviousValue.Length > 0)
                {
                    if (!result.Value.SequenceEqual(result.PreviousValue))
                    {
                        dgvScanResults.Rows[rowIndex].Cells["Value"].Style.BackColor = Color.FromArgb(144, 238, 144); // Light green
                        dgvScanResults.Rows[rowIndex].Cells["Value"].Style.ForeColor = Color.DarkGreen;
                        dgvScanResults.Rows[rowIndex].Cells["Value"].Style.Font = new Font(dgvScanResults.Font, FontStyle.Bold);
                    }
                    else
                    {
                        dgvScanResults.Rows[rowIndex].Cells["Value"].Style.ForeColor = Color.Gray;
                    }
                }
            }
            
            if (results.Count > displayCount)
            {
                LogMessage($"⚠️ Showing {displayCount} of {results.Count} results (display limit)");
            }
        }
        finally
        {
            dgvScanResults.ResumeLayout();
        }
    }

    private async void BtnPointerScan_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !_xbdmClient.IsConnected) return;

        if (dgvScanResults?.SelectedRows.Count == 0)
        {
            LogMessage("❌ Select an address to scan for pointers");
            return;
        }

        try
        {
            var selectedAddr = dgvScanResults.SelectedRows[0].Cells["Address"].Value?.ToString();
            if (selectedAddr == null) return;

            var targetAddr = Convert.ToUInt32(selectedAddr.Replace("0x", ""), 16);

            btnPointerScan!.Enabled = false;
            btnPointerScan.Text = "⏳ Scanning...";

            _memoryEngine.SetClient(_xbdmClient);
            var paths = await _memoryEngine.PointerScan(targetAddr);

            LogMessage($"✅ Found {paths.Count} pointer paths:");
            foreach (var path in paths.Take(20))
            {
                LogMessage($"  {path}");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Pointer scan error: {ex.Message}");
        }
        finally
        {
            btnPointerScan!.Enabled = true;
            btnPointerScan.Text = "🎯 Pointer Scan";
        }
    }

    private async void BtnImportAddresses_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Address List (*.txt)|*.txt|CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            Title = "Import Address List"
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            try
            {
                var addresses = await _memoryEngine.ImportAddresses(dialog.FileName);
                foreach (var addr in addresses)
                {
                    dgvWatchList!.Rows.Add(false, $"0x{addr.Address:X8}", addr.Description, addr.Type.ToString(), "");
                }
                _watchListTimer!.Start();
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Import error: {ex.Message}");
            }
        }
    }

    private async void BtnExportAddresses_Click(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Address List (*.txt)|*.txt|CSV Files (*.csv)|*.csv",
            Title = "Export Address List",
            FileName = "addresses.txt"
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            try
            {
                await _memoryEngine.ExportAddresses(dialog.FileName);
                LogMessage($"✅ Exported to {dialog.FileName}");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Export error: {ex.Message}");
            }
        }
    }

    private async void BtnDissect_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !_xbdmClient.IsConnected) return;

        if (dgvWatchList?.SelectedRows.Count == 0)
        {
            LogMessage("❌ Select an address to dissect");
            return;
        }

        try
        {
            var selectedAddr = dgvWatchList.SelectedRows[0].Cells["Address"].Value?.ToString();
            if (selectedAddr == null) return;

            var baseAddr = Convert.ToUInt32(selectedAddr.Replace("0x", ""), 16);

            _memoryEngine.SetClient(_xbdmClient);
            var members = await _memoryEngine.DissectStructure(baseAddr, 256);

            LogMessage($"🔬 Structure analysis at 0x{baseAddr:X8}:");
            foreach (var member in members)
            {
                LogMessage($"  +0x{member.Offset:X} ({member.Type}): {member.Value}");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Dissect error: {ex.Message}");
        }
    }

    private void AddAddressToWatchList(string address, string type)
    {
        var addr = Convert.ToUInt32(address.Replace("0x", ""), 16);
        var valueType = Enum.Parse<AdvancedMemoryEngine.ValueType>(type);
        
        _memoryEngine.AddToWatchList(addr, valueType, $"Watch_{addr:X8}");
        dgvWatchList!.Rows.Add(false, address, $"Watch_{addr:X8}", type, "");
        
        if (!_watchListTimer!.Enabled)
            _watchListTimer.Start();
    }

    private async Task UpdateWatchList()
    {
        if (_xbdmClient == null || !_xbdmClient.IsConnected)
        {
            _watchListTimer?.Stop();
            return;
        }

        await _memoryEngine.UpdateWatchList();
    }

    private async void DgvWatchList_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.ColumnIndex == dgvWatchList!.Columns["Frozen"]!.Index && e.RowIndex >= 0)
        {
            var row = dgvWatchList.Rows[e.RowIndex];
            var frozen = Convert.ToBoolean(row.Cells["Frozen"].Value);
            var address = row.Cells["Address"].Value?.ToString();
            
            // TODO: Implement freeze functionality through AdvancedMemoryEngine
        }
    }

    private async void DgvWatchList_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != dgvWatchList!.Columns["Value"]!.Index) return;

        var row = dgvWatchList.Rows[e.RowIndex];
        var address = row.Cells["Address"].Value?.ToString();
        var type = row.Cells["Type"].Value?.ToString();
        var currentValue = row.Cells["Value"].Value?.ToString() ?? "";

        using var dialog = new Form
        {
            Text = "Edit Value",
            Size = new Size(300, 150),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var txtValue = new TextBox
        {
            Location = new Point(20, 30),
            Size = new Size(240, 25),
            Text = currentValue
        };

        var btnOk = new Button
        {
            Text = "OK",
            Location = new Point(80, 70),
            DialogResult = DialogResult.OK
        };

        var btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(170, 70),
            DialogResult = DialogResult.Cancel
        };

        dialog.Controls.AddRange(new Control[] { txtValue, btnOk, btnCancel });
        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;

        if (dialog.ShowDialog() == DialogResult.OK && _xbdmClient != null)
        {
            try
            {
                var addr = Convert.ToUInt32(address!.Replace("0x", ""), 16);
                var valueType = Enum.Parse<AdvancedMemoryEngine.ValueType>(type!);
                
                byte[] bytes = valueType switch
                {
                    AdvancedMemoryEngine.ValueType.Byte => new[] { byte.Parse(txtValue.Text) },
                    AdvancedMemoryEngine.ValueType.Int16 => BeBytes(short.Parse(txtValue.Text)),
                    AdvancedMemoryEngine.ValueType.Int32 => BeBytes(int.Parse(txtValue.Text)),
                    AdvancedMemoryEngine.ValueType.Float => BeBytes(float.Parse(txtValue.Text)),
                    _ => BeBytes(int.Parse(txtValue.Text))
                };

                await _xbdmClient.SetMemoryAsync(addr, bytes);
                LogMessage($"✅ Updated 0x{addr:X8} = {txtValue.Text}");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Write error: {ex.Message}");
            }
        }
    }

    private async void BtnStructWalk_Click(object? sender, EventArgs e)
    {
            if (dgvScanResults == null || dgvScanResults.SelectedRows.Count == 0)
            {
                LogMessage("⚠️ Select an address to walk structure");
                return;
            }

            try
            {
                var addr = Convert.ToUInt32(dgvScanResults.SelectedRows[0].Cells[0].Value.ToString(), 16);
                LogMessage($"🚶 Walking structure at 0x{addr:X8}...");

                var result = await _memoryEngine.WalkStructure(addr, 512, 2);

                // Display in new window
                var walkForm = new Form
                {
                    Text = $"Structure Walk - 0x{addr:X8}",
                    Size = new Size(800, 600),
                    StartPosition = FormStartPosition.CenterParent
                };

                var dgv = new DataGridView
                {
                    Dock = DockStyle.Fill,
                    BackgroundColor = Color.White,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                    SelectionMode = DataGridViewSelectionMode.FullRowSelect
                };

                dgv.Columns.AddRange(new[]
                {
                    new DataGridViewTextBoxColumn { HeaderText = "Offset", Name = "Offset" },
                    new DataGridViewTextBoxColumn { HeaderText = "Address", Name = "Address" },
                    new DataGridViewTextBoxColumn { HeaderText = "Type", Name = "Type" },
                    new DataGridViewTextBoxColumn { HeaderText = "Name", Name = "Name" },
                    new DataGridViewTextBoxColumn { HeaderText = "Value", Name = "Value" }
                });

                foreach (var member in result.Members)
                {
                    dgv.Rows.Add(
                        $"0x{member.Offset:X}",
                        $"0x{member.Address:X8}",
                        member.Type,
                        member.Name,
                        member.Value
                    );
                }

                walkForm.Controls.Add(dgv);
                walkForm.Show();

                LogMessage($"✅ Found {result.Members.Count} structure members");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Struct walk error: {ex.Message}");
            }
        }

        // ==================== RECORDING TAB ====================
        private TabPage CreateRecordingTab()
        {
            var tab = new TabPage("📹 Recording");

            var mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };

            // Control Panel - ENHANCED with more options
            var controlPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 200,
                BackColor = Color.FromArgb(240, 240, 245),
                Padding = new Padding(10)
            };

            var lblTitle = new Label
            {
                Text = "📹 Professional Memory Recording",
                Location = new Point(10, 10),
                AutoSize = true,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = Color.FromArgb(32, 33, 36)
            };

            // ===== ROW 1: Address Range =====
            var lblStartAddr = new Label
            {
                Text = "Start Address:",
                Location = new Point(10, 45),
                AutoSize = true,
                Font = new Font("Segoe UI", 9F)
            };

            txtRecordStartAddr = new TextBox
            {
                Location = new Point(110, 42),
                Width = 120,
                Text = "0x80000000",
                Font = new Font("Consolas", 10F),
                BackColor = Color.White
            };
            var tooltipRecorder = new ToolTip();
            tooltipRecorder.SetToolTip(txtRecordStartAddr, "Start address for memory region monitoring");
            
            var lblEndAddr = new Label
            {
                Text = "End Address:",
                Location = new Point(240, 45),
                AutoSize = true,
                Font = new Font("Segoe UI", 9F)
            };
            
            txtRecordEndAddr = new TextBox
            {
                Name = "txtRecordEndAddr",
                Location = new Point(330, 42),
                Width = 120,
                Text = "0x80010000",
                Font = new Font("Consolas", 10F),
                BackColor = Color.White
            };
            tooltipRecorder.SetToolTip(txtRecordEndAddr, "End address for memory region (custom range)");

            var lblSize = new Label
            {
                Text = "or Size:",
                Location = new Point(460, 45),
                AutoSize = true,
                Font = new Font("Segoe UI", 9F)
            };

            txtRecordSize = new TextBox
            {
                Location = new Point(515, 42),
                Width = 85,
                Text = "65536",
                Font = new Font("Consolas", 10F),
                BackColor = Color.White
            };
            tooltipRecorder.SetToolTip(txtRecordSize, "Size in bytes (auto-calculates if end address provided)");
            
            // ===== ROW 2: Memory Type and Endianness =====
            var lblMemType = new Label
            {
                Text = "Memory Type:",
                Location = new Point(10, 80),
                AutoSize = true,
                Font = new Font("Segoe UI", 9F)
            };
            
            var cmbMemoryType = new ComboBox
            {
                Name = "cmbMemoryType",
                Location = new Point(110, 77),
                Width = 120,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9F)
            };
            cmbMemoryType.Items.AddRange(new[] { "All Changes", "Byte (8-bit)", "Int16 (16-bit)", "Int32 (32-bit)", "Int64 (64-bit)", "Float (32-bit)", "Double (64-bit)" });
            cmbMemoryType.SelectedIndex = 0;
            tooltipRecorder.SetToolTip(cmbMemoryType, "Filter by data type for focused monitoring");
            
            var lblEndianness = new Label
            {
                Text = "Endianness:",
                Location = new Point(240, 80),
                AutoSize = true,
                Font = new Font("Segoe UI", 9F)
            };
            
            var cmbEndianness = new ComboBox
            {
                Name = "cmbEndianness",
                Location = new Point(330, 77),
                Width = 120,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9F)
            };
            cmbEndianness.Items.AddRange(new[] { "Big Endian (Xbox 360)", "Little Endian (PC)" });
            cmbEndianness.SelectedIndex = 0;
            tooltipRecorder.SetToolTip(cmbEndianness, "Xbox 360 uses Big Endian byte order");
            
            var lblThreshold = new Label
            {
                Text = "Change Threshold:",
                Location = new Point(460, 80),
                AutoSize = true,
                Font = new Font("Segoe UI", 9F)
            };
            
            var numThreshold = new NumericUpDown
            {
                Name = "numThreshold",
                Location = new Point(575, 77),
                Width = 80,
                Minimum = 1,
                Maximum = 1000,
                Value = 1,
                Font = new Font("Consolas", 9F)
            };
            tooltipRecorder.SetToolTip(numThreshold, "Minimum value change to record (filter noise)");
            
            // ===== ROW 3: Advanced Options =====
            chkMonitorBaseAddr = new CheckBox
            {
                Text = "📍 Monitor custom region:",
                Location = new Point(10, 115),
                AutoSize = true,
                Font = new Font("Segoe UI", 9F),
                Checked = false,
                ForeColor = Color.FromArgb(0, 102, 204)
            };
            tooltipRecorder.SetToolTip(chkMonitorBaseAddr, "Enable custom region monitoring (specify range below)");
            
            var txtCustomRegionStart = new TextBox
            {
                Name = "txtCustomRegionStart",
                Location = new Point(180, 113),
                Width = 100,
                Text = "0x82000000",
                Font = new Font("Consolas", 9F),
                Enabled = false
            };
            
            var lblRegionTo = new Label
            {
                Text = "to",
                Location = new Point(285, 116),
                AutoSize = true,
                Font = new Font("Segoe UI", 9F)
            };
            
            var txtCustomRegionEnd = new TextBox
            {
                Name = "txtCustomRegionEnd",
                Location = new Point(305, 113),
                Width = 100,
                Text = "0x84000000",
                Font = new Font("Consolas", 9F),
                Enabled = false
            };
            
            // Enable/disable custom region textboxes based on checkbox
            chkMonitorBaseAddr.CheckedChanged += (s, e) =>
            {
                txtCustomRegionStart.Enabled = chkMonitorBaseAddr.Checked;
                txtCustomRegionEnd.Enabled = chkMonitorBaseAddr.Checked;
            };
            
            var chkShowUnchanged = new CheckBox
            {
                Text = "Show unchanged values",
                Location = new Point(420, 115),
                AutoSize = true,
                Font = new Font("Segoe UI", 9F)
            };
            tooltipRecorder.SetToolTip(chkShowUnchanged, "Include values that haven't changed (verbose mode)");

            // ===== ROW 4: Action Buttons =====
            btnStartRecording = new Button
            {
                Text = "⏺️ Start Recording",
                Location = new Point(10, 150),
                Size = new Size(145, 38),
                BackColor = Color.FromArgb(220, 53, 69),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnStartRecording.FlatAppearance.BorderSize = 0;
            btnStartRecording.Click += BtnStartRecording_Click;

            btnStopRecording = new Button
            {
                Text = "⏹️ Stop Recording",
                Location = new Point(165, 150),
                Size = new Size(145, 38),
                BackColor = Color.FromArgb(108, 117, 125),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Enabled = false,
                Cursor = Cursors.Hand
            };
            btnStopRecording.FlatAppearance.BorderSize = 0;
            btnStopRecording.Click += BtnStopRecording_Click;

            btnExportRecording = new Button
            {
                Text = "💾 Export Results",
                Location = new Point(320, 150),
                Size = new Size(140, 38),
                BackColor = Color.FromArgb(0, 123, 255),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Enabled = false,
                Cursor = Cursors.Hand
            };
            btnExportRecording.FlatAppearance.BorderSize = 0;
            btnExportRecording.Click += BtnExportRecording_Click;
            
            var btnClearRecording = new Button
            {
                Text = "🗑️ Clear",
                Location = new Point(470, 150),
                Size = new Size(90, 38),
                BackColor = Color.FromArgb(158, 158, 158),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnClearRecording.FlatAppearance.BorderSize = 0;
            btnClearRecording.Click += (s, e) =>
            {
                if (dgvRecordingChanges != null)
                {
                    dgvRecordingChanges.Rows.Clear();
                    _addressChangeFrequency.Clear();
                    _recentChanges.Clear();
                    if (lstRecentChanges != null) lstRecentChanges.Items.Clear();
                    LogMessage("🗑️ Recording data cleared");
                }
            };

            lblRecordingStatus = new Label
            {
                Text = "⚪ Not Recording",
                Location = new Point(575, 157),
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                ForeColor = Color.Gray
            };

            controlPanel.Controls.AddRange(new Control[]
            {
                lblTitle, 
                lblStartAddr, txtRecordStartAddr, lblEndAddr, txtRecordEndAddr, lblSize, txtRecordSize,
                lblMemType, cmbMemoryType, lblEndianness, cmbEndianness, lblThreshold, numThreshold,
                chkMonitorBaseAddr, txtCustomRegionStart, lblRegionTo, txtCustomRegionEnd, chkShowUnchanged,
                btnStartRecording, btnStopRecording, btnExportRecording, btnClearRecording, lblRecordingStatus
            });

            // Changes Grid
            dgvRecordingChanges = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AllowUserToAddRows = false,
                ReadOnly = true
            };

            dgvRecordingChanges.Columns.AddRange(new[]
            {
                new DataGridViewTextBoxColumn { HeaderText = "Time (ms)", Name = "Time", Width = 80 },
                new DataGridViewTextBoxColumn { HeaderText = "Address", Name = "Address", Width = 110 },
                new DataGridViewTextBoxColumn { HeaderText = "Old Value", Name = "OldValue", Width = 90 },
                new DataGridViewTextBoxColumn { HeaderText = "New Value", Name = "NewValue", Width = 90 },
                new DataGridViewTextBoxColumn { HeaderText = "Delta", Name = "Delta", Width = 70 },
                new DataGridViewTextBoxColumn { HeaderText = "Freq", Name = "Frequency", Width = 50 },
                new DataGridViewTextBoxColumn { HeaderText = "Description", Name = "Description", Width = 120 }
            });
            
            // Enable sorting and filtering
            dgvRecordingChanges.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            dgvRecordingChanges.ColumnHeadersHeight = 30;
            dgvRecordingChanges.EnableHeadersVisualStyles = false;
            dgvRecordingChanges.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(45, 45, 48);
            dgvRecordingChanges.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            dgvRecordingChanges.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            dgvRecordingChanges.RowTemplate.Height = 25;
            dgvRecordingChanges.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 250, 250);

            // Hot Addresses Panel (most frequently changing)
            var hotAddressesPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 250,
                BackColor = Color.FromArgb(250, 250, 250),
                Padding = new Padding(5)
            };
            
            var lblHotAddresses = new Label
            {
                Text = "🔥 Hot Addresses",
                Dock = DockStyle.Top,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 53, 69),
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft
            };
            
            var lstHotAddresses = new ListBox
            {
                Name = "lstHotAddresses",
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9F),
                SelectionMode = SelectionMode.One
            };
            lstHotAddresses.DoubleClick += (s, e) =>
            {
                if (lstHotAddresses.SelectedItem != null)
                {
                    var text = lstHotAddresses.SelectedItem.ToString() ?? "";
                    var match = System.Text.RegularExpressions.Regex.Match(text, @"0x[0-9A-F]+");
                    if (match.Success && txtRecordStartAddr != null)
                    {
                        txtRecordStartAddr.Text = match.Value;
                    }
                }
            };
            
            // Recent Activity Panel (real-time changes)
            var recentPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 200,
                BackColor = Color.FromArgb(245, 245, 245),
                Padding = new Padding(5)
            };
            
            lblRecentActivity = new Label
            {
                Text = "⚡ Live Actions (Last 5 seconds)",
                Dock = DockStyle.Top,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 140, 0),
                Height = 28,
                TextAlign = ContentAlignment.MiddleLeft
            };
            
            lstRecentChanges = new ListBox
            {
                Name = "lstRecentChanges",
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.FromArgb(0, 255, 0),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9F, FontStyle.Bold),
                SelectionMode = SelectionMode.None
            };
            
            recentPanel.Controls.Add(lstRecentChanges);
            recentPanel.Controls.Add(lblRecentActivity);
            
            hotAddressesPanel.Controls.Add(lstHotAddresses);
            hotAddressesPanel.Controls.Add(recentPanel);
            hotAddressesPanel.Controls.Add(lblHotAddresses);
            
            mainPanel.Controls.Add(dgvRecordingChanges);
            mainPanel.Controls.Add(hotAddressesPanel);
            mainPanel.Controls.Add(controlPanel);
            tab.Controls.Add(mainPanel);

            // Setup timer for recording display updates (50ms for real-time)
            _recordingUpdateTimer = new System.Windows.Forms.Timer
            {
                Interval = 50 // Ultra-fast for immediate feedback
            };
            _recordingUpdateTimer.Tick += RecordingUpdateTimer_Tick;

            return tab;
        }

        private async void BtnStartRecording_Click(object? sender, EventArgs e)
        {
            if (_xbdmClient == null || txtRecordStartAddr == null)
            {
                LogMessage("⚠️ Not connected or controls not initialized");
                return;
            }

            try
            {
                // Find controls
                var tab = (Control)this; // search the whole window (pages are hosted in the shell, not a TabControl)
                var txtEndAddr = tab?.Controls.Find("txtRecordEndAddr", true).FirstOrDefault() as TextBox;
                var cmbMemoryType = tab?.Controls.Find("cmbMemoryType", true).FirstOrDefault() as ComboBox;
                var cmbEndianness = tab?.Controls.Find("cmbEndianness", true).FirstOrDefault() as ComboBox;
                var numThreshold = tab?.Controls.Find("numThreshold", true).FirstOrDefault() as NumericUpDown;
                var txtCustomStart = tab?.Controls.Find("txtCustomRegionStart", true).FirstOrDefault() as TextBox;
                var txtCustomEnd = tab?.Controls.Find("txtCustomRegionEnd", true).FirstOrDefault() as TextBox;
                
                // Parse addresses
                var startAddrStr = txtRecordStartAddr.Text.Replace("0x", "");
                var startAddr = MemorySafety.ParseAddress(txtRecordStartAddr.Text, "Start address");
                
                uint endAddr;
                uint size;
                
                // Determine if single address or range
                if (txtEndAddr != null && !string.IsNullOrWhiteSpace(txtEndAddr.Text) && txtEndAddr.Text != "0x80010000")
                {
                    // Use end address
                    var endAddrStr = txtEndAddr.Text.Replace("0x", "");
                    endAddr = MemorySafety.ParseAddress(txtEndAddr.Text, "End address");
                    if (endAddr <= startAddr) throw new FormatException("End address must be greater than the start address");
                    size = endAddr - startAddr;
                }
                else if (txtRecordSize != null && !string.IsNullOrWhiteSpace(txtRecordSize.Text))
                {
                    // Use size
                    size = MemorySafety.TryParseAddress(txtRecordSize.Text, out var sz, hexDefault: false) && sz > 0 ? sz : throw new FormatException($"Size: “{txtRecordSize.Text}” is not a number");
                    endAddr = startAddr + size;
                }
                else
                {
                    // Default to single address (4 bytes)
                    size = 4;
                    endAddr = startAddr + 4;
                }
                
                // Store selected memory type and endianness
                var memTypeStr = cmbMemoryType?.SelectedItem?.ToString() ?? "All Changes";
                var isLittleEndian = cmbEndianness?.SelectedItem?.ToString()?.Contains("Little") ?? false;
                var threshold = (int)(numThreshold?.Value ?? 1);
                
                var monitorBase = chkMonitorBaseAddr?.Checked ?? false;
                
                if (size == 4)
                {
                    LogMessage($"📹 Recording single address: 0x{startAddr:X8} ({memTypeStr})");
                }
                else
                {
                    LogMessage($"📹 Starting memory recording: 0x{startAddr:X8} - 0x{endAddr:X8} ({size} bytes, {memTypeStr})");
                }
                
                if (!MemorySafety.Confirm(this, startAddr, size, "Recording (polling)")) { LogMessage("⏹️ Recording cancelled – range is outside game memory"); return; }
                if (size > 16u << 20) throw new FormatException($"The recording range is {size / 1048576.0:0.#} MB – keep it under 16 MB (use the Dynamic Modder for whole-memory scans)");

                if (monitorBase && txtCustomStart != null && txtCustomEnd != null)
                {
                    uint cs0 = MemorySafety.ParseAddress(txtCustomStart.Text, "Custom region start"), ce0 = MemorySafety.ParseAddress(txtCustomEnd.Text, "Custom region end");
                    if (ce0 <= cs0) throw new FormatException("Custom region end must be greater than its start");
                    if (!MemorySafety.Confirm(this, cs0, ce0 - cs0, "Monitoring the custom region")) { LogMessage("⏹️ Recording cancelled – custom region is outside game memory"); return; }
                    if (ce0 - cs0 > 4u << 20) LogMessage($"⚠️ Custom region is {(ce0 - cs0) / 1048576.0:0.#} MB – it is scanned at a reduced rate to keep the console responsive");
                    var customStartStr = txtCustomStart.Text.Replace("0x", "");
                    var customEndStr = txtCustomEnd.Text.Replace("0x", "");
                    var customStart = MemorySafety.ParseAddress(txtCustomStart.Text, "Custom region start");
                    var customEnd = MemorySafety.ParseAddress(txtCustomEnd.Text, "Custom region end");
                    LogMessage($"📍 Also monitoring custom region: 0x{customStart:X8} - 0x{customEnd:X8}");
                }

                // Start recording in background to avoid UI freeze
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _memoryEngine.StartRecording(startAddr, size);
                        
                        // Start custom region monitoring if enabled
                        if (monitorBase && txtCustomStart != null && txtCustomEnd != null)
                        {
                            var customStartStr = txtCustomStart.Text.Replace("0x", "");
                            var customEndStr = txtCustomEnd.Text.Replace("0x", "");
                            var customStart = MemorySafety.ParseAddress(txtCustomStart.Text, "Custom region start");
                            var customEnd = MemorySafety.ParseAddress(txtCustomEnd.Text, "Custom region end");
                            var customSize = customEnd - customStart;
                            await _memoryEngine.StartBaseAddressMonitoring(customStart, customSize);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (InvokeRequired)
                        {
                            Invoke(() => LogMessage($"❌ Recording error: {ex.Message}"));
                        }
                    }
                });

                // Update UI immediately
                if (btnStartRecording != null) btnStartRecording.Enabled = false;
                if (btnStopRecording != null) btnStopRecording.Enabled = true;
                if (btnExportRecording != null) btnExportRecording.Enabled = false;
                if (chkMonitorBaseAddr != null) chkMonitorBaseAddr.Enabled = false;
                if (lblRecordingStatus != null)
                {
                    lblRecordingStatus.Text = monitorBase ? "🔴 Recording (+ Base)..." : "🔴 Recording...";
                    lblRecordingStatus.ForeColor = Color.Red;
                }

                if (dgvRecordingChanges != null)
                    dgvRecordingChanges.Rows.Clear();
                
                // Clear frequency tracking for new recording session
                _addressChangeFrequency.Clear();
                _addressDescriptions.Clear();
                _recentChanges.Clear();

                _recordingUpdateTimer?.Start();

                LogMessage("✅ Recording started");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Recording start error: {ex.Message}");
            }
        }

        private void BtnStopRecording_Click(object? sender, EventArgs e)
        {
            try
            {
                _recordingUpdateTimer?.Stop();

                var recording = _memoryEngine.StopRecording();
                _memoryEngine.StopBaseAddressMonitoring(); // Stop base monitoring too

                if (btnStartRecording != null) btnStartRecording.Enabled = true;
                if (btnStopRecording != null) btnStopRecording.Enabled = false;
                if (btnExportRecording != null) btnExportRecording.Enabled = recording != null && recording.Changes.Count > 0;
                if (chkMonitorBaseAddr != null) chkMonitorBaseAddr.Enabled = true;
                if (lblRecordingStatus != null)
                {
                    lblRecordingStatus.Text = "⏹️ Stopped";
                    lblRecordingStatus.ForeColor = Color.Gray;
                }

                if (recording != null)
                {
                    var uniqueAddresses = _addressChangeFrequency.Count;
                    var topAddress = _addressChangeFrequency.OrderByDescending(x => x.Value).FirstOrDefault();
                    
                    LogMessage($"✅ Recording stopped: {recording.Changes.Count} total changes");
                    LogMessage($"   📊 {uniqueAddresses} unique addresses modified");
                    
                    if (topAddress.Key != 0)
                    {
                        LogMessage($"   🔥 Hottest address: 0x{topAddress.Key:X8} ({topAddress.Value} changes)");
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Stop recording error: {ex.Message}");
            }
        }

        private Dictionary<uint, int> _addressChangeFrequency = new();
        private Dictionary<uint, string> _addressDescriptions = new();
        private Queue<(DateTime timestamp, string message)> _recentChanges = new();
        
        private void RecordingUpdateTimer_Tick(object? sender, EventArgs e)
        {
            // Run update asynchronously to prevent UI freezing
            Task.Run(() =>
            {
                var recording = _memoryEngine.GetActiveRecording();
                if (recording == null || dgvRecordingChanges == null) return;

                // Calculate new changes
                var currentCount = 0;
                if (InvokeRequired)
                {
                    Invoke(() => currentCount = dgvRecordingChanges.Rows.Count);
                }
                else
                {
                    currentCount = dgvRecordingChanges.Rows.Count;
                }

                if (recording.Changes.Count > currentCount)
                {
                    var newChanges = recording.Changes.Skip(currentCount).Take(100).ToList(); // Limit batch updates
                    
                    // Track frequency and show instant notifications
                    foreach (var change in newChanges)
                    {
                        if (!_addressChangeFrequency.ContainsKey(change.Address))
                            _addressChangeFrequency[change.Address] = 0;
                        _addressChangeFrequency[change.Address]++;
                        
                        // Auto-describe based on change patterns
                        if (!_addressDescriptions.ContainsKey(change.Address))
                        {
                            _addressDescriptions[change.Address] = DescribeChange(change);
                        }
                        
                        // Add to recent changes for instant feedback
                        var delta = (int)change.NewValue - (int)change.OldValue;
                        var deltaStr = delta > 0 ? $"+{delta}" : delta.ToString();
                        var isBase = change.Address >= 0x82000000 && change.Address < 0x82001000;
                        var prefix = isBase ? "📍" : "⚡";
                        var msg = $"{prefix} 0x{change.Address:X8} {deltaStr} [{_addressDescriptions[change.Address]}]";
                        
                        _recentChanges.Enqueue((DateTime.Now, msg));
                        
                        // Keep only last 5 seconds of changes
                        while (_recentChanges.Count > 0 && (DateTime.Now - _recentChanges.Peek().timestamp).TotalSeconds > 5)
                        {
                            _recentChanges.Dequeue();
                        }
                    }
                    
                    // Update UI on UI thread
                    if (InvokeRequired)
                    {
                        Invoke(() =>
                        {
                            AddRecordingChanges(newChanges, recording.Changes.Count);
                            UpdateHotAddresses();
                            UpdateRecentChanges();
                        });
                    }
                    else
                    {
                        AddRecordingChanges(newChanges, recording.Changes.Count);
                        UpdateHotAddresses();
                        UpdateRecentChanges();
                    }
                }
            });
        }
        
        private string DescribeChange(AdvancedMemoryEngine.MemoryChange change)
        {
            var delta = (int)change.NewValue - (int)change.OldValue;
            
            if (delta == -1) return "Decrement";
            if (delta == 1) return "Increment";
            if (Math.Abs(delta) >= 100) return "Major Change";
            if (delta < 0) return "Decrease";
            if (delta > 0) return "Increase";
            return "Modified";
        }
        
        private void UpdateHotAddresses()
        {
            var tab = (Control)this; // search the whole window (pages are hosted in the shell, not a TabControl)
            if (tab == null) return;
            
            var lstHotAddresses = tab.Controls.Find("lstHotAddresses", true).FirstOrDefault() as ListBox;
            if (lstHotAddresses == null) return;
            
            lstHotAddresses.BeginUpdate();
            lstHotAddresses.Items.Clear();
            
            var hotAddresses = _addressChangeFrequency
                .OrderByDescending(x => x.Value)
                .Take(20)
                .Select(x => 
                {
                    var desc = _addressDescriptions.ContainsKey(x.Key) ? _addressDescriptions[x.Key] : "Unknown";
                    return $"0x{x.Key:X8} [{x.Value}x] - {desc}";
                });
            
            foreach (var item in hotAddresses)
            {
                lstHotAddresses.Items.Add(item);
            }
            
            lstHotAddresses.EndUpdate();
        }
        
        private void UpdateRecentChanges()
        {
            if (lstRecentChanges == null) return;
            
            lstRecentChanges.BeginUpdate();
            lstRecentChanges.Items.Clear();
            
            // Show most recent changes at top
            var recentItems = _recentChanges.Reverse().Take(25);
            foreach (var (timestamp, message) in recentItems)
            {
                var age = (DateTime.Now - timestamp).TotalSeconds;
                var ageStr = age < 1 ? "NOW" : $"{age:F1}s";
                lstRecentChanges.Items.Add($"[{ageStr}] {message}");
            }
            
            // Update label with change count
            if (lblRecentActivity != null)
            {
                var count = _recentChanges.Count;
                lblRecentActivity.Text = $"⚡ Live Actions ({count} in last 5s)";
                
                // Flash effect when new changes detected
                if (count > 0)
                {
                    lblRecentActivity.ForeColor = Color.FromArgb(255, 69, 0);
                }
                else
                {
                    lblRecentActivity.ForeColor = Color.FromArgb(255, 140, 0);
                }
            }
            
            lstRecentChanges.EndUpdate();
        }

        private void AddRecordingChanges(List<AdvancedMemoryEngine.MemoryChange> changes, int totalCount)
        {
            if (dgvRecordingChanges == null) return;

            // Get selected memory type
            var tab = (Control)this; // search the whole window (pages are hosted in the shell, not a TabControl)
            var cmbMemoryType = tab?.Controls.Find("cmbMemoryType", true).FirstOrDefault() as ComboBox;
            var memTypeStr = cmbMemoryType?.SelectedItem?.ToString() ?? "All Changes";

            dgvRecordingChanges.SuspendLayout();
            try
            {
                foreach (var change in changes)
                {
                    // Format values according to selected type
                    string oldValueStr, newValueStr, deltaStr;
                    
                    if (memTypeStr.Contains("Int32"))
                    {
                        var oldVal = (int)change.OldValue;
                        var newVal = (int)change.NewValue;
                        oldValueStr = oldVal.ToString();
                        newValueStr = newVal.ToString();
                        var delta = newVal - oldVal;
                        deltaStr = delta > 0 ? $"+{delta}" : delta.ToString();
                    }
                    else if (memTypeStr.Contains("Int16"))
                    {
                        var oldVal = (short)change.OldValue;
                        var newVal = (short)change.NewValue;
                        oldValueStr = oldVal.ToString();
                        newValueStr = newVal.ToString();
                        var delta = newVal - oldVal;
                        deltaStr = delta > 0 ? $"+{delta}" : delta.ToString();
                    }
                    else if (memTypeStr.Contains("Float"))
                    {
                        // Assume 4 bytes for float - need to read full value
                        oldValueStr = $"0x{change.OldValue:X2}";
                        newValueStr = $"0x{change.NewValue:X2}";
                        deltaStr = "Float";
                    }
                    else if (memTypeStr.Contains("Double"))
                    {
                        oldValueStr = $"0x{change.OldValue:X2}";
                        newValueStr = $"0x{change.NewValue:X2}";
                        deltaStr = "Double";
                    }
                    else // Byte or All Changes
                    {
                        oldValueStr = $"0x{change.OldValue:X2}";
                        newValueStr = $"0x{change.NewValue:X2}";
                        var delta = (int)change.NewValue - (int)change.OldValue;
                        deltaStr = delta > 0 ? $"+{delta}" : delta.ToString();
                    }
                    
                    var freq = _addressChangeFrequency.ContainsKey(change.Address) ? _addressChangeFrequency[change.Address] : 1;
                    var desc = _addressDescriptions.ContainsKey(change.Address) ? _addressDescriptions[change.Address] : "";
                    
                    // Mark if this is from custom region
                    var isCustomRegion = change.Address >= 0x82000000 && change.Address < 0x84000000;
                    if (isCustomRegion && string.IsNullOrEmpty(desc))
                    {
                        desc = "📍 Custom Region";
                    }
                    
                    int rowIndex = dgvRecordingChanges.Rows.Add(
                        $"{change.TimeSinceStart.TotalMilliseconds:F0}",
                        $"0x{change.Address:X8}",
                        oldValueStr,
                        newValueStr,
                        deltaStr,
                        freq.ToString(),
                        desc
                    );
                    
                    // Color code based on change type
                    var row = dgvRecordingChanges.Rows[rowIndex];
                    
                    // Special highlighting for custom region changes
                    if (isCustomRegion)
                    {
                        row.DefaultCellStyle.BackColor = Color.FromArgb(230, 240, 255); // Light blue
                        row.Cells["Address"].Style.Font = new Font(dgvRecordingChanges.Font, FontStyle.Bold);
                        row.Cells["Address"].Style.ForeColor = Color.FromArgb(0, 102, 204);
                    }
                    
                    // Color based on delta
                    if (deltaStr.StartsWith("+"))
                    {
                        row.Cells["NewValue"].Style.BackColor = Color.FromArgb(200, 255, 200); // Light green
                        row.Cells["Delta"].Style.ForeColor = Color.Green;
                        row.Cells["Delta"].Style.Font = new Font(dgvRecordingChanges.Font, FontStyle.Bold);
                    }
                    else if (deltaStr.StartsWith("-"))
                    {
                        row.Cells["NewValue"].Style.BackColor = Color.FromArgb(255, 220, 220); // Light red
                        row.Cells["Delta"].Style.ForeColor = Color.Red;
                        row.Cells["Delta"].Style.Font = new Font(dgvRecordingChanges.Font, FontStyle.Bold);
                    }
                    
                    // Highlight hot addresses
                    if (freq > 10)
                    {
                        if (!isCustomRegion) // Don't override custom region color
                        {
                            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 250, 205); // Light yellow
                        }
                        row.Cells["Frequency"].Style.Font = new Font(dgvRecordingChanges.Font, FontStyle.Bold);
                        row.Cells["Frequency"].Style.ForeColor = Color.DarkOrange;
                    }
                    else if (freq > 5)
                    {
                        row.Cells["Frequency"].Style.ForeColor = Color.OrangeRed;
                    }
                }

                // Auto-scroll to latest (only if near bottom)
                if (dgvRecordingChanges.Rows.Count > 0)
                {
                    var lastVisible = dgvRecordingChanges.FirstDisplayedScrollingRowIndex + dgvRecordingChanges.DisplayedRowCount(false);
                    if (lastVisible >= dgvRecordingChanges.Rows.Count - 10)
                    {
                        dgvRecordingChanges.FirstDisplayedScrollingRowIndex = 
                            Math.Max(0, dgvRecordingChanges.Rows.Count - 1);
                    }
                }

                // Update status with rate
                if (lblRecordingStatus != null)
                {
                    var rate = changes.Count > 0 ? (totalCount / (DateTime.Now - changes[0].Timestamp).TotalSeconds) : 0;
                    var baseText = chkMonitorBaseAddr?.Checked == true ? " (+ Base)" : "";
                    lblRecordingStatus.Text = $"🔴 Recording{baseText}... ({totalCount} changes, {rate:F1}/s)";
                }
            }
            finally
            {
                dgvRecordingChanges.ResumeLayout();
            }
        }

        private void BtnExportRecording_Click(object? sender, EventArgs e)
        {
            if (dgvRecordingChanges == null || dgvRecordingChanges.Rows.Count == 0)
            {
                LogMessage("⚠️ No recording data to export");
                return;
            }

            try
            {
                using var dialog = new SaveFileDialog
                {
                    Filter = "CSV Files (*.csv)|*.csv|Text Files (*.txt)|*.txt",
                    DefaultExt = "csv",
                    FileName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    var lines = new List<string>();
                    
                    // Header
                    lines.Add("=== Memory Recording Analysis ===");
                    lines.Add($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    lines.Add($"Total Changes: {dgvRecordingChanges.Rows.Count}");
                    lines.Add($"Unique Addresses: {_addressChangeFrequency.Count}");
                    lines.Add("");
                    
                    // Hot Addresses Summary
                    lines.Add("=== Top 10 Most Active Addresses ===");
                    var topAddresses = _addressChangeFrequency
                        .OrderByDescending(x => x.Value)
                        .Take(10);
                    
                    foreach (var addr in topAddresses)
                    {
                        var desc = _addressDescriptions.ContainsKey(addr.Key) ? _addressDescriptions[addr.Key] : "Unknown";
                        lines.Add($"0x{addr.Key:X8}, {addr.Value} changes, {desc}");
                    }
                    
                    lines.Add("");
                    lines.Add("=== Detailed Change Log ===");
                    lines.Add("Time(ms),Address,OldValue,NewValue,Delta,Frequency,Description");

                    foreach (DataGridViewRow row in dgvRecordingChanges.Rows)
                    {
                        if (row.IsNewRow) continue;
                        var cells = row.Cells;
                        lines.Add($"{cells[0].Value},{cells[1].Value},{cells[2].Value},{cells[3].Value},{cells[4].Value},{cells[5].Value},{cells[6].Value}");
                    }

                    File.WriteAllLines(dialog.FileName, lines);
                    LogMessage($"✅ Exported {dgvRecordingChanges.Rows.Count} changes to {Path.GetFileName(dialog.FileName)}");
                    LogMessage($"   📊 Analysis includes frequency data and hot address summary");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Export error: {ex.Message}");
            }
        }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _scriptIde?.SaveSession();
        _scriptIde?.Stop();
        _autoRefreshTimer?.Stop();
        _watchListTimer?.Stop();
        _recordingUpdateTimer?.Stop();
        _xbdmClient?.Disconnect();
        _statusTimer?.Stop();
        _freezer?.Clear();
        base.OnFormClosing(e);
    }

    // ==================== STATIC ANALYSIS METHODS ====================
    
    private void BtnLoadBinary_Click(object? sender, EventArgs e)
    {
        using var openFileDialog = new OpenFileDialog
        {
            Title = "Load Binary File",
            Filter = "All Files (*.*)|*.*|Executable Files (*.exe;*.dll;*.xex)|*.exe;*.dll;*.xex|Xbox Files (*.xex)|*.xex",
            FilterIndex = 2
        };

        if (openFileDialog.ShowDialog() == DialogResult.OK)
        {
            try
            {
                LogMessage($"📂 Loading binary: {openFileDialog.FileName}");
                _loadedBinary = File.ReadAllBytes(openFileDialog.FileName);
                _loadedBase = 0;
                if (XexFile.IsXex(_loadedBinary))
                {
                    // auto XEX → EXE: analyse the decrypted, decompressed PE image instead of the encrypted container
                    try
                    {
                        var xex = XexFile.Parse(_loadedBinary);
                        _loadedBinary = xex.ExtractBasefile(LogMessage);
                        _loadedBase = xex.LoadAddress;
                        LogMessage($"🔓 XEX converted in memory (title {xex.TitleId:X8}, v{xex.Version}) – analysing PE image @ 0x{_loadedBase:X8}");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"⚠️ XEX conversion failed ({ex.Message}) – analysing raw container");
                    }
                }
                AnalyzeBinary(openFileDialog.FileName);
                
                // Enable export button
                var tab = (Control)this; // search the whole window (pages are hosted in the shell, not a TabControl)
                var btnExport = tab?.Controls.Find("btnExportAnalysis", true).FirstOrDefault() as Button;
                if (btnExport != null) btnExport.Enabled = true;
                
                LogMessage($"✅ Binary loaded: {_loadedBinary.Length} bytes");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Error loading binary: {ex.Message}");
            }
        }
    }

    private void AnalyzeBinary(string filePath)
    {
        if (_loadedBinary == null) return;

        var tab = (Control)this; // search the whole window (pages are hosted in the shell, not a TabControl)
        var txtFileInfo = tab?.Controls.Find("txtFileInfo", true).FirstOrDefault() as RichTextBox;
        var txtStats = tab?.Controls.Find("txtStats", true).FirstOrDefault() as RichTextBox;

        // Display file info
        if (txtFileInfo != null)
        {
            var fileInfo = new FileInfo(filePath);
            txtFileInfo.Clear();
            txtFileInfo.AppendText($"📁 File: {fileInfo.Name}\n");
            if (fileInfo.Exists)
            {
                txtFileInfo.AppendText($"📏 Size: {FormatFileSize(fileInfo.Length)}\n");
                txtFileInfo.AppendText($"📅 Modified: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm}\n");
            }
            else
            {
                // in-memory image (e.g. converted XEX or a module dumped from the console)
                txtFileInfo.AppendText($"📏 Size: {FormatFileSize(_loadedBinary.Length)} (in-memory image)\n");
            }
            txtFileInfo.AppendText($"🔢 Bytes: {_loadedBinary.Length:N0}\n\n");
            
            // Detect file type (Xbox 360 is big-endian)
            var peInfo = PeImage.TryParse(_loadedBinary, _loadedBase);
            if (peInfo != null)
            {
                txtFileInfo.AppendText(peInfo.IsXbox360 ? "🔍 Type: Xbox 360 PE image (PowerPC, big-endian code)\n" : "🔍 Type: PE Executable\n");
                txtFileInfo.AppendText($"📍 Image base: 0x{peInfo.ImageBase:X8}\n");
                if (peInfo.EntryPoint != 0) txtFileInfo.AppendText($"🚪 Entry point: 0x{peInfo.EntryPoint:X8}\n");
                txtFileInfo.AppendText($"📚 Sections: {string.Join(", ", peInfo.Sections.Select(x => x.Name))}\n");
            }
            else if (_loadedBinary.Length >= 4)
            {
                var magic = BitConverter.ToUInt32(_loadedBinary, 0);
                var magicBE = (uint)((_loadedBinary[0] << 24) | (_loadedBinary[1] << 16) | (_loadedBinary[2] << 8) | _loadedBinary[3]);
                
                if (magic == 0x00005A4D) // MZ (little-endian)
                {
                    txtFileInfo.AppendText("🔍 Type: PE Executable\n");
                    txtFileInfo.AppendText("🏗️ Arch: x86/x64 (Little-Endian)\n");
                }
                else if (magicBE == 0x58455831 || magicBE == 0x58455832) // XEX1/XEX2 (big-endian)
                {
                    txtFileInfo.AppendText("🔍 Type: Xbox 360 XEX\n");
                    txtFileInfo.AppendText("🏗️ Arch: PowerPC (Big-Endian)\n");
                    ParseXEXSections();
                }
                else if (magic == 0x7F454C46) // ELF
                {
                    txtFileInfo.AppendText("🔍 Type: ELF Executable\n");
                }
                else
                {
                    txtFileInfo.AppendText("🔍 Type: Binary/Raw Data\n");
                }
            }
        }

        // Extract and display strings
        ExtractStrings();

        // Analyze sections if not already done by XEX parser
        if (txtTextSection?.Text.Length == 0)
        {
            AnalyzeSections();
        }

        // Update statistics
        UpdateStatistics(txtStats);
    }

    private void ParseXEXSections()
    {
        if (_loadedBinary == null || _loadedBinary.Length < 0x1000) return;

        try
        {
            // XEX files are big-endian
            // XEX header starts at offset 0
            // Look for section information
            
            // For now, use heuristic: find executable code patterns
            // Xbox 360 PowerPC code often starts with specific opcodes
            
            int textStart = FindCodeSection();
            int dataStart = FindDataSection(textStart);
            int rdataStart = FindRDataSection(dataStart);
            
            if (textStart > 0 && txtTextSection != null)
            {
                int textSize = Math.Min(dataStart - textStart, 16384); // Max 16KB
                var textData = new byte[textSize];
                Array.Copy(_loadedBinary, textStart, textData, 0, textSize);
                txtTextSection.Text = FormatHexDump(textData, (uint)textStart);
                LogMessage($"📜 .text section found at 0x{textStart:X8} ({textSize} bytes)");
            }
            
            if (dataStart > 0 && txtDataSection != null)
            {
                int dataSize = Math.Min(rdataStart - dataStart, 16384); // Max 16KB
                var dataData = new byte[dataSize];
                Array.Copy(_loadedBinary, dataStart, dataData, 0, dataSize);
                txtDataSection.Text = FormatHexDump(dataData, (uint)dataStart);
                LogMessage($"📊 .data section found at 0x{dataStart:X8} ({dataSize} bytes)");
            }

            if (rdataStart > 0 && txtRDataSection != null)
            {
                int rdataSize = Math.Min(_loadedBinary.Length - rdataStart, 16384); // Max 16KB
                var rdataData = new byte[rdataSize];
                Array.Copy(_loadedBinary, rdataStart, rdataData, 0, rdataSize);
                txtRDataSection.Text = FormatHexDump(rdataData, (uint)rdataStart);
                LogMessage($"📋 .rdata section found at 0x{rdataStart:X8} ({rdataSize} bytes)");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"⚠️ XEX parsing: {ex.Message}");
            AnalyzeSections(); // Fallback
        }
    }

    private int FindCodeSection()
    {
        // Look for PowerPC code patterns (big-endian instructions)
        // Common PowerPC opcodes: 0x7C, 0x38, 0x48, 0x3C, 0x90, 0x94
        for (int i = 0x1000; i < _loadedBinary.Length - 1024; i += 4)
        {
            if (i + 16 > _loadedBinary.Length) break;
            
            // Look for instruction patterns (4-byte aligned, big-endian)
            int instructionCount = 0;
            for (int j = 0; j < 16; j += 4)
            {
                byte opcode = _loadedBinary[i + j];
                // Common PowerPC opcodes
                if (opcode == 0x7C || opcode == 0x38 || opcode == 0x48 || 
                    opcode == 0x3C || opcode == 0x90 || opcode == 0x94 ||
                    opcode == 0x4B || opcode == 0x60 || opcode == 0x80 ||
                    opcode == 0x2C || opcode == 0x40 || opcode == 0x41)
                {
                    instructionCount++;
                }
            }
            
            // If we found multiple valid-looking instructions, this might be code
            if (instructionCount >= 3)
            {
                return i;
            }
        }
        return 0x1000; // Default
    }

    private int FindDataSection(int afterOffset)
    {
        // Data section typically follows code, look for lower entropy
        int searchStart = Math.Max(afterOffset + 0x1000, 0x1000);
        if (searchStart < _loadedBinary.Length / 2)
        {
            return _loadedBinary.Length / 2; // Fallback to midpoint
        }
        return searchStart;
    }

    private void BtnImportText_Click(object? sender, EventArgs e)
    {
        using var openFileDialog = new OpenFileDialog
        {
            Title = "Import .text Section",
            Filter = "All Files (*.*)|*.*|Binary Files (*.bin)|*.bin",
            FilterIndex = 1
        };

        if (openFileDialog.ShowDialog() == DialogResult.OK)
        {
            try
            {
                var textData = File.ReadAllBytes(openFileDialog.FileName);
                if (txtTextSection != null)
                {
                    txtTextSection.Text = FormatHexDump(textData, 0x82000000); // Common Xbox 360 base
                    LogMessage($"✅ Imported .text: {textData.Length} bytes");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Import error: {ex.Message}");
            }
        }
    }

    private void BtnImportData_Click(object? sender, EventArgs e)
    {
        using var openFileDialog = new OpenFileDialog
        {
            Title = "Import .data Section",
            Filter = "All Files (*.*)|*.*|Binary Files (*.bin)|*.bin",
            FilterIndex = 1
        };

        if (openFileDialog.ShowDialog() == DialogResult.OK)
        {
            try
            {
                var dataData = File.ReadAllBytes(openFileDialog.FileName);
                if (txtDataSection != null)
                {
                    txtDataSection.Text = FormatHexDump(dataData, 0x83000000); // Common Xbox 360 data base
                    LogMessage($"✅ Imported .data: {dataData.Length} bytes");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Import error: {ex.Message}");
            }
        }
    }

    private void ExtractStrings()
    {
        if (_loadedBinary == null || lstStrings == null) return;

        lstStrings.Items.Clear();
        _stringReferences.Clear();

        var minLength = 4;
        var currentString = new System.Text.StringBuilder();
        uint stringStartOffset = 0;

        for (uint i = 0; i < _loadedBinary.Length; i++)
        {
            byte b = _loadedBinary[i];

            if (b >= 32 && b <= 126) // Printable ASCII
            {
                if (currentString.Length == 0)
                    stringStartOffset = i;
                currentString.Append((char)b);
            }
            else
            {
                if (currentString.Length >= minLength)
                {
                    var str = currentString.ToString();
                    _stringReferences[stringStartOffset] = str;
                    lstStrings.Items.Add($"0x{stringStartOffset:X8}: {str}");
                }
                currentString.Clear();
            }
        }

        LogMessage($"🔤 Extracted {lstStrings.Items.Count} strings");
    }

    private void AnalyzeSections()
    {
        if (_loadedBinary == null || txtTextSection == null || txtDataSection == null || txtRDataSection == null) return;

        // Try to identify sections based on content analysis
        var sections = IdentifySections();

        // Display .text section
        txtTextSection.Clear();
        if (sections.TextStart >= 0 && sections.TextSize > 0)
        {
            var textData = new byte[Math.Min(sections.TextSize, 16384)]; // Max 16KB display
            Array.Copy(_loadedBinary, sections.TextStart, textData, 0, textData.Length);
            txtTextSection.Text = FormatHexDump(textData, (uint)(_loadedBase + sections.TextStart));
            LogMessage($"📜 .text section: 0x{sections.TextStart:X8} - 0x{sections.TextStart + sections.TextSize:X8} ({sections.TextSize} bytes)");
        }

        // Display .data section
        txtDataSection.Clear();
        if (sections.DataStart >= 0 && sections.DataSize > 0)
        {
            var dataData = new byte[Math.Min(sections.DataSize, 16384)]; // Max 16KB display
            Array.Copy(_loadedBinary, sections.DataStart, dataData, 0, dataData.Length);
            txtDataSection.Text = FormatHexDump(dataData, (uint)(_loadedBase + sections.DataStart));
            LogMessage($"📊 .data section: 0x{sections.DataStart:X8} - 0x{sections.DataStart + sections.DataSize:X8} ({sections.DataSize} bytes)");
        }

        // Display .rdata section
        txtRDataSection.Clear();
        if (sections.RDataStart >= 0 && sections.RDataSize > 0)
        {
            var rdataData = new byte[Math.Min(sections.RDataSize, 16384)]; // Max 16KB display
            Array.Copy(_loadedBinary, sections.RDataStart, rdataData, 0, rdataData.Length);
            txtRDataSection.Text = FormatHexDump(rdataData, (uint)(_loadedBase + sections.RDataStart));
            LogMessage($"📋 .rdata section: 0x{sections.RDataStart:X8} - 0x{sections.RDataStart + sections.RDataSize:X8} ({sections.RDataSize} bytes)");
        }
    }

    private (int TextStart, int TextSize, int DataStart, int DataSize, int RDataStart, int RDataSize) IdentifySections()
    {
        if (_loadedBinary == null)
            return (-1, 0, -1, 0, -1, 0);

        // Real PE section table (XEX files are converted to PE on load)
        var pe = PeImage.TryParse(_loadedBinary);
        if (pe != null && pe.Sections.Count > 0)
        {
            bool memoryLayout = _loadedBinary.Length >= pe.SizeOfImage || pe.Sections.All(x => x.RawOffset == x.Rva);
            (int, int) Range(PeImage.Section? x)
            {
                if (x == null) return (-1, 0);
                int off = (int)(memoryLayout ? x.Rva : x.RawOffset);
                int size = (int)Math.Min(memoryLayout ? x.VirtualSize : Math.Max(x.RawSize, 1), (uint)Math.Max(0, _loadedBinary.Length - off));
                return off >= 0 && off < _loadedBinary.Length ? (off, size) : (-1, 0);
            }
            var text = Range(pe.Sections.FirstOrDefault(x => x.IsCode));
            var data = Range(pe.FindSection(".data") ?? pe.Sections.FirstOrDefault(x => x.IsWritable && !x.IsCode));
            var rdata = Range(pe.FindSection(".rdata") ?? pe.Sections.FirstOrDefault(x => !x.IsWritable && !x.IsCode));
            return (text.Item1, text.Item2, data.Item1, data.Item2, rdata.Item1, rdata.Item2);
        }

        // For Xbox 360 XEX files, try to parse sections properly
        if (TryParseXEXSections(out var sections))
            return sections;

        // Fallback: heuristic-based section identification
        int textStart = FindCodeSection();
        int dataStart = FindDataSection(textStart);
        int rdataStart = FindRDataSection(dataStart);

        int textSize = dataStart - textStart;
        int dataSize = rdataStart - dataStart;
        int rdataSize = _loadedBinary.Length - rdataStart;

        return (textStart, textSize, dataStart, dataSize, rdataStart, rdataSize);
    }

    private bool TryParseXEXSections(out (int TextStart, int TextSize, int DataStart, int DataSize, int RDataStart, int RDataSize) sections)
    {
        sections = (-1, 0, -1, 0, -1, 0);

        if (_loadedBinary == null || _loadedBinary.Length < 0x2000) return false;

        try
        {
            // XEX files have a header that may contain section information
            // For now, use content-based heuristics since full XEX parsing is complex

            // Look for section-like patterns
            int textStart = FindCodeSection();
            if (textStart < 0) return false;

            int dataStart = FindDataSection(textStart);
            int rdataStart = FindRDataSection(dataStart);

            sections = (textStart, dataStart - textStart, dataStart, rdataStart - dataStart, rdataStart, _loadedBinary.Length - rdataStart);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private int FindRDataSection(int afterOffset)
    {
        // .rdata typically contains read-only data like strings and constants
        // Look for areas with high string density after .data
        int searchStart = Math.Max(afterOffset + 0x1000, 0x1000);
        if (searchStart >= _loadedBinary.Length) return _loadedBinary.Length;

        // For simplicity, assume .rdata starts 1/3 into the remaining space
        int remaining = _loadedBinary.Length - afterOffset;
        return afterOffset + (remaining / 3);
    }

    private void UpdateStatistics(RichTextBox? txtStats)
    {
        if (txtStats == null || _loadedBinary == null) return;

        txtStats.Clear();
        txtStats.AppendText($"📊 Analysis Statistics\n");
        txtStats.AppendText($"{'=',30}\n\n");
        txtStats.AppendText($"Total Size: {_loadedBinary.Length:N0} bytes\n");
        txtStats.AppendText($"Strings: {lstStrings?.Items.Count ?? 0}\n");
        txtStats.AppendText($"Cross-Refs: {_crossReferences.Count}\n\n");

        // Byte distribution
        var byteCounts = new int[256];
        foreach (var b in _loadedBinary)
            byteCounts[b]++;

        var entropy = CalculateEntropy(byteCounts, _loadedBinary.Length);
        txtStats.AppendText($"Entropy: {entropy:F2}\n");
        
        // Most common bytes
        var topBytes = byteCounts
            .Select((count, idx) => new { ByteValue = idx, Count = count })
            .OrderByDescending(x => x.Count)
            .Take(5);

        txtStats.AppendText($"\nTop Bytes:\n");
        foreach (var item in topBytes)
        {
            var pct = (item.Count * 100.0) / _loadedBinary.Length;
            txtStats.AppendText($"  0x{item.ByteValue:X2}: {item.Count:N0} ({pct:F1}%)\n");
        }
    }

    private double CalculateEntropy(int[] byteCounts, int totalBytes)
    {
        double entropy = 0;
        foreach (var count in byteCounts)
        {
            if (count == 0) continue;
            double probability = (double)count / totalBytes;
            entropy -= probability * Math.Log2(probability);
        }
        return entropy;
    }

    private void FilterStrings(string filter)
    {
        if (lstStrings == null) return;

        lstStrings.Items.Clear();

        if (string.IsNullOrWhiteSpace(filter))
        {
            // Show all strings
            foreach (var kvp in _stringReferences)
            {
                lstStrings.Items.Add($"0x{kvp.Key:X8}: {kvp.Value}");
            }
        }
        else
        {
            // Filter strings
            foreach (var kvp in _stringReferences)
            {
                if (kvp.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    lstStrings.Items.Add($"0x{kvp.Key:X8}: {kvp.Value}");
                }
            }
        }
    }

    private void LstStrings_DoubleClick(object? sender, EventArgs e)
    {
        if (lstStrings?.SelectedItem == null) return;

        var selected = lstStrings.SelectedItem.ToString();
        if (selected == null) return;

        // Extract address from "0xXXXXXXXX: string" format
        var colonIdx = selected.IndexOf(':');
        if (colonIdx > 0)
        {
            var addrStr = selected.Substring(0, colonIdx).Replace("0x", "");
            if (uint.TryParse(addrStr, System.Globalization.NumberStyles.HexNumber, null, out uint addr))
            {
                LogMessage($"🔍 String at 0x{addr:X8}: {selected.Substring(colonIdx + 2)}");
                FindCrossReferencesForAddress(addr);
            }
        }
    }

    private void BtnSearchPattern_Click(object? sender, EventArgs e)
    {
        if (_loadedBinary == null || txtSearchPattern == null) return;

        var patternStr = txtSearchPattern.Text.Trim();
        if (string.IsNullOrWhiteSpace(patternStr))
        {
            LogMessage("⚠️ Enter a pattern to search (e.g., 48 8B ?? 48 89)");
            return;
        }

        LogMessage($"🔎 Searching for pattern: {patternStr}");

        var pattern = ParsePattern(patternStr);
        if (pattern == null)
        {
            LogMessage("❌ Invalid pattern format");
            return;
        }

        var matches = SearchPattern(pattern);
        LogMessage($"✅ Found {matches.Count} matches");

        if (matches.Count > 0 && lstXRefs != null)
        {
            lstXRefs.Items.Clear();
            foreach (var match in matches.Take(100)) // Limit to 100
            {
                lstXRefs.Items.Add($"0x{match:X8}: Pattern Match");
            }
        }
    }

    private byte[]? ParsePattern(string pattern)
    {
        var parts = pattern.Split(new[] { ' ', ',', '-' }, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>();

        foreach (var part in parts)
        {
            if (part == "??" || part == "?")
            {
                bytes.Add(0xFF); // Wildcard marker (we'll handle in search)
            }
            else if (byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out byte b))
            {
                bytes.Add(b);
            }
            else
            {
                return null;
            }
        }

        return bytes.ToArray();
    }

    private List<uint> SearchPattern(byte[] pattern)
    {
        var matches = new List<uint>();
        if (_loadedBinary == null) return matches;

        for (uint i = 0; i <= _loadedBinary.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (pattern[j] != 0xFF && _loadedBinary[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                matches.Add(i);
        }

        return matches;
    }

    private void BtnFindXRefs_Click(object? sender, EventArgs e)
    {
        if (_loadedBinary == null)
        {
            LogMessage("⚠️ Load a binary first");
            return;
        }

        LogMessage("🔗 Analyzing cross-references (Xbox 360 big-endian)...");
        _crossReferences.Clear();

        // Search for address references - Xbox 360 uses BIG-ENDIAN format
        for (uint i = 0; i <= _loadedBinary.Length - 4; i++)
        {
            // Read as big-endian (Xbox 360 PowerPC format)
            var potentialAddrBE = (uint)((_loadedBinary[i] << 24) | 
                                         (_loadedBinary[i + 1] << 16) | 
                                         (_loadedBinary[i + 2] << 8) | 
                                         _loadedBinary[i + 3]);

            // Xbox 360 address ranges:
            // 0x82000000 - 0x83FFFFFF: Executable memory
            // 0x90000000 - 0x9FFFFFFF: Physical memory
            if ((potentialAddrBE >= 0x82000000 && potentialAddrBE < 0x84000000) ||
                (potentialAddrBE >= 0x90000000 && potentialAddrBE < 0xA0000000))
            {
                if (!_crossReferences.ContainsKey(potentialAddrBE))
                    _crossReferences[potentialAddrBE] = new List<uint>();

                _crossReferences[potentialAddrBE].Add(i);
            }
            
            // Also check little-endian for PC binaries
            var potentialAddrLE = BitConverter.ToUInt32(_loadedBinary, (int)i);
            if (potentialAddrLE >= 0x00400000 && potentialAddrLE < 0x10000000)
            {
                if (!_crossReferences.ContainsKey(potentialAddrLE))
                    _crossReferences[potentialAddrLE] = new List<uint>();

                _crossReferences[potentialAddrLE].Add(i);
            }
        }

        LogMessage($"✅ Found {_crossReferences.Count} potential cross-references");

        // Display in list
        if (lstXRefs != null)
        {
            lstXRefs.Items.Clear();
            var topXRefs = _crossReferences
                .OrderByDescending(kvp => kvp.Value.Count)
                .Take(50);

            foreach (var kvp in topXRefs)
            {
                lstXRefs.Items.Add($"0x{kvp.Key:X8} [{kvp.Value.Count} refs]");
            }
        }
    }

    private void FindCrossReferencesForAddress(uint address)
    {
        if (_crossReferences.TryGetValue(address, out var refs))
        {
            LogMessage($"🔗 Found {refs.Count} cross-references to 0x{address:X8}");
            
            if (lstXRefs != null)
            {
                lstXRefs.Items.Clear();
                foreach (var refAddr in refs.Take(100))
                {
                    lstXRefs.Items.Add($"0x{refAddr:X8} → 0x{address:X8}");
                }
            }
        }
        else
        {
            LogMessage($"No cross-references found for 0x{address:X8}");
        }
    }

    private void LstXRefs_DoubleClick(object? sender, EventArgs e)
    {
        if (lstXRefs?.SelectedItem == null) return;

        var selected = lstXRefs.SelectedItem.ToString();
        if (selected == null) return;

        // Extract address from format
        var parts = selected.Split(new[] { ' ', '→' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0)
        {
            var addrStr = parts[0].Replace("0x", "").Replace("[", "");
            if (uint.TryParse(addrStr, System.Globalization.NumberStyles.HexNumber, null, out uint addr))
            {
                LogMessage($"📍 Jump to 0x{addr:X8}");
                // Could highlight in hex view here
            }
        }
    }

    private void BtnExportAnalysis_Click(object? sender, EventArgs e)
    {
        if (_loadedBinary == null)
        {
            LogMessage("⚠️ No binary loaded");
            return;
        }

        using var saveFileDialog = new SaveFileDialog
        {
            Title = "Export Analysis Report",
            Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
            DefaultExt = "txt",
            FileName = "analysis_report.txt"
        };

        if (saveFileDialog.ShowDialog() == DialogResult.OK)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("═══════════════════════════════════════════════");
                sb.AppendLine("        STATIC ANALYSIS REPORT");
                sb.AppendLine("═══════════════════════════════════════════════");
                sb.AppendLine($"Generated: {DateTime.Now}");
                sb.AppendLine($"Binary Size: {_loadedBinary.Length:N0} bytes");
                sb.AppendLine();
                sb.AppendLine("📊 STATISTICS");
                sb.AppendLine("─────────────────────────────────────────────");
                sb.AppendLine($"Total Strings: {lstStrings?.Items.Count ?? 0}");
                sb.AppendLine($"Cross-References: {_crossReferences.Count}");
                sb.AppendLine();
                sb.AppendLine("🔤 DETECTED STRINGS");
                sb.AppendLine("─────────────────────────────────────────────");
                if (lstStrings != null)
                {
                    foreach (var item in lstStrings.Items)
                    {
                        sb.AppendLine(item.ToString());
                    }
                }
                sb.AppendLine();
                sb.AppendLine("🔗 CROSS-REFERENCES (Top 50)");
                sb.AppendLine("─────────────────────────────────────────────");
                foreach (var kvp in _crossReferences.OrderByDescending(x => x.Value.Count).Take(50))
                {
                    sb.AppendLine($"0x{kvp.Key:X8}: {kvp.Value.Count} references");
                }

                File.WriteAllText(saveFileDialog.FileName, sb.ToString());
                LogMessage($"💾 Analysis exported to: {saveFileDialog.FileName}");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Export error: {ex.Message}");
            }
        }
    }

    private void ClearStaticAnalysis()
    {
        _loadedBinary = null;
        _stringReferences.Clear();
        _crossReferences.Clear();

        txtTextSection?.Clear();
        txtDataSection?.Clear();
        txtRDataSection?.Clear();
        lstStrings?.Items.Clear();
        lstXRefs?.Items.Clear();

        var tab = (Control)this; // search the whole window (pages are hosted in the shell, not a TabControl)
        var txtFileInfo = tab?.Controls.Find("txtFileInfo", true).FirstOrDefault() as RichTextBox;
        var txtStats = tab?.Controls.Find("txtStats", true).FirstOrDefault() as RichTextBox;
        var btnExport = tab?.Controls.Find("btnExportAnalysis", true).FirstOrDefault() as Button;

        txtFileInfo?.Clear();
        txtStats?.Clear();
        if (btnExport != null) btnExport.Enabled = false;

        LogMessage("🗑️ Static analysis cleared");
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    // ==================== CROSS-REFERENCING METHODS ====================

    private void TxtTextSection_MouseClick(object? sender, MouseEventArgs e)
    {
        if (txtTextSection == null || _loadedBinary == null) return;

        var line = txtTextSection.GetLineFromCharIndex(txtTextSection.SelectionStart);
        var lineText = txtTextSection.Lines[line];

        // Extract address from line (format: "XXXXXXXX  ...")
        if (lineText.Length >= 8)
        {
            var addrStr = lineText.Substring(0, 8);
            if (uint.TryParse(addrStr, System.Globalization.NumberStyles.HexNumber, null, out uint address))
            {
                CrossReferenceAddress(address, ".text");
            }
        }
    }

    private void TxtDataSection_MouseClick(object? sender, MouseEventArgs e)
    {
        if (txtDataSection == null || _loadedBinary == null) return;

        var line = txtDataSection.GetLineFromCharIndex(txtDataSection.SelectionStart);
        var lineText = txtDataSection.Lines[line];

        // Extract address from line (format: "XXXXXXXX  ...")
        if (lineText.Length >= 8)
        {
            var addrStr = lineText.Substring(0, 8);
            if (uint.TryParse(addrStr, System.Globalization.NumberStyles.HexNumber, null, out uint address))
            {
                CrossReferenceAddress(address, ".data");
            }
        }
    }

    private void TxtRDataSection_MouseClick(object? sender, MouseEventArgs e)
    {
        if (txtRDataSection == null || _loadedBinary == null) return;

        var line = txtRDataSection.GetLineFromCharIndex(txtRDataSection.SelectionStart);
        var lineText = txtRDataSection.Lines[line];

        // Extract address from line (format: "XXXXXXXX  ...")
        if (lineText.Length >= 8)
        {
            var addrStr = lineText.Substring(0, 8);
            if (uint.TryParse(addrStr, System.Globalization.NumberStyles.HexNumber, null, out uint address))
            {
                CrossReferenceAddress(address, ".rdata");
            }
        }
    }

    private void CrossReferenceAddress(uint address, string sourceSection)
    {
        LogMessage($"🔗 Cross-referencing 0x{address:X8} from {sourceSection}");

        // Find references to this address
        if (_crossReferences.TryGetValue(address, out var refs))
        {
            if (lstXRefs != null)
            {
                lstXRefs.Items.Clear();
                lstXRefs.Items.Add($"📍 Address: 0x{address:X8} ({sourceSection})");
                lstXRefs.Items.Add($"📊 References: {refs.Count}");
                lstXRefs.Items.Add(""); // Separator

                foreach (var refAddr in refs.Take(50))
                {
                    var section = DetermineSection(refAddr);
                    lstXRefs.Items.Add($"0x{refAddr:X8} → 0x{address:X8} ({section})");
                }

                if (refs.Count > 50)
                    lstXRefs.Items.Add($"... and {refs.Count - 50} more");
            }
        }
        else
        {
            LogMessage($"No cross-references found for 0x{address:X8}");
            if (lstXRefs != null)
            {
                lstXRefs.Items.Clear();
                lstXRefs.Items.Add($"📍 Address: 0x{address:X8} ({sourceSection})");
                lstXRefs.Items.Add("❌ No references found");
            }
        }

        // Highlight the address in the appropriate section viewer
        HighlightAddressInSection(address);
    }

    private string DetermineSection(uint address)
    {
        if (_loadedBinary == null) return "unknown";

        // Simple section detection based on address ranges
        if (address >= 0x82000000 && address < 0x83000000)
            return ".text";
        else if (address >= 0x83000000 && address < 0x84000000)
            return ".data";
        else if (address >= 0x84000000 && address < 0x85000000)
            return ".rdata";
        else
            return "unknown";
    }

    private void HighlightAddressInSection(uint address)
    {
        // Clear previous highlights
        ClearHighlights();

        var section = DetermineSection(address);
        RichTextBox? targetBox = null;

        switch (section)
        {
            case ".text":
                targetBox = txtTextSection;
                break;
            case ".data":
                targetBox = txtDataSection;
                break;
            case ".rdata":
                targetBox = txtRDataSection;
                break;
        }

        if (targetBox != null && targetBox.Lines.Length > 0)
        {
            // Find the line containing this address
            for (int i = 0; i < targetBox.Lines.Length; i++)
            {
                var line = targetBox.Lines[i];
                if (line.Length >= 8)
                {
                    var lineAddrStr = line.Substring(0, 8);
                    if (uint.TryParse(lineAddrStr, System.Globalization.NumberStyles.HexNumber, null, out uint lineAddr))
                    {
                        if (lineAddr <= address && address < lineAddr + 16)
                        {
                            // Found the line, highlight it
                            var lineStart = targetBox.GetFirstCharIndexFromLine(i);
                            var lineLength = line.Length;
                            targetBox.Select(lineStart, lineLength);
                            targetBox.SelectionBackColor = Color.Yellow;
                            targetBox.SelectionColor = Color.Black;
                            targetBox.ScrollToCaret();
                            break;
                        }
                    }
                }
            }
        }
    }

    private void ClearHighlights()
    {
        if (txtTextSection != null)
        {
            txtTextSection.SelectAll();
            txtTextSection.SelectionBackColor = Color.FromArgb(30, 30, 30);
            txtTextSection.SelectionColor = Color.FromArgb(220, 220, 220);
            txtTextSection.DeselectAll();
        }

        if (txtDataSection != null)
        {
            txtDataSection.SelectAll();
            txtDataSection.SelectionBackColor = Color.FromArgb(30, 30, 30);
            txtDataSection.SelectionColor = Color.FromArgb(220, 220, 220);
            txtDataSection.DeselectAll();
        }

        if (txtRDataSection != null)
        {
            txtRDataSection.SelectAll();
            txtRDataSection.SelectionBackColor = Color.FromArgb(30, 30, 30);
            txtRDataSection.SelectionColor = Color.FromArgb(220, 220, 220);
            txtRDataSection.DeselectAll();
        }
    }

    // ==================== REGISTER VIEWER METHODS ====================

    private async void BtnRefreshRegisters_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !(_xbdmClient.IsConnected))
        {
            LogMessage("❌ Not connected to Xbox 360");
            return;
        }

        LogMessage("🔄 Reading CPU registers...");
        btnRefreshRegisters!.Enabled = false;
        btnRefreshRegisters.Text = "⏳ Reading...";

        try
        {
            var registers = await _xbdmClient.GetRegistersAsync();
            _currentRegisters = registers;

            if (dgvRegisters != null)
            {
                dgvRegisters.Rows.Clear();

                // Add GPR registers (General Purpose Registers)
                for (int i = 0; i < 32; i++)
                {
                    var regName = $"r{i}";
                    if (registers.TryGetValue(regName, out var value))
                    {
                        dgvRegisters.Rows.Add(regName, $"0x{value:X16}", $"{value}", GetRegisterDescription(regName));
                    }
                }

                // Add special purpose registers
                var specialRegs = new[] { "LR", "CTR", "XER", "CR", "MSR", "SRR0", "SRR1", "PC" };
                foreach (var reg in specialRegs)
                {
                    if (registers.TryGetValue(reg, out var value))
                    {
                        dgvRegisters.Rows.Add(reg, $"0x{value:X16}", $"{value}", GetRegisterDescription(reg));
                    }
                }

                // Add FPR registers (Floating Point Registers)
                for (int i = 0; i < 32; i++)
                {
                    var regName = $"FPR{i}";
                    if (registers.TryGetValue(regName, out var value))
                    {
                        // Convert to double for display
                        var doubleValue = BitConverter.Int64BitsToDouble((long)value);
                        dgvRegisters.Rows.Add(regName, $"0x{value:X16}", $"{doubleValue:F6}", GetRegisterDescription(regName));
                    }
                }
            }

            LogMessage($"✅ Loaded {registers.Count} registers");
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Register read error: {ex.Message}");
        }
        finally
        {
            btnRefreshRegisters.Enabled = true;
            btnRefreshRegisters.Text = "🔄 Refresh Registers";
        }
    }

    private async void BtnSetRegister_Click(object? sender, EventArgs e)
    {
        if (_xbdmClient == null || !(_xbdmClient.IsConnected))
        {
            LogMessage("❌ Not connected to Xbox 360");
            return;
        }

        var regName = txtRegisterName!.Text.Trim();
        var valueStr = txtRegisterValue!.Text.Trim();

        if (string.IsNullOrEmpty(regName) || string.IsNullOrEmpty(valueStr))
        {
            LogMessage("⚠️ Enter register name and value");
            return;
        }

        try
        {
            // Parse value (support hex with 0x prefix or decimal)
            ulong value;
            if (valueStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                value = Convert.ToUInt64(valueStr, 16);
            }
            else
            {
                value = Convert.ToUInt64(valueStr);
            }

            var success = await _xbdmClient.SetRegisterAsync(regName, value);
            if (success)
            {
                // Refresh the display
                await Task.Delay(100); // Small delay for the set to take effect
                BtnRefreshRegisters_Click(null, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Invalid value format: {ex.Message}");
        }
    }

    private void ClearRegisterView()
    {
        _currentRegisters.Clear();
        if (dgvRegisters != null)
        {
            dgvRegisters.Rows.Clear();
        }
        LogMessage("🗑️ Register view cleared");
    }

    private string GetRegisterDescription(string registerName)
    {
        return registerName switch
        {
            // General Purpose Registers
            "r0" => "General Purpose Register 0 (often used for return values)",
            "r1" => "General Purpose Register 1 (Stack Pointer)",
            "r2" => "General Purpose Register 2 (TOC/Global Pointer)",
            "r3" => "General Purpose Register 3 (Function argument/return value)",
            "r4" => "General Purpose Register 4 (Function argument)",
            "r5" => "General Purpose Register 5 (Function argument)",
            "r6" => "General Purpose Register 6 (Function argument)",
            "r7" => "General Purpose Register 7 (Function argument)",
            "r8" => "General Purpose Register 8 (Function argument)",
            "r9" => "General Purpose Register 9 (Function argument)",
            "r10" => "General Purpose Register 10 (Function argument)",
            "r11" => "General Purpose Register 11 (Function argument)",
            "r12" => "General Purpose Register 12 (Function argument)",
            string s when s.StartsWith("r") && int.TryParse(s.Substring(1), out var num) && num >= 13 && num <= 31 =>
                $"General Purpose Register {num} (Local variables/temporaries)",

            // Special Purpose Registers
            "LR" => "Link Register (Return address for function calls)",
            "CTR" => "Count Register (Loop counter/branch target)",
            "XER" => "Fixed Point Exception Register (Carry/overflow flags)",
            "CR" => "Condition Register (Comparison results/flags)",
            "MSR" => "Machine State Register (Processor privilege/state)",
            "SRR0" => "Save/Restore Register 0 (Exception return address)",
            "SRR1" => "Save/Restore Register 1 (Exception MSR save)",
            "PC" => "Program Counter (Current instruction address)",

            // Floating Point Registers
            string s when s.StartsWith("FPR") =>
                $"Floating Point Register {s.Substring(3)} (Double precision floating point)",

            _ => "Unknown register"
        };
    }
}
