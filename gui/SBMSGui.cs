using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

namespace SBMSGui
{
    public sealed class GuiConfigBridgePair
    {
        public bool Enabled;
        public string Mode;
        public string Target;
        public string Horizontal;
        public string Aspect;
        public string Orientation;
        public string Size;
        public string Strategy;
        public string Refresh;
        public string Source;
    }

    public sealed class GuiConfigFile
    {
        public int Version;
        public string SavedByBuild;
        public bool English;
        public bool LightweightMode;
        public int ConfigTabIndex;
        public int StrategyIndex;
        public int FilterIndex;
        public string SourceText;
        public string TargetText;
        public string SingleRefresh;
        public string SelectedSourceDevice;
        public string SelectedTargetDevice;
        public string PrimaryResolution;
        public string PrimarySize;
        public string TargetResolution;
        public string TargetSize;
        public int PrimaryResolutionPresetIndex;
        public int PrimaryAspectPresetIndex;
        public int PrimaryOrientationPresetIndex;
        public int PrimarySizePresetIndex;
        public int TargetResolutionPresetIndex;
        public int TargetAspectPresetIndex;
        public int TargetOrientationPresetIndex;
        public int TargetSizePresetIndex;
        public string ManualBaseHorizontal;
        public string ManualBaseAspect;
        public int ManualBaseOrientationIndex;
        public string ManualBaseSize;
        public string ManualTargetHorizontal;
        public string ManualTargetAspect;
        public int ManualTargetOrientationIndex;
        public string ManualTargetSize;
        public bool StreamMode;
        public bool InputMapping;
        public bool WindowMove;
        public bool DeviceHost;
        public bool VSync;
        // Issue #7: persisted rollback switch for the BETA mode that absorbs
        // valid Windows Settings display edits during topology recovery.
        public bool FollowWindowsTopologyBeta;
        public int SelectedBetaGroupIndex;
        public List<GuiConfigBridgePair> BetaPairs;

        public GuiConfigFile()
        {
            Version = 1;
            FollowWindowsTopologyBeta = true;
            BetaPairs = new List<GuiConfigBridgePair>();
        }
    }

    internal sealed class MainForm : Form
    {
        private const int WM_CLOSE = 0x0010;
        private const uint EVENT_MODIFY_STATE = 0x0002;
        private const int NativeTopologyChangedExitCode = 100;
        private const int NativeSourceUnavailableExitCode = 101;
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
        private const string BuildLabel = "2026-06-29.069-beta";
        private const int WM_SETREDRAW = 0x000B;
        private const int MultiScreenBetaMaxTargets = 2;
        private const int DisplayTopologySettleTimeoutMs = 7000;
        private const int DisplayTopologyStableSamples = 2;
        private const int BetaColEnabled = 0;
        private const int BetaColMode = 1;
        private const int BetaColTarget = 2;
        private const int BetaColHorizontal = 3;
        private const int BetaColAspect = 4;
        private const int BetaColOrientation = 5;
        private const int BetaColSize = 6;
        private const int BetaColStrategy = 7;
        private const int BetaColRefresh = 8;
        private const int BetaColSource = 9;

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

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly TextBox sourceText = new TextBox();
        private readonly TextBox targetText = new TextBox();
        private readonly TextBox singleRefreshText = new TextBox();
        private readonly ComboBox sourceDisplayCombo = new DarkComboBox();
        private readonly ComboBox targetDisplayCombo = new DarkComboBox();
        private readonly ListBox displayList = new ListBox();
        private readonly ComboBox strategyCombo = new DarkComboBox();
        private readonly ComboBox primaryResolutionPresetCombo = new DarkComboBox();
        private readonly ComboBox primaryAspectPresetCombo = new DarkComboBox();
        private readonly ComboBox primaryOrientationPresetCombo = new DarkComboBox();
        private readonly ComboBox primarySizePresetCombo = new DarkComboBox();
        private readonly ComboBox targetResolutionPresetCombo = new DarkComboBox();
        private readonly ComboBox targetAspectPresetCombo = new DarkComboBox();
        private readonly ComboBox targetOrientationPresetCombo = new DarkComboBox();
        private readonly ComboBox targetSizePresetCombo = new DarkComboBox();
        private readonly TextBox primaryResolutionText = new TextBox();
        private readonly TextBox primarySizeText = new TextBox();
        private readonly TextBox targetResolutionText = new TextBox();
        private readonly TextBox targetSizeText = new TextBox();
        private readonly TabControl configInputTabs = new DarkTabControl();
        private readonly TabPage presetConfigPage = new TabPage();
        private readonly TabPage manualConfigPage = new TabPage();
        private readonly TextBox manualBaseHorizontalText = new TextBox();
        private readonly TextBox manualBaseAspectText = new TextBox();
        private readonly ComboBox manualBaseOrientationCombo = new DarkComboBox();
        private readonly TextBox manualBaseSizeText = new TextBox();
        private readonly TextBox manualTargetHorizontalText = new TextBox();
        private readonly TextBox manualTargetAspectText = new TextBox();
        private readonly ComboBox manualTargetOrientationCombo = new DarkComboBox();
        private readonly TextBox manualTargetSizeText = new TextBox();
        private readonly ComboBox filterCombo = new DarkComboBox();
        private readonly CheckBox inputCheck = new CheckBox();
        private readonly CheckBox windowMoveCheck = new CheckBox();
        private readonly CheckBox deviceHostCheck = new CheckBox();
        private readonly CheckBox streamModeCheck = new CheckBox();
        private readonly CheckBox followWindowsTopologyCheck = new CheckBox();
        private readonly TabControl betaGroupTabs = new DarkTabControl();
        private readonly DataGridView betaPairGrid = new DataGridView();
        private readonly GlowButton addBetaGroupButton = new GlowButton();
        private readonly GlowButton removeBetaGroupButton = new GlowButton();
        private readonly CheckBox vsyncCheck = new CheckBox();
        private readonly GlowButton calculateButton = new GlowButton();
        private readonly GlowButton applyConfigButton = new GlowButton();
        private readonly GlowButton startButton = new GlowButton();
        private readonly GlowButton stopButton = new GlowButton();
        private readonly GlowButton listButton = new GlowButton();
        private readonly GlowButton configButton = new GlowButton();
        private readonly GlowButton startupButton = new GlowButton();
        private readonly GlowButton languageButton = new GlowButton();
        private readonly GlowButton lightweightButton = new GlowButton();
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
        private readonly ContextMenuStrip languagePopup = new ContextMenuStrip();
        private readonly ToolStripMenuItem trayOpenMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem trayStopMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem trayExitMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem popupEnglishMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem popupChineseMenuItem = new ToolStripMenuItem();
        private readonly System.Windows.Forms.Timer languagePopupCloseTimer = new System.Windows.Forms.Timer();
        private readonly Label statusLabel = new Label();
        private readonly Label routeLabel = new Label();
        private readonly Panel configInlineHost = new BufferedPanel();
        private readonly Panel configLockPanel = new BufferedPanel();
        private readonly Label configLockLabel = new Label();
        private readonly GlowButton configLockBackButton = new GlowButton();
        private RowStyle configInlineRowStyle;

        private Process process;
        private readonly List<Process> betaProcesses = new List<Process>();
        private Process deviceHostProcess;
        private readonly StringBuilder deviceHostLog = new StringBuilder();
        private string lastNativeArgs = "";
        private string lastManagedVirtualResolution = "";
        private string lastManagedVirtualRefresh = "";
        private int lastManagedVirtualOrientation = DMDO_DEFAULT;
        private bool stoppingRequested;
        private bool restartingAfterTopologyChange;
        private bool bridgeStarting;
        private readonly string root;
        private readonly string nativeExe;
        private readonly string deviceHostExe;
        private readonly string userDataDir;
        private readonly string configPath;
        private readonly string logDirectory;
        private readonly string sessionLogPath;
        private readonly string latestLogPath;
        private readonly string errorLogPath;
        private readonly System.Windows.Forms.Timer configurationSaveTimer = new System.Windows.Forms.Timer();
        private readonly List<DisplayChoice> displays = new List<DisplayChoice>();
        private Form configForm;
        private bool english;
        private bool exiting;
        private bool loadingConfiguration;
        private bool configurationPersistenceReady;
        private bool configurationFileLoaded;
        private bool updatingPresetCombos;
        private bool updatingConfigurationInputs;
        private bool updatingBetaPairGrid;
        private bool rebuildingGroupTabs;
        private bool groupTabsEventsBound;
        private bool suppressStreamModePrompt;
        private bool suppressBetaGroupTabChange;
        private bool forceConfigLockForProbe;
        private int selectedBetaGroupIndex;
        private string pendingConfigSourceDevice = "";
        private string pendingConfigTargetDevice = "";
        private string pendingConfigLoadMessage = "";

        private static readonly Color ThemeBack = Color.FromArgb(0, 10, 4);
        private static readonly Color ThemeText = Color.White;
        private static readonly Color ThemeActive = Color.FromArgb(72, 255, 0);
        private static readonly Color ThemeRed = Color.Red;
        private static readonly Color ThemePanel = ThemeBack;
        private static readonly Color ThemePanel2 = ThemeBack;
        private static readonly Color ThemeGreen = ThemeActive;
        private static readonly Color ThemeMuted = ThemeText;

        private struct Resolution
        {
            public int Width;
            public int Height;
        }

        private sealed class DisplayModeCandidate
        {
            public Resolution Resolution;
            public int Refresh;
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
            public string SunshineId;
            public int Orientation;
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

        private sealed class DisplayRuntimeMode
        {
            public Resolution Resolution;
            public string Refresh;
            public int Orientation;
        }

        private sealed class BridgePairConfig
        {
            public bool StreamOnly;
            public DisplayChoice TargetDisplay;
            public Resolution TargetResolution;
            public Resolution SourceResolution;
            public int Orientation;
            public int StrategyIndex;
            public double TargetSize;
            public string Refresh;
            public DataGridViewRow Row;
        }

        private sealed class BridgePairSnapshot
        {
            public bool Enabled;
            public string Mode;
            public string Target;
            public string Horizontal;
            public string Aspect;
            public string Orientation;
            public string Size;
            public string Strategy;
            public string Refresh;
            public string Source;
        }

        private sealed class DarkComboBox : ComboBox
        {
            private const int WM_PAINT = 0x000F;
            private const int WM_PRINT = 0x0317;
            private const int WM_PRINTCLIENT = 0x0318;

            public DarkComboBox()
            {
                FlatStyle = FlatStyle.Flat;
                DrawMode = DrawMode.OwnerDrawFixed;
            }

            protected override void OnDropDown(EventArgs e)
            {
                DropDownWidth = Math.Max(Width, MeasureDropDownWidth());
                base.OnDropDown(e);
            }

            protected override void OnSelectedIndexChanged(EventArgs e)
            {
                base.OnSelectedIndexChanged(e);
                Invalidate();
            }

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                Invalidate();
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg == WM_PAINT)
                {
                    PaintDarkChrome();
                }
                else if ((m.Msg == WM_PRINT || m.Msg == WM_PRINTCLIENT) && m.WParam != IntPtr.Zero)
                {
                    using (Graphics graphics = Graphics.FromHdc(m.WParam))
                    {
                        PaintDarkChrome(graphics);
                    }
                }
            }

            private void PaintDarkChrome()
            {
                if (!IsHandleCreated)
                {
                    return;
                }
                using (Graphics graphics = Graphics.FromHwnd(Handle))
                {
                    PaintDarkChrome(graphics);
                }
            }

            private void PaintDarkChrome(Graphics graphics)
            {
                Rectangle bounds = ClientRectangle;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return;
                }
                using (Brush background = new SolidBrush(ThemePanel))
                {
                    graphics.FillRectangle(background, bounds);
                }
                int buttonWidth = Math.Max(24, Math.Min(bounds.Height, bounds.Width));
                Rectangle button = new Rectangle(Math.Max(bounds.Left, bounds.Right - buttonWidth), bounds.Top, buttonWidth, bounds.Height);
                using (Brush background = new SolidBrush(ThemePanel))
                {
                    graphics.FillRectangle(background, button);
                }
                using (Pen border = new Pen(ThemeText))
                {
                    graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
                    graphics.DrawRectangle(border, button.X, button.Y, button.Width - 1, button.Height - 1);
                }

                string text = Text;
                Rectangle textBounds = new Rectangle(bounds.Left + 7, bounds.Top + 1, Math.Max(0, bounds.Width - buttonWidth - 12), bounds.Height - 2);
                TextRenderer.DrawText(
                    graphics,
                    text,
                    Font,
                    textBounds,
                    ThemeText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                Point center = new Point(button.Left + button.Width / 2, button.Top + button.Height / 2 + 1);
                Point[] arrow = new[]
                {
                    new Point(center.X - 4, center.Y - 2),
                    new Point(center.X + 4, center.Y - 2),
                    new Point(center.X, center.Y + 3)
                };
                using (Brush arrowBrush = new SolidBrush(ThemeText))
                {
                    graphics.FillPolygon(arrowBrush, arrow);
                }
            }

            private int MeasureDropDownWidth()
            {
                int width = Width;
                foreach (object item in Items)
                {
                    string text = Convert.ToString(item, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(text))
                    {
                        width = Math.Max(width, TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPadding).Width + 42);
                    }
                }
                return width;
            }
        }

        private sealed class BufferedPanel : Panel
        {
            public BufferedPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw, true);
            }
        }

        private sealed class DarkTabControl : TabControl
        {
            public DarkTabControl()
            {
                SetStyle(ControlStyles.UserPaint |
                         ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw, true);
            }

            protected override void OnSelectedIndexChanged(EventArgs e)
            {
                base.OnSelectedIndexChanged(e);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(ThemeBack);
                for (int i = 0; i < TabPages.Count; ++i)
                {
                    Rectangle tabBounds = GetTabRect(i);
                    bool selected = i == SelectedIndex;
                    using (Brush background = new SolidBrush(ThemeBack))
                    {
                        e.Graphics.FillRectangle(background, tabBounds);
                    }
                    using (Pen border = new Pen(selected ? ThemeActive : ThemeText))
                    {
                        e.Graphics.DrawRectangle(border, tabBounds.X, tabBounds.Y, tabBounds.Width - 1, tabBounds.Height - 1);
                    }
                    if (selected)
                    {
                        using (Pen top = new Pen(ThemeActive, 2F))
                        {
                            e.Graphics.DrawLine(top, tabBounds.Left + 1, tabBounds.Top + 1, tabBounds.Right - 2, tabBounds.Top + 1);
                        }
                    }
                    TextRenderer.DrawText(
                        e.Graphics,
                        TabPages[i].Text,
                        Font,
                        tabBounds,
                        ThemeText,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
                }

                Rectangle display = DisplayRectangle;
                if (display.Width > 0 && display.Height > 0)
                {
                    using (Brush background = new SolidBrush(ThemeBack))
                    {
                        e.Graphics.FillRectangle(background, display);
                    }
                    using (Pen border = new Pen(ThemeText))
                    {
                        e.Graphics.DrawRectangle(border, display.X, display.Y, display.Width - 1, display.Height - 1);
                    }
                }
            }
        }

        private sealed class GlowButton : Button
        {
            private bool hover;
            private bool pressed;

            public bool DangerFill { get; set; }
            public bool ActiveFill { get; set; }
            public bool Minimal { get; set; }
            public bool ShowGlyph { get; set; }

            public GlowButton()
            {
                SetStyle(ControlStyles.UserPaint |
                         ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                FlatStyle = FlatStyle.Flat;
                UseVisualStyleBackColor = false;
                BackColor = Color.Transparent;
                ForeColor = ThemeText;
                Height = 32;
                Minimal = true;
                ShowGlyph = true;
                Cursor = Cursors.Hand;
                TextAlign = ContentAlignment.MiddleCenter;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                hover = true;
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                hover = false;
                pressed = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs mevent)
            {
                pressed = true;
                Invalidate();
                base.OnMouseDown(mevent);
            }

            protected override void OnMouseUp(MouseEventArgs mevent)
            {
                pressed = false;
                Invalidate();
                base.OnMouseUp(mevent);
            }

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                UpdateMinimumWidth();
                Invalidate();
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                UpdateMinimumWidth();
                Invalidate();
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                UpdateMinimumWidth();
                Invalidate();
            }

            protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
            {
                int required = GetRequiredWidth(height);
                if (required > 0 && width < required)
                {
                    width = required;
                }
                base.SetBoundsCore(x, y, width, height, specified);
            }

            public override Size GetPreferredSize(Size proposedSize)
            {
                Size preferred = base.GetPreferredSize(proposedSize);
                int required = GetRequiredWidth(Height);
                if (required > 0)
                {
                    preferred.Width = Math.Max(preferred.Width, required);
                }
                return preferred;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                bool hot = hover || pressed || ActiveFill;
                bool filled = hot;
                Color fill = DangerFill ? ThemeRed : ThemeBack;
                Color text = DangerFill ? (hot ? ThemeText : ThemeRed) : ThemeText;
                Color corner = DangerFill ? (hot ? ThemeText : ThemeRed) : (hot ? ThemeActive : ThemeText);
                Color border = DangerFill ? ThemeRed : (hot ? ThemeActive : ThemeText);
                Rectangle rect = new Rectangle(1, 1, Width - 3, Height - 3);

                using (Brush background = new SolidBrush(GetPaintBackColor()))
                {
                    e.Graphics.FillRectangle(background, ClientRectangle);
                }
                if (filled)
                {
                    using (Brush brush = new SolidBrush(fill))
                    {
                        e.Graphics.FillRectangle(brush, rect);
                    }
                }
                using (Pen pen = new Pen(border))
                {
                    e.Graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                }
                Rectangle textBounds = rect;
                if (ShowGlyph && Width >= 48)
                {
                    DrawCornerGlyph(e.Graphics, rect, corner, hot);
                    textBounds = GetTextBounds(rect);
                }
                TextRenderer.DrawText(
                    e.Graphics,
                    Text,
                    Font,
                    textBounds,
                    text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
            }

            private Color GetPaintBackColor()
            {
                if (Parent == null || Parent.BackColor.A == 0)
                {
                    return ThemeBack;
                }
                return Parent.BackColor;
            }

            private static int GetCornerSize(Rectangle bounds)
            {
                return Math.Min(bounds.Height - 3, Math.Max(24, bounds.Height - 8));
            }

            private Rectangle GetTextBounds(Rectangle bounds)
            {
                int inset = ShowGlyph && Width >= 48 ? GetCornerSize(bounds) + 18 : 0;
                return new Rectangle(bounds.Left + inset, bounds.Top, Math.Max(0, bounds.Width - inset - 18), bounds.Height);
            }

            private int GetRequiredWidth(int height)
            {
                if (!ShowGlyph || string.IsNullOrEmpty(Text))
                {
                    return 0;
                }
                int corner = Math.Min(Math.Max(height - 6, 24), 34);
                int textWidth = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                return corner + 80 + textWidth;
            }

            private void UpdateMinimumWidth()
            {
                int required = GetRequiredWidth(Height);
                if (required <= 0)
                {
                    return;
                }
                if (MinimumSize.Width != required)
                {
                    MinimumSize = new Size(required, MinimumSize.Height);
                }
                if (Width < required)
                {
                    Width = required;
                }
                if (Parent != null)
                {
                    Parent.PerformLayout();
                }
            }

            private static void DrawCornerGlyph(Graphics graphics, Rectangle bounds, Color color, bool filled)
            {
                int corner = GetCornerSize(bounds);
                Point topLeft = new Point(bounds.Left, bounds.Top);
                Point topJoin = new Point(bounds.Left + corner, bounds.Top);
                Point leftJoin = new Point(bounds.Left, bounds.Top + corner);

                if (filled)
                {
                    using (Brush brush = new SolidBrush(color))
                    {
                        graphics.FillPolygon(brush, new[] { topLeft, topJoin, leftJoin });
                    }
                    return;
                }

                using (Pen pen = new Pen(color, 2.4F))
                {
                    pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Miter;
                    graphics.DrawLine(pen, topLeft, topJoin);
                    graphics.DrawLine(pen, topLeft, leftJoin);
                    graphics.DrawLine(pen, leftJoin, topJoin);
                }
            }
        }

        private sealed class MinimalMenuRenderer : ToolStripRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (Brush brush = new SolidBrush(ThemeBack))
                {
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
                }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                Rectangle rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                using (Pen pen = new Pen(ThemeText))
                {
                    e.Graphics.DrawRectangle(pen, rect);
                }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                ToolStripMenuItem item = e.Item as ToolStripMenuItem;
                bool active = e.Item.Selected || (item != null && item.Checked);
                Rectangle rect = new Rectangle(1, 1, e.Item.Width - 2, e.Item.Height - 2);
                using (Brush brush = new SolidBrush(active ? ThemeActive : ThemeBack))
                {
                    e.Graphics.FillRectangle(brush, rect);
                }
                using (Pen pen = new Pen(ThemeText))
                {
                    e.Graphics.DrawRectangle(pen, rect);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                ToolStripMenuItem item = e.Item as ToolStripMenuItem;
                bool active = e.Item.Selected || (item != null && item.Checked);
                Rectangle rect = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
                TextRenderer.DrawText(
                    e.Graphics,
                    e.Text,
                    e.TextFont,
                    rect,
                    active ? ThemeBack : ThemeText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                ToolStripMenuItem item = e.Item as ToolStripMenuItem;
                bool active = e.Item.Selected || (item != null && item.Checked);
                Color color = active ? ThemeBack : ThemeText;
                Rectangle bounds = e.ArrowRectangle;
                Point center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                Point[] arrow = new[]
                {
                    new Point(center.X - 2, center.Y - 5),
                    new Point(center.X - 2, center.Y + 5),
                    new Point(center.X + 4, center.Y)
                };
                using (Brush brush = new SolidBrush(color))
                {
                    e.Graphics.FillPolygon(brush, arrow);
                }
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Rectangle rect = new Rectangle(4, 4, Math.Max(16, e.Item.Height - 8), Math.Max(16, e.Item.Height - 8));
                using (Pen pen = new Pen(ThemeText))
                {
                    e.Graphics.DrawRectangle(pen, rect);
                    e.Graphics.DrawLine(pen, rect.Left + 4, rect.Top + rect.Height / 2, rect.Left + rect.Width / 2 - 1, rect.Bottom - 5);
                    e.Graphics.DrawLine(pen, rect.Left + rect.Width / 2 - 1, rect.Bottom - 5, rect.Right - 4, rect.Top + 5);
                }
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using (Pen pen = new Pen(ThemeText))
                {
                    int y = e.Item.Height / 2;
                    e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
                }
            }
        }

        public MainForm()
        {
            root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory));
            nativeExe = Path.Combine(root, "SBMSNative.exe");
            deviceHostExe = Path.Combine(root, "SBMSDeviceHost.exe");
            userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
            configPath = Path.Combine(userDataDir, "config.xml");
            logDirectory = Path.Combine(userDataDir, "logs");
            sessionLogPath = Path.Combine(logDirectory, "SBMS-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
            latestLogPath = Path.Combine(logDirectory, "latest.log");
            errorLogPath = Path.Combine(logDirectory, "error.log");
            InitializeDiagnostics();

            Text = AppName + " " + BuildLabel;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(980, 720);
            MinimumSize = new Size(780, 540);
            Font = new Font("Segoe UI", 9F);
            BackColor = ThemeBack;

            sourceText.Text = "4552x2560";
            sourceText.ReadOnly = true;
            targetText.Text = "2560x1440";
            targetText.ReadOnly = true;
            singleRefreshText.Text = "60";
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
            followWindowsTopologyCheck.Text = "跟随Windows BETA";
            followWindowsTopologyCheck.Checked = true;
            ConfigureBetaPairGrid();
            addBetaGroupButton.Text = "+ 新增组 BETA";
            addBetaGroupButton.Width = 126;
            addBetaGroupButton.AccessibleDescription = "risk";
            removeBetaGroupButton.Text = "删除组";
            removeBetaGroupButton.Width = 90;
            vsyncCheck.Text = "VSync";
            vsyncCheck.Checked = true;
            EnforceDefaultRuntimeOptions();
            calculateButton.Text = "计算";
            calculateButton.Width = 90;
            applyConfigButton.Text = "应用";
            applyConfigButton.Width = 100;

            BuildMainUi();
            BuildConfigForm();
            ApplyTheme(configInlineHost);
            LoadConfiguration();
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
            singleRefreshText.TextChanged += delegate { SyncSingleRefreshToBetaRow(); };
            sourceDisplayCombo.SelectedIndexChanged += delegate { SyncSelectedDisplaysToSelectors(); };
            targetDisplayCombo.SelectedIndexChanged += delegate { SyncSelectedDisplaysToSelectors(); };
            streamModeCheck.CheckedChanged += delegate { OnStreamModeChanged(); };
            followWindowsTopologyCheck.CheckedChanged += delegate { UpdateToggleVisuals(); };
            addBetaGroupButton.Click += delegate { AddBetaGroupFromUi(); };
            removeBetaGroupButton.Click += delegate { RemoveSelectedBetaGroup(); RecalculateBetaPairGrid(false); UpdateRuntimeOptionState(false); };
            startButton.Click += delegate { ToggleBridge(); };
            configButton.Click += delegate { ShowConfigForm(); };
            startupButton.Click += delegate { ToggleStartupFromButton(); };
            languageButton.Click += delegate { ShowLanguagePopup(); };
            languageButton.MouseEnter += delegate { ShowLanguagePopup(); };
            lightweightButton.Click += delegate { ToggleLightweightMode(); };
            listButton.Click += delegate { RunList(); };
            FormClosing += OnFormClosing;
            FormClosed += delegate { trayIcon.Dispose(); };
            ConfigureConfigurationPersistence();
            startupMenuItem.Checked = IsStartupEnabled();
            if (!configurationFileLoaded)
            {
                lightweightMenuItem.Checked = true;
            }
            ApplyStrategy(false);
            RefreshDisplays();
            ApplyLanguage();
            UpdateRuntimeOptionState();
            ApplyTheme(this);
            UpdateMainActionButtons();
            configurationPersistenceReady = true;
            SaveConfigurationNow(false);
            AppendLog("GUI版本 = " + BuildLabel);
            AppendLog("配置文件 = " + configPath);
            AppendLog("日志文件 = " + sessionLogPath);
            if (!string.IsNullOrWhiteSpace(pendingConfigLoadMessage))
            {
                AppendLog(pendingConfigLoadMessage);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            EnableDarkTitleBar(this);
        }

        private void BuildMainUi()
        {
            settingsMenuItem.DropDownItems.Add(startupMenuItem);
            languageMenuItem.DropDownItems.Add(chineseMenuItem);
            languageMenuItem.DropDownItems.Add(englishMenuItem);
            settingsMenuItem.DropDownItems.Add(languageMenuItem);

            configMenuItem.Click += delegate { ShowConfigForm(); };
            startupMenuItem.CheckOnClick = true;
            startupMenuItem.Click += delegate { ToggleStartup(); };
            lightweightMenuItem.CheckOnClick = true;
            lightweightMenuItem.Click += delegate { OnLightweightMenuChanged(); };
            chineseMenuItem.Click += delegate { english = false; ApplyLanguage(); ScheduleConfigurationSave(); };
            englishMenuItem.Click += delegate { english = true; ApplyLanguage(); ScheduleConfigurationSave(); };

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

            languagePopup.Items.Add(popupEnglishMenuItem);
            languagePopup.Items.Add(popupChineseMenuItem);
            ConfigureLanguagePopup();
            popupEnglishMenuItem.Click += delegate { english = true; ApplyLanguage(); HideLanguagePopup(); ScheduleConfigurationSave(); };
            popupChineseMenuItem.Click += delegate { english = false; ApplyLanguage(); HideLanguagePopup(); ScheduleConfigurationSave(); };

            var main = new TableLayoutPanel();
            main.Dock = DockStyle.Fill;
            main.Padding = new Padding(12, 10, 12, 12);
            main.ColumnCount = 1;
            main.RowCount = 5;
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            configInlineRowStyle = new RowStyle(SizeType.Absolute, 0);
            main.RowStyles.Add(configInlineRowStyle);
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(main);

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

            configInlineHost.Dock = DockStyle.Fill;
            configInlineHost.Visible = false;
            configInlineHost.BackColor = ThemeBack;
            main.Controls.Add(configInlineHost, 0, 2);

            logText.Dock = DockStyle.Fill;
            logText.Multiline = true;
            logText.ScrollBars = ScrollBars.None;
            logText.ReadOnly = true;
            logText.Font = new Font("Consolas", 10F);
            logText.BorderStyle = BorderStyle.FixedSingle;
            var logMenu = new ContextMenuStrip();
            logMenu.Items.Add("清空", null, delegate { logText.Clear(); });
            logText.ContextMenuStrip = logMenu;
            main.Controls.Add(logText, 0, 3);

            var actionRow = new TableLayoutPanel();
            actionRow.Dock = DockStyle.Fill;
            actionRow.ColumnCount = 3;
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actionRow.Padding = new Padding(0, 6, 0, 0);

            var buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.None;
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0);
            startButton.Width = 110;
            listButton.Width = 120;
            startButton.ShowGlyph = true;
            listButton.ShowGlyph = true;
            startButton.Margin = new Padding(0, 0, 8, 0);
            listButton.Margin = new Padding(0, 0, 8, 0);
            buttons.Controls.Add(startButton);
            buttons.Controls.Add(listButton);

            var quickButtons = new FlowLayoutPanel();
            quickButtons.Dock = DockStyle.None;
            quickButtons.AutoSize = true;
            quickButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            quickButtons.FlowDirection = FlowDirection.LeftToRight;
            quickButtons.WrapContents = false;
            quickButtons.Padding = new Padding(0);
            configButton.Width = 92;
            startupButton.Width = 78;
            languageButton.Width = 86;
            lightweightButton.Width = 122;
            configButton.ShowGlyph = false;
            languageButton.ShowGlyph = false;
            startupButton.ShowGlyph = true;
            lightweightButton.ShowGlyph = true;
            configButton.Margin = new Padding(0);
            languageButton.Margin = new Padding(0, 0, 8, 0);
            lightweightButton.Margin = new Padding(0, 0, 8, 0);
            quickButtons.Controls.Add(startupButton);
            startupButton.Margin = new Padding(0, 0, 8, 0);
            quickButtons.Controls.Add(lightweightButton);
            quickButtons.Controls.Add(languageButton);
            quickButtons.Controls.Add(configButton);
            quickButtons.Height = 34;
            quickButtons.MinimumSize = new Size(0, quickButtons.Height);

            actionRow.Controls.Add(buttons, 0, 0);
            actionRow.Controls.Add(new Panel(), 1, 0);
            actionRow.Controls.Add(quickButtons, 2, 0);
            main.Controls.Add(actionRow, 0, 4);
        }

        private void BuildConfigForm()
        {
            configInlineHost.Controls.Clear();
            configForm = new Form();
            configForm.StartPosition = FormStartPosition.CenterParent;
            configForm.Size = new Size(1120, 780);
            configForm.MinimumSize = new Size(980, 700);
            configForm.ShowInTaskbar = false;
            configForm.BackColor = ThemeBack;
            configForm.ForeColor = ThemeText;
            EnableDarkTitleBar(configForm);
            configForm.HandleCreated += delegate { EnableDarkTitleBar(configForm); };
            configForm.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                e.Cancel = true;
                configForm.Hide();
            };

            var configHost = new Panel();
            configHost.Dock = DockStyle.Fill;
            configInlineHost.Controls.Add(configHost);

            var panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.Padding = new Padding(14);
            panel.ColumnCount = 1;
            panel.RowCount = 3;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            configHost.Controls.Add(panel);

            ConfigureInputTabs();
            ConfigureTabControl(betaGroupTabs);
            betaGroupTabs.ItemSize = new Size(138, 32);
            betaGroupTabs.Dock = DockStyle.Fill;
            if (!groupTabsEventsBound)
            {
                betaGroupTabs.SelectedIndexChanged += delegate { OnBetaGroupTabChanged(); };
                groupTabsEventsBound = true;
            }
            panel.Controls.Add(betaGroupTabs, 0, 0);

            var configButtons = new FlowLayoutPanel();
            configButtons.Dock = DockStyle.Fill;
            configButtons.FlowDirection = FlowDirection.LeftToRight;
            var closeButton = new GlowButton { Width = 100 };
            closeButton.Click += delegate { ToggleInlineConfig(false); };
            closeButton.Tag = "关闭";
            applyConfigButton.Tag = "应用";
            ConfigureToggle(followWindowsTopologyCheck, 168, false);
            followWindowsTopologyCheck.Margin = new Padding(0, 6, 12, 0);
            configButtons.Controls.Add(followWindowsTopologyCheck);
            configButtons.Controls.Add(removeBetaGroupButton);
            configButtons.Controls.Add(applyConfigButton);
            configButtons.Controls.Add(closeButton);
            panel.Controls.Add(configButtons, 0, 1);

            configLockPanel.Dock = DockStyle.Fill;
            configLockPanel.Name = "configLockPanel";
            configLockPanel.BackColor = ThemeBack;
            configLockPanel.Visible = false;
            var lockLayout = new TableLayoutPanel();
            lockLayout.Dock = DockStyle.Fill;
            lockLayout.BackColor = ThemeBack;
            lockLayout.ColumnCount = 1;
            lockLayout.RowCount = 4;
            lockLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lockLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            lockLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            lockLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            lockLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            configLockLabel.Dock = DockStyle.Fill;
            configLockLabel.Name = "configLockLabel";
            configLockLabel.TextAlign = ContentAlignment.MiddleCenter;
            configLockLabel.Font = new Font("Segoe UI", 24F, FontStyle.Bold);
            configLockLabel.ForeColor = ThemeRed;
            configLockLabel.BackColor = ThemeBack;
            lockLayout.Controls.Add(configLockLabel, 0, 1);
            var lockButtonHost = new TableLayoutPanel();
            lockButtonHost.Dock = DockStyle.Fill;
            lockButtonHost.BackColor = ThemeBack;
            lockButtonHost.ColumnCount = 3;
            lockButtonHost.RowCount = 1;
            lockButtonHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            lockButtonHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            lockButtonHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            configLockBackButton.Tag = "返回";
            configLockBackButton.Width = 140;
            configLockBackButton.Height = 36;
            configLockBackButton.Dock = DockStyle.Fill;
            configLockBackButton.Margin = new Padding(0);
            configLockBackButton.Click += delegate { ToggleInlineConfig(false); };
            lockButtonHost.Controls.Add(configLockBackButton, 1, 0);
            lockLayout.Controls.Add(lockButtonHost, 0, 2);
            configLockPanel.Controls.Add(lockLayout);
            configHost.Controls.Add(configLockPanel);
            configLockPanel.BringToFront();
            UpdateMappingTabs();
        }

        private Control CreateSingleMappingPanel()
        {
            var panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.Padding = new Padding(0, 8, 0, 0);
            panel.ColumnCount = 2;
            panel.RowCount = 6;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            ConfigureToggle(streamModeCheck, 122, true);
            AddLabel(panel, "模式", 0);
            panel.Controls.Add(streamModeCheck, 1, 0);
            AddLabel(panel, "虚拟源", 1);
            panel.Controls.Add(sourceDisplayCombo, 1, 1);
            AddLabel(panel, "输出目标", 2);
            panel.Controls.Add(targetDisplayCombo, 1, 2);
            AddLabel(panel, "配置方式", 3);
            panel.Controls.Add(configInputTabs, 1, 3);
            AddLabel(panel, "尺寸策略", 4);
            panel.Controls.Add(strategyCombo, 1, 4);

            var sourcePanel = new FlowLayoutPanel();
            sourcePanel.Dock = DockStyle.Fill;
            sourcePanel.FlowDirection = FlowDirection.LeftToRight;
            sourcePanel.WrapContents = false;
            sourceText.Width = 120;
            targetText.Width = 120;
            singleRefreshText.Width = 58;
            sourcePanel.Controls.Add(sourceText);
            sourcePanel.Controls.Add(targetText);
            sourcePanel.Controls.Add(CreateInlineLabel("刷新率"));
            sourcePanel.Controls.Add(singleRefreshText);
            sourcePanel.Controls.Add(calculateButton);
            AddLabel(panel, "映射结果", 5);
            panel.Controls.Add(sourcePanel, 1, 5);
            return panel;
        }

        private void UpdateMappingTabs()
        {
            UpdateMappingTabs(true);
        }

        private void UpdateMappingTabs(bool rebuild)
        {
            selectedBetaGroupIndex = Math.Max(0, Math.Min(selectedBetaGroupIndex, Math.Max(0, betaPairGrid.Rows.Count - 1)));
            removeBetaGroupButton.Visible = betaPairGrid.Rows.Count > 1 && selectedBetaGroupIndex > 0;
            if (rebuild)
            {
                RebuildBetaGroupTabs();
            }
            else
            {
                betaGroupTabs.Invalidate();
                UpdateToggleVisuals();
            }
        }

        private void RebuildBetaGroupTabs()
        {
            if (betaGroupTabs == null || betaGroupTabs.IsDisposed || betaGroupTabs.Parent == null || rebuildingGroupTabs)
            {
                return;
            }
            rebuildingGroupTabs = true;
            SuspendRedraw(betaGroupTabs);
            betaGroupTabs.SuspendLayout();
            if (betaPairGrid.Rows.Count > 0 && betaGroupTabs.SelectedIndex >= 0 && betaGroupTabs.SelectedIndex < betaPairGrid.Rows.Count)
            {
                selectedBetaGroupIndex = betaGroupTabs.SelectedIndex;
            }
            int selected = Math.Max(0, Math.Min(selectedBetaGroupIndex, Math.Max(0, betaPairGrid.Rows.Count - 1)));
            try
            {
                if (betaPairGrid.Rows.Count == 0 && betaPairGrid.Columns.Count > 0)
                {
                    updatingBetaPairGrid = true;
                    try
                    {
                        AddBetaGroupRowInternal(CreateDefaultBetaPairSnapshot(""), false);
                    }
                    finally
                    {
                        updatingBetaPairGrid = false;
                    }
                }

                betaGroupTabs.TabPages.Clear();
                for (int i = 0; i < betaPairGrid.Rows.Count; ++i)
                {
                    TabPage page = new TabPage(GetBetaGroupTabText(i));
                    page.BackColor = ThemeBack;
                    page.ForeColor = ThemeGreen;
                    page.Controls.Add(i == 0 ? CreateSingleMappingPanel() : CreateBetaGroupEditor(i));
                    ApplyTheme(page);
                    betaGroupTabs.TabPages.Add(page);
                }
                if (betaPairGrid.Rows.Count < MultiScreenBetaMaxTargets)
                {
                    TabPage addPage = new TabPage("+");
                    addPage.Tag = "add";
                    addPage.BackColor = ThemeBack;
                    addPage.ForeColor = ThemeGreen;
                    betaGroupTabs.TabPages.Add(addPage);
                }
                if (betaGroupTabs.TabPages.Count > 0)
                {
                    suppressBetaGroupTabChange = true;
                    try
                    {
                        betaGroupTabs.SelectedIndex = Math.Max(0, Math.Min(selected, betaPairGrid.Rows.Count - 1));
                    }
                    finally
                    {
                        suppressBetaGroupTabChange = false;
                    }
                }
            }
            finally
            {
                betaGroupTabs.ResumeLayout(true);
                ResumeRedraw(betaGroupTabs);
                rebuildingGroupTabs = false;
            }
            UpdateToggleVisuals();
        }

        private static void SuspendRedraw(Control control)
        {
            if (control != null && control.IsHandleCreated)
            {
                SendMessage(control.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private static void ResumeRedraw(Control control)
        {
            if (control != null && control.IsHandleCreated)
            {
                SendMessage(control.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                control.Invalidate(true);
            }
        }

        private string GetBetaGroupTabText(int rowIndex)
        {
            if (english)
            {
                return "Mapping group " + (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
            }
            string[] numerals = { "一", "二", "三", "四", "五" };
            string suffix = rowIndex >= 0 && rowIndex < numerals.Length
                ? numerals[rowIndex]
                : (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
            return "映射组" + suffix;
        }

        private void OnBetaGroupTabChanged()
        {
            if (rebuildingGroupTabs || suppressBetaGroupTabChange || betaGroupTabs.SelectedIndex < 0)
            {
                return;
            }
            int selected = betaGroupTabs.SelectedIndex;
            int addIndex = betaPairGrid.Rows.Count;
            if (selected == addIndex && betaPairGrid.Rows.Count < MultiScreenBetaMaxTargets)
            {
                int previous = selectedBetaGroupIndex;
                int previousCount = betaPairGrid.Rows.Count;
                AddBetaGroupFromUi();
                if (betaPairGrid.Rows.Count == previousCount)
                {
                    SelectBetaGroupTab(previous);
                }
                return;
            }
            if (selected < betaPairGrid.Rows.Count)
            {
                selectedBetaGroupIndex = selected;
                UpdateRuntimeOptionState(false);
            }
        }

        private void SelectBetaGroupTab(int rowIndex)
        {
            if (betaGroupTabs.TabPages.Count == 0 || betaPairGrid.Rows.Count == 0)
            {
                return;
            }
            selectedBetaGroupIndex = Math.Max(0, Math.Min(rowIndex, betaPairGrid.Rows.Count - 1));
            suppressBetaGroupTabChange = true;
            try
            {
                betaGroupTabs.SelectedIndex = selectedBetaGroupIndex;
            }
            finally
            {
                suppressBetaGroupTabChange = false;
            }
        }

        private Control CreateBetaGroupEditor(int rowIndex)
        {
            DataGridViewRow row = betaPairGrid.Rows[rowIndex];
            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.Padding = new Padding(0, 10, 0, 0);
            grid.ColumnCount = 2;
            grid.RowCount = 9;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < grid.RowCount; ++i)
            {
                grid.RowStyles.Add(new RowStyle(i == 8 ? SizeType.Percent : SizeType.Absolute, i == 8 ? 100 : 36));
            }

            var enabledToggle = new CheckBox();
            enabledToggle.Tag = "启用";
            enabledToggle.Text = T("启用");
            enabledToggle.Checked = IsBetaRowEnabled(row);
            ConfigureToggle(enabledToggle, 88);
            enabledToggle.CheckedChanged += delegate
            {
                if (rowIndex >= betaPairGrid.Rows.Count) return;
                betaPairGrid.Rows[rowIndex].Cells[BetaColEnabled].Value = enabledToggle.Checked;
                RecalculateBetaPairGrid(false);
                UpdateRuntimeOptionState(false);
            };
            AddLabel(grid, "启用", 0);
            grid.Controls.Add(enabledToggle, 1, 0);

            var streamToggle = new CheckBox();
            streamToggle.Text = T("仅虚拟桌面");
            streamToggle.Checked = IsBetaRowStreamOnly(row);
            ConfigureToggle(streamToggle, 142, true);
            AddLabel(grid, "模式", 1);
            grid.Controls.Add(streamToggle, 1, 1);

            var horizontalText = CreateEditorTextBox(GetCellText(row, BetaColHorizontal));
            var aspectText = CreateEditorTextBox(GetCellText(row, BetaColAspect));
            var resolutionText = CreateEditorTextBox(GetBetaResolutionText(row));
            var sizeText = CreateEditorTextBox(GetCellText(row, BetaColSize));
            var refreshText = CreateEditorTextBox(GetCellText(row, BetaColRefresh));
            var sourceOutput = CreateEditorTextBox(GetCellText(row, BetaColSource));
            sourceOutput.ReadOnly = true;

            var orientationCombo = new DarkComboBox();
            orientationCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            orientationCombo.Items.AddRange(new object[] { "横屏", "竖屏", "横屏反向", "竖屏反向" });
            SelectComboByText(orientationCombo, GetCellText(row, BetaColOrientation));

            var strategyComboBox = new DarkComboBox();
            strategyComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            strategyComboBox.Items.AddRange(new object[] { "真实尺寸比例", "文字清晰优先", "直接使用源" });
            SelectComboByText(strategyComboBox, GetCellText(row, BetaColStrategy));

            horizontalText.TextChanged += delegate { UpdateBetaCellFromText(rowIndex, BetaColHorizontal, horizontalText.Text, sourceOutput); };
            resolutionText.TextChanged += delegate { UpdateBetaResolutionFromText(rowIndex, resolutionText.Text, sourceOutput); };
            aspectText.TextChanged += delegate { UpdateBetaCellFromText(rowIndex, BetaColAspect, aspectText.Text, sourceOutput); };
            sizeText.TextChanged += delegate { UpdateBetaCellFromText(rowIndex, BetaColSize, sizeText.Text, sourceOutput); };
            refreshText.TextChanged += delegate { UpdateBetaCellFromText(rowIndex, BetaColRefresh, refreshText.Text, sourceOutput); };
            orientationCombo.SelectedIndexChanged += delegate { UpdateBetaCellFromText(rowIndex, BetaColOrientation, Convert.ToString(orientationCombo.SelectedItem, CultureInfo.InvariantCulture), sourceOutput); };
            strategyComboBox.SelectedIndexChanged += delegate { UpdateBetaCellFromText(rowIndex, BetaColStrategy, Convert.ToString(strategyComboBox.SelectedItem, CultureInfo.InvariantCulture), sourceOutput); };

            streamToggle.CheckedChanged += delegate
            {
                if (rowIndex >= betaPairGrid.Rows.Count) return;
                if (streamToggle.Checked && !ShowRiskConfirmation("串流模式 BETA", "串流模式为BETA功能, 只创建虚拟桌面, 不复制到任何物理显示器"))
                {
                    streamToggle.Checked = false;
                    UpdateToggleVisuals();
                    return;
                }
                SetBetaRowStreamMode(rowIndex, streamToggle.Checked);
                RecalculateBetaPairGrid(false);
                RebuildBetaGroupTabs();
                UpdateRuntimeOptionState(false);
            };

            if (streamToggle.Checked)
            {
                AddLabel(grid, "实际分辨率", 2);
                resolutionText.Width = 150;
                grid.Controls.Add(resolutionText, 1, 2);
                AddLabel(grid, "实际尺寸", 3);
                sizeText.Width = 72;
                grid.Controls.Add(sizeText, 1, 3);
                AddLabel(grid, "计算策略", 4);
                strategyComboBox.Width = 138;
                grid.Controls.Add(strategyComboBox, 1, 4);
                AddLabel(grid, "刷新率", 5);
                refreshText.Width = 72;
                grid.Controls.Add(refreshText, 1, 5);
                AddLabel(grid, "虚拟源", 6);
                sourceOutput.Width = 150;
                grid.Controls.Add(sourceOutput, 1, 6);
                return grid;
            }

            var targetCombo = new DarkComboBox();
            targetCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            targetCombo.Dock = DockStyle.Left;
            targetCombo.Width = 720;
            foreach (DisplayChoice display in GetPhysicalDisplays())
            {
                targetCombo.Items.Add(GetDisplayLabel(display));
            }
            string currentTarget = GetNormalTargetLabel(row);
            SelectComboByText(targetCombo, currentTarget);
            targetCombo.SelectedIndexChanged += delegate
            {
                if (rowIndex >= betaPairGrid.Rows.Count || IsBetaRowStreamOnly(betaPairGrid.Rows[rowIndex])) return;
                string selected = Convert.ToString(targetCombo.SelectedItem, CultureInfo.InvariantCulture);
                DataGridViewRow current = betaPairGrid.Rows[rowIndex];
                current.Cells[BetaColTarget].Value = selected;
                DisplayChoice display = FindDisplayByTargetLabel(selected);
                current.Tag = display;
                if (display != null)
                {
                    PopulateBetaRowFromDisplay(current, display);
                    RecalculateBetaPairGrid(false);
                    RebuildBetaGroupTabs();
                }
            };
            AddLabel(grid, "目标显示器", 2);
            grid.Controls.Add(targetCombo, 1, 2);
            AddLabel(grid, "横向像素", 3);
            grid.Controls.Add(horizontalText, 1, 3);
            AddLabel(grid, "比例", 4);
            grid.Controls.Add(aspectText, 1, 4);
            AddLabel(grid, "方向", 5);
            grid.Controls.Add(orientationCombo, 1, 5);

            var lower = new FlowLayoutPanel();
            lower.Dock = DockStyle.Fill;
            lower.FlowDirection = FlowDirection.LeftToRight;
            lower.WrapContents = false;
            lower.Padding = new Padding(0, 1, 0, 0);
            lower.Controls.Add(CreateInlineLabel("输出尺寸"));
            lower.Controls.Add(sizeText);
            lower.Controls.Add(CreateInlineLabel("策略"));
            lower.Controls.Add(strategyComboBox);
            lower.Controls.Add(CreateInlineLabel("刷新率"));
            lower.Controls.Add(refreshText);
            lower.Controls.Add(CreateInlineLabel("虚拟源"));
            lower.Controls.Add(sourceOutput);
            sizeText.Width = 72;
            strategyComboBox.Width = 138;
            refreshText.Width = 72;
            sourceOutput.Width = 118;
            AddLabel(grid, "映射结果", 6);
            grid.Controls.Add(lower, 1, 6);
            return grid;
        }

        private TextBox CreateEditorTextBox(string text)
        {
            var textBox = new TextBox();
            textBox.Text = text;
            textBox.Dock = DockStyle.Left;
            textBox.Width = 150;
            textBox.BorderStyle = BorderStyle.FixedSingle;
            return textBox;
        }

        private Label CreateInlineLabel(string text)
        {
            var label = new Label();
            label.Text = T(text);
            label.Tag = text;
            label.AutoSize = true;
            label.Padding = new Padding(10, 7, 4, 0);
            return label;
        }

        private static void SelectComboByText(ComboBox combo, string text)
        {
            for (int i = 0; i < combo.Items.Count; ++i)
            {
                if (string.Equals(Convert.ToString(combo.Items[i], CultureInfo.InvariantCulture), text, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
        }

        private void UpdateBetaCellFromText(int rowIndex, int columnIndex, string value, TextBox sourceOutput)
        {
            if (rowIndex < 0 || rowIndex >= betaPairGrid.Rows.Count)
            {
                return;
            }
            betaPairGrid.Rows[rowIndex].Cells[columnIndex].Value = value;
            RecalculateBetaPairGrid(false);
            sourceOutput.Text = GetCellText(betaPairGrid.Rows[rowIndex], BetaColSource);
        }

        private void UpdateBetaResolutionFromText(int rowIndex, string value, TextBox sourceOutput)
        {
            if (rowIndex < 0 || rowIndex >= betaPairGrid.Rows.Count)
            {
                return;
            }

            Resolution resolution;
            if (!TryParseResolution(value, out resolution) || resolution.Width <= 0 || resolution.Height <= 0)
            {
                betaPairGrid.Rows[rowIndex].Cells[BetaColSource].Value = "参数无效";
                sourceOutput.Text = "参数无效";
                return;
            }

            int horizontal;
            string aspect;
            string orientation;
            BuildResolutionParts(resolution, out horizontal, out aspect, out orientation);

            updatingBetaPairGrid = true;
            try
            {
                DataGridViewRow row = betaPairGrid.Rows[rowIndex];
                row.Cells[BetaColHorizontal].Value = horizontal.ToString(CultureInfo.InvariantCulture);
                row.Cells[BetaColAspect].Value = aspect;
                row.Cells[BetaColOrientation].Value = orientation;
            }
            finally
            {
                updatingBetaPairGrid = false;
            }

            RecalculateBetaPairGrid(false);
            sourceOutput.Text = GetCellText(betaPairGrid.Rows[rowIndex], BetaColSource);
        }

        private static string GetBetaResolutionText(DataGridViewRow row)
        {
            Resolution resolution;
            return TryReadBetaResolutionSpec(row, out resolution) ? FormatResolution(resolution) : GetCellText(row, BetaColHorizontal);
        }

        private void SetBetaRowStreamMode(int rowIndex, bool streamOnly)
        {
            if (rowIndex < 0 || rowIndex >= betaPairGrid.Rows.Count)
            {
                return;
            }
            DataGridViewRow row = betaPairGrid.Rows[rowIndex];
            row.Cells[BetaColMode].Value = streamOnly ? "串流" : "输出";
            if (streamOnly)
            {
                row.Tag = null;
                string streamLabel = "串流目标 " + (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
                AddComboItemIfMissing(BetaColTarget, streamLabel);
                row.Cells[BetaColTarget].Value = streamLabel;
            }
            else
            {
                DisplayChoice display = GetDefaultPhysicalDisplay("");
                if (display != null)
                {
                    row.Tag = display;
                    string targetLabel = GetDisplayLabel(display);
                    AddComboItemIfMissing(BetaColTarget, targetLabel);
                    row.Cells[BetaColTarget].Value = targetLabel;
                    PopulateBetaRowFromDisplay(row, display);
                }
            }
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
            betaPairGrid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "模式", FillWeight = 72, FlatStyle = FlatStyle.Flat });
            betaPairGrid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "目标显示器", FillWeight = 170, FlatStyle = FlatStyle.Flat });
            betaPairGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "横向像素", FillWeight = 72 });
            betaPairGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "比例", FillWeight = 62 });
            betaPairGrid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "方向", FillWeight = 86, FlatStyle = FlatStyle.Flat });
            betaPairGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "输出尺寸", FillWeight = 64 });
            betaPairGrid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "策略", FillWeight = 118, FlatStyle = FlatStyle.Flat });
            betaPairGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "刷新率", FillWeight = 60 });
            betaPairGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "虚拟源", FillWeight = 92 });

            DataGridViewComboBoxColumn modeColumn = betaPairGrid.Columns[BetaColMode] as DataGridViewComboBoxColumn;
            if (modeColumn != null)
            {
                modeColumn.Items.AddRange(new object[] { "输出", "串流" });
            }
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
                if (IsBetaRowStreamOnly(e.RowIndex) && e.ColumnIndex == BetaColTarget)
                {
                    e.Cancel = true;
                }
            };
            betaPairGrid.EditingControlShowing += delegate(object sender, DataGridViewEditingControlShowingEventArgs e)
            {
                ComboBox combo = e.Control as ComboBox;
                if (combo != null)
                {
                    combo.BackColor = ThemeBack;
                    combo.ForeColor = ThemeText;
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.DrawMode = DrawMode.OwnerDrawFixed;
                    combo.DrawItem -= DrawDarkComboItem;
                    combo.DrawItem += DrawDarkComboItem;
                    combo.DropDownWidth = MeasureComboDropDownWidth(combo);
                }
            };
            betaPairGrid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
        }

        private void ConfigureInputTabs()
        {
            ConfigureTabControl(configInputTabs);
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
            AddLabel(grid, "输出目标", 2);
            grid.Controls.Add(CreatePresetPanel(targetResolutionPresetCombo, targetAspectPresetCombo, targetOrientationPresetCombo, targetSizePresetCombo), 1, 2);
            return grid;
        }

        private Control CreateManualModePanel()
        {
            var grid = CreateModeGrid();
            AddHeaderRow(grid);
            AddLabel(grid, "基准", 1);
            grid.Controls.Add(CreateManualInputPanel(manualBaseHorizontalText, manualBaseAspectText, manualBaseOrientationCombo, manualBaseSizeText), 1, 1);
            AddLabel(grid, "输出目标", 2);
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
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            return grid;
        }

        private static void AddHeaderRow(TableLayoutPanel grid)
        {
            var header = new FlowLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.FlowDirection = FlowDirection.LeftToRight;
            header.WrapContents = false;
            header.Controls.Add(CreateHeaderLabel("横向像素", 138));
            header.Controls.Add(CreateHeaderLabel("比例", 86));
            header.Controls.Add(CreateHeaderLabel("方向", 104));
            header.Controls.Add(CreateHeaderLabel("实际尺寸", 108));
            grid.Controls.Add(header, 1, 0);
        }

        private static Label CreateHeaderLabel(string text, int width)
        {
            return new Label
            {
                Text = text,
                Tag = text,
                Width = width,
                Height = 22,
                Margin = new Padding(3, 0, 3, 0),
                TextAlign = ContentAlignment.MiddleLeft
            };
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
            if (configInlineHost.Controls.Count == 0)
            {
                BuildConfigForm();
                ApplyTheme(configInlineHost);
                ApplyLanguage();
            }
            UpdateConfigLock();
            ToggleInlineConfig(true);
        }

        private void ShowLanguagePopup()
        {
            if (!languageButton.Visible)
            {
                return;
            }
            languageButton.ActiveFill = true;
            languageButton.Invalidate();
            if (languagePopup.Visible)
            {
                return;
            }
            languagePopup.Show(languageButton, new Point(0, -languagePopup.PreferredSize.Height));
            languagePopupCloseTimer.Start();
        }

        private void ConfigureLanguagePopup()
        {
            languagePopup.RenderMode = ToolStripRenderMode.System;
            languagePopup.Renderer = new MinimalMenuRenderer();
            languagePopup.ShowCheckMargin = false;
            languagePopup.ShowImageMargin = false;
            languagePopup.Padding = new Padding(0);
            languagePopup.Margin = new Padding(0);
            languagePopup.AutoSize = true;
            ConfigureLanguagePopupItem(popupEnglishMenuItem);
            ConfigureLanguagePopupItem(popupChineseMenuItem);
            languagePopup.Closed += delegate { HideLanguagePopup(); };
            languagePopup.MouseLeave += delegate { CloseLanguagePopupIfPointerLeft(); };
            languageButton.MouseLeave += delegate { CloseLanguagePopupIfPointerLeft(); };
            languagePopupCloseTimer.Interval = 80;
            languagePopupCloseTimer.Tick += delegate { CloseLanguagePopupIfPointerLeft(); };
        }

        private void ConfigureLanguagePopupItem(ToolStripMenuItem item)
        {
            item.AutoSize = false;
            item.Size = new Size(Math.Max(languageButton.Width, 86), 34);
            item.Margin = new Padding(0);
            item.Padding = new Padding(0);
            item.DisplayStyle = ToolStripItemDisplayStyle.Text;
        }

        private void CloseLanguagePopupIfPointerLeft()
        {
            if (!languagePopup.Visible)
            {
                languagePopupCloseTimer.Stop();
                return;
            }
            Point cursor = Cursor.Position;
            Rectangle buttonBounds = languageButton.RectangleToScreen(languageButton.ClientRectangle);
            Rectangle popupBounds = languagePopup.Bounds;
            if (!buttonBounds.Contains(cursor) && !popupBounds.Contains(cursor))
            {
                HideLanguagePopup();
            }
        }

        private void HideLanguagePopup()
        {
            languagePopupCloseTimer.Stop();
            if (languagePopup.Visible)
            {
                languagePopup.Close(ToolStripDropDownCloseReason.CloseCalled);
            }
            languageButton.ActiveFill = false;
            languageButton.Invalidate();
        }

        private void ToggleStartupFromButton()
        {
            startupMenuItem.Checked = !startupMenuItem.Checked;
            ToggleStartup();
            UpdateMainActionButtons();
        }

        private void ToggleLightweightMode()
        {
            lightweightMenuItem.Checked = !lightweightMenuItem.Checked;
            OnLightweightMenuChanged();
        }

        private void OnLightweightMenuChanged()
        {
            AppendLog(T(lightweightMenuItem.Checked ? "轻量模式开启" : "轻量模式关闭"));
            UpdateStatus();
            UpdateMainActionButtons();
            ScheduleConfigurationSave();
        }

        private void InitializeDiagnostics()
        {
            try
            {
                Directory.CreateDirectory(userDataDir);
                Directory.CreateDirectory(logDirectory);
                File.WriteAllText(latestLogPath, "", Encoding.UTF8);
                if (!File.Exists(errorLogPath))
                {
                    File.WriteAllText(errorLogPath, "", Encoding.UTF8);
                }
                WriteDiagnosticLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " session start build=" + BuildLabel, false);
            }
            catch
            {
            }
        }

        public static void WriteFatalError(string source, Exception exception)
        {
            try
            {
                string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
                string logs = Path.Combine(baseDir, "logs");
                Directory.CreateDirectory(logs);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                              " fatal[" + source + "] " +
                              (exception == null ? "unknown" : exception.ToString());
                File.AppendAllText(Path.Combine(logs, "error.log"), line + Environment.NewLine, Encoding.UTF8);
                File.AppendAllText(Path.Combine(logs, "latest.log"), line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }

        private void LoadConfiguration()
        {
            loadingConfiguration = true;
            try
            {
                if (!File.Exists(configPath))
                {
                    pendingConfigLoadMessage = "配置文件未找到, 已使用默认配置";
                    return;
                }

                GuiConfigFile config;
                var serializer = new XmlSerializer(typeof(GuiConfigFile));
                using (FileStream stream = File.OpenRead(configPath))
                {
                    config = (GuiConfigFile)serializer.Deserialize(stream);
                }
                if (config == null)
                {
                    pendingConfigLoadMessage = "配置文件为空, 已使用默认配置";
                    return;
                }

                ApplyConfiguration(config);
                configurationFileLoaded = true;
                pendingConfigLoadMessage = "配置已读取 = " + configPath;
            }
            catch (Exception ex)
            {
                pendingConfigLoadMessage = "配置读取失败: " + ex.Message;
                WriteDiagnosticLine(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + pendingConfigLoadMessage, true);
            }
            finally
            {
                loadingConfiguration = false;
            }
        }

        private void ApplyConfiguration(GuiConfigFile config)
        {
            english = config.English;
            lightweightMenuItem.Checked = config.LightweightMode;
            SetSelectedIndex(configInputTabs, config.ConfigTabIndex);
            SetSelectedIndex(strategyCombo, config.StrategyIndex);
            SetSelectedIndex(filterCombo, config.FilterIndex);
            SetTextIfPresent(sourceText, config.SourceText);
            SetTextIfPresent(targetText, config.TargetText);
            SetTextIfPresent(singleRefreshText, config.SingleRefresh);
            pendingConfigSourceDevice = config.SelectedSourceDevice ?? "";
            pendingConfigTargetDevice = config.SelectedTargetDevice ?? "";

            SetTextIfPresent(primaryResolutionText, config.PrimaryResolution);
            SetTextIfPresent(primarySizeText, config.PrimarySize);
            SetTextIfPresent(targetResolutionText, config.TargetResolution);
            SetTextIfPresent(targetSizeText, config.TargetSize);
            SetSelectedIndex(primaryResolutionPresetCombo, config.PrimaryResolutionPresetIndex);
            SetSelectedIndex(primaryAspectPresetCombo, config.PrimaryAspectPresetIndex);
            SetSelectedIndex(primaryOrientationPresetCombo, config.PrimaryOrientationPresetIndex);
            SetSelectedIndex(primarySizePresetCombo, config.PrimarySizePresetIndex);
            SetSelectedIndex(targetResolutionPresetCombo, config.TargetResolutionPresetIndex);
            SetSelectedIndex(targetAspectPresetCombo, config.TargetAspectPresetIndex);
            SetSelectedIndex(targetOrientationPresetCombo, config.TargetOrientationPresetIndex);
            SetSelectedIndex(targetSizePresetCombo, config.TargetSizePresetIndex);

            SetTextIfPresent(manualBaseHorizontalText, config.ManualBaseHorizontal);
            SetTextIfPresent(manualBaseAspectText, config.ManualBaseAspect);
            SetSelectedIndex(manualBaseOrientationCombo, config.ManualBaseOrientationIndex);
            SetTextIfPresent(manualBaseSizeText, config.ManualBaseSize);
            SetTextIfPresent(manualTargetHorizontalText, config.ManualTargetHorizontal);
            SetTextIfPresent(manualTargetAspectText, config.ManualTargetAspect);
            SetSelectedIndex(manualTargetOrientationCombo, config.ManualTargetOrientationIndex);
            SetTextIfPresent(manualTargetSizeText, config.ManualTargetSize);

            streamModeCheck.Checked = config.StreamMode;
            inputCheck.Checked = config.InputMapping;
            windowMoveCheck.Checked = config.WindowMove;
            deviceHostCheck.Checked = config.DeviceHost;
            vsyncCheck.Checked = config.VSync;
            followWindowsTopologyCheck.Checked = config.FollowWindowsTopologyBeta;
            selectedBetaGroupIndex = Math.Max(0, config.SelectedBetaGroupIndex);

            if (config.BetaPairs != null && config.BetaPairs.Count > 0)
            {
                var snapshots = new List<BridgePairSnapshot>();
                foreach (GuiConfigBridgePair pair in config.BetaPairs)
                {
                    snapshots.Add(new BridgePairSnapshot
                    {
                        Enabled = pair.Enabled,
                        Mode = pair.Mode,
                        Target = pair.Target,
                        Horizontal = pair.Horizontal,
                        Aspect = pair.Aspect,
                        Orientation = pair.Orientation,
                        Size = pair.Size,
                        Strategy = pair.Strategy,
                        Refresh = pair.Refresh,
                        Source = pair.Source
                    });
                }
                RestoreBetaPairRows(snapshots, pendingConfigTargetDevice);
            }
        }

        private void ConfigureConfigurationPersistence()
        {
            configurationSaveTimer.Interval = 700;
            configurationSaveTimer.Tick += delegate
            {
                configurationSaveTimer.Stop();
                SaveConfigurationNow(false);
            };

            EventHandler schedule = delegate { ScheduleConfigurationSave(); };
            sourceText.TextChanged += schedule;
            targetText.TextChanged += schedule;
            singleRefreshText.TextChanged += schedule;
            primaryResolutionText.TextChanged += schedule;
            primarySizeText.TextChanged += schedule;
            targetResolutionText.TextChanged += schedule;
            targetSizeText.TextChanged += schedule;
            manualBaseHorizontalText.TextChanged += schedule;
            manualBaseAspectText.TextChanged += schedule;
            manualBaseSizeText.TextChanged += schedule;
            manualTargetHorizontalText.TextChanged += schedule;
            manualTargetAspectText.TextChanged += schedule;
            manualTargetSizeText.TextChanged += schedule;

            sourceDisplayCombo.SelectedIndexChanged += schedule;
            targetDisplayCombo.SelectedIndexChanged += schedule;
            strategyCombo.SelectedIndexChanged += schedule;
            primaryResolutionPresetCombo.SelectedIndexChanged += schedule;
            primaryAspectPresetCombo.SelectedIndexChanged += schedule;
            primaryOrientationPresetCombo.SelectedIndexChanged += schedule;
            primarySizePresetCombo.SelectedIndexChanged += schedule;
            targetResolutionPresetCombo.SelectedIndexChanged += schedule;
            targetAspectPresetCombo.SelectedIndexChanged += schedule;
            targetOrientationPresetCombo.SelectedIndexChanged += schedule;
            targetSizePresetCombo.SelectedIndexChanged += schedule;
            manualBaseOrientationCombo.SelectedIndexChanged += schedule;
            manualTargetOrientationCombo.SelectedIndexChanged += schedule;
            filterCombo.SelectedIndexChanged += schedule;
            configInputTabs.SelectedIndexChanged += schedule;
            betaGroupTabs.SelectedIndexChanged += schedule;

            inputCheck.CheckedChanged += schedule;
            windowMoveCheck.CheckedChanged += schedule;
            deviceHostCheck.CheckedChanged += schedule;
            streamModeCheck.CheckedChanged += schedule;
            vsyncCheck.CheckedChanged += schedule;
            followWindowsTopologyCheck.CheckedChanged += schedule;

            betaPairGrid.RowsAdded += delegate { ScheduleConfigurationSave(); };
            betaPairGrid.RowsRemoved += delegate { ScheduleConfigurationSave(); };
            betaPairGrid.CellValueChanged += delegate { ScheduleConfigurationSave(); };
        }

        private void ScheduleConfigurationSave()
        {
            if (loadingConfiguration || !configurationPersistenceReady || exiting || updatingBetaPairGrid || rebuildingGroupTabs)
            {
                return;
            }
            configurationSaveTimer.Stop();
            configurationSaveTimer.Start();
        }

        private void SaveConfigurationNow(bool announce)
        {
            if (loadingConfiguration)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(userDataDir);
                GuiConfigFile config = CaptureConfiguration();
                string tempPath = configPath + ".tmp";
                var settings = new XmlWriterSettings
                {
                    Encoding = Encoding.UTF8,
                    Indent = true,
                    OmitXmlDeclaration = false
                };
                var serializer = new XmlSerializer(typeof(GuiConfigFile));
                using (XmlWriter writer = XmlWriter.Create(tempPath, settings))
                {
                    serializer.Serialize(writer, config);
                }
                File.Copy(tempPath, configPath, true);
                File.Delete(tempPath);
                if (announce)
                {
                    AppendLog("配置已保存 = " + configPath);
                }
            }
            catch (Exception ex)
            {
                AppendLog("配置保存失败: " + ex.Message);
            }
        }

        private GuiConfigFile CaptureConfiguration()
        {
            var config = new GuiConfigFile();
            config.Version = 1;
            config.SavedByBuild = BuildLabel;
            config.English = english;
            config.LightweightMode = lightweightMenuItem.Checked;
            config.ConfigTabIndex = GetSelectedIndex(configInputTabs);
            config.StrategyIndex = GetSelectedIndex(strategyCombo);
            config.FilterIndex = GetSelectedIndex(filterCombo);
            config.SourceText = GetText(sourceText);
            config.TargetText = GetText(targetText);
            config.SingleRefresh = GetText(singleRefreshText);
            config.SelectedSourceDevice = GetSelectedDeviceName(sourceDisplayCombo);
            config.SelectedTargetDevice = GetSelectedDeviceName(targetDisplayCombo);
            config.PrimaryResolution = GetText(primaryResolutionText);
            config.PrimarySize = GetText(primarySizeText);
            config.TargetResolution = GetText(targetResolutionText);
            config.TargetSize = GetText(targetSizeText);
            config.PrimaryResolutionPresetIndex = GetSelectedIndex(primaryResolutionPresetCombo);
            config.PrimaryAspectPresetIndex = GetSelectedIndex(primaryAspectPresetCombo);
            config.PrimaryOrientationPresetIndex = GetSelectedIndex(primaryOrientationPresetCombo);
            config.PrimarySizePresetIndex = GetSelectedIndex(primarySizePresetCombo);
            config.TargetResolutionPresetIndex = GetSelectedIndex(targetResolutionPresetCombo);
            config.TargetAspectPresetIndex = GetSelectedIndex(targetAspectPresetCombo);
            config.TargetOrientationPresetIndex = GetSelectedIndex(targetOrientationPresetCombo);
            config.TargetSizePresetIndex = GetSelectedIndex(targetSizePresetCombo);
            config.ManualBaseHorizontal = GetText(manualBaseHorizontalText);
            config.ManualBaseAspect = GetText(manualBaseAspectText);
            config.ManualBaseOrientationIndex = GetSelectedIndex(manualBaseOrientationCombo);
            config.ManualBaseSize = GetText(manualBaseSizeText);
            config.ManualTargetHorizontal = GetText(manualTargetHorizontalText);
            config.ManualTargetAspect = GetText(manualTargetAspectText);
            config.ManualTargetOrientationIndex = GetSelectedIndex(manualTargetOrientationCombo);
            config.ManualTargetSize = GetText(manualTargetSizeText);
            config.StreamMode = streamModeCheck.Checked;
            config.InputMapping = inputCheck.Checked;
            config.WindowMove = windowMoveCheck.Checked;
            config.DeviceHost = deviceHostCheck.Checked;
            config.VSync = vsyncCheck.Checked;
            config.FollowWindowsTopologyBeta = followWindowsTopologyCheck.Checked;
            config.SelectedBetaGroupIndex = Math.Max(0, selectedBetaGroupIndex);
            config.BetaPairs.Clear();

            foreach (BridgePairSnapshot snapshot in CaptureBetaPairSnapshots())
            {
                config.BetaPairs.Add(new GuiConfigBridgePair
                {
                    Enabled = snapshot.Enabled,
                    Mode = snapshot.Mode,
                    Target = snapshot.Target,
                    Horizontal = snapshot.Horizontal,
                    Aspect = snapshot.Aspect,
                    Orientation = snapshot.Orientation,
                    Size = snapshot.Size,
                    Strategy = snapshot.Strategy,
                    Refresh = snapshot.Refresh,
                    Source = snapshot.Source
                });
            }
            return config;
        }

        private static int GetSelectedIndex(ComboBox combo)
        {
            return combo == null ? -1 : combo.SelectedIndex;
        }

        private static int GetSelectedIndex(TabControl tabs)
        {
            return tabs == null ? -1 : tabs.SelectedIndex;
        }

        private static void SetSelectedIndex(ComboBox combo, int index)
        {
            if (combo != null && index >= 0 && index < combo.Items.Count)
            {
                combo.SelectedIndex = index;
            }
        }

        private static void SetSelectedIndex(TabControl tabs, int index)
        {
            if (tabs != null && index >= 0 && index < tabs.TabPages.Count)
            {
                tabs.SelectedIndex = index;
            }
        }

        private static void SetTextIfPresent(TextBox textBox, string value)
        {
            if (textBox != null && !string.IsNullOrWhiteSpace(value))
            {
                textBox.Text = value.Trim();
            }
        }

        private static string GetText(TextBox textBox)
        {
            return textBox == null ? "" : (textBox.Text ?? "").Trim();
        }

        private void WriteDiagnosticLine(string line, bool error)
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
                string text = line + Environment.NewLine;
                File.AppendAllText(sessionLogPath, text, Encoding.UTF8);
                File.AppendAllText(latestLogPath, text, Encoding.UTF8);
                if (error)
                {
                    File.AppendAllText(errorLogPath, text, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        private void ToggleInlineConfig(bool show)
        {
            configInlineHost.Visible = show;
            if (configInlineRowStyle != null)
            {
                configInlineRowStyle.Height = show ? 460 : 0;
            }
            configButton.ActiveFill = show;
            configButton.Invalidate();
            PerformLayout();
        }

        internal string RunConfigProbe(string screenshotPath)
        {
            Show();
            ShowConfigForm();
            Application.DoEvents();
            SleepWithUiPump(250);
            Application.DoEvents();

            if (configInlineHost.Controls.Count == 0)
            {
                throw new InvalidOperationException("configInlineHost unavailable");
            }

            BringToFront();
            Activate();
            Application.DoEvents();
            SleepWithUiPump(250);
            Application.DoEvents();

            using (var bitmap = new Bitmap(configInlineHost.Width, configInlineHost.Height))
            {
                configInlineHost.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
            }

            var tabNames = new List<string>();
            foreach (TabPage page in betaGroupTabs.TabPages)
            {
                tabNames.Add(page.Text);
            }
            string tabSummary = string.Join(" | ", tabNames.ToArray());
            File.WriteAllText(screenshotPath + ".txt", tabSummary, Encoding.UTF8);
            return tabSummary;
        }

        internal void RunRiskProbe(string screenshotPath)
        {
            Show();
            ShowConfigForm();
            Application.DoEvents();
            SleepWithUiPump(250);
            Application.DoEvents();
            ShowRiskConfirmation("多组映射 BETA", "多组映射为BETA功能, 不确定稳定性", screenshotPath);
        }

        internal void RunStreamConfigProbe(string screenshotPath)
        {
            Show();
            ShowConfigForm();
            if (betaPairGrid.Rows.Count == 0)
            {
                AddBetaGroupRow(null, false);
            }
            if (betaPairGrid.Rows.Count == 1)
            {
                AddBetaGroupRow(null, false);
            }
            SetBetaRowStreamMode(1, true);
            selectedBetaGroupIndex = 1;
            RecalculateBetaPairGrid(false);
            RebuildBetaGroupTabs();
            SelectBetaGroupTab(1);
            Application.DoEvents();
            SleepWithUiPump(250);
            Application.DoEvents();

            using (var bitmap = new Bitmap(configInlineHost.Width, configInlineHost.Height))
            {
                configInlineHost.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        internal void RunLockProbe(string screenshotPath)
        {
            Show();
            forceConfigLockForProbe = true;
            ShowConfigForm();
            UpdateConfigLock();
            BringToFront();
            Activate();
            Application.DoEvents();
            SleepWithUiPump(250);
            Application.DoEvents();

            using (var bitmap = new Bitmap(configLockPanel.Width, configLockPanel.Height))
            {
                configLockPanel.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            string lockInfo =
                "host=" + configInlineHost.Bounds +
                " lock=" + configLockPanel.Bounds +
                " visible=" + configLockPanel.Visible +
                " index=" + (configLockPanel.Parent == null ? -1 : configLockPanel.Parent.Controls.GetChildIndex(configLockPanel));
            File.WriteAllText(screenshotPath + ".txt", lockInfo, Encoding.UTF8);
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
            ScheduleConfigurationSave();
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
                p.StartInfo.StandardOutputEncoding = Encoding.Default;
                p.StartInfo.StandardErrorEncoding = Encoding.Default;
                return CaptureProcessOutput(p, 10000, out output);
            }
        }

        private static bool CaptureProcessOutput(Process p, int timeoutMs, out string output)
        {
            var buffer = new StringBuilder();
            DataReceivedEventHandler appendLine = delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null)
                {
                    return;
                }
                lock (buffer)
                {
                    buffer.AppendLine(e.Data);
                }
            };

            try
            {
                p.OutputDataReceived += appendLine;
                p.ErrorDataReceived += appendLine;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    KillProcessQuietly(p);
                    lock (buffer)
                    {
                        output = "timeout: " + p.StartInfo.FileName + " " + p.StartInfo.Arguments + Environment.NewLine + buffer;
                    }
                    return false;
                }
                p.WaitForExit();
                lock (buffer)
                {
                    output = buffer.ToString();
                }
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        private static void KillProcessQuietly(Process p)
        {
            try
            {
                if (p != null && !p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(1000);
                }
            }
            catch
            {
            }
        }

        private static void SleepWithUiPump(int milliseconds)
        {
            int deadline = Environment.TickCount + milliseconds;
            while (Environment.TickCount < deadline)
            {
                Application.DoEvents();
                int remaining = deadline - Environment.TickCount;
                Thread.Sleep(Math.Max(1, Math.Min(50, remaining)));
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
            popupEnglishMenuItem.Text = "English";
            popupChineseMenuItem.Text = "中文";
            trayIcon.Text = AppName;
            chineseMenuItem.Checked = !english;
            englishMenuItem.Checked = english;
            popupChineseMenuItem.Checked = !english;
            popupEnglishMenuItem.Checked = english;
            listButton.Text = T("刷新列表");
            configButton.Text = T("配置");
            startupButton.Text = T("自启");
            languageButton.Text = english ? "语言" : "lang";
            lightweightButton.Text = T("轻量模式");
            calculateButton.Text = T("计算");
            applyConfigButton.Text = T("应用");
            addBetaGroupButton.Text = "+ " + T("新增组") + " BETA";
            removeBetaGroupButton.Text = T("删除组");
            inputCheck.Text = T("鼠标映射");
            windowMoveCheck.Text = T("迁移窗口");
            deviceHostCheck.Text = T("管理虚拟显示器");
            streamModeCheck.Text = T("串流模式");
            followWindowsTopologyCheck.Text = T("跟随Windows BETA");
            if (configInlineHost.Controls.Count > 0)
            {
                ApplyLanguageToControls(configInlineHost);
            }
            ApplyBetaPairGridLanguage();
            RebuildBetaGroupTabs();
            configLockLabel.Text = T("配置已锁定") + Environment.NewLine + T("请先停止 SBMS");
            configLockBackButton.Text = T("返回");
            ApplyComboTexts();
            UpdateMainActionButtons();
            UpdateToggleVisuals();
            UpdateStatus();
        }

        private void ApplyBetaPairGridLanguage()
        {
            if (betaPairGrid.Columns.Count < 10)
            {
                return;
            }
            betaPairGrid.Columns[BetaColEnabled].HeaderText = T("启用");
            betaPairGrid.Columns[BetaColMode].HeaderText = T("模式");
            betaPairGrid.Columns[BetaColTarget].HeaderText = T("目标显示器");
            betaPairGrid.Columns[BetaColHorizontal].HeaderText = T("横向像素");
            betaPairGrid.Columns[BetaColAspect].HeaderText = T("比例");
            betaPairGrid.Columns[BetaColOrientation].HeaderText = T("方向");
            betaPairGrid.Columns[BetaColSize].HeaderText = T("输出尺寸");
            betaPairGrid.Columns[BetaColStrategy].HeaderText = T("策略");
            betaPairGrid.Columns[BetaColRefresh].HeaderText = T("刷新率");
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
                case "自启": return "Startup";
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
                case "跟随Windows BETA": return "Follow Windows BETA";
                case "多屏 BETA": return "Multi-screen BETA";
                case "多组映射": return "Multi-mapping";
                case "映射配置": return "Mapping";
                case "单组映射": return "Single mapping";
                case "多组映射 BETA": return "Multi-mapping BETA";
                case "串流模式 BETA": return "Streaming mode BETA";
                case "组": return "Group";
                case "新增组": return "Add group";
                case "删除组": return "Remove group";
                case "启用": return "On";
                case "模式": return "Mode";
                case "输出": return "Output";
                case "目标显示器": return "Target display";
                case "串流目标": return "Streaming target";
                case "仅虚拟桌面": return "Virtual only";
                case "横向像素": return "Horizontal px";
                case "实际分辨率": return "Actual resolution";
                case "比例": return "Aspect";
                case "方向": return "Orientation";
                case "尺寸": return "Size";
                case "实际尺寸": return "Actual size";
                case "输出尺寸": return "Target size";
                case "刷新率": return "Refresh rate";
                case "计算策略": return "Sizing";
                case "策略": return "Strategy";
                case "如果不清楚这个选项的作用，请不要勾选": return "Do not enable this unless you know what it does";
                case "多组映射支持为BETA功能, 不保证稳定性": return "Multi-mapping support is BETA and is not guaranteed stable";
                case "多组映射为BETA功能, 不确定稳定性": return "Multi-mapping is a BETA feature and may be unstable";
                case "串流模式为BETA功能, 不确定稳定性": return "Streaming mode is a BETA feature and may be unstable";
                case "串流模式为BETA功能, 只创建虚拟桌面, 不复制到任何物理显示器": return "Streaming mode is BETA; it only creates a virtual desktop and does not copy it to a physical display";
                case "串流模式只创建虚拟桌面，不复制到任何物理显示器": return "Streaming mode only creates a virtual desktop and does not copy it to a physical display";
                case "确认": return "Confirm";
                case "放弃更改": return "Cancel";
                case "虚拟源": return "Virtual source";
                case "输出目标": return "Output target";
                case "配置方式": return "Input";
                case "预设": return "Preset";
                case "手动": return "Manual";
                case "基准": return "Base";
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
                case "轻量模式开启": return "Lightweight mode enabled";
                case "轻量模式关闭": return "Lightweight mode disabled";
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
                case "返回": return "Back";
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
            languagePopup.BackColor = ThemePanel;
            languagePopup.ForeColor = ThemeGreen;
            ApplyThemeToMenuItems(languagePopup.Items);
        }

        private static void StyleControl(Control control)
        {
            if (control == null)
            {
                return;
            }
            if (control.Name == "configLockPanel")
            {
                control.BackColor = ThemeBack;
                control.ForeColor = ThemeRed;
                return;
            }
            if (control.Name == "configLockLabel")
            {
                control.BackColor = ThemeBack;
                control.ForeColor = ThemeRed;
                return;
            }
            if (control is TextBox || control is ListBox)
            {
                control.BackColor = ThemePanel;
                control.ForeColor = ThemeText;
            }
            else if (control is ComboBox)
            {
                ComboBox combo = (ComboBox)control;
                combo.BackColor = ThemePanel;
                combo.ForeColor = ThemeText;
                combo.FlatStyle = FlatStyle.Flat;
                combo.DrawMode = DrawMode.OwnerDrawFixed;
                combo.DrawItem -= DrawDarkComboItem;
                combo.DrawItem += DrawDarkComboItem;
                combo.DropDownWidth = MeasureComboDropDownWidth(combo);
            }
            else if (control is DataGridView)
            {
                DataGridView grid = (DataGridView)control;
                grid.BackgroundColor = ThemePanel;
                grid.GridColor = ThemeGreen;
                grid.DefaultCellStyle.BackColor = ThemePanel;
                grid.DefaultCellStyle.ForeColor = ThemeText;
                grid.DefaultCellStyle.SelectionBackColor = ThemeActive;
                grid.DefaultCellStyle.SelectionForeColor = ThemeBack;
                grid.ColumnHeadersDefaultCellStyle.BackColor = ThemePanel2;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = ThemeGreen;
                grid.RowHeadersDefaultCellStyle.BackColor = ThemePanel2;
                grid.RowHeadersDefaultCellStyle.ForeColor = ThemeGreen;
            }
            else if (control is Button)
            {
                Button button = (Button)control;
                StyleButton(button, string.Equals(button.AccessibleDescription, "risk", StringComparison.OrdinalIgnoreCase), false);
            }
            else if (control is TabControl || control is TabPage)
            {
                control.BackColor = ThemeBack;
                control.ForeColor = ThemeGreen;
            }
            else
            {
                control.BackColor = ThemeBack;
                control.ForeColor = ThemeMuted;
            }
            CheckBox checkBox = control as CheckBox;
            if (checkBox != null)
            {
                checkBox.FlatStyle = FlatStyle.Flat;
                ApplyToggleVisual(checkBox);
            }
        }

        private static void ConfigureToggle(CheckBox checkBox, int width)
        {
            ConfigureToggle(checkBox, width, false);
        }

        private static void ConfigureToggle(CheckBox checkBox, int width, bool risk)
        {
            checkBox.Appearance = Appearance.Button;
            checkBox.AutoSize = false;
            checkBox.Width = width;
            checkBox.Height = 30;
            checkBox.TextAlign = ContentAlignment.MiddleCenter;
            checkBox.FlatStyle = FlatStyle.Flat;
            checkBox.FlatAppearance.BorderSize = 1;
            checkBox.Margin = new Padding(0, 0, 8, 0);
            checkBox.AccessibleDescription = risk ? "risk" : "normal";
            checkBox.UseVisualStyleBackColor = false;
            checkBox.FlatAppearance.MouseOverBackColor = ThemeBack;
            checkBox.FlatAppearance.MouseDownBackColor = ThemeActive;
            checkBox.FlatAppearance.CheckedBackColor = ThemeActive;
            ApplyToggleVisual(checkBox);
        }

        private void UpdateToggleVisuals()
        {
            ApplyToggleVisual(inputCheck);
            ApplyToggleVisual(windowMoveCheck);
            ApplyToggleVisual(deviceHostCheck);
            ApplyToggleVisual(streamModeCheck);
            ApplyToggleVisual(followWindowsTopologyCheck);
            ApplyToggleVisual(vsyncCheck);
            foreach (TabPage page in betaGroupTabs.TabPages)
            {
                ApplyToggleVisuals(page);
            }
        }

        private void ApplyToggleVisuals(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                CheckBox checkBox = control as CheckBox;
                if (checkBox != null && checkBox.Appearance == Appearance.Button)
                {
                    ApplyToggleVisual(checkBox);
                }
                ApplyToggleVisuals(control);
            }
        }

        private static void ApplyToggleVisual(CheckBox checkBox)
        {
            if (checkBox == null || checkBox.Appearance != Appearance.Button)
            {
                return;
            }
            bool risk = string.Equals(checkBox.AccessibleDescription, "risk", StringComparison.OrdinalIgnoreCase);
            Color line = risk ? ThemeRed : ThemeText;
            if (checkBox.Checked)
            {
                checkBox.BackColor = risk ? ThemeRed : ThemeActive;
                checkBox.ForeColor = risk ? ThemeText : ThemeBack;
            }
            else
            {
                checkBox.BackColor = ThemeBack;
                checkBox.ForeColor = line;
            }
            checkBox.FlatAppearance.BorderColor = line;
        }

        private static void ConfigureTabControl(TabControl tabs)
        {
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.ItemSize = new Size(116, 30);
            tabs.BackColor = ThemeBack;
            tabs.ForeColor = ThemeText;
            tabs.DrawItem -= DrawDarkTab;
            tabs.DrawItem += DrawDarkTab;
        }

        private static void DrawDarkTab(object sender, DrawItemEventArgs e)
        {
            TabControl tabs = sender as TabControl;
            if (tabs == null || e.Index < 0 || e.Index >= tabs.TabPages.Count)
            {
                return;
            }
            bool selected = e.Index == tabs.SelectedIndex;
            Rectangle bounds = e.Bounds;
            using (Brush background = new SolidBrush(ThemeBack))
            {
                e.Graphics.FillRectangle(background, bounds);
            }
            using (Pen border = new Pen(selected ? ThemeActive : ThemeText))
            {
                e.Graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
            }
            if (selected)
            {
                using (Pen top = new Pen(ThemeActive, 2F))
                {
                    e.Graphics.DrawLine(top, bounds.Left + 1, bounds.Top + 1, bounds.Right - 2, bounds.Top + 1);
                }
            }
            TextRenderer.DrawText(
                e.Graphics,
                tabs.TabPages[e.Index].Text,
                tabs.Font,
                bounds,
                ThemeText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
        }

        private static void DrawDarkComboItem(object sender, DrawItemEventArgs e)
        {
            ComboBox combo = sender as ComboBox;
            if (combo == null)
            {
                return;
            }
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (Brush background = new SolidBrush(selected ? ThemeActive : ThemePanel))
            {
                e.Graphics.FillRectangle(background, e.Bounds);
            }
            string text = e.Index >= 0 && e.Index < combo.Items.Count
                ? Convert.ToString(combo.Items[e.Index], CultureInfo.InvariantCulture)
                : combo.Text;
            TextRenderer.DrawText(
                e.Graphics,
                text,
                combo.Font,
                e.Bounds,
                selected ? ThemeBack : ThemeText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
        }

        private static int MeasureComboDropDownWidth(ComboBox combo)
        {
            int width = combo.Width;
            foreach (object item in combo.Items)
            {
                string text = Convert.ToString(item, CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(text))
                {
                    width = Math.Max(width, TextRenderer.MeasureText(text, combo.Font, Size.Empty, TextFormatFlags.NoPadding).Width + 42);
                }
            }
            return width;
        }

        private static void EnableDarkTitleBar(Form form)
        {
            if (form == null || !form.IsHandleCreated)
            {
                return;
            }
            int enabled = 1;
            if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
            }
        }

        private bool ShowRiskConfirmation(string title, string message)
        {
            return ShowRiskConfirmation(title, message, null);
        }

        private bool ShowRiskConfirmation(string title, string message, string screenshotPath)
        {
            Form owner = configForm != null && configForm.Visible ? configForm : this;
            using (var dialog = new Form())
            {
                dialog.FormBorderStyle = FormBorderStyle.None;
                dialog.StartPosition = FormStartPosition.Manual;
                Rectangle ownerClientBounds = owner.RectangleToScreen(owner.ClientRectangle);
                dialog.Bounds = ownerClientBounds;
                dialog.ShowInTaskbar = false;
                dialog.TopMost = owner.TopMost;
                dialog.BackColor = ThemeBack;
                dialog.BackgroundImage = CaptureBlurredBackground(owner);
                dialog.BackgroundImageLayout = ImageLayout.Stretch;

                var layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.BackColor = Color.Transparent;
                layout.ColumnCount = 1;
                layout.RowCount = 3;
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 154));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
                dialog.Controls.Add(layout);

                var titleLabel = new Label();
                titleLabel.Text = T(message);
                titleLabel.Dock = DockStyle.Fill;
                titleLabel.TextAlign = ContentAlignment.MiddleCenter;
                titleLabel.Font = new Font("Segoe UI", english ? 22F : 28F, FontStyle.Bold);
                titleLabel.ForeColor = ThemeRed;
                titleLabel.BackColor = Color.Transparent;
                titleLabel.Padding = new Padding(36, 0, 36, 0);
                layout.Controls.Add(titleLabel, 0, 1);

                var buttons = new FlowLayoutPanel();
                buttons.Dock = DockStyle.Bottom;
                buttons.Height = 68;
                buttons.FlowDirection = FlowDirection.RightToLeft;
                buttons.WrapContents = false;
                buttons.Padding = new Padding(0, 0, 28, 24);
                buttons.BackColor = Color.Transparent;
                var confirm = new GlowButton { Text = T("确认"), Width = 170, Height = 36, DialogResult = DialogResult.OK };
                var cancel = new GlowButton { Text = T("放弃更改"), Width = 210, Height = 36, DialogResult = DialogResult.Cancel };
                StyleRiskButton(confirm, true);
                StyleRiskButton(cancel, false);
                buttons.Controls.Add(confirm);
                buttons.Controls.Add(cancel);
                layout.Controls.Add(buttons, 0, 2);
                dialog.AcceptButton = confirm;
                dialog.CancelButton = cancel;
                System.Windows.Forms.Timer probeTimer = null;
                if (!string.IsNullOrEmpty(screenshotPath))
                {
                    probeTimer = new System.Windows.Forms.Timer();
                    probeTimer.Interval = 350;
                    probeTimer.Tick += delegate
                    {
                        probeTimer.Stop();
                        CaptureDialog(dialog, screenshotPath);
                        dialog.DialogResult = DialogResult.Cancel;
                        dialog.Close();
                    };
                    probeTimer.Start();
                }
                DialogResult result = dialog.ShowDialog(owner);
                if (probeTimer != null)
                {
                    probeTimer.Dispose();
                }
                return result == DialogResult.OK;
            }
        }

        private static void CaptureDialog(Form dialog, string screenshotPath)
        {
            if (dialog.Width <= 0 || dialog.Height <= 0)
            {
                return;
            }
            using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
            {
                dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private static void StyleRiskButton(Button button, bool danger)
        {
            StyleButton(button, danger, danger);
        }

        private static void StyleButton(Button button, bool danger, bool active)
        {
            GlowButton glowButton = button as GlowButton;
            if (glowButton != null)
            {
                glowButton.DangerFill = danger;
                glowButton.ActiveFill = active;
                glowButton.FlatAppearance.BorderSize = 0;
                glowButton.FlatAppearance.MouseOverBackColor = ThemeBack;
                glowButton.FlatAppearance.MouseDownBackColor = ThemeBack;
                glowButton.Invalidate();
                return;
            }
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.BackColor = active ? (danger ? ThemeRed : ThemeActive) : ThemeBack;
            button.ForeColor = active ? (danger ? ThemeText : ThemeBack) : (danger ? ThemeRed : ThemeText);
            button.FlatAppearance.BorderColor = danger ? ThemeRed : ThemeText;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = ThemePanel;
            button.FlatAppearance.MouseDownBackColor = ThemeActive;
        }

        private static Bitmap CaptureBlurredBackground(Control source)
        {
            int width = Math.Max(1, source.ClientSize.Width);
            int height = Math.Max(1, source.ClientSize.Height);
            Bitmap capture = new Bitmap(width, height);
            try
            {
                source.DrawToBitmap(capture, new Rectangle(0, 0, width, height));
            }
            catch
            {
                using (Graphics graphics = Graphics.FromImage(capture))
                {
                    graphics.Clear(ThemeBack);
                }
            }
            int smallWidth = Math.Max(1, width / 14);
            int smallHeight = Math.Max(1, height / 14);
            Bitmap small = new Bitmap(smallWidth, smallHeight);
            using (Graphics graphics = Graphics.FromImage(small))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(capture, new Rectangle(0, 0, smallWidth, smallHeight));
            }
            Bitmap blurred = new Bitmap(width, height);
            using (Graphics graphics = Graphics.FromImage(blurred))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(small, new Rectangle(0, 0, width, height));
                using (Brush brush = new SolidBrush(Color.FromArgb(180, ThemeBack)))
                {
                    graphics.FillRectangle(brush, new Rectangle(0, 0, width, height));
                }
            }
            capture.Dispose();
            small.Dispose();
            return blurred;
        }

        private void UpdateConfigLock()
        {
            bool locked = forceConfigLockForProbe || IsBridgeRunning();
            configLockPanel.Visible = locked;
            if (locked)
            {
                configLockPanel.BringToFront();
            }
        }

        private void OnStreamModeChanged()
        {
            if (!suppressStreamModePrompt && streamModeCheck.Checked)
            {
                if (!ShowRiskConfirmation("串流模式 BETA", "串流模式为BETA功能, 只创建虚拟桌面, 不复制到任何物理显示器"))
                {
                    suppressStreamModePrompt = true;
                    streamModeCheck.Checked = false;
                    suppressStreamModePrompt = false;
                    UpdateToggleVisuals();
                    return;
                }
            }
            ApplyStreamModeToBetaPairGrid();
            UpdateRuntimeOptionState(false);
            UpdateToggleVisuals();
            UpdateStatus();
        }

        private void ApplyStreamModeToBetaPairGrid()
        {
            if (betaPairGrid.Columns.Count <= BetaColTarget)
            {
                return;
            }

            bool streamOnly = streamModeCheck.Checked && !IsMultiMappingEnabled();
            betaPairGrid.Columns[BetaColTarget].HeaderText = T("目标显示器");
            betaPairGrid.Columns[BetaColTarget].ReadOnly = streamOnly;
            if (IsMultiMappingEnabled())
            {
                SyncFirstBetaRowFromSingleControls();
                RebuildBetaGroupTabs();
                return;
            }

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
                        row.Cells[BetaColMode].Value = "串流";
                        row.Cells[BetaColTarget].Value = streamLabel;
                    }
                    else
                    {
                        if (!IsMultiMappingEnabled() || string.IsNullOrWhiteSpace(GetCellText(row, BetaColMode)))
                        {
                            row.Cells[BetaColMode].Value = "输出";
                        }
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
            SyncFirstBetaRowFromSingleControls();
            RebuildBetaGroupTabs();
        }

        private void SyncFirstBetaRowFromSingleControls()
        {
            if (updatingBetaPairGrid || betaPairGrid.Columns.Count <= BetaColSource || betaPairGrid.Rows.Count == 0)
            {
                return;
            }

            updatingBetaPairGrid = true;
            try
            {
                DataGridViewRow row = betaPairGrid.Rows[0];
                row.Cells[BetaColEnabled].Value = true;
                bool streamOnly = streamModeCheck.Checked;
                row.Cells[BetaColMode].Value = streamOnly ? "串流" : "输出";
                DisplayChoice targetDisplay = null;
                if (streamOnly)
                {
                    row.Tag = null;
                    string streamLabel = "串流目标 1";
                    AddComboItemIfMissing(BetaColTarget, streamLabel);
                    row.Cells[BetaColTarget].Value = streamLabel;
                }
                else
                {
                    targetDisplay = targetDisplayCombo.SelectedItem as DisplayChoice ??
                                    FindDisplayByTargetLabel(targetText.Text.Trim()) ??
                                    GetDefaultPhysicalDisplay("");
                    if (targetDisplay != null)
                    {
                        row.Tag = targetDisplay;
                        string targetLabel = GetDisplayLabel(targetDisplay);
                        AddComboItemIfMissing(BetaColTarget, targetLabel);
                        row.Cells[BetaColTarget].Value = targetLabel;
                    }
                }

                Resolution targetResolution;
                if (TryParseResolution(targetResolutionText.Text.Trim(), out targetResolution))
                {
                    int horizontal;
                    string aspect;
                    string orientation;
                    BuildResolutionParts(targetResolution, out horizontal, out aspect, out orientation);
                    row.Cells[BetaColHorizontal].Value = horizontal.ToString(CultureInfo.InvariantCulture);
                    row.Cells[BetaColAspect].Value = aspect;
                    row.Cells[BetaColOrientation].Value = orientation;
                }

                string targetSize = targetSizeText.Text.Trim();
                double parsedSize;
                if (TryParseSize(targetSize, out parsedSize) && parsedSize > 0.0)
                {
                    row.Cells[BetaColSize].Value = targetSize;
                }

                row.Cells[BetaColStrategy].Value = GetStrategyTextForIndex(strategyCombo.SelectedIndex);
                row.Cells[BetaColRefresh].Value = GetRefreshOrDefault(singleRefreshText.Text, targetDisplay);
                row.Cells[BetaColSource].Value = sourceText.Text.Trim();
            }
            finally
            {
                updatingBetaPairGrid = false;
            }
            RecalculateBetaPairGrid(false);
        }

        private void SyncSingleRefreshToBetaRow()
        {
            if (updatingBetaPairGrid || betaPairGrid.Columns.Count <= BetaColSource || betaPairGrid.Rows.Count == 0)
            {
                return;
            }

            betaPairGrid.Rows[0].Cells[BetaColRefresh].Value = singleRefreshText.Text.Trim();
        }

        private static string GetStrategyTextForIndex(int index)
        {
            if (index == 1)
            {
                return "文字清晰优先";
            }
            if (index == 2)
            {
                return "直接使用源";
            }
            return "真实尺寸比例";
        }

        private void UpdateRuntimeOptionState()
        {
            UpdateRuntimeOptionState(true);
        }

        private void UpdateRuntimeOptionState(bool rebuildTabs)
        {
            EnforceDefaultRuntimeOptions();
            bool multiBeta = IsMultiMappingEnabled();
            bool streamOnly = streamModeCheck.Checked && !multiBeta;
            bool allMultiRowsStreamOnly = multiBeta && CountEnabledBetaPairs() > 0 &&
                                          CountEnabledStreamOnlyBetaPairs() == CountEnabledBetaPairs();
            bool hasNativeOutput = !streamOnly && !allMultiRowsStreamOnly;
            if ((streamOnly || multiBeta) && !deviceHostCheck.Checked)
            {
                deviceHostCheck.Checked = true;
            }

            bool bridgeRunning = IsBridgeRunning();
            deviceHostCheck.Enabled = !streamOnly && !multiBeta && !bridgeRunning;
            targetDisplayCombo.Enabled = !streamModeCheck.Checked && !bridgeRunning;
            targetText.Enabled = !streamModeCheck.Checked && !bridgeRunning;
            betaPairGrid.Enabled = multiBeta && !bridgeRunning;
            addBetaGroupButton.Enabled = !bridgeRunning;
            removeBetaGroupButton.Enabled = !bridgeRunning;
            filterCombo.Enabled = hasNativeOutput && !bridgeRunning;
            inputCheck.Enabled = hasNativeOutput && !bridgeRunning;
            windowMoveCheck.Enabled = hasNativeOutput && !bridgeRunning;
            vsyncCheck.Enabled = hasNativeOutput && !bridgeRunning;
            streamModeCheck.Enabled = !bridgeRunning;
            UpdateMappingTabs(rebuildTabs);
            UpdateToggleVisuals();
        }

        private void EnforceDefaultRuntimeOptions()
        {
            inputCheck.Checked = true;
            windowMoveCheck.Checked = true;
            deviceHostCheck.Checked = true;
            vsyncCheck.Checked = true;
            if (filterCombo.Items.Count > 0 && filterCombo.SelectedIndex < 0)
            {
                filterCombo.SelectedIndex = 0;
            }
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
            bool multiConfigured = IsMultiMappingEnabled();
            bool streamOnly = streamModeCheck.Checked && !multiConfigured && running && (process == null || process.HasExited);
            bool multiBeta = multiConfigured && running && (HasRunningBetaProcess() || IsDeviceHostRunning());
            statusLabel.Text = AppName + " // " + T(running ? (multiBeta ? "多屏BETA运行中" : (streamOnly ? "串流中" : "运行中")) : "待机");
            if (multiConfigured)
            {
                int pairCount = Math.Max(1, CountEnabledBetaPairs());
                int streamCount = CountEnabledStreamOnlyBetaPairs();
                int outputCount = Math.Max(0, pairCount - streamCount);
                routeLabel.Text = T("虚拟源") + " x" + pairCount.ToString(CultureInfo.InvariantCulture) +
                                  "  >  " + T("目标") + " x" + outputCount.ToString(CultureInfo.InvariantCulture) +
                                  (streamCount > 0 ? "  //  " + T("串流模式") + " x" + streamCount.ToString(CultureInfo.InvariantCulture) : "");
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
            UpdateMainActionButtons();
        }

        private void UpdateMainActionButtons()
        {
            bool running = IsBridgeRunning();
            startButton.Text = T(running ? "停止" : "启动");
            startButton.DangerFill = running;
            startButton.ActiveFill = false;
            startButton.Minimal = !running;
            startButton.Invalidate();
            startupButton.ActiveFill = startupMenuItem.Checked;
            startupButton.DangerFill = false;
            startupButton.Minimal = true;
            startupButton.Invalidate();
            languageButton.ActiveFill = false;
            languageButton.DangerFill = false;
            languageButton.Minimal = true;
            languageButton.Invalidate();
            lightweightButton.ActiveFill = lightweightMenuItem.Checked;
            lightweightButton.DangerFill = false;
            lightweightButton.Minimal = true;
            lightweightButton.Invalidate();
            configButton.DangerFill = false;
            configButton.ActiveFill = configInlineHost.Visible;
            configButton.Minimal = true;
            configButton.Invalidate();
            listButton.DangerFill = false;
            listButton.ActiveFill = false;
            listButton.Minimal = true;
            listButton.Invalidate();
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
            if (string.IsNullOrWhiteSpace(previousSourceDevice) && !string.IsNullOrWhiteSpace(pendingConfigSourceDevice))
            {
                previousSourceDevice = pendingConfigSourceDevice;
            }
            if (string.IsNullOrWhiteSpace(previousTargetDevice) && !string.IsNullOrWhiteSpace(pendingConfigTargetDevice))
            {
                previousTargetDevice = pendingConfigTargetDevice;
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
            pendingConfigSourceDevice = "";
            pendingConfigTargetDevice = "";
            SyncSelectedDisplaysToSelectors();
            ApplyStreamModeToBetaPairGrid();
            RecalculateBetaPairGrid(false);
            UpdateStatus();
        }

        private static bool TryParseDisplayLine(string line, out DisplayChoice display)
        {
            display = null;
            Match match = Regex.Match(line, @"^(\\\\\.\\DISPLAY\d+)( primary)?\: pos=[^ ]+ mode=(\d+x\d+)@(\d+)(?: sunshine=(\{[0-9a-fA-F-]{36}\}))? name=(.+)$");
            if (!match.Success)
            {
                return false;
            }

            string name = match.Groups[6].Value.Trim();
            DisplayRuntimeMode runtimeMode;
            bool hasRuntimeMode = TryGetCurrentDisplayMode(match.Groups[1].Value, out runtimeMode);
            display = new DisplayChoice
            {
                DeviceName = match.Groups[1].Value,
                Primary = match.Groups[2].Success,
                Resolution = hasRuntimeMode ? FormatResolution(runtimeMode.Resolution) : match.Groups[3].Value,
                Refresh = hasRuntimeMode && !string.IsNullOrWhiteSpace(runtimeMode.Refresh) ? runtimeMode.Refresh : match.Groups[4].Value,
                SunshineId = match.Groups[5].Success ? match.Groups[5].Value : "",
                Name = name,
                Orientation = hasRuntimeMode ? runtimeMode.Orientation : DMDO_DEFAULT,
                Virtual = IsVirtualDisplayName(name)
            };
            return true;
        }

        private static bool TryGetCurrentDisplayMode(string deviceName, out DisplayRuntimeMode mode)
        {
            mode = null;
            var devMode = new DEVMODE();
            devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref devMode) ||
                devMode.dmPelsWidth <= 0 ||
                devMode.dmPelsHeight <= 0)
            {
                return false;
            }

            mode = new DisplayRuntimeMode
            {
                Resolution = new Resolution { Width = devMode.dmPelsWidth, Height = devMode.dmPelsHeight },
                Refresh = devMode.dmDisplayFrequency > 0
                    ? devMode.dmDisplayFrequency.ToString(CultureInfo.InvariantCulture)
                    : "",
                Orientation = NormalizeDisplayOrientation(devMode.dmDisplayOrientation)
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
                    Mode = GetCellText(row, BetaColMode),
                    Target = GetNormalTargetLabel(row),
                    Horizontal = GetCellText(row, BetaColHorizontal),
                    Aspect = GetCellText(row, BetaColAspect),
                    Orientation = GetCellText(row, BetaColOrientation),
                    Size = GetCellText(row, BetaColSize),
                    Strategy = GetCellText(row, BetaColStrategy),
                    Refresh = GetCellText(row, BetaColRefresh),
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
            RebuildBetaGroupTabs();
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
                AppendLog("已新增 BETA 配置组");
                UpdateMappingTabs();
                ScheduleConfigurationSave();
            }
        }

        private void AddBetaGroupFromUi()
        {
            if (IsBridgeRunning())
            {
                UpdateConfigLock();
                return;
            }
            if (betaPairGrid.Rows.Count >= 1)
            {
                if (!ShowRiskConfirmation("多组映射 BETA", "多组映射为BETA功能, 不确定稳定性"))
                {
                    return;
                }
            }
            if (betaPairGrid.Rows.Count == 0)
            {
                AddBetaGroupRow(null, false);
            }
            SuspendRedraw(configInlineHost);
            try
            {
                SyncFirstBetaRowFromSingleControls();
                AddBetaGroupRow(null, true);
                RecalculateBetaPairGrid(false);
                selectedBetaGroupIndex = Math.Max(0, betaPairGrid.Rows.Count - 1);
                UpdateRuntimeOptionState(false);
                SelectBetaGroupTab(selectedBetaGroupIndex);
            }
            finally
            {
                ResumeRedraw(configInlineHost);
            }
        }

        private void AddBetaGroupRowInternal(BridgePairSnapshot snapshot, bool selectNewRow)
        {
            string savedTargetLabel = snapshot != null ? (snapshot.Target ?? "").Trim() : "";
            DisplayChoice display = FindDisplayByTargetLabel(savedTargetLabel) ?? GetDefaultPhysicalDisplay("");
            string rowMode = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Mode) ? snapshot.Mode : "输出";
            bool streamOnly = IsStreamModeText(rowMode);
            string targetLabel = streamOnly ? "" : (!string.IsNullOrWhiteSpace(savedTargetLabel) ? savedTargetLabel : (display != null ? GetDisplayLabel(display) : ""));
            string rowHorizontal = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Horizontal) ? snapshot.Horizontal : "2560";
            string rowAspect = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Aspect) ? snapshot.Aspect : "16:9";
            string rowOrientation = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Orientation) ? snapshot.Orientation : "横屏";
            string rowSize = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Size) ? snapshot.Size : (display != null ? GuessTargetSize(display) : "24");
            string rowStrategy = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Strategy) ? snapshot.Strategy : "真实尺寸比例";
            string rowRefresh = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Refresh) ? snapshot.Refresh : GetRefreshOrDefault("", display);
            string rowSource = snapshot != null ? snapshot.Source : "";
            bool enabled = snapshot == null || snapshot.Enabled;

            if (streamOnly)
            {
                targetLabel = "串流目标 " + (betaPairGrid.Rows.Count + 1).ToString(CultureInfo.InvariantCulture);
            }
            else if (display != null)
            {
                targetLabel = GetDisplayLabel(display);
            }
            AddComboItemIfMissing(BetaColTarget, targetLabel);
            int index = betaPairGrid.Rows.Add(enabled, streamOnly ? "串流" : "输出", targetLabel, rowHorizontal, rowAspect, rowOrientation, rowSize, rowStrategy, rowRefresh, rowSource);
            betaPairGrid.Rows[index].Tag = streamOnly ? null : display;
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
                Mode = "输出",
                Target = display != null ? GetDisplayLabel(display) : "",
                Horizontal = horizontal,
                Aspect = aspect,
                Orientation = orientation,
                Size = display != null ? GuessTargetSize(display) : "24",
                Strategy = "真实尺寸比例",
                Refresh = display != null ? GetRefreshOrDefault(display.Refresh, display) : "60",
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

            int index = betaGroupTabs.SelectedIndex >= 0 ? betaGroupTabs.SelectedIndex :
                        (betaPairGrid.CurrentRow != null ? betaPairGrid.CurrentRow.Index : betaPairGrid.Rows.Count - 1);
            if (index == 0)
            {
                AppendLog("组 1 为基准组，不能删除");
                return;
            }
            if (index >= 0 && index < betaPairGrid.Rows.Count)
            {
                betaPairGrid.Rows.RemoveAt(index);
            }
            selectedBetaGroupIndex = Math.Max(0, Math.Min(index - 1, betaPairGrid.Rows.Count - 1));
            UpdateMappingTabs();
            SelectBetaGroupTab(selectedBetaGroupIndex);
            ScheduleConfigurationSave();
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
            row.Cells[BetaColRefresh].Value = GetRefreshOrDefault("", display);
        }

        private void OnBetaPairGridCellValueChanged(DataGridViewCellEventArgs e)
        {
            if (updatingBetaPairGrid || e.RowIndex < 0 || e.RowIndex >= betaPairGrid.Rows.Count)
            {
                return;
            }

            DataGridViewRow row = betaPairGrid.Rows[e.RowIndex];
            if (e.ColumnIndex == BetaColMode)
            {
                if (IsBetaRowStreamOnly(row))
                {
                    row.Tag = null;
                    string streamLabel = "串流目标 " + (e.RowIndex + 1).ToString(CultureInfo.InvariantCulture);
                    AddComboItemIfMissing(BetaColTarget, streamLabel);
                    row.Cells[BetaColTarget].Value = streamLabel;
                }
                else
                {
                    DisplayChoice display = GetDefaultPhysicalDisplay("");
                    if (display != null)
                    {
                        row.Tag = display;
                        string targetLabel = GetDisplayLabel(display);
                        AddComboItemIfMissing(BetaColTarget, targetLabel);
                        row.Cells[BetaColTarget].Value = targetLabel;
                        PopulateBetaRowFromDisplay(row, display);
                    }
                }
                RebuildBetaGroupTabs();
            }
            if (e.ColumnIndex == BetaColTarget && !IsBetaRowStreamOnly(row))
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

        private int CountEnabledStreamOnlyBetaPairs()
        {
            int count = 0;
            foreach (DataGridViewRow row in betaPairGrid.Rows)
            {
                if (IsBetaRowEnabled(row) && IsBetaRowStreamOnly(row))
                {
                    ++count;
                }
            }
            return count;
        }

        private static int CountOutputBridgePairs(List<BridgePairConfig> pairs)
        {
            int count = 0;
            for (int i = 0; i < pairs.Count; ++i)
            {
                if (!pairs[i].StreamOnly)
                {
                    ++count;
                }
            }
            return count;
        }

        private bool IsMultiMappingEnabled()
        {
            return betaPairGrid.Rows.Count > 1;
        }

        private bool IsBetaRowStreamOnly(int rowIndex)
        {
            return rowIndex >= 0 && rowIndex < betaPairGrid.Rows.Count && IsBetaRowStreamOnly(betaPairGrid.Rows[rowIndex]);
        }

        private static bool IsBetaRowStreamOnly(DataGridViewRow row)
        {
            return row != null && IsStreamModeText(GetCellText(row, BetaColMode));
        }

        private static bool IsStreamModeText(string text)
        {
            return string.Equals(text, "串流", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "Streaming", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "Virtual only", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "仅虚拟桌面", StringComparison.OrdinalIgnoreCase);
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
                    Resolution targetResolution;
                    double targetSize;
                    Resolution source;
                    if (!TryCalculateBetaRowSource(row, baseResolution, baseSize, out targetResolution, out targetSize, out source))
                    {
                        row.Cells[BetaColSource].Value = "参数无效";
                        continue;
                    }

                    row.Cells[BetaColSource].Value = FormatResolution(source);
                }
            }
            finally
            {
                updatingBetaPairGrid = false;
            }
            if (log)
            {
                AppendLog("多组映射已计算");
            }
            UpdateStatus();
        }

        private bool TryGetEnabledBridgePairs(bool streamOnly, out List<BridgePairConfig> pairs, out string message)
        {
            pairs = new List<BridgePairConfig>();
            message = "";
            RecalculateBetaPairGrid(false);
            Resolution baseResolution;
            double baseSize;
            if (!TryParseResolution(primaryResolutionText.Text, out baseResolution) ||
                !TryParseSize(primarySizeText.Text, out baseSize) ||
                baseResolution.Width <= 0 ||
                baseResolution.Height <= 0 ||
                baseSize <= 0.0)
            {
                message = "多组映射基准参数无效";
                return false;
            }

            foreach (DataGridViewRow row in betaPairGrid.Rows)
            {
                if (!IsBetaRowEnabled(row))
                {
                    continue;
                }

                bool rowStreamOnly = streamOnly || IsBetaRowStreamOnly(row);
                DisplayChoice targetDisplay = row.Tag as DisplayChoice;
                if (!rowStreamOnly && targetDisplay == null)
                {
                    message = "多组映射缺少目标显示器";
                    return false;
                }

                Resolution targetResolution;
                double targetSize;
                Resolution sourceResolution;
                if (!TryCalculateBetaRowSource(row, baseResolution, baseSize, out targetResolution, out targetSize, out sourceResolution))
                {
                    message = "多组映射参数无效: " + GetCellText(row, BetaColTarget);
                    return false;
                }
                row.Cells[BetaColSource].Value = FormatResolution(sourceResolution);

                pairs.Add(new BridgePairConfig
                {
                    StreamOnly = rowStreamOnly,
                    TargetDisplay = targetDisplay,
                    TargetResolution = targetResolution,
                    SourceResolution = sourceResolution,
                    Orientation = GetOrientationMode(GetCellText(row, BetaColOrientation)),
                    StrategyIndex = GetBetaRowStrategyIndex(row),
                    TargetSize = targetSize,
                    Refresh = GetRefreshOrDefault(GetCellText(row, BetaColRefresh), targetDisplay),
                    Row = row
                });
            }

            if (pairs.Count == 0)
            {
                message = "多组映射未启用";
                return false;
            }
            if (pairs.Count > MultiScreenBetaMaxTargets)
            {
                message = "多屏 BETA 当前最多支持 " + MultiScreenBetaMaxTargets.ToString(CultureInfo.InvariantCulture) + " 个配置组";
                return false;
            }
            return true;
        }

        private static bool TryCalculateBetaRowSource(
            DataGridViewRow row,
            Resolution baseResolution,
            double baseSize,
            out Resolution targetResolution,
            out double targetSize,
            out Resolution sourceResolution)
        {
            sourceResolution = new Resolution();
            if (!TryReadBetaTargetSpec(row, out targetResolution, out targetSize))
            {
                return false;
            }

            int strategy = GetBetaRowStrategyIndex(row);
            if (strategy == 2)
            {
                sourceResolution = targetResolution;
            }
            else if (strategy == 1)
            {
                sourceResolution = CalculateQualitySource(baseResolution, targetResolution, baseSize, targetSize);
            }
            else
            {
                sourceResolution = CalculatePhysicalSource(baseResolution, targetResolution, baseSize, targetSize);
            }
            return sourceResolution.Width > 0 && sourceResolution.Height > 0;
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
            if (!TryReadBetaResolutionSpec(row, out targetResolution) ||
                !TryParseSize(GetCellText(row, BetaColSize), out targetSize) ||
                targetSize <= 0.0)
            {
                return false;
            }
            return true;
        }

        private static bool TryReadBetaResolutionSpec(DataGridViewRow row, out Resolution targetResolution)
        {
            targetResolution = new Resolution();
            int horizontal;
            int aspectW;
            int aspectH;
            if (!int.TryParse(GetCellText(row, BetaColHorizontal), out horizontal) ||
                horizontal <= 0 ||
                !TryParseAspectText(GetCellText(row, BetaColAspect), out aspectW, out aspectH))
            {
                return false;
            }

            int width = RoundEven(horizontal);
            int height = RoundEven(width * aspectH / (double)aspectW);
            string orientation = GetCellText(row, BetaColOrientation);
            bool portrait = string.Equals(orientation, "竖屏", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(orientation, "竖屏反向", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(orientation, "Portrait", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(orientation, "Portrait flipped", StringComparison.OrdinalIgnoreCase);
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

        private static int NormalizeDisplayOrientation(int orientation)
        {
            switch (orientation)
            {
                case DMDO_90:
                case DMDO_180:
                case DMDO_270:
                    return orientation;
                default:
                    return DMDO_DEFAULT;
            }
        }

        private static string GetOrientationText(int orientation)
        {
            switch (NormalizeDisplayOrientation(orientation))
            {
                case DMDO_90:
                    return "竖屏";
                case DMDO_180:
                    return "横屏反向";
                case DMDO_270:
                    return "竖屏反向";
                default:
                    return "横屏";
            }
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

        private static string GetRefreshOrDefault(string refreshText, DisplayChoice display)
        {
            int refresh;
            if (int.TryParse((refreshText ?? "").Trim(), out refresh) && refresh > 0)
            {
                return refresh.ToString(CultureInfo.InvariantCulture);
            }
            if (display != null && int.TryParse((display.Refresh ?? "").Trim(), out refresh) && refresh > 0)
            {
                return refresh.ToString(CultureInfo.InvariantCulture);
            }
            return "60";
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
                if (string.IsNullOrWhiteSpace(singleRefreshText.Text))
                {
                    singleRefreshText.Text = GetRefreshOrDefault("", targetDisplay);
                }
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
            SaveConfigurationNow(true);
        }

        private static Resolution CalculatePhysicalSource(Resolution primary, Resolution target, double primarySize, double targetSize)
        {
            double primaryPhysicalWidth = CalculatePhysicalWidth(primary, primarySize);
            double targetPhysicalWidth = CalculatePhysicalWidth(target, targetSize);
            if (primaryPhysicalWidth <= 0.0 || targetPhysicalWidth <= 0.0 || target.Width <= 0)
            {
                return new Resolution { Width = 1, Height = 1 };
            }

            double primaryPixelsPerInchX = primary.Width / primaryPhysicalWidth;
            int width = RoundEven(targetPhysicalWidth * primaryPixelsPerInchX);
            int height = RoundEven(width * target.Height / (double)target.Width);
            return new Resolution { Width = Math.Max(width, 1), Height = Math.Max(height, 1) };
        }

        private static double CalculatePhysicalWidth(Resolution resolution, double diagonalInches)
        {
            double width = resolution.Width;
            double height = resolution.Height;
            double diagonalPixels = Math.Sqrt(width * width + height * height);
            return diagonalPixels <= 0.0 ? 0.0 : diagonalInches * width / diagonalPixels;
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

        private void ToggleBridge()
        {
            if (IsBridgeRunning())
            {
                StopBridge();
            }
            else
            {
                StartBridge();
            }
        }

        private void StartBridge()
        {
            if (IsBridgeRunning() || bridgeStarting)
            {
                return;
            }
            bridgeStarting = true;
            startButton.Enabled = false;
            try
            {
            SyncFirstBetaRowFromSingleControls();
            EnforceDefaultRuntimeOptions();
            bool multiBeta = IsMultiMappingEnabled();
            bool streamOnly = streamModeCheck.Checked && !multiBeta;
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
                if (!TryGetEnabledBridgePairs(false, out betaPairs, out betaMessage))
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
            lastManagedVirtualResolution = "";
            lastManagedVirtualRefresh = "";
            lastManagedVirtualOrientation = DMDO_DEFAULT;
            DisplayChoice streamOnlyVirtualSource = null;

            if (manageVirtualDisplay)
            {
                int virtualDeviceCount = multiBeta ? betaPairs.Count : 1;
                if (!StartDeviceHost(virtualDeviceCount))
                {
                    AbortBridgeStart("虚拟显示器 host 启动失败，已停止桥接");
                    return;
                }
                if (multiBeta)
                {
                    List<DisplayChoice> virtualSources;
                    string virtualWaitFailure;
                    if (!WaitForVirtualSources(betaPairs.Count, 30000, out virtualSources, out virtualWaitFailure))
                    {
                        AbortBridgeStart(string.IsNullOrWhiteSpace(virtualWaitFailure)
                            ? "等待多屏 BETA 虚拟显示器超时，needed=" + betaPairs.Count.ToString(CultureInfo.InvariantCulture)
                            : virtualWaitFailure);
                        return;
                    }

                    for (int i = 0; i < betaPairs.Count && i < virtualSources.Count; ++i)
                    {
                        string modeMessage;
                        Resolution appliedResolution;
                        string appliedRefresh;
                        if (!TryApplyDisplayMode(virtualSources[i].DeviceName, betaPairs[i].SourceResolution, betaPairs[i].Refresh, betaPairs[i].Orientation, out appliedResolution, out appliedRefresh, out modeMessage))
                        {
                            AbortBridgeStart(modeMessage);
                            return;
                        }
                        AppendLog(modeMessage);
                        betaPairs[i].SourceResolution = appliedResolution;
                        betaPairs[i].Refresh = appliedRefresh;

                        DisplayChoice confirmedSource;
                        if (!WaitForVirtualSourceMode(virtualSources[i].DeviceName, appliedResolution, 5000, out confirmedSource))
                        {
                            AbortBridgeStart("BETA[" + (i + 1).ToString(CultureInfo.InvariantCulture) + "] 虚拟模式未确认: " + virtualSources[i].DeviceName + " -> " + FormatResolution(appliedResolution));
                            return;
                        }
                        virtualSources[i] = confirmedSource;
                        betaPairs[i].Refresh = confirmedSource.Refresh;
                        AppendLog("BETA[" + (i + 1).ToString(CultureInfo.InvariantCulture) + "] 虚拟模式已确认: " + confirmedSource.DeviceName + " " + confirmedSource.Resolution + "@" + confirmedSource.Refresh);
                    }
                    RefreshDisplays();
                    for (int i = 0; i < betaPairs.Count && i < betaPairGrid.Rows.Count; ++i)
                    {
                        betaPairGrid.Rows[i].Cells[BetaColSource].Value = FormatResolution(betaPairs[i].SourceResolution);
                        betaPairGrid.Rows[i].Cells[BetaColRefresh].Value = betaPairs[i].Refresh;
                    }

                    /*
                     * Issue #5: each ChangeDisplaySettingsEx call can make Windows rebuild
                     * the active display topology asynchronously. The GUI may confirm
                     * \\.\DISPLAY129 in one --list probe, then a freshly started native
                     * process can enumerate during the next topology commit and miss that
                     * same id. Before launching any BETA native process, wait for two stable
                     * native display-list samples, rebind physical targets, rediscover the
                     * current virtual sources, and restore requested modes if Windows reset
                     * them during the settle window.
                     */
                    string startupRecoveryMessage;
                    if (!WaitForDisplayTopologyToSettle(DisplayTopologySettleTimeoutMs, out startupRecoveryMessage))
                    {
                        AbortBridgeStart(startupRecoveryMessage);
                        return;
                    }

                    if (!RebindBetaTargetDisplays(out startupRecoveryMessage))
                    {
                        AbortBridgeStart(startupRecoveryMessage);
                        return;
                    }

                    if (!TryGetEnabledBridgePairs(false, out betaPairs, out startupRecoveryMessage))
                    {
                        AbortBridgeStart(startupRecoveryMessage);
                        return;
                    }

                    if (!WaitForVirtualSources(betaPairs.Count, 15000, out virtualSources, out virtualWaitFailure))
                    {
                        AbortBridgeStart(string.IsNullOrWhiteSpace(virtualWaitFailure)
                            ? "BETA 启动前等待虚拟显示器稳定超时，needed=" + betaPairs.Count.ToString(CultureInfo.InvariantCulture)
                            : virtualWaitFailure);
                        return;
                    }

                    if (!RestoreBetaVirtualModesAfterTopologyChange(virtualSources, betaPairs, out startupRecoveryMessage))
                    {
                        AbortBridgeStart(startupRecoveryMessage);
                        return;
                    }

                    for (int i = 0; i < betaPairs.Count && i < betaPairGrid.Rows.Count; ++i)
                    {
                        betaPairGrid.Rows[i].Cells[BetaColSource].Value = FormatResolution(betaPairs[i].SourceResolution);
                        betaPairGrid.Rows[i].Cells[BetaColRefresh].Value = betaPairs[i].Refresh;
                    }

                    if (virtualSources.Count < betaPairs.Count)
                    {
                        AbortBridgeStart("多屏 BETA 虚拟源数量不足，virtual=" + virtualSources.Count.ToString(CultureInfo.InvariantCulture) + " groups=" + betaPairs.Count.ToString(CultureInfo.InvariantCulture));
                        return;
                    }

                    sourceText.Text = virtualSources[0].DeviceName;
                    int outputPairCount = CountOutputBridgePairs(betaPairs);
                    int streamPairCount = betaPairs.Count - outputPairCount;
                    if (outputPairCount == 0)
                    {
                        process = null;
                        stoppingRequested = false;
                        lastNativeArgs = "";
                        SetRunning(true);
                        AppendLog("多组虚拟桌面模式已启动: " + betaPairs.Count.ToString(CultureInfo.InvariantCulture) + " 个虚拟源");
                        AppendStreamOnlySunshineDisplayIds(virtualSources, betaPairs);
                        return;
                    }

                    if (!StartMultiScreenBeta(virtualSources, betaPairs))
                    {
                        AbortBridgeStart("多屏 BETA 启动失败，已停止虚拟显示器 host");
                        return;
                    }
                    SetRunning(true);
                    AppendLog("多屏 BETA 已启动: 输出 " + outputPairCount.ToString(CultureInfo.InvariantCulture) + " 组, 串流 " + streamPairCount.ToString(CultureInfo.InvariantCulture) + " 组");
                    return;
                }

                DisplayChoice virtualSource;
                string singleVirtualWaitFailure;
                if (!WaitForAnyVirtualSource(30000, out virtualSource, out singleVirtualWaitFailure))
                {
                    string waitMessage = string.IsNullOrWhiteSpace(singleVirtualWaitFailure)
                        ? "等待虚拟显示器超时，requested=" + requestedSource
                        : singleVirtualWaitFailure;
                    if (deviceHostProcess != null && deviceHostProcess.HasExited)
                    {
                        AppendLog("虚拟显示器 host 已退出，exit=" + deviceHostProcess.ExitCode);
                        if (deviceHostLog.Length > 0)
                        {
                            AppendLog(deviceHostLog.ToString().TrimEnd());
                        }
                    }
                    AbortBridgeStart(waitMessage);
                    return;
                }
                AppendLog("虚拟显示器已就位: " + virtualSource);

                Resolution requestedResolution;
                if (TryParseResolution(requestedSource, out requestedResolution) &&
                    !string.Equals(virtualSource.Resolution, requestedSource, StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog("请求虚拟模式: " + requestedSource);
                    string modeMessage;
                    string requestedRefresh = GetSingleMappingRefresh(virtualSource);
                    Resolution appliedResolution;
                    string appliedRefresh;
                    if (TryApplyDisplayMode(virtualSource.DeviceName, requestedResolution, requestedRefresh, GetSelectedDisplayOrientation(), out appliedResolution, out appliedRefresh, out modeMessage))
                    {
                        AppendLog(modeMessage);
                        DisplayChoice switchedSource;
                        if (WaitForVirtualSourceMode(virtualSource.DeviceName, appliedResolution, 5000, out switchedSource))
                        {
                            virtualSource = switchedSource;
                            requestedSource = FormatResolution(appliedResolution);
                            sourceText.Text = requestedSource;
                            AppendLog("虚拟模式已确认: " + virtualSource);
                        }
                        else
                        {
                            RefreshDisplays();
                            AbortBridgeStart("虚拟模式切换后未确认到目标分辨率，停止启动: requested=" + requestedSource + " applied=" + FormatResolution(appliedResolution));
                            return;
                        }
                    }
                    else
                    {
                        AbortBridgeStart(modeMessage);
                        return;
                    }
                }

                sourceSelector = virtualSource.DeviceName;
                sourceResolutionForFilter = virtualSource.Resolution;
                sourceText.Text = sourceSelector;
                lastManagedVirtualResolution = virtualSource.Resolution;
                lastManagedVirtualRefresh = GetSingleMappingRefresh(virtualSource);
                lastManagedVirtualOrientation = GetSelectedDisplayOrientation();
                streamOnlyVirtualSource = virtualSource;
                RefreshDisplays();
                SelectComboByDevice(sourceDisplayCombo, sourceSelector);
            }

            if (streamOnly)
            {
                lastNativeArgs = "";
                stoppingRequested = false;
                AppendLog("串流模式已启动：仅创建虚拟桌面，未启动 native 输出");
                DisplayChoice selectedVirtualSource = streamOnlyVirtualSource ?? sourceDisplayCombo.SelectedItem as DisplayChoice;
                if (selectedVirtualSource != null && selectedVirtualSource.Virtual)
                {
                    AppendSunshineDisplayIdLog("串流模式", selectedVirtualSource);
                }
                SetRunning(true);
                return;
            }

            var args = new StringBuilder();
            args.Append(BuildSingleNativeArgs(sourceSelector, sourceResolutionForFilter, targetText.Text.Trim(), targetResolutionForFilter));
            AppendSelectedDisplayLog();
            lastNativeArgs = args.ToString();
            stoppingRequested = false;
            AppendLog("native 参数: " + lastNativeArgs);
            if (!StartNativeProcess(lastNativeArgs, false))
            {
                AbortBridgeStart("native 启动失败，已停止桥接");
                return;
            }
            SetRunning(true);
            AppendLog("已启动");
            }
            finally
            {
                bridgeStarting = false;
                if (!IsBridgeRunning())
                {
                    startButton.Enabled = true;
                    UpdateMainActionButtons();
                }
            }
        }

        private bool StartNativeProcess(string args, bool restarted)
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
                    if (!stoppingRequested && IsRecoverableNativeDisplayExit(exitCode))
                    {
                        /*
                         * Issue #8: Do not cap recoverable single-output restarts with a
                         * cumulative counter. Windows can emit several transient source or
                         * topology failures while the user is still editing layout, and the
                         * bridge should keep trying instead of carrying an old recovery count
                         * until it kills the current session.
                         */
                        AppendLog(GetRecoverableNativeExitMessage(exitCode) + "，重启 native 输出");
                        if (!TryRestartNativeAfterTopologyChange())
                        {
                            AbortBridgeStart("native 重启失败，已停止桥接");
                        }
                        return;
                    }
                    StopDeviceHost();
                    SetRunning(false);
                });
            };
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                AppendLog((restarted ? "native 重启失败: " : "native 启动失败: ") + ex.Message);
                process = null;
                return false;
            }
            AppendLog(restarted ? "native 已重启" : "native 已启动");
            return true;
        }

        private static bool IsRecoverableNativeDisplayExit(int exitCode)
        {
            return exitCode == NativeTopologyChangedExitCode ||
                   exitCode == NativeSourceUnavailableExitCode;
        }

        private static string GetRecoverableNativeExitMessage(int exitCode)
        {
            return exitCode == NativeSourceUnavailableExitCode
                ? "检测到虚拟源枚举短暂丢失"
                : "检测到显示拓扑变化";
        }

        private bool TryRestartNativeAfterTopologyChange()
        {
            if (!deviceHostCheck.Checked)
            {
                return StartNativeProcess(lastNativeArgs, true);
            }

            /*
             * Issue #1: a topology change must restart native output without
             * stopping the managed virtual display host.
             *
             * DXGI desktop duplication intentionally returns DXGI_ERROR_ACCESS_LOST when the
             * Windows display topology changes. The virtual monitor itself usually survives,
             * but its \\.\DISPLAYxx name can be reassigned while Windows rebuilds the desktop.
             *
             * The old code waited for sourceText, which had already been overwritten with the
             * previous \\.\DISPLAYxx id. After a layout change that id often no longer existed,
             * so the GUI timed out and stopped the host. Recovery must therefore start from
             * the current display list: find the managed virtual monitor again, restore the
             * requested mode if Windows reset it, then rebuild native arguments with the new
             * device names.
             */
            DisplayChoice virtualSource;
            string waitFailure;
            if (!WaitForAnyVirtualSource(10000, out virtualSource, out waitFailure))
            {
                AppendLog(string.IsNullOrWhiteSpace(waitFailure)
                    ? "拓扑变化后未重新发现虚拟显示器"
                    : waitFailure);
                return false;
            }

            Resolution desiredResolution;
            if (TryParseResolution(lastManagedVirtualResolution, out desiredResolution) &&
                !string.Equals(virtualSource.Resolution, lastManagedVirtualResolution, StringComparison.OrdinalIgnoreCase))
            {
                string modeMessage;
                Resolution appliedResolution;
                string appliedRefresh;
                if (!TryApplyDisplayMode(virtualSource.DeviceName, desiredResolution, lastManagedVirtualRefresh, lastManagedVirtualOrientation, out appliedResolution, out appliedRefresh, out modeMessage))
                {
                    AppendLog(modeMessage);
                    return false;
                }

                AppendLog(modeMessage);
                DisplayChoice switchedSource;
                if (!WaitForVirtualSourceMode(virtualSource.DeviceName, appliedResolution, 5000, out switchedSource))
                {
                    AppendLog("拓扑变化后虚拟模式未确认: " + virtualSource.DeviceName + " -> " + FormatResolution(appliedResolution));
                    return false;
                }

                virtualSource = switchedSource;
                lastManagedVirtualResolution = virtualSource.Resolution;
                lastManagedVirtualRefresh = virtualSource.Refresh;
            }

            RefreshDisplays();
            SelectComboByDevice(sourceDisplayCombo, virtualSource.DeviceName);
            sourceText.Text = virtualSource.DeviceName;

            DisplayChoice targetDisplay = targetDisplayCombo.SelectedItem as DisplayChoice;
            if (targetDisplay != null)
            {
                targetText.Text = targetDisplay.DeviceName;
                targetResolutionText.Text = targetDisplay.Resolution;
            }

            string targetSelector = targetText.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetSelector))
            {
                AppendLog("拓扑变化后未找到目标显示器");
                return false;
            }

            string targetResolutionForFilter = targetDisplay != null ? targetDisplay.Resolution : targetResolutionText.Text.Trim();
            lastNativeArgs = BuildSingleNativeArgs(virtualSource.DeviceName, virtualSource.Resolution, targetSelector, targetResolutionForFilter);
            AppendLog("拓扑变化后重新选择源: " + virtualSource);
            AppendSelectedDisplayLog();
            AppendLog("native 参数: " + lastNativeArgs);
            return StartNativeProcess(lastNativeArgs, true);
        }

        private string BuildSingleNativeArgs(string sourceSelector, string sourceResolutionForFilter, string targetSelector, string targetResolutionForFilter)
        {
            var args = new StringBuilder();
            args.Append("--source ").Append(Quote(sourceSelector));
            args.Append(" --target ").Append(Quote(targetSelector));
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
            return args.ToString();
        }

        private bool StartMultiScreenBeta(List<DisplayChoice> virtualSources, List<BridgePairConfig> pairs)
        {
            StopBetaProcesses();
            stoppingRequested = false;
            lastNativeArgs = "";
            process = null;

            int count = Math.Min(virtualSources.Count, pairs.Count);
            for (int i = 0; i < count; ++i)
            {
                DisplayChoice source = virtualSources[i];
                BridgePairConfig pair = pairs[i];
                if (pair.StreamOnly)
                {
                    AppendLog("BETA[" + (i + 1).ToString(CultureInfo.InvariantCulture) + "] 仅虚拟桌面: " + source.DeviceName);
                    AppendSunshineDisplayIdLog("BETA[" + (i + 1).ToString(CultureInfo.InvariantCulture) + "]", source);
                    continue;
                }
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
                    /*
                     * Issue #5: BETA native can be created immediately after virtual mode
                     * changes, while Windows is still committing the display topology. A
                     * dedicated source-unavailable exit means the native process did not
                     * crash; it only saw a stale/missing \\.\DISPLAYxx snapshot. Recover by
                     * keeping the host alive and rebuilding native processes from the latest
                     * display enumeration.
                     */
                    if (IsRecoverableNativeDisplayExit(exitCode))
                    {
                        RestartBridgeAfterTopologyChange("beta native[" + index.ToString(CultureInfo.InvariantCulture) + "] " + GetRecoverableNativeExitMessage(exitCode));
                        return;
                    }
                    RemoveExitedBetaProcesses();
                    AbortBridgeStart("多屏 BETA 子进程异常退出，已停止全部桥接");
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

        private bool WaitForDisplayTopologyToSettle(int timeoutMs, out string message)
        {
            message = "";
            string previousSignature = "";
            int stableSamples = 0;
            var deadline = Environment.TickCount + timeoutMs;

            while (Environment.TickCount < deadline)
            {
                if (deviceHostProcess != null && deviceHostProcess.HasExited)
                {
                    message = "拓扑变化恢复时虚拟显示器 host 已退出";
                    return false;
                }

                string listOutput = CaptureNativeOutput("--list");
                string signature = BuildDisplayListSignature(listOutput);
                if (!string.IsNullOrWhiteSpace(signature) &&
                    string.Equals(signature, previousSignature, StringComparison.Ordinal))
                {
                    ++stableSamples;
                    if (stableSamples >= DisplayTopologyStableSamples)
                    {
                        RefreshDisplays();
                        return true;
                    }
                }
                else
                {
                    previousSignature = signature;
                    stableSamples = string.IsNullOrWhiteSpace(signature) ? 0 : 1;
                }

                SleepWithUiPump(500);
            }

            message = "等待显示拓扑稳定超时";
            return false;
        }

        private static string BuildDisplayListSignature(string listOutput)
        {
            var lines = new List<string>();
            foreach (string rawLine in listOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                string line = rawLine.Trim();
                DisplayChoice display;
                if (TryParseDisplayLine(line, out display))
                {
                    lines.Add(line);
                }
            }
            lines.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("\n", lines.ToArray());
        }

        private bool RebindBetaTargetDisplays(out string message)
        {
            message = "";
            RefreshDisplays();
            RefreshBetaTargetChoices();

            updatingBetaPairGrid = true;
            try
            {
                foreach (DataGridViewRow row in betaPairGrid.Rows)
                {
                    if (!IsBetaRowEnabled(row) || IsBetaRowStreamOnly(row))
                    {
                        continue;
                    }

                    DisplayChoice previousTarget = row.Tag as DisplayChoice;
                    string requestedLabel = GetCellText(row, BetaColTarget);
                    DisplayChoice target = FindDisplayByTargetLabel(requestedLabel);
                    if (target == null && previousTarget != null)
                    {
                        target = GetDefaultPhysicalDisplay(previousTarget.DeviceName);
                    }
                    if (target == null)
                    {
                        message = "拓扑变化后未找到 BETA 目标显示器: " + requestedLabel;
                        return false;
                    }

                    string targetLabel = GetDisplayLabel(target);
                    AddComboItemIfMissing(BetaColTarget, targetLabel);
                    row.Tag = target;
                    row.Cells[BetaColTarget].Value = targetLabel;
                }
            }
            finally
            {
                updatingBetaPairGrid = false;
            }

            return true;
        }

        private bool RestoreBetaVirtualModesAfterTopologyChange(List<DisplayChoice> virtualSources, List<BridgePairConfig> betaPairs, out string message)
        {
            return RestoreBetaVirtualModesAfterTopologyChange(virtualSources, betaPairs, false, out message);
        }

        private bool RestoreBetaVirtualModesAfterTopologyChange(List<DisplayChoice> virtualSources, List<BridgePairConfig> betaPairs, bool absorbWindowsRuntimeMode, out string message)
        {
            message = "";
            int count = Math.Min(virtualSources.Count, betaPairs.Count);
            for (int i = 0; i < count; ++i)
            {
                DisplayChoice source = virtualSources[i];
                BridgePairConfig pair = betaPairs[i];

                /*
                 * Issue #7: after SBMS has created the virtual displays, Windows Settings
                 * should be allowed to own the live desktop topology. In rollback mode this
                 * block is skipped and the older strict restore path below reapplies the
                 * SBMS mapping table. In the BETA follow-Windows mode, recovery absorbs the
                 * current active virtual source mode into the running pair so a user rotation
                 * or valid mode change is not immediately undone while native output restarts.
                 */
                if (absorbWindowsRuntimeMode && TryAbsorbWindowsRuntimeVirtualMode(i + 1, source, pair))
                {
                    continue;
                }

                string desiredResolution = FormatResolution(pair.SourceResolution);
                if (string.Equals(source.Resolution, desiredResolution, StringComparison.OrdinalIgnoreCase))
                {
                    pair.Refresh = source.Refresh;
                    continue;
                }

                string modeMessage;
                Resolution appliedResolution;
                string appliedRefresh;
                if (!TryApplyDisplayMode(source.DeviceName, pair.SourceResolution, pair.Refresh, pair.Orientation, out appliedResolution, out appliedRefresh, out modeMessage))
                {
                    message = modeMessage;
                    return false;
                }

                AppendLog(modeMessage);
                DisplayChoice confirmedSource;
                if (!WaitForVirtualSourceMode(source.DeviceName, appliedResolution, 5000, out confirmedSource))
                {
                    message = "拓扑变化后虚拟模式未确认: " + source.DeviceName + " -> " + FormatResolution(appliedResolution);
                    return false;
                }

                virtualSources[i] = confirmedSource;
                pair.SourceResolution = appliedResolution;
                pair.Refresh = confirmedSource.Refresh;
            }

            return true;
        }

        private bool TryAbsorbWindowsRuntimeVirtualMode(int index, DisplayChoice source, BridgePairConfig pair)
        {
            Resolution runtimeResolution;
            if (source == null ||
                pair == null ||
                !source.Virtual ||
                !TryParseResolution(source.Resolution, out runtimeResolution) ||
                runtimeResolution.Width <= 0 ||
                runtimeResolution.Height <= 0)
            {
                return false;
            }

            string runtimeRefresh = string.IsNullOrWhiteSpace(source.Refresh) ? pair.Refresh : source.Refresh;
            int runtimeOrientation = NormalizeDisplayOrientation(source.Orientation);
            bool changed = !SameResolution(runtimeResolution, pair.SourceResolution) ||
                           !string.Equals(runtimeRefresh, pair.Refresh, StringComparison.OrdinalIgnoreCase) ||
                           runtimeOrientation != pair.Orientation;

            pair.SourceResolution = runtimeResolution;
            pair.Refresh = runtimeRefresh;
            pair.Orientation = runtimeOrientation;
            ApplyRuntimeModeToBetaRow(pair.Row, runtimeResolution, runtimeRefresh, runtimeOrientation);

            if (changed)
            {
                AppendLog("BETA[" + index.ToString(CultureInfo.InvariantCulture) + "] 跟随 Windows 虚拟模式: " +
                          source.DeviceName + " " + FormatResolution(runtimeResolution) +
                          (string.IsNullOrWhiteSpace(runtimeRefresh) ? "" : "@" + runtimeRefresh) +
                          " orientation=" + runtimeOrientation.ToString(CultureInfo.InvariantCulture));
                SaveConfigurationNow(false);
            }
            return true;
        }

        private void ApplyRuntimeModeToBetaRow(DataGridViewRow row, Resolution resolution, string refresh, int orientation)
        {
            if (row == null)
            {
                return;
            }

            int horizontal = Math.Max(resolution.Width, resolution.Height);
            int shortSide = Math.Min(resolution.Width, resolution.Height);
            int divisor = GreatestCommonDivisor(horizontal, shortSide);

            updatingBetaPairGrid = true;
            try
            {
                row.Cells[BetaColHorizontal].Value = horizontal.ToString(CultureInfo.InvariantCulture);
                row.Cells[BetaColAspect].Value = (horizontal / divisor).ToString(CultureInfo.InvariantCulture) + ":" +
                                                  (shortSide / divisor).ToString(CultureInfo.InvariantCulture);
                row.Cells[BetaColOrientation].Value = GetOrientationText(orientation);
                row.Cells[BetaColSource].Value = FormatResolution(resolution);
                if (!string.IsNullOrWhiteSpace(refresh))
                {
                    row.Cells[BetaColRefresh].Value = refresh;
                }
            }
            finally
            {
                updatingBetaPairGrid = false;
            }
        }

        private void RestartBridgeAfterTopologyChange(string source)
        {
            if (stoppingRequested || restartingAfterTopologyChange)
            {
                return;
            }
            restartingAfterTopologyChange = true;
            /*
             * Issue #8: This path intentionally has no recovery-count fuse. Windows Settings
             * may produce multiple short-lived topology/source snapshots while a user rotates
             * or drags virtual monitors. Treat each recoverable exit as a fresh chance to
             * settle, rebind, and rebuild native output while the device host stays alive.
             */
            AppendLog("检测到显示拓扑/虚拟源变化，恢复 native 输出: " + source);
            try
            {
                /*
                 * Issue #4: Windows Settings applies a display-layout edit while the
                 * SBMS software devices are still part of the active topology. Closing
                 * the device host here calls SwDeviceClose, which removes the virtual
                 * monitors in the middle of that transaction and can make Windows roll
                 * the topology change back.
                 *
                 * DXGI desktop duplication is allowed to die on topology changes, so the
                 * short-lived native mirror processes are stopped and recreated. The
                 * long-lived software-device host is deliberately kept alive until the
                 * user explicitly stops SBMS or exits the GUI.
                 */
                stoppingRequested = true;
                StopBetaProcesses();
                if (process != null && !process.HasExited)
                {
                    process.CloseMainWindow();
                    PostCloseToProcess(process.Id);
                    if (!process.WaitForExit(3000))
                    {
                        process.Kill();
                    }
                }
                process = null;

                stoppingRequested = false;

                string recoveryMessage;
                if (!WaitForDisplayTopologyToSettle(DisplayTopologySettleTimeoutMs, out recoveryMessage))
                {
                    AppendLog(recoveryMessage + "，虚拟显示器保持运行，可手动停止后重试");
                    return;
                }

                if (!RebindBetaTargetDisplays(out recoveryMessage))
                {
                    AppendLog(recoveryMessage + "，虚拟显示器保持运行，可手动停止后重试");
                    return;
                }

                List<BridgePairConfig> betaPairs;
                if (!TryGetEnabledBridgePairs(false, out betaPairs, out recoveryMessage))
                {
                    AppendLog(recoveryMessage + "，虚拟显示器保持运行，可手动停止后重试");
                    return;
                }

                List<DisplayChoice> virtualSources;
                if (!WaitForVirtualSources(betaPairs.Count, 15000, out virtualSources, out recoveryMessage))
                {
                    AppendLog((string.IsNullOrWhiteSpace(recoveryMessage)
                        ? "拓扑变化后等待虚拟显示器超时"
                        : recoveryMessage) + "，虚拟显示器保持运行，可手动停止后重试");
                    return;
                }

                if (!RestoreBetaVirtualModesAfterTopologyChange(virtualSources, betaPairs, followWindowsTopologyCheck.Checked, out recoveryMessage))
                {
                    AppendLog(recoveryMessage + "，虚拟显示器保持运行，可手动停止后重试");
                    return;
                }

                int outputPairCount = CountOutputBridgePairs(betaPairs);
                if (outputPairCount == 0)
                {
                    SetRunning(true);
                    AppendLog("拓扑变化后仍为仅虚拟桌面模式，未启动 native 输出");
                    AppendStreamOnlySunshineDisplayIds(virtualSources, betaPairs);
                    return;
                }

                if (!StartMultiScreenBeta(virtualSources, betaPairs))
                {
                    AppendLog("拓扑变化后 native 输出恢复失败，虚拟显示器保持运行，可手动停止后重试");
                    return;
                }

                SetRunning(true);
                AppendLog("拓扑变化后 native 输出已恢复，虚拟显示器 host 未重启");
            }
            catch
            {
            }
            finally
            {
                stoppingRequested = false;
                restartingAfterTopologyChange = false;
            }
        }

        private void AppendSelectedDisplayLog()
        {
            DisplayChoice sourceDisplay = sourceDisplayCombo.SelectedItem as DisplayChoice;
            DisplayChoice targetDisplay = targetDisplayCombo.SelectedItem as DisplayChoice;
            AppendLog("选择源: " + (sourceDisplay != null ? sourceDisplay.ToString() : sourceText.Text.Trim()));
            AppendLog("选择目标: " + (targetDisplay != null ? targetDisplay.ToString() : targetText.Text.Trim()));
        }

        private void AppendSunshineDisplayIdLog(string prefix, DisplayChoice source)
        {
            if (source == null)
            {
                return;
            }

            /*
             * Issue #6: Sunshine asks for its own stable display id instead of the
             * transient Windows \\.\DISPLAYxx name. SBMSNative resolves that id during
             * --list, and the GUI prints it only for stream-only virtual desktops so the
             * user can paste the exact value into Sunshine after the managed virtual
             * source has been created and mode-confirmed.
             */
            if (!string.IsNullOrWhiteSpace(source.SunshineId))
            {
                AppendLog(prefix + " Sunshine显示器ID: " + source.SunshineId + " (" + source.DeviceName + ")");
            }
            else
            {
                AppendLog(prefix + " Sunshine显示器ID未解析: " + source.DeviceName);
            }
        }

        private void AppendStreamOnlySunshineDisplayIds(List<DisplayChoice> virtualSources, List<BridgePairConfig> pairs)
        {
            int count = Math.Min(virtualSources.Count, pairs.Count);
            for (int i = 0; i < count; ++i)
            {
                if (pairs[i].StreamOnly)
                {
                    AppendSunshineDisplayIdLog("BETA[" + (i + 1).ToString(CultureInfo.InvariantCulture) + "]", virtualSources[i]);
                }
            }
        }

        private bool StartDeviceHost(int virtualDeviceCount)
        {
            if (IsDeviceHostRunning())
            {
                return true;
            }

            SignalDeviceHostStop();
            deviceHostLog.Length = 0;
            int requestedCount = Math.Max(1, Math.Min(virtualDeviceCount, 3));
            deviceHostProcess = new Process();
            deviceHostProcess.StartInfo.FileName = deviceHostExe;
            deviceHostProcess.StartInfo.Arguments = "--count " + requestedCount.ToString(CultureInfo.InvariantCulture);
            deviceHostProcess.StartInfo.WorkingDirectory = root;
            deviceHostProcess.StartInfo.UseShellExecute = false;
            deviceHostProcess.StartInfo.RedirectStandardOutput = true;
            deviceHostProcess.StartInfo.RedirectStandardError = true;
            deviceHostProcess.StartInfo.CreateNoWindow = true;
            deviceHostProcess.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            deviceHostProcess.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            deviceHostProcess.EnableRaisingEvents = true;
            deviceHostProcess.OutputDataReceived += OnOutput;
            deviceHostProcess.ErrorDataReceived += OnOutput;
            deviceHostProcess.OutputDataReceived += OnDeviceHostOutput;
            deviceHostProcess.ErrorDataReceived += OnDeviceHostOutput;
            Process startedHost = deviceHostProcess;
            deviceHostProcess.Exited += delegate
            {
                BeginInvoke((Action)delegate
                {
                    if (deviceHostProcess != startedHost)
                    {
                        return;
                    }
                    int exitCode = GetProcessExitCode(startedHost);
                    AppendLog("虚拟显示器 host 已退出 exit=" + exitCode.ToString(CultureInfo.InvariantCulture));
                    deviceHostProcess = null;
                    if (stoppingRequested || exiting)
                    {
                        RefreshDisplays();
                        UpdateStatus();
                        return;
                    }
                    AbortBridgeStart("虚拟显示器 host 异常退出，已停止桥接");
                });
            };

            try
            {
                deviceHostProcess.Start();
                deviceHostProcess.BeginOutputReadLine();
                deviceHostProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                AppendLog("虚拟显示器 host 启动失败: " + ex.Message);
                deviceHostProcess = null;
                return false;
            }
            AppendLog("虚拟显示器 host 已启动 count=" + requestedCount.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        private bool TryGetVirtualDisplayLoadFailure(ref int nextProblemCheck, out string message)
        {
            message = "";
            if (Environment.TickCount < nextProblemCheck)
            {
                return false;
            }

            nextProblemCheck = Environment.TickCount + 3000;
            return TryGetIddSampleDriverProblem(out message);
        }

        private static bool TryGetIddSampleDriverProblem(out string message)
        {
            message = "";
            string output;
            RunTool("pnputil.exe", "/enum-devices /instanceid \"SWD\\IDDSAMPLEDRIVER\\IDDSAMPLEDRIVER\" /drivers /properties", out output);
            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            var details = new List<string>();
            string statusLine = FindOutputLine(output, "Status:");
            string problemLine = FindOutputLine(output, "Problem Code:");
            string hasProblemValue = FindPropertyValue(output, "DEVPKEY_Device_HasProblem");

            bool hasProblemLine = !string.IsNullOrWhiteSpace(problemLine) &&
                                  problemLine.IndexOf("CM_PROB_NONE", StringComparison.OrdinalIgnoreCase) < 0 &&
                                  !Regex.IsMatch(problemLine, @"Problem Code:\s*0\b", RegexOptions.IgnoreCase);
            bool statusFailed = !string.IsNullOrWhiteSpace(statusLine) &&
                                (statusLine.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 statusLine.IndexOf("problem", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 statusLine.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0);
            bool propertyFailed = string.Equals(hasProblemValue, "TRUE", StringComparison.OrdinalIgnoreCase);

            if (!hasProblemLine && !statusFailed && !propertyFailed)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(statusLine))
            {
                details.Add(statusLine);
            }
            if (!string.IsNullOrWhiteSpace(problemLine))
            {
                details.Add(problemLine);
            }
            if (!string.IsNullOrWhiteSpace(hasProblemValue))
            {
                details.Add("HasProblem=" + hasProblemValue);
            }

            string problemStatusLine = FindOutputLine(output, "Problem Status:");
            if (!string.IsNullOrWhiteSpace(problemStatusLine))
            {
                details.Add(problemStatusLine);
            }
            string configLine = FindPropertyValue(output, "DEVPKEY_Device_ConfigurationId");
            if (!string.IsNullOrWhiteSpace(configLine))
            {
                details.Add("Configuration=" + configLine);
            }
            string infPathLine = FindPropertyValue(output, "DEVPKEY_Device_DriverInfPath");
            if (!string.IsNullOrWhiteSpace(infPathLine))
            {
                details.Add("DriverInf=" + infPathLine);
            }
            string versionLine = FindPropertyValue(output, "DEVPKEY_Device_DriverVersion");
            if (!string.IsNullOrWhiteSpace(versionLine))
            {
                details.Add("DriverVersion=" + versionLine);
            }
            string driverLine = FindOutputLine(output, "Driver Name:");
            if (!string.IsNullOrWhiteSpace(driverLine))
            {
                details.Add(driverLine);
            }
            string serviceLine = FindOutputLine(output, "Service:");
            if (!string.IsNullOrWhiteSpace(serviceLine))
            {
                details.Add(serviceLine);
            }
            AddRecentKernelPnpDetails(details);

            message = "虚拟显示器驱动加载失败: " + string.Join("; ", details.ToArray());
            return true;
        }

        private static void AddRecentKernelPnpDetails(List<string> details)
        {
            try
            {
                var query = new EventLogQuery(
                    "Microsoft-Windows-Kernel-PnP/Configuration",
                    PathType.LogName,
                    "*[System[EventID=411]]");
                query.ReverseDirection = true;

                using (var reader = new EventLogReader(query))
                {
                    for (int i = 0; i < 24; ++i)
                    {
                        EventRecord record = reader.ReadEvent();
                        if (record == null)
                        {
                            break;
                        }
                        try
                        {
                            if (AddKernelPnpRecordDetails(details, record))
                            {
                                break;
                            }
                        }
                        finally
                        {
                            record.Dispose();
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static bool AddKernelPnpRecordDetails(List<string> details, EventRecord record)
        {
            string xml = record.ToXml();
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            string deviceId = FindEventDataValue(doc, "DeviceInstanceId");
            if (deviceId.IndexOf("IddSampleDriver", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            AddDetail(details, "KernelPnP=411");
            AddDetail(details, "KernelDriver=" + FindEventDataValue(doc, "DriverName"));
            AddDetail(details, "KernelService=" + FindEventDataValue(doc, "ServiceName"));
            AddDetail(details, "KernelUpperFilters=" + FindEventDataValue(doc, "UpperFilters"));
            AddDetail(details, "KernelProblem=" + FindEventDataValue(doc, "Problem"));
            AddDetail(details, "KernelProblemStatus=" + FindEventDataValue(doc, "Status"));
            return true;
        }

        private static string FindEventDataValue(XmlDocument doc, string name)
        {
            XmlNode node = doc.SelectSingleNode("//*[local-name()='Data' and @Name='" + name + "']");
            return node == null ? "" : node.InnerText.Trim();
        }

        private static void AddDetail(List<string> details, string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return;
            }
            foreach (string existing in details)
            {
                if (string.Equals(existing, detail, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            details.Add(detail);
        }

        private static string FindPropertyValue(string text, string propertyName)
        {
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; ++i)
            {
                if (lines[i].IndexOf(propertyName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                for (int j = i + 1; j < lines.Length; ++j)
                {
                    string value = lines[j].Trim();
                    if (value.Length == 0)
                    {
                        continue;
                    }
                    if (value.IndexOf("[", StringComparison.Ordinal) >= 0 &&
                        value.IndexOf("]", StringComparison.Ordinal) >= 0)
                    {
                        return "";
                    }
                    return value;
                }
            }
            return "";
        }

        private static string FindOutputLine(string text, string prefix)
        {
            foreach (string rawLine in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                string line = rawLine.Trim();
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return line;
                }
            }
            return "";
        }

        private bool WaitForSourceDisplay(string source, int timeoutMs)
        {
            var deadline = Environment.TickCount + timeoutMs;
            int nextProblemCheck = Environment.TickCount + 1500;
            while (Environment.TickCount < deadline)
            {
                if (deviceHostProcess != null && deviceHostProcess.HasExited)
                {
                    return false;
                }
                string loadFailure;
                if (TryGetVirtualDisplayLoadFailure(ref nextProblemCheck, out loadFailure))
                {
                    AppendLog(loadFailure);
                    return false;
                }
                var list = CaptureNativeOutput("--list");
                DisplayChoice display;
                if (TryFindVirtualSource(source, list, out display))
                {
                    RefreshDisplays();
                    return true;
                }
                SleepWithUiPump(500);
            }
            return false;
        }

        private bool WaitForAnyVirtualSource(int timeoutMs, out DisplayChoice source, out string failureMessage)
        {
            var deadline = Environment.TickCount + timeoutMs;
            int nextProblemCheck = Environment.TickCount + 1500;
            source = null;
            failureMessage = "";
            while (Environment.TickCount < deadline)
            {
                if (deviceHostProcess != null && deviceHostProcess.HasExited)
                {
                    return false;
                }
                if (TryGetVirtualDisplayLoadFailure(ref nextProblemCheck, out failureMessage))
                {
                    return false;
                }
                var list = CaptureNativeOutput("--list");
                if (TryFindFirstVirtualSource(list, out source))
                {
                    RefreshDisplays();
                    return true;
                }
                SleepWithUiPump(500);
            }
            return false;
        }

        private bool WaitForVirtualSources(int minimumCount, int timeoutMs, out List<DisplayChoice> sources, out string failureMessage)
        {
            var deadline = Environment.TickCount + timeoutMs;
            int nextProblemCheck = Environment.TickCount + 1500;
            sources = new List<DisplayChoice>();
            failureMessage = "";
            while (Environment.TickCount < deadline)
            {
                if (deviceHostProcess != null && deviceHostProcess.HasExited)
                {
                    return false;
                }
                if (TryGetVirtualDisplayLoadFailure(ref nextProblemCheck, out failureMessage))
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
                SleepWithUiPump(500);
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
            int nextProblemCheck = Environment.TickCount + 1500;
            source = null;
            while (Environment.TickCount < deadline)
            {
                if (deviceHostProcess != null && deviceHostProcess.HasExited)
                {
                    return false;
                }
                string loadFailure;
                if (TryGetVirtualDisplayLoadFailure(ref nextProblemCheck, out loadFailure))
                {
                    AppendLog(loadFailure);
                    return false;
                }
                var list = CaptureNativeOutput("--list");
                if (TryFindVirtualSource(selector, list, out source))
                {
                    RefreshDisplays();
                    return true;
                }
                SleepWithUiPump(500);
            }
            return false;
        }

        private bool WaitForVirtualSourceMode(string deviceName, Resolution resolution, int timeoutMs, out DisplayChoice source)
        {
            var deadline = Environment.TickCount + timeoutMs;
            int nextProblemCheck = Environment.TickCount + 1500;
            source = null;
            while (Environment.TickCount < deadline)
            {
                if (deviceHostProcess != null && deviceHostProcess.HasExited)
                {
                    return false;
                }
                string loadFailure;
                if (TryGetVirtualDisplayLoadFailure(ref nextProblemCheck, out loadFailure))
                {
                    AppendLog(loadFailure);
                    return false;
                }
                var list = CaptureNativeOutput("--list");
                if (TryFindVirtualSourceMode(deviceName, resolution, list, out source))
                {
                    RefreshDisplays();
                    return true;
                }
                SleepWithUiPump(500);
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

        private static bool TryFindVirtualSourceMode(string deviceName, Resolution resolution, string listOutput, out DisplayChoice source)
        {
            source = null;
            string resolutionText = FormatResolution(resolution);
            foreach (string rawLine in listOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                DisplayChoice display;
                if (!TryParseDisplayLine(rawLine.Trim(), out display) || !display.Virtual)
                {
                    continue;
                }
                if (string.Equals(display.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(display.Resolution, resolutionText, StringComparison.OrdinalIgnoreCase))
                {
                    source = display;
                    return true;
                }
            }
            return false;
        }

        private string GetSingleMappingRefresh(DisplayChoice fallbackDisplay)
        {
            DisplayChoice targetDisplay = targetDisplayCombo.SelectedItem as DisplayChoice;
            return GetRefreshOrDefault(singleRefreshText.Text, targetDisplay ?? fallbackDisplay);
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

        private static bool SameResolution(Resolution left, Resolution right)
        {
            return left.Width == right.Width && left.Height == right.Height;
        }

        private static List<DisplayModeCandidate> GetDisplayModeCandidates(string deviceName)
        {
            var candidates = new List<DisplayModeCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int modeIndex = 0; modeIndex < 1024; ++modeIndex)
            {
                var mode = new DEVMODE();
                mode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (!EnumDisplaySettings(deviceName, modeIndex, ref mode))
                {
                    break;
                }
                if (mode.dmPelsWidth <= 0 || mode.dmPelsHeight <= 0)
                {
                    continue;
                }

                string key = mode.dmPelsWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                             mode.dmPelsHeight.ToString(CultureInfo.InvariantCulture) + "@" +
                             mode.dmDisplayFrequency.ToString(CultureInfo.InvariantCulture);
                if (seen.Contains(key))
                {
                    continue;
                }

                seen.Add(key);
                candidates.Add(new DisplayModeCandidate
                {
                    Resolution = new Resolution { Width = mode.dmPelsWidth, Height = mode.dmPelsHeight },
                    Refresh = mode.dmDisplayFrequency
                });
            }
            return candidates;
        }

        private static bool TrySelectSupportedDisplayMode(string deviceName, Resolution requestedResolution, string requestedRefreshText, out Resolution selectedResolution, out string selectedRefreshText, out string snapMessage)
        {
            selectedResolution = requestedResolution;
            selectedRefreshText = requestedRefreshText;
            snapMessage = "";

            List<DisplayModeCandidate> candidates = GetDisplayModeCandidates(deviceName);
            if (candidates.Count == 0)
            {
                return false;
            }

            int requestedRefresh;
            bool hasRequestedRefresh = int.TryParse(requestedRefreshText, out requestedRefresh) && requestedRefresh > 0;
            double requestedAspect = requestedResolution.Height > 0
                ? requestedResolution.Width / (double)requestedResolution.Height
                : 0.0;
            DisplayModeCandidate best = null;
            double bestScore = double.MaxValue;

            for (int i = 0; i < candidates.Count; ++i)
            {
                DisplayModeCandidate candidate = candidates[i];
                bool exactResolution = SameResolution(candidate.Resolution, requestedResolution);
                double aspect = candidate.Resolution.Height > 0
                    ? candidate.Resolution.Width / (double)candidate.Resolution.Height
                    : 0.0;
                double aspectError = requestedAspect > 0.0
                    ? Math.Abs(aspect - requestedAspect) / requestedAspect
                    : 0.0;
                if (!exactResolution && aspectError > 0.02)
                {
                    continue;
                }

                double sizeError = Math.Abs(candidate.Resolution.Width - requestedResolution.Width) +
                                   Math.Abs(candidate.Resolution.Height - requestedResolution.Height);
                double refreshError = 0.0;
                if (hasRequestedRefresh && candidate.Refresh > 0)
                {
                    refreshError = Math.Abs(candidate.Refresh - requestedRefresh) * 0.05;
                }
                else if (!hasRequestedRefresh && candidate.Refresh > 0)
                {
                    refreshError = -candidate.Refresh * 0.001;
                }
                double score = (exactResolution ? 0.0 : sizeError + aspectError * 10000.0) + refreshError;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best == null)
            {
                return false;
            }

            selectedResolution = best.Resolution;
            selectedRefreshText = best.Refresh > 0
                ? best.Refresh.ToString(CultureInfo.InvariantCulture)
                : requestedRefreshText;

            if (!SameResolution(selectedResolution, requestedResolution) ||
                (hasRequestedRefresh && best.Refresh > 0 && best.Refresh != requestedRefresh))
            {
                snapMessage = "虚拟模式贴合可用模式: requested=" + FormatResolution(requestedResolution) +
                              (hasRequestedRefresh ? "@" + requestedRefresh.ToString(CultureInfo.InvariantCulture) : "") +
                              " applied=" + FormatResolution(selectedResolution) +
                              (best.Refresh > 0 ? "@" + best.Refresh.ToString(CultureInfo.InvariantCulture) : "");
            }
            return true;
        }

        private static bool TryApplyDisplayMode(string deviceName, Resolution resolution, string refreshText, int orientation, out Resolution appliedResolution, out string appliedRefresh, out string message)
        {
            appliedResolution = resolution;
            appliedRefresh = refreshText;
            Resolution selectedResolution;
            string selectedRefreshText;
            string snapMessage;
            if (!TrySelectSupportedDisplayMode(deviceName, resolution, refreshText, out selectedResolution, out selectedRefreshText, out snapMessage))
            {
                selectedResolution = resolution;
                selectedRefreshText = refreshText;
                snapMessage = "";
            }

            var devMode = new DEVMODE();
            devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref devMode))
            {
                message = "读取虚拟显示器当前模式失败: " + deviceName;
                return false;
            }

            int refresh;
            bool hasRefresh = int.TryParse(selectedRefreshText, out refresh) && refresh > 0;
            devMode.dmPelsWidth = selectedResolution.Width;
            devMode.dmPelsHeight = selectedResolution.Height;
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
                appliedResolution = selectedResolution;
                appliedRefresh = hasRefresh ? refresh.ToString(CultureInfo.InvariantCulture) : selectedRefreshText;
                message = (snapMessage.Length > 0 ? snapMessage + "; " : "") +
                          "虚拟模式切换成功: " + deviceName + " -> " + FormatResolution(selectedResolution) + (hasRefresh ? "@" + refresh.ToString(CultureInfo.InvariantCulture) : "") + " orientation=" + orientation;
                return true;
            }

            if (hasRefresh)
            {
                devMode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYORIENTATION;
                result = ChangeDisplaySettingsEx(deviceName, ref devMode, IntPtr.Zero, 0, IntPtr.Zero);
                if (result == DISP_CHANGE_SUCCESSFUL)
                {
                    appliedResolution = selectedResolution;
                    appliedRefresh = "";
                    message = (snapMessage.Length > 0 ? snapMessage + "; " : "") +
                              "虚拟模式切换成功: " + deviceName + " -> " + FormatResolution(selectedResolution) + " orientation=" + orientation;
                    return true;
                }
            }

            message = (snapMessage.Length > 0 ? snapMessage + "; " : "") +
                      "虚拟模式切换失败: " + deviceName + " -> " + FormatResolution(selectedResolution) + " result=" + result;
            return false;
        }

        private string CaptureNativeOutput(string args)
        {
            using (var p = CreateProcess(args))
            {
                string output;
                if (CaptureProcessOutput(p, 3000, out output))
                {
                    return output;
                }
                return output.Length > 0 ? output : "SBMSNative error: " + args;
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

        private void AbortBridgeStart(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                AppendLog(message);
            }

            stoppingRequested = true;
            StopBetaProcesses();
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.CloseMainWindow();
                        PostCloseToProcess(process.Id);
                        if (!process.WaitForExit(3000))
                        {
                            process.Kill();
                        }
                    }
                }
                catch
                {
                }
                process = null;
            }

            StopDeviceHost();
            if (!WaitForVirtualDisplaysToClear(5000))
            {
                AppendLog("虚拟显示器未在超时内全部移除，已刷新当前列表");
            }
            else
            {
                RefreshDisplays();
            }
            lastNativeArgs = "";
            restartingAfterTopologyChange = false;
            stoppingRequested = false;
            SetRunning(false);
        }

        private bool WaitForVirtualDisplaysToClear(int timeoutMs)
        {
            var deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                string list = CaptureNativeOutput("--list");
                if (ParseVirtualSources(list).Count == 0)
                {
                    return true;
                }
                SleepWithUiPump(250);
            }
            RefreshDisplays();
            return false;
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
                   IsDeviceHostRunning();
        }

        private bool IsDeviceHostRunning()
        {
            try
            {
                return deviceHostProcess != null && !deviceHostProcess.HasExited;
            }
            catch
            {
                return false;
            }
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
                SaveConfigurationNow(false);
                HideToTray();
                return;
            }

            trayIcon.Visible = false;
            SaveConfigurationNow(false);
            StopBridge();
        }

        private void HideToTray()
        {
            ToggleInlineConfig(false);
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
            SaveConfigurationNow(false);
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
            if (text == null)
            {
                text = "";
            }
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            foreach (string rawLine in lines)
            {
                if (rawLine.Length == 0)
                {
                    continue;
                }
                AppendLogLine(rawLine);
            }
            if (text.IndexOf("0x80070005", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AppendLogLine("需要管理员权限创建虚拟显示器，请以管理员身份运行 GUI。");
            }
        }

        private void AppendLogLine(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + text;
            logText.AppendText(line + Environment.NewLine);
            WriteDiagnosticLine(line, IsErrorLogLine(text));
        }

        private static bool IsErrorLogLine(string text)
        {
            if (text == null)
            {
                return false;
            }
            string lower = text.ToLowerInvariant();
            if (lower.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("failure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("hasproblem=true", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("problem code", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("problem status", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("失败", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("错误", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("异常", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("超时", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("找不到", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("未确认", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("不足", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("无效", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("强制结束", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            Match exitMatch = Regex.Match(text, @"exit\s*=\s*(-?\d+)", RegexOptions.IgnoreCase);
            if (exitMatch.Success)
            {
                int exitCode;
                if (int.TryParse(exitMatch.Groups[1].Value, out exitCode))
                {
                    return exitCode != 0;
                }
            }
            return false;
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
            startButton.Enabled = true;
            stopButton.Enabled = true;
            displayList.Enabled = true;
            sourceText.Enabled = true;
            targetText.Enabled = true;
            singleRefreshText.Enabled = true;
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
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                MainForm.WriteFatalError("thread", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Exception exception = e.ExceptionObject as Exception;
                if (exception == null)
                {
                    exception = new Exception(Convert.ToString(e.ExceptionObject, CultureInfo.InvariantCulture));
                }
                MainForm.WriteFatalError("domain", exception);
            };
            if (args.Length >= 2 && string.Equals(args[0], "--config-probe", StringComparison.OrdinalIgnoreCase))
            {
                using (var form = new MainForm())
                {
                    string tabs = form.RunConfigProbe(args[1]);
                    Console.WriteLine("config_probe_tabs=" + tabs);
                }
                Application.ExitThread();
                Environment.Exit(0);
                return;
            }
            if (args.Length >= 2 && string.Equals(args[0], "--risk-probe", StringComparison.OrdinalIgnoreCase))
            {
                using (var form = new MainForm())
                {
                    form.RunRiskProbe(args[1]);
                    Console.WriteLine("risk_probe=" + args[1]);
                }
                Application.ExitThread();
                Environment.Exit(0);
                return;
            }
            if (args.Length >= 2 && string.Equals(args[0], "--stream-config-probe", StringComparison.OrdinalIgnoreCase))
            {
                using (var form = new MainForm())
                {
                    form.RunStreamConfigProbe(args[1]);
                    Console.WriteLine("stream_config_probe=" + args[1]);
                }
                Application.ExitThread();
                Environment.Exit(0);
                return;
            }
            if (args.Length >= 2 && string.Equals(args[0], "--lock-probe", StringComparison.OrdinalIgnoreCase))
            {
                using (var form = new MainForm())
                {
                    form.RunLockProbe(args[1]);
                    Console.WriteLine("lock_probe=" + args[1]);
                }
                Application.ExitThread();
                Environment.Exit(0);
                return;
            }
            Application.Run(new MainForm());
        }
    }
}
