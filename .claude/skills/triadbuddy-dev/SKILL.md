---
name: triadbuddy-dev
description: Conventions and known patterns for working on the FFTriadBuddyDalamud plugin (Dalamud/Triple Triad solver) — UI readers, solver integration, config persistence, and localization workflow. Use when adding/modifying UI scrapers, solver features, or translations in this repo.
---

# TriadBuddy Dalamud Plugin

Dalamud plugin (net9.0-windows, `Dalamud.NET.Sdk/13.0.0`, **Dalamud API level 13**)
that wraps the standalone FFTriadBuddy solver (`triad-shared` git submodule) and
reads FFXIV's Triple Triad UI via ImGui/memory scraping to drive solver suggestions.

現況：分支 **`tc-7.20`**，`TriadBuddy.json` 的 `DalamudApiLevel` 是 **13**，UI 已在
commit `8da1868`「API13 port: ImGui.NET -> Dalamud.Bindings.ImGui」整個換成
`Dalamud.Bindings.ImGui`。**舊筆記寫「API level 12」「ImGui.NET」的都是 `tc-7.15` 時代的資訊**；
`tc-7.15` 現在是凍結的 API12 archive，不要往上面提交（GitHub 的 `origin/HEAD` 還指著它，
clone 完先 `git checkout tc-7.20`）。

## Project layout
- `plugin/` — plugin code (windows, UI readers, memory readers, config).
- `triad-shared/` — submodule with the solver core (`SolverGame`-adjacent
  types like `TriadGameSimulation`, `TriadNpc`, `TriadCard`).
- `assets/loc/*.json` — per-locale string tables (Crowdin format:
  `{ "KEY": { "message": "..." } }`).
- `data/` — static game data (cards, NPCs).

## UI reader pattern
Each game addon that needs scraping gets its own `UIReaderTriadXxx` class:
- Implements the scheduler-observed addon interface, registered via
  `uiReaderScheduler.AddObservedAddon(...)` in `Plugin.cs`.
- Exposes an `On...Changed` event that Plugin.cs wires up to push data into
  `SolverUtils.solverGame` and, if the data should survive plugin reload,
  into `Service.pluginConfig` (see Tournament deck example below).
- See `UIReaderTriadTournamentDeck.cs` for the reference shape: reads card
  candidate groups from the tournament deck-building screen, exposes
  `groups`/`ruleNames` plus `GetAllCandidateCardIds()`.

## Feature: Tournament deck support
Added because tournaments let players pick from wider card pools than NPC
matches, which the base solver doesn't handle. Key pieces:
- `UIReaderTriadTournamentDeck.cs` — scrapes candidate groups + active rules.
- `SolverGame.SetTournamentGroups(rawGroups, ruleNames)` +
  `ComputeTournamentDeckSuggestion()` — builds valid card combinations and
  picks a suggested deck under simulation (`RunRandomGame`, `PickRandomBit`).
- `Configuration.TournamentGroups` / `TournamentRuleNames` persist the last
  scraped state so a suggestion survives plugin/game reload; restored once
  in `Plugin.cs` `Framework_Update` behind a `tournamentGroupsRestored` guard
  gated on `dataLoader.IsDataReady`.
- `PluginOverlays.SetTournamentDeckReader(...)` wires the overlay highlight
  for tournament deck building the same way it does for normal matches.

When adding another "restore scraped state from config after reload" flow,
follow this same guard-flag-in-Framework_Update pattern rather than
restoring eagerly in the constructor (data loader may not be ready yet).

## Localization
- Add a new locale by dropping `assets/loc/<code>.json` (Crowdin key/message
  format), then editing `TriadBuddy.csproj` **in two ItemGroups**, and appending
  `<code>` to `supportedLangCodes` in `Plugin.cs` (currently
  `{ "de", "en", "es", "fr", "ja", "ko", "zh", "tw" }`, `plugin/Plugin.cs:35`):
  - `<None Remove="assets\loc\<code>.json" />`
  - `<EmbeddedResource Include="assets\loc\<code>.json" />` ← **這條最容易漏**。
    只加 `None Remove` 不加 `EmbeddedResource Include`，檔案不會被打包進 dll，
    執行期 `SetupWithLangCode` 會找不到資源而靜默 fallback，不會有編譯錯誤提示你。
- `tw` (Traditional Chinese, Taiwan) was added alongside `zh` (Simplified) —
  keep both in sync manually; Crowdin does not auto-derive one from the
  other for this project.
- Missing keys should still be added to `en.json` first — it's the
  fallback/reference set other locales are diffed against.
- **語言是用 Dalamud 的 UI 語言字串決定的，不是 `ClientLanguage`**：
  `plugin/Plugin.cs:53` 是 `locManager.SetupWithLangCode(pluginInterface.UiLanguage)`，
  之後靠 `pluginInterface.LanguageChanged` 事件（`OnLanguageChanged(string langCode)`）更新，
  代碼不在 `supportedLangCodes` 就 `SetupWithFallbacks()`。
  所以 2026-07 那次「TC 的 `ClientLanguage` 從 `ChineseSimplified`(4) 變成
  `TraditionalChinese`(7)、害一堆外掛靜默掉回英文/日文」的事件，**本 repo 不受影響、沒有東西要修**
  ——這是艦隊掃描時已知的誤判來源之一，不要「順手修」。
  （真的哪天要用 `ClientLanguage`：CI 釘的 Dalamud 13.0.0.6 **沒有** `TraditionalChinese`
  這個列舉名，必須寫數值 `is 4 or 5 or 7`，寫列舉名本機過、CI 炸。）

## Solver integration notes
- `SolverUtils.solverGame` is the single shared solver instance; UI readers
  push scraped state into it, never hold their own solver state.
- `SolverGame.OnLocalPlayerSideDetected` lets a UI reader (e.g. the normal
  game reader) tell the solver which side is the local player once known —
  wire new readers through the same event rather than guessing red/blue.

## Common gotchas / FAQ
- **`UIStateTriadGame.Equals` must include every field that changes solver
  behavior.** `SetCurrentState` in `UIReaderTriadGame.cs` only fires
  `OnUIStateChanged` when `Equals` says the state changed. Originally
  `Equals` compared `move`/`rules`/`redPlayerDesc`/`board`/decks but NOT
  `isPvP` or `localIsBlue` — so flipping red/blue side detection (e.g. a
  manual "swap sides" toggle) silently did nothing whenever the board itself
  hadn't changed since the last poll, because the "no change" state got
  swallowed before it ever reached the solver. When adding any new field to
  `UIStateTriadGame` that the solver needs to react to, add it to `Equals`
  too, or `OnUIStateChanged` won't fire for it.
- **Red/blue side detection is heuristic, not authoritative.** `lastKnownLocalIsBlue`
  in `UIReaderTriadGame.cs` is inferred from which deck has unlocked cards,
  overridden by `forcedLocalIsRed` (driven by `SolverGame.OnLocalPlayerSideDetected`,
  which itself infers "must be red" only from an NPC-name-parse failure during
  PvP). `forcedLocalIsRed` must only be reset at genuine match-start
  transitions (`isPvP && !wasPvP`) — resetting it every frame wipes out a
  correct detection mid-match. A manual override (`ToggleLocalSide` /
  `manualSideOverrideActive`) exists as a user-facing escape hatch (swap-side
  button in `PluginWindowStatus.cs`, PvP-only) since the heuristic can still
  misjudge in edge cases like chaos rules (no cards ever "unlocked").
- **No offline card/NPC data.** `TriadCardDB` / `TriadNpcDB` (`data/*.cs`) are
  populated at runtime from FFXIV's Lumina Excel sheets via
  `GameDataLoader.cs` — there is no bundled JSON snapshot of cards or NPCs.
  Any batch analysis (e.g. "find best deck vs all NPCs of a ruleset") must
  run inside the live plugin/game session; a standalone headless script
  can't get real data without re-implementing the Lumina load.
- **Deck rarity limits already exist.** The "≤1 Legendary (5★), ≤2
  Epic-or-above (4★+), unlimited Rare-or-below" constraint is already
  enforced by `TriadDeckOptimizer` (`triad-shared/sources/gamelogic/TriadDeckOptimizer.cs`
  `maxSlotsPerRarity`: `Legendary→1`, `Epic→2`). Don't re-implement this —
  reuse `TriadDeckOptimizer.Process(npc, regionMods, lockedCards)`.
- **No "best deck vs all NPCs in a ruleset" tool yet.** Existing optimizers
  only target one NPC (or one region-mod set) at a time:
  `TriadDeckOptimizer.Process` (single NPC) and
  `SolverGame.ComputeTournamentDeckSuggestion()` (tournament candidate pool,
  not grouped-by-ruleset NPC batch). Building a batch "highest win rate
  across all same-ruleset NPCs" feature means grouping `TriadNpc.Rules` and
  aggregating win rate across the group — not present as of 2026-07.

## Build
`TriadBuddy.csproj` 有兩組互斥的 `PropertyGroup`/`ItemGroup`，靠 `GITHUB_ACTIONS` 切換
`DalamudLibPath`（兩邊都是 `<Reference Remove="Dalamud"/>` + 明確 HintPath，
參照的是 `Dalamud.dll` 與 **`Dalamud.Bindings.ImGui.dll`**，不是 `ImGui.NET.dll`）。

- **CI**（`GITHUB_ACTIONS=true`）：路徑是 repo 根目錄的 `lib\`。**repo 裡並沒有 `lib/` 資料夾**
  ——workflow 會先下載釘住的 Dalamud
  （`ffxiv-tc-port/DalamudPluginsTC` release `dalamud-pin-v13.0.0.6/dalamud-api13-net9.zip`）
  解壓到 `$GITHUB_WORKSPACE\lib` 再建置。舊筆記只寫「CI 從 `lib/` 解析」會讓人以為那是簽入的資料夾。
- 🔴 **本機**：csproj 寫死 `$(appdata)\FFXIVSimpleLauncher\Dalamud\Injector`，
  **那裡是啟動器自帶的舊 Dalamud 12.0.2.0**（實測 FileVersion，沒有 `Dalamud.Bindings.ImGui.dll`）。
  直接 `dotnet build` 會炸：

  ```
  error CS0234: 命名空間 'Dalamud' 中沒有類型或命名空間名稱 'Bindings'
  ```

  這個 csproj **不讀 `DALAMUD_HOME`**，設環境變數沒用；要用 MSBuild 全域屬性覆蓋：

  ```powershell
  dotnet build TriadBuddy.csproj -c Release -p:DalamudLibPath="<pin目錄>"
  ```

  （這裡的 HintPath 是 `$(DalamudLibPath)\Dalamud.dll`，自己有分隔符，路徑結尾**不要**再加反斜線。）

  本機實測可用的 API13 Dalamud：`%APPDATA%\xivlauncher\addon\Hooks\dev`（13.0.0.6，與 CI 同版）、
  `D:\ffxiv-tc-port\Dalamud\bin\Release`（13.0.0.16，遊戲執行期實際載入的那份）。
  **不要**去覆寫 `%APPDATA%\FFXIVSimpleLauncher\Dalamud\Injector`。
- ⚠️ CI 釘 13.0.0.6、執行期是 13.0.0.16，**「本機編得過」不等於「CI 編得過」**。

## Versioning / release
- `BuildNumber.txt`（受 git 追蹤）是建置自動遞增的計數檔，`PersistBuildNumber` target 每次 build +1
  → **每次建置都會弄髒工作區**。它是建置副產物，`git checkout -- BuildNumber.txt` 還原即可，
  小心 `git add -A`。工作區髒掉會讓 `release_plugin.py` 判定「有未提交變更」而**跳過這個外掛不發版**。
- csproj 的 `<VersionPrefix>7.15.0</VersionPrefix>` **跟實際發版無關**：`release.yml`（只吃
  `workflow_dispatch`）用 `-p:Version=<tag> -p:AssemblyVersion=… -p:FileVersion=…` 從 git tag 覆蓋，
  再把 `latest.zip` 改名成 `TriadBuddy.zip` 發佈。所以 feed 上是 `v7.20.0.x` 而 csproj 還寫 `7.15.0`
  **不是漏改**，不要去動它。
- `release_plugin.py` 本來就是平行執行，而且**只推 tag**，不推分支、也不 commit `repo.json`。
