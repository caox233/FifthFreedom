// Independent ABI checks against the real windows-sys declarations. Compile
// metadata only; this file does not load DLLs or execute Windows APIs.
extern crate windows_sys;
use std::mem::{offset_of, size_of};
use windows_sys::Win32::NetworkManagement::IpHelper::{MIB_IF_ROW2, MIB_IF_TABLE2,
    MIB_UNICASTIPADDRESS_ROW, MIB_UNICASTIPADDRESS_TABLE, MIB_IPFORWARD_ROW2, MIB_IPFORWARD_TABLE2};
const _: () = {
    assert!(size_of::<MIB_IF_ROW2>() == 1352);
    assert!(offset_of!(MIB_IF_ROW2, InterfaceLuid) == 0);
    assert!(offset_of!(MIB_IF_ROW2, InterfaceIndex) == 8);
    assert!(offset_of!(MIB_IF_ROW2, InterfaceGuid) == 12);
    assert!(offset_of!(MIB_IF_ROW2, Alias) == 28);
    assert!(offset_of!(MIB_IF_TABLE2, Table) == 8);
    assert!(size_of::<MIB_UNICASTIPADDRESS_ROW>() == 80);
    assert!(offset_of!(MIB_UNICASTIPADDRESS_ROW, InterfaceLuid) == 32);
    assert!(offset_of!(MIB_UNICASTIPADDRESS_ROW, InterfaceIndex) == 40);
    assert!(offset_of!(MIB_UNICASTIPADDRESS_ROW, OnLinkPrefixLength) == 60);
    assert!(offset_of!(MIB_UNICASTIPADDRESS_ROW, SkipAsSource) == 61);
    assert!(offset_of!(MIB_UNICASTIPADDRESS_ROW, DadState) == 64);
    assert!(offset_of!(MIB_UNICASTIPADDRESS_TABLE, Table) == 8);
    assert!(size_of::<MIB_IPFORWARD_ROW2>() == 104);
    assert!(offset_of!(MIB_IPFORWARD_ROW2, InterfaceLuid) == 0);
    assert!(offset_of!(MIB_IPFORWARD_ROW2, InterfaceIndex) == 8);
    assert!(offset_of!(MIB_IPFORWARD_ROW2, DestinationPrefix) == 12);
    assert!(offset_of!(MIB_IPFORWARD_ROW2, NextHop) == 44);
    assert!(offset_of!(MIB_IPFORWARD_TABLE2, Table) == 8);
};
