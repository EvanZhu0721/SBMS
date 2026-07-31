use std::mem::{MaybeUninit, size_of};
use std::net::Ipv4Addr;

use windows::Win32::Foundation::{ERROR_BUFFER_OVERFLOW, ERROR_NO_DATA, NO_ERROR};
use windows::Win32::NetworkManagement::IpHelper::{
    GAA_FLAG_INCLUDE_GATEWAYS, GAA_FLAG_SKIP_ANYCAST, GAA_FLAG_SKIP_DNS_SERVER,
    GAA_FLAG_SKIP_MULTICAST, GET_ADAPTERS_ADDRESSES_FLAGS, GetAdaptersAddresses,
    IF_TYPE_ETHERNET_CSMACD, IF_TYPE_FASTETHER, IF_TYPE_FASTETHER_FX, IF_TYPE_GIGABITETHERNET,
    IF_TYPE_IEEE80211, IF_TYPE_PPP, IF_TYPE_PROP_VIRTUAL, IF_TYPE_SOFTWARE_LOOPBACK,
    IF_TYPE_TUNNEL, IF_TYPE_WWANPP, IF_TYPE_WWANPP2, IP_ADAPTER_ADDRESSES_LH,
    IP_ADAPTER_GATEWAY_ADDRESS_LH,
};
use windows::Win32::NetworkManagement::Ndis::IfOperStatusUp;
use windows::Win32::Networking::WinSock::{
    AF_INET, IpDadStateDeprecated, IpDadStatePreferred, SOCKADDR_IN, SOCKET_ADDRESS,
};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct LanIpv4Candidate {
    address: Ipv4Addr,
    interface_type: u32,
    has_gateway: bool,
    has_physical_address: bool,
    dad_rank: u8,
    metric: u32,
}

pub(crate) fn preferred_lan_ipv4() -> Result<Option<Ipv4Addr>, String> {
    let flags = GET_ADAPTERS_ADDRESSES_FLAGS(
        GAA_FLAG_INCLUDE_GATEWAYS.0
            | GAA_FLAG_SKIP_ANYCAST.0
            | GAA_FLAG_SKIP_MULTICAST.0
            | GAA_FLAG_SKIP_DNS_SERVER.0,
    );
    let mut required_bytes = 0_u32;
    let sizing_status =
        unsafe { GetAdaptersAddresses(AF_INET.0.into(), flags, None, None, &mut required_bytes) };
    if sizing_status == ERROR_NO_DATA.0 || (sizing_status == NO_ERROR.0 && required_bytes == 0) {
        return Ok(None);
    }
    if sizing_status != ERROR_BUFFER_OVERFLOW.0 {
        return Err(format_windows_error(
            "Couldn’t size the network adapter list",
            sizing_status,
        ));
    }

    for _ in 0..2 {
        let record_count = (required_bytes as usize).div_ceil(size_of::<IP_ADAPTER_ADDRESSES_LH>());
        let mut storage = Vec::<MaybeUninit<IP_ADAPTER_ADDRESSES_LH>>::with_capacity(record_count);
        storage.resize_with(record_count, MaybeUninit::uninit);
        let adapters = storage.as_mut_ptr().cast::<IP_ADAPTER_ADDRESSES_LH>();
        let status = unsafe {
            GetAdaptersAddresses(
                AF_INET.0.into(),
                flags,
                None,
                Some(adapters),
                &mut required_bytes,
            )
        };
        if status == ERROR_BUFFER_OVERFLOW.0 {
            continue;
        }
        if status == ERROR_NO_DATA.0 {
            return Ok(None);
        }
        if status != NO_ERROR.0 {
            return Err(format_windows_error(
                "Couldn’t enumerate network adapters",
                status,
            ));
        }

        let mut candidates = Vec::new();
        let mut adapter = adapters;
        while let Some(current) = unsafe { adapter.as_ref() } {
            if current.OperStatus == IfOperStatusUp && !excluded_interface_type(current.IfType) {
                let has_gateway = has_usable_ipv4_gateway(current.FirstGatewayAddress);
                let has_physical_address = current.PhysicalAddressLength > 0;
                let mut unicast = current.FirstUnicastAddress;
                while let Some(address) = unsafe { unicast.as_ref() } {
                    let dad_rank = if address.DadState == IpDadStatePreferred {
                        Some(0)
                    } else if address.DadState == IpDadStateDeprecated {
                        Some(1)
                    } else {
                        None
                    };
                    if let Some(dad_rank) = dad_rank
                        && let Some(ipv4) = socket_address_ipv4(&address.Address)
                        && usable_lan_ipv4(ipv4)
                    {
                        candidates.push(LanIpv4Candidate {
                            address: ipv4,
                            interface_type: current.IfType,
                            has_gateway,
                            has_physical_address,
                            dad_rank,
                            metric: current.Ipv4Metric,
                        });
                    }
                    unicast = address.Next;
                }
            }
            adapter = current.Next;
        }
        return Ok(select_lan_ipv4(&candidates));
    }

    Err("The network adapter list changed repeatedly while it was being read".into())
}

fn socket_address_ipv4(address: &SOCKET_ADDRESS) -> Option<Ipv4Addr> {
    if address.lpSockaddr.is_null()
        || address.iSockaddrLength < i32::try_from(size_of::<SOCKADDR_IN>()).unwrap_or(i32::MAX)
    {
        return None;
    }
    let socket = unsafe { &*address.lpSockaddr.cast::<SOCKADDR_IN>() };
    if socket.sin_family != AF_INET {
        return None;
    }
    let octets = unsafe { socket.sin_addr.S_un.S_un_b };
    Some(Ipv4Addr::new(
        octets.s_b1,
        octets.s_b2,
        octets.s_b3,
        octets.s_b4,
    ))
}

fn has_usable_ipv4_gateway(mut gateway: *mut IP_ADAPTER_GATEWAY_ADDRESS_LH) -> bool {
    while let Some(current) = unsafe { gateway.as_ref() } {
        if socket_address_ipv4(&current.Address).is_some_and(usable_lan_ipv4) {
            return true;
        }
        gateway = current.Next;
    }
    false
}

fn format_windows_error(context: &str, code: u32) -> String {
    format!(
        "{context}: {}",
        std::io::Error::from_raw_os_error(code as i32)
    )
}

fn excluded_interface_type(interface_type: u32) -> bool {
    matches!(
        interface_type,
        IF_TYPE_PPP | IF_TYPE_SOFTWARE_LOOPBACK | IF_TYPE_PROP_VIRTUAL | IF_TYPE_TUNNEL
    )
}

fn physical_interface_rank(interface_type: u32) -> u8 {
    match interface_type {
        IF_TYPE_ETHERNET_CSMACD
        | IF_TYPE_FASTETHER
        | IF_TYPE_FASTETHER_FX
        | IF_TYPE_GIGABITETHERNET
        | IF_TYPE_IEEE80211
        | IF_TYPE_WWANPP
        | IF_TYPE_WWANPP2 => 0,
        _ => 1,
    }
}

fn usable_lan_ipv4(address: Ipv4Addr) -> bool {
    !address.is_unspecified()
        && !address.is_loopback()
        && !address.is_link_local()
        && !address.is_multicast()
        && !address.is_broadcast()
}

fn select_lan_ipv4(candidates: &[LanIpv4Candidate]) -> Option<Ipv4Addr> {
    candidates
        .iter()
        .min_by_key(|candidate| {
            (
                !candidate.has_gateway,
                !candidate.has_physical_address,
                physical_interface_rank(candidate.interface_type),
                candidate.dad_rank,
                candidate.metric,
                candidate.address.octets(),
            )
        })
        .map(|candidate| candidate.address)
}

#[cfg(test)]
mod tests {
    use super::*;

    const ETHERNET: u32 = IF_TYPE_ETHERNET_CSMACD;

    fn candidate(
        address: [u8; 4],
        interface_type: u32,
        has_gateway: bool,
        has_physical_address: bool,
        preferred: bool,
        metric: u32,
    ) -> LanIpv4Candidate {
        LanIpv4Candidate {
            address: Ipv4Addr::from(address),
            interface_type,
            has_gateway,
            has_physical_address,
            dad_rank: u8::from(!preferred),
            metric,
        }
    }

    #[test]
    fn prefers_a_physical_gateway_over_virtual_or_gatewayless_interfaces() {
        let candidates = [
            candidate(
                [100, 109, 89, 106],
                IF_TYPE_PROP_VIRTUAL,
                true,
                true,
                true,
                1,
            ),
            candidate([192, 168, 50, 9], ETHERNET, false, true, true, 5),
            candidate([192, 168, 5, 71], ETHERNET, true, true, true, 25),
        ];

        assert_eq!(
            select_lan_ipv4(&candidates),
            Some(Ipv4Addr::new(192, 168, 5, 71))
        );
    }

    #[test]
    fn metric_breaks_ties_between_physical_gateway_interfaces() {
        let candidates = [
            candidate([192, 168, 1, 20], ETHERNET, true, true, true, 35),
            candidate([192, 168, 5, 71], IF_TYPE_IEEE80211, true, true, true, 10),
        ];

        assert_eq!(
            select_lan_ipv4(&candidates),
            Some(Ipv4Addr::new(192, 168, 5, 71))
        );
    }

    #[test]
    fn preferred_address_wins_over_deprecated_address_on_the_same_adapter() {
        let candidates = [
            candidate([192, 168, 5, 60], ETHERNET, true, true, false, 5),
            candidate([192, 168, 5, 71], ETHERNET, true, true, true, 25),
        ];

        assert_eq!(
            select_lan_ipv4(&candidates),
            Some(Ipv4Addr::new(192, 168, 5, 71))
        );
    }

    #[test]
    fn rejects_non_lan_ipv4_special_ranges() {
        for address in [
            Ipv4Addr::UNSPECIFIED,
            Ipv4Addr::LOCALHOST,
            Ipv4Addr::new(169, 254, 1, 1),
            Ipv4Addr::new(224, 0, 0, 1),
            Ipv4Addr::BROADCAST,
        ] {
            assert!(!usable_lan_ipv4(address), "{address}");
        }
        assert!(usable_lan_ipv4(Ipv4Addr::new(192, 168, 5, 71)));
    }

    #[test]
    fn live_scan_never_returns_a_filtered_address() {
        if let Some(address) = preferred_lan_ipv4().expect("Windows adapter scan should succeed") {
            println!("preferred LAN IPv4: {address}");
            assert!(usable_lan_ipv4(address));
        }
    }
}
