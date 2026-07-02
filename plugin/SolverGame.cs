using FFTriadBuddy;
using MgAl2O4.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TriadBuddyPlugin
{
    public class SolverGame
    {
        public enum Status
        {
            NoErrors,
            FailedToParseCards,
            FailedToParseRules,
            FailedToParseNpc,
        }

        private TriadGameScreenMemory screenMemory = new();
        private TriadNpc? pvpNpc;
        // [groupIdx][optionIdx] = list of cards in that option
        private List<List<TriadCard>>[]? tournamentGroups;
        private List<string> tournamentRuleNames = new();
        private List<TriadGameModifier> tournamentMods = new();

        // Best deck option per group (0-based), set after ComputeTournamentDeckSuggestion.
        public int[]? bestTournamentOptions;
        public float bestTournamentWinRate;
        public event Action? OnTournamentSuggestionReady;

        private TriadNpc GetPvPNpc()
        {
            if (pvpNpc == null) RebuildPvPNpc();
            return pvpNpc!;
        }

        public void SetTournamentGroups(List<List<int>>[] rawGroups, List<string> ruleNames)
        {
            var groups = new List<List<TriadCard>>[rawGroups.Length];
            for (int g = 0; g < rawGroups.Length; g++)
            {
                groups[g] = new List<List<TriadCard>>();
                foreach (var option in rawGroups[g])
                {
                    var cards = option
                        .Select(id => TriadCardDB.Get().FindById(id))
                        .Where(c => c != null).Select(c => c!)
                        .ToList();
                    if (cards.Count > 0) groups[g].Add(cards);
                }
            }
            tournamentGroups = groups;
            tournamentRuleNames = ruleNames;
            pvpNpc = null;
        }

        public void RebuildPvPNpc()
        {
            var deck = new TriadDeck();
            if (Service.pluginConfig != null)
            {
                foreach (var id in Service.pluginConfig.PvpOpponentCardIds)
                {
                    var card = TriadCardDB.Get().FindById(id);
                    if (card != null) deck.knownCards.Add(card);
                }
            }

            if (deck.knownCards.Count == 0)
            {
                // Fallback: use top 5 from tournament candidates or global DB.
                IEnumerable<TriadCard> pool = tournamentGroups != null
                    ? tournamentGroups.SelectMany(g => g.SelectMany(opt => opt))
                    : TriadCardDB.Get().cards.Where(c => c != null).Select(c => c!);
                foreach (var c in pool.OrderByDescending(c => c.Sides.Sum()).Take(5))
                    deck.knownCards.Add(c);
            }

            var mods = new List<TriadGameModifier>();
            var parseCtx = new GameUIParser();
            foreach (var name in tournamentRuleNames)
            {
                var mod = parseCtx.ParseModifier(name, markFailed: false);
                if (mod != null) mods.Add(mod);
            }
            if (mods.Count > 0)
                Service.logger.Info($"[SolverGame] PvP NPC rules: [{string.Join(", ", mods.Select(m => m.GetLocalizedName()))}]");

            tournamentMods = mods;
            pvpNpc = new TriadNpc(0, mods, new List<TriadCard>(), deck);
        }

        // Returns list of valid combinations: each combination is a list of cards (one full 5-card deck).
        // Filters out combinations inconsistent with red cards already placed on board.
        private List<List<TriadCard>> GetValidCombinations(List<TriadCard> placedRedCards)
        {
            if (tournamentGroups == null || tournamentGroups.Length == 0)
                return new List<List<TriadCard>>();

            // For each group, determine which options are still possible.
            var validOptionsPerGroup = new List<List<List<TriadCard>>>();
            foreach (var group in tournamentGroups)
            {
                var validOptions = new List<List<TriadCard>>();
                bool anyConfirmed = false;
                List<TriadCard>? confirmedOption = null;

                foreach (var option in group)
                {
                    // Option is confirmed if any of its cards is on the board.
                    bool confirmed = option.Any(c => placedRedCards.Contains(c));
                    // Option is eliminated if a card from a DIFFERENT option in this group is on the board.
                    bool eliminated = !confirmed && placedRedCards.Any(rc => group.Any(o => o != option && o.Contains(rc)));

                    if (confirmed) { anyConfirmed = true; confirmedOption = option; }
                    if (!eliminated) validOptions.Add(option);
                }

                // If one option is confirmed, only that option is valid.
                validOptionsPerGroup.Add(anyConfirmed && confirmedOption != null
                    ? new List<List<TriadCard>> { confirmedOption }
                    : validOptions);
            }

            // Enumerate all combinations across groups.
            var combinations = new List<List<TriadCard>> { new List<TriadCard>() };
            foreach (var groupOptions in validOptionsPerGroup)
            {
                var expanded = new List<List<TriadCard>>();
                foreach (var combo in combinations)
                    foreach (var option in groupOptions)
                    {
                        var newCombo = new List<TriadCard>(combo);
                        newCombo.AddRange(option);
                        expanded.Add(newCombo);
                    }
                combinations = expanded;
            }
            return combinations;
        }
        // Enumerate all myDeck options (one per group), evaluate vs all opponent combos, return best.
        public void ComputeTournamentDeckSuggestion()
        {
            if (tournamentGroups == null || tournamentGroups.Length != 3)
            {
                bestTournamentOptions = null;
                OnTournamentSuggestionReady?.Invoke();
                return;
            }

            // Capture group data for background thread.
            var groups = tournamentGroups;
            var ruleNames = tournamentRuleNames.ToList();

            Task.Run(() =>
            {
                // Parse rules fresh here (tournamentMods may not be populated yet).
                var parseCtx = new GameUIParser();
                var mods = ruleNames
                    .Select(n => parseCtx.ParseModifier(n, markFailed: false))
                    .Where(m => m != null).Select(m => m!).ToList();

                // Use a lightweight simulation without the complex agent.
                var simulation = new TriadGameSimulation();
                simulation.Initialize(mods);

                // Build all 27 my-deck combos as (optionPerGroup[], cards[])
                var myOptions = new List<(int[] opts, List<TriadCard> cards)>();
                for (int a = 0; a < groups[0].Count; a++)
                for (int b = 0; b < groups[1].Count; b++)
                for (int c = 0; c < groups[2].Count; c++)
                {
                    var cards = new List<TriadCard>();
                    cards.AddRange(groups[0][a]);
                    cards.AddRange(groups[1][b]);
                    cards.AddRange(groups[2][c]);
                    myOptions.Add((new[] { a, b, c }, cards));
                }

                // Build all 27 opponent combos
                var oppCombos = GetValidCombinations(new List<TriadCard>());

                const int gamesPerPair = 50;
                var rng = new Random(42);

                float bestScore = -1f;
                int[] bestOpts = new[] { 0, 0, 0 };
                float bestWin = 0f;

                foreach (var (opts, myCards) in myOptions)
                {
                    float totalScore = 0f;
                    var myDeck = new TriadDeck();
                    myDeck.knownCards.AddRange(myCards);

                    foreach (var oppCards in oppCombos)
                    {
                        var oppDeck = new TriadDeck();
                        oppDeck.knownCards.AddRange(oppCards);

                        int wins = 0, draws = 0;
                        for (int g = 0; g < gamesPerPair; g++)
                        {
                            var state = simulation.StartGame(myDeck, oppDeck, ETriadGameState.InProgressBlue);
                            RunRandomGame(simulation, state, rng);
                            if (state.state == ETriadGameState.BlueWins) wins++;
                            else if (state.state == ETriadGameState.BlueDraw) draws++;
                        }
                        totalScore += (wins + 0.5f * draws) / gamesPerPair;
                    }

                    float avg = oppCombos.Count > 0 ? totalScore / oppCombos.Count : 0f;
                    if (avg > bestScore)
                    {
                        bestScore = avg;
                        bestOpts = opts;
                        bestWin = avg;
                    }
                }

                bestTournamentOptions = bestOpts;
                bestTournamentWinRate = bestWin;
                Service.logger.Info($"[TournamentDeck] Best deck: group0→opt{bestOpts[0]}, group1→opt{bestOpts[1]}, group2→opt{bestOpts[2]}, win={bestWin:P0}");
                OnTournamentSuggestionReady?.Invoke();
            });
        }

        private static void RunRandomGame(TriadGameSimulation simulation, TriadGameSimulationState state, Random rng)
        {
            while (state.state == ETriadGameState.InProgressBlue || state.state == ETriadGameState.InProgressRed)
            {
                var deck = state.state == ETriadGameState.InProgressBlue ? state.deckBlue : state.deckRed;
                var owner = state.state == ETriadGameState.InProgressBlue ? ETriadCardOwner.Blue : ETriadCardOwner.Red;

                // Pick random available card
                int cardMask = deck.availableCardMask;
                if (cardMask == 0) break;
                int cardIdx = PickRandomBit(cardMask, rng);

                // Pick random empty board slot
                int boardMask = 0;
                for (int i = 0; i < state.board.Length; i++)
                    if (state.board[i] == null) boardMask |= (1 << i);
                if (boardMask == 0) break;
                int boardPos = PickRandomBit(boardMask, rng);

                simulation.PlaceCard(state, cardIdx, deck, owner, boardPos);
            }
        }

        private static int PickRandomBit(int mask, Random rng)
        {
            int count = 0;
            for (int tmp = mask; tmp != 0; tmp &= tmp - 1) count++;
            int pick = rng.Next(count);
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    if (pick-- == 0) return i;
                }
            }
            return 0;
        }

        public TriadCard? redAdvMoveCard => screenMemory.deckRed?.GetCard(redAdvMoveCardIdx);
        public int redAdvMoveCardIdx;
        public int redAdvMoveBoardIdx;
        public SolverResult redAdvMoveWinChance;
        public bool hasRedAdvMove;

        public TriadGameScreenMemory? DebugScreenMemory => screenMemory;

        private ScannerTriad.GameState? cachedScreenState;
        public ScannerTriad.GameState? DebugScreenState => cachedScreenState;

        public TriadNpc? lastGameNpc;
        public TriadNpc? currentNpc;
        public TriadCard? moveCard => screenMemory.deckBlue?.GetCard(moveCardIdx);
        public int moveCardIdx;
        public int moveBoardIdx;
        public SolverResult moveWinChance;
        public bool hasMove;
        public bool pvpNeedsDeckConfig;

        public Status status;
        public bool HasErrors => status != Status.NoErrors;

        public event Action<bool>? OnMoveChanged;
        // Fired when we detect the local player is the Red player in PvP (true=red, false=unknown/blue)
        public event Action<bool>? OnLocalPlayerSideDetected;

        public async void UpdateGame(UIStateTriadGame stateOb)
        {
            status = Status.NoErrors;

            ScannerTriad.GameState? screenOb = null;
            if (stateOb != null)
            {
                var parseCtx = new GameUIParser();
                screenOb = stateOb.ToTriadScreenState(parseCtx);
                currentNpc = stateOb.ToTriadNpc(parseCtx);

                if (parseCtx.HasErrors)
                {
                    currentNpc = null;
                    status =
                        parseCtx.hasFailedCard ? Status.FailedToParseCards :
                        parseCtx.hasFailedModifier ? Status.FailedToParseRules :
                        parseCtx.hasFailedNpc ? Status.FailedToParseNpc :
                        Status.NoErrors;
                }
            }
            else
            {
                // not really an error state, ui reader will push null state when game is finished
                currentNpc = null;
            }

            if (currentNpc != null)
            {
                lastGameNpc = currentNpc;
            }

            cachedScreenState = screenOb;
            bool isPvP = stateOb != null && (stateOb.isPvP || status == Status.FailedToParseNpc);
            bool userConfiguredDeck = isPvP && (Service.pluginConfig?.PvpOpponentCardIds.Any(id => id >= 0) ?? false);
            bool hasTournamentDeck = tournamentGroups != null && tournamentGroups.Length > 0;
            pvpNeedsDeckConfig = isPvP && !userConfiguredDeck && !hasTournamentDeck;
            var solverNpc = isPvP ? GetPvPNpc() : currentNpc;
            // When solver detected NPC parse failed = PvP but GetUIStatePvP missed it, the local player must be Red
            // (blue player's name would match an NPC, only the red = local player's name causes parse failure)
            if (status == Status.FailedToParseNpc && !stateOb!.isPvP)
                OnLocalPlayerSideDetected?.Invoke(true);
            else if (!isPvP)
                OnLocalPlayerSideDetected?.Invoke(false);
            if (solverNpc != null &&
                screenOb != null && screenOb.turnState == ScannerTriad.ETurnState.Active &&
                stateOb != null)
            {
                var updateFlags = screenMemory.OnNewScan(screenOb, solverNpc);
                if (updateFlags != TriadGameScreenMemory.EUpdateFlags.None)
                {
                    if (screenMemory.deckBlue != null && screenMemory.gameState != null && screenMemory.gameSolver != null)
                    {
#if DEBUG
                        // turn on verbose debugging when checking solver's behavior
                        screenMemory.gameSolver.agent.debugFlags = TriadGameAgent.DebugFlags.ShowMoveStart | TriadGameAgent.DebugFlags.ShowMoveDetails;
#endif // DEBUG

                        SolverUtils.solverDeckOptimize?.SetPauseForGameSolver(true);

                        bool useTournamentMultiScenario = isPvP && tournamentGroups != null && tournamentGroups.Length > 0;
                        var nextMoveInfo = useTournamentMultiScenario
                            ? await UpdateGameRunSolverMultiScenario()
                            : await UpdateGameRunSolver();

                        hasMove = true;
                        moveCardIdx = nextMoveInfo.Item1;
                        moveBoardIdx = (moveCardIdx < 0) ? -1 : nextMoveInfo.Item2;
                        moveWinChance = nextMoveInfo.Item3;

                        var solverCardOb = screenMemory.deckBlue.GetCard(moveCardIdx);
                        if ((screenMemory.gameState.forcedCardIdx >= 0) && (moveCardIdx != screenMemory.gameState.forcedCardIdx))
                        {
                            // swap + chaos may cause selecting wrong instance of duplicated card?
                            // it really, really shouldn't unless solver's agent is broken

                            var forcedCardOb = screenMemory.deckBlue.GetCard(screenMemory.gameState.forcedCardIdx);

                            var solverCardDesc = solverCardOb != null ? solverCardOb.Name.GetCodeName() : "??";
                            var forcedCardDesc = forcedCardOb != null ? forcedCardOb.Name.GetCodeName() : "??";
                            Service.logger.Warning($"Solver selected card [{moveCardIdx}]:{solverCardDesc}, but game wants: [{screenMemory.gameState.forcedCardIdx}]:{forcedCardDesc} !");

                            moveCardIdx = screenMemory.gameState.forcedCardIdx;
                            solverCardOb = forcedCardOb;
                        }

                        Logger.WriteLine("  suggested move: [{0}] {1} {2} (expected: {3})",
                            moveBoardIdx, ETriadCardOwner.Blue,
                            solverCardOb != null ? solverCardOb.Name.GetCodeName() : "??",
                            moveWinChance.expectedResult);

                        SolverUtils.solverDeckOptimize?.SetPauseForGameSolver(false);
                    }
                    else
                    {
                        hasMove = false;
                    }

                    OnMoveChanged?.Invoke(hasMove);

                    // when in PvP with known opponent deck, also find opponent's worst move (best for blue)
                    if (isPvP && screenMemory.deckRed != null && screenMemory.gameState != null && screenMemory.gameSolver != null)
                    {
                        bool hasKnownRedCards = GetPvPNpc().Deck.knownCards.Count > 0;
                        if (hasKnownRedCards)
                        {
                            var redAdvInfo = await FindWorstRedMoveForBlue();
                            hasRedAdvMove = redAdvInfo.Item1 >= 0;
                            redAdvMoveCardIdx = redAdvInfo.Item1;
                            redAdvMoveBoardIdx = redAdvInfo.Item2;
                            redAdvMoveWinChance = redAdvInfo.Item3;
                        }
                        else
                        {
                            hasRedAdvMove = false;
                        }
                    }
                    else
                    {
                        hasRedAdvMove = false;
                    }
                }
            }
            else if (hasMove)
            {
                hasMove = false;
                hasRedAdvMove = false;
                OnMoveChanged?.Invoke(hasMove);
            }
        }

        private Task<Tuple<int, int, SolverResult>> UpdateGameRunSolver()
        {
            screenMemory.gameSolver.FindNextMove(screenMemory.gameState, out int bestCardIdx, out int bestBoardPos, out var solverResult);
            return Task.FromResult(new Tuple<int, int, SolverResult>(bestCardIdx, bestBoardPos, solverResult));
        }

        private Task<Tuple<int, int, SolverResult>> UpdateGameRunSolverMultiScenario()
        {
            // Collect red cards already on board to narrow valid combinations.
            var placedRed = new List<TriadCard>();
            if (screenMemory.gameState?.board != null)
            {
                foreach (var piece in screenMemory.gameState.board)
                {
                    if (piece?.owner == ETriadCardOwner.Red && piece.card != null)
                        placedRed.Add(piece.card);
                }
            }

            var combinations = GetValidCombinations(placedRed);
            if (combinations.Count == 0)
                return UpdateGameRunSolver();

            // score[cardIdx * 9 + boardPos] = sum of scores across scenarios
            var totalScore = new float[TriadDeckInstance.maxAvailableCards * 9];
            int scenarioCount = 0;

            foreach (var combo in combinations)
            {
                var deck = new TriadDeck();
                deck.knownCards.AddRange(combo);

                var deckInst = new TriadDeckInstanceManual(deck);
                // Remove cards already placed on board from available mask.
                for (int i = 0; i < deck.knownCards.Count; i++)
                {
                    if (placedRed.Contains(deck.knownCards[i]))
                        deckInst.availableCardMask &= ~(1 << i);
                }

                var stateCopy = new TriadGameSimulationState(screenMemory.gameState);
                stateCopy.deckRed = deckInst;
                stateCopy.forcedCardIdx = screenMemory.gameState.forcedCardIdx;

                screenMemory.gameSolver.FindNextMove(stateCopy, out int cardIdx, out int boardPos, out var result);
                if (cardIdx >= 0 && boardPos >= 0)
                    totalScore[cardIdx * 9 + boardPos] += result.score;
                scenarioCount++;
            }

            // Pick move with highest aggregate score (require at least one scenario recommended it).
            float bestScore = 0f;
            int bestCard = -1, bestBoard = -1;
            for (int ci = 0; ci < TriadDeckInstance.maxAvailableCards; ci++)
                for (int bi = 0; bi < 9; bi++)
                {
                    float s = totalScore[ci * 9 + bi];
                    if (s > bestScore) { bestScore = s; bestCard = ci; bestBoard = bi; }
                }

            // Fall back to single-scenario if multi-scenario gave no result.
            if (bestCard < 0)
                return UpdateGameRunSolver();

            // Run single-scenario solver to get a proper SolverResult for display.
            screenMemory.gameSolver.FindNextMove(screenMemory.gameState, out _, out _, out var displayResult);
            Service.logger.Info($"[MultiScenario] {scenarioCount} combos, best: card[{bestCard}] board[{bestBoard}] score={bestScore:F2}");

            return Task.FromResult(new Tuple<int, int, SolverResult>(bestCard, bestBoard, displayResult));
        }

        private Task<Tuple<int, int, SolverResult>> FindWorstRedMoveForBlue()
        {
            var solver = screenMemory.gameSolver;
            var gameState = screenMemory.gameState;

            // temporarily switch to red's turn to enumerate red's available actions
            var origState = gameState.state;
            gameState.state = ETriadGameState.InProgressRed;
            solver.FindAvailableActions(gameState, out int availBoardMask, out int availCardsMask);
            gameState.state = origState;

            int bestCardIdx = -1;
            int bestBoardPos = -1;
            SolverResult bestResult = SolverResult.Zero;

            for (int cardIdx = 0; cardIdx < TriadDeckInstance.maxAvailableCards; cardIdx++)
            {
                if ((availCardsMask & (1 << cardIdx)) == 0) continue;

                for (int boardPos = 0; boardPos < 9; boardPos++)
                {
                    if ((availBoardMask & (1 << boardPos)) == 0) continue;

                    // clone state and simulate red placing this card
                    var testState = new TriadGameSimulationState(gameState);
                    testState.state = ETriadGameState.InProgressRed;
                    bool placed = solver.simulation.PlaceCard(testState, cardIdx, testState.deckRed, ETriadCardOwner.Red, boardPos);
                    if (!placed) continue;

                    // now solve for blue from this resulting state
                    SolverResult blueResult;
                    if (testState.state == ETriadGameState.InProgressBlue)
                    {
                        solver.FindNextMove(testState, out _, out _, out blueResult);
                    }
                    else
                    {
                        // game ended after red move – use zero as result if red won, keep as is for other states
                        blueResult = SolverResult.Zero;
                    }

                    if (blueResult.IsBetterThan(bestResult))
                    {
                        bestResult = blueResult;
                        bestCardIdx = cardIdx;
                        bestBoardPos = boardPos;
                    }
                }
            }

            return Task.FromResult(new Tuple<int, int, SolverResult>(bestCardIdx, bestBoardPos, bestResult));
        }

        public void UpdateKnownPlayerDeck(TriadDeck playerDeck)
        {
            screenMemory.UpdatePlayerDeck(playerDeck);
        }

        public (List<TriadCard>, List<TriadCard>) GetScreenRedDeckDebug()
        {
            var knownCards = new List<TriadCard>();
            var unknownCards = new List<TriadCard>();

            if (screenMemory != null && screenMemory.deckRed != null && screenMemory.deckRed.deck != null)
            {
                var deckInst = screenMemory.deckRed;
                if (deckInst.availableCardMask > 0)
                {
                    for (int Idx = 0; Idx < deckInst.cards.Length; Idx++)
                    {
                        bool bIsAvailable = (deckInst.availableCardMask & (1 << Idx)) != 0;
                        if (bIsAvailable)
                        {
                            TriadCard cardOb = deckInst.GetCard(Idx);
                            bool bIsKnownPool = deckInst.deck.knownCards.Contains(cardOb);

                            var listToUse = bIsKnownPool ? knownCards : unknownCards;
                            listToUse.Add(cardOb);
                        }
                    }
                }

                int visibleCardsMask = (deckInst.cards != null) ? ((1 << deckInst.cards.Length) - 1) : 0;
                bool hasHiddenCards = (deckInst.availableCardMask & ~visibleCardsMask) != 0;
                if (hasHiddenCards && deckInst.cards != null)
                {
                    for (int Idx = deckInst.cards.Length; Idx < 15; Idx++)
                    {
                        bool bIsAvailable = (deckInst.availableCardMask & (1 << Idx)) != 0;
                        if (bIsAvailable)
                        {
                            TriadCard cardOb = deckInst.GetCard(Idx);
                            bool bIsKnownPool = (deckInst.unknownPoolMask & (1 << Idx)) == 0;

                            var listToUse = bIsKnownPool ? knownCards : unknownCards;
                            listToUse.Add(cardOb);
                        }
                    }
                }
            }

            return (knownCards, unknownCards);
        }
    }
}