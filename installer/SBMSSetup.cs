using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SBMSSetup
{
    internal sealed class SetupForm : Form
    {
        private readonly string sourceRoot;
        private readonly string installRoot;
        private readonly TextBox logText = new TextBox();
        private readonly CheckBox driverCheck = new CheckBox();
        private readonly CheckBox shortcutCheck = new CheckBox();
        private readonly CheckBox startupCheck = new CheckBox();
        private readonly Button installButton = new Button();
        private readonly Button uninstallButton = new Button();
        private readonly Button openButton = new Button();

        public SetupForm()
        {
            sourceRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SBMS");

            Text = "SBMS Setup";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(720, 460);
            MinimumSize = new Size(640, 400);
            BackColor = Color.FromArgb(6, 12, 8);
            ForeColor = Color.FromArgb(190, 255, 210);
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

            var title = new Label();
            title.Text = "SBMS";
            title.Dock = DockStyle.Fill;
            title.Font = new Font("Segoe UI", 18F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(190, 255, 210);
            root.Controls.Add(title, 0, 0);

            var options = new FlowLayoutPanel();
            options.Dock = DockStyle.Fill;
            options.FlowDirection = FlowDirection.LeftToRight;
            driverCheck.Text = "安装/更新测试驱动";
            driverCheck.Checked = true;
            shortcutCheck.Text = "开始菜单快捷方式";
            shortcutCheck.Checked = true;
            startupCheck.Text = "开机自启";
            options.Controls.Add(driverCheck);
            options.Controls.Add(shortcutCheck);
            options.Controls.Add(startupCheck);
            root.Controls.Add(options, 0, 1);

            logText.Dock = DockStyle.Fill;
            logText.Multiline = true;
            logText.ReadOnly = true;
            logText.ScrollBars = ScrollBars.Vertical;
            logText.Font = new Font("Consolas", 10F);
            logText.BackColor = Color.FromArgb(10, 22, 14);
            logText.ForeColor = Color.White;
            root.Controls.Add(logText, 0, 2);

            var buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            installButton.Text = "安装";
            uninstallButton.Text = "卸载文件";
            openButton.Text = "打开安装目录";
            installButton.Width = 110;
            uninstallButton.Width = 110;
            openButton.Width = 140;
            buttons.Controls.Add(installButton);
            buttons.Controls.Add(uninstallButton);
            buttons.Controls.Add(openButton);
            root.Controls.Add(buttons, 0, 3);

            StyleButton(installButton);
            StyleButton(uninstallButton);
            StyleButton(openButton);

            installButton.Click += delegate { RunGuarded(Install); };
            uninstallButton.Click += delegate { RunGuarded(UninstallFiles); };
            openButton.Click += delegate
            {
                if (Directory.Exists(installRoot))
                {
                    Process.Start("explorer.exe", installRoot);
                }
            };

            Log("source=" + sourceRoot);
            Log("target=" + installRoot);
        }

        private static void StyleButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(14, 32, 20);
            button.ForeColor = Color.FromArgb(190, 255, 210);
            button.FlatAppearance.BorderColor = Color.FromArgb(190, 255, 210);
        }

        private void RunGuarded(Action action)
        {
            installButton.Enabled = false;
            uninstallButton.Enabled = false;
            try
            {
                action();
                Log("done");
            }
            catch (Exception ex)
            {
                Log("error=" + ex.Message);
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
            EnsureNotRunning();
            CopyPayload();
            if (driverCheck.Checked)
            {
                InstallDriver();
            }
            if (shortcutCheck.Checked)
            {
                CreateShortcut();
            }
            if (startupCheck.Checked)
            {
                CreateStartupTask();
            }
        }

        private void EnsurePayload()
        {
            RequireFile("SBMS.exe");
            RequireFile("SBMSNative.exe");
            RequireFile("SBMSDeviceHost.exe");
            RequireFile(Path.Combine("driver", "IddSampleDriver", "IddSampleDriver.inf"));
            RequireFile(Path.Combine("driver", "IddSampleDriver", "IddSampleDriver.dll"));
            RequireFile(Path.Combine("driver", "IddSampleDriver", "iddsampledriver.cat"));
        }

        private void EnsureNotRunning()
        {
            string[] names = { "SBMS", "SBMSNative", "SBMSDeviceHost" };
            foreach (string name in names)
            {
                Process[] processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                {
                    throw new InvalidOperationException("请先退出正在运行的 " + name);
                }
            }
        }

        private void CopyPayload()
        {
            if (SamePath(sourceRoot, installRoot))
            {
                Log("already in Program Files");
                return;
            }

            if (Directory.Exists(installRoot))
            {
                Directory.Delete(installRoot, true);
            }
            Directory.CreateDirectory(installRoot);
            CopyDirectory(sourceRoot, installRoot);
            Log("copied files");
        }

        private void InstallDriver()
        {
            string inf = Path.Combine(installRoot, "driver", "IddSampleDriver", "IddSampleDriver.inf");
            RunProcess("pnputil.exe", "/add-driver " + Quote(inf) + " /install", 120000);
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
        }

        private void CreateStartupTask()
        {
            string target = Path.Combine(installRoot, "SBMS.exe");
            string taskRun = "\"" + target + "\"";
            RunProcess("schtasks.exe", "/Create /TN SBMS /SC ONLOGON /TR " + Quote(taskRun) + " /RL HIGHEST /F", 30000);
        }

        private void UninstallFiles()
        {
            EnsureNotRunning();
            RunProcess("schtasks.exe", "/Delete /TN SBMS /F", 30000, true);
            string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "SBMS.lnk");
            if (File.Exists(shortcut))
            {
                File.Delete(shortcut);
                Log("removed shortcut");
            }
            if (Directory.Exists(installRoot) && !SamePath(sourceRoot, installRoot))
            {
                Directory.Delete(installRoot, true);
                Log("removed Program Files copy");
            }
            Log("driver package is left installed; remove it manually only when display recovery is confirmed");
        }

        private void RequireFile(string relativePath)
        {
            string path = Path.Combine(sourceRoot, relativePath);
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
                process.StartInfo.FileName = fileName;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                process.Start();
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException(fileName + " timeout");
                }
                string output = (stdout + stderr).Trim();
                if (output.Length > 0)
                {
                    Log(output);
                }
                if (process.ExitCode != 0 && !allowFailure)
                {
                    throw new InvalidOperationException(fileName + " exit=" + process.ExitCode);
                }
            }
        }

        private void Log(string line)
        {
            logText.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }
}
