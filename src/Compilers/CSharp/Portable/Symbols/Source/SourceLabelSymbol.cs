﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    internal sealed class SourceLabelSymbol : LabelSymbol
    {
        private readonly MethodSymbol _containingMethod;
        private readonly SyntaxNodeOrToken _identifierNodeOrToken;

        /// <summary>
        /// Switch case labels have a constant expression associated with them.
        /// </summary>
        private readonly ConstantValue _switchCaseLabelConstant;

        public SourceLabelSymbol(
            MethodSymbol containingMethod,
            SyntaxNodeOrToken identifierNodeOrToken,
            ConstantValue switchCaseLabelConstant = null)
            : base(identifierNodeOrToken.IsToken ? identifierNodeOrToken.AsToken().ValueText : identifierNodeOrToken.ToString())
        {
            _containingMethod = containingMethod;
            _identifierNodeOrToken = identifierNodeOrToken;
            _switchCaseLabelConstant = switchCaseLabelConstant;
        }

        public SourceLabelSymbol(
            MethodSymbol containingMethod,
            ConstantValue switchCaseLabelConstant = null)
            : base(switchCaseLabelConstant.ToString())
        {
            _containingMethod = containingMethod;
            _identifierNodeOrToken = default(SyntaxToken);
            _switchCaseLabelConstant = switchCaseLabelConstant;
        }

        public override ImmutableArray<Location> Locations
        {
            get
            {
                return _identifierNodeOrToken.IsToken && _identifierNodeOrToken.Parent == null
                    ? ImmutableArray<Location>.Empty
                    : ImmutableArray.Create<Location>(_identifierNodeOrToken.GetLocation());
            }
        }

        public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences
        {
            get
            {
                CSharpSyntaxNode node = null;

                if (_identifierNodeOrToken.IsToken)
                {
                    if (_identifierNodeOrToken.Parent != null)
                        node = _identifierNodeOrToken.Parent.FirstAncestorOrSelf<LabeledStatementSyntax>();
                }
                else
                {
                    node = _identifierNodeOrToken.AsNode().FirstAncestorOrSelf<SwitchLabelSyntax>();
                }

                return node == null ? ImmutableArray<SyntaxReference>.Empty : ImmutableArray.Create<SyntaxReference>(node.GetReference());
            }
        }

        public override MethodSymbol ContainingMethod
        {
            get
            {
                return _containingMethod;
            }
        }

        public override Symbol ContainingSymbol
        {
            get
            {
                return _containingMethod;
            }
        }

        // Get the identifier node or token that defined this label symbol. This is useful for robustly
        // checking if a label symbol actually matches a particular definition, even in the presence
        // of duplicates.
        internal override SyntaxNodeOrToken IdentifierNodeOrToken
        {
            get
            {
                return _identifierNodeOrToken;
            }
        }

        /// <summary>
        /// If the label is a switch case label, returns the associated constant value with
        /// case expression, otherwise returns null.
        /// </summary>
        public ConstantValue SwitchCaseLabelConstant
        {
            get
            {
                return _switchCaseLabelConstant;
            }
        }

        public override bool Equals(object obj)
        {
            if (obj == (object)this)
            {
                return true;
            }

            var symbol = obj as SourceLabelSymbol;
            return (object)symbol != null
                && symbol._identifierNodeOrToken.Kind() != SyntaxKind.None
                && symbol._identifierNodeOrToken.Equals(_identifierNodeOrToken)
                && Equals(symbol._containingMethod, _containingMethod);
        }

        public override int GetHashCode()
        {
            return _identifierNodeOrToken.GetHashCode();
        }
    }
}
