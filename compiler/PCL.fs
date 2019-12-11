﻿namespace Compiler

open FSharp.Text.Lexing
open CodeGenerator
open LLVMSharp

open System.Text.RegularExpressions
open System.Diagnostics
open System.Runtime.InteropServices

module PCL =

  let private verifyAndDump _module =
    if LLVM.VerifyModule (_module, LLVMVerifierFailureAction.LLVMPrintMessageAction, ref null) <> LLVMBool 0 then
      printfn "Erroneuous module\n"
    else
      if Helpers.Environment.CLI.InterimCodeToStdout then
        LLVM.DumpModule _module
      else
        LLVM.PrintModuleToFile (_module, "test.txt", ref null) |> ignore

  let private generateX86Assembly () =
    LLVM.InitializeX86TargetInfo()
    LLVM.InitializeX86Target()
    LLVM.InitializeX86TargetMC()

    LLVM.InitializeX86AsmPrinter()

    let defaultTriple = Marshal.PtrToStringAnsi <| LLVM.GetDefaultTargetTriple ()
    let mutable target = Unchecked.defaultof<LLVMTargetRef>
    let mutable err = Unchecked.defaultof<string>

    let mutable buffer = Unchecked.defaultof<LLVMMemoryBufferRef>

    printfn "Default triple: %A" defaultTriple
    if LLVM.GetTargetFromTriple (defaultTriple, &target, &err) <> LLVMBool 0 then
      printfn "Could not get target from triple %A" err

    let targetMachine = LLVM.CreateTargetMachine (target, "x86-64", "generic", "", 
                          LLVMCodeGenOptLevel.LLVMCodeGenLevelAggressive, LLVMRelocMode.LLVMRelocStatic, LLVMCodeModel.LLVMCodeModelSmall)

    if LLVM.TargetMachineEmitToMemoryBuffer (targetMachine, CodeModule.theModule, LLVMCodeGenFileType.LLVMAssemblyFile, &err, &buffer) <> LLVMBool 0 then
      printfn "Could not emit assembly code"

    Marshal.PtrToStringAuto (LLVM.GetBufferStart buffer)

  let private combined program = async {
    let! semantic = Async.StartChild <| async { return Engine.Analyze program }
    let! arBuilder = Async.StartChild <| async { return GenerateARTypes program }

    let! res1 = semantic
    let! res2 = arBuilder

    return (res1, res2)
  }

  [<EntryPoint>]
  let main argv =

    if not(Helpers.Environment.CLI.parseCLIArguments argv) then
      exit 1

    (* Setup the input text *)
    let input = System.IO.File.ReadAllText Helpers.Environment.CLI.FileName

    (* Parse and perform semantic analysis *)
    try
      let parse input =
        let lexbuf = LexBuffer<_>.FromString input
        Parser.start Lexer.read lexbuf

      match parse input with
      | Some program -> printfn "errors:\n%A" Helpers.Error.Parser.errorList

                        let semanticAnalysis, semanticInstruction = Engine.Analyze program
                        let arTypes, globalInstructions = GenerateARTypes program
                        let labelNames = GenerateLabelledNames program

                        if not(semanticAnalysis) then
                          printfn "Semantic Analysis failed. Goodbye..."
                          exit 1

                        let topLevelFunction = 
                          match semanticInstruction with
                          | Base.SemDeclFunction (n, t, il) -> (n, t, il)
                          | _                      -> raise <| Helpers.Error.InternalException "Top Level Instruction must be a function"

                        let normalizedHierarchy = Engine.NormalizeInstructionHierarchy topLevelFunction |> Map.toList |> List.map snd

                        // printfn "normalized %A" normalizedHierarchy

                        // Can run in parallel with a few adjustments in AR type generation
                        // let semanticAnalysis, arTypes =
                        //   combined program |> 
                        //   Async.RunSynchronously

                        let externalFunctions = 
                          Helpers.ExternalFunctions.ExternalIO
                          |> List.map (fun (n, l, ret) -> (n, (List.map (fun (x,y,z) -> y,z) l), ret, [LLVMLinkage.LLVMExternalLinkage]))

                        CodeGenerator.GenerateLLVMModule ()

                        let theFPM = LLVM.CreatePassManager ()
                        LLVM.AddBasicAliasAnalysisPass theFPM
                        LLVM.AddPromoteMemoryToRegisterPass theFPM
                        LLVM.AddInstructionCombiningPass theFPM
                        LLVM.AddReassociatePass theFPM
                        LLVM.AddGVNPass theFPM
                        LLVM.AddCFGSimplificationPass theFPM
                        let thefFPM = LLVM.CreateFunctionPassManagerForModule(CodeModule.theModule)
                        LLVM.AddBasicAliasAnalysisPass thefFPM
                        LLVM.AddPromoteMemoryToRegisterPass thefFPM
                        LLVM.AddInstructionCombiningPass thefFPM
                        LLVM.AddReassociatePass thefFPM
                        LLVM.AddGVNPass thefFPM
                        LLVM.AddCFGSimplificationPass thefFPM
                        LLVM.InitializeFunctionPassManager thefFPM |> ignore

                        // generate global symbols (global variables and function declarations, external and private)
                        globalInstructions |> List.iter (fun gd -> gd ||> GenerateGlobalVariable)
                        List.iter (fun (x, y, z, w) -> CodeGenerator.GenerateFunctionRogue x y z w |> ignore) externalFunctions
                        List.iter (fun f -> CodeGenerator.GenerateFunctionPrototype arTypes f Base.Unit [] |> ignore) normalizedHierarchy

                        // Generate main function which calls the program's entry function
                        CodeGenerator.GenerateMain (fst normalizedHierarchy.Head) |> ignore

                        // Generate all program functions
                        List.iter (fun func -> CodeGenerator.GenerateFunctionCode arTypes labelNames func |> ignore) normalizedHierarchy

                        // * 'Custom Optimization Pass' which will transform all allocas to bitcasts of one big alloca 
                        // let theFunctionToOptimize = LLVM.GetNamedFunction (CodeModule.theModule, "hello.f")

                        // let mutable func = LLVM.GetFirstFunction CodeModule.theModule
                        // while func.Pointer <> System.IntPtr.Zero do
                        //   if not(Array.isEmpty <| func.GetBasicBlocks ()) then
                        //     let mutable fInstr = ((func.GetBasicBlocks ()).[0]).GetFirstInstruction ()
                        //     let mutable shouldRun = true
                        //     while fInstr.Pointer <> System.IntPtr.Zero do
                        //       if shouldRun && (fInstr.IsAAllocaInst ()).Pointer <> System.IntPtr.Zero then
                        //         shouldRun <- false
                        //         let newInstr = GenerateLocal <| CodeGenerator.Utils.ToLLVM Base.Integer
                        //         fInstr.ReplaceAllUsesWith (newInstr)
                        //       fInstr <- fInstr.GetNextInstruction ()
                        //     done
                        //   func <- LLVM.GetNextFunction func
                        // done

                        // LLVM.RunFunctionPassManager (thefFPM, theFunctionToOptimize) |> ignore
                        if Helpers.Environment.CLI.ShouldOptimize then
                          LLVM.RunPassManager (theFPM, CodeModule.theModule) |> ignore

                        verifyAndDump CodeModule.theModule
                       
      | None -> printfn "errors:\n%A\n\nNo input given" Helpers.Error.Parser.errorList
    with
      | Helpers.Error.Lexer.LexerException e -> printfn "Lex Exception -> %s" <| Helpers.Error.StringifyError e             ; exit 1             
      | Helpers.Error.Parser.ParserException e -> printfn "Parse Exception -> %s" <| Helpers.Error.StringifyError e         ; exit 1
      | Helpers.Error.Semantic.SemanticException e -> printfn "Semantic Exception -> %s" <| Helpers.Error.StringifyError e  ; exit 1
      | Helpers.Error.Symbolic.SymbolicException e -> printfn "Symbolic Exception -> %s" <| Helpers.Error.StringifyError e  ; exit 1
      | e -> printfn "%A" e ; exit 1

    let assemblyString = generateX86Assembly ()

    if Helpers.Environment.CLI.FinalCodeToStdout then
      printfn "%s" assemblyString
    else
      System.IO.File.WriteAllText("test.asm", assemblyString)
    0