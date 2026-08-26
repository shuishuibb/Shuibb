# Shuibb

MapleStory WZ 編輯器。上游為 [HaRepacker-resurrected](https://github.com/lastbattle/Harepacker-resurrected)（HaSuite）。

自 `shuibb-baseline-v1` 起，所有正式功能（含歷年 IL transplant 移植的修改、
TokiAiAssistant、SkillPreview、MapleLib 修正）皆完整存在本 Source，
正常 Release Build 直接產生等同正式版功能的成品，不再依賴 IL transplant
或任何 deployed binary。

- 建置：`dotnet build Shuibb.slnx -c Release`
- 開發規則 / 專案地圖 / 測試 / 部署：見 `CLAUDE.md`
- `tools/iltransplant`：僅為歷史 / 緊急維修工具，正常流程禁用
