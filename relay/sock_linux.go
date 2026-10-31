package main

import (
	"errors"
	"net"
	"net/netip"
	"os"
	"time"
)

type netUDP struct {
	c       *net.UDPConn
	timeout time.Duration
}

func listen(ip [4]byte, port int) (packetConn, error) {
	c, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IP(ip[:]), Port: port})
	if err != nil {
		return nil, err
	}
	return &netUDP{c: c}, nil
}

func (u *netUDP) ReadFrom(b []byte) (int, netip.AddrPort, error) {
	if u.timeout > 0 {
		u.c.SetReadDeadline(time.Now().Add(u.timeout))
	}
	n, from, err := u.c.ReadFromUDPAddrPort(b)
	return n, netip.AddrPortFrom(from.Addr().Unmap(), from.Port()), err
}

func (u *netUDP) WriteTo(b []byte, to netip.AddrPort) error {
	_, err := u.c.WriteToUDPAddrPort(b, to)
	return err
}

func (u *netUDP) SetReadTimeout(d time.Duration) error {
	u.timeout = d
	return nil
}

func (u *netUDP) LocalAddr() netip.AddrPort {
	return u.c.LocalAddr().(*net.UDPAddr).AddrPort()
}

func (u *netUDP) Close() error { return u.c.Close() }

func isTimeout(err error) bool { return errors.Is(err, os.ErrDeadlineExceeded) }

func (u *netUDP) isClosed(err error) bool { return errors.Is(err, net.ErrClosed) }
