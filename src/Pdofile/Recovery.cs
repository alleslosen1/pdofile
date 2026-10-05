using System.Security.Cryptography;
using System.Text;
using Pdofile.Ink;
using Pdofile.Pdf;

namespace Pdofile;

/// <summary>
/// Crash-recovery journal: unsaved ink is written to %LOCALAPPDATA%\Pdofile\Recovery a few
/// seconds after each change, and offered back the next time the same PDF is opened.
/// </summary>
internal static class Recovery
{
    static readonly byte[] Magic = "INKR1"u8.ToArray();

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pdofile", "Recovery");

    static string FileFor(string pdfPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(pdfPath).ToLowerInvariant()));
        return Path.Combine(Dir, Convert.ToHexString(hash)[..24] + ".ink");
    }

    /// <summary>Serialises on the calling (UI) thread; strokes are immutable so this is cheap and safe.</summary>
    public static byte[] Snapshot(InkStore store)
    {
        var (strokes, _) = store.SnapshotForSave();
        var o = new List<byte>(Magic);
        InkCodec.WriteVar(o, (uint)strokes.Count);
        foreach (var (page, list) in strokes)
        {
            InkCodec.WriteVar(o, (uint)page);
            InkCodec.WriteVar(o, (uint)list.Count);
            foreach (var s in list.OrderBy(s => s.Seq)) InkCodec.Write(o, s);
        }
        return o.ToArray();
    }

    public static void Write(string pdfPath, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            FileUtil.WriteAtomic(FileFor(pdfPath), data);
        }
        catch
        {
            // Recovery is best effort.
        }
    }

    public static Dictionary<int, List<Stroke>>? TryRead(string pdfPath, int pageCount)
    {
        try
        {
            string f = FileFor(pdfPath);
            if (!File.Exists(f)) return null;
            var d = File.ReadAllBytes(f);
            if (d.Length < Magic.Length || !d.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
            int pos = Magic.Length;
            int pages = (int)InkCodec.ReadVar(d, ref pos);
            var result = new Dictionary<int, List<Stroke>>();
            for (int i = 0; i < pages; i++)
            {
                int page = (int)InkCodec.ReadVar(d, ref pos);
                int count = (int)InkCodec.ReadVar(d, ref pos);
                var list = new List<Stroke>(count);
                for (int k = 0; k < count; k++) list.Add(InkCodec.Read(d, ref pos, page));
                if (page < pageCount) result[page] = list;
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(string pdfPath)
    {
        try { File.Delete(FileFor(pdfPath)); } catch { }
    }
}
