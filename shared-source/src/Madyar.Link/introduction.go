package main

import (
	"bufio"
	"bytes"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"time"
	"unicode/utf16"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/libp2p/go-libp2p/core/discovery"
	"github.com/libp2p/go-libp2p/core/network"
	"github.com/libp2p/go-libp2p/core/peer"
	"github.com/libp2p/go-libp2p/core/protocol"
	routing "github.com/libp2p/go-libp2p/p2p/discovery/routing"
)

const introductionProtocol protocol.ID = "/madyar/contact/1"
const maxIntroductionFrame = 8192
const maxInvites = 16
const invitationLifetime = 90 * 24 * time.Hour
const introductionReadTimeout = 5 * time.Second

type issuedInvite struct {
	ID         string    `json:"id"`
	ExpiresUtc time.Time `json:"expiresUtc"`
}
type introductionEnvelope struct {
	Version int    `json:"version"`
	ID      string `json:"id"`
	To      string `json:"to"`
	Invite  string `json:"invite"`
	Code    string `json:"code"`
}
type introductionACK struct {
	Version  int    `json:"version"`
	ID       string `json:"id"`
	To       string `json:"to"`
	Accepted bool   `json:"accepted"`
}

func validCapabilityID(id string) bool {
	u, err := uuid.Parse(id)
	return err == nil && u != uuid.Nil && u.String() == id
}

// Replace the full bounded registry atomically. Invalid updates retain the previous capabilities.
func (n *node) SetInvites(invites []issuedInvite) error {
	if len(invites) > maxInvites {
		return errors.New("too_many_invites")
	}
	now := time.Now()
	next := make(map[string]time.Time, len(invites))
	for _, v := range invites {
		if !validCapabilityID(v.ID) || !v.ExpiresUtc.After(now) || v.ExpiresUtc.After(now.Add(invitationLifetime)) {
			return errors.New("invalid_invites")
		}
		if _, exists := next[v.ID]; exists {
			return errors.New("invalid_invites")
		}
		next[v.ID] = v.ExpiresUtc
	}
	n.mu.Lock()
	defer n.mu.Unlock()
	if n.closed {
		return errors.New("disabled")
	}
	n.invites = next
	for token, p := range n.pending {
		if p.invite != "" && !n.activeInviteLocked(p.invite, time.Now()) {
			select {
			case p.result <- false:
			default:
			}
			delete(n.pending, token)
		}
	}
	select {
	case n.discoveryRefresh <- struct{}{}:
	default:
	}
	return nil
}
func (n *node) activeInviteLocked(id string, now time.Time) bool {
	expiry, ok := n.invites[id]
	return !n.closed && ok && expiry.After(now)
}
func (n *node) activeInvites() []issuedInvite {
	n.mu.Lock()
	defer n.mu.Unlock()
	now := time.Now()
	result := make([]issuedInvite, 0, len(n.invites))
	if n.closed {
		return result
	}
	for id, expiry := range n.invites {
		if expiry.After(now) {
			result = append(result, issuedInvite{id, expiry})
		} else {
			delete(n.invites, id)
		}
	}
	return result
}
func inviteNamespace(issuer peer.ID, invite string) string {
	sum := sha256.Sum256([]byte("madyar/invite/1\x00" + issuer.String() + "\x00" + invite))
	return "madyar/invite/1/" + hex.EncodeToString(sum[:])
}
func validIntroduction(e introductionEnvelope) bool {
	if e.Version != 1 || !validCapabilityID(e.ID) || !validCapabilityID(e.Invite) || e.Code == "" || !utf8.ValidString(e.Code) || len(utf16.Encode([]rune(e.Code))) > 4096 {
		return false
	}
	if _, err := peer.Decode(e.To); err != nil {
		return false
	}
	raw, err := json.Marshal(e)
	return err == nil && len(raw)+1 <= maxIntroductionFrame
}

// This protocol has an exact envelope: reject duplicates/case variants, trailing objects and invalid UTF-8.
func readIntroductionFrame(r io.Reader, v any, fields ...string) error {
	raw, err := bufio.NewReader(io.LimitReader(r, maxIntroductionFrame+1)).ReadBytes('\n')
	if err != nil {
		return errors.New("invalid_intro_frame")
	}
	if len(raw) > maxIntroductionFrame || !utf8.Valid(raw) {
		return errors.New("invalid_intro_frame")
	}
	decoder := json.NewDecoder(bytes.NewReader(raw))
	token, err := decoder.Token()
	if err != nil || token != json.Delim('{') {
		return errors.New("invalid_intro_frame")
	}
	seen := map[string]bool{}
	allowed := map[string]bool{}
	for _, name := range fields {
		allowed[name] = true
	}
	for decoder.More() {
		token, err = decoder.Token()
		if err != nil {
			return errors.New("invalid_intro_frame")
		}
		name, ok := token.(string)
		if !ok || !allowed[name] || seen[name] {
			return errors.New("invalid_intro_frame")
		}
		seen[name] = true
		var value json.RawMessage
		if decoder.Decode(&value) != nil {
			return errors.New("invalid_intro_frame")
		}
	}
	if _, err = decoder.Token(); err != nil || len(seen) != len(fields) {
		return errors.New("invalid_intro_frame")
	}
	var extra any
	if decoder.Decode(&extra) != io.EOF {
		return errors.New("invalid_intro_frame")
	}
	if json.Unmarshal(raw, v) != nil {
		return errors.New("invalid_intro_frame")
	}
	return nil
}
func writeIntroductionFrame(w io.Writer, v any) error {
	raw, err := json.Marshal(v)
	if err != nil || len(raw)+1 > maxIntroductionFrame {
		return errors.New("invalid_intro_frame")
	}
	return writeFrame(w, v)
}

func (n *node) receiveIntroduction(s network.Stream) {
	if !n.begin() {
		s.Reset()
		return
	}
	defer n.wg.Done()
	defer s.Close()
	select {
	case n.slots <- struct{}{}:
		defer func() { <-n.slots }()
	default:
		s.Reset()
		return
	}
	s.SetDeadline(time.Now().Add(introductionReadTimeout))
	var e introductionEnvelope
	if readIntroductionFrame(s, &e, "version", "id", "to", "invite", "code") != nil || !validIntroduction(e) || e.To != n.h.ID().String() || s.Conn().RemotePeer() == n.h.ID() {
		s.Reset()
		return
	}
	sender := s.Conn().RemotePeer()
	random := make([]byte, 24)
	if _, err := rand.Read(random); err != nil {
		s.Reset()
		return
	}
	token := hex.EncodeToString(random)
	result := make(chan bool, 1)
	n.mu.Lock()
	if !n.activeInviteLocked(e.Invite, time.Now()) {
		n.mu.Unlock()
		s.Reset()
		return
	}
	expiry := n.invites[e.Invite]
	n.pending[token] = pendingReceipt{peer: sender, result: result, invite: e.Invite}
	n.mu.Unlock()
	defer func() { n.mu.Lock(); delete(n.pending, token); n.mu.Unlock() }()
	deadline := time.Now().Add(streamTimeout)
	if expiry.Before(deadline) {
		deadline = expiry
	}
	s.SetDeadline(deadline)
	// C# validates the signed return contact against this authenticated RemotePeer and persists before accept.
	n.emit(event{Op: "intro_received", Peer: sender.String(), ID: e.ID, Invite: e.Invite, Text: e.Code, Receipt: token})
	timer := time.NewTimer(time.Until(deadline))
	defer timer.Stop()
	select {
	case accepted := <-result:
		n.mu.Lock()
		if accepted && n.activeInviteLocked(e.Invite, time.Now()) {
			// Serialize the last capability check with revocation; cap a blocked ACK write so disable remains bounded.
			if limit := time.Now().Add(time.Second); limit.Before(deadline) {
				s.SetWriteDeadline(limit)
			}
			if writeIntroductionFrame(s, introductionACK{1, e.ID, sender.String(), true}) != nil {
				s.Reset()
			}
		} else {
			s.Reset()
		}
		n.mu.Unlock()
	case <-timer.C:
		s.Reset()
	case <-n.ctx.Done():
		s.Reset()
	}
}
func (n *node) SendIntroduction(id, to, invite, code string) (err error) {
	defer func() {
		detail := "acknowledged"
		if err != nil {
			detail = err.Error()
		}
		n.emit(event{Op: "intro_sent", Peer: to, ID: id, Delivered: err == nil, Detail: detail})
	}()
	e := introductionEnvelope{1, id, to, invite, code}
	if !validIntroduction(e) {
		return errors.New("invalid_introduction")
	}
	recipient, _ := peer.Decode(to)
	if recipient == n.h.ID() {
		return errors.New("invalid_recipient")
	}
	if !n.begin() {
		return errors.New("disabled")
	}
	defer n.wg.Done()
	select {
	case n.introSends <- struct{}{}:
		defer func() { <-n.introSends }()
	default:
		return errors.New("busy")
	}
	ctx, cancel := context.WithTimeout(n.ctx, streamTimeout)
	defer cancel()
	if len(n.h.Peerstore().Addrs(recipient)) == 0 && n.dht != nil {
		n.findInvitationIssuer(ctx, recipient, invite)
	}
	s, openErr := n.h.NewStream(network.WithAllowLimitedConn(ctx, "madyar contact introduction"), recipient, introductionProtocol)
	if openErr != nil && n.dht != nil && ctx.Err() == nil {
		n.findInvitationIssuer(ctx, recipient, invite)
		s, openErr = n.h.NewStream(network.WithAllowLimitedConn(ctx, "madyar contact introduction"), recipient, introductionProtocol)
	}
	if openErr != nil {
		return errors.New("route_unavailable")
	}
	defer s.Close()
	deadline, _ := ctx.Deadline()
	s.SetDeadline(deadline)
	if s.Conn().RemotePeer() != recipient || n.ctx.Err() != nil {
		s.Reset()
		return errors.New("peer_rejected")
	}
	if writeIntroductionFrame(s, e) != nil {
		s.Reset()
		return errors.New("send_failed")
	}
	var ack introductionACK
	if readIntroductionFrame(s, &ack, "version", "id", "to", "accepted") != nil {
		return errors.New("ack_missing")
	}
	if ack.Version != 1 || ack.ID != id || ack.To != n.h.ID().String() || !ack.Accepted || n.ctx.Err() != nil {
		return errors.New("ack_rejected")
	}
	return nil
}
func (n *node) findInvitationIssuer(ctx context.Context, issuer peer.ID, invite string) {
	router := routing.NewRoutingDiscovery(n.dht)
	found, err := router.FindPeers(ctx, inviteNamespace(issuer, invite), discovery.Limit(16))
	if err != nil {
		return
	}
	for info := range found {
		if info.ID == issuer && ctx.Err() == nil {
			n.h.Connect(network.WithAllowLimitedConn(ctx, "madyar pinned invitation issuer"), info)
			return
		}
	}
}
func (n *node) advertiseInvites(router *routing.RoutingDiscovery, advertised map[string]time.Time) {
	active := n.activeInvites()
	current := map[string]bool{}
	for _, invite := range active {
		current[invite.ID] = true
		if time.Since(advertised[invite.ID]) < 30*time.Minute {
			continue
		}
		ctx, cancel := context.WithTimeout(n.ctx, 10*time.Second)
		ttl := time.Until(invite.ExpiresUtc)
		if ttl > time.Hour {
			ttl = time.Hour
		}
		if ttl <= 0 {
			cancel()
			continue
		}
		_, err := router.Advertise(ctx, inviteNamespace(n.h.ID(), invite.ID), discovery.TTL(ttl))
		cancel()
		if err == nil {
			advertised[invite.ID] = time.Now()
		} else {
			n.emit(event{Op: "state", Detail: "invite_advertise_unavailable"})
		}
		if n.ctx.Err() != nil {
			return
		}
	}
	for id := range advertised {
		if !current[id] {
			delete(advertised, id)
		}
	}
}
