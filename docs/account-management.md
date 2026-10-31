# Accounts

Account browsing is available while playing; account changes require the game and services to be stopped and use the
automatic-backup preference. **Register** and **Create** on the section go through the server's dev host instead and work
beside running servers ([launcher.md](launcher.md)); this page is about the offline changes.

## Using Accounts

1. Open Waypoint on the intended LocalExile checkout.
2. Choose **Accounts** in the sidebar. The selected runtime profile determines the save database.
3. Search by account name, character name, class, ascendancy or league. Select an account to see its characters,
   levels, class/ascendancy and last-played time. **Reload accounts** refreshes the data.
4. For changes, close the game and stop all LocalExile services through the tool that started them. Waypoint keeps
   account browsing available while playing, but refuses edits while a game, login service, Gateway, watcher,
   account service or conflicting installation is detected. Keep them stopped until the operation completes.
5. Leave **Automatic backups** (the Backups section) enabled for recovery. A required backup failure stops the change.

**Create account** uses LocalExile's existing local registration service, including the first-account setup path.
Account names accept 3–32 ASCII letters, digits, underscores or hyphens. Passwords require at least four characters;
Waypoint does not retain them in preferences, command arguments, request files or operation transcripts.

**Duplicate…** defaults to **Full copy**: a new login, characters, progress, personal/guild stashes and stash tabs
in all leagues. Supply a new password and review the proposed unique character names (3–23 allowed characters).
**Start empty** creates only a login. Full copies receive new account and character identities and clear old
vitals-session ownership. The source account is retained. Opaque progress/mod fields are preserved; arbitrary
mod-specific references inside them are not rewritten, so custom mod behavior may need independent validation.

**Rename…** changes the login name and related personal/guild stash keys across leagues. Character names, progress
and account identity remain attached to the same account. **Set password…** replaces both supported login verifiers;
enter the new credentials again in a client that saved the previous ones.

**Delete account…** shows the account, character/stash counts, database path and backup policy. Enter the exact account
name to continue. It removes the login, active character records/progress and related personal/guild stashes in every
league. Character progress is archived using LocalExile's existing deleted-character table convention. **Enter** defaults
to **Cancel**; closing the window also cancels. Other accounts are preserved. No character/stat editor is included.

Every confirmation identifies the reviewed database. Changing checkout/profile requires another review. A stale
account revision, destination name collision, unfamiliar layout or write lock causes a refusal without partial edits.
Errors keep the account list available for a fresh reload. Manual starts outside Waypoint must wait for the offline
change to finish; the launcher's mutex cannot prevent another tool from starting a service.

## Backups and recovery

Open **Backups** to manage snapshots. They record the source commit and the last Gateway build Waypoint recorded
using that database; outside launches remain unknown. Account changes use these same snapshots, not a second backup
system. To undo an edit, close the game, stop services, select the backup made before the change, and choose
**Restore saves…**. This restores the entire save database, including other accounts' state at that time. Choose the
matching source separately if necessary. Disabling automatic backups removes this automatic recovery copy.

## Supported storage contract

LocalExile main was verified at `89b7aa908213ac8eb9b5ac5aa7ae54c14189c3e9`. Reference sources:
`GameServer.Storage/SaveDatabase.cs`, `SqliteAccountStore.cs`, account models/service,
`GameServer.Gateway/CharacterSaves.cs`, `AccountStash.cs`, `AscendancyCatalog.cs` and `AscendancyProgress.cs`.
No LocalExile source changes, DLL references or game assets are added to the production launcher.

Accounts and account-character records are JSON in SQLite rows. Progress is stored separately by character name.
Personal/guild stashes use account/league keys and separate tab rows. Main ascendancy is a durable family/id/revision
selection in progress; display names come from this checkout's extracted `data/tables/Ascendancy.json`, using Name
or LeagueName. Missing display data shows the durable ascendancy ID; absence of a selection falls back to base class.

Listings use one read-only SQLite snapshot and expose no credential verifiers. Mutations run parameterized SQL in
one immediate transaction, verify identities, recheck the selected account revision and check database integrity.
Known tables, columns, types, keys and JSON metadata are required for direct writes. Unknown layouts, extra
tables/triggers/views or pending legacy imports remain read-only or are rejected when unreadable. This conservative
contract can disable edits after an upstream/mod schema change until support is reviewed. Waypoint never initializes
or migrates a database directly. Python with SQLite 3.37+ is required. Querying occurs on entry/reload or context
change, not on every runtime-status poll. Limits are 2,000 accounts, 48,000 characters and 16 MiB per record.

See [runtime contract](runtime-contract.md) for process gates, stdin transport and database binding, and
[UI review and validation](ui-review/accounts.md) for the checks performed and their limits.
