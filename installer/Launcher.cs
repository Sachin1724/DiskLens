// DiskLens.exe - what the Start-menu shortcut points at.
// Starts the local server with no console window, waits for it to answer, then
// opens the browser. A second launch just reopens the page.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

static class Launcher {
    const int Port = 8731;
    static readonly string Url = "http://127.0.0.1:" + Port + "/";

    static bool Answers(int timeoutMs) {
        try {
            var req = (HttpWebRequest)WebRequest.Create(Url + "api/status");
            req.Timeout = timeoutMs;
            using (var res = (HttpWebResponse)req.GetResponse())
                return res.StatusCode == HttpStatusCode.OK;
        } catch { return false; }
    }

    static string FindPwsh() {
        foreach (var p in new[] {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\PowerShell\7\pwsh.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\PowerShell\7\pwsh.exe"),
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe")
        }) if (File.Exists(p)) return p;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';')) {
            try {
                var candidate = Path.Combine(dir.Trim(), "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            } catch { }
        }
        return null;
    }

    [STAThread]
    static int Main(string[] args) {
        var here = Path.GetDirectoryName(Application.ExecutablePath);
        var server = Path.Combine(here, "server.ps1");

        if (Answers(1200)) { Open(); return 0; }   // already running

        var pwsh = FindPwsh();
        if (pwsh == null) {
            MessageBox.Show(
                "DiskLens needs PowerShell 7.\r\n\r\nInstall it with:\r\n\r\n" +
                "    winget install --id Microsoft.PowerShell\r\n\r\n" +
                "then start DiskLens again.",
                "DiskLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }

        var psi = new ProcessStartInfo(pwsh,
            "-NoProfile -ExecutionPolicy Bypass -File \"" + server + "\" -NoBrowser -Port " + Port) {
            WorkingDirectory = here,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        try { Process.Start(psi); } catch (Exception e) {
            MessageBox.Show("Could not start the DiskLens server.\r\n\r\n" + e.Message,
                "DiskLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }

        // The first scan takes a while, but the page is served immediately.
        for (var i = 0; i < 40 && !Answers(500); i++) Thread.Sleep(250);
        Open();
        return 0;
    }

    static void Open() {
        try { Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true }); } catch { }
    }
}
