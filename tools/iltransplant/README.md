# iltransplant（歷史工具 / 緊急維修工具）

dnlib 外科式 IL 移植工具。Shuibb baseline 之前，正式版 DLL 是閉源二進位，
所有修改都靠它把新編譯的 method body 移植進 deployed DLL。

**自 shuibb-baseline-v1 起，所有功能已存在 Source，正常開發流程禁止使用本工具。**
只在「必須直接修補某顆無法重建的二進位」的緊急情境才拿出來，
且事後必須把同樣的修改補回 Source。

用法：`iltransplant <target-original.dll> <source-with-new-code.dll> <output.dll>`
（spec 寫死在 Program.cs 內，依 target 檔名自動選 WvsWzImg / MapleLib spec 組。）
