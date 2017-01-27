// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
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
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;

namespace ICSharpCode.Decompiler.ILAst {
	/// <summary>
	/// Converts stack-based bytecode to variable-based bytecode by calculating use-define chains
	/// </summary>
	public class ILAstBuilder
	{
		/// <summary> Immutable </summary>
		struct StackSlot
		{
			public readonly ByteCode[] Definitions;  // Reaching definitions of this stack slot
			public readonly ILVariable LoadFrom;     // Variable used for storage of the value
			
			public StackSlot(ByteCode[] definitions, ILVariable loadFrom)
			{
				this.Definitions = definitions;
				this.LoadFrom = loadFrom;
			}
			
			public static StackSlot[] ModifyStack(StackSlot[] stack, int popCount, int pushCount, ByteCode pushDefinition)
			{
				StackSlot[] newStack = new StackSlot[stack.Length - popCount + pushCount];
				Array.Copy(stack, newStack, stack.Length - popCount);
				for (int i = stack.Length - popCount; i < newStack.Length; i++) {
					newStack[i] = new StackSlot(new [] { pushDefinition }, null);
				}
				return newStack;
			}
		}
		
		/// <summary> Immutable </summary>
		struct VariableSlot
		{
			public readonly ByteCode[] Definitions;       // Reaching deinitions of this variable
			public readonly bool       UnknownDefinition; // Used for initial state and exceptional control flow
			
			static readonly VariableSlot UnknownInstance = new VariableSlot(Array.Empty<ByteCode>(), true);

			public VariableSlot(ByteCode[] definitions, bool unknownDefinition)
			{
				this.Definitions = definitions;
				this.UnknownDefinition = unknownDefinition;
			}
			
			public static VariableSlot[] CloneVariableState(VariableSlot[] state)
			{
				if (state.Length == 0)
					return state;
				VariableSlot[] clone = new VariableSlot[state.Length];
				Array.Copy(state, clone, state.Length);
				return clone;
			}
			
			public static VariableSlot[] MakeUknownState(int varCount)
			{
				if (varCount == 0)
					return Array.Empty<VariableSlot>();
				VariableSlot[] unknownVariableState = new VariableSlot[varCount];
				for (int i = 0; i < unknownVariableState.Length; i++) {
					unknownVariableState[i] = UnknownInstance;
				}
				return unknownVariableState;
			}
		}
		
		sealed class ByteCode
		{
			public ILLabel  Label;      // Non-null only if needed
			public uint     Offset;
			public uint     EndOffset;
			public ILCode   Code;
			public object   Operand;
			public int      PopCount;   // -1 means pop all
			public int      PushCount;
			public string   Name { get { return "IL_" + this.Offset.ToString("X2"); } }
			public ByteCode Next;
			public Instruction[]    Prefixes;        // Non-null only if needed
			public StackSlot[]      StackBefore;     // Unique per bytecode; not shared
			public VariableSlot[]   VariablesBefore; // Unique per bytecode; not shared
			public List<ILVariable> StoreTo;         // Store result of instruction to those AST variables
			
			public bool IsVariableDefinition {
				get {
					return (this.Code == ILCode.Stloc) || (this.Code == ILCode.Ldloca && this.Next != null && this.Next.Code == ILCode.Initobj);
				}
			}
			
			public override string ToString()
			{
				StringBuilder sb = new StringBuilder();
				
				// Label
				sb.Append(this.Name);
				sb.Append(':');
				if (this.Label != null)
					sb.Append('*');
				
				// Name
				sb.Append(' ');
				if (this.Prefixes != null) {
					foreach (var prefix in this.Prefixes) {
						sb.Append(prefix.OpCode.Name);
						sb.Append(' ');
					}
				}
				sb.Append(this.Code.GetName());
				
				if (this.Operand != null) {
					sb.Append(' ');
					if (this.Operand is Instruction) {
						sb.Append("IL_" + ((Instruction)this.Operand).Offset.ToString("X2"));
					} else if (this.Operand is IList<Instruction>) {
						foreach(Instruction inst in (IList<Instruction>)this.Operand) {
							if (inst == null)
								continue;
							sb.Append("IL_" + inst.Offset.ToString("X2"));
							sb.Append(" ");
						}
					} else if (this.Operand is ILLabel) {
						sb.Append(((ILLabel)this.Operand).Name);
					} else if (this.Operand is ILLabel[]) {
						foreach(ILLabel label in (ILLabel[])this.Operand) {
							sb.Append(label.Name);
							sb.Append(" ");
						}
					} else {
						sb.Append(this.Operand.ToString());
					}
				}
				
				if (this.StackBefore != null) {
					sb.Append(" StackBefore={");
					bool first = true;
					foreach (StackSlot slot in this.StackBefore) {
						if (!first) sb.Append(",");
						bool first2 = true;
						foreach(ByteCode defs in slot.Definitions) {
							if (!first2) sb.Append("|");
							sb.AppendFormat("IL_{0:X2}", defs.Offset);
							first2 = false;
						}
						first = false;
					}
					sb.Append("}");
				}
				
				if (this.StoreTo != null && this.StoreTo.Count > 0) {
					sb.Append(" StoreTo={");
					bool first = true;
					foreach (ILVariable stackVar in this.StoreTo) {
						if (!first) sb.Append(",");
						sb.Append(stackVar.Name);
						first = false;
					}
					sb.Append("}");
				}
				
				if (this.VariablesBefore != null) {
					sb.Append(" VarsBefore={");
					bool first = true;
					foreach (VariableSlot varSlot in this.VariablesBefore) {
						if (!first) sb.Append(",");
						if (varSlot.UnknownDefinition) {
							sb.Append("?");
						} else {
							bool first2 = true;
							foreach (ByteCode storedBy in varSlot.Definitions) {
								if (!first2) sb.Append("|");
								sb.AppendFormat("IL_{0:X2}", storedBy.Offset);
								first2 = false;
							}
						}
						first = false;
					}
					sb.Append("}");
				}
				
				return sb.ToString();
			}
		}
		
		MethodDef methodDef;
		bool optimize;
		
		// Virtual instructions to load exception on stack
		readonly Dictionary<ExceptionHandler, ByteCode> ldexceptions = new Dictionary<ExceptionHandler, ILAstBuilder.ByteCode>();
		readonly Dictionary<ExceptionHandler, ByteCode> ldfilters = new Dictionary<ExceptionHandler, ILAstBuilder.ByteCode>();

		public List<ILVariable> Parameters = new List<ILVariable>();
		DecompilerContext context;
		readonly Dictionary<Instruction, ByteCode> instrToByteCode = new Dictionary<Instruction, ByteCode>();
		readonly List<ByteCode> StackAnalysis_body = new List<ByteCode>();
		readonly List<ILLabel> StackAnalysis_List_ILLabel = new List<ILLabel>();
		readonly Dictionary<ILVariable, bool> StackAnalysis_Dict_ILVariable_Boolean = new Dictionary<ILVariable, bool>();
		readonly HashSet<ByteCode> StackAnalysis_HashSet_ByteCode = new HashSet<ByteCode>();
		readonly List<Instruction> StackAnalysis_cachedPrefixes = new List<Instruction>(1);
		readonly Dictionary<ILVariable, StackSlot?> StackAnalysis_ILVariable_StackSlot_dict = new Dictionary<ILVariable, StackSlot?>();
		readonly HashSet<ILVariable> StackAnalysis_ILVariables_hash = new HashSet<ILVariable>();

		static readonly Dictionary<Code, ILCode> ilCodeTranslation =
			OpCodes.OneByteOpCodes
			.Concat(OpCodes.TwoByteOpCodes)
			.GroupBy(opCode => opCode.Code)
			.Select(group => group.First())
			.Select(opCode => new
			{
				Code = opCode.Code,
				ILCode = opCode.OpCodeType == OpCodeType.Nternal ?
					ILCode.Nop :
					(ILCode)Enum.Parse(typeof(ILCode), opCode.Code.ToString())
			})
			.ToDictionary(translation => translation.Code, translation => translation.ILCode);

		public ILAstBuilder()
		{
		}

		public void Reset()
		{
			methodDef = null;
			optimize = false;
			ldexceptions.Clear();
			ldfilters.Clear();
			Parameters.Clear();
			context = null;
			instrToByteCode.Clear();
			StackAnalysis_List_ILLabel.Clear();
			StackAnalysis_Dict_ILVariable_Boolean.Clear();
			StackAnalysis_HashSet_ByteCode.Clear();
			StackAnalysis_cachedPrefixes.Clear();
			StackAnalysis_ILVariable_StackSlot_dict.Clear();
			StackAnalysis_ILVariables_hash.Clear();
			nullByteCodeDummy.Next = null;
		}
		
		public List<ILNode> Build(MethodDef methodDef, bool optimize, DecompilerContext context)
		{
			this.methodDef = methodDef;
			this.optimize = optimize;
			this.context = context;
			
			if (methodDef.Body.Instructions.Count == 0) return new List<ILNode>();
			
			List<ByteCode> body = StackAnalysis(methodDef);
			
			List<ILNode> ast = ConvertToAst(body, new HashSet<ExceptionHandler>(methodDef.Body.ExceptionHandlers));
			
			return ast;
		}

		readonly ByteCode nullByteCodeDummy = new ByteCode();
		List<ByteCode> StackAnalysis(MethodDef methodDef)
		{
			instrToByteCode.Clear();

			// Create temporary structure for the stack analysis
			StackAnalysis_body.Clear();
			List<Instruction> prefixes = null;
			var instructions = methodDef.Body.Instructions;
			int instructionsCount = instructions.Count;
			var inst = 0 < instructionsCount ? instructions[0] : null;
			var prevByteCode = nullByteCodeDummy;
			for (int i = 0; i < instructionsCount; i++) {
				var next = i + 1 < instructionsCount ? instructions[i + 1] : null;
				if (inst.OpCode.OpCodeType == OpCodeType.Prefix) {
					if (prefixes == null)
						prefixes = StackAnalysis_cachedPrefixes;
					prefixes.Add(inst);
					inst = next;
					continue;
				}
				ILCode code = ilCodeTranslation[inst.OpCode.Code];
				object operand = inst.Operand;
				ILCodeUtil.ExpandMacro(ref code, ref operand, methodDef);
				ByteCode byteCode = new ByteCode() {
					Offset      = inst.Offset,
					EndOffset   = next?.Offset ?? (uint)methodDef.Body.GetCodeSize(),
					Code        = code,
					Operand     = operand,
					PopCount    = inst.GetPopDelta(methodDef),
					PushCount   = inst.GetPushDelta(methodDef)
				};
				if (prefixes != null) {
					instrToByteCode[prefixes[0]] = byteCode;
					byteCode.Offset = prefixes[0].Offset;
					byteCode.Prefixes = prefixes.ToArray();
					prefixes = null;
					StackAnalysis_cachedPrefixes.Clear();
				} else {
					instrToByteCode[inst] = byteCode;
				}
				StackAnalysis_body.Add(byteCode);
				prevByteCode.Next = byteCode;
				prevByteCode = byteCode;
				inst = next;
			}
			
			Stack<ByteCode> agenda = new Stack<ByteCode>();
			
			int varCount = methodDef.Body.Variables.Count;
			
			var exceptionHandlerStarts = new HashSet<ByteCode>(methodDef.Body.ExceptionHandlers.Select(eh => eh.HandlerStart == null ? null : instrToByteCode[eh.HandlerStart]));
			exceptionHandlerStarts.Remove(null);

			// HACK: Some MS reference assemblies contain just a RET instruction. If the method
			// returns a value, the code below will eventually throw an exception in
			// StackSlot.ModifyStack().
			if (StackAnalysis_body.Count == 1 && StackAnalysis_body[0].Code == ILCode.Ret)
				StackAnalysis_body[0].PopCount = 0;

			// Add known states
			if(methodDef.Body.HasExceptionHandlers) {
				foreach(ExceptionHandler ex in methodDef.Body.ExceptionHandlers) {
					if (ex.HandlerStart == null)
						continue;
					ByteCode handlerStart = instrToByteCode[ex.HandlerStart];
					handlerStart.StackBefore = Array.Empty<StackSlot>();
					handlerStart.VariablesBefore = VariableSlot.MakeUknownState(varCount);
					if (ex.HandlerType == ExceptionHandlerType.Catch || ex.HandlerType == ExceptionHandlerType.Filter) {
						// Catch and Filter handlers start with the exeption on the stack
						ByteCode ldexception = new ByteCode() {
							Code = ILCode.Ldexception,
							Operand = ex.CatchType,
							PopCount = 0,
							PushCount = 1
						};
						ldexceptions[ex] = ldexception;
						handlerStart.StackBefore = new StackSlot[] { new StackSlot(new [] { ldexception }, null) };
					}
					agenda.Push(handlerStart);
					
					if (ex.HandlerType == ExceptionHandlerType.Filter && ex.FilterStart != null) {
						ByteCode filterStart = instrToByteCode[ex.FilterStart];
						ByteCode ldexception = new ByteCode() {
							Code = ILCode.Ldexception,
							Operand = ex.CatchType,
							PopCount = 0,
							PushCount = 1
						};
						ldfilters[ex] = ldexception;
						filterStart.StackBefore = new StackSlot[] { new StackSlot(new [] { ldexception }, null) };
						filterStart.VariablesBefore = VariableSlot.MakeUknownState(varCount);
						agenda.Push(filterStart);
					}
				}
			}
			
			StackAnalysis_body[0].StackBefore = Array.Empty<StackSlot>();
			StackAnalysis_body[0].VariablesBefore = VariableSlot.MakeUknownState(varCount);
			agenda.Push(StackAnalysis_body[0]);
			
			// Process agenda
			while(agenda.Count > 0) {
				context.CancellationToken.ThrowIfCancellationRequested();
				ByteCode byteCode = agenda.Pop();

				// Calculate new stack
				StackSlot[] newStack = StackSlot.ModifyStack(byteCode.StackBefore, byteCode.PopCount >= 0 ? byteCode.PopCount : byteCode.StackBefore.Length, byteCode.PushCount, byteCode);
				
				// Calculate new variable state
				VariableSlot[] newVariableState = VariableSlot.CloneVariableState(byteCode.VariablesBefore);
				if (byteCode.IsVariableDefinition && byteCode.Operand is Local) {
					newVariableState[((Local)byteCode.Operand).Index] = new VariableSlot(new [] { byteCode }, false);
				}
				// After the leave, finally block might have touched the variables
				else if (byteCode.Code == ILCode.Leave) {
					newVariableState = VariableSlot.MakeUknownState(varCount);
				}
				
				// Find all successors
				List<ByteCode> branchTargets = new List<ByteCode>();
				if (!byteCode.Code.IsUnconditionalControlFlow()) {
					if (exceptionHandlerStarts.Contains(byteCode.Next)) {
						// Do not fall though down to exception handler
						// It is invalid IL as per ECMA-335 �12.4.2.8.1, but some obfuscators produce it
					} else {
						branchTargets.Add(byteCode.Next);
					}
				}
				if (byteCode.Operand is IList<Instruction>) {
					foreach(Instruction inst2 in (IList<Instruction>)byteCode.Operand) {
						ByteCode target = instrToByteCode[inst2];
						branchTargets.Add(target);
						// The target of a branch must have label
						if (target.Label == null) {
							target.Label = new ILLabel() { Name = target.Name };
						}
					}
				} else if (byteCode.Operand is Instruction) {
					ByteCode target = instrToByteCode[(Instruction)byteCode.Operand];
					branchTargets.Add(target);
					// The target of a branch must have label
					if (target.Label == null) {
						target.Label = new ILLabel() { Name = target.Name };
					}
				}
				
				// Apply the state to successors
				foreach (ByteCode branchTarget in branchTargets) {
					if (branchTarget == null)
						continue;
					if (branchTarget.StackBefore == null && branchTarget.VariablesBefore == null) {
						if (branchTargets.Count == 1) {
							branchTarget.StackBefore = newStack;
							branchTarget.VariablesBefore = newVariableState;
						} else {
							// Do not share data for several bytecodes
							branchTarget.StackBefore = StackSlot.ModifyStack(newStack, 0, 0, null);
							branchTarget.VariablesBefore = VariableSlot.CloneVariableState(newVariableState);
						}
						agenda.Push(branchTarget);
					} else {
						if (branchTarget.StackBefore.Length != newStack.Length) {
							throw new Exception("Inconsistent stack size at " + byteCode.Name);
						}
						
						// Be careful not to change our new data - it might be reused for several branch targets.
						// In general, be careful that two bytecodes never share data structures.
						
						bool modified = false;
						
						// Merge stacks - modify the target
						for (int i = 0; i < newStack.Length; i++) {
							ByteCode[] oldDefs = branchTarget.StackBefore[i].Definitions;
							ByteCode[] newDefs = Union(oldDefs, newStack[i].Definitions);
							if (newDefs.Length > oldDefs.Length) {
								branchTarget.StackBefore[i] = new StackSlot(newDefs, null);
								modified = true;
							}
						}
						
						// Merge variables - modify the target
						for (int i = 0; i < newVariableState.Length; i++) {
							VariableSlot oldSlot = branchTarget.VariablesBefore[i];
							if (!oldSlot.UnknownDefinition) {
								VariableSlot newSlot = newVariableState[i];
								if (newSlot.UnknownDefinition) {
									branchTarget.VariablesBefore[i] = newSlot;
									modified = true;
								} else {
									ByteCode[] oldDefs = oldSlot.Definitions;
									ByteCode[] newDefs = Union(oldDefs, newSlot.Definitions);
									if (newDefs.Length > oldDefs.Length) {
										branchTarget.VariablesBefore[i] = new VariableSlot(newDefs, false);
										modified = true;
									}
								}
							}
						}
						
						if (modified) {
							agenda.Push(branchTarget);
						}
					}
				}
			}
			
			// Occasionally the compilers or obfuscators generate unreachable code (which might be intentionally invalid)
			// I believe it is safe to just remove it
			StackAnalysis_body.RemoveAll(b => b.StackBefore == null);
			
			// Generate temporary variables to replace stack
			foreach(ByteCode byteCode in StackAnalysis_body) {
				int argIdx = 0;
				int popCount = byteCode.PopCount >= 0 ? byteCode.PopCount : byteCode.StackBefore.Length;
				for (int i = byteCode.StackBefore.Length - popCount; i < byteCode.StackBefore.Length; i++) {
					ILVariable tmpVar = new ILVariable() { Name = "arg_" + byteCode.Offset.ToString("X2") + "_" + argIdx.ToString(), GeneratedByDecompiler = true };
					byteCode.StackBefore[i] = new StackSlot(byteCode.StackBefore[i].Definitions, tmpVar);
					foreach(ByteCode pushedBy in byteCode.StackBefore[i].Definitions) {
						if (pushedBy.StoreTo == null) {
							pushedBy.StoreTo = new List<ILVariable>(1);
						}
						pushedBy.StoreTo.Add(tmpVar);
					}
					argIdx++;
				}
			}

			StackAnalysis_ILVariable_StackSlot_dict.Clear();
			StackAnalysis_ILVariables_hash.Clear();
			bool dictInitd = false;

			// Try to use single temporary variable insted of several if possilbe (especially useful for dup)
			// This has to be done after all temporary variables are assigned so we know about all loads
			foreach (ByteCode byteCode in StackAnalysis_body) {
				if (byteCode.StoreTo != null && byteCode.StoreTo.Count > 1) {
					var locVars = byteCode.StoreTo;

					if (!dictInitd) {
						dictInitd = true;
						foreach (var bc in StackAnalysis_body) {
							var storeTo = byteCode.StoreTo;
							if (storeTo == null)
								continue;
							foreach (var v in storeTo)
								StackAnalysis_ILVariables_hash.Add(v);
						}
						foreach (var bc in StackAnalysis_body) {
							foreach (var s in bc.StackBefore) {
								var loadFrom = s.LoadFrom;
								if (loadFrom == null)
									continue;
								if (StackAnalysis_ILVariables_hash.Contains(loadFrom)) {
									if (StackAnalysis_ILVariable_StackSlot_dict.ContainsKey(loadFrom))
										StackAnalysis_ILVariable_StackSlot_dict[loadFrom] = null;
									else
										StackAnalysis_ILVariable_StackSlot_dict[loadFrom] = s;
								}
							}
						}
					}

					// For each of the variables, find the location where it is loaded - there should be preciesly one
					StackAnalysis_Dict_ILVariable_Boolean.Clear();
					bool singleStore = true;
					for (int ii = 0; ii < locVars.Count; ii++) {
						var locVar = locVars[ii];
						StackAnalysis_Dict_ILVariable_Boolean[locVar] = true;
						StackSlot? nss;
						if (!(StackAnalysis_ILVariable_StackSlot_dict.TryGetValue(locVar, out nss) && nss != null && nss.Value.Definitions.Length == 1 && nss.Value.Definitions[0] == byteCode)) {
							singleStore = false;
							break;
						}
					}
					// We now know that all the variables have a single load,
					// Let's make sure that they have also a single store - us
					if (singleStore) {
						// Great - we can reduce everything into single variable
						ILVariable tmpVar = new ILVariable() { Name = string.Format("expr_{0:X2}", byteCode.Offset), GeneratedByDecompiler = true };
						locVars.Clear();
						locVars.Add(tmpVar);
						foreach(ByteCode bc in StackAnalysis_body) {
							for (int i = 0; i < bc.StackBefore.Length; i++) {
								// Is it one of the variable to be merged?
								var key = bc.StackBefore[i].LoadFrom;
								if (key != null && StackAnalysis_Dict_ILVariable_Boolean.ContainsKey(key)) {
									// Replace with the new temp variable
									bc.StackBefore[i] = new StackSlot(bc.StackBefore[i].Definitions, tmpVar);
								}
							}
						}
					}
				}
			}
			
			// Split and convert the normal local variables
			ConvertLocalVariables(StackAnalysis_body);

			// Convert branch targets to labels
			foreach (ByteCode byteCode in StackAnalysis_body) {
				if (byteCode.Operand is IList<Instruction>) {
					StackAnalysis_List_ILLabel.Clear();
					var oldList = (IList<Instruction>)byteCode.Operand;
					for (int i = 0; i < oldList.Count; i++)
						StackAnalysis_List_ILLabel.Add(instrToByteCode[oldList[i]].Label);
					byteCode.Operand = StackAnalysis_List_ILLabel.ToArray();
				} else if (byteCode.Operand is Instruction) {
					byteCode.Operand = instrToByteCode[(Instruction)byteCode.Operand].Label;
				}
			}
			
			// Convert parameters to ILVariables
			ConvertParameters(StackAnalysis_body);
			
			return StackAnalysis_body;
		}

		ByteCode[] Union(ByteCode[] a, ByteCode[] b)
		{
			if (a == b)
				return a;
			if (a.Length == 0)
				return b;
			if (b.Length == 0)
				return a;
			if (a.Length == 1) {
				if (b.Length == 1)
					return a[0] == b[0] ? a : new[] { a[0], b[0] };
			}
			else if (a.Length == 2 && b.Length == 2) {
				if ((a[0] == b[0] && a[1] == b[1]) || (a[0] == b[1] && a[1] == b[0]))
					return a;
			}
			StackAnalysis_HashSet_ByteCode.Clear();
			foreach (var x in a)
				StackAnalysis_HashSet_ByteCode.Add(x);
			foreach (var x in b)
				StackAnalysis_HashSet_ByteCode.Add(x);
			if (a.Length == b.Length && a.Length == StackAnalysis_HashSet_ByteCode.Count)
				return a;
			var ary = new ByteCode[StackAnalysis_HashSet_ByteCode.Count];
			int i = 0;
			foreach (var x in StackAnalysis_HashSet_ByteCode)
				ary[i++] = x;
			return ary;
		}

		static bool IsDeterministicLdloca(ByteCode b)
		{
			var v = b.Operand;
			b = b.Next;
			if (b.Code == ILCode.Initobj) return true;

			// instance method calls on value types use the variable ref deterministically
			int stack = 1;
			while (true) {
				if (b.PopCount < 0) return false;
				stack -= b.PopCount;
				if (stack == 0) break;
				if (stack < 0) return false;
				switch (b.Code) {
					case ILCode.Brfalse_S:
					case ILCode.Brtrue_S:
					case ILCode.Beq_S:
					case ILCode.Bge_S:
					case ILCode.Bgt_S:
					case ILCode.Ble_S:
					case ILCode.Blt_S:
					case ILCode.Bne_Un_S:
					case ILCode.Bge_Un_S:
					case ILCode.Bgt_Un_S:
					case ILCode.Ble_Un_S:
					case ILCode.Blt_Un_S:
					case ILCode.Brfalse:
					case ILCode.Brtrue:
					case ILCode.Beq:
					case ILCode.Bge:
					case ILCode.Bgt:
					case ILCode.Ble:
					case ILCode.Blt:
					case ILCode.Bne_Un:
					case ILCode.Bge_Un:
					case ILCode.Bgt_Un:
					case ILCode.Ble_Un:
					case ILCode.Blt_Un:
					case ILCode.Switch:
					case ILCode.Br:
					case ILCode.Br_S:
					case ILCode.Leave:
					case ILCode.Leave_S:
					case ILCode.Ret:
					case ILCode.Endfilter:
					case ILCode.Endfinally:
					case ILCode.Throw:
					case ILCode.Rethrow:
					case ILCode.LoopContinue:
					case ILCode.LoopOrSwitchBreak:
					case ILCode.YieldBreak:
					case ILCode.Jmp:
						return false;

					case ILCode.Ldloc:
					case ILCode.Ldloca:
					case ILCode.Stloc:
						if (v != null && b.Operand == v) return false;
						break;
				}
				stack += b.PushCount;
				b = b.Next;
				if (b == null) return false;
			}
			if (b.Code == ILCode.Ldfld || b.Code == ILCode.Stfld)
				return true;
			return (b.Code == ILCode.Call || b.Code == ILCode.Callvirt) && b.Operand is IMethod && ((IMethod)b.Operand).MethodSig != null && ((IMethod)b.Operand).MethodSig.HasThis;
		}
		
		sealed class VariableInfo
		{
			public ILVariable Variable;
			public List<ByteCode> Defs;
			public List<ByteCode> Uses;
		}
		
		/// <summary>
		/// If possible, separates local variables into several independent variables.
		/// It should undo any compilers merging.
		/// </summary>
		void ConvertLocalVariables(List<ByteCode> body)
		{
			foreach(Local varDef in methodDef.Body.Variables) {

				// Find all definitions and uses of this variable
				var defs = new List<ByteCode>();
				var uses = new List<ByteCode>();
				foreach (var b in body) {
					if (b.Operand == varDef) {
						if (b.IsVariableDefinition)
							defs.Add(b);
						else
							uses.Add(b);
					}
				}
				
				List<VariableInfo> newVars;
				
				// If the variable is pinned, use single variable.
				// If any of the uses is from unknown definition, use single variable
				// If any of the uses is ldloca with a nondeterministic usage pattern, use  single variable
				if (!optimize || varDef.Type is PinnedSig || uses.Any(b => b.VariablesBefore[varDef.Index].UnknownDefinition || (b.Code == ILCode.Ldloca && !IsDeterministicLdloca(b)))) {				
					newVars = new List<VariableInfo>(1) { new VariableInfo() {
						Variable = new ILVariable() {
							Name = string.IsNullOrEmpty(varDef.Name) ? "var_" + varDef.Index : varDef.Name,
							Type = varDef.Type is PinnedSig ? ((PinnedSig)varDef.Type).Next : varDef.Type,
							OriginalVariable = varDef
						},
						Defs = defs,
						Uses = uses
					}};
				} else {
					// Create a new variable for each definition
					newVars = defs.Select(def => new VariableInfo() {
						Variable = new ILVariable() {
							Name = (string.IsNullOrEmpty(varDef.Name) ? "var_" + varDef.Index : varDef.Name) + "_" + def.Offset.ToString("X2"),
							Type = varDef.Type,
							OriginalVariable = varDef
					    },
					    Defs = new List<ByteCode>() { def },
					    Uses  = new List<ByteCode>()
					}).ToList();
					
					// VB.NET uses the 'init' to allow use of uninitialized variables.
					// We do not really care about them too much - if the original variable
					// was uninitialized at that point it means that no store was called and
					// thus all our new variables must be uninitialized as well.
					// So it does not matter which one we load.
					
					// TODO: We should add explicit initialization so that C# code compiles.
					// Remember to handle cases where one path inits the variable, but other does not.
					
					// Add loads to the data structure; merge variables if necessary
					foreach(ByteCode use in uses) {
						ByteCode[] useDefs = use.VariablesBefore[varDef.Index].Definitions;
						if (useDefs.Length == 1) {
							VariableInfo newVar = newVars.Single(v => v.Defs.Contains(useDefs[0]));
							newVar.Uses.Add(use);
						} else {
							List<VariableInfo> mergeVars = newVars.Where(v => v.Defs.Intersect(useDefs).Any()).ToList();
							VariableInfo mergedVar = new VariableInfo() {
								Variable = mergeVars[0].Variable,
								Defs = mergeVars.SelectMany(v => v.Defs).ToList(),
								Uses = mergeVars.SelectMany(v => v.Uses).ToList()
							};
							mergedVar.Uses.Add(use);
							newVars = newVars.Except(mergeVars).ToList();
							newVars.Add(mergedVar);
						}
					}
				}
				
				// Set bytecode operands
				foreach(VariableInfo newVar in newVars) {
					foreach(ByteCode def in newVar.Defs) {
						def.Operand = newVar.Variable;
					}
					foreach(ByteCode use in newVar.Uses) {
						use.Operand = newVar.Variable;
					}
				}
			}
		}
		
		void ConvertParameters(List<ByteCode> body)
		{
			ILVariable thisParameter = null;
			if (methodDef.HasThis) {
				TypeDef type = methodDef.DeclaringType;
				thisParameter = new ILVariable();
				thisParameter.Type = DnlibExtensions.IsValueType(type) ? new ByRefSig(type.ToTypeSig()) : type.ToTypeSig();
				thisParameter.Name = "this";
				thisParameter.OriginalParameter = methodDef.Parameters[0];
			}
			foreach (Parameter p in methodDef.Parameters.SkipNonNormal()) {
				this.Parameters.Add(new ILVariable { Type = p.Type, Name = p.Name, OriginalParameter = p });
			}
			if (this.Parameters.Count > 0 && (methodDef.SemanticsAttributes & (MethodSemanticsAttributes.Setter | MethodSemanticsAttributes.AddOn | MethodSemanticsAttributes.RemoveOn)) != 0) {
				// last parameter must be 'value', so rename it
				this.Parameters.Last().Name = "value";
			}
			foreach (ByteCode byteCode in body) {
				Parameter p;
				switch (byteCode.Code) {
					case ILCode.Ldarg:
						p = byteCode.Operand as Parameter;
						byteCode.Code = ILCode.Ldloc;
						byteCode.Operand = p == null ? null : p.IsHiddenThisParameter ? thisParameter : this.Parameters[p.MethodSigIndex];
						break;
					case ILCode.Starg:
						p = byteCode.Operand as Parameter;
						byteCode.Code = ILCode.Stloc;
						byteCode.Operand = p == null ? null : p.IsHiddenThisParameter ? thisParameter : this.Parameters[p.MethodSigIndex];
						break;
					case ILCode.Ldarga:
						p = byteCode.Operand as Parameter;
						byteCode.Code = ILCode.Ldloca;
						byteCode.Operand = p == null ? null : p.IsHiddenThisParameter ? thisParameter : this.Parameters[p.MethodSigIndex];
						break;
				}
			}
			if (thisParameter != null)
				this.Parameters.Add(thisParameter);
		}
		
		List<ILNode> ConvertToAst(List<ByteCode> body, HashSet<ExceptionHandler> ehs)
		{
			List<ILNode> ast = new List<ILNode>();

			uint codeSize = (uint)methodDef.Body.GetCodeSize();
			while (ehs.Any()) {
				ILTryCatchBlock tryCatchBlock = new ILTryCatchBlock();
				
				// Find the first and widest scope
				uint tryStart = ehs.Min(eh => eh.TryStart.GetOffset());
				uint tryEnd   = ehs.Where(eh => eh.TryStart.GetOffset() == tryStart).Max(eh => eh.TryEnd?.Offset ?? codeSize);
				var handlers = ehs.Where(eh => eh.TryStart.GetOffset() == tryStart && (eh.TryEnd?.Offset ?? codeSize) == tryEnd).ToList();
				
				// Remember that any part of the body migt have been removed due to unreachability
				
				// Cut all instructions up to the try block
				{
					int tryStartIdx = 0;
					while (tryStartIdx < body.Count && body[tryStartIdx].Offset < tryStart) tryStartIdx++;
					ast.AddRange(ConvertToAst(body.CutRange(0, tryStartIdx)));
				}
				
				// Cut the try block
				{
					HashSet<ExceptionHandler> nestedEHs = new HashSet<ExceptionHandler>(ehs.Where(eh => (tryStart <= eh.TryStart.GetOffset() && (eh.TryEnd?.Offset ?? codeSize) < tryEnd) || (tryStart < eh.TryStart.GetOffset() && (eh.TryEnd?.Offset ?? codeSize) <= tryEnd)));
					ehs.ExceptWith(nestedEHs);
					int tryEndIdx = 0;
					while (tryEndIdx < body.Count && body[tryEndIdx].Offset < tryEnd) tryEndIdx++;
					tryCatchBlock.TryBlock = new ILBlock(ConvertToAst(body.CutRange(0, tryEndIdx), nestedEHs), CodeBracesRangeFlags.TryBraces);
				}
				
				// Cut all handlers
				tryCatchBlock.CatchBlocks = new List<ILTryCatchBlock.CatchBlock>();
				foreach(ExceptionHandler eh in handlers) {
					uint handlerEndOffset = eh.HandlerEnd?.Offset ?? codeSize;
					int startIdx = 0;
					while (startIdx < body.Count && body[startIdx].Offset < eh.HandlerStart.GetOffset()) startIdx++;
					int endIdx = startIdx;
					while (endIdx < body.Count && body[endIdx].Offset < handlerEndOffset) endIdx++;
					HashSet<ExceptionHandler> nestedEHs = new HashSet<ExceptionHandler>(ehs.Where(e => (eh.HandlerStart.GetOffset() <= e.TryStart.GetOffset() && (e.TryEnd?.Offset ?? codeSize) < handlerEndOffset) || (eh.HandlerStart.GetOffset() < e.TryStart.GetOffset() && (e.TryEnd?.Offset ?? codeSize) <= handlerEndOffset)));
					ehs.ExceptWith(nestedEHs);
					List<ILNode> handlerAst = ConvertToAst(body.CutRange(startIdx, endIdx - startIdx), nestedEHs);
					if (eh.HandlerType == ExceptionHandlerType.Catch) {
						ILTryCatchBlock.CatchBlock catchBlock = new ILTryCatchBlock.CatchBlock(context.CalculateBinSpans, handlerAst) {
							ExceptionType = eh.CatchType.ToTypeSig(),
						};
						// Handle the automatically pushed exception on the stack
						ByteCode ldexception = ldexceptions[eh];
						ConvertExceptionVariable(eh, catchBlock, ldexception);
						tryCatchBlock.CatchBlocks.Add(catchBlock);
					} else if (eh.HandlerType == ExceptionHandlerType.Finally) {
						tryCatchBlock.FinallyBlock = new ILBlock(handlerAst, CodeBracesRangeFlags.FinallyBraces);
					} else if (eh.HandlerType == ExceptionHandlerType.Fault) {
						tryCatchBlock.FaultBlock = new ILBlock(handlerAst, CodeBracesRangeFlags.FaultBraces);
					} else if (eh.HandlerType == ExceptionHandlerType.Filter) {
						ILTryCatchBlock.CatchBlock catchBlock = new ILTryCatchBlock.CatchBlock(context.CalculateBinSpans, handlerAst) {
							ExceptionType = eh.CatchType.ToTypeSig(),
						};
						// Handle the automatically pushed exception on the stack
						ByteCode ldexception = ldexceptions[eh];
						ConvertExceptionVariable(eh, catchBlock, ldexception);
						tryCatchBlock.CatchBlocks.Add(catchBlock);

						// Extract the filter part
						startIdx = 0;
						while (startIdx < body.Count && body[startIdx].Offset < eh.FilterStart.GetOffset()) startIdx++;
						endIdx = startIdx;
						uint ehHandlerStart = eh.HandlerStart.GetOffset();
						while (endIdx < body.Count && body[endIdx].Offset < ehHandlerStart) endIdx++;
						nestedEHs = new HashSet<ExceptionHandler>(ehs.Where(e => (eh.FilterStart.GetOffset() <= e.TryStart.GetOffset() && (e.TryEnd?.Offset ?? codeSize) < ehHandlerStart) || (eh.FilterStart.GetOffset() < e.TryStart.GetOffset() && (e.TryEnd?.Offset ?? codeSize) <= ehHandlerStart)));
						ehs.ExceptWith(nestedEHs);
						List<ILNode> filterAst = ConvertToAst(body.CutRange(startIdx, endIdx - startIdx), nestedEHs);
						var filterBlock = new ILTryCatchBlock.FilterILBlock(context.CalculateBinSpans, filterAst) {
							ExceptionType = null,
						};
						ByteCode ldfilter = ldfilters[eh];
						ConvertExceptionVariable(eh, filterBlock, ldfilter);
						catchBlock.FilterBlock = filterBlock;
					}
				}
				
				ehs.ExceptWith(handlers);
				
				ast.Add(tryCatchBlock);
			}
			
			// Add whatever is left
			ast.AddRange(ConvertToAst(body));
			
			return ast;
		}

		private void ConvertExceptionVariable(ExceptionHandler eh, ILTryCatchBlock.CatchBlockBase catchBlock, ByteCode ldexception)
		{
			if ((ldexception.StoreTo?.Count ?? 0) == 0) {
				// Exception is not used
				catchBlock.ExceptionVariable = null;
			} else if (ldexception.StoreTo.Count == 1) {
				ILExpression first = catchBlock.Body[0] as ILExpression;
				if (first != null &&
					first.Code == ILCode.Pop &&
					first.Arguments[0].Code == ILCode.Ldloc &&
					first.Arguments[0].Operand == ldexception.StoreTo[0])
				{
					// The exception is just poped - optimize it all away;
					if (context.Settings.AlwaysGenerateExceptionVariableForCatchBlocksUnlessTypeIsObject && (eh.CatchType != null && !eh.CatchType.IsSystemObject()))
						catchBlock.ExceptionVariable = new ILVariable() { Name = "ex_" + eh.HandlerStart.GetOffset().ToString("X2"), GeneratedByDecompiler = true };
					else
						catchBlock.ExceptionVariable = null;
					if (context.CalculateBinSpans)
						catchBlock.Body[0].AddSelfAndChildrenRecursiveBinSpans(catchBlock.StlocBinSpans);
					catchBlock.Body.RemoveAt(0);
				} else {
					catchBlock.ExceptionVariable = ldexception.StoreTo[0];
				}
			} else {
				ILVariable exTemp = new ILVariable() { Name = "ex_" + eh.HandlerStart.GetOffset().ToString("X2"), GeneratedByDecompiler = true };
				catchBlock.ExceptionVariable = exTemp;
				foreach (ILVariable storeTo in ldexception.StoreTo) {
					catchBlock.Body.Insert(0, new ILExpression(ILCode.Stloc, storeTo, new ILExpression(ILCode.Ldloc, exTemp)));
				}
			}
		}
		
		List<ILNode> ConvertToAst(List<ByteCode> body)
		{
			List<ILNode> ast = new List<ILNode>();
			
			// Convert stack-based IL code to ILAst tree
			foreach(ByteCode byteCode in body) {
				if (byteCode.StackBefore == null) {
					// Unreachable code
					continue;
				}
				
				ILExpression expr = new ILExpression(byteCode.Code, byteCode.Operand);
				if (context.CalculateBinSpans)
					expr.BinSpans.Add(new BinSpan(byteCode.Offset, byteCode.EndOffset - byteCode.Offset));
				if (byteCode.Prefixes != null && byteCode.Prefixes.Length > 0) {
					ILExpressionPrefix[] prefixes = new ILExpressionPrefix[byteCode.Prefixes.Length];
					for (int i = 0; i < prefixes.Length; i++) {
						prefixes[i] = new ILExpressionPrefix(ilCodeTranslation[byteCode.Prefixes[i].OpCode.Code], byteCode.Prefixes[i].Operand);
					}
					expr.Prefixes = prefixes;
				}
				
				// Label for this instruction
				if (byteCode.Label != null) {
					ast.Add(byteCode.Label);
				}

				// Reference arguments using temporary variables
				int popCount = byteCode.PopCount >= 0 ? byteCode.PopCount : byteCode.StackBefore.Length;
				for (int i = byteCode.StackBefore.Length - popCount; i < byteCode.StackBefore.Length; i++) {
					StackSlot slot = byteCode.StackBefore[i];
					expr.Arguments.Add(new ILExpression(ILCode.Ldloc, slot.LoadFrom));
				}
				
				// Store the result to temporary variable(s) if needed
				if ((byteCode.StoreTo?.Count ?? 0) == 0) {
					ast.Add(expr);
				} else if (byteCode.StoreTo.Count == 1) {
					ast.Add(new ILExpression(ILCode.Stloc, byteCode.StoreTo[0], expr));
				} else {
					ILVariable tmpVar = new ILVariable() { Name = "expr_" + byteCode.Offset.ToString("X2"), GeneratedByDecompiler = true };
					ast.Add(new ILExpression(ILCode.Stloc, tmpVar, expr));
					foreach(ILVariable storeTo in byteCode.StoreTo.AsEnumerable().Reverse()) {
						ast.Add(new ILExpression(ILCode.Stloc, storeTo, new ILExpression(ILCode.Ldloc, tmpVar)));
					}
				}
			}
			
			return ast;
		}
	}
	
	public static class ILAstBuilderExtensionMethods
	{
		public static List<T> CutRange<T>(this List<T> list, int start, int count)
		{
			List<T> ret = new List<T>(count);
			for (int i = 0; i < count; i++) {
				ret.Add(list[start + i]);
			}
			list.RemoveRange(start, count);
			return ret;
		}
	}
}
