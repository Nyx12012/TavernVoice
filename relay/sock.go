package main

import (
	"net/netip"
	"time"
)

type packetConn interface {
	ReadFrom(b []byte) (int, netip.AddrPort, error)
	WriteTo(b []byte, to netip.AddrPort) error
	SetReadTimeout(d time.Duration) error
	LocalAddr() netip.AddrPort
	Close() error
	isClosed(err error) bool
}
