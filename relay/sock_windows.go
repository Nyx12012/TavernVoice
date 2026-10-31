package main

import (
	"errors"
	"fmt"
	"net/netip"
	"sync"
	"syscall"
	"time"
	"unsafe"
)

const (
	soRcvTimeo     = 0x1006
	sioUDPNetReset = syscall.IOC_IN | syscall.IOC_VENDOR | 15
	wsaETimedOut   = syscall.Errno(10060)
	wsaENotSock    = syscall.Errno(10038)
	wsaEIntr       = syscall.Errno(10004)
)

var wsaOnce sync.Once

type sockaddrIn struct {
	family uint16
	port   [2]byte
	addr   [4]byte
	zero   [8]byte
}

type winUDP struct {
	h      syscall.Handle
	local  netip.AddrPort
	closed bool
	mu     sync.Mutex
}

func listen(ip [4]byte, port int) (packetConn, error) {
	wsaOnce.Do(func() {
		var d syscall.WSAData
		syscall.WSAStartup(uint32(0x202), &d)
	})
	h, err := syscall.Socket(syscall.AF_INET, syscall.SOCK_DGRAM, syscall.IPPROTO_UDP)
	if err != nil {
		return nil, err
	}
	for _, ctl := range []uint32{syscall.SIO_UDP_CONNRESET, sioUDPNetReset} {
		off, ret := uint32(0), uint32(0)
		syscall.WSAIoctl(h, ctl, (*byte)(unsafe.Pointer(&off)), 4, nil, 0, &ret, nil, 0)
	}
	if err := syscall.Bind(h, &syscall.SockaddrInet4{Port: port, Addr: ip}); err != nil {
		syscall.Closesocket(h)
		return nil, err
	}
	local := netip.AddrPortFrom(netip.AddrFrom4(ip), uint16(port))
	if sa, err := syscall.Getsockname(h); err == nil {
		if in, ok := sa.(*syscall.SockaddrInet4); ok {
			local = netip.AddrPortFrom(netip.AddrFrom4(in.Addr), uint16(in.Port))
		}
	}
	return &winUDP{h: h, local: local}, nil
}

func (u *winUDP) ReadFrom(b []byte) (int, netip.AddrPort, error) {
	var from sockaddrIn
	fromLen := int32(unsafe.Sizeof(from))
	buf := syscall.WSABuf{Len: uint32(len(b)), Buf: &b[0]}
	var n, flags uint32
	err := syscall.WSARecvFrom(u.h, &buf, 1, &n, &flags, (*syscall.RawSockaddrAny)(unsafe.Pointer(&from)), &fromLen, nil, nil)
	if err != nil {
		return 0, netip.AddrPort{}, err
	}
	port := uint16(from.port[0])<<8 | uint16(from.port[1])
	return int(n), netip.AddrPortFrom(netip.AddrFrom4(from.addr), port), nil
}

func (u *winUDP) WriteTo(b []byte, to netip.AddrPort) error {
	if !to.Addr().Unmap().Is4() {
		return fmt.Errorf("%s is not IPv4", to)
	}
	sa := sockaddrIn{family: syscall.AF_INET, addr: to.Addr().Unmap().As4()}
	sa.port[0], sa.port[1] = byte(to.Port()>>8), byte(to.Port())
	buf := syscall.WSABuf{Len: uint32(len(b)), Buf: &b[0]}
	var sent uint32
	return syscall.WSASendTo(u.h, &buf, 1, &sent, 0, (*syscall.RawSockaddrAny)(unsafe.Pointer(&sa)), int32(unsafe.Sizeof(sa)), nil, nil)
}

func (u *winUDP) SetReadTimeout(d time.Duration) error {
	return syscall.SetsockoptInt(u.h, syscall.SOL_SOCKET, soRcvTimeo, int(d/time.Millisecond))
}

func (u *winUDP) LocalAddr() netip.AddrPort { return u.local }

func (u *winUDP) Close() error {
	u.mu.Lock()
	defer u.mu.Unlock()
	if u.closed {
		return nil
	}
	u.closed = true
	return syscall.Closesocket(u.h)
}

func isTimeout(err error) bool { return errors.Is(err, wsaETimedOut) }

func (u *winUDP) isClosed(err error) bool {
	u.mu.Lock()
	defer u.mu.Unlock()
	return u.closed || errors.Is(err, wsaENotSock) || errors.Is(err, wsaEIntr)
}
