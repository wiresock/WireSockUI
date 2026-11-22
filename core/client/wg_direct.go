package main

import (
	"context"
	"fmt"
	"log"
	"net"

	"golang.zx2c4.com/wireguard/conn"
)

// Pin native WireGuard UDP sockets too. Open is called by the device after the
// adapter routes are installed, so the saved interface must be used explicitly.
type directBind struct{ conn.Bind }

func (b *directBind) BindSocketToInterface4(index uint32, blackhole bool) error {
	if p, ok := b.Bind.(conn.BindSocketToInterface); ok {
		return p.BindSocketToInterface4(index, blackhole)
	}
	return fmt.Errorf("bind does not support interface selection")
}
func (b *directBind) BindSocketToInterface6(index uint32, blackhole bool) error {
	if p, ok := b.Bind.(conn.BindSocketToInterface); ok {
		return p.BindSocketToInterface6(index, blackhole)
	}
	return fmt.Errorf("bind does not support interface selection")
}
func (b *directBind) Open(port uint16) ([]conn.ReceiveFunc, uint16, error) {
	f, p, e := b.Bind.Open(port)
	if e != nil {
		return nil, 0, e
	}
	if e = pinDirectBind(b.Bind); e != nil {
		b.Close()
		return nil, 0, e
	}
	return f, p, nil
}

func runDirectWireGuard(ctx context.Context, path string) error {
	cfg, err := loadWGConfig(path)
	if err != nil {
		return err
	}
	if cfg.Endpoint == "" {
		return fmt.Errorf("[Peer] Endpoint is required for direct WireGuard")
	}
	address, err := net.ResolveUDPAddr("udp", cfg.Endpoint)
	if err != nil {
		return err
	}
	cfg.Endpoint = address.String()
	cfg.Direct = true
	if err = initBypassInterface(); err != nil {
		return err
	}
	tunnel, err := startWireGuard(cfg, &directBind{conn.NewDefaultBind()})
	if err != nil {
		return err
	}
	defer tunnel.Close()
	log.Print("[WG] direct UDP transport started; waiting for handshake")
	select {
	case <-ctx.Done():
		return nil
	case err := <-tunnel.failure:
		return err
	}
}
