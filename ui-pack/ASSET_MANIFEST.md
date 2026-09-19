# Asset Manifest

All assets are reusable components. Background scenes contain no embedded interface labels or controls.

## Backgrounds

| Path | Format | Canvas | Usage |
|---|---|---:|---|
| `assets/backgrounds/main-menu.png` | PNG | 1672×939 | Main menu coastal battlefield; dark left safe area |
| `assets/backgrounds/auth.png` | PNG | 1672×939 | Sign-in and registration mountain base; calm center |
| `assets/backgrounds/multiplayer.png` | PNG | 1672×939 | Rooms, create room and lobby archipelago |

## Branding

| Path | Format | Canvas | Usage |
|---|---|---:|---|
| `assets/branding/ironfront-reborn-logo.png` | PNG | 1040×256 | Full horizontal wordmark |
| `assets/branding/ironfront-symbol.png` | PNG | 256×256 | Compact game mark |

## Icons

All icons are transparent 96×96 white-alpha PNGs intended for runtime tinting.

`user.png`, `users.png`, `lock.png`, `search.png`, `refresh.png`, `plus.png`, `settings.png`, `power.png`, `target.png`, `copy.png`, `leave.png`, `chevron.png`, `wifi.png`, `shield.png`.

## Interface surfaces

| Path | Format | Usage |
|---|---|---|
| `assets/panels/operations-panel.png` | PNG | Angular glass panel (1280×720) |
| `assets/buttons/primary.png` | PNG | Orange high-priority action (640×116) |
| `assets/buttons/secondary.png` | PNG | Blue-steel secondary action (640×116) |
| `assets/inputs/field.png` | PNG | Text/select field frame (840×104) |
| `assets/badges/host.png` | PNG | Host status badge (192×48) |
| `assets/badges/ready.png` | PNG | Ready status badge (192×48) |
| `assets/decorative/corner.png` | PNG | Orange/cyan HUD corner accent (240×240) |

The original SVG files remain beside these PNGs only as editable source masters. HTML and Unity runtime paths use PNG exclusively.

## Interface states

Hover, focus, active, disabled, validation, locked, full, ready and host states are implemented in CSS/HTML so they scale cleanly and can be ported to an engine UI system.

## Screen previews

Eight 1920×1080 preview renders are provided in `previews/`: main menu, sign in, create account, practice, settings, rooms, create room and waiting room.
