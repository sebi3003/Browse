# Browse for macOS

The macOS version of Browse. It is a native Swift app (Xcode, WKWebView) that is meant to look and behave like the Windows app, and to share
bookmarks and Feeds channels with it through the sync file in OneDrive.

The Windows app lives in the repository root (`*.xaml`, `Services/`, ...). Everything the Mac app must follow is written down in `../docs/`:

| File                       | What it is |
|----------------------------|------------|
| `../docs/DESIGN.md`        | Colours, sizes, layout, popups, the Feeds page - the look to match. |
| `../docs/FEATURES.md`      | Every feature with its exact rules and numbers - the behaviour to match. |
| `../docs/SYNC-FORMAT.md`   | The sync file format and rules (the contract between the two apps). |
| `../docs/sync-samples/`    | Sample sync files, merge scenarios with expected results, and the `isNewer` cases, to unit-test the Swift sync against. |

## Plan

1. **Shell**: one window, one tab, address bar, WKWebView, the dark theme and panel look.
2. **Data and sync** as a Swift package with unit tests that load every file in `../docs/sync-samples/`.
3. **Bookmarks bar, panels, tabs**, profiles (Work/Private, tabs per profile).
4. **Browser features**: ad blocking (content rules from the HaGeZi domain list), YouTube ad handling, downloads panel, favicons, cache chip, fullscreen.
5. **Feeds page**.
6. **Packaging**: signing, notarization, updates (Sparkle).

## Working on the Mac

Clone this repository into a normal folder (for example `~/Developer/Browse`, **not** inside OneDrive), open or create the Xcode project in `mac/`, and commit and push from Xcode
(Source Control) or the terminal. The Windows side pulls the same way. Windows and Mac only share `docs/`; neither touches the other's folder.
