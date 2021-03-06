﻿namespace FSCL.Compiler.AcceleratedCollections

open FSCL.Compiler
open FSCL.Language
open System.Collections.Generic
open System.Reflection
open System.Reflection.Emit
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Core.LanguagePrimitives
open System
open Microsoft.FSharp.Reflection
open AcceleratedCollectionUtil
open FSCL.Compiler.Util
open Microsoft.FSharp.Linq.RuntimeHelpers

type AcceleratedArrayMap2Handler() =    
    interface IAcceleratedCollectionHandler with
        member this.Process(methodInfo, cleanArgs, root, meta, step, env, opts) =
            // Inspect operator
            let computationFunction, subExpr =                
                AcceleratedCollectionUtil.ParseOperatorLambda(cleanArgs.[0], step, env, opts)
                                
            match subExpr with
            | Some(kfg, newEnv) ->
                // This coll fun is a composition 
                let node = new KFGCollectionCompositionNode(methodInfo, kfg, newEnv)
                
                // Parse arguments
                let subnode1 = step.Process(cleanArgs.[1], env, opts)
                let subnode2 = step.Process(cleanArgs.[2], env, opts)
                node.InputNodes.Add(subnode1)
                node.InputNodes.Add(subnode2)
                Some(node :> IKFGNode)   
            | _ ->
                // This coll fun is a kernel
                match computationFunction with
                | Some(thisVar, ob, functionName, functionInfo, functionParamVars, functionReturnType, functionBody) ->
                    // We need to get the type of a array whose elements type is the same of the functionInfo parameter
                    let firstInputArrayType, secondInputArrayType = 
                        if methodInfo.Name = "Map2" then
                            (functionParamVars.[0].Type.MakeArrayType(), functionParamVars.[1].Type.MakeArrayType())
                        else
                            (functionParamVars.[1].Type.MakeArrayType(), functionParamVars.[2].Type.MakeArrayType())
                    let outputArrayType = functionReturnType.MakeArrayType()
                    let kernelName, runtimeName = 
                        if methodInfo.Name = "Map2" then
                            "ArrayMap2_" + functionName, "Array.map2"
                        else
                            "ArrayMapi2_" + functionName, "Array.mapi2"     
                    
                    // Create parameters placeholders
                    let input1Holder = Quotations.Var("input_array_1", firstInputArrayType)
                    let input2Holder = Quotations.Var("input_array_2", secondInputArrayType)
                    let outputHolder = Quotations.Var("output_array", outputArrayType)
                    let tupleHolder = Quotations.Var("tupledArg", FSharpType.MakeTupleType([| firstInputArrayType; secondInputArrayType; typeof<WorkItemInfo> |]))
                    let wiHolder = Quotations.Var("workItemInfo", typeof<WorkItemInfo>)

                    // Finally, create the body of the kernel
                    let globalIdVar = Quotations.Var("global_id", typeof<int>)
                    let firstGetElementMethodInfo, _ = AcceleratedCollectionUtil.GetArrayAccessMethodInfo(firstInputArrayType.GetElementType(), 1)
                    let secondGetElementMethodInfo, _ = AcceleratedCollectionUtil.GetArrayAccessMethodInfo(secondInputArrayType.GetElementType(), 1)
                    let _, setElementMethodInfo = AcceleratedCollectionUtil.GetArrayAccessMethodInfo(outputArrayType.GetElementType(), 1)
                    let kernelBody = 
                        Expr.Lambda(tupleHolder,
                            Expr.Let(input1Holder, Expr.TupleGet(Expr.Var(tupleHolder), 0),
                                Expr.Let(input2Holder, Expr.TupleGet(Expr.Var(tupleHolder), 1),
                                    Expr.Let(wiHolder, Expr.TupleGet(Expr.Var(tupleHolder), 2),
                                        Expr.Let(outputHolder, Expr.Call(
                                                            GetZeroCreateMethod(outputArrayType.GetElementType(), 1), 
                                                                                [ Expr.PropertyGet(Expr.Var(input1Holder), 
                                                                                                firstInputArrayType.GetProperty("Length")) ]),
                                            Expr.Let(globalIdVar,
                                                        Expr.Call(Expr.Var(wiHolder), typeof<WorkItemInfo>.GetMethod("GlobalID"), [ Expr.Value(0) ]),
                                                        Expr.Sequential(
                                                            Expr.Call(setElementMethodInfo,
                                                                [ Expr.Var(outputHolder);
                                                                    Expr.Var(globalIdVar);
                                                                    AcceleratedCollectionUtil.BuildApplication(
                                                                            functionBody,
                                                                            
                                                                            if methodInfo.Name = "Map2" then
                                                                                [ Expr.Call(firstGetElementMethodInfo,
                                                                                            [ Expr.Var(input1Holder);
                                                                                                Expr.Var(globalIdVar) 
                                                                                            ]);
                                                                                    Expr.Call(secondGetElementMethodInfo,
                                                                                            [ Expr.Var(input2Holder);
                                                                                                Expr.Var(globalIdVar) 
                                                                                            ])
                                                                                ]
                                                                            else
                                                                                // Mapi2
                                                                                [ Expr.Var(globalIdVar);
                                                                                  Expr.Call(firstGetElementMethodInfo,
                                                                                            [ Expr.Var(input1Holder);
                                                                                                Expr.Var(globalIdVar) 
                                                                                            ]);
                                                                                  Expr.Call(secondGetElementMethodInfo,
                                                                                            [ Expr.Var(input2Holder);
                                                                                                Expr.Var(globalIdVar) 
                                                                                            ])
                                                                                ]
                                                                )]),
                                                                Expr.Var(outputHolder))))))))
                                                                                
                    // Add the current kernel
                    let envVars, outVals = 
                        QuotationAnalysis.KernelParsing.ExtractEnvRefs(functionBody)
                    let mapFunctionInfo = new FunctionInfo(functionName, 
                                                           None,
                                                           functionParamVars,
                                                           functionReturnType,
                                                           envVars, outVals,
                                                           functionBody)
                                                           
                    // Add current kernelbody
                    let kInfo = new AcceleratedKernelInfo(kernelName, 
                                                          methodInfo,
                                                          [ input1Holder; input2Holder ],
                                                          outputArrayType,
                                                          envVars, outVals,
                                                          kernelBody,
                                                          meta,
                                                          runtimeName, Some(mapFunctionInfo :> IFunctionInfo),
                                                          Some(functionBody))
                    let kernelModule = new KernelModule(thisVar, ob, kInfo)

                    kernelModule.Functions.Add(mapFunctionInfo.ID, mapFunctionInfo)
                    kInfo.CalledFunctions.Add(mapFunctionInfo.ID)
                
                    // Create node
                    let node = new KFGKernelNode(kernelModule)
                    
//                    // Create data node for outsiders
//                    for o in outsiders do 
//                        node.InputNodes.Add(new KFGOutsiderDataNode(o))

                    // Parse arguments
                    let subnode1 = step.Process(cleanArgs.[1], env, opts)
                    node.InputNodes.Add(subnode1)
                    let subnode2 = step.Process(cleanArgs.[2], env, opts)
                    node.InputNodes.Add(subnode2)

                    Some(node :> IKFGNode)  
                | _ ->
                    None
