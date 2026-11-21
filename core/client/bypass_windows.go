//go:build windows

package main

import (
	"encoding/binary"
	"fmt"
	"log"
	"net"
	"strings"
	"sync/atomic"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
)

const (
	ipUnicastIf   = 31 // IP_UNICAST_IF
	ipv6UnicastIf = 31 // IPV6_UNICAST_IF
)

var bypassIfIndex atomic.Uint32

// initBypassInterface records the interface Windows uses for the internet right now,
// before any tunnel exists, and pins all later core sockets to it.
func initBypassInterface() error {
	sa := &windows.SockaddrInet4{Addr: [4]byte{8, 8, 8, 8}}
	var idx uint32
	if err := windows.GetBestInterfaceEx(sa, &idx); err != nil {
		return fmt.Errorf("find the internet interface: %w", err)
	}
	name := fmt.Sprint(idx)
	if iface, err := net.InterfaceByIndex(int(idx)); err == nil {
		name = fmt.Sprintf("%q (index %d)", iface.Name, idx)
	}
	bypassIfIndex.Store(idx)
	log.Printf("[BYPASS] core traffic pinned to %s, outside the VPN tunnel", name)
	return nil
}

func bindToBypassInterface(network string, c syscall.RawConn) error {
	idx := bypassIfIndex.Load()
	if idx == 0 {
		return nil
	}
	var sockErr error
	err := c.Control(func(fd uintptr) {
		h := windows.Handle(fd)
		if strings.HasSuffix(network, "6") {
			v := idx // IPV6_UNICAST_IF takes the index in host byte order
			sockErr = windows.Setsockopt(h, windows.IPPROTO_IPV6, ipv6UnicastIf, (*byte)(unsafe.Pointer(&v)), 4)
			return
		}
		// IP_UNICAST_IF takes the index in network byte order.
		var be [4]byte
		binary.BigEndian.PutUint32(be[:], idx)
		v := *(*uint32)(unsafe.Pointer(&be[0]))
		sockErr = windows.Setsockopt(h, windows.IPPROTO_IP, ipUnicastIf, (*byte)(unsafe.Pointer(&v)), 4)
		if sockErr != nil {
			return
		}
		if network == "udp" || network == "tcp" { // dual-stack sockets
			v6 := idx
			_ = windows.Setsockopt(h, windows.IPPROTO_IPV6, ipv6UnicastIf, (*byte)(unsafe.Pointer(&v6)), 4)
		}
	})
	if err != nil {
		return err
	}
	return sockErr
}
