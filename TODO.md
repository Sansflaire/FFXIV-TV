# FFXIV-TV — Future Work / Known Issues

Items to address after current phase is stable.

---

## Bug: Screen rect draws over game UI

**Priority:** Medium
**Screenshot:** screen overlaps chat log, plugin list, and other native game UI elements
**Expected:** The screen should render behind all game UI (HUD, chat, menus)
**Root cause (hypothesis):** Our D3D11 draw callback fires inside `ImGui_ImplDX11_RenderDrawData`,
which runs after the game's 3D scene but BEFORE or alongside native game UI rendering.
The game's native UI (chat, minimap, hotbars) is rendered in a separate pass after the
ImGui pass — so our quad ends up on top of it.

**Possible fixes to investigate:**
- Hook a render point that fires BEFORE the native UI pass, not during ImGui
- Use ImGui's z-ordering or draw channels to push our quad below native UI
- Investigate FFXIV's render pipeline ordering to find a hook point between 3D scene and native UI
