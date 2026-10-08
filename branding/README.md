# DariaTech brand sources

The product manifest is the single source for product identity, contact details and palette. Run `python3 scripts/generate-branding.py` after changes; `--check` checks generated browser copies, including binary images. Assets are bundled locally; browsers do not contact the company website to render branding.

Official sources retrieved 2026-10-07:

- `logo.svg`: the original inline `brand-mark` SVG from https://www.dariatech.de/ (only framework-specific attributes removed; geometry/colors preserved).
- `company-logo.png`: unchanged PNG returned by https://www.dariatech.de/logo.
- `favicon.svg`: unchanged https://www.dariatech.de/favicon.svg.
- `installer-icon.png`: unchanged https://www.dariatech.de/apple-touch-icon.png; used in the Windows installer wizard.
- `installer-wizard-{100,150,200}.png` and `installer-small-{100,150,200}.png`: Windows installer side panel and header badge, rendered by `scripts/generate-installer-art.py` from the manifest palette, product name and the official logo mark geometry. Regenerate (requires Pillow) after changing any of these.
- Palette: website stylesheet `/_astro/gaming-pc-service-_city_.DkEU5CcL.css`. Primary `#1a6b54`, dark green `#0c3c30`, bright mint `#28b89a`; the official logo uses turquoise `#01c4a6`. Light/dark surface tokens reuse this palette. Backup status colors remain semantic and independent.
- Company display name, support email `kontakt@dariatech.de` and telephone `+49 8331 99 59 369`: official website header and structured company data. No street address is inferred from the site's locality-only structured address.

Official branding is used at the company owner's request. These corporate marks are not relicensed under Duplicati's MIT license. Upstream and third-party copyright/license notices remain unchanged.

## Updating an existing Console installation

After pulling these changes, rebuild and replace only the Console image (no schema migration is needed for branding):

```bash
cd /opt/dariatech-backup
git pull --ff-only
docker compose build console
docker compose up -d console
```

The deployment keeps the existing database and secret volumes. Console asset URLs include content versions so cached branding refreshes after an update. The agent installer ships updated local-engine branding; existing PCs receive it when the new installer is run as an upgrade.
