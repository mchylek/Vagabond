﻿namespace System
open System.Reflection

[<assembly: AssemblyVersionAttribute("0.5.2")>]
[<assembly: AssemblyFileVersionAttribute("0.5.2")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "0.5.2"
