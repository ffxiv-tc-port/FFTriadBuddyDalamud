using System;
using System.Runtime.InteropServices;

namespace TriadBuddyPlugin
{
    public class UnsafeReaderTriadDeck
    {
        public bool HasErrors { get; private set; }

        private delegate void SetSelectedCardDelegate(IntPtr addonPtr, int cellIdx);
        private readonly SetSelectedCardDelegate? SetSelectedCardFunc;

        private delegate void RefreshUIDelegate(IntPtr agentPtr);
        private readonly RefreshUIDelegate? RefreshUIFunc;

        public UnsafeReaderTriadDeck()
        {
            IntPtr SetSelectedCardPtr = IntPtr.Zero;
            IntPtr RefreshUIPtr = IntPtr.Zero;

            if (Service.sigScanner != null)
            {
                try
                {
                    SetSelectedCardPtr = Service.sigScanner.ScanText("E8 ?? ?? ?? ?? BE ?? ?? ?? ?? 40 84 FF");

                    // Client::UI::Agent::AgentGoldSaucer.ReceiveEvent msg:6 -> FUN_140b973b0 msg:7
                    //  writes to agent +0x100 and calls refresh
                    //
                    // 台服 7.20 離線鑑識(2026-08-20):舊的呼叫點特徵碼
                    // "e8 ?? ?? ?? ?? 84 c0 0f 94 c0 88 43 58" 在台服執行檔有 7 個命中,
                    // 解出兩個相異目標 —— 6 個指向 0x140FF6AE0、1 個指向 0x140FF5780。
                    // Dalamud 取位址最小的第一命中,目前剛好是對的那個,但只是巧合。
                    //
                    // 正解 = 0x140FF6AE0,證據三軸:
                    //  (1) 它最後把 AtkValue 陣列送給 addon id = [agent+0xF0];而 [agent+0xF0]
                    //      在整支執行檔只有一個寫入點 0x140FF69FC,值來自
                    //      RaptureAtkModule::OpenAddon(addonNameId = 0x170)。台服 addon 名稱表
                    //      (0x141FE7160 起、stride 0x18、index 0 = "CursorAddon")的第 0x170 項
                    //      逐字就是 "GSInfoEditDeck" —— 正是本 reader 的 GetAddonName()。
                    //      另一個候選 0x140FF5780 送的是 [agent+0xE8],那不是本 addon。
                    //  (2) 只有它讀 agent+0x120/0x121/0x122/0x124(本外掛在呼叫前歸零的三個牌組篩選欄位)、
                    //      agent+0x110..0x118 的 5 個牌組欄位、以及 agent+0x28/0x30 這對迭代器(>= 5 張)。
                    //  (3) 上游註解說的「寫 agent+0x100 之後呼叫 refresh」在台服對應到
                    //      0x140FF7112: mov [rbx+0x100], esi / 0x140FF711B: call 0x140FF6AE0,
                    //      該處位於 ReceiveEvent 的子處理常式 0x140FF6FB0(= FUN_140b973b0 的對應物)。
                    //
                    // 改成直接錨在函式序言(不再是 E8 呼叫點),台服 7.20 唯一命中 0x140FF6AE0
                    // 且落在 .pdata 函式起點;遮罩掉堆疊框大小、__chkstk rel32 與 rip 相對位移,
                    // 保留 lea r12,[rcx+0xF0] 這個語意錨(把 +0xF0 改成 +0xE8 即 0 命中)。
                    // 找不到時 ScanText 擲例外 -> HasErrors -> 整個 reader 關閉(fail-closed)。
                    RefreshUIPtr = Service.sigScanner.ScanText(
                        "41 54 41 56 B8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 2B E0 48 8B 05 ?? ?? ?? ?? " +
                        "48 33 C4 48 89 84 24 ?? ?? ?? ?? 4C 8D A1 F0 00 00 00 4C 8B F1 41 83 3C 24 00");
                }
                catch (Exception ex)
                {
                    Service.logger.Error(ex, "oh noes!");
                }
            }

            HasErrors = (SetSelectedCardPtr == IntPtr.Zero) || (RefreshUIPtr == IntPtr.Zero);
            if (!HasErrors)
            {
                SetSelectedCardFunc = Marshal.GetDelegateForFunctionPointer<SetSelectedCardDelegate>(SetSelectedCardPtr);
                RefreshUIFunc = Marshal.GetDelegateForFunctionPointer<RefreshUIDelegate>(RefreshUIPtr);
            }
            else
            {
                Service.logger.Error("Failed to find triad deck functions, turning reader off");
            }
        }

        public void SetSelectedCard(IntPtr addonPtr, int cellIdx)
        {
            if (SetSelectedCardFunc == null || cellIdx < 0 || cellIdx >= 30)
            {
                return;
            }

            SetSelectedCardFunc(addonPtr, cellIdx);
        }

        public void RefreshUI(IntPtr agentPtr)
        {
            if (RefreshUIFunc != null)
            {
                RefreshUIFunc(agentPtr);
            }
        }
    }
}
