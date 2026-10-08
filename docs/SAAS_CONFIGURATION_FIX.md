# SaaS-Konfigurationskorrektur – 08.10.2026

Entwicklungsbranch: `fix/native-saas-configuration-20261008`.
Ausgangspunkt: `feature/managed-sources`, Commit
`f2fa3b8308204c88e71f5633bc3cd7e2aaa81af4`.
Der gesicherte Branch, Backup-Branches und `master` bleiben unverändert.

## Nachgewiesene Ursache

Der fehlgeschlagene GitHub-Job `113250961164` aus Workflowlauf
`37759127995` scheiterte beim **zweiten** `ApplyConfiguration` in
`NativeSaasTests.cs`, also beim Aktualisieren eines zuvor angelegten Jobs.
Der Fehler ließ sich lokal auf dem unveränderten Ausgangscommit reproduzieren.

`ManagedConfiguration` erzeugte `abort-if-source-missing=true` für alle Jobs.
`SaasPolicy.Options` lieferte dieselbe Option zusätzlich. Die resultierende
Settings-Liste enthielt den Namen zweimal. Das Engine-POST speicherte diese
Einträge, während das anschließende PUT in
`Backup.UnmaskSensitiveInformation` die gespeicherten Optionen mittels
`ToDictionary(..., StringComparer.OrdinalIgnoreCase)` einlas und scheiterte.

Ein isolierter Test gegen die reale Engine bestätigte:

- POST mit den beiden identischen Einträgen: HTTP 200.
- PUT dieses Jobs: HTTP 500 mit
  `An item with the same key has already been added. Key: abort-if-source-missing`.
- PUT mit bereinigtem Payload gegen den alten doppelten Bestand: weiterhin HTTP 500.
- Frischer Job mit eindeutigen Optionen, POST und PUT: HTTP 200.

Es handelt sich um einen Konfigurationsfehler vor der Provider-Authentifizierung,
nicht um die erwartete Ablehnung ungültiger Microsoft-/Google-Zugangsdaten.

## Änderungen und Kompatibilität

1. Der Agent führt Optionen in einem Dictionary mit case-insensitiven Namen
   zusammen und serialisiert weiterhin das bestehende Engine-Settings-Array.
   `abort-if-source-missing=true` bleibt aktiv. Die Allowlisten für Backend-,
   Quellen- und Skriptoptionen bleiben erhalten; NAS-/Proxmox-Hooks werden
   weiterhin aus der lokal geprüften Konfiguration erstellt.
2. Die Engine akzeptiert im vorherigen Bestand nur Doppeleinträge mit gleichem
   Wert und gleichem Filter (`null` und leer gelten als derselbe leere Filter).
   Damit kann das reguläre PUT die alte Konfiguration unter derselben Job-ID
   mit eindeutigen Optionen ersetzen. Unterschiedliche Werte oder Filter
   werden abgelehnt; die Fehlermeldung enthält keine Optionswerte.
3. Der native SaaS-Test prüft für beide Originalprovider eindeutige Optionen,
   wiederholtes Anwenden, neue Revisionen und die Reparatur eines absichtlich
   angelegten Legacy-Doppeleintrags unter gleicher Job-ID. Die vorhandenen
   negativen Authentifizierungs-/Historientests bleiben Bestandteil der Prüfung.
4. Engine-Regressionstests prüfen identische, auch unterschiedlich geschriebene
   Namen, maskierte Secrets, widersprüchliche Werte/Filter und den unveränderten
   vorherigen In-Memory-Bestand.
5. Die SaaS-CI läuft auch für diesen Fix-Branch und Änderungen an der betroffenen
   REST-Bibliothek beziehungsweise an DariaTech-Code in Pull Requests. Sie
   führt die Maskierungsregression zusätzlich aus. Es werden weiterhin keine
   eingeschränkten Provider-Binaries veröffentlicht.

Die Reparatur erfolgt erst beim regulären Anwenden einer Konfiguration mit der
korrigierten **Agent- und Engine-Version**. Ein Agent-Update allein behebt die
PUT-Verarbeitung eines bereits beschädigten Jobs auf einer alten Engine nicht.
Es wurde keine Produktionsdatenbank, Kundeninstallation oder gespeicherte
Kundenkonfiguration bearbeitet. Bei widersprüchlichen Legacy-Optionen ist eine
separate, lokal geprüfte Bereinigung erforderlich; es wird kein Wert geraten.

## Prüfung der sieben SaaS-Commits

Die sieben Commit-IDs sind historisch nicht alle Vorfahren des Ausgangsbranches.
Das bedeutet hier keine fehlenden Funktionen: Der Commit `50b1f587` integrierte
die SaaS-Arbeit bis `f0b2094f` in `master`, unter Erhalt des dortigen Designs.
`feature/managed-sources` basiert auf diesem Stand. Die betroffenen Dateien und
Deltas wurden inhaltlich verglichen; 14 von 37 betroffenen Dateien sind sogar
identisch mit dem SaaS-Branch. Die restlichen Unterschiede betreffen spätere
Plattform-/NAS-/Proxmox-Erweiterungen, Testanpassungen und vorhandenes Design.

| SaaS-Commit | Inhalt | Ergebnis |
| --- | --- | --- |
| `6dc5db6d` | Verschlüsselte SaaS-Quellen, lokale Autorisierung, separat genehmigter Provider-Restore | Implementiert; `Saas.cs` und `SaasWorkloads.cs` identisch, Verträge später um Proxmox erweitert |
| `b0b19161` | Console-Formulare, Backup-Konfiguration, signierte Restore-Aktionen | Implementiert; aktuelle Formulare behalten zusätzlich NAS/Proxmox und das integrierte Design |
| `28b209de` | Native SaaS-/Negativtests, Providerregeln, Console-Tests | Implementiert; native Tests hier um gezielte Regression erweitert |
| `45ef0ec4` | Nullable optionale Formularfelder, vollständige Engine-Backupdetails | Bereits enthalten; Windows-Smoke-Test identisch, neue Formularfelder bleiben erhalten |
| `c09ecf74` | Unvollständige Cloud-Backups als Fehler, Ergebnis-/Historienabgleich | Enthalten; `HistoryCapture.cs` identisch, Taskabgleich später für verwaltete Quellen erweitert |
| `f0b2094f` | SaaS-Dokumentation und Live-Abnahmegrenzen | `SAAS_INTEGRATION.md` identisch; dieser Bericht ergänzt den neuen Befund |
| `c30019de` | Unix-TLS-Pinning, geschützte Pfade und Zustand | Patchgleich über `eb7596f9` enthalten; inzwischen native Dienste/Updates erweitert |

Es wurde kein vollständiger Branch-Merge und kein redundanter Cherry-Pick
vorgenommen. Dadurch werden die späteren Funktionen und Sicherheitsprüfungen
nicht durch ältere Implementierungen ersetzt.

## Validierung

Die detaillierten lokalen Logs, Reproduktionsdaten und TRX-Ergebnisse liegen
separat unter `/workspace/saas-fix-evidence-20261008/`; sie enthalten ausschließlich
Testdaten. Die ursprüngliche Sicherung unter `backup-audit-20261008` wurde nicht
als Arbeitsverzeichnis verwendet.

Die abschließenden Ergebnisse stehen im Entwicklungsbericht. Die lokale
Reproduktion vor der Korrektur scheiterte erwartungsgemäß; nach der Korrektur
bestand die native SaaS-Integration einschließlich beider Provider und ungültiger
Zugangsdaten. Die maskierungsbezogenen Engine-Regressionen und der Offline-
Providervertrag bestehen ebenfalls.

## Verbleibende Aufgaben

- Erfolgreiche Live-SaaS-Sicherung und Objekt-Restore in eigenen Microsoft-/Google-
  Testmandanten mit geprüften Modulrechten, Consent und tatsächlichen Credentials.
- Providerbezogene Prüfung von Pagination, Throttling, Tokenablauf, Sitzabdeckung,
  Retention und wiederhergestellten Daten; negative Authentifizierungstests
  belegen keine erfolgreiche Live-Sicherung.
- Getrennte native Windows/macOS-/ARM64-Abnahme und reale Proxmox-/NAS-Abnahme
  entsprechend dem vorhandenen Plattformplan.
- Gekoppelte Agent-/Engine-Upgradeplanung für alte Doppeleinträge; Konflikte
  benötigen eine lokale, explizite Bewertung.
- Review vor einer späteren Übernahme in `master`; kein produktiver Rollout
  Bestandteil dieser Arbeit.
