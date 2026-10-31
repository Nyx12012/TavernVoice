package main

import (
	"bytes"
	"io"
	"net"
	"os"
	"os/exec"
	"strconv"
	"testing"
	"time"
)

func TestMain(m *testing.M) {
	if ms := os.Getenv("RELAY_TEST_SLEEP_MS"); ms != "" {
		n, _ := strconv.Atoi(ms)
		time.Sleep(time.Duration(n) * time.Millisecond)
		os.Exit(7)
	}
	os.Exit(m.Run())
}

func standIn(t *testing.T, ms int) (int, <-chan struct{}) {
	t.Helper()
	c := exec.Command(os.Args[0])
	c.Env = append(os.Environ(), "RELAY_TEST_SLEEP_MS="+strconv.Itoa(ms))
	if err := c.Start(); err != nil {
		t.Fatal(err)
	}
	done := make(chan struct{})
	go func() { c.Wait(); close(done) }()
	t.Cleanup(func() { c.Process.Kill(); <-done })
	return c.Process.Pid, done
}

func freePort(t *testing.T) int {
	t.Helper()
	c, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	port := c.LocalAddr().(*net.UDPAddr).Port
	c.Close()
	return port
}

func testConfig(port int) config {
	return config{addr: "127.0.0.1", port: port, key: testKey, rangeM: 50}
}

type running struct {
	code chan int
	stop chan struct{}
}

func start(cfg config) running {
	r := running{code: make(chan int, 1), stop: make(chan struct{})}
	go func() { r.code <- run(cfg, newLogger(io.Discard), r.stop) }()
	return r
}

func (r running) wait(t *testing.T, within time.Duration) int {
	t.Helper()
	select {
	case c := <-r.code:
		return c
	case <-time.After(within):
		t.Fatalf("relay still running after %s", within)
		return -1
	}
}

func client(t *testing.T, port int) *net.UDPConn {
	t.Helper()
	c, err := net.DialUDP("udp4", nil, &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: port})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { c.Close() })
	return c
}

func exchange(c *net.UDPConn, out []byte, wait time.Duration) []byte {
	c.Write(out)
	c.SetReadDeadline(time.Now().Add(wait))
	buf := make([]byte, 2048)
	n, err := c.Read(buf)
	if err != nil {
		return nil
	}
	return buf[:n]
}

func answers(t *testing.T, port int, within time.Duration) bool {
	t.Helper()
	deadline := time.Now().Add(within)
	for time.Now().Before(deadline) {
		if got := exchange(client(t, port), appendHello(nil, Token{0x77}), 200*time.Millisecond); bytes.Equal(got, appendUnknown(nil)) {
			return true
		}
	}
	return false
}

func TestParseArgs(t *testing.T) {
	cfg, err := parseArgs([]string{"--port", "1771", "--parent", "42", "--key", "101112131415161718191a1b1c1d1e1f", "--range", "30"})
	if err != nil || cfg.port != 1771 || cfg.parent != 42 || cfg.key != testKey || cfg.rangeM != 30 || cfg.addr != "0.0.0.0" {
		t.Fatalf("%+v %v", cfg, err)
	}
	bad := [][]string{
		{},
		{"--key", "abc"},
		{"--key", "101112131415161718191a1b1c1d1e1f00"},
		{"--key", "101112131415161718191a1b1c1d1e1f", "--port", "0"},
		{"--key", "101112131415161718191a1b1c1d1e1f", "--range", "0"},
		{"--key", "101112131415161718191a1b1c1d1e1f", "--range", "NaN"},
		{"--key", "101112131415161718191a1b1c1d1e1f", "--addr", "::1"},
	}
	for _, args := range bad {
		if _, err := parseArgs(args); err == nil {
			t.Errorf("%v accepted", args)
		}
	}
}

func TestWatchParent(t *testing.T) {
	pid, done := standIn(t, 300)
	gone := make(chan string, 2)
	if err := watchParent(pid, gone, newLogger(io.Discard)); err != nil {
		t.Fatal(err)
	}
	<-done
	exited := time.Now()
	select {
	case how := <-gone:
		t.Logf("noticed by %s, %d ms after the exit", how, time.Since(exited).Milliseconds())
	case <-time.After(3 * time.Second):
		t.Fatalf("parent exit not noticed within 3 s")
	}
}

func TestRunExitsWhenParentIsGone(t *testing.T) {
	pid, _ := standIn(t, 500)
	cfg := testConfig(freePort(t))
	cfg.parent = pid
	r := start(cfg)
	if !answers(t, cfg.port, 2*time.Second) {
		t.Fatalf("relay never answered")
	}
	if code := r.wait(t, 4*time.Second); code != exitParentGone {
		t.Fatalf("exit %d, want 0", code)
	}
}

func TestRunRefusesAParentThatIsAlreadyGone(t *testing.T) {
	pid, done := standIn(t, 0)
	<-done
	cfg := testConfig(freePort(t))
	cfg.parent = pid
	if code := start(cfg).wait(t, 3*time.Second); code != exitParentGone {
		t.Fatalf("exit %d, want 0", code)
	}
}

func TestHandover(t *testing.T) {
	port := freePort(t)
	old := start(testConfig(port))
	if !answers(t, port, 2*time.Second) {
		t.Fatalf("old relay never answered")
	}
	fresh := start(testConfig(port))
	if code := old.wait(t, 3*time.Second); code != exitReplaced {
		t.Fatalf("old relay exit %d, want 3", code)
	}
	if !answers(t, port, 3*time.Second) {
		t.Fatalf("new relay never answered")
	}
	close(fresh.stop)
	fresh.wait(t, 3*time.Second)
}

func TestPortHeldBySomethingElse(t *testing.T) {
	port := freePort(t)
	squatter, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: port})
	if err != nil {
		t.Fatal(err)
	}
	defer squatter.Close()
	saved := handoverWait
	handoverWait = 800 * time.Millisecond
	defer func() { handoverWait = saved }()
	if code := start(testConfig(port)).wait(t, 3*time.Second); code != exitPortHeld {
		t.Fatalf("exit %d, want 2", code)
	}
}

func TestEndToEnd(t *testing.T) {
	port := freePort(t)
	r := start(testConfig(port))
	defer func() { close(r.stop); r.wait(t, 3*time.Second) }()
	if !answers(t, port, 2*time.Second) {
		t.Fatalf("relay never answered")
	}
	ctl := client(t, port)
	ctl.Write(appendRoster(nil, testKey, players[:2]))
	ctl.Write(appendPos(nil, testKey, []position{{1, vec3{0, 0, 0}}, {2, vec3{10, 0, 0}}}))

	a, b := client(t, port), client(t, port)
	if got := exchange(a, appendHello(nil, tokA), time.Second); !bytes.Equal(got, appendBound(nil, 1)) {
		t.Fatalf("A hello: %x", got)
	}
	if got := exchange(b, appendHello(nil, tokB), time.Second); !bytes.Equal(got, appendBound(nil, 2)) {
		t.Fatalf("B hello: %x", got)
	}
	a.Write(up(101, 3, 0))
	b.SetReadDeadline(time.Now().Add(time.Second))
	buf := make([]byte, 2048)
	n, err := b.Read(buf)
	want := appendVoice(nil, typeVoiceDown, voicePacket{id: 1, spurt: 3, seq: 0, payload: opus})
	if err != nil || !bytes.Equal(buf[:n], want) {
		t.Fatalf("B got %x (%v), want %x", buf[:n], err, want)
	}
}
