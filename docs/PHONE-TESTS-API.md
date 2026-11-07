# Phone To test API v1 — development contract

For ProJictAndroid `23969f37` / PC `3678453e`. This is new development work,
not a claim that the installed PC release already implements it. It requires a
PC build containing `PhoneTestsApi` v1; the first shipping PC version is still
to be assigned. The installed 0.1.47 phone surface predates these routes.

Use the existing paired HTTPS origin and exact PC certificate-DER fingerprint.
The bearer must be a **You, on your phone** token. Agent tokens/MCP/Sharing
credentials are not substitutes. Read-only phone tokens can list/read, not
answer. Frozen and expired tokens remain blocked by the API gate; new operations
also recheck mutable access/expiry on dispatch. Only the token's permitted local,
non-Inbox projects are included; shared/remote projects are excluded.

## Routes

- `GET /api/v1/me/tests`: all pending tests, oldest first (stable ID tie-break).
- `GET /api/v1/me/tests?project=<project-id>`: pending tests in one project.
- `GET /api/v1/me/projects/<project-id>/tests/<test-id>`: one test, including an
  answered/closed result so a client can inspect its state after uncertainty.
- `POST /api/v1/me/projects/<project-id>/tests/<test-id>`: answer once.

IDs are the **full** GUIDs returned by the API (36-character hyphenated or full
32-character format). Short prefixes/project names are not accepted here. The
project/test pair is always resolved together; an ID in another project cannot
redirect the operation. Omit/leave blank `project` for All projects.

## List response

```json
{
  "apiVersion": 1,
  "project": null,
  "projects": [{"id":"11111111-1111-1111-1111-111111111111","name":"Fixture Alpha"}],
  "tests": [{
    "id":"22222222-2222-2222-2222-222222222222",
    "projectId":"11111111-1111-1111-1111-111111111111",
    "projectName":"Fixture Alpha",
    "itemId":"33333333-3333-3333-3333-333333333333",
    "itemText":"Fixture item",
    "steps":"Open the synthetic screen.",
    "expected":"A fixture appears.",
    "askedBy":"FixtureAgent",
    "askedAt":"2026-11-06T12:00:00.0000000",
    "state":"waiting",
    "canAnswer":true,
    "files":[{"id":"44444444-4444-4444-4444-444444444444","name":"fixture.apk","size":1024}],
    "answeredBy":null,
    "answeredAt":null,
    "note":null
  }]
}
```

`projects` always lists the token's eligible projects, including projects without
pending tests, so filters do not disappear. `project` echoes the selected ID or
null. `tests` includes only waiting records matching that filter. Optional text
fields may be null. A deleted item's test retains its item ID and uses
`(the item was deleted)` as item text. Timestamps are ISO-8601 strings in the PC
record's existing time representation; they are display metadata, not expiry.

File entries are **metadata only**: full attachment ID, base filename and
nonnegative size. No PC paths, URL or download permission is exposed. File
download/open/install and uploading answer files require a separate supported
phone contract; they are not implemented by v1.

## Detail and answer

Detail returns `{"apiVersion":1,"test":<test object>}`. State is one of
`waiting`, `works`, `doesnt_work`, `closed`; `canAnswer` is false for settled
records and read-only tokens.

Answer JSON accepts only:

```json
{"works":false,"note":"The synthetic screen did not open."}
```

`works` must be a JSON boolean. `note` is optional/null text, trimmed; a blank
note becomes null and the maximum is 4,000 characters after trimming, matching
the PC's `TestFlow`. The PC allows a no-note failure like its own UI; the phone
may ask for a useful note as a stricter UX rule. Unknown properties (including
files/paths/by) and non-text notes are rejected, not silently accepted.

Answers invoke the same `ProjectViewModel.AnswerTest` operation as the PC UI:
history/notifications are signed as the PC user, Works does **not** accept a
finished review, and Doesn't work sends a done item needing review back to Todo.
The response is `{"apiVersion":1,"test":<answered test>,"sentBack":false|true}`.
The result is terminal for that test ID; another answer or agent-closed record
is a conflict, never an automatic overwrite.

## Status and retry boundary

- 200: list/detail/confirmed answer response.
- 400: invalid/full-ID format, malformed works/note, overlong note or unsupported
  body properties. Unavailable project/test IDs are not echoed back.
- 401: normal bearer authorization failure/expiry at the API gate.
- 403: wrong token kind, frozen/read-only access or access expiring before queued
  dispatch. Agent-token access to `/me` and phone-token access to agent/MCP
  endpoints remain forbidden.
- 404: project/test is not available within this token's local scope, or route
  isn't implemented by an older PC.
- 409: already answered/closed or changed test; read/reconcile, do not replay.

A lost/cancelled/malformed response after POST may already be saved. The client
must keep its note and require explicit reconciliation/acknowledgement rather
than retry automatically. Neither a native-route change nor re-pairing may
silently re-admit that feedback on a different client/PC. No live user data,
credentials, real calls, audio providers or publication are used by the fixtures.
