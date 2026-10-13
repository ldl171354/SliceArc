using System.Reflection;
using System.Runtime.InteropServices;

namespace SliceArc.Core.SevenZip;

/// <summary>One archive format handler exposed by 7z.dll.</summary>
public sealed record ArchiveFormat(uint Index, string Name, Guid Clsid, string[] Extensions)
{
    public override string ToString() =>
        $"{Name} [{string.Join(", ", Extensions)}]";
}

/// <summary>
/// Locates 7z.dll, enumerates its format handlers, and instantiates IInArchive.
///
/// Deliberately no hard-coded CLSIDs: every handler is discovered through
/// GetHandlerProperty2(kClassID), which returns the GUID as a 16-byte VT_BSTR.
/// That way a 7-Zip upgrade can add or renumber formats without breaking us.
/// </summary>
internal static class SevenZipLibrary
{
    private const string DllName = "7z.dll";

    private static readonly object Gate = new();
    private static bool _resolverInstalled;
    private static IReadOnlyList<ArchiveFormat>? _formats;

    /// <summary>Path 7z.dll was actually loaded from, once resolved.</summary>
    public static string? LoadedFrom { get; private set; }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, DllName);

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf)) yield return Path.Combine(pf, "7-Zip", DllName);

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf86)) yield return Path.Combine(pf86, "7-Zip", DllName);
    }

    public static void EnsureLoaded()
    {
        if (_resolverInstalled) return;
        lock (Gate)
        {
            if (_resolverInstalled) return;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(SevenZipLibrary).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // Another code path already installed a resolver; that is fine.
            }
            _resolverInstalled = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(DllName, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;

        foreach (var path in CandidatePaths())
        {
            if (!File.Exists(path)) continue;
            if (NativeLibrary.TryLoad(path, out var handle))
            {
                LoadedFrom = path;
                return handle;
            }
        }

        // Single-file self-contained publish: 7z.dll is bundled inside the EXE
        // and extracted to a temp dir at startup. The default native probing
        // path includes that temp dir, so TryLoad by name will find it there.
        if (NativeLibrary.TryLoad(DllName, out var bundled))
        {
            LoadedFrom = "<bundled>";
            return bundled;
        }

        return IntPtr.Zero;
    }

    /// <summary>Every format handler 7z.dll knows about.</summary>
    public static IReadOnlyList<ArchiveFormat> Formats
    {
        get
        {
            EnsureLoaded();
            lock (Gate) { return _formats ??= EnumerateFormats(); }
        }
    }

    private static List<ArchiveFormat> EnumerateFormats()
    {
        var result = new List<ArchiveFormat>();

        var hr = SevenZipNative.GetNumberOfFormats(out var count);
        if (hr != 0) return result;

        for (uint i = 0; i < count; i++)
        {
            var name = ReadString(i, SevenZipNative.HandlerProp.Name) ?? $"format{i}";
            var clsid = ReadClsid(i);
            if (clsid == Guid.Empty) continue;

            var extRaw = ReadString(i, SevenZipNative.HandlerProp.Extension) ?? string.Empty;
            var exts = extRaw
                .Split(new[] { ' ', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            result.Add(new ArchiveFormat(i, name, clsid, exts));
        }

        return result;
    }

    private static string? ReadString(uint formatIndex, uint propId)
    {
        var pv = PropVariant.Create();
        try
        {
            if (SevenZipNative.GetHandlerProperty2(formatIndex, propId, ref pv) != 0) return null;
            return pv.AsString();
        }
        finally { pv.Clear(); }
    }

    private static Guid ReadClsid(uint formatIndex)
    {
        var pv = PropVariant.Create();
        try
        {
            if (SevenZipNative.GetHandlerProperty2(formatIndex, SevenZipNative.HandlerProp.ClassId, ref pv) != 0)
                return Guid.Empty;

            // IArchive.h: kClassID is "binary GUID in VT_BSTR" - raw 16 bytes,
            // not a formatted string.
            if (pv.VarType != SevenZipNative.VarType.Bstr || pv.PointerValue == IntPtr.Zero)
                return Guid.Empty;

            var bytes = new byte[16];
            Marshal.Copy(pv.PointerValue, bytes, 0, 16);
            return new Guid(bytes);
        }
        finally { pv.Clear(); }
    }

    /// <summary>
    /// Best-effort handler lookup for a file name. Volume members keep their real
    /// extension in the middle (archive.7z.001), so callers should pass the
    /// group's base name rather than the physical volume name.
    /// </summary>
    public static ArchiveFormat? FindByExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return null;

        var bare = ext.TrimStart('.').ToLowerInvariant();
        foreach (var f in Formats)
        {
            foreach (var e in f.Extensions)
            {
                if (e.TrimStart('.').Equals(bare, StringComparison.OrdinalIgnoreCase)) return f;
            }
        }
        return null;
    }

    /// <summary>Instantiate a handler by CLSID and cast it to IInArchive.</summary>
    public static IInArchive CreateInArchive(Guid clsid)
    {
        EnsureLoaded();

        var iid = SevenZipNative.IID_IInArchive;
        var hr = SevenZipNative.CreateObject(clsid, iid, out var ptr);
        if (hr != 0 || ptr == IntPtr.Zero)
            throw new InvalidOperationException(
                $"7z.dll CreateObject failed for {clsid}: 0x{hr:X8}");

        try
        {
            return (IInArchive)Marshal.GetObjectForIUnknown(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>
    /// Instantiate a handler by CLSID and cast it to IOutArchive. Same CLSID
    /// space as <see cref="CreateInArchive"/>: the format handler object serves
    /// both IInArchive and IOutArchive from the same CreateObject call, so the
    /// only difference is which interface IID we ask for.
    /// </summary>
    public static IOutArchive CreateOutArchive(Guid clsid)
    {
        EnsureLoaded();

        var iid = SevenZipNative.IID_IOutArchive;
        var hr = SevenZipNative.CreateObject(clsid, iid, out var ptr);
        if (hr != 0 || ptr == IntPtr.Zero)
            throw new InvalidOperationException(
                $"7z.dll CreateObject(IOutArchive) failed for {clsid}: 0x{hr:X8}. " +
                "The handler may not support writing - check HandlerProp.Update first.");

        try
        {
            return (IOutArchive)Marshal.GetObjectForIUnknown(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>True iff the handler reports <c>kUpdate = true</c> in its property bag.</summary>
    public static bool SupportsWrite(ArchiveFormat format)
    {
        var pv = PropVariant.Create();
        try
        {
            if (SevenZipNative.GetHandlerProperty2(format.Index, SevenZipNative.HandlerProp.Update, ref pv) != 0)
                return false;
            return pv.AsBool();
        }
        finally { pv.Clear(); }
    }

    /// <summary>Handler with a write side, by file extension; null if read-only or unknown.</summary>
    public static ArchiveFormat? FindWritableByExtension(string fileName)
    {
        var f = FindByExtension(fileName);
        return f is not null && SupportsWrite(f) ? f : null;
    }

    public static void ReleaseInArchive(IInArchive archive)
    {
        if (archive is not null && Marshal.IsComObject(archive))
            Marshal.ReleaseComObject(archive);
    }

    public static void ReleaseOutArchive(IOutArchive archive)
    {
        if (archive is not null && Marshal.IsComObject(archive))
            Marshal.ReleaseComObject(archive);
    }
}
