---
name: triadbuddy-dev
description: Conventions and known patterns for working on the FFTriadBuddyDalamud plugin (Dalamud/Triple Triad solver) — UI readers, solver integration, config persistence, and localization workflow. Use when adding/modifying UI scrapers, solver features, or translations in this repo.
---

# TriadBuddy Dalamud Plugin

Dalamud plugin (net9.0-windows, Dalamud API level 12) that wraps the standalone
FFTriadBuddy solver (`triad-shared` git submodule) and reads FFXIV's Triple
Triad UI via ImGui/memory scraping to drive solver suggestions.

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
  format), adding `<None Remove="assets\loc\<code>.json" />` in
  `TriadBuddy.csproj`, and appending `<code>` to `supportedLangCodes` in
  `Plugin.cs`.
- `tw` (Traditional Chinese, Taiwan) was added alongside `zh` (Simplified) —
  keep both in sync manually; Crowdin does not auto-derive one from the
  other for this project.
- Missing keys should still be added to `en.json` first — it's the
  fallback/reference set other locales are diffed against.

## Solver integration notes
- `SolverUtils.solverGame` is the single shared solver instance; UI readers
  push scraped state into it, never hold their own solver state.
- `SolverGame.OnLocalPlayerSideDetected` lets a UI reader (e.g. the normal
  game reader) tell the solver which side is the local player once known —
  wire new readers through the same event rather than guessing red/blue.

## Common gotchas / FAQ
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
Standard `dotnet build` against `TriadBuddy.sln`. Locally the Dalamud/ImGui.NET
references resolve from `%appdata%\FFXIVSimpleLauncher\Dalamud\Injector`; CI
(`GITHUB_ACTIONS=true`) instead resolves them from a `lib/` folder — see the
conditional `PropertyGroup`s in `TriadBuddy.csproj` if references break.
