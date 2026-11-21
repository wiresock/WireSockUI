package main

// Minimal SOCKS5 server (RFC 1928, no authentication, UDP ASSOCIATE only) used as the
// WireGuard ingress when the host is WireSock.
//
// WireSock refuses peers whose Endpoint is a loopback address: before starting it looks up
// the physical route to the endpoint ("Failed to figure out the route to the VPN server").
// Its SOCKS5 support (Socks5Proxy + Socks5ProxyAllTraffic) lets the profile keep a real,
// routable Endpoint while every WireGuard datagram is actually sent to this proxy. The
// proxy ignores the requested destination and hands the payload to the TURN streams, the
// same way the plain UDP -listen socket does.
//
// socks5PacketConn implements net.PacketConn so it can replace that listener unchanged:
// ReadFrom strips the SOCKS5 UDP header, WriteTo adds it back (with the destination the
// client asked for as the source address, as RFC 1928 requires).

import (
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"sync"
	"sync/atomic"
	"time"
)

const (
	socks5Version       = 5
	socks5CmdUDPAssoc   = 3
	socks5AtypIPv4      = 1
	socks5AtypDomain    = 3
	socks5AtypIPv6      = 4
	socks5MaxHeaderSize = 4 + 1 + 255 + 2
)

type socks5PacketConn struct {
	relay    *net.UDPConn
	listener net.Listener
	header   atomic.Value // []byte: SOCKS5 UDP header echoed on replies
	readBuf  []byte
	closed   atomic.Bool
	done     chan struct{}
	wg       sync.WaitGroup

	rxPackets, rxBytes, rxDropped, txPackets, txBytes, txErrors atomic.Int64
	dropLogs                                                    atomic.Int32
}

// listenSocks5UDP starts the TCP control listener and the UDP relay on the same loopback host.
func listenSocks5UDP(address string) (net.PacketConn, error) {
	tcpAddr, err := net.ResolveTCPAddr("tcp", address)
	if err != nil {
		return nil, err
	}
	if !tcpAddr.IP.IsLoopback() {
		return nil, fmt.Errorf("-socks5 must listen on a loopback address, got %s", address)
	}
	listener, err := net.ListenTCP("tcp", tcpAddr)
	if err != nil {
		return nil, err
	}
	// The UDP relay shares the TCP port: some SOCKS5 clients (WireSock among them, it
	// seems) send datagrams to the proxy address itself instead of the BND.ADDR reply.
	boundTCP := listener.Addr().(*net.TCPAddr)
	relay, err := net.ListenUDP("udp", &net.UDPAddr{IP: boundTCP.IP, Port: boundTCP.Port})
	if err != nil {
		_ = listener.Close()
		return nil, err
	}
	c := &socks5PacketConn{relay: relay, listener: listener, readBuf: make([]byte, 65535), done: make(chan struct{})}
	c.wg.Add(2)
	go c.acceptLoop()
	go c.statsLoop()
	log.Printf("[SOCKS5] listening on %s (UDP relay %s)", listener.Addr(), relay.LocalAddr())
	return c, nil
}

// statsLoop logs relay traffic counters every 5 s while they change, so a stalled
// WireGuard handshake shows whether packets reach the proxy and whether replies leave it.
func (c *socks5PacketConn) statsLoop() {
	defer c.wg.Done()
	var last [6]int64
	t := time.NewTicker(5 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-c.done:
			return
		case <-t.C:
		}
		cur := [6]int64{c.rxPackets.Load(), c.rxBytes.Load(), c.rxDropped.Load(),
			c.txPackets.Load(), c.txBytes.Load(), c.txErrors.Load()}
		if cur != last {
			log.Printf("[SOCKS5] relay: from WireGuard %d pkts/%d B (dropped %d), to WireGuard %d pkts/%d B (errors %d)",
				cur[0], cur[1], cur[2], cur[3], cur[4], cur[5])
			last = cur
		}
	}
}

func (c *socks5PacketConn) logDrop(format string, args ...any) {
	c.rxDropped.Add(1)
	if c.dropLogs.Add(1) <= 10 || isDebug {
		log.Printf("[SOCKS5] dropped datagram: "+format, args...)
	}
}

func (c *socks5PacketConn) acceptLoop() {
	defer c.wg.Done()
	for {
		conn, err := c.listener.Accept()
		if err != nil {
			if !c.closed.Load() {
				log.Printf("[SOCKS5] accept: %v", err)
			}
			return
		}
		go c.serveControl(conn)
	}
}

// serveControl performs the SOCKS5 handshake and keeps the association open until the
// client closes the TCP connection (RFC 1928 ties the UDP association to it).
func (c *socks5PacketConn) serveControl(conn net.Conn) {
	defer conn.Close()
	if ra, ok := conn.RemoteAddr().(*net.TCPAddr); !ok || !ra.IP.IsLoopback() {
		return
	}
	_ = conn.SetDeadline(time.Now().Add(10 * time.Second))

	// Greeting: VER NMETHODS METHODS...
	hdr := make([]byte, 2)
	if _, err := io.ReadFull(conn, hdr); err != nil || hdr[0] != socks5Version {
		return
	}
	methods := make([]byte, hdr[1])
	if _, err := io.ReadFull(conn, methods); err != nil {
		return
	}
	noAuth := false
	for _, m := range methods {
		if m == 0 {
			noAuth = true
		}
	}
	if !noAuth {
		_, _ = conn.Write([]byte{socks5Version, 0xFF})
		log.Printf("[SOCKS5] client offered no acceptable auth method %v", methods)
		return
	}
	if _, err := conn.Write([]byte{socks5Version, 0}); err != nil {
		return
	}

	// Request: VER CMD RSV ATYP DST.ADDR DST.PORT
	req := make([]byte, 4)
	if _, err := io.ReadFull(conn, req); err != nil || req[0] != socks5Version {
		return
	}
	if _, err := readSocks5Address(conn, req[3]); err != nil {
		return
	}
	if req[1] != socks5CmdUDPAssoc {
		// Only UDP is relayed; WireGuard never needs CONNECT.
		_, _ = conn.Write([]byte{socks5Version, 7, 0, socks5AtypIPv4, 0, 0, 0, 0, 0, 0})
		log.Printf("[SOCKS5] rejected command %d (only UDP ASSOCIATE is supported)", req[1])
		return
	}

	relayAddr := c.relay.LocalAddr().(*net.UDPAddr)
	reply := []byte{socks5Version, 0, 0}
	if ip4 := relayAddr.IP.To4(); ip4 != nil {
		reply = append(reply, socks5AtypIPv4)
		reply = append(reply, ip4...)
	} else {
		reply = append(reply, socks5AtypIPv6)
		reply = append(reply, relayAddr.IP.To16()...)
	}
	reply = binary.BigEndian.AppendUint16(reply, uint16(relayAddr.Port))
	if _, err := conn.Write(reply); err != nil {
		return
	}
	log.Printf("[SOCKS5] UDP association opened for %s", conn.RemoteAddr())

	_ = conn.SetDeadline(time.Time{})
	_, _ = io.Copy(io.Discard, conn) // hold until the client closes
	log.Printf("[SOCKS5] UDP association closed for %s", conn.RemoteAddr())
}

func readSocks5Address(r io.Reader, atyp byte) ([]byte, error) {
	var n int
	switch atyp {
	case socks5AtypIPv4:
		n = 4
	case socks5AtypIPv6:
		n = 16
	case socks5AtypDomain:
		l := make([]byte, 1)
		if _, err := io.ReadFull(r, l); err != nil {
			return nil, err
		}
		n = int(l[0])
	default:
		return nil, fmt.Errorf("unsupported SOCKS5 address type %d", atyp)
	}
	buf := make([]byte, n+2)
	_, err := io.ReadFull(r, buf)
	return buf, err
}

// parseSocks5UDPHeader returns the header length of a SOCKS5 UDP datagram.
func parseSocks5UDPHeader(p []byte) (int, error) {
	if len(p) < 4 || p[0] != 0 || p[1] != 0 {
		return 0, errors.New("malformed SOCKS5 UDP header")
	}
	if p[2] != 0 {
		return 0, errors.New("fragmented SOCKS5 UDP datagrams are not supported")
	}
	var n int
	switch p[3] {
	case socks5AtypIPv4:
		n = 4 + 4 + 2
	case socks5AtypIPv6:
		n = 4 + 16 + 2
	case socks5AtypDomain:
		if len(p) < 5 {
			return 0, errors.New("short SOCKS5 domain header")
		}
		n = 4 + 1 + int(p[4]) + 2
	default:
		return 0, fmt.Errorf("unsupported SOCKS5 address type %d", p[3])
	}
	if len(p) < n {
		return 0, errors.New("short SOCKS5 UDP header")
	}
	return n, nil
}

func (c *socks5PacketConn) ReadFrom(b []byte) (int, net.Addr, error) {
	for {
		n, addr, err := c.relay.ReadFromUDP(c.readBuf)
		if err != nil {
			return 0, nil, err
		}
		if !addr.IP.IsLoopback() {
			c.logDrop("non-loopback source %s", addr)
			continue
		}
		h, err := parseSocks5UDPHeader(c.readBuf[:n])
		if err != nil {
			c.logDrop("from %s (%d bytes, first bytes % x): %v", addr, n, c.readBuf[:min(n, 12)], err)
			continue
		}
		c.rxPackets.Add(1)
		c.rxBytes.Add(int64(n - h))
		if c.rxPackets.Load() == 1 {
			log.Printf("[SOCKS5] first WireGuard datagram from %s, %d bytes, header % x", addr, n-h, c.readBuf[:h])
		}
		if prev, _ := c.header.Load().([]byte); len(prev) != h || string(prev) != string(c.readBuf[:h]) {
			c.header.Store(append([]byte(nil), c.readBuf[:h]...))
		}
		return copy(b, c.readBuf[h:n]), addr, nil
	}
}

func (c *socks5PacketConn) WriteTo(p []byte, addr net.Addr) (int, error) {
	header, _ := c.header.Load().([]byte)
	if header == nil {
		return 0, errors.New("no SOCKS5 UDP association yet")
	}
	out := make([]byte, 0, len(header)+len(p))
	out = append(out, header...)
	out = append(out, p...)
	if _, err := c.relay.WriteTo(out, addr); err != nil {
		if c.txErrors.Add(1) <= 10 {
			log.Printf("[SOCKS5] reply to %s failed: %v", addr, err)
		}
		return 0, err
	}
	c.txPackets.Add(1)
	c.txBytes.Add(int64(len(p)))
	return len(p), nil
}

func (c *socks5PacketConn) Close() error {
	if c.closed.Swap(true) {
		return nil
	}
	close(c.done)
	err1 := c.listener.Close()
	err2 := c.relay.Close()
	c.wg.Wait()
	if err1 != nil {
		return err1
	}
	return err2
}

func (c *socks5PacketConn) LocalAddr() net.Addr                { return c.relay.LocalAddr() }
func (c *socks5PacketConn) SetDeadline(t time.Time) error      { return c.relay.SetDeadline(t) }
func (c *socks5PacketConn) SetReadDeadline(t time.Time) error  { return c.relay.SetReadDeadline(t) }
func (c *socks5PacketConn) SetWriteDeadline(t time.Time) error { return c.relay.SetWriteDeadline(t) }
