package main

import (
	"math"
	"time"
)

const (
	bucketBurst      = 55
	bucketRefill     = 50
	newSpurtMaxSeq   = 25
	restartSpurtJump = 16
	restartGap       = 10 * time.Second
	lateFrames       = 12
)

type verdict int

const (
	passed verdict = iota
	passedLate
	overLimit
	outOfOrder
)

func (v verdict) ok() bool { return v == passed || v == passedLate }

type validator struct {
	tokens  float64
	last    time.Time
	spurt   uint16
	seq     uint16
	seen    uint64
	started bool
}

func newValidator(now time.Time) validator {
	return validator{tokens: bucketBurst, last: now}
}

func (v *validator) accept(spurt, seq uint16, now time.Time) verdict {
	gap := now.Sub(v.last)
	v.tokens = math.Min(bucketBurst, v.tokens+gap.Seconds()*bucketRefill)
	v.last = now
	if v.tokens < 1 {
		return overLimit
	}
	v.tokens--

	if !v.started || spurt != v.spurt {
		back := int16(spurt - v.spurt)
		if v.started && back <= 0 && back > -restartSpurtJump && gap <= restartGap {
			return outOfOrder
		}
		if seq >= newSpurtMaxSeq {
			return outOfOrder
		}
		v.started, v.spurt, v.seq, v.seen = true, spurt, seq, 1
		return passed
	}
	advance := seq - v.seq
	if advance == 0 {
		return outOfOrder
	}
	if advance < 0x8000 {
		if advance >= 64 {
			v.seen = 0
		} else {
			v.seen <<= advance
		}
		v.seen |= 1
		v.seq = seq
		return passed
	}
	back := v.seq - seq
	if back > lateFrames {
		return outOfOrder
	}
	bit := uint64(1) << back
	if v.seen&bit != 0 {
		return outOfOrder
	}
	v.seen |= bit
	return passedLate
}
