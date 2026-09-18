# Ironfront Reborn UI Asset Pack

A complete eight-screen military game interface prototype with reusable assets.

## Run

Open `index.html` directly in a modern browser. For clipboard support and the most reliable local behavior, serve the folder:

```bash
python3 -m http.server 8080
```

Then visit `http://localhost:8080`.

## Included screens

- Main Menu
- Sign In
- Create Account
- Practice Mode
- Settings
- Room Browser
- Create Room
- Waiting Room

## Demo flow

Select **Multiplayer**, enter any username plus a password with at least six characters, and choose **Log In**. The room browser supports search and filters. Join a room or create one, then switch teams, copy the invite code and toggle readiness in the lobby.

Select **Practice Offline** to configure a solo match with map, mode, AI difficulty, bot count, match duration, weather, time of day, team and vehicle rules, then launch directly without a lobby. **Settings** includes resolution, display mode, FPS limit, quality, VSync, audio mixer, FOV, sensitivity, subtitles, language and accessibility preferences; changes persist locally in the browser.

Account creation follows the game contract: username (3–16 lowercase letters, digits or underscore), password, repeated password and optional display name.

## Integration

- Reuse the clean raster scenes from `assets/backgrounds/`.
- Use the independent SVG controls and icons from their category folders.
- Adjust colors, typography and sizing in `css/tokens.css`.
- Screen markup is in `index.html`; interaction state is in `js/app.js`.
- The prototype contains no framework and requires no compilation.

## Resolution

Designed for 1920×1080. Responsive rules support 1366×768 and narrower displays.

## Production note

The name and identity are included as part of this requested prototype. Confirm ownership and distribution rights before a commercial release.
