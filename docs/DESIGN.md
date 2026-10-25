# Browse – design sheet

The look of the Windows app, written down so the macOS app can match it. Source of truth in code: `App.xaml`, `Icons.xaml`,
`MainWindow.xaml` (styles `BookmarkChip`, `ProfileChip`, `ToolChip*`, `TabItem`, `OpBtn`), `Views/FeedsView.xaml`, and the colour
strings in `MainWindow.xaml.cs`. If a value here and the code disagree, the code wins; fix this file.

Colours are `#RRGGBB`; `#AARRGGBB`-style values are written as "alpha xx". All sizes are in points/DIPs (1 pt = 1 DIP).

## 1. Principle

Everything is built from **panels**: rounded rectangles, **32 high**, **4 corner radius**, **6 gaps** between neighbours. A panel with an
icon is **two panels side by side with a 3 gap**: a 32×32 icon tile, then a label panel. Nothing has borders or shadows except popups.
One near-black background everywhere; panels carry the colour.

## 2. Colours

| Token            | Value      | Used for |
|------------------|------------|----------|
| `Bg` / `Surface` | `#0A0C10`  | Window, toolbar, bookmark row, popups, address panel fill, page background before a page loads. |
| `Hover`          | `#46484C`  | Hairline borders of popups/menus, hovered menu item. |
| `Fg`             | `#E8EAED`  | Default text. |
| `Muted`          | `#9AA0A6`  | Secondary text. |
| `Light`          | `#E7EDF5`  | Text on slate panels. |
| `Slate`          | `#1A2332`  | Link bookmarks (tile and label), unselected tab label, channel/video label panels, cache chip. |
| `Slate hover`    | `#232F42`  | Hover of slate panels. |
| `Navy`           | `#0B2A5B`  | Bookmark **folders**, tab icon tile, back/forward tile, Feeds folders; text on it is white. |
| `Navy hover`     | `#123A7A`  | Hover of navy panels. |
| `Selected`       | `#2B4F94`  | Selected tab label, selected Feeds item, active filter chip; text white. |
| Focus blue       | `#5C8AF2`  | Address panel border while typing, text selection. |
| Work             | label `#3B6FD9`, tile `#1F4FA8` | Profile toggle in Work. |
| Private          | label `#8E4EC6`, tile `#5E2D8C` | Profile toggle in Private. |
| Feeds on         | label `#B3261E`, tile `#7A1B16` | Feeds button while the Feeds page is open (off = slate label, navy tile). |
| Downloads button | alpha E0 `#E07B16` | Orange tile (File Manager's "Network" orange). |
| Menu button      | alpha 80 `#2E9E4F` | Green tile (File Manager's "Drive" green). |
| Trash/Delete     | `#F28B82`  | Red accent for delete. (The trash icon itself is the red drawing in `Icons.xaml`.) |

Hover/pressed on the coloured tool tiles = a white sheet over the tile at **16 %** (hover) and **30 %** (pressed). Disabled = **35 %** opacity.

Download cards: card `#11161C` with border `#202833`; progress track `#161D26`, fill `#2B4F94`; percent `#4ADE80`, size `#5CC8FF`,
speed `#FFB454`, time left `#F2E08C`; buttons `#18212B` with border `#3A4656`, text `#E7EDF5`.

## 3. Type

* Text: **Segoe UI 14** (Mac: SF Pro / system, 14 pt); secondary lines 12; the big download numbers 20 semibold; chips' text 14.
* Icons: Windows uses the *Segoe Fluent Icons* font for toolbar glyphs (back E72B, forward E72A, download E896, menu E712,
  trash E74D, new tab E710, briefcase E821, lock E72E, video E714). **Not available on the Mac: use SF Symbols or vector assets.**
* Colour illustrations (trash, cut, copy, paste, YouTube mark, folder, globe) are vector drawings in `Icons.xaml` (48×48 canvases);
  export them as SVG/PDF for the Mac.

## 4. Layout (top to bottom)

Rows are separated by **6**; the first row has a 6 top margin; the page starts 6 below the tabs.

1. **Toolbar** (height 32), left to right, 6 apart: Profile toggle (tile 32 + label) · **Feeds** button (tile + label "Feeds") ·
   Back (32×32 navy tile) · Forward · **Address** (tile 32 with the page icon, then a panel with the text, a ☆ button inside at the right;
   border 1 `Hover`, `#5C8AF2` while typing, radius 4) · Cache chip (trash tile + live size, label slate) · [Update ⬆, green text] ·
   Downloads (orange tile) · Menu ⋮ (green tile).
2. **Bookmark row** (min height 32, wraps onto more rows; at most 4 rows then scrolls): chips 32 high with `margin-right 6`, row gap 6.
   * Link: slate icon tile (favicon 16×16, globe stand-in) + slate label, `padding 10,3`, max label width 180, ellipsis.
   * Folder: **no tile**, navy label, white text.
3. **Tab row** (32): tile 32 (page icon on navy, globe stand-in) + label panel 158 wide (title, ✕ 22×22 at the right); `margin-right 6`;
   selected label `#2B4F94`; **＋** new-tab tile 32×32 at the end.
4. **Page area.** Downloads strip at the bottom: one card per download (see colours), slides up on appear.

Fullscreen (F11): everything but the page hides. Feeds page open: bookmark row, tab row and page hide; the toolbar stays.

## 5. Popups and menus

* Menus (⋮, tab menu, folder/channel menu): background `Bg`, 1 border `Hover`, item padding `12,6,10,6`, text 14, hover `Hover`,
  a 16×16 icon column on the left when an item has an icon, submenu arrow on the right.
* **Folder panels** (bookmark folder opened from the bar): own popup, **radius 10**, padding `0,3`, rows 32 high with 3 vertical padding
  (so rows are 6 apart) and 6 horizontal margin; rows use the same chips as the bar (link = slate tile + slate label; folder = navy
  label across the full row with a ▸ arrow). Submenus fly out to the right; hover opens after ~250 ms; closes ~0.6 s after the pointer leaves.
* **Icon bar** (right-click on a bookmark): small popup at the pointer, radius 8, padding 4, 32×32 icon buttons:
  delete (red trash) · cut · paste (only when something is cut) · open in new tab · copy link.
* Drag-drop marker: 3 wide white line between chips, or a 2 white outline around a folder to drop into.

## 6. Feeds page (four columns)

Gap 6 around and between; draggable dividers. Widths (default): folders 210, channels 300, videos 470, player = rest.
Every column starts with a 32 high header panel.

* **Folders:** header navy "Folders" + a 32×32 navy **＋** tile. Items 32 high, navy label, white text, channel count at the right (`#9FB4D9`, 12). Selected `#2B4F94`.
* **Channels:** items **64 high**: square thumbnail panel 64×64 (channel picture, or the initial letter on slate) + slate label (title 14 semibold, "N videos" 12 muted). Channels with nothing in the filter are faded to 50 %.
* **Videos:** header = chips *Today · Week · Last 5 · All · Refresh* (32 high, slate; active = `#2B4F94`) + a muted status text. Items **64 high**: thumbnail panel 114×64 (16:9) + slate label (title up to 2 lines, then "Channel · age" 12 muted).
* **Player:** header panel = "title — channel"; below it the YouTube page. The video's own fullscreen takes the whole window.
