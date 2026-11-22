//go:build !windows

package main

import (
	"errors"
	"golang.zx2c4.com/wireguard/conn"
)

type wgTunnel struct{ failure chan error }

func startWireGuard(*wgConfig, conn.Bind) (*wgTunnel, error) {
	return nil, errors.New("-wg-config is only implemented on Windows")
}

func (*wgTunnel) Close() {}
