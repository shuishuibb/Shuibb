using System;
using System.Linq;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace iltransplant
{
    /// <summary>
    /// Re-maps any TypeDef/FieldDef/MethodDef that belongs to the SOURCE module back onto the
    /// equivalent (same full name / same declaring type + member name) definition that already
    /// exists in the TARGET module, instead of letting dnlib's default Importer create a brand
    /// new cross-assembly reference to the source module. This is what makes it safe to clone a
    /// method body from a freshly-built assembly into a differently-built (hand-patched) copy of
    /// "the same" assembly: sibling members the cloned code calls (fields, helper methods, other
    /// types defined in the same project) resolve back to the target's own definitions.
    /// </summary>
    class LocalRedirectMapper : ImportMapper
    {
        readonly ModuleDef sourceModule;
        readonly ModuleDef targetModule;

        public LocalRedirectMapper(ModuleDef sourceModule, ModuleDef targetModule)
        {
            this.sourceModule = sourceModule;
            this.targetModule = targetModule;
        }

        public override ITypeDefOrRef Map(ITypeDefOrRef source)
        {
            if (source is TypeDef td && td.Module == sourceModule)
                return FindTargetType(td);
            return null;
        }

        public override IField Map(FieldDef source)
        {
            if (source.Module != sourceModule) return null;
            var targetType = FindTargetType(source.DeclaringType);
            var found = targetType.Fields.FirstOrDefault(f => f.Name == source.Name);
            if (found == null)
                throw new InvalidOperationException($"Local field not found in target: {source.DeclaringType.FullName}.{source.Name}");
            return found;
        }

        public override IMethod Map(MethodDef source)
        {
            if (source.Module != sourceModule) return null;
            var targetType = FindTargetType(source.DeclaringType);
            var candidates = targetType.Methods.Where(m => m.Name == source.Name && m.Parameters.Count == source.Parameters.Count).ToList();
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count > 1)
            {
                foreach (var c in candidates)
                {
                    bool match = true;
                    for (int i = 0; i < source.Parameters.Count; i++)
                        if (c.Parameters[i].Type.FullName != source.Parameters[i].Type.FullName) { match = false; break; }
                    if (match) return c;
                }
                throw new InvalidOperationException($"Ambiguous local method mapping: {source.DeclaringType.FullName}.{source.Name}");
            }
            throw new InvalidOperationException($"Local method not found in target: {source.DeclaringType.FullName}.{source.Name}");
        }

        TypeDef FindTargetType(TypeDef sourceType)
        {
            var found = targetModule.Find(sourceType.FullName, false);
            if (found != null)
                return found;

            // Compiler-generated closure types (<>c, <>c__DisplayClassN_M) are numbered by
            // their ordinal position among ALL synthetic types in the file. Inserting new code
            // earlier in the file shifts that ordinal for everything declared after it, so an
            // unrelated, untouched closure can legitimately have a different N between the two
            // builds even though its shape (captured fields, method count) is identical. Fall
            // back to matching by shape within the same declaring type.
            if (sourceType.DeclaringType != null && IsCompilerGeneratedClosure(sourceType.Name))
            {
                var targetDeclaringType = FindTargetType(sourceType.DeclaringType);
                var shapePattern = ClosureShapePattern(sourceType.Name);
                var candidates = targetDeclaringType.NestedTypes
                    .Where(t => IsCompilerGeneratedClosure(t.Name) && ClosureShapePattern(t.Name) == shapePattern)
                    .ToList();
                if (candidates.Count == 1)
                    return candidates[0];
                if (candidates.Count > 1)
                {
                    // Disambiguate by structural shape: same field count/types and method count.
                    var bySignature = candidates.Where(c =>
                        c.Fields.Count == sourceType.Fields.Count &&
                        c.Methods.Count == sourceType.Methods.Count &&
                        c.Fields.Select(f => f.FieldType.FullName).SequenceEqual(sourceType.Fields.Select(f => f.FieldType.FullName))
                    ).ToList();
                    if (bySignature.Count == 1)
                        return bySignature[0];
                    throw new InvalidOperationException($"Ambiguous closure type mapping: {sourceType.FullName} ({candidates.Count} same-shape candidates in target)");
                }
            }

            throw new InvalidOperationException($"Local declaring type not found in target module: {sourceType.FullName}");
        }

        static bool IsCompilerGeneratedClosure(string name) => name.StartsWith("<>c");

        // "<>c__DisplayClass45_0" -> "__DisplayClass_0" (ordinal N removed, trailing M kept -
        // M resets per containing method, so it stays meaningful even when N drifts).
        static string ClosureShapePattern(string name)
        {
            int lastUnderscore = name.LastIndexOf('_');
            return lastUnderscore < 0 ? name : System.Text.RegularExpressions.Regex.Replace(name.Substring(0, lastUnderscore), "[0-9]+", "") + name.Substring(lastUnderscore);
        }
    }

    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: iltransplant <target-original.dll> <source-with-new-code.dll> <output.dll>");
                return 2;
            }
            string targetPath = args[0];
            string sourcePath = args[1];
            string outputPath = args[2];

            // Skill range / effect preview. The panel itself lives in the separate
            // SkillPreview.dll and is parked into grid1 from code, so MainPanel.xaml (and the
            // compiled BAML in the target) stays untouched - all the host needs is the field,
            // the show/hide helper, and the ShowObjectValue call site that drives it.
            var specsHost = new[]
            {
                new TypePatchSpec(
                    "HaRepacker.GUI.Panels.MainPanel",
                    // Neither of these has a field initialiser, so .ctor is untouched.
                    NewFieldNames: new[] { "skillPreviewPanel", "nodeEditorPanel",
                                           "nativeTreeVirtualizationApplied",
                                           // Where the clipboard contents were copied from, so a
                                           // paste can land in the matching container.
                                           "clipboardParentName",
                                           "nativeChangedNodeBrush",
                                           // Lazily created inside PopulateNativeTreeItem.
                                           "pendingNativeFills" },
                    // The "Batch*" block is the node / string / folder-image batch toolset. It adds
                    // no fields: every bit of cross-call state travels through ref/out parameters,
                    // so MainPanel's .ctor does not have to be replaced. Its dialogs are built from
                    // code (no designer, no .resx, no BAML) so the target's resource streams stay
                    // byte-identical, and it contains no lambda, iterator or LINQ closure, so the
                    // build emits no new compiler-generated type for the mapper to match.
                    NewMethodNames: new[] { "ShowSkillPreviewIfApplicable",
                                            "ResizeImagesByPercentToBgra32", "ResizeImagesByPercentCore",
                                            "BatchPrompt", "BatchConfirm", "BatchInfo",
                                            "GetSelectedBatchNodes", "BatchEnsureParsed",
                                            "BatchSetValuesByNodeName", "BatchSetValueRecursive",
                                            "BatchFormatNumber", "BatchApplyScalarValue",
                                            "BatchDeleteNodesByName", "BatchCollectNodesByName",
                                            "BatchOffsetNodeNames", "BatchShiftNumericName",
                                            "BatchFindRenameCollision", "BatchSortRenameOrder", "BatchNameOrderIsAfter",
                                            "BatchReplaceText", "BatchReplaceOrDeleteText",
                                            "BatchReplaceTextCore", "BatchReplaceRecursive",
                                            "BatchCleanupStringWz", "BatchGetStringWzFiles",
                                            "BatchResolveInStringWz", "BatchGetChildObject",
                                            "BatchCollectItemIds", "BatchScanStringDirectory",
                                            "BatchScanStringContainer", "BatchIsScannedStringImage",
                                            "BatchGetStringCategory",
                                            "BatchCoverFolderImages", "BatchImportFolderImages", "BatchFolderImages",
                                            "BatchIsSupportedImageFile", "BatchStripExtension", "BatchTrimLeadingSeparators", "BatchLoadBitmap",
                                            "BatchApplyFolderImage", "BatchFindNodeByPath",
                                            "BatchSetCanvasBitmap", "BatchCreateCanvasNode",
                                            // Replacing a canvas's artwork used to let
                                            // WzPngProperty.PNG re-detect the surface format, so a
                                            // BGRA4444 icon could be rewritten as ARGB1555 - fine
                                            // in the editor, garbage in the game.
                                            "SetCanvasBitmapPreservingFormat",
                                            "EnableNativeTreeVirtualization",
                                            // Paste into every selected node + show pasted/edited
                                            // nodes in red in the WPF mirror.
                                            "PasteIntoNode", "ApplyNativeNodeForeground", "RedirectPasteTarget",
                                            // Chunked WPF population: a TreeViewItem costs ~60us to
                                            // build, so a 3,096-child node (String.wz/Eqp.img/Eqp/Cap)
                                            // froze the UI for ~440ms. Only the first 200 are built
                                            // synchronously now; the rest fill in at Background
                                            // priority, and anything needing the whole list calls
                                            // FlushPendingNativeTreeItems first.
                                            "AppendNativeTreeItems", "FillPendingNativeTreeItems",
                                            "FlushPendingNativeTreeItems",
                                            // Realize a virtualized container before scrolling to
                                            // it, so "jump to this node" actually moves the tree.
                                            "BringNativeNodeIntoView", "FindNativeItemsHost",
                                            // Inline field editor for ordinary entities (item /
                                            // mob / npc codes), parked into grid1 like the skill
                                            // preview and driven from ShowObjectValue.
                                            "ShowNodeEditorIfApplicable" },
                    // ConvertCanvasToBgra32 no longer re-applies the list.wz XOR mask, which
                    // was using a hardcoded GMS key and corrupted files on other encryptions.
                    // ResizeImageNodeRecursively / ResizeCanvasByScale gained a forceBgra32
                    // parameter, so their signatures are replaced along with their bodies.
                    // Tree-performance round. MainPanel keeps its TreeViewMS as a data model only
                    // (the visible tree is the WPF one), and EnsureVisible() was forcing that
                    // invisible control to create a native handle - after which every node insert
                    // crosses into the native control and reparsing String.wz/Skill.img (42k nodes)
                    // takes ~13s instead of ~40ms. UpdateNativeSelectionVisuals now repaints only
                    // what changed instead of sweeping every TreeViewItem ever created (~36ms per
                    // click once a 10k-child node is open), and CreateNativeTreeItem /
                    // DataTreeViewItem_PreviewMouseLeftButtonDown use a plain string as the
                    // lazy-load placeholder instead of a second TreeViewItem per node.
                    ReplacedMethodNames: new[] { "ShowObjectValue", "ConvertCanvasToBgra32",
                                                 "ResizeImagesByPercent", "ResizeImageNodeRecursively",
                                                 "ResizeCanvasByScale",
                                                 "CreateNativeTreeItem",
                                                 "DataTreeViewItem_PreviewMouseLeftButtonDown",
                                                 "button_nextSearch_Click",
                                                 // DoPaste now walks DataTree.SelectedNodes instead
                                                 // of just SelectedNode; UpdateNativeSelectionVisuals
                                                 // re-applies the red instead of clearing Foreground.
                                                 "DoPaste", "DoCopy", "UpdateNativeSelectionVisuals",
                                                 "PopulateNativeTreeItem", "SelectAndRevealNativeNode",
                                                 "GetVisibleNativeNodes",
                                                 // Now routes through SetCanvasBitmapPreservingFormat
                                                 // so a hand-swapped icon keeps its surface format.
                                                 "ChangeCanvasPropBoxImage",
                                                 // Both "jump to a node" paths now go through
                                                 // BringNativeNodeIntoView. BringIntoView alone is a
                                                 // no-op on a virtualized TreeViewItem - it has no
                                                 // visual parent to scroll toward - so typing a node
                                                 // name moved the selection while the tree stayed
                                                 // put. Type-ahead already exists in the baseline,
                                                 // hence replaced rather than added.
                                                 "JumpToTypeAheadMatch" }),
                new TypePatchSpec(
                    "HaRepacker.GUI.MainForm",
                    NewFieldNames: Array.Empty<string>(),
                    // "AI 助手" in the Tools menu. The assistant is a separate assembly reached by
                    // reflection, so none of this needs a reference the transplanter would have to
                    // map; MainForm_Load was empty in both builds, which makes it a free hook.
                    NewMethodNames: new[] { "AddAiAssistantMenuItem", "GetAiAssistantAssemblyPath",
                                            "AiAssistantMenuItem_Click", "ShowAiAssistant",
                                            // Shared with ContextMenuManager's "ask about this
                                            // node" entry, so the reflection contract with
                                            // TokiAiAssistant.dll exists in exactly one place.
                                            "ShowAiAssistantWindow" },
                    // The menu's Paste never refreshed the WPF mirror, so a paste from the menu
                    // left the visible tree showing the pre-paste state.
                    // SortNodesRecursively: assigning TreeViewNodeSorter left the TreeView in Sorted
                    // mode, so with the "Sort" option on EVERY later Nodes.Add became a sorted
                    // insert - one big WZ file (Map002.wz, 13,978 nodes) took ~840ms to attach
                    // instead of ~10ms. It now sorts explicitly and hands the tree back unsorted.
                    ReplacedMethodNames: new[] { "PasteToolStripMenuItem_Click", "SortNodesRecursively",
                                                 "MainForm_Load" }),
                new TypePatchSpec(
                    "HaRepacker.WzNode",
                    NewFieldNames: Array.Empty<string>(),
                    NewMethodNames: new[] { "EnsureVisibleIfDisplayed", "SortChildNodes", "SortNodeCollection", "IsNodeCollectionSorted" },
                    // Reparse now applies the "Sort" order itself, since the tree is no longer left
                    // in Sorted mode for it to inherit.
                    ReplacedMethodNames: new[] { "AddObject", "Reparse" }),
                new TypePatchSpec(
                    "HaRepacker.ContextMenuManager",
                    NewFieldNames: new[] { "ResizeImageToBgra32", "AskAiAboutNode",
                                           "AddNodeBatchMenu", "BatchSetValues", "BatchOffsetNumber",
                                           "BatchReplaceText", "BatchReplaceOrDelete", "BatchDeleteNodes",
                                           "BatchCleanupString", "BatchCoverFolderImages", "BatchImportFolderImages" },
                    NewMethodNames: new[] { "ResizeImageToBgra32_Click",
                                            "BatchSetValuesByNodeName_Click", "BatchOffsetNodeNames_Click",
                                            "BatchReplaceText_Click", "BatchReplaceOrDeleteText_Click",
                                            "BatchDeleteNodesByName_Click", "BatchCleanupStringWz_Click",
                                            "BatchCoverFolderImages_Click", "BatchImportFolderImages_Click",
                                            "AskAiAboutNode_Click", "BuildAiNodePath" },
                    // CreateMenu is replaced as well this time: the new "批次節點工具" submenu has to
                    // be appended to the item list it builds.
                    ReplacedMethodNames: new[] { ".ctor", "CreateMenu", "CreateNewImgFileInDirectory" }),
            };
            // MapleLib fixes:
            //  - LoadCanvasSection: canvas files numbered 100+ were never loaded.
            //  - WzCanvasProperty: an _outlink was only ever looked up inside the canvas's own
            //    WzFile, so clients that split one logical tree across several files (or keep
            //    artwork in _Canvas files) resolved to a 1x1 placeholder.
            var specsMapleLib = new[]
            {
                new TypePatchSpec(
                    "MapleLib.WzFileManager",
                    NewFieldNames: Array.Empty<string>(),
                    NewMethodNames: Array.Empty<string>(),
                    // LoadWzFile(path, encVersion) now reports an already-loaded file as null - the
                    // same way it already reports an unresolvable path - instead of parsing a second
                    // copy and throwing. The throw escaped the callers' Parallel.ForEach as an
                    // AggregateException, out of their async void handler, and killed the app with an
                    // unhandled-exception dialog whenever a .wz was opened twice. Overload-qualified
                    // because the (string, WzFile) overload must keep throwing - its duplicate check
                    // is the invariant MapleLib's own tests assert on.
                    ReplacedMethodNames: new[] { "LoadCanvasSection",
                                                 "LoadWzFile(System.String,MapleLib.WzLib.WzMapleVersion)" }),
                new TypePatchSpec(
                    "MapleLib.WzLib.WzProperties.WzPngProperty",
                    NewFieldNames: Array.Empty<string>(),
                    // CompressPng now keeps the format the canvas already has instead of
                    // re-detecting one, which silently rewrote BGRA4444 icons as ARGB1555.
                    NewMethodNames: new[] { "TryGetSurfaceFormat" },
                    ReplacedMethodNames: new[] { "CompressPng" }),
                new TypePatchSpec(
                    "MapleLib.WzLib.WzProperties.WzCanvasProperty",
                    NewFieldNames: new[] { "_scannedLinkDirectories" },
                    NewMethodNames: new[] { "ResolveLinkedImagePropertyAcrossLoadedFiles", "SearchLoadedFilesForLink",
                                            "TryLoadLinkedFilesFromDisk", "CollectImagesNamed" },

                    ReplacedMethodNames: new[] { "GetLinkedWzImageProperty" }),
                new TypePatchSpec(
                    "MapleLib.Converters.ImageConverter",
                    NewFieldNames: Array.Empty<string>(),
                    NewMethodNames: Array.Empty<string>(),
                    // ToWpfBitmap encoded the bitmap to PNG and decoded it straight back just to
                    // change type - ~19ms for a 452x360 frame, which was almost the whole cost of
                    // arrow-keying through skill effect frames. Now copies the pixels directly.
                    ReplacedMethodNames: new[] { "ToWpfBitmap" }),
            };
            // Pick the spec set from the assembly being patched, rather than a variable that
            // has to be edited by hand before each run - flipping it the wrong way silently
            // produces a DLL missing half its patches.
            string targetName = System.IO.Path.GetFileNameWithoutExtension(targetPath);
            TypePatchSpec[] specs;
            if (targetName.Equals("MapleLib", StringComparison.OrdinalIgnoreCase))
                specs = specsMapleLib;
            else if (targetName.StartsWith("WvsWzImg", StringComparison.OrdinalIgnoreCase))
                specs = specsHost;
            else
            {
                Console.Error.WriteLine($"No spec set defined for target assembly '{targetName}'.");
                return 2;
            }
            Console.WriteLine($"Target '{targetName}': applying {specs.Length} type spec(s).");
            var specsUnused = new[]
            {
                new TypePatchSpec(
                    "MapleLib.WzFileManager",
                    NewFieldNames: Array.Empty<string>(),
                    NewMethodNames: Array.Empty<string>(),
                    ReplacedMethodNames: new[] { "GetIniWzIndexInfo", "UnloadWzFile" }),
                new TypePatchSpec(
                    "HaRepacker.GUI.Panels.MainPanel",
                    NewFieldNames: new[] { "typeAheadBuffer", "typeAheadLastKeyTimeUtc" },
                    NewMethodNames: new[] {
                        "TryGetTypeAheadChar",
                        "ConvertImagesToBgra32", "ConvertImageNodeRecursively_toBgra32",
                        "ConvertCanvasToBgra32", "DeflateCompressWithZlibHeader", "ApplyListWzXorMask",
                        "ResizeImagesByPercent", "ResizeImageNodeRecursively", "ResizeCanvasByScale"
                    },
                    // JumpToTypeAheadMatch already exists in the target (from the earlier
                    // type-ahead patch) but its BODY changed (multi-digit prefix fix) - it needs
                    // to be replaced, not skipped as "already exists" the way NewMethodNames would.
                    ReplacedMethodNames: new[] { "DataTreeView_PreviewKeyDown", "JumpToTypeAheadMatch" }),
                new TypePatchSpec(
                    "HaRepacker.ContextMenuManager",
                    NewFieldNames: new[] { "ConvertToBgra32", "ResizeImage", "getMainPanel" },
                    NewMethodNames: new[] {
                        "ConvertToBgra32_Click", "ResizeImage_Click",
                        "SaveFile_Click", "SaveImg_Click", "CreateNewImgFile_Click", "DeleteImgFile_Click",
                        "CollapseAllChildNode_Click", "ExpandAllChildNode_Click",
                        "Rename_Click", "Remove_Click", "Unload_Click", "Reload_Click",
                        "SortChildNodesView_Click", "SortPropertiesByName_Click",
                        "AddImage_Click", "AddDirectory_Click", "AddByteFloat_Click", "AddCanvas_Click",
                        "AddLong_Click", "AddInt_Click", "AddConvex_Click", "AddDouble_Click", "AddNull_Click",
                        "AddSound_Click", "AddString_Click", "AddSub_Click", "AddUshort_Click", "AddUOL_Click",
                        "AddVector_Click", "Animate_Click", "SaveAnimation_Click", "FixInlink_Click",
                        "AiUpscaleQualityOnly_Click", "AiUpscale1_5x_Click", "AiUpscale2x_Click", "AiUpscale4x_Click"
                    },
                    ReplacedMethodNames: new[] { ".ctor", "CreateMenu" }),
                new TypePatchSpec(
                    "HaRepacker.GUI.MainForm",
                    NewFieldNames: Array.Empty<string>(),
                    NewMethodNames: new[] { "<.ctor>b__3_1" },
                    ReplacedMethodNames: new[] { ".ctor" }),
            };

            var targetModule = ModuleDefMD.Load(targetPath);
            var sourceModule = ModuleDefMD.Load(sourcePath);
            var mapper = new LocalRedirectMapper(sourceModule, targetModule);
            var importer = new Importer(targetModule, ImporterOptions.TryToUseTypeDefs, new GenericParamContext(), mapper);

            // Resolve source/target TypeDefs for every spec up front.
            var resolved = new List<(TypePatchSpec Spec, TypeDef SourceType, TypeDef TargetType)>();
            foreach (var spec in specs)
            {
                var sourceType = sourceModule.Find(spec.TypeName, false);
                var targetType = targetModule.Find(spec.TypeName, false);
                if (sourceType == null) { Console.Error.WriteLine($"Type not found in source: {spec.TypeName}"); return 1; }
                if (targetType == null) { Console.Error.WriteLine($"Type not found in target: {spec.TypeName}"); return 1; }
                resolved.Add((spec, sourceType, targetType));
            }

            // Phase 1: add every new field, across all specs, before any body is cloned - new
            // methods below may reference fields belonging to a *different* spec in this list.
            foreach (var (spec, sourceType, targetType) in resolved)
            {
                foreach (var fname in spec.NewFieldNames)
                {
                    if (targetType.Fields.Any(f => f.Name == fname))
                    {
                        Console.WriteLine($"[skip] field already exists: {spec.TypeName}.{fname}");
                        continue;
                    }
                    var srcField = sourceType.Fields.Single(f => f.Name == fname);
                    var sig = importer.Import(srcField.FieldSig);
                    var newField = new FieldDefUser(srcField.Name, sig, srcField.Attributes);
                    targetType.Fields.Add(newField);
                    Console.WriteLine($"[add] field {spec.TypeName}.{fname} : {sig}");
                }
            }

            // Phase 2: create every new method as an empty stub (signature only, no body yet) so
            // that phase 3 can freely resolve calls between new methods regardless of which one
            // happens to come first in NewMethodNames or which spec it belongs to.
            var newMethodStubs = new List<(MethodDef SourceMethod, MethodDef TargetMethod)>();
            foreach (var (spec, sourceType, targetType) in resolved)
            {
                foreach (var mname in spec.NewMethodNames)
                {
                    if (targetType.Methods.Any(m => MethodMatches(m, mname)))
                    {
                        Console.WriteLine($"[skip] method already exists: {spec.TypeName}.{mname}");
                        continue;
                    }
                    var srcMethod = PickMethod(sourceType, mname, "source");
                    var sig = (MethodSig)importer.Import(srcMethod.MethodSig);
                    var newMethod = new MethodDefUser(srcMethod.Name, sig, srcMethod.ImplAttributes, srcMethod.Attributes);
                    targetType.Methods.Add(newMethod);
                    newMethodStubs.Add((srcMethod, newMethod));
                    Console.WriteLine($"[add stub] {spec.TypeName}.{mname}");
                }
            }

            // Phase 2b: bring every REPLACED method's signature up to date before any body is
            // cloned. A replaced method's parameter list can legitimately change (e.g. a helper
            // gaining a flag), and new methods cloned in phase 3 may call it - the call would
            // otherwise fail to bind against the target's stale signature.
            var replacedTargets = new List<(TypePatchSpec Spec, MethodDef SourceMethod, MethodDef TargetMethod)>();
            foreach (var (spec, sourceType, targetType) in resolved)
            {
                foreach (var mname in spec.ReplacedMethodNames)
                {
                    var srcMethod = PickMethod(sourceType, mname, "source");
                    var tgtMethod = PickMethod(targetType, mname, "target");
                    tgtMethod.MethodSig = (MethodSig)importer.Import(srcMethod.MethodSig);
                    tgtMethod.Parameters.UpdateParameterTypes();
                    replacedTargets.Add((spec, srcMethod, tgtMethod));
                    Console.WriteLine($"[sync signature] {spec.TypeName}.{mname}");
                }
            }

            // Phase 3: now that every new field and every new method signature exists in the
            // target module, clone the actual CIL bodies.
            foreach (var (srcMethod, newMethod) in newMethodStubs)
            {
                CloneBody(srcMethod, newMethod, importer);
                Console.WriteLine($"[clone body] {newMethod.DeclaringType.FullName}.{newMethod.Name}");
            }

            // Phase 4: clone the replaced methods' bodies. Runs last so they can call anything
            // added in phases 1-3; their signatures were already synced in phase 2b.
            foreach (var (spec, srcMethod, tgtMethod) in replacedTargets)
            {
                CloneBody(srcMethod, tgtMethod, importer);
                Console.WriteLine($"[replace body] {spec.TypeName}.{tgtMethod.Name}");
            }

            targetModule.Write(outputPath);
            Console.WriteLine($"Wrote: {outputPath}");
            return 0;
        }

        record TypePatchSpec(string TypeName, string[] NewFieldNames, string[] NewMethodNames, string[] ReplacedMethodNames);

        // A spec entry is normally just a method name ("Reparse"). When a type has several
        // overloads of that name, Single() would throw, so the entry may instead be qualified
        // with a parameter type list - "LoadWzFile(System.String,MapleLib.WzLib.WzMapleVersion)".
        static string MethodNameOf(string spec)
        {
            int paren = spec.IndexOf('(');
            return paren < 0 ? spec : spec.Substring(0, paren);
        }

        static string[] ParamTypesOf(string spec)
        {
            int paren = spec.IndexOf('(');
            if (paren < 0) return null; // unqualified - match on name alone
            string inner = spec.Substring(paren + 1).TrimEnd(')').Trim();
            if (inner.Length == 0) return Array.Empty<string>();
            return inner.Split(',').Select(s => s.Trim()).ToArray();
        }

        static bool MethodMatches(MethodDef m, string spec)
        {
            if (m.Name != MethodNameOf(spec)) return false;
            var want = ParamTypesOf(spec);
            if (want == null) return true;
            var actual = m.Parameters.Where(p => !p.IsHiddenThisParameter)
                                     .Select(p => p.Type.FullName).ToArray();
            return actual.Length == want.Length && actual.SequenceEqual(want);
        }

        static MethodDef PickMethod(TypeDef type, string spec, string which)
        {
            var matches = type.Methods.Where(m => MethodMatches(m, spec)).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count == 0)
                throw new InvalidOperationException($"Method not found in {which}: {type.FullName}.{spec}");
            throw new InvalidOperationException(
                $"Ambiguous method '{spec}' in {which} {type.FullName} ({matches.Count} overloads) - "
                + "qualify it with a parameter type list, e.g. Name(System.String,System.Int32)");
        }

        static void CloneBody(MethodDef sourceMethod, MethodDef targetMethod, Importer importer)
        {
            var srcBody = sourceMethod.Body;
            var newBody = new CilBody { InitLocals = srcBody.InitLocals, MaxStack = srcBody.MaxStack };

            var localMap = new Dictionary<Local, Local>();
            foreach (var l in srcBody.Variables)
            {
                var newLocal = new Local(importer.Import(l.Type), l.Name);
                localMap[l] = newLocal;
                newBody.Variables.Add(newLocal);
            }

            var instrMap = new Dictionary<Instruction, Instruction>();
            foreach (var instr in srcBody.Instructions)
            {
                var newInstr = new Instruction(instr.OpCode) { Operand = instr.Operand };
                instrMap[instr] = newInstr;
                newBody.Instructions.Add(newInstr);
            }

            foreach (var instr in newBody.Instructions)
                instr.Operand = FixOperand(instr.Operand, instrMap, localMap, targetMethod, importer);

            foreach (var eh in srcBody.ExceptionHandlers)
            {
                newBody.ExceptionHandlers.Add(new ExceptionHandler(eh.HandlerType)
                {
                    TryStart = eh.TryStart != null ? instrMap[eh.TryStart] : null,
                    TryEnd = eh.TryEnd != null ? instrMap[eh.TryEnd] : null,
                    HandlerStart = eh.HandlerStart != null ? instrMap[eh.HandlerStart] : null,
                    HandlerEnd = eh.HandlerEnd != null ? instrMap[eh.HandlerEnd] : null,
                    FilterStart = eh.FilterStart != null ? instrMap[eh.FilterStart] : null,
                    CatchType = eh.CatchType != null ? importer.Import(eh.CatchType) : null,
                });
            }

            targetMethod.Body = newBody;
        }

        static object FixOperand(object operand, Dictionary<Instruction, Instruction> instrMap, Dictionary<Local, Local> localMap, MethodDef targetMethod, Importer importer)
        {
            switch (operand)
            {
                case null: return null;
                case Instruction target: return instrMap[target];
                case Instruction[] targets: return Array.ConvertAll(targets, t => instrMap[t]);
                case Local srcLocal: return localMap[srcLocal];
                case Parameter srcParam: return targetMethod.Parameters[srcParam.Index];
                case MemberRef mr: return importer.Import(mr);
                case FieldDef fd: return importer.Import(fd);
                case MethodDef md: return importer.Import(md);
                case MethodSpec ms: return importer.Import(ms);
                case ITypeDefOrRef typeRef: return importer.Import(typeRef);
                case string s: return s;
                default:
                    if (operand.GetType().IsPrimitive) return operand;
                    throw new NotSupportedException($"Unhandled operand type: {operand.GetType().FullName} (value: {operand})");
            }
        }
    }
}
