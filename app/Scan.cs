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
  // The same stamp for the folder's own loose files only. Fresh rolls up and so
  // a project containing node_modules is always "as fresh" as its cache; this
  // is what lets the cache be judged by the project around it instead.
  public static Dictionary<string, long> FreshOwn = NewMap();
  public static Dictionary<string, List<string>> Kids = NewKids();

  public static long FileCount;
  public static long DirCount;
  public static volatile string Current = "";
  public static volatile string Root = "";
  public static volatile bool Done;
  public static volatile bool Running;
  public static volatile bool HasIndex;        // something is worth showing
  public static long IndexedAtUtc;             // DateTime.ToBinary of the last full walk

  const int FORMAT = 3;          // 2 added the per-folder freshness stamp, 3 stores it un-rolled
  static readonly char SEP = Path.DirectorySeparatorChar;
  static readonly string SEPS = SEP.ToString();

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

  // Bumped by every Start/Load. A walk that finds its number stale was
  // superseded (the user switched drive) and abandons itself without publishing.
  static int Gen;

  public static void Start(string root) {
    int g = Interlocked.Increment(ref Gen);
    Running = true; Done = false; HasIndex = false;
    var t = new Thread(() => Walk(root, g));
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
                       Dictionary<string, long> fresh, bool live, int gen = -1) {
    var stack = new Stack<string>();
    stack.Push(start);
    while (stack.Count > 0) {
      if (gen >= 0 && gen != Gen) return;
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
                                        Dictionary<string, long> freshOwn, out Dictionary<string, long> fresh) {
    var total = NewMap();
    fresh = NewMap();
    var keys = own.Keys.ToList();
    keys.Sort((a, b) => b.Length.CompareTo(a.Length));
    foreach (var k in keys) {
      long t = own[k], f;
      freshOwn.TryGetValue(k, out f);
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

  static string Day(long ticks) {
    return ticks <= 0 ? null : new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd");
  }

  // Newest write in the folder *around* dir: the parent's own files plus every
  // sibling. For node_modules that is "when was this project last worked on".
  public static string ProjectFreshOf(string dir) {
    var key = Key(dir);
    var parent = ParentOf(key);
    if (parent == null) return null;
    var pk = Key(parent);
    long best; FreshOwn.TryGetValue(pk, out best);
    List<string> sibs;
    if (Kids.TryGetValue(pk, out sibs))
      foreach (var c in sibs) {
        if (string.Equals(c, key, StringComparison.OrdinalIgnoreCase)) continue;
        long f;
        if (Fresh.TryGetValue(c, out f) && f > best) best = f;
      }
    return Day(best);
  }

  // Newest write around a project root, ignoring the folders (build output,
  // dependencies) that a build or an install touches without anyone working.
  public static string ProjectFreshExcept(string parent, Func<string, bool> skip) {
    long best; FreshOwn.TryGetValue(Key(parent), out best);
    List<string> kids;
    if (Kids.TryGetValue(Key(parent), out kids))
      foreach (var c in kids) {
        long f;
        if (!skip(c) && Fresh.TryGetValue(c, out f) && f > best) best = f;
      }
    return Day(best);
  }

  public static string FreshOf(string dir) {
    long t;
    if (!Fresh.TryGetValue(Key(dir), out t) || t <= 0) return null;
    return new DateTime(t, DateTimeKind.Utc).ToString("yyyy-MM-dd");
  }

  public static void Walk(string root) { Walk(root, Interlocked.Increment(ref Gen)); }

  static void Walk(string root, int gen) {
    root = root.TrimEnd(SEP) + SEPS;
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
      WalkInto(branch, part.Own, part.Kids, part.Fresh, true, gen);
      parts.Add(part);
    });
    if (gen != Gen) return;                       // superseded: publish nothing, leave Running to the new scan
    foreach (var p in parts) {
      foreach (var kv in p.Own) own[kv.Key] = kv.Value;
      foreach (var kv in p.Kids) kids[kv.Key] = kv.Value;
      foreach (var kv in p.Fresh) fresh[kv.Key] = kv.Value;
    }

    Dictionary<string, long> rolled;
    var total = Rollup(own, kids, fresh, out rolled);
    Own = own; Kids = kids; Total = total; FreshOwn = fresh; Fresh = rolled;
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
        var fresh = FreshOwn;
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
    root = root.TrimEnd(SEP) + SEPS;
    Interlocked.Increment(ref Gen);               // a saved index replaces whatever walk is in flight
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
          else if (kids.TryGetValue(p + SEPS, out list)) list.Add(k);
        }
        Dictionary<string, long> rolled;
        Own = own; Kids = kids; FreshOwn = fresh; Total = Rollup(own, kids, fresh, out rolled); Fresh = rolled;
        Root = root; FileCount = files; DirCount = dirs; IndexedAtUtc = when;
        Done = true; HasIndex = true; Running = false;
        return true;
      }
    } catch { return false; }
  }

  // ----------------------------------------------------------- patching ---
  static string ParentOf(string dir) {
    try {
      var p = Path.GetDirectoryName(dir.TrimEnd(SEP));
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
    foreach (var d in SubtreeOf(key)) { Own.Remove(d); Kids.Remove(d); Total.Remove(d); Fresh.Remove(d); FreshOwn.Remove(d); }
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
    var target = key.TrimEnd(SEP);
    if (target.Length == 2 || target.Length == 0) target = key;   // a root keeps its slash ("C:\" or "/")
    if (!Directory.Exists(target)) { Save(); return -1; }

    var own = NewMap(); var kids = NewKids(); var fresh = NewMap();
    WalkInto(target, own, kids, fresh, false);
    foreach (var kv in own) Own[kv.Key] = kv.Value;
    foreach (var kv in kids) Kids[kv.Key] = kv.Value;
    foreach (var kv in fresh) FreshOwn[kv.Key] = kv.Value;

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

  // Re-read one folder's own files only, leaving its subfolders' totals alone.
  // Used by the live journal: a file changed in dir, so only dir needs reading.
  // A subfolder that appeared or vanished is the one case that goes deeper.
  public static void RefreshDir(string dir) {
    var key = Key(dir);
    var target = key.TrimEnd(SEP);
    if (target.Length == 2 || target.Length == 0) target = key;
    if (!Own.ContainsKey(key)) { if (Directory.Exists(target)) RefreshSubtree(target); return; }
    if (!Directory.Exists(target)) { Forget(key); return; }

    var mine = new List<string>(); long newest;
    long own = ScanDir(target, mine, false, out newest);
    long delta = own - Own[key];
    Own[key] = own; FreshOwn[key] = newest;
    Total[key] = Math.Max(0, Total[key] + delta);
    long f;
    if (!Fresh.TryGetValue(key, out f) || newest > f) Fresh[key] = newest;
    AddToAncestors(key, delta);

    var before = Kids.TryGetValue(key, out var old) ? old.ToList() : new List<string>();
    foreach (var c in before) if (!mine.Any(m => string.Equals(m, c, StringComparison.OrdinalIgnoreCase))) Forget(c);
    foreach (var c in mine) if (!before.Any(b => string.Equals(b, c, StringComparison.OrdinalIgnoreCase))) RefreshSubtree(c);
  }

  // The root is stored with a trailing separator and every other folder without
  // one, while callers pass either form. Resolve to whichever key exists.
  static string Key(string dir) {
    if (dir == null) return "";
    if (Total.ContainsKey(dir) || Kids.ContainsKey(dir)) return dir;
    string trimmed = dir.TrimEnd(SEP);
    if (Total.ContainsKey(trimmed) || Kids.ContainsKey(trimmed)) return trimmed;
    string slashed = trimmed + SEPS;
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

// The biggest files anywhere, with their age. One enumeration of the volume,
// keeping a running top-n, so the cost is the walk and nothing else.
public static class BigScan {
  public static volatile bool Running;
  public static volatile bool Done;
  public static volatile string Current = "";
  public static volatile string Scope = "";
  public static long Seen;
  static string[] _rows = new string[0];
  const int KEEP = 300;

  public static string[] Rows() { return _rows; }

  public static void Start(string root, long minBytes) {
    if (Running) return;
    root = DupeScan.NormalizeRoot(root);
    Running = true; Done = false; Scope = root;
    var t = new Thread(() => Run(root, minBytes));
    t.IsBackground = true;
    t.Start();
  }

  static void Run(string root, long minBytes) {
    Seen = 0;
    var opts = new EnumerationOptions {
      RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
    };
    var best = new List<FileInfo>(KEEP + 1);
    long floor = minBytes;
    try {
      foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts)) {
        Seen++;
        if ((Seen & 4095) == 0) Current = f.DirectoryName ?? "";
        long len;
        try { len = f.Length; } catch { continue; }
        if (len < floor) continue;
        best.Add(f);
        if (best.Count > KEEP * 2) {
          best.Sort((a, b) => b.Length.CompareTo(a.Length));
          best.RemoveRange(KEEP, best.Count - KEEP);
          floor = Math.Max(minBytes, best[KEEP - 1].Length);
        }
      }
    } catch {}
    best.Sort((a, b) => b.Length.CompareTo(a.Length));
    _rows = best.Take(KEEP).Select(f => {
      string date; try { date = f.LastWriteTimeUtc.ToString("yyyy-MM-dd"); } catch { date = ""; }
      return f.Length + "|" + date + "|" + f.FullName;
    }).ToArray();
    Current = ""; Done = true; Running = false;
    try { File.WriteAllLines(Store(Scope), _rows); } catch {}
  }

  static string Store(string root) { return DiskScan.IndexPath(root) + ".big"; }

  public static bool Load(string root) {
    root = DupeScan.NormalizeRoot(root);
    try {
      var p = Store(root);
      if (!File.Exists(p)) return false;
      _rows = File.ReadAllLines(p).Where(l => l.Length > 0).ToArray();
      Scope = root; Done = _rows.Length > 0;
      return Done;
    } catch { return false; }
  }

  public static void Drop(string path) {
    _rows = _rows.Where(r => !r.EndsWith("|" + path, StringComparison.OrdinalIgnoreCase)).ToArray();
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
