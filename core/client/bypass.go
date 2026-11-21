package main

// Keep the core's own traffic (TURN servers, VK API, DNS) off the tunnel it feeds.
//
// In WireSock's virtual adapter mode the default route points into the WireSock adapter
// and DisallowedApps does not keep vkturn-core out of it, so every TURN re-allocation after
// the tunnel came up looped back into the tunnel ("failed to allocate: all retransmissions
// failed"). Like WireGuard for Windows, the core pins its sockets to the physical
// interface chosen before the tunnel starts (IP_UNICAST_IF / IPV6_UNICAST_IF).

import (
	"net"
	"syscall"
)

// bypassDialer returns a dialer whose sockets are pinned to the bypass interface (if any).
func bypassDialer() *net.Dialer {
	return &net.Dialer{Control: bypassControl}
}

// bypassDialUDP is net.DialUDP("udp", nil, raddr) with the bypass binding applied.
func bypassDialUDP(raddr *net.UDPAddr) (*net.UDPConn, error) {
	c, err := bypassDialer().Dial("udp", raddr.String())
	if err != nil {
		return nil, err
	}
	return c.(*net.UDPConn), nil
}

func bypassControl(network, address string, c syscall.RawConn) error {
	return bindToBypassInterface(network, c)
}
