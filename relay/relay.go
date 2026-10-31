package main

import (
	"math"
	"net/netip"
	"time"
)

const (
	unknownEvery    = 5 * time.Second
	maxUnknownPeers = 4096
	posStaleLog     = time.Second
)

type session struct {
	token  Token
	tag    uint32
	sender int32
	addr   netip.AddrPort
	check  validator
}

type send struct {
	to   netip.AddrPort
	data []byte
}

type counts struct {
	forwarded, tapped, unknown, malformed, wrongTag, limited, order, late, noPosition uint64
}

type Relay struct {
	key      Key
	range2   float64
	logf     func(key, format string, args ...interface{})
	sessions map[Token]*session
	byAddr   map[netip.AddrPort]*session
	where    map[int32]vec3
	members  map[int32]bool
	fallback map[int32]bool
	tapTo    netip.AddrPort
	tapBuf   []byte
	told     map[netip.AddrPort]time.Time
	lastPos  time.Time
	stats    counts
	out      []send
	voiceBuf []byte
	replyBuf [8]byte
}

func NewRelay(key Key, rangeMeters float64, logf func(key, format string, args ...interface{})) *Relay {
	if logf == nil {
		logf = func(string, string, ...interface{}) {}
	}
	return &Relay{
		key:      key,
		range2:   rangeMeters * rangeMeters,
		logf:     logf,
		sessions: map[Token]*session{},
		byAddr:   map[netip.AddrPort]*session{},
		where:    map[int32]vec3{},
		members:  map[int32]bool{},
		fallback: map[int32]bool{},
		tapBuf:   make([]byte, 0, 2+16+voiceHeader+maxPayload),
		told:     map[netip.AddrPort]time.Time{},
		voiceBuf: make([]byte, 0, voiceHeader+maxPayload),
	}
}

func (r *Relay) Handle(b []byte, from netip.AddrPort, now time.Time) (out []send, quit bool) {
	r.out = r.out[:0]
	from = netip.AddrPortFrom(from.Addr().Unmap(), from.Port())
	typ, ok := packetType(b)
	if !ok {
		r.stats.malformed++
		return nil, false
	}
	loopback := from.Addr().IsLoopback()
	switch typ {
	case typeHello:
		r.hello(b, from, now)
	case typeVoiceUp:
		r.voice(b, from, now)
	case typeBye:
		r.bye(b, from)
	case typeRoster:
		if loopback {
			r.roster(b, now)
		}
	case typePos:
		if loopback {
			r.positions(b, now)
		}
	case typeTapList:
		if loopback {
			r.tapList(b)
		}
	case typeRange:
		if loopback {
			r.setRange(b)
		}
	case typeQuit:
		if loopback && len(b) == 2 {
			return nil, true
		}
	default:
		r.stats.malformed++
	}
	return r.out, false
}

func (r *Relay) Tick(now time.Time) {
	for a, at := range r.told {
		if now.Sub(at) >= unknownEvery {
			delete(r.told, a)
		}
	}
}

func (r *Relay) reply(to netip.AddrPort, data []byte) {
	r.out = append(r.out, send{to, data})
}

func (r *Relay) unknown(to netip.AddrPort, now time.Time) {
	r.stats.unknown++
	if at, seen := r.told[to]; seen && now.Sub(at) < unknownEvery {
		return
	}
	if len(r.told) >= maxUnknownPeers {
		if _, seen := r.told[to]; !seen {
			return
		}
	}
	r.told[to] = now
	r.reply(to, appendUnknown(r.replyBuf[:0]))
}

func (r *Relay) hello(b []byte, from netip.AddrPort, now time.Time) {
	t, ok := parseHello(b)
	if !ok {
		r.stats.malformed++
		return
	}
	s := r.sessions[t]
	if s == nil {
		r.unknown(from, now)
		return
	}
	if s.addr != from {
		r.bind(s, from)
	}
	r.reply(from, appendBound(r.replyBuf[:0], s.sender))
}

func (r *Relay) bind(s *session, addr netip.AddrPort) {
	if old := r.byAddr[addr]; old != nil {
		old.addr = netip.AddrPort{}
	}
	if s.addr.IsValid() {
		delete(r.byAddr, s.addr)
		r.logf("rebind", "player %d moved from %s to %s", s.sender, s.addr, addr)
	} else {
		r.logf("bind", "player %d bound at %s", s.sender, addr)
	}
	s.addr = addr
	r.byAddr[addr] = s
}

func (r *Relay) unbind(s *session) {
	if s.addr.IsValid() {
		delete(r.byAddr, s.addr)
		s.addr = netip.AddrPort{}
	}
}

func (r *Relay) bye(b []byte, from netip.AddrPort) {
	tag, ok := parseBye(b)
	if !ok {
		r.stats.malformed++
		return
	}
	if s := r.byAddr[from]; s != nil && s.tag == tag {
		r.unbind(s)
		r.logf("bye", "player %d said bye", s.sender)
	}
}

func (r *Relay) voice(b []byte, from netip.AddrPort, now time.Time) {
	p, ok := parseVoice(b)
	if !ok {
		r.stats.malformed++
		return
	}
	s := r.byAddr[from]
	if s == nil {
		r.unknown(from, now)
		return
	}
	if p.id != s.tag {
		r.stats.wrongTag++
		r.unknown(from, now)
		return
	}
	switch s.check.accept(p.spurt, p.seq, now) {
	case overLimit:
		r.stats.limited++
		return
	case outOfOrder:
		r.stats.order++
		return
	case passedLate:
		r.stats.late++
	}
	at, ok := r.where[s.sender]
	if !ok {
		r.stats.noPosition++
		return
	}
	p.id = uint32(s.sender)
	r.voiceBuf = appendVoice(r.voiceBuf[:0], typeVoiceDown, p)
	for _, o := range r.byAddr {
		if o == s {
			continue
		}
		there, ok := r.where[o.sender]
		if !ok || !(dist2(at, there) <= r.range2) {
			continue
		}
		r.out = append(r.out, send{o.addr, r.voiceBuf})
		r.stats.forwarded++
	}
	if r.tapTo.IsValid() && r.fallbackInRange(s.sender, at) {
		r.tapBuf = appendTap(r.tapBuf[:0], r.key, p)
		r.out = append(r.out, send{r.tapTo, r.tapBuf})
		r.stats.tapped++
	}
}

func (r *Relay) fallbackInRange(sender int32, at vec3) bool {
	for id := range r.fallback {
		if id == sender {
			continue
		}
		if there, ok := r.where[id]; ok && dist2(at, there) <= r.range2 {
			return true
		}
	}
	return false
}

func (r *Relay) SetTap(to netip.AddrPort) { r.tapTo = to }

func (r *Relay) setRange(b []byte) {
	m, ok := parseRange(b, r.key)
	if !ok {
		r.logf("badcontrol", "range datagram rejected (wrong key, size or value)")
		return
	}
	r.range2 = m * m
	r.logf("range", "range is now %g m", m)
}

func (r *Relay) Range() float64 { return math.Sqrt(r.range2) }

func (r *Relay) tapList(b []byte) {
	ids, ok := parseTapList(b, r.key)
	if !ok {
		r.logf("badcontrol", "tap list datagram rejected (wrong key or size)")
		return
	}
	next := make(map[int32]bool, len(ids))
	for _, id := range ids {
		next[id] = true
	}
	if len(next) != len(r.fallback) {
		r.logf("taplist", "%d players on the in-game fallback", len(next))
	}
	r.fallback = next
}

func (r *Relay) roster(b []byte, now time.Time) {
	ms, ok := parseRoster(b, r.key)
	if !ok {
		r.logf("badcontrol", "roster datagram rejected (wrong key or size)")
		return
	}
	keep := make(map[Token]bool, len(ms))
	senders := make(map[int32]bool, len(ms))
	added := 0
	for _, m := range ms {
		s := r.sessions[m.token]
		if s == nil {
			s = &session{token: m.token, check: newValidator(now)}
			r.sessions[m.token] = s
			added++
		}
		s.tag, s.sender = m.tag, m.sender
		keep[m.token] = true
		senders[m.sender] = true
	}
	removed := 0
	for t, s := range r.sessions {
		if !keep[t] {
			r.unbind(s)
			delete(r.sessions, t)
			removed++
		}
	}
	for id := range r.where {
		if !senders[id] {
			delete(r.where, id)
		}
	}
	r.members = senders
	if added > 0 || removed > 0 {
		r.logf("roster", "roster: %d players (+%d, -%d)", len(r.sessions), added, removed)
	}
}

func (r *Relay) positions(b []byte, now time.Time) {
	ps, ok := parsePos(b, r.key)
	if !ok {
		r.logf("badcontrol", "position datagram rejected (wrong key or size)")
		return
	}
	if !r.lastPos.IsZero() && now.Sub(r.lastPos) > posStaleLog {
		r.logf("stale", "positions resumed after %s", now.Sub(r.lastPos).Round(10*time.Millisecond))
	}
	r.lastPos = now
	for _, p := range ps {
		if r.members[p.sender] {
			r.where[p.sender] = p.at
		}
	}
}

func (r *Relay) Bound() int { return len(r.byAddr) }

func (r *Relay) Players() int { return len(r.sessions) }

func dist2(a, b vec3) float64 {
	dx := float64(a.x) - float64(b.x)
	dy := float64(a.y) - float64(b.y)
	dz := float64(a.z) - float64(b.z)
	return dx*dx + dy*dy + dz*dz
}
