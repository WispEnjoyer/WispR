using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace WispR
{
    /// <summary>
    /// A minimal native event handler that WinRT events (TypedEventHandler&lt;TSender, TArgs&gt;) can call.
    /// It only signals that something changed; the media thread then reads the new state itself.
    ///
    /// Why it exists: the media sessions in our process are a cached copy that Windows refreshes from the
    /// player when someone listens to their change events. Without a listener the copy can stay stuck at
    /// how it was when the song started — typically "no length yet" for YouTube in Firefox-based browsers —
    /// while apps that do listen (Wallpaper Engine's media widgets) see the full timeline.
    ///
    /// The object answers for any delegate interface it's asked about (they all have the same shape: IUnknown
    /// plus Invoke(sender, args)), but refuses COM's marshalling interfaces, so it's always called directly.
    /// It lives for the whole run of the app; the reference count is only kept for COM's sake.
    /// </summary>
    sealed class WinRtEventSink
    {
        public readonly IntPtr Pointer;
        readonly AutoResetEvent signal;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QiFn(IntPtr self, ref Guid iid, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint RefFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int InvokeFn(IntPtr self, IntPtr sender, IntPtr args);

        // kept alive for as long as the native side may call them
        readonly QiFn qi; readonly RefFn addRef, release; readonly InvokeFn invoke;
        int refs = 1;

        public int Fired; // how many events arrived (diagnostics)

        static readonly Guid IUnknown = new Guid("00000000-0000-0000-C000-000000000046");
        static readonly Guid IAgileObject = new Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
        static readonly Guid IInspectable = new Guid("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
        static readonly Guid INoMarshal = new Guid("ECC8691B-C1DB-4DC0-855E-65F6C551AF49");

        public WinRtEventSink(AutoResetEvent signal)
        {
            this.signal = signal;
            qi = QueryInterface; addRef = AddRef; release = Release; invoke = Invoke;
            IntPtr vtbl = Marshal.AllocHGlobal(IntPtr.Size * 4);
            Marshal.WriteIntPtr(vtbl, 0 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(qi));
            Marshal.WriteIntPtr(vtbl, 1 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(addRef));
            Marshal.WriteIntPtr(vtbl, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(release));
            Marshal.WriteIntPtr(vtbl, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(invoke));
            Pointer = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(Pointer, vtbl);
        }

        int QueryInterface(IntPtr self, ref Guid iid, out IntPtr obj)
        {
            obj = IntPtr.Zero;
            // COM's own infrastructure interfaces (…-0000-0000-C000-000000000046: IMarshal, IStdMarshalInfo,
            // IExternalConnection, IWeakReferenceSource…) and IInspectable/INoMarshal: not us.
            string s = iid.ToString();
            bool comInfra = s.EndsWith("-0000-0000-c000-000000000046", StringComparison.OrdinalIgnoreCase) && iid != IUnknown;
            if (comInfra || iid == IInspectable || iid == INoMarshal) return unchecked((int)0x80004002); // E_NOINTERFACE
            obj = self; // IUnknown, IAgileObject (free-threaded: call us from any thread), or the event's delegate type
            Interlocked.Increment(ref refs);
            return 0;
        }

        uint AddRef(IntPtr self) => (uint)Interlocked.Increment(ref refs);
        uint Release(IntPtr self) => (uint)Math.Max(1, Interlocked.Decrement(ref refs)); // never freed: lives as long as the app

        int Invoke(IntPtr self, IntPtr sender, IntPtr args)
        {
            Interlocked.Increment(ref Fired);
            try { signal.Set(); } catch { }
            return 0;
        }
    }
}
