//go:build windows

package main

import (
	"bufio"
	"fmt"
	"log"
	"net/netip"
	"strconv"
	"strings"
	"time"

	"golang.org/x/sys/windows"
	"golang.zx2c4.com/wireguard/device"
	"golang.zx2c4.com/wireguard/tun"
	"golang.zx2c4.com/wireguard/windows/tunnel/winipcfg"
)

const wgAdapterName = "cronixWG"

type wgTunnel struct {
	dev  *device.Device
	tun  tun.Device
	luid winipcfg.LUID
	done chan struct{}
}

// startWireGuard brings up the Wintun adapter and an in-process wireguard-go device whose
// only transport is the TURN bridge. Call initBypassInterface first so the core's own
// sockets are pinned to the physical interface before the default route moves here.
func startWireGuard(cfg *wgConfig, bridge *turnBridge) (*wgTunnel, error) {
	tdev, err := tun.CreateTUN(wgAdapterName, cfg.MTU)
	if err != nil {
		return nil, fmt.Errorf("create the %q adapter (wintun.dll next to vkturn-core.exe, elevated?): %w", wgAdapterName, err)
	}
	native, ok := tdev.(*tun.NativeTun)
	if !ok {
		_ = tdev.Close()
		return nil, fmt.Errorf("unexpected TUN implementation %T", tdev)
	}
	luid := winipcfg.LUID(native.LUID())

	logger := &device.Logger{
		Verbosef: func(string, ...any) {},
		Errorf:   func(f string, a ...any) { log.Printf("[WG] "+f, a...) },
	}
	if isDebug {
		logger.Verbosef = func(f string, a ...any) { log.Printf("[WG] "+f, a...) }
	}
	dev := device.NewDevice(tdev, bridge, logger)
	if err := dev.IpcSet(cfg.uapi()); err != nil {
		dev.Close()
		return nil, fmt.Errorf("configure WireGuard: %w", err)
	}
	if err := configureWGAdapter(luid, cfg); err != nil {
		dev.Close()
		return nil, err
	}
	if err := dev.Up(); err != nil {
		dev.Close()
		return nil, fmt.Errorf("start WireGuard: %w", err)
	}

	t := &wgTunnel{dev: dev, tun: tdev, luid: luid, done: make(chan struct{})}
	go t.statsLoop()
	log.Printf("[WG] tunnel %q up: %s, %d allowed prefix(es), DNS %v, MTU %d",
		wgAdapterName, joinPrefixes(cfg.Addresses), len(cfg.AllowedIPs), cfg.DNS, cfg.MTU)
	return t, nil
}

func configureWGAdapter(luid winipcfg.LUID, cfg *wgConfig) error {
	for _, family := range []winipcfg.AddressFamily{windows.AF_INET, windows.AF_INET6} {
		iface, err := luid.IPInterface(family)
		if err != nil {
			continue // the family may be unbound on this adapter
		}
		iface.UseAutomaticMetric = false
		iface.Metric = 0
		iface.DadTransmits = 0
		iface.RouterDiscoveryBehavior = winipcfg.RouterDiscoveryDisabled
		if family == windows.AF_INET {
			iface.NLMTU = uint32(cfg.MTU)
		} else if cfg.MTU >= 1280 {
			iface.NLMTU = uint32(cfg.MTU)
		}
		if err := iface.Set(); err != nil {
			log.Printf("[WG] adapter settings (family %d): %v", family, err)
		}
	}

	if err := luid.SetIPAddresses(cfg.Addresses); err != nil {
		return fmt.Errorf("assign %s: %w", joinPrefixes(cfg.Addresses), err)
	}

	var routes []*winipcfg.RouteData
	for _, p := range cfg.AllowedIPs {
		p = p.Masked()
		hop := netip.IPv4Unspecified()
		if p.Addr().Is6() {
			if !hasIPv6(cfg.Addresses) {
				continue // no IPv6 address on the tunnel: Windows refuses the route
			}
			hop = netip.IPv6Unspecified()
		}
		routes = append(routes, &winipcfg.RouteData{Destination: p, NextHop: hop, Metric: 0})
	}
	if err := luid.SetRoutes(routes); err != nil {
		return fmt.Errorf("add routes: %w", err)
	}

	if len(cfg.DNS) > 0 {
		var v4, v6 []netip.Addr
		for _, a := range cfg.DNS {
			if a.Is4() {
				v4 = append(v4, a)
			} else {
				v6 = append(v6, a)
			}
		}
		if len(v4) > 0 {
			if err := luid.SetDNS(windows.AF_INET, v4, nil); err != nil {
				log.Printf("[WG] set IPv4 DNS: %v", err)
			}
		}
		if len(v6) > 0 && hasIPv6(cfg.Addresses) {
			if err := luid.SetDNS(windows.AF_INET6, v6, nil); err != nil {
				log.Printf("[WG] set IPv6 DNS: %v", err)
			}
		}
	}
	return nil
}

// statsLoop reports transfer and handshake state for the UI every two seconds.
func (t *wgTunnel) statsLoop() {
	tick := time.NewTicker(2 * time.Second)
	defer tick.Stop()
	handshakeSeen := false
	for {
		select {
		case <-t.done:
			return
		case <-tick.C:
		}
		state, err := t.dev.IpcGet()
		if err != nil {
			continue
		}
		var rx, tx, hsSec int64
		sc := bufio.NewScanner(strings.NewReader(state))
		for sc.Scan() {
			k, v, _ := strings.Cut(sc.Text(), "=")
			n, _ := strconv.ParseInt(v, 10, 64)
			switch k {
			case "rx_bytes":
				rx = n
			case "tx_bytes":
				tx = n
			case "last_handshake_time_sec":
				hsSec = n
			}
		}
		ago := int64(-1)
		if hsSec > 0 {
			ago = int64(time.Since(time.Unix(hsSec, 0)).Seconds())
			if !handshakeSeen {
				handshakeSeen = true
				log.Printf("[WG] handshake with the server completed")
				emitEvent("wg_handshake", nil)
			}
		}
		emitEvent("wg_stats", map[string]any{"rx_bytes": rx, "tx_bytes": tx, "handshake_ago": ago})
	}
}

func (t *wgTunnel) Close() {
	select {
	case <-t.done:
		return
	default:
		close(t.done)
	}
	t.dev.Close() // closes the TUN device; Wintun removes the adapter and its routes
}

func hasIPv6(prefixes []netip.Prefix) bool {
	for _, p := range prefixes {
		if p.Addr().Is6() {
			return true
		}
	}
	return false
}

func joinPrefixes(prefixes []netip.Prefix) string {
	s := make([]string, len(prefixes))
	for i, p := range prefixes {
		s[i] = p.String()
	}
	return strings.Join(s, ", ")
}
