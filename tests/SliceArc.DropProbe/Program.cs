using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SliceArc.Core.DragDrop;

namespace SliceArc.DropProbe;

/// <summary>
/// Drives a real OLE drop onto the real SliceArc main window.
///
/// Why this runs in-process: the window's drop target pointer, published as the
/// "OleDropTargetInterface" window property by RegisterDragDrop, is only valid
/// inside the process that owns it. Handing it to another process and calling
/// Marshal.GetObjectForIUnknown on it reads foreign memory and kills the CLR
/// with "Internal CLR error (0x80131506)". Genuine cross-process drag-and-drop
/// works only through OLE's own marshalling, i.e. inside the modal DoDragDrop
/// loop, which cannot be driven without a mouse.
///
/// So the probe references the app assembly, constructs the actual
/// SliceArc.App.Views.MainWindow, shows it, and performs DragEnter / DragOver /
/// Drop on that window's own drop target with a genuine CF_HDROP data object.
/// Everything the user's mouse would trigger runs: WPF's drop routing, the
/// window's AllowDrop, the Drop handler and the archive update behind it.
///
/// Usage: SliceArc.DropProbe &lt;archive&gt; &lt;path&gt; [path...]
/// Exit codes: 0 drop accepted, 2 bad args, 3 no drop target, 6 target refused,
///             7 Drop failed, 8 drop accepted but the archive never changed.
/// </summary>
internal static class Program
{
    private const uint DropEffectCopy = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: SliceArc.DropProbe <archive> <path> [path...]");
            return 2;
        }

        var archive = Path.GetFullPath(args[0]);
        var files = args.Skip(1).Select(Path.GetFullPath).ToArray();

        foreach (var file in files)
        {
            if (!File.Exists(file) && !Directory.Exists(file))
            {
                Console.Error.WriteLine($"not found: {file}");
                return 2;
            }
        }

        // A plain Application, not SliceArc.App.App: instantiating the app class
        // drags in its OnStartup, which builds a *second* main window from the
        // same command line and leaves two windows fighting over the drop.
        // The only thing the window really needs from the app class is the theme
        // dictionary (App.CurrentTheme is a static with a Light default).
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/SliceArc.App;component/Themes/Minimal.xaml", UriKind.Absolute),
        });

        var window = new SliceArc.App.Views.MainWindow(archive);

        // Application.Run() normally installs this. Without it, the continuations
        // after "await Task.Run(...)" inside the window land on a pool thread,
        // touch UI objects, throw a cross-thread exception and vanish into an
        // unobserved task - the work happens but the UI never learns about it.
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        window.Show();
        Pump(TimeSpan.FromSeconds(2));   // lay out, load the archive

        var hwnd = new WindowInteropHelper(window).Handle;
        var targetPtr = GetProp(hwnd, "OleDropTargetInterface");
        if (targetPtr == IntPtr.Zero)
        {
            Console.Error.WriteLine("window is not an OLE drop target (GetProp OleDropTargetInterface == 0)");
            window.Close();
            return 3;
        }

        Console.WriteLine($"hwnd=0x{hwnd:X} dropTarget=0x{targetPtr:X} files={files.Length}");
        Console.WriteLine($"appType={Application.Current.GetType().FullName} " +
                          $"log={SliceArc.App.Services.AppLog.FilePath}");

        var beforeWrite = File.GetLastWriteTimeUtc(archive);
        var exit = DriveDrop(targetPtr, hwnd, files);

        // Diagnostic: how many windows exist (a modal error dialog would show up
        // as an extra window) and what the drop handler was looking at.
        Console.WriteLine($"windows={Application.Current.Windows.Count} " +
                          $"main='{Application.Current.MainWindow?.Title}' mine='{window.Title}'");

        if (exit == 0)
        {
            // The handler runs the update on a background task, so keep pumping
            // until the archive on disk has actually been rewritten.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                Pump(TimeSpan.FromMilliseconds(200));
                if (File.GetLastWriteTimeUtc(archive) != beforeWrite) break;
            }

            Pump(TimeSpan.FromSeconds(5));   // let the UI finish its bookkeeping

            if (File.GetLastWriteTimeUtc(archive) == beforeWrite)
            {
                Console.Error.WriteLine("drop was accepted but the archive was never rewritten");
                exit = 8;
            }
            else
            {
                Console.WriteLine("archive was rewritten");
            }
        }

        window.Close();
        return exit;
    }

    private static int DriveDrop(IntPtr targetPtr, IntPtr hwnd, string[] files)
    {
        // The window's drop target is a *managed* WPF object (OleDropTarget), so
        // Marshal.GetObjectForIUnknown hands back that managed instance and any
        // cast to a ComImport IDropTarget fails - the CLR never builds an RCW for
        // its own CCW. Invoking the vtable slots directly is the way in, and it
        // also proves the slot layout the shell itself will use.
        // IDropTarget: 0 QI, 1 AddRef, 2 Release, 3 DragEnter, 4 DragOver, 5 DragLeave, 6 Drop.
        var dragEnter = VTableSlot<DragEnterDelegate>(targetPtr, 3);
        var dragOver = VTableSlot<DragOverDelegate>(targetPtr, 4);
        var drop = VTableSlot<DropDelegate>(targetPtr, 6);

        OleInitialize(IntPtr.Zero);

        var data = new HdropDataObject(files);
        var dataPtr = Marshal.GetComInterfaceForObject(data, typeof(IOleDataObject));

        try
        {
            var point = ClientCentre(hwnd);
            Console.WriteLine($"drop point (screen) = {point.x},{point.y}");

            // A real drag sends DragEnter once and then DragOver continuously as
            // the mouse moves. The window only handles DragOver (the usual WPF
            // pattern), so DragEnter legitimately reports "no effect" and the
            // decision is made on the DragOver calls. Bailing out here would be
            // testing the wrong thing.
            var effect = DropEffectCopy;
            var enter = dragEnter(targetPtr, dataPtr, 0, point, ref effect);
            Console.WriteLine($"DragEnter hr=0x{enter:X8} effect={effect}");

            effect = 0;
            for (var i = 0; i < 3; i++)
            {
                effect = DropEffectCopy;
                var over = dragOver(targetPtr, 0, point, ref effect);
                Console.WriteLine($"DragOver  hr=0x{over:X8} effect={effect}");
                if (over != 0 || effect == 0) break;
            }

            if (effect == 0)
            {
                Console.Error.WriteLine("target refused the drop (DragOver returned no effect)");
                return 6;
            }

            effect = DropEffectCopy;
            var dropped = drop(targetPtr, dataPtr, 0, point, ref effect);
            Console.WriteLine($"Drop      hr=0x{dropped:X8} effect={effect}");

            // WPF's drop target answers S_FALSE from Drop - that is its normal
            // success return, not a failure. Only a real error code means the
            // drop was rejected.
            if (dropped != 0 && dropped != 1)
            {
                Console.Error.WriteLine($"Drop failed with hr=0x{dropped:X8}");
                return 7;
            }

            return 0;
        }
        finally
        {
            Marshal.Release(dataPtr);
        }
    }

    /// <summary>Read slot N out of a COM vtable and call it as an unmanaged delegate.</summary>
    private static T VTableSlot<T>(IntPtr comPointer, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comPointer);
        var address = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    /// <summary>Run the WPF dispatcher for a while so queued work and rendering proceed.</summary>
    private static void Pump(TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(50),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private static POINTL ClientCentre(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var rect)) throw new InvalidOperationException("GetClientRect failed");

        var point = new POINTL
        {
            x = (rect.Right - rect.Left) / 2,
            y = (rect.Bottom - rect.Top) / 2,
        };

        if (!ClientToScreen(hwnd, ref point)) throw new InvalidOperationException("ClientToScreen failed");
        return point;
    }

    // =====================================================================
    // Interop
    // =====================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>IDropTarget (00000122-...): what RegisterDragDrop publishes.</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DragEnterDelegate(IntPtr self, IntPtr pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DragOverDelegate(IntPtr self, uint grfKeyState, POINTL pt, ref uint pdwEffect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DropDelegate(IntPtr self, IntPtr pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hWnd, string lpString);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINTL lpPoint);
}
