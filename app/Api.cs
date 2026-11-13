// Rules, safety and the request routes the page talks to. This is the old
// server.ps1 minus the HTTP: the page sends {id, path, body}, Handle() answers
// with the same JSON the /api/* routes used to return.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Net.Http;
using System.Reflection;

namespace DiskLens {

public static class Rules {
  static readonly bool Win = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

  // First match wins, so the cache rules sit above the system rule that would
  // otherwise protect C:\Windows\Temp. Paths are matched with "/" as separator
  // on every OS so one rule set reads the same on Windows and macOS.
  static readonly (string V, string N, Regex R)[] Verdicts = {
    ("CACHE",  "Build or package cache - rebuilds itself",
      Rx(@"/(\.gradle/caches|npm-cache|\.npm|uv/cache|pip/Cache|\.cargo/registry/cache|\.nuget/packages|\.m2/repository|__pycache__|node_modules|\.next|\.nuxt|\.parcel-cache|\.pytest_cache|\.mypy_cache|\.venv|target/debug|target/release|DerivedData|Homebrew)$")),
    ("CACHE",  "Temporary files - safe to empty",
      Rx(@"/(Temp|tmp|CrashDumps|Crashpad|Download|Prefetch|Logs|DiagnosticReports)$")),
    ("CACHE",  "App cache - the app refills it on demand",
      Rx(@"/([Cc]aches?|Code Cache|GPUCache|ShaderCache|DawnCache|WebCache|INetCache|GrShaderCache)$")),
    ("MODELS", "Model weights - re-downloading is slow",
      Rx(@"/(huggingface|\.ollama|\.lmstudio|whisper|models)(/|$)")),
    ("HEAVY",  "Container or VM disks - prune from their own tool, do not delete",
      Rx(@"/(Docker|wsl|VirtualBox VMs|Hyper-V|\.vagrant|Parallels|\.docker|CoreSimulator/Devices|Virtual Machines\.localized)(/|$)")),
    ("BACKUP", "A backup or an old install - safe once a newer one exists",
      Rx(@"/(Windows\.old|\$WINDOWS\.~BT|\$WINDOWS\.~WS|MobileSync/Backup/[^/]+|Xcode/Archives|iOS DeviceSupport|Time Machine Backups|Backups\.backupdb)(/|$)")),
    ("DATA",   "Your own files",
      Rx(@"/(Documents|Pictures|Videos|Movies|Music|Desktop|Downloads|OneDrive[^/]*|iCloud Drive[^/]*|Mobile Documents)$")),
    ("SYSTEM", "Windows needs this",
      Rx(@"^[A-Za-z]:/(Windows|Program Files|Program Files \(x86\)|ProgramData|\$Recycle\.Bin|System Volume Information|Recovery)")),
    ("SYSTEM", "macOS needs this",
      Rx(@"^/(System|Library|Applications|usr|bin|sbin|etc|var|private|opt|cores|Volumes|dev)(/|$)|^/Users/[^/]+/Library/(Application Support|Containers|Group Containers|Preferences|Keychains|Mail|Messages|Mobile Documents)")),
  };

  // Never removable, whatever the UI asks for.
  static readonly Regex[] Deny = {
    Rx(@"^([A-Za-z]:)?/?$"),                       // a drive root or /
    Rx(@"^[A-Za-z]:/[^/]+/?$"),                    // first-level folder on Windows
    Rx(@"^/[^/]+/?$"),                             // first-level folder on macOS
    Rx(@"^[A-Za-z]:/Users/[^/]+/?$"),
    Rx(@"^/Users/[^/]+/?$"),
    Rx(@"/(Documents|Pictures|Videos|Movies|Music|Desktop|Downloads|Favorites|Links|Searches|Contacts|OneDrive[^/]*|Public|Mobile Documents)/?$"),
    Rx(@"^[A-Za-z]:/Windows/(System32|SysWOW64|WinSxS|Installer|Fonts|Boot|assembly|servicing)"),
    Rx(@"^[A-Za-z]:/Program Files"),
    Rx(@"^[A-Za-z]:/ProgramData"),
    Rx(@"^[A-Za-z]:/Windows"),
    Rx(@"^[A-Za-z]:/(\$Recycle\.Bin|System Volume Information|Recovery|PerfLogs)"),
    Rx(@"/AppData/(Local|Roaming|LocalLow)/?$"),
    Rx(@"/AppData/?$"),
    Rx(@"^/Users/[^/]+/Library/?$"),
    Rx(@"^/Users/[^/]+/Library/[^/]+/?$"),         // ~/Library/Caches itself stays; its children may go
    Rx(@"^/(System|Library|usr|bin|sbin|etc|var|private|opt|Applications)(/|$)"),
  };

  // The only places under a system root that are genuinely disposable.
  static readonly Regex[] AllowInSystem = {
    Rx(@"^[A-Za-z]:/Windows/(Temp|Prefetch|SoftwareDistribution/Download)(/|$)"),
    Rx(@"^/(private/)?(tmp|var/folders|var/tmp|var/log)/[^/]+"),
    Rx(@"^/Library/(Caches|Logs)/[^/]+"),
    Rx(@"^/Users/[^/]+/Library/(Caches|Logs|Developer/Xcode/DerivedData|Developer/Xcode/Archives|Developer/Xcode/iOS DeviceSupport|Developer/CoreSimulator/Caches)/[^/]+"),
  };

  static Regex Rx(string p) => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);

  // Normalised view of a path for the rule engine: forward slashes, no trailing slash.
  public static string Norm(string path) => (path ?? "").Replace('\\', '/').TrimEnd('/');

  public static (string v, string note) Verdict(string path) {
    var p = Norm(path);
    foreach (var r in Verdicts) if (r.R.IsMatch(p)) return (r.V, r.N);
    return ("UNKNOWN", "Not recognised - look inside before removing");
  }

  public static bool Removable(string path) {
    if (string.IsNullOrWhiteSpace(path)) return false;
    var p = Norm(path);
    if (p.Length < 4) return false;
    foreach (var a in AllowInSystem) if (a.IsMatch(p)) return Exists(path);
    foreach (var d in Deny) if (d.IsMatch(p)) return false;
    // Whatever the OS or an installed program owns stays put, however deep it sits.
    if (Verdict(p).v == "SYSTEM") return false;
    return Exists(path);
  }

  static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

  // ------------------------------------------------------------ platform ---
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct SHFILEOPSTRUCT {
    public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
    [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle;
  }
  [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

  // Recycle Bin on Windows, Finder Trash on macOS. Never a hard delete.
  public static void Trash(string path) {
    if (Win) {
      var op = new SHFILEOPSTRUCT {
        wFunc = 3 /* FO_DELETE */, pFrom = path + "\0\0",
        fFlags = 0x0040 /* ALLOWUNDO */ | 0x0010 /* NOCONFIRMATION */ | 0x0004 /* SILENT */ | 0x0400 /* NOERRORUI */
      };
      int rc = SHFileOperation(ref op);
      if (rc != 0 || op.fAnyOperationsAborted || Exists(path)) throw new IOException("recycle failed: " + rc);
    } else {
      var q = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
      var psi = new ProcessStartInfo("osascript") { RedirectStandardError = true, UseShellExecute = false };
      psi.ArgumentList.Add("-e");
      psi.ArgumentList.Add($"tell application \"Finder\" to delete POSIX file \"{q}\"");
      var p = Process.Start(psi);
      p.WaitForExit();
      if (p.ExitCode != 0 || Exists(path)) throw new IOException("trash failed: " + p.StandardError.ReadToEnd());
    }
  }

  // A folder opens; a file is only highlighted. Never hand a file to the shell
  // to launch with whatever is registered for its extension.
  public static void Reveal(string path) {
    bool dir = Directory.Exists(path), file = File.Exists(path);
    if (!dir && !file) return;
    if (Win) Process.Start(new ProcessStartInfo("explorer.exe", dir ? $"\"{path}\"" : $"/select,\"{path}\"") { UseShellExecute = false });
    else {
      var psi = new ProcessStartInfo("open") { UseShellExecute = false };
      if (!dir) psi.ArgumentList.Add("-R");
      psi.ArgumentList.Add(path);
      Process.Start(psi);
    }
  }

  // Fixed volumes with free/used bytes. Windows lists drive letters; macOS lists
  // "/" plus whatever is mounted under /Volumes (external disks).
  public static List<object> Volumes() {
    var rows = new List<object>();
    long rootSize = 0;
    try { rootSize = new DriveInfo("/").TotalSize; } catch {}
    foreach (var d in DriveInfo.GetDrives()) {
      try {
        if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)) continue;
        var name = d.Name;
        if (Win) name = name.TrimEnd('\\');
        else if (name != "/" && !name.StartsWith("/Volumes/")) continue;
        else if (name.StartsWith("/Volumes/") && d.TotalSize == rootSize) continue;   // same APFS container as /
        rows.Add(new { name, free = d.AvailableFreeSpace, used = d.TotalSize - d.AvailableFreeSpace });
      } catch {}
    }
    return rows;
  }

  public static (long free, long used) Volume(string root) {
    try { var d = new DriveInfo(root); return (d.AvailableFreeSpace, d.TotalSize - d.AvailableFreeSpace); }
    catch { return (0, 0); }
  }

  public static string DefaultDrive => Win ? (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") : "/";
}

// ------------------------------------------------------------------ routes ---
public class Api {
  public string Drive = Rules.DefaultDrive;
  public static readonly bool Win = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
  static readonly JsonSerializerOptions J = new JsonSerializerOptions { PropertyNamingPolicy = null };

  public void Boot(string drive) {
    if (!string.IsNullOrWhiteSpace(drive)) Drive = drive;
    // A saved index opens instantly; the drive is only walked when there is none.
    if (!DiskScan.Load(Drive)) DiskScan.Start(Drive);
  }

  public string Handle(string route, Dictionary<string, string> q, string body) {
    try { return JsonSerializer.Serialize(Route(route, q, body), J); }
    catch (Exception e) { return JsonSerializer.Serialize(new { error = e.Message }, J); }
  }

  static string Q(Dictionary<string, string> q, string k, string dflt = null) =>
    q != null && q.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : dflt;
  static string Leaf(string p) => Path.GetFileName(p.TrimEnd('\\', '/'));

  object Route(string route, Dictionary<string, string> q, string body) {
    var path = Q(q, "path");
    switch (route) {

      case "/api/status": {
        var (free, used) = Rules.Volume(Drive);
        return new {
          running = DiskScan.Running, done = DiskScan.Done,
          dirs = DiskScan.Running ? DiskScan.DirCount : DiskScan.Own.Count,
          files = DiskScan.FileCount, current = DiskScan.Current, root = DiskScan.Root,
          free, used, drive = Drive, sep = Path.DirectorySeparatorChar.ToString(),
          os = Win ? "windows" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "mac" : "linux",
          hasIndex = DiskScan.HasIndex,
          indexedAt = DiskScan.IndexedAtUtc != 0 ? DateTime.FromBinary(DiskScan.IndexedAtUtc).ToString("o") : null
        };
      }

      case "/api/drives": return new { rows = Rules.Volumes() };

      case "/api/rescan": {
        var d = Q(q, "drive", Drive);
        bool switching = d != Drive;
        // Re-asking for the drive already being walked changes nothing; switching always wins.
        if (switching || !DiskScan.Running) {
          Drive = d;
          // Switching drives uses that drive's saved index if it has one.
          if (!(switching && DiskScan.Load(d))) DiskScan.Start(d);
        }
        return new { ok = true, drive = Drive };
      }

      case "/api/dupes/start": {
        long min = long.Parse(Q(q, "min", "10")) << 20;
        var scope = path ?? Drive;
        DupeScan.Start(scope, min);
        return new { ok = true, scope };
      }

      case "/api/dupes": {
        // A scan from a previous session is still on disk - hand it back.
        if (!DupeScan.Running && !DupeScan.Done) DupeScan.Load(Drive);
        var rows = DupeScan.Groups().Select(g => {
          var f = g.Split('|');
          long size = long.Parse(f[0]);
          var paths = f.Skip(1).ToArray();
          return new { size, copies = paths.Length, wasted = size * (paths.Length - 1), name = Leaf(paths[0]), paths };
        }).ToArray();
        return new {
          running = DupeScan.Running, done = DupeScan.Done, scope = DupeScan.Scope, current = DupeScan.Current,
          seen = DupeScan.Seen, hashed = DupeScan.Hashed, wasted = DupeScan.Wasted, rows
        };
      }

      case "/api/refresh": {
        long size = DiskScan.RefreshSubtree(path);
        return new { path, size, gone = size < 0 };
      }

      case "/api/children": {
        var rows = DiskScan.Children(path).Select(r => {
          var f = r.Split('|', 3);
          bool isDir = f[1] == "d";
          var v = isDir ? Rules.Verdict(f[2]) : ("FILES", "Loose files sitting in this folder");
          return new {
            size = long.Parse(f[0]), dir = isDir, path = f[2], name = isDir ? Leaf(f[2]) : "files here",
            verdict = v.Item1, note = v.Item2, removable = isDir && Rules.Removable(f[2]), fresh = DiskScan.FreshOf(f[2]),
            projectFresh = v.Item1 == "CACHE" ? DiskScan.ProjectFreshOf(f[2]) : null
          };
        }).OrderByDescending(r => r.size).ToArray();
        return new { path, rows };
      }

      case "/api/hotspots": {
        long min = long.Parse(Q(q, "min", "500")) << 20;
        var rows = DiskScan.Hotspots(min, 120).Select(r => {
          var f = r.Split('|', 2);
          var v = Rules.Verdict(f[1]);
          return new { size = long.Parse(f[0]), path = f[1], name = Leaf(f[1]), verdict = v.v, note = v.note,
                       removable = Rules.Removable(f[1]), fresh = DiskScan.FreshOf(f[1]),
                       projectFresh = v.v == "CACHE" ? DiskScan.ProjectFreshOf(f[1]) : null };
        }).ToArray();
        return new { rows };
      }

      case "/api/preview": {
        var files = DiskScan.TopFiles(path, 15, 2500).Select(r => {
          var f = r.Split('|', 3);
          return new { size = long.Parse(f[0]), date = f[1], path = f[2], name = Leaf(f[2]) };
        }).ToArray();
        var v = Rules.Verdict(path);
        return new { path, size = DiskScan.SizeOf(path), verdict = v.v, note = v.note, removable = Rules.Removable(path),
                     files, fresh = DiskScan.FreshOf(path), truncated = DiskScan.LastPreviewTruncated };
      }

      case "/api/reveal": Rules.Reveal(path); return new { ok = true };

      case "/api/recycle": {
        var req = JsonSerializer.Deserialize<RecycleBody>(body ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        long freed = 0; var removed = new List<string>(); var refused = new List<string>();
        foreach (var t in req?.paths ?? new string[0]) {
          if (!Rules.Removable(t)) { refused.Add(t); continue; }
          bool isFile = File.Exists(t);
          long sz = isFile ? new FileInfo(t).Length : DiskScan.SizeOf(t);
          try {
            Rules.Trash(t);
            if (isFile) { DiskScan.ForgetFile(t, sz); BigScan.Drop(t); } else DiskScan.Forget(t);   // patch the index, never rescan
            if (sz > 0) freed += sz;
            removed.Add(t);
            Log(sz, t);
          } catch { refused.Add(t); }
        }
        if (removed.Count > 0) DiskScan.Save();
        return new { freed, removed, refused };
      }

      case "/api/files/start": {
        long min = long.Parse(Q(q, "min", "200")) << 20;
        BigScan.Start(Drive, min);
        return new { ok = true };
      }

      case "/api/files": {
        if (!BigScan.Running && !BigScan.Done) BigScan.Load(Drive);
        var rows = BigScan.Rows().Select(r => {
          var f = r.Split('|', 3);
          var v = Rules.Verdict(Path.GetDirectoryName(f[2]) ?? f[2]);
          // A file inherits its folder's verdict for protection; what it *is* comes from its name.
          string kind = FileKind(f[2]);
          return new { size = long.Parse(f[0]), fresh = f[1] == "" ? null : f[1], path = f[2], name = Leaf(f[2]), dir = false,
                       verdict = v.v == "SYSTEM" ? "SYSTEM" : "FILES", kind, note = kind + " - " + v.note,
                       removable = v.v != "SYSTEM" && Rules.Removable(f[2]) };
        }).ToArray();
        return new { running = BigScan.Running, done = BigScan.Done, seen = BigScan.Seen, current = BigScan.Current, rows };
      }

      case "/api/apps": return new { rows = Apps() };

      case "/api/apps/uninstall": {
        var id = Q(q, "id");
        if (Win) {
          // The vendor's own uninstaller, with its own UI. We never touch Program Files ourselves.
          Process.Start(new ProcessStartInfo("cmd.exe", "/c " + id) { UseShellExecute = true });
        } else {
          if (!Regex.IsMatch(Rules.Norm(id), @"^(/Users/[^/]+)?/Applications/[^/]+\.app$")) throw new Exception("not an application bundle");
          long sz = DiskScan.SizeOf(id);
          Rules.Trash(id);
          DiskScan.Forget(id); DiskScan.Save(); Log(sz, id);
        }
        return new { ok = true };
      }

      case "/api/update": return CheckUpdate().GetAwaiter().GetResult();

      case "/api/update/install": {
        var u = Q(q, "url");
        // Only our own release asset, over https, ever gets downloaded and run.
        if (!Win || u == null || !Regex.IsMatch(u, @"^https://github\.com/Sachin1724/DiskLens/releases/download/[^/]+/DiskLens-Setup\.exe$"))
          throw new Exception("not an update this app can install");
        var dst = Path.Combine(Path.GetTempPath(), "DiskLens-Setup-update.exe");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) }) {
          http.DefaultRequestHeaders.UserAgent.ParseAdd("DiskLens");
          File.WriteAllBytes(dst, http.GetByteArrayAsync(u).GetAwaiter().GetResult());
        }
        Process.Start(new ProcessStartInfo(dst, "/silent /relaunch") { UseShellExecute = true });
        // The installer waits for us to exit, replaces the files and starts the new version.
        System.Threading.Tasks.Task.Run(() => { System.Threading.Thread.Sleep(600); Environment.Exit(0); });
        return new { ok = true };
      }

      case "/api/system": return new { rows = SystemRows() };

      case "/api/system/empty-bin": {
        if (Win) { int rc = SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); if (rc != 0) throw new Exception("rc " + rc); }
        else Process.Start(new ProcessStartInfo("osascript", "-e \"tell application \\\"Finder\\\" to empty trash\"") { UseShellExecute = false })?.WaitForExit();
        return new { ok = true };
      }

      case "/api/log": {
        var rows = new List<object>(); long total = 0;
        try {
          foreach (var line in File.ReadAllLines(LogPath()).Reverse().Take(300)) {
            var f = line.Split('|', 3);
            if (f.Length < 3) continue;
            long sz = long.Parse(f[1]); total += sz;
            rows.Add(new { when = f[0], size = sz, path = f[2] });
          }
        } catch {}
        return new { rows, total };
      }

      default: return new { error = "no such route: " + route };
    }
  }
  class RecycleBody { public string[] paths { get; set; } }

  public static readonly string Current =
    (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

  // Publishing a GitHub release tagged vX.Y.Z with DiskLens-Setup.exe attached
  // is the whole update procedure. One quiet request per launch; offline is fine.
  static async System.Threading.Tasks.Task<object> CheckUpdate() {
    try {
      using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
      http.DefaultRequestHeaders.UserAgent.ParseAdd("DiskLens");
      var json = await http.GetStringAsync("https://api.github.com/repos/Sachin1724/DiskLens/releases/latest");
      using var doc = JsonDocument.Parse(json);
      var tag = (doc.RootElement.GetProperty("tag_name").GetString() ?? "").TrimStart('v');
      string url = null;
      foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        if (a.GetProperty("name").GetString() == "DiskLens-Setup.exe") url = a.GetProperty("browser_download_url").GetString();
      var notes = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() : "";
      return new { current = Current, latest = tag, newer = IsNewer(tag, Current), url, notes, page = doc.RootElement.GetProperty("html_url").GetString() };
    } catch (Exception e) { return new { current = Current, latest = (string)null, newer = false, error = e.Message }; }
  }

  public static bool IsNewer(string a, string b) =>
    Version.TryParse(a, out var x) && Version.TryParse(b, out var y) && x > y;

  [DllImport("shell32.dll")] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

  static string LogPath() => Path.Combine(Path.GetDirectoryName(DiskScan.IndexPath("x")), "removed.log");
  static void Log(long size, string path) {
    try { File.AppendAllText(LogPath(), DateTime.UtcNow.ToString("o") + "|" + size + "|" + path + "\n"); } catch {}
  }

  static readonly Regex Installer = new Regex(@"\.(exe|msi|msix|appx|dmg|pkg|iso)$", RegexOptions.IgnoreCase);
  static readonly Regex Media = new Regex(@"\.(mp4|mkv|mov|avi|webm|m4v|wmv|mp3|flac|wav)$", RegexOptions.IgnoreCase);
  static readonly Regex Archive = new Regex(@"\.(zip|7z|rar|tar|gz|tgz|bz2|xz)$", RegexOptions.IgnoreCase);
  static readonly Regex Disk = new Regex(@"\.(vhd|vhdx|vmdk|vdi|qcow2|ova|img)$", RegexOptions.IgnoreCase);
  static string FileKind(string path) {
    var n = Path.GetFileName(path);
    if (Installer.IsMatch(n)) return "installer";
    if (Media.IsMatch(n)) return "media";
    if (Archive.IsMatch(n)) return "archive";
    if (Disk.IsMatch(n)) return "disk image";
    return "file";
  }

  // Installed applications: name, size, when installed, when its folder was
  // last written (updates, not usage - the only honest signal available
  // without an elevated process), and how to remove it the proper way.
  static List<object> Apps() {
    var rows = new List<object>();
    if (Win) {
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var (hive, sub) in new[] {
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall") }) {
        using var root = hive.OpenSubKey(sub);
        if (root == null) continue;
        foreach (var name in root.GetSubKeyNames()) {
          try {
            using var k = root.OpenSubKey(name);
            var title = k?.GetValue("DisplayName") as string;
            if (string.IsNullOrWhiteSpace(title) || !seen.Add(title)) continue;
            if ((k.GetValue("SystemComponent") as int?) == 1) continue;
            var uninstall = k.GetValue("QuietUninstallString") as string ?? k.GetValue("UninstallString") as string;
            if (string.IsNullOrWhiteSpace(uninstall)) continue;
            var loc = (k.GetValue("InstallLocation") as string ?? "").Trim('"', ' ');
            long size = (k.GetValue("EstimatedSize") as int? ?? 0) * 1024L;
            if (loc.Length > 3) { long idx = DiskScan.SizeOf(loc); if (idx > size) size = idx; }
            var inst = k.GetValue("InstallDate") as string;
            string installed = inst == null ? null
              : Regex.IsMatch(inst, @"^\d{8}$") ? inst.Substring(0, 4) + "-" + inst.Substring(4, 2) + "-" + inst.Substring(6, 2)
              : DateTime.TryParse(inst, out var dt) ? dt.ToString("yyyy-MM-dd") : null;
            rows.Add(new { name = title, publisher = k.GetValue("Publisher") as string ?? "", size, installed,
                           fresh = loc.Length > 3 ? DiskScan.FreshOf(loc) : null, path = loc, id = uninstall });
          } catch {}
        }
      }
    } else {
      var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
      foreach (var dir in new[] { "/Applications", Path.Combine(home, "Applications") }) {
        if (!Directory.Exists(dir)) continue;
        foreach (var app in Directory.GetDirectories(dir, "*.app")) {
          try {
            string used = null;
            try {
              var psi = new ProcessStartInfo("mdls") { RedirectStandardOutput = true, UseShellExecute = false };
              psi.ArgumentList.Add("-name"); psi.ArgumentList.Add("kMDItemLastUsedDate"); psi.ArgumentList.Add("-raw"); psi.ArgumentList.Add(app);
              var pr = Process.Start(psi); var o = pr.StandardOutput.ReadToEnd().Trim(); pr.WaitForExit();
              if (o.Length >= 10 && o != "(null)") used = o.Substring(0, 10);
            } catch {}
            rows.Add(new { name = Path.GetFileNameWithoutExtension(app), publisher = "", size = Math.Max(0, DiskScan.SizeOf(app)),
                           installed = Directory.GetCreationTimeUtc(app).ToString("yyyy-MM-dd"),
                           fresh = used ?? DiskScan.FreshOf(app), path = app, id = app });
          } catch {}
        }
      }
    }
    return rows.OrderByDescending(r => (long)r.GetType().GetProperty("size").GetValue(r)).ToList();
  }

  // Things the app must not delete itself but that eat space all the same:
  // size, what it is, and the one correct command to shrink it.
  static List<object> SystemRows() {
    var rows = new List<object>();
    long SizeOf(string p) { long s = DiskScan.SizeOf(p); if (s >= 0) return s; try { return new FileInfo(p).Length; } catch { return -1; } }
    void Add(string name, string path, string hint, string cmd, string action = null) {
      long size = SizeOf(path);
      if (size <= 0 && action == null) return;
      rows.Add(new { name, path, size = Math.Max(0, size), hint, cmd, action });
    }
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (Win) {
      var sys = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
      Add("Hibernation file", sys + @"\hiberfil.sys", "Lets Windows resume from hibernate. Off if you never hibernate.", "powercfg /hibernate off");
      Add("Page file", sys + @"\pagefile.sys", "Virtual memory. Windows manages it; shrinking it is rarely worth it.", null);
      Add("Previous Windows install", sys + @"\Windows.old", "Kept 10 days after an upgrade so you can roll back.", "cleanmgr  (tick: Previous Windows installation(s))");
      Add("Upgrade leftovers", sys + @"\$WINDOWS.~BT", "Setup files from a feature update.", "cleanmgr  (tick: Temporary Windows installation files)");
      Add("Component store (WinSxS)", sys + @"\Windows\WinSxS", "Every version of every system file. Only DISM may trim it.", "Dism /Online /Cleanup-Image /StartComponentCleanup");
      Add("Windows Update cache", sys + @"\Windows\SoftwareDistribution\Download", "Downloaded updates already installed.", "net stop wuauserv && rd /s /q %SystemRoot%\\SoftwareDistribution\\Download && net start wuauserv");
      Add("Recycle Bin", sys + @"\$Recycle.Bin", "Everything DiskLens and you removed is still here until emptied.", null, "empty-bin");
      Add("Docker Desktop disk", Path.Combine(local, @"Docker\wsl"), "Images, containers and volumes live in one virtual disk that only grows.", "docker system prune -a --volumes");
      foreach (var vhd in SafeGlob(Path.Combine(local, "Packages"), "ext4.vhdx"))
        Add("WSL distro disk", vhd, "A Linux distro's virtual disk. Delete files inside, then compact.", "wsl --shutdown  then  diskpart: select vdisk file=\"...\"  compact vdisk");
      Add("Delivery Optimization cache", Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? sys + @"\Windows", @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), "Update chunks shared with other PCs.", "Delete-DeliveryOptimizationCache -Force");
    } else {
      Add("Trash", Path.Combine(home, ".Trash"), "Everything DiskLens and you removed is still here until emptied.", null, "empty-bin");
      Add("Time Machine local snapshots", "/", "APFS snapshots macOS keeps when the backup disk is away. Invisible to the file walk.", "tmutil thinlocalsnapshots / 999999999999 4");
      Add("Homebrew downloads", Path.Combine(home, "Library/Caches/Homebrew"), "Old bottles and tarballs.", "brew cleanup -s --prune=all");
      Add("Xcode simulators", Path.Combine(home, "Library/Developer/CoreSimulator/Devices"), "Every simulator runtime and its data.", "xcrun simctl delete unavailable");
      Add("Xcode device support", Path.Combine(home, "Library/Developer/Xcode/iOS DeviceSupport"), "Symbols for every iOS version a device ever ran. Keep the current one.", null);
      Add("Docker Desktop disk", Path.Combine(home, "Library/Containers/com.docker.docker/Data/vms/0/data/Docker.raw"), "Images, containers and volumes in one virtual disk.", "docker system prune -a --volumes");
      Add("Mail downloads", Path.Combine(home, "Library/Mail"), "Attachments cached by Mail.app.", "Mail > Settings > Accounts > Download Attachments: Recent");
    }
    return rows;
  }

  static IEnumerable<string> SafeGlob(string root, string name) {
    try { return Directory.EnumerateFiles(root, name, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 }); }
    catch { return new string[0]; }
  }

  // ----------------------------------------------------------- self-test ---
  // The verdicts, the deny list, the walk and the rollup, pinned the way the
  // PowerShell version pinned them. `DiskLens --selftest`.
  public static void SelfTest() {
    void Must(bool ok, string what) { if (!ok) throw new Exception("self-test: " + what); }
    Must(Rules.Verdict(@"C:\Users\x\.gradle\caches").v == "CACHE", "verdict: gradle caches");
    Must(Rules.Verdict(@"C:\Windows\Temp").v == "CACHE", "verdict: cache must beat system");
    Must(Rules.Verdict(@"C:\Windows\System32").v == "SYSTEM", "verdict: system");
    Must(Rules.Verdict(@"C:\Users\x\.cache\huggingface").v == "MODELS", "verdict: models");
    Must(Rules.Verdict(@"C:\Users\x\Videos").v == "DATA", "verdict: data");
    Must(Rules.Verdict(@"C:\Users\x\projects\thing").v == "UNKNOWN", "verdict: fallback");
    Must(Rules.Verdict(@"C:\Users\x\AppData\Local\Docker\wsl\disk").v == "HEAVY", "verdict: vm disk inside Docker");
    Must(Rules.Verdict("/Users/x/Library/Caches").v == "CACHE", "mac verdict: Library/Caches");
    Must(Rules.Verdict("/Users/x/Library/Developer/Xcode/DerivedData").v == "CACHE", "mac verdict: DerivedData");
    Must(Rules.Verdict("/System/Library").v == "SYSTEM", "mac verdict: /System");
    Must(Rules.Verdict("/Users/x/Movies").v == "DATA", "mac verdict: Movies");
    Must(Rules.Verdict("/Users/x/Library/Application Support/Foo").v == "SYSTEM", "mac verdict: Application Support");
    Must(Rules.Verdict("/Users/x/code/app/node_modules").v == "CACHE", "mac verdict: node_modules");
    Must(Rules.Verdict(@"C:\Users\x\AppData\Roaming\Apple Computer\MobileSync\Backup\abc").v == "BACKUP", "verdict: iPhone backup");
    Must(Rules.Verdict(@"C:\Windows.old").v == "BACKUP", "verdict: Windows.old is a backup, above the system rule");
    Must(Rules.Removable(@"C:\Users\x\AppData\Roaming\Apple Computer\MobileSync\Backup\abc") == Directory.Exists(@"C:\Users\x\AppData\Roaming\Apple Computer\MobileSync\Backup\abc"), "removable: a phone backup is the user's to drop");
    Must(IsNewer("2.2.0", "2.1.0") && !IsNewer("2.1.0", "2.1.0") && !IsNewer("2.0.9", "2.1.0") && !IsNewer("junk", "2.1.0"), "update: version compare");
    Must(FileKind("setup.exe") == "installer" && FileKind("a.MKV") == "media" && FileKind("x.txt") == "file", "file kind");
    foreach (var p in new[] { @"C:\", @"C:\Users", @"C:\Windows", @"C:\ProgramData", @"C:\Users\sachi",
                              @"C:\Windows\System32\drivers", @"C:\Program Files\x", @"C:\Users\sachi\AppData\Local",
                              @"C:\Users\sachi\Videos", @"C:\Users\sachi\Downloads", @"C:\ProgramData\SomeApp\Extras",
                              @"C:\ProgramData\NVIDIA\NGX\models\x", @"C:\Windows\WinSxS",
                              "/", "/Users", "/Users/x", "/System", "/Library/Caches", "/Applications/Foo.app",
                              "/Users/x/Library", "/Users/x/Library/Caches", "/Users/x/Documents", "/private/var" })
      Must(!Rules.Removable(p), "deny list let through: " + p);
    if (Win) {
      Must(Rules.Removable(@"C:\Windows\Temp"), "removable: Windows\\Temp is the documented exception");
      Must(Rules.Removable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")),
           "removable: a cache under AppData should pass");
    }

    var tmp = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid());
    var sub = Path.Combine(tmp, "sub");
    Directory.CreateDirectory(sub);
    File.WriteAllBytes(Path.Combine(sub, "a.bin"), new byte[4096]);
    File.WriteAllBytes(Path.Combine(tmp, "b.bin"), new byte[2048]);
    var S = Path.DirectorySeparatorChar.ToString();
    DiskScan.Walk(tmp);
    Must(DiskScan.SizeOf(tmp) == 6144, "rollup: parent total wrong");
    Must(DiskScan.SizeOf(sub) == 4096, "rollup: child total wrong");
    Must(DiskScan.SizeOf(sub + S) == 4096, "rollup: trailing separator");
    Must(DiskScan.Children(tmp + S).Length == 2, "Children: trailing separator");
    Must(DiskScan.FileCount == 2, "walk: file count");
    Must(DiskScan.TopFiles(tmp, 5, 2500).Length == 2, "TopFiles: count");
    Must(DiskScan.Forget(sub) == 4096, "Forget: returned size");
    Must(DiskScan.SizeOf(tmp) == 2048, "Forget: parent total not settled");
    File.WriteAllBytes(Path.Combine(sub, "c.bin"), new byte[1024]);
    Must(DiskScan.RefreshSubtree(sub) == 5120, "RefreshSubtree: subtree total");
    Must(DiskScan.SizeOf(tmp) == 7168, "RefreshSubtree: ancestors not updated");
    DiskScan.Save();
    DiskScan.Forget(sub);
    Must(DiskScan.Load(tmp), "Load: snapshot not restored");
    Must(DiskScan.SizeOf(sub) == 5120, "Load: child total lost");
    Must(DiskScan.Children(sub).Length == 1, "Load: kid links not rebuilt");
    Must(DiskScan.RefreshSubtree(Path.Combine(tmp, "gone")) == -1, "RefreshSubtree: missing folder");
    DiskScan.ForgetFile(Path.Combine(sub, "c.bin"), 1024);
    Must(DiskScan.SizeOf(sub) == 4096, "ForgetFile: parent own bytes");
    Stopwatch sw;

    // Project freshness: a cache is judged by what surrounds it, not by itself.
    var proj = Path.Combine(tmp, "proj"); var nm = Path.Combine(proj, "node_modules");
    Directory.CreateDirectory(nm);
    File.WriteAllBytes(Path.Combine(nm, "dep.js"), new byte[10]);
    File.WriteAllBytes(Path.Combine(proj, "index.js"), new byte[10]);
    File.SetLastWriteTimeUtc(Path.Combine(nm, "dep.js"), DateTime.UtcNow.AddDays(-200));
    File.SetLastWriteTimeUtc(Path.Combine(proj, "index.js"), DateTime.UtcNow.AddDays(-3));
    DiskScan.RefreshSubtree(proj);
    Must(DiskScan.FreshOf(nm) == DateTime.UtcNow.AddDays(-200).ToString("yyyy-MM-dd"), "FreshOf: the cache itself is old");
    Must(DiskScan.ProjectFreshOf(nm) == DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd"), "ProjectFreshOf: the project around it is live");
    Must(DiskScan.FreshOf(proj) == DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd"), "FreshOf: rolls up to the newest");
    DiskScan.Save();
    Must(DiskScan.Load(tmp) && DiskScan.ProjectFreshOf(nm) == DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd"), "Load: own stamps survive the round trip");

    // Big files: the top list, oldest-first evidence attached, persisted.
    BigScan.Start(tmp, 1);
    sw = Stopwatch.StartNew();
    while (BigScan.Running && sw.Elapsed.TotalSeconds < 30) System.Threading.Thread.Sleep(50);
    Must(BigScan.Rows().Length >= 3 && BigScan.Rows()[0].StartsWith("4096|"), "BigScan: biggest first");
    BigScan.Drop(Path.Combine(sub, "a.bin"));
    Must(!BigScan.Rows()[0].StartsWith("4096|"), "BigScan: Drop removes the row");
    Must(BigScan.Load(tmp), "BigScan: results survive");
    try { File.Delete(DiskScan.IndexPath(tmp + S) + ".big"); } catch {}

    // Duplicate finder: two identical files, one decoy of the same size.
    var d1 = Path.Combine(tmp, "d1"); var d2 = Path.Combine(tmp, "d2");
    Directory.CreateDirectory(d1); Directory.CreateDirectory(d2);
    var twin = new byte[3 << 19]; twin[0] = 7; twin[twin.Length - 1] = 9;
    var odd = new byte[3 << 19]; odd[0] = 1; odd[odd.Length - 1] = 2;
    File.WriteAllBytes(Path.Combine(d1, "twin.bin"), twin);
    File.WriteAllBytes(Path.Combine(d2, "twin-copy.bin"), twin);
    File.WriteAllBytes(Path.Combine(d2, "different.bin"), odd);
    DupeScan.Start(tmp, 1 << 20);
    sw = Stopwatch.StartNew();
    while (DupeScan.Running && sw.Elapsed.TotalSeconds < 30) System.Threading.Thread.Sleep(100);
    Must(DupeScan.Groups().Length == 1, "DupeScan: expected 1 group");
    Must(DupeScan.Groups()[0].Split('|').Length == 3, "DupeScan: a group must hold exactly the two copies");
    Must(DupeScan.Wasted == (3 << 19), "DupeScan: wasted bytes");
    Must(DupeScan.NormalizeRoot("C:") == @"C:\", "DupeScan: a bare drive must mean its root");

    try { File.Delete(DiskScan.IndexPath(tmp + S) + ".dupes"); File.Delete(DiskScan.IndexPath(tmp + S)); } catch {}
    Must(Rules.Removable(tmp), "removable: a temp dir should pass");
    Directory.Delete(tmp, true);

    // The route layer answers in the shape the page expects.
    var api = new Api();
    using var doc = JsonDocument.Parse(api.Handle("/api/drives", null, null));
    Must(doc.RootElement.TryGetProperty("rows", out _), "route: /api/drives shape");
    Must(api.Handle("/api/nope", null, null).Contains("no such route"), "route: unknown route is an error, not a crash");
  }
}
}
