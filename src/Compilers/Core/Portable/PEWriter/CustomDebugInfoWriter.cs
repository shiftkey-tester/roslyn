﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Roslyn.Utilities;
using CDI = Microsoft.Cci.CustomDebugInfoConstants;

namespace Microsoft.Cci
{
    internal sealed class CustomDebugInfoWriter
    {
        private uint _methodTokenWithModuleInfo;
        private IMethodBody _methodBodyWithModuleInfo;

        private uint _previousMethodTokenWithUsingInfo;
        private IMethodBody _previousMethodBodyWithUsingInfo;

        private readonly PdbWriter _pdbWriter;

        public CustomDebugInfoWriter(PdbWriter pdbWriter)
        {
            Debug.Assert(pdbWriter != null);
            _pdbWriter = pdbWriter;
        }

        /// <summary>
        /// Returns true if the namespace scope for this method should be forwarded to another method.
        /// Returns non-null <paramref name="forwardToMethod"/> if the forwarding should be done directly via UsingNamespace,
        /// null if the forwarding is done via custom debug info.
        /// </summary>
        public bool ShouldForwardNamespaceScopes(EmitContext context, IMethodBody methodBody, uint methodToken, out IMethodDefinition forwardToMethod)
        {
            if (ShouldForwardToPreviousMethodWithUsingInfo(context, methodBody))
            {
                // SerializeNamespaceScopeMetadata will do the actual forwarding in case this is a CSharp method.
                // VB on the other hand adds a "@methodtoken" to the scopes instead.
                if (context.Module.GenerateVisualBasicStylePdb)
                {
                    forwardToMethod = _previousMethodBodyWithUsingInfo.MethodDefinition;
                }
                else
                {
                    forwardToMethod = null;
                }

                return true;
            }

            _previousMethodBodyWithUsingInfo = methodBody;
            _previousMethodTokenWithUsingInfo = methodToken;
            forwardToMethod = null;
            return false;
        }

        public byte[] SerializeMethodDebugInfo(EmitContext context, IMethodBody methodBody, uint methodToken, bool isEncDelta, bool suppressNewCustomDebugInfo, out bool emitExternNamespaces)
        {
            emitExternNamespaces = false;

            // CONSIDER: this may not be the same "first" method as in Dev10, but
            // it shouldn't matter since all methods will still forward to a method
            // containing the appropriate information.
            if (_methodBodyWithModuleInfo == null) //UNDONE: || edit-and-continue
            {
                // This module level information could go on every method (and does in
                // the edit-and-continue case), but - as an optimization - we'll just
                // put it on the first method we happen to encounter and then put a
                // reference to the first method's token in every other method (so they
                // can find the information).
                if (context.Module.GetAssemblyReferenceAliases(context).Any())
                {
                    _methodTokenWithModuleInfo = methodToken;
                    _methodBodyWithModuleInfo = methodBody;
                    emitExternNamespaces = true;
                }
            }

            var customDebugInfo = ArrayBuilder<BlobWriter>.GetInstance();

            SerializeIteratorClassMetadata(methodBody, customDebugInfo);

            // NOTE: This is an attempt to match Dev10's apparent behavior.  For iterator methods (i.e. the method
            // that appears in source, not the synthesized ones), Dev10 only emits the ForwardIterator and IteratorLocal
            // custom debug info (e.g. there will be no information about the usings that were in scope).
            // NOTE: There seems to be an unusual behavior in ISymUnmanagedWriter where, if all the methods in a type are
            // iterator methods, no custom debug info is emitted for any method.  Adding a single non-iterator
            // method causes the custom debug info to be produced for all methods (including the iterator methods).
            // Since we are making the same ISymUnmanagedWriter calls as Dev10, we see the same behavior (i.e. this
            // is not a regression).
            if (methodBody.StateMachineTypeName == null)
            {
                SerializeNamespaceScopeMetadata(context, methodBody, customDebugInfo);
                SerializeStateMachineLocalScopes(methodBody, customDebugInfo);
            }

            if (!suppressNewCustomDebugInfo)
            {
                SerializeDynamicLocalInfo(methodBody, customDebugInfo);

                // delta doesn't need this information - we use information recorded by previous generation emit
                if (!isEncDelta)
                {
                    var encMethodInfo = MetadataWriter.GetEncMethodDebugInfo(methodBody);
                    SerializeCustomDebugInformation(encMethodInfo, customDebugInfo);
                }
            }

            byte[] result = SerializeCustomDebugMetadata(customDebugInfo);
            customDebugInfo.Free();
            return result;
        }

        // internal for testing
        internal static void SerializeCustomDebugInformation(EditAndContinueMethodDebugInformation debugInfo, ArrayBuilder<BlobWriter> customDebugInfo)
        {
            if (!debugInfo.LocalSlots.IsDefaultOrEmpty)
            {
                customDebugInfo.Add(SerializeRecord(CDI.CdiKindEditAndContinueLocalSlotMap, debugInfo.SerializeLocalSlots));
            }

            if (!debugInfo.Lambdas.IsDefaultOrEmpty)
            {
                customDebugInfo.Add(SerializeRecord(CDI.CdiKindEditAndContinueLambdaMap, debugInfo.SerializeLambdaMap));
            }
        }

        private static BlobWriter SerializeRecord(byte kind, Action<BlobWriter> data)
        {
            var cmw = new BlobWriter();
            cmw.WriteByte(CDI.CdiVersion);
            cmw.WriteByte(kind);
            cmw.WriteByte(0);

            // alignment size (will be patched)
            uint alignmentSizeAndLengthPosition = cmw.Position;
            cmw.WriteByte(0);

            // length (will be patched)
            cmw.WriteUint(0);

            data(cmw);

            uint length = cmw.Position;
            uint alignedLength = 4 * ((length + 3) / 4);
            byte alignmentSize = (byte)(alignedLength - length);

            for (int i = 0; i < alignmentSize; i++)
            {
                cmw.WriteByte(0);
            }

            cmw.Position = alignmentSizeAndLengthPosition;
            cmw.WriteByte(alignmentSize);
            cmw.WriteUint(alignedLength);

            cmw.Position = length;
            return cmw;
        }

        private static void SerializeIteratorClassMetadata(IMethodBody methodBody, ArrayBuilder<BlobWriter> customDebugInfo)
        {
            SerializeReferenceToIteratorClass(methodBody.StateMachineTypeName, customDebugInfo);
        }

        private static void SerializeReferenceToIteratorClass(string iteratorClassName, ArrayBuilder<BlobWriter> customDebugInfo)
        {
            if (iteratorClassName == null) return;
            var cmw = new BlobWriter();
            cmw.WriteByte(CDI.CdiVersion);
            cmw.WriteByte(CDI.CdiKindForwardIterator);
            cmw.Align(4);
            uint length = 10 + (uint)iteratorClassName.Length * 2;
            if ((length & 3) != 0) length += 4 - (length & 3);
            cmw.WriteUint(length);
            cmw.WriteUTF16(iteratorClassName);
            cmw.WriteShort(0);
            cmw.Align(4);
            Debug.Assert(cmw.Position == length);
            customDebugInfo.Add(cmw);
        }

        private static void SerializeStateMachineLocalScopes(IMethodBody methodBody, ArrayBuilder<BlobWriter> customDebugInfo)
        {
            var scopes = methodBody.StateMachineHoistedLocalScopes;
            if (scopes.IsDefaultOrEmpty)
            {
                return;
            }

            uint numberOfScopes = (uint)scopes.Length;
            var cmw = new BlobWriter();
            cmw.WriteByte(CDI.CdiVersion);
            cmw.WriteByte(CDI.CdiKindStateMachineHoistedLocalScopes);
            cmw.Align(4);
            cmw.WriteUint(12 + numberOfScopes * 8);
            cmw.WriteUint(numberOfScopes);
            foreach (var scope in scopes)
            {
                if (scope.IsDefault)
                {
                    cmw.WriteUint(0);
                    cmw.WriteUint(0);
                }
                else
                {
                    // Dev12 C# emits end-inclusive range
                    cmw.WriteUint((uint)scope.StartOffset);
                    cmw.WriteUint((uint)scope.EndOffset - 1);
                }
            }

            customDebugInfo.Add(cmw);
        }

        private static void SerializeDynamicLocalInfo(IMethodBody methodBody, ArrayBuilder<BlobWriter> customDebugInfo)
        {
            if (!methodBody.HasDynamicLocalVariables)
            {
                return; //There are no dynamic locals
            }

            var dynamicLocals = ArrayBuilder<ILocalDefinition>.GetInstance();

            foreach (ILocalDefinition local in methodBody.LocalVariables)
            {
                if (local.IsDynamic)
                {
                    dynamicLocals.Add(local);
                }
            }

            int dynamicVariableCount = dynamicLocals.Count;

            foreach (var currentScope in methodBody.LocalScopes)
            {
                foreach (var localConstant in currentScope.Constants)
                {
                    if (localConstant.IsDynamic)
                    {
                        dynamicLocals.Add(localConstant);
                    }
                }
            }

            Debug.Assert(dynamicLocals.Any()); // There must be at least one dynamic local if this point is reached

            const int blobSize = 200;//DynamicAttribute - 64, DynamicAttributeLength - 4, SlotIndex -4, IdentifierName - 128
            var cmw = new BlobWriter();
            cmw.WriteByte(CDI.CdiVersion);
            cmw.WriteByte(CDI.CdiKindDynamicLocals);
            cmw.Align(4);
            // size = Version,Kind + size + cBuckets + (dynamicCount * sizeOf(Local Blob))
            cmw.WriteUint(4 + 4 + 4 + (uint)dynamicLocals.Count * blobSize);//Size of the Dynamic Block
            cmw.WriteUint((uint)dynamicLocals.Count);

            int localIndex = 0;
            foreach (ILocalDefinition local in dynamicLocals)
            {
                if (local.Name.Length > 63)//Ignore and push empty information
                {
                    cmw.WriteBytes(0, blobSize);
                    continue;
                }

                var dynamicTransformFlags = local.DynamicTransformFlags;
                if (!dynamicTransformFlags.IsDefault && dynamicTransformFlags.Length <= 64)
                {
                    byte[] flag = new byte[64];
                    for (int k = 0; k < dynamicTransformFlags.Length; k++)
                    {
                        if ((bool)dynamicTransformFlags[k].Value)
                        {
                            flag[k] = 1;
                        }
                    }
                    cmw.WriteBytes(flag); //Written Flag
                    cmw.WriteUint((uint)dynamicTransformFlags.Length); //Written Length
                }
                else
                {
                    cmw.WriteBytes(0, 68); //Empty flag array and size.
                }

                if (localIndex < dynamicVariableCount)
                {
                    // Dynamic variable
                    cmw.WriteUint((uint)local.SlotIndex);
                }
                else
                {
                    // Dynamic constant
                    cmw.WriteUint(0);
                }

                char[] localName = new char[64];
                local.Name.CopyTo(0, localName, 0, local.Name.Length);
                cmw.WriteUTF16(localName);

                localIndex++;
            }

            dynamicLocals.Free();
            customDebugInfo.Add(cmw);
        }

        // internal for testing
        internal static byte[] SerializeCustomDebugMetadata(ArrayBuilder<BlobWriter> recordWriters)
        {
            if (recordWriters.Count == 0)
            {
                return null;
            }

            BlobWriter cmw = BlobWriter.GetInstance();
            cmw.WriteByte(CDI.CdiVersion);
            cmw.WriteByte((byte)recordWriters.Count); // count
            cmw.Align(4);
            foreach (BlobWriter recordWriter in recordWriters)
            {
                recordWriter.WriteTo(cmw);
            }

            var result = cmw.ToArray();
            cmw.Free();
            return result;
        }

        private void SerializeNamespaceScopeMetadata(EmitContext context, IMethodBody methodBody, ArrayBuilder<BlobWriter> customDebugInfo)
        {
            if (context.Module.GenerateVisualBasicStylePdb)
            {
                return;
            }

            if (ShouldForwardToPreviousMethodWithUsingInfo(context, methodBody))
            {
                Debug.Assert(!ReferenceEquals(_previousMethodBodyWithUsingInfo, methodBody));
                SerializeReferenceToPreviousMethodWithUsingInfo(customDebugInfo);
                return;
            }

            List<ushort> usingCounts = new List<ushort>();
            var cmw = new BlobWriter();
            for (IImportScope scope = methodBody.ImportScope; scope != null; scope = scope.Parent)
            {
                usingCounts.Add((ushort)scope.GetUsedNamespaces().Length);
            }

            // ACASEY: This originally wrote (uint)12, (ushort)1, (ushort)0 in the
            // case where usingCounts was empty, but I'm not sure why.
            if (usingCounts.Count > 0)
            {
                uint streamLength;
                cmw.WriteByte(CDI.CdiVersion);
                cmw.WriteByte(CDI.CdiKindUsingInfo);
                cmw.Align(4);

                cmw.WriteUint(streamLength = BitArithmeticUtilities.Align((uint)usingCounts.Count * 2 + 10, 4));
                cmw.WriteUshort((ushort)usingCounts.Count);
                foreach (ushort uc in usingCounts)
                {
                    cmw.WriteUshort(uc);
                }

                cmw.Align(4);
                Debug.Assert(streamLength == cmw.Length);
                customDebugInfo.Add(cmw);
            }

            if (_methodBodyWithModuleInfo != null && !ReferenceEquals(_methodBodyWithModuleInfo, methodBody))
            {
                SerializeReferenceToMethodWithModuleInfo(customDebugInfo);
            }
        }

        private bool ShouldForwardToPreviousMethodWithUsingInfo(EmitContext context, IMethodBody methodBody)
        {
            if (_previousMethodBodyWithUsingInfo == null ||
                ReferenceEquals(_previousMethodBodyWithUsingInfo, methodBody))
            {
                return false;
            }

            // VB includes method namespace in namespace scopes:
            if (context.Module.GenerateVisualBasicStylePdb)
            {
                if (_pdbWriter.GetOrCreateSerializedNamespaceName(_previousMethodBodyWithUsingInfo.MethodDefinition.ContainingNamespace) !=
                    _pdbWriter.GetOrCreateSerializedNamespaceName(methodBody.MethodDefinition.ContainingNamespace))
                {
                    return false;
                }
            }

            var previousScopes = _previousMethodBodyWithUsingInfo.ImportScope;

            // methods share the same import scope (common case for methods declared in the same file)
            if (methodBody.ImportScope == previousScopes)
            {
                return true;
            }

            // If methods are in different files they don't share the same scopes,
            // but the imports might be the same nevertheless.
            // Note: not comparing project-level imports since those are the same for all method bodies.
            var s1 = methodBody.ImportScope;
            var s2 = previousScopes;
            while (s1 != null && s2 != null)
            {
                if (!s1.GetUsedNamespaces().SequenceEqual(s2.GetUsedNamespaces()))
                {
                    return false;
                }

                s1 = s1.Parent;
                s2 = s2.Parent;
            }

            return s1 == s2;
        }

        private void SerializeReferenceToMethodWithModuleInfo(ArrayBuilder<BlobWriter> customDebugInfo)
        {
            BlobWriter cmw = new BlobWriter(12);
            cmw.WriteByte(CDI.CdiVersion);
            cmw.WriteByte(CDI.CdiKindForwardToModuleInfo);
            cmw.Align(4);
            cmw.WriteUint(12);
            cmw.WriteUint(_methodTokenWithModuleInfo);
            customDebugInfo.Add(cmw);
        }

        private void SerializeReferenceToPreviousMethodWithUsingInfo(ArrayBuilder<BlobWriter> customDebugInfo)
        {
            BlobWriter cmw = new BlobWriter(12);
            cmw.WriteByte(CDI.CdiVersion);
            cmw.WriteByte(CDI.CdiKindForwardInfo);
            cmw.Align(4);
            cmw.WriteUint(12);
            cmw.WriteUint(_previousMethodTokenWithUsingInfo);
            customDebugInfo.Add(cmw);
        }
    }
}
