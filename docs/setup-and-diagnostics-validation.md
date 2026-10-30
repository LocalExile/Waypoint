# Setup and diagnostics validation, 30 October 2026

This follows the [fresh standard installation validation](standard-install-validation.md), using the same independent
physical client, default build/client paths and frozen upstream main commit `c748f5fa8b315391a777c08ca184dc43768b60fa`.
No LocalExile source changes, external-path PR, local wrapper, junction or shared writable database was used.

## Results

| Check | Result |
| --- | --- |
| Launcher solution build | Passed, zero warnings/errors |
| Launcher executable checks | 31 passed |
| PowerShell ownership checks | 9 passed |
| Install extraction dependency | Passed with the checkout's locked package |
| Prepare an already prepared client | Passed; SHA-256 of existing login keys, original exe and prepared exe unchanged |
| Unpaired prepared client fixture | Refused before copying or creating files |
| Create a new local account | Passed in the independent test database |
| Password handling | Generated test password absent from operation files; no registration transcript |
| Temporary account service shutdown | Port 7000 free after success and after duplicate-account refusal |
| Duplicate account request | Refused without changing the existing password; failure identifies account creation |
| Cold Play from the desktop | Passed; extraction check, service build, listener readiness and copied game launch |
| Visible progress | Named service-build stage and elapsed time observed; no invented completion percentage |
| Close game and Stop servers | Passed through desktop controls; all standard ports free afterward |
| Minimum-size UI | Play, Setup, Logs, account/instruction dialogs and diagnostic preview inspected at 1020 × 730 |
| Incomplete setup UI | Play disabled; missing prerequisite instructions/actions visible; failure and Retry exercised |
| Diagnostics and filtering | Automated checks cover bounded reads, search/severity combination, private file exclusion, credential/name/path redaction including escaped JSON, and public build identity retention |
| Preservation audit | Original protected file hashes unchanged; no reparse points; independent archive/keys; SQLite integrity OK |

The setup test added a second disposable account to the validation installation. It now has two test accounts and
one test character; the original account/character from the earlier gameplay test remain intact. The temporary audit
was updated to expect that explicit addition. Launcher settings used for the blocked UI fixture were backed up and restored.

The observed desktop binary during the lifecycle/minimum-size checks reported informational version
`1.0.0+c2677aa07b261f5699b85abf4154d6c1d19c1233`; it was built from the working changes on that base, not an immutable
release. Final source changes were rebuilt before completion. The displayed feature version is now 0.2.0; the product
display name remains provisional and independent of the internal application identity.

## Limits

The existing independent client was used for the preparation preservation check. A second complete fresh client
copy was not created for this feature pass. The earlier standard-installation check already covered fresh manual
preparation and entry into the world; this pass checks the new launcher actions and cold lifecycle. The full upstream
test suite was not rerun for launcher-only changes.

Client copying remains a user action in File Explorer. Account creation uses Host, not the native login helper;
sign-in and character creation remain inside the game. Diagnostic redaction is pattern-based and requires preview
review. Backup/restore, operation-log retention policy and external build/client paths remain separate work.

The validation game/services were left stopped. No changes were pushed or published.
