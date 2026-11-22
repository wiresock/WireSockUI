package main

// turnBridge connects an in-process wireguard-go device to the TURN streams.
//
// It is both a conn.Bind (what wireguard-go sends and receives through) and a
// net.PacketConn (what the existing stream code uses as its local WireGuard listener):
//
//	wireguard-go --Bind.Send--> outCh --PacketConn.ReadFrom--> TURN streams --> server
//	server --> TURN streams --PacketConn.WriteTo--> inCh --ReceiveFunc--> wireguard-go
//
// There are no sockets on this side at all, so nothing can loop through the tunnel.

import (
	"errors"
	"net"
	"net/netip"
	"sync"
	"time"

	"golang.zx2c4.com/wireguard/conn"
)

const turnBridgeEndpointString = "192.0.2.1:51820" // TEST-NET-1 placeholder, never dialled

var turnBridgeAddr = &net.UDPAddr{IP: net.IPv4(192, 0, 2, 1), Port: 51820}

type turnBridge struct {
	outCh chan []byte
	inCh  chan []byte

	mu     sync.Mutex
	open   bool
	closed chan struct{} // closed while the Bind is closed
	dead   chan struct{} // closed when the PacketConn side is closed
}

func newTurnBridge() *turnBridge {
	c := make(chan struct{})
	close(c)
	return &turnBridge{
		outCh:  make(chan []byte, 1024),
		inCh:   make(chan []byte, 1024),
		closed: c,
		dead:   make(chan struct{}),
	}
}

// ---- conn.Bind ----

type turnBridgeEndpoint struct{}

func (turnBridgeEndpoint) ClearSrc()           {}
func (turnBridgeEndpoint) SrcToString() string { return "" }
func (turnBridgeEndpoint) DstToString() string { return turnBridgeEndpointString }
func (turnBridgeEndpoint) DstToBytes() []byte  { return []byte{192, 0, 2, 1, 0xCA, 0x6C} }
func (turnBridgeEndpoint) DstIP() netip.Addr   { return netip.AddrFrom4([4]byte{192, 0, 2, 1}) }
func (turnBridgeEndpoint) SrcIP() netip.Addr   { return netip.Addr{} }

func (b *turnBridge) Open(uint16) ([]conn.ReceiveFunc, uint16, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.open {
		return nil, 0, conn.ErrBindAlreadyOpen
	}
	b.open = true
	closed := make(chan struct{})
	b.closed = closed
	recv := func(packets [][]byte, sizes []int, eps []conn.Endpoint) (int, error) {
		select {
		case <-closed:
			return 0, net.ErrClosed
		case <-b.dead:
			return 0, net.ErrClosed
		case p := <-b.inCh:
			sizes[0] = copy(packets[0], p)
			eps[0] = turnBridgeEndpoint{}
			return 1, nil
		}
	}
	return []conn.ReceiveFunc{recv}, 51820, nil
}

func (b *turnBridge) Close() error {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.open {
		close(b.closed)
		b.open = false
	}
	return nil
}

func (b *turnBridge) SetMark(uint32) error { return nil }
func (b *turnBridge) BatchSize() int       { return 1 }

func (b *turnBridge) ParseEndpoint(string) (conn.Endpoint, error) {
	return turnBridgeEndpoint{}, nil
}

func (b *turnBridge) Send(bufs [][]byte, _ conn.Endpoint) error {
	for _, buf := range bufs {
		p := append([]byte(nil), buf...)
		select {
		case b.outCh <- p:
		case <-b.dead:
			return net.ErrClosed
		default:
			// Streams are saturated; dropping is what a congested UDP path would do.
		}
	}
	return nil
}

// ---- net.PacketConn (local side of the TURN stream code) ----

func (b *turnBridge) ReadFrom(p []byte) (int, net.Addr, error) {
	select {
	case pkt := <-b.outCh:
		return copy(p, pkt), turnBridgeAddr, nil
	case <-b.dead:
		return 0, nil, net.ErrClosed
	}
}

func (b *turnBridge) WriteTo(p []byte, _ net.Addr) (int, error) {
	pkt := append([]byte(nil), p...)
	select {
	case b.inCh <- pkt:
		return len(p), nil
	case <-b.dead:
		return 0, net.ErrClosed
	default:
		return 0, errors.New("wireguard receive queue full")
	}
}

func (b *turnBridge) closePacketConn() {
	b.mu.Lock()
	defer b.mu.Unlock()
	select {
	case <-b.dead:
	default:
		close(b.dead)
	}
}

// PacketConn.Close; the Bind has its own Close above, so expose this one via a wrapper.
type turnBridgePacketConn struct{ *turnBridge }

func (c turnBridgePacketConn) Close() error                     { c.closePacketConn(); return nil }
func (c turnBridgePacketConn) LocalAddr() net.Addr              { return turnBridgeAddr }
func (c turnBridgePacketConn) SetDeadline(time.Time) error      { return nil }
func (c turnBridgePacketConn) SetReadDeadline(time.Time) error  { return nil }
func (c turnBridgePacketConn) SetWriteDeadline(time.Time) error { return nil }
