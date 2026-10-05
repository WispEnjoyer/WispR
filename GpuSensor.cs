using System;
using System.Runtime.InteropServices;

namespace WispR
{
    /// <summary>
    /// GPU temperature and load for AMD Radeon cards, read through AMD's own display library
    /// (atiadlxx.dll, installed with the Radeon/Adrenalin driver) — the same numbers Adrenalin shows.
    /// No admin rights or extra software needed. On other GPUs (or without the driver) it reports
    /// "not available".
    /// </summary>
    static class GpuSensor
    {
        const int PMLOG_TEMPERATURE_EDGE = 8, PMLOG_INFO_ACTIVITY_GFX = 19, PMLOG_TEMPERATURE_HOTSPOT = 27;
        const int MaxSensors = 256;

        static IntPtr context;
        static int adapter = -1;
        static bool tried, failed;
        static readonly object gate = new object();
        static AdlMalloc malloc; // kept alive: the library calls back into it

        public struct Reading
        {
            public bool Ok;
            public int Temp;      // °C, edge (what Adrenalin calls "GPU temperature")
            public int Hotspot;   // °C, junction / hotspot (-1 if not reported)
            public int Load;      // % graphics activity (-1 if not reported)
        }

        public static Reading Read()
        {
            lock (gate)
            {
                if (failed) return default;
                if (!tried) { tried = true; if (!Init()) { failed = true; return default; } }
                IntPtr buf = Marshal.AllocHGlobal(4 + MaxSensors * 8);
                try
                {
                    // zero it, then ask for the current sensor values
                    for (int i = 0; i < 4 + MaxSensors * 8; i += 4) Marshal.WriteInt32(buf, i, 0); // as AMD's sample does
                    if (ADL2_New_QueryPMLogData_Get(context, adapter, buf) != 0) return default;
                    int Sensor(int id) => Marshal.ReadInt32(buf, 4 + id * 8) != 0 ? Marshal.ReadInt32(buf, 4 + id * 8 + 4) : -1;
                    int edge = Sensor(PMLOG_TEMPERATURE_EDGE), hot = Sensor(PMLOG_TEMPERATURE_HOTSPOT);
                    if (edge <= 0 && hot <= 0) return default;
                    return new Reading { Ok = true, Temp = edge > 0 ? edge : hot, Hotspot = hot, Load = Sensor(PMLOG_INFO_ACTIVITY_GFX) };
                }
                catch (Exception ex) { Log.Error("GpuSensor.Read", ex); return default; }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }

        static bool Init()
        {
            try
            {
                malloc = size => Marshal.AllocCoTaskMem(size);
                if (ADL2_Main_Control_Create(malloc, 1, out context) != 0 || context == IntPtr.Zero)
                {
                    Log.Write("GPU temperature: AMD driver library didn't start.");
                    return false;
                }
                if (ADL2_Adapter_NumberOfAdapters_Get(context, out int n) != 0) n = 0;
                // several adapter entries can belong to the same card (one per output): take the first
                // active one that actually reports a temperature
                IntPtr buf = Marshal.AllocHGlobal(4 + MaxSensors * 8);
                try
                {
                    for (int i = 0; i < n && adapter < 0; i++)
                    {
                        if (ADL2_Adapter_Active_Get(context, i, out int active) != 0 || active == 0) continue;
                        for (int k = 0; k < 4 + MaxSensors * 8; k += 4) Marshal.WriteInt32(buf, k, 0);
                        if (ADL2_New_QueryPMLogData_Get(context, i, buf) != 0) continue;
                        bool edge = Marshal.ReadInt32(buf, 4 + PMLOG_TEMPERATURE_EDGE * 8) != 0;
                        bool hot = Marshal.ReadInt32(buf, 4 + PMLOG_TEMPERATURE_HOTSPOT * 8) != 0;
                        if (edge || hot) adapter = i;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
                if (adapter < 0) { Log.Write("GPU temperature: no AMD GPU reporting temperatures (" + n + " adapter entries)."); return false; }
                Log.Write("GPU temperature: reading AMD adapter " + adapter + ".");
                return true;
            }
            catch (DllNotFoundException) { Log.Write("GPU temperature: no AMD driver library (atiadlxx.dll) — not an AMD GPU?"); return false; }
            catch (EntryPointNotFoundException) { Log.Write("GPU temperature: the AMD driver is too old for sensor readings."); return false; }
            catch (Exception ex) { Log.Error("GpuSensor.Init", ex); return false; }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr AdlMalloc(int size);

        // atiadlxx.dll is the 64-bit library (WispR runs 64-bit on 64-bit Windows)
        [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_Main_Control_Create(AdlMalloc callback, int enumConnectedAdapters, out IntPtr context);
        [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int count);
        [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_Adapter_Active_Get(IntPtr context, int adapter, out int active);
        [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_New_QueryPMLogData_Get(IntPtr context, int adapter, IntPtr output);
    }
}
