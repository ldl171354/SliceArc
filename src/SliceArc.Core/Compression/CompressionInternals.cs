using System.Runtime.InteropServices;
using SliceArc.Core.SevenZip;

namespace SliceArc.Core.Compression;

/// <summary>
/// Walks the caller's sources into a flat item list. Shared by the "create a new
/// archive" pipeline (<see cref="CompressionEngine"/>) and the "add to an
/// existing archive" pipeline (<see cref="ArchiveUpdateEngine"/>) so both name
/// entries identically.
/// </summary>
internal static class CompressionSourceWalker
{
    /// <summary>
    /// Walk every source into a flat list. Each top-level source becomes the
    /// root of its subtree in the archive, named after the source itself
    /// (so compressing "D:\foo" produces "foo/..." inside, not "D:/foo/...").
    /// A source that is a bare file is added by its file name.
    /// </summary>
    public static List<PendingItem> Build(IReadOnlyList<string> sources, List<string> errors)
    {
        var items = new List<PendingItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var src in sources)
        {
            if (string.IsNullOrWhiteSpace(src)) continue;
            var full = Path.GetFullPath(src);
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                errors.Add($"跳过不存在的源：{src}");
                continue;
            }

            var rootName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(rootName)) rootName = "root";

            if (Directory.Exists(full))
            {
                AddDirectory(full, rootName, items, seen);
                // Add the root itself last so its mtime/attrib get stored too.
                var info = new DirectoryInfo(full);
                TryAddItem(new PendingItem
                {
                    ArchivePath = rootName,
                    FullPath = full,
                    IsDirectory = true,
                    Modified = info.LastWriteTime,
                    Attributes = (uint)info.Attributes,
                }, items, seen);
            }
            else
            {
                var info = new FileInfo(full);
                TryAddItem(new PendingItem
                {
                    ArchivePath = rootName,
                    FullPath = full,
                    IsDirectory = false,
                    Size = info.Length,
                    Modified = info.LastWriteTime,
                    Attributes = (uint)info.Attributes,
                }, items, seen);
            }
        }

        return items;
    }

    private static void AddDirectory(string dir, string archivePrefix, List<PendingItem> items, HashSet<string> seen)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            var subPrefix = archivePrefix + "/" + name;
            AddDirectory(sub, subPrefix, items, seen);
            var info = new DirectoryInfo(sub);
            TryAddItem(new PendingItem
            {
                ArchivePath = subPrefix,
                FullPath = sub,
                IsDirectory = true,
                Modified = info.LastWriteTime,
                Attributes = (uint)info.Attributes,
            }, items, seen);
        }

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            var info = new FileInfo(file);
            TryAddItem(new PendingItem
            {
                ArchivePath = archivePrefix + "/" + name,
                FullPath = file,
                IsDirectory = false,
                Size = info.Length,
                Modified = info.LastWriteTime,
                Attributes = (uint)info.Attributes,
            }, items, seen);
        }
    }

    private static void TryAddItem(PendingItem item, List<PendingItem> items, HashSet<string> seen)
    {
        var key = item.ArchivePath.Replace('\\', '/');
        if (!seen.Add(key)) return;     // case-sensitive dedupe; collisions from renames get the first one
        items.Add(item);
    }
}

/// <summary>
/// The property bag a 7z.dll write handler reads before UpdateItems. Shared by
/// the create-new and add-to-existing pipelines so both configure the handler
/// the same way. Names are the short codes 7-Zip's own CLI uses (xCmd.prop
/// names - see CPP/7zip/UI/Common/ArchiveCommandLine.cpp), so anything that
/// works for "7z.exe a" works here too.
/// </summary>
internal static class CompressionHandlerConfig
{
    public static void Apply(
        IOutArchive archive,
        string handlerName,
        int level,
        bool solid,
        string? method,
        int threads,
        bool encryptHeaders,
        string? password,
        List<string> errors)
    {
        var props = new List<(string Name, PropVariant Value)>();
        var is7z = handlerName.Equals("7z", StringComparison.OrdinalIgnoreCase);
        var isZip = handlerName.Equals("zip", StringComparison.OrdinalIgnoreCase);

        // Level "x" — supported by 7z, zip, gzip, bzip2, xz, zstd. Tar/wim ignore it.
        // Use VT_UI4: the ZIP handler checks vt==VT_UI4 and returns E_INVALIDARG for VT_I4.
        if (!handlerName.Equals("tar", StringComparison.OrdinalIgnoreCase) &&
            !handlerName.Equals("wim", StringComparison.OrdinalIgnoreCase))
            props.Add(("x", new PropVariant { VarType = SevenZipNative.VarType.UI4, UIntValue = (uint)level }));
        // Solid "s" — only the 7z handler understands this.
        if (is7z && solid)
            props.Add(("s", new PropVariant { VarType = SevenZipNative.VarType.Bool, ShortValue = (short)-1 }));
        if (!string.IsNullOrWhiteSpace(method))
            props.Add(("m", MethodValue(method)));
        if (threads > 0)
            props.Add(("mt", new PropVariant { VarType = SevenZipNative.VarType.UI4, UIntValue = (uint)threads }));
        // Encrypt headers "he" - 7z only. Turning it on also encrypts the file
        // names, so the archive cannot be listed without the password. Zip has no
        // equivalent, and asking it for "he" makes SetProperties fail.
        if (is7z && encryptHeaders)
            props.Add(("he", new PropVariant { VarType = SevenZipNative.VarType.Bool, ShortValue = (short)-1 }));

        // The password itself never travels as a property - the handler pulls it
        // from the update callback via ICryptoGetTextPassword2 (see
        // CryptoGetTextPassword2 below). We only validate it here, because the
        // zip handler rejects a non-ASCII password with E_INVALIDARG and the user
        // deserves to be told that in words.
        if (!string.IsNullOrEmpty(password))
        {
            if (is7z)
            {
                // 7z always encrypts with AES-256 once a password is defined.
            }
            else if (isZip)
            {
                if (VerifyZipPassword(password) is { } problem)
                {
                    errors.Add(problem);
                }
                else
                {
                    // "em" picks the zip encryption method. ZipCrypto is the
                    // historical default and is cryptographically broken; AES-256 is
                    // what Bandizip and 7-Zip's own UI offer as the safe choice.
                    props.Add(("em", new PropVariant
                    {
                        VarType = SevenZipNative.VarType.Bstr,
                        PointerValue = Marshal.StringToBSTR("AES256"),
                    }));
                }
            }
        }
        // Volume splitting is handled at the output stream level (VolumeOutputSink),
        // not through SetProperties. The 7z handler's SetProperties rejects "v"/"vz".

        if (props.Count == 0) return;

        // ISetProperties takes parallel arrays. Build them on the unmanaged heap
        // so the handler can read them without crossing the CLR marshaler.
        var setProps = TryQueryInterface<ISetProperties>(archive);
        if (setProps is null) return;

        var names = new IntPtr[props.Count];
        var values = new PropVariant[props.Count];
        var bstrs = new List<IntPtr>();   // keep BSTRs alive until the call returns

        for (var i = 0; i < props.Count; i++)
        {
            var (name, value) = props[i];
            names[i] = Marshal.StringToCoTaskMemUni(name);
            if (value.VarType == SevenZipNative.VarType.Bstr && value.PointerValue != IntPtr.Zero)
                bstrs.Add(value.PointerValue);
            values[i] = value;
        }

        try
        {
            var namesPtr = Marshal.AllocCoTaskMem(IntPtr.Size * props.Count);
            var valuesPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<PropVariant>() * props.Count);
            try
            {
                Marshal.Copy(names, 0, namesPtr, props.Count);
                // Marshal the PropVariant[] to unmanaged memory so the handler can read it.
                for (var i = 0; i < props.Count; i++)
                    Marshal.StructureToPtr(values[i], valuesPtr + i * Marshal.SizeOf<PropVariant>(), fDeleteOld: false);

                var hr = setProps.SetProperties(namesPtr, valuesPtr, (uint)props.Count);
                if (hr != SevenZipNative.HResult.S_OK)
                    errors.Add($"压缩参数未被接受（错误码 0x{hr:X8}），部分选项可能未生效。");
            }
            finally
            {
                Marshal.FreeCoTaskMem(namesPtr);
                Marshal.FreeCoTaskMem(valuesPtr);
            }
        }
        finally
        {
            foreach (var n in names) Marshal.FreeCoTaskMem(n);
            foreach (var b in bstrs) Marshal.FreeBSTR(b);
        }
    }

    private static PropVariant MethodValue(string method)
    {
        // 7z.dll accepts the method as a BSTR like "LZMA", "deflate", "bzip2"...
        return new PropVariant
        {
            VarType = SevenZipNative.VarType.Bstr,
            PointerValue = Marshal.StringToBSTR(method),
        };
    }

    /// <summary>
    /// The zip format stores its password as a byte string in the OEM codepage,
    /// so the handler refuses anything outside plain ASCII with E_INVALIDARG
    /// (<c>IsSimpleAsciiString</c> in ZipHandlerOut.cpp). Catch that here so the
    /// user gets a sentence instead of a bare HRESULT.
    /// </summary>
    private static string? VerifyZipPassword(string password)
    {
        foreach (var c in password)
        {
            if (c < 0x20 || c > 0x7E)
            {
                return "zip 格式的口令只能用 ASCII 字符（字母、数字、常见符号），" +
                       "当前口令含中文或其他特殊字符，请更换口令，或改用 7z 格式。";
            }
        }
        return null;
    }

    private static T? TryQueryInterface<T>(object obj) where T : class
    {
        try
        {
            var ptr = Marshal.GetComInterfaceForObject(obj, typeof(T));
            if (ptr == IntPtr.Zero) return null;
            try { return (T)Marshal.GetObjectForIUnknown(ptr); }
            finally { Marshal.Release(ptr); }
        }
        catch { return null; }
    }
}
