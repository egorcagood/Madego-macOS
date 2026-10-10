package main

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"encoding/base64"
	"encoding/json"
	"strings"
	"testing"

	"github.com/libp2p/go-libp2p/core/crypto"
	"github.com/libp2p/go-libp2p/core/peer"
)

// Fails if start derives a fresh identity, exposes the seed, or EOF leaves the process alive.
func TestJSONLinesIdentityEOF(t *testing.T) {
	seed := bytes.Repeat([]byte{7}, 32)
	key, _ := crypto.UnmarshalEd25519PrivateKey(ed25519.NewKeyFromSeed(seed))
	want, _ := peer.IDFromPrivateKey(key)
	input := `{"op":"start","seed":"` + base64.StdEncoding.EncodeToString(seed) + `","contacts":[],"internet":false}` + "\n"
	var output bytes.Buffer
	if err := run(context.Background(), strings.NewReader(input), &output); err != nil {
		t.Fatal(err)
	}
	var e event
	if err := json.NewDecoder(&output).Decode(&e); err != nil {
		t.Fatal(err)
	}
	if e.Op != "ready" || e.Peer != want.String() || len(e.Addresses) == 0 {
		t.Fatalf("unexpected ready %+v", e)
	}
}
func TestRejectInvalidInitialization(t *testing.T) {
	for _, raw := range []string{`{"op":"start","seed":"AA==","internet":false}`, `{"op":"send"}`, strings.Repeat("a", 2*1024*1024+1)} {
		var output bytes.Buffer
		if err := run(context.Background(), strings.NewReader(raw+"\n"), &output); err == nil {
			t.Fatal("accepted invalid init")
		}
	}
}

// A successful command completion signal lets a process caller avoid racing new contact pins.
func TestJSONFixtureCommandCompletion(t *testing.T) {
	seed := bytes.Repeat([]byte{7}, 32)
	remoteKey, _ := crypto.UnmarshalEd25519PrivateKey(ed25519.NewKeyFromSeed(bytes.Repeat([]byte{8}, 32)))
	remote, _ := peer.IDFromPrivateKey(remoteKey)
	input := `{"op":"start","seed":"` + base64.StdEncoding.EncodeToString(seed) + `","internet":false}` + "\n" + `{"op":"contacts","contacts":["` + remote.String() + `"]}` + "\n" + `{"op":"fixtureaddr","peer":"` + remote.String() + `","addresses":["/ip4/127.0.0.1/tcp/4321"]}` + "\n"
	var output bytes.Buffer
	if err := run(context.Background(), strings.NewReader(input), &output); err != nil {
		t.Fatal(err)
	}
	decoder := json.NewDecoder(&output)
	seen := false
	for decoder.More() {
		var e event
		if decoder.Decode(&e) != nil {
			break
		}
		if e.Op == "state" && e.Detail == "fixture_ready" {
			seen = true
		}
	}
	if !seen {
		t.Fatal("no processed-fixture completion signal")
	}
}
