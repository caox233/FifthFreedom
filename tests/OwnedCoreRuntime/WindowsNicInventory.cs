using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SplinterCellCNLauncher.Services;

// Test instrumentation only. These are bounded, non-atomic kernel samples;
// they never create a NIC, retain a session or authorize production traffic.
internal sealed record KernelNic(string Guid, ulong Luid, uint Index, string Alias);
internal sealed record KernelAddress(ulong Luid, uint Index, string Address, byte Prefix, bool SkipAsSource, uint DadState);
internal sealed record KernelRoute(ulong Luid, uint Index, string Destination, byte Prefix, string NextHop);
internal sealed class WindowsNicInventory
{
    // Windows SDK netioapi.h, default packing, identical for x86/AMD64.
    // MIB_IF_ROW2: 1352; MIB_UNICASTIPADDRESS_ROW: 80; both tables start at 8.
    internal const int IfStride = 1352, AddressStride = 80, RouteStride = 104, TableOffset = 8, Limit = 4096;
    internal IReadOnlyList<KernelNic> Interfaces { get; }
    internal IReadOnlyList<KernelAddress> Addresses { get; }
    internal IReadOnlyList<KernelRoute> Routes { get; }
    private WindowsNicInventory(IReadOnlyList<KernelNic> interfaces, IReadOnlyList<KernelAddress> addresses, IReadOnlyList<KernelRoute> routes)
    { Interfaces = interfaces; Addresses = addresses; Routes = routes; }
    internal static IOException Invalid() => new("CORE_RUNTIME_KERNEL_INVALID");
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
    private static string Alias(ReadOnlySpan<byte> data)
    {
        int length = -1;
        for (int i = 0; i < data.Length; i += 2)
            if (BinaryPrimitives.ReadUInt16LittleEndian(data[i..]) == 0) { length = i; break; }
        if (length <= 0) throw Invalid();
        string value;
        try { value = new UnicodeEncoding(false, false, true).GetString(data[..length]); }
        catch (DecoderFallbackException) { throw Invalid(); }
        if (value.Any(char.IsControl)) throw Invalid();
        return value;
    }
    private static int Count(ReadOnlySpan<byte> table, int stride)
    {
        if (table.Length < TableOffset) throw Invalid();
        uint count = U32(table, 0);
        if (count > Limit || table.Length != checked(TableOffset + (int)count * stride)) throw Invalid();
        return (int)count;
    }
    internal static WindowsNicInventory Decode(byte[] ifTable, byte[] addressTable, byte[] routeTable)
    {
        var interfaces = new List<KernelNic>(); var addresses = new List<KernelAddress>();
        var guids = new HashSet<string>(); var luids = new HashSet<ulong>(); var indices = new HashSet<uint>();
        int count = Count(ifTable, IfStride);
        if (count == 0) throw Invalid();
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> row = ifTable.AsSpan(TableOffset + i * IfStride, IfStride);
            ulong luid = U64(row, 0); uint index = U32(row, 8);
            string guid = new Guid(row.Slice(12, 16)).ToString("N");
            string alias = Alias(row.Slice(28, 514));
            if (luid == 0 || index == 0 || guid == new string('0', 32)
                || !guids.Add(guid) || !luids.Add(luid) || !indices.Add(index)) throw Invalid();
            interfaces.Add(new(guid, luid, index, alias));
        }
        var unique = new HashSet<(ulong, uint, string)>();
        count = Count(addressTable, AddressStride);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> row = addressTable.AsSpan(TableOffset + i * AddressStride, AddressStride);
            if (BinaryPrimitives.ReadUInt16LittleEndian(row) != 2) throw Invalid(); // AF_INET only request
            string address = new IPAddress(row.Slice(4, 4)).ToString();
            ulong luid = U64(row, 32); uint index = U32(row, 40); uint dad = U32(row, 64);
            if (luid == 0 || index == 0 || row[60] > 32 || row[61] > 1 || dad > 4
                || !unique.Add((luid, index, address))
                || !interfaces.Any(n => n.Luid == luid && n.Index == index)) throw Invalid();
            addresses.Add(new(luid, index, address, row[60], row[61] != 0, dad));
        }
        var routes = new List<KernelRoute>();
        count = Count(routeTable, RouteStride);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> row = routeTable.AsSpan(TableOffset + i * RouteStride, RouteStride);
            ulong luid = U64(row, 0); uint index = U32(row, 8);
            if (BinaryPrimitives.ReadUInt16LittleEndian(row[12..]) != 2
                || BinaryPrimitives.ReadUInt16LittleEndian(row[44..]) != 2 || row[40] > 32
                || !interfaces.Any(n => n.Luid == luid && n.Index == index)) throw Invalid();
            string destination = new IPAddress(row.Slice(16, 4)).ToString();
            string nextHop = new IPAddress(row.Slice(48, 4)).ToString();
            if ((AddressNumber(destination) & Mask(row[40])) != AddressNumber(destination)) throw Invalid();
            var route = new KernelRoute(luid, index, destination, row[40], nextHop);
            if (routes.Contains(route)) throw Invalid();
            routes.Add(route);
        }
        return new(interfaces.AsReadOnly(), addresses.AsReadOnly(), routes.AsReadOnly());
    }
    private delegate uint Query(out IntPtr result);
    private static byte[] Read(Query query, int stride)
    {
        IntPtr table = IntPtr.Zero;
        try
        {
            if (query(out table) != 0 || table == IntPtr.Zero) throw Invalid();
            uint count = unchecked((uint)Marshal.ReadInt32(table));
            if (count > Limit) throw Invalid();
            var bytes = new byte[checked(TableOffset + (int)count * stride)];
            Marshal.Copy(table, bytes, 0, bytes.Length); return bytes;
        }
        finally { if (table != IntPtr.Zero) FreeMibTable(table); }
    }
    internal static WindowsNicInventory Capture()
    {
        if (!OperatingSystem.IsWindows()) throw Invalid();
        var snapshot = Decode(Read(GetIfTable2, IfStride), Read((out IntPtr ptr) => GetUnicastIpAddressTable(2, out ptr), AddressStride),
            Read((out IntPtr ptr) => GetIpForwardTable2(2, out ptr), RouteStride));
        // Independent scalar APIs reject a table-layout error or identity change
        // between samples. Failure is never interpreted as an absent adapter.
        foreach (var nic in snapshot.Interfaces)
        {
            var guid = Guid.ParseExact(nic.Guid, "N"); ulong luid = nic.Luid;
            if (ConvertInterfaceGuidToLuid(ref guid, out ulong observedLuid) != 0 || observedLuid != nic.Luid
                || ConvertInterfaceLuidToIndex(ref luid, out uint index) != 0 || index != nic.Index) throw Invalid();
            var alias = new StringBuilder(257);
            if (ConvertInterfaceLuidToAlias(ref luid, alias, 257) != 0 || alias.ToString() != nic.Alias) throw Invalid();
        }
        return snapshot;
    }
    internal bool Uses(string address) => Addresses.Any(row => row.Address == address);
    internal KernelNic Require(OwnedCoreNicSnapshot reported, string expectedName, string expectedGuid, string source)
    {
        if (reported.Name != expectedName || reported.Guid != expectedGuid
            || !ulong.TryParse(reported.Luid, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong luid)) throw Invalid();
        var found = Interfaces.Where(n => n.Guid == reported.Guid && n.Luid == luid && n.Index == reported.Index && n.Alias == reported.Name).ToArray();
        if (found.Length != 1 || !Addresses.Any(a => a.Luid == luid && a.Index == reported.Index
                && a.Address == source && a.Prefix == 24 && !a.SkipAsSource && a.DadState == 4)) throw Invalid();
        return found[0];
    }
    internal bool Absent(KernelNic owned) => Interfaces.All(n => n.Guid != owned.Guid && n.Luid != owned.Luid && n.Index != owned.Index && n.Alias != owned.Alias)
        && Addresses.All(a => a.Luid != owned.Luid && a.Index != owned.Index)
        && Routes.All(r => r.Luid != owned.Luid && r.Index != owned.Index);
    private static uint AddressNumber(string value) => BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(value).GetAddressBytes());
    private static uint Mask(byte prefix) => prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
    private static bool Overlap(string left, byte leftPrefix, string right, byte rightPrefix)
    {
        uint mask = Mask(Math.Min(leftPrefix, rightPrefix));
        return (AddressNumber(left) & mask) == (AddressNumber(right) & mask);
    }
    internal bool ConflictsWithProbeSubnet() => Addresses.Any(a => Overlap(a.Address, a.Prefix, "10.254.253.0", 24))
        || Routes.Any(r => r.Prefix != 0 && Overlap(r.Destination, r.Prefix, "10.254.253.0", 24));
    internal void RequireOnlyOwnedProbeChanges(WindowsNicInventory before, KernelNic owned)
    {
        if (Digest(owned) != before.Digest() || !Addresses.Where(a => a.Luid == owned.Luid || a.Index == owned.Index)
                .All(a => a.Luid == owned.Luid && a.Index == owned.Index && a.Address == "10.254.253.1" && a.Prefix == 24)
            || !Routes.Where(r => r.Luid == owned.Luid || r.Index == owned.Index)
                .All(r => r.Luid == owned.Luid && r.Index == owned.Index && AllowedOwnedRoute(r) && r.NextHop == "0.0.0.0")
            || !Routes.Any(r => r.Luid == owned.Luid && r.Index == owned.Index
                && r.Destination == "10.254.253.0" && r.Prefix == 24 && r.NextHop == "0.0.0.0")) throw Invalid();
    }
    private static bool AllowedOwnedRoute(KernelRoute route) =>
        (route.Prefix == 24 && route.Destination == "10.254.253.0")
        || (route.Prefix == 32 && route.Destination is "10.254.253.1" or "10.254.253.255" or "255.255.255.255")
        || (route.Prefix == 4 && route.Destination == "224.0.0.0");
    internal string Digest(KernelNic? exclude = null)
    {
        var interfaces = Interfaces.Where(n => n != exclude).OrderBy(n => n.Guid, StringComparer.Ordinal).ToArray();
        var addresses = Addresses.Where(a => exclude is null || a.Luid != exclude.Luid && a.Index != exclude.Index)
            .OrderBy(a => a.Luid).ThenBy(a => a.Index).ThenBy(a => a.Address, StringComparer.Ordinal).ToArray();
        var routes = Routes.Where(r => exclude is null || r.Luid != exclude.Luid && r.Index != exclude.Index)
            .OrderBy(r => r.Luid).ThenBy(r => r.Index).ThenBy(r => r.Destination, StringComparer.Ordinal)
            .ThenBy(r => r.Prefix).ThenBy(r => r.NextHop, StringComparer.Ordinal).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { interfaces, addresses, routes })));
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetIfTable2(out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern uint GetUnicastIpAddressTable(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr memory);
    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceGuidToLuid(ref Guid guid, out ulong luid);
    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceLuidToIndex(ref ulong luid, out uint index);
    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)] private static extern uint ConvertInterfaceLuidToAlias(ref ulong luid, StringBuilder alias, nuint length);
}
