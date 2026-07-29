using Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FFTriadBuddy;
using System.Numerics;

namespace TriadBuddyPlugin
{
    public class PluginOverlays
    {
        public const uint colorWin = 0xFF00FF00;
        public const uint colorDraw = 0xFF00D7FF;
        public const uint colorLose = 0xFF0000FF;
        public const uint colorBoard = 0xFFFF7C00;

        public readonly UIReaderTriadGame uiReaderGame;
        public readonly UIReaderTriadPrep uiReaderPrep;
        public UIReaderTriadTournamentDeck? uiReaderTournamentDeck;

        // overlay: game board
        private bool hasGameOverlay;
        private uint gameCardColor;
        private int gameCardIdx;
        private int gameBoardIdx;

        // overlay: deck selection
        private bool hasDeckSelection;

        // overlay: tournament deck suggestion
        private bool hasTournamentDeck;
        private bool tournamentSuggestionReady;

        public PluginOverlays(UIReaderTriadGame uiReaderGame, UIReaderTriadPrep uiReaderPrep)
        {
            this.uiReaderGame = uiReaderGame;
            this.uiReaderPrep = uiReaderPrep;

            if (SolverUtils.solverGame != null)
            {
                SolverUtils.solverGame.OnMoveChanged += OnSolverMove;
                SolverUtils.solverGame.OnTournamentSuggestionReady += () => tournamentSuggestionReady = true;
            }

            uiReaderPrep.OnDeckSelectionChanged += (active) => hasDeckSelection = active;
        }

        public void SetTournamentDeckReader(UIReaderTriadTournamentDeck reader)
        {
            uiReaderTournamentDeck = reader;
            reader.OnAddonVisibilityChanged += (visible) => { hasTournamentDeck = visible; if (!visible) tournamentSuggestionReady = false; };
        }

        public void OnSolverMove(bool foundMove)
        {
            hasGameOverlay = foundMove;
            if (foundMove && SolverUtils.solverGame != null)
            {
                gameBoardIdx = SolverUtils.solverGame.moveBoardIdx;
                gameCardIdx = SolverUtils.solverGame.moveCardIdx;
                gameCardColor = GetChanceColor(SolverUtils.solverGame.moveWinChance);
            }
            else
            {
                gameBoardIdx = -1;
                gameCardIdx = -1;
            }
        }

        public void OnDraw()
        {
            if (hasGameOverlay)
            {
                DrawGameOverlay();
            }

            if (hasDeckSelection)
            {
                DrawDeckSelectionOverlay();
            }

            if (hasTournamentDeck)
            {
                DrawTournamentDeckOverlay();
            }
        }

        private void DrawGameOverlay()
        {
            if (uiReaderGame == null ||
                (uiReaderGame.status != UIReaderTriadGame.Status.NoErrors &&
                 uiReaderGame.status != UIReaderTriadGame.Status.PvPMatch))
            {
                hasGameOverlay = false;
                return;
            }

            if (Service.pluginConfig.ShowSolverHintsInGame)
            {
                bool localIsBlue = uiReaderGame.currentState?.localIsBlue ?? true;
                var (deckCardPos, deckCardSize) = localIsBlue
                    ? uiReaderGame.GetBlueCardPosAndSize(gameCardIdx)
                    : uiReaderGame.GetRedCardPosAndSize(gameCardIdx);
                var (boardCardPos, boardCardSize) = uiReaderGame.GetBoardCardPosAndSize(gameBoardIdx);
                var drawCardPos = deckCardPos + ImGuiHelpers.MainViewport.Pos;
                var drawBoardPos = boardCardPos + ImGuiHelpers.MainViewport.Pos;

                var drawList = ImGui.GetForegroundDrawList(ImGuiHelpers.MainViewport);
                drawList.AddRect(drawCardPos, drawCardPos + deckCardSize, gameCardColor, 5.0f, ImDrawFlags.RoundCornersAll, 5.0f * ImGuiHelpers.GlobalScale);
                drawList.AddRect(drawBoardPos, drawBoardPos + boardCardSize, colorBoard, 5.0f, ImDrawFlags.RoundCornersAll, 5.0f * ImGuiHelpers.GlobalScale);
            }
        }

        private void DrawDeckSelectionOverlay()
        {
            if (uiReaderPrep == null || uiReaderPrep.cachedState == null || SolverUtils.solverPreGameDecks == null)
            {
                hasDeckSelection = false;
                return;
            }

            var drawList = ImGui.GetForegroundDrawList(ImGuiHelpers.MainViewport);
            const int padding = 5;
            var hintTextOffset = new Vector2(padding, padding);

            for (int idx = 0; idx < uiReaderPrep.cachedState.decks.Count; idx++)
            {
                var deckState = uiReaderPrep.cachedState.decks[idx];
                if (SolverUtils.solverPreGameDecks.preGameDecks.TryGetValue(deckState.id, out var deckData))
                {
                    bool isSolverReady = deckData.chance.score > 0;
                    var hintText = !isSolverReady ? "..." : deckData.chance.winChance.ToString("P0");
                    uint hintColor = !isSolverReady ? 0xFFFFFFFF : GetChanceColor(deckData.chance);

                    var hintTextSize = ImGui.CalcTextSize(hintText);
                    var hintRectSize = hintTextSize;
                    hintRectSize.X += padding * 2;
                    hintRectSize.Y += padding * 2;

                    var hintPos = deckState.screenPos + ImGuiHelpers.MainViewport.Pos;
                    hintPos.X += padding;
                    hintPos.Y += (deckState.screenSize.Y - hintTextSize.Y) / 2;

                    drawList.AddRectFilled(hintPos, hintPos + hintRectSize, 0x80000000, 5.0f, ImDrawFlags.RoundCornersAll);
                    drawList.AddText(hintPos + hintTextOffset, hintColor, hintText);
                }
            }
        }

        private void DrawTournamentDeckOverlay()
        {
            if (uiReaderTournamentDeck == null || SolverUtils.solverGame == null) return;

            var opts = SolverUtils.solverGame.bestTournamentOptions;
            var drawList = ImGui.GetForegroundDrawList(ImGuiHelpers.MainViewport);
            var vpPos = ImGuiHelpers.MainViewport.Pos;

            if (tournamentSuggestionReady && opts != null)
            {
                // Highlight the recommended slot for each group.
                for (int g = 0; g < 3 && g < opts.Length; g++)
                {
                    var (pos, size) = uiReaderTournamentDeck.GetSlotPosAndSize(g, opts[g]);
                    if (size == Vector2.Zero) continue;
                    var drawPos = pos + vpPos;
                    drawList.AddRect(drawPos, drawPos + size, colorWin, 5.0f, ImDrawFlags.RoundCornersAll, 4.0f * ImGuiHelpers.GlobalScale);
                }

                // Show win rate label near top-left of first group's recommended slot.
                var (labelPos, _) = uiReaderTournamentDeck.GetSlotPosAndSize(0, opts[0]);
                if (labelPos != Vector2.Zero)
                {
                    float winRate = SolverUtils.solverGame.bestTournamentWinRate;
                    string label = $"{Localization.Localize("TD_Recommended", "Recommended")} {winRate:P0}";
                    var textPos = labelPos + vpPos + new Vector2(0, -22 * ImGuiHelpers.GlobalScale);
                    drawList.AddRectFilled(textPos, textPos + new Vector2(ImGui.CalcTextSize(label).X + 8, 20 * ImGuiHelpers.GlobalScale), 0xC0000000, 3.0f);
                    drawList.AddText(textPos + new Vector2(4, 2), colorWin, label);
                }
            }
            else
            {
                // Still computing, or the background computation failed: either way there's no
                // recommendation to highlight, so just show a status label on the first group slot.
                var (pos, size) = uiReaderTournamentDeck.GetSlotPosAndSize(0, 0);
                if (size != Vector2.Zero)
                {
                    bool failed = tournamentSuggestionReady && SolverUtils.solverGame.tournamentComputeFailed;
                    string label = failed
                        ? Localization.Localize("TD_Failed", "Failed to compute")
                        : Localization.Localize("TD_Computing", "Computing...");
                    uint labelColor = failed ? colorLose : 0xFFFFFFFF;
                    var textPos = pos + vpPos + new Vector2(0, -22 * ImGuiHelpers.GlobalScale);
                    drawList.AddRectFilled(textPos, textPos + new Vector2(ImGui.CalcTextSize(label).X + 8, 20 * ImGuiHelpers.GlobalScale), 0xC0000000, 3.0f);
                    drawList.AddText(textPos + new Vector2(4, 2), labelColor, label);
                }
            }
        }

        public uint GetChanceColor(SolverResult chance)
        {
            return (chance.expectedResult == ETriadGameState.BlueWins) ? colorWin :
                (chance.expectedResult == ETriadGameState.BlueDraw) ? colorDraw :
                colorLose;
        }
    }
}
