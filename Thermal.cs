using System.Runtime.InteropServices;

namespace Whatts;

public sealed record ThermalZone(string Name, double Celsius);

/// <summary>
/// Reads ACPI thermal zones from the "Thermal Zone Information" performance counters.
/// Unlike MSAcpi_ThermalZoneTemperature in WMI, this needs no admin rights.
/// These are firmware-defined sensors (often CPU/SoC, skin, or chassis), not per-core CPU temps.
/// </summary>
public sealed class ThermalReader : IDisposable
{
    readonly IntPtr query;
    readonly IntPtr counter;
    readonly bool tenthsOfKelvin;

    public ThermalReader()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0)
        {
            query = IntPtr.Zero;
            return;
        }

        // "High Precision Temperature" is in tenths of a kelvin; older Windows only has whole kelvins.
        if (PdhAddEnglishCounterW(query, @"\Thermal Zone Information(*)\High Precision Temperature", IntPtr.Zero, out counter) == 0)
            tenthsOfKelvin = true;
        else if (PdhAddEnglishCounterW(query, @"\Thermal Zone Information(*)\Temperature", IntPtr.Zero, out counter) != 0)
            counter = IntPtr.Zero;
    }

    public List<ThermalZone> Read()
    {
        var zones = new List<ThermalZone>();
        if (counter == IntPtr.Zero || PdhCollectQueryData(query) != 0)
            return zones;

        const uint format = PDH_FMT_DOUBLE | PDH_FMT_NOCAP100;
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, format, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA || size == 0)
            return zones;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, format, ref size, out uint count, buf) != 0)
                return zones;

            int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(buf + i * stride);
                if (item.FmtValue.CStatus > PDH_CSTATUS_NEW_DATA) continue;

                double kelvin = tenthsOfKelvin ? item.FmtValue.DoubleValue / 10.0 : item.FmtValue.DoubleValue;
                if (kelvin <= 0) continue;

                string name = Marshal.PtrToStringUni(item.szName) ?? $"Zone {i}";
                zones.Add(new ThermalZone(CleanName(name), kelvin - 273.15));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        zones.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return zones;
    }

    // Instance names look like "\_TZ.TZS0"; the "\_TZ." prefix is just the ACPI namespace.
    static string CleanName(string name) =>
        name.StartsWith(@"\_TZ.", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;

    public void Dispose()
    {
        if (query != IntPtr.Zero) PdhCloseQuery(query);
    }

    // ---- PDH interop ----

    const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    const uint PDH_MORE_DATA = 0x800007D2;
    const uint PDH_CSTATUS_NEW_DATA = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double DoubleValue; // union; we always ask for PDH_FMT_DOUBLE
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);
}
