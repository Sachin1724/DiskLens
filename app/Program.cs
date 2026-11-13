// DiskLens - a light, local disk analyser and cleaner for Windows and macOS.
// One window, no server, no browser: the page in ui.html runs inside a native
// webview and talks to Api.Handle() over the webview's message channel.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Photino.NET;

namespace DiskLens {

static class Program {
  [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

  [STAThread]
  static int Main(string[] args) {
    string drive = null;
    for (int i = 0; i < args.Length; i++) {
      if (args[i] == "--selftest") {
        if (Api.Win) AttachConsole(-1);   // a WinExe has no console of its own
        try { Api.SelfTest(); Console.WriteLine("\nself-test OK"); return 0; }
        catch (Exception e) { Console.WriteLine("\n" + e.Message); return 1; }
      }
      if (args[i] == "--drive" && i + 1 < args.Length) drive = args[++i];
    }

    var api = new Api();
    api.Boot(drive);

    var here = AppContext.BaseDirectory;
    var page = Path.Combine(here, "wwwroot", "ui.html");
    if (!File.Exists(page)) page = Path.Combine(here, "ui.html");

    var win = new PhotinoWindow()
      .SetTitle("DiskLens")
      .SetUseOsDefaultSize(false)
      .SetSize(1380, 900)
      .Center()
      .SetResizable(true);
    var ico = Path.Combine(here, "disklens.ico");
    if (Api.Win && File.Exists(ico)) win.SetIconFile(ico);

    // The page sends {id, path, body}; we answer {id, data}. Work runs off the
    // UI thread so a slow preview never freezes the window, and the reply is
    // marshalled back onto it because the webview wants it there.
    win.RegisterWebMessageReceivedHandler((sender, msg) => {
      var w = (PhotinoWindow)sender;
      Task.Run(() => {
        long id = 0; string reply;
        try {
          using var doc = JsonDocument.Parse(msg);
          id = doc.RootElement.GetProperty("id").GetInt64();
          var path = doc.RootElement.GetProperty("path").GetString() ?? "";
          string body = doc.RootElement.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
          var (route, q) = Split(path);
          reply = api.Handle(route, q, body);
        } catch (Exception e) {
          reply = JsonSerializer.Serialize(new { error = e.Message });
        }
        var envelope = "{\"id\":" + id + ",\"data\":" + reply + "}";
        w.Invoke(() => w.SendWebMessage(envelope));
      });
    });

    win.Load(page);
    win.WaitForClose();
    return 0;
  }

  static (string route, Dictionary<string, string> q) Split(string path) {
    var q = new Dictionary<string, string>();
    int i = path.IndexOf('?');
    if (i < 0) return (path, q);
    foreach (var kv in path.Substring(i + 1).Split('&', StringSplitOptions.RemoveEmptyEntries)) {
      int eq = kv.IndexOf('=');
      var k = Uri.UnescapeDataString(eq < 0 ? kv : kv.Substring(0, eq));
      var v = eq < 0 ? "" : Uri.UnescapeDataString(kv.Substring(eq + 1).Replace('+', ' '));
      q[k] = v;
    }
    return (path.Substring(0, i), q);
  }
}
}
