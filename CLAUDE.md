# Shuibb

Shuibb 是 MapleStory WZ 編輯器（前身 TOKI改 / HaRepacker fork）。
**這個資料夾（Shuibb_SourceBaseline）是唯一主要開發 Source。**

## 專案地圖

| 專案 | 說明 |
|---|---|
| `HaRepacker/Harepacker-resurrected.csproj` | 主程式（輸出 `WvsWzImg.exe/.dll`） |
| `MapleLib/MapleLib/` | WZ 核心（解析、加密、儲存）；測試在 `MapleLib/MapleLib.Tests/` |
| `HaSharedLibrary/` | 共用 UI / 主題（`GUI/Themes/HarepackerTheme.xaml`） |
| `SkillPreview/` | 衛星面板：技能預覽、技能數值編輯器、右側節點編輯器、EditorTheme |
| `TokiAiAssistant/` | AI 助手外掛（主程式用反射載入 `TokiAiAssistant.dll`，可缺席） |
| `RealESRGAN_AI_Upscale/` | AI 放大外掛 |
| `tests/` | Regression harness（見下） |
| `tools/iltransplant/` | **歷史工具**，正常開發禁用（見 tools/iltransplant/README.md） |
| `HaCreator/`, `WzImg-MCP-Server/`, `UnitTest_*` | 上游附帶，不在 Shuibb.slnx，不參與建置 |

## 建置

```
dotnet build Shuibb.slnx -c Release
```

主程式輸出：`HaRepacker/bin/Release/net10.0-windows/`（完整可執行）。

## 測試

| Harness | 涵蓋 | 資料來源 |
|---|---|---|
| `tests/svtest` | 技能數值編輯器全功能（40 檢查） | `tests/data/skillvalue_sandbox`（不在 git，929MB，遺失時從 `D:\3.私服檔案\技術谷4.0` 重建 Data\Skill + Data\Lang） |
| `tests/switchtest` | 技能切換 / ID 正確（10） | 同上 |
| `tests/derivetest` | 說明範本推導、level 區間、批次（57） | 同上 |
| `tests/nodetest` | 右側節點編輯器 + String.wz 對應 + 主題切換（26） | `D:\3.私服檔案\技術谷4.0`（唯讀） |
| `tests/opentwice` | 重複開 WZ 不崩潰、不重複載入（9） | 同上 |
| `tests/typeahead` | Type-ahead 跳轉 + 虛擬化樹捲動（12，需帶參數，見其原始碼） | 同上 |
| `MapleLib.Tests` | 核心 349 單元測試（含存檔 round-trip） | 自帶 |

跑法：`dotnet build` 後直接執行 harness 的 exe，結束碼 0 = 全過，log 在 exe 旁的 `<名稱>.txt`。
Harness 全部用 ProjectReference —— 跑的一定是本 baseline 建出的 DLL，不需要再查 hash。

## 部署

把 `HaRepacker/bin/Release/net10.0-windows/` 中**有變動的 DLL** 覆蓋到正式資料夾
（目前為 `C:\Users\66\Desktop\修改工具\TOKI改_技能數值版`），外掛（TokiAiAssistant.dll、
RealESRGAN_AI_Upscale.dll）只在其專案有改動時才需要覆蓋。覆蓋前確認 WvsWzImg.exe 沒在執行。

## Shuibb Development Rules

1. `Shuibb_SourceBaseline` 是唯一主要 Source。
2. 所有新 Feature / Bug Fix 直接修改 Source。
3. 正常開發禁止直接 patch deployed DLL。
4. 正常開發禁止使用 IL transplant。
5. 使用 Git 管理每一輪修改。
6. 優先讀取與目前需求直接相關的檔案。
7. 不要每次重新掃描整個 Repository。
8. 修改後優先看本次 `git diff`。
9. 小修改只跑 targeted tests。
10. 一般 bug 只跑 related regression。
11. 只有核心 parser / serializer / save-load 修改才跑 full regression。
12. 正常修改禁止 whole-assembly decompile diff。
13. 不做未要求的 refactor。
14. 不順手修改附近無關程式。
15. 優先最小修改。
16. 已有 Regression 可以證明的事情不要重做測試工具。
17. 需求完成、測試成功後就停止。
18. 發現其他問題可以記錄，但除非阻止目前任務，否則不要順便修。
19. 不為了「更完整」自行擴大 scope。
20. Token 與驗證成本優先花在真正有風險的區域。
