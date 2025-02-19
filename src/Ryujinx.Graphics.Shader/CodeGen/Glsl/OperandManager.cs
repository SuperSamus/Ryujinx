using Ryujinx.Graphics.Shader.CodeGen.Glsl.Instructions;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.StructuredIr;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using static Ryujinx.Graphics.Shader.StructuredIr.InstructionInfo;

namespace Ryujinx.Graphics.Shader.CodeGen.Glsl
{
    class OperandManager
    {
        private Dictionary<AstOperand, string> _locals;

        public OperandManager()
        {
            _locals = new Dictionary<AstOperand, string>();
        }

        public string DeclareLocal(AstOperand operand)
        {
            string name = $"{DefaultNames.LocalNamePrefix}_{_locals.Count}";

            _locals.Add(operand, name);

            return name;
        }

        public string GetExpression(CodeGenContext context, AstOperand operand)
        {
            return operand.Type switch
            {
                OperandType.Argument => GetArgumentName(operand.Value),
                OperandType.Constant => NumberFormatter.FormatInt(operand.Value),
                OperandType.LocalVariable => _locals[operand],
                OperandType.Undefined => DefaultNames.UndefinedName,
                _ => throw new ArgumentException($"Invalid operand type \"{operand.Type}\"."),
            };
        }

        public static string GetArgumentName(int argIndex)
        {
            return $"{DefaultNames.ArgumentNamePrefix}{argIndex}";
        }

        public static AggregateType GetNodeDestType(CodeGenContext context, IAstNode node)
        {
            // TODO: Get rid of that function entirely and return the type from the operation generation
            // functions directly, like SPIR-V does.

            if (node is AstOperation operation)
            {
                if (operation.Inst == Instruction.Load || operation.Inst.IsAtomic())
                {
                    switch (operation.StorageKind)
                    {
                        case StorageKind.ConstantBuffer:
                        case StorageKind.StorageBuffer:
                            if (operation.GetSource(0) is not AstOperand bindingIndex || bindingIndex.Type != OperandType.Constant)
                            {
                                throw new InvalidOperationException($"First input of {operation.Inst} with {operation.StorageKind} storage must be a constant operand.");
                            }

                            if (operation.GetSource(1) is not AstOperand fieldIndex || fieldIndex.Type != OperandType.Constant)
                            {
                                throw new InvalidOperationException($"Second input of {operation.Inst} with {operation.StorageKind} storage must be a constant operand.");
                            }

                            BufferDefinition buffer = operation.StorageKind == StorageKind.ConstantBuffer
                                ? context.Config.Properties.ConstantBuffers[bindingIndex.Value]
                                : context.Config.Properties.StorageBuffers[bindingIndex.Value];
                            StructureField field = buffer.Type.Fields[fieldIndex.Value];

                            return field.Type & AggregateType.ElementTypeMask;

                        case StorageKind.LocalMemory:
                        case StorageKind.SharedMemory:
                            if (operation.GetSource(0) is not AstOperand { Type: OperandType.Constant } bindingId)
                            {
                                throw new InvalidOperationException($"First input of {operation.Inst} with {operation.StorageKind} storage must be a constant operand.");
                            }

                            MemoryDefinition memory = operation.StorageKind == StorageKind.LocalMemory
                                ? context.Config.Properties.LocalMemories[bindingId.Value]
                                : context.Config.Properties.SharedMemories[bindingId.Value];

                            return memory.Type & AggregateType.ElementTypeMask;

                        case StorageKind.Input:
                        case StorageKind.InputPerPatch:
                        case StorageKind.Output:
                        case StorageKind.OutputPerPatch:
                            if (operation.GetSource(0) is not AstOperand varId || varId.Type != OperandType.Constant)
                            {
                                throw new InvalidOperationException($"First input of {operation.Inst} with {operation.StorageKind} storage must be a constant operand.");
                            }

                            IoVariable ioVariable = (IoVariable)varId.Value;
                            bool isOutput = operation.StorageKind == StorageKind.Output || operation.StorageKind == StorageKind.OutputPerPatch;
                            bool isPerPatch = operation.StorageKind == StorageKind.InputPerPatch || operation.StorageKind == StorageKind.OutputPerPatch;
                            int location = 0;
                            int component = 0;

                            if (context.Config.HasPerLocationInputOrOutput(ioVariable, isOutput))
                            {
                                if (operation.GetSource(1) is not AstOperand vecIndex || vecIndex.Type != OperandType.Constant)
                                {
                                    throw new InvalidOperationException($"Second input of {operation.Inst} with {operation.StorageKind} storage must be a constant operand.");
                                }

                                location = vecIndex.Value;

                                if (operation.SourcesCount > 2 &&
                                    operation.GetSource(2) is AstOperand elemIndex &&
                                    elemIndex.Type == OperandType.Constant &&
                                    context.Config.HasPerLocationInputOrOutputComponent(ioVariable, location, elemIndex.Value, isOutput))
                                {
                                    component = elemIndex.Value;
                                }
                            }

                            (_, AggregateType varType) = IoMap.GetGlslVariable(context.Config, ioVariable, location, component, isOutput, isPerPatch);

                            return varType & AggregateType.ElementTypeMask;
                    }
                }
                else if (operation.Inst == Instruction.Call)
                {
                    AstOperand funcId = (AstOperand)operation.GetSource(0);

                    Debug.Assert(funcId.Type == OperandType.Constant);

                    return context.GetFunction(funcId.Value).ReturnType;
                }
                else if (operation.Inst == Instruction.VectorExtract)
                {
                    return GetNodeDestType(context, operation.GetSource(0)) & ~AggregateType.ElementCountMask;
                }
                else if (operation is AstTextureOperation texOp)
                {
                    if (texOp.Inst == Instruction.ImageLoad ||
                        texOp.Inst == Instruction.ImageStore ||
                        texOp.Inst == Instruction.ImageAtomic)
                    {
                        return texOp.GetVectorType(texOp.Format.GetComponentType());
                    }
                    else if (texOp.Inst == Instruction.TextureSample)
                    {
                        return texOp.GetVectorType(GetDestVarType(operation.Inst));
                    }
                }

                return GetDestVarType(operation.Inst);
            }
            else if (node is AstOperand operand)
            {
                if (operand.Type == OperandType.Argument)
                {
                    int argIndex = operand.Value;

                    return context.CurrentFunction.GetArgumentType(argIndex);
                }

                return OperandInfo.GetVarType(operand);
            }
            else
            {
                throw new ArgumentException($"Invalid node type \"{node?.GetType().Name ?? "null"}\".");
            }
        }
    }
}
