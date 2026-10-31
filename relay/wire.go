package main

import (
	"crypto/subtle"
	"encoding/binary"
	"math"
)

const (
	wireVersion = 2

	typeHello     = 1
	typeBound     = 2
	typeUnknown   = 3
	typeVoiceUp   = 4
	typeVoiceDown = 5
	typeBye       = 6
	typeRoster    = 16
	typePos       = 17
	typeQuit      = 18
	typeTapList   = 19
	typeTap       = 20
	typeRange     = 21

	maxPayload  = 512
	voiceHeader = 12
	helloSize   = 2 + 16
	byeSize     = 2 + 4
	rosterItem  = 16 + 4 + 4
	posItem     = 4 + 12
	tapItem     = 4
	rangeItem   = 4
	controlHead = 2 + 16 + 2
)

type Token [16]byte

type Key [16]byte

type member struct {
	token  Token
	tag    uint32
	sender int32
}

type vec3 struct{ x, y, z float32 }

type position struct {
	sender int32
	at     vec3
}

type voicePacket struct {
	id      uint32
	spurt   uint16
	seq     uint16
	payload []byte
}

var le = binary.LittleEndian

func packetType(b []byte) (byte, bool) {
	if len(b) < 2 || b[0] != wireVersion {
		return 0, false
	}
	return b[1], true
}

func parseHello(b []byte) (Token, bool) {
	var t Token
	if len(b) != helloSize {
		return t, false
	}
	copy(t[:], b[2:])
	return t, true
}

func parseVoice(b []byte) (voicePacket, bool) {
	if len(b) < voiceHeader+1 {
		return voicePacket{}, false
	}
	n := int(le.Uint16(b[10:]))
	if n == 0 || n > maxPayload || len(b) != voiceHeader+n {
		return voicePacket{}, false
	}
	return voicePacket{
		id:      le.Uint32(b[2:]),
		spurt:   le.Uint16(b[6:]),
		seq:     le.Uint16(b[8:]),
		payload: b[voiceHeader:],
	}, true
}

func parseBye(b []byte) (uint32, bool) {
	if len(b) != byeSize {
		return 0, false
	}
	return le.Uint32(b[2:]), true
}

func controlBody(b []byte, key Key, item int) ([]byte, int, bool) {
	if len(b) < controlHead || subtle.ConstantTimeCompare(b[2:18], key[:]) != 1 {
		return nil, 0, false
	}
	count := int(le.Uint16(b[18:]))
	body := b[controlHead:]
	if len(body) != count*item {
		return nil, 0, false
	}
	return body, count, true
}

func parseRoster(b []byte, key Key) ([]member, bool) {
	body, count, ok := controlBody(b, key, rosterItem)
	if !ok {
		return nil, false
	}
	out := make([]member, count)
	for i := range out {
		e := body[i*rosterItem:]
		copy(out[i].token[:], e[:16])
		out[i].tag = le.Uint32(e[16:])
		out[i].sender = int32(le.Uint32(e[20:]))
	}
	return out, true
}

func parsePos(b []byte, key Key) ([]position, bool) {
	body, count, ok := controlBody(b, key, posItem)
	if !ok {
		return nil, false
	}
	out := make([]position, count)
	for i := range out {
		e := body[i*posItem:]
		out[i].sender = int32(le.Uint32(e))
		out[i].at = vec3{f32(e[4:]), f32(e[8:]), f32(e[12:])}
	}
	return out, true
}

func parseTapList(b []byte, key Key) ([]int32, bool) {
	body, count, ok := controlBody(b, key, tapItem)
	if !ok {
		return nil, false
	}
	out := make([]int32, count)
	for i := range out {
		out[i] = int32(le.Uint32(body[i*tapItem:]))
	}
	return out, true
}

func parseRange(b []byte, key Key) (float64, bool) {
	body, count, ok := controlBody(b, key, rangeItem)
	if !ok || count != 1 {
		return 0, false
	}
	m := float64(f32(body))
	if !(m > 0) || math.IsInf(m, 0) {
		return 0, false
	}
	return m, true
}

func f32(b []byte) float32 { return math.Float32frombits(le.Uint32(b)) }

func head(dst []byte, typ byte) []byte { return append(dst, wireVersion, typ) }

func appendHello(dst []byte, t Token) []byte { return append(head(dst, typeHello), t[:]...) }

func appendBound(dst []byte, sender int32) []byte {
	return le.AppendUint32(head(dst, typeBound), uint32(sender))
}

func appendUnknown(dst []byte) []byte { return head(dst, typeUnknown) }

func appendVoice(dst []byte, typ byte, p voicePacket) []byte {
	dst = le.AppendUint32(head(dst, typ), p.id)
	dst = le.AppendUint16(dst, p.spurt)
	dst = le.AppendUint16(dst, p.seq)
	dst = le.AppendUint16(dst, uint16(len(p.payload)))
	return append(dst, p.payload...)
}

func appendBye(dst []byte, tag uint32) []byte { return le.AppendUint32(head(dst, typeBye), tag) }

func appendRoster(dst []byte, key Key, ms []member) []byte {
	dst = le.AppendUint16(append(head(dst, typeRoster), key[:]...), uint16(len(ms)))
	for _, m := range ms {
		dst = append(dst, m.token[:]...)
		dst = le.AppendUint32(dst, m.tag)
		dst = le.AppendUint32(dst, uint32(m.sender))
	}
	return dst
}

func appendPos(dst []byte, key Key, ps []position) []byte {
	dst = le.AppendUint16(append(head(dst, typePos), key[:]...), uint16(len(ps)))
	for _, p := range ps {
		dst = le.AppendUint32(dst, uint32(p.sender))
		dst = le.AppendUint32(dst, math.Float32bits(p.at.x))
		dst = le.AppendUint32(dst, math.Float32bits(p.at.y))
		dst = le.AppendUint32(dst, math.Float32bits(p.at.z))
	}
	return dst
}

func appendQuit(dst []byte) []byte { return head(dst, typeQuit) }

func appendTapList(dst []byte, key Key, ids []int32) []byte {
	dst = le.AppendUint16(append(head(dst, typeTapList), key[:]...), uint16(len(ids)))
	for _, id := range ids {
		dst = le.AppendUint32(dst, uint32(id))
	}
	return dst
}

func appendRange(dst []byte, key Key, meters float32) []byte {
	dst = le.AppendUint16(append(head(dst, typeRange), key[:]...), 1)
	return le.AppendUint32(dst, math.Float32bits(meters))
}

func appendTap(dst []byte, key Key, p voicePacket) []byte {
	dst = append(head(dst, typeTap), key[:]...)
	dst = le.AppendUint32(dst, p.id)
	dst = le.AppendUint16(dst, p.spurt)
	dst = le.AppendUint16(dst, p.seq)
	dst = le.AppendUint16(dst, uint16(len(p.payload)))
	return append(dst, p.payload...)
}
