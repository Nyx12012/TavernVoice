package main

import (
	"fmt"
	"io"
	"sync"
	"time"
)

const logEvery = 10 * time.Second

type logger struct {
	mu      sync.Mutex
	out     chan string
	done    chan struct{}
	closed  bool
	dropped int
	last    map[string]time.Time
	held    map[string]int
}

func newLogger(w io.Writer) *logger {
	l := &logger{
		out:  make(chan string, 256),
		done: make(chan struct{}),
		last: map[string]time.Time{},
		held: map[string]int{},
	}
	go func() {
		for line := range l.out {
			fmt.Fprintln(w, line)
		}
		close(l.done)
	}()
	return l
}

func (l *logger) printf(format string, args ...interface{}) {
	line := fmt.Sprintf(format, args...)
	l.mu.Lock()
	defer l.mu.Unlock()
	l.put(line)
}

func (l *logger) limited(key, format string, args ...interface{}) {
	now := time.Now()
	l.mu.Lock()
	defer l.mu.Unlock()
	if at, ok := l.last[key]; ok && now.Sub(at) < logEvery {
		l.held[key]++
		return
	}
	l.last[key] = now
	line := fmt.Sprintf(format, args...)
	if n := l.held[key]; n > 0 {
		line += fmt.Sprintf(" (and %d more like it)", n)
		delete(l.held, key)
	}
	l.put(line)
}

func (l *logger) put(line string) {
	if l.closed {
		return
	}
	if l.dropped > 0 {
		select {
		case l.out <- fmt.Sprintf("(%d log lines dropped: nothing was reading)", l.dropped):
			l.dropped = 0
		default:
		}
	}
	select {
	case l.out <- line:
	default:
		l.dropped++
	}
}

func (l *logger) close(wait time.Duration) {
	l.mu.Lock()
	if !l.closed {
		l.closed = true
		close(l.out)
	}
	l.mu.Unlock()
	select {
	case <-l.done:
	case <-time.After(wait):
	}
}
