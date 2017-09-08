﻿using System.Collections.Generic;
using System.Linq;
using ASCompletion.Completion;
using ASCompletion.Context;
using LintingHelper;
using PluginCore;
using PluginCore.Localization;
using PluginCore.Managers;
using ScintillaNet;

namespace HaXeContext.Linters
{
    class DiagnosticsLinter : ILintProvider
    {
        public void LintAsync(string[] files, LintCallback callback)
        {
            var context = ASContext.GetLanguageContext("haxe") as Context;
            if (context == null) return;
            var completionMode = ((HaXeSettings) context.Settings).CompletionMode;
            if (completionMode == HaxeCompletionModeEnum.FlashDevelop) return;
            var haxeVersion = context.GetCurrentSDKVersion();
            if (haxeVersion < "3.3.0") return;

            var list = new List<LintingResult>();

            var sci = new ScintillaControl
            {
                FileName = "",
                ConfigurationLanguage = "haxe"
            };

            var hc = context.GetHaxeComplete(sci, new ASExpr {Position = 0}, true, HaxeCompilerService.GLOBAL_DIAGNOSTICS);

            //Make sure all files are actually compiled
            foreach (var file in files)
            {
                var fileModel = context.GetCachedFileModel(file);
                hc.AdditionalArguments.Add($"--macro \"haxe.macro.Context.getModule('{fileModel.FullPackage}')\"");
                //TODO: this adds way too many arguments for any non trivial codebase
            }
                

            hc.GetDiagnostics((complete, results, status) =>
            {
                sci.Dispose();

                AddDiagnosticsResults(list, files, status, results, hc);

                callback(list);
            });
        }

        void AddDiagnosticsResults(List<LintingResult> list, string[] files, HaxeCompleteStatus status, List<HaxeDiagnosticsResult> results, HaxeComplete hc)
        {
            if (status == HaxeCompleteStatus.DIAGNOSTICS && results != null)
            {
                var wasLinted = new Dictionary<string, bool>();

                foreach (var file in files)
                    wasLinted[file] = false;

                foreach (var res in results)
                {
                    var range = res.Range ?? res.Args.Range;

                    var result = new LintingResult
                    {
                        File = range.Path,
                        FirstChar = range.CharacterStart,
                        Length = range.CharacterEnd - range.CharacterStart,
                        Line = range.LineStart + 1,
                    };

                    if (!files.Contains(result.File)) //ignore results we were not asked for
                        continue;

                    wasLinted[result.File] = true;

                    switch (res.Severity)
                    {
                        case HaxeDiagnosticsSeverity.INFO:
                            result.Severity = LintingSeverity.Info;
                            break;
                        case HaxeDiagnosticsSeverity.ERROR:
                            result.Severity = LintingSeverity.Error;
                            break;
                        case HaxeDiagnosticsSeverity.WARNING:
                            result.Severity = LintingSeverity.Warning;
                            break;
                        default:
                            continue;
                    }

                    switch (res.Kind)
                    {
                        case HaxeDiagnosticsKind.UnusedImport:
                            result.Description = TextHelper.GetString("Info.UnusedImport");
                            break;
                        case HaxeDiagnosticsKind.UnresolvedIdentifier:
                            result.Description = TextHelper.GetString("Info.UnresolvedIdentifier");
                            break;
                        case HaxeDiagnosticsKind.CompilerError:
                        case HaxeDiagnosticsKind.RemovableCode:
                            result.Description = res.Args.Description;
                            break;
                        default: //in case new kinds are added in new compiler versions
                            continue;
                    }
                    
                    list.Add(result);
                }

                foreach (var file in wasLinted)
                {
                    if (!file.Value)
                    {
                        //at this point, the file either has no flaws, or was not linted, because it is not in use
                        //TODO: lint this file on its own :(
                    }
                }
            }
            else if (status == HaxeCompleteStatus.ERROR)
            {
                PluginBase.RunAsync(() =>
                {
                    TraceManager.Add(hc.Errors, (int)TraceType.Error);
                });
            }
        }
    }
}
