---
name: aibridge
description: "Unity CLI 工具。执行编译、资源搜索、游戏对象操作、变换操作、组件检查、场景/预制体管理、截图捕获和 GIF 录制。支持多命令执行、运行时扩展和脚本自动化。"
commands: [compile, asset, gameobject, transform, inspector, selection, scene, prefab, screenshot, gameview, get_logs, focus, batch, multi, menu_item, editor]
capabilities: [asset-lookup, scene-editing, build-automation, visual-verification, component-inspection, serialized-property-editing, prefab-asset-editing, component-field-editing, hierarchy-manipulation, prefab-management, console-monitoring, editor-control, script-automation]
triggers: [unity, compile, gameobject, transform, component, serializedproperty, property, scene, prefab, screenshot, gif, console, log, asset, hierarchy, inspector, selection, menu, editor, focus, batch, gameview, resolution, script, automation]
---

# AI Bridge Unity Skill

## AI Operating Rules

- Use `compile unity` for Unity validation. `compile dotnet` is an explicit extra solution-build check, not a fallback.
- For Unity assets, prefer `asset search/find --format paths`; use host file reads for file contents, and `asset read_text` only when host reads are unavailable.
- For serialized edits, discover targets with `inspector get_components/get_properties/find_property`, then write with `inspector set_property/set_properties`; avoid raw YAML unless no Unity API path exists.
- For prefab asset edits, use `assetPath + objectPath + componentName` or `componentIndex`; `componentInstanceId` is scene-only.
- For complex prefab asset edits, use the `aibridge-prefab-patch` skill and prefer `prefab patch --ops <file>` with dry-run first.
- In PowerShell, avoid inline complex `--json`; build JSON in a variable, escape embedded quotes for native EXE argument passing, and pass command parameters directly, especially `inspector set_properties --values $values`.
- `focus` is Windows CLI-only, `screenshot` requires Play Mode, and `multi` is preferred for batch dispatch.
- `multi --cmd` accepts plain CLI commands separated by `&` and automatically emits Batch `call` lines; `call`, `delay`, `log`, `menu`, and `#` comment lines are kept as native Batch script.

---

## Invocation

**CLI Path:** `./AIBridgeCache/CLI/AIBridgeCLI.exe` (run from Unity project root)

**Alias (used in examples below):** `$CLI`

**OS Syntax:**
- Windows: `./AIBridgeCache/CLI/AIBridgeCLI.exe <command> <action> [options]`
- macOS/Linux: `dotnet ./AIBridgeCache/CLI/AIBridgeCLI.dll <command> <action> [options]`
- PowerShell: `& "./AIBridgeCache/CLI/AIBridgeCLI.exe" <command> <action> [options]`

**Global Options:**
- `--timeout <ms>` - Timeout (default: 5000)
- `--raw` / `--pretty` - JSON output (default: raw)
- `--json <json>` / `--stdin` - Complex parameters
- `--help` - Show help

**Cache Directory:** `AIBridgeCache/` (commands, results, screenshots)

---

## Command Reference

### `focus` - Bring Unity to Foreground

CLI-only, Windows-only. Triggers Unity refresh/compile via Windows API.

```bash
$CLI focus
# Output: {"Success":true,"ProcessId":1234,"WindowTitle":"MyProject - Unity 6000.0.51f1","Error":null}
```

### `multi` - Execute Multiple Commands (Recommended)

```bash
$CLI multi --cmd 'editor log --message Step1&gameobject create --name Cube --primitiveType Cube'
$CLI multi --stdin  # Read from stdin (one per line)
```

### `aibridge-prefab-patch` - Complex Prefab Asset Edits

Use the separate `aibridge-prefab-patch` skill for complex prefab changes that need child creation, component creation, internal references, or serialized array edits.

<!-- AIBRIDGE:COMMANDS -->

---

**Skill Version**: 1.0
**Package**: cn.lys.aibridge
