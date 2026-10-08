# Approved signed agent updates

Remote updates are opt-in and require a separate, locally pinned ECDSA public key on each managed Windows x64 agent. Default is disabled. Do not reuse the Console command key as an update key. The update private key stays offline; the Console holds only its public key. No unverified binary is executed.

## Release approval

1. Build/test the actual installer for a strictly newer four-component agent version. Versions are compiled into the agent and installer. Reusing a version cannot update agents already on that version.
2. Generate a separate key pair using `scripts/generate-update-signing-key.sh /offline/protected/keys`; distribute only updates-public.pem through your trusted RMM/local administration channel.
3. Construct a manifest with `ReleaseId` (UUID), `Sequence` (strictly increasing integer), `Product` = DariaTechBackupAgent, `Platform` = win-x64, `Version` (e.g. 0.2.0.0), `ArtifactUrl` (HTTPS), `Sha256`, `Length`, `Issued`, `Expires` (UTC, validity at most 90 days). Set hash/length from the final installer bytes, after Authenticode signing if used. A new signature is required after any byte change.
4. On the offline signer run `dotnet run --project DariaTech/Tools -- sign-update PRIVATE_KEY MANIFEST_JSON ARTIFACT_EXE OUTPUT_JSON`. It checks the artifact's size/hash before signing. Publish artifact and signed manifest, never the private key.
5. Set Console Updates:PublicKeyFile to the independently trusted public key. POST the signed JSON to `/api/v1/management/agent-releases` as SuperAdmin with MFA session + CSRF. Expired/invalid signatures or replayed sequence numbers are rejected.
6. Approve selected pilot devices through POST `/update-deployments` with `{deviceId,releaseId}`. Broader rollout is a subsequent explicit per-device approval, not automatic publication to all customers.
7. Locally configure AllowAgentUpdates=true and UpdatePublicKeyFile; require ManageEngine=true. Default download hosts are github.com and release-assets.githubusercontent.com; HTTPS redirects must remain within the local allowlist. Console credentials are never sent to artifact servers.

## Agent verification and execution

The agent validates signatures, product/platform, expiry, size bounds, monotonically accepted sequence and strictly newer installed version. Downloads have a size/time limit; streaming SHA-256 is checked against the signed manifest. All redirect hosts must be explicitly permitted locally. Downloads are staged under the protected state root. The staged artifact is rehashed before execution, and installer file version must equal the manifest version. Active/queued engine tasks defer installation.

A write-ahead update journal records Applying before the verified installer starts. The installer preserves enrollment, engine credentials, databases, locally configured management opt-ins and pinned keys. After service restart the agent compares its actual compiled version and reports Installed only on a match. An uncertain installation is reported Failed/InstallationIndeterminate after a 30-minute grace period, never blindly launched again. Deployment state distinguishes Approved, Downloaded, Applying, Installed, Rejected and Failed.

The detached verified installer must finish stopping/replacing/restarting the service. If it fails after stopping the service, the Console sees the device offline and a technician must recover it using the verified last known good installer and retained protected state. There is deliberately no unverified automatic downgrade. Windows CI passed ordinary upgrades and the end-to-end approved remote update across two distinct compiled versions, including bad-signature and tampered-binary rejection. Fault injection stops the service and removes its installed executable; the verified approved installer repairs it while retaining enrollment and pinned trust. This tests operator recovery from offline/missing-binary state, not interruption at every installer phase. Arbitrary mid-installer interruption, supported customer Windows versions, reboot/VSS and Authenticode remain production rollout gates.

Manifest signatures protect artifact authenticity independently of Authenticode. Unsigned Windows installers still trigger operating-system reputation/signing concerns and remain labelled pilot prereleases. Authenticode requires the actual DariaTech signing certificate; no dummy certificate is shipped as production trust. Keep encrypted master keys, DPAPI state and offline update trust backed up with the recovery procedure.
