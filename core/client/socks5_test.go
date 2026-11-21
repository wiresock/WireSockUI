package main

import (
	"bytes"
	"encoding/binary"
	"io"
	"net"
	"testing"
	"time"
)

func TestSocks5UDPAssociateRoundTrip(t *testing.T) {
	pc, err := listenSocks5UDP("127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer pc.Close()
	s := pc.(*socks5PacketConn)

	ctl, err := net.Dial("tcp", s.listener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer ctl.Close()
	_ = ctl.SetDeadline(time.Now().Add(3 * time.Second))

	// greeting with no-auth + user/pass offered
	if _, err := ctl.Write([]byte{5, 2, 0, 2}); err != nil {
		t.Fatal(err)
	}
	resp := make([]byte, 2)
	if _, err := io.ReadFull(ctl, resp); err != nil || !bytes.Equal(resp, []byte{5, 0}) {
		t.Fatalf("greeting reply %v %v", resp, err)
	}
	// UDP ASSOCIATE 0.0.0.0:0
	if _, err := ctl.Write([]byte{5, 3, 0, 1, 0, 0, 0, 0, 0, 0}); err != nil {
		t.Fatal(err)
	}
	reply := make([]byte, 10)
	if _, err := io.ReadFull(ctl, reply); err != nil || reply[1] != 0 || reply[3] != 1 {
		t.Fatalf("associate reply %v %v", reply, err)
	}
	relay := &net.UDPAddr{IP: net.IP(reply[4:8]), Port: int(binary.BigEndian.Uint16(reply[8:10]))}

	client, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()

	// client -> proxy: header for destination 45.196.97.204:51830 + payload
	header := []byte{0, 0, 0, 1, 45, 196, 97, 204, 0xCA, 0x76}
	payload := []byte("wireguard-handshake")
	if _, err := client.WriteTo(append(append([]byte{}, header...), payload...), relay); err != nil {
		t.Fatal(err)
	}
	buf := make([]byte, 2048)
	_ = s.SetReadDeadline(time.Now().Add(3 * time.Second))
	n, from, err := pc.ReadFrom(buf)
	if err != nil || !bytes.Equal(buf[:n], payload) {
		t.Fatalf("ReadFrom got %q from %v err %v", buf[:n], from, err)
	}

	// proxy -> client: reply must carry the same header
	if _, err := pc.WriteTo([]byte("response"), from); err != nil {
		t.Fatal(err)
	}
	_ = client.SetReadDeadline(time.Now().Add(3 * time.Second))
	n, _, err = client.ReadFrom(buf)
	if err != nil || !bytes.Equal(buf[:n], append(append([]byte{}, header...), []byte("response")...)) {
		t.Fatalf("client got %v err %v", buf[:n], err)
	}
}

func TestSocks5RejectsConnectAndBadHeaders(t *testing.T) {
	pc, err := listenSocks5UDP("127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer pc.Close()
	ctl, err := net.Dial("tcp", pc.(*socks5PacketConn).listener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer ctl.Close()
	_ = ctl.SetDeadline(time.Now().Add(3 * time.Second))
	_, _ = ctl.Write([]byte{5, 1, 0})
	resp := make([]byte, 2)
	_, _ = io.ReadFull(ctl, resp)
	_, _ = ctl.Write([]byte{5, 1, 0, 1, 1, 2, 3, 4, 0, 80}) // CONNECT
	reply := make([]byte, 10)
	if _, err := io.ReadFull(ctl, reply); err != nil || reply[1] != 7 {
		t.Fatalf("CONNECT should be refused with 7, got %v %v", reply, err)
	}

	for _, bad := range [][]byte{{1, 0, 0, 1}, {0, 0, 1, 1, 1, 2, 3, 4, 0, 1}, {0, 0, 0, 9}, {0, 0, 0, 1, 1}} {
		if _, err := parseSocks5UDPHeader(bad); err == nil {
			t.Fatalf("header %v should be rejected", bad)
		}
	}
	if _, err := listenSocks5UDP("0.0.0.0:0"); err == nil {
		t.Fatal("non-loopback listen address must be refused")
	}
}
