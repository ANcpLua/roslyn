﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using Analyzer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Roslyn.Diagnostics.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public sealed class SymbolIsBannedAnalyzer : DiagnosticAnalyzer
    {
        internal const string BannedSymbolsFileName = "BannedSymbols.txt";

        internal static readonly DiagnosticDescriptor SymbolIsBannedRule = new DiagnosticDescriptor(
            id: RoslynDiagnosticIds.SymbolIsBannedRuleId,
            title: RoslynDiagnosticsAnalyzersResources.SymbolIsBannedTitle,
            messageFormat: RoslynDiagnosticsAnalyzersResources.SymbolIsBannedMessage,
            category: "ApiDesign",
            defaultSeverity: DiagnosticHelpers.DefaultDiagnosticSeverity,
            isEnabledByDefault: DiagnosticHelpers.EnabledByDefaultIfNotBuildingVSIX,
            description: RoslynDiagnosticsAnalyzersResources.SymbolIsBannedDescription,
            customTags: WellKnownDiagnosticTags.Telemetry);

        internal static readonly DiagnosticDescriptor DuplicateBannedSymbolRule = new DiagnosticDescriptor(
            id: RoslynDiagnosticIds.DuplicateBannedSymbolRuleId,
            title: RoslynDiagnosticsAnalyzersResources.DuplicateBannedSymbolTitle,
            messageFormat: RoslynDiagnosticsAnalyzersResources.DuplicateBannedSymbolMessage,
            category: "ApiDesign",
            defaultSeverity: DiagnosticHelpers.DefaultDiagnosticSeverity,
            isEnabledByDefault: DiagnosticHelpers.EnabledByDefaultIfNotBuildingVSIX,
            description: RoslynDiagnosticsAnalyzersResources.DuplicateBannedSymbolDescription,
            customTags: WellKnownDiagnosticTags.Telemetry);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(SymbolIsBannedRule, DuplicateBannedSymbolRule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();

            // Analyzer needs to get callbacks for generated code, and might report diagnostics in generated code.
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext compilationContext)
        {
            var additionalFiles = compilationContext.Options.AdditionalFiles;

            if (!TryGetApiData(additionalFiles, compilationContext.CancellationToken, out ApiData apiData))
            {
                return;
            }

            if (!ValidateApiFiles(apiData, out List<Diagnostic> errors))
            {
                compilationContext.RegisterCompilationEndAction(context =>
                {
                    foreach (Diagnostic cur in errors)
                    {
                        context.ReportDiagnostic(cur);
                    }
                });

                return;
            }

            var bannedApis = ResolveBannedApis(compilationContext.Compilation, apiData, compilationContext.CancellationToken);
            if (bannedApis.Count > 0)
            {
                var symbolDisplayFormat = compilationContext.Compilation.Language == LanguageNames.CSharp
                    ? SymbolDisplayFormat.CSharpShortErrorMessageFormat
                    : SymbolDisplayFormat.VisualBasicShortErrorMessageFormat;
                compilationContext.RegisterOperationAction(
                    oac => AnalyzeOperation(oac, bannedApis, symbolDisplayFormat),
                    OperationKind.ObjectCreation,
                    OperationKind.Invocation,
                    OperationKind.EventReference,
                    OperationKind.FieldReference,
                    OperationKind.MethodReference,
                    OperationKind.PropertyReference);
            }
        }

        private static ImmutableHashSet<ISymbol> ResolveBannedApis(Compilation compilation, ApiData apiData, CancellationToken cancellationToken)
        {
            var bannedApisBuilder = ImmutableHashSet.CreateBuilder<ISymbol>();
            foreach (var apiString in apiData.ApiList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(apiString.Text, compilation);
                if (!symbols.IsDefaultOrEmpty)
                {
                    bannedApisBuilder.UnionWith(symbols);
                }
            }

            var bannedApis = bannedApisBuilder.ToImmutable();
            return bannedApis;
        }

        private static void AnalyzeOperation(OperationAnalysisContext oac, ImmutableHashSet<ISymbol> bannedSymbols, SymbolDisplayFormat symbolDisplayFormat)
        {
            ITypeSymbol type = null;
            switch (oac.Operation)
            {
                case IObjectCreationOperation objectCreation:
                    type = objectCreation.Type.OriginalDefinition;
                    break;

                case IInvocationOperation invocation:
                    type = invocation.TargetMethod.ContainingType.OriginalDefinition;
                    break;

                case IMemberReferenceOperation memberReference:
                    type = memberReference.Member.ContainingType.OriginalDefinition;
                    break;
            }

            while (!(type is null))
            {
                if (bannedSymbols.Contains(type))
                {
                    oac.ReportDiagnostic(Diagnostic.Create(SymbolIsBannedRule, oac.Operation.Syntax.GetLocation(), type.ToDisplayString(symbolDisplayFormat)));
                    break;
                }

                type = type.ContainingType;
            }
        }

        private static ApiData ReadApiData(string path, SourceText sourceText)
        {
            ImmutableArray<ApiLine>.Builder apiBuilder = ImmutableArray.CreateBuilder<ApiLine>();

            foreach (TextLine line in sourceText.Lines)
            {
                string text = line.ToString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var apiLine = new ApiLine(text, line.Span, sourceText, path);
                apiBuilder.Add(apiLine);
            }

            return new ApiData(apiBuilder.ToImmutable());
        }

        private static bool TryGetApiData(ImmutableArray<AdditionalText> additionalTexts, CancellationToken cancellationToken, out ApiData apiData)
        {
            if (!TryGetApiText(additionalTexts, cancellationToken, out AdditionalText apiText))
            {
                apiData = default(ApiData);
                return false;
            }

            var sourceText = apiText.GetText(cancellationToken);
            if (sourceText == null)
            {
                apiData = default(ApiData);
                return false;
            }

            apiData = ReadApiData(apiText.Path, sourceText);
            return true;
        }

        private static bool TryGetApiText(ImmutableArray<AdditionalText> additionalTexts, CancellationToken cancellationToken, out AdditionalText apiText)
        {
            apiText = null;

            StringComparer comparer = StringComparer.Ordinal;
            foreach (AdditionalText text in additionalTexts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string fileName = Path.GetFileName(text.Path);
                if (comparer.Equals(fileName, BannedSymbolsFileName))
                {
                    apiText = text;
                    return true;
                }
            }

            return false;
        }

        private static bool ValidateApiFiles(ApiData apiData, out List<Diagnostic> errors)
        {
            errors = new List<Diagnostic>();
            var publicApiMap = new Dictionary<string, ApiLine>(StringComparer.Ordinal);
            ValidateApiList(publicApiMap, apiData.ApiList, errors);

            return errors.Count == 0;
        }

        private static void ValidateApiList(Dictionary<string, ApiLine> publicApiMap, ImmutableArray<ApiLine> apiList, List<Diagnostic> errors)
        {
            foreach (ApiLine cur in apiList)
            {
                if (publicApiMap.TryGetValue(cur.Text, out ApiLine existingLine))
                {
                    LinePositionSpan existingLinePositionSpan = existingLine.SourceText.Lines.GetLinePositionSpan(existingLine.Span);
                    Location existingLocation = Location.Create(existingLine.Path, existingLine.Span, existingLinePositionSpan);

                    LinePositionSpan duplicateLinePositionSpan = cur.SourceText.Lines.GetLinePositionSpan(cur.Span);
                    Location duplicateLocation = Location.Create(cur.Path, cur.Span, duplicateLinePositionSpan);
                    errors.Add(Diagnostic.Create(DuplicateBannedSymbolRule, duplicateLocation, new[] { existingLocation }, cur.Text));
                }
                else
                {
                    publicApiMap.Add(cur.Text, cur);
                }
            }
        }

        private class ApiData
        {
            public ApiData(ImmutableArray<ApiLine> apiList)
                => ApiList = apiList;

            public ImmutableArray<ApiLine> ApiList { get; }
        }

        private class ApiLine
        {
            public TextSpan Span { get; }
            public SourceText SourceText { get; }
            public string Path { get; }
            public string Text { get; }

            public ApiLine(string text, TextSpan span, SourceText sourceText, string path)
            {
                Text = text;
                Span = span;
                SourceText = sourceText;
                Path = path;
            }
        }
    }
}
