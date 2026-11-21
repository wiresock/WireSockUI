package main

// IPv4/UDP framing for the -wintun ingress (see wintun_windows.go).
//
// WireSock does not send WireGuard through sockets: its NDIS filter builds the UDP packets
// and puts them straight onto the adapter that routes to the Endpoint, so a loopback
// endpoint or SOCKS5 proxy never reaches a local process. With -wintun the core owns a
// Wintun adapter (local 10.255.255.1/30) and pretends to be the WireGuard server at the
// other end of that link (10.255.255.2:51820). WireSock routes the Endpoint through the
// adapter, the core reads the raw IPv4/UDP packets, unwraps the WireGuard payload for the
// TURN streams and writes replies back as packets "from" the fake server.

import (
	"encoding/binary"
	"errors"
	"net"
)

const (
	ipv4HeaderLen = 20
	udpHeaderLen  = 8
	ipProtoUDP    = 17
)

// parseIPv4UDP extracts the UDP payload of an unfragmented IPv4 packet addressed to dst.
// It returns ok=false for anything else (IPv6, ICMP, broadcasts, other ports...).
func parseIPv4UDP(pkt []byte, dst *net.UDPAddr) (payload []byte, src *net.UDPAddr, ok bool) {
	if len(pkt) < ipv4HeaderLen+udpHeaderLen || pkt[0]>>4 != 4 {
		return nil, nil, false
	}
	ihl := int(pkt[0]&0x0f) * 4
	total := int(binary.BigEndian.Uint16(pkt[2:4]))
	if ihl < ipv4HeaderLen || total > len(pkt) || total < ihl+udpHeaderLen || pkt[9] != ipProtoUDP {
		return nil, nil, false
	}
	if flagsFrag := binary.BigEndian.Uint16(pkt[6:8]); flagsFrag&0x3fff != 0 { // MF or offset set
		return nil, nil, false
	}
	if !net.IP(pkt[16:20]).Equal(dst.IP.To4()) {
		return nil, nil, false
	}
	udp := pkt[ihl:total]
	if int(binary.BigEndian.Uint16(udp[2:4])) != dst.Port {
		return nil, nil, false
	}
	udpLen := int(binary.BigEndian.Uint16(udp[4:6]))
	if udpLen < udpHeaderLen || udpLen > len(udp) {
		return nil, nil, false
	}
	src = &net.UDPAddr{
		IP:   append(net.IP(nil), pkt[12:16]...),
		Port: int(binary.BigEndian.Uint16(udp[0:2])),
	}
	return udp[udpHeaderLen:udpLen], src, true
}

// buildIPv4UDP returns an IPv4/UDP packet carrying payload from src to dst, with valid
// IP and UDP checksums.
func buildIPv4UDP(src, dst *net.UDPAddr, payload []byte, id uint16) ([]byte, error) {
	s4, d4 := src.IP.To4(), dst.IP.To4()
	if s4 == nil || d4 == nil {
		return nil, errors.New("wintun ingress supports IPv4 only")
	}
	total := ipv4HeaderLen + udpHeaderLen + len(payload)
	if total > 0xffff {
		return nil, errors.New("packet too large")
	}
	pkt := make([]byte, total)
	pkt[0] = 0x45
	binary.BigEndian.PutUint16(pkt[2:4], uint16(total))
	binary.BigEndian.PutUint16(pkt[4:6], id)
	pkt[8] = 64 // TTL
	pkt[9] = ipProtoUDP
	copy(pkt[12:16], s4)
	copy(pkt[16:20], d4)
	binary.BigEndian.PutUint16(pkt[10:12], checksum16(pkt[:ipv4HeaderLen], 0))

	udp := pkt[ipv4HeaderLen:]
	binary.BigEndian.PutUint16(udp[0:2], uint16(src.Port))
	binary.BigEndian.PutUint16(udp[2:4], uint16(dst.Port))
	binary.BigEndian.PutUint16(udp[4:6], uint16(udpHeaderLen+len(payload)))
	copy(udp[udpHeaderLen:], payload)

	// UDP checksum over the pseudo-header (src, dst, proto, length) and the datagram.
	var pseudo uint32
	pseudo += uint32(binary.BigEndian.Uint16(s4[0:2])) + uint32(binary.BigEndian.Uint16(s4[2:4]))
	pseudo += uint32(binary.BigEndian.Uint16(d4[0:2])) + uint32(binary.BigEndian.Uint16(d4[2:4]))
	pseudo += ipProtoUDP + uint32(len(udp))
	sum := checksum16(udp, pseudo)
	if sum == 0 {
		sum = 0xffff
	}
	binary.BigEndian.PutUint16(udp[6:8], sum)
	return pkt, nil
}

func checksum16(b []byte, initial uint32) uint16 {
	sum := initial
	for i := 0; i+1 < len(b); i += 2 {
		sum += uint32(binary.BigEndian.Uint16(b[i:]))
	}
	if len(b)%2 == 1 {
		sum += uint32(b[len(b)-1]) << 8
	}
	for sum>>16 != 0 {
		sum = sum&0xffff + sum>>16
	}
	return ^uint16(sum)
}

// wintunLinkAddresses derives the adapter address (peer IP - 1, /30) from the fake peer.
func wintunLinkAddresses(peer string) (local net.IP, fake *net.UDPAddr, err error) {
	fake, err = net.ResolveUDPAddr("udp4", peer)
	if err != nil {
		return nil, nil, err
	}
	ip := fake.IP.To4()
	if ip == nil || !ip.IsPrivate() || ip[3]&3 != 2 {
		return nil, nil, errors.New("-wintun needs a private IPv4 that is the second host of a /30, e.g. 10.255.255.2:51820")
	}
	local = net.IPv4(ip[0], ip[1], ip[2], ip[3]-1).To4()
	return local, fake, nil
}
