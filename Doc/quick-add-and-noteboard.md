# Quick Add and Note Board

In Button Settings, select a wheel button and choose **Note Board** or **Quick Add web page / note** directly from its action-type menu, then save. Invoke that button with the normal wheel gesture. The same choices are available for a group child or a hold action. Both tools are also available from the tray menu. Quick Info keeps its existing gesture.

The direct choices use the existing built-in Function actions in JSON/XML, so existing configured tool buttons continue to work. A/N do not invoke tools, and the wheel centre has no A/N hint.

## Quick Add

- **Web page:** paste or type an HTTP/HTTPS address. An explicit supported link already in the clipboard is filled in automatically without changing the clipboard. Bare domains may be typed manually. Standard `obsidian://open` links are also supported.
- **Obsidian note:** choose an automatically discovered vault, search by note name or relative path, and select the note. This searches file names only and generates the Obsidian URI directly; opening Obsidian and copying a URI is unnecessary.
- **Note Board:** bind the built-in board to a wheel button.

Choose a main button, an existing group with room, or a specific group child. The destination has no default selection. Replacing an occupied button, a group, or a button with a hold action requires explicit confirmation. The normal configuration still has exactly nine main entries.

Saving updates the active JSON and its existing saved profile. A changed configuration or profile invalidates the session instead of overwriting the newer version. Individual replacements are atomic, and ordinary failures roll back the profile when safe. This does not provide a transaction journal across the two files for a power failure.

## Note Board

The board is a single plain-text scratch pad. Input is saved after 500 ms of inactivity and before closing. **Save to…** chooses a TXT or Markdown working file; subsequent automatic saves and future launches use that location. The previous file remains available. The default file is `%LOCALAPPDATA%\ActionWheel\noteboard.txt`; the chosen location is stored in `noteboard-location.json` beside it.

A failed read disables editing instead of replacing the file with an empty draft. A failed save keeps the window and text. External changes are detected before normal saves. A recovery action saves a separate copy before reloading. Escape closes the board after saving, while an Escape already used by the text editor/IME is left to that editor.

## Input ownership

The interactive wheel acquires the foreground. Ordinary keys, wheel scrolling, and an outside dismissal click do not operate the application underneath. Consumed key/click releases remain consumed after the wheel closes. The chord's first mouse-down may already have reached the original application before the chord is recognised; its matching release is returned to that original child window.

Closing or cancelling restores the original foreground only while the wheel still owns it. A user-initiated intervening focus change is preserved. Actions that send shortcuts verify the original window before dispatch. The released refresh animation remains a non-activating, click-through decoration.

## Opening on the wheel's display

Each wheel invocation captures its physical screen point before dispatch. Its settings editor,
Quick Add and Note Board open centered in that display's work area, including when the tool
already exists. Existing logical sizes and maximized state are retained where they fit. A tray
invocation uses the cursor display captured when that command runs.

External launches follow the same captured display. A plain executable with no arguments can
reuse an unambiguous existing window, restore it if minimized, and bring it forward. URLs,
documents and argument-bearing launches still reach their normal handler so the requested
content opens; a bounded observer identifies that handler's new or activated window, moves it
when necessary and raises it once. A unique unchanged handler is also eligible after a short
settle period. Same-display external windows keep their position. Window titles, web content
and desktop pixels are not read or saved.

The observer checks process identity, creation time and protocol/file association. Known Windows
Calculator, Notepad and Terminal launch stubs use their exact package families. It does not
guess between ambiguous windows, accept generic shell hosts without verified children, or keep
pulling a window back after a user drag. Special shell surfaces such as Task View and the Run
dialog, arbitrary launcher handoffs and nonstandard frameless windows may use their own layout.
Late launches cannot take focus from a newer interactive wheel. Pending observers end after
their bounded wait or launcher shutdown; newer requests for the same handler supersede older ones.

## Verification

`Tools/QuickAddCheck`, `Tools/NoteBoardCheck`, `Tools/MouseDispatchCheck`, `Tools/ChordGateCheck`, and `Tools/RingGestureTests` exercise persistence, replacement, input pairing and geometry. Run `--input-smoke-report <absolute-json-path>` for a brief native-window focus check using disposable windows without global hooks or simulated input.

`--quick-add-preview` opens the real editor against temporary config copies. `--note-board-preview <absolute-note-path>` opens an isolated board without changing the remembered working location.

`--window-placement-smoke <absolute-json-path>` checks real but DWM-cloaked disposable windows
on all attached displays, including normal/maximized/minimized placement and logical sizing.
It also checks synthetic candidate selection and the original screen surviving delayed dispatch
from a reused wheel. It does not open external applications, inject input or change user settings;
external application association and foreground behavior still require desktop acceptance.
