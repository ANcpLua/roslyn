﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Copilot;
using Microsoft.CodeAnalysis.DocumentationComments;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.CodeAnalysis.Threading;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Language.Suggestions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json;
using Roslyn.Utilities;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace Microsoft.CodeAnalysis.DocumentationComments;

internal abstract class AbstractDocumentationCommentCommandHandler : SuggestionProviderBase,
    IChainedCommandHandler<TypeCharCommandArgs>,
    ICommandHandler<ReturnKeyCommandArgs>,
    ICommandHandler<InsertCommentCommandArgs>,
    IChainedCommandHandler<OpenLineAboveCommandArgs>,
    IChainedCommandHandler<OpenLineBelowCommandArgs>
{
    private readonly IUIThreadOperationExecutor _uiThreadOperationExecutor;
    private readonly ITextUndoHistoryRegistry _undoHistoryRegistry;
    private readonly IEditorOperationsFactoryService _editorOperationsFactoryService;
    private readonly EditorOptionsService _editorOptionsService;
    private readonly SuggestionServiceBase? _suggestionServiceBase;
    private readonly IAsynchronousOperationListener _asyncListener;

    private SuggestionManagerBase? _suggestionManagerBase;
    private VisualStudio.Threading.IAsyncDisposable? _intellicodeLineCompletionsDisposable;

    internal SuggestionSessionBase? _suggestionSession;

    public readonly IThreadingContext? ThreadingContext;

    protected AbstractDocumentationCommentCommandHandler(
        IUIThreadOperationExecutor uiThreadOperationExecutor,
        ITextUndoHistoryRegistry undoHistoryRegistry,
        IEditorOperationsFactoryService editorOperationsFactoryService,
        EditorOptionsService editorOptionsService,
        SuggestionServiceBase? suggestionServiceBase,
        IThreadingContext? threadingContext,
        IAsynchronousOperationListenerProvider listenerProvider)
    {
        Contract.ThrowIfNull(uiThreadOperationExecutor);
        Contract.ThrowIfNull(undoHistoryRegistry);
        Contract.ThrowIfNull(editorOperationsFactoryService);

        _uiThreadOperationExecutor = uiThreadOperationExecutor;
        _undoHistoryRegistry = undoHistoryRegistry;
        _editorOperationsFactoryService = editorOperationsFactoryService;
        _editorOptionsService = editorOptionsService;
        _suggestionServiceBase = suggestionServiceBase;
        _asyncListener = listenerProvider.GetListener(FeatureAttribute.GenerateDocumentation);
        ThreadingContext = threadingContext;
    }

    protected abstract string ExteriorTriviaText { get; }

    private char TriggerCharacter => ExteriorTriviaText[^1];

    public string DisplayName => EditorFeaturesResources.Documentation_Comment;

    private static DocumentationCommentSnippet? InsertOnCharacterTyped(IDocumentationCommentSnippetService service, ParsedDocument document, int position, DocumentationCommentOptions options, CancellationToken cancellationToken)
        => service.GetDocumentationCommentSnippetOnCharacterTyped(document, position, options, cancellationToken);

    private static DocumentationCommentSnippet? InsertOnEnterTyped(IDocumentationCommentSnippetService service, ParsedDocument document, int position, DocumentationCommentOptions options, CancellationToken cancellationToken)
        => service.GetDocumentationCommentSnippetOnEnterTyped(document, position, options, cancellationToken);

    private static DocumentationCommentSnippet? InsertOnCommandInvoke(IDocumentationCommentSnippetService service, ParsedDocument document, int position, DocumentationCommentOptions options, CancellationToken cancellationToken)
        => service.GetDocumentationCommentSnippetOnCommandInvoke(document, position, options, cancellationToken);

    private static void ApplySnippet(DocumentationCommentSnippet snippet, ITextBuffer subjectBuffer, ITextView textView)
    {
        var replaceSpan = snippet.SpanToReplace.ToSpan();
        subjectBuffer.Replace(replaceSpan, snippet.SnippetText);
        textView.TryMoveCaretToAndEnsureVisible(subjectBuffer.CurrentSnapshot.GetPoint(replaceSpan.Start + snippet.CaretOffset));
    }

    private bool CompleteComment(
        ITextBuffer subjectBuffer,
        ITextView textView,
        Func<IDocumentationCommentSnippetService, ParsedDocument, int, DocumentationCommentOptions, CancellationToken, DocumentationCommentSnippet?> getSnippetAction,
        CancellationToken cancellationToken)
    {
        var caretPosition = textView.GetCaretPoint(subjectBuffer) ?? -1;
        if (caretPosition < 0)
            return false;

        var document = subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
        if (document == null)
            return false;

        var service = document.GetRequiredLanguageService<IDocumentationCommentSnippetService>();
        var parsedDocument = ParsedDocument.CreateSynchronously(document, cancellationToken);
        var options = subjectBuffer.GetDocumentationCommentOptions(_editorOptionsService, document.Project.Services);

        // Apply snippet in reverse order so that the first applied snippet doesn't affect span of next snippets.
        var snapshots = textView.Selection.GetSnapshotSpansOnBuffer(subjectBuffer).OrderByDescending(s => s.Span.Start);
        var returnValue = false;
        foreach (var snapshot in snapshots)
        {
            var snippet = getSnippetAction(service, parsedDocument, snapshot.Span.Start, options, cancellationToken);
            if (snippet != null)
            {
                ApplySnippet(snippet, subjectBuffer, textView);
                var oldSnapshot = subjectBuffer.CurrentSnapshot;
                var oldCaret = textView.Caret.Position.VirtualBufferPosition;

                returnValue = true;

                // Only calls into the suggestion manager is available, the shell of the comment still gets inserted regardless.
                if (_suggestionManagerBase != null && ThreadingContext != null)
                {
                    ThreadingContext.ThrowIfNotOnUIThread();

                    var token = _asyncListener.BeginAsyncOperation(nameof(GenerateDocumentationProposalAsync));
                    _ = GenerateDocumentationProposalAsync(document, snippet, oldSnapshot, oldCaret, cancellationToken).CompletesAsyncOperation(token);
                }
            }
        }

        return returnValue;
    }

    private async Task GenerateDocumentationProposalAsync(Document document, DocumentationCommentSnippet snippet,
        ITextSnapshot oldSnapshot, VirtualSnapshotPoint oldCaret, CancellationToken cancellationToken)
    {
        await Task.Yield().ConfigureAwait(false);

        // Bailing out if copilot is not available or the option is not enabled.
        if (document.GetRequiredLanguageService<ICopilotCodeAnalysisService>() is not { } copilotService ||
                await copilotService.IsAvailableAsync(cancellationToken).ConfigureAwait(false) is false)
        {
            return;
        }

        if (document.GetLanguageService<ICopilotOptionsService>() is not { } copilotOptionService ||
            !await copilotOptionService.IsGenerateDocumentationCommentOptionEnabledAsync().ConfigureAwait(false))
        {
            return;
        }

        var snippetProposal = GetSnippetProposal(snippet.SnippetText, snippet.MemberNode, snippet.Position, snippet.CaretOffset);

        if (snippetProposal is null)
        {
            return;
        }

        // Do not do IntelliCode line completions if we're about to generate a documentation comment
        // so that won't have interfering grey text.
        _intellicodeLineCompletionsDisposable = await _suggestionManagerBase!.DisableProviderAsync(SuggestionServiceNames.IntelliCodeLineCompletions, cancellationToken).ConfigureAwait(false);

        var proposalEdits = await GetProposedEditsAsync(snippetProposal, copilotService, oldSnapshot, snippet.IndentText, cancellationToken).ConfigureAwait(false);

        var proposal = Proposal.TryCreateProposal(null, proposalEdits, oldCaret, flags: ProposalFlags.SingleTabToAccept);

        if (proposal is null)
        {
            return;
        }

        var suggestion = new DocumentationCommentSuggestion(this, proposal);

        var session = this._suggestionSession = await (_suggestionManagerBase.TryDisplaySuggestionAsync(suggestion, cancellationToken)).ConfigureAwait(false);

        if (session != null)
        {
            await TryDisplaySuggestionAsync(session, suggestion, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Traverses the documentation comment shell and retrieves the pieces that are needed to generate the documentation comment.
    /// </summary>
    private static DocumentationCommentProposal? GetSnippetProposal(string? comments, SyntaxNode? memberNode, int? position, int caret)
    {
        if (comments is null)
        {
            return null;
        }

        if (memberNode is null)
        {
            return null;
        }

        if (position is null)
        {
            return null;
        }

        var startIndex = position.Value;
        var proposedEdits = new List<DocumentationCommentProposedEdit>();
        var index = 0;

        var summaryStartTag = comments.IndexOf("<summary>", index, StringComparison.Ordinal);
        var summaryEndTag = comments.IndexOf("</summary>", index, StringComparison.Ordinal);
        if (summaryEndTag != -1 && summaryStartTag != -1)
        {
            proposedEdits.Add(new DocumentationCommentProposedEdit(new TextSpan(caret + startIndex, 0), null, DocumentationCommentTagType.Summary));
        }

        while (true)
        {
            var paramEndTag = comments.IndexOf("</param>", index, StringComparison.Ordinal);
            var paramStartTag = comments.IndexOf("<param name=\"", index, StringComparison.Ordinal);

            if (paramStartTag == -1 || paramEndTag == -1)
            {
                break;
            }

            var paramNameStart = paramStartTag + "<param name=\"".Length;
            var paramNameEnd = comments.IndexOf("\">", paramNameStart, StringComparison.Ordinal);
            if (paramNameEnd != -1)
            {
                var parameterName = comments.Substring(paramNameStart, paramNameEnd - paramNameStart);
                proposedEdits.Add(new DocumentationCommentProposedEdit(new TextSpan(paramEndTag + startIndex, 0), parameterName, DocumentationCommentTagType.Param));
            }

            index = paramEndTag + "</param>".Length;
        }

        var returnsEndTag = comments.IndexOf("</returns>", index, StringComparison.Ordinal);
        if (returnsEndTag != -1)
        {
            proposedEdits.Add(new DocumentationCommentProposedEdit(new TextSpan(returnsEndTag + startIndex, 0), null, DocumentationCommentTagType.Returns));
        }

        while (true)
        {
            var exceptionEndTag = comments.IndexOf("</exception>", index, StringComparison.Ordinal);
            var exceptionStartTag = comments.IndexOf("<exception cref=\"", index, StringComparison.Ordinal);

            if (exceptionEndTag == -1 || exceptionStartTag == -1)
            {
                break;
            }

            var exceptionNameStart = exceptionStartTag + "<exception cref=\"".Length;
            var exceptionNameEnd = comments.IndexOf("\">", exceptionNameStart, StringComparison.Ordinal);
            if (exceptionNameEnd != -1)
            {
                var exceptionName = comments.Substring(exceptionNameStart, exceptionNameEnd - exceptionNameStart);
                proposedEdits.Add(new DocumentationCommentProposedEdit(new TextSpan(exceptionEndTag + startIndex, 0), exceptionName, DocumentationCommentTagType.Exception));
            }

            index = exceptionEndTag + "</exception>".Length;
        }

        return new DocumentationCommentProposal(memberNode.ToFullString(), proposedEdits.ToImmutableArray());
    }

    /// <summary>
    /// Calls into the copilot service to get the pieces for the documentation comment.
    /// </summary>
    private static async Task<IReadOnlyList<ProposedEdit>> GetProposedEditsAsync(
        DocumentationCommentProposal proposal, ICopilotCodeAnalysisService copilotService,
        ITextSnapshot oldSnapshot, string? indentText, CancellationToken cancellationToken)
    {
        var list = new List<ProposedEdit>();
        var (copilotText, isQuotaExceeded) = await copilotService.GetDocumentationCommentAsync(proposal, cancellationToken).ConfigureAwait(false);

        // Quietly fail if the quota has been exceeded.
        if (isQuotaExceeded)
        {
            return list;
        }

        // The response from Copilot is structured like a JSON object, so make sure it is being returned appropriately.
        if (copilotText is null || copilotText.AsSpan().Trim() is "{}" or "{ }" or "")
        {
            return list;
        }

        // If the response can't be properly converted, something went wrong and bail out.
        Dictionary<string, string>? props;
        try
        {
            props = JsonConvert.DeserializeObject<Dictionary<string, string>>(copilotText);
        }
        catch (Exception)
        {
            return list;
        }

        if (props is null)
        {
            return list;
        }

        foreach (var edit in proposal.ProposedEdits)
        {
            string? copilotStatement = null;
            var textSpan = edit.SpanToReplace;

            if (edit.TagType == DocumentationCommentTagType.Summary && props.TryGetValue(DocumentationCommentTagType.Summary.ToString(), out var summary) && !string.IsNullOrEmpty(summary))
            {
                copilotStatement = summary;
            }
            else if (edit.TagType == DocumentationCommentTagType.Param && props.TryGetValue(edit.SymbolName!, out var param) && !string.IsNullOrEmpty(param))
            {
                copilotStatement = param;
            }
            else if (edit.TagType == DocumentationCommentTagType.Returns && props.TryGetValue(DocumentationCommentTagType.Returns.ToString(), out var returns) && !string.IsNullOrEmpty(returns))
            {
                copilotStatement = returns;
            }
            else if (edit.TagType == DocumentationCommentTagType.Exception && props.TryGetValue(edit.SymbolName!, out var exception) && !string.IsNullOrEmpty(exception))
            {
                copilotStatement = exception;
            }

            var proposedEdit = new ProposedEdit(new SnapshotSpan(oldSnapshot, textSpan.Start, textSpan.Length),
                AddNewLinesToCopilotText(copilotStatement!, indentText, characterLimit: 120));
            list.Add(proposedEdit);
        }

        return list;

        static string AddNewLinesToCopilotText(string copilotText, string? indentText, int characterLimit)
        {
            var builder = new StringBuilder();
            var words = copilotText.Split(' ');
            var currentLineLength = 0;
            characterLimit -= (indentText!.Length + "/// ".Length);
            foreach (var word in words)
            {
                if (currentLineLength + word.Length >= characterLimit)
                {
                    builder.AppendLine();
                    builder.Append(indentText);
                    builder.Append("/// ");
                    currentLineLength = 0;
                }

                if (currentLineLength > 0)
                {
                    builder.Append(' ');
                    currentLineLength++;
                }

                builder.Append(word);
                currentLineLength += word.Length;
            }

            return builder.ToString();
        }
    }

    private async Task<bool> TryDisplaySuggestionAsync(SuggestionSessionBase session, DocumentationCommentSuggestion suggestion, CancellationToken cancellationToken)
    {
        if (ThreadingContext is null)
        {
            return false;
        }

        try
        {
            await ThreadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await session.DisplayProposalAsync(suggestion.Proposal, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
        }

        return false;
    }

    public async Task ClearSuggestionAsync(ReasonForDismiss reason, CancellationToken cancellationToken)
    {
        if (_suggestionSession != null)
        {
            await _suggestionSession.DismissAsync(reason, cancellationToken).ConfigureAwait(false);
        }

        _suggestionSession = null;
        await DisposeAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_intellicodeLineCompletionsDisposable != null)
        {
            await _intellicodeLineCompletionsDisposable.DisposeAsync().ConfigureAwait(false);
            _intellicodeLineCompletionsDisposable = null;
        }
    }

    public CommandState GetCommandState(TypeCharCommandArgs args, Func<CommandState> nextHandler)
        => nextHandler();

    public void ExecuteCommand(TypeCharCommandArgs args, Action nextHandler, CommandExecutionContext context)
    {
        // Ensure the character is actually typed in the editor
        nextHandler();

        if (args.TypedChar != TriggerCharacter)
            return;

        // Don't execute in cloud environment, as we let LSP handle that
        if (args.SubjectBuffer.IsInLspEditorContext())
            return;

        ThreadingContext?.JoinableTaskFactory.Run(async () =>
            {
                if (_suggestionServiceBase is not null)
                {
                    _suggestionManagerBase = await _suggestionServiceBase.TryRegisterProviderAsync(this, args.TextView, "AmbientAIDocumentationComments", context.OperationContext.UserCancellationToken).ConfigureAwait(false);
                }
            });

        CompleteComment(args.SubjectBuffer, args.TextView, InsertOnCharacterTyped, CancellationToken.None);
    }

    public CommandState GetCommandState(ReturnKeyCommandArgs args)
        => CommandState.Unspecified;

    public bool ExecuteCommand(ReturnKeyCommandArgs args, CommandExecutionContext context)
    {
        var cancellationToken = context.OperationContext.UserCancellationToken;

        // Don't execute in cloud environment, as we let LSP handle that
        if (args.SubjectBuffer.IsInLspEditorContext())
        {
            return false;
        }

        // Check to see if the current line starts with exterior trivia. If so, we'll take over.
        // If not, let the nextHandler run.

        var originalPosition = -1;

        // The original position should be a position that is consistent with the syntax tree, even
        // after Enter is pressed. Thus, we use the start of the first selection if there is one.
        // Otherwise, getting the tokens to the right or the left might return unexpected results.

        if (args.TextView.Selection.SelectedSpans.Count > 0)
        {
            var selectedSpan = args.TextView.Selection
                .GetSnapshotSpansOnBuffer(args.SubjectBuffer)
                .FirstOrNull();

            originalPosition = selectedSpan != null
                ? selectedSpan.Value.Start
                : args.TextView.GetCaretPoint(args.SubjectBuffer) ?? -1;
        }

        if (originalPosition < 0)
        {
            return false;
        }

        if (!CurrentLineStartsWithExteriorTrivia(args.SubjectBuffer, originalPosition, cancellationToken))
        {
            return false;
        }

        // According to JasonMal, the text undo history is associated with the surface buffer
        // in projection buffer scenarios, so the following line's usage of the surface buffer
        // is correct.
        using var transaction = _undoHistoryRegistry.GetHistory(args.TextView.TextBuffer).CreateTransaction(EditorFeaturesResources.Insert_new_line);
        var editorOperations = _editorOperationsFactoryService.GetEditorOperations(args.TextView);
        editorOperations.InsertNewLine();

        CompleteComment(args.SubjectBuffer, args.TextView, InsertOnEnterTyped, CancellationToken.None);

        // Since we're wrapping the ENTER key undo transaction, we always complete
        // the transaction -- even if we didn't generate anything.
        transaction.Complete();

        return true;
    }

    public CommandState GetCommandState(InsertCommentCommandArgs args)
    {
        var caretPosition = args.TextView.GetCaretPoint(args.SubjectBuffer) ?? -1;
        if (caretPosition < 0)
        {
            return CommandState.Unavailable;
        }

        var document = args.SubjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
        if (document == null)
        {
            return CommandState.Unavailable;
        }

        var service = document.GetRequiredLanguageService<IDocumentationCommentSnippetService>();

        var isValidTargetMember = false;
        _uiThreadOperationExecutor.Execute("IntelliSense", defaultDescription: "", allowCancellation: true, showProgress: false, action: c =>
        {
            var parsedDocument = ParsedDocument.CreateSynchronously(document, c.UserCancellationToken);
            isValidTargetMember = service.IsValidTargetMember(parsedDocument, caretPosition, c.UserCancellationToken);
        });

        return isValidTargetMember
            ? CommandState.Available
            : CommandState.Unavailable;
    }

    public bool ExecuteCommand(InsertCommentCommandArgs args, CommandExecutionContext context)
    {
        using (context.OperationContext.AddScope(allowCancellation: true, EditorFeaturesResources.Inserting_documentation_comment))
        {
            return CompleteComment(args.SubjectBuffer, args.TextView, InsertOnCommandInvoke, context.OperationContext.UserCancellationToken);
        }
    }

    public CommandState GetCommandState(OpenLineAboveCommandArgs args, Func<CommandState> nextHandler)
        => nextHandler();

    public void ExecuteCommand(OpenLineAboveCommandArgs args, Action nextHandler, CommandExecutionContext context)
    {
        // Check to see if the current line starts with exterior trivia. If so, we'll take over.
        // If not, let the nextHandler run.

        var subjectBuffer = args.SubjectBuffer;
        var caretPosition = args.TextView.GetCaretPoint(subjectBuffer) ?? -1;
        if (caretPosition < 0)
        {
            nextHandler();
            return;
        }

        if (!CurrentLineStartsWithExteriorTrivia(subjectBuffer, caretPosition, context.OperationContext.UserCancellationToken))
        {
            nextHandler();
            return;
        }

        // Allow nextHandler() to run and then insert exterior trivia if necessary.
        nextHandler();

        var document = subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
        if (document == null)
        {
            return;
        }

        var service = document.GetRequiredLanguageService<IDocumentationCommentSnippetService>();

        InsertExteriorTriviaIfNeeded(service, args.TextView, subjectBuffer, context.OperationContext.UserCancellationToken);
    }

    public CommandState GetCommandState(OpenLineBelowCommandArgs args, Func<CommandState> nextHandler)
        => nextHandler();

    public void ExecuteCommand(OpenLineBelowCommandArgs args, Action nextHandler, CommandExecutionContext context)
    {
        // Check to see if the current line starts with exterior trivia. If so, we'll take over.
        // If not, let the nextHandler run.

        var subjectBuffer = args.SubjectBuffer;
        var caretPosition = args.TextView.GetCaretPoint(subjectBuffer) ?? -1;
        if (caretPosition < 0)
        {
            nextHandler();
            return;
        }

        if (!CurrentLineStartsWithExteriorTrivia(subjectBuffer, caretPosition, context.OperationContext.UserCancellationToken))
        {
            nextHandler();
            return;
        }

        var document = subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
        if (document == null)
        {
            return;
        }

        var service = document.GetRequiredLanguageService<IDocumentationCommentSnippetService>();

        // Allow nextHandler() to run and the insert exterior trivia if necessary.
        nextHandler();

        InsertExteriorTriviaIfNeeded(service, args.TextView, subjectBuffer, context.OperationContext.UserCancellationToken);
    }

    private void InsertExteriorTriviaIfNeeded(IDocumentationCommentSnippetService service, ITextView textView, ITextBuffer subjectBuffer, CancellationToken cancellationToken)
    {
        var caretPosition = textView.GetCaretPoint(subjectBuffer) ?? -1;
        if (caretPosition < 0)
        {
            return;
        }

        var document = subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
        if (document == null)
        {
            return;
        }

        var parsedDocument = ParsedDocument.CreateSynchronously(document, cancellationToken);

        // We only insert exterior trivia if the current line does not start with exterior trivia
        // and the previous line does.

        var currentLine = parsedDocument.Text.Lines.GetLineFromPosition(caretPosition);
        if (currentLine.LineNumber <= 0)
        {
            return;
        }

        var previousLine = parsedDocument.Text.Lines[currentLine.LineNumber - 1];

        if (LineStartsWithExteriorTrivia(currentLine) || !LineStartsWithExteriorTrivia(previousLine))
        {
            return;
        }

        var options = subjectBuffer.GetDocumentationCommentOptions(_editorOptionsService, document.Project.Services);

        var snippet = service.GetDocumentationCommentSnippetFromPreviousLine(options, currentLine, previousLine);
        if (snippet != null)
        {
            ApplySnippet(snippet, subjectBuffer, textView);
        }
    }

    private bool CurrentLineStartsWithExteriorTrivia(ITextBuffer subjectBuffer, int position, CancellationToken cancellationToken)
    {
        var document = subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
        if (document == null)
        {
            return false;
        }

        var parsedDocument = ParsedDocument.CreateSynchronously(document, cancellationToken);
        var currentLine = parsedDocument.Text.Lines.GetLineFromPosition(position);

        return LineStartsWithExteriorTrivia(currentLine);
    }

    private bool LineStartsWithExteriorTrivia(TextLine line)
    {
        var lineText = line.ToString();

        var lineOffset = lineText.GetFirstNonWhitespaceOffset() ?? -1;
        if (lineOffset < 0)
        {
            return false;
        }

        return string.CompareOrdinal(lineText, lineOffset, ExteriorTriviaText, 0, ExteriorTriviaText.Length) == 0;
    }
}
