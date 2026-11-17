// Keeps the saved index current by reading the NTFS change journal, so a file
// that merely grew is noticed without a rescan. Windows only, and opening the
// volume needs an elevated process - so this is opt-in and says so plainly.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DiskLens {

public static class Live {
  public static readonly bool Supported = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
  public static volatile bool On;
  public static volatile string Error = "";
  public static long Applied;                    // folders re-read since it was switched on
  public static int Pending => _pending.Count;
  public static string Since = "";

  static readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
  static Thread _t;
  static int _gen;
  static string _letter;

  public static bool Elevated() {
    if (!Supported) return false;
    try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
    catch { return false; }
  }

  static string FlagPath() => Path.Combine(Path.GetDirectoryName(DiskScan.IndexPath("x")), "live.on");
  public static bool Wanted() => File.Exists(FlagPath());
  public static void Want(bool on) {
    try { if (on) File.WriteAllText(FlagPath(), "1"); else File.Delete(FlagPath()); } catch {}
  }

  public static void Start(string drive) {
    Stop();
    Error = "";
    if (!Supported) { Error = "live tracking is Windows-only"; return; }
    if (!Elevated()) { Error = "needs DiskLens to run as administrator"; return; }
    if (drive.Length < 2 || drive[1] != ':') { Error = "not an NTFS drive letter"; return; }
    _letter = drive.Substring(0, 2);
    int g = Interlocked.Increment(ref _gen);
    On = true; Since = DateTime.Now.ToString("HH:mm"); Applied = 0;
    _t = new Thread(() => Loop(g)) { IsBackground = true };
    _t.Start();
  }

  public static void Stop() { Interlocked.Increment(ref _gen); On = false; lock (_pending) _pending.Clear(); }

  // ---------------------------------------------------------------- win32 ---
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
  [DllImport("kernel32.dll", SetLastError = true)]
  static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inLen, IntPtr outBuf, int outLen, out int ret, IntPtr ov);
  [DllImport("kernel32.dll", SetLastError = true)]
  static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref FileIdDescriptor id, uint access, uint share, IntPtr sec, uint flags);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern int GetFinalPathNameByHandle(SafeFileHandle h, StringBuilder path, int len, uint flags);

  [StructLayout(LayoutKind.Explicit, Size = 24)]
  struct FileIdDescriptor {
    [FieldOffset(0)] public uint Size;
    [FieldOffset(4)] public uint Type;        // 0 = FileIdType
    [FieldOffset(8)] public ulong FileId;
  }

  const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4, FSCTL_READ_USN_JOURNAL = 0x000900bb;
  // data written/extended/truncated, created, deleted, renamed (old and new name)
  const uint REASONS = 0x1 | 0x2 | 0x4 | 0x100 | 0x200 | 0x1000 | 0x2000;
  const int MAX_PER_TICK = 400;               // folders re-read per pass; the rest wait for the next

  static void Loop(int gen) {
    SafeFileHandle vol = null;
    try {
      vol = CreateFile(@"\\.\" + _letter, 0x80000000 /* GENERIC_READ */, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
      if (vol.IsInvalid) { Error = "could not open the volume (" + Marshal.GetLastWin32Error() + ")"; On = false; return; }
      if (!Query(vol, out ulong journalId, out long next)) { Error = "no change journal on this drive"; On = false; return; }

      var buf = Marshal.AllocHGlobal(1 << 16);
      var req = Marshal.AllocHGlobal(40);
      try {
        while (gen == _gen) {
          // Drain everything written since last time.
          while (gen == _gen) {
            Marshal.WriteInt64(req, 0, next); Marshal.WriteInt32(req, 8, (int)REASONS); Marshal.WriteInt32(req, 12, 0);
            Marshal.WriteInt64(req, 16, 0); Marshal.WriteInt64(req, 24, 0); Marshal.WriteInt64(req, 32, (long)journalId);
            if (!DeviceIoControl(vol, FSCTL_READ_USN_JOURNAL, req, 40, buf, 1 << 16, out int got, IntPtr.Zero)) {
              int e = Marshal.GetLastWin32Error();
              // 1181: our position fell off the front of the journal. Events were lost, so say so.
              if (e == 1181 && Query(vol, out journalId, out next)) { Error = "some changes were missed - press Rescan once"; break; }
              Error = "journal read failed (" + e + ")"; On = false; return;
            }
            if (got <= 8) break;
            next = Marshal.ReadInt64(buf, 0);
            for (int off = 8; off + 60 <= got;) {
              int len = Marshal.ReadInt32(buf, off);
              if (len <= 0) break;
              ulong parent = (ulong)Marshal.ReadInt64(buf, off + 16);
              var pp = PathOf(vol, parent);
              if (pp != null) lock (_pending) _pending.Add(pp);
              off += len;
            }
          }
          Apply();
          for (int i = 0; i < 20 && gen == _gen; i++) Thread.Sleep(1000);   // ~20 s between passes
        }
      } finally { Marshal.FreeHGlobal(buf); Marshal.FreeHGlobal(req); }
    } catch (Exception e) { Error = e.Message; On = false; }
    finally { vol?.Dispose(); }
  }

  static bool Query(SafeFileHandle vol, out ulong id, out long next) {
    id = 0; next = 0;
    var o = Marshal.AllocHGlobal(64);
    try {
      if (!DeviceIoControl(vol, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, o, 64, out _, IntPtr.Zero)) return false;
      id = (ulong)Marshal.ReadInt64(o, 0); next = Marshal.ReadInt64(o, 16);
      return true;
    } finally { Marshal.FreeHGlobal(o); }
  }

  // A file-reference number is a folder identity, not a path; ask the volume which folder it is.
  static string PathOf(SafeFileHandle vol, ulong frn) {
    var d = new FileIdDescriptor { Size = 24, Type = 0, FileId = frn };
    using var h = OpenFileById(vol, ref d, 0, 7, IntPtr.Zero, 0x02000000);
    if (h.IsInvalid) return null;                 // folder already gone; its own parent's change covers it
    var sb = new StringBuilder(1024);
    int n = GetFinalPathNameByHandle(h, sb, sb.Capacity, 0);
    if (n <= 0 || n >= sb.Capacity) return null;
    var p = sb.ToString();
    return p.StartsWith(@"\\?\") ? p.Substring(4) : p;
  }

  static void Apply() {
    string[] batch;
    lock (_pending) {
      batch = _pending.Where(p => p.StartsWith(_letter, StringComparison.OrdinalIgnoreCase)).Take(MAX_PER_TICK).ToArray();
      _pending.ExceptWith(batch);   // the remainder waits for the next pass
    }
    if (batch.Length == 0) return;
    foreach (var d in batch) {
      try { DiskScan.RefreshDir(d); Interlocked.Increment(ref Applied); } catch {}
    }
    DiskScan.Save();
  }
}
}
