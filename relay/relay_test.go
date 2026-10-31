package main

import (
	"bytes"
	"math"
	"net/netip"
	"testing"
	"time"
)

var (
	testKey = Key{0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f}
	tokA    = Token{0xa1}
	tokB    = Token{0xb2}
	tokC    = Token{0xc3}
	addrA   = netip.MustParseAddrPort("203.0.113.1:50001")
	addrB   = netip.MustParseAddrPort("203.0.113.2:50002")
	addrC   = netip.MustParseAddrPort("198.51.100.3:50003")
	server  = netip.MustParseAddrPort("127.0.0.1:40000")
	t0      = time.Unix(1_700_000_000, 0)
	opus    = []byte{0xfc, 0xff, 0xfe, 0x01, 0x02, 0x03}
)

var players = []member{
	{token: tokA, tag: 101, sender: 1},
	{token: tokB, tag: 102, sender: 2},
	{token: tokC, tag: 103, sender: 3},
}

func handle(t *testing.T, r *Relay, b []byte, from netip.AddrPort, now time.Time) []send {
	t.Helper()
	out, quit := r.Handle(b, from, now)
	if quit {
		t.Fatalf("unexpected quit")
	}
	cp := make([]send, len(out))
	for i, s := range out {
		cp[i] = send{s.to, append([]byte(nil), s.data...)}
	}
	return cp
}

func up(tag uint32, spurt, seq uint16) []byte {
	return appendVoice(nil, typeVoiceUp, voicePacket{id: tag, spurt: spurt, seq: seq, payload: opus})
}

func place(t *testing.T, r *Relay, now time.Time, ps ...position) {
	t.Helper()
	handle(t, r, appendPos(nil, testKey, ps), server, now)
}

func ready(t *testing.T) *Relay {
	t.Helper()
	r := NewRelay(testKey, 50, nil)
	handle(t, r, appendRoster(nil, testKey, players), server, t0)
	for i, a := range []netip.AddrPort{addrA, addrB, addrC} {
		out := handle(t, r, appendHello(nil, players[i].token), a, t0)
		if len(out) != 1 || out[0].to != a || !bytes.Equal(out[0].data, appendBound(nil, players[i].sender)) {
			t.Fatalf("hello %d: got %v", i, out)
		}
	}
	place(t, r, t0,
		position{1, vec3{0, 0, 0}},
		position{2, vec3{49.9, 0, 0}},
		position{3, vec3{0, 0, 50.1}})
	return r
}

func recipients(out []send) map[netip.AddrPort]bool {
	m := map[netip.AddrPort]bool{}
	for _, s := range out {
		m[s.to] = true
	}
	return m
}

func TestWireRoundTrip(t *testing.T) {
	if got, ok := parseHello(appendHello(nil, tokA)); !ok || got != tokA {
		t.Fatalf("hello: %v %v", got, ok)
	}
	p := voicePacket{id: 7, spurt: 65535, seq: 3, payload: opus}
	got, ok := parseVoice(appendVoice(nil, typeVoiceUp, p))
	if !ok || got.id != 7 || got.spurt != 65535 || got.seq != 3 || !bytes.Equal(got.payload, opus) {
		t.Fatalf("voice: %+v %v", got, ok)
	}
	if tag, ok := parseBye(appendBye(nil, 99)); !ok || tag != 99 {
		t.Fatalf("bye: %v %v", tag, ok)
	}
	ms, ok := parseRoster(appendRoster(nil, testKey, players), testKey)
	if !ok || len(ms) != 3 || ms[2] != players[2] {
		t.Fatalf("roster: %v %v", ms, ok)
	}
	ps := []position{{-5, vec3{1.5, -2.25, 1e6}}}
	gotPs, ok := parsePos(appendPos(nil, testKey, ps), testKey)
	if !ok || len(gotPs) != 1 || gotPs[0] != ps[0] {
		t.Fatalf("pos: %v %v", gotPs, ok)
	}
	if b := appendBound(nil, -2); len(b) != 6 || b[0] != 2 || b[1] != typeBound {
		t.Fatalf("bound: %v", b)
	}
}

func TestWireRejectsBadSizes(t *testing.T) {
	h := appendHello(nil, tokA)
	for _, b := range [][]byte{h[:len(h)-1], append(h, 0)} {
		if _, ok := parseHello(b); ok {
			t.Errorf("%d byte hello parsed", len(b))
		}
	}
	v := up(1, 0, 0)
	voices := map[string][]byte{
		"too short": v[:len(v)-1],
		"too long":  append(append([]byte(nil), v...), 0),
		"empty":     appendVoice(nil, typeVoiceUp, voicePacket{payload: nil}),
		"over 512":  appendVoice(nil, typeVoiceUp, voicePacket{payload: make([]byte, 513)}),
	}
	for name, b := range voices {
		if _, ok := parseVoice(b); ok {
			t.Errorf("voice %s parsed", name)
		}
	}
	if _, ok := parseVoice(appendVoice(nil, typeVoiceUp, voicePacket{payload: make([]byte, 512)})); !ok {
		t.Errorf("512 byte payload refused")
	}
	roster := appendRoster(nil, testKey, players)
	if _, ok := parseRoster(roster[:len(roster)-1], testKey); ok {
		t.Errorf("truncated roster parsed")
	}
	if _, ok := parseRoster(roster, Key{1}); ok {
		t.Errorf("roster with the wrong key parsed")
	}
}

func TestHelloWithUnknownToken(t *testing.T) {
	r := NewRelay(testKey, 50, nil)
	out := handle(t, r, appendHello(nil, tokA), addrA, t0)
	if len(out) != 1 || !bytes.Equal(out[0].data, appendUnknown(nil)) {
		t.Fatalf("got %v", out)
	}
}

func TestRangeFiltering(t *testing.T) {
	r := ready(t)
	out := handle(t, r, up(101, 1, 0), addrA, t0)
	to := recipients(out)
	if len(out) != 1 || !to[addrB] {
		t.Fatalf("A at 0 should reach B at 49.9 only, got %v", to)
	}

	out = handle(t, r, up(103, 1, 0), addrC, t0)
	if len(out) != 0 {
		t.Fatalf("C is 50.1 from A and further from B, got %v", recipients(out))
	}

	place(t, r, t0, position{3, vec3{0, 30, 39}})
	out = handle(t, r, up(103, 1, 1), addrC, t0)
	if to := recipients(out); len(out) != 1 || !to[addrA] {
		t.Fatalf("C moved to 49.2 from A, got %v", to)
	}
}

func TestForwardStampsSender(t *testing.T) {
	r := ready(t)
	in := up(101, 9, 4)
	out := handle(t, r, in, addrA, t0)
	if len(out) != 1 {
		t.Fatalf("got %d sends", len(out))
	}
	want := appendVoice(nil, typeVoiceDown, voicePacket{id: 1, spurt: 9, seq: 4, payload: opus})
	if !bytes.Equal(out[0].data, want) || len(out[0].data) != len(in) {
		t.Fatalf("got %x, want %x", out[0].data, want)
	}
}

func TestNoPositionMeansSilence(t *testing.T) {
	r := NewRelay(testKey, 50, nil)
	handle(t, r, appendRoster(nil, testKey, players), server, t0)
	handle(t, r, appendHello(nil, tokA), addrA, t0)
	handle(t, r, appendHello(nil, tokB), addrB, t0)
	if out := handle(t, r, up(101, 1, 0), addrA, t0); len(out) != 0 {
		t.Fatalf("nobody has a position yet, got %v", out)
	}
	place(t, r, t0, position{1, vec3{}})
	if out := handle(t, r, up(101, 1, 1), addrA, t0); len(out) != 0 {
		t.Fatalf("B has no position, got %v", out)
	}
	place(t, r, t0, position{2, vec3{1, 0, 0}})
	if out := handle(t, r, up(101, 1, 2), addrA, t0); len(out) != 1 {
		t.Fatalf("both placed, got %v", out)
	}
}

func TestPositionsSurviveAServerFreeze(t *testing.T) {
	r := ready(t)
	later := t0.Add(30 * time.Second)
	r.Tick(later)
	if out := handle(t, r, up(101, 1, 0), addrA, later); len(out) != 1 {
		t.Fatalf("30 s without positions must not stop voice, got %v", out)
	}
	place(t, r, later, position{1, vec3{0, 0, 0}})
	if out := handle(t, r, up(101, 1, 1), addrA, later); len(out) != 1 {
		t.Fatalf("B left out of the update keeps its last position, got %v", out)
	}
}

func TestNaNPositionHearsNothing(t *testing.T) {
	r := ready(t)
	nan := float32(math.NaN())
	place(t, r, t0, position{2, vec3{nan, 0, 0}})
	if out := handle(t, r, up(101, 1, 0), addrA, t0); len(out) != 0 {
		t.Fatalf("got %v", out)
	}
}

func TestUnknownRateLimit(t *testing.T) {
	r := NewRelay(testKey, 50, nil)
	count := func(now time.Time, from netip.AddrPort) int {
		return len(handle(t, r, appendHello(nil, tokA), from, now))
	}
	if count(t0, addrA) != 1 {
		t.Fatalf("first UNKNOWN missing")
	}
	if count(t0.Add(time.Second), addrA) != 0 || count(t0.Add(4900*time.Millisecond), addrA) != 0 {
		t.Fatalf("UNKNOWN sent twice within 5 s")
	}
	if count(t0.Add(2*time.Second), addrB) != 1 {
		t.Fatalf("another endpoint has its own limit")
	}
	r.Tick(t0.Add(5 * time.Second))
	if count(t0.Add(5*time.Second), addrA) != 1 {
		t.Fatalf("UNKNOWN missing after 5 s")
	}
	if n := len(handle(t, r, up(1, 0, 0), addrA, t0.Add(6*time.Second))); n != 0 {
		t.Fatalf("voice from an unbound endpoint shares the limit, got %d", n)
	}
}

func TestUnknownPeerCap(t *testing.T) {
	r := NewRelay(testKey, 50, nil)
	for i := 0; i < maxUnknownPeers; i++ {
		a := netip.AddrPortFrom(netip.AddrFrom4([4]byte{10, byte(i >> 16), byte(i >> 8), byte(i)}), 9)
		handle(t, r, appendHello(nil, tokA), a, t0)
	}
	if n := len(handle(t, r, appendHello(nil, tokA), addrA, t0)); n != 0 {
		t.Fatalf("a full table must not grow, got %d replies", n)
	}
	r.Tick(t0.Add(unknownEvery))
	if n := len(handle(t, r, appendHello(nil, tokA), addrA, t0.Add(unknownEvery))); n != 1 {
		t.Fatalf("the table empties after 5 s, got %d replies", n)
	}
}

func TestRebindAfterNATChange(t *testing.T) {
	r := ready(t)
	moved := netip.MustParseAddrPort("203.0.113.1:61000")
	out := handle(t, r, up(101, 1, 0), moved, t0)
	if len(out) != 1 || !bytes.Equal(out[0].data, appendUnknown(nil)) {
		t.Fatalf("voice from the new port should draw UNKNOWN, got %v", out)
	}
	handle(t, r, appendHello(nil, tokA), moved, t0)
	if out := handle(t, r, up(101, 1, 1), moved, t0); len(out) != 1 {
		t.Fatalf("after HELLO the new port forwards, got %v", out)
	}
	if out := handle(t, r, up(101, 1, 2), addrA, t0); len(out) != 1 || !bytes.Equal(out[0].data, appendUnknown(nil)) {
		t.Fatalf("the old port is unbound, got %v", out)
	}
	if out := handle(t, r, up(102, 1, 0), addrB, t0); !recipients(out)[moved] || recipients(out)[addrA] {
		t.Fatalf("B's voice goes to A's new port only, got %v", recipients(out))
	}
}

func TestWrongTagDrawsUnknown(t *testing.T) {
	r := ready(t)
	out := handle(t, r, up(999, 1, 0), addrA, t0)
	if len(out) != 1 || !bytes.Equal(out[0].data, appendUnknown(nil)) {
		t.Fatalf("got %v", out)
	}
}

func TestRosterRemovalUnbinds(t *testing.T) {
	r := ready(t)
	handle(t, r, appendRoster(nil, testKey, players[:1]), server, t0)
	if r.Players() != 1 || r.Bound() != 1 {
		t.Fatalf("players %d bound %d", r.Players(), r.Bound())
	}
	place(t, r, t0, position{2, vec3{1, 0, 0}})
	if out := handle(t, r, up(101, 1, 0), addrA, t0); len(out) != 0 {
		t.Fatalf("B left: nothing to forward to, got %v", out)
	}
	if out := handle(t, r, appendHello(nil, tokB), addrB, t0.Add(time.Minute)); len(out) != 1 || !bytes.Equal(out[0].data, appendUnknown(nil)) {
		t.Fatalf("B's token is gone, got %v", out)
	}
}

func TestRosterRepeatKeepsBindings(t *testing.T) {
	r := ready(t)
	for i := 0; i < 3; i++ {
		handle(t, r, appendRoster(nil, testKey, players), server, t0.Add(time.Duration(i)*5*time.Second))
	}
	if r.Bound() != 3 {
		t.Fatalf("bound %d", r.Bound())
	}
	if out := handle(t, r, up(101, 1, 0), addrA, t0.Add(15*time.Second)); len(out) != 1 {
		t.Fatalf("got %v", out)
	}
}

func TestBye(t *testing.T) {
	r := ready(t)
	handle(t, r, appendBye(nil, 555), addrB, t0)
	if r.Bound() != 3 {
		t.Fatalf("BYE with the wrong tag unbound someone")
	}
	handle(t, r, appendBye(nil, 102), addrB, t0)
	if r.Bound() != 2 {
		t.Fatalf("bound %d after BYE", r.Bound())
	}
	if out := handle(t, r, up(101, 1, 0), addrA, t0); len(out) != 0 {
		t.Fatalf("B said bye, got %v", out)
	}
}

func TestControlNeedsLoopbackAndKey(t *testing.T) {
	r := NewRelay(testKey, 50, nil)
	outside := netip.MustParseAddrPort("192.0.2.50:40000")
	handle(t, r, appendRoster(nil, testKey, players), outside, t0)
	handle(t, r, appendRoster(nil, Key{9}, players), server, t0)
	if r.Players() != 0 {
		t.Fatalf("roster accepted from outside or with the wrong key")
	}
	if _, quit := r.Handle(appendQuit(nil), outside, t0); quit {
		t.Fatalf("QUIT accepted from outside")
	}
	mapped := netip.MustParseAddrPort("[::ffff:127.0.0.1]:40000")
	handle(t, r, appendRoster(nil, testKey, players), mapped, t0)
	if r.Players() != 3 {
		t.Fatalf("roster from mapped loopback refused")
	}
	if _, quit := r.Handle(appendQuit(nil), server, t0); !quit {
		t.Fatalf("QUIT from loopback refused")
	}
}

func TestReplyNeverLargerThanRequest(t *testing.T) {
	r := ready(t)
	inputs := [][]byte{
		{}, {2}, {2, 0}, {2, typeUnknown}, {2, typeBound, 1, 2, 3, 4},
		appendHello(nil, tokA), appendHello(nil, Token{0xee}), appendHello(nil, tokA)[:3],
		up(101, 1, 0), up(999, 1, 0), up(101, 1, 0)[:12],
		appendBye(nil, 1), appendQuit(nil)[:1],
		appendVoice(nil, typeVoiceDown, voicePacket{id: 1, payload: opus}),
	}
	strangers := []netip.AddrPort{addrA, netip.MustParseAddrPort("192.0.2.77:1234")}
	now := t0
	for _, from := range strangers {
		for _, in := range inputs {
			now = now.Add(6 * time.Second)
			r.Tick(now)
			out, _ := r.Handle(in, from, now)
			for _, s := range out {
				if s.to == from && len(s.data) > len(in) {
					t.Fatalf("%d byte reply to a %d byte datagram %x", len(s.data), len(in), in)
				}
			}
		}
	}
}

func FuzzReplySize(f *testing.F) {
	f.Add(appendHello(nil, tokA))
	f.Add(up(101, 1, 0))
	f.Add([]byte{2, typeHello})
	f.Add(appendBye(nil, 101))
	f.Fuzz(func(t *testing.T, in []byte) {
		r := ready(t)
		from := netip.MustParseAddrPort("192.0.2.9:999")
		for _, src := range []netip.AddrPort{from, addrA} {
			out, _ := r.Handle(in, src, t0.Add(time.Minute))
			for _, s := range out {
				if s.to == src && len(s.data) > len(in) {
					t.Fatalf("%d byte reply to %d bytes", len(s.data), len(in))
				}
			}
		}
	})
}

func TestTokenBucket(t *testing.T) {
	v := newValidator(t0)
	seq := uint16(0)
	next := func(now time.Time) bool {
		ok := v.accept(1, seq, now).ok()
		if ok {
			seq++
		}
		return ok
	}
	for i := 0; i < bucketBurst; i++ {
		if !next(t0) {
			t.Fatalf("packet %d of the burst refused", i)
		}
	}
	if next(t0) {
		t.Fatalf("packet past the burst accepted")
	}
	if !next(t0.Add(21 * time.Millisecond)) {
		t.Fatalf("21 ms refills one token")
	}
	if next(t0.Add(21 * time.Millisecond)) {
		t.Fatalf("only one token refilled")
	}
	now := t0.Add(10 * time.Second)
	accepted := 0
	for i := 0; i < 100; i++ {
		if next(now) {
			accepted++
		}
	}
	if accepted != bucketBurst {
		t.Fatalf("a long rest refills to the burst only, got %d", accepted)
	}
}

func TestSustainedTalkFitsTheBucket(t *testing.T) {
	v := newValidator(t0)
	now := t0
	for i := 0; i < 50*120; i++ {
		now = now.Add(20 * time.Millisecond)
		if !v.accept(1, uint16(i), now).ok() {
			t.Fatalf("frame %d of steady 50/s talk refused", i)
		}
	}
}

func TestSequenceRules(t *testing.T) {
	type step struct {
		spurt, seq uint16
		after      time.Duration
		want       bool
	}
	cases := map[string][]step{
		"first packet near the start":  {{5, 3, 0, true}},
		"first packet late in a spurt": {{5, 25, 0, false}},
		"advance within 50":            {{5, 0, 0, true}, {5, 50, 20 * time.Millisecond, true}},
		"a jump forward resyncs":       {{5, 0, 0, true}, {5, 300, 20 * time.Millisecond, true}, {5, 301, 20 * time.Millisecond, true}},
		"a packet from before is old":  {{5, 300, 0, false}, {5, 10, 0, true}, {5, 400, time.Second, true}, {5, 387, 0, false}},
		"one frame late passes":        {{5, 300, 0, false}, {5, 10, 0, true}, {5, 400, time.Second, true}, {5, 399, 0, true}},
		"late passes once":             {{5, 0, 0, true}, {5, 2, 0, true}, {5, 1, 0, true}, {5, 1, 0, false}, {5, 2, 0, false}},
		"twelve late passes":           {{5, 0, 0, true}, {5, 20, 0, true}, {5, 8, 0, true}, {5, 7, 0, false}},
		"late across a jump":           {{5, 0, 0, true}, {5, 100, 0, true}, {5, 99, 0, true}, {5, 0, 0, false}},
		"late at a spurt start":        {{5, 1, 0, true}, {5, 0, 0, true}, {5, 2, 0, true}},
		"late across the wrap":         {{5, 0, 0, true}, {5, 20000, 0, true}, {5, 40000, 0, true}, {5, 60000, 0, true}, {5, 65535, 0, true}, {5, 1, 0, true}, {5, 0, 0, true}, {5, 65535, 0, false}, {5, 65534, 0, true}},
		"duplicate":                    {{5, 4, 0, true}, {5, 4, 20 * time.Millisecond, false}},
		"older sequence":               {{5, 30, 0, false}, {5, 10, 0, true}, {5, 24, 0, true}, {5, 11, 20 * time.Millisecond, false}},
		"sequence wraps":               {{5, 0, 0, true}, {5, 40, time.Second, true}, {5, 65530, 0, false}},
		"new spurt":                    {{5, 20, 0, true}, {6, 0, time.Second, true}},
		"new spurt starting late":      {{5, 20, 0, true}, {6, 30, time.Second, false}},
		"spurt wraps":                  {{65535, 0, 0, true}, {0, 0, time.Second, true}},
		"older spurt soon after":       {{20, 0, 0, true}, {19, 0, time.Second, false}},
		"older spurt after 10 s":       {{20, 0, 0, true}, {10, 0, 10*time.Second + time.Millisecond, true}},
		"older spurt at exactly 10 s":  {{20, 0, 0, true}, {10, 0, 10 * time.Second, false}},
		"spurt 16 back resyncs":        {{20, 0, 0, true}, {4, 0, time.Second, true}},
		"spurt 15 back is refused":     {{20, 0, 0, true}, {5, 0, time.Second, false}},
	}
	for name, steps := range cases {
		v := newValidator(t0)
		now := t0
		for i, s := range steps {
			now = now.Add(s.after)
			if got := v.accept(s.spurt, s.seq, now).ok(); got != s.want {
				t.Errorf("%s, step %d (spurt %d seq %d): got %v", name, i, s.spurt, s.seq, got)
			}
		}
	}
}

func TestLateVoiceIsForwardedOnce(t *testing.T) {
	r := ready(t)
	handle(t, r, up(101, 1, 0), addrA, t0)
	handle(t, r, up(101, 1, 2), addrA, t0.Add(40*time.Millisecond))
	if out := handle(t, r, up(101, 1, 1), addrA, t0.Add(45*time.Millisecond)); len(out) != 1 || out[0].to != addrB {
		t.Fatalf("a frame one behind goes on to B, got %v", out)
	}
	if out := handle(t, r, up(101, 1, 1), addrA, t0.Add(50*time.Millisecond)); len(out) != 0 {
		t.Fatalf("the same late frame twice was forwarded again")
	}
	if r.stats.late != 1 || r.stats.order != 1 {
		t.Fatalf("want 1 late and 1 out of order, got %+v", r.stats)
	}
}

func TestRangeControl(t *testing.T) {
	r := ready(t)
	handle(t, r, appendRange(nil, testKey, 10), netip.MustParseAddrPort("192.0.2.1:1"), t0)
	handle(t, r, appendRange(nil, Key{9}, 10), server, t0)
	if r.Range() != 50 {
		t.Fatalf("a range from outside or with the wrong key changed it to %g", r.Range())
	}
	for _, bad := range []float32{0, -5, float32(math.Inf(1)), float32(math.NaN())} {
		handle(t, r, appendRange(nil, testKey, bad), server, t0)
	}
	if r.Range() != 50 {
		t.Fatalf("a bad range value was taken: %g", r.Range())
	}
	handle(t, r, appendRange(nil, testKey, 60), server, t0)
	if out := handle(t, r, up(101, 1, 0), addrA, t0); !recipients(out)[addrC] {
		t.Fatalf("C at 50.1 m hears A once the range is 60 m, got %v", out)
	}
	handle(t, r, appendRange(nil, testKey, 10), server, t0)
	if out := handle(t, r, up(101, 1, 1), addrA, t0.Add(20*time.Millisecond)); len(out) != 0 {
		t.Fatalf("nobody is within 10 m of A, got %v", out)
	}
}

func TestRefusedVoiceIsNotForwarded(t *testing.T) {
	r := ready(t)
	if out := handle(t, r, up(101, 1, 30), addrA, t0); len(out) != 0 {
		t.Fatalf("a new spurt at sequence 30 was forwarded")
	}
	handle(t, r, up(101, 1, 0), addrA, t0)
	if out := handle(t, r, up(101, 1, 0), addrA, t0.Add(20*time.Millisecond)); len(out) != 0 {
		t.Fatalf("a duplicate was forwarded")
	}
}

func TestTapCopiesVoiceForFallbackPlayers(t *testing.T) {
	r := NewRelay(testKey, 50, nil)
	tap := netip.MustParseAddrPort("127.0.0.1:41000")
	r.SetTap(tap)
	handle(t, r, appendRoster(nil, testKey, players), server, t0)
	handle(t, r, appendHello(nil, tokA), addrA, t0)
	place(t, r, t0, position{1, vec3{}}, position{2, vec3{10, 0, 0}}, position{3, vec3{80, 0, 0}})
	if out := handle(t, r, up(101, 1, 0), addrA, t0); len(out) != 0 {
		t.Fatalf("nobody in range is bound or on the fallback, got %v", out)
	}
	handle(t, r, appendTapList(nil, testKey, []int32{2, 3}), server, t0)
	out := handle(t, r, up(101, 1, 1), addrA, t0)
	want := appendTap(nil, testKey, voicePacket{id: 1, spurt: 1, seq: 1, payload: opus})
	if len(out) != 1 || out[0].to != tap || !bytes.Equal(out[0].data, want) {
		t.Fatalf("B is on the fallback 10 m away: want one tap copy, got %v", out)
	}
	handle(t, r, appendTapList(nil, testKey, []int32{3}), server, t0)
	if out := handle(t, r, up(101, 1, 2), addrA, t0); len(out) != 0 {
		t.Fatalf("only C is on the fallback and it is 80 m away, got %v", out)
	}
}

func TestTapNeedsATapPortLoopbackAndKey(t *testing.T) {
	r := NewRelay(testKey, 50, nil)
	handle(t, r, appendRoster(nil, testKey, players), server, t0)
	handle(t, r, appendHello(nil, tokA), addrA, t0)
	place(t, r, t0, position{1, vec3{}}, position{2, vec3{1, 0, 0}})
	handle(t, r, appendTapList(nil, testKey, []int32{2}), server, t0)
	if out := handle(t, r, up(101, 1, 0), addrA, t0); len(out) != 0 {
		t.Fatalf("no --tap port: nothing to copy to, got %v", out)
	}
	r.SetTap(netip.MustParseAddrPort("127.0.0.1:41000"))
	handle(t, r, appendTapList(nil, testKey, nil), server, t0)
	handle(t, r, appendTapList(nil, testKey, []int32{2}), netip.MustParseAddrPort("192.0.2.1:1"), t0)
	handle(t, r, appendTapList(nil, Key{7}, []int32{2}), server, t0)
	if out := handle(t, r, up(101, 1, 1), addrA, t0); len(out) != 0 {
		t.Fatalf("tap lists from outside or with the wrong key must be ignored, got %v", out)
	}
	if ids, ok := parseTapList(appendTapList(nil, testKey, []int32{-4, 9}), testKey); !ok || len(ids) != 2 || ids[0] != -4 {
		t.Fatalf("tap list round trip: %v %v", ids, ok)
	}
}
