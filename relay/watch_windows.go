package main

import (
	"fmt"
	"syscall"
	"time"
)

const (
	synchronize             = 0x00100000
	processQueryLimitedInfo = 0x1000
	stillActive             = 259
)

func watchParent(pid int, gone chan<- string, log *logger) error {
	h, err := syscall.OpenProcess(synchronize|processQueryLimitedInfo, false, uint32(pid))
	if err != nil {
		return err
	}
	go func() {
		ev, err := syscall.WaitForSingleObject(h, syscall.INFINITE)
		if ev == syscall.WAIT_OBJECT_0 {
			gone <- "wait"
			return
		}
		log.printf("waiting on the server process failed (%#x, %v); polling it instead", ev, err)
	}()
	go func() {
		for range time.Tick(2 * time.Second) {
			var code uint32
			if err := syscall.GetExitCodeProcess(h, &code); err != nil {
				log.printf("polling the server process failed: %v", err)
				return
			}
			if code != stillActive {
				gone <- fmt.Sprintf("poll, exit code %d", code)
				return
			}
		}
	}()
	return nil
}

func platform() string {
	wine := false
	if ntdll, err := syscall.LoadDLL("ntdll.dll"); err == nil {
		_, err = ntdll.FindProc("wine_get_version")
		wine = err == nil
	}
	v, _ := syscall.GetVersion()
	s := fmt.Sprintf("windows %d.%d.%d", byte(v), byte(v>>8), uint16(v>>16))
	if wine {
		s += " under wine"
	}
	return s
}
