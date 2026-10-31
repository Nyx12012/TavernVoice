package main

import (
	"syscall"
	"time"
)

func watchParent(pid int, gone chan<- string, log *logger) error {
	if err := syscall.Kill(pid, 0); err != nil {
		return err
	}
	go func() {
		for range time.Tick(2 * time.Second) {
			if syscall.Kill(pid, 0) != nil {
				gone <- "poll"
				return
			}
		}
	}()
	return nil
}

func platform() string { return "linux test build" }
