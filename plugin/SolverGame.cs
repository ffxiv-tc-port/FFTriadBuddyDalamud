using FFTriadBuddy;
using MgAl2O4.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        private bool wasPvP = false;
        private TriadNpc? pvpNpc;
        // [groupIdx][optionIdx] = list of cards in that option
        private List<List<TriadCard>>[]? tournamentGroups;
        private List<string> tournamentRuleNames = new();
        private List<TriadGameModifier> tournamentMods = new();

        // Best deck option per group (0-based), set after ComputeTournamentDeckSuggestion.
        public int[]? bestTournamentOptions;
        public float bestTournamentWinRate;
        // Set when the background computation in ComputeTournamentDeckSuggestion threw an
        // exception, so the UI can show a failure state instead of waiting forever.
        public bool tournamentComputeFailed;
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
        private static List<List<TriadCard>> GetValidCombinations(List<List<TriadCard>>[]? tournamentGroups, List<TriadCard> placedRedCards)
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
        // Background task: evaluates every my-deck option vs all opponent combos, stores the best in bestTournamentOptions.
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
            tournamentComputeFailed = false;

            Task.Run(() =>
            {
                try
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
                    var oppCombos = GetValidCombinations(groups, new List<TriadCard>());

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
                }
                catch (Exception ex)
                {
                    // Without this, an exception here would fault the Task silently: OnTournamentSuggestionReady
                    // would never fire and the UI would be stuck showing "Computing..." forever.
                    Service.logger.Error(ex, "[TournamentDeck] Failed to compute deck suggestion");
                    bestTournamentOptions = null;
                    tournamentComputeFailed = true;
                }
                finally
                {
                    OnTournamentSuggestionReady?.Invoke();
                }
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
        // 這個盤面的求解擲了例外、沒有提示。為真時 hasMove 必為假；重新求解或輪到對手時清掉。
        public bool solveFailed;
        public bool pvpNeedsDeckConfig;

        public Status status;
        public bool HasErrors => status != Status.NoErrors;

        public event Action<bool>? OnMoveChanged;
        // Fired when we detect the local player is the Red player in PvP (true=red, false=unknown/blue)
        public event Action<bool>? OnLocalPlayerSideDetected;
        // Fired whenever the current rules make the unlocked-card side-detection heuristic trustworthy
        // (true) or not (false, e.g. All Open / Chaos where both/neither hand renders unlocked).
        public event Action<bool>? OnSideDetectionReliabilityChanged;
        // Fired exactly once when a PvP match transitions from not-active to active (see UpdateGame for why this
        // can't just reuse OnLocalPlayerSideDetected, which fires on every update).
        public event Action? OnNewMatchStarted;

        /// <summary>
        /// The in-match solve runs on the thread pool; OnFrameworkUpdate applies its result on the framework thread.
        /// Contract: everything except WorkerLoop / RunSolve and what they call runs on the framework thread only.
        /// The worker touches only its SolveRequest (an independent copy), backgroundSolver and Service.logger.
        /// At most one solve runs at a time and only the newest waiting request is kept (latest wins). A changed
        /// solveGeneration makes a result stale: it is never applied, and the worker stops between FindNextMove calls.
        /// </summary>
        private readonly TriadGameSolver backgroundSolver = new();
        private readonly object solveLock = new();
        private readonly Action workerLoop;
        private SolveRequest? pendingRequest;   // under solveLock
        private SolveRequest? finishedRequest;  // under solveLock
        private bool workerRunning;             // under solveLock
        private int solveGeneration;
        private volatile bool disposed;
        private bool solveInFlight;

        // True from handing a board to the worker until OnFrameworkUpdate sees the worker idle.
        public bool IsSolving => solveInFlight;

        public SolverGame()
        {
            workerLoop = WorkerLoop;
#if DEBUG
            // turn on verbose debugging when checking solver's behavior
            backgroundSolver.agent.debugFlags = TriadGameAgent.DebugFlags.ShowMoveStart | TriadGameAgent.DebugFlags.ShowMoveDetails;
#endif // DEBUG
        }

        // Solver input and output. Filled on the framework thread, then owned by the worker until it is handed back.
        private sealed class SolveRequest
        {
            public readonly int generation;
            public readonly TriadGameSimulationState state;
            public readonly List<TriadGameModifier> modifiers;
            public readonly ETriadGameSpecialMod specialRules;
            public readonly TriadGameModifier.EFeature modFeatures;
            public readonly List<List<TriadCard>>[]? tournamentGroups;
            public readonly bool findRedAdvMove;

            public bool failed;
            public bool aborted;
            public int moveCardIdx = -1;
            public int moveBoardIdx = -1;
            public SolverResult moveResult;
            public int redCardIdx = -1;
            public int redBoardIdx = -1;
            public SolverResult redResult;

            public SolveRequest(int generation, TriadGameSimulationState state, TriadGameSimulation simulation,
                List<List<TriadCard>>[]? tournamentGroups, bool findRedAdvMove)
            {
                this.generation = generation;
                this.state = state;
                modifiers = new List<TriadGameModifier>(simulation.modifiers);
                specialRules = simulation.specialRules;
                modFeatures = simulation.modFeatures;
                this.tournamentGroups = tournamentGroups;
                this.findRedAdvMove = findRedAdvMove;
            }
        }

        // 不是 async：求解交給背景，這裡同步做完。例外在這裡接住，免得框架更新跳過本插件其餘的處理。
        public void UpdateGame(UIStateTriadGame stateOb)
        {
            try
            {
                UpdateGameInner(stateOb);
            }
            catch (Exception ex)
            {
                Service.logger.Error(ex, "[SolverGame] UpdateGame failed, move hint cleared until the next board update");
                ResetAfterFailedUpdate();
            }
        }

        // 契約：不可擲例外（呼叫端在 catch 裡）。
        // 清掉過期的出牌提示、作廢求解中的盤面；下一次盤面更新照常重新求解。牌組最佳化的暫停由 OnFrameworkUpdate 放開。
        private void ResetAfterFailedUpdate()
        {
            hasMove = false;
            hasRedAdvMove = false;
            solveFailed = true;
            try
            {
                InvalidatePendingSolve();
                OnMoveChanged?.Invoke(false);
            }
            catch (Exception ex)
            {
                Service.logger.Error(ex, "[SolverGame] cleanup after failed UpdateGame threw");
            }
        }

        // 遊戲的對局視窗關掉時呼叫：求解中的結果不再套用（提示本身照舊留著，和之前一樣）。
        public void DiscardPendingSolve() => InvalidatePendingSolve();

        public void Dispose()
        {
            disposed = true;
            InvalidatePendingSolve();
            lock (solveLock)
            {
                finishedRequest = null;
            }
        }

        private void InvalidatePendingSolve()
        {
            Volatile.Write(ref solveGeneration, solveGeneration + 1);
            lock (solveLock)
            {
                pendingRequest = null;
            }
        }

        // 每幀由插件的 Framework.Update 呼叫；沒有求解在跑時直接返回。
        public void OnFrameworkUpdate()
        {
            if (!solveInFlight || disposed)
            {
                return;
            }

            SolveRequest? done;
            bool idle;
            lock (solveLock)
            {
                done = finishedRequest;
                finishedRequest = null;
                idle = !workerRunning && pendingRequest == null;
            }

            if (done != null && !done.aborted && done.generation == solveGeneration)
            {
                try
                {
                    ApplySolve(done);
                }
                catch (Exception ex)
                {
                    Service.logger.Error(ex, "[SolverGame] applying solver result failed, move hint cleared until the next board update");
                    ResetAfterFailedUpdate();
                }
            }

            if (idle)
            {
                solveInFlight = false;
                SolverUtils.solverDeckOptimize?.SetPauseForGameSolver(false);
            }
        }

        private void UpdateGameInner(UIStateTriadGame stateOb)
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
            // A brand new PvP match starting (false -> true transition) fires a dedicated one-shot event so
            // UIReaderTriadGame can clear any manual override / stale side guess left over from a previous match.
            // This must NOT reuse OnLocalPlayerSideDetected: that event also fires on every UpdateGame call from the two
            // branches below, so it would wipe the player's manual swap whenever isPvP/status flickers for one scan.
            if (isPvP && !wasPvP)
                OnNewMatchStarted?.Invoke();
            // When solver detected NPC parse failed = PvP but GetUIStatePvP missed it, the local player must be Red
            // (blue player's name would match an NPC, only the red = local player's name causes parse failure).
            if (status == Status.FailedToParseNpc && !stateOb!.isPvP)
                OnLocalPlayerSideDetected?.Invoke(true);
            else if (!isPvP)
                OnLocalPlayerSideDetected?.Invoke(false);
            wasPvP = isPvP;

            // Under "All Open" / "Chaos" rules both hands render unlocked at once (or neither does),
            // so UIReaderTriadGame's unlocked-card heuristic has nothing to key off of and will just
            // keep repeating whatever side it last guessed - which is often wrong for the whole match.
            // Tell it to stop guessing and wait for the player to confirm/swap manually instead.
            bool sideDetectionUnreliable = isPvP && screenOb != null &&
                screenOb.mods.Exists(m => m is TriadGameModifierAllOpen || m is TriadGameModifierChaos);
            OnSideDetectionReliabilityChanged?.Invoke(!sideDetectionUnreliable);
            if (solverNpc != null &&
                screenOb != null && screenOb.turnState == ScannerTriad.ETurnState.Active &&
                stateOb != null)
            {
                var updateFlags = screenMemory.OnNewScan(screenOb, solverNpc);
                if (updateFlags != TriadGameScreenMemory.EUpdateFlags.None)
                {
                    solveFailed = false;
                    InvalidatePendingSolve();

                    bool canSolve = screenMemory.deckBlue != null && screenMemory.gameState != null && screenMemory.gameSolver != null;
                    // 舊盤面的提示在新結果出來前不留著（可能指向已經放了牌的格子）。
                    if (hasMove || !canSolve)
                    {
                        hasMove = false;
                        OnMoveChanged?.Invoke(hasMove);
                    }
                    hasRedAdvMove = false;

                    if (canSolve)
                    {
                        bool useTournamentMultiScenario = isPvP && tournamentGroups != null && tournamentGroups.Length > 0;
                        // when in PvP with known opponent deck, also find opponent's worst move (best for blue)
                        bool findRedAdvMove = isPvP && screenMemory.deckRed != null && GetPvPNpc().Deck.knownCards.Count > 0;
                        var gameState = screenMemory.gameState!;
                        // 複製建構子不帶 forcedCardIdx／bDebugRules，這裡補上，背景拿到的盤面才和原物件等值。
                        var stateCopy = new TriadGameSimulationState(gameState)
                        {
                            forcedCardIdx = gameState.forcedCardIdx,
                            bDebugRules = gameState.bDebugRules,
                        };
                        SubmitSolve(new SolveRequest(solveGeneration, stateCopy, screenMemory.gameSolver!.simulation,
                            useTournamentMultiScenario ? tournamentGroups : null, findRedAdvMove));
                    }
                }
            }
            else
            {
                InvalidatePendingSolve();
                if (hasMove)
                {
                    hasMove = false;
                    hasRedAdvMove = false;
                    OnMoveChanged?.Invoke(hasMove);
                }
                else
                {
                    solveFailed = false;
                }
            }
        }

        private void SubmitSolve(SolveRequest req)
        {
            solveInFlight = true;
            SolverUtils.solverDeckOptimize?.SetPauseForGameSolver(true);

            bool startWorker;
            lock (solveLock)
            {
                pendingRequest = req;
                startWorker = !workerRunning;
                workerRunning = true;
            }

            if (startWorker)
            {
                try
                {
                    _ = Task.Run(workerLoop);
                }
                catch
                {
                    lock (solveLock)
                    {
                        workerRunning = false;
                        pendingRequest = null;
                    }
                    throw;
                }
            }
        }

        // Thread pool. Never throws: RunSolve catches solver failures, and anything else just ends this worker.
        private void WorkerLoop()
        {
            try
            {
                while (true)
                {
                    SolveRequest? req;
                    lock (solveLock)
                    {
                        req = pendingRequest;
                        pendingRequest = null;
                        if (req == null || disposed)
                        {
                            workerRunning = false;
                            return;
                        }
                    }

                    RunSolve(req);

                    lock (solveLock)
                    {
                        finishedRequest = req;
                    }
                }
            }
            catch
            {
                lock (solveLock)
                {
                    workerRunning = false;
                }
            }
        }

        private bool IsStale(SolveRequest req) => disposed || Volatile.Read(ref solveGeneration) != req.generation;

        // Thread pool. Same FindNextMove calls, in the same order, as the old synchronous solve on the framework thread.
        private void RunSolve(SolveRequest req)
        {
            try
            {
                var simulation = backgroundSolver.simulation;
                simulation.modifiers.Clear();
                simulation.modifiers.AddRange(req.modifiers);
                simulation.specialRules = req.specialRules;
                simulation.modFeatures = req.modFeatures;

                bool finished = (req.tournamentGroups != null) ? SolveMultiScenario(req) : SolveSingleScenario(req);
                if (finished && req.findRedAdvMove)
                {
                    finished = FindWorstRedMoveForBlue(req);
                }

                req.aborted = !finished;
            }
            catch (Exception ex)
            {
                req.failed = true;
                try
                {
                    if (!disposed)
                    {
                        Service.logger.Error(ex, "[SolverGame] solve failed, move hint cleared until the next board update");
                    }
                }
                catch
                {
                    // 記錄本身失敗也不能讓工作執行緒停擺；結果照樣交回（failed）。
                }
            }
        }

        private bool SolveSingleScenario(SolveRequest req)
        {
            backgroundSolver.FindNextMove(req.state, out req.moveCardIdx, out req.moveBoardIdx, out req.moveResult);
            return true;
        }

        private bool SolveMultiScenario(SolveRequest req)
        {
            var gameState = req.state;

            // Collect red cards already on board to narrow valid combinations.
            var placedRed = new List<TriadCard>();
            foreach (var piece in gameState.board)
            {
                if (piece?.owner == ETriadCardOwner.Red && piece.card != null)
                    placedRed.Add(piece.card);
            }

            var combinations = GetValidCombinations(req.tournamentGroups, placedRed);
            if (combinations.Count == 0)
                return SolveSingleScenario(req);

            // score[cardIdx * 9 + boardPos] = sum of scores across scenarios
            var totalScore = new float[TriadDeckInstance.maxAvailableCards * 9];
            int scenarioCount = 0;

            foreach (var combo in combinations)
            {
                if (IsStale(req))
                    return false;

                var deck = new TriadDeck();
                deck.knownCards.AddRange(combo);

                var deckInst = new TriadDeckInstanceManual(deck);
                // Remove cards already placed on board from available mask.
                for (int i = 0; i < deck.knownCards.Count; i++)
                {
                    if (placedRed.Contains(deck.knownCards[i]))
                        deckInst.availableCardMask &= ~(1 << i);
                }

                var stateCopy = new TriadGameSimulationState(gameState);
                stateCopy.deckRed = deckInst;
                stateCopy.forcedCardIdx = gameState.forcedCardIdx;

                backgroundSolver.FindNextMove(stateCopy, out int cardIdx, out int boardPos, out var result);
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
                return SolveSingleScenario(req);

            if (IsStale(req))
                return false;

            // Run single-scenario solver to get a proper SolverResult for display.
            backgroundSolver.FindNextMove(gameState, out _, out _, out var displayResult);
            if (!disposed)
                Service.logger.Info($"[MultiScenario] {scenarioCount} combos, best: card[{bestCard}] board[{bestBoard}] score={bestScore:F2}");

            req.moveCardIdx = bestCard;
            req.moveBoardIdx = bestBoard;
            req.moveResult = displayResult;
            return true;
        }

        private bool FindWorstRedMoveForBlue(SolveRequest req)
        {
            var solver = backgroundSolver;
            var gameState = req.state;

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
                    if (IsStale(req)) return false;

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

            req.redCardIdx = bestCardIdx;
            req.redBoardIdx = bestBoardPos;
            req.redResult = bestResult;
            return true;
        }

        // Framework thread, current generation only. Same field updates and event order as the old synchronous code.
        private void ApplySolve(SolveRequest req)
        {
            if (req.failed)
            {
                hasMove = false;
                hasRedAdvMove = false;
                solveFailed = true;
                OnMoveChanged?.Invoke(false);
                return;
            }

            hasMove = true;
            moveCardIdx = req.moveCardIdx;
            moveBoardIdx = (moveCardIdx < 0) ? -1 : req.moveBoardIdx;
            moveWinChance = req.moveResult;

            var deckBlue = req.state.deckBlue;
            int forcedCardIdx = req.state.forcedCardIdx;
            var solverCardOb = deckBlue.GetCard(moveCardIdx);
            if ((forcedCardIdx >= 0) && (moveCardIdx != forcedCardIdx))
            {
                // swap + chaos may cause selecting wrong instance of duplicated card?
                // it really, really shouldn't unless solver's agent is broken

                var forcedCardOb = deckBlue.GetCard(forcedCardIdx);

                var solverCardDesc = solverCardOb != null ? solverCardOb.Name.GetCodeName() : "??";
                var forcedCardDesc = forcedCardOb != null ? forcedCardOb.Name.GetCodeName() : "??";
                Service.logger.Warning($"Solver selected card [{moveCardIdx}]:{solverCardDesc}, but game wants: [{forcedCardIdx}]:{forcedCardDesc} !");

                moveCardIdx = forcedCardIdx;
                solverCardOb = forcedCardOb;
            }

            Logger.WriteLine("  suggested move: [{0}] {1} {2} (expected: {3})",
                moveBoardIdx, ETriadCardOwner.Blue,
                solverCardOb != null ? solverCardOb.Name.GetCodeName() : "??",
                moveWinChance.expectedResult);

            OnMoveChanged?.Invoke(hasMove);

            if (req.findRedAdvMove)
            {
                hasRedAdvMove = req.redCardIdx >= 0;
                redAdvMoveCardIdx = req.redCardIdx;
                redAdvMoveBoardIdx = req.redBoardIdx;
                redAdvMoveWinChance = req.redResult;
            }
            else
            {
                hasRedAdvMove = false;
            }
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