# Percy Agent

Percy Agent is a local, zero-login email triage application designed to turn many
mailboxes into one finite daily decision queue.

This first build is deliberately safe:

- binds only to `127.0.0.1`;
- uses synthetic messages and placeholder accounts;
- does not connect to Outlook, IMAP, Gmail, n8n, or Baldrick;
- cannot send, move, or delete email;
- stores its local state in `%LOCALAPPDATA%\PercyAgent`.

## Run

Run:

```powershell
.\build.ps1
.\start-percy-agent.cmd
```

Then open <http://127.0.0.1:8765>.

## What is implemented

- finite morning queue rather than an unread count;
- per-account operating policies;
- configurable phrase/sender/subject rules;
- recoverable warm-up quarantine policy;
- draft-required and triage-only account distinction;
- local-only architecture and audit-friendly decisions;
- representative synthetic data for testing the interaction model.

## Account policies

| Policy | Behaviour |
|---|---|
| `triage` | Surface important mail, never generate replies |
| `draft` | Prepare drafts for messages that require replies |
| `outreach` | Hide warm-up traffic and draft genuine prospect replies |
| `monitor` | Report important activity without changing the mailbox |
| `cleanup` | Quarantine predictable automated/warm-up traffic |

Nothing will send automatically. Live connectors and native drafts belong to the
next phase, after the interface and rules feel right.

## Automatic start

`install-autostart.ps1` creates a per-user Startup shortcut. It is provided but
is not run automatically by the installer or application.

The published application uses the installed .NET desktop runtime and has no
third-party package dependencies.
