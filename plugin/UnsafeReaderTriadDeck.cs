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
                    // 台服:舊的呼叫點特徵碼多重命中、解出兩個相異目標,所以改成直接錨在函式序言(不再是 E8 呼叫點);
                    // 遮罩掉堆疊框大小、__chkstk rel32 與 rip 相對位移,保留 lea r12,[rcx+0xF0] 這個語意錨(把 +0xF0 改成 +0xE8 即 0 命中)。
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
