using FFTriadBuddy;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MgAl2O4.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace TriadBuddyPlugin
{
    // Reads candidate cards from the tournament deck builder UI (TripleTriadPickUpDeckSelect).
    // The screen shows 3 groups; the opponent picks one option per group to form their 5-card deck:
    //   Group 1 (root id=6):  3 options × 2 cards each
    //   Group 2 (root id=20): 3 options × 2 cards each
    //   Group 3 (root id=34): 3 options × 1 card  each
    public class UIReaderTriadTournamentDeck : IUIReader
    {
        // [groupIdx][optionIdx] = list of card IDs in that option
        public List<List<int>>[] groups = new List<List<int>>[3];
        // Rule names parsed from the tournament deck selection screen (e.g. ["選拔", "全明牌", "逆轉"])
        public List<string> ruleNames = new();

        public Action? OnCandidatesChanged;
        public Action<bool>? OnAddonVisibilityChanged;

        public UIReaderTriadTournamentDeck()
        {
            for (int i = 0; i < 3; i++)
                groups[i] = new List<List<int>>();
        }

        private IntPtr cachedAddonPtr = IntPtr.Zero;

        public string GetAddonName() => "TripleTriadPickUpDeckSelect";
        public void OnAddonLost() { cachedAddonPtr = IntPtr.Zero; OnAddonVisibilityChanged?.Invoke(false); }
        public void OnAddonShown(IntPtr addonPtr) { cachedAddonPtr = addonPtr; OnAddonVisibilityChanged?.Invoke(true); ReadCandidates(addonPtr); }
        public void OnAddonUpdate(IntPtr addonPtr) { cachedAddonPtr = addonPtr; }

        // Returns screen position and size of the slot node for the given group and option.
        public unsafe (Vector2 pos, Vector2 size) GetSlotPosAndSize(int groupIdx, int optionIdx)
        {
            if (cachedAddonPtr == IntPtr.Zero) return (Vector2.Zero, Vector2.Zero);
            var baseNode = (AtkUnitBase*)cachedAddonPtr;

            // slot node IDs per group: group0→11,12,13  group1→25,26,27  group2→39,40,41
            uint[][] slotIds = { new uint[] { 11, 12, 13 }, new uint[] { 25, 26, 27 }, new uint[] { 39, 40, 41 } };
            if (groupIdx < 0 || groupIdx >= 3 || optionIdx < 0 || optionIdx >= slotIds[groupIdx].Length)
                return (Vector2.Zero, Vector2.Zero);

            var node = baseNode->GetNodeById(slotIds[groupIdx][optionIdx]);
            if (node == null) return (Vector2.Zero, Vector2.Zero);
            return GUINodeUtils.GetNodePosAndSize(node);
        }

        private unsafe void ReadCandidates(IntPtr addonPtr)
        {
            var baseNode = (AtkUnitBase*)addonPtr;
            if (baseNode == null) return;

            // Rule names are displayed in node[49] (id=3), format: "Rule1/Rule2\n\tRule3"
            var newRuleNames = new List<string>();
            if (baseNode->UldManager.NodeListCount > 49)
            {
                var ruleNode = baseNode->UldManager.NodeList[49];
                if (ruleNode != null && ruleNode->Type == NodeType.Text)
                {
                    var ruleText = GUINodeUtils.GetNodeText(ruleNode) ?? "";
                    foreach (var part in ruleText.Split(new[] { '/', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var name = part.Trim();
                        if (!string.IsNullOrEmpty(name))
                            newRuleNames.Add(name);
                    }
                }
            }

            // Group 1 (root id=6): pair slots id=11,12,13 → 2 cards each at component index 5 and 6
            var g1 = ReadGroup(baseNode, new uint[] { 11, 12, 13 }, new int[] { 5, 6 });
            // Group 2 (root id=20): pair slots id=25,26,27 → 2 cards each at component index 5 and 6
            var g2 = ReadGroup(baseNode, new uint[] { 25, 26, 27 }, new int[] { 5, 6 });
            // Group 3 (root id=34): single slots id=39,40,41 → 1 card each at component index 4
            var g3 = ReadGroup(baseNode, new uint[] { 39, 40, 41 }, new int[] { 4 });

            bool rulesChanged = !newRuleNames.SequenceEqual(ruleNames);
            bool cardsChanged = !GroupsEqual(groups[0], g1) || !GroupsEqual(groups[1], g2) || !GroupsEqual(groups[2], g3);

            ruleNames = newRuleNames;
            groups[0] = g1;
            groups[1] = g2;
            groups[2] = g3;

            if (cardsChanged || rulesChanged)
            {
                Service.logger.Info($"[TournamentDeck] Read {CountCards(g1)} + {CountCards(g2)} + {CountCards(g3)} cards, rules: [{string.Join(", ", ruleNames)}]");
                OnCandidatesChanged?.Invoke();
            }
        }

        private unsafe List<List<int>> ReadGroup(AtkUnitBase* baseNode, uint[] slotNodeIds, int[] cardComponentIndices)
        {
            var result = new List<List<int>>();
            foreach (var slotId in slotNodeIds)
            {
                var slotNode = baseNode->GetNodeById(slotId);
                if (slotNode == null) { result.Add(new List<int>()); continue; }

                var slotComp = (AtkComponentNode*)slotNode;
                var cards = new List<int>();
                foreach (var ci in cardComponentIndices)
                {
                    if (ci >= slotComp->Component->UldManager.NodeListCount) continue;
                    var cardCompNode = slotComp->Component->UldManager.NodeList[ci];
                    if (cardCompNode == null || (int)cardCompNode->Type < 1000) continue;

                    int cardId = ReadCardIdFromComponent((AtkComponentNode*)cardCompNode);
                    if (cardId > 0) cards.Add(cardId);
                }
                result.Add(cards);
            }
            return result;
        }

        // Inside a card component (type=1006), NodeList[3] is the icon image node (id=20).
        // Texture path is "ui/icon/087000/087XXX.tex" where XXX - 87000 = card ID.
        private unsafe int ReadCardIdFromComponent(AtkComponentNode* cardCompNode)
        {
            if (cardCompNode->Component->UldManager.NodeListCount < 4) return -1;
            var iconNode = cardCompNode->Component->UldManager.NodeList[3];
            var texPath = GUINodeUtils.GetNodeTexturePath(iconNode);
            return ParseCardIdFromIconPath(texPath);
        }

        private static int ParseCardIdFromIconPath(string? texPath)
        {
            if (texPath == null) return -1;
            // e.g. "ui/icon/087000/087403.tex"
            int lastSlash = texPath.LastIndexOf('/');
            if (lastSlash < 0) return -1;
            var filename = texPath.Substring(lastSlash + 1);
            if (!filename.EndsWith(".tex")) return -1;
            if (!int.TryParse(filename[..^4], out int iconNum)) return -1;
            int cardId = iconNum - 87000;
            return (cardId > 0 && cardId < 500) ? cardId : -1;
        }

        // Collect all unique candidate card IDs across all groups and options.
        public List<int> GetAllCandidateCardIds()
        {
            var result = new List<int>();
            foreach (var group in groups)
                foreach (var option in group)
                    foreach (var id in option)
                        if (!result.Contains(id)) result.Add(id);
            return result;
        }

        private static bool GroupsEqual(List<List<int>> a, List<List<int>> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Count != b[i].Count) return false;
                for (int j = 0; j < a[i].Count; j++)
                    if (a[i][j] != b[i][j]) return false;
            }
            return true;
        }

        private static int CountCards(List<List<int>> g)
        {
            int n = 0;
            foreach (var opt in g) n += opt.Count;
            return n;
        }
    }
}
