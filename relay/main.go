package main

import (
	"encoding/hex"
	"errors"
	"flag"
	"fmt"
	"math"
	"net"
	"net/netip"
	"os"
	"time"
)

const version = "2.0.0"

const (
	exitParentGone = 0
	exitUsage      = 1
	exitPortHeld   = 2
	exitReplaced   = 3
)

const handoverStep = 250 * time.Millisecond

var handoverWait = 5 * time.Second

type config struct {
	addr   string
	port   int
	parent int
	key    Key
	rangeM float64
	tap    int
}

func main() {
	log := newLogger(os.Stdout)
	cfg, err := parseArgs(os.Args[1:])
	if err != nil {
		log.printf("%v; exit 1", err)
		log.close(time.Second)
		os.Exit(exitUsage)
	}
	code := run(cfg, log, nil)
	log.close(time.Second)
	os.Exit(code)
}

func parseArgs(args []string) (config, error) {
	fs := flag.NewFlagSet("TavernVoiceRelay", flag.ContinueOnError)
	addr := fs.String("addr", "0.0.0.0", "address to bind")
	port := fs.Int("port", 1770, "UDP port")
	parent := fs.Int("parent", 0, "exit when this process is gone")
	key := fs.String("key", "", "32 hex digits that control datagrams must carry")
	rangeM := fs.Float64("range", 50, "hearing range in meters")
	tap := fs.Int("tap", 0, "loopback UDP port of the server mod, for players on the in-game fallback")
	if err := fs.Parse(args); err != nil {
		return config{}, err
	}
	cfg := config{addr: *addr, port: *port, parent: *parent, rangeM: *rangeM, tap: *tap}
	k, err := hex.DecodeString(*key)
	if err != nil || len(k) != len(cfg.key) {
		return config{}, errors.New("--key must be 32 hex digits")
	}
	copy(cfg.key[:], k)
	if cfg.port < 1 || cfg.port > 65535 {
		return config{}, fmt.Errorf("--port %d is out of range", cfg.port)
	}
	if cfg.tap < 0 || cfg.tap > 65535 {
		return config{}, fmt.Errorf("--tap %d is out of range", cfg.tap)
	}
	if !(cfg.rangeM > 0) || math.IsInf(cfg.rangeM, 0) {
		return config{}, fmt.Errorf("--range %g must be a positive number", cfg.rangeM)
	}
	if net.ParseIP(cfg.addr).To4() == nil {
		return config{}, fmt.Errorf("--addr %q is not an IPv4 address", cfg.addr)
	}
	return cfg, nil
}

func run(cfg config, log *logger, stop <-chan struct{}) int {
	log.printf("TavernVoiceRelay %s pid %d, port %d, parent %d, range %g m, %s",
		version, os.Getpid(), cfg.port, cfg.parent, cfg.rangeM, platform())
	conn, err := bind(cfg, log)
	if err != nil {
		log.printf("port %d is held by something else (%v); exit 2", cfg.port, err)
		return exitPortHeld
	}
	defer conn.Close()
	log.printf("listening on udp %s", conn.LocalAddr())

	gone := make(chan string, 2)
	if cfg.parent > 0 {
		if err := watchParent(cfg.parent, gone, log); err != nil {
			log.printf("server process %d is already gone (%v); exit 0", cfg.parent, err)
			return exitParentGone
		}
	}
	r := NewRelay(cfg.key, cfg.rangeM, log.limited)
	if cfg.tap > 0 {
		r.SetTap(netip.AddrPortFrom(netip.AddrFrom4([4]byte{127, 0, 0, 1}), uint16(cfg.tap)))
	}
	return serve(conn, r, gone, stop, log)
}

func bind(cfg config, log *logger) (packetConn, error) {
	ip := [4]byte(net.ParseIP(cfg.addr).To4())
	conn, err := listen(ip, cfg.port)
	if err == nil {
		return conn, nil
	}
	log.printf("port %d is taken (%v); asking the relay there to quit", cfg.port, err)
	deadline := time.Now().Add(handoverWait)
	for time.Now().Before(deadline) {
		sendQuit(cfg.port)
		time.Sleep(handoverStep)
		if conn, err = listen(ip, cfg.port); err == nil {
			log.printf("took port %d over from the old relay", cfg.port)
			return conn, nil
		}
	}
	return nil, err
}

func sendQuit(port int) {
	c, err := listen([4]byte{127, 0, 0, 1}, 0)
	if err != nil {
		return
	}
	c.WriteTo(appendQuit(nil), netip.AddrPortFrom(netip.AddrFrom4([4]byte{127, 0, 0, 1}), uint16(port)))
	c.Close()
}

func serve(conn packetConn, r *Relay, gone <-chan string, stop <-chan struct{}, log *logger) int {
	buf := make([]byte, 65536)
	if err := conn.SetReadTimeout(time.Second); err != nil {
		log.printf("setting the read timeout failed: %v", err)
	}
	now := time.Now()
	lastTick, nextSummary := now, now.Add(time.Minute)
	var shown counts
	for {
		select {
		case how := <-gone:
			log.printf("server process is gone (%s); exit 0", how)
			return exitParentGone
		case <-stop:
			return exitParentGone
		default:
		}
		n, from, err := conn.ReadFrom(buf)
		now = time.Now()
		if now.Sub(lastTick) >= time.Second {
			r.Tick(now)
			lastTick = now
			if now.After(nextSummary) {
				if r.stats != shown {
					log.printf("%s", summary(r, shown))
					shown = r.stats
				}
				nextSummary = now.Add(time.Minute)
			}
		}
		if err != nil {
			if isTimeout(err) {
				continue
			}
			if conn.isClosed(err) {
				log.printf("socket closed; exit 1")
				return exitUsage
			}
			log.limited("read", "read: %v", err)
			time.Sleep(10 * time.Millisecond)
			continue
		}
		out, quit := r.Handle(buf[:n], from, now)
		if quit {
			log.printf("a newer relay asked this one to quit; exit 3")
			return exitReplaced
		}
		for _, s := range out {
			if err := conn.WriteTo(s.data, s.to); err != nil {
				log.limited("write", "send to %s: %v", s.to, err)
			}
		}
	}
}

func summary(r *Relay, since counts) string {
	s := r.stats
	return fmt.Sprintf("last minute: %d players, %d bound, %d forwarded, %d tapped, %d late packets passed on; dropped %d unknown, %d wrong tag, %d over limit, %d out of order, %d without a position, %d malformed",
		r.Players(), r.Bound(), s.forwarded-since.forwarded, s.tapped-since.tapped, s.late-since.late, s.unknown-since.unknown, s.wrongTag-since.wrongTag,
		s.limited-since.limited, s.order-since.order, s.noPosition-since.noPosition, s.malformed-since.malformed)
}
