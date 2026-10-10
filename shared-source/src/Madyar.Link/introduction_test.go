package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	libp2p "github.com/libp2p/go-libp2p"
	"github.com/libp2p/go-libp2p/core/network"
	"github.com/libp2p/go-libp2p/core/peer"
	"github.com/libp2p/go-libp2p/core/peerstore"
	"github.com/libp2p/go-libp2p/core/protocol"
	relayclient "github.com/libp2p/go-libp2p/p2p/protocol/circuitv2/client"
	relay "github.com/libp2p/go-libp2p/p2p/protocol/circuitv2/relay"
	ma "github.com/multiformats/go-multiaddr"
	"net"
	"strings"
	"testing"
	"time"
)

// Fails if helper rejects the approved capability/update command shape.
func TestIntroductionCommandContract(t *testing.T) {
	for _, raw := range []string{
		`{"op":"invites","invites":[{"id":"035d904a-84aa-40c1-a13f-ff128a2e98d3","expiresUtc":"2026-11-01T00:00:00Z"}]}`,
		`{"op":"intro_send","id":"035d904a-84aa-40c1-a13f-ff128a2e98d3","to":"peer","invite":"035d904a-84aa-40c1-a13f-ff128a2e98d3","text":"MDC1-code"}`,
	} {
		if _, err := parseCommand([]byte(raw)); err != nil {
			t.Fatalf("introduction wire command rejected: %v", err)
		}
	}
}

// Fails if a zero-contact issuer cannot negotiate the independent request protocol.
func TestIntroductionProtocolAvailableWithoutContacts(t *testing.T) {
	a, _ := testNode(t)
	b, _ := testNode(t)
	ctx, cancel := context.WithTimeout(a.ctx, 2*time.Second)
	defer cancel()
	if err := a.h.Connect(ctx, peer.AddrInfo{ID: b.h.ID(), Addrs: b.h.Addrs()}); err != nil {
		t.Fatal(err)
	}
	s, err := a.h.NewStream(ctx, b.h.ID(), protocol.ID("/madyar/contact/1"))
	if err != nil {
		t.Fatalf("introduction protocol missing: %v", err)
	}
	s.Reset()
}

const testInvite = "b5b34b8a-ccfb-4a45-b91c-c9f27ef100dc"
const requestID = "035d904a-84aa-40c1-a13f-ff128a2e98d3"

func introductionPair(t *testing.T) (*node, *node, <-chan event, <-chan event) {
	t.Helper()
	sender, se := testNode(t)
	issuer, ie := testNode(t)
	if err := sender.SetContacts([]string{issuer.h.ID().String()}); err != nil {
		t.Fatal(err)
	}
	if err := issuer.SetInvites([]issuedInvite{{testInvite, time.Now().UTC().Add(time.Hour)}}); err != nil {
		t.Fatal(err)
	}
	return sender, issuer, se, ie
}
func connectIntroduction(t *testing.T, a, b *node) {
	t.Helper()
	if err := a.h.Connect(a.ctx, peer.AddrInfo{ID: b.h.ID(), Addrs: b.h.Addrs()}); err != nil {
		t.Fatal(err)
	}
}
func pendingIntroduction(t *testing.T, a, b *node, events <-chan event) (event, <-chan error) {
	t.Helper()
	done := make(chan error, 1)
	go func() { done <- a.SendIntroduction(requestID, b.h.ID().String(), testInvite, "MDC1-transport-fixture") }()
	timer := time.NewTimer(5 * time.Second)
	defer timer.Stop()
	for {
		select {
		case e := <-events:
			if e.Op == "intro_received" {
				return e, done
			}
		case err := <-done:
			t.Fatalf("request failed before persistence: %v", err)
		case <-timer.C:
			t.Fatal("no introduction receipt")
		}
	}
}
func assertIntroACK(t *testing.T, a, b *node, ae, be <-chan event) {
	t.Helper()
	e, done := pendingIntroduction(t, a, b, be)
	if e.Peer != a.h.ID().String() || e.ID != requestID || e.Invite != testInvite || e.Text != "MDC1-transport-fixture" || e.Receipt == "" {
		t.Fatalf("wrong authenticated request %+v", e)
	}
	select {
	case err := <-done:
		t.Fatalf("ACK before storage decision: %v", err)
	case <-time.After(30 * time.Millisecond):
	}
	if err := b.SetContacts(nil); err != nil {
		t.Fatal(err)
	} // unknown requester receipt is not an ordinary-contact receipt
	b.Accept(e.Receipt, true)
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	if sent := awaitEvent(t, ae, "intro_sent"); !sent.Delivered || sent.ID != requestID {
		t.Fatalf("missing delivered intro: %+v", sent)
	}
	if b.contact(a.h.ID()) {
		t.Fatal("transport auto-added requester")
	}
	if err := a.Send(requestID, b.h.ID().String(), "text", "still forbidden"); err == nil {
		t.Fatal("unknown ordinary message was accepted after request ACK")
	}
}
func TestIntroductionDirectDurableACK(t *testing.T) {
	a, b, ae, be := introductionPair(t)
	connectIntroduction(t, a, b)
	assertIntroACK(t, a, b, ae, be)
}
func TestIntroductionRelayDurableACK(t *testing.T) {
	a, b, ae, be := introductionPair(t)
	r, err := libp2p.New(libp2p.ListenAddrStrings("/ip4/127.0.0.1/tcp/0"))
	if err != nil {
		t.Fatal(err)
	}
	defer r.Close()
	service, err := relay.New(r)
	if err != nil {
		t.Fatal(err)
	}
	defer service.Close()
	if _, err = relayclient.Reserve(b.ctx, b.h, peer.AddrInfo{ID: r.ID(), Addrs: r.Addrs()}); err != nil {
		t.Fatal(err)
	}
	addr := r.Addrs()[0].Encapsulate(ma.StringCast("/p2p/" + r.ID().String() + "/p2p-circuit"))
	a.h.Peerstore().AddAddrs(b.h.ID(), []ma.Multiaddr{addr}, peerstore.TempAddrTTL)
	assertIntroACK(t, a, b, ae, be)
	limited := false
	for _, c := range a.h.Network().ConnsToPeer(b.h.ID()) {
		limited = limited || c.Stat().Limited
	}
	if !limited {
		t.Fatal("request fixture bypassed relay")
	}
}
func TestIntroductionCapabilityRevokeExpiryAndDisable(t *testing.T) {
	for _, mode := range []string{"revoke", "expire", "disable", "negative"} {
		t.Run(mode, func(t *testing.T) {
			a, b, ae, be := introductionPair(t)
			connectIntroduction(t, a, b)
			if mode == "expire" {
				if err := b.SetInvites([]issuedInvite{{testInvite, time.Now().Add(150 * time.Millisecond)}}); err != nil {
					t.Fatal(err)
				}
			}
			e, done := pendingIntroduction(t, a, b, be)
			switch mode {
			case "revoke":
				if err := b.SetInvites(nil); err != nil {
					t.Fatal(err)
				}
			case "expire":
				time.Sleep(200 * time.Millisecond)
			case "disable":
				b.Close()
			}
			b.Accept(e.Receipt, mode != "negative")
			select {
			case err := <-done:
				if err == nil {
					t.Fatal("stale/negative capability ACKed")
				}
			case <-time.After(2 * time.Second):
				t.Fatal("request not cancelled")
			}
			if sent := awaitEvent(t, ae, "intro_sent"); sent.Delivered {
				t.Fatal("rejected request delivered")
			}
		})
	}
}
func TestIntroductionInvalidCapabilitiesDoNotReplaceCurrent(t *testing.T) {
	a, b, _, be := introductionPair(t)
	connectIntroduction(t, a, b)
	invalid := [][]issuedInvite{{{testInvite, time.Now().Add(-time.Second)}}, {{testInvite, time.Now().Add(91 * 24 * time.Hour)}}, {{"not-a-capability", time.Now().Add(time.Hour)}}, {{testInvite, time.Now().Add(time.Hour)}, {testInvite, time.Now().Add(time.Hour)}}}
	tooMany := make([]issuedInvite, 17)
	for i := range tooMany {
		tooMany[i] = issuedInvite{fmt.Sprintf("10000000-0000-4000-8000-%012d", i+1), time.Now().Add(time.Hour)}
	}
	invalid = append(invalid, tooMany)
	for _, v := range invalid {
		if err := b.SetInvites(v); err == nil {
			t.Fatal("invalid capability replacement accepted")
		}
	}
	e, done := pendingIntroduction(t, a, b, be)
	b.Accept(e.Receipt, true)
	if err := <-done; err != nil {
		t.Fatal("invalid update destroyed active capability", err)
	}
}
func TestIntroductionRejectsMalformedInboundBeforeEvent(t *testing.T) {
	a, b, _, be := introductionPair(t)
	connectIntroduction(t, a, b)
	valid := introductionEnvelope{Version: 1, ID: requestID, To: b.h.ID().String(), Invite: testInvite, Code: "MDC1-fixture"}
	encode := func(e introductionEnvelope) string { raw, _ := json.Marshal(e); return string(raw) }
	wrongTarget := valid
	wrongTarget.To = a.h.ID().String()
	wrongCap := valid
	wrongCap.Invite = "10000000-0000-4000-8000-000000000001"
	oversized := valid
	oversized.Code = strings.Repeat("a", 4097)
	rawValid := encode(valid)
	for _, raw := range []string{encode(wrongTarget), encode(wrongCap), encode(oversized), strings.Replace(rawValid, `"version":1`, `"version":1,"version":1`, 1), strings.Replace(rawValid, `"version":1`, `"Version":1`, 1), strings.Replace(rawValid, `"version":1`, `"version":1,"extra":true`, 1), strings.Replace(rawValid, "MDC1-fixture", string([]byte{255}), 1), strings.Repeat("a", 8192)} {
		s, err := a.h.NewStream(network.WithAllowLimitedConn(a.ctx, "introduction rejection test"), b.h.ID(), introductionProtocol)
		if err != nil {
			t.Fatal(err)
		}
		s.SetDeadline(time.Now().Add(time.Second))
		fmt.Fprintln(s, raw)
		var ack envelope
		err = json.NewDecoder(s).Decode(&ack)
		s.Close()
		if err == nil {
			t.Fatal("malformed request received ACK")
		}
	}
	for {
		select {
		case e := <-be:
			if e.Op == "intro_received" {
				t.Fatal("invalid request emitted", e)
			}
		default:
			return
		}
	}
}
func TestIntroductionBoundedInitialReadsAndDeadline(t *testing.T) {
	a, b, _, _ := introductionPair(t)
	connectIntroduction(t, a, b)
	var streams []network.Stream
	defer func() {
		for _, s := range streams {
			s.Reset()
		}
	}()
	for i := 0; i < 8; i++ {
		s, err := a.h.NewStream(a.ctx, b.h.ID(), introductionProtocol)
		if err != nil {
			t.Fatal(err)
		}
		if _, err = s.Write([]byte("{")); err != nil {
			t.Fatal(err)
		}
		streams = append(streams, s)
	}
	until := time.Now().Add(time.Second)
	for len(b.slots) < 8 && time.Now().Before(until) {
		time.Sleep(time.Millisecond)
	}
	if len(b.slots) != 8 {
		t.Fatal("fixture did not occupy all inbound workers")
	}
	ninth, err := a.h.NewStream(a.ctx, b.h.ID(), introductionProtocol)
	if err != nil {
		t.Fatal(err)
	}
	defer ninth.Close()
	ninth.SetDeadline(time.Now().Add(time.Second))
	ninth.Write([]byte("{"))
	var one [1]byte
	if _, err = ninth.Read(one[:]); err == nil {
		t.Fatal("ninth request not bounded")
	} else if timeout, ok := err.(net.Error); ok && timeout.Timeout() {
		t.Fatal("ninth request timed out instead of being rejected")
	}
	streams[0].SetReadDeadline(time.Now().Add(6 * time.Second))
	started := time.Now()
	if _, err = streams[0].Read(one[:]); err == nil {
		t.Fatal("idle initial request not closed")
	}
	if time.Since(started) > 5500*time.Millisecond {
		t.Fatal("initial untrusted read exceeds 5-second deadline")
	}
}
func TestIntroductionNamespaceDomainAndIssuerPin(t *testing.T) {
	a, _ := testNode(t)
	b, _ := testNode(t)
	// Independent literal domain calculation, guards accidental use of ordinary pair rendezvous.
	h := sha256.Sum256([]byte("madyar/invite/1\x00" + a.h.ID().String() + "\x00" + testInvite))
	want := "madyar/invite/1/" + hex.EncodeToString(h[:])
	if inviteNamespace(a.h.ID(), testInvite) != want || inviteNamespace(b.h.ID(), testInvite) == want {
		t.Fatal("wrong invitation rendezvous domain/issuer")
	}
}
func TestIntroductionJSONReceiptCommand(t *testing.T) {
	input := `{"op":"start","seed":"` + base64.StdEncoding.EncodeToString(bytes.Repeat([]byte{1}, 32)) + `","internet":false}` + "\n" + `{"op":"invites","invites":[{"id":"` + testInvite + `","expiresUtc":"` + time.Now().Add(time.Hour).UTC().Format(time.RFC3339Nano) + `"}]}` + "\n"
	var output bytes.Buffer
	if err := run(context.Background(), strings.NewReader(input), &output); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(output.String(), `"detail":"invites_updated"`) {
		t.Fatal("missing processed capability update acknowledgement", output.String())
	}
}
