﻿// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using dnlib.DotNet;

namespace ICSharpCode.Decompiler.ILAst
{
	class YieldReturnDecompiler
	{
		// For a description on the code generated by the C# compiler for yield return:
		// http://csharpindepth.com/Articles/Chapter6/IteratorBlockImplementation.aspx
		
		// The idea here is:
		// - Figure out whether the current method is instanciating an enumerator
		// - Figure out which of the fields is the state field
		// - Construct an exception table based on states. This allows us to determine, for each state, what the parent try block is.
		
		// See http://community.sharpdevelop.net/blogs/danielgrunwald/archive/2011/03/06/ilspy-yield-return.aspx
		// for a description of this step.
		
		DecompilerContext context;
		TypeDef enumeratorType;
		MethodDef enumeratorCtor;
		MethodDef disposeMethod;
		FieldDef stateField;
		FieldDef currentField;
		Dictionary<FieldDef, ILVariable> fieldToParameterMap = new Dictionary<FieldDef, ILVariable>();
		List<ILNode> newBody;
		
		#region Run() method
		public static void Run(DecompilerContext context, ILBlock method, List<ILNode> list_ILNode, Func<ILBlock, ILInlining> getILInlining)
		{
			if (!context.Settings.YieldReturn)
				return; // abort if enumerator decompilation is disabled
			var yrd = new YieldReturnDecompiler();
			yrd.context = context;
			if (!yrd.MatchEnumeratorCreationPattern(method))
				return;
			yrd.enumeratorType = yrd.enumeratorCtor.DeclaringType;
			#if DEBUG && CRASH_IN_DEBUG_MODE
			if (Debugger.IsAttached) {
				yrd.Run();
			} else {
				#endif
				try {
					yrd.Run();
				} catch (SymbolicAnalysisFailedException) {
					return;
				}
				#if DEBUG && CRASH_IN_DEBUG_MODE
			}
			#endif
			method.Body.Clear();
			method.EntryGoto = null;
			method.Body.AddRange(yrd.newBody);//TODO: Make sure that the removed BinSpans from Clear() above is saved in the new body
			
			// Repeat the inlining/copy propagation optimization because the conversion of field access
			// to local variables can open up additional inlining possibilities.
			var inlining = getILInlining(method);
			inlining.InlineAllVariables();
			inlining.CopyPropagation(list_ILNode);
		}
		
		void Run()
		{
			AnalyzeCtor();
			AnalyzeCurrentProperty();
			ResolveIEnumerableIEnumeratorFieldMapping();
			ConstructExceptionTable();
			AnalyzeMoveNext();
			TranslateFieldsToLocalAccess();
		}
		#endregion
		
		#region Match the enumerator creation pattern
		bool MatchEnumeratorCreationPattern(ILBlock method)
		{
			if (method.Body.Count == 0)
				return false;
			ILExpression newObj;
			if (method.Body.Count == 1) {
				// ret(newobj(...))
				if (method.Body[0].Match(ILCode.Ret, out newObj))
					return MatchEnumeratorCreationNewObj(newObj, out enumeratorCtor);
				else
					return false;
			}
			// stloc(var_1, newobj(..)
			ILVariable var1;
			if (!method.Body[0].Match(ILCode.Stloc, out var1, out newObj))
				return false;
			if (!MatchEnumeratorCreationNewObj(newObj, out enumeratorCtor))
				return false;
			
			int i;
			for (i = 1; i < method.Body.Count; i++) {
				// stfld(..., ldloc(var_1), ldloc(parameter))
				IField storedField;
				ILExpression ldloc, loadParameter;
				if (!method.Body[i].Match(ILCode.Stfld, out storedField, out ldloc, out loadParameter))
					break;
				ILVariable loadedVar, loadedArg;
				if (!ldloc.Match(ILCode.Ldloc, out loadedVar) || !loadParameter.Match(ILCode.Ldloc, out loadedArg))
					return false;
				storedField = GetFieldDefinition(storedField);
				if (loadedVar != var1 || storedField == null || !loadedArg.IsParameter)
					return false;
				fieldToParameterMap[(FieldDef)storedField] = loadedArg;
			}
			ILVariable var2;
			ILExpression ldlocForStloc2;
			if (i < method.Body.Count && method.Body[i].Match(ILCode.Stloc, out var2, out ldlocForStloc2)) {
				// stloc(var_2, ldloc(var_1))
				if (ldlocForStloc2.Code != ILCode.Ldloc || ldlocForStloc2.Operand != var1)
					return false;
				i++;
			} else {
				// the compiler might skip the above instruction in release builds; in that case, it directly returns stloc.Operand
				var2 = var1;
			}
			ILExpression retArg;
			if (i < method.Body.Count && method.Body[i].Match(ILCode.Ret, out retArg)) {
				// ret(ldloc(var_2))
				if (retArg.Code == ILCode.Ldloc && retArg.Operand == var2) {
					return true;
				}
			}
			return false;
		}
		
		static FieldDef GetFieldDefinition(IField field)
		{
			return DnlibExtensions.ResolveFieldWithinSameModule(field);
		}
		
		static MethodDef GetMethodDefinition(IMethod method)
		{
			return DnlibExtensions.ResolveMethodWithinSameModule(method);
		}
		
		bool MatchEnumeratorCreationNewObj(ILExpression expr, out MethodDef ctor)
		{
			// newobj(CurrentType/...::.ctor, ldc.i4(-2))
			ctor = null;
			if (expr.Code != ILCode.Newobj || expr.Arguments.Count != 1)
				return false;
			if (expr.Arguments[0].Code != ILCode.Ldc_I4)
				return false;
			int initialState = (int)expr.Arguments[0].Operand;
			if (!(initialState == -2 || initialState == 0))
				return false;
			ctor = GetMethodDefinition(expr.Operand as IMethod);
			if (ctor == null || ctor.DeclaringType.DeclaringType != context.CurrentType)
				return false;
			return IsCompilerGeneratorEnumerator(ctor.DeclaringType);
		}
		
		public static bool IsCompilerGeneratorEnumerator(TypeDef type)
		{
			if (!(type.DeclaringType != null && type.IsCompilerGenerated()))
				return false;
			foreach (var i in type.Interfaces) {
				if (i.Interface == null)
					continue;
				if (i.Interface.Name == "IEnumerator" && i.Interface.Namespace == "System.Collections")
					return true;
			}
			return false;
		}
		#endregion
		
		#region Figure out what the 'state' field is (analysis of .ctor())
		/// <summary>
		/// Looks at the enumerator's ctor and figures out which of the fields holds the state.
		/// </summary>
		void AnalyzeCtor()
		{
			ILBlock method = CreateILAst(enumeratorCtor);
			
			foreach (ILNode node in method.Body) {
				IField field;
				ILExpression instExpr;
				ILExpression stExpr;
				ILVariable arg;
				if (node.Match(ILCode.Stfld, out field, out instExpr, out stExpr) &&
				    instExpr.MatchThis() &&
				    stExpr.Match(ILCode.Ldloc, out arg) &&
				    arg.IsParameter && arg.OriginalParameter.MethodSigIndex == 0)
				{
					stateField = GetFieldDefinition(field);
				}
			}
			if (stateField == null)
				throw new SymbolicAnalysisFailedException();
		}
		
		/// <summary>
		/// Creates ILAst for the specified method, optimized up to before the 'YieldReturn' step.
		/// </summary>
		ILBlock CreateILAst(MethodDef method)
		{
			if (method == null || !method.HasBody)
				throw new SymbolicAnalysisFailedException();
			
			ILBlock ilMethod = new ILBlock();

			var astBuilder = context.Cache.GetILAstBuilder();
			try {
				ilMethod.Body = astBuilder.Build(method, true, context);
			}
			finally {
				context.Cache.Return(astBuilder);
			}

			var optimizer = this.context.Cache.GetILAstOptimizer();
			try {
				optimizer.Optimize(context, ilMethod, ILAstOptimizationStep.YieldReturn);
			}
			finally {
				this.context.Cache.Return(optimizer);
			}

			return ilMethod;
		}
		#endregion
		
		#region Figure out what the 'current' field is (analysis of get_Current())
		/// <summary>
		/// Looks at the enumerator's get_Current method and figures out which of the fields holds the current value.
		/// </summary>
		void AnalyzeCurrentProperty()
		{
			MethodDef getCurrentMethod = enumeratorType.Methods.FirstOrDefault(
				m => m.Name.StartsWith("System.Collections.Generic.IEnumerator", StringComparison.Ordinal)
				&& m.Name.EndsWith(".get_Current", StringComparison.Ordinal));
			ILBlock method = CreateILAst(getCurrentMethod);
			if (method.Body.Count == 1) {
				// release builds directly return the current field
				ILExpression retExpr;
				IField field;
				ILExpression ldFromObj;
				if (method.Body[0].Match(ILCode.Ret, out retExpr) &&
				    retExpr.Match(ILCode.Ldfld, out field, out ldFromObj) &&
				    ldFromObj.MatchThis())
				{
					currentField = GetFieldDefinition(field);
				}
			} else if (method.Body.Count == 2) {
				ILVariable v, v2;
				ILExpression stExpr;
				IField field;
				ILExpression ldFromObj;
				ILExpression retExpr;
				if (method.Body[0].Match(ILCode.Stloc, out v, out stExpr) &&
				    stExpr.Match(ILCode.Ldfld, out field, out ldFromObj) &&
				    ldFromObj.MatchThis() &&
				    method.Body[1].Match(ILCode.Ret, out retExpr) &&
				    retExpr.Match(ILCode.Ldloc, out v2) &&
				    v == v2)
				{
					currentField = GetFieldDefinition(field);
				}
			}
			if (currentField == null)
				throw new SymbolicAnalysisFailedException();
		}
		#endregion
		
		#region Figure out the mapping of IEnumerable fields to IEnumerator fields  (analysis of GetEnumerator())
		void ResolveIEnumerableIEnumeratorFieldMapping()
		{
			MethodDef getEnumeratorMethod = enumeratorType.Methods.FirstOrDefault(
				m => m.Name.StartsWith("System.Collections.Generic.IEnumerable", StringComparison.Ordinal)
				&& m.Name.EndsWith(".GetEnumerator", StringComparison.Ordinal));
			if (getEnumeratorMethod == null)
				return; // no mappings (maybe it's just an IEnumerator implementation?)
			
			ILBlock method = CreateILAst(getEnumeratorMethod);
			foreach (ILNode node in method.Body) {
				IField stField;
				ILExpression stToObj;
				ILExpression stExpr;
				IField ldField;
				ILExpression ldFromObj;
				if (node.Match(ILCode.Stfld, out stField, out stToObj, out stExpr) &&
				    stExpr.Match(ILCode.Ldfld, out ldField, out ldFromObj) &&
				    ldFromObj.MatchThis())
				{
					FieldDef storedField = GetFieldDefinition(stField);
					FieldDef loadedField = GetFieldDefinition(ldField);
					if (storedField != null && loadedField != null) {
						ILVariable mappedParameter;
						if (fieldToParameterMap.TryGetValue(loadedField, out mappedParameter))
							fieldToParameterMap[storedField] = mappedParameter;
					}
				}
			}
		}
		#endregion
		
		#region Construction of the exception table (analysis of Dispose())
		// We construct the exception table by analyzing the enumerator's Dispose() method.
		
		// Assumption: there are no loops/backward jumps
		// We 'run' the code, with "state" being a symbolic variable
		// so it can form expressions like "state + x" (when there's a sub instruction)
		// For each instruction, we maintain a list of value ranges for state for which the instruction is reachable.
		// This is (int.MinValue, int.MaxValue) for the first instruction.
		// These ranges are propagated depending on the conditional jumps performed by the code.
		
		Dictionary<MethodDef, StateRange> finallyMethodToStateRange;
		
		void ConstructExceptionTable()
		{
			disposeMethod = enumeratorType.Methods.FirstOrDefault(m => m.Name == "System.IDisposable.Dispose");
			ILBlock ilMethod = CreateILAst(disposeMethod);
			
			var rangeAnalysis = new StateRangeAnalysis(ilMethod.Body[0], StateRangeAnalysisMode.IteratorDispose, stateField);
			rangeAnalysis.AssignStateRanges(ilMethod.Body, ilMethod.Body.Count);
			finallyMethodToStateRange = rangeAnalysis.finallyMethodToStateRange;
			
			// Now look at the finally blocks:
			foreach (var tryFinally in ilMethod.GetSelfAndChildrenRecursive<ILTryCatchBlock>()) {
				var range = rangeAnalysis.ranges[tryFinally.TryBlock.Body[0]];
				var finallyBody = tryFinally.FinallyBlock.Body;
				if (finallyBody.Count != 2)
					throw new SymbolicAnalysisFailedException();
				ILExpression call = finallyBody[0] as ILExpression;
				if (call == null || call.Code != ILCode.Call || call.Arguments.Count != 1)
					throw new SymbolicAnalysisFailedException();
				if (!call.Arguments[0].MatchThis())
					throw new SymbolicAnalysisFailedException();
				if (!finallyBody[1].Match(ILCode.Endfinally))
					throw new SymbolicAnalysisFailedException();
				
				MethodDef mdef = GetMethodDefinition(call.Operand as IMethod);
				if (mdef == null || finallyMethodToStateRange.ContainsKey(mdef))
					throw new SymbolicAnalysisFailedException();
				finallyMethodToStateRange.Add(mdef, range);
			}
			rangeAnalysis = null;
		}
		#endregion
		
		#region Analysis of MoveNext()
		ILVariable returnVariable;
		ILLabel returnLabel;
		ILLabel returnFalseLabel;
		
		void AnalyzeMoveNext()
		{
			MethodDef moveNextMethod = enumeratorType.Methods.FirstOrDefault(m => m.Name == "MoveNext");
			ILBlock ilMethod = CreateILAst(moveNextMethod);
			
			if (ilMethod.Body.Count == 0)
				throw new SymbolicAnalysisFailedException();
			ILExpression lastReturnArg;
			if (!ilMethod.Body.Last().Match(ILCode.Ret, out lastReturnArg))
				throw new SymbolicAnalysisFailedException();
			
			// There are two possibilities:
			if (lastReturnArg.Code == ILCode.Ldloc) {
				// a) the compiler uses a variable for returns (in debug builds, or when there are try-finally blocks)
				returnVariable = (ILVariable)lastReturnArg.Operand;
				returnLabel = ilMethod.Body.ElementAtOrDefault(ilMethod.Body.Count - 2) as ILLabel;
				if (returnLabel == null)
					throw new SymbolicAnalysisFailedException();
			} else {
				// b) the compiler directly returns constants
				returnVariable = null;
				returnLabel = null;
				// In this case, the last return must return false.
				if (lastReturnArg.Code != ILCode.Ldc_I4 || (int)lastReturnArg.Operand != 0)
					throw new SymbolicAnalysisFailedException();
			}
			
			ILTryCatchBlock tryFaultBlock = ilMethod.Body[0] as ILTryCatchBlock;
			List<ILNode> body;
			int bodyLength;
			if (tryFaultBlock != null) {
				// there are try-finally blocks
				if (returnVariable == null) // in this case, we must use a return variable
					throw new SymbolicAnalysisFailedException();
				// must be a try-fault block:
				if (tryFaultBlock.CatchBlocks.Count != 0 || tryFaultBlock.FinallyBlock != null || tryFaultBlock.FaultBlock == null)
					throw new SymbolicAnalysisFailedException();
				
				ILBlock faultBlock = tryFaultBlock.FaultBlock;
				// Ensure the fault block contains the call to Dispose().
				if (faultBlock.Body.Count != 2)
					throw new SymbolicAnalysisFailedException();
				IMethod disposeMethodRef;
				ILExpression disposeArg;
				if (!faultBlock.Body[0].Match(ILCode.Call, out disposeMethodRef, out disposeArg))
					throw new SymbolicAnalysisFailedException();
				if (GetMethodDefinition(disposeMethodRef) != disposeMethod || !disposeArg.MatchThis())
					throw new SymbolicAnalysisFailedException();
				if (!faultBlock.Body[1].Match(ILCode.Endfinally))
					throw new SymbolicAnalysisFailedException();
				
				body = tryFaultBlock.TryBlock.Body;
				bodyLength = body.Count;
			} else {
				// no try-finally blocks
				body = ilMethod.Body;
				if (returnVariable == null)
					bodyLength = body.Count - 1; // all except for the return statement
				else
					bodyLength = body.Count - 2; // all except for the return label and statement
			}
			
			// Now verify that the last instruction in the body is 'ret(false)'
			if (returnVariable != null) {
				// If we don't have a return variable, we already verified that above.
				// If we do have one, check for 'stloc(returnVariable, ldc.i4(0))'
				
				// Maybe might be a jump to the return label after the stloc:
				ILExpression leave = body.ElementAtOrDefault(bodyLength - 1) as ILExpression;
				if (leave != null && (leave.Code == ILCode.Br || leave.Code == ILCode.Leave) && leave.Operand == returnLabel)
					bodyLength--;
				ILExpression store0 = body.ElementAtOrDefault(bodyLength - 1) as ILExpression;
				if (store0 == null || store0.Code != ILCode.Stloc || store0.Operand != returnVariable)
					throw new SymbolicAnalysisFailedException();
				if (store0.Arguments[0].Code != ILCode.Ldc_I4 || (int)store0.Arguments[0].Operand != 0)
					throw new SymbolicAnalysisFailedException();
				
				bodyLength--; // don't conside the stloc instruction to be part of the body
			}
			// The last element in the body usually is a label pointing to the 'ret(false)'
			returnFalseLabel = body.ElementAtOrDefault(bodyLength - 1) as ILLabel;
			// Note: in Roslyn-compiled code, returnFalseLabel may be null.
			
			var rangeAnalysis = new StateRangeAnalysis(body[0], StateRangeAnalysisMode.IteratorMoveNext, stateField);
			int pos = rangeAnalysis.AssignStateRanges(body, bodyLength);
			rangeAnalysis.EnsureLabelAtPos(body, ref pos, ref bodyLength);
			
			var labels = rangeAnalysis.CreateLabelRangeMapping(body, pos, bodyLength);
			ConvertBody(body, pos, bodyLength, labels);
		}
		#endregion
		
		#region ConvertBody
		struct SetState
		{
			public readonly int NewBodyPos;
			public readonly int NewState;
			
			public SetState(int newBodyPos, int newState)
			{
				this.NewBodyPos = newBodyPos;
				this.NewState = newState;
			}
		}
		
		void ConvertBody(List<ILNode> body, int startPos, int bodyLength, List<KeyValuePair<ILLabel, StateRange>> labels)
		{
			newBody = new List<ILNode>();
			newBody.Add(MakeGoTo(labels, 0));
			List<SetState> stateChanges = new List<SetState>();
			int currentState = -1;
			// Copy all instructions from the old body to newBody.
			for (int pos = startPos; pos < bodyLength; pos++) {
				ILExpression expr = body[pos] as ILExpression;
				if (expr != null && expr.Code == ILCode.Stfld && expr.Arguments[0].MatchThis()) {
					// Handle stores to 'state' or 'current'
					FieldDef field;
					if ((field = GetFieldDefinition(expr.Operand as IField)) == stateField) {
						if (expr.Arguments[1].Code != ILCode.Ldc_I4)
							throw new SymbolicAnalysisFailedException();
						currentState = (int)expr.Arguments[1].Operand;
						stateChanges.Add(new SetState(newBody.Count, currentState));
					} else if (field == currentField) {
						newBody.Add(new ILExpression(ILCode.YieldReturn, null, expr.Arguments[1]));
					} else {
						newBody.Add(body[pos]);
					}
				} else if (returnVariable != null && expr != null && expr.Code == ILCode.Stloc && expr.Operand == returnVariable) {
					// handle store+branch to the returnVariable
					ILExpression br = body.ElementAtOrDefault(++pos) as ILExpression;
					if (br == null || !(br.Code == ILCode.Br || br.Code == ILCode.Leave) || br.Operand != returnLabel || expr.Arguments[0].Code != ILCode.Ldc_I4)
						throw new SymbolicAnalysisFailedException();
					int val = (int)expr.Arguments[0].Operand;
					if (val == 0) {
						newBody.Add(new ILExpression(ILCode.YieldBreak, null));
					} else if (val == 1) {
						newBody.Add(MakeGoTo(labels, currentState));
					} else {
						throw new SymbolicAnalysisFailedException();
					}
				} else if (expr != null && expr.Code == ILCode.Ret) {
					if (expr.Arguments.Count != 1 || expr.Arguments[0].Code != ILCode.Ldc_I4)
						throw new SymbolicAnalysisFailedException();
					// handle direct return (e.g. in release builds)
					int val = (int)expr.Arguments[0].Operand;
					if (val == 0) {
						newBody.Add(new ILExpression(ILCode.YieldBreak, null));
					} else if (val == 1) {
						newBody.Add(MakeGoTo(labels, currentState));
					} else {
						throw new SymbolicAnalysisFailedException();
					}
				} else if (expr != null && expr.Code == ILCode.Call && expr.Arguments.Count == 1 && expr.Arguments[0].MatchThis()) {
					MethodDef method = GetMethodDefinition(expr.Operand as IMethod);
					if (method == null)
						throw new SymbolicAnalysisFailedException();
					StateRange stateRange;
					if (method == disposeMethod) {
						// Explicit call to dispose is used for "yield break;" within the method.
						ILExpression br = body.ElementAtOrDefault(++pos) as ILExpression;
						if (br == null || !(br.Code == ILCode.Br || br.Code == ILCode.Leave) || br.Operand != returnFalseLabel)
							throw new SymbolicAnalysisFailedException();
						newBody.Add(new ILExpression(ILCode.YieldBreak, null));
					} else if (finallyMethodToStateRange.TryGetValue(method, out stateRange)) {
						// Call to Finally-method
						int index = stateChanges.FindIndex(ss => stateRange.Contains(ss.NewState));
						if (index < 0)
							throw new SymbolicAnalysisFailedException();
						
						ILLabel label = new ILLabel();
						label.Name = "JumpOutOfTryFinally" + stateChanges[index].NewState.ToString();
						newBody.Add(new ILExpression(ILCode.Leave, label));
						
						SetState stateChange = stateChanges[index];
						// Move all instructions from stateChange.Pos to newBody.Count into a try-block
						stateChanges.RemoveRange(index, stateChanges.Count - index); // remove all state changes up to the one we found
						ILTryCatchBlock tryFinally = new ILTryCatchBlock();
						tryFinally.TryBlock = new ILBlock(newBody.GetRange(stateChange.NewBodyPos, newBody.Count - stateChange.NewBodyPos));
						newBody.RemoveRange(stateChange.NewBodyPos, newBody.Count - stateChange.NewBodyPos); // remove all nodes that we just moved into the try block
						tryFinally.CatchBlocks = new List<ILTryCatchBlock.CatchBlock>();
						tryFinally.FinallyBlock = ConvertFinallyBlock(method);
						newBody.Add(tryFinally);
						newBody.Add(label);
					}
				} else {
					newBody.Add(body[pos]);
				}
			}
			newBody.Add(new ILExpression(ILCode.YieldBreak, null));
		}
		
		ILExpression MakeGoTo(ILLabel targetLabel)
		{
			Debug.Assert(targetLabel != null);
			if (targetLabel == returnFalseLabel)
				return new ILExpression(ILCode.YieldBreak, null);
			else
				return new ILExpression(ILCode.Br, targetLabel);
		}
		
		ILExpression MakeGoTo(List<KeyValuePair<ILLabel, StateRange>> labels, int state)
		{
			foreach (var pair in labels) {
				if (pair.Value.Contains(state))
					return MakeGoTo(pair.Key);
			}
			throw new SymbolicAnalysisFailedException();
		}
		
		ILBlock ConvertFinallyBlock(MethodDef finallyMethod)
		{
			ILBlock block = CreateILAst(finallyMethod);
			// Get rid of assignment to state
			IField stfld;
			List<ILExpression> args;
			if (block.Body.Count > 0 && block.Body[0].Match(ILCode.Stfld, out stfld, out args)) {
				if (GetFieldDefinition(stfld) == stateField && args[0].MatchThis())
					block.Body.RemoveAt(0);//TODO: Save BinSpans?
			}
			// Convert ret to endfinally
			foreach (ILExpression expr in block.GetSelfAndChildrenRecursive<ILExpression>()) {
				if (expr.Code == ILCode.Ret)
					expr.Code = ILCode.Endfinally;
			}
			return block;
		}
		#endregion
		
		#region TranslateFieldsToLocalAccess
		void TranslateFieldsToLocalAccess()
		{
			TranslateFieldsToLocalAccess(newBody, fieldToParameterMap);
		}
		
		internal static void TranslateFieldsToLocalAccess(List<ILNode> newBody, Dictionary<FieldDef, ILVariable> fieldToParameterMap)
		{
			var fieldToLocalMap = new DefaultDictionary<FieldDef, ILVariable>(f => new ILVariable { Name = f.Name.String, Type = f.FieldType });
			List<ILExpression> listExpr = null;
			foreach (ILNode node in newBody) {
				foreach (ILExpression expr in node.GetSelfAndChildrenRecursive<ILExpression>(listExpr ?? (listExpr = new List<ILExpression>()))) {
					FieldDef field = GetFieldDefinition(expr.Operand as IField);
					if (field != null) {
						switch (expr.Code) {
							case ILCode.Ldfld:
								if (expr.Arguments[0].MatchThis()) {
									expr.Code = ILCode.Ldloc;
									if (fieldToParameterMap.ContainsKey(field)) {
										expr.Operand = fieldToParameterMap[field];
									} else {
										expr.Operand = fieldToLocalMap[field];
									}
									expr.Arguments.Clear();//TODO: Save BinSpans?
								}
								break;
							case ILCode.Stfld:
								if (expr.Arguments[0].MatchThis()) {
									expr.Code = ILCode.Stloc;
									if (fieldToParameterMap.ContainsKey(field)) {
										expr.Operand = fieldToParameterMap[field];
									} else {
										expr.Operand = fieldToLocalMap[field];
									}
									expr.Arguments.RemoveAt(0);//TODO: Save BinSpans?
								}
								break;
							case ILCode.Ldflda:
								if (expr.Arguments[0].MatchThis()) {
									expr.Code = ILCode.Ldloca;
									if (fieldToParameterMap.ContainsKey(field)) {
										expr.Operand = fieldToParameterMap[field];
									} else {
										expr.Operand = fieldToLocalMap[field];
									}
									expr.Arguments.Clear();//TODO: Save BinSpans?
								}
								break;
						}
					}
				}
			}
		}
		#endregion
	}
}
