using System.Runtime.InteropServices;

namespace TaskbarMonitor.Interop;

internal static partial class NativeMethods
{
    /// <summary>MIB_IF_ROW2 (netioapi.h). Field order/alignment must match exactly; verified at
    /// runtime against Get-NetAdapter during development. Only InOctets/OutOctets are consumed.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MIB_IF_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu;
        public uint Type;
        public uint TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public uint AccessType;
        public uint DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public uint OperStatus;
        public uint AdminStatus;
        public uint MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
    }

    [DllImport("iphlpapi.dll")]
    public static extern int GetIfEntry2(ref MIB_IF_ROW2 row);

    /// <summary>Route-table lookup only; no packet is sent to the destination.</summary>
    [DllImport("iphlpapi.dll")]
    public static extern int GetBestInterfaceEx(byte[] destAddr, out uint bestIfIndex);

    public delegate void RouteChangeCallback(IntPtr callerContext, IntPtr row, int notificationType);

    [DllImport("iphlpapi.dll")]
    public static extern uint NotifyRouteChange2(ushort addressFamily, RouteChangeCallback callback, IntPtr callerContext,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification, out IntPtr notificationHandle);

    [DllImport("iphlpapi.dll")]
    public static extern uint CancelMibChangeNotify2(IntPtr notificationHandle);

    public const ushort AF_UNSPEC = 0;
    public const ushort AF_INET = 2;

    /// <summary>sockaddr_in for an IPv4 address, port 0.</summary>
    public static byte[] MakeSockaddrIn(byte a, byte b, byte c, byte d)
    {
        var sa = new byte[16];
        sa[0] = (byte)(AF_INET & 0xFF);
        sa[1] = (byte)(AF_INET >> 8);
        sa[4] = a; sa[5] = b; sa[6] = c; sa[7] = d;
        return sa;
    }
}
