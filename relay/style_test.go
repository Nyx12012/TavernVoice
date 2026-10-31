package main

import (
	"go/parser"
	"go/token"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestPublicStyle(t *testing.T) {
	files, _ := filepath.Glob("*")
	for _, f := range files {
		b, err := os.ReadFile(f)
		if err != nil {
			continue
		}
		for _, dash := range []string{string(rune(0x2014)), string(rune(0x2013))} {
			if strings.Contains(string(b), dash) {
				t.Errorf("%s has a %U dash", f, []rune(dash)[0])
			}
		}
		if !strings.HasSuffix(f, ".go") {
			continue
		}
		parsed, err := parser.ParseFile(token.NewFileSet(), f, b, parser.ParseComments)
		if err != nil {
			t.Errorf("%s: %v", f, err)
			continue
		}
		if len(parsed.Comments) > 0 {
			t.Errorf("%s has %d comments", f, len(parsed.Comments))
		}
	}
}
