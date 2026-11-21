//go:build !windows

package main

import (
	"errors"
	"net"
)

func listenWintun(string) (net.PacketConn, error) {
	return nil, errors.New("-wintun is only available on Windows")
}
