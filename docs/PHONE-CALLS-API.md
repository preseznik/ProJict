# Phone calls API v1

For ProJictAndroid `d03a6a2f` / PC `d6b701ce`. Available from ProJict 0.1.48. You call one of your agents from the
phone while the app is open. It is the same call as one started on the PC (the
agent is rung, picks up with `answer_call`, hears you as messages in your private
conversation and answers there). The difference is where the audio is: ProJict
hears the **phone's** microphone instead of the PC's, and the agent's voice plays on
the **phone** instead of the PC's speakers. The PC opens no call window.

Use the paired HTTPS origin, exact certificate pin and a **You, on your phone**
token, as for the rest of `/api/v1/me`. Read-only tokens can list but not call.
One call at a time per PC, as always. A call belongs to the token that started it:
other tokens get 404 for it.

## Control (REST)

- `GET /api/v1/me/calls` returns
  `{"apiVersion":1,"canCall":true,"busy":false,"agents":[{"name":"Lark","canTakeCall":true}],"call":null|<call>}`.
  `agents` are your agents connected to the PC now; `canTakeCall` is true while one
  hears Chat right away (only those can be rung). `busy` means the PC is in a call
  that isn't this phone's. `call` is this phone's current or last call.
- `POST /api/v1/me/calls` with `{"agent":"Lark","language":null}` → **201**
  `{"apiVersion":1,"call":<call>,"media":{...}}`. `language` is optional: one of
  the PC's call languages (`"sl"`, `"en"`, …) or null for the PC's setting.
  Only those two properties are accepted.
- `GET /api/v1/me/calls/{call}` → `{"apiVersion":1,"call":<call>}`.
- `POST /api/v1/me/calls/{call}/mute` with `{"muted":true|false}`.
- `POST /api/v1/me/calls/{call}/stop-speaking` with no body: cuts the agent's
  answer off (the phone then gets `audio_stop`).
- `POST /api/v1/me/calls/{call}/end` with no body. Ending a call that's already
  over returns how it ended (200), so it is safe after a lost answer.

`{call}` is the full ID from the API (36- or 32-character GUID).

The call object:

```json
{
  "id": "…", "channelId": "…", "originId": "…", "agent": "Lark",
  "state": "ringing | in_call | ended",
  "agentState": "ringing | joined | declined | missed | left | cant_now",
  "muted": false, "speaking": "you | agent | null",
  "problem": null, "preparing": null,
  "hearing": "elevenlabs | whisper | windows", "voices": "elevenlabs | windows",
  "language": null, "startedAt": "…", "endedAt": null, "endReason": null
}
```

`channelId` is the call's own chat (what was said stays there); `originId` is your
private conversation with the agent. `problem` is a sentence to show (no microphone
sound, a speech service failing over to Windows' voices, …). `hearing`/`voices`
say which speech services the PC uses for this call (Settings › Chat on the PC;
ElevenLabs costs credits). Windows' online dictation can only hear the PC's
microphone, so a phone call uses Windows' offline recognizer instead of it.

`endReason` once ended: `hung_up` (the phone ended it), `declined`, `no_answer`,
`agent_left`, `cant_take_calls`, `media_closed`, `media_lost`, `media_not_connected`,
`bad_audio`, `phone_too_slow`, `token_invalid`, `token_paused`, `token_renewed`,
`token_revoked` or `ended`.

## Media (WebSocket)

`GET /api/v1/me/calls/{call}/media` as a WebSocket upgrade, with the same bearer
token and pin. Open it right after starting the call: if it isn't connected
within 20 seconds the call ends (`media_not_connected`). One connection per call
(a second gets 409). There is no resume in v1: when the connection closes, the call
ends; call again to continue.

- **Phone → PC, binary:** the microphone as PCM, signed 16-bit little-endian,
  16 kHz, mono. Send about every 100 ms (3,200 bytes); frames must be an even
  number of bytes, at most 6,400 (200 ms). Up to 1.5× real time is accepted (bursts
  after a stall, up to 2 s); faster or malformed frames are dropped, and a phone
  that keeps sending them is cut off (`bad_audio`). While muted, frames are dropped
  on the PC, but the phone should stop sending them too.
- **PC → phone, binary:** the agent's voice in the same PCM format, about 100 ms
  per frame, sent at roughly real time with up to 400 ms lead. Play in order.
- **PC → phone, text (JSON):**
  - `{"type":"state","call":<call>}` when the call changes (and once on connect).
  - `{"type":"caption","who":"you|agent|projict","name":"Lark"|null,"text":"…"}`:
    what you said as heard, what the agent said, and ProJict's notes.
  - `{"type":"audio_stop"}`: drop any of the agent's audio not played yet.
- **Phone → PC, text:** ignored in v1.

The PC pings every 5 s and closes a connection that doesn't answer within 15 s
(OkHttp answers pings by itself). If the phone reads too slowly and the PC's
queue fills, the call ends (`phone_too_slow`) rather than buffering without
bound. When the call ends, the PC sends the final `state` event and closes
normally.

## Retry boundary

Don't repeat `POST /me/calls` after an uncertain result: read `GET /me/calls`. If
`call` is a live call of yours, connect its media or end it. Mute,
stop-speaking and end are safe to repeat. Pausing, renewing or revoking the
token ends its call at once. Expiry ends it within a second.

## Statuses

200/201 success; 400 malformed body or ID; 401 bearer problem at the gate; 403
wrong token kind, paused, read-only or expired; 404 no such agent connected / no
such call for this phone / older PC; 409 the PC is in a call, the agent can't take
calls now, the call is over (for mute/stop-speaking) or its media is connected
already.
