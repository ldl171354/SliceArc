using System.Runtime.InteropServices;

namespace SliceArc.Core.SevenZip;

// =========================================================================
// Interfaces WE implement and 7z.dll calls back into.
//
// These are declared [ComVisible] with InterfaceIsIUnknown so the CLR builds a
// COM Callable Wrapper whose vtable matches 7-Zip's C++ layout exactly. Method
// order is load-bearing: it is the vtable order.
//
//   IInStream   : IUnknown{QI,AddRef,Release} + ISequentialInStream{Read} + {Seek}
//   IOutStream  : ... + ISequentialOutStream{Write}
//   IProgress   : ... + {SetTotal, SetCompleted}
//
// [PreserveSig] everywhere: 7-Zip speaks raw HRESULTs, and we must not let the
// CLR turn a non-zero return into an exception before it crosses the boundary.
// =========================================================================

/// <summary>ISequentialInStream (group 3, sub 0x01). 7z.dll reads from us.</summary>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000300010000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISequentialInStream
{
    /// <param name="data">Destination buffer owned by the caller.</param>
    /// <param name="size">Bytes requested.</param>
    /// <param name="processedSize">Bytes actually produced; partial reads are legal.</param>
    [PreserveSig]
    int Read(IntPtr data, uint size, out uint processedSize);
}

/// <summary>
/// IInStream (group 3, sub 0x03). This is the interface that makes per-volume
/// accounting possible: every byte 7z.dll consumes passes through our Read/Seek,
/// so we always know which physical volume the decoder is sitting on.
/// </summary>
/// <remarks>
/// Read is declared here rather than inherited from ISequentialInStream. In COM
/// the derived vtable is the flattened base-then-derived layout, but relying on
/// the CLR to synthesise that from a managed interface hierarchy leaves room for
/// a slot to land in the wrong place - which manifests as a corrupted CLR
/// (0x80131506) the moment 7z.dll calls through the pointer. Spelling both slots
/// out makes the layout unambiguous: [QueryInterface, AddRef, Release, Read, Seek].
/// </remarks>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000300030000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInStream
{
    /// <param name="data">Destination buffer owned by the caller.</param>
    /// <param name="size">Bytes requested.</param>
    /// <param name="processedSize">UInt32* we write; 7-Zip permits NULL here.</param>
    [PreserveSig]
    int Read(IntPtr data, uint size, IntPtr processedSize);

    /// <param name="offset">Signed offset; may seek past EOF (legal), never before 0.</param>
    /// <param name="seekOrigin">0=begin, 1=current, 2=end.</param>
    /// <param name="newPosition">UInt64* we write; 7-Zip documents the value as undefined on error and may pass NULL.</param>
    [PreserveSig]
    int Seek(long offset, uint seekOrigin, IntPtr newPosition);
}

/// <summary>ISequentialOutStream (group 3, sub 0x02). 7z.dll writes to us.</summary>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000300020000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISequentialOutStream
{
    [PreserveSig]
    int Write(IntPtr data, uint size, IntPtr processedSize);
}

/// <summary>
/// IOutStream (group 3, sub 0x04). The seekable write side. Flat-declared like
/// IInStream: vtable is [QI, AddRef, Release, Write (from ISequentialOutStream),
/// Seek, SetSize]. 7z.dll QIs for this on the same CCW it got ISequentialOutStream
/// from when the format handler needs to seek (rewrite headers, jump back for solid).
/// </summary>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000300040000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOutStream
{
    // ---- ISequentialOutStream slot ----
    [PreserveSig]
    int Write(IntPtr data, uint size, IntPtr processedSize);

    // ---- IOutStream slots ----
    [PreserveSig]
    int Seek(long offset, uint seekOrigin, IntPtr newPosition);

    /// <param name="newSize">Set the total length of the stream (truncate or extend).</param>
    [PreserveSig]
    int SetSize(long newSize);
}

/// <summary>IProgress (group 0, sub 5). Base of the extract callback.</summary>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000000050000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IProgress
{
    [PreserveSig]
    int SetTotal(ulong total);

    /// <summary>The pointer may be NULL before SetTotal; treat it as "unknown".</summary>
    [PreserveSig]
    int SetCompleted(IntPtr completeValue);
}

/// <summary>
/// IArchiveOpenCallback (group 6, sub 0x10). SetTotal reports the volume count
/// 7-Zip discovered while opening a split archive - this is our first hard
/// signal that we are dealing with volumes at all.
/// </summary>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000600100000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IArchiveOpenCallback
{
    /// <param name="files">Pointer to UInt64 file count, may be NULL.</param>
    /// <param name="bytes">Pointer to UInt64 byte total, may be NULL.</param>
    [PreserveSig]
    int SetTotal(IntPtr files, IntPtr bytes);

    [PreserveSig]
    int SetCompleted(IntPtr files, IntPtr bytes);
}

/// <summary>
/// IArchiveOpenVolumeCallback (group 6, sub 0x30). When 7-Zip wants a sibling
/// volume it asks us by name; returning S_FALSE makes it give up on that volume.
/// </summary>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000600300000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IArchiveOpenVolumeCallback
{
    [PreserveSig]
    int GetProperty(uint propId, IntPtr value);

    [PreserveSig]
    int GetStream([MarshalAs(UnmanagedType.LPWStr)] string name, out IInStream? inStream);
}

/// <summary>
/// IArchiveExtractCallback (group 6, sub 0x20).
/// </summary>
/// <remarks>
/// Flattened on purpose, exactly like IInStream. The C++ interface derives from
/// IProgress, so the real vtable is
///   [QueryInterface, AddRef, Release, SetTotal, SetCompleted,
///    GetStream, PrepareOperation, SetOperationResult]
/// Declaring IProgress as a base interface and letting the CLR synthesise that
/// layout is what produced E_POINTER on Extract; spelling out all eight slots
/// removes the ambiguity.
/// </remarks>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000600200000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IArchiveExtractCallback
{
    // ---- IProgress slots ----
    [PreserveSig]
    int SetTotal(ulong total);

    [PreserveSig]
    int SetCompleted(IntPtr completeValue);

    // ---- IArchiveExtractCallback slots ----
    /// <param name="index">Item index in the archive.</param>
    /// <param name="outStream">ISequentialOutStream** we fill in; leaving it NULL skips the item.</param>
    /// <param name="askExtractMode">NExtract::NAskMode.</param>
    [PreserveSig]
    int GetStream(uint index, IntPtr outStream, int askExtractMode);

    [PreserveSig]
    int PrepareOperation(int askExtractMode);

    [PreserveSig]
    int SetOperationResult(int opRes);
}

// =========================================================================
// Password callbacks. A handler never takes a password as a property - it
// QueryInterfaces whichever callback object it was handed (the extract callback
// while decoding, the update callback while encoding) for one of these two.
// =========================================================================

/// <summary>
/// ICryptoGetTextPassword (group 5, sub 0x10). The 7z and zip handlers both go
/// through this interface when they need a password to open or decode.
/// </summary>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000500100000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICryptoGetTextPassword
{
    /// <param name="password">
    /// BSTR* the handler owns and frees with SysFreeString. Returning E_ABORT
    /// (rather than an empty BSTR) is how a client says "I have no password" -
    /// an empty string means "the password is empty".
    /// </param>
    [PreserveSig]
    int CryptoGetTextPassword(out IntPtr password);
}

/// <summary>
/// ICryptoGetTextPassword2 (group 5, sub 0x11). Same contract plus an explicit
/// "a password is defined" flag; this is the one the *write* path uses, so the
/// handler can tell "no encryption wanted" from "encrypt with an empty password".
/// </summary>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000500110000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICryptoGetTextPassword2
{
    /// <param name="passwordIsDefined">Int32* set to 1 when a password is supplied.</param>
    /// <param name="password">BSTR* the handler frees with SysFreeString.</param>
    [PreserveSig]
    int CryptoGetTextPassword2(IntPtr passwordIsDefined, out IntPtr password);
}

// =========================================================================
// Update (compression) interfaces we implement and 7z.dll calls back into.
// Same flat-declaration rule as the extract side (坑 1 in NOTES-7z-com-interop.md):
// IArchiveUpdateCallback derives from IProgress, so the vtable is
//   [QI, AddRef, Release, SetTotal, SetCompleted,
//    GetUpdateItemInfo, GetProperty, GetStream, SetOperationResult]
// and IArchiveUpdateCallback2 appends [GetVolumeSize, GetVolumeStream].
// =========================================================================

/// <summary>
/// IArchiveUpdateCallback (group 6, sub 0x80). Drives an UpdateItems call:
/// for every item the handler asks "is this new data? new props? where in the
/// old archive?", then asks for the property bag, then asks for the input
/// stream. We answer from an in-memory file list built before the call.
/// </summary>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000600800000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IArchiveUpdateCallback
{
    // ---- IProgress slots ----
    [PreserveSig]
    int SetTotal(ulong total);

    [PreserveSig]
    int SetCompleted(IntPtr completeValue);

    // ---- IArchiveUpdateCallback slots ----
    /// <param name="index">Item index in the new archive (0..numItems-1).</param>
    /// <param name="newData">1 = provide new bytes, 0 = keep old bytes (repack).</param>
    /// <param name="newProps">1 = provide new properties, 0 = keep old.</param>
    /// <param name="indexInArchive">Index in the OLD archive this item maps to, or UInt32.MaxValue for "new item".</param>
    [PreserveSig]
    int GetUpdateItemInfo(uint index, IntPtr newData, IntPtr newProps, IntPtr indexInArchive);

    /// <summary>
    /// Property bag for item <paramref name="index"/>. Same PROPIDs as
    /// IInArchive.GetProperty. Uses IntPtr (not ref PropVariant) because 7z.dll
    /// allocates a 16-byte PROPVARIANT and passes the pointer; the CLR's ref
    /// struct marshalling would write Marshal.SizeOf&lt;PropVariant&gt;() bytes
    /// (24) back, overwriting 8 bytes of adjacent stack. IntPtr lets us write
    /// exactly 16 bytes.
    /// </summary>
    [PreserveSig]
    int GetProperty(uint index, uint propId, IntPtr value);

    /// <param name="index">Item index.</param>
    /// <param name="inStream">ISequentialInStream* we fill in; NULL for directories.</param>
    [PreserveSig]
    int GetStream(uint index, IntPtr inStream);

    [PreserveSig]
    int SetOperationResult(int operationResult);
}

/// <summary>
/// IArchiveUpdateCallback2 (group 6, sub 0x82). Adds volume queries; the
/// handler asks for these when the "v"/"vz" properties were set via
/// ISetProperties, i.e. when the user asked for split volumes.
/// </summary>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000600820000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IArchiveUpdateCallback2
{
    // ---- IProgress slots ----
    [PreserveSig]
    int SetTotal(ulong total);

    [PreserveSig]
    int SetCompleted(IntPtr completeValue);

    // ---- IArchiveUpdateCallback slots ----
    [PreserveSig]
    int GetUpdateItemInfo(uint index, IntPtr newData, IntPtr newProps, IntPtr indexInArchive);

    [PreserveSig]
    int GetProperty(uint index, uint propId, IntPtr value);

    [PreserveSig]
    int GetStream(uint index, IntPtr inStream);

    [PreserveSig]
    int SetOperationResult(int operationResult);

    // ---- IArchiveUpdateCallback2 slots ----
    /// <param name="index">0-based volume index.</param>
    /// <param name="size">UInt64* we write the byte size this volume should be capped at.</param>
    [PreserveSig]
    int GetVolumeSize(uint index, IntPtr size);

    /// <param name="index">0-based volume index.</param>
    /// <param name="volumeStream">ISequentialOutStream** we fill in to write this volume's bytes to.</param>
    [PreserveSig]
    int GetVolumeStream(uint index, IntPtr volumeStream);
}

/// <summary>
/// ISetProperties (group 6, sub 0x03). Optional; QI'd off the IOutArchive when
/// the caller wants to tune level, method, solid, password, volumes, etc.
/// Names are short codes ("x" = level, "m" = method, "p" = password, "v" = volumes...).
/// </summary>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000600030000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISetProperties
{
    [PreserveSig]
    int SetProperties(
        IntPtr names,            // const wchar_t* const*
        IntPtr values,           // const PROPVARIANT*
        uint numProps);
}

// =========================================================================
// Interfaces 7z.dll implements and WE call.
// =========================================================================

/// <summary>
/// IInArchive (group 6, sub 0x60). Method order must match IArchive.h exactly;
/// the CLR resolves these by vtable slot, not by name.
/// </summary>
[ComImport]
[Guid("23170F69-40C1-278A-0000-000600600000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInArchive
{
    /// <remarks>
    /// The stream and callback arrive as raw interface pointers, not typed
    /// parameters, and that is deliberate. The default marshaler hands a
    /// managed-interface argument to native code as an IUnknown pointer and
    /// expects the callee to QueryInterface; 7-Zip does not - it calls Read and
    /// Seek straight off the pointer it was given. Passing the explicit
    /// IInStream pointer from Marshal.GetComInterfaceForObject is what keeps the
    /// vtable correct (getting this wrong corrupts the CLR: 0x80131506).
    /// </remarks>
    [PreserveSig]
    int Open(IntPtr stream, IntPtr maxCheckStartPosition, IntPtr openCallback);

    [PreserveSig]
    int Close();

    [PreserveSig]
    int GetNumberOfItems(out uint numItems);

    [PreserveSig]
    int GetProperty(uint index, uint propId, ref PropVariant value);

    /// <param name="indices">Sorted item indices; pass null with numItems = 0xFFFFFFFF for "all".</param>
    /// <param name="extractCallback">Raw IArchiveExtractCallback pointer - same reasoning as Open.</param>
    [PreserveSig]
    int Extract(
        [In, MarshalAs(UnmanagedType.LPArray)] uint[]? indices,
        uint numItems,
        int testMode,
        IntPtr extractCallback);

    [PreserveSig]
    int GetArchiveProperty(uint propId, ref PropVariant value);

    [PreserveSig]
    int GetNumberOfProperties(out uint numProps);

    [PreserveSig]
    int GetPropertyInfo(
        uint index,
        [MarshalAs(UnmanagedType.BStr)] out string name,
        out uint propId,
        out ushort varType);

    [PreserveSig]
    int GetNumberOfArchiveProperties(out uint numProps);

    [PreserveSig]
    int GetArchivePropertyInfo(
        uint index,
        [MarshalAs(UnmanagedType.BStr)] out string name,
        out uint propId,
        out ushort varType);
}

/// <summary>
/// IOutArchive (group 6, sub 0xA0). The write side of a handler; QI'd off the
/// same object CreateObject returned (so the same CLSID serves both reads and
/// writes for handlers that support it - 7z, zip, tar, gz, bz2, xz, zst...).
///
/// UpdateItems is the whole API: we hand it an output stream, a count of new
/// items, and a callback, and the handler walks the callback to enumerate and
/// pull bytes. Properties (level, method, password, volumes...) are configured
/// beforehand via ISetProperties (QI'd off the same handler object).
/// </summary>
[ComImport]
[Guid("23170F69-40C1-278A-0000-000600A00000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOutArchive
{
    /// <param name="outStream">Where the finished archive bytes go. Must be seekable for some formats.</param>
    /// <param name="numItems">Count of items in the NEW archive.</param>
    /// <param name="updateCallback">Our IArchiveUpdateCallback (or IArchiveUpdateCallback2 for volumes).</param>
    [PreserveSig]
    int UpdateItems(IntPtr outStream, uint numItems, IntPtr updateCallback);

    /// <param name="type">NFileTimeType::EEnum - what timestamp flavour the handler wants in GetProperty(kpidMTime).</param>
    [PreserveSig]
    int GetFileTimeType(out uint type);
}
