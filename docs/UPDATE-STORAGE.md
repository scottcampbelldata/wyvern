# Wyvern release storage

Released installers and signed metadata are stored in /srv/app-updates/wyvern on the Ubuntu host reached with ssh ubuntu. This repository remains the source of this app's build and release scripts.

All six apps share the Ubuntu release store and read-only feed at [the app catalog](http://100.110.71.108:8796/apps.json). Each product keeps its own signed manifest and verification key. Installed clients retain compatible endpoints that read the same release files: http://100.110.71.108:8795/wyvern/latest.json. LocalExpander retains its pinned HTTPS connection. NetBird is required; the feed has no public Internet fallback, directory listings or uploads.

Release command: pwsh -File installer/build-installer.ps1, then pwsh -File installer/publish-update.ps1 -Server ubuntu.

The private release key is on Ubuntu at ~/.local/share/wyvern-release/update-signing-key.pem, outside every served directory. Do not delete, replace, check into Git, or place private keys in OneDrive. New-version manifests are signed on Ubuntu over administrator SSH and installers are checked against their signed SHA-256 and size. Losing a signing key requires a manual client bootstrap; an existing pinned key cannot safely accept a replacement automatically.

Release scripts do not publish copies to OneDrive. A one-time manual installer may be copied to OneDrive/software only when explicitly requested for a signing-key migration. Existing portable installers are archived privately under /srv/app-installer-archive/portable; they are outside the update feed. Prior OneDrive installer copies and recovery files are preserved under /srv/app-installer-archive and the local recovery directory C:\Dev\ReleaseBackups\onedrive-update-migration-2026-10-14.

The former server release directories are persistent bind mounts onto the new store. This keeps old installed clients and publisher path validation working through a reboot. Migration backups are under /var/backups/app-updates/2026-10-14.