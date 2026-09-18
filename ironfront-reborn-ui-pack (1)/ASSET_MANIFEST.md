# Asset Manifest

All assets are reusable components. Background scenes contain no embedded interface labels or controls.

## Backgrounds

| Path | Format | Canvas | Usage |
|---|---|---:|---|
| `assets/backgrounds/main-menu.png` | PNG | 1672×939 | Main menu coastal battlefield; dark left safe area |
| `assets/backgrounds/auth.png` | PNG | 1672×939 | Sign-in and registration mountain base; calm center |
| `assets/backgrounds/multiplayer.png` | PNG | 1672×939 | Rooms, create room and lobby archipelago |

## Branding

| Path | Format | ViewBox | Usage |
|---|---|---:|---|
| `assets/branding/ironfront-reborn-logo.svg` | SVG | 520×128 | Full horizontal wordmark |
| `assets/branding/ironfront-symbol.svg` | SVG | 64×64 | Compact game mark |

## Icons

All icons are transparent SVGs on a 24×24 viewBox and inherit `currentColor` where applicable.

`user.svg`, `users.svg`, `lock.svg`, `search.svg`, `refresh.svg`, `plus.svg`, `settings.svg`, `power.svg`, `target.svg`, `copy.svg`, `leave.svg`, `chevron.svg`, `wifi.svg`, `shield.svg`.

## Interface surfaces

| Path | Format | Usage |
|---|---|---|
| `assets/panels/operations-panel.svg` | SVG | Scalable angular glass panel |
| `assets/buttons/primary.svg` | SVG | Orange high-priority action |
| `assets/buttons/secondary.svg` | SVG | Blue-steel secondary action |
| `assets/inputs/field.svg` | SVG | Text/select field frame |
| `assets/badges/host.svg` | SVG | Host status badge |
| `assets/badges/ready.svg` | SVG | Ready status badge |
| `assets/decorative/corner.svg` | SVG | Orange/cyan HUD corner accent |

## Interface states

Hover, focus, active, disabled, validation, locked, full, ready and host states are implemented in CSS/HTML so they scale cleanly and can be ported to an engine UI system.

## Screen previews

Eight 1920×1080 preview renders are provided in `previews/`: main menu, sign in, create account, practice, settings, rooms, create room and waiting room.
