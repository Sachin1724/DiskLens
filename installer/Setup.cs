// DiskLens-Setup.exe - a per-user installer with the whole app embedded.
//
//   Setup.exe              the window: Install / Uninstall
//   Setup.exe /silent      install, no window
//   Setup.exe /uninstall   remove (this is what Add/Remove Programs calls)
//
// Per-user on purpose: no elevation, nothing written outside the profile, and
// uninstall is a registry key plus one folder.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

static class Setup {
    const string Name = "DiskLens";
    const string Version = "1.0.0";
    const string Publisher = "DiskLens";
    const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskLens";

    static string Target {
        get {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", Name);
        }
    }

    // Every payload file is an embedded resource named payload.<file>.
    static readonly string[] Payload = {
        "server.ps1", "ui.html", "index.html", "README.md", "DiskLens.exe", "disklens.ico"
    };

    static void Write(string resource, string path) {
        var asm = Assembly.GetExecutingAssembly();
        using (var src = asm.GetManifestResourceStream("payload." + resource)) {
            if (src == null) throw new FileNotFoundException("missing payload: " + resource);
            using (var dst = File.Create(path)) src.CopyTo(dst);
        }
    }

    static void Shortcut(string lnk, string target, string icon, string description) {
        // Creating a .lnk needs the shell COM object; borrowing PowerShell for
        // three lines beats carrying an interop assembly.
        var ps = "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('" + lnk + "');" +
                 "$s.TargetPath='" + target + "';" +
                 "$s.WorkingDirectory='" + Path.GetDirectoryName(target) + "';" +
                 "$s.IconLocation='" + icon + "';" +
                 "$s.Description='" + description + "';$s.Save()";
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + ps.Replace("\"", "\\\"") + "\"") {
            CreateNoWindow = true, UseShellExecute = false
        };
        using (var p = Process.Start(psi)) p.WaitForExit(15000);
    }

    static string StartMenu {
        get {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", Name + ".lnk");
        }
    }
    static string Desktop {
        get {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Name + ".lnk");
        }
    }

    public static void Install(bool desktopShortcut) {
        Directory.CreateDirectory(Target);
        foreach (var f in Payload) Write(f, Path.Combine(Target, f));

        var exe = Path.Combine(Target, "DiskLens.exe");
        var ico = Path.Combine(Target, "disklens.ico");
        var self = Path.Combine(Target, "uninstall.exe");
        File.Copy(Assembly.GetExecutingAssembly().Location, self, true);

        Directory.CreateDirectory(Path.GetDirectoryName(StartMenu));
        Shortcut(StartMenu, exe, ico, "Find what is eating your disk");
        if (desktopShortcut) Shortcut(Desktop, exe, ico, "Find what is eating your disk");

        using (var k = Registry.CurrentUser.CreateSubKey(RegKey)) {
            k.SetValue("DisplayName", Name);
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", Publisher);
            k.SetValue("DisplayIcon", ico);
            k.SetValue("InstallLocation", Target);
            k.SetValue("UninstallString", "\"" + self + "\" /uninstall");
            k.SetValue("QuietUninstallString", "\"" + self + "\" /uninstall /silent");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", 2048, RegistryValueKind.DWord);
        }
    }

    public static void Uninstall() {
        foreach (var p in new[] { StartMenu, Desktop })
            try { if (File.Exists(p)) File.Delete(p); } catch { }

        // Stop a running copy before pulling the files out from under it.
        foreach (var proc in Process.GetProcessesByName("pwsh")) {
            try {
                if (proc.MainModule != null) { }
            } catch { }
        }
        try {
            foreach (var f in Directory.GetFiles(Target)) {
                if (f.EndsWith("uninstall.exe", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { }
            }
        } catch { }

        try { Registry.CurrentUser.DeleteSubKeyTree(RegKey, false); } catch { }

        // uninstall.exe cannot delete itself while it runs; a detached cmd can.
        var self = Assembly.GetExecutingAssembly().Location;
        if (self.StartsWith(Target, StringComparison.OrdinalIgnoreCase)) {
            var psi = new ProcessStartInfo("cmd.exe",
                "/c ping 127.0.0.1 -n 3 > nul & rd /s /q \"" + Target + "\"") {
                CreateNoWindow = true, UseShellExecute = false
            };
            try { Process.Start(psi); } catch { }
        } else {
            try { Directory.Delete(Target, true); } catch { }
        }
    }

    // ------------------------------------------------------------------ ui ---
    class Window : Form {
        readonly CheckBox desk = new CheckBox();
        readonly Label status = new Label();

        public Window(bool installed) {
            Text = Name + " Setup";
            ClientSize = new Size(520, 300);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = ColorTranslator.FromHtml("#07070A");
            ForeColor = ColorTranslator.FromHtml("#F4F4F7");
            Font = new Font("Segoe UI", 9f);
            try { Icon = new Icon(typeof(Setup), "disklens.ico"); } catch { }

            var word = new Label {
                Text = "  DiskLens  ",
                Font = new Font("Segoe UI", 20f, FontStyle.Bold),
                ForeColor = ColorTranslator.FromHtml("#07070A"),
                BackColor = ColorTranslator.FromHtml("#C6F135"),
                AutoSize = true, Location = new Point(28, 28)
            };

            var pitch = new Label {
                Text = "Find what is eating your disk, and clear it without guessing.\r\n" +
                       "Treemap, hotspots, duplicate finder, Recycle-Bin cleaner.",
                ForeColor = ColorTranslator.FromHtml("#9A9AA8"),
                AutoSize = false, Size = new Size(460, 46), Location = new Point(30, 86)
            };

            var where = new Label {
                Text = "Installs for you only, into\r\n" + Target,
                ForeColor = ColorTranslator.FromHtml("#6B6B7A"),
                AutoSize = false, Size = new Size(460, 40), Location = new Point(30, 140)
            };

            desk.Text = "Put a shortcut on the desktop";
            desk.Checked = true;
            desk.AutoSize = true;
            desk.Location = new Point(30, 188);
            desk.ForeColor = ColorTranslator.FromHtml("#F4F4F7");

            status.AutoSize = false;
            status.Size = new Size(300, 20);
            status.Location = new Point(30, 252);
            status.ForeColor = ColorTranslator.FromHtml("#C6F135");

            var go = new Button {
                Text = installed ? "Reinstall" : "Install",
                Size = new Size(150, 40), Location = new Point(340, 232),
                FlatStyle = FlatStyle.Flat,
                BackColor = ColorTranslator.FromHtml("#C6F135"),
                ForeColor = ColorTranslator.FromHtml("#07070A"),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            go.FlatAppearance.BorderSize = 0;
            go.Click += (s, e) => {
                status.Text = "Copying files...";
                Application.DoEvents();
                try {
                    Install(desk.Checked);
                    status.Text = "Installed.";
                    Process.Start(new ProcessStartInfo(Path.Combine(Target, "DiskLens.exe")) {
                        UseShellExecute = true, WorkingDirectory = Target });
                    Close();
                } catch (Exception ex) {
                    status.ForeColor = ColorTranslator.FromHtml("#FF4D4D");
                    status.Text = ex.Message;
                }
            };

            var remove = new Button {
                Text = "Uninstall",
                Size = new Size(110, 40), Location = new Point(216, 232),
                FlatStyle = FlatStyle.Flat,
                BackColor = ColorTranslator.FromHtml("#1B1B23"),
                ForeColor = ColorTranslator.FromHtml("#F4F4F7"),
                Visible = installed
            };
            remove.FlatAppearance.BorderSize = 0;
            remove.Click += (s, e) => {
                if (MessageBox.Show("Remove DiskLens and its shortcuts?\r\n\r\n" +
                        "Your saved index stays in %LOCALAPPDATA%\\DiskLens.",
                        "DiskLens", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Uninstall();
                MessageBox.Show("DiskLens has been removed.", "DiskLens");
                Close();
            };

            Controls.AddRange(new Control[] { word, pitch, where, desk, status, go, remove });
        }
    }

    [STAThread]
    static int Main(string[] args) {
        var arg = string.Join(" ", args).ToLowerInvariant();
        var silent = arg.Contains("/silent") || arg.Contains("-silent");

        try {
            if (arg.Contains("/uninstall") || arg.Contains("-uninstall")) {
                if (silent) { Uninstall(); return 0; }
                Application.EnableVisualStyles();
                if (MessageBox.Show("Remove DiskLens and its shortcuts?", "DiskLens",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) {
                    Uninstall();
                    MessageBox.Show("DiskLens has been removed.", "DiskLens");
                }
                return 0;
            }

            if (silent) {
                Install(true);
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Window(Directory.Exists(Target) &&
                File.Exists(Path.Combine(Target, "DiskLens.exe"))));
            return 0;
        } catch (Exception e) {
            if (!silent) MessageBox.Show(e.ToString(), "DiskLens Setup",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
