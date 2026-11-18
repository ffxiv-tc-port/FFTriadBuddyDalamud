using FFXIVClientStructs.FFXIV.Component.GUI;
using MgAl2O4.Utils;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace TriadBuddyPlugin
{
    public class UIReaderTriadGame : IUIReader
    {
        [StructLayout(LayoutKind.Explicit, Size = 0x1000)]              // no idea what size, last entries seems to be around +0xfc0? 
        private unsafe struct AddonTripleTriad
        {
            [FieldOffset(0x0)] public AtkUnitBase AtkUnitBase;
            [FieldOffset(0x238)] public byte TurnState;                 // 0: waiting, 1: normal move, 2: masked move (order/chaos)

            [FieldOffset(0x240)] public AddonTripleTriadCard BlueDeck0; // 2be = end of numbers
            [FieldOffset(0x2e8)] public AddonTripleTriadCard BlueDeck1; // 366 = end of numbers
            [FieldOffset(0x390)] public AddonTripleTriadCard BlueDeck2;
            [FieldOffset(0x438)] public AddonTripleTriadCard BlueDeck3;
            [FieldOffset(0x4e0)] public AddonTripleTriadCard BlueDeck4;

            [FieldOffset(0x588)] public AddonTripleTriadCard RedDeck0;
            [FieldOffset(0x630)] public AddonTripleTriadCard RedDeck1;
            [FieldOffset(0x6d8)] public AddonTripleTriadCard RedDeck2;
            [FieldOffset(0x780)] public AddonTripleTriadCard RedDeck3;
            [FieldOffset(0x828)] public AddonTripleTriadCard RedDeck4;

            [FieldOffset(0x8d0)] public AddonTripleTriadCard Board0;
            [FieldOffset(0x978)] public AddonTripleTriadCard Board1;
            [FieldOffset(0xa20)] public AddonTripleTriadCard Board2;
            [FieldOffset(0xac8)] public AddonTripleTriadCard Board3;
            [FieldOffset(0xb70)] public AddonTripleTriadCard Board4;
            [FieldOffset(0xc18)] public AddonTripleTriadCard Board5;
            [FieldOffset(0xcc0)] public AddonTripleTriadCard Board6;
            [FieldOffset(0xd68)] public AddonTripleTriadCard Board7;
            [FieldOffset(0xe10)] public AddonTripleTriadCard Board8;
        }

        [StructLayout(LayoutKind.Explicit, Size = 0xA8)]
        private unsafe struct AddonTripleTriadCard
        {
            [FieldOffset(0x8)] public AtkComponentBase* CardDropControl;
            [FieldOffset(0x80)] public byte CardRarity;                 // 1..5
            [FieldOffset(0x81)] public byte CardType;                   // 0: no type, 1: primal, 2: scion, 3: beastman, 4: garland
            [FieldOffset(0x82)] public byte CardOwner;                  // 0: empty, 1: blue, 2: red
            [FieldOffset(0x83)] public byte NumSideU;
            [FieldOffset(0x84)] public byte NumSideD;
            [FieldOffset(0x85)] public byte NumSideR;
            [FieldOffset(0x86)] public byte NumSideL;
            [FieldOffset(0xA4)] public bool HasCard;

            // 0x87 - constant per card, changes between npcs
            // 0x88 - fixed per card, not ID
            // 0x89 - fixed per card, 40/41 ?
        }

        public enum Status
        {
            NoErrors,
            AddonNotFound,
            AddonNotVisible,
            PvPMatch,
            FailedToReadMove,
            FailedToReadRules,
            FailedToReadRedPlayer,
            FailedToReadCards,
        }

        public UIStateTriadGame? currentState;
        public Status status = Status.AddonNotFound;
        private bool lastKnownLocalIsBlue = true;
        private bool manualSideOverrideActive = false;
        public bool forcedLocalIsRed = false;
        // false when current rules (All Open / Chaos) make the unlocked-card heuristic meaningless;
        // in that case the player must confirm/swap the side manually via ToggleLocalSide().
        public bool sideDetectionReliable = true;
        public bool HasErrors => status >= Status.FailedToReadMove;
        public bool IsVisible => (status != Status.AddonNotFound) && (status != Status.AddonNotVisible);

        public event Action<UIStateTriadGame?>? OnUIStateChanged;

        private IntPtr addonPtr;

        // Each frame is read into scratchState; it becomes currentState only when it differs, and is then replaced,
        // so a state handed to OnUIStateChanged is never written again. Native pointers in the lists are this frame's only.
        private UIStateTriadGame? scratchState;
        private readonly List<ulong> nodesL0 = new();
        private readonly List<ulong> nodesNameL1 = new();
        private readonly List<ulong> nodesNameL2 = new();
        private readonly List<ulong> nodesRule = new();
        private readonly NativeStringSlots redPlayerTexts = new(isAnsi: false);
        private readonly NativeStringSlots ruleTexts = new(isAnsi: false);
        private readonly NativeStringSlots cardTexturePaths = new(isAnsi: true);

        public string GetAddonName()
        {
            return "TripleTriad";
        }

        public void OnAddonLost()
        {
            SetStatus(Status.AddonNotFound);
            SetCurrentState(null);
            addonPtr = IntPtr.Zero;
            manualSideOverrideActive = false;
        }

        // Lets the user manually flip which side is "local" when auto-detection gets it wrong.
        // Stays pinned until a new match starts (ResetSideDetectionForNewMatch) or the addon is lost.
        public void ToggleLocalSide()
        {
            manualSideOverrideActive = true;
            lastKnownLocalIsBlue = !lastKnownLocalIsBlue;
        }

        // Called when SolverGame detects a brand-new match starting: clears any manual override and
        // resets the initial guess to blue (the common case), so unreliable-detection rules (All Open /
        // Chaos) still start from a sane guess for the player to confirm/swap instead of carrying over
        // a stale value from whatever match happened before.
        public void ResetSideDetectionForNewMatch()
        {
            manualSideOverrideActive = false;
            lastKnownLocalIsBlue = true;
        }

        public void OnAddonShown(IntPtr addonPtr)
        {
            this.addonPtr = addonPtr;
        }

        public unsafe void OnAddonUpdate(IntPtr addonPtr)
        {
            var addon = (AddonTripleTriad*)addonPtr;
            if (addon == null)
            {
                return;
            }

            status = Status.NoErrors;
            var newState = scratchState ??= CreateScratchState();
            newState.move = 0;
            newState.isPvP = false;
            newState.localIsBlue = true;

            GUINodeUtils.CollectImmediateChildNodes(addon->AtkUnitBase.RootNode, nodesL0);
            ReadUIDescriptionRedPlayer(nodesL0, newState.redPlayerDesc);
            ReadUIDescriptionRules(nodesL0, newState.rules);
            newState.isPvP = GetUIStatePvP(nodesL0);

            if (status == Status.NoErrors)
            {
                newState.move = addon->TurnState;
                if (newState.move > 2)
                {
                    SetStatus(Status.FailedToReadMove);
                }
            }

            if (status == Status.NoErrors)
            {
                ReadCardData(addon->BlueDeck0, newState.blueDeck[0], 0);
                ReadCardData(addon->BlueDeck1, newState.blueDeck[1], 1);
                ReadCardData(addon->BlueDeck2, newState.blueDeck[2], 2);
                ReadCardData(addon->BlueDeck3, newState.blueDeck[3], 3);
                ReadCardData(addon->BlueDeck4, newState.blueDeck[4], 4);

                ReadCardData(addon->RedDeck0, newState.redDeck[0], 5);
                ReadCardData(addon->RedDeck1, newState.redDeck[1], 6);
                ReadCardData(addon->RedDeck2, newState.redDeck[2], 7);
                ReadCardData(addon->RedDeck3, newState.redDeck[3], 8);
                ReadCardData(addon->RedDeck4, newState.redDeck[4], 9);

                ReadCardData(addon->Board0, newState.board[0], 10);
                ReadCardData(addon->Board1, newState.board[1], 11);
                ReadCardData(addon->Board2, newState.board[2], 12);
                ReadCardData(addon->Board3, newState.board[3], 13);
                ReadCardData(addon->Board4, newState.board[4], 14);
                ReadCardData(addon->Board5, newState.board[5], 15);
                ReadCardData(addon->Board6, newState.board[6], 16);
                ReadCardData(addon->Board7, newState.board[7], 17);
                ReadCardData(addon->Board8, newState.board[8], 18);
            }

            if (status == Status.NoErrors && newState.move > 0 && !manualSideOverrideActive && sideDetectionReliable)
            {
                bool hasUnlockedBlue = Array.Exists(newState.blueDeck, c => c.isPresent && !c.isLocked);
                bool hasUnlockedRed = Array.Exists(newState.redDeck, c => c.isPresent && !c.isLocked);
                if (hasUnlockedBlue && !hasUnlockedRed) lastKnownLocalIsBlue = true;
                else if (hasUnlockedRed && !hasUnlockedBlue) lastKnownLocalIsBlue = false;
                // if both or neither unlocked (e.g. chaos rules), keep last known value
            }
            if (status == Status.NoErrors && !manualSideOverrideActive)
            {
                if (forcedLocalIsRed)
                {
                    lastKnownLocalIsBlue = false;
                }
                else if (!newState.isPvP)
                {
                    lastKnownLocalIsBlue = true; // NPC match: local player is always blue
                }
            }
            if (status == Status.NoErrors)
            {
                newState.localIsBlue = lastKnownLocalIsBlue;
            }

            SetCurrentState(status == Status.NoErrors ? newState : null);
            if (ReferenceEquals(currentState, newState))
            {
                scratchState = null;
            }

            // pvp status is part of UI state, allow setting detection, just make sure that solver will be disabled
            // if this flag is not set in pvp match, red player description will not match known npc and solver will be in failed state anyway
            if (newState.isPvP)
            {
                SetStatus(Status.PvPMatch);
            }
        }

        private void SetStatus(Status newStatus)
        {
            if (status != newStatus)
            {
                status = newStatus;
                if (HasErrors)
                {
                    Service.logger.Error("ui reader error: " + newStatus);
                }
            }
        }

        private void SetCurrentState(UIStateTriadGame? newState)
        {
            bool isEmpty = newState == null;
            bool wasEmpty = currentState == null;

            if (isEmpty && wasEmpty)
            {
                return;
            }

            bool changed = (isEmpty != wasEmpty);
            if (!changed && newState != null && currentState != null)
            {
                changed = !currentState.Equals(newState);
            }

            if (changed)
            {
                currentState = newState;
                OnUIStateChanged?.Invoke(newState);
            }
        }

        private static UIStateTriadGame CreateScratchState()
        {
            var state = new UIStateTriadGame();
            for (int idx = 0; idx < state.blueDeck.Length; idx++) { state.blueDeck[idx] = new UIStateTriadCard(); }
            for (int idx = 0; idx < state.redDeck.Length; idx++) { state.redDeck[idx] = new UIStateTriadCard(); }
            for (int idx = 0; idx < state.board.Length; idx++) { state.board[idx] = new UIStateTriadCard(); }
            return state;
        }

        private unsafe void ReadUIDescriptionRedPlayer(List<ulong> level0, List<string> listRedDesc)
        {
            listRedDesc.Clear();

            var nodeName0 = GUINodeUtils.PickNode(level0, 6, 12);
            GUINodeUtils.CollectImmediateChildNodes(nodeName0, nodesNameL1);
            var nodeNameL1 = GUINodeUtils.PickNode(nodesNameL1, 0, 5);
            GUINodeUtils.CollectAllChildNodes(nodeNameL1, nodesNameL2);
            // there are multiple text boxes, for holding different combinations of name & titles?
            // idk, too lazy to investigate, grab everything inside
            int numParsed = 0;
            foreach (var testNodeAddr in nodesNameL2)
            {
                var testNode = (AtkResNode*)testNodeAddr;
                var isVisible = (testNode != null) ? (testNode->NodeFlags & NodeFlags.Visible) == NodeFlags.Visible : false;
                if (isVisible)
                {
                    numParsed++;
                    var text = redPlayerTexts.Read(numParsed - 1, GUINodeUtils.GetNodeTextPtr(testNode));
                    if (!string.IsNullOrEmpty(text))
                    {
                        listRedDesc.Add(text);
                    }
                }
            }

            if (numParsed == 0)
            {
                SetStatus(Status.FailedToReadRedPlayer);
            }
        }

        private unsafe void ReadUIDescriptionRules(List<ulong> level0, List<string> listRuleDesc)
        {
            listRuleDesc.Clear();

            var nodeRule0 = GUINodeUtils.PickNode(level0, 4, 12);
            GUINodeUtils.CollectImmediateChildNodes(nodeRule0, nodesRule);
            if (nodesRule.Count == 5)
            {
                for (int idx = 0; idx < 4; idx++)
                {
                    var text = ruleTexts.Read(idx, GUINodeUtils.GetNodeTextPtr((AtkResNode*)nodesRule[4 - idx]));
                    if (!string.IsNullOrEmpty(text))
                    {
                        listRuleDesc.Add(text);
                    }
                }
            }
            else
            {
                SetStatus(Status.FailedToReadRules);
            }
        }

        private unsafe bool GetUIStatePvP(List<ulong> level0)
        {
            // verify, is that for both pvp and tournament games?
            var nodePvPButton = GUINodeUtils.PickNode(level0, 11, 12);
            if (nodePvPButton != null && nodePvPButton->IsVisible())
            {
                return true;
            }

            return false;
        }

        private unsafe (string, bool) GetCardTextureData(AddonTripleTriadCard addonCard, int slot)
        {
            // DragDrop Component
            // [1] Icon Component
            //     [0] Base Component <- locked out colors here
            //         [3] Image Node
            var nodeA = GUINodeUtils.PickChildNode(addonCard.CardDropControl, 1, 3);
            var nodeB = GUINodeUtils.PickChildNode(nodeA, 0, 2);
            var nodeC = GUINodeUtils.PickChildNode(nodeB, 3, 21);
            var texPath = cardTexturePaths.Read(slot, GUINodeUtils.GetNodeTexturePathPtr(nodeC));

            if (nodeC == null)
            {
                SetStatus(Status.FailedToReadCards);
            }

            bool isLocked = (nodeB != null) && (nodeB->MultiplyRed < 100);
            return (texPath ?? "", isLocked);
        }

        // Leaves resultOb exactly as a new UIStateTriadCard filled from addonCard; slot keys the texture path cache.
        private unsafe void ReadCardData(AddonTripleTriadCard addonCard, UIStateTriadCard resultOb, int slot)
        {
            resultOb.numU = 0;
            resultOb.numL = 0;
            resultOb.numD = 0;
            resultOb.numR = 0;
            resultOb.rarity = 0;
            resultOb.type = 0;
            resultOb.owner = 0;
            resultOb.isPresent = false;
            resultOb.isLocked = false;
            resultOb.texturePath = null;

            if (addonCard.HasCard)
            {
                resultOb.isPresent = true;
                resultOb.owner = addonCard.CardOwner;

                bool isKnown = (addonCard.NumSideU != 0);
                if (isKnown)
                {
                    resultOb.numU = addonCard.NumSideU;
                    resultOb.numL = addonCard.NumSideL;
                    resultOb.numD = addonCard.NumSideD;
                    resultOb.numR = addonCard.NumSideR;
                    resultOb.rarity = addonCard.CardRarity;
                    resultOb.type = addonCard.CardType;

                    (resultOb.texturePath, resultOb.isLocked) = GetCardTextureData(addonCard, slot);
                }
            }
        }

        private unsafe (Vector2, Vector2) GetCardPosAndSize(AddonTripleTriadCard addonCard)
        {
            if (addonCard.CardDropControl != null && addonCard.CardDropControl->OwnerNode != null)
            {
                var resNode = &addonCard.CardDropControl->OwnerNode->AtkResNode;
                return GUINodeUtils.GetNodePosAndSize(resNode);
            }

            return (Vector2.Zero, Vector2.Zero);
        }

        // Draw 端呼叫：Update 之後視窗若已被關掉或換掉就回 false，呼叫端整段不畫。
        public bool IsAddonStillCurrent()
        {
            return GetAddonPtrForDraw() != IntPtr.Zero;
        }

        private IntPtr GetAddonPtrForDraw()
        {
            return UIReaderScheduler.GetIfStillCurrent(Service.gameGui, GetAddonName(), addonPtr);
        }

        public unsafe (Vector2, Vector2) GetBlueCardPosAndSize(int idx)
        {
            var drawAddonPtr = GetAddonPtrForDraw();
            if (drawAddonPtr != IntPtr.Zero)
            {
                var addon = (AddonTripleTriad*)drawAddonPtr;
                switch (idx)
                {
                    case 0: return GetCardPosAndSize(addon->BlueDeck0);
                    case 1: return GetCardPosAndSize(addon->BlueDeck1);
                    case 2: return GetCardPosAndSize(addon->BlueDeck2);
                    case 3: return GetCardPosAndSize(addon->BlueDeck3);
                    case 4: return GetCardPosAndSize(addon->BlueDeck4);
                    default: break;
                }
            }

            return (Vector2.Zero, Vector2.Zero);
        }

        public unsafe (Vector2, Vector2) GetRedCardPosAndSize(int idx)
        {
            var drawAddonPtr = GetAddonPtrForDraw();
            if (drawAddonPtr != IntPtr.Zero)
            {
                var addon = (AddonTripleTriad*)drawAddonPtr;
                switch (idx)
                {
                    case 0: return GetCardPosAndSize(addon->RedDeck0);
                    case 1: return GetCardPosAndSize(addon->RedDeck1);
                    case 2: return GetCardPosAndSize(addon->RedDeck2);
                    case 3: return GetCardPosAndSize(addon->RedDeck3);
                    case 4: return GetCardPosAndSize(addon->RedDeck4);
                    default: break;
                }
            }

            return (Vector2.Zero, Vector2.Zero);
        }

        public unsafe (Vector2, Vector2) GetBoardCardPosAndSize(int idx)
        {
            var drawAddonPtr = GetAddonPtrForDraw();
            if (drawAddonPtr != IntPtr.Zero)
            {
                var addon = (AddonTripleTriad*)drawAddonPtr;
                switch (idx)
                {
                    case 0: return GetCardPosAndSize(addon->Board0);
                    case 1: return GetCardPosAndSize(addon->Board1);
                    case 2: return GetCardPosAndSize(addon->Board2);
                    case 3: return GetCardPosAndSize(addon->Board3);
                    case 4: return GetCardPosAndSize(addon->Board4);
                    case 5: return GetCardPosAndSize(addon->Board5);
                    case 6: return GetCardPosAndSize(addon->Board6);
                    case 7: return GetCardPosAndSize(addon->Board7);
                    case 8: return GetCardPosAndSize(addon->Board8);
                    default: break;
                }
            }

            return (Vector2.Zero, Vector2.Zero);
        }
    }
}
