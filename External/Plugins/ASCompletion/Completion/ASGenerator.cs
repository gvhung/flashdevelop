using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ASCompletion.Context;
using ASCompletion.Generators;
using ASCompletion.Helpers;
using ASCompletion.Model;
using ASCompletion.Settings;
using PluginCore;
using PluginCore.Controls;
using PluginCore.Helpers;
using PluginCore.Localization;
using PluginCore.Managers;
using PluginCore.Utilities;
using ScintillaNet;

namespace ASCompletion.Completion
{
    public class ASGenerator : IContextualGenerator
    {
        #region context detection (ie. entry points)

        internal const string patternEvent = "Listener\\s*\\((\\s*([a-z_0-9.\\\"']+)\\s*,)?\\s*(?<event>[a-z_0-9.\\\"']+)\\s*,\\s*(this\\.)?{0}";
        const string patternAS2Delegate = @"\.\s*create\s*\(\s*[a-z_0-9.]+,\s*{0}";
        const string patternVarDecl = @"\s*{0}\s*:\s*{1}";
        const string patternMethod = @"{0}\s*\(";
        const string patternMethodDecl = @"function\s+{0}\s*\(";
        const string patternClass = @"new\s*{0}";
        const string BlankLine = "$(Boundary)\n\n";
        const string NewLine = "$(Boundary)\n";
        static private Regex reModifiers = new Regex("^\\s*(\\$\\(Boundary\\))?([a-z ]+)(function|var|const)", RegexOptions.Compiled);
        static private Regex reSuperCall = new Regex("^super\\s*\\(", RegexOptions.Compiled);

        protected internal static string contextToken;
        static internal string contextParam;
        static internal Match contextMatch;
        static internal ASResult contextResolved;
        static internal MemberModel contextMember;
        static private bool firstVar;

        static private bool IsHaxe
        {
            get { return ASContext.Context.CurrentModel.haXe; }
        }

        public static bool HandleGeneratorCompletion(ScintillaControl sci, bool autoHide, string word)
        {
            var generator = ASContext.Context.CodeGenerator as ASGenerator;
            if (generator == null) return false;
            if (!string.IsNullOrEmpty(word) && word == ASContext.Context.Features.overrideKey)
                return generator.HandleOverrideCompletion(autoHide);
            return false;
        }

        public static void ContextualGenerator(ScintillaControl sci, List<ICompletionListItem> options)
        {
            ASContext.Context.CodeGenerator.ContextualGenerator(sci, sci.CurrentPos, options);
        }

        public bool ContextualGenerator(ScintillaControl sci, int position, List<ICompletionListItem> options)
        {
            var context = ASContext.Context;
            if (context is ASContext) ((ASContext)context).UpdateCurrentFile(false); // update model

            lookupPosition = -1;
            if (sci.PositionIsOnComment(position)) return false;
            int style = sci.BaseStyleAt(position);
            if (style == 19 || style == 24) // on keyword
                return false;
            contextMatch = null;
            contextToken = sci.GetWordFromPosition(position);
            var expr = ASComplete.GetExpressionType(sci, sci.WordEndPosition(position, true));
            ContextualGenerator(sci, position, expr, options);
            return true;
        }

        protected virtual void ContextualGenerator(ScintillaControl sci, int position, ASResult resolve, List<ICompletionListItem> options)
        {
            var line = sci.LineFromPosition(position);
            var found = GetDeclarationAtLine(line);
            if (CanShowConvertToConst(sci, position, resolve, found))
            {
                ShowConvertToConst(found, options);
                return;
            }

            contextResolved = resolve;
            var context = ASContext.Context;
            var isNotInterface = (context.CurrentClass.Flags & FlagType.Interface) == 0;

            // ignore automatic vars (MovieClip members)
            if (isNotInterface
                && resolve.Member != null
                && (((resolve.Member.Flags & FlagType.AutomaticVar) > 0) || (resolve.InClass != null && resolve.InClass.QualifiedName == "Object")))
            {
                resolve.Member = null;
                resolve.Type = null;
            }

            if (isNotInterface && !found.InClass.IsVoid() && contextToken != null)
            {
                // implement interface
                if (CanShowImplementInterfaceList(sci, position, resolve, found))
                {
                    contextParam = resolve.Type.Type;
                    ShowImplementInterface(found, options);
                    return;
                }
                // promote to class var
                if (!context.CurrentClass.IsVoid() && resolve.Member != null && (resolve.Member.Flags & FlagType.LocalVar) > 0)
                {
                    contextMember = resolve.Member;
                    ShowPromoteLocalAndAddParameter(found, options);
                    return;
                }
            }

            var suggestItemDeclaration = false;
            if (contextToken != null && resolve.Member == null && sci.BaseStyleAt(position) != 5)
            {
                // import declaration
                if ((resolve.Type == null || resolve.Type.IsVoid() || !context.IsImported(resolve.Type, line)) && CheckAutoImport(resolve, options)) return;
                if (resolve.Type == null)
                {
                    suggestItemDeclaration = ASComplete.IsTextStyle(sci.BaseStyleAt(position - 1));
                }
            }
            if (isNotInterface && found.Member != null)
            {
                // private var -> property
                if ((found.Member.Flags & FlagType.Variable) > 0 && (found.Member.Flags & FlagType.LocalVar) == 0)
                {
                    var text = sci.GetLine(line);
                    // maybe we just want to import the member's non-imported type
                    Match m = Regex.Match(text, String.Format(patternVarDecl, found.Member.Name, contextToken));
                    if (m.Success)
                    {
                        contextMatch = m;
                        ClassModel type = context.ResolveType(contextToken, context.CurrentModel);
                        if (type.IsVoid() && CheckAutoImport(resolve, options))
                            return;
                    }
                    ShowGetSetList(found, options);
                    return;
                }
                // inside a function
                if ((found.Member.Flags & (FlagType.Function | FlagType.Getter | FlagType.Setter)) > 0
                    && resolve.Member == null && resolve.Type == null)
                {
                    if (IsHaxe)
                    {
                        if (contextToken == "get")
                        {
                            ShowGetterList(found, options);
                            return;
                        }
                        if (contextToken == "set")
                        {
                            ShowSetterList(found, options);
                            return;
                        }
                    }
                    var text = sci.GetLine(line);
                    if (contextToken != null)
                    {
                        // "generate event handlers" suggestion
                        string re = String.Format(patternEvent, contextToken);
                        Match m = Regex.Match(text, re, RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            contextMatch = m;
                            contextParam = CheckEventType(m.Groups["event"].Value);
                            ShowEventList(found, options);
                            return;
                        }
                        m = Regex.Match(text, String.Format(patternAS2Delegate, contextToken), RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            contextMatch = m;
                            ShowDelegateList(found, options);
                            return;
                        }
                        // suggest delegate
                        if (context.Features.hasDelegates)
                        {
                            m = Regex.Match(text, @"([a-z0-9_.]+)\s*\+=\s*" + contextToken, RegexOptions.IgnoreCase);
                            if (m.Success)
                            {
                                int offset = sci.PositionFromLine(sci.LineFromPosition(position))
                                    + m.Groups[1].Index + m.Groups[1].Length;
                                resolve = ASComplete.GetExpressionType(sci, offset);
                                if (resolve.Member != null)
                                    contextMember = ResolveDelegate(resolve.Member.Type, resolve.InFile);
                                contextMatch = m;
                                ShowDelegateList(found, options);
                                return;
                            }
                        }
                    }
                    else
                    {
                        // insert a default handler name, then "generate event handlers" suggestion
                        Match m = Regex.Match(text, String.Format(patternEvent, ""), RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            int regexIndex = m.Index + sci.PositionFromLine(sci.CurrentLine);
                            GenerateDefaultHandlerName(sci, position, regexIndex, m.Groups["event"].Value, true);
                            resolve = ASComplete.GetExpressionType(sci, sci.CurrentPos);
                            if (resolve.Member == null || (resolve.Member.Flags & FlagType.AutomaticVar) > 0)
                            {
                                contextMatch = m;
                                contextParam = CheckEventType(m.Groups["event"].Value);
                                ShowEventList(found, options);
                            }
                            return;
                        }

                        // insert default delegate name, then "generate delegate" suggestion
                        if (context.Features.hasDelegates)
                        {
                            m = Regex.Match(text, @"([a-z0-9_.]+)\s*\+=\s*", RegexOptions.IgnoreCase);
                            if (m.Success)
                            {
                                int offset = sci.PositionFromLine(sci.LineFromPosition(position))
                                        + m.Groups[1].Index + m.Groups[1].Length;
                                resolve = ASComplete.GetExpressionType(sci, offset);
                                if (resolve.Member != null)
                                {
                                    contextMember = ResolveDelegate(resolve.Member.Type, resolve.InFile);
                                    string delegateName = resolve.Member.Name;
                                    if (delegateName.StartsWithOrdinal("on")) delegateName = delegateName.Substring(2);
                                    GenerateDefaultHandlerName(sci, position, offset, delegateName, false);
                                    resolve = ASComplete.GetExpressionType(sci, sci.CurrentPos);
                                    if (resolve.Member == null || (resolve.Member.Flags & FlagType.AutomaticVar) > 0)
                                    {
                                        contextMatch = m;
                                        ShowDelegateList(found, options);
                                    }
                                    return;
                                }
                            }
                        }
                    }
                }

                // "Generate fields from parameters" suggestion
                if ((found.Member.Flags & FlagType.Function) > 0
                    && found.Member.Parameters != null && (found.Member.Parameters.Count > 0)
                    && resolve.Member != null && (resolve.Member.Flags & FlagType.ParameterVar) > 0)
                {
                    contextMember = resolve.Member;
                    ShowFieldFromParameter(found, options);
                    return;
                }

                // "add to interface" suggestion
                if (CanShowAddToInterfaceList(sci, position, resolve, found))
                {
                    string funcName = found.Member.Name;
                    FlagType flags = found.Member.Flags & ~FlagType.Access;

                    List<string> interfaces = new List<string>();
                    foreach (string interf in found.InClass.Implements)
                    {
                        bool skip = false;
                        ClassModel cm = context.ResolveType(interf, context.CurrentModel);
                        foreach (MemberModel m in cm.Members)
                        {
                            if (m.Name.Equals(funcName) && m.Flags.Equals(flags))
                            {
                                skip = true;
                                break;
                            }
                        }
                        if (!skip)
                        {
                            interfaces.Add(interf);
                        }
                    }
                    if (interfaces.Count > 0)
                    {
                        ShowAddInterfaceDefList(found, interfaces, options);
                        return;
                    }
                }

                // "assign var to statement" suggestion
                int curLine = sci.CurrentLine;
                string ln = sci.GetLine(curLine).TrimEnd();
                if (ln.Length > 0
                    && ln.Length <= sci.CurrentPos - sci.PositionFromLine(curLine)) // cursor at end of line
                {
                    var returnType = GetStatementReturnType(sci, found.InClass, sci.GetLine(curLine), sci.PositionFromLine(curLine));
                    if (returnType.resolve.Member?.Type == ASContext.Context.Features.voidKey) return;
                    if (returnType.resolve.Type == null && returnType.resolve.Context?.WordBefore == "new") ShowNewClassList(found, returnType.resolve.Context, options);
                    else if (returnType.resolve.Type == null && returnType.resolve.Member == null) return;
                    else ShowAssignStatementToVarList(found, returnType, options);
                    return;
                }
            }

            // suggest generate constructor / toString
            if (CanShowGenerateConstructorAndToString(sci, position, resolve, found))
            {
                bool hasConstructor = false;
                bool hasToString = false;
                foreach (MemberModel m in context.CurrentClass.Members)
                {
                    if (!hasConstructor && (m.Flags & FlagType.Constructor) > 0)
                        hasConstructor = true;

                    if (!hasToString && (m.Flags & FlagType.Function) > 0 && m.Name.Equals("toString"))
                        hasToString = true;
                }

                if (!hasConstructor || !hasToString)
                {
                    ShowConstructorAndToStringList(found, hasConstructor, hasToString, options);
                    return;
                }
            }

            if (isNotInterface
                && resolve.Member != null
                && resolve.Type != null
                && resolve.Type.QualifiedName == context.Features.stringKey
                && !found.InClass.IsVoid())
            {
                int lineStartPos = sci.PositionFromLine(sci.CurrentLine);
                var text = sci.GetLine(line);
                string lineStart = text.Substring(0, sci.CurrentPos - lineStartPos);
                Match m = Regex.Match(lineStart, String.Format(@"new\s+(?<event>\w+)\s*\(\s*\w+", lineStart));
                if (m.Success)
                {
                    Group g = m.Groups["event"];
                    ASResult eventResolve = ASComplete.GetExpressionType(sci, lineStartPos + g.Index + g.Length);
                    if (eventResolve != null && eventResolve.Type != null)
                    {
                        ClassModel aType = eventResolve.Type;
                        aType.ResolveExtends();
                        while (!aType.IsVoid() && aType.QualifiedName != "Object")
                        {
                            if (aType.QualifiedName == "flash.events.Event")
                            {
                                contextParam = eventResolve.Type.QualifiedName;
                                ShowEventMetatagList(found, options);
                                return;
                            }
                            aType = aType.Extends;
                        }
                    }
                }
            }

            // suggest declaration
            if (contextToken != null)
            {
                if (suggestItemDeclaration)
                {
                    var text = sci.GetLine(line);
                    Match m = Regex.Match(text, String.Format(patternClass, contextToken));
                    if (m.Success)
                    {
                        contextMatch = m;
                        ShowNewClassList(found, options);
                    }
                    else if (!found.InClass.IsVoid())
                    {
                        m = Regex.Match(text, String.Format(patternMethod, contextToken));
                        if (m.Success)
                        {
                            contextMatch = m;
                            ShowNewMethodList(found, options);
                        }
                        else ShowNewVarList(found, options);
                    }
                }
                else
                {
                    if (resolve.InClass != null
                        && resolve.InClass.InFile != null
                        && resolve.Member != null
                        && (resolve.Member.Flags & FlagType.Function) > 0
                        && File.Exists(resolve.InClass.InFile.FileName)
                        && !resolve.InClass.InFile.FileName.StartsWithOrdinal(PathHelper.AppDir))
                    {
                        var text = sci.GetLine(line);
                        Match m = Regex.Match(text, String.Format(patternMethodDecl, contextToken));
                        Match m2 = Regex.Match(text, String.Format(patternMethod, contextToken));
                        if (!m.Success && m2.Success)
                        {
                            contextMatch = m;
                            ShowChangeMethodDeclList(found, options);
                        }
                    }
                    else if (resolve.Type != null
                        && resolve.Type.InFile != null
                        && resolve.RelClass != null
                        && File.Exists(resolve.Type.InFile.FileName)
                        && !resolve.Type.InFile.FileName.StartsWithOrdinal(PathHelper.AppDir))
                    {
                        var text = sci.GetLine(line);
                        var m = Regex.Match(text, string.Format(patternClass, contextToken));
                        if (m.Success)
                        {
                            contextMatch = m;
                            MemberModel constructor = null;
                            var type = resolve.Type;
                            while (!type.IsVoid())
                            {
                                constructor = type.Members.Search(type.Name, FlagType.Constructor, 0);
                                if (constructor != null) break;
                                type.ResolveExtends();
                                type = type.Extends;
                            }
                            if (constructor == null) ShowConstructorAndToStringList(new FoundDeclaration { InClass = resolve.Type }, false, true, options);
                            else
                            {
                                var constructorParametersCount = constructor.Parameters?.Count ?? 0;
                                var wordEndPosition = sci.WordEndPosition(sci.CurrentPos, true);
                                var parameters = ParseFunctionParameters(sci, wordEndPosition);
                                if (parameters.Count != constructorParametersCount) ShowChangeConstructorDeclarationList(found, parameters, options);
                                else
                                {
                                    for (var i = 0; i < parameters.Count; i++)
                                    {
                                        if (parameters[i].paramType == constructor.Parameters[i].Type) continue;
                                        ShowChangeConstructorDeclarationList(found, parameters, options);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            // TODO: Empty line, show generators list? yep
        }

        /// <summary>
        /// Check if "Convert to constant" are available at the current cursor position.
        /// </summary>
        /// <param name="sci">The Scintilla control containing the document</param>
        /// <param name="position">Cursor position</param>
        /// <param name="expr">Expression at cursor position</param>
        /// <param name="found">The declaration target at current line(can not be null)</param>
        /// <returns>true, if can show `Convert to constant` list</returns>
        protected virtual bool CanShowConvertToConst(ScintillaControl sci, int position, ASResult expr, FoundDeclaration found)
        {
            return !ASContext.Context.CurrentClass.Flags.HasFlag(FlagType.Interface)
                && ASComplete.IsLiteralStyle(sci.BaseStyleAt(position));
        }

        /// <summary>
        /// Check if "Generate constructor" and "Generate toString()" are available at the current cursor position.
        /// </summary>
        /// <param name="sci">The Scintilla control containing the document</param>
        /// <param name="position">Cursor position</param>
        /// <param name="expr">Expression at cursor position</param>
        /// <param name="found">The declaration target at current line(can not be null)</param>
        /// <returns>true, if can show `Generate constructor` and(or) `Generate toString()` list</returns>
        protected virtual bool CanShowGenerateConstructorAndToString(ScintillaControl sci, int position, ASResult expr, FoundDeclaration found)
        {
            return contextToken == null
                && found.Member == null
                && !found.InClass.IsVoid()
                && !found.InClass.Flags.HasFlag(FlagType.Interface)
                && position < sci.LineEndPosition(found.InClass.LineTo)
                && !ASContext.Context.CodeComplete.PositionIsBeforeBody(sci, position, found.InClass);
        }

        /// <summary>
        /// Check if "Implement Interface" are available at the current cursor position.
        /// </summary>
        /// <param name="sci">The Scintilla control containing the document</param>
        /// <param name="position">Cursor position</param>
        /// <param name="expr">Expression at cursor position</param>
        /// <param name="found">Declaration target at current line(can not be null)</param>
        /// <returns>true, if can show `Implement Interface` list</returns>
        protected virtual bool CanShowImplementInterfaceList(ScintillaControl sci, int position, ASResult expr, FoundDeclaration found)
        {
            return expr.Context.ContextFunction == null && expr.Context.ContextMember == null
                && expr.Member == null && expr.Type != null && (expr.Type.Flags & FlagType.Interface) > 0;
        }

        /// <summary>
        /// Check if "Add to interface" are available at the current cursor position.
        /// </summary>
        /// <param name="sci">The Scintilla control containing the document</param>
        /// <param name="position">Cursor position</param>
        /// <param name="expr">Expression at cursor position</param>
        /// <param name="found">Declaration target at current line(can not be null)</param>
        /// <returns>true, if can show `Add to interface` list</returns>
        protected virtual bool CanShowAddToInterfaceList(ScintillaControl sci, int position, ASResult expr, FoundDeclaration found)
        {
            return expr.Member != null
                   && !expr.Member.Flags.HasFlag(FlagType.Static)
                   && !expr.Member.Flags.HasFlag(FlagType.Constructor)
                   && expr.Member.Name == found.Member.Name
                   && sci.LineFromPosition(position) == found.Member.LineFrom
                   && ((found.Member.Flags & FlagType.Function) > 0
                       || (found.Member.Flags & FlagType.Getter) > 0
                       || (found.Member.Flags & FlagType.Setter) > 0)
                   && !found.InClass.IsVoid()
                   && found.InClass.Implements != null
                   && found.InClass.Implements.Count > 0;
        }

        private static MemberModel ResolveDelegate(string type, FileModel inFile)
        {
            foreach (MemberModel def in inFile.Members)
                if (def.Name == type && (def.Flags & FlagType.Delegate) > 0)
                    return def;

            if (type.IndexOf('.') < 0)
            {
                string dotType = '.' + type;
                MemberList imports = ASContext.Context.ResolveImports(inFile);
                foreach (MemberModel import in imports)
                    if (import.Type.EndsWithOrdinal(dotType))
                    {
                        type = import.Type;
                        break;
                    }
            }

            MemberList known = ASContext.Context.GetAllProjectClasses();
            foreach (MemberModel def in known)
                if (def.Type == type && (def.Flags & FlagType.Delegate) > 0)
                    return def;
            return null;
        }

        private static void GenerateDefaultHandlerName(ScintillaControl sci, int position, int targetPos, string eventName, bool closeBrace)
        {
            string target = null;
            int contextOwnerPos = GetContextOwnerEndPos(sci, sci.WordStartPosition(targetPos, true));
            if (contextOwnerPos != -1)
            {
                ASResult contextOwnerResult = ASComplete.GetExpressionType(sci, contextOwnerPos);
                if (contextOwnerResult != null && !contextOwnerResult.IsNull()
                    && contextOwnerResult.Member != null)
                {
                    if (contextOwnerResult.Member.Name == "contentLoaderInfo" && sci.CharAt(contextOwnerPos) == '.')
                    {
                        // we want to name the event from the loader var and not from the contentLoaderInfo parameter
                        contextOwnerPos = GetContextOwnerEndPos(sci, sci.WordStartPosition(contextOwnerPos - 1, true));
                        if (contextOwnerPos != -1)
                        {
                            contextOwnerResult = ASComplete.GetExpressionType(sci, contextOwnerPos);
                            if (contextOwnerResult != null && !contextOwnerResult.IsNull()
                                && contextOwnerResult.Member != null)
                            {
                                target = contextOwnerResult.Member.Name;
                            }
                        }
                    }
                    else
                    {
                        target = contextOwnerResult.Member.Name;
                    }
                }
            }
            
            eventName = Camelize(eventName.Substring(eventName.LastIndexOf('.') + 1));
            if (target != null) target = target.TrimStart('_');

            switch (ASContext.CommonSettings.HandlerNamingConvention)
            {
                case HandlerNamingConventions.handleTargetEventName:
                    if (target == null) contextToken = "handle" + Capitalize(eventName);
                    else contextToken = "handle" + Capitalize(target) + Capitalize(eventName);
                    break;
                case HandlerNamingConventions.onTargetEventName:
                    if (target == null) contextToken = "on" + Capitalize(eventName);
                    else contextToken = "on" + Capitalize(target) + Capitalize(eventName);
                    break;
                case HandlerNamingConventions.target_eventNameHandler:
                    if (target == null) contextToken = eventName + "Handler";
                    else contextToken = target + "_" + eventName + "Handler";
                    break;
                default: //HandlerNamingConventions.target_eventName
                    if (target == null) contextToken = eventName;
                    else contextToken = target + "_" + eventName;
                    break;
            }

            char c = (char)sci.CharAt(position - 1);
            if (c == ',') InsertCode(position, "$(Boundary) " + contextToken + "$(Boundary)", sci);
            else InsertCode(position, contextToken, sci);

            position = sci.WordEndPosition(position + 1, true);
            sci.SetSel(position, position);
            c = (char)sci.CharAt(position);
            if (c <= 32) if (closeBrace) sci.ReplaceSel(");"); else sci.ReplaceSel(";");

            sci.SetSel(position, position);
        }

        private static FoundDeclaration GetDeclarationAtLine(int line)
        {
            FoundDeclaration result = new FoundDeclaration();
            FileModel model = ASContext.Context.CurrentModel;

            foreach (MemberModel member in model.Members)
            {
                if (member.LineFrom <= line && member.LineTo >= line)
                {
                    result.Member = member;
                    return result;
                }
            }

            foreach (ClassModel aClass in model.Classes)
            {
                if (aClass.LineFrom <= line && aClass.LineTo >= line)
                {
                    result.InClass = aClass;
                    foreach (MemberModel member in aClass.Members)
                    {
                        if (member.LineFrom <= line && member.LineTo >= line)
                        {
                            result.Member = member;
                            return result;
                        }
                    }
                    return result;
                }
            }
            return result;
        }

        protected bool CheckAutoImport(ASResult expr, List<ICompletionListItem> options)
        {
            if (ASContext.Context.CurrentClass.Equals(expr.RelClass)) return false;
            var allClasses = ASContext.Context.GetAllProjectClasses();
            if (allClasses != null)
            {
                var names = new HashSet<string>();
                var matches = new List<MemberModel>();
                var dotToken = "." + contextToken;
                foreach (MemberModel member in allClasses)
                    if (!names.Contains(member.Name) && member.Name.EndsWithOrdinal(dotToken))
                    {
                        matches.Add(member);
                        names.Add(member.Name);
                    }
                if (matches.Count > 0)
                {
                    ShowImportClass(matches, options);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// For the Event handlers generator:
        /// check that the event name's const is declared in an Event type
        /// </summary>
        internal static string CheckEventType(string name)
        {
            if (name.IndexOf('"') >= 0) return "Event";
            if (name.IndexOf('.') > 0) name = name.Substring(0, name.IndexOf('.'));
            ClassModel model = ASContext.Context.ResolveType(name, ASContext.Context.CurrentModel);
            if (model.IsVoid() || model.Name == "Event") return "Event";
            model.ResolveExtends();
            while (!model.IsVoid() && model.Name != "Event")
                model = model.Extends;
            if (model.Name == "Event") return name;
            else return "Event";
        }
        #endregion

        #region generators lists

        private static void ShowImportClass(List<MemberModel> matches, ICollection<ICompletionListItem> options)
        {
            if (matches.Count == 1)
            {
                GenerateJob(GeneratorJobType.AddImport, matches[0], null, null, null);
                return;
            }
            
            foreach (MemberModel member in matches)
            {
                if ((member.Flags & FlagType.Class) > 0)
                    options.Add(new GeneratorItem("import " + member.Type, GeneratorJobType.AddImport, member, null));
                else if (member.IsPackageLevel)
                    options.Add(new GeneratorItem("import " + member.Name, GeneratorJobType.AddImport, member, null));
            }
        }

        private static void ShowPromoteLocalAndAddParameter(FoundDeclaration found, ICollection<ICompletionListItem> options)
        {
            string label = TextHelper.GetString("ASCompletion.Label.PromoteLocal");
            string labelMove = TextHelper.GetString("ASCompletion.Label.MoveDeclarationOnTop");
            string labelParam = TextHelper.GetString("ASCompletion.Label.AddAsParameter");
            options.Add(new GeneratorItem(label, GeneratorJobType.PromoteLocal, found.Member, found.InClass));
            options.Add(new GeneratorItem(labelMove, GeneratorJobType.MoveLocalUp, found.Member, found.InClass));
            options.Add(new GeneratorItem(labelParam, GeneratorJobType.AddAsParameter, found.Member, found.InClass));
        }

        private static void ShowConvertToConst(FoundDeclaration found, ICollection<ICompletionListItem> options)
        {
            string label = TextHelper.GetString("ASCompletion.Label.ConvertToConst");
            options.Add(new GeneratorItem(label, GeneratorJobType.ConvertToConst, found.Member, found.InClass));
        }

        private static void ShowImplementInterface(FoundDeclaration found, ICollection<ICompletionListItem> options)
        {
            string label = TextHelper.GetString("ASCompletion.Label.ImplementInterface");
            options.Add(new GeneratorItem(label, GeneratorJobType.ImplementInterface, null, found.InClass));
        }

        private static void ShowNewVarList(FoundDeclaration found, ICollection<ICompletionListItem> options)
        {
            bool generateClass = true;
            ScintillaControl sci = ASContext.CurSciControl;
            int currentPos = sci.CurrentPos;
            ASResult exprAtCursor = ASComplete.GetExpressionType(sci, sci.WordEndPosition(currentPos, true));
            if (exprAtCursor == null || exprAtCursor.InClass == null || found.InClass.QualifiedName.Equals(exprAtCursor.RelClass.QualifiedName))
                exprAtCursor = null;
            ASResult exprLeft = null;
            int curWordStartPos = sci.WordStartPosition(currentPos, true);
            if ((char)sci.CharAt(curWordStartPos - 1) == '.') exprLeft = ASComplete.GetExpressionType(sci, curWordStartPos - 1);
            if (exprLeft != null && exprLeft.Type == null) exprLeft = null;
            if (exprLeft != null)
            {
                if (exprLeft.Type.InFile != null && !File.Exists(exprLeft.Type.InFile.FileName)) return;
                generateClass = false;
                ClassModel curClass = ASContext.Context.CurrentClass;
                if (!IsHaxe)
                {
                    if (exprLeft.Type.Equals(curClass)) exprLeft = null;
                }
                else 
                {
                    while (!curClass.IsVoid())
                    {
                        if (curClass.Equals(exprLeft.Type))
                        {
                            exprLeft = null;
                            break;
                        }
                        curClass.ResolveExtends();
                        curClass = curClass.Extends;
                    }
                }
            }
            string label;
            if ((exprAtCursor != null && exprAtCursor.RelClass != null && (exprAtCursor.RelClass.Flags & FlagType.Interface) > 0)
                || (found.InClass != null && (found.InClass.Flags & FlagType.Interface) > 0))
            {
                label = TextHelper.GetString("ASCompletion.Label.GenerateFunctionInterface");
                options.Add(new GeneratorItem(label, GeneratorJobType.FunctionPublic, found.Member, found.InClass));
            }
            else
            {
                string textAtCursor = sci.GetWordFromPosition(currentPos);
                bool isConst = textAtCursor != null && textAtCursor.ToUpper().Equals(textAtCursor);
                if (isConst)
                {
                    label = TextHelper.GetString("ASCompletion.Label.GenerateConstant");
                    options.Add(new GeneratorItem(label, GeneratorJobType.Constant, found.Member, found.InClass));
                }

                bool genProtectedDecl = GetDefaultVisibility(found.InClass) == Visibility.Protected;
                if (exprAtCursor == null && exprLeft == null)
                {
                    if (genProtectedDecl) label = TextHelper.GetString("ASCompletion.Label.GenerateProtectedVar");
                    else label = TextHelper.GetString("ASCompletion.Label.GeneratePrivateVar");
                    options.Add(new GeneratorItem(label, GeneratorJobType.Variable, found.Member, found.InClass));
                }

                label = TextHelper.GetString("ASCompletion.Label.GeneratePublicVar");
                options.Add(new GeneratorItem(label, GeneratorJobType.VariablePublic, found.Member, found.InClass));

                if (exprAtCursor == null && exprLeft == null)
                {
                    if (genProtectedDecl) label = TextHelper.GetString("ASCompletion.Label.GenerateProtectedFunction");
                    else label = TextHelper.GetString("ASCompletion.Label.GeneratePrivateFunction");
                    options.Add(new GeneratorItem(label, GeneratorJobType.Function, found.Member, found.InClass));
                }

                label = TextHelper.GetString("ASCompletion.Label.GenerateFunctionPublic");
                options.Add(new GeneratorItem(label, GeneratorJobType.FunctionPublic, found.Member, found.InClass));

                if (generateClass)
                {
                    label = TextHelper.GetString("ASCompletion.Label.GenerateClass");
                    options.Add(new GeneratorItem(label, GeneratorJobType.Class, found.Member, found.InClass));
                }
            }
        }

        private static void ShowChangeMethodDeclList(FoundDeclaration found, ICollection<ICompletionListItem> options)
        {
            string label = TextHelper.GetString("ASCompletion.Label.ChangeMethodDecl");
            options.Add(new GeneratorItem(label, GeneratorJobType.ChangeMethodDecl, found.Member, found.InClass));
        }

        private static void ShowChangeConstructorDeclarationList(FoundDeclaration found, IList<FunctionParameter> parameters, ICollection<ICompletionListItem> options)
        {
            var label = TextHelper.GetString("ASCompletion.Label.ChangeConstructorDecl");
            options.Add(new GeneratorItem(label, GeneratorJobType.ChangeConstructorDecl, found.Member, found.InClass, parameters));
        }

        private static void ShowNewMethodList(FoundDeclaration found, ICollection<ICompletionListItem> options)
        {
            ScintillaControl sci = ASContext.CurSciControl;
            ASResult result = ASComplete.GetExpressionType(sci, sci.WordEndPosition(sci.CurrentPos, true));
            if (result == null || result.RelClass == null || found.InClass.QualifiedName.Equals(result.RelClass.QualifiedName))
                result = null;
            string label;
            ClassModel inClass = result != null ? result.RelClass : found.InClass;
            bool isInterface = (inClass.Flags & FlagType.Interface) > 0;
            if (!isInterface && result == null)
            {
                if (GetDefaultVisibility(found.InClass) == Visibility.Protected)
                    label = TextHelper.GetString("ASCompletion.Label.GenerateProtectedFunction");
                else label = TextHelper.GetString("ASCompletion.Label.GeneratePrivateFunction");
                options.Add(new GeneratorItem(label, GeneratorJobType.Function, found.Member, found.InClass));
            }
            if (isInterface) label = TextHelper.GetString("ASCompletion.Label.GenerateFunctionInterface");
            else label = TextHelper.GetString("ASCompletion.Label.GenerateFunctionPublic");
            options.Add(new GeneratorItem(label, GeneratorJobType.FunctionPublic, found.Member, found.InClass));
            label = TextHelper.GetString("ASCompletion.Label.GeneratePublicCallback");
            options.Add(new GeneratorItem(label, GeneratorJobType.VariablePublic, found.Member, found.InClass));
        }

        private static void ShowAssignStatementToVarList(FoundDeclaration found, StatementReturnType data, ICollection<ICompletionListItem> options)
        {
            var label = TextHelper.GetString("ASCompletion.Label.AssignStatementToVar");
            options.Add(new GeneratorItem(label, GeneratorJobType.AssignStatementToVar, found.Member, found.InClass, data));
        }

        private static void ShowNewClassList(FoundDeclaration found, ICollection<ICompletionListItem> options) => ShowNewClassList(found, null, options);

        private static void ShowNewClassList(FoundDeclaration found, ASExpr expr, ICollection<ICompletionListItem> options)
        {
            var label = TextHelper.GetString("ASCompletion.Label.GenerateClass");
            options.Add(new GeneratorItem(label, GeneratorJobType.Class, found.Member, found.InClass, expr));
        }

        private static void ShowConstructorAndToStringList(FoundDeclaration found, bool hasConstructor, bool hasToString, ICollection<ICompletionListItem> options)
        {
            if (!hasConstructor)
            {
                string label = TextHelper.GetString("ASCompletion.Label.GenerateConstructor");
                options.Add(new GeneratorItem(label, GeneratorJobType.Constructor, found.Member, found.InClass));
            }

            if (!hasToString)
            {
                string label = TextHelper.GetString("ASCompletion.Label.GenerateToString");
                options.Add(new GeneratorItem(label, GeneratorJobType.ToString, found.Member, found.InClass));
            }
        }

        private static void ShowEventMetatagList(FoundDeclaration found, ICollection<ICompletionListItem> options)
        {
            string label = TextHelper.GetString("ASCompletion.Label.GenerateEventMetatag");
            options.Add(new GeneratorItem(label, GeneratorJobType.EventMetatag, found.Member, found.InClass));
        }

        private static void ShowFieldFromParameter(FoundDeclaration found, ICollection<ICompletionListItem> options)
        {
            Hashtable parameters = new Hashtable();
            parameters["scope"] = GetDefaultVisibility(found.InClass);
            string label;
            if (GetDefaultVisibility(found.InClass) == Visibility.Protected)
                label = TextHelper.GetString("ASCompletion.Label.GenerateProtectedFieldFromParameter");
            else label = TextHelper.GetString("ASCompletion.Label.GeneratePrivateFieldFromParameter");
            options.Add(new GeneratorItem(label, GeneratorJobType.FieldFromParameter, found.Member, found.InClass, parameters));
            parameters = new Hashtable();
            parameters["scope"] = Visibility.Public;
            label = TextHelper.GetString("ASCompletion.Label.GeneratePublicFieldFromParameter");
            options.Add(new GeneratorItem(label, GeneratorJobType.FieldFromParameter, found.Member, found.InClass, parameters));
        }

        private static void ShowAddInterfaceDefList(FoundDeclaration found, IEnumerable<string> interfaces, ICollection<ICompletionListItem> options)
        {
            var label = TextHelper.GetString("ASCompletion.Label.AddInterfaceDef");
            foreach (var interf in interfaces)
            {
                options.Add(new GeneratorItem(String.Format(label, interf), GeneratorJobType.AddInterfaceDef, found.Member, found.InClass, interf));
            }
        }

        private static void ShowDelegateList(FoundDeclaration found, ICollection<ICompletionListItem> options)
        {
            string label = String.Format(TextHelper.GetString("ASCompletion.Label.GenerateHandler"), "Delegate");
            options.Add(new GeneratorItem(label, GeneratorJobType.Delegate, found.Member, found.InClass));
        }

        internal static void ShowEventList(FoundDeclaration found, List<ICompletionListItem> options)
        {
            string tmp = TextHelper.GetString("ASCompletion.Label.GenerateHandler");
            string labelEvent = String.Format(tmp, "Event");
            string labelDataEvent = String.Format(tmp, "DataEvent");
            string labelContext = String.Format(tmp, contextParam);
            string[] choices;
            if (contextParam != "Event") choices = new string[] { labelContext, labelEvent };
            else if (HasDataEvent()) choices = new string[] { labelEvent, labelDataEvent };
            else choices = new string[] { labelEvent };

            for (int i = 0; i < choices.Length; i++)
            {
                var choice = choices[i];
                options.Add(new GeneratorItem(choice,
                    choice == labelContext ? GeneratorJobType.ComplexEvent : GeneratorJobType.BasicEvent,
                    found.Member, found.InClass));
            }
        }

        private static bool HasDataEvent()
        {
            return !ASContext.Context.ResolveType("flash.events.DataEvent", ASContext.Context.CurrentModel).IsVoid();
        }

        private static void ShowGetSetList(FoundDeclaration found, ICollection<ICompletionListItem> options)
        {
            string name = GetPropertyNameFor(found.Member);
            ASResult result = new ASResult();
            ClassModel curClass = ASContext.Context.CurrentClass;
            ASComplete.FindMember(name, curClass, result, FlagType.Getter, 0);
            bool hasGetter = !result.IsNull();
            ASComplete.FindMember(name, curClass, result, FlagType.Setter, 0);
            bool hasSetter = !result.IsNull();
            if (hasGetter && hasSetter) return;
            if (!hasGetter && !hasSetter)
            {
                string label = TextHelper.GetString("ASCompletion.Label.GenerateGetSet");
                options.Add(new GeneratorItem(label, GeneratorJobType.GetterSetter, found.Member, found.InClass));
            }
            ShowGetterList(found, options);
            ShowSetterList(found, options);
        }

        private static void ShowGetterList(FoundDeclaration found, ICollection<ICompletionListItem> options)
        {
            var name = GetPropertyNameFor(found.Member);
            var result = new ASResult();
            ASComplete.FindMember(name, ASContext.Context.CurrentClass, result, FlagType.Getter, 0);
            if (!result.IsNull()) return;
            var label = TextHelper.GetString("ASCompletion.Label.GenerateGet");
            options.Add(new GeneratorItem(label, GeneratorJobType.Getter, found.Member, found.InClass));
        }

        private static void ShowSetterList(FoundDeclaration found, ICollection<ICompletionListItem> options)
        {
            var name = GetPropertyNameFor(found.Member);
            var result = new ASResult();
            ASComplete.FindMember(name, ASContext.Context.CurrentClass, result, FlagType.Setter, 0);
            if (!result.IsNull()) return;
            var label = TextHelper.GetString("ASCompletion.Label.GenerateSet");
            options.Add(new GeneratorItem(label, GeneratorJobType.Setter, found.Member, found.InClass));
        }

        #endregion

        #region code generation

        public static void SetJobContext(String contextToken, String contextParam, MemberModel contextMember, Match contextMatch)
        {
            ASGenerator.contextToken = contextToken;
            ASGenerator.contextParam = contextParam;
            ASGenerator.contextMember = contextMember;
            ASGenerator.contextMatch = contextMatch;
        }

        public static void GenerateJob(GeneratorJobType job, MemberModel member, ClassModel inClass, string itemLabel, Object data)
        {
            ScintillaControl sci = ASContext.CurSciControl;
            lookupPosition = sci.CurrentPos;

            int position;
            MemberModel latest;
            bool detach = true;
            switch (job)
            {
                case GeneratorJobType.Getter:
                case GeneratorJobType.Setter:
                case GeneratorJobType.GetterSetter:
                    GenerateProperty(job, member, inClass, sci);
                    break;

                case GeneratorJobType.BasicEvent:
                case GeneratorJobType.ComplexEvent:
                    latest = TemplateUtils.GetTemplateBlockMember(sci, TemplateUtils.GetBoundary("EventHandlers"));
                    if (latest == null)
                    {
                        if (ASContext.CommonSettings.MethodsGenerationLocations == MethodsGenerationLocations.AfterSimilarAccessorMethod)
                            latest = GetLatestMemberForFunction(inClass, GetDefaultVisibility(inClass), member);
                        if (latest == null)
                            latest = member;
                    }

                    position = sci.PositionFromLine(latest.LineTo + 1) - (sci.EOLMode == 0 ? 2 : 1);
                    sci.SetSel(position, position);
                    string type = contextParam;
                    if (job == GeneratorJobType.BasicEvent)
                        type = itemLabel.Contains("DataEvent") ? "DataEvent" : "Event";
                    GenerateEventHandler(contextToken, type, member, position, inClass);
                    break;

                case GeneratorJobType.Delegate:
                    position = sci.PositionFromLine(member.LineTo + 1) - ((sci.EOLMode == 0) ? 2 : 1);
                    sci.SetSel(position, position);
                    GenerateDelegateMethod(contextToken, member, position, inClass);
                    break;

                case GeneratorJobType.Constant:
                case GeneratorJobType.Variable:
                case GeneratorJobType.VariablePublic:
                    sci.BeginUndoAction();
                    try
                    {
                        GenerateVariableJob(job, sci, member, detach, inClass);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;

                case GeneratorJobType.Constructor:
                    sci.BeginUndoAction();
                    try
                    {
                        GenerateConstructorJob(sci, inClass);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;

                case GeneratorJobType.ChangeConstructorDecl:
                    sci.BeginUndoAction();
                    try
                    {
                        var parameters = data as IList<FunctionParameter>;
                        if (parameters == null) ChangeConstructorDecl(sci, inClass);
                        else ChangeConstructorDecl(sci, inClass, parameters);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;

                case GeneratorJobType.Function:
                case GeneratorJobType.FunctionPublic:
                    sci.BeginUndoAction();
                    try
                    {
                        GenerateFunctionJob(job, sci, member, detach, inClass);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;

                case GeneratorJobType.ImplementInterface:
                    ClassModel iType = ASContext.Context.ResolveType(contextParam, inClass.InFile ?? ASContext.Context.CurrentModel );
                    if (iType.IsVoid()) return;

                    latest = GetLatestMemberForFunction(inClass, Visibility.Public, null);
                    if (latest == null)
                        latest = FindLatest(0, 0, inClass, false, false);

                    if (latest == null)
                    {
                        position = GetBodyStart(inClass.LineFrom, inClass.LineTo, sci);
                        detach = false;
                    }
                    else
                        position = sci.PositionFromLine(latest.LineTo + 1) - ((sci.EOLMode == 0) ? 2 : 1);

                    sci.SetSel(position, position);
                    GenerateImplementation(iType, inClass, sci, detach);
                    break;

                case GeneratorJobType.MoveLocalUp:
                    sci.BeginUndoAction();
                    try
                    {
                        if (!RemoveLocalDeclaration(sci, contextMember)) return;

                        position = GetBodyStart(member.LineFrom, member.LineTo, sci);
                        sci.SetSel(position, position);

                        string varType = contextMember.Type;
                        if (varType == "") varType = null;

                        string template = TemplateUtils.GetTemplate("Variable");
                        template = TemplateUtils.ReplaceTemplateVariable(template, "Name", contextMember.Name);
                        template = TemplateUtils.ReplaceTemplateVariable(template, "Type", varType);
                        template = TemplateUtils.ReplaceTemplateVariable(template, "Modifiers", null);
                        template = TemplateUtils.ReplaceTemplateVariable(template, "Value", null);
                        template += "\n$(Boundary)";

                        lookupPosition += SnippetHelper.InsertSnippetText(sci, position, template);

                        sci.SetSel(lookupPosition, lookupPosition);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;

                case GeneratorJobType.PromoteLocal:
                    sci.BeginUndoAction();
                    try
                    {
                        if (!RemoveLocalDeclaration(sci, contextMember)) return;
                        
                        latest = GetLatestMemberForVariable(GeneratorJobType.Variable, inClass, GetDefaultVisibility(inClass), member);
                        if (latest == null) return;

                        position = FindNewVarPosition(sci, inClass, latest);
                        if (position <= 0) return;
                        sci.SetSel(position, position);

                        var newMember = new MemberModel
                        {
                            Name = contextMember.Name,
                            Type = contextMember.Type,
                            Access = GetDefaultVisibility(inClass)
                        };
                        if ((member.Flags & FlagType.Static) > 0) newMember.Flags |= FlagType.Access;

                        GenerateVariable(newMember, position, detach);
                        sci.SetSel(lookupPosition, lookupPosition);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;

                case GeneratorJobType.AddAsParameter:
                    sci.BeginUndoAction();
                    try
                    {
                        AddAsParameter(sci, member);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    
                    break;

                case GeneratorJobType.AddImport:
                    position = sci.CurrentPos;
                    if ((member.Flags & (FlagType.Class | FlagType.Enum | FlagType.Struct | FlagType.TypeDef)) == 0)
                    {
                        if (member.InFile == null) break;
                        member.Type = member.Name;
                    }
                    sci.BeginUndoAction();
                    try
                    {
                        int offset = InsertImport(member, true);
                        position += offset;
                        sci.SetSel(position, position);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;

                case GeneratorJobType.Class:
                    if (data is ASExpr) GenerateClass(sci, inClass, (ASExpr) data);
                    else GenerateClass(sci, inClass, sci.GetWordFromPosition(sci.CurrentPos));
                    break;

                case GeneratorJobType.ToString:
                    sci.BeginUndoAction();
                    try
                    {
                        GenerateToString(sci, inClass);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;

                case GeneratorJobType.FieldFromParameter:
                    sci.BeginUndoAction();
                    try
                    {
                        GenerateFieldFromParameter(sci, member, inClass, (Visibility)(((Hashtable)data)["scope"]));
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;

                case GeneratorJobType.AddInterfaceDef:
                    sci.BeginUndoAction();
                    try
                    {
                        AddInterfaceDefJob(sci, member, inClass, (String)data);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;

                case GeneratorJobType.ConvertToConst:
                    sci.BeginUndoAction();
                    try
                    {
                        ConvertToConst(sci, member, inClass, detach);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;

                case GeneratorJobType.ChangeMethodDecl:
                    sci.BeginUndoAction();
                    try
                    {
                        ChangeMethodDecl(sci, inClass);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;

                case GeneratorJobType.EventMetatag:
                    sci.BeginUndoAction();
                    try
                    {
                        EventMetatag(sci, inClass);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;

                case GeneratorJobType.AssignStatementToVar:
                    sci.BeginUndoAction();
                    try
                    {
                        if (data is StatementReturnType) AssignStatementToVar(sci, inClass, (StatementReturnType)data);
                        else AssignStatementToVar(sci, inClass);
                    }
                    finally
                    {
                        sci.EndUndoAction();
                    }
                    break;
            }
        }

        private static void GenerateProperty(GeneratorJobType job, MemberModel member, ClassModel inClass, ScintillaControl sci)
        {
            string name = GetPropertyNameFor(member);
            PropertiesGenerationLocations location = ASContext.CommonSettings.PropertiesGenerationLocation;
            var latest = TemplateUtils.GetTemplateBlockMember(sci, TemplateUtils.GetBoundary("AccessorsMethods"));
            if (latest != null)
            {
                location = PropertiesGenerationLocations.AfterLastPropertyDeclaration;
            }
            else
            {
                if (location == PropertiesGenerationLocations.AfterLastPropertyDeclaration)
                {
                    if (IsHaxe)
                    {
                        if (job == GeneratorJobType.Setter) latest = FindMember("get_" + (name ?? member.Name), inClass);
                        else if (job == GeneratorJobType.Getter) latest = FindMember("set_" + (name ?? member.Name), inClass);
                        if (latest == null) latest = FindLatest(FlagType.Function, 0, inClass, false, false);
                    }
                    else
                    {
                        if (job == GeneratorJobType.Getter || job == GeneratorJobType.Setter) latest = FindMember(name ?? member.Name, inClass);
                        if (latest == null) latest = FindLatest(FlagType.Getter | FlagType.Setter, 0, inClass, false, false);
                    }
                }
                else latest = member;
            }
            if (latest == null) return;

            sci.BeginUndoAction();
            try
            {
                if (IsHaxe)
                {
                    if (name == null) name = member.Name;
                    string args = "(default, default)";
                    if (job == GeneratorJobType.GetterSetter) args = "(get, set)";
                    else if (job == GeneratorJobType.Getter) args = "(get, null)";
                    else if (job == GeneratorJobType.Setter) args = "(default, set)";
                    MakeHaxeProperty(sci, member, args);
                }
                else
                {
                    if ((member.Access & Visibility.Public) > 0) // hide member
                    {
                        MakePrivate(sci, member, inClass);
                    }
                    if (name == null) // rename var with starting underscore
                    {
                        name = member.Name;
                        string newName = GetNewPropertyNameFor(member);
                        if (RenameMember(sci, member, newName)) member.Name = newName;
                    }
                }
                var startsWithNewLine = true;
                var endsWithNewLine = false;
                int atLine;
                if (location == PropertiesGenerationLocations.BeforeVariableDeclaration) atLine = latest.LineTo;
                else
                {
                    if (job == GeneratorJobType.Getter && (latest.Flags & (FlagType.Dynamic | FlagType.Function)) != 0)
                    {
                        atLine = latest.LineFrom;
                        var declaration = GetDeclarationAtLine(atLine - 1);
                        startsWithNewLine = declaration.Member != null;
                        endsWithNewLine = true;
                    }
                    else atLine = latest.LineTo + 1;
                }
                var position = sci.PositionFromLine(atLine) - ((sci.EOLMode == 0) ? 2 : 1);
                sci.SetSel(position, position);
                if (job == GeneratorJobType.GetterSetter)
                {
                    sci.SetSel(position, position);
                    GenerateGetterSetter(name, member, position);
                }
                else
                {
                    if (job == GeneratorJobType.Setter)
                    {
                        sci.SetSel(position, position);
                        GenerateSetter(name, member, position);
                    }
                    else if (job == GeneratorJobType.Getter)
                    {
                        sci.SetSel(position, position);
                        GenerateGetter(name, member, position, startsWithNewLine, endsWithNewLine);
                    }
                }
            }
            finally
            {
                sci.EndUndoAction();
            }
        }

        static void AssignStatementToVar(ScintillaControl sci, ClassModel inClass)
        {
            var currentLine = sci.CurrentLine;
            var returnType = GetStatementReturnType(sci, inClass, sci.GetLine(currentLine), sci.PositionFromLine(currentLine));
            AssignStatementToVar(sci, inClass, returnType);
        }
        static void AssignStatementToVar(ScintillaControl sci, ClassModel inClass, StatementReturnType returnType)
        {
            var ctx = inClass.InFile.Context;
            var resolve = returnType.resolve;
            List<ASResult> expressions = null;
            var context = resolve.Context;
            if (context != null)
            {
                // for example: typeof v, delete o[k], ...
                if (((ASGenerator) ctx.CodeGenerator).AssignStatementToVar(sci, inClass, context)) return;
                // for example: 1 + 1, 1 << 1, ...
                var operators = ctx.Features.ArithmeticOperators
                    .Select(it => it.ToString())
                    .Concat(ctx.Features.IncrementDecrementOperators)
                    .Concat(ctx.Features.BitwiseOperators)
                    .Concat(ctx.Features.BooleanOperators)
                    .ToHashSet();
                var sep = new[] {' '};
                var isValid = new Func<ASExpr, bool>((c) => c.Separator.Contains(' ') 
                    && c.Separator.Split(sep, StringSplitOptions.RemoveEmptyEntries).Any(it => operators.Contains(it.Trim())));
                if (operators.Contains(context.Separator) || operators.Contains(context.RightOperator) || isValid(context))
                {
                    var current = resolve;
                    context = current.Context;
                    expressions = new List<ASResult> {current};
                    var rop = false;
                    while (operators.Contains(context.Separator) || (rop = operators.Contains(context.RightOperator)) || isValid(context))
                    {
                        var position = rop ? context.PositionExpression : context.SeparatorPosition;
                        current = ASComplete.GetExpressionType(sci, position, false, true);
                        if (current == null || current.IsNull()) break;
                        expressions.Add(current);
                        context = current.Context;
                        rop = false;
                    }
                }
            }
            string type = null;
            int pos;
            if (expressions == null) pos = GetStartOfStatement(resolve);
            else
            {
                var last = expressions.Last();
                pos = last.Context.Separator != ";" ? last.Context.SeparatorPosition : last.Context.PositionExpression;
                var first = expressions.First();
                if (ctx.Features.BooleanOperators.Contains(first.Context.Separator)) type = ctx.Features.booleanKey;
            }
            if (type == null
                && resolve.Member == null && resolve.Type.Flags.HasFlag(FlagType.Class)
                && resolve.Type.Name != ctx.Features.booleanKey
                && resolve.Type.Name != "Function"
                && !string.IsNullOrEmpty(resolve.Path) && !char.IsDigit(resolve.Path[0]))
            {
                var expr = ASComplete.GetExpression(sci, returnType.position);
                if (string.IsNullOrEmpty(expr.WordBefore))
                {
                    var characters = ScintillaControl.Configuration.GetLanguage(ctx.Settings.LanguageId.ToLower()).characterclass.Characters;
                    if (resolve.Path.All(it => characters.Contains(it)))
                    {
                        if (inClass.InFile.haXe) type = "Class<Dynamic>";
                        else type = ctx.ResolveType("Class", resolve.InFile).QualifiedName;
                    }
                }
            }

            var word = returnType.word;
            if (!string.IsNullOrEmpty(word) && char.IsDigit(word[0])) word = null;
            string varname = null;
            if (string.IsNullOrEmpty(type) && !resolve.IsNull())
            {
                if (resolve.Member?.Type != null) type = resolve.Member.Type;
                else if (resolve.Type?.Name != null)
                {
                    type = resolve.Type.QualifiedName;
                    if (resolve.Type.IndexType == "*") type += ".<*>";
                    else if (resolve.Type.FullName.Contains(".<Vector>")) type = resolve.Type.FullName.Replace(".<Vector>", ".<Vector.<*>>");
                }

                if (resolve.Member?.Name != null) varname = GuessVarName(resolve.Member.Name, type);
            }
            if (!string.IsNullOrEmpty(word) && (string.IsNullOrEmpty(type) || Regex.IsMatch(type, "(<[^]]+>)"))) word = null;
            if (type == ctx.Features.voidKey) type = null;
            if (varname == null) varname = GuessVarName(word, type);
            if (varname != null && varname == word) varname = varname.Length == 1 ? varname + "1" : varname[0] + "";
            varname = AvoidKeyword(varname);
            
            string cleanType = null;
            if (type != null) cleanType = FormatType(GetShortType(type));
            var template = TemplateUtils.GetTemplate("AssignVariable");
            template = TemplateUtils.ReplaceTemplateVariable(template, "Name", varname);
            template = TemplateUtils.ReplaceTemplateVariable(template, "Type", cleanType);

            sci.SetSel(pos, pos);
            InsertCode(pos, template, sci);

            if (ctx.Settings.GenerateImports && type != null)
            {
                var inClassForImport = resolve.InClass ?? resolve.RelClass ?? inClass;
                var types = GetQualifiedTypes(new [] {type}, inClassForImport.InFile);
                AddImportsByName(types, sci.LineFromPosition(pos));
            }
        }

        protected virtual bool AssignStatementToVar(ScintillaControl sci, ClassModel inClass, ASExpr expr)
        {
            var ctx = inClass.InFile.Context;
            ClassModel type = null;
            if (expr.WordBefore == "typeof") type = ctx.ResolveType(ctx.Features.stringKey, inClass.InFile);
            else if(expr.WordBefore == "delete") type = ctx.ResolveType(ctx.Features.booleanKey, inClass.InFile);
            if (type == null) return false;
            var varName = GuessVarName(type.Name, type.Type);
            varName = AvoidKeyword(varName);
            var template = TemplateUtils.GetTemplate("AssignVariable");
            template = TemplateUtils.ReplaceTemplateVariable(template, "Name", varName);
            template = TemplateUtils.ReplaceTemplateVariable(template, "Type", type.Name);
            var pos = expr.WordBeforePosition;
            sci.SetSel(pos, pos);
            InsertCode(pos, template, sci);
            return true;
        }

        public static string AvoidKeyword(string word)
        {
            var features = ASContext.Context.Features;
            return features.accessKeywords.Contains(word)
                   || features.codeKeywords.Contains(word)
                   || features.declKeywords.Contains(word)
                   || features.typesKeywords.Contains(word)
                   || features.typesPreKeys.Contains(word)
                ? $"{word}Value"
                : word;
        }

        private static void EventMetatag(ScintillaControl sci, ClassModel inClass)
        {
            ASResult resolve = ASComplete.GetExpressionType(sci, sci.WordEndPosition(sci.CurrentPos, true));
            string line = sci.GetLine(inClass.LineFrom);
            int position = sci.PositionFromLine(inClass.LineFrom) + (line.Length - line.TrimStart().Length);

            string value = resolve.Member.Value;
            if (value != null)
            {
                if (value.StartsWith('\"'))
                {
                    value = value.Trim(new char[] { '"' });
                }
                else if (value.StartsWith('\''))
                {
                    value = value.Trim(new char[] { '\'' });
                }
            }
            else value = resolve.Member.Type;

            if (string.IsNullOrEmpty(value))
                return;

            Regex re1 = new Regex("'(?:[^'\\\\]|(?:\\\\\\\\)|(?:\\\\\\\\)*\\\\.{1})*'");
            Regex re2 = new Regex("\"(?:[^\"\\\\]|(?:\\\\\\\\)|(?:\\\\\\\\)*\\\\.{1})*\"");
            Match m1 = re1.Match(value);
            Match m2 = re2.Match(value);

            if (m1.Success || m2.Success)
            {
                Match m = null;
                if (m1.Success && m2.Success) m = m1.Index > m2.Index ? m2 : m1;
                else if (m1.Success) m = m1;
                else m = m2;
                value = value.Substring(m.Index + 1, m.Length - 2);
            }

            string template = TemplateUtils.GetTemplate("EventMetatag");
            template = TemplateUtils.ReplaceTemplateVariable(template, "Name", value);
            template = TemplateUtils.ReplaceTemplateVariable(template, "Type", contextParam);
            template += "\n$(Boundary)";

            AddLookupPosition();

            sci.CurrentPos = position;
            sci.SetSel(position, position);
            InsertCode(position, template, sci);
        }

        private static void ConvertToConst(ScintillaControl sci, MemberModel member, ClassModel inClass, bool detach)
        {
            String suggestion = "NEW_CONST";
            String label = TextHelper.GetString("ASCompletion.Label.ConstName");
            String title = TextHelper.GetString("ASCompletion.Title.ConvertToConst");

            Hashtable info = new Hashtable();
            info["suggestion"] = suggestion;
            info["label"] = label;
            info["title"] = title;
            DataEvent de = new DataEvent(EventType.Command, "ProjectManager.LineEntryDialog", info);
            EventManager.DispatchEvent(null, de);
            if (!de.Handled)
                return;
            
            suggestion = (string)info["suggestion"];

            int position = sci.CurrentPos;
            int style = sci.BaseStyleAt(position);
            MemberModel latest = null;

            int wordPosEnd = position + 1;
            int wordPosStart = position;

            while (sci.BaseStyleAt(wordPosEnd) == style) wordPosEnd++;
            while (sci.BaseStyleAt(wordPosStart - 1) == style) wordPosStart--;
            
            sci.SetSel(wordPosStart, wordPosEnd);
            string word = sci.SelText;
            sci.ReplaceSel(suggestion);
            
            if (member == null)
            {
                detach = false;
                lookupPosition = -1;
                position = sci.WordStartPosition(sci.CurrentPos, true);
                sci.SetSel(position, sci.WordEndPosition(position, true));
            }
            else
            {
                latest = GetLatestMemberForVariable(GeneratorJobType.Constant, inClass, 
                    Visibility.Private, new MemberModel("", "", FlagType.Static, 0));
                if (latest != null)
                {
                    position = FindNewVarPosition(sci, inClass, latest);
                }
                else
                {
                    position = GetBodyStart(inClass.LineFrom, inClass.LineTo, sci);
                    detach = false;
                }
                if (position <= 0) return;
                sci.SetSel(position, position);
            }

            MemberModel m = NewMember(suggestion, member, FlagType.Variable | FlagType.Constant | FlagType.Static, GetDefaultVisibility(inClass));

            var features = ASContext.Context.Features;

            switch (style)
            {
                case 4:
                    m.Type = features.numberKey;
                    break;
                case 6:
                case 7:
                    m.Type = features.stringKey;
                    break;
            }

            m.Value = word;
            GenerateVariable(m, position, detach);
        }

        private static void ChangeMethodDecl(ScintillaControl sci, ClassModel inClass)
        {
            int wordPos = sci.WordEndPosition(sci.CurrentPos, true);
            var parameters = ParseFunctionParameters(sci, wordPos);

            ASResult funcResult = ASComplete.GetExpressionType(sci, sci.WordEndPosition(sci.CurrentPos, true));
            if (funcResult == null || funcResult.Member == null) return;
            if (funcResult.InClass != null && !funcResult.InClass.Equals(inClass))
            {
                AddLookupPosition();
                lookupPosition = -1;

                ASContext.MainForm.OpenEditableDocument(funcResult.InClass.InFile.FileName, true);
                sci = ASContext.CurSciControl;
                var fileModel = ASContext.Context.GetCodeModel(sci.Text);
                foreach (ClassModel cm in fileModel.Classes)
                {
                    if (cm.QualifiedName.Equals(funcResult.InClass.QualifiedName))
                    {
                        funcResult.InClass = cm;
                        break;
                    }
                }
                inClass = funcResult.InClass;

                ASContext.Context.UpdateContext(inClass.LineFrom);
            }

            MemberList members = inClass.Members;
            foreach (MemberModel m in members)
            {
                if (m.Equals(funcResult.Member))
                {
                    funcResult.Member = m;
                    break;
                }
            }

            ChangeDecl(sci, inClass, funcResult.Member, parameters);
        }

        private static void ChangeConstructorDecl(ScintillaControl sci, ClassModel inClass)
        {
            var position = sci.WordEndPosition(sci.CurrentPos, true);
            var parameters = ParseFunctionParameters(sci, position);
            ChangeConstructorDecl(sci, inClass, parameters);
        }

        private static void ChangeConstructorDecl(ScintillaControl sci, ClassModel inClass, IList<FunctionParameter> parameters)
        {
            var funcResult = ASComplete.GetExpressionType(sci, sci.WordEndPosition(sci.CurrentPos, true));
            if (funcResult == null || funcResult.Type == null) return;
            if (!funcResult.Type.Equals(inClass))
            {
                AddLookupPosition();
                lookupPosition = -1;

                ASContext.MainForm.OpenEditableDocument(funcResult.Type.InFile.FileName, true);
                sci = ASContext.CurSciControl;
                var fileModel = ASContext.Context.GetFileModel(funcResult.Type.InFile.FileName);
                foreach (ClassModel cm in fileModel.Classes)
                {
                    if (cm.QualifiedName.Equals(funcResult.Type.QualifiedName))
                    {
                        funcResult.Type = cm;
                        break;
                    }
                }

                inClass = funcResult.Type;
                ASContext.Context.UpdateContext(inClass.LineFrom);
            }

            foreach (MemberModel m in inClass.Members)
            {
                if ((m.Flags & FlagType.Constructor) > 0)
                {
                    funcResult.Member = m;
                    break;
                }
            }

            if (funcResult.Member == null) return;
            if (!string.IsNullOrEmpty(ASContext.Context.Features.ConstructorKey)) funcResult.Member.Name = ASContext.Context.Features.ConstructorKey;

            ChangeDecl(sci, inClass, funcResult.Member, parameters);
        }

        private static void ChangeDecl(ScintillaControl sci, ClassModel inClass, MemberModel memberModel, IList<FunctionParameter> functionParameters)
        {
            bool paramsDiffer = false;
            if (memberModel.Parameters != null)
            {
                // check that parameters have one and the same type
                if (memberModel.Parameters.Count == functionParameters.Count)
                {
                    if (functionParameters.Count > 0)
                    {
                        List<MemberModel> parameters = memberModel.Parameters;
                        for (int i = 0; i < parameters.Count; i++)
                        {
                            MemberModel p = parameters[i];
                            if (p.Type != functionParameters[i].paramType)
                            {
                                paramsDiffer = true;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    paramsDiffer = true;
                }
            }
            // check that parameters count differs
            else if (functionParameters.Count != 0)
            {
                paramsDiffer = true;
            }

            if (paramsDiffer)
            {
                int app = 0;
                List<MemberModel> newParameters = new List<MemberModel>();
                List<MemberModel> existingParameters = memberModel.Parameters;
                for (int i = 0; i < functionParameters.Count; i++)
                {
                    FunctionParameter p = functionParameters[i];
                    if (existingParameters != null
                        && existingParameters.Count > (i - app)
                        && existingParameters[i - app].Type == p.paramType)
                    {
                        newParameters.Add(existingParameters[i - app]);
                    }
                    else
                    {
                        if (existingParameters != null && existingParameters.Count < functionParameters.Count)
                        {
                            app++;
                        }
                        newParameters.Add(new MemberModel(AvoidKeyword(p.paramName), p.paramType, FlagType.ParameterVar, 0));
                    }
                }
                memberModel.Parameters = newParameters;

                int posStart = sci.PositionFromLine(memberModel.LineFrom);
                int posEnd = sci.LineEndPosition(memberModel.LineTo);
                sci.SetSel(posStart, posEnd);
                string selectedText = sci.SelText;
                Regex rStart = new Regex(@"\s{1}" + memberModel.Name + @"\s*\(([^\)]*)\)(\s*:\s*([^({{|\n|\r|\s|;)]+))?");
                Match mStart = rStart.Match(selectedText);
                if (!mStart.Success)
                {
                    return;
                }

                int start = mStart.Index + posStart;
                int end = start + mStart.Length;

                sci.SetSel(start, end);

                string decl = TemplateUtils.ToDeclarationString(memberModel, TemplateUtils.GetTemplate("MethodDeclaration"));
                InsertCode(sci.CurrentPos, "$(Boundary) " + decl, sci);

                // add imports to function argument types
                if (ASContext.Context.Settings.GenerateImports && functionParameters.Count > 0)
                {
                    var l = new string[functionParameters.Count];
                    for (var i = 0; i < functionParameters.Count; i++)
                    {
                        l[i] = functionParameters[i].paramQualType;
                    }
                    var types = GetQualifiedTypes(l, inClass.InFile);
                    start += AddImportsByName(types, sci.LineFromPosition(end));
                }

                sci.SetSel(start, start);
            }
        }

        private static void AddAsParameter(ScintillaControl sci, MemberModel member)
        {
            if (!RemoveLocalDeclaration(sci, contextMember)) return;

            int posStart = sci.PositionFromLine(member.LineFrom);
            int posEnd = sci.LineEndPosition(member.LineTo);
            sci.SetSel(posStart, posEnd);
            string selectedText = sci.SelText;
            Regex rStart = new Regex(@"\s{1}" + member.Name + @"\s*\(([^\)]*)\)(\s*:\s*([^({{|\n|\r|\s|;)]+))?");
            Match mStart = rStart.Match(selectedText);
            if (!mStart.Success)
                return;

            int start = mStart.Index + posStart + 1;
            int end = mStart.Index + posStart + mStart.Length;

            sci.SetSel(start, end);

            MemberModel memberCopy = (MemberModel) member.Clone();

            if (memberCopy.Parameters == null)
                memberCopy.Parameters = new List<MemberModel>();

            memberCopy.Parameters.Add(contextMember);

            string template = TemplateUtils.ToDeclarationString(memberCopy, TemplateUtils.GetTemplate("MethodDeclaration"));
            InsertCode(start, template, sci);

            int currPos = sci.LineEndPosition(sci.CurrentLine);

            sci.SetSel(currPos, currPos);
            sci.CurrentPos = currPos;
        }

        private static void AddInterfaceDefJob(ScintillaControl sci, MemberModel member, ClassModel inClass, string interf)
        {
            var context = ASContext.Context;
            ClassModel aType = context.ResolveType(interf, context.CurrentModel);
            if (aType.IsVoid()) return;
            var fileModel = ASContext.Context.GetFileModel(aType.InFile.FileName);
            foreach (ClassModel cm in fileModel.Classes)
            {
                if (cm.QualifiedName.Equals(aType.QualifiedName))
                {
                    aType = cm;
                    break;
                }
            }

            string template;
            if ((member.Flags & FlagType.Getter) > 0)
            {
                template = TemplateUtils.GetTemplate("IGetter");
            }
            else if ((member.Flags & FlagType.Setter) > 0)
            {
                template = TemplateUtils.GetTemplate("ISetter");
            }
            else template = TemplateUtils.GetTemplate("IFunction");

            ASContext.MainForm.OpenEditableDocument(aType.InFile.FileName, true);
            sci = ASContext.CurSciControl;

            MemberModel latest = GetLatestMemberForFunction(aType, Visibility.Default, new MemberModel());
            int position;
            if (latest == null)
            {
                position = GetBodyStart(aType.LineFrom, aType.LineTo, sci);
            }
            else
            {
                position = sci.PositionFromLine(latest.LineTo + 1) - ((sci.EOLMode == 0) ? 2 : 1);
                template = NewLine + template;
            }
            sci.SetSel(position, position);
            sci.CurrentPos = position;
            template = TemplateUtils.ReplaceTemplateVariable(template, "Type", member.Type ?? context.Features.voidKey);
            template = TemplateUtils.ToDeclarationString(member, template);
            template = TemplateUtils.ReplaceTemplateVariable(template, "BlankLine", NewLine);
            template = TemplateUtils.ReplaceTemplateVariable(template, "Void", context.Features.voidKey);

            if (context.Settings.GenerateImports)
            {
                List<string> importsList = new List<string>();
                List<MemberModel> parms = member.Parameters;
                if (parms != null && parms.Count > 0)
                {
                    importsList.AddRange(from t in parms where t.Type != null select t.Type);
                }
                if (member.Type != null) importsList.Add(member.Type);
                if (importsList.Count > 0)
                {
                    var types = GetQualifiedTypes(importsList, inClass.InFile);
                    position += AddImportsByName(types, sci.LineFromPosition(position));
                }
            }

            sci.SetSel(position, position);
            sci.CurrentPos = position;

            InsertCode(position, template, sci);
        }

        private static void GenerateFieldFromParameter(ScintillaControl sci, MemberModel member, ClassModel inClass, Visibility scope)
        {
            int funcBodyStart = GetBodyStart(member.LineFrom, member.LineTo, sci, false);
            int fbsLine = sci.LineFromPosition(funcBodyStart);
            int endPos = sci.LineEndPosition(member.LineTo);

            sci.SetSel(funcBodyStart, endPos);
            string body = sci.SelText;
            string trimmed = body.TrimStart();

            Match m = reSuperCall.Match(trimmed);
            if (m.Success && m.Index == 0)
            {
                funcBodyStart = GetEndOfStatement(funcBodyStart + (body.Length - trimmed.Length), endPos, sci);
            }

            funcBodyStart = GetOrSetPointOfInsertion(funcBodyStart, endPos, fbsLine, sci);

            sci.SetSel(funcBodyStart, funcBodyStart);
            sci.CurrentPos = funcBodyStart;

            bool isVararg = false;
            string paramName = contextMember.Name;
            var paramType = contextMember.Type;
            if (paramName.StartsWithOrdinal("..."))
            {
                paramName = paramName.TrimStart(' ', '.');
                isVararg = true;
            }
            else if (inClass.InFile.haXe && paramName.StartsWithOrdinal("?"))
            {
                paramName = paramName.Remove(0, 1);
                if (!string.IsNullOrEmpty(paramType) && !paramType.StartsWith("Null<")) paramType = $"Null<{paramType}>";
            }
            string varName = paramName;
            string scopedVarName = varName;

            if ((scope & Visibility.Public) > 0)
            {
                if ((member.Flags & FlagType.Static) > 0)
                    scopedVarName = inClass.Name + "." + varName;
                else
                    scopedVarName = "this." + varName;
            }
            else
            {
                if (ASContext.CommonSettings.PrefixFields.Length > 0 && !varName.StartsWithOrdinal(ASContext.CommonSettings.PrefixFields))
                {
                    scopedVarName = varName = ASContext.CommonSettings.PrefixFields + varName;
                }

                if (ASContext.CommonSettings.GenerateScope || ASContext.CommonSettings.PrefixFields == "")
                {
                    if ((member.Flags & FlagType.Static) > 0)
                        scopedVarName = inClass.Name + "." + varName;
                    else
                        scopedVarName = "this." + varName;
                }
            }

            string template = TemplateUtils.GetTemplate("FieldFromParameter");
            template = TemplateUtils.ReplaceTemplateVariable(template, "Name", scopedVarName);
            template = TemplateUtils.ReplaceTemplateVariable(template, "Value", paramName);
            template += "\n$(Boundary)";

            SnippetHelper.InsertSnippetText(sci, funcBodyStart, template);

            //TODO: We also need to check parent classes!!!
            MemberList classMembers = inClass.Members;
            foreach (MemberModel classMember in classMembers)
                if (classMember.Name.Equals(varName))
                {
                    ASContext.Panel.RestoreLastLookupPosition();
                    return;
                }

            MemberModel latest = GetLatestMemberForVariable(GeneratorJobType.Variable, inClass, GetDefaultVisibility(inClass), new MemberModel());
            if (latest == null) return;

            int position = FindNewVarPosition(sci, inClass, latest);
            if (position <= 0) return;
            sci.SetSel(position, position);
            sci.CurrentPos = position;

            MemberModel mem = NewMember(varName, member, FlagType.Variable, scope);
            if (isVararg) mem.Type = "Array";
            else mem.Type = paramType;

            GenerateVariable(mem, position, true);
            ASContext.Panel.RestoreLastLookupPosition();
        }

        /// <summary>
        /// Tries to get the best position inside a code block, delimited by { and }, to add new code, inserting new lines if needed.
        /// </summary>
        /// <param name="lineFrom">The line inside the Scintilla document where the owner member of the body starts</param>
        /// <param name="lineTo">The line inside the Scintilla document where the owner member of the body ends</param>
        /// <param name="sci">The Scintilla control containing the document</param>
        /// <returns>The position inside the scintilla document, or -1 if not suitable position was found</returns>
        public static int GetBodyStart(int lineFrom, int lineTo, ScintillaControl sci)
        {
            return GetBodyStart(lineFrom, lineTo, sci, true);
        }

        /// <summary>
        /// Tries to get the start position of a code block, delimited by { and }
        /// </summary>
        /// <param name="lineFrom">The line inside the Scintilla document where the owner member of the body starts</param>
        /// <param name="lineTo">The line inside the Scintilla document where the owner member of the body ends</param>
        /// <param name="sci">The Scintilla control containing the document</param>
        /// <param name="needsPointOfInsertion">If true looks for the position to add new code, inserting new lines if needed</param>
        /// <returns>The position inside the Scintilla document, or -1 if not suitable position was found</returns>
        public static int GetBodyStart(int lineFrom, int lineTo, ScintillaControl sci, bool needsPointOfInsertion)
        {
            int posStart = sci.PositionFromLine(lineFrom);
            int posEnd = sci.LineEndPosition(lineTo);

            int funcBodyStart = -1;

            int genCount = 0, parCount = 0;
            for (int i = posStart; i <= posEnd; i++)
            {
                char c = (char)sci.CharAt(i);

                if (c == '{')
                {
                    int style = sci.BaseStyleAt(i);
                    if (ASComplete.IsCommentStyle(style) || ASComplete.IsLiteralStyle(style) || genCount > 0 || parCount > 0)
                        continue;
                    funcBodyStart = i;
                    break;
                }
                else if (c == '<')
                {
                    int style = sci.BaseStyleAt(i);
                    if (style == 10)
                        genCount++;
                }
                else if (c == '>')
                {
                    int style = sci.BaseStyleAt(i);
                    if (style == 10 && genCount > 0)
                        genCount--;
                }
                else if (c == '(')
                {
                    int style = sci.BaseStyleAt(i);
                    if (style == 10)
                        parCount++;
                }
                else if (c == ')')
                {
                    int style = sci.BaseStyleAt(i);
                    if (style == 10)
                        parCount--;
                }
            }

            if (funcBodyStart == -1)
                return -1;

            if (needsPointOfInsertion)
            {
                int ln = sci.LineFromPosition(funcBodyStart);

                funcBodyStart++;
                return GetOrSetPointOfInsertion(funcBodyStart, posEnd, ln, sci);
            }

            return funcBodyStart + 1;
        }

        [Obsolete(message: "Please use ASGenerator.GetStartOfStatement(expr) instead of ASGenerator.GetStartOfStatement(sci, statementEnd, expr)")]
        public static int GetStartOfStatement(ScintillaControl sci, int statementEnd, ASResult expr) => GetStartOfStatement(expr);

        public static int GetStartOfStatement(ASResult expr)
        {
            if (expr.Type != null)
            {
                var wordBefore = expr.Context.WordBefore;
                if (wordBefore != null) return expr.Context.WordBeforePosition;
            }
            return expr.Context.PositionExpression;
        }

        /// <summary>
        /// Tries to get the best position after a statement, to add new code, inserting new lines if needed.
        /// </summary>
        /// <param name="startPos">The position inside the Scintilla document where the statement starts</param>
        /// <param name="endPos">The position inside the Scintilla document where the owner member of the statement ends</param>
        /// <param name="sci">The Scintilla control containing the document</param>
        /// <returns>The position inside the Scintilla document</returns>
        /// <remarks>For now internal because for the current use we don't need to detect a lot of cases! use with caution!</remarks>
        public static int GetEndOfStatement(int startPos, int endPos, ScintillaControl sci)
        {
            int groupCount = 0;
            int brCount = 0;
            int statementEnd = startPos;
            sci.Colourise(0, -1);
            while (statementEnd < endPos)
            {
                if (sci.PositionIsOnComment(statementEnd) || sci.PositionIsInString(statementEnd))
                {
                    statementEnd++;
                    continue;
                }
                char c = (char)sci.CharAt(statementEnd++);
                bool endOfStatement = false;
                switch (c)
                {
                    case '\r':
                    case '\n':
                        endOfStatement = groupCount == 0 && brCount == 0;
                        break;
                    case ';':
                        endOfStatement = brCount == 0; // valid or invalid end of statement
                        break;
                    case '(':
                    case '[':
                        groupCount++;
                        break;
                    case '{':
                        brCount++;
                        break;
                    case ')':
                    case ']':
                        groupCount--;
                        break;
                    case '}':
                        brCount--;
                        break;
                }

                if (endOfStatement) break;
            }

            return statementEnd;
        }

        /// <summary>
        /// Looks for the best next position to insert new code, inserting new lines if needed
        /// </summary>
        /// <param name="startPos">The position inside the Scintilla document to start looking for the insertion position</param>
        /// <param name="endPos">The end position inside the Scintilla document</param>
        /// <param name="baseLine">The line inside the document to use as the base for the indentation level and detect if the desired point
        /// matches the end line</param>
        /// <param name="sci">The ScintillaControl where our document resides</param>
        /// <returns>The insertion point position</returns>
        private static int GetOrSetPointOfInsertion(int startPos, int endPos, int baseLine, ScintillaControl sci)
        {
            char[] characterClass = { ' ', '\r', '\n', '\t' };
            int nCount = 0;
            int extraLine = 1;

            int initialLn = sci.LineFromPosition(startPos);
            int baseIndent = sci.GetLineIndentation(baseLine);

            bool found = false;
            while (startPos <= endPos)
            {
                char c = (char)sci.CharAt(startPos);
                if (Array.IndexOf(characterClass, c) == -1)
                {
                    int endLn = sci.LineFromPosition(startPos);
                    if (endLn == baseLine || endLn == initialLn)
                    {
                        sci.InsertText(startPos, sci.NewLineMarker);
                        // Do we want to set the line indentation no matter what? {\r\t\t\t\r} -> {\r\t\r}
                        // Better results in most cases, but maybe highly unwanted in others?
                        sci.SetLineIndentation(++endLn, baseIndent + sci.Indent);
                        startPos = sci.LineIndentPosition(endLn);
                    }
                    if (c == '}')
                    {
                        sci.InsertText(startPos, sci.NewLineMarker);
                        sci.SetLineIndentation(endLn + 1, baseIndent);
                        // In relation with previous comment... we'll reinden this one: {\r} -> {\r\t\r}
                        if (sci.GetLineIndentation(endLn) <= baseIndent)
                        {
                            sci.SetLineIndentation(endLn, baseIndent + sci.Indent);
                            startPos = sci.LineIndentPosition(endLn);
                        }
                    }
                    found = true;
                    break;
                }
                else if (sci.EOLMode == 1 && c == '\r' && (++nCount) > extraLine)
                {
                    found = true;
                    break;
                }
                else if (c == '\n' && (++nCount) > extraLine)
                {
                    if (sci.EOLMode != 2)
                    {
                        startPos--;
                    }
                    found = true;
                    break;
                }
                startPos++;
            }

            if (!found) startPos--;

            return startPos;
        }

        private static void GenerateToString(ScintillaControl sci, ClassModel inClass)
        {
            MemberModel resultMember = new MemberModel("toString", ASContext.Context.Features.stringKey, FlagType.Function, Visibility.Public);

            bool isOverride = false;
            inClass.ResolveExtends();
            if (inClass.Extends != null)
            {
                ClassModel aType = inClass.Extends;
                while (!aType.IsVoid() && aType.QualifiedName != "Object")
                {
                    foreach (MemberModel method in aType.Members)
                    {
                        if (method.Name == "toString")
                        {
                            isOverride = true;
                            break;
                        }
                    }
                    if (isOverride)
                    {
                        resultMember.Flags |= FlagType.Override;
                        break;
                    }
                    // interface inheritance
                    aType = aType.Extends;
                }
            }
            MemberList members = inClass.Members;
            StringBuilder membersString = new StringBuilder();
            int len = 0;
            foreach (MemberModel m in members)
            {
                if (((m.Flags & FlagType.Variable) > 0 || (m.Flags & FlagType.Getter) > 0)
                    && (m.Access & Visibility.Public) > 0
                    && (m.Flags & FlagType.Constant) == 0)
                {
                    var oneMembersString = new StringBuilder();
                    oneMembersString.Append(" ").Append(m.Name).Append("=\" + ").Append(m.Name).Append(" + ");
                    membersString.Append(oneMembersString);
                    len += oneMembersString.Length;
                    if (len > 80)
                    {
                        len = 0;
                        membersString.Append("\n\t\t\t\t");
                    }
                    membersString.Append("\"");
                }
            }


            string template = TemplateUtils.GetTemplate("ToString");
            string result = TemplateUtils.ToDeclarationWithModifiersString(resultMember, template);
            result = TemplateUtils.ReplaceTemplateVariable(result, "Body", "\"[" + inClass.Name + membersString + "]\"");

            InsertCode(sci.CurrentPos, result, sci);
        }

        private static void GenerateVariableJob(GeneratorJobType job, ScintillaControl sci, MemberModel member, bool detach, ClassModel inClass)
        {
            var wordStartPos = sci.WordStartPosition(sci.CurrentPos, true);
            Visibility visibility = job.Equals(GeneratorJobType.Variable) ? GetDefaultVisibility(inClass) : Visibility.Public;
            // evaluate, if the variable (or constant) should be generated in other class
            ASResult varResult = ASComplete.GetExpressionType(sci, sci.WordEndPosition(sci.CurrentPos, true));
            if (member != null && ASContext.CommonSettings.GenerateScope && !varResult.Context.Value.Contains(ASContext.Context.Features.dot)) AddExplicitScopeReference(sci, inClass, member);
            int contextOwnerPos = GetContextOwnerEndPos(sci, sci.WordStartPosition(sci.CurrentPos, true));
            MemberModel isStatic = new MemberModel();
            if (contextOwnerPos != -1)
            {
                ASResult contextOwnerResult = ASComplete.GetExpressionType(sci, contextOwnerPos);
                if (contextOwnerResult != null
                    && (contextOwnerResult.Member == null || (contextOwnerResult.Member.Flags & FlagType.Constructor) > 0)
                    && contextOwnerResult.Type != null)
                {
                    isStatic.Flags |= FlagType.Static;
                }
            }
            else if (member != null && (member.Flags & FlagType.Static) > 0)
            {
                isStatic.Flags |= FlagType.Static;
            }

            ASResult returnType = null;
            int lineNum = sci.CurrentLine;
            string line = sci.GetLine(lineNum);
            
            if (Regex.IsMatch(line, "\\b" + Regex.Escape(contextToken) + "\\("))
            {
                returnType = new ASResult();
                returnType.Type = ASContext.Context.ResolveType("Function", null);
            }
            else
            {
                var m = Regex.Match(line, @"=\s*[^;\n\r}}]+");
                if (m.Success)
                {
                    int posLineStart = sci.PositionFromLine(lineNum);
                    if (posLineStart + m.Index >= sci.CurrentPos)
                    {
                        line = line.Substring(m.Index);
                        StatementReturnType rType = GetStatementReturnType(sci, inClass, line, posLineStart + m.Index);
                        if (rType != null)
                        {
                            returnType = rType.resolve;
                        }
                    }
                }
            }
            bool isOtherClass = false;
            if (varResult.RelClass != null && !varResult.RelClass.IsVoid() && !varResult.RelClass.Equals(inClass))
            {
                AddLookupPosition();
                lookupPosition = -1;

                ASContext.MainForm.OpenEditableDocument(varResult.RelClass.InFile.FileName, false);
                sci = ASContext.CurSciControl;
                isOtherClass = true;
                var fileModel = ASContext.Context.GetCodeModel(sci.Text);
                foreach (ClassModel cm in fileModel.Classes)
                {
                    if (cm.QualifiedName.Equals(varResult.RelClass.QualifiedName))
                    {
                        varResult.RelClass = cm;
                        break;
                    }
                }
                inClass = varResult.RelClass;

                ASContext.Context.UpdateContext(inClass.LineFrom);
            }

            var latest = GetLatestMemberForVariable(job, inClass, visibility, isStatic);
            var position = 0;
            // if we generate variable in current class..
            if (!isOtherClass && member == null)
            {
                detach = false;
                lookupPosition = -1;
                position = sci.WordStartPosition(sci.CurrentPos, true);
                sci.SetSel(position, sci.WordEndPosition(position, true));
            }
            else // if we generate variable in another class
            {
                if (latest != null)
                {
                    position = FindNewVarPosition(sci, inClass, latest);
                }
                else
                {
                    position = GetBodyStart(inClass.LineFrom, inClass.LineTo, sci);
                    detach = false;
                }
                if (position <= 0) return;
                sci.SetSel(position, position);
            }

            // if this is a constant, we assign a value to constant
            string returnTypeStr = null;
            if (job == GeneratorJobType.Constant && returnType == null) isStatic.Flags |= FlagType.Static;
            else if (returnType != null)
            {
                if (returnType.Member != null)
                {
                    if (returnType.Member.Type != ASContext.Context.Features.voidKey)
                        returnTypeStr = returnType.Member.Type;
                }
                else if (returnType.Type != null) returnTypeStr = returnType.Type.Name;
                if (ASContext.Context.Settings.GenerateImports)
                {
                    ClassModel inClassForImport;
                    if (returnType.InClass != null) inClassForImport = returnType.InClass;
                    else if (returnType.RelClass != null) inClassForImport = returnType.RelClass;
                    else inClassForImport = inClass;
                    List<string> imports = null;
                    if (returnType.Member != null)
                    {
                        if (returnType.Member.Type != ASContext.Context.Features.voidKey) imports = new List<string> {returnType.Member.Type};
                    }
                    else if (returnType.Type != null) imports = new List<string> {returnType.Type.QualifiedName};
                    if (imports != null)
                    {
                        var types = GetQualifiedTypes(imports, inClassForImport.InFile);
                        position += AddImportsByName(types, sci.LineFromPosition(position));
                        sci.SetSel(position, position);
                    }
                }
            }

            var kind = job.Equals(GeneratorJobType.Constant) ? FlagType.Constant : FlagType.Variable;
            var newMember = NewMember(contextToken, isStatic, kind, visibility);
            if (returnTypeStr != null) newMember.Type = returnTypeStr;
            else
            {
                var pos = wordStartPos;
                var index = ASComplete.FindParameterIndex(sci, ref pos);
                if (pos != -1)
                {
                    var expr = ASComplete.GetExpressionType(sci, pos);
                    if (expr?.Member?.Parameters.Count > 0) newMember.Type = expr.Member.Parameters[index].Type;
                }
            }
            if (job == GeneratorJobType.Constant && returnType == null)
            {
                if (string.IsNullOrEmpty(newMember.Type)) newMember.Type = "String = \"" + Camelize(contextToken) + "\"";
                else
                {
                    var value = ASContext.Context.GetDefaultValue(newMember.Type);
                    if (!string.IsNullOrEmpty(value)) newMember.Type += " = " + value;
                }
            }
            GenerateVariable(newMember, position, detach);
        }

        private static int GetContextOwnerEndPos(ScintillaControl sci, int wordStartPos)
        {
            int pos = wordStartPos - 1;
            bool dotFound = false;
            while (pos > 0)
            {
                char c = (char) sci.CharAt(pos);
                if (c == '.' && !dotFound) dotFound = true;
                else if (c == '\t' || c == '\n' || c == '\r' || c == ' ') { /* skip */ }
                else return dotFound ? pos + 1 : -1;
                pos--;
            }
            return pos;
        }

        public static string Capitalize(string name)
        {
            return !string.IsNullOrEmpty(name) ? Char.ToUpper(name[0]) + name.Substring(1) : name;
        }

        public static string Camelize(string name)
        {
            name = name.Trim(new char[] { '\'', '"' });
            string[] parts = name.ToLower().Split('_');
            string result = "";
            foreach (string part in parts)
            {
                if (result.Length > 0)
                    result += Capitalize(part);
                else result = part;
            }
            return result;
        }

        public static List<FunctionParameter> ParseFunctionParameters(ScintillaControl sci, int p)
        {
            List<FunctionParameter> prms = new List<FunctionParameter>();
            StringBuilder sb = new StringBuilder();
            List<ASResult> types = new List<ASResult>();
            bool isFuncStarted = false;
            bool doBreak = false;
            bool writeParam = false;
            int subClosuresCount = 0;
            var arrCount = 0;
            IASContext ctx = ASContext.Context;
            char[] charsToTrim = {' ', '\t', '\r', '\n'};
            int counter = sci.TextLength; // max number of chars in parameters line (to avoid infinitive loop)
            string characterClass = ScintillaControl.Configuration.GetLanguage(sci.ConfigurationLanguage).characterclass.Characters;

            while (p < counter && !doBreak)
            {
                if (sci.PositionIsOnComment(p))
                {
                    p++;
                    continue;
                }
                if (sci.PositionIsInString(p))
                {
                    sb.Append((char)sci.CharAt(p++));
                    continue;
                }
                var c = (char) sci.CharAt(p++);
                ASResult result;
                if (c == '(' && !isFuncStarted)
                {
                    if (sb.ToString().Trim(charsToTrim).Length == 0)
                    {
                        isFuncStarted = true;
                    }
                    else break;
                }
                else if (c == ';' && !isFuncStarted) break;
                else if (c == ')' && isFuncStarted && subClosuresCount == 0)
                {
                    isFuncStarted = false;
                    writeParam = true;
                    doBreak = true;
                }
                else if (c == '(' || c == '[' || c == '{')
                {
                    if (c == '[') arrCount++;
                    subClosuresCount++;
                    sb.Append(c);
                }
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (c == ']') arrCount--;
                    subClosuresCount--;
                    sb.Append(c);
                    if (subClosuresCount == 0)
                    {
                        if (c == ']')
                        {
                            if (arrCount == 0)
                            {
                                var cNext = sci.CharAt(p);
                                if (cNext != '[' && cNext != '.')
                                {
                                    if (!sb.ToString().Contains("<"))
                                    {
                                        result = ASComplete.GetExpressionType(sci, p);
                                        if (result.Type != null) result.Member = null;
                                        else result.Type = ctx.ResolveType(ctx.Features.arrayKey, null);
                                        types.Insert(0, result);
                                    }
                                    writeParam = true;
                                }
                            }
                        }
                    }
                }
                else if (c == ',' && subClosuresCount == 0) writeParam = true;
                else if (isFuncStarted) sb.Append(c);
                else if (characterClass.Contains(c)) doBreak = true;

                if (writeParam)
                {
                    writeParam = false;
                    string trimmed = sb.ToString().Trim(charsToTrim);
                    var trimmedLength = trimmed.Length;
                    if (trimmedLength > 0)
                    {
                        var last = trimmed[trimmedLength - 1];
                        var type = last == '}' && trimmed.StartsWith(ctx.Features.functionKey)
                                   ? ctx.ResolveType("Function", null)
                                   : ctx.ResolveToken(trimmed, ctx.CurrentModel);
                        if (!type.IsVoid()) result = new ASResult {Type = type};
                        else result = ASComplete.GetExpressionType(sci, p - 1, false, true);
                        if (result != null && !result.IsNull())
                        {
                            if (characterClass.Contains(last))
                            {
                                types.Insert(0, result);
                            }
                            else
                            {
                                types.Add(result);
                            }
                        }
                        if (types.Count == 0)
                        {
                            result = new ASResult();
                            result.Type = ctx.ResolveType(ctx.Features.objectKey, null);
                            types.Add(result);
                        }

                        result = types[0];
                        string paramName = null;
                        string paramType = null;
                        string paramQualType = null;

                        if (result.Member == null)
                        {
                            paramType = result.Type.Name;
                            paramQualType = result.Type.QualifiedName;
                        }
                        else
                        {
                            if (result.Member.Name != null)
                            {
                                paramName = result.Member.Name.Trim('@');
                            }
                            if (result.Member.Type == null)
                            {
                                paramType = ctx.Features.dynamicKey;
                                paramQualType = ctx.Features.dynamicKey;
                            }
                            else
                            {
                                paramType = FormatType(GetShortType(result.Member.Type));
                                if (result.InClass == null)
                                {
                                    paramQualType = result.Type.QualifiedName;
                                }
                                else
                                {
                                    paramQualType = GetQualifiedType(result.Member.Type, result.InClass);
                                }
                            }
                        }
                        prms.Add(new FunctionParameter(paramName, paramType, paramQualType, result));
                    }
                    types = new List<ASResult>();
                    sb = new StringBuilder();
                }
            }
            for (int i = 0; i < prms.Count; i++)
            {
                if (prms[i].paramType == "void")
                {
                    prms[i].paramName = "object";
                    prms[i].paramType = null;
                }
                else prms[i].paramName = GuessVarName(prms[i].paramName, FormatType(GetShortType(prms[i].paramType)));
            }
            for (int i = 0; i < prms.Count; i++)
            {
                int iterator = -1;
                bool nameUnique = false;
                string name = prms[i].paramName;
                string suggestedName = name;
                while (!nameUnique) 
                {
                    iterator++;
                    suggestedName = name + (iterator == 0 ? "" : iterator + "");
                    bool gotMatch = false;
                    for (int j = 0; j < i; j++)
                    {
                        if (prms[j] != prms[i] && prms[j].paramName == suggestedName)
                        {
                            gotMatch = true;
                            break;
                        }
                    }
                    nameUnique = !gotMatch;
                }
                prms[i].paramName = suggestedName;
            }
            return prms;
        }

        static void GenerateConstructorJob(ScintillaControl sci, ClassModel inClass)
        {
            var position = sci.WordEndPosition(sci.CurrentPos, true);
            var parameters = ParseFunctionParameters(sci, position);
            var member = new MemberModel(inClass.Name, inClass.QualifiedName, FlagType.Constructor | FlagType.Function, Visibility.Public)
            {
                Parameters = parameters.Select(it => new MemberModel(it.paramName, it.paramQualType, FlagType.ParameterVar, 0)).ToList()
            };
            var currentClass = ASContext.Context.CurrentClass;
            if (currentClass != inClass)
            {
                AddLookupPosition();
                lookupPosition = -1;
                if (currentClass.InFile != inClass.InFile) sci = ((ITabbedDocument)PluginBase.MainForm.OpenEditableDocument(inClass.InFile.FileName, false)).SciControl;
                ASContext.Context.UpdateContext(inClass.LineFrom);
            }
            position = GetBodyStart(inClass.LineFrom, inClass.LineTo, sci);
            sci.SetSel(position, position);
            GenerateFunction(member, position, inClass, false);
        }

        private static void GenerateFunctionJob(GeneratorJobType job, ScintillaControl sci, MemberModel member, bool detach, ClassModel inClass)
        {
            Visibility visibility = job.Equals(GeneratorJobType.FunctionPublic) ? Visibility.Public : GetDefaultVisibility(inClass);
            var wordStartPos = sci.WordStartPosition(sci.CurrentPos, true);
            int wordPos = sci.WordEndPosition(sci.CurrentPos, true);
            List<FunctionParameter> parameters = ParseFunctionParameters(sci, wordPos);
            // evaluate, if the function should be generated in other class
            ASResult funcResult = ASComplete.GetExpressionType(sci, sci.WordEndPosition(sci.CurrentPos, true));
            if (member != null && ASContext.CommonSettings.GenerateScope && !funcResult.Context.Value.Contains(ASContext.Context.Features.dot)) AddExplicitScopeReference(sci, inClass, member);
            int contextOwnerPos = GetContextOwnerEndPos(sci, sci.WordStartPosition(sci.CurrentPos, true));
            MemberModel isStatic = new MemberModel();
            if (contextOwnerPos != -1)
            {
                var contextOwnerResult = ASComplete.GetExpressionType(sci, contextOwnerPos);
                if (contextOwnerResult != null
                    && (contextOwnerResult.Member == null || (contextOwnerResult.Member.Flags & FlagType.Constructor) > 0)
                    && contextOwnerResult.Type != null)
                {
                    isStatic.Flags |= FlagType.Static;
                }
            }
            else if (member != null && (member.Flags & FlagType.Static) > 0)
            {
                isStatic.Flags |= FlagType.Static;
            }
            bool isOtherClass = false;
            if (funcResult.RelClass != null && !funcResult.RelClass.IsVoid() && !funcResult.RelClass.Equals(inClass))
            {
                AddLookupPosition();
                lookupPosition = -1;

                ASContext.MainForm.OpenEditableDocument(funcResult.RelClass.InFile.FileName, true);
                sci = ASContext.CurSciControl;
                isOtherClass = true;
                var fileModel = ASContext.Context.GetCodeModel(sci.Text);
                foreach (ClassModel cm in fileModel.Classes)
                {
                    if (cm.QualifiedName.Equals(funcResult.RelClass.QualifiedName))
                    {
                        funcResult.RelClass = cm;
                        break;
                    }
                }
                inClass = funcResult.RelClass;

                ASContext.Context.UpdateContext(inClass.LineFrom);
            }

            string blockTmpl;
            if ((isStatic.Flags & FlagType.Static) > 0)
            {
                blockTmpl = TemplateUtils.GetBoundary("StaticMethods");
            }
            else if ((visibility & Visibility.Public) > 0)
            {
                blockTmpl = TemplateUtils.GetBoundary("PublicMethods");
            }
            else
            {
                blockTmpl = TemplateUtils.GetBoundary("PrivateMethods");
            }
            var position = 0;
            var latest = TemplateUtils.GetTemplateBlockMember(sci, blockTmpl);
            if (latest == null || (!isOtherClass && member == null))
            {
                latest = GetLatestMemberForFunction(inClass, visibility, isStatic);
                // if we generate function in current class..
                if (!isOtherClass)
                {
                    var location = ASContext.CommonSettings.MethodsGenerationLocations;
                    if (member == null)
                    {
                        detach = false;
                        lookupPosition = -1;
                        position = sci.WordStartPosition(sci.CurrentPos, true);
                        sci.SetSel(position, sci.WordEndPosition(position, true));
                    }
                    else if (latest != null && location == MethodsGenerationLocations.AfterSimilarAccessorMethod)
                    {
                        position = sci.PositionFromLine(latest.LineTo + 1) - (sci.EOLMode == 0 ? 2 : 1);
                        sci.SetSel(position, position);
                    }
                    else
                    {
                        position = sci.PositionFromLine(member.LineTo + 1) - (sci.EOLMode == 0 ? 2 : 1);
                        sci.SetSel(position, position);
                    }
                }
                else // if we generate function in another class..
                {
                    if (latest != null)
                    {
                        position = sci.PositionFromLine(latest.LineTo + 1) - (sci.EOLMode == 0 ? 2 : 1);
                    }
                    else
                    {
                        position = GetBodyStart(inClass.LineFrom, inClass.LineTo, sci);
                        detach = false;
                    }
                    sci.SetSel(position, position);
                }
            }
            else
            {
                position = sci.PositionFromLine(latest.LineTo + 1) - (sci.EOLMode == 0 ? 2 : 1);
                sci.SetSel(position, position);
            }
            string newMemberType = null;
            ASResult callerExpr = null;
            MemberModel caller = null;
            var pos = wordStartPos;
            var parameterIndex = ASComplete.FindParameterIndex(sci, ref pos);
            if (pos != -1)
            {
                callerExpr = ASComplete.GetExpressionType(sci, pos);
                if (callerExpr != null) caller = callerExpr.Member;
            }
            if (caller?.Parameters != null && caller.Parameters.Count > 0)
            {
                Func<string, string> cleanType = null;
                cleanType = s => s.StartsWith("(") && s.EndsWith(')') ? cleanType(s.Trim('(', ')')) : s;
                var parameterType = caller.Parameters[parameterIndex].Type;
                if ((char) sci.CharAt(wordPos) == '(') newMemberType = parameterType;
                else
                {
                    var isNativeFunctionType = false;
                    if (parameterType == "Function")
                    {
                        if (IsHaxe)
                        {
                            var paramType = ASContext.Context.ResolveType(parameterType, callerExpr.InFile);
                            if (paramType.InFile.Package == "haxe" && paramType.InFile.Module == "Constraints")
                                isNativeFunctionType = true;
                        }
                        else isNativeFunctionType = true;
                    }
                    var voidKey = ASContext.Context.Features.voidKey;
                    if (isNativeFunctionType) newMemberType = voidKey;
                    else
                    {
                        var parCount = 0;
                        var braCount = 0;
                        var genCount = 0;
                        var startPosition = 0;
                        var typeLength = parameterType.Length;
                        for (var i = 0; i < typeLength; i++)
                        {
                            string type = null;
                            var c = parameterType[i];
                            if (c == '(') parCount++;
                            else if (c == ')')
                            {
                                parCount--;
                                if (parCount == 0 && braCount == 0 && genCount == 0)
                                {
                                    type = parameterType.Substring(startPosition, (i + 1) - startPosition);
                                    startPosition = i + 1;
                                }
                            }
                            else if (c == '{') braCount++;
                            else if (c == '}')
                            {
                                braCount--;
                                if (parCount == 0 && braCount == 0 && genCount == 0)
                                {
                                    type = parameterType.Substring(startPosition, (i + 1) - startPosition);
                                    startPosition = i + 1;
                                }
                            }
                            else if (c == '<') genCount++;
                            else if (c == '>' && parameterType[i - 1] != '-')
                            {
                                genCount--;
                                if (parCount == 0 && braCount == 0 && genCount == 0)
                                {
                                    type = parameterType.Substring(startPosition, (i + 1) - startPosition);
                                    startPosition = i + 1;
                                }
                            }
                            else if (parCount == 0 && braCount == 0 && genCount == 0 && c == '-' &&
                                     parameterType[i + 1] == '>')
                            {
                                if (i > startPosition) type = parameterType.Substring(startPosition, i - startPosition);
                                startPosition = i + 2;
                                i++;
                            }
                            if (type == null)
                            {
                                if (i == typeLength - 1 && i > startPosition)
                                    newMemberType = parameterType.Substring(startPosition);
                                continue;
                            }
                            type = cleanType(type);
                            var parameter = $"parameter{parameters.Count}";
                            if (type.StartsWith('?'))
                            {
                                parameter = $"?{parameter}";
                                type = type.TrimStart('?');
                            }
                            if (i == typeLength - 1) newMemberType = type;
                            else parameters.Add(new FunctionParameter(parameter, type, type, callerExpr));
                        }
                        if (parameters.Count == 1 && parameters[0].paramType == voidKey)
                            parameters.Clear();
                    }
                }
                newMemberType = cleanType(newMemberType);
            }
            // add imports to function argument types
            if (ASContext.Context.Settings.GenerateImports && parameters.Count > 0)
            {
                var types = GetQualifiedTypes(parameters.Select(it => it.paramQualType), inClass.InFile);
                position += AddImportsByName(types, sci.LineFromPosition(position));
                if (latest == null) sci.SetSel(position, sci.WordEndPosition(position, true));
                else sci.SetSel(position, position);
            }
            var newMember = NewMember(contextToken, isStatic, FlagType.Function, visibility);
            newMember.Parameters = parameters.Select(it => new MemberModel(AvoidKeyword(it.paramName), it.paramType, FlagType.ParameterVar, 0)).ToList();
            if (newMemberType != null) newMember.Type = newMemberType;
            GenerateFunction(newMember, position, inClass, detach);
        }

        static void GenerateFunction(MemberModel member, int position, ClassModel inClass, bool detach)
        {
            string template;
            string decl;
            if ((inClass.Flags & FlagType.Interface) > 0)
            {
                template = TemplateUtils.GetTemplate("IFunction");
                decl = TemplateUtils.ToDeclarationString(member, template);
            }
            else if ((member.Flags & FlagType.Constructor) > 0)
            {
                template = TemplateUtils.GetTemplate("Constructor");
                decl = TemplateUtils.ToDeclarationWithModifiersString(member, template);
            }
            else
            {
                string body = null;
                switch (ASContext.CommonSettings.GeneratedMemberDefaultBodyStyle)
                {
                    case GeneratedMemberBodyStyle.ReturnDefaultValue:
                        var type = member.Type;
                        if (inClass.InFile.haXe)
                        {
                            var expr = inClass.InFile.Context.ResolveType(type, inClass.InFile);
                            if ((expr.Flags & FlagType.Abstract) != 0 && !string.IsNullOrEmpty(expr.ExtendsType))
                                type = expr.ExtendsType;
                        }
                        var defaultValue = inClass.InFile.Context.GetDefaultValue(type);
                        if (!string.IsNullOrEmpty(defaultValue)) body = $"return {defaultValue};";
                        break;
                }
                template = TemplateUtils.GetTemplate("Function");
                decl = TemplateUtils.ToDeclarationWithModifiersString(member, template);
                decl = TemplateUtils.ReplaceTemplateVariable(decl, "Body", body);
            }
            if (detach) decl = NewLine + TemplateUtils.ReplaceTemplateVariable(decl, "BlankLine", NewLine);
            else decl = TemplateUtils.ReplaceTemplateVariable(decl, "BlankLine", null);
            InsertCode(position, decl);
        }

        static void GenerateClass(ScintillaControl sci, ClassModel inClass, ASExpr data)
        {
            var parameters = ParseFunctionParameters(sci, sci.WordEndPosition(data.PositionExpression, false));
            GenerateClass(inClass, data.Value, parameters);
        }

        static void GenerateClass(ScintillaControl sci, ClassModel inClass, string className)
        {
            var parameters = ParseFunctionParameters(sci, sci.WordEndPosition(sci.CurrentPos, true));
            GenerateClass(inClass, className, parameters);
        }

        private static void GenerateClass(ClassModel inClass, string className, IList<FunctionParameter> parameters)
        {
            AddLookupPosition(); // remember last cursor position for Shift+F4

            List<MemberModel> constructorArgs = new List<MemberModel>();
            List<String> constructorArgTypes = new List<String>();
            MemberModel paramMember = new MemberModel();
            for (int i = 0; i < parameters.Count; i++)
            {
                FunctionParameter p = parameters[i];
                constructorArgs.Add(new MemberModel(AvoidKeyword(p.paramName), p.paramType, FlagType.ParameterVar, 0));
                constructorArgTypes.Add(CleanType(GetQualifiedType(p.paramQualType, inClass)));
            }
            
            paramMember.Parameters = constructorArgs;

            IProject project = PluginBase.CurrentProject;
            if (String.IsNullOrEmpty(className)) className = "Class";
            string projFilesDir = Path.Combine(PathHelper.TemplateDir, "ProjectFiles");
            string projTemplateDir = Path.Combine(projFilesDir, project.GetType().Name);
            string paramsString = TemplateUtils.ParametersString(paramMember, true);
            Hashtable info = new Hashtable();
            info["className"] = className;
            info["templatePath"] = Path.Combine(projTemplateDir, $"Class{ASContext.Context.Settings.DefaultExtension}.fdt");
            info["inDirectory"] = Path.GetDirectoryName(inClass.InFile.FileName);
            info["constructorArgs"] = paramsString.Length > 0 ? paramsString : null;
            info["constructorArgTypes"] = constructorArgTypes;
            DataEvent de = new DataEvent(EventType.Command, "ProjectManager.CreateNewFile", info);
            EventManager.DispatchEvent(null, de);
        }

        public static void GenerateExtractVariable(ScintillaControl sci, string newName)
        {
            string expression = sci.SelText.Trim(new char[] { '=', ' ', '\t', '\n', '\r', ';', '.' });
            expression = expression.TrimEnd(new char[] { '(', '[', '{', '<' });
            expression = expression.TrimStart(new char[] { ')', ']', '}', '>' });

            var cFile = ASContext.Context.GetCodeModel(ASContext.Context.CurrentModel, sci.Text);
            MemberModel current = cFile.Context.CurrentMember;

            string characterClass = ScintillaControl.Configuration.GetLanguage(sci.ConfigurationLanguage).characterclass.Characters;

            int funcBodyStart = GetBodyStart(current.LineFrom, current.LineTo, sci);
            sci.SetSel(funcBodyStart, sci.LineEndPosition(current.LineTo));
            string currentMethodBody = sci.SelText;
            var insertPosition = funcBodyStart + currentMethodBody.IndexOfOrdinal(expression);
            var line = sci.LineFromPosition(insertPosition);
            insertPosition = sci.LineIndentPosition(line);
            
            int lastPos = -1;
            sci.Colourise(0, -1);
            while (true)
            {
                lastPos = currentMethodBody.IndexOfOrdinal(expression, lastPos + 1);
                if (lastPos > -1)
                {
                    char prevOrNextChar;
                    if (lastPos > 0)
                    {
                        prevOrNextChar = currentMethodBody[lastPos - 1];
                        if (characterClass.IndexOf(prevOrNextChar) > -1)
                        {
                            continue;
                        }
                    }
                    if (lastPos + expression.Length < currentMethodBody.Length)
                    {
                        prevOrNextChar = currentMethodBody[lastPos + expression.Length];
                        if (characterClass.IndexOf(prevOrNextChar) > -1)
                        {
                            continue;
                        }
                    }

                    var pos = funcBodyStart + lastPos;
                    int style = sci.BaseStyleAt(pos);
                    if (ASComplete.IsCommentStyle(style)) continue;
                    sci.SetSel(pos, pos + expression.Length);
                    sci.ReplaceSel(newName);
                    currentMethodBody = currentMethodBody.Substring(0, lastPos) + newName + currentMethodBody.Substring(lastPos + expression.Length);
                    lastPos += newName.Length;
                }
                else
                {
                    break;
                }
            }
            
            sci.CurrentPos = insertPosition;
            sci.SetSel(sci.CurrentPos, sci.CurrentPos);
            MemberModel m = new MemberModel(newName, "", FlagType.LocalVar, 0);
            m.Value = expression;

            string snippet = TemplateUtils.GetTemplate("Variable");
            snippet = TemplateUtils.ReplaceTemplateVariable(snippet, "Modifiers", null);
            snippet = TemplateUtils.ToDeclarationString(m, snippet);
            snippet += NewLine + "$(Boundary)";
            SnippetHelper.InsertSnippetText(sci, sci.CurrentPos, snippet);
        }

        public static void GenerateExtractMethod(ScintillaControl sci, string newName)
        {
            string selection = sci.SelText;
            if (string.IsNullOrEmpty(selection))
            {
                return;
            }

            var trimmedLength = selection.TrimStart().Length;
            if (trimmedLength == 0) return;

            sci.SetSel(sci.SelectionStart + selection.Length - trimmedLength, sci.SelectionEnd);
            sci.CurrentPos = sci.SelectionEnd;

            int lineStart = sci.LineFromPosition(sci.SelectionStart);
            int lineEnd = sci.LineFromPosition(sci.SelectionEnd);
            int firstLineIndent = sci.GetLineIndentation(lineStart);
            int entryPointIndent = sci.Indent;

            for (int i = lineStart; i <= lineEnd; i++)
            {
                int indent = sci.GetLineIndentation(i);
                if (i > lineStart)
                {
                    sci.SetLineIndentation(i, indent - firstLineIndent + entryPointIndent);
                }
            }

            string selText = sci.SelText;
            string template = TemplateUtils.GetTemplate("CallFunction");
            template = TemplateUtils.ReplaceTemplateVariable(template, "Name", newName);
            template = TemplateUtils.ReplaceTemplateVariable(template, "Arguments", "");
            sci.Colourise(0, -1);
            var pos = sci.SelectionEnd - 1;
            var endPos = sci.TextLength;
            while (pos++ < endPos)
            {
                var style = sci.StyleAt(pos);
                if (ASComplete.IsCommentStyle(style)) continue;
                var c = (char) sci.CharAt(pos);
                if (c == '\n' || c == '\r')
                {
                    template += ";";
                    break;
                }
                if ((c == ';' || c == ',' || c == '.' || c == ')' || c == '}' || c == '{') && !ASComplete.IsStringStyle(style)) break;
            }
            InsertCode(sci.CurrentPos, template, sci);

            ASContext.Context.GetCodeModel(ASContext.Context.CurrentModel, sci.Text);
            FoundDeclaration found = GetDeclarationAtLine(lineStart);
            if (found.Member == null) return;

            lookupPosition = sci.CurrentPos;
            AddLookupPosition();

            MemberModel latest = TemplateUtils.GetTemplateBlockMember(sci, TemplateUtils.GetBoundary("PrivateMethods"));

            if (latest == null)
                latest = GetLatestMemberForFunction(found.InClass, GetDefaultVisibility(found.InClass), found.Member);

            if (latest == null)
                latest = found.Member;

            int position = sci.PositionFromLine(latest.LineTo + 1) - ((sci.EOLMode == 0) ? 2 : 1);
            sci.SetSel(position, position);

            FlagType flags = FlagType.Function;
            if ((found.Member.Flags & FlagType.Static) > 0)
            {
                flags |= FlagType.Static;
            }

            MemberModel m = new MemberModel(newName, ASContext.Context.Features.voidKey, flags, GetDefaultVisibility(found.InClass));

            template = NewLine + TemplateUtils.GetTemplate("Function");
            template = TemplateUtils.ToDeclarationWithModifiersString(m, template);
            template = TemplateUtils.ReplaceTemplateVariable(template, "Body", selText);
            template = TemplateUtils.ReplaceTemplateVariable(template, "BlankLine", NewLine);
            InsertCode(position, template, sci);
        }

        private static int FindNewVarPosition(ScintillaControl sci, ClassModel inClass, MemberModel latest)
        {
            firstVar = false;
            // found a var?
            if ((latest.Flags & FlagType.Variable) > 0)
                return sci.PositionFromLine(latest.LineTo + 1) - ((sci.EOLMode == 0) ? 2 : 1);

            // add as first member
            int line = 0;
            int maxLine = sci.LineCount;
            if (inClass != null)
            {
                line = inClass.LineFrom;
                maxLine = inClass.LineTo;
            }
            else if (ASContext.Context.InPrivateSection) line = ASContext.Context.CurrentModel.PrivateSectionIndex;
            else maxLine = ASContext.Context.CurrentModel.PrivateSectionIndex;
            while (line < maxLine)
            {
                string text = sci.GetLine(line++);
                if (text.IndexOf('{') >= 0)
                {
                    firstVar = true;
                    return sci.PositionFromLine(line) - ((sci.EOLMode == 0) ? 2 : 1);
                }
            }
            return -1;
        }

        private static bool RemoveLocalDeclaration(ScintillaControl sci, MemberModel contextMember)
        {
            int removed = 0;
            if (contextResolved != null)
            {
                contextResolved.Context.LocalVars.Items.Sort(new ByDeclarationPositionMemberComparer());
                contextResolved.Context.LocalVars.Items.Reverse();
                foreach (MemberModel member in contextResolved.Context.LocalVars)
                {
                    if (member.Name == contextMember.Name)
                    {
                        RemoveOneLocalDeclaration(sci, member);
                        removed++;
                    }
                }
            }
            if (removed == 0) return RemoveOneLocalDeclaration(sci, contextMember);
            else return true;
        }

        private static bool RemoveOneLocalDeclaration(ScintillaControl sci, MemberModel contextMember)
        {
            string type = "";
            if (contextMember.Type != null && (contextMember.Flags & FlagType.Inferred) == 0)
            {
                type = FormatType(contextMember.Type);
                if (type.IndexOf('*') > 0)
                    type = type.Replace("/*", @"/\*\s*").Replace("*/", @"\s*\*/");
                type = @":\s*" + type;
            }
            var name = contextMember.Name;
            Regex reDecl = new Regex(String.Format(@"[\s\(]((var|const)\s+{0}\s*{1})\s*", name, type));
            for (int i = contextMember.LineFrom; i <= contextMember.LineTo + 10; i++)
            {
                string text = sci.GetLine(i);
                Match m = reDecl.Match(text);
                if (m.Success)
                {
                    int index = sci.MBSafeTextLength(text.Substring(0, m.Groups[1].Index));
                    int position = sci.PositionFromLine(i) + index;
                    int len = sci.MBSafeTextLength(m.Groups[1].Value);
                    sci.SetSel(position, position + len);
                    if (ASContext.CommonSettings.GenerateScope) name = "this." + name;
                    if (contextMember.Type == null || (contextMember.Flags & FlagType.Inferred) != 0) name += " ";
                    sci.ReplaceSel(name);
                    UpdateLookupPosition(position, name.Length - len);
                    return true;
                }
            }
            return false;
        }

        internal static StatementReturnType GetStatementReturnType(ScintillaControl sci, ClassModel inClass, string line, int startPos)
        {
            Regex target = new Regex(@"[;\s\n\r]*", RegexOptions.RightToLeft);
            Match m = target.Match(line);
            if (!m.Success) return null;
            line = line.Substring(0, m.Index);
            if (line.Length == 0) return null;
            var pos = startPos + m.Index;
            var expr = ASComplete.GetExpressionType(sci, pos, false, true);
            if (expr.Type != null || expr.Member != null) pos = expr.Context.Position;
            var ctx = inClass.InFile.Context;
            var features = ctx.Features;
            ASResult resolve = expr;
            if (resolve.Type != null && !resolve.IsPackage)
            {
                if (resolve.Type.Name == "Function")
                {
                    if (IsHaxe)
                    {
                        var voidKey = features.voidKey;
                        var parameters = resolve.Member.Parameters?.Select(it => it.Type).ToList() ?? new List<string> {voidKey};
                        parameters.Add(resolve.Member.Type ?? voidKey);
                        var qualifiedName = string.Empty;
                        for (var i = 0; i < parameters.Count; i++)
                        {
                            if (i > 0) qualifiedName += "->";
                            var t = parameters[i];
                            if (t.Contains("->") && !t.StartsWith('(')) t = $"({t})";
                            qualifiedName += t;
                        }
                        resolve = new ASResult {Type = new ClassModel {Name = qualifiedName, InFile = FileModel.Ignore}, Context =  expr.Context};
                    }
                    else resolve.Member = null;
                }
                else if (!string.IsNullOrEmpty(resolve.Path) && Regex.IsMatch(resolve.Path, @"(\.\[.{0,}?\])$", RegexOptions.RightToLeft))
                    resolve.Member = null;
            }
            var word = sci.GetWordFromPosition(pos);
            if (string.IsNullOrEmpty(word) && resolve.Type != null)
            {
                var tokens = Regex.Split(resolve.Context.Value, Regex.Escape(features.dot));
                word = tokens.LastOrDefault(it => it.Length > 0 && !(it.Length >= 2 && it[0] == '#' && it[it.Length - 1] == '~') && char.IsLetter(it[0]));
            }
            return new StatementReturnType(resolve, pos, word);
        }

        protected static string GuessVarName(string name, string type)
        {
            if (name == "_") name = null;
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(type))
            {
                var m = Regex.Match(type, "^([a-z0-9_$]+)", RegexOptions.IgnoreCase);
                if (m.Success) name = m.Groups[1].Value;
                else name = type;
            }

            if (string.IsNullOrEmpty(name)) return name;

            // if constant then convert to camelCase
            if (name.ToUpper() == name) name = Camelize(name);

            // if getter, then remove 'get' prefix
            name = name.TrimStart('_');
            if (name.Length > 3 && name.StartsWithOrdinal("get") && (name[3].ToString() == char.ToUpper(name[3]).ToString()))
            {
                name = char.ToLower(name[3]) + name.Substring(4);
            }

            if (name.Length > 1) name = char.ToLower(name[0]) + name.Substring(1);
            else name = char.ToLower(name[0]) + "";

            if (name == "this" || type == name)
            {
                if (!string.IsNullOrEmpty(type)) name = char.ToLower(type[0]) + type.Substring(1);
                else name = "p_this";
            }
            return name;
        }

        private static void GenerateImplementation(ClassModel iType, ClassModel inClass, ScintillaControl sci, bool detached)
        {
            var typesUsed = new HashSet<string>();

            StringBuilder sb = new StringBuilder();

            string header = TemplateUtils.ReplaceTemplateVariable(TemplateUtils.GetTemplate("ImplementHeader"), "Class", iType.Type);

            header = TemplateUtils.ReplaceTemplateVariable(header, "BlankLine", detached ? BlankLine : null);

            sb.Append(header);
            sb.Append(NewLine);
            bool entry = true;
            ASResult result = new ASResult();
            IASContext context = ASContext.Context;
            ContextFeatures features = context.Features;
            bool canGenerate = false;
            bool isHaxe = IsHaxe;
            FlagType flags = (FlagType.Function | FlagType.Getter | FlagType.Setter);
            if (isHaxe) flags |= FlagType.Variable;

            iType.ResolveExtends(); // resolve inheritance chain
            while (!iType.IsVoid() && iType.QualifiedName != "Object")
            {
                foreach (MemberModel method in iType.Members)
                {
                    if ((method.Flags & flags) == 0
                        || method.Name == iType.Name)
                        continue;

                    // check if method exists
                    ASComplete.FindMember(method.Name, inClass, result, method.Flags, 0);
                    if (!result.IsNull()) continue;

                    string decl;
                    if ((method.Flags & FlagType.Getter) > 0)
                    {
                        if (isHaxe)
                        {
                            decl = TemplateUtils.ToDeclarationWithModifiersString(method, TemplateUtils.GetTemplate("Property"));

                            string templateName = null;
                            string metadata = null;
                            if (method.Parameters[0].Name == "get")
                            {
                                if (method.Parameters[1].Name == "set")
                                {
                                    templateName = "GetterSetter";
                                    metadata = "@:isVar";
                                }
                                else
                                    templateName = "Getter";
                            }
                            else if (method.Parameters[1].Name == "set")
                            {
                                templateName = "Setter";
                            }

                            decl = TemplateUtils.ReplaceTemplateVariable(decl, "MetaData", metadata);

                            if (templateName != null)
                            {
                                var accessor = NewLine + TemplateUtils.ToDeclarationString(method, TemplateUtils.GetTemplate(templateName));
                                accessor = TemplateUtils.ReplaceTemplateVariable(accessor, "Modifiers", null);
                                accessor = TemplateUtils.ReplaceTemplateVariable(accessor, "Member", method.Name);
                                decl += accessor;
                            }
                        }
                        else
                            decl = TemplateUtils.ToDeclarationWithModifiersString(method, TemplateUtils.GetTemplate("Getter"));
                    }
                    else if ((method.Flags & FlagType.Setter) > 0)
                        decl = TemplateUtils.ToDeclarationWithModifiersString(method, TemplateUtils.GetTemplate("Setter"));
                    else if ((method.Flags & FlagType.Function) > 0)
                        decl = TemplateUtils.ToDeclarationWithModifiersString(method, TemplateUtils.GetTemplate("Function"));
                    else
                        decl = NewLine + TemplateUtils.ToDeclarationWithModifiersString(method, TemplateUtils.GetTemplate("Variable"));
                    decl = TemplateUtils.ReplaceTemplateVariable(decl, "Member", "_" + method.Name);
                    decl = TemplateUtils.ReplaceTemplateVariable(decl, "Void", features.voidKey);
                    decl = TemplateUtils.ReplaceTemplateVariable(decl, "Body", null);
                    decl = TemplateUtils.ReplaceTemplateVariable(decl, "BlankLine", NewLine);

                    if (!entry)
                    {
                        decl = TemplateUtils.ReplaceTemplateVariable(decl, "EntryPoint", null);
                    }

                    decl += NewLine;

                    entry = false;

                    sb.Append(decl);
                    canGenerate = true;

                    typesUsed.Add(method.Type);

                    if (method.Parameters != null && method.Parameters.Count > 0)
                        foreach (MemberModel param in method.Parameters)
                            typesUsed.Add(param.Type);
                }
                if (ASContext.Context.Settings.GenerateImports) typesUsed = (HashSet<string>) GetQualifiedTypes(typesUsed, iType.InFile);
                // interface inheritance
                iType = iType.Extends;
            }
            if (!canGenerate)
                return;

            sci.BeginUndoAction();
            try
            {
                int position = sci.CurrentPos;
                if (ASContext.Context.Settings.GenerateImports && typesUsed.Count > 0)
                {
                    int offset = AddImportsByName(typesUsed, sci.LineFromPosition(position));
                    position += offset;
                    sci.SetSel(position, position);
                }
                InsertCode(position, sb.ToString(), sci);
            }
            finally { sci.EndUndoAction(); }
        }

        private static void AddTypeOnce(List<string> typesUsed, string qualifiedName)
        {
            if (!typesUsed.Contains(qualifiedName)) typesUsed.Add(qualifiedName);
        }

        static IEnumerable<string> GetQualifiedTypes(IEnumerable<string> types, FileModel inFile)
        {
            var result = new HashSet<string>();
            types = ASContext.Context.DecomposeTypes(types);
            foreach (var type in types)
            {
                if (type == "*" || type.Contains(".")) result.Add(type);
                else
                {
                    var model = ASContext.Context.ResolveType(type, inFile);
                    if (!model.IsVoid() && model.InFile.Package.Length > 0) result.Add(model.QualifiedName);
                }
            }
            return result;
        }

        private static string GetQualifiedType(string type, ClassModel aType)
        {
            var dynamicKey = ASContext.Context.Features.dynamicKey ?? "*";
            if (string.IsNullOrEmpty(type)) return dynamicKey;
            if (ASContext.Context.DecomposeTypes(new [] {type}).Count() > 1) return type;
            if (type.IndexOf('<') > 0) // Vector.<Point>
            {
                Match mGeneric = Regex.Match(type, "<([^>]+)>");
                if (mGeneric.Success)
                {
                    return GetQualifiedType(mGeneric.Groups[1].Value, aType);
                }
            }

            if (type.IndexOf('.') > 0) return type;

            ClassModel aClass = ASContext.Context.ResolveType(type, aType.InFile);
            if (!aClass.IsVoid())
            {
                return aClass.QualifiedName;
            }
            return dynamicKey;
        }

        private static MemberModel NewMember(string contextToken, MemberModel calledFrom, FlagType kind, Visibility visi)
        {
            string type = (kind == FlagType.Function && !ASContext.Context.Features.hasInference) 
                ? ASContext.Context.Features.voidKey : null;
            if (calledFrom != null && (calledFrom.Flags & FlagType.Static) > 0)
                kind |= FlagType.Static;
            return new MemberModel(contextToken, type, kind, visi);
        }

        /// <summary>
        /// Get Visibility.Private or Visibility.Protected, depending on user setting forcing the use of protected.
        /// </summary>
        private static Visibility GetDefaultVisibility(ClassModel model)
        {
            if (ASContext.Context.Features.protectedKey != null
                && ASContext.CommonSettings.GenerateProtectedDeclarations
                && (model.Flags & FlagType.Final) == 0)
                return Visibility.Protected;
            return Visibility.Private;
        }

        private static void GenerateVariable(MemberModel member, int position, bool detach)
        {
            string result;
            if ((member.Flags & FlagType.Constant) > 0)
            {
                string template = TemplateUtils.GetTemplate("Constant");
                result = TemplateUtils.ToDeclarationWithModifiersString(member, template);
                result = TemplateUtils.ReplaceTemplateVariable(result, "Value", member.Value);
            }
            else
            {
                string template = TemplateUtils.GetTemplate("Variable");
                result = TemplateUtils.ToDeclarationWithModifiersString(member, template);
            }

            if (firstVar) 
            { 
                result = '\t' + result; 
                firstVar = false; 
            }
            if (detach) result = NewLine + result;
            InsertCode(position, result);
        }

        public static bool MakePrivate(ScintillaControl Sci, MemberModel member, ClassModel inClass)
        {
            ContextFeatures features = ASContext.Context.Features;
            string visibility = GetPrivateKeyword(inClass);
            if (features.publicKey == null || visibility == null) return false;
            Regex rePublic = new Regex(String.Format(@"\s*({0})\s+", features.publicKey));

            for (int i = member.LineFrom; i <= member.LineTo; i++)
            {
                var line = Sci.GetLine(i);
                var m = rePublic.Match(line);
                if (m.Success)
                {
                    var index = Sci.MBSafeTextLength(line.Substring(0, m.Groups[1].Index));
                    var position = Sci.PositionFromLine(i) + index;
                    Sci.SetSel(position, position + features.publicKey.Length);
                    Sci.ReplaceSel(visibility);
                    UpdateLookupPosition(position, features.publicKey.Length - visibility.Length);
                    return true;
                }
            }
            return false;
        }

        public static bool MakeHaxeProperty(ScintillaControl Sci, MemberModel member, string args)
        {
            ContextFeatures features = ASContext.Context.Features;
            string kind = features.varKey;

            if ((member.Flags & FlagType.Getter) > 0)
                kind = features.getKey;
            else if ((member.Flags & FlagType.Setter) > 0)
                kind = features.setKey;
            else if (member.Flags == FlagType.Function)
                kind = features.functionKey;

            Regex reMember = new Regex(String.Format(@"{0}\s+({1})[\s:]", kind, member.Name));

            for (int i = member.LineFrom; i <= member.LineTo; i++)
            {
                var line = Sci.GetLine(i);
                var m = reMember.Match(line);
                if (m.Success)
                {
                    var index = Sci.MBSafeTextLength(line.Substring(0, m.Groups[1].Index));
                    var position = Sci.PositionFromLine(i) + index;
                    Sci.SetSel(position, position + member.Name.Length);
                    Sci.ReplaceSel(member.Name + args);
                    UpdateLookupPosition(position, 1);
                    return true;
                }
            }
            return false;
        }

        public static bool RenameMember(ScintillaControl Sci, MemberModel member, string newName)
        {
            ContextFeatures features = ASContext.Context.Features;
            string kind = features.varKey;

            if ((member.Flags & FlagType.Getter) > 0)
                kind = features.getKey;
            else if ((member.Flags & FlagType.Setter) > 0)
                kind = features.setKey;
            else if (member.Flags == FlagType.Function)
                kind = features.functionKey;

            Regex reMember = new Regex(String.Format(@"{0}\s+({1})[\s:]", kind, member.Name));

            for (int i = member.LineFrom; i <= member.LineTo; i++)
            {
                var line = Sci.GetLine(i);
                var m = reMember.Match(line);
                if (m.Success)
                {
                    var index = Sci.MBSafeTextLength(line.Substring(0, m.Groups[1].Index));
                    var position = Sci.PositionFromLine(i) + index;
                    Sci.SetSel(position, position + member.Name.Length);
                    Sci.ReplaceSel(newName);
                    UpdateLookupPosition(position, 1);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Return an obvious property name matching a private var, or null
        /// </summary>
        private static string GetPropertyNameFor(MemberModel member)
        {
            string name = member.Name;
            if (name.Length == 0 || (member.Access & Visibility.Public) > 0 || IsHaxe) return null;
            Match parts = Regex.Match(name, "([^_$]*)[_$]+(.*)");
            if (parts.Success)
            {
                string pre = parts.Groups[1].Value;
                string post = parts.Groups[2].Value;
                return pre.Length > post.Length ? pre : post;
            }
            return null;
        }

        /// <summary>
        /// Return a smart new property name
        /// </summary>
        private static string GetNewPropertyNameFor(MemberModel member)
        {
            if (member.Name.Length == 0)
                return "prop";
            if (Regex.IsMatch(member.Name, "^[A-Z].*[a-z]"))
                return Char.ToLower(member.Name[0]) + member.Name.Substring(1);
            else
                return "_" + member.Name;
        }

        private static void GenerateDelegateMethod(string name, MemberModel afterMethod, int position, ClassModel inClass)
        {
            ContextFeatures features = ASContext.Context.Features;

            string acc = GetPrivateAccessor(afterMethod, inClass);
            string template = TemplateUtils.GetTemplate("Delegate");
            string args = null;
            string type = features.voidKey;

            if (features.hasDelegates && contextMember != null) // delegate functions types
            {
                args = contextMember.ParametersString();
                type = contextMember.Type;
            }

            string decl = BlankLine + TemplateUtils.ReplaceTemplateVariable(template, "Modifiers", acc);
            decl = TemplateUtils.ReplaceTemplateVariable(decl, "Name", name);
            decl = TemplateUtils.ReplaceTemplateVariable(decl, "Arguments", args);
            decl = TemplateUtils.ReplaceTemplateVariable(decl, "Type", type);
            InsertCode(position, decl);
        }

        private static void GenerateEventHandler(string name, string type, MemberModel afterMethod, int position, ClassModel inClass)
        {
            ScintillaControl sci = ASContext.CurSciControl;
            sci.BeginUndoAction();
            try
            {
                int delta = 0;
                ClassModel eventClass = ASContext.Context.ResolveType(type, ASContext.Context.CurrentModel);
                if (eventClass.IsVoid())
                {
                    if (TryImportType("flash.events." + type, ref delta, sci.LineFromPosition(position)))
                    {
                        position += delta;
                        sci.SetSel(position, position);
                    }
                    else type = null;
                }
                lookupPosition += delta;
                var newMember = new MemberModel
                {
                    Name = name,
                    Type = type,
                    Access = GetDefaultVisibility(inClass)
                };
                if ((afterMethod.Flags & FlagType.Static) > 0) newMember.Flags = FlagType.Static;
                var template = TemplateUtils.GetTemplate("EventHandler");
                var decl = NewLine + TemplateUtils.ToDeclarationWithModifiersString(newMember, template);
                decl = TemplateUtils.ReplaceTemplateVariable(decl, "Void", ASContext.Context.Features.voidKey);

                string eventName = contextMatch.Groups["event"].Value;
                string autoRemove = AddRemoveEvent(eventName);
                if (autoRemove != null)
                {
                    if (autoRemove.Length == 0 && ASContext.CommonSettings.GenerateScope) autoRemove = "this";
                    if (autoRemove.Length > 0) autoRemove += ".";
                    string remove = string.Format("{0}removeEventListener({1}, {2});\n\t$(EntryPoint)", autoRemove, eventName, name);
                    decl = decl.Replace("$(EntryPoint)", remove);
                }
                InsertCode(position, decl, sci);
            }
            finally
            {
                sci.EndUndoAction();
            }
        }

        private static bool TryImportType(string type, ref int delta, int atLine)
        {
            ClassModel eventClass = ASContext.Context.ResolveType(type, ASContext.Context.CurrentModel);
            if (eventClass.IsVoid())
                return false;
            
            List<string> typesUsed = new List<string>();
            typesUsed.Add(type);
            delta += AddImportsByName(typesUsed, atLine);
            return true;
        }

        private static string AddRemoveEvent(string eventName)
        {
            foreach (string autoRemove in ASContext.CommonSettings.EventListenersAutoRemove)
            {
                string test = autoRemove.Trim();
                if (test.Length == 0 || test.StartsWithOrdinal("//")) continue;
                int colonPos = test.IndexOf(':');
                if (colonPos >= 0) test = test.Substring(colonPos + 1);
                if (test != eventName) continue;
                return colonPos < 0 ? "" : autoRemove.Trim().Substring(0, colonPos);
            }
            return null;
        }

        private static void GenerateGetter(string name, MemberModel member, int position) => GenerateGetter(name, member, position, true, false);

        private static void GenerateGetter(string name, MemberModel member, int position, bool startsWithNewLine, bool endsWithNewLine)
        {
            var newMember = new MemberModel
            {
                Name = name,
                Type = FormatType(member.Type),
                Access = IsHaxe ? Visibility.Private : Visibility.Public
            };
            if ((member.Flags & FlagType.Static) > 0) newMember.Flags = FlagType.Static;
            string template = TemplateUtils.GetTemplate("Getter");
            string decl;
            if (startsWithNewLine) decl = NewLine + TemplateUtils.ToDeclarationWithModifiersString(newMember, template);
            else decl = TemplateUtils.ToDeclarationWithModifiersString(newMember, template);
            decl = TemplateUtils.ReplaceTemplateVariable(decl, "Member", member.Name);
            decl = TemplateUtils.ReplaceTemplateVariable(decl, "BlankLine", NewLine);
            if (endsWithNewLine) decl += NewLine + NewLine;
            InsertCode(position, decl);
        }

        private static void GenerateSetter(string name, MemberModel member, int position)
        {
            var newMember = new MemberModel
            {
                Name = name,
                Type = FormatType(member.Type),
                Access = IsHaxe ? Visibility.Private : Visibility.Public
            };
            if ((member.Flags & FlagType.Static) > 0) newMember.Flags = FlagType.Static;
            string template = TemplateUtils.GetTemplate("Setter");
            string decl = NewLine + TemplateUtils.ToDeclarationWithModifiersString(newMember, template);
            decl = TemplateUtils.ReplaceTemplateVariable(decl, "Member", member.Name);
            decl = TemplateUtils.ReplaceTemplateVariable(decl, "Void", ASContext.Context.Features.voidKey ?? "void");
            decl = TemplateUtils.ReplaceTemplateVariable(decl, "BlankLine", NewLine);
            InsertCode(position, decl);
        }

        private static void GenerateGetterSetter(string name, MemberModel member, int position)
        {
            string template = TemplateUtils.GetTemplate("GetterSetter");
            if (template == "")
            {
                GenerateSetter(name, member, position);
                ASContext.CurSciControl.SetSel(position, position);
                GenerateGetter(name, member, position);
                return;
            }
            var newMember = new MemberModel
            {
                Name = name,
                Type = FormatType(member.Type),
                Access = IsHaxe ? Visibility.Private : Visibility.Public
            };
            if ((member.Flags & FlagType.Static) > 0) newMember.Flags = FlagType.Static;
            string decl = NewLine + TemplateUtils.ToDeclarationWithModifiersString(newMember, template);
            decl = TemplateUtils.ReplaceTemplateVariable(decl, "Member", member.Name);
            decl = TemplateUtils.ReplaceTemplateVariable(decl, "Void", ASContext.Context.Features.voidKey ?? "void");
            decl = TemplateUtils.ReplaceTemplateVariable(decl, "BlankLine", NewLine);
            InsertCode(position, decl);
        }

        private static string GetStaticKeyword(MemberModel member)
        {
            if ((member.Flags & FlagType.Static) > 0) return ASContext.Context.Features.staticKey ?? "static";
            return null;
        }

        private static string GetPrivateAccessor(MemberModel member, ClassModel inClass)
        {
            string acc = GetStaticKeyword(member);
            if (!string.IsNullOrEmpty(acc)) acc += " ";
            return acc + GetPrivateKeyword(inClass);
        }

        private static string GetPrivateKeyword(ClassModel inClass)
        {
            if (GetDefaultVisibility(inClass) == Visibility.Protected) return ASContext.Context.Features.protectedKey ?? "protected";
            return ASContext.Context.Features.privateKey ?? "private";
        }

        private static MemberModel GetLatestMemberForFunction(ClassModel inClass, Visibility funcVisi, MemberModel isStatic)
        {
            MemberModel latest = null;
            if (isStatic != null && (isStatic.Flags & FlagType.Static) > 0)
            {
                latest = FindLatest(FlagType.Function | FlagType.Static, funcVisi, inClass);
                if (latest == null)
                {
                    latest = FindLatest(FlagType.Function | FlagType.Static, 0, inClass, true, false);
                }
            }
            else
            {
                latest = FindLatest(FlagType.Function, funcVisi, inClass);
            }
            if (latest == null)
            {
                latest = FindLatest(FlagType.Function, 0, inClass, true, false);
            }
            if (latest == null)
            {
                latest = FindLatest(FlagType.Function, 0, inClass, false, false);
            }
            return latest;
        }

        private static MemberModel GetLatestMemberForVariable(GeneratorJobType job, ClassModel inClass, Visibility varVisi, MemberModel isStatic)
        {
            MemberModel latest = null;
            if (job.Equals(GeneratorJobType.Constant))
            {
                if ((isStatic.Flags & FlagType.Static) > 0)
                {
                    latest = FindLatest(FlagType.Constant | FlagType.Static, varVisi, inClass);
                }
                else
                {
                    latest = FindLatest(FlagType.Constant, varVisi, inClass);
                }
                if (latest == null)
                {
                    latest = FindLatest(FlagType.Constant, 0, inClass, true, false);
                }
            }
            else
            {
                if ((isStatic.Flags & FlagType.Static) > 0)
                {
                    latest = FindLatest(FlagType.Variable | FlagType.Static, varVisi, inClass);
                    if (latest == null)
                    {
                        latest = FindLatest(FlagType.Variable | FlagType.Static, 0, inClass, true, false);
                    }
                }
                else
                {
                    latest = FindLatest(FlagType.Variable, varVisi, inClass);
                }
            }
            if (latest == null)
            {
                latest = FindLatest(FlagType.Variable, varVisi, inClass, false, false);
            }
            return latest;
        }

        private static MemberModel FindMember(string name, ClassModel inClass)
        {
            MemberList list;
            if (inClass == ClassModel.VoidClass)
                list = ASContext.Context.CurrentModel.Members;
            else list = inClass.Members;

            MemberModel found = null;
            foreach (MemberModel member in list)
            {
                if (member.Name == name)
                {
                    found = member;
                    break;
                }
            }
            return found;
        }

        private static MemberModel FindLatest(FlagType match, ClassModel inClass)
        {
            return FindLatest(match, 0, inClass);
        }

        private static MemberModel FindLatest(FlagType match, Visibility visi, ClassModel inClass)
        {
            return FindLatest(match, visi, inClass, true, true);
        }

        private static MemberModel FindLatest(FlagType match, Visibility visi, ClassModel inClass, bool isFlagMatchStrict, bool isVisibilityMatchStrict)
        {
            MemberList list;
            if (inClass == ClassModel.VoidClass)
                list = ASContext.Context.CurrentModel.Members;
            else
                list = inClass.Members;

            MemberModel latest = null;
            MemberModel fallback = null;
            foreach (MemberModel member in list)
            {
                fallback = member;
                if (isFlagMatchStrict && isVisibilityMatchStrict)
                {
                    if ((member.Flags & match) == match && (visi == 0 || (member.Access & visi) == visi))
                    {
                        latest = member;
                    }
                }
                else if (isFlagMatchStrict)
                {
                    if ((member.Flags & match) == match && (visi == 0 || (member.Access & visi) > 0))
                    {
                        latest = member;
                    }
                }
                else if (isVisibilityMatchStrict)
                {
                    if ((member.Flags & match) > 0 && (visi == 0 || (member.Access & visi) == visi))
                    {
                        latest = member;
                    }
                }
                else
                {
                    if ((member.Flags & match) > 0 && (visi == 0 || (member.Access & visi) > 0))
                    {
                        latest = member;
                    }
                }

            }
            if (isFlagMatchStrict || isVisibilityMatchStrict)
                fallback = null;
            return latest ?? fallback;
        }

        static void AddExplicitScopeReference(ScintillaControl sci, ClassModel inClass, MemberModel inMember)
        {
            var position = sci.CurrentPos;
            var start = sci.WordStartPosition(position, true);
            var length = sci.MBSafeTextLength(contextToken);
            sci.SetSel(start, start + length);
            var scope = (inMember.Flags & FlagType.Static) != 0 ? inClass.QualifiedName : "this";
            var text = $"{scope}{ASContext.Context.Features.dot}{contextToken}";
            sci.ReplaceSel(text);
            UpdateLookupPosition(position, text.Length - length);
        }
        #endregion

        #region override generator

        /// <summary>
        /// List methods to override
        /// </summary>
        /// <param name="autoHide">Don't keep the list open if the word does not match</param>
        /// <returns>Completion was handled</returns>
        protected virtual bool HandleOverrideCompletion(bool autoHide)
        {
            var ctx = ASContext.Context;
            var curClass = ctx.CurrentClass;
            if (curClass.IsVoid()) return false;

            var members = new List<MemberModel>();
            curClass.ResolveExtends(); // Resolve inheritance chain

            // explore getters or setters
            const FlagType mask = FlagType.Function | FlagType.Getter | FlagType.Setter;
            var tmpClass = curClass.Extends;
            var acc = ctx.TypesAffinity(curClass, tmpClass);
            while (tmpClass != null && !tmpClass.IsVoid())
            {
                if (tmpClass.QualifiedName.StartsWithOrdinal("flash.utils.Proxy"))
                {
                    foreach (MemberModel member in tmpClass.Members)
                    {
                        member.Namespace = "flash_proxy";
                        members.Add(member);
                    }
                    break;
                }

                foreach (MemberModel member in tmpClass.Members)
                {
                    if (curClass.Members.Search(member.Name, FlagType.Override, 0) != null) continue;
                    if ((member.Flags & FlagType.Dynamic) > 0
                        && (member.Access & acc) > 0
                        && ((member.Flags & FlagType.Function) > 0 || (member.Flags & mask) > 0))
                    {
                        members.Add(member);
                    }
                }

                tmpClass = tmpClass.Extends;
                // members visibility
                acc = ctx.TypesAffinity(curClass, tmpClass);
            }
            members.Sort();

            var list = new List<ICompletionListItem>();
            MemberModel last = null;
            foreach (var member in members)
            {
                if (last == null || last.Name != member.Name)
                    list.Add(new MemberItem(member));
                last = member;
            }
            if (list.Count > 0) CompletionList.Show(list, autoHide);
            return true;
        }

        public static void GenerateOverride(ScintillaControl Sci, ClassModel ofClass, MemberModel member, int position)
        {
            var context = ASContext.Context;
            var features = context.Features;
            List<string> typesUsed = new List<string>();
            bool isProxy = (member.Namespace == "flash_proxy");
            if (isProxy) typesUsed.Add("flash.utils.flash_proxy");
            
            int line = Sci.LineFromPosition(position);
            string currentText = Sci.GetLine(line);
            int startPos = currentText.Length;
            GetStartPos(currentText, ref startPos, features.privateKey);
            GetStartPos(currentText, ref startPos, features.protectedKey);
            GetStartPos(currentText, ref startPos, features.internalKey);
            GetStartPos(currentText, ref startPos, features.publicKey);
            GetStartPos(currentText, ref startPos, features.staticKey);
            GetStartPos(currentText, ref startPos, features.overrideKey);
            startPos += Sci.PositionFromLine(line);

            var newMember = new MemberModel
            {
                Name = member.Name,
                Type = member.Type
            };
            if (features.hasNamespaces && !string.IsNullOrEmpty(member.Namespace) && member.Namespace != "internal")
                newMember.Namespace = member.Namespace;
            else newMember.Access = member.Access;

            bool isAS2Event = context.Settings.LanguageId == "AS2" && member.Name.StartsWithOrdinal("on");
            if (!isAS2Event && ofClass.QualifiedName != "Object") newMember.Flags |= FlagType.Override;

            string decl = "";

            FlagType flags = member.Flags;
            if ((flags & FlagType.Static) > 0) newMember.Flags |= FlagType.Static;
            var parameters = member.Parameters;
            if ((flags & (FlagType.Getter | FlagType.Setter)) > 0)
            {
                if (IsHaxe) newMember.Access = Visibility.Private;
                var type = newMember.Type;
                var name = newMember.Name;
                if (parameters != null && parameters.Count == 1) type = parameters[0].Type;
                type = FormatType(type);
                if (type == null && !features.hasInference) type = features.objectKey;
                newMember.Type = type;
                var currentClass = context.CurrentClass;
                if (ofClass.Members.Search(name, FlagType.Getter, 0) != null
                    && (!IsHaxe || (parameters?[0].Name == "get" && currentClass.Members.Search($"get_{name}", FlagType.Function, 0) == null)))
                {
                    var template = TemplateUtils.GetTemplate("OverrideGetter", "Getter");
                    template = TemplateUtils.ToDeclarationWithModifiersString(newMember, template);
                    template = TemplateUtils.ReplaceTemplateVariable(template, "Member", $"super.{name}");
                    decl += template;
                }
                if (ofClass.Members.Search(name, FlagType.Setter, 0) != null
                    && (!IsHaxe || (parameters?[1].Name == "set" && currentClass.Members.Search($"set_{name}", FlagType.Function, 0) == null)))
                {
                    var template = TemplateUtils.GetTemplate("OverrideSetter", "Setter");
                    template = TemplateUtils.ToDeclarationWithModifiersString(newMember, template);
                    template = TemplateUtils.ReplaceTemplateVariable(template, "Member", $"super.{name}");
                    template = TemplateUtils.ReplaceTemplateVariable(template, "Void", features.voidKey ?? "void");
                    if (decl.Length > 0) template = "\n\n" + template.Replace("$(EntryPoint)", "");
                    decl += template;
                }
                decl = TemplateUtils.ReplaceTemplateVariable(decl, "BlankLine", "");
                typesUsed.Add(type);
            }
            else
            {
                var type = FormatType(newMember.Type);
                var noRet = type == null || type.Equals("void", StringComparison.OrdinalIgnoreCase);
                type = (noRet && type != null) ? features.voidKey : type;
                if (!noRet) typesUsed.Add(type);
                newMember.Template = member.Template;
                newMember.Type = type;
                // fix parameters if needed
                if (parameters != null)
                    foreach (MemberModel para in parameters)
                        if (para.Type == "any") para.Type = "*";

                newMember.Parameters = parameters;
                var action = (isProxy || isAS2Event) ? "" : GetSuperCall(member, typesUsed);
                var template = TemplateUtils.GetTemplate("MethodOverride");
                template = TemplateUtils.ToDeclarationWithModifiersString(newMember, template);
                template = TemplateUtils.ReplaceTemplateVariable(template, "Method", action);
                decl = template;
            }

            Sci.BeginUndoAction();
            try
            {
                if (context.Settings.GenerateImports && typesUsed.Count > 0)
                {
                    var types = GetQualifiedTypes(typesUsed, ofClass.InFile);
                    int offset = AddImportsByName(types, line);
                    position += offset;
                    startPos += offset;
                }

                Sci.SetSel(startPos, position + member.Name.Length);
                InsertCode(startPos, decl, Sci);
            }
            finally { Sci.EndUndoAction(); }
        }

        public static void GenerateDelegateMethods(ScintillaControl sci, MemberModel member,
            Dictionary<MemberModel, ClassModel> selectedMembers, ClassModel classModel, ClassModel inClass)
        {
            sci.BeginUndoAction();
            try
            {
                string result = TemplateUtils.ReplaceTemplateVariable(
                    TemplateUtils.GetTemplate("DelegateMethodsHeader"), 
                    "Class", 
                    classModel.Type);

                int position = -1;
                List<string> importsList = new List<string>();
                bool isStaticMember = (member.Flags & FlagType.Static) > 0;

                inClass.ResolveExtends();
                
                Dictionary<MemberModel, ClassModel>.KeyCollection selectedMemberKeys = selectedMembers.Keys;
                foreach (MemberModel m in selectedMemberKeys)
                {
                    MemberModel mCopy = (MemberModel) m.Clone();

                    string methodTemplate = NewLine;

                    bool overrideFound = false;
                    ClassModel baseClassType = inClass;
                    while (baseClassType != null && !baseClassType.IsVoid())
                    {
                        MemberList inClassMembers = baseClassType.Members;
                        foreach (MemberModel inClassMember in inClassMembers)
                        {
                            if ((inClassMember.Flags & FlagType.Function) > 0
                               && m.Name.Equals(inClassMember.Name))
                            {
                                mCopy.Flags |= FlagType.Override;
                                overrideFound = true;
                                break;
                            }
                        }

                        if (overrideFound)
                            break;

                        baseClassType = baseClassType.Extends;
                    }

                    var flags = m.Flags;
                    if (isStaticMember && (flags & FlagType.Static) == 0) mCopy.Flags |= FlagType.Static;
                    var variableTemplate = string.Empty;
                    if (IsHaxe & (flags & (FlagType.Getter | FlagType.Setter)) != 0)
                    {
                        variableTemplate = NewLine + NewLine + (TemplateUtils.GetStaticExternOverride(m) + TemplateUtils.GetModifiers(m)).Trim() + " var " + m.Name;
                    }
                    if ((flags & FlagType.Getter) > 0)
                    {
                        if (!IsHaxe || (m.Parameters[0].Name != "null" && m.Parameters[0].Name != "never"))
                        {
                            string modifiers;
                            if (IsHaxe)
                            {
                                variableTemplate += "(get, ";
                                modifiers = (TemplateUtils.GetStaticExternOverride(m) + TemplateUtils.GetModifiers(Visibility.Private)).Trim();
                            }
                            else modifiers = (TemplateUtils.GetStaticExternOverride(m) + TemplateUtils.GetModifiers(m)).Trim();
                            methodTemplate += TemplateUtils.GetTemplate("Getter");
                            methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "Modifiers", modifiers);
                            methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "Name", m.Name);
                            methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "EntryPoint", "");
                            methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "Type", FormatType(m.Type));
                            methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "Member", member.Name + "." + m.Name);
                            flags &= ~FlagType.Function;
                        }
                        else variableTemplate += "(" + m.Parameters[0].Name + ", ";
                    }
                    if ((flags & FlagType.Setter) > 0)
                    {
                        if (!IsHaxe || (m.Parameters[1].Name != "null" && m.Parameters[1].Name != "never"))
                        {
                            string modifiers;
                            string type;
                            if (IsHaxe)
                            {
                                variableTemplate += "set)";
                                if (methodTemplate != NewLine) methodTemplate += NewLine;
                                modifiers = (TemplateUtils.GetStaticExternOverride(m) + TemplateUtils.GetModifiers(Visibility.Private)).Trim();
                                type = FormatType(m.Type);
                            }
                            else
                            {
                                modifiers = (TemplateUtils.GetStaticExternOverride(m) + TemplateUtils.GetModifiers(m)).Trim();
                                type = m.Parameters[0].Type;
                            }
                            methodTemplate += TemplateUtils.GetTemplate("Setter");
                            methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "Modifiers", modifiers);
                            methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "Name", m.Name);
                            methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "EntryPoint", "");
                            methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "Type", type);
                            methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "Member", member.Name + "." + m.Name);
                            methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "Void", ASContext.Context.Features.voidKey ?? "void");
                            flags &= ~FlagType.Function;
                        }
                        else variableTemplate += m.Parameters[1].Name + ")";
                    }
                    if (!string.IsNullOrEmpty(variableTemplate))
                    {
                        variableTemplate += ":" + m.Type + ";";
                        result += variableTemplate;
                    }
                    if ((flags & FlagType.Function) > 0)
                    {
                        methodTemplate += TemplateUtils.GetTemplate("Function");
                        methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "Body", "<<$(Return) >>$(Body)");
                        methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "EntryPoint", null);
                        methodTemplate = TemplateUtils.ToDeclarationWithModifiersString(mCopy, methodTemplate);
                        if (m.Type != null && m.Type.ToLower() != "void")
                            methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "Return", "return");
                        else
                            methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "Return", null);

                        // check for varargs
                        bool isVararg = false;
                        if (m.Parameters != null && m.Parameters.Count > 0)
                        {
                            MemberModel mm = m.Parameters[m.Parameters.Count - 1];
                            if (mm.Name.StartsWithOrdinal("..."))
                                isVararg = true;
                        }

                        string callMethodTemplate = TemplateUtils.GetTemplate("CallFunction");
                        if (!isVararg)
                        {
                            callMethodTemplate = TemplateUtils.ReplaceTemplateVariable(callMethodTemplate, "Name", member.Name + "." + m.Name);
                            callMethodTemplate = TemplateUtils.ReplaceTemplateVariable(callMethodTemplate, "Arguments", 
                                TemplateUtils.CallParametersString(m));
                            callMethodTemplate += ";";
                        }
                        else 
                        {
                            List<MemberModel> pseudoParamsList = new List<MemberModel>();
                            pseudoParamsList.Add(new MemberModel("null", null, FlagType.ParameterVar, 0));
                            pseudoParamsList.Add(new MemberModel("[$(Subarguments)].concat($(Lastsubargument))", null, FlagType.ParameterVar, 0));
                            MemberModel pseudoParamsOwner = new MemberModel();
                            pseudoParamsOwner.Parameters = pseudoParamsList;

                            callMethodTemplate = TemplateUtils.ReplaceTemplateVariable(callMethodTemplate, "Name",
                                member.Name + "." + m.Name + ".apply");
                            callMethodTemplate = TemplateUtils.ReplaceTemplateVariable(callMethodTemplate, "Arguments",
                                TemplateUtils.CallParametersString(pseudoParamsOwner));
                            callMethodTemplate += ";";

                            List<MemberModel> arrayParamsList = new List<MemberModel>();
                            for (int i = 0; i < m.Parameters.Count - 1; i++)
                            {
                                MemberModel param = m.Parameters[i];
                                arrayParamsList.Add(param);
                            }

                            pseudoParamsOwner.Parameters = arrayParamsList;

                            callMethodTemplate = TemplateUtils.ReplaceTemplateVariable(callMethodTemplate, "Subarguments",
                                TemplateUtils.CallParametersString(pseudoParamsOwner));

                            callMethodTemplate = TemplateUtils.ReplaceTemplateVariable(callMethodTemplate, "Lastsubargument", 
                                m.Parameters[m.Parameters.Count - 1].Name.TrimStart('.', ' '));
                        }

                        methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "Body", callMethodTemplate);
                    }
                    methodTemplate = TemplateUtils.ReplaceTemplateVariable(methodTemplate, "BlankLine", NewLine);
                    result += methodTemplate;

                    if (ASContext.Context.Settings.GenerateImports && m.Parameters != null)
                    {
                        importsList.AddRange(from param in m.Parameters where param.Type != null select param.Type);
                    }

                    if (position < 0)
                    {
                        MemberModel latest = GetLatestMemberForFunction(inClass, mCopy.Access, mCopy);
                        if (latest == null)
                        {
                            position = sci.WordStartPosition(sci.CurrentPos, true);
                            sci.SetSel(position, sci.WordEndPosition(position, true));
                        }
                        else
                        {
                            position = sci.PositionFromLine(latest.LineTo + 1) - ((sci.EOLMode == 0) ? 2 : 1);
                            sci.SetSel(position, position);
                        }
                    }
                    else position = sci.CurrentPos;

                    if (ASContext.Context.Settings.GenerateImports && m.Type != null) importsList.Add(m.Type);
                }

                if (ASContext.Context.Settings.GenerateImports && importsList.Count > 0 && position > -1)
                {
                    var types = GetQualifiedTypes(importsList, inClass.InFile);
                    position += AddImportsByName(types, sci.LineFromPosition(position));
                    sci.SetSel(position, position);
                }

                InsertCode(position, result, sci);
            }
            finally { sci.EndUndoAction(); }
        }

        private static void GetStartPos(string currentText, ref int startPos, string keyword)
        {
            if (keyword == null) return;
            int p = currentText.IndexOfOrdinal(keyword);
            if (p > 0 && p < startPos) startPos = p;
        }

        private static string GetShortType(string type)
        {
            return string.IsNullOrEmpty(type) ? type : Regex.Replace(type, @"(?=\w+\.<)|(?:\w+\.)", string.Empty);
        }

        private static string FormatType(string type)
        {
            return MemberModel.FormatType(type);
        }

        private static string CleanType(string type)
        {
            if (string.IsNullOrEmpty(type))
            {
                return type;
            }
            int p = type.IndexOf('$');
            if (p > 0) type = type.Substring(0, p);
            p = type.IndexOf('<');
            if (p > 1 && type[p - 1] == '.') p--;
            if (p > 0) type = type.Substring(0, p);
            p = type.IndexOf('@');
            if (p > 0)
            {
                type = type.Substring(0, p);
            }
            return type;
        }

        private static string GetSuperCall(MemberModel member, List<string> typesUsed)
        {
            string args = "";
            if (member.Parameters != null)
                foreach (MemberModel param in member.Parameters)
                {
                    if (param.Name.StartsWith('.')) break;
                    args += ", " + TemplateUtils.GetParamName(param);
                    AddTypeOnce(typesUsed, param.Type);
                }

            bool noRet = string.IsNullOrEmpty(member.Type) || member.Type.Equals("void", StringComparison.OrdinalIgnoreCase);
            if (!noRet) AddTypeOnce(typesUsed, member.Type);

            string action = "";
            if ((member.Flags & FlagType.Function) > 0)
            {
                action =
                    (noRet ? "" : "return ")
                    + "super." + member.Name
                    + ((args.Length > 2) ? "(" + args.Substring(2) + ")" : "()") + ";";
            }
            else if ((member.Flags & FlagType.Setter) > 0 && args.Length > 0)
            {
                action = "super." + member.Name + " = " + member.Parameters[0].Name + ";";
            }
            else if ((member.Flags & FlagType.Getter) > 0)
            {
                action = "return super." + member.Name + ";";
            }
            return action;
        }

        #endregion

        #region imports generator

        /// <summary>
        /// Generates all the missing imports in the given types list
        /// </summary>
        /// <param name="typesUsed">Types to import if needed</param>
        /// <param name="atLine">Current line in editor</param>
        /// <returns>Inserted characters count</returns>
        private static int AddImportsByName(IEnumerable<string> typesUsed, int atLine)
        {
            int length = 0;
            IASContext context = ASContext.Context;
            var addedTypes = new HashSet<string>();
            typesUsed = context.DecomposeTypes(typesUsed);
            foreach (string type in typesUsed)
            {
                var cleanType = CleanType(type);
                if (string.IsNullOrEmpty(cleanType) || addedTypes.Contains(cleanType) || cleanType.IndexOf('.') <= 0)
                    continue;
                addedTypes.Add(cleanType);
                MemberModel import = new MemberModel(cleanType.Substring(cleanType.LastIndexOf('.') + 1), cleanType, FlagType.Import, Visibility.Public);
                if (!context.IsImported(import, atLine))
                    length += InsertImport(import, false);
            }
            return length;
        }

        /// <summary>
        /// Add an 'import' statement in the current file
        /// </summary>
        /// <param name="member">Generates 'import {member.Type};'</param>
        /// <param name="fixScrolling">Keep the editor view as if we didn't add any code in the file</param>
        /// <returns>Inserted characters count</returns>
        public static int InsertImport(MemberModel member, bool fixScrolling)
        {
            ScintillaControl sci = ASContext.CurSciControl;
            FileModel cFile = ASContext.Context.CurrentModel;
            int position = sci.CurrentPos;
            int curLine = sci.LineFromPosition(position);

            string fullPath = member.Type;
            if ((member.Flags & (FlagType.Class | FlagType.Enum | FlagType.TypeDef | FlagType.Struct)) > 0)
            {
                FileModel inFile = member.InFile;
                if (inFile != null && inFile.Module == member.Name && inFile.Package != "")
                    fullPath = inFile.Package + "." + inFile.Module;
                fullPath = CleanType(fullPath);
            }
            string nl = LineEndDetector.GetNewLineMarker(sci.EOLMode);
            string statement = "import " + fullPath + ";" + nl;

            // locate insertion point
            int line = (ASContext.Context.InPrivateSection) ? cFile.PrivateSectionIndex : 0;
            if (cFile.InlinedRanges != null)
            {
                foreach (InlineRange range in cFile.InlinedRanges)
                {
                    if (position > range.Start && position < range.End)
                    {
                        line = sci.LineFromPosition(range.Start) + 1;
                        break;
                    }
                }
            }
            int firstLine = line;
            bool found = false;
            int packageLine = -1;
            int indent = 0;
            int skipIfDef = 0;
            var importComparer = new CaseSensitiveImportComparer();
            while (line < curLine)
            {
                var txt = sci.GetLine(line++).TrimStart();
                if (txt.StartsWith("package"))
                {
                    packageLine = line;
                    firstLine = line;
                }
                // skip Haxe #if blocks
                else if (txt.StartsWithOrdinal("#if ") && txt.IndexOfOrdinal("#end") < 0) skipIfDef++;
                else if (skipIfDef > 0)
                {
                    if (txt.StartsWithOrdinal("#end")) skipIfDef--;
                    else continue;
                }
                // insert imports after a package declaration
                else if (txt.Length > 6 && txt.StartsWithOrdinal("import") && txt[6] <= 32)
                {
                    packageLine = -1;
                    found = true;
                    indent = sci.GetLineIndentation(line - 1);
                    // insert in alphabetical order
                    var mImport = ASFileParserRegexes.Import.Match(txt);
                    if (mImport.Success && importComparer.Compare(mImport.Groups["package"].Value, fullPath) > 0)
                    {
                        line--;
                        break;
                    }
                }
                else if (found)
                {
                    line--;
                    break;
                }

                if (packageLine >= 0 && !IsHaxe && txt.IndexOf('{') >= 0)
                {
                    packageLine = -1;
                    indent = sci.GetLineIndentation(line - 1) + PluginBase.MainForm.Settings.IndentSize;
                    firstLine = line;
                }
            }

            // insert
            if (line == curLine) line = firstLine;
            position = sci.PositionFromLine(line);
            firstLine = sci.FirstVisibleLine;
            sci.SetSel(position, position);
            sci.ReplaceSel(statement);
            sci.SetLineIndentation(line, indent);
            sci.LineScroll(0, firstLine - sci.FirstVisibleLine + 1);

            ASContext.Context.RefreshContextCache(fullPath);
            return sci.GetLine(line).Length;
        }
        #endregion

        #region common safe code insertion
        static private int lookupPosition;

        public static void InsertCode(int position, string src)
        {
            InsertCode(position, src, ASContext.CurSciControl);
        }

        public static void InsertCode(int position, string src, ScintillaControl sci)
        {
            sci.BeginUndoAction();
            try
            {
                if (ASContext.CommonSettings.DeclarationModifierOrder.Length > 1)
                    src = FixModifiersLocation(src, ASContext.CommonSettings.DeclarationModifierOrder);

                int len = SnippetHelper.InsertSnippetText(sci, position + sci.MBSafeTextLength(sci.SelText), src);
                UpdateLookupPosition(position, len);
                AddLookupPosition(sci);
            }
            finally { sci.EndUndoAction(); }
        }

        /// <summary>
        /// Order declaration modifiers
        /// </summary>
        private static string FixModifiersLocation(string src, string[] modifierOrder)
        {
            bool needUpdate = false;
            string[] lines = src.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                Match m = reModifiers.Match(line);
                if (!m.Success) continue;

                Group decl = m.Groups[2];
                string modifiers = decl.Value;
                string before = "", after = "";
                bool insertAfter = false;

                for (int j = 0; j < modifierOrder.Length; j++)
                {
                    string modifier = modifierOrder[j];
                    if (modifier == GeneralSettings.DECLARATION_MODIFIER_REST) insertAfter = true;
                    else
                    {
                        modifier = RemoveAndExtractModifier(modifier, ref modifiers);
                        if (insertAfter) after += modifier;
                        else before += modifier;
                    }
                }

                modifiers = before + modifiers + after;

                if (decl.Value != modifiers)
                {
                    lines[i] = line.Remove(decl.Index, decl.Length).Insert(decl.Index, modifiers);
                    needUpdate = true;
                }
            }
            return needUpdate ? string.Join("\n", lines) : src;
        }

        private static string RemoveAndExtractModifier(string modifier, ref string modifiers)
        {
            modifier += " ";
            int index = modifiers.IndexOf(modifier, StringComparison.Ordinal);

            if (index == -1) return null;
            modifiers = modifiers.Remove(index, modifier.Length);
            return modifier;
        }

        private static void UpdateLookupPosition(int position, int delta)
        {
            if (lookupPosition > position)
            {
                if (lookupPosition < position + delta) lookupPosition = position;// replaced text at cursor position
                else lookupPosition += delta;
            }
        }

        private static void AddLookupPosition()
        {
            AddLookupPosition(ASContext.CurSciControl);
        }

        private static void AddLookupPosition(ScintillaControl sci)
        {
            if (lookupPosition >= 0 && sci != null)
            {
                int lookupLine = sci.LineFromPosition(lookupPosition);
                int lookupCol = lookupPosition - sci.PositionFromLine(lookupLine);
                // TODO: Refactor, doesn't make a lot of sense to have this feature inside the Panel
                ASContext.Panel.SetLastLookupPosition(sci.FileName, lookupLine, lookupCol);
            }
        }

        #endregion
    }

    #region related structures
    /// <summary>
    /// Available generators
    /// </summary>
    public enum GeneratorJobType:int
    {
        GetterSetter,
        Getter,
        Setter,
        ComplexEvent,
        BasicEvent,
        Delegate,
        Variable,
        Function,
        ImplementInterface,
        PromoteLocal,
        MoveLocalUp,
        AddImport,
        Class,
        FunctionPublic,
        VariablePublic,
        Constant,
        Constructor,
        ToString,
        FieldFromParameter,
        AddInterfaceDef,
        ConvertToConst,
        AddAsParameter,
        ChangeMethodDecl,
        EventMetatag,
        AssignStatementToVar,
        ChangeConstructorDecl,
    }

    /// <summary>
    /// Generation completion list item
    /// </summary>
    class GeneratorItem : ICompletionListItem
    {
        private string label;
        internal GeneratorJobType job { get; }
        private MemberModel member;
        private ClassModel inClass;
        private Object data;

        public GeneratorItem(string label, GeneratorJobType job, MemberModel member, ClassModel inClass)
        {
            this.label = label;
            this.job = job;
            this.member = member;
            this.inClass = inClass;
        }

        public GeneratorItem(string label, GeneratorJobType job, MemberModel member, ClassModel inClass, Object data) : this(label, job, member, inClass)
        {
            this.data = data;
        }

        public string Label
        {
            get { return label; }
        }
        public string Description
        {
            get { return TextHelper.GetString("Info.GeneratorTemplate"); }
        }

        public Bitmap Icon
        {
            get { return (Bitmap)ASContext.Panel.GetIcon(PluginUI.ICON_DECLARATION); }
        }

        public string Value
        {
            get
            {
                ASGenerator.GenerateJob(job, member, inClass, label, data);
                return null;
            }
        }

        public Object Data
        {
            get
            {
                return data;
            }
        }
    }

    public class FoundDeclaration
    {
        public MemberModel Member;
        public ClassModel InClass;

        public FoundDeclaration()
        {
            Member = null;
            InClass = ClassModel.VoidClass;
        }
    }

    public class FunctionParameter
    {
        public string paramType;
        public string paramQualType;
        public string paramName;
        public ASResult result;

        public FunctionParameter(string parameter, string paramType, string paramQualType, ASResult result)
        {
            this.paramName = parameter;
            this.paramType = paramType;
            this.paramQualType = paramQualType;
            this.result = result;
        }
    }

    class StatementReturnType
    {
        public ASResult resolve;
        public Int32 position;
        public String word;

        public StatementReturnType(ASResult resolve, Int32 position, String word)
        {
            this.resolve = resolve;
            this.position = position;
            this.word = word;
        }
    }
    #endregion
}

