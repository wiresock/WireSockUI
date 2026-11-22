//go:build !windows

package main

import "errors"

type wgTunnel struct{}

func startWireGuard(*wgConfig, *turnBridge) (*wgTunnel, error) {
	return nil, errors.New("-wg-config is only implemented on Windows")
}

func (*wgTunnel) Close() {}
