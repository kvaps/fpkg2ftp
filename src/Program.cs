// fpkg2ftp: unpack a PS5 fPKG straight onto a console over FTP.
//
// The package's inner image is read block by block (InnerImage, from PS5 UltraPack's
// ffpfsc-pkg-tool) and every file is written into an FTP upload as it is decoded, so the
// game never lands on the local disk. Only the sce_sys metadata, which the package keeps
// in its CNT entry table outside the inner image, goes through a small temporary folder.
//
// GPL-3.0-or-later, as LibProsperoPkg and InnerImage.cs it is built from.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentFTP;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PKG;
using PkgTool;

namespace Fpkg2Ftp;

static class Program
{
    sealed class Options
    {
        public string Package = "";
        public string Host = "";
        public int Port = 2121;
        public string User = "anonymous";
        public string Password = "";
        public string? Dest;
        public string Root = "/data/homebrew";
        public string Passcode = new('0', 32);
        public bool Overwrite;
        public bool List;
    }

    sealed record Item(string Path, long Size, PfsReader.File? Inner, string? Local);

    const string Usage = """
        usage: fpkg2ftp <game.pkg> --host <ps5 ip> [options]

          --host IP          the console's address (not needed with --list)
          --port N           FTP port (default 2121)
          --user NAME        FTP user (default anonymous)
          --password PASS    FTP password (default empty)
          --dest PATH        target folder (default <root>/<title id from param.json>)
          --root PATH        parent folder for the default target (default /data/homebrew)
          --passcode HEX32   package passcode (default 32 zeros)
          --overwrite        upload every file again, even one already there at the right size
          --list             only list what the package holds, upload nothing
        """;

    static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        Options o;
        try { o = Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var tmp = Path.Combine(Path.GetTempPath(), "fpkg2ftp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var inner = new InnerImage(o.Package, o.Passcode);
            var items = InnerFiles(inner).ToList();
            var sceSys = CntSceSys(o.Package, o.Passcode, tmp);
            // The CNT copy of a sce_sys file wins over one in the inner image, as in UltraPack.
            items.RemoveAll(i => sceSys.ContainsKey(i.Path));
            items.AddRange(sceSys.Select(kv => new Item(kv.Key, new FileInfo(kv.Value).Length, null, kv.Value)));
            items.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            long total = items.Sum(i => i.Size);

            if (o.List)
            {
                foreach (var i in items) Console.WriteLine($"{i.Size,15:N0}  {i.Path}");
                Console.WriteLine($"{items.Count} files, {total / 1e9:F2} GB");
                return 0;
            }

            var dest = (o.Dest ?? $"{o.Root.TrimEnd('/')}/{TitleId(sceSys)}").TrimEnd('/');
            Console.WriteLine($"{o.Package}: {items.Count} files, {total / 1e9:F2} GB -> ftp://{o.Host}:{o.Port}{dest}");
            Upload(o, inner, items, dest, total);
            Console.WriteLine("done");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                      or FluentFTP.Exceptions.FtpException or System.Net.Sockets.SocketException
                                      or TimeoutException or ArgumentException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    static Options Parse(string[] args)
    {
        var o = new Options();
        string Next(ref int i) => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host": o.Host = Next(ref i); break;
                case "--port": o.Port = int.Parse(Next(ref i)); break;
                case "--user": o.User = Next(ref i); break;
                case "--password": o.Password = Next(ref i); break;
                case "--dest": o.Dest = Next(ref i); break;
                case "--root": o.Root = Next(ref i); break;
                case "--passcode": o.Passcode = Next(ref i); break;
                case "--overwrite": o.Overwrite = true; break;
                case "--list": o.List = true; break;
                default:
                    if (args[i].StartsWith('-') || o.Package != "") throw new ArgumentException($"unexpected argument {args[i]}");
                    o.Package = args[i];
                    break;
            }
        }
        if (o.Package == "") throw new ArgumentException("no package given");
        if (!File.Exists(o.Package)) throw new ArgumentException($"{o.Package} does not exist");
        if (!o.List && o.Host == "") throw new ArgumentException("--host is required");
        if (o.Passcode.Length != 32) throw new ArgumentException("--passcode must be 32 characters");
        return o;
    }

    /// <summary>Every file of the inner image, with its path relative to /app0.</summary>
    static IEnumerable<Item> InnerFiles(InnerImage inner)
    {
        var stack = new Stack<PfsReader.Dir>();
        stack.Push(inner.Pfs.GetURoot());
        while (stack.Count > 0)
        {
            foreach (var node in stack.Pop().children)
            {
                if (node is PfsReader.Dir dir) stack.Push(dir);
                else if (node is PfsReader.File file) yield return new Item(inner.RelativePath(file), file.size, file, null);
            }
        }
    }

    /// <summary>sce_sys/&lt;name&gt; -> local temp file for the sce_sys entries of the CNT table
    /// (param.json, icons, PlayGo, ...); the package's own bookkeeping entries are skipped.</summary>
    static Dictionary<string, string> CntSceSys(string pkg, string passcode, string tmp)
    {
        Directory.CreateDirectory(tmp);
        // The library refuses an extraction path through a symbolic link, and macOS's temp
        // folder is one (/var -> /private/var).
        tmp = RealPath(tmp);
        ProsperoPackageArchive.ExtractCntEntries(pkg, tmp, passcode, includeEncrypted: true);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(tmp, file).Replace('\\', '/');
            if (IsSceSysName(rel)) result["sce_sys/" + rel] = file;
        }
        return result;
    }

    /// <summary>A named file of /app0/sce_sys, not a dot entry or an unnamed "entry-0000040a.bin"
    /// id (same rule as UltraPack's ffpfsc-pkg-tool).</summary>
    static bool IsSceSysName(string rel)
    {
        if (rel.Length == 0 || rel.Split('/').Any(s => s.Length == 0 || s[0] == '.' || s == "..")) return false;
        var leaf = Path.GetFileName(rel);
        if (!leaf.Contains('.')) return false;
        var stem = Path.GetFileNameWithoutExtension(leaf);
        if (stem.StartsWith("entry-", StringComparison.OrdinalIgnoreCase)) stem = stem[6..];
        else if (stem.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) stem = stem[2..];
        else return true;
        return !(stem.Length > 0 && stem.All(Uri.IsHexDigit));
    }

    /// <summary>The title id from sce_sys/param.json: titleId, or the PPSAxxxxx part of contentId.</summary>
    static string TitleId(Dictionary<string, string> sceSys)
    {
        if (sceSys.TryGetValue("sce_sys/param.json", out var path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("titleId", out var t) && t.GetString() is { Length: > 0 } title) return title;
            if (root.TryGetProperty("contentId", out var c) && c.GetString() is { Length: >= 16 } content) return content[7..16];
        }
        throw new InvalidDataException("the package has no sce_sys/param.json with a title id; give --dest");
    }

    static void Upload(Options o, InnerImage inner, List<Item> items, string dest, long total)
    {
        using var ftp = new FtpClient(o.Host, o.User, o.Password, o.Port);
        ftp.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        ftp.Config.ConnectTimeout = 15000;
        ftp.Config.ReadTimeout = 60000;
        ftp.Config.DataConnectionReadTimeout = 60000;
        ftp.Connect();

        var made = new HashSet<string>(StringComparer.Ordinal);
        void EnsureDir(string dir)
        {
            if (dir.Length == 0 || made.Contains(dir)) return;
            if (!ftp.DirectoryExists(dir)) ftp.CreateDirectory(dir, force: true);
            made.Add(dir);
        }

        var clock = Stopwatch.StartNew();
        long done = 0, sent = 0;
        int skipped = 0;
        var lastReport = TimeSpan.Zero;
        void Report(string path, bool force = false)
        {
            if (!force && clock.Elapsed - lastReport < TimeSpan.FromSeconds(1)) return;
            lastReport = clock.Elapsed;
            double mbps = sent / 1e6 / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
            double pct = total > 0 ? done * 100.0 / total : 100;
            Console.Write($"\r{pct,5:F1}%  {done / 1e9,7:F2}/{total / 1e9:F2} GB  {mbps,6:F1} MB/s  {Shorten(path),-50}");
        }

        foreach (var item in items)
        {
            var remote = $"{dest}/{item.Path}";
            EnsureDir(remote[..remote.LastIndexOf('/')]);
            if (!o.Overwrite && ftp.GetFileSize(remote) == item.Size)
            {
                done += item.Size;
                skipped++;
                Report(item.Path);
                continue;
            }
            using (var upload = ftp.OpenWrite(remote, FtpDataType.Binary, checkIfFileExists: false))
            {
                if (item.Inner != null)
                {
                    inner.CopyFile(item.Inner, upload, n => { done += n; sent += n; Report(item.Path); });
                }
                else
                {
                    using var local = File.OpenRead(item.Local!);
                    local.CopyTo(upload);
                    done += item.Size;
                    sent += item.Size;
                }
            }
            var reply = ftp.GetReply();
            if (!reply.Success) throw new IOException($"upload of {remote} failed: {reply.Code} {reply.Message}");
            if (ftp.GetFileSize(remote) != item.Size)
                throw new IOException($"{remote} has the wrong size on the console after the upload");
            Report(item.Path);
        }
        Report("", force: true);
        Console.WriteLine();
        if (skipped > 0) Console.WriteLine($"{skipped} file(s) were already there and were skipped");
    }

    static string RealPath(string path)
    {
        if (OperatingSystem.IsWindows()) return Path.GetFullPath(path);
        var ptr = realpath(Path.GetFullPath(path), IntPtr.Zero);
        if (ptr == IntPtr.Zero) return Path.GetFullPath(path);
        try { return Marshal.PtrToStringUTF8(ptr) ?? path; } finally { free(ptr); }
    }
    [DllImport("libc")] static extern IntPtr realpath(string path, IntPtr resolved);
    [DllImport("libc")] static extern void free(IntPtr p);

    static string Shorten(string path) => path.Length <= 50 ? path : "..." + path[^47..];
}
