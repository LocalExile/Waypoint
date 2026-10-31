# Accounts — planned for 0.6.0

This feature is being implemented locally for owner review. No push or publication is authorized.

## User experience

Accounts belongs beside Backups in the workspace navigation. Use a searchable account list and a character table
with name, level, ascendancy (falling back to base class), league and last played. Keep the selected account and
character visible through refreshes. Explain loading, empty, filtered-empty, unsupported and failed states.
Reuse Waypoint's themes, reading preferences, keyboard navigation and shared modal frame.

Initial account actions: Create, Rename, Set password, Duplicate and Delete. Delete requires entering the account
name and reviewing character/stash impact. Automatic backups follow the existing global preference and can be
changed on this page. Writes require stopped servers/game, no ownership conflicts and the launcher's global writer
lock. A failed backup blocks the change. Unknown storage layouts fail closed. Do not modify real player data in tests.

## Storage contract investigated

Upstream main was verified at `89b7aa908213ac8eb9b5ac5aa7ae54c14189c3e9` during design, matching the local checkout.
Reference sources: `GameServer.Storage/SaveDatabase.cs`, `SqliteAccountStore.cs`, account models/service,
`GameServer.Gateway/CharacterSaves.cs`, `AccountStash.cs`, `AscendancyCatalog.cs` and `AscendancyProgress.cs`.

Accounts and account-character records are JSON in SQLite rows. Character progress is a separate name-keyed table.
Personal and guild stashes have account/league keys and separate stash-tab rows. Main ascendancy is a durable
family/id/revision selection in character progress; display names come from the selected checkout's extracted
`data/tables/Ascendancy.json`. Use its Name or LeagueName, preserving unknown identifiers honestly when data is absent.
Do not infer ascendancy from a base class or copy game data into Waypoint.

Read listings use one SQLite snapshot and expose no password verifiers. Mutation requests use stdin, never passwords
in arguments/logs/files. Retain unknown JSON fields, use parameterized SQL and transactions, check the selected
account's revision again inside the write transaction, and verify database integrity. Do not initialize/migrate a save
database behind the owner's back. Keep account creation through LocalExile's supported registration flow.

Full duplication, if selected, needs new account/character identities, reviewed unique character names, copied progress
and optionally stash data. Clear the cloned progress's old vitals-session ownership. Rename/delete must address the
same account's personal/guild league stashes and preserve unrelated accounts. Backup recovery uses the existing manager.

## Validation plan

Disposable SQLite fixtures cover consistency, transactional rollback, stale selections, name collisions, malformed/
unsupported layouts, credentials, class/ascendancy display, cloning and account/stash relationships. Bridge checks cover
process conflicts, backups and serialization. Cross-check writes against LocalExile's real account/storage classes in
an isolated test harness without launching a client or changing emulator source. Exercise the WPF page/dialogs at
minimum size, larger fonts, both languages and themes. Finish with a separate local review package and restored owner
preferences. No release/push until the owner reviews and authorizes it.
