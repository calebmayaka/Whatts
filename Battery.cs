using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Whatts;

/// <summary>Flags from BATTERY_STATUS.PowerState.</summary>
[Flags]
public enum PowerState : uint
{
    None = 0,
    OnLine = 0x1,
    Discharging = 0x2,
    Charging = 0x4,
    Critical = 0x8,
}

/// <summary>
/// One snapshot of one battery. Capacities are mWh, voltage mV, rate mW
/// (positive while charging, negative while discharging). Null = firmware didn't report it.
/// </summary>
public sealed record BatteryReading
{
    public string Name { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Chemistry { get; init; } = "";
    /// <summary>Capacity is on a relative scale rather than mWh, so rate is meaningless.</summary>
    public bool RelativeUnits { get; init; }
    public PowerState State { get; init; }
    public uint? Capacity { get; init; }
    public uint? FullCharged { get; init; }
    public uint? Design { get; init; }
    public uint? VoltageMv { get; init; }
    public int? RateMw { get; init; }
    public uint? CycleCount { get; init; }
    public double? TemperatureC { get; init; }

    public double? Percent =>
        Capacity is uint c && FullCharged is uint f && f > 0 ? Math.Min(100.0, 100.0 * c / f) : null;

    public double? HealthPercent =>
        FullCharged is uint f && Design is uint d && d > 0 ? 100.0 * f / d : null;

    public double? Watts => RateMw / 1000.0;
    public double? Volts => VoltageMv / 1000.0;
    public double? Amps => Watts is double w && Volts is double v && v > 0 ? w / v : null;
}

/// <summary>
/// Reads batteries through the battery class driver (IOCTL_BATTERY_QUERY_*), the same
/// interface the Windows battery meter uses. No admin rights or extra drivers needed.
/// </summary>
public static class BatteryReader
{
    public static List<BatteryReading> ReadAll()
    {
        var list = new List<BatteryReading>();
        foreach (string path in DevicePaths())
            if (Read(path) is { } reading)
                list.Add(reading);
        return list;
    }

    static BatteryReading? Read(string path)
    {
        using SafeFileHandle? h = Open(path);
        if (h is null) return null;

        uint wait = 0;
        if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, ref wait, sizeof(uint), out uint tag, sizeof(uint), out _, IntPtr.Zero) || tag == 0)
            return null; // empty battery slot

        var query = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = BatteryInformation };
        if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, ref query, QuerySize,
                out BATTERY_INFORMATION info, Marshal.SizeOf<BATTERY_INFORMATION>(), out _, IntPtr.Zero))
            return null;

        var wantStatus = new BATTERY_WAIT_STATUS { BatteryTag = tag };
        if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_STATUS, ref wantStatus, Marshal.SizeOf<BATTERY_WAIT_STATUS>(),
                out BATTERY_STATUS status, Marshal.SizeOf<BATTERY_STATUS>(), out _, IntPtr.Zero))
            return null;

        // Most laptop batteries don't implement this level; failures and nonsense mean "not reported".
        double? tempC = null;
        query.InformationLevel = BatteryTemperature;
        if (DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, ref query, QuerySize, out uint tenthsKelvin, sizeof(uint), out _, IntPtr.Zero))
        {
            double c = tenthsKelvin / 10.0 - 273.15;
            if (c is > -30 and < 120) tempC = c;
        }

        return new BatteryReading
        {
            Name = QueryString(h, tag, BatteryDeviceName),
            Manufacturer = QueryString(h, tag, BatteryManufactureName),
            Chemistry = Encoding.ASCII.GetString(BitConverter.GetBytes(info.Chemistry)).TrimEnd('\0', ' '),
            RelativeUnits = (info.Capabilities & BATTERY_CAPACITY_RELATIVE) != 0,
            State = (PowerState)status.PowerState,
            Capacity = status.Capacity == BATTERY_UNKNOWN ? null : status.Capacity,
            FullCharged = Positive(info.FullChargedCapacity),
            Design = Positive(info.DesignedCapacity),
            VoltageMv = Positive(status.Voltage),
            RateMw = status.Rate == BATTERY_UNKNOWN_RATE ? null : status.Rate,
            CycleCount = info.CycleCount == 0 ? null : info.CycleCount,
            TemperatureC = tempC,
        };
    }

    static uint? Positive(uint v) => v is 0 or BATTERY_UNKNOWN ? null : v;

    static string QueryString(SafeFileHandle h, uint tag, int level)
    {
        var query = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = level };
        var buf = new byte[512];
        if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, ref query, QuerySize, buf, buf.Length, out int got, IntPtr.Zero))
            return "";
        return Encoding.Unicode.GetString(buf, 0, got).TrimEnd('\0').Trim();
    }

    static SafeFileHandle? Open(string path)
    {
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h.IsInvalid)
        {
            h.Dispose();
            h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        }
        if (h.IsInvalid)
        {
            h.Dispose();
            return null;
        }
        return h;
    }

    static List<string> DevicePaths()
    {
        var paths = new List<string>();
        var guid = GUID_DEVCLASS_BATTERY;
        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) return paths;
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data))
                    break; // ERROR_NO_MORE_ITEMS

                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
                if (needed <= 0) continue;

                IntPtr detail = Marshal.AllocHGlobal(needed);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: DWORD + WCHAR, padded on 64-bit.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, needed, out _, IntPtr.Zero))
                        paths.Add(Marshal.PtrToStringUni(detail + 4) ?? "");
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return paths;
    }

    // ---- Win32 interop ----

    static readonly Guid GUID_DEVCLASS_BATTERY = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2;
    const uint OPEN_EXISTING = 3, FILE_ATTRIBUTE_NORMAL = 0x80;

    // CTL_CODE(FILE_DEVICE_BATTERY = 0x29, fn, METHOD_BUFFERED, FILE_READ_ACCESS)
    const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
    const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;
    const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;

    // BATTERY_QUERY_INFORMATION_LEVEL
    const int BatteryInformation = 0, BatteryTemperature = 2, BatteryDeviceName = 4, BatteryManufactureName = 6;

    const uint BATTERY_CAPACITY_RELATIVE = 0x40000000;
    const uint BATTERY_UNKNOWN = 0xFFFFFFFF;
    const int BATTERY_UNKNOWN_RATE = unchecked((int)0x80000000);

    static readonly int QuerySize = Marshal.SizeOf<BATTERY_QUERY_INFORMATION>();

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_QUERY_INFORMATION
    {
        public uint BatteryTag;
        public int InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_INFORMATION
    {
        public uint Capabilities;
        public uint TechnologyAndReserved; // UCHAR Technology + UCHAR Reserved[3]
        public uint Chemistry;             // UCHAR[4], e.g. "LION"
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_WAIT_STATUS
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_STATUS
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, out int requiredSize, IntPtr devInfo);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint inBuf, int inSize, out uint outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_QUERY_INFORMATION inBuf, int inSize, out BATTERY_INFORMATION outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_QUERY_INFORMATION inBuf, int inSize, out uint outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_QUERY_INFORMATION inBuf, int inSize, [Out] byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_WAIT_STATUS inBuf, int inSize, out BATTERY_STATUS outBuf, int outSize, out int returned, IntPtr overlapped);
}
