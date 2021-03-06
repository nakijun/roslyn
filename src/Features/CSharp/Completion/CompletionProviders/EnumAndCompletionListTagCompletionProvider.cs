// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Completion.Providers;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Extensions.ContextQuery;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Completion.Providers
{
    internal class EnumAndCompletionListTagCompletionProvider : AbstractCompletionProvider
    {
        public override bool IsCommitCharacter(CompletionItem completionItem, char ch, string textTypedSoFar)
        {
            // Only commit on dot.
            return ch == '.';
        }

        public override bool SendEnterThroughToEditor(CompletionItem completionItem, string textTypedSoFar)
        {
            // Standard enter behavior.
            return CompletionUtilities.SendEnterThroughToEditor(completionItem, textTypedSoFar);
        }

        public override bool IsTriggerCharacter(SourceText text, int characterPosition, OptionSet options)
        {
            // Bring up on space or at the start of a word, or after a ( or [.
            //
            // Note: we don't want to bring this up after traditional enum operators like & or |.
            // That's because we don't like the experience where the enum appears directly after the
            // operator.  Instead, the user normally types <space> and we will bring up the list
            // then.
            var ch = text[characterPosition];
            return
                ch == ' ' ||
                ch == '[' ||
                ch == '(' ||
                (options.GetOption(CompletionOptions.TriggerOnTypingLetters, LanguageNames.CSharp) && CompletionUtilities.IsStartingNewWord(text, characterPosition));
        }

        protected override async Task<IEnumerable<CompletionItem>> GetItemsWorkerAsync(
            Document document, int position, CompletionTriggerInfo triggerInfo,
            CancellationToken cancellationToken)
        {
            var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            if (tree.IsInNonUserCode(position, cancellationToken))
            {
                return null;
            }

            var token = tree.FindTokenOnLeftOfPosition(position, cancellationToken);
            if (token.IsMandatoryNamedParameterPosition())
            {
                return null;
            }

            var typeInferenceService = document.GetLanguageService<ITypeInferenceService>();

            var span = new TextSpan(position, 0);
            var semanticModel = await document.GetSemanticModelForSpanAsync(span, cancellationToken).ConfigureAwait(false);
            var type = typeInferenceService.InferType(semanticModel, position,
                objectAsDefault: true,
                cancellationToken: cancellationToken);

            // If we have a Nullable<T>, unwrap it.
            if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                type = type.GetTypeArguments().FirstOrDefault();

                if (type == null)
                {
                    return null;
                }
            }

            if (type.TypeKind != TypeKind.Enum)
            {
                type = GetCompletionListType(type, semanticModel.GetEnclosingNamedType(position, cancellationToken), semanticModel.Compilation);
                if (type == null)
                {
                    return null;
                }
            }

            if (!type.IsEditorBrowsable(document.ShouldHideAdvancedMembers(), semanticModel.Compilation))
            {
                return null;
            }

            // Does type have any aliases?
            ISymbol alias = await type.FindApplicableAlias(position, semanticModel, cancellationToken).ConfigureAwait(false);

            var displayService = document.GetLanguageService<ISymbolDisplayService>();
            var displayText = alias != null
                ? alias.Name
                : displayService.ToMinimalDisplayString(semanticModel, position, type);

            var workspace = document.Project.Solution.Workspace;
            var text = await semanticModel.SyntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var textChangeSpan = CompletionUtilities.GetTextChangeSpan(text, position);

            var item = new CSharpCompletionItem(
                workspace,
                this,
                displayText: displayText,
                filterSpan: textChangeSpan,
                descriptionFactory: CommonCompletionUtilities.CreateDescriptionFactory(workspace, semanticModel, position, alias ?? type),
                glyph: (alias ?? type).GetGlyph(),
                preselect: true);
            return SpecializedCollections.SingletonEnumerable(item);
        }

        private INamedTypeSymbol GetCompletionListType(ITypeSymbol type, INamedTypeSymbol within, Compilation compilation)
        {
            // PERF: None of the SpecialTypes include <completionlist> tags,
            // so we don't even need to load the documentation.
            if (type.IsSpecialType())
            {
                return null;
            }

            // PERF: Avoid parsing XML unless the text contains the word "completionlist".
            string xmlText = type.GetDocumentationCommentXml();
            if (xmlText == null || !xmlText.Contains(DocumentationCommentXmlNames.CompletionListElementName))
            {
                return null;
            }

            var documentation = Shared.Utilities.DocumentationComment.FromXmlFragment(xmlText);

            var completionListType = documentation.CompletionListCref != null
                ? DocumentationCommentId.GetSymbolsForDeclarationId(documentation.CompletionListCref, compilation).OfType<INamedTypeSymbol>().FirstOrDefault()
                : null;

            return completionListType != null && completionListType.IsAccessibleWithin(within)
                ? completionListType
                : null;
        }
    }
}
