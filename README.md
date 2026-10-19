# Triad Buddy

[MgAl2O4/FFTriadBuddyDalamud](https://github.com/MgAl2O4/FFTriadBuddyDalamud) 的台服移植版。
九宮幻卡求解輔助工具，以 Dalamud 插件形式運作，僅支援 **NPC 對戰**。

## 功能特色

1. **標示下一步最佳落子**（開放 PvP 對戰時不提供）  
![Game overlay](/assets/image1.png)  
2. **賽前依照對手 NPC 自動最佳化卡組**  
![Deck optimizer](/assets/image2.png)  
3. **更方便地找出尚未收集的卡片**  
![Collection details](/assets/image3.png)  
4. **「選拔」規則挑卡時，依目前規則與候選卡片提供建議卡組**

其他細節：
- 求解器會即時讀取遊戲內的九宮格盤面、雙方手牌與規則，計算出勝率最高（或扣分最少）的落子建議。
- 卡組最佳化器可針對特定 NPC 對手，從你擁有的卡片中挑出最適合的 5 張卡組。
- 卡片搜尋 / 收集狀態視窗可依稀有度、屬性、取得方式等篩選，快速找出還缺哪些卡片、可以從哪個 NPC 取得。
- 指令 `/triadbuddy`：打開狀態視窗。

獨立版工具（非 Dalamud 插件）：https://github.com/MgAl2O4/FFTriadBuddy

## 台服版的修改

- 新增「選拔」規則的建議卡組（讀取挑卡畫面的候選卡片與生效規則）
- 「全明牌」「混亂」規則下紅藍方自動判斷不可靠時，狀態視窗提供「交換紅藍方判斷」按鈕手動切換
- 對局提示或卡組最佳化計算出錯時不會讓遊戲關閉，改成收掉提示並在狀態視窗顯示紅字「求解失敗」
- 介面繁體中文化（`tw` 語系，術語依台服官方用語：九宮幻卡、卡組）

## 安裝

在 Dalamud 設定的「自訂插件庫」加入
`https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json`
並啟用，再從插件列表安裝 TriadBuddy。

## PvP 支援說明

求解器的核心邏輯建立在「已知的 NPC 固定卡組」之上。開放 PvP 對戰時，對手可使用的卡片範圍幾乎不受限，導致所有落子預測都失去意義、形同亂猜。

沒錯，你可以自行複製這個專案並關掉 PvP 檢查——授權條款允許你自由修改程式碼。但求解器並不會因此就懂得應付未知的卡片組合，把牌隨便放在盤面上得到的結果可能也差不多。如果你真的想要做出可靠的 PvP 支援，請有心理準備——那幾乎等於要把整個求解器重寫一遍。你已經被警告了 :)

**「選拔」規則挑卡**則是不同的情境：在挑卡階段，候選卡片與目前的規則都是已知的資訊，因此插件可以讀取候選卡片與生效規則，據此提供建議卡組（見上方功能 4）。這個功能不會、也無法預測對手在對戰中的實際落子。

## 翻譯 / 多語系

上游的翻譯在這裡協助：https://crowdin.com/project/fftriadbuddy 。專案中的翻譯檔案：
* `plugin.json`：本插件使用的多語系檔案
* `strings.resx`：獨立版工具使用的多語系檔案

插件目前支援的語系代碼：`de`（德文）、`en`（英文）、`es`（西班牙文）、`fr`（法文）、`ja`（日文）、`ko`（韓文）、`zh`（中文）、`tw`（繁體中文）。

新增語系的方式：
1. 在 `assets/loc/<語系代碼>.json` 新增對應的翻譯檔（格式與 `en.json` 相同的 key/message 結構，`en.json` 為翻譯對照的基準版本）。
2. 在 `TriadBuddy.csproj` 中加入 `<None Remove="assets\loc\<語系代碼>.json" />`。
3. 在 `plugin/Plugin.cs` 的 `supportedLangCodes` 陣列中加入該語系代碼。

原作者：[MgAl2O4](https://github.com/MgAl2O4)
