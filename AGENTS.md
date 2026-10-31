# BottomScreen release conventions

- The user's fixed installation directory is `C:\Users\zhbgu\Documents\Codex\2026-10-30\zhe\outputs\BottomScreen`.
- Every release must publish BOTH directories: `outputs\BottomScreen-v<version>` containing that version, AND `outputs\BottomScreen` updated in place for the user's shortcuts. Neither directory is optional. Keep existing versioned releases as historical archives; ZIPs are optional additional artifacts.
- Preserve the existing `settings.json`, user logs, shortcuts, and Windows graphics preferences associated with the fixed executable path. Do not copy fresh defaults over user settings.
- Back up the previous installed application before replacing it. Check for a running instance before replacing its executable; do not force-terminate a user's game or active capture session.
- Verify that BOTH release directories contain the tested executable with the same version and SHA256, and that the fixed installation's user settings were preserved. Keep fresh versioned release defaults separate from the fixed installation's existing settings.
- Final delivery links should point to `outputs\BottomScreen\BottomScreen.exe` so existing shortcuts and per-app settings continue working.
