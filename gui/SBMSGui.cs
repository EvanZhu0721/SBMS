using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace SBMSGui
{
    internal sealed class MainForm : Form
    {
        private const int WM_CLOSE = 0x0010;
        private const uint EVENT_MODIFY_STATE = 0x0002;
        private const int NativeTopologyChangedExitCode = 100;
        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int DISP_CHANGE_SUCCESSFUL = 0;
        private const int DM_PELSWIDTH = 0x00080000;
        private const int DM_PELSHEIGHT = 0x00100000;
        private const int DM_DISPLAYFREQUENCY = 0x00400000;
        private const int DM_DISPLAYORIENTATION = 0x00000080;
        private const int DMDO_DEFAULT = 0;
        private const int DMDO_90 = 1;
        private const int DMDO_180 = 2;
        private const int DMDO_270 = 3;
        private const string AppName = "SBMS";
        private const string AppLongName = "SBMS - bridges multiple screens";
        private const string BuildLabel = "2026-06-27.022-beta";
        private const int MultiScreenBetaMaxTargets = 3;
        private const int BetaColEnabled = 0;
        private const int BetaColTarget = 1;
        private const int BetaColHorizontal = 2;
        private const int BetaColAspect = 3;
        private const int BetaColOrientation = 4;
        private const int BetaColSize = 5;
        private const int BetaColStrategy = 6;
        private const int BetaColSource = 7;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenEvent(uint desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr lParam);

        private readonly TextBox sourceText = new TextBox();
        private readonly TextBox targetText = new TextBox();
        private readonly ComboBox sourceDisplayCombo = new ComboBox();
        private readonly ComboBox targetDisplayCombo = new ComboBox();
        private readonly ListBox displayList = new ListBox();
        private readonly ComboBox strategyCombo = new ComboBox();
        private readonly ComboBox primaryResolutionPresetCombo = new ComboBox();
        private readonly ComboBox primaryAspectPresetCombo = new ComboBox();
        private readonly ComboBox primaryOrientationPresetCombo = new ComboBox();
        private readonly ComboBox primarySizePresetCombo = new ComboBox();
        private readonly ComboBox targetResolutionPresetCombo = new ComboBox();
        private readonly ComboBox targetAspectPresetCombo = new ComboBox();
        private readonly ComboBox targetOrientationPresetCombo = new ComboBox();
        private readonly ComboBox targetSizePresetCombo = new ComboBox();
        private readonly TextBox primaryResolutionText = new TextBox();
        private readonly TextBox primarySizeText = new TextBox();
        private readonly TextBox targetResolutionText = new TextBox();
        private readonly TextBox targetSizeText = new TextBox();
        private readonly TabControl configInputTabs = new TabControl();
        private readonly TabPage presetConfigPage = new TabPage();
        private readonly TabPage manualConfigPage = new TabPage();
        private readonly TextBox manualBaseHorizontalText = new TextBox();
        private readonly TextBox manualBaseAspectText = new TextBox();
        private readonly ComboBox manualBaseOrientationCombo = new ComboBox();
        private readonly TextBox manualBaseSizeText = new TextBox();
        private readonly TextBox manualTargetHorizontalText = new TextBox();
        private readonly TextBox manualTargetAspectText = new TextBox();
        private readonly ComboBox manualTargetOrientationCombo = new ComboBox();
        private readonly TextBox manualTargetSizeText = new TextBox();
        private readonly ComboBox filterCombo = new ComboBox();
        private readonly CheckBox inputCheck = new CheckBox();
        private readonly CheckBox windowMoveCheck = new CheckBox();
        private readonly CheckBox deviceHostCheck = new CheckBox();
        private readonly CheckBox streamModeCheck = new CheckBox();
        private readonly Label streamModeWarningLabel = new Label();
        private readonly CheckBox multiScreenBetaCheck = new CheckBox();
        private readonly DataGridView betaPairGrid = new DataGridView();
        private readonly Button addBetaGroupButton = new Button();
        private readonly Button removeBetaGroupButton = new Button();
        private readonly CheckBox vsyncCheck = new CheckBox();
        private readonly Button calculateButton = new Button();
        private readonly Button applyConfigButton = new Button();
        private readonly Button startButton = new Button();
        private readonly Button stopButton = new Button();
        private readonly Button listButton = new Button();
        private readonly TextBox logText = new TextBox();
        private readonly MenuStrip menu = new MenuStrip();
        private readonly ToolStripMenuItem settingsMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem configMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem startupMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem lightweightMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem languageMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem chineseMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem englishMenuItem = new ToolStripMenuItem();
        private readonly NotifyIcon trayIcon = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem trayOpenMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem trayStopMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem trayExitMenuItem = new ToolStripMenuItem();
        private readonly Label statusLabel = new Label();
        private readonly Label routeLabel = new Label();
        private readonly Panel configLockPanel = new Panel();
        private readonly Label configLockLabel = new Label();

        private Process process;
        private readonly List<Process> betaProcesses = new List<Process>();
        private Process deviceHostProcess;
        private readonly StringBuilder deviceHostLog = new StringBuilder();
        private string lastNativeArgs = "";
        private bool stoppingRequested;
        private int nativeRestartCount;
        private readonly string root;
        private readonly string nativeExe;
        private readonly string deviceHostExe;
        private readonly List<DisplayChoice> displays = new List<DisplayChoice>();
        private Form configForm;
        private bool english;
        private bool exiting;
        private bool updatingPresetCombos;
        private bool updatingConfigurationInputs;
        private bool updatingBetaPairGrid;

        private static readonly Color ThemeBack = Color.FromArgb(6, 12, 8);
        private static readonly Color ThemePanel = Color.FromArgb(10, 22, 14);
        private static readonly Color ThemePanel2 = Color.FromArgb(14, 32, 20);
        private static readonly Color ThemeGreen = Color.FromArgb(190, 255, 210);
        private static readonly Color ThemeMuted = Color.FromArgb(230, 255, 235);
        private static readonly Color ThemeRed = Color.FromArgb(255, 70, 70);

        private struct Resolution
        {
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        private sealed class DisplayChoice
        {
            public int Number;
            public string DeviceName;
            public string Resolution;
            public string Refresh;
            public string Name;
            public bool Primary;
            public bool Virtual;

            public override string ToString()
            {
                return Number + "  " + DeviceName + "  " + Resolution + "@" + Refresh +
                       (Primary ? "  基准" : "") +
                       (Virtual ? "  虚拟" : "") +
                       "  " + Name;
            }
        }

        private sealed class BridgePairConfig
        {
            public DisplayChoice TargetDisplay;
            public Resolution TargetResolution;
            public Resolution SourceResolution;
            public int Orientation;
            public int StrategyIndex;
            public double TargetSize;
        }

        private sealed class BridgePairSnapshot
        {
            public bool Enabled;
            public string Target;
            public string Horizontal;
            public string Aspect;
            public string Orientation;
            public string Size;
            public string Strategy;
            public string Source;
        }

        public MainForm()
        {
            root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory));
            nativeExe = Path.Combine(root, "SBMSNative.exe");
            deviceHostExe = Path.Combine(root, "SBMSDeviceHost.exe");

            Text = AppName + " " + BuildLabel;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(980, 720);
            MinimumSize = new Size(780, 540);
            Font = new Font("Segoe UI", 9F);
            BackColor = ThemeBack;

            sourceText.Text = "4550x2560";
            sourceText.ReadOnly = true;
            targetText.Text = "2560x1440";
            targetText.ReadOnly = true;
            sourceDisplayCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            targetDisplayCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            primaryResolutionText.Text = "5120x2880";
            primarySizeText.Text = "27";
            targetResolutionText.Text = "2560x1440";
            targetSizeText.Text = "24";
            manualBaseHorizontalText.Text = "5120";
            manualBaseAspectText.Text = "16:9";
            manualBaseSizeText.Text = "27";
            manualTargetHorizontalText.Text = "2560";
            manualTargetAspectText.Text = "16:9";
            manualTargetSizeText.Text = "24";
            strategyCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            strategyCombo.Items.AddRange(new object[] { "真实尺寸比例", "文字清晰优先", "直接使用源" });
            strategyCombo.SelectedIndex = 0;
            ConfigurePresetCombos();
            ConfigureManualOrientationCombos();
            primaryResolutionPresetCombo.SelectedIndex = 3;
            primaryAspectPresetCombo.SelectedIndex = 0;
            primaryOrientationPresetCombo.SelectedIndex = 0;
            primarySizePresetCombo.SelectedIndex = 7;
            targetResolutionPresetCombo.SelectedIndex = 1;
            targetAspectPresetCombo.SelectedIndex = 0;
            targetOrientationPresetCombo.SelectedIndex = 0;
            targetSizePresetCombo.SelectedIndex = 6;
            manualBaseOrientationCombo.SelectedIndex = 0;
            manualTargetOrientationCombo.SelectedIndex = 0;
            SyncConfigurationInputsFromMode(false);
            filterCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            filterCombo.Items.AddRange(new object[] { "自动", "线性平滑", "锐利最近邻", "整数2x" });
            filterCombo.SelectedIndex = 0;
            inputCheck.Text = "鼠标映射";
            inputCheck.Checked = true;
            windowMoveCheck.Text = "迁移窗口";
            windowMoveCheck.Checked = true;
            deviceHostCheck.Text = "管理虚拟显示器";
            deviceHostCheck.Checked = true;
            streamModeCheck.Text = "串流模式";
            streamModeCheck.AutoSize = true;
            streamModeWarningLabel.Text = "如果不清楚这个选项的作用，请不要勾选";
            streamModeWarningLabel.Tag = "如果不清楚这个选项的作用，请不要勾选";
            streamModeWarningLabel.Name = "streamModeWarningLabel";
            streamModeWarningLabel.AutoSize = true;
            streamModeWarningLabel.TextAlign = ContentAlignment.MiddleLeft;
            streamModeWarningLabel.Padding = new Padding(4, 4, 0, 0);
            multiScreenBetaCheck.Text = "多屏 BETA";
            multiScreenBetaCheck.AutoSize = true;
            ConfigureBetaPairGrid();
            addBetaGroupButton.Text = "⊕ 新增组 β";
            addBetaGroupButton.Width = 112;
            removeBetaGroupButton.Text = "删除组";
            removeBetaGroupButton.Width = 90;
            vsyncCheck.Text = "VSync";
            calculateButton.Text = "计算";
            calculateButton.Width = 90;
            applyConfigButton.Text = "应用";
            applyConfigButton.Width = 100;

            BuildMainUi();
            BuildConfigForm();
            ApplyTheme(configForm);
            calculateButton.Click += delegate { ApplyStrategy(true); };
            applyConfigButton.Click += delegate { ApplyConfigurationChanges(); };
            strategyCombo.SelectedIndexChanged += delegate { ApplyStrategy(false); };
            configInputTabs.SelectedIndexChanged += delegate { SyncConfigurationInputsFromMode(true); };
            primaryResolutionPresetCombo.SelectedIndexChanged += delegate { ApplyPresetSelections(true); };
            primaryAspectPresetCombo.SelectedIndexChanged += delegate { ApplyPresetSelections(true); };
            primaryOrientationPresetCombo.SelectedIndexChanged += delegate { ApplyPresetSelections(true); };
            primarySizePresetCombo.SelectedIndexChanged += delegate { ApplyPresetSelections(true); };
            targetResolutionPresetCombo.SelectedIndexChanged += delegate { ApplyPresetSelections(true); };
            targetAspectPresetCombo.SelectedIndexChanged += delegate { ApplyPresetSelections(true); };
            targetOrientationPresetCombo.SelectedIndexChanged += delegate { ApplyPresetSelections(true); };
            targetSizePresetCombo.SelectedIndexChanged += delegate { ApplyPresetSelections(true); };
            manualBaseHorizontalText.TextChanged += delegate { ApplyManualSelections(true); };
            manualBaseAspectText.TextChanged += delegate { ApplyManualSelections(true); };
            manualBaseOrientationCombo.SelectedIndexChanged += delegate { ApplyManualSelections(true); };
            manualBaseSizeText.TextChanged += delegate { ApplyManualSelections(true); };
            manualTargetHorizontalText.TextChanged += delegate { ApplyManualSelections(true); };
            manualTargetAspectText.TextChanged += delegate { ApplyManualSelections(true); };
            manualTargetOrientationCombo.SelectedIndexChanged += delegate { ApplyManualSelections(true); };
            manualTargetSizeText.TextChanged += delegate { ApplyManualSelections(true); };
            targetResolutionText.TextChanged += delegate { SyncTargetSelector(); };
            sourceDisplayCombo.SelectedIndexChanged += delegate { SyncSelectedDisplaysToSelectors(); };
            targetDisplayCombo.SelectedIndexChanged += delegate { SyncSelectedDisplaysToSelectors(); };
            streamModeCheck.CheckedChanged += delegate { OnStreamModeChanged(); };
            multiScreenBetaCheck.CheckedChanged += delegate { OnMultiScreenBetaChanged(); };
            addBetaGroupButton.Click += delegate { AddBetaGroupRow(null, true); RecalculateBetaPairGrid(false); UpdateRuntimeOptionState(); };
            removeBetaGroupButton.Click += delegate { RemoveSelectedBetaGroup(); RecalculateBetaPairGrid(false); UpdateRuntimeOptionState(); };
            startButton.Click += delegate { StartBridge(); };
            stopButton.Click += delegate { StopBridge(); };
            listButton.Click += delegate { RunList(); };
            FormClosing += OnFormClosing;
            FormClosed += delegate { trayIcon.Dispose(); };
            startupMenuItem.Checked = IsStartupEnabled();
            lightweightMenuItem.Checked = true;
            ApplyStrategy(false);
            RefreshDisplays();
            ApplyLanguage();
            UpdateRuntimeOptionState();
            ApplyTheme(this);
            AppendLog("GUI版本 = " + BuildLabel);
        }

        private void BuildMainUi()
        {
            menu.Dock = DockStyle.Top;
            settingsMenuItem.DropDownItems.Add(configMenuItem);
            settingsMenuItem.DropDownItems.Add(startupMenuItem);
            languageMenuItem.DropDownItems.Add(chineseMenuItem);
            languageMenuItem.DropDownItems.Add(englishMenuItem);
            settingsMenuItem.DropDownItems.Add(languageMenuItem);
            menu.Items.Add(settingsMenuItem);
            menu.Items.Add(lightweightMenuItem);
            Controls.Add(menu);
            MainMenuStrip = menu;

            configMenuItem.Click += delegate { ShowConfigForm(); };
            startupMenuItem.CheckOnClick = true;
            startupMenuItem.Click += delegate { ToggleStartup(); };
            lightweightMenuItem.CheckOnClick = true;
            chineseMenuItem.Click += delegate { english = false; ApplyLanguage(); };
            englishMenuItem.Click += delegate { english = true; ApplyLanguage(); };

            trayMenu.Items.Add(trayOpenMenuItem);
            trayMenu.Items.Add(trayStopMenuItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(trayExitMenuItem);
            trayOpenMenuItem.Click += delegate { ShowMainWindow(); };
            trayStopMenuItem.Click += delegate { StopBridge(); };
            trayExitMenuItem.Click += delegate { ExitApplication(); };
            trayIcon.Icon = SystemIcons.Application;
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = false;
            trayIcon.DoubleClick += delegate { ShowMainWindow(); };

            var main = new TableLayoutPanel();
            main.Dock = DockStyle.Fill;
            main.Padding = new Padding(12, 10, 12, 12);
            main.ColumnCount = 1;
            main.RowCount = 4;
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(main);
            main.BringToFront();
            menu.BringToFront();

            var status = new TableLayoutPanel();
            status.Dock = DockStyle.Fill;
            status.ColumnCount = 2;
            status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            status.Padding = new Padding(0, 0, 0, 6);
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.Font = new Font("Consolas", 10F, FontStyle.Bold);
            routeLabel.Dock = DockStyle.Fill;
            routeLabel.TextAlign = ContentAlignment.MiddleRight;
            routeLabel.Font = new Font("Consolas", 9F);
            status.Controls.Add(statusLabel, 0, 0);
            status.Controls.Add(routeLabel, 1, 0);
            main.Controls.Add(status, 0, 0);

            displayList.Dock = DockStyle.Fill;
            displayList.Font = new Font("Consolas", 9F);
            displayList.BorderStyle = BorderStyle.FixedSingle;
            displayList.HorizontalScrollbar = true;
            main.Controls.Add(displayList, 0, 1);

            logText.Dock = DockStyle.Fill;
            logText.Multiline = true;
            logText.ScrollBars = ScrollBars.Vertical;
            logText.ReadOnly = true;
            logText.Font = new Font("Consolas", 10F);
            logText.BorderStyle = BorderStyle.FixedSingle;
            var logMenu = new ContextMenuStrip();
            logMenu.Items.Add("清空", null, delegate { logText.Clear(); });
            logText.ContextMenuStrip = logMenu;
            main.Controls.Add(logText, 0, 2);

            var buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.Padding = new Padding(0, 8, 0, 0);
            startButton.Width = 110;
            stopButton.Width = 110;
            listButton.Width = 120;
            stopButton.Enabled = false;
            buttons.Controls.Add(startButton);
            buttons.Controls.Add(stopButton);
            buttons.Controls.Add(listButton);
            main.Controls.Add(buttons, 0, 3);
        }

        private void BuildConfigForm()
        {
            configForm = new Form();
            configForm.StartPosition = FormStartPosition.CenterParent;
            configForm.Size = new Size(1120, 760);
            configForm.MinimumSize = new Size(980, 680);
            configForm.ShowInTaskbar = false;
            configForm.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                e.Cancel = true;
                configForm.Hide();
            };

            var configHost = new Panel();
            configHost.Dock = DockStyle.Fill;
            configForm.Controls.Add(configHost);

            var panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.Padding = new Padding(14);
            panel.ColumnCount = 2;
            panel.RowCount = 10;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 214));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            configHost.Controls.Add(panel);

            AddLabel(panel, "虚拟源", 0);
            panel.Controls.Add(sourceDisplayCombo, 1, 0);
            AddLabel(panel, "输出目标", 1);
            panel.Controls.Add(targetDisplayCombo, 1, 1);
            AddLabel(panel, "配置方式", 2);
            ConfigureInputTabs();
            panel.Controls.Add(configInputTabs, 1, 2);
            AddLabel(panel, "尺寸策略", 3);
            panel.Controls.Add(strategyCombo, 1, 3);

            var sourcePanel = new FlowLayoutPanel();
            sourcePanel.Dock = DockStyle.Fill;
            sourcePanel.FlowDirection = FlowDirection.LeftToRight;
            sourceText.Width = 130;
            targetText.Width = 130;
            sourcePanel.Controls.Add(sourceText);
            sourcePanel.Controls.Add(targetText);
            sourcePanel.Controls.Add(calculateButton);
            AddLabel(panel, "映射结果", 4);
            panel.Controls.Add(sourcePanel, 1, 4);

            AddLabel(panel, "缩放滤镜", 5);
            filterCombo.Width = 170;
            panel.Controls.Add(filterCombo, 1, 5);

            var checks = new FlowLayoutPanel();
            checks.Dock = DockStyle.Fill;
            checks.FlowDirection = FlowDirection.LeftToRight;
            checks.Controls.Add(inputCheck);
            checks.Controls.Add(windowMoveCheck);
            checks.Controls.Add(deviceHostCheck);
            checks.Controls.Add(streamModeCheck);
            checks.Controls.Add(multiScreenBetaCheck);
            checks.Controls.Add(streamModeWarningLabel);
            checks.Controls.Add(vsyncCheck);
            AddLabel(panel, "运行选项", 6);
            panel.Controls.Add(checks, 1, 6);

            AddLabel(panel, "多屏配置组", 7);
            panel.Controls.Add(CreateBetaGroupPanel(), 1, 7);

            var configButtons = new FlowLayoutPanel();
            configButtons.Dock = DockStyle.Fill;
            configButtons.FlowDirection = FlowDirection.LeftToRight;
            var closeButton = new Button { Width = 100 };
            closeButton.Click += delegate { configForm.Hide(); };
            closeButton.Tag = "关闭";
            applyConfigButton.Tag = "应用";
            configButtons.Controls.Add(applyConfigButton);
            configButtons.Controls.Add(closeButton);
            panel.Controls.Add(configButtons, 1, 8);

            configLockPanel.Dock = DockStyle.Fill;
            configLockPanel.Name = "configLockPanel";
            configLockPanel.BackColor = Color.FromArgb(18, 8, 8);
            configLockPanel.Visible = false;
            configLockLabel.Dock = DockStyle.Fill;
            configLockLabel.Name = "configLockLabel";
            configLockLabel.TextAlign = ContentAlignment.MiddleCenter;
            configLockLabel.Font = new Font("Segoe UI", 24F, FontStyle.Bold);
            configLockLabel.ForeColor = ThemeRed;
            configLockLabel.BackColor = Color.FromArgb(18, 8, 8);
            configLockPanel.Controls.Add(configLockLabel);
            configHost.Controls.Add(configLockPanel);
            configLockPanel.BringToFront();
        }

        private Control CreateBetaGroupPanel()
        {
            var host = new TableLayoutPanel();
            host.Dock = DockStyle.Fill;
            host.RowCount = 2;
            host.ColumnCount = 1;
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var tools = new FlowLayoutPanel();
            tools.Dock = DockStyle.Fill;
            tools.FlowDirection = FlowDirection.LeftToRight;
            tools.Controls.Add(addBetaGroupButton);
            tools.Controls.Add(removeBetaGroupButton);
            host.Controls.Add(tools, 0, 0);

            betaPairGrid.Dock = DockStyle.Fill;
            host.Controls.Add(betaPairGrid, 0, 1);
            return host;
        }

        private void ConfigureBetaPairGrid()
        {
            if (betaPairGrid.Columns.Count > 0)
            {
                return;
            }

            betaPairGrid.AllowUserToAddRows = false;
            betaPairGrid.AllowUserToDeleteRows = false;
            betaPairGrid.RowHeadersVisible = false;
            betaPairGrid.MultiSelect = false;
            betaPairGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            betaPairGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            betaPairGrid.BorderStyle = BorderStyle.FixedSingle;
            betaPairGrid.BackgroundColor = ThemePanel;
            betaPairGrid.EnableHeadersVisualStyles = false;

            betaPairGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "启用", FillWeight = 42 });
            betaPairGrid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "目标显示器", FillWeight = 170, FlatStyle = FlatStyle.Flat });
            betaPairGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "横向像素", FillWeight = 72 });
            betaPairGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "比例", FillWeight = 62 });
            betaPairGrid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "方向", FillWeight = 86, FlatStyle = FlatStyle.Flat });
            betaPairGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "尺寸", FillWeight = 58 });
            betaPairGrid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "策略", FillWeight = 118, FlatStyle = FlatStyle.Flat });
            betaPairGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "虚拟源", FillWeight = 92 });

            DataGridViewComboBoxColumn orientationColumn = betaPairGrid.Columns[BetaColOrientation] as DataGridViewComboBoxColumn;
            if (orientationColumn != null)
            {
                orientationColumn.Items.AddRange(new object[] { "横屏", "竖屏", "横屏反向", "竖屏反向" });
            }
            DataGridViewComboBoxColumn strategyColumn = betaPairGrid.Columns[BetaColStrategy] as DataGridViewComboBoxColumn;
            if (strategyColumn != null)
            {
                strategyColumn.Items.AddRange(new object[] { "真实尺寸比例", "文字清晰优先", "直接使用源" });
            }

            betaPairGrid.CurrentCellDirtyStateChanged += delegate
            {
                if (betaPairGrid.IsCurrentCellDirty)
                {
                    betaPairGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
            betaPairGrid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e) { OnBetaPairGridCellValueChanged(e); };
            betaPairGrid.CellEndEdit += delegate { RecalculateBetaPairGrid(false); };
            betaPairGrid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                if (streamModeCheck.Checked && e.ColumnIndex == BetaColTarget)
                {
                    e.Cancel = true;
                }
            };
            betaPairGrid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
        }

        private void ConfigureInputTabs()
        {
            configInputTabs.Dock = DockStyle.Fill;
            configInputTabs.TabPages.Clear();
            presetConfigPage.Text = T("预设");
            presetConfigPage.Tag = "预设";
            manualConfigPage.Text = T("手动");
            manualConfigPage.Tag = "手动";
            presetConfigPage.Controls.Clear();
            manualConfigPage.Controls.Clear();
            presetConfigPage.Controls.Add(CreatePresetModePanel());
            manualConfigPage.Controls.Add(CreateManualModePanel());
            configInputTabs.TabPages.Add(presetConfigPage);
            configInputTabs.TabPages.Add(manualConfigPage);
            configInputTabs.SelectedIndex = Math.Max(0, Math.Min(configInputTabs.SelectedIndex, configInputTabs.TabPages.Count - 1));
        }

        private Control CreatePresetModePanel()
        {
            var grid = CreateModeGrid();
            AddHeaderRow(grid);
            AddLabel(grid, "基准", 1);
            grid.Controls.Add(CreatePresetPanel(primaryResolutionPresetCombo, primaryAspectPresetCombo, primaryOrientationPresetCombo, primarySizePresetCombo), 1, 1);
            AddLabel(grid, "目标", 2);
            grid.Controls.Add(CreatePresetPanel(targetResolutionPresetCombo, targetAspectPresetCombo, targetOrientationPresetCombo, targetSizePresetCombo), 1, 2);
            return grid;
        }

        private Control CreateManualModePanel()
        {
            var grid = CreateModeGrid();
            AddHeaderRow(grid);
            AddLabel(grid, "基准", 1);
            grid.Controls.Add(CreateManualInputPanel(manualBaseHorizontalText, manualBaseAspectText, manualBaseOrientationCombo, manualBaseSizeText), 1, 1);
            AddLabel(grid, "目标", 2);
            grid.Controls.Add(CreateManualInputPanel(manualTargetHorizontalText, manualTargetAspectText, manualTargetOrientationCombo, manualTargetSizeText), 1, 2);
            return grid;
        }

        private static TableLayoutPanel CreateModeGrid()
        {
            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.Padding = new Padding(8, 8, 8, 4);
            grid.ColumnCount = 2;
            grid.RowCount = 3;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            return grid;
        }

        private static void AddHeaderRow(TableLayoutPanel grid)
        {
            var header = new Label
            {
                Text = "横向像素    比例        方向          尺寸",
                Tag = "横向像素    比例        方向          尺寸",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Consolas", 9F)
            };
            grid.Controls.Add(header, 1, 0);
        }

        private static void AddLabel(TableLayoutPanel panel, string text, int row)
        {
            panel.Controls.Add(new Label
            {
                Text = text,
                Tag = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, row);
        }

        private static FlowLayoutPanel CreateInlinePanel(TextBox resolutionBox, TextBox sizeBox, string suffix)
        {
            var panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.LeftToRight;
            resolutionBox.Width = 120;
            sizeBox.Width = 60;
            panel.Controls.Add(resolutionBox);
            panel.Controls.Add(sizeBox);
            panel.Controls.Add(new Label { Text = suffix, Tag = suffix, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 5, 0, 0) });
            return panel;
        }

        private static FlowLayoutPanel CreatePresetPanel(ComboBox resolutionBox, ComboBox aspectBox, ComboBox orientationBox, ComboBox sizeBox)
        {
            var panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.LeftToRight;
            resolutionBox.Width = 138;
            aspectBox.Width = 86;
            orientationBox.Width = 104;
            sizeBox.Width = 96;
            panel.Controls.Add(resolutionBox);
            panel.Controls.Add(aspectBox);
            panel.Controls.Add(orientationBox);
            panel.Controls.Add(sizeBox);
            return panel;
        }

        private static FlowLayoutPanel CreateManualInputPanel(TextBox horizontalBox, TextBox aspectBox, ComboBox orientationBox, TextBox sizeBox)
        {
            var panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.LeftToRight;
            horizontalBox.Width = 138;
            aspectBox.Width = 86;
            orientationBox.Width = 104;
            sizeBox.Width = 64;
            panel.Controls.Add(horizontalBox);
            panel.Controls.Add(aspectBox);
            panel.Controls.Add(orientationBox);
            panel.Controls.Add(sizeBox);
            panel.Controls.Add(new Label { Text = "英寸", Tag = "英寸", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 5, 0, 0) });
            return panel;
        }

        private void ShowConfigForm()
        {
            if (configForm == null)
            {
                BuildConfigForm();
                ApplyTheme(configForm);
                ApplyLanguage();
            }
            UpdateConfigLock();
            configForm.Show(this);
            configForm.Activate();
        }

        private void ToggleStartup()
        {
            bool requested = startupMenuItem.Checked;
            string output;
            bool ok = requested ? EnableStartup(out output) : DisableStartup(out output);
            if (!ok)
            {
                startupMenuItem.Checked = !requested;
            }
            AppendLog((requested ? T("开机自启开启") : T("开机自启关闭")) + ": " + output.Trim());
        }

        private bool EnableStartup(out string output)
        {
            string exe = Path.Combine(root, "SBMS.exe");
            string taskRun = "\"" + exe + "\"";
            return RunTool("schtasks.exe", "/Create /TN SBMS /SC ONLOGON /TR " + Quote(taskRun) + " /RL HIGHEST /F", out output);
        }

        private bool DisableStartup(out string output)
        {
            return RunTool("schtasks.exe", "/Delete /TN SBMS /F", out output);
        }

        private static bool IsStartupEnabled()
        {
            string output;
            return RunTool("schtasks.exe", "/Query /TN SBMS", out output);
        }

        private static bool RunTool(string fileName, string arguments, out string output)
        {
            using (var p = new Process())
            {
                p.StartInfo.FileName = fileName;
                p.StartInfo.Arguments = arguments;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                p.Start();
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                output = stdout + stderr;
                return p.ExitCode == 0;
            }
        }

        private void ApplyLanguage()
        {
            Text = AppName + " " + BuildLabel;
            settingsMenuItem.Text = T("设置");
            configMenuItem.Text = T("配置");
            startupMenuItem.Text = T("开机自启");
            lightweightMenuItem.Text = T("轻量模式");
            languageMenuItem.Text = T("语言");
            chineseMenuItem.Text = "中文";
            englishMenuItem.Text = "English";
            trayOpenMenuItem.Text = T("打开");
            trayStopMenuItem.Text = T("停止");
            trayExitMenuItem.Text = T("退出");
            trayIcon.Text = AppName;
            chineseMenuItem.Checked = !english;
            englishMenuItem.Checked = english;
            startButton.Text = T("启动");
            stopButton.Text = T("停止");
            listButton.Text = T("刷新列表");
            calculateButton.Text = T("计算");
            applyConfigButton.Text = T("应用");
            addBetaGroupButton.Text = "⊕ " + T("新增组") + " β";
            removeBetaGroupButton.Text = T("删除组");
            inputCheck.Text = T("鼠标映射");
            windowMoveCheck.Text = T("迁移窗口");
            deviceHostCheck.Text = T("管理虚拟显示器");
            streamModeCheck.Text = T("串流模式");
            multiScreenBetaCheck.Text = T("多屏 BETA");
            streamModeWarningLabel.Text = T("如果不清楚这个选项的作用，请不要勾选");
            if (configForm != null)
            {
                configForm.Text = T("配置");
                ApplyLanguageToControls(configForm);
            }
            ApplyBetaPairGridLanguage();
            configLockLabel.Text = T("配置已锁定") + Environment.NewLine + T("请先停止 SBMS");
            ApplyComboTexts();
            UpdateStatus();
        }

        private void ApplyBetaPairGridLanguage()
        {
            if (betaPairGrid.Columns.Count < 8)
            {
                return;
            }
            betaPairGrid.Columns[BetaColEnabled].HeaderText = T("启用");
            betaPairGrid.Columns[BetaColTarget].HeaderText = T(streamModeCheck.Checked ? "串流目标" : "目标显示器");
            betaPairGrid.Columns[BetaColHorizontal].HeaderText = T("横向像素");
            betaPairGrid.Columns[BetaColAspect].HeaderText = T("比例");
            betaPairGrid.Columns[BetaColOrientation].HeaderText = T("方向");
            betaPairGrid.Columns[BetaColSize].HeaderText = T("尺寸");
            betaPairGrid.Columns[BetaColStrategy].HeaderText = T("策略");
            betaPairGrid.Columns[BetaColSource].HeaderText = T("虚拟源");
        }

        private void ApplyLanguageToControls(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                string key = control.Tag as string;
                if (!string.IsNullOrEmpty(key))
                {
                    control.Text = T(key);
                }
                ApplyLanguageToControls(control);
            }
        }

        private void ApplyComboTexts()
        {
            int strategyIndex = strategyCombo.SelectedIndex;
            strategyCombo.Items.Clear();
            strategyCombo.Items.AddRange(new object[] { T("真实尺寸比例"), T("文字清晰优先"), T("直接使用源") });
            strategyCombo.SelectedIndex = Math.Max(0, Math.Min(strategyIndex, strategyCombo.Items.Count - 1));

            int filterIndex = filterCombo.SelectedIndex;
            filterCombo.Items.Clear();
            filterCombo.Items.AddRange(new object[] { T("自动"), T("线性平滑"), T("锐利最近邻"), T("整数2x") });
            filterCombo.SelectedIndex = Math.Max(0, Math.Min(filterIndex, filterCombo.Items.Count - 1));

            ConfigurePresetCombos();
            ConfigureManualOrientationCombos();
            if (configInputTabs.TabPages.Count == 2)
            {
                presetConfigPage.Text = T("预设");
                manualConfigPage.Text = T("手动");
            }
        }

        private void ConfigurePresetCombos()
        {
            updatingPresetCombos = true;
            ConfigureResolutionPresetCombo(primaryResolutionPresetCombo);
            ConfigureResolutionPresetCombo(targetResolutionPresetCombo);
            ConfigureAspectPresetCombo(primaryAspectPresetCombo);
            ConfigureAspectPresetCombo(targetAspectPresetCombo);
            ConfigureOrientationPresetCombo(primaryOrientationPresetCombo);
            ConfigureOrientationPresetCombo(targetOrientationPresetCombo);
            ConfigureSizePresetCombo(primarySizePresetCombo);
            ConfigureSizePresetCombo(targetSizePresetCombo);
            updatingPresetCombos = false;
        }

        private void ConfigureManualOrientationCombos()
        {
            ConfigureOrientationPresetCombo(manualBaseOrientationCombo);
            ConfigureOrientationPresetCombo(manualTargetOrientationCombo);
        }

        private void ConfigureResolutionPresetCombo(ComboBox combo)
        {
            int index = combo.SelectedIndex;
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Items.Clear();
            combo.Items.AddRange(new object[] {
                "1080p",
                "2K / 1440p",
                "4K / 2160p",
                "5K / 2880p",
                "8K / 4320p",
                "2880p"
            });
            combo.SelectedIndex = index >= 0 ? Math.Min(index, combo.Items.Count - 1) : 0;
        }

        private void ConfigureAspectPresetCombo(ComboBox combo)
        {
            int index = combo.SelectedIndex;
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Items.Clear();
            combo.Items.AddRange(new object[] { "16:9", "16:10", "4:3" });
            combo.SelectedIndex = index >= 0 ? Math.Min(index, combo.Items.Count - 1) : 0;
        }

        private void ConfigureOrientationPresetCombo(ComboBox combo)
        {
            int index = combo.SelectedIndex;
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Items.Clear();
            combo.Items.AddRange(new object[] {
                T("横屏"),
                T("竖屏"),
                T("横屏反向"),
                T("竖屏反向")
            });
            combo.SelectedIndex = index >= 0 ? Math.Min(index, combo.Items.Count - 1) : 0;
        }

        private void ConfigureSizePresetCombo(ComboBox combo)
        {
            int index = combo.SelectedIndex;
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Items.Clear();
            combo.Items.AddRange(new object[] {
                "13.3\"",
                "14\"",
                "15.3\"",
                "15.6\"",
                "16\"",
                "18\"",
                "24\"",
                "27\"",
                "32\""
            });
            combo.SelectedIndex = index >= 0 ? Math.Min(index, combo.Items.Count - 1) : 0;
        }

        private void ApplyPresetSelections(bool recalculate)
        {
            if (updatingPresetCombos || updatingConfigurationInputs)
            {
                return;
            }
            if (configInputTabs.TabPages.Count > 0 && configInputTabs.SelectedIndex != 0)
            {
                return;
            }

            ApplyResolutionPreset(primaryResolutionPresetCombo, primaryAspectPresetCombo, primaryOrientationPresetCombo, primaryResolutionText);
            ApplyResolutionPreset(targetResolutionPresetCombo, targetAspectPresetCombo, targetOrientationPresetCombo, targetResolutionText);
            ApplySizePreset(primarySizePresetCombo, primarySizeText);
            ApplySizePreset(targetSizePresetCombo, targetSizeText);

            if (recalculate)
            {
                ApplyStrategy(false);
                UpdateStatus();
            }
        }

        private void ApplyManualSelections(bool recalculate)
        {
            if (updatingConfigurationInputs)
            {
                return;
            }
            if (configInputTabs.TabPages.Count > 0 && configInputTabs.SelectedIndex != 1)
            {
                return;
            }

            if (!ApplyManualInput(manualBaseHorizontalText, manualBaseAspectText, manualBaseOrientationCombo, manualBaseSizeText, primaryResolutionText, primarySizeText) ||
                !ApplyManualInput(manualTargetHorizontalText, manualTargetAspectText, manualTargetOrientationCombo, manualTargetSizeText, targetResolutionText, targetSizeText))
            {
                if (recalculate)
                {
                    AppendLog("参数无效");
                }
                return;
            }

            if (recalculate)
            {
                ApplyStrategy(false);
                UpdateStatus();
            }
        }

        private void SyncConfigurationInputsFromMode(bool recalculate)
        {
            updatingConfigurationInputs = true;
            try
            {
                if (configInputTabs.TabPages.Count == 0 || configInputTabs.SelectedIndex == 0)
                {
                    ApplyResolutionPreset(primaryResolutionPresetCombo, primaryAspectPresetCombo, primaryOrientationPresetCombo, primaryResolutionText);
                    ApplyResolutionPreset(targetResolutionPresetCombo, targetAspectPresetCombo, targetOrientationPresetCombo, targetResolutionText);
                    ApplySizePreset(primarySizePresetCombo, primarySizeText);
                    ApplySizePreset(targetSizePresetCombo, targetSizeText);
                }
                else
                {
                    ApplyManualInput(manualBaseHorizontalText, manualBaseAspectText, manualBaseOrientationCombo, manualBaseSizeText, primaryResolutionText, primarySizeText);
                    ApplyManualInput(manualTargetHorizontalText, manualTargetAspectText, manualTargetOrientationCombo, manualTargetSizeText, targetResolutionText, targetSizeText);
                }
            }
            finally
            {
                updatingConfigurationInputs = false;
            }

            if (recalculate)
            {
                ApplyStrategy(false);
                UpdateStatus();
            }
        }

        private static void ApplyResolutionPreset(ComboBox resolutionCombo, ComboBox aspectCombo, ComboBox orientationCombo, TextBox targetTextBox)
        {
            int baseWidth = GetResolutionPresetBaseWidth(resolutionCombo.SelectedIndex);
            if (baseWidth <= 0)
            {
                return;
            }

            int aspectW;
            int aspectH;
            GetAspect(aspectCombo.SelectedIndex, out aspectW, out aspectH);
            int width = RoundEven(baseWidth);
            int height = RoundEven(width * aspectH / (double)aspectW);
            bool portrait = orientationCombo.SelectedIndex == 1 || orientationCombo.SelectedIndex == 3;
            if (portrait)
            {
                int temp = width;
                width = height;
                height = temp;
            }
            targetTextBox.Text = width + "x" + height;
        }

        private static int GetResolutionPresetBaseWidth(int index)
        {
            switch (index)
            {
                case 0: return 1920;
                case 1: return 2560;
                case 2: return 3840;
                case 3: return 5120;
                case 4: return 7680;
                case 5: return 5120;
                default: return 1920;
            }
        }

        private static bool ApplyManualInput(TextBox horizontalBox, TextBox aspectBox, ComboBox orientationBox, TextBox sizeBox, TextBox resolutionTarget, TextBox sizeTarget)
        {
            int horizontal;
            int aspectW;
            int aspectH;
            double size;
            if (!int.TryParse(horizontalBox.Text.Trim(), out horizontal) ||
                horizontal <= 0 ||
                !TryParseAspectText(aspectBox.Text, out aspectW, out aspectH) ||
                !TryParseSize(sizeBox.Text, out size) ||
                size <= 0.0)
            {
                return false;
            }

            int width = RoundEven(horizontal);
            int height = RoundEven(width * aspectH / (double)aspectW);
            bool portrait = orientationBox.SelectedIndex == 1 || orientationBox.SelectedIndex == 3;
            if (portrait)
            {
                int temp = width;
                width = height;
                height = temp;
            }
            resolutionTarget.Text = width + "x" + height;
            sizeTarget.Text = sizeBox.Text.Trim().Replace(',', '.');
            return true;
        }

        private static void GetAspect(int index, out int width, out int height)
        {
            switch (index)
            {
                case 1:
                    width = 16;
                    height = 10;
                    return;
                case 2:
                    width = 4;
                    height = 3;
                    return;
                default:
                    width = 16;
                    height = 9;
                    return;
            }
        }

        private static void ApplySizePreset(ComboBox combo, TextBox targetTextBox)
        {
            string value = GetSizePresetValue(combo.SelectedIndex);
            if (!string.IsNullOrEmpty(value))
            {
                targetTextBox.Text = value;
            }
        }

        private static string GetSizePresetValue(int index)
        {
            switch (index)
            {
                case 0: return "13.3";
                case 1: return "14";
                case 2: return "15.3";
                case 3: return "15.6";
                case 4: return "16";
                case 5: return "18";
                case 6: return "24";
                case 7: return "27";
                case 8: return "32";
                default: return "24";
            }
        }

        private string T(string text)
        {
            if (!english)
            {
                return text;
            }
            switch (text)
            {
                case "设置": return "Settings";
                case "配置": return "Configuration";
                case "开机自启": return "Start with Windows";
                case "轻量模式": return "Lightweight mode";
                case "语言": return "Language";
                case "打开": return "Open";
                case "退出": return "Exit";
                case "启动": return "Start";
                case "停止": return "Stop";
                case "刷新列表": return "Refresh";
                case "计算": return "Calculate";
                case "应用": return "Apply";
                case "鼠标映射": return "Pointer";
                case "迁移窗口": return "Move windows";
                case "管理虚拟显示器": return "Virtual display";
                case "串流模式": return "Streaming mode";
                case "多屏 BETA": return "Multi-screen BETA";
                case "多屏配置组": return "Multi-screen groups";
                case "新增组": return "Add group";
                case "删除组": return "Remove group";
                case "启用": return "On";
                case "目标显示器": return "Target display";
                case "串流目标": return "Streaming target";
                case "横向像素": return "Horizontal px";
                case "比例": return "Aspect";
                case "方向": return "Orientation";
                case "尺寸": return "Size";
                case "策略": return "Strategy";
                case "如果不清楚这个选项的作用，请不要勾选": return "Do not enable this unless you know what it does";
                case "虚拟源": return "Virtual source";
                case "输出目标": return "Target";
                case "配置方式": return "Input";
                case "预设": return "Preset";
                case "手动": return "Manual";
                case "基准": return "Base";
                case "横向像素    比例        方向          尺寸": return "Horizontal px   aspect      orientation    size";
                case "目标预设": return "Target preset";
                case "目标屏参数": return "Target spec";
                case "尺寸策略": return "Sizing";
                case "启动分辨率": return "Launch mode";
                case "映射结果": return "Mapping";
                case "缩放滤镜": return "Scaling";
                case "运行选项": return "Runtime";
                case "英寸": return "inch";
                case "横屏": return "Landscape";
                case "竖屏": return "Portrait";
                case "横屏反向": return "Landscape flipped";
                case "竖屏反向": return "Portrait flipped";
                case "关闭": return "Close";
                case "真实尺寸比例": return "Physical size";
                case "文字清晰优先": return "Text clarity";
                case "直接使用源": return "Direct source";
                case "自动": return "Auto";
                case "线性平滑": return "Linear";
                case "锐利最近邻": return "Nearest";
                case "整数2x": return "Integer 2x";
                case "开机自启开启": return "Startup enabled";
                case "开机自启关闭": return "Startup disabled";
                case "运行中": return "Running";
                case "串流中": return "Streaming";
                case "多屏BETA运行中": return "Multi-screen BETA running";
                case "待机": return "Idle";
                case "源": return "source";
                case "目标": return "target";
                case "按计算分辨率等待虚拟屏": return "Wait for calculated virtual display";
                case "配置已应用": return "Configuration applied";
                case "运行中配置已锁定": return "Configuration is locked while running";
                case "配置已锁定": return "CONFIGURATION LOCKED";
                case "请先停止 SBMS": return "Stop SBMS before changing settings";
                case "已隐藏到托盘": return "Hidden to tray";
                default: return text;
            }
        }

        private void ApplyTheme(Control parent)
        {
            StyleControl(parent);
            foreach (Control control in parent.Controls)
            {
                ApplyTheme(control);
            }
            menu.BackColor = ThemePanel;
            menu.ForeColor = ThemeGreen;
            ApplyThemeToMenuItems(menu.Items);
            trayMenu.BackColor = ThemePanel;
            trayMenu.ForeColor = ThemeGreen;
            ApplyThemeToMenuItems(trayMenu.Items);
        }

        private static void StyleControl(Control control)
        {
            if (control == null)
            {
                return;
            }
            if (control.Name == "configLockPanel")
            {
                control.BackColor = Color.FromArgb(18, 8, 8);
                control.ForeColor = ThemeRed;
                return;
            }
            if (control.Name == "configLockLabel")
            {
                control.BackColor = Color.FromArgb(18, 8, 8);
                control.ForeColor = ThemeRed;
                return;
            }
            if (control.Name == "streamModeWarningLabel")
            {
                control.BackColor = ThemeBack;
                control.ForeColor = ThemeRed;
                return;
            }
            if (control is TextBox || control is ListBox || control is ComboBox)
            {
                control.BackColor = ThemePanel;
                control.ForeColor = Color.White;
            }
            else if (control is DataGridView)
            {
                DataGridView grid = (DataGridView)control;
                grid.BackgroundColor = ThemePanel;
                grid.GridColor = ThemeGreen;
                grid.DefaultCellStyle.BackColor = ThemePanel;
                grid.DefaultCellStyle.ForeColor = Color.White;
                grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(8, 82, 33);
                grid.DefaultCellStyle.SelectionForeColor = Color.White;
                grid.ColumnHeadersDefaultCellStyle.BackColor = ThemePanel2;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = ThemeGreen;
                grid.RowHeadersDefaultCellStyle.BackColor = ThemePanel2;
                grid.RowHeadersDefaultCellStyle.ForeColor = ThemeGreen;
            }
            else if (control is Button)
            {
                control.BackColor = ThemePanel2;
                control.ForeColor = ThemeGreen;
                ((Button)control).FlatStyle = FlatStyle.Flat;
                ((Button)control).FlatAppearance.BorderColor = ThemeGreen;
            }
            else
            {
                control.BackColor = ThemeBack;
                control.ForeColor = ThemeMuted;
            }
            CheckBox checkBox = control as CheckBox;
            if (checkBox != null)
            {
                checkBox.ForeColor = ThemeMuted;
            }
        }

        private void UpdateConfigLock()
        {
            bool locked = IsBridgeRunning();
            configLockPanel.Visible = locked;
            if (locked)
            {
                configLockPanel.BringToFront();
            }
        }

        private void OnStreamModeChanged()
        {
            ApplyStreamModeToBetaPairGrid();
            UpdateRuntimeOptionState();
            UpdateStatus();
        }

        private void OnMultiScreenBetaChanged()
        {
            if (multiScreenBetaCheck.Checked)
            {
                if (!deviceHostCheck.Checked)
                {
                    deviceHostCheck.Checked = true;
                }
            }
            UpdateRuntimeOptionState();
            UpdateStatus();
        }

        private void ApplyStreamModeToBetaPairGrid()
        {
            if (betaPairGrid.Columns.Count <= BetaColTarget)
            {
                return;
            }

            bool streamOnly = streamModeCheck.Checked;
            betaPairGrid.Columns[BetaColTarget].HeaderText = T(streamOnly ? "串流目标" : "目标显示器");
            betaPairGrid.Columns[BetaColTarget].ReadOnly = streamOnly;

            updatingBetaPairGrid = true;
            try
            {
                for (int i = 0; i < betaPairGrid.Rows.Count; ++i)
                {
                    DataGridViewRow row = betaPairGrid.Rows[i];
                    if (streamOnly)
                    {
                        string streamLabel = "串流目标 " + (i + 1).ToString(CultureInfo.InvariantCulture);
                        AddComboItemIfMissing(BetaColTarget, streamLabel);
                        row.Cells[BetaColTarget].Value = streamLabel;
                    }
                    else
                    {
                        DisplayChoice display = row.Tag as DisplayChoice ?? GetDefaultPhysicalDisplay("");
                        if (display != null)
                        {
                            string targetLabel = GetDisplayLabel(display);
                            AddComboItemIfMissing(BetaColTarget, targetLabel);
                            row.Cells[BetaColTarget].Value = targetLabel;
                            row.Tag = display;
                        }
                    }
                }
            }
            finally
            {
                updatingBetaPairGrid = false;
            }
        }

        private void UpdateRuntimeOptionState()
        {
            bool streamOnly = streamModeCheck.Checked;
            bool multiBeta = multiScreenBetaCheck.Checked;
            if ((streamOnly || multiBeta) && !deviceHostCheck.Checked)
            {
                deviceHostCheck.Checked = true;
            }

            bool bridgeRunning = IsBridgeRunning();
            deviceHostCheck.Enabled = !streamOnly && !multiBeta && !bridgeRunning;
            targetDisplayCombo.Enabled = !streamOnly && !multiBeta && !bridgeRunning;
            targetText.Enabled = !streamOnly && !multiBeta && !bridgeRunning;
            betaPairGrid.Enabled = multiBeta && !bridgeRunning;
            addBetaGroupButton.Enabled = multiBeta && !bridgeRunning && betaPairGrid.Rows.Count < MultiScreenBetaMaxTargets;
            removeBetaGroupButton.Enabled = multiBeta && !bridgeRunning && betaPairGrid.Rows.Count > 1;
            filterCombo.Enabled = !streamOnly && !bridgeRunning;
            inputCheck.Enabled = !streamOnly && !bridgeRunning;
            windowMoveCheck.Enabled = !streamOnly && !bridgeRunning;
            vsyncCheck.Enabled = !streamOnly && !bridgeRunning;
            streamModeCheck.Enabled = !bridgeRunning;
            multiScreenBetaCheck.Enabled = !bridgeRunning;
        }

        private static void ApplyThemeToMenuItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.BackColor = ThemePanel;
                item.ForeColor = ThemeGreen;
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem != null)
                {
                    menuItem.DropDown.BackColor = ThemePanel;
                    menuItem.DropDown.ForeColor = ThemeGreen;
                    ApplyThemeToMenuItems(menuItem.DropDownItems);
                }
            }
        }

        private void UpdateStatus()
        {
            bool running = IsBridgeRunning();
            bool streamOnly = streamModeCheck.Checked && running && (process == null || process.HasExited);
            bool multiBeta = multiScreenBetaCheck.Checked && running && (streamModeCheck.Checked || HasRunningBetaProcess());
            statusLabel.Text = AppName + " // " + T(running ? (multiBeta ? "多屏BETA运行中" : (streamOnly ? "串流中" : "运行中")) : "待机");
            if (multiScreenBetaCheck.Checked)
            {
                int pairCount = Math.Max(1, CountEnabledBetaPairs());
                routeLabel.Text = T("虚拟源") + " x" + pairCount.ToString(CultureInfo.InvariantCulture) +
                                  (streamModeCheck.Checked ? "  //  " + T("串流模式") : "  >  " + T("目标") + " x" + pairCount.ToString(CultureInfo.InvariantCulture));
            }
            else if (streamModeCheck.Checked)
            {
                routeLabel.Text = T("虚拟源") + " " + sourceText.Text.Trim() + "  //  " + T("串流模式");
            }
            else
            {
                routeLabel.Text = T("源") + " " + sourceText.Text.Trim() + "  >  " + T("目标") + " " + targetText.Text.Trim();
            }
            trayStopMenuItem.Enabled = running;
        }

        private void RefreshDisplays()
        {
            string previousSourceDevice = GetSelectedDeviceName(sourceDisplayCombo);
            string previousTargetDevice = GetSelectedDeviceName(targetDisplayCombo);
            List<BridgePairSnapshot> betaSnapshots = CaptureBetaPairSnapshots();
            if (string.IsNullOrWhiteSpace(previousSourceDevice) && IsDisplayDeviceSelector(sourceText.Text.Trim()))
            {
                previousSourceDevice = sourceText.Text.Trim();
            }
            if (string.IsNullOrWhiteSpace(previousTargetDevice) && IsDisplayDeviceSelector(targetText.Text.Trim()))
            {
                previousTargetDevice = targetText.Text.Trim();
            }

            displays.Clear();
            displayList.Items.Clear();
            sourceDisplayCombo.Items.Clear();
            targetDisplayCombo.Items.Clear();
            betaPairGrid.Rows.Clear();

            if (!File.Exists(nativeExe))
            {
                displayList.Items.Add("找不到 SBMSNative.exe");
                return;
            }

            string output = CaptureNativeOutput("--list");
            foreach (string rawLine in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                DisplayChoice display;
                if (TryParseDisplayLine(rawLine.Trim(), out display))
                {
                    display.Number = displays.Count + 1;
                    displays.Add(display);
                    displayList.Items.Add(display);
                    if (display.Virtual)
                    {
                        sourceDisplayCombo.Items.Add(display);
                    }
                    else
                    {
                        targetDisplayCombo.Items.Add(display);
                    }
                }
            }

            if (sourceDisplayCombo.Items.Count == 0)
            {
                sourceDisplayCombo.Items.Add(T("按计算分辨率等待虚拟屏"));
            }
            SelectDefaultDisplays(previousSourceDevice, previousTargetDevice);
            RefreshBetaTargetChoices();
            RestoreBetaPairRows(betaSnapshots, previousTargetDevice);
            SyncSelectedDisplaysToSelectors();
            ApplyStreamModeToBetaPairGrid();
            RecalculateBetaPairGrid(false);
            UpdateStatus();
        }

        private static bool TryParseDisplayLine(string line, out DisplayChoice display)
        {
            display = null;
            Match match = Regex.Match(line, @"^(\\\\\.\\DISPLAY\d+)( primary)?\: pos=[^ ]+ mode=(\d+x\d+)@(\d+) name=(.+)$");
            if (!match.Success)
            {
                return false;
            }

            string name = match.Groups[5].Value.Trim();
            display = new DisplayChoice
            {
                DeviceName = match.Groups[1].Value,
                Primary = match.Groups[2].Success,
                Resolution = match.Groups[3].Value,
                Refresh = match.Groups[4].Value,
                Name = name,
                Virtual = IsVirtualDisplayName(name)
            };
            return true;
        }

        private static bool IsVirtualDisplayName(string name)
        {
            string lower = name.ToLowerInvariant();
            return lower.Contains("iddsample") ||
                   lower.Contains("displaybridge") ||
                   lower.Contains("sbms");
        }

        private static string GetSelectedDeviceName(ComboBox combo)
        {
            DisplayChoice display = combo.SelectedItem as DisplayChoice;
            return display != null ? display.DeviceName : "";
        }

        private static bool IsDisplayDeviceSelector(string value)
        {
            return value.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase);
        }

        private List<BridgePairSnapshot> CaptureBetaPairSnapshots()
        {
            var snapshots = new List<BridgePairSnapshot>();
            foreach (DataGridViewRow row in betaPairGrid.Rows)
            {
                snapshots.Add(new BridgePairSnapshot
                {
                    Enabled = IsBetaRowEnabled(row),
                    Target = GetNormalTargetLabel(row),
                    Horizontal = GetCellText(row, BetaColHorizontal),
                    Aspect = GetCellText(row, BetaColAspect),
                    Orientation = GetCellText(row, BetaColOrientation),
                    Size = GetCellText(row, BetaColSize),
                    Strategy = GetCellText(row, BetaColStrategy),
                    Source = GetCellText(row, BetaColSource)
                });
            }
            return snapshots;
        }

        private void RefreshBetaTargetChoices()
        {
            DataGridViewComboBoxColumn targetColumn = betaPairGrid.Columns[BetaColTarget] as DataGridViewComboBoxColumn;
            if (targetColumn == null)
            {
                return;
            }

            targetColumn.Items.Clear();
            foreach (DisplayChoice display in GetPhysicalDisplays())
            {
                targetColumn.Items.Add(GetDisplayLabel(display));
            }
        }

        private void RestoreBetaPairRows(List<BridgePairSnapshot> snapshots, string previousTargetDevice)
        {
            updatingBetaPairGrid = true;
            try
            {
                betaPairGrid.Rows.Clear();
                if (snapshots.Count == 0)
                {
                    AddBetaGroupRowInternal(CreateDefaultBetaPairSnapshot(previousTargetDevice), false);
                }
                else
                {
                    for (int i = 0; i < snapshots.Count && i < MultiScreenBetaMaxTargets; ++i)
                    {
                        AddBetaGroupRowInternal(snapshots[i], false);
                    }
                }
                if (betaPairGrid.Rows.Count == 0)
                {
                    AddBetaGroupRowInternal(CreateDefaultBetaPairSnapshot(previousTargetDevice), false);
                }
            }
            finally
            {
                updatingBetaPairGrid = false;
            }
        }

        private void AddBetaGroupRow(BridgePairSnapshot snapshot, bool userAdded)
        {
            if (betaPairGrid.Rows.Count >= MultiScreenBetaMaxTargets)
            {
                AppendLog("多屏 BETA 当前最多支持 " + MultiScreenBetaMaxTargets.ToString(CultureInfo.InvariantCulture) + " 个配置组");
                return;
            }

            updatingBetaPairGrid = true;
            try
            {
                AddBetaGroupRowInternal(snapshot ?? CreateDefaultBetaPairSnapshot(""), true);
            }
            finally
            {
                updatingBetaPairGrid = false;
            }

            if (userAdded)
            {
                multiScreenBetaCheck.Checked = true;
                ApplyStreamModeToBetaPairGrid();
                AppendLog("已新增 BETA 配置组");
            }
        }

        private void AddBetaGroupRowInternal(BridgePairSnapshot snapshot, bool selectNewRow)
        {
            DisplayChoice display = FindDisplayByTargetLabel(snapshot.Target) ?? GetDefaultPhysicalDisplay("");
            string targetLabel = display != null ? GetDisplayLabel(display) : "";
            string rowHorizontal = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Horizontal) ? snapshot.Horizontal : "2560";
            string rowAspect = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Aspect) ? snapshot.Aspect : "16:9";
            string rowOrientation = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Orientation) ? snapshot.Orientation : "横屏";
            string rowSize = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Size) ? snapshot.Size : (display != null ? GuessTargetSize(display) : "24");
            string rowStrategy = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Strategy) ? snapshot.Strategy : "真实尺寸比例";
            string rowSource = snapshot != null ? snapshot.Source : "";
            bool enabled = snapshot == null || snapshot.Enabled;

            AddComboItemIfMissing(BetaColTarget, targetLabel);
            int index = betaPairGrid.Rows.Add(enabled, targetLabel, rowHorizontal, rowAspect, rowOrientation, rowSize, rowStrategy, rowSource);
            betaPairGrid.Rows[index].Tag = display;
            if (display != null && snapshot != null && string.IsNullOrWhiteSpace(snapshot.Horizontal))
            {
                PopulateBetaRowFromDisplay(betaPairGrid.Rows[index], display);
            }
            if (selectNewRow)
            {
                betaPairGrid.ClearSelection();
                betaPairGrid.Rows[index].Selected = true;
            }
        }

        private BridgePairSnapshot CreateDefaultBetaPairSnapshot(string previousTargetDevice)
        {
            DisplayChoice display = GetDefaultPhysicalDisplay(previousTargetDevice);
            string horizontal = "2560";
            string aspect = "16:9";
            string orientation = "横屏";
            if (display != null)
            {
                Resolution resolution;
                if (TryParseResolution(display.Resolution, out resolution))
                {
                    int parsedHorizontal;
                    BuildResolutionParts(resolution, out parsedHorizontal, out aspect, out orientation);
                    horizontal = parsedHorizontal.ToString(CultureInfo.InvariantCulture);
                }
            }
            return new BridgePairSnapshot
            {
                Enabled = true,
                Target = display != null ? GetDisplayLabel(display) : "",
                Horizontal = horizontal,
                Aspect = aspect,
                Orientation = orientation,
                Size = display != null ? GuessTargetSize(display) : "24",
                Strategy = "真实尺寸比例",
                Source = ""
            };
        }

        private void RemoveSelectedBetaGroup()
        {
            if (betaPairGrid.Rows.Count <= 1)
            {
                AppendLog("至少保留一个配置组");
                return;
            }

            int index = betaPairGrid.CurrentRow != null ? betaPairGrid.CurrentRow.Index : betaPairGrid.Rows.Count - 1;
            if (index >= 0 && index < betaPairGrid.Rows.Count)
            {
                betaPairGrid.Rows.RemoveAt(index);
            }
        }

        private List<DisplayChoice> GetPhysicalDisplays()
        {
            var physical = new List<DisplayChoice>();
            foreach (DisplayChoice display in displays)
            {
                if (!display.Virtual)
                {
                    physical.Add(display);
                }
            }
            return physical;
        }

        private static string GetDisplayLabel(DisplayChoice display)
        {
            return display == null ? "" : display.ToString();
        }

        private string GetNormalTargetLabel(DataGridViewRow row)
        {
            DisplayChoice display = row.Tag as DisplayChoice;
            if (display != null)
            {
                return GetDisplayLabel(display);
            }
            return GetCellText(row, BetaColTarget);
        }

        private DisplayChoice FindDisplayByTargetLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return null;
            }
            foreach (DisplayChoice display in GetPhysicalDisplays())
            {
                string displayLabel = GetDisplayLabel(display);
                if (string.Equals(displayLabel, label, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(display.DeviceName, label, StringComparison.OrdinalIgnoreCase) ||
                    label.IndexOf(display.DeviceName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return display;
                }
            }
            return null;
        }

        private DisplayChoice GetDefaultPhysicalDisplay(string previousTargetDevice)
        {
            if (!string.IsNullOrWhiteSpace(previousTargetDevice))
            {
                foreach (DisplayChoice display in GetPhysicalDisplays())
                {
                    if (string.Equals(display.DeviceName, previousTargetDevice, StringComparison.OrdinalIgnoreCase))
                    {
                        return display;
                    }
                }
            }

            DisplayChoice selectedTarget = targetDisplayCombo.SelectedItem as DisplayChoice;
            if (selectedTarget != null && !selectedTarget.Virtual)
            {
                return selectedTarget;
            }

            List<DisplayChoice> physical = GetPhysicalDisplays();
            return physical.Count > 0 ? physical[0] : null;
        }

        private void AddComboItemIfMissing(int columnIndex, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            DataGridViewComboBoxColumn column = betaPairGrid.Columns[columnIndex] as DataGridViewComboBoxColumn;
            if (column == null)
            {
                return;
            }
            foreach (object item in column.Items)
            {
                if (string.Equals(Convert.ToString(item, CultureInfo.InvariantCulture), value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            column.Items.Add(value);
        }

        private void PopulateBetaRowFromDisplay(DataGridViewRow row, DisplayChoice display)
        {
            if (row == null || display == null)
            {
                return;
            }
            Resolution resolution;
            if (!TryParseResolution(display.Resolution, out resolution))
            {
                return;
            }

            int horizontal;
            string aspect;
            string orientation;
            BuildResolutionParts(resolution, out horizontal, out aspect, out orientation);
            row.Cells[BetaColHorizontal].Value = horizontal.ToString(CultureInfo.InvariantCulture);
            row.Cells[BetaColAspect].Value = aspect;
            row.Cells[BetaColOrientation].Value = orientation;
            row.Cells[BetaColSize].Value = GuessTargetSize(display);
        }

        private void OnBetaPairGridCellValueChanged(DataGridViewCellEventArgs e)
        {
            if (updatingBetaPairGrid || e.RowIndex < 0 || e.RowIndex >= betaPairGrid.Rows.Count)
            {
                return;
            }

            DataGridViewRow row = betaPairGrid.Rows[e.RowIndex];
            if (e.ColumnIndex == BetaColTarget && !streamModeCheck.Checked)
            {
                DisplayChoice display = FindDisplayByTargetLabel(GetCellText(row, BetaColTarget));
                row.Tag = display;
                if (display != null)
                {
                    updatingBetaPairGrid = true;
                    try
                    {
                        PopulateBetaRowFromDisplay(row, display);
                    }
                    finally
                    {
                        updatingBetaPairGrid = false;
                    }
                }
            }
            RecalculateBetaPairGrid(false);
        }

        private int CountEnabledBetaPairs()
        {
            int count = 0;
            foreach (DataGridViewRow row in betaPairGrid.Rows)
            {
                if (IsBetaRowEnabled(row))
                {
                    ++count;
                }
            }
            return count;
        }

        private void RecalculateBetaPairGrid(bool log)
        {
            if (updatingBetaPairGrid)
            {
                return;
            }

            Resolution baseResolution;
            double baseSize;
            if (!TryParseResolution(primaryResolutionText.Text, out baseResolution) ||
                !TryParseSize(primarySizeText.Text, out baseSize) ||
                baseResolution.Width <= 0 ||
                baseResolution.Height <= 0 ||
                baseSize <= 0.0)
            {
                return;
            }

            updatingBetaPairGrid = true;
            try
            {
                foreach (DataGridViewRow row in betaPairGrid.Rows)
                {
                    int strategy = GetBetaRowStrategyIndex(row);
                    if (strategy == 2)
                    {
                        continue;
                    }

                    Resolution targetResolution;
                    double targetSize;
                    if (!TryReadBetaTargetSpec(row, out targetResolution, out targetSize))
                    {
                        row.Cells[BetaColSource].Value = "参数无效";
                        continue;
                    }

                    Resolution source = strategy == 1
                        ? CalculateQualitySource(baseResolution, targetResolution, baseSize, targetSize)
                        : CalculatePhysicalSource(baseResolution, targetResolution, baseSize, targetSize);
                    row.Cells[BetaColSource].Value = FormatResolution(source);
                }
            }
            finally
            {
                updatingBetaPairGrid = false;
            }
            if (log)
            {
                AppendLog("多屏配置组已计算");
            }
            UpdateStatus();
        }

        private bool TryGetEnabledBridgePairs(bool streamOnly, out List<BridgePairConfig> pairs, out string message)
        {
            pairs = new List<BridgePairConfig>();
            message = "";
            RecalculateBetaPairGrid(false);

            foreach (DataGridViewRow row in betaPairGrid.Rows)
            {
                if (!IsBetaRowEnabled(row))
                {
                    continue;
                }

                DisplayChoice targetDisplay = row.Tag as DisplayChoice;
                if (!streamOnly && targetDisplay == null)
                {
                    message = "多屏配置组缺少目标显示器";
                    return false;
                }

                Resolution targetResolution;
                double targetSize;
                if (!TryReadBetaTargetSpec(row, out targetResolution, out targetSize))
                {
                    message = "多屏配置组参数无效: " + GetCellText(row, BetaColTarget);
                    return false;
                }

                Resolution sourceResolution;
                if (!TryParseResolution(GetCellText(row, BetaColSource), out sourceResolution))
                {
                    message = "多屏配置组虚拟源无效: " + GetCellText(row, BetaColTarget);
                    return false;
                }

                pairs.Add(new BridgePairConfig
                {
                    TargetDisplay = targetDisplay,
                    TargetResolution = targetResolution,
                    SourceResolution = sourceResolution,
                    Orientation = GetOrientationMode(GetCellText(row, BetaColOrientation)),
                    StrategyIndex = GetBetaRowStrategyIndex(row),
                    TargetSize = targetSize
                });
            }

            if (pairs.Count == 0)
            {
                message = "多屏配置组未启用";
                return false;
            }
            if (pairs.Count > MultiScreenBetaMaxTargets)
            {
                message = "多屏 BETA 当前最多支持 " + MultiScreenBetaMaxTargets.ToString(CultureInfo.InvariantCulture) + " 个配置组";
                return false;
            }
            return true;
        }

        private static bool IsBetaRowEnabled(DataGridViewRow row)
        {
            object value = row.Cells[BetaColEnabled].Value;
            return value is bool && (bool)value;
        }

        private static string GetCellText(DataGridViewRow row, int column)
        {
            object value = row.Cells[column].Value;
            return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }

        private static bool TryReadBetaTargetSpec(DataGridViewRow row, out Resolution targetResolution, out double targetSize)
        {
            targetResolution = new Resolution();
            targetSize = 0.0;
            int horizontal;
            int aspectW;
            int aspectH;
            if (!int.TryParse(GetCellText(row, BetaColHorizontal), out horizontal) ||
                horizontal <= 0 ||
                !TryParseAspectText(GetCellText(row, BetaColAspect), out aspectW, out aspectH) ||
                !TryParseSize(GetCellText(row, BetaColSize), out targetSize) ||
                targetSize <= 0.0)
            {
                return false;
            }

            int width = RoundEven(horizontal);
            int height = RoundEven(width * aspectH / (double)aspectW);
            string orientation = GetCellText(row, BetaColOrientation);
            bool portrait = string.Equals(orientation, "竖屏", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(orientation, "竖屏反向", StringComparison.OrdinalIgnoreCase);
            targetResolution = portrait
                ? new Resolution { Width = height, Height = width }
                : new Resolution { Width = width, Height = height };
            return true;
        }

        private static int GetBetaRowStrategyIndex(DataGridViewRow row)
        {
            string strategy = GetCellText(row, BetaColStrategy);
            if (string.Equals(strategy, "文字清晰优先", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(strategy, "Text clarity", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            if (string.Equals(strategy, "直接使用源", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(strategy, "Direct source", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
            return 0;
        }

        private static int GetOrientationMode(string orientation)
        {
            if (string.Equals(orientation, "竖屏", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(orientation, "Portrait", StringComparison.OrdinalIgnoreCase))
            {
                return DMDO_90;
            }
            if (string.Equals(orientation, "横屏反向", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(orientation, "Landscape flipped", StringComparison.OrdinalIgnoreCase))
            {
                return DMDO_180;
            }
            if (string.Equals(orientation, "竖屏反向", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(orientation, "Portrait flipped", StringComparison.OrdinalIgnoreCase))
            {
                return DMDO_270;
            }
            return DMDO_DEFAULT;
        }

        private static void BuildResolutionParts(Resolution resolution, out int horizontal, out string aspect, out string orientation)
        {
            bool portrait = resolution.Height > resolution.Width;
            horizontal = portrait ? resolution.Height : resolution.Width;
            int aspectW = Math.Max(resolution.Width, resolution.Height);
            int aspectH = Math.Min(resolution.Width, resolution.Height);
            int divisor = GreatestCommonDivisor(aspectW, aspectH);
            aspect = (aspectW / divisor).ToString(CultureInfo.InvariantCulture) + ":" + (aspectH / divisor).ToString(CultureInfo.InvariantCulture);
            orientation = portrait ? "竖屏" : "横屏";
        }

        private static string GuessTargetSize(DisplayChoice display)
        {
            Resolution resolution;
            if (TryParseResolution(display.Resolution, out resolution))
            {
                int max = Math.Max(resolution.Width, resolution.Height);
                if (max >= 7680)
                {
                    return "32";
                }
                if (max >= 5120)
                {
                    return "27";
                }
                if (max >= 3840)
                {
                    return "27";
                }
                if (max >= 2560)
                {
                    return "24";
                }
            }
            return "24";
        }

        private void SelectDefaultDisplays(string previousSourceDevice, string previousTargetDevice)
        {
            SelectSourceByResolutionOrFirst(previousSourceDevice, sourceText.Text.Trim());
            SelectTargetByResolutionOrFirst(previousTargetDevice, targetResolutionText.Text.Trim());
        }

        private void SelectSourceByResolutionOrFirst(string previousDevice, string resolution)
        {
            if (SelectComboByDevice(sourceDisplayCombo, previousDevice))
            {
                return;
            }

            for (int i = 0; i < sourceDisplayCombo.Items.Count; ++i)
            {
                DisplayChoice display = sourceDisplayCombo.Items[i] as DisplayChoice;
                if (display != null && string.Equals(display.Resolution, resolution, StringComparison.OrdinalIgnoreCase))
                {
                    sourceDisplayCombo.SelectedIndex = i;
                    return;
                }
            }
            sourceDisplayCombo.SelectedIndex = sourceDisplayCombo.Items.Count > 0 ? 0 : -1;
        }

        private bool SelectComboByDevice(ComboBox combo, string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return false;
            }
            for (int i = 0; i < combo.Items.Count; ++i)
            {
                DisplayChoice display = combo.Items[i] as DisplayChoice;
                if (display != null && string.Equals(display.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return true;
                }
            }
            return false;
        }

        private void SelectTargetByResolutionOrFirst(string previousDevice, string resolution)
        {
            if (SelectComboByDevice(targetDisplayCombo, previousDevice))
            {
                return;
            }

            int firstResolutionMatch = -1;
            int nonPrimaryResolutionMatch = -1;
            for (int i = 0; i < targetDisplayCombo.Items.Count; ++i)
            {
                DisplayChoice display = targetDisplayCombo.Items[i] as DisplayChoice;
                if (display != null && string.Equals(display.Resolution, resolution, StringComparison.OrdinalIgnoreCase))
                {
                    if (firstResolutionMatch < 0)
                    {
                        firstResolutionMatch = i;
                    }
                    if (!display.Primary && nonPrimaryResolutionMatch < 0)
                    {
                        nonPrimaryResolutionMatch = i;
                    }
                }
            }
            if (nonPrimaryResolutionMatch >= 0)
            {
                targetDisplayCombo.SelectedIndex = nonPrimaryResolutionMatch;
                return;
            }
            if (firstResolutionMatch >= 0)
            {
                targetDisplayCombo.SelectedIndex = firstResolutionMatch;
                return;
            }

            int smallestIndex = -1;
            long smallestArea = long.MaxValue;
            for (int i = 0; i < targetDisplayCombo.Items.Count; ++i)
            {
                DisplayChoice display = targetDisplayCombo.Items[i] as DisplayChoice;
                Resolution parsed;
                if (display != null && TryParseResolution(display.Resolution, out parsed))
                {
                    long area = (long)parsed.Width * parsed.Height;
                    if (area < smallestArea)
                    {
                        smallestArea = area;
                        smallestIndex = i;
                    }
                }
            }
            targetDisplayCombo.SelectedIndex = smallestIndex >= 0 ? smallestIndex : (targetDisplayCombo.Items.Count > 0 ? 0 : -1);
        }

        private void SyncSelectedDisplaysToSelectors()
        {
            DisplayChoice sourceDisplay = sourceDisplayCombo.SelectedItem as DisplayChoice;
            DisplayChoice targetDisplay = targetDisplayCombo.SelectedItem as DisplayChoice;
            if (sourceDisplay != null && strategyCombo.SelectedIndex == 2)
            {
                sourceText.Text = sourceDisplay.DeviceName;
            }
            else if (strategyCombo.SelectedIndex != 2)
            {
                ApplyStrategy(false);
            }
            if (targetDisplay != null)
            {
                targetText.Text = targetDisplay.DeviceName;
                targetResolutionText.Text = targetDisplay.Resolution;
                if (!updatingConfigurationInputs && configInputTabs.TabPages.Count > 0 && configInputTabs.SelectedIndex == 1)
                {
                    PopulateManualTargetFromResolution(targetDisplay.Resolution);
                }
            }
        }

        private void PopulateManualTargetFromResolution(string resolutionText)
        {
            Resolution resolution;
            if (!TryParseResolution(resolutionText, out resolution) || resolution.Width <= 0 || resolution.Height <= 0)
            {
                return;
            }
            updatingConfigurationInputs = true;
            try
            {
                bool portrait = resolution.Height > resolution.Width;
                int horizontal = portrait ? resolution.Height : resolution.Width;
                int aspectW = Math.Max(resolution.Width, resolution.Height);
                int aspectH = Math.Min(resolution.Width, resolution.Height);
                int divisor = GreatestCommonDivisor(aspectW, aspectH);
                manualTargetHorizontalText.Text = horizontal.ToString(CultureInfo.InvariantCulture);
                manualTargetAspectText.Text = (aspectW / divisor).ToString(CultureInfo.InvariantCulture) + ":" + (aspectH / divisor).ToString(CultureInfo.InvariantCulture);
                manualTargetOrientationCombo.SelectedIndex = portrait ? 1 : 0;
            }
            finally
            {
                updatingConfigurationInputs = false;
            }
        }

        private void ApplyStrategy(bool log)
        {
            if (!updatingConfigurationInputs)
            {
                SyncConfigurationInputsFromMode(false);
            }

            Resolution primary;
            Resolution target;
            double primarySize;
            double targetSize;
            if (!TryParseResolution(primaryResolutionText.Text, out primary) ||
                !TryParseResolution(targetResolutionText.Text, out target) ||
                !TryParseSize(primarySizeText.Text, out primarySize) ||
                !TryParseSize(targetSizeText.Text, out targetSize) ||
                primary.Width <= 0 || primary.Height <= 0 ||
                target.Width <= 0 || target.Height <= 0 ||
                primarySize <= 0.0 || targetSize <= 0.0)
            {
                if (log)
                {
                    AppendLog("参数无效");
                }
                return;
            }

            if (strategyCombo.SelectedIndex == 2)
            {
                sourceText.ReadOnly = false;
                DisplayChoice selectedTarget = targetDisplayCombo.SelectedItem as DisplayChoice;
                targetText.Text = selectedTarget != null ? selectedTarget.DeviceName : FormatResolution(target);
                return;
            }

            sourceText.ReadOnly = true;
            Resolution source;
            if (strategyCombo.SelectedIndex == 1)
            {
                source = CalculateQualitySource(primary, target, primarySize, targetSize);
            }
            else
            {
                source = CalculatePhysicalSource(primary, target, primarySize, targetSize);
            }

            DisplayChoice selectedSource = sourceDisplayCombo.SelectedItem as DisplayChoice;
            DisplayChoice selectedTargetDisplay = targetDisplayCombo.SelectedItem as DisplayChoice;
            sourceText.Text = FormatResolution(source);
            targetText.Text = selectedTargetDisplay != null ? selectedTargetDisplay.DeviceName : FormatResolution(target);
            if (log)
            {
                AppendLog("源显示器 = " + sourceText.Text);
            }
            RecalculateBetaPairGrid(false);
            UpdateStatus();
        }

        private void SyncTargetSelector()
        {
            DisplayChoice selectedTarget = targetDisplayCombo.SelectedItem as DisplayChoice;
            if (selectedTarget != null)
            {
                targetText.Text = selectedTarget.DeviceName;
            }
            else if (strategyCombo.SelectedIndex != 2)
            {
                targetText.Text = targetResolutionText.Text.Trim();
            }
            UpdateStatus();
        }

        private void ApplyConfigurationChanges()
        {
            ApplyStrategy(true);
            UpdateStatus();
            bool running = IsBridgeRunning();
            if (running)
            {
                UpdateConfigLock();
                AppendLog(T("运行中配置已锁定"));
                return;
            }

            RefreshDisplays();
            RecalculateBetaPairGrid(true);
            AppendLog(T("配置已应用"));
        }

        private static Resolution CalculatePhysicalSource(Resolution primary, Resolution target, double primarySize, double targetSize)
        {
            double ratio = targetSize / primarySize;
            int width = RoundEven(primary.Width * ratio);
            int height = RoundEven(width * target.Height / (double)target.Width);
            return new Resolution { Width = Math.Max(width, 1), Height = Math.Max(height, 1) };
        }

        private static Resolution CalculateQualitySource(Resolution primary, Resolution target, double primarySize, double targetSize)
        {
            Resolution physical = CalculatePhysicalSource(primary, target, primarySize, targetSize);
            int bestScale = 1;
            int bestError = int.MaxValue;
            for (int scale = 1; scale <= 4; ++scale)
            {
                int width = target.Width * scale;
                int error = Math.Abs(width - physical.Width);
                if (error < bestError)
                {
                    bestError = error;
                    bestScale = scale;
                }
            }
            return new Resolution { Width = target.Width * bestScale, Height = target.Height * bestScale };
        }

        private bool TryCalculateStrategySource(out Resolution source)
        {
            source = new Resolution();
            if (!updatingConfigurationInputs)
            {
                SyncConfigurationInputsFromMode(false);
            }

            Resolution primary;
            Resolution target;
            double primarySize;
            double targetSize;
            if (!TryParseResolution(primaryResolutionText.Text, out primary) ||
                !TryParseResolution(targetResolutionText.Text, out target) ||
                !TryParseSize(primarySizeText.Text, out primarySize) ||
                !TryParseSize(targetSizeText.Text, out targetSize) ||
                primary.Width <= 0 || primary.Height <= 0 ||
                target.Width <= 0 || target.Height <= 0 ||
                primarySize <= 0.0 || targetSize <= 0.0)
            {
                return false;
            }

            source = strategyCombo.SelectedIndex == 1
                ? CalculateQualitySource(primary, target, primarySize, targetSize)
                : CalculatePhysicalSource(primary, target, primarySize, targetSize);
            return true;
        }

        private static bool IsExact2x(Resolution source, Resolution target)
        {
            return source.Width == target.Width * 2 && source.Height == target.Height * 2;
        }

        private static int RoundEven(double value)
        {
            int rounded = (int)Math.Round(value);
            return (rounded % 2 == 0) ? rounded : rounded + 1;
        }

        private static int GreatestCommonDivisor(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                int t = a % b;
                a = b;
                b = t;
            }
            return Math.Max(a, 1);
        }

        private static bool TryParseResolution(string text, out Resolution resolution)
        {
            resolution = new Resolution();
            string[] parts = text.Trim().ToLowerInvariant().Split('x');
            if (parts.Length != 2)
            {
                return false;
            }
            int width;
            int height;
            if (!int.TryParse(parts[0].Trim(), out width) || !int.TryParse(parts[1].Trim(), out height))
            {
                return false;
            }
            resolution.Width = width;
            resolution.Height = height;
            return true;
        }

        private static bool TryParseSize(string text, out double value)
        {
            string normalized = text.Trim().Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseAspectText(string text, out int width, out int height)
        {
            width = 0;
            height = 0;
            string normalized = text.Trim().Replace('：', ':').Replace('/', ':');
            string[] parts = normalized.Split(':');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0].Trim(), out width) ||
                !int.TryParse(parts[1].Trim(), out height) ||
                width <= 0 ||
                height <= 0)
            {
                return false;
            }
            return true;
        }

        private static string FormatResolution(Resolution resolution)
        {
            return resolution.Width + "x" + resolution.Height;
        }

        private void StartBridge()
        {
            if (IsBridgeRunning())
            {
                return;
            }
            bool streamOnly = streamModeCheck.Checked;
            bool multiBeta = multiScreenBetaCheck.Checked;
            bool manageVirtualDisplay = deviceHostCheck.Checked || streamOnly;
            if (multiBeta)
            {
                manageVirtualDisplay = true;
            }
            if (streamOnly && !deviceHostCheck.Checked)
            {
                deviceHostCheck.Checked = true;
            }
            if (multiBeta && !deviceHostCheck.Checked)
            {
                deviceHostCheck.Checked = true;
            }
            if (manageVirtualDisplay && !IsRunningAsAdministrator())
            {
                AppendLog("需要管理员权限创建虚拟显示器，请关闭后以管理员身份重新运行 GUI。");
                return;
            }
            if (!File.Exists(nativeExe))
            {
                AppendLog("找不到 SBMSNative.exe");
                return;
            }
            if (manageVirtualDisplay && !File.Exists(deviceHostExe))
            {
                AppendLog("找不到 SBMSDeviceHost.exe");
                return;
            }

            List<BridgePairConfig> betaPairs = new List<BridgePairConfig>();
            if (multiBeta)
            {
                string betaMessage;
                if (!TryGetEnabledBridgePairs(streamOnly, out betaPairs, out betaMessage))
                {
                    AppendLog(betaMessage);
                    return;
                }
            }

            string requestedSource = sourceText.Text.Trim();
            Resolution calculatedSource;
            if (!multiBeta && manageVirtualDisplay && strategyCombo.SelectedIndex != 2 && TryCalculateStrategySource(out calculatedSource))
            {
                requestedSource = FormatResolution(calculatedSource);
                sourceText.Text = requestedSource;
            }
            string sourceSelector = requestedSource;
            string sourceResolutionForFilter = requestedSource;
            DisplayChoice selectedTargetForArgs = targetDisplayCombo.SelectedItem as DisplayChoice;
            string targetResolutionForFilter = selectedTargetForArgs != null ? selectedTargetForArgs.Resolution : targetResolutionText.Text.Trim();

            if (manageVirtualDisplay)
            {
                StartDeviceHost();
                if (multiBeta)
                {
                    List<DisplayChoice> virtualSources;
                    if (!WaitForVirtualSources(betaPairs.Count, 30000, out virtualSources))
                    {
                        AppendLog("等待多屏 BETA 虚拟显示器超时，needed=" + betaPairs.Count.ToString(CultureInfo.InvariantCulture));
                        StopDeviceHost();
                        return;
                    }

                    for (int i = 0; i < betaPairs.Count && i < virtualSources.Count; ++i)
                    {
                        string modeMessage;
                        if (TryApplyDisplayMode(virtualSources[i].DeviceName, betaPairs[i].SourceResolution, virtualSources[i].Refresh, betaPairs[i].Orientation, out modeMessage))
                        {
                            AppendLog(modeMessage);
                        }
                        else
                        {
                            AppendLog(modeMessage);
                        }
                    }
                    Thread.Sleep(500);
                    RefreshDisplays();
                    virtualSources = GetCurrentVirtualSources(betaPairs.Count);

                    if (virtualSources.Count < betaPairs.Count)
                    {
                        AppendLog("多屏 BETA 虚拟源数量不足，virtual=" + virtualSources.Count.ToString(CultureInfo.InvariantCulture) + " groups=" + betaPairs.Count.ToString(CultureInfo.InvariantCulture));
                        StopDeviceHost();
                        return;
                    }

                    sourceText.Text = virtualSources[0].DeviceName;
                    if (streamOnly)
                    {
                        process = null;
                        stoppingRequested = false;
                        nativeRestartCount = 0;
                        lastNativeArgs = "";
                        SetRunning(true);
                        AppendLog("多屏串流模式已启动: " + betaPairs.Count.ToString(CultureInfo.InvariantCulture) + " 个虚拟源");
                        return;
                    }

                    if (!StartMultiScreenBeta(virtualSources, betaPairs))
                    {
                        StopDeviceHost();
                        SetRunning(false);
                        return;
                    }
                    SetRunning(true);
                    AppendLog("多屏 BETA 已启动: " + betaPairs.Count.ToString(CultureInfo.InvariantCulture) + " 组");
                    return;
                }

                DisplayChoice virtualSource;
                if (!WaitForAnyVirtualSource(30000, out virtualSource))
                {
                    AppendLog("等待虚拟显示器超时，requested=" + requestedSource);
                    if (deviceHostProcess != null && deviceHostProcess.HasExited)
                    {
                        AppendLog("虚拟显示器 host 已退出，exit=" + deviceHostProcess.ExitCode);
                        if (deviceHostLog.Length > 0)
                        {
                            AppendLog(deviceHostLog.ToString().TrimEnd());
                        }
                    }
                    StopDeviceHost();
                    return;
                }
                AppendLog("虚拟显示器已就位: " + virtualSource);

                Resolution requestedResolution;
                if (TryParseResolution(requestedSource, out requestedResolution) &&
                    !string.Equals(virtualSource.Resolution, requestedSource, StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog("请求虚拟模式: " + requestedSource);
                    string modeMessage;
                    if (TryApplyDisplayMode(virtualSource.DeviceName, requestedResolution, virtualSource.Refresh, GetSelectedDisplayOrientation(), out modeMessage))
                    {
                        AppendLog(modeMessage);
                        DisplayChoice switchedSource;
                        if (WaitForVirtualSource(requestedSource, 5000, out switchedSource))
                        {
                            virtualSource = switchedSource;
                            AppendLog("虚拟模式已确认: " + virtualSource);
                        }
                        else
                        {
                            RefreshDisplays();
                            DisplayChoice refreshedSource;
                            if (TryGetSelectedOrFirstVirtualSource(virtualSource.DeviceName, out refreshedSource))
                            {
                                virtualSource = refreshedSource;
                            }
                            AppendLog("虚拟模式切换后未确认到目标分辨率，当前: " + virtualSource.Resolution);
                        }
                    }
                    else
                    {
                        AppendLog(modeMessage);
                        AppendLog("使用当前虚拟模式继续: " + virtualSource.Resolution);
                    }
                }

                sourceSelector = virtualSource.DeviceName;
                sourceResolutionForFilter = virtualSource.Resolution;
                sourceText.Text = sourceSelector;
                RefreshDisplays();
                SelectComboByDevice(sourceDisplayCombo, sourceSelector);
            }

            if (streamOnly)
            {
                lastNativeArgs = "";
                stoppingRequested = false;
                nativeRestartCount = 0;
                AppendLog("串流模式已启动：仅创建虚拟桌面，未启动 native 输出");
                SetRunning(true);
                return;
            }

            var args = new StringBuilder();
            args.Append("--source ").Append(Quote(sourceSelector));
            args.Append(" --target ").Append(Quote(targetText.Text.Trim()));
            args.Append(" --filter ").Append(Quote(GetFilterArgument(sourceResolutionForFilter, targetResolutionForFilter)));
            if (!inputCheck.Checked)
            {
                args.Append(" --no-input");
            }
            if (!windowMoveCheck.Checked)
            {
                args.Append(" --no-window-move");
            }
            if (vsyncCheck.Checked)
            {
                args.Append(" --vsync");
            }
            AppendSelectedDisplayLog();
            lastNativeArgs = args.ToString();
            stoppingRequested = false;
            nativeRestartCount = 0;
            AppendLog("native 参数: " + lastNativeArgs);
            StartNativeProcess(lastNativeArgs, false);
            SetRunning(true);
            AppendLog("已启动");
        }

        private void StartNativeProcess(string args, bool restarted)
        {
            process = CreateProcess(args);
            Process startedProcess = process;
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnOutput;
            process.Exited += delegate
            {
                BeginInvoke((Action)delegate
                {
                    int exitCode = GetProcessExitCode(startedProcess);
                    AppendLog("native 进程已退出 exit=" + exitCode);
                    if (process != startedProcess)
                    {
                        return;
                    }
                    if (stoppingRequested)
                    {
                        StopDeviceHost();
                        SetRunning(false);
                        return;
                    }
                    if (!stoppingRequested && exitCode == NativeTopologyChangedExitCode && nativeRestartCount < 5)
                    {
                        ++nativeRestartCount;
                        AppendLog("检测到显示拓扑变化，重启 native 输出 " + nativeRestartCount + "/5");
                        RefreshDisplays();
                        if (deviceHostCheck.Checked && !WaitForSourceDisplay(sourceText.Text.Trim(), 10000))
                        {
                            AppendLog("重启前等待虚拟显示器超时");
                            StopDeviceHost();
                            SetRunning(false);
                            return;
                        }
                        StartNativeProcess(lastNativeArgs, true);
                        return;
                    }
                    StopDeviceHost();
                    SetRunning(false);
                });
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            AppendLog(restarted ? "native 已重启" : "native 已启动");
        }

        private bool StartMultiScreenBeta(List<DisplayChoice> virtualSources, List<BridgePairConfig> pairs)
        {
            StopBetaProcesses();
            stoppingRequested = false;
            nativeRestartCount = 0;
            lastNativeArgs = "";
            process = null;

            int count = Math.Min(virtualSources.Count, pairs.Count);
            for (int i = 0; i < count; ++i)
            {
                DisplayChoice source = virtualSources[i];
                BridgePairConfig pair = pairs[i];
                DisplayChoice target = pair.TargetDisplay;
                if (target == null)
                {
                    AppendLog("BETA[" + (i + 1).ToString(CultureInfo.InvariantCulture) + "] 缺少目标显示器");
                    return false;
                }
                var args = new StringBuilder();
                args.Append("--source ").Append(Quote(source.DeviceName));
                args.Append(" --target ").Append(Quote(target.DeviceName));
                args.Append(" --filter ").Append(Quote(GetFilterArgument(FormatResolution(pair.SourceResolution), target.Resolution)));
                if (!inputCheck.Checked)
                {
                    args.Append(" --no-input");
                }
                if (!windowMoveCheck.Checked)
                {
                    args.Append(" --no-window-move");
                }
                if (vsyncCheck.Checked)
                {
                    args.Append(" --vsync");
                }
                string nativeArgs = args.ToString();
                AppendLog("BETA[" + (i + 1).ToString(CultureInfo.InvariantCulture) + "] " + source.DeviceName + " -> " + target.DeviceName + " args: " + nativeArgs);
                if (!StartBetaNativeProcess(nativeArgs, i + 1))
                {
                    stoppingRequested = true;
                    StopBetaProcesses();
                    AppendLog("多屏 BETA 启动失败，已回滚已启动的输出进程");
                    return false;
                }
            }
            return true;
        }

        private bool StartBetaNativeProcess(string args, int index)
        {
            Process betaProcess = CreateProcess(args);
            betaProcesses.Add(betaProcess);
            betaProcess.EnableRaisingEvents = true;
            betaProcess.OutputDataReceived += OnOutput;
            betaProcess.ErrorDataReceived += OnOutput;
            Process startedProcess = betaProcess;
            betaProcess.Exited += delegate
            {
                BeginInvoke((Action)delegate
                {
                    int exitCode = GetProcessExitCode(startedProcess);
                    AppendLog("beta native[" + index.ToString(CultureInfo.InvariantCulture) + "] 已退出 exit=" + exitCode.ToString(CultureInfo.InvariantCulture));
                    if (stoppingRequested)
                    {
                        return;
                    }
                    AppendLog("多屏 BETA 子进程异常退出，停止全部桥接");
                    stoppingRequested = true;
                    RemoveExitedBetaProcesses();
                    StopBetaProcesses();
                    StopDeviceHost();
                    SetRunning(false);
                });
            };
            try
            {
                betaProcess.Start();
                betaProcess.BeginOutputReadLine();
                betaProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                AppendLog("beta native[" + index.ToString(CultureInfo.InvariantCulture) + "] 启动失败: " + ex.Message);
                try
                {
                    betaProcesses.Remove(betaProcess);
                    if (!betaProcess.HasExited)
                    {
                        betaProcess.Kill();
                    }
                }
                catch
                {
                }
                return false;
            }
            AppendLog("beta native[" + index.ToString(CultureInfo.InvariantCulture) + "] 已启动");
            return true;
        }

        private void AppendSelectedDisplayLog()
        {
            DisplayChoice sourceDisplay = sourceDisplayCombo.SelectedItem as DisplayChoice;
            DisplayChoice targetDisplay = targetDisplayCombo.SelectedItem as DisplayChoice;
            AppendLog("选择源: " + (sourceDisplay != null ? sourceDisplay.ToString() : sourceText.Text.Trim()));
            AppendLog("选择目标: " + (targetDisplay != null ? targetDisplay.ToString() : targetText.Text.Trim()));
        }

        private void StartDeviceHost()
        {
            if (deviceHostProcess != null && !deviceHostProcess.HasExited)
            {
                return;
            }

            SignalDeviceHostStop();
            deviceHostLog.Length = 0;
            deviceHostProcess = new Process();
            deviceHostProcess.StartInfo.FileName = deviceHostExe;
            deviceHostProcess.StartInfo.WorkingDirectory = root;
            deviceHostProcess.StartInfo.UseShellExecute = false;
            deviceHostProcess.StartInfo.RedirectStandardOutput = true;
            deviceHostProcess.StartInfo.RedirectStandardError = true;
            deviceHostProcess.StartInfo.CreateNoWindow = true;
            deviceHostProcess.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            deviceHostProcess.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            deviceHostProcess.OutputDataReceived += OnOutput;
            deviceHostProcess.ErrorDataReceived += OnOutput;
            deviceHostProcess.OutputDataReceived += OnDeviceHostOutput;
            deviceHostProcess.ErrorDataReceived += OnDeviceHostOutput;
            deviceHostProcess.Start();
            deviceHostProcess.BeginOutputReadLine();
            deviceHostProcess.BeginErrorReadLine();
            AppendLog("虚拟显示器 host 已启动");
        }

        private bool WaitForSourceDisplay(string source, int timeoutMs)
        {
            var deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (deviceHostProcess != null && deviceHostProcess.HasExited)
                {
                    return false;
                }
                var list = CaptureNativeOutput("--list");
                DisplayChoice display;
                if (TryFindVirtualSource(source, list, out display))
                {
                    RefreshDisplays();
                    return true;
                }
                Thread.Sleep(500);
            }
            return false;
        }

        private bool WaitForAnyVirtualSource(int timeoutMs, out DisplayChoice source)
        {
            var deadline = Environment.TickCount + timeoutMs;
            source = null;
            while (Environment.TickCount < deadline)
            {
                if (deviceHostProcess != null && deviceHostProcess.HasExited)
                {
                    return false;
                }
                var list = CaptureNativeOutput("--list");
                if (TryFindFirstVirtualSource(list, out source))
                {
                    RefreshDisplays();
                    return true;
                }
                Thread.Sleep(500);
            }
            return false;
        }

        private bool WaitForVirtualSources(int minimumCount, int timeoutMs, out List<DisplayChoice> sources)
        {
            var deadline = Environment.TickCount + timeoutMs;
            sources = new List<DisplayChoice>();
            while (Environment.TickCount < deadline)
            {
                if (deviceHostProcess != null && deviceHostProcess.HasExited)
                {
                    return false;
                }
                string list = CaptureNativeOutput("--list");
                sources = ParseVirtualSources(list);
                if (sources.Count >= minimumCount)
                {
                    RefreshDisplays();
                    sources = GetCurrentVirtualSources(minimumCount);
                    return sources.Count >= minimumCount;
                }
                Thread.Sleep(500);
            }
            return false;
        }

        private List<DisplayChoice> GetCurrentVirtualSources(int maximumCount)
        {
            var sources = new List<DisplayChoice>();
            for (int i = 0; i < displays.Count; ++i)
            {
                DisplayChoice display = displays[i];
                if (display.Virtual)
                {
                    sources.Add(display);
                    if (sources.Count >= maximumCount)
                    {
                        break;
                    }
                }
            }
            return sources;
        }

        private static List<DisplayChoice> ParseVirtualSources(string listOutput)
        {
            var sources = new List<DisplayChoice>();
            foreach (string rawLine in listOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                DisplayChoice display;
                if (TryParseDisplayLine(rawLine.Trim(), out display) && display.Virtual)
                {
                    sources.Add(display);
                }
            }
            return sources;
        }

        private bool WaitForVirtualSource(string selector, int timeoutMs, out DisplayChoice source)
        {
            var deadline = Environment.TickCount + timeoutMs;
            source = null;
            while (Environment.TickCount < deadline)
            {
                if (deviceHostProcess != null && deviceHostProcess.HasExited)
                {
                    return false;
                }
                var list = CaptureNativeOutput("--list");
                if (TryFindVirtualSource(selector, list, out source))
                {
                    RefreshDisplays();
                    return true;
                }
                Thread.Sleep(500);
            }
            return false;
        }

        private bool TryGetSelectedOrFirstVirtualSource(string preferredDevice, out DisplayChoice source)
        {
            source = null;
            for (int i = 0; i < displays.Count; ++i)
            {
                DisplayChoice display = displays[i];
                if (display.Virtual && string.Equals(display.DeviceName, preferredDevice, StringComparison.OrdinalIgnoreCase))
                {
                    source = display;
                    return true;
                }
            }
            for (int i = 0; i < displays.Count; ++i)
            {
                DisplayChoice display = displays[i];
                if (display.Virtual)
                {
                    source = display;
                    return true;
                }
            }
            return false;
        }

        private static bool TryFindFirstVirtualSource(string listOutput, out DisplayChoice source)
        {
            source = null;
            foreach (string rawLine in listOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                DisplayChoice display;
                if (TryParseDisplayLine(rawLine.Trim(), out display) && display.Virtual)
                {
                    source = display;
                    return true;
                }
            }
            return false;
        }

        private static bool TryFindVirtualSource(string selector, string listOutput, out DisplayChoice source)
        {
            source = null;
            foreach (string rawLine in listOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                DisplayChoice display;
                if (!TryParseDisplayLine(rawLine.Trim(), out display) || !display.Virtual)
                {
                    continue;
                }
                if (string.Equals(display.DeviceName, selector, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(display.Resolution, selector, StringComparison.OrdinalIgnoreCase))
                {
                    source = display;
                    return true;
                }
            }
            return false;
        }

        private int GetSelectedDisplayOrientation()
        {
            ComboBox orientationCombo = configInputTabs.TabPages.Count > 0 && configInputTabs.SelectedIndex == 1
                ? manualTargetOrientationCombo
                : targetOrientationPresetCombo;
            switch (orientationCombo.SelectedIndex)
            {
                case 1: return DMDO_90;
                case 2: return DMDO_180;
                case 3: return DMDO_270;
                default: return DMDO_DEFAULT;
            }
        }

        private static bool TryApplyDisplayMode(string deviceName, Resolution resolution, string refreshText, int orientation, out string message)
        {
            var devMode = new DEVMODE();
            devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref devMode))
            {
                message = "读取虚拟显示器当前模式失败: " + deviceName;
                return false;
            }

            int refresh;
            bool hasRefresh = int.TryParse(refreshText, out refresh) && refresh > 0;
            devMode.dmPelsWidth = resolution.Width;
            devMode.dmPelsHeight = resolution.Height;
            devMode.dmDisplayOrientation = orientation;
            devMode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYORIENTATION;
            if (hasRefresh)
            {
                devMode.dmDisplayFrequency = refresh;
                devMode.dmFields |= DM_DISPLAYFREQUENCY;
            }

            int result = ChangeDisplaySettingsEx(deviceName, ref devMode, IntPtr.Zero, 0, IntPtr.Zero);
            if (result == DISP_CHANGE_SUCCESSFUL)
            {
                message = "虚拟模式切换成功: " + deviceName + " -> " + FormatResolution(resolution) + (hasRefresh ? "@" + refresh : "") + " orientation=" + orientation;
                return true;
            }

            if (hasRefresh)
            {
                devMode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYORIENTATION;
                result = ChangeDisplaySettingsEx(deviceName, ref devMode, IntPtr.Zero, 0, IntPtr.Zero);
                if (result == DISP_CHANGE_SUCCESSFUL)
                {
                    message = "虚拟模式切换成功: " + deviceName + " -> " + FormatResolution(resolution) + " orientation=" + orientation;
                    return true;
                }
            }

            message = "虚拟模式切换失败: " + deviceName + " -> " + FormatResolution(resolution) + " result=" + result;
            return false;
        }

        private string CaptureNativeOutput(string args)
        {
            using (var p = CreateProcess(args))
            {
                p.Start();
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(3000);
                return stdout + Environment.NewLine + stderr;
            }
        }

        private void RunList()
        {
            if (!File.Exists(nativeExe))
            {
                AppendLog("找不到 SBMSNative.exe");
                return;
            }
            RefreshDisplays();
            var list = CreateProcess("--list");
            list.OutputDataReceived += OnOutput;
            list.ErrorDataReceived += OnOutput;
            list.Start();
            list.BeginOutputReadLine();
            list.BeginErrorReadLine();
        }

        private Process CreateProcess(string args)
        {
            var p = new Process();
            p.StartInfo.FileName = nativeExe;
            p.StartInfo.Arguments = args;
            p.StartInfo.WorkingDirectory = root;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            return p;
        }

        private void StopBetaProcesses()
        {
            for (int i = betaProcesses.Count - 1; i >= 0; --i)
            {
                Process betaProcess = betaProcesses[i];
                if (betaProcess == null)
                {
                    continue;
                }
                try
                {
                    if (!betaProcess.HasExited)
                    {
                        betaProcess.CloseMainWindow();
                        PostCloseToProcess(betaProcess.Id);
                    }
                }
                catch
                {
                }
            }

            for (int i = betaProcesses.Count - 1; i >= 0; --i)
            {
                Process betaProcess = betaProcesses[i];
                if (betaProcess == null)
                {
                    continue;
                }
                try
                {
                    if (!betaProcess.HasExited && !betaProcess.WaitForExit(3000))
                    {
                        AppendLog("beta native 正常关闭超时，强制结束");
                        betaProcess.Kill();
                    }
                }
                catch
                {
                }
            }
            betaProcesses.Clear();
        }

        private void StopBridge()
        {
            stoppingRequested = true;
            StopBetaProcesses();
            if (process == null || process.HasExited)
            {
                StopDeviceHost();
                SetRunning(false);
                return;
            }
            try
            {
                process.CloseMainWindow();
                PostCloseToProcess(process.Id);
                if (!process.WaitForExit(3000))
                {
                    AppendLog("正常关闭超时，强制结束");
                    process.Kill();
                }
            }
            catch
            {
            }
            StopDeviceHost();
            SetRunning(false);
        }

        private bool IsBridgeRunning()
        {
            return (process != null && !process.HasExited) ||
                   HasRunningBetaProcess() ||
                   (deviceHostProcess != null && !deviceHostProcess.HasExited);
        }

        private bool HasRunningBetaProcess()
        {
            for (int i = 0; i < betaProcesses.Count; ++i)
            {
                Process betaProcess = betaProcesses[i];
                try
                {
                    if (betaProcess != null && !betaProcess.HasExited)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
            return false;
        }

        private void RemoveExitedBetaProcesses()
        {
            for (int i = betaProcesses.Count - 1; i >= 0; --i)
            {
                Process betaProcess = betaProcesses[i];
                try
                {
                    if (betaProcess == null || betaProcess.HasExited)
                    {
                        betaProcesses.RemoveAt(i);
                    }
                }
                catch
                {
                    betaProcesses.RemoveAt(i);
                }
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!exiting &&
                e.CloseReason == CloseReason.UserClosing &&
                lightweightMenuItem.Checked &&
                IsBridgeRunning())
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            trayIcon.Visible = false;
            StopBridge();
        }

        private void HideToTray()
        {
            if (configForm != null)
            {
                configForm.Hide();
            }
            Hide();
            trayIcon.Visible = true;
            UpdateStatus();
            AppendLog(T("已隐藏到托盘"));
        }

        private void ShowMainWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            trayIcon.Visible = false;
            UpdateStatus();
        }

        private void ExitApplication()
        {
            exiting = true;
            trayIcon.Visible = false;
            StopBridge();
            Close();
        }

        private void StopDeviceHost()
        {
            if (deviceHostProcess == null)
            {
                return;
            }

            SignalDeviceHostStop();
            try
            {
                if (!deviceHostProcess.WaitForExit(4000))
                {
                    AppendLog("虚拟显示器 host 正常关闭超时，强制结束");
                    deviceHostProcess.Kill();
                }
            }
            catch
            {
            }
            deviceHostProcess = null;
        }

        private static void SignalDeviceHostStop()
        {
            IntPtr handle = OpenEvent(EVENT_MODIFY_STATE, false, "Local\\SBMSDeviceHostStop");
            if (handle == IntPtr.Zero)
            {
                return;
            }
            SetEvent(handle);
            CloseHandle(handle);
        }

        private static void PostCloseToProcess(int processId)
        {
            EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == (uint)processId)
                {
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);
        }

        private static bool IsRunningAsAdministrator()
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        private void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
            {
                return;
            }
            string prefix = sender == deviceHostProcess ? "[host] " :
                            sender == process ? "[native] " :
                            "[cmd] ";
            BeginInvoke((Action)delegate { AppendLog(prefix + e.Data); });
        }

        private void OnDeviceHostOutput(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
            {
                return;
            }
            lock (deviceHostLog)
            {
                deviceHostLog.AppendLine(e.Data);
            }
        }

        private void AppendLog(string text)
        {
            logText.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine);
            if (text.IndexOf("0x80070005", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                logText.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + " 需要管理员权限创建虚拟显示器，请以管理员身份运行 GUI。" + Environment.NewLine);
            }
        }

        private static int GetProcessExitCode(Process p)
        {
            try
            {
                return p != null && p.HasExited ? p.ExitCode : -1;
            }
            catch
            {
                return -1;
            }
        }

        private void SetRunning(bool running)
        {
            startButton.Enabled = !running;
            stopButton.Enabled = running;
            displayList.Enabled = true;
            sourceText.Enabled = true;
            targetText.Enabled = true;
            sourceDisplayCombo.Enabled = true;
            targetDisplayCombo.Enabled = true;
            strategyCombo.Enabled = true;
            configInputTabs.Enabled = true;
            primaryResolutionPresetCombo.Enabled = true;
            primaryAspectPresetCombo.Enabled = true;
            primaryOrientationPresetCombo.Enabled = true;
            primarySizePresetCombo.Enabled = true;
            targetResolutionPresetCombo.Enabled = true;
            targetAspectPresetCombo.Enabled = true;
            targetOrientationPresetCombo.Enabled = true;
            targetSizePresetCombo.Enabled = true;
            primaryResolutionText.Enabled = true;
            primarySizeText.Enabled = true;
            targetResolutionText.Enabled = true;
            targetSizeText.Enabled = true;
            manualBaseHorizontalText.Enabled = true;
            manualBaseAspectText.Enabled = true;
            manualBaseOrientationCombo.Enabled = true;
            manualBaseSizeText.Enabled = true;
            manualTargetHorizontalText.Enabled = true;
            manualTargetAspectText.Enabled = true;
            manualTargetOrientationCombo.Enabled = true;
            manualTargetSizeText.Enabled = true;
            filterCombo.Enabled = true;
            calculateButton.Enabled = true;
            applyConfigButton.Enabled = true;
            inputCheck.Enabled = true;
            windowMoveCheck.Enabled = true;
            deviceHostCheck.Enabled = true;
            streamModeCheck.Enabled = true;
            multiScreenBetaCheck.Enabled = true;
            betaPairGrid.Enabled = true;
            addBetaGroupButton.Enabled = true;
            removeBetaGroupButton.Enabled = true;
            vsyncCheck.Enabled = true;
            UpdateRuntimeOptionState();
            UpdateConfigLock();
            UpdateStatus();
        }

        private string GetFilterArgument()
        {
            return GetFilterArgument(sourceText.Text, targetText.Text);
        }

        private string GetFilterArgument(string sourceResolutionText, string targetResolutionTextValue)
        {
            switch (filterCombo.SelectedIndex)
            {
                case 1:
                    return "linear";
                case 2:
                    return "point";
                case 3:
                    return "box2x";
                default:
                    Resolution source;
                    Resolution target;
                    if (TryParseResolution(sourceResolutionText, out source) &&
                        TryParseResolution(targetResolutionTextValue, out target) &&
                        IsExact2x(source, target))
                    {
                        return "box2x";
                    }
                    return "linear";
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
