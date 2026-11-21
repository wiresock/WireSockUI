//go:build windows

package main

import (
	"errors"
	"fmt"
	"log"
	"net"
	"os/exec"
	"sync/atomic"
	"time"

	"golang.org/x/sys/windows"
	"golang.zx2c4.com/wireguard/tun"
)

const wintunAdapterName = "cronixWG VK TURN"

type wintunPacketConn struct {
	dev    tun.Device
	fake   *net.UDPAddr
	bufs   [][]byte
	sizes  []int
	ipID   atomic.Uint32
	closed atomic.Bool

	rxPackets, txPackets, ignored atomic.Int64
	done                          chan struct{}
}

// listenWintun creates the adapter, gives it local/30 and serves fake as the WireGuard peer.
func listenWintun(peer string) (net.PacketConn, error) {
	local, fake, err := wintunLinkAddresses(peer)
	if err != nil {
		return nil, err
	}
	dev, err := tun.CreateTUN(wintunAdapterName, 1420)
	if err != nil {
		return nil, fmt.Errorf("create Wintun adapter (is wintun.dll next to vkturn-core.exe and are we elevated?): %w", err)
	}
	name, err := dev.Name()
	if err != nil {
		_ = dev.Close()
		return nil, err
	}
	// netsh keeps this dependency-free; the adapter disappears with the process.
	// Duplicate address detection would keep the address "tentative" (and the /30 route
	// missing) for about a second; WireSock started in that window routes the Endpoint
	// through the default gateway instead of this adapter, so turn it off.
	if out, err := exec.Command("netsh", "interface", "ipv4", "set", "interface", name,
		"dadtransmits=0", "routerdiscovery=disabled").CombinedOutput(); err != nil {
		log.Printf("[WINTUN] could not disable duplicate address detection: %v: %s", err, out)
	}
	out, err := exec.Command("netsh", "interface", "ipv4", "set", "address",
		"name="+name, "source=static", "address="+local.String(), "mask=255.255.255.252").CombinedOutput()
	if err != nil {
		_ = dev.Close()
		return nil, fmt.Errorf("assign %s/30 to %q: %v: %s", local, name, err, out)
	}
	if err := waitForWintunRoute(name, fake.IP, 15*time.Second); err != nil {
		_ = dev.Close()
		return nil, err
	}
	log.Printf("[WINTUN] adapter %q up: local %s/30, fake WireGuard peer %s", name, local, fake)

	c := &wintunPacketConn{
		dev:   dev,
		fake:  fake,
		bufs:  [][]byte{make([]byte, 65535)},
		sizes: make([]int, 1),
		done:  make(chan struct{}),
	}
	go c.statsLoop()
	return c, nil
}

// waitForWintunRoute blocks until Windows routes peer through the named adapter, so the
// WireSock tunnel is never started before its Endpoint resolves to this adapter.
func waitForWintunRoute(name string, peer net.IP, timeout time.Duration) error {
	sa := &windows.SockaddrInet4{}
	copy(sa.Addr[:], peer.To4())
	start := time.Now()
	for {
		iface, ifErr := net.InterfaceByName(name)
		var best uint32
		routeErr := windows.GetBestInterfaceEx(sa, &best)
		if ifErr == nil && routeErr == nil && int(best) == iface.Index {
			// Give the NDIS filter of WireSock a moment to attach to the new adapter too.
			time.Sleep(500 * time.Millisecond)
			log.Printf("[WINTUN] route to %s via %q ready after %v", peer, name, time.Since(start).Truncate(time.Millisecond))
			return nil
		}
		if time.Since(start) > timeout {
			return fmt.Errorf("Windows did not route %s through %q within %v (interface: %v, best interface %d, route error: %v)",
				peer, name, timeout, ifErr, best, routeErr)
		}
		time.Sleep(100 * time.Millisecond)
	}
}

func (c *wintunPacketConn) statsLoop() {
	var last [3]int64
	t := time.NewTicker(5 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-c.done:
			return
		case <-t.C:
		}
		cur := [3]int64{c.rxPackets.Load(), c.txPackets.Load(), c.ignored.Load()}
		if cur != last {
			log.Printf("[WINTUN] from WireGuard %d pkts, to WireGuard %d pkts, ignored %d other packets", cur[0], cur[1], cur[2])
			last = cur
		}
	}
}

func (c *wintunPacketConn) ReadFrom(b []byte) (int, net.Addr, error) {
	for {
		n, err := c.dev.Read(c.bufs, c.sizes, 0)
		if err != nil {
			return 0, nil, err
		}
		if n == 0 {
			continue
		}
		payload, src, ok := parseIPv4UDP(c.bufs[0][:c.sizes[0]], c.fake)
		if !ok {
			c.ignored.Add(1)
			continue
		}
		if c.rxPackets.Add(1) == 1 {
			log.Printf("[WINTUN] first WireGuard packet from %s (%d bytes)", src, len(payload))
		}
		return copy(b, payload), src, nil
	}
}

func (c *wintunPacketConn) WriteTo(p []byte, addr net.Addr) (int, error) {
	dst, ok := addr.(*net.UDPAddr)
	if !ok {
		return 0, errors.New("wintun ingress: destination is not a UDP address")
	}
	pkt, err := buildIPv4UDP(c.fake, dst, p, uint16(c.ipID.Add(1)))
	if err != nil {
		return 0, err
	}
	if _, err := c.dev.Write([][]byte{pkt}, 0); err != nil {
		return 0, err
	}
	c.txPackets.Add(1)
	return len(p), nil
}

func (c *wintunPacketConn) Close() error {
	if c.closed.Swap(true) {
		return nil
	}
	close(c.done)
	return c.dev.Close()
}

func (c *wintunPacketConn) LocalAddr() net.Addr                { return c.fake }
func (c *wintunPacketConn) SetDeadline(t time.Time) error      { return nil }
func (c *wintunPacketConn) SetReadDeadline(t time.Time) error  { return nil }
func (c *wintunPacketConn) SetWriteDeadline(t time.Time) error { return nil }
