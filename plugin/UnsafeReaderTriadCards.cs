using System;
using System.Runtime.InteropServices;

namespace TriadBuddyPlugin
{
    public class UnsafeReaderTriadCards
    {
        public bool HasErrors { get; private set; }

        private delegate byte IsCardOwnedDelegate(IntPtr uiState, ushort cardId);
        private delegate byte IsNpcBeatenDelegate(IntPtr uiState, int triadNpcId);

        private readonly IsCardOwnedDelegate? IsCardOwnedFunc;
        private readonly IsNpcBeatenDelegate? IsNpcBeatenFunc;
        private readonly IntPtr UIStatePtr;

        public UnsafeReaderTriadCards()
        {
            IntPtr IsCardOwnedPtr = IntPtr.Zero;
            IntPtr IsNpcBeatenPtr = IntPtr.Zero;

            if (Service.sigScanner != null)
            {
                try
                {
                    // IsTriadNpcCompleted(void* uiState, int triadNpcId)
                    //   identified by pretty unique rowId from TripleTriad sheet: 0x230002
                    //   looking for negative of that number (0xFFDCFFFE) gives pretty much only npc access functions (set + get)

                    IsNpcBeatenPtr = Service.sigScanner.ScanText("40 53 48 83 ec 20 8d 82 fe ff dc ff");

                    // IsTriadCardOwned(void* uiState, ushort cardId)
                    //   used by GSInfoCardList's agent, function preparing card lists
                    //   +0x30 ptr to end of list, +0x10c used filter (all, only owned, only missing)
                    //   break on end of list write, check loops counting cards at function start in filter == 1 scope

                    IsCardOwnedPtr = Service.sigScanner.ScanText("40 53 48 83 ec 20 48 8b d9 66 85 d2 74 3b 0f");

                    // UIState addr, use LEA opcode before calling IsTriadCardOwned, same function as described above
                    // 台服 7.20 離線鑑識(2026-08-20):這條特徵碼在台服執行檔有 4 個命中
                    // (0x140FF4A43 / 0x140FF4A83 / 0x140FF5B54 / 0x140FF5E83),Dalamud 取位址最小的第一命中。
                    // 四個命中的 lea 全部指向同一個靜態位址 0x142931C90,所以多重命中目前不影響結果;
                    // 且該位址與 FFXIVClientStructs 的 UIState.Instance() 靜態位址逐字相同(獨立交叉驗證)。
                    // 四個命中也都緊接著 call 0x140A4C510 —— 正是上面 IsCardOwnedPtr 解出的位址。
                    // 這個值會被當成 uiState 傳進 IsCardOwned / IsNpcBeaten 兩支裸函式指標。
                    // 若日後改版讓其中某個命中改指別的全域,第一命中會靜默改綁 —— 屆時要把這條收斂成唯一命中。
                    UIStatePtr = Service.sigScanner.GetStaticAddressFromSig("48 8d 0d ?? ?? ?? ?? e8 ?? ?? ?? ?? 84 c0 74 0f 8b cb");
                }
                catch (Exception ex)
                {
                    Service.logger.Error(ex, "oh noes!");
                }
            }

            HasErrors = (IsNpcBeatenPtr == IntPtr.Zero) || (IsCardOwnedPtr == IntPtr.Zero) || (UIStatePtr == IntPtr.Zero);
            if (!HasErrors)
            {
                IsCardOwnedFunc = Marshal.GetDelegateForFunctionPointer<IsCardOwnedDelegate>(IsCardOwnedPtr);
                IsNpcBeatenFunc = Marshal.GetDelegateForFunctionPointer<IsNpcBeatenDelegate>(IsNpcBeatenPtr);
            }
            else
            {
                Service.logger.Error("Failed to find triad card functions, turning reader off");
            }
        }

        public bool IsCardOwned(int cardId)
        {
            if (HasErrors || cardId <= 0 || cardId > 65535)
            {
                return false;
            }

            return (IsCardOwnedFunc != null) && IsCardOwnedFunc(UIStatePtr, (ushort)cardId) != 0;
        }

        public bool IsNpcBeaten(int npcId)
        {
            if (HasErrors || npcId < 0x230002)
            {
                return false;
            }

            return (IsNpcBeatenFunc != null) && IsNpcBeatenFunc(UIStatePtr, npcId) != 0;
        }

        /*public void TestBeatenNpcs()
        {
            // fixed addr from 5.58
            IntPtr memAddr = UIStatePtr + 0x15d18;

            byte[] flags = Dalamud.Memory.MemoryHelper.ReadRaw(memAddr, 0x70 / 8);
            flags[10 / 8] |= 1 << (10 % 8);
            flags[11 / 8] |= 1 << (11 % 8);
            flags[12 / 8] |= 1 << (12 % 8);

            Dalamud.Memory.MemoryHelper.WriteRaw(memAddr, flags);
        }*/

        /*public void TestOwnedCardBits()
        {
            // fixed addr from 5.58
            IntPtr memAddr = UIStatePtr + 0x15ce5;

            byte[] flags = Dalamud.Memory.MemoryHelper.ReadRaw(memAddr, 0x29);
            flags[70 / 8] |= 1 << (70 % 8);
            flags[71 / 8] |= 1 << (71 % 8);
            flags[72 / 8] |= 1 << (72 % 8);

            Dalamud.Memory.MemoryHelper.WriteRaw(memAddr, flags);
        }*/
    }
}
