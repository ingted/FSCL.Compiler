﻿namespace FSCL.Compiler.FunctionCodegen

open FSCL.Compiler
open System.Collections.Generic
open System.Reflection
open Microsoft.FSharp.Quotations

[<StepProcessor("FSCL_IF_ELSE_CODEGEN_PROCESSOR", "FSCL_FUNCTION_CODEGEN_STEP")>]
type IfThenElseCodegen() =   
    inherit FunctionBodyCodegenProcessor()
    let rec LiftAndOrOperator(expr:Expr, step:FunctionCodegenStep, cont) =
        match expr with
        | Patterns.IfThenElse(condinner, ifbinner, elsebinner) ->
            match ifbinner with
            | Patterns.Value(o, t) ->
                if(t = typeof<bool>) then
                    if (o :?> bool) then
                        Some("(" + cont(condinner) + ") || (" + cont(elsebinner) + ")")
                    else
                        None
                else
                    None
            | _ ->
                match elsebinner with  
                | Patterns.Value(o, t) ->
                    if(t = typeof<bool>) then   
                        if (not (o :?> bool)) then
                            Some("(" + cont(condinner) + ") && (" + cont(ifbinner) + ")")
                        else
                            None
                    else
                        None      
                | _ ->
                None      
        | _ ->
            None              

    override this.Run((expr, cont), en, opts) =
        let engine = en :?> FunctionCodegenStep
        match expr with
        | Patterns.IfThenElse(cond, ifb, elseb) ->
            let checkBoolOp = LiftAndOrOperator(expr, engine, cont)
            if checkBoolOp.IsSome then
                Some(checkBoolOp.Value)
            else
                // Fix: if null (Microsoft.Fsharp.Core.Unit) don't generate else branch
                if elseb.Type = typeof<Microsoft.FSharp.Core.unit> && elseb = Expr.Value<Microsoft.FSharp.Core.unit>(()) then
                    Some("if(" + cont(cond) + ") {\n" + cont(ifb) + "}\n")
                else
                    Some("if(" + cont(cond) + ") {\n" + cont(ifb) + "}\nelse {\n" + cont(elseb) + "\n}\n")
        | _ ->
            None