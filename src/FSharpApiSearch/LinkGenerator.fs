﻿namespace FSharpApiSearch

type LinkGenerator = Api -> string option

open FSharpApiSearch.StringPrinter

module LinkGenerator =
  open System.Web
  open System.Text

  type internal StringJoinContext = private {
    mutable IsFirstElement: bool
    Separator: string
  }
  with
    static member Create(sep) = { IsFirstElement = true; Separator = sep }
    member this.Print(sb: StringBuilder) =
      if this.IsFirstElement then
        this.IsFirstElement <- false
        sb
      else
        sb.Append(this.Separator)

  let internal toLower (str: string) = str.ToLower()
  let internal urlEncode (str: string) = HttpUtility.UrlEncode(str)

  let internal urlName (n: NameItem) =
    match n.Name with
    | SymbolName n -> n
    | OperatorName (n, _) -> n
    | WithCompiledName (n, _) -> n

  module internal FSharp =
    open System.Text

    let fullOpReplaceTable =
      dict [
        "..", "[-..-]"
        ".. ..", "[-..-..-]"
        "-", "[-]"
        "=", "[=]"
        "%", "[r-]"
        ":=", "[-=-]"
        "?%", "[-qr]"
        "?%?", "[-qr-q]"
        "~%", "[-zr"
      ]

    let opReplaceTable =
      dict [
        '<', '['
        '>', ']'
        '|', 'h'
        '*', 'a'
        '&', 'n'
        '+', 'p'
        '%', 'r'
        '/', 's'
        '~', 'z'
        '?', 'q'
      ]

    let isActivePattern (api: Api) = match api.Signature with ApiSignature.ActivePatten _ -> true | _ -> false

    let replaceOp (name: string) (sb:StringBuilder) =
      let op = name.[2..(name.Length - 3)]
      match fullOpReplaceTable.TryGetValue(op) with
      | true, replaced -> sb.Append(replaced)
      | false, _ ->
        let replaced = op |> String.map (fun s -> match opReplaceTable.TryGetValue(s) with true, r -> r | false, _ -> s)
        sb.Append("[-").Append(replaced).Append("-]")

    let isArray (n: NameItem) = (urlName n).StartsWith("[")

    let namePart (api: Api) (ns: Name) (sb: StringBuilder) =
      let path = urlName (List.item 1 ns)
      let name = urlName ns.Head
      sb.Append(path).Append(".") |> ignore
      if isActivePattern api then
        sb.Append(name.[3..(name.Length - 4)].Replace("|_", "").Replace("|", "h"))
      elif name.StartsWith("( ") then
        replaceOp name sb
      else
        sb.Append(name)

    let genericParamsPart (ns: Name) (sb: StringBuilder) =
      match ns with
      | { Name = OperatorName ("( = )", _) } :: { Name = SymbolName "Operators" } :: _ -> sb.Append("'t")
      | { Name = WithCompiledName ("array2D", "CreateArray2D") } :: { Name = SymbolName "ExtraTopLevelOperators" } :: _ -> sb.Append("['t]")
      | _ ->

        let genericParameters = ns |> List.collect (fun n -> n.GenericParameters) |> List.distinct

        match genericParameters with
        | [] -> sb
        | _ ->
          let ctx = StringJoinContext.Create(",")
          sb.Append("[") |> ignore
          for gp in genericParameters do sb.Append(ctx.Print).Append(gp.Print) |> ignore
          sb.Append("]") |> ignore
          sb

    let kindPart (api: Api) (ns: Name) : string option =
      match api.Signature with
      | ApiSignature.ActivePatten _ -> Some "active-pattern"
      | ApiSignature.ModuleValue _ when ns.Head.GenericParameters.IsEmpty = false -> Some "type-function"
      | ApiSignature.ModuleValue _
      | ApiSignature.ModuleFunction _ -> Some "function"
      | ApiSignature.InstanceMember (_, m) | ApiSignature.StaticMember (_, m) when m.Kind = MemberKind.Method -> Some "method"
      | ApiSignature.InstanceMember (_, m) | ApiSignature.StaticMember (_, m) when m.Kind = MemberKind.Field -> None
      | ApiSignature.InstanceMember _ | ApiSignature.StaticMember _ -> Some "property"
      | ApiSignature.Constructor _ -> Some "constructor"
      | ApiSignature.ModuleDefinition _ -> Some "module"
      | ApiSignature.FullTypeDefinition td  ->
        if isArray td.Name.Head then
          None
        else
          match td.Kind with
          | TypeDefinitionKind.Class -> Some "class"
          | TypeDefinitionKind.Interface -> Some "interface"
          | TypeDefinitionKind.Type -> Some "type"
          | TypeDefinitionKind.Union -> Some "union"
          | TypeDefinitionKind.Record -> Some "record"
          | TypeDefinitionKind.Enumeration -> Some "enumeration"
      | ApiSignature.TypeAbbreviation _ -> Some "type-abbreviation"
      | ApiSignature.TypeExtension _ ->
        match urlName (List.last ns) with
        | "System" -> Some "extension-method"
        | _ -> Some "method"
      | ApiSignature.ExtensionMember _ -> Some "extension-method"
      | ApiSignature.UnionCase _ -> None
      | ApiSignature.ComputationExpressionBuilder _ -> Some "class"

    let generate (api: Api) =
      let ns =
        let ns = ApiName.toName api.Name
        match api.Signature with
        | ApiSignature.Constructor _ -> ns.Tail // skip "new"
        | _ -> ns
      kindPart api ns
      |> Option.map (fun kind ->
        let sb = StringBuilder()
        sb.Append(namePart api ns).Append(genericParamsPart ns).Append("-").Append(kind).Append("-[fsharp]") |> ignore
        string sb |> toLower |> urlEncode
      )

  module internal Msdn =
    let isGeneric api = api.Name |> ApiName.toName |> List.exists (fun n -> List.isEmpty n.GenericParameters = false)
    let canGenerate (api: Api) =
      match api.Signature with
      | ApiSignature.ActivePatten _
      | ApiSignature.ModuleValue _
      | ApiSignature.ModuleFunction _
      | ApiSignature.Constructor _
      | ApiSignature.ModuleDefinition _
      | ApiSignature.FullTypeDefinition _
      | ApiSignature.TypeAbbreviation _
      | ApiSignature.TypeExtension _
      | ApiSignature.ExtensionMember _
      | ApiSignature.ComputationExpressionBuilder _
      | ApiSignature.UnionCase _ -> false

      | ApiSignature.InstanceMember _
      | ApiSignature.StaticMember _ -> true

    let generate (api: Api) =
      if isGeneric api then
        None
      elif canGenerate api = false then
        None
      else
        (ApiName.toName api.Name |> Seq.rev |> Seq.map urlName |> String.concat ".") + ".aspx"
        |> toLower
        |> Some

  module internal DotNetApiBrowser =
    open SpecialTypes
    open SpecialTypes.LowType.Patterns
    open System.Collections.Generic
    open System.Text

    type VariableMemory = Dictionary<string, string>

    let variableMemory (api: Api) (name: Name) =
      let variableMemory = VariableMemory()
      let memory (prefix: string) (n: NameItem) =
        n.GenericParameters
        |> List.iteri (fun variableId p ->
          let variable = p.Name
          if variableMemory.ContainsKey(variable) = false then
            variableMemory.[variable] <- prefix + string variableId
        )
        
      match api.Signature with
      | ApiSignature.FullTypeDefinition _ ->
        name |> List.iter (memory "_")
      | _ ->
        name.Head |> memory "__"
        name.Tail |> List.iter (memory "_")
      variableMemory

    let printName (modifiedString: string) (ctx: StringJoinContext) (mapping: string -> string) (sb: StringBuilder) (name: NameItem) (wroteGeneric: bool) : bool =
      sb.Append(ctx.Print) |> ignore

      if not wroteGeneric && not name.GenericParameters.IsEmpty then
        sb.Append(urlName name |> mapping).Append(modifiedString).Append(string name.GenericParameters.Length) |> ignore
        true
      else
        sb.Append(urlName name |> mapping) |> ignore
        wroteGeneric

    let printUrlPart (api: ApiSignature) (name: Name) (sb: StringBuilder) : StringBuilder =
      let ctx = StringJoinContext.Create(".")
      let printName' name = List.foldBack (printName "-" ctx toLower sb) name false |> ignore
      match api with
      | ApiSignature.FullTypeDefinition _ ->
        printName' name
      | ApiSignature.Constructor _ ->
        printName' name.Tail
        sb.Append(ctx.Print).Append("-ctor") |> ignore
      | _ ->
        printName' name.Tail
        sb.Append(ctx.Print).Append(urlName name.Head |> toLower) |> ignore
      sb

    let rec parameterElement (api: Api) (variableMemory: VariableMemory) (t: LowType) (sb: StringBuilder) : StringBuilder =
      match t with
      | Unit -> sb
      | Identifier (ConcreteType { Name = name }, _) ->
        let ns = name |> List.rev
        sb.AppendJoin("_", ns, (fun n sb -> sb.Append(urlName n)))
      | Array (_, elem, _) -> sb.Append(parameterElement api variableMemory elem).Append("__")
      | ByRef (_, arg, _) -> sb.Append(parameterElement api variableMemory arg).Append("_")
      | Generic (Identifier (ConcreteType { Name = { Name = SymbolName "nativeptr" } :: { Name = SymbolName "Core" } :: { Name = SymbolName "FSharp" } :: { Name = SymbolName "Microsoft" } :: [] }, _), args, _) ->
        sb.AppendJoin("_", args, (parameterElement api variableMemory))
            .Append("_")
      | Generic (id, args, _) ->
        sb.Append(parameterElement api variableMemory id) |> ignore
        
        sb.Append("_") |> ignore
        sb.AppendJoin("_", args, (parameterElement api variableMemory))
            .Append("_")
      | Variable (_, v, _) -> sb.Append(variableMemory.[v.Name])
      | Delegate (d, _, _) -> sb.Append(parameterElement api variableMemory d)
      | AbbreviationRoot root -> sb.Append(parameterElement api variableMemory root)
      | _ -> sb

    let hasParameter (member': Member) =
      match member'.Parameters with
      | [] | [ [ { Type = Unit } ] ] -> false
      | _ -> true

    let printHashPart (api: Api) (name: Name) (member': Member) (sb: StringBuilder) : StringBuilder =
      let ctx = StringJoinContext.Create("_")
      let printName' name = List.foldBack (printName "_" ctx id sb) name false |> ignore
      match api.Signature with
      | ApiSignature.FullTypeDefinition _ ->
        printName' name
      | ApiSignature.Constructor _ ->
        printName' name.Tail
        sb.Append(ctx.Print).Append("_ctor") |> ignore
      | _ ->
        printName' name.Tail
        printName "__" ctx id sb name.Head false |> ignore

      if hasParameter member' then
        let variableMemory = variableMemory api name
        let paramsCtx = StringJoinContext.Create("_")
        sb.Append("_") |> ignore
        for ps in member'.Parameters do
          for p in ps do
            sb.Append(paramsCtx.Print).Append(parameterElement api variableMemory p.Type) |> ignore
        sb.Append("_") |> ignore
      sb

    let generate (view: string) (api: Api) =
      let name = ApiName.toName api.Name
      match api.Signature with
      | ApiSignature.ActivePatten _
      | ApiSignature.ModuleValue _
      | ApiSignature.ModuleFunction _
      | ApiSignature.ModuleDefinition _            
      | ApiSignature.TypeAbbreviation _
      | ApiSignature.TypeExtension _
      | ApiSignature.ComputationExpressionBuilder _
      | ApiSignature.UnionCase _ -> None

      | ApiSignature.FullTypeDefinition _ ->
        let sb = StringBuilder().Append(printUrlPart api.Signature name).Append("?view=").Append(view)
        Some (string sb)

      | ApiSignature.ExtensionMember (member' : Member)
      | ApiSignature.Constructor (_ , (member' : Member))
      | ApiSignature.InstanceMember (_ , (member' : Member))
      | ApiSignature.StaticMember (_ ,(member' : Member)) ->
        let sb = StringBuilder().Append(printUrlPart api.Signature name).Append("?view=").Append(view).Append("#").Append(printHashPart api name member')
        Some (string sb)

  module internal FParsec =
    open System.Text
    
    let moduleName (api: Api) =
      let names = api.Name |> ApiName.toName |> List.rev
      match names with
      | { Name = SymbolName ("FParsec") } :: { Name = SymbolName moduleName } :: _ ->
        if moduleName = "Internals" || moduleName = "Emit" then
          None
        else
          Some moduleName
      | _ -> None

    let urlPart (moduleName: string) (sb: StringBuilder) =
      sb.Append(toLower moduleName).Append(".html")

    let opReplaceTable =
      dict [
        ' ', ""
        '(', ""
        ')', ""
        '.', ".."
      ]

    let opReplace (ns: NameItem) (sb: StringBuilder) =
      match ns.Name with
      | OperatorName (name, _) ->
        for ch in name do
          match opReplaceTable.TryGetValue(ch) with
          | true, replaced -> sb.Append(replaced) |> ignore
          | false, _ -> sb.Append(":").Append(int ch).Append(":") |> ignore
        sb
      | SymbolName name -> sb.Append(name)
      | WithCompiledName (name, _) -> sb.Append(name)

    let membersPart (ns: Name) (sb: StringBuilder) =
      sb.Append("members.").Append(ns.Head |> opReplace)

    let staticMembersPart (ns: Name) (sb: StringBuilder) =
      sb.Append("members.").Append(urlName ns.[1]).Append("..").Append(urlName ns.[0])

    let generate (api: Api) =
      moduleName api
      |> Option.bind (fun moduleName ->
        match api.Signature with
        | ApiSignature.ActivePatten _    
        | ApiSignature.TypeExtension _
        | ApiSignature.UnionCase _ 
        | ApiSignature.ExtensionMember _
        | ApiSignature.Constructor _ -> None
      
        | ApiSignature.ModuleDefinition _ ->
          let sb = StringBuilder().Append(urlPart moduleName)
          Some (string sb)  

        | ApiSignature.ComputationExpressionBuilder _
        | ApiSignature.ModuleFunction _   
        | ApiSignature.ModuleValue _
        | ApiSignature.TypeAbbreviation _
        | ApiSignature.FullTypeDefinition _ 
        | ApiSignature.InstanceMember _ ->
          let sb = StringBuilder().Append(urlPart moduleName).Append("#").Append(api.Name |> ApiName.toName |> membersPart)
          Some (string sb)

        | ApiSignature.StaticMember (_, mem) ->
          match mem.Kind with
          | MemberKind.Method ->
            let sb = StringBuilder().Append(urlPart moduleName).Append("#").Append(api.Name |> ApiName.toName |> staticMembersPart)
            Some (string sb)
          | _ -> None
      )

  let fsharp baseUrl: LinkGenerator = fun api -> FSharp.generate api |> Option.map (fun apiUrl -> baseUrl + apiUrl)
  let msdn baseUrl: LinkGenerator = fun api -> Msdn.generate api |> Option.map (fun apiUrl -> baseUrl + apiUrl)
  let dotNetApiBrowser baseUrl (view: string) : LinkGenerator = fun api -> DotNetApiBrowser.generate view api |> Option.map (fun apiUrl -> baseUrl + apiUrl)
  let fparsec baseUrl : LinkGenerator = fun api -> FParsec.generate api |> Option.map(fun apiUrl -> baseUrl + apiUrl)