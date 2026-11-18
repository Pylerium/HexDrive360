using System.Text;

namespace MemoryEditor;

/// <summary>
/// Generates standalone GUI applications from selected offsets
/// with pre-made professional templates
/// </summary>
public class GUIBuilder
{
    public enum GUITemplate
    {
        ModernDark,
        GamingRGB,
        CleanWhite,
        CyberpunkNeon,
        MinimalFlat,
        MatrixGreen,
        RetroArcade,
        ProfessionalBlue
    }
    
    public class BuildOptions
    {
        public string ProjectName { get; set; } = "CustomModTool";
        public GUITemplate Template { get; set; } = GUITemplate.ModernDark;
        public string XboxIP { get; set; } = "192.168.1.14";
        public int XboxPort { get; set; } = 730;
        public List<OffsetManager.OffsetEntry> Offsets { get; set; } = new();
        public bool IncludeScriptEngine { get; set; } = true;
        public bool IncludeAutoConnect { get; set; } = true;
        public bool IncludePresets { get; set; } = true;
        public string OutputDirectory { get; set; } = "";
    }
    
    /// <summary>
    /// Generates a complete standalone GUI project
    /// </summary>
    public async Task<bool> GenerateProject(BuildOptions options)
    {
        try
        {
            var projectDir = Path.Combine(options.OutputDirectory, options.ProjectName);
            Directory.CreateDirectory(projectDir);
            
            // Generate project file
            await GenerateProjectFile(projectDir, options.ProjectName);
            
            // Copy XBDMClient with correct namespace
            await GenerateJRPC2Client(projectDir, options.ProjectName);
            
            // Generate main form
            await GenerateMainForm(projectDir, options);
            
            // Generate Program.cs
            await GenerateProgramFile(projectDir, options.ProjectName);
            
            // Generate README
            await GenerateReadme(projectDir, options);
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating project: {ex.Message}");
            return false;
        }
    }
    
    private async Task GenerateProjectFile(string projectDir, string projectName)
    {
        var csproj = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationIcon>icon.ico</ApplicationIcon>
    <AssemblyName>{projectName}</AssemblyName>
  </PropertyGroup>
</Project>";
        
        await File.WriteAllTextAsync(Path.Combine(projectDir, $"{projectName}.csproj"), csproj);
    }
    
    private async Task GenerateJRPC2Client(string projectDir, string projectNamespace)
    {
        // Copy the actual XBDMClient.cs file from the main application
        var sourceFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "XBDMClient.cs");
        
        // If not in bin directory, look in the project root
        if (!File.Exists(sourceFile))
        {
            var projectRoot = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)?.Parent?.Parent?.Parent?.FullName;
            if (projectRoot != null)
            {
                sourceFile = Path.Combine(projectRoot, "XBDMClient.cs");
            }
        }
        
        if (File.Exists(sourceFile))
        {
            // Read the file and replace the namespace
            var content = await File.ReadAllTextAsync(sourceFile);
            content = content.Replace("namespace MemoryEditor;", $"namespace {projectNamespace};");
            await File.WriteAllTextAsync(Path.Combine(projectDir, "XBDMClient.cs"), content);
        }
        else
        {
            throw new FileNotFoundException("XBDMClient.cs not found. Make sure it exists in the project root.");
        }
    }
    
    private async Task GenerateMainForm(string projectDir, BuildOptions options)
    {
        var template = options.Template;
        var colors = GetTemplateColors(template);
        
        var sb = new StringBuilder();
        sb.AppendLine("using System.Text;");
        sb.AppendLine();
        sb.AppendLine($"namespace {options.ProjectName};");
        sb.AppendLine();
        sb.AppendLine("public partial class MainForm : Form");
        sb.AppendLine("{");
        sb.AppendLine("    private XBDMClient? _xbdmClient;");
        sb.AppendLine("    private TextBox txtIpAddress = null!;");
        sb.AppendLine("    private Button btnConnect = null!;");
        sb.AppendLine("    private Label lblStatus = null!;");
        sb.AppendLine("    private RichTextBox txtConsole = null!;");
        sb.AppendLine();
        
        // Generate offset fields
        sb.AppendLine("    // Offset Controls");
        foreach (var offset in options.Offsets)
        {
            var sanitizedName = SanitizeName(offset.Name);
            sb.AppendLine($"    private Button btn{sanitizedName} = null!;");
            if (string.IsNullOrEmpty(offset.ByteValues))
            {
                sb.AppendLine($"    private TextBox txt{sanitizedName}Value = null!;");
            }
        }
        sb.AppendLine();
        
        sb.AppendLine("    public MainForm()");
        sb.AppendLine("    {");
        sb.AppendLine("        InitializeComponent();");
        if (options.IncludeAutoConnect)
        {
            sb.AppendLine("        _ = AutoConnectAsync();");
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        
        // Generate InitializeComponent based on template
        sb.AppendLine("    private void InitializeComponent()");
        sb.AppendLine("    {");
        sb.AppendLine($"        this.Text = \"{options.ProjectName} - Xbox 360 Mod Tool\";");
        sb.AppendLine("        this.Size = new Size(1200, 800);");
        sb.AppendLine("        this.StartPosition = FormStartPosition.CenterScreen;");
        sb.AppendLine($"        this.BackColor = Color.FromArgb({colors.BackgroundColor});");
        sb.AppendLine($"        this.ForeColor = Color.FromArgb({colors.ForegroundColor});");
        sb.AppendLine("        this.MinimumSize = new Size(1000, 700);");
        sb.AppendLine();
        
        // Connection panel
        GenerateConnectionPanel(sb, colors);
        
        // Mod panel
        GenerateModPanel(sb, options, colors);
        
        // Console panel
        GenerateConsolePanel(sb, colors);
        
        sb.AppendLine("    }");
        sb.AppendLine();
        
        // Generate connection methods
        GenerateConnectionMethods(sb, options);
        
        // Generate mod button handlers
        GenerateModHandlers(sb, options);
        
        // Generate utility methods
        GenerateUtilityMethods(sb, colors);
        
        sb.AppendLine("}");
        
        await File.WriteAllTextAsync(Path.Combine(projectDir, "MainForm.cs"), sb.ToString());
    }
    
    private void GenerateConnectionPanel(StringBuilder sb, TemplateColors colors)
    {
        sb.AppendLine("        // Connection Panel");
        sb.AppendLine("        var connectionPanel = new Panel");
        sb.AppendLine("        {");
        sb.AppendLine("            Dock = DockStyle.Top,");
        sb.AppendLine("            Height = 80,");
        sb.AppendLine($"            BackColor = Color.FromArgb({colors.PanelColor}),");
        sb.AppendLine("            Padding = new Padding(15)");
        sb.AppendLine("        };");
        sb.AppendLine();
        sb.AppendLine("        var lblIp = new Label { Text = \"Xbox IP:\", Location = new Point(20, 25), AutoSize = true };");
        sb.AppendLine($"        txtIpAddress = new TextBox {{ Location = new Point(100, 22), Size = new Size(150, 25), Text = \"{colors.DefaultIP}\", BackColor = Color.FromArgb({colors.InputColor}) }};");
        sb.AppendLine();
        sb.AppendLine($"        btnConnect = new Button {{ Text = \"🔌 Connect\", Location = new Point(270, 20), Size = new Size(120, 35), BackColor = Color.FromArgb({colors.ButtonColor}), ForeColor = Color.White, FlatStyle = FlatStyle.Flat }};");
        sb.AppendLine("        btnConnect.FlatAppearance.BorderSize = 0;");
        sb.AppendLine("        btnConnect.Click += async (s, e) => await ConnectAsync();");
        sb.AppendLine();
        sb.AppendLine($"        lblStatus = new Label {{ Text = \"⚫ Not Connected\", Location = new Point(410, 25), AutoSize = true, Font = new Font(\"Segoe UI\", 10F, FontStyle.Bold) }};");
        sb.AppendLine();
        sb.AppendLine("        connectionPanel.Controls.AddRange(new Control[] { lblIp, txtIpAddress, btnConnect, lblStatus });");
        sb.AppendLine("        this.Controls.Add(connectionPanel);");
        sb.AppendLine();
    }
    
    private void GenerateModPanel(StringBuilder sb, BuildOptions options, TemplateColors colors)
    {
        sb.AppendLine("        // Mod Panel");
        sb.AppendLine("        var modPanel = new Panel");
        sb.AppendLine("        {");
        sb.AppendLine("            Dock = DockStyle.Fill,");
        sb.AppendLine($"            BackColor = Color.FromArgb({colors.BackgroundColor}),");
        sb.AppendLine("            Padding = new Padding(15),");
        sb.AppendLine("            AutoScroll = true");
        sb.AppendLine("        };");
        sb.AppendLine();
        
        int yPos = 20;
        var categories = options.Offsets.GroupBy(o => o.Category).ToList();
        
        foreach (var categoryGroup in categories)
        {
            sb.AppendLine($"        // Category: {categoryGroup.Key}");
            sb.AppendLine($"        var lbl{SanitizeName(categoryGroup.Key)} = new Label");
            sb.AppendLine("        {");
            sb.AppendLine($"            Text = \"━━━ {categoryGroup.Key} ━━━\",");
            sb.AppendLine($"            Location = new Point(20, {yPos}),");
            sb.AppendLine("            AutoSize = true,");
            sb.AppendLine("            Font = new Font(\"Segoe UI\", 12F, FontStyle.Bold),");
            sb.AppendLine($"            ForeColor = Color.FromArgb({colors.AccentColor})");
            sb.AppendLine("        };");
            sb.AppendLine($"        modPanel.Controls.Add(lbl{SanitizeName(categoryGroup.Key)});");
            yPos += 40;
            sb.AppendLine();
            
            foreach (var offset in categoryGroup)
            {
                var sanitizedName = SanitizeName(offset.Name);
                
                if (string.IsNullOrEmpty(offset.ByteValues))
                {
                    // Input + Button layout
                    sb.AppendLine($"        txt{sanitizedName}Value = new TextBox");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            Location = new Point(20, {yPos}),");
                    sb.AppendLine("            Size = new Size(120, 30),");
                    sb.AppendLine("            Font = new Font(\"Consolas\", 10F),");
                    sb.AppendLine($"            BackColor = Color.FromArgb({colors.InputColor}),");
                    sb.AppendLine("            Text = \"9999\"");
                    sb.AppendLine("        };");
                    sb.AppendLine($"        modPanel.Controls.Add(txt{sanitizedName}Value);");
                    sb.AppendLine();
                    
                    sb.AppendLine($"        btn{sanitizedName} = new Button");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            Text = \"{offset.Name}\",");
                    sb.AppendLine($"            Location = new Point(150, {yPos}),");
                    sb.AppendLine("            Size = new Size(300, 35),");
                    sb.AppendLine($"            BackColor = Color.FromArgb({colors.ButtonColor}),");
                    sb.AppendLine("            ForeColor = Color.White,");
                    sb.AppendLine("            FlatStyle = FlatStyle.Flat,");
                    sb.AppendLine("            Font = new Font(\"Segoe UI\", 9.5F)");
                    sb.AppendLine("        };");
                }
                else
                {
                    // Button only layout
                    sb.AppendLine($"        btn{sanitizedName} = new Button");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            Text = \"✨ {offset.Name}\",");
                    sb.AppendLine($"            Location = new Point(20, {yPos}),");
                    sb.AppendLine("            Size = new Size(430, 35),");
                    sb.AppendLine($"            BackColor = Color.FromArgb({colors.AccentButtonColor}),");
                    sb.AppendLine("            ForeColor = Color.White,");
                    sb.AppendLine("            FlatStyle = FlatStyle.Flat,");
                    sb.AppendLine("            Font = new Font(\"Segoe UI\", 9.5F, FontStyle.Bold)");
                    sb.AppendLine("        };");
                }
                
                sb.AppendLine($"        btn{sanitizedName}.FlatAppearance.BorderSize = 0;");
                sb.AppendLine($"        btn{sanitizedName}.Click += async (s, e) => await Apply{sanitizedName}();");
                sb.AppendLine($"        modPanel.Controls.Add(btn{sanitizedName});");
                sb.AppendLine();
                
                yPos += 45;
            }
            
            yPos += 20;
        }
        
        sb.AppendLine("        this.Controls.Add(modPanel);");
        sb.AppendLine();
    }
    
    private void GenerateConsolePanel(StringBuilder sb, TemplateColors colors)
    {
        sb.AppendLine("        // Console Panel");
        sb.AppendLine("        var consolePanel = new Panel");
        sb.AppendLine("        {");
        sb.AppendLine("            Dock = DockStyle.Bottom,");
        sb.AppendLine("            Height = 200,");
        sb.AppendLine($"            BackColor = Color.FromArgb({colors.PanelColor})");
        sb.AppendLine("        };");
        sb.AppendLine();
        sb.AppendLine("        txtConsole = new RichTextBox");
        sb.AppendLine("        {");
        sb.AppendLine("            Dock = DockStyle.Fill,");
        sb.AppendLine($"            BackColor = Color.FromArgb({colors.ConsoleColor}),");
        sb.AppendLine($"            ForeColor = Color.FromArgb({colors.ConsoleForeColor}),");
        sb.AppendLine("            Font = new Font(\"Consolas\", 9F),");
        sb.AppendLine("            ReadOnly = true,");
        sb.AppendLine("            BorderStyle = BorderStyle.None");
        sb.AppendLine("        };");
        sb.AppendLine();
        sb.AppendLine("        consolePanel.Controls.Add(txtConsole);");
        sb.AppendLine("        this.Controls.Add(consolePanel);");
        sb.AppendLine();
    }
    
    private void GenerateConnectionMethods(StringBuilder sb, BuildOptions options)
    {
        sb.AppendLine("    private async Task ConnectAsync()");
        sb.AppendLine("    {");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            LogMessage(\"🔄 Connecting to Xbox...\");");
        sb.AppendLine($"            _xbdmClient = new XBDMClient(txtIpAddress.Text, {options.XboxPort});");
        sb.AppendLine($"            await _xbdmClient.ConnectAsync();");;
        sb.AppendLine("            lblStatus.Text = \"✅ Connected\";");
        sb.AppendLine("            lblStatus.ForeColor = Color.LimeGreen;");
        sb.AppendLine("            LogMessage(\"✅ Connected successfully!\");");
        sb.AppendLine("        }");
        sb.AppendLine("        catch (Exception ex)");
        sb.AppendLine("        {");
        sb.AppendLine("            lblStatus.Text = \"❌ Failed\";");
        sb.AppendLine("            lblStatus.ForeColor = Color.Red;");
        sb.AppendLine("            LogMessage($\"❌ Connection failed: {ex.Message}\");");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        
        if (options.IncludeAutoConnect)
        {
            sb.AppendLine("    private async Task AutoConnectAsync()");
            sb.AppendLine("    {");
            sb.AppendLine("        await Task.Delay(1000);");
            sb.AppendLine("        await ConnectAsync();");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
    }
    
    private void GenerateModHandlers(StringBuilder sb, BuildOptions options)
    {
        foreach (var offset in options.Offsets)
        {
            var sanitizedName = SanitizeName(offset.Name);
            
            sb.AppendLine($"    private async Task Apply{sanitizedName}()");
            sb.AppendLine("    {");
            sb.AppendLine("        if (_xbdmClient == null || !_xbdmClient.IsConnected)");
            sb.AppendLine("        {");
            sb.AppendLine($"            LogMessage(\"❌ Not connected to Xbox\");");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            
            if (!string.IsNullOrEmpty(offset.ByteValues))
            {
                // Fixed bytes
                sb.AppendLine($"            LogMessage(\"🔧 Applying {offset.Name}...\");");
                sb.AppendLine($"            var bytes = HexStringToBytes(\"{offset.ByteValues.Replace(" ", "")}\");");
                sb.AppendLine($"            await _xbdmClient.SetMemoryAsync(0x{offset.BaseAddress:X8}, bytes);");
                sb.AppendLine($"            LogMessage(\"✅ {offset.Name} applied!\");");
            }
            else
            {
                // Value input
                sb.AppendLine($"            var value = int.Parse(txt{sanitizedName}Value.Text);");
                sb.AppendLine($"            LogMessage($\"🔧 Applying {{value}} to {offset.Name}...\");");
                
                switch (offset.Type)
                {
                    case "Float":
                        sb.AppendLine($"            var floatValue = float.Parse(txt{sanitizedName}Value.Text);");
                        sb.AppendLine($"            await _xbdmClient.PokeFloatAsync(0x{offset.BaseAddress:X8}, floatValue);");
                        break;
                    case "Byte":
                        sb.AppendLine($"            var byteValue = byte.Parse(txt{sanitizedName}Value.Text);");
                        sb.AppendLine($"            await _xbdmClient.SetMemoryAsync(0x{offset.BaseAddress:X8}, new[] {{ byteValue }});");
                        break;
                    default:
                        sb.AppendLine($"            await _xbdmClient.PokeInt32Async(0x{offset.BaseAddress:X8}, value);");
                        break;
                }
                
                sb.AppendLine($"            LogMessage(\"✅ {offset.Name} applied!\");");
            }
            
            sb.AppendLine("        }");
            sb.AppendLine("        catch (Exception ex)");
            sb.AppendLine("        {");
            sb.AppendLine($"            LogMessage($\"❌ Error applying {offset.Name}: {{ex.Message}}\");");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
    }
    
    private void GenerateUtilityMethods(StringBuilder sb, TemplateColors colors)
    {
        sb.AppendLine("    private void LogMessage(string message)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (txtConsole.InvokeRequired)");
        sb.AppendLine("        {");
        sb.AppendLine("            txtConsole.Invoke(() => LogMessage(message));");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        var timestamp = DateTime.Now.ToString(\"HH:mm:ss\");");
        sb.AppendLine("        txtConsole.AppendText($\"[{timestamp}] {message}\\n\");");
        sb.AppendLine("        txtConsole.ScrollToCaret();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private byte[] HexStringToBytes(string hex)");
        sb.AppendLine("    {");
        sb.AppendLine("        hex = hex.Replace(\" \", \"\").Replace(\",\", \"\");");
        sb.AppendLine("        var bytes = new byte[hex.Length / 2];");
        sb.AppendLine("        for (int i = 0; i < bytes.Length; i++)");
        sb.AppendLine("        {");
        sb.AppendLine("            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);");
        sb.AppendLine("        }");
        sb.AppendLine("        return bytes;");
        sb.AppendLine("    }");
    }
    
    private async Task GenerateProgramFile(string projectDir, string projectName)
    {
        var program = $@"namespace {projectName};

static class Program
{{
    [STAThread]
    static void Main()
    {{
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }}
}}";
        
        await File.WriteAllTextAsync(Path.Combine(projectDir, "Program.cs"), program);
    }
    
    private async Task GenerateReadme(string projectDir, BuildOptions options)
    {
        var readme = $@"# {options.ProjectName}

Auto-generated Xbox 360 mod tool built with Memory Editor GUI Builder.

## Features
- {options.Offsets.Count} pre-configured mods
- XBDM/JRPC2 protocol support
- Clean, modern interface
- One-click mod application

## Usage
1. Launch the application
2. It will auto-connect to {options.XboxIP}:730
3. Click any mod button to apply
4. View console log for status

## Requirements
- .NET 8.0 Runtime
- Xbox 360 with XBDM enabled
- Black Ops 2 TU18

## Template: {options.Template}

Generated on {DateTime.Now:yyyy-MM-dd HH:mm:ss}
";
        
        await File.WriteAllTextAsync(Path.Combine(projectDir, "README.md"), readme);
    }
    
    private string SanitizeName(string name)
    {
        return new string(name.Where(c => char.IsLetterOrDigit(c)).ToArray());
    }
    
    private TemplateColors GetTemplateColors(GUITemplate template)
    {
        return template switch
        {
            GUITemplate.ModernDark => new TemplateColors
            {
                BackgroundColor = "20, 20, 25",
                ForegroundColor = "220, 220, 220",
                PanelColor = "30, 30, 35",
                ButtonColor = "70, 130, 255",
                AccentButtonColor = "255, 90, 130",
                AccentColor = "100, 180, 255",
                InputColor = "40, 40, 45",
                ConsoleColor = "15, 15, 20",
                ConsoleForeColor = "0, 255, 100",
                DefaultIP = "192.168.1.14"
            },
            GUITemplate.GamingRGB => new TemplateColors
            {
                BackgroundColor = "18, 18, 20",
                ForegroundColor = "255, 255, 255",
                PanelColor = "25, 25, 28",
                ButtonColor = "255, 0, 150",
                AccentButtonColor = "0, 255, 200",
                AccentColor = "255, 50, 255",
                InputColor = "30, 30, 35",
                ConsoleColor = "10, 10, 15",
                ConsoleForeColor = "0, 255, 255",
                DefaultIP = "192.168.1.14"
            },
            GUITemplate.CleanWhite => new TemplateColors
            {
                BackgroundColor = "250, 250, 252",
                ForegroundColor = "30, 30, 30",
                PanelColor = "240, 240, 245",
                ButtonColor = "0, 120, 215",
                AccentButtonColor = "255, 140, 0",
                AccentColor = "0, 90, 180",
                InputColor = "255, 255, 255",
                ConsoleColor = "245, 245, 248",
                ConsoleForeColor = "60, 60, 60",
                DefaultIP = "192.168.1.14"
            },
            GUITemplate.CyberpunkNeon => new TemplateColors
            {
                BackgroundColor = "10, 10, 15",
                ForegroundColor = "0, 255, 255",
                PanelColor = "15, 15, 22",
                ButtonColor = "255, 0, 255",
                AccentButtonColor = "0, 255, 255",
                AccentColor = "255, 255, 0",
                InputColor = "20, 20, 30",
                ConsoleColor = "5, 5, 10",
                ConsoleForeColor = "0, 255, 255",
                DefaultIP = "192.168.1.14"
            },
            GUITemplate.MinimalFlat => new TemplateColors
            {
                BackgroundColor = "245, 245, 245",
                ForegroundColor = "50, 50, 50",
                PanelColor = "235, 235, 235",
                ButtonColor = "100, 100, 100",
                AccentButtonColor = "70, 70, 70",
                AccentColor = "80, 80, 80",
                InputColor = "255, 255, 255",
                ConsoleColor = "240, 240, 240",
                ConsoleForeColor = "70, 70, 70",
                DefaultIP = "192.168.1.14"
            },
            GUITemplate.MatrixGreen => new TemplateColors
            {
                BackgroundColor = "0, 0, 0",
                ForegroundColor = "0, 255, 0",
                PanelColor = "5, 10, 5",
                ButtonColor = "0, 180, 0",
                AccentButtonColor = "0, 255, 100",
                AccentColor = "0, 255, 0",
                InputColor = "10, 20, 10",
                ConsoleColor = "0, 0, 0",
                ConsoleForeColor = "0, 255, 0",
                DefaultIP = "192.168.1.14"
            },
            GUITemplate.RetroArcade => new TemplateColors
            {
                BackgroundColor = "30, 0, 50",
                ForegroundColor = "255, 200, 100",
                PanelColor = "40, 0, 60",
                ButtonColor = "255, 100, 0",
                AccentButtonColor = "255, 0, 255",
                AccentColor = "255, 255, 0",
                InputColor = "50, 10, 70",
                ConsoleColor = "20, 0, 40",
                ConsoleForeColor = "255, 200, 100",
                DefaultIP = "192.168.1.14"
            },
            GUITemplate.ProfessionalBlue => new TemplateColors
            {
                BackgroundColor = "240, 244, 248",
                ForegroundColor = "20, 40, 60",
                PanelColor = "230, 236, 242",
                ButtonColor = "30, 90, 160",
                AccentButtonColor = "20, 120, 200",
                AccentColor = "10, 80, 140",
                InputColor = "255, 255, 255",
                ConsoleColor = "235, 240, 245",
                ConsoleForeColor = "40, 60, 80",
                DefaultIP = "192.168.1.14"
            },
            _ => throw new ArgumentException("Unknown template")
        };
    }
    
    private class TemplateColors
    {
        public string BackgroundColor { get; set; } = "";
        public string ForegroundColor { get; set; } = "";
        public string PanelColor { get; set; } = "";
        public string ButtonColor { get; set; } = "";
        public string AccentButtonColor { get; set; } = "";
        public string AccentColor { get; set; } = "";
        public string InputColor { get; set; } = "";
        public string ConsoleColor { get; set; } = "";
        public string ConsoleForeColor { get; set; } = "";
        public string DefaultIP { get; set; } = "";
    }
}
