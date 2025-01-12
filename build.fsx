// --------------------------------------------------------------------------------------
// FAKE build script 
// --------------------------------------------------------------------------------------

#r "paket: groupref build //"
#load "./.fake/build.fsx/intellisense.fsx"

open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Tools
open Fake.Api
open System

// --------------------------------------------------------------------------------------
// Information about the project to be used at NuGet and in AssemblyInfo files
// --------------------------------------------------------------------------------------

Environment.CurrentDirectory <- __SOURCE_DIRECTORY__

let gitOwner = "mbraceproject"
let gitName = "Vagabond"
let gitHome = "https://github.com/" + gitOwner

let artifactsDir = __SOURCE_DIRECTORY__ @@ "artifacts"

let configuration = Environment.environVarOrDefault "Configuration" "Release"
let release = ReleaseNotes.load "RELEASE_NOTES.md"

//
//// --------------------------------------------------------------------------------------
//// The rest of the code is standard F# build script 
//// --------------------------------------------------------------------------------------

// --------------------------------------------------------------------------------------
// Clean build results & restore NuGet packages

Target.create "Clean" (fun _ ->
    Shell.cleanDirs [ artifactsDir ]
)

//
//// --------------------------------------------------------------------------------------
//// Build library & test project

Target.create "Build" (fun _ ->
    DotNet.build (fun c ->
        { c with
            Configuration = DotNet.BuildConfiguration.fromString configuration

            MSBuildParams =
            { c.MSBuildParams with
                Properties = [("Version", release.NugetVersion)] }

        }) __SOURCE_DIRECTORY__
    )


// --------------------------------------------------------------------------------------
// Run the unit tests using test runner & kill test runner when complete

Target.create "RunTests" (fun _ ->
    DotNet.test (fun c ->
        { c with
            Configuration = DotNet.BuildConfiguration.fromString configuration
            NoBuild = true
            Blame = true

        }) __SOURCE_DIRECTORY__
)

//
//// --------------------------------------------------------------------------------------
//// Build a NuGet package

Target.create "NuGet.Pack" (fun _ ->
    let releaseNotes = String.toLines release.Notes |> System.Net.WebUtility.HtmlEncode
    DotNet.pack (fun pack ->
        { pack with
            OutputPath = Some artifactsDir
            Configuration = DotNet.BuildConfiguration.Release
            MSBuildParams =
                { pack.MSBuildParams with
                    Properties = 
                        [("Version", release.NugetVersion)
                         ("PackageReleaseNotes", releaseNotes)] }
        }) __SOURCE_DIRECTORY__
)

Target.create "NuGet.ValidateSourceLink" (fun _ ->
    for nupkg in !! (artifactsDir @@ "*.nupkg") do
        let p = DotNet.exec id "sourcelink" (sprintf "test %s" nupkg)
        if not p.OK then failwithf "failed to validate sourcelink for %s" nupkg
)

Target.create "NuGet.Push" (fun _ ->
    for artifact in !! (artifactsDir + "/*nupkg") do
        let source = "https://api.nuget.org/v3/index.json"
        let key = Environment.GetEnvironmentVariable "NUGET_KEY"
        let result = DotNet.exec id "nuget" (sprintf "push -s %s -k %s %s" source key artifact)
        if not result.OK then failwith "failed to push packages"
)

// Doc generation

Target.create "GenerateDocs" (fun _ ->
    let result = DotNet.exec (fun o -> { o with WorkingDirectory = "docs" }) "fsi" "--define:RELEASE tools/generate.fsx"
    if not result.OK then failwith "failed to generate docs"
)

Target.create "ReleaseDocs" (fun _ ->
    let tempDocsDir = "temp/gh-pages"
    let outputDocsDir = "docs/output"

    Directory.ensure outputDocsDir

    Shell.cleanDir tempDocsDir
    Git.Repository.cloneSingleBranch "" (gitHome + "/" + gitName + ".git") "gh-pages" tempDocsDir
    Shell.copyRecursive outputDocsDir tempDocsDir true |> Trace.tracefn "%A"
    Git.Staging.stageAll tempDocsDir
    Git.Commit.exec tempDocsDir (sprintf "Update generated documentation for version %s" release.NugetVersion)
    Git.Branches.push tempDocsDir
)

// Github Releases

Target.create "ReleaseGitHub" (fun _ ->
    let remote =
        Git.CommandHelper.getGitResult "" "remote -v"
        |> Seq.filter (fun (s: string) -> s.EndsWith("(push)"))
        |> Seq.tryFind (fun (s: string) -> s.Contains(gitOwner + "/" + gitName))
        |> function None -> gitHome + "/" + gitName | Some (s: string) -> s.Split().[0]

    //StageAll ""
    Git.Commit.exec "" (sprintf "Bump version to %s" release.NugetVersion)
    Git.Branches.pushBranch "" remote (Git.Information.getBranchName "")

    Git.Branches.tag "" release.NugetVersion
    Git.Branches.pushTag "" remote release.NugetVersion

    let client =
        match Environment.GetEnvironmentVariable "GITHUB_TOKEN" with
        | null -> 
            let user =
                match Environment.environVarOrDefault "github-user" "" with
                | s when not (String.IsNullOrWhiteSpace s) -> s
                | _ -> UserInput.getUserInput "Username: "
            let pw =
                match Environment.environVarOrDefault "github-pw" "" with
                | s when not (String.IsNullOrWhiteSpace s) -> s
                | _ -> UserInput.getUserInput "Password: "

            GitHub.createClient user pw
        | token -> GitHub.createClientWithToken token

    // release on github
    client
    |> GitHub.draftNewRelease gitOwner gitName release.NugetVersion (release.SemVer.PreRelease <> None) release.Notes
    |> GitHub.publishDraft
    |> Async.RunSynchronously
)

// --------------------------------------------------------------------------------------
// Run all targets by default. Invoke 'build <Target>' to override

Target.create "Default" ignore
Target.create "Bundle" ignore
Target.create "Release" ignore

"Clean"
  ==> "Build"
  ==> "RunTests"
  ==> "Default"

"Default"
  ==> "NuGet.Pack"
  // disabling due to AssemblyInfo.cs glitch with dotnet SDK 3.1
  //==> "NuGet.ValidateSourceLink"
  ==> "GenerateDocs"
  ==> "Bundle"

"Bundle"
  ==> "ReleaseDocs"
  ==> "NuGet.Push"
  ==> "ReleaseGithub"
  ==> "Release"

Target.runOrDefault "Bundle"
