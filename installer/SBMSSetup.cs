using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace SBMSSetup
{
    internal sealed class SetupForm : Form
    {
        private readonly string sourceRoot;
        private readonly string installRoot;
        private readonly Label titleLabel = new Label();
        private readonly TextBox logText = new TextBox();
        private readonly CheckBox driverCheck = new CheckBox();
        private readonly CheckBox shortcutCheck = new CheckBox();
        private readonly CheckBox startupCheck = new CheckBox();
        private readonly GlowButton installButton = new GlowButton();
        private readonly GlowButton uninstallButton = new GlowButton();
        private readonly GlowButton openButton = new GlowButton();
        private readonly GlowButton languageButton = new GlowButton();
        private readonly string setupLogPath;
        private string stagedReleaseRoot;
        private string installBackupRoot;
        private bool installedPayloadCommitted;
        private bool english = true;
        private const string SetupBuildLabel = SBMSBuild.ProductVersionInfo.SemVer;

        private static readonly Color ThemeBack = Color.FromArgb(0, 10, 4);
        private static readonly Color ThemeText = Color.White;
        private static readonly Color ThemeActive = Color.FromArgb(72, 255, 0);
        private static readonly Color ThemeRed = Color.Red;

        private sealed class GlowButton : Button
        {
            private bool hover;
            private bool pressed;

            public bool DangerFill { get; set; }
            public bool ActiveFill { get; set; }

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
                Cursor = Cursors.Hand;
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
                Color fill = DangerFill ? ThemeRed : ThemeBack;
                Color text = DangerFill ? (hot ? ThemeText : ThemeRed) : ThemeText;
                Color corner = DangerFill ? (hot ? ThemeText : ThemeRed) : (hot ? ThemeActive : ThemeText);
                Color border = DangerFill ? ThemeRed : (hot ? ThemeActive : ThemeText);
                Rectangle rect = new Rectangle(1, 1, Width - 3, Height - 3);

                using (Brush background = new SolidBrush(Parent != null ? Parent.BackColor : ThemeBack))
                {
                    e.Graphics.FillRectangle(background, ClientRectangle);
                }
                if (hot)
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
                DrawCornerGlyph(e.Graphics, rect, corner, hot);
                Rectangle textBounds = GetTextBounds(rect);
                TextRenderer.DrawText(
                    e.Graphics,
                    Text,
                    Font,
                    textBounds,
                    text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
            }

            private static int GetCornerSize(Rectangle bounds)
            {
                return Math.Min(bounds.Height - 3, Math.Max(24, bounds.Height - 8));
            }

            private Rectangle GetTextBounds(Rectangle bounds)
            {
                int inset = GetCornerSize(bounds) + 18;
                return new Rectangle(bounds.Left + inset, bounds.Top, Math.Max(0, bounds.Width - inset - 18), bounds.Height);
            }

            private int GetRequiredWidth(int height)
            {
                if (string.IsNullOrEmpty(Text))
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

        public SetupForm()
        {
            sourceRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SBMS");
            setupLogPath = CreateSetupLogPath();

            Text = "SBMS Setup";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(720, 460);
            MinimumSize = new Size(640, 400);
            BackColor = ThemeBack;
            ForeColor = ThemeText;
            Font = new Font("Segoe UI", 9F);

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(14);
            root.RowCount = 4;
            root.ColumnCount = 1;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(root);

            var titleRow = new TableLayoutPanel();
            titleRow.Dock = DockStyle.Fill;
            titleRow.ColumnCount = 2;
            titleRow.RowCount = 1;
            titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            root.Controls.Add(titleRow, 0, 0);

            titleLabel.Text = "SBMS";
            titleLabel.Dock = DockStyle.Fill;
            titleLabel.Font = new Font("Segoe UI", 18F, FontStyle.Bold);
            titleLabel.ForeColor = ThemeText;
            titleRow.Controls.Add(titleLabel, 0, 0);

            languageButton.Width = 96;
            languageButton.Height = 32;
            languageButton.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            titleRow.Controls.Add(languageButton, 1, 0);

            var options = new FlowLayoutPanel();
            options.Dock = DockStyle.Fill;
            options.FlowDirection = FlowDirection.LeftToRight;
            driverCheck.Checked = true;
            StyleOption(driverCheck, 230);
            shortcutCheck.Checked = true;
            StyleOption(shortcutCheck, 190);
            StyleOption(startupCheck, 190);
            options.Controls.Add(driverCheck);
            options.Controls.Add(shortcutCheck);
            options.Controls.Add(startupCheck);
            root.Controls.Add(options, 0, 1);

            logText.Dock = DockStyle.Fill;
            logText.Multiline = true;
            logText.ReadOnly = true;
            logText.ScrollBars = ScrollBars.Vertical;
            logText.Font = new Font("Consolas", 10F);
            logText.BackColor = ThemeBack;
            logText.ForeColor = ThemeText;
            root.Controls.Add(logText, 0, 2);

            var buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            installButton.Width = 126;
            uninstallButton.Width = 150;
            openButton.Width = 190;
            installButton.Height = 34;
            uninstallButton.Height = 34;
            openButton.Height = 34;
            uninstallButton.AccessibleDescription = "risk";
            buttons.Controls.Add(installButton);
            buttons.Controls.Add(uninstallButton);
            buttons.Controls.Add(openButton);
            root.Controls.Add(buttons, 0, 3);

            StyleButton(languageButton);
            StyleButton(installButton);
            StyleButton(uninstallButton);
            StyleButton(openButton);

            languageButton.Click += delegate { english = !english; ApplyLanguage(); };
            installButton.Click += delegate { RunGuarded(Install); };
            uninstallButton.Click += delegate { RunGuarded(UninstallFiles); };
            openButton.Click += delegate
            {
                if (Directory.Exists(installRoot))
                {
                    Process.Start("explorer.exe", installRoot);
                }
            };

            ApplyLanguage();
            Log("setupVersion=" + SetupBuildLabel);
            Log("source=" + sourceRoot);
            Log("target=" + installRoot);
            Log("admin=" + Program.IsAdministrator().ToString());
            Log("setupLog=" + setupLogPath);
        }

        private static void StyleButton(Button button)
        {
            GlowButton glow = button as GlowButton;
            if (glow != null)
            {
                glow.DangerFill = string.Equals(glow.AccessibleDescription, "risk", StringComparison.OrdinalIgnoreCase);
                glow.FlatAppearance.BorderSize = 0;
                glow.FlatAppearance.MouseOverBackColor = ThemeBack;
                glow.FlatAppearance.MouseDownBackColor = ThemeBack;
                glow.Invalidate();
                return;
            }
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.BackColor = ThemeBack;
            button.ForeColor = ThemeText;
            button.FlatAppearance.BorderColor = ThemeText;
            button.FlatAppearance.MouseOverBackColor = ThemeActive;
            button.FlatAppearance.MouseDownBackColor = ThemeActive;
        }

        private static void StyleOption(CheckBox checkBox, int width)
        {
            checkBox.Appearance = Appearance.Button;
            checkBox.AutoSize = false;
            checkBox.Width = width;
            checkBox.Height = 30;
            checkBox.TextAlign = ContentAlignment.MiddleCenter;
            checkBox.FlatStyle = FlatStyle.Flat;
            checkBox.UseVisualStyleBackColor = false;
            checkBox.Margin = new Padding(0, 0, 8, 0);
            checkBox.FlatAppearance.BorderColor = ThemeText;
            checkBox.FlatAppearance.MouseOverBackColor = ThemeActive;
            checkBox.FlatAppearance.MouseDownBackColor = ThemeActive;
            checkBox.CheckedChanged += delegate { ApplyOptionVisual(checkBox); };
            ApplyOptionVisual(checkBox);
        }

        private static void ApplyOptionVisual(CheckBox checkBox)
        {
            checkBox.BackColor = checkBox.Checked ? ThemeActive : ThemeBack;
            checkBox.ForeColor = checkBox.Checked ? ThemeBack : ThemeText;
        }

        private void ApplyLanguage()
        {
            Text = T("SBMS Setup");
            titleLabel.Text = "SBMS";
            languageButton.Text = english ? "语言" : "lang";
            driverCheck.Text = T("Stage verified virtual display driver package");
            shortcutCheck.Text = T("Start Menu shortcut");
            startupCheck.Text = T("Start with Windows");
            installButton.Text = T("Install");
            uninstallButton.Text = T("Remove files");
            openButton.Text = T("Open install folder");
        }

        private string T(string text)
        {
            if (english)
            {
                return text;
            }

            switch (text)
            {
                case "SBMS Setup":
                    return "SBMS 安装器";
                case "Stage verified virtual display driver package":
                    return "暂存已验证的虚拟显示驱动包";
                case "Start Menu shortcut":
                    return "开始菜单快捷方式";
                case "Start with Windows":
                    return "开机自启";
                case "Install":
                    return "安装";
                case "Remove files":
                    return "卸载文件";
                case "Open install folder":
                    return "打开安装目录";
                case "done":
                    return "完成";
                case "error":
                    return "错误";
                case "Please exit running ":
                    return "请先退出正在运行的 ";
                case "already in Program Files":
                    return "已在 Program Files 中";
                case "copied files":
                    return "已复制文件";
                case "driver package staged":
                    return "驱动包已暂存";
                case "shortcut applied":
                    return "快捷方式已应用";
                case "startup task applied":
                    return "自启已应用";
                case "removed shortcut":
                    return "已删除快捷方式";
                case "removed Program Files copy":
                    return "已删除 Program Files 副本";
                case "driver package is left installed; remove it manually only when display recovery is confirmed":
                    return "驱动包已保留；确认显示恢复后再手动移除";
                default:
                    return text;
            }
        }

        private void RunGuarded(Action action)
        {
            installButton.Enabled = false;
            uninstallButton.Enabled = false;
            try
            {
                action();
                Log(T("done"));
            }
            catch (Exception ex)
            {
                Log(T("error") + "=" + ex.Message);
            }
            finally
            {
                installButton.Enabled = true;
                uninstallButton.Enabled = true;
            }
        }

        private void Install()
        {
            EnsurePayload();
            bool installSucceeded = false;
            try
            {
                InstallTransaction.Execute(
                    VerifyRelease,
                    StageAndReverify,
                    EnsureNotRunning,
                    CopyPayload,
                    driverCheck.Checked ? (Action)InstallDriver : delegate { },
                    shortcutCheck.Checked ? (Action)CreateShortcutBestEffort : delegate { },
                    startupCheck.Checked ? (Action)CreateStartupTaskBestEffort : delegate { });
                installSucceeded = true;
            }
            finally
            {
                CleanupStaging(installSucceeded);
            }
        }

        private void StageAndReverify()
        {
            if (!SBMSBuild.ProductionSigningInfo.IntegrityRequired)
            {
                return;
            }

            string stagingParent = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "SBMS.Staging");
            Directory.CreateDirectory(stagingParent);
            stagedReleaseRoot = Path.Combine(stagingParent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagedReleaseRoot);
            File.Copy(
                Path.Combine(sourceRoot, "SBMS.release.cat"),
                Path.Combine(stagedReleaseRoot, "SBMS.release.cat"),
                false);
            CopyDirectory(
                Path.Combine(sourceRoot, "payload"),
                Path.Combine(stagedReleaseRoot, "payload"));
            ReleaseIntegrityVerifier.VerifyOrThrow(
                stagedReleaseRoot,
                Application.ExecutablePath,
                SBMSBuild.ProductionSigningInfo.PublisherThumbprint,
                SBMSBuild.ProductionSigningInfo.WhqlCatalogSubjects);
            Log(T("staged release integrity verified"));
        }

        private void CleanupStaging(bool installSucceeded)
        {
            if (installedPayloadCommitted && !installSucceeded)
            {
                if (Directory.Exists(installRoot))
                {
                    Directory.Delete(installRoot, true);
                }
                if (!String.IsNullOrWhiteSpace(installBackupRoot) &&
                    Directory.Exists(installBackupRoot))
                {
                    Directory.Move(installBackupRoot, installRoot);
                }
            }
            else if (!String.IsNullOrWhiteSpace(installBackupRoot) &&
                     Directory.Exists(installBackupRoot))
            {
                BestEffortDeleteDirectory(installBackupRoot, "install backup");
            }
            if (!String.IsNullOrWhiteSpace(stagedReleaseRoot) &&
                Directory.Exists(stagedReleaseRoot))
            {
                BestEffortDeleteDirectory(stagedReleaseRoot, "verified staging");
            }
            stagedReleaseRoot = null;
            installBackupRoot = null;
            installedPayloadCommitted = false;
        }

        private void BestEffortDeleteDirectory(string path, string label)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                // A completed driver-store operation must never be reported as
                // a failed install solely because nonfunctional residue could
                // not be removed. A future maintenance run may remove it.
                Log(label + " cleanup deferred: " + ex.Message);
            }
        }

        private void VerifyRelease()
        {
            if (SBMSBuild.ProductionSigningInfo.IntegrityRequired)
            {
                ReleaseIntegrityVerifier.VerifyOrThrow(
                    sourceRoot,
                    Application.ExecutablePath,
                    SBMSBuild.ProductionSigningInfo.PublisherThumbprint,
                    SBMSBuild.ProductionSigningInfo.WhqlCatalogSubjects);
                Log(T("release integrity verified"));
            }
        }

        private void EnsurePayload()
        {
            RequireFile("SBMS.exe");
            RequireFile("SBMSNative.exe");
            RequireFile("SBMSDeviceHost.exe");
            RequireFile("install-sbms-driver.ps1");
            RequireFile(Path.Combine("driver", "SBMSIndirectDisplay", "SBMSIndirectDisplay.inf"));
            RequireFile(Path.Combine("driver", "SBMSIndirectDisplay", "SBMSIndirectDisplay.dll"));
            RequireFile(Path.Combine("driver", "SBMSIndirectDisplay", "sbmsindirectdisplay.cat"));
            RequireFile(Path.Combine("driver", "SBMSIndirectDisplay", "driver-identity.json"));
            RequireFile(Path.Combine("driver", "SBMSIndirectDisplay", "SBMS.driver-whql.json"));
        }

        private string PayloadRoot
        {
            get
            {
                return SBMSBuild.ProductionSigningInfo.IntegrityRequired
                    ? Path.Combine(stagedReleaseRoot ?? sourceRoot, "payload")
                    : sourceRoot;
            }
        }

        private void EnsureNotRunning()
        {
            string[] names = { "SBMS", "SBMSNative", "SBMSDeviceHost" };
            foreach (string name in names)
            {
                Process[] processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                {
                    throw new InvalidOperationException(T("Please exit running ") + name);
                }
            }
        }

        private void CopyPayload()
        {
            if (SamePath(PayloadRoot, installRoot))
            {
                Log(T("already in Program Files"));
                return;
            }

            if (!SBMSBuild.ProductionSigningInfo.IntegrityRequired)
            {
                if (Directory.Exists(installRoot))
                {
                    Directory.Delete(installRoot, true);
                }
                Directory.CreateDirectory(installRoot);
                CopyDirectory(PayloadRoot, installRoot);
                Log(T("copied files"));
                return;
            }

            string candidate = installRoot + ".new." + Guid.NewGuid().ToString("N");
            installBackupRoot = installRoot + ".backup." + Guid.NewGuid().ToString("N");
            try
            {
                CopyDirectory(PayloadRoot, candidate);
                ReleaseIntegrityVerifier.VerifyPayloadOrThrow(
                    candidate,
                    Path.Combine(stagedReleaseRoot, "SBMS.release.cat"),
                    Application.ExecutablePath,
                    SBMSBuild.ProductionSigningInfo.PublisherThumbprint,
                    SBMSBuild.ProductionSigningInfo.WhqlCatalogSubjects);
                if (Directory.Exists(installRoot))
                {
                    Directory.Move(installRoot, installBackupRoot);
                }
                Directory.Move(candidate, installRoot);
                installedPayloadCommitted = true;
                Log(T("verified payload committed"));
            }
            catch
            {
                if (Directory.Exists(candidate))
                {
                    Directory.Delete(candidate, true);
                }
                if (!Directory.Exists(installRoot) &&
                    Directory.Exists(installBackupRoot))
                {
                    Directory.Move(installBackupRoot, installRoot);
                }
                throw;
            }
        }

        private void InstallDriver()
        {
            string script = Path.Combine(installRoot, "install-sbms-driver.ps1");
            string command = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(script) + " -Force";
            if (SBMSBuild.ProductionSigningInfo.IntegrityRequired)
            {
                command += " -VerifiedByInstaller -VerifiedReleaseRoot " + Quote(stagedReleaseRoot);
            }
            else
            {
                command += " -AllowTestSigned";
            }
            RunProcess("powershell.exe", command, 180000);
            Log(T("driver package staged"));
        }

        private void CreateShortcut()
        {
            string shortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                "SBMS.lnk");
            string target = Path.Combine(installRoot, "SBMS.exe");
            string command =
                "$w=New-Object -ComObject WScript.Shell; " +
                "$s=$w.CreateShortcut('" + EscapePowerShell(shortcut) + "'); " +
                "$s.TargetPath='" + EscapePowerShell(target) + "'; " +
                "$s.WorkingDirectory='" + EscapePowerShell(installRoot) + "'; " +
                "$s.Save()";
            RunProcess("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + Quote(command), 30000);
            Log(T("shortcut applied"));
        }

        private void CreateShortcutBestEffort()
        {
            RunBestEffort("shortcut", CreateShortcut);
        }

        private void CreateStartupTask()
        {
            string target = Path.Combine(installRoot, "SBMS.exe");
            string taskRun = "\"" + target + "\"";
            RunProcess("schtasks.exe", "/Create /TN SBMS /SC ONLOGON /TR " + Quote(taskRun) + " /RL HIGHEST /F", 30000);
            Log(T("startup task applied"));
        }

        private void CreateStartupTaskBestEffort()
        {
            RunBestEffort("startup task", CreateStartupTask);
        }

        private void RunBestEffort(string label, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                try
                {
                    Log(label + " integration deferred: " + ex.Message);
                }
                catch
                {
                    // Best-effort integration and its diagnostic path must not
                    // reverse an already completed core installation.
                }
            }
        }

        private void UninstallFiles()
        {
            EnsureNotRunning();
            RunProcess("schtasks.exe", "/Delete /TN SBMS /F", 30000, true);
            string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "SBMS.lnk");
            if (File.Exists(shortcut))
            {
                File.Delete(shortcut);
                Log(T("removed shortcut"));
            }
            if (Directory.Exists(installRoot) && !SamePath(sourceRoot, installRoot))
            {
                Directory.Delete(installRoot, true);
                Log(T("removed Program Files copy"));
            }
            Log(T("driver package is left installed; remove it manually only when display recovery is confirmed"));
        }

        private void RequireFile(string relativePath)
        {
            string path = Path.Combine(PayloadRoot, relativePath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("missing " + relativePath);
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source))
            {
                string name = Path.GetFileName(directory);
                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                CopyDirectory(directory, Path.Combine(destination, name));
            }
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }
        }

        private void RunProcess(string fileName, string arguments, int timeoutMs, bool allowFailure = false)
        {
            using (var process = new Process())
            {
                Log("exec " + fileName + " " + NormalizeCommand(arguments));
                process.StartInfo.FileName = fileName;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.StandardOutputEncoding = Encoding.Default;
                process.StartInfo.StandardErrorEncoding = Encoding.Default;
                process.Start();
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException(fileName + " timeout");
                }
                string output = (stdout + stderr).Trim();
                WriteRawToolOutput(fileName, arguments, stdout, stderr);
                if (output.Length > 0)
                {
                    LogToolOutput(fileName, output);
                }
                Log(fileName + " exit=" + process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (process.ExitCode != 0 && !allowFailure)
                {
                    throw new InvalidOperationException(fileName + " exit=" + process.ExitCode);
                }
            }
        }

        private void LogToolOutput(string fileName, string output)
        {
            if (ContainsNonAscii(output))
            {
                Log(fileName + " output: localized system text saved to " + setupLogPath + " (" + CountLines(output).ToString(System.Globalization.CultureInfo.InvariantCulture) + " lines)");
                return;
            }
            Log(output);
        }

        private void WriteRawToolOutput(string fileName, string arguments, string stdout, string stderr)
        {
            try
            {
                var builder = new StringBuilder();
                builder.AppendLine("== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " exec " + fileName + " ==");
                builder.AppendLine(NormalizeCommand(arguments));
                if (!string.IsNullOrEmpty(stdout))
                {
                    builder.AppendLine("-- stdout --");
                    builder.AppendLine(stdout.TrimEnd());
                }
                if (!string.IsNullOrEmpty(stderr))
                {
                    builder.AppendLine("-- stderr --");
                    builder.AppendLine(stderr.TrimEnd());
                }
                File.AppendAllText(setupLogPath, builder.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static bool ContainsNonAscii(string text)
        {
            foreach (char ch in text)
            {
                if (ch > 127)
                {
                    return true;
                }
            }
            return false;
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            int count = 1;
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    count++;
                }
            }
            return count;
        }

        private static string NormalizeCommand(string arguments)
        {
            string normalized = arguments.Replace(Environment.NewLine, " ");
            while (normalized.Contains("  "))
            {
                normalized = normalized.Replace("  ", " ");
            }
            return normalized;
        }

        private void Log(string line)
        {
            string formatted = DateTime.Now.ToString("HH:mm:ss.fff") + " " + line;
            logText.AppendText(formatted + Environment.NewLine);
            try
            {
                File.AppendAllText(setupLogPath, formatted + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static string CreateSetupLogPath()
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SBMS",
                "logs");
            Directory.CreateDirectory(logDir);
            return Path.Combine(logDir, "setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd('\\'),
                Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string EscapePowerShell(string value)
        {
            return value.Replace("'", "''");
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            if (!IsAdministrator())
            {
                try
                {
                    var startInfo = new ProcessStartInfo(Application.ExecutablePath);
                    startInfo.UseShellExecute = true;
                    startInfo.Verb = "runas";
                    Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "SBMS Setup needs administrator rights to stage the virtual display driver package in Driver Store.\r\n\r\n" + ex.Message,
                        "SBMS Setup",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }

        internal static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
    }
}
