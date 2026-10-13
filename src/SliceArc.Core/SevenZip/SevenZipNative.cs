using System.Runtime.InteropServices;

namespace SliceArc.Core.SevenZip;

/// <summary>
/// Raw 7z.dll surface: interface GUIDs, item property ids, VARTYPE values and
/// the four exported entry points.
///
/// Every constant here is transcribed from the 7-Zip 26.04 sources checked into
/// <c>.refs/src</c>, never from memory:
///   CPP/7zip/IDecl.h            - the Z7_DECL_IFACE_7ZIP_SUB GUID layout
///   CPP/7zip/IStream.h          - ISequentialInStream / IInStream
///   CPP/7zip/IProgress.h        - IProgress
///   CPP/7zip/PropID.h           - item property ids
///   CPP/7zip/Archive/IArchive.h - archive interfaces, NAskMode, NOperationResult
/// </summary>
internal static class SevenZipNative
{
    public const string DllName = "7z.dll";

    // ---------------------------------------------------------------------
    // Interface GUIDs.
    //
    // IDecl.h declares every 7-Zip interface as
    //   { 0x23170F69, 0x40C1, 0x278A, 0, 0, 0, groupId, 0, subId, 0, 0 }
    // so the textual form is 23170F69-40C1-278A-0000-00GG00SS0000 with the
    // group and sub ids spliced in. Spot-checked against IInArchive (0x60/6)
    // and IInStream (0x03/3) below.
    // ---------------------------------------------------------------------

    /// <summary>IInStream, group 3 sub 0x03.</summary>
    public static readonly Guid IID_IInStream = new("23170F69-40C1-278A-0000-000300030000");

    /// <summary>ISequentialInStream, group 3 sub 0x01.</summary>
    public static readonly Guid IID_ISequentialInStream = new("23170F69-40C1-278A-0000-000300010000");

    /// <summary>ISequentialOutStream, group 3 sub 0x02.</summary>
    public static readonly Guid IID_ISequentialOutStream = new("23170F69-40C1-278A-0000-000300020000");

    /// <summary>IProgress, group 0 sub 5.</summary>
    public static readonly Guid IID_IProgress = new("23170F69-40C1-278A-0000-000000050000");

    /// <summary>IArchiveOpenCallback, group 6 sub 0x10.</summary>
    public static readonly Guid IID_IArchiveOpenCallback = new("23170F69-40C1-278A-0000-000600100000");

    /// <summary>IArchiveExtractCallback, group 6 sub 0x20.</summary>
    public static readonly Guid IID_IArchiveExtractCallback = new("23170F69-40C1-278A-0000-000600200000");

    /// <summary>IArchiveOpenVolumeCallback, group 6 sub 0x30.</summary>
    public static readonly Guid IID_IArchiveOpenVolumeCallback = new("23170F69-40C1-278A-0000-000600300000");

    /// <summary>IInArchive, group 6 sub 0x60.</summary>
    public static readonly Guid IID_IInArchive = new("23170F69-40C1-278A-0000-000600600000");

    /// <summary>IOutArchive, group 6 sub 0xA0. QI'd off the same handler object as IInArchive.</summary>
    public static readonly Guid IID_IOutArchive = new("23170F69-40C1-278A-0000-000600A00000");

    /// <summary>IArchiveUpdateCallback, group 6 sub 0x80. Our callback the handler drives during UpdateItems.</summary>
    public static readonly Guid IID_IArchiveUpdateCallback = new("23170F69-40C1-278A-0000-000600800000");

    /// <summary>IArchiveUpdateCallback2, group 6 sub 0x82. Adds GetVolumeSize / GetVolumeStream for split archives.</summary>
    public static readonly Guid IID_IArchiveUpdateCallback2 = new("23170F69-40C1-278A-0000-000600820000");

    /// <summary>ISetProperties, group 6 sub 0x03. QI'd off IOutArchive to configure level/method/password/volumes.</summary>
    public static readonly Guid IID_ISetProperties = new("23170F69-40C1-278A-0000-000600030000");

    /// <summary>IInArchiveGetStream, group 6 sub 0x40.</summary>
    public static readonly Guid IID_IInArchiveGetStream = new("23170F69-40C1-278A-0000-000600400000");

    // ---------------------------------------------------------------------
    // Exported entry points (7z.dll, all STDAPI == __stdcall).
    // ---------------------------------------------------------------------

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int CreateObject(in Guid clsid, in Guid iid, out IntPtr outObject);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int GetNumberOfFormats(out uint numFormats);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int GetHandlerProperty2(uint formatIndex, uint propId, ref PropVariant value);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int SetLargePageMode();

    // ---------------------------------------------------------------------
    // NArchive::NHandlerPropID - property ids accepted by GetHandlerProperty2.
    // These are a *different* numbering from item PROPIDs; do not mix them up.
    // ---------------------------------------------------------------------

    internal static class HandlerProp
    {
        public const uint Name = 0;
        public const uint ClassId = 1;      // CLSID, delivered as VT_BSTR of raw bytes
        public const uint Extension = 2;
        public const uint AddExtension = 3;
        public const uint Update = 4;
        public const uint KeepName = 5;
        public const uint Signature = 6;
        public const uint MultiSignature = 7;
        public const uint SignatureOffset = 8;
        public const uint AltStreams = 9;
        public const uint NtSecure = 10;
        public const uint Flags = 11;
        public const uint TimeFlags = 12;
    }

    // ---------------------------------------------------------------------
    // Item PROPIDs, in enum order from PropID.h. The numeric values matter.
    // ---------------------------------------------------------------------

    internal static class PropId
    {
        public const uint NoProperty = 0;
        public const uint MainSubfile = 1;
        public const uint HandlerItemIndex = 2;
        public const uint Path = 3;
        public const uint Name = 4;
        public const uint Extension = 5;
        public const uint IsDir = 6;
        public const uint Size = 7;
        public const uint PackSize = 8;
        public const uint Attrib = 9;
        public const uint CTime = 10;
        public const uint ATime = 11;
        public const uint MTime = 12;
        public const uint Solid = 13;
        public const uint Commented = 14;
        public const uint Encrypted = 15;
        public const uint SplitBefore = 16;
        public const uint SplitAfter = 17;
        public const uint DictionarySize = 18;
        public const uint CRC = 19;
        public const uint Type = 20;
        public const uint IsAnti = 21;
        public const uint Method = 22;
        public const uint HostOS = 23;
        public const uint FileSystem = 24;
        public const uint Position = 29;
        public const uint Volume = 34;
        public const uint IsVolume = 35;
        public const uint Offset = 36;
        public const uint NumBlocks = 38;
        public const uint NumVolumes = 39;
        public const uint PhySize = 44;
        public const uint HeadersSize = 45;
        public const uint Checksum = 46;
        public const uint Error = 55;
        public const uint TotalSize = 56;
        public const uint FreeSpace = 57;
        public const uint VolumeName = 59;
        public const uint ErrorType = 69;
        public const uint NumErrors = 70;
        public const uint ErrorFlags = 71;
        public const uint WarningFlags = 72;
        public const uint VirtualSize = 77;
        public const uint UnpackSize = 78;
        public const uint TotalPhySize = 79;
        public const uint VolumeIndex = 80;
    }

    /// <summary>VARTYPE values used by the properties we actually read.</summary>
    internal static class VarType
    {
        public const ushort Empty = 0;
        public const ushort Null = 1;
        public const ushort I4 = 3;
        public const ushort Bstr = 8;
        public const ushort Bool = 11;
        public const ushort UI4 = 19;
        public const ushort I8 = 20;
        public const ushort UI8 = 21;
        public const ushort LPWStr = 31;
        public const ushort FileTime = 64;
    }

    /// <summary>NExtract::NAskMode.</summary>
    internal static class AskMode
    {
        public const int Extract = 0;
        public const int Test = 1;
        public const int Skip = 2;
        public const int ReadExternal = 3;
    }

    /// <summary>NExtract::NOperationResult.</summary>
    internal static class OperationResult
    {
        public const int Ok = 0;
        public const int UnsupportedMethod = 1;
        public const int DataError = 2;
        public const int CrcError = 3;
        public const int Unavailable = 4;
        public const int UnexpectedEnd = 5;
        public const int DataAfterEnd = 6;
        public const int IsNotArc = 7;
        public const int HeadersError = 8;
        public const int WrongPassword = 9;
        public const int UnsupportedFeature = 10;
        public const int MemError = 11;

        /// <summary>
        /// Short, user-facing label. This is what shows up next to a file name in
        /// the result list, so it has to read as Chinese rather than as the raw
        /// engine enum that 7z.dll hands back.
        /// </summary>
        public static string Describe(int opRes) => opRes switch
        {
            Ok => "正常",
            UnsupportedMethod => "不支持的压缩算法",
            DataError => "数据损坏",
            CrcError => "CRC 校验失败（数据损坏）",
            Unavailable => "无法访问",
            UnexpectedEnd => "数据意外中断（压缩包不完整）",
            DataAfterEnd => "压缩包尾部有多余数据",
            IsNotArc => "不是有效的压缩包",
            HeadersError => "压缩包头损坏或无法解密",
            WrongPassword => "压缩包已加密，需要口令",
            UnsupportedFeature => "不支持的压缩包特性",
            MemError => "内存不足",
            _ => $"未知错误（代码 {opRes}）",
        };

        /// <summary>
        /// What the user can actually do about it. Paired with
        /// <see cref="Describe"/> so a failure never just states the symptom.
        /// </summary>
        public static string Hint(int opRes) => opRes switch
        {
            UnsupportedMethod or UnsupportedFeature =>
                "该压缩包使用了本程序尚不支持的算法，请换用其他解压软件。",
            DataError or CrcError =>
                "数据已损坏。若这是分卷或分片下载的文件，请重新下载缺失或不完整的分卷后重试。",
            UnexpectedEnd =>
                "压缩包不完整，通常是分卷缺失或下载中断。请确认所有分卷齐全后重试。",
            IsNotArc or HeadersError =>
                "该文件不是有效的压缩包，或文件头已损坏；加密压缩包也可能是口令不对。",
            WrongPassword =>
                "该压缩包已加密，需要正确的口令。请重新输入口令后重试。",
            Unavailable =>
                "文件正被其他程序占用，或没有访问权限。请关闭占用它的程序后重试。",
            MemError =>
                "可用内存不足。请关闭其他程序后重试，或分次解压。",
            _ => "请确认压缩包完整、未被占用，然后重试。",
        };

        /// <summary>
        /// True for the results that mean "this archive needs a password".
        /// HeadersError is deliberately excluded: a corrupt header would then
        /// nag the user for a password that cannot possibly help.
        /// </summary>
        public static bool ImpliesPassword(int opRes) => opRes == WrongPassword;
    }

    /// <summary>NUpdateNotifyOp - passed to IArchiveUpdateCallbackFile::ReportOperation. Not used directly here
    /// (we don't implement that optional interface) but documented for completeness.</summary>
    internal static class UpdateNotifyOp
    {
        public const int Add = 0;
        public const int Update = 1;
        public const int Analyze = 2;
        public const int Replicate = 3;
        public const int Repack = 4;
        public const int Skip = 5;
        public const int Delete = 6;
        public const int Header = 7;
        public const int HashRead = 8;
        public const int InFileChanged = 9;
    }

    /// <summary>Common HRESULTs we return from our own COM implementations.</summary>
    internal static class HResult
    {
        public const int S_OK = 0;
        public const int S_FALSE = 1;
        public const int E_ABORT = unchecked((int)0x80004004);
        public const int E_FAIL = unchecked((int)0x80004005);
        public const int E_NOTIMPL = unchecked((int)0x80004001);
        public const int E_INVALIDARG = unchecked((int)0x80070057);
        public const int E_POINTER = unchecked((int)0x80004003);
        public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
        public const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);
        /// <summary>HRESULT_FROM_WIN32(ERROR_NEGATIVE_SEEK) - what 7-Zip wants on a bad Seek.</summary>
        public const int E_NEGATIVE_SEEK = unchecked((int)0x80070083);
    }

    /// <summary>STREAM_SEEK_* origins accepted by IInStream::Seek.</summary>
    internal static class SeekOrigin7z
    {
        public const uint Begin = 0;
        public const uint Current = 1;
        public const uint End = 2;
    }
}

/// <summary>
/// PROPVARIANT — the COM property bag struct 7z.dll uses for every item
/// attribute and handler configuration knob. 7z.dll allocates this as a
/// 16-byte struct on 64-bit (8 header + 8 union), but we size the managed
/// proxy at 24 bytes for safe P/Invoke stack headroom; the extra 8 bytes are
/// never read by 7z.dll and protect against uninitialised-stack reads.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct PropVariant
{
    [FieldOffset(0)] public ushort VarType;
    [FieldOffset(2)] public ushort Reserved1;
    [FieldOffset(4)] public ushort Reserved2;
    [FieldOffset(6)] public ushort Reserved3;

    [FieldOffset(8)] public byte ByteValue;
    [FieldOffset(8)] public short ShortValue;
    [FieldOffset(8)] public int IntValue;
    [FieldOffset(8)] public uint UIntValue;
    [FieldOffset(8)] public long LongValue;
    [FieldOffset(8)] public ulong ULongValue;
    [FieldOffset(8)] public long FileTime;
    [FieldOffset(8)] public IntPtr PointerValue;

    public static PropVariant Create() => new() { VarType = SevenZipNative.VarType.Empty };

    public readonly ulong AsUInt64() => VarType switch
    {
        SevenZipNative.VarType.UI4 => UIntValue,
        SevenZipNative.VarType.UI8 => ULongValue,
        SevenZipNative.VarType.I4 => (ulong)(uint)IntValue,
        SevenZipNative.VarType.I8 => (ulong)LongValue,
        SevenZipNative.VarType.Bool => ShortValue != 0 ? 1UL : 0UL,
        _ => 0UL,
    };

    public readonly long AsInt64() => VarType switch
    {
        SevenZipNative.VarType.UI4 => UIntValue,
        SevenZipNative.VarType.UI8 => unchecked((long)ULongValue),
        SevenZipNative.VarType.I4 => IntValue,
        SevenZipNative.VarType.I8 => LongValue,
        _ => 0L,
    };

    public readonly bool AsBool() => VarType switch
    {
        SevenZipNative.VarType.Bool => ShortValue != 0,
        SevenZipNative.VarType.UI4 => UIntValue != 0,
        SevenZipNative.VarType.I4 => IntValue != 0,
        _ => false,
    };

    public readonly string? AsString() => VarType switch
    {
        SevenZipNative.VarType.Bstr or SevenZipNative.VarType.LPWStr
            => PointerValue == IntPtr.Zero ? null : Marshal.PtrToStringUni(PointerValue),
        _ => null,
    };

    /// <summary>Free any allocation 7z.dll handed us. Safe to call on VT_EMPTY.</summary>
    public void Clear()
    {
        switch (VarType)
        {
            case SevenZipNative.VarType.Bstr:
                if (PointerValue != IntPtr.Zero) Marshal.FreeBSTR(PointerValue);
                break;
            case SevenZipNative.VarType.LPWStr:
                if (PointerValue != IntPtr.Zero) Marshal.FreeCoTaskMem(PointerValue);
                break;
        }
        VarType = SevenZipNative.VarType.Empty;
        PointerValue = IntPtr.Zero;
    }
}
