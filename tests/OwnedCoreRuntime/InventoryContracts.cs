using System.Buffers.Binary;
using System.Net;
using System.Text;
using SplinterCellCNLauncher.Services;

internal static class InventoryContracts
{
    private const string Name = "ff-123456781234123412341234567890ab", GuidText = "123456781234123412341234567890ab";
    private static readonly Guid GuidValue = Guid.ParseExact(GuidText, "N");
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("CORE_RUNTIME_CONTRACT_FAILED"); }
    private static void Reject(Action action)
    {
        try { action(); } catch (IOException) { return; }
        throw new InvalidOperationException("CORE_RUNTIME_CONTRACT_FAILED");
    }
    private static byte[] Table(int stride, params byte[][] rows)
    {
        var table = new byte[8 + rows.Length * stride]; BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
        for (int i = 0; i < rows.Length; i++) rows[i].CopyTo(table, 8 + i * stride);
        return table;
    }
    private static byte[] Nic(ulong luid = 0x0102030405060708, uint index = 37, string? alias = null)
    {
        var row = new byte[1352]; BinaryPrimitives.WriteUInt64LittleEndian(row, luid);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), index); GuidValue.TryWriteBytes(row.AsSpan(12, 16));
        Encoding.Unicode.GetBytes(alias ?? Name).CopyTo(row, 28); return row;
    }
    private static byte[] Address(string address = "10.254.253.1", byte prefix = 24, uint dad = 4)
    {
        var row = new byte[80]; row[0] = 2; IPAddress.Parse(address).GetAddressBytes().CopyTo(row, 4);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(32), 0x0102030405060708);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(40), 37); row[60] = prefix;
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(64), dad); return row;
    }
    private static byte[] Route(string network = "10.254.253.0", byte prefix = 24, string nextHop = "0.0.0.0")
    {
        var row = new byte[104]; BinaryPrimitives.WriteUInt64LittleEndian(row, 0x0102030405060708);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), 37); row[12] = row[44] = 2;
        IPAddress.Parse(network).GetAddressBytes().CopyTo(row, 16); row[40] = prefix;
        IPAddress.Parse(nextHop).GetAddressBytes().CopyTo(row, 48); return row;
    }
    private static WindowsNicInventory Sample(byte[]? nic = null, byte[]? address = null, byte[]? route = null) => WindowsNicInventory.Decode(
        Table(1352, nic ?? Nic()), Table(80, address ?? Address()), Table(104, route ?? Route()));
    private static OwnedCoreNicSnapshot Report(string? guid = null, string? luid = null, uint index = 37) => new(Name, guid ?? GuidText, luid ?? "0102030405060708", index);
    internal static int Run()
    {
        int passed = 0;
        void Case(string label, Action action) { action(); passed++; Console.WriteLine("PASS " + label); }
        Case("kernel GUID byte order and all identity components bind", () => {
            var s = Sample(); var nic = s.Require(Report(), Name, GuidText, "10.254.253.1");
            Check(nic.Guid == GuidText && nic.Luid == 0x0102030405060708 && nic.Index == 37);
            Reject(() => s.Require(Report(luid: "0102030405060709"), Name, GuidText, "10.254.253.1"));
            Reject(() => s.Require(Report(index: 38), Name, GuidText, "10.254.253.1"));
            Reject(() => s.Require(Report(guid: new string('1', 32)), Name, GuidText, "10.254.253.1"));
        });
        Case("kernel source needs actual preferred non-skipped exact prefix", () => {
            Reject(() => Sample(address: Address("10.254.253.2")).Require(Report(), Name, GuidText, "10.254.253.1"));
            Reject(() => Sample(address: Address(prefix: 32)).Require(Report(), Name, GuidText, "10.254.253.1"));
            Reject(() => Sample(address: Address(dad: 1)).Require(Report(), Name, GuidText, "10.254.253.1"));
            byte[] a = Address(); a[61] = 1;
            Reject(() => Sample(address: a).Require(Report(), Name, GuidText, "10.254.253.1"));
        });
        Case("invalid or truncated tables fail rather than imply absence", () => {
            Reject(() => WindowsNicInventory.Decode(new byte[0], Table(80), Table(104)));
            var bad = Table(1352, Nic()); BinaryPrimitives.WriteUInt32LittleEndian(bad, 4097);
            Reject(() => WindowsNicInventory.Decode(bad, Table(80), Table(104)));
            Reject(() => WindowsNicInventory.Decode(Table(1352, Nic())[..^1], Table(80), Table(104)));
            Reject(() => WindowsNicInventory.Decode(Table(1352, Nic()), new byte[9], Table(104)));
            Reject(() => WindowsNicInventory.Decode(Table(1352, Nic()), Table(80), new byte[9]));
        });
        Case("duplicate zero and malformed kernel identities reject", () => {
            Reject(() => WindowsNicInventory.Decode(Table(1352, Nic(), Nic()), Table(80), Table(104)));
            Reject(() => Sample(nic: Nic(luid: 0))); Reject(() => Sample(nic: Nic(index: 0)));
            Reject(() => Sample(nic: Nic(alias: "bad\nname")));
            var bad = Nic(); bad.AsSpan(28, 514).Fill(1); Reject(() => Sample(nic: bad));
            bad = Nic(); bad[28] = 0; bad[29] = 0xD8; bad[30] = bad[31] = 0; Reject(() => Sample(nic: bad));
        });
        Case("orphan addresses and invalid route shapes reject", () => {
            var a = Address(); BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(40), 99); Reject(() => Sample(address: a));
            var r = Route(); r[12] = 23; Reject(() => Sample(route: r));
            Reject(() => Sample(route: Route("10.254.253.1", 24)));
            Reject(() => Sample(route: Route(prefix: 33)));
        });
        Case("subnet conflict includes broader connected networks and specific routes", () => {
            Check(Sample().ConflictsWithProbeSubnet());
            Check(Sample(address: Address("10.20.30.40", 8), route: Route("0.0.0.0", 0, "10.20.30.1")).ConflictsWithProbeSubnet());
            Check(Sample(address: Address("192.0.2.1"), route: Route("10.254.253.99", 32)).ConflictsWithProbeSubnet());
            Check(Sample(address: Address("192.0.2.1"), route: Route("10.0.0.0", 8)).ConflictsWithProbeSubnet());
            Check(!Sample(address: Address("192.0.2.1"), route: Route("0.0.0.0", 0, "192.0.2.254")).ConflictsWithProbeSubnet());
        });
        Case("identity reuse does not count as owned interface absence", () => {
            var s = Sample(); KernelNic owned = s.Interfaces[0]; Check(!s.Absent(owned));
            Check(!s.Absent(owned with { Guid = new string('1', 32), Index = 999, Alias = "another" }));
            Check(!s.Absent(owned with { Guid = new string('1', 32), Luid = 999, Alias = "another" }));
            Check(!s.Absent(owned with { Guid = new string('1', 32), Luid = 999, Index = 999 }));
            Check(s.Absent(new(new string('1', 32), 999, 999, "another")));
        });
        Case("foreign identity and route changes affect sanitized digest", () => {
            var s = Sample(); Check(s.Digest() != Sample(nic: Nic(alias: "changed")).Digest());
            Check(s.Digest() != Sample(address: Address("10.254.253.2")).Digest());
            Check(s.Digest() != Sample(route: Route("0.0.0.0", 0)).Digest());
            Check(s.Digest().Length == 64 && s.Digest() == Sample().Digest());
        });
        Case("owned changes allow only fixed subnet and exact kernel automatic routes", () => {
            byte[] foreign = Nic(99, 98, "foreign"); Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee").TryWriteBytes(foreign.AsSpan(12, 16));
            var before = WindowsNicInventory.Decode(Table(1352, foreign), Table(80), Table(104));
            var after = WindowsNicInventory.Decode(Table(1352, foreign, Nic()), Table(80, Address()),
                Table(104, Route(), Route("10.254.253.1",32), Route("10.254.253.255",32), Route("224.0.0.0",4), Route("255.255.255.255",32)));
            after.RequireOnlyOwnedProbeChanges(before, after.Interfaces[1]);
            foreach (var extra in new[] { Route("0.0.0.0",0), Route("224.0.0.0",8), Route("10.254.253.2",32), Route("10.254.253.0",24,"10.254.253.2") })
            {
                var bad = WindowsNicInventory.Decode(Table(1352, foreign, Nic()), Table(80, Address()), Table(104, Route(), extra));
                Reject(() => bad.RequireOnlyOwnedProbeChanges(before, bad.Interfaces[1]));
            }
        });
        Case("cleanup comparison retains foreign addresses routes and exact identity", () => {
            var before = Sample(); var removedAddress = WindowsNicInventory.Decode(Table(1352, Nic()), Table(80), Table(104, Route()));
            Check(before.Digest() != removedAddress.Digest());
            var reordered = WindowsNicInventory.Decode(Table(1352, Nic()), Table(80, Address()), Table(104, Route(), Route("224.0.0.0",4)));
            var reverse = WindowsNicInventory.Decode(Table(1352, Nic()), Table(80, Address()), Table(104, Route("224.0.0.0",4), Route()));
            Check(reordered.Digest() == reverse.Digest());
        });
        Case("normal cleanup rejects absence already observed before EndReads", () => {
            bool triggered=false;
            try { NormalCleanupObservation.Run(()=>new(true,true,false,true,true),()=>triggered=true,default).GetAwaiter().GetResult(); }
            catch(RuntimeCheckFailure failure) { Check(failure.Code=="NIC_ABSENT_BEFORE_END_READS"&&!triggered); return; }
            throw new InvalidOperationException("CORE_RUNTIME_CONTRACT_FAILED");
        });
        Case("normal cleanup requires a new sample after the actual trigger", () => {
            bool triggered=false;int reads=0;
            NormalCleanupObservation.Run(()=>{reads++;if(reads==1){Check(!triggered);return new(true,true,true,false,true);}Check(triggered);return new(true,true,false,true,true);},()=>{Check(reads==1);triggered=true;},default).GetAwaiter().GetResult();
            Check(triggered&&reads==2);
            // The synchronous native read cannot be forcibly interrupted, but
            // a response arriving after the one total budget must still fail.
            long now=0;reads=0;
            try { NormalCleanupObservation.Run(()=>{reads++;if(reads==1)return new(true,true,true,false,true);now=8000;return new(true,true,false,true,true);},()=>{},default,()=>now).GetAwaiter().GetResult(); }
            catch(RuntimeCheckFailure failure) { Check(failure.Code=="NIC_CLEANUP_OBSERVATION_TIMEOUT"&&reads==2);return; }
            throw new InvalidOperationException("CORE_RUNTIME_CONTRACT_FAILED");
        });
        Case("post-trigger core exit cannot count as observed live cleanup", () => {
            int reads=0;
            try { NormalCleanupObservation.Run(()=>++reads==1?new(true,true,true,false,true):new(true,false,false,true,true),()=>{},default).GetAwaiter().GetResult(); }
            catch(RuntimeCheckFailure failure) { Check(failure.Code=="CORE_EXITED_BEFORE_NIC_ABSENT"&&reads==2);return; }
            throw new InvalidOperationException("CORE_RUNTIME_CONTRACT_FAILED");
        });
        return passed;
    }
}
