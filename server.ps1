#Requires -Version 7
<#
DiskLens - a light, local disk analyser and cleaner.

One full pass over the drive builds a per-directory size map in memory; the
treemap, hotspot list and cleaner all read from that map, so drilling around
afterwards is instant. Deletions go to the Recycle Bin and a deny list keeps
system directories out of reach.

  .\server.ps1                 # scan C:, open the UI
  .\server.ps1 -Drive D: -Port 9000
  .\server.ps1 -SelfTest
#>
param(
  [string]$Drive = $env:SystemDrive,
  [int]$Port = 8731,
  [switch]$NoBrowser,
  [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName Microsoft.VisualBasic
Add-Type -AssemblyName System.Web

# ---------------------------------------------------------------- scanner ---
# The walk is C# because a million-file enumeration in PowerShell loops is
# minutes slower for no benefit. FileInfo from EnumerateFiles already carries
# Length, so there is no second stat call per file.
if (-not ('DiskScan' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public static class DiskScan {
  public static Dictionary<string, long> Own   = NewMap();
  public static Dictionary<string, long> Total = NewMap();
  // Newest file write under each folder, in UTC ticks. Free to collect - the
  // walk already has every FileInfo in hand - and it is the honest proxy for
  // "is this still in use".
  public static Dictionary<string, long> Fresh = NewMap();
  public static Dictionary<string, List<string>> Kids = NewKids();

  public static long FileCount;
  public static long DirCount;
  public static volatile string Current = "";
  public static volatile string Root = "";
  public static volatile bool Done;
  public static volatile bool Running;
  public static volatile bool HasIndex;        // something is worth showing
  public static long IndexedAtUtc;             // DateTime.ToBinary of the last full walk

  const int FORMAT = 2;          // 2 added the per-folder freshness stamp

  class Part {
    public Dictionary<string, long> Own = NewMap();
    public Dictionary<string, long> Fresh = NewMap();
    public Dictionary<string, List<string>> Kids = NewKids();
  }

  static Dictionary<string, long> NewMap() {
    return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
  }
  static Dictionary<string, List<string>> NewKids() {
    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
  }

  public static void Start(string root) {
    var t = new Thread(() => Walk(root));
    t.IsBackground = true;
    t.Start();
  }

  // One directory: its own bytes, and its child directories appended to kids.
  // live=false for a subtree refresh, so patching one folder cannot inflate the
  // headline counts that belong to the last full walk.
  static long ScanDir(string dir, List<string> kids, bool live, out long newest) {
    long own = 0;
    newest = 0;
    var di = new DirectoryInfo(dir);
    try {
      foreach (var f in di.EnumerateFiles()) {
        try {
          own += f.Length;
          var t = f.LastWriteTimeUtc.Ticks;
          if (t > newest) newest = t;
          if (live) Interlocked.Increment(ref FileCount);
        } catch {}
      }
    } catch {}
    try {
      foreach (var sub in di.EnumerateDirectories()) {
        // A reparse point's bytes belong to its target; following it double-counts.
        try { if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue; }
        catch { continue; }
        kids.Add(sub.FullName);
      }
    } catch {}
    if (live) { Interlocked.Increment(ref DirCount); Current = dir; }
    return own;
  }

  // Depth-first from one starting folder into the caller's own maps, so workers
  // never touch a shared dictionary.
  static void WalkInto(string start, Dictionary<string, long> own, Dictionary<string, List<string>> kids,
                       Dictionary<string, long> fresh, bool live) {
    var stack = new Stack<string>();
    stack.Push(start);
    while (stack.Count > 0) {
      string dir = stack.Pop();
      var mine = new List<string>();
      long newest;
      own[dir] = ScanDir(dir, mine, live, out newest);
      fresh[dir] = newest;
      kids[dir] = mine;
      for (int i = 0; i < mine.Count; i++) stack.Push(mine[i]);
    }
  }

  // A child path is always longer than its parent, so longest-first visits every
  // child before the parent that needs its total.
  static Dictionary<string, long> Rollup(Dictionary<string, long> own, Dictionary<string, List<string>> kids,
                                        Dictionary<string, long> fresh) {
    var total = NewMap();
    var keys = own.Keys.ToList();
    keys.Sort((a, b) => b.Length.CompareTo(a.Length));
    foreach (var k in keys) {
      long t = own[k], f;
      fresh.TryGetValue(k, out f);
      List<string> kk;
      if (kids.TryGetValue(k, out kk))
        foreach (var c in kk) {
          long cv;
          if (total.TryGetValue(c, out cv)) t += cv;
          long cf;
          if (fresh.TryGetValue(c, out cf) && cf > f) f = cf;   // newest wins upward
        }
      total[k] = t;
      fresh[k] = f;
    }
    return total;
  }

  public static string FreshOf(string dir) {
    long t;
    if (!Fresh.TryGetValue(Key(dir), out t) || t <= 0) return null;
    return new DateTime(t, DateTimeKind.Utc).ToString("yyyy-MM-dd");
  }

  public static void Walk(string root) {
    root = root.TrimEnd('\\') + "\\";
    Running = true; Root = root;
    FileCount = 0; DirCount = 0;

    // Build into fresh maps and publish by reference at the end: readers keep
    // serving the previous index for the whole scan instead of seeing a
    // half-built one, and there is no shared dictionary to race on.
    var own = NewMap();
    var kids = NewKids();
    var fresh = NewMap();
    var top = new List<string>();
    long rootNewest;
    own[root] = ScanDir(root, top, true, out rootNewest);
    fresh[root] = rootNewest;
    kids[root] = top;

    var parts = new ConcurrentBag<Part>();
    // Metadata reads are latency-bound, not CPU-bound, so oversubscribe.
    var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount * 2) };
    Parallel.ForEach(top, opts, branch => {
      var part = new Part();
      WalkInto(branch, part.Own, part.Kids, part.Fresh, true);
      parts.Add(part);
    });
    foreach (var p in parts) {
      foreach (var kv in p.Own) own[kv.Key] = kv.Value;
      foreach (var kv in p.Kids) kids[kv.Key] = kv.Value;
      foreach (var kv in p.Fresh) fresh[kv.Key] = kv.Value;
    }

    var total = Rollup(own, kids, fresh);
    Own = own; Kids = kids; Total = total; Fresh = fresh;
    IndexedAtUtc = DateTime.UtcNow.ToBinary();
    Done = true; HasIndex = true; Running = false;
    Save();
  }

  // ---------------------------------------------------------- persistence ---
  public static string IndexPath(string root) {
    var dir = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskLens");
    Directory.CreateDirectory(dir);
    var name = root.Replace(":", "").Replace("\\", "").Replace("/", "");
    if (name.Length == 0) name = "drive";
    return Path.Combine(dir, name + ".idx");
  }

  // Only (path, ownBytes) is stored. Kids and Total are derived, so there is no
  // second source of truth to fall out of step - and the file is half the size.
  public static void Save() {
    try {
      var own = Own; var root = Root;
      using (var fs = File.Create(IndexPath(root)))
      using (var w = new BinaryWriter(fs, System.Text.Encoding.UTF8)) {
        w.Write(FORMAT);
        w.Write(root);
        w.Write(IndexedAtUtc);
        w.Write(FileCount);
        w.Write(DirCount);
        var fresh = Fresh;
        w.Write(own.Count);
        foreach (var kv in own) {
          long f;
          fresh.TryGetValue(kv.Key, out f);
          w.Write(kv.Key); w.Write(kv.Value); w.Write(f);
        }
      }
    } catch {}
  }

  public static bool Load(string root) {
    root = root.TrimEnd('\\') + "\\";
    try {
      var path = IndexPath(root);
      if (!File.Exists(path)) return false;
      using (var fs = File.OpenRead(path))
      using (var r = new BinaryReader(fs, System.Text.Encoding.UTF8)) {
        if (r.ReadInt32() != FORMAT) return false;
        if (!string.Equals(r.ReadString(), root, StringComparison.OrdinalIgnoreCase)) return false;
        long when = r.ReadInt64(), files = r.ReadInt64(), dirs = r.ReadInt64();
        int n = r.ReadInt32();
        if (n <= 0) return false;
        var own = NewMap();
        var fresh = NewMap();
        for (int i = 0; i < n; i++) {
          var k = r.ReadString();
          own[k] = r.ReadInt64();
          fresh[k] = r.ReadInt64();
        }
        var kids = NewKids();
        foreach (var k in own.Keys) kids[k] = new List<string>();
        foreach (var k in own.Keys) {
          var p = ParentOf(k);
          if (p == null) continue;
          List<string> list;
          if (kids.TryGetValue(p, out list)) list.Add(k);
          else if (kids.TryGetValue(p + "\\", out list)) list.Add(k);
        }
        Own = own; Kids = kids; Fresh = fresh; Total = Rollup(own, kids, fresh);
        Root = root; FileCount = files; DirCount = dirs; IndexedAtUtc = when;
        Done = true; HasIndex = true; Running = false;
        return true;
      }
    } catch { return false; }
  }

  // ----------------------------------------------------------- patching ---
  static string ParentOf(string dir) {
    try {
      var p = Path.GetDirectoryName(dir.TrimEnd('\\'));
      return string.IsNullOrEmpty(p) ? null : p;
    } catch { return null; }
  }

  static List<string> SubtreeOf(string key) {
    var all = new List<string>();
    var stack = new Stack<string>();
    stack.Push(key);
    while (stack.Count > 0) {
      var d = stack.Pop();
      all.Add(d);
      List<string> kk;
      if (Kids.TryGetValue(d, out kk)) foreach (var c in kk) stack.Push(c);
    }
    return all;
  }

  static void AddToAncestors(string key, long delta) {
    if (delta == 0) return;
    for (var a = ParentOf(key); a != null; a = ParentOf(a)) {
      var ak = Key(a);
      long v;
      if (Total.TryGetValue(ak, out v)) Total[ak] = Math.Max(0, v + delta);
    }
  }

  // Drop a folder from the index and settle the books upward. Deleting through
  // the app no longer costs a full rescan.
  // ponytail: FileCount/DirCount are not adjusted, so the headline counts drift
  // until the next full walk - wire per-subtree counts in if anyone cares.
  public static long Forget(string dir) {
    var key = Key(dir);
    long old = SizeOf(key);
    if (old < 0) return 0;
    // ponytail: an ancestor's freshness stamp is left as it was - recomputing a
    // max upward costs a subtree walk, and it is a hint, not a number.
    foreach (var d in SubtreeOf(key)) { Own.Remove(d); Kids.Remove(d); Total.Remove(d); Fresh.Remove(d); }
    var parent = ParentOf(key);
    if (parent != null) {
      List<string> pk;
      if (Kids.TryGetValue(Key(parent), out pk))
        pk.RemoveAll(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
    }
    AddToAncestors(key, -old);
    return old;
  }

  // Re-read one folder and splice the new numbers in. Everything else keeps its
  // existing totals, so this is a sub-second operation on a normal folder.
  public static long RefreshSubtree(string dir) {
    var key = Key(dir);
    Forget(key);
    var target = key.TrimEnd('\\');
    if (target.Length == 2) target = key;                    // a drive root keeps its slash
    if (!Directory.Exists(target)) { Save(); return -1; }

    var own = NewMap(); var kids = NewKids(); var fresh = NewMap();
    WalkInto(target, own, kids, fresh, false);
    foreach (var kv in own) Own[kv.Key] = kv.Value;
    foreach (var kv in kids) Kids[kv.Key] = kv.Value;

    var keys = own.Keys.ToList();
    keys.Sort((a, b) => b.Length.CompareTo(a.Length));
    foreach (var k in keys) {
      long t = own[k], f;
      fresh.TryGetValue(k, out f);
      List<string> kk;
      if (kids.TryGetValue(k, out kk))
        foreach (var c in kk) {
          long cv;
          if (Total.TryGetValue(c, out cv)) t += cv;
          long cf;
          if (Fresh.TryGetValue(c, out cf) && cf > f) f = cf;
        }
      Total[k] = t;
      Fresh[k] = f;
    }

    var parent = ParentOf(target);
    if (parent != null) {
      List<string> pk;
      if (Kids.TryGetValue(Key(parent), out pk) &&
          !pk.Any(x => string.Equals(x, target, StringComparison.OrdinalIgnoreCase))) pk.Add(target);
    }
    long now = SizeOf(target);
    AddToAncestors(target, now);
    HasIndex = true;
    Save();
    return now;
  }

  // The root is stored with a trailing separator and every other folder without
  // one, while callers pass either form. Resolve to whichever key exists.
  static string Key(string dir) {
    if (dir == null) return "";
    if (Total.ContainsKey(dir) || Kids.ContainsKey(dir)) return dir;
    string trimmed = dir.TrimEnd('\\');
    if (Total.ContainsKey(trimmed) || Kids.ContainsKey(trimmed)) return trimmed;
    string slashed = trimmed + "\\";
    if (Total.ContainsKey(slashed) || Kids.ContainsKey(slashed)) return slashed;
    return dir;
  }

  public static long SizeOf(string dir) {
    long v;
    return Total.TryGetValue(Key(dir), out v) ? v : -1;
  }

  // The biggest folders anywhere, minus the ones that are just a thin wrapper
  // around a single fat child (those add a row and no information).
  public static string[] Hotspots(long minBytes, int limit) {
    var hits = new List<KeyValuePair<string, long>>();
    foreach (var kv in Total) {
      if (kv.Value < minBytes) continue;
      long biggestKid = 0, bigKids = 0;
      List<string> kids;
      if (Kids.TryGetValue(kv.Key, out kids))
        foreach (var c in kids) {
          long cv;
          if (!Total.TryGetValue(c, out cv)) continue;
          if (cv > biggestKid) biggestKid = cv;
          if (cv >= minBytes) bigKids++;
        }
      if (biggestKid >= kv.Value * 0.5) continue;              // a wrapper around one fat child
      long ownHere; Own.TryGetValue(kv.Key, out ownHere);
      if (bigKids >= 2 && ownHere < kv.Value * 0.1) continue;  // a container; its children are listed
      hits.Add(kv);
    }
    return hits.OrderByDescending(h => h.Value).Take(limit)
               .Select(h => h.Value + "|" + h.Key).ToArray();
  }

  public static string[] Children(string dir) {
    var rows = new List<string>();
    string key = Key(dir);
    List<string> kids;
    if (Kids.TryGetValue(key, out kids))
      foreach (var c in kids) rows.Add(SizeOf(c) + "|d|" + c);
    long own;
    if (Own.TryGetValue(key, out own) && own > 0) rows.Add(own + "|f|" + key);
    return rows.ToArray();
  }

  // Deleting a single file only moves its parent's own bytes.
  public static void ForgetFile(string path, long size) {
    var parent = ParentOf(path);
    if (parent == null || size <= 0) return;
    var pk = Key(parent);
    long v;
    if (Own.TryGetValue(pk, out v)) Own[pk] = Math.Max(0, v - size);
    if (Total.TryGetValue(pk, out v)) Total[pk] = Math.Max(0, v - size);
    AddToAncestors(pk, -size);
  }

  public static volatile bool LastPreviewTruncated;

  // Bounded on purpose: a preview of C:\Users would otherwise walk 450 GB while
  // the single-threaded HTTP loop waits, which freezes the whole UI. Keeps the
  // running top-n instead of sorting everything.
  // ponytail: time budget, not a background index - revisit if previews of
  // multi-million-file folders need to be exact.
  public static string[] TopFiles(string dir, int n, int msBudget) {
    LastPreviewTruncated = false;
    var opts = new EnumerationOptions {
      RecurseSubdirectories = true,
      IgnoreInaccessible = true,
      AttributesToSkip = FileAttributes.ReparsePoint
    };
    var best = new List<FileInfo>(n + 1);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    long seen = 0;
    try {
      foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts)) {
        seen++;
        if (best.Count < n) {
          best.Add(f);
          if (best.Count == n) best.Sort((a, b) => b.Length.CompareTo(a.Length));
        } else if (f.Length > best[n - 1].Length) {
          best[n - 1] = f;
          best.Sort((a, b) => b.Length.CompareTo(a.Length));
        }
        if ((seen & 2047) == 0 && clock.ElapsedMilliseconds > msBudget) {
          LastPreviewTruncated = true;
          break;
        }
      }
    } catch {}
    best.Sort((a, b) => b.Length.CompareTo(a.Length));
    return best.Select(f => f.Length + "|" + f.LastWriteTime.ToString("yyyy-MM-dd") + "|" + f.FullName)
               .ToArray();
  }
}

// Same bytes, several places. Size buckets first (free), then a signature only
// for files that collide on size, so almost nothing gets read.
public static class DupeScan {
  public static volatile bool Running;
  public static volatile bool Done;
  public static volatile string Current = "";
  public static volatile string Scope = "";
  public static long Seen, Candidates, Hashed, Wasted;
  static string[] _groups = new string[0];

  public static string[] Groups() { return _groups; }

  // "C:" means "the current directory on C:" to Windows, not the drive root.
  // Scanning the wrong folder silently returns nothing, so normalise first.
  public static string NormalizeRoot(string root) {
    if (string.IsNullOrWhiteSpace(root)) return root;
    root = root.Trim();
    if (root.Length == 2 && root[1] == ':') return root + "\\";
    return root;
  }

  public static void Start(string root, long minBytes) {
    if (Running) return;
    root = NormalizeRoot(root);
    // Flip the flag here, not inside the thread: a caller that polls Running
    // immediately would otherwise see "finished" before the scan even began.
    Running = true; Done = false; Scope = root;
    var t = new Thread(() => Run(root, minBytes));
    t.IsBackground = true;
    t.Start();
  }

  // ponytail: the signature is size + SHA256 of the first and last 1 MB, not the
  // whole file - fast enough to run over a drive, and nothing is deleted without
  // a human looking at the paths. Hash the full file if that ever stops being true.
  static string Signature(string path, long size) {
    try {
      using (var sha = System.Security.Cryptography.SHA256.Create())
      using (var fs = File.OpenRead(path)) {
        var buf = new byte[1 << 20];
        int n = fs.Read(buf, 0, buf.Length);
        sha.TransformBlock(buf, 0, n, null, 0);
        if (size > (2L << 20)) {
          fs.Seek(-(1 << 20), SeekOrigin.End);
          n = fs.Read(buf, 0, buf.Length);
          sha.TransformBlock(buf, 0, n, null, 0);
        }
        var tail = BitConverter.GetBytes(size);
        sha.TransformFinalBlock(tail, 0, tail.Length);
        return BitConverter.ToString(sha.Hash).Replace("-", "");
      }
    } catch { return null; }
  }

  static void Run(string root, long minBytes) {
    root = NormalizeRoot(root);
    Running = true; Done = false; Scope = root;
    Seen = 0; Candidates = 0; Hashed = 0; Wasted = 0;
    _groups = new string[0];

    var opts = new EnumerationOptions {
      RecurseSubdirectories = true,
      IgnoreInaccessible = true,
      AttributesToSkip = FileAttributes.ReparsePoint
    };
    var bySize = new Dictionary<long, List<string>>();
    try {
      foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts)) {
        Seen++;
        if ((Seen & 4095) == 0) Current = f.DirectoryName ?? "";
        long len;
        try { len = f.Length; } catch { continue; }
        if (len < minBytes) continue;
        List<string> bucket;
        if (!bySize.TryGetValue(len, out bucket)) { bucket = new List<string>(); bySize[len] = bucket; }
        bucket.Add(f.FullName);
      }
    } catch {}

    var found = new List<KeyValuePair<long, string>>();   // wasted bytes -> row
    foreach (var kv in bySize) {
      if (kv.Value.Count < 2) continue;
      Candidates += kv.Value.Count;
      var byHash = new Dictionary<string, List<string>>();
      foreach (var p in kv.Value) {
        Current = p;
        var sig = Signature(p, kv.Key);
        Hashed++;
        if (sig == null) continue;
        List<string> g;
        if (!byHash.TryGetValue(sig, out g)) { g = new List<string>(); byHash[sig] = g; }
        g.Add(p);
      }
      foreach (var g in byHash.Values) {
        if (g.Count < 2) continue;
        long wasted = kv.Key * (g.Count - 1);
        Wasted += wasted;
        found.Add(new KeyValuePair<long, string>(wasted, kv.Key + "|" + string.Join("|", g)));
      }
    }

    _groups = found.OrderByDescending(x => x.Key).Take(200).Select(x => x.Value).ToArray();
    Current = ""; Done = true; Running = false;
    Save();
  }

  // The scan costs a minute, so it outlives the process like the index does.
  static string Store(string root) { return DiskScan.IndexPath(root) + ".dupes"; }

  public static void Save() {
    try { File.WriteAllLines(Store(Scope), _groups); } catch {}
  }

  public static bool Load(string root) {
    root = NormalizeRoot(root);
    try {
      var p = Store(root);
      if (!File.Exists(p)) return false;
      var rows = File.ReadAllLines(p).Where(l => l.Length > 0).ToArray();
      if (rows.Length == 0) return false;
      long wasted = 0;
      foreach (var row in rows) {
        var f = row.Split('|');
        long size;
        if (f.Length > 2 && long.TryParse(f[0], out size)) wasted += size * (f.Length - 2);
      }
      _groups = rows; Scope = root; Wasted = wasted; Done = true;
      return true;
    } catch { return false; }
  }
}
'@
}

# -------------------------------------------------------- classification ---
# First match wins, so the cache rules sit above the system rule that would
# otherwise protect C:\Windows\Temp.
$Rules = @(
  @{ V = 'CACHE'; N = 'Build or package cache - rebuilds itself'; R = '\\(\.gradle\\caches|npm-cache|\.npm|uv\\cache|pip\\Cache|\.cargo\\registry\\cache|\.nuget\\packages|\.m2\\repository|__pycache__|node_modules|\.next|\.nuxt|\.parcel-cache|\.pytest_cache|\.mypy_cache)$' }
  @{ V = 'CACHE'; N = 'Temporary files - safe to empty'; R = '\\(Temp|tmp|CrashDumps|Crashpad|Download|Prefetch)$' }
  @{ V = 'CACHE'; N = 'App cache - the app refills it on demand'; R = '\\([Cc]aches?|Code Cache|GPUCache|ShaderCache|DawnCache|WebCache|INetCache|GrShaderCache)$' }
  @{ V = 'MODELS'; N = 'Model weights - re-downloading is slow'; R = '\\(huggingface|\.ollama|\.lmstudio|whisper|models)(\\|$)' }
  @{ V = 'HEAVY'; N = 'Container or VM disks - prune from their own tool, do not delete'; R = '\\(Docker|wsl|VirtualBox VMs|Hyper-V|\.vagrant)(\\|$)' }
  @{ V = 'DATA'; N = 'Your own files'; R = '\\(Documents|Pictures|Videos|Music|Desktop|Downloads|OneDrive[^\\]*)$' }
  @{ V = 'SYSTEM'; N = 'Windows needs this'; R = '^[A-Za-z]:\\(Windows|Program Files|Program Files \(x86\)|ProgramData|\$Recycle\.Bin|System Volume Information|Recovery)' }
)

# Never removable, whatever the UI asks for.
$Deny = @(
  '^[A-Za-z]:\\?$'
  '^[A-Za-z]:\\[^\\]+\\?$'
  '^[A-Za-z]:\\Users\\[^\\]+\\?$'
  '\\(Documents|Pictures|Videos|Music|Desktop|Downloads|Favorites|Links|Searches|Contacts|OneDrive[^\\]*)\\?$'
  '^[A-Za-z]:\\Windows\\(System32|SysWOW64|WinSxS|Installer|Fonts|Boot|assembly|servicing)'
  '^[A-Za-z]:\\Program Files'
  '^[A-Za-z]:\\ProgramData'
  '^[A-Za-z]:\\Windows'
  '^[A-Za-z]:\\(\$Recycle\.Bin|System Volume Information|Recovery|PerfLogs)'
  '\\AppData\\(Local|Roaming|LocalLow)\\?$'
  '\\AppData\\?$'
)

function Get-Verdict([string]$Path) {
  $p = $Path.TrimEnd('\')
  foreach ($r in $Rules) { if ($p -match $r.R) { return @{ v = $r.V; note = $r.N } } }
  @{ v = 'UNKNOWN'; note = 'Not recognised - look inside before removing' }
}

# The only three places under a system root that are genuinely disposable.
$AllowInSystem = @('^[A-Za-z]:\\Windows\\(Temp|Prefetch|SoftwareDistribution\\Download)(\\|$)')

function Test-Removable([string]$Path) {
  if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
  $p = $Path.TrimEnd('\')
  if ($p.Length -lt 4) { return $false }
  foreach ($a in $AllowInSystem) { if ($p -match $a) { return [bool](Test-Path -LiteralPath $Path) } }
  foreach ($d in $Deny) { if ($p -match $d) { return $false } }
  # Whatever Windows or an installed program owns stays put, however deep it sits.
  if ((Get-Verdict $p).v -eq 'SYSTEM') { return $false }
  [bool](Test-Path -LiteralPath $Path)
}

# ------------------------------------------------------------- self-test ---
if ($SelfTest) {
  if ((Get-Verdict 'C:\Users\x\.gradle\caches').v -ne 'CACHE') { throw 'verdict: gradle caches' }
  if ((Get-Verdict 'C:\Windows\Temp').v -ne 'CACHE') { throw 'verdict: cache must beat system' }
  if ((Get-Verdict 'C:\Windows\System32').v -ne 'SYSTEM') { throw 'verdict: system' }
  if ((Get-Verdict 'C:\Users\x\.cache\huggingface').v -ne 'MODELS') { throw 'verdict: models' }
  if ((Get-Verdict 'C:\Users\x\Videos').v -ne 'DATA') { throw 'verdict: data' }
  if ((Get-Verdict 'C:\Users\x\projects\thing').v -ne 'UNKNOWN') { throw 'verdict: fallback' }
  if ((Get-Verdict 'C:\Users\x\AppData\Local\Docker\wsl\disk').v -ne 'HEAVY') { throw 'verdict: vm disk inside Docker' }
  foreach ($p in @('C:\', 'C:\Users', 'C:\Windows', 'C:\ProgramData', 'C:\Users\sachi',
                   'C:\Windows\System32\drivers', 'C:\Program Files\x',
                   'C:\Users\sachi\AppData\Local', 'C:\Users\sachi\Videos',
                   'C:\Users\sachi\Downloads')) {
    if (Test-Removable $p) { throw "deny list let through: $p" }
  }
  if (Test-Removable 'C:\Users\sachi\AppData\Local\no-such-dir-xyz') { throw 'removable: missing path' }
  if (Test-Removable 'C:\ProgramData\SomeApp\Extras') { throw 'removable: SYSTEM verdict must be protected' }
  if (Test-Removable 'C:\ProgramData\NVIDIA\NGX\models\x') { throw 'removable: a system root beats a cache rule' }
  if (Test-Removable 'C:\Windows\WinSxS') { throw 'removable: WinSxS' }
  if (-not (Test-Removable 'C:\Windows\Temp')) { throw 'removable: Windows\Temp is the documented exception' }
  if (-not (Test-Removable "$env:LOCALAPPDATA\Temp")) { throw 'removable: a cache under AppData should pass' }

  $tmp = Join-Path ([IO.Path]::GetTempPath()) ("dl-" + [guid]::NewGuid())
  New-Item -ItemType Directory -Path "$tmp\sub" -Force | Out-Null
  [IO.File]::WriteAllBytes("$tmp\sub\a.bin", (New-Object byte[] 4096))
  [IO.File]::WriteAllBytes("$tmp\b.bin", (New-Object byte[] 2048))
  [DiskScan]::Walk($tmp)
  if ([DiskScan]::SizeOf($tmp) -ne 6144) { throw 'rollup: parent total wrong' }
  if ([DiskScan]::SizeOf("$tmp\sub") -ne 4096) { throw 'rollup: child total wrong' }
  # Walking up builds a path with a trailing separator; it must resolve the same.
  if ([DiskScan]::SizeOf("$tmp\sub\") -ne 4096) { throw 'rollup: trailing separator' }
  if (([DiskScan]::Children("$tmp\")).Count -ne 2) { throw 'Children: trailing separator' }
  if (([DiskScan]::Children("$tmp\sub\")).Count -ne 1) { throw 'Children: leaf with a trailing separator' }
  if ([DiskScan]::FileCount -ne 2) { throw 'walk: file count' }
  if (([DiskScan]::TopFiles($tmp, 5, 2500)).Count -ne 2) { throw 'TopFiles: count' }
  $top = [DiskScan]::TopFiles($tmp, 1, 2500)
  if ($top.Count -ne 1 -or [long]($top[0].Split('|')[0]) -ne 4096) { throw 'TopFiles: keeps the biggest' }
  if ([DiskScan]::LastPreviewTruncated) { throw 'TopFiles: a tiny tree must not hit the budget' }
  if (([DiskScan]::Children($tmp)).Count -ne 2) { throw 'Children: dir row plus own-files row' }

  # Forget: the subtree goes, and every ancestor's total settles without a rescan.
  if ([DiskScan]::Forget("$tmp\sub") -ne 4096) { throw 'Forget: returned size' }
  if ([DiskScan]::SizeOf("$tmp\sub") -ne -1) { throw 'Forget: subtree still indexed' }
  if ([DiskScan]::SizeOf($tmp) -ne 2048) { throw 'Forget: parent total not settled' }
  if (([DiskScan]::Children($tmp)).Count -ne 1) { throw 'Forget: parent still lists the child' }

  # RefreshSubtree: re-reads one folder, including whatever changed inside it.
  [IO.File]::WriteAllBytes("$tmp\sub\c.bin", (New-Object byte[] 1024))
  if ([DiskScan]::RefreshSubtree("$tmp\sub") -ne 5120) { throw 'RefreshSubtree: subtree total' }
  if ([DiskScan]::SizeOf($tmp) -ne 7168) { throw 'RefreshSubtree: ancestors not updated' }
  if (([DiskScan]::Children($tmp)).Count -ne 2) { throw 'RefreshSubtree: child not relinked' }

  # The snapshot round-trips: mutate memory, reload, get the saved numbers back.
  [DiskScan]::Save()
  [DiskScan]::Forget("$tmp\sub") | Out-Null
  if (-not [DiskScan]::Load($tmp)) { throw 'Load: snapshot not restored' }
  if ([DiskScan]::SizeOf("$tmp\sub") -ne 5120) { throw 'Load: child total lost' }
  if ([DiskScan]::SizeOf($tmp) -ne 7168) { throw 'Load: rollup not rebuilt' }
  if (([DiskScan]::Children("$tmp\sub")).Count -ne 1) { throw 'Load: kid links not rebuilt' }
  if (-not [DiskScan]::HasIndex) { throw 'Load: index not marked usable' }
  if ([DiskScan]::RefreshSubtree("$tmp\gone") -ne -1) { throw 'RefreshSubtree: missing folder' }

  # Freshness rolls up as a max: a parent is as fresh as its newest file.
  (Get-Item "$tmp\sub\c.bin").LastWriteTime = (Get-Date).AddDays(-400)
  (Get-Item "$tmp\sub\a.bin").LastWriteTime = (Get-Date).AddDays(-10)
  [DiskScan]::RefreshSubtree("$tmp\sub") | Out-Null
  $expected = (Get-Date).AddDays(-10).ToUniversalTime().ToString('yyyy-MM-dd')
  if ([DiskScan]::FreshOf("$tmp\sub") -ne $expected) { throw 'FreshOf: newest file wins' }
  if ([DiskScan]::FreshOf('C:\no\such\dir')) { throw 'FreshOf: unknown path' }

  # Deleting one file only moves its parent's own bytes.
  [DiskScan]::ForgetFile("$tmp\sub\c.bin", 1024)
  if ([DiskScan]::SizeOf("$tmp\sub") -ne 4096) { throw 'ForgetFile: parent own bytes' }
  if ([DiskScan]::SizeOf($tmp) -ne 6144) { throw 'ForgetFile: ancestors' }

  # Duplicate finder: two identical files, one decoy of the same size.
  New-Item -ItemType Directory -Path "$tmp\d1", "$tmp\d2" -Force | Out-Null
  $twin = New-Object byte[] (1.5MB); $twin[0] = 7; $twin[1572863] = 9
  [IO.File]::WriteAllBytes("$tmp\d1\twin.bin", $twin)
  [IO.File]::WriteAllBytes("$tmp\d2\twin-copy.bin", $twin)
  $odd = New-Object byte[] (1.5MB); $odd[0] = 1; $odd[1572863] = 2
  [IO.File]::WriteAllBytes("$tmp\d2\different.bin", $odd)
  [DupeScan]::Start($tmp, 1MB)
  $wait = [Diagnostics.Stopwatch]::StartNew()
  while ([DupeScan]::Running -and $wait.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 100 }
  $groups = [DupeScan]::Groups()
  if ($groups.Count -ne 1) { throw "DupeScan: expected 1 group, got $($groups.Count)" }
  $cols = $groups[0].Split('|')
  if ($cols.Count -ne 3) { throw 'DupeScan: a group must hold exactly the two copies' }
  if ([long]$cols[0] -ne 1.5MB) { throw 'DupeScan: group size' }
  if ([DupeScan]::Wasted -ne 1.5MB) { throw 'DupeScan: wasted bytes' }
  if ([DupeScan]::NormalizeRoot('C:') -ne 'C:\') { throw 'DupeScan: a bare drive must mean its root' }
  if ([DupeScan]::NormalizeRoot('C:\Users') -ne 'C:\Users') { throw 'DupeScan: a real path is left alone' }
  if (-not [DupeScan]::Load($tmp)) { throw 'DupeScan: results did not survive' }
  if (([DupeScan]::Groups()).Count -ne 1) { throw 'DupeScan: reloaded group count' }
  if ([DupeScan]::Wasted -ne 1.5MB) { throw 'DupeScan: reloaded wasted bytes' }
  Remove-Item ([DiskScan]::IndexPath("$tmp\") + '.dupes') -Force -ErrorAction SilentlyContinue
  Remove-Item ([DiskScan]::IndexPath("$tmp\")) -Force -ErrorAction SilentlyContinue
  if (-not (Test-Removable $tmp)) { throw 'removable: a temp dir should pass' }
  Remove-Item $tmp -Recurse -Force
  'self-test OK'
  return
}

# ------------------------------------------------------------- http layer ---
$uiPath = Join-Path $PSScriptRoot 'ui.html'
if (-not (Test-Path -LiteralPath $uiPath)) { throw 'ui.html is missing next to server.ps1' }

$listener = [System.Net.HttpListener]::new()
for ($p = $Port; $p -lt $Port + 20; $p++) {
  try {
    $listener.Prefixes.Clear()
    $listener.Prefixes.Add("http://127.0.0.1:$p/")
    $listener.Start()
    $Port = $p
    break
  } catch { $listener = [System.Net.HttpListener]::new() }
}
if (-not $listener.IsListening) { throw 'no free port in range' }

function Send-Json($ctx, $obj) {
  $b = [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Depth 6 -Compress))
  $ctx.Response.ContentType = 'application/json; charset=utf-8'
  $ctx.Response.OutputStream.Write($b, 0, $b.Length)
}

# A saved index opens instantly; the drive is only walked when there is none.
if ([DiskScan]::Load($Drive)) {
  Write-Host "  index restored from $([DiskScan]::IndexPath($Drive))" -ForegroundColor DarkGray
} else {
  [DiskScan]::Start($Drive)
}
$url = "http://127.0.0.1:$Port/"
Write-Host "`n  DiskLens  $url   (Ctrl+C to stop)`n" -ForegroundColor Cyan
if (-not $NoBrowser) { Start-Process $url | Out-Null }

while ($listener.IsListening) {
  $ctx = $listener.GetContext()
  try {
    $route = $ctx.Request.Url.AbsolutePath
    $q = [System.Web.HttpUtility]::ParseQueryString($ctx.Request.Url.Query)
    $path = $q['path']

    switch -Regex ($route) {

      '^/$|^/index\.html$' {
        $b = [IO.File]::ReadAllBytes($uiPath)
        $ctx.Response.ContentType = 'text/html; charset=utf-8'
        $ctx.Response.OutputStream.Write($b, 0, $b.Length)
      }

      '^/landing$|^/landing\.html$' {
        $page = Join-Path $PSScriptRoot 'index.html'
        if (Test-Path -LiteralPath $page) {
          $b = [IO.File]::ReadAllBytes($page)
          $ctx.Response.ContentType = 'text/html; charset=utf-8'
          $ctx.Response.OutputStream.Write($b, 0, $b.Length)
        } else { $ctx.Response.StatusCode = 404 }
      }

      '^/api/status$' {
        $d = Get-PSDrive $Drive.TrimEnd(':', '\') -ErrorAction SilentlyContinue
        Send-Json $ctx @{
          running = [DiskScan]::Running
          done    = [DiskScan]::Done
          dirs    = if ([DiskScan]::Running) { [DiskScan]::DirCount } else { [DiskScan]::Own.Count }
          files   = [DiskScan]::FileCount
          current = [DiskScan]::Current
          root    = [DiskScan]::Root
          free    = if ($d) { [long]$d.Free } else { 0 }
          used    = if ($d) { [long]$d.Used } else { 0 }
          drive   = $Drive
          hasIndex  = [DiskScan]::HasIndex
          indexedAt = if ([DiskScan]::IndexedAtUtc -ne 0) {
            [datetime]::FromBinary([DiskScan]::IndexedAtUtc).ToString('o')
          } else { $null }
        }
      }

      '^/api/drives$' {
        $rows = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Free -ne $null } |
          ForEach-Object { @{ name = "$($_.Name):"; free = [long]$_.Free; used = [long]$_.Used } }
        Send-Json $ctx @{ rows = @($rows) }
      }

      '^/api/rescan$' {
        if (-not [DiskScan]::Running) {
          $d = $q['drive']; if ([string]::IsNullOrWhiteSpace($d)) { $d = $Drive }
          $switching = $d -ne $Drive
          $Drive = $d
          # Switching drives uses that drive's saved index if it has one.
          if (-not ($switching -and [DiskScan]::Load($d))) {
            [DiskScan]::HasIndex = $false
            [DiskScan]::Start($d)
          }
        }
        Send-Json $ctx @{ ok = $true; drive = $Drive }
      }

      '^/api/dupes/start$' {
        $min = $q['min']; if ([string]::IsNullOrWhiteSpace($min)) { $min = '10' }
        $scope = $path; if ([string]::IsNullOrWhiteSpace($scope)) { $scope = $Drive }
        [DupeScan]::Start($scope, ([long]$min * 1MB))
        Send-Json $ctx @{ ok = $true; scope = $scope }
      }

      '^/api/dupes$' {
        # A scan from a previous session is still on disk - hand it back.
        if (-not [DupeScan]::Running -and -not [DupeScan]::Done) { [DupeScan]::Load($Drive) | Out-Null }
        $rows = foreach ($g in [DupeScan]::Groups()) {
          $f = $g.Split('|')
          $size = [long]$f[0]
          $paths = @($f[1..($f.Count - 1)])
          @{ size = $size; copies = $paths.Count; wasted = ($size * ($paths.Count - 1))
             name = (Split-Path $paths[0] -Leaf); paths = $paths }
        }
        Send-Json $ctx @{
          running = [DupeScan]::Running; done = [DupeScan]::Done
          scope = [DupeScan]::Scope; current = [DupeScan]::Current
          seen = [DupeScan]::Seen; hashed = [DupeScan]::Hashed
          wasted = [DupeScan]::Wasted; rows = @($rows)
        }
      }

      '^/api/refresh$' {
        $size = [DiskScan]::RefreshSubtree($path)
        Send-Json $ctx @{ path = $path; size = $size; gone = ($size -lt 0) }
      }

      '^/api/children$' {
        $rows = foreach ($r in [DiskScan]::Children($path)) {
          $f = $r.Split('|', 3)
          $isDir = $f[1] -eq 'd'
          $v = if ($isDir) { Get-Verdict $f[2] } else { @{ v = 'FILES'; note = 'Loose files sitting in this folder' } }
          @{ size = [long]$f[0]; dir = $isDir; path = $f[2]
            name = if ($isDir) { Split-Path $f[2] -Leaf } else { 'files here' }
            verdict = $v.v; note = $v.note; removable = ($isDir -and (Test-Removable $f[2]))
            fresh = [DiskScan]::FreshOf($f[2]) }
        }
        Send-Json $ctx @{ path = $path; rows = @($rows | Sort-Object -Property size -Descending) }
      }

      '^/api/hotspots$' {
        $minMb = $q['min']; if ([string]::IsNullOrWhiteSpace($minMb)) { $minMb = '500' }
        $rows = foreach ($r in [DiskScan]::Hotspots(([long]$minMb * 1MB), 120)) {
          $f = $r.Split('|', 2)
          $v = Get-Verdict $f[1]
          @{ size = [long]$f[0]; path = $f[1]; name = (Split-Path $f[1] -Leaf)
            verdict = $v.v; note = $v.note; removable = (Test-Removable $f[1])
            fresh = [DiskScan]::FreshOf($f[1]) }
        }
        Send-Json $ctx @{ rows = @($rows) }
      }

      '^/api/preview$' {
        $rows = foreach ($r in [DiskScan]::TopFiles($path, 15, 2500)) {
          $f = $r.Split('|', 3)
          @{ size = [long]$f[0]; date = $f[1]; path = $f[2]; name = (Split-Path $f[2] -Leaf) }
        }
        $v = Get-Verdict $path
        Send-Json $ctx @{ path = $path; size = [DiskScan]::SizeOf($path); verdict = $v.v
          note = $v.note; removable = (Test-Removable $path); files = @($rows)
          fresh = [DiskScan]::FreshOf($path)
          truncated = [DiskScan]::LastPreviewTruncated }
      }

      '^/api/reveal$' {
        # A folder opens; a file is only highlighted. Never hand a file to the
        # shell to launch with whatever is registered for its extension.
        if (Test-Path -LiteralPath $path -PathType Container) {
          Start-Process explorer.exe "`"$path`"" | Out-Null
        } elseif (Test-Path -LiteralPath $path -PathType Leaf) {
          Start-Process explorer.exe "/select,`"$path`"" | Out-Null
        }
        Send-Json $ctx @{ ok = $true }
      }

      '^/api/recycle$' {
        $body = [IO.StreamReader]::new($ctx.Request.InputStream).ReadToEnd() | ConvertFrom-Json
        $freed = 0L; $removed = @(); $refused = @()
        foreach ($t in @($body.paths)) {
          if (-not (Test-Removable $t)) { $refused += $t; continue }
          $isFile = Test-Path -LiteralPath $t -PathType Leaf
          $sz = if ($isFile) { (Get-Item -LiteralPath $t -Force).Length } else { [DiskScan]::SizeOf($t) }
          try {
            if ($isFile) {
              [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile(
                $t, 'OnlyErrorDialogs', 'SendToRecycleBin')
              [DiskScan]::ForgetFile($t, $sz)
            } else {
              [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(
                $t, 'OnlyErrorDialogs', 'SendToRecycleBin')
              [DiskScan]::Forget($t) | Out-Null   # patch the index, never rescan
            }
            if ($sz -gt 0) { $freed += $sz }
            $removed += $t
          } catch { $refused += $t }
        }
        if ($removed.Count) { [DiskScan]::Save() }
        Send-Json $ctx @{ freed = $freed; removed = @($removed); refused = @($refused) }
      }

      default { $ctx.Response.StatusCode = 404 }
    }
  } catch {
    $ctx.Response.StatusCode = 500
    try { Send-Json $ctx @{ error = "$_" } } catch {}
  } finally { try { $ctx.Response.Close() } catch {} }
}
